using UnityEngine;
using NOT_Lonely;
using Sefpro.RatchetBelt;

namespace Sefpro.DesktopTesting
{
    /// <summary>
    /// Pointer-driven desktop interaction manager for the VR Training Warehouse.
    /// Handles reticle raycasting, contextual prompts, first-person look pausing,
    /// screen-space arc mapping for ratchet handle operation, lever emission highlighting,
    /// tooth engagement notifications, and station control adapters.
    /// </summary>
    [RequireComponent(typeof(DesktopPlayerController))]
    public class DesktopInteractionManager : MonoBehaviour
    {
        public enum TargetType
        {
            None,
            RatchetHandle,
            RatchetReleaseCatch,
            RatchetPawl,
            RatchetHook,
            PlatformSideHook,
            CraneRemote,
            CraneButton,
            VibroKnob,
            Dashboard,
            HopperHandwheel,
            HopperLiftingPoint,
            JawTongsLiftingRing,
            JawTongsPin,
            GenericInteractable
        }

        [Header("Raycast Settings")]
        [Tooltip("Maximum interaction reach in meters.")]
        public float interactionRange = 3.5f;

        [Tooltip("Maximum distance to identify an object and prompt 'Too far away'.")]
        public float identificationRange = 12.0f;

        [Tooltip("Layer mask for interactable raycasting.")]
        public LayerMask raycastLayers = ~0;

        [Header("Ratchet Handle Drag")]
        [Tooltip("Sensitivity when dragging the ratchet handle with the mouse.")]
        public float handleDragSensitivity = 45.0f;

        [Header("UI & HUD Settings")]
        public bool showHelpOverlay = false;

        private DesktopPlayerController playerController;
        private Camera playerCamera;

        // Active Hover / Target State
        private TargetType currentTargetType = TargetType.None;
        private GameObject currentTargetObject;
        private string currentPromptText = "";
        private bool isTargetInRange = false;

        // Specific Station Components Cached
        private RatchetBeltMechanicsController ratchetController;
        private PlatformHookAttachment hookAttachment;
        private NL_Remote craneRemote;
        private NL_RemoteButton activeCraneButton;
        private NL_AttachCargo craneAttachCargo;
        private XRSteppedKnob vibroKnob;
        private LiquidSolidPrototype.Core.HopperPourManager hopperManager;
        private LiquidSolidPrototype.Input.HopperHandwheel hopperHandwheel;
        private LiquidSolidPrototype.Core.HopperCraneLiftingPoint hopperLiftingPoint;
        private Sefpro.JawTongs.JawTongsCraneLiftingPoint tongsLiftingPoint;

        // Lever Highlighting
        private MaterialPropertyBlock highlightPropBlock;
        private MeshRenderer[] leverRenderers;
        private bool isLeverHighlighted = false;

        // Handle Dragging State
        private bool isDraggingHandle = false;
        private bool isDraggingHopperWheel = false;
        private bool isDraggingStrapHook = false;
        private bool isDraggingCraneRemote = false;
        private float craneRemoteDragDistance = 1.25f;
        private Plane strapDragPlane;

        // Crane Mode State
        private bool isOperatingCrane = false;

        // Tooth / Release Feedback Notification State
        private float toothFlashEndTime = 0.0f;
        private string toothFlashMessage = "";

        // Hook Hover State
        private PlatformSideHook lastHoveredHook;

        public bool IsOperatingCrane => isOperatingCrane;
        public bool IsDraggingHandle => isDraggingHandle;
        public TargetType CurrentTarget => currentTargetType;

        private void Awake()
        {
            playerController = GetComponent<DesktopPlayerController>();
            highlightPropBlock = new MaterialPropertyBlock();
        }

        private void Start()
        {
            FindStationControllers();
        }

        private void OnEnable()
        {
            SubscribeRatchetEvents();
        }

        private void OnDisable()
        {
            UnsubscribeRatchetEvents();
            SetLeverHighlight(false);
        }

        private void FindStationControllers()
        {
            ratchetController = FindAnyObjectByType<RatchetBeltMechanicsController>();
            hookAttachment = FindAnyObjectByType<PlatformHookAttachment>();
            craneRemote = FindAnyObjectByType<NL_Remote>();
            craneAttachCargo = FindAnyObjectByType<NL_AttachCargo>();
            vibroKnob = FindAnyObjectByType<XRSteppedKnob>();
            hopperManager = FindAnyObjectByType<LiquidSolidPrototype.Core.HopperPourManager>();
            hopperHandwheel = FindAnyObjectByType<LiquidSolidPrototype.Input.HopperHandwheel>();
            hopperLiftingPoint = FindAnyObjectByType<LiquidSolidPrototype.Core.HopperCraneLiftingPoint>();
            tongsLiftingPoint = FindAnyObjectByType<Sefpro.JawTongs.JawTongsCraneLiftingPoint>();

            if (craneRemote != null)
            {
                craneRemote.enableKeyboardControls = false;
            }

            if (ratchetController != null)
            {
                ratchetController.enableLegacyMouseDrag = false;
                CacheLeverRenderers();
                SubscribeRatchetEvents();
            }
        }

        private void CacheLeverRenderers()
        {
            if (ratchetController != null && ratchetController.handleTransform != null)
            {
                leverRenderers = ratchetController.handleTransform.GetComponentsInChildren<MeshRenderer>();
            }
        }

        private void SubscribeRatchetEvents()
        {
            if (ratchetController == null) return;

            ratchetController.OnToothClicked -= HandleToothClicked;
            ratchetController.OnToothClicked += HandleToothClicked;

            ratchetController.OnStepReleased -= HandleStepReleased;
            ratchetController.OnStepReleased += HandleStepReleased;

            ratchetController.OnReleaseNotification -= HandleReleaseNotification;
            ratchetController.OnReleaseNotification += HandleReleaseNotification;

            ratchetController.OnMechanicsReset -= HandleMechanicsReset;
            ratchetController.OnMechanicsReset += HandleMechanicsReset;
        }

        private void UnsubscribeRatchetEvents()
        {
            if (ratchetController == null) return;
            ratchetController.OnToothClicked -= HandleToothClicked;
            ratchetController.OnStepReleased -= HandleStepReleased;
            ratchetController.OnReleaseNotification -= HandleReleaseNotification;
            ratchetController.OnMechanicsReset -= HandleMechanicsReset;
        }

        private void HandleReleaseNotification(string message)
        {
            toothFlashMessage = $"<color=#FFFF00><b>{message}</b></color>";
            toothFlashEndTime = Time.time + 3.5f;
        }

