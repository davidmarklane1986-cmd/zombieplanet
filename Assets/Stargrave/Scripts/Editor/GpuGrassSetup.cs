using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// One-click setup for the fresh, self-contained GPU grass carpet.
///
/// Decouples <see cref="GpuGrassCarpet"/> from the foliage spawn profile by assigning grass
/// prefabs directly to the component, then applies the painted-surface-colour defaults. Run
/// after the foliage rules were wiped to bring back ONLY grass as the first fresh placement rule.
/// </summary>
public static class GpuGrassSetup
{
    // Flat grass-clump prefabs (authored as flat XZ meshes). Add packs here to widen the pool.
    static readonly string[] GrassFolders =
    {
        "Assets/ThirdParty/Kenny/NatureKit/Prefabs/GroundCover",
    };
    static readonly string[] GrassNameTokens = { "grass", "plant_flat" };

    const string NatureKitGrass = "Assets/Proxy Games/Stylized Nature Kit Lite/Prefabs/Foliage/Grass/Grass.prefab";

    [MenuItem("Tools/Stargrave/Grass/Setup GPU Grass Carpet")]
    public static void SetupGpuGrass()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorUtility.DisplayDialog("GPU Grass",
                "Unity is still compiling/importing. Wait for the spinner (bottom-right) to finish, then run this again — otherwise it applies stale settings.", "OK");
            return;
        }

        var planet = Object.FindFirstObjectByType<Planet>();
        if (planet == null)
        {
            EditorUtility.DisplayDialog("GPU Grass", "No Planet found in the open scene. Open SampleScene first.", "OK");
            return;
        }

        var carpet = Object.FindFirstObjectByType<GpuGrassCarpet>();
        if (carpet == null)
        {
            var go = new GameObject("GpuGrassCarpet");
            Undo.RegisterCreatedObjectUndo(go, "Create GpuGrassCarpet");
            go.transform.SetParent(planet.transform, false);
            carpet = Undo.AddComponent<GpuGrassCarpet>(go);
        }

        var grass = GatherGrassPrefabs();
        if (grass.Count == 0)
        {
            EditorUtility.DisplayDialog("GPU Grass",
                "No grass prefabs found in the configured folders. Check GrassFolders in GpuGrassSetup.cs.", "OK");
            return;
        }

        Undo.RecordObject(carpet, "Setup GPU Grass Carpet");
        carpet.grassPrefabs = grass;
        // Density: 400k hit the count ceiling before covering the whole planet's green, so raise it
        // and tighten spacing to actually carpet the green everywhere.
        carpet.count = 600000;
        carpet.minSpacing = 0.35f;
        // Snap the final painted colour to its nearest gradient SHADE and grass only green shades.
        // This is the authoritative "is it green" test; beach/tan/brown/grey/blue are non-green
        // shades and excluded automatically (no elevation floor or RGB tuning needed).
        carpet.useNearestGreenShade = true;
        carpet.greenSource = GpuGrassCarpet.GreenColorSource.PaintedSurface;
        // Let colour alone decide the upper limit — the painted colour already bakes in elevation,
        // so no derived elevation band (it was rejecting green blend zones).
        carpet.deriveElevationFromGradientKeys = false;
        // Inclusive green test: grab the whole olive / yellow-green BLEND strip (where the green
        // biome mixes into the desert biome) by only requiring green >= red. Pure desert-yellow
        // (g<r), brown (r>g), grey/snow and blue water still fail. Beach-sand sits at this same
        // borderline colour, so it is excluded by the elevation floor below instead of by colour.
        carpet.minGreenChannel = 0.16f;
        carpet.minGreenOverRed = 0.0f;
        carpet.minGreenOverBlue = 0.04f;
        // No elevation floor needed — the nearest-shade test puts beach on the beach shade (non-green).
        carpet.minElevation = 0f;
        carpet.maxElevation = 1f;
        carpet.grassBiomeMaxPercent = 1f;     // off — let the painted colour decide (no latitude band)
        // The active "With Biomes" shader applies NO rock/slope tint, so steep ground stays green.
        // Let colour alone decide where grass goes; only cap near-vertical faces where flat clumps
        // would look wrong. This recovers all the green hillsides that the 45 cap was rejecting.
        carpet.maxSlope = 80f;
        carpet.scatterPerFrame = 16000;        // bigger per-frame budget so the larger attempt count still finishes fast
        carpet.enabled = true;

        EditorUtility.SetDirty(carpet);
        EditorSceneManager.MarkSceneDirty(carpet.gameObject.scene);

        Debug.Log($"[GpuGrassSetup] GPU grass carpet configured with {grass.Count} grass prefab(s): " +
                  string.Join(", ", grass.ConvertAll(p => p != null ? p.name : "null")) +
                  $". nearestGreenShade={carpet.useNearestGreenShade}, Source={carpet.greenSource}, " +
                  $"count={carpet.count}, minSpacing={carpet.minSpacing}, maxSlope={carpet.maxSlope}, " +
                  $"minElev={carpet.minElevation}. Save the scene (Ctrl+S).");
    }

    // --- Debug: render the terrain as solid colour bands (no blending) to verify the green mapping ---
    const string SolidActiveKey = "Stargrave_SolidBands_Active";
    const string SolidBlurKey = "Stargrave_SolidBands_Blur";
    const string SolidBlendKey = "Stargrave_SolidBands_Blend";
    const string SolidModesKey = "Stargrave_SolidBands_Modes";

    [MenuItem("Tools/Stargrave/Grass/Debug: Toggle Solid Colour Bands")]
    public static void ToggleSolidBands()
    {
        var planet = Object.FindFirstObjectByType<Planet>();
        if (planet == null || planet.colourSettings == null ||
            planet.colourSettings.biomeColourSettings == null ||
            planet.colourSettings.biomeColourSettings.biomes == null)
        {
            EditorUtility.DisplayDialog("Solid Bands", "No Planet/ColourSettings found in the open scene.", "OK");
            return;
        }

        var cs = planet.colourSettings;
        var bcs = cs.biomeColourSettings;
        var biomes = bcs.biomes;
        bool active = EditorPrefs.GetBool(SolidActiveKey, false);

        if (!active)
        {
            // Save originals, then force hard bands: Fixed gradients + zero biome blur/blend.
            EditorPrefs.SetFloat(SolidBlurKey, bcs.textureBoundaryBlur);
            EditorPrefs.SetFloat(SolidBlendKey, bcs.blendAmount);
            var modes = new System.Text.StringBuilder();
            for (int i = 0; i < biomes.Length; i++)
            {
                if (i > 0) modes.Append(',');
                modes.Append(biomes[i].gradient != null ? (int)biomes[i].gradient.mode : 0);
                if (biomes[i].gradient != null) biomes[i].gradient.mode = GradientMode.Fixed;
            }
            EditorPrefs.SetString(SolidModesKey, modes.ToString());
            bcs.textureBoundaryBlur = 0f;
            bcs.blendAmount = 0f;
            EditorPrefs.SetBool(SolidActiveKey, true);
            Debug.Log("[GpuGrassSetup] Solid colour bands ON — terrain now shows each gradient key as a hard band. Toggle again to restore.");
        }
        else
        {
            // Restore originals.
            bcs.textureBoundaryBlur = EditorPrefs.GetFloat(SolidBlurKey, 0.03f);
            bcs.blendAmount = EditorPrefs.GetFloat(SolidBlendKey, 0.242f);
            string[] modes = EditorPrefs.GetString(SolidModesKey, "").Split(',');
            for (int i = 0; i < biomes.Length; i++)
            {
                if (biomes[i].gradient == null) continue;
                int m = (i < modes.Length && int.TryParse(modes[i], out int mv)) ? mv : 0;
                biomes[i].gradient.mode = (GradientMode)m;
            }
            EditorPrefs.SetBool(SolidActiveKey, false);
            Debug.Log("[GpuGrassSetup] Solid colour bands OFF — terrain blending restored.");
        }

        EditorUtility.SetDirty(cs);
        // Full rebuild (mesh + colours), NOT just OnColourSettingsUpdated — the latter can leave
        // empty meshes and make the planet vanish in edit mode.
        planet.GeneratePlanet();
        AssetDatabase.SaveAssets();
    }

    [MenuItem("Tools/Stargrave/Grass/Remove GPU Grass Carpet")]
    public static void RemoveGpuGrass()
    {
        var carpet = Object.FindFirstObjectByType<GpuGrassCarpet>();
        if (carpet == null)
        {
            Debug.Log("[GpuGrassSetup] No GpuGrassCarpet found.");
            return;
        }

        var scene = carpet.gameObject.scene;
        Undo.DestroyObjectImmediate(carpet.gameObject);
        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log("[GpuGrassSetup] Removed GpuGrassCarpet. Save the scene (Ctrl+S).");
    }

    static List<GameObject> GatherGrassPrefabs()
    {
        var list = new List<GameObject>();
        var seen = new HashSet<string>();

        foreach (var folder in GrassFolders)
        {
            if (!AssetDatabase.IsValidFolder(folder))
                continue;

            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { folder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                string lower = path.ToLowerInvariant();
                bool match = false;
                for (int i = 0; i < GrassNameTokens.Length; i++)
                {
                    if (lower.Contains(GrassNameTokens[i]))
                    {
                        match = true;
                        break;
                    }
                }
                if (!match || !seen.Add(path))
                    continue;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null)
                    list.Add(prefab);
            }
        }

        var natureGrass = AssetDatabase.LoadAssetAtPath<GameObject>(NatureKitGrass);
        if (natureGrass != null && seen.Add(NatureKitGrass))
            list.Add(natureGrass);

        return list;
    }
}
