using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using ValheimMetrics.Exposition;
using ValheimMetrics.Ownership;
using ValheimMetrics.Traffic;

namespace ValheimMetrics.Collectors
{
    // Quem simula o que. O servidor nao simula mob: cada ZDO tem um dono, e e o cliente dele que roda a IA.
    // Com o mundo passando de um milhao de ZDOs, a varredura inteira num frame so custava 30-50 ms por segundo;
    // agora ela anda pelos baldes por setor com orcamento por frame e so publica a volta completa.
    sealed class OwnershipCollector : ICollector
    {
        const double BudgetSeconds = 0.001;
        const int CheckEvery = 256;

        static AccessTools.FieldRef<ZDOMan, List<ZDO>[]> _bySector;
        static ZDOMan _zdoman;
        static SectorCursor<ZDO> _cursor;
        static readonly OwnerTally Tally = new OwnerTally();
        static readonly Stopwatch Step = new Stopwatch();
        static readonly Func<bool> OverBudget = () => Step.Elapsed.TotalSeconds >= BudgetSeconds;
        static readonly Action<ZDO> Visit = Count;
        static HashSet<int> _creaturePrefabs;

        static double _passStartedAt = -1;
        static double _passWork;
        static double _passStepMax;
        static double _lastPassSeconds;
        static double _lastPassWork;
        static double _lastPassStepMax;

        public string Name => "ownership";

        public void Install(Harmony harmony)
        {
            _bySector = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");
        }

        public static void OnFrame(double now)
        {
            if (_bySector == null)
                return;
            var zdoman = ZDOMan.instance;
            if (zdoman == null || (_creaturePrefabs = Prefabs.Creatures) == null)
                return;
            if (zdoman != _zdoman)
            {
                _zdoman = zdoman;
                _cursor = new SectorCursor<ZDO>(_bySector(zdoman), CheckEvery);
                _passStartedAt = -1;
            }
            if (_passStartedAt < 0)
            {
                _passStartedAt = now;
                _passWork = _passStepMax = 0;
            }

            Step.Restart();
            bool wrapped = _cursor.Step(OverBudget, Visit);
            double spent = Step.Elapsed.TotalSeconds;
            _passWork += spent;
            if (spent > _passStepMax)
                _passStepMax = spent;

            if (!wrapped)
                return;
            Tally.Finish();
            _lastPassSeconds = now - _passStartedAt;
            _lastPassWork = _passWork;
            _lastPassStepMax = _passStepMax;
            _passStartedAt = -1;
        }

        static void Count(ZDO zdo)
        {
            long owner = zdo.GetOwner();
            if (owner == 0)
                return;
            if (!_creaturePrefabs.Contains(zdo.GetPrefab()))
            {
                Tally.Add(owner);
                return;
            }
            var key = new ZdoKey(zdo.m_uid.UserID, zdo.m_uid.ID);
            if (Tally.AddCreature(owner, key, zdo.GetBool(ZDOVars.s_eventCreature, false)))
            {
                var player = Players.ForUid(owner);
                if (player != null)
                    player.CreatureOwnerChanges++;
            }
        }

        public void Write(PrometheusWriter w, double now)
        {
            var zdoman = ZDOMan.instance;
            if (zdoman == null)
                return;

            w.Family("valheim_zdos", "gauge", "ZDOs no mundo.");
            w.Sample("valheim_zdos", zdoman.NrOfObjects());

            w.Family("valheim_ownership_scan_passes_total", "counter", "Voltas completas da contagem de donos.");
            w.Sample("valheim_ownership_scan_passes_total", Tally.Passes);
            w.Family("valheim_ownership_scan_pass_seconds", "gauge", "Tempo de relogio da ultima volta (idade maxima da contagem).");
            w.Sample("valheim_ownership_scan_pass_seconds", _lastPassSeconds);
            w.Family("valheim_ownership_scan_work_seconds", "gauge", "Tempo de thread principal gasto na ultima volta, somando os frames.");
            w.Sample("valheim_ownership_scan_work_seconds", _lastPassWork);
            w.Family("valheim_ownership_scan_step_max_seconds", "gauge", "Maior fatia de um frame na ultima volta.");
            w.Sample("valheim_ownership_scan_step_max_seconds", _lastPassStepMax);

            // Sem volta completa ainda: melhor faltar a serie que mostrar zero falso.
            if (Tally.Passes == 0)
                return;

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
            foreach (var owner in Tally.Owners())
            {
                if (Players.ForUid(owner) == null)
                    orphan += Tally.Of(owner).Zdos;
            }
            w.Family("valheim_zdos_owned_by_disconnected", "gauge", "ZDOs com dono que nao esta conectado.");
            w.Sample("valheim_zdos_owned_by_disconnected", orphan);

            w.Family("valheim_creature_owner_changes_total", "counter", "Criaturas que passaram a ser simuladas por este jogador (ping-pong de dono).");
            foreach (var p in Players.Connected)
                w.Sample("valheim_creature_owner_changes_total", p.CreatureOwnerChanges, p.Labels);
        }

        static OwnerCount Get(PlayerState p) =>
            p.Peer != null ? Tally.Of(p.Peer.m_uid) : OwnerCount.Empty;
    }
}
