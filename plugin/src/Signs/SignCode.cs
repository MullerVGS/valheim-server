using System.Text;

namespace ValheimMetrics.Signs
{
    // O jogador pede um icone escrevendo na placa o texto inteiro no formato :nome:.
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
