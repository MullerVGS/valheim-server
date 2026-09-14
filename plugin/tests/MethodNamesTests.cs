using ValheimMetrics.Exposition;
using Xunit;

public class MethodNamesTests
{
    // Hash falso e independente do jogo: basta ser deterministico para testar a resolucao.
    static int Len(string s) => s.Length;

    [Fact]
    public void Resolve_hash_conhecido_para_o_nome()
    {
        var names = new MethodNames(new[] { "Damage", "RequestOwn" }, Len);

        Assert.Equal("Damage", names.Resolve(6));
        Assert.Equal("RequestOwn", names.Resolve(10));
    }

    [Fact]
    public void Hash_desconhecido_vira_hexadecimal_estavel()
    {
        var names = new MethodNames(new[] { "Damage" }, Len);

        Assert.Equal("0x00000063", names.Resolve(99));
        Assert.Equal("0xfffffffe", names.Resolve(-2));
    }

    [Fact]
    public void Em_colisao_vence_o_primeiro_candidato()
    {
        var names = new MethodNames(new[] { "abcd", "wxyz" }, Len);

        Assert.Equal("abcd", names.Resolve(4));
    }
}
