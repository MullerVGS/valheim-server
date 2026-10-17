using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using ValheimMetrics.Recording;
using Xunit;

public class RecordFormatTests
{
    static RecordReader Reader(RecordBuffer b) => new RecordReader(new MemoryStream(b.Data, 0, b.Length));

    [Fact]
    public void RecordsComeBackWithEveryField()
    {
        var b = new RecordBuffer(16);
        b.Header();
        b.Begin(RecordKind.Peer, 1234);
        b.U8(7);
        b.U16(65000);
        b.U32(4000000000);
        b.I32(-5);
        b.I64(-1234567890123456789);
        b.F32(-1.5f);
        b.F64(123456.789);
        b.Str("Ásgeir Ødegård");
        b.Blob(new byte[] { 1, 2, 3 }, 2);
        b.End();

        var r = Reader(b);
        Assert.Equal(RecordFormat.Version, r.Version);
        Assert.True(r.Next(out var kind, out var ms, out var payload));
        Assert.Equal(RecordKind.Peer, kind);
        Assert.Equal(1234u, ms);
        var p = new Payload(payload);
        Assert.Equal(7, p.U8());
        Assert.Equal(65000, p.U16());
        Assert.Equal(4000000000u, p.U32());
        Assert.Equal(-5, p.I32());
        Assert.Equal(-1234567890123456789, p.I64());
        Assert.Equal(-1.5f, p.F32());
        Assert.Equal(123456.789, p.F64());
        Assert.Equal("Ásgeir Ødegård", p.Str());
        Assert.Equal(new byte[] { 1, 2 }, p.Blob());
        Assert.Equal(0, p.Remaining);
        Assert.False(r.Next(out _, out _, out _));
    }

    [Fact]
    public void UnknownKindsAreSkippedByLength()
    {
        var b = new RecordBuffer(64);
        b.Header();
        b.Begin((RecordKind)200, 1);
        b.Str("future");
        b.End();
        b.Begin(RecordKind.Marker, 2);
        b.Str("stop");
        b.End();

        var r = Reader(b);
        Assert.True(r.Next(out var first, out _, out _));
        Assert.Equal((RecordKind)200, first);
        Assert.True(r.Next(out var kind, out _, out var payload));
        Assert.Equal(RecordKind.Marker, kind);
        Assert.Equal("stop", new Payload(payload).Str());
    }

    [Fact]
    public void RecordCutShortEndsTheStream()
    {
        var b = new RecordBuffer(64);
        b.Header();
        b.Begin(RecordKind.Marker, 1);
        b.Str("whole");
        b.End();
        b.Begin(RecordKind.Marker, 2);
        b.Str("cut by a crash");
        b.End();

        var r = new RecordReader(new MemoryStream(b.Data, 0, b.Length - 3));
        Assert.True(r.Next(out _, out _, out _));
        Assert.False(r.Next(out _, out _, out _));
    }

    [Fact]
    public void AbortDropsAHalfWrittenRecord()
    {
        var b = new RecordBuffer(64);
        b.Header();
        int at = b.Length;
        b.Begin(RecordKind.Zdo, 1);
        b.I64(1);
        b.Abort(at);
        b.Begin(RecordKind.Marker, 2);
        b.Str("next");
        b.End();

        var r = Reader(b);
        Assert.True(r.Next(out var kind, out _, out _));
        Assert.Equal(RecordKind.Marker, kind);
    }

    [Fact]
    public void BufferGrowsPastItsCapacity()
    {
        var b = new RecordBuffer(4);
        b.Header();
        b.Begin(RecordKind.Snapshot, 0);
        b.Blob(new byte[100000], 100000);
        b.End();

        Assert.True(Reader(b).Next(out _, out _, out var payload));
        Assert.Equal(100004, payload.Length);
    }

    [Fact]
    public void SwapHandsTheFilledArrayOver()
    {
        var b = new RecordBuffer(64);
        b.Header();
        var fresh = new byte[64];
        var full = b.Swap(fresh);

        Assert.Equal((byte)'V', full[0]);
        Assert.Equal(0, b.Length);
        Assert.Same(fresh, b.Data);
    }

    [Fact]
    public void NotARecordingIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => new RecordReader(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6 })));
    }
}

