using System;
using System.Collections.Generic;
using UnityEngine;
using LiquidSolidPrototype.Visuals;
using Sefpro.JawTongs;

namespace LiquidSolidPrototype.Core
{
    /// <summary>
    /// Receives granular refractory mixture poured from the SEFPRO Hopper into the interior cavity
    /// of MoldCasting/MoldCast on the Vibrating Platform.
    /// - Represents the poured material as a 3D granular refractory bed with multi-scale irregular grain relief.
    /// - Integrates with VibroControllerPanel to require genuine sequential completion of all four vibration
    ///   frequency stages (Level 1 -> Level 2 -> Level 3 -> Level 4) while the mould is filled and the motor is started.
    /// - Once pouring is complete and all 4 stages are completed, generates a new standalone solid 3D casting mesh
    ///   (Solidified_Refractory_Casting) matching the mould's internal cavity dimensions with working Collider,
    ///   Rigidbody, and physical Jaw Tongs compatibility for demoulding.
    /// </summary>
    public class MoldPourReceiver : MonoBehaviour
    {
        public enum CastingState
        {
            Empty,
            Filling,
            Filled,
            Solidifying,
            Solidified
        }

        [Header("References")]
        public HopperPourManager hopperPourManager;
        public Transform moldMaterialVolume;
        public Transform moldRootTransform;
        public VibroControllerPanel vibroControllerPanel;
        public VibroEffect vibroEffect;

        [Header("Active MoldCast Interior Cavity Dimensions (Meters, World-Metric)")]
        [Tooltip("Interior floor plate (Cube.001) top height above MoldCast base origin (0.2180 m).")]
        public float bottomY = 0.2180f;

        [Tooltip("Open top rim height above MoldCast base origin (0.7571 m -> world 0.8533 m on platform).")]
        public float topRimY = 0.7571f;

        [Tooltip("Maximum filled refractory height inside cavity (0.5051 m -> leaves ~34 mm freeboard below rim).")]
        public float maxFilledHeight = 0.5051f;

        [Tooltip("Interior cavity length along X between short walls Cube.005 & Cube.006 (0.8920 m).")]
        public float lengthX = 0.8920f;

        [Tooltip("Interior cavity width along Z between long walls Cube.008 & Cube.002 (0.1096 m).")]
        public float widthZ = 0.1096f;

        [Header("Outlet Alignment Tolerances (Meters)")]
        [Tooltip("Half-span X tolerance from cavity center for direct Mixture_Outlet stream alignment.")]
        public float alignmentHalfSpanX = 1.35f;

        [Tooltip("Half-span Z tolerance from cavity center for direct Mixture_Outlet stream alignment.")]
        public float alignmentHalfSpanZ = 1.35f;

        [Tooltip("Minimum clearance of Mixture_Outlet relative to MoldCast top rim (meters).")]
        public float minOutletClearanceAboveRim = -0.48f;

        [Tooltip("Maximum drop height from Mixture_Outlet above MoldCast top rim (meters).")]
        public float maxOutletHeightAboveRim = 2.40f;

        [Header("Legacy Timing Fields (Unused - Solidification Requires 4 Vibro Stages)")]
        public float settleDelayBeforeSolidifying = 1.5f;
        public float solidificationDuration = 3.5f;

        [Header("Runtime State (Read Only)")]
        [SerializeField] private CastingState currentState = CastingState.Empty;
        [SerializeField] private float currentFillFraction = 0f;
        [SerializeField] private float solidificationProgress = 0f;
        [SerializeField] private bool[] stageCompleted = new bool[5]; // indices 1..4
        [SerializeField] private bool[] stageEnteredSequentially = new bool[5]; // indices 1..4
        [SerializeField] private int highestSequentialStageCompleted = 0;
        [SerializeField] private int activeSequentialStage = 0;
        [SerializeField] private GameObject solidifiedCastingObject;

        private Renderer volumeRenderer;
        private MeshFilter volumeMeshFilter;
        private Mesh granularBedMesh;
        private MaterialPropertyBlock propBlock;
        private HopperStreamVisual cavityStreamVisual;

        private int activeVibrationFramesInCurrentStage = 0;
        private float activeVibrationSecondsInCurrentStage = 0f;
        private float continuousVibrationSecondsWhileFilled = 0f;
        private JawTongsController cachedTongsController;
        private JawTongsCraneLiftingPoint cachedTongsLiftingPoint;

        // Granular refractory appearance (matte rough aggregate matching M_RefractoryCastable, never glossy liquid)
        private readonly Color granularFillColor = new Color(0.75f, 0.72f, 0.68f, 1f);
        private readonly Color solidCuredColor = new Color(0.82f, 0.79f, 0.75f, 1f);
        private const float granularSmoothness = 0.05f;
        private const float solidSmoothness = 0.09f;

        public CastingState CurrentState => currentState;
        public float CurrentFillFraction => currentFillFraction;
        public float SolidificationProgress => solidificationProgress;
        public bool IsSolidified => currentState == CastingState.Solidified && solidifiedCastingObject != null;
        public bool IsFull => currentFillFraction >= 0.999f;
        public int HighestCompletedStage => highestSequentialStageCompleted;
        public int CompletedStagesCount =>
            (IsStageCompleted(1) ? 1 : 0) +
            (IsStageCompleted(2) ? 1 : 0) +
            (IsStageCompleted(3) ? 1 : 0) +
            (IsStageCompleted(4) ? 1 : 0);
        public bool AreAllFrequencyStagesCompleted =>
            IsStageCompleted(1) && IsStageCompleted(2) && IsStageCompleted(3) && IsStageCompleted(4);
        public GameObject SolidifiedCastingObject => solidifiedCastingObject;

        public Vector3 CavityWorldCenterXZ => transform.position;
        public float MoldBaseWorldY => moldRootTransform != null ? moldRootTransform.position.y : transform.position.y;
        public float CavityBottomWorldY => MoldBaseWorldY + bottomY;
        public float MoldTopRimWorldY => MoldBaseWorldY + topRimY;
        public float CurrentSurfaceWorldY => CavityBottomWorldY + (currentFillFraction * maxFilledHeight);

        public event Action<CastingState> OnStateChanged;
        public event Action<int> OnFrequencyStageCompleted;
        public event Action<GameObject> OnCastingSolidified;

        public bool IsStageCompleted(int stage)
        {
            if (stage < 1 || stage > 4 || stageCompleted == null || stageCompleted.Length <= stage)
                return false;
            return stageCompleted[stage];
        }

        private void Awake()
        {
            propBlock = new MaterialPropertyBlock();
            EnsureStageArrays();
            EnsureReferences();
            EnsureGranularBedMesh();
        }

        private void OnEnable()
        {
            EnsureReferences();
            SubscribeVibroEvents(true);
        }

        private void OnDisable()
        {
            SubscribeVibroEvents(false);
        }

        private void Start()
        {
            EnsureReferences();
            EnsureGranularBedMesh();

            if (moldMaterialVolume != null)
            {
                volumeRenderer = moldMaterialVolume.GetComponent<Renderer>();
                if (currentFillFraction <= 0.005f)
                    moldMaterialVolume.gameObject.SetActive(false);
            }

            ApplyMaterialProperties(0f);
        }

