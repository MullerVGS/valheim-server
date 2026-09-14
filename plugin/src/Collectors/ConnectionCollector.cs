using System.Collections.Generic;
using HarmonyLib;
using Steamworks;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Collectors
{
    // O que o F2 mostra, visto do servidor. ISocket.GetConnectionQuality usa a API Steam de cliente
    // e lanca no dedicado; aqui a leitura vai direto na interface de game server.
    sealed class ConnectionCollector : ICollector
    {
        static AccessTools.FieldRef<ZSteamSocket, HSteamNetConnection> _con;
        static AccessTools.FieldRef<ZRpc, int> _sentData;
        static AccessTools.FieldRef<ZRpc, int> _recvData;

        readonly List<(PlayerState player, SteamNetConnectionRealTimeStatus_t status)> _steam =
            new List<(PlayerState, SteamNetConnectionRealTimeStatus_t)>();

        public string Name => "connection";

        public void Install(Harmony harmony)
        {
            _con = AccessTools.FieldRefAccess<ZSteamSocket, HSteamNetConnection>("m_con");
            _sentData = AccessTools.FieldRefAccess<ZRpc, int>("m_sentData");
            _recvData = AccessTools.FieldRefAccess<ZRpc, int>("m_recvData");
        }

        public void Write(PrometheusWriter w, double now)
        {
            var players = Players.Connected;

            w.Family("valheim_players_connected", "gauge", "Jogadores conectados e prontos.");
            w.Sample("valheim_players_connected", players.Count);

            _steam.Clear();
            foreach (var p in players)
            {
                if (!(p.Peer.m_socket is ZSteamSocket socket))
                    continue;
                var status = default(SteamNetConnectionRealTimeStatus_t);
                var lane = default(SteamNetConnectionRealTimeLaneStatus_t);
                if (SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(_con(socket), ref status, 0, ref lane) == EResult.k_EResultOK)
                    _steam.Add((p, status));
            }

            w.Family("valheim_player_ping_seconds", "gauge", "Ping medido pelo Steam.");
            foreach (var (p, s) in _steam)
                w.Sample("valheim_player_ping_seconds", s.m_nPing / 1000.0, p.Labels);

            w.Family("valheim_player_connection_quality_ratio", "gauge", "Fracao de pacotes entregues (1 = perfeito). side=local: servidor->jogador visto aqui; remote: visto pelo jogador.");
            foreach (var (p, s) in _steam)
            {
                w.Sample("valheim_player_connection_quality_ratio", s.m_flConnectionQualityLocal, With(p, "side", "local"));
                w.Sample("valheim_player_connection_quality_ratio", s.m_flConnectionQualityRemote, With(p, "side", "remote"));
            }

            w.Family("valheim_player_bytes_per_second", "gauge", "Taxa suavizada pelo Steam. direction=out: servidor->jogador. Teto de envio: 153600.");
            foreach (var (p, s) in _steam)
            {
                w.Sample("valheim_player_bytes_per_second", s.m_flOutBytesPerSec, With(p, "direction", "out"));
                w.Sample("valheim_player_bytes_per_second", s.m_flInBytesPerSec, With(p, "direction", "in"));
            }

            w.Family("valheim_player_packets_per_second", "gauge", "Pacotes por segundo suavizados pelo Steam.");
            foreach (var (p, s) in _steam)
            {
                w.Sample("valheim_player_packets_per_second", s.m_flOutPacketsPerSec, With(p, "direction", "out"));
                w.Sample("valheim_player_packets_per_second", s.m_flInPacketsPerSec, With(p, "direction", "in"));
            }

            w.Family("valheim_player_send_rate_limit_bytes_per_second", "gauge", "Taxa de envio que o Steam aplica a conexao.");
            foreach (var (p, s) in _steam)
                w.Sample("valheim_player_send_rate_limit_bytes_per_second", s.m_nSendRateBytesPerSecond, p.Labels);

            w.Family("valheim_player_steam_pending_bytes", "gauge", "Bytes na fila do Steam. unacked conta na fila que decide o envio de ZDO.");
            foreach (var (p, s) in _steam)
            {
                w.Sample("valheim_player_steam_pending_bytes", s.m_cbPendingReliable, With(p, "kind", "reliable"));
                w.Sample("valheim_player_steam_pending_bytes", s.m_cbPendingUnreliable, With(p, "kind", "unreliable"));
                w.Sample("valheim_player_steam_pending_bytes", s.m_cbSentUnackedReliable, With(p, "kind", "unacked"));
            }

            w.Family("valheim_player_steam_queue_seconds", "gauge", "Tempo estimado ate a fila do Steam esvaziar.");
            foreach (var (p, s) in _steam)
                w.Sample("valheim_player_steam_queue_seconds", s.m_usecQueueTime.m_SteamNetworkingMicroseconds / 1e6, p.Labels);

            w.Family("valheim_player_seconds_since_pong", "gauge", "Segundos desde o ultimo pong do ZRpc. Em 30 o servidor derruba (ZRpc timeout).");
            foreach (var p in players)
                w.Sample("valheim_player_seconds_since_pong", p.Peer.m_rpc?.GetTimeSinceLastPing() ?? 0, p.Labels);

            w.Family("valheim_player_rpc_bytes_total", "counter", "Bytes da aplicacao por jogador (ZRpc).");
            foreach (var p in players)
            {
                var rpc = p.Peer.m_rpc;
                if (rpc == null)
                    continue;
                w.Sample("valheim_player_rpc_bytes_total", (uint)_sentData(rpc), With(p, "direction", "out"));
                w.Sample("valheim_player_rpc_bytes_total", (uint)_recvData(rpc), With(p, "direction", "in"));
            }
        }

        static string[] With(PlayerState p, string key, string value) =>
            new[] { p.Labels[0], p.Labels[1], p.Labels[2], p.Labels[3], key, value };
    }
}
