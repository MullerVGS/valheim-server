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

        public static void Patch(Harmony harmony, Type type, string method, Type[] args, Type host,
            string prefix = null, string postfix = null)
        {
            var key = type.Name + "." + method;
            try
            {
                var original = AccessTools.Method(type, method, args)
                    ?? throw new MissingMethodException(type.FullName, method);
                harmony.Patch(original,
                    prefix: prefix == null ? null : new HarmonyMethod(AccessTools.Method(host, prefix)),
                    postfix: postfix == null ? null : new HarmonyMethod(AccessTools.Method(host, postfix)));
                Status[key] = true;
            }
            catch (Exception e)
            {
                Status[key] = false;
                Plugin.Log.LogWarning($"Patch {key} nao aplicou: {e.Message}");
            }
        }
    }
}
