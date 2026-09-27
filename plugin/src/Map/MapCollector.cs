using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using ValheimMetrics.Collectors;
using ValheimMetrics.Exposition;
using ValheimMetrics.Traffic;

namespace ValheimMetrics.Map
{
    // O mapa no Grafana: posicao dos jogadores, pins das mesas de cartografia, portais, camas, mortes,
    // reclamacoes de lag e ZDOs por zona, tudo com coordenada de mundo em metros (labels x/z). Com
    // VALHEIM_MAP_DIR, tambem desenha o fundo em tiles XYZ (ver MapProjection) so com o que as mesas
    // mostram. O que depende de objeto do jogo roda na thread principal; parse e desenho, numa thread
    // propria de baixa prioridade.
    sealed class MapCollector : ICollector
    {
        const double ScanDelaySeconds = 20;
        const double RefreshSeconds = 60;
        const double TombstoneNameWaitSeconds = 30;
        const int TopZones = 300;

        static readonly int TablePrefab = "piece_cartographytable".GetStableHashCode();
        static readonly int TombstonePrefab = "Player_tombstone".GetStableHashCode();
        static readonly Dictionary<int, string> BedPrefabs = Named("bed", "piece_bed02");

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly string Dir = Environment.GetEnvironmentVariable("VALHEIM_MAP_DIR");

        static readonly HashSet<ZDOID> Tables = new HashSet<ZDOID>();
        static readonly HashSet<ZDOID> Portals = new HashSet<ZDOID>();
        static readonly HashSet<ZDOID> Beds = new HashSet<ZDOID>();
        static readonly HashSet<ZDOID> KnownTombstones = new HashSet<ZDOID>();
        static readonly Dictionary<ZDOID, double> NewTombstones = new Dictionary<ZDOID, double>();

        static readonly Dictionary<string, long> Deaths = new Dictionary<string, long>();
        static readonly Dictionary<string, long> LagReports = new Dictionary<string, long>();
        static readonly Dictionary<string, string> NamesBySteamId = new Dictionary<string, string>();
        static bool _namesDirty;

        static AccessTools.FieldRef<ZDOMan, List<ZDO>[]> _bySector;
        static bool _scanned;
        static double _scanAt = -1;
        static double _nextRefresh;

        static List<string[]> _portals = new List<string[]>();
        static List<string[]> _beds = new List<string[]>();
        static List<string[]> _tables = new List<string[]>();
        static List<KeyValuePair<string[], int>> _zones = new List<KeyValuePair<string[], int>>();

        static readonly List<byte[]> TableData = new List<byte[]>();
        static Thread _worker;
        static volatile SharedMap _map;
        static volatile int _tiles;
        static double _renderSeconds;
        static long _renders;
        static long _renderErrors;

        public string Name => "map";

        public void Install(Harmony harmony)
        {
            _bySector = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");
            Patcher.Patch(harmony, typeof(ZDO), "Deserialize", new[] { typeof(ZPackage) }, typeof(MapCollector),
                postfix: nameof(DeserializePostfix), tag: "map");
            LoadNames();
        }

        // Portal e o que o proprio jogo conecta (Game.PortalPrefabHash): madeira, pedra e os que vierem.
        static bool IsPortal(int prefab) => Game.instance != null && Game.instance.PortalPrefabHash.Contains(prefab);

        static string PrefabName(int prefab)
        {
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            return go != null ? go.name : prefab.ToString(Inv);
        }

        static Dictionary<int, string> Named(params string[] names)
        {
            var map = new Dictionary<int, string>();
            foreach (var n in names)
                map[n.GetStableHashCode()] = n;
            return map;
        }

