using UnityEngine;

namespace LiquidSolidPrototype.Visuals
{
    /// <summary>
    /// Controls the visual representation of the granular refractory mixture stream
    /// discharging from the hopper's Mixture_Outlet chute into the active MoldCast cavity.
    /// Renders a cascade of 3D irregular faceted refractory grain clusters combined with
    /// crisp alpha-clipped refractory grain particles strictly bounded to terminate on the
    /// rising mould fill surface with zero wall bleed or floor overflow.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class HopperStreamVisual : MonoBehaviour
    {
        [Header("Material & Appearance")]
        [SerializeField] private Material streamMaterial;
        [Tooltip("Base radius along X of the granular discharge stream at full opening.")]
        [SerializeField] private float baseRadius = 0.044f;
        [Tooltip("Slot-fitted radius along Z so stream enters the 109.6mm MoldCast cavity cleanly.")]
        [SerializeField] private float slotRadiusZ = 0.032f;
        [Tooltip("Fallback length of the discharge stream below the nozzle chute.")]
        [SerializeField] private float streamLength = 0.36f;
        [SerializeField] private int radialSegments = 10;
        [SerializeField] private int heightSegments = 14;

        [Header("Granular Motion & Velocity")]
        [Tooltip("Downward grain cascade speed (cycles/sec) simulating falling refractory granules.")]
        [SerializeField] private float textureScrollSpeed = 4.2f;
        [Tooltip("Turbulent radial noise amplitude creating irregular grain cluster offsets.")]
        [SerializeField] private float granularEdgeTurbulence = 0.008f;

        [Header("Granular Particle Cascade")]
        [SerializeField] private bool enableParticleCascade = true;
        [SerializeField] private int maxParticles = 180;
        [SerializeField] private float maxParticleEmissionRate = 220f;
        [SerializeField] private Vector2 particleSizeRange = new Vector2(0.008f, 0.018f);
        [SerializeField] private Material particleMaterial;

        private const int GrainClusterCount = 56;
        private const int VertsPerGrain = 12; // Faceted irregular tetrahedron (4 faces * 3 unshared verts for crisp flat-shaded facets)
        private const int TrisPerGrain = 12;

        private struct GrainClusterTemplate
        {
            public float basePhase;
            public float speedScale;
            public Vector2 radialOffset;
            public Vector3 sizeXYZ;
            public Vector3 rotAngles;
            public Vector3 spinSpeed;
            public Color grainTint;
        }

        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private MaterialPropertyBlock propBlock;
        private Mesh streamMesh;
        private GrainClusterTemplate[] clusterTemplates;
        private Vector3[] dynamicVertices;
        private Vector3[] dynamicNormals;
        private Vector2[] dynamicUVs;
        private Color[] dynamicColors;

        private ParticleSystem grainParticleSystem;
        private ParticleSystem.EmissionModule particleEmission;
        private ParticleSystem.Particle[] particleBuffer;

        private bool isActive = false;
        private float currentFlowFraction = 0f;
        private float currentScrollOffset = 0f;

        private bool hasImpactTarget = false;
        private float targetImpactWorldY = 0.314f;
        private Vector3 targetCavityWorldXZ = Vector3.zero;

        public bool IsActive => isActive;
        public float StreamLength => streamLength;
        public float BaseRadius => baseRadius;
        public float CurrentFlowFraction => currentFlowFraction;

        private static readonly Vector3[] BaseTetraCorners = new Vector3[]
        {
            new Vector3( 0.00f,  0.85f,  0.00f),
            new Vector3(-0.82f, -0.55f,  0.52f),
            new Vector3( 0.78f, -0.48f,  0.46f),
            new Vector3( 0.06f, -0.52f, -0.88f)
        };

        private static readonly int[,] TetraFaceIndices = new int[,]
        {
            { 0, 1, 2 },
            { 0, 2, 3 },
            { 0, 3, 1 },
            { 1, 3, 2 }
        };

        private void Awake()
        {
            EnsureComponents();
            BuildGranularMesh();
            EnsureParticleSystem();
            SetStreamActive(false, 0f);
        }

        private void OnEnable()
        {
            EnsureComponents();
            BuildGranularMesh();
            EnsureParticleSystem();
            UpdateStreamGeometry();
        }

        private void Start()
        {
            SetStreamActive(false, 0f);
        }

        /// <summary>
        /// Sets the world-space granular surface Y and cavity centerline XZ so the stream terminates
        /// cleanly inside the active MoldCast cavity without passing through walls or floor.
        /// </summary>
        public void SetTargetImpactSurface(float impactWorldY, Vector3 cavityCenterWorldXZ)
        {
            hasImpactTarget = true;
            targetImpactWorldY = impactWorldY;
            targetCavityWorldXZ = cavityCenterWorldXZ;
        }

        public void ClearImpactTarget()
        {
            hasImpactTarget = false;
        }

        private void EnsureComponents()
        {
            meshFilter = GetComponent<MeshFilter>();
            if (meshFilter == null) meshFilter = gameObject.AddComponent<MeshFilter>();

            meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer == null) meshRenderer = gameObject.AddComponent<MeshRenderer>();

            if (streamMaterial == null)
            {
#if UNITY_EDITOR
                streamMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_RefractoryCastable.mat");
#endif
            }

            if (streamMaterial != null)
            {
                meshRenderer.sharedMaterial = streamMaterial;
            }

            if (propBlock == null)
                propBlock = new MaterialPropertyBlock();

            meshRenderer.GetPropertyBlock(propBlock);
            propBlock.SetColor("_BaseColor", new Color(0.76f, 0.73f, 0.69f, 1f));
            propBlock.SetColor("_Color", new Color(0.76f, 0.73f, 0.69f, 1f));
            propBlock.SetFloat("_Smoothness", 0.05f);
            propBlock.SetFloat("_Glossiness", 0.05f);
            propBlock.SetFloat("_Metallic", 0.0f);
            propBlock.SetFloat("_BumpScale", 1.65f);
            meshRenderer.SetPropertyBlock(propBlock);
        }

