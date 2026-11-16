using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Configurable startup mode for the mold training workflow.
/// </summary>
public enum MoldStartMode
{
    AssembledDismantleFirst,
    DismantledAtStart
}

/// <summary>
/// High-level workflow phase separating initial dismantling from guided sequential reassembly.
/// </summary>
public enum AssemblyWorkflowPhase
{
    Dismantling,
    Reassembling
}

/// <summary>
/// Ordered mechanical assembly stages validated strictly from live component states.
/// </summary>
public enum MoldAssemblyStage
{
    DismantleMold = 0,
    BaseInstallation = 1,
    LongWallInstallation = 2,
    ShortWallInstallation = 3,
    RodInstallation = 4,
    ScrewInsertion = 5,
    ScrewTightening = 6,
    Completed = 7
}

/// <summary>
/// Feedback severity categories surfaced to the VR progress UI.
/// </summary>
public enum AssemblyFeedbackSeverity
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// Immutable live snapshot of all mold component states (evaluated from actual MoldSnapPoint, MoldPart, ScrewSlot, and ScrewInteraction states).
/// </summary>
[Serializable]
public struct MoldAssemblySnapshot
{
    public AssemblyWorkflowPhase WorkflowPhase;
    public MoldAssemblyStage Stage;
    public int BaseInstalled;
    public int BaseRequired;
    public int LongWallsInstalled;
    public int LongWallsRequired;
    public int ShortWallsInstalled;
    public int ShortWallsRequired;
    public int WallsInstalled;
    public int WallsRequired;
    public int RodsInstalled;
    public int RodsRequired;
    public int ScrewsInserted;
    public int ScrewsRequired;
    public int ScrewsTightened;
    public int ScrewsLoosenedOrRemoved;
    public int TotalDismantledParts;
    public int TotalPartsRequired;
    public float OverallProgressNormalized;
    public bool IsAssemblyComplete;
    public string ActiveBitName;
    public string ActiveBitFastenerTag;
}

/// <summary>
/// Centralized manager owning the casting mold dismantling and reassembly sequence, stage gating,
/// live component-state validation, tool/placement feedback events, and overall progress calculation.
/// Never relies on blind interaction counters — every evaluation inspects actual live component states.
/// </summary>
[DisallowMultipleComponent]
public class MoldAssemblyManager : MonoBehaviour
{
    [Header("Workflow Configuration")]
    [Tooltip("AssembledDismantleFirst starts with the mold assembled so the user dismantles first, then reassembles. DismantledAtStart stages parts on the floor for immediate reassembly.")]
    [SerializeField] private MoldStartMode startMode = MoldStartMode.AssembledDismantleFirst;

    [Tooltip("When enabled, enforces strict stage prerequisites across Base -> Walls -> Rods -> Loose Screw Insertion -> Screw Tightening.")]
    [SerializeField] private bool enforceStrictStageGating = true;

    [Tooltip("When enabled alongside strict stage gating, requires all fasteners to be inserted in loose positions before forward tightening begins.")]
    [SerializeField] private bool requireAllScrewsInsertedBeforeTightening = false;

    [Header("Stage Snap Points & Threaded Slots")]
    [Tooltip("Stage 1 base snap points (Pallet Base + Inner Floor Plate).")]
    [SerializeField] private List<MoldSnapPoint> baseSnapPoints = new List<MoldSnapPoint>();

    [Tooltip("Stage 2 wall snap points (2 Long Side Walls + 2 Short End Walls).")]
    [SerializeField] private List<MoldSnapPoint> wallSnapPoints = new List<MoldSnapPoint>();

    [Tooltip("Stage 3 connecting rod & vertical bolt snap points (6 ScrewRods + VerticalScrew).")]
    [SerializeField] private List<MoldSnapPoint> rodSnapPoints = new List<MoldSnapPoint>();

    [Tooltip("Stage 4 & 5 threaded fastener slots (6 Rod Hex Nuts + 1 Vertical Hex Nut).")]
    [SerializeField] private List<ScrewSlot> screwSlots = new List<ScrewSlot>();

    [Header("Screwdriver & Tool References")]
    [SerializeField] private XRElectricScrewdriver screwdriver;
    [SerializeField] private ScrewdriverHeadAttachment headSocket;
    [SerializeField] private ScrewTighteningController tighteningController;

    [Header("Dismantled Staging Anchor")]
    [Tooltip("World origin near the workstation used when laying out dismantled parts via DeployDismantledLayout.")]
    [SerializeField] private Vector3 dismantledStagingOrigin = new Vector3(1.65f, 0.18f, -7.85f);

    private AssemblyWorkflowPhase _workflowPhase = AssemblyWorkflowPhase.Dismantling;
    private MoldAssemblySnapshot _currentSnapshot;
    private MoldAssemblyStage _lastReportedStage = (MoldAssemblyStage)(-1);
    private bool _stageGatesRegistered;
    private bool _suppressFeedbackEvents;

    /// <summary>Raised whenever live component states or workflow stage change.</summary>
    public event Action<MoldAssemblySnapshot> StateUpdated;

    /// <summary>Raised whenever the active MoldAssemblyStage transitions.</summary>
    public event Action<MoldAssemblyStage, MoldAssemblyStage> StageChanged;

    /// <summary>Raised for user-visible placement, rejection, stage-gate, or tool compatibility messages.</summary>
    public event Action<AssemblyFeedbackSeverity, string> FeedbackPublished;

    /// <summary>Raised when full reassembly reaches validated completion.</summary>
    public event Action AssemblyCompleted;

    public MoldStartMode StartMode
    {
        get => startMode;
        set => startMode = value;
    }

    public bool EnforceStrictStageGating
    {
        get => enforceStrictStageGating;
        set => enforceStrictStageGating = value;
    }

    public bool RequireAllScrewsInsertedBeforeTightening
    {
        get => requireAllScrewsInsertedBeforeTightening;
        set => requireAllScrewsInsertedBeforeTightening = value;
    }

    public AssemblyWorkflowPhase WorkflowPhase => _workflowPhase;

    public MoldAssemblyStage CurrentStage => _currentSnapshot.Stage;

    public MoldAssemblySnapshot CurrentSnapshot => _currentSnapshot;

    public bool IsAssemblyComplete => _currentSnapshot.IsAssemblyComplete;

