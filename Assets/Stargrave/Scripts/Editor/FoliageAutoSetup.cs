using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

public class FoliageAutoSetup
{
    const string NatureKitFoliage = "Assets/Proxy Games/Stylized Nature Kit Lite/Prefabs/Foliage";
    const string NatureKitRocks = "Assets/Proxy Games/Stylized Nature Kit Lite/Prefabs/Rocks";
    const string CityVegetation = "Assets/ithappy/Cartoon_City_Free/Prefabs/Vegetation";

    [MenuItem("Tools/Setup Foliage")]
    public static void Setup()
    {
        GameObject spawnerObj = GameObject.Find("FoliageSpawner");
        if (spawnerObj == null)
        {
            spawnerObj = new GameObject("FoliageSpawner");
            Undo.RegisterCreatedObjectUndo(spawnerObj, "Create Foliage Spawner");
        }

        var spawner = spawnerObj.GetComponent<Stargrave.FoliageSpawner>();
        if (spawner == null)
            spawner = Undo.AddComponent<Stargrave.FoliageSpawner>(spawnerObj);

        var foliageList = LoadPrefabs(NatureKitFoliage);
        var rockList = LoadPrefabs(NatureKitRocks);

        Planet planet = Object.FindFirstObjectByType<Planet>();
        Undo.RecordObject(spawner, "Setup Foliage Spawner");
        spawner.foliagePrefabs = foliageList;
        spawner.rockPrefabs = rockList;
        spawner.spawnRadius = 35f;
        spawner.foliageCount = 150;
        spawner.rockCount = 60;
        spawner.minDistanceBetweenObjects = 1.5f;
        spawner.usePoissonDistribution = true;
        spawner.useDensityBasedOnGreen = true;
        spawner.minGreennessForSpawn = 0.1f;
        spawner.foliageOnlyOnGreen = false;
        spawner.planet = planet;
        if (planet != null)
            spawner.planetCenter = planet.transform.position;

        int groundLayer = LayerMask.NameToLayer("Ground");
        spawner.groundLayer = 1 << (groundLayer == -1 ? 0 : groundLayer);

        spawner.transform.up = (spawner.transform.position - Vector3.zero).normalized;
        spawner.SpawnAll();
        Debug.Log($"Foliage Spawner setup complete ({foliageList.Count} foliage, {rockList.Count} rocks).");
    }

    [MenuItem("Tools/Setup Simple Foliage")]
    public static void SetupSimpleFoliage()
    {
        var spawner = EnsureSimpleSpawner();
        spawner.spawnRules = BuildBasicRules();
        ApplyCommonSpawnerSettings(spawner);
        Debug.Log("[Simple Foliage] Basic rules applied.");
    }

    [MenuItem("Tools/Setup Rich Planet Flora")]
    public static void SetupRichPlanetFloraMenu()
    {
        FoliageRichFloraEditor.SetupRichPlanetFlora();
    }

    public static SimpleFoliageSpawner EnsureSimpleSpawner()
    {
        var spawnerObj = GameObject.Find("SimpleFoliageSpawner");
        if (spawnerObj == null)
        {
            spawnerObj = new GameObject("SimpleFoliageSpawner");
            Undo.RegisterCreatedObjectUndo(spawnerObj, "Create Simple Foliage Spawner");
        }

        var spawner = spawnerObj.GetComponent<SimpleFoliageSpawner>();
        if (spawner == null)
            spawner = Undo.AddComponent<SimpleFoliageSpawner>(spawnerObj);
        return spawner;
    }

    [MenuItem("Tools/Setup Simple Foliage (One Rule Per Asset)")]
    public static void SetupSimpleFoliagePerAsset()
    {
        var spawner = EnsureSimpleSpawner();
        var allPrefabs = new List<GameObject>();
        allPrefabs.AddRange(LoadPrefabs(NatureKitFoliage));
        allPrefabs.AddRange(LoadPrefabs(NatureKitRocks));

        var rules = new List<SimpleFoliageSpawner.BiomeSpawnRule>();
        var greenMatches = new List<SimpleFoliageSpawner.BiomeColorMatch>
        {
            SimpleFoliageSpawner.BiomeColorMatch.GreenArea,
            SimpleFoliageSpawner.BiomeColorMatch.DarkGreen,
            SimpleFoliageSpawner.BiomeColorMatch.LightGreen
        };
        var rockMatches = new List<SimpleFoliageSpawner.BiomeColorMatch> { SimpleFoliageSpawner.BiomeColorMatch.BrownGray };

        foreach (var prefab in allPrefabs)
        {
            var n = prefab.name.ToLowerInvariant();
            bool isTree = n.Contains("spruce") || n.Contains("tree");
            bool isRock = n.Contains("rock") || n.Contains("mountain") || n.Contains("cliff");

            rules.Add(new SimpleFoliageSpawner.BiomeSpawnRule
            {
                name = prefab.name,
                prefabs = new List<GameObject> { prefab },
                count = isTree ? 25 : (isRock ? 15 : 50),
                colorMatches = isRock ? rockMatches : greenMatches,
                minDistanceBetween = isTree ? 4f : (isRock ? 2f : 1.2f),
                maxElevation = isTree ? 0.85f : 1f,
                alignToPlanetCenter = isTree,
                useGreennessProbability = !isRock
            });
        }

        Undo.RecordObject(spawner, "Setup Simple Foliage Per Asset");
        spawner.spawnRules = rules;
        ApplyCommonSpawnerSettings(spawner);
        Debug.Log($"[Simple Foliage] Created {rules.Count} per-asset rules.");
    }

