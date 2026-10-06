using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimMetrics.Collectors;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.ChunkLoader
{
    // The dedicated server is parked outside the world (1e6, 1e6). Moving its reference position
    // to a sign enables native terrain, objects and ownership. LoadedAreas extends those paths
    // to every anchor.
    sealed class ChunkLoader : ICollector
    {
        const double CheckSeconds = 2;
        const double ScanDelaySeconds = 30;

        static readonly int SignPrefab = "sign".GetStableHashCode();
        static readonly int StampKey = "valheim-server.chunk_loader".GetStableHashCode();

        static bool _enabled;
        static readonly AnchorBook<ZDOID> _book = new AnchorBook<ZDOID>();
        static readonly Queue<ZDOID> _pending = new Queue<ZDOID>();
        static readonly List<ZDOID> _dead = new List<ZDOID>();
        static bool _scanned;
        static double _scanAt = -1;
        static double _nextCheck;
        static ZDOID _anchor = ZDOID.None;
        static Vector3 _anchorPos;
        static Vector3 _parked;
        static bool _parkedKnown;
        static int _setterLogs;
        static bool _active;
        static long _moves;

        public string Name => "chunk_loader";

        public void Install(Harmony harmony)
        {
            var raw = Environment.GetEnvironmentVariable(AnchorBook<ZDOID>.Variable);
            if (raw?.Trim() != "1")
            {
                if (!string.IsNullOrWhiteSpace(raw))
                    Plugin.Log.LogWarning($"{AnchorBook<ZDOID>.Variable}={raw} ignorado: esperado 1.");
                return;
            }
            if (!Patcher.Patch(harmony, typeof(ZDO), "Deserialize", new[] { typeof(ZPackage) }, typeof(ChunkLoader),
                    postfix: nameof(DeserializePostfix), tag: "chunk_loader"))
                return;
            // O dedicado regrava o proprio ponto durante o jogo; sem isso a gravacao dele venceria a nossa
            // em parte dos frames.
            if (!Patcher.Patch(harmony, typeof(ZNet), "SetReferencePosition", new[] { typeof(Vector3) }, typeof(ChunkLoader),
                    prefix: nameof(SetReferencePositionPrefix), tag: "chunk_loader"))
                return;
            if (!LoadedAreas.Install(harmony))
                return;
            _enabled = true;
            Plugin.Log.LogInfo("Chunk loader enabled: every sign reading \"chunkloader\" keeps its area loaded.");
        }

        // Guarda o que o jogo quer (para devolver sem placa) e, com placa, troca pelo ponto dela.
        static void SetReferencePositionPrefix(ref Vector3 __0)
        {
            if (_inside)
                return;
            if (!_parkedKnown || __0 != _parked)
            {
                if (_setterLogs < 5)
                {
                    _setterLogs++;
                    Plugin.Log.LogInfo($"Chunk loader: o jogo pos o ponto do servidor em ({__0.x:0}, {__0.z:0}):\n{Environment.StackTrace}");
                }
                _parked = __0;
                _parkedKnown = true;
            }
            if (_active)
                __0 = _anchorPos;
        }

        static bool _inside;
        internal static readonly List<Vector3> Centers = new List<Vector3>();
        static readonly Dictionary<ZDOID, Vector3> _anchors = new Dictionary<ZDOID, Vector3>();
        static readonly HashSet<Vector2s> _zones = new HashSet<Vector2s>();
        internal static bool Active => _enabled && _active;

        // Show that the sign is active while preserving the player's text and formatting.
        static void Underline(ZDO zdo, bool active)
        {
            if (zdo == null || !zdo.IsValid())
                return;
            var text = zdo.GetString(ZDOVars.s_text);
            if (!AnchorBook<ZDOID>.IsMarker(text))
                return;
            var next = AnchorBook<ZDOID>.Mark(text, active);
            if (next == null)
                return;
            zdo.Set(ZDOVars.s_text, next);
            Plugin.Log.LogInfo($"Chunk loader: placa em ({zdo.GetPosition().x:0}, {zdo.GetPosition().z:0}) {(active ? "sublinhada" : "sem sublinhado")}.");
        }

        static void Point(Vector3 pos)
        {
            _inside = true;
            try
            {
                ZNet.instance.SetReferencePosition(pos);
            }
            finally
            {
                _inside = false;
            }
        }

        // Roda dentro do RPC que recebe ZDO de cliente: so anota, a leitura acontece no frame seguinte.
        static void DeserializePostfix(ZDO __instance)
        {
            try
            {
                if (__instance.GetPrefab() == SignPrefab)
                    _pending.Enqueue(__instance.m_uid);
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

            while (_pending.Count > 0)
            {
                var zdo = ZDOMan.instance.GetZDO(_pending.Dequeue());
                try
                {
                    if (zdo != null && zdo.IsValid())
                        Observe(zdo);
                }
                catch
                {
                    Patcher.Errors++;
                }
            }

            if (!_scanned)
            {
                if (_scanAt < 0)
                    _scanAt = now + ScanDelaySeconds;
                else if (now >= _scanAt)
                    ScanExisting();
            }

            if (now >= _nextCheck)
            {
                _nextCheck = now + CheckSeconds;
                try
                {
                    Refresh();
                }
                catch (Exception e)
                {
                    Patcher.Errors++;
                    Plugin.Log.LogWarning($"Chunk loader: {e.Message}");
                }
            }

            if (_active)
                Point(_anchorPos);
        }

        static void Observe(ZDO zdo)
        {
            long stored = zdo.GetLong(StampKey);
            long stamp = _book.Observe(zdo.m_uid, zdo.GetString(ZDOVars.s_text), stored, ZNet.instance.GetTime().Ticks);
            if (stamp > 0 && stamp != stored)
                zdo.Set(StampKey, stamp);
            else if (stamp == 0 && stored != 0)
                zdo.RemoveLong(StampKey);
        }

        // Uma vez por boot: placas escritas antes do restart ou com o plugin desligado.
        static void ScanExisting()
        {
            _scanned = true;
            try
            {
                var byId = AccessTools.Field(typeof(ZDOMan), "m_objectsByID").GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
                if (byId == null)
                    throw new MissingFieldException(nameof(ZDOMan), "m_objectsByID");
                var signs = new List<ZDO>();
                foreach (var zdo in byId.Values)
                    if (zdo.GetPrefab() == SignPrefab)
                        signs.Add(zdo);
                foreach (var zdo in signs)
                    Observe(zdo);
                Plugin.Log.LogInfo($"Chunk loader: {signs.Count} placas no mundo, {_book.Count} escritas \"chunkloader\".");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Chunk loader: varredura inicial falhou: {e.Message}");
            }
        }

        static void Refresh()
        {
            _dead.Clear();
            foreach (var id in _book.Ids)
            {
                var zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null || !zdo.IsValid())
                    _dead.Add(id);
            }
            foreach (var id in _dead)
                _book.Forget(id);

            bool picked = _book.TryPick(out var anchor);
            _anchors.Clear();
            _zones.Clear();
            Centers.Clear();
            foreach (var id in _book.Ids)
            {
                var zdo = ZDOMan.instance.GetZDO(id);
                Underline(zdo, true);
                var pos = zdo.GetPosition();
                _anchors[id] = pos;
                if (_zones.Add(ZoneSystem.GetZone(pos)))
                    Centers.Add(pos);
            }

            if (picked)
            {
                var pos = ZDOMan.instance.GetZDO(anchor).GetPosition();
                if (!_active && !_parkedKnown)
                {
                    _parked = ZNet.instance.GetReferencePosition();
                    _parkedKnown = true;
                }
                if (!_active || anchor != _anchor)
                {
                    _moves++;
                    var zone = ZoneSystem.GetZone(pos);
                    Plugin.Log.LogInfo($"Chunk loader em ({pos.x:0}, {pos.z:0}), zona {zone.x},{zone.y}.");
                }
                _anchor = anchor;
                _anchorPos = pos;
                _active = true;
            }
            else if (_active)
            {
                _active = false;
                _anchor = ZDOID.None;
                Point(_parked);
                Plugin.Log.LogInfo($"Chunk loader sem placa: o ponto do servidor volta a ({_parked.x:0}, {_parked.z:0}).");
            }
        }

        public void Write(PrometheusWriter w, double now)
        {
            // Vale com o loader desligado tambem: mostra o que o servidor simula por conta propria.
            if (ZNetScene.instance != null)
            {
                w.Family("valheim_server_instances", "gauge", "Objects instantiated by the server across all loaded areas.");
                w.Sample("valheim_server_instances", ZNetScene.instance.NrOfInstances());
                int wild = 0, tamed = 0;
                foreach (var c in Character.GetAllCharacters())
                {
                    if (c == null || c.IsPlayer() || !c.IsOwner())
                        continue;
                    if (c.IsTamed())
                        tamed++;
                    else
                        wild++;
                }
                w.Family("valheim_server_characters", "gauge", "Criaturas que o proprio servidor esta simulando.");
                w.Sample("valheim_server_characters", wild, "kind", "wild");
                w.Sample("valheim_server_characters", tamed, "kind", "tamed");
            }
            if (ZNet.instance != null)
            {
                var refPos = ZNet.instance.GetReferencePosition();
                w.Family("valheim_server_reference_position_meters", "gauge", "Primary server reference point (newest sign).");
                w.Sample("valheim_server_reference_position_meters", refPos.x, "axis", "x");
                w.Sample("valheim_server_reference_position_meters", refPos.z, "axis", "z");
                var sim = ZNet.instance.GetSyncedSimulationDistance();
                w.Family("valheim_server_simulation_distance_zones", "gauge", "Distancia de simulacao do servidor em zonas (near = terreno e objetos, far = objetos distantes).");
                w.Sample("valheim_server_simulation_distance_zones", sim.NearSimulationDistance, "part", "near");
                w.Sample("valheim_server_simulation_distance_zones", sim.FarSimulationDistance, "part", "far");
                w.Family("valheim_server_simulation_classic", "gauge", "1 se a area e quadrada (modo classico); 0 = recorte redondo.");
                w.Sample("valheim_server_simulation_classic", sim.IsClassic ? 1 : 0);
            }

            if (!_enabled)
                return;
            w.Family("valheim_chunk_loader_active", "gauge", "1 if at least one \"chunkloader\" sign keeps an area active.");
            w.Sample("valheim_chunk_loader_active", _active ? 1 : 0);
            w.Family("valheim_chunk_loader_signs", "gauge", "Signs reading \"chunkloader\" (all keep their areas active).");
            w.Sample("valheim_chunk_loader_signs", _book.Count);
            w.Family("valheim_chunk_loader_areas", "gauge", "Distinct zone centers kept active by signs.");
            w.Sample("valheim_chunk_loader_areas", Centers.Count);
            w.Family("valheim_chunk_loader_anchor_position_meters", "gauge", "Position of each active sign, in meters.");
            foreach (var anchor in _anchors)
            {
                var id = anchor.Key.ToString();
                w.Sample("valheim_chunk_loader_anchor_position_meters", anchor.Value.x, "anchor", id, "axis", "x");
                w.Sample("valheim_chunk_loader_anchor_position_meters", anchor.Value.z, "anchor", id, "axis", "z");
            }
            w.Family("valheim_chunk_loader_moves_total", "counter", "Primary reference sign changes.");
            w.Sample("valheim_chunk_loader_moves_total", _moves);
        }
    }
}
