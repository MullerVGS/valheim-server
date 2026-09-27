using ValheimMetrics.Tuning;
using Xunit;

public class OwnerHandoffRulesTests
{
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(2, 2, true)]
    [InlineData(3, 0, false)]
    [InlineData(-2, 1, true)]
    public void Classico_carrega_o_quadrado_inteiro(int x, int y, bool expected) =>
        Assert.Equal(expected, OwnerHandoffRules.Loaded(0, 0, x, y, near: 2, classic: true));

    [Theory]
    [InlineData(2, 0, true)]
    [InlineData(2, 1, true)]
    [InlineData(2, 2, false)]
    public void Fora_do_classico_corta_as_quinas(int x, int y, bool expected) =>
        Assert.Equal(expected, OwnerHandoffRules.Loaded(0, 0, x, y, near: 2, classic: false));

    [Fact]
    public void Distancia_nunca_validada_so_carrega_a_propria_zona()
    {
        Assert.True(OwnerHandoffRules.Loaded(5, -3, 5, -3, near: 0, classic: false));
        Assert.False(OwnerHandoffRules.Loaded(5, -3, 6, -3, near: 0, classic: false));
    }

    static HandoffDecision Decide(bool mine, bool hasOwner, bool selfActive, bool selfLoaded, bool ownerActive,
        bool ownerLoaded, bool sticky, bool hysteresis) =>
        OwnerHandoffRules.Decide(mine, hasOwner, selfActive, selfLoaded, ownerActive, ownerLoaded, sticky, hysteresis);

    [Fact]
    public void Dono_dentro_da_area_ativa_nao_muda()
    {
        var d = Decide(mine: true, hasOwner: true, selfActive: true, selfLoaded: true, ownerActive: true, ownerLoaded: true, sticky: true, hysteresis: true);
        Assert.Equal(HandoffAction.None, d.Action);
        Assert.False(d.Kept);
    }

    [Fact]
    public void Dono_que_saiu_da_area_mas_ainda_carrega_segura_a_peca()
    {
        var d = Decide(mine: true, hasOwner: true, selfActive: false, selfLoaded: true, ownerActive: false, ownerLoaded: false, sticky: true, hysteresis: true);
        Assert.Equal(HandoffAction.None, d.Action);
        Assert.True(d.Kept);
    }

    [Fact]
    public void Em_medicao_age_como_o_jogo_e_conta()
    {
        var release = Decide(mine: true, hasOwner: true, selfActive: false, selfLoaded: true, ownerActive: false, ownerLoaded: false, sticky: true, hysteresis: false);
        Assert.Equal(HandoffAction.Release, release.Action);
        Assert.True(release.Kept);

        var claim = Decide(mine: false, hasOwner: true, selfActive: true, selfLoaded: false, ownerActive: false, ownerLoaded: true, sticky: true, hysteresis: false);
        Assert.Equal(HandoffAction.Claim, claim.Action);
        Assert.True(claim.Kept);
    }

    [Fact]
    public void Dono_que_nao_carrega_mais_solta()
    {
        var d = Decide(mine: true, hasOwner: true, selfActive: false, selfLoaded: false, ownerActive: false, ownerLoaded: false, sticky: true, hysteresis: true);
        Assert.Equal(HandoffAction.Release, d.Action);
        Assert.False(d.Kept);
    }

    [Fact]
    public void Outro_jogador_nao_toma_peca_que_o_dono_ainda_carrega()
    {
        var d = Decide(mine: false, hasOwner: true, selfActive: true, selfLoaded: false, ownerActive: false, ownerLoaded: true, sticky: true, hysteresis: true);
        Assert.Equal(HandoffAction.None, d.Action);
        Assert.True(d.Kept);
    }

    [Fact]
    public void Criatura_selvagem_troca_como_no_jogo()
    {
        var mine = Decide(mine: true, hasOwner: true, selfActive: false, selfLoaded: true, ownerActive: false, ownerLoaded: false, sticky: false, hysteresis: true);
        Assert.Equal(HandoffAction.Release, mine.Action);

        var other = Decide(mine: false, hasOwner: true, selfActive: true, selfLoaded: false, ownerActive: false, ownerLoaded: true, sticky: false, hysteresis: true);
        Assert.Equal(HandoffAction.Claim, other.Action);
        Assert.False(other.Kept);
    }

    [Fact]
    public void Sem_dono_quem_esta_na_area_assume()
    {
        var d = Decide(mine: false, hasOwner: false, selfActive: true, selfLoaded: false, ownerActive: false, ownerLoaded: false, sticky: true, hysteresis: true);
        Assert.Equal(HandoffAction.Claim, d.Action);
    }

    [Fact]
    public void Fora_da_minha_area_nao_assumo_nada()
    {
        var d = Decide(mine: false, hasOwner: false, selfActive: false, selfLoaded: false, ownerActive: false, ownerLoaded: false, sticky: true, hysteresis: true);
        Assert.Equal(HandoffAction.None, d.Action);
    }
}
