using System;
using System.Collections.Generic;

namespace ValheimMetrics.Chests
{
    // Um bau como o remanejo o ve: os itens (copia, nunca o ZDO), a grade e os slots reservados.
    public sealed class SortChest
    {
        public ChestItems Items;
        public int Width;
        public int Height;
        public IReadOnlyList<SlotMark> Marks = Array.Empty<SlotMark>();
        public bool Changed;

        public bool MarkedFor(int x, int y, string prefab)
        {
            foreach (var mark in Marks)
                if (mark.Slot.X == x && mark.Slot.Y == y && mark.Kind.Prefab == prefab)
                    return true;
            return false;
        }
    }

    // Tira do bau de origem o que ja tem casa num dos destinos e poe la. Casa = bau que ja guarda
    // aquele item (pilha de zero conta) ou tem slot reservado para ele. O que nao tem casa fica.
    //
    // Destinos vem do mais perto ao mais longe; o item enche o primeiro e o que sobrar segue para o
    // proximo. Empilha como Inventory.AddItem: mesma pilha so com prefab, qualidade e nivel do mundo
    // iguais. Slot reservado nunca perde o item dele nem recebe outro.
    //
    // Link "sempre": o que sobrou sem casa (ou sem espaco na casa) vai para esses baus, tenham eles o
    // item ou nao. Casa vem primeiro; o "sempre" e o resto.
    public static class SortPlanner
    {
        public static bool HasCargo(SortChest source, Func<int, ItemInfo?> byHash)
        {
            foreach (var item in source.Items.Items)
                if (Movable(source, item, byHash) != null)
                    return true;
            return false;
        }

        // Devolve quantas unidades mudaram de bau. Mexe nas copias e marca Changed em quem mudou.
        public static int Run(SortChest source, IReadOnlyList<SortChest> destinations, IReadOnlyList<SortChest> always,
            Func<int, ItemInfo?> byHash)
        {
            var cargo = new List<ChestItem>(source.Items.Items);
            cargo.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));

            int moved = 0;
            foreach (var item in cargo)
            {
                var info = Movable(source, item, byHash);
                if (info == null)
                    continue;

                int left = item.Stack;
                foreach (var destination in destinations)
                    if (left > 0 && IsHome(destination, info.Value))
                        left -= Give(destination, item, left, info.Value);
                foreach (var destination in always)
                    if (left > 0)
                        left -= Give(destination, item, left, info.Value);
                moved += item.Stack - left;

                if (left == item.Stack)
                    continue;
                int at = source.Items.Items.IndexOf(item);
                if (left == 0)
                    source.Items.Items.RemoveAt(at);
                else
                    source.Items.Items[at] = item.Rewrite(item.X, item.Y, left);
                source.Changed = true;
            }
            return moved;
        }

        static int Give(SortChest chest, ChestItem item, int amount, ItemInfo info)
        {
            int taken = info.Stackable ? Stack(chest, item, amount, info) : Place(chest, item, info) ? amount : 0;
            if (taken > 0)
                chest.Changed = true;
            return taken;
        }

        static ItemInfo? Movable(SortChest source, ChestItem item, Func<int, ItemInfo?> byHash)
        {
            if (item.Stack <= 0 || item.PrefabHash == 0)
                return null;
            var info = byHash(item.PrefabHash);
            if (info == null || source.MarkedFor(item.X, item.Y, info.Value.Name))
                return null;
            return info;
        }

        static bool IsHome(SortChest chest, ItemInfo info)
        {
            foreach (var item in chest.Items.Items)
                if (item.PrefabHash == info.Hash)
                    return true;
            foreach (var mark in chest.Marks)
                if (mark.Kind.Prefab == info.Name)
                    return true;
            return false;
        }

        static int Stack(SortChest chest, ChestItem item, int amount, ItemInfo info)
        {
            int left = amount;
            var items = chest.Items.Items;
            for (int i = 0; i < items.Count && left > 0; i++)
            {
                var pile = items[i];
                if (pile.PrefabHash != item.PrefabHash || pile.Quality != item.Quality || pile.WorldLevel != item.WorldLevel
                    || pile.Stack >= info.MaxStack)
                    continue;
                int add = Math.Min(info.MaxStack - pile.Stack, left);
                var grown = pile.Rewrite(pile.X, pile.Y, pile.Stack + add);
                if (item.Cheated)
                    grown.MarkCheated();
                items[i] = grown;
                left -= add;
            }
            while (left > 0 && FreeSlot(chest, info, out int x, out int y))
            {
                int add = Math.Min(info.MaxStack, left);
                items.Add(item.Rewrite(x, y, add));
                left -= add;
            }
            return amount - left;
        }

        static bool Place(SortChest chest, ChestItem item, ItemInfo info)
        {
            if (!FreeSlot(chest, info, out int x, out int y))
                return false;
            chest.Items.Items.Add(item.Rewrite(x, y, item.Stack));
            return true;
        }

        // Slot vazio reservado para este item primeiro; depois o primeiro vazio sem reserva, de cima para baixo.
        static bool FreeSlot(SortChest chest, ItemInfo info, out int x, out int y)
        {
            foreach (var mark in chest.Marks)
            {
                if (mark.Kind.Prefab != info.Name || mark.Slot.X >= chest.Width || mark.Slot.Y >= chest.Height
                    || chest.Items.At(mark.Slot.X, mark.Slot.Y) != null)
                    continue;
                x = mark.Slot.X;
                y = mark.Slot.Y;
                return true;
            }
            for (y = 0; y < chest.Height; y++)
                for (x = 0; x < chest.Width; x++)
                    if (chest.Items.At(x, y) == null && !Reserved(chest, x, y))
                        return true;
            x = y = -1;
            return false;
        }

        static bool Reserved(SortChest chest, int x, int y)
        {
            foreach (var mark in chest.Marks)
                if (mark.Slot.X == x && mark.Slot.Y == y)
                    return true;
            return false;
        }
    }
}
