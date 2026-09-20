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
    // text do TextMeshPro) e todo cliente, sem mod nenhum, desenha. O mesmo caminho serve ao jogador
    // que quer escrever mais que 50: abreviacao e continuacao, em SignTextRules.
    sealed class SignIcons : ICollector
    {
        public const string CatalogVariable = "VALHEIM_SIGN_ICONS_CATALOG";

        // Ao lado do catalogo gerado: entradas escritas a mao (desenho com nome, abreviacao). E lido
        // depois e ganha do gerado, entao regerar os icones nao apaga o que e de casa.
        public const string CustomFileName = "custom.txt";

        // Espera o mundo carregar antes de varrer as placas que ja existem.
        const double ScanDelaySeconds = 10;
        const double ReloadCheckSeconds = 5;

        static readonly int SignPrefab = "sign".GetStableHashCode();
        // Anotado na propria placa: e o que permite devolver o icone quando alguem confirma o texto
        // cortado, e redesenhar quando o catalogo muda. Cliente sem mod ignora a chave.
        static readonly int IconKey = "valheim-server.sign_icon".GetStableHashCode();
        static readonly int LabelKey = "valheim-server.sign_label".GetStableHashCode();
        // O que o jogador escreveu (com abreviacao, ja emendado), quando o texto da placa e a expansao.
        static readonly int SourceKey = "valheim-server.sign_source".GetStableHashCode();

        static SignIconCatalog _catalog;
        static string[] _paths;
        static string _loadedStamp;
        static string _seenStamp;
        static double _reloadCheckAt;
        static long _reloads;
        static readonly Queue<ZDOID> _pending = new Queue<ZDOID>();
        static readonly SortedDictionary<string, long> _changes = new SortedDictionary<string, long>();
        // Texto digitado que cada placa tinha: e a base de uma continuacao, que chega ja por cima dele.
        static readonly Dictionary<ZDOID, string> _typed = new Dictionary<ZDOID, string>();
        static double _scanAt = -1;
        static bool _scanned;
        static long _signsWithIcon;
        static long _signsWithSource;

        public string Name => "sign_icons";

        public void Install(Harmony harmony)
        {
            var path = Environment.GetEnvironmentVariable(CatalogVariable);
            if (string.IsNullOrWhiteSpace(path))
                return;

            path = path.Trim();
            _paths = new[] { path, Path.Combine(Path.GetDirectoryName(path) ?? "", CustomFileName) };
            var stamp = Stamp();
            var catalog = Load();
            if (catalog.Icons == 0)
                throw new InvalidDataException($"{path} nao tem nenhum icone.");

            if (!Patcher.Patch(harmony, typeof(ZDO), "Deserialize", new[] { typeof(ZPackage) }, typeof(SignIcons),
                    postfix: nameof(DeserializePostfix)))
                return;
            _catalog = catalog;
            _loadedStamp = _seenStamp = stamp;
            Plugin.Log.LogInfo($"Icones de placa: {Describe(catalog)}.");
        }

        static SignIconCatalog Load()
        {
            var lines = new List<string>();
            foreach (var path in _paths)
                if (File.Exists(path))
                    lines.AddRange(File.ReadLines(path));
            var warnings = new List<string>();
            var catalog = SignIconCatalog.Parse(lines, warnings);
            for (int i = 0; i < warnings.Count && i < 10; i++)
                Plugin.Log.LogWarning($"{CatalogVariable}: {warnings[i]}");
            return catalog;
        }

        static string Describe(SignIconCatalog catalog)
        {
            return $"{catalog.Icons} icones, {catalog.Aliases} apelidos, {catalog.Macros} abreviacoes, padrao {catalog.DefaultIcon ?? "nenhum"}";
        }

        static string Stamp()
        {
            var parts = new List<string>();
            foreach (var path in _paths)
            {
                var info = new FileInfo(path);
                parts.Add(info.Exists ? $"{info.Length}@{info.LastWriteTimeUtc.Ticks}" : "-");
            }
            return string.Join("|", parts);
        }

        // Arquivo trocado com o servidor no ar: recarrega e redesenha, sem restart. So aceita o
        // arquivo depois de ve-lo igual em duas conferencias, para nao ler copia pela metade.
        static void ReloadIfChanged(double now)
        {
            if (now < _reloadCheckAt)
                return;
            _reloadCheckAt = now + ReloadCheckSeconds;
            try
            {
                var stamp = Stamp();
                bool settled = stamp == _seenStamp;
                _seenStamp = stamp;
                if (stamp == _loadedStamp || !settled)
                    return;
                _loadedStamp = stamp;
                var catalog = Load();
                if (catalog.Icons == 0)
                {
                    Plugin.Log.LogWarning("Icones de placa: catalogo novo sem nenhum icone, mantido o anterior.");
                    return;
                }
                _catalog = catalog;
                _reloads++;
                _scanned = false;
                _scanAt = now;
                Plugin.Log.LogInfo($"Icones de placa: catalogo recarregado, {Describe(catalog)}.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Icones de placa: recarga falhou, mantido o catalogo anterior: {e.Message}");
            }
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

            ReloadIfChanged(now);

            while (_pending.Count > 0)
            {
                var zdo = ZDOMan.instance.GetZDO(_pending.Dequeue());
                try
                {
                    if (zdo != null && zdo.IsValid())
                        Apply(zdo, scanning: false);
                }
                catch
                {
                    Patcher.Errors++;
                }
            }

            if (_scanned)
                return;
            if (_scanAt < 0)
                _scanAt = now + ScanDelaySeconds;
            else if (now >= _scanAt)
                ScanExisting();
        }

        // Uma vez por boot e a cada catalogo recarregado: converte codigo escrito com o plugin
        // desligado e redesenha o que ficou para tras de um catalogo antigo.
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
                _signsWithSource = 0;
                _typed.Clear();
                foreach (var zdo in signs)
                    Apply(zdo, scanning: true);
                Plugin.Log.LogInfo($"Icones de placa: {signs.Count} placas no mundo, {_signsWithIcon} com icone, {_signsWithSource} com texto longo.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Icones de placa: varredura inicial falhou: {e.Message}");
            }
        }

        static void Apply(ZDO zdo, bool scanning)
        {
            var text = zdo.GetString(ZDOVars.s_text);
            var storedIcon = zdo.GetString(IconKey);
            var storedSource = zdo.GetString(SourceKey);
            bool hadIcon = !string.IsNullOrEmpty(storedIcon);
            bool hadSource = !string.IsNullOrEmpty(storedSource);
            bool hasIcon = hadIcon;
            bool hasSource = hadSource;
            string result = null;

            _typed.TryGetValue(zdo.m_uid, out var previous);
            bool asksIcon = SignCode.TryParse(text, out _, out _);
            var written = asksIcon
                ? default
                : SignTextRules.Decide(_catalog, text, storedSource, previous);
            if (written.Action != SignTextAction.None)
            {
                if (written.Action != SignTextAction.Release)
                {
                    zdo.Set(ZDOVars.s_text, written.Text);
                    text = written.Text;
                }
                hasSource = written.Source != null;
                if (hasSource)
                    zdo.Set(SourceKey, written.Source);
                else if (hadSource)
                    zdo.RemoveString(SourceKey);
                if (hadIcon)
                {
                    zdo.RemoveString(IconKey);
                    zdo.RemoveString(LabelKey);
                }
                hasIcon = false;
                result = "text_" + written.Action.ToString().ToLowerInvariant();
            }
            else if (!hadSource || asksIcon)
            {
                var decision = SignIconRules.Decide(_catalog, text, storedIcon, zdo.GetString(LabelKey));
                switch (decision.Action)
                {
                    case SignIconAction.None:
                        break;
                    case SignIconAction.Release:
                        zdo.RemoveString(IconKey);
                        zdo.RemoveString(LabelKey);
                        hasIcon = false;
                        break;
                    default:
                        zdo.Set(IconKey, decision.Icon);
                        zdo.Set(LabelKey, decision.Label);
                        zdo.Set(ZDOVars.s_text, decision.Text);
                        text = decision.Text;
                        hasIcon = true;
                        if (hadSource)
                            zdo.RemoveString(SourceKey);
                        hasSource = false;
                        break;
                }
                if (decision.Action != SignIconAction.None)
                    result = decision.Action == SignIconAction.Apply && decision.UsedDefault
                        ? "default"
                        : decision.Action.ToString().ToLowerInvariant();
            }

            if (text != null && text.Length <= SignCode.GameInputLimit)
                _typed[zdo.m_uid] = text;
            else
                _typed.Remove(zdo.m_uid);

            // A varredura conta do zero; fora dela so interessa a transicao.
            _signsWithIcon += scanning ? (hasIcon ? 1 : 0) : (hasIcon ? 1 : 0) - (hadIcon ? 1 : 0);
            _signsWithSource += scanning ? (hasSource ? 1 : 0) : (hasSource ? 1 : 0) - (hadSource ? 1 : 0);
            if (result != null)
                _changes[result] = (_changes.TryGetValue(result, out var n) ? n : 0) + 1;
        }

        public void Write(PrometheusWriter w, double now)
        {
            if (_catalog == null)
                return;
            w.Family("valheim_sign_icons_catalog_entries", "gauge", "Entradas do catalogo de icones de placa.");
            w.Sample("valheim_sign_icons_catalog_entries", _catalog.Icons, "kind", "icon");
            w.Sample("valheim_sign_icons_catalog_entries", _catalog.Aliases, "kind", "alias");
            w.Sample("valheim_sign_icons_catalog_entries", _catalog.Macros, "kind", "macro");

            w.Family("valheim_sign_icons_catalog_reloads_total", "counter", "Catalogos recarregados com o servidor no ar.");
            w.Sample("valheim_sign_icons_catalog_reloads_total", _reloads);

            w.Family("valheim_sign_icons_signs", "gauge", "Placas do mundo desenhadas pelo servidor.");
            w.Sample("valheim_sign_icons_signs", _signsWithIcon);

            w.Family("valheim_sign_icons_long_texts", "gauge", "Placas com texto por extenso gravado pelo servidor (abreviacao ou continuacao).");
            w.Sample("valheim_sign_icons_long_texts", _signsWithSource);

            w.Family("valheim_sign_icons_changes_total", "counter",
                "Placas reescritas: apply = codigo virou icone, default = codigo desconhecido, restore = texto cortado devolvido, refresh = catalogo mudou, release = jogador escreveu por cima; text_* = o mesmo para abreviacao e continuacao (text_write = texto por extenso gravado).");
            foreach (var kv in _changes)
                w.Sample("valheim_sign_icons_changes_total", kv.Value, "result", kv.Key);
        }
    }
}
