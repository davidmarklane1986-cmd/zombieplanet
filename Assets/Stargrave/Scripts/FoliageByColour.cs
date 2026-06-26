using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// "Colour → asset" foliage placement driver for the procedural planet.
///
/// One shared scatter pass raycasts random points on the planet, classifies each point by its
/// dominant gradient KEY colour (<see cref="Planet.GetSurfaceKeyColorAtPosition"/>), and assigns it
/// to the <see cref="FoliageColourRule"/> whose <see cref="FoliageColourRule.targetColour"/> is the
/// closest match. Each rule then thins by a greenness-style density falloff and a per-rule spacing
/// grid before placing an instance.
///
/// Two render backends:
///  • <see cref="FoliageRenderMode.GpuInstanced"/> — GPU-instanced like GpuGrassCarpet (grass/flowers).
///  • <see cref="FoliageRenderMode.GameObjectPool"/> — real instantiated prefabs with colliders/LODs (trees/rocks).
///
/// This is additive: <see cref="GpuGrassCarpet"/> is untouched. With no palette assigned it falls
/// back to a single built-in grass rule, so dropping this component on an object "just works".
/// </summary>
public class FoliageByColour : MonoBehaviour
{
    [Header("Rules")]
    [Tooltip("Palette of colour→asset rules. If empty, 'Palette Resource Name' is loaded from Resources, else the inline fallback rules below are used.")]
    public FoliagePalette palette;
    [Tooltip("If 'Palette' is unassigned, load a FoliagePalette with this name from a Resources folder.")]
    public string paletteResourceName = "FoliagePalette";
    [Tooltip("Used only when no palette is found. Defaults to a single grass rule so grass works out of the box.")]
    public List<FoliageColourRule> inlineRules = new List<FoliageColourRule>
    {
        new FoliageColourRule
        {
            name = "Meadow Grass",
            useBiomeGradientRule = true,
            biomeIndex = 0,
            targetColour = new Color(0.22f, 0.45f, 0.02f, 1f),
            colourTolerance = 0.35f,
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
        }
    };

    [Header("Scatter budget")]
    [Tooltip("Raycasts are spread across frames during scatter to avoid a load hang.")]
    [Min(500)] public int scatterPerFrame = 16000;
    [Tooltip("Cap on GameObjectPool instantiations per frame to avoid hitches when placing trees/rocks.")]
    [Min(16)] public int maxInstantiatesPerFrame = 200;
    [Tooltip("Multiplies the attempt budget over the summed target counts. This is a mostly-ocean planet, so most rays land in water and are wasted.")]
    [Min(2)] public int attemptBudgetMultiplier = 20;

    [Header("Area limits (global)")]
    [Tooltip("Reject points below water (uses the planet's water radius).")]
    public bool excludeUnderwater = true;
    [Tooltip("How high above the surface rays start (world units). Should clear the tallest terrain.")]
    public float rayHeightAboveSurface = 80f;

    [Header("Diagnostics")]
    [Tooltip("Paste a world position, then right-click the component header -> Diagnose Probe Position.")]
    public Vector3 debugProbePosition;
    public bool logResults = true;

    const int BatchSize = 1023; // Graphics.DrawMeshInstanced hard limit per call.

    // Greenness window for the grass-parity (biome-gradient) rule's smooth edge feather. Values are
    // in the units of ColourGenerator.GetBiomeGradientGreenness (0 = non-green, ~0.07 on beach-yellow
    // key1, ~0.73 at the first solid-green key2, 1.0 in the core green keys key3/key4). Below 'Lo' the
    // surface reads yellow/brown so grass is excluded; at/above 'Hi' it is solid green so density is
    // full; between them density smoothstep-ramps so grass blends out gradually toward the beach
    // (below) and rock (above) bands instead of cutting off hard.
    const float GradientGreenLo = 0.2f;
    const float GradientGreenHi = 0.6f;

    // ---- GPU-instanced batch set (one per GpuInstanced rule). Mirrors GpuGrassCarpet's draw path. ----
    class SubMeshDraw
    {
        public Mesh mesh;
        public int subMesh;
        public Material material;
        public Matrix4x4 relMatrix;
        public readonly List<Matrix4x4> matrices = new List<Matrix4x4>();
        public Matrix4x4[][] batches;
    }

    class PrefabModel
    {
        public readonly List<SubMeshDraw> draws = new List<SubMeshDraw>();
    }

    class GpuBatchSet
    {
        public readonly List<PrefabModel> models = new List<PrefabModel>();
        public readonly List<SubMeshDraw> allDraws = new List<SubMeshDraw>();
        public readonly List<Material> ownedMaterials = new List<Material>();
        public ShadowCastingMode shadowCasting = ShadowCastingMode.Off;
        public bool receiveShadows = true;

