using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Places and maintains a machined 3D steel flat washer (with true center bore hole and beveled outer rim)
/// behind a nut fastener:
/// - While the nut is seated on a rod (from 100% tightened out to 0% loosened at the rod tip), the washer
///   rests flat against the mold wall / flange bearing surface around the rod behind the nut.
/// - When the loosened nut is removed off the rod tip by hand, the washer stays paired behind the nut's
///   bearing face so zero washers float in mid-air when rods and walls are dismantled.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(ScrewInteraction))]
public class NutWasher : MonoBehaviour
{
    public const float HorizontalWasherThickness = 0.0045f;
    public const float HorizontalWasherInnerRadius = 0.0138f;
    public const float HorizontalWasherOuterRadius = 0.0330f;

    public const float VerticalWasherThickness = 0.0038f;
    public const float VerticalWasherInnerRadius = 0.0108f;
    public const float VerticalWasherOuterRadius = 0.0255f;

    [Header("Washer Visual Reference")]
    [Tooltip("3D steel flat washer GameObject placed behind this nut (auto-created under MoldCast/Washers if empty).")]
    [SerializeField] private Transform washerVisual;

    [Tooltip("Optional custom material override for the steel washer.")]
    [SerializeField] private Material customWasherMaterial;

    private static Mesh s_horizontalWasherMesh;
    private static Mesh s_verticalWasherMesh;
    private static Material s_sharedWasherMaterial;

    private ScrewInteraction _screw;
    private MoldPart _part;
    private BoxCollider _nutBoxCollider;

    public Transform WasherTransform => washerVisual;

