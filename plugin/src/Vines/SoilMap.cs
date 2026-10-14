using System;
using System.Collections.Generic;
using System.IO;

namespace ValheimMetrics.Vines
{
    // Cultivated vertices of one zone, read from the TerrainComp data (ZDO "TCData", already
    // decompressed): version, operations, last op point and radius, the height deltas, then the
    // paint mask. Heightmap.IsCultivated is green above 0.5; vertex i sits at (i % pitch, i / pitch)
    // counted from the zone center minus half the pitch (Heightmap.WorldToVertexMask).
    public static class SoilMap
    {
        public static List<(int X, int Z)> Cultivated(byte[] data, double centerX, double centerZ)
        {
            var found = new List<(int, int)>();
            using var reader = new BinaryReader(new MemoryStream(data));
            reader.ReadInt32();
            reader.ReadInt32();
            reader.ReadBytes(16);
            int heights = reader.ReadInt32();
            for (int i = 0; i < heights; i++)
                if (reader.ReadBoolean())
                    reader.ReadBytes(8);
            int paints = reader.ReadInt32();
            int pitch = (int)Math.Round(Math.Sqrt(paints));
            int half = pitch / 2;
            for (int i = 0; i < paints; i++)
            {
                if (!reader.ReadBoolean())
                    continue;
                reader.ReadSingle();
                float green = reader.ReadSingle();
                reader.ReadSingle();
                reader.ReadSingle();
                if (green > 0.5f)
                    found.Add(((int)Math.Round(centerX) + i % pitch - half, (int)Math.Round(centerZ) + i / pitch - half));
            }
            return found;
        }

        // Any cultivated vertex within reach, on the ground plane.
        public static bool Near(ICollection<long> soil, double x, double z, double reach)
        {
            int r = (int)Math.Ceiling(reach);
            int cx = (int)Math.Round(x), cz = (int)Math.Round(z);
            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    double ox = cx + dx - x, oz = cz + dz - z;
                    if (ox * ox + oz * oz < reach * reach && soil.Contains(Key(cx + dx, cz + dz)))
                        return true;
                }
            return false;
        }

        public static long Key(int x, int z) => ((long)x << 32) | (uint)z;
    }
}
