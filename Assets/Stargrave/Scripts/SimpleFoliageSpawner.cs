using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns foliage across the planet surface with biome-aware rules, natural clustering, and patch density.
/// </summary>
public class SimpleFoliageSpawner : MonoBehaviour
{
    public enum BiomeColorMatch
    {
        [Tooltip("All green areas - light, dark, any green-dominated color")]
        GreenArea,
        [Tooltip("Dark green - forest, dense vegetation")]
        DarkGreen,
        [Tooltip("Light green - grassland, meadows")]
        LightGreen,
        [Tooltip("Brown/gray - rocks, dead vegetation")]
        BrownGray,
        [Tooltip("Sandy/yellow - beaches, desert")]
        Sandy,
        [Tooltip("Snow/rock - mountain tops")]
        SnowRock,
        [Tooltip("Custom RGB ranges")]
        Custom,
        [Tooltip("Spawn in any color")]
        Any
    }

    public enum SpawnDistribution
    {
        [Tooltip("Evenly scattered valid points across the planet")]
        Scattered,
        [Tooltip("Grouped into forest/meadow patches for thick environments")]
        Clustered,
        [Tooltip("Tight meadow fill — many small clusters, very dense ground cover")]
        MeadowFill
    }

    public enum FoliageOrientation
    {
        [Tooltip("Stand on the terrain hit normal — mushrooms, plant_flat patches")]
        AlignToSurface,
        [Tooltip("Local up points away from planet center — trees, flowers, palms")]
        AlignToPlanetCenter,
        [Tooltip("Grass/rocks flat; trees/flowers/bushes radial (recommended)")]
        AutoByPrefabName,
        [Tooltip("Lie in the terrain tangent plane — grass clumps, leaf litter")]
        LayFlatOnSurface
    }

    [System.Serializable]
    public class BiomeSpawnRule
    {
        [Tooltip("e.g. Spruce 1, Grass, Standard Rock")]
        public string name = "Asset";
        [Tooltip("One prefab per rule for per-asset control, or multiple for variety")]
        public List<GameObject> prefabs = new List<GameObject>();

        [Header("Count")]
        public int count = 100;
        [Range(0.1f, 3f)]
        public float densityMultiplier = 1f;
        public float minDistanceBetween = 2f;

        [Header("Distribution")]
        public SpawnDistribution distribution = SpawnDistribution.Scattered;
        [Tooltip("Number of patch centers to find before spawning (Clustered / MeadowFill).")]
        public int clusterCount = 48;
        [Tooltip("Radius around each cluster center on the local tangent plane.")]
        public float clusterRadius = 14f;
        [Tooltip("Minimum separation between cluster centers on the surface.")]
        public float clusterMinSeparation = 22f;
        [Range(0f, 1f)]
        [Tooltip("Share of spawns that use cluster placement instead of scattered fallback.")]
        public float clusterSpawnWeight = 0.92f;
        [Tooltip("Save successful spawns as cluster anchors for later rules (e.g. forest trees).")]
        public bool registerClusterCenters;
        [Tooltip("Prefer spawning near cluster anchors registered by earlier rules.")]
        public bool spawnNearRegisteredClusters;
        public float nearClusterRadius = 11f;
        [Range(0f, 1f)]
        public float nearClusterWeight = 0.88f;

        [Header("Biome Colors (spawns if ANY match)")]
        public List<BiomeColorMatch> colorMatches = new List<BiomeColorMatch> { BiomeColorMatch.GreenArea };
        [Range(0f, 1f)] public float minGreenness = 0.2f;

        [Header("Custom RGB (when Custom is in Color Matches)")]
        [Range(0f, 1f)] public float minR, maxR = 1f;
        [Range(0f, 1f)] public float minG, maxG = 1f;
        [Range(0f, 1f)] public float minB, maxB = 1f;

        [Header("Elevation")]
        [Range(0f, 1f)] public float minElevation = 0f;
        [Range(0f, 1f)] public float maxElevation = 1f;

