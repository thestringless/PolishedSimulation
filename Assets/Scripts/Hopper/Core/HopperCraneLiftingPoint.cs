using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using NOT_Lonely;
using Sefpro.Crane;

namespace LiquidSolidPrototype.Core
{
    [ExecuteAlways]
    [RequireComponent(typeof(Collider))]
    public class HopperCraneLiftingPoint : MonoBehaviour
    {
        public const float ExactBailBarLocalZ = 2.0266f;
        public const float ExactHookSaddleOffset = 0.2984f;

        [Header("Hopper Root")]
        [Tooltip("The main structural root transform of the hopper station that moves as a coherent whole.")]
        public Transform hopperRoot;

        [Header("Equipment Specifications")]
        [Tooltip("Nominal structural equipment mass in kilograms (rigid, non-physical).")]
        public float fixedMassKg = 1250f;

        [Header("State (Read Only)")]
        [SerializeField] private bool isAttached = false;
        [SerializeField] private bool isCapturing = false;
        [SerializeField] private bool isHookInRange = false;
        [SerializeField] private Transform currentHookMount;
        [SerializeField] private Rigidbody currentHookRb;

        [Header("Warehouse Physical Limits")]
        public float minGroundY = 0.0f;
        public Vector2 xBounds = new Vector2(-32.0f, -1.5f);
        public Vector2 zBounds = new Vector2(-15.5f, -2.5f);

        [Header("Lifting Attachment Reference Points (Measured from Scene Geometry)")]
        [Tooltip("Local offset on Lifting_Bail_Anchor to the exact 3D center of the 24.00mm diameter top horizontal bail bar.")]
        public Vector3 bailCrossbarOffset = new Vector3(0.0000f, 0.0000f, ExactBailBarLocalZ);

        [Tooltip("Exact offset (0.2984m) from WireHookMount/Hook_mesh origin down to the C-hook curved saddle center (inner floor -0.31042m + 0.01200m bar radius).")]
        public float hookSaddleOffset = ExactHookSaddleOffset;

        [Header("C-Hook Pocket Seating Tolerances")]
        public float maxHorizontalSeatingDistance = 0.065f;
        public Vector2 verticalSeatingRange = new Vector2(-0.020f, 0.020f);
        public float maxTotalSeatingDistance = 0.075f;
        public float maxPhysicalSurfaceContactGap = 0.022f;

        /// <summary>
        /// Explicit lifecycle for hopper crane proximity capture, connection confirmation, lifting, and automatic landing release.
        /// </summary>
        public enum HopperConnectionState
        {
            Disconnected,
            Capturing,
            ContactValidated,
            Connected,
            LiftAuthorized,
            Lifting,
            Grounded,
        }

        // Legacy enum kept for backwards compatibility
        public enum HopperLiftState
        {
            Disconnected,
            ConnectedWaiting,
            ConnectedReadyToLift
        }

        [Header("Automatic Proximity-Assisted Capture (<= 0.50 m)")]
        [Tooltip("Enable automatic controlled C-hook capture when the hook saddle comes within autoConnectRadius (0.50 m) of the bail center.")]
        public bool autoConnect = true;

        [Tooltip("Capture trigger distance (m) between C-hook saddle reference point and bail top horizontal bar center (0.50 m).")]
        public float autoConnectRadius = 0.50f;

        [Tooltip("Duration (s) of the smooth controlled C-hook capture alignment glide onto the bail (~0.35 s).")]
        public float captureGlideDuration = 0.35f;

        [Tooltip("Allowed vertical window used only if direct non-captured manual validation is queried.")]
        public Vector2 autoConnectVerticalWindow = new Vector2(-0.50f, 0.50f);

        [Tooltip("Maximum tilt (degrees) of the hook's up axis from world up.")]
        public float autoConnectMaxHookTiltDegrees = 60f;

        [Tooltip("Maximum hook speed (m/s) allowed to initiate capture.")]
        public float autoConnectMaxHookSpeed = 4.0f;

        [Header("Lift Authorization & Ground Release Timings")]
        [Tooltip("Seconds after C-hook seating before the crane is allowed to lift the hopper (0.75 s).")]
        public float liftAuthorizationDelay = 0.75f;

        [Tooltip("Minimum clearance above ground (m) required to mark the hopper as actively lifted (Lifting state).")]
        public float liftedClearanceThreshold = 0.035f;

        [Tooltip("Maximum clearance above ground (m) considered physical ground contact during landing.")]
        public float groundedClearanceThreshold = 0.008f;

        [Tooltip("Seconds of continuous stable ground contact after lifting before automatic disconnection (0.40 s).")]
        public float groundReleaseDelay = 0.40f;

        [SerializeField] private HopperConnectionState connectionState = HopperConnectionState.Disconnected;
        [SerializeField] private HopperLiftState liftState = HopperLiftState.Disconnected;
        private float captureTimer;
        private float waitTimer;
        private float groundedTimer;
        private bool hasLeftGroundThisCycle;
        private float maxClearanceReachedThisCycle;
        private bool hasCompletedGroundRelease;
        private float lastHookSaddleY;
        private float lastHoistUpTimestamp = -10f;
        private float lastMeasuredCaptureTriggerDistance = -1f;
        private int totalCompletedCaptures = 0;

        [Tooltip("After disconnection, automatic capture re-arms only once the C-hook saddle has moved farther than this distance (m) from the bail (> 0.50 m to prevent oscillation).")]
        public float autoConnectRearmDistance = 0.62f;

        [Tooltip("If the hook saddle is externally forced farther than this distance (m) from the bail during the 0.75s confirmation delay, connection cancels.")]
        public float waitingAbortDistance = 0.12f;

        [Header("Connection Status UI")]
        [Tooltip("World-Space TextMeshPro indicators displaying the 6-stage operator status.")]
        public List<TMP_Text> statusTextIndicators = new List<TMP_Text>();

        private bool autoConnectArmed = true;
        private Transform lastDetachedHook;
        private bool following;
        private Vector3 followOffset;
        private Quaternion followRotation;

        // Controlled capture interpolation state
        private Vector3 _captureStartHookMountPos;
        private Quaternion _captureStartHookMountRot;
        private float _captureStartBeamPos;
        private float _captureStartTelpherPos;
        private float _captureStartHookHeight;
        private float _captureTargetBeamPos;
        private float _captureTargetTelpherPos;
        private float _captureTargetHookHeight;
        private bool _hasCraneCaptureTargets;
        private bool _collisionsIgnoredWithHook;

        private Rigidbody hopperRb;
        private Transform originalParent;
        private Collider topBendCollider;
        private Collider[] bailColliders;
        private Collider[] allHopperColliders;
        private Collider[] hookColliders;
        private Transform cachedHookMount;
        private Transform cachedHookMesh;
        private NL_Hook cachedNlHook;
        private NL_OverheadCrane cachedCrane;
        private NL_AttachCargo cachedAttachCargo;

        /// <summary>
        /// Exact 3D world position of the C-hook's curved load-bearing saddle reference point
        /// (local (0, -0.2984, 0) in Hook_mesh / WireHookMount space).
        /// </summary>
        public Vector3 SaddleWorld(Transform hookMount)
        {
            if (hookMount == null)
                return transform.TransformPoint(bailCrossbarOffset);

            if (cachedHookMesh != null && cachedHookMount == hookMount)
            {
                return cachedHookMesh.TransformPoint(new Vector3(0f, -hookSaddleOffset, 0f));
            }
            return hookMount.position - hookMount.up * hookSaddleOffset;
        }

