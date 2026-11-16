using TMPro;
using UnityEngine;

/// <summary>
/// Presents live mold dismantling/reassembly stages, required next actions, completed vs remaining item counts,
/// overall progress bar, active screwdriver bit status, and real-time placement/tool feedback on a world-space VR Canvas.
/// Driven strictly by MoldAssemblyManager events without per-frame Update polling.
/// </summary>
[DisallowMultipleComponent]
public class AssemblyProgressManager : MonoBehaviour
{
    [Header("Core Manager Reference")]
    [SerializeField] private MoldAssemblyManager assemblyManager;

    [Header("Header & Stage UI Elements")]
    [SerializeField] private TextMeshProUGUI workflowModeText;
    [SerializeField] private UnityEngine.UI.Image workflowModeBadge;
    [SerializeField] private TextMeshProUGUI activeBitText;
    [SerializeField] private TextMeshProUGUI stageTitleText;
    [SerializeField] private TextMeshProUGUI instructionText;

    [Header("Completed vs Remaining Checklist Rows")]
    [SerializeField] private TextMeshProUGUI baseStatusRowText;
    [SerializeField] private TextMeshProUGUI wallStatusRowText;
    [SerializeField] private TextMeshProUGUI rodStatusRowText;
    [SerializeField] private TextMeshProUGUI screwInsertStatusRowText;
    [SerializeField] private TextMeshProUGUI screwTightenStatusRowText;

    [Header("Overall Progress Bar")]
    [SerializeField] private RectTransform progressFillRect;
    [SerializeField] private UnityEngine.UI.Image progressFillImage;
    [SerializeField] private TextMeshProUGUI progressPercentText;

    [Header("Live Feedback Banner")]
    [SerializeField] private UnityEngine.UI.Image feedbackBannerBackground;
    [SerializeField] private TextMeshProUGUI feedbackMessageText;

    private static readonly Color BadgeDismantleColor = new Color(0.85f, 0.46f, 0.09f, 0.95f);
    private static readonly Color BadgeReassembleColor = new Color(0.11f, 0.49f, 0.85f, 0.95f);
    private static readonly Color BadgeCompleteColor = new Color(0.11f, 0.68f, 0.36f, 0.95f);

    private static readonly Color FeedbackInfoBg = new Color(0.12f, 0.21f, 0.33f, 0.95f);
    private static readonly Color FeedbackSuccessBg = new Color(0.09f, 0.36f, 0.22f, 0.95f);
    private static readonly Color FeedbackWarningBg = new Color(0.44f, 0.28f, 0.06f, 0.95f);
    private static readonly Color FeedbackErrorBg = new Color(0.46f, 0.11f, 0.14f, 0.95f);

    public MoldAssemblyManager AssemblyManager => assemblyManager;

    public string CurrentStageDisplayed => stageTitleText != null ? stageTitleText.text : string.Empty;

    public string LastFeedbackDisplayed => feedbackMessageText != null ? feedbackMessageText.text : string.Empty;

    private void Awake()
    {
        if (assemblyManager == null)
            assemblyManager = GetComponent<MoldAssemblyManager>();
    }

    private void OnEnable()
    {
        if (assemblyManager == null)
            assemblyManager = GetComponent<MoldAssemblyManager>();

        if (assemblyManager != null)
        {
            assemblyManager.StateUpdated -= RefreshFromSnapshot;
            assemblyManager.FeedbackPublished -= ShowFeedback;
            assemblyManager.StateUpdated += RefreshFromSnapshot;
            assemblyManager.FeedbackPublished += ShowFeedback;
        }
    }

    private void Start()
    {
        if (assemblyManager != null)
            RefreshFromSnapshot(assemblyManager.CurrentSnapshot);
    }

    private void OnDisable()
    {
        if (assemblyManager != null)
        {
            assemblyManager.StateUpdated -= RefreshFromSnapshot;
            assemblyManager.FeedbackPublished -= ShowFeedback;
        }
    }

