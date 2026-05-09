using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Manimal.HackerMod.CustomEFTData;
using SPT.Reflection.Patching;

namespace Manimal.HackerMod.Patches
{
    // routes HackerDeviceItem through HackerDeviceController when something calls
    // Player.SetInHandsUsableItem on it. mirrors KomradeKids shape for custom usables.
    //
    // vanilla SetInHandsUsableItem dispatches by type to a handful of hard-coded
    // controllers (PortableRangeFinder, RadioTransmitter, etc) and silently no-ops
    // for unrecognised types. prefix-detecting our type and calling
    // Player.Proceed<HackerDeviceController> plugs us into the same dispatch path.
    internal sealed class SetInHandsHackerDevicePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            typeof(Player).GetMethod(
                nameof(Player.SetInHandsUsableItem),
                BindingFlags.Public | BindingFlags.Instance);

        [PatchPrefix]
        private static bool Prefix(Player __instance, Item item, Callback<GInterface202> callback)
        {
            if (item == null) return true;

            // diag — if this fires for our tpl but item is NOT HackerDeviceItem,
            // the custom-parent registration didnt re-classify the inventory item
            // and we need a fresh spawn to get the new class.
            if (item.TemplateId.ToString() == HackerConstants.HackerDeviceTpl)
            {
                Plugin.LogSource?.LogInfo(
                    $"[HackerMod] SetInHandsUsableItem with our tpl — item runtime type='{item.GetType().FullName}'");
            }

            if (item is HackerDeviceItem)
            {
                Plugin.LogSource?.LogInfo("[HackerMod] Routing to HackerDeviceController");
                __instance.Proceed<HackerDeviceController>(item, callback, true);
                return false;
            }
            return true;
        }
    }
}
