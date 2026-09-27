using System;
using System.Collections.Generic;

namespace ValheimMetrics.Map
{
    // Terreno do gerador de mundo. Precisa aguentar chamada fora da thread principal.
    public interface ITerrain
    {
        void Sample(double x, double z, out int biome, out float height, out bool forest);
    }

    public readonly struct TileId : IEquatable<TileId>
    {
        public readonly int Zoom;
        public readonly int X;
        public readonly int Y;

        public TileId(int zoom, int x, int y)
        {
            Zoom = zoom;
            X = x;
            Y = y;
        }

        public string Path(string root) => System.IO.Path.Combine(root, Zoom.ToString(), X.ToString(), Y + ".png");

        public bool Equals(TileId other) => Zoom == other.Zoom && X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is TileId other && Equals(other);
        public override int GetHashCode() => (Zoom * 7919 + X) * 7919 + Y;
    }

    // Buffers de um tile, reaproveitados de tile em tile: no Mono do servidor cada alocacao grande
    // vira coleta de lixo, e a coleta para o jogo inteiro.
    public sealed class TileCanvas
    {
        public const int G = MapProjection.TileSize + 2;
        public readonly float[] Alpha = new float[G * G];
        public readonly double[] Xs = new double[G * G];
        public readonly double[] Zs = new double[G * G];
        public readonly bool[] Need = new bool[G * G];
        public readonly float[] Height = new float[G * G];
        public readonly int[] Biome = new int[G * G];
        public readonly bool[] Forest = new bool[G * G];
        public readonly byte[] Rgba = new byte[MapProjection.TileSize * MapProjection.TileSize * 4];
        public readonly List<int> Scratch = new List<int>();
    }

    // Pinta um tile XYZ com o terreno so onde a mesa ja mostra; o resto fica transparente, sem spoiler.
    // A borda do explorado sai da interpolacao bilinear da mascara de 12 m, entao nao aparece a escada.
    // As pecas construidas vao por cima (PieceLayer).
    public static class TilePainter
    {
        public const int MinZoom = 9;
        public const int MaxZoom = 17;
        // Muda quando o desenho muda: entra na assinatura de todo tile, entao tudo e redesenhado.
        public const int Style = 3;
        const int N = MapProjection.TileSize;
        const float WaterLevel = 30f;

        // Heightmap.Biome
        const int Meadows = 1, Swamp = 2, Mountain = 4, BlackForest = 8, Plains = 16, AshLands = 32,
            DeepNorth = 64, Ocean = 256, Mistlands = 512;

        // Tiles que um conjunto de pixels de mapa alcanca em todos os zooms (com 1 pixel de folga,
        // porque a borda interpolada invade o vizinho).
        public static HashSet<TileId> TilesTouching(IEnumerable<int> pixels, int maxZoom = MaxZoom)
        {
            var tiles = new HashSet<TileId>();
            var half = SharedMap.Size / 2;
            foreach (var p in pixels)
            {
                int j = p % SharedMap.Size, i = p / SharedMap.Size;
                double x0 = (j - half - 1) * SharedMap.PixelSize, x1 = (j - half + 2) * SharedMap.PixelSize;
                double z0 = (i - half - 1) * SharedMap.PixelSize, z1 = (i - half + 2) * SharedMap.PixelSize;
                for (int zoom = MinZoom; zoom <= maxZoom; zoom++)
                {
                    MapProjection.TileRange(zoom, x0, z0, x1, z1, out var tx0, out var ty0, out var tx1, out var ty1);
                    for (int ty = ty0; ty <= ty1; ty++)
                        for (int tx = tx0; tx <= tx1; tx++)
                            tiles.Add(new TileId(zoom, tx, ty));
                }
            }
            return tiles;
        }

        // Pixels cujo estado mudou entre duas mascaras (null = nada explorado).
        public static List<int> Changed(bool[] before, bool[] after)
        {
            var changed = new List<int>();
            for (int i = 0; i < after.Length; i++)
                if ((before != null && before[i]) != after[i])
                    changed.Add(i);
            return changed;
        }

        // Retangulo de mundo do tile.
        public static void Bounds(TileId tile, out double x0, out double z0, out double x1, out double z1)
        {
            MapProjection.ToWorld(tile.Zoom, tile.X * N, (tile.Y + 1) * N, out x0, out z0);
            MapProjection.ToWorld(tile.Zoom, (tile.X + 1) * N, tile.Y * N, out x1, out z1);
        }