        private void HandleToothClicked(int toothCount)
        {
            float shorteningMm = ratchetController != null ? ratchetController.TargetShortening * 1000f : toothCount * 10f;
            if (ratchetController != null && ratchetController.IsFullyTightened)
            {
                toothFlashMessage = $"<color=#00FFAA><b>★ STROKE #{toothCount} COMPLETED: Fully Tightened State Reached (-{shorteningMm:F0} mm)! ★</b></color>";
            }
            else
            {
                toothFlashMessage = $"<color=#00FFAA><b>★ STROKE #{toothCount} COMPLETED! Mandrel +30.0°, Webbing -{shorteningMm:F0} mm ★</b></color>";
            }
            toothFlashEndTime = Time.time + 2.5f;
        }

        private void HandleStepReleased(int remainingTeeth)
        {
            float shorteningMm = ratchetController != null ? ratchetController.TargetShortening * 1000f : remainingTeeth * 10f;
            toothFlashMessage = $"<color=#FFCC00><b>▼ STEP RELEASE: 1 Tooth Paid Out (+10.0 mm). Remaining: {remainingTeeth} teeth (-{shorteningMm:F0} mm) ▼</b></color>";
            toothFlashEndTime = Time.time + 2.5f;
        }

        private void HandleMechanicsReset()
        {
            toothFlashMessage = "<color=#00E5FF><b>● RATCHET RESET: Restored Baseline Length & Rest Position ●</b></color>";
            toothFlashEndTime = Time.time + 2.0f;
        }

        private void Update()
        {
            if (playerCamera == null && playerController.CameraTransform != null)
            {
                playerCamera = playerController.CameraTransform.GetComponent<Camera>();
            }

            // Toggle help overlay
            if (Input.GetKeyDown(KeyCode.H) || Input.GetKeyDown(KeyCode.F1))
            {
                showHelpOverlay = !showHelpOverlay;
            }

            // Sync active belt with RatchetBeltMechanicsController.ActiveBeltIndex
            if (ratchetController == null || ratchetController.beltIndex != RatchetBeltMechanicsController.ActiveBeltIndex)
            {
                var allBelts = FindObjectsByType<RatchetBeltMechanicsController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                foreach (var b in allBelts)
                {
                    if (b.beltIndex == RatchetBeltMechanicsController.ActiveBeltIndex)
                    {
                        ratchetController = b;
                        hookAttachment = b.GetComponent<PlatformHookAttachment>();
                        CacheLeverRenderers();
                        SubscribeRatchetEvents();
                        break;
                    }
                }
            }

            if (isOperatingCrane)
            {
                HandleCraneOperationMode();
                return;
            }

            // While actively dragging handle, maintain exclusive lock on handle interaction
            if (isDraggingHandle)
            {
                ProcessHandleDrag();
                return;
            }

            // While actively dragging hopper handwheel, maintain exclusive lock
            if (isDraggingHopperWheel)
            {
                ProcessHopperWheelDrag();
                return;
            }

            // While actively dragging strap hook across mold, maintain exclusive lock
            if (isDraggingStrapHook)
            {
                ProcessStrapHookDrag();
                return;
            }

            // While actively dragging free-floating crane remote in 3D space
            if (isDraggingCraneRemote)
            {
                ProcessCraneRemoteDrag();
                return;
            }

            // If pressing crane pendant button
            if (activeCraneButton != null)
            {
                ProcessCraneButtonHold();
                return;
            }

            PerformRaycast();
            HandleInteractionInput();
            UpdateLeverHighlight();
            UpdateHookHighlight();
        }

        private void UpdateHookHighlight()
        {
            if (currentTargetType == TargetType.PlatformSideHook && currentTargetObject != null && isTargetInRange)
            {
                var hook = currentTargetObject.GetComponent<PlatformSideHook>() ?? currentTargetObject.GetComponentInParent<PlatformSideHook>();
                if (hook != lastHoveredHook)
                {
                    if (lastHoveredHook != null) lastHoveredHook.SetHovered(false);
                    lastHoveredHook = hook;
                    if (lastHoveredHook != null) lastHoveredHook.SetHovered(true);
                }
            }
            else if (lastHoveredHook != null)
            {
                lastHoveredHook.SetHovered(false);
                lastHoveredHook = null;
            }
        }

