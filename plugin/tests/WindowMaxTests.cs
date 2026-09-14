using ValheimMetrics.Exposition;
using Xunit;

public class WindowMaxTests
{
    [Fact]
    public void Max_cobre_os_ultimos_n_segundos_inteiros()
    {
        var w = new WindowMax(5);
        w.Add(0.2, 0.5);
        w.Add(3.0, 0.1);

        Assert.Equal(0.5, w.Max(4.9));
        Assert.Equal(0.1, w.Max(5.5));
    }

    [Fact]
    public void Janela_vazia_ou_expirada_da_zero()
    {
        var w = new WindowMax(5);
        Assert.Equal(0, w.Max(10));

        w.Add(1, 0.3);
        Assert.Equal(0, w.Max(100));
    }

    [Fact]
    public void Mesmo_segundo_guarda_o_maior()
    {
        var w = new WindowMax(2);
        w.Add(7.1, 0.2);
        w.Add(7.8, 0.9);
        w.Add(7.9, 0.4);

        Assert.Equal(0.9, w.Max(8.5));
    }
}
