using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Represents a threaded fastening location on an installed connecting rod or vertical bolt.
/// Enforces rod-stage prerequisites, validates fastener type and bore alignment, seats inserted
/// fasteners into a visibly offset loose starting position, and locks the underlying rod while occupied.
/// </summary>
[DisallowMultipleComponent]
public class ScrewSlot : MonoBehaviour
{
    private static readonly List<ScrewSlot> ActiveScrewSlots = new List<ScrewSlot>();

    [Header("Slot Identity & Compatibility")]
    [Tooltip("Unique identifier for this threaded location (e.g., ThreadSlot_Rod_0 .. 5, ThreadSlot_Vertical).")]
    [SerializeField] private string slotId;

    [Tooltip("Fastener type identifier accepted at this threaded location (e.g., RodHexNut_M24, VerticalHexNut_M16).")]
    [SerializeField] private string acceptedFastenerType = "RodHexNut_M24";

    [Tooltip("ScrewdriverHead.FastenerCompatibilityId required to tighten/loosen a fastener in this slot.")]
    [SerializeField] private string requiredBitCompatibilityId = "HexNut_Rod";

    [Header("Rod Prerequisites")]
    [Tooltip("Specific rod/bolt snap point whose protruding threads host this slot.")]
    [SerializeField] private MoldSnapPoint hostRodSnapPoint;

    [Tooltip("All rod snap points that must be Installed before fastener insertion is enabled.")]
    [SerializeField] private List<MoldSnapPoint> requiredRodSnapPoints = new List<MoldSnapPoint>();

    [Header("Thread Geometry & Tolerances")]
    [SerializeField] private Transform moldContainer;
    [SerializeField] private Vector3 tightenedLocalPosition;
    [SerializeField] private Quaternion tightenedLocalRotation = Quaternion.identity;
    [SerializeField] private Vector3 localColliderCenterOffset = Vector3.zero;

    [Tooltip("Local axis on the canonical fastener rotation pointing INWARD (tightening travel direction).")]
    [SerializeField] private Vector3 localTightenTravelAxis = Vector3.forward;

    [Tooltip("World-space axial distance (meters) between the Loose Starting Position (0) and Fully Tightened Position (1).")]
    [SerializeField] private float axialTravelDistance = 0.062f;

    [Tooltip("Maximum world-space distance (meters) from the thread entry/loose point allowed when inserting a fastener.")]
    [SerializeField] private float insertionPositionTolerance = 0.24f;

    [Tooltip("Maximum bore-axis angle error (degrees) allowed when aligning a fastener with the bolt threads (90 = accept any approach angle in XR Simulator and auto-align).")]
    [Range(5f, 180f)]
    [SerializeField] private float boreAlignmentToleranceDegrees = 90f;

    [Tooltip("Fastener initially seated in this slot on scene load (when starting assembled).")]
    [SerializeField] private ScrewInteraction initialSeatedScrew;

    private readonly List<Func<string>> _externalInsertPrerequisites = new List<Func<string>>();
    private Func<bool> _rodBlockerDelegate;
    private ScrewInteraction _seatedScrew;
    private float _lastRejectLogTime = -10f;

    /// <summary>Raised when a fastener is inserted into this slot's loose starting position.</summary>
    public event Action<ScrewSlot, ScrewInteraction> ScrewInsertedLoose;

    /// <summary>Raised when a fastener is removed from this slot.</summary>
    public event Action<ScrewSlot, ScrewInteraction> ScrewRemoved;

    /// <summary>Raised when an invalid fastener or out-of-stage insertion attempt is rejected.</summary>
    public event Action<ScrewSlot, ScrewInteraction, string> InsertionRejected;

    public static event Action<ScrewSlot, ScrewInteraction, string> GlobalInsertionRejected;

    public static IReadOnlyList<ScrewSlot> AllActive => ActiveScrewSlots;

    public string SlotId => string.IsNullOrWhiteSpace(slotId) ? name : slotId;

    public string AcceptedFastenerType => acceptedFastenerType;

    public string RequiredBitCompatibilityId => requiredBitCompatibilityId;

