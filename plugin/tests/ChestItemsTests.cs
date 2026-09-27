using System.Collections.Generic;
using System.IO;
using System.Linq;
using ValheimMetrics.Chests;
using Xunit;

namespace ValheimMetrics.Tests
{
    // Gravador copiado do ItemData.Save / Inventory.Save decompilados (cliente de 25/09/2026).
    static class GameFormat
    {
        public sealed class Item
        {
            public float Durability = 100f;
            public int X, Y, WorldLevel;
            public bool PickedUp, Equipped, Cheated;
            public int Quality = 1, Stack = 1, Variant;
            public long CrafterId;
            public string CrafterName = "";
            public int? PrefabHash = 12345;
            public Dictionary<string, string> Custom = new Dictionary<string, string>();
        }

        public static byte[] Chest(int version, params Item[] items)
        {
            var stream = new MemoryStream();
            var w = new BinaryWriter(stream);
            w.Write(version);
            w.Write((ushort)items.Length);
            foreach (var it in items)
            {
                int flags = 0;
                flags |= it.PickedUp ? 1 : 0;
                flags |= it.Equipped ? 2 : 0;
                flags |= it.Quality != 1 ? 4 : 0;
                flags |= it.Stack != 1 ? 8 : 0;
                flags |= it.Variant != 0 ? 16 : 0;
                flags |= it.CrafterId != 0 ? 32 : 0;
                flags |= it.PrefabHash != null ? 64 : 0;
                flags |= it.Custom.Count != 0 ? 128 : 0;
                w.Write((int)(it.Durability * 100f));
                w.Write((byte)it.X);
                w.Write((byte)it.Y);
                w.Write((byte)it.WorldLevel);
                w.Write((byte)flags);
                if ((flags & 4) != 0) w.Write((ushort)it.Quality);
                if ((flags & 8) != 0) w.Write((ushort)it.Stack);
                if ((flags & 16) != 0) w.Write(it.Variant);
                if ((flags & 32) != 0) w.Write(it.CrafterId);
                if ((flags & 32) != 0) w.Write(it.CrafterName);
                if ((flags & 64) != 0) w.Write(it.PrefabHash.Value);
                if ((flags & 128) != 0) WriteNumItems(w, it.Custom.Count);
                foreach (var kv in it.Custom)
                {
                    w.Write(kv.Key);
                    w.Write(kv.Value);
                }
                if (version >= 109)
                    w.Write((byte)(it.Cheated ? 1 : 0));
            }
            w.Flush();
            return stream.ToArray();
        }

        static void WriteNumItems(BinaryWriter w, int n)
        {
            if (n < 128)
            {
                w.Write((byte)n);
                return;
            }
            w.Write((byte)((n >> 8) | 0x80));
            w.Write((byte)n);
        }
    }

    public class ChestItemsTests
    {
        static GameFormat.Item Rich()
        {
            var custom = new Dictionary<string, string>();
            for (int i = 0; i < 130; i++)
                custom["k" + i] = "valor ção " + i;
            return new GameFormat.Item
            {
                X = 7, Y = 3, WorldLevel = 2, PickedUp = true, Equipped = true, Cheated = true,
                Quality = 4, Stack = 1, Variant = 3, CrafterId = 987654321L, CrafterName = "Björn",
                PrefabHash = -55, Durability = 37.5f, Custom = custom,
            };
        }

        [Theory]
        [InlineData(108)]
        [InlineData(109)]
        public void ReadsEveryFieldAndKeepsTheBytes(int version)
        {
            var bytes = GameFormat.Chest(version,
                new GameFormat.Item { X = 0, Y = 0, Stack = 50, PrefabHash = 111 },
                Rich(),
                new GameFormat.Item { X = 1, Y = 0, Stack = 0, PrefabHash = 222 });

            Assert.True(ChestItems.TryParse(bytes, out var chest));

            Assert.Equal(version, chest.Version);
            Assert.Equal(3, chest.Items.Count);
            Assert.Equal((111, 0, 0, 50, 1), (chest.Items[0].PrefabHash, chest.Items[0].X, chest.Items[0].Y, chest.Items[0].Stack, chest.Items[0].Quality));
            Assert.Equal((-55, 7, 3, 2, 4, 1), (chest.Items[1].PrefabHash, chest.Items[1].X, chest.Items[1].Y, chest.Items[1].WorldLevel, chest.Items[1].Quality, chest.Items[1].Stack));
            Assert.Equal(0, chest.Items[2].Stack);
            Assert.Equal(bytes, chest.ToBytes());
        }

        [Fact]
        public void EmptyChest()
        {
            var bytes = GameFormat.Chest(109);

            Assert.True(ChestItems.TryParse(bytes, out var chest));
            Assert.Empty(chest.Items);
            Assert.Equal(bytes, chest.ToBytes());
        }

        [Theory]
        [InlineData(106)]
        [InlineData(107)]
        [InlineData(110)]
        public void LeavesOtherVersionsAlone(int version)
        {
            Assert.False(ChestItems.TryParse(GameFormat.Chest(version, new GameFormat.Item()), out _));
        }

