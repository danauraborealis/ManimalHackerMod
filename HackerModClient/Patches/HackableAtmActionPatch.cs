using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Manimal.HackerMod.CustomEFTData;
using Manimal.HackerMod.Minigame;
using SPT.Reflection.Patching;

namespace Manimal.HackerMod.Patches
{
    // prefix on GetActionsClass.GetAvailableActions(owner, GInterface177):
    // if the targeted interactable is a HackableAtm, build "Use {device}"
    // actions and short-circuit the original. without this the original
    // hits the bottom of its long if-else chain and throws
    // "No interactions defined for HackableAtm".
    internal sealed class HackableAtmActionPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            // resolve by parameter shape — GInterface177 is obfuscated and changes
            // index between BSG builds.
            return typeof(GetActionsClass)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "GetAvailableActions" &&
                    m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType == typeof(GamePlayerOwner));
        }

        [PatchPrefix]
        private static bool Prefix(
            GamePlayerOwner owner,
            object interactive,            // GInterface177, bound positionally
            ref ActionsReturnClass __result)
        {
            var atm = interactive as HackableAtm;
            if (atm == null) return true; // not ours, let original run

            try
            {
                __result = BuildActions(owner, atm);
            }
            catch (System.Exception ex)
            {
                Plugin.LogSource?.LogError($"[HackerMod] HackableAtmActionPatch: {ex.Message}");
                __result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };
            }
            return false;
        }

        private static ActionsReturnClass BuildActions(GamePlayerOwner owner, HackableAtm atm)
        {
            var result = new ActionsReturnClass { Actions = new List<ActionsTypesClass>() };

            if (owner?.Player == null) return result;
            if (owner.Player.Side == EPlayerSide.Savage) return result;

            // hide entry once the ATM has been hacked / burned through (same
            // lockout convention as keycard doors)
            if (HackerMinigameController.IsLockedOut(atm.AtmId))
            {
                return result;
            }

            var inventory = owner.Player.Profile?.Inventory;
            if (inventory == null) return result;

            // one entry per device variant on body. matches KeycardDoorHackPatch.
            foreach (var tpl in HackerConstants.AllHackerDeviceTpls)
            {
                Item device = inventory.GetAllItemByTemplate(new MongoID(tpl)).FirstOrDefault();
                if (device == null) continue;

                Item captured = device;
                string label = $"Use {device.ShortName.Localized(null)}";
                result.Actions.Add(new ActionsTypesClass
                {
                    Name = label,
                    Disabled = false,
                    Action = () => HackerMinigameController.BeginHack(atm, owner, captured),
                });
            }

            return result;
        }
    }
}
