using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ValheimMetrics.Access
{
    public sealed class AccessEntry
    {
        public string SteamId;
        public string Name = "";
        public long LastSeen;
        public long LastDenied;
        public int Denied;
        // Pedido aberto: numero curto que o admin digita para liberar. 0 = sem pedido.
        public int Ticket;
    }

    // Quem ja tentou entrar, com nome e SteamID, e os pedidos em aberto de quem foi barrado pela
    // whitelist. Sem dependencia do jogo: o servidor chama Record a cada entrada e salva o texto.
    public sealed class AccessBook
    {
        // Cliente barrado costuma tentar de novo em seguida: um aviso por pessoa nessa janela.
        public const long NotifyGapSeconds = 60;

        readonly Dictionary<string, AccessEntry> _entries = new Dictionary<string, AccessEntry>();

        public IEnumerable<AccessEntry> All => _entries.Values;

        public AccessEntry Get(string steamId) =>
            steamId != null && _entries.TryGetValue(steamId, out var entry) ? entry : null;

        // Devolve true quando vale avisar os admins: barrado sem aviso recente.
        public bool Record(string steamId, string name, bool allowed, long now)
        {
            if (string.IsNullOrEmpty(steamId))
                return false;
            if (!_entries.TryGetValue(steamId, out var entry))
            {
                entry = new AccessEntry { SteamId = steamId };
                _entries[steamId] = entry;
            }
            if (!string.IsNullOrEmpty(name))
                entry.Name = name;
            entry.LastSeen = now;
            if (allowed)
            {
                entry.Ticket = 0;
                return false;
            }

            bool notify = entry.Ticket == 0 || now - entry.LastDenied >= NotifyGapSeconds;
            if (entry.Ticket == 0)
                entry.Ticket = NextTicket();
            entry.LastDenied = now;
            entry.Denied++;
            return notify;
        }

        public bool Close(string steamId)
        {
            var entry = Get(steamId);
            if (entry == null || entry.Ticket == 0)
                return false;
            entry.Ticket = 0;
            return true;
        }

        // Pedido some sozinho quando a pessoa entra na whitelist por fora (arquivo editado a mao).
        public List<AccessEntry> Pending(Func<string, bool> isPermitted) =>
            _entries.Values
                .Where(e => e.Ticket != 0 && !isPermitted(e.SteamId))
                .OrderBy(e => e.Ticket)
                .ToList();

        // Numero do pedido (com ou sem #), SteamID ou comeco do nome. Nome ambiguo nao resolve.
        public static AccessEntry Resolve(IList<AccessEntry> pending, string query)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0)
                return null;
            var number = query.TrimStart('#');
            if (int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var ticket))
            {
                var byTicket = pending.FirstOrDefault(e => e.Ticket == ticket);
                if (byTicket != null)
                    return byTicket;
            }
            var byId = pending.FirstOrDefault(e => e.SteamId == query);
            if (byId != null)
                return byId;
            var byName = pending.Where(e => e.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();
            return byName.Count == 1 ? byName[0] : null;
        }

        int NextTicket()
        {
            int max = 0;
            foreach (var e in _entries.Values)
                max = Math.Max(max, e.Ticket);
            return max + 1;
        }

        // Uma linha por pessoa: steamid, ultima vez, ultima barrada, barradas, pedido, nome (por ultimo:
        // nome pode ter qualquer coisa menos tab e quebra de linha, que o jogo nao deixa digitar).
        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append("# steam_id\tlast_seen\tlast_denied\tdenied\tticket\tname\n");
            foreach (var e in _entries.Values.OrderBy(e => e.SteamId, StringComparer.Ordinal))
            {
                sb.Append(e.SteamId).Append('\t')
                    .Append(e.LastSeen.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(e.LastDenied.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(e.Denied.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(e.Ticket.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(Clean(e.Name)).Append('\n');
            }
            return sb.ToString();
        }

        public static AccessBook Parse(string text)
        {
            var book = new AccessBook();
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#')
                    continue;
                var f = line.Split(new[] { '\t' }, 6);
                if (f.Length < 6 || f[0].Length == 0)
                    continue;
                if (!long.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seen)
                    || !long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var denied)
                    || !int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                    || !int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticket))
                    continue;
                book._entries[f[0]] = new AccessEntry
                {
                    SteamId = f[0], LastSeen = seen, LastDenied = denied, Denied = count, Ticket = ticket, Name = f[5],
                };
            }
            return book;
        }

        static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }
}
