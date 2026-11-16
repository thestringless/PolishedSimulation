using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Configurable snap point for casting mold assembly and dismantling.
/// Validates prerequisites, part identity/category, position tolerance, and orientation alignment,
/// locks snapped parts kinematically without physics jitter, and enforces dependency-ordered removal.
/// </summary>
[DisallowMultipleComponent]
public class MoldSnapPoint : MonoBehaviour
{
    private static readonly List<MoldSnapPoint> ActiveSnapPoints = new List<MoldSnapPoint>();

    [Header("Snap Point Identity")]
    [Tooltip("Unique identifier for this snap location (e.g., Snap_Base_Pallet, Snap_Wall_Long_Front).")]
    [SerializeField] private string snapPointId;

    [Tooltip("Accepted category of mold component.")]
    [SerializeField] private MoldPartCategory acceptedCategory = MoldPartCategory.LongWall;

    [Tooltip("Specific MoldPart instance required at this snap point when requireExactPart is true.")]
    [SerializeField] private MoldPart acceptedPart;

    [Tooltip("When true, only 'acceptedPart' is valid here. When false, any free MoldPart of 'acceptedCategory' (and matching slot if >= 0) is accepted.")]
    [SerializeField] private bool requireExactPart = true;

    [Tooltip("Optional slot index filter when requireExactPart is false (-1 = accept any slot in acceptedCategory).")]
    [SerializeField] private int acceptedSlotIndex = -1;

    [Header("Target Pose & Alignment Tolerances")]
    [Tooltip("Target local position inside MoldContainer (cached from the assembled reference model).")]
    [SerializeField] private Transform moldContainer;

    [SerializeField] private Vector3 targetLocalPosition;
    [SerializeField] private Quaternion targetLocalRotation = Quaternion.identity;

    [Tooltip("Local offset from target pose to the part's geometric collider center (decouples position check from off-center FBX pivots).")]
    [SerializeField] private Vector3 localColliderCenterOffset = Vector3.zero;

    [Tooltip("Maximum world-space distance (meters) between part geometric center and snap target center.")]
    [SerializeField] private float positionTolerance = 0.24f;

    [Tooltip("Maximum angular error (degrees) between part rotation and target rotation.")]
    [Range(2f, 180f)]
    [SerializeField] private float rotationToleranceDegrees = 38f;

    [Tooltip("Allow 180-degree symmetric orientation around the part's up/forward symmetry axis when physically symmetric.")]
    [SerializeField] private bool allowSymmetricFlip = false;

    [Tooltip("Local axis around which a 180-degree flip is considered equivalent when allowSymmetricFlip is true.")]
    [SerializeField] private Vector3 symmetryLocalAxis = Vector3.up;

    [Tooltip("When true (for cylindrical connecting rods and bolts), validates directed shaft-axis alignment regardless of roll around the shaft.")]
    [SerializeField] private bool useAxialAlignmentOnly = false;

    [Tooltip("Local shaft direction from head end toward threaded tip when useAxialAlignmentOnly is enabled.")]
    [SerializeField] private Vector3 localShaftAxis = Vector3.forward;

    [Tooltip("Allow inserting an unheaded symmetrical rod from either direction along localShaftAxis.")]
    [SerializeField] private bool allowBidirectionalShaftAxis = false;

    [Header("Assembly & Dismantling Dependencies")]
    [Tooltip("All listed prerequisite snap points must be Installed before this snap point accepts a part.")]
    [SerializeField] private List<MoldSnapPoint> prerequisiteSnapPoints = new List<MoldSnapPoint>();

    [Tooltip("When true, once a part snaps here it remains locked against hand grabbing even when no dependents are installed.")]
    [SerializeField] private bool lockPermanentlyOnceInstalled = false;

    [Tooltip("When true, while this snap point is occupied it locks its prerequisiteSnapPoints against removal. Set false when prerequisites enforce assembly order only (e.g., Long Walls before Short Walls) while allowing free wall removal order during dismantling.")]
    [SerializeField] private bool blockPrerequisiteRemovalWhenOccupied = true;

    [Tooltip("Part currently occupying this snap point in Installed state (auto-synced if starting assembled).")]
    [SerializeField] private MoldPart initialOccupiedPart;

