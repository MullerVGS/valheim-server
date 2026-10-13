using System.Collections.Generic;
using ValheimMetrics.Summons;
using Xunit;

public class SummonTripsTests
{
    const long Peer = 7;
    const int Character = 100;

    readonly HashSet<int> _alive = new HashSet<int>();
    readonly SummonTrips<int, string> _trips = new SummonTrips<int, string>();

    bool Sample(float x, float y, float z, int character = Character) =>
        _trips.Sample(Peer, character, x, y, z, out _, out _, out _);

    List<TripOutcome<int, string>> Settle(double now) => _trips.Settle(now, _alive.Contains);

    [Fact]
    public void WalkingIsNotATrip()
    {
        Sample(0, 0, 0);

        Assert.False(Sample(5, 0, 5));
        Assert.False(Sample(10, 0, 10));
    }

    [Fact]
    public void PortalIsATripAndTellsWhereThePlayerLeftFrom()
    {
        Sample(10, 30, -4);

        Assert.True(_trips.Sample(Peer, Character, 2000, 35, 900, out var x, out var y, out var z));
        Assert.Equal((10f, 30f, -4f), (x, y, z));
    }

    [Fact]
    public void DungeonDoorIsATrip()
    {
        Sample(10, 30, 10);

        Assert.True(Sample(10, 5030, 10));
    }

    [Fact]
    public void FirstPositionOfAPlayerIsNotATrip()
    {
        Assert.False(Sample(2000, 35, 900));
    }

    [Fact]
    public void RespawnIsNotATrip()
    {
        Sample(0, 0, 0);

        Assert.False(Sample(2000, 35, 900, character: 0));
        Assert.False(Sample(2000, 35, 900, character: 101));
    }

    [Fact]
    public void PlayerWhoLeftIsForgotten()
    {
        Sample(0, 0, 0);
        _trips.Retain(_ => false);

        Assert.False(Sample(2000, 35, 900));
    }

    [Fact]
    public void TripWaitsToSettle()
    {
        _alive.Add(1);
        _trips.Depart("Bob", 10, new[] { 1 });

        Assert.Empty(Settle(12.9));
        Assert.Single(Settle(13));
        Assert.True(_trips.Idle);
    }

    [Fact]
    public void FollowerStillInTheWorldWasLeftBehind()
    {
        _alive.Add(1);
        _trips.Depart("Bob", 10, new[] { 1 });

        var trip = Assert.Single(Settle(13));
        Assert.Equal("Bob", trip.Player);
        Assert.Equal(new[] { 1 }, trip.Left);
        Assert.Empty(trip.Unsummoned);
        Assert.Equal(0, trip.Gone);
    }

    [Fact]
    public void UnsummonedFollowerComesBackFromItsSnapshot()
    {
        _alive.Add(1);
        _trips.Depart("Bob", 10, new[] { 1 });
        _trips.Unsummoned(1, "Bob", 10.1, "snapshot-1");
        _alive.Remove(1);

        var trip = Assert.Single(Settle(13));
        Assert.Empty(trip.Left);
        Assert.Equal(new[] { "snapshot-1" }, trip.Unsummoned);
    }

    [Fact]
    public void UnsummonThatArrivedBeforeTheJumpStillCounts()
    {
        _trips.Unsummoned(1, "Bob", 9.5, "snapshot-1");
        _trips.Depart("Bob", 10, new int[0]);

        var trip = Assert.Single(Settle(13));
        Assert.Equal(new[] { "snapshot-1" }, trip.Unsummoned);
    }

    [Fact]
    public void FollowerThatDiedOnTheWayIsGone()
    {
        _trips.Depart("Bob", 10, new[] { 1 });

        var trip = Assert.Single(Settle(13));
        Assert.Empty(trip.Unsummoned);
        Assert.Equal(1, trip.Gone);
    }

    [Fact]
    public void UnsummonWithoutATripExpires()
    {
        _trips.Unsummoned(1, "Bob", 10, "snapshot-1");

        Assert.Empty(Settle(13));
        Assert.False(_trips.Idle);
        Assert.Empty(Settle(16.1));
        Assert.True(_trips.Idle);
    }

    [Fact]
    public void OldUnsummonDoesNotJoinALaterTrip()
    {
        _trips.Unsummoned(1, "Bob", 5, "snapshot-1");
        _trips.Depart("Bob", 9, new int[0]);

        var trip = Assert.Single(Settle(12));
        Assert.Empty(trip.Unsummoned);
    }

    [Fact]
    public void UnsummonOfSomeoneElseStaysOutOfTheTrip()
    {
        _trips.Unsummoned(1, "Ana", 10, "snapshot-1");
        _trips.Depart("Bob", 10, new int[0]);

        var trip = Assert.Single(Settle(13));
        Assert.Empty(trip.Unsummoned);
    }

    [Fact]
    public void SnapshotIsUsedOnce()
    {
        _trips.Unsummoned(1, "Bob", 10, "snapshot-1");
        _trips.Depart("Bob", 10, new int[0]);
        _trips.Depart("Bob", 10.5, new int[0]);

        var trips = Settle(14);
        Assert.Equal(2, trips.Count);
        Assert.Single(trips[0].Unsummoned);
        Assert.Empty(trips[1].Unsummoned);
    }

    [Fact]
    public void EachFollowerHasItsOwnFate()
    {
        _alive.UnionWith(new[] { 1, 2, 3 });
        _trips.Depart("Bob", 10, new[] { 1, 2, 3 });
        _trips.Unsummoned(2, "Bob", 10, "snapshot-2");
        _alive.Remove(2);
        _alive.Remove(3);

        var trip = Assert.Single(Settle(13));
        Assert.Equal(new[] { 1 }, trip.Left);
        Assert.Equal(new[] { "snapshot-2" }, trip.Unsummoned);
        Assert.Equal(1, trip.Gone);
    }
}
