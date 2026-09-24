using System;
using System.Collections.Generic;
using HarmonyLib;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Collectors
{
    // Quem simula o que. O servidor nao simula mob: cada ZDO tem um dono, e e o cliente dele que roda a IA.
    // Varre ZDOExtraData.s_owner (so ZDOs com dono), nao os ~200k de m_objectsByID.
    sealed class OwnershipCollector : ICollector
    {
        sealed class Owned
        {
            public long Zdos;
            public long Creatures;
            public long EventCreatures;
        }

        Dictionary<ZDOID, ushort> _owners;
        Dictionary<ZDOID, long> _lastCreatureOwner = new Dictionary<ZDOID, long>();
        Dictionary<ZDOID, long> _creatureOwner = new Dictionary<ZDOID, long>();
        readonly Dictionary<long, Owned> _byOwner = new Dictionary<long, Owned>();
        readonly List<long> _gone = new List<long>();

        public string Name => "ownership";

        public void Install(Harmony harmony)
        {
            _owners = (Dictionary<ZDOID, ushort>)AccessTools.Field(typeof(ZDOExtraData), "s_owner")?.GetValue(null)
                ?? throw new MissingFieldException("ZDOExtraData", "s_owner");
        }

        public void Write(PrometheusWriter w, double now)
        {
            var zdoman = ZDOMan.instance;
            if (zdoman == null)
                return;
            var creaturePrefabs = Prefabs.Creatures;

            foreach (var owned in _byOwner.Values)
                owned.Zdos = owned.Creatures = owned.EventCreatures = 0;

            foreach (var kv in _owners)
            {
                long owner = ZDOID.GetUserID(kv.Value);
                if (owner == 0)
                    continue;
                if (!_byOwner.TryGetValue(owner, out var owned))
                    _byOwner[owner] = owned = new Owned();
                owned.Zdos++;

                if (creaturePrefabs == null)
                    continue;
                var zdo = zdoman.GetZDO(kv.Key);
                if (zdo == null || !creaturePrefabs.Contains(zdo.GetPrefab()))
                    continue;
                if (zdo.GetBool(ZDOVars.s_eventCreature, false))
                    owned.EventCreatures++;
                else
                    owned.Creatures++;

                _creatureOwner[kv.Key] = owner;
                if (_lastCreatureOwner.TryGetValue(kv.Key, out var previous) && previous != owner)
                {
                    var player = Players.ForUid(owner);
                    if (player != null)
                        player.CreatureOwnerChanges++;
                }
            }
            var swap = _lastCreatureOwner;
            _lastCreatureOwner = _creatureOwner;
            _creatureOwner = swap;
            _creatureOwner.Clear();

            _gone.Clear();
            foreach (var kv in _byOwner)
            {
                if (kv.Value.Zdos == 0)
                    _gone.Add(kv.Key);
            }
            foreach (var owner in _gone)
                _byOwner.Remove(owner);

            w.Family("valheim_zdos", "gauge", "ZDOs no mundo.");
            w.Sample("valheim_zdos", zdoman.NrOfObjects());

            w.Family("valheim_zdos_owned", "gauge", "ZDOs cujo dono e o jogador (o cliente dele simula).");
            foreach (var p in Players.Connected)
                w.Sample("valheim_zdos_owned", Get(p).Zdos, p.Labels);

            w.Family("valheim_creatures_owned", "gauge", "Criaturas simuladas pelo jogador. kind=event: mob de raid.");
            foreach (var p in Players.Connected)
            {
                var owned = Get(p);
                w.Sample("valheim_creatures_owned", owned.Creatures, p.Labels[0], p.Labels[1], p.Labels[2], p.Labels[3], "kind", "normal");
                w.Sample("valheim_creatures_owned", owned.EventCreatures, p.Labels[0], p.Labels[1], p.Labels[2], p.Labels[3], "kind", "event");
            }

            long orphan = 0;
            foreach (var kv in _byOwner)
            {
                if (Players.ForUid(kv.Key) == null)
                    orphan += kv.Value.Zdos;
            }
            w.Family("valheim_zdos_owned_by_disconnected", "gauge", "ZDOs com dono que nao esta conectado.");
            w.Sample("valheim_zdos_owned_by_disconnected", orphan);

            w.Family("valheim_creature_owner_changes_total", "counter", "Criaturas que passaram a ser simuladas por este jogador (ping-pong de dono).");
            foreach (var p in Players.Connected)
                w.Sample("valheim_creature_owner_changes_total", p.CreatureOwnerChanges, p.Labels);
        }

        Owned Get(PlayerState p) =>
            p.Peer != null && _byOwner.TryGetValue(p.Peer.m_uid, out var owned) ? owned : Empty;

        static readonly Owned Empty = new Owned();
    }
}
