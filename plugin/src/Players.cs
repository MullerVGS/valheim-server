using System.Collections.Generic;
using ValheimMetrics.Exposition;

namespace ValheimMetrics
{
    // Contadores sobrevivem a reconexao: a chave estavel e o SteamID, nao o uid da sessao.
    sealed class PlayerState
    {
        public readonly string SteamId;
        public string Name = "";
        public string[] Labels;
        public ZNetPeer Peer;

        public long SendCycles;
        public long SendCyclesThrottled;
        public int QueueAtDecision;
        public readonly WindowMax QueueMax = new WindowMax(5);
        public readonly Histogram CycleInterval = new Histogram(new[] { 0.05, 0.1, 0.2, 0.5, 1, 2, 5 });
        public double LastCycle = -1;
        public long ZdosSent;
        public long ZdosReceived;
        public long ZdoDataBytes;
        public double LastZdoData = -1;
        public long RoutedRpcSent;
        public long CreatureOwnerChanges;

        public PlayerState(string steamId)
        {
            SteamId = steamId;
            Labels = new[] { "player", Name, "steam_id", SteamId };
        }

        public void SetName(string name)
        {
            name = name ?? "";
            if (name == Name)
                return;
            Name = name;
            Labels = new[] { "player", Name, "steam_id", SteamId };
        }
    }

    static class Players
    {
        static readonly Dictionary<long, PlayerState> ByUid = new Dictionary<long, PlayerState>();
        static readonly Dictionary<string, PlayerState> BySteamId = new Dictionary<string, PlayerState>();
        static readonly List<long> Stale = new List<long>();
        public static readonly List<PlayerState> Connected = new List<PlayerState>();

        public static PlayerState ForPeer(ZNetPeer peer)
        {
            if (peer == null || peer.m_uid == 0)
                return null;
            if (ByUid.TryGetValue(peer.m_uid, out var player))
                return player;
            var steamId = peer.m_socket?.GetHostName() ?? "";
            if (!BySteamId.TryGetValue(steamId, out player))
            {
                player = new PlayerState(steamId);
                BySteamId[steamId] = player;
            }
            player.Peer = peer;
            ByUid[peer.m_uid] = player;
            return player;
        }

        public static PlayerState ForUid(long uid) => ByUid.TryGetValue(uid, out var player) ? player : null;

        public static void Refresh()
        {
            Connected.Clear();
            var net = ZNet.instance;
            if (net == null)
            {
                ByUid.Clear();
                return;
            }
            var peers = net.GetPeers();
            foreach (var peer in peers)
            {
                if (!peer.IsReady())
                    continue;
                var player = ForPeer(peer);
                player.Peer = peer;
                player.SetName(peer.m_playerName);
                Connected.Add(player);
            }

            Stale.Clear();
            foreach (var kv in ByUid)
            {
                if (!peers.Exists(p => p.m_uid == kv.Key))
                    Stale.Add(kv.Key);
            }
            foreach (var uid in Stale)
                ByUid.Remove(uid);
        }
    }
}
