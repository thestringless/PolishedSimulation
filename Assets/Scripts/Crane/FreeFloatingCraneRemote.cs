using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Attachment;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Transformers;
using NOT_Lonely;

namespace Sefpro.Crane
{
    /// <summary>
    /// Configures the OverheadCrane handheld remote for:
    /// 1. Dedicated Left-Hand near-hand XR grabbing (rejecting XRPokeInteractor, XRGazeInteractor, XRSocketInteractor,
    ///    and non-XROrigin test spheres) with an active grip-release watchdog so XRBaseInputInteractor StateChange
    ///    latching or XRInteractionSimulator left-grip latching can never leave the remote stuck to the hand.
    /// 2. Uncontested single-authority pose control (addDefaultGrabTransformers = false, trackPosition/Rotation/Scale = false,
    ///    unparentTransformOnGrab = false, enforced across Update, FixedUpdate, LateUpdate, and onBeforeRender).
    /// 3. Release-driven physical docking into CraneRemoteDockingStation:
    ///    - Never docks while selected/held (UI: REMOTE IN USE).
    ///    - On genuine Grip release inside valid docking region: exits selection, blocks re-selection, glides smoothly
    ///      to DockAnchor (UI: DOCKING), and marks IsDocked = true (UI: REMOTE PARKED) ONLY after physical arrival
    ///      and multi-frame stability at DockAnchor are verified.
    ///    - On release outside valid docking region: stops following the hand immediately and stays stationary where
    ///      released (UI: RELEASE TO DOCK).
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(XRGrabInteractable))]
    [RequireComponent(typeof(NL_Remote))]
    public class FreeFloatingCraneRemote : MonoBehaviour
    {
        public enum RemotePhysicalState
        {
            ParkedAtAnchor,
            HeldInHand,
            DockingToAnchor,
            ReleasedUndocked
        }

        public const int DefaultLayerBit = 1 << 0;
        public const int RemoteBodyLayerBit = 1 << 2;
        public const int RemoteControlsLayerBit = 1 << 3;

        public static readonly Vector3 DefaultLeftEnvelopingLocalPosition = new Vector3(-0.005f, -0.030f, -0.018f);
        public static readonly Vector3 DefaultLeftEnvelopingLocalEuler = new Vector3(-78f, 185f, 0f);

        [Header("Core References")]
        public NL_Remote remoteController;
        public NL_OverheadCrane overheadCrane;
        public Rigidbody remoteRigidbody;
        public XRGrabInteractable grabInteractable;

        [Header("Docking / Parking Holder")]
        [Tooltip("Physical industrial docking holder where the remote starts parked and docks when released inside the docking area.")]
        public CraneRemoteDockingStation dockingStation;
        [Tooltip("When true, the remote starts securely parked in dockingStation upon entering Play Mode.")]
        public bool startParkedInDock = true;
        [SerializeField] private bool isDocked = true;
        [SerializeField] private bool isDocking = false;
        [SerializeField] private RemotePhysicalState physicalState = RemotePhysicalState.ParkedAtAnchor;

        [Header("Left-Hand Attachment Transform (Dedicated Remote-Holding Hand)")]
        public Transform primaryAttachTransform;
        public Transform leftHandAttachTransform;
        public Transform rightHandAttachTransform;

        [Header("Left-Hand Enveloping Local Pose (Remote Space)")]
        public Vector3 leftAttachLocalPosition = new Vector3(-0.005f, -0.030f, -0.018f);
        public Vector3 leftAttachLocalEuler = new Vector3(-78f, 185f, 0f);

        [Header("Colliders & Buttons")]
        public List<BoxCollider> bodyColliders = new List<BoxCollider>();
        public List<NL_RemoteButton> remoteButtons = new List<NL_RemoteButton>();
        public List<XRSimpleInteractable> buttonInteractables = new List<XRSimpleInteractable>();

        [Header("Suspended / Parked Pose State")]
        [SerializeField] private Vector3 suspendedPosition;
        [SerializeField] private Quaternion suspendedRotation = Quaternion.identity;
        [SerializeField] private bool hasInitializedPose = false;

        private IXRSelectInteractor _activeSelectInteractor;
        private Transform _activeHandReferenceTransform;
        private Transform _activeRemoteAttachPoint;
        private bool _isDesktopDragging = false;
        private bool _isSimulatedLeftGrab = false;

        private int _unperformedSelectFrameCount = 0;
        private bool _forcingSelectExitFromWatchdog = false;
        private IXRSelectInteractor _releasedInteractorWaitingInputReset;
        private float _reselectCooldownUntilTime = 0f;

        private float _dockSnapElapsed = 0f;
        private Vector3 _dockSnapStartPos;
        private Quaternion _dockSnapStartRot;
        private Vector3 _prevDockCheckPos;
        private int _dockVerifyStableFrames = 0;
        private string _lastDockResultMessage = string.Empty;

        private RemoteBodyInteractionFilter _bodyFilter;
        private RemoteButtonInteractionFilter _buttonFilter;

        public bool IsHeld => (grabInteractable != null && grabInteractable.isSelected) || _isSimulatedLeftGrab || _isDesktopDragging;
        public bool IsDocked => isDocked;
        public bool IsDocking => isDocking;
        public bool IsSnappingToDock => isDocking;
        public RemotePhysicalState PhysicalState => physicalState;
        public string LastDockResultMessage => _lastDockResultMessage;

        public IXRSelectInteractor HoldingInteractor =>
            (grabInteractable != null && grabInteractable.isSelected && grabInteractable.firstInteractorSelecting != null)
                ? grabInteractable.firstInteractorSelecting
                : _activeSelectInteractor;
        public Transform ActiveRemoteAttachPoint => _activeRemoteAttachPoint;
        public Transform ActiveHandReferenceTransform => _activeHandReferenceTransform;
        public Vector3 SuspendedPosition => suspendedPosition;
        public Quaternion SuspendedRotation => suspendedRotation;

        private void Reset()
        {
            leftAttachLocalPosition = DefaultLeftEnvelopingLocalPosition;
            leftAttachLocalEuler = DefaultLeftEnvelopingLocalEuler;
            EnsureConfigured();
            if (dockingStation != null && startParkedInDock)
                SnapToDockImmediate();
            else
                CaptureCurrentPoseAsSuspended();
        }

        private void Awake()
        {
            EnsureConfigured();
            ApplyFloatingRigidbodySettings();

            if (dockingStation != null && startParkedInDock)
            {
                SnapToDockImmediate();
            }
            else if (!hasInitializedPose || Application.isPlaying)
            {
                CaptureCurrentPoseAsSuspended();
            }
        }

        private void OnEnable()
        {
            EnsureConfigured();
            BindGrabEvents(true);
            RegisterInteractionFilters(true);
            ApplyFloatingRigidbodySettings();

            if (dockingStation != null && isDocked && !IsHeld)
            {
                SnapToDockImmediate();
            }
            else if (!hasInitializedPose)
            {
                CaptureCurrentPoseAsSuspended();
            }

            Application.onBeforeRender += OnBeforeRenderSync;
        }

        private void OnDisable()
        {
            BindGrabEvents(false);
            RegisterInteractionFilters(false);
            Application.onBeforeRender -= OnBeforeRenderSync;
            ClearActiveHandSelectionReferences();
            _isDesktopDragging = false;
            isDocking = false;
            _dockVerifyStableFrames = 0;
            _releasedInteractorWaitingInputReset = null;
            _reselectCooldownUntilTime = 0f;

            StopAllCraneButtonsAndMotion();
        }

        private void ClearActiveHandSelectionReferences()
        {
            _activeSelectInteractor = null;
            _activeHandReferenceTransform = null;
            _activeRemoteAttachPoint = null;
            _isSimulatedLeftGrab = false;
            _unperformedSelectFrameCount = 0;
        }

        public void EnsureConfigured()
        {
            if (remoteController == null)
                remoteController = GetComponent<NL_Remote>();

            if (overheadCrane == null)
            {
                if (remoteController != null)
                    overheadCrane = remoteController.ResolveCraneReference();
                if (overheadCrane == null)
                    overheadCrane = FindAnyObjectByType<NL_OverheadCrane>();
            }

            if (remoteController != null && overheadCrane != null)
            {
                remoteController.crane = overheadCrane;
            }

            if (dockingStation == null)
            {
                dockingStation = FindAnyObjectByType<CraneRemoteDockingStation>();
                if (dockingStation != null && dockingStation.targetRemote == null)
                {
                    dockingStation.targetRemote = this;
                }
            }

            if (remoteRigidbody == null)
                remoteRigidbody = GetComponent<Rigidbody>();

            if (grabInteractable == null)
                grabInteractable = GetComponent<XRGrabInteractable>();

            ConfigureRenderersOffscreenUpdate();
            ConfigureBodyAndButtonColliders();
            EnsureAttachTransforms();
            ConfigureGrabInteractable();
            ConfigureButtonInteractables();
            RegisterInteractionFilters(true);
            ApplyFloatingRigidbodySettings();

            if (dockingStation != null)
            {
                dockingStation.IgnoreCollisionsWithRemote();
            }
        }

        private void ConfigureRenderersOffscreenUpdate()
        {
            var smrs = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < smrs.Length; i++)
            {
                if (smrs[i] != null)
                {
                    smrs[i].updateWhenOffscreen = true;
                }
            }
        }

        public void EnsureAttachTransforms()
        {
            if (Mathf.Abs(leftAttachLocalEuler.y) < 90f || Mathf.Abs(leftAttachLocalEuler.x - (-25f)) < 5f || Mathf.Abs(leftAttachLocalEuler.x - 335f) < 5f)
            {
                leftAttachLocalPosition = DefaultLeftEnvelopingLocalPosition;
                leftAttachLocalEuler = DefaultLeftEnvelopingLocalEuler;
            }

            Transform statusCanvas = transform.Find("CraneHookStatusCanvas");
            if (statusCanvas != null)
            {
                statusCanvas.localPosition = new Vector3(0f, 0.255f, 0.035f);
                statusCanvas.localEulerAngles = new Vector3(-20f, 180f, 0f);
            }

            Transform legacyRight = transform.Find("RightHand_AttachPoint");
            if (legacyRight != null)
            {
                if (Application.isPlaying)
                {
                    legacyRight.gameObject.SetActive(false);
                    Destroy(legacyRight.gameObject);
                }
                else
                {
                    DestroyImmediate(legacyRight.gameObject);
                }
            }
            rightHandAttachTransform = null;

            leftHandAttachTransform = GetOrCreateAttachChild("LeftHand_AttachPoint", leftAttachLocalPosition, leftAttachLocalEuler);
            primaryAttachTransform = leftHandAttachTransform;
        }

        private Transform GetOrCreateAttachChild(string childName, Vector3 localPos, Vector3 localEuler)
        {
            Transform child = transform.Find(childName);
            if (child == null)
            {
                var go = new GameObject(childName);
                child = go.transform;
                child.SetParent(transform, false);
            }
            child.gameObject.SetActive(true);
            child.localPosition = localPos;
            child.localRotation = Quaternion.Euler(localEuler);
            child.localScale = Vector3.one;
            return child;
        }

        private void ConfigureBodyAndButtonColliders()
        {
            bodyColliders.Clear();
            var ownBoxes = GetComponents<BoxCollider>();
            for (int i = 0; i < ownBoxes.Length; i++)
            {
                BoxCollider bc = ownBoxes[i];
                if (bc == null) continue;
                bc.isTrigger = false;
                bc.enabled = true;

                if (bc.size.y > 0.20f)
                {
                    float minZ = -0.03635f;
                    float maxZ = 0.01200f;
                    bc.center = new Vector3(bc.center.x, bc.center.y, (minZ + maxZ) * 0.5f);
                    bc.size = new Vector3(bc.size.x, bc.size.y, maxZ - minZ);
                }

                bodyColliders.Add(bc);
            }

            remoteButtons.Clear();
            var btns = GetComponentsInChildren<NL_RemoteButton>(true);
            for (int i = 0; i < btns.Length; i++)
            {
                NL_RemoteButton btn = btns[i];
                if (btn == null) continue;

                CapsuleCollider cc = btn.GetComponent<CapsuleCollider>();
                if (cc != null)
                {
                    cc.enabled = true;
                    cc.isTrigger = false;
                    cc.direction = 2;
                    cc.center = new Vector3(0f, 0f, 0.003f);
                    cc.radius = 0.0135f;
                    cc.height = 0.0320f;
                    btn.btnCollider = cc;
                }

                remoteButtons.Add(btn);
            }

            if (Application.isPlaying)
            {
                var allCols = GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < allCols.Length; i++)
                {
                    for (int j = i + 1; j < allCols.Length; j++)
                    {
                        if (allCols[i] != null && allCols[j] != null)
                        {
                            Physics.IgnoreCollision(allCols[i], allCols[j], true);
                        }
                    }
                }
            }
        }

        private void ConfigureGrabInteractable()
        {
            if (grabInteractable == null) return;

            EnsureAttachTransforms();

            // Prevent XRGrabInteractable from auto-adding XRGeneralGrabTransformer or driving transform/parent/scale.
            // FreeFloatingCraneRemote is the sole pose authority while held, docking, parked, and released.
            grabInteractable.addDefaultGrabTransformers = false;
            grabInteractable.ClearSingleGrabTransformers();
            grabInteractable.ClearMultipleGrabTransformers();

            var grabTransformers = GetComponents<XRBaseGrabTransformer>();
            for (int i = 0; i < grabTransformers.Length; i++)
            {
                if (grabTransformers[i] != null)
                    grabTransformers[i].enabled = false;
            }

            grabInteractable.movementType = XRBaseInteractable.MovementType.Kinematic;
            grabInteractable.selectMode = InteractableSelectMode.Single;
            grabInteractable.attachTransform = leftHandAttachTransform;
            grabInteractable.secondaryAttachTransform = null;
            grabInteractable.predictedVisualsTransform = null;
            grabInteractable.useDynamicAttach = false;
            grabInteractable.matchAttachPosition = false;
            grabInteractable.matchAttachRotation = false;
            grabInteractable.snapToColliderVolume = false;
            grabInteractable.attachEaseInTime = 0f;
            grabInteractable.unparentTransformOnGrab = false;
            grabInteractable.retainTransformParent = false;
            grabInteractable.throwOnDetach = false;
            grabInteractable.forceGravityOnDetach = false;
            grabInteractable.trackPosition = false;
            grabInteractable.trackRotation = false;
            grabInteractable.trackScale = false;
            grabInteractable.smoothPosition = false;
            grabInteractable.smoothRotation = false;
            grabInteractable.smoothScale = false;
            grabInteractable.farAttachMode = InteractableFarAttachMode.Near;
            grabInteractable.distanceCalculationMode = XRBaseInteractable.DistanceCalculationMode.ColliderPosition;
            grabInteractable.interactionLayers = DefaultLayerBit | RemoteBodyLayerBit;

            grabInteractable.colliders.Clear();
            for (int i = 0; i < bodyColliders.Count; i++)
            {
                if (bodyColliders[i] != null)
                    grabInteractable.colliders.Add(bodyColliders[i]);
            }
        }

        private void ConfigureButtonInteractables()
        {
            buttonInteractables.Clear();
            for (int i = 0; i < remoteButtons.Count; i++)
            {
                NL_RemoteButton btn = remoteButtons[i];
                if (btn == null) continue;

                Collider btnCol = btn.GetComponent<Collider>();
                if (btnCol != null)
                {
                    btnCol.enabled = true;
                    btnCol.isTrigger = false;
                    btn.btnCollider = btnCol;
                }

                XRSimpleInteractable simple = btn.GetComponent<XRSimpleInteractable>();
                if (simple == null)
                    simple = btn.gameObject.AddComponent<XRSimpleInteractable>();

                simple.selectMode = InteractableSelectMode.Single;
                simple.distanceCalculationMode = XRBaseInteractable.DistanceCalculationMode.ColliderPosition;
                simple.interactionLayers = DefaultLayerBit | RemoteControlsLayerBit;
                simple.colliders.Clear();
                if (btnCol != null)
                    simple.colliders.Add(btnCol);

                buttonInteractables.Add(simple);
            }
        }

        private void RegisterInteractionFilters(bool register)
        {
            if (_bodyFilter == null)
                _bodyFilter = new RemoteBodyInteractionFilter(this);
            if (_buttonFilter == null)
                _buttonFilter = new RemoteButtonInteractionFilter(this);

            if (grabInteractable != null)
            {
                grabInteractable.selectFilters.Remove(_bodyFilter);
                grabInteractable.hoverFilters.Remove(_bodyFilter);
                if (register)
                {
                    grabInteractable.selectFilters.Add(_bodyFilter);
                    grabInteractable.hoverFilters.Add(_bodyFilter);
                }
            }

            for (int i = 0; i < buttonInteractables.Count; i++)
            {
                var simple = buttonInteractables[i];
                if (simple == null) continue;
                simple.selectFilters.Remove(_buttonFilter);
                simple.hoverFilters.Remove(_buttonFilter);
                if (register)
                {
                    simple.selectFilters.Add(_buttonFilter);
                    simple.hoverFilters.Add(_buttonFilter);
                }
            }
        }

        public void ApplyFloatingRigidbodySettings()
        {
            if (remoteRigidbody == null)
                remoteRigidbody = GetComponent<Rigidbody>();
            if (remoteRigidbody == null)
                return;

            if (!remoteRigidbody.isKinematic)
            {
                remoteRigidbody.linearVelocity = Vector3.zero;
                remoteRigidbody.angularVelocity = Vector3.zero;
            }
            remoteRigidbody.useGravity = false;
            remoteRigidbody.isKinematic = true;
            remoteRigidbody.interpolation = RigidbodyInterpolation.None;
            remoteRigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        }

        private void BindGrabEvents(bool subscribe)
        {
            if (grabInteractable == null) return;

            grabInteractable.selectEntered.RemoveListener(OnGrabSelectEntered);
            grabInteractable.selectExited.RemoveListener(OnGrabSelectExited);

            if (subscribe)
            {
                grabInteractable.selectEntered.AddListener(OnGrabSelectEntered);
                grabInteractable.selectExited.AddListener(OnGrabSelectExited);
            }
        }

        public static bool IsLeftHandInteractor(IXRInteractor interactor)
        {
            if (interactor == null || interactor.transform == null)
                return false;

            Transform t = interactor.transform;
            while (t != null)
            {
                string n = t.name;
                if (n.IndexOf("Left", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                if (n.IndexOf("Right", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
                t = t.parent;
            }
            return false;
        }

        public static bool IsRightHandInteractor(IXRInteractor interactor)
        {
            if (interactor == null || interactor.transform == null)
                return false;

            Transform t = interactor.transform;
            while (t != null)
            {
                string n = t.name;
                if (n.IndexOf("Right", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                if (n.IndexOf("Left", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// Validates that an interactor is a genuine Left-Hand GRAB interactor on the XR Rig
        /// (explicitly rejecting XRPokeInteractor, XRGazeInteractor, XRSocketInteractor, Teleport rays,
        /// and disconnected scene-root test spheres).
        /// </summary>
        public static bool IsEligibleLeftHandGrabInteractor(IXRInteractor interactor)
        {
            if (interactor == null || interactor.transform == null)
                return false;

            if (interactor is XRPokeInteractor ||
                interactor is XRGazeInteractor ||
                interactor is XRSocketInteractor)
            {
                return false;
            }

            if (interactor.transform.name.IndexOf("Teleport", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            if (!IsLeftHandInteractor(interactor))
                return false;

            if (interactor.transform.GetComponentInParent<XROrigin>() == null)
                return false;

            return true;
        }

        public static Transform ResolveHandReferenceTransform(IXRInteractor interactor, IXRInteractable interactable)
        {
            if (interactor == null)
                return null;

            if (interactor is Component comp && comp != null)
            {
                var attachController = comp.GetComponent<InteractionAttachController>();
                if (attachController != null && attachController.transformToFollow != null)
                {
                    return attachController.transformToFollow;
                }
            }

            Transform attach = interactor.GetAttachTransform(interactable);
            if (attach != null)
                return attach;

            return interactor.transform;
        }

        public Transform SelectRemoteAttachTransformForInteractor(IXRInteractor interactor)
        {
            EnsureAttachTransforms();
            if (!IsEligibleLeftHandGrabInteractor(interactor))
                return null;

            if (grabInteractable != null && leftHandAttachTransform != null)
            {
                grabInteractable.attachTransform = leftHandAttachTransform;
            }
            return leftHandAttachTransform;
        }

        public bool IsAnyDirectionalButtonActive()
        {
            for (int i = 0; i < remoteButtons.Count; i++)
            {
                var btn = remoteButtons[i];
                if (btn != null && btn.IsPressed)
                    return true;
            }
            for (int i = 0; i < buttonInteractables.Count; i++)
            {
                var b = buttonInteractables[i];
                if (b != null && b.isSelected)
                    return true;
            }
            return false;
        }

        public void StopAllCraneButtonsAndMotion()
        {
            if (remoteController != null)
            {
                remoteController.enableKeyboardControls = false;
                remoteController.StopAllButtonCommandsImmediate();
            }

            for (int i = 0; i < remoteButtons.Count; i++)
            {
                var btn = remoteButtons[i];
                if (btn == null) continue;
                btn.ClearAllHoldSourcesImmediate(invokeReleaseIfWasPressed: true);
            }
        }

        /// <summary>
        /// Checks whether an interactor's raw select input is currently physically held/performed.
        /// </summary>
        private static bool IsInteractorRawSelectPerformed(IXRSelectInteractor interactor)
        {
            if (interactor is XRBaseInputInteractor inputInteractor && inputInteractor.selectInput != null)
            {
                return inputInteractor.selectInput.ReadIsPerformed();
            }
            return interactor != null && interactor.isSelectActive;
        }

        private static bool WasInteractorSelectNewlyPressedThisFrame(IXRSelectInteractor interactor)
        {
            if (interactor is XRBaseInputInteractor inputInteractor && inputInteractor.selectInput != null)
            {
                return inputInteractor.selectInput.ReadWasPerformedThisFrame();
            }
            return false;
        }

        /// <summary>
        /// Active watchdog while grabInteractable.isSelected == true:
        /// 1. Catches XRI 3.5.1 StateChange missed-completion latches (where selectInput.ReadIsPerformed() is false
        ///    for >= 2 consecutive frames, yet LogicalInputState.active remained true because wasCompletedThisFrame
        ///    was missed across a frame/modality boundary).
        /// 2. Catches XRInteractionSimulator's left-grip latch (where releasing 'G' after releasing 'Left Shift'
        ///    routes grip=0 to RightController instead of LeftController, leaving LeftController grip stuck ON).
        /// When detected, calls XRInteractionManager.SelectExit cleanly so OnGrabSelectExited runs the normal
        /// release -> docking sequence.
        /// </summary>
        private void MonitorGenuineGripReleaseWhileSelected()
        {
            if (_forcingSelectExitFromWatchdog || grabInteractable == null || !grabInteractable.isSelected)
            {
                _unperformedSelectFrameCount = 0;
                return;
            }

            IXRSelectInteractor currentSelector = grabInteractable.firstInteractorSelecting ?? _activeSelectInteractor;
            if (currentSelector == null)
                return;

            // 1. Check XRInteractionSimulator keyboard 'G' grip release (handles releasing G even if Left Shift was already released)
            var sim = XRInteractionSimulator.instance;
            if (sim != null && sim.isActiveAndEnabled && sim.gripInput != null && sim.gripInput.ReadWasCompletedThisFrame())
            {
                if (currentSelector is XRBaseInputInteractor simInter &&
                    simInter.selectInput != null &&
                    simInter.selectInput.inputSourceMode != XRInputButtonReader.InputSourceMode.ManualValue)
                {
                    ForceCleanSelectExit(currentSelector);
                    return;
                }
            }

            // 2. Check raw selectInput performed state to defeat StateChange missed-completion latching
            if (currentSelector is XRBaseInputInteractor inputInteractor && inputInteractor.selectInput != null)
            {
                bool rawPerformed = inputInteractor.selectInput.ReadIsPerformed();
                bool analogReleased = true;
                if (inputInteractor.selectInput.TryReadValue(out float analogVal))
                {
                    analogReleased = analogVal <= 0.35f;
                }

                if (!rawPerformed && analogReleased)
                {
                    _unperformedSelectFrameCount++;
                    if (_unperformedSelectFrameCount >= 2)
                    {
                        ForceCleanSelectExit(currentSelector);
                    }
                }
                else
                {
                    _unperformedSelectFrameCount = 0;
                }
            }
        }

        private void ForceCleanSelectExit(IXRSelectInteractor selector)
        {
            if (grabInteractable == null || grabInteractable.interactionManager == null || selector == null)
                return;

            _unperformedSelectFrameCount = 0;
            _forcingSelectExitFromWatchdog = true;
            try
            {
                _releasedInteractorWaitingInputReset = selector;
                _reselectCooldownUntilTime = Time.unscaledTime + 0.22f;
                grabInteractable.interactionManager.SelectExit(selector, grabInteractable);
            }
            finally
            {
                _forcingSelectExitFromWatchdog = false;
            }
        }

        /// <summary>
        /// Used at scene initialization / reset when the remote is not held, placing it directly at DockAnchor.
        /// </summary>
        public void SnapToDockImmediate()
        {
            if (IsHeld)
                return;

            if (dockingStation == null)
                dockingStation = FindAnyObjectByType<CraneRemoteDockingStation>();
            if (dockingStation == null)
                return;

            StopAllCraneButtonsAndMotion();

            if (transform.parent != null)
                transform.SetParent(null, true);

            isDocking = false;
            _dockSnapElapsed = 0f;
            _dockVerifyStableFrames = 2;

            Vector3 dockPos = dockingStation.DockWorldPosition;
            Quaternion dockRot = dockingStation.DockWorldRotation;
            SetSuspendedPose(dockPos, dockRot);

            isDocked = dockingStation.IsRemotePhysicallyAtAnchor(transform);
            physicalState = isDocked ? RemotePhysicalState.ParkedAtAnchor : RemotePhysicalState.ReleasedUndocked;
            dockingStation.RefreshActualPhysicalStatus();
            _lastDockResultMessage = isDocked ? "Parked securely in docking holder." : "Failed anchor verification.";
        }

        private void BeginPostReleaseDockingMovement()
        {
            if (dockingStation == null || IsHeld)
                return;

            StopAllCraneButtonsAndMotion();

            if (transform.parent != null)
                transform.SetParent(null, true);

            // Never report REMOTE PARKED when docking begins; IsDocked becomes true ONLY after physical arrival & stability at DockAnchor
            isDocked = false;
            isDocking = true;
            physicalState = RemotePhysicalState.DockingToAnchor;

            _dockSnapStartPos = transform.position;
            _dockSnapStartRot = transform.rotation;
            _prevDockCheckPos = transform.position;
            _dockSnapElapsed = 0f;
            _dockVerifyStableFrames = 0;

            SetSuspendedPose(_dockSnapStartPos, _dockSnapStartRot);
            dockingStation.RefreshActualPhysicalStatus();
            _lastDockResultMessage = "Docking movement started toward DockAnchor.";
        }

        private void UndockOnGrab()
        {
            isDocked = false;
            isDocking = false;
            physicalState = RemotePhysicalState.HeldInHand;
            _dockSnapElapsed = 0f;
            _dockVerifyStableFrames = 0;
            _unperformedSelectFrameCount = 0;

            if (dockingStation != null)
            {
                dockingStation.RefreshActualPhysicalStatus();
            }
            _lastDockResultMessage = "Retrieved by Left Hand.";
        }

        /// <summary>
        /// Evaluates docking eligibility ONLY after the Remote has genuinely exited hand selection.
        /// - Inside valid docking region: begins smooth physical movement (DOCKING) toward DockAnchor.
        /// - Outside valid docking region: stays stationary where released (RELEASE TO DOCK), never following the hand.
        /// </summary>
        public bool HandleGenuineGripRelease()
        {
            if (IsHeld)
            {
                _lastDockResultMessage = "Docking skipped: Remote is still held.";
                return false;
            }

            StopAllCraneButtonsAndMotion();

            if (transform.parent != null)
                transform.SetParent(null, true);

            ApplyFloatingRigidbodySettings();

            if (dockingStation == null)
                dockingStation = FindAnyObjectByType<CraneRemoteDockingStation>();

            string rejectReason = string.Empty;
            if (dockingStation != null && dockingStation.IsWithinValidDockingRegion(transform, out rejectReason))
            {
                BeginPostReleaseDockingMovement();
                return true;
            }

            isDocking = false;
            isDocked = false;
            physicalState = RemotePhysicalState.ReleasedUndocked;
            _dockVerifyStableFrames = 0;
            CaptureCurrentPoseAsSuspended();
            EnforceSuspendedPoseImmediate();
            _lastDockResultMessage = dockingStation != null ? rejectReason : "No docking station present.";
            if (dockingStation != null)
            {
                dockingStation.RefreshActualPhysicalStatus();
            }
            return false;
        }

        private void OnGrabSelectEntered(SelectEnterEventArgs args)
        {
            if (!IsEligibleLeftHandGrabInteractor(args.interactorObject))
            {
                if (grabInteractable != null && grabInteractable.interactionManager != null && args.interactorObject != null)
                {
                    grabInteractable.interactionManager.SelectExit(args.interactorObject, grabInteractable);
                }
                return;
            }

            _releasedInteractorWaitingInputReset = null;
            _reselectCooldownUntilTime = 0f;
            _unperformedSelectFrameCount = 0;

            ApplyFloatingRigidbodySettings();
            _activeSelectInteractor = args.interactorObject;
            _activeRemoteAttachPoint = SelectRemoteAttachTransformForInteractor(_activeSelectInteractor);
            _activeHandReferenceTransform = ResolveHandReferenceTransform(_activeSelectInteractor, grabInteractable);

            UndockOnGrab();
            SyncHeldPose();

            for (int i = 0; i < remoteButtons.Count; i++)
            {
                var btn = remoteButtons[i];
                if (btn == null) continue;
                if (btn.btnCollider != null) btn.btnCollider.enabled = true;
                btn.ClearInteractorHold(_activeSelectInteractor);
            }

            CaptureCurrentPoseAsSuspended();
            if (dockingStation != null)
                dockingStation.RefreshActualPhysicalStatus();
        }

        private void OnGrabSelectExited(SelectExitEventArgs args)
        {
            var releasingInteractor = args.interactorObject;
            if (releasingInteractor != null)
            {
                for (int i = 0; i < remoteButtons.Count; i++)
                {
                    var btn = remoteButtons[i];
                    if (btn != null)
                        btn.ClearInteractorHold(releasingInteractor);
                }
            }

            // If still selected by another valid interactor, keep tracking that interactor
            if (grabInteractable != null && grabInteractable.isSelected)
            {
                _activeSelectInteractor = grabInteractable.firstInteractorSelecting;
                _activeRemoteAttachPoint = SelectRemoteAttachTransformForInteractor(_activeSelectInteractor);
                _activeHandReferenceTransform = ResolveHandReferenceTransform(_activeSelectInteractor, grabInteractable);
                return;
            }

            // Genuine full release confirmed: record short re-selection guard & clear all hand references immediately
            if (releasingInteractor != null)
            {
                _releasedInteractorWaitingInputReset = IsInteractorRawSelectPerformed(releasingInteractor) ? releasingInteractor : null;
                _reselectCooldownUntilTime = Time.unscaledTime + 0.20f;
            }

            ClearActiveHandSelectionReferences();
            HandleGenuineGripRelease();
        }

        public bool BeginLeftHandGrab(IXRSelectInteractor leftInteractor)
        {
            if (leftInteractor == null || !CanInteractorSelectBody(leftInteractor))
                return false;

            _releasedInteractorWaitingInputReset = null;
            _reselectCooldownUntilTime = 0f;

            ApplyFloatingRigidbodySettings();
            _isSimulatedLeftGrab = true;
            _activeSelectInteractor = leftInteractor;
            _activeRemoteAttachPoint = SelectRemoteAttachTransformForInteractor(leftInteractor);
            _activeHandReferenceTransform = ResolveHandReferenceTransform(leftInteractor, grabInteractable);

            UndockOnGrab();
            SyncHeldPose();

            for (int i = 0; i < remoteButtons.Count; i++)
            {
                var btn = remoteButtons[i];
                if (btn == null) continue;
                if (btn.btnCollider != null) btn.btnCollider.enabled = true;
                btn.ClearInteractorHold(leftInteractor);
            }

            CaptureCurrentPoseAsSuspended();
            if (dockingStation != null)
                dockingStation.RefreshActualPhysicalStatus();
            return true;
        }

        public bool ReleaseLeftHandGrab()
        {
            var releasingInteractor = _activeSelectInteractor;
            ClearActiveHandSelectionReferences();

            if (releasingInteractor != null)
            {
                for (int i = 0; i < remoteButtons.Count; i++)
                {
                    var btn = remoteButtons[i];
                    if (btn != null)
                        btn.ClearInteractorHold(releasingInteractor);
                }
            }

            return HandleGenuineGripRelease();
        }

        public void SyncHeldPose()
        {
            bool xrSelected = grabInteractable != null && grabInteractable.isSelected;
            if (!xrSelected && !_isSimulatedLeftGrab)
                return;

            if (_activeSelectInteractor == null && xrSelected)
            {
                _activeSelectInteractor = grabInteractable.firstInteractorSelecting;
            }
            if (_activeSelectInteractor == null)
                return;

            if (_activeRemoteAttachPoint == null)
            {
                _activeRemoteAttachPoint = SelectRemoteAttachTransformForInteractor(_activeSelectInteractor);
            }
            if (_activeHandReferenceTransform == null)
            {
                _activeHandReferenceTransform = ResolveHandReferenceTransform(_activeSelectInteractor, grabInteractable);
            }

            if (_activeHandReferenceTransform != null && _activeRemoteAttachPoint != null)
            {
                Vector3 attachLocalPos = _activeRemoteAttachPoint.localPosition;
                Quaternion attachLocalRot = _activeRemoteAttachPoint.localRotation;

                Quaternion targetRot = _activeHandReferenceTransform.rotation * Quaternion.Inverse(attachLocalRot);
                Vector3 targetPos = _activeHandReferenceTransform.position - (targetRot * attachLocalPos);

                SetSuspendedPose(targetPos, targetRot);
            }
        }

        public void CaptureCurrentPoseAsSuspended()
        {
            suspendedPosition = transform.position;
            suspendedRotation = transform.rotation;
            hasInitializedPose = true;
        }

        public void SetSuspendedPose(Vector3 worldPosition, Quaternion worldRotation)
        {
            suspendedPosition = worldPosition;
            suspendedRotation = worldRotation;
            hasInitializedPose = true;
            transform.SetPositionAndRotation(suspendedPosition, suspendedRotation);
            if (remoteRigidbody != null && Application.isPlaying)
            {
                remoteRigidbody.position = suspendedPosition;
                remoteRigidbody.rotation = suspendedRotation;
            }
            if (Application.isPlaying)
            {
                Physics.SyncTransforms();
            }
        }

        public void BeginDesktopDrag()
        {
            _isDesktopDragging = true;
            UndockOnGrab();
            ApplyFloatingRigidbodySettings();
        }

        public void EndDesktopDrag()
        {
            _isDesktopDragging = false;
            HandleGenuineGripRelease();
        }

        private void StepPoseAndDocking(float dt)
        {
            // Clear post-release input reset latch as soon as the interactor's raw select input is observed released
            if (_releasedInteractorWaitingInputReset != null && !IsInteractorRawSelectPerformed(_releasedInteractorWaitingInputReset))
            {
                _releasedInteractorWaitingInputReset = null;
            }

            // 1. While actively held, monitor for genuine Grip release and follow ONLY the holding Left Hand (never run docking!)
            if (grabInteractable != null && grabInteractable.isSelected)
            {
                MonitorGenuineGripReleaseWhileSelected();
                // If MonitorGenuineGripReleaseWhileSelected triggered SelectExit, grabInteractable.isSelected is now false!
            }

            if ((grabInteractable != null && grabInteractable.isSelected) || _isSimulatedLeftGrab)
            {
                if (isDocked) isDocked = false;
                if (isDocking) isDocking = false;
                physicalState = RemotePhysicalState.HeldInHand;
                SyncHeldPose();
                return;
            }

            if (_isDesktopDragging)
                return;

            // 2. While moving smoothly toward DockAnchor after Grip release (DOCKING state)
            if (isDocking && dockingStation != null)
            {
                physicalState = RemotePhysicalState.DockingToAnchor;

                if (IsAnyDirectionalButtonActive())
                {
                    StopAllCraneButtonsAndMotion();
                }

                if (transform.parent != null)
                    transform.SetParent(null, true);

                Vector3 targetPos = dockingStation.DockWorldPosition;
                Quaternion targetRot = dockingStation.DockWorldRotation;

                float duration = Mathf.Max(0.02f, dockingStation.gentleSnapDuration);
                _dockSnapElapsed += Mathf.Max(dt, 0.001f);
                float u = Mathf.Clamp01(_dockSnapElapsed / duration);
                float smooth = Mathf.SmoothStep(0f, 1f, u);

                Vector3 nextPos = u >= 1f ? targetPos : Vector3.Lerp(_dockSnapStartPos, targetPos, smooth);
                Quaternion nextRot = u >= 1f ? targetRot : Quaternion.Slerp(_dockSnapStartRot, targetRot, smooth);
                float frameStepDelta = Vector3.Distance(nextPos, _prevDockCheckPos);
                _prevDockCheckPos = nextPos;

                SetSuspendedPose(nextPos, nextRot);

                // Verify physical arrival AND settled multi-frame stability at DockAnchor before reporting REMOTE PARKED
                if (u >= 1f)
                {
                    SetSuspendedPose(targetPos, targetRot);
                    if (dockingStation.IsRemotePhysicallyAtAnchor(transform))
                    {
                        if (frameStepDelta <= 0.0005f)
                            _dockVerifyStableFrames++;
                        else
                            _dockVerifyStableFrames = 1;

                        if (_dockVerifyStableFrames >= 2)
                        {
                            isDocking = false;
                            isDocked = true;
                            physicalState = RemotePhysicalState.ParkedAtAnchor;
                            _lastDockResultMessage = "Parked securely in docking holder (pose & stability verified).";
                            dockingStation.RefreshActualPhysicalStatus();
                            return;
                        }
                    }
                    else if (_dockSnapElapsed > duration + 0.50f)
                    {
                        // Anti-hang safety fallback so UI can never stay stuck in DOCKING indefinitely
                        isDocking = false;
                        isDocked = false;
                        physicalState = RemotePhysicalState.ReleasedUndocked;
                        _dockVerifyStableFrames = 0;
                        _lastDockResultMessage = "Docking aborted: physical arrival verification failed.";
                        dockingStation.RefreshActualPhysicalStatus();
                        return;
                    }
                }
                else
                {
                    _dockVerifyStableFrames = 0;
                }

                dockingStation.RefreshActualPhysicalStatus();
                return;
            }

            // 3. While securely parked in DockAnchor
            if (isDocked && dockingStation != null)
            {
                physicalState = RemotePhysicalState.ParkedAtAnchor;

                suspendedPosition = dockingStation.DockWorldPosition;
                suspendedRotation = dockingStation.DockWorldRotation;
                hasInitializedPose = true;
                EnforceSuspendedPoseImmediate();
                return;
            }

            // 4. Released outside dock: remain stationary at release pose (never follow hand, never fall)
            physicalState = RemotePhysicalState.ReleasedUndocked;
            if (hasInitializedPose)
            {
                EnforceSuspendedPoseImmediate();
            }
        }

        private void Update()
        {
            if (!Application.isPlaying)
                return;

            StepPoseAndDocking(Time.deltaTime);
        }

        private void FixedUpdate()
        {
            if (!Application.isPlaying)
                return;

            ApplyFloatingRigidbodySettings();

            if ((grabInteractable != null && grabInteractable.isSelected) || _isSimulatedLeftGrab)
            {
                SyncHeldPose();
            }
            else if (!_isDesktopDragging && hasInitializedPose)
            {
                if (isDocked && !isDocking && dockingStation != null)
                {
                    suspendedPosition = dockingStation.DockWorldPosition;
                    suspendedRotation = dockingStation.DockWorldRotation;
                }
                // Enforce suspendedPosition/suspendedRotation during BOTH isDocking AND parked/released states!
                EnforceSuspendedPoseImmediate();
            }
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying)
            {
                if (!IsHeld)
                {
                    if (isDocked && dockingStation != null)
                    {
                        suspendedPosition = dockingStation.DockWorldPosition;
                        suspendedRotation = dockingStation.DockWorldRotation;
                        transform.SetPositionAndRotation(suspendedPosition, suspendedRotation);
                    }
                    else
                    {
                        suspendedPosition = transform.position;
                        suspendedRotation = transform.rotation;
                    }
                    hasInitializedPose = true;
                }
                return;
            }

            if ((grabInteractable != null && grabInteractable.isSelected) || _isSimulatedLeftGrab)
            {
                SyncHeldPose();
            }
            else if (!_isDesktopDragging && hasInitializedPose)
            {
                if (isDocked && !isDocking && dockingStation != null)
                {
                    suspendedPosition = dockingStation.DockWorldPosition;
                    suspendedRotation = dockingStation.DockWorldRotation;
                }
                // Enforce suspendedPosition/suspendedRotation in LateUpdate too so nothing overrides docking glide!
                EnforceSuspendedPoseImmediate();
            }
        }

        private void OnBeforeRenderSync()
        {
            if (!Application.isPlaying)
                return;

            if ((grabInteractable != null && grabInteractable.isSelected) || _isSimulatedLeftGrab)
            {
                SyncHeldPose();
            }
            else if (!_isDesktopDragging && hasInitializedPose)
            {
                EnforceSuspendedPoseImmediate();
            }
        }

        private void EnforceSuspendedPoseImmediate()
        {
            if ((transform.position - suspendedPosition).sqrMagnitude > 1e-10f ||
                Quaternion.Angle(transform.rotation, suspendedRotation) > 1e-4f)
            {
                transform.SetPositionAndRotation(suspendedPosition, suspendedRotation);
                if (remoteRigidbody != null && Application.isPlaying)
                {
                    remoteRigidbody.position = suspendedPosition;
                    remoteRigidbody.rotation = suspendedRotation;
                }
                if (Application.isPlaying)
                {
                    Physics.SyncTransforms();
                }
            }
        }

        public bool IsInteractorOperatingAnyButton(IXRInteractor interactor)
        {
            if (interactor == null) return false;
            for (int i = 0; i < buttonInteractables.Count; i++)
            {
                var b = buttonInteractables[i];
                if (b != null && b.isSelected && ReferenceEquals(b.firstInteractorSelecting, interactor))
                    return true;
            }
            for (int i = 0; i < remoteButtons.Count; i++)
            {
                var rb = remoteButtons[i];
                if (rb != null && rb.IsInteractorHoldingButton(interactor))
                    return true;
            }
            return false;
        }

        public NL_RemoteButton FindClosestButton(Vector3 worldPoint, out float minDistance)
        {
            minDistance = float.MaxValue;
            NL_RemoteButton closest = null;
            for (int i = 0; i < remoteButtons.Count; i++)
            {
                var btn = remoteButtons[i];
                if (btn == null) continue;
                float d = Vector3.Distance(worldPoint, btn.transform.position);
                if (d < minDistance)
                {
                    minDistance = d;
                    closest = btn;
                }
            }
            return closest;
        }

        public bool CanInteractorSelectBody(IXRSelectInteractor interactor)
        {
            if (_bodyFilter == null)
                _bodyFilter = new RemoteBodyInteractionFilter(this);
            return _bodyFilter.Process(interactor, grabInteractable);
        }

        public bool CanInteractorSelectButton(IXRSelectInteractor interactor, XRSimpleInteractable buttonInteractable)
        {
            if (_buttonFilter == null)
                _buttonFilter = new RemoteButtonInteractionFilter(this);
            return _buttonFilter.Process(interactor, buttonInteractable);
        }

        private sealed class RemoteBodyInteractionFilter : IXRSelectFilter, IXRHoverFilter
        {
            private readonly FreeFloatingCraneRemote _owner;
            private const float MaxNearHandGrabDistance = 0.25f;

            public bool canProcess => _owner != null && _owner.isActiveAndEnabled;

            public RemoteBodyInteractionFilter(FreeFloatingCraneRemote owner)
            {
                _owner = owner;
            }

            private bool IsWithinNearHandReach(IXRInteractor interactor)
            {
                if (_owner == null || interactor == null) return false;
                Transform handTf = ResolveHandReferenceTransform(interactor, _owner.grabInteractable);
                if (handTf == null) return false;

                Vector3 handPos = handTf.position;
                float minDist = float.MaxValue;
                for (int i = 0; i < _owner.bodyColliders.Count; i++)
                {
                    var bc = _owner.bodyColliders[i];
                    if (bc == null || !bc.enabled) continue;
                    Vector3 closest = bc.ClosestPoint(handPos);
                    float d = Vector3.Distance(handPos, closest);
                    if (d < minDist) minDist = d;
                }

                if (minDist == float.MaxValue)
                {
                    minDist = Vector3.Distance(handPos, _owner.transform.position);
                }

                return minDist <= MaxNearHandGrabDistance;
            }

            public bool Process(IXRHoverInteractor interactor, IXRHoverInteractable interactable)
            {
                if (_owner == null) return true;

                if (!IsEligibleLeftHandGrabInteractor(interactor))
                    return false;

                if (_owner.IsDocking)
                    return false;

                if (_owner.IsHeld && _owner.HoldingInteractor != null)
                {
                    return ReferenceEquals(interactor, _owner.HoldingInteractor);
                }

                if (_owner.IsInteractorOperatingAnyButton(interactor))
                    return false;

                return IsWithinNearHandReach(interactor);
            }

            public bool Process(IXRSelectInteractor interactor, IXRSelectInteractable interactable)
            {
                if (_owner == null) return true;

                if (!IsEligibleLeftHandGrabInteractor(interactor))
                    return false;

                // Block unwanted re-selection while actively gliding into DockAnchor
                if (_owner.IsDocking)
                    return false;

                // If already held by this interactor, allow continuing selection (watchdog monitors Grip release)
                if (_owner.IsHeld && _owner.HoldingInteractor != null)
                {
                    return ReferenceEquals(interactor, _owner.HoldingInteractor);
                }

                // Prevent immediate bounce/latched re-selection right after release unless a brand-new press occurred
                bool newlyPressedThisFrame = WasInteractorSelectNewlyPressedThisFrame(interactor);
                if (!newlyPressedThisFrame)
                {
                    if (Application.isPlaying && Time.unscaledTime < _owner._reselectCooldownUntilTime)
                        return false;

                    if (ReferenceEquals(interactor, _owner._releasedInteractorWaitingInputReset))
                    {
                        if (IsInteractorRawSelectPerformed(interactor))
                            return false;
                        _owner._releasedInteractorWaitingInputReset = null;
                    }
                }
                else
                {
                    _owner._releasedInteractorWaitingInputReset = null;
                }

                if (_owner.IsInteractorOperatingAnyButton(interactor))
                    return false;

                if (!IsWithinNearHandReach(interactor))
                    return false;

                _owner.SelectRemoteAttachTransformForInteractor(interactor);
                return true;
            }
        }

        private sealed class RemoteButtonInteractionFilter : IXRSelectFilter, IXRHoverFilter
        {
            private readonly FreeFloatingCraneRemote _owner;
            private const float MaxButtonHoldReach = 0.25f;

            public bool canProcess => _owner != null && _owner.isActiveAndEnabled;

            public RemoteButtonInteractionFilter(FreeFloatingCraneRemote owner)
            {
                _owner = owner;
            }

            public bool Process(IXRHoverInteractor interactor, IXRHoverInteractable interactable)
            {
                return EvaluateButtonAccess(interactor, interactable);
            }

            public bool Process(IXRSelectInteractor interactor, IXRSelectInteractable interactable)
            {
                if (_owner != null && _owner.IsDocking)
                    return false;

                if (interactable != null && interactable.isSelected && ReferenceEquals(interactable.firstInteractorSelecting, interactor))
                {
                    if (_owner != null && _owner.IsHeld && ReferenceEquals(interactor, _owner.HoldingInteractor))
                    {
                        return false;
                    }

                    return true;
                }

                return EvaluateButtonAccess(interactor, interactable);
            }

            private bool EvaluateButtonAccess(IXRInteractor interactor, IXRInteractable interactable)
            {
                if (_owner == null || interactor == null || interactable == null)
                    return false;

                if (_owner.IsDocking)
                    return false;

                if (_owner.IsHeld && ReferenceEquals(interactor, _owner.HoldingInteractor))
                {
                    return false;
                }

                Transform attach = ResolveHandReferenceTransform(interactor, interactable);
                if (attach == null) attach = interactor.transform;

                NL_RemoteButton closest = _owner.FindClosestButton(attach.position, out float dist);
                if (dist <= MaxButtonHoldReach && closest != null && closest.transform != interactable.transform)
                {
                    return false;
                }

                return true;
            }
        }
    }
}
