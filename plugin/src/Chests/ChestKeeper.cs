using System;
using System.Collections.Generic;

namespace ValheimMetrics.Chests
{
    // O que o servidor sabe de um prefab de item (vem do ZNetScene, fora daqui).
    public readonly struct ItemInfo
    {
        public readonly string Name;
        public readonly int Hash;
        public readonly int MaxStack;
        public readonly bool HasTiers;

        public ItemInfo(string name, int hash, int maxStack, bool hasTiers)
        {
            Name = name;
            Hash = hash;
            MaxStack = maxStack;
            HasTiers = hasTiers;
        }

        public bool Stackable => MaxStack > 1;
    }

    public sealed class ChestOutcome
    {
        public bool ItemsChanged;
        public bool MarksChanged;
        public List<SlotMark> Marks;
        public int GhostsAdded;
        public int GhostsRemoved;
    }

    // Poe o bau de acordo com as marcas, com as mesmas regras que o mod aplica no bau aberto:
    // slot marcado vazio ganha fantasma, fantasma sem marca sai, outro item no slot marcado derruba a marca.
    public static class ChestKeeper
    {
        public static ChestOutcome Apply(ChestItems chest, IReadOnlyList<SlotMark> marks, int width, int prefabHeight,
            Func<int, ItemInfo?> byHash, Func<string, ItemInfo?> byName)
        {
            int height = chest.Height(prefabHeight);

            var contents = new List<SlotContent>(chest.Items.Count);
            foreach (var item in chest.Items)
                contents.Add(new SlotContent(new Slot(item.X, item.Y), KindOf(item, byHash), item.Stack));

            var plan = Reconciler.Plan(marks, contents, width, height);
            var outcome = new ChestOutcome { Marks = new List<SlotMark>(marks) };
            foreach (var mark in plan.DropMarks)
                outcome.Marks.Remove(mark);

            foreach (var slot in plan.RemoveGhosts)
            {
                var ghost = chest.At(slot.X, slot.Y);
                if (ghost != null && ghost.Stack <= 0 && chest.Items.Remove(ghost))
                    outcome.GhostsRemoved++;
            }
            foreach (var mark in plan.AddGhosts)
            {
                var info = byName(mark.Kind.Prefab);
                // Item que sumiu do jogo ou nao empilha: fantasma dele seria copia de graca.
                if (info == null || !info.Value.Stackable)
                {
                    outcome.Marks.Remove(mark);
                    continue;
                }
                if (chest.At(mark.Slot.X, mark.Slot.Y) != null)
                    continue;
                chest.Items.Add(chest.Ghost(info.Value.Hash, mark.Slot.X, mark.Slot.Y, mark.Kind.WorldLevel, mark.Kind.Quality));
                outcome.GhostsAdded++;
            }

            outcome.ItemsChanged = outcome.GhostsAdded > 0 || outcome.GhostsRemoved > 0;
            outcome.MarksChanged = outcome.Marks.Count != marks.Count;
            return outcome;
        }

        // Mesma chave que o mod calcula (ItemData.IsSameType): prefab, nivel do mundo e, se tem niveis, qualidade.
        static ItemKind KindOf(ChestItem item, Func<int, ItemInfo?> byHash)
        {
            var info = byHash(item.PrefabHash);
            if (info == null)
                return default;
            return new ItemKind(info.Value.Name, info.Value.HasTiers ? item.Quality : 1, item.WorldLevel);
        }
    }
}