        public bool BuildModels(List<GameObject> prefabs, bool forceDoubleSided)
        {
            if (prefabs == null || prefabs.Count == 0)
                return false;

            foreach (var prefab in prefabs)
            {
                if (prefab == null)
                    continue;

                var model = new PrefabModel();
                var rootToLocal = prefab.transform.worldToLocalMatrix;

                foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
                {
                    var mesh = mf.sharedMesh;
                    if (mesh == null)
                        continue;
                    var renderer = mf.GetComponent<MeshRenderer>();
                    if (renderer == null)
                        continue;

                    var sharedMats = renderer.sharedMaterials;
                    Matrix4x4 rel = rootToLocal * mf.transform.localToWorldMatrix;

                    int subCount = Mathf.Max(1, mesh.subMeshCount);
                    for (int s = 0; s < subCount; s++)
                    {
                        Material src = (sharedMats != null && s < sharedMats.Length) ? sharedMats[s] : null;
                        if (src == null && sharedMats != null && sharedMats.Length > 0)
                            src = sharedMats[0];
                        if (src == null)
                            continue;

                        var instanced = new Material(src) { enableInstancing = true };
                        if (forceDoubleSided)
                        {
                            if (instanced.HasProperty("_Cull"))
                                instanced.SetInt("_Cull", 0);
                            else if (instanced.HasProperty("_CullMode"))
                                instanced.SetInt("_CullMode", 0);
                        }
                        ownedMaterials.Add(instanced);

                        var draw = new SubMeshDraw
                        {
                            mesh = mesh,
                            subMesh = s,
                            material = instanced,
                            relMatrix = rel
                        };
                        model.draws.Add(draw);
                        allDraws.Add(draw);
                    }
                }

                if (model.draws.Count > 0)
                    models.Add(model);
            }

            return models.Count > 0;
        }

        public void AddInstance(Matrix4x4 baseTRS)
        {
            if (models.Count == 0)
                return;
            var model = models[Random.Range(0, models.Count)];
            for (int i = 0; i < model.draws.Count; i++)
            {
                var d = model.draws[i];
                d.matrices.Add(baseTRS * d.relMatrix);
            }
        }

        public void FinalizeBatches()
        {
            foreach (var d in allDraws)
            {
                int total = d.matrices.Count;
                if (total == 0)
                {
                    d.batches = System.Array.Empty<Matrix4x4[]>();
                    continue;
                }
                int batchCount = (total + BatchSize - 1) / BatchSize;
                d.batches = new Matrix4x4[batchCount][];
                for (int b = 0; b < batchCount; b++)
                {
                    int start = b * BatchSize;
                    int len = Mathf.Min(BatchSize, total - start);
                    var arr = new Matrix4x4[len];
                    d.matrices.CopyTo(start, arr, 0, len);
                    d.batches[b] = arr;
                }
                d.matrices.Clear();
                d.matrices.TrimExcess();
            }
        }

        public int DrawnInstanceCount()
        {
            int total = 0;
            foreach (var d in allDraws)
                if (d.batches != null)
                    foreach (var b in d.batches)
                        total += b.Length;
            return total;
        }

        public void Draw(int layer)
        {
            for (int i = 0; i < allDraws.Count; i++)
            {
                var d = allDraws[i];
                if (d.batches == null)
                    continue;
                for (int b = 0; b < d.batches.Length; b++)
                {
                    var batch = d.batches[b];
                    Graphics.DrawMeshInstanced(
                        d.mesh, d.subMesh, d.material,
                        batch, batch.Length, null,
                        shadowCasting, receiveShadows, layer);
                }
            }
        }

        public void Dispose()
        {
            foreach (var m in ownedMaterials)
                if (m != null)
                    Object.Destroy(m);
            ownedMaterials.Clear();
        }
    }

    // ---- Per-rule runtime state for the shared scatter pass ----
    class RuleRuntime
    {
        public FoliageColourRule rule;
        public List<GameObject> prefabs;      // resolved prefabs (may differ from rule.prefabs for grass fallback)
        public GpuBatchSet gpu;               // non-null for GpuInstanced
        public Transform poolContainer;       // non-null for GameObjectPool
        public HashSet<Vector3Int> occupied = new HashSet<Vector3Int>();
        public float invCell = 1f;
        public int placed;
        // telemetry
        public int rejSlope, rejElev, rejDensity, rejSpacing;
    }