        private void PerformRaycast()
        {
            currentTargetType = TargetType.None;
            currentTargetObject = null;
            currentPromptText = "";
            isTargetInRange = false;

            if (playerCamera == null) return;

            Ray ray = playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0.0f));
            if (Physics.Raycast(ray, out RaycastHit hit, identificationRange, raycastLayers, QueryTriggerInteraction.Ignore))
            {
                currentTargetObject = hit.collider.gameObject;
                float dist = hit.distance;
                isTargetInRange = dist <= interactionRange;

                ClassifyTarget(currentTargetObject, hit.collider);

                if (currentTargetType != TargetType.None && !isTargetInRange)
                {
                    currentPromptText = "<b>[Too far away]</b> Get closer to interact (" + dist.ToString("F1") + "m)";
                }
            }
        }

        private void ClassifyTarget(GameObject go, Collider col)
        {
            // Dynamically switch focus if aiming at any ratchet belt component
            var hitBelt = go.GetComponentInParent<RatchetBeltMechanicsController>();
            if (hitBelt != null && hitBelt != ratchetController)
            {
                ratchetController = hitBelt;
                hookAttachment = hitBelt.GetComponent<PlatformHookAttachment>();
                RatchetBeltMechanicsController.ActiveBeltIndex = hitBelt.beltIndex;
                CacheLeverRenderers();
                SubscribeRatchetEvents();
            }

            // 1. Ratchet Belt Release Catch & Handle
            if (ratchetController != null)
            {
                if (go.name == "Handle_Release_Catch" ||
                    (ratchetController.releaseCatchTransform != null && (go.transform == ratchetController.releaseCatchTransform || go.transform.IsChildOf(ratchetController.releaseCatchTransform))))
                {
                    currentTargetType = TargetType.RatchetReleaseCatch;
                    currentPromptText = $"<b>Belt #{ratchetController.beltIndex} Release Catch</b>\n<b>[LMB]</b> <color=#FFCC00>Pull Release Catch (Full Tension Release)</color>";
                    return;
                }

                if (go.name == "Handle_Grip")
                {
                    currentTargetType = TargetType.RatchetHandle;
                    BuildRatchetHandlePrompt();
                    return;
                }

                // 2. Ratchet Belt Release / Pawl
                if (go.name == "Ratchet_Pawl" || (ratchetController.pawlTransform != null && (go.transform == ratchetController.pawlTransform || go.transform.IsChildOf(ratchetController.pawlTransform))))
                {
                    currentTargetType = TargetType.RatchetPawl;
                    currentPromptText = "<b>[LMB]</b> 1-Tooth Step Release | <b>[Hold LMB]</b> Continuous Release\n<i>[P] Power Stroke | [L] Release | [R] Reset</i>";
                    return;
                }

                // 3. J-Hooks (End Fittings)
                if ((ratchetController.fixedHookTransform != null && (go.transform == ratchetController.fixedHookTransform || go.transform.IsChildOf(ratchetController.fixedHookTransform))) ||
                    (ratchetController.adjustableHookTransform != null && (go.transform == ratchetController.adjustableHookTransform || go.transform.IsChildOf(ratchetController.adjustableHookTransform))))
                {
                    currentTargetType = TargetType.RatchetHook;
                    bool isFixed = ratchetController.fixedHookTransform != null && (go.transform == ratchetController.fixedHookTransform || go.transform.IsChildOf(ratchetController.fixedHookTransform));
                    string hookName = isFixed ? "Fixed J-Hook" : "Adjustable J-Hook";
                    bool isAttached = hookAttachment != null && (isFixed ? hookAttachment.IsFixedHookAttached : hookAttachment.IsAdjHookAttached);
                    if (isFixed)
                    {
                        currentPromptText = $"<b>{hookName}</b>\n<b>[LMB]</b> {(isAttached ? "<color=#FFFF00>Detach from Platform Hook</color>" : "<color=#00FFAA>Attach to Platform Side Hook</color>")}";
                    }
                    else
                    {
                        currentPromptText = isAttached
                            ? $"<b>{hookName}</b>\n<b>[LMB]</b> <color=#FFFF00>Detach from Platform Hook</color>"
                            : $"<b>{hookName} (Free End)</b>\n<b>[Hold LMB & Drag]</b> Pull across mold to near anchor hook";
                    }
                    return;
                }
            }

            // 4. Platform Side Hooks
            var sideHook = go.GetComponent<PlatformSideHook>() ?? go.GetComponentInParent<PlatformSideHook>();
            if (sideHook != null)
            {
                sideHook.AutoInferBeltIndex();
                currentTargetType = TargetType.PlatformSideHook;
                string hookSideStr = sideHook.side.ToString();
                string statusStr = sideHook.IsOccupied
                    ? "<color=#FFFF00>[Occupied - Click to Unhook (if tension released)]</color>"
                    : $"<color=#00FFAA>[Belt #{sideHook.assignedBeltIndex} Anchor - Pull Belt #{sideHook.assignedBeltIndex} J-Hook Here]</color>";
                currentPromptText = $"<b>Platform {hookSideStr} Side Hook (Belt #{sideHook.assignedBeltIndex})</b>\n<b>[LMB]</b> {statusStr}";
                return;
            }

            // 5. Crane Remote Buttons
            NL_RemoteButton btn = go.GetComponent<NL_RemoteButton>() ?? go.GetComponentInParent<NL_RemoteButton>();
            if (btn != null)
            {
                currentTargetType = TargetType.CraneButton;
                currentPromptText = "<b>[Hold LMB]</b> Run Crane: " + btn.gameObject.name;
                return;
            }

            // 5. Crane Remote Body
            if (craneRemote != null && (go == craneRemote.gameObject || go.transform.IsChildOf(craneRemote.transform)))
            {
                currentTargetType = TargetType.CraneRemote;
                currentPromptText = "<b>Crane Remote (Free-Floating Handheld)</b>\n<b>[E / LMB]</b> Crane Controls (WASD/QE) | <b>[Hold RMB & Drag]</b> Reposition Remote";
                return;
            }

            // 6. Vibroplatform Frequency Knob & Start/Stop Push Buttons
            XRSteppedKnob knob = go.GetComponent<XRSteppedKnob>() ?? go.GetComponentInParent<XRSteppedKnob>();
            if (knob != null)
            {
                vibroKnob = knob;
                currentTargetType = TargetType.VibroKnob;
                bool allowed = knob.IsRegulatorInteractionAllowed();
                string statusHint = allowed
                    ? "<b>[Single Press / LMB]</b> Step Up (+1) | <b>[Double Press / RMB]</b> Step Down (-1)"
                    : "<color=#FF6644><b>[Locked — Press Green START Button First]</b></color>";
                currentPromptText = $"{statusHint}\n<i>Selected: Level {knob.StepLevel} / 4 ({GetFrequencyString(knob.StepLevel)})</i>";
                return;
            }

            VibroPushButton vibroBtn = go.GetComponent<VibroPushButton>() ?? go.GetComponentInParent<VibroPushButton>();
            if (vibroBtn != null)
            {
                currentTargetType = TargetType.VibroKnob;
                string btnLabel = vibroBtn.Role == VibroPushButton.ButtonRole.Start
                    ? "<color=#00FFAA>START Vibration Motor</color>"
                    : "<color=#FF5555>STOP Vibration Motor</color>";
                currentPromptText = $"<b>Vibro Controller Push Button ({vibroBtn.gameObject.name})</b>\n<b>[LMB]</b> {btnLabel}";
                return;
            }

            // 7. Sefpro Dashboard
            if (go.name.Contains("Sefpro") && !go.name.Contains("Hopper") && !go.transform.root.name.Contains("Hopper") || go.name.Contains("Dashboard") || go.transform.root.name.Contains("SefproDashboard"))
            {
                currentTargetType = TargetType.Dashboard;
                currentPromptText = "<b>Sefpro Training Dashboard</b>\n<i>Status Display Module (Non-Interactive Visual UI)</i>";
                return;
            }

            // 8. Hopper Crane Lifting Bail (STRICT: only the bail target)
            var liftingPoint = go.GetComponent<LiquidSolidPrototype.Core.HopperCraneLiftingPoint>() ?? go.GetComponentInParent<LiquidSolidPrototype.Core.HopperCraneLiftingPoint>();
            if (liftingPoint != null || go.name.Contains("Lifting_Bail") || go.name.Contains("LiftingBail") || go.name.Contains("Lifting_Eye") || go.name.Contains("Bail"))
            {
                if (liftingPoint == null) liftingPoint = FindAnyObjectByType<LiquidSolidPrototype.Core.HopperCraneLiftingPoint>();
                currentTargetType = TargetType.HopperLiftingPoint;
                if (liftingPoint != null && liftingPoint.IsAttached)
                {
                    currentPromptText = "<b>Hopper U-Shaped Lifting Bail</b> <color=#00FFAA>[Connected to Crane Hook]</color>\n<b>[E]</b> Operate Crane (WASD/QE) | <b>[F / LMB]</b> Detach Crane Hook";
                }
                else
                {
                    currentPromptText = "<b>Hopper U-Shaped Lifting Bail</b> <color=#FFFF00>[Crane Lifting Target]</color>\n<b>[LMB]</b> Connect Crane Hook to Bail | <b>[E]</b> Crane Controls";
                }
                return;
            }

            // 8b. Jaw Tongs Central Crane Lifting Ring (Ring_Mesh)
            var tongsLift = go.GetComponent<Sefpro.JawTongs.JawTongsCraneLiftingPoint>() ?? go.GetComponentInParent<Sefpro.JawTongs.JawTongsCraneLiftingPoint>();
            if (tongsLift != null || go.name == "Ring_Mesh" || go.name.Contains("LiftingRing") || go.name.Contains("CentralLiftingRing"))
            {
                if (tongsLift == null) tongsLift = FindAnyObjectByType<Sefpro.JawTongs.JawTongsCraneLiftingPoint>();
                tongsLiftingPoint = tongsLift;
                currentTargetType = TargetType.JawTongsLiftingRing;
                if (tongsLift != null && tongsLift.IsAttached)
                {
                    currentPromptText = "<b>Jaw Tongs Central Lifting Ring</b> <color=#00FFAA>[Connected to Crane Hook]</color>\n<b>[E]</b> Operate Crane (WASD/QE) | <b>[F / LMB]</b> Detach Crane Hook";
                }
                else
                {
                    currentPromptText = "<b>Jaw Tongs Central Lifting Ring</b> <color=#FFFF00>[Crane Lifting Point]</color>\n<b>[LMB]</b> Connect Crane Hook to Ring | <b>[E]</b> Crane Controls";
                }
                return;
            }

            // 8c. Jaw Tongs Locking Pin (Pin)
            if (go.name == "Pin" && go.GetComponentInParent<Sefpro.JawTongs.JawTongsController>() != null)
            {
                var tongsCtrl = go.GetComponentInParent<Sefpro.JawTongs.JawTongsController>();
                currentTargetType = TargetType.JawTongsPin;
                if (tongsCtrl.IsPinEngaged)
                {
                    currentPromptText = "<b>Jaw Tongs Locking Pin</b> <color=#00FFAA>[ENGAGED — Both Jaw Ends Secured]</color>\n<b>[LMB]</b> <color=#FFFF00>Release Locking Pin (Unlock & Drop Both Jaw Ends)</color>";
                }
                else
                {
                    currentPromptText = "<b>Jaw Tongs Locking Pin</b> <color=#FFFF00>[RELEASED — Jaw Ends Unlocked]</color>\n<b>[LMB]</b> <color=#00FFAA>Engage Locking Pin (Secure Both Jaw Ends)</color>";
                }
                return;
            }

            // 9. SEFPRO Hopper Handwheel Rim (STRICT: only Handwheel_L_Rim and Handwheel_R_Rim trigger mixture release)
            if (go.name == "Handwheel_L_Rim" || go.name == "Handwheel_R_Rim")
            {
                currentTargetType = TargetType.HopperHandwheel;
                BuildHopperPrompt();
                return;
            }

            // 10. General Hopper Body (Informational ONLY, does NOT drag handwheel or pour)
            if (go.name.Contains("Hopper") || go.transform.root.name.Contains("SEFPRO_Hopper"))
            {
                currentTargetType = TargetType.GenericInteractable;
                currentPromptText = "<b>SEFPRO Vibrocasting Hopper Station</b>\n<i>Aim at Top Lifting Bail to Attach Crane Hook | Aim at Handwheel Rim to Rotate Gate</i>";
                return;
            }
        }

        private void BuildRatchetHandlePrompt()
        {
            if (ratchetController == null) return;

            string stateDesc = "";
            switch (ratchetController.CurrentState)
            {
                case RatchetBeltMechanicsController.StrokeState.ReadyAtRest:
                    stateDesc = ratchetController.IsFullyTightened
                        ? "<color=#00FFAA>[Fully Tightened - Next Power Stroke will Release/Loosen Belt]</color>"
                        : $"<color=#00FFAA>[Ready for Stroke #{ratchetController.CompletedStrokes + 1} - Drag Forward]</color>";
                    break;
                case RatchetBeltMechanicsController.StrokeState.Advancing:
                    stateDesc = $"<color=#FFFF00>[Advancing Stroke #{ratchetController.CompletedStrokes + 1}: {ratchetController.CurrentHandleAngle:F0}° / 55°]</color>";
                    break;
                case RatchetBeltMechanicsController.StrokeState.ToothClickedWaitingReturn:
                    stateDesc = "<color=#00FFAA>[Stroke Advance Completed! Drag Back to Neutral to Reset Pawl]</color>";
                    break;
                case RatchetBeltMechanicsController.StrokeState.Returning:
                    stateDesc = $"<color=#00E5FF>[Returning Handle to Rest: {ratchetController.CurrentHandleAngle:F0}° / 5°]</color>";
                    break;
            }

            string strokeCountText = ratchetController.IsFullyTightened
                ? "<color=#00FFAA>4 / 4 (MAX TENSION)</color>"
                : $"{ratchetController.CompletedStrokes} / 4";

            currentPromptText = "<b>Hold LMB and drag to operate ratchet lever</b>\n" +
                                $"Stroke: <b>{strokeCountText}</b> | Angle: <b>{ratchetController.CurrentHandleAngle:F0}°</b> / 65° | " +
                                $"Shortening: <b>-{(ratchetController.TargetShortening * 1000f):F0} mm</b>\n" +
                                $"{stateDesc}  <i>[P: Power Stroke | L: Release | R: Reset]</i>";
        }

        private string GetFrequencyString(int step)
        {
            int clamped = Mathf.Clamp(step, 0, VibroEffect.DefaultStepFrequencies.Length - 1);
            if (vibroKnob != null && vibroKnob.ControllerPanel != null)
                return $"{vibroKnob.ControllerPanel.GetFrequencyForStep(clamped):0.#} Hz";

            float hz = (vibroKnob != null && vibroKnob.VibrationEffect != null)
                ? vibroKnob.VibrationEffect.GetFrequencyForStep(clamped)
                : VibroEffect.DefaultStepFrequencies[clamped];
            return $"{hz:0.#} Hz";
        }

        private void HandleInteractionInput()
        {
            if (!isTargetInRange) return;

            switch (currentTargetType)
            {
                case TargetType.RatchetHandle:
                    if (Input.GetMouseButtonDown(0))
                    {
                        StartHandleDrag();
                    }
                    break;

                case TargetType.RatchetReleaseCatch:
                    if (Input.GetMouseButtonDown(0) && ratchetController != null)
                    {
                        ratchetController.TriggerFullRelease();
                    }
                    break;

                case TargetType.RatchetPawl:
                    if (Input.GetMouseButtonDown(0) && ratchetController != null)
                    {
                        ratchetController.TriggerStepRelease();
                    }
                    break;

                case TargetType.RatchetHook:
                    if (hookAttachment != null)
                    {
                        bool isFixed = ratchetController != null && ratchetController.fixedHookTransform != null &&
                            (currentTargetObject.transform == ratchetController.fixedHookTransform || currentTargetObject.transform.IsChildOf(ratchetController.fixedHookTransform));
                        if (isFixed)
                        {
                            if (Input.GetMouseButtonDown(0)) hookAttachment.ToggleFixedHook();
                        }
                        else
                        {
                            if (Input.GetMouseButtonDown(0))
                            {
                                StartStrapHookDrag();
                            }
                        }
                    }
                    break;

                case TargetType.PlatformSideHook:
                    if (Input.GetMouseButtonDown(0))
                    {
                        var hook = currentTargetObject.GetComponent<PlatformSideHook>() ?? currentTargetObject.GetComponentInParent<PlatformSideHook>();
                        if (hook != null)
                        {
                            hook.AutoInferBeltIndex();
                            if (hook.IsOccupied)
                            {
                                // Find the belt attached to this hook so detaching affects the right belt
                                var allAttachments = FindObjectsByType<PlatformHookAttachment>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                                foreach (var att in allAttachments)
                                {
                                    if (att.FixedAttachedTo == hook) { att.DetachFixedHook(); break; }
                                    if (att.AdjAttachedTo == hook) { att.DetachAdjustableHook(snapToRestPosition: false); break; }
                                }
                            }
                            else if (hookAttachment != null)
                            {
                                if (!hookAttachment.IsFixedHookAttached && hook.IsFarSide)
                                {
                                    hookAttachment.AttachFixedHook(hook);
                                }
                                else if (!hookAttachment.IsAdjHookAttached)
                                {
                                    // Require deliberate proximity & correct belt ownership (never teleport-attach across mold or to wrong hook)
                                    if (hookAttachment.CanAttachAdjustableToHook(hook, checkProximityAndWrap: true, out string rejectMsg))
                                    {
                                        hookAttachment.AttachAdjustableHook(hook);
                                    }
                                    else
                                    {
                                        toothFlashMessage = $"<color=#FF6644><b>{rejectMsg}</b></color>";
                                        toothFlashEndTime = Time.time + 3.2f;
                                    }
                                }
                            }
                        }
                    }
                    break;

                case TargetType.CraneRemote:
                    if (Input.GetKeyDown(KeyCode.E) || Input.GetMouseButtonDown(0))
                    {
                        EnterCraneOperationMode();
                    }
                    else if (Input.GetMouseButtonDown(1))
                    {
                        StartCraneRemoteDrag();
                    }
                    break;

                case TargetType.CraneButton:
                    if (Input.GetMouseButtonDown(0))
                    {
                        NL_RemoteButton btn = currentTargetObject.GetComponent<NL_RemoteButton>() ?? currentTargetObject.GetComponentInParent<NL_RemoteButton>();
                        if (btn != null)
                        {
                            activeCraneButton = btn;
                            activeCraneButton.StartHover();
                        }
                    }
                    break;

                case TargetType.VibroKnob:
                    XRSteppedKnob knob = currentTargetObject.GetComponent<XRSteppedKnob>() ?? currentTargetObject.GetComponentInParent<XRSteppedKnob>();
                    if (knob != null)
                    {
                        if (Input.GetMouseButtonDown(0))
                        {
                            knob.RegisterVRPress();
                        }
                        else if (Input.GetMouseButtonDown(1))
                        {
                            knob.StepPrevious();
                        }
                    }
                    else
                    {
                        VibroPushButton vibroBtn = currentTargetObject.GetComponent<VibroPushButton>() ?? currentTargetObject.GetComponentInParent<VibroPushButton>();
                        if (vibroBtn != null && Input.GetMouseButtonDown(0))
                        {
                            vibroBtn.PressButton();
                        }
                    }
                    break;

                case TargetType.HopperHandwheel:
                    if (hopperHandwheel == null && hopperManager != null)
                    {
                        hopperHandwheel = hopperManager.GetComponentInChildren<LiquidSolidPrototype.Input.HopperHandwheel>();
                    }

                    float scrollDelta = Input.mouseScrollDelta.y;
                    if (Mathf.Abs(scrollDelta) > 0.01f && hopperHandwheel != null)
                    {
                        hopperHandwheel.ApplyAngleDelta(scrollDelta * 20f);
                        BuildHopperPrompt();
                    }

                    if (Input.GetMouseButtonDown(0))
                    {
                        StartHopperWheelDrag();
                    }

                    if (Input.GetKeyDown(KeyCode.R) && hopperManager != null)
                    {
                        hopperManager.ResetProcess();
                        BuildHopperPrompt();
                    }
                    break;

                case TargetType.HopperLiftingPoint:
                    if (Input.GetMouseButtonDown(0))
                    {
                        if (hopperLiftingPoint == null)
                        {
                            hopperLiftingPoint = FindAnyObjectByType<LiquidSolidPrototype.Core.HopperCraneLiftingPoint>();
                        }
                        if (craneAttachCargo == null)
                        {
                            craneAttachCargo = FindAnyObjectByType<NL_AttachCargo>();
                        }

                        if (hopperLiftingPoint != null)
                        {
                            if (hopperLiftingPoint.IsAttached)
                            {
                                if (craneAttachCargo != null) craneAttachCargo.ToggleAttach();
                                toothFlashMessage = "<color=#FFFF00><b>Crane Hook Detached from Hopper Lifting Bail.</b></color>";
                                toothFlashEndTime = Time.time + 3.0f;
                            }
                            else
                            {
                                if (craneAttachCargo != null)
                                {
                                    hopperLiftingPoint.SetHookInRange(true, craneAttachCargo.transform, craneAttachCargo.rbCurrent);
                                    craneAttachCargo.Attach();
                                    if (hopperLiftingPoint.IsAttached)
                                    {
                                        toothFlashMessage = "<color=#00FFAA><b>★ Crane Hook Connected to Hopper Lifting Bail! Wait 0.75s then Press [E] for Crane Controls ★</b></color>";
                                    }
                                    else
                                    {
                                        toothFlashMessage = "<color=#FFCC00><b>Move Crane Hook to physically contact the top bent section of the Lifting Bail to connect!</b></color>";
                                    }
                                    toothFlashEndTime = Time.time + 3.5f;
                                }
                            }
                        }
                    }
                    break;

                case TargetType.JawTongsLiftingRing:
                    if (Input.GetMouseButtonDown(0))
                    {
                        if (tongsLiftingPoint == null)
                        {
                            tongsLiftingPoint = FindAnyObjectByType<Sefpro.JawTongs.JawTongsCraneLiftingPoint>();
                        }
                        if (craneAttachCargo == null)
                        {
                            craneAttachCargo = FindAnyObjectByType<NL_AttachCargo>();
                        }

                        if (tongsLiftingPoint != null && craneAttachCargo != null)
                        {
                            if (tongsLiftingPoint.IsAttached)
                            {
                                craneAttachCargo.Detach();
                                toothFlashMessage = "<color=#FFFF00><b>Crane Hook Detached from Jaw Tongs Central Lifting Ring.</b></color>";
                                toothFlashEndTime = Time.time + 3.0f;
                            }
                            else
                            {
                                tongsLiftingPoint.SetHookInRange(true, craneAttachCargo.transform, craneAttachCargo.rbCurrent);
                                craneAttachCargo.tongsLiftingTarget = tongsLiftingPoint;
                                craneAttachCargo.Attach();
                                if (tongsLiftingPoint.IsAttached)
                                {
                                    toothFlashMessage = "<color=#00FFAA><b>★ Crane Hook Connected to Jaw Tongs Central Lifting Ring! Press [E] for Crane Controls ★</b></color>";
                                }
                                else
                                {
                                    toothFlashMessage = "<color=#FFCC00><b>Move Crane Hook closer to the Central Lifting Ring to connect!</b></color>";
                                }
                                toothFlashEndTime = Time.time + 3.5f;
                            }
                        }
                    }
                    else if (Input.GetKeyDown(KeyCode.E))
                    {
                        EnterCraneOperationMode();
                    }
                    break;

                case TargetType.JawTongsPin:
                    if (Input.GetMouseButtonDown(0) && currentTargetObject != null)
                    {
                        var tongsCtrl = currentTargetObject.GetComponentInParent<Sefpro.JawTongs.JawTongsController>();
                        if (tongsCtrl != null)
                        {
                            tongsCtrl.TogglePin();
                            toothFlashMessage = tongsCtrl.IsPinEngaged
                                ? "<color=#00FFAA><b>★ Jaw Tongs Locking Pin ENGAGED — Both Jaw Ends Secured! ★</b></color>"
                                : "<color=#FFFF00><b>▼ Jaw Tongs Locking Pin RELEASED — Both Jaw Ends Unlocked! ▼</b></color>";
                            toothFlashEndTime = Time.time + 3.0f;
                        }
                    }
                    break;
            }
        }

        private void StartHandleDrag()
        {
            if (ratchetController == null || ratchetController.handleTransform == null) return;

            isDraggingHandle = true;

            // Pause camera look so mouse movement exclusively controls handle rotation
            if (playerController != null)
            {
                playerController.isLookEnabled = false;
            }

            SetLeverHighlight(true);
        }

        private void StopHandleDrag()
        {
            isDraggingHandle = false;

            // Resume first-person mouse look
            if (playerController != null)
            {
                playerController.isLookEnabled = true;
            }

            // Retain highlight only if still hovering
            UpdateLeverHighlight();
        }

        private void ProcessHandleDrag()
        {
            if (!Input.GetMouseButton(0) || ratchetController == null || ratchetController.handleTransform == null)
            {
                StopHandleDrag();
                return;
            }

            // Read raw mouse deltas
            float mouseX = Input.GetAxis("Mouse X");
            float mouseY = Input.GetAxis("Mouse Y");

            float effectiveDelta = CalculateHandleArcDelta(mouseX, mouseY);

            if (Mathf.Abs(effectiveDelta) > 0.0001f)
            {
                float targetAngle = Mathf.Clamp(ratchetController.CurrentHandleAngle + effectiveDelta,
                                                ratchetController.minHandleAngle,
                                                ratchetController.maxHandleAngle);
                ratchetController.SetHandleAngle(targetAngle);
            }

            // Update contextual prompt while dragging
            BuildRatchetHandlePrompt();
        }

        /// <summary>
        /// Projects the 2D mouse motion vector onto the handle's angular arc in screen space,
        /// blending with intuitive forward/up drag for a natural 1:1 physical feel.
        /// </summary>
        private float CalculateHandleArcDelta(float mouseX, float mouseY)
        {
            if (ratchetController == null || ratchetController.handleTransform == null || playerCamera == null)
                return (mouseY + mouseX) * handleDragSensitivity;

            Transform h = ratchetController.handleTransform;
            Vector3 pivot = h.position;

            Transform grip = h.Find("Handle_Grip");
            Vector3 gripPos = grip != null ? grip.position : pivot + h.forward * 0.16f;

            Vector3 pivotScreen = playerCamera.WorldToScreenPoint(pivot);
            if (pivotScreen.z <= 0.05f)
            {
                return (mouseY + mouseX) * handleDragSensitivity;
            }

            // Compute exact 3D hinge tangent around the coaxial mandrel axle (h.up)
            Vector3 worldArcDir = Vector3.Cross(h.up, gripPos - pivot).normalized;
            Vector3 tangentScreen = playerCamera.WorldToScreenPoint(pivot + worldArcDir * 0.12f);
            Vector2 radScreen = (Vector2)(tangentScreen - pivotScreen);
            Vector2 arcTangent = radScreen.sqrMagnitude > 1.0f ? radScreen.normalized : Vector2.up;

            Vector2 mouseDelta = new Vector2(mouseX, mouseY);
            float arcProjection = Vector2.Dot(mouseDelta, arcTangent);
            float verticalBias = mouseY;

            return (arcProjection * 0.70f + verticalBias * 0.30f) * handleDragSensitivity;
        }

        private void StartHopperWheelDrag()
        {
            if (hopperHandwheel == null && hopperManager != null)
            {
                hopperHandwheel = hopperManager.GetComponentInChildren<LiquidSolidPrototype.Input.HopperHandwheel>();
            }

            isDraggingHopperWheel = true;
            if (playerController != null)
            {
                playerController.isLookEnabled = false;
            }
        }

        private void StopHopperWheelDrag()
        {
            isDraggingHopperWheel = false;
            if (hopperHandwheel != null)
            {
                hopperHandwheel.NotifyDesktopDragEnded();
            }
            if (playerController != null)
            {
                playerController.isLookEnabled = true;
            }
            BuildHopperPrompt();
        }

        private void ProcessHopperWheelDrag()
        {
            if (!Input.GetMouseButton(0) || hopperHandwheel == null)
            {
                StopHopperWheelDrag();
                return;
            }

            float mouseX = Input.GetAxis("Mouse X");
            float mouseY = Input.GetAxis("Mouse Y");
            float dragMag = Mathf.Abs(mouseX) + Mathf.Abs(mouseY);
            if (dragMag > 0.01f)
            {
                float deltaAngle = Mathf.Max((-mouseY + mouseX * 0.5f) * 35f, dragMag * 28f);
                hopperHandwheel.ApplyAngleDelta(deltaAngle);
            }
            BuildHopperPrompt();
        }

        private void StartStrapHookDrag()
        {
            if (hookAttachment == null) return;
            if (!hookAttachment.BeginFreeEndGrab(out string blockMsg))
            {
                toothFlashMessage = $"<color=#FF6644><b>{blockMsg}</b></color>";
                toothFlashEndTime = Time.time + 3.2f;
                return;
            }

            isDraggingStrapHook = true;
            float currentBeltX = ratchetController != null ? ratchetController.transform.position.x : -22.72f;
            strapDragPlane = new Plane(Vector3.right, new Vector3(currentBeltX, 0.35f, -5.46f));
            toothFlashMessage = $"<color=#00E5FF><b>Grabbed Belt #{(ratchetController != null ? ratchetController.beltIndex : 1)} Free End: Pull Over Mold to Platform_SideHook_Near_{(ratchetController != null ? ratchetController.beltIndex : 1)}</b></color>";
            toothFlashEndTime = Time.time + 2.8f;
        }

        private void StopStrapHookDrag()
        {
            if (!isDraggingStrapHook) return;
            isDraggingStrapHook = false;

            if (hookAttachment != null)
            {
                hookAttachment.TryAttachAdjustableHookFromDrag(out string msg);
                toothFlashMessage = msg;
                toothFlashEndTime = Time.time + 3.0f;
            }
        }

        private void ProcessStrapHookDrag()
        {
            if (!Input.GetMouseButton(0))
            {
                StopStrapHookDrag();
                return;
            }

            if (playerCamera == null || hookAttachment == null) return;

            Ray ray = playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0.0f));
            Vector3 desiredWorld;

            // Raycast against environment & PlatformSideHooks while ignoring all RatchetBelt colliders
            // so the grabbed J-Hook collider never self-raycasts and flies toward the camera!
            RaycastHit[] hits = Physics.RaycastAll(ray, 4.5f, raycastLayers, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            bool foundSurfaceHit = false;
            RaycastHit bestHit = default;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider c = hits[i].collider;
                if (c == null) continue;
                if (c.GetComponentInParent<RatchetBeltMechanicsController>() != null)
                    continue;
                bestHit = hits[i];
                foundSurfaceHit = true;
                break;
            }

            if (foundSurfaceHit)
            {
                var hitHook = bestHit.collider.GetComponent<PlatformSideHook>() ?? bestHit.collider.GetComponentInParent<PlatformSideHook>();
                if (hitHook != null)
                {
                    desiredWorld = hitHook.AnchorWorldPosition;
                }
                else
                {
                    desiredWorld = bestHit.point + bestHit.normal * 0.032f;
                }
            }
            else if (strapDragPlane.Raycast(ray, out float enter))
            {
                desiredWorld = ray.GetPoint(enter);
            }
            else
            {
                desiredWorld = ray.GetPoint(1.8f);
            }

            hookAttachment.SetAdjustableHookDragPosition(desiredWorld);

            int bIdx = ratchetController != null ? ratchetController.beltIndex : 1;
            if (hookAttachment.IsWithinAttachmentRange(out float dist))
            {
                currentPromptText = $"<color=#00FFAA><b>★ Aligned with Near_{bIdx} ({dist:F2}m) — Release LMB to Connect Hook ★</b></color>";
            }
            else
            {
                currentPromptText = $"<b>Pulling Belt #{bIdx} Webbing Around Mold</b> ({dist:F2}m to Near_{bIdx}, need <= {hookAttachment.attachmentProximityThreshold:F2}m)\n<i>Release LMB anywhere to leave strap resting in place</i>";
            }
        }

        private void BuildHopperPrompt()
        {
            if (hopperHandwheel == null && hopperManager != null)
            {
                hopperHandwheel = hopperManager.GetComponentInChildren<LiquidSolidPrototype.Input.HopperHandwheel>();
            }
            if (hopperManager == null)
            {
                hopperManager = FindAnyObjectByType<LiquidSolidPrototype.Core.HopperPourManager>();
            }

            float openFrac = hopperHandwheel != null ? hopperHandwheel.OpenFraction : 0f;
            float angle = hopperHandwheel != null ? hopperHandwheel.CurrentAngle : 0f;
            float gate = openFrac * 28f;
            float rem = hopperManager != null ? hopperManager.RemainingMaterial : 100f;
            bool isDischarging = hopperManager != null && hopperManager.IsDischarging;

            currentPromptText = $"<b>[Hold LMB Drag / Scroll]</b> Rotate Discharge Handwheel | <b>[R]</b> Reset Hopper\n" +
                               $"<i>Gate: {gate:F1}° ({angle:F0}° / 720°) | Opening: {(openFrac * 100f):F0}% | Material: {rem:F1}% {(isDischarging ? "<color=#00FFAA>[POURING]</color>" : "")}</i>";
        }

        private void ProcessCraneButtonHold()
        {
            if (Input.GetMouseButton(0) && activeCraneButton != null)
            {
                activeCraneButton.HoldHover();
            }
            else
            {
                if (activeCraneButton != null)
                {
                    activeCraneButton.StopHover();
                    activeCraneButton = null;
                }
            }
        }

        private void StartCraneRemoteDrag()
        {
            if (craneRemote == null || playerCamera == null) return;

            isDraggingCraneRemote = true;
            craneRemoteDragDistance = Mathf.Clamp(
                Vector3.Distance(playerCamera.transform.position, craneRemote.transform.position),
                0.55f,
                2.8f);

            var floating = craneRemote.GetComponent<Sefpro.Crane.FreeFloatingCraneRemote>();
            if (floating != null)
                floating.BeginDesktopDrag();
        }

        private void StopCraneRemoteDrag()
        {
            if (!isDraggingCraneRemote) return;
            isDraggingCraneRemote = false;

            if (craneRemote != null)
            {
                var floating = craneRemote.GetComponent<Sefpro.Crane.FreeFloatingCraneRemote>();
                if (floating != null)
                    floating.EndDesktopDrag();
            }
        }

        private void ProcessCraneRemoteDrag()
        {
            if (!Input.GetMouseButton(1) || craneRemote == null || playerCamera == null)
            {
                StopCraneRemoteDrag();
                return;
            }

            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.01f)
            {
                craneRemoteDragDistance = Mathf.Clamp(craneRemoteDragDistance + scroll * 0.12f, 0.55f, 2.8f);
            }

            Ray ray = playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0.0f));
            Vector3 targetPos = ray.GetPoint(craneRemoteDragDistance);
            targetPos.y = Mathf.Max(targetPos.y, 0.45f);

            Vector3 toCam = playerCamera.transform.position - targetPos;
            toCam.y = 0f;
            Quaternion targetRot = toCam.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(toCam.normalized, Vector3.up)
                : craneRemote.transform.rotation;

            var floating = craneRemote.GetComponent<Sefpro.Crane.FreeFloatingCraneRemote>();
            if (floating != null)
                floating.SetSuspendedPose(targetPos, targetRot);
            else
                craneRemote.transform.SetPositionAndRotation(targetPos, targetRot);

            currentPromptText = "<color=#00FFAA><b>Repositioning Free-Floating Crane Remote</b></color>\n<i>[Scroll] Adjust Distance | Release RMB to Suspend in Place</i>";
        }

        private void UpdateLeverHighlight()
        {
            bool shouldHighlight = isDraggingHandle || (currentTargetType == TargetType.RatchetHandle && isTargetInRange);
            if (shouldHighlight != isLeverHighlighted)
            {
                SetLeverHighlight(shouldHighlight);
            }
        }

        private void SetLeverHighlight(bool highlight)
        {
            isLeverHighlighted = highlight;

            if (leverRenderers == null || leverRenderers.Length == 0)
            {
                CacheLeverRenderers();
            }

            if (leverRenderers == null) return;

            foreach (var rend in leverRenderers)
            {
                if (rend == null) continue;

                if (highlight)
                {
                    highlightPropBlock.SetColor("_EmissionColor", new Color(0.28f, 0.24f, 0.05f)); // Golden glow
                    rend.SetPropertyBlock(highlightPropBlock);
                }
                else
                {
                    rend.SetPropertyBlock(null);
                }
            }
        }

        public void EnterCraneOperationMode()
        {
            isOperatingCrane = true;
            playerController.isMovementEnabled = false;

            if (craneRemote != null)
            {
                craneRemote.enableKeyboardControls = false;
            }
        }

        public void ExitCraneOperationMode()
        {
            isOperatingCrane = false;
            playerController.isMovementEnabled = true;

            if (craneRemote != null)
            {
                craneRemote.enableKeyboardControls = false;
            }
        }

        private void HandleCraneOperationMode()
        {
            // Attach / Detach Cargo or Hopper
            if (Input.GetKeyDown(KeyCode.F))
            {
                if (craneAttachCargo != null)
                {
                    craneAttachCargo.ToggleAttach();
                }
            }

            // Exit keys
            if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Return))
            {
                ExitCraneOperationMode();
            }
        }

        private void OnGUI()
        {
            DrawReticle();
            DrawInteractionPrompt();
            DrawToothEngagementBanner();
            DrawHelpOverlay();
            DrawCraneModeBanner();
        }

        private void DrawReticle()
        {
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;

            Color reticleColor = new Color(1f, 1f, 1f, 0.45f); // Soft white
            float reticleSize = 6.0f;

            if (isDraggingHandle || isDraggingHopperWheel)
            {
                reticleColor = new Color(1.0f, 0.85f, 0.0f, 1.0f); // Bright Gold while dragging
                reticleSize = 10.0f;
            }
            else if (currentTargetType != TargetType.None)
            {
                if (isTargetInRange)
                {
                    reticleColor = new Color(0.0f, 1.0f, 0.65f, 0.95f); // Emerald / Cyan
                    reticleSize = 9.0f;
                }
                else
                {
                    reticleColor = new Color(1.0f, 0.7f, 0.1f, 0.8f); // Amber
                    reticleSize = 7.0f;
                }
            }

            GUI.color = reticleColor;
            GUI.DrawTexture(new Rect(cx - reticleSize * 0.5f, cy - reticleSize * 0.5f, reticleSize, reticleSize), Texture2D.whiteTexture);

            // Crosshair rings when targeted or dragging
            if (isDraggingHandle || isDraggingHopperWheel || (currentTargetType != TargetType.None && isTargetInRange))
            {
                float ring = 14.0f;
                GUI.DrawTexture(new Rect(cx - ring - 3, cy - 1, 4, 2), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx + ring - 1, cy - 1, 4, 2), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - 1, cy - ring - 3, 2, 4), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - 1, cy + ring - 1, 2, 4), Texture2D.whiteTexture);
            }

            GUI.color = Color.white;
        }

        private void DrawInteractionPrompt()
        {
            if (string.IsNullOrEmpty(currentPromptText) || isOperatingCrane) return;

            GUIStyle style = new GUIStyle(GUI.skin.box);
            style.alignment = TextAnchor.MiddleCenter;
            style.fontSize = 13;
            style.richText = true;
            style.normal.textColor = Color.white;

            float width = 520.0f;
            float height = 54.0f;
            float x = (Screen.width - width) * 0.5f;
            float y = Screen.height - height - 32.0f;

            GUI.Box(new Rect(x, y, width, height), currentPromptText, style);
        }

        private void DrawToothEngagementBanner()
        {
            if (Time.time > toothFlashEndTime || string.IsNullOrEmpty(toothFlashMessage)) return;

            float width = 600.0f;
            float height = 36.0f;
            float x = (Screen.width - width) * 0.5f;
            float y = 20.0f;

            GUIStyle bannerStyle = new GUIStyle(GUI.skin.box);
            bannerStyle.alignment = TextAnchor.MiddleCenter;
            bannerStyle.fontSize = 14;
            bannerStyle.richText = true;

            GUI.Box(new Rect(x, y, width, height), toothFlashMessage, bannerStyle);
        }

        private void DrawHelpOverlay()
        {
            if (!showHelpOverlay)
            {
                GUIStyle hintStyle = new GUIStyle(GUI.skin.label);
                hintStyle.alignment = TextAnchor.MiddleRight;
                hintStyle.fontSize = 11;
                hintStyle.richText = true;
                GUI.Label(new Rect(Screen.width - 160, 10, 150, 22), "<color=#888888>[H / F1: Help]</color>", hintStyle);
                return;
            }

            float pad = 12.0f;
            float w = 350.0f;
            float h = 240.0f;

            float x = Screen.width - w - pad;
            float y = pad;

            GUI.Box(new Rect(x, y, w, h), "<b>Desktop Testing Controls (F1/H: Toggle)</b>");

            GUILayout.BeginArea(new Rect(x + 10, y + 25, w - 20, h - 30));
            GUILayout.Label("<b>[WASD]</b> Move Player | <b>[Shift]</b> Sprint");
            GUILayout.Label("<b>[Mouse]</b> Look Around | <b>[Space]</b> Jump");
            GUILayout.Label("<b>[LMB Drag]</b> Move Ratchet Lever (0°..65°)");
            GUILayout.Label("<b>[Esc]</b> Free Cursor | <b>[Click View]</b> Lock Cursor");
            GUILayout.Space(2);
            GUILayout.Label("<b>SEFPRO Hopper Station:</b>");
            GUILayout.Label("• <b>[LMB Drag / Scroll]</b> Rotate Handwheel");
            GUILayout.Label("• <b>[R]</b> Reset Hopper Fill & Gate");
            GUILayout.Space(2);
            GUILayout.Label("<b>Ratchet Belt Hotkeys:</b>");
            GUILayout.Label("• <b>[P]</b> Power Stroke | <b>[L]</b> Step Release");
            GUILayout.EndArea();
        }

        private void DrawCraneModeBanner()
        {
            if (!isOperatingCrane) return;

            float w = 580.0f;
            float h = 95.0f;
            float x = (Screen.width - w) * 0.5f;
            float y = 20.0f;

            GUIStyle bannerStyle = new GUIStyle(GUI.skin.box);
            bannerStyle.alignment = TextAnchor.MiddleCenter;
            bannerStyle.fontSize = 14;
            bannerStyle.richText = true;

            string bannerText = "<color=#00ffcc><b>★ CRANE REMOTE PENDANT ACTIVE ★</b></color>\n" +
                                "<b>[W / S]</b> Gantry Forward / Backward  |  <b>[A / D]</b> Trolley Left / Right\n" +
                                "<b>[Q / E]</b> Hoist Down / Up  |  <b>[F]</b> Attach / Detach Cargo & Hopper  |  <b>[Space / Esc]</b> Exit";

            GUI.Box(new Rect(x, y, w, h), bannerText, bannerStyle);
        }
    }
}
