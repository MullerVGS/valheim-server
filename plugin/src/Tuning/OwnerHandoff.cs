using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimMetrics.Tuning
{
    // Substitui ZDOMan.ReleaseNearbyZDOS por uma replica com as regras de OwnerHandoffRules.
    // Base sobre a quina de zona fazia 2-3 mil pecas trocarem de dono a cada borda cruzada, e cada
    // troca e revisao nova reenviada a todos que as veem.
    static class OwnerHandoff
    {
        public enum Kind { Object, Tamed, Creature, Player }
        public static readonly string[] KindNames = { "object", "tamed", "creature", "player" };

        public static OwnerHysteresisMode Mode { get; private set; }
        public static readonly long[] Releases = new long[4];
        public static readonly long[] Claims = new long[4];
        public static readonly long[] Kept = new long[4];

        struct OwnerView
        {
            public bool Peer;
            public Vector2s Zone;
            public int Near;
            public bool Classic;
        }

        static readonly List<ZDO> _near = new List<ZDO>();
        static readonly Dictionary<long, OwnerView> _owners = new Dictionary<long, OwnerView>();
        static bool _failed;

        public static void Install(Harmony harmony, OwnerHysteresisMode mode)
        {
            if (mode == OwnerHysteresisMode.Off)
                return;
            if (Patcher.Patch(harmony, typeof(ZDOMan), "ReleaseNearbyZDOS", new[] { typeof(Vector3), typeof(long) },
                    typeof(OwnerHandoff), prefix: nameof(ReleaseNearbyZDOSPrefix)))
            {
                Mode = mode;
                Plugin.Log.LogInfo(mode == OwnerHysteresisMode.On
                    ? "Histerese de dono ligada: o dono so perde a posse quando o objeto sai do que ele tem carregado."
                    : "Histerese de dono em medicao: comportamento do jogo, contando o que seria segurado.");
            }
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
            zdoman.FindSectorObjects(zone, new SimulationDistance(synced.NearSimulationDistance, 0, synced.IsClassic), _near);
            var self = View(net, uid);

            foreach (var zdo in _near)
            {
                if (!zdo.Persistent)
                    continue;
                var position = zdo.GetPosition();
                long owner = zdo.GetOwner();
                bool mine = owner == uid;
                bool hasOwner = zdo.HasOwner();
                bool selfActive = ZNetScene.InActiveArea(position, zone);

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
                        continue;
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
        }

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
                    view.Zone = ZoneSystem.GetZone(peer.GetRefPos());
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