    readonly List<RuleRuntime> _runtimes = new List<RuleRuntime>();
    Planet _planet;
    int _layer;
    bool _ready;

    /// <summary>Active rule list: palette (assigned or Resources) first, else the inline fallback.</summary>
    public List<FoliageColourRule> GetActiveRules()
    {
        if (palette != null && palette.rules != null && palette.rules.Count > 0)
            return palette.rules;

        if (palette == null && !string.IsNullOrEmpty(paletteResourceName))
        {
            var loaded = Resources.Load<FoliagePalette>(paletteResourceName);
            if (loaded != null && loaded.rules != null && loaded.rules.Count > 0)
            {
                palette = loaded;
                return loaded.rules;
            }
        }

        return inlineRules;
    }

    /// <summary>Simple RGB euclidean colour distance. Chosen for predictability: tolerance reads as
    /// "how far in RGB space" and matches what the gradient editor shows. (Perceptual/HSV distance
    /// would weight hue differently and make the tolerance slider less intuitive.)</summary>
    public static float ColourDistance(Color a, Color b)
    {
        float dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
        return Mathf.Sqrt(dr * dr + dg * dg + db * db);
    }

    /// <summary>Colour-swatch band membership strength m∈(0,1] for a rule at a point's key colour. -1 if no match.</summary>
    static float MatchStrength(FoliageColourRule rule, Color keyColour, int biomeIndex, int keyIndex)
    {
        if (rule.matchByKey)
            return (biomeIndex == rule.biomeIndex && keyIndex == rule.keyIndex) ? 1f : -1f;

        float tol = Mathf.Max(1e-4f, rule.colourTolerance);
        float dist = ColourDistance(keyColour, rule.targetColour);
        float m = 1f - Mathf.Clamp01(dist / tol);
        return m > 0f ? m : -1f;
    }

    /// <summary>
    /// Membership strength m for a rule at a world point. -1 means "does not qualify".
    ///  • useBiomeGradientRule (GRASS PARITY): m = biome 'biomeIndex's CONTINUOUS greenness (0..1) by
    ///    elevation (latitude-INDEPENDENT, via Planet.GetSurfaceGreennessAtPosition). A SOFT gate keeps
    ///    every point whose greenness is at least <see cref="GradientGreenLo"/> so the band edges can
    ///    feather smoothly; clearly yellow/brown ground (greenness below Lo) is rejected. (The old hard
    ///    boolean IsGreenKeyColor gate clipped the fade — it rejected everything below greenness ≈0.48
    ///    at the lower edge, so density cliffed instead of tapering.)
    ///  • otherwise: colour-swatch distance / matchByKey (see <see cref="MatchStrength"/>).
    /// Returns m in [0,1] when qualified, or -1 when it does not qualify.
    /// </summary>
    static float RuleMatchStrength(Planet planet, FoliageColourRule rule, Vector3 pos, Color keyColour, int biomeIndex, int keyIndex)
    {
        // Biome-dominance gate (ANY rule): restrict this rule to where 'requiredBiomeIndex' dominates the
        // surface colour. Used by the desert (biome 1) and snow (biome 2) areas. Reject below the weight
        // threshold; qualifying points return full membership (m=1) — area is defined by biome + the
        // rule's elevation/slope, density/borders by BiomeDominanceFactor + targetCount/spacing. Rules
        // with requireBiomeDominance=false (grass, forest, palms, rocks) skip this entirely → unchanged.
        if (rule.requireBiomeDominance)
        {
            if (planet == null)
                return -1f;
            float w = Mathf.Clamp01(planet.GetBiomeWeightAtPosition(pos, rule.requiredBiomeIndex));
            if (w < rule.minRequiredBiomeWeight)
                return -1f;
            return 1f;
        }

        if (rule.useBiomeGradientRule)
        {
            if (planet == null)
                return -1f;
            // Biome exclusion: reject where OTHER biomes (desert/snow) meaningfully colour the ground so
            // grass stays in its own biome. Hard-reject past the threshold (not just keepProb=0) so this
            // point stops being a grass CANDIDATE and other rules (e.g. rocks) can claim it instead.
            float other = 1f - Mathf.Clamp01(planet.GetBiomeWeightAtPosition(pos, rule.biomeIndex));
            if (rule.maxOtherBiomeInfluence < 1f && other >= rule.maxOtherBiomeInfluence)
                return -1f;
            float greenness = Mathf.Clamp01(planet.GetSurfaceGreennessAtPosition(pos, rule.biomeIndex));
            // Soft gate: keep weakly-green EDGE points (so KeepProb can taper them to ~0), but reject
            // clearly non-green ground so yellow/brown never get grass. Lo sits well above beach-yellow
            // (~0.07) and brown (0).
            return greenness >= GradientGreenLo ? greenness : -1f;
        }
        return MatchStrength(rule, keyColour, biomeIndex, keyIndex);
    }

