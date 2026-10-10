// Contrato compartilhado com o mod de cliente que liga baus (Chest Sort): mesmos tipos e mesmo texto.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimMetrics.Chests
{
    /// <summary>
    /// One chest this chest sends items to. The target is named by its own id, never by its ZDO id: the game
    /// renumbers every object when the world loads. The position is where the target stood when linked, so a
    /// client can draw the link without having that chest loaded. An <see cref="Always"/> link also takes
    /// what has no home anywhere, whether or not the target holds that item.
    /// </summary>
    public readonly struct SortLink : IEquatable<SortLink>
    {
        public readonly ulong Target;
        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly bool Always;

        public SortLink(ulong target, float x, float y, float z, bool always = false)
        {
            Target = target;
            X = x;
            Y = y;
            Z = z;
            Always = always;
        }

        public bool Equals(SortLink other) => Target == other.Target;
        public override bool Equals(object obj) => obj is SortLink other && Equals(other);
        public override int GetHashCode() => Target.GetHashCode();
        public override string ToString() => Target.ToString("x", CultureInfo.InvariantCulture);
    }

    /// <summary>A chest that takes part in sorting: its id and the chests it sends to. No links = only receives.</summary>
    public sealed class SortNode
    {
        public ulong Id;
        public readonly List<SortLink> Links = new List<SortLink>();

        public SortNode(ulong id)
        {
            Id = id;
        }

        public int IndexOf(ulong target)
        {
            for (int i = 0; i < Links.Count; i++)
                if (Links[i].Target == target)
                    return i;
            return -1;
        }
    }

    /// <summary>
    /// Text form of a node, kept in a ZDO string: <c>1|id|target@x,y,z|target@x,y,z,a|...</c>, ids in hex
    /// and a trailing <c>a</c> on an "always" link.
    /// Decoding never throws; links it cannot read are skipped, and a node without a readable id is no node.
    /// </summary>
    public static class SortCodec
    {
        public const string Key = "valheim-server.sort";

        private const string Version = "1";
        private const string AlwaysMark = ",a";

        public static string Encode(SortNode node)
        {
            if (node == null || node.Id == 0)
                return "";
            var text = new StringBuilder(Version).Append('|').Append(Hex(node.Id));
            foreach (var link in node.Links)
            {
                if (link.Target == 0 || link.Target == node.Id)
                    continue;
                text.Append('|').Append(Hex(link.Target)).Append('@')
                    .Append(Number(link.X)).Append(',').Append(Number(link.Y)).Append(',').Append(Number(link.Z));
                if (link.Always)
                    text.Append(AlwaysMark);
            }
            return text.ToString();
        }

        public static SortNode Decode(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            string[] parts = text.Split('|');
            if (parts.Length < 2 || parts[0] != Version || !TryHex(parts[1], out ulong id) || id == 0)
                return null;

            var node = new SortNode(id);
            for (int i = 2; i < parts.Length; i++)
            {
                int at = parts[i].IndexOf('@');
                if (at <= 0 || !TryHex(parts[i].Substring(0, at), out ulong target) || target == 0 || target == id)
                    continue;
                string[] place = parts[i].Substring(at + 1).Split(',');
                bool always = place.Length == 4 && place[3] == "a";
                if ((place.Length != 3 && !always) || !TryNumber(place[0], out float x) || !TryNumber(place[1], out float y) || !TryNumber(place[2], out float z))
                    continue;
                if (node.IndexOf(target) < 0)
                    node.Links.Add(new SortLink(target, x, y, z, always));
            }
            return node;
        }

        private static string Hex(ulong value) => value.ToString("x", CultureInfo.InvariantCulture);

        private static string Number(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        private static bool TryHex(string s, out ulong value) =>
            ulong.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);

        private static bool TryNumber(string s, out float value) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
