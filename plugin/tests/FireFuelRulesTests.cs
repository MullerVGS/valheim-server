using ValheimMetrics.Fire;
using Xunit;

public class FireFuelRulesTests
{
    const long Second = 10_000_000;
    const float SecPerFuel = 100;

    static FuelStep Observe(float before, float after, double seconds, float owed = 0, int factor = 5) =>
        FuelRules.Observe(before, 0, after, (long)(seconds * Second), SecPerFuel, factor, owed);

    [Fact]
    public void Queima_normal_devolve_quatro_quintos()
    {
        var step = Observe(5, 4, 100);

        Assert.Equal(0.8f, step.Owed, 4);
        Assert.False(step.BurnedOut);
    }

    [Fact]
    public void Acumula_sobre_o_que_ja_devia()
    {
        var step = Observe(5, 4.5f, 50, owed: 1);

        Assert.Equal(1.4f, step.Owed, 4);
    }

    [Fact]
    public void Queda_maior_que_o_tempo_explica_nao_e_devolvida_alem_do_tempo()
    {
        // Retirada de combustivel ou escrita perdida: so o que o relogio justifica volta.
        var step = Observe(5, 2, 100);

        Assert.Equal(0.8f, step.Owed, 4);
    }

    [Fact]
    public void Fogo_apagado_ou_molhado_nao_ganha_nada()
    {
        var step = Observe(5, 5, 600);

        Assert.Equal(0, step.Owed);
    }

    [Fact]
    public void Reabastecido_na_janela_nao_ganha_nada()
    {
        var step = Observe(2, 3, 100);

        Assert.Equal(0, step.Owed);
    }

    [Fact]
    public void Sem_tempo_passado_nao_mexe()
    {
        var step = Observe(5, 4, 0, owed: 0.3f);

        Assert.Equal(0.3f, step.Owed, 4);
        Assert.False(step.BurnedOut);
    }

    [Fact]
    public void Apagou_por_falta_devolve_o_que_restaria_no_ritmo_lento()
    {
        // Volta depois de 1000 s longe: o jogo queimou 10, havia 6; no ritmo 5x queimaria 2.
        var step = Observe(6, 0, 1000);

        Assert.True(step.BurnedOut);
        Assert.Equal(4f, step.Owed, 4);
    }

    [Fact]
    public void Apagou_conta_o_que_ainda_devia()
    {
        var step = Observe(1, 0, 1000, owed: 0.5f);

        Assert.True(step.BurnedOut);
        Assert.Equal(0, step.Owed);
    }

    [Fact]
    public void Apagou_de_vez_mesmo_no_ritmo_lento_fica_em_zero()
    {
        var step = Observe(1, 0, 10_000);

        Assert.True(step.BurnedOut);
        Assert.Equal(0, step.Owed);
    }

    [Fact]
    public void Fator_um_desliga()
    {
        var step = Observe(5, 4, 100, factor: 1);

        Assert.Equal(0, step.Owed);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" 5 ", 5)]
    [InlineData("1", null)]
    [InlineData("0", null)]
    [InlineData("101", null)]
    [InlineData("cinco", null)]
    [InlineData("100", 100)]
    public void Le_o_fator(string raw, int? expected)
    {
        Assert.Equal(expected, FuelRules.ParseFactor(raw));
    }
}
