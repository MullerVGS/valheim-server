using System;
using System.Diagnostics;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using ValheimMetrics.Collectors;
using ValheimMetrics.Exposition;

namespace ValheimMetrics
{
    [BepInPlugin(Guid, "Valheim Metrics", Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "valheim-server.metrics";
        public const string Version = "0.1.0";
        const int DefaultPort = 9780;

        internal static ManualLogSource Log;

        readonly PrometheusWriter _writer = new PrometheusWriter();
        ICollector[] _collectors;
        bool[] _installed;
        double[] _collectSeconds;
        long[] _collectErrors;
        MetricsServer _server;
        Harmony _harmony;
        double _nextSnapshot;

        void Awake()
        {
            Log = Logger;
            if (!Application.isBatchMode)
            {
                Log.LogInfo("Nao e servidor dedicado; exporter desligado.");
                return;
            }

            _harmony = new Harmony(Guid);
            _collectors = new ICollector[]
            {
                new ConnectionCollector(),
                new ThrottleCollector(),
                new OwnershipCollector(),
                new EventCollector(),
                new ServerCollector(),
                new RpcCollector(),
            };
            _installed = new bool[_collectors.Length];
            _collectSeconds = new double[_collectors.Length];
            _collectErrors = new long[_collectors.Length];

            for (int i = 0; i < _collectors.Length; i++)
            {
                try
                {
                    _collectors[i].Install(_harmony);
                    _installed[i] = true;
                }
                catch (Exception e)
                {
                    Log.LogWarning($"Coletor {_collectors[i].Name} desligado: {e}");
                }
            }

            var port = ReadPort();
            _server = new MetricsServer(port, Log);
            _server.Start();

            // O GameObject do BepInEx nao sobrevive a troca de cena no Valheim; o nosso sim.
            var host = new GameObject("ValheimMetrics");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<Driver>().Plugin = this;

            Log.LogInfo($"Exporter em :{port}/metrics");
        }

        void OnDestroy()
        {
            _server?.Stop();
            _harmony?.UnpatchSelf();
        }

        internal void Tick()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            ServerCollector.OnFrame(now, Time.unscaledDeltaTime);
            if (now < _nextSnapshot)
                return;
            _nextSnapshot = now + 1;
            Snapshot(now);
        }

        void Snapshot(double now)
        {
            _writer.Reset();
            try
            {
                Players.Refresh();
            }
            catch (Exception e)
            {
                Log.LogWarning($"Players.Refresh: {e.Message}");
            }

            for (int i = 0; i < _collectors.Length; i++)
            {
                if (!_installed[i])
                    continue;
                long t0 = Stopwatch.GetTimestamp();
                try
                {
                    _collectors[i].Write(_writer, now);
                }
                catch (Exception e)
                {
                    if (_collectErrors[i]++ % 60 == 0)
                        Log.LogWarning($"Coletor {_collectors[i].Name}: {e}");
                }
                _collectSeconds[i] = (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
            }

            WriteSelf();
            _server.Publish(_writer.ToString());
        }

        void WriteSelf()
        {
            var w = _writer;
            w.Family("valheim_exporter_info", "gauge", "Versoes do exporter, do jogo e do BepInEx.");
            w.Sample("valheim_exporter_info", 1,
                "version", Version,
                "game_version", global::Version.GetVersionString(false),
                "bepinex_version", typeof(BaseUnityPlugin).Assembly.GetName().Version.ToString());

            w.Family("valheim_exporter_collector_ok", "gauge", "1 se o coletor instalou.");
            for (int i = 0; i < _collectors.Length; i++)
                w.Sample("valheim_exporter_collector_ok", _installed[i] ? 1 : 0, "collector", _collectors[i].Name);

            w.Family("valheim_exporter_patch_ok", "gauge", "1 se o patch no metodo do jogo aplicou. 0 = membro mudou numa atualizacao.");
            foreach (var kv in Patcher.Status)
                w.Sample("valheim_exporter_patch_ok", kv.Value ? 1 : 0, "target", kv.Key);

            w.Family("valheim_exporter_patch_errors_total", "counter", "Excecoes engolidas dentro dos patches (o jogo segue).");
            w.Sample("valheim_exporter_patch_errors_total", Patcher.Errors);

            w.Family("valheim_exporter_collect_seconds", "gauge", "Custo do ultimo snapshot por coletor, na thread principal.");
            for (int i = 0; i < _collectors.Length; i++)
                w.Sample("valheim_exporter_collect_seconds", _collectSeconds[i], "collector", _collectors[i].Name);

            w.Family("valheim_exporter_collect_errors_total", "counter", "Excecoes ao montar o snapshot por coletor.");
            for (int i = 0; i < _collectors.Length; i++)
                w.Sample("valheim_exporter_collect_errors_total", _collectErrors[i], "collector", _collectors[i].Name);
        }

        static int ReadPort()
        {
            var raw = Environment.GetEnvironmentVariable("VALHEIM_METRICS_PORT");
            return int.TryParse(raw, out var port) && port > 0 ? port : DefaultPort;
        }
    }

    sealed class Driver : MonoBehaviour
    {
        internal Plugin Plugin;

        void Update() => Plugin.Tick();
    }
}
