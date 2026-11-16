using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds the painted floor layout of the hangar: one central pedestrian walkway and outlined equipment zones.
/// Everything is generated under WarehouseEnvironment/Floor_Markings from the layout below, so the markings can be
/// rebuilt after changing a number: Tools > Warehouse > Rebuild Floor Markings.
/// World axes: X runs along the hangar (east roller door at x = 0), Z across it (south wall +0.5, north wall -18.7).
/// </summary>
public static class WarehouseFloorMarkings
{
    public const string ParentPath = "WarehouseEnvironment";
    public const string RootName = "Floor_Markings";
    const string AssetDir = "Assets/MyAssets/Hangar/FloorMarkings";

    // ---------------------------------------------------------------- layout (metres, world space)

    public const float LineWidth = 0.10f;
    public const float WalkwayEdgeWidth = 0.12f;

    /// <summary>Central walkway: from the roller door to the west end of the hall.</summary>
    public static readonly Rect Walkway = Rect.MinMaxRect(-31.0f, -11.0f, -0.4f, -9.0f); // x, z

    public struct Zone
    {
        public string name, label;
        public float xMin, xMax, zMin, zMax;
        public bool southOfWalkway; // decides which edge faces the walkway (label side)
        public Zone(string name, string label, float xMin, float xMax, float zMin, float zMax, bool south)
        { this.name = name; this.label = label; this.xMin = xMin; this.xMax = xMax; this.zMin = zMin; this.zMax = zMax; southOfWalkway = south; }
    }

    public static readonly Zone[] Zones =
    {
        // Big equipment: one zone each, south of the walkway, all inside the crane hook's reach.
        new Zone("Zone_CastingStation", "CASTING STATION", -25.7f, -19.7f, -8.7f, -3.4f, true),
        new Zone("Zone_MixingStation",  "MIXING STATION",  -14.7f,  -9.9f, -8.7f, -3.4f, true),
        // Small equipment: shared zones, north of the walkway, opposite the casting station.
        new Zone("Zone_LiftingGear",    "LIFTING GEAR",          -25.7f, -20.9f, -13.9f, -11.3f, false),
        new Zone("Zone_TongsAndRemote", "TONGS & CRANE REMOTE",  -20.6f, -16.8f, -13.9f, -11.3f, false),
        new Zone("Zone_SupervisorOffice", "SUPERVISOR & QC OFFICE", -8.4f, -1.6f, -6.2f, -0.2f, true),
        new Zone("Zone_ControlCabin",     "OPERATIONS & SAFETY",    -9.0f, -2.0f, -18.0f, -13.4f, false),
    };

    static readonly Color Yellow = new Color(0.95f, 0.72f, 0.02f);
    static readonly Color WalkwayGreen = new Color(0.13f, 0.40f, 0.29f);
    static readonly Color LabelWhite = new Color(0.90f, 0.90f, 0.86f);

    const float FillY = 0.014f, LineY = 0.016f, TextY = 0.019f;

    // ---------------------------------------------------------------- build

