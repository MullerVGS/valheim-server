using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimMetrics.Chests
{
    /// <summary>
    /// Text form of a container's marks, kept in a ZDO string: <c>1|x,y,prefab,quality,worldLevel|...</c>.
    /// Decoding never throws; entries it cannot read are skipped.
    /// </summary>
    public static class MarkCodec
    {
        private const string Version = "1";

        public static string Encode(IEnumerable<SlotMark> marks)
        {
            var text = new StringBuilder();
            foreach (var mark in marks)
            {
                if (!Encodable(mark.Kind.Prefab))
                    continue;
                text.Append('|')
                    .Append(mark.Slot.X.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(mark.Slot.Y.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(mark.Kind.Prefab).Append(',')
                    .Append(mark.Kind.Quality.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(mark.Kind.WorldLevel.ToString(CultureInfo.InvariantCulture));
            }
            return text.Length == 0 ? "" : Version + text;
        }

        public static List<SlotMark> Decode(string text)
        {
            var marks = new List<SlotMark>();
            if (string.IsNullOrEmpty(text))
                return marks;

            string[] parts = text.Split('|');
            if (parts[0] != Version)
                return marks;

            for (int i = 1; i < parts.Length; i++)
            {
                string[] fields = parts[i].Split(',');
                if (fields.Length != 5
                    || !TryInt(fields[0], out int x) || !TryInt(fields[1], out int y)
                    || !TryInt(fields[3], out int quality) || !TryInt(fields[4], out int worldLevel)
                    || fields[2].Length == 0 || x < 0 || y < 0)
                    continue;
                marks.Add(new SlotMark(new Slot(x, y), new ItemKind(fields[2], quality, worldLevel)));
            }
            return marks;
        }

        private static bool Encodable(string prefab) =>
            !string.IsNullOrEmpty(prefab) && prefab.IndexOf('|') < 0 && prefab.IndexOf(',') < 0;

        private static bool TryInt(string s, out int value) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
