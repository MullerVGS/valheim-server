using System.Collections.Generic;

namespace ValheimMetrics.Signs
{
    // Catalogo gerado por tools/sign-icons a partir do jogo de quem hospeda. Uma entrada por linha,
    // campos separados por TAB:
    //   I <id> <rich text, com quebra de linha escrita como \n>
    //   A <apelido> <id>
    //   D <id do icone padrao>
    public sealed class SignIconCatalog
    {
        readonly Dictionary<string, string> _texts = new Dictionary<string, string>();
        readonly Dictionary<string, string> _aliases = new Dictionary<string, string>();

        public int Icons => _texts.Count;
        public int Aliases => _aliases.Count;
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
                else if (fields[0] == "A" && fields.Length == 3)
                    aliases.Add(new KeyValuePair<string, string>(fields[1], fields[2]));
                else if (fields[0] == "D" && fields.Length == 2)
                    defaultIcon = fields[1];
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
    }
}
