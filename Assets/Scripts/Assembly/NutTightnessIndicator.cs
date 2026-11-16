using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Displays a crisp, camera-facing 3D status indicator above a seated nut/fastener:
/// - Red X mark (with live circular torque progress ring) whenever seated and NOT fully tight (0% .. 97%).
/// - Green Tick mark (with pop-scale confirmation) as soon as the nut reaches 100% full torque.
/// Hides cleanly when the nut is completely removed (Free on the floor).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(ScrewInteraction))]
public class NutTightnessIndicator : MonoBehaviour
{
    [Header("Indicator Placement")]
    [Tooltip("World-space vertical offset above the nut's geometric center.")]
    [SerializeField] private float verticalOffset = 0.068f;

    [Tooltip("Outward offset along the bolt's front/loosen axis so the badge never clips into the mold wall.")]
    [SerializeField] private float outwardOffset = 0.018f;

    [Tooltip("World diameter (meters) of the status badge.")]
    [SerializeField] private float badgeWorldSize = 0.052f;

    [Header("Touch Visibility")]
    [Tooltip("When true, the Red X / Green Tick indicator is hidden by default and only appears when the screwdriver touches the nut.")]
    [SerializeField] private bool showOnlyWhenScrewdriverTouches = true;

    [Tooltip("Short grace hold (seconds) after contact ends to prevent micro-jitter flicker.")]
    [SerializeField] private float touchLingerSeconds = 0.25f;

    private static Sprite s_discSprite;
    private static Sprite s_ringSprite;
    private static Sprite s_redCrossSprite;
    private static Sprite s_greenTickSprite;
    private static ScrewTighteningController s_cachedTighteningController;

    private static readonly Color RedBgColor = new Color(0.22f, 0.04f, 0.05f, 0.92f);
    private static readonly Color RedAccentColor = new Color(1.00f, 0.20f, 0.22f, 1.00f);
    private static readonly Color GreenBgColor = new Color(0.04f, 0.22f, 0.09f, 0.92f);
    private static readonly Color GreenAccentColor = new Color(0.14f, 0.92f, 0.42f, 1.00f);
    private static readonly Color AmberProgressColor = new Color(1.00f, 0.68f, 0.16f, 1.00f);

    private ScrewInteraction _screw;
    private MoldPart _part;
    private Transform _badgeRoot;
    private Canvas _canvas;
    private Image _bgImage;
    private Image _ringImage;
    private Image _symbolImage;
    private RectTransform _barFillRect;
    private Image _barFillImage;
    private TextMeshProUGUI _percentText;
    private bool _wasTightened;
    private bool _wasLoose;
    private bool _lastLoosenMode;
    private float _popTimer;
    private float _touchLingerTimer;

    private void Awake()
    {
        _screw = GetComponent<ScrewInteraction>();
        _part = GetComponent<MoldPart>();
        EnsureSpritesGenerated();
        EnsureBadgeVisuals();
    }

    private void OnEnable()
    {
        if (_screw == null) _screw = GetComponent<ScrewInteraction>();
        if (_part == null) _part = GetComponent<MoldPart>();

        if (_screw != null)
            _screw.TightenProgressChanged += OnTightenProgressChanged;
        if (_part != null)
            _part.StateChanged += OnPartStateChanged;

        RefreshVisualState(allowPop: false);
    }

    private void Start()
    {
        RefreshVisualState(allowPop: false);
    }

    private void OnDisable()
    {
        if (_screw != null)
            _screw.TightenProgressChanged -= OnTightenProgressChanged;
        if (_part != null)
            _part.StateChanged -= OnPartStateChanged;
    }

    private void OnDestroy()
    {
        if (_badgeRoot != null)
        {
            if (Application.isPlaying)
                Destroy(_badgeRoot.gameObject);
            else
                DestroyImmediate(_badgeRoot.gameObject);
        }
    }