    public IReadOnlyList<MoldSnapPoint> BaseSnapPoints => baseSnapPoints;
    public IReadOnlyList<MoldSnapPoint> WallSnapPoints => wallSnapPoints;
    public IReadOnlyList<MoldSnapPoint> RodSnapPoints => rodSnapPoints;
    public IReadOnlyList<ScrewSlot> ScrewSlots => screwSlots;
    public ScrewdriverHeadAttachment HeadSocket => headSocket;

    private void Awake()
    {
        RegisterStageGatesOnce();
    }

    private void OnEnable()
    {
        RegisterStageGatesOnce();
        SubscribeEvents(true);
    }

    private void Start()
    {
        if (startMode == MoldStartMode.DismantledAtStart)
        {
            DeployDismantledLayout();
        }
        else
        {
            // Determine initial workflow phase from actual live dismantleable occupancy at startup.
            int dismantleableOccupied = CountDismantleableOccupied(baseSnapPoints)
                                      + CountOccupiedSnapPoints(wallSnapPoints)
                                      + CountOccupiedSnapPoints(rodSnapPoints)
                                      + CountOccupiedScrewSlots(screwSlots);
            _workflowPhase = dismantleableOccupied > 0
                ? AssemblyWorkflowPhase.Dismantling
                : AssemblyWorkflowPhase.Reassembling;

            if (_workflowPhase == AssemblyWorkflowPhase.Dismantling && screwdriver != null)
                screwdriver.SetReverseDirection(true);

            SyncShortWallRemovalLockForPhase();
            EvaluateAssemblyState();
            PublishFeedback(
                AssemblyFeedbackSeverity.Info,
                _workflowPhase == AssemblyWorkflowPhase.Dismantling
                    ? "Dismantling Mode: Unscrew & detach all 6 horizontal tie rods FIRST (small vertical rods are locked until horizontal rods are detached), then remove vertical rods, Side Pieces, and Length Pieces."
                    : GetStageInstructionText(_currentSnapshot));
        }
    }

    private void OnDisable()
    {
        SubscribeEvents(false);
        if (tighteningController != null && tighteningController.ExternalTightenGateValidator == ValidateForwardTighteningStageGate)
            tighteningController.ExternalTightenGateValidator = null;
    }

