using System.Collections.Generic;
using UnityEngine;

namespace Sefpro.RatchetBelt
{
    /// <summary>
    /// Generates and dynamically updates a physically realistic, constant-length, non-penetrating
    /// 50 mm × 2.4 mm industrial polyester webbing strap for each RatchetBelt assembly:
    /// - Strictly preserves the belt's physical unspooled webbing length within [minAdjustable, maxAdjustable]
    ///   at all times (zero rubber-band stretching, zero transform scaling, zero width/thickness distortion).
    /// - Keeps the anchored/wrapped route stationary on MoldCast while only the free section responds when grabbed.
    /// - Uses parallel-transport frame extrusion so lateral (±X) pulls never twist or collapse the 50 mm width.
    /// - Pulls progressively taut across PullSlack and 4 ratchet strokes down to exact minimum wrapped length.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(100)]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class RatchetWebbingRibbonRenderer : MonoBehaviour
    {
        private struct SliceObstacleRect
        {
            public float minZ;
            public float maxZ;
            public float maxY;
        }

        [Header("Key Anchors & References")]
        public Transform mandrelExitTransform;
        public Transform adjustableHookTransform;
        public Transform moldTransform;
        public Transform platformDeckTransform;
        public RatchetBeltMechanicsController mechanicsController;
        public PlatformHookAttachment hookAttachment;

        [Header("Physical Strap Dimensions")]
        [Tooltip("Webbing strap width in meters (50 mm = 0.050 m).")]
        public float strapWidth = 0.050f;

        [Tooltip("Webbing strap thickness in meters (2.4 mm = 0.0024 m).")]
        public float strapThickness = 0.0024f;

        [Header("Live Mold Geometry Bounds (World Space Readout)")]
        public float centerX = -22.540f;
        public float moldNearZ = -5.786f;
        public float moldFarZ = -5.134f;
        public float moldTopY = 0.8533f;
        public float moldBottomY = 0.0962f;

        [Header("Slack & Tail Visuals")]
        [Tooltip("Render the loose webbing tail exiting the mandrel spindle.")]
        public bool renderLooseTail = true;

        [Header("Measured Webbing Telemetry (Read Only)")]
        [SerializeField] private float measuredMainWebbingLengthMeters;

        private Mesh mesh;
        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;

        private readonly List<SliceObstacleRect> _sliceObstacles = new List<SliceObstacleRect>(32);
        private readonly List<Vector2> _hullInput = new List<Vector2>(64);
        private readonly List<Vector2> _upperHull = new List<Vector2>(32);
        private readonly List<Vector3> _mainWaypoints = new List<Vector3>(64);
        private readonly List<Vector3> _evalBuffer = new List<Vector3>(64);
        private readonly List<Vector3> _verts = new List<Vector3>(320);
        private readonly List<Vector3> _norms = new List<Vector3>(320);
        private readonly List<Vector2> _uvs = new List<Vector2>(320);
        private readonly List<int> _tris = new List<int>(960);

        private Vector3 _prevPStart;
        private Vector3 _prevPEnd;
        private Vector3 _clothInertiaOffset;
        private Vector3 _clothInertiaVel;
        private float _clothWavePhase;
        private bool _hasPrevEndpoints;
        private int _lastClothSimFrame = -1;

        /// <summary>
        /// Exact measured 3D polyline arc-length of the extruded adjustable webbing (meters).
        /// </summary>
        public float MeasuredMainWebbingLengthMeters => measuredMainWebbingLengthMeters;

        /// <summary>
        /// Unit world-space tangent direction arriving at the Adjustable J-Hook eyelet.
        /// </summary>
        public Vector3 EndArrivalTangent { get; private set; } = Vector3.forward;

        /// <summary>
        /// Unit world-space binormal (width axis) arriving at the Adjustable J-Hook eyelet.
        /// </summary>
        public Vector3 EndArrivalBinormal { get; private set; } = Vector3.left;

        private void Awake()
        {
            EnsureMeshInitialized();
        }

        private void OnEnable()
        {
            EnsureMeshInitialized();
        }

        private void EnsureMeshInitialized()
        {
            if (transform.localScale != Vector3.one)
            {
                transform.localScale = Vector3.one;
            }

            if (meshFilter == null)
                meshFilter = GetComponent<MeshFilter>();
            if (meshRenderer == null)
                meshRenderer = GetComponent<MeshRenderer>();

            if (mesh == null)
            {
                mesh = new Mesh { name = "Dynamic_Mould_Locking_Webbing_Ribbon" };
                mesh.MarkDynamic();
            }

            if (meshFilter != null && meshFilter.sharedMesh != mesh)
            {
                meshFilter.sharedMesh = mesh;
            }
        }

        private void LateUpdate()
        {
            UpdateRibbonGeometry();
        }

        /// <summary>
        /// Rebuilds the 3D flat webbing ribbon mesh from authoritative mechanical, hook, and mold state.
        /// </summary>
        public void UpdateRibbonGeometry()
        {
            EnsureMeshInitialized();

            // Never use transform scaling or alter physical cross-section
            strapWidth = 0.050f;
            strapThickness = 0.0024f;

            if (mandrelExitTransform == null || adjustableHookTransform == null || mechanicsController == null || moldTransform == null)
            {
                AutoFindReferences();
                if (mandrelExitTransform == null || adjustableHookTransform == null)
                    return;
            }

            if (hookAttachment == null && mechanicsController != null)
            {
                hookAttachment = mechanicsController.hookAttachment != null
                    ? mechanicsController.hookAttachment
                    : mechanicsController.GetComponent<PlatformHookAttachment>();
            }

            float trackX = mechanicsController != null ? mechanicsController.transform.position.x : transform.position.x;

            Transform hookEyelet = adjustableHookTransform.Find("Anchor_AdjustableHook");
            Vector3 pHook = hookEyelet != null ? hookEyelet.position : adjustableHookTransform.position;

            int strokes = 4;
            bool isAdjHookAttached = true;
            float spoolRadius = 0.0100f;

            if (mechanicsController != null)
            {
                strokes = mechanicsController.CompletedStrokes;
                isAdjHookAttached = mechanicsController.IsAdjHookAttached;
                spoolRadius = mechanicsController.CurrentSpoolRadius;
            }
            else
            {
                spoolRadius = Mathf.Lerp(0.0100f, 0.0144f, strokes / 4.0f);
            }

            if (isAdjHookAttached && Mathf.Abs(pHook.x - trackX) < 0.02f)
            {
                pHook.x = trackX;
            }

            float mandrelCenterZ = -6.0197f;
            float mandrelCenterY = 0.3891f;
            if (mandrelExitTransform != null && !mandrelExitTransform.name.Contains("Anchor"))
            {
                mandrelCenterZ = mandrelExitTransform.position.z;
                mandrelCenterY = mandrelExitTransform.position.y;
                trackX = mandrelExitTransform.position.x;
            }

            float targetArcLength = hookAttachment != null
                ? hookAttachment.CurrentActiveAdjustableStrapLengthMeters
                : 2.020f;

            float halfWidth = strapWidth * 0.5f;
            float halfThick = strapThickness * 0.5f;
            float surfaceClearance = halfThick + 0.0028f; // 4.0 mm centerline clearance

            bool hasMold = CollectStationaryMoldSlices(trackX, halfWidth + 0.003f, surfaceClearance, out Bounds fullMoldBounds);

            float deckTopY = GetPlatformDeckTopY() + halfThick + 0.0020f;
            float floorMinY = halfThick + 0.0025f;

            bool moldOnPlatform = hookAttachment != null
                ? hookAttachment.IsMoldCurrentlyOnPlatform()
                : (_sliceObstacles.Count > 0 && Mathf.Abs(centerX - (-22.54f)) < 0.85f);

            bool routeOverMold = hasMold && moldOnPlatform && _sliceObstacles.Count > 0 &&
                (hookAttachment != null
                    ? hookAttachment.ShouldRouteOverMold(adjustableHookTransform.position)
                    : (isAdjHookAttached || pHook.z > moldNearZ + 0.03f));

            if (isAdjHookAttached)
            {
                _clothInertiaOffset = Vector3.zero;
                _clothInertiaVel = Vector3.zero;
                _hasPrevEndpoints = false;
            }

            BuildConstantLengthMainWaypoints(
                trackX,
                mandrelCenterY,
                mandrelCenterZ,
                spoolRadius,
                pHook,
                targetArcLength,
                hasMold,
                fullMoldBounds,
                routeOverMold,
                isAdjHookAttached,
                surfaceClearance,
                deckTopY,
                floorMinY,
                _mainWaypoints);

            List<Vector3> tailWaypoints = null;
            if (renderLooseTail)
            {
                tailWaypoints = BuildLooseTailWaypoints(trackX, mandrelCenterY, mandrelCenterZ, strokes);
            }

            BuildExtrudedRibbonMesh(_mainWaypoints, tailWaypoints);
        }

        private void BuildConstantLengthMainWaypoints(
            float trackX,
            float mandrelY,
            float mandrelZ,
            float spoolRadius,
            Vector3 pHook,
            float targetArcLength,
            bool hasMold,
            Bounds fullMoldBounds,
            bool routeOverMold,
            bool isAdjHookAttached,
            float surfaceClearance,
            float deckTopY,
            float floorMinY,
            List<Vector3> outWaypoints)
        {
            outWaypoints.Clear();
            Vector2 spoolCenter = new Vector2(mandrelZ, mandrelY);

            if (routeOverMold)
            {
                float maxRouteZ = Mathf.Max(pHook.z, moldNearZ + 0.02f);
                _hullInput.Clear();

                for (int i = 0; i < _sliceObstacles.Count; i++)
                {
                    SliceObstacleRect r = _sliceObstacles[i];
                    if (r.minZ <= maxRouteZ + 0.005f && r.minZ >= mandrelZ + 0.01f)
                        _hullInput.Add(new Vector2(r.minZ, r.maxY));
                    if (r.maxZ <= maxRouteZ + 0.005f && r.maxZ >= mandrelZ + 0.01f)
                        _hullInput.Add(new Vector2(r.maxZ, r.maxY));
                    if (pHook.z > r.minZ && pHook.z < r.maxZ)
                        _hullInput.Add(new Vector2(pHook.z, Mathf.Max(r.maxY, pHook.y)));
                }

                Vector2 approxFirstTarget = _hullInput.Count > 0 ? _hullInput[0] : new Vector2(pHook.z, pHook.y);
                for (int i = 1; i < _hullInput.Count; i++)
                {
                    if (_hullInput[i].y > approxFirstTarget.y)
                        approxFirstTarget = _hullInput[i];
                }

                Vector2 pStart2D = PlatformHookAttachment.ComputeUpperSpoolTangent(spoolCenter, spoolRadius, approxFirstTarget);
                Vector2 pEnd2D = new Vector2(Mathf.Max(pHook.z, mandrelZ + 0.025f), pHook.y);
                PlatformHookAttachment.ComputeUpperConvexHull(pStart2D, pEnd2D, _hullInput, _upperHull);

                if (_upperHull.Count >= 2)
                {
                    pStart2D = PlatformHookAttachment.ComputeUpperSpoolTangent(spoolCenter, spoolRadius, _upperHull[1]);
                    _upperHull[0] = pStart2D;
                }

                if (isAdjHookAttached)
                {
                    // Both ends anchored on the belt plane: solve outward bow strictly from remaining ratchet stroke slack
                    // (not live vibration displacement) so the tightened belt stays 100% taut and never bows during vibration.
                    List<Vector2> smoothProfile = PlatformHookAttachment.BuildFilletedHullProfile(_upperHull, 0.016f);
                    float minLen = EvaluateAttachedWrappedProfile(smoothProfile, trackX, pHook, 0f, hasMold, fullMoldBounds, surfaceClearance, deckTopY, floorMinY, _evalBuffer);

                    float remainingStrokeSlack = 0f;
                    if (mechanicsController != null && !mechanicsController.IsFullyTightened)
                    {
                        float maxShortening = RatchetBeltMechanicsController.MAX_TIGHTEN_STROKES * mechanicsController.shorteningPerTooth;
                        remainingStrokeSlack = Mathf.Max(0f, maxShortening - mechanicsController.TargetShortening);
                    }

                    float desiredAttachedLength = minLen + remainingStrokeSlack;
                    float bestBow = 0f;
                    if (desiredAttachedLength > minLen + 0.0004f)
                    {
                        float lo = 0f;
                        float hi = 0.28f;
                        for (int iter = 0; iter < 14; iter++)
                        {
                            float mid = 0.5f * (lo + hi);
                            float len = EvaluateAttachedWrappedProfile(smoothProfile, trackX, pHook, mid, hasMold, fullMoldBounds, surfaceClearance, deckTopY, floorMinY, _evalBuffer);
                            if (len < desiredAttachedLength) lo = mid;
                            else hi = mid;
                        }
                        bestBow = 0.5f * (lo + hi);
                    }

                    EvaluateAttachedWrappedProfile(smoothProfile, trackX, pHook, bestBow, hasMold, fullMoldBounds, surfaceClearance, deckTopY, floorMinY, outWaypoints);
                    return;
                }

                // Unattached & routing over MoldCast:
                // Keep the wrapped prefix up to the last MoldCast contact corner stationary at X = trackX,
                // and solve the free span (lastContact -> pHook) so total arc-length == targetArcLength!
                if (_upperHull.Count >= 3)
                {
                    List<Vector2> prefixHull = new List<Vector2>(_upperHull.Count - 1);
                    for (int i = 0; i < _upperHull.Count - 1; i++)
                        prefixHull.Add(_upperHull[i]);

                    List<Vector2> filletedPrefix = PlatformHookAttachment.BuildFilletedHullProfile(prefixHull, 0.016f);
                    float prefixLen = 0f;
                    for (int i = 0; i < filletedPrefix.Count; i++)
                    {
                        Vector3 wp = new Vector3(trackX, filletedPrefix[i].y, filletedPrefix[i].x);
                        if (i > 0) prefixLen += Vector3.Distance(wp, outWaypoints[outWaypoints.Count - 1]);
                        outWaypoints.Add(wp);
                    }

                    Vector3 freeStart = outWaypoints[outWaypoints.Count - 1];
                    float remainingBudget = Mathf.Max(Vector3.Distance(freeStart, pHook), targetArcLength - prefixLen);
                    float outwardZSign = pHook.z >= moldFarZ ? +1f : -1f;

                    StepClothDynamics(freeStart, pHook, false);
                    SolveConstantLengthFreeSpan(
                        freeStart,
                        pHook,
                        remainingBudget,
                        outwardZSign,
                        hasMold,
                        fullMoldBounds,
                        surfaceClearance,
                        deckTopY,
                        floorMinY,
                        _evalBuffer);

                    for (int i = 1; i < _evalBuffer.Count; i++)
                    {
                        outWaypoints.Add(_evalBuffer[i]);
                    }
                    return;
                }
            }

            // Unwrapped free span from Mandrel upper spool tangent to pHook:
            // Solve exact slack/drape parameter so 3D polyline arc-length == targetArcLength at all times!
            Vector2 hookZY = new Vector2(pHook.z, pHook.y);
            Vector2 startUnwrapped2D = PlatformHookAttachment.ComputeUpperSpoolTangent(spoolCenter, spoolRadius, hookZY);
            Vector3 pStart = new Vector3(trackX, startUnwrapped2D.y, startUnwrapped2D.x);

            StepClothDynamics(pStart, pHook, false);
            SolveConstantLengthFreeSpan(
                pStart,
                pHook,
                targetArcLength,
                -1f, // Drape excess cleanly onto operator (-Z) side
                hasMold,
                fullMoldBounds,
                surfaceClearance,
                deckTopY,
                floorMinY,
                outWaypoints);
        }

        private float EvaluateAttachedWrappedProfile(
            List<Vector2> smoothProfile,
            float trackX,
            Vector3 pHook,
            float bowAmplitude,
            bool hasMold,
            Bounds fullMoldBounds,
            float surfaceClearance,
            float deckTopY,
            float floorMinY,
            List<Vector3> outPts)
        {
            outPts.Clear();
            if (smoothProfile.Count == 0) return 0f;

            outPts.Add(new Vector3(trackX, smoothProfile[0].y, smoothProfile[0].x));
            float totalLen = 0f;

            for (int i = 1; i < smoothProfile.Count; i++)
            {
                Vector2 segA = smoothProfile[i - 1];
                Vector2 segB = smoothProfile[i];
                float segLen = Vector2.Distance(segB, segA);

                bool isFreeInclinedSpan = segLen > 0.16f && Mathf.Abs(segB.y - segA.y) > segLen * 0.18f;
                int subSteps = isFreeInclinedSpan ? 12 : (segLen > 0.14f ? 4 : 1);

                Vector2 segDir = segLen > 0.0001f ? (segB - segA) / segLen : Vector2.right;
                Vector2 outwardNormal = new Vector2(-segDir.y, segDir.x);
                if (outwardNormal.y < -0.2f) outwardNormal = -outwardNormal;

                for (int s = 1; s <= subSteps; s++)
                {
                    float localT = (float)s / subSteps;
                    Vector2 pt2 = Vector2.Lerp(segA, segB, localT);

                    if (isFreeInclinedSpan && bowAmplitude > 0.0001f && s < subSteps)
                    {
                        pt2 += outwardNormal * (bowAmplitude * Mathf.Sin(Mathf.PI * localT));
                    }

                    Vector3 wp = (s == subSteps && i == smoothProfile.Count - 1)
                        ? pHook
                        : PushPointOutsideEnvironment(new Vector3(trackX, pt2.y, pt2.x), hasMold, fullMoldBounds, surfaceClearance, deckTopY, floorMinY);

                    totalLen += Vector3.Distance(wp, outPts[outPts.Count - 1]);
                    outPts.Add(wp);
                }
            }

            return totalLen;
        }

        private void StepClothDynamics(Vector3 pStart, Vector3 pEnd, bool isAttached)
        {
            if (!Application.isPlaying || isAttached)
            {
                _clothInertiaOffset = Vector3.zero;
                _clothInertiaVel = Vector3.zero;
                _hasPrevEndpoints = false;
                return;
            }

            if (Time.frameCount == _lastClothSimFrame)
                return;
            _lastClothSimFrame = Time.frameCount;

            float dt = Mathf.Clamp(Time.deltaTime, 0.001f, 0.05f);
            Vector3 vStart = _hasPrevEndpoints ? (pStart - _prevPStart) / dt : Vector3.zero;
            Vector3 vEnd = _hasPrevEndpoints ? (pEnd - _prevPEnd) / dt : Vector3.zero;

            if (!_hasPrevEndpoints || vStart.sqrMagnitude > 400f || vEnd.sqrMagnitude > 400f)
            {
                _prevPStart = pStart;
                _prevPEnd = pEnd;
                _clothInertiaOffset = Vector3.zero;
                _clothInertiaVel = Vector3.zero;
                _hasPrevEndpoints = true;
                return;
            }

            _prevPStart = pStart;
            _prevPEnd = pEnd;

            Vector3 midVel = vStart * 0.30f + vEnd * 0.70f;
            Vector3 targetLag = Vector3.ClampMagnitude(-midVel * 0.055f, 0.14f);
            Vector3 accel = (targetLag - _clothInertiaOffset) * 28.0f - _clothInertiaVel * 6.0f;
            _clothInertiaVel += accel * dt;
            _clothInertiaOffset += _clothInertiaVel * dt;
            _clothInertiaOffset = Vector3.ClampMagnitude(_clothInertiaOffset, 0.15f);

            if (midVel.sqrMagnitude > 0.002f || _clothInertiaVel.sqrMagnitude > 0.002f)
                _clothWavePhase = Mathf.Repeat(_clothWavePhase + dt * (3.2f + midVel.magnitude * 2.5f), Mathf.PI * 2f);
        }

        private void SolveConstantLengthFreeSpan(
            Vector3 pStart,
            Vector3 pEnd,
            float targetSpanLength,
            float outwardZSign,
            bool hasMold,
            Bounds fullMoldBounds,
            float surfaceClearance,
            float deckTopY,
            float floorMinY,
            List<Vector3> outPts)
        {
            float baseLen = EvaluateFreeSpanCurve(pStart, pEnd, 0f, outwardZSign, hasMold, fullMoldBounds, surfaceClearance, deckTopY, floorMinY, outPts);
            if (targetSpanLength <= baseLen + 0.0005f)
            {
                return;
            }

            float lo = 0f;
            float hi = 4.00f;
            for (int iter = 0; iter < 18; iter++)
            {
                float mid = 0.5f * (lo + hi);
                float len = EvaluateFreeSpanCurve(pStart, pEnd, mid, outwardZSign, hasMold, fullMoldBounds, surfaceClearance, deckTopY, floorMinY, outPts);
                if (len < targetSpanLength) lo = mid;
                else hi = mid;
            }

            EvaluateFreeSpanCurve(pStart, pEnd, 0.5f * (lo + hi), outwardZSign, hasMold, fullMoldBounds, surfaceClearance, deckTopY, floorMinY, outPts);
        }

        private float EvaluateFreeSpanCurve(
            Vector3 pStart,
            Vector3 pEnd,
            float slackParam,
            float outwardZSign,
            bool hasMold,
            Bounds fullMoldBounds,
            float surfaceClearance,
            float deckTopY,
            float floorMinY,
            List<Vector3> outPts)
        {
            const int segments = 48;
            outPts.Clear();
            outPts.Add(pStart);

            float totalLen = 0f;
            float sagAmp = slackParam * 1.18f;
            float smoothBlend = Mathf.Clamp01(slackParam * 5.0f);
            float looseness = Mathf.Clamp01(slackParam * 3.0f + 0.25f);

            // Same lateral sign with distinct phase per belt so both belts drape organically without crossing each other
            float phase = (mechanicsController != null && mechanicsController.beltIndex == 2) ? 0.65f : 0.0f;

            // Shift catenary belly gently toward the lower endpoint (|asym| <= 0.25 < 1/pi guarantees dsZ/dt > 0 everywhere!)
            float heightDiff = pEnd.y - pStart.y;
            float asym = Mathf.Clamp(heightDiff * 0.32f, -0.25f, 0.25f) * Mathf.Clamp01(slackParam * 2.5f);

            for (int i = 1; i < segments; i++)
            {
                float t = (float)i / segments;
                float u = 2f * t - 1f;
                float parabola = 1f - u * u;
                float sineBell = Mathf.Sin(Mathf.PI * t);
                float catenaryBell = 0.60f * parabola + 0.40f * sineBell;

                float sZ = Mathf.Clamp01(t + asym * sineBell);
                float baseY = Mathf.Lerp(pStart.y, pEnd.y, sZ);
                float rawY = baseY - sagAmp * catenaryBell;

                float airFactor = Mathf.Clamp01((rawY - floorMinY + 0.06f) / 0.18f);
                float ripple = Mathf.Sin(Mathf.PI * 2f * t - _clothWavePhase) * 0.28f;
                Vector3 dynamicSway = Application.isPlaying
                    ? _clothInertiaOffset * (sineBell * looseness * (0.35f + 0.65f * airFactor) * (1f + ripple))
                    : Vector3.zero;

                rawY += dynamicSway.y;

                float groundExcess = Mathf.Max(0f, (floorMinY + 0.035f) - rawY);

                // Multi-ripple serpentine cloth relaxation on the floor (absorbs normal floor slack with zero -Z foldback!)
                float waveAmp = Mathf.Min(0.165f, groundExcess * 0.22f);
                float waveShape = 0.58f * Mathf.Sin(Mathf.PI * 2f * t + phase) +
                                  0.42f * Mathf.Sin(Mathf.PI * 5f * t + phase * 1.4f);
                float xGroundWave = waveAmp * waveShape * sineBell;

                // Only coil outward in Z when extreme slack exists (e.g. holding hook very close to buckle), paired with X separation
                float zCoilDrape = outwardZSign * Mathf.Max(0f, slackParam - 0.85f) * 0.75f * sineBell;

                float yFabricRipple = groundExcess > 0.01f
                    ? 0.0038f * Mathf.Abs(Mathf.Sin(Mathf.PI * 4f * t + phase)) * Mathf.Clamp01(groundExcess * 4f)
                    : 0f;

                float layeredFloorY = floorMinY + 0.004f * (1f - t) + yFabricRipple;

                // C-infinity tangential cloth peel off the floor (eliminates sharp floor corner kink)
                float dFloor = rawY - layeredFloorY;
                float smoothY = layeredFloorY + 0.5f * (dFloor + Mathf.Sqrt(dFloor * dFloor + 0.0020f));

                float latT = Mathf.Lerp(t, Mathf.SmoothStep(0f, 1f, t), smoothBlend);

                Vector3 pt = new Vector3(
                    Mathf.Lerp(pStart.x, pEnd.x, latT) + xGroundWave + dynamicSway.x,
                    smoothY,
                    Mathf.Lerp(pStart.z, pEnd.z, sZ) + zCoilDrape + dynamicSway.z);

                pt = PushPointOutsideEnvironment(pt, hasMold, fullMoldBounds, surfaceClearance, deckTopY, layeredFloorY);

                totalLen += Vector3.Distance(pt, outPts[outPts.Count - 1]);
                outPts.Add(pt);
            }

            totalLen += Vector3.Distance(pEnd, outPts[outPts.Count - 1]);
            outPts.Add(pEnd);
            return totalLen;
        }

        private List<Vector3> BuildLooseTailWaypoints(float trackX, float mandrelCenterY, float mandrelCenterZ, int strokes)
        {
            List<Vector3> tailWaypoints = new List<Vector3>(5);
            float progress = mechanicsController != null ? mechanicsController.CurrentProgress : (strokes / 4.0f);

            // Loose tail exits cleanly into open space beneath the mandrel (-10.0 mm per stroke), clamped above the factory floor
            const float baseTailProtrude = 0.0655f;
            float targetProtrude = Mathf.Max(0.018f, baseTailProtrude - progress * 0.0400f);
            const float floorTailMinY = 0.0040f;

            Vector3 pSlotTop = new Vector3(trackX, Mathf.Max(floorTailMinY + 0.004f, mandrelCenterY - 0.003f), mandrelCenterZ + 0.004f);
            Vector3 pSlotExit = new Vector3(trackX, Mathf.Max(floorTailMinY + 0.002f, mandrelCenterY - 0.011f), mandrelCenterZ + 0.012f);

            tailWaypoints.Add(pSlotTop);
            tailWaypoints.Add(pSlotExit);
            for (int k = 1; k <= 3; k++)
            {
                float s = targetProtrude * (k / 3.0f);
                float rawY = pSlotExit.y - s * 0.92f;
                float y = Mathf.Max(floorTailMinY, rawY);
                float extraZ = rawY < floorTailMinY ? (floorTailMinY - rawY) * 0.85f : 0f;
                float z = pSlotExit.z + Mathf.Sin((k / 3.0f) * 1.1f) * (targetProtrude * 0.24f) + extraZ;
                tailWaypoints.Add(new Vector3(trackX, y, z));
            }
            return tailWaypoints;
        }

        private bool CollectStationaryMoldSlices(
            float trackX,
            float halfBandWidth,
            float clearance,
            out Bounds fullMoldBounds)
        {
            _sliceObstacles.Clear();
            fullMoldBounds = default;

            if (moldTransform == null || !moldTransform.gameObject.activeInHierarchy)
                return false;

            bool hasFull = false;
            float minBandX = trackX - halfBandWidth;
            float maxBandX = trackX + halfBandWidth;

            Renderer[] rends = moldTransform.GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < rends.Length; i++)
            {
                Renderer r = rends[i];
                if (r == null || !r.enabled) continue;
                if (r.name == "Mold_Material_Volume") continue;

                MoldPart mp = r.GetComponent<MoldPart>();
                if (mp != null && mp.IsFree) continue;

                Bounds b = r.bounds;
                if (!hasFull)
                {
                    fullMoldBounds = b;
                    hasFull = true;
                }
                else
                {
                    fullMoldBounds.Encapsulate(b);
                }

                if (b.max.x >= minBandX && b.min.x <= maxBandX)
                {
                    _sliceObstacles.Add(new SliceObstacleRect
                    {
                        minZ = b.min.z - clearance,
                        maxZ = b.max.z + clearance,
                        maxY = b.max.y + clearance
                    });
                }
            }

            if (hasFull)
            {
                centerX = fullMoldBounds.center.x;
                moldNearZ = fullMoldBounds.min.z;
                moldFarZ = fullMoldBounds.max.z;
                moldTopY = fullMoldBounds.max.y;
                moldBottomY = fullMoldBounds.min.y;
            }

            return hasFull;
        }

