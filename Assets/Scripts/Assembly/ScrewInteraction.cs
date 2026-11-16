using System;
using UnityEngine;

/// <summary>
/// Per-fastener component managing pickup, threaded slot insertion, loose starting state,
/// compatibility metadata, axial travel parameters, and tightening/loosening state transitions.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(MoldPart))]
public class ScrewInteraction : MonoBehaviour
{
    [Header("Fastener Identity & Tool Compatibility")]
    [Tooltip("Identifier for this fastener instance (e.g., RodNut_0 .. 5, Vertical_HexNut).")]
    [SerializeField] private string fastenerId;

    [Tooltip("Fastener type tag matched against ScrewSlot.AcceptedFastenerType (e.g., RodHexNut_M24, VerticalHexNut_M16).")]
    [SerializeField] private string fastenerType = "RodHexNut_M24";

    [Tooltip("Required ScrewdriverHead.FastenerCompatibilityId for tightening and loosening.")]
    [SerializeField] private string compatibleHeadId = "HexNut_Rod";

    [Header("Thread & Tightening Parameters")]
    [Tooltip("Local bore/rotation axis around which this fastener spins when tightened or loosened.")]
    [SerializeField] private Vector3 localRotationAxis = Vector3.forward;

    [Tooltip("Total revolutions of the fastener required to travel from loose (0) to fully tightened (1).")]
    [SerializeField] private float totalTighteningRevolutions = 4.0f;

    [Tooltip("Normalized progress threshold (0..1) at or above which the fastener counts as fully tightened.")]
    [Range(0.8f, 1f)]
    [SerializeField] private float tighteningCompletionThreshold = 0.98f;

    [Tooltip("Maximum normalized progress (0..1) at or below which the fastener is loose enough to remove by hand.")]
    [Range(0f, 0.25f)]
    [SerializeField] private float handRemovableThreshold = 0.05f;

    [Header("Live Fastening State")]
    [Tooltip("Normalized tightening depth/progress: 0 = loose starting position, 1 = fully tightened stop.")]
    [Range(0f, 1f)]
    [SerializeField] private float tightenProgress = 1f;

    [Tooltip("Threaded slot where this fastener is currently seated (null when Free).")]
    [SerializeField] private ScrewSlot currentSlot;

    private MoldPart _moldPart;
    private NutWasher _nutWasher;
    private ConnectingRod _captiveHostRod;
    private float _cumulativeSpinDegrees;
    private bool _wasInsertedLooseByUser;

    /// <summary>Raised whenever TightenProgress changes (screw, normalizedProgress, isTightened).</summary>
    public event Action<ScrewInteraction, float, bool> TightenProgressChanged;

    /// <summary>True when this fastener was placed into a slot at 0% loose by the operator and has not yet been tightened.</summary>
    public bool WasInsertedLooseByUser => _wasInsertedLooseByUser;

    /// <summary>True while this loosened nut stays attached to the tip of its removed ConnectingRod.</summary>
    public bool IsAttachedToRemovedRod => _captiveHostRod != null && currentSlot == null;

    /// <summary>Host ConnectingRod this nut is currently attached to while the rod is removed (null while seated in a slot).</summary>
    public ConnectingRod CaptiveHostRod => _captiveHostRod;

    public string FastenerId => string.IsNullOrWhiteSpace(fastenerId) ? name : fastenerId;

    public string FastenerType => fastenerType;

    public string CompatibleHeadId => compatibleHeadId;

    public MoldPart Part => _moldPart != null ? _moldPart : (_moldPart = GetComponent<MoldPart>());

    public Vector3 LocalRotationAxis =>
        localRotationAxis.sqrMagnitude > Mathf.Epsilon ? localRotationAxis.normalized : Vector3.forward;

    public Vector3 WorldThreadAxis => transform.TransformDirection(LocalRotationAxis).normalized;

    public float TotalTighteningRevolutions => Mathf.Max(0.25f, totalTighteningRevolutions);

    public float TotalTighteningDegrees => TotalTighteningRevolutions * 360f;

    public float TighteningCompletionThreshold => tighteningCompletionThreshold;

    public float HandRemovableThreshold => handRemovableThreshold;

    public float TightenProgress => tightenProgress;

    public ScrewSlot CurrentSlot => currentSlot;

    public bool IsSeated => currentSlot != null && Part != null && !Part.IsFree;

    public bool IsLoose => IsSeated && tightenProgress <= handRemovableThreshold;

    public bool IsTightened => IsSeated && tightenProgress >= tighteningCompletionThreshold;

    private void Awake()
    {
        EnsureBound();
    }

    private void OnEnable()
    {
        EnsureBound();
        if (_moldPart != null)
        {
            _moldPart.StateChanged += OnMoldPartStateChanged;
            _moldPart.TrySnapOnReleaseCallback = OnReleasedInFreeState;
            _moldPart.CanBeRemovedPredicate = CanRemoveByHand;
        }
    }

