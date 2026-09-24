using ValheimMetrics.Experience;
using Xunit;

public class LagReportTextTests
{
    [Theory]
    [InlineData("lag")]
    [InlineData("LAG")]
    [InlineData("ta lagando muito")]
    [InlineData("lagado demais!!")]
    [InlineData("tá travando")]
    [InlineData("travou tudo")]
    [InlineData("laggy af")]
    public void Reconhece(string text) => Assert.True(LagReportText.IsReport(text));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("flag no portal")]
    [InlineData("vamos no pantano")]
    [InlineData("o blag")]
    public void Ignora(string text) => Assert.False(LagReportText.IsReport(text));

    [Fact]
    public void MesmaFalaParaVariosDestinatariosContaUmaVez()
    {
        var dedup = new LagReportDedup(10);
        Assert.True(dedup.Accept("a", 100));
        Assert.False(dedup.Accept("a", 100.01));
        Assert.True(dedup.Accept("b", 100.02));
        Assert.True(dedup.Accept("a", 111));
    }
}
