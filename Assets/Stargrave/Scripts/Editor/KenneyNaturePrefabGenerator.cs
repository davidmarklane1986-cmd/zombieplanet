using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Turns Kenney Nature Kit GLB models into categorized spawn prefabs for <see cref="SimpleFoliageSpawner"/>.
/// </summary>
public static class KenneyNaturePrefabGenerator
{
    public const string GlbRoot = "Assets/ThirdParty/Kenny/NatureKit/Models/GLTF format";

    public static string PrefabRoot => FoliageRichPresetBuilder.KenneyFloraPaths.PrefabRoot;

    public enum FloraCategory
    {
        Skip,
        Trees,
        Palms,
        GroundCover,
        Understory,
        Rocks
    }

    [MenuItem("Tools/Kenney/Generate Nature Flora Prefabs")]
    public static void GenerateFromMenu()
    {
        int created = GenerateAll();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[Kenney] Generated/updated {created} nature flora prefabs under {PrefabRoot}.");
    }

    public static int GenerateAll()
    {
        if (!AssetDatabase.IsValidFolder(GlbRoot))
        {
            Debug.LogWarning($"[Kenney] GLB folder not found: {GlbRoot}");
            return 0;
        }

        EnsureFolder(PrefabRoot);
        foreach (FloraCategory cat in System.Enum.GetValues(typeof(FloraCategory)))
        {
            if (cat == FloraCategory.Skip)
                continue;
            EnsureFolder($"{PrefabRoot}/{cat}");
        }

        int created = 0;
        string[] glbGuids = AssetDatabase.FindAssets("", new[] { GlbRoot });
        foreach (string guid in glbGuids)
        {
            string glbPath = AssetDatabase.GUIDToAssetPath(guid);
            if (!glbPath.EndsWith(".glb", System.StringComparison.OrdinalIgnoreCase))
                continue;

            string fileName = Path.GetFileNameWithoutExtension(glbPath);
            FloraCategory category = Classify(fileName);
            if (category == FloraCategory.Skip)
                continue;

            if (TryCreatePrefab(glbPath, fileName, category))
                created++;
        }

        return created;
    }

    public static bool HasGeneratedPrefabs()
    {
        if (!AssetDatabase.IsValidFolder(PrefabRoot))
            return false;

        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { PrefabRoot });
        return guids != null && guids.Length > 0;
    }

    public static FloraCategory Classify(string fileName)
    {
        string n = fileName.ToLowerInvariant();

        if (n.StartsWith("tree_palm"))
            return FloraCategory.Palms;

        if (n.StartsWith("tree_"))
        {
            if (n.Contains("_fall") || n.Contains("_dark"))
                return FloraCategory.Skip;
            return FloraCategory.Trees;
        }

        if (n.StartsWith("grass_") || n.StartsWith("flower_") || n.StartsWith("plant_flat"))
            return FloraCategory.GroundCover;

        if (n.StartsWith("plant_bush") || n.StartsWith("mushroom_") || n == "log" || n.StartsWith("log_") || n.StartsWith("stump"))
            return FloraCategory.Understory;

        if (n.StartsWith("rock_") || n.StartsWith("stone_"))
            return FloraCategory.Rocks;

        return FloraCategory.Skip;
    }

    static bool TryCreatePrefab(string glbPath, string fileName, FloraCategory category)
    {
        string prefabPath = $"{PrefabRoot}/{category}/{fileName}.prefab";
        GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(glbPath);
        if (source == null)
            return false;

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
        if (instance == null)
            instance = Object.Instantiate(source);

        instance.name = fileName;
        EnsureUrpMaterials(instance);

        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
        Object.DestroyImmediate(instance);
        return prefab != null;
    }

    static void EnsureUrpMaterials(GameObject root)
    {
        Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
        if (urpLit == null)
            return;

        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            Material[] mats = renderer.sharedMaterials;
            bool changed = false;

            for (int i = 0; i < mats.Length; i++)
            {
                Material mat = mats[i];
                if (mat == null)
                    continue;

                string shaderName = mat.shader != null ? mat.shader.name : string.Empty;
                bool needsUpgrade = string.IsNullOrEmpty(shaderName)
                    || shaderName.Contains("InternalError")
                    || shaderName == "Standard"
                    || shaderName.StartsWith("Legacy Shaders/");

                if (!needsUpgrade)
                    continue;

                Texture baseMap = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
                if (baseMap == null && mat.HasProperty("_BaseMap"))
                    baseMap = mat.GetTexture("_BaseMap");

                Color baseColor = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
                if (mat.HasProperty("_BaseColor"))
                    baseColor = mat.GetColor("_BaseColor");

                mat.shader = urpLit;
                if (baseMap != null)
                    mat.SetTexture("_BaseMap", baseMap);
                mat.SetColor("_BaseColor", baseColor);
                changed = true;
            }

            if (changed)
                renderer.sharedMaterials = mats;
        }
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;

        string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        string folder = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, folder);
    }
}