    private void Start()
    {
        if (currentSlot != null && Part != null && !Part.IsFree)
            RefreshPoseAndLockFromProgress(invokeEvents: false);
    }

    private void OnDisable()
    {
        if (_moldPart != null)
            _moldPart.StateChanged -= OnMoldPartStateChanged;
    }

    /// <summary>
    /// Configures fastener compatibility, rotation axis, thread revolutions, and starting state.
    /// </summary>
    public void Configure(
        string id,
        string typeTag,
        string requiredHeadCompatibility,
        Vector3 rotationAxisLocal,
        float revolutionsToTighten,
        ScrewSlot initialSlot,
        float initialProgress = 1f)
    {
        EnsureBound();
        fastenerId = id;
        fastenerType = typeTag;
        compatibleHeadId = requiredHeadCompatibility;
        if (rotationAxisLocal.sqrMagnitude > Mathf.Epsilon)
            localRotationAxis = rotationAxisLocal.normalized;
        totalTighteningRevolutions = Mathf.Max(0.25f, revolutionsToTighten);
        currentSlot = initialSlot;
        tightenProgress = Mathf.Clamp01(initialProgress);
        _cumulativeSpinDegrees = tightenProgress * TotalTighteningDegrees;
        RefreshPoseAndLockFromProgress(invokeEvents: false);
    }

