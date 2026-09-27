using System;
using System.Collections.Generic;
using System.IO;

namespace ValheimMetrics.Map
{
    // Arquivos que o plugin entrega ao site do mapa (site/), na pasta VALHEIM_MAP_DIR. O plugin so
    // entrega dado cru; o desenho e do site. Tudo little endian, linha 0 do mapa = sul (z minimo),
    // coluna 0 = oeste, pixel de 12 m como o mapa do jogo.
    public static class MapFiles
    {
        public const string Terrain = "terrain.bin";
        public const string Explored = "explored.bin";
        public const string Pieces = "pieces.bin";

        // Grava inteiro num temporario e troca: quem le nunca ve arquivo pela metade.
        public static void WriteAtomic(string path, Action<BinaryWriter> write)
        {
            var tmp = path + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp)))
                write(w);
            if (File.Exists(path))
                File.Delete(path);
            File.Move(tmp, path);
        }
    }

    // terrain.bin: o mundo inteiro, como o Minimap.GenerateWorldMap do cliente monta as texturas do
    // mapa. E spoiler: o site so manda ao navegador o que as mesas mostram.
    //   'VHM1' | u32 size | f32 pixelSize | i32 seed
    //   f16 altura[N] | u8 bioma[N] | u8 floresta[N] | u8 bruma[N] | u8 explorado[N] (sempre 0 aqui)
    // Bioma: 0 oceano/nada, 1 prado, 2 pantano, 3 montanha, 4 floresta negra, 5 planicie, 6 cinzas,
    // 7 extremo norte, 8 terras nebulosas.
    public sealed class TerrainGrid
    {
        public const int Size = SharedMap.Size;
        public const int HeaderBytes = 16;
        const int N = Size * Size;

        public readonly ushort[] Height = new ushort[N];
        public readonly byte[] Biome = new byte[N];
        public readonly byte[] Forest = new byte[N];
        public readonly byte[] Mist = new byte[N];

        // Heightmap.Biome -> indice do arquivo.
        public static byte BiomeIndex(int biome)
        {
            switch (biome)
            {
                case 1: return 1;
                case 2: return 2;
                case 4: return 3;
                case 8: return 4;
                case 16: return 5;
                case 32: return 6;
                case 64: return 7;
                case 512: return 8;
                default: return 0;
            }
        }

        // Centro do pixel do mapa, como no GenerateWorldMap: (j - 1024) * 12 + 6.
        public static float Center(int index) => (index - Size / 2) * SharedMap.PixelSize + SharedMap.PixelSize / 2;

        // Minimap.GetMaskColor: floresta no vermelho, bruma das terras nebulosas no verde. Nada na agua.
        public static void Mask(int biome, float height, float forestFactor, out byte forest, out byte mist)
        {
            forest = 0;
            mist = 0;
            if (height < 30f)
                return;
            switch (biome)
            {
                case 1: forest = forestFactor < 1.15f ? (byte)255 : (byte)0; break;
                case 16: forest = forestFactor < 0.8f ? (byte)255 : (byte)0; break;
                case 8: forest = 255; break;
                case 512:
                    double t = Math.Max(0, Math.Min(1, (forestFactor - 1.1) / 0.2));
                    mist = (byte)Math.Round((1 - t * t * (3 - 2 * t)) * 255);
                    break;
            }
        }

        public void Set(int i, int j, int biome, float height, float forestFactor)
        {
            int k = i * Size + j;
            Height[k] = HalfFloat.FromFloat(height);
            Biome[k] = BiomeIndex(biome);
            Mask(biome, height, forestFactor, out Forest[k], out Mist[k]);
        }

        public void Write(BinaryWriter w, int seed)
        {
            w.Write(new[] { (byte)'V', (byte)'H', (byte)'M', (byte)'1' });
            w.Write((uint)Size);
            w.Write(SharedMap.PixelSize);
            w.Write(seed);
            var bytes = new byte[N * 2];
            Buffer.BlockCopy(Height, 0, bytes, 0, bytes.Length);
            w.Write(bytes);
            w.Write(Biome);
            w.Write(Forest);
            w.Write(Mist);
            w.Write(new byte[N]);
        }

        // Seed gravada no cabecalho, ou null se o arquivo nao existe ou nao e deste formato.
        public static int? ReadSeed(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length != HeaderBytes + N * 6L)
                return null;
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                var magic = r.ReadBytes(4);
                if (magic[0] != 'V' || magic[1] != 'H' || magic[2] != 'M' || magic[3] != '1' || r.ReadUInt32() != Size)
                    return null;
                r.ReadSingle();
                return r.ReadInt32();
            }
        }
    }

    // explored.bin: 1 byte por pixel (1 = alguma mesa mostra), sem cabecalho: 2048 x 2048 bytes.
    public static class ExploredFile
    {
        public static void Write(BinaryWriter w, bool[] explored)
        {
            var bytes = new byte[explored.Length];
            for (int i = 0; i < explored.Length; i++)
                bytes[i] = explored[i] ? (byte)1 : (byte)0;
            w.Write(bytes);
        }

        public static bool Same(bool[] a, bool[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }
    }

    // pieces.bin: construcoes vistas de cima, so onde as mesas mostram.
    //   'VPC1' | u32 n | n x (f32 x, f32 z, f32 y, f32 cos, f32 sin, f32 meiaX, f32 meiaZ, u8 tipo)
    // Retangulo no centro (x, z), girado pelo yaw (cos/sin), meia largura nos eixos locais x e z; y e a
    // altura (telhado cobre o que esta embaixo). Tipo = PieceKind.
    public static class PiecesFile
    {
        public const int RecordBytes = 29;

        public static void Write(BinaryWriter w, IList<PieceMark> marks)
        {
            w.Write(new[] { (byte)'V', (byte)'P', (byte)'C', (byte)'1' });
            w.Write((uint)marks.Count);
            foreach (var m in marks)
            {
                w.Write(m.X);
                w.Write(m.Z);
                w.Write(m.Y);
                w.Write(m.Cos);
                w.Write(m.Sin);
                w.Write(m.HalfX);
                w.Write(m.HalfZ);
                w.Write((byte)m.Kind);
            }
        }
    }

    // pieces-catalog.bin: forma de cada prefab de peca, para montar as construcoes de um save antigo
    // fora do jogo (o save guarda prefab, posicao e giro; a forma vem dos colliders do prefab).
    //   'VPK1' | u32 n | n x (i32 hash do prefab, u8 tipo, u8 planta, f32 centroX, f32 centroZ, f32 meiaX, f32 meiaZ)
    // Planta = 1: entra sem criador (planta crescida nasce sem). Tipo = PieceKind.
    public struct CatalogEntry
    {
        public int Prefab;
        public PieceKind Kind;
        public bool Crop;
        public float CenterX;
        public float CenterZ;
        public float HalfX;
        public float HalfZ;
    }

    public static class CatalogFile
    {
        public const string Name = "pieces-catalog.bin";
        public const int RecordBytes = 22;

        public static void Write(BinaryWriter w, IList<CatalogEntry> entries)
        {
            w.Write(new[] { (byte)'V', (byte)'P', (byte)'K', (byte)'1' });
            w.Write((uint)entries.Count);
            foreach (var e in entries)
            {
                w.Write(e.Prefab);
                w.Write((byte)e.Kind);
                w.Write(e.Crop ? (byte)1 : (byte)0);
                w.Write(e.CenterX);
                w.Write(e.CenterZ);
                w.Write(e.HalfX);
                w.Write(e.HalfZ);
            }
        }
    }

    // float -> IEEE 754 meia precisao (o Mono do jogo nao tem System.Half). Arredonda para o mais
    // proximo; altura do mundo cabe com folga (+-65504, passo de 0,03 m a 60 m).
    public static class HalfFloat
    {
        public static ushort FromFloat(float value)
        {
            uint f = BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);
            uint sign = (f >> 16) & 0x8000;
            int exp = (int)((f >> 23) & 0xFF) - 127 + 15;
            uint mant = f & 0x7FFFFF;
            if (exp >= 31)
                return (ushort)(sign | 0x7C00);
            if (exp <= 0)
            {
                if (exp < -10)
                    return (ushort)sign;
                mant |= 0x800000;
                int shift = 14 - exp;
                uint half = mant >> shift;
                if (((mant >> (shift - 1)) & 1) != 0)
                    half++;
                return (ushort)(sign | half);
            }
            uint h = sign | ((uint)exp << 10) | (mant >> 13);
            if ((mant & 0x1000) != 0)
                h++;
            return (ushort)h;
        }
    }
}

