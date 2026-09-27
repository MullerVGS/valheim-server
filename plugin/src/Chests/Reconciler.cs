using System.Collections.Generic;

namespace ValheimMetrics.Chests
{
    /// <summary>What has to change so that the container matches its marks.</summary>
    public sealed class ReconcilePlan
    {
        /// <summary>Ghosts with no mark of their kind on their slot. Removed before <see cref="AddGhosts"/> are placed.</summary>
        public readonly List<Slot> RemoveGhosts = new List<Slot>();

        /// <summary>Marked slots that are empty and get a ghost.</summary>
        public readonly List<SlotMark> AddGhosts = new List<SlotMark>();

        /// <summary>Marks that no longer hold: outside the grid, duplicated, or with another item on their slot.</summary>
        public readonly List<SlotMark> DropMarks = new List<SlotMark>();

        public bool IsEmpty => RemoveGhosts.Count == 0 && AddGhosts.Count == 0 && DropMarks.Count == 0;
    }

    public static class Reconciler
    {
        /// <summary>
        /// Rules, in order:
        /// 1. a mark outside the grid, or a second mark on the same slot, is dropped;
        /// 2. a ghost whose slot has no mark of its kind is removed, and its slot counts as empty from then on;
        /// 3. a marked slot that is empty gets a ghost;
        /// 4. a marked slot holding another kind of item loses its mark: someone put something else there.
        /// </summary>
        public static ReconcilePlan Plan(IReadOnlyList<SlotMark> marks, IReadOnlyList<SlotContent> contents, int width, int height)
        {
            var plan = new ReconcilePlan();

            var kept = new Dictionary<Slot, SlotMark>();
            foreach (var mark in marks)
            {
                bool inside = mark.Slot.X >= 0 && mark.Slot.Y >= 0 && mark.Slot.X < width && mark.Slot.Y < height;
                if (!inside || kept.ContainsKey(mark.Slot))
                    plan.DropMarks.Add(mark);
                else
                    kept.Add(mark.Slot, mark);
            }

            var occupied = new Dictionary<Slot, SlotContent>();
            foreach (var content in contents)
            {
                if (occupied.ContainsKey(content.Slot))
                    continue;
                if (content.IsGhost && !(kept.TryGetValue(content.Slot, out var mark) && mark.Kind == content.Kind))
                {
                    plan.RemoveGhosts.Add(content.Slot);
                    continue;
                }
                occupied.Add(content.Slot, content);
            }

            foreach (var mark in kept.Values)
            {
                if (!occupied.TryGetValue(mark.Slot, out var content))
                    plan.AddGhosts.Add(mark);
                else if (content.Kind != mark.Kind)
                    plan.DropMarks.Add(mark);
            }

            return plan;
        }
    }
}