    /// <summary>
    /// Checks whether a given ScrewdriverHead is compatible with this fastener.
    /// </summary>
    public bool IsHeadCompatible(ScrewdriverHead head)
    {
        if (head == null)
            return false;

        return string.Equals(
            head.FastenerCompatibilityId?.Trim(),
            compatibleHeadId?.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Called by ScrewSlot when this fastener is seated into a slot (progress = 0 for loose insertion, 1 for pre-tightened).
    /// </summary>
    internal void OnSeatedIntoSlot(ScrewSlot slot, float initialProgress)
    {
        EnsureBound();
        currentSlot = slot;
        if (slot != null)
        {
            Vector3 inwardLocal = Quaternion.Inverse(slot.CanonicalWorldRotation) * (-slot.WorldLoosenDirection);
            if (inwardLocal.sqrMagnitude > 1e-4f)
                localRotationAxis = inwardLocal.normalized;
        }
        tightenProgress = Mathf.Clamp01(initialProgress);
        _wasInsertedLooseByUser = tightenProgress <= 0.01f;
        _cumulativeSpinDegrees = tightenProgress * TotalTighteningDegrees;
        RefreshPoseAndLockFromProgress(invokeEvents: true);
    }

    /// <summary>
    /// Advances (+degrees) or loosens (-degrees) this seated fastener progressively along its thread axis.
    /// Stops cleanly at 1.0 (fully tightened limit) and 0.0 (loose removable position).
    /// Returns the actual signed degrees applied before hitting the mechanical stop.
    /// </summary>
    public float ApplyRotationalDrive(float signedDriveDegrees)
    {
        if (!IsSeated || currentSlot == null || Mathf.Abs(signedDriveDegrees) < 1e-5f)
            return 0f;

        float totalDeg = TotalTighteningDegrees;
        float previousProgress = tightenProgress;
        float desiredProgress = Mathf.Clamp01(previousProgress + (signedDriveDegrees / totalDeg));
        float actualProgressDelta = desiredProgress - previousProgress;
        if (Mathf.Abs(actualProgressDelta) < 1e-6f)
            return 0f;

        float actualDegrees = actualProgressDelta * totalDeg;
        tightenProgress = desiredProgress;
        if (tightenProgress >= tighteningCompletionThreshold)
            _wasInsertedLooseByUser = false;
        _cumulativeSpinDegrees += actualDegrees;

        RefreshPoseAndLockFromProgress(invokeEvents: true);
        return actualDegrees;
    }

    /// <summary>
    /// Directly sets normalized tightening progress (0 = loose starting state, 1 = fully tightened).
    /// </summary>
    public void SetTightenProgress(float normalizedProgress)
    {
        if (!IsSeated || currentSlot == null)
        {
            tightenProgress = 0f;
            return;
        }

        tightenProgress = Mathf.Clamp01(normalizedProgress);
        if (tightenProgress >= tighteningCompletionThreshold)
            _wasInsertedLooseByUser = false;
        _cumulativeSpinDegrees = tightenProgress * TotalTighteningDegrees;
        RefreshPoseAndLockFromProgress(invokeEvents: true);
    }

    [ContextMenu("Test: Loosen Completely To LooseSeated")]
    public void TestLoosenCompletely()
    {
        SetTightenProgress(0f);
    }

    [ContextMenu("Test: Tighten Completely")]
    public void TestTightenCompletely()
    {
        SetTightenProgress(1f);
    }

    public void SyncWasherImmediate()
    {
        if (_nutWasher == null)
            _nutWasher = GetComponent<NutWasher>();

        if (_nutWasher != null)
        {
            _nutWasher.EnsureWasherVisual();
            _nutWasher.SyncWasherPose();
        }
    }

    internal void AttachCaptiveToRemovedRod(ConnectingRod hostRod)
    {
        if (hostRod == null)
            return;

        EnsureBound();
        if (_captiveHostRod == hostRod && currentSlot == null && Part != null && Part.IsCaptiveFollower)
            return;

        _captiveHostRod = hostRod;

        if (currentSlot != null)
        {
            ScrewSlot prevSlot = currentSlot;
            currentSlot = null;
            tightenProgress = 0f;
            _cumulativeSpinDegrees = 0f;
            _wasInsertedLooseByUser = false;
            prevSlot.NotifySeatedScrewRemoved(this);
        }

        if (Part != null)
        {
            bool prevSuppress = MoldPart.SuppressStateChangeAudio;
            MoldPart.SuppressStateChangeAudio = true;
            try
            {
                Part.SetCaptiveFollowerMode(true);
                Part.SetState(MoldPartState.Free, snapToAssembledPose: false);
            }
            finally
            {
                MoldPart.SuppressStateChangeAudio = prevSuppress;
            }
        }

        TightenProgressChanged?.Invoke(this, 0f, false);
        SyncWasherImmediate();
    }

    internal void DetachCaptiveAndSeatIntoSlot(ScrewSlot targetSlot)
    {
        EnsureBound();
        _captiveHostRod = null;
        if (Part != null)
            Part.SetCaptiveFollowerMode(false);

        if (targetSlot != null)
            targetSlot.SeatCaptiveScrewFromInstalledRod(this);
    }

    private bool CanRemoveByHand(MoldPart part)
    {
        // Captive nuts stay on the tips of their rods; operators grab & remove the rod itself once loosened.
        return currentSlot == null && _captiveHostRod == null;
    }

    private bool OnReleasedInFreeState(MoldPart part)
    {
        if (_captiveHostRod != null)
            return false;

        return ScrewSlot.TryInsertReleasedScrewToAnySlot(this);
    }

    private void OnMoldPartStateChanged(MoldPart part, MoldPartState previousState, MoldPartState newState)
    {
        if (newState == MoldPartState.Free && currentSlot != null)
        {
            ScrewSlot prevSlot = currentSlot;
            currentSlot = null;
            tightenProgress = 0f;
            _cumulativeSpinDegrees = 0f;
            _wasInsertedLooseByUser = false;
            prevSlot.NotifySeatedScrewRemoved(this);
            TightenProgressChanged?.Invoke(this, 0f, false);
        }

        if (_nutWasher != null)
            _nutWasher.SyncWasherPose();
    }

    private void RefreshPoseAndLockFromProgress(bool invokeEvents)
    {
        EnsureBound();
        if (currentSlot == null || Part == null)
            return;

        _captiveHostRod = null;
        if (Part.IsCaptiveFollower)
            Part.SetCaptiveFollowerMode(false);

        Vector3 targetPos = currentSlot.EvaluateWorldPosition(tightenProgress);
        // Negate spin angle around the inward-pointing LocalRotationAxis so right-hand-thread fasteners
        // visually rotate CLOCKWISE when tightening (+progress) and ANTI-CLOCKWISE when loosening (-progress).
        Quaternion spinRot = Quaternion.AngleAxis(-_cumulativeSpinDegrees, LocalRotationAxis);
        Quaternion targetRot = currentSlot.CanonicalWorldRotation * spinRot;

        MoldPartState desiredPartState = tightenProgress >= tighteningCompletionThreshold
            ? MoldPartState.Installed
            : MoldPartState.LooseSeated;

        Part.SnapToWorldPose(targetPos, targetRot, desiredPartState);

        // Keep captive nuts locked onto the rod so grabbing at the rod always removes the rod with its nuts attached!
        Part.SetDependencyLocked(true);
        currentSlot.RefreshHostRodRetention();

        if (_nutWasher != null)
        {
            _nutWasher.EnsureWasherVisual();
            _nutWasher.SyncWasherPose();
        }

        if (invokeEvents)
            TightenProgressChanged?.Invoke(this, tightenProgress, IsTightened);
    }

    private void EnsureBound()
    {
        if (_moldPart == null)
            _moldPart = GetComponent<MoldPart>();

        if (_moldPart != null)
        {
            _moldPart.TrySnapOnReleaseCallback = OnReleasedInFreeState;
            _moldPart.CanBeRemovedPredicate = CanRemoveByHand;
        }

        if (_nutWasher == null)
        {
            _nutWasher = GetComponent<NutWasher>();
            if (_nutWasher == null && Application.isPlaying)
                _nutWasher = gameObject.AddComponent<NutWasher>();
        }
    }
}
