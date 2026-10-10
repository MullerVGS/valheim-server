using System.Collections.Generic;
using UnityEngine;

namespace ValheimMetrics.Chests
{
    // O que o servidor sabe de cada prefab de item, lido do ZNetScene uma vez por prefab.
    static class ItemCatalog
    {
        static readonly Dictionary<int, ItemInfo?> _byHash = new Dictionary<int, ItemInfo?>();
        static readonly Dictionary<string, ItemInfo?> _byName = new Dictionary<string, ItemInfo?>();

        public static ItemInfo? ByHash(int hash)
        {
            if (_byHash.TryGetValue(hash, out var cached))
                return cached;
            var info = Describe(ZNetScene.instance.GetPrefab(hash));
            _byHash[hash] = info;
            return info;
        }

        public static ItemInfo? ByName(string name)
        {
            if (_byName.TryGetValue(name, out var cached))
                return cached;
            var info = Describe(ZNetScene.instance.GetPrefab(name));
            _byName[name] = info;
            return info;
        }

        static ItemInfo? Describe(GameObject prefab)
        {
            var drop = prefab ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null)
                return null;
            var shared = drop.m_itemData.m_shared;
            return new ItemInfo(prefab.name, prefab.name.GetStableHashCode(), shared.m_maxStackSize, shared.m_maxQuality > 1);
        }
    }
}
