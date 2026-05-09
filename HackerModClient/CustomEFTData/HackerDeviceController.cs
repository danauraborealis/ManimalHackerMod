using EFT;
using UnityEngine;

namespace Manimal.HackerMod.CustomEFTData
{
    // custom HandsController for the hacker device. animator binding lives in
    // vmethod_0 — engine calls it synchronously after attaching the controller
    // to its WeaponPrefab so theres no race with OnHandsLoaded or polling.
    //
    // bound by SetInHandsHackerDevicePatch + the dispatch patches: when the
    // held item is a HackerDeviceItem theyll route the swap through
    // Player.Proceed<HackerDeviceController> instead of the default UsableItemController.
    //
    // animator must ship a Hands layer with states Equip, Idle_Loop, Tap,
    // Outro Success, Outro Fail. Active bool gates Spawn -> Equip.
    public class HackerDeviceController : Player.UsableItemController
    {
        private const int HandsLayer = 1;

        // tap audio fires at frame 7 of the 18-frame Tap clip (30fps) so the click
        // lines up with the visible finger contact. animation events on the prefab
        // cant reach this controller (its added at runtime not authored on the prefab)
        // so we schedule via coroutine.
        private const float TapAudioDelaySeconds = 7f / 30f;
        private const string TapAudioClipName    = "Blastgang_finger_tap_oneshot_FP";

        public Animator PhoneAnimator { get; private set; }
        private AudioSource _tapAudioSource;

        // diag — if Awake fires but vmethod_0 doesnt, the engines smethod_4 setup
        // chain is throwing silently between AddComponent and vmethod_0
        private void Awake()
        {
            Plugin.LogSource?.LogInfo(
                $"[HackerMod] HackerDeviceController.Awake on '{gameObject.name}'");
        }

        public override void vmethod_0(Player player, WeaponPrefab weaponPrefab)
        {
            Plugin.LogSource?.LogInfo(
                $"[HackerMod] HackerDeviceController.vmethod_0 — weaponPrefab={(weaponPrefab == null ? "<null>" : weaponPrefab.gameObject.name)}");

            try
            {
                base.vmethod_0(player, weaponPrefab);

                if (weaponPrefab != null)
                {
                    PhoneAnimator = weaponPrefab.GetComponentInChildren<Animator>();
                    if (PhoneAnimator != null)
                    {
                        // phone GO comes from AssetPoolObjects pool. between hacks
                        // unity deactivates it but doesnt reset the animator, so on
                        // hack 2 the state machine wakes up wherever it stopped
                        // (typically Idle_Loop after the previous outro) and the
                        // Spawn -> Equip transition wont fire. force-rewind to Spawn.
                        // also clear Tap/Success/Fail in case last hacks bools left
                        // a transition primed.
                        PhoneAnimator.SetBool("Tap",     false);
                        PhoneAnimator.SetBool("Success", false);
                        PhoneAnimator.SetBool("Fail",    false);
                        PhoneAnimator.Play("Spawn", HandsLayer, 0f);
                        PhoneAnimator.Update(0f);
                        PhoneAnimator.SetBool("Active", true);

                        Plugin.LogSource?.LogInfo(
                            $"[HackerMod] HackerDeviceController bound animator '{PhoneAnimator.gameObject.name}' (controller='{PhoneAnimator.runtimeAnimatorController?.name}')");
                    }
                    else
                    {
                        Plugin.LogSource?.LogWarning(
                            "[HackerMod] HackerDeviceController.vmethod_0: no Animator under WeaponPrefab.");
                    }

                    BindTapAudio(weaponPrefab);
                }
            }
            catch (System.Exception ex)
            {
                Plugin.LogSource?.LogError(
                    $"[HackerMod] HackerDeviceController.vmethod_0 threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // prefer an AudioSource on the prefab (clip slot populated). fall back to
        // looking up the AudioClip by name in loaded resources and attaching a
        // fresh AudioSource here on the controllers GameObject.
        private void BindTapAudio(WeaponPrefab weaponPrefab)
        {
            try
            {
                var src = weaponPrefab.GetComponentInChildren<AudioSource>(includeInactive: true);
                if (src != null && src.clip != null)
                {
                    src.playOnAwake = false;
                    _tapAudioSource = src;
                    Plugin.LogSource?.LogInfo(
                        $"[HackerMod] Tap audio bound to prefab AudioSource on '{src.gameObject.name}' (clip='{src.clip.name}')");
                    return;
                }

                AudioClip clip = null;
                foreach (var c in Resources.FindObjectsOfTypeAll<AudioClip>())
                {
                    if (c == null) continue;
                    if (string.Equals(c.name, TapAudioClipName, System.StringComparison.OrdinalIgnoreCase))
                    { clip = c; break; }
                }
                if (clip == null)
                {
                    Plugin.LogSource?.LogWarning(
                        $"[HackerMod] Tap audio: clip '{TapAudioClipName}' not found in loaded resources.");
                    return;
                }

                _tapAudioSource = gameObject.AddComponent<AudioSource>();
                _tapAudioSource.clip          = clip;
                _tapAudioSource.playOnAwake   = false;
                _tapAudioSource.spatialBlend  = 0f;   // 2D — phone is in the players hand
                _tapAudioSource.volume        = 0.8f;
                Plugin.LogSource?.LogInfo(
                    $"[HackerMod] Tap audio: attached runtime AudioSource with clip '{clip.name}'");
            }
            catch (System.Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[HackerMod] BindTapAudio: {ex.Message}");
            }
        }

        public void PlayTap(float crossFadeSeconds = 0f)
        {
            if (PhoneAnimator == null) return;
            try
            {
                if (crossFadeSeconds > 0f)
                    PhoneAnimator.CrossFade("Tap", crossFadeSeconds, HandsLayer, 0f);
                else
                    PhoneAnimator.Play("Tap", HandsLayer, 0f);

                if (_tapAudioSource != null && _tapAudioSource.clip != null)
                    StartCoroutine(PlayTapAudioAfter(TapAudioDelaySeconds));
            }
            catch (System.Exception ex)
            {
                Plugin.LogSource?.LogError($"[HackerMod] PlayTap: {ex.Message}");
            }
        }

        private System.Collections.IEnumerator PlayTapAudioAfter(float delay)
        {
            yield return new UnityEngine.WaitForSecondsRealtime(delay);
            if (_tapAudioSource == null || _tapAudioSource.clip == null) yield break;
            try { _tapAudioSource.PlayOneShot(_tapAudioSource.clip); }
            catch (System.Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[HackerMod] tap audio play: {ex.Message}");
            }
        }

        public void PlayOutroSuccess() => PlayOutro("Outro Success");
        public void PlayOutroFail()    => PlayOutro("Outro Fail");

        private void PlayOutro(string stateName)
        {
            if (PhoneAnimator == null) return;
            try
            {
                PhoneAnimator.SetBool("Active", false);
                PhoneAnimator.Play(stateName, HandsLayer, 0f);
            }
            catch (System.Exception ex)
            {
                Plugin.LogSource?.LogError($"[HackerMod] PlayOutro({stateName}): {ex.Message}");
            }
        }
    }
}
