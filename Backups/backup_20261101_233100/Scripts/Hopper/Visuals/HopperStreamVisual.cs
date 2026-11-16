using System;
using UnityEngine;

namespace LiquidSolidPrototype.Visuals
{
    /// <summary>
    /// Controls the visual representation of the dense, continuous granular refractory stream
    /// discharging from the hopper's Mixture_Outlet chute.
    /// Combines a high-density textured granular core column with procedural UV velocity scrolling
    /// and a bounded aggregate particle cascade for authentic bulk material tumbling.
    /// Progressively scales with gate opening fraction and cuts off immediately upon closure or empty state.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class HopperStreamVisual : MonoBehaviour
    {
        [Header("Material & Appearance")]
        [SerializeField] private Material streamMaterial;
        [Tooltip("Base radius of the granular discharge stream at full opening.")]
        [SerializeField] private float baseRadius = 0.082f;
        [Tooltip("Length of the discharge stream below the nozzle chute.")]
        [SerializeField] private float streamLength = 0.38f;
        [SerializeField] private int radialSegments = 14;
        [SerializeField] private int heightSegments = 10;

        [Header("Granular Motion & Velocity")]
        [Tooltip("Downward texture scroll speed simulating falling bulk granules.")]
        [SerializeField] private float textureScrollSpeed = 7.5f;
        [Tooltip("Turbulent radial noise amplitude creating ragged granular edges.")]
        [SerializeField] private float granularEdgeTurbulence = 0.012f;

        [Header("Granular Particle Cascade")]
        [SerializeField] private bool enableParticleCascade = true;
        [SerializeField] private int maxParticles = 300;
        [SerializeField] private float maxParticleEmissionRate = 350f;
        [SerializeField] private Vector2 particleSizeRange = new Vector2(0.010f, 0.024f);
        [SerializeField] private Material particleMaterial;

        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private Mesh streamMesh;
        private Vector3[] baseVertices;
        private Vector3[] dynamicVertices;
        private Vector2[] baseUVs;
        private Vector2[] dynamicUVs;
        private MaterialPropertyBlock propBlock;

        private ParticleSystem grainParticleSystem;
        private ParticleSystem.EmissionModule particleEmission;

        private bool isActive = false;
        private float currentFlowFraction = 0f;
        private float currentScrollOffset = 0f;

        public bool IsActive => isActive;
        public float StreamLength => streamLength;
        public float BaseRadius => baseRadius;
        public float CurrentFlowFraction => currentFlowFraction;

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

        private void EnsureComponents()
        {
            meshFilter = GetComponent<MeshFilter>();
            if (meshFilter == null) meshFilter = gameObject.AddComponent<MeshFilter>();

            meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer == null) meshRenderer = gameObject.AddComponent<MeshRenderer>();

            propBlock = new MaterialPropertyBlock();

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
        }

        private void BuildGranularMesh()
        {
            streamMesh = new Mesh { name = "Granular_Refractory_Stream_Mesh" };
            streamMesh.MarkDynamic();

            int vertCount = (heightSegments + 1) * (radialSegments + 1);
            baseVertices = new Vector3[vertCount];
            dynamicVertices = new Vector3[vertCount];
            baseUVs = new Vector2[vertCount];
            dynamicUVs = new Vector2[vertCount];
            int[] triangles = new int[heightSegments * radialSegments * 6];

            int vertIdx = 0;
            for (int y = 0; y <= heightSegments; y++)
            {
                float v = (float)y / heightSegments;
                float currentY = -v * streamLength;

                // Gravitational acceleration narrowing: free-fall stream narrows slightly as it accelerates
                // v = sqrt(2 * g * y), cross section area A ~ 1/v
                float gravityNarrow = Mathf.Lerp(1.0f, 0.82f, Mathf.Sqrt(Mathf.Max(0.01f, v)));

                for (int x = 0; x <= radialSegments; x++)
                {
                    float u = (float)x / radialSegments;
                    float angle = u * Mathf.PI * 2f;

                    // Pseudo-random granular edge perturbation (aggregate bumps and crevices)
                    float bump1 = Mathf.Sin(angle * 3f + v * 8f) * 0.4f;
                    float bump2 = Mathf.Cos(angle * 7f - v * 14f) * 0.3f;
                    float bump3 = Mathf.Sin(angle * 11f + v * 21f) * 0.2f;
                    float granularFactor = 1.0f + (bump1 + bump2 + bump3) * 0.20f;

                    float vx = Mathf.Cos(angle) * baseRadius * gravityNarrow * granularFactor;
                    float vz = Mathf.Sin(angle) * baseRadius * gravityNarrow * granularFactor;

                    baseVertices[vertIdx] = new Vector3(vx, currentY, vz);
                    dynamicVertices[vertIdx] = baseVertices[vertIdx];
                    baseUVs[vertIdx] = new Vector2(u * 2.5f, v * 3.5f);
                    dynamicUVs[vertIdx] = baseUVs[vertIdx];
                    vertIdx++;
                }
            }

            int triIdx = 0;
            for (int y = 0; y < heightSegments; y++)
            {
                for (int x = 0; x < radialSegments; x++)
                {
                    int current = y * (radialSegments + 1) + x;
                    int next = current + (radialSegments + 1);

                    triangles[triIdx++] = current;
                    triangles[triIdx++] = next;
                    triangles[triIdx++] = current + 1;

                    triangles[triIdx++] = next;
                    triangles[triIdx++] = next + 1;
                    triangles[triIdx++] = current + 1;
                }
            }

            streamMesh.vertices = baseVertices;
            streamMesh.uv = baseUVs;
            streamMesh.triangles = triangles;
            streamMesh.RecalculateNormals();
            streamMesh.RecalculateTangents();
            streamMesh.RecalculateBounds();

            meshFilter.sharedMesh = streamMesh;
        }

