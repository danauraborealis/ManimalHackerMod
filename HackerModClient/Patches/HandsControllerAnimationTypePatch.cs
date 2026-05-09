using System.Reflection;
using EFT;
using HarmonyLib;
using Manimal.HackerMod.CustomEFTData;
using SPT.Reflection.Patching;

namespace Manimal.HackerMod.Patches
{
    // patches HandsControllerClass.method_49 — picks the EWeaponAnimationType for
    // the held item. vanilla switch only knows pistols, revolvers, knives etc and
    // falls through to a default for unknowns, leaving the player animator in a
    // bad state for our viewmodel.
    //
    // we force Pistol — same as KomradeKid for their gameboy. closest fit:
    // one-handed object held in front of the camera.
    internal sealed class HandsControllerAnimationTypePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            typeof(HandsControllerClass).GetMethod(
                "method_49",
                BindingFlags.Public | BindingFlags.Instance);

        [PatchPrefix]
        private static bool Prefix(
            ref PlayerAnimator.EWeaponAnimationType __result,
            HandsControllerClass __instance)
        {
            if (__instance.ItemInHands is HackerDeviceItem)
            {
                __result = PlayerAnimator.EWeaponAnimationType.Pistol;
                return false;
            }
            return true;
        }
    }
}
