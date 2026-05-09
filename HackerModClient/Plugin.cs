using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Manimal.HackerMod.Patches;
using UnityEngine;

namespace Manimal.HackerMod
{
    // forge-compliant: GUID is reverse-domain lowercase, name is "Username-ModName".
    // version comes from ModVersion.g.cs (generated from $(ModVersion) in Directory.Build.props).
    [BepInPlugin(PluginGuid, PluginName, ModVersion.Value)]
    [BepInDependency("com.wtt.commonlib")]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.manimal.hackermod";
        public const string PluginName = "Manimal-HackerMod";

        public static ManualLogSource LogSource;
        public static ConfigEntry<KeyboardShortcut> AbortKey;
        public static ConfigEntry<float> HackTimeoutSeconds;

        private void Awake()
        {
            LogSource = Logger;

            AbortKey = Config.Bind(
                section: "Controls",
                key: "AbortHack",
                defaultValue: new KeyboardShortcut(KeyCode.Escape),
                description: "Hotkey that immediately cancels the active hacking minigame.");

            HackTimeoutSeconds = Config.Bind(
                section: "Gameplay",
                key: "HackTimeoutSeconds",
                defaultValue: 8f,
                description: "Seconds per minigame stage before the attempt auto-aborts (no strike applied).");

            // each patch is its own ModulePatch so a broken one wont block the others
            new KeycardDoorHackPatch().Enable();
            new SuppressPauseMenuPatch().Enable();
            new SetInHandsHackerDevicePatch().Enable();
            new ClientUsableItemControllerSmethod11Patch().Enable();
            new HandsControllerAnimationTypePatch().Enable();
            new UsableInterfaceDispatchPatch().Enable();
            new ExceptionLoggerPatch().Enable();
            new CreatePrefabLoggerPatch().Enable();
            new AtmDiscoveryPatch().Enable();
            new HackableAtmActionPatch().Enable();

            LogSource.LogInfo($"[{PluginName}] loaded v{ModVersion.Value}");
        }
    }
}