        private void EnsureParticleSystem()
        {
            if (!enableParticleCascade) return;

            Transform childTr = transform.Find("Discharge_Grains");
            // Remove any duplicate children if present
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
                pGo.transform.localPosition = new Vector3(0f, 0.02f, 0f);
                pGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); // Point downwards
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
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.20f, 0.32f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.8f, 2.8f);
            main.startSize = new ParticleSystem.MinMaxCurve(particleSizeRange.x, particleSizeRange.y);
            main.gravityModifier = 1.0f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;

            var shape = grainParticleSystem.shape;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = baseRadius * 0.45f;

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

            // 1. Dynamic downward texture scrolling simulating cascading bulk material
            currentScrollOffset += Time.deltaTime * textureScrollSpeed;
            if (currentScrollOffset > 1000f) currentScrollOffset -= 1000f;

            // 2. Responsive granular column width & edge perturbation
            UpdateStreamGeometry();

            // 3. Dynamic Particle Cascade Emission
            if (grainParticleSystem != null)
            {
                float targetRate = currentFlowFraction * maxParticleEmissionRate;
                particleEmission.rateOverTime = targetRate;

                if (!grainParticleSystem.isPlaying && targetRate > 0.01f)
                {
                    grainParticleSystem.Play();
                }
            }
        }

        private void UpdateStreamGeometry()
        {
            if (streamMesh == null || baseVertices == null) return;

            float flowScale = Mathf.Lerp(0.30f, 1.05f, currentFlowFraction);
            float time = Application.isPlaying ? Time.time * 12f : 1.5f;
            int vertCount = baseVertices.Length;

            for (int i = 0; i < vertCount; i++)
            {
                Vector3 bp = baseVertices[i];
                float heightRatio = -bp.y / streamLength;

                // Dynamic jitter representing tumbling granular surface clumping
                float jitter = Mathf.Sin(time + heightRatio * 15f + bp.x * 20f) * granularEdgeTurbulence * currentFlowFraction;

                dynamicVertices[i] = new Vector3(
                    bp.x * flowScale + (bp.x > 0 ? jitter : -jitter),
                    bp.y,
                    bp.z * flowScale + (bp.z > 0 ? jitter * 0.7f : -jitter * 0.7f)
                );

                dynamicUVs[i] = new Vector2(baseUVs[i].x, baseUVs[i].y + currentScrollOffset);
            }

            streamMesh.vertices = dynamicVertices;
            streamMesh.uv = dynamicUVs;
            streamMesh.RecalculateNormals();
            streamMesh.RecalculateBounds();
        }

        /// <summary>
        /// Enables or disables the granular stream visual and scales emission and thickness with gate flow.
        /// When active=false, shuts off spawning immediately.
        /// </summary>
        public void SetStreamActive(bool active, float flowFraction = 1f)
        {
            if (meshFilter == null || streamMesh == null)
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
                    particleEmission.rateOverTime = currentFlowFraction * maxParticleEmissionRate;
                    if (!grainParticleSystem.isPlaying)
                    {
                        grainParticleSystem.Play();
                    }
                    if (!Application.isPlaying)
                    {
                        grainParticleSystem.Simulate(0.18f, true, true);
                    }
                }
                else
                {
                    particleEmission.rateOverTime = 0f;
                    grainParticleSystem.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                    if (!Application.isPlaying)
                    {
                        grainParticleSystem.Clear();
                    }
                }
            }

            if (isActive && streamMesh != null)
            {
                UpdateStreamGeometry();
            }
        }
    }
}
