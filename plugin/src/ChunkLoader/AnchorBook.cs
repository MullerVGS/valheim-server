using System;
using System.Collections.Generic;
using System.Text;

namespace ValheimMetrics.ChunkLoader
{
    // Persistent sign registry. Every sign is an anchor; the newest remains the primary
    // reference point for compatibility with single-center metrics.
    public sealed class AnchorBook<TId>
    {
        public const string Variable = "VALHEIM_CHUNK_LOADER";
        const string Marker = "chunkloader";

        readonly Dictionary<TId, long> _stamps = new Dictionary<TId, long>();

        public int Count => _stamps.Count;
        public IEnumerable<TId> Ids => _stamps.Keys;

        // "Chunk Loader", "[chunk-loader]" e "<b>chunkloader</b>" valem: so letras e digitos contam.
        public static bool IsMarker(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 200)
                return false;
            var sb = new StringBuilder(Marker.Length);
            bool inTag = false;
            foreach (char c in text)
            {
                if (c == '<')
                    inTag = true;
                else if (c == '>')
                    inTag = false;
                else if (!inTag && char.IsLetterOrDigit(c))
                {
                    if (sb.Length == Marker.Length)
                        return false;
                    sb.Append(char.ToLowerInvariant(c));
                }
            }
            return sb.ToString() == Marker;
        }

        // Devolve o carimbo que a placa deve guardar (0 = nenhum). O que ja conhecemos vale mais que o
        // do ZDO: cliente que nao recebeu o carimbo ainda manda a placa sem ele.
        public long Observe(TId id, string text, long stored, long now)
        {
            if (!IsMarker(text))
            {
                _stamps.Remove(id);
                return 0;
            }
            if (!_stamps.TryGetValue(id, out var stamp))
            {
                stamp = stored > 0 ? stored : now;
                _stamps[id] = stamp;
            }
            return stamp;
        }

        public void Forget(TId id) => _stamps.Remove(id);

        // Underline every active sign.
        // null = o texto ja esta certo.
        public static string Mark(string text, bool active)
        {
            if (text == null)
                return null;
            bool underlined = text.StartsWith(UnderlineOpen, StringComparison.Ordinal)
                && text.EndsWith(UnderlineClose, StringComparison.Ordinal)
                && text.Length >= UnderlineOpen.Length + UnderlineClose.Length;
            if (active == underlined)
                return null;
            return active
                ? UnderlineOpen + text + UnderlineClose
                : text.Substring(UnderlineOpen.Length, text.Length - UnderlineOpen.Length - UnderlineClose.Length);
        }

        const string UnderlineOpen = "<u>";
        const string UnderlineClose = "</u>";

        public bool TryPick(out TId id)
        {
            id = default;
            long best = long.MinValue;
            bool found = false;
            foreach (var kv in _stamps)
            {
                if (kv.Value > best)
                {
                    best = kv.Value;
                    id = kv.Key;
                    found = true;
                }
            }
            return found;
        }
    }
}
