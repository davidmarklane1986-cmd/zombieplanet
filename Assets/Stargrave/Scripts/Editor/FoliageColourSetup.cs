using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Editor tooling for the "colour → asset" foliage system (additive to GpuGrassSetup):
///  • Create Palette            — make a FoliagePalette in Resources, seeded with a grass rule (grass parity).
///  • Generate Rules From Gradient — add one rule per biome-0 gradient key, targetColour from each key.
///  • Setup Foliage Driver      — add/configure the FoliageByColour component on the scene.
/// </summary>
public static class FoliageColourSetup
{
    const string PaletteFolder = "Assets/Stargrave/Resources";
    const string PaletteName = "FoliagePalette";
    const string PalettePath = PaletteFolder + "/" + PaletteName + ".asset";

    // Same flat grass-clump sources GpuGrassSetup uses, so the grass rule reproduces today's grass.
    static readonly string[] GrassFolders = { "Assets/ThirdParty/Kenny/NatureKit/Prefabs/GroundCover" };
    static readonly string[] GrassNameTokens = { "grass", "plant_flat" };
    const string NatureKitGrass = "Assets/Proxy Games/Stylized Nature Kit Lite/Prefabs/Foliage/Grass/Grass.prefab";

    // The biome-0 mid-green gradient key the grass rule targets (matches GpuGrassCarpet's green band).
    static readonly Color GrassGreen = new Color(0.22f, 0.45f, 0.02f, 1f);

    // NatureKit prefab folders for the one-click tree/rock/palm populate.
    const string TreesFolder = "Assets/ThirdParty/Kenny/NatureKit/Prefabs/Trees";
    const string PalmsFolder = "Assets/ThirdParty/Kenny/NatureKit/Prefabs/Palms";
    const string RocksFolder = "Assets/ThirdParty/Kenny/NatureKit/Prefabs/Rocks";

    // Gradient-key colours these rules target (biome 0, tint = 0 so raw key colour).
    static readonly Color BeachYellow = new Color(0.83f, 0.85f, 0.40f, 1f);
    static readonly Color RockBrown = new Color(0.59f, 0.37f, 0.21f, 1f);

