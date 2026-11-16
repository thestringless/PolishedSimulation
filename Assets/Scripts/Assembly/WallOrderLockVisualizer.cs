using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Renders clear 3D visual padlock badges on Mold Walls whenever sequence locks restrict removal or installation:
/// - Dismantling Stage: Once tie rods & vertical bolts are removed, displays a red LOCKED badge on both
///   Length Pieces (Long Walls) until BOTH Side Pieces (Short End Walls) have been removed first.
/// - Assembling Stage: Displays a red LOCKED badge at both Side Piece (Short End Wall) slots until BOTH
///   Length Pieces (Long Walls) are installed first (and shows LOCKED on Length Pieces once a Side Piece locks them in).
/// </summary>
[DisallowMultipleComponent]
public class WallOrderLockVisualizer : MonoBehaviour
{
    [SerializeField] private MoldAssemblyManager assemblyManager;

    private static Sprite s_padlockSprite;
    private static Sprite s_pillSprite;

    private sealed class LockBadgeInstance
    {
        public MoldSnapPoint SnapPoint;
        public Transform Root;
        public Image Bg;
        public Image Icon;
        public TextMeshProUGUI Label;
    }

    private readonly List<LockBadgeInstance> _badges = new List<LockBadgeInstance>();

    private void Awake()
    {
        if (assemblyManager == null)
            assemblyManager = GetComponent<MoldAssemblyManager>();

        EnsureSprites();
        BuildBadges();
    }

    private void OnEnable()
    {
        if (assemblyManager == null)
            assemblyManager = GetComponent<MoldAssemblyManager>();

        if (assemblyManager != null)
            assemblyManager.StateUpdated += OnStateUpdated;

        RefreshBadges();
    }

    private void Start()
    {
        RefreshBadges();
    }

    private void OnDisable()
    {
        if (assemblyManager != null)
            assemblyManager.StateUpdated -= OnStateUpdated;
    }

    private void OnDestroy()
    {
        for (int i = 0; i < _badges.Count; i++)
        {
            if (_badges[i].Root != null)
            {
                if (Application.isPlaying) Destroy(_badges[i].Root.gameObject);
                else DestroyImmediate(_badges[i].Root.gameObject);
            }
        }
        _badges.Clear();
    }

    private void LateUpdate()
    {
        Camera cam = Camera.main;
        if (cam == null)
            return;

        for (int i = 0; i < _badges.Count; i++)
        {
            var b = _badges[i];
            if (b.Root == null || !b.Root.gameObject.activeSelf || b.SnapPoint == null)
                continue;

            Vector3 center = b.SnapPoint.IsOccupied && b.SnapPoint.OccupiedPart != null
                ? MoldSnapPoint.GetPartWorldGeometricCenter(b.SnapPoint.OccupiedPart)
                : b.SnapPoint.TargetWorldGeometricCenter;

            b.Root.position = center + Vector3.up * 0.39f;

            Vector3 toCam = b.Root.position - cam.transform.position;
            if (toCam.sqrMagnitude > 1e-6f)
                b.Root.rotation = Quaternion.LookRotation(toCam.normalized, Vector3.up);
        }
    }

    private void OnStateUpdated(MoldAssemblySnapshot snapshot)
    {
        RefreshBadges();
    }

    public void RefreshBadges()
    {
        if (assemblyManager == null)
            return;

        EnsureSprites();
        if (_badges.Count == 0)
            BuildBadges();

        MoldAssemblySnapshot s = assemblyManager.CurrentSnapshot;
        bool isDismantling = assemblyManager.WorkflowPhase == AssemblyWorkflowPhase.Dismantling;
        bool allRodsRemoved = s.RodsInstalled == 0 && s.ScrewsInserted == 0;
        bool anyShortWallOccupied = s.ShortWallsInstalled > 0;
        bool bothLongWallsOccupied = s.LongWallsInstalled >= s.LongWallsRequired && s.LongWallsRequired > 0;
        bool allBaseOccupied = s.BaseInstalled >= s.BaseRequired;

        for (int i = 0; i < _badges.Count; i++)
        {
            var b = _badges[i];
            if (b.SnapPoint == null || b.Root == null)
                continue;

            bool showLock = false;
            string lockText = string.Empty;

            if (isDismantling)
            {
                // During Dismantling, once rods are removed, highlight that Length Walls are LOCKED until Side Walls are removed first!
                if (b.SnapPoint.AcceptedCategory == MoldPartCategory.LongWall &&
                    b.SnapPoint.IsOccupied &&
                    allRodsRemoved &&
                    anyShortWallOccupied)
                {
                    showLock = true;
                    lockText = "LOCKED: Remove Side Pieces First";
                }
            }
            else // Reassembling
            {
                if (b.SnapPoint.AcceptedCategory == MoldPartCategory.ShortWall &&
                    !b.SnapPoint.IsOccupied &&
                    allBaseOccupied &&
                    !bothLongWallsOccupied)
                {
                    showLock = true;
                    lockText = "LOCKED: Place Length Pieces First";
                }
                else if (b.SnapPoint.AcceptedCategory == MoldPartCategory.LongWall &&
                         b.SnapPoint.IsOccupied &&
                         anyShortWallOccupied &&
                         s.WallsInstalled < s.WallsRequired)
                {
                    showLock = true;
                    lockText = "LOCKED by Side Piece";
                }
            }

            if (b.Root.gameObject.activeSelf != showLock)
                b.Root.gameObject.SetActive(showLock);

            if (showLock && b.Label != null)
                b.Label.text = lockText;
        }
    }

