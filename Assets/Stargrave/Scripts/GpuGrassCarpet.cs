using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// GPU-instanced grass carpet for the procedural planet.
///
/// Renders hundreds of thousands of grass blades with a handful of draw calls via
/// <see cref="Graphics.DrawMeshInstanced"/> — no per-blade GameObjects, so there is no
/// instantiate stall and no scene bloat. Blades are scattered over green surface only,
/// laid flat to the terrain normal (matching the Kenney flat-clump meshes), then drawn
/// every frame from precomputed transform matrices.
///
/// Zero-config: drop this on any GameObject. It auto-finds the <see cref="Planet"/> and
/// reuses the grass prefabs/meshes from the "Meadow Grass" rule of the foliage profile
/// (or any prefabs assigned in <see cref="grassPrefabs"/>).
/// </summary>
public class GpuGrassCarpet : MonoBehaviour
{
    [Header("Coverage")]
    [Tooltip("Target number of grass blades to scatter. GPU-instanced, so this can be large.")]
    public int count = 600000;
    [Tooltip("Raycasts are spread across frames during scatter to avoid a load hang.")]
    [Min(500)] public int scatterPerFrame = 16000;
    [Tooltip("Minimum distance between blades (world units). Prevents grass stacking on grass and gives even coverage.")]
    [Min(0.05f)] public float minSpacing = 0.35f;

    [Header("Density falloff (greener = denser)")]
    [Tooltip("ON: thin the grass by how green the ground is — dense in the greenest core, gradually sparse toward the green-band boundary.")]
    public bool densityByGreenness = true;
    [Tooltip("Spawn chance at the FAINTEST green edge (0 = bare right at the boundary, 1 = full density everywhere).")]
    [Range(0f, 1f)] public float edgeDensity = 0.05f;
    [Tooltip("Falloff shape. 1 = linear; >1 concentrates grass into the greenest areas and thins the mid-tones faster.")]
    [Range(0.25f, 4f)] public float densityFalloff = 1.6f;

    public enum GreenColorSource
    {
        [Tooltip("The dominant gradient KEY (terrain band) the planet's own colour math picks at " +
                 "this point — the crisp 'this is the grass band' decision. Grass spawns exactly " +
                 "where the gradient is green. Recommended.")]
        SurfaceGradientKey = 0,
        [Tooltip("The exact blended colour the shader PAINTS on the ground (gradient + tint + height bands).")]
        PaintedSurface = 1,
        [Tooltip("The unblended grass-biome gradient (element 0), sampled by elevation.")]
        BiomeGradient = 2,
        [Tooltip("The blended biome gradient by elevation, without height-band tint.")]
        BlendedBiome = 3,
    }

    [Header("Green test — keyed off the colour math that paints the surface")]
    [Tooltip("Preferred. Snap the final painted colour to its nearest gradient SHADE and grass only " +
             "the green shades. Matches what the eye calls green; ignores the RGB margins below.")]
    public bool useNearestGreenShade = true;
    [Tooltip("Which colour drives the green test when 'Use Nearest Green Shade' is OFF.")]
    public GreenColorSource greenSource = GreenColorSource.PaintedSurface;
    [Tooltip("Which biome's gradient defines green when using BiomeGradient source. 0 = first/grass biome.")]
    public int grassBiomeIndex = 0;
    [Tooltip("Green channel must be at least this. Lower = more coverage.")]
    [Range(0f, 1f)] public float minGreenChannel = 0.16f;
    [Tooltip("Green must exceed RED by at least this. 0 = include the olive/yellow-green blend; raise to reject more yellow.")]
    [Range(0f, 0.5f)] public float minGreenOverRed = 0.0f;
    [Tooltip("Green must exceed BLUE by at least this. Rejects blue water and grey/white snow & rock.")]
    [Range(0f, 0.5f)] public float minGreenOverBlue = 0.04f;

    [Header("Elevation band from gradient sliders")]
    [Tooltip("ON: read the GREEN colour-key slider positions from the biome's gradient and use that " +
             "span as the grass elevation band. Drag the green keys in the gradient editor and the " +
             "grass band follows. Turn OFF to set Min/Max Elevation manually below.")]
    public bool deriveElevationFromGradientKeys = false;
    [Tooltip("Widen the derived band by this much on each side (0 = exactly the slider positions).")]
    [Range(0f, 0.2f)] public float gradientBandPadding = 0.02f;

