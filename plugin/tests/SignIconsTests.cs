using System.Collections.Generic;
using ValheimMetrics.Signs;
using Xunit;

public class SignIconsTests
{
    const string Honey = "<cspace=-0.24><line-height=0.317><size=0.317><#0000>██<#fb3>█\n<#da4>███";
    const string Weed = "<cspace=-0.24><line-height=0.4><size=0.4><#0000>█<#6f6>██\n<#3a3>████████";

    static SignIconCatalog Catalog(List<string> warnings = null) =>
        SignIconCatalog.Parse(new[]
        {
            "# comentario",
            "D\tweed",
            "I\thoney\t" + Honey.Replace("\n", "\\n"),
            "I\tweed\t" + Weed.Replace("\n", "\\n"),
            "A\tmel\thoney",
            "A\tmaconha\tweed",
        }, warnings ?? new List<string>());

    [Theory]
    [InlineData(":honey:", "honey")]
    [InlineData("  :Honey:  ", "honey")]
    [InlineData(":Yellow Mushroom:", "yellowmushroom")]
    [InlineData(":yellow_mushroom:", "yellowmushroom")]
    [InlineData(":Dente-de-leão:", "dentedeleao")]
    public void Le_o_codigo_e_normaliza(string text, string key)
    {
        Assert.True(SignCode.TryParse(text, out var parsed));
        Assert.Equal(key, parsed);
    }

    [Theory]
    [InlineData("honey")]
    [InlineData(":honey")]
    [InlineData("::")]
    [InlineData(": - :")]
    [InlineData("Mel :honey:")]
    [InlineData(":a::b:")]
    [InlineData(":<b>x</b>:")]
    [InlineData("")]
    [InlineData(null)]
    public void Texto_comum_nao_e_codigo(string text)
    {
        Assert.False(SignCode.TryParse(text, out _));
    }

    [Fact]
    public void Catalogo_devolve_quebra_de_linha_de_verdade()
    {
        var catalog = Catalog();

        Assert.Equal(2, catalog.Icons);
        Assert.Equal(2, catalog.Aliases);
        Assert.Equal(Honey, catalog.TextOf("honey"));
        Assert.Contains("\n", catalog.TextOf("honey"));
    }

    [Fact]
    public void Apelido_para_icone_ausente_e_linha_torta_viram_aviso()
    {
        var warnings = new List<string>();
        var catalog = SignIconCatalog.Parse(new[] { "I\thoney\tx", "A\tmel\thoney", "A\tcera\twax", "lixo", "D\tnada" }, warnings);

        Assert.Equal(1, catalog.Aliases);
        Assert.Null(catalog.DefaultIcon);
        Assert.Equal(3, warnings.Count);
    }

    [Fact]
    public void Apelido_nao_encobre_icone_de_mesmo_nome()
    {
        var catalog = SignIconCatalog.Parse(new[] { "I\tiron\tA", "I\tscrap\tB", "A\tiron\tscrap" }, new List<string>());

        Assert.Equal("iron", catalog.Resolve("iron"));
    }

    [Fact]
    public void Codigo_vira_icone_pelo_id_ou_pelo_apelido()
    {
        var byId = SignIconRules.Decide(Catalog(), ":honey:", "");
        var byAlias = SignIconRules.Decide(Catalog(), ":Mel:", "");

        Assert.Equal(SignIconAction.Apply, byId.Action);
        Assert.Equal(Honey, byId.Text);
        Assert.False(byId.UsedDefault);
        Assert.Equal("honey", byAlias.Icon);
        Assert.False(byAlias.UsedDefault);
    }

    [Fact]
    public void Codigo_desconhecido_cai_na_folha()
    {
        var d = SignIconRules.Decide(Catalog(), ":espada lendaria:", "");

        Assert.Equal(SignIconAction.Apply, d.Action);
        Assert.Equal("weed", d.Icon);
        Assert.Equal(Weed, d.Text);
        Assert.True(d.UsedDefault);
    }

    [Fact]
    public void Pedir_a_folha_pelo_nome_nao_conta_como_padrao()
    {
        Assert.False(SignIconRules.Decide(Catalog(), ":weed:", "").UsedDefault);
        Assert.False(SignIconRules.Decide(Catalog(), ":maconha:", "").UsedDefault);
    }

    [Fact]
    public void Sem_icone_padrao_codigo_desconhecido_fica_como_esta()
    {
        var catalog = SignIconCatalog.Parse(new[] { "I\thoney\tx" }, new List<string>());

        Assert.Equal(SignIconAction.None, SignIconRules.Decide(catalog, ":nada:", "").Action);
    }

    [Fact]
    public void Placa_comum_nunca_e_tocada()
    {
        Assert.Equal(SignIconAction.None, SignIconRules.Decide(Catalog(), "HONEY", "").Action);
        Assert.Equal(SignIconAction.None, SignIconRules.Decide(Catalog(), "<#f00>███", null).Action);
    }

    [Fact]
    public void Icone_intacto_nao_e_reescrito()
    {
        Assert.Equal(SignIconAction.None, SignIconRules.Decide(Catalog(), Honey, "honey").Action);
    }

    [Fact]
    public void Texto_cortado_pelo_campo_do_jogo_e_devolvido()
    {
        var cut = Honey.Substring(0, SignCode.GameInputLimit);

        var d = SignIconRules.Decide(Catalog(), cut, "honey");

        Assert.Equal(SignIconAction.Restore, d.Action);
        Assert.Equal(Honey, d.Text);
    }

    [Fact]
    public void Corte_de_um_catalogo_antigo_tambem_e_devolvido()
    {
        var d = SignIconRules.Decide(Catalog(), "<cspace=-0.2><line-height=0.333><size=0.333><#00", "honey");

        Assert.Equal(SignIconAction.Restore, d.Action);
    }

    [Fact]
    public void Desenho_de_catalogo_antigo_e_refeito()
    {
        var old = "<cspace=-0.2><line-height=0.333><size=0.333><#0000>" + new string('█', 40);

        var d = SignIconRules.Decide(Catalog(), old, "honey");

        Assert.Equal(SignIconAction.Refresh, d.Action);
        Assert.Equal(Honey, d.Text);
    }

    [Fact]
    public void Jogador_que_escreve_por_cima_recebe_a_placa_de_volta()
    {
        Assert.Equal(SignIconAction.Release, SignIconRules.Decide(Catalog(), "Mel da casa", "honey").Action);
    }

    [Fact]
    public void Trocar_o_codigo_troca_o_icone()
    {
        var d = SignIconRules.Decide(Catalog(), ":weed:", "honey");

        Assert.Equal(SignIconAction.Apply, d.Action);
        Assert.Equal("weed", d.Icon);
    }

    [Fact]
    public void Icone_que_saiu_do_catalogo_solta_a_placa()
    {
        Assert.Equal(SignIconAction.Release, SignIconRules.Decide(Catalog(), "qualquer", "sumiu").Action);
    }
}
