using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ValheimMetrics.Signs
{
    // Catalogo gerado por tools/sign-icons a partir do jogo de quem hospeda. Uma entrada por linha,
    // campos separados por TAB:
    //   I <id> <rich text, com quebra de linha escrita como \n; {label} = nome escondido, so para o hover>
    //   T <id> <o mesmo desenho, menor, com {label} a mostra em cima e {ls} = tamanho do rotulo>
    //   A <apelido> <id>
    //   D <id do icone padrao>
    //   M <abreviacao> <rich text que entra no lugar de {abreviacao}>
    //   P <parametro> <numero>   (units, bold_fit, bold_overflow: ver Resize; brightness: ver Dim)
    public sealed class SignIconCatalog
    {
        readonly Dictionary<string, string> _texts = new Dictionary<string, string>();
        readonly Dictionary<string, string> _titled = new Dictionary<string, string>();
        readonly Dictionary<string, string> _aliases = new Dictionary<string, string>();
        readonly Dictionary<string, string> _macros = new Dictionary<string, string>();

        readonly Dictionary<string, double> _parameters = new Dictionary<string, double>();

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
                else if (fields[0] == "P" && fields.Length == 3
                    && double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var parameter))
                    catalog._parameters[fields[1]] = parameter;
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

        // Rotulo curto cabe no tamanho grande; ate LongestShownLabel, no pequeno; maior que isso fica
        // so no hover. Os tamanhos sao inteiros de proposito: o hover do jogo so tira tag sem ponto, e
        // o rotulo tem que sair legivel la.
        const int LargeLabelLength = 10;
        const int LongestShownLabel = 22;

        // Maior lado de icone que nao quebra linha: a fileira mais larga tem que caber na area de
        // texto da placa (18,29 unidades).
        const double LargestSize = 18;

        static readonly Regex Tag = new Regex(@"<[^<>]*>", RegexOptions.CultureInvariant);
        // No rotulo so passa tag que nao mexe na altura nem na largura da linha: o desenho depende
        // de tudo caber na tabua (ver Resize).
        static readonly Regex LabelTag = new Regex(@"^</?(?:#|b>|i>|u>|s>|color\b|alpha\b|material\b|mark\b)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        static readonly Regex Header = new Regex(
            @"<cspace=-(?<a>[0-9.]+)>(?<m><material=[^<>]*>)?<line-height=(?<p>[0-9.]+)><size=(?<s>[0-9.]+)>",
            RegexOptions.CultureInvariant);
        static readonly Regex Blocks = new Regex(@"^(?:█|\n|<#[0-9a-fA-F]{3,8}>)*$", RegexOptions.CultureInvariant);
        const string HoverReset = "<size=100.0%><cspace=0.0><line-height=100.0%>";

        static readonly Regex ColorTag = new Regex(@"<#(?<hex>[0-9a-fA-F]{3,8})>", RegexOptions.CultureInvariant);

        // O texto que vai para a placa: o desenho do icone com o rotulo no lugar. size = lado do
        // icone em unidades da tabua; brightness = porcentagem do brilho padrao; null = o do catalogo.
        public string Compose(string icon, SignLabel label, double? size = null, double? brightness = null)
        {
            var plain = TextOf(icon);
            if (plain == null)
                return null;
            var text = Tag.Replace(Expand(label.Text ?? ""), m => LabelTag.IsMatch(m.Value) ? m.Value : "");
            int visible = Tag.Replace(text, "").Length;
            var template = label.Shown && visible <= LongestShownLabel && _titled.TryGetValue(icon, out var titled)
                ? titled
                : plain;
            if (size.HasValue)
                template = Resize(template, plain, size.Value);
            template = Dim(template, brightness);
            return template
                .Replace("{ls}", visible <= LargeLabelLength ? "2" : "1")
                .Replace("{label}", text);
        }

        // O catalogo traz o desenho num tamanho so; outro tamanho e o mesmo desenho com os tres
        // numeros do cabecalho refeitos. O extra do Bold por caractere depende do auto-size da placa:
        // 0,24 quando tudo cabe na tabua (fecha em 8), 0,03 quando nao cabe (cai para 1).
        string Resize(string template, string plain, double size)
        {
            var reference = Header.Match(plain);
            var header = Header.Match(template);
            if (!reference.Success || !header.Success)
                return template;
            var drawing = template.Substring(header.Index + header.Length);
            if (drawing.EndsWith(HoverReset, System.StringComparison.Ordinal))
                drawing = drawing.Substring(0, drawing.Length - HoverReset.Length);
            if (!Blocks.IsMatch(drawing))
                return template;

            double units = Parameter("units", 7.6);
            size = System.Math.Max(1, System.Math.Min(LargestSize, size));
            double pixel = Number(reference.Groups["p"]) * size / units;
            double fitting = Number(header.Groups["p"]);
            double overlap = Number(header.Groups["s"]) / fitting - 1;
            double bold = pixel <= fitting + 1e-6 ? Parameter("bold_fit", 0.24) : Parameter("bold_overflow", 0.03);
            var resized = "<cspace=-" + Format(bold + overlap * pixel) + ">" + header.Groups["m"].Value
                + "<line-height=" + Format(pixel) + "><size=" + Format(pixel * (1 + overlap)) + ">";
            return template.Substring(0, header.Index) + resized + template.Substring(header.Index + header.Length);
        }

        // O desenho e sem iluminacao: a cor escrita e o brilho que se ve. Catalogo com `P brightness`
        // traz as cores cheias e o brilho padrao a aplicar; sem ele (catalogo antigo) as cores ja vem
        // escurecidas. A porcentagem do jogador e sobre o padrao, e nada passa da cor cheia. So o
        // desenho muda: a cor do rotulo e de quem escreveu.
        string Dim(string template, double? percent)
        {
            double standard = Parameter("brightness", 1);
            double factor = System.Math.Min(1, standard * System.Math.Max(5, percent ?? 100) / 100);
            if (System.Math.Abs(factor - 1) < 1e-9)
                return template;
            int label = template.IndexOf("{label}", System.StringComparison.Ordinal);
            int start = label < 0 ? 0 : template.IndexOf('\n', label) + 1;
            return template.Substring(0, start) + ColorTag.Replace(template.Substring(start), m => Scale(m, factor));
        }

        static string Scale(Match tag, double factor)
        {
            var hex = tag.Groups["hex"].Value;
            if (hex.Length != 3 && hex.Length != 4 && hex.Length != 6 && hex.Length != 8)
                return tag.Value;
            int width = hex.Length <= 4 ? 1 : 2;
            int full = width == 1 ? 15 : 255;
            var sb = new StringBuilder("<#", hex.Length + 3);
            for (int channel = 0; channel * width < hex.Length; channel++)
            {
                var digits = hex.Substring(channel * width, width);
                if (channel < 3)
                {
                    int value = int.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    int scaled = (int)System.Math.Round(value * factor, System.MidpointRounding.AwayFromZero);
                    digits = System.Math.Min(full, scaled).ToString(width == 1 ? "x1" : "x2", CultureInfo.InvariantCulture);
                }
                sb.Append(digits);
            }
            return sb.Append('>').ToString();
        }

        double Parameter(string name, double fallback)
        {
            return _parameters.TryGetValue(name, out var value) ? value : fallback;
        }

        static double Number(Group group)
        {
            return double.Parse(group.Value, CultureInfo.InvariantCulture);
        }

        // Sempre com ponto: e o que faz a tag sobreviver no hover do jogo.
        static string Format(double value)
        {
            return value.ToString("0.0##", CultureInfo.InvariantCulture);
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
