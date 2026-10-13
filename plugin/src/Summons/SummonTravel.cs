using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimMetrics.Collectors;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Summons
{
    // Followers arrive with their player after a portal or dungeon door, without a client mod.
    // Teleportation is client-side: the server detects the character ZDO's position jump afterwards.
    // By then the owner has either unsummoned distant followers or unloaded their area.
    // Move followers left behind, transferring ownership and advancing their revision. Recreate
    // unsummoned followers from a ZDO snapshot captured when RPC_UnSummon crosses the server.
    // Both paths preserve `follow`; the client resumes following when it instantiates the summon.
    // The client's unsummon effect at the origin still plays.
    sealed class SummonTravel : ICollector
    {
        sealed class Snapshot
        {
            public int Prefab;
            public Vector3 At;
            public ZPackage Data;
        }

        const uint RevisionLead = 1000;
        // Allow slack on m_unsummonDistance: snapshot and destination reflect the server view.
        const float ReachSlack = 0.8f;
        const float RingMeters = 0.8f;
        const float DropMeters = 0.5f;

        static readonly int UnSummonHash = "RPC_UnSummon".GetStableHashCode();
        static readonly SummonTrips<ZDOID, Snapshot> _trips = new SummonTrips<ZDOID, Snapshot>();
        static readonly List<ZDO> _near = new List<ZDO>();
        static readonly List<ZDOID> _followers = new List<ZDOID>();
        static readonly Dictionary<int, float> _reach = new Dictionary<int, float>();
        static bool _enabled;
        static long _travels;
        static long _moved;
        static long _recreated;
        static long _lost;

        public string Name => "summon_travel";

        public void Install(Harmony harmony)
        {
            var raw = Environment.GetEnvironmentVariable(SummonTrips<ZDOID, Snapshot>.Variable);
            if (string.IsNullOrWhiteSpace(raw))
                return;
            if (raw.Trim() != "1")
            {
                Plugin.Log.LogWarning($"{SummonTrips<ZDOID, Snapshot>.Variable}={raw} ignored: expected 1.");
                return;
            }
            if (!Patcher.Patch(harmony, typeof(ZRoutedRpc.RoutedRPCData), "Deserialize", new[] { typeof(ZPackage) }, typeof(SummonTravel),
                    postfix: nameof(RoutedPostfix), tag: "summon_travel"))
                return;
            _enabled = true;
            Plugin.Log.LogInfo("Summon travel: followers accompany their player through portals and dungeon doors.");
        }

        // The summon ZDO still exists when the routed RPC arrives; destruction follows in the
        // next packet, so capture its state now.
        static void RoutedPostfix(ZRoutedRpc.RoutedRPCData __instance)
        {
            if (__instance.m_methodHash != UnSummonHash)
                return;
            try
            {
                var zdo = ZDOMan.instance?.GetZDO(__instance.m_targetZDO);
                if (zdo == null || !SummonNames.IsSummon(zdo.GetPrefab()))
                    return;
                var player = zdo.GetString(ZDOVars.s_follow);
                if (player.Length == 0)
                    return;
                var data = new ZPackage();
                zdo.Serialize(data);
                _trips.Unsummoned(zdo.m_uid, player, Time.realtimeSinceStartupAsDouble,
                    new Snapshot { Prefab = zdo.GetPrefab(), At = zdo.GetPosition(), Data = data });
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        public static void OnFrame(double now)
        {
            if (!_enabled)
                return;
            var net = ZNet.instance;
            if (net == null || !net.IsServer() || ZDOMan.instance == null || ZNetScene.instance == null)
                return;
            try
            {
                foreach (var peer in net.GetPeers())
                {
                    if (!peer.IsReady() || !Where(peer, out var at))
                        continue;
                    if (_trips.Sample(peer.m_uid, peer.m_characterID, at.x, at.y, at.z, out var x, out var y, out var z))
                        Depart(peer, new Vector3(x, y, z), now);
                }
                if (_trips.Idle)
                    return;
                foreach (var trip in _trips.Settle(now, Exists))
                    Arrive(trip);
            }
            catch (Exception e)
            {
                Patcher.Errors++;
                Plugin.Log.LogWarning($"Summon travel: {e.Message}");
            }
        }

        // Followers are in the loaded area the player just left. Those outside it would already
        // have been unsummoned or left behind earlier.
        static void Depart(ZNetPeer peer, Vector3 from, double now)
        {
            var synced = ZNet.instance.GetSyncedSimulationDistance();
            _near.Clear();
            _followers.Clear();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(from),
                new SimulationDistance(synced.NearSimulationDistance, 0, synced.IsClassic), _near);
            foreach (var zdo in _near)
                if (SummonNames.IsSummon(zdo.GetPrefab()) && zdo.GetString(ZDOVars.s_follow) == peer.m_playerName)
                    _followers.Add(zdo.m_uid);
            _near.Clear();
            _trips.Depart(peer.m_playerName, now, _followers);
        }

        static void Arrive(TripOutcome<ZDOID, Snapshot> trip)
        {
            if (trip.Left.Count + trip.Unsummoned.Count + trip.Gone == 0)
                return;
            _travels++;
            var peer = ZNet.instance.GetPeerByPlayerName(trip.Player);
            if (peer == null || !Where(peer, out var destination))
            {
                _lost += trip.Unsummoned.Count + trip.Gone;
                return;
            }

            int moved = 0, recreated = 0, walking = 0;
            foreach (var id in trip.Left)
            {
                var zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null || !zdo.IsValid())
                    continue;
                // A destination in the same loaded area lets the follower walk there.
                if (ZNetScene.InActiveArea(zdo.GetPosition(), destination))
                {
                    walking++;
                    continue;
                }
                Place(zdo, peer, Spot(destination, moved + recreated));
                zdo.DataRevision += RevisionLead;
                moved++;
            }
            foreach (var snapshot in trip.Unsummoned)
            {
                // Unsummoning within reach is the summon limit, not the trip.
                if (Vector3.Distance(snapshot.At, destination) < Reach(snapshot.Prefab) * ReachSlack)
                    continue;
                var zdo = ZDOMan.instance.CreateNewZDO(destination, snapshot.Prefab);
                snapshot.Data.SetPos(0);
                zdo.Deserialize(snapshot.Data);
                Place(zdo, peer, Spot(destination, moved + recreated));
                SummonNames.Adopt(zdo);
                recreated++;
            }

            _moved += moved;
            _recreated += recreated;
            _lost += trip.Gone;
            Plugin.Log.LogInfo($"Summon travel: {trip.Player}: {moved} moved, {recreated} recreated, {walking} in the same area, {trip.Gone} lost.");
        }

        // The arriving player owns the summon: their client instantiates and simulates it.
        static void Place(ZDO zdo, ZNetPeer peer, Vector3 at)
        {
            zdo.SetOwner(peer.m_uid);
            zdo.SetPosition(at);
            zdo.Set(ZDOVars.s_spawnPoint, at);
        }

        // The server has no destination terrain: place around the player, slightly above their feet.
        static Vector3 Spot(Vector3 center, int index)
        {
            float angle = index * 2.4f;
            return center + new Vector3(Mathf.Cos(angle) * RingMeters, DropMeters, Mathf.Sin(angle) * RingMeters);
        }

        // Peer reference positions arrive every 2 s; character ZDO positions use the normal stream.
        static bool Where(ZNetPeer peer, out Vector3 at)
        {
            var zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
            bool valid = zdo != null && zdo.IsValid();
            at = valid ? zdo.GetPosition() : Vector3.zero;
            return valid;
        }

        // Distance at which the owner unsummons; infinite if the prefab has no distance limit.
        static float Reach(int prefabHash)
        {
            if (_reach.TryGetValue(prefabHash, out var cached))
                return cached;
            var prefab = ZNetScene.instance.GetPrefab(prefabHash);
            var tame = prefab ? prefab.GetComponent<Tameable>() : null;
            float reach = tame != null && tame.m_unsummonDistance > 0 ? tame.m_unsummonDistance : float.PositiveInfinity;
            _reach[prefabHash] = reach;
            Plugin.Log.LogInfo($"Summon travel: {(prefab ? prefab.name : prefabHash.ToString())} is unsummoned at {reach} m from its target.");
            return reach;
        }

        static bool Exists(ZDOID id)
        {
            var zdo = ZDOMan.instance.GetZDO(id);
            return zdo != null && zdo.IsValid();
        }

        static bool Online(long uid) => ZNet.instance != null && ZNet.instance.GetPeer(uid) != null;

        public void Write(PrometheusWriter w, double now)
        {
            if (!_enabled)
                return;
            _trips.Retain(Online);
            w.Family("valheim_summon_travel_trips_total", "counter", "Player teleports with following summons.");
            w.Sample("valheim_summon_travel_trips_total", _travels);
            w.Family("valheim_summon_travel_moved_total", "counter", "Summons left at the origin and moved to the destination by the server.");
            w.Sample("valheim_summon_travel_moved_total", _moved);
            w.Family("valheim_summon_travel_recreated_total", "counter", "Summons unsummoned by distance and recreated at the destination.");
            w.Sample("valheim_summon_travel_recreated_total", _recreated);
            w.Family("valheim_summon_travel_lost_total", "counter", "Summons lost without an unsummon snapshot or after the player disconnected.");
            w.Sample("valheim_summon_travel_lost_total", _lost);
        }
    }
}
