using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValheimMetrics.Experience;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Collectors
{
    // O que o jogador sente, medido do servidor, sem F2:
    // - relay: quanto o servidor segura a atualizacao de um jogador/criatura antes de repassar a cada um;
    // - backlog: objetos que mudaram e ficaram para o proximo ciclo;
    // - buraco de pacotes: cliente que parou de mandar qualquer coisa (PC travado ou rede);
    // - pickup e bau cronometrados pelos RPCs que passam pelo servidor;
    // - "lag" digitado no chat vira marcador com hora e autor.
    sealed class ExperienceCollector : ICollector
    {
        const double PendingTimeout = 30;

        sealed class PendingPickup
        {
            public long Sender;
            public double Since;
        }

        struct ContainerKey : IEquatable<ContainerKey>
        {
            public ZDOID Zdo;
            public long Player;

            public bool Equals(ContainerKey o) => Zdo == o.Zdo && Player == o.Player;
            public override bool Equals(object o) => o is ContainerKey k && Equals(k);
            public override int GetHashCode() => Zdo.GetHashCode() * 31 + Player.GetHashCode();
        }

        static readonly int RequestOwn = "RPC_RequestOwn".GetStableHashCode();
        static readonly int ChatMessage = "ChatMessage".GetStableHashCode();
        static readonly int Say = "Say".GetStableHashCode();
        static readonly Dictionary<int, int> ContainerRequests = new Dictionary<int, int>
        {
            { "RPC_RequestOpen".GetStableHashCode(), 0 },
            { "RPC_RequestStack".GetStableHashCode(), 0 },
            { "RPC_RequestTakeAll".GetStableHashCode(), 0 },
        };
        static readonly HashSet<int> ContainerResponses = new HashSet<int>
        {
            "RPC_OpenResponse".GetStableHashCode(),
            "RPC_StackResponse".GetStableHashCode(),
            "RPC_TakeAllResponse".GetStableHashCode(),
        };

        static FieldInfo _peersField;
        static FieldInfo _peerZdosField;
        static FieldInfo _peerOfField;
        static FieldInfo _infoDataRevision;
        static FieldInfo _infoOwnerRevision;
        static FieldInfo _infoSyncTime;
        static AccessTools.FieldRef<ZDOMan, List<ZDO>> _tempToSync;
        static AccessTools.FieldRef<ZDOMan, int> _zdosSent;

        static PlayerState _viewer;
        static IDictionary _viewerZdos;
        static bool _listBuilt;
        static readonly HashSet<ZDOID> KnownToViewer = new HashSet<ZDOID>();
        static readonly List<object> PeerScratch = new List<object>();

        static readonly Dictionary<ZDOID, PendingPickup> Pickups = new Dictionary<ZDOID, PendingPickup>();
        static readonly Dictionary<ContainerKey, double> Containers = new Dictionary<ContainerKey, double>();
        static readonly List<ZDOID> DoneZdos = new List<ZDOID>();
        static readonly List<ContainerKey> DoneContainers = new List<ContainerKey>();
        static readonly Dictionary<string, long> Dropped = new Dictionary<string, long>();
        static readonly LagReportDedup Dedup = new LagReportDedup(10);
        static long _lagReportsUnknown;

        public string Name => "experience";

        public void Install(Harmony harmony)
        {
            var zdoPeer = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer")
                ?? throw new MissingMemberException("ZDOMan", "ZDOPeer");
            var info = AccessTools.Inner(zdoPeer, "PeerZDOInfo")
                ?? throw new MissingMemberException("ZDOPeer", "PeerZDOInfo");
            _peersField = Need(AccessTools.Field(typeof(ZDOMan), "m_peers"), "ZDOMan.m_peers");
            _peerZdosField = Need(AccessTools.Field(zdoPeer, "m_zdos"), "ZDOPeer.m_zdos");
            _peerOfField = Need(AccessTools.Field(zdoPeer, "m_peer"), "ZDOPeer.m_peer");
            _infoDataRevision = Need(AccessTools.Field(info, "m_dataRevision"), "PeerZDOInfo.m_dataRevision");
            _infoOwnerRevision = Need(AccessTools.Field(info, "m_ownerRevision"), "PeerZDOInfo.m_ownerRevision");
            _infoSyncTime = Need(AccessTools.Field(info, "m_syncTime"), "PeerZDOInfo.m_syncTime");
            _tempToSync = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>>("m_tempToSync");
            _zdosSent = AccessTools.FieldRefAccess<ZDOMan, int>("m_zdosSent");

            var self = typeof(ExperienceCollector);
            Patcher.Patch(harmony, typeof(ZDOMan), "SendZDOs", new[] { zdoPeer, typeof(bool) }, self,
                nameof(SendZDOsPrefix), nameof(SendZDOsPostfix), tag: Name);
            Patcher.Patch(harmony, typeof(ZDOMan), "ServerSortSendZDOS", new[] { typeof(List<ZDO>), typeof(Vector3), zdoPeer }, self,
                postfix: nameof(SortPostfix), tag: Name);
            Patcher.Patch(harmony, typeof(ZRoutedRpc.RoutedRPCData), "Deserialize", new[] { typeof(ZPackage) }, self,
                postfix: nameof(RoutedPostfix), tag: Name);
            Patcher.Patch(harmony, typeof(ZRpc), "HandlePackage", new[] { typeof(ZPackage) }, self,
                nameof(HandlePackagePrefix), tag: Name);
        }

        static FieldInfo Need(FieldInfo field, string name) =>
            field ?? throw new MissingFieldException(name);

        // --- relay e backlog -------------------------------------------------------------------

        static void SendZDOsPrefix(ZDOMan __instance, object __0, out int __state)
        {
            __state = 0;
            _viewer = null;
            _listBuilt = false;
            try
            {
                __state = _zdosSent(__instance);
                _viewer = Players.ForPeer((ZNetPeer)_peerOfField.GetValue(__0));
                _viewerZdos = (IDictionary)_peerZdosField.GetValue(__0);
                KnownToViewer.Clear();
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        // Roda dentro de CreateSyncList, antes do envio: e o ultimo momento em que o m_zdos do jogador ainda
        // diz o que ele ja tinha. Objeto que entra na area agora nao e atraso, e primeira vista; os distantes
        // e os forcados entram na lista depois do sort e ficam de fora por nao estarem aqui.
        static void SortPostfix(List<ZDO> __0)
        {
            if (_viewer == null)
                return;
            try
            {
                _listBuilt = true;
                var creatures = Prefabs.Creatures;
                foreach (var zdo in __0)
                {
                    if (Kind(zdo, creatures) != 0 && _viewerZdos.Contains(zdo.m_uid))
                        KnownToViewer.Add(zdo.m_uid);
                }
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        static void SendZDOsPostfix(ZDOMan __instance, int __state)
        {
            var viewer = _viewer;
            _viewer = null;
            if (viewer?.Peer == null || !_listBuilt)
                return;
            try
            {
                var list = _tempToSync(__instance);
                int sent = Math.Min(_zdosSent(__instance) - __state, list.Count);
                double now = Time.realtimeSinceStartupAsDouble;
                viewer.Backlog = list.Count - sent;
                viewer.BacklogMax.Add(now, viewer.Backlog);

                var peers = OwnerPeers(__instance);
                var creatures = Prefabs.Creatures;
                float time = Time.time;
                long viewerUid = viewer.Peer.m_uid;
                for (int i = 0; i < sent; i++)
                {
                    var zdo = list[i];
                    int kind = Kind(zdo, creatures);
                    if (kind == 0 || !KnownToViewer.Contains(zdo.m_uid))
                        continue;
                    long owner = zdo.GetOwner();
                    if (owner == 0 || owner == viewerUid)
                        continue;
                    var ownerZdos = ZdosOf(peers, owner);
                    var infoBox = ownerZdos?[zdo.m_uid];
                    if (infoBox == null || (uint)_infoDataRevision.GetValue(infoBox) != zdo.DataRevision)
                        continue;
                    double delay = Math.Max(0, time - (float)_infoSyncTime.GetValue(infoBox));
                    (kind == 1 ? viewer.RelayPlayers : viewer.RelayCreatures).Observe(delay);
                }

                if (Pickups.Count > 0)
                    DeliverPickups(viewer, viewerUid, now);
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        // 1 = jogador, 2 = criatura, 0 = o resto (construcao, item, terreno).
        static int Kind(ZDO zdo, HashSet<int> creatures)
        {
            int prefab = zdo.GetPrefab();
            if (prefab == Prefabs.Player)
                return 1;
            return creatures != null && creatures.Contains(prefab) ? 2 : 0;
        }

        static List<object> OwnerPeers(ZDOMan zdoman)
        {
            PeerScratch.Clear();
            foreach (var peer in (IList)_peersField.GetValue(zdoman))
                PeerScratch.Add(peer);
            return PeerScratch;
        }

        static IDictionary ZdosOf(List<object> peers, long uid)
        {
            foreach (var peer in peers)
            {
                var net = (ZNetPeer)_peerOfField.GetValue(peer);
                if (net != null && net.m_uid == uid)
                    return (IDictionary)_peerZdosField.GetValue(peer);
            }
            return null;
        }

        // --- pickup --------------------------------------------------------------------------

        // Pedido -> dono antigo cede -> servidor recebe a troca -> servidor manda a troca ao pedinte.
        // Fecha quando o pedinte ja recebeu a revisao de dono em que ele e o dono.
        static void DeliverPickups(PlayerState viewer, long viewerUid, double now)
        {
            DoneZdos.Clear();
            foreach (var kv in Pickups)
            {
                if (kv.Value.Sender != viewerUid)
                    continue;
                var zdo = ZDOMan.instance.GetZDO(kv.Key);
                if (zdo == null || zdo.GetOwner() != viewerUid)
                    continue;
                var infoBox = _viewerZdos[kv.Key];
                if (infoBox == null || (ushort)_infoOwnerRevision.GetValue(infoBox) != zdo.OwnerRevision)
                    continue;
                viewer.Pickup.Observe(now - kv.Value.Since);
                DoneZdos.Add(kv.Key);
            }
            foreach (var id in DoneZdos)
                Pickups.Remove(id);
        }

        // --- RPCs roteados: pickup, bau, RPC perdido, chat ------------------------------------

        static void RoutedPostfix(ZRoutedRpc.RoutedRPCData __instance)
        {
            try
            {
                var data = __instance;
                double now = Time.realtimeSinceStartupAsDouble;
                int method = data.m_methodHash;

                // O servidor so repassa a quem esta conectado: pedido ao dono que saiu some em silencio.
                if (data.m_targetPeerID != 0 && data.m_targetPeerID != ZDOMan.GetSessionID()
                    && ZNet.instance != null && ZNet.instance.GetPeer(data.m_targetPeerID) == null)
                {
                    var name = RpcCollector.NameOf(method);
                    Dropped[name] = (Dropped.TryGetValue(name, out var n) ? n : 0) + 1;
                }

                if (method == RequestOwn)
                {
                    var requester = Players.ForUid(data.m_senderPeerID);
                    if (requester != null)
                        requester.PickupRequests++;
                    if (!Pickups.TryGetValue(data.m_targetZDO, out var pending) || pending.Sender != data.m_senderPeerID)
                        Pickups[data.m_targetZDO] = new PendingPickup { Sender = data.m_senderPeerID, Since = now };
                }
                else if (ContainerRequests.ContainsKey(method))
                {
                    var key = new ContainerKey { Zdo = data.m_targetZDO, Player = data.m_senderPeerID };
                    if (!Containers.ContainsKey(key))
                        Containers[key] = now;
                }
                else if (ContainerResponses.Contains(method))
                {
                    var key = new ContainerKey { Zdo = data.m_targetZDO, Player = data.m_targetPeerID };
                    if (Containers.TryGetValue(key, out var since))
                    {
                        Players.ForUid(data.m_targetPeerID)?.Container.Observe(now - since);
                        Containers.Remove(key);
                    }
                }
                else if (method == ChatMessage || method == Say)
                {
                    OnChat(data, method == ChatMessage, now);
                }
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        // ChatMessage: Vector3, int, UserInfo(nome, id), texto. Say: int, UserInfo, texto.
        static void OnChat(ZRoutedRpc.RoutedRPCData data, bool shout, double now)
        {
            var pkg = data.m_parameters;
            int pos = pkg.GetPos();
            try
            {
                pkg.SetPos(0);
                if (shout)
                    pkg.ReadVector3();
                pkg.ReadInt();
                var name = pkg.ReadString();
                pkg.ReadString();
                var text = pkg.ReadString();
                if (!LagReportText.IsReport(text))
                    return;
                var player = Players.ForUid(data.m_senderPeerID);
                var who = player?.SteamId ?? name;
                if (!Dedup.Accept(who, now))
                    return;
                if (player != null)
                    player.LagReports++;
                else
                    _lagReportsUnknown++;
                Plugin.Log.LogInfo($"Reclamacao de lag: {player?.Name ?? name} ({who}): \"{text}\"");
            }
            finally
            {
                pkg.SetPos(pos);
            }
        }

        // --- buraco de pacotes ---------------------------------------------------------------

        // Pacote que chega logo depois de uma travada do servidor esperou a travada, nao o cliente:
        // desconta o frame em que foi processado.
        static void HandlePackagePrefix(ZRpc __instance)
        {
            try
            {
                var net = ZNet.instance;
                if (net == null)
                    return;
                foreach (var peer in net.GetPeers())
                {
                    if (peer.m_rpc != __instance)
                        continue;
                    var player = Players.ForPeer(peer);
                    if (player == null)
                        return;
                    double now = Time.realtimeSinceStartupAsDouble;
                    if (player.LastPacket >= 0)
                    {
                        double gap = Math.Max(0, now - player.LastPacket - Time.unscaledDeltaTime);
                        player.PacketGap.Observe(gap);
                        player.PacketGapMax.Add(now, gap);
                    }
                    player.LastPacket = now;
                    return;
                }
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        // --- snapshot ------------------------------------------------------------------------

        static void Expire(double now)
        {
            DoneZdos.Clear();
            foreach (var kv in Pickups)
            {
                if (now - kv.Value.Since < PendingTimeout && ZDOMan.instance.GetZDO(kv.Key) != null)
                    continue;
                // Item que sumiu antes de trocar de dono foi pego por outro ou despawnou: nao e atraso.
                if (ZDOMan.instance.GetZDO(kv.Key) != null)
                {
                    var p = Players.ForUid(kv.Value.Sender);
                    if (p != null)
                        p.PickupTimeouts++;
                }
                DoneZdos.Add(kv.Key);
            }
            foreach (var id in DoneZdos)
                Pickups.Remove(id);

            DoneContainers.Clear();
            foreach (var kv in Containers)
            {
                if (now - kv.Value < PendingTimeout)
                    continue;
                var p = Players.ForUid(kv.Key.Player);
                if (p != null)
                    p.ContainerTimeouts++;
                DoneContainers.Add(kv.Key);
            }
            foreach (var key in DoneContainers)
                Containers.Remove(key);
        }

        public void Write(PrometheusWriter w, double now)
        {
            Expire(now);
            var players = Players.Connected;

            w.Family("valheim_player_relay_delay_seconds", "histogram",
                "Tempo entre o servidor receber a atualizacao do dono e repassa-la a este jogador. kind=player: outros jogadores; kind=creature: criaturas. Soma-se ao ping dos dois.");
            foreach (var p in players)
            {
                w.Histogram("valheim_player_relay_delay_seconds", p.RelayPlayers, p.Labels[0], p.Labels[1], p.Labels[2], p.Labels[3], "kind", "player");
                w.Histogram("valheim_player_relay_delay_seconds", p.RelayCreatures, p.Labels[0], p.Labels[1], p.Labels[2], p.Labels[3], "kind", "creature");
            }

            w.Family("valheim_player_backlog_zdos", "gauge", "Objetos mudados que ficaram para o proximo ciclo de envio (ultimo ciclo).");
            foreach (var p in players)
                w.Sample("valheim_player_backlog_zdos", p.Backlog, p.Labels);

            w.Family("valheim_player_backlog_max_zdos", "gauge", "Maior backlog nos ultimos 5s.");
            foreach (var p in players)
                w.Sample("valheim_player_backlog_max_zdos", p.BacklogMax.Max(now), p.Labels);

            w.Family("valheim_player_packet_gap_seconds", "histogram",
                "Intervalo sem nenhum pacote do jogador, descontada travada do servidor. Acima de 0,3s: PC dele travou ou a rede parou.");
            foreach (var p in players)
                w.Histogram("valheim_player_packet_gap_seconds", p.PacketGap, p.Labels);

            w.Family("valheim_player_packet_gap_max_seconds", "gauge", "Maior intervalo sem pacote do jogador nos ultimos 5s.");
            foreach (var p in players)
                w.Sample("valheim_player_packet_gap_max_seconds", p.PacketGapMax.Max(now), p.Labels);

            w.Family("valheim_player_action_seconds", "histogram",
                "Tempo de acao que depende de outro cliente, medido no servidor (some o ping do jogador). action=pickup: pedir posse do item ate receber; action=container: abrir/empilhar/pegar tudo do bau.");
            foreach (var p in players)
            {
                w.Histogram("valheim_player_action_seconds", p.Pickup, p.Labels[0], p.Labels[1], p.Labels[2], p.Labels[3], "action", "pickup");
                w.Histogram("valheim_player_action_seconds", p.Container, p.Labels[0], p.Labels[1], p.Labels[2], p.Labels[3], "action", "container");
            }

            w.Family("valheim_player_action_timeouts_total", "counter", "Acoes sem resposta em 30s.");
            foreach (var p in players)
            {
                w.Sample("valheim_player_action_timeouts_total", p.PickupTimeouts, p.Labels[0], p.Labels[1], p.Labels[2], p.Labels[3], "action", "pickup");
                w.Sample("valheim_player_action_timeouts_total", p.ContainerTimeouts, p.Labels[0], p.Labels[1], p.Labels[2], p.Labels[3], "action", "container");
            }

            w.Family("valheim_player_pickup_requests_total", "counter", "Pedidos de posse de item (inclui repeticoes do backoff do jogo).");
            foreach (var p in players)
                w.Sample("valheim_player_pickup_requests_total", p.PickupRequests, p.Labels);

            w.Family("valheim_routed_rpc_dropped_total", "counter", "RPCs roteados para jogador que nao esta conectado: o servidor descarta sem aviso.");
            foreach (var kv in Dropped)
                w.Sample("valheim_routed_rpc_dropped_total", kv.Value, "method", kv.Key);

            w.Family("valheim_lag_reports_total", "counter", "Mensagens de chat com lag/trav*: reclamacao do jogador, com hora exata.");
            foreach (var p in players)
                w.Sample("valheim_lag_reports_total", p.LagReports, p.Labels);
            w.Sample("valheim_lag_reports_total", _lagReportsUnknown, "player", "", "steam_id", "");
        }
    }
}
