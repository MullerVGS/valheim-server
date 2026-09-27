using System;
using System.Collections.Generic;
using System.IO;

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

    // Pinta um tile XYZ com o terreno so onde a mesa ja mostra; o resto fica transparente, sem spoiler.
    // A borda do explorado sai da interpolacao bilinear da mascara de 12 m, entao nao aparece a escada.
    public static class TilePainter
    {
        public const int MinZoom = 9;
        public const int MaxZoom = 14;
        const int N = MapProjection.TileSize;
        const float WaterLevel = 30f;

        // Heightmap.Biome
        const int Meadows = 1, Swamp = 2, Mountain = 4, BlackForest = 8, Plains = 16, AshLands = 32,
            DeepNorth = 64, Ocean = 256, Mistlands = 512;

        // Tiles que um conjunto de pixels de mapa alcanca em todos os zooms (com 1 pixel de folga,
        // porque a borda interpolada invade o vizinho).
        public static HashSet<TileId> TilesTouching(IEnumerable<int> pixels)
        {
            var tiles = new HashSet<TileId>();
            var half = SharedMap.Size / 2;
            foreach (var p in pixels)
            {
                int j = p % SharedMap.Size, i = p / SharedMap.Size;
                double x0 = (j - half - 1) * SharedMap.PixelSize, x1 = (j - half + 2) * SharedMap.PixelSize;
                double z0 = (i - half - 1) * SharedMap.PixelSize, z1 = (i - half + 2) * SharedMap.PixelSize;
                for (int zoom = MinZoom; zoom <= MaxZoom; zoom++)
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

        // Grava ou apaga cada tile. Devolve quantos ficaram com desenho.
        public static int Render(IEnumerable<TileId> tiles, bool[] explored, ITerrain terrain, string root)
        {
            int drawn = 0;
            foreach (var tile in tiles)
            {
                var path = tile.Path(root);
                var rgba = Paint(tile, explored, terrain);
                if (rgba == null)
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, Png.Encode(N, N, rgba));
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tmp, path);
                drawn++;
            }
            return drawn;
        }

        public static byte[] Paint(TileId tile, bool[] explored, ITerrain terrain)
        {
            const int G = N + 2;
            var alpha = new float[G * G];
            var xs = new double[G * G];
            var zs = new double[G * G];
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
                return null;

            var need = new bool[G * G];
            for (int r = 1; r <= N; r++)
                for (int c = 1; c <= N; c++)
                    if (alpha[r * G + c] > 0)
                    {
                        int k = r * G + c;
                        need[k] = need[k - 1] = need[k + 1] = need[k - G] = need[k + G] = true;
                    }

            var height = new float[G * G];
            var biome = new int[G * G];
            var forest = new bool[G * G];
            for (int k = 0; k < G * G; k++)
                if (need[k])
                    terrain.Sample(xs[k], zs[k], out biome[k], out height[k], out forest[k]);

            double mpp = SharedMap.PixelSize * Math.Pow(2, MapProjection.NativeZoom - tile.Zoom);
            var rgba = new byte[N * N * 4];
            for (int r = 1; r <= N; r++)
            {
                for (int c = 1; c <= N; c++)
                {
                    int k = r * G + c;
                    if (alpha[k] <= 0)
                        continue;
                    // Luz de noroeste: x cresce para leste, a linha cresce para o sul.
                    double dhdx = (height[k + 1] - height[k - 1]) / (2 * mpp);
                    double dhdz = (height[k - G] - height[k + G]) / (2 * mpp);
                    Color(biome[k], height[k], forest[k], dhdx - dhdz, out var red, out var green, out var blue);
                    int o = ((r - 1) * N + (c - 1)) * 4;
                    rgba[o] = red;
                    rgba[o + 1] = green;
                    rgba[o + 2] = blue;
                    rgba[o + 3] = (byte)Math.Round(alpha[k] * 255);
                }
            }
            return rgba;
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

        public static void Color(int biome, float height, bool forest, double slope, out byte red, out byte green, out byte blue)
        {
            double r, g, b;
            if (height < WaterLevel)
            {
                double depth = Math.Min(1, Math.Max(0, (WaterLevel - height) / 40.0));
                r = 78 - 43 * depth;
                g = 132 - 62 * depth;
                b = 165 - 50 * depth;
                red = (byte)r;
                green = (byte)g;
                blue = (byte)b;
                return;
            }

            switch (biome)
            {
                case Meadows: (r, g, b) = forest ? (96.0, 128.0, 56.0) : (142.0, 168.0, 70.0); break;
                case BlackForest: (r, g, b) = (62.0, 86.0, 46.0); break;
                case Swamp: (r, g, b) = (104.0, 88.0, 62.0); break;
                case Mountain: (r, g, b) = (222.0, 228.0, 234.0); break;
                case Plains: (r, g, b) = forest ? (170.0, 160.0, 90.0) : (206.0, 190.0, 112.0); break;
                case Mistlands: (r, g, b) = forest ? (92.0, 86.0, 104.0) : (122.0, 112.0, 132.0); break;
                case AshLands: (r, g, b) = (140.0, 52.0, 42.0); break;
                case DeepNorth: (r, g, b) = (206.0, 222.0, 236.0); break;
                case Ocean: (r, g, b) = (200.0, 188.0, 148.0); break;
                default: (r, g, b) = (160.0, 160.0, 160.0); break;
            }
            // Praia: o primeiro metro e meio acima da agua.
            if (height < WaterLevel + 1.5f && biome != Mountain && biome != DeepNorth)
                (r, g, b) = (214.0, 200.0, 150.0);

            double shade = Math.Max(0.6, Math.Min(1.4, 1 + slope * 0.6));
            red = Clamp(r * shade);
            green = Clamp(g * shade);
            blue = Clamp(b * shade);
        }

        static byte Clamp(double v) => (byte)Math.Max(0, Math.Min(255, Math.Round(v)));
    }
}
