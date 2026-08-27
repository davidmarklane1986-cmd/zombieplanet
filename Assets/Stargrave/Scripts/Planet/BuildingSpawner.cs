using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public enum BuildingSizeClass
{
    Short = 0,
    Tall = 1
}

/// <summary>Weighted building mesh/prefab for <see cref="BuildingSpawner"/>.</summary>
[System.Serializable]
public class BuildingSpawnVariant
{
    public string name;
    public GameObject prefab;
    public BuildingSizeClass sizeClass = BuildingSizeClass.Short;
    [Min(0f)] public float weight = 1f;
    [Tooltip("Uniform local scale applied to the instance (1 = authored size).")]
    [Min(0.01f)] public float scale = 1f;
}

/// <summary>
/// Places small settlements on dry, flatish land. Tall buildings only appear with short neighbours;
/// if a tall cannot be surrounded, a short is used instead. Flat-area size decides: one short,
/// several shorts, or shorts + one tall.
/// </summary>
[DisallowMultipleComponent]
public sealed class BuildingSpawner : MonoBehaviour
{
    public static BuildingSpawner Instance { get; private set; }

    [Header("Prefabs")]
    [Tooltip("Weighted building prefabs. Mark TwistedTower / high-rises as Tall.")]
    public List<BuildingSpawnVariant> variants = new List<BuildingSpawnVariant>();

    [Header("Settlements")]
    [Tooltip("How many separate towns / clusters to place.")]
    [Min(0)] public int targetSettlements = 8;
    [Tooltip("World-unit minimum spacing between settlement centers.")]
    [Min(20f)] public float settlementSpacing = 160f;
    [Tooltip("Center-to-center lot pitch inside a block (keep above shortMaxFootprint).")]
    [Min(4f)] public float buildingSpacing = 7f;
    [Tooltip("Width of the cross streets between facing blocks.")]
    [Min(3f)] public float streetWidth = 4f;
    [Tooltip("Minimum short buildings required around a tall one.")]
    [Min(2)] public int minShortsAroundTall = 4;
    [Tooltip("Never accept a settlement with fewer buildings than this (rejects loners).")]
    [Min(3)] public int minBuildingsPerSettlement = 4;
    [Tooltip("Max short buildings in a short-only town.")]
    [Min(1)] public int maxShortsPerSettlement = 20;
    [Tooltip("Max short buildings accompanying one tall.")]
    [Min(2)] public int maxShortsWithTall = 20;
    [Min(8)] public int maxAttempts = 360;
    public bool spawnOnPlanetReady = true;
    public bool clearBeforeSpawn = true;
    public bool regeneratePlanetAfterSpawn = true;
    public bool spreadSearchAcrossFrames = true;
    [Min(1)] public int attemptsPerFrame = 6;

    [Header("Pad / site")]
    [Min(0.5f)] public float shortFlatRadius = 4.5f;
    [Min(0.5f)] public float tallFlatRadius = 12f;
    [Min(0.5f)] public float blendWidth = 12f;
    [Min(0f)] public float dryClearance = 1.25f;
    [Range(1f, 45f)] public float maxSlopeDegrees = 14f;
    [Min(0.1f)] public float maxHeightVariation = 3.5f;
    [Min(4)] public int siteSampleCount = 6;
    [Min(4)] public int siteSearchAttemptsPerTry = 16;
    [Tooltip("How far (world units) we probe when measuring contiguous flat land.")]
    [Min(10f)] public float maxFlatProbeRadius = 80f;
    [Min(2f)] public float flatProbeStep = 6f;

    [Header("Instance")]
    [Tooltip("If true, only used when a plot has no street-facing yaw (avoid for towns).")]
    public bool randomYaw = false;
    public string spawnedRootName = "SpawnedBuildings";
    [Tooltip("Uniformly scale each prefab so its mesh height matches the short/tall target.")]
    public bool autoScaleToTargetHeight = true;
    [Tooltip("Target height for Short (single-storey) buildings, world units along building up.")]
    [Min(1f)] public float shortTargetHeight = 10f;
    [Tooltip("Target height for Tall (multi-storey) buildings.")]
    [Min(1f)] public float tallTargetHeight = 32f;
    [Tooltip("After height fit, cap Short footprint (max of width/depth).")]
    [Min(1f)] public float shortMaxFootprint = 7f;
    [Tooltip("After height fit, cap Tall footprint.")]
    [Min(1f)] public float tallMaxFootprint = 22f;
    [Min(0.05f)] public float minAutoScale = 0.15f;
    [Min(0.05f)] public float maxAutoScale = 80f;
    [Tooltip("Add MeshColliders when a prefab has no solid collider (Kenny FBX samples are often visual-only).")]
    public bool ensureSolidColliders = true;

    // Legacy serialized fields (kept so older scenes don't lose data silently).
    [HideInInspector] public int targetCount = 10;
    [HideInInspector] public float minSpacing = 140f;
    [HideInInspector] public float flatRadius = 10f;

    Transform _spawnRoot;
    readonly List<BuildingPad> _spawnedPads = new List<BuildingPad>(64);
    bool _subscribed;
    bool _spawnedThisSession;
    bool _isSpawning;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        MigrateLegacyFields();
#if UNITY_EDITOR
        TryAssignDefaultVariants();
#endif
    }

    void MigrateLegacyFields()
    {
        // Older scenes only had targetCount / minSpacing / flatRadius.
        if (targetSettlements <= 0 && targetCount > 0)
            targetSettlements = Mathf.Max(1, targetCount / 3);
        if (settlementSpacing < 20f && minSpacing > 20f)
            settlementSpacing = minSpacing;
        if (shortFlatRadius <= 0.5f && flatRadius > 0.5f)
            shortFlatRadius = flatRadius;
        // Prefer real street towns over loners; one cross-street ring is 4 lots.
        if (minBuildingsPerSettlement != 4)
            minBuildingsPerSettlement = 4;
        // Compact small-town lot pitch (~half prior spacing so ring-2 towns fit in the same shelf).
        if (buildingSpacing < 5f || buildingSpacing > 9f)
            buildingSpacing = 7f;
        if (streetWidth < 3f || streetWidth > 6f)
            streetWidth = 4f;
        if (maxFlatProbeRadius < 50f || maxFlatProbeRadius > 100f)
            maxFlatProbeRadius = 80f;
        if (flatProbeStep < 4f || flatProbeStep > 10f)
            flatProbeStep = 6f;
        if (maxShortsPerSettlement < 12 || maxShortsPerSettlement > 28)
            maxShortsPerSettlement = 20;
        if (maxShortsWithTall < 12 || maxShortsWithTall > 28)
            maxShortsWithTall = 20;
        if (shortMaxFootprint < 5f || shortMaxFootprint > 9f)
            shortMaxFootprint = 7f;
        if (shortFlatRadius < 3.5f || shortFlatRadius > 6f)
            shortFlatRadius = 4.5f;
        if (minShortsAroundTall < 4)
            minShortsAroundTall = 4;
        if (minShortsAroundTall > 8)
            minShortsAroundTall = 4;
        if (maxAttempts < 400)
            maxAttempts = 480;
        if (targetSettlements < 6)
            targetSettlements = 8;
        if (siteSearchAttemptsPerTry < 16)
            siteSearchAttemptsPerTry = 24;
        randomYaw = false;
    }