    private void LateUpdate()
    {
        if (_screw == null || _part == null || _badgeRoot == null)
            return;

        bool isSeated = _screw.IsSeated && !_part.IsFree;
        if (!isSeated)
        {
            _touchLingerTimer = 0f;
            if (_badgeRoot.gameObject.activeSelf)
                _badgeRoot.gameObject.SetActive(false);
            return;
        }

        bool isTouchedNow = IsTouchedByScrewdriver();
        if (isTouchedNow)
            _touchLingerTimer = Mathf.Max(_touchLingerTimer, touchLingerSeconds);
        else if (_touchLingerTimer > 0f)
            _touchLingerTimer = Mathf.Max(0f, _touchLingerTimer - Time.deltaTime);

        bool show = !showOnlyWhenScrewdriverTouches || isTouchedNow || _touchLingerTimer > 0f;
        if (_badgeRoot.gameObject.activeSelf != show)
            _badgeRoot.gameObject.SetActive(show);

        if (!show)
            return;

        if (IsLoosenModeActive() != _lastLoosenMode)
            RefreshVisualState(allowPop: false);

        Vector3 nutCenter = MoldSnapPoint.GetPartWorldGeometricCenter(_part);
        Vector3 outward = _screw.CurrentSlot != null ? _screw.CurrentSlot.WorldLoosenDirection : Vector3.up;
        Vector3 worldPos;
        if (Mathf.Abs(outward.y) > 0.7f)
        {
            float centerZ = -7.116f;
            Transform moldRoot = _part.MoldContainer;
            if (moldRoot != null)
            {
                Transform pallet = moldRoot.Find("Cube.004");
                Renderer r = pallet != null ? pallet.GetComponent<Renderer>() : null;
                if (r != null) centerZ = r.bounds.center.z;
            }
            float zSign = nutCenter.z >= centerZ ? 1f : -1f;
            worldPos = nutCenter + Vector3.up * 0.052f + new Vector3(0f, 0f, zSign * 0.075f);
        }
        else
        {
            worldPos = nutCenter + Vector3.up * verticalOffset + outward * outwardOffset;
        }
        _badgeRoot.position = worldPos;

        Camera cam = Camera.main;
        if (cam != null)
        {
            Vector3 toCam = _badgeRoot.position - cam.transform.position;
            if (toCam.sqrMagnitude > 1e-6f)
                _badgeRoot.rotation = Quaternion.LookRotation(toCam.normalized, Vector3.up);
        }

        float scaleMult = 1f;
        if (_popTimer > 0f)
        {
            _popTimer = Mathf.Max(0f, _popTimer - Time.deltaTime);
            float t = 1f - (_popTimer / 0.28f);
            scaleMult = 1f + Mathf.Sin(t * Mathf.PI) * 0.38f;
        }
        _badgeRoot.localScale = Vector3.one * (badgeWorldSize * 0.01f * scaleMult);
    }

    private ScrewTighteningController GetTighteningController()
    {
        ScrewTighteningController ctrl = ScrewTighteningController.ActiveInstance;
        if (ctrl == null)
        {
            if (s_cachedTighteningController == null)
                s_cachedTighteningController = FindAnyObjectByType<ScrewTighteningController>();
            ctrl = s_cachedTighteningController;
        }
        return ctrl;
    }

    private bool IsTouchedByScrewdriver()
    {
        ScrewTighteningController ctrl = GetTighteningController();
        return ctrl != null && ctrl.IsScrewdriverTouchingFastener(_screw);
    }

    private bool IsLoosenModeActive()
    {
        ScrewTighteningController ctrl = GetTighteningController();
        return ctrl != null && ctrl.IsReverseDirectionActive;
    }

    private void OnTightenProgressChanged(ScrewInteraction screw, float progress, bool isTightened)
    {
        RefreshVisualState(allowPop: true);
    }

    private void OnPartStateChanged(MoldPart part, MoldPartState prev, MoldPartState next)
    {
        if (next == MoldPartState.Free)
            _touchLingerTimer = 0f;
        RefreshVisualState(allowPop: false);
    }

