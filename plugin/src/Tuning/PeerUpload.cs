using System.Collections.Generic;
using HarmonyLib;
using Steamworks;

namespace ValheimMetrics.Tuning
{
    // Bytes/s que o servidor recebe de cada jogador, suavizados pelo Steam: e o upload do cliente.
    // A varredura de dono roda para cada jogador a cada 2 s; uma leitura por segundo basta.
    static class PeerUpload
    {
        const double RefreshSeconds = 1;

        static AccessTools.FieldRef<ZSteamSocket, HSteamNetConnection> _con;
        static readonly Dictionary<long, (double at, float bytes)> _cache = new Dictionary<long, (double, float)>();

        // Sem leitura (socket que nao e Steam, conexao fechando) = null, e ninguem perde criatura.
        public static float? InBytes(ZNetPeer peer, double now)
        {
            if (peer == null)
                return null;
            if (_cache.TryGetValue(peer.m_uid, out var cached) && now - cached.at < RefreshSeconds)
                return cached.bytes < 0 ? (float?)null : cached.bytes;

            float bytes = -1;
            if (peer.m_socket is ZSteamSocket socket)
            {
                _con ??= AccessTools.FieldRefAccess<ZSteamSocket, HSteamNetConnection>("m_con");
                var status = default(SteamNetConnectionRealTimeStatus_t);
                var lane = default(SteamNetConnectionRealTimeLaneStatus_t);
                if (SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(_con(socket), ref status, 0, ref lane) == EResult.k_EResultOK)
                    bytes = status.m_flInBytesPerSec;
            }
            if (_cache.Count > 256)
                _cache.Clear();
            _cache[peer.m_uid] = (now, bytes);
            return bytes < 0 ? (float?)null : bytes;
        }
    }
}