    private readonly List<MoldSnapPoint> _dependentSnapPoints = new List<MoldSnapPoint>();
    private readonly List<Func<bool>> _externalRemovalBlockers = new List<Func<bool>>();
    private readonly List<Func<string>> _externalInstallPrerequisites = new List<Func<string>>();
    private MoldPart _occupiedPart;
    private float _lastRejectLogTime = -10f;

    /// <summary>Raised when a valid part snaps and locks into this snap point.</summary>
    public event Action<MoldSnapPoint, MoldPart> PartSnapped;

    /// <summary>Raised when an installed part is removed from this snap point during dismantling.</summary>
    public event Action<MoldSnapPoint, MoldPart> PartRemoved;

    /// <summary>Raised when a nearby part fails prerequisite, identity, or alignment checks.</summary>
    public event Action<MoldSnapPoint, MoldPart, string> PlacementRejected;

    /// <summary>Global notification raised whenever any MoldSnapPoint snaps, removes, or rejects a part.</summary>
    public static event Action<MoldSnapPoint, MoldPart, string> GlobalPlacementRejected;

    public static IReadOnlyList<MoldSnapPoint> AllActive => ActiveSnapPoints;

    public string SnapPointId => string.IsNullOrWhiteSpace(snapPointId) ? name : snapPointId;

    public MoldPartCategory AcceptedCategory => acceptedCategory;

    public MoldPart AcceptedPart => acceptedPart;

    public bool RequireExactPart => requireExactPart;

    public float PositionTolerance => positionTolerance;

    public float RotationToleranceDegrees => rotationToleranceDegrees;

    public bool LockPermanentlyOnceInstalled
    {
        get => lockPermanentlyOnceInstalled;
        set
        {
            lockPermanentlyOnceInstalled = value;
            RefreshLockState();
        }
    }

    public bool BlockPrerequisiteRemovalWhenOccupied
    {
        get => blockPrerequisiteRemovalWhenOccupied;
        set
        {
            blockPrerequisiteRemovalWhenOccupied = value;
            RefreshAllActiveLocks();
        }
    }

    public IReadOnlyList<MoldSnapPoint> PrerequisiteSnapPoints => prerequisiteSnapPoints;

    public IReadOnlyList<MoldSnapPoint> DependentSnapPoints => _dependentSnapPoints;

    public MoldPart OccupiedPart => _occupiedPart;

    public bool IsOccupied => _occupiedPart != null && _occupiedPart.IsInstalled;

    public Vector3 TargetWorldPosition =>
        moldContainer != null ? moldContainer.TransformPoint(targetLocalPosition) : transform.position;

    public Quaternion TargetWorldRotation =>
        moldContainer != null ? moldContainer.rotation * targetLocalRotation : transform.rotation;

    public Vector3 TargetWorldShaftAxis =>
        (TargetWorldRotation * (localShaftAxis.sqrMagnitude > Mathf.Epsilon ? localShaftAxis.normalized : Vector3.forward)).normalized;

    public Vector3 TargetWorldGeometricCenter
    {
        get
        {
            Vector3 worldScale = moldContainer != null ? moldContainer.lossyScale : Vector3.one;
            Vector3 scaledOffset = Vector3.Scale(localColliderCenterOffset, worldScale);
            return TargetWorldPosition + (TargetWorldRotation * scaledOffset);
        }
    }

    private void Awake()
    {
        RegisterDependencyLinks();
        SyncInitialOccupiedPart();
    }

    private void OnEnable()
    {
        if (!ActiveSnapPoints.Contains(this))
            ActiveSnapPoints.Add(this);

        RegisterDependencyLinks();
        BindCandidateCallbacks();
        RefreshLockState();
    }

    private void Start()
    {
        RegisterDependencyLinks();
        SyncInitialOccupiedPart();
        BindCandidateCallbacks();
        RefreshAllActiveLocks();
    }

    private void OnDisable()
    {
        ActiveSnapPoints.Remove(this);
        UnregisterDependencyLinks();

        if (_occupiedPart != null)
            _occupiedPart.StateChanged -= OnOccupiedPartStateChanged;
    }