        public bool IsAttached => isAttached;
        public bool IsCapturing => isCapturing;
        public HopperConnectionState ConnectionState => connectionState;
        public HopperLiftState LiftState => liftState;
        public float CurrentCaptureTimer => captureTimer;
        public float CurrentWaitTimer => waitTimer;
        public float CurrentGroundedTimer => groundedTimer;
        public bool HasLeftGroundThisCycle => hasLeftGroundThisCycle;
        public bool IsAutoConnectArmed => autoConnectArmed;
        public bool HasCompletedGroundRelease => hasCompletedGroundRelease;
        public float LastMeasuredCaptureTriggerDistance => lastMeasuredCaptureTriggerDistance;
        public int TotalCompletedCaptures => totalCompletedCaptures;

        /// <summary>True only when connected AND the post-connection 0.75s delay has elapsed (LiftAuthorized, Lifting, or Grounded before release).</summary>
        public bool IsLiftAuthorized => isAttached && (connectionState == HopperConnectionState.LiftAuthorized ||
                                                       connectionState == HopperConnectionState.Lifting ||
                                                       connectionState == HopperConnectionState.Grounded);

        /// <summary>True while attached but lifting is still blocked by the 0.75s connection delay.</summary>
        public bool IsLiftBlockedWhileConnected => isAttached && !IsLiftAuthorized;

        public bool IsHookInRange => isHookInRange;
        public Transform CurrentHookMount => currentHookMount;

        /// <summary>Exact 3D world center of the hopper lifting bail's top horizontal bar.</summary>
        public Vector3 CrossbarWorldPosition => transform.TransformPoint(bailCrossbarOffset);

        /// <summary>Unit world vector along the hopper lifting bail's top horizontal bar.</summary>
        public Vector3 BailHorizontalBarAxisWorld => transform.right.normalized;

        [System.Serializable]
        private struct CasterWheelInfo
        {
            public Transform wheelTransform;
            public MeshRenderer renderer;
            public float wheelRadius;
        }

        private List<CasterWheelInfo> casterWheels = new List<CasterWheelInfo>();

        private void InitCasters()
        {
            if (casterWheels != null && casterWheels.Count > 0)
                return;

            if (hopperRoot == null)
                hopperRoot = transform.root;

            var allTransforms = hopperRoot.GetComponentsInChildren<Transform>(true);
            foreach (var t in allTransforms)
            {
                if (t.name.Contains("Caster") && t.name.Contains("Wheel"))
                {
                    var mr = t.GetComponent<MeshRenderer>();
                    var mf = t.GetComponent<MeshFilter>();
                    float radius = 0.095f;
                    if (mr != null)
                    {
                        radius = mr.bounds.extents.y;
                    }
                    else if (mf != null && mf.sharedMesh != null)
                    {
                        var b = mf.sharedMesh.bounds;
                        radius = Mathf.Max(b.extents.x, b.extents.y, b.extents.z) * t.lossyScale.y;
                    }

                    casterWheels.Add(new CasterWheelInfo
                    {
                        wheelTransform = t,
                        renderer = mr,
                        wheelRadius = radius
                    });
                }
            }
        }

        public void ConfigureTopBendCollider()
        {
            // Migrate legacy offsets to exact triangle-measured scene geometry values
            if (Mathf.Abs(bailCrossbarOffset.z - 2.0194f) < 0.01f)
            {
                bailCrossbarOffset = new Vector3(0.0000f, 0.0000f, ExactBailBarLocalZ);
            }
            if (Mathf.Abs(hookSaddleOffset - 0.365f) < 0.01f || Mathf.Abs(hookSaddleOffset - 0.324f) < 0.01f)
            {
                hookSaddleOffset = ExactHookSaddleOffset;
            }
            if (autoConnectRadius < 0.45f)
            {
                autoConnectRadius = 0.50f;
            }
            if (autoConnectRearmDistance <= autoConnectRadius + 0.05f)
            {
                autoConnectRearmDistance = 0.62f;
            }

            var cap = GetComponent<CapsuleCollider>();
            if (cap != null)
            {
                cap.center = bailCrossbarOffset;
                cap.direction = 0; // Local X-axis along the 99.4 mm wide top horizontal bar
                cap.radius = 0.012f; // Exact 24.00 mm diameter steel bar
                cap.height = 0.100f; // Exact 99.4 mm top arch width
                cap.isTrigger = false;
                topBendCollider = cap;
            }
            else
            {
                topBendCollider = GetComponent<Collider>();
            }
        }

        public void ConfigureHookCShapeColliders(Transform hookMount = null)
        {
            if (hookMount == null)
            {
                if (cachedAttachCargo == null)
                    cachedAttachCargo = FindAnyObjectByType<NL_AttachCargo>();
                if (cachedAttachCargo != null)
                    hookMount = cachedAttachCargo.transform;
            }
            if (hookMount == null)
                return;

            cachedHookMount = hookMount;
            cachedAttachCargo = hookMount.GetComponent<NL_AttachCargo>();
            if (cachedAttachCargo != null)
            {
                cachedAttachCargo.connectPointOffset = hookSaddleOffset;
            }

            if (cachedNlHook == null)
                cachedNlHook = hookMount.GetComponentInParent<NL_Hook>();
            if (cachedCrane == null)
            {
                cachedCrane = cachedNlHook != null && cachedNlHook.Crane != null
                    ? cachedNlHook.Crane
                    : FindAnyObjectByType<NL_OverheadCrane>();
            }

            if (!Application.isPlaying)
            {
                hookMount.localEulerAngles = Vector3.zero;
            }
            else
            {
                var cj = hookMount.GetComponent<ConfigurableJoint>();
                if (cj != null)
                {
                    cj.targetRotation = Quaternion.identity;
                }
            }

            Transform hookMesh = hookMount.Find("Hook_mesh");
            if (hookMesh == null)
            {
                var children = hookMount.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < children.Length; i++)
                {
                    if (children[i].name == "Hook_mesh")
                    {
                        hookMesh = children[i];
                        break;
                    }
                }
            }
            if (hookMesh == null)
                return;

            cachedHookMesh = hookMesh;
            hookMesh.localPosition = Vector3.zero;
            hookMesh.localEulerAngles = new Vector3(0f, 90f, 0f);
            hookMesh.localScale = Vector3.one;

            var mc = hookMesh.GetComponent<MeshCollider>();
            if (mc != null)
            {
                mc.enabled = false;
            }

            var boxes = hookMesh.GetComponents<BoxCollider>();
            while (boxes.Length < 3)
            {
                hookMesh.gameObject.AddComponent<BoxCollider>();
                boxes = hookMesh.GetComponents<BoxCollider>();
            }

            // Box 0: Upper Shank & Pocket Roof (bottom face at local Y = -0.252 m, matching exact triangle pocket roof -0.2517 m)
            boxes[0].isTrigger = false;
            boxes[0].enabled = true;
            boxes[0].center = new Vector3(0.000f, -0.066f, 0.000f);
            boxes[0].size = new Vector3(0.070f, 0.372f, 0.050f);