        [Fact]
        public void RefusesTruncatedOrTrailingBytes()
        {
            var bytes = GameFormat.Chest(109, Rich());

            Assert.False(ChestItems.TryParse(bytes.Take(bytes.Length - 1).ToArray(), out _));
            Assert.False(ChestItems.TryParse(bytes.Concat(new byte[] { 0 }).ToArray(), out _));
            Assert.False(ChestItems.TryParse(null, out _));
        }

        [Theory]
        [InlineData(108, 1)]
        [InlineData(109, 1)]
        [InlineData(109, 3)]
        public void GhostIsWhatTheGameWritesForAStackOfZero(int version, int quality)
        {
            var expected = GameFormat.Chest(version, new GameFormat.Item { X = 5, Y = 2, WorldLevel = 1, Quality = quality, Stack = 0, PrefabHash = 999 });
            var chest = new ChestItems { Version = version };

            chest.Items.Add(chest.Ghost(999, 5, 2, 1, quality));

            Assert.Equal(expected, chest.ToBytes());
            Assert.True(ChestItems.TryParse(chest.ToBytes(), out var back));
            Assert.Equal(0, back.Items[0].Stack);
        }
    }

    public class ChestKeeperTests
    {
        static readonly Dictionary<string, ItemInfo> Db = new Dictionary<string, ItemInfo>
        {
            ["Wood"] = new ItemInfo("Wood", 1, true, false),
            ["Stone"] = new ItemInfo("Stone", 2, true, false),
            ["SwordIron"] = new ItemInfo("SwordIron", 3, false, true),
        };

        static ItemInfo? ByHash(int h) => Db.Values.Where(i => i.Hash == h).Select(i => (ItemInfo?)i).FirstOrDefault();
        static ItemInfo? ByName(string n) => Db.TryGetValue(n, out var i) ? i : (ItemInfo?)null;
        static SlotMark Mark(int x, int y, string prefab) => new SlotMark(new Slot(x, y), new ItemKind(prefab, 1, 0));

        static ChestItems Chest(params GameFormat.Item[] items)
        {
            Assert.True(ChestItems.TryParse(GameFormat.Chest(109, items), out var chest));
            return chest;
        }

        [Fact]
        public void EmptiedMarkedSlotGetsAGhostTheGameCanRead()
        {
            var chest = Chest(new GameFormat.Item { X = 0, Y = 0, Stack = 20, PrefabHash = 2 });

            var outcome = ChestKeeper.Apply(chest, new[] { Mark(3, 1, "Wood") }, 5, 2, ByHash, ByName);

            Assert.True(outcome.ItemsChanged);
            Assert.False(outcome.MarksChanged);
            var expected = GameFormat.Chest(109,
                new GameFormat.Item { X = 0, Y = 0, Stack = 20, PrefabHash = 2 },
                new GameFormat.Item { X = 3, Y = 1, Stack = 0, PrefabHash = 1 });
            Assert.Equal(expected, chest.ToBytes());
        }

        [Fact]
        public void NothingToDoWritesNothing()
        {
            var chest = Chest(new GameFormat.Item { X = 3, Y = 1, Stack = 7, PrefabHash = 1 });

            var outcome = ChestKeeper.Apply(chest, new[] { Mark(3, 1, "Wood") }, 5, 2, ByHash, ByName);

            Assert.False(outcome.ItemsChanged);
            Assert.False(outcome.MarksChanged);
        }

        [Fact]
        public void OtherItemTakesTheSlotAndTheMarkGoes()
        {
            var chest = Chest(new GameFormat.Item { X = 3, Y = 1, Stack = 7, PrefabHash = 2 });

            var outcome = ChestKeeper.Apply(chest, new[] { Mark(3, 1, "Wood") }, 5, 2, ByHash, ByName);

            Assert.False(outcome.ItemsChanged);
            Assert.True(outcome.MarksChanged);
            Assert.Empty(outcome.Marks);
        }

        [Fact]
        public void GhostWithoutMarkLeaves()
        {
            var chest = Chest(new GameFormat.Item { X = 0, Y = 0, Stack = 0, PrefabHash = 1 }, new GameFormat.Item { X = 1, Y = 0, Stack = 4, PrefabHash = 2 });

            var outcome = ChestKeeper.Apply(chest, new SlotMark[0], 5, 2, ByHash, ByName);

            Assert.Equal(1, outcome.GhostsRemoved);
            Assert.Equal(GameFormat.Chest(109, new GameFormat.Item { X = 1, Y = 0, Stack = 4, PrefabHash = 2 }), chest.ToBytes());
        }

        [Fact]
        public void NonStackableOrUnknownMarksAreDroppedWithoutGhost()
        {
            var chest = Chest();

            var outcome = ChestKeeper.Apply(chest, new[] { Mark(0, 0, "SwordIron"), Mark(1, 0, "Removed") }, 5, 2, ByHash, ByName);

            Assert.False(outcome.ItemsChanged);
            Assert.True(outcome.MarksChanged);
            Assert.Empty(outcome.Marks);
        }

        [Fact]
        public void GridGrowsToFitItemsLikeUpdateRows()
        {
            var chest = Chest(new GameFormat.Item { X = 0, Y = 4, Stack = 1, PrefabHash = 2 });

            var outcome = ChestKeeper.Apply(chest, new[] { Mark(1, 3, "Wood") }, 5, 2, ByHash, ByName);

            Assert.Equal(1, outcome.GhostsAdded);
            Assert.False(outcome.MarksChanged);
        }
    }
}
