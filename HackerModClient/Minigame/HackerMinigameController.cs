using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.Interactive;
using EFT.InventoryLogic;
using Manimal.HackerMod.CustomEFTData;
using UnityEngine;

namespace Manimal.HackerMod.Minigame
{
    // static façade for launching/ticking/closing the minigame. owns the
    // runner GO and the process-scoped strike/lockout state read by the
    // action-menu patches.
    //
    // strike/lockout state is process-scoped — resets when BepInEx exits.
    // persisting across SPT restarts would need a profile-side store.
    public static class HackerMinigameController
    {
        private static GameObject _host;
        private static HackerMinigameRunner _runner;

        private static readonly Dictionary<string, int> _strikes = new Dictionary<string, int>();
        private static readonly HashSet<string> _lockouts = new HashSet<string>();

        public const int MaxStrikes = 3;

        public static bool IsActive => _runner != null && _runner.isActiveAndEnabled;

        public static bool IsLockedOut(string targetId)
            => !string.IsNullOrEmpty(targetId) && _lockouts.Contains(targetId);

        public static int GetStrikes(string targetId)
            => targetId != null && _strikes.TryGetValue(targetId, out var n) ? n : 0;

        // bumps strike count; locks out at MaxStrikes
        public static int RegisterStrike(string targetId)
        {
            if (string.IsNullOrEmpty(targetId)) return 0;
            _strikes.TryGetValue(targetId, out var n);
            n++;
            _strikes[targetId] = n;
            if (n >= MaxStrikes) _lockouts.Add(targetId);
            return n;
        }

        public static void RegisterLockout(string targetId)
        {
            if (!string.IsNullOrEmpty(targetId)) _lockouts.Add(targetId);
        }

        public static void BeginHack(KeycardDoor door, GamePlayerOwner owner, Item device)
        {
            if (door == null) return;
            BeginHackInternal(new DoorHackTarget(door), owner, device);
        }

        public static void BeginHack(CustomEFTData.HackableAtm atm, GamePlayerOwner owner, Item device)
        {
            if (atm == null) return;
            BeginHackInternal(new AtmHackTarget(atm), owner, device);
        }

        private static void BeginHackInternal(IHackTarget target, GamePlayerOwner owner, Item device)
        {
            if (target == null || owner == null || device == null) return;
            if (IsActive) return;

            // defence-in-depth: action-menu patches hide the entry on locked-out
            // targets but a stale closure could still fire BeginHack
            if (IsLockedOut(target.Id)) return;

            if (_host == null)
            {
                _host = new GameObject("HackerMod.MinigameHost");
                UnityEngine.Object.DontDestroyOnLoad(_host);
            }

            if (_runner == null)
            {
                _runner = _host.AddComponent<HackerMinigameRunner>();
            }
            _runner.Begin(target, owner, device);
        }

        public static void Abort()
        {
            if (_runner != null) _runner.Cancel();
        }

        // wraps NotificationManagerClass with a null-guard for the loading-screen
        // path where the manager singleton may not exist yet
        internal static void Notify(string message, ENotificationIconType icon, Color color)
        {
            try
            {
                NotificationManagerClass.DisplayMessageNotification(
                    message, ENotificationDurationType.Default, icon, color);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[HackerMod] Notify failed: {ex.Message}");
            }
        }
    }

    public enum ZoneColor { Green, White, Red, Blue }

    internal struct Zone
    {
        public ZoneColor Color;
        public float Start;  // 0..1 along the bar (inclusive)
        public float End;    // 0..1 along the bar (exclusive)
    }

    // what the player is hacking. door unlocks on success, ATM spits cash.
    // runner only cares about id (strike/lockout tracking), success outcome, denied sound.
    internal interface IHackTarget
    {
        string Id { get; }
        string SuccessNotification { get; }
        string LockoutNotification { get; }
        void OnSuccess(bool blueWin);
        void PlayDeniedSound(int times);
        // called once when the hack ends in a final way (success or lockout-fail),
        // not for retryable outcomes (white strike, abort, timeout). lets the target
        // tear itself down — matters for ATMs, doors handle their own state.
        void OnHackEnded(bool success);
    }

    internal sealed class DoorHackTarget : IHackTarget
    {
        public readonly EFT.Interactive.KeycardDoor Door;
        public DoorHackTarget(EFT.Interactive.KeycardDoor door) { Door = door; }
        public string Id => Door?.Id;
        public string SuccessNotification => "Door unlocked.";
        public string LockoutNotification => "Hack failed — device permanently locked out.";
        public void OnSuccess(bool blueWin)
        {
            try { Door?.Unlock(); }
            catch (System.Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[HackerMod] Door.Unlock: {ex.Message}");
            }
        }
        public void PlayDeniedSound(int times)
        {
            HackerMinigameRunner.PlayDeniedBeepStatic(Door, times);
        }
        public void OnHackEnded(bool success) { /* door state handles itself */ }
    }

