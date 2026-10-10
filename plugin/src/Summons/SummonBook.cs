using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ValheimMetrics.Summons
{
    // Os nomes que cada jogador deu as suas invocacoes, em ordem, e quais invocacoes vivas usam cada um.
    // Invocacao nova cujo nome nao esta na lista dele recebe o primeiro nome livre (o de quem sumiu);
    // renomear no jogo troca o nome antigo pelo novo na lista. So segura o nome a invocacao que esta
    // com o jogador: a que ficou para tras (portal, logout) segue no mundo e nao prende o nome dela.
    // Sem dependencia do jogo: o servidor chama Observe a cada ZDO que chega e salva o texto.
    public sealed class SummonBook<TId>
    {
        public const string Variable = "VALHEIM_SUMMON_NAMES";
        public const int MaxNamesPerPlayer = 30;
        public const int MaxNameLength = 64;

        sealed class Live
        {
            public string Player;
            public string Name;
            // Nome sorteado que trocamos: se voltar antes do dono aceitar o nosso, regrava em vez de
            // contar como renomeacao.
            public string Replaced;
        }

        readonly Dictionary<string, List<string>> _rosters = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        readonly Dictionary<TId, Live> _live = new Dictionary<TId, Live>();

        public int LiveCount => _live.Count;
        public int PlayerCount => _rosters.Count;

        public IReadOnlyList<string> Roster(string player) =>
            _rosters.TryGetValue(player, out var names) ? names : (IReadOnlyList<string>)Array.Empty<string>();

        public string PlayerOf(TId id) => _live.TryGetValue(id, out var live) ? live.Player : null;

        // `player` vazio = ainda nao se sabe de quem e (ja conhecido, segue com quem era).
        // Devolve o nome a gravar no ZDO, ou null para deixar como esta. `changed` diz se a lista mudou.
        public string Observe(TId id, string player, string name, Func<TId, bool> exists, Func<TId, bool> present, out bool changed)
        {
            changed = false;
            name = Clean(name);
            if (_live.TryGetValue(id, out var live))
            {
                if (name.Length == 0)
                    return null;
                if (name == live.Name)
                {
                    live.Replaced = null;
                    return null;
                }
                if (name == live.Replaced)
                    return live.Name;
                changed = Rename(live.Player, live.Name, name);
                live.Name = name;
                live.Replaced = null;
                return null;
            }

            if (string.IsNullOrEmpty(player))
                return null;
            Prune(exists);
            var roster = RosterFor(player);
            var used = new HashSet<string>(_live.Where(l => l.Value.Player == player && present(l.Key)).Select(l => l.Value.Name), StringComparer.Ordinal);
            string chosen = null;
            if (!roster.Contains(name) || used.Contains(name))
                chosen = roster.FirstOrDefault(n => !used.Contains(n));
            _live[id] = new Live { Player = player, Name = chosen ?? name, Replaced = chosen == null ? null : name };
            return chosen;
        }

        // Ja existia antes do plugin olhar (boot): conta como viva sem mexer no nome.
        public void Adopt(TId id, string player, string name)
        {
            if (string.IsNullOrEmpty(player) || _live.ContainsKey(id))
                return;
            _live[id] = new Live { Player = player, Name = Clean(name) };
        }

        public void Forget(TId id) => _live.Remove(id);

        void Prune(Func<TId, bool> exists)
        {
            foreach (var id in _live.Keys.Where(k => !exists(k)).ToList())
                _live.Remove(id);
        }

        List<string> RosterFor(string player)
        {
            if (!_rosters.TryGetValue(player, out var names))
                _rosters[player] = names = new List<string>();
            return names;
        }

        bool Rename(string player, string before, string after)
        {
            var roster = RosterFor(player);
            int at = roster.IndexOf(before);
            int dup = roster.IndexOf(after);
            if (at >= 0 && dup >= 0)
            {
                roster.RemoveAt(at);
                return true;
            }
            if (at >= 0)
            {
                roster[at] = after;
                return true;
            }
            if (dup >= 0)
                return false;
            roster.Add(after);
            if (roster.Count > MaxNamesPerPlayer)
                roster.RemoveAt(0);
            return true;
        }

        static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "";
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                if (!char.IsControl(c))
                    sb.Append(c);
            var s = sb.ToString().Trim();
            return s.Length > MaxNameLength ? s.Substring(0, MaxNameLength) : s;
        }

        // Uma linha por jogador: jogador TAB nome TAB nome...
        public string Serialize()
        {
            var sb = new StringBuilder();
            foreach (var kv in _rosters.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (kv.Value.Count == 0)
                    continue;
                sb.Append(kv.Key);
                foreach (var n in kv.Value)
                    sb.Append('\t').Append(n);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        public static SummonBook<TId> Parse(string text)
        {
            var book = new SummonBook<TId>();
            foreach (var line in (text ?? "").Split('\n'))
            {
                var parts = line.TrimEnd('\r').Split('\t');
                var player = parts[0].Trim();
                if (player.Length == 0)
                    continue;
                var roster = book.RosterFor(player);
                foreach (var raw in parts.Skip(1))
                {
                    var n = Clean(raw);
                    if (n.Length > 0 && !roster.Contains(n) && roster.Count < MaxNamesPerPlayer)
                        roster.Add(n);
                }
            }
            return book;
        }
    }
}
