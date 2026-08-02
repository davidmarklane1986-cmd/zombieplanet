#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Editor tool that builds distinct 3D-model power-up prefabs (instead of plain tinted spheres)
/// and writes them to <c>Assets/Stargrave/Resources/PowerUps/</c> so the runtime-created
/// <see cref="ItemSpawner"/> can auto-load them via <c>Resources.Load</c>.
///
/// Run: <b>Tools/Stargrave/Build Power-Up Prefabs</b>
///
/// Each Kenney kit (FoodKit, BlasterKit, ...) ships its own "colormap" texture atlas next to its
/// OBJ files; this tool guarantees the saved prefabs render textured (URP) rather than white/magenta.
/// </summary>
public static class StargravePowerUpModelSetup
{
    // ----------------------------------------------------------------------------------
    // MODEL SOURCES — tweak these to swap models. To use a REAL gun model for rapid fire
    // later, change RapidFireModelPath (and optionally RapidFireTargetSize) only.
    // ----------------------------------------------------------------------------------
    const string FoodKitObjDir = "Assets/ThirdParty/Kenny/FoodKit/Models/OBJ format";
    const string BlasterKitObjDir = "Assets/ThirdParty/Kenny/BlasterKit/Models/OBJ format";

    const string HealthModelPath = FoodKitObjDir + "/burger.obj";          // HealthPack -> food
    const string SpeedModelPathPrimary = FoodKitObjDir + "/soda-can.obj";  // SpeedBoost -> soda can
    const string SpeedModelPathFallback = FoodKitObjDir + "/soda.obj";     // fallback if soda-can missing

    // Rapid fire -> a real blaster gun from the Kenney BlasterKit (reads clearly as a weapon).
    // >>> Swap this single constant to repoint at a different gun model later. <<<
    // (FoodKit "/utensil-knife.obj" remains a valid no-gun-kit fallback if BlasterKit is removed.)
    const string RapidFireModelPath = BlasterKitObjDir + "/blaster-a.obj";

    // ----------------------------------------------------------------------------------
    // PER-KIND UNIFORM SIZE — largest bounding-box dimension in metres after fitting.
    // Bump these up/down to make a given pickup bigger/smaller in the world.
    // ----------------------------------------------------------------------------------
    const float HealthTargetSize = 0.95f;
    const float SpeedTargetSize = 0.85f;
    const float RapidFireTargetSize = 0.95f;

    // Extra upright/orientation tweak per kind (degrees). Default upright = identity.
    static readonly Vector3 HealthExtraEuler = Vector3.zero;
    static readonly Vector3 SpeedExtraEuler = Vector3.zero;
    static readonly Vector3 RapidFireExtraEuler = Vector3.zero;

    const float SpinSpeedDegrees = 72f;

    // ----------------------------------------------------------------------------------
    // OUTPUT LOCATIONS
    // ----------------------------------------------------------------------------------
    const string ResourcesDir = "Assets/Stargrave/Resources";
    const string PowerUpsResourcesDir = ResourcesDir + "/PowerUps";
    const string HealthPrefabPath = PowerUpsResourcesDir + "/PowerUp_Health.prefab";
    const string SpeedPrefabPath = PowerUpsResourcesDir + "/PowerUp_Speed.prefab";
    const string RapidFirePrefabPath = PowerUpsResourcesDir + "/PowerUp_RapidFire.prefab";

    // Each Kenney kit keeps its own "colormap.png" atlas next to its OBJ files.
    const string ColormapTextureRelative = "Textures/colormap.png";
    const string MaterialsDir = "Assets/Stargrave/Materials/PowerUps";

    // Cache of fallback colormap materials, keyed by texture asset path.
    static readonly Dictionary<string, Material> _colormapMaterials = new Dictionary<string, Material>();

    [MenuItem("Tools/Stargrave/Build Power-Up Prefabs")]
    public static void BuildPowerUpPrefabs()
    {
        EnsureFolder(PowerUpsResourcesDir);
        EnsureFolder(MaterialsDir);
        _colormapMaterials.Clear();

        GameObject healthPrefab = BuildPrefab(
            "PowerUp_Health", HealthModelPath, null,
            PowerUpPickup.Kind.HealthPack, HealthTargetSize, HealthExtraEuler,
            HealthPrefabPath);

        GameObject speedPrefab = BuildPrefab(
            "PowerUp_Speed", SpeedModelPathPrimary, SpeedModelPathFallback,
            PowerUpPickup.Kind.SpeedBoost, SpeedTargetSize, SpeedExtraEuler,
            SpeedPrefabPath);

        GameObject rapidFirePrefab = BuildPrefab(
            "PowerUp_RapidFire", RapidFireModelPath, null,
            PowerUpPickup.Kind.FireRateBoost, RapidFireTargetSize, RapidFireExtraEuler,
            RapidFirePrefabPath);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        TryWireSceneSpawner(healthPrefab, speedPrefab, rapidFirePrefab);

        Debug.Log($"Stargrave: Build Power-Up Prefabs finished. " +
                  $"Health={(healthPrefab != null ? "OK" : "FAILED")}, " +
                  $"Speed={(speedPrefab != null ? "OK" : "FAILED")}, " +
                  $"RapidFire={(rapidFirePrefab != null ? "OK" : "FAILED")}. " +
                  $"Prefabs live under {PowerUpsResourcesDir} and load at runtime via Resources.");
    }

