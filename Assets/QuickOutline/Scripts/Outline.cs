//
//  Outline.cs
//  QuickOutline
//
//  Created by Chris Nolet on 3/30/18.
//  Copyright © 2018 Chris Nolet. All rights reserved.
//

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[DisallowMultipleComponent]
public class Outline : MonoBehaviour
{
    private static readonly HashSet<Mesh> registeredMeshes = new HashSet<Mesh>();
    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    public enum Mode
    {
        OutlineAll,
        OutlineVisible,
        OutlineHidden,
        OutlineAndSilhouette,
        SilhouetteOnly
    }

    public Mode OutlineMode
    {
        get { return outlineMode; }
        set
        {
            outlineMode = value;
            needsUpdate = true;
        }
    }

    public Color OutlineColor
    {
        get { return outlineColor; }
        set
        {
            outlineColor = value;
            needsUpdate = true;
        }
    }

    public float OutlineWidth
    {
        get { return outlineWidth; }
        set
        {
            outlineWidth = value;
            needsUpdate = true;
        }
    }

    [Serializable]
    private class ListVector3
    {
        public List<Vector3> data;
    }

    [SerializeField]
    private Mode outlineMode;

    [SerializeField]
    private Color outlineColor = Color.white;

    [SerializeField, Range(0f, 10f)]
    private float outlineWidth = 2f;

    [Header("Optional")]
    [SerializeField]
    private bool precomputeOutline;

    [SerializeField, HideInInspector]
    private List<Mesh> bakeKeys = new List<Mesh>();

    [SerializeField, HideInInspector]
    private List<ListVector3> bakeValues = new List<ListVector3>();

    private Renderer[] renderers;
    private Material outlineMaskMaterial;
    private Material outlineFillMaterial;
    private MaterialPropertyBlock fallbackPropertyBlock;
    private bool hasShaderMaterials;
    private bool needsUpdate;

    private void Awake()
    {
        renderers = GetComponentsInChildren<Renderer>(true);

        Material maskTemplate = Resources.Load<Material>(@"Materials/OutlineMask");
        Material fillTemplate = Resources.Load<Material>(@"Materials/OutlineFill");

        if (maskTemplate != null && fillTemplate != null)
        {
            outlineMaskMaterial = Instantiate(maskTemplate);
            outlineFillMaterial = Instantiate(fillTemplate);
            outlineMaskMaterial.name = "OutlineMask (Instance)";
            outlineFillMaterial.name = "OutlineFill (Instance)";
            hasShaderMaterials = true;
            LoadSmoothNormals();
        }
        else
        {
            hasShaderMaterials = false;
            fallbackPropertyBlock = new MaterialPropertyBlock();
        }

        needsUpdate = true;
    }