    /// <summary>
    /// Configures this snap point directly from an assembled MoldPart's reference pose and collider bounds.
    /// </summary>
    public void ConfigureFromMoldPart(
        string id,
        MoldPart referencePart,
        bool exactMatch,
        float posTol,
        float rotTolDeg,
        List<MoldSnapPoint> prerequisites = null,
        bool startOccupied = true)
    {
        if (referencePart == null)
            return;

        snapPointId = id;
        acceptedCategory = referencePart.Category;
        acceptedPart = referencePart;
        requireExactPart = exactMatch;
        acceptedSlotIndex = exactMatch ? referencePart.SlotIndex : -1;
        moldContainer = referencePart.MoldContainer != null ? referencePart.MoldContainer : referencePart.transform.parent;
        targetLocalPosition = referencePart.AssembledLocalPosition;
        targetLocalRotation = referencePart.AssembledLocalRotation;
        localColliderCenterOffset = ExtractLocalColliderCenter(referencePart);
        positionTolerance = posTol;
        rotationToleranceDegrees = rotTolDeg;

        prerequisiteSnapPoints.Clear();
        if (prerequisites != null)
        {
            for (int i = 0; i < prerequisites.Count; i++)
            {
                if (prerequisites[i] != null && prerequisites[i] != this)
                    prerequisiteSnapPoints.Add(prerequisites[i]);
            }
        }

        transform.SetPositionAndRotation(TargetWorldGeometricCenter, TargetWorldRotation);
        initialOccupiedPart = startOccupied ? referencePart : null;
    }

    /// <summary>
    /// Enables cylindrical shaft-axis alignment mode (roll-invariant around shaft) for connecting rods and bolts.
    /// </summary>
    public void ConfigureAxialAlignment(Vector3 shaftLocalAxis, bool bidirectional = false)
    {
        useAxialAlignmentOnly = true;
        if (shaftLocalAxis.sqrMagnitude > Mathf.Epsilon)
            localShaftAxis = shaftLocalAxis.normalized;
        allowBidirectionalShaftAxis = bidirectional;
    }

    /// <summary>
    /// Registers an external condition (e.g., an installed connecting rod or tightened fastener) that blocks removing this part.
    /// </summary>
    public void AddExternalRemovalBlocker(Func<bool> isBlockedEvaluator)
    {
        if (isBlockedEvaluator != null && !_externalRemovalBlockers.Contains(isBlockedEvaluator))
        {
            _externalRemovalBlockers.Add(isBlockedEvaluator);
            RefreshLockState();
        }
    }

    /// <summary>
    /// Registers an external prerequisite check (returns empty/null when satisfied, or a human-readable blocker reason).
    /// </summary>
    public void AddExternalInstallPrerequisite(Func<string> prerequisiteValidator)
    {
        if (prerequisiteValidator != null && !_externalInstallPrerequisites.Contains(prerequisiteValidator))
            _externalInstallPrerequisites.Add(prerequisiteValidator);
    }