        // Tudo que muda o desenho do tile: estilo, pixels da mascara que ele alcanca (com a folga da
        // interpolacao) e, do zoom das pecas para cima, as pecas dentro dele.
        public static ulong Signature(TileId tile, bool[] explored, PieceLayer pieces, List<int> scratch)
        {
            Bounds(tile, out var x0, out var z0, out var x1, out var z1);
            ulong h = PieceMark.Mix(1469598103934665603UL, Style);
            int half = SharedMap.Size / 2;
            int j0 = Math.Max(0, (int)Math.Floor(x0 / SharedMap.PixelSize) + half - 1);
            int j1 = Math.Min(SharedMap.Size - 1, (int)Math.Floor(x1 / SharedMap.PixelSize) + half + 1);
            int i0 = Math.Max(0, (int)Math.Floor(z0 / SharedMap.PixelSize) + half - 1);
            int i1 = Math.Min(SharedMap.Size - 1, (int)Math.Floor(z1 / SharedMap.PixelSize) + half + 1);
            unchecked
            {
                for (int i = i0; i <= i1; i++)
                    for (int j = j0; j <= j1; j++)
                        if (explored[i * SharedMap.Size + j])
                            h = PieceMark.Mix(h, (ulong)(i * SharedMap.Size + j));
            }
            if (pieces != null && tile.Zoom >= PieceLayer.MinZoom)
                h = PieceMark.Mix(h, pieces.Signature(x0, z0, x1, z1, scratch));
            return h;
        }

        // Para teste e uso avulso: canvas proprio, devolve o RGBA ou null se o tile fica vazio.
        public static byte[] Paint(TileId tile, bool[] explored, ITerrain terrain, PieceLayer pieces = null)
        {
            var canvas = new TileCanvas();
            return Paint(tile, explored, terrain, pieces, canvas) ? canvas.Rgba : null;
        }

        public static bool Paint(TileId tile, bool[] explored, ITerrain terrain, PieceLayer pieces, TileCanvas canvas)
        {
            const int G = TileCanvas.G;
            var alpha = canvas.Alpha;
            var xs = canvas.Xs;
            var zs = canvas.Zs;
            bool any = false;
            for (int r = 0; r < G; r++)
            {
                for (int c = 0; c < G; c++)
                {
                    int k = r * G + c;
                    MapProjection.ToWorld(tile.Zoom, tile.X * N + c - 1 + 0.5, tile.Y * N + r - 1 + 0.5, out xs[k], out zs[k]);
                    alpha[k] = Coverage(explored, xs[k], zs[k]);
                    if (alpha[k] > 0 && r > 0 && r <= N && c > 0 && c <= N)
                        any = true;
                }
            }
            if (!any)
                return false;

            var need = canvas.Need;
            Array.Clear(need, 0, need.Length);
            for (int r = 1; r <= N; r++)
                for (int c = 1; c <= N; c++)
                    if (alpha[r * G + c] > 0)
                    {
                        int k = r * G + c;
                        need[k] = need[k - 1] = need[k + 1] = need[k - G] = need[k + G] = true;
                    }

            var height = canvas.Height;
            var biome = canvas.Biome;
            var forest = canvas.Forest;
            for (int k = 0; k < G * G; k++)
                if (need[k])
                    terrain.Sample(xs[k], zs[k], out biome[k], out height[k], out forest[k]);

            double mpp = SharedMap.PixelSize * Math.Pow(2, MapProjection.NativeZoom - tile.Zoom);
            var rgba = canvas.Rgba;
            Array.Clear(rgba, 0, rgba.Length);
            for (int r = 1; r <= N; r++)
            {
                for (int c = 1; c <= N; c++)
                {
                    int k = r * G + c;
                    if (alpha[k] <= 0)
                        continue;
                    // x cresce para leste, a linha cresce para o sul: dhdz positivo = sobe para o norte.
                    double dhdx = (height[k + 1] - height[k - 1]) / (2 * mpp);
                    double dhdz = (height[k - G] - height[k + G]) / (2 * mpp);
                    Color(biome[k], height[k], forest[k], Shade(dhdx, dhdz), Grain(xs[k], zs[k]),
                        out var red, out var green, out var blue);
                    int o = ((r - 1) * N + (c - 1)) * 4;
                    rgba[o] = red;
                    rgba[o + 1] = green;
                    rgba[o + 2] = blue;
                    double a = alpha[k];
                    rgba[o + 3] = (byte)Math.Round(a * a * (3 - 2 * a) * 255);
                }
            }
            pieces?.Draw(tile, rgba, canvas.Scratch);
            return true;
        }