public class RecordFileTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "recorder-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    [Fact]
    public void ChunksArriveCompressedAndInOrder()
    {
        var path = Path.Combine(_dir, "a.vrec.gz");
        var file = new RecordFile(path, 1 << 20);
        var b = new RecordBuffer(RecordFile.ChunkBytes);
        b.Header();
        for (uint i = 0; i < 3; i++)
        {
            b.Begin(RecordKind.Marker, i);
            b.Str("m" + i);
            b.End();
            int length = b.Length;
            Assert.True(file.Submit(b.Swap(file.Rent()), length));
        }
        file.Close();
        Assert.True(file.Wait(5000));
        Assert.True(file.Finished);
        Assert.Null(file.Error);
        Assert.True(file.CompressedBytes > 0);

        using var input = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        var r = new RecordReader(input);
        var texts = new List<string>();
        while (r.Next(out _, out _, out var payload))
            texts.Add(new Payload(payload).Str());
        Assert.Equal(new[] { "m0", "m1", "m2" }, texts);
    }

    [Fact]
    public void FullQueueDropsInsteadOfWaiting()
    {
        var file = new RecordFile(Path.Combine(_dir, "b.vrec.gz"), 10);

        Assert.False(file.Submit(new byte[100], 100));
        Assert.Equal(100, file.DroppedBytes);
        Assert.Equal(0, file.WrittenBytes);
        file.Close();
        Assert.True(file.Wait(5000));
    }

    [Fact]
    public void ExistingFileIsNeverOverwritten()
    {
        var path = Path.Combine(_dir, "c.vrec.gz");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "old");

        Assert.Throws<IOException>(() => new RecordFile(path, 1 << 20));
        Assert.Equal("old", File.ReadAllText(path));
    }
}

public class SessionRulesTests
{
    const int Boss = 1;
    const int OtherBoss = 2;
    const int Admin = 10;

    static RecorderSettings Settings(bool auto = true) =>
        new RecorderSettings { AutoBoss = auto, Radius = 100, TailSeconds = 45, MaxSeconds = 3600, MaxBytes = 1000 };

    readonly SessionRules<int> _rules = new SessionRules<int>(Settings());

    [Fact]
    public void BossStartsTheSessionOnce()
    {
        Assert.True(_rules.Boss(Boss, 0, 0, 0, 10));
        Assert.False(_rules.Boss(Boss, 1, 0, 0, 11));
        Assert.True(_rules.Active);
        Assert.Equal(10, _rules.StartedAt);
    }

    [Fact]
    public void AreaIsASphereAroundEachAnchor()
    {
        _rules.Boss(Boss, 0, 30, 0, 0);
        _rules.Boss(OtherBoss, 1000, 30, 0, 0);

        Assert.True(_rules.Inside(99, 30, 0));
        Assert.False(_rules.Inside(101, 30, 0));
        Assert.True(_rules.Inside(1000, 30, 99));
        // Dungeon interior right above the arena.
        Assert.False(_rules.Inside(0, 5000, 0));
    }

    [Fact]
    public void AreaFollowsTheBoss()
    {
        _rules.Boss(Boss, 0, 0, 0, 0);
        _rules.Moved(Boss, 500, 0, 0);

        Assert.True(_rules.Inside(450, 0, 0));
        Assert.False(_rules.Inside(0, 0, 0));
    }

    [Fact]
    public void BossGoneEndsAfterTheTail()
    {
        _rules.Boss(Boss, 0, 0, 0, 0);
        _rules.Seen(Boss, 0, 0, 0, 100);

        Assert.Equal(StopReason.None, _rules.Check(145, 0));
        Assert.Equal(StopReason.BossGone, _rules.Check(146, 0));
    }

    [Fact]
    public void BossWithNobodyAroundDoesNotKeepItAlive()
    {
        _rules.Boss(Boss, 0, 0, 0, 0);
        for (int t = 1; t < 100; t++)
            _rules.Moved(Boss, t, 0, 0);

        Assert.Equal(StopReason.BossGone, _rules.Check(100, 0));
    }

    [Fact]
    public void SecondBossKeepsItGoing()
    {
        _rules.Boss(Boss, 0, 0, 0, 0);
        _rules.Boss(OtherBoss, 0, 0, 0, 40);

        Assert.Equal(StopReason.None, _rules.Check(80, 0));
        Assert.Equal(StopReason.BossGone, _rules.Check(86, 0));
    }

