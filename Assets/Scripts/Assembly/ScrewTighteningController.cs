using System;
using UnityEngine;

/// <summary>
/// Coordinates strict front-of-bolt impact wrench insertion, coaxial socket-over-nut seating
/// (so the deep hex socket head completely covers the nut while tightening and loosening),
/// knob-selected rotation direction (Tighten CW vs Loosen CCW), tactile operator feedback effects
/// (impact vibration, haptics, procedural impact & torque-lock audio, torque flash ring),
/// and synchronized nut/socket travel.
/// </summary>
[DisallowMultipleComponent]
public class ScrewTighteningController : MonoBehaviour
{
    [Header("Screwdriver / Impact Wrench References")]
    [Tooltip("Motor & direction controller on the impact wrench.")]
    [SerializeField] private XRElectricScrewdriver screwdriver;

    [Tooltip("Interchangeable-head socket (optional when built-in Impact_Hex_Socket is used).")]
    [SerializeField] private ScrewdriverHeadAttachment headSocket;

    [Tooltip("Visual child rig containing the wrench housing, chuck, socket cup, and knob for jitter-free coaxial nut seating.")]
    [SerializeField] private Transform wrenchVisualRig;

    [Tooltip("Local Y coordinate (in unscaled root space) of the hollow Impact_Hex_Socket cavity center that covers the nut.")]
    [SerializeField] private float socketCavityLocalY = 0.153f;

    [Header("Front-of-Bolt Engagement Rules")]
    [Tooltip("Maximum outward distance (meters) in front of the nut along the bolt tip axis to engage.")]
    [SerializeField] private float frontEngageMaxDistance = 0.145f;

    [Tooltip("Maximum radial distance (meters) off the bolt shaft centerline when inserting through the front of the bolt.")]
    [SerializeField] private float frontRadialTolerance = 0.075f;

    [Tooltip("Maximum angle (degrees) between the wrench nose forward axis and the inward bolt axis when inserting from the front.")]
    [Range(15f, 85f)]
    [SerializeField] private float maxAxialAlignmentAngle = 65f;

    [Tooltip("Legacy contact radius kept for inspector compatibility.")]
    [SerializeField] private float contactRadius = 0.085f;

    [Tooltip("Ratio of fastener degrees turned per degree of socket rotation (1.0 = direct 1:1 socket-to-nut coupling).")]
    [SerializeField] private float driveGearRatio = 1.0f;

    [Header("Touch / Front-Insert Drive (XR Simulator & VR)")]
    [Tooltip("When enabled, inserting the wrench head straight onto the front of a bolt drives in the direction selected by the wrench's Direction Knob.")]
    [SerializeField] private bool enableTouchAssistWithoutTrigger = true;

    [Tooltip("Socket/nut angular speed (degrees/sec) during front-insert operation.")]
    [SerializeField] private float touchAssistDegreesPerSec = 1080f;

    [Tooltip("Enables simulator-friendly front-insertion drive without requiring analog trigger squeeze.")]
    [SerializeField] private bool touchScrewdriverToDismantle = true;

    [Tooltip("When false (default), completely loosened nuts stay seated on the tip of the rod instead of falling down.")]
    [SerializeField] private bool autoPopOffScrewWhenUnscrewed = false;

    [Tooltip("Allows the built-in Impact_Hex_Socket to drive both tie-rod nuts and side flange nuts without manual bit swapping.")]
    [SerializeField] private bool bypassBitCheckInTouchDismantle = true;

    [SerializeField] private float simulatorTouchContactDistance = 0.145f;

    private const float PassiveEngageSettleSeconds = 0.075f;
    private const float WalkBlockPlanarSpeed = 0.08f;
    private const float WalkDisengagePlanarSpeed = 0.25f;
    private const float WalkBlockAngularSpeedDeg = 18f;

    private static AudioClip s_impactTickClip;
    private static AudioClip s_torqueLockClip;

    private AudioSource _fxAudioSource;
    private ScrewInteraction _engagedScrew;
    private ScrewInteraction _completedTouchAssistScrew;
    private MoldPart _lastPoppedHostBolt;
    private float _blockHostBoltPopUntil = -10f;
    private MoldPart _engagedVerticalBolt;
    private float _verticalBoltTouchTimer;
    private float _nextImpactPulseTime;
    private bool _isDrivingThisFrame;
    private Transform _playerRigTransform;
    private Vector3 _lastRigWorldPos;
    private Quaternion _lastRigWorldRot = Quaternion.identity;
    private bool _hasRigPoseSample;
    private float _smoothedRigSpeed;
    private float _smoothedRigAngularSpeed;
    private ScrewInteraction _pendingEngageCandidate;
    private float _pendingEngageDwellSeconds;
    private bool _isVisualRigReturning;

    // Torque-lock visual burst ring at socket mouth
    private Transform _torqueBurstRing;
    private Renderer _torqueBurstRenderer;
    private MaterialPropertyBlock _burstMpb;
    private float _torqueBurstTimer;

    private int _lastFeedbackKind; // 0=none, 1=incompatible, 2=misaligned/non-front, 3=stage-blocked, 4=knob-direction
    private ScrewInteraction _lastFeedbackScrew;
    private ScrewdriverHead _lastFeedbackHead;
    private float _lastFeedbackTime = -10f;

    /// <summary>Raised when a fastener reaches its configured 100% tightening threshold.</summary>
    public event Action<ScrewInteraction> ScrewFullyTightened;

    /// <summary>Raised when a fastener is loosened outward to its hand-removable threshold (or popped free).</summary>
    public event Action<ScrewInteraction> ScrewLoosenedToRemovable;

    /// <summary>Raised when an incompatible head approaches a seated fastener.</summary>
    public event Action<ScrewdriverHead, ScrewInteraction, string> IncompatibleToolDetected;

    /// <summary>Raised when the wrench approaches a bolt from the side/back instead of straight through the front of the bolt.</summary>
    public event Action<ScrewInteraction, float> MisalignedToolDetected;

    /// <summary>Raised when forward tightening is blocked by stage gating or when the Direction Knob is set to the wrong direction.</summary>
    public event Action<ScrewInteraction, string> TighteningStageBlocked;

    /// <summary>Optional external stage-gate validator evaluated before forward tightening (+degrees); returns null/empty when allowed.</summary>
    public Func<ScrewInteraction, string> ExternalTightenGateValidator { get; set; }

    public static ScrewTighteningController ActiveInstance { get; private set; }

    public ScrewInteraction EngagedScrew => _engagedScrew;

    public bool IsReverseDirectionActive => screwdriver != null && screwdriver.ReverseDirection;

