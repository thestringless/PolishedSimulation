using System;
using UnityEngine;

namespace LiquidSolidPrototype.Visuals
{
    /// <summary>
    /// Generates and manages the dynamic 3D semi-solid refractory material volume inside the SEFPRO Vibrocasting Hopper.
    /// Conforms accurately to the hopper's tapering inner funnel walls without wall penetration or rim floating.
    /// Features cohesive thixotropic paste surface relief, granular texture normals, and smooth discharge drawdown.
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
        [SerializeField] private float minZ = 0.66f;
        [Tooltip("Maximum local Z (full level, below the rim).")]
        [SerializeField] private float maxZ = 1.30f;
        [Tooltip("Z height where funnel meets the vertical top collar.")]
        [SerializeField] private float collarZ = 1.20f;

        [Tooltip("Half-width X at throat vs collar.")]
        [SerializeField] private Vector2 halfWidthX = new Vector2(0.22f, 0.50f);
        [Tooltip("Y range at throat (minY, maxY).")]
        [SerializeField] private Vector2 throatY = new Vector2(0.05f, 0.32f);
        [Tooltip("Y range at collar (minY, maxY).")]
        [SerializeField] private Vector2 collarY = new Vector2(-0.38f, 0.40f);

        [Header("Surface Detail")]
        [Range(4, 16)]
        [SerializeField] private int gridSegments = 10;
        [Tooltip("Viscous paste mound height at full capacity.")]
        [SerializeField] private float pasteMoundHeight = 0.024f;
        [Tooltip("Subtle paste ripple magnitude.")]
        [SerializeField] private float pasteRippleScale = 0.006f;

        private Mesh mesh;
        private MeshRenderer meshRenderer;

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
            RebuildMesh();
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

        private float CalculateSurfaceRelief(float u, float v, float curZ)
        {
            // Cohesive paste mound (higher in middle, slopes to edges)
            float dome = Mathf.Sin(u * Mathf.PI) * Mathf.Sin(v * Mathf.PI) * pasteMoundHeight;

            // Thixotropic granular mortar ripple folds
            float ripple = (Mathf.Sin(u * Mathf.PI * 4f) * Mathf.Cos(v * Mathf.PI * 4f)) * pasteRippleScale;

            // Subtle funnel drawdown depression in the center as material discharges
            float drawdown = (1f - fillFraction) * Mathf.Sin(u * Mathf.PI) * Mathf.Sin(v * Mathf.PI) * 0.020f;

            return dome + ripple - drawdown;
        }

        private void RebuildMesh()
        {
            if (mesh == null) return;
            mesh.Clear();

            int seg = gridSegments;
            int topVertCount = (seg + 1) * (seg + 1);
            int totalVerts = topVertCount + 4 + 16;

            var vertices = new Vector3[totalVerts];
            var normals = new Vector3[totalVerts];
            var uvs = new Vector2[totalVerts];

            int triCount = (seg * seg * 6) + 24 + 6;
            var triangles = new int[triCount];

            float curZ = Mathf.Lerp(minZ, maxZ, fillFraction);
            CalculateBoundsAtHeight(curZ, out float halfX, out float minY, out float maxY);

            // 1. Top grid vertices
            int vIdx = 0;
            for (int j = 0; j <= seg; j++)
            {
                float v = (float)j / seg;
                float y = Mathf.Lerp(minY, maxY, v);
                for (int i = 0; i <= seg; i++)
                {
                    float u = (float)i / seg;
                    float x = Mathf.Lerp(-halfX, halfX, u);
                    float relief = CalculateSurfaceRelief(u, v, curZ);

                    vertices[vIdx] = new Vector3(x, y, curZ + relief);
                    uvs[vIdx] = new Vector2(u * 2.5f, v * 2.5f);
                    vIdx++;
                }
            }

            // Top grid triangles
            int tIdx = 0;
            for (int j = 0; j < seg; j++)
            {
                for (int i = 0; i < seg; i++)
                {
                    int row1 = j * (seg + 1);
                    int row2 = (j + 1) * (seg + 1);

                    triangles[tIdx++] = row1 + i;
                    triangles[tIdx++] = row2 + i;
                    triangles[tIdx++] = row1 + i + 1;

                    triangles[tIdx++] = row1 + i + 1;
                    triangles[tIdx++] = row2 + i;
                    triangles[tIdx++] = row2 + i + 1;
                }
            }

            // 2. Skirt vertices (connects top perimeter down to throat)
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
                vertices[vIdx] = tl; normals[vIdx] = norm; uvs[vIdx] = new Vector2(0, 1); vIdx++;
                vertices[vIdx] = tr; normals[vIdx] = norm; uvs[vIdx] = new Vector2(1, 1); vIdx++;
                vertices[vIdx] = br; normals[vIdx] = norm; uvs[vIdx] = new Vector2(1, 0); vIdx++;
                vertices[vIdx] = bl; normals[vIdx] = norm; uvs[vIdx] = new Vector2(0, 0); vIdx++;

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
            vertices[vIdx] = bBL; normals[vIdx] = Vector3.back; uvs[vIdx] = new Vector2(0, 0); vIdx++;
            vertices[vIdx] = bBR; normals[vIdx] = Vector3.back; uvs[vIdx] = new Vector2(1, 0); vIdx++;
            vertices[vIdx] = bTR; normals[vIdx] = Vector3.back; uvs[vIdx] = new Vector2(1, 1); vIdx++;
            vertices[vIdx] = bTL; normals[vIdx] = Vector3.back; uvs[vIdx] = new Vector2(0, 1); vIdx++;

            triangles[tIdx++] = botBase;
            triangles[tIdx++] = botBase + 2;
            triangles[tIdx++] = botBase + 1;

            triangles[tIdx++] = botBase;
            triangles[tIdx++] = botBase + 3;
            triangles[tIdx++] = botBase + 2;

            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
        }

        private void UpdateVertices()
        {
            if (mesh == null) return;
            var vertices = mesh.vertices;
            float curZ = Mathf.Lerp(minZ, maxZ, fillFraction);
            CalculateBoundsAtHeight(curZ, out float halfX, out float minY, out float maxY);

            int seg = gridSegments;
            int vIdx = 0;
            for (int j = 0; j <= seg; j++)
            {
                float v = (float)j / seg;
                float y = Mathf.Lerp(minY, maxY, v);
                for (int i = 0; i <= seg; i++)
                {
                    float u = (float)i / seg;
                    float x = Mathf.Lerp(-halfX, halfX, u);
                    float relief = CalculateSurfaceRelief(u, v, curZ);
                    vertices[vIdx++] = new Vector3(x, y, curZ + relief);
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
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
        }
    }
}
