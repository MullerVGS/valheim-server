using System;
using System.Collections.Generic;
using System.Globalization;

namespace ValheimMetrics.Map
{
    // Texto que vai para os labels do mapa e aparece no clique do Grafana.
    public static class MapText
    {
        // Minimap.PinType, na ordem do enum do jogo.
        static readonly string[] PinKinds =
        {
            "Fogueira", "Casa", "Martelo", "Ponto", "Runa", "Morte", "Cama", "Grito", "Nenhum", "Chefe",
            "Jogador", "Evento", "Ping", "Área de evento", "Hildir", "Hildir", "Hildir",
        };

        public static string PinKind(int type) => type >= 0 && type < PinKinds.Length ? PinKinds[type] : "Outro";

        // "piece_chest_wood 40%, wood_wall 12%": as n maiores partes do total, em ordem.
        public static string TopShares(IEnumerable<KeyValuePair<string, long>> parts, int n)
        {
            var list = new List<KeyValuePair<string, long>>(parts);
            long total = 0;
            foreach (var kv in list)
                total += kv.Value;
            if (total <= 0)
                return "";
            list.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
            var shown = new List<string>();
            for (int i = 0; i < list.Count && i < n; i++)
                shown.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1:0}%", list[i].Key, 100.0 * list[i].Value / total));
            return string.Join(", ", shown);
        }

        public static string Distance(double x0, double z0, double x1, double z1) =>
            Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0)).ToString("F0", CultureInfo.InvariantCulture);
    }
}