    /// <summary>
    /// Returns true while the screwdriver/impact-wrench socket head is touching or engaged on the given seated fastener.
    /// Used by NutTightnessIndicator so tightness badges only appear when the screwdriver touches a nut.
    /// </summary>
    public bool IsScrewdriverTouchingFastener(ScrewInteraction screw)
    {
        if (screwdriver != null && !screwdriver.IsHeld && !screwdriver.EditorTestRunMotor)
            return false;

        if (screw == null || screw.Part == null || screw.Part.IsFree || !screw.IsSeated)
            return false;

        if (_engagedScrew == screw)
            return true;

        if (IsRigWalking)
            return false;

        Vector3 nutCenter = MoldSnapPoint.GetPartWorldGeometricCenter(screw.Part);
        bool isVerticalSlot = IsVerticalSideFastenerSlot(screw.CurrentSlot);
        if (isVerticalSlot)
        {
            float dTop = GetClosestDistanceToWrenchFront(nutCenter);
            float dShaft = GetClosestDistanceToWrenchFront(nutCenter - Vector3.up * 0.045f);
            if (Mathf.Min(dTop, dShaft) <= 0.195f)
                return true;
        }

        float touchRad = Mathf.Max(0.125f, contactRadius);
        float touchRadiusSq = touchRad * touchRad;

        Vector3 rawSocket = GetRawUnseatedSocketWorldPosition();
        if ((rawSocket - nutCenter).sqrMagnitude <= touchRadiusSq)
            return true;

        Transform toolTr = screwdriver != null ? screwdriver.transform : transform;
        if (screw.CurrentSlot != null &&
            Vector3.Dot(toolTr.position - nutCenter, screw.CurrentSlot.WorldLoosenDirection) >= 0f &&
            GetClosestDistanceToWrenchFront(nutCenter) <= touchRad)
        {
            return true;
        }

        return false;
    }

    public bool EnableTouchAssistWithoutTrigger
    {
        get => enableTouchAssistWithoutTrigger;
        set => enableTouchAssistWithoutTrigger = value;
    }

    public bool TouchScrewdriverToDismantle
    {
        get => touchScrewdriverToDismantle;
        set => touchScrewdriverToDismantle = value;
    }

    public bool AutoPopOffScrewWhenUnscrewed
    {
        get => autoPopOffScrewWhenUnscrewed;
        set => autoPopOffScrewWhenUnscrewed = value;
    }

    public bool BypassBitCheckInTouchDismantle
    {
        get => bypassBitCheckInTouchDismantle;
        set => bypassBitCheckInTouchDismantle = value;
    }

    private void Awake()
    {
        ActiveInstance = this;
        AutoAssignReferences();
        // Force loosened nuts to stay seated on the tip of the rod instead of falling down (overrides serialized scene flag).
        autoPopOffScrewWhenUnscrewed = false;
        frontEngageMaxDistance = Mathf.Clamp(frontEngageMaxDistance, 0.10f, 0.165f);
        frontRadialTolerance = Mathf.Clamp(frontRadialTolerance, 0.05f, 0.085f);
        maxAxialAlignmentAngle = Mathf.Clamp(maxAxialAlignmentAngle, 35f, 68f);
        simulatorTouchContactDistance = Mathf.Clamp(simulatorTouchContactDistance, 0.10f, 0.165f);
        EnsureEffectsInitialized();
    }

    private void OnEnable()
    {
        ActiveInstance = this;
    }

    private void Reset()
    {
        AutoAssignReferences();
    }

    private void OnDisable()
    {
        if (ActiveInstance == this)
            ActiveInstance = null;

        SetEngagedScrew(null);
        _pendingEngageCandidate = null;
        _pendingEngageDwellSeconds = 0f;
        ResetWrenchVisualRigImmediate();
        ClearFeedbackLatch();
        _completedTouchAssistScrew = null;
    }

    private void OnDestroy()
    {
        if (_torqueBurstRing != null)
        {
            if (Application.isPlaying) Destroy(_torqueBurstRing.gameObject);
            else DestroyImmediate(_torqueBurstRing.gameObject);
        }
    }

    private void Update()
    {
        _isDrivingThisFrame = false;
        StepFastening(Time.deltaTime);
        UpdateTorqueBurstVisual(Time.deltaTime);
    }

    private void LateUpdate()
    {
        UpdateWrenchVisualSeating(Time.deltaTime);
    }

    private bool IsRigWalking =>
        Application.isPlaying && (_smoothedRigSpeed > WalkBlockPlanarSpeed || _smoothedRigAngularSpeed > WalkBlockAngularSpeedDeg);

    private void UpdateRigMotionState(float deltaTime)
    {
        if (!Application.isPlaying || deltaTime <= 1e-5f)
            return;

        if (_playerRigTransform == null)
        {
            var xrOrigin = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
            if (xrOrigin != null)
            {
                _playerRigTransform = xrOrigin.Origin != null ? xrOrigin.Origin.transform : xrOrigin.transform;
            }
            else
            {
                var cc = FindAnyObjectByType<CharacterController>();
                if (cc != null)
                    _playerRigTransform = cc.transform;
            }
        }

        if (_playerRigTransform == null)
        {
            _smoothedRigSpeed = 0f;
            _smoothedRigAngularSpeed = 0f;
            return;
        }

        Vector3 pos = _playerRigTransform.position;
        Quaternion rot = _playerRigTransform.rotation;
        if (!_hasRigPoseSample)
        {
            _lastRigWorldPos = pos;
            _lastRigWorldRot = rot;
            _hasRigPoseSample = true;
            _smoothedRigSpeed = 0f;
            _smoothedRigAngularSpeed = 0f;
            return;
        }

        float instSpeed = Vector3.Distance(pos, _lastRigWorldPos) / deltaTime;
        float instAng = Quaternion.Angle(rot, _lastRigWorldRot) / deltaTime;
        _lastRigWorldPos = pos;
        _lastRigWorldRot = rot;

        float alpha = Mathf.Clamp01(deltaTime * 20f);
        _smoothedRigSpeed = instSpeed > _smoothedRigSpeed
            ? instSpeed
            : Mathf.Lerp(_smoothedRigSpeed, instSpeed, alpha);
        _smoothedRigAngularSpeed = instAng > _smoothedRigAngularSpeed
            ? instAng
            : Mathf.Lerp(_smoothedRigAngularSpeed, instAng, alpha);
    }

