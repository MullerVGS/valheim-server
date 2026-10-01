using System.Collections.Generic;
using ValheimMetrics.Summons;
using Xunit;

public class SummonBookTests
{
    readonly HashSet<int> _alive = new HashSet<int>();
    readonly SummonBook<int> _book = new SummonBook<int>();

    string Summon(int id, string player, string randomName)
    {
        _alive.Add(id);
        return _book.Observe(id, player, randomName, _alive.Contains, out _);
    }

    void Rename(int id, string name) => _book.Observe(id, "", name, _alive.Contains, out _);

    [Fact]
    public void FirstSummonKeepsTheRandomName()
    {
        Assert.Null(Summon(1, "Bob", "Rattle"));
        Assert.Empty(_book.Roster("Bob"));
    }

    [Fact]
    public void RenamedNameComesBackOnTheNextSummon()
    {
        Summon(1, "Bob", "Rattle");
        Rename(1, "Ossinho");
        _alive.Remove(1);

        Assert.Equal("Ossinho", Summon(2, "Bob", "Clank"));
        Assert.Equal(new[] { "Ossinho" }, _book.Roster("Bob"));
    }

    [Fact]
    public void NameInUseIsNotGivenTwice()
    {
        Summon(1, "Bob", "Rattle");
        Rename(1, "Ossinho");

        Assert.Null(Summon(2, "Bob", "Clank"));
    }

    [Fact]
    public void NewSummonGetsTheNameOfTheOneThatLeft()
    {
        Summon(1, "Bob", "A"); Rename(1, "Ossinho");
        Summon(2, "Bob", "B"); Rename(2, "Tibia");
        Summon(3, "Bob", "C"); Rename(3, "Caveirao");
        _alive.Remove(2);

        Assert.Equal("Tibia", Summon(4, "Bob", "D"));
    }

    [Fact]
    public void RenameReplacesTheOldNameInTheRoster()
    {
        Summon(1, "Bob", "A"); Rename(1, "Ossinho"); Rename(1, "Ossao");

        Assert.Equal(new[] { "Ossao" }, _book.Roster("Bob"));
    }

    [Fact]
    public void RandomNameThatComesBackIsWrittenAgainNotLearned()
    {
        Summon(1, "Bob", "A"); Rename(1, "Ossinho");
        _alive.Remove(1);
        Assert.Equal("Ossinho", Summon(2, "Bob", "Clank"));

        Assert.Equal("Ossinho", _book.Observe(2, "Bob", "Clank", _alive.Contains, out var changed));
        Assert.False(changed);
        Assert.Equal(new[] { "Ossinho" }, _book.Roster("Bob"));

        Assert.Null(_book.Observe(2, "Bob", "Ossinho", _alive.Contains, out _));
        Rename(2, "Clank");
        Assert.Equal(new[] { "Clank" }, _book.Roster("Bob"));
    }

    [Fact]
    public void PlayersHaveSeparateRosters()
    {
        Summon(1, "Bob", "A"); Rename(1, "Ossinho");
        _alive.Remove(1);

        Assert.Null(Summon(2, "Ana", "B"));
    }

    [Fact]
    public void AdoptedSummonHoldsItsName()
    {
        var book = SummonBook<int>.Parse("Bob\tOssinho\tTibia\n");
        _alive.Add(1);
        book.Adopt(1, "Bob", "Ossinho");
        _alive.Add(2);

        Assert.Equal("Tibia", book.Observe(2, "Bob", "Clank", _alive.Contains, out _));
    }

    [Fact]
    public void UnknownPlayerIsLeftAlone()
    {
        _alive.Add(1);
        Assert.Null(_book.Observe(1, "", "Rattle", _alive.Contains, out _));
        Assert.Equal(0, _book.LiveCount);
    }

    [Fact]
    public void RoundTripsThroughText()
    {
        Summon(1, "Bob", "A"); Rename(1, "Ossinho");
        Summon(2, "Bob", "B"); Rename(2, "Tíbia");
        Summon(3, "Ana Clara", "C"); Rename(3, "Caveirão");

        var again = SummonBook<int>.Parse(_book.Serialize());

        Assert.Equal(new[] { "Ossinho", "Tíbia" }, again.Roster("Bob"));
        Assert.Equal(new[] { "Caveirão" }, again.Roster("Ana Clara"));
    }

    [Fact]
    public void ControlCharactersNeverReachTheFile()
    {
        Summon(1, "Bob", "A"); Rename(1, "Oss\tin\nho");

        Assert.Equal(new[] { "Ossinho" }, SummonBook<int>.Parse(_book.Serialize()).Roster("Bob"));
    }
}
