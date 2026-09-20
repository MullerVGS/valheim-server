using System.Collections.Generic;
using ValheimMetrics.Signs;
using Xunit;

public class SignLabelTests
{
    const string Reset = "<size=100.0%><cspace=0.0><line-height=100.0%>";
    const string Plain = "<cspace=-0.0><size=1><line-height=0><#0000>{label}\n<cspace=-0.288><material=Valheim_Fonts/Valheim-Norse><line-height=0.32><size=0.368><#a63>███\n<#0000>█<#a63>██" + Reset;
    const string Titled = "<cspace=-0.0><size={ls}><line-height=1>{label}\n<cspace=-0.276><material=Valheim_Fonts/Valheim-Norse><line-height=0.24><size=0.276><#a63>███\n<#0000>█<#a63>██" + Reset;
    const string Unlit = "<material=Valheim_Fonts/Valheim-Norse>";

    static SignIconCatalog Catalog() =>
        SignIconCatalog.Parse(new[]
        {
            "D\tweed",
            "I\twood\t" + Plain.Replace("\n", "\\n"),
            "T\twood\t" + Titled.Replace("\n", "\\n"),
            "I\tweed\t" + Plain.Replace("\n", "\\n").Replace("a63", "3a3"),
            "A\tmadeira\twood",
            "M\tu\t" + Unlit,
            "I\tholo\t<cspace=-0.0><size=1><line-height=0><#0000>{label}\\n<cspace=-0.03><material=Valheim_Fonts/Valheim-Norse><line-height=0.284><size=0.284><#c32a>▅▅<space=13.632>▅▅",
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

        var text = Catalog().Compose("wood", label);

        Assert.StartsWith("<cspace=-0.0><size=1><line-height=0><#0000>Tudo que sobrou da ultima raid\n", text);
    }

    [Fact]
    public void Abreviacao_no_rotulo_e_expandida_e_nao_conta_no_tamanho()
    {
        Assert.True(SignCode.TryParse("{u}<#fc6>Madeira :wood:", out _, out var label));

        var text = Catalog().Compose("wood", label);

        Assert.StartsWith("<cspace=-0.0><size=2><line-height=1>" + Unlit + "<#fc6>Madeira\n", text);
    }

    [Fact]
    public void Tag_que_mexe_no_tamanho_da_linha_sai_do_rotulo()
    {
        Assert.True(SignCode.TryParse("<size=5><b>Madeira</b><line-height=9> :wood:", out _, out var label));

        var text = Catalog().Compose("wood", label);

        Assert.StartsWith("<cspace=-0.0><size=2><line-height=1><b>Madeira</b>\n", text);
    }

    [Theory]
    [InlineData("<size=12>:wood:", 12.0, null)]
    [InlineData("<size=3.5> :wood:", 3.5, null)]
    [InlineData("Madeira <size=12>:wood:", 12.0, "Madeira")]
    [InlineData("<size=12>:wood: Madeira", 12.0, "Madeira")]
    public void Size_colado_no_codigo_e_o_tamanho_do_icone(string text, double size, string label)
    {
        Assert.True(SignCode.TryParse(text, out var key, out var parsed, out var parsedSize));
        Assert.Equal("wood", key);
        Assert.Equal(size, parsedSize.Value, 3);
        Assert.Equal(label != null, parsed.Shown);
        if (label != null)
            Assert.Equal(label, parsed.Text);
    }

    [Fact]
    public void Sem_size_o_icone_fica_do_tamanho_do_catalogo()
    {
        Assert.True(SignCode.TryParse("Madeira :wood:", out _, out _, out var size));
        Assert.Null(size);
    }

    [Fact]
    public void Icone_maior_que_a_tabua_usa_o_avanco_do_auto_size_derrubado()
    {
        // 7,6 unidades = passo 0,32; 15,2 = o dobro. Nao cabe: o auto-size cai para 1 e o extra do
        // Bold vira 0,03. Sobreposicao de 15% mantida: 0,03 + 0,15 * 0,64 = 0,126.
        var text = Catalog().Compose("wood", new SignLabel { Text = "wood" }, 15.2);

        Assert.Contains("\n<cspace=-0.126><material=Valheim_Fonts/Valheim-Norse><line-height=0.64><size=0.736><#a63>███\n", text);
        Assert.EndsWith(Reset, text);
    }

    [Fact]
    public void Icone_menor_que_o_padrao_continua_na_conta_de_quem_cabe()
    {
        // metade: passo 0,16; cabe: extra 0,24 + 0,15 * 0,16 = 0,264
        var text = Catalog().Compose("wood", new SignLabel { Text = "wood" }, 3.8);

        Assert.Contains("<cspace=-0.264><material=Valheim_Fonts/Valheim-Norse><line-height=0.16><size=0.184>", text);
    }

    [Fact]
    public void Com_rotulo_o_tamanho_pedido_so_cabe_ate_o_desenho_com_titulo()
    {
        var label = new SignLabel { Text = "Madeira", Shown = true };

        var cabe = Catalog().Compose("wood", label, 5.0);
        var vaza = Catalog().Compose("wood", label, 7.6);

        Assert.Contains("<size=2><line-height=1>Madeira\n<cspace=-0.272>", cabe);
        Assert.Contains("<size=2><line-height=1>Madeira\n<cspace=-0.078>", vaza);
    }

    [Fact]
    public void Tamanho_e_limitado_ao_que_nao_quebra_linha()
    {
        var text = Catalog().Compose("wood", new SignLabel { Text = "wood" }, 500);

        Assert.Contains("<line-height=0.758>", text);
    }

    [Fact]
    public void Desenho_que_nao_e_so_bloco_ignora_o_tamanho()
    {
        var catalog = Catalog();

        Assert.Equal(catalog.Compose("holo", new SignLabel { Text = "holo" }),
            catalog.Compose("holo", new SignLabel { Text = "holo" }, 15));
    }

    [Fact]
    public void Tamanho_pedido_fica_anotado_e_volta_no_redesenho()
    {
        var catalog = Catalog();

        var d = SignIconRules.Decide(catalog, "<size=15.2>:wood:", "", null, null);
        Assert.Equal(SignIconAction.Apply, d.Action);
        Assert.Equal("15.2", d.Size);
        Assert.Contains("<line-height=0.64>", d.Text);

        Assert.Equal(SignIconAction.None, SignIconRules.Decide(catalog, d.Text, d.Icon, d.Label, d.Size).Action);
        var cut = SignIconRules.Decide(catalog, d.Text.Substring(0, SignCode.GameInputLimit), d.Icon, d.Label, d.Size);
        Assert.Equal(SignIconAction.Restore, cut.Action);
        Assert.Equal(d.Text, cut.Text);
        Assert.Equal("15.2", cut.Size);
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
