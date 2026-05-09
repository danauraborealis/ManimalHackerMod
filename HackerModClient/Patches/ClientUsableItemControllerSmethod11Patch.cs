using System;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using HarmonyLib;
using Manimal.HackerMod.CustomEFTData;
using SPT.Reflection.Patching;

namespace Manimal.HackerMod.Patches
{
    // patches ClientUsableItemController.smethod_11 — the static async factory
    // the engine uses to build the controller from an item id during a hand swap.
    //
    // vanilla only recognizes PortableRangeFinderItemClass. for HackerDeviceItem
    // the FindItem<PortableRangeFinderItemClass> cast returns null and the engine
    // builds a controller with a null item, breaking the swap chain.
    // we prefix-detect our type and call the same async smethod_7 ourselves.
    internal sealed class ClientUsableItemControllerSmethod11Patch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            typeof(ClientUsableItemController).GetMethod(
                "smethod_11",
                BindingFlags.Public | BindingFlags.Static);

        [PatchPrefix]
        private static bool Prefix(
            ref Task<ClientUsableItemController> __result,
            ClientPlayer player,
            string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return true;

            var item = player.InventoryController.FindItem<HackerDeviceItem>(itemId);
            if (item != null)
            {
                __result = Player.UsableItemController.smethod_7<ClientUsableItemController>(player, item);
                return false;
            }
            return true;
        }
    }
}
