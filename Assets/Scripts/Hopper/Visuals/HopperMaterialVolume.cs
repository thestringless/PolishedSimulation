using System;
using UnityEngine;

namespace LiquidSolidPrototype.Visuals
{
    /// <summary>
    /// Generates and manages the dynamic 3D granular refractory castable material pile inside the SEFPRO Vibrocasting Hopper.
    /// Conforms accurately to the hopper's inner collar and tapering funnel walls without wall penetration or rim floating.
    /// Features an unmistakable three-dimensional heap silhouette with an asymmetric crest, naturally sloping sides
    /// (angle of repose) extending to the walls, multi-frequency aggregate clumping, realistic local contours,
    /// and authentic center-funnel discharge drawdown.
    /// Fully visible in Edit Mode and runtime.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class HopperMaterialVolume : MonoBehaviour
    {
        [Header("Fill Settings")]
        [Range(0f, 1f)]
        [SerializeField] private float fillFraction = 1f;

        [Header("Material Profile In Local Space")]
        [Tooltip("Minimum local Z (bottom throat opening).")]
        [SerializeField] private float minZ = 0.68f;
        [Tooltip("Maximum local Z (base fill line along collar walls at full capacity).")]
        [SerializeField] private float maxZ = 1.238f;
        [Tooltip("Z height where funnel meets the vertical top collar.")]
        [SerializeField] private float collarZ = 1.15f;

        [Tooltip("Half-width X at throat vs collar.")]
        [SerializeField] private Vector2 halfWidthX = new Vector2(0.235f, 0.512f);
        [Tooltip("Y range at throat (minY, maxY).")]
        [SerializeField] private Vector2 throatY = new Vector2(0.045f, 0.335f);
        [Tooltip("Y range at collar (minY, maxY).")]
        [SerializeField] private Vector2 collarY = new Vector2(-0.415f, 0.415f);

        [Header("Granular Mixture Pile Geometry")]
        [Range(16, 40)]
        [SerializeField] private int gridSegments = 28;
        [Tooltip("Height of the central aggregate heap at full capacity (meters).")]
        [SerializeField] private float moundHeight = 0.145f;
        [Tooltip("Horizontal offset of the deposition peak for realistic chute asymmetry (meters X, Y).")]
        [SerializeField] private Vector2 peakOffset = new Vector2(-0.025f, 0.030f);
        [Tooltip("Slope exponent controlling angle of repose profile (1.0 = linear cone, >1.0 = rounded crest).")]
        [SerializeField] private float slopeExponent = 1.15f;
        [Tooltip("Width of boundary transition zone against hopper walls (meters).")]
        [SerializeField] private float wallMargin = 0.032f;
        [Tooltip("Magnitude of aggregate clumps (damp cohesive material chunks 2-8 cm).")]
        [SerializeField] private float clumpMagnitude = 0.026f;
        [Tooltip("Magnitude of macro settling terraces and deposition drift.")]
        [SerializeField] private float macroContourMagnitude = 0.018f;
        [Tooltip("Fine granular surface rugosity.")]
        [SerializeField] private float fineGranularMagnitude = 0.0085f;

        [Header("Discharge Drawdown Dynamics")]
        [Tooltip("Maximum depth of the central drawdown funnel as material empties.")]
        [SerializeField] private float maxDrawdownDepth = 0.16f;

        private Mesh mesh;
        private MeshRenderer meshRenderer;
        private MaterialPropertyBlock propBlock;

        public float FillFraction => fillFraction;
        public bool IsEmpty => fillFraction <= 0.005f;

        private void Awake()
        {
            EnsureMesh();
        }

        private void OnEnable()
        {
            EnsureMesh();
        }

        private void EnsureMesh()
        {
            meshRenderer = GetComponent<MeshRenderer>();
            var meshFilter = GetComponent<MeshFilter>();
            if (mesh == null)
            {
                mesh = new Mesh { name = "Hopper_Material_Mesh" };
                mesh.MarkDynamic();
            }
            if (meshFilter != null)
            {
                meshFilter.sharedMesh = mesh;
            }
            ApplyGranularMaterialAppearance();
            RebuildMesh();
        }

        private void ApplyGranularMaterialAppearance()
        {
            if (meshRenderer == null)
                meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer == null)
                return;

            if (propBlock == null)
                propBlock = new MaterialPropertyBlock();

            meshRenderer.GetPropertyBlock(propBlock);
            propBlock.SetColor("_BaseColor", new Color(0.78f, 0.75f, 0.71f, 1f));
            propBlock.SetColor("_Color", new Color(0.78f, 0.75f, 0.71f, 1f));
            propBlock.SetFloat("_Smoothness", 0.05f);
            propBlock.SetFloat("_Glossiness", 0.05f);
            propBlock.SetFloat("_Metallic", 0.0f);
            propBlock.SetFloat("_BumpScale", 1.7f);
            meshRenderer.SetPropertyBlock(propBlock);
        }

