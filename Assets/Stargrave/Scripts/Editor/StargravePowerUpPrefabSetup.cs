#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Creates a starter <see cref="PowerUpPickup"/> prefab (trigger sphere) under Assets/Stargrave/Prefabs/.
/// </summary>
public static class StargravePowerUpPrefabSetup
{
    const string PrefabDir = "Assets/Stargrave/Prefabs";
    const string PrefabPath = PrefabDir + "/PowerUp_Speed.prefab";

    [MenuItem("Tools/Stargrave/Create Sample PowerUp Prefab")]
    public static void CreateSamplePrefab()
    {
        EnsureFolder(PrefabDir);

        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "PowerUp_Speed";
        Object.DestroyImmediate(go.GetComponent<Collider>());
        var sphere = go.AddComponent<SphereCollider>();
        sphere.isTrigger = true;
        sphere.radius = 0.5f;
        go.transform.localScale = Vector3.one * 0.7f;

        var pickup = go.AddComponent<PowerUpPickup>();
        pickup.kind = PowerUpPickup.Kind.SpeedBoost;
        pickup.durationSeconds = 18f;
        pickup.multiplier = 1.4f;
        pickup.destroyOnPickup = false;
        pickup.respawnDelaySeconds = 35f;

        PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
        Object.DestroyImmediate(go);

        AssetDatabase.Refresh();
        Debug.Log($"Stargrave: saved {PrefabPath}. Duplicate in Project and change Kind / values for more pickup types.");
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;
        string parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
        string leaf = System.IO.Path.GetFileName(path);
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent ?? "Assets", leaf);
    }
}
#endif
