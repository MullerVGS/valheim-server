using System.Collections.Generic;
using System.Text;

namespace ValheimMetrics.Signs
{
    // Catalogo gerado por tools/sign-icons a partir do jogo de quem hospeda. Uma entrada por linha,
    // campos separados por TAB:
    //   I <id> <rich text, com quebra de linha escrita como \n; {label} = nome escondido, so para o hover>
    //   T <id> <o mesmo desenho, menor, com {label} a mostra em cima e {ls} = tamanho do rotulo>
    //   A <apelido> <id>
    //   D <id do icone padrao>
    //   M <abreviacao> <rich text que entra no lugar de {abreviacao}>
    public sealed class SignIconCatalog
    {
        readonly Dictionary<string, string> _texts = new Dictionary<string, string>();
        readonly Dictionary<string, string> _titled = new Dictionary<string, string>();
        readonly Dictionary<string, string> _aliases = new Dictionary<string, string>();
        readonly Dictionary<string, string> _macros = new Dictionary<string, string>();

        public int Icons => _texts.Count;
        public int Aliases => _aliases.Count;
        public int Macros => _macros.Count;
        public string DefaultIcon { get; private set; }

        public static SignIconCatalog Parse(IEnumerable<string> lines, List<string> warnings)
        {
            var catalog = new SignIconCatalog();
            var aliases = new List<KeyValuePair<string, string>>();
            string defaultIcon = null;
            int number = 0;
            foreach (var line in lines)
            {
                number++;
                if (line.Length == 0 || line[0] == '#')
                    continue;
                var fields = line.Split('\t');
                if (fields[0] == "I" && fields.Length == 3 && fields[1].Length > 0 && fields[2].Length > 0)
                    catalog._texts[fields[1]] = fields[2].Replace("\\n", "\n");
                else if (fields[0] == "T" && fields.Length == 3 && fields[1].Length > 0 && fields[2].Length > 0)
                    catalog._titled[fields[1]] = fields[2].Replace("\\n", "\n");
                else if (fields[0] == "A" && fields.Length == 3)
                    aliases.Add(new KeyValuePair<string, string>(fields[1], fields[2]));
                else if (fields[0] == "D" && fields.Length == 2)
                    defaultIcon = fields[1];
                else if (fields[0] == "M" && fields.Length == 3)
                {
                    var name = fields[1].ToLowerInvariant();
                    if (IsMacroName(name) && fields[2].Length > 0)
                        catalog._macros[name] = fields[2].Replace("\\n", "\n");
                    else
                        warnings.Add($"linha {number} ignorada: abreviacao pede nome de letras e digitos e um texto.");
                }
                else
                    warnings.Add($"linha {number} ignorada: formato desconhecido.");
            }

            // Apelido nunca encobre um icone de mesmo nome, e so vale se o alvo existe.
            foreach (var alias in aliases)
            {
                if (catalog._texts.ContainsKey(alias.Key))
                    continue;
                if (catalog._texts.ContainsKey(alias.Value))
                    catalog._aliases[alias.Key] = alias.Value;
                else
                    warnings.Add($"apelido {alias.Key} aponta para icone ausente {alias.Value}.");
            }

            if (defaultIcon != null && catalog._texts.ContainsKey(defaultIcon))
                catalog.DefaultIcon = defaultIcon;
            else if (defaultIcon != null)
                warnings.Add($"icone padrao {defaultIcon} nao esta no catalogo.");
            return catalog;
        }

        // Chave desconhecida cai no icone padrao; sem padrao, null (a placa fica como o jogador escreveu).
        public string Resolve(string key)
        {
            if (_texts.ContainsKey(key))
                return key;
            if (_aliases.TryGetValue(key, out var target))
                return target;
            return DefaultIcon;
        }

        public bool Knows(string key)
        {
            return _texts.ContainsKey(key) || _aliases.ContainsKey(key);
        }

        public string TextOf(string icon)
        {
            return icon != null && _texts.TryGetValue(icon, out var text) ? text : null;
        }

        // Rotulo curto cabe no tamanho grande; ate LongestShownLabel, no pequeno. Sao inteiros de
        // proposito: o hover do jogo so tira tag sem ponto, e o rotulo tem que sair legivel la.
        const int LargeLabelLength = 10;

        // O texto que vai para a placa: o desenho do icone com o rotulo no lugar.
        public string Compose(string icon, SignLabel label)
        {
            var plain = TextOf(icon);
            if (plain == null)
                return null;
            var template = label.Shown && _titled.TryGetValue(icon, out var titled) ? titled : plain;
            var text = label.Text ?? "";
            return template
                .Replace("{ls}", text.Length <= LargeLabelLength ? "2" : "1")
                .Replace("{label}", text);
        }

        public bool HasMacro(string text)
        {
            return text != null && text.IndexOf('{') >= 0 && Expand(text) != text;
        }

        // Troca cada {abreviacao} conhecida pelo texto dela, numa passada so. Chave desconhecida ou
        // sem fechar fica como o jogador escreveu.
        public string Expand(string text)
        {
            if (string.IsNullOrEmpty(text) || _macros.Count == 0 || text.IndexOf('{') < 0)
                return text;
            var sb = new StringBuilder(text.Length + 64);
            int i = 0;
            while (i < text.Length)
            {
                int close = text[i] == '{' ? text.IndexOf('}', i + 1) : -1;
                if (close > i && _macros.TryGetValue(text.Substring(i + 1, close - i - 1).ToLowerInvariant(), out var expansion))
                {
                    sb.Append(expansion);
                    i = close + 1;
                }
                else
                {
                    sb.Append(text[i]);
                    i++;
                }
            }
            return sb.ToString();
        }

        static bool IsMacroName(string name)
        {
            if (name.Length == 0)
                return false;
            foreach (var c in name)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
                    return false;
            return true;
        }
    }
}
