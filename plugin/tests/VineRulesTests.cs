using System.Collections.Generic;
using System.Linq;
using ValheimMetrics.Vines;
using Xunit;

public class VineRulesTests
{
    const long Minute = 600_000_000;
    const long Respawn = 200 * Minute;
    const long Now = 1000 * Minute;

    // Face of a wall in the x = 0 plane, as Vine segments grow on it: one step apart along z and up y.
    static VineSegment At(double along, double up, bool berry = false, long harvested = 0, double depth = 0, bool back = false, bool soil = true) =>
        new VineSegment
        {
            Center = new V3(depth, up, along),
            Wall = back ? new V3(-1, 0, 0) : new V3(1, 0, 0),
            Up = new V3(0, 1, 0),
            Along = back ? new V3(0, 0, -1) : new V3(0, 0, 1),
            HasBerry = berry,
            Enabled = true,
            HarvestedTicks = harvested,
            NearSoil = soil,
        };

    static List<VineSegment> Row(int count, double up = 0) =>
        Enumerable.Range(0, count).Select(i => At(i * VineRules.Step, up)).ToList();

    static List<int> Plan(List<VineSegment> segments, double spacing = 2) =>
        VineRules.Plan(segments, Now, Respawn, spacing);

    [Theory]
    [InlineData("2", 2.0)]
    [InlineData(" 1.5 ", 1.5)]
    [InlineData("10", 10.0)]
    public void Spacing_valido(string raw, double meters)
    {
        Assert.Equal(meters, VineRules.ParseSpacing(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0.1")]
    [InlineData("11")]
    [InlineData("1,5")]
    public void Spacing_invalido_desliga(string raw)
    {
        Assert.Null(VineRules.ParseSpacing(raw));
    }

    [Fact]
    public void Ponta_nao_da_cacho()
    {
        var row = Row(5);

        Assert.Equal(1, VineRules.Connections(row, 0));
        Assert.Equal(2, VineRules.Connections(row, 2));
        Assert.DoesNotContain(0, Plan(row, spacing: 0.5));
        Assert.DoesNotContain(4, Plan(row, spacing: 0.5));
    }

    [Fact]
    public void Espacamento_menor_que_o_passo_enche_todo_segmento_ligado()
    {
        Assert.Equal(new[] { 1, 2, 3 }, Plan(Row(5), spacing: 0.5));
    }

    [Fact]
    public void Espacamento_de_dois_metros_pula_o_vizinho()
    {
        Assert.Equal(new[] { 1, 3 }, Plan(Row(5)));
    }

    [Fact]
    public void Cacho_existente_bloqueia_so_dentro_do_espacamento()
    {
        var row = Row(6);
        row[2] = At(2 * VineRules.Step, 0, berry: true);

        Assert.Equal(new[] { 4 }, Plan(row));
    }

    [Fact]
    public void Muro_cheio_nao_tem_dois_cachos_perto_na_mesma_face()
    {
        var wall = Row(6, 0).Concat(Row(6, VineRules.Step)).Concat(Row(6, 2 * VineRules.Step)).ToList();

        var given = Plan(wall);

        Assert.NotEmpty(given);
        foreach (int a in given)
            foreach (int b in given)
                if (a != b)
                    Assert.True((wall[a].Center - wall[b].Center).Length >= 2);
    }

    [Fact]
    public void Colhido_ha_pouco_espera_o_tempo_do_jogo()
    {
        var row = Row(3);
        row[1] = At(VineRules.Step, 0, harvested: Now - Respawn + Minute);

        Assert.Empty(Plan(row));

        row[1] = At(VineRules.Step, 0, harvested: Now - Respawn);
        Assert.Equal(new[] { 1 }, Plan(row));
    }

    [Fact]
    public void Colhido_ha_mais_tempo_vem_primeiro()
    {
        var row = Row(4);
        row[1] = At(VineRules.Step, 0, harvested: Now - Respawn);
        row[2] = At(2 * VineRules.Step, 0, harvested: Now - 2 * Respawn);

        Assert.Equal(new[] { 2 }, Plan(row));
    }

    [Fact]
    public void Outra_face_da_parede_nao_conta()
    {
        var front = Row(3);
        var back = Enumerable.Range(0, 3).Select(i => At(i * VineRules.Step, 0, depth: 0.3, back: true)).ToList();
        back[1] = At(VineRules.Step, 0, berry: true, depth: 0.3, back: true);

        var given = Plan(front.Concat(back).ToList());

        Assert.Equal(new[] { 1 }, given);
    }

    [Fact]
    public void Segmento_desligado_nao_ganha()
    {
        var row = Row(3);
        var middle = row[1];
        middle.Enabled = false;
        row[1] = middle;

        Assert.Empty(Plan(row));
    }

    [Fact]
    public void Deriva_do_crescimento_ainda_conta_como_ligacao()
    {
        // Vine.m_randomOffset desloca o filho ate 0,9 m para o lado de quem cresceu.
        var row = new List<VineSegment> { At(0, 0), At(1.5, 0.85), At(3, 0.05) };

        Assert.Equal(2, VineRules.Connections(row, 1));
    }

    [Fact]
    public void Vinha_selvagem_fica_com_a_regra_do_jogo()
    {
        var wild = Enumerable.Range(0, 5).Select(i => At(i * VineRules.Step, 0, soil: false)).ToList();

        Assert.Empty(Plan(wild, spacing: 0.5));
    }

    [Fact]
    public void Um_pedaco_perto_da_terra_arada_marca_a_vinha_inteira()
    {
        var vine = Enumerable.Range(0, 4).Select(i => At(0, i * VineRules.Step, soil: i == 0)).ToList();
        var wild = Enumerable.Range(0, 4).Select(i => At(20, i * VineRules.Step, soil: false)).ToList();

        var planted = VineRules.Planted(vine.Concat(wild).ToList());

        Assert.Equal(new[] { true, true, true, true, false, false, false, false }, planted);
    }

    [Fact]
    public void Terra_arada_lida_do_TCData()
    {
        // TerrainComp.Save com pitch 3: so o vertice (2, 1) cultivado, (0, 0) pintado de outra cor.
        var ms = new System.IO.MemoryStream();
        var w = new System.IO.BinaryWriter(ms);
        w.Write(1); w.Write(7); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(4f);
        w.Write(9);
        for (int i = 0; i < 9; i++)
        {
            w.Write(i == 4);
            if (i == 4) { w.Write(1f); w.Write(0f); }
        }
        w.Write(9);
        for (int i = 0; i < 9; i++)
        {
            bool painted = i == 0 || i == 5;
            w.Write(painted);
            if (painted) { w.Write(1f); w.Write(i == 5 ? 1f : 0.2f); w.Write(0f); w.Write(1f); }
        }

        var soil = SoilMap.Cultivated(ms.ToArray(), 100, -50);

        Assert.Equal(new[] { (101, -50) }, soil);
        var keys = new HashSet<long> { SoilMap.Key(101, -50) };
        Assert.True(SoilMap.Near(keys, 103, -49, VineRules.SoilReach));
        Assert.False(SoilMap.Near(keys, 104, -50, VineRules.SoilReach));
    }
}