    [MenuItem("Tools/Stargrave/Foliage/Create Palette")]
    public static void CreatePalette()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorUtility.DisplayDialog("Foliage Palette",
                "Unity is still compiling/importing. Wait for the spinner (bottom-right) to finish, then run again.", "OK");
            return;
        }

        if (!AssetDatabase.IsValidFolder(PaletteFolder))
        {
            if (!AssetDatabase.IsValidFolder("Assets/Stargrave"))
                AssetDatabase.CreateFolder("Assets", "Stargrave");
            AssetDatabase.CreateFolder("Assets/Stargrave", "Resources");
        }

        var existing = AssetDatabase.LoadAssetAtPath<FoliagePalette>(PalettePath);
        if (existing != null)
        {
            // Migrate an older palette in place: flip the grass rule to the latitude-independent
            // biome-gradient path (grass parity). Other rules (colour/tree/rock) are left untouched.
            int migrated = MigrateGrassRule(existing);
            Selection.activeObject = existing;
            EditorGUIUtility.PingObject(existing);
            if (migrated > 0)
                Debug.Log($"[FoliageColourSetup] Palette already existed at {PalettePath} — migrated {migrated} grass rule(s) to useBiomeGradientRule=true, biomeIndex=0 (grass parity restored). Other rules untouched. Re-Play to apply.");
            else
                Debug.Log($"[FoliageColourSetup] Palette already exists at {PalettePath} — selected it (grass rule already up to date; other rules untouched).");
            return;
        }

        var palette = ScriptableObject.CreateInstance<FoliagePalette>();
        palette.rules = new List<FoliageColourRule> { BuildGrassRule() };

        AssetDatabase.CreateAsset(palette, PalettePath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Selection.activeObject = palette;
        EditorGUIUtility.PingObject(palette);

        var grass = palette.rules[0];
        Debug.Log($"[FoliageColourSetup] Created palette at {PalettePath} with grass rule 0 " +
                  $"({grass.prefabs.Count} grass prefab(s), targetColour ({GrassGreen.r},{GrassGreen.g},{GrassGreen.b})). " +
                  "Next: 'Setup Foliage Driver', or 'Generate Rules From Gradient' to add per-colour rules.");
    }

    // Upgrades an existing palette's grass rule to the latitude-independent biome-gradient path.
    // Targets the rule named "Meadow Grass" (and, defensively, rule index 0 if it still colour-matches
    // the grass green) so a user-renamed/custom rule 0 is not clobbered. Returns how many were changed.
    static int MigrateGrassRule(FoliagePalette palette)
    {
        if (palette == null || palette.rules == null)
            return 0;

        int changed = 0;
        for (int i = 0; i < palette.rules.Count; i++)
        {
            var r = palette.rules[i];
            if (r == null || r.useBiomeGradientRule)
                continue;

            bool isGrass = r.name == "Meadow Grass" ||
                           (i == 0 && r.render == FoliageRenderMode.GpuInstanced &&
                            ColorClose(r.targetColour, GrassGreen, 0.08f));
            if (!isGrass)
                continue;

            r.useBiomeGradientRule = true;
            r.biomeIndex = 0;
            changed++;
        }

        if (changed > 0)
        {
            EditorUtility.SetDirty(palette);
            AssetDatabase.SaveAssets();
        }
        return changed;
    }

    static bool ColorClose(Color a, Color b, float tol)
    {
        return Mathf.Abs(a.r - b.r) <= tol && Mathf.Abs(a.g - b.g) <= tol && Mathf.Abs(a.b - b.b) <= tol;
    }

    static FoliageColourRule BuildGrassRule()
    {
        return new FoliageColourRule
        {
            name = "Meadow Grass",
            enabled = true,
            // GRASS PARITY: latitude-independent element-0 gradient-green test, exactly the approved
            // grass (so desert-latitude-but-green spots like (268,132,115) still get grass). Colour
            // matching stays the default for the generated per-colour and tree/rock rules.
            useBiomeGradientRule = true,
            biomeIndex = 0,
            // Latitude bias OFF by default so coverage/density exactly reproduce the approved grass.
            // Raise latitudeInfluence (0..1) on the asset to thin grass toward desert/snow latitudes;
            // latitudeWidth tunes how sharp that falloff is. biomeIndex 0 = grass's home latitude.
            latitudeInfluence = 0f,
            latitudeWidth = 0.5f,
            targetColour = GrassGreen,
            colourTolerance = 0.35f,
            matchByKey = false,
            prefabs = GatherGrassPrefabs(),
            targetCount = 600000,
            minSpacing = 0.35f,
            edgeDensity = 0.05f,
            densityFalloff = 1.6f,
            render = FoliageRenderMode.GpuInstanced,
            orient = FoliageOrientMode.AlignToSurface,
            scaleRange = new Vector2(0.8f, 1.5f),
            maxSlope = 80f,
            elevationRange = new Vector2(0f, 1f),
            surfaceOffset = 0.02f,
            forceDoubleSided = true,
        };
    }

    [MenuItem("Tools/Stargrave/Foliage/Generate Rules From Gradient")]
    public static void GenerateRulesFromGradient()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorUtility.DisplayDialog("Foliage Palette",
                "Unity is still compiling/importing. Wait for the spinner to finish, then run again.", "OK");
            return;
        }

        var planet = Object.FindFirstObjectByType<Planet>();
        if (planet == null || planet.colourSettings == null ||
            planet.colourSettings.biomeColourSettings == null ||
            planet.colourSettings.biomeColourSettings.biomes == null ||
            planet.colourSettings.biomeColourSettings.biomes.Length == 0)
        {
            EditorUtility.DisplayDialog("Foliage Palette", "No Planet/ColourSettings/biomes found in the open scene. Open SampleScene first.", "OK");
            return;
        }

        var palette = LoadOrCreatePalette();
        if (palette == null)
            return;

        var biome = planet.colourSettings.biomeColourSettings.biomes[0];
        var grad = biome.gradient;
        var keys = grad != null ? grad.colorKeys : null;
        if (keys == null || keys.Length == 0)
        {
            EditorUtility.DisplayDialog("Foliage Palette", "Biome 0 has no gradient colour keys.", "OK");
            return;
        }

        var existingNames = new HashSet<string>();
        foreach (var r in palette.rules)
            if (r != null) existingNames.Add(r.name);

        int added = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            // Tinted key colour — same math ClassifySurface uses, so it matches GetSurfaceKeyColorAtPosition.
            Color tinted = keys[i].color * (1f - biome.tintPercent) + biome.tint * biome.tintPercent;
            string hex = ColorUtility.ToHtmlStringRGB(tinted);
            string ruleName = $"Biome0 Key{i} (#{hex})";
            if (existingNames.Contains(ruleName))
                continue;

            palette.rules.Add(new FoliageColourRule
            {
                name = ruleName,
                enabled = true,
                targetColour = tinted,
                colourTolerance = 0.25f,
                matchByKey = false,
                biomeIndex = 0,
                keyIndex = i,
                prefabs = new List<GameObject>(), // user fills these
                targetCount = 2000,
                minSpacing = 1.5f,
                edgeDensity = 0.2f,
                densityFalloff = 1.5f,
                render = FoliageRenderMode.GpuInstanced,
                orient = FoliageOrientMode.AlignToSurface,
                scaleRange = new Vector2(0.8f, 1.2f),
                maxSlope = 80f,
                elevationRange = new Vector2(0f, 1f),
                surfaceOffset = 0.02f,
                forceDoubleSided = true,
            });
            existingNames.Add(ruleName);
            added++;
        }

        EditorUtility.SetDirty(palette);
        AssetDatabase.SaveAssets();
        Selection.activeObject = palette;
        EditorGUIUtility.PingObject(palette);
        Debug.Log($"[FoliageColourSetup] Added {added} rule(s) from biome-0 gradient keys ({keys.Length} keys total). " +
                  "Assign prefabs to the new rules, and switch tree/rock rules to GameObjectPool if needed.");
    }

    [MenuItem("Tools/Stargrave/Foliage/Populate Trees, Rocks & Palms")]
    public static void PopulateTreesRocksPalms()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorUtility.DisplayDialog("Foliage Palette",
                "Unity is still compiling/importing. Wait for the spinner to finish, then run again.", "OK");
            return;
        }

        var palette = LoadOrCreatePalette();
        if (palette == null)
            return;

        var existingNames = new HashSet<string>();
        foreach (var r in palette.rules)
            if (r != null) existingNames.Add(r.name);

        var trees = GatherPrefabsInFolder(TreesFolder);
        var palms = GatherPrefabsInFolder(PalmsFolder);
        var rocks = GatherPrefabsInFolder(RocksFolder);

        int added = 0;

        // Forest trees on the green bands: colour-matched (latitude-aware), upright, sparse, flatter
        // ground only, concentrated in the greenest core via a steep density falloff.
        added += TryAddRule(palette, existingNames, new FoliageColourRule
        {
            name = "Forest Trees",
            enabled = true,
            targetColour = GrassGreen,
            colourTolerance = 0.22f,
            prefabs = trees,
            targetCount = 1500,
            minSpacing = 6f,
            edgeDensity = 0.05f,
            densityFalloff = 2.2f,
            render = FoliageRenderMode.GameObjectPool,
            orient = FoliageOrientMode.Upright,
            scaleRange = new Vector2(4f, 7f),
            maxSlope = 35f,
            elevationRange = new Vector2(0.13f, 0.46f),
            surfaceOffset = 0f,
            forceDoubleSided = false,
        });

        // Palms on the beach-yellow band, near the shore, low and sparse.
        added += TryAddRule(palette, existingNames, new FoliageColourRule
        {
            name = "Beach Palms",
            enabled = true,
            targetColour = BeachYellow,
            colourTolerance = 0.16f,
            prefabs = palms,
            targetCount = 500,
            minSpacing = 8f,
            edgeDensity = 0.1f,
            densityFalloff = 1.5f,
            render = FoliageRenderMode.GameObjectPool,
            orient = FoliageOrientMode.Upright,
            scaleRange = new Vector2(4f, 6f),
            maxSlope = 30f,
            elevationRange = new Vector2(0.04f, 0.13f),
            surfaceOffset = 0f,
            forceDoubleSided = false,
        });

        // Rocks/stones on the brown bands; allowed on steeper ground than trees.
        added += TryAddRule(palette, existingNames, new FoliageColourRule
        {
            name = "Rocks",
            enabled = true,
            targetColour = RockBrown,
            colourTolerance = 0.22f,
            prefabs = rocks,
            targetCount = 2500,
            minSpacing = 4f,
            edgeDensity = 0.15f,
            densityFalloff = 1.5f,
            render = FoliageRenderMode.GameObjectPool,
            orient = FoliageOrientMode.Upright,
            scaleRange = new Vector2(1.5f, 3.5f),
            maxSlope = 90f,
            elevationRange = new Vector2(0.4f, 1f),
            surfaceOffset = 0f,
            forceDoubleSided = false,
        });

        EditorUtility.SetDirty(palette);
        AssetDatabase.SaveAssets();
        Selection.activeObject = palette;
        EditorGUIUtility.PingObject(palette);
        Debug.Log($"[FoliageColourSetup] Populated {added} rule(s): Forest Trees ({trees.Count} prefabs), " +
                  $"Beach Palms ({palms.Count}), Rocks ({rocks.Count}). All GameObjectPool/Upright. " +
                  "Tune scaleRange/targetCount/elevationRange on the palette, then (re)Play. " +
                  "NOTE: scale is a guess for this planet size — adjust scaleRange if trees look too small/large.");
    }

    [MenuItem("Tools/Stargrave/Foliage/Populate Desert & Snow")]
    public static void PopulateDesertAndSnow()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorUtility.DisplayDialog("Foliage Palette",
                "Unity is still compiling/importing. Wait for the spinner to finish, then run again.", "OK");
            return;
        }

        var palette = LoadOrCreatePalette();
        if (palette == null)
            return;

        var existingNames = new HashSet<string>();
        foreach (var r in palette.rules)
            if (r != null) existingNames.Add(r.name);

        var palms = GatherPrefabsInFolder(PalmsFolder);
        var rocks = GatherPrefabsInFolder(RocksFolder);
        // Snow wants conifers specifically: filter the tree folder by name. Fall back to all trees if the
        // NatureKit naming doesn't match, so the rule still gets valid prefab references.
        var pines = GatherPrefabsInFolderFiltered(TreesFolder, "pine", "fir", "spruce", "conifer");
        if (pines.Count == 0)
        {
            pines = GatherPrefabsInFolder(TreesFolder);
            Debug.LogWarning("[FoliageColourSetup] No pine/fir/spruce-named tree prefabs found in " +
                             $"{TreesFolder}; 'Snow Pines' falls back to all trees. Adjust name tokens for conifer-only.");
        }

        int added = 0;

        // Desert palms on low sandy ground where biome 1 (desert) dominates the colour.
        added += TryAddRule(palette, existingNames, new FoliageColourRule
        {
            name = "Desert Palms",
            enabled = true,
            requireBiomeDominance = true,
            requiredBiomeIndex = 1,
            minRequiredBiomeWeight = 0.5f,
            targetColour = BeachYellow,
            colourTolerance = 0.2f,
            prefabs = palms,
            targetCount = 400,
            minSpacing = 9f,
            edgeDensity = 0.1f,
            densityFalloff = 1.5f,
            render = FoliageRenderMode.GameObjectPool,
            orient = FoliageOrientMode.Upright,
            scaleRange = new Vector2(4f, 6f),
            maxSlope = 30f,
            elevationRange = new Vector2(0.04f, 0.2f),
            surfaceOffset = 0f,
            forceDoubleSided = false,
        });

        // Desert rocks across the desert, mid elevations, allowed on steep ground.
        added += TryAddRule(palette, existingNames, new FoliageColourRule
        {
            name = "Desert Rocks",
            enabled = true,
            requireBiomeDominance = true,
            requiredBiomeIndex = 1,
            minRequiredBiomeWeight = 0.5f,
            targetColour = RockBrown,
            colourTolerance = 0.22f,
            prefabs = rocks,
            targetCount = 1500,
            minSpacing = 4f,
            edgeDensity = 0.15f,
            densityFalloff = 1.5f,
            render = FoliageRenderMode.GameObjectPool,
            orient = FoliageOrientMode.Upright,
            scaleRange = new Vector2(1.5f, 3.5f),
            maxSlope = 90f,
            elevationRange = new Vector2(0.2f, 0.75f),
            surfaceOffset = 0f,
            forceDoubleSided = false,
        });

        // Snow rocks across the snow biome, higher ground, sparse.
        added += TryAddRule(palette, existingNames, new FoliageColourRule
        {
            name = "Snow Rocks",
            enabled = true,
            requireBiomeDominance = true,
            requiredBiomeIndex = 2,
            minRequiredBiomeWeight = 0.5f,
            targetColour = RockBrown,
            colourTolerance = 0.22f,
            prefabs = rocks,
            targetCount = 1200,
            minSpacing = 4.5f,
            edgeDensity = 0.15f,
            densityFalloff = 1.5f,
            render = FoliageRenderMode.GameObjectPool,
            orient = FoliageOrientMode.Upright,
            scaleRange = new Vector2(1.5f, 3.5f),
            maxSlope = 90f,
            elevationRange = new Vector2(0.65f, 1f),
            surfaceOffset = 0f,
            forceDoubleSided = false,
        });

        // Sparse conifers in the snow biome, flatter ground, upright.
        added += TryAddRule(palette, existingNames, new FoliageColourRule
        {
            name = "Snow Pines",
            enabled = true,
            requireBiomeDominance = true,
            requiredBiomeIndex = 2,
            minRequiredBiomeWeight = 0.5f,
            targetColour = GrassGreen,
            colourTolerance = 0.22f,
            prefabs = pines,
            targetCount = 800,
            minSpacing = 7f,
            edgeDensity = 0.05f,
            densityFalloff = 2.2f,
            render = FoliageRenderMode.GameObjectPool,
            orient = FoliageOrientMode.Upright,
            scaleRange = new Vector2(4f, 7f),
            maxSlope = 35f,
            elevationRange = new Vector2(0.35f, 0.65f),
            surfaceOffset = 0f,
            forceDoubleSided = false,
        });

        EditorUtility.SetDirty(palette);
        AssetDatabase.SaveAssets();
        Selection.activeObject = palette;
        EditorGUIUtility.PingObject(palette);
        Debug.Log($"[FoliageColourSetup] Populated {added} biome rule(s): Desert Palms ({palms.Count} prefabs), " +
                  $"Desert Rocks ({rocks.Count}), Snow Rocks ({rocks.Count}), Snow Pines ({pines.Count}). " +
                  "All GameObjectPool/Upright, gated to biome 1 (desert) / biome 2 (snow) dominance. " +
                  "Tune targetCount/scaleRange/elevationRange on the palette, then (re)Play.");
    }

    static int TryAddRule(FoliagePalette palette, HashSet<string> existingNames, FoliageColourRule rule)
    {
        if (existingNames.Contains(rule.name))
            return 0;
        palette.rules.Add(rule);
        existingNames.Add(rule.name);
        return 1;
    }

    static List<GameObject> GatherPrefabsInFolder(string folder)
    {
        var list = new List<GameObject>();
        if (!AssetDatabase.IsValidFolder(folder))
        {
            Debug.LogWarning($"[FoliageColourSetup] Prefab folder not found: {folder}");
            return list;
        }
        foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { folder }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null)
                list.Add(prefab);
        }
        return list;
    }

    /// <summary>Prefabs in a folder whose path contains ANY of the given (lowercase) name tokens.</summary>
    static List<GameObject> GatherPrefabsInFolderFiltered(string folder, params string[] nameTokens)
    {
        var list = new List<GameObject>();
        if (!AssetDatabase.IsValidFolder(folder))
        {
            Debug.LogWarning($"[FoliageColourSetup] Prefab folder not found: {folder}");
            return list;
        }
        foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { folder }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            string lower = path.ToLowerInvariant();
            bool match = nameTokens == null || nameTokens.Length == 0;
            if (!match)
                foreach (var tok in nameTokens)
                    if (!string.IsNullOrEmpty(tok) && lower.Contains(tok)) { match = true; break; }
            if (!match)
                continue;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null)
                list.Add(prefab);
        }
        return list;
    }

    [MenuItem("Tools/Stargrave/Foliage/Setup Foliage Driver")]
    public static void SetupFoliageDriver()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorUtility.DisplayDialog("Foliage Driver",
                "Unity is still compiling/importing. Wait for the spinner (bottom-right) to finish, then run again.", "OK");
            return;
        }

        var planet = Object.FindFirstObjectByType<Planet>();
        if (planet == null)
        {
            EditorUtility.DisplayDialog("Foliage Driver", "No Planet found in the open scene. Open SampleScene first.", "OK");
            return;
        }

        var driver = Object.FindFirstObjectByType<FoliageByColour>();
        if (driver == null)
        {
            var go = new GameObject("FoliageByColour");
            Undo.RegisterCreatedObjectUndo(go, "Create FoliageByColour");
            go.transform.SetParent(planet.transform, false);
            driver = Undo.AddComponent<FoliageByColour>(go);
        }

        var palette = AssetDatabase.LoadAssetAtPath<FoliagePalette>(PalettePath);
        if (palette == null)
            palette = LoadOrCreatePalette();

        Undo.RecordObject(driver, "Setup Foliage Driver");
        driver.palette = palette;
        driver.paletteResourceName = PaletteName;
        driver.scatterPerFrame = 16000;
        driver.maxInstantiatesPerFrame = 200;
        driver.attemptBudgetMultiplier = 20;
        driver.excludeUnderwater = true;
        driver.rayHeightAboveSurface = 80f;
        driver.enabled = true;

        EditorUtility.SetDirty(driver);
        EditorSceneManager.MarkSceneDirty(driver.gameObject.scene);

        int ruleCount = palette != null && palette.rules != null ? palette.rules.Count : 0;
        Debug.Log($"[FoliageColourSetup] FoliageByColour driver configured with palette '{(palette != null ? palette.name : "<inline fallback>")}' " +
                  $"({ruleCount} rule(s)). Save the scene (Ctrl+S), then Play.");
    }

    static FoliagePalette LoadOrCreatePalette()
    {
        var palette = AssetDatabase.LoadAssetAtPath<FoliagePalette>(PalettePath);
        if (palette != null)
            return palette;

        CreatePalette();
        return AssetDatabase.LoadAssetAtPath<FoliagePalette>(PalettePath);
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
                    if (lower.Contains(GrassNameTokens[i])) { match = true; break; }
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