        [Header("Other")]
        [Range(0f, 90f)] public float maxSlope = 50f;
        [Tooltip("Use greenness as spawn probability within valid areas")]
        public bool useGreennessProbability = true;
        [Tooltip("How the instance is oriented on the planet surface.")]
        public FoliageOrientation orientation = FoliageOrientation.AutoByPrefabName;
        [Tooltip("Legacy fallback when orientation is Auto and prefab name is unknown.")]
        public bool alignToPlanetCenter = false;
        [Range(0f, 360f)]
        public float randomYawDegrees = 360f;
        public bool doubleSidedMaterials = false;
        public bool forceHighestLOD = false;
        [Range(0.5f, 2f)]
        public float scaleMin = 0.85f;
        [Range(0.5f, 2f)]
        public float scaleMax = 1.15f;

        [Header("Burst Clumps")]
        [Tooltip("Spawn this many instances per accepted anchor (thick ground cover).")]
        public int spawnBurstMin = 1;
        public int spawnBurstMax = 1;
        [Tooltip("Tangent-plane radius for extra instances in a burst.")]
        public float burstRadius = 1.5f;
    }

    [Header("Profile")]
    [Tooltip("Optional layout asset. Applied on Start when enabled.")]
    public FoliageSpawnProfile profile;
    public bool useProfileWhenSet = true;
    [Tooltip("When no profile is set, load Stargrave/Resources/RichPlanetFlora if present.")]
    public bool loadProfileFromResources = true;
    [Header("Rules")]
    public List<BiomeSpawnRule> spawnRules = new List<BiomeSpawnRule>();

    [Header("Global")]
    public float spawnHeightAboveSurface = 80f;
    [Range(0.1f, 3f)]
    public float globalDensityMultiplier = 1f;
    [Tooltip("Skip positions below the planet water shell.")]
    public bool excludeUnderwater = true;
    [Tooltip("Minimum separation checked across all rules (prevents trees in bushes).")]
    [Min(0f)]
    public float globalMinSeparation = 0.35f;
    [Tooltip("Extra patchiness inside valid biomes (0 = off). Creates thick groves and open gaps.")]
    [Range(0f, 1f)]
    public float patchNoiseStrength = 0.55f;
    public bool forceDoubleSidedAll = false;
    public bool forceHighestLODAll = false;
    [Range(0f, 1f)]
    public float viewAngleMaxSmoothness = 0.08f;

    [Header("Debug")]
    public bool logResults = true;

    Planet _planet;
    readonly Dictionary<string, int> _spawnedByRule = new Dictionary<string, int>();
    readonly List<Vector3> _registeredClusterCenters = new List<Vector3>();
    readonly List<Vector3> _ruleClusterCenters = new List<Vector3>();
    readonly List<Vector3> _globalSpawnedPositions = new List<Vector3>();

    void ApplyProfileIfConfigured()
    {
        if (useProfileWhenSet && profile != null)
        {
            profile.ApplyTo(this);
            return;
        }

        if (!loadProfileFromResources)
            return;

        var resourceProfile = Resources.Load<FoliageSpawnProfile>("RichPlanetFlora");
        if (resourceProfile != null)
            resourceProfile.ApplyTo(this);
    }

    IEnumerator Start()
    {
        ApplyProfileIfConfigured();

        _planet = Object.FindFirstObjectByType<Planet>();
        if (_planet == null)
        {
            Debug.LogWarning("[SimpleFoliageSpawner] No Planet found.");
            yield break;
        }

        while (!_planet.IsGenerated)
            yield return null;
        yield return null;

        SpawnAll();
        if (logResults)
        {
            foreach (var kv in _spawnedByRule)
                Debug.Log($"[SimpleFoliageSpawner] {kv.Key}: {kv.Value}");
            LogOrientationValidation();
        }
    }

    [ContextMenu("Apply Profile")]
    public void ApplyProfileContext()
    {
        ApplyProfileIfConfigured();
    }

