using System.Collections.Generic;
using ValheimMetrics.Chests;
using Xunit;
using static ValheimMetrics.Tests.F;

namespace ValheimMetrics.Tests
{
    public class ReconcilerTests
    {
        private static ReconcilePlan Plan(SlotMark[] marks, params SlotContent[] contents) =>
            Reconciler.Plan(marks, contents, 8, 4);

        [Fact]
        public void EmptiedMarkedSlotGetsAGhost()
        {
            var plan = Plan(new[] { Mark(2, 1, Wood) });

            Assert.Equal(new[] { Mark(2, 1, Wood) }, plan.AddGhosts);
            Assert.Empty(plan.RemoveGhosts);
            Assert.Empty(plan.DropMarks);
        }

        [Fact]
        public void MarkedSlotWithItsStackOrGhostIsLeftAlone()
        {
            var plan = Plan(new[] { Mark(0, 0, Wood), Mark(1, 0, Stone) }, Stack(0, 0, Wood, 37), Ghost(1, 0, Stone));

            Assert.True(plan.IsEmpty);
        }

        [Fact]
        public void AnotherItemOnAMarkedSlotDropsTheMark()
        {
            var plan = Plan(new[] { Mark(0, 0, Wood) }, Stack(0, 0, Stone, 5));

            Assert.Equal(new[] { Mark(0, 0, Wood) }, plan.DropMarks);
            Assert.Empty(plan.AddGhosts);
        }

        [Fact]
        public void GhostWithoutMarkIsRemoved()
        {
            var plan = Plan(new SlotMark[0], Ghost(3, 2, Resin), Stack(0, 0, Wood, 1));

            Assert.Equal(new[] { S(3, 2) }, plan.RemoveGhosts);
            Assert.Empty(plan.AddGhosts);
        }

        [Fact]
        public void GhostOfTheWrongKindIsReplaced()
        {
            var plan = Plan(new[] { Mark(0, 0, Wood) }, Ghost(0, 0, Stone));

            Assert.Equal(new[] { S(0, 0) }, plan.RemoveGhosts);
            Assert.Equal(new[] { Mark(0, 0, Wood) }, plan.AddGhosts);
            Assert.Empty(plan.DropMarks);
        }

        [Fact]
        public void WorldLevelAndQualityAreDifferentKinds()
        {
            var plan = Plan(new[] { Mark(0, 0, Wood), Mark(1, 0, Wood) },
                Stack(0, 0, new ItemKind("Wood", 1, 1), 3), Stack(1, 0, new ItemKind("Wood", 2, 0), 3));

            Assert.Equal(2, plan.DropMarks.Count);
        }

        [Fact]
        public void MarksOutsideTheGridAreDropped()
        {
            var plan = Plan(new[] { Mark(8, 0, Wood), Mark(0, 4, Wood), Mark(7, 3, Wood) });

            Assert.Equal(new[] { Mark(8, 0, Wood), Mark(0, 4, Wood) }, plan.DropMarks);
            Assert.Equal(new[] { Mark(7, 3, Wood) }, plan.AddGhosts);
        }

        [Fact]
        public void SecondMarkOnTheSameSlotIsDropped()
        {
            var plan = Plan(new[] { Mark(0, 0, Wood), Mark(0, 0, Stone) });

            Assert.Equal(new[] { Mark(0, 0, Stone) }, plan.DropMarks);
            Assert.Equal(new[] { Mark(0, 0, Wood) }, plan.AddGhosts);
        }

        [Fact]
        public void UnknownItemOnAMarkedSlotDropsTheMark()
        {
            var plan = Plan(new[] { Mark(0, 0, Wood) }, Stack(0, 0, default(ItemKind), 1));

            Assert.Equal(new[] { Mark(0, 0, Wood) }, plan.DropMarks);
        }
    }
}