        // ZDO novo ou revisto vindo de um cliente. So compara inteiros: roda para todo ZDO recebido.
        static void DeserializePostfix(ZDO __instance)
        {
            try
            {
                int prefab = __instance.GetPrefab();
                if (prefab == TablePrefab)
                    Tables.Add(__instance.m_uid);
                else if (prefab == TombstonePrefab)
                {
                    if (!KnownTombstones.Contains(__instance.m_uid) && !NewTombstones.ContainsKey(__instance.m_uid))
                        NewTombstones[__instance.m_uid] = Time.realtimeSinceStartupAsDouble;
                }
                else if (IsPortal(prefab))
                    Portals.Add(__instance.m_uid);
                else if (BedPrefabs.ContainsKey(prefab))
                    Beds.Add(__instance.m_uid);
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        public static void OnLagReport(PlayerState player, Vector3 pos)
        {
            Bump(LagReports, player?.Name ?? "", pos);
        }

        static void Bump(Dictionary<string, long> counters, string player, Vector3 pos)
        {
            var zone = Zone.Of(pos.x, pos.z);
            var key = player + "\n" + zone.CenterX.ToString("F0", Inv) + "\n" + zone.CenterZ.ToString("F0", Inv);
            counters[key] = (counters.TryGetValue(key, out var n) ? n : 0) + 1;
        }

        public void Write(PrometheusWriter w, double now)
        {
            if (ZDOMan.instance != null && ZNet.instance != null && ZNet.instance.IsServer() && ZNetScene.instance != null)
            {
                if (!_scanned)
                {
                    if (_scanAt < 0)
                        _scanAt = now + ScanDelaySeconds;
                    else if (now >= _scanAt)
                        ScanExisting();
                }
                if (_scanned)
                {
                    RememberNames();
                    SettleTombstones(now);
                    if (now >= _nextRefresh)
                    {
                        _nextRefresh = now + RefreshSeconds;
                        Refresh();
                    }
                }
            }

            WritePlayers(w);
            WriteStatic(w);
            WriteCounters(w);
            WriteMapState(w);
        }

        // Uma vez por boot: o que ja estava no mundo. Tumulo existente nao e morte nova.
        static void ScanExisting()
        {
            _scanned = true;
            var sw = Stopwatch.StartNew();
            var sectors = _bySector(ZDOMan.instance);
            foreach (var list in sectors)
            {
                if (list == null)
                    continue;
                foreach (var zdo in list)
                {
                    int prefab = zdo.GetPrefab();
                    if (prefab == TablePrefab)
                        Tables.Add(zdo.m_uid);
                    else if (prefab == TombstonePrefab)
                    {
                        if (!NewTombstones.ContainsKey(zdo.m_uid))
                            KnownTombstones.Add(zdo.m_uid);
                    }
                    else if (BedPrefabs.ContainsKey(prefab))
                        Beds.Add(zdo.m_uid);
                }
            }
            // Portal nao mora no balde por setor: o ZDOMan guarda a parte, para conectar os pares.
            if (AccessTools.Field(typeof(ZDOMan), "m_portalObjects")?.GetValue(ZDOMan.instance) is IDictionary portals)
            {
                foreach (IEnumerable list in portals.Values)
                    foreach (ZDO zdo in list)
                        if (IsPortal(zdo.GetPrefab()))
                            Portals.Add(zdo.m_uid);
            }
            else
                Plugin.Log.LogWarning("Mapa: ZDOMan.m_portalObjects nao encontrado; portais so aparecem quando alguem mexe neles.");
            Plugin.Log.LogInfo($"Mapa: {Tables.Count} mesas, {Portals.Count} portais, {Beds.Count} camas, " +
                $"{KnownTombstones.Count} tumulos no mundo ({sw.ElapsedMilliseconds} ms).");
            Refresh();
        }

        static void SettleTombstones(double now)
        {
            if (NewTombstones.Count == 0)
                return;
            var done = new List<ZDOID>();
            foreach (var kv in NewTombstones)
            {
                var zdo = ZDOMan.instance.GetZDO(kv.Key);
                if (zdo == null)
                {
                    done.Add(kv.Key);
                    continue;
                }
                var name = zdo.GetString(ZDOVars.s_ownerName);
                if (name.Length == 0 && now - kv.Value < TombstoneNameWaitSeconds)
                    continue;
                Bump(Deaths, name, zdo.GetPosition());
                done.Add(kv.Key);
            }
            foreach (var id in done)
            {
                NewTombstones.Remove(id);
                KnownTombstones.Add(id);
            }
        }

        static void Refresh()
        {
            _portals = Describe(Portals, DescribePortal);
            _beds = Describe(Beds, zdo => new[] { "owner", zdo.GetString(ZDOVars.s_ownerName) });
            _tables = Describe(Tables, zdo => new string[0]);
            _zones = CountZones();

            var data = new List<byte[]>();
            foreach (var id in Tables)
            {
                var bytes = ZDOMan.instance.GetZDO(id)?.GetByteArray(ZDOVars.s_data);
                if (bytes != null)
                    data.Add(bytes);
            }
            StartWorker(data);
        }

        // Par pelo mesmo vinculo que o TeleportWorld usa; sem par = portal que ninguem nomeou igual.
        static string[] DescribePortal(ZDO zdo)
        {
            var target = ZDOMan.instance.GetZDO(zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal));
            var pos = zdo.GetPosition();
            string to = "", distance = "";
            if (target != null)
            {
                var tp = target.GetPosition();
                to = tp.x.ToString("F0", Inv) + ", " + tp.z.ToString("F0", Inv);
                distance = MapText.Distance(pos.x, pos.z, tp.x, tp.z);
            }
            return new[]
            {
                "tag", zdo.GetString(ZDOVars.s_tag),
                "prefab", PrefabName(zdo.GetPrefab()),
                "connected", target != null ? "sim" : "não",
                "target", to,
                "distance_m", distance,
            };
        }

        static List<string[]> Describe(HashSet<ZDOID> ids, Func<ZDO, string[]> labels)
        {
            var result = new List<string[]>();
            ids.RemoveWhere(id => ZDOMan.instance.GetZDO(id) == null);
            foreach (var id in ids)
            {
                var zdo = ZDOMan.instance.GetZDO(id);
                var pos = zdo.GetPosition();
                result.Add(MapProjection.Labels(labels(zdo), pos.x, pos.z));
            }
            return result;
        }

        // m_objectsBySector: indice (y + 256) * 512 + (x + 256) da zona de 64 m; o 0 guarda o que cai fora.
        // So entra zona que as mesas mostram, para o calor nao denunciar lugar que ninguem registrou.
        static List<KeyValuePair<string[], int>> CountZones()
        {
            var map = _map;
            var counts = new List<KeyValuePair<int, int>>();
            var sectors = _bySector(ZDOMan.instance);
            for (int s = 1; s < sectors.Length; s++)
            {
                int n = sectors[s]?.Count ?? 0;
                if (n == 0)
                    continue;
                float x = (s % 512 - 256) * Zone.Size, z = (s / 512 - 256) * Zone.Size;
                if (map == null || !SharedMap.ToPixel(x, z, out var j, out var i) || !map.Explored[i * SharedMap.Size + j])
                    continue;
                counts.Add(new KeyValuePair<int, int>(s, n));
            }
            counts.Sort((a, b) => b.Value.CompareTo(a.Value));
            var result = new List<KeyValuePair<string[], int>>();
            var byPrefab = new Dictionary<int, long>();
            for (int k = 0; k < counts.Count && k < TopZones; k++)
            {
                int s = counts[k].Key;
                byPrefab.Clear();
                foreach (var zdo in sectors[s])
                {
                    int prefab = zdo.GetPrefab();
                    byPrefab[prefab] = (byPrefab.TryGetValue(prefab, out var n) ? n : 0) + 1;
                }
                var named = new List<KeyValuePair<string, long>>();
                foreach (var kv in byPrefab)
                    named.Add(new KeyValuePair<string, long>(PrefabName(kv.Key), kv.Value));
                result.Add(new KeyValuePair<string[], int>(
                    MapProjection.Labels(new[] { "top", MapText.TopShares(named, 3) },
                        (s % 512 - 256) * Zone.Size, (s / 512 - 256) * Zone.Size), counts[k].Value));
            }
            return result;
        }

        static void StartWorker(List<byte[]> data)
        {
            if (_worker != null && _worker.IsAlive)
                return;
            bool same = data.Count == TableData.Count && _map != null;
            for (int i = 0; same && i < data.Count; i++)
                same = ReferenceEquals(data[i], TableData[i]);
            if (same)
                return;
            TableData.Clear();
            TableData.AddRange(data);

            var terrain = Dir != null && WorldGenerator.instance != null ? new GameTerrain(WorldGenerator.instance) : null;
            _worker = new Thread(() => Work(data, terrain))
            {
                IsBackground = true,
                Name = "ValheimMetrics.Map",
                Priority = System.Threading.ThreadPriority.BelowNormal,
            };
            _worker.Start();
        }

        static void Work(List<byte[]> data, ITerrain terrain)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var maps = new List<SharedMap>();
                foreach (var bytes in data)
                    maps.Add(SharedMap.FromCompressed(bytes));
                var map = SharedMap.Union(maps);
                _map = map;
                if (terrain != null)
                {
                    var tilesDir = Path.Combine(Dir, "tiles");
                    var maskPath = Path.Combine(Dir, "explored.gz");
                    var stylePath = Path.Combine(Dir, "style.txt");
                    var style = TilePainter.Style.ToString(Inv);
                    bool sameStyle = File.Exists(stylePath) && File.ReadAllText(stylePath).Trim() == style;
                    var before = Directory.Exists(tilesDir) && sameStyle ? MaskFile.Load(maskPath) : null;
                    var tiles = TilePainter.TilesTouching(TilePainter.Changed(before, map.Explored));
                    if (tiles.Count > 0)
                    {
                        int drawn = TilePainter.Render(tiles, map.Explored, terrain, tilesDir);
                        MaskFile.Save(maskPath, map.Explored);
                        File.WriteAllText(stylePath, style);
                        Plugin.Log.LogInfo($"Mapa: {tiles.Count} tiles revistos ({drawn} com desenho) em {sw.Elapsed.TotalSeconds:0.0} s.");
                    }
                    _tiles = Directory.GetFiles(tilesDir, "*.png", SearchOption.AllDirectories).Length;
                }
                _renderSeconds = sw.Elapsed.TotalSeconds;
                Interlocked.Increment(ref _renders);
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _renderErrors);
                Plugin.Log.LogWarning($"Mapa: leitura ou desenho falhou: {e}");
            }
        }

        static void RememberNames()
        {
            foreach (var p in Players.Connected)
            {
                if (p.Name.Length == 0 || p.SteamId.Length == 0)
                    continue;
                if (NamesBySteamId.TryGetValue(p.SteamId, out var known) && known == p.Name)
                    continue;
                NamesBySteamId[p.SteamId] = p.Name;
                _namesDirty = true;
            }
            if (_namesDirty && Dir != null)
            {
                _namesDirty = false;
                try
                {
                    Directory.CreateDirectory(Dir);
                    var lines = new List<string>();
                    foreach (var kv in NamesBySteamId)
                        lines.Add(kv.Key + "\t" + kv.Value);
                    File.WriteAllLines(Path.Combine(Dir, "players.tsv"), lines);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Mapa: nao gravou players.tsv: {e.Message}");
                }
            }
        }

        static void LoadNames()
        {
            if (Dir == null)
                return;
            var path = Path.Combine(Dir, "players.tsv");
            if (!File.Exists(path))
                return;
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split('\t');
                if (parts.Length == 2)
                    NamesBySteamId[parts[0]] = parts[1];
            }
        }

        static string AuthorName(string author)
        {
            // "Steam_7656..." no pin; o SteamID nu no socket.
            var id = author.StartsWith("Steam_", StringComparison.Ordinal) ? author.Substring(6) : author;
            return NamesBySteamId.TryGetValue(id, out var name) ? name : "";
        }

        // Pins automaticos (chefe pelo Vegvisir, baus da Hildir) vem como token de traducao; o servidor
        // dedicado nao carrega traducao, entao os conhecidos saem daqui e o resto perde so o "$".
        static readonly Dictionary<string, string> PinTokens = new Dictionary<string, string>
        {
            { "$enemy_eikthyr", "Eikthyr" },
            { "$enemy_gdking", "O Ancião" },
            { "$enemy_bonemass", "Massa Óssea" },
            { "$enemy_dragon", "Moder" },
            { "$enemy_goblinking", "Yagluth" },
            { "$enemy_seekerqueen", "A Rainha" },
            { "$enemy_fader", "Fader" },
            { "$hud_pin_hildir1", "Baú da Hildir 1" },
            { "$hud_pin_hildir2", "Baú da Hildir 2" },
            { "$hud_pin_hildir3", "Baú da Hildir 3" },
        };

        static string PinName(string name)
        {
            if (!name.StartsWith("$", StringComparison.Ordinal))
                return name;
            return PinTokens.TryGetValue(name, out var known) ? known : name.Substring(1);
        }

        static void WritePlayers(PrometheusWriter w)
        {
            w.Family("valheim_player_position_meters", "gauge", "Posicao de referencia do jogador (a que o servidor usa para zonas), por eixo.");
            foreach (var p in Players.Connected)
            {
                var pos = p.Peer.m_refPos;
                w.Sample("valheim_player_position_meters", pos.x, "player", p.Name, "steam_id", p.SteamId, "axis", "x");
                w.Sample("valheim_player_position_meters", pos.y, "player", p.Name, "steam_id", p.SteamId, "axis", "y");
                w.Sample("valheim_player_position_meters", pos.z, "player", p.Name, "steam_id", p.SteamId, "axis", "z");
            }

            // Valor = Heightmap.Biome (1 Prado, 2 Pantano, 4 Montanha, 8 Floresta Negra, 16 Planicie, 32 Cinzas,
            // 64 Extremo Norte, 256 Oceano, 512 Nevoa): numero para nao virar serie nova a cada troca de bioma.
            w.Family("valheim_player_biome", "gauge", "Bioma onde o jogador esta, pelo gerador de mundo (Heightmap.Biome).");
            var gen = WorldGenerator.instance;
            if (gen != null)
                foreach (var p in Players.Connected)
                    w.Sample("valheim_player_biome", (int)gen.GetBiome(p.Peer.m_refPos), "player", p.Name, "steam_id", p.SteamId);
        }

        static void WriteStatic(PrometheusWriter w)
        {
            w.Family("valheim_portal_info", "gauge", "Portal no mundo, com nome e posicao.");
            foreach (var labels in _portals)
                w.Sample("valheim_portal_info", 1, labels);

            w.Family("valheim_bed_info", "gauge", "Cama no mundo, com dono e posicao.");
            foreach (var labels in _beds)
                w.Sample("valheim_bed_info", 1, labels);

            w.Family("valheim_map_table_info", "gauge", "Mesa de cartografia no mundo.");
            foreach (var labels in _tables)
                w.Sample("valheim_map_table_info", 1, labels);

            w.Family("valheim_zone_zdos", "gauge", "ZDOs na zona de 64 m (centro em x/z), so as mais cheias entre as que as mesas mostram.");
            foreach (var kv in _zones)
                w.Sample("valheim_zone_zdos", kv.Value, kv.Key);

            var map = _map;
            w.Family("valheim_map_pin_info", "gauge", "Pin das mesas de cartografia (uniao, sem repetidos).");
            if (map != null)
            {
                foreach (var pin in map.Pins)
                {
                    w.Sample("valheim_map_pin_info", 1, MapProjection.Labels(new[]
                    {
                        "name", PinName(pin.Name),
                        "type", ((Minimap.PinType)pin.Type).ToString(),
                        "kind", MapText.PinKind(pin.Type),
                        "checked", pin.Checked ? "sim" : "não",
                        "author", AuthorName(pin.Author ?? ""),
                        "author_id", pin.Author ?? "",
                    }, pin.X, pin.Z));
                }
            }
        }

        static void WriteCounters(PrometheusWriter w)
        {
            w.Family("valheim_player_deaths_total", "counter", "Mortes (tumulo novo) por jogador e zona de 64 m onde caiu.");
            foreach (var kv in Deaths)
                w.Sample("valheim_player_deaths_total", kv.Value, ZoneLabels(kv.Key));

            w.Family("valheim_lag_reports_by_zone_total", "counter", "Reclamacoes de lag no chat por jogador e zona de 64 m onde ele estava.");
            foreach (var kv in LagReports)
                w.Sample("valheim_lag_reports_by_zone_total", kv.Value, ZoneLabels(kv.Key));
        }

        static string[] ZoneLabels(string key)
        {
            var parts = key.Split('\n');
            return MapProjection.Labels(new[] { "player", parts[0] },
                double.Parse(parts[1], Inv), double.Parse(parts[2], Inv));
        }

        static void WriteMapState(PrometheusWriter w)
        {
            var map = _map;
            int explored = 0;
            if (map != null)
                foreach (var e in map.Explored)
                    if (e)
                        explored++;

            w.Family("valheim_map_explored_square_meters", "gauge", "Area que as mesas de cartografia mostram (uniao).");
            w.Sample("valheim_map_explored_square_meters", explored * (double)SharedMap.PixelSize * SharedMap.PixelSize);
            w.Family("valheim_map_tiles", "gauge", "Tiles PNG do fundo do mapa no disco.");
            w.Sample("valheim_map_tiles", _tiles);
            w.Family("valheim_map_render_seconds", "gauge", "Duracao da ultima leitura das mesas (e desenho dos tiles, se ligado).");
            w.Sample("valheim_map_render_seconds", _renderSeconds);
            w.Family("valheim_map_renders_total", "counter", "Leituras das mesas concluidas.");
            w.Sample("valheim_map_renders_total", Interlocked.Read(ref _renders));
            w.Family("valheim_map_render_errors_total", "counter", "Leituras ou desenhos que falharam (detalhe no log).");
            w.Sample("valheim_map_render_errors_total", Interlocked.Read(ref _renderErrors));
        }
    }
}
