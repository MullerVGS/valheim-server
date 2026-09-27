using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ValheimMetrics.Map;
using Xunit;

public class MapTests
{
    const int Size = SharedMap.Size;

    static byte[] SharedMapBytes(int version, Action<bool[]> explore, params (string name, float x, float z, int type, string author)[] pins)
    {
        var explored = new bool[Size * Size];
        explore(explored);
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(version);
            w.Write(explored.Length);
            foreach (var e in explored)
                w.Write(e);
            w.Write(pins.Length);
            foreach (var p in pins)
            {
                w.Write(42L);
                w.Write(p.name);
                w.Write(p.x);
                w.Write(0f);
                w.Write(p.z);
                w.Write(p.type);
                w.Write(false);
                if (version >= 3)
                    w.Write(p.author);
            }
        }
        return ms.ToArray();
    }

    static byte[] Gzip(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress))
            gz.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    static int Pixel(double x, double z)
    {
        Assert.True(SharedMap.ToPixel(x, z, out var j, out var i));
        return i * Size + j;
    }

    [Fact]
    public void Le_o_que_a_mesa_grava()
    {
        var raw = SharedMapBytes(3, e => e[Pixel(100, -50)] = true, ("cobre", 100, -50, 3, "Steam_1"), ("$enemy_eikthyr", 7, 8, 9, "Steam_2"));

        var map = SharedMap.FromCompressed(Gzip(raw));

        Assert.Equal(1, map.Explored.Count(e => e));
        Assert.True(map.Explored[Pixel(100, -50)]);
        Assert.Equal(new[] { "cobre", "$enemy_eikthyr" }, map.Pins.Select(p => p.Name));
        Assert.Equal((100f, -50f, 3, "Steam_1"), (map.Pins[0].X, map.Pins[0].Z, map.Pins[0].Type, map.Pins[0].Author));
    }

    [Fact]
    public void Versao_2_nao_tem_autor()
    {
        var map = SharedMap.Parse(SharedMapBytes(2, e => { }, ("casa", 1, 2, 1, null)));

        Assert.Equal("", map.Pins.Single().Author);
    }

    [Fact]
    public void Tamanho_errado_e_recusado()
    {
        var raw = new byte[8];
        BitConverter.GetBytes(3).CopyTo(raw, 0);
        BitConverter.GetBytes(256 * 256).CopyTo(raw, 4);

        Assert.Throws<InvalidDataException>(() => SharedMap.Parse(raw));
    }

    [Fact]
    public void Uniao_soma_o_explorado_e_nao_repete_pin()
    {
        var a = SharedMap.Parse(SharedMapBytes(3, e => e[Pixel(0, 0)] = true, ("cobre", 100.2f, 5, 3, "Steam_1")));
        var b = SharedMap.Parse(SharedMapBytes(3, e => e[Pixel(500, 500)] = true, ("cobre", 99.9f, 5, 3, "Steam_1"), ("vila", 1, 1, 0, "Steam_1")));

        var map = SharedMap.Union(new[] { a, b });

        Assert.Equal(2, map.Explored.Count(e => e));
        Assert.Equal(new[] { "cobre", "vila" }, map.Pins.Select(p => p.Name));
    }

    [Fact]
    public void Pixel_do_mapa_como_o_Minimap()
    {
        Assert.True(SharedMap.ToPixel(0, 0, out var j, out var i));
        Assert.Equal((1024, 1024), (j, i));
        Assert.True(SharedMap.ToPixel(-0.1, 11.9, out j, out i));
        Assert.Equal((1023, 1024), (j, i));
        Assert.False(SharedMap.ToPixel(12288, 0, out _, out _));
    }

    [Fact]
    public void Mundo_cabe_exato_em_8x8_tiles_no_zoom_11()
    {
        MapProjection.ToGlobalPixel(11, -12288, 12288, out var gx0, out var gy0);
        MapProjection.ToGlobalPixel(11, 12288, -12288, out var gx1, out var gy1);

        Assert.Equal(1020 * 256.0, gx0, 6);
        Assert.Equal(1028 * 256.0, gx1, 6);
        // Mercator em 0,7 grau: menos de 0,05 px de desvio do linear.
        Assert.Equal(1020 * 256.0, gy0, 1);
        Assert.Equal(1028 * 256.0, gy1, 1);
    }

    [Theory]
    [InlineData(9, 0, 0)]
    [InlineData(14, 10499.5, -3321.25)]
    [InlineData(12, -777.7, 9001.1)]
    public void Pixel_e_mundo_vao_e_voltam(int zoom, double x, double z)
    {
        MapProjection.ToGlobalPixel(zoom, x, z, out var gx, out var gy);
        MapProjection.ToWorld(zoom, gx, gy, out var bx, out var bz);

        Assert.Equal(x, bx, 6);
        Assert.Equal(z, bz, 6);
    }

    [Fact]
    public void Norte_fica_em_cima()
    {
        MapProjection.ToGlobalPixel(14, 0, 1000, out _, out var north);
        MapProjection.ToGlobalPixel(14, 0, -1000, out _, out var south);

        Assert.True(north < south);
    }

    [Fact]
    public void Png_valido_com_os_pixels()
    {
        var rgba = Enumerable.Range(0, 3 * 2 * 4).Select(i => (byte)(i * 7)).ToArray();

        var png = Png.Encode(3, 2, rgba);

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8));
        int idat = IndexOf(png, Encoding.ASCII.GetBytes("IDAT"));
        int len = (png[idat - 4] << 24) | (png[idat - 3] << 16) | (png[idat - 2] << 8) | png[idat - 1];
        using var z = new ZLibStream(new MemoryStream(png, idat + 4, len), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var rows = raw.ToArray();
        Assert.Equal(2 * (1 + 12), rows.Length);
        Assert.Equal(rgba.Take(12), rows.Skip(1).Take(12));
        Assert.Equal(rgba.Skip(12), rows.Skip(14));
    }

    static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }

    sealed class FlatTerrain : ITerrain
    {
        public int Calls;

        public void Sample(double x, double z, out int biome, out float height, out bool forest)
        {
            Calls++;
            biome = 1;
            height = 40;
            forest = false;
        }
    }

    static TileId TileOf(int zoom, double x, double z)
    {
        MapProjection.ToGlobalPixel(zoom, x, z, out var gx, out var gy);
        return new TileId(zoom, (int)(gx / 256), (int)(gy / 256));
    }

    [Fact]
    public void Tile_sem_nada_explorado_nao_desenha_nem_consulta_terreno()
    {
        var explored = new bool[Size * Size];
        explored[Pixel(0, 0)] = true;
        var terrain = new FlatTerrain();

        Assert.Null(TilePainter.Paint(TileOf(14, 3000, 3000), explored, terrain));
        Assert.Equal(0, terrain.Calls);
    }

    [Fact]
    public void Pixel_explorado_aparece_e_o_resto_fica_transparente()
    {
        var explored = new bool[Size * Size];
        explored[Pixel(1000, 1000)] = true;
        var tile = TileOf(14, 1002, 1002);

        var rgba = TilePainter.Paint(tile, explored, new FlatTerrain());

        Assert.NotNull(rgba);
        MapProjection.ToGlobalPixel(14, 1002, 1002, out var gx, out var gy);
        int at = (((int)gy - tile.Y * 256) * 256 + ((int)gx - tile.X * 256)) * 4;
        Assert.True(rgba[at + 3] > 200);
        Assert.Equal(0, rgba[3]);
    }

    [Fact]
    public void Mudanca_alcanca_o_tile_em_todos_os_zooms()
    {
        var before = new bool[Size * Size];
        var after = (bool[])before.Clone();
        after[Pixel(-2000, 1500)] = true;

        var changed = TilePainter.Changed(before, after);
        var tiles = TilePainter.TilesTouching(changed);

        Assert.Single(changed);
        for (int zoom = TilePainter.MinZoom; zoom <= TilePainter.MaxZoom; zoom++)
            Assert.Contains(TileOf(zoom, -1994, 1506), tiles);
        Assert.Equal(TilePainter.Changed(null, after), changed);
    }

    static string TempDir() => Path.Combine(Path.GetTempPath(), "map-" + Guid.NewGuid());

    static RenderJob Job(bool[] explored, PieceLayer pieces, string dir, RenderStats stats, ITerrain terrain = null, double duty = 1, Action<int> sleep = null) =>
        new RenderJob(explored, terrain ?? new FlatTerrain(), pieces, dir, TilePainter.MaxZoom, duty, stats, sleep);

    [Fact]
    public void Render_desenha_todos_os_zooms_e_apaga_o_que_esvaziou()
    {
        var dir = TempDir();
        var explored = new bool[Size * Size];
        explored[Pixel(0, 0)] = true;
        var stats = new RenderStats();

        Job(explored, null, dir, stats).Run();
        var files = Directory.GetFiles(Path.Combine(dir, "tiles"), "*.png", SearchOption.AllDirectories);
        Assert.Equal(stats.TilesDrawn, files.Length);
        Assert.True(stats.TilesDrawn >= TilePainter.MaxZoom - TilePainter.MinZoom + 1);
        Assert.Equal(0, stats.Pending);

        Job(new bool[Size * Size], null, dir, new RenderStats()).Run();
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "tiles"), "*.png", SearchOption.AllDirectories));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Segundo_passe_sem_mudanca_nao_redesenha()
    {
        var dir = TempDir();
        var explored = new bool[Size * Size];
        explored[Pixel(500, 500)] = true;
        var pieces = new PieceLayer(new[] { new PieceMark(PieceKind.Stone, 500, 40, 500, 0, 1, 1) });
        Job(explored, pieces, dir, new RenderStats()).Run();

        var terrain = new FlatTerrain();
        var stats = new RenderStats();
        Job(explored, pieces, dir, stats, terrain).Run();

        Assert.Equal(0, stats.TilesDrawn);
        Assert.Equal(0, terrain.Calls);
        Assert.True(stats.TilesSkipped > 0);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Peca_nova_redesenha_so_os_tiles_dela_a_partir_do_zoom_das_pecas()
    {
        var dir = TempDir();
        var explored = new bool[Size * Size];
        for (int k = -3; k <= 3; k++)
            for (int m = -3; m <= 3; m++)
                explored[Pixel(1000 + k * 12, 1000 + m * 12)] = true;
        Job(explored, new PieceLayer(new PieceMark[0]), dir, new RenderStats()).Run();

        var pieces = new PieceLayer(new[] { new PieceMark(PieceKind.Wood, 1001, 40, 1001, 0, 1, 1) });
        var stats = new RenderStats();
        Job(explored, pieces, dir, stats).Run();

        Assert.Equal(TilePainter.MaxZoom - PieceLayer.MinZoom + 1, stats.TilesDrawn);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Tile_apagado_do_disco_volta_mesmo_com_assinatura_igual()
    {
        var dir = TempDir();
        var explored = new bool[Size * Size];
        explored[Pixel(0, 0)] = true;
        Job(explored, null, dir, new RenderStats()).Run();
        var victim = Directory.GetFiles(Path.Combine(dir, "tiles"), "*.png", SearchOption.AllDirectories).First();
        File.Delete(victim);

        var stats = new RenderStats();
        Job(explored, null, dir, stats).Run();

        Assert.Equal(1, stats.TilesDrawn);
        Assert.True(File.Exists(victim));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Duty_dorme_na_proporcao_do_trabalho()
    {
        var dir = TempDir();
        var explored = new bool[Size * Size];
        explored[Pixel(0, 0)] = true;
        var slept = new List<int>();

        Job(explored, null, dir, new RenderStats(), new SlowTerrain(), 0.5, ms => slept.Add(ms)).Run();

        Assert.NotEmpty(slept);
        Assert.All(slept, ms => Assert.InRange(ms, 1, 10000));
        Directory.Delete(dir, true);
    }

    sealed class SlowTerrain : ITerrain
    {
        public void Sample(double x, double z, out int biome, out float height, out bool forest)
        {
            System.Threading.Thread.SpinWait(200);
            biome = 1;
            height = 40;
            forest = false;
        }
    }

    [Fact]
    public void Configuracao_do_desenho_com_padrao_e_limites()
    {
        var env = new Dictionary<string, string>
        {
            { DrawSettings.AtVariable, "03:15" },
            { DrawSettings.MaxZoomVariable, "30" },
            { DrawSettings.DutyVariable, "0.25" },
        };

        var s = DrawSettings.Parse(k => env.TryGetValue(k, out var v) ? v : null);
        var d = DrawSettings.Parse(k => k == DrawSettings.AtVariable ? "lixo" : null);

        Assert.Equal(new TimeSpan(3, 15, 0), s.At);
        Assert.Equal(18, s.MaxZoom);
        Assert.Equal(0.25, s.Duty);
        Assert.Equal(new TimeSpan(4, 30, 0), d.At);
        Assert.Equal(TilePainter.MaxZoom, d.MaxZoom);
        Assert.Equal(1, d.ScanBudgetMs);
    }

    [Fact]
    public void Proximo_horario_e_hoje_ou_amanha()
    {
        var s = new DrawSettings { At = new TimeSpan(4, 30, 0) };

        Assert.Equal(new DateTime(2026, 9, 27, 4, 30, 0), s.NextAfter(new DateTime(2026, 9, 27, 1, 0, 0)));
        Assert.Equal(new DateTime(2026, 9, 28, 4, 30, 0), s.NextAfter(new DateTime(2026, 9, 27, 4, 30, 0)));
        Assert.Equal(new DateTime(2026, 9, 28, 4, 30, 0), s.NextAfter(new DateTime(2026, 9, 27, 9, 0, 0)));
    }

    [Fact]
    public void Livro_de_tiles_vai_ao_disco_e_volta()
    {
        var path = Path.Combine(Path.GetTempPath(), "book-" + Guid.NewGuid());
        var book = new TileBook();
        book.Entries[new TileId(17, 65540, 65530)] = new TileEntry { Signature = 0xDEADBEEFCAFEUL, Drawn = true };
        book.Entries[new TileId(9, 256, 255)] = new TileEntry { Signature = 7, Drawn = false };

        book.Save(path);
        var back = TileBook.Load(path);

        Assert.Equal(book.Entries, back.Entries);
        Assert.Empty(TileBook.Load(path + ".nao-existe").Entries);
        File.Delete(path);
    }

    static (byte r, byte g, byte b) At(byte[] rgba, TileId tile, double x, double z)
    {
        MapProjection.ToGlobalPixel(tile.Zoom, x, z, out var gx, out var gy);
        int o = (((int)gy - tile.Y * 256) * 256 + ((int)gx - tile.X * 256)) * 4;
        return (rgba[o], rgba[o + 1], rgba[o + 2]);
    }

    static bool[] ExploredAround(double x, double z)
    {
        var explored = new bool[Size * Size];
        for (int k = -3; k <= 3; k++)
            for (int m = -3; m <= 3; m++)
                explored[Pixel(x + k * 12, z + m * 12)] = true;
        return explored;
    }

    [Fact]
    public void Peca_pinta_a_cor_do_material_e_so_dentro_do_retangulo()
    {
        var explored = ExploredAround(200, 200);
        var tile = TileOf(17, 200, 200);
        // Parede de pedra de 4 m no eixo x local, 0,5 m de espessura, sem giro.
        var pieces = new PieceLayer(new[] { new PieceMark(PieceKind.Stone, 200, 40, 200, 0, 2f, 0.25f) });

        var rgba = TilePainter.Paint(tile, explored, new FlatTerrain(), pieces);
        var bare = TilePainter.Paint(tile, explored, new FlatTerrain());

        PieceLayer.Color(PieceKind.Stone, out var sr, out var sg, out var sb);
        Assert.Equal((sr, sg, sb), At(rgba, tile, 201, 200.05));
        Assert.Equal(At(bare, tile, 200, 201.5), At(rgba, tile, 200, 201.5));
    }

    [Fact]
    public void Giro_de_90_graus_troca_os_eixos_da_peca()
    {
        var explored = ExploredAround(200, 200);
        var tile = TileOf(17, 200, 200);
        var pieces = new PieceLayer(new[] { new PieceMark(PieceKind.Stone, 200, 40, 200, 90, 2f, 0.25f) });

        var rgba = TilePainter.Paint(tile, explored, new FlatTerrain(), pieces);
        var bare = TilePainter.Paint(tile, explored, new FlatTerrain());

        PieceLayer.Color(PieceKind.Stone, out var sr, out var sg, out var sb);
        Assert.Equal((sr, sg, sb), At(rgba, tile, 200.05, 201));
        Assert.Equal(At(bare, tile, 201.5, 200), At(rgba, tile, 201.5, 200));
    }

    [Fact]
    public void Peca_mais_alta_cobre_a_de_baixo()
    {
        var explored = ExploredAround(200, 200);
        var tile = TileOf(17, 200, 200);
        var pieces = new PieceLayer(new[]
        {
            new PieceMark(PieceKind.Marble, 200, 50, 200, 0, 1, 1),
            new PieceMark(PieceKind.Wood, 200, 40, 200, 0, 1, 1),
        });

        var rgba = TilePainter.Paint(tile, explored, new FlatTerrain(), pieces);

        PieceLayer.Color(PieceKind.Marble, out var r, out var g, out var b);
        Assert.Equal((r, g, b), At(rgba, tile, 200.3, 200.3));
    }

    [Fact]
    public void Peca_fora_da_area_das_mesas_nao_aparece()
    {
        var explored = ExploredAround(200, 200);
        var tile = TileOf(15, 200, 200);
        var pieces = new PieceLayer(new[] { new PieceMark(PieceKind.Stone, 330, 40, 200, 0, 5, 5) });

        var rgba = TilePainter.Paint(tile, explored, new FlatTerrain(), pieces);

        MapProjection.ToGlobalPixel(15, 330, 200, out var gx, out var gy);
        int o = (((int)gy - tile.Y * 256) * 256 + ((int)gx - tile.X * 256)) * 4;
        Assert.Equal(0, rgba[o + 3]);
        Assert.Equal(0, rgba[o]);
    }

    [Fact]
    public void Abaixo_do_zoom_das_pecas_o_tile_e_so_terreno()
    {
        var explored = ExploredAround(200, 200);
        var tile = TileOf(PieceLayer.MinZoom - 1, 200, 200);
        var pieces = new PieceLayer(new[] { new PieceMark(PieceKind.Stone, 200, 40, 200, 0, 20, 20) });

        Assert.Equal(TilePainter.Paint(tile, explored, new FlatTerrain()), TilePainter.Paint(tile, explored, new FlatTerrain(), pieces));
        Assert.Equal(TilePainter.Signature(tile, explored, null, new List<int>()), TilePainter.Signature(tile, explored, pieces, new List<int>()));
    }

    [Fact]
    public void Assinatura_nao_depende_da_ordem_das_pecas()
    {
        var a = new PieceMark(PieceKind.Wood, 10, 40, 10, 30, 1, 2);
        var b = new PieceMark(PieceKind.Iron, 12, 41, 9, 0, 1, 1);
        var tile = TileOf(16, 10, 10);
        var explored = ExploredAround(10, 10);

        var s1 = TilePainter.Signature(tile, explored, new PieceLayer(new[] { a, b }), new List<int>());
        var s2 = TilePainter.Signature(tile, explored, new PieceLayer(new[] { b, a }), new List<int>());
        var s3 = TilePainter.Signature(tile, explored, new PieceLayer(new[] { a }), new List<int>());

        Assert.Equal(s1, s2);
        Assert.NotEqual(s1, s3);
    }

    [Fact]
    public void Agua_abaixo_de_30_m()
    {
        TilePainter.Color(1, 29f, false, 1, 0, out var r, out var g, out var b);
        TilePainter.Color(1, 40f, false, 1, 0, out var lr, out var lg, out var lb);

        Assert.True(b > r && b > g);
        Assert.True(lg > lb);
    }

    [Fact]
    public void Labels_de_posicao_em_metros_e_graus()
    {
        var labels = MapProjection.Labels(new[] { "player", "Tuttan" }, 17476.266666, -8738.133333);

        Assert.Equal(new[] { "player", "Tuttan", "x", "17476", "z", "-8738", "lat", "-0.500000", "lon", "1.000000" }, labels);
    }

    [Fact]
    public void Chao_plano_nao_muda_de_cor_e_encosta_para_a_luz_clareia()
    {
        Assert.Equal(1.0, TilePainter.Shade(0, 0), 6);
        // Sobe para leste e desce para o norte: a face olha para noroeste, de onde vem a luz.
        Assert.True(TilePainter.Shade(0.5, -0.5) > 1.0);
        Assert.True(TilePainter.Shade(-0.5, 0.5) < 1.0);
    }

    [Fact]
    public void Grao_fica_entre_menos_1_e_1_e_nao_depende_do_zoom()
    {
        for (int i = 0; i < 1000; i++)
        {
            double g = TilePainter.Grain(i * 3.7 - 1800, i * -2.3 + 400);
            Assert.InRange(g, -1, 1);
        }
        Assert.Equal(TilePainter.Grain(10.2, -7.1), TilePainter.Grain(10.4, -7.3));
    }

    [Fact]
    public void Floresta_e_mais_escura_que_campo()
    {
        TilePainter.Color(1, 40f, false, 1, 0, out var r, out var g, out _);
        TilePainter.Color(1, 40f, true, 1, 0, out var fr, out var fg, out _);

        Assert.True(fr < r && fg < g);
    }

    [Theory]
    [InlineData(0, "Fogueira")]
    [InlineData(3, "Ponto")]
    [InlineData(9, "Chefe")]
    [InlineData(16, "Hildir")]
    [InlineData(99, "Outro")]
    public void Tipo_de_pin_em_portugues(int type, string kind)
    {
        Assert.Equal(kind, MapText.PinKind(type));
    }

    [Fact]
    public void Partes_maiores_em_ordem_com_porcentagem()
    {
        var parts = new[]
        {
            new KeyValuePair<string, long>("wood_wall", 20),
            new KeyValuePair<string, long>("piece_chest", 50),
            new KeyValuePair<string, long>("torch", 20),
            new KeyValuePair<string, long>("rock", 10),
        };

        Assert.Equal("piece_chest 50%, torch 20%", MapText.TopShares(parts, 2));
        Assert.Equal("", MapText.TopShares(new KeyValuePair<string, long>[0], 3));
    }
}