        /// <summary>
        /// Updates the current fill fraction (0.0 = empty, 1.0 = full).
        /// </summary>
        public void SetFillAmount(float fraction)
        {
            fillFraction = Mathf.Clamp01(fraction);
            if (meshRenderer != null)
            {
                meshRenderer.enabled = fillFraction > 0.005f;
            }

            if (meshRenderer != null && meshRenderer.enabled)
            {
                UpdateVertices();
            }
        }

        public void SetFillFraction(float fraction)
        {
            SetFillAmount(fraction);
        }

        public void ResetFill()
        {
            SetFillAmount(1.0f);
        }

        private void OnValidate()
        {
            if (mesh != null)
            {
                RebuildMesh();
            }
        }

        private void CalculateBoundsAtHeight(float z, out float halfX, out float minY, out float maxY)
        {
            float u = Mathf.Clamp01((z - minZ) / (collarZ - minZ));
            halfX = Mathf.Lerp(halfWidthX.x, halfWidthX.y, u);
            minY = Mathf.Lerp(throatY.x, collarY.x, u);
            maxY = Mathf.Lerp(throatY.y, collarY.y, u);
        }

        private float CalculateSurfaceRelief(float u, float v, float x, float y, float halfX, float minY, float maxY, float curZ)
        {
            // Calculate distance to nearest hopper wall in local meters
            float distToLeft = x - (-halfX);
            float distToRight = halfX - x;
            float distToFront = y - minY;
            float distToBack = maxY - y;
            float distToWall = Mathf.Min(Mathf.Min(distToLeft, distToRight), Mathf.Min(distToFront, distToBack));

            if (distToWall <= 0.0001f) return 0f;

            // Boundary damping: smoothly brings relief to 0 at the walls within wallMargin
            float wallDamp = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(distToWall / wallMargin));

            // Peak coordinates with chute deposition asymmetry
            float peakX = peakOffset.x;
            float peakY = Mathf.Lerp(minY, maxY, 0.5f) + peakOffset.y;

            // Directional rotation for deposition crest (subtle angle mimicking chute flow)
            float dx = x - peakX;
            float dy = y - peakY;
            float rotX = dx * 0.92f - dy * 0.39f;
            float rotY = dx * 0.39f + dy * 0.92f;

            // Scaled radial distance accounting for rectangular hopper proportions
            float distFromPeak = Mathf.Sqrt(rotX * rotX * 1.0f + rotY * rotY * 1.30f);

            // Natural angle-of-repose heap silhouette
            float maxHeapRadius = Mathf.Max(halfX, (maxY - minY) * 0.5f) * 1.25f;
            float normDist = Mathf.Clamp01(distFromPeak / maxHeapRadius);
            float heapSlope = Mathf.Clamp01(1.0f - Mathf.Pow(normDist, slopeExponent));
            float heapMound = heapSlope * moundHeight;

            // Chute deposition ridge / crest along flow axis
            float ridge = Mathf.Exp(-Mathf.Pow(rotX * 3.5f, 2f)) * 0.022f * Mathf.Clamp01(1.0f - normDist * 1.5f);

