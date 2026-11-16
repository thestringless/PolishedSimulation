using UnityEngine;

namespace LiquidSolidPrototype.Visuals
{
    /// <summary>
    /// Controls the visual representation of the thick off-white refractory stream
    /// discharging from the hopper's Mixture_Outlet chute.
    /// Simulates a heavy, viscous extruded column with dynamic ripple pulsation.
    /// Synchronized with gate open fraction and automatically cuts off when closed or empty.
    /// Self-contained and does not require an external receiving mold.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class HopperStreamVisual : MonoBehaviour
    {
        [Header("Appearance")]
        [SerializeField] private Material streamMaterial;
        [Tooltip("Base radius of the viscous extruded column.")]
        [SerializeField] private float baseRadius = 0.075f;
        [Tooltip("Length of the viscous discharge column below the nozzle.")]
        [SerializeField] private float streamLength = 0.90f;
        [SerializeField] private int radialSegments = 16;
        [SerializeField] private int heightSegments = 10;

        [Header("Dynamic Flow Effect")]
        [SerializeField] private float pulseSpeed = 7f;
        [SerializeField] private float pulseMagnitude = 0.05f;

        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private Mesh streamMesh;
        private Vector3[] baseVertices;
        private Vector3[] dynamicVertices;

        private bool isActive = false;
        private float currentFlowFraction = 0f;

        public bool IsActive => isActive;
        public float StreamLength => streamLength;
        public float BaseRadius => baseRadius;

        private void Awake()
        {
            meshFilter = GetComponent<MeshFilter>();
            if (meshFilter == null) meshFilter = gameObject.AddComponent<MeshFilter>();

            meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer == null) meshRenderer = gameObject.AddComponent<MeshRenderer>();

            if (streamMaterial != null)
            {
                meshRenderer.sharedMaterial = streamMaterial;
            }

            BuildMesh();
            SetStreamActive(false, 0f);
        }

        private void Start()
        {
            SetStreamActive(false, 0f);
        }

        private void BuildMesh()
        {
            streamMesh = new Mesh { name = "Refractory_Discharge_Stream_Mesh" };
            streamMesh.MarkDynamic();

            int vertCount = (heightSegments + 1) * (radialSegments + 1);
            baseVertices = new Vector3[vertCount];
            dynamicVertices = new Vector3[vertCount];
            Vector2[] uvs = new Vector2[vertCount];
            int[] triangles = new int[heightSegments * radialSegments * 6];

            int vertIdx = 0;
            for (int y = 0; y <= heightSegments; y++)
            {
                float v = (float)y / heightSegments;
                float currentY = -v * streamLength;

                // Heavy viscous extrusion: slight taper and round teardrop termination at the bottom
                float taper = Mathf.Lerp(1.0f, 0.78f, Mathf.Pow(v, 0.7f));
                if (v > 0.88f)
                {
                    // Rounded discharge tip
                    float tipFactor = (1.0f - v) / 0.12f;
                    taper *= Mathf.SmoothStep(0.2f, 1.0f, tipFactor);
                }

                for (int x = 0; x <= radialSegments; x++)
                {
                    float u = (float)x / radialSegments;
                    float angle = u * Mathf.PI * 2f;

                    float vx = Mathf.Cos(angle) * baseRadius * taper;
                    float vz = Mathf.Sin(angle) * baseRadius * taper;

                    baseVertices[vertIdx] = new Vector3(vx, currentY, vz);
                    dynamicVertices[vertIdx] = baseVertices[vertIdx];
                    uvs[vertIdx] = new Vector2(u * 2f, v * 2f);
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
            streamMesh.uv = uvs;
            streamMesh.triangles = triangles;
            streamMesh.RecalculateNormals();
            streamMesh.RecalculateTangents();
            streamMesh.RecalculateBounds();

            meshFilter.sharedMesh = streamMesh;
        }

        private void Update()
        {
            if (!isActive || streamMesh == null) return;

            // Animate dynamic fluid waviness / pulsation
            float time = Time.time * pulseSpeed;
            int vertCount = baseVertices.Length;

            for (int i = 0; i < vertCount; i++)
            {
                Vector3 basePos = baseVertices[i];
                float heightFactor = -basePos.y / streamLength;
                float wave = Mathf.Sin(time + heightFactor * 6f) * pulseMagnitude;

                // Scale thickness with flow fraction (thicker when gate is wide open)
                float flowScale = Mathf.Lerp(0.40f, 1.10f, currentFlowFraction);
                dynamicVertices[i] = new Vector3(
                    basePos.x * flowScale * (1f + wave),
                    basePos.y,
                    basePos.z * flowScale * (1f - wave * 0.5f)
                );
            }

            streamMesh.vertices = dynamicVertices;
            streamMesh.RecalculateNormals();
        }

        /// <summary>
        /// Enables or disables the stream visual and scales its thickness with gate flow.
        /// </summary>
        public void SetStreamActive(bool active, float flowFraction = 1f)
        {
            isActive = active;
            currentFlowFraction = Mathf.Clamp01(flowFraction);

            if (meshRenderer != null)
            {
                meshRenderer.enabled = active;
            }
        }
    }
}