    public void RefreshVisualState(bool allowPop)
    {
        EnsureSpritesGenerated();
        EnsureBadgeVisuals();

        if (_screw == null || _part == null || _badgeRoot == null)
            return;

        bool seated = _screw.IsSeated && !_part.IsFree;
        if (!seated)
        {
            _wasTightened = false;
            _wasLoose = false;
            _touchLingerTimer = 0f;
            _badgeRoot.gameObject.SetActive(false);
            return;
        }

        bool loosenMode = IsLoosenModeActive();
        _lastLoosenMode = loosenMode;

        bool tight = _screw.IsTightened;
        bool loose = _screw.IsLoose;
        float rawProgress = Mathf.Clamp01(_screw.TightenProgress);

        // Direction-aware 0% -> 100% progress:
        // - In Loosen (Anti-Clockwise) mode: reaches 100% (1.0) the instant the nut reaches the tip of the rod!
        // - In Tighten (Clockwise) mode: reaches 100% (1.0) when fully torqued against the washer!
        float barProgress = loosenMode
            ? (loose ? 1f : Mathf.Clamp01(1f - rawProgress))
            : (tight ? 1f : rawProgress);

        if (tight)
        {
            _bgImage.color = GreenBgColor;
            _symbolImage.sprite = s_greenTickSprite;
            _symbolImage.color = GreenAccentColor;
        }
        else
        {
            _bgImage.color = RedBgColor;
            _symbolImage.sprite = s_redCrossSprite;
            _symbolImage.color = RedAccentColor;
        }

        Color progressColor = barProgress >= 0.995f
            ? GreenAccentColor
            : (barProgress > 0.02f ? AmberProgressColor : RedAccentColor);

        _ringImage.color = tight && !loosenMode ? GreenAccentColor : progressColor;
        _ringImage.fillAmount = barProgress;

        if (_barFillRect != null)
        {
            _barFillRect.anchorMin = Vector2.zero;
            _barFillRect.anchorMax = new Vector2(barProgress, 1f);
            _barFillRect.offsetMin = Vector2.zero;
            _barFillRect.offsetMax = Vector2.zero;
        }

        if (_barFillImage != null)
            _barFillImage.color = barProgress >= 0.995f ? GreenAccentColor : AmberProgressColor;

        if (_percentText != null)
            _percentText.text = $"{Mathf.RoundToInt(barProgress * 100f)}%";

        if (allowPop && Application.isPlaying)
        {
            bool reachedTightGoal = !_wasTightened && tight && !loosenMode;
            bool reachedRodTipGoal = !_wasLoose && loose && loosenMode;
            if (reachedTightGoal || reachedRodTipGoal)
            {
                if (!showOnlyWhenScrewdriverTouches || _touchLingerTimer > 0f || IsTouchedByScrewdriver())
                {
                    _popTimer = 0.28f;
                    _touchLingerTimer = Mathf.Max(_touchLingerTimer, 0.75f);
                }
            }
        }

        _wasTightened = tight;
        _wasLoose = loose;
        bool visible = !showOnlyWhenScrewdriverTouches || IsTouchedByScrewdriver() || _touchLingerTimer > 0f;
        _badgeRoot.gameObject.SetActive(visible);
    }

