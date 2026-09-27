using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;
using ValheimMetrics.Collectors;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Traffic
{
    // Quanto cada ZDO custa na rede. Enviado: ZDO.Serialize so roda em ZDOMan.SendZDOs, uma vez por
    // ZDO por jogador que o recebe, num pacote limpo; o cabecalho fixo em volta (id, revisoes, dono,
    // posicao, tamanho) soma 42 bytes. Recebido: ZDO.Deserialize so roda para revisao aceita de dono;
    // o tamanho e lido antes e o prefab depois, porque ZDO novo so ganha prefab dentro do Deserialize.
    // Por prefab vira contador; os ZDOs e as zonas de 64 m mais caros de cada janela vao para o log.
    sealed class TrafficCollector : ICollector
    {
        const int HeaderBytes = 12 + 2 + 4 + 8 + 12 + 4;
        const double WindowSeconds = 300;
        const int TopZdos = 10;
        const int TopZones = 5;

        static readonly TrafficBook Book = new TrafficBook();
        static readonly Dictionary<int, string> Names = new Dictionary<int, string>();
        static double _windowStart = -1;

        public string Name => "zdo_traffic";

        public void Install(Harmony harmony)
        {
            var self = typeof(TrafficCollector);
            Patcher.Patch(harmony, typeof(ZDO), "Serialize", new[] { typeof(ZPackage) }, self,
                postfix: nameof(SerializePostfix), tag: "traffic");
            Patcher.Patch(harmony, typeof(ZDO), "Deserialize", new[] { typeof(ZPackage) }, self,
                prefix: nameof(DeserializePrefix), postfix: nameof(DeserializePostfix), tag: "traffic");
        }

        static void DeserializePrefix(ZPackage pkg, out int __state)
        {
            try
            {
                __state = pkg.Size();
            }
            catch
            {
                __state = -1;
                Patcher.Errors++;
            }
        }

        static void DeserializePostfix(ZDO __instance, int __state)
        {
            if (__state >= 0)
                Count(__instance, __state, sent: false);
        }

        static void SerializePostfix(ZDO __instance, ZPackage pkg) => Count(__instance, pkg.Size(), sent: true);

        static void Count(ZDO zdo, int size, bool sent)
        {
            try
            {
                var pos = zdo.GetPosition();
                Book.Add(new ZdoKey(zdo.m_uid.UserID, zdo.m_uid.ID), zdo.GetPrefab(), pos.x, pos.z, zdo.GetOwner(),
                    size + HeaderBytes, sent);
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        public void Write(PrometheusWriter w, double now)
        {
            if (_windowStart < 0)
                _windowStart = now;
            else if (now - _windowStart >= WindowSeconds)
            {
                LogWindow(Book.Take(TopZdos, TopZones), now - _windowStart);
                _windowStart = now;
            }

            w.Family("valheim_zdo_traffic_bytes_total", "counter",
                "Bytes de ZDO por prefab: sent = servidor para jogadores (uma vez por destinatario), received = donos para o servidor.");
            foreach (var kv in Book.ByPrefab)
            {
                var prefab = NameOf(kv.Key);
                w.Sample("valheim_zdo_traffic_bytes_total", kv.Value.SentBytes, "prefab", prefab, "direction", "sent");
                w.Sample("valheim_zdo_traffic_bytes_total", kv.Value.ReceivedBytes, "prefab", prefab, "direction", "received");
            }
            w.Family("valheim_zdo_traffic_updates_total", "counter", "Envios e recebimentos de ZDO por prefab.");
            foreach (var kv in Book.ByPrefab)
            {
                var prefab = NameOf(kv.Key);
                w.Sample("valheim_zdo_traffic_updates_total", kv.Value.SentUpdates, "prefab", prefab, "direction", "sent");
                w.Sample("valheim_zdo_traffic_updates_total", kv.Value.ReceivedUpdates, "prefab", prefab, "direction", "received");
            }
            w.Family("valheim_zdo_traffic_window_zdos", "gauge", "ZDOs distintos que passaram pela rede na janela atual.");
            w.Sample("valheim_zdo_traffic_window_zdos", Book.WindowZdos);
        }

        static void LogWindow(TrafficBook.Window window, double seconds)
        {
            if (window.Zdos == 0)
                return;
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(string.Format(inv, "Trafego de ZDO em {0:0}s: {1} ZDOs, enviado {2:0.0} KB/s, recebido {3:0.0} KB/s.",
                seconds, window.Zdos, window.Total.SentBytes / 1024.0 / seconds, window.Total.ReceivedBytes / 1024.0 / seconds));
            sb.Append("\n  ZDOs mais caros:");
            foreach (var hot in window.TopZdos)
                sb.Append(string.Format(inv, "\n    {0} ({1:0},{2:0}) dono {3}: {4:0.00} KB/s, {5} envios, {6} recebidos",
                    NameOf(hot.Prefab), hot.X, hot.Z, OwnerName(hot.Owner), hot.Tally.Bytes / 1024.0 / seconds,
                    hot.Tally.SentUpdates, hot.Tally.ReceivedUpdates));
            sb.Append("\n  Zonas de 64 m mais caras:");
            foreach (var zone in window.TopZones)
            {
                var prefabs = new List<KeyValuePair<int, long>>(zone.BytesByPrefab);
                prefabs.Sort((a, b) => b.Value.CompareTo(a.Value));
                var parts = new List<string>();
                for (int i = 0; i < prefabs.Count && i < 3; i++)
                    parts.Add(string.Format(inv, "{0} {1:0}%", NameOf(prefabs[i].Key), 100.0 * prefabs[i].Value / Math.Max(1, zone.Tally.Bytes)));
                sb.Append(string.Format(inv, "\n    ({0:0},{1:0}): {2:0.00} KB/s, {3} ZDOs; {4}",
                    zone.Zone.CenterX, zone.Zone.CenterZ, zone.Tally.Bytes / 1024.0 / seconds, zone.Zdos, string.Join(", ", parts)));
            }
            Plugin.Log.LogInfo(sb.ToString());
        }

        static string OwnerName(long uid)
        {
            if (uid == 0)
                return "ninguem";
            if (ZNet.instance != null && uid == ZDOMan.GetSessionID())
                return "servidor";
            var player = Players.ForUid(uid);
            return player != null && player.Name.Length > 0 ? player.Name : uid.ToString(CultureInfo.InvariantCulture);
        }

        static string NameOf(int prefab)
        {
            if (Names.TryGetValue(prefab, out var name))
                return name;
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            if (go == null)
                return prefab.ToString(CultureInfo.InvariantCulture);
            Names[prefab] = go.name;
            return go.name;
        }
    }
}