    static void ApplyCommonSpawnerSettings(SimpleFoliageSpawner spawner)
    {
        Undo.RecordObject(spawner, "Setup Foliage Spawner Settings");
        spawner.globalDensityMultiplier = 1.15f;
        spawner.excludeUnderwater = true;
        spawner.patchNoiseStrength = 0.55f;
        spawner.forceDoubleSidedAll = true;
        spawner.logResults = true;
    }

    static List<GameObject> LoadPrefabs(string folder)
    {
        var list = new List<GameObject>();
        foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { folder }))
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            if (prefab != null)
                list.Add(prefab);
        }

        return list;
    }

    static GameObject LoadPrefabByName(string folder, string nameContains)
    {
        foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { folder }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.ToLowerInvariant().Contains(nameContains.ToLowerInvariant()))
                return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        return null;
    }

    static List<GameObject> LoadPrefabsByNames(string folder, params string[] nameContains)
    {
        var list = new List<GameObject>();
        foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { folder }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var lower = path.ToLowerInvariant();
            for (int i = 0; i < nameContains.Length; i++)
            {
                if (lower.Contains(nameContains[i].ToLowerInvariant()))
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab != null)
                        list.Add(prefab);
                    break;
                }
            }
        }

        return list;
    }

    static List<SimpleFoliageSpawner.BiomeSpawnRule> BuildBasicRules()
    {
        var foliageList = LoadPrefabs(NatureKitFoliage);
        var rockList = LoadPrefabs(NatureKitRocks);

        var trees = new List<GameObject>();
        var grass = new List<GameObject>();
        foreach (var p in foliageList)
        {
            var n = p.name.ToLowerInvariant();
            if (n.Contains("spruce") || n.Contains("tree") || n.Contains("log") || n.Contains("stump"))
                trees.Add(p);
            else
                grass.Add(p);
        }

        var rules = new List<SimpleFoliageSpawner.BiomeSpawnRule>
        {
            new SimpleFoliageSpawner.BiomeSpawnRule
            {
                name = "Trees",
                prefabs = trees.Count > 0 ? trees : foliageList,
                count = 120,
                distribution = SimpleFoliageSpawner.SpawnDistribution.Clustered,
                clusterCount = 36,
                clusterRadius = 16f,
                colorMatches = new List<SimpleFoliageSpawner.BiomeColorMatch>
                {
                    SimpleFoliageSpawner.BiomeColorMatch.DarkGreen,
                    SimpleFoliageSpawner.BiomeColorMatch.GreenArea
                },
                minDistanceBetween = 3.5f,
                maxElevation = 0.82f,
                alignToPlanetCenter = true,
                registerClusterCenters = true
            },
            new SimpleFoliageSpawner.BiomeSpawnRule
            {
                name = "Grass",
                prefabs = grass.Count > 0 ? grass : foliageList,
                count = 500,
                distribution = SimpleFoliageSpawner.SpawnDistribution.MeadowFill,
                clusterCount = 80,
                clusterRadius = 9f,
                clusterMinSeparation = 14f,
                colorMatches = new List<SimpleFoliageSpawner.BiomeColorMatch> { SimpleFoliageSpawner.BiomeColorMatch.LightGreen, SimpleFoliageSpawner.BiomeColorMatch.GreenArea },
                minGreenness = 0.12f,
                minDistanceBetween = 0.7f,
                maxElevation = 0.88f
            }
        };

        if (rockList.Count > 0)
        {
            rules.Add(new SimpleFoliageSpawner.BiomeSpawnRule
            {
                name = "Rocks",
                prefabs = rockList,
                count = 80,
                colorMatches = new List<SimpleFoliageSpawner.BiomeColorMatch> { SimpleFoliageSpawner.BiomeColorMatch.BrownGray },
                useGreennessProbability = false,
                minDistanceBetween = 2f,
                minElevation = 0.55f,
                maxElevation = 1f
            });
        }

        return rules;
    }

    static List<SimpleFoliageSpawner.BiomeSpawnRule> BuildRichEnvironmentRules()
    {
        return FoliageRichPresetBuilder.BuildRichEnvironmentRules();
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