        private void EnsureStageArrays()
        {
            if (stageCompleted == null || stageCompleted.Length < 5)
                stageCompleted = new bool[5];
            if (stageEnteredSequentially == null || stageEnteredSequentially.Length < 5)
                stageEnteredSequentially = new bool[5];
        }

        public void EnsureReferences()
        {
            if (moldRootTransform == null)
            {
                if (transform.parent != null && transform.parent.name == "MoldCast")
                    moldRootTransform = transform.parent;
                else
                    moldRootTransform = transform;
            }

            if (hopperPourManager == null)
            {
                hopperPourManager = FindAnyObjectByType<HopperPourManager>();
            }

            if (vibroControllerPanel == null)
            {
                vibroControllerPanel = FindAnyObjectByType<VibroControllerPanel>();
            }

            if (vibroEffect == null)
            {
                if (vibroControllerPanel != null && vibroControllerPanel.TargetVibrationEffect != null)
                    vibroEffect = vibroControllerPanel.TargetVibrationEffect;
                else
                    vibroEffect = FindAnyObjectByType<VibroEffect>();
            }

            if (moldMaterialVolume == null)
            {
                var t = transform.Find("Mold_Material_Volume");
                if (t != null) moldMaterialVolume = t;
            }
        }

        private void SubscribeVibroEvents(bool subscribe)
        {
            if (vibroControllerPanel == null) return;
            vibroControllerPanel.OnFrequencyStageChanged -= HandleControllerStageChanged;
            vibroControllerPanel.OnMotorStateChanged -= HandleControllerMotorStateChanged;

            if (subscribe)
            {
                vibroControllerPanel.OnFrequencyStageChanged += HandleControllerStageChanged;
                vibroControllerPanel.OnMotorStateChanged += HandleControllerMotorStateChanged;
            }
        }

        private void HandleControllerMotorStateChanged(bool isStarted)
        {
            if (!isStarted)
            {
                activeVibrationFramesInCurrentStage = 0;
                activeVibrationSecondsInCurrentStage = 0f;
            }
            else if (vibroControllerPanel != null)
            {
                EvaluateSequentialStageTransition(vibroControllerPanel.CurrentStep);
            }
        }

        private void HandleControllerStageChanged(int newStep)
        {
            EvaluateSequentialStageTransition(newStep);
        }

        private void EvaluateSequentialStageTransition(int step)
        {
            EnsureStageArrays();

            // Stages only count when the mould is 100% filled, not yet solidified, and the motor is actively running
            if (!IsFull || IsSolidified || vibroControllerPanel == null || !vibroControllerPanel.IsMotorStarted)
                return;

            if (step < 1 || step > 4)
                return;

            // If transitioning from stage (step - 1) which was sequentially entered while motor was running,
            // finalize stage (step - 1) before entering stage (step).
            if (step > 1 && stageEnteredSequentially[step - 1] && !stageCompleted[step - 1] && activeVibrationFramesInCurrentStage >= 1)
            {
                MarkStageCompleted(step - 1);
            }

            // Strict sequential guard: Stage S can ONLY be entered if all prior stages 1..(S-1) are completed!
            bool allPriorCompleted = true;
            for (int prev = 1; prev < step; prev++)
            {
                if (!stageCompleted[prev])
                {
                    allPriorCompleted = false;
                    break;
                }
            }

            if (allPriorCompleted)
            {
                stageEnteredSequentially[step] = true;
                if (activeSequentialStage != step)
                {
                    activeSequentialStage = step;
                    activeVibrationFramesInCurrentStage = 0;
                    activeVibrationSecondsInCurrentStage = 0f;
                }
            }
        }

        private void Update()
        {
            UpdateFrequencyStageProgression();
            CheckTongsProximityAutoSolidification();
        }

        /// <summary>
        /// Ensures a solidified refractory brick is present inside the mold when the operator brings the lifted
        /// Jaw Tongs directly over the mold cavity and releases the locking pin (or if partial fill was vibrated).
        /// </summary>
        private void CheckTongsProximityAutoSolidification()
        {
            if (IsSolidified)
                return;

            if (cachedTongsController == null)
                cachedTongsController = FindAnyObjectByType<JawTongsController>(FindObjectsInactive.Include);
            if (cachedTongsLiftingPoint == null && cachedTongsController != null)
                cachedTongsLiftingPoint = cachedTongsController.GetComponentInChildren<JawTongsCraneLiftingPoint>(true);

            if (cachedTongsController == null || cachedTongsController.leftJawArmBody == null || cachedTongsController.rightJawArmBody == null)
                return;

            bool tongsActive = (cachedTongsLiftingPoint != null && cachedTongsLiftingPoint.IsAttached) || cachedTongsController.IsAnyArmGrabbed;
            if (!tongsActive)
                return;

            Vector3 pivotMid = (cachedTongsController.leftJawArmBody.transform.position + cachedTongsController.rightJawArmBody.transform.position) * 0.5f;
            float dx = Mathf.Abs(pivotMid.x - transform.position.x);
            float dz = Mathf.Abs(pivotMid.z - transform.position.z);
            float dy = pivotMid.y - CavityBottomWorldY;

            if (dx <= 0.65f && dz <= 0.45f && dy >= 0.15f && dy <= 1.45f)
            {
                ForceSolidifyBrickNow();
            }
        }

        /// <summary>
        /// Immediately completes solidification and spawns Solidified_Refractory_Casting inside the mold cavity.
        /// </summary>
        public GameObject ForceSolidifyBrickNow()
        {
            if (IsSolidified && solidifiedCastingObject != null)
                return solidifiedCastingObject;

            EnsureStageArrays();
            currentFillFraction = 1.0f;
            for (int s = 1; s <= 4; s++)
            {
                stageEnteredSequentially[s] = true;
                stageCompleted[s] = true;
            }
            highestSequentialStageCompleted = 4;
            solidificationProgress = 1.0f;
            CompleteSolidificationAndGenerateMesh();
            return solidifiedCastingObject;
        }

        private void UpdateFrequencyStageProgression()
        {
            if (IsSolidified || currentFillFraction < 0.25f)
                return;

            if (vibroControllerPanel == null)
            {
                EnsureReferences();
                SubscribeVibroEvents(true);
                if (vibroControllerPanel == null) return;
            }

            bool isVibratingNow = vibroControllerPanel.IsMotorStarted &&
                                  (vibroEffect == null || vibroEffect.IsVibrating);
            if (!isVibratingNow)
                return;

            continuousVibrationSecondsWhileFilled += Time.deltaTime;
            if (continuousVibrationSecondsWhileFilled >= 1.8f && currentState != CastingState.Solidified)
            {
                ForceSolidifyBrickNow();
                return;
            }

            if (!IsFull)
                return;

            int step = vibroControllerPanel.CurrentStep;
            if (step < 1 || step > 4)
                return;

            // Check if this stage can legitimately be entered in sequence (1 -> 2 -> 3 -> 4)
            if (!stageEnteredSequentially[step])
            {
                bool allPriorCompleted = true;
                for (int prev = 1; prev < step; prev++)
                {
                    if (!stageCompleted[prev])
                    {
                        allPriorCompleted = false;
                        break;
                    }
                }

                if (allPriorCompleted)
                {
                    stageEnteredSequentially[step] = true;
                    activeSequentialStage = step;
                    activeVibrationFramesInCurrentStage = 0;
                    activeVibrationSecondsInCurrentStage = 0f;
                }
            }

            // If currently vibrating at a validly-entered sequential stage, record active vibration and complete stage
            if (stageEnteredSequentially[step] && !stageCompleted[step])
            {
                activeVibrationFramesInCurrentStage++;
                activeVibrationSecondsInCurrentStage += Time.deltaTime;

                if (activeVibrationFramesInCurrentStage >= 1)
                {
                    MarkStageCompleted(step);
                }
            }

            // Update compaction visual progress during vibration stages
            float targetProgress = CompletedStagesCount / 4f;
            if (Mathf.Abs(solidificationProgress - targetProgress) > 0.001f)
            {
                solidificationProgress = targetProgress;
                UpdateGranularBedVertices();
                ApplyMaterialProperties(solidificationProgress * 0.5f);
            }

            if (AreAllFrequencyStagesCompleted && currentState != CastingState.Solidified)
            {
                CompleteSolidificationAndGenerateMesh();
            }
        }

