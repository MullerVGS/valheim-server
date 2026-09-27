using ValheimMetrics.Traffic;
using Xunit;

public class TrafficBookTests
{
    static readonly ZdoKey A = new ZdoKey(1, 1);
    static readonly ZdoKey B = new ZdoKey(1, 2);
    static readonly ZdoKey C = new ZdoKey(2, 1);

    [Theory]
    [InlineData(0f, 0f, 0, 0)]
    [InlineData(31.9f, -31.9f, 0, 0)]
    [InlineData(32f, -32.1f, 1, -1)]
    [InlineData(-96.1f, 95.9f, -2, 1)]
    public void Zona_igual_ao_ZoneSystem(float x, float z, int zx, int zy)
    {
        var zone = Zone.Of(x, z);

        Assert.Equal((zx, zy), (zone.X, zone.Y));
    }

    [Fact]
    public void Prefab_acumula_envio_e_recebimento_separados()
    {
        var book = new TrafficBook();

        book.Add(A, 10, 0, 0, 7, 100, sent: true);
        book.Add(A, 10, 0, 0, 7, 100, sent: true);
        book.Add(B, 10, 0, 0, 7, 60, sent: false);

        var tally = book.ByPrefab[10];
        Assert.Equal((200L, 2L, 60L, 1L), (tally.SentBytes, tally.SentUpdates, tally.ReceivedBytes, tally.ReceivedUpdates));
    }

    [Fact]
    public void Ranking_por_bytes_e_janela_zera_mas_prefab_nao()
    {
        var book = new TrafficBook();
        book.Add(A, 10, 0, 0, 7, 50, sent: true);
        book.Add(B, 20, 10, 10, 7, 300, sent: true);
        book.Add(C, 30, 200, 200, 8, 120, sent: false);
        book.Add(A, 10, 5, 5, 9, 50, sent: false);

        var window = book.Take(2, 5);

        Assert.Equal(3, window.Zdos);
        Assert.Equal(new[] { B, C }, window.TopZdos.ConvertAll(h => h.Key));
        Assert.Equal(520L, window.Total.SentBytes + window.Total.ReceivedBytes);
        var first = window.TopZdos[0];
        Assert.Equal((20, 10f, 7L), (first.Prefab, first.X, first.Owner));
        Assert.Equal(0, book.WindowZdos);
        Assert.Equal(100L, book.ByPrefab[10].Bytes);
    }

    [Fact]
    public void Zona_soma_os_ZDOs_dela_e_divide_por_prefab()
    {
        var book = new TrafficBook();
        book.Add(A, 10, 0, 0, 7, 100, sent: true);
        book.Add(B, 20, 20, -20, 7, 300, sent: true);
        book.Add(C, 10, 500, 500, 7, 350, sent: true);

        var window = book.Take(10, 1);

        var zone = Assert.Single(window.TopZones);
        Assert.Equal((0, 0, 2, 400L), (zone.Zone.X, zone.Zone.Y, zone.Zdos, zone.Tally.Bytes));
        Assert.Equal(300L, zone.BytesByPrefab[20]);
    }

    [Fact]
    public void Ultima_posicao_e_dono_ganham()
    {
        var book = new TrafficBook();
        book.Add(A, 10, 0, 0, 7, 10, sent: true);
        book.Add(A, 10, 100, 64, 8, 10, sent: true);

        var hot = book.Take(1, 1).TopZdos[0];

        Assert.Equal((100f, 64f, 8L), (hot.X, hot.Z, hot.Owner));
    }

    [Fact]
    public void Prefab_conhecido_depois_substitui_o_zero()
    {
        var book = new TrafficBook();
        book.Add(A, 0, 0, 0, 7, 10, sent: false);
        book.Add(A, 42, 0, 0, 7, 10, sent: false);
        book.Add(A, 0, 0, 0, 7, 10, sent: false);

        Assert.Equal(42, book.Take(1, 1).TopZdos[0].Prefab);
    }
}