        private void BuildGranularMesh()
        {
            if (streamMesh == null)
            {
                streamMesh = new Mesh { name = "Granular_Refractory_Stream_Mesh" };
                streamMesh.MarkDynamic();
            }
            else
            {
                streamMesh.Clear();
            }

            clusterTemplates = new GrainClusterTemplate[GrainClusterCount];
            int totalVerts = GrainClusterCount * VertsPerGrain;
            int totalTris = GrainClusterCount * TrisPerGrain;

            dynamicVertices = new Vector3[totalVerts];
            dynamicNormals = new Vector3[totalVerts];
            dynamicUVs = new Vector2[totalVerts];
            dynamicColors = new Color[totalVerts];
            int[] triangles = new int[totalTris];

            Color baseGrey = new Color(0.78f, 0.75f, 0.71f, 1f);

            for (int c = 0; c < GrainClusterCount; c++)
            {
                float h1 = Frac(Mathf.Sin(c * 12.9898f + 1.23f) * 43758.5453f);
                float h2 = Frac(Mathf.Sin(c * 78.2330f + 4.56f) * 29183.1234f);
                float h3 = Frac(Mathf.Sin(c * 39.1170f + 7.89f) * 61542.9871f);
                float h4 = Frac(Mathf.Sin(c * 93.5110f + 2.41f) * 19843.6512f);

                float angle = h1 * Mathf.PI * 2f;
                float radiusFrac = Mathf.Sqrt(h2) * 0.88f;

                clusterTemplates[c] = new GrainClusterTemplate
                {
                    basePhase = (c + h3 * 0.65f) / GrainClusterCount,
                    speedScale = Mathf.Lerp(0.88f, 1.16f, h4),
                    radialOffset = new Vector2(Mathf.Cos(angle) * radiusFrac, Mathf.Sin(angle) * radiusFrac),
                    sizeXYZ = new Vector3(
                        Mathf.Lerp(0.0085f, 0.0175f, h1),
                        Mathf.Lerp(0.0095f, 0.0210f, h2),
                        Mathf.Lerp(0.0080f, 0.0165f, h3)),
                    rotAngles = new Vector3(h1 * 360f, h2 * 360f, h3 * 360f),
                    spinSpeed = new Vector3(
                        Mathf.Lerp(-240f, 240f, h2),
                        Mathf.Lerp(-240f, 240f, h3),
                        Mathf.Lerp(-240f, 240f, h4)),
                    grainTint = new Color(
                        baseGrey.r * Mathf.Lerp(0.84f, 1.12f, h4),
                        baseGrey.g * Mathf.Lerp(0.84f, 1.12f, h4),
                        baseGrey.b * Mathf.Lerp(0.84f, 1.12f, h4),
                        1f)
                };

                int vBase = c * VertsPerGrain;
                int tBase = c * TrisPerGrain;

                for (int f = 0; f < 4; f++)
                {
                    int fv = vBase + f * 3;
                    dynamicUVs[fv + 0] = new Vector2((h1 + 0.0f) * 0.5f, (h2 + 0.0f) * 0.5f);
                    dynamicUVs[fv + 1] = new Vector2((h1 + 0.4f) * 0.5f, (h2 + 0.1f) * 0.5f);
                    dynamicUVs[fv + 2] = new Vector2((h1 + 0.2f) * 0.5f, (h2 + 0.4f) * 0.5f);

                    dynamicColors[fv + 0] = clusterTemplates[c].grainTint;
                    dynamicColors[fv + 1] = clusterTemplates[c].grainTint;
                    dynamicColors[fv + 2] = clusterTemplates[c].grainTint;

                    triangles[tBase + f * 3 + 0] = fv + 0;
                    triangles[tBase + f * 3 + 1] = fv + 1;
                    triangles[tBase + f * 3 + 2] = fv + 2;
                }
            }

            UpdateStreamGeometry();

            streamMesh.vertices = dynamicVertices;
            streamMesh.normals = dynamicNormals;
            streamMesh.uv = dynamicUVs;
            streamMesh.colors = dynamicColors;
            streamMesh.triangles = triangles;
            streamMesh.RecalculateBounds();

            meshFilter.sharedMesh = streamMesh;
        }

