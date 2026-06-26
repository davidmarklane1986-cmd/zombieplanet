using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Creates and applies rich planet flora profiles; auto-applies before Play when the scene still uses sparse rules.
/// </summary>
[InitializeOnLoad]
public static class FoliageRichFloraEditor
{
    const string ProfilePath = "Assets/Stargrave/Resources/RichPlanetFlora.asset";

    static FoliageRichFloraEditor()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.ExitingEditMode)
            return;

        var spawner = Object.FindFirstObjectByType<SimpleFoliageSpawner>();
        if (spawner == null)
            return;

        if (!ShouldAutoUpgrade(spawner))
            return;

        var profile = EnsureProfileAsset();
        spawner.profile = profile;
        spawner.useProfileWhenSet = true;
        spawner.loadProfileFromResources = true;
        profile.ApplyTo(spawner);
        EditorUtility.SetDirty(spawner);
        Debug.Log("[Rich Flora] Applied RichPlanetFlora profile before Play.");
    }

    static bool ShouldAutoUpgrade(SimpleFoliageSpawner spawner)
    {
        if (spawner.profile != null)
            return false;

        if (spawner.spawnRules == null || spawner.spawnRules.Count == 0)
            return true;

        int total = 0;
        bool hasClustering = false;
        for (int i = 0; i < spawner.spawnRules.Count; i++)
        {
            total += spawner.spawnRules[i].count;
            if (spawner.spawnRules[i].distribution != SimpleFoliageSpawner.SpawnDistribution.Scattered)
                hasClustering = true;
        }

        if (total < 1200)
            return true;

        return !hasClustering && spawner.spawnRules.Count > 10;
    }

    [MenuItem("Tools/Create Rich Planet Flora Profile Asset")]
    public static FoliageSpawnProfile EnsureProfileAssetMenu()
    {
        var profile = EnsureProfileAsset();
        Selection.activeObject = profile;
        return profile;
    }

    public static FoliageSpawnProfile EnsureProfileAsset()
    {
        if (!KenneyNaturePrefabGenerator.HasGeneratedPrefabs())
            KenneyNaturePrefabGenerator.GenerateAll();

        var profile = AssetDatabase.LoadAssetAtPath<FoliageSpawnProfile>(ProfilePath);
        if (profile == null)
        {
            System.IO.Directory.CreateDirectory("Assets/Stargrave/Resources");
            profile = ScriptableObject.CreateInstance<FoliageSpawnProfile>();
            AssetDatabase.CreateAsset(profile, ProfilePath);
        }

        FoliageRichPresetBuilder.PopulateProfile(profile);
        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();
        return profile;
    }

    public static void SetupRichPlanetFlora()
    {
        var spawner = FoliageAutoSetup.EnsureSimpleSpawner();
        var profile = EnsureProfileAsset();

        spawner.ClearAll();
        spawner.profile = profile;
        spawner.useProfileWhenSet = true;
        spawner.loadProfileFromResources = true;
        profile.ApplyTo(spawner);

        EditorUtility.SetDirty(spawner);
        EditorUtility.SetDirty(profile);
        EditorSceneManager.MarkSceneDirty(spawner.gameObject.scene);
        Selection.activeGameObject = spawner.gameObject;
        Debug.Log($"[Rich Flora] Profile at {ProfilePath} with {profile.spawnRules.Count} rules. Enter Play to spawn.");
    }
}
