using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ValheimMetrics.Access;
using Xunit;

public class AccessBookTests
{
    static readonly System.Func<string, bool> Nobody = _ => false;

    [Fact]
    public void Barrado_abre_pedido_e_avisa()
    {
        var book = new AccessBook();
        Assert.True(book.Record("111", "Beorn", allowed: false, now: 1000));
        var pending = book.Pending(Nobody);
        Assert.Single(pending);
        Assert.Equal(1, pending[0].Ticket);
        Assert.Equal("Beorn", pending[0].Name);
    }

    [Fact]
    public void Tentativa_repetida_nao_avisa_de_novo_dentro_da_janela()
    {
        var book = new AccessBook();
        book.Record("111", "Beorn", false, 1000);
        Assert.False(book.Record("111", "Beorn", false, 1030));
        Assert.True(book.Record("111", "Beorn", false, 1030 + AccessBook.NotifyGapSeconds));
        Assert.Equal(1, book.Get("111").Ticket);
        Assert.Equal(3, book.Get("111").Denied);
    }

    [Fact]
    public void Quem_entra_nao_abre_pedido_e_fecha_o_que_tinha()
    {
        var book = new AccessBook();
        Assert.False(book.Record("222", "Tuttan", true, 1000));
        Assert.Empty(book.Pending(Nobody));
        book.Record("111", "Beorn", false, 1000);
        book.Record("111", "Beorn", true, 1100);
        Assert.Empty(book.Pending(Nobody));
        Assert.Equal(1100, book.Get("111").LastSeen);
    }

    [Fact]
    public void Numeros_sao_estaveis_e_nao_se_reusam_enquanto_ha_pedido_aberto()
    {
        var book = new AccessBook();
        book.Record("111", "Beorn", false, 1000);
        book.Record("222", "Llolly", false, 1001);
        book.Close("111");
        book.Record("333", "Coisitona", false, 1002);
        var pending = book.Pending(Nobody);
        Assert.Equal(new[] { 2, 3 }, pending.Select(e => e.Ticket));
    }

    [Fact]
    public void Whitelist_editada_por_fora_tira_da_lista()
    {
        var book = new AccessBook();
        book.Record("111", "Beorn", false, 1000);
        Assert.Empty(book.Pending(id => id == "111"));
    }

    [Theory]
    [InlineData("2", "222")]
    [InlineData("#2", "222")]
    [InlineData("111", "111")]
    [InlineData("llo", "222")]
    [InlineData("BEORN", "111")]
    public void Resolve_por_numero_id_ou_nome(string query, string expected)
    {
        var book = new AccessBook();
        book.Record("111", "Beorn", false, 1000);
        book.Record("222", "Llolly", false, 1001);
        Assert.Equal(expected, AccessBook.Resolve(book.Pending(Nobody), query)?.SteamId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("9")]
    [InlineData("Be")]
    [InlineData("ninguem")]
    public void Resolve_nao_chuta(string query)
    {
        var book = new AccessBook();
        book.Record("111", "Beorn", false, 1000);
        book.Record("222", "Bento", false, 1001);
        Assert.Null(AccessBook.Resolve(book.Pending(Nobody), query));
    }

    [Fact]
    public void Serializa_e_le_de_volta()
    {
        var book = new AccessBook();
        book.Record("111", "Müller Gräs", true, 1000);
        book.Record("222", "Nome\tcom tab", false, 2000);
        var again = AccessBook.Parse(book.Serialize());
        Assert.Equal("Müller Gräs", again.Get("111").Name);
        Assert.Equal("Nome com tab", again.Get("222").Name);
        Assert.Equal(1, again.Get("222").Ticket);
        Assert.Equal(2000, again.Get("222").LastDenied);
        Assert.Equal(1, again.Get("222").Denied);
    }

    [Fact]
    public void Parse_ignora_linha_quebrada()
    {
        var book = AccessBook.Parse("# cabecalho\nlixo\n333\t1\t2\t3\t4\tBeorn\n444\tx\t2\t3\t4\tRuim\n");
        Assert.Equal(new[] { "333" }, book.All.Select(e => e.SteamId));
    }

    [Fact]
    public void Estado_em_json_valido_com_nome_da_whitelist()
    {
        var book = new AccessBook();
        book.Record("111", "Be\"orn <b>", false, 1000);
        book.Record("222", "Tuttan", true, 900);
        var json = AccessState.ToJson(book.Pending(Nobody), new List<string> { "222", "999" }, book, 1234);
        Assert.DoesNotContain("<", json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1234, root.GetProperty("now").GetInt64());
        Assert.Equal("Be\"orn <b>", root.GetProperty("pending")[0].GetProperty("name").GetString());
        Assert.Equal("Tuttan", root.GetProperty("permitted")[0].GetProperty("name").GetString());
        Assert.Equal("", root.GetProperty("permitted")[1].GetProperty("name").GetString());
    }
}
