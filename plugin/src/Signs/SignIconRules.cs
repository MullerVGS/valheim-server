namespace ValheimMetrics.Signs
{
    public enum SignIconAction
    {
        None,
        // Jogador escreveu um codigo: troca pelo icone.
        Apply,
        // Alguem confirmou o texto cortado pelo campo do jogo: devolve o icone inteiro.
        Restore,
        // O catalogo mudou desde que a placa foi desenhada: redesenha.
        Refresh,
        // Jogador escreveu outra coisa por cima: a placa volta a ser dele.
        Release,
    }

    public struct SignIconDecision
    {
        public SignIconAction Action;
        public string Icon;
        public string Text;
        public bool UsedDefault;
    }

    // Decide o que fazer com uma placa a partir do texto atual e do icone que o servidor anotou nela.
    public static class SignIconRules
    {
        // Todo texto gerado comeca assim; em 50 caracteres so cabe o cabecalho, nunca uma linha de pixels.
        const string GeneratedPrefix = "<cspace=-";

        public static SignIconDecision Decide(SignIconCatalog catalog, string text, string storedIcon)
        {
            text = text ?? "";
            if (SignCode.TryParse(text, out var key))
            {
                var icon = catalog.Resolve(key);
                if (icon == null)
                    return default;
                return new SignIconDecision
                {
                    Action = SignIconAction.Apply,
                    Icon = icon,
                    Text = catalog.TextOf(icon),
                    UsedDefault = !catalog.Knows(key),
                };
            }

            if (string.IsNullOrEmpty(storedIcon))
                return default;

            var expected = catalog.TextOf(storedIcon);
            if (expected == null)
                return new SignIconDecision { Action = SignIconAction.Release };
            if (text == expected)
                return default;
            if (text.Length > SignCode.GameInputLimit)
                return new SignIconDecision { Action = SignIconAction.Refresh, Icon = storedIcon, Text = expected };
            if (SignCode.IsTruncationOf(text, expected) || text.StartsWith(GeneratedPrefix, System.StringComparison.Ordinal))
                return new SignIconDecision { Action = SignIconAction.Restore, Icon = storedIcon, Text = expected };
            return new SignIconDecision { Action = SignIconAction.Release };
        }
    }
}