    [Fact]
    public void HoldRunsWithoutABoss()
    {
        Assert.True(_rules.Hold(Admin, 0, 0, 0, 0));
        _rules.Seen(Admin, 0, 0, 0, 1000);

        Assert.True(_rules.Held);
        Assert.Equal(StopReason.None, _rules.Check(1000, 0));
    }

    [Fact]
    public void HolderLeavingEndsIt()
    {
        _rules.Hold(Admin, 0, 0, 0, 0);

        Assert.Equal(StopReason.HolderGone, _rules.Check(46, 0));
    }

    [Fact]
    public void HoldDuringABossFightJoinsIt()
    {
        _rules.Boss(Boss, 0, 0, 0, 0);

        Assert.False(_rules.Hold(Admin, 500, 0, 0, 5));
        Assert.True(_rules.Inside(550, 0, 0));
    }

    [Fact]
    public void ReleaseKeepsRecordingWhileTheBossLives()
    {
        _rules.Boss(Boss, 0, 0, 0, 0);
        _rules.Hold(Admin, 500, 0, 0, 0);
        _rules.Release();

        Assert.False(_rules.Held);
        Assert.False(_rules.Inside(500, 0, 0));
        Assert.Equal(StopReason.None, _rules.Check(10, 0));
    }

    [Fact]
    public void ReleaseWithoutABossEndsIt()
    {
        _rules.Hold(Admin, 0, 0, 0, 0);
        _rules.Release();

        Assert.Equal(StopReason.Released, _rules.Check(1, 0));
    }

    [Fact]
    public void HeldSessionOutlivesTheBoss()
    {
        _rules.Boss(Boss, 0, 0, 0, 0);
        _rules.Hold(Admin, 0, 0, 0, 0);
        _rules.Seen(Admin, 0, 0, 0, 200);

        Assert.Equal(StopReason.None, _rules.Check(200, 0));
    }

    [Fact]
    public void LimitsStopEverything()
    {
        _rules.Hold(Admin, 0, 0, 0, 0);
        _rules.Seen(Admin, 0, 0, 0, 3601);

        Assert.Equal(StopReason.MaxDuration, _rules.Check(3601, 0));
        Assert.Equal(StopReason.MaxBytes, _rules.Check(10, 1001));
    }

    [Fact]
    public void ManualModeIgnoresBossesUntilSomeoneHolds()
    {
        var rules = new SessionRules<int>(Settings(auto: false));

        Assert.False(rules.Boss(Boss, 0, 0, 0, 0));
        Assert.False(rules.Active);

        rules.Hold(Admin, 0, 0, 0, 0);
        rules.Boss(Boss, 300, 0, 0, 1);
        Assert.True(rules.Inside(350, 0, 0));
    }

    [Fact]
    public void StopClearsTheSession()
    {
        _rules.Boss(Boss, 0, 0, 0, 0);
        _rules.Hold(Admin, 0, 0, 0, 0);
        _rules.Stop();

        Assert.False(_rules.Active);
        Assert.False(_rules.Held);
        Assert.Equal(0, _rules.AnchorCount);
        Assert.Equal(StopReason.None, _rules.Check(1000, 0));
        Assert.True(_rules.Boss(Boss, 0, 0, 0, 2000));
    }

    [Theory]
    [InlineData("Fader", "Fader")]
    [InlineData("Luta final: Rei Congelado!", "Luta-final-Rei-Congelado")]
    [InlineData("Ásgeir Ødegård", "sgeir-deg-rd")]
    [InlineData("../../etc", "etc")]
    [InlineData("", "scene")]
    [InlineData(null, "scene")]
    public void SlugIsSafeForFileNames(string label, string slug)
    {
        Assert.Equal(slug, SessionRules<int>.Slug(label));
    }

    [Theory]
    [InlineData("1", null, true, 150f)]
    [InlineData(" manual ", "80", false, 80f)]
    [InlineData("1", "5", true, 150f)]
    [InlineData("1", "abc", true, 150f)]
    public void SettingsParse(string raw, string radius, bool auto, float expected)
    {
        var s = RecorderSettings.Parse(raw, radius);
        Assert.Equal(auto, s.AutoBoss);
        Assert.Equal(expected, s.Radius);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("yes")]
    public void SettingsOff(string raw)
    {
        Assert.Null(RecorderSettings.Parse(raw, null));
    }
}