    static GameObject BuildPrefab(
        string prefabName,
        string modelPath,
        string fallbackModelPath,
        PowerUpPickup.Kind kind,
        float targetSize,
        Vector3 extraEuler,
        string prefabPath)
    {
        GameObject modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        string usedPath = modelPath;
        if (modelAsset == null && !string.IsNullOrEmpty(fallbackModelPath))
        {
            modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(fallbackModelPath);
            usedPath = fallbackModelPath;
        }

        if (modelAsset == null)
        {
            Debug.LogError($"Stargrave: could not load model for {prefabName} at '{modelPath}'" +
                           (string.IsNullOrEmpty(fallbackModelPath) ? "" : $" (or fallback '{fallbackModelPath}')") +
                           ". Skipping this prefab.");
            return null;
        }

        var root = new GameObject(prefabName);
        root.transform.position = Vector3.zero;
        root.transform.rotation = Quaternion.identity;

        GameObject model = Object.Instantiate(modelAsset);
        model.transform.SetParent(root.transform, false);
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = Quaternion.Euler(extraEuler);
        model.transform.localScale = Vector3.one;

        // --- Fit uniform scale to target world size, then centre on the trigger origin. ---
        if (TryGetWorldBounds(model, out Bounds bounds))
        {
            float maxDim = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            float scale = maxDim > 1e-4f ? targetSize / maxDim : 1f;
            model.transform.localScale = Vector3.one * scale;

            if (TryGetWorldBounds(model, out bounds))
            {
                // Root sits at world origin, so offsetting by -center centres the model on it.
                Vector3 offset = root.transform.position - bounds.center;
                model.transform.position += offset;
                TryGetWorldBounds(model, out bounds);
            }
        }
        else
        {
            bounds = new Bounds(Vector3.zero, Vector3.one * targetSize);
            Debug.LogWarning($"Stargrave: {prefabName} model has no renderers to measure; using default trigger size.");
        }

        ApplyMaterials(model, usedPath, prefabName);

        var col = root.AddComponent<SphereCollider>();
        col.isTrigger = true;
        col.center = Vector3.zero;
        col.radius = Mathf.Max(0.5f, bounds.extents.magnitude * 0.85f);

        var pickup = root.AddComponent<PowerUpPickup>();
        pickup.kind = kind;
        pickup.tintByKind = false; // keep the Kenney model's own texture
        pickup.spinSpeedDegrees = SpinSpeedDegrees;

        float triggerRadius = col.radius; // capture before the root (and its collider) is destroyed
        GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out bool success);
        Object.DestroyImmediate(root);

        if (!success || saved == null)
        {
            Debug.LogError($"Stargrave: failed to save prefab '{prefabPath}'.");
            return null;
        }

