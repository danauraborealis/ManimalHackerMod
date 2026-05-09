using System.Reflection;
using EFT.InventoryLogic;
using HarmonyLib;
using Manimal.HackerMod.CustomEFTData;
using SPT.Reflection.Patching;

namespace Manimal.HackerMod.Patches
{
    // patches GClass2970.smethod_0 — dispatcher that returns the GInterface323
    // for a given item during the controller swap chain. vanilla handles radio
    // transmitter + rangefinder, returns null for everything else. null breaks
    // downstream code that expects a non-null instance, which is why our
    // vmethod_0 never fires without this patch.
    //
    // prefix-detect our type and hand back the stub.
    internal sealed class UsableInterfaceDispatchPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            typeof(GClass2970).GetMethod(
                "smethod_0",
                BindingFlags.Public | BindingFlags.Static);

        [PatchPrefix]
        private static bool Prefix(ref GInterface323 __result, Item item)
        {
            if (item is HackerDeviceItem)
            {
                __result = new HackerDeviceInterfaceClass();
                return false;
            }
            return true;
        }
    }
}
