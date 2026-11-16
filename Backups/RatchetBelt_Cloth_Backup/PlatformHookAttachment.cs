using System.Collections.Generic;
using UnityEngine;

namespace Sefpro.RatchetBelt
{
    /// <summary>
    /// Coordinates physical attachment, strict mathematical belt-length constraints,
    /// 3D exterior surface sliding, unattached resting retention, wrong-hook rejection,
    /// tension locking, and release of the ratchet belt's end fittings
    /// (Fixed J-Hook and Adjustable J-Hook) on the Vibrating Platform.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class PlatformHookAttachment : MonoBehaviour
    {
        private struct MoldObstacleSlice
        {
            public float minZ;
            public float maxZ;
            public float maxY;
        }

        [Header("Controller & Renderer References")]
        public RatchetBeltMechanicsController mechanicsController;
        public RatchetWebbingRibbonRenderer ribbonRenderer;

        [Header("Platform Hook Anchors")]
        public PlatformSideHook platformHookLeft;   // Far (-Z) anchor hook for Fixed J-Hook
        public PlatformSideHook platformHookRight;  // Near (+Z) anchor hook for Adjustable J-Hook

        [Header("Mold & Platform Collision References")]
        [Tooltip("Active MoldCast transform on the Vibrating Platform used for exterior strap/hook collision.")]
        public Transform activeMoldTransform;

        [Tooltip("Vibrating_Rubber_Surface transform used for platform deck elevation & footprint.")]
        public Transform platformDeckTransform;

        [Header("Strict Belt-Length Constraint (Calculated Per Belt)")]
        [Tooltip("Small, controlled extra strap allowance (meters) above minimum wrapped securing length for handling, hooking, and 4-stroke ratcheting.")]
        [Range(0.040f, 0.160f)]
        public float handlingSlackAllowanceMeters = 0.090f;

        [Tooltip("Calculated fixed-side length from Far hook eyelet through Fixed Webbing & Buckle to Mandrel departure tangent (meters).")]
        [SerializeField] private float fixedAssemblyLengthMeters = 0.309f;

        [Tooltip("Calculated minimum adjustable webbing routing length from Mandrel over MoldCast to Near hook eyelet (meters).")]
        [SerializeField] private float minAdjustableStrapLengthMeters = 1.930f;

        [Tooltip("Configured maximum adjustable webbing routing length = minAdjustable + handlingSlackAllowance (meters).")]
        [SerializeField] private float maxAdjustableStrapLengthMeters = 2.020f;

        [Tooltip("Calculated complete minimum belt length (Fixed + Buckle + Wrapped Adjustable) to secure around MoldCast (meters).")]
        [SerializeField] private float minTotalBeltLengthMeters = 2.239f;

        [Tooltip("Configured complete maximum belt length = minTotal + handlingSlackAllowance (meters).")]
        [SerializeField] private float maxTotalBeltLengthMeters = 2.329f;

        [Header("Attachment States (Read Only)")]
        [SerializeField] private bool isFixedHookAttached = true;
        [SerializeField] private PlatformSideHook fixedAttachedTo;
        [SerializeField] private Vector3 fixedHookRestPos;
        [SerializeField] private Quaternion fixedHookRestRot = Quaternion.Euler(0f, 0f, 90f);

        [SerializeField] private bool isAdjHookAttached = false;
        [SerializeField] private PlatformSideHook adjAttachedTo;
        [SerializeField] private Vector3 adjHookRestPos;
        [SerializeField] private Quaternion adjHookRestRot = Quaternion.Euler(0f, 180f, 90f);

        [Header("Free-End Physical Tracking")]
        [Tooltip("Deliberate alignment radius around the target side hook anchor (meters).")]
        public float attachmentProximityThreshold = 0.13f;

        [Tooltip("Legacy readout mirrored to maxAdjustableStrapLengthMeters.")]
        public float maxStrapPayoutMeters = 2.020f;

        [SerializeField] private bool isFreeEndGrabbed = false;
        [SerializeField] private bool isSettlingToSurface = false;
        [SerializeField] private Vector3 unattachedRestingWorldPos;
        [SerializeField] private Quaternion unattachedRestingWorldRot = Quaternion.Euler(0f, 180f, 90f);
        [SerializeField] private bool hasWrappedOverMold = false;

        private PlatformSideHook[] _allSceneHooks;
        private PlatformSideHook _highlightedWrongHook;
        private readonly List<MoldObstacleSlice> _moldSlices = new List<MoldObstacleSlice>(32);
        private readonly List<Vector2> _hullInput = new List<Vector2>(64);
        private readonly List<Vector2> _upperHull = new List<Vector2>(32);
        private bool _lengthInitialized;

        public bool IsFixedHookAttached => isFixedHookAttached;
        public bool IsAdjHookAttached => isAdjHookAttached;
        public bool IsFreeEndGrabbed => isFreeEndGrabbed;
        public bool HasWrappedOverMold => hasWrappedOverMold;
        public PlatformSideHook FixedAttachedTo => fixedAttachedTo;
        public PlatformSideHook AdjAttachedTo => adjAttachedTo;
        public Vector3 UnattachedRestingWorldPos => unattachedRestingWorldPos;

        public float FixedAssemblyLengthMeters => fixedAssemblyLengthMeters;
        public float MinAdjustableStrapLengthMeters => minAdjustableStrapLengthMeters;
        public float MaxAdjustableStrapLengthMeters => maxAdjustableStrapLengthMeters;
        public float MinTotalBeltLengthMeters => minTotalBeltLengthMeters;
        public float MaxTotalBeltLengthMeters => maxTotalBeltLengthMeters;

        /// <summary>
        /// Portion of handlingSlackAllowanceMeters removed manually via PullSlack(), leaving 4 * shorteningPerTooth (40 mm) for ratchet strokes.
        /// </summary>
        public float ManualSlackAllowanceMeters
        {
            get
            {
                float ratchetTakeup = mechanicsController != null
                    ? RatchetBeltMechanicsController.MAX_TIGHTEN_STROKES * mechanicsController.shorteningPerTooth
                    : 0.040f;
                return Mathf.Max(0.010f, handlingSlackAllowanceMeters - ratchetTakeup);
            }
        }

        /// <summary>
        /// Exact physical length of the unspooled adjustable webbing at the current instant.
        /// Strictly bounded within [minAdjustableStrapLengthMeters, maxAdjustableStrapLengthMeters].
        /// Never increases when grabbed!
        /// </summary>
        public float CurrentActiveAdjustableStrapLengthMeters
        {
            get
            {
                if (!_lengthInitialized)
                    RecalculateBeltLengthConstraints();

                if (mechanicsController == null || !isAdjHookAttached)
                {
                    // Unattached or being grabbed/wrapped: constant physical unspooled length = maxAdjustableStrapLengthMeters
                    return maxAdjustableStrapLengthMeters;
                }

                float manualSlack = Mathf.Clamp(mechanicsController.SlackMeters, 0f, ManualSlackAllowanceMeters);
                float manualPulled = ManualSlackAllowanceMeters - manualSlack;
                float ratchetShortened = Mathf.Clamp(mechanicsController.TotalShortening, 0f,
                    RatchetBeltMechanicsController.MAX_TIGHTEN_STROKES * mechanicsController.shorteningPerTooth);

                float activeLen = maxAdjustableStrapLengthMeters - manualPulled - ratchetShortened;
                return Mathf.Clamp(activeLen, minAdjustableStrapLengthMeters, maxAdjustableStrapLengthMeters);
            }
        }

        /// <summary>
        /// Total physical belt length (Fixed assembly + Active adjustable webbing), strictly within [MinTotalBeltLengthMeters, MaxTotalBeltLengthMeters].
        /// </summary>
        public float CurrentTotalBeltLengthMeters => fixedAssemblyLengthMeters + CurrentActiveAdjustableStrapLengthMeters;

        private void Awake()
        {
            handlingSlackAllowanceMeters = Mathf.Clamp(handlingSlackAllowanceMeters, 0.040f, 0.160f);
            attachmentProximityThreshold = Mathf.Clamp(attachmentProximityThreshold, 0.04f, 0.14f);

            if (mechanicsController == null)
                mechanicsController = GetComponent<RatchetBeltMechanicsController>();
            if (ribbonRenderer == null)
                ribbonRenderer = GetComponentInChildren<RatchetWebbingRibbonRenderer>(true);

            FindPlatformHooks();
            ResolveEnvironmentReferences();
            RecalculateBeltLengthConstraints();
        }

        private void Start()
        {
            FindPlatformHooks();
            ResolveEnvironmentReferences();
            RecalculateBeltLengthConstraints();
            CacheRestTransforms();
            if (Application.isPlaying)
            {
                SyncInitialAttachmentStates();
            }
        }

        public void ResolveEnvironmentReferences()
        {
            if (mechanicsController == null)
                mechanicsController = GetComponent<RatchetBeltMechanicsController>();
            if (ribbonRenderer == null)
                ribbonRenderer = GetComponentInChildren<RatchetWebbingRibbonRenderer>(true);

            if (activeMoldTransform == null)
            {
                GameObject mc = GameObject.Find("MoldCasting/MoldCast");
                if (mc == null) mc = GameObject.Find("MoldCast");
                if (mc != null) activeMoldTransform = mc.transform;
            }

            if (platformDeckTransform == null)
            {
                GameObject deck = GameObject.Find("VibratingPlatform (1)/Vibrating_Rubber_Surface");
                if (deck == null) deck = GameObject.Find("Vibrating_Rubber_Surface");
                if (deck != null) platformDeckTransform = deck.transform;
            }

            _allSceneHooks = FindObjectsByType<PlatformSideHook>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        }

        public void FindPlatformHooks()
        {
            int myBeltIdx = mechanicsController != null ? mechanicsController.beltIndex : (name.Contains("2") ? 2 : 1);
            float myX = transform.position.x;
            var allHooks = FindObjectsByType<PlatformSideHook>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            _allSceneHooks = allHooks;

            PlatformSideHook bestFar = null;
            PlatformSideHook bestNear = null;
            float bestFarDist = float.MaxValue;
            float bestNearDist = float.MaxValue;

            foreach (var h in allHooks)
            {
                if (h == null) continue;
                h.AutoInferBeltIndex();
                if (h.assignedBeltIndex != myBeltIdx) continue;

                float dX = Mathf.Abs(h.transform.position.x - myX);
                if (h.IsFarSide && dX < bestFarDist)
                {
                    bestFar = h;
                    bestFarDist = dX;
                }
                else if (h.IsNearSide && dX < bestNearDist)
                {
                    bestNear = h;
                    bestNearDist = dX;
                }
            }

            if (platformHookLeft == null || platformHookLeft.assignedBeltIndex != myBeltIdx)
                platformHookLeft = bestFar;
            if (platformHookRight == null || platformHookRight.assignedBeltIndex != myBeltIdx)
                platformHookRight = bestNear;
        }

        /// <summary>
        /// Calculates this belt's exact minimum wrapped securing length and maximum allowed length
        /// directly from actual MoldCast dimensions, belt X plane, buckle/mandrel geometry, and platform side hooks.
        /// </summary>
        public void RecalculateBeltLengthConstraints()
        {
            handlingSlackAllowanceMeters = Mathf.Clamp(handlingSlackAllowanceMeters, 0.040f, 0.160f);
            FindPlatformHooks();
            ResolveEnvironmentReferences();

            float beltX = GetBeltPlaneX();
            // Always evaluate fixed & wrapped mold securing length against the mold-clamping buckle posture
            // so floor-to-mold buckle pivoting never alters the belt's calibrated physical length!
            Vector3 clampedMandrelCenter = new Vector3(beltX, 0.3853f, -6.0221f);
            Vector3 clampedBuckleRear = new Vector3(beltX, 0.3418f, -6.0391f);

            // 1. Fixed assembly length: Far side hook eyelet -> Rear buckle crossbar -> Mandrel departure tangent
            Vector3 farEyelet = platformHookLeft != null
                ? platformHookLeft.AnchorWorldPosition + platformHookLeft.AnchorWorldRotation * new Vector3(-0.0020f, 0f, 0.0004f)
                : new Vector3(beltX, 0.1323f, -6.1436f);

            fixedAssemblyLengthMeters = Vector3.Distance(farEyelet, clampedBuckleRear) +
                                        Vector3.Distance(clampedBuckleRear, clampedMandrelCenter);

            // 2. Near securing hook eyelet world position when J_Hook_Adjustable is seated on Platform_SideHook_Near
            Vector3 nearAnchorPos = platformHookRight != null
                ? platformHookRight.AnchorWorldPosition
                : new Vector3(beltX, 0.1343f, -4.7760f);
            Quaternion nearAnchorRot = platformHookRight != null
                ? platformHookRight.AnchorWorldRotation
                : Quaternion.Euler(0f, 180f, 90f);
            Vector3 nearEyelet = nearAnchorPos + nearAnchorRot * GetAdjustableEyeletLocalOffset();
            nearEyelet.x = beltX;

            // 3. Collect exact MoldCast obstacle slices overlapping this belt's 50 mm width
            const float strapWidth = 0.050f;
            const float strapThickness = 0.0024f;
            const float surfaceClearance = strapThickness * 0.5f + 0.0028f; // 4.0 mm centerline clearance
            CollectMoldSlicesForBand(beltX - strapWidth * 0.5f - 0.003f, beltX + strapWidth * 0.5f + 0.003f, surfaceClearance);

            // 4. Compute exact filleted upper convex hull from tightened spool radius (14.4 mm) over MoldCast to nearEyelet
            Vector2 spoolZY = new Vector2(clampedMandrelCenter.z, clampedMandrelCenter.y);
            float tightenedSpoolRadius = RatchetBeltMechanicsController.MAX_SPOOL_RADIUS;

            _hullInput.Clear();
            for (int i = 0; i < _moldSlices.Count; i++)
            {
                MoldObstacleSlice s = _moldSlices[i];
                if (s.minZ >= spoolZY.x + 0.005f) _hullInput.Add(new Vector2(s.minZ, s.maxY));
                if (s.maxZ >= spoolZY.x + 0.005f) _hullInput.Add(new Vector2(s.maxZ, s.maxY));
            }

            Vector2 approxTarget = _hullInput.Count > 0 ? _hullInput[0] : new Vector2(nearEyelet.z, nearEyelet.y);
            for (int i = 1; i < _hullInput.Count; i++)
            {
                if (_hullInput[i].y > approxTarget.y) approxTarget = _hullInput[i];
            }

            Vector2 pStart2D = ComputeUpperSpoolTangent(spoolZY, tightenedSpoolRadius, approxTarget);
            Vector2 pEnd2D = new Vector2(nearEyelet.z, nearEyelet.y);
            ComputeUpperConvexHull(pStart2D, pEnd2D, _hullInput, _upperHull);
            if (_upperHull.Count >= 2)
            {
                pStart2D = ComputeUpperSpoolTangent(spoolZY, tightenedSpoolRadius, _upperHull[1]);
                _upperHull[0] = pStart2D;
            }

            List<Vector2> filleted = BuildFilletedHullProfile(_upperHull, 0.016f);
            float wrappedArc = 0f;
            for (int i = 1; i < filleted.Count; i++)
            {
                wrappedArc += Vector2.Distance(filleted[i], filleted[i - 1]);
            }

            minAdjustableStrapLengthMeters = Mathf.Max(0.50f, wrappedArc);
            maxAdjustableStrapLengthMeters = minAdjustableStrapLengthMeters + handlingSlackAllowanceMeters;
            minTotalBeltLengthMeters = fixedAssemblyLengthMeters + minAdjustableStrapLengthMeters;
            maxTotalBeltLengthMeters = fixedAssemblyLengthMeters + maxAdjustableStrapLengthMeters;
            maxStrapPayoutMeters = maxAdjustableStrapLengthMeters;
            _lengthInitialized = true;
        }

        public void CacheRestTransforms()
        {
            float myX = GetBeltPlaneX();
            fixedHookRestPos = platformHookLeft != null
                ? platformHookLeft.AnchorWorldPosition
                : new Vector3(myX, 0.1343f, -6.1440f);
            fixedHookRestRot = platformHookLeft != null
                ? platformHookLeft.AnchorWorldRotation
                : Quaternion.Euler(0f, 0f, 90f);

            // Accessible operator-side resting spot on the factory floor at the opposite/free end of the belt
            // With buckle at Z = -6.4028 m and unspooled adjustable length = 2.020 m, placing the J-Hook at Z = -8.385 m
            // extends the strap cleanly along the factory floor so J_Hook_Adjustable sits right at the very tip of the webbing!
            adjHookRestPos = new Vector3(myX, 0.0445f, -8.3850f);
            adjHookRestRot = ComputeNaturalFreeHookRotation(adjHookRestPos);

            if (!isAdjHookAttached)
            {
                if (mechanicsController != null && mechanicsController.adjustableHookTransform != null)
                {
                    Vector3 cur = mechanicsController.adjustableHookTransform.position;
                    // If the free hook is still near the old platform-top or mid-floor position, snap it to the floor tip resting position
                    if (cur.z > -8.25f && cur.y < 0.25f && !hasWrappedOverMold)
                        cur = adjHookRestPos;
                    unattachedRestingWorldPos = ConstrainFreeEndWorldPosition(cur);
                    unattachedRestingWorldRot = ComputeNaturalFreeHookRotation(unattachedRestingWorldPos);
                }
                else
                {
                    unattachedRestingWorldPos = adjHookRestPos;
                    unattachedRestingWorldRot = adjHookRestRot;
                }
            }
            else
            {
                unattachedRestingWorldPos = adjHookRestPos;
                unattachedRestingWorldRot = adjHookRestRot;
            }
        }

        private void SyncInitialAttachmentStates()
        {
            if (isFixedHookAttached && platformHookLeft != null)
            {
                fixedAttachedTo = platformHookLeft;
                if (mechanicsController != null && mechanicsController.fixedHookTransform != null)
                {
                    platformHookLeft.SetOccupied(mechanicsController.fixedHookTransform);
                    mechanicsController.fixedHookTransform.SetPositionAndRotation(
                        platformHookLeft.AnchorWorldPosition,
                        platformHookLeft.AnchorWorldRotation);
                }
            }

            if (isAdjHookAttached && platformHookRight != null)
            {
                adjAttachedTo = platformHookRight;
                hasWrappedOverMold = true;
                if (mechanicsController != null && mechanicsController.adjustableHookTransform != null)
                {
                    platformHookRight.SetOccupied(mechanicsController.adjustableHookTransform);
                    mechanicsController.adjustableHookTransform.SetPositionAndRotation(
                        platformHookRight.AnchorWorldPosition,
                        platformHookRight.AnchorWorldRotation);
                }
            }
            else if (!isAdjHookAttached && mechanicsController != null && mechanicsController.adjustableHookTransform != null)
            {
                if (adjAttachedTo != null)
                {
                    adjAttachedTo.SetOccupied(null);
                    adjAttachedTo = null;
                }
                if (unattachedRestingWorldPos == Vector3.zero)
                    unattachedRestingWorldPos = adjHookRestPos;

                unattachedRestingWorldPos = ConstrainFreeEndWorldPosition(unattachedRestingWorldPos);
                unattachedRestingWorldRot = ComputeNaturalFreeHookRotation(unattachedRestingWorldPos);
                mechanicsController.adjustableHookTransform.SetPositionAndRotation(unattachedRestingWorldPos, unattachedRestingWorldRot);
            }

            UpdateBuckleAssemblyPosture();
            SyncAllStitchVisuals();
        }

        private Transform _cachedIndustrialAssembly;
        private Transform _cachedFixedWebbing;
        private Transform _cachedSafetyLabel;
        private Transform _cachedStitchBuckleLoop;

        /// <summary>
        /// Smoothly interpolates the buckle assembly (RatchetBelt_Industrial_Assembly) between its natural
        /// right-side-up resting posture on the factory floor (when unattached on the -Z operator side)
        /// and its 60° mold-clamping posture (when lifting/wrapping over MoldCast or attached to Near hook),
        /// while keeping J_Hook_Fixed mechanically connected to Platform_SideHook_Far at constant Fixed_Webbing length (0.2784 m).
        /// </summary>
        public void UpdateBuckleAssemblyPosture()
        {
            if (mechanicsController == null) return;

            if (_cachedIndustrialAssembly == null)
            {
                Transform t = mechanicsController.transform.Find("RatchetBelt_Industrial_Assembly");
                if (t == null && mechanicsController.mandrelTransform != null)
                    t = mechanicsController.mandrelTransform.parent;
                _cachedIndustrialAssembly = t;
            }
            if (_cachedIndustrialAssembly == null) return;

            if (_cachedFixedWebbing == null)
                _cachedFixedWebbing = _cachedIndustrialAssembly.Find("Fixed_Webbing");
            if (_cachedSafetyLabel == null && _cachedFixedWebbing != null)
                _cachedSafetyLabel = _cachedFixedWebbing.Find("Safety_Inspection_Label");
            if (_cachedStitchBuckleLoop == null)
                _cachedStitchBuckleLoop = _cachedIndustrialAssembly.Find("Stitch_Buckle_Loop");

            float beltX = mechanicsController.transform.position.x;

            // Compute blend factor t in [0..1]: 0 = resting on factory floor, 1 = mold-clamping posture
            float tBlend = 0f;
            if (isAdjHookAttached || hasWrappedOverMold)
            {
                tBlend = 1f;
            }
            else if (mechanicsController.adjustableHookTransform != null)
            {
                Vector3 adjPos = mechanicsController.adjustableHookTransform.position;
                float tY = Mathf.InverseLerp(0.08f, 0.42f, adjPos.y);
                float tZ = Mathf.InverseLerp(-6.50f, -5.80f, adjPos.z);
                tBlend = Mathf.SmoothStep(0f, 1f, Mathf.Max(tY, tZ));
            }

            Vector3 floorBucklePos = new Vector3(beltX, 0.0320f, -6.4028f);
            Quaternion floorBuckleRot = Quaternion.Euler(0f, 270f, 0f);

            Vector3 moldVibroDelta = Vector3.zero;
            if (Application.isPlaying && activeMoldTransform != null)
            {
                var coupler = activeMoldTransform.GetComponent<MoldVibroPlatformCoupler>();
                if (coupler != null)
                    moldVibroDelta = coupler.CurrentDeckDisplacement;
            }

            Vector3 moldBucklePos = new Vector3(beltX, 0.3713f, -5.9979f) + moldVibroDelta;
            Quaternion moldBuckleRot = Quaternion.Euler(0f, 270f, 60f);

            // Preserve world transforms of J_Hook_Fixed and J_Hook_Adjustable while pivoting parent
            Vector3 savedFixedPos = mechanicsController.fixedHookTransform != null ? mechanicsController.fixedHookTransform.position : Vector3.zero;
            Quaternion savedFixedRot = mechanicsController.fixedHookTransform != null ? mechanicsController.fixedHookTransform.rotation : Quaternion.identity;
            Vector3 savedAdjPos = mechanicsController.adjustableHookTransform != null ? mechanicsController.adjustableHookTransform.position : Vector3.zero;
            Quaternion savedAdjRot = mechanicsController.adjustableHookTransform != null ? mechanicsController.adjustableHookTransform.rotation : Quaternion.identity;

            Vector3 targetBucklePos = Vector3.Lerp(floorBucklePos, moldBucklePos, tBlend);
            Quaternion targetBuckleRot = Quaternion.Slerp(floorBuckleRot, moldBuckleRot, tBlend);
            _cachedIndustrialAssembly.SetPositionAndRotation(targetBucklePos, targetBuckleRot);

            if (mechanicsController.fixedHookTransform != null)
            {
                if (isFixedHookAttached && platformHookLeft != null)
                {
                    mechanicsController.fixedHookTransform.SetPositionAndRotation(
                        platformHookLeft.AnchorWorldPosition,
                        platformHookLeft.AnchorWorldRotation);
                }
                else
                {
                    mechanicsController.fixedHookTransform.SetPositionAndRotation(savedFixedPos, savedFixedRot);
                }
            }

            if (mechanicsController.adjustableHookTransform != null)
            {
                if (isAdjHookAttached && platformHookRight != null)
                {
                    mechanicsController.adjustableHookTransform.SetPositionAndRotation(
                        platformHookRight.AnchorWorldPosition,
                        platformHookRight.AnchorWorldRotation);
                }
                else
                {
                    mechanicsController.adjustableHookTransform.SetPositionAndRotation(savedAdjPos, savedAdjRot);
                }
            }

            // Orient Fixed_Webbing (constant unscaled length 0.2784 m) from the buckle directly to J_Hook_Fixed
            if (_cachedFixedWebbing != null && mechanicsController.fixedHookTransform != null)
            {
                Vector3 fixAnchor = mechanicsController.fixedHookTransform.position;
                Vector3 dirToFixed = (fixAnchor - targetBucklePos);
                if (dirToFixed.sqrMagnitude > 0.0001f)
                {
                    dirToFixed.Normalize();
                    // Fixed_Webbing local -X points toward J_Hook_Fixed, local +Y spans width (-1, 0, 0)
                    Vector3 fwRight = -dirToFixed;
                    Vector3 fwUp = new Vector3(-1f, 0f, 0f);
                    Vector3 fwFwd = Vector3.Cross(fwRight, fwUp).normalized;
                    if (fwFwd.sqrMagnitude > 0.0001f)
                    {
                        _cachedFixedWebbing.SetPositionAndRotation(targetBucklePos, Quaternion.LookRotation(fwFwd, fwUp));
                    }
                }
            }
        }

        private void LateUpdate()
        {
            if (!_lengthInitialized)
                RecalculateBeltLengthConstraints();

            UpdateBuckleAssemblyPosture();

            // 1. Keep Fixed J-Hook rigidly locked to its Far platform side hook (never moves when free end is grabbed)
            if (isFixedHookAttached && fixedAttachedTo != null && mechanicsController != null && mechanicsController.fixedHookTransform != null)
            {
                mechanicsController.fixedHookTransform.SetPositionAndRotation(
                    fixedAttachedTo.AnchorWorldPosition,
                    fixedAttachedTo.AnchorWorldRotation);
            }

            // 2. Keep Adjustable J-Hook locked when attached, or smoothly settle/maintain its constrained unattached resting pose
            if (mechanicsController != null && mechanicsController.adjustableHookTransform != null)
            {
                Transform adjTr = mechanicsController.adjustableHookTransform;
                if (isAdjHookAttached && adjAttachedTo != null)
                {
                    adjTr.SetPositionAndRotation(adjAttachedTo.AnchorWorldPosition, adjAttachedTo.AnchorWorldRotation);
                    hasWrappedOverMold = true;
                }
                else if (!isFreeEndGrabbed && Application.isPlaying)
                {
                    if (isSettlingToSurface)
                    {
                        float supportY = GetSupportingSurfaceHeightAt(unattachedRestingWorldPos.x, unattachedRestingWorldPos.z, unattachedRestingWorldPos.y);
                        unattachedRestingWorldPos.y = Mathf.MoveTowards(unattachedRestingWorldPos.y, supportY, 2.4f * Time.deltaTime);
                        unattachedRestingWorldPos = ConstrainFreeEndWorldPosition(unattachedRestingWorldPos);
                        if (Mathf.Abs(unattachedRestingWorldPos.y - supportY) <= 0.0015f)
                        {
                            unattachedRestingWorldPos.y = supportY;
                            unattachedRestingWorldPos = ConstrainFreeEndWorldPosition(unattachedRestingWorldPos);
                            isSettlingToSurface = false;
                        }
                    }
                    else
                    {
                        unattachedRestingWorldPos = ConstrainFreeEndWorldPosition(unattachedRestingWorldPos);
                    }

                    unattachedRestingWorldRot = ComputeNaturalFreeHookRotation(unattachedRestingWorldPos);
                    adjTr.SetPositionAndRotation(unattachedRestingWorldPos, unattachedRestingWorldRot);
                    UpdateMoldWrapProgress(adjTr.position);
                }
            }

            SyncAllStitchVisuals();
        }

        public void SyncAdjustableStitchVisual()
        {
            SyncAllStitchVisuals();
        }

        public void SyncAllStitchVisuals()
        {
            if (mechanicsController == null) return;

            // 1. Fixed Hook & Buckle Loop stitches stay aligned flush along the fixed strap tangent
            if (mechanicsController.fixedHookTransform != null && mechanicsController.stitchFixTransform != null)
            {
                Vector3 fixEyelet = mechanicsController.fixedHookTransform.position +
                                    mechanicsController.fixedHookTransform.rotation * new Vector3(-0.0020f, 0f, 0.0004f);
                Vector3 mandrelCenter = GetMandrelWorldCenter();
                Vector3 fixDir = (mandrelCenter - fixEyelet);
                if (fixDir.sqrMagnitude < 0.0001f) fixDir = new Vector3(0f, 0.885f, 0.466f);
                else fixDir.Normalize();

                Vector3 fixNormal = Vector3.Cross(new Vector3(-1f, 0f, 0f), fixDir).normalized;
                if (fixNormal.sqrMagnitude < 0.0001f) fixNormal = Vector3.up;

                mechanicsController.stitchFixTransform.SetPositionAndRotation(
                    fixEyelet + fixDir * 0.032f + fixNormal * 0.0016f,
                    Quaternion.LookRotation(fixNormal, fixDir));

                if (_cachedStitchBuckleLoop != null)
                {
                    _cachedStitchBuckleLoop.SetPositionAndRotation(
                        fixEyelet + fixDir * 0.194f + fixNormal * 0.0016f,
                        Quaternion.LookRotation(fixNormal, fixDir));
                }
            }

            // 2. Adjustable Hook stitch aligns flush with the arriving adjustable webbing tangent
            if (mechanicsController.adjustableHookTransform != null && mechanicsController.stitchAdjTransform != null)
            {
                Transform adj = mechanicsController.adjustableHookTransform;
                Vector3 eyelet = adj.position + adj.rotation * GetAdjustableEyeletLocalOffset();

                Vector3 backAlongStrap;
                Vector3 ribbonBinormal = new Vector3(-1f, 0f, 0f);
                if (ribbonRenderer != null && ribbonRenderer.EndArrivalTangent.sqrMagnitude > 0.1f)
                {
                    backAlongStrap = -ribbonRenderer.EndArrivalTangent.normalized;
                    if (ribbonRenderer.EndArrivalBinormal.sqrMagnitude > 0.1f)
                        ribbonBinormal = ribbonRenderer.EndArrivalBinormal.normalized;
                }
                else
                {
                    backAlongStrap = adj.right.sqrMagnitude > 0.1f ? adj.right.normalized : Vector3.up;
                }

                Vector3 surfNormal = Vector3.Cross(backAlongStrap, ribbonBinormal).normalized;
                if (surfNormal.sqrMagnitude < 0.0001f) surfNormal = Vector3.up;

                Vector3 stitchPos = eyelet + backAlongStrap * 0.026f + surfNormal * 0.0016f;
                if (stitchPos.y < 0.022f)
                {
                    stitchPos.y = Mathf.Max(0.022f, stitchPos.y);
                    backAlongStrap = new Vector3(backAlongStrap.x, Mathf.Max(0f, backAlongStrap.y), backAlongStrap.z).normalized;
                    if (backAlongStrap.sqrMagnitude < 0.0001f) backAlongStrap = Vector3.forward;
                    surfNormal = Vector3.Cross(backAlongStrap, ribbonBinormal).normalized;
                    if (surfNormal.sqrMagnitude < 0.0001f) surfNormal = Vector3.up;
                }

                mechanicsController.stitchAdjTransform.SetPositionAndRotation(
                    stitchPos,
                    Quaternion.LookRotation(surfNormal, backAlongStrap));
            }
        }

        /// <summary>
        /// Computes a physically consistent J-Hook orientation whose top webbing slot faces directly into
        /// the arriving strap tangent without twisting upside-down, blending cleanly into the Near hook's
        /// seating orientation when aligned near Platform_SideHook_Near.
        /// </summary>
        public Quaternion ComputeNaturalFreeHookRotation(Vector3 hookWorldPos)
        {
            if (isAdjHookAttached && adjAttachedTo != null)
                return adjAttachedTo.AnchorWorldRotation;

            // Default incoming webbing direction at the free hook
            Vector3 toWebbing;
            Vector3 widthAxis = Vector3.right; // +X across the 50 mm strap

            if (ribbonRenderer != null && ribbonRenderer.EndArrivalTangent.sqrMagnitude > 0.1f && Application.isPlaying)
            {
                toWebbing = -ribbonRenderer.EndArrivalTangent.normalized;
                if (ribbonRenderer.EndArrivalBinormal.sqrMagnitude > 0.1f)
                    widthAxis = -ribbonRenderer.EndArrivalBinormal.normalized;
            }
            else
            {
                Vector3 anchor = GetMandrelWorldCenter();
                if (ShouldRouteOverMold(hookWorldPos) && TryGetMoldExteriorBounds(out Bounds mb))
                {
                    anchor = new Vector3(GetBeltPlaneX(), mb.max.y + 0.004f, hookWorldPos.z >= mb.max.z ? mb.max.z : mb.min.z);
                }
                toWebbing = (anchor - hookWorldPos);
                if (toWebbing.sqrMagnitude < 0.0004f) toWebbing = Vector3.up;
                else toWebbing.Normalize();
            }

            // Ensure J-Hook local +X (right) points into incoming webbing, local +Y (up) spans strap width (+X)
            Vector3 localRight = toWebbing;
            Vector3 localUp = widthAxis - localRight * Vector3.Dot(widthAxis, localRight);
            if (localUp.sqrMagnitude < 0.01f)
            {
                localUp = Vector3.Cross(Vector3.forward, localRight);
                if (localUp.sqrMagnitude < 0.01f) localUp = Vector3.right;
            }
            localUp.Normalize();
            if (localUp.x < 0f) localUp = -localUp;

            Vector3 localForward = Vector3.Cross(localRight, localUp).normalized;
            if (localForward.sqrMagnitude < 0.001f) localForward = Vector3.back;

            // When resting on the factory floor on the operator (-Z) side, keep the J-hook bend upright (+Y)
            // so the entire metal hook body sits cleanly above the floor (minY >= 0.004 m) and is easy to grab!
            if (!isAdjHookAttached && !hasWrappedOverMold && hookWorldPos.z < -6.30f && hookWorldPos.y < 0.16f && localForward.y < 0f)
            {
                localUp = -localUp;
                localForward = -localForward;
            }

            Quaternion strapAlignedRot = Quaternion.LookRotation(localForward, localUp);

            // Smoothly blend into exact Near side-hook seating pose when brought within 30 cm of Platform_SideHook_Near
            if (platformHookRight != null)
            {
                float d = Vector3.Distance(hookWorldPos, platformHookRight.AnchorWorldPosition);
                if (d < 0.32f)
                {
                    float t = Mathf.SmoothStep(0f, 1f, 1f - Mathf.Clamp01((d - 0.06f) / 0.26f));
                    strapAlignedRot = Quaternion.Slerp(strapAlignedRot, platformHookRight.AnchorWorldRotation, t);
                }
            }

            return strapAlignedRot;
        }

        // ------------------------------------------------------------------
        // VR & Desktop Free-End Grabbing Lifecycle
        // ------------------------------------------------------------------

        public bool CanDetachAdjustableHook(out string blockReason)
        {
            blockReason = string.Empty;
            if (!isAdjHookAttached)
                return true;

            if (mechanicsController != null &&
                (mechanicsController.CompletedStrokes > 0 || mechanicsController.TargetShortening > 0.0005f))
            {
                blockReason = $"Belt #{mechanicsController.beltIndex} is under ratchet tension ({mechanicsController.CompletedStrokes}/4 strokes)! Pull Release Catch / Pawl to release tension before unhooking.";
                return false;
            }

            return true;
        }

        public bool BeginFreeEndGrab(out string statusMessage)
        {
            statusMessage = string.Empty;
            ResolveEnvironmentReferences();
            if (!_lengthInitialized)
                RecalculateBeltLengthConstraints();

            if (isAdjHookAttached)
            {
                if (!CanDetachAdjustableHook(out string blockReason))
                {
                    statusMessage = blockReason;
                    if (mechanicsController != null)
                        mechanicsController.PublishUserNotification(blockReason);
                    return false;
                }

                Vector3 curPos = mechanicsController.adjustableHookTransform.position;
                Quaternion curRot = mechanicsController.adjustableHookTransform.rotation;
                DetachAdjustableHook(snapToRestPosition: false);
                unattachedRestingWorldPos = ConstrainFreeEndWorldPosition(curPos);
                unattachedRestingWorldRot = curRot;
                mechanicsController.adjustableHookTransform.SetPositionAndRotation(unattachedRestingWorldPos, curRot);
            }

            isFreeEndGrabbed = true;
            isSettlingToSurface = false;
            if (mechanicsController != null)
                RatchetBeltMechanicsController.ActiveBeltIndex = mechanicsController.beltIndex;

            UpdateAlignmentFeedbackWhileHeld(
                mechanicsController != null && mechanicsController.adjustableHookTransform != null
                    ? mechanicsController.adjustableHookTransform.position
                    : unattachedRestingWorldPos,
                out _);
            return true;
        }

        /// <summary>
        /// Updates the live 3D position and rotation of the grabbed free J-hook, enforcing strict routed
        /// belt-length limits [MinTotalBeltLength, MaxTotalBeltLength] and smooth exterior surface sliding.
        /// </summary>
        public Vector3 UpdateGrabbedFreeEndPose(Vector3 desiredWorldPos, Quaternion? desiredWorldRot = null)
        {
            if (mechanicsController == null || mechanicsController.adjustableHookTransform == null)
                return desiredWorldPos;

            isFreeEndGrabbed = true;
            isSettlingToSurface = false;

            // Update progressive wrap state BEFORE & AFTER constraining so lifting over MoldCast engages smoothly
            UpdateMoldWrapProgress(desiredWorldPos);
            Vector3 constrainedPos = ConstrainFreeEndWorldPosition(desiredWorldPos);
            UpdateMoldWrapProgress(constrainedPos);

            Quaternion naturalRot = ComputeNaturalFreeHookRotation(constrainedPos);
            Quaternion rot = desiredWorldRot.HasValue
                ? Quaternion.Slerp(naturalRot, desiredWorldRot.Value, 0.15f)
                : naturalRot;

            mechanicsController.adjustableHookTransform.SetPositionAndRotation(constrainedPos, rot);
            unattachedRestingWorldPos = constrainedPos;
            unattachedRestingWorldRot = rot;

            UpdateBuckleAssemblyPosture();
            UpdateAlignmentFeedbackWhileHeld(constrainedPos, out _);
            if (ribbonRenderer != null)
            {
                ribbonRenderer.UpdateRibbonGeometry();
            }
            SyncAllStitchVisuals();

            return constrainedPos;
        }

        public bool CompleteFreeEndRelease(out string resultMessage)
        {
            isFreeEndGrabbed = false;
            ClearAllHookHighlights();

            if (mechanicsController == null || mechanicsController.adjustableHookTransform == null)
            {
                resultMessage = "Adjustable hook unavailable.";
                return false;
            }

            Vector3 releasePos = ConstrainFreeEndWorldPosition(mechanicsController.adjustableHookTransform.position);
            unattachedRestingWorldPos = releasePos;

            float validDist = platformHookRight != null
                ? Vector3.Distance(releasePos, platformHookRight.AnchorWorldPosition)
                : float.MaxValue;
            PlatformSideHook wrongHook = FindNearbyWrongHook(releasePos, out float wrongDist);
            if (wrongHook != null && (wrongDist < validDist || validDist > attachmentProximityThreshold))
            {
                isSettlingToSurface = true;
                int myIdx = mechanicsController.beltIndex;
                resultMessage = $"Wrong Hook Rejected: Belt #{myIdx} cannot attach to {wrongHook.name} (Belt #{wrongHook.assignedBeltIndex} {wrongHook.side}). Connect to Platform_SideHook_Near_{myIdx}!";
                mechanicsController.PublishUserNotification(resultMessage);
                Debug.LogWarning($"[PlatformHookAttachment] {resultMessage}");
                return false;
            }

            if (CanAttachAdjustableToHook(platformHookRight, checkProximityAndWrap: true, out string rejectReason))
            {
                AttachAdjustableHook(platformHookRight);
                resultMessage = $"<color=#00FFAA><b>★ Belt #{mechanicsController.beltIndex} Connected to {platformHookRight.name}! Operate Ratchet Lever to Tighten. ★</b></color>";
                return true;
            }

            isSettlingToSurface = true;
            unattachedRestingWorldRot = ComputeNaturalFreeHookRotation(releasePos);

            if (!string.IsNullOrEmpty(rejectReason))
                resultMessage = $"<color=#FFCC00><b>Strap Released in Place: {rejectReason}</b></color>";
            else
                resultMessage = "<color=#00E5FF><b>Strap Released in Place (Shape Retained)</b></color>";

            return false;
        }

        // ------------------------------------------------------------------
        // Strict Routed Belt-Length Constraint & Exterior Surface Sliding
        // ------------------------------------------------------------------

        public bool ShouldRouteOverMold(Vector3 hookWorldPos)
        {
            if (isAdjHookAttached)
                return true;

            if (!TryGetMoldExteriorBounds(out Bounds moldB))
                return false;

            // If pulled laterally completely outside the mold's X width, do not force wrap over top
            if (hookWorldPos.x < moldB.min.x - 0.08f || hookWorldPos.x > moldB.max.x + 0.08f)
                return false;

            if (hasWrappedOverMold)
            {
                // Stay wrapped unless brought back low onto the operator (-Z) side
                if (hookWorldPos.z < moldB.min.z - 0.02f && hookWorldPos.y < moldB.max.y - 0.08f)
                    return false;
                return true;
            }

            // Engage upper mold routing smoothly once lifted near/above the upper half of the -Z wall
            return hookWorldPos.z > moldB.min.z - 0.025f && hookWorldPos.y >= moldB.max.y - 0.16f;
        }

        public Vector3 ConstrainFreeEndWorldPosition(Vector3 worldPos)
        {
            ResolveEnvironmentReferences();
            if (!_lengthInitialized)
                RecalculateBeltLengthConstraints();

            // Step 1: Push outside solid environment surfaces (floor, platform deck, MoldCast exterior)
            worldPos = PushHookOutsideEnvironment(worldPos);

            // Step 2: Enforce STRICT routed belt-length ceiling (never straight-line Euclidean!)
            float maxAdjBudget = CurrentActiveAdjustableStrapLengthMeters - 0.0005f;
            Quaternion naturalRot = ComputeNaturalFreeHookRotation(worldPos);
            Vector3 eyeletOffset = naturalRot * GetAdjustableEyeletLocalOffset();
            Vector3 pEyelet = worldPos + eyeletOffset;

            GetRoutedAnchorAndPrefixLength(pEyelet, ShouldRouteOverMold(worldPos), out Vector3 lastContactAnchor, out float prefixArcLength);

            Vector3 spanVec = pEyelet - lastContactAnchor;
            float spanDist = spanVec.magnitude;
            float totalRouted = prefixArcLength + spanDist;

            if (totalRouted > maxAdjBudget && spanDist > 0.0001f)
            {
                float allowedSpan = Mathf.Max(0.015f, maxAdjBudget - prefixArcLength);
                Vector3 clampedEyelet = lastContactAnchor + (spanVec / spanDist) * allowedSpan;
                worldPos = clampedEyelet - eyeletOffset;

                // Re-project onto exterior surface if clamping pulled slightly toward a corner, then enforce hard ceiling
                worldPos = PushHookOutsideEnvironment(worldPos);
                naturalRot = ComputeNaturalFreeHookRotation(worldPos);
                eyeletOffset = naturalRot * GetAdjustableEyeletLocalOffset();
                pEyelet = worldPos + eyeletOffset;
                GetRoutedAnchorAndPrefixLength(pEyelet, ShouldRouteOverMold(worldPos), out lastContactAnchor, out prefixArcLength);
                spanVec = pEyelet - lastContactAnchor;
                spanDist = spanVec.magnitude;
                if (prefixArcLength + spanDist > maxAdjBudget && spanDist > 0.0001f)
                {
                    allowedSpan = Mathf.Max(0.015f, maxAdjBudget - prefixArcLength);
                    worldPos = (lastContactAnchor + (spanVec / spanDist) * allowedSpan) - eyeletOffset;
                }
            }

            return worldPos;
        }

        /// <summary>
        /// Computes the exact routed arc-length consumed from the Mandrel spool departure tangent over any
        /// contacted MoldCast upper-hull corners up to the final straight free span toward candidateEyelet.
        /// </summary>
        public void GetRoutedAnchorAndPrefixLength(
            Vector3 candidateEyelet,
            bool routeOverMold,
            out Vector3 lastContactAnchor,
            out float prefixArcLength)
        {
            float beltX = GetBeltPlaneX();
            Vector3 mandrelCenter = GetMandrelWorldCenter();
            float spoolRadius = mechanicsController != null ? mechanicsController.CurrentSpoolRadius : RatchetBeltMechanicsController.MIN_SPOOL_RADIUS;
            Vector2 spoolZY = new Vector2(mandrelCenter.z, mandrelCenter.y);

            if (!routeOverMold || activeMoldTransform == null)
            {
                Vector2 pStart = ComputeUpperSpoolTangent(spoolZY, spoolRadius, new Vector2(candidateEyelet.z, candidateEyelet.y));
                lastContactAnchor = new Vector3(beltX, pStart.y, pStart.x);
                prefixArcLength = 0f;
                return;
            }

            const float strapWidth = 0.050f;
            const float surfaceClearance = 0.0040f;
            float minX = Mathf.Min(beltX, candidateEyelet.x) - strapWidth * 0.5f - 0.003f;
            float maxX = Mathf.Max(beltX, candidateEyelet.x) + strapWidth * 0.5f + 0.003f;
            CollectMoldSlicesForBand(minX, maxX, surfaceClearance);

            if (_moldSlices.Count == 0)
            {
                Vector2 pStart = ComputeUpperSpoolTangent(spoolZY, spoolRadius, new Vector2(candidateEyelet.z, candidateEyelet.y));
                lastContactAnchor = new Vector3(beltX, pStart.y, pStart.x);
                prefixArcLength = 0f;
                return;
            }

            float maxRouteZ = candidateEyelet.z;
            _hullInput.Clear();
            for (int i = 0; i < _moldSlices.Count; i++)
            {
                MoldObstacleSlice r = _moldSlices[i];
                if (r.minZ <= maxRouteZ + 0.005f && r.minZ >= spoolZY.x + 0.01f)
                    _hullInput.Add(new Vector2(r.minZ, r.maxY));
                if (r.maxZ <= maxRouteZ + 0.005f && r.maxZ >= spoolZY.x + 0.01f)
                    _hullInput.Add(new Vector2(r.maxZ, r.maxY));
            }

            if (_hullInput.Count == 0)
            {
                Vector2 pStart = ComputeUpperSpoolTangent(spoolZY, spoolRadius, new Vector2(candidateEyelet.z, candidateEyelet.y));
                lastContactAnchor = new Vector3(beltX, pStart.y, pStart.x);
                prefixArcLength = 0f;
                return;
            }

            Vector2 approxFirst = _hullInput[0];
            for (int i = 1; i < _hullInput.Count; i++)
                if (_hullInput[i].y > approxFirst.y) approxFirst = _hullInput[i];

            Vector2 start2D = ComputeUpperSpoolTangent(spoolZY, spoolRadius, approxFirst);
            Vector2 end2D = new Vector2(Mathf.Max(candidateEyelet.z, spoolZY.x + 0.02f), candidateEyelet.y);
            ComputeUpperConvexHull(start2D, end2D, _hullInput, _upperHull);

            if (_upperHull.Count >= 2)
            {
                start2D = ComputeUpperSpoolTangent(spoolZY, spoolRadius, _upperHull[1]);
                _upperHull[0] = start2D;
            }

            if (_upperHull.Count <= 2)
            {
                lastContactAnchor = new Vector3(beltX, start2D.y, start2D.x);
                prefixArcLength = 0f;
                return;
            }

            // Sum filleted prefix up to the last mold contact vertex _upperHull[count - 2]
            List<Vector2> prefixHull = new List<Vector2>(_upperHull.Count - 1);
            for (int i = 0; i < _upperHull.Count - 1; i++)
                prefixHull.Add(_upperHull[i]);

            List<Vector2> filletedPrefix = BuildFilletedHullProfile(prefixHull, 0.016f);
            float sum = 0f;
            for (int i = 1; i < filletedPrefix.Count; i++)
            {
                sum += Vector2.Distance(filletedPrefix[i], filletedPrefix[i - 1]);
            }

            Vector2 last2D = filletedPrefix[filletedPrefix.Count - 1];
            prefixArcLength = sum;
            lastContactAnchor = new Vector3(beltX, last2D.y, last2D.x);
        }

        private Vector3 PushHookOutsideEnvironment(Vector3 worldPos)
        {
            // 1. Factory floor minimum height
            worldPos.y = Mathf.Max(0.0445f, worldPos.y);

            // 2. Vibrating Platform deck & steel frame collision exclusion
            if (TryGetPlatformWorldBounds(out Bounds platBounds))
            {
                const float hookPad = 0.020f;
                if (worldPos.x > platBounds.min.x - hookPad && worldPos.x < platBounds.max.x + hookPad &&
                    worldPos.z > platBounds.min.z + 0.025f && worldPos.z < platBounds.max.z - 0.025f)
                {
                    float deckClearY = platBounds.max.y + 0.018f;
                    if (worldPos.y < deckClearY)
                        worldPos.y = deckClearY;
                }
            }

            // 3. Active MoldCast 3D exterior push-out
            if (TryGetMoldExteriorBounds(out Bounds moldBounds))
            {
                const float pad = 0.024f;
                float minX = moldBounds.min.x - pad;
                float maxX = moldBounds.max.x + pad;
                float minY = moldBounds.min.y;
                float maxY = moldBounds.max.y + pad;
                float minZ = moldBounds.min.z - pad;
                float maxZ = moldBounds.max.z + pad;

                if (worldPos.x > minX && worldPos.x < maxX &&
                    worldPos.y >= minY && worldPos.y < maxY &&
                    worldPos.z > minZ && worldPos.z < maxZ)
                {
                    float dTop = maxY - worldPos.y;
                    float dBack = worldPos.z - minZ;
                    float dFront = maxZ - worldPos.z;
                    float dLeft = worldPos.x - minX;
                    float dRight = maxX - worldPos.x;

                    float best = Mathf.Min(dTop, Mathf.Min(Mathf.Min(dBack, dFront), Mathf.Min(dLeft, dRight)));
                    if (best == dTop) worldPos.y = maxY;
                    else if (best == dBack) worldPos.z = minZ;
                    else if (best == dFront) worldPos.z = maxZ;
                    else if (best == dLeft) worldPos.x = minX;
                    else worldPos.x = maxX;
                }
            }

            return worldPos;
        }

        public float GetBeltPlaneX()
        {
            if (mechanicsController != null && mechanicsController.mandrelTransform != null)
                return mechanicsController.mandrelTransform.position.x;
            return transform.position.x;
        }

        public Vector3 GetMandrelWorldCenter()
        {
            if (mechanicsController != null && mechanicsController.mandrelTransform != null)
                return mechanicsController.mandrelTransform.position;
            return new Vector3(transform.position.x, 0.3891f, -6.0197f);
        }

        private Vector3 GetAdjustableEyeletLocalOffset()
        {
            if (mechanicsController != null && mechanicsController.adjustableHookTransform != null)
            {
                Transform eye = mechanicsController.adjustableHookTransform.Find("Anchor_AdjustableHook");
                if (eye != null) return eye.localPosition;
            }
            return new Vector3(0.0005f, 0f, -0.0031f);
        }

        private Vector3 ComputeApproximateEyeletWorldOffset(Vector3 hookWorldPos)
        {
            Quaternion rot = (mechanicsController != null && mechanicsController.adjustableHookTransform != null)
                ? mechanicsController.adjustableHookTransform.rotation
                : Quaternion.Euler(0f, 180f, 90f);
            return rot * GetAdjustableEyeletLocalOffset();
        }

        private void CollectMoldSlicesForBand(float minX, float maxX, float clearance)
        {
            _moldSlices.Clear();
            if (activeMoldTransform == null || !activeMoldTransform.gameObject.activeInHierarchy)
                return;

            Renderer[] rends = activeMoldTransform.GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < rends.Length; i++)
            {
                Renderer r = rends[i];
                if (r == null || !r.enabled || r.name == "Mold_Material_Volume") continue;
                MoldPart mp = r.GetComponent<MoldPart>();
                if (mp != null && mp.IsFree) continue;

                Bounds b = r.bounds;
                if (b.max.x >= minX && b.min.x <= maxX)
                {
                    _moldSlices.Add(new MoldObstacleSlice
                    {
                        minZ = b.min.z - clearance,
                        maxZ = b.max.z + clearance,
                        maxY = b.max.y + clearance
                    });
                }
            }
        }