            // Layered procedural noise: Macro slump terraces and settling contours
            float macro1 = (Mathf.PerlinNoise(u * 3.5f + 2.3f, v * 3.5f + 1.9f) - 0.5f);
            float macro2 = (Mathf.PerlinNoise(u * 6.8f + 8.1f, v * 6.8f + 5.2f) - 0.5f);
            float macroContour = (macro1 * 0.7f + macro2 * 0.3f) * macroContourMagnitude;

            // Layered procedural noise: Medium aggregate clumps (cohesive damp refractory lumps 2-8 cm)
            float clump1 = (Mathf.PerlinNoise(u * 12.0f + 11.4f, v * 12.0f + 13.8f) - 0.5f);
            float clump2 = (Mathf.PerlinNoise(u * 22.0f + 6.3f, v * 22.0f + 9.1f) - 0.5f);
            float clump3 = (Mathf.PerlinNoise(u * 36.0f + 17.5f, v * 36.0f + 21.2f) - 0.5f);
            float aggregateClumps = (clump1 * 0.55f + clump2 * 0.30f + clump3 * 0.15f) * clumpMagnitude;

            // Fine granular rugosity (multi-scale irregular mineral grain peaks)
            float g1 = Mathf.PerlinNoise(u * 48.0f + 3.7f, v * 48.0f + 9.2f) - 0.5f;
            float g2 = Mathf.PerlinNoise(u * 82.0f + 19.1f, v * 82.0f + 4.6f) - 0.5f;
            float fineGranular = (g1 * 0.65f + g2 * 0.35f) * 2.2f * fineGranularMagnitude;

            // Combine full pile relief with wall containment
            float fullPile = (heapMound + ridge + macroContour + aggregateClumps + fineGranular) * wallDamp;

            // Discharge drawdown dynamics:
            // As fillFraction decreases, the initial mound collapses and material draws down into the throat
            float throatCenterX = 0f;
            float throatCenterY = (throatY.x + throatY.y) * 0.5f;
            float dtx = (x - throatCenterX);
            float dty = (y - throatCenterY) * 1.25f;
            float distFromThroat = Mathf.Sqrt(dtx * dtx + dty * dty);
            float funnelProfile = Mathf.Clamp01(1.0f - Mathf.Pow(Mathf.Clamp01(distFromThroat / 0.50f), 1.35f));

            float moundScale = Mathf.Pow(fillFraction, 1.35f);
            float dischargeProgress = 1f - fillFraction;
            float drawdown = dischargeProgress * funnelProfile * wallDamp * maxDrawdownDepth;

