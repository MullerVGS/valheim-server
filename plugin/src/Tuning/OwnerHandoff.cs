using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimMetrics.Summons;

namespace ValheimMetrics.Tuning
{
    // Substitui ZDOMan.ReleaseNearbyZDOS por uma replica com as regras de OwnerHandoffRules.
    // Base sobre a quina de zona fazia 2-3 mil pecas trocarem de dono a cada borda cruzada, e cada
    // troca e revisao nova reenviada a todos que as veem.
    // A mesma varredura faz o balanceamento (OwnerBalanceRules): criatura de dono com upload no teto
    // passa para o jogador da vez, se ele esta perto e tem folga.
    static class OwnerHandoff
    {
        public enum Kind { Object, Tamed, Creature, Player }
        public static readonly string[] KindNames = { "object", "tamed", "creature", "player" };

        public static OwnerHysteresisMode Mode { get; private set; }
        public static OwnerBalanceMode Balance { get; private set; }
        public static bool Patched { get; private set; }
        public static readonly long[] Releases = new long[4];
        public static readonly long[] Claims = new long[4];
        public static readonly long[] Kept = new long[4];
        // Criaturas passadas de dono (Balance = On) ou que seriam (Measure), por Kind.
        public static readonly long[] BalanceMoves = new long[4];

        struct OwnerView
        {
            public bool Peer;
            public Vector2s Zone;
            public Vector3 Position;
            public int Near;
            public bool Classic;
        }

        static readonly List<ZDO> _near = new List<ZDO>();
        static readonly Dictionary<long, OwnerView> _owners = new Dictionary<long, OwnerView>();
        static bool _failed;

        struct ShedCandidate
        {
            public ZDO Zdo;
            public long Owner;
            public Kind Kind;
            public float Meters;
        }

        static readonly OwnerBalanceBook<ZDOID> _book = new OwnerBalanceBook<ZDOID>();
        static readonly List<ShedCandidate> _shed = new List<ShedCandidate>();
        static readonly Dictionary<long, bool> _overloaded = new Dictionary<long, bool>();
        static readonly Dictionary<long, int> _shedFrom = new Dictionary<long, int>();
        static double _prunedAt;

        public static void Install(Harmony harmony, OwnerHysteresisMode mode, OwnerBalanceMode balance)
        {
            if (mode == OwnerHysteresisMode.Off && balance == OwnerBalanceMode.Off)
                return;
            if (!Patcher.Patch(harmony, typeof(ZDOMan), "ReleaseNearbyZDOS", new[] { typeof(Vector3), typeof(long) },
                    typeof(OwnerHandoff), prefix: nameof(ReleaseNearbyZDOSPrefix)))
                return;
            Patched = true;
            Mode = mode;
            Balance = balance;
            if (mode != OwnerHysteresisMode.Off)
                Plugin.Log.LogInfo(mode == OwnerHysteresisMode.On
                    ? "Histerese de dono ligada: o dono so perde a posse quando o objeto sai do que ele tem carregado."
                    : "Histerese de dono em medicao: comportamento do jogo, contando o que seria segurado.");
            if (balance != OwnerBalanceMode.Off)
                Plugin.Log.LogInfo(balance == OwnerBalanceMode.On
                    ? "Balanceamento de criaturas ligado: dono com upload no teto passa criaturas para quem esta perto e tem folga."
                    : "Balanceamento de criaturas em medicao: conta o que passaria de dono, sem passar.");
        }

        // Falhou uma vez = volta ao metodo do jogo para sempre, em vez de repetir a excecao a cada 2 s.
        static bool ReleaseNearbyZDOSPrefix(ZDOMan __instance, Vector3 __0, long __1)
        {
            if (_failed)
                return true;
            try
            {
                Release(__instance, __0, __1);
                return false;
            }
            catch (Exception e)
            {
                _failed = true;
                Patcher.Errors++;
                Plugin.Log.LogWarning($"Histerese de dono desligada, volta ao jogo: {e}");
                return true;
            }
        }

        static void Release(ZDOMan zdoman, Vector3 refPosition, long uid)
        {
            var net = ZNet.instance;
            var synced = net.GetSyncedSimulationDistance();
            var zone = ZoneSystem.GetZone(refPosition);
            bool hysteresis = Mode == OwnerHysteresisMode.On;
            var creatures = Prefabs.Creatures;

            _owners.Clear();
            _near.Clear();
            ChunkLoader.LoadedAreas.FindOwnershipObjects(zdoman, zone,
                new SimulationDistance(synced.NearSimulationDistance, 0, synced.IsClassic), _near, null, uid);
            var self = View(net, uid);
            double now = Time.realtimeSinceStartupAsDouble;
            bool receiving = Receiving(net, uid, creatures, now);

            foreach (var zdo in _near)
            {
                if (!zdo.Persistent)
                    continue;
                var position = zdo.GetPosition();
                long owner = zdo.GetOwner();
                bool mine = owner == uid;
                bool hasOwner = zdo.HasOwner();
                bool selfActive = ChunkLoader.LoadedAreas.OwnershipActiveArea(position, zone, uid);

                // Os campos caros so quando o jogo trocaria o dono.
                bool candidate = mine ? !selfActive : selfActive;
                if (!candidate)
                    continue;
                bool ownerActive = false;
                OwnerView ownerView = default;
                if (!mine && hasOwner)
                {
                    ownerView = View(net, owner);
                    ownerActive = ownerView.Peer && ZNetScene.InActiveArea(position, ownerView.Zone);
                    if (ownerActive)
                    {
                        if (receiving)
                            ConsiderShed(net, zdo, owner, ownerView, self, position, creatures, now);
                        continue;
                    }
                }

                var kind = Classify(zdo, creatures);
                bool sticky = kind == Kind.Object || kind == Kind.Tamed;
                var sector = zdo.GetSector();
                bool selfLoaded = mine && sticky && Loads(self, sector);
                bool ownerLoaded = !mine && sticky && hasOwner && Loads(ownerView, sector);

                var decision = OwnerHandoffRules.Decide(mine, hasOwner, selfActive, selfLoaded, ownerActive,
                    ownerLoaded, sticky, hysteresis);
                int k = (int)kind;
                if (decision.Kept)
                    Kept[k]++;
                if (decision.Action == HandoffAction.Release)
                {
                    zdo.SetOwner(0L);
                    Releases[k]++;
                }
                else if (decision.Action == HandoffAction.Claim)
                {
                    zdo.SetOwner(uid);
                    Claims[k]++;
                }
            }

            if (receiving && _shed.Count > 0)
                Shed(net, uid, now);
        }

