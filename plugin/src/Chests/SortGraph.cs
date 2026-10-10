using System;
using System.Collections.Generic;

namespace ValheimMetrics.Chests
{
    // Para onde um bau pode mandar: tudo que os links alcancam, do mais perto ao mais longe.
    public static class SortGraph
    {
        // Fica de fora quem leva de volta ao proprio bau: entre baus ligados em circulo o item
        // andaria para sempre, porque cada um "ja tem" o item do outro. Link para bau que sumiu
        // (nodeOf devolve null) e ignorado.
        public static List<ulong> Destinations(SortNode source, Func<ulong, SortNode> nodeOf)
        {
            var order = new List<ulong>();
            var nodes = new Dictionary<ulong, SortNode> { [source.Id] = source };
            var queue = new Queue<SortNode>();
            queue.Enqueue(source);
            while (queue.Count > 0)
            {
                foreach (var link in queue.Dequeue().Links)
                {
                    if (nodes.ContainsKey(link.Target))
                        continue;
                    var next = nodeOf(link.Target);
                    if (next == null || next.Id != link.Target)
                        continue;
                    nodes[next.Id] = next;
                    order.Add(next.Id);
                    queue.Enqueue(next);
                }
            }

            var back = LeadingTo(source.Id, nodes);
            order.RemoveAll(back.Contains);
            return order;
        }

        static HashSet<ulong> LeadingTo(ulong target, Dictionary<ulong, SortNode> nodes)
        {
            var senders = new Dictionary<ulong, List<ulong>>();
            foreach (var node in nodes.Values)
            {
                foreach (var link in node.Links)
                {
                    if (!nodes.ContainsKey(link.Target))
                        continue;
                    if (!senders.TryGetValue(link.Target, out var list))
                        senders[link.Target] = list = new List<ulong>();
                    list.Add(node.Id);
                }
            }

            var found = new HashSet<ulong>();
            var queue = new Queue<ulong>();
            queue.Enqueue(target);
            while (queue.Count > 0)
            {
                if (!senders.TryGetValue(queue.Dequeue(), out var list))
                    continue;
                foreach (var id in list)
                    if (id != target && found.Add(id))
                        queue.Enqueue(id);
            }
            return found;
        }
    }
}