            return (fullPile * moundScale) - drawdown;
        }

        private void RebuildMesh()
        {
            if (mesh == null) return;
            mesh.Clear();

            int seg = gridSegments;
            int numTopVerts = (seg + 1) * (seg + 1);
            int totalVerts = numTopVerts + 16 + 4; // top surface + 4 side quads + bottom cap

            int numTopTris = seg * seg * 6;
            int totalTris = numTopTris + 24 + 6; // top surface + 4 side quads (24) + bottom cap (6)

            Vector3[] vertices = new Vector3[totalVerts];
            Vector3[] normals = new Vector3[totalVerts];
            Vector2[] uvs = new Vector2[totalVerts];
            Color[] colors = new Color[totalVerts];
            int[] triangles = new int[totalTris];

            float curZ = Mathf.Lerp(minZ, maxZ, fillFraction);
            CalculateBoundsAtHeight(curZ, out float halfX, out float minY, out float maxY);

            Color warmGrey = new Color(0.93f, 0.91f, 0.88f, 1f);

            int vIdx = 0;
            for (int j = 0; j <= seg; j++)
            {
                float v = (float)j / seg;
                float y = Mathf.Lerp(minY, maxY, v);
                for (int i = 0; i <= seg; i++)
                {
                    float u = (float)i / seg;
                    float x = Mathf.Lerp(-halfX, halfX, u);
                    float relief = CalculateSurfaceRelief(u, v, x, y, halfX, minY, maxY, curZ);

                    vertices[vIdx] = new Vector3(x, y, curZ + relief);
                    normals[vIdx] = Vector3.forward;
                    uvs[vIdx] = new Vector2(u * 3.5f, v * 3.5f);

                    float shading = Mathf.Clamp(1.0f + relief * 1.3f, 0.85f, 1.15f);
                    colors[vIdx] = new Color(warmGrey.r * shading, warmGrey.g * shading, warmGrey.b * shading, 1f);
                    vIdx++;
                }
            }

            // Top surface triangulation
            int tIdx = 0;
            for (int j = 0; j < seg; j++)
            {
                for (int i = 0; i < seg; i++)
                {
                    int row1 = j * (seg + 1);
                    int row2 = (j + 1) * (seg + 1);

                    triangles[tIdx++] = row1 + i;
                    triangles[tIdx++] = row1 + i + 1;
                    triangles[tIdx++] = row2 + i + 1;

                    triangles[tIdx++] = row1 + i;
                    triangles[tIdx++] = row2 + i + 1;
                    triangles[tIdx++] = row2 + i;
                }
            }

            // Side walls and bottom cap for complete closed volume inside hopper
            float botHalfX = halfWidthX.x;
            float botMinY = throatY.x;
            float botMaxY = throatY.y;

            Vector3 tBL = new Vector3(-halfX, minY, curZ);
            Vector3 tBR = new Vector3(halfX, minY, curZ);
            Vector3 tTR = new Vector3(halfX, maxY, curZ);
            Vector3 tTL = new Vector3(-halfX, maxY, curZ);

            Vector3 bBL = new Vector3(-botHalfX, botMinY, minZ);
            Vector3 bBR = new Vector3(botHalfX, botMinY, minZ);
            Vector3 bTR = new Vector3(botHalfX, botMaxY, minZ);
            Vector3 bTL = new Vector3(-botHalfX, botMaxY, minZ);

            void AddQuad(Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl, Vector3 norm)
            {
                int baseV = vIdx;
                vertices[vIdx] = tl; normals[vIdx] = norm; uvs[vIdx] = new Vector2(0, 1); colors[vIdx] = warmGrey; vIdx++;
                vertices[vIdx] = tr; normals[vIdx] = norm; uvs[vIdx] = new Vector2(1, 1); colors[vIdx] = warmGrey; vIdx++;
                vertices[vIdx] = br; normals[vIdx] = norm; uvs[vIdx] = new Vector2(1, 0); colors[vIdx] = warmGrey; vIdx++;
                vertices[vIdx] = bl; normals[vIdx] = norm; uvs[vIdx] = new Vector2(0, 0); colors[vIdx] = warmGrey; vIdx++;

                triangles[tIdx++] = baseV;
                triangles[tIdx++] = baseV + 1;
                triangles[tIdx++] = baseV + 2;

                triangles[tIdx++] = baseV;
                triangles[tIdx++] = baseV + 2;
                triangles[tIdx++] = baseV + 3;
            }

            // Front wall (min Y)
            AddQuad(tBL, tBR, bBR, bBL, Vector3.down);
            // Right wall (max X)
            AddQuad(tBR, tTR, bTR, bBR, Vector3.right);
            // Back wall (max Y)
            AddQuad(tTR, tTL, bTL, bTR, Vector3.up);
            // Left wall (min X)
            AddQuad(tTL, tBL, bBL, bTL, Vector3.left);

            // Bottom cap
            int botBase = vIdx;
            vertices[vIdx] = bBL; normals[vIdx] = Vector3.back; uvs[vIdx] = new Vector2(0, 0); colors[vIdx] = warmGrey; vIdx++;
            vertices[vIdx] = bBR; normals[vIdx] = Vector3.back; uvs[vIdx] = new Vector2(1, 0); colors[vIdx] = warmGrey; vIdx++;
            vertices[vIdx] = bTR; normals[vIdx] = Vector3.back; uvs[vIdx] = new Vector2(1, 1); colors[vIdx] = warmGrey; vIdx++;
            vertices[vIdx] = bTL; normals[vIdx] = Vector3.back; uvs[vIdx] = new Vector2(0, 1); colors[vIdx] = warmGrey; vIdx++;

            triangles[tIdx++] = botBase;
            triangles[tIdx++] = botBase + 2;
            triangles[tIdx++] = botBase + 1;

            triangles[tIdx++] = botBase;
            triangles[tIdx++] = botBase + 3;
            triangles[tIdx++] = botBase + 2;

            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
        }

        private void UpdateVertices()
        {
            if (mesh == null) return;

            int seg = gridSegments;
            int numTopVerts = (seg + 1) * (seg + 1);
            int totalVerts = numTopVerts + 16 + 4;

            if (mesh.vertexCount != totalVerts)
            {
                RebuildMesh();
                return;
            }

            var vertices = mesh.vertices;
            var colors = mesh.colors;
            float curZ = Mathf.Lerp(minZ, maxZ, fillFraction);
            CalculateBoundsAtHeight(curZ, out float halfX, out float minY, out float maxY);

            Color warmGrey = new Color(0.93f, 0.91f, 0.88f, 1f);

            int vIdx = 0;
            for (int j = 0; j <= seg; j++)
            {
                float v = (float)j / seg;
                float y = Mathf.Lerp(minY, maxY, v);
                for (int i = 0; i <= seg; i++)
                {
                    float u = (float)i / seg;
                    float x = Mathf.Lerp(-halfX, halfX, u);
                    float relief = CalculateSurfaceRelief(u, v, x, y, halfX, minY, maxY, curZ);

                    vertices[vIdx] = new Vector3(x, y, curZ + relief);
                    if (colors != null && colors.Length > vIdx)
                    {
                        float shading = Mathf.Clamp(1.0f + relief * 1.3f, 0.85f, 1.15f);
                        colors[vIdx] = new Color(warmGrey.r * shading, warmGrey.g * shading, warmGrey.b * shading, 1f);
                    }
                    vIdx++;
                }
            }

            float botHalfX = halfWidthX.x;
            float botMinY = throatY.x;
            float botMaxY = throatY.y;

            Vector3 tBL = new Vector3(-halfX, minY, curZ);
            Vector3 tBR = new Vector3(halfX, minY, curZ);
            Vector3 tTR = new Vector3(halfX, maxY, curZ);
            Vector3 tTL = new Vector3(-halfX, maxY, curZ);

            Vector3 bBL = new Vector3(-botHalfX, botMinY, minZ);
            Vector3 bBR = new Vector3(botHalfX, botMinY, minZ);
            Vector3 bTR = new Vector3(botHalfX, botMaxY, minZ);
            Vector3 bTL = new Vector3(-botHalfX, botMaxY, minZ);

            // Front quad
            vertices[vIdx++] = tBL; vertices[vIdx++] = tBR; vertices[vIdx++] = bBR; vertices[vIdx++] = bBL;
            // Right quad
            vertices[vIdx++] = tBR; vertices[vIdx++] = tTR; vertices[vIdx++] = bTR; vertices[vIdx++] = bBR;
            // Back quad
            vertices[vIdx++] = tTR; vertices[vIdx++] = tTL; vertices[vIdx++] = bTL; vertices[vIdx++] = bTR;
            // Left quad
            vertices[vIdx++] = tTL; vertices[vIdx++] = tBL; vertices[vIdx++] = bBL; vertices[vIdx++] = bTL;
            // Bottom cap
            vertices[vIdx++] = bBL; vertices[vIdx++] = bBR; vertices[vIdx++] = bTR; vertices[vIdx++] = bTL;

            mesh.vertices = vertices;
            if (colors != null && colors.Length == vertices.Length)
            {
                mesh.colors = colors;
            }
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
        }
    }
}

