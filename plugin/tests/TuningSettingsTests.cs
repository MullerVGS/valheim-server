using System.Collections.Generic;
using ValheimMetrics.Tuning;
using Xunit;

public class TuningSettingsTests
{
    static TuningSettings Parse(Dictionary<string, string> env) =>
        TuningSettings.Parse(name => env.TryGetValue(name, out var v) ? v : null);

    [Fact]
    public void Sem_variavel_nao_ajusta_nada()
    {
        var s = Parse(new Dictionary<string, string>());

        Assert.Null(s.ServerFps);
        Assert.Null(s.ZdoSendLimitBytes);
        Assert.Null(s.SteamSendRateBytes);
        Assert.Empty(s.Warnings);
    }

    [Fact]
    public void Le_os_tres_ajustes()
    {
        var s = Parse(new Dictionary<string, string>
        {
            ["VALHEIM_SERVER_FPS"] = "60",
            ["VALHEIM_ZDO_SEND_LIMIT_BYTES"] = " 20480 ",
            ["VALHEIM_STEAM_SEND_RATE_BYTES"] = "307200",
        });

        Assert.Equal(60, s.ServerFps);
        Assert.Equal(20480, s.ZdoSendLimitBytes);
        Assert.Equal(307200, s.SteamSendRateBytes);
        Assert.Empty(s.Warnings);
    }

    [Fact]
    public void Vazio_conta_como_ausente()
    {
        var s = Parse(new Dictionary<string, string> { ["VALHEIM_SERVER_FPS"] = "" });

        Assert.Null(s.ServerFps);
        Assert.Empty(s.Warnings);
    }

    [Theory]
    [InlineData("VALHEIM_SERVER_FPS", "29")]
    [InlineData("VALHEIM_SERVER_FPS", "361")]
    [InlineData("VALHEIM_SERVER_FPS", "sessenta")]
    [InlineData("VALHEIM_ZDO_SEND_LIMIT_BYTES", "8192")]
    [InlineData("VALHEIM_ZDO_SEND_LIMIT_BYTES", "65537")]
    [InlineData("VALHEIM_STEAM_SEND_RATE_BYTES", "153599")]
    [InlineData("VALHEIM_STEAM_SEND_RATE_BYTES", "1048577")]
    public void Fora_da_faixa_e_ignorado_com_aviso(string name, string value)
    {
        var s = Parse(new Dictionary<string, string> { [name] = value });

        Assert.Null(s.ServerFps);
        Assert.Null(s.ZdoSendLimitBytes);
        Assert.Null(s.SteamSendRateBytes);
        Assert.Single(s.Warnings);
    }

    [Fact]
    public void Limites_da_faixa_valem()
    {
        var s = Parse(new Dictionary<string, string>
        {
            ["VALHEIM_SERVER_FPS"] = "360",
            ["VALHEIM_ZDO_SEND_LIMIT_BYTES"] = "10240",
            ["VALHEIM_STEAM_SEND_RATE_BYTES"] = "1048576",
        });

        Assert.Equal(360, s.ServerFps);
        Assert.Equal(10240, s.ZdoSendLimitBytes);
        Assert.Equal(1048576, s.SteamSendRateBytes);
    }
}
