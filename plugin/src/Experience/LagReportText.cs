using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimMetrics.Experience
{
    // Reclamacao de lag digitada no chat do jogo. Palavra solta, sem comando: e o que o jogador ja escreve.
    public static class LagReportText
    {
        static readonly string[] Prefixes = { "lag", "trav" };

        public static bool IsReport(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            foreach (var word in Words(text))
            {
                foreach (var prefix in Prefixes)
                {
                    if (word.StartsWith(prefix, StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        static IEnumerable<string> Words(string text)
        {
            var folded = Fold(text);
            var sb = new StringBuilder();
            foreach (var c in folded)
            {
                if (char.IsLetter(c))
                {
                    sb.Append(c);
                    continue;
                }
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
            }
            if (sb.Length > 0)
                yield return sb.ToString();
        }

        static string Fold(string text)
        {
            var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString();
        }
    }

    // O cliente manda a mesma fala uma vez por destinatario: sem isto, 4 jogadores viram 4 reclamacoes.
    public sealed class LagReportDedup
    {
        readonly double _windowSeconds;
        readonly Dictionary<string, double> _last = new Dictionary<string, double>();

        public LagReportDedup(double windowSeconds)
        {
            _windowSeconds = windowSeconds;
        }

        public bool Accept(string who, double now)
        {
            if (_last.TryGetValue(who, out var at) && now - at < _windowSeconds)
                return false;
            _last[who] = now;
            return true;
        }
    }
}