    internal sealed class AtmHackTarget : IHackTarget
    {
        public readonly CustomEFTData.HackableAtm Atm;
        private readonly System.Random _rng = new System.Random();
        public AtmHackTarget(CustomEFTData.HackableAtm atm) { Atm = atm; }
        public string Id => Atm?.AtmId;
        public string SuccessNotification => "Withdrawal complete.";
        public string LockoutNotification => "ATM tripped its alarm — device locked out.";
        public void OnSuccess(bool blueWin)
        {
            int stacks = _rng.Next(HackerConstants.AtmMinStackCount, HackerConstants.AtmMaxStackCount + 1);
            int per    = blueWin ? HackerConstants.AtmBlueStackSize : HackerConstants.AtmGreenStackSize;
            try { Atm?.SpawnRubleStacks(stacks, per); }
            catch (System.Exception ex)
            {
                Plugin.LogSource?.LogWarning($"[HackerMod] ATM payout: {ex.Message}");
            }
        }
        public void PlayDeniedSound(int times) { /* no denied-beep clip on ATMs */ }
        public void OnHackEnded(bool success)
        {
            // lock out regardless of outcome. success -> not repeatable (infinite money).
            // lockout-fail already RegisterLockout'd via CommitNeedle, this is defensive.
            if (Atm != null)
            {
                HackerMinigameController.RegisterLockout(Atm.AtmId);
                Atm.DisableInteraction();
            }
        }
    }

    // per-session runtime. ticks the needle, draws the bar, applies outcomes.
    // re-used across hacks via Begin/Cancel — single instance, no concurrent hacks.
    internal sealed class HackerMinigameRunner : MonoBehaviour
    {
        private IHackTarget _target;
        private GamePlayerOwner _owner;

        private const int   TotalStages           = 3;
        private const float NeedleTraverseSeconds = 0.7f;   // edge-to-edge sweep time
        private const float RerollTimePenalty     = 2f;
        private const float StrikeTimePenalty     = 3f;     // deducted from stage timer per non-lockout white
        private const int   MinZones              = 8;
        private const int   MaxZones              = 11;     // exclusive upper for Random.Next

        // (green, white, red) per stage. matches the bioshock 2 abundant->rare curve.
        // green shrinks, white stays wide, red dominates stage 3.
        private static readonly (float g, float w, float r)[] StageWeights =
        {
            (0.50f, 0.35f, 0.15f),
            (0.35f, 0.40f, 0.25f),
            (0.25f, 0.40f, 0.35f),
        };

        // (green, blue, white, red) for Refs device. blue eats some of greens
        // allocation. blue zones get halved width at generation so the path is
        // harder than green even when its nominally rare-ish.
        private static readonly (float g, float b, float w, float r)[] RefStageWeights =
        {
            (0.40f, 0.15f, 0.30f, 0.15f),
            (0.27f, 0.13f, 0.35f, 0.25f),
            (0.18f, 0.10f, 0.37f, 0.35f),
        };

        // cumulative blue hits needed for blue path crack. no use consumed.
        private const int BluePathHitsNeeded = 3;

        private int   _stage;
        private float _stageDeadline;
        private float _needlePos;
        private float _needleDir;
        private readonly List<Zone> _zones = new List<Zone>();
        private bool _running;
        private readonly System.Random _rng = new System.Random();

        // device this hack is consuming a use from. captured at Begin so a
        // mid-hack item swap cant dodge the consumption.
        private Item _deviceItem;

        // what the player held before the hack — restored via TrySetLastEquippedWeapon
        // after the outro
        private Item _prevHandsItem;

        // cosmetic phone-screen UI. pure flair, does not affect game logic.
        private PhoneScreenRenderer _screenUI;

        // true for Refs device — enables blue zones + 3-blue-hits-no-use-consumed path
        private bool _isRefDevice;
        private int  _blueHits;

        // while true, bar/needle UI is hidden and F/R are no-ops so the player
        // cant blow past the equip -> tap -> screen-transition cinematic
        private bool _introActive;

        // intro timing — frames at clip 30fps, converted to seconds
        private const float EquipSeconds       = 22f / 30f;
        private const float TapImpactSeconds   = 8f  / 30f;
        private const float TransitionSeconds  = 0.4f;

        public void Begin(IHackTarget target, GamePlayerOwner owner, Item device)
        {
            _target      = target;
            _owner       = owner;
            _stage       = 0;
            _running     = true;
            _introActive = true;
            _blueHits    = 0;
            _deviceItem  = device;
            _isRefDevice = device != null && HackerConstants.HasBluePath(device.TemplateId);
            enabled      = true;

            EquipPhone(owner.Player);

            // intro: equip -> auto-tap -> screen cross-fade -> minigame.
            // F/R/Esc gated by _introActive so the player cant skip it.
            StartCoroutine(IntroSequence());
        }

        // equip -> tap -> screen transition -> minigame. unscaled time so
        // Time.timeScale doesnt desync from the animator.
        private System.Collections.IEnumerator IntroSequence()
        {
            // wait for the controller to spawn + bind its animator. EquipPhone
            // is async (DropCurrentController -> callback chain) so PhoneAnimator
            // is null for a few frames after Begin returns.
            const int HandsLayer = 1;
            float waitStop = Time.unscaledTime + 2f;
            Animator animator = null;
            while (Time.unscaledTime < waitStop)
            {
                animator = GetController()?.PhoneAnimator;
                if (animator != null) break;
                yield return null;
            }
            if (!_running) yield break;

            // break at 80% of Equip (instead of waiting for the full
            // Equip -> Idle_Loop handoff) so the tap follows without a
            // visible settle beat
            if (animator != null)
            {
                float equipStop = Time.unscaledTime + EquipSeconds + 1f;
                while (Time.unscaledTime < equipStop)
                {
                    var info = animator.GetCurrentAnimatorStateInfo(HandsLayer);
                    if (info.IsName("Idle_Loop")) break;
                    if (info.IsName("Equip") && info.normalizedTime >= 0.80f) break;
                    yield return null;
                }
            }
            else
            {
                yield return new WaitForSecondsRealtime(EquipSeconds * 0.80f);
            }
            if (!_running) yield break;

            // crossfade into tap so it blends out of late-Equip or Idle_Loop
            // instead of hard-cutting
            GetController()?.PlayTap(crossFadeSeconds: 0.1f);

            // wait til tap impact so the hand "press" lines up with the screen wipe
            yield return new WaitForSecondsRealtime(TapImpactSeconds);
            if (!_running) yield break;

            _screenUI?.BeginIntroTransition(TransitionSeconds);

            yield return new WaitForSecondsRealtime(TransitionSeconds);
            if (!_running) yield break;

            _introActive = false;
            StartStage();
        }

