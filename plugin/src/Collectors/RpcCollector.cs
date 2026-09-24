using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using ValheimMetrics.Exposition;

namespace ValheimMetrics.Collectors
{
    // RPC trafega so o hash do nome (GetStableHashCode). Os nomes saem das strings do proprio
    // assembly do jogo: primeiro as que alimentam Register/Invoke, depois o resto como reserva.
    // Patches no caminho quente: sem alocacao depois do primeiro hash visto.
    sealed class RpcCollector : ICollector
    {
        sealed class Counter
        {
            public long Count;
            public long Bytes;
        }

        const int PingHash = 0;

        static readonly Dictionary<int, Counter> Received = new Dictionary<int, Counter>();
        static readonly Dictionary<string, long> Sent = new Dictionary<string, long>();
        static readonly Dictionary<int, Counter> RoutedReceived = new Dictionary<int, Counter>();
        static readonly Dictionary<int, Counter> RoutedForwarded = new Dictionary<int, Counter>();
        static MethodNames _names;

        public string Name => "rpc";

        public void Install(Harmony harmony)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                _names = BuildNames();
                Plugin.Log.LogInfo($"RPC: {_names.Count} nomes resolviveis em {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception e)
            {
                _names = new MethodNames(Array.Empty<string>(), s => s.GetStableHashCode());
                Plugin.Log.LogWarning($"RPC: sem nomes, labels em hex: {e.Message}");
            }

            var self = typeof(RpcCollector);
            Patcher.Patch(harmony, typeof(ZRpc), "HandlePackage", new[] { typeof(ZPackage) }, self, nameof(HandlePackagePrefix));
            Patcher.Patch(harmony, typeof(ZRpc), "Invoke", new[] { typeof(string), typeof(object[]) }, self, nameof(InvokePrefix));
            Patcher.Patch(harmony, typeof(ZRoutedRpc.RoutedRPCData), "Deserialize", new[] { typeof(ZPackage) }, self, postfix: nameof(RoutedReceivedPostfix));
            Patcher.Patch(harmony, typeof(ZRoutedRpc), "RouteRPC", new[] { typeof(ZRoutedRpc.RoutedRPCData) }, self, nameof(RouteRpcPrefix));
        }

        static void HandlePackagePrefix(ZPackage __0)
        {
            int pos = 0;
            try
            {
                pos = __0.GetPos();
                Add(Received, __0.ReadInt(), __0.Size());
            }
            catch
            {
                Patcher.Errors++;
            }
            finally
            {
                __0.SetPos(pos);
            }
        }

        static void InvokePrefix(string __0)
        {
            try
            {
                Sent[__0] = (Sent.TryGetValue(__0, out var n) ? n : 0) + 1;
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        static void RoutedReceivedPostfix(ZRoutedRpc.RoutedRPCData __instance)
        {
            try
            {
                Add(RoutedReceived, __instance.m_methodHash, __instance.m_parameters?.Size() ?? 0);
                var sender = Players.ForUid(__instance.m_senderPeerID);
                if (sender != null)
                    sender.RoutedRpcSent++;
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        static void RouteRpcPrefix(ZRoutedRpc.RoutedRPCData __0)
        {
            try
            {
                Add(RoutedForwarded, __0.m_methodHash, __0.m_parameters?.Size() ?? 0);
            }
            catch
            {
                Patcher.Errors++;
            }
        }

        static void Add(Dictionary<int, Counter> counters, int hash, int bytes)
        {
            if (!counters.TryGetValue(hash, out var c))
                counters[hash] = c = new Counter();
            c.Count++;
            c.Bytes += bytes;
        }

        public void Write(PrometheusWriter w, double now)
        {
            WriteCounters(w, "valheim_rpc_received", "RPCs recebidos dos jogadores por metodo.", Received);

            w.Family("valheim_rpc_sent_total", "counter", "RPCs enviados pelo servidor por metodo.");
            foreach (var kv in Sent)
                w.Sample("valheim_rpc_sent_total", kv.Value, "method", kv.Key);

            WriteCounters(w, "valheim_routed_rpc_received", "RPCs roteados recebidos (acoes sobre objetos e jogadores).", RoutedReceived);
            WriteCounters(w, "valheim_routed_rpc_forwarded", "RPCs roteados que o servidor repassou a outros jogadores.", RoutedForwarded);

            w.Family("valheim_player_routed_rpc_total", "counter", "RPCs roteados enviados pelo jogador.");
            foreach (var p in Players.Connected)
                w.Sample("valheim_player_routed_rpc_total", p.RoutedRpcSent, p.Labels);
        }

        static void WriteCounters(PrometheusWriter w, string prefix, string help, Dictionary<int, Counter> counters)
        {
            w.Family(prefix + "_total", "counter", help);
            foreach (var kv in counters)
                w.Sample(prefix + "_total", kv.Value.Count, "method", NameOf(kv.Key));

            w.Family(prefix + "_bytes_total", "counter", help + " Bytes.");
            foreach (var kv in counters)
                w.Sample(prefix + "_bytes_total", kv.Value.Bytes, "method", NameOf(kv.Key));
        }

        internal static string NameOf(int hash) =>
            hash == PingHash ? "ping" : _names?.Resolve(hash) ?? hash.ToString("x8");

        static MethodNames BuildNames()
        {
            var registered = new List<string>();
            var other = new List<string>();
            using (var module = ModuleDefinition.ReadModule(typeof(ZNet).Assembly.Location))
            {
                foreach (var type in module.GetTypes())
                {
                    foreach (var method in type.Methods)
                    {
                        if (!method.HasBody)
                            continue;
                        var ins = method.Body.Instructions;
                        for (int i = 0; i < ins.Count; i++)
                        {
                            if (ins[i].OpCode.Code != Code.Ldstr)
                                continue;
                            var s = (string)ins[i].Operand;
                            (FeedsRpc(ins, i) ? registered : other).Add(s);
                        }
                    }
                }
            }
            return new MethodNames(registered.Concat(other), s => s.GetStableHashCode());
        }

        static bool FeedsRpc(Mono.Collections.Generic.Collection<Instruction> ins, int at)
        {
            for (int j = at + 1; j < ins.Count && j <= at + 8; j++)
            {
                var code = ins[j].OpCode.Code;
                if ((code == Code.Call || code == Code.Callvirt) && ins[j].Operand is MethodReference m)
                    return m.Name.StartsWith("Register", StringComparison.Ordinal)
                        || m.Name.StartsWith("Invoke", StringComparison.Ordinal);
            }
            return false;
        }
    }
}