    [Header("Area limits")]
    [Tooltip("The active biomes shader applies no rock tint, so green stays green on slopes. Keep high and let the colour test decide; only caps near-vertical faces where flat clumps look wrong.")]
    [Range(0f, 90f)] public float maxSlope = 80f;
    [Tooltip("Skip low ground (beach/shore). Auto-set when 'Derive Elevation From Gradient Keys' is on.")]
    [Range(0f, 1f)] public float minElevation = 0f;
    [Tooltip("Cap how high grass climbs. Auto-set when 'Derive Elevation From Gradient Keys' is on.")]
    [Range(0f, 1f)] public float maxElevation = 1f;
    public bool excludeUnderwater = true;
    [Tooltip("Latitude gate (biome %): 1 = OFF (recommended — let the painted surface colour decide). " +
             "Lower only if you want to force grass off desert/snow latitudes regardless of colour. " +
             "Note: low values create a visible latitude BAND, so leave at 1 to follow the gradient.")]
    [Range(0f, 1f)] public float grassBiomeMaxPercent = 1f;

    [Header("Diagnostics")]
    [Tooltip("Paste a world position, then right-click the component header -> Diagnose Probe Position to see why it is/ isn't grassed.")]
    public Vector3 debugProbePosition;

    [Header("Placement")]
    [Tooltip("Lift each blade slightly along the surface normal to avoid z-fighting with the ground.")]
    public float surfaceOffset = 0.02f;
    [Range(0.3f, 3f)] public float scaleMin = 0.8f;
    [Range(0.3f, 3f)] public float scaleMax = 1.5f;
    [Tooltip("How high above the surface rays start (world units). Should clear the tallest terrain.")]
    public float rayHeightAboveSurface = 80f;

    [Header("Source meshes")]
    [Tooltip("Optional. If empty, grass prefabs are pulled from the 'Meadow Grass' rule of RichPlanetFlora.")]
    public List<GameObject> grassPrefabs = new List<GameObject>();

    [Header("Rendering")]
    public ShadowCastingMode shadowCasting = ShadowCastingMode.Off;
    public bool receiveShadows = true;
    [Tooltip("Force double-sided so flat grass cards render from both sides.")]
    public bool forceDoubleSided = true;

    [Header("Debug")]
    public bool logResults = true;

    const int BatchSize = 1023; // Graphics.DrawMeshInstanced hard limit per call.

    class SubMeshDraw
    {
        public Mesh mesh;
        public int subMesh;
        public Material material;
        public Matrix4x4 relMatrix; // mesh transform relative to its prefab root
        public readonly List<Matrix4x4> matrices = new List<Matrix4x4>();
        public Matrix4x4[][] batches;
    }

    class PrefabModel
    {
        public readonly List<SubMeshDraw> draws = new List<SubMeshDraw>();
    }

    readonly List<PrefabModel> _models = new List<PrefabModel>();
    readonly List<Material> _ownedMaterials = new List<Material>();
    readonly List<SubMeshDraw> _allDraws = new List<SubMeshDraw>();

    Planet _planet;
    int _layer;
    bool _ready;

    IEnumerator Start()
    {
        _layer = gameObject.layer;

        if (logResults)
            Debug.Log($"[GpuGrassCarpet] Start: nearestGreenShade={useNearestGreenShade}, source={greenSource}, " +
                      $"count={count}, minSpacing={minSpacing}, maxSlope={maxSlope}, minElev={minElevation}, " +
                      $"prefabs={(grassPrefabs != null ? grassPrefabs.Count : 0)}.");

        _planet = Object.FindFirstObjectByType<Planet>();
        if (_planet == null)
        {
            Debug.LogWarning("[GpuGrassCarpet] No Planet found; disabling.");
            enabled = false;
            yield break;
        }

        int waited = 0;
        while (!_planet.IsGenerated)
        {
            if (logResults && waited == 300)
                Debug.LogWarning("[GpuGrassCarpet] Still waiting for Planet.IsGenerated after ~5s — scatter is blocked on planet generation.");
            waited++;
            yield return null;
        }
        yield return null;
        if (logResults)
            Debug.Log("[GpuGrassCarpet] Planet generated — beginning scatter.");

        if (deriveElevationFromGradientKeys &&
            _planet.TryGetGrassElevationBand(grassBiomeIndex, minGreenChannel, minGreenOverRed, minGreenOverBlue,
                out float bandMin, out float bandMax))
        {
            minElevation = Mathf.Clamp01(bandMin - gradientBandPadding);
            maxElevation = Mathf.Clamp01(bandMax + gradientBandPadding);
            if (logResults)
                Debug.Log($"[GpuGrassCarpet] Grass elevation band from gradient green-key sliders " +
                          $"(biome {grassBiomeIndex}): [{bandMin:F3}..{bandMax:F3}] -> using " +
                          $"[{minElevation:F3}..{maxElevation:F3}] with {gradientBandPadding:F3} padding.");
        }

        if (!BuildModels())
        {
            Debug.LogWarning("[GpuGrassCarpet] No grass meshes available; disabling.");
            enabled = false;
            yield break;
        }

        yield return StartCoroutine(Scatter());

        FinalizeBatches();
        _ready = true;

        if (logResults)
        {
            // matrices were moved into batches and cleared, so count the finalized batches.
            int total = 0;
            foreach (var d in _allDraws)
                if (d.batches != null)
                    foreach (var b in d.batches)
                        total += b.Length;
            Debug.Log($"[GpuGrassCarpet] Ready: {total} drawn instances across {_allDraws.Count} draw groups.");
        }
    }

