using System;
using System.Collections.Generic;

namespace ValheimMetrics.Exposition
{
    // O nome do RPC nao trafega, so o hash. Resolve a partir de candidatos em ordem de prioridade.
    public sealed class MethodNames
    {
        readonly Dictionary<int, string> _byHash = new Dictionary<int, string>();
        readonly Dictionary<int, string> _unknown = new Dictionary<int, string>();

        public MethodNames(IEnumerable<string> candidates, Func<string, int> hash)
        {
            foreach (var name in candidates)
            {
                var h = hash(name);
                if (!_byHash.ContainsKey(h))
                    _byHash[h] = name;
            }
        }

        public int Count => _byHash.Count;

        public string Resolve(int hash)
        {
            if (_byHash.TryGetValue(hash, out var name))
                return name;
            if (!_unknown.TryGetValue(hash, out name))
            {
                name = "0x" + hash.ToString("x8");
                _unknown[hash] = name;
            }
            return name;
        }
    }
}
