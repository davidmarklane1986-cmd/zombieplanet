#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// One-click builder for the 16-colour foliage ZONE system. It:
///  1. Computes the SAME 16 adaptive colours as the <c>PlanetMercator_16</c> map by reusing
///     <see cref="PlanetMercatorMap16ColourExporter.ComputePlanetPalette"/> (live-surface sampling +
///     median-cut), so each zone exactly matches the map.
///  2. Creates/overwrites <c>Assets/Stargrave/Resources/FoliagePalette.asset</c> with EXACTLY 16
///     <see cref="FoliageColourRule"/>s — one per palette colour — auto-assigned to an asset/zone by a
///     colour heuristic (water → empty, greens → grass/forest, tan → palms, brown → rocks, snow → snow
///     rocks/pines). Every field stays Inspector-tweakable afterward.
///  3. Wires the scene <see cref="FoliageByColour"/> to
///     <see cref="FoliageByColour.FoliagePlacementMode.NearestPaletteColour"/> with that palette and
///     saves the scene.
///
/// Re-runnable / idempotent: re-running recomputes the colours and overwrites the 16 rules in place.
///
/// The runtime classifier groups rules by exact targetColour, so a power user can ADD a second rule with
/// the SAME targetColour (e.g. grass + scattered trees on one green zone) and BOTH place — but this tool
/// emits exactly one rule per colour to keep the palette a clean 16-entry list.
///
/// execute_menu_item is unreliable over the MCP bridge, so this also runs on domain reload when a trigger
/// file exists (same pattern as the map exporter): drop <c>&lt;project&gt;/tmp/foliage16_build.trigger</c>
/// then force a recompile/refresh to run it.
/// </summary>
[InitializeOnLoad]
public static class Build16ColourFoliagePalette
{
    const int PALETTE_SIZE = 16;

