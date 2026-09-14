using ValheimMetrics.Exposition;
using Xunit;

public class PrometheusWriterTests
{
    [Fact]
    public void Gauge_com_labels_sai_no_formato_texto_do_prometheus()
    {
        var w = new PrometheusWriter();
        w.Family("valheim_player_ping_seconds", "gauge", "Ping do jogador.");
        w.Sample("valheim_player_ping_seconds", 0.045, "player", "Tuttan", "steam_id", "76561198845838078");

        Assert.Equal(
            "# HELP valheim_player_ping_seconds Ping do jogador.\n" +
            "# TYPE valheim_player_ping_seconds gauge\n" +
            "valheim_player_ping_seconds{player=\"Tuttan\",steam_id=\"76561198845838078\"} 0.045\n",
            w.ToString());
    }

    [Fact]
    public void Label_escapa_barra_aspas_e_quebra_de_linha_e_preserva_acento()
    {
        var w = new PrometheusWriter();
        w.Sample("m", 1, "player", "Müller \"Gräs\"\\x\ny");

        Assert.Equal("m{player=\"Müller \\\"Gräs\\\"\\\\x\\ny\"} 1\n", w.ToString());
    }

    [Fact]
    public void Valor_sem_label_usa_cultura_invariante_e_especiais()
    {
        var w = new PrometheusWriter();
        w.Sample("a", 1234.5);
        w.Sample("b", double.NaN);
        w.Sample("c", double.PositiveInfinity);

        Assert.Equal("a 1234.5\nb NaN\nc +Inf\n", w.ToString());
    }

    [Fact]
    public void Histograma_sai_cumulativo_com_inf_soma_e_contagem()
    {
        var h = new Histogram(new[] { 0.01, 0.1, 1 });
        h.Observe(0.005);
        h.Observe(0.05);
        h.Observe(0.07);
        h.Observe(5);

        var w = new PrometheusWriter();
        w.Histogram("valheim_server_frame_seconds", h, "k", "v");

        Assert.Equal(
            "valheim_server_frame_seconds_bucket{k=\"v\",le=\"0.01\"} 1\n" +
            "valheim_server_frame_seconds_bucket{k=\"v\",le=\"0.1\"} 3\n" +
            "valheim_server_frame_seconds_bucket{k=\"v\",le=\"1\"} 3\n" +
            "valheim_server_frame_seconds_bucket{k=\"v\",le=\"+Inf\"} 4\n" +
            "valheim_server_frame_seconds_sum{k=\"v\"} 5.125\n" +
            "valheim_server_frame_seconds_count{k=\"v\"} 4\n",
            w.ToString());
    }

    [Fact]
    public void Reset_esvazia_para_o_proximo_snapshot()
    {
        var w = new PrometheusWriter();
        w.Sample("a", 1);
        w.Reset();
        w.Sample("b", 2);

        Assert.Equal("b 2\n", w.ToString());
    }
}
