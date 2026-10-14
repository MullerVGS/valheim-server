using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimMetrics.Collectors;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Vines
{
    // Ashvine berries packed tighter than the game allows, without a client mod (VineRules).
    // Vine and Pickable only run on the ZDO owner; the dedicated server just sees segments arrive.
    // It remembers when each one was harvested and, once a minute, hands berries out with the same
    // RPC the game's respawn uses, so loaded clients show them at once. The flag also goes into the
    // ZDO, ahead in revision, for segments nobody has loaded. A berry already shown is never taken
    // back by the game: Vine.CheckBerryBlocker only blocks segments that have none.
    sealed class VineBerries : ICollector
    {
        const double SweepSeconds = 60;
        const double ScanDelaySeconds = 30;
        const uint RevisionLead = 1000;
        const float DefaultRespawnMinutes = 200;

        static readonly int VinePrefab = "VineAsh".GetStableHashCode();
        static readonly int HarvestKey = "valheim-server.vine_harvest".GetStableHashCode();
        // Vine.m_berryBlocker.center: the middle of the segment's face.
        static readonly Vector3 FaceCenter = new Vector3(-0.22f, 0.75f, 0f);

        static double _spacing;
        static bool _enabled;
        static bool _scanned;
        static double _scanAt = -1;
        static double _nextSweep;
        static long _respawnTicks;
        static readonly HashSet<ZDOID> _vines = new HashSet<ZDOID>();
        static readonly Dictionary<ZDOID, long> _harvested = new Dictionary<ZDOID, long>();
        static readonly Queue<(ZDOID id, long ticks)> _harvests = new Queue<(ZDOID, long)>();
        static readonly List<ZDOID> _ids = new List<ZDOID>();
        static readonly List<VineSegment> _segments = new List<VineSegment>();
        static int _withBerry;
        static long _given;
        static long _harvestCount;

        public string Name => "vine_berries";

        public void Install(Harmony harmony)
        {
            var raw = Environment.GetEnvironmentVariable(VineRules.Variable);
            var spacing = VineRules.ParseSpacing(raw);
            if (spacing == null)
            {
                if (!string.IsNullOrWhiteSpace(raw))
                    Plugin.Log.LogWarning($"{VineRules.Variable}={raw} ignored: expected meters between {VineRules.MinSpacing} and {VineRules.MaxSpacing}.");
                return;
            }
            if (!Patcher.Patch(harmony, typeof(ZDO), "Deserialize", new[] { typeof(ZPackage) }, typeof(VineBerries),
                    prefix: nameof(DeserializePrefix), postfix: nameof(DeserializePostfix), tag: "vine_berries"))
                return;
            _spacing = spacing.Value;
            _enabled = true;
            Plugin.Log.LogInfo($"Vine berries: Ashvine berries at least {_spacing:0.##} m apart on the same face.");
        }

        // Runs inside the RPC that receives a client's ZDO: remembers whether the segment had a berry.
        static void DeserializePrefix(ZDO __instance, out bool __state)
        {
            __state = false;
            try
            {
                if (__instance.GetPrefab() == VinePrefab)
                    __state = !__instance.GetBool(ZDOVars.s_picked, true);
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        // Berry -> picked is a harvest. Picked -> picked with a new time is the game's blocker
        // restarting the timer, which this rule ignores.
        static void DeserializePostfix(ZDO __instance, bool __state)
        {
            try
            {
                if (__instance.GetPrefab() != VinePrefab)
                    return;
                _vines.Add(__instance.m_uid);
                if (__state && __instance.GetBool(ZDOVars.s_picked, true))
                {
                    long ticks = __instance.GetLong(ZDOVars.s_pickedTime);
                    _harvests.Enqueue((__instance.m_uid, ticks > 0 ? ticks : ZNet.instance.GetTime().Ticks));
                }
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        public static void OnFrame(double now)
        {
            if (!_enabled || ZDOMan.instance == null || ZNet.instance == null || !ZNet.instance.IsServer())
                return;

            while (_harvests.Count > 0)
            {
                var (id, ticks) = _harvests.Dequeue();
                _harvested[id] = ticks;
                _harvestCount++;
                var zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null || !zdo.IsValid())
                    continue;
                // Survives restarts; ahead in revision so the owner adopts it instead of overwriting it.
                zdo.Set(HarvestKey, ticks);
                zdo.DataRevision += RevisionLead;
            }

            if (!_scanned)
            {
                if (_scanAt < 0)
                    _scanAt = now + ScanDelaySeconds;
                else if (now >= _scanAt)
                    ScanExisting();
                return;
            }

            // Without the respawn time every segment would look due.
            if (now < _nextSweep || _respawnTicks <= 0)
                return;
            _nextSweep = now + SweepSeconds;
            try
            {
                Sweep();
            }
            catch (Exception e)
            {
                Patcher.Errors++;
                Plugin.Log.LogWarning($"Vine berries: {e.Message}");
            }
        }

        // Once per boot: segments grown before the restart.
        static void ScanExisting()
        {
            _scanned = true;
            try
            {
                _respawnTicks = TimeSpan.FromMinutes(RespawnMinutes()).Ticks;
                var byId = AccessTools.Field(typeof(ZDOMan), "m_objectsByID").GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
                if (byId == null)
                    throw new MissingFieldException(nameof(ZDOMan), "m_objectsByID");
                foreach (var zdo in byId.Values)
                {
                    if (zdo.GetPrefab() != VinePrefab)
                        continue;
                    _vines.Add(zdo.m_uid);
                    long ticks = zdo.GetLong(HarvestKey);
                    if (ticks > 0 && !_harvested.ContainsKey(zdo.m_uid))
                        _harvested[zdo.m_uid] = ticks;
                }
                Plugin.Log.LogInfo($"Vine berries: {_vines.Count} Ashvine segments, {_harvested.Count} with a known harvest, respawn {RespawnMinutes():0} min.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Vine berries: initial scan failed: {e.Message}");
            }
        }

        static float RespawnMinutes()
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(VinePrefab) : null;
            var pickable = prefab != null ? prefab.GetComponent<Pickable>() : null;
            return pickable != null && pickable.m_respawnTimeMinutes > 0 ? pickable.m_respawnTimeMinutes : DefaultRespawnMinutes;
        }

        static void Sweep()
        {
            _ids.Clear();
            _segments.Clear();
            _withBerry = 0;
            var dead = new List<ZDOID>();
            foreach (var id in _vines)
            {
                var zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null || !zdo.IsValid())
                {
                    dead.Add(id);
                    continue;
                }
                var rotation = zdo.GetRotation();
                bool berry = !zdo.GetBool(ZDOVars.s_picked, true);
                if (berry)
                    _withBerry++;
                _harvested.TryGetValue(id, out long harvested);
                _ids.Add(id);
                _segments.Add(new VineSegment
                {
                    Center = ToV3(zdo.GetPosition() + rotation * FaceCenter),
                    Wall = ToV3(rotation * Vector3.right),
                    Up = ToV3(rotation * Vector3.up),
                    Along = ToV3(rotation * Vector3.forward),
                    HasBerry = berry,
                    Enabled = zdo.GetBool(ZDOVars.s_enabled, true),
                    HarvestedTicks = harvested,
                });
            }
            foreach (var id in dead)
            {
                _vines.Remove(id);
                _harvested.Remove(id);
            }

            var given = VineRules.Plan(_segments, ZNet.instance.GetTime().Ticks, _respawnTicks, _spacing);
            foreach (int i in given)
            {
                var id = _ids[i];
                var zdo = ZDOMan.instance.GetZDO(id);
                zdo.Set(ZDOVars.s_picked, false);
                zdo.DataRevision += RevisionLead;
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, id, "RPC_SetPicked", false);
            }
            _given += given.Count;
            _withBerry += given.Count;
        }

        static V3 ToV3(Vector3 v) => new V3(v.x, v.y, v.z);

        public void Write(PrometheusWriter w, double now)
        {
            if (!_enabled)
                return;
            w.Family("valheim_vine_berry_spacing_meters", "gauge", "Minimum distance between Ashvine berries on the same face.");
            w.Sample("valheim_vine_berry_spacing_meters", _spacing);
            w.Family("valheim_vine_segments", "gauge", "Ashvine segments in the world.");
            w.Sample("valheim_vine_segments", _vines.Count);
            w.Family("valheim_vine_berries", "gauge", "Ashvine segments holding a berry at the last sweep.");
            w.Sample("valheim_vine_berries", _withBerry);
            w.Family("valheim_vine_berries_given_total", "counter", "Berries the server put on Ashvine segments.");
            w.Sample("valheim_vine_berries_given_total", _given);
            w.Family("valheim_vine_harvests_total", "counter", "Ashvine berries harvested by players.");
            w.Sample("valheim_vine_harvests_total", _harvestCount);
        }
    }
}
