using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ValheimMetrics
{
    // Patch que falha nao derruba o coletor inteiro: fica registrado e vira valheim_exporter_patch_ok=0.
    static class Patcher
    {
        public static readonly SortedDictionary<string, bool> Status = new SortedDictionary<string, bool>();
        public static long Errors;

        public static bool Patch(Harmony harmony, Type type, string method, Type[] args, Type host,
            string prefix = null, string postfix = null, string transpiler = null, string tag = null)
        {
            // O mesmo metodo pode receber patch de mais de um lugar (coletor e ajuste): a chave distingue.
            var key = type.Name + "." + method + (transpiler == null ? "" : "#transpiler") + (tag == null ? "" : "#" + tag);
            try
            {
                var original = AccessTools.Method(type, method, args)
                    ?? throw new MissingMethodException(type.FullName, method);
                harmony.Patch(original,
                    prefix: prefix == null ? null : new HarmonyMethod(AccessTools.Method(host, prefix)),
                    postfix: postfix == null ? null : new HarmonyMethod(AccessTools.Method(host, postfix)),
                    transpiler: transpiler == null ? null : new HarmonyMethod(AccessTools.Method(host, transpiler)));
                Status[key] = true;
                return true;
            }
            catch (Exception e)
            {
                Status[key] = false;
                Plugin.Log.LogWarning($"Patch {key} nao aplicou: {e.Message}");
                return false;
            }
        }
    }
}
