using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// Editor setup for the spherical planet ocean (approach B: transparent shell + scene depth).
///
/// On the next domain reload, if the trigger file (Assets/Stargrave/.setup_planet_ocean) is
/// present, this finds every <see cref="Planet"/> in the open scene(s), adds a
/// <see cref="PlanetOceanLayer"/> if missing, builds/positions the ocean shell, assigns a saved
/// ocean material, then saves the scene and deletes its own trigger. It is idempotent and safe to
/// run repeatedly, and it does NOT edit any URP renderer .asset YAML (the shell needs only the
/// already-enabled Depth + Opaque textures, which it verifies via the public URP API).
///
/// Manual entry points live under Tools/Stargrave/.
/// </summary>
[InitializeOnLoad]
public static class PlanetOceanSetup
{
    const string TriggerPath = "Assets/Stargrave/.setup_planet_ocean";
    const string MaterialPath = "Assets/Stargrave/Materials/PlanetOcean.mat";
    // Reuse the kept water normal texture for the wave ripples if it is still present (cosmetic only).
    const string WaveNormalGuid = "f87d53fcaf34530488b706ee35d9b519";

    static PlanetOceanSetup()
    {
        EditorApplication.delayCall += () =>
        {
            if (File.Exists(TriggerPath))
            {
                SetupAllPlanets();
                File.Delete(TriggerPath);
                AssetDatabase.Refresh();
            }
        };
    }

    [MenuItem("Tools/Stargrave/Setup Planet Ocean")]
    public static void SetupMenu()
    {
        SetupAllPlanets();
    }

    [MenuItem("Tools/Stargrave/Remove Planet Ocean")]
    public static void RemoveMenu()
    {
        int removed = 0;
        foreach (PlanetOceanLayer layer in Object.FindObjectsByType<PlanetOceanLayer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (layer == null)
                continue;
            layer.RemoveOcean();
            Object.DestroyImmediate(layer);
            removed++;
        }

        MarkAndSaveOpenScenes();
        Debug.Log($"[PlanetOceanSetup] Removed ocean from {removed} planet layer(s).");
    }

    [MenuItem("Tools/Stargrave/Arm Planet Ocean Setup")]
    public static void Arm()
    {
        File.WriteAllText(TriggerPath, "armed");
        AssetDatabase.Refresh();
        Debug.Log("[PlanetOceanSetup] Armed. The ocean will be set up on the next domain reload.");
    }

    static void SetupAllPlanets()
    {
        EnsureDepthAndOpaqueTextures();

        Material mat = GetOrCreateOceanMaterial();
        if (mat == null)
        {
            Debug.LogError(
                $"[PlanetOceanSetup] Shader '{PlanetOceanLayer.ShaderName}' not found. " +
                "Make sure PlanetOcean.shader imported without errors, then run Tools/Stargrave/Setup Planet Ocean.");
            return;
        }

        Planet[] planets = Object.FindObjectsByType<Planet>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (planets == null || planets.Length == 0)
        {
            Debug.LogWarning("[PlanetOceanSetup] No Planet found in the open scene(s). Nothing to do.");
            return;
        }

        int count = 0;
        foreach (Planet planet in planets)
        {
            if (planet == null)
                continue;

            PlanetOceanLayer layer = planet.GetComponent<PlanetOceanLayer>();
            if (layer == null)
                layer = Undo.AddComponent<PlanetOceanLayer>(planet.gameObject);

            Undo.RecordObject(layer, "Setup Planet Ocean");
            layer.oceanMaterial = mat;
            layer.radiusMode = PlanetOceanLayer.RadiusMode.ManualLocalScale;
            layer.manualLocalScale = 610f;
            layer.CreateOrUpdateOcean();
            EditorUtility.SetDirty(layer);
            count++;
        }

        MarkAndSaveOpenScenes();
        Debug.Log($"[PlanetOceanSetup] Ocean set up on {count} planet(s) using material '{MaterialPath}'.");
    }

    static Material GetOrCreateOceanMaterial()
    {
        Material existing = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (existing != null)
        {
            // Keep it bound to the right shader in case it was previously pointing elsewhere.
            Shader s = Shader.Find(PlanetOceanLayer.ShaderName);
            if (s != null && existing.shader != s)
            {
                existing.shader = s;
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssets();
            }
            return existing;
        }

        Shader shader = Shader.Find(PlanetOceanLayer.ShaderName);
        if (shader == null)
            return null;

        string dir = Path.GetDirectoryName(MaterialPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        Material mat = new Material(shader) { name = "PlanetOcean" };
        TryAssignWaveNormals(mat);

        AssetDatabase.CreateAsset(mat, MaterialPath);
        AssetDatabase.SaveAssets();
        return mat;
    }

    static void TryAssignWaveNormals(Material mat)
    {
        string path = AssetDatabase.GUIDToAssetPath(WaveNormalGuid);
        if (string.IsNullOrEmpty(path))
            return;
        Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (tex == null)
            return;
        if (mat.HasProperty("_WaveNormalA"))
            mat.SetTexture("_WaveNormalA", tex);
        if (mat.HasProperty("_WaveNormalB"))
            mat.SetTexture("_WaveNormalB", tex);
    }

    /// <summary>
    /// The shell samples the URP scene depth texture, which requires Depth (and we read Opaque for
    /// consistency). Toggle via the public URP API only — never by editing the renderer .asset YAML.
    /// </summary>
    static void EnsureDepthAndOpaqueTextures()
    {
        var urp = GetActiveUrpAsset();
        if (urp == null)
        {
            Debug.LogWarning("[PlanetOceanSetup] Active render pipeline is not URP; cannot verify Depth/Opaque textures.");
            return;
        }

        bool changed = false;
        if (!urp.supportsCameraDepthTexture)
        {
            urp.supportsCameraDepthTexture = true;
            changed = true;
        }
        if (!urp.supportsCameraOpaqueTexture)
        {
            urp.supportsCameraOpaqueTexture = true;
            changed = true;
        }

        if (changed)
        {
            EditorUtility.SetDirty(urp);
            AssetDatabase.SaveAssets();
            Debug.Log("[PlanetOceanSetup] Enabled Depth/Opaque texture on the active URP asset.");
        }
    }

    static UniversalRenderPipelineAsset GetActiveUrpAsset()
    {
        var rp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (rp != null)
            return rp;
        return QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
    }

    static void MarkAndSaveOpenScenes()
    {
        var dirty = new List<Scene>();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded)
                continue;
            EditorSceneManager.MarkSceneDirty(scene);
            dirty.Add(scene);
        }
        if (dirty.Count > 0)
            EditorSceneManager.SaveOpenScenes();
    }
}