    /// <summary>
    /// Evaluates ground-truth occupancy, alignment/lock states, and fastener tightening progress across all mold components.
    /// </summary>
    public MoldAssemblySnapshot EvaluateAssemblyState()
    {
        int baseReq = CountValidEntries(baseSnapPoints);
        int baseInst = CountOccupiedSnapPoints(baseSnapPoints);
        int dismantleableBaseReq = CountDismantleableEntries(baseSnapPoints);
        int dismantleableBaseInst = CountDismantleableOccupied(baseSnapPoints);

        int longWallsReq = 0, longWallsInst = 0;
        int shortWallsReq = 0, shortWallsInst = 0;
        for (int i = 0; i < wallSnapPoints.Count; i++)
        {
            MoldSnapPoint wsp = wallSnapPoints[i];
            if (wsp == null) continue;
            if (wsp.AcceptedCategory == MoldPartCategory.ShortWall)
            {
                shortWallsReq++;
                if (wsp.IsOccupied) shortWallsInst++;
            }
            else
            {
                longWallsReq++;
                if (wsp.IsOccupied) longWallsInst++;
            }
        }

        int wallsReq = longWallsReq + shortWallsReq;
        int wallsInst = longWallsInst + shortWallsInst;

        int rodsReq = CountValidEntries(rodSnapPoints);
        int rodsInst = CountOccupiedSnapPoints(rodSnapPoints);

        int screwsReq = CountValidScrewSlots(screwSlots);
        int screwsInserted = 0;
        int screwsTightened = 0;
        int screwsLoosenedOrRemoved = 0;
        float screwProgressSum = 0f;

        for (int i = 0; i < screwSlots.Count; i++)
        {
            ScrewSlot slot = screwSlots[i];
            if (slot == null)
                continue;

            if (slot.IsOccupied && slot.SeatedScrew != null)
            {
                screwsInserted++;
                float prog = slot.SeatedScrew.IsLoose
                    ? 0f
                    : (slot.IsFullyTightened ? 1f : Mathf.Clamp01(slot.SeatedScrew.TightenProgress));
                screwProgressSum += prog;

                if (slot.IsFullyTightened)
                    screwsTightened++;

                if (slot.SeatedScrew.IsLoose)
                    screwsLoosenedOrRemoved++;
            }
            else
            {
                screwsLoosenedOrRemoved++;
            }
        }

        int removableMoldPartsReq = dismantleableBaseReq + wallsReq + rodsReq;
        int removableMoldPartsInst = dismantleableBaseInst + wallsInst + rodsInst;
        int removedMoldParts = Mathf.Max(0, removableMoldPartsReq - removableMoldPartsInst);

        int dismantleablePartsReq = removableMoldPartsReq + screwsReq;
        int totalDismantled = removedMoldParts + screwsLoosenedOrRemoved;

        // Automatically transition from Dismantling -> Reassembling once all dismantleable mold parts (rods, walls, floor plate) have been removed.
        bool transitionedFromDismantle = false;
        if (_workflowPhase == AssemblyWorkflowPhase.Dismantling && removableMoldPartsInst == 0 && removableMoldPartsReq > 0)
        {
            _workflowPhase = AssemblyWorkflowPhase.Reassembling;
            SyncShortWallRemovalLockForPhase();
            transitionedFromDismantle = true;
        }

        MoldAssemblyStage stage;
        float overallProgress;
        bool isComplete = false;

        if (_workflowPhase == AssemblyWorkflowPhase.Dismantling)
        {
            stage = MoldAssemblyStage.DismantleMold;
            float totalDismantleUnits = Mathf.Max(1f, screwsReq + removableMoldPartsReq);
            float loosenedUnits = Mathf.Max(0f, screwsReq - screwProgressSum);
            overallProgress = Mathf.Clamp01((loosenedUnits + removedMoldParts) / totalDismantleUnits);
        }
        else
        {
            bool allBaseDone = baseReq == 0 || baseInst >= baseReq;
            bool allLongWallsDone = allBaseDone && (longWallsReq == 0 || longWallsInst >= longWallsReq);
            bool allShortWallsDone = allLongWallsDone && (shortWallsReq == 0 || shortWallsInst >= shortWallsReq);
            bool allWallsDone = allLongWallsDone && allShortWallsDone && wallsReq > 0;
            bool allRodsDone = allWallsDone && rodsInst >= rodsReq && rodsReq > 0;
            bool allScrewsSeated = allRodsDone && screwsInserted >= screwsReq && screwsReq > 0;
            bool allScrewsTight = allScrewsSeated && screwsTightened >= screwsReq && screwsReq > 0;

            if (!allBaseDone)
                stage = MoldAssemblyStage.BaseInstallation;
            else if (!allLongWallsDone)
                stage = MoldAssemblyStage.LongWallInstallation;
            else if (!allShortWallsDone)
                stage = MoldAssemblyStage.ShortWallInstallation;
            else if (!allRodsDone)
                stage = MoldAssemblyStage.RodInstallation;
            else if (!allScrewsSeated)
                stage = MoldAssemblyStage.ScrewInsertion;
            else if (!allScrewsTight)
                stage = MoldAssemblyStage.ScrewTightening;
            else
            {
                stage = MoldAssemblyStage.Completed;
                isComplete = true;
            }

            float totalAssemblyUnits = Mathf.Max(1f, removableMoldPartsReq + screwsReq);
            float completedUnits = removableMoldPartsInst + screwProgressSum;
            overallProgress = isComplete ? 1f : Mathf.Clamp01(completedUnits / totalAssemblyUnits);
        }

        ScrewdriverHead activeHead = headSocket != null ? headSocket.CurrentHead : null;
        bool hasBuiltInBit = activeHead == null && screwdriver != null && screwdriver.BuiltInDriverBit != null;
        string builtInName = hasBuiltInBit && screwdriver.BuiltInDriverBit.name.IndexOf("Socket", StringComparison.OrdinalIgnoreCase) >= 0
            ? "Impact Hex Socket"
            : "Built-In Driver Bit";

        _currentSnapshot = new MoldAssemblySnapshot
        {
            WorkflowPhase = _workflowPhase,
            Stage = stage,
            BaseInstalled = baseInst,
            BaseRequired = baseReq,
            LongWallsInstalled = longWallsInst,
            LongWallsRequired = longWallsReq,
            ShortWallsInstalled = shortWallsInst,
            ShortWallsRequired = shortWallsReq,
            WallsInstalled = wallsInst,
            WallsRequired = wallsReq,
            RodsInstalled = rodsInst,
            RodsRequired = rodsReq,
            ScrewsInserted = screwsInserted,
            ScrewsRequired = screwsReq,
            ScrewsTightened = screwsTightened,
            ScrewsLoosenedOrRemoved = screwsLoosenedOrRemoved,
            TotalDismantledParts = totalDismantled,
            TotalPartsRequired = dismantleablePartsReq,
            OverallProgressNormalized = overallProgress,
            IsAssemblyComplete = isComplete,
            ActiveBitName = activeHead != null ? activeHead.DisplayName : (hasBuiltInBit ? builtInName : "None (Empty Chuck)"),
            ActiveBitFastenerTag = activeHead != null ? activeHead.FastenerCompatibilityId : (hasBuiltInBit ? "Universal" : "None")
        };

        MoldAssemblyStage prevStage = _lastReportedStage;
        bool stageChanged = prevStage != stage;
        _lastReportedStage = stage;

        if (transitionedFromDismantle && !_suppressFeedbackEvents)
        {
            PublishFeedback(
                AssemblyFeedbackSeverity.Success,
                "Mold dismantled! Now put everything back in order — Step 1: Place both Long Walls onto the Base first (Short Walls restricted).");
        }
        else if (stageChanged && prevStage != (MoldAssemblyStage)(-1) && !_suppressFeedbackEvents)
        {
            if (stage == MoldAssemblyStage.Completed)
            {
                PublishFeedback(
                    AssemblyFeedbackSeverity.Success,
                    $"ASSEMBLY COMPLETE: All 4 walls (Long then Short), {_currentSnapshot.RodsRequired} rods, and {_currentSnapshot.ScrewsRequired} screws are back in their original positions!");
                AssemblyCompleted?.Invoke();
            }
            else if (_workflowPhase == AssemblyWorkflowPhase.Reassembling)
            {
                PublishFeedback(
                    AssemblyFeedbackSeverity.Info,
                    GetStageInstructionText(_currentSnapshot));
            }
        }

        if (stageChanged && prevStage != (MoldAssemblyStage)(-1))
            StageChanged?.Invoke(prevStage, stage);

        StateUpdated?.Invoke(_currentSnapshot);
        return _currentSnapshot;
    }

    /// <summary>
    /// Returns a concise human-readable title for the active stage.
    /// </summary>
    public static string GetStageTitle(MoldAssemblySnapshot s)
    {
        switch (s.Stage)
        {
            case MoldAssemblyStage.DismantleMold:
                if (s.ScrewsInserted > 0 || s.RodsInstalled > 0)
                    return "DISMANTLE: HORIZONTAL RODS FIRST, THEN VERTICAL RODS";
                return "DISMANTLE STEP 3/3: GRAB & REMOVE ALL 4 WALLS";

            case MoldAssemblyStage.BaseInstallation:
                return "REASSEMBLE: PLACE MOLD BASE";
            case MoldAssemblyStage.LongWallInstallation:
                return "REASSEMBLE STEP 1/4: PLACE 2 LONG WALLS FIRST";
            case MoldAssemblyStage.ShortWallInstallation:
                return "REASSEMBLE STEP 2/4: PLACE 2 SHORT END WALLS";
            case MoldAssemblyStage.RodInstallation:
                return "REASSEMBLE STEP 3/4: INSERT CONNECTING RODS";
            case MoldAssemblyStage.ScrewInsertion:
                return "REASSEMBLE STEP 4/4: PLACE & TIGHTEN SCREWS";
            case MoldAssemblyStage.ScrewTightening:
                return "REASSEMBLE STEP 4/4: TIGHTEN REMAINING SCREWS";
            case MoldAssemblyStage.Completed:
                return "WORKFLOW COMPLETE — MOLD RESTORED TO ORIGINAL POSITION";
            default:
                return s.Stage.ToString();
        }
    }