            // Box 1: C-Hook Back Spine (inner front wall at local X = -0.0215 m, matching exact triangle back wall -0.02151 m)
            boxes[1].isTrigger = false;
            boxes[1].enabled = true;
            boxes[1].center = new Vector3(-0.0465f, -0.279f, 0.000f);
            boxes[1].size = new Vector3(0.050f, 0.158f, 0.050f);

            // Box 2: C-Hook Lower Saddle Floor & Bill (top load-bearing surface at exact local Y = -0.3104 m, outer bottom at -0.3583 m)
            boxes[2].isTrigger = false;
            boxes[2].enabled = true;
            boxes[2].center = new Vector3(0.008f, -0.3344f, 0.000f);
            boxes[2].size = new Vector3(0.084f, 0.0479f, 0.046f);

            for (int i = 3; i < boxes.Length; i++)
            {
                boxes[i].enabled = false;
            }
        }

        /// <summary>
        /// Calculates the exact seated world position and rotation for WireHookMount directly from the live
        /// Lifting_Bail transform and Hook_mesh local orientation so:
        /// - Hook_mesh.forward (cylindrical C-hook saddle channel) is coaxial with BailHorizontalBarAxisWorld.
        /// - Hook_mesh.up is aligned with Vector3.up.
        /// - Hook_mesh saddle point (0, -0.2984, 0) coincides 1:1 with CrossbarWorldPosition (zero mesh intersection, zero gap).
        /// </summary>
        public void ComputeTargetHookMountWorldPose(Transform hookMount, out Vector3 targetMountPos, out Quaternion targetMountRot)
        {
            Vector3 bailCenter = CrossbarWorldPosition;
            Vector3 bailAxis = BailHorizontalBarAxisWorld;
            if (bailAxis.sqrMagnitude < 1e-4f)
                bailAxis = Vector3.right;

            Quaternion desiredHookMeshWorldRot = Quaternion.LookRotation(bailAxis, Vector3.up);
            Quaternion hookMeshLocalRot = (cachedHookMesh != null && cachedHookMount == hookMount)
                ? cachedHookMesh.localRotation
                : Quaternion.Euler(0f, 90f, 0f);

            targetMountRot = desiredHookMeshWorldRot * Quaternion.Inverse(hookMeshLocalRot);
            targetMountPos = bailCenter + (targetMountRot * Vector3.up) * hookSaddleOffset;
        }

        /// <summary>
        /// Solves the exact overhead crane normalized coordinates (initialBeamPos, initialTelpherPos, hookHeight)
        /// that place the crane hoist cable plumb above targetMountPos in PhysX equilibrium.
        /// </summary>
        private bool SolvePlumbCraneCoordinatesForMountPos(Vector3 targetMountPos, out float solvedBeamPos, out float solvedTelpherPos, out float solvedHookHeight)
        {
            solvedBeamPos = 0f;
            solvedTelpherPos = 0f;
            solvedHookHeight = 0f;

            if (cachedCrane == null || cachedNlHook == null || cachedNlHook.hookJoint == null)
                return false;

            float origBeam = cachedCrane.initialBeamPos;
            float origTelpher = cachedCrane.initialTelpherPos;
            float origHook = cachedCrane.hookHeight;

            float b = origBeam;
            float tp = origTelpher;
            float hh = origHook;

            Transform connTf = cachedNlHook.hookJoint.connectedBody != null
                ? cachedNlHook.hookJoint.connectedBody.transform
                : cachedNlHook.wireHookMount.parent;

            float beamSpan = Mathf.Max(0.1f, cachedCrane.craneLength - 2.316f);
            float telpherSpan = Mathf.Max(0.1f, cachedCrane.craneWidth - 1.57f);
            float hoistSpan = Mathf.Max(0.1f, (cachedCrane.craneHeight + cachedNlHook.hookOffset) - 0.5f);

            for (int iter = 0; iter < 12; iter++)
            {
                cachedCrane.initialBeamPos = b;
                cachedCrane.initialTelpherPos = tp;
                cachedCrane.hookHeight = hh;
                cachedCrane.UpdateMainBeamPos(b);
                cachedCrane.UpdateTelpherPos(tp);
                cachedCrane.UpdateHookHeight();

                Vector3 worldConnAnchor = connTf.TransformPoint(cachedNlHook.hookJoint.connectedAnchor);
                Vector3 eqMountPos = worldConnAnchor - Vector3.up * cachedNlHook.hookJoint.anchor.y;

                float errX = targetMountPos.x - eqMountPos.x;
                float errZ = targetMountPos.z - eqMountPos.z;
                float errY = targetMountPos.y - eqMountPos.y;

                b = Mathf.Clamp01(b - errX / beamSpan);
                tp = Mathf.Clamp01(tp - errZ / telpherSpan);
                hh = Mathf.Clamp01(hh + errY / hoistSpan);
            }

            // Restore current crane pose after solving so capture glides smoothly from current pose
            cachedCrane.initialBeamPos = origBeam;
            cachedCrane.initialTelpherPos = origTelpher;
            cachedCrane.hookHeight = origHook;
            cachedCrane.UpdateMainBeamPos(origBeam);
            cachedCrane.UpdateTelpherPos(origTelpher);
            cachedCrane.UpdateHookHeight();

            solvedBeamPos = b;
            solvedTelpherPos = tp;
            solvedHookHeight = hh;
            return true;
        }

        private void SetHookHopperCollisionsIgnored(Transform hookMount, bool ignore)
        {
            if (hookMount == null)
                return;

            if (allHopperColliders == null || allHopperColliders.Length == 0)
            {
                if (hopperRoot == null) hopperRoot = transform.root;
                allHopperColliders = hopperRoot.GetComponentsInChildren<Collider>(true);
            }
            hookColliders = hookMount.GetComponentsInChildren<Collider>(true);

            if (allHopperColliders != null && hookColliders != null)
            {
                for (int i = 0; i < allHopperColliders.Length; i++)
                {
                    var hCol = allHopperColliders[i];
                    if (hCol == null) continue;
                    for (int j = 0; j < hookColliders.Length; j++)
                    {
                        var cCol = hookColliders[j];
                        if (cCol != null)
                        {
                            Physics.IgnoreCollision(hCol, cCol, ignore);
                        }
                    }
                }
            }
            _collisionsIgnoredWithHook = ignore;
        }

        private void ApplyHookMountPoseClean(Transform hookMount, Rigidbody hookRb, Vector3 worldPos, Quaternion worldRot)
        {
            if (cachedNlHook != null && cachedNlHook.wireHookMount == hookMount)
            {
                cachedNlHook.SetHookWorldPoseAndZeroVelocity(worldPos, worldRot);
            }
            else if (hookMount != null)
            {
                hookMount.SetPositionAndRotation(worldPos, worldRot);
                if (hookRb != null && Application.isPlaying)
                {
                    hookRb.position = worldPos;
                    hookRb.rotation = worldRot;
                    hookRb.linearVelocity = Vector3.zero;
                    hookRb.angularVelocity = Vector3.zero;
                }
            }
        }

