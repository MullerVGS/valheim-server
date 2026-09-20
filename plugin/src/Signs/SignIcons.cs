using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using ValheimMetrics.Collectors;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Signs
{
    // O limite de 50 caracteres da placa e so do campo de digitacao do cliente: Sign.SetText grava o
    // texto no ZDO sem validar tamanho, e o receptor aceita qualquer revisao maior sem conferir dono.
    // Entao o servidor pode trocar ":mel:" por um icone de milhares de caracteres (pixel art em rich
    // text do TextMeshPro) e todo cliente, sem mod nenhum, desenha.
    sealed class SignIcons : ICollector
    {
        public const string CatalogVariable = "VALHEIM_SIGN_ICONS_CATALOG";

        // Espera o mundo carregar antes de varrer as placas que ja existem.
        const double ScanDelaySeconds = 10;

        static readonly int SignPrefab = "sign".GetStableHashCode();
        // Anotado na propria placa: e o que permite devolver o icone quando alguem confirma o texto
        // cortado, e redesenhar quando o catalogo muda. Cliente sem mod ignora a chave.
        static readonly int IconKey = "valheim-server.sign_icon".GetStableHashCode();

        static SignIconCatalog _catalog;
        static readonly Queue<ZDOID> _pending = new Queue<ZDOID>();
        static readonly SortedDictionary<string, long> _changes = new SortedDictionary<string, long>();
        static double _scanAt = -1;
        static bool _scanned;
        static long _signsWithIcon;

        public string Name => "sign_icons";

        public void Install(Harmony harmony)
        {
            var path = Environment.GetEnvironmentVariable(CatalogVariable);
            if (string.IsNullOrWhiteSpace(path))
                return;

            var warnings = new List<string>();
            var catalog = SignIconCatalog.Parse(File.ReadLines(path.Trim()), warnings);
            for (int i = 0; i < warnings.Count && i < 10; i++)
                Plugin.Log.LogWarning($"{CatalogVariable}: {warnings[i]}");
            if (catalog.Icons == 0)
                throw new InvalidDataException($"{path} nao tem nenhum icone.");

            if (!Patcher.Patch(harmony, typeof(ZDO), "Deserialize", new[] { typeof(ZPackage) }, typeof(SignIcons),
                    postfix: nameof(DeserializePostfix)))
                return;
            _catalog = catalog;
            Plugin.Log.LogInfo($"Icones de placa: {catalog.Icons} icones, {catalog.Aliases} apelidos, padrao {catalog.DefaultIcon ?? "nenhum"}.");
        }

        // Roda dentro do RPC que recebe ZDO de cliente: so anota, a troca acontece no frame seguinte.
        static void DeserializePostfix(ZDO __instance)
        {
            try
            {
                if (__instance.GetPrefab() == SignPrefab)
                    _pending.Enqueue(__instance.m_uid);
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        public static void OnFrame(double now)
        {
            if (_catalog == null || ZDOMan.instance == null || ZNet.instance == null || !ZNet.instance.IsServer())
                return;

            while (_pending.Count > 0)
            {
                var zdo = ZDOMan.instance.GetZDO(_pending.Dequeue());
                if (zdo != null && zdo.IsValid())
                    Apply(zdo, scanning: false);
            }

            if (_scanned)
                return;
            if (_scanAt < 0)
                _scanAt = now + ScanDelaySeconds;
            else if (now >= _scanAt)
                ScanExisting();
        }

        // Uma vez por boot: converte codigo escrito com o plugin desligado e redesenha o que ficou
        // para tras de um catalogo antigo.
        static void ScanExisting()
        {
            _scanned = true;
            try
            {
                var byId = AccessTools.Field(typeof(ZDOMan), "m_objectsByID").GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
                if (byId == null)
                    throw new MissingFieldException(nameof(ZDOMan), "m_objectsByID");
                var signs = new List<ZDO>();
                foreach (var zdo in byId.Values)
                    if (zdo.GetPrefab() == SignPrefab)
                        signs.Add(zdo);
                _signsWithIcon = 0;
                foreach (var zdo in signs)
                    Apply(zdo, scanning: true);
                Plugin.Log.LogInfo($"Icones de placa: {signs.Count} placas no mundo, {_signsWithIcon} com icone.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Icones de placa: varredura inicial falhou: {e.Message}");
            }
        }

        static void Apply(ZDO zdo, bool scanning)
        {
            var stored = zdo.GetString(IconKey);
            bool had = !string.IsNullOrEmpty(stored);
            bool has = had;
            var decision = SignIconRules.Decide(_catalog, zdo.GetString(ZDOVars.s_text), stored);
            switch (decision.Action)
            {
                case SignIconAction.None:
                    break;
                case SignIconAction.Release:
                    zdo.RemoveString(IconKey);
                    has = false;
                    break;
                default:
                    zdo.Set(IconKey, decision.Icon);
                    zdo.Set(ZDOVars.s_text, decision.Text);
                    has = true;
                    break;
            }

            // A varredura conta do zero; fora dela so interessa a transicao.
            _signsWithIcon += scanning ? (has ? 1 : 0) : (has ? 1 : 0) - (had ? 1 : 0);
            if (decision.Action == SignIconAction.None)
                return;
            var result = decision.Action == SignIconAction.Apply && decision.UsedDefault
                ? "default"
                : decision.Action.ToString().ToLowerInvariant();
            _changes[result] = (_changes.TryGetValue(result, out var n) ? n : 0) + 1;
        }

        public void Write(PrometheusWriter w, double now)
        {
            if (_catalog == null)
                return;
            w.Family("valheim_sign_icons_catalog_entries", "gauge", "Entradas do catalogo de icones de placa.");
            w.Sample("valheim_sign_icons_catalog_entries", _catalog.Icons, "kind", "icon");
            w.Sample("valheim_sign_icons_catalog_entries", _catalog.Aliases, "kind", "alias");

            w.Family("valheim_sign_icons_signs", "gauge", "Placas do mundo desenhadas pelo servidor.");
            w.Sample("valheim_sign_icons_signs", _signsWithIcon);

            w.Family("valheim_sign_icons_changes_total", "counter",
                "Placas reescritas: apply = codigo virou icone, default = codigo desconhecido, restore = texto cortado devolvido, refresh = catalogo mudou, release = jogador escreveu por cima.");
            foreach (var kv in _changes)
                w.Sample("valheim_sign_icons_changes_total", kv.Value, "result", kv.Key);
        }
    }
}