    public bool IsVerticalFastener
    {
        get
        {
            if (_screw == null)
                _screw = GetComponent<ScrewInteraction>();

            if (_screw != null && _screw.CurrentSlot != null)
                return Mathf.Abs(_screw.CurrentSlot.WorldTightenDirection.y) > 0.7f;

            if (_screw != null && !string.IsNullOrEmpty(_screw.FastenerType) &&
                _screw.FastenerType.IndexOf("Vertical", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return name.IndexOf("002", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    public float WasherThicknessWorld =>
        IsVerticalFastener ? VerticalWasherThickness : HorizontalWasherThickness;

    private void Awake()
    {
        BindComponents();
        EnsureWasherVisual();
        SyncWasherPose();
    }

    private void OnEnable()
    {
        BindComponents();
        if (_screw != null)
            _screw.TightenProgressChanged += OnTightenProgressChanged;
        if (_part != null)
            _part.StateChanged += OnPartStateChanged;

        EnsureWasherVisual();
        SyncWasherPose();
    }

    private void Start()
    {
        EnsureWasherVisual();
        SyncWasherPose();
    }

    private void OnDisable()
    {
        if (_screw != null)
            _screw.TightenProgressChanged -= OnTightenProgressChanged;
        if (_part != null)
            _part.StateChanged -= OnPartStateChanged;
    }

    private void LateUpdate()
    {
        if (washerVisual == null)
            EnsureWasherVisual();

        SyncWasherPose();
    }

    private void OnDestroy()
    {
        // Clean up runtime-created non-persistent washer visual when nut is destroyed in Play Mode.
        if (Application.isPlaying && washerVisual != null)
            Destroy(washerVisual.gameObject);
    }

    public void AssignWasherVisual(Transform existingWasher, Mesh sharedMeshOverride = null, Material sharedMatOverride = null)
    {
        washerVisual = existingWasher;
        if (sharedMatOverride != null)
            customWasherMaterial = sharedMatOverride;

        if (washerVisual != null)
        {
            MeshFilter mf = washerVisual.GetComponent<MeshFilter>();
            if (mf != null && sharedMeshOverride != null)
                mf.sharedMesh = sharedMeshOverride;

            MeshRenderer mr = washerVisual.GetComponent<MeshRenderer>();
            if (mr != null && sharedMatOverride != null)
                mr.sharedMaterial = sharedMatOverride;
        }

        SyncWasherPose();
    }

    /// <summary>
    /// Ensures the 3D steel flat washer GameObject exists under MoldCast/Washers with zero physics colliders.
    /// </summary>
    public void EnsureWasherVisual()
    {
        BindComponents();
        Transform container = ResolveWashersParent();
        string washerName = "Washer_" + name;

        if (washerVisual == null && container != null)
        {
            Transform existing = container.Find(washerName);
            if (existing != null)
                washerVisual = existing;
        }

        bool isVertical = IsVerticalFastener;
        Mesh targetMesh = GetOrCreateWasherMesh(isVertical);
        Material targetMat = customWasherMaterial != null ? customWasherMaterial : GetOrCreateWasherMaterial();

        if (washerVisual == null)
        {
            GameObject go = new GameObject(washerName);
            if (!Application.isPlaying)
            {
                // Persistent scene setup is handled cleanly by MoldWasherSceneSetup in Editor; avoid dirtying if transient.
            }
            if (container != null)
                go.transform.SetParent(container, worldPositionStays: false);

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = targetMesh;

            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = targetMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            mr.receiveShadows = true;

            washerVisual = go.transform;
        }
        else
        {
            if (container != null && washerVisual.parent != container)
                washerVisual.SetParent(container, worldPositionStays: true);

            MeshFilter mf = washerVisual.GetComponent<MeshFilter>();
            if (mf == null) mf = washerVisual.gameObject.AddComponent<MeshFilter>();
            if (mf.sharedMesh == null)
                mf.sharedMesh = targetMesh;

            MeshRenderer mr = washerVisual.GetComponent<MeshRenderer>();
            if (mr == null) mr = washerVisual.gameObject.AddComponent<MeshRenderer>();
            if (mr.sharedMaterial == null && targetMat != null)
                mr.sharedMaterial = targetMat;

            // Ensure no stray collider on the washer interferes with grabbing nuts or rods.
            Collider strayCol = washerVisual.GetComponent<Collider>();
            if (strayCol != null)
            {
                if (Application.isPlaying) Destroy(strayCol);
                else DestroyImmediate(strayCol);
            }
        }
    }

    /// <summary>
    /// Synchronizes the 3D washer's world position, rotation, and metric scale:
    /// - Seated on rod: flat against the mold wall / flange bearing face behind the nut.
    /// - Removed (Free): captive right behind the nut's inner bearing face.
    /// </summary>
    public void SyncWasherPose()
    {
        if (washerVisual == null)
            return;

        BindComponents();

        // Keep exact 1:1 world-meter geometry regardless of parent (MoldCast = 0.33) scale.
        Transform parent = washerVisual.parent;
        Vector3 desiredScale = Vector3.one;
        if (parent != null)
        {
            Vector3 ps = parent.lossyScale;
            desiredScale = new Vector3(
                Mathf.Abs(ps.x) > 1e-5f ? 1f / ps.x : 1f,
                Mathf.Abs(ps.y) > 1e-5f ? 1f / ps.y : 1f,
                Mathf.Abs(ps.z) > 1e-5f ? 1f / ps.z : 1f);
        }

        if ((washerVisual.localScale - desiredScale).sqrMagnitude > 1e-9f)
            washerVisual.localScale = desiredScale;

        if (_screw != null && _screw.IsSeated && _screw.CurrentSlot != null)
        {
            ScrewSlot slot = _screw.CurrentSlot;
            if (!washerVisual.gameObject.activeSelf)
                washerVisual.gameObject.SetActive(true);

            Vector3 targetPos = slot.WasherWorldCenter;
            Quaternion targetRot = slot.CanonicalWorldRotation;
            if ((washerVisual.position - targetPos).sqrMagnitude > 1e-9f ||
                Quaternion.Angle(washerVisual.rotation, targetRot) > 0.02f)
            {
                washerVisual.SetPositionAndRotation(targetPos, targetRot);
            }
            return;
        }

        // Nut is Free (held in hand or resting on floor): keep washer flush behind the nut's inner bearing face.
        if (_screw != null && _part != null)
        {
            if (!washerVisual.gameObject.activeSelf)
                washerVisual.gameObject.SetActive(true);

            Vector3 nutCenter = MoldSnapPoint.GetPartWorldGeometricCenter(_part);
            Vector3 inwardDir = _screw.WorldThreadAxis;
            if (_screw.CaptiveHostRod != null && _screw.CaptiveHostRod.Part != null)
            {
                Vector3 toRodCenter = MoldSnapPoint.GetPartWorldGeometricCenter(_screw.CaptiveHostRod.Part) - nutCenter;
                if (toRodCenter.sqrMagnitude > 1e-6f && Vector3.Dot(inwardDir, toRodCenter) < 0f)
                    inwardDir = -inwardDir;
            }
            float nutHalfThick = EstimateNutHalfThicknessWorld();
            float washerHalfThick = WasherThicknessWorld * 0.5f;

            Vector3 behindNutPos = nutCenter + inwardDir * (nutHalfThick + washerHalfThick);
            Quaternion targetRot = transform.rotation;
            if ((washerVisual.position - behindNutPos).sqrMagnitude > 1e-9f ||
                Quaternion.Angle(washerVisual.rotation, targetRot) > 0.02f)
            {
                washerVisual.SetPositionAndRotation(behindNutPos, targetRot);
            }
        }
    }

    private float EstimateNutHalfThicknessWorld()
    {
        if (_nutBoxCollider == null)
            _nutBoxCollider = GetComponent<BoxCollider>();

        if (_nutBoxCollider != null && _screw != null)
        {
            float localProj = Mathf.Abs(Vector3.Dot(_nutBoxCollider.center, _screw.LocalRotationAxis));
            float worldScale = Mathf.Abs(transform.lossyScale.z);
            if (localProj > 0.001f)
                return localProj * worldScale;
        }

        return IsVerticalFastener ? 0.01165f : 0.01683f;
    }

    private Transform ResolveWashersParent()
    {
        Transform moldRoot = _part != null && _part.MoldContainer != null
            ? _part.MoldContainer
            : transform.parent;

        if (moldRoot == null)
            return null;

        Transform group = moldRoot.Find("Washers");
        if (group == null)
        {
            GameObject groupGo = new GameObject("Washers");
            groupGo.transform.SetParent(moldRoot, worldPositionStays: false);
            groupGo.transform.localPosition = Vector3.zero;
            groupGo.transform.localRotation = Quaternion.identity;
            groupGo.transform.localScale = Vector3.one;
            group = groupGo.transform;
        }

        return group;
    }

    private void BindComponents()
    {
        if (_screw == null)
            _screw = GetComponent<ScrewInteraction>();
        if (_part == null)
            _part = GetComponent<MoldPart>();
        if (_nutBoxCollider == null)
            _nutBoxCollider = GetComponent<BoxCollider>();
    }

    private void OnTightenProgressChanged(ScrewInteraction screw, float progress, bool isTightened)
    {
        SyncWasherPose();
    }

    private void OnPartStateChanged(MoldPart part, MoldPartState previous, MoldPartState current)
    {
        SyncWasherPose();
    }

    public static Mesh GetOrCreateWasherMesh(bool isVertical)
    {
        if (isVertical)
        {
            if (s_verticalWasherMesh == null)
            {
                s_verticalWasherMesh = BuildAnnularWasherMesh(
                    "Proc_SteelWasher_M16",
                    VerticalWasherInnerRadius,
                    VerticalWasherOuterRadius,
                    VerticalWasherThickness,
                    radialSegments: 36);
            }
            return s_verticalWasherMesh;
        }

        if (s_horizontalWasherMesh == null)
        {
            s_horizontalWasherMesh = BuildAnnularWasherMesh(
                "Proc_SteelWasher_M26",
                HorizontalWasherInnerRadius,
                HorizontalWasherOuterRadius,
                HorizontalWasherThickness,
                radialSegments: 40);
        }
        return s_horizontalWasherMesh;
    }

    public static Material GetOrCreateWasherMaterial()
    {
        if (s_sharedWasherMaterial != null)
            return s_sharedWasherMaterial;

        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null)
            sh = Shader.Find("Standard");

        if (sh == null)
            return null;

        Material mat = new Material(sh)
        {
            name = "Mat_Steel_Washer_Runtime",
            hideFlags = HideFlags.DontSave
        };
        Color steelZinc = new Color(0.60f, 0.63f, 0.67f, 1f);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", steelZinc);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", steelZinc);
        if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0.80f);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.64f);
        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.64f);

        s_sharedWasherMaterial = mat;
        return s_sharedWasherMaterial;
    }

