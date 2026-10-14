using ValheimMetrics.Tuning;
using Xunit;

public class OwnerBalanceRulesTests
{
    [Theory]
    [InlineData(130000f, true)]
    [InlineData(115000f, true)]
    [InlineData(110000f, false)]
    public void Dono_no_teto_esta_sobrecarregado(float upload, bool expected) =>
        Assert.Equal(expected, OwnerBalanceRules.Overloaded(upload));

    [Theory]
    [InlineData(20000f, true)]
    [InlineData(60000f, true)]
    [InlineData(70000f, false)]
    public void Recebe_so_quem_tem_folga(float upload, bool expected) =>
        Assert.Equal(expected, OwnerBalanceRules.Spare(upload));

    [Fact]
    public void Faixa_entre_os_limites_impede_a_volta()
    {
        // Quem recebeu ate o limite de folga ainda esta longe de virar sobrecarregado.
        Assert.False(OwnerBalanceRules.Overloaded(OwnerBalanceRules.SpareBytes * 1.5f));
    }

    [Theory]
    [InlineData(10f, 30f, true)]
    [InlineData(30f, 30f, true)]
    [InlineData(37f, 30f, true)]
    [InlineData(39f, 30f, false)]
    public void Criatura_vai_para_quem_esta_tao_perto_quanto_o_dono(float receiver, float owner, bool expected) =>
        Assert.Equal(expected, OwnerBalanceRules.Closer(receiver, owner));

    [Fact]
    public void Quem_recebeu_espera_antes_de_receber_de_novo()
    {
        var book = new OwnerBalanceBook<int>();
        Assert.True(book.CanReceive(7, now: 100));

        book.Moved(1, receiver: 7, now: 100);

        Assert.False(book.CanReceive(7, now: 100 + OwnerBalanceRules.ReceiverCooldownSeconds - 1));
        Assert.True(book.CanReceive(7, now: 100 + OwnerBalanceRules.ReceiverCooldownSeconds));
        Assert.True(book.CanReceive(8, now: 101));
    }

    [Fact]
    public void Criatura_movida_nao_volta_a_mudar_logo()
    {
        var book = new OwnerBalanceBook<int>();
        book.Moved(1, receiver: 7, now: 100);

        Assert.False(book.CanMove(1, now: 100 + OwnerBalanceRules.CreatureCooldownSeconds - 1));
        Assert.True(book.CanMove(1, now: 100 + OwnerBalanceRules.CreatureCooldownSeconds));
        Assert.True(book.CanMove(2, now: 101));
    }

    [Fact]
    public void Poda_esquece_o_que_ja_venceu()
    {
        var book = new OwnerBalanceBook<int>();
        book.Moved(1, receiver: 7, now: 0);
        book.Moved(2, receiver: 8, now: 100);

        book.Prune(now: OwnerBalanceRules.CreatureCooldownSeconds + 1);

        Assert.Equal(1, book.Tracked);
        Assert.False(book.CanMove(2, now: OwnerBalanceRules.CreatureCooldownSeconds + 1));
    }
}
