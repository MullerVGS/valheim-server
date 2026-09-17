using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using ValheimMetrics.Exposition;
using ValheimMetrics.Tuning;

namespace ValheimMetrics.Collectors
{
    // ZDOMan.SendZDOs so envia se sobram 2048 bytes do limite (10240 no jogo, ajustavel em
    // ServerTuning): fila acima disso pula o ciclo daquele jogador. GetSendQueueSize so e chamado
    // de dentro de SendZDOs.
    sealed class ThrottleCollector : ICollector
    {
        const int MinAvailableBytes = 2048;

        static AccessTools.FieldRef<object, ZNetPeer> _peerOf;
        static AccessTools.FieldRef<ZDOMan, int> _zdosSent;
        static AccessTools.FieldRef<ZDOMan, int> _zdosRecv;
        static PlayerState _sending;
        static PlayerState _receiving;

        public string Name => "throttle";

        public void Install(Harmony harmony)
        {
            var zdoPeer = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer")
                ?? throw new MissingMemberException("ZDOMan", "ZDOPeer");
            _peerOf = AccessTools.FieldRefAccess<ZNetPeer>(zdoPeer, "m_peer");
            _zdosSent = AccessTools.FieldRefAccess<ZDOMan, int>("m_zdosSent");
            _zdosRecv = AccessTools.FieldRefAccess<ZDOMan, int>("m_zdosRecv");

            var self = typeof(ThrottleCollector);
            Patcher.Patch(harmony, typeof(ZDOMan), "SendZDOs", new[] { zdoPeer, typeof(bool) }, self,
                nameof(SendZDOsPrefix), nameof(SendZDOsPostfix));
            Patcher.Patch(harmony, typeof(ZSteamSocket), "GetSendQueueSize", Type.EmptyTypes, self,
                postfix: nameof(QueuePostfix));
            Patcher.Patch(harmony, typeof(ZDOMan), "RPC_ZDOData", new[] { typeof(ZRpc), typeof(ZPackage) }, self,
                nameof(ZdoDataPrefix), nameof(ZdoDataPostfix));
        }

