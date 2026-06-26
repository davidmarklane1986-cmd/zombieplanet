using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds dense, biome-sensible flora rules from project prefabs.
/// Used by editor setup menus and <see cref="FoliageSpawnProfile"/> assets.
/// </summary>
public static class FoliageRichPresetBuilder
{
    const string NatureKitFoliage = "Assets/Proxy Games/Stylized Nature Kit Lite/Prefabs/Foliage";
    const string NatureKitRocks = "Assets/Proxy Games/Stylized Nature Kit Lite/Prefabs/Rocks";
    const string CityVegetation = "Assets/ithappy/Cartoon_City_Free/Prefabs/Vegetation";
    const string KennyNaturePrefabs = KenneyFloraPaths.PrefabRoot;

    public static class KenneyFloraPaths
    {
        public const string PrefabRoot = "Assets/ThirdParty/Kenny/NatureKit/Prefabs";
        public const string Trees = PrefabRoot + "/Trees";
        public const string Palms = PrefabRoot + "/Palms";
        public const string GroundCover = PrefabRoot + "/GroundCover";
        public const string Understory = PrefabRoot + "/Understory";
        public const string Rocks = PrefabRoot + "/Rocks";
    }

    public static void PopulateProfile(FoliageSpawnProfile profile)
    {
        if (profile == null)
            return;

        profile.spawnRules = BuildRichEnvironmentRules();
        profile.globalDensityMultiplier = 1.2f;
        profile.excludeUnderwater = true;
        profile.patchNoiseStrength = 0.55f;
        profile.forceDoubleSidedAll = true;
        profile.globalMinSeparation = 0.35f;
    }

    /// <summary>
    /// Fresh, clean slate: returns NO rules. The previous rich preset (Forest Canopy, Understory,
    /// Floor Debris, Meadow Grass, Flowers, Coastal, Rocky Outcrops) was removed deliberately so
    /// placement can be rebuilt from scratch. Add new <see cref="SimpleFoliageSpawner.BiomeSpawnRule"/>
    /// entries below to define the fresh asset-placement rules.
    ///
    /// Prefab sources still available via helpers: LoadPrefab(path), LoadPrefabs(folder),
    /// LoadPrefabsByNames(folder, token), FilterPrefabsByName(list, tokens...).
    /// Known prefab roots: NatureKitFoliage, NatureKitRocks, CityVegetation, KenneyFloraPaths.*
    /// </summary>
    public static List<SimpleFoliageSpawner.BiomeSpawnRule> BuildRichEnvironmentRules()
    {
        var rules = new List<SimpleFoliageSpawner.BiomeSpawnRule>();

        // ── Fresh rules go here ───────────────────────────────────────────────
        // (intentionally empty — clean slate)

        return rules;
    }

    static GameObject LoadPrefab(string assetPath)
    {
#if UNITY_EDITOR
        return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
#else
        return null;
#endif
    }

    static List<GameObject> LoadPrefabs(string folder)
    {
        var list = new List<GameObject>();
#if UNITY_EDITOR
        foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:Prefab", new[] { folder }))
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
            if (prefab != null)
                list.Add(prefab);
        }
#endif
        return list;
    }

    static List<GameObject> LoadPrefabsByNames(string folder, string nameContains)
    {
        var list = new List<GameObject>();
#if UNITY_EDITOR
        foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:Prefab", new[] { folder }))
        {
            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            if (path.ToLowerInvariant().Contains(nameContains.ToLowerInvariant()))
            {
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null)
                    list.Add(prefab);
            }
        }
#endif
        return list;
    }

    static List<GameObject> FilterPrefabsByName(List<GameObject> source, params string[] nameTokens)
    {
        var list = new List<GameObject>();
        if (source == null)
            return list;

        for (int i = 0; i < source.Count; i++)
        {
            GameObject prefab = source[i];
            if (prefab == null)
                continue;

            string n = prefab.name.ToLowerInvariant();
            for (int t = 0; t < nameTokens.Length; t++)
            {
                if (n.Contains(nameTokens[t].ToLowerInvariant()))
                {
                    list.Add(prefab);
                    break;
                }
            }
        }

        return list;
    }

    static List<GameObject> MergeLists(List<GameObject> a, List<GameObject> b)
    {
        var merged = new List<GameObject>();
        if (a != null)
            merged.AddRange(a);
        if (b != null)
            merged.AddRange(b);
        return merged;
    }

    static List<GameObject> ListOrSingle(List<GameObject> source)
    {
        return source != null && source.Count > 0 ? source : new List<GameObject>();
    }

    static List<GameObject> ListOfNonNull(params GameObject[] items)
    {
        var list = new List<GameObject>();
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] != null)
                list.Add(items[i]);
        }

        return list;
    }
}
