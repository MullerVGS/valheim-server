using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using HarmonyLib;
using UnityEngine;
using ValheimMetrics.Collectors;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Recording
{
    // Records what players do around a boss (or an admin) to a file, for replaying it later with
    // the game's own models. Passive: it copies what the clients already send the server (ZDO data
    // and routed RPCs) and sends nothing back, so the game's traffic does not change.
    //
    // Hot paths only test the session and copy bytes into a buffer; a thread compresses to disk.
    // Starts: a boss ZDO reaching the server (VALHEIM_RECORDER=1), or the client mod's control RPC
    // from an admin. Files: <save dir>/recordings/<local time>-<label>.vrec.gz (RecordFormat).
    sealed class Recorder : ICollector
    {
        public const string ControlRpc = "valheim-server.recorder";
        public const string StateRpc = "valheim-server.recorder.state";
        const int TopLeft = 1;
        const int FlushAt = RecordFile.ChunkBytes * 3 / 4;
        const long MaxQueuedBytes = 64L << 20;
        const double WorldEvery = 0.5;
        const double FlushEvery = 1;
        const double SnapshotBudgetSeconds = 0.0005;

        delegate void FindObjectsFn(ZDOMan man, Vector2s sector, List<ZDO> objects, HashSet<ZoneSystem.SectorIndex> visited);

        static readonly AccessTools.FieldRef<ZPackage, MemoryStream> PackageStream =
            AccessTools.FieldRefAccess<ZPackage, MemoryStream>("m_stream");
        static readonly int PingHash = "RPC_Ping".GetStableHashCode();

        static RecorderSettings _settings;
        static SessionRules<ZDOID> _rules;
        static FindObjectsFn _findObjects;
        static RecordBuffer _buffer;
        static RecordFile _file;
        static readonly List<RecordFile> _closing = new List<RecordFile>();
        static double _startedAt;
        static double _retryAt;
        static string _label = "";
        static string _reason = "";

        static readonly HashSet<int> _bosses = new HashSet<int>();
        static bool _bossesBuilt;
        static readonly HashSet<ZDOID> _newBosses = new HashSet<ZDOID>();
        static readonly List<ZDOID> _anchorIds = new List<ZDOID>();

        static readonly HashSet<int> _prefabsNamed = new HashSet<int>();
        static readonly HashSet<int> _methodsNamed = new HashSet<int>();
        static readonly Dictionary<long, ZDOID> _peersNamed = new Dictionary<long, ZDOID>();

        static readonly Queue<long> _zones = new Queue<long>();
        static readonly HashSet<long> _zonesSeen = new HashSet<long>();
        static readonly List<ZDO> _zoneObjects = new List<ZDO>();
        static readonly HashSet<ZoneSystem.SectorIndex> _visited = new HashSet<ZoneSystem.SectorIndex>();
        static int _zoneIndex;
        static readonly ZPackage _scratch = new ZPackage();

        static readonly HashSet<long> _controllers = new HashSet<long>();
        static ZRoutedRpc _registeredOn;
        static double _nextWorld;
        static double _nextFlush;
        static bool _dropMarker;

        static long _sessions;
        static readonly long[] _records = new long[RecordFormat.KindCount];
        static long _sessionBytes;
        static long _droppedBytes;
        static long _ticks;
        static long _errors;
        static string _lastFile = "";
        static long _lastFileBytes;

        public string Name => "recorder";

        public void Install(Harmony harmony)
        {
            var raw = Environment.GetEnvironmentVariable(RecorderSettings.Variable);
            _settings = RecorderSettings.Parse(raw, Environment.GetEnvironmentVariable(RecorderSettings.RadiusVariable));
            if (_settings == null)
            {
                if (!string.IsNullOrWhiteSpace(raw))
                    Plugin.Log.LogWarning($"{RecorderSettings.Variable}={raw} ignored: expected 1 or manual.");
                return;
            }
            _findObjects = AccessTools.MethodDelegate<FindObjectsFn>(
                AccessTools.Method(typeof(ZDOMan), "FindObjects",
                    new[] { typeof(Vector2s), typeof(List<ZDO>), typeof(HashSet<ZoneSystem.SectorIndex>) })
                ?? throw new MissingMethodException("ZDOMan", "FindObjects"));
            var self = typeof(Recorder);
            if (!Patcher.Patch(harmony, typeof(ZDO), "Deserialize", new[] { typeof(ZPackage) }, self,
                    postfix: nameof(DeserializePostfix), tag: "recorder"))
                return;
            Patcher.Patch(harmony, typeof(ZRoutedRpc.RoutedRPCData), "Deserialize", new[] { typeof(ZPackage) }, self,
                postfix: nameof(RoutedPostfix), tag: "recorder");
            _rules = new SessionRules<ZDOID>(_settings);
            Plugin.Log.LogInfo($"Recorder: {(_settings.AutoBoss ? "bosses and admins" : "admins")} start recordings, radius {_settings.Radius} m, in {Directory()}.");
        }

        // --- hot paths -------------------------------------------------------------------------

        static void DeserializePostfix(ZDO __instance, ZPackage __0)
        {
            if (_rules == null)
                return;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                if (_bossesBuilt && _bosses.Contains(__instance.GetPrefab()) && !_rules.IsBossAnchor(__instance.m_uid))
                    _newBosses.Add(__instance.m_uid);
                if (!_rules.Active)
                    return;
                var p = __instance.GetPosition();
                if (!_rules.Inside(p.x, p.y, p.z))
                    return;
                var stream = PackageStream(__0);
                WriteZdo(RecordKind.Zdo, __instance, stream.GetBuffer(), (int)stream.Length);
            }
            catch
            {
                _errors++;
                Patcher.Errors++;
            }
            finally
            {
                _ticks += Stopwatch.GetTimestamp() - t0;
            }
        }

        static void RoutedPostfix(ZRoutedRpc.RoutedRPCData __instance)
        {
            if (_rules == null || !_rules.Active)
                return;
            long t0 = Stopwatch.GetTimestamp();
            int at = -1;
            try
            {
                int method = __instance.m_methodHash;
                if (method == PingHash)
                    return;
                var target = __instance.m_targetZDO;
                if (!target.IsNone())
                {
                    var zdo = ZDOMan.instance?.GetZDO(target);
                    if (zdo == null)
                        return;
                    var p = zdo.GetPosition();
                    if (!_rules.Inside(p.x, p.y, p.z))
                        return;
                }
                NameMethod(method);
                var stream = PackageStream(__instance.m_parameters);
                at = _buffer.Length;
                _buffer.Begin(RecordKind.Routed, Ms());
                _buffer.I64(__instance.m_senderPeerID);
                _buffer.I64(__instance.m_targetPeerID);
                Id(target);
                _buffer.I32(method);
                _buffer.Blob(stream.GetBuffer(), (int)stream.Length);
                _buffer.End();
                Counted(RecordKind.Routed);
            }
            catch
            {
                if (at >= 0)
                    _buffer.Abort(at);
                _errors++;
                Patcher.Errors++;
            }
            finally
            {
                _ticks += Stopwatch.GetTimestamp() - t0;
            }
        }

        static void WriteZdo(RecordKind kind, ZDO zdo, byte[] data, int length)
        {
            NamePrefab(zdo.GetPrefab());
            int at = _buffer.Length;
            try
            {
                var p = zdo.GetPosition();
                _buffer.Begin(kind, Ms());
                Id(zdo.m_uid);
                _buffer.U16(zdo.OwnerRevision);
                _buffer.U32(zdo.DataRevision);
                _buffer.I64(zdo.GetOwner());
                _buffer.F32(p.x);
                _buffer.F32(p.y);
                _buffer.F32(p.z);
                _buffer.Blob(data, length);
                _buffer.End();
                Counted(kind);
            }
            catch
            {
                _buffer.Abort(at);
                throw;
            }
        }

        // --- frame -----------------------------------------------------------------------------

        public static void OnFrame(double now)
        {
            if (_rules == null)
                return;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                Finish();
                if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null || ZNetScene.instance == null)
                    return;
                BuildBosses();
                Register();
                TakeBosses(now);
                if (!_rules.Active)
                    return;
                Anchors(now);
                if (now >= _nextWorld)
                {
                    _nextWorld = now + WorldEvery;
                    World();
                }
                Snapshot();
                var reason = _file.Error != null ? StopReason.WriteFailed : _rules.Check(now, _sessionBytes);
                if (reason != StopReason.None)
                    Stop(reason.ToString());
                else if (_buffer.Length >= FlushAt || now >= _nextFlush)
                    Flush(now);
            }
            catch (Exception e)
            {
                _errors++;
                Patcher.Errors++;
                Plugin.Log.LogWarning($"Recorder: {e.Message}");
            }
            finally
            {
                _ticks += Stopwatch.GetTimestamp() - t0;
            }
        }

        static void BuildBosses()
        {
            if (_bossesBuilt)
                return;
            foreach (var prefab in ZNetScene.instance.m_prefabs)
            {
                var character = prefab ? prefab.GetComponent<Character>() : null;
                if (character != null && character.m_boss)
                    _bosses.Add(prefab.name.GetStableHashCode());
            }
            _bossesBuilt = true;
            Plugin.Log.LogInfo($"Recorder: {_bosses.Count} boss prefabs.");
        }

        static void TakeBosses(double now)
        {
            if (_newBosses.Count == 0)
                return;
            // A file that cannot be created is retried once a minute, not on every boss update.
            if (!_rules.Active && now < _retryAt)
            {
                _newBosses.Clear();
                return;
            }
            foreach (var id in _newBosses)
            {
                var zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null)
                    continue;
                var p = zdo.GetPosition();
                if (!PlayerNear(p))
                    continue;
                string name = PrefabName(zdo.GetPrefab());
                bool wasActive = _rules.Active;
                if (_rules.Boss(id, p.x, p.y, p.z, now))
                {
                    if (!Start(name, "boss", now))
                        break;
                }
                else if (!wasActive)
                {
                    continue;
                }
                QueueZones(p);
                Marker("boss:" + name);
                Plugin.Log.LogInfo($"Recorder: boss {name} at ({p.x:F0}, {p.y:F0}, {p.z:F0}) anchors {_label}.");
            }
            _newBosses.Clear();
        }

        static void Anchors(double now)
        {
            _rules.CopyAnchors(_anchorIds);
            foreach (var id in _anchorIds)
            {
                var zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null || !zdo.IsValid())
                    continue;
                var p = zdo.GetPosition();
                if (!_rules.IsBossAnchor(id) || PlayerNear(p))
                    _rules.Seen(id, p.x, p.y, p.z, now);
                else
                    _rules.Moved(id, p.x, p.y, p.z);
            }
        }

        static bool PlayerNear(Vector3 p)
        {
            float r2 = _settings.Radius * _settings.Radius;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer.IsReady() && (peer.m_refPos - p).sqrMagnitude <= r2)
                    return true;
            }
            return false;
        }

        // Reference positions of every player, and who they are (again when a death gives a new character).
        static void World()
        {
            var peers = ZNet.instance.GetPeers();
            foreach (var peer in peers)
            {
                if (!peer.IsReady())
                    continue;
                if (_peersNamed.TryGetValue(peer.m_uid, out var character) && character == peer.m_characterID)
                    continue;
                _peersNamed[peer.m_uid] = peer.m_characterID;
                _buffer.Begin(RecordKind.Peer, Ms());
                _buffer.I64(peer.m_uid);
                _buffer.Str(peer.m_playerName);
                _buffer.Str(peer.m_socket?.GetHostName() ?? "");
                Id(peer.m_characterID);
                _buffer.End();
                Counted(RecordKind.Peer);
            }

            int count = 0;
            foreach (var peer in peers)
            {
                if (peer.IsReady())
                    count++;
            }
            _buffer.Begin(RecordKind.World, Ms());
            _buffer.F64(ZNet.instance.GetTimeSeconds());
            _buffer.U16((ushort)count);
            foreach (var peer in peers)
            {
                if (!peer.IsReady())
                    continue;
                _buffer.I64(peer.m_uid);
                _buffer.F32(peer.m_refPos.x);
                _buffer.F32(peer.m_refPos.y);
                _buffer.F32(peer.m_refPos.z);
            }
            _buffer.End();
            Counted(RecordKind.World);
        }

        // What was already in the area: zone by zone, within a small budget per frame.
        static void QueueZones(Vector3 p)
        {
            var center = ZoneSystem.GetZone(p);
            int k = Mathf.CeilToInt(_settings.Radius / ZoneSystem.c_ZoneSize);
            float reach = _settings.Radius + ZoneSystem.c_ZoneSize * 0.71f;
            for (int x = center.x - k; x <= center.x + k; x++)
            {
                for (int y = center.y - k; y <= center.y + k; y++)
                {
                    var zone = new Vector2s(x, y);
                    var mid = ZoneSystem.GetZonePos(zone);
                    if (new Vector2(mid.x - p.x, mid.z - p.z).magnitude > reach)
                        continue;
                    long key = (long)x << 32 | (uint)y;
                    if (_zonesSeen.Add(key))
                        _zones.Enqueue(key);
                }
            }
        }

        static void Snapshot()
        {
            if (_zones.Count == 0 && _zoneIndex >= _zoneObjects.Count)
                return;
            long until = Stopwatch.GetTimestamp() + (long)(SnapshotBudgetSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < until)
            {
                if (_zoneIndex >= _zoneObjects.Count)
                {
                    if (_zones.Count == 0)
                        return;
                    long key = _zones.Dequeue();
                    _zoneObjects.Clear();
                    _visited.Clear();
                    _zoneIndex = 0;
                    _findObjects(ZDOMan.instance, new Vector2s((int)(key >> 32), (int)(uint)key), _zoneObjects, _visited);
                    continue;
                }
                var zdo = _zoneObjects[_zoneIndex++];
                if (zdo == null || !zdo.IsValid())
                    continue;
                var p = zdo.GetPosition();
                if (!_rules.Inside(p.x, p.y, p.z))
                    continue;
                _scratch.Clear();
                zdo.Serialize(_scratch);
                int length = _scratch.Size();
                WriteZdo(RecordKind.Snapshot, zdo, PackageStream(_scratch).GetBuffer(), length);
            }
        }

        // --- session ---------------------------------------------------------------------------

        // False (and no session) when the file cannot be created.
        static bool Start(string label, string reason, double now)
        {
            var stem = Path.Combine(Directory(), $"{DateTime.Now:yyyyMMdd-HHmmss}-{SessionRules<ZDOID>.Slug(label)}");
            var path = stem + ".vrec.gz";
            for (int n = 2; File.Exists(path); n++)
                path = $"{stem}-{n}.vrec.gz";
            try
            {
                _file = new RecordFile(path, MaxQueuedBytes);
            }
            catch (Exception e)
            {
                _rules.Stop();
                _errors++;
                _retryAt = now + 60;
                Plugin.Log.LogWarning($"Recorder: cannot create {path}: {e.Message}");
                return false;
            }
            _label = label;
            _reason = reason;
            _startedAt = now;
            _sessionBytes = 0;
            _prefabsNamed.Clear();
            _methodsNamed.Clear();
            _peersNamed.Clear();
            _zones.Clear();
            _zonesSeen.Clear();
            _zoneObjects.Clear();
            _zoneIndex = 0;
            _nextWorld = 0;
            _nextFlush = now + FlushEvery;
            _dropMarker = false;

            _buffer = new RecordBuffer(RecordFile.ChunkBytes);
            _buffer.Header();
            _buffer.Begin(RecordKind.Meta, 0);
            _buffer.Str(Plugin.Version);
            _buffer.Str(global::Version.GetVersionString(false));
            _buffer.Str(ZNet.instance.GetWorldName());
            _buffer.Str(label);
            _buffer.Str(reason);
            _buffer.Str(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            _buffer.F64(ZNet.instance.GetTimeSeconds());
            _buffer.F32(_settings.Radius);
            _buffer.End();
            Counted(RecordKind.Meta);
            _sessions++;
            _lastFile = Path.GetFileName(path);
            Plugin.Log.LogInfo($"Recorder: recording {label} ({reason}) to {path}.");
            Notify($"<color=red>●</color> Recording <color=orange>{label}</color>.");
            return true;
        }

        static void Stop(string reason)
        {
            Marker("stop:" + reason);
            Flush(Time.realtimeSinceStartupAsDouble);
            var minutes = (Time.realtimeSinceStartupAsDouble - _startedAt) / 60;
            Plugin.Log.LogInfo($"Recorder: {_label} stopped ({reason}) after {minutes:F1} min, {_sessionBytes / 1048576.0:F1} MB raw" +
                               (_file.DroppedBytes > 0 ? $", {_file.DroppedBytes / 1048576.0:F1} MB dropped." : "."));
            _file.Close();
            _closing.Add(_file);
            _rules.Stop();
            Notify($"Recording of <color=orange>{_label}</color> saved ({minutes:F0} min).");
        }

        // Closes the current file; called when the server quits.
        public static void Shutdown()
        {
            if (_rules == null || !_rules.Active)
                return;
            try
            {
                Stop("shutdown");
                foreach (var f in _closing)
                    f.Wait(3000);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Recorder shutdown: {e.Message}");
            }
        }

        static void Finish()
        {
            for (int i = _closing.Count - 1; i >= 0; i--)
            {
                var f = _closing[i];
                if (!f.Finished)
                    continue;
                _closing.RemoveAt(i);
                if (f.Error != null)
                    Plugin.Log.LogWarning($"Recorder: {Path.GetFileName(f.Path)} failed: {f.Error.Message}");
                else
                    Plugin.Log.LogInfo($"Recorder: {Path.GetFileName(f.Path)} closed, {f.CompressedBytes / 1048576.0:F1} MB.");
                if (Path.GetFileName(f.Path) == _lastFile)
                    _lastFileBytes = f.CompressedBytes;
            }
        }

        static void Flush(double now)
        {
            _nextFlush = now + FlushEvery;
            int length = _buffer.Length;
            if (length == 0)
                return;
            var full = _buffer.Swap(_file.Rent());
            if (_file.Submit(full, length))
            {
                _sessionBytes += length;
                if (_dropMarker)
                {
                    _dropMarker = false;
                    Marker("dropped");
                }
            }
            else
            {
                _droppedBytes += length;
                _dropMarker = true;
            }
        }

        static void Marker(string text)
        {
            _buffer.Begin(RecordKind.Marker, Ms());
            _buffer.Str(text);
            _buffer.End();
            Counted(RecordKind.Marker);
        }

        // --- control from the client mod --------------------------------------------------------

        static void Register()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || rpc == _registeredOn)
                return;
            rpc.Register<string, string>(ControlRpc, OnControl);
            _registeredOn = rpc;
            _controllers.Clear();
        }

        // Commands: hello (the mod connected), start <label>, stop, mark <text>. Admins only.
        static void OnControl(long sender, string command, string text)
        {
            try
            {
                var net = ZNet.instance;
                var peer = net?.GetPeer(sender);
                if (peer == null || !net.IsAdmin(peer.m_socket.GetHostName()))
                    return;
                _controllers.Add(sender);
                double now = Time.realtimeSinceStartupAsDouble;
                switch (command)
                {
                    case "hello":
                        SendState(sender);
                        break;
                    case "start":
                        var zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
                        if (zdo == null)
                        {
                            Tell(sender, "Recorder: your character is not on the server yet.");
                            return;
                        }
                        var p = zdo.GetPosition();
                        var label = string.IsNullOrWhiteSpace(text) ? peer.m_playerName : text.Trim();
                        if (_rules.Hold(peer.m_characterID, p.x, p.y, p.z, now))
                        {
                            if (!Start(label, "manual", now))
                            {
                                Tell(sender, "Recorder: could not create the file (see the server log).");
                                return;
                            }
                        }
                        else
                            Notify($"<color=orange>{peer.m_playerName}</color> holds the recording of <color=orange>{_label}</color>.");
                        QueueZones(p);
                        Marker("hold:" + peer.m_playerName);
                        break;
                    case "stop":
                        if (!_rules.Active)
                        {
                            Tell(sender, "Nothing is being recorded.");
                            return;
                        }
                        Marker("release:" + peer.m_playerName);
                        _rules.Release();
                        if (_rules.Check(now, _sessionBytes) != StopReason.None)
                            Stop("released");
                        else
                            Notify($"Recording of <color=orange>{_label}</color> goes on while the boss lives.");
                        break;
                    case "mark":
                        if (!_rules.Active)
                        {
                            Tell(sender, "Nothing is being recorded.");
                            return;
                        }
                        var mark = string.IsNullOrWhiteSpace(text) ? "mark" : text.Trim();
                        Marker("mark:" + mark);
                        Tell(sender, $"Mark <color=orange>{mark}</color> at {FormatClock(now - _startedAt)}.");
                        break;
                }
            }
            catch (Exception e)
            {
                _errors++;
                Plugin.Log.LogWarning($"Recorder control: {e.Message}");
            }
        }

        // Only peers that run the client mod hear about recordings; the others never notice.
        static void Notify(string text)
        {
            foreach (var uid in _controllers)
            {
                Tell(uid, text);
                SendState(uid);
            }
        }

        static void Tell(long uid, string text)
        {
            if (ZNet.instance?.GetPeer(uid) == null)
                return;
            ZRoutedRpc.instance.InvokeRoutedRPC(uid, "ShowMessage", TopLeft, text);
        }

        static void SendState(long uid)
        {
            if (ZNet.instance?.GetPeer(uid) == null)
                return;
            bool active = _rules.Active;
            ZRoutedRpc.instance.InvokeRoutedRPC(uid, StateRpc, active, active ? _label : "",
                active ? Time.realtimeSinceStartupAsDouble - _startedAt : 0.0);
        }

        // --- helpers ---------------------------------------------------------------------------

        static uint Ms() => (uint)Math.Max(0, (Time.realtimeSinceStartupAsDouble - _startedAt) * 1000);

        static void Id(ZDOID id)
        {
            _buffer.I64(id.UserID);
            _buffer.U32(id.ID);
        }

        static void Counted(RecordKind kind) => _records[(int)kind]++;

        static void NamePrefab(int hash)
        {
            if (!_prefabsNamed.Add(hash))
                return;
            _buffer.Begin(RecordKind.PrefabName, Ms());
            _buffer.I32(hash);
            _buffer.Str(PrefabName(hash));
            _buffer.End();
            Counted(RecordKind.PrefabName);
        }

        static void NameMethod(int hash)
        {
            if (!_methodsNamed.Add(hash))
                return;
            _buffer.Begin(RecordKind.MethodName, Ms());
            _buffer.I32(hash);
            _buffer.Str(RpcCollector.NameOf(hash));
            _buffer.End();
            Counted(RecordKind.MethodName);
        }

        static string PrefabName(int hash)
        {
            var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(hash) : null;
            return prefab ? prefab.name : hash.ToString("x8");
        }

        static string Directory() => Path.Combine(Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "recordings");

        static string FormatClock(double seconds) => $"{(int)(seconds / 60)}:{(int)(seconds % 60):00}";

        public void Write(PrometheusWriter w, double now)
        {
            if (_rules == null)
                return;
            w.Family("valheim_recorder_active", "gauge", "1 while a recording is being written.");
            w.Sample("valheim_recorder_active", _rules.Active ? 1 : 0);
            w.Family("valheim_recorder_sessions_total", "counter", "Recordings started since boot.");
            w.Sample("valheim_recorder_sessions_total", _sessions);
            w.Family("valheim_recorder_anchors", "gauge", "Bosses and admins the recording area follows.");
            w.Sample("valheim_recorder_anchors", _rules.AnchorCount);
            w.Family("valheim_recorder_records_total", "counter", "Records written by kind.");
            for (int i = 1; i < _records.Length; i++)
                w.Sample("valheim_recorder_records_total", _records[i], "kind", ((RecordKind)i).ToString());
            w.Family("valheim_recorder_session_bytes", "gauge", "Uncompressed bytes handed to the current or last recording.");
            w.Sample("valheim_recorder_session_bytes", _sessionBytes);
            w.Family("valheim_recorder_file_bytes", "gauge", "Compressed size of the current or last recording file.");
            w.Sample("valheim_recorder_file_bytes", _rules.Active && _file != null ? _file.CompressedBytes : _lastFileBytes);
            w.Family("valheim_recorder_queue_bytes", "gauge", "Bytes waiting for the writer thread.");
            w.Sample("valheim_recorder_queue_bytes", _file?.QueuedBytes ?? 0);
            w.Family("valheim_recorder_dropped_bytes_total", "counter", "Bytes dropped because the disk fell behind.");
            w.Sample("valheim_recorder_dropped_bytes_total", _droppedBytes);
            w.Family("valheim_recorder_main_thread_seconds_total", "counter", "Time the recorder spent on the game thread.");
            w.Sample("valheim_recorder_main_thread_seconds_total", _ticks / (double)Stopwatch.Frequency);
            w.Family("valheim_recorder_errors_total", "counter", "Records skipped by an exception.");
            w.Sample("valheim_recorder_errors_total", _errors);
            int controllers = 0;
            foreach (var uid in _controllers)
            {
                if (ZNet.instance?.GetPeer(uid) != null)
                    controllers++;
            }
            w.Family("valheim_recorder_controllers", "gauge", "Admins connected with the client mod.");
            w.Sample("valheim_recorder_controllers", controllers);
        }
    }
}