    static readonly string TmpDir =
        Path.GetFullPath(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "tmp"));
    static readonly string TriggerPath = Path.Combine(TmpDir, "foliage16_build.trigger");

    static Build16ColourFoliagePalette()
    {
        EditorApplication.delayCall += () =>
        {
            try
            {
                if (File.Exists(TriggerPath))
                {
                    File.Delete(TriggerPath);
                    Debug.Log("[Build16ColourFoliagePalette] Trigger file detected on domain reload — building 16-colour foliage palette.");
                    Build();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Build16ColourFoliagePalette] Trigger check failed: {e}");
            }
        };
    }

    [MenuItem("Tools/Stargrave/Build 16-Colour Foliage Palette")]
    public static void Build()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogError("[Build16ColourFoliagePalette] Unity is still compiling/importing. Wait for the spinner (bottom-right), then run again.");
            return;
        }

        var planet = Object.FindAnyObjectByType<Planet>();
        if (planet == null)
        {
            Debug.LogError("[Build16ColourFoliagePalette] No Planet in the open scene. Open the scene with the planet (e.g. SampleScene) and try again.");
            return;
        }

        // 1) Compute the 16 palette colours from the LIVE planet (identical to the PlanetMercator_16 map).
        Color32[] palette32 = PlanetMercatorMap16ColourExporter.ComputePlanetPalette(planet, PALETTE_SIZE);
        if (palette32 == null || palette32.Length == 0)
        {
            Debug.LogError("[Build16ColourFoliagePalette] Failed to compute the planet palette (see earlier errors).");
            return;
        }

        // 2) Gather the SAME asset references the existing FoliageColourSetup uses.
        var grass = FoliageColourSetup.GetGrassPrefabs();
        var trees = FoliageColourSetup.GetTreePrefabs();
        var palms = FoliageColourSetup.GetPalmPrefabs();
        var rocks = FoliageColourSetup.GetRockPrefabs();
        var pines = FoliageColourSetup.GetPinePrefabs();

        // 3) Auto-assign one rule per colour by the heuristic.
        var rules = new List<FoliageColourRule>(PALETTE_SIZE);
        var report = new StringBuilder();
        report.AppendLine("[Build16ColourFoliagePalette] 16-colour foliage zones (winner-take-all nearest palette colour):");
        report.AppendLine("  idx  hex       rgb                zone         asset");

        for (int i = 0; i < palette32.Length; i++)
        {
            Color32 p = palette32[i];
            Color col = new Color(p.r / 255f, p.g / 255f, p.b / 255f, 1f);
            string hex = ColorUtility.ToHtmlStringRGB(col);

            ZoneKind kind = Classify(col, out bool judgement);
            string asset = AddZoneRule(rules, kind, col, hex, grass, trees, palms, rocks, pines);

            report.AppendLine($"  [{i:00}] #{hex}  rgb({p.r,3},{p.g,3},{p.b,3})  {kind,-10}  {asset}{(judgement ? "   <-- REVIEW (borderline call)" : "")}");
        }

        // Continuous grass carpet (biome-gradient), independent of colour-zone patches. Blankets green
        // land and feathers out toward beach / desert / snow via greenness + biome exclusion.
        if (grass != null && grass.Count > 0)
        {
            rules.Add(MeadowCarpetRule(grass));
            report.AppendLine($"  [++] Meadow Grass carpet (biome-gradient GPU, {grass.Count} meshes) — continuous coverage, not a colour zone");
        }

        // Sparse woodland trees across the same green land (biome-gradient), so meadows/olive get tree
        // cover beyond the dark-green forest colour zone. Clusters via clusterStrength; denser forests
        // still come from the Forest Trees / Grove Fill zone rules.
        if (trees != null && trees.Count > 0)
        {
            rules.Add(ScatteredWoodlandRule(trees));
            report.AppendLine($"  [++] Scattered Woodland (biome-gradient pooled, {trees.Count} prefabs) — sparse trees across green land");
        }

        // 4) Create/overwrite the palette asset in Resources (idempotent).
        var palette = LoadOrCreatePaletteAsset();
        palette.rules = rules;
        EditorUtility.SetDirty(palette);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // 5) Wire the scene driver: nearest-palette mode + this palette, then save the scene.
        var driver = Object.FindAnyObjectByType<FoliageByColour>();
        string driverNote;
        if (driver == null)
        {
            var go = new GameObject("FoliageByColour");
            go.transform.SetParent(planet.transform, false);
            driver = go.AddComponent<FoliageByColour>();
            driverNote = "created a new FoliageByColour GameObject under the planet";
        }
        else
        {
            driverNote = "updated the existing FoliageByColour";
        }
        driver.placementMode = FoliageByColour.FoliagePlacementMode.NearestPaletteColour;
        driver.palette = palette;
        driver.paletteResourceName = FoliageColourSetup.SharedPaletteName;
        driver.enabled = true;
        EditorUtility.SetDirty(driver);
        EditorSceneManager.MarkSceneDirty(driver.gameObject.scene);
        EditorSceneManager.SaveOpenScenes();

        report.AppendLine($"  Palette asset : {FoliageColourSetup.SharedPalettePath}  ({rules.Count} rules)");
        report.AppendLine($"  Driver        : {driverNote}; placementMode = NearestPaletteColour; palette assigned; scene saved.");
        report.AppendLine($"  Prefab pools  : grass {grass.Count}, trees {trees.Count}, palms {palms.Count}, rocks {rocks.Count}, pines {pines.Count}.");
        report.AppendLine("  Tweak any zone in the FoliagePalette asset (targetColour / render / targetCount / minSpacing / scaleRange / placeNothing). Re-Play to apply.");
        Debug.Log(report.ToString());
    }

    // ---------------------------------------------------------------------------------------------
    // Colour heuristic
    // ---------------------------------------------------------------------------------------------

    enum ZoneKind { None, Trees, GrassDense, GrassSparse, Palms, Rocks, SnowRocks, SnowPines }

    /// <summary>
    /// Maps a palette colour to a single foliage zone. <paramref name="judgement"/> is set when the colour
    /// sits near a decision boundary, so the log can flag it for the user to review/override.
    /// </summary>
    static ZoneKind Classify(Color c, out bool judgement)
    {
        float r = c.r, g = c.g, b = c.b;
        float max = Mathf.Max(r, Mathf.Max(g, b));
        float min = Mathf.Min(r, Mathf.Min(g, b));
        float sat = max - min;
        judgement = false;

        // 1) Saturated blue dominant -> water / empty zone (places nothing).
        if (b >= r && b >= g && (b - Mathf.Max(r, g)) > 0.06f && sat > 0.18f)
        {
            if ((b - Mathf.Max(r, g)) < 0.12f) judgement = true; // near the pale-blue/snow boundary
            return ZoneKind.None;
        }

        // 2) Snow — ONLY genuinely achromatic-bright colours, or a clearly blue-dominant pale grey. This is
        //    deliberately checked BEFORE green/warm but kept TIGHT (sat thresholds) so desaturated OLIVES /
        //    TANS (which are still hue-dominant) fall through to the green/warm families instead of being
        //    mistaken for snow. Pale blue-grey -> pines; near-white / light grey -> rocks.
        bool achromaticBright = max > 0.62f && sat < 0.13f;                 // near-white / light grey
        bool paleBlueGrey = b >= r && b >= g && max > 0.6f && sat < 0.20f;  // cold pale grey
        if (achromaticBright || paleBlueGrey)
        {
            if (sat > 0.10f) judgement = true; // anything not nearly-grey is a borderline snow call
            return (b >= r) ? ZoneKind.SnowPines : ZoneKind.SnowRocks;
        }

        // 3) Green dominant (green is the max channel and clearly above blue).
        if (g >= r && g > b)
        {
            if (max < 0.5f && (g - r) > 0.10f) return ZoneKind.Trees;   // dark/rich green -> forest
            if ((g - r) >= 0.10f) return ZoneKind.GrassDense;            // solid green -> grass
            if ((g - r) < 0.04f) judgement = true;                      // olive / yellow-green boundary
            return ZoneKind.GrassSparse;                                // yellow-green / olive -> sparse grass
        }

        // 4) Warm (red >= green): darker red-over-blue -> brown rocks, else tan/beige -> beach palms.
        if (max < 0.58f && (r - g) > 0.04f && (r - b) > 0.18f)
            return ZoneKind.Rocks;
        judgement = true; // tan vs brown vs sparse-grass is fuzzy
        return ZoneKind.Palms;
    }

    /// <summary>Adds exactly one rule for the zone and returns a short human-readable asset description.</summary>
    static string AddZoneRule(List<FoliageColourRule> rules, ZoneKind kind, Color col, string hex,
        List<GameObject> grass, List<GameObject> trees, List<GameObject> palms,
        List<GameObject> rocks, List<GameObject> pines)
    {
        switch (kind)
        {
            case ZoneKind.None:
                rules.Add(EmptyRule($"Water/None #{hex}", col));
                return "(empty / placeNothing)";
            case ZoneKind.Trees:
                rules.Add(PooledRule($"Forest Trees #{hex}", col, trees, 14000, 1.8f, new Vector2(4f, 7f), 35f, 0.45f, 0.032f));
                rules.Add(PooledRule($"Forest Grove Fill #{hex}", col, trees, 8000, 1.3f, new Vector2(3f, 5.5f), 35f, 0.75f, 0.04f));
                return $"Forest Trees + Grove Fill (pooled, {trees.Count} prefabs)";
            case ZoneKind.GrassDense:
            case ZoneKind.GrassSparse:
                // Colour zone kept for winner-take-all partitioning only. Grass coverage comes from the
                // Meadow Grass biome-gradient carpet rule appended after all zones (continuous + blend-out).
                // Sparse trees on these greens come from Scattered Woodland (also appended).
                rules.Add(EmptyRule($"Land #{hex}", col));
                return "(land marker — grass/trees via carpet rules)";
            case ZoneKind.Palms:
                rules.Add(PooledRule($"Beach Palms #{hex}", col, palms, 900, 5.5f, new Vector2(4f, 6f), 30f));
                return $"Beach Palms (pooled, {palms.Count} prefabs)";
            case ZoneKind.Rocks:
                rules.Add(PooledRule($"Rocks #{hex}", col, rocks, 2500, 4f, new Vector2(1.5f, 3.5f), 90f));
                return $"Rocks (pooled, {rocks.Count} prefabs)";
            case ZoneKind.SnowRocks:
                rules.Add(PooledRule($"Snow Rocks #{hex}", col, rocks, 1200, 4.5f, new Vector2(1.5f, 3.5f), 90f));
                return $"Snow Rocks (pooled, {rocks.Count} prefabs)";
            case ZoneKind.SnowPines:
                rules.Add(PooledRule($"Snow Pines #{hex}", col, pines, 5500, 2.4f, new Vector2(4f, 7f), 35f, 0.4f, 0.03f));
                return $"Snow Pines (pooled, {pines.Count} prefabs)";
        }
        return "(none)";
    }

    // ---------------------------------------------------------------------------------------------
    // Rule factories (all fields stay Inspector-tweakable; targetColour set EXACTLY to the palette colour)
    // ---------------------------------------------------------------------------------------------

    static FoliageColourRule BaseRule(string name, Color col)
    {
        return new FoliageColourRule
        {
            name = name,
            enabled = true,
            useBiomeGradientRule = false,
            matchByKey = false,
            requireBiomeDominance = false,
            placeNothing = false,
            targetColour = col,
            colourTolerance = 0.25f, // unused by NearestPaletteColour mode, kept sensible for other modes
            latitudeInfluence = 0f,
            elevationRange = new Vector2(0f, 1f),
        };
    }

    static FoliageColourRule EmptyRule(string name, Color col)
    {
        var r = BaseRule(name, col);
        r.placeNothing = true;
        r.targetCount = 0;
        r.render = FoliageRenderMode.GpuInstanced;
        return r;
    }

    static FoliageColourRule GrassRule(string name, Color col, List<GameObject> grass, int count, float spacing)
    {
        var r = BaseRule(name, col);
        r.prefabs = new List<GameObject>(grass);
        r.render = FoliageRenderMode.GpuInstanced;
        r.orient = FoliageOrientMode.AlignToSurface;
        r.targetCount = count;
        r.minSpacing = spacing;
        r.edgeDensity = 0.05f;
        r.densityFalloff = 1.6f;
        r.scaleRange = new Vector2(0.8f, 1.5f);
        r.maxSlope = 80f;
        r.surfaceOffset = 0.02f;
        r.forceDoubleSided = true;
        return r;
    }

    /// <summary>Single planet-wide grass carpet: greenness membership, feathers out at non-green borders.</summary>
    static FoliageColourRule MeadowCarpetRule(List<GameObject> grass)
    {
        var r = GrassRule("Meadow Grass", new Color(0.22f, 0.45f, 0.02f, 1f), grass, 1200000, 0.24f);
        r.useBiomeGradientRule = true;
        r.biomeIndex = 0;
        r.maxOtherBiomeInfluence = 0.72f;
        r.edgeDensity = 0.14f;
        r.densityFalloff = 1.35f;
        r.colourTolerance = 0.35f;
        return r;
    }

    /// <summary>
    /// Sparse trees across green land (same greenness gate as meadow grass). Covers meadows/olive beyond
    /// the dark-forest colour zone; clusterStrength packs them into small woodland clumps.
    /// </summary>
    static FoliageColourRule ScatteredWoodlandRule(List<GameObject> trees)
    {
        var r = PooledRule("Scattered Woodland", new Color(0.22f, 0.45f, 0.02f, 1f), trees,
            9000, 7.5f, new Vector2(3.5f, 6.5f), 40f, 0.65f, 0.028f);
        r.useBiomeGradientRule = true;
        r.biomeIndex = 0;
        r.maxOtherBiomeInfluence = 0.68f;
        r.edgeDensity = 0.1f;
        r.densityFalloff = 1.35f;
        r.colourTolerance = 0.35f;
        r.elevationRange = new Vector2(0.05f, 1f); // stay off the lowest beach band
        return r;
    }

    static FoliageColourRule PooledRule(string name, Color col, List<GameObject> prefabs, int count,
        float spacing, Vector2 scale, float maxSlope, float clusterStrength = 0f, float clusterScale = 0.035f)
    {
        var r = BaseRule(name, col);
        r.prefabs = new List<GameObject>(prefabs);
        r.render = FoliageRenderMode.GameObjectPool;
        r.orient = FoliageOrientMode.Upright;
        r.targetCount = count;
        r.minSpacing = spacing;
        r.edgeDensity = 0.1f;
        r.densityFalloff = 1.5f;
        r.clusterStrength = clusterStrength;
        r.clusterScale = clusterScale;
        r.scaleRange = scale;
        r.maxSlope = maxSlope;
        r.surfaceOffset = 0f;
        r.forceDoubleSided = false;
        return r;
    }

    static FoliagePalette LoadOrCreatePaletteAsset()
    {
        if (!AssetDatabase.IsValidFolder(FoliageColourSetup.SharedPaletteFolder))
        {
            if (!AssetDatabase.IsValidFolder("Assets/Stargrave"))
                AssetDatabase.CreateFolder("Assets", "Stargrave");
            AssetDatabase.CreateFolder("Assets/Stargrave", "Resources");
        }

        var palette = AssetDatabase.LoadAssetAtPath<FoliagePalette>(FoliageColourSetup.SharedPalettePath);
        if (palette == null)
        {
            palette = ScriptableObject.CreateInstance<FoliagePalette>();
            AssetDatabase.CreateAsset(palette, FoliageColourSetup.SharedPalettePath);
        }
        return palette;
    }
}
#endif
