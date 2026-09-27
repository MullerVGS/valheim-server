using System;
using System.Globalization;

namespace ValheimMetrics.Map
{
    // O Geomap do Grafana so desenha em Web Mercator. O mundo vira um quadrado pequeno em volta de
    // (0, 0) graus: 1 grau = MetersPerDegree metros, escolhido para que o mapa da mesa (2048 px de
    // 12 m) caiba exato em 8x8 tiles no zoom 11. Longitude = x / MetersPerDegree; latitude = z /
    // MetersPerDegree (norte = +z). Tao perto do Equador o Mercator e linear: na borda do mundo o
    // erro e de 0,3 m, e os tiles usam a inversa exata, entao ponto e fundo nunca divergem.
    public static class MapProjection
    {
        public const int TileSize = 256;
        public const int NativeZoom = 11;
        public const double MetersPerDegree = SharedMap.Size * SharedMap.PixelSize / (360.0 / (1 << (NativeZoom - 3)));

        public static double Lon(double x) => x / MetersPerDegree;
        public static double Lat(double z) => z / MetersPerDegree;

        // Labels de posicao das metricas: metros de mundo para gente, graus para o Geomap.
        public static string[] Labels(double x, double z)
        {
            var inv = CultureInfo.InvariantCulture;
            return new[]
            {
                "x", x.ToString("F0", inv),
                "z", z.ToString("F0", inv),
                "lat", Lat(z).ToString("F6", inv),
                "lon", Lon(x).ToString("F6", inv),
            };
        }

        public static string[] Labels(string[] head, double x, double z)
        {
            var coords = Labels(x, z);
            var all = new string[head.Length + coords.Length];
            Array.Copy(head, all, head.Length);
            Array.Copy(coords, 0, all, head.Length, coords.Length);
            return all;
        }

        static double WorldPixels(int zoom) => TileSize * (double)(1L << zoom);

        // Pixel global (com fracao) do ponto no zoom dado, origem no canto superior esquerdo do planeta.
        public static void ToGlobalPixel(int zoom, double x, double z, out double gx, out double gy)
        {
            double n = WorldPixels(zoom);
            gx = (Lon(x) + 180.0) / 360.0 * n;
            double lat = Lat(z) * Math.PI / 180.0;
            gy = (1.0 - Math.Log(Math.Tan(lat) + 1.0 / Math.Cos(lat)) / Math.PI) / 2.0 * n;
        }

        public static void ToWorld(int zoom, double gx, double gy, out double x, out double z)
        {
            double n = WorldPixels(zoom);
            x = (gx / n * 360.0 - 180.0) * MetersPerDegree;
            double lat = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * gy / n))) * 180.0 / Math.PI;
            z = lat * MetersPerDegree;
        }

        // Tiles que cobrem o retangulo de mundo [x0, x1] x [z0, z1].
        public static void TileRange(int zoom, double x0, double z0, double x1, double z1,
            out int tx0, out int ty0, out int tx1, out int ty1)
        {
            ToGlobalPixel(zoom, x0, z1, out var gx0, out var gy0);
            ToGlobalPixel(zoom, x1, z0, out var gx1, out var gy1);
            tx0 = (int)Math.Floor(gx0 / TileSize);
            ty0 = (int)Math.Floor(gy0 / TileSize);
            tx1 = (int)Math.Floor(gx1 / TileSize);
            ty1 = (int)Math.Floor(gy1 / TileSize);
        }
    }
}
