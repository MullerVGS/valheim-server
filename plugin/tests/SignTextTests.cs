using System.Collections.Generic;
using ValheimMetrics.Signs;
using Xunit;

public class SignTextTests
{
    const string Unlit = "<material=Valheim_Fonts/Valheim-Norse>";

    static SignIconCatalog Catalog(List<string> warnings = null) =>
        SignIconCatalog.Parse(new[]
        {
            "I\thoney\tx",
            "M\tu\t" + Unlit,
            "M\tfogo\t<#f60>🔥",
        }, warnings ?? new List<string>());

    [Fact]
    public void Abreviacao_conhecida_e_expandida()
    {
        var catalog = Catalog();

        Assert.Equal(2, catalog.Macros);
        Assert.Equal(Unlit + "<size=20><#f60>🔥", catalog.Expand("{u}<size=20><#f60>🔥"));
        Assert.Equal(Unlit + "<#f60>🔥", catalog.Expand("{U}{Fogo}"));
    }

    [Fact]
    public void Chave_desconhecida_ou_aberta_fica_como_o_jogador_escreveu()
    {
        var catalog = Catalog();

        Assert.Equal("{nada} {u", catalog.Expand("{nada} {u"));
        Assert.False(catalog.HasMacro("{nada} {u"));
        Assert.True(catalog.HasMacro("a {u} b"));
    }

    [Fact]
    public void Abreviacao_sem_nome_valido_ou_vazia_vira_aviso()
    {
        var warnings = new List<string>();
        var catalog = SignIconCatalog.Parse(new[] { "I\thoney\tx", "M\tU 1\tx", "M\tv\t" }, warnings);

        Assert.Equal(0, catalog.Macros);
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void Texto_com_abreviacao_e_escrito_por_extenso_e_a_fonte_fica_guardada()
    {
        var d = SignTextRules.Decide(Catalog(), "{u}<#f60>🔥", null, null);

        Assert.Equal(SignTextAction.Write, d.Action);
        Assert.Equal("{u}<#f60>🔥", d.Source);
        Assert.Equal(Unlit + "<#f60>🔥", d.Text);
    }

    [Fact]
    public void Placa_comum_nunca_e_tocada()
    {
        Assert.Equal(SignTextAction.None, SignTextRules.Decide(Catalog(), "Mel da casa", null, null).Action);
        Assert.Equal(SignTextAction.None, SignTextRules.Decide(Catalog(), "{nada}", "", "antes").Action);
        Assert.Equal(SignTextAction.None, SignTextRules.Decide(Catalog(), null, null, null).Action);
    }

    [Fact]
    public void Continuacao_emenda_no_texto_comum_que_estava_na_placa()
    {
        var d = SignTextRules.Decide(Catalog(), ">><size=9>███", null, "<#f00>");

        Assert.Equal(SignTextAction.Write, d.Action);
        Assert.Equal("<#f00><size=9>███", d.Text);
    }

    [Fact]
    public void Continuacao_emenda_na_fonte_guardada_e_passa_de_50()
    {
        var first = "{u}" + new string('a', 40);
        var second = ">>" + new string('b', 48);

        var d = SignTextRules.Decide(Catalog(), second, first, Unlit + new string('a', 40));

        Assert.Equal(SignTextAction.Write, d.Action);
        Assert.Equal(first + new string('b', 48), d.Source);
        Assert.Equal(Unlit + new string('a', 40) + new string('b', 48), d.Text);
        Assert.True(d.Text.Length > SignCode.GameInputLimit);
    }

    [Fact]
    public void Continuacao_sem_nada_antes_comeca_do_zero()
    {
        var d = SignTextRules.Decide(Catalog(), ">>abc", null, null);

        Assert.Equal("abc", d.Text);
    }

    [Fact]
    public void Continuacao_nao_emenda_em_desenho_do_servidor()
    {
        var drawing = "<cspace=-0.24>" + new string('█', 80);

        var d = SignTextRules.Decide(Catalog(), ">>abc", null, drawing);

        Assert.Equal("abc", d.Text);
    }

    [Fact]
    public void Marcador_repetido_nao_sobra_no_texto()
    {
        var d = SignTextRules.Decide(Catalog(), ">>>>abc", null, null);

        Assert.Equal("abc", d.Text);
        Assert.Equal(SignTextAction.None, SignTextRules.Decide(Catalog(), d.Text, d.Source, null).Action);
    }

    [Fact]
    public void Resultado_curto_e_sem_abreviacao_volta_a_ser_placa_comum()
    {
        var d = SignTextRules.Decide(Catalog(), ">>def", null, "abc");

        Assert.Equal(SignTextAction.Write, d.Action);
        Assert.Equal("abcdef", d.Text);
        Assert.Null(d.Source);
    }

    [Fact]
    public void Texto_por_extenso_intacto_nao_e_reescrito()
    {
        Assert.Equal(SignTextAction.None,
            SignTextRules.Decide(Catalog(), Unlit + "<#f60>🔥", "{u}<#f60>🔥", null).Action);
    }

    [Fact]
    public void Texto_cortado_pelo_campo_do_jogo_e_devolvido()
    {
        var source = "{u}" + new string('a', 40);
        var full = Unlit + new string('a', 40);

        var d = SignTextRules.Decide(Catalog(), full.Substring(0, SignCode.GameInputLimit), source, full);

        Assert.Equal(SignTextAction.Restore, d.Action);
        Assert.Equal(full, d.Text);
        Assert.Equal(source, d.Source);
    }

    [Fact]
    public void Abreviacao_que_mudou_no_catalogo_e_reescrita()
    {
        var old = "<material=Valheim_Fonts/Valheim-Rune>" + new string('a', 40);

        var d = SignTextRules.Decide(Catalog(), old, "{u}" + new string('a', 40), null);

        Assert.Equal(SignTextAction.Refresh, d.Action);
        Assert.Equal(Unlit + new string('a', 40), d.Text);
    }

    [Fact]
    public void Jogador_que_escreve_por_cima_recebe_a_placa_de_volta()
    {
        Assert.Equal(SignTextAction.Release,
            SignTextRules.Decide(Catalog(), "Fogueira", "{u}<#f60>🔥", Unlit + "<#f60>🔥").Action);
    }

    [Fact]
    public void Escrever_outra_abreviacao_por_cima_troca_a_fonte()
    {
        var d = SignTextRules.Decide(Catalog(), "{fogo}", "{u}<#f60>🔥", Unlit + "<#f60>🔥");

        Assert.Equal(SignTextAction.Write, d.Action);
        Assert.Equal("{fogo}", d.Source);
        Assert.Equal("<#f60>🔥", d.Text);
    }
}