    public MoldSnapPoint HostRodSnapPoint => hostRodSnapPoint;

    public float AxialTravelDistance => axialTravelDistance;

    public ScrewInteraction SeatedScrew => _seatedScrew;

    public ScrewInteraction InitialSeatedScrew => initialSeatedScrew;

    public bool IsOccupied => _seatedScrew != null && !_seatedScrew.Part.IsFree;

    /// <summary>
    /// True only while a seated nut is threaded inward beyond its loose rod-tip threshold.
    /// Once loosened onto the tip of the rod (IsLoose == true), the host rod unlocks so it can be removed like before.
    /// </summary>
    public bool IsBlockingHostRodRemoval => IsOccupied && _seatedScrew != null && !_seatedScrew.IsLoose;

    public bool IsFullyTightened => IsOccupied && _seatedScrew.IsTightened;

    public bool IsVerticalSlot => Mathf.Abs(WorldTightenDirection.y) > 0.7f;

    public float WasherThicknessWorld =>
        IsVerticalSlot ? NutWasher.VerticalWasherThickness : NutWasher.HorizontalWasherThickness;

    /// <summary>Original wall-flush fastener pivot position (before shifting outward by washer thickness).</summary>
    public Vector3 BaseTightenedWorldPosition =>
        moldContainer != null ? moldContainer.TransformPoint(tightenedLocalPosition) : transform.position;

    public Vector3 BaseTightenedWorldGeometricCenter
    {
        get
        {
            Vector3 worldScale = moldContainer != null ? moldContainer.lossyScale : Vector3.one;
            Vector3 scaledOffset = Vector3.Scale(localColliderCenterOffset, worldScale);
            return BaseTightenedWorldPosition + (CanonicalWorldRotation * scaledOffset);
        }
    }

    public float NutHalfThicknessWorld
    {
        get
        {
            Vector3 localAxis = localTightenTravelAxis.sqrMagnitude > Mathf.Epsilon
                ? localTightenTravelAxis.normalized
                : Vector3.forward;
            Vector3 worldScale = moldContainer != null ? moldContainer.lossyScale : Vector3.one;
            float scaleAlongAxis = Mathf.Abs(Vector3.Dot(worldScale, localAxis));
            if (scaleAlongAxis < 1e-4f)
                scaleAlongAxis = Mathf.Abs(worldScale.x);

            float halfThick = Mathf.Abs(Vector3.Dot(localColliderCenterOffset, localAxis)) * scaleAlongAxis;
            if (halfThick < 0.002f)
                halfThick = IsVerticalSlot ? 0.01165f : 0.01683f;
            return halfThick;
        }
    }

    /// <summary>World point on the mold wall / flange clamping face where the steel washer sits around the rod.</summary>
    public Vector3 WallBearingWorldPosition =>
        BaseTightenedWorldGeometricCenter + WorldTightenDirection * NutHalfThicknessWorld;

    /// <summary>World geometric center of the 3D steel flat washer seated behind this nut.</summary>
    public Vector3 WasherWorldCenter =>
        WallBearingWorldPosition + WorldLoosenDirection * (WasherThicknessWorld * 0.5f);

    /// <summary>Fully tightened nut stop (shifted outward by WasherThicknessWorld so the nut clamps flush against the washer).</summary>
    public Vector3 TightenedWorldPosition =>
        BaseTightenedWorldPosition + WorldLoosenDirection * WasherThicknessWorld;

    public Quaternion CanonicalWorldRotation =>
        moldContainer != null ? moldContainer.rotation * tightenedLocalRotation : transform.rotation;

    /// <summary>Unit world vector pointing INWARD along the bolt threads (tightening direction).</summary>
    public Vector3 WorldTightenDirection =>
        (CanonicalWorldRotation * (localTightenTravelAxis.sqrMagnitude > Mathf.Epsilon ? localTightenTravelAxis.normalized : Vector3.forward)).normalized;

    /// <summary>Unit world vector pointing OUTWARD toward the bolt tip (loosening direction).</summary>
    public Vector3 WorldLoosenDirection => -WorldTightenDirection;

