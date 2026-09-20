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
        // Rotulo como fica anotado na placa (SignLabel.Encode).
        public string Label;
        public string Text;
        public bool UsedDefault;
    }

    // Decide o que fazer com uma placa a partir do texto atual e do icone que o servidor anotou nela.
    public static class SignIconRules
    {
        // Todo texto gerado comeca assim; em 50 caracteres so cabe o cabecalho, nunca uma linha de pixels.
        const string GeneratedPrefix = "<cspace=-";

        public static SignIconDecision Decide(SignIconCatalog catalog, string text, string storedIcon, string storedLabel = null)
        {
            text = text ?? "";
            if (SignCode.TryParse(text, out var key, out var label))
            {
                var icon = catalog.Resolve(key);
                if (icon == null)
                    return default;
                return new SignIconDecision
                {
                    Action = SignIconAction.Apply,
                    Icon = icon,
                    Label = label.Encode(),
                    Text = catalog.Compose(icon, label),
                    UsedDefault = !catalog.Knows(key),
                };
            }

            if (string.IsNullOrEmpty(storedIcon))
                return default;

            // Placa desenhada antes de existir rotulo: o id do icone serve de nome escondido.
            var stored = SignLabel.Decode(storedLabel) ?? new SignLabel { Text = storedIcon, Shown = false };
            var expected = catalog.Compose(storedIcon, stored);
            if (expected == null)
                return new SignIconDecision { Action = SignIconAction.Release };
            if (text == expected)
                return default;
            var redraw = new SignIconDecision { Icon = storedIcon, Label = stored.Encode(), Text = expected };
            if (text.Length > SignCode.GameInputLimit)
                redraw.Action = SignIconAction.Refresh;
            else if (SignCode.IsTruncationOf(text, expected) || text.StartsWith(GeneratedPrefix, System.StringComparison.Ordinal))
                redraw.Action = SignIconAction.Restore;
            else
                return new SignIconDecision { Action = SignIconAction.Release };
            return redraw;
        }
    }
}
