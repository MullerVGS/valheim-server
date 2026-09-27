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
    public void Mascara_como_o_Minimap()
    {
        byte f, m;
        TerrainGrid.Mask(1, 40, 1.0f, out f, out m);
        Assert.Equal((255, 0), (f, m));
        TerrainGrid.Mask(1, 40, 1.2f, out f, out m);
        Assert.Equal((0, 0), (f, m));
        TerrainGrid.Mask(16, 40, 0.79f, out f, out _);
        Assert.Equal(255, f);
        TerrainGrid.Mask(16, 40, 0.81f, out f, out _);
        Assert.Equal(0, f);
        TerrainGrid.Mask(8, 29.9f, 0, out f, out _);
        Assert.Equal(0, f);
        TerrainGrid.Mask(512, 40, 1.0f, out f, out m);
        Assert.Equal((0, 255), (f, m));
        TerrainGrid.Mask(512, 40, 1.2f, out _, out m);
        Assert.InRange(m, 127, 128);
        TerrainGrid.Mask(512, 40, 1.4f, out _, out m);
        Assert.Equal(0, m);
    }

    [Theory]
    [InlineData(0f, 0x0000)]
    [InlineData(1f, 0x3C00)]
    [InlineData(-2f, 0xC000)]
    [InlineData(65504f, 0x7BFF)]
    [InlineData(1e9f, 0x7C00)]
    [InlineData(0.000061035156f, 0x0400)]
    public void Meia_precisao_como_o_IEEE(float value, int bits)
    {
        Assert.Equal((ushort)bits, HalfFloat.FromFloat(value));
    }

    [Theory]
    [InlineData(31.37f)]
    [InlineData(-87.5f)]
    [InlineData(212.9f)]
    public void Altura_em_meia_precisao_perde_menos_de_um_decimo(float h)
    {
        Assert.InRange(Math.Abs((float)BitConverter.UInt16BitsToHalf(HalfFloat.FromFloat(h)) - h), 0, 0.1f);
    }

    [Fact]
    public void Centro_do_pixel_como_o_GenerateWorldMap()
    {
        Assert.Equal(-12282f, TerrainGrid.Center(0));
        Assert.Equal(6f, TerrainGrid.Center(1024));
        Assert.Equal(12282f, TerrainGrid.Center(2047));
    }

    [Fact]
    public void Terreno_grava_no_formato_do_site_e_guarda_a_seed()
    {
        var path = Path.Combine(Path.GetTempPath(), "terrain-" + Guid.NewGuid() + ".bin");
        var grid = new TerrainGrid();
        grid.Set(3, 5, 8, 42.5f, 0);
        MapFiles.WriteAtomic(path, w => grid.Write(w, -1434277264));

        var bytes = File.ReadAllBytes(path);
        const int n = Size * Size;
        int k = 3 * Size + 5;
        Assert.Equal("VHM1", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(TerrainGrid.HeaderBytes + n * 6, bytes.Length);
        Assert.Equal(42.5f, (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, 16 + k * 2)));
        Assert.Equal(4, bytes[16 + n * 2 + k]);
        Assert.Equal(255, bytes[16 + n * 3 + k]);
        Assert.Equal(0, bytes[16 + n * 5 + k]);
        Assert.Equal(-1434277264, TerrainGrid.ReadSeed(path));
        Assert.Null(TerrainGrid.ReadSeed(path + ".nao-existe"));
        File.Delete(path);
    }

    [Fact]
    public void Explorado_um_byte_por_pixel_e_compara_conteudo()
    {
        var a = new bool[Size * Size];
        a[Pixel(100, -50)] = true;
        var path = Path.Combine(Path.GetTempPath(), "explored-" + Guid.NewGuid() + ".bin");
        MapFiles.WriteAtomic(path, w => ExploredFile.Write(w, a));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(Size * Size, bytes.Length);
        Assert.Equal(1, bytes.Sum(b => b));
        Assert.Equal(1, bytes[Pixel(100, -50)]);
        Assert.True(ExploredFile.Same(a, (bool[])a.Clone()));
        Assert.False(ExploredFile.Same(null, a));
        var b = (bool[])a.Clone();
        b[0] = true;
        Assert.False(ExploredFile.Same(a, b));
        File.Delete(path);
    }

    [Fact]
    public void Pecas_no_formato_do_site()
    {
        var marks = new[] { new PieceMark(PieceKind.Stone, 10, 35, -20, 90, 2, 0.25f), new PieceMark(PieceKind.Crop, 1, 2, 3, 0, 0.5f, 0.5f) };
        var path = Path.Combine(Path.GetTempPath(), "pieces-" + Guid.NewGuid() + ".bin");
        MapFiles.WriteAtomic(path, w => PiecesFile.Write(w, marks));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal("VPC1", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(2u, BitConverter.ToUInt32(bytes, 4));
        Assert.Equal(8 + 2 * PiecesFile.RecordBytes, bytes.Length);
        Assert.Equal(10f, BitConverter.ToSingle(bytes, 8));
        Assert.Equal(-20f, BitConverter.ToSingle(bytes, 12));
        Assert.Equal(35f, BitConverter.ToSingle(bytes, 16));
        Assert.Equal(1f, BitConverter.ToSingle(bytes, 24), 5);
        Assert.Equal(2f, BitConverter.ToSingle(bytes, 28));
        Assert.Equal((byte)PieceKind.Stone, bytes[8 + 28]);
        Assert.Equal((byte)PieceKind.Crop, bytes[8 + PiecesFile.RecordBytes + 28]);
        File.Delete(path);
    }

    [Fact]
    public void Configuracao_com_padrao_e_limites()
    {
        var s = MapSettings.Parse(k => k == MapSettings.PiecesMinutesVariable ? "2" : k == MapSettings.ScanBudgetVariable ? "3.5" : null);
        var d = MapSettings.Parse(k => k == MapSettings.PiecesMinutesVariable ? "lixo" : null);

        Assert.Equal(TimeSpan.FromMinutes(10), s.PiecesEvery);
        Assert.Equal(3.5, s.ScanBudgetMs);
        Assert.Equal(TimeSpan.FromMinutes(60), d.PiecesEvery);
        Assert.Equal(1, d.ScanBudgetMs);
    }

    [Fact]
    public void Labels_de_posicao_em_metros_e_graus()
    {
        var labels = MapProjection.Labels(new[] { "player", "Tuttan" }, 17476.266666, -8738.133333);

        Assert.Equal(new[] { "player", "Tuttan", "x", "17476", "z", "-8738", "lat", "-0.500000", "lon", "1.000000" }, labels);
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