    /// <summary>
    /// Returns guided next-action instructions derived from actual live component states.
    /// </summary>
    public static string GetStageInstructionText(MoldAssemblySnapshot s)
    {
        switch (s.Stage)
        {
            case MoldAssemblyStage.DismantleMold:
                if (s.RodsInstalled > 2)
                    return $"Set screwdriver to ANTI-CLOCKWISE [Press TAB or flip Switch] to loosen nuts to 100% at the rod tips, then grab & remove all 6 horizontal rods (nuts stay attached — {Mathf.Max(0, s.RodsInstalled - 2)} remaining).";
                if (s.ScrewsInserted > 0 || s.RodsInstalled > 0)
                    return $"Horizontal rods detached — small vertical rods UNLOCKED! Loosen vertical nuts to 100% at the rod tips & remove the vertical rods ({s.RodsRequired - s.RodsInstalled}/{s.RodsRequired} rods removed).";
                if (s.ShortWallsInstalled > 0)
                    return $"Remove BOTH Side Pieces (Short End Walls) FIRST ({s.ShortWallsRequired - s.ShortWallsInstalled}/{s.ShortWallsRequired} removed) — Length Pieces are LOCKED!";
                return $"Side Pieces removed — Length Pieces UNLOCKED! Remove both Length Pieces ({s.LongWallsRequired - s.LongWallsInstalled}/{s.LongWallsRequired} removed).";

            case MoldAssemblyStage.BaseInstallation:
                return "Place the Pallet Base and Inner Floor Plate at the mold station.";

            case MoldAssemblyStage.LongWallInstallation:
                return $"Place BOTH Length Pieces (Long Walls) FIRST ({s.LongWallsInstalled}/{s.LongWallsRequired} placed) — Side Pieces are LOCKED until Length Pieces are placed!";

            case MoldAssemblyStage.ShortWallInstallation:
                return $"Length Pieces in place — Side Pieces UNLOCKED! Grab & place BOTH Side Pieces ({s.ShortWallsInstalled}/{s.ShortWallsRequired} placed).";

            case MoldAssemblyStage.RodInstallation:
                return $"All 4 walls locked! Insert all {s.RodsRequired} rods with their attached nuts ({s.RodsInstalled}/{s.RodsRequired} installed).";

            case MoldAssemblyStage.ScrewInsertion:
                return $"Place all {s.ScrewsRequired} nuts ({s.ScrewsInserted}/{s.ScrewsRequired} seated — Red X), switch screwdriver to CLOCKWISE [Press TAB or flip Switch], and tighten until Green Tick!";

            case MoldAssemblyStage.ScrewTightening:
                return $"Switch screwdriver to CLOCKWISE [Press TAB or flip Switch] and tighten each Red-X nut until Green Tick appears ({s.ScrewsTightened}/{s.ScrewsRequired} tight).";

            case MoldAssemblyStage.Completed:
                return $"All {s.TotalPartsRequired} dismantled components restored & torqued (14/14 Green Ticks)!";

            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// Returns true only when all 6 horizontal connecting tie rods (Category == ConnectingRod) are detached from the mold.
    /// Used to lock the 2 small vertical side rods & their nuts until all horizontal rods are removed so the screwdriver has clearance.
    /// </summary>
    public bool AreAllHorizontalRodsDetached(out int remainingHorizontalRods)
    {
        remainingHorizontalRods = 0;
        for (int i = 0; i < rodSnapPoints.Count; i++)
        {
            MoldSnapPoint sp = rodSnapPoints[i];
            if (sp != null && sp.AcceptedCategory == MoldPartCategory.ConnectingRod && sp.IsOccupied)
                remainingHorizontalRods++;
        }
        return remainingHorizontalRods == 0;
    }

    /// <summary>
    /// Manually switches the workflow phase to Reassembling (useful if testing partial dismantle -> reassembly).
    /// </summary>
    [ContextMenu("Switch To Reassembly Phase")]
    public void SwitchToReassemblyPhase()
    {
        _workflowPhase = AssemblyWorkflowPhase.Reassembling;
        if (screwdriver != null)
            screwdriver.SetReverseDirection(false);
        EvaluateAssemblyState();
        PublishFeedback(AssemblyFeedbackSeverity.Info, GetStageInstructionText(_currentSnapshot));
    }

    /// <summary>
    /// Stages all 20 movable mold components in neat organized rows beside the mold for immediate Stage 1 -> 5 reassembly.
    /// </summary>
    [ContextMenu("Stage Dismantled Parts On Floor")]
    public void DeployDismantledLayout()
    {
        _suppressFeedbackEvents = true;
        MoldPart.SuppressStateChangeAudio = true;
        try
        {
            // Loosen all nuts to their rod tips first, then release & stage rods (carrying their captive nuts & washers!) -> Walls -> FloorPlate
            for (int i = 0; i < screwSlots.Count; i++)
            {
                ScrewSlot slot = screwSlots[i];
                if (slot != null && slot.SeatedScrew != null)
                    slot.SeatedScrew.SetTightenProgress(0f);
            }

            int rodIdx = 0;
            for (int i = 0; i < rodSnapPoints.Count; i++)
            {
                MoldSnapPoint sp = rodSnapPoints[i];
                if (sp == null) continue;
                MoldPart part = sp.OccupiedPart != null ? sp.OccupiedPart : sp.AcceptedPart;
                if (part == null) continue;
                part.ReleaseToFreeState();
                PlaceStagedFreePart(part, dismantledStagingOrigin + new Vector3(0.45f, 0.12f, rodIdx * 0.22f));
                ConnectingRod rod = part.GetComponent<ConnectingRod>();
                if (rod != null)
                    rod.SyncCaptiveNutsToRodPose();
                rodIdx++;
            }

            for (int i = wallSnapPoints.Count - 1; i >= 0; i--)
            {
                MoldSnapPoint sp = wallSnapPoints[i];
                if (sp == null) continue;
                MoldPart part = sp.OccupiedPart != null ? sp.OccupiedPart : sp.AcceptedPart;
                if (part == null) continue;
                part.ReleaseToFreeState();
                PlaceStagedFreePart(part, dismantledStagingOrigin + new Vector3(1.15f, 0.35f, (wallSnapPoints.Count - 1 - i) * 0.55f));
            }

            for (int i = baseSnapPoints.Count - 1; i >= 0; i--)
            {
                MoldSnapPoint sp = baseSnapPoints[i];
                if (sp == null || sp.LockPermanentlyOnceInstalled) continue;
                MoldPart part = sp.OccupiedPart != null ? sp.OccupiedPart : sp.AcceptedPart;
                if (part == null) continue;
                part.ReleaseToFreeState();
                PlaceStagedFreePart(part, dismantledStagingOrigin + new Vector3(1.95f, 0.25f, (baseSnapPoints.Count - 1 - i) * 0.85f));
            }

            _workflowPhase = AssemblyWorkflowPhase.Reassembling;
            if (screwdriver != null)
                screwdriver.SetReverseDirection(false);
            SyncShortWallRemovalLockForPhase();
            _lastReportedStage = (MoldAssemblyStage)(-1);
        }
        finally
        {
            MoldPart.SuppressStateChangeAudio = false;
            _suppressFeedbackEvents = false;
        }

        EvaluateAssemblyState();
        PublishFeedback(AssemblyFeedbackSeverity.Info, GetStageInstructionText(_currentSnapshot));
    }

    /// <summary>
    /// Restores all mold components to their exact assembled & tightened poses and resets Dismantling Mode.
    /// </summary>
    [ContextMenu("Reset Fully Assembled Mold")]
    public void ResetToAssembledState()
    {
        _suppressFeedbackEvents = true;
        MoldPart.SuppressStateChangeAudio = true;
        bool prevGate = enforceStrictStageGating;
        enforceStrictStageGating = false;

        try
        {
            Transform moldRoot = transform;
            MoldPart[] allParts = moldRoot != null
                ? moldRoot.GetComponentsInChildren<MoldPart>(includeInactive: true)
                : Array.Empty<MoldPart>();

            for (int i = 0; i < baseSnapPoints.Count; i++)
            {
                MoldSnapPoint sp = baseSnapPoints[i];
                if (sp != null && !sp.IsOccupied && sp.AcceptedPart != null)
                {
                    if (!sp.AcceptedPart.IsFree)
                        sp.AcceptedPart.SetState(MoldPartState.Free, snapToAssembledPose: false);
                    sp.TrySnapPart(sp.AcceptedPart, checkPoseTolerance: false);
                }
            }

            // Snap Long Walls first, then Short Walls so prerequisites are naturally satisfied
            for (int pass = 0; pass < 2; pass++)
            {
                MoldPartCategory passCategory = pass == 0 ? MoldPartCategory.LongWall : MoldPartCategory.ShortWall;
                for (int i = 0; i < wallSnapPoints.Count; i++)
                {
                    MoldSnapPoint sp = wallSnapPoints[i];
                    if (sp == null || sp.IsOccupied || sp.AcceptedCategory != passCategory) continue;

                    if (sp.AcceptedPart != null)
                    {
                        if (!sp.AcceptedPart.IsFree)
                            sp.AcceptedPart.SetState(MoldPartState.Free, snapToAssembledPose: false);
                        if (sp.TrySnapPart(sp.AcceptedPart, checkPoseTolerance: false))
                            continue;
                    }

                    for (int j = 0; j < allParts.Length; j++)
                    {
                        MoldPart candidate = allParts[j];
                        if (candidate != null && candidate.IsFree && sp.TrySnapPart(candidate, checkPoseTolerance: false))
                            break;
                    }
                }
            }

            for (int i = 0; i < rodSnapPoints.Count; i++)
            {
                MoldSnapPoint sp = rodSnapPoints[i];
                if (sp == null || sp.IsOccupied) continue;

                if (sp.AcceptedPart != null)
                {
                    if (!sp.AcceptedPart.IsFree)
                        sp.AcceptedPart.SetState(MoldPartState.Free, snapToAssembledPose: false);
                    if (sp.TrySnapPart(sp.AcceptedPart, checkPoseTolerance: false))
                        continue;
                }

                for (int j = 0; j < allParts.Length; j++)
                {
                    MoldPart candidate = allParts[j];
                    if (candidate != null && candidate.IsFree && sp.TrySnapPart(candidate, checkPoseTolerance: false))
                        break;
                }
            }

            // Collect all ScrewInteractions under the mold container to re-seat any free fasteners.
            ScrewInteraction[] allScrews = moldRoot != null
                ? moldRoot.GetComponentsInChildren<ScrewInteraction>(includeInactive: true)
                : Array.Empty<ScrewInteraction>();

            for (int i = 0; i < screwSlots.Count; i++)
            {
                ScrewSlot slot = screwSlots[i];
                if (slot == null) continue;

                if (!slot.IsOccupied)
                {
                    if (slot.InitialSeatedScrew != null && !slot.InitialSeatedScrew.IsSeated)
                    {
                        if (slot.InitialSeatedScrew.IsAttachedToRemovedRod)
                        {
                            slot.InitialSeatedScrew.DetachCaptiveAndSeatIntoSlot(slot);
                        }
                        else
                        {
                            if (slot.InitialSeatedScrew.Part != null && !slot.InitialSeatedScrew.Part.IsFree)
                                slot.InitialSeatedScrew.Part.SetState(MoldPartState.Free, snapToAssembledPose: false);
                            slot.TryInsertScrewLoose(slot.InitialSeatedScrew, checkPoseTolerance: false);
                        }
                    }
                    else
                    {
                        for (int j = 0; j < allScrews.Length; j++)
                        {
                            ScrewInteraction candidate = allScrews[j];
                            if (candidate == null || candidate.IsSeated)
                                continue;

                            if (candidate.IsAttachedToRemovedRod)
                            {
                                candidate.DetachCaptiveAndSeatIntoSlot(slot);
                                if (slot.IsOccupied)
                                    break;
                            }
                            else if (slot.TryInsertScrewLoose(candidate, checkPoseTolerance: false))
                            {
                                break;
                            }
                        }
                    }
                }

                if (slot.SeatedScrew != null)
                    slot.SeatedScrew.SetTightenProgress(1f);
            }

            _workflowPhase = AssemblyWorkflowPhase.Dismantling;
            if (screwdriver != null)
                screwdriver.SetReverseDirection(true);
            SyncShortWallRemovalLockForPhase();
            _lastReportedStage = (MoldAssemblyStage)(-1);
        }
        finally
        {
            MoldPart.SuppressStateChangeAudio = false;
            enforceStrictStageGating = prevGate;
            _suppressFeedbackEvents = false;
        }

        EvaluateAssemblyState();
        PublishFeedback(AssemblyFeedbackSeverity.Info, "Mold reset to fully assembled state. Loosen fasteners with the screwdriver to begin dismantling.");
    }

    /// <summary>
    /// Populates serialized lists from MoldCast & ElectricScrewdriver_V3 (used by scene setup).
    /// </summary>
    public void ConfigureReferences(
        List<MoldSnapPoint> basePoints,
        List<MoldSnapPoint> wallPoints,
        List<MoldSnapPoint> rodPoints,
        List<ScrewSlot> fastenerSlots,
        XRElectricScrewdriver motor,
        ScrewdriverHeadAttachment socket,
        ScrewTighteningController tightener)
    {
        baseSnapPoints = basePoints ?? new List<MoldSnapPoint>();
        wallSnapPoints = wallPoints ?? new List<MoldSnapPoint>();
        rodSnapPoints = rodPoints ?? new List<MoldSnapPoint>();
        screwSlots = fastenerSlots ?? new List<ScrewSlot>();
        screwdriver = motor;
        headSocket = socket;
        tighteningController = tightener;
    }

    private void RegisterStageGatesOnce()
    {
        if (tighteningController != null)
            tighteningController.ExternalTightenGateValidator = ValidateForwardTighteningStageGate;

        if (_stageGatesRegistered)
            return;

        _stageGatesRegistered = true;

        for (int i = 0; i < wallSnapPoints.Count; i++)
        {
            MoldSnapPoint wsp = wallSnapPoints[i];
            if (wsp == null) continue;

            if (wsp.AcceptedCategory == MoldPartCategory.ShortWall)
                wsp.AddExternalInstallPrerequisite(ValidateShortWallStagePrerequisite);
            else
                wsp.AddExternalInstallPrerequisite(ValidateLongWallStagePrerequisite);
        }

        for (int i = 0; i < rodSnapPoints.Count; i++)
        {
            MoldSnapPoint rsp = rodSnapPoints[i];
            if (rsp == null) continue;

            rsp.AddExternalInstallPrerequisite(ValidateRodStagePrerequisite);

            // Small vertical side rods can ONLY be removed after ALL horizontal connecting rods are detached from the mold!
            if (rsp.AcceptedCategory == MoldPartCategory.VerticalBolt)
            {
                rsp.AddExternalRemovalBlocker(() => !AreAllHorizontalRodsDetached(out _));
            }
        }

        for (int i = 0; i < screwSlots.Count; i++)
        {
            if (screwSlots[i] != null)
                screwSlots[i].AddExternalInsertPrerequisite(ValidateScrewInsertionStagePrerequisite);
        }
    }

    private void SyncShortWallRemovalLockForPhase()
    {
        // Enforce strict sequence lock in BOTH Dismantling and Reassembling stages:
        // - Dismantling: Side Pieces (ShortWalls) MUST be removed first; while occupied, they lock Length Pieces (LongWalls).
        // - Reassembling: Length Pieces (LongWalls) MUST be placed first; once Side Pieces are placed, they lock Length Pieces.
        for (int i = 0; i < wallSnapPoints.Count; i++)
        {
            MoldSnapPoint wsp = wallSnapPoints[i];
            if (wsp != null && wsp.AcceptedCategory == MoldPartCategory.ShortWall)
                wsp.BlockPrerequisiteRemovalWhenOccupied = true;
        }
        MoldSnapPoint.RefreshAllActiveLocks();
    }

    private string ValidateLongWallStagePrerequisite()
    {
        if (!enforceStrictStageGating)
            return null;

        int req = CountValidEntries(baseSnapPoints);
        int inst = CountOccupiedSnapPoints(baseSnapPoints);
        if (inst < req)
            return $"Stage Gate: Place Base components ({inst}/{req} installed) before placing Long Walls.";

        return null;
    }

    private string ValidateShortWallStagePrerequisite()
    {
        string baseErr = ValidateLongWallStagePrerequisite();
        if (!string.IsNullOrEmpty(baseErr))
            return baseErr;

        // Strictly enforce Long Walls FIRST, Short Walls SECOND (reverse order restricted!)
        int longReq = 0, longInst = 0;
        for (int i = 0; i < wallSnapPoints.Count; i++)
        {
            MoldSnapPoint wsp = wallSnapPoints[i];
            if (wsp != null && wsp.AcceptedCategory == MoldPartCategory.LongWall)
            {
                longReq++;
                if (wsp.IsOccupied) longInst++;
            }
        }

        if (longInst < longReq)
            return $"Restricted Order: Place both Long Walls first ({longInst}/{longReq} placed) before placing the Short Walls!";

        return null;
    }

    private string ValidateRodStagePrerequisite()
    {
        if (!enforceStrictStageGating)
            return null;

        string shortWallErr = ValidateShortWallStagePrerequisite();
        if (!string.IsNullOrEmpty(shortWallErr))
            return shortWallErr;

        int req = CountValidEntries(wallSnapPoints);
        int inst = CountOccupiedSnapPoints(wallSnapPoints);
        if (inst < req)
            return $"Stage Gate: Place all {req} mold walls — 2 Long then 2 Short ({inst}/{req} placed) — before inserting connecting rods.";

        return null;
    }

    private string ValidateScrewInsertionStagePrerequisite()
    {
        if (!enforceStrictStageGating)
            return null;

        // Require Base + both Long Walls + both Short Walls before placing nuts; individual host rod check is enforced by ScrewSlot.HostRodSnapPoint.
        string rodErr = ValidateRodStagePrerequisite();
        if (!string.IsNullOrEmpty(rodErr))
            return rodErr;

        int inst = CountOccupiedSnapPoints(rodSnapPoints);
        if (inst == 0)
            return "Stage Gate: Install connecting rods or side bolts before placing nuts.";

        return null;
    }

    private string ValidateForwardTighteningStageGate(ScrewInteraction targetScrew)
    {
        if (!enforceStrictStageGating)
            return null;

        // Always require base, walls, and rods before forward tightening
        string screwInsertErr = ValidateScrewInsertionStagePrerequisite();
        if (!string.IsNullOrEmpty(screwInsertErr))
            return screwInsertErr;

        if (!requireAllScrewsInsertedBeforeTightening)
            return null;

        int req = CountValidScrewSlots(screwSlots);
        int occ = CountOccupiedScrewSlots(screwSlots);
        if (occ < req)
            return $"Stage Gate: Place all {req} screws in their loose threaded positions ({occ}/{req} seated) before tightening.";

        return null;
    }

    private void SubscribeEvents(bool subscribe)
    {
        SubscribeSnapList(baseSnapPoints, subscribe);
        SubscribeSnapList(wallSnapPoints, subscribe);
        SubscribeSnapList(rodSnapPoints, subscribe);

        for (int i = 0; i < screwSlots.Count; i++)
        {
            ScrewSlot slot = screwSlots[i];
            if (slot == null) continue;

            slot.ScrewInsertedLoose -= OnScrewInsertedLoose;
            slot.ScrewRemoved -= OnScrewRemoved;

            if (subscribe)
            {
                slot.ScrewInsertedLoose += OnScrewInsertedLoose;
                slot.ScrewRemoved += OnScrewRemoved;
            }

            if (slot.SeatedScrew != null)
            {
                slot.SeatedScrew.TightenProgressChanged -= OnScrewTightenProgressChanged;
                if (subscribe)
                    slot.SeatedScrew.TightenProgressChanged += OnScrewTightenProgressChanged;
            }
        }

        MoldSnapPoint.GlobalPlacementRejected -= OnGlobalPlacementRejected;
        ScrewSlot.GlobalInsertionRejected -= OnGlobalScrewInsertionRejected;
        if (subscribe)
        {
            MoldSnapPoint.GlobalPlacementRejected += OnGlobalPlacementRejected;
            ScrewSlot.GlobalInsertionRejected += OnGlobalScrewInsertionRejected;
        }

        if (headSocket != null)
        {
            headSocket.HeadAttached -= OnHeadAttached;
            headSocket.HeadDetached -= OnHeadDetached;
            headSocket.HeadRejected -= OnHeadRejected;
            if (subscribe)
            {
                headSocket.HeadAttached += OnHeadAttached;
                headSocket.HeadDetached += OnHeadDetached;
                headSocket.HeadRejected += OnHeadRejected;
            }
        }

        if (screwdriver != null)
        {
            screwdriver.DirectionChanged -= OnScrewdriverDirectionChanged;
            if (subscribe)
                screwdriver.DirectionChanged += OnScrewdriverDirectionChanged;
        }

        if (tighteningController != null)
        {
            tighteningController.ScrewFullyTightened -= OnScrewFullyTightened;
            tighteningController.ScrewLoosenedToRemovable -= OnScrewLoosenedToRemovable;
            tighteningController.IncompatibleToolDetected -= OnIncompatibleToolDetected;
            tighteningController.MisalignedToolDetected -= OnMisalignedToolDetected;
            tighteningController.TighteningStageBlocked -= OnTighteningStageBlocked;

            if (subscribe)
            {
                tighteningController.ScrewFullyTightened += OnScrewFullyTightened;
                tighteningController.ScrewLoosenedToRemovable += OnScrewLoosenedToRemovable;
                tighteningController.IncompatibleToolDetected += OnIncompatibleToolDetected;
                tighteningController.MisalignedToolDetected += OnMisalignedToolDetected;
                tighteningController.TighteningStageBlocked += OnTighteningStageBlocked;
            }
        }
    }

    private void SubscribeSnapList(List<MoldSnapPoint> list, bool subscribe)
    {
        for (int i = 0; i < list.Count; i++)
        {
            MoldSnapPoint sp = list[i];
            if (sp == null) continue;

            sp.PartSnapped -= OnPartSnapped;
            sp.PartRemoved -= OnPartRemoved;

            if (subscribe)
            {
                sp.PartSnapped += OnPartSnapped;
                sp.PartRemoved += OnPartRemoved;
            }
        }
    }

    private void OnPartSnapped(MoldSnapPoint sp, MoldPart part)
    {
        MoldSnapPoint.RefreshAllActiveLocks();
        EvaluateAssemblyState();
        if (!_suppressFeedbackEvents)
            PublishFeedback(AssemblyFeedbackSeverity.Success, $"Installed '{part.DisplayName}' at '{sp.SnapPointId}'.");
    }

    private void OnPartRemoved(MoldSnapPoint sp, MoldPart part)
    {
        MoldSnapPoint.RefreshAllActiveLocks();
        MoldAssemblyStage prevStage = _currentSnapshot.Stage;
        EvaluateAssemblyState();
        if (!_suppressFeedbackEvents && _currentSnapshot.Stage == prevStage)
        {
            if (_workflowPhase == AssemblyWorkflowPhase.Dismantling &&
                part != null &&
                part.Category == MoldPartCategory.ConnectingRod &&
                AreAllHorizontalRodsDetached(out _))
            {
                PublishFeedback(
                    AssemblyFeedbackSeverity.Success,
                    $"Removed '{part.DisplayName}' — all 6 horizontal rods detached! Small vertical side rods are now UNLOCKED.");
            }
            else
            {
                PublishFeedback(AssemblyFeedbackSeverity.Info, $"Removed '{part.DisplayName}'.");
            }
        }
    }

    private void OnGlobalPlacementRejected(MoldSnapPoint sp, MoldPart part, string reason)
    {
        if (!_suppressFeedbackEvents)
            PublishFeedback(AssemblyFeedbackSeverity.Error, $"Incorrect Placement: {reason}");
    }

    private void OnScrewInsertedLoose(ScrewSlot slot, ScrewInteraction screw)
    {
        if (screw != null)
        {
            screw.TightenProgressChanged -= OnScrewTightenProgressChanged;
            screw.TightenProgressChanged += OnScrewTightenProgressChanged;
        }

        EvaluateAssemblyState();
        if (!_suppressFeedbackEvents)
        {
            string switchHint = screwdriver != null && screwdriver.ReverseDirection
                ? "press TAB (or flip Switch) for CLOCKWISE to tighten!"
                : "touch with CLOCKWISE screwdriver to tighten!";
            PublishFeedback(
                AssemblyFeedbackSeverity.Success,
                $"Placed '{screw.Part.DisplayName}' in loose starting position ({_currentSnapshot.ScrewsInserted}/{_currentSnapshot.ScrewsRequired} seated) — {switchHint}");
        }
    }

    private void OnScrewdriverDirectionChanged(bool isReverseLoosen)
    {
        if (_suppressFeedbackEvents || !Application.isPlaying || Time.timeSinceLevelLoad < 0.05f)
            return;

        PublishFeedback(
            AssemblyFeedbackSeverity.Info,
            isReverseLoosen
                ? "Screwdriver Switch [TAB]: ANTI-CLOCKWISE (CCW) — Loosen Screws"
                : "Screwdriver Switch [TAB]: CLOCKWISE (CW) — Tighten Screws");
    }

    private void OnScrewRemoved(ScrewSlot slot, ScrewInteraction screw)
    {
        if (screw != null)
            screw.TightenProgressChanged -= OnScrewTightenProgressChanged;

        EvaluateAssemblyState();
        if (!_suppressFeedbackEvents && (screw == null || !screw.IsAttachedToRemovedRod))
            PublishFeedback(AssemblyFeedbackSeverity.Info, $"Removed '{screw.Part.DisplayName}' from '{slot.SlotId}'.");
    }

    private void OnScrewTightenProgressChanged(ScrewInteraction screw, float progress, bool isTightened)
    {
        EvaluateAssemblyState();
    }

    private void OnGlobalScrewInsertionRejected(ScrewSlot slot, ScrewInteraction screw, string reason)
    {
        if (!_suppressFeedbackEvents)
            PublishFeedback(AssemblyFeedbackSeverity.Error, $"Fastener Rejected: {reason}");
    }

    private void OnHeadAttached(ScrewdriverHead head)
    {
        EvaluateAssemblyState();
        if (!_suppressFeedbackEvents && head != null)
        {
            PublishFeedback(
                AssemblyFeedbackSeverity.Success,
                $"Installed bit '{head.DisplayName}' (Fastener Type: {head.FastenerCompatibilityId}).");
        }
    }

    private void OnHeadDetached(ScrewdriverHead head)
    {
        EvaluateAssemblyState();
        if (!_suppressFeedbackEvents && head != null)
            PublishFeedback(AssemblyFeedbackSeverity.Info, $"Detached bit '{head.DisplayName}' from screwdriver chuck.");
    }

    private void OnHeadRejected(ScrewdriverHead head, string reason)
    {
        if (!_suppressFeedbackEvents)
            PublishFeedback(AssemblyFeedbackSeverity.Error, $"Bit Attachment Rejected: {reason}");
    }

    private void OnScrewFullyTightened(ScrewInteraction screw)
    {
        EvaluateAssemblyState();
        if (!_suppressFeedbackEvents && !_currentSnapshot.IsAssemblyComplete)
        {
            PublishFeedback(
                AssemblyFeedbackSeverity.Success,
                $"Tightened '{screw.Part.DisplayName}' ({_currentSnapshot.ScrewsTightened}/{_currentSnapshot.ScrewsRequired} fasteners torqued).");
        }
    }

    private void OnScrewLoosenedToRemovable(ScrewInteraction screw)
    {
        EvaluateAssemblyState();
        if (!_suppressFeedbackEvents && screw != null)
        {
            MoldSnapPoint rodSp = screw.CurrentSlot != null ? screw.CurrentSlot.HostRodSnapPoint : null;
            bool rodUnlocked = rodSp != null && rodSp.OccupiedPart != null && !rodSp.HasActiveDependentsBlockingRemoval();
            if (rodUnlocked)
            {
                PublishFeedback(
                    AssemblyFeedbackSeverity.Success,
                    $"Loosened '{screw.Part.DisplayName}' onto rod tip — '{rodSp.OccupiedPart.DisplayName}' is now UNLOCKED to grab & remove!");
            }
            else
            {
                PublishFeedback(
                    AssemblyFeedbackSeverity.Success,
                    $"Loosened '{screw.Part.DisplayName}' onto rod tip — loosen the other end's nut to unlock the rod.");
            }
        }
    }

    private void OnIncompatibleToolDetected(ScrewdriverHead head, ScrewInteraction screw, string reason)
    {
        if (!_suppressFeedbackEvents)
            PublishFeedback(AssemblyFeedbackSeverity.Error, $"Incompatible Tool: {reason}");
    }

    private void OnMisalignedToolDetected(ScrewInteraction screw, float angleDeg)
    {
        if (!_suppressFeedbackEvents)
        {
            PublishFeedback(
                AssemblyFeedbackSeverity.Warning,
                $"Misaligned Screwdriver ({angleDeg:F0}°): Align bit coaxially with '{screw.Part.DisplayName}' threads.");
        }
    }

    private void OnTighteningStageBlocked(ScrewInteraction screw, string reason)
    {
        if (!_suppressFeedbackEvents)
            PublishFeedback(AssemblyFeedbackSeverity.Warning, reason);
    }

    private void PublishFeedback(AssemblyFeedbackSeverity severity, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        FeedbackPublished?.Invoke(severity, message);
    }

    private static void PlaceStagedFreePart(MoldPart part, Vector3 targetCenterWorldPos)
    {
        if (part == null) return;
        Vector3 currentCenter = MoldSnapPoint.GetPartWorldGeometricCenter(part);
        Vector3 delta = targetCenterWorldPos - currentCenter;
        part.transform.position += delta;
        if (part.Body != null && !part.Body.isKinematic)
        {
            part.Body.linearVelocity = Vector3.zero;
            part.Body.angularVelocity = Vector3.zero;
        }
    }

    private static int CountValidEntries(List<MoldSnapPoint> points)
    {
        int count = 0;
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i] != null)
                count++;
        }
        return count;
    }

    private static int CountDismantleableEntries(List<MoldSnapPoint> points)
    {
        int count = 0;
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i] != null && !points[i].LockPermanentlyOnceInstalled)
                count++;
        }
        return count;
    }

    private static int CountOccupiedSnapPoints(List<MoldSnapPoint> points)
    {
        int count = 0;
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i] != null && points[i].IsOccupied)
                count++;
        }
        return count;
    }

    private static int CountDismantleableOccupied(List<MoldSnapPoint> points)
    {
        int count = 0;
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i] != null && !points[i].LockPermanentlyOnceInstalled && points[i].IsOccupied)
                count++;
        }
        return count;
    }

    private static int CountValidScrewSlots(List<ScrewSlot> slots)
    {
        int count = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i] != null)
                count++;
        }
        return count;
    }

    private static int CountOccupiedScrewSlots(List<ScrewSlot> slots)
    {
        int count = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i] != null && slots[i].IsOccupied)
                count++;
        }
        return count;
    }
}
