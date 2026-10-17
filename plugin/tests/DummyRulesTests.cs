using ValheimMetrics.Training;
using Xunit;

public class DummyRulesTests
{
    const float Base = 2500;

    [Fact]
    public void Dummy_novo_vai_para_o_nivel_do_fator_com_vida_cheia()
    {
        var fix = DummyRules.Fix(1, Base, Base, Base, 4);

        Assert.NotNull(fix);
        Assert.Equal(4, fix.Value.Level);
        Assert.Equal(10000f, fix.Value.MaxHealth);
        Assert.Equal(10000f, fix.Value.Health);
    }

    [Fact]
    public void Dummy_apanhando_mantem_a_fracao_da_vida()
    {
        var fix = DummyRules.Fix(1, Base, 1000, Base, 4);

        Assert.Equal(4000f, fix.Value.Health, 2);
    }

    [Fact]
    public void Dummy_ja_no_fator_nao_e_regravado()
    {
        Assert.Null(DummyRules.Fix(4, 10000, 3000, Base, 4));
    }

    [Fact]
    public void Nivel_certo_com_vida_maxima_errada_e_corrigido()
    {
        // The owner reloaded it before the server wrote the max (only level arrived).
        var fix = DummyRules.Fix(4, Base, Base, Base, 4);

        Assert.Equal(10000f, fix.Value.MaxHealth);
        Assert.Equal(10000f, fix.Value.Health);
    }

    [Fact]
    public void Fator_1_devolve_a_vida_do_jogo()
    {
        var fix = DummyRules.Fix(4, 10000, 5000, Base, 1);

        Assert.Equal(1, fix.Value.Level);
        Assert.Equal(Base, fix.Value.MaxHealth);
        Assert.Equal(1250f, fix.Value.Health, 2);
    }

    [Fact]
    public void Dummy_morto_fica_quieto()
    {
        Assert.Null(DummyRules.Fix(1, Base, 0, Base, 4));
    }

    [Fact]
    public void Vida_acima_da_maxima_vira_cheia()
    {
        var fix = DummyRules.Fix(1, Base, 3000, Base, 2);

        Assert.Equal(5000f, fix.Value.Health);
    }

    [Theory]
    [InlineData("4", 4)]
    [InlineData(" 1 ", 1)]
    [InlineData("100", 100)]
    [InlineData("0", null)]
    [InlineData("101", null)]
    [InlineData("x", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Le_o_fator(string raw, int? expected)
    {
        Assert.Equal(expected, DummyRules.ParseFactor(raw));
    }
}
