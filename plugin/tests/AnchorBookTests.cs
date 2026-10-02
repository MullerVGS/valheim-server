using ValheimMetrics.ChunkLoader;
using Xunit;

public class AnchorBookTests
{
    readonly AnchorBook<int> _book = new AnchorBook<int>();

    [Theory]
    [InlineData("chunkloader")]
    [InlineData("Chunk Loader")]
    [InlineData("  [chunk-loader]  ")]
    [InlineData("<b>CHUNKLOADER</b>")]
    [InlineData("<color=#ff0000>chunk</color> loader")]
    public void MarkerIgnoresCaseSpacingAndTags(string text) => Assert.True(AnchorBook<int>.IsMarker(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("chunk")]
    [InlineData("chunkloader 2")]
    [InlineData("meu chunkloader")]
    [InlineData("chunkloaders")]
    public void AnythingElseIsNotAMarker(string text) => Assert.False(AnchorBook<int>.IsMarker(text));

    [Fact]
    public void NewMarkerIsStampedNow()
    {
        Assert.Equal(100, _book.Observe(1, "chunkloader", 0, 100));
        Assert.True(_book.TryPick(out var id));
        Assert.Equal(1, id);
    }

    [Fact]
    public void StampSavedOnTheSignSurvivesARestart()
    {
        Assert.Equal(40, _book.Observe(1, "chunkloader", 40, 100));
    }

    [Fact]
    public void KnownStampWinsOverAClientThatHasNotSeenIt()
    {
        _book.Observe(1, "chunkloader", 0, 100);
        Assert.Equal(100, _book.Observe(1, "chunkloader", 0, 500));
    }

    [Fact]
    public void MostRecentSignWins()
    {
        _book.Observe(1, "chunkloader", 0, 100);
        _book.Observe(2, "chunkloader", 0, 200);
        _book.Observe(3, "chunkloader", 150, 300);

        Assert.True(_book.TryPick(out var id));
        Assert.Equal(2, id);
    }

    [Fact]
    public void RewritingTheTextDropsTheSign()
    {
        _book.Observe(1, "chunkloader", 0, 100);
        Assert.Equal(0, _book.Observe(1, "Madeira", 100, 200));
        Assert.False(_book.TryPick(out _));
    }

    [Fact]
    public void ForgottenSignHandsOverToTheOlderOne()
    {
        _book.Observe(1, "chunkloader", 0, 100);
        _book.Observe(2, "chunkloader", 0, 200);
        _book.Forget(2);

        Assert.True(_book.TryPick(out var id));
        Assert.Equal(1, id);
    }

    [Fact]
    public void ActiveSignGetsUnderlinedOnce()
    {
        Assert.Equal("<u>Chunk Loader</u>", AnchorBook<int>.Mark("Chunk Loader", true));
        Assert.Null(AnchorBook<int>.Mark("<u>Chunk Loader</u>", true));
        Assert.True(AnchorBook<int>.IsMarker("<u>Chunk Loader</u>"));
    }

    [Fact]
    public void InactiveSignLosesOnlyOurUnderline()
    {
        Assert.Equal("chunkloader", AnchorBook<int>.Mark("<u>chunkloader</u>", false));
        Assert.Equal("<b>chunkloader</b>", AnchorBook<int>.Mark("<u><b>chunkloader</b></u>", false));
        Assert.Null(AnchorBook<int>.Mark("chunkloader", false));
        Assert.Null(AnchorBook<int>.Mark("<u>x", false));
    }
}