        private static float Frac(float x) => x - Mathf.Floor(x);

        private void EnsureParticleSystem()
        {
            if (!enableParticleCascade) return;

            Transform childTr = transform.Find("Discharge_Grains");
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform c = transform.GetChild(i);
                if (c.name == "Discharge_Grains" && childTr != null && c != childTr)
                {
                    if (Application.isPlaying) Destroy(c.gameObject);
                    else DestroyImmediate(c.gameObject);
                }
            }

            GameObject pGo;
            if (childTr == null)
            {
                pGo = new GameObject("Discharge_Grains");
                pGo.transform.SetParent(transform, false);
                pGo.transform.localPosition = new Vector3(0f, -0.01f, 0f);
                pGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            }
            else
            {
                pGo = childTr.gameObject;
            }

            grainParticleSystem = pGo.GetComponent<ParticleSystem>();
            if (grainParticleSystem == null) grainParticleSystem = pGo.AddComponent<ParticleSystem>();

            var main = grainParticleSystem.main;
            main.playOnAwake = false;
            main.loop = true;
            main.maxParticles = maxParticles;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.14f, 0.22f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.6f, 2.3f);
            main.startSize = new ParticleSystem.MinMaxCurve(particleSizeRange.x, particleSizeRange.y);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.68f, 0.65f, 0.61f, 1f),
                new Color(0.84f, 0.81f, 0.77f, 1f));
            main.gravityModifier = 1.0f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;

            var shape = grainParticleSystem.shape;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.020f; // Tightly collimated inside the 109.6mm cavity opening

            particleEmission = grainParticleSystem.emission;
            particleEmission.rateOverTime = 0f;

            if (particleMaterial == null)
            {
#if UNITY_EDITOR
                particleMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_RefractoryGrain_Particle.mat");
#endif
            }

            var pRenderer = pGo.GetComponent<ParticleSystemRenderer>();
            if (pRenderer != null)
            {
                pRenderer.sharedMaterial = particleMaterial != null ? particleMaterial : streamMaterial;
                pRenderer.renderMode = ParticleSystemRenderMode.Billboard;
                pRenderer.alignment = ParticleSystemRenderSpace.View;
            }
        }

        private void Update()
        {
            if (!isActive || streamMesh == null) return;

            currentScrollOffset += Time.deltaTime * textureScrollSpeed;
            if (currentScrollOffset > 1000f) currentScrollOffset -= 1000f;

            UpdateStreamGeometry();

            if (grainParticleSystem != null)
            {
                UpdateParticleDropLifetime();
                float targetRate = currentFlowFraction * maxParticleEmissionRate;
                particleEmission.rateOverTime = targetRate;

                if (!grainParticleSystem.isPlaying && targetRate > 0.01f)
                {
                    grainParticleSystem.Play();
                }
            }
        }

        private void LateUpdate()
        {
            if (!isActive || grainParticleSystem == null || !grainParticleSystem.isPlaying)
                return;

            ClampParticlesToStreamCorridor();
        }

        private void ClampParticlesToStreamCorridor()
        {
            int maxCount = grainParticleSystem.main.maxParticles;
            if (particleBuffer == null || particleBuffer.Length < maxCount)
                particleBuffer = new ParticleSystem.Particle[Mathf.Max(maxCount, 256)];

            int alive = grainParticleSystem.GetParticles(particleBuffer);
            if (alive <= 0) return;

            float minAllowedY = hasImpactTarget
                ? targetImpactWorldY + 0.005f
                : transform.position.y - streamLength;

            Vector3 startPos = transform.position;
            Vector3 endPos = hasImpactTarget
                ? new Vector3(targetCavityWorldXZ.x, minAllowedY, targetCavityWorldXZ.z)
                : new Vector3(startPos.x, minAllowedY, startPos.z);

            bool modified = false;
            for (int i = 0; i < alive; i++)
            {
                Vector3 pos = particleBuffer[i].position;
                if (pos.y <= minAllowedY)
                {
                    particleBuffer[i].remainingLifetime = -0.01f;
                    modified = true;
                    continue;
                }

                if (hasImpactTarget)
                {
                    float dropFrac = Mathf.Clamp01((startPos.y - pos.y) / Mathf.Max(0.05f, startPos.y - minAllowedY));
                    Vector3 centerAtY = Vector3.Lerp(startPos, endPos, dropFrac);
                    float maxR = Mathf.Lerp(baseRadius, slotRadiusZ * 0.9f, dropFrac);
                    float dx = Mathf.Clamp(pos.x - centerAtY.x, -maxR, maxR);
                    float dz = Mathf.Clamp(pos.z - centerAtY.z, -slotRadiusZ * 0.85f, slotRadiusZ * 0.85f);
                    particleBuffer[i].position = new Vector3(centerAtY.x + dx, pos.y, centerAtY.z + dz);
                    modified = true;
                }
            }

            if (modified)
            {
                grainParticleSystem.SetParticles(particleBuffer, alive);
            }
        }

        private void UpdateParticleDropLifetime()
        {
            if (grainParticleSystem == null) return;

            float dropMeters = hasImpactTarget
                ? Mathf.Clamp(transform.position.y - targetImpactWorldY - 0.015f, 0.05f, 1.40f)
                : streamLength;
            const float v0 = 2.3f;
            const float g = 9.81f;
            float maxLife = (-v0 + Mathf.Sqrt(v0 * v0 + 2f * g * dropMeters)) / g;
            maxLife = Mathf.Clamp(maxLife, 0.04f, 0.40f);

            var main = grainParticleSystem.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(maxLife * 0.70f, maxLife);
        }

        private void UpdateStreamGeometry()
        {
            if (streamMesh == null || clusterTemplates == null || dynamicVertices == null) return;

            Vector3 lossy = transform.lossyScale;
            float invSx = Mathf.Abs(lossy.x) > 0.0001f ? 1f / Mathf.Abs(lossy.x) : 1f;
            float invSy = Mathf.Abs(lossy.y) > 0.0001f ? 1f / Mathf.Abs(lossy.y) : 1f;
            float invSz = Mathf.Abs(lossy.z) > 0.0001f ? 1f / Mathf.Abs(lossy.z) : 1f;

            float worldDropLength = streamLength;
            Vector3 localTargetShift = Vector3.zero;
            if (hasImpactTarget)
            {
                worldDropLength = Mathf.Clamp(transform.position.y - targetImpactWorldY - 0.006f, 0.05f, 1.45f);
                Vector3 worldBottom = new Vector3(targetCavityWorldXZ.x, targetImpactWorldY, targetCavityWorldXZ.z);
                Vector3 localBottom = transform.InverseTransformPoint(worldBottom);
                localTargetShift = new Vector3(localBottom.x, 0f, localBottom.z);
            }

            float flowScale = Mathf.Lerp(0.45f, 1.00f, Mathf.Max(0.15f, currentFlowFraction));
            float rX = Mathf.Min(baseRadius, 0.044f) * flowScale;
            float rZ = Mathf.Min(slotRadiusZ, 0.032f) * flowScale;

            float time = Application.isPlaying ? Time.time : 0.35f;
            int activeClusterLimit = Mathf.Clamp(Mathf.CeilToInt(GrainClusterCount * Mathf.Lerp(0.35f, 1f, currentFlowFraction)), 12, GrainClusterCount);

            Vector3[] transformedCorners = new Vector3[4];

            for (int c = 0; c < GrainClusterCount; c++)
            {
                int vBase = c * VertsPerGrain;
                if (c >= activeClusterLimit)
                {
                    for (int k = 0; k < VertsPerGrain; k++)
                    {
                        dynamicVertices[vBase + k] = Vector3.zero;
                        dynamicNormals[vBase + k] = Vector3.up;
                    }
                    continue;
                }

                GrainClusterTemplate tpl = clusterTemplates[c];
                float vFrac = Frac(tpl.basePhase + currentScrollOffset * tpl.speedScale);
                // Accelerate downward with gravity profile (vFrac^1.25)
                float dropT = Mathf.Pow(vFrac, 1.22f);
                float bendFrac = Mathf.SmoothStep(0f, 1f, dropT);
                float narrow = Mathf.Lerp(0.95f, 0.72f, dropT);

                float jitterX = Mathf.Sin(time * 9f + c * 2.1f) * granularEdgeTurbulence;
                float jitterZ = Mathf.Cos(time * 8f + c * 3.4f) * granularEdgeTurbulence * 0.6f;

                Vector3 centerLocal = new Vector3(
                    localTargetShift.x * bendFrac + (tpl.radialOffset.x * rX * narrow + jitterX) * invSx,
                    -dropT * worldDropLength * invSy,
                    localTargetShift.z * bendFrac + (tpl.radialOffset.y * rZ * narrow + jitterZ) * invSz
                );

                Quaternion rot = Quaternion.Euler(tpl.rotAngles + tpl.spinSpeed * time);
                Vector3 grainScale = new Vector3(
                    tpl.sizeXYZ.x * invSx,
                    tpl.sizeXYZ.y * Mathf.Lerp(1.0f, 1.35f, dropT) * invSy,
                    tpl.sizeXYZ.z * invSz);

                for (int corner = 0; corner < 4; corner++)
                {
                    Vector3 sc = Vector3.Scale(BaseTetraCorners[corner], grainScale);
                    transformedCorners[corner] = centerLocal + (rot * sc);
                }

                for (int f = 0; f < 4; f++)
                {
                    Vector3 p0 = transformedCorners[TetraFaceIndices[f, 0]];
                    Vector3 p1 = transformedCorners[TetraFaceIndices[f, 1]];
                    Vector3 p2 = transformedCorners[TetraFaceIndices[f, 2]];
                    Vector3 fn = Vector3.Cross(p1 - p0, p2 - p0).normalized;

                    int fv = vBase + f * 3;
                    dynamicVertices[fv + 0] = p0;
                    dynamicVertices[fv + 1] = p1;
                    dynamicVertices[fv + 2] = p2;

                    dynamicNormals[fv + 0] = fn;
                    dynamicNormals[fv + 1] = fn;
                    dynamicNormals[fv + 2] = fn;
                }
            }

            streamMesh.vertices = dynamicVertices;
            streamMesh.normals = dynamicNormals;
            streamMesh.RecalculateBounds();
        }

        public void SetStreamActive(bool active, float flowFraction = 1f)
        {
            if (meshFilter == null || streamMesh == null || clusterTemplates == null)
            {
                EnsureComponents();
                BuildGranularMesh();
                EnsureParticleSystem();
            }

            isActive = active;
            currentFlowFraction = Mathf.Clamp01(flowFraction);

            if (meshRenderer != null)
            {
                meshRenderer.enabled = active && currentFlowFraction > 0.01f;
            }

            if (grainParticleSystem != null)
            {
                if (active && currentFlowFraction > 0.01f)
                {
                    UpdateParticleDropLifetime();
                    particleEmission.rateOverTime = currentFlowFraction * maxParticleEmissionRate;
                    if (!grainParticleSystem.isPlaying)
                    {
                        grainParticleSystem.Play();
                    }
                    if (!Application.isPlaying)
                    {
                        grainParticleSystem.Simulate(0.14f, true, true);
                    }
                }
                else
                {
                    particleEmission.rateOverTime = 0f;
                    grainParticleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
            }

            if (isActive && streamMesh != null)
            {
                UpdateStreamGeometry();
            }
        }
    }
}
