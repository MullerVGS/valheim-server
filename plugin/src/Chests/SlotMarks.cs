// Contrato compartilhado com o mod de cliente que marca slots (Ghost Stacks): mesmos tipos, texto e regras.
using System;

namespace ValheimMetrics.Chests
{
    /// <summary>A cell of a container grid.</summary>
    public readonly struct Slot : IEquatable<Slot>
    {
        public readonly int X;
        public readonly int Y;

        public Slot(int x, int y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(Slot other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is Slot other && Equals(other);
        public override int GetHashCode() => (X * 397) ^ Y;
        public static bool operator ==(Slot a, Slot b) => a.Equals(b);
        public static bool operator !=(Slot a, Slot b) => !a.Equals(b);
        public override string ToString() => $"({X},{Y})";
    }

    /// <summary>
    /// The fields the game compares before letting two items share a stack (<c>ItemData.IsSameType</c>):
    /// prefab, world level and, for items that have tiers, quality. The game layer stores quality 1 for
    /// items without tiers so that equality here matches the game's rule.
    /// </summary>
    public readonly struct ItemKind : IEquatable<ItemKind>
    {
        public readonly string Prefab;
        public readonly int Quality;
        public readonly int WorldLevel;

        public ItemKind(string prefab, int quality, int worldLevel)
        {
            Prefab = prefab;
            Quality = quality;
            WorldLevel = worldLevel;
        }

        public bool IsKnown => !string.IsNullOrEmpty(Prefab);

        public bool Equals(ItemKind other) =>
            string.Equals(Prefab, other.Prefab, StringComparison.Ordinal) && Quality == other.Quality && WorldLevel == other.WorldLevel;

        public override bool Equals(object obj) => obj is ItemKind other && Equals(other);
        public override int GetHashCode() => ((Prefab?.GetHashCode() ?? 0) * 397) ^ (Quality * 31) ^ WorldLevel;
        public static bool operator ==(ItemKind a, ItemKind b) => a.Equals(b);
        public static bool operator !=(ItemKind a, ItemKind b) => !a.Equals(b);
        public override string ToString() => $"{Prefab} q{Quality} wl{WorldLevel}";
    }

    /// <summary>A slot reserved for one kind of item. It outlives the stack: when the stack leaves, a ghost keeps the slot.</summary>
    public readonly struct SlotMark : IEquatable<SlotMark>
    {
        public readonly Slot Slot;
        public readonly ItemKind Kind;

        public SlotMark(Slot slot, ItemKind kind)
        {
            Slot = slot;
            Kind = kind;
        }

        public SlotMark At(Slot slot) => new SlotMark(slot, Kind);

        public bool Equals(SlotMark other) => Slot == other.Slot && Kind == other.Kind;
        public override bool Equals(object obj) => obj is SlotMark other && Equals(other);
        public override int GetHashCode() => (Slot.GetHashCode() * 397) ^ Kind.GetHashCode();
        public override string ToString() => $"{Kind} @ {Slot}";
    }

    /// <summary>What occupies a slot right now. <see cref="Stack"/> 0 is a ghost.</summary>
    public readonly struct SlotContent
    {
        public readonly Slot Slot;
        public readonly ItemKind Kind;
        public readonly int Stack;

        public SlotContent(Slot slot, ItemKind kind, int stack)
        {
            Slot = slot;
            Kind = kind;
            Stack = stack;
        }

        public bool IsGhost => Stack <= 0;
    }
}