    /// <summary>
    /// Density multiplier (0..1) that feathers a gradient rule out as OTHER biomes take over the surface
    /// colour: 1 while biome 'biomeIndex' clearly dominates, smoothstep down to 0 as the combined
    /// other-biome influence rises to <see cref="FoliageColourRule.maxOtherBiomeInfluence"/> — so grass
    /// blends into the desert/snow border instead of cutting off. 1 for non-gradient rules / when disabled.
    /// </summary>
    static float BiomeExclusionFactor(Planet planet, FoliageColourRule rule, Vector3 pos)
    {
        if (!rule.useBiomeGradientRule || planet == null)
            return 1f;
        float threshold = Mathf.Clamp01(rule.maxOtherBiomeInfluence);
        if (threshold >= 1f)
            return 1f; // exclusion disabled
        float other = 1f - Mathf.Clamp01(planet.GetBiomeWeightAtPosition(pos, rule.biomeIndex));
        if (other >= threshold)
            return 0f;
        // Feather over the top 40% of the allowed range (smoothstep -> 0 slope at both ends, no cliff).
        float fadeStart = threshold * 0.6f;
        float t = Mathf.InverseLerp(threshold, fadeStart, other); // 1 below fadeStart, 0 at threshold
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Density multiplier (0..1) for a biome-dominance rule (desert/snow): 0 at the required-biome weight
    /// threshold, smoothstep up to 1 as that biome takes firm hold — so the new area feathers in at its
    /// border instead of a hard edge, mirroring the grass exclusion feather on the other side of the
    /// boundary. 1 for rules without requireBiomeDominance.
    /// </summary>
    static float BiomeDominanceFactor(Planet planet, FoliageColourRule rule, Vector3 pos)
    {
        if (!rule.requireBiomeDominance || planet == null)
            return 1f;
        float threshold = Mathf.Clamp01(rule.minRequiredBiomeWeight);
        float w = Mathf.Clamp01(planet.GetBiomeWeightAtPosition(pos, rule.requiredBiomeIndex));
        if (w <= threshold)
            return 0f;
        // Reach full density 40% of the way from the threshold up to a fully-owned biome (smoothstep).
        float fadeEnd = Mathf.Lerp(threshold, 1f, 0.4f);
        float t = Mathf.InverseLerp(threshold, fadeEnd, w); // 0 at threshold, 1 at fadeEnd
        return t * t * (3f - 2f * t);
    }

    static float KeepProb(FoliageColourRule rule, float m)
    {
        if (rule.useBiomeGradientRule)
        {
            // m is continuous greenness. Smoothstep-ramp it across the [Lo,Hi] window: 0 at/below the
            // band border, 1 in the solid-green core. Multiplying by 'feather' guarantees the keep
            // probability reaches EXACTLY zero at the border (no residual grass on yellow/brown), while
            // edgeDensity lifts the toe and densityFalloff shapes the ramp.
            float t = Mathf.InverseLerp(GradientGreenLo, GradientGreenHi, m);
            float feather = t * t * (3f - 2f * t); // smoothstep (0 slope at both ends -> no hard edge)
            return feather * Mathf.Lerp(rule.edgeDensity, 1f, Mathf.Pow(feather, rule.densityFalloff));
        }
        return Mathf.Lerp(rule.edgeDensity, 1f, Mathf.Pow(Mathf.Clamp01(m), rule.densityFalloff));
    }

    /// <summary>How close this point's biome-latitude is to the rule's home biome (biomeIndex), 0..1.
    /// 1 at the home latitude, fading to 0 once it is `latitudeWidth` (in biome-percent) away.</summary>
    static float LatitudeWeight(FoliageColourRule rule, float biomePercent, int numBiomes)
    {
        float home = numBiomes > 1 ? rule.biomeIndex / (float)(numBiomes - 1) : 0f;
        float width = Mathf.Max(0.05f, rule.latitudeWidth);
        return 1f - Mathf.Clamp01(Mathf.Abs(biomePercent - home) / width);
    }

    /// <summary>Multiplicative latitude density factor for keepProb. 1 when latitudeInfluence is 0
    /// (approved behaviour unchanged); blends toward <see cref="LatitudeWeight"/> as influence rises.</summary>
    static float LatitudeFactor(FoliageColourRule rule, float biomePercent, int numBiomes)
    {
        if (rule.latitudeInfluence <= 0f)
            return 1f;
        return Mathf.Lerp(1f, LatitudeWeight(rule, biomePercent, numBiomes), Mathf.Clamp01(rule.latitudeInfluence));
    }

    List<GameObject> ResolvePrefabs(FoliageColourRule rule)
    {
        if (rule.prefabs != null && rule.prefabs.Count > 0)
            return rule.prefabs;

        // Grass fallback: reuse the Meadow Grass prefabs from RichPlanetFlora so the grass rule works
        // even before the user assigns prefabs (parity with GpuGrassCarpet.ResolveGrassPrefabs).
        var profile = Resources.Load<FoliageSpawnProfile>("RichPlanetFlora");
        if (profile != null && profile.spawnRules != null)
        {
            foreach (var r in profile.spawnRules)
                if (r != null && r.name == "Meadow Grass" && r.prefabs != null && r.prefabs.Count > 0)
                    return r.prefabs;
        }
        return null;
    }

    IEnumerator Start()
    {
        _layer = gameObject.layer;

        _planet = Object.FindFirstObjectByType<Planet>();
        if (_planet == null)
        {
            Debug.LogWarning("[FoliageByColour] No Planet found; disabling.");
            enabled = false;
            yield break;
        }

        int waited = 0;
        while (!_planet.IsGenerated)
        {
            if (logResults && waited == 300)
                Debug.LogWarning("[FoliageByColour] Still waiting for Planet.IsGenerated after ~5s — scatter is blocked on planet generation.");
            waited++;
            yield return null;
        }
        yield return null;

        if (!BuildRuntimes())
        {
            Debug.LogWarning("[FoliageByColour] No usable rules (no prefabs / meshes available); disabling.");
            enabled = false;
            yield break;
        }

        if (logResults)
            Debug.Log($"[FoliageByColour] Planet generated — scattering {_runtimes.Count} rule(s).");

        yield return StartCoroutine(Scatter());

        foreach (var rt in _runtimes)
            rt.gpu?.FinalizeBatches();
        _ready = true;

        if (logResults)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[FoliageByColour] Ready.\n");
            foreach (var rt in _runtimes)
            {
                int drawn = rt.gpu != null ? rt.gpu.DrawnInstanceCount() : rt.placed;
                sb.Append($"  rule '{rt.rule.name}' ({rt.rule.render}): placed {rt.placed}/{rt.rule.targetCount}, drawn {drawn}. " +
                          $"rejected -> slope {rt.rejSlope}, elev {rt.rejElev}, density {rt.rejDensity}, spacing {rt.rejSpacing}\n");
            }
            Debug.Log(sb.ToString());
        }
    }

