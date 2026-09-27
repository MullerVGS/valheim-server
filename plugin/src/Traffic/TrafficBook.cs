using System;
using System.Collections.Generic;

namespace ValheimMetrics.Traffic
{
    public readonly struct ZdoKey : IEquatable<ZdoKey>
    {
        public readonly long User;
        public readonly uint Id;

        public ZdoKey(long user, uint id)
        {
            User = user;
            Id = id;
        }

        public bool Equals(ZdoKey other) => User == other.User && Id == other.Id;
        public override bool Equals(object obj) => obj is ZdoKey other && Equals(other);
        public override int GetHashCode() => (User.GetHashCode() * 397) ^ (int)Id;
    }

    public readonly struct Zone : IEquatable<Zone>
    {
        public const float Size = 64f;

        public readonly int X;
        public readonly int Y;

        public Zone(int x, int y)
        {
            X = x;
            Y = y;
        }

        // ZoneSystem.GetZone: zonas de 64 m centradas no multiplo de 64.
        public static Zone Of(float x, float z) =>
            new Zone((int)Math.Floor((x + 32.0) / Size), (int)Math.Floor((z + 32.0) / Size));

        public float CenterX => X * Size;
        public float CenterZ => Y * Size;

        public bool Equals(Zone other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is Zone other && Equals(other);
        public override int GetHashCode() => (X * 397) ^ Y;
    }

    public sealed class Tally
    {
        public long SentBytes;
        public long SentUpdates;
        public long ReceivedBytes;
        public long ReceivedUpdates;

        public long Bytes => SentBytes + ReceivedBytes;

        public void Add(int bytes, bool sent)
        {
            if (sent)
            {
                SentBytes += bytes;
                SentUpdates++;
            }
            else
            {
                ReceivedBytes += bytes;
                ReceivedUpdates++;
            }
        }
    }

    public sealed class HotZdo
    {
        public ZdoKey Key;
        public int Prefab;
        public float X;
        public float Z;
        public long Owner;
        public readonly Tally Tally = new Tally();
    }

    public sealed class HotZone
    {
        public Zone Zone;
        public readonly Tally Tally = new Tally();
        public readonly Dictionary<int, long> BytesByPrefab = new Dictionary<int, long>();
        public int Zdos;
    }

    // Soma o que cada ZDO custa na rede. Por prefab acumula para sempre (vira contador); por ZDO e por
    // zona so dentro da janela, que o dono esvazia ao ler o ranking.
    public sealed class TrafficBook
    {
        public readonly Dictionary<int, Tally> ByPrefab = new Dictionary<int, Tally>();
        readonly Dictionary<ZdoKey, HotZdo> _zdos = new Dictionary<ZdoKey, HotZdo>();

        public int WindowZdos => _zdos.Count;

        public void Add(ZdoKey key, int prefab, float x, float z, long owner, int bytes, bool sent)
        {
            if (!ByPrefab.TryGetValue(prefab, out var total))
                ByPrefab[prefab] = total = new Tally();
            total.Add(bytes, sent);

            if (!_zdos.TryGetValue(key, out var hot))
                _zdos[key] = hot = new HotZdo { Key = key, Prefab = prefab };
            if (prefab != 0)
                hot.Prefab = prefab;
            hot.X = x;
            hot.Z = z;
            hot.Owner = owner;
            hot.Tally.Add(bytes, sent);
        }

        public Window Take(int topZdos, int topZones)
        {
            var window = new Window();
            var zones = new Dictionary<Zone, HotZone>();
            foreach (var hot in _zdos.Values)
            {
                window.Total.SentBytes += hot.Tally.SentBytes;
                window.Total.SentUpdates += hot.Tally.SentUpdates;
                window.Total.ReceivedBytes += hot.Tally.ReceivedBytes;
                window.Total.ReceivedUpdates += hot.Tally.ReceivedUpdates;

                var zone = Zone.Of(hot.X, hot.Z);
                if (!zones.TryGetValue(zone, out var hz))
                    zones[zone] = hz = new HotZone { Zone = zone };
                hz.Zdos++;
                hz.Tally.SentBytes += hot.Tally.SentBytes;
                hz.Tally.SentUpdates += hot.Tally.SentUpdates;
                hz.Tally.ReceivedBytes += hot.Tally.ReceivedBytes;
                hz.Tally.ReceivedUpdates += hot.Tally.ReceivedUpdates;
                hz.BytesByPrefab.TryGetValue(hot.Prefab, out var b);
                hz.BytesByPrefab[hot.Prefab] = b + hot.Tally.Bytes;
            }
            window.Zdos = _zdos.Count;
            window.TopZdos = Top(_zdos.Values, topZdos, h => h.Tally.Bytes);
            window.TopZones = Top(zones.Values, topZones, z => z.Tally.Bytes);
            _zdos.Clear();
            return window;
        }

        static List<T> Top<T>(IEnumerable<T> items, int n, Func<T, long> weight)
        {
            var list = new List<T>(items);
            list.Sort((a, b) => weight(b).CompareTo(weight(a)));
            if (list.Count > n)
                list.RemoveRange(n, list.Count - n);
            return list;
        }

        public sealed class Window
        {
            public readonly Tally Total = new Tally();
            public int Zdos;
            public List<HotZdo> TopZdos;
            public List<HotZone> TopZones;
        }
    }
}
