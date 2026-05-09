using System.Reflection;
using EFT;
using HarmonyLib;
using Manimal.HackerMod.Minigame;
using SPT.Reflection.Patching;

namespace Manimal.HackerMod.Patches
{
    // suppress the in-raid pause menu while the minigame is open. without this,
    // the abort hotkey (default Escape) also pops EFTs pause overlay since the
    // same key drives both.
    //
    // prefix because the original opens the menu inside its body — postfix is
    // too late.
    //
    // the minigames own abort detection uses BepInEx KeyboardShortcut.IsDown
    // (polls unity input directly) so blocking EFTs command pipeline here
    // doesnt stop the abort itself from firing.
    internal sealed class SuppressPauseMenuPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            typeof(EftGamePlayerOwner).GetMethod(
                nameof(EftGamePlayerOwner.TranslateExitScreenInput),
                BindingFlags.Public | BindingFlags.Instance);

        [PatchPrefix]
        private static bool Prefix(ref bool __result)
        {
            if (!HackerMinigameController.IsActive) return true;

            __result = false; // no exit-screen happened
            return false;     // skip original
        }
    }
}
