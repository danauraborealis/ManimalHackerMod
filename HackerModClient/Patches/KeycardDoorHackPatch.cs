using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.Interactive;
using HarmonyLib;
using Manimal.HackerMod.Minigame;
using SPT.Reflection.Patching;

namespace Manimal.HackerMod.Patches
{
    // adds a "Use {device}" entry to the keycard door action menu when the player
    // has a hacker device in inventory.
    //
    // we resolve the smethod by parameter shape because BSGs obfuscator renumbers
    // smethod_* across patches (4.0.x = smethod_13, older = smethod_9). looking it
    // up by signature (static, returns ActionsReturnClass, takes a KeycardDoor) is
    // stable across renumbers. arguments bound positionally (__0, __1) so the
    // patch survives a parameter rename too.
    internal sealed class KeycardDoorHackPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            var target = typeof(GetActionsClass)
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name.StartsWith("smethod_") &&
                    m.ReturnType == typeof(ActionsReturnClass) &&
                    m.GetParameters().Any(p => p.ParameterType == typeof(KeycardDoor)));

            if (target == null)
                Plugin.LogSource?.LogError(
                    "[HackerMod] Could not locate GetActionsClass.smethod_* with a KeycardDoor parameter — patch will not apply.");
            else
                Plugin.LogSource?.LogInfo(
                    $"[HackerMod] KeycardDoor dispatcher resolved to GetActionsClass.{target.Name}");

            return target;
        }

        [PatchPostfix]
        private static void Postfix(
            ref ActionsReturnClass __result,
            GamePlayerOwner __0,
            KeycardDoor __1)
        {
            var owner = __0;
            var door  = __1;

            // dispatcher is [CanBeNull] on the unlocked-non-proxy branch
            if (__result == null || __result.Actions == null) return;

            // headless / non-MainPlayer paths — bail before Profile access
            var mainPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
            if (mainPlayer == null) return;
            if (owner?.Player == null) return;
            if (owner.Player.Side == EPlayerSide.Savage) return;

            // door must be Locked — anything else means theres nothing to hack
            if (door == null || door.DoorState != EDoorState.Locked) return;

            // hide entry on doors the player has burned through (3 strikes / red zone)
            if (HackerMinigameController.IsLockedOut(door.Id)) return;

            // one entry per device variant on body so the player can pick which one
            // to spend a use on. label is "Use {ShortName}" for at-a-glance disambig.
            var inventory = owner.Player.Profile?.Inventory;
            if (inventory == null) return;

            foreach (var tpl in HackerConstants.AllHackerDeviceTpls)
            {
                Item device = inventory.GetAllItemByTemplate(new MongoID(tpl)).FirstOrDefault();
                if (device == null) continue;

                Item captured = device; // close over the resolved item
                string label = $"Use {device.ShortName.Localized(null)}";
                __result.Actions.Add(new ActionsTypesClass
                {
                    Name = label,
                    Disabled = false,
                    Action = () => HackerMinigameController.BeginHack(door, owner, captured),
                });
            }
        }
    }
}
