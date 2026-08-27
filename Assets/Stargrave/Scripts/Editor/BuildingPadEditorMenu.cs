using UnityEditor;
using UnityEngine;

/// <summary>Editor helpers for hybrid building terrain pads and auto-scatter.</summary>
public static class BuildingPadEditorMenu
{
    static readonly string[] DefaultBuildingPrefabPaths =
    {
        // Short / single-storey
        "Assets/ThirdParty/Kenny/ModularBuildingsKit/Models/FBX format/building-sample-house-a.fbx",
        "Assets/ThirdParty/Kenny/ModularBuildingsKit/Models/FBX format/building-sample-house-b.fbx",
        "Assets/ThirdParty/Kenny/ModularBuildingsKit/Models/FBX format/building-sample-house-c.fbx",
        // Tall / multi-storey
        "Assets/ithappy/Cartoon_City_Free/Prefabs/Buildings/Eco_Building_Grid.prefab",
        "Assets/ithappy/Cartoon_City_Free/Prefabs/Buildings/Eco_Building_Terrace.prefab",
        "Assets/ithappy/Cartoon_City_Free/Prefabs/Buildings/Eco_Building_Slope.prefab",
        "Assets/ithappy/Cartoon_City_Free/Prefabs/Buildings/Regular_Building_TwistedTower_Large.prefab",
    };

    [MenuItem("Tools/Stargrave/Buildings/Regenerate Planet With Pads")]
    public static void RegeneratePlanetWithPads()
    {
        PlanetBuildingPads.RegeneratePlanetWithPads();
        var planet = PlanetBuildingPads.FindPlanet();
        if (planet != null)
            EditorUtility.SetDirty(planet.gameObject);
        if (!Application.isPlaying)
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
    }

    [MenuItem("Tools/Stargrave/Buildings/Setup Building Spawner")]
    public static void SetupBuildingSpawner()
    {
        var spawner = Object.FindFirstObjectByType<BuildingSpawner>();
        if (spawner == null)
        {
            var go = new GameObject("BuildingSpawner");
            spawner = go.AddComponent<BuildingSpawner>();
            Undo.RegisterCreatedObjectUndo(go, "Create Building Spawner");
        }

        Undo.RecordObject(spawner, "Setup Building Spawner");
        if (spawner.variants == null)
            spawner.variants = new System.Collections.Generic.List<BuildingSpawnVariant>();
        spawner.variants.Clear();

        for (int i = 0; i < DefaultBuildingPrefabPaths.Length; i++)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(DefaultBuildingPrefabPaths[i]);
            if (prefab == null)
                continue;
            spawner.variants.Add(new BuildingSpawnVariant
            {
                name = prefab.name,
                prefab = prefab,
                sizeClass = BuildingSpawner.InferSizeClass(prefab.name),
                weight = 1f,
                scale = 1f
            });
        }

        EditorUtility.SetDirty(spawner);
        Selection.activeGameObject = spawner.gameObject;
        Debug.Log($"[BuildingSpawner] Setup complete with {spawner.variants.Count} building variant(s). Use 'Spawn Buildings Now' or enter Play.");
    }

    [MenuItem("Tools/Stargrave/Buildings/Spawn Buildings Now")]
    public static void SpawnBuildingsNow()
    {
        var spawner = Object.FindFirstObjectByType<BuildingSpawner>();
        if (spawner == null)
        {
            SetupBuildingSpawner();
            spawner = Object.FindFirstObjectByType<BuildingSpawner>();
        }
        if (spawner == null)
            return;

        Undo.RegisterFullObjectHierarchyUndo(spawner.gameObject, "Spawn Buildings");
        spawner.SpawnBuildings();
        EditorUtility.SetDirty(spawner.gameObject);
        if (!Application.isPlaying)
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
    }

    [MenuItem("GameObject/Stargrave/Building Pad", false, 12)]
    public static void CreateBuildingPad(MenuCommand command)
    {
        var planet = PlanetBuildingPads.FindPlanet();
        var go = new GameObject("BuildingPad");
        var pad = go.AddComponent<BuildingPad>();

        if (planet != null)
        {
            Vector3 center = planet.transform.position;
            Vector3 axis = planet.transform.up;
            if (SceneView.lastActiveSceneView != null && SceneView.lastActiveSceneView.camera != null)
            {
                Camera cam = SceneView.lastActiveSceneView.camera;
                Vector3 guess = cam.transform.position + cam.transform.forward * 40f;
                Vector3 a = guess - center;
                if (a.sqrMagnitude > 1e-6f)
                    axis = a.normalized;
            }

            if (!planet.IsGenerated)
                planet.GeneratePlanet();

            if (BuildingPadSiteEvaluator.TryFindSuitableSite(
                    planet, axis, BuildingPadSiteEvaluator.Settings.FromPad(pad),
                    out Vector3 bestAxis, out var report))
            {
                pad.ApplyPoseOnAxis(planet, bestAxis);
                Debug.Log($"[BuildingPad] Created on suitable site: {report.reason}");
            }
            else
            {
                pad.ApplyPoseOnAxis(planet, axis);
                Debug.LogWarning($"[BuildingPad] No suitable dry/flat site near view aim: {report.reason}. Placed on aim point — move and Find Suitable Site Nearby.");
            }
        }

        GameObjectUtility.SetParentAndAlign(go, command.context as GameObject);
        Undo.RegisterCreatedObjectUndo(go, "Create Building Pad");
        Selection.activeGameObject = go;
    }
}

[CustomEditor(typeof(BuildingPad))]
public sealed class BuildingPadInspector : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        var pad = (BuildingPad)target;

        EditorGUILayout.Space(6f);
        var report = pad.EvaluateSite();
        if (report.isValid)
            EditorGUILayout.HelpBox(report.reason, MessageType.Info);
        else
            EditorGUILayout.HelpBox(report.reason, MessageType.Warning);

        if (GUILayout.Button("Find Suitable Site Nearby"))
        {
            Undo.RecordObject(pad.transform, "Find Suitable Building Site");
            pad.FindSuitableSiteNearby();
            EditorUtility.SetDirty(pad.transform);
        }

        if (GUILayout.Button("Snap To Aimed Surface (no search)"))
        {
            Undo.RecordObject(pad.transform, "Snap Building Pad");
            pad.SnapToPlanetSurface(false);
            EditorUtility.SetDirty(pad.transform);
        }

        if (GUILayout.Button("Regenerate Planet With Pads"))
        {
            BuildingPadEditorMenu.RegeneratePlanetWithPads();
        }
    }
}

[CustomEditor(typeof(BuildingSpawner))]
public sealed class BuildingSpawnerInspector : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        var spawner = (BuildingSpawner)target;
        EditorGUILayout.Space(6f);
        EditorGUILayout.HelpBox(
            "Towns sit on a street-cross grid. Size and which lots are filled vary — " +
            "not every town is a full symmetrical ring, but buildings stay on the grid and face the street.",
            MessageType.Info);

        if (GUILayout.Button("Spawn Buildings Now"))
        {
            Undo.RegisterFullObjectHierarchyUndo(spawner.gameObject, "Spawn Buildings");
            spawner.SpawnBuildings();
            EditorUtility.SetDirty(spawner);
        }

        if (GUILayout.Button("Clear Spawned Buildings"))
        {
            Undo.RegisterFullObjectHierarchyUndo(spawner.gameObject, "Clear Buildings");
            spawner.ClearSpawned();
            PlanetBuildingPads.RegeneratePlanetWithPads();
            EditorUtility.SetDirty(spawner);
        }
    }
}