    /// <summary>
    /// Checks whether all prerequisite snap points and external prerequisites are currently satisfied.
    /// </summary>
    public bool ArePrerequisitesMet(out string missingReason)
    {
        for (int i = 0; i < prerequisiteSnapPoints.Count; i++)
        {
            MoldSnapPoint prereq = prerequisiteSnapPoints[i];
            if (prereq != null && !prereq.IsOccupied)
            {
                if (acceptedCategory == MoldPartCategory.ShortWall && prereq.acceptedCategory == MoldPartCategory.LongWall)
                {
                    missingReason = "Restricted Order: Place both Long Walls first before placing the Short Walls!";
                    return false;
                }

                string neededLabel = prereq.acceptedPart != null
                    ? prereq.acceptedPart.DisplayName
                    : prereq.AcceptedCategory.ToString();
                missingReason = $"Prerequisite '{neededLabel}' must be installed before '{SnapPointId}'.";
                return false;
            }
        }

        for (int i = 0; i < _externalInstallPrerequisites.Count; i++)
        {
            string extReason = _externalInstallPrerequisites[i]?.Invoke();
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
    /// Checks whether any dependent snap point or external blocker currently prevents removing this snap point's part.
    /// </summary>
    public bool HasActiveDependentsBlockingRemoval()
    {
        if (lockPermanentlyOnceInstalled)
            return true;

        for (int i = 0; i < _dependentSnapPoints.Count; i++)
        {
            MoldSnapPoint dep = _dependentSnapPoints[i];
            if (dep != null && dep.IsOccupied && dep.blockPrerequisiteRemovalWhenOccupied)
                return true;
        }

        for (int i = 0; i < _externalRemovalBlockers.Count; i++)
        {
            if (_externalRemovalBlockers[i] != null && _externalRemovalBlockers[i].Invoke())
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks whether the candidate MoldPart matches this snap point's identity/category rules.
    /// </summary>
    public bool MatchesIdentity(MoldPart candidate)
    {
        if (candidate == null)
            return false;

        if (requireExactPart)
            return candidate == acceptedPart;

        if (candidate.Category != acceptedCategory)
            return false;

        if (acceptedSlotIndex >= 0 && candidate.SlotIndex != acceptedSlotIndex)
            return false;

        return true;
    }

    /// <summary>
    /// Evaluates full snap eligibility (occupancy, prerequisites, identity, position tolerance, and rotation alignment).
    /// </summary>
    public bool CanSnapPart(MoldPart candidate, bool checkPoseTolerance, out Quaternion resolvedSnapRotation, out string rejectionReason)
    {
        resolvedSnapRotation = TargetWorldRotation;

        if (candidate == null)
        {
            rejectionReason = "Candidate part is null.";
            return false;
        }

        if (IsOccupied)
        {
            rejectionReason = $"Snap point '{SnapPointId}' is already occupied by '{_occupiedPart.DisplayName}'.";
            return false;
        }

        if (!candidate.IsFree)
        {
            rejectionReason = $"'{candidate.DisplayName}' is already installed.";
            return false;
        }

        if (!MatchesIdentity(candidate))
        {
            string expected = requireExactPart && acceptedPart != null
                ? acceptedPart.DisplayName
                : acceptedCategory.ToString();
            rejectionReason = $"Incorrect part '{candidate.DisplayName}' for '{SnapPointId}' (expected '{expected}').";
            return false;
        }

        if (!ArePrerequisitesMet(out string prereqReason))
        {
            rejectionReason = prereqReason;
            return false;
        }

        if (checkPoseTolerance)
        {
            Vector3 candidateCenter = GetPartWorldGeometricCenter(candidate);
            float posError = Vector3.Distance(candidateCenter, TargetWorldGeometricCenter);
            if (posError > positionTolerance)
            {
                rejectionReason = $"'{candidate.DisplayName}' is too far from '{SnapPointId}' ({posError:F3}m > {positionTolerance:F3}m).";
                return false;
            }

            float rotError = ComputeMinimumAngularError(candidate, out resolvedSnapRotation);
            if (rotError > rotationToleranceDegrees)
            {
                rejectionReason = $"'{candidate.DisplayName}' orientation misaligned at '{SnapPointId}' ({rotError:F1} deg > {rotationToleranceDegrees:F1} deg).";
                return false;
            }
        }

        rejectionReason = string.Empty;
        return true;
    }

    /// <summary>
    /// Attempts to validate and snap the candidate MoldPart into this snap point.
    /// </summary>
    public bool TrySnapPart(MoldPart candidate, bool checkPoseTolerance = true)
    {
        if (!CanSnapPart(candidate, checkPoseTolerance, out Quaternion resolvedRot, out string reason))
        {
            if (candidate != null)
            {
                float proximity = Vector3.Distance(GetPartWorldGeometricCenter(candidate), TargetWorldGeometricCenter);
                if (!checkPoseTolerance || proximity <= positionTolerance * 1.4f)
                    NotifyRejection(candidate, reason);
            }
            return false;
        }

        AttachPartInternal(candidate, resolvedRot, invokeEvent: true);
        return true;
    }

    /// <summary>
    /// Evaluates all active MoldSnapPoints when a free MoldPart is released by the user.
    /// </summary>
    public static bool TrySnapReleasedPartToAnyActivePoint(MoldPart releasedPart)
    {
        if (releasedPart == null || !releasedPart.IsFree)
            return false;

        Vector3 partCenter = GetPartWorldGeometricCenter(releasedPart);

        // 1. First look for a matching identity snap point within position tolerance.
        MoldSnapPoint bestValidMatch = null;
        float bestDist = float.MaxValue;

        MoldSnapPoint closestNearbyMismatch = null;
        string closestMismatchReason = null;
        float closestMismatchDist = float.MaxValue;

        for (int i = 0; i < ActiveSnapPoints.Count; i++)
        {
            MoldSnapPoint sp = ActiveSnapPoints[i];
            if (sp == null || !sp.isActiveAndEnabled || sp.IsOccupied)
                continue;

            float dist = Vector3.Distance(partCenter, sp.TargetWorldGeometricCenter);
            if (dist > sp.positionTolerance * 1.35f)
                continue;

            if (sp.CanSnapPart(releasedPart, checkPoseTolerance: true, out _, out string reason))
            {
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestValidMatch = sp;
                }
            }
            else if (dist <= sp.positionTolerance && dist < closestMismatchDist)
            {
                closestMismatchDist = dist;
                closestNearbyMismatch = sp;
                closestMismatchReason = reason;
            }
        }

        if (bestValidMatch != null)
            return bestValidMatch.TrySnapPart(releasedPart, checkPoseTolerance: true);

        if (closestNearbyMismatch != null)
            closestNearbyMismatch.NotifyRejection(releasedPart, closestMismatchReason);

        return false;
    }

    /// <summary>
    /// Re-evaluates dependency lock states across all active snap points.
    /// </summary>
    public static void RefreshAllActiveLocks()
    {
        for (int i = 0; i < ActiveSnapPoints.Count; i++)
        {
            if (ActiveSnapPoints[i] != null)
                ActiveSnapPoints[i].RefreshLockState();
        }
    }

    /// <summary>
    /// Updates whether the currently occupied part is locked against XR removal based on active dependents.
    /// </summary>
    public void RefreshLockState()
    {
        if (_occupiedPart == null)
            return;

        if (!_occupiedPart.IsInstalled)
        {
            ClearOccupiedPartInternal(_occupiedPart);
            return;
        }

        bool locked = HasActiveDependentsBlockingRemoval();
        _occupiedPart.SetDependencyLocked(locked);
    }

    private void AttachPartInternal(MoldPart part, Quaternion worldRot, bool invokeEvent)
    {
        if (_occupiedPart != null && _occupiedPart != part)
            _occupiedPart.StateChanged -= OnOccupiedPartStateChanged;

        _occupiedPart = part;
        part.StateChanged -= OnOccupiedPartStateChanged;
        part.StateChanged += OnOccupiedPartStateChanged;
        part.CanBeRemovedPredicate = _ => !HasActiveDependentsBlockingRemoval();

        part.SnapToWorldPose(TargetWorldPosition, worldRot, MoldPartState.Installed);
        RefreshAllActiveLocks();

        if (invokeEvent)
            PartSnapped?.Invoke(this, part);
    }

    private void OnOccupiedPartStateChanged(MoldPart part, MoldPartState previousState, MoldPartState newState)
    {
        if (part != _occupiedPart)
            return;

        if (newState != MoldPartState.Installed)
        {
            ClearOccupiedPartInternal(part);
        }
    }

    private void ClearOccupiedPartInternal(MoldPart removedPart)
    {
        if (removedPart != null)
        {
            removedPart.StateChanged -= OnOccupiedPartStateChanged;
            removedPart.SetDependencyLocked(false);
        }

        _occupiedPart = null;
        RefreshAllActiveLocks();

        if (removedPart != null)
            PartRemoved?.Invoke(this, removedPart);
    }

    private void SyncInitialOccupiedPart()
    {
        if (_occupiedPart != null)
            return;

        MoldPart starter = initialOccupiedPart != null ? initialOccupiedPart : acceptedPart;
        if (starter != null && starter.IsInstalled && MatchesIdentity(starter))
        {
            AttachPartInternal(starter, TargetWorldRotation, invokeEvent: false);
        }
    }

    private void RegisterDependencyLinks()
    {
        for (int i = 0; i < prerequisiteSnapPoints.Count; i++)
        {
            MoldSnapPoint prereq = prerequisiteSnapPoints[i];
            if (prereq != null && prereq != this && !prereq._dependentSnapPoints.Contains(this))
                prereq._dependentSnapPoints.Add(this);
        }
    }

    private void UnregisterDependencyLinks()
    {
        for (int i = 0; i < prerequisiteSnapPoints.Count; i++)
        {
            MoldSnapPoint prereq = prerequisiteSnapPoints[i];
            if (prereq != null)
                prereq._dependentSnapPoints.Remove(this);
        }
    }

    private void BindCandidateCallbacks()
    {
        if (acceptedPart != null)
            acceptedPart.TrySnapOnReleaseCallback = TrySnapReleasedPartToAnyActivePoint;

        if (moldContainer != null)
        {
            MoldPart[] parts = moldContainer.GetComponentsInChildren<MoldPart>(includeInactive: true);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] != null && parts[i].Category == acceptedCategory && parts[i].TrySnapOnReleaseCallback == null)
                    parts[i].TrySnapOnReleaseCallback = TrySnapReleasedPartToAnyActivePoint;
            }
        }
    }