    [ContextMenu("Spawn All Now")]
    public void SpawnAllNow()
    {
        ApplyProfileIfConfigured();
        _planet = Object.FindFirstObjectByType<Planet>();
        if (_planet == null)
        {
            Debug.LogWarning("[SimpleFoliageSpawner] No Planet found.");
            return;
        }

        SpawnAll();
    }

    void SpawnAll()
    {
        _spawnedByRule.Clear();
        _registeredClusterCenters.Clear();
        _globalSpawnedPositions.Clear();

        foreach (var rule in spawnRules)
        {
            if (rule.prefabs == null || rule.prefabs.Count == 0)
                continue;

            var container = new GameObject(rule.name);
            container.transform.SetParent(transform);
            container.transform.localPosition = Vector3.zero;

            int targetCount = Mathf.Max(0, Mathf.RoundToInt(rule.count * rule.densityMultiplier * globalDensityMultiplier));
            int spawned = SpawnRule(rule, container.transform, targetCount);
            _spawnedByRule[rule.name] = spawned;
        }
    }

    int SpawnRule(BiomeSpawnRule rule, Transform container, int targetCount)
    {
        var planetCenter = _planet.transform.position;
        float baseRadiusWorld = (_planet.shapeSettings != null) ? _planet.GetBaseRadiusWorld() : 400f;
        float maxRadiusWorld = (_planet.shapeSettings != null) ? _planet.GetMaxSurfaceRadiusWorld() : baseRadiusWorld;
        if (maxRadiusWorld < baseRadiusWorld)
            maxRadiusWorld = baseRadiusWorld;

        var groundMask = LayerMask.GetMask("Default", "Ground");
        if (groundMask == 0)
            groundMask = ~0;

        _ruleClusterCenters.Clear();
        if (UsesClusters(rule))
            BuildClusterCenters(rule, planetCenter, maxRadiusWorld, groundMask);

        var spawnedPositions = new List<Vector3>();
        int done = 0;
        int maxAttempts = Mathf.Max(200, targetCount * 4);

        for (int i = 0; i < targetCount; i++)
        {
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (!TrySampleSpawnPoint(rule, planetCenter, maxRadiusWorld, groundMask, spawnedPositions, out Vector3 anchorPos, out Vector3 up, out Vector3 normal, out float spawnWeight))
                    continue;

                if (rule.useGreennessProbability && Random.value > spawnWeight)
                    continue;

                int burst = Mathf.Clamp(Random.Range(rule.spawnBurstMin, rule.spawnBurstMax + 1), 1, 24);
                int burstPlaced = 0;

                for (int b = 0; b < burst; b++)
                {
                    Vector3 pos = anchorPos;
                    Vector3 spawnUp = up;
                    Vector3 spawnNormal = normal;

                    if (b > 0)
                    {
                        if (!TryBurstOffset(rule, anchorPos, up, planetCenter, maxRadiusWorld, groundMask, spawnedPositions, out pos, out spawnUp, out spawnNormal))
                            continue;
                    }

                    if (!TryPlaceInstance(rule, container, spawnedPositions, pos, spawnUp, spawnNormal))
                        continue;

                    burstPlaced++;
                    done++;
                }

                if (burstPlaced > 0)
                    break;
            }
        }

        if (logResults && done < targetCount)
            Debug.LogWarning($"[SimpleFoliageSpawner] {rule.name}: placed {done}/{targetCount}.");

        return done;
    }

    bool TryPlaceInstance(
        BiomeSpawnRule rule,
        Transform container,
        List<Vector3> spawnedPositions,
        Vector3 pos,
        Vector3 up,
        Vector3 normal)
    {
        if (!HasSeparation(pos, rule.minDistanceBetween, spawnedPositions))
            return false;
        if (!HasSeparation(pos, globalMinSeparation, _globalSpawnedPositions))
            return false;

        var prefab = rule.prefabs[Random.Range(0, rule.prefabs.Count)];
#if UNITY_EDITOR
        var inst = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab, container);
#else
        var inst = Object.Instantiate(prefab, container);