    [MenuItem("Tools/Warehouse/Rebuild Floor Markings")]
    public static void Rebuild()
    {
        var parent = GameObject.Find(ParentPath);
        if (parent == null)
        {
            Debug.LogError($"[WarehouseFloorMarkings] '{ParentPath}' not found in the open scene.");
            return;
        }

        Transform old = parent.transform.Find(RootName);
        if (old != null)
            Undo.DestroyObjectImmediate(old.gameObject);

        if (!AssetDatabase.IsValidFolder(AssetDir))
        {
            System.IO.Directory.CreateDirectory(AssetDir);
            AssetDatabase.Refresh();
        }

        var root = new GameObject(RootName);
        Undo.RegisterCreatedObjectUndo(root, "Rebuild Floor Markings");
        root.transform.SetParent(parent.transform, false);
        root.isStatic = true;

        Material yellow = GetMaterial("M_FloorPaint_Yellow", Yellow);
        Material green = GetMaterial("M_FloorPaint_Walkway", WalkwayGreen);

        // Walkway: green fill, yellow edge lines (open at the door end, closed at the far end).
        var walk = Child(root.transform, "Walkway");
        var fill = new List<Vector4> { new Vector4(Walkway.xMin, Walkway.xMax, Walkway.yMin, Walkway.yMax) };
        MakeMesh(walk, "Walkway_Fill", fill, FillY, green);

        var edges = new List<Vector4>
        {
            new Vector4(Walkway.xMin, Walkway.xMax, Walkway.yMax - WalkwayEdgeWidth, Walkway.yMax),
            new Vector4(Walkway.xMin, Walkway.xMax, Walkway.yMin, Walkway.yMin + WalkwayEdgeWidth),
            new Vector4(Walkway.xMin, Walkway.xMin + WalkwayEdgeWidth, Walkway.yMin + WalkwayEdgeWidth, Walkway.yMax - WalkwayEdgeWidth),
        };
        MakeMesh(walk, "Walkway_Edges", edges, LineY, yellow);

        float midZ = 0.5f * (Walkway.yMin + Walkway.yMax);
        Label(walk, "Label_Walkway_Door", "WALKWAY", new Vector3(Walkway.xMax - 2.2f, TextY, midZ), Vector3.left, 0.26f, true);
        Label(walk, "Label_Walkway_West", "WALKWAY", new Vector3(Walkway.xMin + 2.2f, TextY, midZ), Vector3.right, 0.26f, true);

        // Zones: yellow outline + a label on the edge that faces the walkway.
        var zonesRoot = Child(root.transform, "Zones");
        var frames = new List<Vector4>();
        foreach (var z in Zones)
        {
            float w = LineWidth;
            frames.Add(new Vector4(z.xMin, z.xMax, z.zMax - w, z.zMax));
            frames.Add(new Vector4(z.xMin, z.xMax, z.zMin, z.zMin + w));
            frames.Add(new Vector4(z.xMin, z.xMin + w, z.zMin + w, z.zMax - w));
            frames.Add(new Vector4(z.xMax - w, z.xMax, z.zMin + w, z.zMax - w));

            // Read from the walkway: text starts at the reader's left-hand corner, just inside the line.
            const float inset = 0.22f;
            Vector3 pos, up;
            if (z.southOfWalkway) { pos = new Vector3(z.xMin + inset, TextY, z.zMin + inset); up = Vector3.forward; }
            else { pos = new Vector3(z.xMax - inset, TextY, z.zMax - inset); up = Vector3.back; }
            Label(zonesRoot, "Label_" + z.name, z.label, pos, up, 0.17f, false);
        }
        MakeMesh(zonesRoot, "Zone_Outlines", frames, LineY, yellow);

        AssetDatabase.SaveAssets();
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(root.scene);
        Debug.Log($"[WarehouseFloorMarkings] Built walkway + {Zones.Length} zones under {ParentPath}/{RootName}.");
    }

    // ---------------------------------------------------------------- helpers

    static Transform Child(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.isStatic = true;
        return go.transform;
    }

    static Material GetMaterial(string name, Color color)
    {
        string path = $"{AssetDir}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.SetColor("_BaseColor", color);
        mat.SetFloat("_Smoothness", 0.22f);
        mat.SetFloat("_Metallic", 0f);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    /// <summary>One flat mesh from rectangles given as (xMin, xMax, zMin, zMax).</summary>
    static void MakeMesh(Transform parent, string name, List<Vector4> rects, float y, Material mat)
    {
        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        foreach (var r in rects)
        {
            int i = verts.Count;
            verts.Add(new Vector3(r.x, y, r.z)); verts.Add(new Vector3(r.x, y, r.w));
            verts.Add(new Vector3(r.y, y, r.w)); verts.Add(new Vector3(r.y, y, r.z));
            uvs.Add(new Vector2(r.x, r.z)); uvs.Add(new Vector2(r.x, r.w)); uvs.Add(new Vector2(r.y, r.w)); uvs.Add(new Vector2(r.y, r.z));
            tris.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
        }
        var mesh = new Mesh { name = name };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        string path = $"{AssetDir}/{name}.asset";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(mesh, path);

        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.isStatic = true;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = true;
    }

    /// <summary>Stencil text lying on the floor. 'up' is the world direction of the top of the letters.</summary>
    static void Label(Transform parent, string name, string text, Vector3 pos, Vector3 up, float capHeight, bool centred)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(Vector3.down, up));

        var tmp = go.AddComponent<TextMeshPro>();
        tmp.font = TMP_Settings.defaultFontAsset;
        tmp.text = text;
        tmp.fontStyle = FontStyles.Bold;
        tmp.fontSize = capHeight / 0.07f;
        tmp.color = LabelWhite;
        tmp.characterSpacing = 6f;
        tmp.alignment = centred ? TextAlignmentOptions.Center : TextAlignmentOptions.BottomLeft;
        tmp.rectTransform.pivot = centred ? new Vector2(0.5f, 0.5f) : Vector2.zero;
        tmp.rectTransform.sizeDelta = new Vector2(12f, capHeight * 2f);
        tmp.raycastTarget = false;
        var mr = go.GetComponent<MeshRenderer>();
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
    }
}