    private float ComputeMinimumAngularError(MoldPart candidate, out Quaternion bestTargetRotation)
    {
        Quaternion candidateRotation = candidate.transform.rotation;
        Quaternion baseTarget = TargetWorldRotation;
        bestTargetRotation = baseTarget;

        if (useAxialAlignmentOnly)
        {
            ConnectingRod rod = candidate.GetComponent<ConnectingRod>();
            Vector3 candidateAxis = rod != null
                ? rod.WorldShaftAxis
                : (candidateRotation * (localShaftAxis.sqrMagnitude > Mathf.Epsilon ? localShaftAxis.normalized : Vector3.forward)).normalized;

            Vector3 targetAxis = TargetWorldShaftAxis;
            float directAngle = Vector3.Angle(candidateAxis, targetAxis);
            if (allowBidirectionalShaftAxis)
            {
                float reverseAngle = Vector3.Angle(-candidateAxis, targetAxis);
                return Mathf.Min(directAngle, reverseAngle);
            }

            return directAngle;
        }

        float anglePrimary = Quaternion.Angle(candidateRotation, baseTarget);

        if (allowSymmetricFlip && symmetryLocalAxis.sqrMagnitude > Mathf.Epsilon)
        {
            Vector3 worldSymAxis = (baseTarget * symmetryLocalAxis.normalized).normalized;
            Quaternion flippedTarget = Quaternion.AngleAxis(180f, worldSymAxis) * baseTarget;
            float angleFlipped = Quaternion.Angle(candidateRotation, flippedTarget);
            if (angleFlipped < anglePrimary)
            {
                bestTargetRotation = baseTarget; // Snap cleanly to canonical assembled orientation
                return angleFlipped;
            }
        }

        return anglePrimary;
    }

