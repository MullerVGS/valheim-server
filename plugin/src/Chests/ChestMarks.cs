using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimMetrics.Collectors;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Chests
{
    // Slot de bau reservado para um item. A marca e posta pelo mod de cliente (so quem quer marcar
    // precisa dele) numa chave do ZDO do bau, que cliente sem mod carrega intacta. O servidor recoloca
    // a pilha de zero ("fantasma") quando o bau fecha, qualquer que seja o jogador: o place stacks
    // vanilla enche o fantasma, entao ninguem precisa de mod para o item voltar ao slot.
    //
    // Com o bau aberto so o dono escreve nele; o servidor espera o InUse voltar a 0 e o ZDO ficar
    // parado alguns segundos antes de gravar. O receptor aceita a revisao maior mesmo sendo dono
    // (o mesmo caminho dos icones de placa).
    sealed class ChestMarks : ICollector
    {
        public const string Variable = "VALHEIM_SLOT_MARKS";
        public const string MarksKey = "valheim-server.slot_marks";

        const double SettleSeconds = 3;
        const double ScanDelaySeconds = 10;

        static readonly int MarksHash = MarksKey.GetStableHashCode();

        static bool _enabled;
        static readonly Dictionary<ZDOID, Pending> _pending = new Dictionary<ZDOID, Pending>();
        static readonly List<ZDOID> _due = new List<ZDOID>();
        static readonly Dictionary<int, ItemInfo?> _byHash = new Dictionary<int, ItemInfo?>();
        static readonly Dictionary<string, ItemInfo?> _byName = new Dictionary<string, ItemInfo?>();
        static readonly Dictionary<int, Vector2Int?> _sizes = new Dictionary<int, Vector2Int?>();
        static double _scanAt = -1;
        static bool _scanned;
        static long _ghostsAdded;
        static long _ghostsRemoved;
        static long _marksDropped;
        static long _skipped;
        static long _markedChests;

        struct Pending
        {
            public double Due;
            public uint Revision;
        }

        public string Name => "slot_marks";

        public void Install(Harmony harmony)
        {
            if (Environment.GetEnvironmentVariable(Variable)?.Trim() != "1")
                return;
            if (!Patcher.Patch(harmony, typeof(ZDO), "Deserialize", new[] { typeof(ZPackage) }, typeof(ChestMarks),
                    postfix: nameof(DeserializePostfix), tag: "slot_marks"))
                return;
            _enabled = true;
            Plugin.Log.LogInfo("Slots marcados de bau: ligado.");
        }

        // Roda dentro do RPC que recebe ZDO de cliente: so anota. A chave de marcas so existe em bau marcado.
        static void DeserializePostfix(ZDO __instance)
        {
            try
            {
                if (__instance.GetString(MarksHash).Length > 0)
                    _pending[__instance.m_uid] = new Pending { Due = Time.realtimeSinceStartupAsDouble + SettleSeconds, Revision = __instance.DataRevision };
            }
            catch
            {
                Patcher.Errors++;
            }
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
            }

            if (_pending.Count == 0)
                return;
            _due.Clear();
            foreach (var kv in _pending)
                if (now >= kv.Value.Due)
                    _due.Add(kv.Key);
            foreach (var id in _due)
            {
                var pending = _pending[id];
                _pending.Remove(id);
                var zdo = ZDOMan.instance.GetZDO(id);
                try
                {
                    if (zdo == null || !zdo.IsValid())
                        continue;
                    // Mexeu de novo durante a espera: espera mais.
                    if (zdo.DataRevision != pending.Revision)
                    {
                        _pending[id] = new Pending { Due = now + SettleSeconds, Revision = zdo.DataRevision };
                        continue;
                    }
                    // Aberto: quem abriu e dono e o mod dele (se tiver) cuida; o fechamento chega como ZDO novo.
                    if (zdo.GetInt(ZDOVars.s_inUse) != 0)
                        continue;
                    Keep(zdo);
                }
                catch (Exception e)
                {
                    Patcher.Errors++;
                    Plugin.Log.LogWarning($"Slots marcados: bau {id} ignorado: {e.Message}");
                }
            }
        }

        // Uma vez por boot: bau marcado que perdeu fantasma com o plugin desligado.
        static void ScanExisting(double now)
        {
            _scanned = true;
            try
            {
                var byId = AccessTools.Field(typeof(ZDOMan), "m_objectsByID").GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
                if (byId == null)
                    throw new MissingFieldException(nameof(ZDOMan), "m_objectsByID");
                int found = 0;
                foreach (var zdo in byId.Values)
                {
                    if (zdo.GetString(MarksHash).Length == 0)
                        continue;
                    found++;
                    _pending[zdo.m_uid] = new Pending { Due = now, Revision = zdo.DataRevision };
                }
                _markedChests = found;
                Plugin.Log.LogInfo($"Slots marcados: {found} baus com marca no mundo.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Slots marcados: varredura inicial falhou: {e.Message}");
            }
        }

        static void Keep(ZDO zdo)
        {
            var size = SizeOf(zdo.GetPrefab());
            if (size == null)
            {
                _skipped++;
                return;
            }
            var bytes = zdo.GetByteArray(ZDOVars.s_items);
            ChestItems chest;
            if (bytes == null)
                chest = new ChestItems { Version = ChestItems.ChunksNCheats };
            else if (!ChestItems.TryParse(bytes, out chest))
            {
                _skipped++;
                return;
            }

            var marks = MarkCodec.Decode(zdo.GetString(MarksHash));
            var outcome = ChestKeeper.Apply(chest, marks, size.Value.x, size.Value.y, ByHash, ByName);
            if (outcome.ItemsChanged)
                zdo.Set(ZDOVars.s_items, chest.ToBytes());
            if (outcome.MarksChanged)
                zdo.Set(MarksHash, MarkCodec.Encode(outcome.Marks));
            _ghostsAdded += outcome.GhostsAdded;
            _ghostsRemoved += outcome.GhostsRemoved;
            _marksDropped += marks.Count - outcome.Marks.Count;
        }

        // Bau que se desfaz quando vazio (lapide, saco de loot) nao pode ter fantasma: nunca sumiria.
        static Vector2Int? SizeOf(int prefabHash)
        {
            if (_sizes.TryGetValue(prefabHash, out var cached))
                return cached;
            Vector2Int? size = null;
            var prefab = ZNetScene.instance.GetPrefab(prefabHash);
            var container = prefab ? prefab.GetComponentInChildren<Container>(true) : null;
            if (container != null && !container.m_autoDestroyEmpty && prefab.GetComponent<TombStone>() == null)
                size = new Vector2Int(container.m_width, container.m_height);
            _sizes[prefabHash] = size;
            return size;
        }

        static ItemInfo? ByHash(int hash)
        {
            if (_byHash.TryGetValue(hash, out var cached))
                return cached;
            var info = Describe(ZNetScene.instance.GetPrefab(hash));
            _byHash[hash] = info;
            return info;
        }

        static ItemInfo? ByName(string name)
        {
            if (_byName.TryGetValue(name, out var cached))
                return cached;
            var info = Describe(ZNetScene.instance.GetPrefab(name));
            _byName[name] = info;
            return info;
        }

        static ItemInfo? Describe(GameObject prefab)
        {
            var drop = prefab ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null)
                return null;
            var shared = drop.m_itemData.m_shared;
            return new ItemInfo(prefab.name, prefab.name.GetStableHashCode(), shared.m_maxStackSize > 1, shared.m_maxQuality > 1);
        }

        public void Write(PrometheusWriter w, double now)
        {
            if (!_enabled)
                return;
            w.Family("valheim_slot_marks_ghosts_added_total", "counter", "Pilhas de zero recolocadas em slot marcado.");
            w.Sample("valheim_slot_marks_ghosts_added_total", _ghostsAdded);
            w.Family("valheim_slot_marks_ghosts_removed_total", "counter", "Pilhas de zero tiradas de slot sem marca.");
            w.Sample("valheim_slot_marks_ghosts_removed_total", _ghostsRemoved);
            w.Family("valheim_slot_marks_dropped_total", "counter", "Marcas desfeitas (outro item no slot, item inexistente ou fora da grade).");
            w.Sample("valheim_slot_marks_dropped_total", _marksDropped);
            w.Family("valheim_slot_marks_skipped_total", "counter", "Baus marcados nao mexidos (prefab desconhecido ou formato de item novo).");
            w.Sample("valheim_slot_marks_skipped_total", _skipped);
            w.Family("valheim_slot_marks_chests_at_boot", "gauge", "Baus com marca achados na varredura do boot.");
            w.Sample("valheim_slot_marks_chests_at_boot", _markedChests);
            w.Family("valheim_slot_marks_pending", "gauge", "Baus marcados esperando fechar ou assentar.");
            w.Sample("valheim_slot_marks_pending", _pending.Count);
        }
    }
}
