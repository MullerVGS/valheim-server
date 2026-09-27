using System;
using System.Globalization;

namespace ValheimMetrics.Map
{
    // Labels de posicao das metricas. O Geomap do Grafana so desenha em Web Mercator: o mundo vira um
    // quadrado pequeno em volta de (0, 0) graus, 1 grau = MetersPerDegree metros (o mapa da mesa, 2048
    // px de 12 m, cabe exato em 8x8 tiles no zoom 11). Longitude = x / MetersPerDegree; latitude =
    // z / MetersPerDegree (norte = +z). Tao perto do Equador o Mercator e linear.
    public static class MapProjection
    {
        public const int NativeZoom = 11;
        public const double MetersPerDegree = SharedMap.Size * SharedMap.PixelSize / (360.0 / (1 << (NativeZoom - 3)));

        public static double Lon(double x) => x / MetersPerDegree;
        public static double Lat(double z) => z / MetersPerDegree;

        // Metros de mundo para gente, graus para o Geomap.
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
    }
}
