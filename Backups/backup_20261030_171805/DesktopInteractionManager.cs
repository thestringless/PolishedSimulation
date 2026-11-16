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
            RatchetPawl,
            RatchetHook,
            PlatformSideHook,
            CraneRemote,
            CraneButton,
            VibroKnob,
            Dashboard,
            HopperHandwheel,
            HopperLiftingPoint,
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
        public bool showHelpOverlay = true;

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

        // Lever Highlighting
        private MaterialPropertyBlock highlightPropBlock;
        private MeshRenderer[] leverRenderers;
        private bool isLeverHighlighted = false;

        // Handle Dragging State
        private bool isDraggingHandle = false;
        private bool isDraggingHopperWheel = false;

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
            // 1. Ratchet Belt Handle (HIGHEST PRIORITY: Never grab hooks/body when lever is aimed at)
            if (ratchetController != null)
            {
                if (go.name == "Ratchet_Handle" || go.name == "Handle_Grip" ||
                    (ratchetController.handleTransform != null && (go.transform == ratchetController.handleTransform || go.transform.IsChildOf(ratchetController.handleTransform))))
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
                    currentPromptText = $"<b>{hookName}</b>\n<b>[LMB]</b> {(isAttached ? "<color=#FFFF00>Detach from Platform Hook</color>" : "<color=#00FFAA>Attach to Platform Side Hook</color>")}";
                    return;
                }
            }

            // 4. Platform Side Hooks
            var sideHook = go.GetComponent<PlatformSideHook>() ?? go.GetComponentInParent<PlatformSideHook>();
            if (sideHook != null)
            {
                currentTargetType = TargetType.PlatformSideHook;
                string hookSideStr = sideHook.side.ToString();
                string statusStr = sideHook.IsOccupied ? "<color=#FFFF00>[Occupied - Click to Detach]</color>" : "<color=#00FFAA>[Open - Click to Attach J-Hook]</color>";
                currentPromptText = $"<b>Platform {hookSideStr} Side Hook</b>\n<b>[LMB]</b> {statusStr}";
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
                currentPromptText = "<b>[E / LMB]</b> Take Crane Controls (Pendant Mode)";
                return;
            }

            // 6. Vibroplatform Frequency Knob
            XRSteppedKnob knob = go.GetComponent<XRSteppedKnob>() ?? go.GetComponentInParent<XRSteppedKnob>();
            if (knob != null)
            {
                currentTargetType = TargetType.VibroKnob;
                currentPromptText = "<b>[LMB]</b> Next Frequency Step | <b>[RMB]</b> Previous Step\n<i>Current: Step " + knob.StepLevel + " / 4 (" + GetFrequencyString(knob.StepLevel) + ")</i>";
                return;
            }

            // 7. Sefpro Dashboard
            if (go.name.Contains("Sefpro") && !go.name.Contains("Hopper") && !go.transform.root.name.Contains("Hopper") || go.name.Contains("Dashboard") || go.transform.root.name.Contains("SefproDashboard"))
            {
                currentTargetType = TargetType.Dashboard;
                currentPromptText = "<b>Sefpro Training Dashboard</b>\n<i>Status Display Module (Non-Interactive Visual UI)</i>";
                return;
            }

            // 8. Hopper Crane Lifting Eye
            var liftingPoint = go.GetComponent<LiquidSolidPrototype.Core.HopperCraneLiftingPoint>() ?? go.GetComponentInParent<LiquidSolidPrototype.Core.HopperCraneLiftingPoint>();
            if (liftingPoint != null || go.name.Contains("Lifting_Eye") || go.name.Contains("LiftingEye"))
            {
                currentTargetType = TargetType.HopperLiftingPoint;
                string attachStatus = (liftingPoint != null && liftingPoint.IsAttached) ? "<color=#00FFAA>[Attached to Crane Hook]</color>" : "<color=#FFFF00>[Crane Hook Lifting Target]</color>";
                currentPromptText = $"<b>Hopper Structural Lifting Eye</b>\n{attachStatus} <i>Lower crane hook and press [F] in crane pendant mode</i>";
                return;
            }

            // 9. SEFPRO Hopper Handwheel & Station
            var hw = go.GetComponent<LiquidSolidPrototype.Input.HopperHandwheel>() ?? go.GetComponentInParent<LiquidSolidPrototype.Input.HopperHandwheel>();
            if (hw != null || go.name.Contains("Handwheel") || go.name.Contains("Hopper") || go.transform.root.name.Contains("SEFPRO_Hopper"))
            {
                currentTargetType = TargetType.HopperHandwheel;
                BuildHopperPrompt();
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
            switch (step)
            {
                case 0: return "0.0 Hz (Off)";
                case 1: return "6.0 Hz (Low)";
                case 2: return "13.3 Hz (Medium)";
                case 3: return "20.7 Hz (High)";
                case 4: return "28.0 Hz (Max)";
                default: return "Active";
            }
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

                case TargetType.RatchetPawl:
                    if (Input.GetMouseButtonDown(0) && ratchetController != null)
                    {
                        ratchetController.TriggerStepRelease();
                    }
                    break;

                case TargetType.RatchetHook:
                    if (Input.GetMouseButtonDown(0) && hookAttachment != null)
                    {
                        bool isFixed = ratchetController != null && ratchetController.fixedHookTransform != null &&
                            (currentTargetObject.transform == ratchetController.fixedHookTransform || currentTargetObject.transform.IsChildOf(ratchetController.fixedHookTransform));
                        if (isFixed) hookAttachment.ToggleFixedHook();
                        else hookAttachment.ToggleAdjustableHook();
                    }
                    break;

                case TargetType.PlatformSideHook:
                    if (Input.GetMouseButtonDown(0) && hookAttachment != null)
                    {
                        var hook = currentTargetObject.GetComponent<PlatformSideHook>() ?? currentTargetObject.GetComponentInParent<PlatformSideHook>();
                        if (hook != null)
                        {
                            if (hook.IsOccupied)
                            {
                                if (hookAttachment.FixedAttachedTo == hook) hookAttachment.DetachFixedHook();
                                else if (hookAttachment.AdjAttachedTo == hook) hookAttachment.DetachAdjustableHook();
                            }
                            else
                            {
                                if (!hookAttachment.IsFixedHookAttached) hookAttachment.AttachFixedHook(hook);
                                else if (!hookAttachment.IsAdjHookAttached) hookAttachment.AttachAdjustableHook(hook);
                            }
                        }
                    }
                    break;

                case TargetType.CraneRemote:
                    if (Input.GetKeyDown(KeyCode.E) || Input.GetMouseButtonDown(0))
                    {
                        EnterCraneOperationMode();
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
                            knob.StepNext();
                        }
                        else if (Input.GetMouseButtonDown(1))
                        {
                            knob.StepPrevious();
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
            Vector3 gripPos = grip != null ? grip.position : pivot + h.forward * 0.2f;

            Vector3 pivotScreen = playerCamera.WorldToScreenPoint(pivot);
            Vector3 gripScreen = playerCamera.WorldToScreenPoint(gripPos);

            if (pivotScreen.z <= 0.05f || gripScreen.z <= 0.05f)
            {
                return (mouseY + mouseX) * handleDragSensitivity;
            }

            Vector2 radScreen = (Vector2)(gripScreen - pivotScreen);
            Vector2 arcTangent = radScreen.sqrMagnitude > 1.0f
                ? new Vector2(-radScreen.y, radScreen.x).normalized
                : Vector2.up;

            Vector2 mouseDelta = new Vector2(mouseX, mouseY);
            float arcProjection = Vector2.Dot(mouseDelta, arcTangent);

            // Forward/Up drag bias (+Y is up/away from player)
            float verticalBias = mouseY;

            // Blend 65% arc projection with 35% vertical bias
            float combined = (arcProjection * 0.65f + verticalBias * 0.35f) * handleDragSensitivity;
            return combined;
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
            if (playerController != null)
            {
                playerController.isLookEnabled = true;
            }
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
            float deltaAngle = (-mouseY + mouseX * 0.5f) * 25f;

            hopperHandwheel.ApplyAngleDelta(deltaAngle);
            BuildHopperPrompt();
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
                craneRemote.enableKeyboardControls = true;
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

            float width = 460.0f;
            float height = 64.0f;
            float x = (Screen.width - width) * 0.5f;
            float y = Screen.height * 0.5f + 24.0f;

            GUI.Box(new Rect(x, y, width, height), currentPromptText, style);
        }

        private void DrawToothEngagementBanner()
        {
            if (Time.time > toothFlashEndTime || string.IsNullOrEmpty(toothFlashMessage)) return;

            float width = 560.0f;
            float height = 38.0f;
            float x = (Screen.width - width) * 0.5f;
            float y = Screen.height * 0.5f - 75.0f;

            GUIStyle bannerStyle = new GUIStyle(GUI.skin.box);
            bannerStyle.alignment = TextAnchor.MiddleCenter;
            bannerStyle.fontSize = 14;
            bannerStyle.richText = true;

            GUI.Box(new Rect(x, y, width, height), toothFlashMessage, bannerStyle);
        }

        private void DrawHelpOverlay()
        {
            if (!showHelpOverlay) return;

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