    /// <summary>Effective axial travel from TightenedWorldPosition (1) out to the exact tip of the rod (0).</summary>
    public float EffectiveTipTravelDistance => ComputeEffectiveTravelToRodTip();

    /// <summary>World position where a completely loosened nut (progress = 0) stays seated right on the tip of the rod.</summary>
    public Vector3 LooseWorldPosition => TightenedWorldPosition + WorldLoosenDirection * EffectiveTipTravelDistance;

    public Vector3 LooseWorldGeometricCenter
    {
        get
        {
            Vector3 worldScale = moldContainer != null ? moldContainer.lossyScale : Vector3.one;
            Vector3 scaledOffset = Vector3.Scale(localColliderCenterOffset, worldScale);
            return LooseWorldPosition + (CanonicalWorldRotation * scaledOffset);
        }
    }

    public Vector3 TightenedWorldGeometricCenter
    {
        get
        {
            Vector3 worldScale = moldContainer != null ? moldContainer.lossyScale : Vector3.one;
            Vector3 scaledOffset = Vector3.Scale(localColliderCenterOffset, worldScale);
            return TightenedWorldPosition + (CanonicalWorldRotation * scaledOffset);
        }
    }

    private float ComputeEffectiveTravelToRodTip()
    {
        if (IsVerticalSlot)
        {
            // VerticalScrew protrudes ~37.9 mm above the side flange bearing face:
            // 3.8 mm washer + 23.3 mm nut + 10.5 mm travel = 37.6 mm (flush on top tip of vertical rod).
            return 0.0105f;
        }

        if (hostRodSnapPoint != null)
        {
            bool isCurrentlyOccupied = hostRodSnapPoint.OccupiedPart != null;
            MoldPart rodPart = isCurrentlyOccupied
                ? hostRodSnapPoint.OccupiedPart
                : hostRodSnapPoint.AcceptedPart;

            if (rodPart != null)
            {
                Vector3 rodInstalledPos = isCurrentlyOccupied ? rodPart.transform.position : hostRodSnapPoint.TargetWorldPosition;
                Quaternion rodInstalledRot = isCurrentlyOccupied ? rodPart.transform.rotation : hostRodSnapPoint.TargetWorldRotation;
                Vector3 rodWorldScale = rodPart.transform.lossyScale;

                Vector3 loosenDir = WorldLoosenDirection;
                Vector3 wallPos = WallBearingWorldPosition;
                float maxDistFromWall = -1f;

                MeshFilter[] mfs = rodPart.GetComponentsInChildren<MeshFilter>(includeInactive: true);
                for (int m = 0; m < mfs.Length; m++)
                {
                    MeshFilter mf = mfs[m];
                    if (mf == null || mf.sharedMesh == null)
                        continue;

                    Matrix4x4 childToRod = rodPart.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                    Bounds b = mf.sharedMesh.bounds;
                    Vector3 c = b.center;
                    Vector3 e = b.extents;

                    for (int ix = -1; ix <= 1; ix += 2)
                    {
                        for (int iy = -1; iy <= 1; iy += 2)
                        {
                            for (int iz = -1; iz <= 1; iz += 2)
                            {
                                Vector3 localCorner = childToRod.MultiplyPoint3x4(
                                    new Vector3(c.x + ix * e.x, c.y + iy * e.y, c.z + iz * e.z));
                                Vector3 worldCorner = rodInstalledPos + rodInstalledRot * Vector3.Scale(localCorner, rodWorldScale);
                                float dist = Vector3.Dot(worldCorner - wallPos, loosenDir);
                                if (dist > maxDistFromWall)
                                    maxDistFromWall = dist;
                            }
                        }
                    }
                }

                if (maxDistFromWall > 0.025f)
                {
                    // Seat loosened nut right on the tip of the rod (outer face 0.5 mm inside chamfered rod end).
                    float nutFullThickness = NutHalfThicknessWorld * 2f;
                    float travelToTip = (maxDistFromWall - 0.0005f) - nutFullThickness - WasherThicknessWorld;
                    return Mathf.Clamp(travelToTip, 0.010f, 0.030f);
                }
            }
        }

        return Mathf.Clamp(axialTravelDistance, 0.010f, 0.015f);
    }

