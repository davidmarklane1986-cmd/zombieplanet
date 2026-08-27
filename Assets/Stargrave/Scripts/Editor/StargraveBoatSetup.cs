#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds Boat_Canoe prefab from Kenny NatureKit canoe FBX and wires a BoatSpawner in the scene.
/// Run: Tools/Stargrave/Build Boat Prefab
/// </summary>
[InitializeOnLoad]
public static class StargraveBoatSetup
{
    const string CanoeFbx = "Assets/ThirdParty/Kenny/NatureKit/Models/FBX format/canoe.fbx";
    const string PrefabDir = "Assets/Stargrave/Prefabs/Vehicles";
    const string ResourcesDir = "Assets/Stargrave/Resources/Vehicles";
    const string PrefabPath = PrefabDir + "/Boat_Canoe.prefab";
    const string ResourcesPrefabPath = ResourcesDir + "/Boat_Canoe.prefab";
    const string TriggerPath = "Assets/Stargrave/.build_boat_prefab";

    static StargraveBoatSetup()
    {
        EditorApplication.delayCall += () =>
        {
            if (!File.Exists(TriggerPath))
                return;
            try { File.Delete(TriggerPath); } catch { /* ignore */ }
            BuildBoatPrefab();
        };
    }

    [MenuItem("Tools/Stargrave/Build Boat Prefab")]
    public static void BuildBoatPrefab()
    {
        EnsureFolder(PrefabDir);
        EnsureFolder(ResourcesDir);

        GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(CanoeFbx);
        if (source == null)
        {
            Debug.LogError($"[BoatSetup] Missing canoe FBX at {CanoeFbx}");
            return;
        }

        var root = new GameObject("Boat_Canoe");
        try
        {
            GameObject visual = (GameObject)PrefabUtility.InstantiatePrefab(source);
            PrefabUtility.UnpackPrefabInstance(visual, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            visual.name = "CanoeVisual";
            visual.transform.SetParent(root.transform, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            // Kenny canoe is authored flat; scale up to readable world size on the planet.
            visual.transform.localScale = Vector3.one * 2.4f;

            EnsureMeshColliders(visual);

            var rb = root.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.mass = 40f;
            rb.linearDamping = 0.2f;
            rb.angularDamping = 1.5f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

            var boat = root.AddComponent<BoatController>();
            var seatGo = new GameObject("Seat");
            seatGo.transform.SetParent(root.transform, false);
            seatGo.transform.localPosition = new Vector3(0f, 0.65f, 0.1f);
            boat.seat = seatGo.transform;

            var exitGo = new GameObject("Exit");
            exitGo.transform.SetParent(root.transform, false);
            exitGo.transform.localPosition = new Vector3(2.2f, 0.4f, 0f);
            boat.exitPoint = exitGo.transform;

            root.AddComponent<BoatInteractable>();

            // Interact trigger volume (does not block movement).
            var triggerGo = new GameObject("InteractTrigger");
            triggerGo.transform.SetParent(root.transform, false);
            var sphere = triggerGo.AddComponent<SphereCollider>();
            sphere.isTrigger = true;
            sphere.radius = 3.5f;

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            PrefabUtility.SaveAsPrefabAsset(root, ResourcesPrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            WireBoatSpawner(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            Debug.Log($"[BoatSetup] Saved {PrefabPath} and {ResourcesPrefabPath}. BoatSpawner wired if present/created.");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    [MenuItem("Tools/Stargrave/Spawn Boats Now")]
    public static void SpawnBoatsNow()
    {
        var spawner = Object.FindFirstObjectByType<BoatSpawner>();
        if (spawner == null)
        {
            BuildBoatPrefab();
            spawner = Object.FindFirstObjectByType<BoatSpawner>();
        }
        if (spawner == null)
        {
            Debug.LogWarning("[BoatSetup] No BoatSpawner in scene.");
            return;
        }
        if (spawner.boatPrefab == null)
            spawner.boatPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        spawner.SpawnBoats();
    }

    static void WireBoatSpawner(GameObject prefab)
    {
        var spawner = Object.FindFirstObjectByType<BoatSpawner>();
        if (spawner == null)
        {
            var go = new GameObject("BoatSpawner");
            spawner = go.AddComponent<BoatSpawner>();
            Undo.RegisterCreatedObjectUndo(go, "Create Boat Spawner");
        }

        Undo.RecordObject(spawner, "Wire Boat Prefab");
        spawner.boatPrefab = prefab;
        EditorUtility.SetDirty(spawner);
    }

    static void EnsureMeshColliders(GameObject root)
    {
        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter mf = filters[i];
            if (mf == null || mf.sharedMesh == null)
                continue;
            MeshCollider mc = mf.GetComponent<MeshCollider>();
            if (mc == null)
                mc = mf.gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = mf.sharedMesh;
            mc.convex = true; // dynamic rigidbody-friendly
            mc.isTrigger = false;
        }
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;
        string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        string name = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            EnsureFolder(parent);
        if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(name))
            AssetDatabase.CreateFolder(parent, name);
    }
}
#endif