    /// <summary>
    /// Evaluates strict front-of-bolt insertion, direction-knob setting, and synchronized nut/socket motion over deltaTime.
    /// </summary>
    public void StepFastening(float deltaTime)
    {
        if (screwdriver == null || deltaTime <= 0f)
            return;

        UpdateRigMotionState(deltaTime);
        AutoAssignReferences();

        // Never engage or keep the visual rig stuck on a bolt when the screwdriver is released/unheld.
        if (!screwdriver.IsHeld && !screwdriver.EditorTestRunMotor)
        {
            SetEngagedScrew(null);
            _pendingEngageCandidate = null;
            _pendingEngageDwellSeconds = 0f;
            _completedTouchAssistScrew = null;
            _engagedVerticalBolt = null;
            _verticalBoltTouchTimer = 0f;
            ClearFeedbackLatch();
            return;
        }

        bool explicitTriggerPull = screwdriver.IsTriggerActivated || screwdriver.EditorTestRunMotor;

        if (_engagedScrew != null && Application.isPlaying && _smoothedRigSpeed > WalkDisengagePlanarSpeed && !explicitTriggerPull)
        {
            SetEngagedScrew(null);
            _pendingEngageCandidate = null;
            _pendingEngageDwellSeconds = 0f;
            _engagedVerticalBolt = null;
            _verticalBoltTouchTimer = 0f;
            ClearFeedbackLatch();
            return;
        }

        if (_engagedScrew == null && IsRigWalking && !explicitTriggerPull)
        {
            _pendingEngageCandidate = null;
            _pendingEngageDwellSeconds = 0f;
            _engagedVerticalBolt = null;
            _verticalBoltTouchTimer = 0f;
            ClearFeedbackLatch();
            return;
        }

        ScrewdriverHead activeHead = headSocket != null ? headSocket.CurrentHead : null;

        Vector3 rawSocketPos = GetRawUnseatedSocketWorldPosition();
        Vector3 rawForward = GetRawUnseatedToolForward();

        ScrewInteraction frontCandidate = FindBestFrontInsertedFastener(
            rawSocketPos,
            rawForward,
            out ScrewInteraction nonFrontNearbyScrew,
            out float nonFrontAngleDeg);

        if (_completedTouchAssistScrew != null && frontCandidate != _completedTouchAssistScrew)
            _completedTouchAssistScrew = null;

        if (frontCandidate == null)
        {
            SetEngagedScrew(null);
            _pendingEngageCandidate = null;
            _pendingEngageDwellSeconds = 0f;

            if (nonFrontNearbyScrew != null)
            {
                // Operator tried to insert from behind/sideways instead of the front of the bolt!
                EmitFrontInsertionRejected(nonFrontNearbyScrew, nonFrontAngleDeg);
                _engagedVerticalBolt = null;
                _verticalBoltTouchTimer = 0f;
                return;
            }

            ClearFeedbackLatch();

            // Allow backing out an unlocked vertical side bolt during Dismantling (once all horizontal rods are detached!),
            // or guiding the operator if touching an un-nutted vertical bolt during Reassembly.
            if (touchScrewdriverToDismantle && TryHandleVerticalBoltTouch(rawSocketPos, rawForward, deltaTime))
                return;

            _engagedVerticalBolt = null;
            _verticalBoltTouchTimer = 0f;
            if (!screwdriver.ReverseDirection)
                TryNotifyInstalledVerticalBoltMissingNut(rawSocketPos, deltaTime);
            return;
        }

        _engagedVerticalBolt = null;
        _verticalBoltTouchTimer = 0f;

        bool bypassChecks = touchScrewdriverToDismantle && bypassBitCheckInTouchDismantle;
        if (!bypassChecks)
        {
            if (activeHead == null)
            {
                SetEngagedScrew(null);
                _pendingEngageCandidate = null;
                _pendingEngageDwellSeconds = 0f;
                EmitIncompatibleFeedback(null, frontCandidate, "No socket head installed on the impact wrench.");
                return;
            }

            if (!frontCandidate.IsHeadCompatible(activeHead))
            {
                SetEngagedScrew(null);
                _pendingEngageCandidate = null;
                _pendingEngageDwellSeconds = 0f;
                EmitIncompatibleFeedback(
                    activeHead,
                    frontCandidate,
                    $"Incompatible socket '{activeHead.DisplayName}' for '{frontCandidate.Part.DisplayName}'.");
                return;
            }
        }

        ScrewSlot slot = frontCandidate.CurrentSlot;
        if (slot == null)
        {
            SetEngagedScrew(null);
            _pendingEngageCandidate = null;
            _pendingEngageDwellSeconds = 0f;
            ClearFeedbackLatch();
            return;
        }

        Transform moldRoot = frontCandidate.Part != null ? frontCandidate.Part.MoldContainer : null;
        MoldAssemblyManager mgr = moldRoot != null
            ? moldRoot.GetComponent<MoldAssemblyManager>()
            : null;
        bool isReassembling = mgr != null && mgr.WorkflowPhase == AssemblyWorkflowPhase.Reassembling;
        bool isDismantling = mgr == null || mgr.WorkflowPhase == AssemblyWorkflowPhase.Dismantling;
        bool isVerticalSlot = IsVerticalSideFastenerSlot(slot);

        // Enforce Fix 1: Small vertical side nuts/rods can ONLY be unscrewed/removed after ALL 6 horizontal rods are detached!
        if (isVerticalSlot && mgr != null && !mgr.AreAllHorizontalRodsDetached(out int remainingHorizRods))
        {
            bool wantsToDismantleVertical = (isDismantling && !frontCandidate.WasInsertedLooseByUser) ||
                                            (screwdriver.ReverseDirection && !(isReassembling || frontCandidate.WasInsertedLooseByUser));
            if (wantsToDismantleVertical)
            {
                SetEngagedScrew(null);
                _pendingEngageCandidate = null;
                _pendingEngageDwellSeconds = 0f;
                EmitStageBlockedFeedback(
                    frontCandidate,
                    $"Sequence Lock: Detach all 6 horizontal rods first ({remainingHorizRods} remaining) before unscrewing the small vertical rods!");
                return;
            }
        }

        // Strictly obey the screwdriver's active switch / Tab rotation direction:
        // ReverseDirection == false -> Clockwise (+1, Tighten)
        // ReverseDirection == true  -> Anti-Clockwise (-1, Loosen)
        float knobDirectionSign = screwdriver.ReverseDirection ? -1f : +1f;

        if (isVerticalSlot && knobDirectionSign < 0f && mgr != null && !mgr.AreAllHorizontalRodsDetached(out int horizRemAfterKnob))
        {
            SetEngagedScrew(null);
            _pendingEngageCandidate = null;
            _pendingEngageDwellSeconds = 0f;
            EmitStageBlockedFeedback(
                frontCandidate,
                $"Sequence Lock: Detach all 6 horizontal rods first ({horizRemAfterKnob} remaining) before unscrewing the small vertical rods!");
            return;
        }

        if (_completedTouchAssistScrew == frontCandidate &&
            ((knobDirectionSign > 0f && frontCandidate.TightenProgress < 0.995f) ||
             (knobDirectionSign < 0f && frontCandidate.TightenProgress > 0.005f)))
        {
            _completedTouchAssistScrew = null;
        }

        // Reject wrong-direction rotation BEFORE seating so the wrench never sticks onto a nut when set to the opposite direction!
        if (CheckAndReportKnobDirectionMismatch(frontCandidate, knobDirectionSign, isDismantling, isReassembling))
        {
            SetEngagedScrew(null);
            _pendingEngageCandidate = null;
            _pendingEngageDwellSeconds = 0f;
            return;
        }

        // Once a nut has reached 100% tightened (CW) or 0% loose on the rod tip (CCW), keep the screwdriver detached
        // (even while spinning continuously via Tab or VR Trigger) so it never stays stuck on the nut!
        bool alreadyFinishedTightening = knobDirectionSign > 0f && frontCandidate.TightenProgress >= 0.995f;
        bool alreadyFinishedLoosening = knobDirectionSign < 0f && frontCandidate.TightenProgress <= 0.005f;
        if (alreadyFinishedTightening || alreadyFinishedLoosening)
        {
            if (alreadyFinishedLoosening)
            {
                if (frontCandidate.TightenProgress > 0f)
                    frontCandidate.SetTightenProgress(0f);

                if (autoPopOffScrewWhenUnscrewed && isDismantling)
                    PopUnscrewedFastenerOffThreads(frontCandidate, slot);
            }

            SetEngagedScrew(null);
            _pendingEngageCandidate = null;
            _pendingEngageDwellSeconds = 0f;
            ClearFeedbackLatch();
            return;
        }

        if (_engagedScrew == null && !explicitTriggerPull && Application.isPlaying)
        {
            if (_pendingEngageCandidate != frontCandidate)
            {
                _pendingEngageCandidate = frontCandidate;
                _pendingEngageDwellSeconds = 0f;
                return;
            }

            _pendingEngageDwellSeconds += deltaTime;
            if (_pendingEngageDwellSeconds < PassiveEngageSettleSeconds)
                return;
        }

        _pendingEngageCandidate = null;
        _pendingEngageDwellSeconds = 0f;

        // Valid front-of-bolt engagement! Seat the wrench socket coaxially over the nut while actively driving.
        if (_engagedScrew != frontCandidate)
        {
            SetEngagedScrew(frontCandidate);
            _completedTouchAssistScrew = null;
        }

        // Mode A: Motor Running (VR Trigger or XR Simulator Tab Continuous Spin)
        if (screwdriver.IsWorking)
        {
            _completedTouchAssistScrew = null;
            float bitSignedDegrees = Mathf.Abs(screwdriver.GetLiveSignedAngularSpeed()) * knobDirectionSign * deltaTime;

            ClearFeedbackLatch();
            ScrewInteraction activeScrew = _engagedScrew;
            bool stopped = ApplySynchronizedDrive(activeScrew, bitSignedDegrees, alsoSpinBitExternally: true);
            if (stopped)
            {
                _completedTouchAssistScrew = activeScrew;
                if (knobDirectionSign < 0f)
                {
                    if (activeScrew != null && activeScrew.IsSeated && activeScrew.TightenProgress > 0f)
                        activeScrew.SetTightenProgress(0f);

                    if (autoPopOffScrewWhenUnscrewed && isDismantling)
                        PopUnscrewedFastenerOffThreads(activeScrew, slot);
                }
                SetEngagedScrew(null);
            }
            return;
        }

        // Mode B: Front-Insertion Auto-Drive (uses the exact Switch / Tab direction!)
        if ((enableTouchAssistWithoutTrigger || touchScrewdriverToDismantle) && _completedTouchAssistScrew != _engagedScrew)
        {
            ClearFeedbackLatch();
            float assistBitDegrees = knobDirectionSign * touchAssistDegreesPerSec * deltaTime;
            bool hitStop = ApplySynchronizedDrive(_engagedScrew, assistBitDegrees, alsoSpinBitExternally: true);
            if (hitStop)
            {
                ScrewInteraction finishedScrew = _engagedScrew;
                _completedTouchAssistScrew = finishedScrew;

                if (knobDirectionSign < 0f)
                {
                    if (finishedScrew != null && finishedScrew.IsSeated && finishedScrew.TightenProgress > 0f)
                        finishedScrew.SetTightenProgress(0f);

                    if (autoPopOffScrewWhenUnscrewed && isDismantling)
                        PopUnscrewedFastenerOffThreads(finishedScrew, slot);
                }

                // Detach the screwdriver from the nut immediately once screwing finishes!
                SetEngagedScrew(null);
            }
        }
    }

