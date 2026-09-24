using System.Collections.Generic;

namespace ValheimMetrics
{
    // Classifica ZDO pelo prefab. O conjunto de criaturas sai do ZNetScene, que so existe depois do mundo carregar.
    static class Prefabs
    {
        public static readonly int Player = "Player".GetStableHashCode();
        static HashSet<int> _creatures;

        public static HashSet<int> Creatures
        {
            get
            {
                if (_creatures != null || ZNetScene.instance == null)
                    return _creatures;
                var set = new HashSet<int>();
                foreach (var prefab in ZNetScene.instance.m_prefabs)
                {
                    if (prefab == null || prefab.GetComponent<global::Player>() != null)
                        continue;
                    if (prefab.GetComponent<Character>() != null)
                        set.Add(prefab.name.GetStableHashCode());
                }
                if (set.Count > 0)
                    _creatures = set;
                return _creatures;
            }
        }
    }
}