    bool BuildRuntimes()
    {
        _runtimes.Clear();
        var rules = GetActiveRules();
        if (rules == null || rules.Count == 0)
            return false;

        foreach (var rule in rules)
        {
            if (rule == null || !rule.enabled)
                continue;

            var prefabs = ResolvePrefabs(rule);
            var rt = new RuleRuntime
            {
                rule = rule,
                prefabs = prefabs,
                invCell = 1f / Mathf.Max(0.05f, rule.minSpacing),
            };

            if (rule.render == FoliageRenderMode.GpuInstanced)
            {
                var gpu = new GpuBatchSet
                {
                    shadowCasting = rule.shadowCasting,
                    receiveShadows = rule.receiveShadows,
                };
                if (!gpu.BuildModels(prefabs, rule.forceDoubleSided))
                {
                    if (logResults)
                        Debug.LogWarning($"[FoliageByColour] Rule '{rule.name}' (GpuInstanced) has no usable meshes — skipped.");
                    continue;
                }
                rt.gpu = gpu;
            }
            else // GameObjectPool
            {
                if (prefabs == null || prefabs.Count == 0)
                {
                    if (logResults)
                        Debug.LogWarning($"[FoliageByColour] Rule '{rule.name}' (GameObjectPool) has no prefabs — skipped.");
                    continue;
                }
                var container = new GameObject($"Foliage_{rule.name}");
                container.transform.SetParent(transform, false);
                rt.poolContainer = container.transform;
            }

            _runtimes.Add(rt);
        }

        return _runtimes.Count > 0;
    }

    /// <summary>Number of biomes from the planet's colour settings (public fields — no Planet.cs change). Min 1.</summary>
    int BiomeCount()
    {
        var biomes = _planet != null && _planet.colourSettings != null && _planet.colourSettings.biomeColourSettings != null
            ? _planet.colourSettings.biomeColourSettings.biomes : null;
        return (biomes != null && biomes.Length > 0) ? biomes.Length : 1;
    }

