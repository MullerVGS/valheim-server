using System.Collections.Generic;
using System.Linq;
using ValheimMetrics.Chests;
using Xunit;

namespace ValheimMetrics.Tests
{
    public class SortCodecTests
    {
        [Fact]
        public void RoundTrips()
        {
            var node = new SortNode(0xA1);
            node.Links.Add(new SortLink(0xB2, 10.5f, -3.25f, 2000f));
            node.Links.Add(new SortLink(ulong.MaxValue, 0f, 0f, -0.01f));

            string text = SortCodec.Encode(node);

            Assert.Equal("1|a1|b2@10.5,-3.25,2000|ffffffffffffffff@0,0,-0.01", text);
            var back = SortCodec.Decode(text);
            Assert.Equal(0xA1UL, back.Id);
            Assert.Equal(new[] { 0xB2UL, ulong.MaxValue }, back.Links.Select(l => l.Target));
            Assert.Equal((10.5f, -3.25f, 2000f), (back.Links[0].X, back.Links[0].Y, back.Links[0].Z));
        }

        [Fact]
        public void ChestThatOnlyReceivesKeepsItsId()
        {
            Assert.Equal("1|7", SortCodec.Encode(new SortNode(7)));
            Assert.Empty(SortCodec.Decode("1|7").Links);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("1")]
        [InlineData("2|a1")]
        [InlineData("1|0")]
        [InlineData("1|zz")]
        public void UnreadableTextIsNoNode(string text)
        {
            Assert.Null(SortCodec.Decode(text));
        }

        [Fact]
        public void SkipsLinksItCannotRead()
        {
            var node = SortCodec.Decode("1|a1|b2@1,2,3|b2@9,9,9|a1@0,0,0|0@0,0,0|c3|d4@1,2|e5@x,2,3|f6@4,5,6");

            Assert.Equal(new[] { 0xB2UL, 0xF6UL }, node.Links.Select(l => l.Target));
            Assert.Equal(1f, node.Links[0].X);
        }

        [Fact]
        public void NodeWithoutIdIsEmptyText()
        {
            Assert.Equal("", SortCodec.Encode(new SortNode(0)));
            Assert.Equal("", SortCodec.Encode(null));
        }
    }

    public class SortGraphTests
    {
        static SortNode Node(ulong id, params ulong[] targets)
        {
            var node = new SortNode(id);
            foreach (ulong target in targets)
                node.Links.Add(new SortLink(target, 0, 0, 0));
            return node;
        }

        static List<ulong> Destinations(ulong source, params SortNode[] nodes)
        {
            var byId = nodes.ToDictionary(n => n.Id);
            return SortGraph.Destinations(byId[source], id => byId.TryGetValue(id, out var node) ? node : null);
        }

        [Fact]
        public void NearestFirstThroughTheChain()
        {
            var order = Destinations(1, Node(1, 2, 3), Node(2, 4, 5), Node(3), Node(4), Node(5, 6), Node(6));

            Assert.Equal(new ulong[] { 2, 3, 4, 5, 6 }, order);
        }

        [Fact]
        public void ChestReachedTwiceAppearsOnce()
        {
            var order = Destinations(1, Node(1, 2, 3), Node(2, 3), Node(3));

            Assert.Equal(new ulong[] { 2, 3 }, order);
        }

        [Fact]
        public void ChestsInACircleDoNotSendToEachOther()
        {
            var nodes = new[] { Node(1, 2), Node(2, 3), Node(3, 1, 4), Node(4) };

            Assert.Equal(new ulong[] { 4 }, Destinations(1, nodes));
            Assert.Equal(new ulong[] { 4 }, Destinations(2, nodes));
            Assert.Equal(new ulong[] { 4 }, Destinations(3, nodes));
        }

        [Fact]
        public void LinkToAMissingChestIsIgnored()
        {
            var order = Destinations(1, Node(1, 9, 2), Node(2));

            Assert.Equal(new ulong[] { 2 }, order);
        }
    }

    public class SortPlannerTests
    {
        const int Wood = 1, Stone = 2, Sword = 3, Carrot = 4;

        static readonly Dictionary<int, ItemInfo> Db = new Dictionary<int, ItemInfo>
        {
            [Wood] = new ItemInfo("Wood", Wood, 50, false),
            [Stone] = new ItemInfo("Stone", Stone, 50, false),
            [Sword] = new ItemInfo("SwordIron", Sword, 1, true),
            [Carrot] = new ItemInfo("Carrot", Carrot, 50, false),
        };

        static ItemInfo? ByHash(int hash) => Db.TryGetValue(hash, out var info) ? info : (ItemInfo?)null;