        private float GetSupportingSurfaceHeightAt(float x, float z, float currentY)
        {
            float supportY = 0.0445f;

            if (TryGetPlatformWorldBounds(out Bounds platB))
            {
                if (x >= platB.min.x && x <= platB.max.x && z >= platB.min.z && z <= platB.max.z)
                {
                    supportY = Mathf.Max(supportY, 0.142f);
                }
            }

            if (TryGetMoldExteriorBounds(out Bounds moldB))
            {
                if (x >= moldB.min.x - 0.01f && x <= moldB.max.x + 0.01f &&
                    z >= moldB.min.z - 0.01f && z <= moldB.max.z + 0.01f &&
                    currentY >= moldB.max.y - 0.04f)
                {
                    supportY = Mathf.Max(supportY, moldB.max.y + 0.024f);
                }
            }

            return supportY;
        }

        public bool TryGetMoldExteriorBounds(out Bounds moldBounds)
        {
            moldBounds = default;
            if (activeMoldTransform == null)
                ResolveEnvironmentReferences();
            if (activeMoldTransform == null || !activeMoldTransform.gameObject.activeInHierarchy)
                return false;

            bool hasAny = false;
            Renderer[] rends = activeMoldTransform.GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < rends.Length; i++)
            {
                Renderer r = rends[i];
                if (r == null || !r.enabled) continue;
                if (r.name == "Mold_Material_Volume") continue;

                MoldPart mp = r.GetComponent<MoldPart>();
                if (mp != null && mp.IsFree) continue;

                if (!hasAny)
                {
                    moldBounds = r.bounds;
                    hasAny = true;
                }
                else
                {
                    moldBounds.Encapsulate(r.bounds);
                }
            }
            return hasAny;
        }

        public bool TryGetPlatformWorldBounds(out Bounds platBounds)
        {
            platBounds = new Bounds(new Vector3(-22.540f, 0.060f, -5.460f), new Vector3(4.389f, 0.120f, 1.201f));
            if (platformDeckTransform != null)
            {
                Renderer r = platformDeckTransform.GetComponent<Renderer>();
                if (r != null)
                {
                    Bounds b = r.bounds;
                    platBounds = new Bounds(
                        new Vector3(b.center.x, b.max.y * 0.5f, b.center.z),
                        new Vector3(b.size.x + 0.24f, b.max.y + 0.024f, b.size.z + 0.12f));
                }
            }
            return true;
        }

        private void UpdateMoldWrapProgress(Vector3 hookPos)
        {
            if (!TryGetMoldExteriorBounds(out Bounds moldB))
            {
                hasWrappedOverMold = hookPos.z > -5.20f;
                return;
            }

            // Clear wrap if pulled laterally outside mold ends or returned low to operator (-Z) side
            if (hookPos.x < moldB.min.x - 0.08f || hookPos.x > moldB.max.x + 0.08f ||
                (hookPos.z < moldB.min.z - 0.02f && hookPos.y < moldB.max.y - 0.08f))
            {
                hasWrappedOverMold = false;
                return;
            }

            // Set wrap once lifted over mold top or reaching the +Z securing side
            if ((hookPos.y >= moldB.max.y - 0.08f && hookPos.z > moldB.min.z + 0.06f) ||
                hookPos.z >= moldB.max.z - 0.02f)
            {
                hasWrappedOverMold = true;
            }
        }

        // ------------------------------------------------------------------
        // Shared Convex Hull & Spool Tangent Helpers
        // ------------------------------------------------------------------

        internal static Vector2 ComputeUpperSpoolTangent(Vector2 spoolCenter, float spoolRadius, Vector2 targetZY)
        {
            Vector2 vToTarget = targetZY - spoolCenter;
            float dist = vToTarget.magnitude;
            if (dist <= spoolRadius + 0.001f)
                return spoolCenter + Vector2.up * spoolRadius;

            float alpha = Mathf.Atan2(vToTarget.y, vToTarget.x);
            float beta = Mathf.Acos(Mathf.Clamp(spoolRadius / dist, -1f, 1f));
            float theta = alpha + beta;
            return new Vector2(
                spoolCenter.x + spoolRadius * Mathf.Cos(theta),
                spoolCenter.y + spoolRadius * Mathf.Sin(theta));
        }

        internal static void ComputeUpperConvexHull(Vector2 startPt, Vector2 endPt, List<Vector2> obstacles, List<Vector2> outHull)
        {
            List<Vector2> pts = new List<Vector2>(obstacles.Count + 2) { startPt };
            for (int i = 0; i < obstacles.Count; i++)
            {
                Vector2 p = obstacles[i];
                if (p.x >= startPt.x - 0.001f && p.x <= endPt.x + 0.001f)
                    pts.Add(p);
            }
            pts.Add(endPt);

            pts.Sort((a, b) =>
            {
                int cmpX = a.x.CompareTo(b.x);
                return cmpX != 0 ? cmpX : a.y.CompareTo(b.y);
            });

            outHull.Clear();
            for (int i = 0; i < pts.Count; i++)
            {
                Vector2 p = pts[i];
                while (outHull.Count >= 2)
                {
                    Vector2 a = outHull[outHull.Count - 2];
                    Vector2 b = outHull[outHull.Count - 1];
                    float cross = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
                    if (cross >= -0.00001f)
                        outHull.RemoveAt(outHull.Count - 1);
                    else
                        break;
                }
                outHull.Add(p);
            }
        }

        internal static List<Vector2> BuildFilletedHullProfile(List<Vector2> hull, float maxCornerTrim)
        {
            if (hull.Count <= 2)
                return new List<Vector2>(hull);

            List<Vector2> output = new List<Vector2>(hull.Count * 5) { hull[0] };
            const int arcSubdivisions = 4;

            for (int i = 1; i < hull.Count - 1; i++)
            {
                Vector2 prev = hull[i - 1];
                Vector2 corner = hull[i];
                Vector2 next = hull[i + 1];

                Vector2 dIn = corner - prev;
                Vector2 dOut = next - corner;
                float lenIn = dIn.magnitude;
                float lenOut = dOut.magnitude;

                if (lenIn < 0.006f || lenOut < 0.006f)
                {
                    output.Add(corner);
                    continue;
                }

                Vector2 uIn = dIn / lenIn;
                Vector2 uOut = dOut / lenOut;
                if (Vector2.Angle(uIn, uOut) < 4.0f)
                {
                    output.Add(corner);
                    continue;
                }

                float trim = Mathf.Min(maxCornerTrim, Mathf.Min(lenIn, lenOut) * 0.28f);
                Vector2 nIn = new Vector2(-uIn.y, uIn.x);
                Vector2 nOut = new Vector2(-uOut.y, uOut.x);
                Vector2 outwardBisector = (nIn + nOut);
                if (outwardBisector.sqrMagnitude > 0.0001f) outwardBisector.Normalize();
                else outwardBisector = Vector2.up;

                Vector2 apex = corner + outwardBisector * 0.0018f;
                Vector2 pA = corner - uIn * trim + nIn * 0.0012f;
                Vector2 pB = corner + uOut * trim + nOut * 0.0012f;

                for (int s = 0; s <= arcSubdivisions; s++)
                {
                    float t = (float)s / arcSubdivisions;
                    float omt = 1f - t;
                    output.Add(omt * omt * pA + 2f * omt * t * apex + t * t * pB);
                }
            }

            output.Add(hull[hull.Count - 1]);
            return output;
        }

        // ------------------------------------------------------------------
        // Hook Ownership, Alignment Feedback & Attachment Validation
        // ------------------------------------------------------------------

        public bool CanAttachAdjustableToHook(PlatformSideHook targetHook, bool checkProximityAndWrap, out string rejectionReason)
        {
            rejectionReason = string.Empty;
            if (targetHook == null || mechanicsController == null || mechanicsController.adjustableHookTransform == null)
            {
                rejectionReason = "Target side hook missing.";
                return false;
            }

            targetHook.AutoInferBeltIndex();
            int myBeltIdx = mechanicsController.beltIndex;

            if (targetHook.assignedBeltIndex != myBeltIdx)
            {
                rejectionReason = $"Wrong belt hook! Belt #{myBeltIdx} must attach only to Belt #{myBeltIdx}'s side hook (Near_{myBeltIdx}), not {targetHook.name}.";
                return false;
            }

            if (!targetHook.IsNearSide || (platformHookRight != null && targetHook != platformHookRight))
            {
                rejectionReason = $"Cannot attach free end to fixed-side anchor ({targetHook.name}). Pull strap across mold to Near_{myBeltIdx}.";
                return false;
            }

            if (targetHook.IsOccupied && targetHook.AttachedFitting != mechanicsController.adjustableHookTransform)
            {
                rejectionReason = $"{targetHook.name} is already occupied.";
                return false;
            }

            if (checkProximityAndWrap)
            {
                float dist = Vector3.Distance(mechanicsController.adjustableHookTransform.position, targetHook.AnchorWorldPosition);
                if (dist > attachmentProximityThreshold)
                {
                    rejectionReason = $"Align J-Hook within {attachmentProximityThreshold:F2}m of {targetHook.name} (currently {dist:F2}m away).";
                    return false;
                }
            }

            return true;
        }

        private PlatformSideHook FindNearbyWrongHook(Vector3 hookWorldPos, out float closestDist)
        {
            closestDist = float.MaxValue;
            PlatformSideHook found = null;
            if (_allSceneHooks == null || _allSceneHooks.Length == 0)
                _allSceneHooks = FindObjectsByType<PlatformSideHook>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            for (int i = 0; i < _allSceneHooks.Length; i++)
            {
                PlatformSideHook h = _allSceneHooks[i];
                if (h == null || h == platformHookRight) continue;
                h.AutoInferBeltIndex();

                float d = Vector3.Distance(hookWorldPos, h.AnchorWorldPosition);
                if (d <= 0.22f && d < closestDist)
                {
                    if (h == platformHookLeft && d > 0.14f) continue;
                    closestDist = d;
                    found = h;
                }
            }
            return found;
        }

        public PlatformSideHook.AlignmentFeedback UpdateAlignmentFeedbackWhileHeld(Vector3 hookWorldPos, out PlatformSideHook activeFeedbackHook)
        {
            activeFeedbackHook = null;
            if (platformHookRight == null)
                FindPlatformHooks();

            float validDist = platformHookRight != null
                ? Vector3.Distance(hookWorldPos, platformHookRight.AnchorWorldPosition)
                : float.MaxValue;
            PlatformSideHook wrong = FindNearbyWrongHook(hookWorldPos, out float wrongDist);

            if (wrong != null && wrongDist < validDist)
            {
                if (platformHookRight != null)
                    platformHookRight.SetAlignmentFeedback(PlatformSideHook.AlignmentFeedback.None);
                if (_highlightedWrongHook != null && _highlightedWrongHook != wrong)
                    _highlightedWrongHook.SetAlignmentFeedback(PlatformSideHook.AlignmentFeedback.None);

                _highlightedWrongHook = wrong;
                wrong.SetAlignmentFeedback(PlatformSideHook.AlignmentFeedback.WrongHookRejected);
                activeFeedbackHook = wrong;
                return PlatformSideHook.AlignmentFeedback.WrongHookRejected;
            }

            if (_highlightedWrongHook != null)
            {
                _highlightedWrongHook.SetAlignmentFeedback(PlatformSideHook.AlignmentFeedback.None);
                _highlightedWrongHook = null;
            }

            if (platformHookRight != null)
            {
                if (validDist <= attachmentProximityThreshold)
                {
                    platformHookRight.SetAlignmentFeedback(PlatformSideHook.AlignmentFeedback.ValidAligned);
                    activeFeedbackHook = platformHookRight;
                    return PlatformSideHook.AlignmentFeedback.ValidAligned;
                }
                else
                {
                    platformHookRight.SetAlignmentFeedback(PlatformSideHook.AlignmentFeedback.None);
                }
            }

            return PlatformSideHook.AlignmentFeedback.None;
        }

        public void ClearAllHookHighlights()
        {
            if (platformHookRight != null)
                platformHookRight.SetAlignmentFeedback(PlatformSideHook.AlignmentFeedback.None);
            if (_highlightedWrongHook != null)
            {
                _highlightedWrongHook.SetAlignmentFeedback(PlatformSideHook.AlignmentFeedback.None);
                _highlightedWrongHook = null;
            }
        }

        public bool AttachFixedHook(PlatformSideHook targetHook)
        {
            if (targetHook == null || mechanicsController == null || mechanicsController.fixedHookTransform == null)
                return false;

            targetHook.AutoInferBeltIndex();
            if (targetHook.assignedBeltIndex != mechanicsController.beltIndex || !targetHook.IsFarSide)
            {
                string msg = $"Wrong Hook: Fixed end of Belt #{mechanicsController.beltIndex} only attaches to Far_{mechanicsController.beltIndex}!";
                mechanicsController.PublishUserNotification(msg);
                Debug.LogWarning($"[PlatformHookAttachment] {msg}");
                return false;
            }

            if (fixedHookRestPos == Vector3.zero)
                CacheRestTransforms();

            fixedAttachedTo = targetHook;
            targetHook.SetOccupied(mechanicsController.fixedHookTransform);
            isFixedHookAttached = true;

            mechanicsController.fixedHookTransform.SetPositionAndRotation(
                targetHook.AnchorWorldPosition,
                targetHook.AnchorWorldRotation);

            SyncAllStitchVisuals();
            Debug.Log($"[PlatformHookAttachment] Belt #{mechanicsController.beltIndex} Fixed J-Hook attached to {targetHook.name}.");
            return true;
        }

        public bool AttachAdjustableHook(PlatformSideHook targetHook)
        {
            if (!CanAttachAdjustableToHook(targetHook, checkProximityAndWrap: false, out string reason))
            {
                if (mechanicsController != null)
                    mechanicsController.PublishUserNotification(reason);
                Debug.LogWarning($"[PlatformHookAttachment] AttachAdjustableHook rejected: {reason}");
                return false;
            }

            if (adjHookRestPos == Vector3.zero)
                CacheRestTransforms();

            ClearAllHookHighlights();
            isFreeEndGrabbed = false;
            isSettlingToSurface = false;
            hasWrappedOverMold = true;

            adjAttachedTo = targetHook;
            targetHook.SetOccupied(mechanicsController.adjustableHookTransform);
            isAdjHookAttached = true;

            mechanicsController.adjustableHookTransform.SetPositionAndRotation(
                targetHook.AnchorWorldPosition,
                targetHook.AnchorWorldRotation);

            UpdateBuckleAssemblyPosture();
            mechanicsController.NotifyAdjustableHookAttached();
            if (ribbonRenderer != null)
                ribbonRenderer.UpdateRibbonGeometry();
            SyncAllStitchVisuals();

            Debug.Log($"[PlatformHookAttachment] Belt #{mechanicsController.beltIndex} Adjustable J-Hook attached to {targetHook.name} at {targetHook.AnchorWorldPosition:F3}.");
            return true;
        }

        public bool IsWithinAttachmentRange(out float currentDistance)
        {
            currentDistance = float.MaxValue;
            if (platformHookRight == null) FindPlatformHooks();
            if (platformHookRight == null || mechanicsController == null || mechanicsController.adjustableHookTransform == null)
                return false;

            currentDistance = Vector3.Distance(mechanicsController.adjustableHookTransform.position, platformHookRight.AnchorWorldPosition);
            return currentDistance <= attachmentProximityThreshold;
        }

        public void SetAdjustableHookDragPosition(Vector3 worldPos)
        {
            UpdateGrabbedFreeEndPose(worldPos, null);
        }

        public bool TryAttachAdjustableHookFromDrag(out string resultMessage)
        {
            return CompleteFreeEndRelease(out resultMessage);
        }

        public void DetachFixedHook()
        {
            if (!isFixedHookAttached) return;
            if (mechanicsController != null && mechanicsController.CompletedStrokes > 0)
            {
                mechanicsController.PublishUserNotification("Release ratchet tension first before detaching Fixed J-Hook!");
                return;
            }

            if (fixedAttachedTo != null)
            {
                fixedAttachedTo.SetOccupied(null);
                fixedAttachedTo = null;
            }

            isFixedHookAttached = false;

            if (mechanicsController != null && mechanicsController.fixedHookTransform != null && fixedHookRestPos != Vector3.zero)
            {
                mechanicsController.fixedHookTransform.SetPositionAndRotation(fixedHookRestPos, fixedHookRestRot);
            }

            SyncAllStitchVisuals();
            Debug.Log("[PlatformHookAttachment] Fixed J-Hook detached from vibrating platform.");
        }

        public void DetachAdjustableHook()
        {
            DetachAdjustableHook(snapToRestPosition: false);
        }

        public bool DetachAdjustableHook(bool snapToRestPosition)
        {
            if (!isAdjHookAttached)
            {
                if (snapToRestPosition && mechanicsController != null && mechanicsController.adjustableHookTransform != null)
                {
                    hasWrappedOverMold = false;
                    unattachedRestingWorldPos = ConstrainFreeEndWorldPosition(adjHookRestPos);
                    unattachedRestingWorldRot = ComputeNaturalFreeHookRotation(unattachedRestingWorldPos);
                    isSettlingToSurface = false;
                    mechanicsController.adjustableHookTransform.SetPositionAndRotation(unattachedRestingWorldPos, unattachedRestingWorldRot);
                    UpdateBuckleAssemblyPosture();
                    if (ribbonRenderer != null) ribbonRenderer.UpdateRibbonGeometry();
                    SyncAllStitchVisuals();
                }
                return true;
            }

            if (!CanDetachAdjustableHook(out string blockReason))
            {
                if (mechanicsController != null)
                    mechanicsController.PublishUserNotification(blockReason);
                Debug.LogWarning($"[PlatformHookAttachment] {blockReason}");
                return false;
            }

            Vector3 detachedWorldPos = adjAttachedTo != null
                ? adjAttachedTo.AnchorWorldPosition + new Vector3(0f, 0.035f, 0.040f)
                : adjHookRestPos;

            if (adjAttachedTo != null)
            {
                adjAttachedTo.SetOccupied(null);
                adjAttachedTo = null;
            }

            isAdjHookAttached = false;

            if (mechanicsController != null)
            {
                mechanicsController.NotifyAdjustableHookDetached();
            }

            if (snapToRestPosition && adjHookRestPos != Vector3.zero)
            {
                hasWrappedOverMold = false;
                unattachedRestingWorldPos = ConstrainFreeEndWorldPosition(adjHookRestPos);
                unattachedRestingWorldRot = ComputeNaturalFreeHookRotation(unattachedRestingWorldPos);
                isSettlingToSurface = false;
            }
            else
            {
                unattachedRestingWorldPos = ConstrainFreeEndWorldPosition(detachedWorldPos);
                unattachedRestingWorldRot = ComputeNaturalFreeHookRotation(unattachedRestingWorldPos);
                isSettlingToSurface = true;
            }

            if (mechanicsController != null && mechanicsController.adjustableHookTransform != null)
            {
                mechanicsController.adjustableHookTransform.SetPositionAndRotation(unattachedRestingWorldPos, unattachedRestingWorldRot);
                UpdateBuckleAssemblyPosture();
                if (ribbonRenderer != null) ribbonRenderer.UpdateRibbonGeometry();
                SyncAllStitchVisuals();
            }

            Debug.Log($"[PlatformHookAttachment] Belt #{(mechanicsController != null ? mechanicsController.beltIndex : 1)} Adjustable J-Hook detached.");
            return true;
        }

        public void ReturnFreeEndToOperatorSideRest()
        {
            if (isAdjHookAttached)
            {
                if (!DetachAdjustableHook(snapToRestPosition: true))
                    return;
            }

            if (adjHookRestPos == Vector3.zero)
                CacheRestTransforms();

            isFreeEndGrabbed = false;
            isSettlingToSurface = false;
            hasWrappedOverMold = false;
            ClearAllHookHighlights();

            // Update buckle to floor posture first so ConstrainFreeEndWorldPosition measures from the floor mandrel position
            if (mechanicsController != null && mechanicsController.adjustableHookTransform != null)
                mechanicsController.adjustableHookTransform.position = adjHookRestPos;
            UpdateBuckleAssemblyPosture();

            unattachedRestingWorldPos = ConstrainFreeEndWorldPosition(adjHookRestPos);
            unattachedRestingWorldRot = ComputeNaturalFreeHookRotation(unattachedRestingWorldPos);

            if (mechanicsController != null && mechanicsController.adjustableHookTransform != null)
            {
                mechanicsController.adjustableHookTransform.SetPositionAndRotation(unattachedRestingWorldPos, unattachedRestingWorldRot);
                UpdateBuckleAssemblyPosture();
                if (ribbonRenderer != null) ribbonRenderer.UpdateRibbonGeometry();
                SyncAllStitchVisuals();
            }
        }

        public void ToggleFixedHook()
        {
            if (isFixedHookAttached) DetachFixedHook();
            else if (platformHookLeft != null) AttachFixedHook(platformHookLeft);
        }

        public void ToggleAdjustableHook()
        {
            if (isAdjHookAttached)
            {
                DetachAdjustableHook(snapToRestPosition: false);
            }
            else if (platformHookRight != null && IsWithinAttachmentRange(out _))
            {
                AttachAdjustableHook(platformHookRight);
            }
        }
    }
}
