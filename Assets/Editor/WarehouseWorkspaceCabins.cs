using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds realistic, VR-friendly modular workspace cabins in the east bay of the warehouse hangar
/// (x = -9.5 to -1.5) so the east end around the roller door feels like a real industrial facility.
/// Combines meshes by material per cabin for ultra-low draw calls in VR.
/// Rebuild via: Tools > Warehouse > Rebuild Workspace Cabins
/// </summary>
public static class WarehouseWorkspaceCabins
{
    public const string ParentPath = "WarehouseEnvironment";
    public const string RootName = "Workspace_Cabins";
    const string AssetDir = "Assets/MyAssets/Hangar/WorkspaceCabins";

    struct BoxPart
    {
        public Vector3 center;
        public Vector3 size;
        public Quaternion rot;
        public Material mat;
        public bool addCollider;
    }

    static Material matSteelDark;
    static Material matWallPanel;
    static Material matRoofPanel;
    static Material matSafetyYellow;
    static Material matAccentBlue;
    static Material matDeskWood;
    static Material matDarkPlastic;
    static Material matWhiteboard;
    static Material matScreenContent;
    static Material matLedStrip;
    static Material matGreenLed;
    static Material matRedLed;
    static Material matFireRed;
    static Material matGlass;
    static Material matContactShadow;
    static Material matFloorInside;

    [MenuItem("Tools/Warehouse/Rebuild Workspace Cabins")]
    public static void Rebuild()
    {
        var parent = GameObject.Find(ParentPath);
        if (parent == null)
        {
            Debug.LogError($"[WarehouseWorkspaceCabins] '{ParentPath}' not found in scene.");
            return;
        }

        Transform old = parent.transform.Find(RootName);
        if (old != null)
            Undo.DestroyObjectImmediate(old.gameObject);

        EnsureFolders();
        InitMaterials();

        var root = new GameObject(RootName);
        Undo.RegisterCreatedObjectUndo(root, "Rebuild Workspace Cabins");
        root.transform.SetParent(parent.transform, false);
        root.isStatic = true;

        // 1. South-East Two-Story Modular Complex (QC Lab below, Supervisor Mezzanine Office above)
        BuildSouthEastComplex(root.transform);

        // 2. North-East Ground-Level Logistics & Safety Dispatch Cabin
        BuildNorthEastCabin(root.transform);

        // 3. East Doorway Safety Bollards & Details
        BuildEastDoorwayDetails(root.transform);

        // Also rebuild floor markings so the cabin zones match
        WarehouseFloorMarkings.Rebuild();

        AssetDatabase.SaveAssets();
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(root.scene);
        Debug.Log("[WarehouseWorkspaceCabins] Built VR-friendly workspace cabins in the east warehouse area.");
    }

    static void EnsureFolders()
    {
        if (!AssetDatabase.IsValidFolder(AssetDir))
        {
            System.IO.Directory.CreateDirectory(AssetDir);
            AssetDatabase.Refresh();
        }
    }

    static void InitMaterials()
    {
        matSteelDark = LoadOrCreateMat("M_Cabin_SteelDark", new Color(0.09f, 0.10f, 0.12f), 0.82f, 0.45f);

        matWallPanel = LoadOrCreateMat("M_Cabin_WallPanel", new Color(0.68f, 0.71f, 0.75f), 0.22f, 0.36f);
        var cladNorm = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/MyAssets/WarehouseCladding/T_WarehouseCladding_Normal.png");
        if (cladNorm != null)
        {
            matWallPanel.SetTexture("_BumpMap", cladNorm);
            matWallPanel.SetFloat("_BumpScale", 0.22f);
            matWallPanel.EnableKeyword("_NORMALMAP");
            matWallPanel.SetTextureScale("_BaseMap", new Vector2(2f, 2f));
            matWallPanel.SetTextureScale("_BumpMap", new Vector2(2f, 2f));
        }

        matRoofPanel = LoadOrCreateMat("M_Cabin_RoofPanel", new Color(0.20f, 0.22f, 0.25f), 0.50f, 0.34f);
        matSafetyYellow = LoadOrCreateMat("M_Cabin_SafetyYellow", new Color(0.95f, 0.70f, 0.04f), 0.20f, 0.50f);
        matAccentBlue = LoadOrCreateMat("M_Cabin_AccentBlue", new Color(0.12f, 0.32f, 0.50f), 0.35f, 0.48f);
        matDeskWood = LoadOrCreateMat("M_Cabin_DeskSurface", new Color(0.66f, 0.54f, 0.40f), 0.0f, 0.46f);
        matDarkPlastic = LoadOrCreateMat("M_Cabin_DarkPlastic", new Color(0.14f, 0.15f, 0.17f), 0.12f, 0.38f);
        matWhiteboard = LoadOrCreateMat("M_Cabin_Whiteboard", new Color(0.92f, 0.94f, 0.96f), 0.05f, 0.78f);
        matFloorInside = LoadOrCreateMat("M_Cabin_Floor", new Color(0.24f, 0.26f, 0.29f), 0.12f, 0.36f);
        matFireRed = LoadOrCreateMat("M_Cabin_FireRed", new Color(0.76f, 0.14f, 0.12f), 0.25f, 0.52f);

        matLedStrip = LoadOrCreateEmissiveMat("M_Cabin_LedStrip", new Color(0.96f, 0.97f, 1.0f), new Color(2.6f, 2.5f, 2.3f));
        matScreenContent = CreateDashboardScreenMaterial();
        matGreenLed = LoadOrCreateEmissiveMat("M_Cabin_GreenLED", new Color(0.2f, 0.95f, 0.4f), new Color(0.3f, 2.2f, 0.6f));
        matRedLed = LoadOrCreateEmissiveMat("M_Cabin_RedLED", new Color(0.95f, 0.2f, 0.15f), new Color(2.2f, 0.3f, 0.2f));

        matGlass = AssetDatabase.LoadAssetAtPath<Material>("Assets/MyAssets/Hangar/M_Hangar_Glass.mat");
        if (matGlass == null)
            matGlass = LoadOrCreateMat("M_Cabin_Glass", new Color(0.8f, 0.88f, 0.95f, 0.15f), 0f, 0.85f);

        matContactShadow = AssetDatabase.LoadAssetAtPath<Material>("Assets/MyAssets/Hangar/M_ContactShadow.mat");
    }