    private void OnEnable()
    {
        if (renderers == null)
            renderers = GetComponentsInChildren<Renderer>(true);

        if (hasShaderMaterials && outlineMaskMaterial != null && outlineFillMaterial != null)
        {
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var materials = renderer.sharedMaterials.ToList();
                materials.Add(outlineMaskMaterial);
                materials.Add(outlineFillMaterial);
                renderer.materials = materials.ToArray();
            }
        }
        else
        {
            ApplyFallbackHighlight(true);
        }
    }

    private void OnValidate()
    {
        needsUpdate = true;

        if ((!precomputeOutline && bakeKeys.Count != 0) || bakeKeys.Count != bakeValues.Count)
        {
            bakeKeys.Clear();
            bakeValues.Clear();
        }

        if (precomputeOutline && bakeKeys.Count == 0)
        {
            Bake();
        }
    }

    private void Update()
    {
        if (needsUpdate)
        {
            needsUpdate = false;
            UpdateMaterialProperties();
        }
    }

    private void OnDisable()
    {
        if (renderers == null)
            return;

        if (hasShaderMaterials && outlineMaskMaterial != null && outlineFillMaterial != null)
        {
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var materials = renderer.sharedMaterials.ToList();
                materials.Remove(outlineMaskMaterial);
                materials.Remove(outlineFillMaterial);
                renderer.materials = materials.ToArray();
            }
        }
        else
        {
            ApplyFallbackHighlight(false);
        }
    }

    private void OnDestroy()
    {
        if (outlineMaskMaterial != null)
            Destroy(outlineMaskMaterial);
        if (outlineFillMaterial != null)
            Destroy(outlineFillMaterial);
    }

    private void ApplyFallbackHighlight(bool highlighted)
    {
        if (renderers == null) return;
        if (fallbackPropertyBlock == null)
            fallbackPropertyBlock = new MaterialPropertyBlock();

        foreach (var renderer in renderers)
        {
            if (renderer == null) continue;
            renderer.GetPropertyBlock(fallbackPropertyBlock);
            if (highlighted)
            {
                fallbackPropertyBlock.SetColor(EmissionColorId, outlineColor * 0.45f);
            }
            else
            {
                fallbackPropertyBlock.Clear();
            }
            renderer.SetPropertyBlock(fallbackPropertyBlock);
        }
    }

    private void Bake()
    {
        var bakedMeshes = new HashSet<Mesh>();

        foreach (var meshFilter in GetComponentsInChildren<MeshFilter>())
        {
            if (meshFilter.sharedMesh == null || !bakedMeshes.Add(meshFilter.sharedMesh))
                continue;

            var smoothNormals = SmoothNormals(meshFilter.sharedMesh);
            bakeKeys.Add(meshFilter.sharedMesh);
            bakeValues.Add(new ListVector3() { data = smoothNormals });
        }
    }

    private void LoadSmoothNormals()
    {
        foreach (var meshFilter in GetComponentsInChildren<MeshFilter>())
        {
            if (meshFilter.sharedMesh == null || !registeredMeshes.Add(meshFilter.sharedMesh))
                continue;

            var index = bakeKeys.IndexOf(meshFilter.sharedMesh);
            var smoothNormals = (index >= 0) ? bakeValues[index].data : SmoothNormals(meshFilter.sharedMesh);

            meshFilter.sharedMesh.SetUVs(3, smoothNormals);

            var renderer = meshFilter.GetComponent<Renderer>();
            if (renderer != null)
            {
                CombineSubmeshes(meshFilter.sharedMesh, renderer.sharedMaterials);
            }
        }

        foreach (var skinnedMeshRenderer in GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (skinnedMeshRenderer.sharedMesh == null || !registeredMeshes.Add(skinnedMeshRenderer.sharedMesh))
                continue;

            skinnedMeshRenderer.sharedMesh.uv4 = new Vector2[skinnedMeshRenderer.sharedMesh.vertexCount];
            CombineSubmeshes(skinnedMeshRenderer.sharedMesh, skinnedMeshRenderer.sharedMaterials);
        }
    }

    private List<Vector3> SmoothNormals(Mesh mesh)
    {
        var groups = mesh.vertices.Select((vertex, index) => new KeyValuePair<Vector3, int>(vertex, index)).GroupBy(pair => pair.Key);
        var smoothNormals = new List<Vector3>(mesh.normals);

        foreach (var group in groups)
        {
            if (group.Count() == 1)
                continue;

            var smoothNormal = Vector3.zero;
            foreach (var pair in group)
                smoothNormal += smoothNormals[pair.Value];

            smoothNormal.Normalize();

            foreach (var pair in group)
                smoothNormals[pair.Value] = smoothNormal;
        }

        return smoothNormals;
    }

    private void CombineSubmeshes(Mesh mesh, Material[] materials)
    {
        if (mesh.subMeshCount == 1 || mesh.subMeshCount > materials.Length)
            return;

        mesh.subMeshCount++;
        mesh.SetTriangles(mesh.triangles, mesh.subMeshCount - 1);
    }

    private void UpdateMaterialProperties()
    {
        if (!hasShaderMaterials || outlineFillMaterial == null || outlineMaskMaterial == null)
        {
            if (enabled)
                ApplyFallbackHighlight(true);
            return;
        }

        outlineFillMaterial.SetColor("_OutlineColor", outlineColor);

        switch (outlineMode)
        {
            case Mode.OutlineAll:
                outlineMaskMaterial.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
                outlineFillMaterial.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
                outlineFillMaterial.SetFloat("_OutlineWidth", outlineWidth);
                break;

            case Mode.OutlineVisible:
                outlineMaskMaterial.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
                outlineFillMaterial.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.LessEqual);
                outlineFillMaterial.SetFloat("_OutlineWidth", outlineWidth);
                break;

            case Mode.OutlineHidden:
                outlineMaskMaterial.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
                outlineFillMaterial.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Greater);
                outlineFillMaterial.SetFloat("_OutlineWidth", outlineWidth);
                break;

            case Mode.OutlineAndSilhouette:
                outlineMaskMaterial.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.LessEqual);
                outlineFillMaterial.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
                outlineFillMaterial.SetFloat("_OutlineWidth", outlineWidth);
                break;

            case Mode.SilhouetteOnly:
                outlineMaskMaterial.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.LessEqual);
                outlineFillMaterial.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Greater);
                outlineFillMaterial.SetFloat("_OutlineWidth", 0f);
                break;
        }
    }
}