        // routes the device through HackerDeviceController via the
        // DropCurrentController -> manual factory chain (see CreateAndSpawnHackerController).
        // snapshots the previously-held item so RestoreHands can re-equip on outro.
        private void EquipPhone(Player player)
        {
            if (player == null || _deviceItem == null)
            {
                Plugin.LogSource?.LogWarning(
                    $"[HackerMod] EquipPhone bail: player={(player == null ? "null" : "ok")} deviceItem={(_deviceItem == null ? "null" : "ok")}");
                return;
            }

            try { _prevHandsItem = player.HandsController?.Item; }
            catch { /* fall through to SetEmptyHands at the end */ }

            var hcItem = player.HandsController?.Item;
            Plugin.LogSource?.LogInfo(
                $"[HackerMod] EquipPhone — deviceItem id={_deviceItem.Id} tpl={_deviceItem.TemplateId} | currentHandsItem={(hcItem == null ? "<null>" : hcItem.Id + " tpl=" + hcItem.TemplateId)} | sameRef={ReferenceEquals(hcItem, _deviceItem)} | handsControllerType={(player.HandsController?.GetType().FullName ?? "<null>")}");

            try
            {
                try { player.StopBlindFire(); } catch { }
                try { player.RemoveLeftHandItem(); } catch { }

                // save current as LastEquippedWeapon so TrySetLastEquippedWeapon
                // (in RestoreHandsAfterOutro) knows what to bring back. vanilla
                // flows do this via HideWeapon -> TrySaveLastItemInHands; were
                // bypassing that path so call it ourselves.
                try { player.TrySaveLastItemInHands(); } catch { }

                // drop current controller (put-away anim only). post-drop callback
                // runs our manual factory. mirrors Player.Process.Execute:
                // Execute -> DropCurrentController -> callback -> DestroyController -> create.
                Plugin.LogSource?.LogInfo("[HackerMod] DropCurrentController, then create");
                player.DropCurrentController(
                    () => CreateAndSpawnHackerController(player),
                    fastDrop: false,
                    nextControllerItem: _deviceItem);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError(
                    $"[HackerMod] EquipPhone threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void CreateAndSpawnHackerController(Player player)
        {
            try
            {
                // matches Process.Execute's post-drop callback (Class1314.method_1):
                // DestroyController before creating the next one. DropCurrentController
                // only plays the put-away anim — without DestroyController the old
                // HandsController + prefab stay attached and SpawnController
                // just overwrites the reference, leaking the old prefab.
                try
                {
                    if (player.HandsController != null)
                    {
                        Plugin.LogSource?.LogInfo(
                            $"[HackerMod] Post-drop: destroying old controller '{player.HandsController.GetType().Name}'");
                        player.DestroyController();
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogError($"[HackerMod] DestroyController: {ex.Message}");
                }

                Plugin.LogSource?.LogInfo("[HackerMod] Post-drop: creating HackerDeviceController");
                var controller = Player.ItemHandsController.smethod_1<HackerDeviceController>(
                    player,
                    _deviceItem,
                    new Player.ItemHandsController.Delegate8(
                        Comfort.Common.Singleton<PoolManagerClass>.Instance.CreateItemUsablePrefab));
                if (controller == null)
                {
                    Plugin.LogSource?.LogError("[HackerMod] smethod_1 returned null");
                    return;
                }

                Player.UsableItemController.smethod_8<HackerDeviceController>(controller, player);
                player.SpawnController(controller, () =>
                {
                    Plugin.LogSource?.LogInfo("[HackerMod] SpawnController callback fired");
                });

                // pooled phone keeps last hacks mainTexture on its material instance —
                // restore the prefab default before the renderer takes over
                ResetPhoneScreen();
                StartPhoneScreenUI();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError(
                    $"[HackerMod] CreateAndSpawnHackerController threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // post-swap callback. by now vmethod_0 has bound the animator so theres
        // nothing to do beyond logging a load failure.
        private void OnControllerLoaded(Result<GInterface202> result)
        {
            if (!result.Succeed)
            {
                Plugin.LogSource?.LogWarning(
                    $"[HackerMod] HackerDeviceController load failed: {result.Error}");
                return;
            }

            var controller = GetController();
            Plugin.LogSource?.LogInfo(
                $"[HackerMod] OnControllerLoaded — HandsController type='{(_owner?.Player?.HandsController?.GetType().FullName ?? "<null>")}' is HackerDeviceController? {controller != null}");
        }

        private HackerDeviceController GetController()
        {
            try { return _owner?.Player?.HandsController as HackerDeviceController; }
            catch { return null; }
        }

        public void Cancel() => Finish(success: false, reason: "Aborted", silent: false);

        private void StartStage()
        {
            GenerateZones(_stage);
            _stageDeadline = Time.realtimeSinceStartup + Plugin.HackTimeoutSeconds.Value;
            _needlePos     = 0f;
            _needleDir     = +1f;
        }

        private void GenerateZones(int stage)
        {
            _zones.Clear();
            int idx = Mathf.Clamp(stage, 0, StageWeights.Length - 1);

            int n = _rng.Next(MinZones, MaxZones);
            var widths = new float[n];
            var colors = new ZoneColor[n];
            float total = 0f;

            for (int i = 0; i < n; i++)
            {
                colors[i] = _isRefDevice ? PickColorRef(RefStageWeights[idx])
                                         : PickColor(StageWeights[idx]);
                float bw = (float)(_rng.NextDouble() * 0.12 + 0.06); // 0.06..0.18
                // Blue zones are deliberately tighter than green — the
                // alternate path is supposed to take more skill to land.
                if (colors[i] == ZoneColor.Blue) bw *= 0.5f;
                widths[i] = bw;
                total += bw;
            }

            float cursor = 0f;
            for (int i = 0; i < n; i++)
            {
                float frac = widths[i] / total;
                _zones.Add(new Zone
                {
                    Color = colors[i],
                    Start = cursor,
                    End   = cursor + frac,
                });
                cursor += frac;
            }
        }

        private ZoneColor PickColor((float g, float w, float r) weights)
        {
            float roll = (float)_rng.NextDouble();
            if (roll < weights.g) return ZoneColor.Green;
            if (roll < weights.g + weights.w) return ZoneColor.White;
            return ZoneColor.Red;
        }

        private ZoneColor PickColorRef((float g, float b, float w, float r) weights)
        {
            float roll = (float)_rng.NextDouble();
            if (roll < weights.g) return ZoneColor.Green;
            if (roll < weights.g + weights.b) return ZoneColor.Blue;
            if (roll < weights.g + weights.b + weights.w) return ZoneColor.White;
            return ZoneColor.Red;
        }

        private void Update()
        {
            if (!_running) return;

            // During the equip+tap+transition cinematic, swallow input
            // so the player can't strike-out before the bar is even
            // visible. SuppressPauseMenuPatch keeps Esc from popping
            // the pause menu; we still ignore it here so the abort
            // hotkey doesn't fire mid-intro.
            if (_introActive) return;

            if (Plugin.AbortKey != null && Plugin.AbortKey.Value.IsDown())
            {
                Cancel();
                return;
            }

            // Timer expiry — soft abort, no strike. Player can re-engage
            // the door and try again.
            if (Time.realtimeSinceStartup >= _stageDeadline)
            {
                Finish(success: false, reason: "Timed out", silent: false);
                return;
            }

            // Needle bounces edge-to-edge at a constant rate, scaled by
            // unscaledDeltaTime so it stays consistent with the
            // realtimeSinceStartup deadline regardless of game time-scale.
            _needlePos += _needleDir * (Time.unscaledDeltaTime / NeedleTraverseSeconds);
            if (_needlePos >= 1f) { _needlePos = 1f; _needleDir = -1f; }
            else if (_needlePos <= 0f) { _needlePos = 0f; _needleDir = +1f; }

            if (Input.GetKeyDown(KeyCode.F))
            {
                GetController()?.PlayTap();
                CommitNeedle();
                return;
            }
            if (Input.GetKeyDown(KeyCode.R))
            {
                _stageDeadline -= RerollTimePenalty;
                GenerateZones(_stage);
            }
        }

        private void CommitNeedle()
        {
            // Locate the zone the needle is sitting on. _zones are sorted
            // and contiguous, so the first match wins.
            var color = ZoneColor.White;
            for (int i = 0; i < _zones.Count; i++)
            {
                if (_needlePos >= _zones[i].Start && _needlePos < _zones[i].End)
                {
                    color = _zones[i].Color;
                    break;
                }
            }

            // Cosmetic: flash the latest pending notification on the
            // phone screen with the color of the zone the player hit.
            // Pure visual flair — game logic continues below.
            _screenUI?.RegisterTap(color);

            switch (color)
            {
                case ZoneColor.Green:
                    _stage++;
                    if (_stage >= TotalStages)
                    {
                        ConsumeDeviceUse();
                        Finish(success: true, reason: "Cracked", silent: false);
                    }
                    else
                    {
                        StartStage();
                    }
                    break;

                case ZoneColor.Blue:
                    // Alternate path (Ref's device only — green path
                    // still works in parallel). Three blue hits crack
                    // the door and don't burn a use, so the player can
                    // keep the device for another door if they're
                    // skilled enough to land them.
                    _blueHits++;
                    if (_blueHits >= BluePathHitsNeeded)
                    {
                        Finish(success: true, reason: "Cracked (Blue Path)", silent: false);
                    }
                    else
                    {
                        HackerMinigameController.Notify(
                            $"Blue {_blueHits}/{BluePathHitsNeeded}",
                            ENotificationIconType.Default,
                            new Color(0.30f, 0.55f, 0.95f));
                        // Roll a fresh bar but stay on the same stage —
                        // the green path's stage progression is
                        // untouched, so the player can still finish via
                        // green if they prefer. The stage timer keeps
                        // ticking, no penalty applied.
                        GenerateZones(_stage);
                    }
                    break;

                case ZoneColor.White:
                {
                    var n = HackerMinigameController.RegisterStrike(_target?.Id);
                    if (n >= HackerMinigameController.MaxStrikes)
                    {
                        // Terminal: third strike — full lockout, use
                        // consumed, hack ends with the fail outro.
                        ConsumeDeviceUse();
                        _target?.PlayDeniedSound(2);
                        Finish(success: false,
                            reason: "Lockout (3 strikes)",
                            silent: false,
                            lockoutMessage: _target?.LockoutNotification ?? "Hack failed — device permanently locked out.");
                    }
                    else
                    {
                        _target?.PlayDeniedSound(1);
                        HackerMinigameController.Notify(
                            $"Strike {n}/{HackerMinigameController.MaxStrikes} — try again.",
                            ENotificationIconType.Alert,
                            new Color(1f, 0.85f, 0.2f));
                        _stageDeadline -= StrikeTimePenalty;
                        GenerateZones(_stage);
                    }
                    break;
                }

                case ZoneColor.Red:
                    HackerMinigameController.RegisterLockout(_target?.Id);
                    ConsumeDeviceUse();
                    _target?.PlayDeniedSound(2);
                    Finish(success: false,
                        reason: "Tripped (red)",
                        silent: false,
                        lockoutMessage: _target?.LockoutNotification ?? "Hack tripped — device permanently locked out.");
                    break;
            }
        }

        // Lazily-built 1×1 white texture used as the source for every
        // colored fill. GUI.Box uses the skin's nine-slice background
        // sprite which is already dark+translucent — multiplying that
        // by GUI.color gives "slightly tinted black" instead of the
        // intended green/red/etc. Drawing a flat white texture and
        // tinting via GUI.color produces real flat colors.
        private static Texture2D _whitePixel;
        private static Texture2D WhitePixel
        {
            get
            {
                if (_whitePixel == null)
                {
                    _whitePixel = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                    {
                        hideFlags = HideFlags.HideAndDontSave,
                        filterMode = FilterMode.Point,
                    };
                    _whitePixel.SetPixel(0, 0, Color.white);
                    _whitePixel.Apply();
                }
                return _whitePixel;
            }
        }

        private static void DrawRect(Rect rect, Color color)
        {
            var prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, WhitePixel);
            GUI.color = prev;
        }

        private void OnGUI()
        {
            if (!_running) return;
            // Hide the bar/needle HUD during the intro cinematic so the
            // player's focus stays on the phone screen transition.
            if (_introActive) return;

            // Mid-screen panel — sized large enough to be the player's
            // focal point, centered on the X axis and pulled up from
            // the bottom so it sits roughly 60% down the screen
            // (slightly below center so it doesn't cover the door).
            const int barH    = 48;
            const int panelH  = 170;
            int panelW        = Mathf.Min(900, Screen.width - 40);
            var panel = new Rect(
                (Screen.width - panelW) / 2f,
                Screen.height * 0.60f - panelH / 2f,
                panelW,
                panelH);

            // Panel: dark translucent background + thin border drawn as
            // four 1px rects so the chrome isn't dependent on the IMGUI
            // skin (which BepInEx replaces with its own opaque grey).
            DrawRect(panel, new Color(0f, 0f, 0f, 0.75f));
            var borderColor = new Color(0.6f, 0.6f, 0.6f, 0.9f);
            DrawRect(new Rect(panel.x, panel.y, panel.width, 1), borderColor);
            DrawRect(new Rect(panel.x, panel.yMax - 1, panel.width, 1), borderColor);
            DrawRect(new Rect(panel.x, panel.y, 1, panel.height), borderColor);
            DrawRect(new Rect(panel.xMax - 1, panel.y, 1, panel.height), borderColor);

            float timeLeft = Mathf.Max(0f, _stageDeadline - Time.realtimeSinceStartup);
            int   strikes  = HackerMinigameController.GetStrikes(_target?.Id);

            // Pull the live device uses count straight off the
            // KeyComponent so the HUD reflects ConsumeDeviceUse()
            // immediately, no caching.
            string deviceLabel = "Device: ?";
            if (_deviceItem != null && _deviceItem.TryGetItemComponent<KeyComponent>(out var key))
            {
                int max  = key.Template.MaximumNumberOfUsage;
                int left = (max > 0) ? Mathf.Max(0, max - key.NumberOfUsages) : -1;
                deviceLabel = (max > 0) ? $"Device: {left}/{max}" : "Device: ∞";
            }

            var headerStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize  = 16,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
            };
            var smallStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize  = 14,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) },
            };
            var header = new Rect(panel.x + 16, panel.y + 8, panel.width - 32, 22);
            string blueLabel = _isRefDevice
                ? $"    Blue: {_blueHits}/{BluePathHitsNeeded}"
                : "";
            GUI.Label(header,
                $"HACKING — Stage {_stage + 1}/{TotalStages}    Time: {timeLeft:0.0}s    " +
                $"Strikes: {strikes}/{HackerMinigameController.MaxStrikes}{blueLabel}    {deviceLabel}",
                headerStyle);

            // Bar: dark inset + colored zones drawn flat.
            var bar = new Rect(panel.x + 16, panel.y + 40, panel.width - 32, barH);
            DrawRect(bar, new Color(0.08f, 0.08f, 0.08f, 0.9f));
            for (int i = 0; i < _zones.Count; i++)
            {
                var z = _zones[i];
                var seg = new Rect(
                    bar.x + z.Start * bar.width,
                    bar.y + 2,
                    (z.End - z.Start) * bar.width,
                    bar.height - 4);
                DrawRect(seg, ZoneTint(z.Color));
            }

            // Needle: yellow vertical line, slightly overhanging top
            // and bottom so it stays visible against any zone color.
            var nx = bar.x + Mathf.Clamp01(_needlePos) * bar.width;
            DrawRect(new Rect(nx - 2f, bar.y - 5, 4, bar.height + 10), Color.yellow);

            var ctrl = new Rect(panel.x + 16, panel.y + 100, panel.width - 32, 22);
            GUI.Label(ctrl,
                $"[F] Commit    [R] Reroll (-{RerollTimePenalty:0}s)    [{Plugin.AbortKey.Value}] Abort",
                smallStyle);
            var legend = new Rect(panel.x + 16, panel.y + 130, panel.width - 32, 22);
            string legendText = _isRefDevice
                ? "Green = advance    Blue = 3 to crack (no use)    White = strike (3 = lockout)    Red = instant lockout"
                : "Green = advance    White = strike (3 = lockout)    Red = instant lockout";
            GUI.Label(legend, legendText, smallStyle);
        }

        private static Color ZoneTint(ZoneColor c)
        {
            switch (c)
            {
                case ZoneColor.Green: return new Color(0.25f, 0.85f, 0.35f, 1f);
                case ZoneColor.Blue:  return new Color(0.30f, 0.55f, 0.95f, 1f);
                case ZoneColor.White: return new Color(0.90f, 0.90f, 0.90f, 1f);
                case ZoneColor.Red:   return new Color(0.90f, 0.25f, 0.25f, 1f);
                default:              return Color.gray;
            }
        }

        // bumps NumberOfUsages and discards the item at MaximumNumberOfUsage.
        // NumberOfUsages is [GAttribute25] -> serialised to upd, persists across raids.
        // device template is cloned from a key (5448ba0b…) not a flash drive so we
        // get a KeyComponent — info items dont carry one.
        //
        // only called for terminal outcomes (final-stage green, any white-3rd, any red).
        // aborts (Esc / timeout) dont consume a use — no hack attempt completed.
        private void ConsumeDeviceUse()
        {
            if (_deviceItem == null) return;
            if (!_deviceItem.TryGetItemComponent<KeyComponent>(out var key))
            {
                // stale instance cloned from the old flash-drive base — log but dont
                // hard-fail. the hack itself resolved.
                Plugin.LogSource?.LogWarning(
                    "[HackerMod] Device has no KeyComponent — likely a stale instance from before the key-clone update. Spawn a fresh one to enable use tracking.");
                return;
            }

            key.NumberOfUsages++;
            int max       = key.Template.MaximumNumberOfUsage;
            int remaining = (max > 0) ? Mathf.Max(0, max - key.NumberOfUsages) : -1;

            if (max > 0 && key.NumberOfUsages >= max)
            {
                try
                {
                    var owner = (TraderControllerClass)_deviceItem.Parent.GetOwner();
                    InteractionsHandlerClass.Discard(_deviceItem, owner, false);
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogError(
                        $"[HackerMod] Discard failed: {ex.GetType().Name}: {ex.Message}");
                }

                HackerMinigameController.Notify(
                    "Hacker Device burned out — discarded.",
                    ENotificationIconType.Alert,
                    new Color(1f, 0.5f, 0.3f));
            }
            else if (remaining >= 0)
            {
                HackerMinigameController.Notify(
                    $"Hacker Device: {remaining}/{max} uses remaining.",
                    ENotificationIconType.Default,
                    new Color(0.85f, 0.85f, 0.85f));
            }
        }

        // plays the doors DeniedBeep at its position via BetterAudio.PlayAtPointDelayed —
        // same path KeycardDoor.method_9 uses on a failed swipe so the buzz sounds vanilla.
        //
        // times=1 for a single strike, 2 for lockout. 0.35s spacing is tighter than the
        // clip length so the second hit reads as "denial doubled" not two separate buzzes.
        //
        // delayed-play API instead of a coroutine so audio survives Finish() teardown.
        internal static void PlayDeniedBeepStatic(KeycardDoor door, int times)
        {
            if (door == null || door.DeniedBeep == null || times < 1) return;

            try
            {
                var audio = MonoBehaviourSingleton<BetterAudio>.Instance;
                if (audio == null) return;
                var pos = door.transform.position;

                for (int i = 0; i < times; i++)
                {
                    audio.PlayAtPointDelayed(
                        position:      pos,
                        clip:          door.DeniedBeep,
                        sourceGroup:   BetterAudio.AudioSourceGroupType.Environment,
                        rolloff:       15,
                        volume:        0.7f,
                        delay:         0.35f * i,
                        occlusionTest: EOcclusionTest.None);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"[HackerMod] PlayDeniedBeep failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // tears down the runner and dispatches the user-facing toast.
        // silent suppresses the default toast (when the caller showed a more specific one).
        // lockoutMessage is the copy for permanent-lockout outcomes.
        private void Finish(bool success, string reason, bool silent, string lockoutMessage = null)
        {
            _running = false;

            try
            {
                if (success && _target != null)
                {
                    // door: Unlock pipeline (handle anim, GrantedBeep, UnlockSound, auto-open).
                    // atm: spit cash. target encapsulates the side-effect either way.
                    _target.OnSuccess(blueWin: reason != null && reason.IndexOf("Blue", System.StringComparison.OrdinalIgnoreCase) >= 0);
                }

                // Notify the target that this hack instance ended in
                // a final way — success or lockout-fail. Skip retryable
                // outcomes (white strike before lockout, abort, timeout)
                // so the player can re-engage.
                if (_target != null && (success || !string.IsNullOrEmpty(lockoutMessage)))
                {
                    try { _target.OnHackEnded(success); }
                    catch (Exception ex)
                    {
                        Plugin.LogSource?.LogWarning($"[HackerMod] OnHackEnded: {ex.Message}");
                    }
                }

                // Hold the outro animation for a beat so the Tap state
                // queued by the final F-press has time to play through
                // before the animator force-jumps to Outro Success/Fail.
                // Without this, two Animator.Play() calls land in the
                // same frame (Tap, then Outro) and the second wins —
                // so the player never sees the final tap.
                //
                // Capture the controller now: the finally block below
                // clears _owner, after which GetController() would
                // return null and the outro coroutine would no-op.
                var outroController = GetController();
                StartCoroutine(PlayOutroAfterTapDelay(outroController, success));

                if (!silent)
                {
                    if (success)
                    {
                        HackerMinigameController.Notify(
                            _target?.SuccessNotification ?? "Hack succeeded.",
                            ENotificationIconType.Default,
                            new Color(0.3f, 1f, 0.4f));
                    }
                    else if (!string.IsNullOrEmpty(lockoutMessage))
                    {
                        HackerMinigameController.Notify(
                            lockoutMessage,
                            ENotificationIconType.Alert,
                            new Color(1f, 0.35f, 0.35f));
                    }
                    else
                    {
                        HackerMinigameController.Notify(
                            $"Hack {reason.ToLowerInvariant()}.",
                            ENotificationIconType.Alert,
                            new Color(0.85f, 0.85f, 0.85f));
                    }
                }

                Plugin.LogSource?.LogInfo($"[HackerMod] hack {reason} (success={success})");

                // Schedule the put-away — wait for the outro animation
                // to play, then return the player to their previous
                // weapon via the same SetEmptyHands +
                // TrySetLastEquippedWeapon dance Mitsuru and MALA use.
                // Capture references locally so the coroutine survives
                // this MonoBehaviour being disabled in `finally`.
                var player    = _owner?.Player;
                var prevHands = _prevHandsItem;
                var animator  = GetController()?.PhoneAnimator;
                if (player != null)
                {
                    StartCoroutine(RestoreHandsAfterOutro(player, prevHands, animator));
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[HackerMod] Finish threw: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _target = null;
                _owner  = null;
                enabled = false;
            }
        }

        // hold before the outro fires so the player sees the final tap
        private const float TapHoldSeconds = 0.3f;

        // delays outro + screen swap so the queued Tap state has time to play.
        // controller is captured at Finish-time because _owner is cleared in
        // Finish's finally — by the time we wake up GetController() returns null.
        private IEnumerator PlayOutroAfterTapDelay(HackerDeviceController controller, bool success)
        {
            yield return new WaitForSeconds(TapHoldSeconds);

            if (controller != null)
            {
                if (success) controller.PlayOutroSuccess();
                else         controller.PlayOutroFail();
            }

            // outro through the canvas/RT pipeline so it inherits per-device
            // rotation + aspect (otherwise success swipe is sideways/upside-down
            // on anything but vanilla)
            _screenUI?.ShowOutro(success);

            // wait for the outro to play out, then tear down screen UI.
            // polling so we bail early if animator transitions back to Spawn.
            const int HandsLayer = 1;
            var animator = controller?.PhoneAnimator;
            if (animator != null)
            {
                float stop = Time.unscaledTime + 1.7f;
                while (Time.unscaledTime < stop)
                {
                    var info = animator.GetCurrentAnimatorStateInfo(HandsLayer);
                    if (info.IsName("Spawn")) break;
                    if ((info.IsName("Outro Success") || info.IsName("Outro Fail")) && info.normalizedTime >= 0.95f) break;
                    yield return null;
                }
            }
            else
            {
                yield return new WaitForSecondsRealtime(1.3f);
            }

            if (_screenUI != null)
            {
                try { _screenUI.Shutdown(); } catch { }
                Destroy(_screenUI);
                _screenUI = null;
            }
        }

        // wait for the outro to play, then teardown our controller and re-equip prev weapon.
        // we DONT go through SetEmptyHands — that triggers UsableItemController.Drop ->
        // Interface11_0.HideWeapon which tries to play put-away on our animator. our
        // animator doesnt have the engines expected put-away state so the callback
        // never fires and the pipeline hangs with the phone mesh stuck in the scene.
        //
        // our outro animation IS the put-away. once it finishes, DestroyController nulls
        // HandsController + disposes the prefab. then TrySetLastEquippedWeapon's
        // Process.Execute hits the HandsController == null fast-path (Player.cs:37217),
        // skips the drop step, and goes straight to creating the knife controller.
        private static IEnumerator RestoreHandsAfterOutro(Player player, Item prevHandsItem, Animator phoneAnimator)
        {
            if (player == null) yield break;

            // outro clip is 38f at 30fps (~1.27s); add buffer for TapHold pre-delay
            // and animator transition latency. break on Spawn state OR Outro past 0.95.
            if (phoneAnimator != null)
            {
                int hands  = phoneAnimator.layerCount > 1 ? 1 : 0;
                float stop = Time.unscaledTime + 1.7f;
                while (Time.unscaledTime < stop)
                {
                    var info = phoneAnimator.GetCurrentAnimatorStateInfo(hands);
                    if (info.IsName("Spawn")) break;
                    if ((info.IsName("Outro Success") || info.IsName("Outro Fail")) && info.normalizedTime >= 0.95f) break;
                    yield return null;
                }
            }
            else
            {
                yield return new WaitForSeconds(1.3f);
            }

            // Tear down our controller and re-equip the previous
            // weapon back-to-back. NO wait between them — even a single
            // frame of HandsController == null renders as bugged empty
            // arms in the player's first-person view. TrySetLastEquippedWeapon's
            // Process.Execute hits the HandsController == null fast-path
            // (Player.cs:37217) and skips the drop step, going straight
            // to creating the knife controller synchronously.
            try
            {
                if (player.HandsController is HackerDeviceController)
                {
                    Plugin.LogSource?.LogInfo("[HackerMod] RestoreHands: DestroyController + TrySetLastEquippedWeapon");
                    player.DestroyController();
                }

                if (prevHandsItem != null)
                {
                    player.TrySetLastEquippedWeapon(true, null);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[HackerMod] RestoreHands: {ex.Message}");
            }
            yield break;
        }

        // Cache of the prefab's authored screen texture, captured the
        // first time we see the renderer. We can't read it back from
        // renderer.material later because that copy gets overwritten
        // by ApplyOutroScreen during the hack.
        private static Texture _defaultScreenTex;

        // The phone's "Screen" mesh has UVs that sample only a sub-region
        // of the prefab texture. When we replace the material's
        // mainTexture with our own, those same UVs still target that
        // sub-region, so the icon has to be drawn inside it or it
        // shows up in a corner / cropped.
        //
        // Re-captured on every ResetPhoneScreen so a different device
        // variant (e.g. Ref's phone with its own mesh + UV layout)
        // doesn't reuse the previous device's rect.
        private static Rect _screenUVRect = new Rect(0, 0, 1, 1);

        // reads the screen mesh UV bounds. falls back to full 0-1 if mesh
        // isnt readable — icon will be miscentered but still visible.
        // runs every spawn so different mesh layouts get the right rect.
        private static void CaptureScreenUVRect(Renderer renderer)
        {
            // reset first so an unreadable mesh doesnt inherit previous rect
            _screenUVRect = new Rect(0, 0, 1, 1);

            Mesh mesh = null;
            var mf = renderer.GetComponent<MeshFilter>();
            if (mf != null) mesh = mf.sharedMesh;
            else if (renderer is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;

            if (mesh == null || mesh.uv == null || mesh.uv.Length == 0) return;

            var uvs = mesh.uv;
            float u0 = uvs[0].x, u1 = uvs[0].x, v0 = uvs[0].y, v1 = uvs[0].y;
            for (int i = 1; i < uvs.Length; i++)
            {
                if (uvs[i].x < u0) u0 = uvs[i].x;
                if (uvs[i].x > u1) u1 = uvs[i].x;
                if (uvs[i].y < v0) v0 = uvs[i].y;
                if (uvs[i].y > v1) v1 = uvs[i].y;
            }
            _screenUVRect = Rect.MinMaxRect(u0, v0, u1, v1);
            Plugin.LogSource?.LogInfo(
                $"[HackerMod] Screen UV rect: u({u0:F3}-{u1:F3}) v({v0:F3}-{v1:F3})");
        }

        // spins up the cosmetic phone-screen UI on the runner's GO so it gets
        // destroyed cleanly with us. finds the phones "Screen" child renderer
        // and hands it to the renderer for RT binding.
        private void StartPhoneScreenUI()
        {
            var animator = GetController()?.PhoneAnimator;
            if (animator == null) return;

            var screenT = FindChildByName(animator.transform.root, "Screen");
            if (screenT == null) return;
            var screenRenderer = screenT.GetComponent<Renderer>();
            if (screenRenderer == null) return;

            try
            {
                if (_screenUI != null) { try { _screenUI.Shutdown(); } catch { } Destroy(_screenUI); }
                _screenUI = gameObject.AddComponent<PhoneScreenRenderer>();
                string tpl = _deviceItem?.TemplateId ?? "";
                float rotation = HackerConstants.GetCanvasRotation(tpl);
                var size       = HackerConstants.GetCanvasSize(tpl);
                _screenUI.Initialize(screenRenderer, _screenUVRect, rotation, size.width, size.height);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[HackerMod] StartPhoneScreenUI: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // restores the phone screen to its authored default. called on new hack
        // so a previous outros green/red wash doesnt carry over (phone GO is pooled
        // so the per-instance material persists across hacks).
        private void ResetPhoneScreen()
        {
            var animator = GetController()?.PhoneAnimator;
            if (animator == null) return;

            var screen = FindChildByName(animator.transform.root, "Screen");
            if (screen == null) return;
            var renderer = screen.GetComponent<Renderer>();
            if (renderer == null) return;

            // Re-read sharedMaterial.mainTexture every spawn — different
            // device variants ship different default screen textures
            // (e.g. Ref's phone vs the vanilla one). sharedMaterial is
            // the prefab-level material so its texture is never the one
            // we overwrote, but it does change between prefabs.
            _defaultScreenTex = renderer.sharedMaterial?.mainTexture;

            // Cache the screen mesh's UV bounds — the outro icons need
            // to be drawn inside this rect or they'll land in the wrong
            // place / be cropped.
            CaptureScreenUVRect(renderer);

            try
            {
                renderer.material.mainTexture = _defaultScreenTex;

                // Tell the renderer it doesn't participate in lighting.
                // Tarkov's SSAO operates on the depth buffer post-shader,
                // so this won't fully kill darkening from hand silhouettes,
                // but it does correctly flag the surface as a non-lit
                // emissive panel and avoids any probe-based ambient tint.
                renderer.shadowCastingMode      = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows         = false;
                renderer.lightProbeUsage        = UnityEngine.Rendering.LightProbeUsage.Off;
                renderer.reflectionProbeUsage   = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"[HackerMod] ResetPhoneScreen: {ex.Message}");
            }
        }

        private static Transform FindChildByName(Transform root, string name)
        {
            if (root == null) return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                if (t.name == name) return t;
            }
            return null;
        }

    }
}