    static Material LoadOrCreateMat(string name, Color baseColor, float metallic, float smoothness)
    {
        string path = $"{AssetDir}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.SetColor("_BaseColor", baseColor);
        mat.SetFloat("_Metallic", metallic);
        mat.SetFloat("_Smoothness", smoothness);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static Material LoadOrCreateEmissiveMat(string name, Color baseColor, Color emissionColor)
    {
        var mat = LoadOrCreateMat(name, baseColor, 0f, 0.8f);
        mat.EnableKeyword("_EMISSION");
        mat.SetColor("_EmissionColor", emissionColor);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static Material CreateDashboardScreenMaterial()
    {
        string texPath = $"{AssetDir}/T_Cabin_MonitorScreen.png";
        int w = 256, h = 128;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var pixels = new Color[w * h];
        Color bg = new Color(0.06f, 0.11f, 0.18f);
        Color grid = new Color(0.10f, 0.20f, 0.32f);
        Color accent1 = new Color(0.15f, 0.75f, 0.95f);
        Color accent2 = new Color(0.20f, 0.90f, 0.50f);
        Color warn = new Color(0.95f, 0.70f, 0.15f);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Color c = bg;
                if (x % 32 == 0 || y % 24 == 0) c = grid;
                if (y > h - 18) c = new Color(0.10f, 0.26f, 0.42f);
                if (x > 14 && x < 85 && y > 20 && y < h - 26)
                {
                    int row = (y - 20) / 16;
                    int barLen = 25 + ((row * 19) % 40);
                    if ((y - 20) % 16 < 9 && (x - 14) < barLen)
                        c = (row % 3 == 0) ? warn : accent1;
                }
                if (x > 96 && x < 185 && y > 24 && y < h - 28)
                {
                    float wave = 48f + 22f * Mathf.Sin(x * 0.09f) + 10f * Mathf.Cos(x * 0.23f);
                    if (Mathf.Abs(y - wave) < 2.2f) c = accent2;
                    else if (y < wave) c = Color.Lerp(bg, accent2, 0.22f);
                }
                if (x > 196 && x < w - 12 && y > 24 && y < h - 28)
                {
                    if (((y - 24) / 22) % 2 == 0) c = new Color(0.14f, 0.48f, 0.36f);
                }
                pixels[y * w + x] = c;
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        System.IO.File.WriteAllBytes(texPath, tex.EncodeToPNG());
        AssetDatabase.ImportAsset(texPath);

        var loadedTex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
        var mat = LoadOrCreateEmissiveMat("M_Cabin_MonitorScreen", Color.white, new Color(1.45f, 1.55f, 1.65f));
        mat.SetTexture("_BaseMap", loadedTex);
        mat.SetTexture("_EmissionMap", loadedTex);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static void BuildSouthEastComplex(Transform parent)
    {
        var complex = MakeGroup(parent, "Cabin_SE_SupervisorAndQC");
        var parts = new List<BoxPart>();
        var glassParts = new List<BoxPart>();

        float xMin = -8.2f, xMax = -2.2f;
        float zMin = -4.2f, zMax = -0.4f;
        float yGroundTop = 2.65f;
        float yUpperTop = 5.05f;

        AddContactShadow(complex, "Shadow_SE_Complex", new Vector3(-5.0f, 0.011f, -2.8f), new Vector2(7.6f, 5.2f));

        float[] colX = { xMin, 0.5f * (xMin + xMax), xMax };
        float[] colZ = { zMin, zMax };
        foreach (float cx in colX)
        {
            foreach (float cz in colZ)
            {
                AddBox(parts, new Vector3(cx, yUpperTop * 0.5f, cz), new Vector3(0.16f, yUpperTop, 0.16f), matSteelDark);
                AddBox(parts, new Vector3(cx, 0.02f, cz), new Vector3(0.28f, 0.04f, 0.28f), matSafetyYellow);
            }
        }

        foreach (float by in new[] { 0.06f, yGroundTop, yUpperTop })
        {
            AddBox(parts, new Vector3(-5.2f, by, zMin), new Vector3(6.16f, 0.14f, 0.16f), matSteelDark);
            AddBox(parts, new Vector3(-5.2f, by, zMax), new Vector3(6.16f, 0.14f, 0.16f), matSteelDark);
            AddBox(parts, new Vector3(xMin, by, -2.3f), new Vector3(0.16f, 0.14f, 3.96f), matSteelDark);
            AddBox(parts, new Vector3(xMax, by, -2.3f), new Vector3(0.16f, 0.14f, 3.96f), matSteelDark);
        }

        AddBox(parts, new Vector3(-5.2f, yGroundTop + 0.10f, zMin - 0.02f), new Vector3(6.18f, 0.16f, 0.04f), matAccentBlue);
        AddBox(parts, new Vector3(xMin - 0.02f, yGroundTop + 0.10f, -2.3f), new Vector3(0.04f, 0.16f, 3.98f), matAccentBlue);
        AddBox(parts, new Vector3(-5.2f, yUpperTop - 0.06f, zMin - 0.02f), new Vector3(6.18f, 0.18f, 0.04f), matAccentBlue);
        AddBox(parts, new Vector3(xMin - 0.02f, yUpperTop - 0.06f, -2.3f), new Vector3(0.04f, 0.18f, 3.98f), matAccentBlue);

        AddBox(parts, new Vector3(-5.2f, 0.04f, -2.3f), new Vector3(5.9f, 0.08f, 3.7f), matFloorInside);
        AddBox(parts, new Vector3(-5.2f, yGroundTop, -2.3f), new Vector3(5.96f, 0.14f, 3.76f), matFloorInside);
        AddBox(parts, new Vector3(-5.2f, yUpperTop + 0.04f, -2.3f), new Vector3(6.24f, 0.10f, 4.04f), matRoofPanel);

        // Ground floor walls
        AddBox(parts, new Vector3(-5.2f, 1.35f, zMax - 0.05f), new Vector3(5.84f, 2.50f, 0.08f), matWallPanel);
        AddBox(parts, new Vector3(xMax - 0.05f, 1.35f, -2.3f), new Vector3(0.08f, 2.50f, 3.64f), matWallPanel);

        BuildWallWithWindowAndDoor(parts, glassParts,
            new Vector3(xMin + 0.04f, 0f, -2.3f), Quaternion.Euler(0f, 90f, 0f),
            3.64f, 2.50f,
            hasDoor: false, doorCenter: 0f, doorWidth: 0f,
            winCenters: new[] { -0.2f }, winWidth: 1.8f, winYMin: 0.95f, winYMax: 2.15f);

        BuildWallWithWindowAndDoor(parts, glassParts,
            new Vector3(-5.2f, 0f, zMin + 0.04f), Quaternion.identity,
            5.84f, 2.50f,
            hasDoor: true, doorCenter: -1.85f, doorWidth: 0.96f,
            winCenters: new[] { -0.1f, 1.65f }, winWidth: 1.50f, winYMin: 0.95f, winYMax: 2.15f);

        // Upper floor walls
        AddBox(parts, new Vector3(-5.2f, 3.85f, zMax - 0.05f), new Vector3(5.84f, 2.26f, 0.08f), matWallPanel);
        AddBox(parts, new Vector3(xMax - 0.05f, 3.85f, -2.3f), new Vector3(0.08f, 2.26f, 3.64f), matWallPanel);

        BuildWallWithWindowAndDoor(parts, glassParts,
            new Vector3(xMin + 0.04f, yGroundTop + 0.07f, -2.3f), Quaternion.Euler(0f, 90f, 0f),
            3.64f, 2.26f,
            hasDoor: false, doorCenter: 0f, doorWidth: 0f,
            winCenters: new[] { 0.0f }, winWidth: 2.4f, winYMin: 0.80f, winYMax: 1.95f);

        BuildWallWithWindowAndDoor(parts, glassParts,
            new Vector3(-5.2f, yGroundTop + 0.07f, zMin + 0.04f), Quaternion.identity,
            5.84f, 2.26f,
            hasDoor: true, doorCenter: -1.85f, doorWidth: 0.96f,
            winCenters: new[] { -0.1f, 1.65f }, winWidth: 1.55f, winYMin: 0.80f, winYMax: 1.95f);

        // Mezzanine Catwalk Balcony & Industrial Staircase
        float balZMin = -5.35f, balZMax = -4.2f;
        float balZMid = 0.5f * (balZMin + balZMax);
        AddBox(parts, new Vector3(-5.2f, yGroundTop, balZMid), new Vector3(6.0f, 0.12f, 1.15f), matSteelDark);
        foreach (float px in new[] { -8.12f, -5.2f, -2.28f })
        {
            AddBox(parts, new Vector3(px, yGroundTop * 0.5f, balZMin + 0.06f), new Vector3(0.12f, yGroundTop, 0.12f), matSteelDark);
            AddBox(parts, new Vector3(px, 0.02f, balZMin + 0.06f), new Vector3(0.22f, 0.04f, 0.22f), matSafetyYellow);
        }
        BuildSafetyRailing(parts, new Vector3(-8.15f, yGroundTop + 0.06f, balZMin + 0.05f), new Vector3(-2.25f, yGroundTop + 0.06f, balZMin + 0.05f), 5);
        BuildSafetyRailing(parts, new Vector3(-8.15f, yGroundTop + 0.06f, balZMax), new Vector3(-8.15f, yGroundTop + 0.06f, balZMin + 0.05f), 2);

        // Top landing at x = -1.65, z = -4.78, y = 2.65
        AddBox(parts, new Vector3(-1.65f, yGroundTop, balZMid), new Vector3(1.10f, 0.12f, 1.15f), matSteelDark);
        AddBox(parts, new Vector3(-1.15f, yGroundTop * 0.5f, balZMin + 0.06f), new Vector3(0.12f, yGroundTop, 0.12f), matSteelDark);
        AddBox(parts, new Vector3(-1.15f, yGroundTop * 0.5f, balZMax - 0.06f), new Vector3(0.12f, yGroundTop, 0.12f), matSteelDark);
        BuildSafetyRailing(parts, new Vector3(-2.25f, yGroundTop + 0.06f, balZMin + 0.05f), new Vector3(-1.15f, yGroundTop + 0.06f, balZMin + 0.05f), 2);
        BuildSafetyRailing(parts, new Vector3(-1.15f, yGroundTop + 0.06f, balZMin + 0.05f), new Vector3(-1.15f, yGroundTop + 0.06f, balZMax), 2);

        // Stairs descending South (+Z) along East side of complex
        BuildStairFlight(parts, new Vector3(-1.65f, 2.65f, -4.2f), new Vector3(-1.65f, 0.0f, -1.0f), 0.95f, 13);

        // Protective Bollards at SE Complex corners
        BuildBollard(parts, new Vector3(-8.45f, 0f, -5.65f));
        BuildBollard(parts, new Vector3(-0.95f, 0f, -5.65f));

        // Interior furnishings - Ground Floor
        AddBox(parts, new Vector3(-6.4f, 2.52f, -2.3f), new Vector3(1.6f, 0.04f, 0.18f), matLedStrip);
        AddBox(parts, new Vector3(-3.8f, 2.52f, -2.3f), new Vector3(1.6f, 0.04f, 0.18f), matLedStrip);
        BuildDeskWithMonitors(parts, new Vector3(-5.8f, 0.08f, -1.35f), Quaternion.Euler(0f, 180f, 0f), 2.2f, 0.80f, 2);
        BuildDeskWithMonitors(parts, new Vector3(-3.3f, 0.08f, -1.35f), Quaternion.Euler(0f, 180f, 0f), 1.8f, 0.80f, 1);
        AddBox(parts, new Vector3(-2.55f, 1.05f, -2.6f), new Vector3(0.50f, 1.90f, 1.40f), matSteelDark);
        AddBox(parts, new Vector3(-2.55f, 1.55f, -1.4f), new Vector3(0.04f, 0.95f, 1.30f), matWhiteboard);

        // Interior furnishings - Upper Floor
        AddBox(parts, new Vector3(-6.4f, 4.92f, -2.3f), new Vector3(1.6f, 0.04f, 0.18f), matLedStrip);
        AddBox(parts, new Vector3(-3.8f, 4.92f, -2.3f), new Vector3(1.6f, 0.04f, 0.18f), matLedStrip);
        BuildDeskWithMonitors(parts, new Vector3(-5.0f, yGroundTop + 0.07f, -2.5f), Quaternion.identity, 2.4f, 0.85f, 3);
        AddBox(parts, new Vector3(-5.0f, yGroundTop + 1.35f, -0.52f), new Vector3(2.2f, 1.05f, 0.04f), matWhiteboard);
        AddBox(parts, new Vector3(-5.0f, yGroundTop + 1.35f, -0.50f), new Vector3(2.26f, 1.11f, 0.02f), matSteelDark);

        BuildExteriorWallProps(parts, new Vector3(-8.26f, 1.35f, -0.95f), Quaternion.Euler(0f, -90f, 0f));

        AddSignLabel(complex, "Sign_QCLab", "QUALITY CONTROL LAB", new Vector3(-3.70f, 2.36f, zMin - 0.06f), Quaternion.identity, 2.3f, 0.24f);
        AddSignLabel(complex, "Sign_Supervisor", "SUPERVISOR OFFICE", new Vector3(-3.70f, 4.82f, zMin - 0.06f), Quaternion.identity, 2.3f, 0.24f);

        BakePartsToMeshes(complex, "SE_Complex", parts);
        BakePartsToMeshes(complex, "SE_Complex_Glass", glassParts, isGlass: true);

        var col = complex.gameObject.AddComponent<BoxCollider>();
        col.center = new Vector3(-4.9f, 2.55f, -2.8f);
        col.size = new Vector3(7.5f, 5.1f, 4.9f);
    }

    static void BuildNorthEastCabin(Transform parent)
    {
        var cabin = MakeGroup(parent, "Cabin_NE_LogisticsAndSafety");
        var parts = new List<BoxPart>();
        var glassParts = new List<BoxPart>();

        float xMin = -8.6f, xMax = -2.6f;
        float zMin = -17.9f, zMax = -14.1f;
        float yTop = 2.95f;

        AddContactShadow(cabin, "Shadow_NE_Cabin", new Vector3(-5.6f, 0.011f, -16.0f), new Vector2(6.8f, 4.5f));

        float[] colX = { xMin, 0.5f * (xMin + xMax), xMax };
        float[] colZ = { zMin, zMax };
        foreach (float cx in colX)
        {
            foreach (float cz in colZ)
            {
                AddBox(parts, new Vector3(cx, yTop * 0.5f, cz), new Vector3(0.16f, yTop, 0.16f), matSteelDark);
                AddBox(parts, new Vector3(cx, 0.02f, cz), new Vector3(0.28f, 0.04f, 0.28f), matSafetyYellow);
            }
        }

        foreach (float by in new[] { 0.06f, yTop })
        {
            AddBox(parts, new Vector3(-5.6f, by, zMin), new Vector3(6.16f, 0.14f, 0.16f), matSteelDark);
            AddBox(parts, new Vector3(-5.6f, by, zMax), new Vector3(6.16f, 0.14f, 0.16f), matSteelDark);
            AddBox(parts, new Vector3(xMin, by, -16.0f), new Vector3(0.16f, 0.14f, 3.96f), matSteelDark);
            AddBox(parts, new Vector3(xMax, by, -16.0f), new Vector3(0.16f, 0.14f, 3.96f), matSteelDark);
        }

        AddBox(parts, new Vector3(-5.6f, yTop - 0.06f, zMax + 0.02f), new Vector3(6.20f, 0.20f, 0.04f), matAccentBlue);
        AddBox(parts, new Vector3(xMin - 0.02f, yTop - 0.06f, -16.0f), new Vector3(0.04f, 0.20f, 4.00f), matAccentBlue);
        AddBox(parts, new Vector3(xMax + 0.02f, yTop - 0.06f, -16.0f), new Vector3(0.04f, 0.20f, 4.00f), matAccentBlue);

        AddBox(parts, new Vector3(-5.6f, yTop - 0.22f, zMax + 0.35f), new Vector3(6.16f, 0.08f, 0.70f), matRoofPanel);
        AddBox(parts, new Vector3(-5.6f, yTop - 0.22f, zMax + 0.69f), new Vector3(6.18f, 0.10f, 0.04f), matSafetyYellow);

        AddBox(parts, new Vector3(-5.6f, 0.04f, -16.0f), new Vector3(5.9f, 0.08f, 3.7f), matFloorInside);
        AddBox(parts, new Vector3(-5.6f, yTop + 0.05f, -16.0f), new Vector3(6.24f, 0.12f, 4.04f), matRoofPanel);

        // Rooftop HVAC Skid
        AddBox(parts, new Vector3(-6.8f, yTop + 0.45f, -16.2f), new Vector3(1.50f, 0.68f, 1.10f), matWallPanel);
        AddBox(parts, new Vector3(-6.8f, yTop + 0.14f, -16.2f), new Vector3(1.60f, 0.08f, 1.20f), matSteelDark);
        AddBox(parts, new Vector3(-6.35f, yTop + 0.45f, -15.64f), new Vector3(0.45f, 0.45f, 0.04f), matSteelDark);

        AddBox(parts, new Vector3(-5.6f, 1.48f, zMin + 0.05f), new Vector3(5.84f, 2.80f, 0.08f), matWallPanel);
        AddBox(parts, new Vector3(xMax - 0.05f, 1.48f, -16.0f), new Vector3(0.08f, 2.80f, 3.64f), matWallPanel);

        BuildWallWithWindowAndDoor(parts, glassParts,
            new Vector3(xMin + 0.04f, 0f, -16.0f), Quaternion.Euler(0f, -90f, 0f),
            3.64f, 2.80f,
            hasDoor: false, doorCenter: 0f, doorWidth: 0f,
            winCenters: new[] { 0.0f }, winWidth: 2.0f, winYMin: 0.95f, winYMax: 2.25f);

        BuildWallWithWindowAndDoor(parts, glassParts,
            new Vector3(-5.6f, 0f, zMax - 0.04f), Quaternion.Euler(0f, 180f, 0f),
            5.84f, 2.80f,
            hasDoor: true, doorCenter: -1.85f, doorWidth: 0.98f,
            winCenters: new[] { 0.15f, 1.85f }, winWidth: 1.50f, winYMin: 0.95f, winYMax: 2.25f);

        AddBox(parts, new Vector3(-6.8f, 2.80f, -16.0f), new Vector3(1.6f, 0.04f, 0.18f), matLedStrip);
        AddBox(parts, new Vector3(-4.2f, 2.80f, -16.0f), new Vector3(1.6f, 0.04f, 0.18f), matLedStrip);

        BuildDeskWithMonitors(parts, new Vector3(-6.6f, 0.08f, -15.4f), Quaternion.Euler(0f, 180f, 0f), 2.3f, 0.85f, 2);
        BuildDeskWithMonitors(parts, new Vector3(-7.6f, 0.08f, -16.8f), Quaternion.Euler(0f, 90f, 0f), 1.6f, 0.80f, 1);

        AddBox(parts, new Vector3(-4.8f, 1.10f, -17.55f), new Vector3(1.60f, 2.00f, 0.48f), matAccentBlue);
        AddBox(parts, new Vector3(-6.8f, 1.55f, -17.78f), new Vector3(1.80f, 1.00f, 0.04f), matWhiteboard);

        BuildExteriorWallProps(parts, new Vector3(-8.66f, 1.35f, -14.75f), Quaternion.Euler(0f, -90f, 0f));

        BuildBollard(parts, new Vector3(-8.85f, 0f, -13.75f));
        BuildBollard(parts, new Vector3(-2.35f, 0f, -13.75f));

        AddSignLabel(cabin, "Sign_Logistics", "OPERATIONS & DISPATCH", new Vector3(-4.10f, 2.52f, zMax + 0.06f), Quaternion.Euler(0f, 180f, 0f), 2.4f, 0.24f);

        BakePartsToMeshes(cabin, "NE_Cabin", parts);
        BakePartsToMeshes(cabin, "NE_Cabin_Glass", glassParts, isGlass: true);

        var col = cabin.gameObject.AddComponent<BoxCollider>();
        col.center = new Vector3(-5.6f, 1.55f, -16.0f);
        col.size = new Vector3(6.2f, 3.1f, 4.0f);
    }

    static void BuildEastDoorwayDetails(Transform parent)
    {
        var details = MakeGroup(parent, "East_Doorway_SafetyProps");
        var parts = new List<BoxPart>();

        BuildBollard(parts, new Vector3(-0.45f, 0f, -12.68f));
        BuildBollard(parts, new Vector3(-0.45f, 0f, -5.52f));

        BuildSafetyRailing(parts, new Vector3(-2.2f, 0f, -11.35f), new Vector3(-0.6f, 0f, -11.35f), 2);
        BuildSafetyRailing(parts, new Vector3(-2.2f, 0f, -8.65f), new Vector3(-0.6f, 0f, -8.65f), 2);

        BakePartsToMeshes(details, "East_SafetyProps", parts);
    }

    static void BuildWallWithWindowAndDoor(
        List<BoxPart> parts, List<BoxPart> glassParts,
        Vector3 wallBottomCenter, Quaternion rot,
        float length, float height,
        bool hasDoor, float doorCenter, float doorWidth,
        float[] winCenters, float winWidth, float winYMin, float winYMax)
    {
        float thick = 0.08f;
        float doorHeight = 2.12f;

        var openings = new List<(float xMin, float xMax, float yMin, float yMax, bool isDoor)>();
        if (hasDoor)
            openings.Add((doorCenter - doorWidth * 0.5f, doorCenter + doorWidth * 0.5f, 0f, doorHeight, true));
        if (winCenters != null)
        {
            foreach (float wc in winCenters)
                openings.Add((wc - winWidth * 0.5f, wc + winWidth * 0.5f, winYMin, winYMax, false));
        }
        openings.Sort((a, b) => a.xMin.CompareTo(b.xMin));

        float curX = -length * 0.5f;
        float maxX = length * 0.5f;

        foreach (var op in openings)
        {
            if (op.xMin > curX + 0.02f)
            {
                float segW = op.xMin - curX;
                float segMid = curX + segW * 0.5f;
                AddLocalBox(parts, wallBottomCenter, rot, new Vector3(segMid, height * 0.5f, 0f), new Vector3(segW, height, thick), matWallPanel);
            }

            float opW = op.xMax - op.xMin;
            float opMid = 0.5f * (op.xMin + op.xMax);

            if (op.yMin > 0.02f)
            {
                AddLocalBox(parts, wallBottomCenter, rot, new Vector3(opMid, op.yMin * 0.5f, 0f), new Vector3(opW, op.yMin, thick), matWallPanel);
            }

            if (op.yMax < height - 0.02f)
            {
                float headH = height - op.yMax;
                AddLocalBox(parts, wallBottomCenter, rot, new Vector3(opMid, op.yMax + headH * 0.5f, 0f), new Vector3(opW, headH, thick), matWallPanel);
            }

            if (op.isDoor)
            {
                AddLocalBox(parts, wallBottomCenter, rot, new Vector3(op.xMin + 0.03f, doorHeight * 0.5f, 0f), new Vector3(0.06f, doorHeight, 0.11f), matSteelDark);
                AddLocalBox(parts, wallBottomCenter, rot, new Vector3(op.xMax - 0.03f, doorHeight * 0.5f, 0f), new Vector3(0.06f, doorHeight, 0.11f), matSteelDark);
                AddLocalBox(parts, wallBottomCenter, rot, new Vector3(opMid, doorHeight - 0.03f, 0f), new Vector3(opW, 0.06f, 0.11f), matSteelDark);

                float leafW = opW - 0.12f;
                AddLocalBox(parts, wallBottomCenter, rot, new Vector3(opMid, 0.55f, 0f), new Vector3(leafW, 1.10f, 0.05f), matAccentBlue);
                AddLocalBox(parts, wallBottomCenter, rot, new Vector3(opMid, 1.92f, 0f), new Vector3(leafW, 0.30f, 0.05f), matAccentBlue);
                AddLocalBox(parts, wallBottomCenter, rot, new Vector3(op.xMin + 0.16f, 1.45f, 0f), new Vector3(0.20f, 0.75f, 0.05f), matAccentBlue);
                AddLocalBox(parts, wallBottomCenter, rot, new Vector3(op.xMax - 0.16f, 1.45f, 0f), new Vector3(0.20f, 0.75f, 0.05f), matAccentBlue);
                AddLocalBox(glassParts, wallBottomCenter, rot, new Vector3(opMid, 1.45f, 0f), new Vector3(leafW - 0.36f, 0.72f, 0.02f), matGlass);
                AddLocalBox(parts, wallBottomCenter, rot, new Vector3(opMid + 0.25f, 1.02f, -0.045f), new Vector3(0.14f, 0.04f, 0.04f), matSafetyYellow);
                AddLocalBox(parts, wallBottomCenter, rot, new Vector3(opMid, 1.02f, 0.045f), new Vector3(leafW * 0.75f, 0.04f, 0.04f), matSafetyYellow);
            }
            else
            {
                float winH = op.yMax - op.yMin;
                float winMidY = 0.5f * (op.yMin + op.yMax);
                AddLocalBox(parts, wallBottomCenter, rot, new Vector3(opMid, op.yMin + 0.025f, 0f), new Vector3(opW + 0.04f, 0.05f, 0.12f), matSteelDark);
                AddLocalBox(parts, wallBottomCenter, rot, new Vector3(opMid, op.yMax - 0.025f, 0f), new Vector3(opW + 0.04f, 0.05f, 0.12f), matSteelDark);
                AddLocalBox(parts, wallBottomCenter, rot, new Vector3(op.xMin + 0.025f, winMidY, 0f), new Vector3(0.05f, winH, 0.11f), matSteelDark);
                AddLocalBox(parts, wallBottomCenter, rot, new Vector3(op.xMax - 0.025f, winMidY, 0f), new Vector3(0.05f, winH, 0.11f), matSteelDark);
                AddLocalBox(parts, wallBottomCenter, rot, new Vector3(opMid, winMidY, 0f), new Vector3(0.04f, winH, 0.10f), matSteelDark);

                AddLocalBox(glassParts, wallBottomCenter, rot, new Vector3(opMid, winMidY, 0f), new Vector3(opW - 0.06f, winH - 0.06f, 0.02f), matGlass);
            }

            curX = op.xMax;
        }

        if (curX < maxX - 0.02f)
        {
            float segW = maxX - curX;
            float segMid = curX + segW * 0.5f;
            AddLocalBox(parts, wallBottomCenter, rot, new Vector3(segMid, height * 0.5f, 0f), new Vector3(segW, height, thick), matWallPanel);
        }

        AddLocalBox(parts, wallBottomCenter, rot, new Vector3(0f, 0.06f, -0.045f), new Vector3(length, 0.12f, 0.02f), matSteelDark);
    }

    static void BuildDeskWithMonitors(List<BoxPart> parts, Vector3 basePos, Quaternion rot, float width, float depth, int monitorCount)
    {
        float deskH = 0.74f;
        AddLocalBox(parts, basePos, rot, new Vector3(0f, deskH, 0f), new Vector3(width, 0.04f, depth), matDeskWood);
        AddLocalBox(parts, basePos, rot, new Vector3(-width * 0.46f, deskH * 0.5f, 0f), new Vector3(0.05f, deskH, depth * 0.88f), matSteelDark);
        AddLocalBox(parts, basePos, rot, new Vector3(width * 0.46f, deskH * 0.5f, 0f), new Vector3(0.05f, deskH, depth * 0.88f), matSteelDark);
        AddLocalBox(parts, basePos, rot, new Vector3(0f, deskH * 0.55f, -depth * 0.38f), new Vector3(width * 0.92f, deskH * 0.65f, 0.03f), matSteelDark);
        AddLocalBox(parts, basePos, rot, new Vector3(width * 0.30f, deskH * 0.42f, 0.04f), new Vector3(0.42f, deskH * 0.80f, depth * 0.78f), matWallPanel);

        for (int i = 0; i < monitorCount; i++)
        {
            float mx = (monitorCount == 1) ? 0f : Mathf.Lerp(-width * 0.28f, width * 0.28f, (float)i / (monitorCount - 1));
            AddLocalBox(parts, basePos, rot, new Vector3(mx, deskH + 0.03f, -depth * 0.22f), new Vector3(0.22f, 0.02f, 0.16f), matDarkPlastic);
            AddLocalBox(parts, basePos, rot, new Vector3(mx, deskH + 0.16f, -depth * 0.25f), new Vector3(0.05f, 0.26f, 0.04f), matDarkPlastic);
            AddLocalBox(parts, basePos, rot, new Vector3(mx, deskH + 0.32f, -depth * 0.22f), new Vector3(0.58f, 0.35f, 0.03f), matDarkPlastic);
            AddLocalBox(parts, basePos, rot, new Vector3(mx, deskH + 0.32f, -depth * 0.20f), new Vector3(0.54f, 0.31f, 0.01f), matScreenContent);
        }

        AddLocalBox(parts, basePos, rot, new Vector3(-0.15f, 0.46f, depth * 0.55f), new Vector3(0.48f, 0.06f, 0.46f), matDarkPlastic);
        AddLocalBox(parts, basePos, rot, new Vector3(-0.15f, 0.74f, depth * 0.75f), new Vector3(0.46f, 0.52f, 0.06f), matDarkPlastic);
        AddLocalBox(parts, basePos, rot, new Vector3(-0.15f, 0.23f, depth * 0.55f), new Vector3(0.06f, 0.44f, 0.06f), matSteelDark);
        AddLocalBox(parts, basePos, rot, new Vector3(-0.15f, 0.04f, depth * 0.55f), new Vector3(0.52f, 0.04f, 0.52f), matSteelDark);
    }

    static void BuildSafetyRailing(List<BoxPart> parts, Vector3 start, Vector3 end, int postCount)
    {
        float height = 1.05f;
        Vector3 dir = end - start;
        float len = dir.magnitude;
        if (len < 0.05f) return;
        Quaternion rot = Quaternion.LookRotation(dir.normalized, Vector3.up);
        Vector3 mid = 0.5f * (start + end);

        AddBoxRot(parts, mid + Vector3.up * height, new Vector3(0.045f, 0.045f, len + 0.04f), rot, matSafetyYellow);
        AddBoxRot(parts, mid + Vector3.up * (height * 0.52f), new Vector3(0.035f, 0.035f, len), rot, matSafetyYellow);
        AddBoxRot(parts, mid + Vector3.up * 0.06f, new Vector3(0.02f, 0.12f, len), rot, matSafetyYellow);

        for (int i = 0; i < postCount; i++)
        {
            float t = (postCount == 1) ? 0.5f : (float)i / (postCount - 1);
            Vector3 p = Vector3.Lerp(start, end, t);
            AddBox(parts, p + Vector3.up * (height * 0.5f), new Vector3(0.05f, height, 0.05f), matSafetyYellow);
        }
    }

    static void BuildStairFlight(List<BoxPart> parts, Vector3 topPos, Vector3 bottomPos, float width, int steps)
    {
        Vector3 delta = bottomPos - topPos;
        Vector3 horiz = new Vector3(delta.x, 0f, delta.z);
        float slopeLen = delta.magnitude;
        Quaternion slopeRot = Quaternion.LookRotation(delta.normalized, Vector3.up);
        Quaternion flatRot = Quaternion.LookRotation(horiz.normalized, Vector3.up);
        Vector3 right = flatRot * Vector3.right;

        Vector3 mid = 0.5f * (topPos + bottomPos);
        AddBoxRot(parts, mid - right * (width * 0.48f), new Vector3(0.06f, 0.18f, slopeLen), slopeRot, matSteelDark);
        AddBoxRot(parts, mid + right * (width * 0.48f), new Vector3(0.06f, 0.18f, slopeLen), slopeRot, matSteelDark);

        for (int i = 0; i < steps; i++)
        {
            float t = (i + 0.5f) / steps;
            Vector3 stepPos = Vector3.Lerp(topPos, bottomPos, t);
            AddBoxRot(parts, stepPos, new Vector3(width - 0.08f, 0.04f, 0.26f), flatRot, matSteelDark);
            AddBoxRot(parts, stepPos + flatRot * new Vector3(0f, 0.01f, 0.11f), new Vector3(width - 0.08f, 0.025f, 0.04f), flatRot, matSafetyYellow);
        }

        Vector3 railOffset = Vector3.up * 0.95f;
        AddBoxRot(parts, mid - right * (width * 0.48f) + railOffset, new Vector3(0.04f, 0.04f, slopeLen), slopeRot, matSafetyYellow);
        AddBoxRot(parts, mid + right * (width * 0.48f) + railOffset, new Vector3(0.04f, 0.04f, slopeLen), slopeRot, matSafetyYellow);
        for (int i = 0; i <= 3; i++)
        {
            float t = i / 3f;
            Vector3 p = Vector3.Lerp(topPos, bottomPos, t);
            AddBox(parts, p - right * (width * 0.48f) + Vector3.up * 0.48f, new Vector3(0.045f, 0.96f, 0.045f), matSafetyYellow);
            AddBox(parts, p + right * (width * 0.48f) + Vector3.up * 0.48f, new Vector3(0.045f, 0.96f, 0.045f), matSafetyYellow);
        }
    }

    static void BuildExteriorWallProps(List<BoxPart> parts, Vector3 pos, Quaternion rot)
    {
        AddLocalBox(parts, pos, rot, new Vector3(0f, 0f, 0f), new Vector3(0.52f, 0.68f, 0.20f), matWallPanel);
        AddLocalBox(parts, pos, rot, new Vector3(0f, 0f, -0.105f), new Vector3(0.46f, 0.62f, 0.02f), matSteelDark);
        AddLocalBox(parts, pos, rot, new Vector3(0f, 0.15f, -0.115f), new Vector3(0.22f, 0.12f, 0.01f), matSafetyYellow);
        AddLocalBox(parts, pos, rot, new Vector3(-0.10f, -0.08f, -0.115f), new Vector3(0.04f, 0.04f, 0.015f), matGreenLed);
        AddLocalBox(parts, pos, rot, new Vector3(-0.02f, -0.08f, -0.115f), new Vector3(0.04f, 0.04f, 0.015f), matGreenLed);
        AddLocalBox(parts, pos, rot, new Vector3(0.06f, -0.08f, -0.115f), new Vector3(0.04f, 0.04f, 0.015f), matRedLed);
        AddLocalBox(parts, pos, rot, new Vector3(-0.14f, 0.75f, -0.04f), new Vector3(0.03f, 0.85f, 0.03f), matSteelDark);
        AddLocalBox(parts, pos, rot, new Vector3(0.14f, 0.75f, -0.04f), new Vector3(0.03f, 0.85f, 0.03f), matSteelDark);
        AddLocalBox(parts, pos, rot, new Vector3(0.55f, -0.10f, 0f), new Vector3(0.28f, 0.56f, 0.18f), matFireRed);
        AddLocalBox(parts, pos, rot, new Vector3(0.55f, -0.10f, -0.095f), new Vector3(0.22f, 0.48f, 0.01f), matSteelDark);
    }

    static void BuildBollard(List<BoxPart> parts, Vector3 basePos)
    {
        AddBox(parts, basePos + new Vector3(0f, 0.02f, 0f), new Vector3(0.26f, 0.04f, 0.26f), matSteelDark);
        AddBox(parts, basePos + new Vector3(0f, 0.52f, 0f), new Vector3(0.14f, 1.00f, 0.14f), matSafetyYellow);
        AddBox(parts, basePos + new Vector3(0f, 0.82f, 0f), new Vector3(0.146f, 0.10f, 0.146f), matSteelDark);
        AddBox(parts, basePos + new Vector3(0f, 0.62f, 0f), new Vector3(0.146f, 0.10f, 0.146f), matSteelDark);
    }

    static void AddContactShadow(Transform parent, string name, Vector3 pos, Vector2 size)
    {
        if (matContactShadow == null) return;
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        go.isStatic = true;
        var mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = matContactShadow;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
    }

    static void AddSignLabel(Transform parent, string name, string text, Vector3 pos, Quaternion rot, float width, float height)
    {
        var plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
        plate.name = name + "_Plate";
        Object.DestroyImmediate(plate.GetComponent<Collider>());
        plate.transform.SetParent(parent, false);
        plate.transform.SetPositionAndRotation(pos, rot);
        plate.transform.localScale = new Vector3(width, height, 0.02f);
        plate.isStatic = true;
        plate.GetComponent<MeshRenderer>().sharedMaterial = matSteelDark;

        var txtGo = new GameObject(name + "_Text");
        txtGo.transform.SetParent(parent, false);
        txtGo.transform.SetPositionAndRotation(pos - (rot * Vector3.forward) * 0.015f, rot);
        txtGo.isStatic = true;

        var tmp = txtGo.AddComponent<TextMeshPro>();
        tmp.font = TMP_Settings.defaultFontAsset;
        tmp.text = text;
        tmp.fontStyle = FontStyles.Bold;
        tmp.fontSize = 1.35f;
        tmp.color = new Color(0.95f, 0.96f, 0.98f);
        tmp.characterSpacing = 4f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.rectTransform.sizeDelta = new Vector2(width * 0.95f, height * 0.9f);
        tmp.raycastTarget = false;
        var mr = txtGo.GetComponent<MeshRenderer>();
        mr.shadowCastingMode = ShadowCastingMode.Off;
    }

    static Transform MakeGroup(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.isStatic = true;
        return go.transform;
    }

    static void AddBox(List<BoxPart> list, Vector3 center, Vector3 size, Material mat)
    {
        list.Add(new BoxPart { center = center, size = size, rot = Quaternion.identity, mat = mat });
    }

    static void AddBoxRot(List<BoxPart> list, Vector3 center, Vector3 size, Quaternion rot, Material mat)
    {
        list.Add(new BoxPart { center = center, size = size, rot = rot, mat = mat });
    }

    static void AddLocalBox(List<BoxPart> list, Vector3 origin, Quaternion originRot, Vector3 localCenter, Vector3 size, Material mat)
    {
        list.Add(new BoxPart
        {
            center = origin + originRot * localCenter,
            size = size,
            rot = originRot,
            mat = mat
        });
    }

    static void BakePartsToMeshes(Transform parent, string meshBaseName, List<BoxPart> parts, bool isGlass = false)
    {
        if (parts == null || parts.Count == 0) return;

        var byMat = new Dictionary<Material, List<BoxPart>>();
        var matOrder = new List<Material>();
        foreach (var p in parts)
        {
            if (p.mat == null) continue;
            if (!byMat.TryGetValue(p.mat, out var list))
            {
                list = new List<BoxPart>();
                byMat[p.mat] = list;
                matOrder.Add(p.mat);
            }
            list.Add(p);
        }

        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var uvs = new List<Vector2>();
        var subMeshTris = new List<List<int>>();

        foreach (var mat in matOrder)
        {
            var tris = new List<int>();
            foreach (var box in byMat[mat])
            {
                AppendBoxGeometry(box.center, box.size, box.rot, verts, norms, uvs, tris);
            }
            subMeshTris.Add(tris);
        }

        string path = $"{AssetDir}/{meshBaseName}.asset";
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        bool isNew = false;
        if (mesh == null)
        {
            mesh = new Mesh { name = meshBaseName };
            isNew = true;
        }
        else
        {
            mesh.Clear();
        }

        if (verts.Count > 65000)
            mesh.indexFormat = IndexFormat.UInt32;

        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.SetUVs(0, uvs);
        mesh.subMeshCount = matOrder.Count;
        for (int i = 0; i < matOrder.Count; i++)
            mesh.SetTriangles(subMeshTris[i], i);

        mesh.RecalculateBounds();
        mesh.RecalculateTangents();

        if (isNew)
            AssetDatabase.CreateAsset(mesh, path);
        else
            EditorUtility.SetDirty(mesh);

        var go = new GameObject(meshBaseName + "_Mesh");
        go.transform.SetParent(parent, false);
        go.isStatic = true;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = matOrder.ToArray();
        mr.shadowCastingMode = isGlass ? ShadowCastingMode.Off : ShadowCastingMode.On;
        mr.receiveShadows = true;
    }

    static void AppendBoxGeometry(
        Vector3 center, Vector3 size, Quaternion rot,
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris)
    {
        Vector3 h = size * 0.5f;
        AddFace(center, rot, new Vector3(-h.x, -h.y, h.z), new Vector3(h.x, -h.y, h.z), new Vector3(h.x, h.y, h.z), new Vector3(-h.x, h.y, h.z), Vector3.forward, size.x, size.y, verts, norms, uvs, tris);
        AddFace(center, rot, new Vector3(h.x, -h.y, -h.z), new Vector3(-h.x, -h.y, -h.z), new Vector3(-h.x, h.y, -h.z), new Vector3(h.x, h.y, -h.z), Vector3.back, size.x, size.y, verts, norms, uvs, tris);
        AddFace(center, rot, new Vector3(h.x, -h.y, h.z), new Vector3(h.x, -h.y, -h.z), new Vector3(h.x, h.y, -h.z), new Vector3(h.x, h.y, h.z), Vector3.right, size.z, size.y, verts, norms, uvs, tris);
        AddFace(center, rot, new Vector3(-h.x, -h.y, -h.z), new Vector3(-h.x, -h.y, h.z), new Vector3(-h.x, h.y, h.z), new Vector3(-h.x, h.y, -h.z), Vector3.left, size.z, size.y, verts, norms, uvs, tris);
        AddFace(center, rot, new Vector3(-h.x, h.y, h.z), new Vector3(h.x, h.y, h.z), new Vector3(h.x, h.y, -h.z), new Vector3(-h.x, h.y, -h.z), Vector3.up, size.x, size.z, verts, norms, uvs, tris);
        AddFace(center, rot, new Vector3(-h.x, -h.y, -h.z), new Vector3(h.x, -h.y, -h.z), new Vector3(h.x, -h.y, h.z), new Vector3(-h.x, -h.y, h.z), Vector3.down, size.x, size.z, verts, norms, uvs, tris);
    }

    static void AddFace(
        Vector3 center, Quaternion rot,
        Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, Vector3 localNormal,
        float uScale, float vScale,
        List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris)
    {
        int i = verts.Count;
        verts.Add(center + rot * v0);
        verts.Add(center + rot * v1);
        verts.Add(center + rot * v2);
        verts.Add(center + rot * v3);

        Vector3 n = rot * localNormal;
        norms.Add(n); norms.Add(n); norms.Add(n); norms.Add(n);

        bool isSmallScreen = uScale < 0.65f && vScale < 0.40f;
        float uMax = isSmallScreen ? 1f : uScale * 0.5f;
        float vMax = isSmallScreen ? 1f : vScale * 0.5f;
        uvs.Add(new Vector2(0f, 0f));
        uvs.Add(new Vector2(uMax, 0f));
        uvs.Add(new Vector2(uMax, vMax));
        uvs.Add(new Vector2(0f, vMax));

        tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
        tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
    }
}