        // Fracao explorada no ponto, interpolando os centros dos pixels de 12 m.
        public static float Coverage(bool[] explored, double x, double z)
        {
            double fx = x / SharedMap.PixelSize + SharedMap.Size / 2 - 0.5;
            double fz = z / SharedMap.PixelSize + SharedMap.Size / 2 - 0.5;
            int j = (int)Math.Floor(fx), i = (int)Math.Floor(fz);
            double tx = fx - j, tz = fz - i;
            double v = At(explored, j, i) * (1 - tx) * (1 - tz) + At(explored, j + 1, i) * tx * (1 - tz)
                + At(explored, j, i + 1) * (1 - tx) * tz + At(explored, j + 1, i + 1) * tx * tz;
            return (float)v;
        }

        static double At(bool[] explored, int j, int i) =>
            j >= 0 && j < SharedMap.Size && i >= 0 && i < SharedMap.Size && explored[i * SharedMap.Size + j] ? 1 : 0;

        // Lambert com luz de noroeste a 45 graus, relevo exagerado 2x e luz ambiente: 1 = chao plano.
        public static double Shade(double dhdx, double dhdz)
        {
            const double Exaggeration = 2.0;
            double nx = -dhdx * Exaggeration, nz = -dhdz * Exaggeration;
            double nl = Math.Sqrt(nx * nx + 1 + nz * nz);
            const double lx = -0.5, ly = 0.7071, lz = 0.5;
            double lambert = Math.Max(0, (nx * lx + ly + nz * lz) / nl);
            return 0.35 + 0.65 * lambert / ly;
        }

        // Ruido fixo no mundo (celula de 1,5 m), igual em todo zoom: textura de copa e de chao.
        public static double Grain(double x, double z)
        {
            unchecked
            {
                int cx = (int)Math.Floor(x / 1.5), cz = (int)Math.Floor(z / 1.5);
                uint h = (uint)(cx * 73856093) ^ (uint)(cz * 19349663);
                h ^= h >> 13;
                h *= 0x5bd1e995;
                h ^= h >> 15;
                return (h & 0xFFFF) / 65535.0 * 2 - 1;
            }
        }

        public static void Color(int biome, float height, bool forest, double shade, double grain,
            out byte red, out byte green, out byte blue)
        {
            double r, g, b;
            if (height < WaterLevel)
            {
                // Raso turquesa perto da costa, azul fundo longe; a agua nao recebe sombra do fundo.
                double depth = Math.Min(1, Math.Max(0, (WaterLevel - height) / 30.0));
                double t = Math.Sqrt(depth);
                r = 92 - 64 * t;
                g = 158 - 88 * t;
                b = 170 - 52 * t;
                if (height > WaterLevel - 1.2f)
                    (r, g, b) = (r + 28, g + 26, b + 18);
                red = Clamp(r);
                green = Clamp(g);
                blue = Clamp(b);
                return;
            }

            switch (biome)
            {
                case Meadows: (r, g, b) = (132.0, 164.0, 72.0); break;
                case BlackForest: (r, g, b) = (74.0, 98.0, 58.0); break;
                case Swamp: (r, g, b) = (98.0, 86.0, 62.0); break;
                case Mountain: (r, g, b) = (226.0, 231.0, 238.0); break;
                case Plains: (r, g, b) = (206.0, 188.0, 110.0); break;
                case Mistlands: (r, g, b) = (118.0, 110.0, 130.0); break;
                case AshLands: (r, g, b) = (132.0, 54.0, 44.0); break;
                case DeepNorth: (r, g, b) = (212.0, 226.0, 240.0); break;
                case Ocean: (r, g, b) = (200.0, 188.0, 148.0); break;
                default: (r, g, b) = (160.0, 160.0, 160.0); break;
            }
            if (forest)
            {
                // Copa: mais escura e manchada, puxada para o verde da floresta do bioma.
                double k = 0.62 + 0.14 * grain;
                (r, g, b) = (r * k, g * (k + 0.06), b * k);
            }
            else
            {
                double k = 1 + 0.05 * grain;
                (r, g, b) = (r * k, g * k, b * k);
            }
            // Praia: o primeiro metro e meio acima da agua.
            if (height < WaterLevel + 1.5f && biome != Mountain && biome != DeepNorth)
                (r, g, b) = (214.0 + 8 * grain, 200.0 + 8 * grain, 150.0 + 6 * grain);
            // Mais alto, um pouco mais claro: ajuda a ler morro sem curva de nivel.
            double lift = 1 + Math.Min(0.18, Math.Max(0, (height - WaterLevel) / 400.0));

            red = Clamp(r * shade * lift);
            green = Clamp(g * shade * lift);
            blue = Clamp(b * shade * lift);
        }

        static byte Clamp(double v) => (byte)Math.Max(0, Math.Min(255, Math.Round(v)));
    }
}