        static GameFormat.Item Item(int x, int y, int hash, int stack = 1, int quality = 1, int worldLevel = 0) =>
            new GameFormat.Item { X = x, Y = y, PrefabHash = hash, Stack = stack, Quality = quality, WorldLevel = worldLevel };

        static SortChest Chest(int width, int height, params GameFormat.Item[] items)
        {
            Assert.True(ChestItems.TryParse(GameFormat.Chest(109, items), out var parsed));
            return new SortChest { Items = parsed, Width = width, Height = parsed.Height(height) };
        }

        static SortChest Marked(SortChest chest, params SlotMark[] marks)
        {
            chest.Marks = marks;
            return chest;
        }

        static SlotMark Mark(int x, int y, string prefab) => new SlotMark(new Slot(x, y), new ItemKind(prefab, 1, 0));

        static byte[] Bytes(params GameFormat.Item[] items) => GameFormat.Chest(109, items);

        [Fact]
        public void ItemGoesToTheChestThatAlreadyHoldsItAndTheRestStays()
        {
            var source = Chest(5, 2, Item(0, 0, Wood, 30), Item(1, 0, Stone, 12), Item(2, 0, Carrot, 5));
            var woods = Chest(5, 2, Item(0, 0, Wood, 10));
            var stones = Chest(5, 2, Item(3, 1, Stone, 50), Item(0, 0, Stone, 45));

            int moved = SortPlanner.Run(source, new[] { woods, stones }, ByHash);

            Assert.Equal(42, moved);
            Assert.Equal(Bytes(Item(2, 0, Carrot, 5)), source.Items.ToBytes());
            Assert.Equal(Bytes(Item(0, 0, Wood, 40)), woods.Items.ToBytes());
            Assert.Equal(Bytes(Item(3, 1, Stone, 50), Item(0, 0, Stone, 50), Item(1, 0, Stone, 7)), stones.Items.ToBytes());
            Assert.True(source.Changed && woods.Changed && stones.Changed);
        }

        [Fact]
        public void NothingHasAHomeNothingMoves()
        {
            var source = Chest(5, 2, Item(0, 0, Carrot, 5));
            var woods = Chest(5, 2, Item(0, 0, Wood, 10));

            Assert.Equal(0, SortPlanner.Run(source, new[] { woods }, ByHash));
            Assert.False(source.Changed || woods.Changed);
            Assert.True(SortPlanner.HasCargo(source, ByHash));
        }

        [Fact]
        public void FullChestOverflowsToTheNextOneAndLeavesWhatDoesNotFit()
        {
            var source = Chest(5, 2, Item(0, 0, Wood, 50), Item(1, 0, Wood, 50));
            var near = Chest(1, 1, Item(0, 0, Wood, 40));
            var far = Chest(2, 1, Item(0, 0, Wood, 50));

            int moved = SortPlanner.Run(source, new[] { near, far }, ByHash);

            Assert.Equal(60, moved);
            Assert.Equal(Bytes(Item(0, 0, Wood, 50)), near.Items.ToBytes());
            Assert.Equal(Bytes(Item(0, 0, Wood, 50), Item(1, 0, Wood, 50)), far.Items.ToBytes());
            Assert.Equal(Bytes(Item(1, 0, Wood, 40)), source.Items.ToBytes());
        }

        [Fact]
        public void GhostIsAHomeAndGetsFilled()
        {
            var source = Chest(5, 2, Item(0, 0, Wood, 20));
            var woods = Chest(5, 2, Item(2, 1, Wood, 0));

            SortPlanner.Run(source, new[] { woods }, ByHash);

            Assert.Empty(source.Items.Items);
            Assert.Equal(Bytes(Item(2, 1, Wood, 20)), woods.Items.ToBytes());
        }

        [Fact]
        public void GhostsAndReservedItemsStayInTheSource()
        {
            var source = Marked(Chest(5, 2, Item(0, 0, Wood, 0), Item(1, 0, Stone, 8), Item(2, 0, Stone, 3)), Mark(1, 0, "Stone"));
            var both = Chest(5, 2, Item(0, 0, Wood, 1), Item(1, 0, Stone, 1));

            int moved = SortPlanner.Run(source, new[] { both }, ByHash);

            Assert.Equal(3, moved);
            Assert.Equal(Bytes(Item(0, 0, Wood, 0), Item(1, 0, Stone, 8)), source.Items.ToBytes());
        }