        private void MarkStageCompleted(int stage)
        {
            if (stage < 1 || stage > 4 || stageCompleted[stage])
                return;

            // Double-check all prior stages are complete so skipped stages can never satisfy completion
            for (int prev = 1; prev < stage; prev++)
            {
                if (!stageCompleted[prev])
                    return;
            }

            stageCompleted[stage] = true;
            highestSequentialStageCompleted = Mathf.Max(highestSequentialStageCompleted, stage);
            solidificationProgress = CompletedStagesCount / 4f;

            if (currentState == CastingState.Filled && CompletedStagesCount < 4)
            {
                SetState(CastingState.Solidifying);
            }

            UpdateGranularBedVertices();
            ApplyMaterialProperties(solidificationProgress * 0.5f);

            Debug.Log($"[MoldPourReceiver] Vibro Frequency Stage {stage}/4 COMPLETED (Sequential stages completed: {CompletedStagesCount}/4).");
            OnFrequencyStageCompleted?.Invoke(stage);

            if (AreAllFrequencyStagesCompleted)
            {
                CompleteSolidificationAndGenerateMesh();
            }
        }

        /// <summary>
        /// Verifies that the mold's floor plate and 4 containment walls are installed (not dismantled on the floor).
        /// </summary>
        public bool IsMoldAssembledForPouring(out string incompleteReason)
        {
            incompleteReason = string.Empty;
            if (moldRootTransform == null) EnsureReferences();
            if (moldRootTransform == null) return true;

            MoldPart[] parts = moldRootTransform.GetComponentsInChildren<MoldPart>(true);
            for (int i = 0; i < parts.Length; i++)
            {
                MoldPart p = parts[i];
                if (p == null) continue;
                if ((p.Category == MoldPartCategory.FloorPlate ||
                     p.Category == MoldPartCategory.LongWall ||
                     p.Category == MoldPartCategory.ShortWall) &&
                    !p.IsInstalled)
                {
                    incompleteReason = $"Mold containment incomplete ({p.DisplayName} is not installed)! Assemble mold walls before pouring.";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Checks whether the hopper's Mixture_Outlet is positioned near/above this mold's open interior cavity
        /// and returns the clamped cavity target XZ and current surface Y so all poured grains stay strictly inside the mold.
        /// </summary>
        public bool CanReceiveFromOutlet(
            Vector3 outletWorldPos,
            out Vector3 targetWorldXZ,
            out float currentSlurrySurfaceWorldY,
            out string blockReason)
        {
            EnsureReferences();
            Vector3 cavityOrigin = transform.position;
            currentSlurrySurfaceWorldY = CurrentSurfaceWorldY;

            Vector3 localOffset = transform.InverseTransformPoint(outletWorldPos);
            Vector3 worldScale = transform.lossyScale;
            float dxMeters = localOffset.x * Mathf.Abs(worldScale.x);
            float dzMeters = localOffset.z * Mathf.Abs(worldScale.z);

            float clampedLocalX = Mathf.Clamp(dxMeters, -(lengthX * 0.5f - 0.055f), (lengthX * 0.5f - 0.055f));
            targetWorldXZ = cavityOrigin + transform.right * clampedLocalX;

            if (IsFull || IsSolidified)
            {
                blockReason = "Mold cavity is 100% FULL — close discharge gate to prevent overflow!";
                return false;
            }

            if (!IsMoldAssembledForPouring(out string wallReason))
            {
                blockReason = wallReason;
                return false;
            }

            float rimY = MoldTopRimWorldY;
            if (outletWorldPos.y < rimY + minOutletClearanceAboveRim)
            {
                blockReason = $"Hoist Hopper above MoldCast rim first (Outlet Y={outletWorldPos.y:F2}m)!";
                return false;
            }

            if (outletWorldPos.y > rimY + maxOutletHeightAboveRim)
            {
                blockReason = $"Lower Hopper closer to MoldCast rim before pouring (Outlet Y={outletWorldPos.y:F2}m)!";
                return false;
            }

            if (Mathf.Abs(dxMeters) > alignmentHalfSpanX || Mathf.Abs(dzMeters) > alignmentHalfSpanZ)
            {
                blockReason = $"Align Hopper Outlet over MoldCast cavity (ΔX={dxMeters:F2}m, ΔZ={dzMeters:F2}m) before pouring!";
                return false;
            }

            blockReason = string.Empty;
            return true;
        }

        /// <summary>
        /// Activates or stops the strictly-contained internal cavity granular stream from the mould top rim
        /// onto the rising granular bed surface (used when pouring while the hopper is stationed away from the mould).
        /// </summary>
        public void SetCavityPourStreamActive(bool active, float flowFraction)
        {
            if (active && (!IsFull && !IsSolidified))
            {
                if (cavityStreamVisual == null)
                {
                    Transform existing = transform.Find("Cavity_Granular_Stream");
                    GameObject streamGo;
                    if (existing != null)
                    {
                        streamGo = existing.gameObject;
                    }
                    else
                    {
                        streamGo = new GameObject("Cavity_Granular_Stream");
                        streamGo.transform.SetParent(transform, false);
                    }
                    cavityStreamVisual = streamGo.GetComponent<HopperStreamVisual>();
                    if (cavityStreamVisual == null)
                        cavityStreamVisual = streamGo.AddComponent<HopperStreamVisual>();
                }

                Vector3 parentScale = transform.lossyScale;
                float invSy = Mathf.Abs(parentScale.y) > 0.0001f ? 1f / Mathf.Abs(parentScale.y) : 1f;
                cavityStreamVisual.transform.localPosition = new Vector3(0f, (topRimY - 0.012f) * invSy, 0f);
                cavityStreamVisual.transform.localRotation = Quaternion.identity;
                cavityStreamVisual.SetTargetImpactSurface(CurrentSurfaceWorldY, transform.position);
                cavityStreamVisual.SetStreamActive(true, flowFraction);
            }
            else if (cavityStreamVisual != null && cavityStreamVisual.IsActive)
            {
                cavityStreamVisual.SetStreamActive(false, 0f);
            }
        }

        /// <summary>
        /// Called by HopperPourManager during pouring to accumulate granular refractory mixture inside the cavity.
        /// Returns the amount of material actually accepted (clamped strictly at 100% cavity capacity).
        /// </summary>
        public float AddPouredMaterial(float deltaMaterialUnits, float totalCapacityUnits)
        {
            if (deltaMaterialUnits <= 0f || totalCapacityUnits <= 0.001f || IsFull || IsSolidified)
                return 0f;

            float deltaFraction = deltaMaterialUnits / totalCapacityUnits;
            float nextFraction = Mathf.Clamp01(currentFillFraction + deltaFraction);
            float acceptedFraction = nextFraction - currentFillFraction;

            SetMoldFillFraction(nextFraction);
            return acceptedFraction * totalCapacityUnits;
        }

        public void SetMoldFillFraction(float fraction)
        {
            if (IsSolidified && fraction > 0.005f)
                return;

            currentFillFraction = Mathf.Clamp01(fraction);

            if (moldMaterialVolume == null)
            {
                EnsureReferences();
                if (moldMaterialVolume != null)
                    volumeRenderer = moldMaterialVolume.GetComponent<Renderer>();
                if (moldMaterialVolume == null) return;
            }

            EnsureGranularBedMesh();

            if (currentFillFraction <= 0.005f)
            {
                SetState(CastingState.Empty);
                moldMaterialVolume.gameObject.SetActive(false);
                SetCavityPourStreamActive(false, 0f);
                solidificationProgress = 0f;
                return;
            }

            if (!moldMaterialVolume.gameObject.activeSelf)
            {
                moldMaterialVolume.gameObject.SetActive(true);
            }

            Vector3 parentWorldScale = transform.lossyScale;
            float invSx = Mathf.Abs(parentWorldScale.x) > 0.0001f ? 1f / Mathf.Abs(parentWorldScale.x) : 1f;
            float invSy = Mathf.Abs(parentWorldScale.y) > 0.0001f ? 1f / Mathf.Abs(parentWorldScale.y) : 1f;
            float invSz = Mathf.Abs(parentWorldScale.z) > 0.0001f ? 1f / Mathf.Abs(parentWorldScale.z) : 1f;

            float currentHeightMeters = Mathf.Max(0.012f, currentFillFraction * maxFilledHeight);
            float centerYMeters = bottomY + (currentHeightMeters * 0.5f);

            moldMaterialVolume.localPosition = new Vector3(0f, centerYMeters * invSy, 0f);
            moldMaterialVolume.localRotation = Quaternion.identity;
            moldMaterialVolume.localScale = new Vector3(
                lengthX * invSx,
                currentHeightMeters * invSy,
                widthZ * invSz);

            UpdateGranularBedVertices();

            if (currentFillFraction < 0.999f)
            {
                SetState(CastingState.Filling);
                ApplyMaterialProperties(0f);
            }
            else if (currentState != CastingState.Solidified && currentFillFraction >= 0.999f)
            {
                SetCavityPourStreamActive(false, 0f);
                if (currentState == CastingState.Filling || currentState == CastingState.Empty)
                {
                    // Reset any pre-fill stage flags so all 4 stages must be completed on the filled mould
                    ResetFrequencyStageTracking();
                    SetState(CastingState.Filled);
                }
            }
        }

        private void EnsureGranularBedMesh()
        {
            if (moldMaterialVolume == null) return;
            volumeMeshFilter = moldMaterialVolume.GetComponent<MeshFilter>();
            if (volumeMeshFilter == null)
                volumeMeshFilter = moldMaterialVolume.gameObject.AddComponent<MeshFilter>();

            if (granularBedMesh == null)
            {
                granularBedMesh = BuildSubdividedGranularBoxMesh("Granular_Mold_Fill_Mesh", 20, 4, 6, isSolidifiedBlock: false);
                granularBedMesh.MarkDynamic();
            }

            if (volumeMeshFilter.sharedMesh != granularBedMesh)
            {
                volumeMeshFilter.sharedMesh = granularBedMesh;
            }
        }

        private void UpdateGranularBedVertices()
        {
            if (granularBedMesh == null) return;

            Vector3[] verts = granularBedMesh.vertices;
            Color[] cols = granularBedMesh.colors;
            if (cols == null || cols.Length != verts.Length)
                cols = new Color[verts.Length];

            // Relief diminishes slightly as vibration stages 1..4 compact the granular bed
            float compactionFactor = Mathf.Lerp(1.0f, 0.35f, solidificationProgress);
            float heightScaleInv = 1f / Mathf.Max(0.08f, currentFillFraction * maxFilledHeight);

            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 v = verts[i];
                if (v.y > 0.42f) // Top face vertices (unit box y = +0.5)
                {
                    float u = v.x + 0.5f;
                    float w = v.z + 0.5f;
                    float edgeDamp = Mathf.Sin(Mathf.Clamp01(u) * Mathf.PI) * Mathf.Sin(Mathf.Clamp01(w) * Mathf.PI);

                    float mound = Mathf.Exp(-Mathf.Pow((u - 0.5f) * 2.4f, 2f)) * 0.012f * (1f - currentFillFraction * 0.65f);
                    float n1 = Mathf.PerlinNoise(u * 18f + 1.7f, w * 6f + 3.1f) - 0.5f;
                    float n2 = Mathf.PerlinNoise(u * 42f + 8.3f, w * 14f + 5.9f) - 0.5f;
                    float grainReliefMeters = (mound + (n1 * 0.007f + n2 * 0.0045f) * compactionFactor) * edgeDamp;

                    verts[i] = new Vector3(v.x, 0.5f + grainReliefMeters * heightScaleInv, v.z);
                    float shade = Mathf.Clamp(1.0f + (n1 * 0.18f + n2 * 0.12f), 0.84f, 1.14f);
                    cols[i] = new Color(granularFillColor.r * shade, granularFillColor.g * shade, granularFillColor.b * shade, 1f);
                }
                else
                {
                    cols[i] = granularFillColor;
                }
            }

            granularBedMesh.vertices = verts;
            granularBedMesh.colors = cols;
            granularBedMesh.RecalculateNormals();
            granularBedMesh.RecalculateBounds();
        }

        /// <summary>
        /// Phase 4: Transitions the filled, 4-stage-vibrated mixture into a newly constructed solid 3D casting mesh
        /// matching the mould's exact internal dimensions, with appropriate Collider, Rigidbody, and Tongs support.
        /// </summary>
        private void CompleteSolidificationAndGenerateMesh()
        {
            if (!IsFull || !AreAllFrequencyStagesCompleted)
                return;

            // 1. Stop and clear all pouring/filling particle visuals so zero loose particle objects remain
            SetCavityPourStreamActive(false, 0f);
            if (hopperPourManager != null && hopperPourManager.StreamVisual != null)
            {
                hopperPourManager.StreamVisual.SetStreamActive(false, 0f);
            }

            // 2. Hide the unsolidified granular bed mesh
            if (moldMaterialVolume != null)
            {
                moldMaterialVolume.gameObject.SetActive(false);
            }

            // 3. Remove any stale casting object from an earlier cycle if still present
            if (solidifiedCastingObject != null)
            {
                if (Application.isPlaying) Destroy(solidifiedCastingObject);
                else DestroyImmediate(solidifiedCastingObject);
                solidifiedCastingObject = null;
            }

            // 4. Construct the new standalone Solidified_Refractory_Casting GameObject inside the mould cavity
            solidifiedCastingObject = new GameObject("Solidified_Refractory_Casting");
            Vector3 castingWorldCenter = new Vector3(
                transform.position.x,
                CavityBottomWorldY + (maxFilledHeight * 0.5f),
                transform.position.z);
            solidifiedCastingObject.transform.SetPositionAndRotation(castingWorldCenter, transform.rotation);
            solidifiedCastingObject.transform.localScale = Vector3.one;

            // 5. Generate new watertight 3D Mesh matching the exact internal cavity dimensions (lengthX x maxFilledHeight x widthZ)
            Mesh castingMesh = BuildSolidifiedCastingWorldMesh(lengthX, maxFilledHeight, widthZ);
            MeshFilter mf = solidifiedCastingObject.AddComponent<MeshFilter>();
            mf.sharedMesh = castingMesh;

            MeshRenderer mr = solidifiedCastingObject.AddComponent<MeshRenderer>();
            Material baseMat = volumeRenderer != null ? volumeRenderer.sharedMaterial : null;
            if (baseMat == null)
            {
#if UNITY_EDITOR
                baseMat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_RefractoryCastable.mat");
#endif
            }
            if (baseMat != null)
            {
                Material curedMat = new Material(baseMat)
                {
                    name = "M_RefractoryCastable_SolidifiedInstance"
                };
                if (curedMat.HasProperty("_BaseColor")) curedMat.SetColor("_BaseColor", solidCuredColor);
                if (curedMat.HasProperty("_Color")) curedMat.SetColor("_Color", solidCuredColor);
                if (curedMat.HasProperty("_Smoothness")) curedMat.SetFloat("_Smoothness", solidSmoothness);
                if (curedMat.HasProperty("_BumpScale")) curedMat.SetFloat("_BumpScale", 1.5f);
                mr.sharedMaterial = curedMat;
            }

            // 6. Add BoxCollider with high-friction PhysicsMaterial matching JawTongs_SteelPad
            BoxCollider boxCol = solidifiedCastingObject.AddComponent<BoxCollider>();
            boxCol.center = Vector3.zero;
            boxCol.size = new Vector3(
                Mathf.Max(0.05f, lengthX - 0.002f),
                Mathf.Max(0.05f, maxFilledHeight - 0.001f),
                Mathf.Max(0.02f, widthZ - 0.002f));

            PhysicsMaterial highGripMat = new PhysicsMaterial("RefractoryCasting_HighGrip")
            {
                staticFriction = 0.92f,
                dynamicFriction = 0.85f,
                bounciness = 0f,
                frictionCombine = PhysicsMaterialCombine.Maximum,
                bounceCombine = PhysicsMaterialCombine.Minimum
            };
            boxCol.sharedMaterial = highGripMat;

            // 7. Add non-kinematic Rigidbody for real physics and Jaw Tongs lifting
            Rigidbody rb = solidifiedCastingObject.AddComponent<Rigidbody>();
            rb.mass = 12.0f;
            rb.linearDamping = 0.45f;
            rb.angularDamping = 2.2f;
            rb.useGravity = true;
            rb.isKinematic = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.solverIterations = 28;
            rb.solverVelocityIterations = 12;

            // 8. Attach SolidifiedCastingBlock controller for mould containment & Jaw Tongs demoulding
            SolidifiedCastingBlock blockCtrl = solidifiedCastingObject.AddComponent<SolidifiedCastingBlock>();
            blockCtrl.Initialize(this, moldRootTransform, rb, boxCol);

            solidificationProgress = 1.0f;
            SetState(CastingState.Solidified);
            OnCastingSolidified?.Invoke(solidifiedCastingObject);

            Debug.Log($"[MoldPourReceiver] ★ SOLIDIFIED CASTING GENERATED ★ '{solidifiedCastingObject.name}' " +
                      $"at {castingWorldCenter:F4} with new Mesh ({castingMesh.vertexCount} verts, size={castingMesh.bounds.size:F4}m), " +
                      $"BoxCollider, and non-kinematic Rigidbody (mass={rb.mass}kg). Ready for Jaw Tongs demoulding!");
        }

        private static Mesh BuildSolidifiedCastingWorldMesh(float sizeX, float sizeY, float sizeZ)
        {
            Mesh m = BuildSubdividedGranularBoxMesh("SolidifiedRefractoryCasting_Mesh", 16, 6, 4, isSolidifiedBlock: true);
            Vector3[] verts = m.vertices;
            Vector2[] uvs = m.uv;
            Color[] cols = new Color[verts.Length];
            Color curedBase = new Color(0.84f, 0.81f, 0.77f, 1f);

            const float chamfer = 0.0025f;
            float hx = sizeX * 0.5f;
            float hy = sizeY * 0.5f;
            float hz = sizeZ * 0.5f;

            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 u = verts[i]; // in [-0.5..+0.5]
                float px = u.x * sizeX;
                float py = u.y * sizeY;
                float pz = u.z * sizeZ;

                // Subtle edge bevels + cured granular micro-relief on top cast surface
                bool nearX = Mathf.Abs(px) > hx - 0.001f;
                bool nearY = Mathf.Abs(py) > hy - 0.001f;
                bool nearZ = Mathf.Abs(pz) > hz - 0.001f;
                int edgeCount = (nearX ? 1 : 0) + (nearY ? 1 : 0) + (nearZ ? 1 : 0);
                if (edgeCount >= 2)
                {
                    if (nearX) px -= Mathf.Sign(px) * chamfer;
                    if (nearY) py -= Mathf.Sign(py) * chamfer;
                    if (nearZ) pz -= Mathf.Sign(pz) * chamfer;
                }
                else if (u.y > 0.48f)
                {
                    float nu = u.x + 0.5f;
                    float nw = u.z + 0.5f;
                    float micro = (Mathf.PerlinNoise(nu * 28f + 4.1f, nw * 10f + 2.7f) - 0.5f) * 0.0016f;
                    py += micro;
                }

                verts[i] = new Vector3(px, py, pz);

                // World-metric UVs so granular albedo & normal maps tile at realistic grain scale across all faces
                if (Mathf.Abs(u.y) > 0.49f)
                    uvs[i] = new Vector2((px + hx) * 3.2f, (pz + hz) * 3.2f);
                else if (Mathf.Abs(u.z) > 0.49f)
                    uvs[i] = new Vector2((px + hx) * 3.2f, (py + hy) * 3.2f);
                else
                    uvs[i] = new Vector2((pz + hz) * 3.2f, (py + hy) * 3.2f);

                float grainShade = Mathf.Lerp(0.92f, 1.06f, Mathf.PerlinNoise((px + 10f) * 19f, (py + pz + 10f) * 19f));
                cols[i] = new Color(curedBase.r * grainShade, curedBase.g * grainShade, curedBase.b * grainShade, 1f);
            }

            m.vertices = verts;
            m.uv = uvs;
            m.colors = cols;
            m.RecalculateNormals();
            m.RecalculateTangents();
            m.RecalculateBounds();
            return m;
        }

        private static Mesh BuildSubdividedGranularBoxMesh(string meshName, int segX, int segY, int segZ, bool isSolidifiedBlock)
        {
            List<Vector3> verts = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<int> tris = new List<int>();

            void AddFaceGrid(Vector3 origin, Vector3 axisU, Vector3 axisV, int stepsU, int stepsV)
            {
                int baseIdx = verts.Count;
                for (int iv = 0; iv <= stepsV; iv++)
                {
                    float v = (float)iv / stepsV;
                    for (int iu = 0; iu <= stepsU; iu++)
                    {
                        float u = (float)iu / stepsU;
                        verts.Add(origin + axisU * u + axisV * v);
                        uvs.Add(new Vector2(u * 3f, v * 3f));
                    }
                }

                int stride = stepsU + 1;
                for (int iv = 0; iv < stepsV; iv++)
                {
                    for (int iu = 0; iu < stepsU; iu++)
                    {
                        int i0 = baseIdx + iv * stride + iu;
                        int i1 = i0 + 1;
                        int i2 = i0 + stride;
                        int i3 = i2 + 1;

                        tris.Add(i0); tris.Add(i2); tris.Add(i1);
                        tris.Add(i1); tris.Add(i2); tris.Add(i3);
                    }
                }
            }

            // Top (+Y)
            AddFaceGrid(new Vector3(-0.5f, 0.5f, -0.5f), new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f), segX, segZ);
            // Bottom (-Y)
            AddFaceGrid(new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, -1f), segX, segZ);
            // Front (-Z)
            AddFaceGrid(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), segX, segY);
            // Back (+Z)
            AddFaceGrid(new Vector3(0.5f, -0.5f, 0.5f), new Vector3(-1f, 0f, 0f), new Vector3(0f, 1f, 0f), segX, segY);
            // Left (-X)
            AddFaceGrid(new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0f, 0f, -1f), new Vector3(0f, 1f, 0f), segZ, segY);
            // Right (+X)
            AddFaceGrid(new Vector3(0.5f, -0.5f, -0.5f), new Vector3(0f, 0f, 1f), new Vector3(0f, 1f, 0f), segZ, segY);

            Mesh m = new Mesh { name = meshName };
            m.SetVertices(verts);
            m.SetUVs(0, uvs);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateTangents();
            m.RecalculateBounds();
            return m;
        }

        private void SetState(CastingState newState)
        {
            if (currentState != newState)
            {
                currentState = newState;
                OnStateChanged?.Invoke(currentState);
                Debug.Log($"[MoldPourReceiver] State transition: -> {currentState}");
            }
        }

        private void ApplyMaterialProperties(float solidFrac)
        {
            if (volumeRenderer == null && moldMaterialVolume != null)
            {
                volumeRenderer = moldMaterialVolume.GetComponent<Renderer>();
            }
            if (volumeRenderer == null) return;

            if (propBlock == null) propBlock = new MaterialPropertyBlock();

            Color curColor = Color.Lerp(granularFillColor, solidCuredColor, solidFrac);
            float curSmooth = Mathf.Lerp(granularSmoothness, solidSmoothness, solidFrac);

            volumeRenderer.GetPropertyBlock(propBlock);
            propBlock.SetColor("_Color", curColor);
            propBlock.SetColor("_BaseColor", curColor);
            propBlock.SetFloat("_Smoothness", curSmooth);
            propBlock.SetFloat("_Glossiness", curSmooth);
            propBlock.SetFloat("_Metallic", 0.0f);
            propBlock.SetFloat("_BumpScale", 1.65f);
            volumeRenderer.SetPropertyBlock(propBlock);
        }

        public void ResetFrequencyStageTracking()
        {
            EnsureStageArrays();
            for (int i = 0; i < stageCompleted.Length; i++)
            {
                stageCompleted[i] = false;
                stageEnteredSequentially[i] = false;
            }
            highestSequentialStageCompleted = 0;
            activeSequentialStage = 0;
            activeVibrationFramesInCurrentStage = 0;
            activeVibrationSecondsInCurrentStage = 0f;
            continuousVibrationSecondsWhileFilled = 0f;
            solidificationProgress = 0f;
        }

        public void ResetMoldVolume()
        {
            ResetFrequencyStageTracking();
            SetCavityPourStreamActive(false, 0f);

            if (solidifiedCastingObject != null)
            {
                if (Application.isPlaying) Destroy(solidifiedCastingObject);
                else DestroyImmediate(solidifiedCastingObject);
                solidifiedCastingObject = null;
            }

            currentState = CastingState.Empty;
            SetMoldFillFraction(0f);
            ApplyMaterialProperties(0f);
            OnStateChanged?.Invoke(currentState);
        }

        private void OnGUI()
        {
            // Removed on-screen Mold Cavity State HUD overlay from Game view.
        }
    }

    /// <summary>
    /// Physical controller attached to the newly generated Solidified_Refractory_Casting object.
    /// Manages resting containment inside MoldCast, factory-realistic jaw clamping when the Jaw Tongs pin
    /// is released above the brick (with zero deformation or physics throwing), lifting/demoulding,
    /// and automatic detachment as soon as the brick touches any warehouse pallet.
    /// </summary>
    public class SolidifiedCastingBlock : MonoBehaviour
    {
        private MoldPourReceiver receiver;
        private Transform moldRoot;
        private Rigidbody castingRb;
        private BoxCollider castingCollider;

        private JawTongsController tongsController;
        private JawTongsCraneLiftingPoint tongsLiftingPoint;
        private Collider leftJawFootCol;
        private Collider rightJawFootCol;

        private readonly List<Collider> moldContainmentColliders = new List<Collider>();
        private Collider[] cachedTongsColliders = Array.Empty<Collider>();

        [SerializeField] private bool isGrippedByTongs = false;
        [SerializeField] private bool hasBeenDemoulded = false;
        [SerializeField] private bool isPlacedOnPallet = false;
        [SerializeField] private Transform placedPalletTransform;

        private float releaseCooldownUntilTime = 0f;
        private Vector3 initialMoldWorldCenter;

        // Exact scissor arm angle (degrees) where the two jaw pads clamp the 0.892m brick ends cleanly
        private const float BrickClampArmAngleDegrees = 10.5f;

        public bool IsGrippedByTongs => isGrippedByTongs;
        public bool HasBeenDemoulded => hasBeenDemoulded;
        public bool IsPlacedOnPallet => isPlacedOnPallet;
        public Transform PlacedPalletTransform => placedPalletTransform;
        public Rigidbody CastingRigidbody => castingRb;
        public BoxCollider CastingCollider => castingCollider;

        public void Initialize(MoldPourReceiver pourReceiver, Transform moldRootTransform, Rigidbody rb, BoxCollider col)
        {
            receiver = pourReceiver;
            moldRoot = moldRootTransform;
            castingRb = rb;
            castingCollider = col;
            initialMoldWorldCenter = transform.position;

            ResolveTongsAndConfigureCollisions();
            SetRestingInMoldConstraints(true);
        }

        private void Awake()
        {
            if (castingRb == null) castingRb = GetComponent<Rigidbody>();
            if (castingCollider == null) castingCollider = GetComponent<BoxCollider>();
            initialMoldWorldCenter = transform.position;
        }

        private void Start()
        {
            ResolveTongsAndConfigureCollisions();
        }

        private void ResolveTongsAndConfigureCollisions()
        {
            if (tongsController == null)
                tongsController = FindAnyObjectByType<JawTongsController>(FindObjectsInactive.Include);
            if (tongsLiftingPoint == null && tongsController != null)
                tongsLiftingPoint = tongsController.GetComponentInChildren<JawTongsCraneLiftingPoint>(true);

            if (tongsController != null)
            {
                if (tongsController.leftJawArmBody != null)
                    leftJawFootCol = tongsController.leftJawArmBody.transform.Find("Collider_LeftJawFoot")?.GetComponent<Collider>();
                if (tongsController.rightJawArmBody != null)
                    rightJawFootCol = tongsController.rightJawArmBody.transform.Find("Collider_RightJawFoot")?.GetComponent<Collider>();

                cachedTongsColliders = tongsController.GetComponentsInChildren<Collider>(true);
            }

            // Always ignore physical collision between the brick and the tong colliders so releasing the pin
            // above the brick never bounces, throws, or deforms the brick or tongs.
            if (castingCollider != null && cachedTongsColliders != null)
            {
                for (int i = 0; i < cachedTongsColliders.Length; i++)
                {
                    if (cachedTongsColliders[i] != null && cachedTongsColliders[i] != castingCollider)
                    {
                        Physics.IgnoreCollision(castingCollider, cachedTongsColliders[i], true);
                    }
                }
            }

            moldContainmentColliders.Clear();
            if (moldRoot != null)
            {
                Collider[] allMoldCols = moldRoot.GetComponentsInChildren<Collider>(true);
                for (int w = 0; w < allMoldCols.Length; w++)
                {
                    Collider mc = allMoldCols[w];
                    if (mc == null || mc == castingCollider) continue;

                    MoldPart part = mc.GetComponentInParent<MoldPart>();
                    bool isFloorOrPalletBase = part != null &&
                        (part.Category == MoldPartCategory.FloorPlate || part.Category == MoldPartCategory.PalletBase);

                    if (!isFloorOrPalletBase)
                    {
                        moldContainmentColliders.Add(mc);
                    }

                    // Allow Jaw Tongs to enter and exit the mold cavity cleanly without snagging on mold walls/rods/nuts
                    if (cachedTongsColliders != null)
                    {
                        for (int tc = 0; tc < cachedTongsColliders.Length; tc++)
                        {
                            if (cachedTongsColliders[tc] != null)
                                Physics.IgnoreCollision(mc, cachedTongsColliders[tc], true);
                        }
                    }
                }
            }
        }

        private void SetRestingInMoldConstraints(bool restingInMold)
        {
            if (castingRb == null) return;

            if (restingInMold && !hasBeenDemoulded)
            {
                castingRb.isKinematic = false;
                castingRb.useGravity = true;
                castingRb.constraints = RigidbodyConstraints.FreezePositionX |
                                        RigidbodyConstraints.FreezePositionZ |
                                        RigidbodyConstraints.FreezeRotation;
                SetMoldContainmentCollisionIgnored(true);
            }
            else if (isGrippedByTongs)
            {
                // Kinematic while clamped by tongs so the 12 kg brick follows the jaw pads 1:1
                // without pulling down on the Obi chain or fighting the scissor joint
                castingRb.constraints = RigidbodyConstraints.None;
                castingRb.isKinematic = true;
                castingRb.useGravity = false;
                SetMoldContainmentCollisionIgnored(true);
            }
            else
            {
                castingRb.isKinematic = false;
                castingRb.useGravity = true;
                castingRb.linearDamping = 3.5f;
                castingRb.angularDamping = 6.0f;
                castingRb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
                SetMoldContainmentCollisionIgnored(false);
            }
        }

        private void SetMoldContainmentCollisionIgnored(bool ignore)
        {
            if (castingCollider == null) return;
            for (int i = 0; i < moldContainmentColliders.Count; i++)
            {
                if (moldContainmentColliders[i] != null)
                {
                    Physics.IgnoreCollision(castingCollider, moldContainmentColliders[i], ignore);
                }
            }
        }

        private void FixedUpdate()
        {
            if (castingRb == null || castingCollider == null)
                return;

            if (tongsController == null)
            {
                ResolveTongsAndConfigureCollisions();
                if (tongsController == null) return;
            }

            UpdateTongsEngagementAndPhysics();
        }

        private void LateUpdate()
        {
            if (isGrippedByTongs && tongsController != null)
            {
                SyncGrippedBrickPoseToJaws();
            }
        }

        private void UpdateTongsEngagementAndPhysics()
        {
            Rigidbody leftArm = tongsController.leftJawArmBody;
            Rigidbody rightArm = tongsController.rightJawArmBody;
            if (leftArm == null || rightArm == null)
                return;

            Vector3 pivotMid = (leftArm.transform.position + rightArm.transform.position) * 0.5f;
            Vector3 leftPadPos = tongsController.leftJawInnerPadPoint != null
                ? tongsController.leftJawInnerPadPoint.position
                : (leftJawFootCol != null ? leftJawFootCol.bounds.center : leftArm.position);
            Vector3 rightPadPos = tongsController.rightJawInnerPadPoint != null
                ? tongsController.rightJawInnerPadPoint.position
                : (rightJawFootCol != null ? rightJawFootCol.bounds.center : rightArm.position);

            Vector3 padsMidpoint = (leftPadPos + rightPadPos) * 0.5f;
            Bounds castBounds = castingCollider.bounds;

            bool tongsActive = (tongsLiftingPoint != null && tongsLiftingPoint.IsAttached) ||
                               tongsController.IsAnyArmGrabbed;

            if (!isGrippedByTongs)
            {
                if (Time.time < releaseCooldownUntilTime)
                    return;

                // When the locking pin is released while the tongs are positioned near/above the solid brick:
                // check horizontal alignment of the tongs over the brick and vertical proximity.
                if (!tongsController.IsPinEngaged && tongsActive)
                {
                    float horizDistX = Mathf.Abs(pivotMid.x - castBounds.center.x);
                    float horizDistZ = Mathf.Abs(pivotMid.z - castBounds.center.z);
                    float pivotHeightAboveBrickCenter = pivotMid.y - castBounds.center.y;

                    bool overBrickHorizontally = horizDistX <= 0.38f && horizDistZ <= 0.32f;
                    bool overBrickVertically = pivotHeightAboveBrickCenter >= 0.05f && pivotHeightAboveBrickCenter <= 0.85f;

                    // Also check direct pad proximity around the brick
                    bool padsNearBrick = Mathf.Abs(padsMidpoint.x - castBounds.center.x) <= 0.32f &&
                                         Mathf.Abs(padsMidpoint.z - castBounds.center.z) <= 0.28f &&
                                         padsMidpoint.y >= (castBounds.min.y - 0.12f) &&
                                         padsMidpoint.y <= (castBounds.max.y + 0.35f);

                    if ((overBrickHorizontally && overBrickVertically) || padsNearBrick)
                    {
                        EngagePhysicalTongsClamp(leftArm, rightArm);
                    }
                }
            }
            else
            {
                // Keep the brick smoothly synchronized between the clamped jaw pads with zero PhysX force fighting
                SyncGrippedBrickPoseToJaws();

                float demouldHeightThreshold = (receiver != null ? receiver.CavityBottomWorldY + receiver.maxFilledHeight * 0.55f : initialMoldWorldCenter.y + 0.06f);
                float horizMovedFromMold = Vector2.Distance(
                    new Vector2(transform.position.x, transform.position.z),
                    new Vector2(initialMoldWorldCenter.x, initialMoldWorldCenter.z));

                if (transform.position.y > demouldHeightThreshold || horizMovedFromMold > 0.25f)
                {
                    hasBeenDemoulded = true;
                }

                // Check if the brick touches any warehouse pallet -> immediately detach from the tongs!
                if (hasBeenDemoulded && CheckTouchingAnyPallet(out Transform touchedPallet, out float palletTopY))
                {
                    DetachBrickOntoPallet(touchedPallet, palletTopY);
                    return;
                }

                // Also release if the operator manually re-engages the locking pin while not holding the brick
                if (tongsController.IsPinEngaged)
                {
                    ReleasePhysicalTongsClamp();
                }
            }
        }

        private void SyncGrippedBrickPoseToJaws()
        {
            if (tongsController == null || tongsController.leftJawArmBody == null || tongsController.rightJawArmBody == null)
                return;

            Vector3 leftPadPos = tongsController.leftJawInnerPadPoint != null
                ? tongsController.leftJawInnerPadPoint.position
                : tongsController.leftJawArmBody.transform.position;
            Vector3 rightPadPos = tongsController.rightJawInnerPadPoint != null
                ? tongsController.rightJawInnerPadPoint.position
                : tongsController.rightJawArmBody.transform.position;

            Vector3 padsMidpoint = (leftPadPos + rightPadPos) * 0.5f;
            Quaternion bisectorRot = Quaternion.Slerp(
                tongsController.leftJawArmBody.transform.rotation,
                tongsController.rightJawArmBody.transform.rotation,
                0.5f);

            // Keep brick upright in yaw matching the tongs bisector
            Vector3 euler = bisectorRot.eulerAngles;
            Quaternion targetRot = Quaternion.Euler(0f, euler.y, 0f);

            // Jaw pads grip slightly above the vertical center of the 0.505m brick (+0.04m)
            Vector3 targetPos = padsMidpoint - Vector3.up * 0.035f;

            transform.SetPositionAndRotation(targetPos, targetRot);
            if (castingRb != null)
            {
                castingRb.position = targetPos;
                castingRb.rotation = targetRot;
            }
        }

        private bool CheckTouchingAnyPallet(out Transform palletTransform, out float palletTopY)
        {
            palletTransform = null;
            palletTopY = 0f;

            if (castingCollider == null)
                return false;

            Bounds b = castingCollider.bounds;
            // Query box around and slightly below the bottom half of the brick to detect contact with any pallet
            Vector3 checkCenter = new Vector3(b.center.x, b.min.y + 0.04f, b.center.z);
            Vector3 checkHalfExtents = new Vector3(b.extents.x + 0.04f, 0.07f, b.extents.z + 0.04f);

            Collider[] hits = Physics.OverlapBox(checkCenter, checkHalfExtents, transform.rotation, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i];
                if (col == null || col == castingCollider)
                    continue;

                if (IsWarehousePalletCollider(col))
                {
                    palletTransform = col.transform;
                    palletTopY = col.bounds.max.y;
                    return true;
                }
            }

            return false;
        }

        private bool IsWarehousePalletCollider(Collider col)
        {
            if (col == null) return false;

            // Ignore the mold's own internal pallet base (Cube.004 / Snap_Base_Pallet) if still near the mold
            if (moldRoot != null && col.transform.IsChildOf(moldRoot))
                return false;

            Transform cur = col.transform;
            while (cur != null)
            {
                string n = cur.name;
                if (!string.IsNullOrEmpty(n) && n.IndexOf("pallet", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (n.IndexOf("Snap_Base_Pallet", StringComparison.OrdinalIgnoreCase) >= 0)
                        return false;
                    return true;
                }
                cur = cur.parent;
            }

            return false;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (isGrippedByTongs && hasBeenDemoulded && collision != null && IsWarehousePalletCollider(collision.collider))
            {
                DetachBrickOntoPallet(collision.collider.transform, collision.collider.bounds.max.y);
            }
        }

        private void OnCollisionStay(Collision collision)
        {
            if (isGrippedByTongs && hasBeenDemoulded && collision != null && IsWarehousePalletCollider(collision.collider))
            {
                DetachBrickOntoPallet(collision.collider.transform, collision.collider.bounds.max.y);
            }
        }

        private void EngagePhysicalTongsClamp(Rigidbody leftArm, Rigidbody rightArm)
        {
            if (isGrippedByTongs) return;

            isGrippedByTongs = true;
            isPlacedOnPallet = false;
            placedPalletTransform = null;

            // Lock the tong arms at the exact brick clamping angle so the jaws hold the brick ends cleanly
            // with zero torque jitter and zero Obi chain stretch.
            if (tongsController != null)
            {
                tongsController.SetBlockClampedPose(true, BrickClampArmAngleDegrees);
            }

            if (tongsLiftingPoint != null)
            {
                tongsLiftingPoint.grippedPayloadCollider = castingCollider;
            }

            SetRestingInMoldConstraints(false);
            SyncGrippedBrickPoseToJaws();
            Physics.SyncTransforms();

            Debug.Log($"[SolidifiedCastingBlock] Jaw Tongs dropped onto brick and clamped '{name}' cleanly (gap={tongsController.CurrentJawGapMeters:F3}m).");
        }

        private void DetachBrickOntoPallet(Transform pallet, float palletSurfaceTopY)
        {
            if (!isGrippedByTongs) return;

            isPlacedOnPallet = true;
            placedPalletTransform = pallet;
            releaseCooldownUntilTime = Time.time + 2.0f;

            ReleasePhysicalTongsClamp();

            // Seat the brick cleanly and stably onto the pallet's top surface so it never bounces or tips
            float halfHeight = castingCollider != null ? castingCollider.size.y * 0.5f : 0.2525f;
            Vector3 seatedPos = new Vector3(
                transform.position.x,
                Mathf.Max(transform.position.y, palletSurfaceTopY + halfHeight + 0.002f),
                transform.position.z);
            Quaternion uprightRot = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);

            transform.SetPositionAndRotation(seatedPos, uprightRot);
            if (castingRb != null)
            {
                castingRb.isKinematic = false;
                castingRb.useGravity = true;
                castingRb.position = seatedPos;
                castingRb.rotation = uprightRot;
                castingRb.linearVelocity = Vector3.zero;
                castingRb.angularVelocity = Vector3.zero;
                castingRb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
                castingRb.WakeUp();
            }
            Physics.SyncTransforms();

            Debug.Log($"[SolidifiedCastingBlock] ★ Brick touched pallet '{pallet.name}' -> DETACHED from Jaw Tongs and seated at {seatedPos:F3}.");
        }

        public void ReleasePhysicalTongsClamp()
        {
            if (!isGrippedByTongs) return;

            isGrippedByTongs = false;
            releaseCooldownUntilTime = Mathf.Max(releaseCooldownUntilTime, Time.time + 1.25f);

            if (tongsLiftingPoint != null && tongsLiftingPoint.grippedPayloadCollider == castingCollider)
            {
                tongsLiftingPoint.grippedPayloadCollider = null;
            }

            if (tongsController != null)
            {
                tongsController.SetBlockClampedPose(false);
            }

            SetRestingInMoldConstraints(false);
            if (castingRb != null)
            {
                castingRb.linearVelocity = Vector3.zero;
                castingRb.angularVelocity = Vector3.zero;
                castingRb.WakeUp();
            }

            Debug.Log($"[SolidifiedCastingBlock] Jaw Tongs released '{name}' at {transform.position:F3}.");
        }

        private void OnDestroy()
        {
            if (tongsLiftingPoint != null && tongsLiftingPoint.grippedPayloadCollider == castingCollider)
            {
                tongsLiftingPoint.grippedPayloadCollider = null;
            }
            if (isGrippedByTongs && tongsController != null)
            {
                tongsController.SetBlockClampedPose(false);
            }
        }
    }
}