    private void BuildBadges()
    {
        _badges.Clear();
        if (assemblyManager == null || assemblyManager.WallSnapPoints == null)
            return;

        var wallPoints = assemblyManager.WallSnapPoints;
        for (int i = 0; i < wallPoints.Count; i++)
        {
            MoldSnapPoint sp = wallPoints[i];
            if (sp == null)
                continue;

            GameObject rootGo = new GameObject($"__WallLockBadge_{sp.name}", typeof(RectTransform), typeof(Canvas));
            rootGo.hideFlags = HideFlags.DontSave;
            if (transform.parent != null)
            {
                Transform runtimeContainer = transform.parent.Find("RuntimeVisuals");
                if (runtimeContainer == null)
                {
                    GameObject rc = new GameObject("RuntimeVisuals");
                    rc.hideFlags = HideFlags.DontSave;
                    rc.transform.SetParent(transform.parent, false);
                    runtimeContainer = rc.transform;
                }
                rootGo.transform.SetParent(runtimeContainer, false);
            }
            Canvas canvas = rootGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 22;

            RectTransform rt = rootGo.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(340f, 56f);
            rootGo.transform.localScale = Vector3.one * 0.00085f;

            GameObject bgGo = new GameObject("Pill");
            bgGo.transform.SetParent(rootGo.transform, false);
            Image bg = bgGo.AddComponent<Image>();
            bg.sprite = s_pillSprite;
            bg.color = new Color(0.24f, 0.05f, 0.06f, 0.93f);
            bg.raycastTarget = false;
            RectTransform bgRt = bgGo.GetComponent<RectTransform>();
            bgRt.anchorMin = Vector2.zero;
            bgRt.anchorMax = Vector2.one;
            bgRt.offsetMin = Vector2.zero;
            bgRt.offsetMax = Vector2.zero;

            GameObject iconGo = new GameObject("Padlock");
            iconGo.transform.SetParent(rootGo.transform, false);
            Image icon = iconGo.AddComponent<Image>();
            icon.sprite = s_padlockSprite;
            icon.color = new Color(1.0f, 0.30f, 0.28f, 1f);
            icon.raycastTarget = false;
            RectTransform iconRt = iconGo.GetComponent<RectTransform>();
            iconRt.anchorMin = new Vector2(0f, 0.5f);
            iconRt.anchorMax = new Vector2(0f, 0.5f);
            iconRt.pivot = new Vector2(0.5f, 0.5f);
            iconRt.anchoredPosition = new Vector2(30f, 0f);
            iconRt.sizeDelta = new Vector2(36f, 36f);

            GameObject lblGo = new GameObject("Label");
            lblGo.transform.SetParent(rootGo.transform, false);
            TextMeshProUGUI lbl = lblGo.AddComponent<TextMeshProUGUI>();
            lbl.fontSize = 20f;
            lbl.fontStyle = FontStyles.Bold;
            lbl.alignment = TextAlignmentOptions.MidlineLeft;
            lbl.color = new Color(1f, 0.88f, 0.86f, 1f);
            lbl.raycastTarget = false;
            RectTransform lblRt = lblGo.GetComponent<RectTransform>();
            lblRt.anchorMin = Vector2.zero;
            lblRt.anchorMax = Vector2.one;
            lblRt.offsetMin = new Vector2(56f, 4f);
            lblRt.offsetMax = new Vector2(-12f, -4f);

            rootGo.SetActive(false);

            _badges.Add(new LockBadgeInstance
            {
                SnapPoint = sp,
                Root = rootGo.transform,
                Bg = bg,
                Icon = icon,
                Label = lbl
            });
        }
    }

    private static void EnsureSprites()
    {
        if (s_padlockSprite != null && s_pillSprite != null)
            return;

        const int res = 64;
        // Padlock icon (body + shackle arch)
        s_padlockSprite = CreateTexSprite("LockIcon", res, res, (u, v) =>
        {
            // Body rectangle: u in [0.22..0.78], v in [0.12..0.54]
            bool body = u >= 0.22f && u <= 0.78f && v >= 0.12f && v <= 0.54f;
            bool keyhole = Vector2.Distance(new Vector2(u, v), new Vector2(0.5f, 0.34f)) < 0.065f;
            if (body && !keyhole)
                return Color.white;

            // Shackle arch: center (0.5, 0.54), radius 0.19, thickness 0.055, v >= 0.52
            if (v >= 0.50f)
            {
                float d = Vector2.Distance(new Vector2(u, v), new Vector2(0.5f, 0.54f));
                if (d >= 0.135f && d <= 0.215f)
                    return Color.white;
            }
            return Color.clear;
        });

        s_pillSprite = CreateTexSprite("LockPill", 128, 32, (u, v) =>
        {
            float border = (u < 0.015f || u > 0.985f || v < 0.06f || v > 0.94f) ? 1f : 0f;
            return border > 0.5f ? new Color(1f, 0.35f, 0.35f, 1f) : Color.white;
        });
    }

    private static Sprite CreateTexSprite(string name, int w, int h, System.Func<float, float, Color> eval)
    {
        Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.name = name;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        tex.hideFlags = HideFlags.DontSave;
        Color[] px = new Color[w * h];
        for (int y = 0; y < h; y++)
        {
            float v = (y + 0.5f) / h;
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w;
                px[y * w + x] = eval(u, v);
            }
        }
        tex.SetPixels(px);
        tex.Apply(false, false);
        Sprite sp = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
        sp.hideFlags = HideFlags.DontSave;
        return sp;
    }
}