    private void Awake()
    {
        RegisterRodRetentionBlocker();
        SyncInitialSeatedScrew();
    }

    private void OnEnable()
    {
        if (!ActiveScrewSlots.Contains(this))
            ActiveScrewSlots.Add(this);

        RegisterRodRetentionBlocker();
    }

    private void Start()
    {
        RegisterRodRetentionBlocker();
        SyncInitialSeatedScrew();
        RegisterCaptiveNutWithHostRod();
        RefreshHostRodRetention();
    }

    private void OnDisable()
    {
        ActiveScrewSlots.Remove(this);
        if (hostRodSnapPoint != null)
            hostRodSnapPoint.PartRemoved -= OnHostRodRemoved;
    }

    /// <summary>
    /// Configures this threaded slot from an assembled fastener's tightened reference pose.
    /// </summary>
    public void ConfigureFromAssembledFastener(
        string id,
        ScrewInteraction referenceScrew,
        string fastenerTypeTag,
        string bitCompatibilityTag,
        MoldSnapPoint hostRod,
        List<MoldSnapPoint> allRequiredRods,
        float travelMeters,
        bool startSeated = true)
    {
        if (referenceScrew == null)
            return;

        MoldPart part = referenceScrew.Part;
        slotId = id;
        acceptedFastenerType = fastenerTypeTag;
        requiredBitCompatibilityId = bitCompatibilityTag;
        hostRodSnapPoint = hostRod;

        requiredRodSnapPoints.Clear();
        if (allRequiredRods != null)
        {
            for (int i = 0; i < allRequiredRods.Count; i++)
            {
                if (allRequiredRods[i] != null)
                    requiredRodSnapPoints.Add(allRequiredRods[i]);
            }
        }

        moldContainer = part.MoldContainer != null ? part.MoldContainer : part.transform.parent;
        tightenedLocalPosition = part.AssembledLocalPosition;
        tightenedLocalRotation = part.AssembledLocalRotation;

        BoxCollider box = part.GetComponent<BoxCollider>();
        localColliderCenterOffset = box != null ? box.center : Vector3.zero;
        localTightenTravelAxis = referenceScrew.LocalRotationAxis;
        axialTravelDistance = travelMeters;
        initialSeatedScrew = startSeated ? referenceScrew : null;

        transform.SetPositionAndRotation(TightenedWorldGeometricCenter, CanonicalWorldRotation);
    }

    /// <summary>
    /// Evaluates world position for any normalized tightening progress (0 = LooseWorldPosition, 1 = TightenedWorldPosition).
    /// </summary>
    public Vector3 EvaluateWorldPosition(float normalizedProgress)
    {
        return Vector3.Lerp(LooseWorldPosition, TightenedWorldPosition, Mathf.Clamp01(normalizedProgress));
    }

    /// <summary>
    /// Registers an external stage prerequisite check (returns null/empty when satisfied, or a rejection reason).
    /// </summary>
    public void AddExternalInsertPrerequisite(Func<string> validator)
    {
        if (validator != null && !_externalInsertPrerequisites.Contains(validator))
            _externalInsertPrerequisites.Add(validator);
    }

    /// <summary>
    /// Checks whether all required connecting rods (and host rod) and external stage prerequisites are currently satisfied.
    /// </summary>
    public bool AreRodPrerequisitesMet(out string missingReason)
    {
        if (hostRodSnapPoint != null && !hostRodSnapPoint.IsOccupied)
        {
            missingReason = $"Install rod at '{hostRodSnapPoint.SnapPointId}' before placing '{acceptedFastenerType}'.";
            return false;
        }

        for (int i = 0; i < requiredRodSnapPoints.Count; i++)
        {
            MoldSnapPoint rodSp = requiredRodSnapPoints[i];
            if (rodSp != null && !rodSp.IsOccupied)
            {
                missingReason = $"All connecting rods must be installed before inserting screws/nuts (missing '{rodSp.SnapPointId}').";
                return false;
            }
        }

        for (int i = 0; i < _externalInsertPrerequisites.Count; i++)
        {
            string extReason = _externalInsertPrerequisites[i]?.Invoke();
            if (!string.IsNullOrEmpty(extReason))
            {
                missingReason = extReason;
                return false;
            }
        }

        missingReason = string.Empty;
        return true;
    }