    private bool CheckAndReportKnobDirectionMismatch(
        ScrewInteraction screw,
        float knobSign,
        bool isDismantling,
        bool isReassembling)
    {
        if (screw == null)
            return false;

        // Switch is on CLOCKWISE / TIGHTEN (+1), nut is already 100% tight, and operator is trying to dismantle
        if (knobSign > 0f && screw.TightenProgress >= 0.995f && isDismantling)
        {
            EmitKnobDirectionFeedback(
                screw,
                "Screwdriver is running CLOCKWISE (Tighten) — press TAB (or flip Wrench Switch) to rotate ANTI-CLOCKWISE to loosen!");
            return true;
        }

        // Switch is on ANTI-CLOCKWISE / LOOSEN (-1), nut is at 0% loose, and operator placed it to tighten (or is in Reassembling phase)
        if (knobSign < 0f && screw.TightenProgress <= 0.01f && (isReassembling || screw.WasInsertedLooseByUser))
        {
            EmitKnobDirectionFeedback(
                screw,
                "Screwdriver is rotating ANTI-CLOCKWISE (Loosen) — press TAB (or flip Wrench Switch) to run CLOCKWISE to tighten!");
            return true;
        }

        return false;
    }

    private static bool IsVerticalSideFastenerSlot(ScrewSlot slot)
    {
        return slot != null && Mathf.Abs(slot.WorldTightenDirection.y) > 0.7f;
    }

