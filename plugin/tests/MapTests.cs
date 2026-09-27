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

    [Fact]
    public void Tile_que_esvaziou_e_apagado()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-" + Guid.NewGuid());
        var explored = new bool[Size * Size];
        explored[Pixel(0, 0)] = true;
        var tiles = TilePainter.TilesTouching(TilePainter.Changed(null, explored));

        int drawn = TilePainter.Render(tiles, explored, new FlatTerrain(), root);
        var files = Directory.GetFiles(root, "*.png", SearchOption.AllDirectories);
        Assert.Equal(drawn, files.Length);
        Assert.True(drawn >= TilePainter.MaxZoom - TilePainter.MinZoom + 1);

        TilePainter.Render(tiles, new bool[Size * Size], new FlatTerrain(), root);
        Assert.Empty(Directory.GetFiles(root, "*.png", SearchOption.AllDirectories));
        Directory.Delete(root, true);
    }

    [Fact]
    public void Mascara_vai_ao_disco_e_volta()
    {
        var path = Path.Combine(Path.GetTempPath(), "mask-" + Guid.NewGuid() + ".gz");
        var mask = new bool[Size * Size];
        mask[Pixel(-100, 42)] = true;

        MaskFile.Save(path, mask);

        Assert.Equal(mask, MaskFile.Load(path));
        Assert.Null(MaskFile.Load(path + ".nao-existe"));
        File.Delete(path);
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