    /// <summary>
    /// Checks whether a candidate ScrewInteraction can be inserted into this slot right now.
    /// </summary>
    public bool CanInsertScrew(ScrewInteraction candidate, bool checkPoseTolerance, out string rejectionReason)
    {
        if (candidate == null)
        {
            rejectionReason = "Candidate fastener is null.";
            return false;
        }

        if (IsOccupied)
        {
            rejectionReason = $"Threaded slot '{SlotId}' is already occupied by '{_seatedScrew.Part.DisplayName}'.";
            return false;
        }

        if (candidate.IsSeated)
        {
            rejectionReason = $"'{candidate.Part.DisplayName}' is already seated in another slot.";
            return false;
        }

        if (!string.Equals(candidate.FastenerType?.Trim(), acceptedFastenerType?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            rejectionReason = $"Wrong fastener '{candidate.Part.DisplayName}' ({candidate.FastenerType}) for '{SlotId}' (expected {acceptedFastenerType}).";
            return false;
        }

        if (!AreRodPrerequisitesMet(out string rodReason))
        {
            rejectionReason = rodReason;
            return false;
        }

        if (checkPoseTolerance)
        {
            float effectiveTol = GetEffectiveInsertionPositionTolerance();
            Vector3 candidateCenter = MoldSnapPoint.GetPartWorldGeometricCenter(candidate.Part);
            float distLoose = Vector3.Distance(candidateCenter, LooseWorldGeometricCenter);
            float distTight = Vector3.Distance(candidateCenter, TightenedWorldGeometricCenter);
            float minDist = Mathf.Min(distLoose, distTight);

            if (minDist > effectiveTol)
            {
                rejectionReason = $"'{candidate.Part.DisplayName}' is too far from threaded slot '{SlotId}' ({minDist:F3}m > {effectiveTol:F3}m).";
                return false;
            }

            // Bore alignment check (hex nut bore axis must align within tolerance of thread axis).
            float forwardAngle = Vector3.Angle(candidate.WorldThreadAxis, WorldTightenDirection);
            float reverseAngle = Vector3.Angle(-candidate.WorldThreadAxis, WorldTightenDirection);
            float axialError = Mathf.Min(forwardAngle, reverseAngle);
            if (axialError > boreAlignmentToleranceDegrees)
            {
                rejectionReason = $"'{candidate.Part.DisplayName}' is misaligned with thread axis at '{SlotId}' ({axialError:F1} deg > {boreAlignmentToleranceDegrees:F1} deg).";
                return false;
            }
        }

        rejectionReason = string.Empty;
        return true;
    }

    private float GetEffectiveInsertionPositionTolerance()
    {
        if (moldContainer != null)
        {
            MoldAssemblyManager mgr = moldContainer.GetComponent<MoldAssemblyManager>();
            if (mgr != null && mgr.WorkflowPhase == AssemblyWorkflowPhase.Dismantling)
            {
                // During Dismantling, require placing right at the rod tip (<= 6.5 cm) to re-seat a removed nut
                // so pulling a loosened nut off the rod tip and releasing it drops it cleanly instead of snapping back.
                return Mathf.Min(insertionPositionTolerance, 0.065f);
            }
        }

        return insertionPositionTolerance;
    }

    /// <summary>
    /// Inserts a compatible fastener into this slot's LOOSE starting position (progress = 0, LooseSeated, still hand-removable).
    /// </summary>
    public bool TryInsertScrewLoose(ScrewInteraction candidate, bool checkPoseTolerance = true)
    {
        if (!CanInsertScrew(candidate, checkPoseTolerance, out string reason))
        {
            if (candidate != null)
            {
                float effectiveTol = GetEffectiveInsertionPositionTolerance();
                Vector3 candidateCenter = MoldSnapPoint.GetPartWorldGeometricCenter(candidate.Part);
                float dist = Mathf.Min(
                    Vector3.Distance(candidateCenter, LooseWorldGeometricCenter),
                    Vector3.Distance(candidateCenter, TightenedWorldGeometricCenter));
                if (!checkPoseTolerance || dist <= effectiveTol * 1.4f)
                    NotifyRejection(candidate, reason);
            }
            return false;
        }

        BindSeatedScrewInternal(candidate, initialTightenProgress: 0f, invokeInsertedEvent: true);
        return true;
    }

    /// <summary>
    /// Evaluates all active ScrewSlots when a free fastener is released by the user.
    /// </summary>
    public static bool TryInsertReleasedScrewToAnySlot(ScrewInteraction releasedScrew)
    {
        if (releasedScrew == null || releasedScrew.Part == null || !releasedScrew.Part.IsFree)
            return false;

        Vector3 center = MoldSnapPoint.GetPartWorldGeometricCenter(releasedScrew.Part);

        ScrewSlot bestValidSlot = null;
        float bestDist = float.MaxValue;

        ScrewSlot closestRejectedSlot = null;
        string closestRejectReason = null;
        float closestRejectDist = float.MaxValue;

        for (int i = 0; i < ActiveScrewSlots.Count; i++)
        {
            ScrewSlot slot = ActiveScrewSlots[i];
            if (slot == null || !slot.isActiveAndEnabled || slot.IsOccupied)
                continue;

            float effectiveTol = slot.GetEffectiveInsertionPositionTolerance();
            float dist = Mathf.Min(
                Vector3.Distance(center, slot.LooseWorldGeometricCenter),
                Vector3.Distance(center, slot.TightenedWorldGeometricCenter));

            if (dist > effectiveTol * 1.35f)
                continue;

            if (slot.CanInsertScrew(releasedScrew, checkPoseTolerance: true, out string reason))
            {
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestValidSlot = slot;
                }
            }
            else if (dist <= effectiveTol && dist < closestRejectDist)
            {
                closestRejectDist = dist;
                closestRejectedSlot = slot;
                closestRejectReason = reason;
            }
        }

        if (bestValidSlot != null)
            return bestValidSlot.TryInsertScrewLoose(releasedScrew, checkPoseTolerance: true);

        if (closestRejectedSlot != null)
            closestRejectedSlot.NotifyRejection(releasedScrew, closestRejectReason);

        return false;
    }