    /// <summary>
    /// Binds UI element references created on the world-space VR progress canvas.
    /// </summary>
    public void BindUIElements(
        MoldAssemblyManager manager,
        TextMeshProUGUI modeLabel,
        UnityEngine.UI.Image modeBadge,
        TextMeshProUGUI bitLabel,
        TextMeshProUGUI stageLabel,
        TextMeshProUGUI stepInstructionLabel,
        TextMeshProUGUI baseRow,
        TextMeshProUGUI wallRow,
        TextMeshProUGUI rodRow,
        TextMeshProUGUI insertRow,
        TextMeshProUGUI tightenRow,
        RectTransform fillRect,
        UnityEngine.UI.Image fillImage,
        TextMeshProUGUI percentLabel,
        UnityEngine.UI.Image bannerBg,
        TextMeshProUGUI bannerLabel)
    {
        assemblyManager = manager;
        workflowModeText = modeLabel;
        workflowModeBadge = modeBadge;
        activeBitText = bitLabel;
        stageTitleText = stageLabel;
        instructionText = stepInstructionLabel;
        baseStatusRowText = baseRow;
        wallStatusRowText = wallRow;
        rodStatusRowText = rodRow;
        screwInsertStatusRowText = insertRow;
        screwTightenStatusRowText = tightenRow;
        progressFillRect = fillRect;
        progressFillImage = fillImage;
        progressPercentText = percentLabel;
        feedbackBannerBackground = bannerBg;
        feedbackMessageText = bannerLabel;
    }

    /// <summary>
    /// Refreshes all VR HUD labels, checklist counts, and progress bar directly from a live MoldAssemblySnapshot.
    /// </summary>
    public void RefreshFromSnapshot(MoldAssemblySnapshot snapshot)
    {
        bool isDismantling = snapshot.WorkflowPhase == AssemblyWorkflowPhase.Dismantling;

        if (workflowModeText != null)
        {
            workflowModeText.text = snapshot.IsAssemblyComplete
                ? "ASSEMBLY VERIFIED"
                : (isDismantling ? "DISMANTLING PHASE" : "REASSEMBLY PHASE");
        }

        if (workflowModeBadge != null)
        {
            workflowModeBadge.color = snapshot.IsAssemblyComplete
                ? BadgeCompleteColor
                : (isDismantling ? BadgeDismantleColor : BadgeReassembleColor);
        }

        if (activeBitText != null)
        {
            activeBitText.text = $"Active Bit: <color=#FDE047>{snapshot.ActiveBitName}</color>  <color=#94A3B8>[{snapshot.ActiveBitFastenerTag}]</color>";
        }

        if (stageTitleText != null)
            stageTitleText.text = MoldAssemblyManager.GetStageTitle(snapshot);

        if (instructionText != null)
            instructionText.text = MoldAssemblyManager.GetStageInstructionText(snapshot);

        if (isDismantling)
        {
            SetRowText(
                screwTightenStatusRowText,
                "1. Loosen Nuts to Rod Tips",
                snapshot.ScrewsLoosenedOrRemoved,
                snapshot.ScrewsRequired,
                "Loosened",
                snapshot.ScrewsLoosenedOrRemoved >= snapshot.ScrewsRequired,
                snapshot.ScrewsLoosenedOrRemoved < snapshot.ScrewsRequired);

            SetRowText(
                rodStatusRowText,
                "2. Remove Rods (Nuts Attached)",
                snapshot.RodsRequired - snapshot.RodsInstalled,
                snapshot.RodsRequired,
                "Removed",
                snapshot.RodsInstalled == 0,
                snapshot.ScrewsLoosenedOrRemoved > 0 && snapshot.RodsInstalled > 0);

            SetRowText(
                wallStatusRowText,
                "3. Remove Side Pieces (First)",
                snapshot.ShortWallsRequired - snapshot.ShortWallsInstalled,
                snapshot.ShortWallsRequired,
                "Removed",
                snapshot.ShortWallsInstalled == 0,
                snapshot.RodsInstalled == 0 && snapshot.ShortWallsInstalled > 0);

            SetRowText(
                baseStatusRowText,
                "4. Remove Length Pieces",
                snapshot.LongWallsRequired - snapshot.LongWallsInstalled,
                snapshot.LongWallsRequired,
                "Removed",
                snapshot.LongWallsInstalled == 0,
                snapshot.RodsInstalled == 0 && snapshot.ShortWallsInstalled == 0 && snapshot.LongWallsInstalled > 0);

            if (screwInsertStatusRowText != null)
            {
                int removedWithRods = snapshot.ScrewsRequired - snapshot.ScrewsInserted;
                int atTipOnMold = Mathf.Max(0, snapshot.ScrewsLoosenedOrRemoved - removedWithRods);
                screwInsertStatusRowText.text =
                    $"<color=#CBD5E1>   Fastener Status:  {snapshot.ScrewsTightened} Tightened  |  {atTipOnMold} At Rod Tip (100%)  |  {removedWithRods} Attached to Removed Rods</color>";
            }
        }
        else
        {
            SetRowText(
                baseStatusRowText,
                "Step 1 — Long Walls (Place First)",
                snapshot.LongWallsInstalled,
                snapshot.LongWallsRequired,
                "Placed",
                snapshot.LongWallsInstalled >= snapshot.LongWallsRequired,
                snapshot.Stage == MoldAssemblyStage.LongWallInstallation);

            SetRowText(
                wallStatusRowText,
                "Step 2 — Short Walls (After Long)",
                snapshot.ShortWallsInstalled,
                snapshot.ShortWallsRequired,
                "Placed",
                snapshot.ShortWallsInstalled >= snapshot.ShortWallsRequired,
                snapshot.Stage == MoldAssemblyStage.ShortWallInstallation);

            SetRowText(
                rodStatusRowText,
                "Step 3 — Insert Rods (Nuts Attached)",
                snapshot.RodsInstalled,
                snapshot.RodsRequired,
                "Installed",
                snapshot.RodsInstalled >= snapshot.RodsRequired,
                snapshot.Stage == MoldAssemblyStage.RodInstallation);

            SetRowText(
                screwInsertStatusRowText,
                "Step 4a — Nuts Seated at Rod Tips",
                snapshot.ScrewsInserted,
                snapshot.ScrewsRequired,
                "Seated",
                snapshot.ScrewsInserted >= snapshot.ScrewsRequired,
                snapshot.Stage == MoldAssemblyStage.ScrewInsertion);

            SetRowText(
                screwTightenStatusRowText,
                "Step 4b — Tighten Nuts (100%)",
                snapshot.ScrewsTightened,
                snapshot.ScrewsRequired,
                "Tightened",
                snapshot.ScrewsTightened >= snapshot.ScrewsRequired,
                snapshot.Stage == MoldAssemblyStage.ScrewTightening);
        }

        float pct = Mathf.Clamp01(snapshot.OverallProgressNormalized);
        if (progressFillRect != null)
        {
            progressFillRect.anchorMin = Vector2.zero;
            progressFillRect.anchorMax = new Vector2(pct, 1f);
            progressFillRect.offsetMin = Vector2.zero;
            progressFillRect.offsetMax = Vector2.zero;
        }

        if (progressFillImage != null)
        {
            progressFillImage.color = snapshot.IsAssemblyComplete
                ? BadgeCompleteColor
                : (isDismantling ? BadgeDismantleColor : BadgeReassembleColor);
        }

        if (progressPercentText != null)
        {
            string phasePrefix = snapshot.IsAssemblyComplete
                ? "Assembly Complete"
                : (isDismantling ? "Dismantling Progress" : "Reassembly Progress");
            progressPercentText.text = $"{phasePrefix}: {Mathf.RoundToInt(pct * 100f)}%";
        }
    }