    private void NotifyRejection(MoldPart candidate, string reason)
    {
        PlacementRejected?.Invoke(this, candidate, reason);
        GlobalPlacementRejected?.Invoke(this, candidate, reason);

        if (Time.unscaledTime - _lastRejectLogTime > 1.0f)
        {
            _lastRejectLogTime = Time.unscaledTime;
            Debug.Log($"[MoldSnapPoint] {reason}", this);
        }
    }

    private static Vector3 ExtractLocalColliderCenter(MoldPart part)
    {
        if (part == null)
            return Vector3.zero;

        Collider col = part.GetComponent<Collider>();
        if (col is BoxCollider box)
            return box.center;
        if (col is CapsuleCollider cap)
            return cap.center;
        if (col is SphereCollider sph)
            return sph.center;

        MeshFilter mf = part.GetComponent<MeshFilter>();
        return mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds.center : Vector3.zero;
    }

    public static Vector3 GetPartWorldGeometricCenter(MoldPart part)
    {
        if (part == null)
            return Vector3.zero;

        Vector3 localCenter = ExtractLocalColliderCenter(part);
        return part.transform.TransformPoint(localCenter);
    }

    [ContextMenu("Test: Dismantle Occupied Part")]
    private void ContextMenuDismantleOccupiedPart()
    {
        if (_occupiedPart == null)
            return;

        if (HasActiveDependentsBlockingRemoval())
        {
            Debug.LogWarning($"[MoldSnapPoint] Cannot dismantle '{_occupiedPart.DisplayName}' while dependent parts are still installed.", this);
            return;
        }

        MoldPart part = _occupiedPart;
        part.ReleaseToFreeState();
        part.transform.position += Vector3.up * 0.25f + Vector3.left * 0.35f;
    }

    [ContextMenu("Test: Snap Accepted Part")]
    private void ContextMenuSnapAcceptedPart()
    {
        if (acceptedPart != null)
            TrySnapPart(acceptedPart, checkPoseTolerance: false);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = IsOccupied ? new Color(0.15f, 0.85f, 0.35f, 0.55f) : new Color(0.2f, 0.65f, 1.0f, 0.55f);
        Gizmos.DrawWireSphere(TargetWorldGeometricCenter, Mathf.Max(0.03f, positionTolerance * 0.5f));
    }
}
