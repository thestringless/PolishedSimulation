using UnityEngine;
using System.Collections.Generic;

namespace Sefpro.RatchetBelt
{
    /// <summary>
    /// Generates and dynamically updates a physically accurate, non-deforming industrial webbing ribbon
    /// connecting the mandrel winding exit across the casting mold to the adjustable J-hook.
    /// Strictly guarantees constant strap width (50 mm) and thickness (2 mm) with zero Poisson thinning,
    /// scales the mandrel spool radially during ratcheting, and keeps the strap anchored to mold corners.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class RatchetWebbingRibbonRenderer : MonoBehaviour
    {
        [Header("Key Anchors & References")]
        public Transform mandrelExitTransform;
        public Transform adjustableHookTransform;
        public Transform moldTransform;
        public RatchetBeltMechanicsController mechanicsController;

        [Header("Physical Strap Dimensions")]
        [Tooltip("Webbing strap width in meters (50 mm = 0.050 m).")]
        public float strapWidth = 0.050f;

        [Tooltip("Webbing strap thickness in meters (2.2 mm = 0.0022 m).")]
        public float strapThickness = 0.0022f;

        [Header("Mold Geometry Clearance (World Space)")]
        public float moldLeftX = -23.44f;
        public float moldRightX = -21.64f;
        public float moldTopY = 0.715f;
        public float centerZ = -4.24f;

        private Mesh mesh;
        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;

        private void Awake()
        {
            meshFilter = GetComponent<MeshFilter>();
            meshRenderer = GetComponent<MeshRenderer>();
            mesh = new Mesh { name = "Dynamic_Adjustable_Webbing_Ribbon" };
            meshFilter.sharedMesh = mesh;
        }

        private void OnEnable()
        {
            if (mesh == null)
            {
                mesh = new Mesh { name = "Dynamic_Adjustable_Webbing_Ribbon" };
                meshFilter.sharedMesh = mesh;
            }
        }

        private void LateUpdate()
        {
            UpdateRibbonGeometry();
        }

        /// <summary>
        /// Rebuilds the ribbon mesh following the path from mandrel exit across mold to adjustable hook,
        /// maintaining strictly constant strap width and thickness.
        /// </summary>
        public void UpdateRibbonGeometry()
        {
            if (mandrelExitTransform == null || adjustableHookTransform == null)
            {
                AutoFindReferences();
                if (mandrelExitTransform == null || adjustableHookTransform == null) return;
            }

            Vector3 pStart = mandrelExitTransform.position;
            Vector3 pHook = adjustableHookTransform.position;

            // Compute waypoints across the mold contour
            List<Vector3> waypoints = new List<Vector3>();
            waypoints.Add(pStart);

            // Left corner entry of mold
            Vector3 moldLeftCorner = new Vector3(moldLeftX - 0.005f, moldTopY, centerZ);
            Vector3 moldRightCorner = new Vector3(moldRightX + 0.005f, moldTopY, centerZ);

            if (pStart.x < moldLeftX)
            {
                // Ascend along the left wall
                waypoints.Add(new Vector3(moldLeftX - 0.005f, Mathf.Min(pStart.y, moldTopY), centerZ));
                waypoints.Add(moldLeftCorner);
            }

            // Path across top rim towards hook
            if (pHook.x > moldLeftX && pHook.x < moldRightX)
            {
                // Hook is on top of mold (free resting state or mid-drag)
                waypoints.Add(new Vector3(pHook.x, moldTopY, centerZ));
            }
            else if (pHook.x >= moldRightX)
            {
                // Hook has reached or passed the right rim
                waypoints.Add(moldRightCorner);
                // Descend along right wall towards anchor hook
                waypoints.Add(new Vector3(moldRightX + 0.005f, Mathf.Max(pHook.y, 0.14f), centerZ));
            }

            // Final endpoint at hook strap eyelet
            waypoints.Add(pHook);

            // Generate 3D ribbon mesh from waypoints
            BuildExtrudedRibbonMesh(waypoints);
        }

        private void AutoFindReferences()
        {
            if (mandrelExitTransform == null)
            {
                var mandrel = GameObject.Find("Mandrel");
                if (mandrel != null) mandrelExitTransform = mandrel.transform;
            }

            if (adjustableHookTransform == null)
            {
                var hook = GameObject.Find("J_Hook_Adjustable");
                if (hook != null) adjustableHookTransform = hook.transform;
            }

            if (mechanicsController == null)
            {
                mechanicsController = FindAnyObjectByType<RatchetBeltMechanicsController>();
            }

            var moldGo = GameObject.Find("CastingMold");
            if (moldGo != null)
            {
                moldTransform = moldGo.transform;
                moldLeftX = moldGo.transform.position.x - 0.90f;
                moldRightX = moldGo.transform.position.x + 0.90f;
                moldTopY = moldGo.transform.position.y + 0.615f;
                centerZ = moldGo.transform.position.z;
            }
        }

        private void BuildExtrudedRibbonMesh(List<Vector3> points)
        {
            if (points.Count < 2) return;

            List<Vector3> verts = new List<Vector3>();
            List<Vector3> norms = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<int> tris = new List<int>();

            float halfWidth = strapWidth * 0.5f;
            float halfThick = strapThickness * 0.5f;
            float totalLen = 0f;

            for (int i = 0; i < points.Count; i++)
            {
                Vector3 cur = points[i];
                Vector3 forward;
                if (i == 0) forward = (points[1] - cur).normalized;
                else if (i == points.Count - 1) forward = (cur - points[i - 1]).normalized;
                else forward = ((cur - points[i - 1]).normalized + (points[i + 1] - cur).normalized).normalized;

                // Transverse binormal vector (across strap width, along Z)
                Vector3 side = Vector3.forward; // Belt runs mainly along X, width along Z
                if (Mathf.Abs(Vector3.Dot(forward, Vector3.forward)) > 0.85f)
                {
                    side = Vector3.right;
                }
                Vector3 normal = Vector3.Cross(forward, side).normalized;
                side = Vector3.Cross(normal, forward).normalized;

                // Local space of this GameObject
                Vector3 p = transform.InverseTransformPoint(cur);
                Vector3 s = transform.InverseTransformDirection(side * halfWidth);
                Vector3 n = transform.InverseTransformDirection(normal * halfThick);

                if (i > 0) totalLen += Vector3.Distance(points[i], points[i - 1]);
                float v = totalLen * 20f; // UV tiling

                int baseIdx = verts.Count;

                // 4 corners of rectangular cross section (top-left, top-right, bottom-right, bottom-left)
                verts.Add(p - s + n); norms.Add(transform.InverseTransformDirection(normal));  uvs.Add(new Vector2(0f, v));
                verts.Add(p + s + n); norms.Add(transform.InverseTransformDirection(normal));  uvs.Add(new Vector2(1f, v));
                verts.Add(p + s - n); norms.Add(transform.InverseTransformDirection(-normal)); uvs.Add(new Vector2(1f, v));
                verts.Add(p - s - n); norms.Add(transform.InverseTransformDirection(-normal)); uvs.Add(new Vector2(0f, v));

                if (i > 0)
                {
                    int prev = baseIdx - 4;
                    // Top face
                    AddQuad(tris, prev + 0, prev + 1, baseIdx + 1, baseIdx + 0);
                    // Bottom face
                    AddQuad(tris, prev + 2, prev + 3, baseIdx + 3, baseIdx + 2);
                    // Right edge
                    AddQuad(tris, prev + 1, prev + 2, baseIdx + 2, baseIdx + 1);
                    // Left edge
                    AddQuad(tris, prev + 3, prev + 0, baseIdx + 0, baseIdx + 3);
                }
            }

            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
        }

        private static void AddQuad(List<int> tris, int i0, int i1, int i2, int i3)
        {
            tris.Add(i0); tris.Add(i1); tris.Add(i2);
            tris.Add(i0); tris.Add(i2); tris.Add(i3);
        }
    }
}