        private Vector3 PushPointOutsideEnvironment(
            Vector3 pt,
            bool hasMold,
            Bounds moldBounds,
            float clearance,
            float deckTopY,
            float floorMinY)
        {
            if (pt.y < floorMinY)
                pt.y = floorMinY;

            // Vibrating platform rubber deck exclusion
            if (pt.x >= -24.65f && pt.x <= -20.43f && pt.z >= -6.02f && pt.z <= -4.90f)
            {
                if (pt.y < deckTopY)
                    pt.y = deckTopY;
            }

            if (hasMold)
            {
                float minX = moldBounds.min.x - clearance;
                float maxX = moldBounds.max.x + clearance;
                if (pt.x >= minX && pt.x <= maxX)
                {
                    for (int i = 0; i < _sliceObstacles.Count; i++)
                    {
                        SliceObstacleRect r = _sliceObstacles[i];
                        if (pt.z > r.minZ + 0.001f && pt.z < r.maxZ - 0.001f && pt.y < r.maxY)
                        {
                            float dTop = r.maxY - pt.y;
                            float dBack = pt.z - r.minZ;
                            float dFront = r.maxZ - pt.z;
                            if (pt.y > 0.45f && dBack > 0.025f && dFront > 0.025f) pt.y = r.maxY;
                            else if (dTop <= dBack && dTop <= dFront) pt.y = r.maxY;
                            else if (dBack < dFront) pt.z = r.minZ;
                            else pt.z = r.maxZ;
                        }
                    }

                    float beltPlaneX = mechanicsController != null ? mechanicsController.transform.position.x : transform.position.x;
                    if (Mathf.Abs(pt.x - beltPlaneX) > 0.035f)
                    {
                        float minZ = moldBounds.min.z - clearance;
                        float maxZ = moldBounds.max.z + clearance;
                        float topY = moldBounds.max.y + clearance;
                        if (pt.z > minZ && pt.z < maxZ && pt.y < topY)
                        {
                            float dTop = topY - pt.y;
                            float dBack = pt.z - minZ;
                            float dFront = maxZ - pt.z;
                            float dLeft = pt.x - minX;
                            float dRight = maxX - pt.x;
                            float m = Mathf.Min(dTop, Mathf.Min(Mathf.Min(dLeft, dRight), Mathf.Min(dBack, dFront)));
                            if (m == dTop) pt.y = topY;
                            else if (m == dBack) pt.z = minZ;
                            else if (m == dFront) pt.z = maxZ;
                            else if (m == dLeft) pt.x = minX;
                            else pt.x = maxX;
                        }
                    }
                }
            }

            return pt;
        }