    internal void NotifySeatedScrewRemoved(ScrewInteraction screw)
    {
        if (screw != _seatedScrew)
            return;

        _seatedScrew = null;
        RefreshHostRodRetention();
        ScrewRemoved?.Invoke(this, screw);
    }

    internal void RefreshHostRodRetention()
    {
        if (hostRodSnapPoint == null)
            return;

        bool anyBlockingOnRod = IsBlockingHostRodRemoval;
        if (!anyBlockingOnRod)
        {
            for (int i = 0; i < ActiveScrewSlots.Count; i++)
            {
                ScrewSlot other = ActiveScrewSlots[i];
                if (other != null && other != this && other.isActiveAndEnabled &&
                    other.hostRodSnapPoint == hostRodSnapPoint && other.IsBlockingHostRodRemoval)
                {
                    anyBlockingOnRod = true;
                    break;
                }
            }
        }

        MoldPart rodPart = hostRodSnapPoint.OccupiedPart;
        if (rodPart != null)
        {
            ConnectingRod rod = rodPart.GetComponent<ConnectingRod>();
            if (rod != null)
                rod.SetFastenerSeated(anyBlockingOnRod);
        }

        hostRodSnapPoint.RefreshLockState();
    }

    internal void SeatCaptiveScrewFromInstalledRod(ScrewInteraction captiveScrew)
    {
        if (captiveScrew == null || IsOccupied)
            return;

        BindSeatedScrewInternal(captiveScrew, initialTightenProgress: 0f, invokeInsertedEvent: true);
    }

