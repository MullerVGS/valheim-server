using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace ValheimMetrics.Map
{
    public struct TileEntry
    {
        public ulong Signature;
        public bool Drawn;
    }

    // Assinatura de cada tile ja desenhado (ou apagado), no disco: o render pula o que nao mudou e,
    // se for interrompido (restart), recomeca de onde parou.
    public sealed class TileBook
    {
        const int Version = 1;
        public readonly Dictionary<TileId, TileEntry> Entries = new Dictionary<TileId, TileEntry>();

        public static TileBook Load(string path)
        {
            var book = new TileBook();
            if (!File.Exists(path))
                return book;
            try
            {
                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    if (r.ReadInt32() != Version)
                        return book;
                    int n = r.ReadInt32();
                    for (int k = 0; k < n; k++)
                    {
                        var id = new TileId(r.ReadByte(), r.ReadInt32(), r.ReadInt32());
                        book.Entries[id] = new TileEntry { Signature = r.ReadUInt64(), Drawn = r.ReadBoolean() };
                    }
                }
            }
            catch (EndOfStreamException)
            {
                book.Entries.Clear();
            }
            return book;
        }

        public void Save(string path)
        {
            var tmp = path + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp)))
            {
                w.Write(Version);
                w.Write(Entries.Count);
                foreach (var kv in Entries)
                {
                    w.Write((byte)kv.Key.Zoom);
                    w.Write(kv.Key.X);
                    w.Write(kv.Key.Y);
                    w.Write(kv.Value.Signature);
                    w.Write(kv.Value.Drawn);
                }
            }
            if (File.Exists(path))
                File.Delete(path);
            File.Move(tmp, path);
        }
    }

    // Quando e quanto o desenho pode gastar. Tudo com padrao: so VALHEIM_MAP_DIR liga o desenho.
    public sealed class DrawSettings
    {
        public const string AtVariable = "VALHEIM_MAP_DRAW_AT";
        public const string MaxZoomVariable = "VALHEIM_MAP_MAX_ZOOM";
        public const string DutyVariable = "VALHEIM_MAP_DRAW_DUTY";
        public const string ScanBudgetVariable = "VALHEIM_MAP_SCAN_BUDGET_MS";

        // Hora local do container; fora do horario de jogo e longe do restart diario.
        public TimeSpan At = new TimeSpan(4, 30, 0);
        public int MaxZoom = TilePainter.MaxZoom;
        public double Duty = 0.5;
        public double ScanBudgetMs = 1;

        public static DrawSettings Parse(Func<string, string> env)
        {
            var s = new DrawSettings();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var at = env(AtVariable)?.Trim();
            if (!string.IsNullOrEmpty(at) && TimeSpan.TryParseExact(at, @"hh\:mm", inv, out var t))
                s.At = t;
            if (int.TryParse(env(MaxZoomVariable)?.Trim(), System.Globalization.NumberStyles.Integer, inv, out var z))
                s.MaxZoom = Math.Max(MapProjection.NativeZoom, Math.Min(18, z));
            if (double.TryParse(env(DutyVariable)?.Trim(), System.Globalization.NumberStyles.Float, inv, out var d))
                s.Duty = Math.Max(0.05, Math.Min(1, d));
            if (double.TryParse(env(ScanBudgetVariable)?.Trim(), System.Globalization.NumberStyles.Float, inv, out var b))
                s.ScanBudgetMs = Math.Max(0.1, Math.Min(20, b));
            return s;
        }

        // Proxima vez em que o relogio passa por At, estritamente depois de now.
        public DateTime NextAfter(DateTime now)
        {
            var next = now.Date + At;
            return next > now ? next : next.AddDays(1);
        }
    }

    public enum RenderPhase
    {
        Plan,
        Signature,
        Terrain,
        Pieces,
        Encode,
        Write,
        Throttle,
    }

    // Numeros do render para o /metrics. Escritos pela thread do render, lidos pela principal:
    // campos de 64 bits alinhados, leitura rasgada nao acontece em x64.
    public sealed class RenderStats
    {
        public readonly double[] PhaseSeconds = new double[Enum.GetValues(typeof(RenderPhase)).Length];
        public long TilesDrawn;
        public long TilesDeleted;
        public long TilesSkipped;
        public long Runs;
        public long Errors;
        public long GcCollections;
        public volatile bool Active;
        public int Candidates;
        public int Pending;
        public double LastStartUnix;
        public double LastSeconds;
        public double LastTileSecondsMax;
    }

    // Um passe completo pelos tiles: redesenha so o que a assinatura diz que mudou. Terreno e pecas
    // no mesmo desenho. Com duty < 1 dorme depois de cada tile na proporcao do trabalho: 0,5 = no
    // maximo metade de um nucleo, e o passe demora o dobro.
    public sealed class RenderJob
    {
        const int SaveEvery = 100;

        readonly bool[] _explored;
        readonly ITerrain _terrain;
        readonly PieceLayer _pieces;
        readonly string _dir;
        readonly int _maxZoom;
        readonly double _duty;
        readonly RenderStats _stats;
        readonly Action<int> _sleep;

        public RenderJob(bool[] explored, ITerrain terrain, PieceLayer pieces, string dir, int maxZoom, double duty,
            RenderStats stats, Action<int> sleep = null)
        {
            _explored = explored;
            _terrain = terrain;
            _pieces = pieces;
            _dir = dir;
            _maxZoom = maxZoom;
            _duty = Math.Max(0.05, Math.Min(1, duty));
            _stats = stats;
            _sleep = sleep ?? Thread.Sleep;
        }

        public string TilesDir => Path.Combine(_dir, "tiles");
        public string BookPath => Path.Combine(_dir, "tiles.book");

        public void Run()
        {
            var total = Stopwatch.StartNew();
            var phase = Stopwatch.StartNew();
            int gc0 = GcCount();
            _stats.Active = true;
            _stats.LastStartUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            _stats.LastTileSecondsMax = 0;
            try
            {
                var book = TileBook.Load(BookPath);
                var tiles = new HashSet<TileId>(TilePainter.TilesTouching(TilePainter.Changed(null, _explored), _maxZoom));
                foreach (var id in book.Entries.Keys)
                    tiles.Add(id);
                var order = new List<TileId>(tiles);
                // Do mais proximo para o mais longe: as bases aparecem primeiro no zoom que importa.
                order.Sort((a, b) => a.Zoom != b.Zoom ? b.Zoom.CompareTo(a.Zoom) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
                _stats.Candidates = order.Count;
                _stats.Pending = order.Count;
                Add(RenderPhase.Plan, phase);

                var canvas = new TileCanvas();
                var raw = new byte[MapProjection.TileSize * (MapProjection.TileSize * 4 + 1)];
                int sinceSave = 0;
                foreach (var tile in order)
                {
                    var work = Stopwatch.StartNew();
                    phase.Restart();
                    ulong sig = TilePainter.Signature(tile, _explored, _pieces, canvas.Scratch);
                    var path = tile.Path(TilesDir);
                    bool exists = File.Exists(path);
                    Add(RenderPhase.Signature, phase);
                    if (book.Entries.TryGetValue(tile, out var known) && known.Signature == sig && known.Drawn == exists)
                    {
                        Interlocked.Increment(ref _stats.TilesSkipped);
                        _stats.Pending--;
                        continue;
                    }

                    bool drawn = TilePainter.Paint(tile, _explored, _terrain, null, canvas);
                    Add(RenderPhase.Terrain, phase);
                    if (drawn)
                    {
                        _pieces?.Draw(tile, canvas.Rgba, canvas.Scratch);
                        Add(RenderPhase.Pieces, phase);
                        var png = Png.Encode(MapProjection.TileSize, MapProjection.TileSize, canvas.Rgba, raw);
                        Add(RenderPhase.Encode, phase);
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        var tmp = path + ".tmp";
                        File.WriteAllBytes(tmp, png);
                        if (exists)
                            File.Delete(path);
                        File.Move(tmp, path);
                        Interlocked.Increment(ref _stats.TilesDrawn);
                    }
                    else
                    {
                        if (exists)
                            File.Delete(path);
                        Interlocked.Increment(ref _stats.TilesDeleted);
                    }
                    book.Entries[tile] = new TileEntry { Signature = sig, Drawn = drawn };
                    if (++sinceSave >= SaveEvery)
                    {
                        sinceSave = 0;
                        book.Save(BookPath);
                    }
                    Add(RenderPhase.Write, phase);
                    _stats.Pending--;

                    double spent = work.Elapsed.TotalSeconds;
                    if (spent > _stats.LastTileSecondsMax)
                        _stats.LastTileSecondsMax = spent;
                    if (_duty < 1)
                    {
                        int ms = (int)Math.Min(10000, spent * (1 / _duty - 1) * 1000);
                        if (ms > 0)
                            _sleep(ms);
                        Add(RenderPhase.Throttle, phase);
                    }
                }
                book.Save(BookPath);
                Interlocked.Increment(ref _stats.Runs);
            }
            catch
            {
                Interlocked.Increment(ref _stats.Errors);
                throw;
            }
            finally
            {
                _stats.LastSeconds = total.Elapsed.TotalSeconds;
                Interlocked.Add(ref _stats.GcCollections, GcCount() - gc0);
                _stats.Active = false;
            }
        }

        void Add(RenderPhase p, Stopwatch sw)
        {
            _stats.PhaseSeconds[(int)p] += sw.Elapsed.TotalSeconds;
            sw.Restart();
        }

        // Toda coleta passa pela geracao 0. Conta tambem as que o jogo causou no mesmo intervalo.
        static int GcCount() => GC.CollectionCount(0);
    }
}
