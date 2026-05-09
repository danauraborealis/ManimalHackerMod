using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Manimal.HackerMod.Minigame
{
    // cosmetic in-world phone-screen UI. spawns a hidden Camera that renders
    // a Canvas into a RenderTexture, bound to the Screen mesh's mainTexture
    // for the hack duration. pure visual flair, doesnt affect game logic.
    //
    // runner drives it via Initialize / RegisterTap / Shutdown / ShowOutro.
    public class PhoneScreenRenderer : MonoBehaviour
    {
        // RT dims set per-device by Initialize. defaults to portrait.
        // Ref's phone uses landscape because its mesh UV is 180°-flipped from vanilla.
        private int _texWidth  = 360;
        private int _texHeight = 640;
        // layer 31 is conventionally unused in EFT — clean slice for our Camera
        private const int RenderLayer = 31;

        private const float SpawnInterval = 1.4f;
        private const float FlashSeconds  = 0.6f;
        // matches LayoutElement setup in BuildLineSlots. used to compute how
        // many slots fit in the visible LogRoot at runtime.
        private const float LineHeight  = 56f;
        private const float LineSpacing = 6f;
        // minimum slots even if detected height is tiny
        private const int MinLines = 3;
        private int       _maxLines = 5;

        private static readonly string[] LogPhrases =
        {
            "Resolving DNS for 10.0.42.{0}",
            "Probing port 22 (SSH)",
            "Cracking RSA-2048 session key",
            "Bypassing firewall rule 0x{1:X2}",
            "Spoofing MAC 4A:F2:{0:X2}:{1:X2}",
            "Tunneling through 443/TCP",
            "Authenticating as nobody",
            "Hash collision search...",
            "Reading /etc/shadow",
            "Decrypting auth token",
            "Injecting payload (stage {0})",
            "Reverse-shelling 0x{1:X4}",
            "Buffer overflow @ 0xDEADBEEF",
            "Pivoting through gateway",
            "Brute-forcing PIN ({0}/9999)",
            "Defeating 2FA challenge",
        };

        private Camera        _camera;
        private Canvas        _canvas;
        private RenderTexture _renderTexture;
        private RectTransform _logRoot;
        private TMP_FontAsset _font;

        private readonly List<LineUI> _lines = new List<LineUI>();
        private float _nextSpawnAt;
        private int   _phraseIdx;
        private System.Random _rng;

        private Renderer _screenRenderer;
        private Texture  _previousMainTex;
        private Vector2  _previousTextureScale  = Vector2.one;
        private Vector2  _previousTextureOffset = Vector2.zero;
        private bool     _texTransformApplied;

        // intro overlay: home-screen sprite fades out as the log fades in.
        // both share the canvas so a single RT samples them — screen mesh
        // just sees the cross-fade.
        private RawImage    _homeOverlay;
        private CanvasGroup _homeGroup;
        private CanvasGroup _logGroup;
        private bool        _logActive; // line-spawn gate

        // outro overlay layers (all share canvas rotation/aspect with log):
        //   _outroBg    full-rect bg colour (white pre-swipe, red on fail)
        //   _outroFill  green panel growing bottom->top via Image.fillAmount.
        //               UI fill means sweep is in canvas-local space and
        //               always sweeps upward on screen regardless of rotation.
        //   _outroIcon  check/X centred on top of the fill
        private CanvasGroup _outroGroup;
        private Image       _outroBg;
        private Image       _outroFill;
        private Image       _outroIcon;
        private CanvasGroup _outroIconGroup;

        // applied to LogRoot + HomeOverlay + OutroOverlay so canvas content
        // lands horizontally on the visible screen. set per-device by the
        // runner — different prefabs have different UV orientations.
        private float _canvasRotation = 90f;

        private struct LineUI
        {
            public TextMeshProUGUI Text;
            public Image           Background;
            public Color           DefaultBgColor;
            public bool            Tapped;
            public float           FlashUntil;
            public Color           FlashColor;
        }

        // sets up the RT pipeline, binds the RT to the screen material, and
        // remaps the mesh's UV rect to the full 0-1 range of the RT so canvas
        // content fills the visible portion of the screen instead of being
        // clipped to a sub-region.
        public void Initialize(Renderer screenRenderer, Rect uvRect, float canvasRotation, int texWidth, int texHeight)
        {
            _screenRenderer = screenRenderer;
            _canvasRotation = canvasRotation;
            _rng = new System.Random();

            // If the caller didn't specify explicit RT dimensions
            // (passed 0/negative), auto-detect the mesh's visible plane
            // aspect and pick a matching texture aspect so the canvas
            // content doesn't get stretched along whichever axis is
            // mismatched between mesh and texture.
            if (texWidth > 0 && texHeight > 0)
            {
                _texWidth = texWidth; _texHeight = texHeight;
            }
            else
            {
                var (w, h) = ComputeRTDimensions(screenRenderer, canvasRotation);
                _texWidth = w; _texHeight = h;
                Plugin.LogSource?.LogInfo(
                    $"[HackerMod] PhoneScreenRenderer auto-sized RT: {_texWidth}×{_texHeight} (rotation {canvasRotation:0}°)");
            }

            _font = FindUsableFont();
            if (_font == null)
            {
                Plugin.LogSource?.LogWarning(
                    "[HackerMod] PhoneScreenRenderer: no TMP_FontAsset found, text won't render.");
            }

            // Find the home-screen content. Prefer a Sprite (lets the
            // user keep it as a Sprite asset in Unity, no need to also
            // export a separate Texture2D). Fall back to the screen
            // material's mainTexture if a sprite reference isn't
            // attached to the prefab.
            //
            // Sprite source convention: any disabled SpriteRenderer
            // anywhere in the phone prefab whose name starts with
            // "Home" — keeps it out of the 3D scene while remaining
            // discoverable by name. If you have multiple, the first
            // match wins.
            Sprite  homeSprite = FindHomeSprite(screenRenderer);
            Texture homeTex    = homeSprite == null
                ? screenRenderer?.sharedMaterial?.mainTexture
                : null;

            BuildRenderTextureCamera();
            BuildCanvas();
            BuildHomeOverlay(homeSprite, homeTex);
            BuildLineSlots();
            BuildOutroOverlay();

            // Initial state: home is fully visible, log is hidden.
            // Cross-fade is driven by BeginIntroTransition().
            if (_homeGroup != null) _homeGroup.alpha = 1f;
            if (_logGroup  != null) _logGroup.alpha  = 0f;

            // Bind the RT to the screen mesh — replaces the default
            // texture (or last hack's leftover) for the duration of
            // the active hack. ApplyOutroScreen will swap it out on
            // success/fail.
            if (_screenRenderer != null)
            {
                var mat = _screenRenderer.material;
                _previousMainTex       = mat.mainTexture;
                _previousTextureScale  = mat.mainTextureScale;
                _previousTextureOffset = mat.mainTextureOffset;
                mat.mainTexture = _renderTexture;

                // Remap the mesh's UV rect so it samples the full RT
                // (0,0)–(1,1) instead of just a sub-region. Skip if
                // uvRect is invalid (degenerate or full-texture default).
                if (uvRect.width > 0.001f && uvRect.height > 0.001f &&
                    !(Mathf.Approximately(uvRect.width, 1f) && Mathf.Approximately(uvRect.height, 1f) &&
                      Mathf.Approximately(uvRect.x, 0f) && Mathf.Approximately(uvRect.y, 0f)))
                {
                    mat.mainTextureScale  = new Vector2(1f / uvRect.width, 1f / uvRect.height);
                    mat.mainTextureOffset = new Vector2(-uvRect.x / uvRect.width, -uvRect.y / uvRect.height);
                    _texTransformApplied  = true;
                }
            }
        }

        // cross-fades home -> log over `duration` sec. unlocks line-spawn so
        // notifications start streaming as the hack screen comes on.
        public void BeginIntroTransition(float duration)
        {
            _logActive = true;
            // spawn first line immediately so theres something visible when fade starts
            _nextSpawnAt = Time.unscaledTime;
            StartCoroutine(FadeIntro(duration));
        }

        private System.Collections.IEnumerator FadeIntro(float duration)
        {
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float p = Mathf.Clamp01(t / Mathf.Max(0.001f, duration));
                if (_homeGroup != null) _homeGroup.alpha = 1f - p;
                if (_logGroup  != null) _logGroup.alpha  = p;
                yield return null;
            }
            if (_homeGroup != null) _homeGroup.alpha = 0f;
            if (_logGroup  != null) _logGroup.alpha  = 1f;
        }

        // F-pressed. flashes the oldest pending line in the zones color.
        // if every visible line is already tapped, spawn a fresh one and
        // flash that — keeps feedback responsive when the player out-paces
        // the spawn timer.
        public void RegisterTap(ZoneColor zone)
        {
            Color flash = zone == ZoneColor.Green ? new Color(0.20f, 0.85f, 0.30f, 0.85f)
                        : zone == ZoneColor.Blue  ? new Color(0.30f, 0.55f, 0.95f, 0.85f)
                        : zone == ZoneColor.Red   ? new Color(0.90f, 0.20f, 0.20f, 0.85f)
                                                  : new Color(0.95f, 0.78f, 0.15f, 0.85f);

            int target = -1;
            for (int i = 0; i < _lines.Count; i++)
            {
                if (!_lines[i].Tapped && _lines[i].Text.gameObject.activeSelf)
                {
                    target = i;
                    break;
                }
            }
            if (target < 0)
            {
                SpawnLine();
                target = _lines.Count > 0 ? FindFirstActiveIndex() : -1;
                if (target < 0) return;
            }

            var line = _lines[target];
            line.Tapped     = true;
            line.FlashUntil = Time.unscaledTime + FlashSeconds;
            line.FlashColor = flash;

            string mark = zone == ZoneColor.Green ? "  [OK]"
                        : zone == ZoneColor.Blue  ? "  [SUDO]"
                        : zone == ZoneColor.Red   ? "  [TRIPPED]"
                                                  : "  [WARN]";
            line.Text.text = StripStatus(line.Text.text) + mark;
            line.Background.color = flash;

            _lines[target] = line;
        }

        // restores the original texture + UV transform, disposes Camera/RT/Canvas
        public void Shutdown()
        {
            if (_screenRenderer != null)
            {
                try
                {
                    var mat = _screenRenderer.material;
                    if (_previousMainTex != null) mat.mainTexture = _previousMainTex;
                    if (_texTransformApplied)
                    {
                        mat.mainTextureScale  = _previousTextureScale;
                        mat.mainTextureOffset = _previousTextureOffset;
                    }
                }
                catch { /* renderer might be torn down already */ }
            }

            if (_canvas != null)         Destroy(_canvas.gameObject);
            if (_camera != null)         Destroy(_camera.gameObject);
            if (_renderTexture != null)  _renderTexture.Release();
        }

        private void Update()
        {
            // Spawn timer is gated by the intro transition so lines
            // don't accumulate behind the home overlay before the
            // player can see them.
            if (_logActive && Time.unscaledTime >= _nextSpawnAt)
            {
                SpawnLine();
                _nextSpawnAt = Time.unscaledTime + SpawnInterval;
            }

            // Fade flashing backgrounds back toward the default tone
            // once their flash window expires.
            for (int i = 0; i < _lines.Count; i++)
            {
                var line = _lines[i];
                if (line.Tapped && line.Background != null && Time.unscaledTime > line.FlashUntil)
                {
                    var faded = line.FlashColor;
                    faded.a = 0.35f;
                    line.Background.color = faded;
                    _lines[i] = line;
                }
            }
        }

        // ─────────── setup ───────────

        private void BuildRenderTextureCamera()
        {
            _renderTexture = new RenderTexture(_texWidth, _texHeight, 16)
            {
                name = "HackerMod.PhoneRT",
                useMipMap = false,
                filterMode = FilterMode.Bilinear,
            };
            _renderTexture.Create();

            var camGO = new GameObject("HackerMod.PhoneCamera");
            camGO.transform.SetParent(transform, false);
            DontDestroyOnLoad(camGO);

            _camera = camGO.AddComponent<Camera>();
            _camera.orthographic     = true;
            _camera.orthographicSize = _texHeight / 2f;
            _camera.aspect           = (float)_texWidth / _texHeight;
            _camera.clearFlags       = CameraClearFlags.SolidColor;
            _camera.backgroundColor  = new Color(0.06f, 0.07f, 0.10f, 1f);
            _camera.cullingMask      = 1 << RenderLayer;
            _camera.targetTexture    = _renderTexture;
            _camera.depth            = -100;
            _camera.allowHDR         = false;
            _camera.allowMSAA        = false;
        }

        private void BuildCanvas()
        {
            var canvasGO = new GameObject("HackerMod.PhoneCanvas");
            canvasGO.transform.SetParent(transform, false);
            canvasGO.layer = RenderLayer;

            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode   = RenderMode.ScreenSpaceCamera;
            _canvas.worldCamera  = _camera;
            _canvas.planeDistance = 1f;
            _canvas.sortingOrder = -100;

            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode            = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.referencePixelsPerUnit = 100f;

            // The phone is held in LANDSCAPE in 3D, but the screen
            // mesh's UV samples our (portrait) texture in a way that
            // makes horizontal layout in the canvas appear vertical
            // on the visible screen. Compensate with a 90° CCW
            // rotation on the log container, and swap its dimensions
            // (canvas-space height becomes log width post-rotation
            // and vice-versa) so the content fills the canvas after
            // the rotation lands.
            var rootGO = new GameObject("LogRoot");
            rootGO.layer = RenderLayer;
            rootGO.transform.SetParent(canvasGO.transform, false);

            _logRoot = rootGO.AddComponent<RectTransform>();
            _logRoot.anchorMin       = new Vector2(0.5f, 0.5f);
            _logRoot.anchorMax       = new Vector2(0.5f, 0.5f);
            _logRoot.pivot           = new Vector2(0.5f, 0.5f);
            _logRoot.anchoredPosition = Vector2.zero;
            // Size: when rotation is ±90°/±270° we swap dims so the
            // rotated rect fills the canvas; at 0°/180° the canvas
            // dims map straight through.
            bool perpendicular = Mathf.Abs(((_canvasRotation % 180f) + 180f) % 180f - 90f) < 0.5f;
            _logRoot.sizeDelta = perpendicular
                ? new Vector2(_texHeight - 40, _texWidth - 40)
                : new Vector2(_texWidth  - 40, _texHeight - 40);
            _logRoot.localRotation = Quaternion.Euler(0f, 0f, _canvasRotation);

            var layout = rootGO.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment        = TextAnchor.LowerLeft;
            layout.spacing               = LineSpacing;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childControlWidth     = true;
            layout.childControlHeight    = true;

            // (No RectMask2D — Unity's RectMask2D doesn't play well
            // with rotated rects: with the canvas rotated 90°/180°
            // it ends up clipping the entire content. We cap line
            // count via _maxLines below so overflow can't happen
            // anyway.)

            // Compute how many lines actually fit in the visible
            // LogRoot height. The +LineSpacing accounts for the fact
            // that N lines have N-1 spacings between them, so we add
            // one "phantom" spacing for the formula to work cleanly.
            // -1 leaves a small breathing margin so the bottom line
            // never sits flush against the rect edge (where it'd hit
            // the curved phone bezel).
            float available = _logRoot.sizeDelta.y;
            _maxLines = Mathf.Max(MinLines,
                Mathf.FloorToInt((available + LineSpacing) / (LineHeight + LineSpacing)) - 1);

            // CanvasGroup lets us alpha-fade the entire log layer in
            // one place during the intro transition.
            _logGroup = rootGO.AddComponent<CanvasGroup>();
            _logGroup.alpha = 0f;
        }

        // home-screen overlay covering the same canvas footprint as LogRoot
        // with matching rotation. UI Image if a Sprite is available (handles
        // alpha/slicing/atlases natively), RawImage for a Texture, transparent
        // panel if both null.
        private void BuildHomeOverlay(Sprite homeSprite, Texture homeTex)
        {
            var go = new GameObject("HomeOverlay");
            go.layer = RenderLayer;
            go.transform.SetParent(_canvas.transform, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin        = new Vector2(0.5f, 0.5f);
            rt.anchorMax        = new Vector2(0.5f, 0.5f);
            rt.pivot            = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            // Same dimension swap + rotation as LogRoot — the home
            // content then fills the canvas in the same orientation
            // the log lines do.
            bool perpendicular = Mathf.Abs(((_canvasRotation % 180f) + 180f) % 180f - 90f) < 0.5f;
            rt.sizeDelta = perpendicular
                ? new Vector2(_texHeight, _texWidth)
                : new Vector2(_texWidth,  _texHeight);
            rt.localRotation = Quaternion.Euler(0f, 0f, _canvasRotation);

            if (homeSprite != null)
            {
                var img = go.AddComponent<Image>();
                img.sprite         = homeSprite;
                img.preserveAspect = false;          // stretch to fill (the rotated rect already matches the sprite's intended landscape aspect)
                img.raycastTarget  = false;
                img.color          = Color.white;
            }
            else if (homeTex != null)
            {
                _homeOverlay = go.AddComponent<RawImage>();
                _homeOverlay.texture = homeTex;
                _homeOverlay.color   = Color.white;
                _homeOverlay.raycastTarget = false;
            }
            // If both null, we still want a CanvasGroup to drive the
            // fade so subsequent code doesn't NRE — leave the GO empty
            // visually.

            _homeGroup = go.AddComponent<CanvasGroup>();
            _homeGroup.alpha = 1f;

            // Sibling order: LogRoot is added AFTER HomeOverlay (build
            // order in Initialize), so log naturally renders above
            // home — but during intro home is alpha 1 and log is
            // alpha 0, so home is what's visible. Cross-fade swaps
            // them.
            go.transform.SetSiblingIndex(0);
        }

        // ─────────── outro overlay ───────────

        private void BuildOutroOverlay()
        {
            var go = new GameObject("OutroOverlay");
            go.layer = RenderLayer;
            go.transform.SetParent(_canvas.transform, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin        = new Vector2(0.5f, 0.5f);
            rt.anchorMax        = new Vector2(0.5f, 0.5f);
            rt.pivot            = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            // Outro is rotated 90° CW relative to the log/home content.
            // Empirically, the texture's V axis (which UI Image.fillAmount
            // sweeps along when fillMethod=Vertical) lines up with screen
            // horizontal when the overlay matches the log's rotation —
            // an extra -90° brings it back to vertical-on-screen.
            float outroRotation = _canvasRotation - 90f;
            bool perpendicular = Mathf.Abs(((outroRotation % 180f) + 180f) % 180f - 90f) < 0.5f;
            rt.sizeDelta = perpendicular
                ? new Vector2(_texHeight, _texWidth)
                : new Vector2(_texWidth,  _texHeight);
            rt.localRotation = Quaternion.Euler(0f, 0f, outroRotation);

            _outroGroup = go.AddComponent<CanvasGroup>();
            _outroGroup.alpha = 0f;

            // Background panel (white pre-swipe, becomes red on fail).
            var bgGO = new GameObject("Bg");
            bgGO.layer = RenderLayer;
            bgGO.transform.SetParent(go.transform, false);
            var bgRT = bgGO.AddComponent<RectTransform>();
            bgRT.anchorMin = Vector2.zero;
            bgRT.anchorMax = Vector2.one;
            bgRT.offsetMin = Vector2.zero;
            bgRT.offsetMax = Vector2.zero;
            _outroBg = bgGO.AddComponent<Image>();
            _outroBg.sprite        = GetWhitePixelSprite();
            _outroBg.color         = Color.white;
            _outroBg.raycastTarget = false;

            // Fill panel: green, fills from bottom-to-top in
            // canvas-LOCAL space. After all transforms (canvas
            // rotation + RT + mesh UV), local +Y aligns with
            // screen +Y because the log content's text reads
            // left-to-right horizontally on screen — same axis
            // alignment for both devices.
            var fillGO = new GameObject("Fill");
            fillGO.layer = RenderLayer;
            fillGO.transform.SetParent(go.transform, false);
            var fillRT = fillGO.AddComponent<RectTransform>();
            fillRT.anchorMin = Vector2.zero;
            fillRT.anchorMax = Vector2.one;
            fillRT.offsetMin = Vector2.zero;
            fillRT.offsetMax = Vector2.zero;
            _outroFill = fillGO.AddComponent<Image>();
            _outroFill.sprite       = GetWhitePixelSprite();
            _outroFill.color        = new Color(0.10f, 0.65f, 0.20f);
            _outroFill.type         = Image.Type.Filled;
            _outroFill.fillMethod   = Image.FillMethod.Vertical;
            _outroFill.fillOrigin   = (int)Image.OriginVertical.Bottom;
            _outroFill.fillAmount   = 0f;
            _outroFill.raycastTarget = false;

            // Icon (centered checkmark / X). Smallest dimension scales
            // the icon size so it stays consistent across rotations.
            int iconPx = Mathf.RoundToInt(Mathf.Min(rt.sizeDelta.x, rt.sizeDelta.y) * 0.45f);
            var iconGO = new GameObject("Icon");
            iconGO.layer = RenderLayer;
            iconGO.transform.SetParent(go.transform, false);
            var iconRT = iconGO.AddComponent<RectTransform>();
            iconRT.anchorMin = new Vector2(0.5f, 0.5f);
            iconRT.anchorMax = new Vector2(0.5f, 0.5f);
            iconRT.pivot     = new Vector2(0.5f, 0.5f);
            iconRT.sizeDelta = new Vector2(iconPx, iconPx);
            _outroIcon = iconGO.AddComponent<Image>();
            _outroIcon.color         = Color.white;
            _outroIcon.raycastTarget = false;
            _outroIconGroup = iconGO.AddComponent<CanvasGroup>();
            _outroIconGroup.alpha = 0f;

            // Render above home + log so flipping outro alpha to 1
            // covers them regardless of their alphas.
            go.transform.SetAsLastSibling();
        }

        // hides home + log, animates the outro.
        // success: white bg -> green fill bottom-up -> checkmark fades in.
        // fail: instant red bg + white X.
        public void ShowOutro(bool success)
        {
            if (_outroGroup == null) return;

            if (_homeGroup != null) _homeGroup.alpha = 0f;
            if (_logGroup  != null) _logGroup.alpha  = 0f;
            _outroGroup.alpha = 1f;

            if (success)
            {
                _outroBg.color        = Color.white;
                _outroFill.fillAmount = 0f;
                if (_outroIconGroup != null) _outroIconGroup.alpha = 0f;
                StartCoroutine(SuccessOutroSequence());
            }
            else
            {
                _outroBg.color        = new Color(0.75f, 0.10f, 0.10f);
                _outroFill.fillAmount = 0f;
                if (_outroIcon != null) _outroIcon.sprite = GetFailIconSprite();
                if (_outroIconGroup != null) _outroIconGroup.alpha = 1f;
            }
        }

        private System.Collections.IEnumerator SuccessOutroSequence()
        {
            // Pre-swipe wait — matches the hand swipe gesture in the
            // Outro_Success animation clip.
            float wait = SuccessSwipePreDelay;
            while (wait > 0f)
            {
                wait -= Time.unscaledDeltaTime;
                yield return null;
            }

            // Sweep the fill from bottom (0) to top (1).
            float t = 0f;
            while (t < SuccessSwipeSeconds)
            {
                t += Time.unscaledDeltaTime;
                float p = Mathf.Clamp01(t / SuccessSwipeSeconds);
                if (_outroFill != null) _outroFill.fillAmount = p;
                yield return null;
            }
            if (_outroFill != null) _outroFill.fillAmount = 1f;

            // Settle: show checkmark on top of the now-full green fill.
            if (_outroIcon != null) _outroIcon.sprite = GetSuccessIconSprite();
            if (_outroIconGroup != null) _outroIconGroup.alpha = 1f;
        }

        // Outro animation timing.
        private const int   OutroIconSize         = 256;
        private const float SuccessSwipePreDelay  = 14f / 30f;
        private const float SuccessSwipeSeconds   = 6f  / 30f;

        // Procedural sprites — built once, reused.
        private static Sprite _whitePixelSprite;
        private static Sprite _checkSprite;
        private static Sprite _xSprite;

        // 1x1 white sprite for Image components that need a sprite slot but
        // render as a solid color (color comes from Image.color)
        private static Sprite GetWhitePixelSprite()
        {
            if (_whitePixelSprite == null)
            {
                var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                {
                    hideFlags  = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode   = TextureWrapMode.Clamp,
                };
                tex.SetPixel(0, 0, Color.white);
                tex.Apply();
                _whitePixelSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
                _whitePixelSprite.hideFlags = HideFlags.HideAndDontSave;
            }
            return _whitePixelSprite;
        }

        private static Sprite GetSuccessIconSprite()
        {
            if (_checkSprite == null) _checkSprite = BuildIconSprite(drawCheck: true);
            return _checkSprite;
        }

        private static Sprite GetFailIconSprite()
        {
            if (_xSprite == null) _xSprite = BuildIconSprite(drawCheck: false);
            return _xSprite;
        }

        // procedural square icon: transparent bg, white check/X stroke.
        // used as the Sprite for the centred outro icon — bg color comes
        // from the _outroFill / _outroBg layers underneath.
        private static Sprite BuildIconSprite(bool drawCheck)
        {
            int size = OutroIconSize;
            var pixels = new Color[size * size];
            var transparent = new Color(0, 0, 0, 0);
            for (int i = 0; i < pixels.Length; i++) pixels[i] = transparent;

            int marginX = Mathf.RoundToInt(size * 0.10f);
            int marginY = Mathf.RoundToInt(size * 0.10f);
            int x0 = marginX, x1 = size - marginX;
            int y0 = marginY, y1 = size - marginY;
            int thickness = Mathf.Max(8, Mathf.RoundToInt(size * 0.08f));

            if (drawCheck)
            {
                int midX  = x0 + (x1 - x0) / 3;
                int dipY  = y0 + (y1 - y0) / 4;
                int leftY = y0 + (y1 - y0) / 2;
                DrawLine(pixels, size, x0,  leftY, midX, dipY, Color.white, thickness);
                DrawLine(pixels, size, midX, dipY, x1,   y1,   Color.white, thickness);
            }
            else
            {
                DrawLine(pixels, size, x0, y0, x1, y1, Color.white, thickness);
                DrawLine(pixels, size, x0, y1, x1, y0, Color.white, thickness);
            }

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags  = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp,
            };
            tex.SetPixels(pixels);
            tex.Apply();

            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        private static void DrawLine(Color[] pixels, int size, int x0, int y0, int x1, int y1, Color color, int thickness)
        {
            int dx = Mathf.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Mathf.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            int half = thickness / 2;

            while (true)
            {
                for (int oy = -half; oy <= half; oy++)
                for (int ox = -half; ox <= half; ox++)
                {
                    int x = x0 + ox, y = y0 + oy;
                    if (x >= 0 && x < size && y >= 0 && y < size)
                        pixels[y * size + x] = color;
                }
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        // walks the phone prefab hierarchy looking for a SpriteRenderer named
        // "Home*" (e.g. HomeScreen). expected to be disabled on the prefab so
        // it doesnt spawn a 3D billboard — we just want the sprite reference.
        private static Sprite FindHomeSprite(Renderer screenRenderer)
        {
            if (screenRenderer == null) return null;

            var root = screenRenderer.transform.root;
            var srs = root.GetComponentsInChildren<SpriteRenderer>(includeInactive: true);
            for (int i = 0; i < srs.Length; i++)
            {
                if (srs[i] == null || srs[i].sprite == null) continue;
                if (srs[i].name.StartsWith("Home", System.StringComparison.OrdinalIgnoreCase))
                    return srs[i].sprite;
            }
            // fallback: any SpriteRenderer (assume prefab has only one)
            for (int i = 0; i < srs.Length; i++)
            {
                if (srs[i] != null && srs[i].sprite != null) return srs[i].sprite;
            }
            return null;
        }

        private void BuildLineSlots()
        {
            // _maxLines was computed in BuildCanvas from visible LogRoot height —
            // only allocate that many so the recycler kicks in as soon as the
            // visible area fills, instead of overflowing the screen.
            for (int i = 0; i < _maxLines; i++)
            {
                var go = new GameObject($"Line_{i}");
                go.layer = RenderLayer;
                go.transform.SetParent(_logRoot, false);
                go.SetActive(false);

                var bg = go.AddComponent<Image>();
                bg.color = new Color(0.10f, 0.12f, 0.15f, 0.5f);

                var le = go.AddComponent<LayoutElement>();
                le.preferredHeight = 56;
                le.minHeight       = 56;

                var textGO = new GameObject("Text");
                textGO.layer = RenderLayer;
                textGO.transform.SetParent(go.transform, false);

                var rt = textGO.AddComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = new Vector2(12, 4);
                rt.offsetMax = new Vector2(-8, -4);

                var tmp = textGO.AddComponent<TextMeshProUGUI>();
                tmp.text       = "";
                tmp.fontSize   = 28;
                tmp.color      = new Color(0.85f, 0.92f, 0.85f, 1f);
                tmp.alignment  = TextAlignmentOptions.MidlineLeft;
                tmp.enableWordWrapping = false;
                tmp.overflowMode = TextOverflowModes.Ellipsis;
                if (_font != null) tmp.font = _font;

                _lines.Add(new LineUI
                {
                    Text           = tmp,
                    Background     = bg,
                    DefaultBgColor = bg.color,
                    Tapped         = false,
                });
            }
        }

        // ─────────── runtime ops ───────────

        private void SpawnLine()
        {
            int slot = FindUnusedSlot();
            if (slot < 0)
            {
                // All slots full — scroll: drop the oldest tapped, or
                // failing that the topmost line, and reuse the slot.
                slot = FindOldestTappedOrTop();
                if (slot < 0) return;
            }

            var line = _lines[slot];
            line.Text.gameObject.transform.parent.gameObject.SetActive(true);
            line.Text.text = FormatPhrase(NextPhrase());
            line.Tapped    = false;
            line.FlashUntil = 0f;
            line.Background.color = line.DefaultBgColor;
            _lines[slot] = line;

            // VerticalLayoutGroup re-sorts by sibling index — push to
            // bottom so the latest line is the freshest one to tap.
            line.Text.gameObject.transform.parent.SetAsLastSibling();
        }

        private int FindUnusedSlot()
        {
            for (int i = 0; i < _lines.Count; i++)
                if (!_lines[i].Text.gameObject.transform.parent.gameObject.activeSelf)
                    return i;
            return -1;
        }

        private int FindOldestTappedOrTop()
        {
            // Sibling index 0 is the topmost (oldest visible) line.
            int oldestIdx = -1;
            int oldestSibling = int.MaxValue;
            for (int i = 0; i < _lines.Count; i++)
            {
                var parent = _lines[i].Text.gameObject.transform.parent;
                if (!parent.gameObject.activeSelf) continue;
                int sib = parent.GetSiblingIndex();
                if (sib < oldestSibling)
                {
                    oldestSibling = sib;
                    oldestIdx     = i;
                }
            }
            return oldestIdx;
        }

        private int FindFirstActiveIndex()
        {
            for (int i = 0; i < _lines.Count; i++)
                if (_lines[i].Text.gameObject.transform.parent.gameObject.activeSelf)
                    return i;
            return -1;
        }

        private string NextPhrase()
        {
            var s = LogPhrases[_phraseIdx % LogPhrases.Length];
            _phraseIdx++;
            return s;
        }

        private string FormatPhrase(string s)
        {
            // Some templates use {0}/{1} so we get random-looking values.
            int a = _rng.Next(0, 256);
            int b = _rng.Next(0, 65536);
            string body;
            try { body = string.Format(s, a, b); }
            catch { body = s; }

            int hh = (System.DateTime.Now.Hour) % 24;
            int mm = (System.DateTime.Now.Minute) % 60;
            int ss = (System.DateTime.Now.Second) % 60;
            return $"[{hh:D2}:{mm:D2}:{ss:D2}] {body}";
        }

        private static string StripStatus(string s)
        {
            // Strip any prior "  [OK]" / "  [WARN]" / "  [TRIPPED]"
            // suffix so a re-tap doesn't double-stamp it. Belt-and-
            // suspenders — Tapped guard normally prevents this anyway.
            int i = s.LastIndexOf("  [");
            return i >= 0 ? s.Substring(0, i) : s;
        }

        private static TMP_FontAsset FindUsableFont()
        {
            // Tarkov has TMP fonts loaded for its own UI — grab any of
            // them. Resources.FindObjectsOfTypeAll surfaces assets that
            // aren't currently referenced from a scene, which is what
            // we want.
            var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            return fonts != null && fonts.Length > 0 ? fonts[0] : null;
        }

        // reads mesh bounds, finds the two non-thin axes, returns long/short ratio.
        // used to size the RT to match the mesh's visible plane aspect — otherwise
        // a mesh authored with a different shape than vanilla gets stretched.
        private static float ComputeMeshAspect(Renderer renderer)
        {
            if (renderer == null) return 1f;
            Mesh mesh = null;
            var mf = renderer.GetComponent<MeshFilter>();
            if (mf != null) mesh = mf.sharedMesh;
            else if (renderer is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;
            if (mesh == null) return 1f;

            var size = mesh.bounds.size;
            float[] dims = { size.x, size.y, size.z };
            System.Array.Sort(dims); // ascending: [thin, short-visible, long-visible]
            if (dims[1] < 0.0001f) return 1f;
            return dims[2] / dims[1]; // ≥ 1
        }

        // picks RT dims with aspect matching the mesh's visible plane (after
        // accounting for canvas rotation). for ±90° rotations the canvas content
        // is stored sideways in the texture so the texture aspect is the
        // *inverse* of the mesh aspect.
        private static (int width, int height) ComputeRTDimensions(Renderer renderer, float canvasRotation)
        {
            const int LongSide = 640;
            const int MinSide  = 96;

            float meshAspect = ComputeMeshAspect(renderer);
            bool perpendicular = Mathf.Abs(((canvasRotation % 180f) + 180f) % 180f - 90f) < 0.5f;
            float texAspect = perpendicular ? (1f / meshAspect) : meshAspect;

            int w, h;
            if (texAspect >= 1f)
            {
                w = LongSide;
                h = Mathf.Max(MinSide, Mathf.RoundToInt(LongSide / texAspect));
            }
            else
            {
                h = LongSide;
                w = Mathf.Max(MinSide, Mathf.RoundToInt(LongSide * texAspect));
            }
            return (w, h);
        }
    }
}