        private float GetPlatformDeckTopY()
        {
            if (platformDeckTransform != null)
            {
                Renderer r = platformDeckTransform.GetComponent<Renderer>();
                if (r != null) return r.bounds.max.y;
            }
            return 0.0962f;
        }

        public void AutoFindReferences()
        {
            if (mechanicsController == null)
            {
                mechanicsController = GetComponentInParent<RatchetBeltMechanicsController>();
            }

            Transform searchRoot = mechanicsController != null
                ? mechanicsController.transform
                : (transform.parent != null ? transform.parent : transform);

            if (mandrelExitTransform == null)
            {
                Transform[] trs = searchRoot.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < trs.Length; i++)
                {
                    if (trs[i].name == "Mandrel")
                    {
                        mandrelExitTransform = trs[i];
                        break;
                    }
                }
            }

            if (adjustableHookTransform == null)
            {
                Transform[] trs = searchRoot.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < trs.Length; i++)
                {
                    if (trs[i].name == "J_Hook_Adjustable")
                    {
                        adjustableHookTransform = trs[i];
                        break;
                    }
                }
            }

            if (hookAttachment == null && mechanicsController != null)
            {
                hookAttachment = mechanicsController.GetComponent<PlatformHookAttachment>();
            }

            if (moldTransform == null || moldTransform.name == "CastingMold")
            {
                GameObject activeMold = GameObject.Find("MoldCasting/MoldCast");
                if (activeMold == null) activeMold = GameObject.Find("MoldCast");
                if (activeMold != null) moldTransform = activeMold.transform;
            }

            if (platformDeckTransform == null)
            {
                GameObject deck = GameObject.Find("VibratingPlatform (1)/Vibrating_Rubber_Surface");
                if (deck == null) deck = GameObject.Find("Vibrating_Rubber_Surface");
                if (deck != null) platformDeckTransform = deck.transform;
            }
        }

        private void BuildExtrudedRibbonMesh(List<Vector3> mainPoints, List<Vector3> tailPoints)
        {
            if (mainPoints == null || mainPoints.Count < 2) return;

            _verts.Clear();
            _norms.Clear();
            _uvs.Clear();
            _tris.Clear();

            measuredMainWebbingLengthMeters = ExtrudeRibbonStrip(
                mainPoints, _verts, _norms, _uvs, _tris, 0f, new Vector3(-1f, 0f, 0f), recordArrivalFrame: true);

            if (tailPoints != null && tailPoints.Count >= 2)
            {
                ExtrudeRibbonStrip(
                    tailPoints, _verts, _norms, _uvs, _tris, 5.0f, new Vector3(-1f, 0f, 0f), recordArrivalFrame: false);
            }

            mesh.Clear();
            mesh.SetVertices(_verts);
            mesh.SetNormals(_norms);
            mesh.SetUVs(0, _uvs);
            mesh.SetTriangles(_tris, 0);
            mesh.RecalculateBounds();
        }

        private float ExtrudeRibbonStrip(
            List<Vector3> points,
            List<Vector3> verts,
            List<Vector3> norms,
            List<Vector2> uvs,
            List<int> tris,
            float initialV,
            Vector3 initialBinormal,
            bool recordArrivalFrame)
        {
            List<Vector3> cleanPoints = new List<Vector3>(points.Count) { points[0] };
            for (int i = 1; i < points.Count; i++)
            {
                if (i == points.Count - 1 || Vector3.Distance(points[i], cleanPoints[cleanPoints.Count - 1]) > 0.0012f)
                {
                    cleanPoints.Add(points[i]);
                }
            }
            if (cleanPoints.Count < 2) return 0f;

            // Measure exact input polyline length before decimation so sub-millimeter folds are accounted accurately
            float rawArcLen = 0f;
            for (int i = 1; i < points.Count; i++)
                rawArcLen += Vector3.Distance(points[i], points[i - 1]);

            float halfWidth = strapWidth * 0.5f;
            float halfThick = strapThickness * 0.5f;
            float uvDist = initialV;

            int numPoints = cleanPoints.Count;
            int startVertexIndex = verts.Count;

            Vector3 prevTangent = (cleanPoints[1] - cleanPoints[0]).normalized;
            if (prevTangent.sqrMagnitude < 0.0001f) prevTangent = Vector3.forward;

            Vector3 prevBinormal = initialBinormal - prevTangent * Vector3.Dot(initialBinormal, prevTangent);
            if (prevBinormal.sqrMagnitude < 0.001f)
                prevBinormal = Vector3.Cross(prevTangent, Vector3.up);
            prevBinormal.Normalize();

            for (int i = 0; i < numPoints; i++)
            {
                Vector3 cur = cleanPoints[i];
                Vector3 tangent;

                if (i == 0)
                    tangent = (cleanPoints[1] - cur).normalized;
                else if (i == numPoints - 1)
                    tangent = (cur - cleanPoints[i - 1]).normalized;
                else
                    tangent = ((cur - cleanPoints[i - 1]).normalized + (cleanPoints[i + 1] - cur).normalized).normalized;

                if (tangent.sqrMagnitude < 0.0001f) tangent = prevTangent;
                else tangent.Normalize();

                // Parallel-transport previous binormal so ±X lateral pulls NEVER flip or twist the 50 mm strap
                Vector3 transported = Quaternion.FromToRotation(prevTangent, tangent) * prevBinormal;
                transported -= tangent * Vector3.Dot(transported, tangent);
                if (transported.sqrMagnitude < 0.001f)
                    transported = Vector3.Cross(tangent, Vector3.up);
                transported.Normalize();

                // Gently keep flat against (-1, 0, 0) when traveling primarily in the Y-Z plane
                float yzAlignment = 1f - Mathf.Abs(tangent.x);
                if (yzAlignment > 0.35f)
                {
                    Vector3 refBi = initialBinormal - tangent * Vector3.Dot(initialBinormal, tangent);
                    if (refBi.sqrMagnitude > 0.05f)
                    {
                        refBi.Normalize();
                        if (Vector3.Dot(refBi, transported) < 0f) refBi = -refBi;
                        transported = Vector3.Slerp(transported, refBi, yzAlignment * 0.35f).normalized;
                        transported = (transported - tangent * Vector3.Dot(transported, tangent)).normalized;
                    }
                }

                Vector3 binormal = transported;
                prevBinormal = binormal;
                prevTangent = tangent;

                if (recordArrivalFrame && i == numPoints - 1)
                {
                    EndArrivalTangent = tangent;
                    EndArrivalBinormal = binormal;
                }

                Vector3 worldNormal = Vector3.Cross(binormal, tangent).normalized;
                if (worldNormal.sqrMagnitude < 0.0001f) worldNormal = Vector3.up;

                if (i > 0)
                {
                    uvDist += Vector3.Distance(cleanPoints[i], cleanPoints[i - 1]);
                }
                float v = uvDist * 18.0f;

                Vector3 w0 = cur - binormal * halfWidth + worldNormal * halfThick;
                Vector3 w1 = cur + binormal * halfWidth + worldNormal * halfThick;
                Vector3 w2 = cur + binormal * halfWidth - worldNormal * halfThick;
                Vector3 w3 = cur - binormal * halfWidth - worldNormal * halfThick;

                int baseIdx = verts.Count;
                verts.Add(transform.InverseTransformPoint(w0));
                verts.Add(transform.InverseTransformPoint(w1));
                verts.Add(transform.InverseTransformPoint(w2));
                verts.Add(transform.InverseTransformPoint(w3));

                Vector3 lNormal = transform.InverseTransformDirection(worldNormal);
                norms.Add(lNormal);
                norms.Add(lNormal);
                norms.Add(-lNormal);
                norms.Add(-lNormal);

                uvs.Add(new Vector2(0f, v));
                uvs.Add(new Vector2(1f, v));
                uvs.Add(new Vector2(1f, v));
                uvs.Add(new Vector2(0f, v));

                if (i > 0)
                {
                    int prev = baseIdx - 4;
                    AddQuad(tris, prev + 0, prev + 1, baseIdx + 1, baseIdx + 0);
                    AddQuad(tris, prev + 3, baseIdx + 3, baseIdx + 2, prev + 2);
                    AddQuad(tris, prev + 1, baseIdx + 1, baseIdx + 2, prev + 2);
                    AddQuad(tris, prev + 3, baseIdx + 3, baseIdx + 0, prev + 0);
                }
            }

            AddQuad(tris, startVertexIndex + 0, startVertexIndex + 3, startVertexIndex + 2, startVertexIndex + 1);
            int lastBase = verts.Count - 4;
            AddQuad(tris, lastBase + 0, lastBase + 1, lastBase + 2, lastBase + 3);

            return rawArcLen;
        }

        private static void AddQuad(List<int> tris, int i0, int i1, int i2, int i3)
        {
            tris.Add(i0); tris.Add(i1); tris.Add(i2);
            tris.Add(i0); tris.Add(i2); tris.Add(i3);
        }
    }
}
