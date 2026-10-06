using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValheimMetrics.ChunkLoader
{
    // Reuse native loading and geometry. Only server simulation uses the union; peer sync lists
    // and player area checks keep their own centers.
    static class LoadedAreas
    {
        delegate bool CreateLocal(ZoneSystem zones, Vector3 position);
        static CreateLocal _createLocal;
        static bool _creating;
        static readonly List<ZDO> _near = new List<ZDO>();
        static readonly List<ZDO> _far = new List<ZDO>();
        static readonly HashSet<ZDO> _seenNear = new HashSet<ZDO>();
        static readonly HashSet<ZDO> _seenFar = new HashSet<ZDO>();

        public static bool Install(Harmony harmony)
        {
            var host = typeof(LoadedAreas);
            var create = AccessTools.Method(typeof(ZoneSystem), "CreateLocalZones", new[] { typeof(Vector3) });
            if (create == null)
                throw new MissingMethodException(nameof(ZoneSystem), "CreateLocalZones");
            _createLocal = AccessTools.MethodDelegate<CreateLocal>(create);
            bool ok = Patcher.Patch(harmony, typeof(ZoneSystem), "CreateLocalZones", new[] { typeof(Vector3) }, host,
                postfix: nameof(CreateLocalZonesPostfix), tag: "chunk_loader");
            ok &= Patcher.Patch(harmony, typeof(ZNetScene), "CreateDestroyObjects", Type.EmptyTypes, host,
                transpiler: nameof(SceneObjectsTranspiler), tag: "chunk_loader");
            ok &= Patcher.Patch(harmony, typeof(ZNetScene), "OutsideActiveArea", new[] { typeof(Vector3) }, host,
                prefix: nameof(OutsideActiveAreaPrefix), tag: "chunk_loader");
            ok &= Patcher.Patch(harmony, typeof(ZDOMan), "ReleaseNearbyZDOS", new[] { typeof(Vector3), typeof(long) }, host,
                transpiler: nameof(OwnershipTranspiler), tag: "chunk_loader");
            ok &= Patcher.Patch(harmony, typeof(ZDOMan), "IsInPeerActiveArea", new[] { typeof(Vector3), typeof(long) }, host,
                prefix: nameof(IsInPeerActiveAreaPrefix), tag: "chunk_loader");
            return ok;
        }

        static void CreateLocalZonesPostfix(ZoneSystem __instance, Vector3 __0, ref bool __result)
        {
            if (!ChunkLoader.Active || _creating)
                return;
            _creating = true;
            try
            {
                var primary = ZoneSystem.GetZone(__0);
                foreach (var center in ChunkLoader.Centers)
                    if (ZoneSystem.GetZone(center) != primary)
                        __result |= _createLocal(__instance, center);
            }
            finally
            {
                _creating = false;
            }
        }

        static bool Inside(Vector3 point)
        {
            foreach (var center in ChunkLoader.Centers)
                if (ZNetScene.InActiveArea(point, center))
                    return true;
            return false;
        }

        static bool OutsideActiveAreaPrefix(Vector3 __0, ref bool __result)
        {
            if (!ChunkLoader.Active)
                return true;
            __result = !Inside(__0);
            return false;
        }

        // Players take over when they arrive, even while the server keeps the object loaded.
        static bool IsInPeerActiveAreaPrefix(long __1, ref bool __result)
        {
            if (!ChunkLoader.Active || __1 != ZDOMan.GetSessionID())
                return true;
            __result = false;
            return false;
        }

        internal static bool OwnershipActiveArea(Vector3 point, Vector2s zone, long uid)
        {
            if (!ChunkLoader.Active || uid != ZDOMan.GetSessionID())
                return ZNetScene.InActiveArea(point, zone);
            if (!Inside(point))
                return false;
            foreach (var peer in ZNet.instance.GetPeers())
                if (peer.IsReady() && ZNetScene.InActiveArea(point, peer.GetRefPos()))
                    return false;
            return true;
        }

        internal static void FindOwnershipObjects(ZDOMan man, Vector2s zone, SimulationDistance distance,
            List<ZDO> near, List<ZDO> far, long uid)
        {
            if (ChunkLoader.Active && uid == ZDOMan.GetSessionID())
                FindLoadedObjects(man, zone, distance, near, far);
            else
                man.FindSectorObjects(zone, distance, near, far);
        }

        // Near any anchor takes precedence over another anchor's distant ring.
        // FindSectorObjects stays native; no global patch expands peer sync ranges.
        static void FindLoadedObjects(ZDOMan man, Vector2s zone, SimulationDistance distance,
            List<ZDO> near, List<ZDO> far)
        {
            if (!ChunkLoader.Active)
            {
                man.FindSectorObjects(zone, distance, near, far);
                return;
            }
            _seenNear.Clear();
            _seenFar.Clear();
            foreach (var center in ChunkLoader.Centers)
            {
                _near.Clear();
                _far.Clear();
                man.FindSectorObjects(ZoneSystem.GetZone(center), distance, _near, far == null ? null : _far);
                foreach (var zdo in _near)
                    if (_seenNear.Add(zdo))
                        near.Add(zdo);
                foreach (var zdo in _far)
                    if (_seenFar.Add(zdo))
                        far.Add(zdo);
            }
            far?.RemoveAll(zdo => _seenNear.Contains(zdo));
        }

        static readonly System.Reflection.MethodInfo Find = AccessTools.Method(typeof(ZDOMan), "FindSectorObjects");
        static readonly System.Reflection.MethodInfo Active = AccessTools.Method(typeof(ZNetScene), "InActiveArea",
            new[] { typeof(Vector3), typeof(Vector2s) });

        static IEnumerable<CodeInstruction> SceneObjectsTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            int count = 0;
            foreach (var code in instructions)
            {
                if (code.Calls(Find))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(LoadedAreas), nameof(FindLoadedObjects));
                    count++;
                }
                yield return code;
            }
            if (count != 1)
                throw new InvalidOperationException($"CreateDestroyObjects: expected 1 FindSectorObjects call, found {count}.");
        }

        static IEnumerable<CodeInstruction> OwnershipTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            int find = 0, active = 0;
            foreach (var code in instructions)
            {
                if (code.Calls(Find) || code.Calls(Active))
                {
                    var uid = new CodeInstruction(OpCodes.Ldarg_2);
                    uid.MoveLabelsFrom(code);
                    uid.MoveBlocksFrom(code);
                    yield return uid;
                    if (code.Calls(Find))
                    {
                        code.operand = AccessTools.Method(typeof(LoadedAreas), nameof(FindOwnershipObjects));
                        find++;
                    }
                    else
                    {
                        code.operand = AccessTools.Method(typeof(LoadedAreas), nameof(OwnershipActiveArea));
                        active++;
                    }
                    code.opcode = OpCodes.Call;
                }
                yield return code;
            }
            if (find != 1 || active != 2)
                throw new InvalidOperationException($"ReleaseNearbyZDOS: expected 1 FindSectorObjects and 2 InActiveArea calls, found {find}/{active}.");
        }
    }
}
