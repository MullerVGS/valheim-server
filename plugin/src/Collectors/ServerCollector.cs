using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Collectors
{
    // Saude da thread principal: frame time, tempo por subsistema e as travadas conhecidas
    // (Sleep(100) em cada desconexao, PrepareSave do autosave, UnloadUnusedAssets horario).
    // Subsistemas se aninham (RPC_ZDOData roda dentro de ZNet.Update): nao somar.
    sealed class ServerCollector : ICollector
    {
        sealed class Subsystem
        {
            public readonly string Name;
            public double Seconds;
            public long Calls;
            public readonly WindowMax Max = new WindowMax(5);

            public Subsystem(string name) => Name = name;

            public void Stop(long start)
            {
                double s = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
                Seconds += s;
                Calls++;
                Max.Add(Time.realtimeSinceStartupAsDouble, s);
            }
        }

        static readonly Histogram Frames = new Histogram(new[] { 0.005, 0.01, 0.02, 0.033, 0.05, 0.1, 0.2, 0.5, 1, 2 });
        static readonly WindowMax FrameMax = new WindowMax(5);

        static readonly Subsystem ZNetUpdate = new Subsystem("znet_update");
        static readonly Subsystem ZdoManUpdate = new Subsystem("zdoman_update");
        static readonly Subsystem ZoneSystemUpdate = new Subsystem("zonesystem_update");
        static readonly Subsystem ZNetSceneUpdate = new Subsystem("znetscene_update");
        static readonly Subsystem RandEvents = new Subsystem("randevent_fixedupdate");
        static readonly Subsystem ZdoData = new Subsystem("zdo_data_rpc");
        static readonly Subsystem SpawnZone = new Subsystem("spawn_zone");
        static readonly Subsystem Disconnect = new Subsystem("disconnect");
        static readonly Subsystem SaveWorld = new Subsystem("save_world");
        static readonly Subsystem CollectResources = new Subsystem("collect_resources");
        static readonly Subsystem[] All =
        {
            ZNetUpdate, ZdoManUpdate, ZoneSystemUpdate, ZNetSceneUpdate, RandEvents,
            ZdoData, SpawnZone, Disconnect, SaveWorld, CollectResources,
        };

        static readonly Dictionary<(ESteamNetworkingConnectionState, int), long> StateChanges =
            new Dictionary<(ESteamNetworkingConnectionState, int), long>();

        public string Name => "server";

        public static void OnFrame(double now, float dt)
        {
            Frames.Observe(dt);
            FrameMax.Add(now, dt);
        }

        public void Install(Harmony harmony)
        {
            var self = typeof(ServerCollector);
            Patcher.Patch(harmony, typeof(ZNet), "Update", Type.EmptyTypes, self, nameof(Start), nameof(StopZNetUpdate));
            Patcher.Patch(harmony, typeof(ZDOMan), "Update", new[] { typeof(float) }, self, nameof(Start), nameof(StopZdoManUpdate));
            Patcher.Patch(harmony, typeof(ZoneSystem), "Update", Type.EmptyTypes, self, nameof(Start), nameof(StopZoneSystemUpdate));
            Patcher.Patch(harmony, typeof(ZNetScene), "Update", Type.EmptyTypes, self, nameof(Start), nameof(StopZNetSceneUpdate));
            Patcher.Patch(harmony, typeof(RandEventSystem), "FixedUpdate", Type.EmptyTypes, self, nameof(Start), nameof(StopRandEvents));
            Patcher.Patch(harmony, typeof(ZDOMan), "RPC_ZDOData", new[] { typeof(ZRpc), typeof(ZPackage) }, self, nameof(Start), nameof(StopZdoData));
            Patcher.Patch(harmony, typeof(ZoneSystem), "SpawnZone", null, self, nameof(Start), nameof(StopSpawnZone));
            Patcher.Patch(harmony, typeof(ZNet), "Disconnect", new[] { typeof(ZNetPeer) }, self, nameof(Start), nameof(StopDisconnect));
            Patcher.Patch(harmony, typeof(ZNet), "SaveWorld", new[] { typeof(bool) }, self, nameof(Start), nameof(StopSaveWorld));
            Patcher.Patch(harmony, typeof(Game), "CollectResources", new[] { typeof(bool) }, self, nameof(Start), nameof(StopCollectResources));
            Patcher.Patch(harmony, typeof(ZSteamSocket), "OnStatusChanged", null, self, postfix: nameof(StatusChanged));
        }

        static void Start(out long __state) => __state = Stopwatch.GetTimestamp();
        static void StopZNetUpdate(long __state) => ZNetUpdate.Stop(__state);
        static void StopZdoManUpdate(long __state) => ZdoManUpdate.Stop(__state);
        static void StopZoneSystemUpdate(long __state) => ZoneSystemUpdate.Stop(__state);
        static void StopZNetSceneUpdate(long __state) => ZNetSceneUpdate.Stop(__state);
        static void StopRandEvents(long __state) => RandEvents.Stop(__state);
        static void StopZdoData(long __state) => ZdoData.Stop(__state);
        static void StopSpawnZone(long __state) => SpawnZone.Stop(__state);
        static void StopDisconnect(long __state) => Disconnect.Stop(__state);
        static void StopSaveWorld(long __state) => SaveWorld.Stop(__state);
        static void StopCollectResources(long __state) => CollectResources.Stop(__state);

        static void StatusChanged(SteamNetConnectionStatusChangedCallback_t __0)
        {
            try
            {
                var key = (__0.m_info.m_eState, __0.m_info.m_eEndReason);
                StateChanges[key] = (StateChanges.TryGetValue(key, out var n) ? n : 0) + 1;
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        public void Write(PrometheusWriter w, double now)
        {
            w.Family("valheim_server_frame_seconds", "histogram", "Tempo de frame da thread principal do servidor.");
            w.Histogram("valheim_server_frame_seconds", Frames);

            w.Family("valheim_server_frame_max_seconds", "gauge", "Pior frame nos ultimos 5s.");
            w.Sample("valheim_server_frame_max_seconds", FrameMax.Max(now));

            w.Family("valheim_server_target_frame_rate", "gauge", "Application.targetFrameRate (-1 = sem limite).");
            w.Sample("valheim_server_target_frame_rate", Application.targetFrameRate);

            w.Family("valheim_server_subsystem_seconds_total", "counter", "Tempo gasto por subsistema na thread principal. Aninhados: nao somar.");
            foreach (var s in All)
                w.Sample("valheim_server_subsystem_seconds_total", s.Seconds, "subsystem", s.Name);

            w.Family("valheim_server_subsystem_calls_total", "counter", "Chamadas por subsistema.");
            foreach (var s in All)
                w.Sample("valheim_server_subsystem_calls_total", s.Calls, "subsystem", s.Name);

            w.Family("valheim_server_subsystem_max_seconds", "gauge", "Pior chamada por subsistema nos ultimos 5s.");
            foreach (var s in All)
                w.Sample("valheim_server_subsystem_max_seconds", s.Max.Max(now), "subsystem", s.Name);

            var net = ZNet.instance;
            w.Family("valheim_world_saving", "gauge", "1 enquanto o save do mundo roda.");
            w.Sample("valheim_world_saving", net != null && net.IsSaving() ? 1 : 0);

            w.Family("valheim_world_last_save_main_thread_seconds", "gauge", "Parte do ultimo save que travou a thread principal (PrepareSave).");
            w.Sample("valheim_world_last_save_main_thread_seconds",
                net != null && net.SaveThreadStartTime >= net.SaveStartTime ? net.SaveThreadStartTime - net.SaveStartTime : 0);

            w.Family("valheim_world_seconds_until_autosave", "gauge", "Segundos ate o proximo autosave.");
            w.Sample("valheim_world_seconds_until_autosave", Game.instance != null ? Game.m_saveInterval - Game.instance.m_saveTimer : 0);

            w.Family("valheim_server_gc_collections_total", "counter", "Coletas do GC por geracao.");
            for (int gen = 0; gen <= GC.MaxGeneration; gen++)
                w.Sample("valheim_server_gc_collections_total", GC.CollectionCount(gen), "generation", gen.ToString());

            w.Family("valheim_server_managed_heap_bytes", "gauge", "Heap gerenciado em uso (GC.GetTotalMemory).");
            w.Sample("valheim_server_managed_heap_bytes", GC.GetTotalMemory(false));

            w.Family("valheim_connection_state_changes_total", "counter", "Transicoes de conexao do Steam, com o motivo de fim (k_ESteamNetConnectionEnd_*).");
            foreach (var kv in StateChanges)
                w.Sample("valheim_connection_state_changes_total", kv.Value,
                    "state", StateName(kv.Key.Item1), "end_reason", kv.Key.Item2.ToString());
        }

        static string StateName(ESteamNetworkingConnectionState state)
        {
            var name = state.ToString();
            const string prefix = "k_ESteamNetworkingConnectionState_";
            return name.StartsWith(prefix, StringComparison.Ordinal) ? name.Substring(prefix.Length) : name;
        }
    }
}
