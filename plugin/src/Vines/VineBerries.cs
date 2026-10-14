using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using HarmonyLib;
using UnityEngine;
using ValheimMetrics.Collectors;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Vines
{
    // Ashvine berries packed tighter than the game allows on planted vines, without a client mod
    // (VineRules). Planted = touching cultivated soil, read from each zone's TerrainComp ZDO.
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
        static readonly int SoilPrefab = "_TerrainCompiler".GetStableHashCode();
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
        // Cultivated vertices per zone and how many zones claim each one (borders are shared).
        static readonly Dictionary<ZDOID, List<long>> _soilByZone = new Dictionary<ZDOID, List<long>>();
        static readonly Dictionary<long, int> _soil = new Dictionary<long, int>();
        static readonly HashSet<ZDOID> _soilDirty = new HashSet<ZDOID>();
        static int _planted;
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
                int prefab = __instance.GetPrefab();
                if (prefab == SoilPrefab)
                    _soilDirty.Add(__instance.m_uid);
                if (prefab != VinePrefab)
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

            if (_scanned && _soilDirty.Count > 0)
            {
                foreach (var id in _soilDirty)
                    ReadSoil(id, ZDOMan.instance.GetZDO(id));
                _soilDirty.Clear();
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
                    int prefab = zdo.GetPrefab();
                    if (prefab == SoilPrefab)
                        ReadSoil(zdo.m_uid, zdo);
                    if (prefab != VinePrefab)
                        continue;
                    _vines.Add(zdo.m_uid);
                    long ticks = zdo.GetLong(HarvestKey);
                    if (ticks > 0 && !_harvested.ContainsKey(zdo.m_uid))
                        _harvested[zdo.m_uid] = ticks;
                }
                _soilDirty.Clear();
                Plugin.Log.LogInfo($"Vine berries: {_vines.Count} Ashvine segments, {_harvested.Count} with a known harvest, {_soil.Count} cultivated vertices, respawn {RespawnMinutes():0} min.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Vine berries: initial scan failed: {e.Message}");
            }
        }

        static void ReadSoil(ZDOID id, ZDO zdo)
        {
            if (_soilByZone.TryGetValue(id, out var old))
            {
                foreach (long key in old)
                    if (--_soil[key] == 0)
                        _soil.Remove(key);
                _soilByZone.Remove(id);
            }
            if (zdo == null || !zdo.IsValid())
                return;
            try
            {
                var bytes = zdo.GetByteArray(ZDOVars.s_TCData);
                if (bytes == null)
                    return;
                var center = zdo.GetPosition();
                var keys = new List<long>();
                foreach (var (x, z) in SoilMap.Cultivated(Decompress(bytes), center.x, center.z))
                {
                    long key = SoilMap.Key(x, z);
                    keys.Add(key);
                    _soil.TryGetValue(key, out int n);
                    _soil[key] = n + 1;
                }
                if (keys.Count > 0)
                    _soilByZone[id] = keys;
            }
            catch (Exception e)
            {
                Patcher.Errors++;
                Plugin.Log.LogWarning($"Vine berries: terrain {id} unreadable: {e.Message}");
            }
        }

        // Utils.Decompress: gzip.
        static byte[] Decompress(byte[] bytes)
        {
            using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
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
                var position = zdo.GetPosition();
                bool berry = !zdo.GetBool(ZDOVars.s_picked, true);
                if (berry)
                    _withBerry++;
                _harvested.TryGetValue(id, out long harvested);
                _ids.Add(id);
                _segments.Add(new VineSegment
                {
                    Center = ToV3(position + rotation * FaceCenter),
                    Wall = ToV3(rotation * Vector3.right),
                    Up = ToV3(rotation * Vector3.up),
                    Along = ToV3(rotation * Vector3.forward),
                    HasBerry = berry,
                    Enabled = zdo.GetBool(ZDOVars.s_enabled, true),
                    HarvestedTicks = harvested,
                    NearSoil = SoilMap.Near(_soil.Keys, position.x, position.z, VineRules.SoilReach),
                });
            }
            foreach (var id in dead)
            {
                _vines.Remove(id);
                _harvested.Remove(id);
            }

            _planted = 0;
            foreach (bool p in VineRules.Planted(_segments))
                if (p)
                    _planted++;
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
            w.Family("valheim_vine_planted_segments", "gauge", "Ashvine segments of planted vines (touching cultivated soil); only these get berries.");
            w.Sample("valheim_vine_planted_segments", _planted);
            w.Family("valheim_vine_cultivated_vertices", "gauge", "Cultivated terrain vertices known to the server.");
            w.Sample("valheim_vine_cultivated_vertices", _soil.Count);
            w.Family("valheim_vine_berries", "gauge", "Ashvine segments holding a berry at the last sweep.");
            w.Sample("valheim_vine_berries", _withBerry);
            w.Family("valheim_vine_berries_given_total", "counter", "Berries the server put on Ashvine segments.");
            w.Sample("valheim_vine_berries_given_total", _given);
            w.Family("valheim_vine_harvests_total", "counter", "Ashvine berries harvested by players.");
            w.Sample("valheim_vine_harvests_total", _harvestCount);
        }
    }
}
