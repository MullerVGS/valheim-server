namespace ValheimMetrics.Signs
{
    public enum SignTextAction
    {
        None,
        // Jogador escreveu abreviacao ou continuacao: o servidor grava o texto por extenso.
        Write,
        // Alguem confirmou o texto cortado pelo campo do jogo: devolve o texto inteiro.
        Restore,
        // Uma abreviacao mudou no catalogo desde que a placa foi escrita: reescreve.
        Refresh,
        // Jogador escreveu outra coisa por cima: a placa volta a ser dele.
        Release,
    }

    public struct SignTextDecision
    {
        public SignTextAction Action;
        // O que o jogador escreveu, antes de expandir. Null = a placa voltou a ser texto comum.
        public string Source;
        public string Text;
    }

    // O campo do jogo corta em 50 caracteres e isso nao muda sem mod no cliente. O que o servidor
    // pode fazer e aceitar texto curto e gravar texto longo:
    //   {u}<#f60>...   abreviacao do catalogo, expandida aqui;
    //   >>resto        continuacao: emenda no que a placa ja tinha, quantas vezes precisar.
    public static class SignTextRules
    {
        public const string Continuation = ">>";

        // previous = ultimo texto que o servidor viu nessa placa antes desta escrita (null se nao sabe).
        public static SignTextDecision Decide(SignIconCatalog catalog, string text, string storedSource, string previous)
        {
            text = text ?? "";
            bool typed = text.Length <= SignCode.GameInputLimit;

            if (typed && text.StartsWith(Continuation, System.StringComparison.Ordinal))
            {
                var rest = text;
                while (rest.StartsWith(Continuation, System.StringComparison.Ordinal))
                    rest = rest.Substring(Continuation.Length);
                return Write(catalog, BaseOf(storedSource, previous) + rest);
            }
            if (typed && catalog.HasMacro(text))
                return Write(catalog, text);

            if (string.IsNullOrEmpty(storedSource))
                return default;

            var expected = catalog.Expand(storedSource);
            if (text == expected)
                return default;
            if (!typed)
                return new SignTextDecision { Action = SignTextAction.Refresh, Source = storedSource, Text = expected };
            if (SignCode.IsTruncationOf(text, expected))
                return new SignTextDecision { Action = SignTextAction.Restore, Source = storedSource, Text = expected };
            return new SignTextDecision { Action = SignTextAction.Release };
        }

        // Emenda na fonte guardada; sem ela, no texto comum que o jogador tinha digitado. Texto
        // longo sem fonte e desenho do servidor (icone): nao serve de base.
        static string BaseOf(string storedSource, string previous)
        {
            if (!string.IsNullOrEmpty(storedSource))
                return storedSource;
            if (previous != null && previous.Length <= SignCode.GameInputLimit
                && !previous.StartsWith(Continuation, System.StringComparison.Ordinal))
                return previous;
            return "";
        }

        static SignTextDecision Write(SignIconCatalog catalog, string source)
        {
            var text = catalog.Expand(source);
            bool plain = text == source && text.Length <= SignCode.GameInputLimit;
            return new SignTextDecision { Action = SignTextAction.Write, Source = plain ? null : source, Text = text };
        }
    }
}