#if UNITY_EDITOR
    void Reset()
    {
        TryAssignDefaultVariants();
    }

    /// <summary>
    /// Multi-storey city blocks / towers = Tall. Single-storey Kenny houses = Short.
    /// </summary>
    public static BuildingSizeClass InferSizeClass(string prefabName)
    {
        if (string.IsNullOrEmpty(prefabName))
            return BuildingSizeClass.Short;

        // Explicit single-storey / house kits.
        if (prefabName.IndexOf("house", System.StringComparison.OrdinalIgnoreCase) >= 0
            || prefabName.IndexOf("building-type-", System.StringComparison.OrdinalIgnoreCase) >= 0
            || prefabName.IndexOf("cottage", System.StringComparison.OrdinalIgnoreCase) >= 0
            || prefabName.IndexOf("cabin", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return BuildingSizeClass.Short;

        // Multi-storey / towers / eco blocks.
        if (prefabName.IndexOf("Tower", System.StringComparison.OrdinalIgnoreCase) >= 0
            || prefabName.IndexOf("Twisted", System.StringComparison.OrdinalIgnoreCase) >= 0
            || prefabName.IndexOf("Eco_Building", System.StringComparison.OrdinalIgnoreCase) >= 0
            || prefabName.IndexOf("Regular_Building", System.StringComparison.OrdinalIgnoreCase) >= 0
            || prefabName.IndexOf("storey", System.StringComparison.OrdinalIgnoreCase) >= 0
            || prefabName.IndexOf("story", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return BuildingSizeClass.Tall;

        return BuildingSizeClass.Short;
    }

    public void TryAssignDefaultVariants()
    {
        if (variants == null)
            variants = new List<BuildingSpawnVariant>();

        if (variants.Count == 0)
        {
            // Shorts: Kenny single-storey sample houses.
            AddDefault("Assets/ThirdParty/Kenny/ModularBuildingsKit/Models/FBX format/building-sample-house-a.fbx", BuildingSizeClass.Short, 1f);
            AddDefault("Assets/ThirdParty/Kenny/ModularBuildingsKit/Models/FBX format/building-sample-house-b.fbx", BuildingSizeClass.Short, 1f);
            AddDefault("Assets/ThirdParty/Kenny/ModularBuildingsKit/Models/FBX format/building-sample-house-c.fbx", BuildingSizeClass.Short, 1f);
            // Talls: multi-storey ithappy blocks + tower.
            AddDefault("Assets/ithappy/Cartoon_City_Free/Prefabs/Buildings/Eco_Building_Grid.prefab", BuildingSizeClass.Tall, 1f);
            AddDefault("Assets/ithappy/Cartoon_City_Free/Prefabs/Buildings/Eco_Building_Terrace.prefab", BuildingSizeClass.Tall, 1f);
            AddDefault("Assets/ithappy/Cartoon_City_Free/Prefabs/Buildings/Eco_Building_Slope.prefab", BuildingSizeClass.Tall, 1f);
            AddDefault("Assets/ithappy/Cartoon_City_Free/Prefabs/Buildings/Regular_Building_TwistedTower_Large.prefab", BuildingSizeClass.Tall, 1f);
        }
        else
        {
            for (int i = 0; i < variants.Count; i++)
            {
                BuildingSpawnVariant v = variants[i];
                if (v == null || v.prefab == null)
                    continue;
                v.sizeClass = InferSizeClass(v.prefab.name);
            }

            // If everything became Tall (old eco-only list), add Kenny houses as Shorts.
            bool hasShort = false;
            for (int i = 0; i < variants.Count; i++)
            {
                if (variants[i] != null && variants[i].prefab != null && variants[i].sizeClass == BuildingSizeClass.Short)
                {
                    hasShort = true;
                    break;
                }
            }
            if (!hasShort)
            {
                AddDefault("Assets/ThirdParty/Kenny/ModularBuildingsKit/Models/FBX format/building-sample-house-a.fbx", BuildingSizeClass.Short, 1f);
                AddDefault("Assets/ThirdParty/Kenny/ModularBuildingsKit/Models/FBX format/building-sample-house-b.fbx", BuildingSizeClass.Short, 1f);
                AddDefault("Assets/ThirdParty/Kenny/ModularBuildingsKit/Models/FBX format/building-sample-house-c.fbx", BuildingSizeClass.Short, 1f);
            }
        }

        if (variants.Count > 0)
            UnityEditor.EditorUtility.SetDirty(this);
    }

    void AddDefault(string path, BuildingSizeClass size, float weight)
    {
        var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null)
            return;
        variants.Add(new BuildingSpawnVariant
        {
            name = prefab.name,
            prefab = prefab,
            sizeClass = size,
            weight = weight,
            scale = 1f
        });
    }
#endif

    void OnEnable()
    {
        if (_subscribed)
            return;
        Planet.OnPlanetReady += OnPlanetReady;
        _subscribed = true;
    }

    void OnDisable()
    {
        if (!_subscribed)
            return;
        Planet.OnPlanetReady -= OnPlanetReady;
        _subscribed = false;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    void Start()
    {
        var planet = PlanetBuildingPads.FindPlanet();
        if (spawnOnPlanetReady && planet != null && planet.IsGenerated && !_spawnedThisSession && !_isSpawning)
            StartCoroutine(SpawnBurstNextFrame());
    }

    void OnPlanetReady()
    {
        if (!spawnOnPlanetReady || !isActiveAndEnabled || _spawnedThisSession || _isSpawning)
            return;
        StartCoroutine(SpawnBurstNextFrame());
    }

    IEnumerator SpawnBurstNextFrame()
    {
        yield return null;
        if (_spawnedThisSession || _isSpawning)
            yield break;

        if (spreadSearchAcrossFrames && Application.isPlaying)
            yield return StartCoroutine(SpawnSettlementsSpreadRoutine());
        else
            SpawnBuildings();
    }

    [ContextMenu("Spawn Buildings Now")]
    public void SpawnBuildings()
    {
        if (_isSpawning)
            return;

        MigrateLegacyFields();

        var planet = PlanetBuildingPads.FindPlanet();
        if (planet == null)
        {
            Debug.LogWarning("[BuildingSpawner] No Planet found.");
            return;
        }

        _isSpawning = true;
        try
        {
            if (!planet.IsGenerated)
                planet.GeneratePlanet();

            if (clearBeforeSpawn)
                ClearSpawned();

            // Drop destroyed pads from the elevation registry so new poses use clean terrain.
            PlanetBuildingPads.BakeFromScene(planet);

            EnsureSpawnRoot();
            SpawnSettlementBurstImmediate(planet);
        }
        finally
        {
            _isSpawning = false;
        }
    }

    IEnumerator SpawnSettlementsSpreadRoutine()
    {
        if (_isSpawning)
            yield break;

        var planet = PlanetBuildingPads.FindPlanet();
        if (planet == null)
            yield break;

        _isSpawning = true;
        try
        {
            MigrateLegacyFields();

            if (!planet.IsGenerated)
                planet.GeneratePlanet();

            if (clearBeforeSpawn)
                ClearSpawned();

            PlanetBuildingPads.BakeFromScene(planet);

            EnsureSpawnRoot();
            yield return SpawnSettlementBurstSpread(planet);
        }
        finally
        {
            _isSpawning = false;
        }
    }

    void SpawnSettlementBurstImmediate(Planet planet)
    {
        float townSpacing = Mathf.Max(20f, settlementSpacing);
        float invTown = 1f / townSpacing;
        var occupiedTowns = new HashSet<Vector3Int>();
        ReserveExistingPads(planet, occupiedTowns, invTown);

        int wantTowns = Mathf.Max(0, targetSettlements);
        int towns = 0;
        int buildings = 0;
        int attempts = Mathf.Max(wantTowns * 10, maxAttempts);
        Vector3 center = planet.transform.position;

        var gen = new ShapeGenerator();
        gen.UpdateSettings(planet.shapeSettings);
        float scale = PlanetBuildingPads.WorldScale(planet);

        for (int a = 0; a < attempts && towns < wantTowns; a++)
        {
            Vector3 guess = Random.onUnitSphere;
            int placedHere = TryPlaceSettlement(planet, gen, scale, center, guess, occupiedTowns, invTown);
            if (placedHere > 0)
            {
                towns++;
                buildings += placedHere;
            }
        }

        FinishSpawn(towns, wantTowns, buildings, townSpacing);
    }

    IEnumerator SpawnSettlementBurstSpread(Planet planet)
    {
        float townSpacing = Mathf.Max(20f, settlementSpacing);
        float invTown = 1f / townSpacing;
        var occupiedTowns = new HashSet<Vector3Int>();
        ReserveExistingPads(planet, occupiedTowns, invTown);

        int wantTowns = Mathf.Max(0, targetSettlements);
        int towns = 0;
        int buildings = 0;
        int attempts = Mathf.Max(wantTowns * 10, maxAttempts);
        int perFrame = Mathf.Max(1, attemptsPerFrame);
        Vector3 center = planet.transform.position;

        var gen = new ShapeGenerator();
        gen.UpdateSettings(planet.shapeSettings);
        float scale = PlanetBuildingPads.WorldScale(planet);

        for (int a = 0; a < attempts && towns < wantTowns; a++)
        {
            Vector3 guess = Random.onUnitSphere;
            int placedHere = TryPlaceSettlement(planet, gen, scale, center, guess, occupiedTowns, invTown);
            if (placedHere > 0)
            {
                towns++;
                buildings += placedHere;
            }

            if ((a % perFrame) == perFrame - 1)
                yield return null;
        }

        FinishSpawn(towns, wantTowns, buildings, townSpacing);
    }

    void FinishSpawn(int towns, int wantTowns, int buildings, float spacing)
    {
        _spawnedThisSession = true;
        Debug.Log($"[BuildingSpawner] Settlements {towns}/{wantTowns}, buildings {buildings} (town spacing {spacing:F0}).");

        var planet = PlanetBuildingPads.FindPlanet();
        if (buildings > 0 && regeneratePlanetAfterSpawn)
            PlanetBuildingPads.RegeneratePlanetWithPads();
        else if (buildings > 0)
            PlanetBuildingPads.BakeFromScene(planet);

        // Pads change terrain height — re-snap every building to the final surface with radial up.
        if (buildings > 0)
            ReseatAllSpawnedBuildings(planet);
    }

    [ContextMenu("Clear Spawned Buildings")]
    public void ClearSpawned()
    {
        for (int i = 0; i < _spawnedPads.Count; i++)
        {
            if (_spawnedPads[i] != null)
                DestroyImmediateSafe(_spawnedPads[i].gameObject);
        }
        _spawnedPads.Clear();

        // After domain reload `_spawnRoot` is null even though SpawnedBuildings still exists.
        if (_spawnRoot == null)
        {
            Transform existing = transform.Find(spawnedRootName);
            if (existing != null)
                _spawnRoot = existing;
        }

        if (_spawnRoot != null)
        {
            for (int i = _spawnRoot.childCount - 1; i >= 0; i--)
                DestroyImmediateSafe(_spawnRoot.GetChild(i).gameObject);
        }

        _spawnedThisSession = false;
    }

    /// <summary>
    /// Returns buildings placed (0 = failed).
    /// </summary>
    int TryPlaceSettlement(
        Planet planet,
        ShapeGenerator gen,
        float scale,
        Vector3 planetCenter,
        Vector3 preferredAxis,
        HashSet<Vector3Int> occupiedTowns,
        float invTown)
    {
        var hubSettings = MakeSettings(shortFlatRadius);
        hubSettings.searchAttempts = Mathf.Max(hubSettings.searchAttempts, 24);
        if (!BuildingPadSiteEvaluator.TryFindSuitableSite(
                planet, preferredAxis, hubSettings, out Vector3 hubAxis, out _))
            return 0;

        float hubR = gen.CalculateNaturalUnscaledElevation(hubAxis) * scale;
        Vector3 hubWorld = planetCenter + hubAxis * hubR;
        var townCell = WorldToCell(hubWorld, invTown);
        if (!TryClaimWithNeighbors(occupiedTowns, townCell))
            return 0;

        float pitch = LotPitch();
        float flatExtent = MeasureFlatExtent(planet, gen, scale, hubAxis, hubR);
        // Hub must be dry/flat; street lots can rely on pad flattening for local bumps.
        if (flatExtent < shortFlatRadius * 0.5f)
        {
            occupiedTowns.Remove(townCell);
            return 0;
        }

        int maxSlots = Mathf.Max(minBuildingsPerSettlement, EstimateShortSlots(flatExtent));
        int shortSlots = PickTownBuildingCount(maxSlots);
        bool wantTall = maxSlots >= (minShortsAroundTall + 1)
                        && flatExtent >= tallFlatRadius + pitch
                        && HasVariant(BuildingSizeClass.Tall)
                        && HasVariant(BuildingSizeClass.Short)
                        && Random.value < 0.45f;

        int placed = 0;
        if (wantTall)
        {
            int tallStart = _spawnedPads.Count;
            placed = TryPlaceTallTown(planet, gen, scale, planetCenter, hubAxis, flatExtent);
            if (placed < minBuildingsPerSettlement)
            {
                UndoSpawnedFrom(tallStart);
                placed = TryPlaceShortTown(planet, gen, scale, planetCenter, hubAxis, flatExtent, shortSlots);
            }
        }
        else
        {
            placed = TryPlaceShortTown(planet, gen, scale, planetCenter, hubAxis, flatExtent, shortSlots);
        }

        if (placed < minBuildingsPerSettlement)
        {
            // Reject loners / incomplete clusters — keep searching for a real town site.
            if (placed > 0)
                UndoSpawnedFrom(_spawnedPads.Count - placed);
            occupiedTowns.Remove(townCell);
            return 0;
        }

        return placed;
    }

    int TryPlaceTallTown(
        Planet planet,
        ShapeGenerator gen,
        float scale,
        Vector3 planetCenter,
        Vector3 hubAxis,
        float flatExtent)
    {
        // Tall needs its own pad suitability at hub.
        var tallSettings = MakeSettings(tallFlatRadius);
        var tallReport = BuildingPadSiteEvaluator.Evaluate(planet, gen, hubAxis, tallSettings);
        if (!tallReport.isValid)
            return 0;

        int wantShorts = PickTownBuildingCount(Mathf.Clamp(
            EstimateShortSlots(Mathf.Max(0f, flatExtent - tallFlatRadius * 0.35f)),
            minShortsAroundTall,
            maxShortsWithTall));

        float pitch = LotPitch();
        int padStart = _spawnedPads.Count;
        int shortPlaced = 0;
        float streetYawRad = 0f;
        // Retry a few street orientations so one rocky corner doesn't kill a good shelf.
        for (int attempt = 0; attempt < 4 && shortPlaced < minShortsAroundTall; attempt++)
        {
            if (shortPlaced > 0)
                UndoSpawnedFrom(padStart);
            var plots = new List<TownPlot>(Mathf.Max(wantShorts, 16) + 8);
            streetYawRad = CollectTownStreetPlots(
                planet, gen, scale, hubAxis, pitch, Mathf.Max(wantShorts, 16) + 8, plots,
                skipHub: true, forcedStreetYaw: attempt == 0 ? (float?)null : attempt * (Mathf.PI * 0.25f));
            shortPlaced = PlaceCompactTownLots(planet, gen, plots, wantShorts, padStart);
        }
        if (shortPlaced < minShortsAroundTall)
        {
            UndoSpawnedFrom(padStart);
            return 0;
        }

        // Face the tower down the main street so the plaza reads as intentional.
        float tallYaw = YawToward(hubAxis, StreetAlong(hubAxis, streetYawRad));
        if (!TrySpawnBuilding(planet, hubAxis, BuildingSizeClass.Tall, tallFlatRadius, tallYaw))
            return shortPlaced;

        return shortPlaced + 1;
    }

    void UndoSpawnedFrom(int startIndex)
    {
        for (int i = _spawnedPads.Count - 1; i >= startIndex; i--)
        {
            BuildingPad pad = _spawnedPads[i];
            _spawnedPads.RemoveAt(i);
            if (pad != null)
                DestroyImmediateSafe(pad.gameObject);
        }
    }

    int TryPlaceShortTown(
        Planet planet,
        ShapeGenerator gen,
        float scale,
        Vector3 planetCenter,
        Vector3 hubAxis,
        float flatExtent,
        int shortSlots)
    {
        int maxSlots = Mathf.Clamp(
            Mathf.Max(minBuildingsPerSettlement, shortSlots),
            minBuildingsPerSettlement,
            maxShortsPerSettlement);
        int want = PickTownBuildingCount(maxSlots);

        float pitch = LotPitch();
        int padStart = _spawnedPads.Count;
        int placed = 0;
        int plotCapacity = Mathf.Max(want, EstimateShortSlots(flatExtent), 16) + 12;
        for (int attempt = 0; attempt < 4 && placed < minBuildingsPerSettlement; attempt++)
        {
            if (placed > 0)
                UndoSpawnedFrom(padStart);
            var plots = new List<TownPlot>(plotCapacity);
            CollectTownStreetPlots(
                planet, gen, scale, hubAxis, pitch, plotCapacity, plots,
                skipHub: true, forcedStreetYaw: attempt == 0 ? (float?)null : attempt * (Mathf.PI * 0.25f));
            placed = PlaceCompactTownLots(planet, gen, plots, want, padStart);
        }
        if (placed < minBuildingsPerSettlement)
        {
            UndoSpawnedFrom(padStart);
            return 0;
        }

        return placed;
    }

    /// <summary>
    /// Random town size on the street grid — small hamlets through denser blocks.
    /// </summary>
    int PickTownBuildingCount(int maxSlots)
    {
        int lo = minBuildingsPerSettlement;
        int hi = Mathf.Max(lo, maxSlots);
        // Bias toward medium towns, but still allow sparse and dense rolls.
        float t = Mathf.Pow(Random.value, 0.65f);
        return Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(lo, hi, t)), lo, hi);
    }

    /// <summary>
    /// Fill a street-grid subset: lots stay on the cross/block grid, but occupancy is uneven
    /// so towns are not always symmetrical rings.
    /// </summary>
    int PlaceCompactTownLots(
        Planet planet,
        ShapeGenerator gen,
        List<TownPlot> plots,
        int want,
        int padStart)
    {
        if (plots == null || plots.Count == 0 || want <= 0)
            return 0;

        plots.Sort((a, b) => a.ring.CompareTo(b.ring));
        ShufflePlotsWithinRings(plots);

        var shortSettings = MakeSettings(Mathf.Max(3f, shortFlatRadius * 0.45f));
        shortSettings.maxSlopeDegrees = Mathf.Max(shortSettings.maxSlopeDegrees, 22f);
        shortSettings.maxHeightVariation = Mathf.Max(shortSettings.maxHeightVariation, 6.5f);
        float minSep = Mathf.Max(LotPitch() * 0.5f, shortMaxFootprint * 0.55f);
        int placed = 0;

        for (int p = 0; p < plots.Count && placed < want; p++)
        {
            TownPlot plot = plots[p];
            if (!IsPlotBuildable(planet, gen, plot.axis, shortSettings))
                continue;
            if (!IsFarEnoughFromSpawned(planet, plot.axis, minSep, padStart))
                continue;

            // Outer rings leave more empty lots; inner ring stays denser.
            float fillChance = plot.ring <= 1 ? 0.92f : (plot.ring == 2 ? 0.62f : 0.4f);
            if (placed >= minBuildingsPerSettlement && Random.value > fillChance)
                continue;

            if (TrySpawnBuilding(planet, plot.axis, BuildingSizeClass.Short, shortFlatRadius, plot.yawDegrees))
                placed++;
        }

        return placed;
    }

    static void ShufflePlotsWithinRings(List<TownPlot> plots)
    {
        if (plots == null || plots.Count < 2)
            return;

        int i = 0;
        while (i < plots.Count)
        {
            int ring = plots[i].ring;
            int start = i;
            while (i < plots.Count && plots[i].ring == ring)
                i++;
            for (int a = i - 1; a > start; a--)
            {
                int b = Random.Range(start, a + 1);
                TownPlot tmp = plots[a];
                plots[a] = plots[b];
                plots[b] = tmp;
            }
        }
    }

    bool IsPlotBuildable(Planet planet, ShapeGenerator gen, Vector3 axis, BuildingPadSiteEvaluator.Settings settings)
    {
        if (!IsDryEnough(planet, gen, axis, settings.dryClearance))
            return false;
        var report = BuildingPadSiteEvaluator.Evaluate(planet, gen, axis, settings);
        if (report.isValid)
            return true;
        // Accept dry gentle slopes that pads will flatten into the street grid.
        return report.isDry && report.centerSlopeDegrees <= settings.maxSlopeDegrees + 4f;
    }

    bool IsDryEnough(Planet planet, ShapeGenerator gen, Vector3 axis, float dryClearance)
    {
        if (planet == null || gen == null || axis.sqrMagnitude < 1e-10f)
            return false;
        axis.Normalize();
        float scale = PlanetBuildingPads.WorldScale(planet);
        float waterLine = BuildingPadSiteEvaluator.ResolveWaterLine(planet, dryClearance);
        float r = gen.CalculateNaturalUnscaledElevation(axis) * scale;
        return r >= waterLine;
    }

    float LotPitch() => Mathf.Max(buildingSpacing, shortMaxFootprint + 1f);

    float RoadWidth(float pitch) => Mathf.Max(pitch * 0.38f, streetWidth);

    static Vector3 StreetAlong(Vector3 hubAxis, float streetYawRad)
    {
        hubAxis.Normalize();
        Vector3 t1 = Vector3.Cross(hubAxis, Vector3.up);
        if (t1.sqrMagnitude < 1e-6f)
            t1 = Vector3.Cross(hubAxis, Vector3.right);
        t1.Normalize();
        Vector3 t2 = Vector3.Cross(hubAxis, t1);
        return (t1 * Mathf.Cos(streetYawRad) + t2 * Mathf.Sin(streetYawRad)).normalized;
    }

    struct TownPlot
    {
        public Vector3 axis;
        public float yawDegrees;
        public int ring;
    }

    /// <summary>
    /// Four city blocks around a cross of empty streets. Plots use meter offsets on the
    /// tangent plane (then reprojected) so lot pitch stays honest on the sphere.
    /// Returns the street yaw (radians) used for the grid.
    /// </summary>
    float CollectTownStreetPlots(
        Planet planet,
        ShapeGenerator gen,
        float scale,
        Vector3 hubAxis,
        float spacing,
        int capacity,
        List<TownPlot> into,
        bool skipHub,
        float? forcedStreetYaw = null)
    {
        if (into == null || capacity <= 0)
            return 0f;

        hubAxis.Normalize();
        float hubR = gen.CalculateNaturalUnscaledElevation(hubAxis) * scale;
        if (hubR < 1e-3f)
            return 0f;

        Vector3 t1 = Vector3.Cross(hubAxis, Vector3.up);
        if (t1.sqrMagnitude < 1e-6f)
            t1 = Vector3.Cross(hubAxis, Vector3.right);
        t1.Normalize();
        Vector3 t2 = Vector3.Cross(hubAxis, t1);

        float streetYaw = forcedStreetYaw ?? Random.Range(0f, Mathf.PI * 2f);
        float cos = Mathf.Cos(streetYaw);
        float sin = Mathf.Sin(streetYaw);
        Vector3 along = (t1 * cos + t2 * sin).normalized;   // east along main street
        Vector3 across = (t1 * -sin + t2 * cos).normalized; // north

        float lot = Mathf.Max(4f, spacing);
        float road = RoadWidth(lot);
        // Buildings per block edge (1 → 4 plots, 2 → 16, 3 → 36).
        int block = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(Mathf.Max(1, capacity) / 4f)), 1, 3);

        for (int row = -block; row <= block; row++)
        {
            if (row == 0)
                continue; // E–W street
            for (int col = -block; col <= block; col++)
            {
                if (col == 0)
                    continue; // N–S street
                if (into.Count >= capacity)
                    return streetYaw;

                // Meter offsets: empty cross is `road` wide; lots step by `lot` inside each block.
                float u = Mathf.Sign(col) * (road * 0.5f + (Mathf.Abs(col) - 0.5f) * lot);
                float v = Mathf.Sign(row) * (road * 0.5f + (Mathf.Abs(row) - 0.5f) * lot);

                if (skipHub && Mathf.Abs(u) < 1e-3f && Mathf.Abs(v) < 1e-3f)
                    continue;

                Vector3 face = Mathf.Abs(u) <= Mathf.Abs(v)
                    ? -Mathf.Sign(u) * along   // face the N–S street
                    : -Mathf.Sign(v) * across; // face the E–W street

                Vector3 world = hubAxis * hubR + along * u + across * v;
                Vector3 dir = world.normalized;
                int ring = Mathf.Max(Mathf.Abs(row), Mathf.Abs(col));
                into.Add(new TownPlot
                {
                    axis = dir,
                    yawDegrees = YawToward(dir, face),
                    ring = ring
                });
            }
        }

        return streetYaw;
    }

    static float YawToward(Vector3 radialAxis, Vector3 faceWorldDir)
    {
        if (radialAxis.sqrMagnitude < 1e-10f)
            return 0f;
        radialAxis.Normalize();

        Vector3 refForward = Vector3.forward;
        if (Mathf.Abs(Vector3.Dot(refForward, radialAxis)) > 0.95f)
            refForward = Vector3.right;

        Vector3 baseFwd = Vector3.ProjectOnPlane(refForward, radialAxis);
        Vector3 want = Vector3.ProjectOnPlane(faceWorldDir, radialAxis);
        if (baseFwd.sqrMagnitude < 1e-8f || want.sqrMagnitude < 1e-8f)
            return 0f;

        return Vector3.SignedAngle(baseFwd.normalized, want.normalized, radialAxis);
    }

    float MeasureFlatExtent(Planet planet, ShapeGenerator gen, float scale, Vector3 hubAxis, float hubRWorld)
    {
        float maxR = Mathf.Max(shortFlatRadius, maxFlatProbeRadius);
        float step = Mathf.Max(2f, flatProbeStep);
        float ok = shortFlatRadius;

        // Probe with a building-sized pad at satellite points — requiring one giant pad made
        // almost every site look like "only room for 1 building".
        var padSettings = MakeSettings(shortFlatRadius);
        hubAxis.Normalize();
        Vector3 t1 = Vector3.Cross(hubAxis, Vector3.up);
        if (t1.sqrMagnitude < 1e-6f)
            t1 = Vector3.Cross(hubAxis, Vector3.right);
        t1.Normalize();
        Vector3 t2 = Vector3.Cross(hubAxis, t1);

        for (float r = shortFlatRadius + step; r <= maxR; r += step)
        {
            float ang = r / Mathf.Max(1e-3f, hubRWorld);
            int hits = 0;
            const int dirs = 6;
            for (int i = 0; i < dirs; i++)
            {
                float a = (Mathf.PI * 2f * i) / dirs;
                Vector3 dir = (hubAxis + (t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a)) * ang).normalized;
                var report = BuildingPadSiteEvaluator.Evaluate(planet, gen, dir, padSettings);
                if (report.isValid)
                    hits++;
            }

            if (hits < (dirs + 1) / 2)
                break;
            ok = r;
        }

        return ok;
    }

    int EstimateShortSlots(float flatExtent)
    {
        float pitch = LotPitch();
        float road = RoadWidth(pitch);
        float minTownRadius = road * 0.5f + pitch * 0.5f;
        if (flatExtent < minTownRadius)
            return 0;

        // Ring 1 = 4 lots; ring 2 = 16; ring 3 = 36 on large shelves.
        float ring2Radius = road * 0.5f + pitch * 1.5f;
        float ring3Radius = road * 0.5f + pitch * 2.5f;
        if (flatExtent >= ring3Radius)
            return Mathf.Clamp(36, minBuildingsPerSettlement, Mathf.Max(maxShortsPerSettlement, maxShortsWithTall + 1));
        if (flatExtent >= ring2Radius)
            return Mathf.Clamp(16, minBuildingsPerSettlement, Mathf.Max(maxShortsPerSettlement, maxShortsWithTall + 1));
        return Mathf.Clamp(4, minBuildingsPerSettlement, Mathf.Max(maxShortsPerSettlement, maxShortsWithTall + 1));
    }

    BuildingPadSiteEvaluator.Settings MakeSettings(float footprintRadius)
    {
        return new BuildingPadSiteEvaluator.Settings
        {
            flatRadius = Mathf.Max(0.5f, footprintRadius),
            dryClearance = Mathf.Max(0f, dryClearance),
            maxSlopeDegrees = Mathf.Max(1f, maxSlopeDegrees),
            maxHeightVariation = Mathf.Max(0.1f, maxHeightVariation),
            ringSamples = Mathf.Clamp(siteSampleCount, 4, 24),
            searchAttempts = Mathf.Clamp(siteSearchAttemptsPerTry, 4, 128)
        };
    }

    bool TrySpawnBuilding(Planet planet, Vector3 axis, BuildingSizeClass size, float padFlatRadius, float? yawDegrees)
    {
        if (!PickPrefab(size, out GameObject prefab, out float prefabScale))
            return false;

        var go = new GameObject($"BuildingPad_{_spawnedPads.Count:00}_{size}");
        go.transform.SetParent(_spawnRoot, false);
        var pad = go.AddComponent<BuildingPad>();
        CopyPadSettings(pad, padFlatRadius);

        float yaw = yawDegrees ?? (randomYaw ? Random.Range(0f, 360f) : 0f);
        pad.ApplyPoseOnAxis(planet, axis, preserveYaw: true, yawDegrees: yaw);

        if (prefab != null)
        {
            GameObject instance;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                instance = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab, go.transform);
            else
#endif
                instance = Instantiate(prefab, go.transform);

            if (instance != null)
            {
                instance.name = prefab.name;
                instance.transform.localRotation = Quaternion.identity;
                FitBuildingInstance(instance, size, prefabScale);
                if (ensureSolidColliders)
                    EnsureSolidColliders(instance);
            }
        }

        _spawnedPads.Add(pad);
        return true;
    }

    bool IsFarEnoughFromSpawned(Planet planet, Vector3 axis, float minWorldSep, int fromPadIndex)
    {
        if (planet == null || axis.sqrMagnitude < 1e-10f || minWorldSep <= 0f)
            return true;

        axis.Normalize();
        Vector3 center = planet.transform.position;
        float r = planet.GetSurfaceRadiusWorld(axis);
        if (r < 1e-3f)
        {
            for (int i = Mathf.Max(0, fromPadIndex); i < _spawnedPads.Count; i++)
            {
                if (_spawnedPads[i] != null)
                {
                    r = Vector3.Distance(_spawnedPads[i].transform.position, center);
                    break;
                }
            }
        }

        if (r < 1e-3f)
            r = 100f;

        Vector3 proposed = center + axis * r;
        float minSq = minWorldSep * minWorldSep;

        for (int i = Mathf.Max(0, fromPadIndex); i < _spawnedPads.Count; i++)
        {
            BuildingPad pad = _spawnedPads[i];
            if (pad == null)
                continue;
            if ((pad.transform.position - proposed).sqrMagnitude < minSq)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Kenny modular building FBX often ships with no colliders. Add static MeshColliders (or a box fallback).
    /// </summary>
    static void EnsureSolidColliders(GameObject root)
    {
        if (root == null)
            return;

        Collider[] existing = root.GetComponentsInChildren<Collider>(true);
        bool hasSolid = false;
        for (int i = 0; i < existing.Length; i++)
        {
            Collider c = existing[i];
            if (c == null)
                continue;
            if (c.isTrigger)
                c.isTrigger = false;
            hasSolid = true;
        }

        if (hasSolid)
            return;

        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        int added = 0;
        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter mf = filters[i];
            if (mf == null || mf.sharedMesh == null)
                continue;

            MeshCollider mc = mf.GetComponent<MeshCollider>();
            if (mc == null)
                mc = mf.gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = mf.sharedMesh;
            mc.convex = false;
            mc.isTrigger = false;
            added++;
        }

        if (added > 0)
            return;

        // No mesh filters — approximate with a box from renderer bounds.
        Renderer[] rends = root.GetComponentsInChildren<Renderer>(true);
        if (rends == null || rends.Length == 0)
            return;

        Bounds b = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++)
        {
            if (rends[i] != null)
                b.Encapsulate(rends[i].bounds);
        }

        BoxCollider box = root.GetComponent<BoxCollider>();
        if (box == null)
            box = root.AddComponent<BoxCollider>();
        box.isTrigger = false;
        Transform t = root.transform;
        Vector3 localCenter = t.InverseTransformPoint(b.center);
        Vector3 lossy = t.lossyScale;
        Vector3 localSize = new Vector3(
            Mathf.Abs(lossy.x) > 1e-4f ? b.size.x / Mathf.Abs(lossy.x) : b.size.x,
            Mathf.Abs(lossy.y) > 1e-4f ? b.size.y / Mathf.Abs(lossy.y) : b.size.y,
            Mathf.Abs(lossy.z) > 1e-4f ? b.size.z / Mathf.Abs(lossy.z) : b.size.z);
        box.center = localCenter;
        box.size = localSize;
    }

    /// <summary>
    /// Scale the instance to a consistent short/tall height, clamp footprint, and seat the mesh on the pad.
    /// </summary>
    void FitBuildingInstance(GameObject instance, BuildingSizeClass size, float variantScale)
    {
        Transform t = instance.transform;
        t.localScale = Vector3.one;
        t.localPosition = Vector3.zero;
        t.localRotation = Quaternion.identity;

        if (!TryGetLocalBounds(instance, out Bounds localBounds) || localBounds.size.y < 1e-4f)
        {
            t.localScale = Vector3.one * Mathf.Max(0.01f, variantScale);
            SeatInstanceToPadSurface(t);
            return;
        }

        float targetHeight = size == BuildingSizeClass.Tall ? tallTargetHeight : shortTargetHeight;
        float maxFoot = size == BuildingSizeClass.Tall ? tallMaxFootprint : shortMaxFootprint;

        float s = 1f;
        if (autoScaleToTargetHeight)
        {
            s = targetHeight / Mathf.Max(1e-4f, localBounds.size.y);
            s *= Mathf.Max(0.01f, variantScale);
            s = Mathf.Clamp(s, minAutoScale, maxAutoScale);

            float foot = Mathf.Max(localBounds.size.x, localBounds.size.z) * s;
            if (foot > maxFoot && foot > 1e-4f)
                s *= maxFoot / foot;

            s = Mathf.Clamp(s, minAutoScale, maxAutoScale);
        }
        else
        {
            s = Mathf.Max(0.01f, variantScale);
        }

        t.localScale = Vector3.one * s;

        // Seat using mesh-local bounds only — world AABB seating lifts buildings on a sphere.
        SeatInstanceToPadSurface(t);
    }

    /// <summary>
    /// Seat the instance so the lowest mesh point sits on the pad plane (local +Y = away from planet).
    /// Uses world-space mesh vertices so FBX pivots / nested scales cannot leave buildings floating.
    /// </summary>
    static void SeatInstanceToPadSurface(Transform instance)
    {
        if (instance == null)
            return;

        Transform pad = instance.parent;
        if (pad == null)
        {
            instance.localPosition = new Vector3(0f, -1.5f, 0f);
            return;
        }

        Vector3 up = pad.up;
        Vector3 padPos = pad.position;

        bool any = false;
        float minAlongUp = float.PositiveInfinity;
        float sumRight = 0f;
        float sumFwd = 0f;
        int samples = 0;
        Vector3 right = pad.right;
        Vector3 fwd = pad.forward;

        MeshFilter[] filters = instance.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter mf = filters[i];
            if (mf == null || mf.sharedMesh == null)
                continue;
            SampleMeshCorners(mf.transform, mf.sharedMesh.bounds, padPos, up, right, fwd,
                ref minAlongUp, ref sumRight, ref sumFwd, ref samples, ref any);
        }

        SkinnedMeshRenderer[] skinned = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < skinned.Length; i++)
        {
            SkinnedMeshRenderer smr = skinned[i];
            if (smr == null)
                continue;
            SampleMeshCorners(smr.transform, smr.localBounds, padPos, up, right, fwd,
                ref minAlongUp, ref sumRight, ref sumFwd, ref samples, ref any);
        }

        if (!any)
        {
            // Fallback: renderer AABB corners.
            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null)
                    continue;
                Bounds wb = r.bounds;
                Vector3 c = wb.center;
                Vector3 e = wb.extents;
                for (int ix = -1; ix <= 1; ix += 2)
                for (int iy = -1; iy <= 1; iy += 2)
                for (int iz = -1; iz <= 1; iz += 2)
                {
                    Vector3 pt = c + new Vector3(e.x * ix, e.y * iy, e.z * iz);
                    float along = Vector3.Dot(pt - padPos, up);
                    if (along < minAlongUp)
                        minAlongUp = along;
                    sumRight += Vector3.Dot(pt - padPos, right);
                    sumFwd += Vector3.Dot(pt - padPos, fwd);
                    samples++;
                    any = true;
                }
            }
        }

        if (!any)
        {
            instance.localPosition = new Vector3(0f, -1.5f, 0f);
            return;
        }

        // Slightly bury the mesh so raised feet / colliders don't read as waist-high hover.
        const float sink = 0.35f;
        float avgRight = samples > 0 ? sumRight / samples : 0f;
        float avgFwd = samples > 0 ? sumFwd / samples : 0f;

        // Move in world space, then write back local (preserves radial orientation).
        Vector3 delta = up * (-minAlongUp - sink) - right * avgRight - fwd * avgFwd;
        instance.position += delta;
    }

    static void SampleMeshCorners(
        Transform meshT,
        Bounds meshLocalBounds,
        Vector3 padPos,
        Vector3 up,
        Vector3 right,
        Vector3 fwd,
        ref float minAlongUp,
        ref float sumRight,
        ref float sumFwd,
        ref int samples,
        ref bool any)
    {
        Vector3 c = meshLocalBounds.center;
        Vector3 e = meshLocalBounds.extents;
        for (int ix = -1; ix <= 1; ix += 2)
        for (int iy = -1; iy <= 1; iy += 2)
        for (int iz = -1; iz <= 1; iz += 2)
        {
            Vector3 world = meshT.TransformPoint(c + new Vector3(e.x * ix, e.y * iy, e.z * iz));
            float along = Vector3.Dot(world - padPos, up);
            if (along < minAlongUp)
                minAlongUp = along;
            sumRight += Vector3.Dot(world - padPos, right);
            sumFwd += Vector3.Dot(world - padPos, fwd);
            samples++;
            any = true;
        }
    }

    /// <summary>
    /// After planet regen (pads flatten terrain), re-snap every spawned pad to the deformed surface
    /// with planet-radial up so nothing floats or leans with the old slope.
    /// </summary>
    void ReseatAllSpawnedBuildings(Planet planet)
    {
        if (planet == null)
            return;

        Vector3 center = planet.transform.position;
        for (int i = 0; i < _spawnedPads.Count; i++)
        {
            BuildingPad pad = _spawnedPads[i];
            if (pad == null)
                continue;

            Vector3 axis = pad.transform.position - center;
            if (axis.sqrMagnitude < 1e-8f)
                continue;
            axis.Normalize();

            // Keep current spin around radial.
            Vector3 currentFwd = pad.transform.forward;
            Vector3 projected = Vector3.ProjectOnPlane(currentFwd, axis);
            float yaw = 0f;
            if (projected.sqrMagnitude > 1e-6f)
            {
                Vector3 refForward = Vector3.forward;
                if (Mathf.Abs(Vector3.Dot(refForward, axis)) > 0.95f)
                    refForward = Vector3.right;
                Vector3 basisFwd = Vector3.ProjectOnPlane(refForward, axis).normalized;
                Vector3 basisRight = Vector3.Cross(axis, basisFwd);
                yaw = Mathf.Atan2(Vector3.Dot(projected.normalized, basisRight), Vector3.Dot(projected.normalized, basisFwd)) * Mathf.Rad2Deg;
            }

            pad.ApplyPoseOnAxis(planet, axis, preserveYaw: true, yawDegrees: yaw);

            for (int c = 0; c < pad.transform.childCount; c++)
            {
                Transform child = pad.transform.GetChild(c);
                if (child == null)
                    continue;
                child.localRotation = Quaternion.identity;
                SeatInstanceToPadSurface(child);
            }
        }
    }

    /// <summary>
    /// Local-space mesh bounds (not world AABB). World AABB corners inflate badly when the pad
    /// is radially oriented on a sphere, which made buildings hover ~waist height.
    /// </summary>
    static bool TryGetLocalBounds(GameObject root, out Bounds localBounds)
    {
        localBounds = new Bounds();
        if (root == null)
            return false;

        Transform rootT = root.transform;
        bool init = false;

        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter mf = filters[i];
            if (mf == null || mf.sharedMesh == null)
                continue;
            EncapsulateMeshBounds(rootT, mf.transform, mf.sharedMesh.bounds, ref localBounds, ref init);
        }

        SkinnedMeshRenderer[] skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < skinned.Length; i++)
        {
            SkinnedMeshRenderer smr = skinned[i];
            if (smr == null)
                continue;
            EncapsulateMeshBounds(rootT, smr.transform, smr.localBounds, ref localBounds, ref init);
        }

        if (init)
            return true;

        // Fallback: renderer world AABB → local (less accurate).
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null)
                continue;
            Bounds wb = r.bounds;
            Vector3 c = wb.center;
            Vector3 e = wb.extents;
            for (int ix = -1; ix <= 1; ix += 2)
            for (int iy = -1; iy <= 1; iy += 2)
            for (int iz = -1; iz <= 1; iz += 2)
            {
                Vector3 local = rootT.InverseTransformPoint(c + new Vector3(e.x * ix, e.y * iy, e.z * iz));
                if (!init)
                {
                    localBounds = new Bounds(local, Vector3.zero);
                    init = true;
                }
                else
                    localBounds.Encapsulate(local);
            }
        }

        return init;
    }

    static void EncapsulateMeshBounds(Transform rootT, Transform meshT, Bounds meshLocalBounds, ref Bounds localBounds, ref bool init)
    {
        Vector3 c = meshLocalBounds.center;
        Vector3 e = meshLocalBounds.extents;
        for (int ix = -1; ix <= 1; ix += 2)
        for (int iy = -1; iy <= 1; iy += 2)
        for (int iz = -1; iz <= 1; iz += 2)
        {
            Vector3 meshLocal = c + new Vector3(e.x * ix, e.y * iy, e.z * iz);
            Vector3 world = meshT.TransformPoint(meshLocal);
            Vector3 rootLocal = rootT.InverseTransformPoint(world);
            if (!init)
            {
                localBounds = new Bounds(rootLocal, Vector3.zero);
                init = true;
            }
            else
                localBounds.Encapsulate(rootLocal);
        }
    }

    static Vector3Int WorldToCell(Vector3 worldPos, float invCell)
    {
        return new Vector3Int(
            Mathf.FloorToInt(worldPos.x * invCell),
            Mathf.FloorToInt(worldPos.y * invCell),
            Mathf.FloorToInt(worldPos.z * invCell));
    }

    static bool TryClaimWithNeighbors(HashSet<Vector3Int> occupied, Vector3Int cell)
    {
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            var n = new Vector3Int(cell.x + dx, cell.y + dy, cell.z + dz);
            if (occupied.Contains(n))
                return false;
        }

        occupied.Add(cell);
        return true;
    }

    void ReserveExistingPads(Planet planet, HashSet<Vector3Int> occupied, float invCell)
    {
        var pads = Object.FindObjectsByType<BuildingPad>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        Vector3 center = planet.transform.position;
        float scale = PlanetBuildingPads.WorldScale(planet);
        ShapeGenerator gen = null;
        if (planet.shapeSettings != null)
        {
            gen = new ShapeGenerator();
            gen.UpdateSettings(planet.shapeSettings);
        }

        for (int i = 0; i < pads.Length; i++)
        {
            BuildingPad pad = pads[i];
            if (pad == null)
                continue;
            if (_spawnRoot != null && pad.transform.IsChildOf(_spawnRoot))
                continue;

            Vector3 axis = pad.transform.position - center;
            if (axis.sqrMagnitude < 1e-8f)
                continue;
            axis.Normalize();
            float r = gen != null
                ? gen.CalculateNaturalUnscaledElevation(axis) * scale
                : Vector3.Distance(pad.transform.position, center);
            occupied.Add(WorldToCell(center + axis * r, invCell));
        }
    }

    void CopyPadSettings(BuildingPad pad, float padFlatRadius)
    {
        pad.flatRadius = padFlatRadius;
        pad.blendWidth = blendWidth;
        pad.dryClearance = dryClearance;
        pad.maxSlopeDegrees = maxSlopeDegrees;
        pad.maxHeightVariation = maxHeightVariation;
        pad.siteSampleCount = siteSampleCount;
        pad.siteSearchAttempts = siteSearchAttemptsPerTry;
        pad.requireSuitableSite = true;
        pad.skipBakeIfUnsuitable = false;
        pad.suppressFoliage = true;
        pad.alignToPlanetUp = true;
        pad.heightMode = BuildingPad.HeightMode.SampleAtCenter;
    }

    bool HasVariant(BuildingSizeClass size)
    {
        if (variants == null)
            return false;
        for (int i = 0; i < variants.Count; i++)
        {
            if (variants[i] != null && variants[i].prefab != null && variants[i].sizeClass == size)
                return true;
        }
        return false;
    }

    bool PickPrefab(BuildingSizeClass size, out GameObject prefab, out float scale)
    {
        prefab = null;
        scale = 1f;
        if (variants == null || variants.Count == 0)
            return false;

        float total = 0f;
        for (int i = 0; i < variants.Count; i++)
        {
            BuildingSpawnVariant v = variants[i];
            if (v != null && v.prefab != null && v.sizeClass == size)
                total += Mathf.Max(0f, v.weight);
        }
        if (total <= 0f)
            return false;

        float r = Random.value * total;
        for (int i = 0; i < variants.Count; i++)
        {
            BuildingSpawnVariant v = variants[i];
            if (v == null || v.prefab == null || v.sizeClass != size)
                continue;
            r -= Mathf.Max(0f, v.weight);
            if (r <= 0f)
            {
                prefab = v.prefab;
                scale = Mathf.Max(0.01f, v.scale);
                return true;
            }
        }
        return false;
    }

    void EnsureSpawnRoot()
    {
        if (_spawnRoot != null)
            return;
        Transform existing = transform.Find(spawnedRootName);
        if (existing != null)
        {
            _spawnRoot = existing;
            return;
        }
        var go = new GameObject(spawnedRootName);
        go.transform.SetParent(transform, false);
        _spawnRoot = go.transform;
    }

    static void DestroyImmediateSafe(GameObject go)
    {
        if (go == null)
            return;
        if (Application.isPlaying)
            Object.Destroy(go);
        else
            Object.DestroyImmediate(go);
    }
}