    private void BindSeatedScrewInternal(ScrewInteraction screw, float initialTightenProgress, bool invokeInsertedEvent)
    {
        _seatedScrew = screw;
        screw.OnSeatedIntoSlot(this, initialTightenProgress);
        RegisterCaptiveNutWithHostRod();
        RefreshHostRodRetention();

        if (invokeInsertedEvent)
            ScrewInsertedLoose?.Invoke(this, screw);
    }

    private void RegisterCaptiveNutWithHostRod()
    {
        if (_seatedScrew == null || hostRodSnapPoint == null)
            return;

        bool isCurrentlyOccupied = hostRodSnapPoint.OccupiedPart != null;
        MoldPart rodPart = isCurrentlyOccupied
            ? hostRodSnapPoint.OccupiedPart
            : hostRodSnapPoint.AcceptedPart;
        if (rodPart == null)
            return;

        ConnectingRod rod = rodPart.GetComponent<ConnectingRod>();
        if (rod != null)
        {
            Vector3 rodPos = isCurrentlyOccupied ? rodPart.transform.position : hostRodSnapPoint.TargetWorldPosition;
            Quaternion rodRot = isCurrentlyOccupied ? rodPart.transform.rotation : hostRodSnapPoint.TargetWorldRotation;
            rod.RegisterOrUpdateCaptiveNut(
                _seatedScrew,
                LooseWorldPosition,
                CanonicalWorldRotation,
                rodPos,
                rodRot);
        }
    }

    private void SyncInitialSeatedScrew()
    {
        if (_seatedScrew != null)
            return;

        if (initialSeatedScrew != null && !initialSeatedScrew.Part.IsFree)
        {
            float startProgress = initialSeatedScrew.Part.IsInstalled ? 1f : 0f;
            BindSeatedScrewInternal(initialSeatedScrew, startProgress, invokeInsertedEvent: false);
        }
    }

    private void RegisterRodRetentionBlocker()
    {
        if (hostRodSnapPoint != null)
        {
            if (_rodBlockerDelegate == null)
                _rodBlockerDelegate = () => IsBlockingHostRodRemoval;

            hostRodSnapPoint.AddExternalRemovalBlocker(_rodBlockerDelegate);
            hostRodSnapPoint.PartRemoved -= OnHostRodRemoved;
            hostRodSnapPoint.PartRemoved += OnHostRodRemoved;
        }
    }

    private void OnHostRodRemoved(MoldSnapPoint snapPoint, MoldPart removedRod)
    {
        if (_seatedScrew == null || _seatedScrew.Part == null)
            return;

        // Keep loosened nuts (and their washers) attached to the tips of the removed rod!
        ConnectingRod rod = removedRod != null ? removedRod.GetComponent<ConnectingRod>() : null;
        if (rod != null)
        {
            rod.RegisterOrUpdateCaptiveNut(
                _seatedScrew,
                LooseWorldPosition,
                CanonicalWorldRotation,
                snapPoint != null ? snapPoint.TargetWorldPosition : rod.transform.position,
                snapPoint != null ? snapPoint.TargetWorldRotation : rod.transform.rotation);
            _seatedScrew.AttachCaptiveToRemovedRod(rod);
            rod.SyncCaptiveNutsToRodPose();
        }
    }

    private void NotifyRejection(ScrewInteraction candidate, string reason)
    {
        InsertionRejected?.Invoke(this, candidate, reason);
        GlobalInsertionRejected?.Invoke(this, candidate, reason);

        if (Time.unscaledTime - _lastRejectLogTime > 1.0f)
        {
            _lastRejectLogTime = Time.unscaledTime;
            Debug.Log($"[ScrewSlot] {reason}", this);
        }
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.75f, 0.15f, 0.7f);
        Gizmos.DrawWireSphere(LooseWorldGeometricCenter, 0.03f);
        Gizmos.color = new Color(0.2f, 0.9f, 0.35f, 0.7f);
        Gizmos.DrawWireSphere(TightenedWorldGeometricCenter, 0.03f);
        Gizmos.DrawLine(LooseWorldGeometricCenter, TightenedWorldGeometricCenter);
    }
}