    private void EnsureBadgeVisuals()
    {
        if (!Application.isPlaying)
            return;

        if (_badgeRoot != null)
            return;

        // Clean up any leftover child from edit-time reloads
        Transform existing = transform.Find("__NutStatusBadge");
        if (existing != null)
            Destroy(existing.gameObject);

        GameObject rootGo = new GameObject($"__NutStatusBadge_{name}", typeof(RectTransform), typeof(Canvas));
        rootGo.hideFlags = HideFlags.DontSave;

        Transform systemFolder = _part != null && _part.MoldContainer != null ? _part.MoldContainer.parent : null;
        if (systemFolder != null)
        {
            Transform runtimeContainer = systemFolder.Find("RuntimeVisuals");
            if (runtimeContainer == null)
            {
                GameObject rc = new GameObject("RuntimeVisuals");
                rc.hideFlags = HideFlags.DontSave;
                rc.transform.SetParent(systemFolder, false);
                runtimeContainer = rc.transform;
            }
            rootGo.transform.SetParent(runtimeContainer, false);
        }

        _canvas = rootGo.GetComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;
        _canvas.sortingOrder = 25;

        RectTransform canvasRt = rootGo.GetComponent<RectTransform>();
        canvasRt.sizeDelta = new Vector2(100f, 100f);
        _badgeRoot = canvasRt;
        _badgeRoot.localScale = Vector3.one * (badgeWorldSize * 0.01f);

        // 1. Backing disc
        GameObject bgGo = new GameObject("Disc", typeof(RectTransform), typeof(Image));
        bgGo.transform.SetParent(_badgeRoot, false);
        _bgImage = bgGo.GetComponent<Image>();
        _bgImage.sprite = s_discSprite;
        _bgImage.raycastTarget = false;
        RectTransform bgRt = bgGo.GetComponent<RectTransform>();
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;

        // 2. Radial torque progress / border ring
        GameObject ringGo = new GameObject("Ring", typeof(RectTransform), typeof(Image));
        ringGo.transform.SetParent(_badgeRoot, false);
        _ringImage = ringGo.GetComponent<Image>();
        _ringImage.sprite = s_ringSprite;
        _ringImage.type = Image.Type.Filled;
        _ringImage.fillMethod = Image.FillMethod.Radial360;
        _ringImage.fillOrigin = (int)Image.Origin360.Top;
        _ringImage.fillClockwise = true;
        _ringImage.raycastTarget = false;
        RectTransform ringRt = ringGo.GetComponent<RectTransform>();
        ringRt.anchorMin = Vector2.zero;
        ringRt.anchorMax = Vector2.one;
        ringRt.offsetMin = Vector2.zero;
        ringRt.offsetMax = Vector2.zero;

        // 3. Crisp Red X or Green Tick symbol
        GameObject symGo = new GameObject("Symbol", typeof(RectTransform), typeof(Image));
        symGo.transform.SetParent(_badgeRoot, false);
        _symbolImage = symGo.GetComponent<Image>();
        _symbolImage.raycastTarget = false;
        RectTransform symRt = symGo.GetComponent<RectTransform>();
        symRt.anchorMin = new Vector2(0.16f, 0.16f);
        symRt.anchorMax = new Vector2(0.84f, 0.84f);
        symRt.offsetMin = Vector2.zero;
        symRt.offsetMax = Vector2.zero;

        // 4. Compact horizontal progress bar + "100%" readout directly below the badge
        GameObject barBgGo = new GameObject("ProgressBarBg", typeof(RectTransform), typeof(Image));
        barBgGo.transform.SetParent(_badgeRoot, false);
        Image barBgImg = barBgGo.GetComponent<Image>();
        barBgImg.color = new Color(0.06f, 0.09f, 0.14f, 0.95f);
        barBgImg.raycastTarget = false;
        RectTransform barBgRt = barBgGo.GetComponent<RectTransform>();
        barBgRt.anchorMin = new Vector2(0.5f, 0f);
        barBgRt.anchorMax = new Vector2(0.5f, 0f);
        barBgRt.pivot = new Vector2(0.5f, 1f);
        barBgRt.anchoredPosition = new Vector2(0f, -6f);
        barBgRt.sizeDelta = new Vector2(128f, 28f);

        GameObject trackGo = new GameObject("ProgressTrack", typeof(RectTransform), typeof(Image));
        trackGo.transform.SetParent(barBgRt, false);
        Image trackImg = trackGo.GetComponent<Image>();
        trackImg.color = new Color(0.16f, 0.20f, 0.28f, 0.95f);
        trackImg.raycastTarget = false;
        RectTransform trackRt = trackGo.GetComponent<RectTransform>();
        trackRt.anchorMin = Vector2.zero;
        trackRt.anchorMax = Vector2.one;
        trackRt.offsetMin = new Vector2(3f, 3f);
        trackRt.offsetMax = new Vector2(-3f, -3f);

        GameObject fillGo = new GameObject("ProgressFill", typeof(RectTransform), typeof(Image));
        fillGo.transform.SetParent(trackRt, false);
        _barFillImage = fillGo.GetComponent<Image>();
        _barFillImage.color = AmberProgressColor;
        _barFillImage.raycastTarget = false;
        _barFillRect = fillGo.GetComponent<RectTransform>();
        _barFillRect.anchorMin = Vector2.zero;
        _barFillRect.anchorMax = Vector2.one;
        _barFillRect.offsetMin = Vector2.zero;
        _barFillRect.offsetMax = Vector2.zero;

        GameObject pctGo = new GameObject("PercentLabel", typeof(RectTransform), typeof(TextMeshProUGUI));
        pctGo.transform.SetParent(barBgRt, false);
        _percentText = pctGo.GetComponent<TextMeshProUGUI>();
        _percentText.fontSize = 16f;
        _percentText.fontStyle = FontStyles.Bold;
        _percentText.alignment = TextAlignmentOptions.Center;
        _percentText.color = Color.white;
        _percentText.textWrappingMode = TextWrappingModes.NoWrap;
        _percentText.raycastTarget = false;
        _percentText.text = "100%";
        RectTransform pctRt = pctGo.GetComponent<RectTransform>();
        pctRt.anchorMin = Vector2.zero;
        pctRt.anchorMax = Vector2.one;
        pctRt.offsetMin = Vector2.zero;
        pctRt.offsetMax = Vector2.zero;

        if (showOnlyWhenScrewdriverTouches)
            rootGo.SetActive(false);
    }