    /// <summary>
    /// Builds a watertight 3D hollow annular flat washer mesh centered at the origin with bore axis along +Z,
    /// including inner cylindrical bore wall, flat front/back bearing faces, and 45-degree outer rim bevels.
    /// </summary>
    public static Mesh BuildAnnularWasherMesh(
        string meshName,
        float innerRadius,
        float outerRadius,
        float thickness,
        int radialSegments = 36)
    {
        int segs = Mathf.Max(16, radialSegments);
        float halfT = thickness * 0.5f;
        float chamfer = Mathf.Min(thickness * 0.18f, (outerRadius - innerRadius) * 0.12f);
        float bevelOuterR = outerRadius - chamfer;
        float bevelZ = halfT - chamfer;

        // Profile loops around cross-section (each quad band uses distinct vertices for crisp machined normals):
        // 0: Back face (innerRadius -> bevelOuterR at z = -halfT, normal -Z)
        // 1: Back outer bevel (bevelOuterR,-halfT -> outerRadius,-bevelZ)
        // 2: Outer cylindrical rim (outerRadius,-bevelZ -> outerRadius,+bevelZ)
        // 3: Front outer bevel (outerRadius,+bevelZ -> bevelOuterR,+halfT)
        // 4: Front face (bevelOuterR -> innerRadius at z = +halfT, normal +Z)
        // 5: Inner bore cylinder (innerRadius,+halfT -> innerRadius,-halfT, normal -radial)

        var verts = new List<Vector3>(segs * 12);
        var norms = new List<Vector3>(segs * 12);
        var uvs = new List<Vector2>(segs * 12);
        var tris = new List<int>(segs * 36);

        void AddQuadRing(float r0, float z0, float r1, float z1, float radialNormalComp, float zNormalComp)
        {
            int baseIdx = verts.Count;
            Vector2 nz = new Vector2(radialNormalComp, zNormalComp).normalized;

            for (int i = 0; i <= segs; i++)
            {
                float u = (float)i / segs;
                float ang = u * Mathf.PI * 2f;
                float c = Mathf.Cos(ang);
                float s = Mathf.Sin(ang);

                Vector3 p0 = new Vector3(c * r0, s * r0, z0);
                Vector3 p1 = new Vector3(c * r1, s * r1, z1);
                Vector3 n = new Vector3(c * nz.x, s * nz.x, nz.y).normalized;

                verts.Add(p0);
                verts.Add(p1);
                norms.Add(n);
                norms.Add(n);
                uvs.Add(new Vector2(u, 0f));
                uvs.Add(new Vector2(u, 1f));
            }

            // Guarantee clockwise front-facing triangle winding in Unity's left-handed coordinate system:
            bool standardOrderFacesOut =
                Vector3.Dot(Vector3.Cross(verts[baseIdx + 1] - verts[baseIdx], verts[baseIdx + 2] - verts[baseIdx]), norms[baseIdx]) >= 0f;

            for (int i = 0; i < segs; i++)
            {
                int a = baseIdx + i * 2;
                int b = a + 1;
                int c = a + 2;
                int d = a + 3;

                if (standardOrderFacesOut)
                {
                    tris.Add(a);
                    tris.Add(b);
                    tris.Add(c);

                    tris.Add(b);
                    tris.Add(d);
                    tris.Add(c);
                }
                else
                {
                    tris.Add(a);
                    tris.Add(c);
                    tris.Add(b);

                    tris.Add(b);
                    tris.Add(c);
                    tris.Add(d);
                }
            }
        }

        // 0: Back bearing face (-Z)
        AddQuadRing(innerRadius, -halfT, bevelOuterR, -halfT, 0f, -1f);
        // 1: Back outer 45-degree bevel (+radial, -Z)
        AddQuadRing(bevelOuterR, -halfT, outerRadius, -bevelZ, 1f, -1f);
        // 2: Outer cylindrical rim (+radial)
        AddQuadRing(outerRadius, -bevelZ, outerRadius, bevelZ, 1f, 0f);
        // 3: Front outer 45-degree bevel (+radial, +Z)
        AddQuadRing(outerRadius, bevelZ, bevelOuterR, halfT, 1f, 1f);
        // 4: Front bearing face (+Z)
        AddQuadRing(bevelOuterR, halfT, innerRadius, halfT, 0f, 1f);
        // 5: Inner cylindrical bore wall (-radial toward center hole)
        AddQuadRing(innerRadius, halfT, innerRadius, -halfT, -1f, 0f);

        Mesh mesh = new Mesh
        {
            name = meshName
        };
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        return mesh;
    }
}
