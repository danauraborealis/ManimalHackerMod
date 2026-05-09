using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace Manimal.HackerMod.Patches
{
    // diag — hooks PoolManagerClass.CreateItemUsablePrefab and logs the UsePrefab
    // key + resolved GameObject. if our key resolves to "Doge" the bundle failed
    // to load and the engine returned the missing-asset placeholder.
    internal sealed class CreatePrefabLoggerPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            var type = AccessTools.TypeByName("PoolManagerClass");
            return AccessTools.Method(type, "CreateItemUsablePrefab");
        }

        [PatchPostfix]
        private static void Postfix(EFT.InventoryLogic.Item item, UnityEngine.GameObject __result)
        {
            try
            {
                var usePrefabProp = item?.GetType().GetProperty("UsePrefab", BindingFlags.Public | BindingFlags.Instance);
                var usePrefab = usePrefabProp?.GetValue(item);
                if (usePrefab == null) return;

                var pathField = usePrefab.GetType().GetField("path") ?? usePrefab.GetType().GetField("Path");
                var rcidField = usePrefab.GetType().GetField("rcid") ?? usePrefab.GetType().GetField("Rcid");
                var path = pathField?.GetValue(usePrefab) as string;
                var rcid = rcidField?.GetValue(usePrefab) as string;

                if (path != null && path.IndexOf("hacker", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Plugin.LogSource?.LogInfo(
                        $"[HackerMod-Diag] CreateItemUsablePrefab tpl={item?.TemplateId} path='{path}' rcid='{rcid}' => '{(__result == null ? "<null>" : __result.name)}'");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[HackerMod-Diag] logger threw: {ex.Message}");
            }
        }
    }

    // diag — wraps WeaponPrefab.OnCreatePoolRoleModel and logs the real exception
    // before tarkovs obfuscated stack-trace formatter mangles it. remove once
    // weve identified the cause.
    internal sealed class ExceptionLoggerPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            var type = AccessTools.TypeByName("WeaponPrefab");
            return AccessTools.Method(type, "OnCreatePoolRoleModel");
        }

        [PatchPrefix]
        private static void Prefix(object __instance)
        {
            try
            {
                var go = (__instance as UnityEngine.MonoBehaviour)?.gameObject;
                Plugin.LogSource?.LogInfo(
                    $"[HackerMod-Diag] OnCreatePoolRoleModel called on: {(go == null ? "<unknown>" : go.name)}");
            }
            catch { }
        }

        [PatchFinalizer]
        private static Exception Finalizer(Exception __exception, object __instance)
        {
            if (__exception != null)
            {
                try
                {
                    var go = (__instance as UnityEngine.MonoBehaviour)?.gameObject;
                    Plugin.LogSource?.LogError(
                        $"[HackerMod-Diag] EXCEPTION in OnCreatePoolRoleModel on '{(go == null ? "<unknown>" : go.name)}':");
                    Plugin.LogSource?.LogError(
                        $"  Type: {__exception.GetType().FullName}");
                    Plugin.LogSource?.LogError(
                        $"  Message: {__exception.Message}");
                    Plugin.LogSource?.LogError(
                        $"  StackTrace: {__exception.StackTrace}");

                    var inner = __exception.InnerException;
                    while (inner != null)
                    {
                        Plugin.LogSource?.LogError(
                            $"  Inner: {inner.GetType().FullName}: {inner.Message}");
                        Plugin.LogSource?.LogError(
                            $"  Inner stack: {inner.StackTrace}");
                        inner = inner.InnerException;
                    }
                }
                catch { /* dont let logging break anything */ }
            }
            return __exception;
        }
    }
}
