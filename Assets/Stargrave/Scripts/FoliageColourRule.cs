using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>How a rule's instances are rendered.</summary>
public enum FoliageRenderMode
{
    [Tooltip("GPU-instanced via Graphics.DrawMeshInstanced — cheap, no GameObjects. Best for grass/flowers/small clutter (like GpuGrassCarpet).")]
    GpuInstanced = 0,
    [Tooltip("Real instantiated prefabs (keep their colliders/LODGroups). Best for trees/rocks the player interacts with. Heavier; cap counts.")]
    GameObjectPool = 1,
}

/// <summary>How a rule's instances are oriented on the surface.</summary>
public enum FoliageOrientMode
{
    [Tooltip("Align local +Y to the surface normal (flat grass clumps hug terrain).")]
    AlignToSurface = 0,
    [Tooltip("Align local +Y to radial-up (away from planet centre). Keeps trees/rocks standing upright on slopes.")]
    Upright = 1,
}

/// <summary>
/// One "colour → asset" foliage placement rule. A point on the planet matches this rule by how
/// close the point's dominant gradient KEY colour is to <see cref="targetColour"/> (within
/// <see cref="colourTolerance"/>), reproducing the grass density model when the target is the
/// green key. Optionally match an exact gradient key instead via <see cref="matchByKey"/>.
/// </summary>
[System.Serializable]
public class FoliageColourRule
{
    [Tooltip("Label for logs/telemetry only.")]
    public string name = "Rule";
    public bool enabled = true;

    [Header("Targeting mode")]
    [Tooltip("GRASS PARITY: ignore colour-distance and use biome 'biomeIndex's gradient-green-by-elevation test " +
             "(latitude-INDEPENDENT), exactly like the approved element-0 grass. Membership strength = that biome's " +
             "greenness (0..1); density uses edgeDensity/densityFalloff. Leave OFF for colour-swatch matching (trees/rocks).")]
    public bool useBiomeGradientRule = false;

    [Header("Targeting — colour swatch + tolerance (primary)")]
    [Tooltip("The gradient KEY colour this rule places assets on. A point matches by RGB distance from its dominant key colour. (Ignored when 'Use Biome Gradient Rule' is on.)")]
    public Color targetColour = new Color(0.22f, 0.45f, 0.02f, 1f);
    [Tooltip("Max RGB distance (euclidean) from the target before the point is rejected. Larger = wider band. ~0.35 is a good start.")]
    [Range(0.001f, 2f)] public float colourTolerance = 0.35f;

    [Header("Targeting — exact key override (optional)")]
    [Tooltip("Ignore the colour swatch and match an exact gradient key (biome + key index) instead.")]
    public bool matchByKey = false;
    [Tooltip("Biome index for the exact-key override.")]
    public int biomeIndex = 0;
    [Tooltip("Gradient key index within that biome for the exact-key override.")]
    public int keyIndex = 0;

    [Header("Prefabs")]
    [Tooltip("Prefabs scattered for this rule (one is chosen at random per placement). Leave empty for the grass rule to auto-resolve from RichPlanetFlora.")]
    public List<GameObject> prefabs = new List<GameObject>();

    [Header("Coverage / density")]
    [Tooltip("Target number of placements for this rule. Keep small for GameObjectPool (trees/rocks).")]
    public int targetCount = 50000;
    [Tooltip("Minimum distance between this rule's instances (world units). Per-rule spacing grid prevents stacking.")]
    [Min(0.05f)] public float minSpacing = 0.35f;
    [Tooltip("Spawn chance at the FAINTEST colour match edge (0 = bare at the tolerance boundary, 1 = full density everywhere in band).")]
    [Range(0f, 1f)] public float edgeDensity = 0.05f;
    [Tooltip("Falloff shape. 1 = linear; >1 concentrates instances into the best colour match and thins the edges faster.")]
    [Range(0.25f, 4f)] public float densityFalloff = 1.6f;

    [Header("Biome exclusion (gradient rule only)")]
    [Tooltip("Reject/fade this rule where biomes OTHER than 'biomeIndex' influence the blended surface " +
             "colour. 0 = only pure biomeIndex ground qualifies; 1 = never excluded by biome blend. " +
             "~0.5 keeps grass in the green biome (0) and feathers it out as desert/snow (biome 1/2) " +
             "take over the ground colour. Only used when 'Use Biome Gradient Rule' is on.")]
    [Range(0f, 1f)] public float maxOtherBiomeInfluence = 0.5f;

    [Header("Biome dominance gate (any rule)")]
    [Tooltip("Restrict this rule to where a chosen biome DOMINATES the blended surface colour (e.g. " +
             "desert = 1, snow = 2). OFF by default so existing rules are unaffected. Works for any rule " +
             "(colour or gradient) and is independent of 'Use Biome Gradient Rule'.")]
    public bool requireBiomeDominance = false;
    [Tooltip("Which biome must dominate here for this rule to place (0 = temperate/green, 1 = desert, 2 = snow).")]
    public int requiredBiomeIndex = 1;
    [Tooltip("Minimum blend weight (0..1) the required biome must have at a point. ~0.5 = that biome covers " +
             "at least half the colour. Density feathers smoothly to 0 as the weight falls to this threshold, " +
             "so the area blends at its borders instead of cutting off hard.")]
    [Range(0f, 1f)] public float minRequiredBiomeWeight = 0.5f;

    [Header("Latitude density bias (optional, default OFF)")]
    [Tooltip("0 = latitude has NO effect (coverage/density unchanged — approved grass). 1 = density fully follows " +
             "how close this point's biome-latitude is to this rule's home biome (biomeIndex). Use to thin grass " +
             "toward desert/snow latitudes without changing where it qualifies.")]
    [Range(0f, 1f)] public float latitudeInfluence = 0f;
    [Tooltip("Latitude falloff span. Smaller = density drops off sharply away from the home latitude; larger = gentle. " +
             "Measured in biome-percent units (0..1).")]
    [Range(0.05f, 1f)] public float latitudeWidth = 0.5f;

    [Header("Render")]
    public FoliageRenderMode render = FoliageRenderMode.GpuInstanced;
    public FoliageOrientMode orient = FoliageOrientMode.AlignToSurface;

    [Header("Placement")]
    [Tooltip("Uniform scale range (x = min, y = max) applied per instance.")]
    public Vector2 scaleRange = new Vector2(0.8f, 1.5f);
    [Tooltip("Reject faces steeper than this (degrees from radial-up). Flat clumps look wrong on near-vertical faces.")]
    [Range(0f, 90f)] public float maxSlope = 80f;
    [Tooltip("Normalized elevation band (x = min, y = max) this rule is allowed to spawn in.")]
    public Vector2 elevationRange = new Vector2(0f, 1f);
    [Tooltip("Lift each instance along the surface normal to avoid z-fighting with the ground.")]
    public float surfaceOffset = 0.02f;

    [Header("Rendering (GpuInstanced only)")]
    public ShadowCastingMode shadowCasting = ShadowCastingMode.Off;
    public bool receiveShadows = true;
    [Tooltip("Force double-sided so flat cards render from both sides.")]
    public bool forceDoubleSided = true;
}
