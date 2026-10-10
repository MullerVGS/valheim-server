using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValheimMetrics.Collectors;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Chests
{
    // Bau de bagunca: o que cai nele vai sozinho para o bau ligado que ja guarda aquele item. Os links
    // sao postos pelo mod de cliente (Chest Sort) numa chave do ZDO de cada bau; cliente sem mod carrega
    // a chave intacta e so ve os itens mudarem de lugar. Um destino pode ter os proprios links, entao a
    // bagunca principal alimenta a bagunca de comida, que alimenta os baus de cada comida.
    //
    // O servidor tem todos os ZDOs do mundo, carregados ou nao, e mexe nos bytes de s_items sem
    // instanciar nada. O perigo e o cliente: cada Container guarda o inventario em memoria, so rele o
    // ZDO uma vez por segundo e nunca enquanto esta aberto. Quem abre o bau logo depois de o servidor
    // escrever grava a copia velha por cima, e o item some ou duplica. Por isso o servidor "abre" os
    // baus antes de mexer: toma a posse deles (todo pedido de abrir, empilhar ou pegar tudo passa pelo
    // dono, e a resposta e "em uso"), espera o que ja estava a caminho chegar, confere que ninguem
    // mexeu, escreve origem e destinos no mesmo frame, espera os clientes relerem e devolve a posse.
    sealed class ChestSort : ICollector
    {
        public const string Variable = "VALHEIM_CHEST_SORT";

        // Depois dos slots marcados (3 s), para o fantasma ja estar no bau quando o remanejo olhar.
        const double SettleSeconds = 4;
        const double ScanDelaySeconds = 12;
        const double SweepSeconds = 30;
        const double RetrySeconds = 5;
        // Ida e volta de quem abriu o bau sem ter visto a troca de dono.
        const double ArriveSeconds = 0.7;
        // Container.CheckForChanges roda a cada 1 s em cada cliente.
        const double HoldSeconds = 1.5;
        const double SendTimeoutSeconds = 8;
        const uint RevisionLead = 1000;

        static readonly int SortHash = SortCodec.Key.GetStableHashCode();
        static readonly int RequestOpen = "RPC_RequestOpen".GetStableHashCode();
        static readonly int RequestStack = "RPC_RequestStack".GetStableHashCode();
        static readonly int RequestTakeAll = "RPC_RequestTakeAll".GetStableHashCode();

        enum Phase { Locking, Holding }

        sealed class Held
        {
            public ZDOID Id;
            public long PreviousOwner;
            public uint Revision;
            public ushort LockRevision;
            public uint Written;
            public byte[] Bytes;
        }

        sealed class Job
        {
            public ZDOID Source;
            public readonly List<Held> Chests = new List<Held>();
            public Phase Phase;
            public bool Sent;
            public double Due;
            public double Deadline;
            public int Moved;
        }

        static bool _enabled;
        static bool _forcing;
        static FieldInfo _peersField;
        static FieldInfo _peerZdosField;
        static FieldInfo _peerOfField;
        static FieldInfo _infoDataRevision;
        static FieldInfo _infoOwnerRevision;

        static readonly Dictionary<ulong, ZDOID> _byId = new Dictionary<ulong, ZDOID>();
        static readonly HashSet<ZDOID> _sources = new HashSet<ZDOID>();
        static readonly HashSet<ZDOID> _touched = new HashSet<ZDOID>();
        static readonly Dictionary<ZDOID, double> _pending = new Dictionary<ZDOID, double>();
        static readonly Dictionary<ZDOID, Held> _locks = new Dictionary<ZDOID, Held>();
        static readonly Dictionary<int, Vector2Int?> _sizes = new Dictionary<int, Vector2Int?>();
        static Job _job;
        static double _scanAt = -1;
        static double _nextSweep;
        static bool _scanned;
        static long _jobs;
        static long _moved;
        static long _conflicts;
        static long _skipped;
        static long _denied;

        public string Name => "chest_sort";

        public void Install(Harmony harmony)
        {
            if (Environment.GetEnvironmentVariable(Variable)?.Trim() != "1")
                return;

            var zdoPeer = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer")
                ?? throw new MissingMemberException("ZDOMan", "ZDOPeer");
            var info = AccessTools.Inner(zdoPeer, "PeerZDOInfo")
                ?? throw new MissingMemberException("ZDOPeer", "PeerZDOInfo");
            _peersField = Need(AccessTools.Field(typeof(ZDOMan), "m_peers"), "ZDOMan.m_peers");
            _peerZdosField = Need(AccessTools.Field(zdoPeer, "m_zdos"), "ZDOPeer.m_zdos");
            _peerOfField = Need(AccessTools.Field(zdoPeer, "m_peer"), "ZDOPeer.m_peer");
            _infoDataRevision = Need(AccessTools.Field(info, "m_dataRevision"), "PeerZDOInfo.m_dataRevision");
            _infoOwnerRevision = Need(AccessTools.Field(info, "m_ownerRevision"), "PeerZDOInfo.m_ownerRevision");

            var self = typeof(ChestSort);
            // Os tres tem de aplicar: sem a trava o remanejo perderia item, entao nem liga.
            bool ok = Patcher.Patch(harmony, typeof(ZDO), "Deserialize", new[] { typeof(ZPackage) }, self,
                postfix: nameof(DeserializePostfix), tag: Name);
            ok &= Patcher.Patch(harmony, typeof(ZDO), "SetOwner", new[] { typeof(long) }, self,
                prefix: nameof(SetOwnerPrefix), tag: Name);
            ok &= Patcher.Patch(harmony, typeof(ZRoutedRpc), "HandleRoutedRPC", new[] { typeof(ZRoutedRpc.RoutedRPCData) }, self,
                prefix: nameof(HandleRoutedPrefix), tag: Name);
            if (!ok)
                return;
            // Pedido a caminho do dono antigo, de quem ainda nao viu a trava: sem este, so nao recebe o "em uso".
            Patcher.Patch(harmony, typeof(ZRoutedRpc), "RouteRPC", new[] { typeof(ZRoutedRpc.RoutedRPCData) }, self,
                prefix: nameof(RoutePrefix), tag: Name);
            _enabled = true;
            Plugin.Log.LogInfo("Remanejo de baus: ligado.");
        }

        static FieldInfo Need(FieldInfo field, string name) =>
            field ?? throw new MissingFieldException(name);

        internal static bool Locked(ZDOID id) => _locks.ContainsKey(id);

        // Roda dentro do RPC que recebe ZDO de cliente: so anota. A chave so existe em bau ligado.
        static void DeserializePostfix(ZDO __instance)
        {
            if (!_enabled)
                return;
            try
            {
                if (__instance.GetString(SortHash).Length > 0)
                    _touched.Add(__instance.m_uid);
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        // Com o bau travado, a redistribuicao de posse do jogo (a cada 2 s) nao pode devolve-lo a um jogador.
        static bool SetOwnerPrefix(ZDO __instance, long __0)
        {
            if (_locks.Count == 0 || _forcing)
                return true;
            return __0 == ZDOMan.GetSessionID() || !_locks.ContainsKey(__instance.m_uid);
        }

        // Pedido que chega ao servidor como dono do bau travado. Quem pediu ouve "em uso", igual a bau
        // aberto por outro jogador; o resto (dano, desmontar) se perde, como se perde RPC para objeto
        // que o dono nao tem instanciado.
        static bool HandleRoutedPrefix(ZRoutedRpc.RoutedRPCData __0)
        {
            if (_locks.Count == 0 || __0.m_targetZDO.IsNone() || !_locks.ContainsKey(__0.m_targetZDO))
                return true;
            Deny(__0);
            return false;
        }

        static bool RoutePrefix(ZRoutedRpc.RoutedRPCData __0)
        {
            if (_locks.Count == 0 || __0.m_targetZDO.IsNone() || !_locks.ContainsKey(__0.m_targetZDO))
                return true;
            return !Deny(__0);
        }

        static bool Deny(ZRoutedRpc.RoutedRPCData rpc)
        {
            string answer = rpc.m_methodHash == RequestOpen ? "RPC_OpenResponse"
                : rpc.m_methodHash == RequestStack ? "RPC_StackResponse"
                : rpc.m_methodHash == RequestTakeAll ? "RPC_TakeAllResponse"
                : null;
            if (answer == null)
                return false;
            try
            {
                _denied++;
                ZRoutedRpc.instance.InvokeRoutedRPC(rpc.m_senderPeerID, rpc.m_targetZDO, answer, false);
            }
            catch
            {
                Patcher.Errors++;
            }
            return true;
        }

        public static void OnFrame(double now)
        {
            if (!_enabled || ZDOMan.instance == null || ZNet.instance == null || !ZNet.instance.IsServer() || ZNetScene.instance == null)
                return;

            if (!_scanned)
            {
                if (_scanAt < 0)
                    _scanAt = now + ScanDelaySeconds;
                else if (now >= _scanAt)
                    ScanExisting(now);
                return;
            }

            if (_touched.Count > 0)
            {
                foreach (var id in _touched)
                {
                    var zdo = ZDOMan.instance.GetZDO(id);
                    if (zdo != null && zdo.IsValid())
                        Index(zdo, now + SettleSeconds);
                }
                _touched.Clear();
            }

            if (_job != null)
            {
                Step(now);
                return;
            }

            if (now >= _nextSweep)
            {
                // Destino que ganhou espaco ou passou a guardar o item nao avisa: a origem e revista de tempos em tempos.
                _nextSweep = now + SweepSeconds;
                foreach (var id in _sources)
                    if (!_pending.ContainsKey(id))
                        _pending[id] = now;
            }

            if (_pending.Count == 0)
                return;
            ZDOID due = ZDOID.None;
            foreach (var kv in _pending)
            {
                if (now < kv.Value)
                    continue;
                due = kv.Key;
                break;
            }
            if (due.IsNone())
                return;
            _pending.Remove(due);
            try
            {
                Start(due, now);
            }
            catch (Exception e)
            {
                Patcher.Errors++;
                Plugin.Log.LogWarning($"Remanejo: bau {due} ignorado: {e.Message}");
                Abandon();
            }
        }

        // Uma vez por boot: o id de cada bau ligado e as origens que ficaram com item parado.
        static void ScanExisting(double now)
        {
            _scanned = true;
            try
            {
                var byId = AccessTools.Field(typeof(ZDOMan), "m_objectsByID").GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
                if (byId == null)
                    throw new MissingFieldException(nameof(ZDOMan), "m_objectsByID");
                foreach (var zdo in byId.Values)
                    if (zdo.GetString(SortHash).Length > 0)
                        Index(zdo, now);
                Plugin.Log.LogInfo($"Remanejo: {_byId.Count} baus ligados no mundo, {_sources.Count} mandam item.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Remanejo: varredura inicial falhou: {e.Message}");
            }
        }

        static void Index(ZDO zdo, double due)
        {
            var node = SortCodec.Decode(zdo.GetString(SortHash));
            if (node == null)
                return;
            _byId[node.Id] = zdo.m_uid;
            if (node.Links.Count == 0)
            {
                _sources.Remove(zdo.m_uid);
                return;
            }
            _sources.Add(zdo.m_uid);
            _pending[zdo.m_uid] = due;
        }

        static SortNode NodeOf(ulong id)
        {
            if (!_byId.TryGetValue(id, out var uid))
                return null;
            var zdo = ZDOMan.instance.GetZDO(uid);
            if (zdo == null || !zdo.IsValid())
            {
                _byId.Remove(id);
                return null;
            }
            var node = SortCodec.Decode(zdo.GetString(SortHash));
            return node != null && node.Id == id ? node : null;
        }

        static void Start(ZDOID id, double now)
        {
            var zdo = ZDOMan.instance.GetZDO(id);
            var node = zdo != null && zdo.IsValid() ? SortCodec.Decode(zdo.GetString(SortHash)) : null;
            if (node == null || node.Links.Count == 0)
            {
                _sources.Remove(id);
                return;
            }
            // Aberto: o fechamento chega como ZDO novo e poe o bau na fila de novo.
            if (zdo.GetInt(ZDOVars.s_inUse) != 0)
                return;
            var source = View(zdo);
            if (source == null)
            {
                _skipped++;
                return;
            }
            if (!SortPlanner.HasCargo(source, ItemCatalog.ByHash))
                return;

            var zdos = new List<ZDO>();
            var views = new List<SortChest>();
            foreach (ulong target in SortGraph.Destinations(node, NodeOf))
            {
                var other = _byId.TryGetValue(target, out var uid) ? ZDOMan.instance.GetZDO(uid) : null;
                if (other == null || !other.IsValid() || other.GetInt(ZDOVars.s_inUse) != 0)
                    continue;
                var view = View(other);
                if (view == null)
                    continue;
                zdos.Add(other);
                views.Add(view);
            }

            int moved = SortPlanner.Run(source, views, ItemCatalog.ByHash);
            if (moved == 0)
                return;

            _job = new Job { Source = id, Moved = moved, Phase = Phase.Locking, Deadline = now + SendTimeoutSeconds };
            _job.Chests.Add(Hold(zdo, source));
            for (int i = 0; i < views.Count; i++)
                if (views[i].Changed)
                    _job.Chests.Add(Hold(zdos[i], views[i]));
        }

        static void Step(double now)
        {
            var job = _job;
            try
            {
                if (job.Phase == Phase.Locking)
                {
                    if (!Intact(job))
                    {
                        Abort(job, now);
                        return;
                    }
                    if (!job.Sent)
                    {
                        if (Sent(job, data: false))
                        {
                            job.Sent = true;
                            job.Due = now + ArriveSeconds;
                        }
                        else if (now >= job.Deadline)
                            Abort(job, now);
                        return;
                    }
                    if (now < job.Due)
                        return;

                    // Origem e destinos no mesmo frame, e so depois de ter todos na mao: meio remanejo e item perdido.
                    var zdos = new List<ZDO>(job.Chests.Count);
                    foreach (var held in job.Chests)
                        zdos.Add(ZDOMan.instance.GetZDO(held.Id) ?? throw new InvalidOperationException("bau sumiu"));
                    for (int i = 0; i < zdos.Count; i++)
                    {
                        zdos[i].Set(ZDOVars.s_items, job.Chests[i].Bytes);
                        // Escrita de cliente que ainda nao viu a trava chega com revisao menor e e descartada.
                        zdos[i].DataRevision += RevisionLead;
                        job.Chests[i].Written = zdos[i].DataRevision;
                    }
                    foreach (var held in job.Chests)
                        Push(held.Id);
                    job.Phase = Phase.Holding;
                    job.Sent = false;
                    job.Deadline = now + SendTimeoutSeconds;
                    return;
                }

                if (!job.Sent)
                {
                    // Ja escreveu: daqui so da para esperar. Passado o prazo, solta assim mesmo.
                    if (!Sent(job, data: true) && now < job.Deadline)
                        return;
                    job.Sent = true;
                    job.Due = now + HoldSeconds;
                    return;
                }
                if (now < job.Due)
                    return;

                _job = null;
                foreach (var held in job.Chests)
                    Release(held);
                // Destino que tambem manda item segue a corrente sem esperar a proxima revista.
                foreach (var held in job.Chests)
                    if (held.Id != job.Source && _sources.Contains(held.Id))
                        _pending[held.Id] = now;
                _jobs++;
                _moved += job.Moved;
                Plugin.Log.LogInfo($"Remanejo: {job.Moved} itens do bau {job.Source} para {job.Chests.Count - 1} baus.");
            }
            catch (Exception e)
            {
                Patcher.Errors++;
                Plugin.Log.LogWarning($"Remanejo: bau {job.Source} abandonado: {e.Message}");
                Abandon();
            }
        }

        static void Abort(Job job, double now)
        {
            _job = null;
            foreach (var held in job.Chests)
                Release(held);
            _conflicts++;
            _pending[job.Source] = now + RetrySeconds;
        }

        // Saida de emergencia: nenhum bau fica com o servidor por causa de uma excecao.
        static void Abandon()
        {
            _job = null;
            foreach (var held in new List<Held>(_locks.Values))
            {
                try
                {
                    Release(held);
                }
                catch
                {
                    _locks.Remove(held.Id);
                }
            }
        }

        static SortChest View(ZDO zdo)
        {
            var size = SizeOf(zdo.GetPrefab());
            if (size == null)
                return null;
            var bytes = zdo.GetByteArray(ZDOVars.s_items);
            ChestItems items;
            if (bytes == null)
                items = new ChestItems { Version = ChestItems.ChunksNCheats };
            else if (!ChestItems.TryParse(bytes, out items) || items.Version != ChestItems.ChunksNCheats)
                return null;
            return new SortChest
            {
                Items = items,
                Width = size.Value.x,
                Height = items.Height(size.Value.y),
                Marks = MarkCodec.Decode(zdo.GetString(ChestMarks.MarksHash)),
            };
        }

        // So bau que e peca de construcao. Carroca e navio guardam os itens no ZDO do veiculo: tomar a
        // posse dele pararia a fisica. Lapide e saco de loot se desfazem vazios.
        static Vector2Int? SizeOf(int prefabHash)
        {
            if (_sizes.TryGetValue(prefabHash, out var cached))
                return cached;
            Vector2Int? size = null;
            var prefab = ZNetScene.instance.GetPrefab(prefabHash);
            var container = prefab ? prefab.GetComponent<Container>() : null;
            if (container != null && prefab.GetComponent<Piece>() != null && prefab.GetComponent<TombStone>() == null
                && !container.m_autoDestroyEmpty && container.m_rootObjectOverride == null && container.m_wagon == null)
                size = new Vector2Int(container.m_width, container.m_height);
            _sizes[prefabHash] = size;
            return size;
        }

        static Held Hold(ZDO zdo, SortChest view)
        {
            var held = new Held
            {
                Id = zdo.m_uid,
                PreviousOwner = zdo.GetOwner(),
                Revision = zdo.DataRevision,
                Bytes = view.Items.ToBytes(),
            };
            // Duas revisoes a frente: troca de dono que um cliente fez ao mesmo tempo perde para esta.
            ForceOwner(zdo, ZDOMan.GetSessionID(), (ushort)(zdo.OwnerRevision + 1));
            held.LockRevision = zdo.OwnerRevision;
            _locks[zdo.m_uid] = held;
            Push(zdo.m_uid);
            return held;
        }

        static void Release(Held held)
        {
            _locks.Remove(held.Id);
            var zdo = ZDOMan.instance.GetZDO(held.Id);
            if (zdo == null || !zdo.IsValid())
                return;
            long session = ZDOMan.GetSessionID();
            long owner = zdo.GetOwner();
            // Quem abriu o bau no meio da trava (a escrita dele trouxe a posse de volta) fica com ele.
            if (owner == session)
                owner = held.PreviousOwner == session || ZNet.instance.GetPeer(held.PreviousOwner) != null ? held.PreviousOwner : 0L;
            ForceOwner(zdo, owner, held.LockRevision);
            Push(held.Id);
        }

        // SetOwner so sobe a revisao quando o dono muda; aqui ela sobe sempre, para a resposta vencer
        // o que os clientes tem.
        static void ForceOwner(ZDO zdo, long uid, ushort atLeast)
        {
            _forcing = true;
            try
            {
                if (zdo.OwnerRevision < atLeast)
                    zdo.OwnerRevision = atLeast;
                if (zdo.GetOwner() == uid)
                    zdo.SetOwnerInternal(uid == 0L ? ZDOMan.GetSessionID() : 0L);
                zdo.SetOwner(uid);
            }
            finally
            {
                _forcing = false;
            }
        }

        static bool Intact(Job job)
        {
            long session = ZDOMan.GetSessionID();
            foreach (var held in job.Chests)
            {
                var zdo = ZDOMan.instance.GetZDO(held.Id);
                if (zdo == null || !zdo.IsValid() || zdo.GetOwner() != session || zdo.DataRevision != held.Revision
                    || zdo.GetInt(ZDOVars.s_inUse) != 0)
                    return false;
            }
            return true;
        }

        // Fura a fila de envio para quem conhece o bau; quem nao conhece nao tem Container dele.
        static void Push(ZDOID id)
        {
            foreach (var peer in (IEnumerable)_peersField.GetValue(ZDOMan.instance))
            {
                if (!((IDictionary)_peerZdosField.GetValue(peer)).Contains(id))
                    continue;
                var net = (ZNetPeer)_peerOfField.GetValue(peer);
                ZDOMan.instance.ForceSendZDO(net.m_uid, id);
            }
        }

        static bool Sent(Job job, bool data)
        {
            foreach (var peer in (IEnumerable)_peersField.GetValue(ZDOMan.instance))
            {
                var zdos = (IDictionary)_peerZdosField.GetValue(peer);
                foreach (var held in job.Chests)
                {
                    object known = zdos[held.Id];
                    if (known == null)
                        continue;
                    if (data ? (uint)_infoDataRevision.GetValue(known) < held.Written
                             : (ushort)_infoOwnerRevision.GetValue(known) < held.LockRevision)
                        return false;
                }
            }
            return true;
        }

        public void Write(PrometheusWriter w, double now)
        {
            if (!_enabled)
                return;
            w.Family("valheim_chest_sort_chests", "gauge", "Baus com id de remanejo (origens e destinos).");
            w.Sample("valheim_chest_sort_chests", _byId.Count);
            w.Family("valheim_chest_sort_sources", "gauge", "Baus que mandam item para outros.");
            w.Sample("valheim_chest_sort_sources", _sources.Count);
            w.Family("valheim_chest_sort_runs_total", "counter", "Remanejos concluidos (uma origem, um ou mais destinos).");
            w.Sample("valheim_chest_sort_runs_total", _jobs);
            w.Family("valheim_chest_sort_items_moved_total", "counter", "Unidades de item que mudaram de bau.");
            w.Sample("valheim_chest_sort_items_moved_total", _moved);
            w.Family("valheim_chest_sort_conflicts_total", "counter", "Remanejos desfeitos antes de escrever (alguem mexeu num dos baus ou a trava nao chegou).");
            w.Sample("valheim_chest_sort_conflicts_total", _conflicts);
            w.Family("valheim_chest_sort_denied_total", "counter", "Pedidos de abrir, empilhar ou pegar tudo respondidos com \"em uso\" durante a trava.");
            w.Sample("valheim_chest_sort_denied_total", _denied);
            w.Family("valheim_chest_sort_skipped_total", "counter", "Origens nao mexidas (prefab que nao e bau de construcao ou formato de item novo).");
            w.Sample("valheim_chest_sort_skipped_total", _skipped);
            w.Family("valheim_chest_sort_pending", "gauge", "Origens esperando assentar ou a proxima tentativa.");
            w.Sample("valheim_chest_sort_pending", _pending.Count);
            w.Family("valheim_chest_sort_locked", "gauge", "Baus com a posse no servidor agora.");
            w.Sample("valheim_chest_sort_locked", _locks.Count);
        }
    }
}
