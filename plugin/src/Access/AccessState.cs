using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimMetrics.Access
{
    // O JSON que a pagina le: pedidos abertos e a whitelist com o nome que cada SteamID usou por ultimo.
    public static class AccessState
    {
        public static string ToJson(IList<AccessEntry> pending, IList<string> permitted, AccessBook book, long now)
        {
            var sb = new StringBuilder();
            sb.Append("{\"now\":").Append(Num(now)).Append(",\"pending\":[");
            for (int i = 0; i < pending.Count; i++)
            {
                var e = pending[i];
                if (i > 0)
                    sb.Append(',');
                sb.Append("{\"ticket\":").Append(Num(e.Ticket))
                    .Append(",\"steamId\":").Append(Str(e.SteamId))
                    .Append(",\"name\":").Append(Str(e.Name))
                    .Append(",\"lastDenied\":").Append(Num(e.LastDenied))
                    .Append(",\"denied\":").Append(Num(e.Denied))
                    .Append('}');
            }
            sb.Append("],\"permitted\":[");
            for (int i = 0; i < permitted.Count; i++)
            {
                var entry = book.Get(permitted[i]);
                if (i > 0)
                    sb.Append(',');
                sb.Append("{\"steamId\":").Append(Str(permitted[i]))
                    .Append(",\"name\":").Append(Str(entry?.Name ?? ""))
                    .Append(",\"lastSeen\":").Append(Num(entry?.LastSeen ?? 0))
                    .Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);

        public static string Str(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    // < e > escapados: o JSON nunca vira tag se alguem o colar numa pagina.
                    case '<': sb.Append("\\u003c"); break;
                    case '>': sb.Append("\\u003e"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