namespace ValheimMetrics.Map
{
    // Ritmo da exportacao. Tudo com padrao: so VALHEIM_MAP_DIR liga os arquivos.
    public sealed class MapSettings
    {
        public const string PiecesMinutesVariable = "VALHEIM_MAP_PIECES_MINUTES";
        public const string ScanBudgetVariable = "VALHEIM_MAP_SCAN_BUDGET_MS";

        // Varredura das construcoes: ~50 ms de trabalho espalhado em ~50 frames com o mundo de 27/09.
        public System.TimeSpan PiecesEvery = System.TimeSpan.FromMinutes(60);
        public double ScanBudgetMs = 1;

        public static MapSettings Parse(System.Func<string, string> env)
        {
            var s = new MapSettings();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (int.TryParse(env(PiecesMinutesVariable)?.Trim(), System.Globalization.NumberStyles.Integer, inv, out var m))
                s.PiecesEvery = System.TimeSpan.FromMinutes(System.Math.Max(10, System.Math.Min(1440, m)));
            if (double.TryParse(env(ScanBudgetVariable)?.Trim(), System.Globalization.NumberStyles.Float, inv, out var b))
                s.ScanBudgetMs = System.Math.Max(0.1, System.Math.Min(20, b));
            return s;
        }
    }
}