        Debug.Log($"Stargrave: built {prefabName} from '{usedPath}' -> {prefabPath} " +
                  $"(kind={kind}, targetSize={targetSize}, triggerRadius={triggerRadius:0.00}).");
        return saved;
    }

    static bool TryGetWorldBounds(GameObject go, out Bounds bounds)
    {
        var renderers = go.GetComponentsInChildren<Renderer>(true);
        bounds = default;
        bool found = false;
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] == null)
                continue;
            if (!found)
            {
                bounds = renderers[i].bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(renderers[i].bounds);
            }
        }
        return found;
    }

    /// <summary>
    /// Ensures the instantiated model renders textured. If any renderer's material is missing,
    /// magenta (error shader), non-URP, or untextured, the shared URP colormap material is assigned.
    /// </summary>
    static void ApplyMaterials(GameObject model, string modelPath, string prefabName)
    {
        var renderers = model.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            Debug.LogWarning($"Stargrave: {prefabName} has no renderers; nothing to texture.");
            return;
        }

        bool needsFix = false;
        for (int i = 0; i < renderers.Length; i++)
        {
            if (!IsMaterialGood(renderers[i].sharedMaterial))
            {
                needsFix = true;
                break;
            }
        }

        if (!needsFix)
        {
            Debug.Log($"Stargrave: {prefabName} model already imported textured (URP); kept original materials.");
            return;
        }

        Material colormapMat = ResolveColormapMaterial(modelPath);
        if (colormapMat == null)
        {
            Debug.LogWarning($"Stargrave: {prefabName} needs a material fix but the shared colormap material " +
                             "is unavailable. Model may render white/magenta.");
            return;
        }

        int patched = 0;
        for (int i = 0; i < renderers.Length; i++)
        {
            int slots = Mathf.Max(1, renderers[i].sharedMaterials.Length);
            var mats = new Material[slots];
            for (int s = 0; s < slots; s++)
                mats[s] = colormapMat;
            renderers[i].sharedMaterials = mats;
            patched++;
        }

        Debug.Log($"Stargrave: {prefabName} model materials were not URP-textured; " +
                  $"assigned shared colormap material to {patched} renderer(s).");
    }

    static bool IsMaterialGood(Material m)
    {
        if (m == null || m.shader == null)
            return false;

        string sn = m.shader.name;
        if (sn == "Hidden/InternalErrorShader")
            return false;

        bool urp = sn.Contains("Universal Render Pipeline") || sn.Contains("URP");
        if (!urp)
            return false;

        Texture tex = m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : m.mainTexture;
        return tex != null;
    }

    /// <summary>
    /// Builds (or loads/caches) a URP colormap material using the atlas that sits next to the
    /// given model (each Kenney kit ships its own Textures/colormap.png).
    /// </summary>
    static Material ResolveColormapMaterial(string modelPath)
    {
        string modelDir = (Path.GetDirectoryName(modelPath) ?? string.Empty).Replace('\\', '/');
        string texPath = modelDir + "/" + ColormapTextureRelative;

        if (_colormapMaterials.TryGetValue(texPath, out Material cached) && cached != null)
            return cached;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");
        if (shader == null)
        {
            Debug.LogWarning("Stargrave: no suitable shader found for colormap material.");
            return null;
        }

        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
        if (tex == null)
            Debug.LogWarning($"Stargrave: colormap texture not found at '{texPath}'. Material will be untextured.");

        string kit = ExtractKitName(modelPath);
        string matPath = MaterialsDir + "/" + kit + "_Colormap.mat";

        Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, matPath);
        }
        else
        {
            mat.shader = shader;
        }

        if (tex != null)
        {
            if (mat.HasProperty("_BaseMap"))
                mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_MainTex"))
                mat.SetTexture("_MainTex", tex);
            mat.mainTexture = tex;
        }
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", Color.white);

        // Match the matte planet terrain (no specular highlight / reflections), so pickups don't
        // over-brighten under the intensity-2 sun the way default URP Lit Smoothness 0.5 would.
        ModelMatteLighting.MakeMatte(mat);

        EditorUtility.SetDirty(mat);
        _colormapMaterials[texPath] = mat;
        return mat;
    }

    // Pulls the kit folder name (e.g. "FoodKit", "BlasterKit") from a model path under .../Kenny/.
    static string ExtractKitName(string modelPath)
    {
        string normalized = modelPath.Replace('\\', '/');
        string[] parts = normalized.Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("Kenny", System.StringComparison.OrdinalIgnoreCase))
                return Sanitize(parts[i + 1]);
        }
        return "PowerUp";
    }

    static string Sanitize(string s)
    {
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]))
                chars[i] = '_';
        }
        return new string(chars);
    }

    static void TryWireSceneSpawner(GameObject health, GameObject speed, GameObject rapidFire)
    {
        var spawner = Object.FindFirstObjectByType<ItemSpawner>(FindObjectsInactive.Include);
        if (spawner == null)
            return; // Spawner is normally runtime-created; Resources path covers that case.

        var so = new SerializedObject(spawner);
        AssignProp(so, "healthPickupPrefab", health);
        AssignProp(so, "speedPickupPrefab", speed);
        AssignProp(so, "rapidFirePickupPrefab", rapidFire);
        so.ApplyModifiedProperties();

        EditorUtility.SetDirty(spawner);
        EditorSceneManager.MarkSceneDirty(spawner.gameObject.scene);
        Debug.Log("Stargrave: assigned per-kind prefabs to the ItemSpawner in the open scene. " +
                  "Save the scene to persist this (the Resources path also works without it).");
    }

    static void AssignProp(SerializedObject so, string propName, Object value)
    {
        var prop = so.FindProperty(propName);
        if (prop != null && value != null)
            prop.objectReferenceValue = value;
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