    private static float ClosestDistancePointToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float lenSq = ab.sqrMagnitude;
        if (lenSq < 1e-8f)
            return Vector3.Distance(p, a);
        float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / lenSq);
        return Vector3.Distance(p, a + ab * t);
    }

    private float GetClosestDistanceToWrenchFront(Vector3 worldTarget)
    {
        // Always measure against the raw controller-held screwdriver transform (never wrenchVisualRig),
        // otherwise seating wrenchVisualRig onto a nut creates a 0-distance feedback loop that traps detachment!
        Transform tr = screwdriver != null ? screwdriver.transform : transform;
        Vector3 pChuck = tr.TransformPoint(new Vector3(0f, 0.070f, 0f));
        Vector3 pTip = tr.TransformPoint(new Vector3(0f, socketCavityLocalY + 0.050f, 0f));
        return ClosestDistancePointToSegment(worldTarget, pChuck, pTip);
    }

    private float GetClosestDistanceToWrenchFrontSegment(Vector3 segA, Vector3 segB)
    {
        float d0 = GetClosestDistanceToWrenchFront(segA);
        float dMid = GetClosestDistanceToWrenchFront((segA + segB) * 0.5f);
        float d1 = GetClosestDistanceToWrenchFront(segB);
        return Mathf.Min(d0, Mathf.Min(dMid, d1));
    }

    /// <summary>
    /// Evaluates all seated nuts.
    /// - Horizontal tie-rod bolts strictly require front-of-bolt coaxial insertion (along slot.WorldLoosenDirection).
    /// - Recessed vertical side flange bolts (which sit directly underneath the bottom horizontal tie rods against the wall)
    ///   accept ergonomic exterior front/side/angled wrench contact so they detect effortlessly for both loosening and tightening.
    /// </summary>
    private ScrewInteraction FindBestFrontInsertedFastener(
        Vector3 rawSocketPos,
        Vector3 rawToolForward,
        out ScrewInteraction rejectedNonFrontScrew,
        out float rejectedAngleDeg)
    {
        ScrewInteraction bestValid = null;
        float bestScore = float.MaxValue;

        rejectedNonFrontScrew = null;
        rejectedAngleDeg = 0f;
        float closestRejectedDist = float.MaxValue;

        float effectiveMaxFront = Mathf.Max(frontEngageMaxDistance, simulatorTouchContactDistance);
        float effectiveRadialTol = frontRadialTolerance;
        float effectiveMaxAngle = maxAxialAlignmentAngle;
        Transform toolTr = screwdriver != null ? screwdriver.transform : transform;
        var slots = ScrewSlot.AllActive;

        for (int i = 0; i < slots.Count; i++)
        {
            ScrewSlot slot = slots[i];
            if (slot == null || !slot.isActiveAndEnabled || !slot.IsOccupied)
                continue;

            ScrewInteraction screw = slot.SeatedScrew;
            if (screw == null || screw.Part == null)
                continue;

            Vector3 nutCenter = MoldSnapPoint.GetPartWorldGeometricCenter(screw.Part);
            Vector3 frontOutward = slot.WorldLoosenDirection;   // Points out from bolt tip (front of bolt)
            Vector3 inwardAxis = slot.WorldTightenDirection;    // Points inward along bolt shaft

            // Slight hysteresis while actively driving so socket seating stays steady without sticking when pulled away
            bool isAlreadyEngaged = (_engagedScrew == screw);
            float hysteresis = isAlreadyEngaged ? 1.05f : 1.0f;

            if (IsVerticalSideFastenerSlot(slot))
            {
                // Vertical small side rod + nut: check proximity from the raw front chuck/socket nose to the vertical nut + shaft!
                Vector3 targetBottom = slot.TightenedWorldGeometricCenter - Vector3.up * 0.065f;
                Vector3 targetTop = slot.LooseWorldGeometricCenter + Vector3.up * 0.030f;
                float segDist = Mathf.Min(
                    GetClosestDistanceToWrenchFront(nutCenter),
                    GetClosestDistanceToWrenchFrontSegment(targetBottom, targetTop));
                float rootDist = (toolTr.position - nutCenter).magnitude;

                float centerZ = -7.116f;
                Transform moldRoot = screw.Part.MoldContainer;
                if (moldRoot != null)
                {
                    Transform pallet = moldRoot.Find("Cube.004");
                    Renderer r = pallet != null ? pallet.GetComponent<Renderer>() : null;
                    if (r != null) centerZ = r.bounds.center.z;
                }
                float exteriorZSign = nutCenter.z >= centerZ ? 1f : -1f;
                bool validExteriorSide = ((rawSocketPos.z - nutCenter.z) * exteriorZSign) >= -0.095f;

                Vector3 toNut = nutCenter - toolTr.position;
                float dotTowardNut = toNut.sqrMagnitude > 1e-6f ? Vector3.Dot(rawToolForward, toNut.normalized) : 1f;
                float alignToDown = Vector3.Angle(rawToolForward, inwardAxis);
                bool validVerticalOrientation = dotTowardNut >= -0.25f || alignToDown <= 140f;

                if (segDist <= (0.145f * hysteresis) && rootDist <= (0.40f * hysteresis) && validExteriorSide && validVerticalOrientation)
                {
                    float score = segDist * 0.85f;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestValid = screw;
                    }
                }
                continue;
            }

            Vector3 rel = rawSocketPos - nutCenter;
            float dist = rel.magnitude;

            float frontProj = Vector3.Dot(rel, frontOutward);
            float handleFrontProj = Vector3.Dot(toolTr.position - nutCenter, frontOutward);
            float radialDist = (rel - frontOutward * frontProj).magnitude;
            float noseSegDist = GetClosestDistanceToWrenchFront(nutCenter);
            float alignAngle = Vector3.Angle(rawToolForward, inwardAxis);

            bool validAngle = alignAngle <= Mathf.Min(82f, effectiveMaxAngle * (isAlreadyEngaged ? 1.18f : 1.0f));
            bool validSocketFront = handleFrontProj >= 0.0f &&
                                    frontProj >= (-0.095f * hysteresis) &&
                                    frontProj <= (effectiveMaxFront * hysteresis) &&
                                    radialDist <= (effectiveRadialTol * hysteresis);
            bool validNoseContact = handleFrontProj >= 0.02f &&
                                    frontProj >= (-0.115f * hysteresis) &&
                                    noseSegDist <= (0.072f * hysteresis);

            if (validAngle && (validSocketFront || validNoseContact))
            {
                float score = Mathf.Min(radialDist, noseSegDist) * 2.0f + Mathf.Abs(frontProj) * 0.5f;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestValid = screw;
                }
            }
            else if (dist <= 0.135f && dist < closestRejectedDist)
            {
                closestRejectedDist = dist;
                rejectedNonFrontScrew = screw;
                rejectedAngleDeg = handleFrontProj < 0f ? 180f : Mathf.Max(alignAngle, radialDist * 900f);
            }
        }

        if (bestValid != null)
            rejectedNonFrontScrew = null;

        return bestValid;
    }

    private void UpdateWrenchVisualSeating(float deltaTime)
    {
        if (wrenchVisualRig == null || screwdriver == null)
            return;

        if (_engagedScrew != null && _engagedScrew.IsSeated && _engagedScrew.CurrentSlot != null && _engagedScrew.Part != null)
        {
            _isVisualRigReturning = true;
            ScrewSlot slot = _engagedScrew.CurrentSlot;
            Vector3 nutCenter = MoldSnapPoint.GetPartWorldGeometricCenter(_engagedScrew.Part);
            Vector3 inwardDir = slot.WorldTightenDirection; // Wrench local +Y aligns with inward bolt axis!

            // Determine natural local +Z (opposite of pistol handle -Z) perpendicular to inwardDir:
            // For horizontal tie rods, local +Z points upward (+Y) so the pistol grip hangs cleanly downward (-Y).
            // For vertical side bolts (inwardDir = -Y), local +Z points toward the mold center so the grip extends outward clear of the wall.
            Vector3 desiredHandleOpposite;
            Vector3 visualSocketAxis = inwardDir;
            if (Mathf.Abs(inwardDir.y) > 0.75f)
            {
                float centerZ = -7.116f;
                Transform moldRoot = _engagedScrew.Part.MoldContainer;
                if (moldRoot != null)
                {
                    Transform pallet = moldRoot.Find("Cube.004");
                    Renderer r = pallet != null ? pallet.GetComponent<Renderer>() : null;
                    if (r != null) centerZ = r.bounds.center.z;
                }
                float outwardZSign = nutCenter.z >= centerZ ? 1f : -1f;
                // Exact 90-degree vertical seating: socket/barrel (local +Y) points straight down (0, -1, 0) coaxially onto the vertical screw,
                // and pistol grip handle (local -Z) points straight outward (0, 0, +outwardZSign) at 90 degrees away from the mold wall!
                visualSocketAxis = Vector3.down;
                desiredHandleOpposite = new Vector3(0f, 0f, -outwardZSign);
            }
            else
            {
                Vector3 userUp = screwdriver.transform.rotation * Vector3.forward; // root local +Z in world
                Vector3 projected = Vector3.ProjectOnPlane(userUp, visualSocketAxis);
                if (projected.sqrMagnitude < 0.05f || projected.y < 0f)
                    projected = Vector3.ProjectOnPlane(Vector3.up, visualSocketAxis);
                desiredHandleOpposite = projected.normalized;
            }

            // Build rotation whose local +Y == visualSocketAxis and local +Z == desiredHandleOpposite
            Vector3 orthZ = Vector3.ProjectOnPlane(desiredHandleOpposite, visualSocketAxis).normalized;
            if (orthZ.sqrMagnitude < 1e-4f)
                orthZ = Vector3.ProjectOnPlane(Vector3.up, visualSocketAxis).normalized;

            Quaternion targetWorldRot = Quaternion.LookRotation(orthZ, visualSocketAxis);

            // Add subtle realistic impact-wrench rotary hammering vibration while actively torquing/loosening!
            if (_isDrivingThisFrame && Application.isPlaying)
            {
                float vib = Mathf.Sin(Time.unscaledTime * 95f) * 1.6f;
                targetWorldRot = targetWorldRot * Quaternion.Euler(0f, vib, vib * 0.35f);
            }

            Vector3 scaledLocalCavity = Vector3.Scale(new Vector3(0f, socketCavityLocalY, 0f), wrenchVisualRig.lossyScale);
            Vector3 targetWorldPos = nutCenter - (targetWorldRot * scaledLocalCavity);

            if (_isDrivingThisFrame && Application.isPlaying)
            {
                float axialKick = Mathf.Abs(Mathf.Sin(Time.unscaledTime * 55f)) * 0.0018f;
                targetWorldPos -= inwardDir * axialKick;
            }

            float blend = Application.isPlaying ? Mathf.Clamp01(deltaTime * 28f) : 1f;
            wrenchVisualRig.position = Vector3.Lerp(wrenchVisualRig.position, targetWorldPos, blend);
            wrenchVisualRig.rotation = Quaternion.Slerp(wrenchVisualRig.rotation, targetWorldRot, blend);
        }
        else
        {
            if (!Application.isPlaying || (!screwdriver.IsHeld && !screwdriver.EditorTestRunMotor) || !_isVisualRigReturning)
            {
                ResetWrenchVisualRigImmediate();
                return;
            }

            // Smoothly and briskly return visual rig to exact controller-held pose when disengaged
            float blend = Mathf.Clamp01(deltaTime * 35f);
            wrenchVisualRig.localPosition = Vector3.Lerp(wrenchVisualRig.localPosition, Vector3.zero, blend);
            wrenchVisualRig.localRotation = Quaternion.Slerp(wrenchVisualRig.localRotation, Quaternion.identity, blend);
            if (wrenchVisualRig.localPosition.sqrMagnitude < 0.0005f * 0.0005f &&
                Quaternion.Angle(wrenchVisualRig.localRotation, Quaternion.identity) < 0.1f)
            {
                ResetWrenchVisualRigImmediate();
            }
        }
    }

    private void ResetWrenchVisualRigImmediate()
    {
        _isVisualRigReturning = false;
        if (wrenchVisualRig != null)
        {
            wrenchVisualRig.localPosition = Vector3.zero;
            wrenchVisualRig.localRotation = Quaternion.identity;
        }
    }

    private Vector3 GetRawUnseatedSocketWorldPosition()
    {
        if (screwdriver != null)
            return screwdriver.transform.TransformPoint(new Vector3(0f, socketCavityLocalY, 0f));

        return transform.position;
    }

    private Vector3 GetRawUnseatedToolForward()
    {
        if (screwdriver != null)
            return (screwdriver.transform.rotation * Vector3.up).normalized;

        return transform.up;
    }

    private void PopUnscrewedFastenerOffThreads(ScrewInteraction screw, ScrewSlot slot)
    {
        if (screw == null || screw.Part == null || screw.Part.IsFree)
            return;

        MoldPart hostBolt = slot != null && slot.HostRodSnapPoint != null ? slot.HostRodSnapPoint.OccupiedPart : null;
        if (hostBolt != null && hostBolt.Category == MoldPartCategory.VerticalBolt)
        {
            _lastPoppedHostBolt = hostBolt;
            _blockHostBoltPopUntil = Time.unscaledTime + 0.55f;
        }

        Vector3 outwardDir = slot != null ? slot.WorldLoosenDirection : Vector3.right;
        Transform container = screw.Part.MoldContainer;

        screw.Part.ReleaseToFreeState();

        if (Mathf.Abs(outwardDir.y) > 0.7f)
        {
            float centerZ = -7.116f;
            if (container != null)
            {
                Transform pallet = container.Find("Cube.004");
                Renderer r = pallet != null ? pallet.GetComponent<Renderer>() : null;
                if (r != null) centerZ = r.bounds.center.z;
            }
            float zSign = screw.transform.position.z >= centerZ ? 1f : -1f;
            screw.transform.position += Vector3.up * 0.05f + new Vector3(0f, 0f, zSign * 0.16f);
        }
        else
        {
            screw.transform.position += outwardDir * 0.11f;
        }

        if (screw.Part.Body != null)
        {
            screw.Part.Body.position = screw.transform.position;
            screw.Part.Body.linearVelocity = Vector3.zero;
            screw.Part.Body.angularVelocity = Vector3.zero;
        }
        Physics.SyncTransforms();

        if (_engagedScrew == screw)
            SetEngagedScrew(null);
    }

    private bool TryHandleVerticalBoltTouch(Vector3 rawSocketPos, Vector3 rawForward, float deltaTime)
    {
        MoldPart bestBolt = null;
        MoldAssemblyManager bestMgr = null;
        float bestDist = float.MaxValue;

        var snaps = MoldSnapPoint.AllActive;
        for (int i = 0; i < snaps.Count; i++)
        {
            MoldSnapPoint sp = snaps[i];
            if (sp == null || !sp.isActiveAndEnabled || !sp.IsOccupied || sp.AcceptedCategory != MoldPartCategory.VerticalBolt)
                continue;

            MoldPart bolt = sp.OccupiedPart;
            if (bolt == null)
                continue;

            // If its companion vertical ScrewSlot still has a nut seated, FindBestFrontInsertedFastener handles it instead.
            bool hasSeatedNutOnThisBolt = false;
            var allSlots = ScrewSlot.AllActive;
            for (int s = 0; s < allSlots.Count; s++)
            {
                ScrewSlot sl = allSlots[s];
                if (sl != null && sl.isActiveAndEnabled && sl.HostRodSnapPoint == sp && sl.IsOccupied)
                {
                    hasSeatedNutOnThisBolt = true;
                    break;
                }
            }
            if (hasSeatedNutOnThisBolt)
                continue;

            MoldAssemblyManager mgr = bolt.MoldContainer != null
                ? bolt.MoldContainer.GetComponent<MoldAssemblyManager>()
                : null;
            if (mgr != null && mgr.WorkflowPhase != AssemblyWorkflowPhase.Dismantling)
                continue;

            if (bolt == _lastPoppedHostBolt && Application.isPlaying && Time.unscaledTime < _blockHostBoltPopUntil)
                continue;

            // Accept ergonomic exterior front/side/top contact along the vertical bolt shaft & head
            Vector3 boltCenter = MoldSnapPoint.GetPartWorldGeometricCenter(bolt);
            Vector3 boltBottom = boltCenter - Vector3.up * 0.04f;
            Vector3 boltTop = boltCenter + Vector3.up * 0.11f;
            float segDist = GetClosestDistanceToWrenchFrontSegment(boltBottom, boltTop);

            Transform toolTr = screwdriver != null ? screwdriver.transform : transform;
            Vector3 toBolt = boltCenter - toolTr.position;
            float dotTowardBolt = toBolt.sqrMagnitude > 1e-6f ? Vector3.Dot(rawForward, toBolt.normalized) : 1f;
            float angle = Vector3.Angle(rawForward, Vector3.down);

            if (segDist <= 0.19f && (dotTowardBolt >= -0.25f || angle <= 140f))
            {
                if (segDist < bestDist)
                {
                    bestDist = segDist;
                    bestBolt = bolt;
                    bestMgr = mgr;
                }
            }
        }

        if (bestBolt == null)
        {
            _engagedVerticalBolt = null;
            _verticalBoltTouchTimer = 0f;
            return false;
        }

        // Enforce Fix 1: Small vertical side rod can ONLY be removed after ALL 6 horizontal rods are detached!
        if (bestMgr != null && !bestMgr.AreAllHorizontalRodsDetached(out int horizRem))
        {
            _engagedVerticalBolt = null;
            _verticalBoltTouchTimer = 0f;
            EmitStageBlockedFeedback(
                null,
                $"Sequence Lock: Detach all 6 horizontal rods first ({horizRem} remaining) before removing '{bestBolt.DisplayName}'!");
            return true;
        }

        if (!screwdriver.ReverseDirection)
        {
            _engagedVerticalBolt = null;
            _verticalBoltTouchTimer = 0f;
            EmitKnobDirectionFeedback(
                null,
                $"Screwdriver is running CLOCKWISE (Tighten) — press TAB (or flip Wrench Switch) to rotate ANTI-CLOCKWISE to loosen '{bestBolt.DisplayName}'!");
            return true;
        }

        if (_engagedVerticalBolt != bestBolt)
        {
            _engagedVerticalBolt = bestBolt;
            _verticalBoltTouchTimer = 0f;
        }

        _isDrivingThisFrame = true;
        float stepDeg = -touchAssistDegreesPerSec * deltaTime;
        screwdriver.RotateHeadExternalStep(stepDeg);
        bestBolt.transform.Rotate(Vector3.up, stepDeg * 0.5f, Space.World);
        TriggerImpactDriveEffects(0.5f);
        _verticalBoltTouchTimer += deltaTime;

        if (_verticalBoltTouchTimer >= 0.22f)
        {
            Transform container = bestBolt.MoldContainer;
            float centerZ = -7.116f;
            if (container != null)
            {
                Transform pallet = container.Find("Cube.004");
                Renderer r = pallet != null ? pallet.GetComponent<Renderer>() : null;
                if (r != null) centerZ = r.bounds.center.z;
            }
            float zSign = bestBolt.transform.position.z >= centerZ ? 1f : -1f;

            bestBolt.ReleaseToFreeState();
            bestBolt.transform.position += Vector3.up * 0.08f + new Vector3(0.08f, 0f, zSign * 0.22f);
            if (bestBolt.Body != null)
            {
                bestBolt.Body.position = bestBolt.transform.position;
                bestBolt.Body.linearVelocity = Vector3.zero;
                bestBolt.Body.angularVelocity = Vector3.zero;
            }
            Physics.SyncTransforms();

            _engagedVerticalBolt = null;
            _verticalBoltTouchTimer = 0f;
        }

        return true;
    }

    private void TryNotifyInstalledVerticalBoltMissingNut(Vector3 rawSocketPos, float deltaTime)
    {
        var snaps = MoldSnapPoint.AllActive;
        for (int i = 0; i < snaps.Count; i++)
        {
            MoldSnapPoint sp = snaps[i];
            if (sp == null || !sp.isActiveAndEnabled || !sp.IsOccupied || sp.AcceptedCategory != MoldPartCategory.VerticalBolt)
                continue;

            // If its companion vertical ScrewSlot already has a nut seated, FindBestFrontInsertedFastener handles it.
            if (sp.HasActiveDependentsBlockingRemoval())
                continue;

            MoldPart bolt = sp.OccupiedPart;
            if (bolt == null)
                continue;

            Vector3 boltCenter = MoldSnapPoint.GetPartWorldGeometricCenter(bolt);
            float segDist = GetClosestDistanceToWrenchFrontSegment(boltCenter - Vector3.up * 0.04f, boltCenter + Vector3.up * 0.11f);
            if (segDist <= 0.175f)
            {
                screwdriver.RotateHeadExternalStep(touchAssistDegreesPerSec * deltaTime * 0.5f);
                TriggerImpactDriveEffects(0.85f);
                if (!Application.isPlaying || Time.unscaledTime - _lastFeedbackTime >= 1.2f)
                {
                    _lastFeedbackKind = 5;
                    _lastFeedbackTime = Time.unscaledTime;
                    TighteningStageBlocked?.Invoke(
                        null,
                        $"'{bolt.DisplayName}' is seated — place its small Vertical Hex Nut onto the top threads first, then touch with the wrench to tighten!");
                }
                return;
            }
        }
    }

    private bool ApplySynchronizedDrive(ScrewInteraction target, float requestedBitDegrees, bool alsoSpinBitExternally)
    {
        if (target == null || Mathf.Abs(requestedBitDegrees) < 1e-5f)
            return false;

        float requestedScrewDegrees = requestedBitDegrees * driveGearRatio;

        if (requestedScrewDegrees > 0f && ExternalTightenGateValidator != null)
        {
            string gateReason = ExternalTightenGateValidator.Invoke(target);
            if (!string.IsNullOrEmpty(gateReason))
            {
                EmitStageBlockedFeedback(target, gateReason);
                return false;
            }
        }

        bool wasTightened = target.IsTightened;
        bool wasLoose = target.IsLoose;

        float appliedScrewDegrees = target.ApplyRotationalDrive(requestedScrewDegrees);

        if (Mathf.Abs(appliedScrewDegrees) > 1e-5f)
        {
            _isDrivingThisFrame = true;
            if (alsoSpinBitExternally)
            {
                float syncedBitDegrees = Mathf.Abs(driveGearRatio) > 1e-5f
                    ? appliedScrewDegrees / driveGearRatio
                    : requestedBitDegrees;
                screwdriver.RotateHeadExternalStep(syncedBitDegrees);
            }

            TriggerImpactDriveEffects(target.TightenProgress);
        }

        if (!wasTightened && target.IsTightened)
        {
            TriggerTorqueCompleteBurst(target);
            ScrewFullyTightened?.Invoke(target);
        }
        else if (!wasLoose && target.IsLoose)
        {
            if (screwdriver != null)
                screwdriver.SendHapticPulse(0.65f, 0.08f);
            ScrewLoosenedToRemovable?.Invoke(target);
        }

        if (requestedScrewDegrees > 0f && target.TightenProgress >= 0.9999f)
            return true;
        if (requestedScrewDegrees < 0f && target.TightenProgress <= 0.0001f)
            return true;

        return false;
    }

    private void TriggerImpactDriveEffects(float normalizedProgress)
    {
        if (!Application.isPlaying)
            return;

        if (Time.unscaledTime < _nextImpactPulseTime)
            return;

        // Impact hammer cadence speeds up & intensifies slightly as torque builds toward 100%
        _nextImpactPulseTime = Time.unscaledTime + Mathf.Lerp(0.072f, 0.048f, normalizedProgress);

        float amp = Mathf.Lerp(0.35f, 0.80f, normalizedProgress);
        if (screwdriver != null)
            screwdriver.SendHapticPulse(amp, 0.032f);

        EnsureEffectsInitialized();
        if (_fxAudioSource != null && s_impactTickClip != null)
        {
            _fxAudioSource.pitch = Mathf.Lerp(0.92f, 1.22f, normalizedProgress);
            _fxAudioSource.PlayOneShot(s_impactTickClip, 0.55f);
        }
    }

    private void TriggerTorqueCompleteBurst(ScrewInteraction target)
    {
        if (!Application.isPlaying)
            return;

        if (screwdriver != null)
            screwdriver.SendHapticPulse(1.0f, 0.16f);

        EnsureEffectsInitialized();
        if (_fxAudioSource != null && s_torqueLockClip != null)
        {
            _fxAudioSource.pitch = 1.0f;
            _fxAudioSource.PlayOneShot(s_torqueLockClip, 0.95f);
        }

        if (_torqueBurstRing != null && target != null && target.Part != null)
        {
            Vector3 center = MoldSnapPoint.GetPartWorldGeometricCenter(target.Part);
            Vector3 axis = target.CurrentSlot != null ? target.CurrentSlot.WorldLoosenDirection : Vector3.up;
            _torqueBurstRing.position = center + axis * 0.015f;
            _torqueBurstRing.rotation = Quaternion.FromToRotation(Vector3.up, axis);
            _torqueBurstRing.gameObject.SetActive(true);
            _torqueBurstTimer = 0.35f;
        }
    }

    private void UpdateTorqueBurstVisual(float dt)
    {
        if (_torqueBurstRing == null || !_torqueBurstRing.gameObject.activeSelf)
            return;

        _torqueBurstTimer -= dt;
        if (_torqueBurstTimer <= 0f)
        {
            _torqueBurstRing.gameObject.SetActive(false);
            return;
        }

        float t = 1f - (_torqueBurstTimer / 0.35f);
        float radius = Mathf.Lerp(0.055f, 0.135f, t);
        _torqueBurstRing.localScale = new Vector3(radius, 0.003f, radius);

        if (_torqueBurstRenderer != null)
        {
            if (_burstMpb == null) _burstMpb = new MaterialPropertyBlock();
            Color c = new Color(0.18f, 0.96f, 0.45f, 1f - t);
            _burstMpb.SetColor("_BaseColor", c);
            _burstMpb.SetColor("_Color", c);
            _torqueBurstRenderer.SetPropertyBlock(_burstMpb);
        }
    }

    private void EnsureEffectsInitialized()
    {
        if (_fxAudioSource == null)
        {
            Transform knob = transform.Find("WrenchVisualRig/Direction_Selector_Knob");
            if (knob == null && screwdriver != null)
                knob = screwdriver.transform.Find("WrenchVisualRig/Direction_Selector_Knob");

            GameObject host = knob != null ? knob.gameObject : gameObject;
            _fxAudioSource = host.GetComponent<AudioSource>();
            if (_fxAudioSource == null)
            {
                _fxAudioSource = host.AddComponent<AudioSource>();
                _fxAudioSource.playOnAwake = false;
                _fxAudioSource.loop = false;
                _fxAudioSource.spatialBlend = 1f;
            }
        }

        if (s_impactTickClip == null)
            s_impactTickClip = CreateImpactHammerClip();

        if (s_torqueLockClip == null)
            s_torqueLockClip = CreateTorqueLockChimeClip();

        if (_torqueBurstRing == null && Application.isPlaying)
        {
            GameObject cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cyl.name = "__TorqueCompleteBurstRing";
            cyl.hideFlags = HideFlags.DontSave;
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
                cyl.transform.SetParent(runtimeContainer, false);
            }
            Collider c = cyl.GetComponent<Collider>();
            if (c != null) Destroy(c);
            _torqueBurstRenderer = cyl.GetComponent<Renderer>();
            if (_torqueBurstRenderer != null)
            {
                Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                if (sh != null)
                {
                    Material m = new Material(sh);
                    m.hideFlags = HideFlags.DontSave;
                    _torqueBurstRenderer.sharedMaterial = m;
                }
            }
            cyl.SetActive(false);
            _torqueBurstRing = cyl.transform;
        }
    }

    private static AudioClip CreateImpactHammerClip()
    {
        const int sr = 22050;
        int len = Mathf.RoundToInt(sr * 0.032f);
        float[] data = new float[len];
        uint seed = 1337u;
        for (int i = 0; i < len; i++)
        {
            float t = (float)i / sr;
            float env = Mathf.Exp(-t * 145f);
            seed = seed * 1664525u + 1013904223u;
            float noise = ((seed & 0xFFFF) / 32768f) - 1f;
            float anvil = Mathf.Sin(2f * Mathf.PI * 620f * t) * 0.65f + Mathf.Sin(2f * Mathf.PI * 1280f * t) * 0.25f;
            data[i] = (anvil + noise * 0.30f) * env * 0.8f;
        }
        AudioClip clip = AudioClip.Create("ImpactAnvilStrike", len, 1, sr, false);
        clip.hideFlags = HideFlags.DontSave;
        clip.SetData(data, 0);
        return clip;
    }

    private static AudioClip CreateTorqueLockChimeClip()
    {
        const int sr = 22050;
        int len = Mathf.RoundToInt(sr * 0.18f);
        float[] data = new float[len];
        for (int i = 0; i < len; i++)
        {
            float t = (float)i / sr;
            float env1 = Mathf.Exp(-t * 38f);
            float env2 = t >= 0.055f ? Mathf.Exp(-(t - 0.055f) * 28f) : 0f;
            float tone1 = Mathf.Sin(2f * Mathf.PI * 1175f * t) * env1 * 0.55f;
            float tone2 = Mathf.Sin(2f * Mathf.PI * 1760f * t) * env2 * 0.65f;
            data[i] = Mathf.Clamp(tone1 + tone2, -1f, 1f);
        }
        AudioClip clip = AudioClip.Create("TorqueReachedConfirm", len, 1, sr, false);
        clip.hideFlags = HideFlags.DontSave;
        clip.SetData(data, 0);
        return clip;
    }

    private void SetEngagedScrew(ScrewInteraction target)
    {
        _engagedScrew = target;
        if (screwdriver != null)
            screwdriver.SuppressAutoHeadSpin = target != null;
    }

    private void ClearFeedbackLatch()
    {
        _lastFeedbackKind = 0;
        _lastFeedbackScrew = null;
        _lastFeedbackHead = null;
    }

    private void EmitIncompatibleFeedback(ScrewdriverHead head, ScrewInteraction screw, string reason)
    {
        bool sameTarget = _lastFeedbackKind == 1 && _lastFeedbackScrew == screw && _lastFeedbackHead == head;
        if (sameTarget && (!Application.isPlaying || Time.unscaledTime - _lastFeedbackTime < 1.0f))
            return;

        _lastFeedbackKind = 1;
        _lastFeedbackScrew = screw;
        _lastFeedbackHead = head;
        _lastFeedbackTime = Time.unscaledTime;
        IncompatibleToolDetected?.Invoke(head, screw, reason);
    }

    private void EmitFrontInsertionRejected(ScrewInteraction screw, float angleDeg)
    {
        bool sameTarget = _lastFeedbackKind == 2 && _lastFeedbackScrew == screw;
        if (sameTarget && (!Application.isPlaying || Time.unscaledTime - _lastFeedbackTime < 1.0f))
            return;

        _lastFeedbackKind = 2;
        _lastFeedbackScrew = screw;
        _lastFeedbackHead = headSocket != null ? headSocket.CurrentHead : null;
        _lastFeedbackTime = Time.unscaledTime;
        MisalignedToolDetected?.Invoke(screw, angleDeg);
        TighteningStageBlocked?.Invoke(screw, $"Front Insertion Only: Insert the impact wrench socket straight onto the front of '{screw.Part.DisplayName}'!");
    }

    private void EmitStageBlockedFeedback(ScrewInteraction screw, string reason)
    {
        bool sameTarget = _lastFeedbackKind == 3 && _lastFeedbackScrew == screw;
        if (sameTarget && (!Application.isPlaying || Time.unscaledTime - _lastFeedbackTime < 1.0f))
            return;

        _lastFeedbackKind = 3;
        _lastFeedbackScrew = screw;
        _lastFeedbackHead = headSocket != null ? headSocket.CurrentHead : null;
        _lastFeedbackTime = Time.unscaledTime;
        TighteningStageBlocked?.Invoke(screw, reason);
    }

    private void EmitKnobDirectionFeedback(ScrewInteraction screw, string reason)
    {
        bool sameTarget = _lastFeedbackKind == 4 && _lastFeedbackScrew == screw;
        if (sameTarget && (!Application.isPlaying || Time.unscaledTime - _lastFeedbackTime < 1.2f))
            return;

        _lastFeedbackKind = 4;
        _lastFeedbackScrew = screw;
        _lastFeedbackHead = headSocket != null ? headSocket.CurrentHead : null;
        _lastFeedbackTime = Time.unscaledTime;
        TighteningStageBlocked?.Invoke(screw, reason);
    }

    private void AutoAssignReferences()
    {
        if (screwdriver == null)
            screwdriver = GetComponent<XRElectricScrewdriver>();
        if (screwdriver == null)
            screwdriver = GetComponentInParent<XRElectricScrewdriver>();

        if (headSocket == null)
            headSocket = GetComponentInChildren<ScrewdriverHeadAttachment>(includeInactive: true);
        if (headSocket == null && screwdriver != null)
            headSocket = screwdriver.GetComponentInChildren<ScrewdriverHeadAttachment>(includeInactive: true);

        if (wrenchVisualRig == null && screwdriver != null)
            wrenchVisualRig = screwdriver.transform.Find("WrenchVisualRig");
    }
}
