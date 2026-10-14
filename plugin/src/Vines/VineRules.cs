using System;
using System.Collections.Generic;

namespace ValheimMetrics.Vines
{
    public readonly struct V3
    {
        public readonly double X, Y, Z;

        public V3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static V3 operator -(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public double Dot(V3 o) => X * o.X + Y * o.Y + Z * o.Z;
        public double Length => Math.Sqrt(Dot(this));
    }

    // One Ashvine segment as the server sees it in the ZDO. Wall is the segment's local +x (the wall
    // it clings to), Up its local +y and Along its local +z, as in Vine.CheckGrow.
    public struct VineSegment
    {
        public V3 Center;
        public V3 Wall;
        public V3 Up;
        public V3 Along;
        public bool HasBerry;
        public bool Enabled;
        // Server time ticks of the last harvest; 0 = unknown, eligible right away.
        public long HarvestedTicks;
    }

    // Ashvine berries with a smaller exclusion than the game's. In the game a segment only grows a
    // berry if no berry-bearing segment touches its BerryBlocker (4.86 m along the wall x 9.11 m high
    // in front of the face), and a blocked segment restarts its respawn timer. Here a segment may
    // grow one once its own respawn time passed since it was harvested, if it has 2+ connections
    // (Vine.GetBranches() > 0) and no berry on the same face is closer than the spacing.
    public static class VineRules
    {
        public const string Variable = "VALHEIM_VINE_BERRY_SPACING";
        public const double MinSpacing = 0.5;
        public const double MaxSpacing = 10;
        // Vine.m_size: a new segment grows this far from its parent.
        public const double Step = 1.5;
        // Off-axis drift of a grown segment: Vine.m_randomOffset (0.6) x Step, plus a margin.
        const double Drift = 1.1;
        // Segments this close to the face plane share the face; the other side of a wall does not.
        const double FaceDepth = 0.5;
        const double SameFaceDot = 0.9;

        public static double? ParseSpacing(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            return double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var meters)
                   && meters >= MinSpacing && meters <= MaxSpacing
                ? meters
                : (double?)null;
        }

        // Indexes of the segments that should get a berry now, oldest harvest first. Berries handed out
        // in this call count against the next ones.
        public static List<int> Plan(IReadOnlyList<VineSegment> segments, long nowTicks, long respawnTicks, double spacing)
        {
            var grid = new Grid(segments, Math.Max(spacing, Step + Drift));
            var berries = new HashSet<int>();
            var candidates = new List<int>();
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                if (s.HasBerry)
                    berries.Add(i);
                else if (s.Enabled && nowTicks - s.HarvestedTicks >= respawnTicks)
                    candidates.Add(i);
            }
            candidates.Sort((a, b) =>
            {
                int byAge = segments[a].HarvestedTicks.CompareTo(segments[b].HarvestedTicks);
                return byAge != 0 ? byAge : a.CompareTo(b);
            });

            var given = new List<int>();
            foreach (int i in candidates)
            {
                if (Connections(segments, grid, i) < 2 || Crowded(segments, grid, berries, i, spacing))
                    continue;
                berries.Add(i);
                given.Add(i);
            }
            return given;
        }

        // Like Vine.CheckGrow's BlockSensor: another segment one step away in each of the four
        // directions counts as a branch.
        public static int Connections(IReadOnlyList<VineSegment> segments, int index) =>
            Connections(segments, new Grid(segments, Step + Drift), index);

        static int Connections(IReadOnlyList<VineSegment> segments, Grid grid, int index)
        {
            var s = segments[index];
            bool left = false, right = false, top = false, bottom = false;
            foreach (int j in grid.Near(s.Center))
            {
                if (j == index)
                    continue;
                var d = segments[j].Center - s.Center;
                if (Math.Abs(d.Dot(s.Wall)) >= FaceDepth)
                    continue;
                double along = d.Dot(s.Along), up = d.Dot(s.Up);
                if (Math.Abs(up) < Drift)
                {
                    if (Math.Abs(along - Step) < Drift) left = true;
                    if (Math.Abs(along + Step) < Drift) right = true;
                }
                if (Math.Abs(along) < Drift)
                {
                    if (Math.Abs(up - Step) < Drift) top = true;
                    if (Math.Abs(up + Step) < Drift) bottom = true;
                }
            }
            return (left ? 1 : 0) + (right ? 1 : 0) + (top ? 1 : 0) + (bottom ? 1 : 0);
        }

        static bool Crowded(IReadOnlyList<VineSegment> segments, Grid grid, HashSet<int> berries, int index, double spacing)
        {
            var s = segments[index];
            foreach (int j in grid.Near(s.Center))
            {
                if (j == index || !berries.Contains(j) || !SameFace(s, segments[j]))
                    continue;
                var d = segments[j].Center - s.Center;
                double depth = d.Dot(s.Wall);
                double inPlane = Math.Sqrt(Math.Max(0, d.Dot(d) - depth * depth));
                if (inPlane < spacing)
                    return true;
            }
            return false;
        }

        static bool SameFace(VineSegment a, VineSegment b) =>
            a.Wall.Dot(b.Wall) > SameFaceDot && Math.Abs((b.Center - a.Center).Dot(a.Wall)) < FaceDepth;

        // Buckets on the ground plane; a query sees every segment within one cell.
        sealed class Grid
        {
            readonly double _cell;
            readonly Dictionary<(long, long), List<int>> _cells = new Dictionary<(long, long), List<int>>();
            readonly List<int> _near = new List<int>();

            public Grid(IReadOnlyList<VineSegment> segments, double cell)
            {
                _cell = cell;
                for (int i = 0; i < segments.Count; i++)
                {
                    var key = Key(segments[i].Center);
                    if (!_cells.TryGetValue(key, out var list))
                        _cells[key] = list = new List<int>();
                    list.Add(i);
                }
            }

            (long, long) Key(V3 p) => ((long)Math.Floor(p.X / _cell), (long)Math.Floor(p.Z / _cell));

            public List<int> Near(V3 p)
            {
                _near.Clear();
                var (cx, cz) = Key(p);
                for (long x = cx - 1; x <= cx + 1; x++)
                    for (long z = cz - 1; z <= cz + 1; z++)
                        if (_cells.TryGetValue((x, z), out var list))
                            _near.AddRange(list);
                return _near;
            }
        }
    }
}