        // Este jogador pode receber criaturas agora? Antes de saber o que e criatura (boot), nao.
        static bool Receiving(ZNet net, long uid, HashSet<int> creatures, double now)
        {
            _shed.Clear();
            _overloaded.Clear();
            if (Balance == OwnerBalanceMode.Off || creatures == null)
                return false;
            if (now - _prunedAt > OwnerBalanceRules.CreatureCooldownSeconds)
            {
                _book.Prune(now);
                _prunedAt = now;
            }
            if (!_book.CanReceive(uid, now))
                return false;
            return PeerUpload.InBytes(net.GetPeer(uid), now) is float upload && OwnerBalanceRules.Spare(upload);
        }

        static void ConsiderShed(ZNet net, ZDO zdo, long owner, OwnerView ownerView, OwnerView self, Vector3 position,
            HashSet<int> creatures, double now)
        {
            if (!_overloaded.TryGetValue(owner, out var overloaded))
            {
                overloaded = PeerUpload.InBytes(net.GetPeer(owner), now) is float upload
                    && OwnerBalanceRules.Overloaded(upload);
                _overloaded[owner] = overloaded;
            }
            if (!overloaded)
                return;
            var kind = Classify(zdo, creatures);
            if (kind != Kind.Creature && kind != Kind.Tamed)
                return;
            if (SummonNames.IsSummon(zdo.GetPrefab()) || !_book.CanMove(zdo.m_uid, now))
                return;
            float meters = Vector3.Distance(position, self.Position);
            if (!OwnerBalanceRules.Closer(meters, Vector3.Distance(position, ownerView.Position)))
                return;
            _shed.Add(new ShedCandidate { Zdo = zdo, Owner = owner, Kind = kind, Meters = meters });
        }

        // As mais perto de quem recebe primeiro, no maximo MaxPerPass por vez.
        static void Shed(ZNet net, long uid, double now)
        {
            _shed.Sort((a, b) => a.Meters.CompareTo(b.Meters));
            _shedFrom.Clear();
            int count = Math.Min(_shed.Count, OwnerBalanceRules.MaxPerPass);
            for (int i = 0; i < count; i++)
            {
                var c = _shed[i];
                if (Balance == OwnerBalanceMode.On)
                    c.Zdo.SetOwner(uid);
                BalanceMoves[(int)c.Kind]++;
                _book.Moved(c.Zdo.m_uid, uid, now);
                _shedFrom.TryGetValue(c.Owner, out var n);
                _shedFrom[c.Owner] = n + 1;
            }

            var receiver = net.GetPeer(uid);
            foreach (var pair in _shedFrom)
            {
                var giver = net.GetPeer(pair.Key);
                Plugin.Log.LogInfo($"Balanceamento{(Balance == OwnerBalanceMode.On ? "" : " (medicao)")}: {pair.Value} criatura(s) de " +
                    $"{giver?.m_playerName} ({Kb(giver, now)} KB/s) para {receiver?.m_playerName} ({Kb(receiver, now)} KB/s).");
            }
            _shed.Clear();
        }

        static string Kb(ZNetPeer peer, double now) =>
            PeerUpload.InBytes(peer, now) is float bytes ? (bytes / 1000f).ToString("0") : "?";

        static bool Loads(OwnerView view, Vector2s sector) =>
            view.Peer && OwnerHandoffRules.Loaded(view.Zone.x, view.Zone.y, sector.x, sector.y, view.Near, view.Classic);

        // Dono que nao e jogador conectado (o proprio servidor ou quem saiu) nunca segura a posse.
        static OwnerView View(ZNet net, long uid)
        {
            if (_owners.TryGetValue(uid, out var view))
                return view;
            view = default;
            if (uid != ZDOMan.GetSessionID())
            {
                var peer = net.GetPeer(uid);
                if (peer != null)
                {
                    view.Peer = true;
                    view.Position = peer.GetRefPos();
                    view.Zone = ZoneSystem.GetZone(view.Position);
                    view.Near = peer.m_simulationDistance.NearSimulationDistance;
                    view.Classic = peer.m_simulationDistance.IsClassic;
                }
            }
            _owners[uid] = view;
            return view;
        }

        // Sem ZNetScene ainda (boot), tudo e criatura: sem histerese ate saber o que e o que.
        static Kind Classify(ZDO zdo, HashSet<int> creatures)
        {
            int prefab = zdo.GetPrefab();
            if (prefab == Prefabs.Player)
                return Kind.Player;
            if (creatures == null)
                return Kind.Creature;
            if (!creatures.Contains(prefab))
                return Kind.Object;
            return zdo.GetBool(ZDOVars.s_tamed, false) ? Kind.Tamed : Kind.Creature;
        }
    }
}
