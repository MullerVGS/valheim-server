using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimMetrics.Summons
{
    // What a trip did to the summons following its player.
    public sealed class TripOutcome<TId, TSnapshot>
    {
        public string Player;
        // Still in the world, where the player used to be.
        public readonly List<TId> Left = new List<TId>();
        // Unsummoned by the owner: only the captured snapshot remains.
        public readonly List<TSnapshot> Unsummoned = new List<TSnapshot>();
        // Disappeared without an unsummon (died on the way).
        public int Gone;
    }

    // Portals, dungeon doors and teleport commands only move the player. Followers are either
    // unsummoned by their owner for being too far away or left behind. Match position jumps with
    // each follower's fate in the surrounding seconds so the server can bring them along.
    // No game dependencies: the server samples positions, reports unsummons and consumes outcomes.
    public sealed class SummonTrips<TId, TSnapshot>
    {
        public const string Variable = "VALHEIM_SUMMON_TRAVEL";
        // A jump this large between samples indicates teleportation.
        public const float JumpMeters = 20f;
        // Unsummon is sent in the jump frame and may arrive before or after the position update.
        // Waiting also covers the peer reference position's 2 s update period: that position
        // determines which peers receive the ZDO.
        public const double EarlySeconds = 3;
        public const double SettleSeconds = 3;

        sealed class Seen
        {
            public TId Character;
            public float X, Y, Z;
        }

        sealed class Trip
        {
            public string Player;
            public double At;
            public List<TId> Followers;
        }

        sealed class Farewell
        {
            public string Player;
            public double At;
            public TSnapshot Snapshot;
        }

        static readonly EqualityComparer<TId> Ids = EqualityComparer<TId>.Default;
        static readonly List<TripOutcome<TId, TSnapshot>> None = new List<TripOutcome<TId, TSnapshot>>();

        readonly Dictionary<long, Seen> _seen = new Dictionary<long, Seen>();
        readonly List<Trip> _trips = new List<Trip>();
        readonly Dictionary<TId, Farewell> _farewells = new Dictionary<TId, Farewell>();

        public bool Idle => _trips.Count == 0 && _farewells.Count == 0;

        // Return true and the departure position on a jump since the previous sample.
        // A new character (death or first spawn) does not count as a trip.
        public bool Sample(long peer, TId character, float x, float y, float z, out float fromX, out float fromY, out float fromZ)
        {
            fromX = fromY = fromZ = 0;
            if (!_seen.TryGetValue(peer, out var seen))
            {
                _seen[peer] = new Seen { Character = character, X = x, Y = y, Z = z };
                return false;
            }
            double dx = x - seen.X, dy = y - seen.Y, dz = z - seen.Z;
            bool jumped = !Ids.Equals(character, default) && Ids.Equals(character, seen.Character)
                && dx * dx + dy * dy + dz * dz >= JumpMeters * JumpMeters;
            if (jumped)
            {
                fromX = seen.X;
                fromY = seen.Y;
                fromZ = seen.Z;
            }
            seen.Character = character;
            seen.X = x;
            seen.Y = y;
            seen.Z = z;
            return jumped;
        }

        public void Retain(Func<long, bool> online)
        {
            foreach (var peer in _seen.Keys.Where(p => !online(p)).ToList())
                _seen.Remove(peer);
        }

        // `followers` are summons still present near the departure position.
        public void Depart(string player, double now, IEnumerable<TId> followers) =>
            _trips.Add(new Trip { Player = player, At = now, Followers = followers.Distinct().ToList() });

        public void Unsummoned(TId id, string player, double now, TSnapshot snapshot) =>
            _farewells[id] = new Farewell { Player = player, At = now, Snapshot = snapshot };

        // Resolve settled trips. Unsummons without a nearby trip by the same player (summon
        // limit, disconnected owner) expire without producing an outcome.
        public List<TripOutcome<TId, TSnapshot>> Settle(double now, Func<TId, bool> exists)
        {
            List<TripOutcome<TId, TSnapshot>> done = null;
            for (int i = 0; i < _trips.Count;)
            {
                var trip = _trips[i];
                if (now < trip.At + SettleSeconds)
                {
                    i++;
                    continue;
                }
                _trips.RemoveAt(i);

                var ids = trip.Followers;
                foreach (var bye in _farewells)
                    if (Belongs(bye.Value, trip) && !ids.Contains(bye.Key))
                        ids.Add(bye.Key);

                var outcome = new TripOutcome<TId, TSnapshot> { Player = trip.Player };
                foreach (var id in ids)
                {
                    if (exists(id))
                        outcome.Left.Add(id);
                    else if (_farewells.TryGetValue(id, out var bye) && Belongs(bye, trip))
                        outcome.Unsummoned.Add(bye.Snapshot);
                    else
                        outcome.Gone++;
                    _farewells.Remove(id);
                }
                (done ??= new List<TripOutcome<TId, TSnapshot>>()).Add(outcome);
            }

            foreach (var id in _farewells.Where(f => now > f.Value.At + EarlySeconds + SettleSeconds).Select(f => f.Key).ToList())
                _farewells.Remove(id);
            return done ?? None;
        }

        static bool Belongs(Farewell bye, Trip trip) =>
            bye.Player == trip.Player && bye.At >= trip.At - EarlySeconds && bye.At <= trip.At + SettleSeconds;
    }
}