        static void SendZDOsPrefix(ZDOMan __instance, object __0, out int __state)
        {
            __state = 0;
            try
            {
                __state = _zdosSent(__instance);
                var player = Players.ForPeer(_peerOf(__0));
                if (player == null)
                    return;
                double now = Time.realtimeSinceStartupAsDouble;
                if (player.LastCycle >= 0)
                    player.CycleInterval.Observe(now - player.LastCycle);
                player.LastCycle = now;
                player.SendCycles++;
                _sending = player;
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        static void SendZDOsPostfix(ZDOMan __instance, int __state)
        {
            try
            {
                if (_sending != null)
                    _sending.ZdosSent += _zdosSent(__instance) - __state;
            }
            catch
            {
                Patcher.Errors++;
            }
            _sending = null;
        }

        static void QueuePostfix(int __result)
        {
            var player = _sending;
            if (player == null)
                return;
            player.QueueAtDecision = __result;
            player.QueueMax.Add(Time.realtimeSinceStartupAsDouble, __result);
            if (__result > ServerTuning.SendLimitBytes - MinAvailableBytes)
                player.SendCyclesThrottled++;
        }

        static void ZdoDataPrefix(ZDOMan __instance, ZRpc __0, ZPackage __1, out int __state)
        {
            __state = 0;
            _receiving = null;
            try
            {
                __state = _zdosRecv(__instance);
                foreach (var peer in ZNet.instance.GetPeers())
                {
                    if (peer.m_rpc != __0)
                        continue;
                    var player = Players.ForPeer(peer);
                    if (player == null)
                        return;
                    player.ZdoDataBytes += __1.Size();
                    player.LastZdoData = Time.realtimeSinceStartupAsDouble;
                    _receiving = player;
                    return;
                }
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        static void ZdoDataPostfix(ZDOMan __instance, int __state)
        {
            try
            {
                if (_receiving != null)
                    _receiving.ZdosReceived += _zdosRecv(__instance) - __state;
            }
            catch
            {
                Patcher.Errors++;
            }
            _receiving = null;
        }

        static bool TryReadGlobalInt(ESteamNetworkingConfigValue key, out int value)
        {
            value = 0;
            var buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                ulong size = sizeof(int);
                var result = SteamGameServerNetworkingUtils.GetConfigValue(key,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, out _, buffer, ref size);
                if ((int)result <= 0)
                    return false;
                value = Marshal.ReadInt32(buffer);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Write(PrometheusWriter w, double now)
        {
            var players = Players.Connected;

            w.Family("valheim_zdo_send_cycles_total", "counter", "Vezes que o servidor tentou mandar ZDOs ao jogador.");
            foreach (var p in players)
                w.Sample("valheim_zdo_send_cycles_total", p.SendCycles, p.Labels);

            w.Family("valheim_zdo_send_limit_bytes", "gauge", "Limite de bytes por ciclo de envio de ZDO (10240 no jogo).");
            w.Sample("valheim_zdo_send_limit_bytes", ServerTuning.SendLimitBytes);

            // Lido do Steam, nao do ajuste: prova que o valor pegou. Antes do GameServer subir nao ha leitura.
            if (TryReadGlobalInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, out var rateMin)
                && TryReadGlobalInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, out var rateMax))
            {
                w.Family("valheim_steam_send_rate_bytes_per_second", "gauge", "Taxa de envio global do Steam por conexao (153600 no jogo; 262144 e o padrao do Steam, antes do servidor abrir).");
                w.Sample("valheim_steam_send_rate_bytes_per_second", rateMin, "bound", "min");
                w.Sample("valheim_steam_send_rate_bytes_per_second", rateMax, "bound", "max");
            }

            w.Family("valheim_zdo_send_cycles_throttled_total", "counter", "Ciclos pulados porque a fila passou do limite menos 2048 bytes.");
            foreach (var p in players)
                w.Sample("valheim_zdo_send_cycles_throttled_total", p.SendCyclesThrottled, p.Labels);

            w.Family("valheim_zdo_send_queue_bytes", "gauge", "Fila vista no ultimo ciclo de envio.");
            foreach (var p in players)
                w.Sample("valheim_zdo_send_queue_bytes", p.QueueAtDecision, p.Labels);

            w.Family("valheim_zdo_send_queue_max_bytes", "gauge", "Maior fila vista nos ultimos 5s.");
            foreach (var p in players)
                w.Sample("valheim_zdo_send_queue_max_bytes", p.QueueMax.Max(now), p.Labels);

            w.Family("valheim_zdo_send_cycle_interval_seconds", "histogram", "Intervalo entre ciclos de envio ao jogador. Minimo de 50ms; FPS baixo do servidor alonga.");
            foreach (var p in players)
                w.Histogram("valheim_zdo_send_cycle_interval_seconds", p.CycleInterval, p.Labels);

            w.Family("valheim_zdos_sent_total", "counter", "ZDOs enviados ao jogador.");
            foreach (var p in players)
                w.Sample("valheim_zdos_sent_total", p.ZdosSent, p.Labels);

            w.Family("valheim_zdos_received_total", "counter", "ZDOs recebidos do jogador (o que o cliente dele simula e sobe).");
            foreach (var p in players)
                w.Sample("valheim_zdos_received_total", p.ZdosReceived, p.Labels);

            w.Family("valheim_zdo_data_received_bytes_total", "counter", "Bytes de ZDOData recebidos do jogador.");
            foreach (var p in players)
                w.Sample("valheim_zdo_data_received_bytes_total", p.ZdoDataBytes, p.Labels);

            w.Family("valheim_player_seconds_since_zdo_data", "gauge", "Segundos desde o ultimo ZDOData do jogador. Cresce se o cliente dele engasga.");
            foreach (var p in players)
                w.Sample("valheim_player_seconds_since_zdo_data", p.LastZdoData < 0 ? 0 : now - p.LastZdoData, p.Labels);
        }
    }
}