    bool AllRulesFull()
    {
        foreach (var rt in _runtimes)
            if (rt.placed < rt.rule.targetCount)
                return false;
        return true;
    }

    IEnumerator Scatter()
    {
        Vector3 center = _planet.transform.position;
        float baseRadius = (_planet.shapeSettings != null) ? _planet.GetBaseRadiusWorld() : 400f;
        float maxRadius = (_planet.shapeSettings != null) ? _planet.GetMaxSurfaceRadiusWorld() : baseRadius;
        if (maxRadius < baseRadius)
            maxRadius = baseRadius;

        var groundMask = LayerMask.GetMask("Default", "Ground");
        if (groundMask == 0)
            groundMask = ~0;

        float rayStartRadius = maxRadius + rayHeightAboveSurface;
        float rayLength = maxRadius + rayHeightAboveSurface * 3f;
        float waterRadius = excludeUnderwater ? _planet.GetWaterRadiusWorld() : -1f;

        long totalTarget = 0;
        foreach (var rt in _runtimes)
            totalTarget += rt.rule.targetCount;
        long maxAttempts = totalTarget * attemptBudgetMultiplier + 8000;

        int numBiomes = BiomeCount();

        int budget = 0;
        int instThisFrame = 0;
        int rejNoHit = 0, rejUnderBase = 0, rejWater = 0, rejNoRule = 0;

        for (long attempt = 0; attempt < maxAttempts && !AllRulesFull(); attempt++)
        {
            if (++budget >= scatterPerFrame)
            {
                budget = 0;
                instThisFrame = 0;
                yield return null;
            }

            Vector3 dir = Random.onUnitSphere;
            Vector3 rayStart = center + dir * rayStartRadius;
            if (!Physics.Raycast(rayStart, -dir, out var hit, rayLength, groundMask))
            { rejNoHit++; continue; }

            Vector3 pos = hit.point;
            Vector3 radial = (pos - center).normalized;
            float dist = (pos - center).magnitude;

            if (dist < baseRadius - 1f)
            { rejUnderBase++; continue; }
            if (waterRadius > 0f && dist < waterRadius + 0.2f)
            { rejWater++; continue; }

            float slope = Vector3.Angle(hit.normal, radial);
            float elevationNorm = _planet.GetNormalizedElevationAtPosition(pos);
            Color keyColour = _planet.GetSurfaceKeyColorAtPosition(pos);
            _planet.ClassifySurfaceAtPosition(pos, out int clsBiome, out int clsKey, out _);

            // Assign to the rule with the strongest membership (best match wins so bands don't fight).
            // Colour rules use colour-distance membership; gradient (grass-parity) rules use biome
            // greenness — compared uniformly here. bestM starts at -1 so a qualified gradient rule with
            // greenness exactly 0 (green-band edge) is still a candidate.
            RuleRuntime best = null;
            float bestM = -1f;
            foreach (var rt in _runtimes)
            {
                if (rt.placed >= rt.rule.targetCount)
                    continue;
                if (slope > rt.rule.maxSlope)
                { rt.rejSlope++; continue; }
                if (elevationNorm < rt.rule.elevationRange.x || elevationNorm > rt.rule.elevationRange.y)
                { rt.rejElev++; continue; }

                float m = RuleMatchStrength(_planet, rt.rule, pos, keyColour, clsBiome, clsKey);
                if (m >= 0f && m > bestM)
                {
                    bestM = m;
                    best = rt;
                }
            }

            if (best == null)
            { rejNoRule++; continue; }

            // Density falloff: thin by colour-match/greenness strength (dense in the best match, sparse
            // at edges), then optionally bias by latitude proximity to the rule's home biome.
            float keepProb = KeepProb(best.rule, bestM);
            // Feather grass out where biome 1/2 colour the ground (gradient rules only; 1f otherwise).
            keepProb *= BiomeExclusionFactor(_planet, best.rule, pos);
            // Feather biome-dominance areas (desert/snow) in at their borders (1f for other rules).
            keepProb *= BiomeDominanceFactor(_planet, best.rule, pos);
            if (best.rule.latitudeInfluence > 0f)
            {
                float biomePercent = _planet.GetBiomePercentAtPosition(pos);
                keepProb *= LatitudeFactor(best.rule, biomePercent, numBiomes);
            }
            if (Random.value > keepProb)
            { best.rejDensity++; continue; }

            // Per-rule spacing grid prevents stacking within a rule.
            var cell = new Vector3Int(
                Mathf.FloorToInt(pos.x * best.invCell),
                Mathf.FloorToInt(pos.y * best.invCell),
                Mathf.FloorToInt(pos.z * best.invCell));
            if (!best.occupied.Add(cell))
            { best.rejSpacing++; continue; }

            Vector3 up = best.rule.orient == FoliageOrientMode.Upright ? radial : hit.normal;
            Quaternion rot = Quaternion.FromToRotation(Vector3.up, up)
                             * Quaternion.AngleAxis(Random.value * 360f, Vector3.up);
            float scale = Random.Range(best.rule.scaleRange.x, best.rule.scaleRange.y);
            Vector3 placePos = pos + hit.normal * best.rule.surfaceOffset;

            if (best.gpu != null)
            {
                Matrix4x4 baseTRS = Matrix4x4.TRS(placePos, rot, Vector3.one * scale);
                best.gpu.AddInstance(baseTRS);
            }
            else if (best.poolContainer != null && best.prefabs != null && best.prefabs.Count > 0)
            {
                var prefab = best.prefabs[Random.Range(0, best.prefabs.Count)];
                if (prefab != null)
                {
                    var go = Object.Instantiate(prefab, placePos, rot, best.poolContainer);
                    go.transform.localScale = prefab.transform.localScale * scale;
                    if (++instThisFrame >= maxInstantiatesPerFrame)
                    {
                        instThisFrame = 0;
                        budget = 0;
                        yield return null;
                    }
                }
            }

            best.placed++;
        }

        if (logResults)
        {
            Debug.Log($"[FoliageByColour] Scatter done. Global rejects -> noHit {rejNoHit}, underBase {rejUnderBase}, " +
                      $"water {rejWater}, noRuleMatch {rejNoRule}. Rules: {_runtimes.Count}, maxAttempts {maxAttempts}.");
        }
    }

