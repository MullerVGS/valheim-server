using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using ValheimMetrics.Collectors;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Summons
{
    // Invocacao (esqueleto do Cajado dos Mortos e afins) que guarda o nome entre invocacoes, sem mod no
    // cliente. O jogo sorteia um nome de m_randomStartingName no Awake de cada invocacao nova, entao o
    // nome que o jogador deu morre junto com o esqueleto. Aqui o servidor lembra os nomes de cada
    // jogador (SummonBook, em disco) e troca o sorteado pelo primeiro livre.
    //
    // Quem invoca e dono do ZDO e ja grava nome e `follow` (nome do personagem) no mesmo frame. O
    // servidor grava o nome com revisao adiantada, como no combustivel de fogo, e anota quem invocou
    // numa chave propria: `follow` esvazia quando o jogador manda o esqueleto ficar.
    sealed class SummonNames : ICollector
    {
        const string FileName = "summon-names.tsv";
        const string SummonerKey = "valheim-server.summoner";
        const uint RevisionLead = 1000;
        const double SaveEverySeconds = 10;

        static readonly int SummonerHash = SummonerKey.GetStableHashCode();
        static readonly Dictionary<int, bool> _isSummon = new Dictionary<int, bool>();
        static readonly HashSet<ZDOID> _seen = new HashSet<ZDOID>();
        static readonly List<ZDOID> _batch = new List<ZDOID>();
        static SummonBook<ZDOID> _book;
        static string _path;
        static bool _enabled;
        static bool _dirty;
        static double _nextSave;
        static long _renamed;
        static long _learned;

        public string Name => "summon_names";

        public void Install(Harmony harmony)
        {
            var raw = Environment.GetEnvironmentVariable(SummonBook<ZDOID>.Variable);
            if (string.IsNullOrWhiteSpace(raw))
                return;
            if (raw.Trim() != "1")
            {
                Plugin.Log.LogWarning($"{SummonBook<ZDOID>.Variable}={raw} ignorado: esperado 1.");
                return;
            }
            if (!Patcher.Patch(harmony, typeof(ZDO), "Deserialize", new[] { typeof(ZPackage) }, typeof(SummonNames),
                    postfix: nameof(DeserializePostfix), tag: "summon_names"))
                return;
            _enabled = true;
            Plugin.Log.LogInfo("Nomes de invocacao: o servidor lembra os nomes de cada jogador.");
        }

        // Roda dentro do RPC que recebe ZDO de cliente: so anota, o trabalho fica para o frame.
        static void DeserializePostfix(ZDO __instance)
        {
            try
            {
                if (IsSummon(__instance.GetPrefab()))
                    _seen.Add(__instance.m_uid);
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        public static void OnFrame(double now)
        {
            if (!_enabled || _seen.Count == 0 && !_dirty || !Ready())
                return;
            _batch.Clear();
            _batch.AddRange(_seen);
            _seen.Clear();
            foreach (var id in _batch)
            {
                try
                {
                    Handle(id);
                }
                catch (Exception e)
                {
                    Patcher.Errors++;
                    Plugin.Log.LogWarning($"Nomes de invocacao: {id} ignorado: {e.Message}");
                }
            }
            if (_dirty && now >= _nextSave)
            {
                _nextSave = now + SaveEverySeconds;
                _dirty = false;
                try
                {
                    Save();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Nomes de invocacao: nao salvou {_path}: {e.Message}");
                }
            }
        }

        static void Handle(ZDOID id)
        {
            var zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null || !zdo.IsValid())
            {
                _book.Forget(id);
                return;
            }
            var name = zdo.GetString(ZDOVars.s_tamedName);
            var write = _book.Observe(id, SummonerOf(zdo), name, Exists, WithSummoner, out var changed);
            if (changed)
            {
                _dirty = true;
                _learned++;
            }

            bool touched = false;
            var player = _book.PlayerOf(id);
            if (player != null && zdo.GetString(SummonerHash).Length == 0)
            {
                zdo.Set(SummonerHash, player);
                touched = true;
            }
            if (write != null)
            {
                zdo.Set(ZDOVars.s_tamedName, write);
                touched = true;
                _renamed++;
            }
            if (touched)
                zdo.DataRevision += RevisionLead;
        }

        static string SummonerOf(ZDO zdo)
        {
            var who = zdo.GetString(SummonerHash);
            return who.Length > 0 ? who : zdo.GetString(ZDOVars.s_follow);
        }

        static bool Exists(ZDOID id)
        {
            var zdo = ZDOMan.instance.GetZDO(id);
            return zdo != null && zdo.IsValid();
        }

        // Segura o nome a invocacao que esta na area carregada de quem invocou; a que ficou para tras
        // nunca mais e vista pelo dono e so sai do mundo se alguem passar por ela.
        static bool WithSummoner(ZDOID id)
        {
            var zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null || !zdo.IsValid())
                return false;
            var peer = ZNet.instance.GetPeerByPlayerName(SummonerOf(zdo));
            return peer != null && ZNetScene.InActiveArea(zdo.GetPosition(), peer.GetRefPos());
        }

        // O caminho do save so existe depois que o jogo le -savedir; o livro abre no primeiro uso e,
        // no mesmo boot, adota as invocacoes que ja estavam no mundo.
        static bool Ready()
        {
            var net = ZNet.instance;
            if (net == null || !net.IsServer() || ZDOMan.instance == null)
                return false;
            if (_book != null)
                return true;
            _path = Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), FileName);
            _book = File.Exists(_path) ? SummonBook<ZDOID>.Parse(File.ReadAllText(_path)) : new SummonBook<ZDOID>();
            AdoptExisting();
            return true;
        }

        static void AdoptExisting()
        {
            var byId = AccessTools.Field(typeof(ZDOMan), "m_objectsByID").GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
            if (byId == null)
                throw new MissingFieldException(nameof(ZDOMan), "m_objectsByID");
            foreach (var zdo in byId.Values)
            {
                if (!IsSummon(zdo.GetPrefab()))
                    continue;
                var player = SummonerOf(zdo);
                var name = zdo.GetString(ZDOVars.s_tamedName);
                // A que acabou de chegar de um cliente com nome fora da lista do dono e a invocacao nova
                // que abriu o livro: passa por Observe e ganha nome.
                if (_seen.Contains(zdo.m_uid) && !_book.Roster(player).Contains(name))
                    continue;
                _book.Adopt(zdo.m_uid, player, name);
            }
            Plugin.Log.LogInfo($"Nomes de invocacao: {_book.PlayerCount} jogadores no arquivo, {_book.LiveCount} invocacoes no mundo.");
        }

        static void Save()
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, _book.Serialize());
            if (File.Exists(_path))
                File.Replace(tmp, _path, null);
            else
                File.Move(tmp, _path);
        }

        // Invocacao = criatura que nasce domada e ganha nome sorteado (o esqueleto amigo do cajado).
        static bool IsSummon(int prefabHash)
        {
            if (prefabHash == 0 || ZNetScene.instance == null)
                return false;
            if (_isSummon.TryGetValue(prefabHash, out var cached))
                return cached;
            var prefab = ZNetScene.instance.GetPrefab(prefabHash);
            var tame = prefab ? prefab.GetComponent<Tameable>() : null;
            bool summon = tame != null && tame.m_startsTamed && tame.m_randomStartingName.Count > 0
                && prefab.GetComponent<Character>() != null;
            _isSummon[prefabHash] = summon;
            return summon;
        }

        public void Write(PrometheusWriter w, double now)
        {
            if (!_enabled || _book == null)
                return;
            w.Family("valheim_summon_names_live", "gauge", "Invocacoes vivas que o servidor acompanha.");
            w.Sample("valheim_summon_names_live", _book.LiveCount);
            w.Family("valheim_summon_names_players", "gauge", "Jogadores com nomes de invocacao guardados.");
            w.Sample("valheim_summon_names_players", _book.PlayerCount);
            w.Family("valheim_summon_names_renamed_total", "counter", "Invocacoes novas que receberam um nome guardado.");
            w.Sample("valheim_summon_names_renamed_total", _renamed);
            w.Family("valheim_summon_names_learned_total", "counter", "Renomeacoes no jogo que mudaram a lista de nomes.");
            w.Sample("valheim_summon_names_learned_total", _learned);
        }
    }
}