    private static void EnsureSpritesGenerated()
    {
        if (s_discSprite != null && s_ringSprite != null && s_redCrossSprite != null && s_greenTickSprite != null)
            return;

        const int res = 128;
        s_discSprite = CreateProceduralSprite("NutDisc", res, (u, v) =>
        {
            float d = Vector2.Distance(new Vector2(u, v), new Vector2(0.5f, 0.5f));
            float a = Mathf.Clamp01((0.48f - d) * res * 0.5f);
            return new Color(1f, 1f, 1f, a);
        });

        s_ringSprite = CreateProceduralSprite("NutRing", res, (u, v) =>
        {
            float d = Vector2.Distance(new Vector2(u, v), new Vector2(0.5f, 0.5f));
            float outer = Mathf.Clamp01((0.485f - d) * res * 0.5f);
            float inner = Mathf.Clamp01((d - 0.385f) * res * 0.5f);
            return new Color(1f, 1f, 1f, outer * inner);
        });

        // Bold anti-aliased X mark
        s_redCrossSprite = CreateProceduralSprite("RedCrossIcon", res, (u, v) =>
        {
            Vector2 p = new Vector2(u, v);
            float d1 = DistanceToSegment(p, new Vector2(0.23f, 0.23f), new Vector2(0.77f, 0.77f));
            float d2 = DistanceToSegment(p, new Vector2(0.23f, 0.77f), new Vector2(0.77f, 0.23f));
            float d = Mathf.Min(d1, d2);
            float a = Mathf.Clamp01((0.088f - d) * res * 0.45f);
            return new Color(1f, 1f, 1f, a);
        });

        // Bold anti-aliased Checkmark / Tick mark
        s_greenTickSprite = CreateProceduralSprite("GreenTickIcon", res, (u, v) =>
        {
            Vector2 p = new Vector2(u, v);
            float d1 = DistanceToSegment(p, new Vector2(0.20f, 0.50f), new Vector2(0.42f, 0.27f));
            float d2 = DistanceToSegment(p, new Vector2(0.42f, 0.27f), new Vector2(0.82f, 0.74f));
            float d = Mathf.Min(d1, d2);
            float a = Mathf.Clamp01((0.092f - d) * res * 0.45f);
            return new Color(1f, 1f, 1f, a);
        });
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
        return Vector2.Distance(p, a + ab * t);
    }

    private static Sprite CreateProceduralSprite(string texName, int size, System.Func<float, float, Color> pixelEval)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false);
        tex.name = texName;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        tex.hideFlags = HideFlags.DontSave;

        Color[] pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            float v = (y + 0.5f) / size;
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;
                pixels[y * size + x] = pixelEval(u, v);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        Sprite sp = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        sp.hideFlags = HideFlags.DontSave;
        return sp;
    }
}