        private void StopActiveCraneRemoteButtonsOnCapture()
        {
            var floatingRemote = FindAnyObjectByType<FreeFloatingCraneRemote>();
            if (floatingRemote != null)
            {
                floatingRemote.StopAllCraneButtonsAndMotion();
                return;
            }
            var nlRemote = FindAnyObjectByType<NL_Remote>();
            if (nlRemote != null)
            {
                nlRemote.enableKeyboardControls = false;
                nlRemote.StopAllButtonCommandsImmediate();
            }
        }

        private void Awake()
        {
            if (hopperRoot == null)
                hopperRoot = transform.root;

            InitCasters();
            ConfigureTopBendCollider();
            ConfigureHookCShapeColliders();

            originalParent = hopperRoot.parent;
            bailColliders = GetComponentsInChildren<Collider>(true);
            allHopperColliders = hopperRoot.GetComponentsInChildren<Collider>(true);

            hopperRb = hopperRoot.GetComponent<Rigidbody>();
            if (hopperRb == null)
                hopperRb = hopperRoot.gameObject.AddComponent<Rigidbody>();

            hopperRb.isKinematic = true;
            hopperRb.useGravity = false;
            hopperRb.mass = fixedMassKg;
            hopperRb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            BindStatusIndicators();
            UpdateStatusUI();
        }

        private void OnEnable()
        {
            ConfigureTopBendCollider();
            ConfigureHookCShapeColliders();
            BindStatusIndicators();
            UpdateStatusUI();
        }