#endif
        inst.transform.position = pos;
        inst.transform.rotation = ComputeSpawnRotation(prefab, rule, normal, up);
        inst.transform.localScale = Vector3.one * Random.Range(rule.scaleMin, rule.scaleMax);

        if (forceDoubleSidedAll || rule.doubleSidedMaterials)
            MakeRenderersDoubleSided(inst);
        if (forceHighestLODAll || rule.forceHighestLOD)
            ForceHighestLOD(inst);
        if (viewAngleMaxSmoothness < 1f)
            FixViewAngleShading(inst);

        spawnedPositions.Add(pos);
        _globalSpawnedPositions.Add(pos);
        if (rule.registerClusterCenters)
            _registeredClusterCenters.Add(pos);
        return true;
    }

    enum OrientationKind
    {
        PlanetRadial,
        SurfaceStand,
        LayFlatOnSurface
    }

    static Quaternion ComputeSpawnRotation(GameObject prefab, BiomeSpawnRule rule, Vector3 surfaceNormal, Vector3 planetRadialUp)
    {
        OrientationKind kind = ResolveOrientationKind(prefab, rule);
        float yaw = rule.randomYawDegrees > 0f ? Random.Range(0f, rule.randomYawDegrees) : 0f;

        switch (kind)
        {
            case OrientationKind.PlanetRadial:
                return ApplyYawAroundAxis(Quaternion.FromToRotation(Vector3.up, planetRadialUp), yaw);
            case OrientationKind.LayFlatOnSurface:
                return BuildLayFlatRotation(surfaceNormal, yaw);
            default:
                return ApplyYawAroundAxis(Quaternion.FromToRotation(Vector3.up, surfaceNormal), yaw);
        }
    }

    static OrientationKind ResolveOrientationKind(GameObject prefab, BiomeSpawnRule rule)
    {
        if (rule.orientation == FoliageOrientation.AlignToPlanetCenter)
            return OrientationKind.PlanetRadial;
        if (rule.orientation == FoliageOrientation.AlignToSurface)
            return OrientationKind.SurfaceStand;
        if (rule.orientation == FoliageOrientation.LayFlatOnSurface)
            return OrientationKind.LayFlatOnSurface;

        if (TryClassifyPrefabOrientation(prefab, out OrientationKind kind))
            return kind;

        return rule.alignToPlanetCenter ? OrientationKind.PlanetRadial : OrientationKind.SurfaceStand;
    }

    static Quaternion BuildLayFlatRotation(Vector3 surfaceNormal, float yawDegrees)
    {
        Vector3 normal = surfaceNormal.sqrMagnitude > 1e-8f ? surfaceNormal.normalized : Vector3.up;
        Quaternion standOnNormal = Quaternion.FromToRotation(Vector3.up, normal);
        // Authored Y-up grass stands after standOnNormal; tip 90° around local tangent so blades lie on the slope.
        Quaternion layFlat = standOnNormal * Quaternion.AngleAxis(90f, Vector3.right);
        if (yawDegrees > 0f)
            layFlat = Quaternion.AngleAxis(yawDegrees, normal) * layFlat;
        return layFlat;
    }

    static Quaternion ApplyYawAroundAxis(Quaternion baseRotation, float yawDegrees)
    {
        if (yawDegrees <= 0f)
            return baseRotation;
        return baseRotation * Quaternion.AngleAxis(yawDegrees, Vector3.up);
    }

    static bool TryClassifyPrefabOrientation(GameObject prefab, out OrientationKind kind)
    {
        kind = OrientationKind.SurfaceStand;
        if (prefab == null)
            return false;

        string n = prefab.name.ToLowerInvariant();

        if (ContainsAny(n, "plant_flat"))
        {
            kind = OrientationKind.SurfaceStand;
            return true;
        }

        if (ContainsAny(n, "grass", "grass_"))
        {
            // Kenney grass clumps are flat XZ meshes; align their +Y to the surface normal
            // so the clump lies parallel to the ground (not stood up on its edge).
            kind = OrientationKind.SurfaceStand;
            return true;
        }

        if (ContainsAny(n, "branch", "mushroom", "log", "rock", "stone", "cliff", "mountain", "tiny rock", "standard rock"))
        {
            kind = OrientationKind.LayFlatOnSurface;
            return true;
        }

        if (ContainsAny(n, "tree", "spruce", "flower", "palm", "bush", "plant_bush", "stump"))
        {
            kind = OrientationKind.PlanetRadial;
            return true;
        }

        return false;
    }

    static bool ContainsAny(string value, params string[] tokens)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (value.Contains(tokens[i]))
                return true;
        }

        return false;
    }

    void LogOrientationValidation()
    {
        if (_planet == null)
            return;

        Vector3 planetCenter = _planet.transform.position;
        ValidateRuleContainer("Meadow Grass", OrientationKind.SurfaceStand, planetCenter);
        ValidateRuleContainer("Forest Canopy", OrientationKind.PlanetRadial, planetCenter);
    }

    void ValidateRuleContainer(string containerName, OrientationKind expected, Vector3 planetCenter)
    {
        var container = transform.Find(containerName);
        if (container == null || container.childCount == 0)
        {
            Debug.LogWarning($"[SimpleFoliageSpawner] Orientation check skipped: no '{containerName}' instances.");
            return;
        }

        int sampleCount = Mathf.Min(8, container.childCount);
        int passCount = 0;
        float worstMetric = 0f;

        for (int i = 0; i < sampleCount; i++)
        {
            var child = container.GetChild(i * container.childCount / sampleCount);
            Vector3 radial = (child.position - planetCenter).normalized;
            Vector3 surfaceNormal = radial;
            if (Physics.Raycast(child.position + radial * 3f, -radial, out var hit, 12f))
                surfaceNormal = hit.normal;

            Vector3 instanceUp = child.up;
            float metric;
            switch (expected)
            {
                case OrientationKind.LayFlatOnSurface:
                    // Up should lie in the tangent plane (perpendicular to the surface normal).
                    metric = Mathf.Abs(Vector3.Dot(instanceUp, surfaceNormal));
                    break;
                case OrientationKind.SurfaceStand:
                    // Up should align with the surface normal.
                    metric = 1f - Mathf.Abs(Vector3.Dot(instanceUp, surfaceNormal));
                    break;
                default: // PlanetRadial
                    metric = 1f - Mathf.Abs(Vector3.Dot(instanceUp, radial));
                    break;
            }

            worstMetric = Mathf.Max(worstMetric, metric);
            if (metric < 0.15f)
                passCount++;
        }

        string verdict = passCount == sampleCount ? "PASS" : "CHECK";
        Debug.Log($"[SimpleFoliageSpawner] Orientation {verdict} '{containerName}' ({expected}): {passCount}/{sampleCount} within tolerance, worst metric {worstMetric:F3}");
    }

    static bool HasSeparation(Vector3 pos, float minDistance, List<Vector3> positions)
    {
        if (minDistance <= 0f)
            return true;

        float minDistSq = minDistance * minDistance;
        for (int i = 0; i < positions.Count; i++)
        {
            if ((positions[i] - pos).sqrMagnitude < minDistSq)
                return false;
        }

        return true;
    }

    bool TryBurstOffset(
        BiomeSpawnRule rule,
        Vector3 anchorPos,
        Vector3 up,
        Vector3 planetCenter,
        float maxRadiusWorld,
        LayerMask groundMask,
        List<Vector3> spawnedPositions,
        out Vector3 pos,
        out Vector3 normalUp,
        out Vector3 surfaceNormal)
    {
        pos = anchorPos;
        normalUp = up;
        surfaceNormal = up;

        if (!TryRayFromTangentOffset(anchorPos, up, rule.burstRadius * Mathf.Sqrt(Random.value), planetCenter, maxRadiusWorld, groundMask, out Vector3 rayStart, out Vector3 rayDir))
            return false;

        float rayLength = maxRadiusWorld + (spawnHeightAboveSurface * 3f);
        if (!Physics.Raycast(rayStart, rayDir, out var hit, rayLength, groundMask))
            return false;

        pos = hit.point;
        surfaceNormal = hit.normal;
        normalUp = (pos - planetCenter).normalized;

        if (!IsValidSpawnPosition(pos, surfaceNormal, planetCenter, rule, out _))
            return false;

        if (!HasSeparation(pos, rule.minDistanceBetween, spawnedPositions))
            return false;
        if (!HasSeparation(pos, globalMinSeparation, _globalSpawnedPositions))
            return false;

        return true;
    }

    static bool UsesClusters(BiomeSpawnRule rule)
    {
        return rule.distribution == SpawnDistribution.Clustered || rule.distribution == SpawnDistribution.MeadowFill;
    }

    void BuildClusterCenters(BiomeSpawnRule rule, Vector3 planetCenter, float maxRadiusWorld, LayerMask groundMask)
    {
        int targetClusters = rule.distribution == SpawnDistribution.MeadowFill
            ? Mathf.Max(rule.clusterCount, rule.count / 12)
            : rule.clusterCount;

        float separation = rule.distribution == SpawnDistribution.MeadowFill
            ? Mathf.Min(rule.clusterMinSeparation, rule.clusterRadius * 2.5f)
            : rule.clusterMinSeparation;

        int attempts = targetClusters * 80;
        for (int i = 0; i < attempts && _ruleClusterCenters.Count < targetClusters; i++)
        {
            var dir = Random.onUnitSphere;
            var rayStart = planetCenter + dir * (maxRadiusWorld + spawnHeightAboveSurface);
            float rayLength = maxRadiusWorld + (spawnHeightAboveSurface * 3f);
            if (!Physics.Raycast(rayStart, -dir, out var hit, rayLength, groundMask))
                continue;

            if (!IsValidSpawnPosition(hit.point, hit.normal, planetCenter, rule, out _))
                continue;

            bool tooClose = false;
            for (int c = 0; c < _ruleClusterCenters.Count; c++)
            {
                if (Vector3.Distance(_ruleClusterCenters[c], hit.point) < separation)
                {
                    tooClose = true;
                    break;
                }
            }

            if (!tooClose)
                _ruleClusterCenters.Add(hit.point);
        }
    }

    bool TrySampleSpawnPoint(
        BiomeSpawnRule rule,
        Vector3 planetCenter,
        float maxRadiusWorld,
        LayerMask groundMask,
        List<Vector3> spawnedPositions,
        out Vector3 pos,
        out Vector3 up,
        out Vector3 normal,
        out float spawnWeight)
    {
        pos = Vector3.zero;
        up = Vector3.up;
        normal = Vector3.up;
        spawnWeight = 0f;

        Vector3 rayStart;
        Vector3 rayDir;

        if (rule.spawnNearRegisteredClusters && _registeredClusterCenters.Count > 0 && Random.value <= rule.nearClusterWeight)
        {
            Vector3 anchor = _registeredClusterCenters[Random.Range(0, _registeredClusterCenters.Count)];
            up = (anchor - planetCenter).normalized;
            if (!TryRayFromTangentOffset(anchor, up, Random.Range(0f, rule.nearClusterRadius), planetCenter, maxRadiusWorld, groundMask, out rayStart, out rayDir))
                return false;
        }
        else if (UsesClusters(rule) && _ruleClusterCenters.Count > 0 && Random.value <= rule.clusterSpawnWeight)
        {
            Vector3 center = _ruleClusterCenters[Random.Range(0, _ruleClusterCenters.Count)];
            up = (center - planetCenter).normalized;
            float radius = rule.distribution == SpawnDistribution.MeadowFill
                ? rule.clusterRadius * Random.Range(0.2f, 1f)
                : rule.clusterRadius * Mathf.Sqrt(Random.value);
            if (!TryRayFromTangentOffset(center, up, radius, planetCenter, maxRadiusWorld, groundMask, out rayStart, out rayDir))
                return false;
        }
        else
        {
            var dir = Random.onUnitSphere;
            rayStart = planetCenter + dir * (maxRadiusWorld + spawnHeightAboveSurface);
            rayDir = -dir;
        }

        float rayLength = maxRadiusWorld + (spawnHeightAboveSurface * 3f);
        if (!Physics.Raycast(rayStart, rayDir, out var hit, rayLength, groundMask))
            return false;

        pos = hit.point;
        normal = hit.normal;
        up = (pos - planetCenter).normalized;

        if (!IsValidSpawnPosition(pos, normal, planetCenter, rule, out spawnWeight))
            return false;

        if (!HasSeparation(pos, rule.minDistanceBetween, spawnedPositions))
            return false;
        if (!HasSeparation(pos, globalMinSeparation, _globalSpawnedPositions))
            return false;

        return true;
    }

    bool TryRayFromTangentOffset(
        Vector3 surfaceCenter,
        Vector3 up,
        float tangentRadius,
        Vector3 planetCenter,
        float maxRadiusWorld,
        LayerMask groundMask,
        out Vector3 rayStart,
        out Vector3 rayDir)
    {
        Vector3 tangent = Vector3.Cross(up, Random.onUnitSphere);
        if (tangent.sqrMagnitude < 1e-6f)
            tangent = Vector3.Cross(up, Vector3.forward);
        tangent.Normalize();
        Vector3 bitangent = Vector3.Cross(up, tangent);
        Vector2 disk = Random.insideUnitCircle * tangentRadius;
        Vector3 offsetPoint = surfaceCenter + tangent * disk.x + bitangent * disk.y;
        Vector3 dir = (offsetPoint - planetCenter).normalized;
        rayStart = planetCenter + dir * (maxRadiusWorld + spawnHeightAboveSurface);
        rayDir = -dir;
        return true;
    }

    bool IsValidSpawnPosition(Vector3 pos, Vector3 normal, Vector3 planetCenter, BiomeSpawnRule rule, out float spawnWeight)
    {
        spawnWeight = 1f;

        float distFromCenter = (pos - planetCenter).magnitude;
        float baseRadiusWorld = (_planet.shapeSettings != null) ? _planet.GetBaseRadiusWorld() : 400f;
        if (distFromCenter < baseRadiusWorld - 1f)
            return false;

        if (excludeUnderwater && IsUnderwater(pos, planetCenter))
            return false;

        Vector3 up = (pos - planetCenter).normalized;
        if (Vector3.Angle(normal, up) > rule.maxSlope)
            return false;

        float elevationNorm = _planet.GetNormalizedElevationAtPosition(pos);
        float elevMin = Mathf.Min(rule.minElevation, rule.maxElevation);
        float elevMax = Mathf.Max(rule.minElevation, rule.maxElevation);
        if (elevationNorm < elevMin || elevationNorm > elevMax)
            return false;

        Color color = _planet.GetBiomeColorAtPosition(pos);
        if (!MatchesColorRule(color, rule))
            return false;

        float greenness = _planet.GetFoliageGreennessAtPosition(pos);
        spawnWeight = greenness;

        if (patchNoiseStrength > 0.01f && rule.useGreennessProbability)
        {
            Vector3 dir = (pos - planetCenter).normalized;
            float noise = Mathf.PerlinNoise(dir.x * 9.5f + 37f, dir.z * 9.5f + 91f);
            float patch = Mathf.Lerp(1f, noise, patchNoiseStrength);
            spawnWeight *= patch;
        }

        return true;
    }

    bool IsUnderwater(Vector3 pos, Vector3 planetCenter)
    {
        float waterRadius = _planet.GetWaterRadiusWorld();
        if (waterRadius <= 0f)
            return false;
        return (pos - planetCenter).magnitude < waterRadius + 0.2f;
    }

    static void MakeRenderersDoubleSided(GameObject instance)
    {
        foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.materials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null)
                    continue;
                if (mats[i].HasProperty("_Cull"))
                    mats[i].SetInt("_Cull", 0);
                else if (mats[i].HasProperty("_CullMode"))
                    mats[i].SetInt("_CullMode", 0);
            }
        }
    }

    static void ForceHighestLOD(GameObject instance)
    {
        foreach (var lod in instance.GetComponentsInChildren<LODGroup>(true))
            lod.ForceLOD(0);
    }

    void FixViewAngleShading(GameObject instance)
    {
        float maxSmooth = viewAngleMaxSmoothness;
        foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.materials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null)
                    continue;
                if (mats[i].HasProperty("_Smoothness"))
                    mats[i].SetFloat("_Smoothness", Mathf.Min(mats[i].GetFloat("_Smoothness"), maxSmooth));
                if (mats[i].HasProperty("_Glossiness"))
                    mats[i].SetFloat("_Glossiness", Mathf.Min(mats[i].GetFloat("_Glossiness"), maxSmooth));
                if (mats[i].HasProperty("_SmoothnessScale"))
                    mats[i].SetFloat("_SmoothnessScale", maxSmooth);
                if (mats[i].HasProperty("_GlossMapScale"))
                    mats[i].SetFloat("_GlossMapScale", Mathf.Min(mats[i].GetFloat("_GlossMapScale"), maxSmooth));
                if (mats[i].HasProperty("_Metallic"))
                    mats[i].SetFloat("_Metallic", Mathf.Min(mats[i].GetFloat("_Metallic"), maxSmooth));
                if (mats[i].HasProperty("_ClearCoatMask"))
                    mats[i].SetFloat("_ClearCoatMask", 0f);
                if (mats[i].HasProperty("_ClearCoatSmoothness"))
                    mats[i].SetFloat("_ClearCoatSmoothness", 0f);
            }
        }
    }

    bool MatchesColorRule(Color c, BiomeSpawnRule rule)
    {
        if (rule.colorMatches == null || rule.colorMatches.Count == 0)
            return true;

        foreach (var match in rule.colorMatches)
        {
            if (MatchesSingle(c, rule, match))
                return true;
        }

        return false;
    }

    bool MatchesSingle(Color c, BiomeSpawnRule rule, BiomeColorMatch match)
    {
        switch (match)
        {
            case BiomeColorMatch.Any:
                return true;

            case BiomeColorMatch.GreenArea:
                return c.g > c.r && c.g > c.b && c.g >= rule.minGreenness;

            case BiomeColorMatch.DarkGreen:
                return c.g > c.r && c.g > c.b && c.g >= 0.25f && c.g < 0.55f;

            case BiomeColorMatch.LightGreen:
                return c.g > c.r && c.g > c.b && c.g >= 0.45f;

            case BiomeColorMatch.BrownGray:
                {
                    float sat = Mathf.Max(c.r, c.g, c.b) - Mathf.Min(c.r, c.g, c.b);
                    return sat < 0.25f || (c.r > 0.3f && c.g > 0.25f && c.b < 0.4f && c.r >= c.g * 0.8f);
                }

            case BiomeColorMatch.Sandy:
                return c.r > 0.4f && c.g > 0.35f && c.b < c.r * 0.8f;

            case BiomeColorMatch.SnowRock:
                return (c.r + c.g + c.b) / 3f > 0.7f;

            case BiomeColorMatch.Custom:
                return c.r >= rule.minR && c.r <= rule.maxR &&
                       c.g >= rule.minG && c.g <= rule.maxG &&
                       c.b >= rule.minB && c.b <= rule.maxB;

            default:
                return true;
        }
    }

    [ContextMenu("Clear All")]
    public void ClearAll()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            if (Application.isPlaying)
                Object.Destroy(transform.GetChild(i).gameObject);
            else
                Object.DestroyImmediate(transform.GetChild(i).gameObject);
        }

        _registeredClusterCenters.Clear();
        _globalSpawnedPositions.Clear();
        Debug.Log("[SimpleFoliageSpawner] Cleared.");
    }
}
