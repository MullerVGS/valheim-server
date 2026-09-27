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
    // O mapa: posicao dos jogadores, pins das mesas de cartografia, portais, camas, mortes, reclamacoes
    // de lag e ZDOs por zona, como metricas com coordenada de mundo em metros (labels x/z). Com
    // VALHEIM_MAP_DIR, entrega tambem os dados crus do site do mapa (MapFiles): o terreno do mundo
    // (uma vez por seed, numa thread de prioridade minima), o explorado das mesas (quando muda) e as
    // construcoes (varredura aos pedacos na thread principal, com orcamento por frame). O plugin nao
    // desenha nada. Leitura das mesas a cada minuto.
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
        static readonly MapSettings Settings = MapSettings.Parse(Environment.GetEnvironmentVariable);
        // Arquivo que pede uma varredura de construcoes agora.
        const string NowFile = "pieces.now";
        const double TerrainDuty = 0.5;

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
        static double _renderSeconds;
        static long _renders;
        static long _renderErrors;

        static PieceCatalog _catalog;
        static PieceScan _scan;
        static PieceScan _lastScan;
        static int[] _piecesByKind;
        static DateTime _nextScan;
        static bool[] _writtenExplored;
        static long _exploredWrites;
        static long _piecesWrites;
        static long _exportErrors;
        static Thread _terrainThread;
        static bool _terrainChecked;
        static volatile bool _terrainReady;
        static double _terrainSeconds;

        public string Name => "map";

        public void Install(Harmony harmony)
        {
            _bySector = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");
            Patcher.Patch(harmony, typeof(ZDO), "Deserialize", new[] { typeof(ZPackage) }, typeof(MapCollector),
                postfix: nameof(DeserializePostfix), tag: "map");
            LoadNames();
            if (Dir != null)
                Plugin.Log.LogInfo($"Mapa: dados do site em {Dir}; construcoes a cada {Settings.PiecesEvery.TotalMinutes:0} min " +
                    $"(varredura {Settings.ScanBudgetMs:0.##} ms/frame).");
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
                    MaybeExportTerrain();
                    MaybeStartScan();
                }
            }

            WritePlayers(w);
            WriteStatic(w);
            WriteCounters(w);
            WriteMapState(w);
            WriteExport(w);
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

            _worker = new Thread(() => Work(data))
            {
                IsBackground = true,
                Name = "ValheimMetrics.Map",
                Priority = System.Threading.ThreadPriority.BelowNormal,
            };
            _worker.Start();
        }

        static void Work(List<byte[]> data)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var maps = new List<SharedMap>();
                foreach (var bytes in data)
                    maps.Add(SharedMap.FromCompressed(bytes));
                var map = SharedMap.Union(maps);
                _map = map;
                _renderSeconds = sw.Elapsed.TotalSeconds;
                Interlocked.Increment(ref _renders);
                WriteExplored(map.Explored);
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _renderErrors);
                Plugin.Log.LogWarning($"Mapa: leitura das mesas falhou: {e}");
            }
        }

        // Ainda na thread das mesas: so grava quando o explorado mudou.
        static void WriteExplored(bool[] explored)
        {
            if (Dir == null || ExploredFile.Same(_writtenExplored, explored))
                return;
            try
            {
                Directory.CreateDirectory(Dir);
                MapFiles.WriteAtomic(Path.Combine(Dir, MapFiles.Explored), w => ExploredFile.Write(w, explored));
                _writtenExplored = explored;
                Interlocked.Increment(ref _exploredWrites);
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _exportErrors);
                Plugin.Log.LogWarning($"Mapa: nao gravou {MapFiles.Explored}: {e.Message}");
            }
        }

        // Terreno do mundo inteiro, uma vez por seed: 4 milhoes de pontos do gerador numa thread de
        // prioridade minima que dorme na proporcao do trabalho (TerrainDuty = no maximo meio nucleo).
        static void MaybeExportTerrain()
        {
            if (Dir == null || _terrainChecked || WorldGenerator.instance == null || ZNet.World == null)
                return;
            _terrainChecked = true;
            int seed = ZNet.World.m_seed;
            var path = Path.Combine(Dir, MapFiles.Terrain);
            if (TerrainGrid.ReadSeed(path) == seed)
            {
                _terrainReady = true;
                return;
            }
            var gen = WorldGenerator.instance;
            _terrainThread = new Thread(() =>
            {
                try
                {
                    var work = Stopwatch.StartNew();
                    var grid = new TerrainGrid();
                    var row = new Stopwatch();
                    for (int i = 0; i < TerrainGrid.Size; i++)
                    {
                        row.Restart();
                        float z = TerrainGrid.Center(i);
                        for (int j = 0; j < TerrainGrid.Size; j++)
                        {
                            float x = TerrainGrid.Center(j);
                            var biome = gen.GetBiome(x, z);
                            float height = gen.GetBiomeHeight(biome, x, z, out _);
                            grid.Set(i, j, (int)biome, height, WorldGenerator.GetForestFactor(new Vector3(x, 0f, z)));
                        }
                        _terrainSeconds += row.Elapsed.TotalSeconds;
                        Thread.Sleep((int)Math.Min(1000, row.Elapsed.TotalMilliseconds * (1 / TerrainDuty - 1)));
                    }
                    Directory.CreateDirectory(Dir);
                    MapFiles.WriteAtomic(path, w => grid.Write(w, seed));
                    _terrainReady = true;
                    Plugin.Log.LogInfo($"Mapa: terreno da seed {seed} gravado ({_terrainSeconds:0.0} s de trabalho, {work.Elapsed.TotalSeconds:0} s no total).");
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref _exportErrors);
                    Plugin.Log.LogWarning($"Mapa: terreno falhou: {e}");
                }
            })
            {
                IsBackground = true,
                Name = "ValheimMetrics.MapTerrain",
                Priority = System.Threading.ThreadPriority.Lowest,
            };
            _terrainThread.Start();
        }

        // No boot (assim que as mesas foram lidas), a cada PiecesEvery ou com o arquivo pieces.now.
        static void MaybeStartScan()
        {
            if (Dir == null || _map == null || _scan != null)
                return;
            var nowFile = Path.Combine(Dir, NowFile);
            bool asked = File.Exists(nowFile);
            if (!asked && DateTime.Now < _nextScan)
                return;
            if (asked)
                File.Delete(nowFile);
            _nextScan = DateTime.Now + Settings.PiecesEvery;
            if (_catalog == null)
            {
                var sw = Stopwatch.StartNew();
                _catalog = PieceCatalog.Build(ZNetScene.instance);
                Plugin.Log.LogInfo($"Mapa: {_catalog.Count} prefabs de peca no catalogo ({sw.ElapsedMilliseconds} ms); maiores: {_catalog.Largest(8)}.");
            }
            _scan = new PieceScan(_bySector(ZDOMan.instance), _map.Explored, _catalog);
        }

        // Todo frame: um pedaco da varredura, dentro do orcamento. Terminou, o arquivo e gravado fora
        // da thread principal.
        public static void OnFrame()
        {
            var scan = _scan;
            if (scan == null)
                return;
            try
            {
                scan.Step(Settings.ScanBudgetMs / 1000.0);
                if (!scan.Done)
                    return;
                _scan = null;
                _lastScan = scan;
                var marks = scan.Marks;
                var byKind = new int[Enum.GetValues(typeof(PieceKind)).Length];
                foreach (var m in marks)
                    byKind[(int)m.Kind]++;
                _piecesByKind = byKind;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        Directory.CreateDirectory(Dir);
                        MapFiles.WriteAtomic(Path.Combine(Dir, MapFiles.Pieces), w => PiecesFile.Write(w, marks));
                        Interlocked.Increment(ref _piecesWrites);
                    }
                    catch (Exception e)
                    {
                        Interlocked.Increment(ref _exportErrors);
                        Plugin.Log.LogWarning($"Mapa: nao gravou {MapFiles.Pieces}: {e.Message}");
                    }
                });
                Plugin.Log.LogInfo($"Mapa: {marks.Count} pecas em {scan.Zdos} ZDOs, {scan.Seconds * 1000:0} ms em {scan.Frames} frames " +
                    $"(pior frame {scan.MaxFrameSeconds * 1000:0.0} ms).");
            }
            catch (Exception e)
            {
                _scan = null;
                Interlocked.Increment(ref _exportErrors);
                Plugin.Log.LogWarning($"Mapa: varredura das pecas falhou: {e}");
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
            w.Family("valheim_map_render_seconds", "gauge", "Duracao da ultima leitura das mesas.");
            w.Sample("valheim_map_render_seconds", _renderSeconds);
            w.Family("valheim_map_renders_total", "counter", "Leituras das mesas concluidas.");
            w.Sample("valheim_map_renders_total", Interlocked.Read(ref _renders));
            w.Family("valheim_map_render_errors_total", "counter", "Leituras das mesas que falharam (detalhe no log).");
            w.Sample("valheim_map_render_errors_total", Interlocked.Read(ref _renderErrors));
        }

        // Custo e frescor dos arquivos do site.
        static void WriteExport(PrometheusWriter w)
        {
            if (Dir == null)
                return;
            w.Family("valheim_map_terrain_ready", "gauge", "1 quando o terrain.bin da seed atual esta no disco.");
            w.Sample("valheim_map_terrain_ready", _terrainReady ? 1 : 0);
            w.Family("valheim_map_terrain_work_seconds", "gauge", "Trabalho da geracao do terreno neste boot, sem o sono (0 = ja existia).");
            w.Sample("valheim_map_terrain_work_seconds", _terrainSeconds);
            w.Family("valheim_map_file_writes_total", "counter", "Arquivos do site gravados, por arquivo.");
            w.Sample("valheim_map_file_writes_total", Interlocked.Read(ref _exploredWrites), "file", "explored");
            w.Sample("valheim_map_file_writes_total", Interlocked.Read(ref _piecesWrites), "file", "pieces");
            w.Family("valheim_map_export_errors_total", "counter", "Falhas ao gerar ou gravar os arquivos do site (detalhe no log).");
            w.Sample("valheim_map_export_errors_total", Interlocked.Read(ref _exportErrors));
            w.Family("valheim_map_pieces_next_timestamp_seconds", "gauge", "Proxima varredura de construcoes.");
            w.Sample("valheim_map_pieces_next_timestamp_seconds", _nextScan == default ? 0 : new DateTimeOffset(_nextScan).ToUnixTimeSeconds());

            var scan = _lastScan;
            if (scan != null)
            {
                w.Family("valheim_map_piece_scan_seconds", "gauge", "Tempo total da ultima varredura de pecas na thread principal.");
                w.Sample("valheim_map_piece_scan_seconds", scan.Seconds);
                w.Family("valheim_map_piece_scan_frame_seconds_max", "gauge", "Pior frame da ultima varredura de pecas (o que o jogador pode sentir).");
                w.Sample("valheim_map_piece_scan_frame_seconds_max", scan.MaxFrameSeconds);
                w.Family("valheim_map_piece_scan_frames", "gauge", "Frames que a ultima varredura de pecas ocupou.");
                w.Sample("valheim_map_piece_scan_frames", scan.Frames);
                w.Family("valheim_map_piece_scan_zdos", "gauge", "ZDOs olhados na ultima varredura (so zonas que as mesas mostram).");
                w.Sample("valheim_map_piece_scan_zdos", scan.Zdos);
            }
            var byKind = _piecesByKind;
            if (byKind != null)
            {
                w.Family("valheim_map_pieces", "gauge", "Construcoes no pieces.bin por tipo (material da construcao ou funcao).");
                foreach (PieceKind k in Enum.GetValues(typeof(PieceKind)))
                    w.Sample("valheim_map_pieces", byKind[(int)k], "kind", k.ToString().ToLowerInvariant());
            }
        }
    }
}
