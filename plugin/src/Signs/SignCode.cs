using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ValheimMetrics.Signs
{
    // O texto que acompanha o icone. A mostra, vai escrito na tabua em cima do desenho; escondido,
    // so aparece para quem mira a placa (o hover do jogo mostra o texto sem as tags).
    public struct SignLabel
    {
        public string Text;
        public bool Shown;

        // Como fica anotado na placa: "=" a mostra, "~" escondido.
        public string Encode()
        {
            return (Shown ? "=" : "~") + (Text ?? "");
        }

        public static SignLabel? Decode(string stored)
        {
            if (string.IsNullOrEmpty(stored) || (stored[0] != '=' && stored[0] != '~'))
                return null;
            return new SignLabel { Text = stored.Substring(1), Shown = stored[0] == '=' };
        }
    }

    // O jogador pede um icone escrevendo :nome: na placa, sozinho ou na ponta de um rotulo
    // ("Madeira :wood:").
    public static class SignCode
    {
        // O campo do jogo corta em 50 caracteres: texto maior que isso so o servidor escreve.
        public const int GameInputLimit = 50;

        const string Accented = "áàâãäåéèêëíìîïóòôõöúùûüçñ";
        const string Plain = "aaaaaaeeeeiiiiooooouuuucn";

        public static bool TryParse(string text, out string key)
        {
            key = null;
            if (text == null)
                return false;
            var trimmed = text.Trim();
            if (trimmed.Length < 3 || trimmed[0] != ':' || trimmed[trimmed.Length - 1] != ':')
                return false;
            var inner = trimmed.Substring(1, trimmed.Length - 2);
            if (inner.IndexOfAny(new[] { ':', '<', '>', '\n' }) >= 0)
                return false;
            key = Normalize(inner);
            return key.Length > 0;
        }

        static readonly Regex SizeAtEnd = new Regex(@"<size=([0-9]+(?:\.[0-9]+)?)>\s*$", RegexOptions.CultureInvariant);
        static readonly Regex SizeAtStart = new Regex(@"^\s*<size=([0-9]+(?:\.[0-9]+)?)>", RegexOptions.CultureInvariant);

        public static bool TryParse(string text, out string key, out SignLabel label)
        {
            return TryParse(text, out key, out label, out _);
        }

        // ":wood:" sozinho, "Madeira :wood:" ou ":wood: Madeira". Sozinho, o rotulo e o que foi
        // digitado dentro do codigo, escondido. Um <size=N> colado antes do codigo e o lado do icone,
        // em unidades da tabua ("<size=12>:wood:", "Madeira <size=12>:wood:").
        public static bool TryParse(string text, out string key, out SignLabel label, out double? size)
        {
            key = null;
            label = default;
            size = null;
            if (text == null)
                return false;
            var trimmed = text.Trim();
            if (trimmed.Length < 3 || trimmed.IndexOf('\n') >= 0)
                return false;

            string inner, rest;
            Match sized;
            if (trimmed[trimmed.Length - 1] == ':')
            {
                int open = trimmed.LastIndexOf(':', trimmed.Length - 2);
                if (open < 0)
                    return false;
                inner = trimmed.Substring(open + 1, trimmed.Length - open - 2);
                rest = trimmed.Substring(0, open);
                sized = SizeAtEnd.Match(rest);
                if (sized.Success)
                    rest = rest.Substring(0, sized.Index);
            }
            else
            {
                sized = SizeAtStart.Match(trimmed);
                var body = sized.Success ? trimmed.Substring(sized.Length).TrimStart() : trimmed;
                if (body.Length < 3 || body[0] != ':')
                    return false;
                int close = body.IndexOf(':', 1);
                if (close < 0)
                    return false;
                inner = body.Substring(1, close - 1);
                rest = body.Substring(close + 1);
            }

            if (inner.IndexOfAny(new[] { '<', '>' }) >= 0)
                return false;
            key = Normalize(inner);
            if (key.Length == 0)
                return false;
            if (sized.Success)
                size = double.Parse(sized.Groups[1].Value, CultureInfo.InvariantCulture);
            rest = rest.Trim();
            label = rest.Length == 0
                ? new SignLabel { Text = inner.Trim(), Shown = false }
                : new SignLabel { Text = rest, Shown = true };
            return true;
        }

        // Mesma regra do gerador do catalogo: minusculas, sem acento, so letras e digitos.
        // ":Yellow Mushroom:" e ":yellow_mushroom:" viram yellowmushroom (nome traduzido);
        // ":MushroomYellow:" vira mushroomyellow (prefab). O catalogo traz as duas chaves.
        public static string Normalize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var raw in name)
            {
                var c = char.ToLowerInvariant(raw);
                int i = Accented.IndexOf(c);
                if (i >= 0)
                    c = Plain[i];
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                    sb.Append(c);
            }
            return sb.ToString();
        }

        // Quem aperta E numa placa de icone recebe o texto cortado no campo; se confirmar, e isso
        // que volta para o servidor.
        public static bool IsTruncationOf(string text, string full)
        {
            return !string.IsNullOrEmpty(text) && text.Length <= GameInputLimit && full != null
                && full.Length > text.Length && full.StartsWith(text, System.StringComparison.Ordinal);
        }
    }
}
