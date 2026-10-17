using System;
using System.Collections.Generic;
using System.Globalization;

namespace ValheimMetrics.Recording
{
    enum StopReason
    {
        None,
        BossGone,
        HolderGone,
        Released,
        MaxDuration,
        MaxBytes,
        WriteFailed,
    }

    sealed class RecorderSettings
    {
        public const string Variable = "VALHEIM_RECORDER";
        public const string RadiusVariable = "VALHEIM_RECORDER_RADIUS";
        public const float DefaultRadius = 150;

        public bool AutoBoss = true;
        public float Radius = DefaultRadius;
        // Seconds an anchor may go unseen before the session ends: covers the boss dying and the drops.
        public double TailSeconds = 45;
        public double MaxSeconds = 3 * 3600;
        public long MaxBytes = 6L << 30;

        // "1" = bosses start a recording by themselves and admins with the client mod start one by hand;
        // "manual" = only by hand. Anything else (empty included) = off.
        public static RecorderSettings Parse(string raw, string radius)
        {
            var mode = raw?.Trim().ToLowerInvariant();
            if (mode != "1" && mode != "manual")
                return null;
            var s = new RecorderSettings { AutoBoss = mode == "1" };
            if (float.TryParse(radius?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r >= 20 && r <= 1000)
                s.Radius = r;
            return s;
        }
    }

    // When a recording runs and which part of the world it covers, without the game.
    // Anchors are the objects the area follows: bosses (alive while their ZDO exists with a player
    // in range) and admins holding the recording from the client mod. The area is a sphere of
    // `Radius` around each anchor, so dungeon interiors high above the map stay apart.
    sealed class SessionRules<TId>
    {
        sealed class Anchor
        {
            public float X, Y, Z;
            public double LastSeen;
            public bool Boss;
        }

        readonly Dictionary<TId, Anchor> _anchors = new Dictionary<TId, Anchor>();
        readonly RecorderSettings _settings;
        float _radiusSq;

        public SessionRules(RecorderSettings settings)
        {
            _settings = settings;
            _radiusSq = settings.Radius * settings.Radius;
        }

        public bool Active { get; private set; }
        public bool Held { get; private set; }
        public double StartedAt { get; private set; }
        public int AnchorCount => _anchors.Count;

        // True when this starts the session.
        public bool Boss(TId id, float x, float y, float z, double now)
        {
            if (!_settings.AutoBoss && !Active)
                return false;
            bool started = Begin(now);
            Put(id, x, y, z, now, boss: true);
            return started;
        }

        public bool Hold(TId id, float x, float y, float z, double now)
        {
            bool started = Begin(now);
            Held = true;
            Put(id, x, y, z, now, boss: false);
            return started;
        }

        // The session goes on while a boss anchor lives; without one it ends on the next Check.
        public void Release()
        {
            Held = false;
            var holders = new List<TId>();
            foreach (var kv in _anchors)
            {
                if (!kv.Value.Boss)
                    holders.Add(kv.Key);
            }
            foreach (var id in holders)
                _anchors.Remove(id);
        }

        public bool IsAnchor(TId id) => _anchors.ContainsKey(id);

        public bool IsBossAnchor(TId id) => _anchors.TryGetValue(id, out var a) && a.Boss;

        // Present and relevant (for a boss: some player in range).
        public void Seen(TId id, float x, float y, float z, double now)
        {
            if (!_anchors.TryGetValue(id, out var a))
                return;
            a.X = x;
            a.Y = y;
            a.Z = z;
            a.LastSeen = now;
        }

        // Present but nobody around: the area follows it, the clock does not reset.
        public void Moved(TId id, float x, float y, float z)
        {
            if (!_anchors.TryGetValue(id, out var a))
                return;
            a.X = x;
            a.Y = y;
            a.Z = z;
        }

        public bool Inside(float x, float y, float z)
        {
            foreach (var a in _anchors.Values)
            {
                float dx = x - a.X, dy = y - a.Y, dz = z - a.Z;
                if (dx * dx + dy * dy + dz * dz <= _radiusSq)
                    return true;
            }
            return false;
        }

        public void CopyAnchors(List<TId> into)
        {
            into.Clear();
            into.AddRange(_anchors.Keys);
        }

        public StopReason Check(double now, long bytes)
        {
            if (!Active)
                return StopReason.None;
            if (now - StartedAt > _settings.MaxSeconds)
                return StopReason.MaxDuration;
            if (bytes > _settings.MaxBytes)
                return StopReason.MaxBytes;
            bool boss = false, holder = false;
            foreach (var a in _anchors.Values)
            {
                if (now - a.LastSeen > _settings.TailSeconds)
                    continue;
                if (a.Boss)
                    boss = true;
                else
                    holder = true;
            }
            if (boss || (Held && holder))
                return StopReason.None;
            if (Held)
                return StopReason.HolderGone;
            return _anchors.Count == 0 ? StopReason.Released : StopReason.BossGone;
        }

        public void Stop()
        {
            Active = false;
            Held = false;
            _anchors.Clear();
        }

        bool Begin(double now)
        {
            if (Active)
                return false;
            Active = true;
            StartedAt = now;
            return true;
        }

        void Put(TId id, float x, float y, float z, double now, bool boss)
        {
            if (!_anchors.TryGetValue(id, out var a))
                _anchors[id] = a = new Anchor();
            a.X = x;
            a.Y = y;
            a.Z = z;
            a.LastSeen = now;
            a.Boss |= boss;
        }

        // File name part from a label: letters, digits, '-' and '_' only.
        public static string Slug(string label)
        {
            var chars = new List<char>();
            foreach (var c in label ?? "")
            {
                if (c < 128 && (char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                    chars.Add(c);
                else if (chars.Count > 0 && chars[chars.Count - 1] != '-')
                    chars.Add('-');
                if (chars.Count >= 40)
                    break;
            }
            var s = new string(chars.ToArray()).Trim('-');
            return s.Length == 0 ? "scene" : s;
        }
    }
}