    bool BuildModels()
    {
        var prefabs = ResolveGrassPrefabs();
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
                    _ownedMaterials.Add(instanced);

                    var draw = new SubMeshDraw
                    {
                        mesh = mesh,
                        subMesh = s,
                        material = instanced,
                        relMatrix = rel
                    };
                    model.draws.Add(draw);
                    _allDraws.Add(draw);
                }
            }

            if (model.draws.Count > 0)
                _models.Add(model);
        }

        return _models.Count > 0;
    }

    List<GameObject> ResolveGrassPrefabs()
    {
        if (grassPrefabs != null && grassPrefabs.Count > 0)
            return grassPrefabs;

        var profile = Resources.Load<FoliageSpawnProfile>("RichPlanetFlora");
        if (profile == null || profile.spawnRules == null)
            return null;

        foreach (var rule in profile.spawnRules)
        {
            if (rule != null && rule.name == "Meadow Grass" && rule.prefabs != null && rule.prefabs.Count > 0)
                return rule.prefabs;
        }

        return null;
    }

    // The colour the green test runs against, chosen by greenSource. PaintedSurface is the exact
    // colour the planet shader draws on the ground, so grass tracks what the player actually sees.
    Color SampleGreenColor(Planet planet, Vector3 pos)
    {
        switch (greenSource)
        {
            case GreenColorSource.BiomeGradient:
                return planet.GetBiomeGradientColorAtPosition(pos, grassBiomeIndex);
            case GreenColorSource.BlendedBiome:
                return planet.GetBiomeColorAtPosition(pos);
            case GreenColorSource.PaintedSurface:
                return planet.GetSurfaceColorAtPosition(pos);
            default: // SurfaceGradientKey — the crisp terrain-band the gradient math picks here
                return planet.GetSurfaceKeyColorAtPosition(pos);
        }
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

        // Spatial grid so no two blades occupy the same cell -> no stacking, even coverage.
        float cellSize = Mathf.Max(0.05f, minSpacing);
        float invCell = 1f / cellSize;
        var occupied = new HashSet<Vector3Int>();

        int placed = 0;
        int budget = 0;
        // This is a mostly-ocean planet, so ~3 of every 4 random rays land in water and are wasted.
        // Give the scatter a large attempt budget so green LAND actually fills instead of starving
        // when the water rejections burn through the attempts first.
        int maxAttempts = count * 20 + 8000;

        // Rejection telemetry so we can see exactly which filter is starving coverage.
        int rejNoHit = 0, rejUnderBase = 0, rejWater = 0, rejSlope = 0, rejElev = 0, rejGreen = 0, rejBiome = 0, rejSpacing = 0, rejDensity = 0;

        for (int attempt = 0; attempt < maxAttempts && placed < count; attempt++)
        {
            if (++budget >= scatterPerFrame)
            {
                budget = 0;
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
            if (Vector3.Angle(hit.normal, radial) > maxSlope)
            { rejSlope++; continue; }

            float elevationNorm = _planet.GetNormalizedElevationAtPosition(pos);
            if (elevationNorm < minElevation || elevationNorm > maxElevation)
            { rejElev++; continue; }

            // Green test. Preferred: snap the final painted colour to its nearest gradient SHADE and
            // grass only the green shades (matches what the eye calls green, no threshold tuning).
            // Fallback: raw RGB margins on the sampled colour.
            bool isGreen;
            if (useNearestGreenShade)
            {
                // Grass rule = biome `grassBiomeIndex` (element 0, the grass biome) gradient, by
                // elevation, IGNORING latitude. A point is grassed wherever element 0's gradient reads
                // green at that elevation — exactly the green colour-keys in the gradient editor —
                // even at desert/snow latitudes where the blended surface paints tan/white.
                isGreen = _planet.IsSurfaceGreenAtPosition(pos, grassBiomeIndex);
            }
            else
            {
                Color c = SampleGreenColor(_planet, pos);
                isGreen = c.g >= minGreenChannel &&
                          c.g - c.r >= minGreenOverRed &&
                          c.g - c.b >= minGreenOverBlue;
            }
            if (!isGreen)
            { rejGreen++; continue; }

            // Density falloff: thin the grass by how green the ground is so it's dense in the greenest
            // core and gradually sparse toward the boundary. Probabilistic keep — the spacing grid
            // below still prevents stacking, so this just lowers local density where green is weaker.
            if (densityByGreenness && useNearestGreenShade)
            {
                float greenness = _planet.GetSurfaceGreennessAtPosition(pos, grassBiomeIndex);
                float keepProb = Mathf.Lerp(edgeDensity, 1f, Mathf.Pow(greenness, densityFalloff));
                if (Random.value > keepProb)
                { rejDensity++; continue; }
            }

            // Restrict to the grass biome's latitudes (1 = off).
            if (grassBiomeMaxPercent < 0.999f &&
                _planet.GetBiomePercentAtPosition(pos) > grassBiomeMaxPercent)
            { rejBiome++; continue; }

            var cell = new Vector3Int(
                Mathf.FloorToInt(pos.x * invCell),
                Mathf.FloorToInt(pos.y * invCell),
                Mathf.FloorToInt(pos.z * invCell));
            if (!occupied.Add(cell))
            { rejSpacing++; continue; }

            // Flat clump: align local +Y to the surface normal, random yaw around it.
            Quaternion rot = Quaternion.FromToRotation(Vector3.up, hit.normal)
                             * Quaternion.AngleAxis(Random.value * 360f, Vector3.up);
            float scale = Random.Range(scaleMin, scaleMax);
            Matrix4x4 baseTRS = Matrix4x4.TRS(pos + hit.normal * surfaceOffset, rot, Vector3.one * scale);

            var model = _models[Random.Range(0, _models.Count)];
            for (int i = 0; i < model.draws.Count; i++)
            {
                var d = model.draws[i];
                d.matrices.Add(baseTRS * d.relMatrix);
            }

            placed++;
        }

        if (logResults)
        {
            Debug.Log($"[GpuGrassCarpet] Scatter done: placed {placed}/{count}. Band [{minElevation:F3}..{maxElevation:F3}], maxSlope {maxSlope}, source {greenSource}.\n" +
                      $"  Rejected -> green(colour): {rejGreen}, density-thin: {rejDensity}, slope: {rejSlope}, elevation: {rejElev}, water: {rejWater}, spacing/dupe: {rejSpacing}, biome: {rejBiome}, underBase: {rejUnderBase}, noHit: {rejNoHit}");
        }
    }

    void FinalizeBatches()
    {
        foreach (var d in _allDraws)
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

    void Update()
    {
        if (!_ready)
            return;

        for (int i = 0; i < _allDraws.Count; i++)
        {
            var d = _allDraws[i];
            if (d.batches == null)
                continue;

            for (int b = 0; b < d.batches.Length; b++)
            {
                var batch = d.batches[b];
                Graphics.DrawMeshInstanced(
                    d.mesh, d.subMesh, d.material,
                    batch, batch.Length, null,
                    shadowCasting, receiveShadows, _layer);
            }
        }
    }

    [ContextMenu("Diagnose Probe Position")]
    void DiagnoseProbePosition()
    {
        var planet = Object.FindFirstObjectByType<Planet>();
        if (planet == null)
        {
            Debug.LogWarning("[GpuGrassCarpet] Diagnose: no Planet found.");
            return;
        }

        // In edit mode the planet hasn't generated (no shapeGenerator.elevationMinMax / colour
        // textures), so build it first — otherwise every elevation/colour query is null/garbage.
        if (!planet.IsGenerated)
            planet.GeneratePlanet();

        Vector3 pos = debugProbePosition;
        Vector3 center = planet.transform.position;
        Vector3 radial = (pos - center).normalized;
        float dist = (pos - center).magnitude;
        float waterRadius = planet.GetWaterRadiusWorld();
        float elevNorm = planet.GetNormalizedElevationAtPosition(pos);
        float biomePercent = planet.GetBiomePercentAtPosition(pos);
        Color blended = planet.GetBiomeColorAtPosition(pos);
        Color grad0 = planet.GetBiomeGradientColorAtPosition(pos, grassBiomeIndex);
        Color painted = planet.GetSurfaceColorAtPosition(pos);
        planet.ClassifySurfaceAtPosition(pos, out int clsBiome, out int clsKey, out Color clsKeyColor);
        bool haveBand = planet.TryGetGrassElevationBand(grassBiomeIndex, minGreenChannel, minGreenOverRed,
            minGreenOverBlue, out float bandMin, out float bandMax);

        float maxRadius = planet.shapeSettings != null ? planet.GetMaxSurfaceRadiusWorld() : 400f;
        var mask = LayerMask.GetMask("Default", "Ground");
        if (mask == 0) mask = ~0;
        float slope = -1f;
        if (Physics.Raycast(center + radial * (maxRadius + rayHeightAboveSurface), -radial,
                out var hit, maxRadius + rayHeightAboveSurface * 3f, mask))
            slope = Vector3.Angle(hit.normal, radial);

        Color c = SampleGreenColor(planet, pos);
        // Mirror the EXACT test Scatter() runs: with nearest-green-shade ON the rule is element-0's
        // gradient by elevation (latitude-independent); otherwise the raw RGB margins.
        bool greenOk = useNearestGreenShade
            ? planet.IsSurfaceGreenAtPosition(pos, grassBiomeIndex)
            : (c.g >= minGreenChannel && c.g - c.r >= minGreenOverRed && c.g - c.b >= minGreenOverBlue);
        bool biomeOk = !(grassBiomeMaxPercent < 0.999f && biomePercent > grassBiomeMaxPercent);
        bool slopeOk = slope < 0f || slope <= maxSlope;
        bool elevOk = elevNorm >= minElevation && elevNorm <= maxElevation;
        bool waterOk = !(excludeUnderwater && waterRadius > 0f && dist < waterRadius + 0.2f);
        bool spawn = greenOk && biomeOk && slopeOk && elevOk && waterOk;

        Debug.Log(
            $"[GpuGrassCarpet] Probe {pos}\n" +
            $"  surface band  = biome {clsBiome}, key {clsKey}, RGB ({clsKeyColor.r:F2},{clsKeyColor.g:F2},{clsKeyColor.b:F2})  <- the terrain-type the gradient math picks\n" +
            $"  green-key band= {(haveBand ? $"[{bandMin:F3}..{bandMax:F3}] (gradient slider positions)" : "none found")}\n" +
            $"  painted RGB   = ({painted.r:F2},{painted.g:F2},{painted.b:F2})  <- blended colour the shader draws\n" +
            $"  blended RGB   = ({blended.r:F2},{blended.g:F2},{blended.b:F2})\n" +
            $"  biome0 grad   = ({grad0.r:F2},{grad0.g:F2},{grad0.b:F2})\n" +
            $"  GREEN (rule={(useNearestGreenShade ? $"element{grassBiomeIndex} gradient by elevation" : $"RGB on {greenSource}")}) -> {(greenOk ? "OK" : "FAIL")}\n" +
            $"  greenness     = {planet.GetSurfaceGreennessAtPosition(pos, grassBiomeIndex):F2} (1=densest, 0=boundary) -> keepProb ~{Mathf.Lerp(edgeDensity, 1f, Mathf.Pow(planet.GetSurfaceGreennessAtPosition(pos, grassBiomeIndex), densityFalloff)):F2}\n" +
            $"  biomePercent  = {biomePercent:F3} (max {grassBiomeMaxPercent}) -> {(biomeOk ? "OK" : "FAIL")}\n" +
            $"  elevation     = {elevNorm:F3} [{minElevation}-{maxElevation}] -> {(elevOk ? "OK" : "FAIL")}\n" +
            $"  slope         = {slope:F1} (max {maxSlope}) -> {(slopeOk ? "OK" : "FAIL")}\n" +
            $"  underwater    = dist {dist:F1} vs water {waterRadius:F1} -> {(waterOk ? "OK" : "FAIL")}\n" +
            $"  VERDICT: {(spawn ? "WOULD SPAWN" : "BLOCKED")}");
    }

    void OnDisable()
    {
        // Stop drawing if disabled at runtime.
        _ready = false;
    }

    void OnDestroy()
    {
        foreach (var m in _ownedMaterials)
        {
            if (m != null)
                Destroy(m);
        }
        _ownedMaterials.Clear();
    }
}