        public void BindStatusIndicators()
        {
            if (statusTextIndicators == null)
                statusTextIndicators = new List<TMP_Text>();

            statusTextIndicators.RemoveAll(t => t == null);

            var allTmp = FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < allTmp.Length; i++)
            {
                var tmp = allTmp[i];
                if (tmp != null && tmp.gameObject.name.Contains("CraneHookConnectionStatus_TMP"))
                {
                    if (!statusTextIndicators.Contains(tmp))
                        statusTextIndicators.Add(tmp);

                    tmp.enableWordWrapping = false;
                    tmp.overflowMode = TextOverflowModes.Overflow;
                    tmp.alignment = TextAlignmentOptions.Center;

                    Transform canvasTf = tmp.transform.parent != null && tmp.transform.parent.parent != null
                        ? tmp.transform.parent.parent
                        : tmp.transform.parent;
                    if (canvasTf != null)
                    {
                        var canvasRect = canvasTf.GetComponent<RectTransform>();
                        var panelRect = tmp.transform.parent != null ? tmp.transform.parent.GetComponent<RectTransform>() : null;
                        var tmpRect = tmp.GetComponent<RectTransform>();

                        if (canvasRect != null)
                        {
                            if (canvasTf.name.Contains("CraneHookStatusCanvas"))
                            {
                                Vector2 sz = new Vector2(540f, 70f);
                                canvasRect.sizeDelta = sz;
                                if (panelRect != null && panelRect != canvasRect) panelRect.sizeDelta = sz;
                                if (tmpRect != null) tmpRect.sizeDelta = new Vector2(520f, 64f);
                                canvasTf.localPosition = new Vector3(0f, 0.255f, 0.035f);
                                canvasTf.localEulerAngles = new Vector3(-20f, 180f, 0f);
                                canvasTf.localScale = new Vector3(0.00095f, 0.00095f, 0.00095f);
                                tmp.fontSize = 23f;
                            }
                            else if (canvasTf.name.Contains("HopperBailStatusCanvas"))
                            {
                                Vector2 sz = new Vector2(640f, 88f);
                                canvasRect.sizeDelta = sz;
                                if (panelRect != null && panelRect != canvasRect) panelRect.sizeDelta = sz;
                                if (tmpRect != null) tmpRect.sizeDelta = new Vector2(620f, 80f);
                                canvasTf.position = CrossbarWorldPosition + new Vector3(0f, 0.68f, 0.16f);
                                canvasTf.rotation = Quaternion.LookRotation(Vector3.back, Vector3.up);
                                canvasTf.localScale = new Vector3(0.0020f, 0.0020f, 0.0020f);
                                tmp.fontSize = 28f;
                            }
                        }
                    }
                }
            }
        }

        public void GetOperatorStatusDisplay(out string label, out Color color)
        {
            if (isCapturing || connectionState == HopperConnectionState.Capturing)
            {
                label = "ENGAGING — HOLD POSITION";
                color = new Color(1.0f, 0.90f, 0.20f, 1.0f);
                return;
            }

            if (!isAttached || connectionState == HopperConnectionState.Disconnected)
            {
                if (hasCompletedGroundRelease)
                {
                    label = "DISCONNECTED";
                    color = new Color(1.0f, 0.38f, 0.32f, 1.0f);
                }
                else
                {
                    label = "DISCONNECTED — ALIGN HOOK";
                    color = new Color(1.0f, 0.65f, 0.20f, 1.0f);
                }
                return;
            }

            if (connectionState == HopperConnectionState.ContactValidated ||
                connectionState == HopperConnectionState.Connected)
            {
                label = "ENGAGING — HOLD POSITION";
                color = new Color(1.0f, 0.90f, 0.20f, 1.0f);
                return;
            }

            if (connectionState == HopperConnectionState.LiftAuthorized && !hasLeftGroundThisCycle)
            {
                label = "CONNECTED — READY TO LIFT";
                color = new Color(0.15f, 1.0f, 0.45f, 1.0f);
                return;
            }

            if (connectionState == HopperConnectionState.Grounded)
            {
                label = "LOWER TO RELEASE";
                color = new Color(1.0f, 0.75f, 0.18f, 1.0f);
                return;
            }

            bool activelyHoistingUp = (Time.time - lastHoistUpTimestamp) <= 0.25f;
            if (activelyHoistingUp)
            {
                label = "LIFTING";
                color = new Color(0.20f, 0.95f, 1.0f, 1.0f);
            }
            else
            {
                label = "LOWER TO RELEASE";
                color = new Color(1.0f, 0.75f, 0.18f, 1.0f);
            }
        }

        public void UpdateStatusUI()
        {
            GetOperatorStatusDisplay(out string label, out Color color);

            if (statusTextIndicators == null) return;
            for (int i = 0; i < statusTextIndicators.Count; i++)
            {
                var tmp = statusTextIndicators[i];
                if (tmp == null) continue;
                if (tmp.text != label)
                    tmp.text = label;
                tmp.color = color;

                Transform canvasTf = tmp.transform.parent != null && tmp.transform.parent.parent != null
                    ? tmp.transform.parent.parent
                    : tmp.transform.parent;
                if (canvasTf != null && canvasTf.name.Contains("HopperBailStatusCanvas"))
                {
                    canvasTf.position = CrossbarWorldPosition + new Vector3(0f, 0.68f, 0.16f);
                    Camera cam = Camera.main;
                    if (cam != null)
                    {
                        Vector3 awayFromCam = canvasTf.position - cam.transform.position;
                        awayFromCam.y = 0f;
                        if (awayFromCam.sqrMagnitude > 0.01f)
                        {
                            canvasTf.rotation = Quaternion.LookRotation(awayFromCam.normalized, Vector3.up);
                        }
                    }
                }
            }
        }

        public float GetClearanceAboveGround(LayerMask groundMask)
        {
            InitCasters();
            if (casterWheels == null || casterWheels.Count == 0)
            {
                return (hopperRoot != null ? hopperRoot.position.y : transform.position.y) - minGroundY;
            }

            float minClearance = float.MaxValue;

            for (int i = 0; i < casterWheels.Count; i++)
            {
                var info = casterWheels[i];
                if (info.wheelTransform == null) continue;

                float wheelBottomY = (info.renderer != null)
                    ? info.renderer.bounds.min.y
                    : (info.wheelTransform.position.y - info.wheelRadius);

                Vector3 wheelPos = info.wheelTransform.position;
                Vector3 rayOrigin = new Vector3(wheelPos.x, wheelPos.y + 1.0f, wheelPos.z);
                RaycastHit[] hits = Physics.RaycastAll(rayOrigin, Vector3.down, 20.0f, groundMask, QueryTriggerInteraction.Ignore);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

                float groundY = minGroundY;
                for (int h = 0; h < hits.Length; h++)
                {
                    var hitCol = hits[h].collider;
                    if (hitCol != null && !hitCol.transform.IsChildOf(hopperRoot))
                    {
                        groundY = hits[h].point.y;
                        break;
                    }
                }

                float clearance = wheelBottomY - groundY;
                if (clearance < minClearance)
                {
                    minClearance = clearance;
                }
            }

            return minClearance;
        }

        public bool TryGetMinAllowedHookHeight(float currentHookHeight, float craneHeight, float hookOffset, out float minHookHeight)
        {
            minHookHeight = 0f;
            float travelSpan = (craneHeight + hookOffset) - 0.5f;
            if (travelSpan <= 0.01f)
                travelSpan = 4.6f;

            // Block manual downward hoist while controlled proximity capture is guiding the hook
            if (isCapturing)
            {
                minHookHeight = currentHookHeight;
                return true;
            }

            Transform hookTf = currentHookMount != null ? currentHookMount : cachedHookMount;

            if (isAttached)
            {
                if (!following)
                {
                    minHookHeight = currentHookHeight;
                    return true;
                }

                float clearance = GetClearanceAboveGround(~0);
                if (clearance <= 0.001f)
                {
                    minHookHeight = currentHookHeight;
                    return true;
                }

                float deltaT = clearance / travelSpan;
                minHookHeight = Mathf.Max(0f, currentHookHeight - deltaT);
                return true;
            }

            if (hookTf != null)
            {
                Vector3 saddle = SaddleWorld(hookTf);
                Vector3 bail = CrossbarWorldPosition;
                float dx = Mathf.Abs(saddle.x - bail.x);
                float dz = Mathf.Abs(saddle.z - bail.z);
                float dy = saddle.y - bail.y;

                if (dx <= 0.055f && dz <= 0.025f && dy <= 0.080f && dy >= -0.040f)
                {
                    float saddleAboveBail = Mathf.Max(0f, dy);
                    minHookHeight = Mathf.Max(0f, currentHookHeight - (saddleAboveBail / travelSpan));
                    return true;
                }
            }

            return false;
        }

        public void SetHookInRange(bool inRange, Transform hookMount = null, Rigidbody hookRb = null)
        {
            isHookInRange = inRange;
            if (inRange)
            {
                currentHookMount = hookMount;
                currentHookRb = hookRb;
                if (cachedHookMount != hookMount)
                {
                    ConfigureHookCShapeColliders(hookMount);
                }
            }
            else if (!isAttached && !isCapturing)
            {
                currentHookMount = null;
                currentHookRb = null;
            }
        }

        public bool ValidatePhysicalHookToBailContact(Transform hookMount, out float horizDist, out float vertOffset, out float surfaceGap, out string failureReason)
        {
            horizDist = float.MaxValue;
            vertOffset = float.MaxValue;
            surfaceGap = float.MaxValue;
            failureReason = string.Empty;

            if (hookMount == null)
            {
                failureReason = "Hook mount is null.";
                return false;
            }

            if (topBendCollider == null)
                ConfigureTopBendCollider();
            if (cachedHookMesh == null || cachedHookMount != hookMount)
                ConfigureHookCShapeColliders(hookMount);

            Vector3 hookSaddleWorld = SaddleWorld(hookMount);
            Vector3 bailWorld = CrossbarWorldPosition;

            Vector3 offset = hookSaddleWorld - bailWorld;
            horizDist = new Vector2(offset.x, offset.z).magnitude;
            vertOffset = offset.y;
            float totalDist = offset.magnitude;

            Transform hookMesh = cachedHookMesh != null ? cachedHookMesh : hookMount;
            float axisAlignment = Mathf.Abs(Vector3.Dot(hookMesh.forward.normalized, BailHorizontalBarAxisWorld));
            if (axisAlignment < 0.819f)
            {
                failureReason = $"C-hook channel axis is misaligned with bail bar (alignment={axisAlignment:F2}, need >= 0.82).";
                return false;
            }

            // Pocket saddle center for 24mm bar resting on -0.31042m inner curve is local (0.000, -0.2984, 0.000)
            Vector3 bailInHook = hookMesh.InverseTransformPoint(bailWorld);
            if (bailInHook.x < -0.022f || bailInHook.x > 0.024f ||
                bailInHook.y < -0.314f || bailInHook.y > -0.282f ||
                Mathf.Abs(bailInHook.z) > 0.052f)
            {
                failureReason = $"Bail bar not seated inside C-hook pocket: bailInHook=({bailInHook.x:F3}, {bailInHook.y:F3}, {bailInHook.z:F3})m (target (0.000, -0.298, 0.000)m).";
                return false;
            }

            if (horizDist > maxHorizontalSeatingDistance ||
                vertOffset < verticalSeatingRange.x || vertOffset > verticalSeatingRange.y ||
                totalDist > maxTotalSeatingDistance)
            {
                failureReason = $"Hook saddle outside seating window: horiz={horizDist:F3}m, vert={vertOffset:F3}m, total={totalDist:F3}m.";
                return false;
            }

            var hCols = hookMount.GetComponentsInChildren<Collider>(true);
            bool hasTopBendContact = false;
            float minGap = float.MaxValue;

            if (topBendCollider != null && hCols != null)
            {
                for (int i = 0; i < hCols.Length; i++)
                {
                    Collider hc = hCols[i];
                    if (hc == null || !hc.enabled || hc.isTrigger) continue;

                    if (Physics.ComputePenetration(
                        hc, hc.transform.position, hc.transform.rotation,
                        topBendCollider, topBendCollider.transform.position, topBendCollider.transform.rotation,
                        out _, out _))
                    {
                        minGap = 0f;
                        hasTopBendContact = true;
                        break;
                    }

                    Vector3 ptOnBail = topBendCollider.ClosestPoint(hookSaddleWorld);
                    Vector3 ptOnHook = hc.ClosestPoint(ptOnBail);
                    Vector3 ptOnBail2 = topBendCollider.ClosestPoint(ptOnHook);
                    float gap = Vector3.Distance(ptOnHook, ptOnBail2);
                    if (gap < minGap)
                        minGap = gap;

                    if (gap <= maxPhysicalSurfaceContactGap)
                    {
                        hasTopBendContact = true;
                    }
                }
            }

            surfaceGap = minGap;
            if (!hasTopBendContact)
            {
                failureReason = $"No physical contact with top bend collider (surface gap={surfaceGap:F3}m > {maxPhysicalSurfaceContactGap:F3}m).";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Initiates smooth controlled proximity-assisted capture of the C-hook onto the bail when within <= 0.50 m.
        /// Moves and aligns the hook/crane smoothly to the stationary hopper bail (never snaps the hopper to the hook).
        /// </summary>
        private bool BeginProximityCapture(Transform hookMount, Rigidbody hookRb, float measuredDistance)
        {
            if (isAttached || isCapturing || hookMount == null)
                return false;

            if (cachedHookMesh == null || cachedHookMount != hookMount)
                ConfigureHookCShapeColliders(hookMount);

            // Do not capture if another payload (e.g., Jaw Tongs) is currently attached to the crane hook
            if (cachedAttachCargo != null && cachedAttachCargo.tongsLiftingTarget != null && cachedAttachCargo.tongsLiftingTarget.IsAttached)
                return false;

            if (hopperRoot == null)
                hopperRoot = transform.root;
            if (hopperRb == null)
                hopperRb = hopperRoot.GetComponent<Rigidbody>();

            lastMeasuredCaptureTriggerDistance = measuredDistance;
            currentHookMount = hookMount;
            currentHookRb = hookRb != null ? hookRb : hookMount.GetComponent<Rigidbody>();
            originalParent = hopperRoot.parent;

            // Stop active crane directional buttons so approach input does not push the hook through or past the bail
            StopActiveCraneRemoteButtonsOnCapture();

            // Ignore hook-vs-hopper collisions during capture and attachment so the hook glides cleanly into the saddle
            SetHookHopperCollisionsIgnored(hookMount, true);

            _captureStartHookMountPos = hookMount.position;
            _captureStartHookMountRot = hookMount.rotation;

            ComputeTargetHookMountWorldPose(hookMount, out Vector3 targetMountPos, out _);
            if (cachedCrane != null)
            {
                _captureStartBeamPos = cachedCrane.initialBeamPos;
                _captureStartTelpherPos = cachedCrane.initialTelpherPos;
                _captureStartHookHeight = cachedCrane.hookHeight;
                _hasCraneCaptureTargets = SolvePlumbCraneCoordinatesForMountPos(
                    targetMountPos,
                    out _captureTargetBeamPos,
                    out _captureTargetTelpherPos,
                    out _captureTargetHookHeight);
            }
            else
            {
                _hasCraneCaptureTargets = false;
            }

            isCapturing = true;
            isAttached = false;
            captureTimer = 0f;
            waitTimer = 0f;
            groundedTimer = 0f;
            hasLeftGroundThisCycle = false;
            maxClearanceReachedThisCycle = 0f;
            hasCompletedGroundRelease = false;
            following = false;
            connectionState = HopperConnectionState.Capturing;
            liftState = HopperLiftState.ConnectedWaiting;

            UpdateStatusUI();
            Debug.Log($"[HopperCraneLiftingPoint] Controlled proximity capture initiated at distance={measuredDistance:F3}m (<= {autoConnectRadius:F2}m). Guiding C-hook smoothly to bail.");
            return true;
        }

        private void StepControlledCapture(float dt)
        {
            if (!isCapturing || currentHookMount == null)
                return;

            ComputeTargetHookMountWorldPose(currentHookMount, out Vector3 targetMountPos, out Quaternion targetMountRot);

            float duration = Mathf.Max(0.02f, captureGlideDuration);
            captureTimer += dt;
            float u = Mathf.Clamp01(captureTimer / duration);
            float smooth = Mathf.SmoothStep(0f, 1f, u);

            if (_hasCraneCaptureTargets && cachedCrane != null)
            {
                cachedCrane.initialBeamPos = Mathf.Lerp(_captureStartBeamPos, _captureTargetBeamPos, smooth);
                cachedCrane.initialTelpherPos = Mathf.Lerp(_captureStartTelpherPos, _captureTargetTelpherPos, smooth);
                cachedCrane.hookHeight = Mathf.Lerp(_captureStartHookHeight, _captureTargetHookHeight, smooth);
                cachedCrane.UpdateMainBeamPos(cachedCrane.initialBeamPos);
                cachedCrane.UpdateTelpherPos(cachedCrane.initialTelpherPos);
                cachedCrane.UpdateHookHeight();
            }

            Vector3 interpPos = Vector3.Lerp(_captureStartHookMountPos, targetMountPos, smooth);
            Quaternion interpRot = Quaternion.Slerp(_captureStartHookMountRot, targetMountRot, smooth);
            ApplyHookMountPoseClean(currentHookMount, currentHookRb, interpPos, interpRot);
            Physics.SyncTransforms();

            if (u >= 1f)
            {
                CompleteCapturedAttachment(targetMountPos, targetMountRot);
            }
        }

        private void CompleteCapturedAttachment(Vector3 exactMountPos, Quaternion exactMountRot)
        {
            if (_hasCraneCaptureTargets && cachedCrane != null)
            {
                cachedCrane.initialBeamPos = _captureTargetBeamPos;
                cachedCrane.initialTelpherPos = _captureTargetTelpherPos;
                cachedCrane.hookHeight = _captureTargetHookHeight;
                cachedCrane.UpdateMainBeamPos(_captureTargetBeamPos);
                cachedCrane.UpdateTelpherPos(_captureTargetTelpherPos);
                cachedCrane.UpdateHookHeight();
            }

            ApplyHookMountPoseClean(currentHookMount, currentHookRb, exactMountPos, exactMountRot);
            Physics.SyncTransforms();

            isCapturing = false;
            connectionState = HopperConnectionState.ContactValidated;
            connectionState = HopperConnectionState.Connected;
            liftState = HopperLiftState.ConnectedWaiting;
            waitTimer = 0f;
            groundedTimer = 0f;
            hasLeftGroundThisCycle = false;
            maxClearanceReachedThisCycle = 0f;
            hasCompletedGroundRelease = false;
            following = false;
            lastHookSaddleY = SaddleWorld(currentHookMount).y;

            if (hopperRb != null)
            {
                hopperRb.isKinematic = true;
                hopperRb.useGravity = false;
                hopperRb.mass = fixedMassKg;
                hopperRb.linearVelocity = Vector3.zero;
            }

            isAttached = true;
            totalCompletedCaptures++;

            if (cachedAttachCargo != null)
            {
                cachedAttachCargo.isAttached = true;
            }

            UpdateStatusUI();
            Debug.Log($"[HopperCraneLiftingPoint] C-hook seated around bail (SaddleWorld={SaddleWorld(currentHookMount):F4} == BailCenter={CrossbarWorldPosition:F4}). Waiting {liftAuthorizationDelay:F2}s for LiftAuthorized.");
        }

        /// <summary>
        /// Attaches the hopper assembly to the crane hook (supports both direct seated attachment and <= 0.50m proximity capture).
        /// </summary>
        public bool AttachToHook(Transform hookMount, Rigidbody hookRb)
        {
            if (isAttached || isCapturing || hookMount == null || hookRb == null)
                return false;

            if (hopperRoot == null)
                hopperRoot = transform.root;
            if (hopperRb == null)
                hopperRb = hopperRoot.GetComponent<Rigidbody>();

            if (cachedHookMesh == null || cachedHookMount != hookMount)
                ConfigureHookCShapeColliders(hookMount);

            float dist = Vector3.Distance(SaddleWorld(hookMount), CrossbarWorldPosition);

            // If already tightly seated inside the C-pocket, snap cleanly to exact saddle alignment and connect immediately
            if (ValidatePhysicalHookToBailContact(hookMount, out float horizDist, out float vertOffset, out float surfaceGap, out string failReason))
            {
                currentHookMount = hookMount;
                currentHookRb = hookRb;
                originalParent = hopperRoot.parent;
                lastMeasuredCaptureTriggerDistance = dist;

                SetHookHopperCollisionsIgnored(hookMount, true);
                ComputeTargetHookMountWorldPose(hookMount, out Vector3 exactMountPos, out Quaternion exactMountRot);
                if (cachedCrane != null && SolvePlumbCraneCoordinatesForMountPos(exactMountPos, out float b, out float tp, out float hh))
                {
                    _captureTargetBeamPos = b;
                    _captureTargetTelpherPos = tp;
                    _captureTargetHookHeight = hh;
                    _hasCraneCaptureTargets = true;
                }
                CompleteCapturedAttachment(exactMountPos, exactMountRot);
                return true;
            }

            // Otherwise, if within the 0.50 m capture distance, initiate smooth controlled proximity capture
            if (dist <= autoConnectRadius)
            {
                BeginProximityCapture(hookMount, hookRb, dist);
                return false; // isAttached becomes true once the 0.35s controlled capture glide finishes
            }

            Debug.LogWarning($"[HopperCraneLiftingPoint] Attachment rejected (dist={dist:F3}m > {autoConnectRadius:F2}m) - {failReason}");
            return false;
        }

        /// <summary>
        /// Detaches the hopper assembly from the crane hook, revoking lift authorization and leaving the hopper supported on the ground.
        /// </summary>
        public void DetachFromHook()
        {
            if (!isAttached && !isCapturing && connectionState == HopperConnectionState.Disconnected)
                return;

            bool releasedAfterLift = hasLeftGroundThisCycle;

            isCapturing = false;
            connectionState = HopperConnectionState.Disconnected;
            liftState = HopperLiftState.Disconnected;
            captureTimer = 0f;
            waitTimer = 0f;
            groundedTimer = 0f;
            hasLeftGroundThisCycle = false;
            maxClearanceReachedThisCycle = 0f;
            hasCompletedGroundRelease = releasedAfterLift;

            // Prevent immediate re-capture or state oscillation: require moving > autoConnectRearmDistance (0.62 m) away first
            lastDetachedHook = currentHookMount;
            autoConnectArmed = false;
            following = false;

            if (hopperRoot != null)
            {
                if (hopperRoot.parent != originalParent)
                    hopperRoot.SetParent(originalParent, true);

                float clearance = GetClearanceAboveGround(~0);
                if (Mathf.Abs(clearance) <= 0.03f)
                {
                    Vector3 pos = hopperRoot.position;
                    pos.y -= clearance;
                    hopperRoot.position = pos;
                }
            }

            if (hopperRb != null)
            {
                hopperRb.isKinematic = true;
                hopperRb.useGravity = false;
                hopperRb.mass = fixedMassKg;
                hopperRb.linearVelocity = Vector3.zero;
            }

            isAttached = false;
            currentHookMount = null;
            currentHookRb = null;

            if (lastDetachedHook != null)
            {
                var cargo = lastDetachedHook.GetComponent<NL_AttachCargo>();
                if (cargo != null && (cargo.tongsLiftingTarget == null || !cargo.tongsLiftingTarget.IsAttached))
                {
                    cargo.isAttached = false;
                }
            }

            UpdateStatusUI();
            Debug.Log($"[HopperCraneLiftingPoint] State -> Disconnected (releasedAfterLift={releasedAfterLift}) at {(hopperRoot != null ? hopperRoot.position : transform.position):F3}.");
        }

        private void FixedUpdate()
        {
            if (!Application.isPlaying)
                return;

            if (isCapturing && currentHookMount != null)
            {
                ComputeTargetHookMountWorldPose(currentHookMount, out Vector3 targetPos, out Quaternion targetRot);
                float u = Mathf.Clamp01(captureTimer / Mathf.Max(0.02f, captureGlideDuration));
                float smooth = Mathf.SmoothStep(0f, 1f, u);
                ApplyHookMountPoseClean(
                    currentHookMount,
                    currentHookRb,
                    Vector3.Lerp(_captureStartHookMountPos, targetPos, smooth),
                    Quaternion.Slerp(_captureStartHookMountRot, targetRot, smooth));
            }
            else if (isAttached && currentHookMount != null)
            {
                ComputeTargetHookMountWorldPose(currentHookMount, out Vector3 exactSeatedMountPos, out Quaternion exactSeatedMountRot);
                if (!following)
                {
                    // During the 0.75s confirmation delay on the ground, keep the hook locked plumb in the bail saddle
                    ApplyHookMountPoseClean(currentHookMount, currentHookRb, exactSeatedMountPos, exactSeatedMountRot);
                }
                else
                {
                    // While lifting/transporting, stabilize hook orientation upright so C-hook channel never twists off the bail bar
                    currentHookMount.rotation = exactSeatedMountRot;
                    if (currentHookRb != null)
                    {
                        currentHookRb.rotation = exactSeatedMountRot;
                        currentHookRb.angularVelocity = Vector3.zero;
                    }
                    if (cachedNlHook != null && cachedNlHook.inertiaBody != null)
                    {
                        cachedNlHook.inertiaBody.rotation = exactSeatedMountRot;
                        cachedNlHook.inertiaBody.angularVelocity = Vector3.zero;
                    }
                }
            }
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying)
                return;

            if (!following || !isAttached || !IsLiftAuthorized)
                return;
            if (currentHookMount == null)
            {
                DetachFromHook();
                return;
            }

            ComputeTargetHookMountWorldPose(currentHookMount, out _, out Quaternion exactSeatedMountRot);
            currentHookMount.rotation = exactSeatedMountRot;

            Vector3 targetPos = SaddleWorld(currentHookMount) + followOffset;

            float currentClearance = GetClearanceAboveGround(~0);
            float deltaY = targetPos.y - hopperRoot.position.y;
            if (currentClearance + deltaY < 0f)
            {
                targetPos.y = hopperRoot.position.y - currentClearance;
            }

            hopperRoot.SetPositionAndRotation(targetPos, followRotation);
        }

        private void Update()
        {
            if (!Application.isPlaying)
            {
                UpdateStatusUI();
                return;
            }

            if (cachedAttachCargo == null)
            {
                cachedAttachCargo = FindAnyObjectByType<NL_AttachCargo>();
                if (cachedAttachCargo != null && cachedHookMount == null)
                {
                    ConfigureHookCShapeColliders(cachedAttachCargo.transform);
                }
            }

            // 1. Handle controlled proximity-assisted capture glide if active
            if (isCapturing)
            {
                if (currentHookMount == null)
                {
                    DetachFromHook();
                    return;
                }
                StepControlledCapture(Time.deltaTime);
                UpdateStatusUI();
                return;
            }

            // 2. Manage post-disconnect re-arm hysteresis (> 0.62 m) and clear temporary collision ignore once hook clears bail (> 0.20 m)
            if (!isAttached)
            {
                Transform liveHook = lastDetachedHook != null
                    ? lastDetachedHook
                    : (cachedAttachCargo != null ? cachedAttachCargo.transform : cachedHookMount);

                if (liveHook != null)
                {
                    float distFromBail = Vector3.Distance(SaddleWorld(liveHook), CrossbarWorldPosition);

                    if (_collisionsIgnoredWithHook && distFromBail > 0.20f)
                    {
                        SetHookHopperCollisionsIgnored(liveHook, false);
                    }

                    if (!autoConnectArmed && distFromBail > autoConnectRearmDistance)
                    {
                        autoConnectArmed = true;
                        hasCompletedGroundRelease = false;
                    }
                    else if (autoConnectArmed && hasCompletedGroundRelease && distFromBail > autoConnectRearmDistance)
                    {
                        hasCompletedGroundRelease = false;
                    }

                    // Monitor 0.50m proximity capture directly every frame when armed and available
                    if (autoConnect && autoConnectArmed && distFromBail <= autoConnectRadius)
                    {
                        Rigidbody rb = cachedAttachCargo != null && cachedAttachCargo.rbCurrent != null
                            ? cachedAttachCargo.rbCurrent
                            : liveHook.GetComponent<Rigidbody>();
                        TryAutoConnect(liveHook, rb);
                    }
                }

                UpdateStatusUI();
                return;
            }

            if (currentHookMount == null)
            {
                DetachFromHook();
                return;
            }

            float currentSaddleY = SaddleWorld(currentHookMount).y;
            if (currentSaddleY > lastHookSaddleY + 0.0002f)
            {
                lastHoistUpTimestamp = Time.time;
            }
            lastHookSaddleY = currentSaddleY;

            // 3. State: Connected (ENGAGING — HOLD POSITION, waiting 0.75s for LiftAuthorized)
            if (connectionState == HopperConnectionState.Connected)
            {
                // Keep hook seated plumb in the C-hook saddle throughout the 0.75s confirmation delay
                ComputeTargetHookMountWorldPose(currentHookMount, out Vector3 exactSeatedMountPos, out Quaternion exactSeatedMountRot);
                if (_hasCraneCaptureTargets && cachedCrane != null)
                {
                    cachedCrane.initialBeamPos = _captureTargetBeamPos;
                    cachedCrane.initialTelpherPos = _captureTargetTelpherPos;
                    cachedCrane.hookHeight = _captureTargetHookHeight;
                    cachedCrane.UpdateMainBeamPos(_captureTargetBeamPos);
                    cachedCrane.UpdateTelpherPos(_captureTargetTelpherPos);
                    cachedCrane.UpdateHookHeight();
                }
                ApplyHookMountPoseClean(currentHookMount, currentHookRb, exactSeatedMountPos, exactSeatedMountRot);

                waitTimer += Time.deltaTime;
                if (waitTimer >= liftAuthorizationDelay)
                {
                    SetHookHopperCollisionsIgnored(currentHookMount, true);
                    followOffset = hopperRoot.position - CrossbarWorldPosition;
                    followRotation = hopperRoot.rotation;
                    following = true;
                    hopperRb.isKinematic = true;
                    hopperRb.useGravity = false;
                    connectionState = HopperConnectionState.LiftAuthorized;
                    liftState = HopperLiftState.ConnectedReadyToLift;
                    Debug.Log($"[HopperCraneLiftingPoint] State -> LiftAuthorized / CONNECTED — READY TO LIFT after {waitTimer:F2}s.");
                }

                UpdateStatusUI();
                return;
            }

            // 4. States: LiftAuthorized, Lifting, Grounded
            if (IsLiftAuthorized)
            {
                if (following && currentHookMount != null)
                {
                    ComputeTargetHookMountWorldPose(currentHookMount, out _, out Quaternion exactSeatedMountRot);
                    currentHookMount.rotation = exactSeatedMountRot;

                    Vector3 targetPos = SaddleWorld(currentHookMount) + followOffset;
                    float preClearance = GetClearanceAboveGround(~0);
                    float dY = targetPos.y - hopperRoot.position.y;
                    if (preClearance + dY < 0f)
                        targetPos.y = hopperRoot.position.y - preClearance;
                    hopperRoot.SetPositionAndRotation(targetPos, followRotation);
                }

                float clearance = GetClearanceAboveGround(~0);
                if (clearance > maxClearanceReachedThisCycle)
                    maxClearanceReachedThisCycle = clearance;

                if (!hasLeftGroundThisCycle)
                {
                    if (clearance >= liftedClearanceThreshold)
                    {
                        hasLeftGroundThisCycle = true;
                        connectionState = HopperConnectionState.Lifting;
                        groundedTimer = 0f;
                        lastHoistUpTimestamp = Time.time;
                        Debug.Log($"[HopperCraneLiftingPoint] State -> Lifting (clearance={clearance:F3}m >= {liftedClearanceThreshold:F3}m).");
                    }
                }
                else
                {
                    if (clearance <= groundedClearanceThreshold)
                    {
                        if (connectionState != HopperConnectionState.Grounded)
                        {
                            connectionState = HopperConnectionState.Grounded;
                            groundedTimer = 0f;
                            Debug.Log($"[HopperCraneLiftingPoint] State -> Grounded / LOWER TO RELEASE (clearance={clearance:F4}m). Starting {groundReleaseDelay:F2}s stable landing timer.");
                        }

                        groundedTimer += Time.deltaTime;
                        if (groundedTimer >= groundReleaseDelay)
                        {
                            Debug.Log($"[HopperCraneLiftingPoint] Automatic ground-contact release triggered after {groundedTimer:F2}s stable landing (maxLift={maxClearanceReachedThisCycle:F3}m).");
                            DetachFromHook();
                            return;
                        }
                    }
                    else
                    {
                        if (connectionState == HopperConnectionState.Grounded)
                        {
                            connectionState = HopperConnectionState.Lifting;
                            groundedTimer = 0f;
                            Debug.Log($"[HopperCraneLiftingPoint] Landing aborted (clearance={clearance:F3}m) -> State returned to Lifting.");
                        }
                    }
                }
            }

            UpdateStatusUI();
        }

        /// <summary>
        /// Automatic capture check within 0.50 m of the hopper lifting bail's top horizontal bar center.
        /// </summary>
        public bool TryAutoConnect(Transform hookMount, Rigidbody hookRb)
        {
            if (!autoConnect || isAttached || isCapturing || hookMount == null)
                return false;

            // Do not capture if another payload (e.g., Jaw Tongs) is currently attached
            if (cachedAttachCargo != null && cachedAttachCargo.tongsLiftingTarget != null && cachedAttachCargo.tongsLiftingTarget.IsAttached)
                return false;

            float dist = Vector3.Distance(SaddleWorld(hookMount), CrossbarWorldPosition);

            if (dist > autoConnectRadius)
            {
                if (!autoConnectArmed && dist > autoConnectRearmDistance)
                    autoConnectArmed = true;
                return false;
            }

            if (!autoConnectArmed)
                return false;

            if (hookRb != null && hookRb.linearVelocity.magnitude > autoConnectMaxHookSpeed)
                return false;

            BeginProximityCapture(hookMount, hookRb, dist);
            return isAttached;
        }

        public void ToggleAttachment(Transform hookMount, Rigidbody hookRb)
        {
            if (isAttached || isCapturing)
                DetachFromHook();
            else
                AttachToHook(hookMount, hookRb);
        }
    }
}
