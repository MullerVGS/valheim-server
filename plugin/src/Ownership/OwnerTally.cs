using System.Collections.Generic;
using ValheimMetrics.Traffic;

namespace ValheimMetrics.Ownership
{
    public readonly struct OwnerCount
    {
        public static readonly OwnerCount Empty = new OwnerCount(0, 0, 0);

        public readonly long Zdos;
        public readonly long Creatures;
        public readonly long EventCreatures;

        public OwnerCount(long zdos, long creatures, long eventCreatures)
        {
            Zdos = zdos;
            Creatures = creatures;
            EventCreatures = eventCreatures;
        }

        public override string ToString() => $"{Zdos}/{Creatures}/{EventCreatures}";
    }

    // Contagem por dono montada ao longo de uma volta pelo mundo; so vira resultado quando a volta fecha,
    // para o painel nunca ver meia contagem.
    sealed class OwnerTally
    {
        sealed class Counter
        {
            public long Zdos;
            public long Creatures;
            public long EventCreatures;
        }

        Dictionary<long, Counter> _building = new Dictionary<long, Counter>();
        Dictionary<long, Counter> _published = new Dictionary<long, Counter>();
        Dictionary<ZdoKey, long> _creatureOwner = new Dictionary<ZdoKey, long>();
        Dictionary<ZdoKey, long> _lastCreatureOwner = new Dictionary<ZdoKey, long>();

        public long Passes { get; private set; }

        public void Add(long owner) => Get(owner).Zdos++;

        // Conta a criatura (ela tambem e um ZDO do dono) e diz se o dono mudou desde a volta anterior.
        public bool AddCreature(long owner, ZdoKey creature, bool eventCreature)
        {
            var c = Get(owner);
            c.Zdos++;
            if (eventCreature)
                c.EventCreatures++;
            else
                c.Creatures++;
            _creatureOwner[creature] = owner;
            return _lastCreatureOwner.TryGetValue(creature, out var previous) && previous != owner;
        }

        public void Finish()
        {
            var published = _published;
            _published = _building;
            _building = published;
            _building.Clear();

            var last = _lastCreatureOwner;
            _lastCreatureOwner = _creatureOwner;
            _creatureOwner = last;
            _creatureOwner.Clear();

            Passes++;
        }

        public OwnerCount Of(long owner) =>
            _published.TryGetValue(owner, out var c) ? new OwnerCount(c.Zdos, c.Creatures, c.EventCreatures) : OwnerCount.Empty;

        public IEnumerable<long> Owners() => _published.Keys;

        Counter Get(long owner)
        {
            if (!_building.TryGetValue(owner, out var c))
                _building[owner] = c = new Counter();
            return c;
        }
    }
}
