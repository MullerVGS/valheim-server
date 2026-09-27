using ValheimMetrics.Chests;

namespace ValheimMetrics.Tests
{
    internal static class F
    {
        public static readonly ItemKind Wood = new ItemKind("Wood", 1, 0);
        public static readonly ItemKind Stone = new ItemKind("Stone", 1, 0);
        public static readonly ItemKind Resin = new ItemKind("Resin", 1, 0);

        public static Slot S(int x, int y) => new Slot(x, y);
        public static SlotMark Mark(int x, int y, ItemKind kind) => new SlotMark(S(x, y), kind);
        public static SlotContent Stack(int x, int y, ItemKind kind, int stack) => new SlotContent(S(x, y), kind, stack);
        public static SlotContent Ghost(int x, int y, ItemKind kind) => new SlotContent(S(x, y), kind, 0);
    }
}
