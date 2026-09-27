using System.Collections.Generic;
using ValheimMetrics.Ownership;
using ValheimMetrics.Traffic;
using Xunit;

public class OwnershipScanTests
{
    static List<int>[] Sectors(params int[][] lists)
    {
        var sectors = new List<int>[lists.Length];
        for (int i = 0; i < lists.Length; i++)
            sectors[i] = lists[i] == null ? null : new List<int>(lists[i]);
        return sectors;
    }

    // Orcamento em itens: estoura depois de n visitas, conferido a cada item.
    static System.Func<bool> After(int n)
    {
        int seen = 0;
        return () => ++seen >= n;
    }

    [Fact]
    public void Passada_inteira_visita_tudo_uma_vez_e_fecha_a_volta()
    {
        var cursor = new SectorCursor<int>(Sectors(new[] { 1, 2 }, null, new int[0], new[] { 3 }), checkEvery: 1);
        var seen = new List<int>();

        bool wrapped = cursor.Step(() => false, seen.Add);

        Assert.True(wrapped);
        Assert.Equal(new[] { 1, 2, 3 }, seen);
    }

    [Fact]
    public void Orcamento_estourado_retoma_do_mesmo_ponto_ate_dentro_do_setor()
    {
        var cursor = new SectorCursor<int>(Sectors(new[] { 1, 2, 3, 4 }, new[] { 5 }), checkEvery: 1);
        var seen = new List<int>();

        Assert.False(cursor.Step(After(2), seen.Add));
        Assert.Equal(new[] { 1, 2 }, seen);
        Assert.False(cursor.Step(After(2), seen.Add));
        Assert.Equal(new[] { 1, 2, 3, 4 }, seen);
        Assert.True(cursor.Step(After(2), seen.Add));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, seen);
    }

    [Fact]
    public void Volta_fechada_recomeca_do_primeiro_setor()
    {
        var cursor = new SectorCursor<int>(Sectors(new[] { 1 }, new[] { 2 }), checkEvery: 1);
        cursor.Step(() => false, _ => { });
        var seen = new List<int>();

        cursor.Step(After(1), seen.Add);

        Assert.Equal(new[] { 1 }, seen);
    }

    [Fact]
    public void Setor_que_encolheu_entre_frames_nao_estoura()
    {
        var sectors = Sectors(new[] { 1, 2, 3, 4 }, new[] { 5 });
        var cursor = new SectorCursor<int>(sectors, checkEvery: 1);
        cursor.Step(After(3), _ => { });
        sectors[0].RemoveRange(1, 3);
        var seen = new List<int>();

        Assert.True(cursor.Step(() => false, seen.Add));
        Assert.Equal(new[] { 5 }, seen);
    }

    [Fact]
    public void Conferencia_do_relogio_espacada_ainda_para()
    {
        var cursor = new SectorCursor<int>(Sectors(new[] { 1, 2, 3, 4, 5, 6 }), checkEvery: 4);
        var seen = new List<int>();

        cursor.Step(() => true, seen.Add);

        Assert.Equal(new[] { 1, 2, 3, 4 }, seen);
    }

    static readonly ZdoKey Wolf = new ZdoKey(9, 1);
    static readonly ZdoKey Troll = new ZdoKey(9, 2);

    [Fact]
    public void Contagem_so_aparece_quando_a_volta_fecha()
    {
        var tally = new OwnerTally();
        tally.Add(7);
        tally.AddCreature(7, Wolf, eventCreature: false);

        Assert.Equal(0, tally.Passes);
        Assert.Equal(OwnerCount.Empty, tally.Of(7));

        tally.Finish();

        Assert.Equal(1, tally.Passes);
        Assert.Equal(new OwnerCount(2, 1, 0), tally.Of(7));
    }

    [Fact]
    public void Passada_nova_substitui_a_anterior_e_dono_sumido_sai()
    {
        var tally = new OwnerTally();
        tally.Add(7);
        tally.Add(8);
        tally.Finish();
        tally.Add(8);
        tally.AddCreature(8, Troll, eventCreature: true);
        tally.Finish();

        Assert.Equal(OwnerCount.Empty, tally.Of(7));
        Assert.Equal(new OwnerCount(2, 0, 1), tally.Of(8));
        Assert.Equal(new[] { 8L }, tally.Owners());
    }

    [Fact]
    public void Criatura_que_troca_de_dono_entre_passadas_e_detectada()
    {
        var tally = new OwnerTally();
        Assert.False(tally.AddCreature(7, Wolf, eventCreature: false));
        tally.Finish();

        Assert.False(tally.AddCreature(7, Wolf, eventCreature: false));
        Assert.False(tally.AddCreature(8, Troll, eventCreature: false));
        tally.Finish();

        Assert.True(tally.AddCreature(8, Wolf, eventCreature: false));
        Assert.False(tally.AddCreature(8, Troll, eventCreature: false));
    }
}