    void Update()
    {
        if (!_ready)
            return;
        for (int i = 0; i < _runtimes.Count; i++)
            _runtimes[i].gpu?.Draw(_layer);
    }

    void OnDisable()
    {
        _ready = false;
    }

    void OnDestroy()
    {
        foreach (var rt in _runtimes)
            rt.gpu?.Dispose();
        _runtimes.Clear();
    }

    [ContextMenu("Diagnose Probe Position")]
    void DiagnoseProbePosition()
    {
        var planet = Object.FindFirstObjectByType<Planet>();
        if (planet == null)
        {
            Debug.LogWarning("[FoliageByColour] Diagnose: no Planet found.");
            return;
        }

        // In edit mode the planet hasn't generated, so build it first — otherwise every query is garbage.
        if (!planet.IsGenerated)
            planet.GeneratePlanet();

        Vector3 pos = debugProbePosition;
        Vector3 center = planet.transform.position;
        Vector3 radial = (pos - center).normalized;
        float dist = (pos - center).magnitude;
        float waterRadius = planet.GetWaterRadiusWorld();
        float elevNorm = planet.GetNormalizedElevationAtPosition(pos);
        Color keyColour = planet.GetSurfaceKeyColorAtPosition(pos);
        planet.ClassifySurfaceAtPosition(pos, out int clsBiome, out int clsKey, out _);

        float maxRadius = planet.shapeSettings != null ? planet.GetMaxSurfaceRadiusWorld() : 400f;
        var mask = LayerMask.GetMask("Default", "Ground");
        if (mask == 0) mask = ~0;
        float slope = -1f;
        if (Physics.Raycast(center + radial * (maxRadius + rayHeightAboveSurface), -radial,
                out var hit, maxRadius + rayHeightAboveSurface * 3f, mask))
            slope = Vector3.Angle(hit.normal, radial);

        bool waterOk = !(excludeUnderwater && waterRadius > 0f && dist < waterRadius + 0.2f);
        float biomePercent = planet.GetBiomePercentAtPosition(pos);
        int numBiomes = (planet.colourSettings != null && planet.colourSettings.biomeColourSettings != null &&
                         planet.colourSettings.biomeColourSettings.biomes != null &&
                         planet.colourSettings.biomeColourSettings.biomes.Length > 0)
            ? planet.colourSettings.biomeColourSettings.biomes.Length : 1;

        var sb = new System.Text.StringBuilder();
        sb.Append($"[FoliageByColour] Probe {pos}\n");
        sb.Append($"  dominant key  = biome {clsBiome}, key {clsKey}, RGB ({keyColour.r:F2},{keyColour.g:F2},{keyColour.b:F2})\n");
        sb.Append($"  elevation     = {elevNorm:F3}\n");
        sb.Append($"  slope         = {(slope < 0f ? "no-hit" : slope.ToString("F1"))}\n");
        sb.Append($"  biomePercent  = {biomePercent:F3} (0=first biome latitude .. 1=last), numBiomes {numBiomes}\n");
        sb.Append($"  underwater    = dist {dist:F1} vs water {waterRadius:F1} -> {(waterOk ? "OK" : "FAIL")}\n");

        var rules = GetActiveRules();
        float bestM = -1f; string bestName = "(none)";
        foreach (var rule in rules)
        {
            if (rule == null || !rule.enabled) continue;
            bool slopeOk = slope < 0f || slope <= rule.maxSlope;
            bool elevOk = elevNorm >= rule.elevationRange.x && elevNorm <= rule.elevationRange.y;
            float m = RuleMatchStrength(planet, rule, pos, keyColour, clsBiome, clsKey);
            float baseKeep = m >= 0f ? KeepProb(rule, m) : 0f;
            float biomeFactor = BiomeExclusionFactor(planet, rule, pos);
            float domFactor = BiomeDominanceFactor(planet, rule, pos);
            float latWeight = LatitudeWeight(rule, biomePercent, numBiomes);
            float latFactor = LatitudeFactor(rule, biomePercent, numBiomes);
            float keep = baseKeep * biomeFactor * domFactor * latFactor;
            bool gates = slopeOk && elevOk && waterOk && m >= 0f;
            if (gates && m > bestM) { bestM = m; bestName = rule.name; }

            string latLine = $"      latitude: biomePercent {biomePercent:F2}, weight {latWeight:F2}, influence {rule.latitudeInfluence:F2} (width {rule.latitudeWidth:F2}) -> baseKeep {baseKeep:F2} * exclude {biomeFactor:F2} * dominance {domFactor:F2} * lat {latFactor:F2} = keepProb {keep:F2}\n";
            if (rule.requireBiomeDominance)
            {
                float reqW = planet.GetBiomeWeightAtPosition(pos, rule.requiredBiomeIndex);
                sb.Append($"      biome-dominance: require biome {rule.requiredBiomeIndex} weight>={rule.minRequiredBiomeWeight:F2}, here={reqW:F2} -> {(reqW >= rule.minRequiredBiomeWeight ? "in-area" : "out")} (factor {domFactor:F2})\n");
            }
            if (rule.useBiomeGradientRule)
            {
                bool greenOk = m >= 0f;
                float greenness = planet.GetSurfaceGreennessAtPosition(pos, rule.biomeIndex);
                float otherInfluence = 1f - Mathf.Clamp01(planet.GetBiomeWeightAtPosition(pos, rule.biomeIndex));
                sb.Append($"  rule '{rule.name}' [{rule.render}] BIOME-GRADIENT(green) biome {rule.biomeIndex} (latitude-independent coverage)\n");
                sb.Append($"      green={(greenOk ? "OK" : "FAIL")} greenness={greenness:F2} | biome{rule.biomeIndex} weight={1f - otherInfluence:F2} other={otherInfluence:F2} (max {rule.maxOtherBiomeInfluence:F2}, factor {biomeFactor:F2}) | slope {(slopeOk ? "OK" : "FAIL")} elev {(elevOk ? "OK" : "FAIL")} -> {(gates ? "candidate" : "blocked")}\n");
                sb.Append(latLine);
            }
            else
            {
                sb.Append($"  rule '{rule.name}' [{rule.render}] COLOUR target ({rule.targetColour.r:F2},{rule.targetColour.g:F2},{rule.targetColour.b:F2}) tol {rule.colourTolerance:F2}\n");
                if (rule.matchByKey)
                    sb.Append($"      matchByKey biome {rule.biomeIndex}/key {rule.keyIndex} vs here {clsBiome}/{clsKey} -> m={(m < 0f ? 0f : m):F2} | slope {(slopeOk ? "OK" : "FAIL")} elev {(elevOk ? "OK" : "FAIL")} -> {(gates ? "candidate" : "blocked")}\n");
                else
                    sb.Append($"      dist={ColourDistance(keyColour, rule.targetColour):F2} m={(m < 0f ? 0f : m):F2} | slope {(slopeOk ? "OK" : "FAIL")} elev {(elevOk ? "OK" : "FAIL")} -> {(gates ? "candidate" : "blocked")}\n");
                sb.Append(latLine);
            }
        }
        sb.Append($"  WINNER (highest m, gates passed): {bestName} (m={(bestM < 0f ? 0f : bestM):F2}) -> {(bestM >= 0f && waterOk ? "WOULD SPAWN (probabilistic by keepProb)" : "BLOCKED")}");
        Debug.Log(sb.ToString());
    }
}