        [Fact]
        public void EmptyReservedSlotIsAHomeAndNoOtherItemTakesIt()
        {
            var source = Chest(5, 2, Item(0, 0, Stone, 50), Item(1, 0, Wood, 10), Item(2, 0, Stone, 5));
            var chest = Marked(Chest(2, 1, Item(1, 0, Stone, 50)), Mark(0, 0, "Wood"));

            int moved = SortPlanner.Run(source, new[] { chest }, ByHash);

            Assert.Equal(10, moved);
            Assert.Equal(Bytes(Item(1, 0, Stone, 50), Item(0, 0, Wood, 10)), chest.Items.ToBytes());
            Assert.Equal(Bytes(Item(0, 0, Stone, 50), Item(2, 0, Stone, 5)), source.Items.ToBytes());
        }

        [Fact]
        public void StacksOnlyWithTheSameQualityAndWorldLevel()
        {
            var source = Chest(5, 2, Item(0, 0, Wood, 5, worldLevel: 1));
            var woods = Chest(2, 1, Item(0, 0, Wood, 10));

            SortPlanner.Run(source, new[] { woods }, ByHash);

            Assert.Equal(Bytes(Item(0, 0, Wood, 10), Item(1, 0, Wood, 5, worldLevel: 1)), woods.Items.ToBytes());
        }

        [Fact]
        public void GearMovesWholeToAFreeSlotNextToItsKind()
        {
            var sword = new GameFormat.Item
            {
                X = 4, Y = 1, PrefabHash = Sword, Quality = 3, Variant = 2, CrafterId = 42, CrafterName = "Björn",
                Durability = 61.5f, Custom = new Dictionary<string, string> { ["k"] = "v" },
            };
            var source = Chest(5, 2, sword, Item(0, 0, Carrot, 3));
            var armory = Chest(2, 1, Item(0, 0, Sword, quality: 1));

            int moved = SortPlanner.Run(source, new[] { armory }, ByHash);

            Assert.Equal(1, moved);
            sword.X = 1;
            sword.Y = 0;
            Assert.Equal(Bytes(Item(0, 0, Sword, quality: 1), sword), armory.Items.ToBytes());
            Assert.Equal(Bytes(Item(0, 0, Carrot, 3)), source.Items.ToBytes());
        }

        [Fact]
        public void UnknownPrefabIsLeftAlone()
        {
            var source = Chest(5, 2, Item(0, 0, 999, 5));
            var other = Chest(5, 2, Item(0, 0, 999, 5));

            Assert.Equal(0, SortPlanner.Run(source, new[] { other }, ByHash));
            Assert.False(SortPlanner.HasCargo(source, ByHash));
        }

        [Fact]
        public void GridTallerThanThePrefabIsUsed()
        {
            var source = Chest(5, 2, Item(0, 0, Wood, 50));
            var woods = Chest(1, 1, Item(0, 2, Wood, 50));

            SortPlanner.Run(source, new[] { woods }, ByHash);

            Assert.Equal(Bytes(Item(0, 2, Wood, 50), Item(0, 0, Wood, 50)), woods.Items.ToBytes());
        }

        [Fact]
        public void CheatedMarkSpreadsToTheStackItJoins()
        {
            var cheated = Item(0, 0, Wood, 5);
            cheated.Cheated = true;
            var source = Chest(5, 2, cheated);
            var woods = Chest(5, 2, Item(0, 0, Wood, 10));

            SortPlanner.Run(source, new[] { woods }, ByHash);

            var expected = Item(0, 0, Wood, 15);
            expected.Cheated = true;
            Assert.Equal(Bytes(expected), woods.Items.ToBytes());
        }
    }

    public class ChestItemRewriteTests
    {
        [Theory]
        [InlineData(1, 50)]
        [InlineData(50, 1)]
        [InlineData(7, 0)]
        public void RewriteIsWhatTheGameWouldSave(int from, int to)
        {
            var item = new GameFormat.Item
            {
                X = 1, Y = 1, PrefabHash = 77, Stack = from, Quality = 2, Variant = 5, CrafterId = 9, CrafterName = "ção",
                WorldLevel = 3, PickedUp = true, Custom = new Dictionary<string, string> { ["a"] = "b" }, Cheated = true,
            };
            Assert.True(ChestItems.TryParse(GameFormat.Chest(109, item), out var chest));

            chest.Items[0] = chest.Items[0].Rewrite(6, 3, to);

            item.X = 6;
            item.Y = 3;
            item.Stack = to;
            Assert.Equal(GameFormat.Chest(109, item), chest.ToBytes());
            Assert.True(ChestItems.TryParse(chest.ToBytes(), out var back));
            Assert.Equal((6, 3, to, 2), (back.Items[0].X, back.Items[0].Y, back.Items[0].Stack, back.Items[0].Quality));
        }
    }
}
