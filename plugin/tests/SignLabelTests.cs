using System.Collections.Generic;
using ValheimMetrics.Signs;
using Xunit;

public class SignLabelTests
{
    const string Plain = "<cspace=-0.0><size=1><line-height=0><#0000>{label}\n<cspace=-0.287><size=0.364><#a63>███<size=100.0%>";
    const string Titled = "<cspace=-0.0><size={ls}><line-height=1>{label}\n<cspace=-0.27><size=0.3><#a63>███<size=100.0%>";

    static SignIconCatalog Catalog() =>
        SignIconCatalog.Parse(new[]
        {
            "D\tweed",
            "I\twood\t" + Plain.Replace("\n", "\\n"),
            "T\twood\t" + Titled.Replace("\n", "\\n"),
            "I\tweed\t" + Plain.Replace("\n", "\\n").Replace("a63", "3a3"),
            "A\tmadeira\twood",
        }, new List<string>());

    [Theory]
    [InlineData("Madeira :wood:", "wood", "Madeira")]
    [InlineData("  Madeira   :Wood:  ", "wood", "Madeira")]
    [InlineData(":wood: Madeira", "wood", "Madeira")]
    [InlineData("Bau 2: lenha :fine wood:", "finewood", "Bau 2: lenha")]
    public void Texto_com_codigo_na_ponta_vira_rotulo_e_icone(string text, string key, string label)
    {
        Assert.True(SignCode.TryParse(text, out var parsedKey, out var parsedLabel));
        Assert.Equal(key, parsedKey);
        Assert.Equal(label, parsedLabel.Text);
        Assert.True(parsedLabel.Shown);
    }

    [Fact]
    public void Codigo_sozinho_guarda_o_que_foi_digitado_como_nome_escondido()
    {
        Assert.True(SignCode.TryParse(" :Yellow Mushroom: ", out var key, out var label));
        Assert.Equal("yellowmushroom", key);
        Assert.Equal("Yellow Mushroom", label.Text);
        Assert.False(label.Shown);
    }

    [Fact]
    public void Rotulo_comprido_demais_para_a_tabua_fica_so_no_hover()
    {
        Assert.True(SignCode.TryParse("Tudo que sobrou da ultima raid :wood:", out _, out var label));
        Assert.False(label.Shown);
        Assert.Equal("Tudo que sobrou da ultima raid", label.Text);
    }

    [Theory]
    [InlineData("Madeira")]
    [InlineData("Madeira :wood: e pedra")]
    [InlineData("Madeira\n:wood:")]
    [InlineData("a :<b>: ")]
    [InlineData("12:30")]
    public void Texto_sem_codigo_na_ponta_nao_e_pedido(string text)
    {
        Assert.False(SignCode.TryParse(text, out _, out _));
    }

    [Fact]
    public void Rotulo_vai_e_volta_da_anotacao_da_placa()
    {
        var shown = SignLabel.Decode(new SignLabel { Text = "Madeira", Shown = true }.Encode());
        var hidden = SignLabel.Decode(new SignLabel { Text = "wood", Shown = false }.Encode());

        Assert.True(shown.Value.Shown);
        Assert.Equal("Madeira", shown.Value.Text);
        Assert.False(hidden.Value.Shown);
        Assert.Null(SignLabel.Decode(""));
        Assert.Null(SignLabel.Decode(null));
    }

    [Fact]
    public void Rotulo_a_mostra_usa_o_desenho_com_titulo_e_o_tamanho_pela_largura()
    {
        var catalog = Catalog();

        var curto = catalog.Compose("wood", new SignLabel { Text = "Madeira", Shown = true });
        var longo = catalog.Compose("wood", new SignLabel { Text = "Madeira de pinho", Shown = true });

        Assert.StartsWith("<cspace=-0.0><size=2><line-height=1>Madeira\n", curto);
        Assert.StartsWith("<cspace=-0.0><size=1><line-height=1>Madeira de pinho\n", longo);
    }

    [Fact]
    public void Nome_escondido_entra_no_desenho_comum()
    {
        var text = Catalog().Compose("wood", new SignLabel { Text = "wood", Shown = false });

        Assert.StartsWith("<cspace=-0.0><size=1><line-height=0><#0000>wood\n", text);
    }

    [Fact]
    public void Catalogo_sem_desenho_com_titulo_cai_no_comum()
    {
        var text = Catalog().Compose("weed", new SignLabel { Text = "Erva", Shown = true });

        Assert.StartsWith("<cspace=-0.0><size=1><line-height=0><#0000>Erva\n", text);
    }

    [Fact]
    public void Madeira_e_codigo_viram_rotulo_e_icone()
    {
        var d = SignIconRules.Decide(Catalog(), "Madeira :madeira:", "", null);

        Assert.Equal(SignIconAction.Apply, d.Action);
        Assert.Equal("wood", d.Icon);
        Assert.Equal("=Madeira", d.Label);
        Assert.Contains("<size=2><line-height=1>Madeira\n", d.Text);
    }

    [Fact]
    public void Placa_com_rotulo_intacta_nao_e_reescrita_e_o_corte_e_devolvido()
    {
        var catalog = Catalog();
        var full = catalog.Compose("wood", new SignLabel { Text = "Madeira", Shown = true });

        Assert.Equal(SignIconAction.None, SignIconRules.Decide(catalog, full, "wood", "=Madeira").Action);

        var d = SignIconRules.Decide(catalog, full.Substring(0, SignCode.GameInputLimit), "wood", "=Madeira");
        Assert.Equal(SignIconAction.Restore, d.Action);
        Assert.Equal(full, d.Text);
        Assert.Equal("=Madeira", d.Label);
    }

    [Fact]
    public void Placa_desenhada_antes_do_rotulo_existir_e_refeita_com_o_id_como_nome()
    {
        var old = "<cspace=-0.24><line-height=0.317><size=0.317><#0000>" + new string('█', 40);

        var d = SignIconRules.Decide(Catalog(), old, "wood", null);

        Assert.Equal(SignIconAction.Refresh, d.Action);
        Assert.StartsWith("<cspace=-0.0><size=1><line-height=0><#0000>wood\n", d.Text);
    }
}