    /// <summary>
    /// Updates the color-coded live feedback banner with placement, stage-gate, or tool compatibility messages.
    /// </summary>
    public void ShowFeedback(AssemblyFeedbackSeverity severity, string message)
    {
        if (feedbackMessageText != null)
            feedbackMessageText.text = message;

        if (feedbackBannerBackground != null)
        {
            switch (severity)
            {
                case AssemblyFeedbackSeverity.Success:
                    feedbackBannerBackground.color = FeedbackSuccessBg;
                    break;
                case AssemblyFeedbackSeverity.Warning:
                    feedbackBannerBackground.color = FeedbackWarningBg;
                    break;
                case AssemblyFeedbackSeverity.Error:
                    feedbackBannerBackground.color = FeedbackErrorBg;
                    break;
                default:
                    feedbackBannerBackground.color = FeedbackInfoBg;
                    break;
            }
        }
    }

    private static void SetRowText(
        TextMeshProUGUI label,
        string title,
        int completed,
        int required,
        string unitVerb,
        bool isDone,
        bool isActive)
    {
        if (label == null)
            return;

        int remaining = Mathf.Max(0, required - completed);
        string prefixTag;
        string hexColor;

        if (isDone)
        {
            prefixTag = "[DONE]";
            hexColor = "#4ADE80";
        }
        else if (isActive)
        {
            prefixTag = "[NEXT]";
            hexColor = "#38BDF8";
        }
        else
        {
            prefixTag = "[WAIT]";
            hexColor = "#94A3B8";
        }

        label.text = $"<color={hexColor}>{prefixTag}  {title}:  <b>{completed}/{required}</b> {unitVerb}  <i>({remaining} remaining)</i></color>";
    }
}
