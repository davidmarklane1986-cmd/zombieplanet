using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Scene-level director for the autonomous faction simulation.
/// It creates itself after scene load when no authored instance exists, so the simulation works
/// in the existing third-person scene without changing the player bootstrap.
/// </summary>
[DisallowMultipleComponent]
public sealed class FactionSimulation : MonoBehaviour
{
    public static FactionSimulation Instance { get; private set; }

    /// <summary>True after deferred faction world boot has finished.</summary>
    public bool IsWorldSimReady => _started;

    /// <summary>True while prospect/faction spawn work is running across frames.</summary>
    public bool IsWorldSimBooting => _bootRoutine != null;

    [Header("Runtime")]
    public bool autoStart = true;
    public bool debugOverlay = true;
    public bool verboseEvents = true;
    [Tooltip("Spectator BAR/Zero-K economy: mex → energy → factory → raider spam. Parks village merchant/noble loop.")]
    public bool useBarEconomyMode = true;
    [Min(0.1f)] public float directorTickInterval = 1f;
    [Min(0.1f)] public float spawnSearchRadius = 90f;
    [Min(20f)] public float minimumFactionSeparation = 280f;
    [Tooltip("Floor used when packing many factions if the preferred separation cannot fit.")]
    [Min(20f)] public float minimumFactionSeparationFloor = 150f;
    [Tooltip("Extra packing passes that try to push sites farther apart after a full set is found.")]
    [Min(0)] public int spawnSeparationImprovePasses = 4;
    [Min(4)] public int spawnSearchAttempts = 96;

    [Header("Existing scene references (optional auto-resolved)")]
    public Planet planet;
    public FoliageByColour foliage;
    public BuildingSpawner buildingSpawner;

    [Header("Existing visual assets")]
    public PlayableCharacterDef workerCharacter;
    public PlayableCharacterDef soldierCharacter;
    public PlayableCharacterDef archerCharacter;
    public PlayableCharacterDef nobleCharacter;
    public PlayableCharacterDef merchantCharacter;
    public GameObject townHallPrefab;
    public GameObject barracksPrefab;
    public GameObject marketPrefab;
    public GameObject mintPrefab;
    public GameObject claimableTownPrefab;
    public GameObject mexPrefab;
    public GameObject energyGenPrefab;
    public GameObject factoryPrefab;

    [Header("Faction data")]
    public FactionDefinition[] factionDefinitions;
    public FactionDefinition factionADefinition;
    public FactionDefinition factionBDefinition;
    [Range(2, 8)] public int minFactionCount = 8;
    [Range(2, 8)] public int maxFactionCount = 8;
    public FactionEconomySettings economy = new FactionEconomySettings();
    public FactionCombatSettings combat = new FactionCombatSettings();
    public FactionWarfareSettings warfare = new FactionWarfareSettings();

    readonly List<FactionController> _factions = new List<FactionController>(8);
    readonly List<Vector3> _foliageStreamPoints = new List<Vector3>(32);
    float _nextFoliageStream;
    readonly List<Vector3> _woodProspects = new List<Vector3>(256);
    readonly List<Vector3> _stoneProspects = new List<Vector3>(256);
    readonly List<Vector3> _roamerScatterAxes = new List<Vector3>(64);
    readonly List<float> _nextFactionTicks = new List<float>(8);
    float _nextStartCheck;
    bool _started;
    bool _subscribedPlanet;
    bool _bothReachedEngagement;
    float _lastLoggedBalance;
    Coroutine _bootRoutine;

    public IReadOnlyList<FactionController> Factions => _factions;
    public bool WarfareEnabled => FactionRegistry.WarfareEnabled;
    public float CurrentStrengthDifference
    {
        get
        {
            if (_factions.Count < 2)
                return 0f;
            float min = float.PositiveInfinity;
            float max = float.NegativeInfinity;
            int n = 0;
            for (int i = 0; i < _factions.Count; i++)
            {
                if (_factions[i] == null)
                    continue;
                float s = _factions[i].Strength;
                min = Mathf.Min(min, s);
                max = Mathf.Max(max, s);
                n++;
            }
            return n < 2 ? 0f : max - min;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void CreateRuntimeInstance()
    {
        if (FindFirstObjectByType<FactionSimulation>() != null)
            return;

        var go = new GameObject("FactionSimulation");
        go.AddComponent<FactionSimulation>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        FactionRegistry.Reset();
        ResolveSceneReferences();
        FactionStructureVisibilityCuller.Ensure(this);
    }

    void OnEnable()
    {
        if (planet != null && !_subscribedPlanet)
        {
            Planet.OnPlanetReady += OnPlanetReady;
            _subscribedPlanet = true;
        }
    }

    void Start()
    {
        if (planet != null && planet.IsGenerated)
            BeginDeferredStart();
    }

    void OnDisable()
    {
        if (_subscribedPlanet)
        {
            Planet.OnPlanetReady -= OnPlanetReady;
            _subscribedPlanet = false;
        }
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
        if (FactionRegistry.Factions.Count > 0)
            FactionRegistry.Reset();
    }

    void OnPlanetReady()
    {
        BeginDeferredStart();
    }

    void BeginDeferredStart()
    {
        if (_started || !autoStart || _bootRoutine != null)
            return;
        if (planet == null || !planet.IsGenerated)
            return;
        _bootRoutine = StartCoroutine(CoBootWorldSim());
    }

    void ResolveSceneReferences()
    {
        if (planet == null)
            planet = FindFirstObjectByType<Planet>();
        if (foliage == null)
            foliage = FindFirstObjectByType<FoliageByColour>();
        if (buildingSpawner == null)
            buildingSpawner = BuildingSpawner.Instance != null
                ? BuildingSpawner.Instance
                : FindFirstObjectByType<BuildingSpawner>();

        ResolvePlayableCharacters();
        ResolveBuildingPrefabs();
    }

    void ResolvePlayableCharacters()
    {
        if (workerCharacter != null && soldierCharacter != null &&
            archerCharacter != null && nobleCharacter != null && merchantCharacter != null)
            return;

        PlayableCharacterDef[] defs = Resources.LoadAll<PlayableCharacterDef>("PlayableCharacters");
        for (int i = 0; i < defs.Length; i++)
        {
            PlayableCharacterDef def = defs[i];
            if (def == null || def.characterPrefab == null)
                continue;
            if (soldierCharacter == null && def.id == "cyborg")
                soldierCharacter = def;
            if (workerCharacter == null && def.id == "survivor")
                workerCharacter = def;
            if (archerCharacter == null && def.id == "cowboy")
                archerCharacter = def;
            if (nobleCharacter == null && def.id == "criminal")
                nobleCharacter = def;
            if (merchantCharacter == null && def.id == "skater")
                merchantCharacter = def;
        }

        for (int i = 0; i < defs.Length; i++)
        {
            if (defs[i] == null || defs[i].characterPrefab == null)
                continue;
            if (workerCharacter == null)
                workerCharacter = defs[i];
            if (soldierCharacter == null)
                soldierCharacter = defs[i];
            if (archerCharacter == null)
                archerCharacter = defs[i];
            if (nobleCharacter == null)
                nobleCharacter = defs[i];
            if (merchantCharacter == null)
                merchantCharacter = defs[i];
        }

        if (archerCharacter == null)
            archerCharacter = soldierCharacter;
        if (nobleCharacter == null)
            nobleCharacter = workerCharacter;
        if (merchantCharacter == null)
            merchantCharacter = workerCharacter;
    }

    void ResolveBuildingPrefabs()
    {
        if (buildingSpawner == null || buildingSpawner.variants == null)
            return;

        for (int i = 0; i < buildingSpawner.variants.Count; i++)
        {
            BuildingSpawnVariant variant = buildingSpawner.variants[i];
            if (variant == null || variant.prefab == null)
                continue;
            string name = variant.prefab.name;
            if (townHallPrefab == null && name.IndexOf("TwistedTower", System.StringComparison.OrdinalIgnoreCase) >= 0)
                townHallPrefab = variant.prefab;
            if (barracksPrefab == null && name.IndexOf("house-b", System.StringComparison.OrdinalIgnoreCase) >= 0)
                barracksPrefab = variant.prefab;
            if (marketPrefab == null && name.IndexOf("house-c", System.StringComparison.OrdinalIgnoreCase) >= 0)
                marketPrefab = variant.prefab;
            if (marketPrefab == null && name.IndexOf("house-a", System.StringComparison.OrdinalIgnoreCase) >= 0)
                marketPrefab = variant.prefab;
            if (mintPrefab == null && name.IndexOf("house-a", System.StringComparison.OrdinalIgnoreCase) >= 0 &&
                variant.prefab != marketPrefab)
                mintPrefab = variant.prefab;
            if (claimableTownPrefab == null && name.IndexOf("house", System.StringComparison.OrdinalIgnoreCase) >= 0 &&
                variant.prefab != barracksPrefab && variant.prefab != marketPrefab)
                claimableTownPrefab = variant.prefab;
            if (mexPrefab == null && name.IndexOf("house-a", System.StringComparison.OrdinalIgnoreCase) >= 0)
                mexPrefab = variant.prefab;
            if (energyGenPrefab == null &&
                (name.IndexOf("Eco_Building_Grid", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                 name.IndexOf("Eco_Building_Terrace", System.StringComparison.OrdinalIgnoreCase) >= 0))
                energyGenPrefab = variant.prefab;
            if (factoryPrefab == null &&
                (name.IndexOf("Eco_Building_Slope", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                 name.IndexOf("Eco_Building_Grid", System.StringComparison.OrdinalIgnoreCase) >= 0))
                factoryPrefab = variant.prefab;
        }

        if (barracksPrefab == null)
        {
            for (int i = 0; i < buildingSpawner.variants.Count; i++)
            {
                BuildingSpawnVariant variant = buildingSpawner.variants[i];
                if (variant == null || variant.prefab == null)
                    continue;
                string name = variant.prefab.name;
                if (name.IndexOf("house", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    barracksPrefab = variant.prefab;
                    break;
                }
            }
        }

        if (barracksPrefab == null)
        {
            for (int i = 0; i < buildingSpawner.variants.Count; i++)
            {
                BuildingSpawnVariant variant = buildingSpawner.variants[i];
                if (variant == null || variant.prefab == null)
                    continue;
                if (variant.prefab != townHallPrefab)
                {
                    barracksPrefab = variant.prefab;
                    break;
                }
            }
        }

        if (marketPrefab == null)
        {
            for (int i = 0; i < buildingSpawner.variants.Count; i++)
            {
                BuildingSpawnVariant variant = buildingSpawner.variants[i];
                if (variant == null || variant.prefab == null)
                    continue;
                if (variant.sizeClass == BuildingSizeClass.Short &&
                    variant.prefab != barracksPrefab &&
                    variant.prefab != townHallPrefab)
                {
                    marketPrefab = variant.prefab;
                    break;
                }
            }
        }

        if (marketPrefab == null)
            marketPrefab = barracksPrefab;
        if (mintPrefab == null)
            mintPrefab = marketPrefab != null ? marketPrefab : barracksPrefab;
        if (claimableTownPrefab == null)
            claimableTownPrefab = marketPrefab != null ? marketPrefab : barracksPrefab;
        if (mexPrefab == null)
            mexPrefab = marketPrefab != null ? marketPrefab : barracksPrefab;
        if (energyGenPrefab == null)
        {
            for (int i = 0; i < buildingSpawner.variants.Count; i++)
            {
                BuildingSpawnVariant variant = buildingSpawner.variants[i];
                if (variant == null || variant.prefab == null)
                    continue;
                if (variant.sizeClass == BuildingSizeClass.Tall && variant.prefab != townHallPrefab)
                {
                    energyGenPrefab = variant.prefab;
                    break;
                }
            }
        }
        if (energyGenPrefab == null)
            energyGenPrefab = barracksPrefab;
        if (factoryPrefab == null)
            factoryPrefab = energyGenPrefab != null ? energyGenPrefab : barracksPrefab;
    }

    public GameObject GetBuildingPrefab(BuildingKind kind)
    {
        switch (kind)
        {
            case BuildingKind.TownHall: return townHallPrefab;
            case BuildingKind.Barracks: return barracksPrefab;
            case BuildingKind.Market: return marketPrefab;
            case BuildingKind.Mint: return mintPrefab;
            case BuildingKind.Mex: return mexPrefab;
            case BuildingKind.EnergyGen: return energyGenPrefab;
            case BuildingKind.Factory: return factoryPrefab;
            default: return null;
        }
    }

    public GameObject GetUnitPrefab(Stargrave.Rts2.Rts2Role role)
    {
        PlayableCharacterDef def = null;
        switch (role)
        {
            case Stargrave.Rts2.Rts2Role.Worker: def = workerCharacter; break;
            case Stargrave.Rts2.Rts2Role.Infantry: def = soldierCharacter; break;
            case Stargrave.Rts2.Rts2Role.Archer: def = archerCharacter; break;
            case Stargrave.Rts2.Rts2Role.Noble: def = nobleCharacter; break;
            case Stargrave.Rts2.Rts2Role.Merchant: def = merchantCharacter; break;
        }
        return def != null ? def.characterPrefab : null;
    }

    public void TryStart()
    {
        BeginDeferredStart();
    }

    IEnumerator CoBootWorldSim()
    {
        try
        {
            // Paint loading jokes for at least one frame before heavy work.
            yield return null;

            ResolveSceneReferences();
            if (!_subscribedPlanet)
            {
                Planet.OnPlanetReady += OnPlanetReady;
                _subscribedPlanet = true;
            }

            FoliageResourceAdapter.EnsureExists(foliage);
            yield return null;

            yield return CoBuildResourceProspectAtlas();
            yield return null;

            EnsureRts2World();
            FactionStructureVisibilityCuller.Ensure(this);
            yield return null;

            yield return CoCreateFactions();

            if (_factions.Count < 2)
            {
                Debug.LogError(
                    $"[FactionSimulation] Boot finished with {_factions.Count} factions — will retry.",
                    this);
                yield break;
            }

            RebuildRts2DenseNav();
            yield return null;

            _started = true;
            if (verboseEvents)
                Debug.Log($"[FactionSimulation] World sim ready ({_factions.Count} factions).", this);
        }
        finally
        {
            // Always clear so a failed/aborted boot can be retried via TryStart.
            _bootRoutine = null;
        }
    }

    IEnumerator CoBuildResourceProspectAtlas()
    {
        _woodProspects.Clear();
        _stoneProspects.Clear();
        if (planet == null)
            yield break;

        for (int i = 0; i < 384; i++)
        {
            if (!TryFindDryAxis(Random.onUnitSphere, out Vector3 axis, maxSamples: 12))
            {
                if ((i & 7) == 7)
                    yield return null;
                continue;
            }

            Vector3 position = planet.GetSurfacePointWorld(axis);
            if (FactionController.SurfaceSupportsResource(planet, position, FactionResourceType.Wood))
                _woodProspects.Add(position);
            if (FactionController.SurfaceSupportsResource(planet, position, FactionResourceType.Stone))
                _stoneProspects.Add(position);
            if ((i & 7) == 7)
                yield return null;
        }

        int stoneAttempts = 0;
        while (_stoneProspects.Count < 96 && stoneAttempts < 640)
        {
            stoneAttempts++;
            if (!TryFindDryAxis(Random.onUnitSphere, out Vector3 stoneAxis, maxSamples: 12))
            {
                if ((stoneAttempts & 7) == 7)
                    yield return null;
                continue;
            }

            Vector3 stonePos = planet.GetSurfacePointWorld(stoneAxis);
            if (FactionController.SurfaceSupportsResource(planet, stonePos, FactionResourceType.Stone))
                _stoneProspects.Add(stonePos);
            if ((stoneAttempts & 7) == 7)
                yield return null;
        }

        if (verboseEvents)
            Debug.Log($"[FactionSimulation] Resource prospects: wood {_woodProspects.Count}, stone {_stoneProspects.Count}.");
    }

    IEnumerator CoCreateFactions()
    {
        if (_factions.Count > 0)
            yield break;

        _roamerScatterAxes.Clear();
        int min = Mathf.Clamp(minFactionCount, 2, 8);
        int max = Mathf.Clamp(maxFactionCount, min, 8);
        int wanted = Random.Range(min, max + 1);
        var axes = new List<Vector3>(wanted);

        yield return CoFindSeparatedSpawnAxes(wanted, axes);
        if (axes.Count < 2)
        {
            Debug.LogError("[FactionSimulation] Could not find enough mainland faction spawn sites.", this);
            yield break;
        }

        yield return null;

        if (axes.Count < wanted && verboseEvents)
        {
            Debug.LogWarning(
                $"[FactionSimulation] Wanted {wanted} factions; placed {axes.Count} dry sites.",
                this);
        }

        FactionDefinition[] defs = ResolveDefinitions();
        for (int i = 0; i < axes.Count; i++)
        {
            FactionDefinition def = i < defs.Length ? defs[i] : null;
            string fallback = UniqueFallbackName(def, i);
            string id = def != null && !string.IsNullOrWhiteSpace(def.factionId)
                ? $"{def.factionId}_{i}"
                : $"faction_{i}";
            CreateFaction(i, id, def, axes[i], fallback);
            yield return null;
        }

        PinNearestStoneProspects();
        _nextFoliageStream = 0f;
        RefreshFactionFoliageStreaming();
        yield return null;
        SpawnClaimableTowns();
        if (verboseEvents)
            Debug.Log($"[FactionSimulation] Spawned {_factions.Count} factions.", this);
    }

    void EnsureRts2World()
    {
        Stargrave.Rts2.Rts2World world = Stargrave.Rts2.Rts2World.Instance;
        if (world == null)
        {
            var go = new GameObject("Rts2World");
            go.transform.SetParent(transform, false);
            world = go.AddComponent<Stargrave.Rts2.Rts2World>();
        }

        // Coarse bake first; densify after faction/town anchors exist.
        world.BakeAndStart(this, System.Array.Empty<Vector3>());
    }

    void RebuildRts2DenseNav()
    {
        Stargrave.Rts2.Rts2World world = Stargrave.Rts2.Rts2World.Instance;
        if (world == null || !world.IsReady)
            return;

        var anchors = new System.Collections.Generic.List<Vector3>(_factions.Count + 16);
        for (int i = 0; i < _factions.Count; i++)
        {
            FactionController faction = _factions[i];
            if (faction == null)
                continue;
            anchors.Add(faction.GetSafePosition());
        }

        IReadOnlyList<ClaimableTown> towns = FactionRegistry.Towns;
        for (int i = 0; i < towns.Count; i++)
        {
            if (towns[i] != null)
                anchors.Add(towns[i].transform.position);
        }

        world.RebuildNavDense(this, anchors);
    }

    void CreateFactions()
    {
        if (_factions.Count > 0)
            return;

        _roamerScatterAxes.Clear();
        int min = Mathf.Clamp(minFactionCount, 2, 8);
        int max = Mathf.Clamp(maxFactionCount, min, 8);
        int wanted = Random.Range(min, max + 1);
        var axes = new List<Vector3>(wanted);
        if (!TryFindSeparatedSpawnAxes(wanted, axes) || axes.Count < 2)
        {
            Debug.LogError("[FactionSimulation] Could not find enough mainland faction spawn sites.", this);
            return;
        }
        if (axes.Count < wanted && verboseEvents)
        {
            Debug.LogWarning(
                $"[FactionSimulation] Wanted {wanted} factions; placed {axes.Count} dry sites.",
                this);
        }

        FactionDefinition[] defs = ResolveDefinitions();
        for (int i = 0; i < axes.Count; i++)
        {
            FactionDefinition def = i < defs.Length ? defs[i] : null;
            string fallback = UniqueFallbackName(def, i);
            string id = def != null && !string.IsNullOrWhiteSpace(def.factionId)
                ? $"{def.factionId}_{i}"
                : $"faction_{i}";
            CreateFaction(i, id, def, axes[i], fallback);
        }

        PinNearestStoneProspects();
        _nextFoliageStream = 0f;
        RefreshFactionFoliageStreaming();
        SpawnClaimableTowns();
        if (verboseEvents)
            Debug.Log($"[FactionSimulation] Spawned {_factions.Count} factions.", this);
    }

    void SpawnClaimableTowns()
    {
        if (planet == null || FactionRegistry.Towns.Count > 0)
            return;

        int wanted = Mathf.Max(1, economy.claimableTownCount);
        float townSep = Mathf.Max(40f, economy.claimableTownMinSeparation);
        float factionSep = Mathf.Max(40f, economy.claimableTownFactionSeparation);
        float townSepSq = townSep * townSep;
        float factionSepSq = factionSep * factionSep;
        var axes = new List<Vector3>(wanted);
        int attempts = Mathf.Max(spawnSearchAttempts * 2, 128);
        for (int i = 0; i < attempts && axes.Count < wanted; i++)
        {
            if (!TryFindDryAxis(Random.onUnitSphere, out Vector3 axis, maxSamples: 12))
                continue;
            Vector3 pos = planet.GetSurfacePointWorld(axis);
            bool ok = true;
            for (int f = 0; f < _factions.Count; f++)
            {
                if (_factions[f] == null)
                    continue;
                if ((_factions[f].GetSafePosition() - pos).sqrMagnitude < factionSepSq)
                {
                    ok = false;
                    break;
                }
            }
            if (!ok)
                continue;
            for (int t = 0; t < axes.Count; t++)
            {
                Vector3 other = planet.GetSurfacePointWorld(axes[t]);
                if ((other - pos).sqrMagnitude < townSepSq)
                {
                    ok = false;
                    break;
                }
            }
            if (!ok)
                continue;
            axes.Add(axis);
        }

        for (int i = 0; i < axes.Count; i++)
            ClaimableTown.Create(this, axes[i], claimableTownPrefab);

        if (verboseEvents)
            Debug.Log($"[FactionSimulation] Spawned {axes.Count} claimable towns.", this);
    }

    FactionDefinition[] ResolveDefinitions()
    {
        if (factionDefinitions != null && factionDefinitions.Length > 0)
            return factionDefinitions;

        int n = 0;
        if (factionADefinition != null)
            n++;
        if (factionBDefinition != null)
            n++;
        var fallback = new FactionDefinition[n];
        int w = 0;
        if (factionADefinition != null)
            fallback[w++] = factionADefinition;
        if (factionBDefinition != null)
            fallback[w] = factionBDefinition;
        return fallback;
    }

    static string UniqueFallbackName(FactionDefinition def, int index)
    {
        if (def != null &&
            !string.IsNullOrWhiteSpace(def.displayName) &&
            def.displayName != "Faction")
            return def.displayName;
        return $"Faction {index + 1}";
    }

    void BuildResourceProspectAtlas()
    {
        _woodProspects.Clear();
        _stoneProspects.Clear();
        if (planet == null)
            return;

        for (int i = 0; i < 384; i++)
        {
            if (!TryFindDryAxis(Random.onUnitSphere, out Vector3 axis, maxSamples: 12))
                continue;
            Vector3 position = planet.GetSurfacePointWorld(axis);
            if (FactionController.SurfaceSupportsResource(planet, position, FactionResourceType.Wood))
                _woodProspects.Add(position);
            if (FactionController.SurfaceSupportsResource(planet, position, FactionResourceType.Stone))
                _stoneProspects.Add(position);
        }

        int stoneAttempts = 0;
        while (_stoneProspects.Count < 96 && stoneAttempts < 640)
        {
            stoneAttempts++;
            if (!TryFindDryAxis(Random.onUnitSphere, out Vector3 stoneAxis))
                continue;
            Vector3 stonePos = planet.GetSurfacePointWorld(stoneAxis);
            if (FactionController.SurfaceSupportsResource(planet, stonePos, FactionResourceType.Stone))
                _stoneProspects.Add(stonePos);
        }

        if (verboseEvents)
            Debug.Log($"[FactionSimulation] Resource prospects: wood {_woodProspects.Count}, stone {_stoneProspects.Count}.");
    }

    public bool TryGetNearestProspect(FactionResourceType type, Vector3 from, out Vector3 position)
    {
        return TryGetNextProspect(type, from, null, out position);
    }

    public bool TryGetNextProspect(
        FactionResourceType type,
        Vector3 origin,
        IList<Vector3> skip,
        out Vector3 position)
    {
        List<Vector3> prospects = type == FactionResourceType.Wood ? _woodProspects : _stoneProspects;
        position = default;
        float bestSq = float.PositiveInfinity;
        const float skipMergeSq = 45f * 45f;
        for (int i = 0; i < prospects.Count; i++)
        {
            Vector3 candidate = prospects[i];
            if (IsSkippedProspect(candidate, skip, skipMergeSq))
                continue;
            float d = (candidate - origin).sqrMagnitude;
            if (d < bestSq)
            {
                bestSq = d;
                position = candidate;
            }
        }
        return bestSq < float.PositiveInfinity;
    }

    void PinNearestStoneProspects()
    {
        for (int i = 0; i < _factions.Count; i++)
        {
            FactionController faction = _factions[i];
            if (faction == null)
                continue;
            if (!TryGetNextProspect(FactionResourceType.Stone, faction.GetSafePosition(), null, out Vector3 prospect))
                continue;
            faction.RememberStreamFocus(prospect, true, FactionResourceType.Stone);
        }
    }

    static bool IsSkippedProspect(Vector3 candidate, IList<Vector3> skip, float mergeSq)
    {
        if (skip == null)
            return false;
        for (int i = 0; i < skip.Count; i++)
        {
            if ((skip[i] - candidate).sqrMagnitude <= mergeSq)
                return true;
        }
        return false;
    }

    void RefreshFactionFoliageStreaming()
    {
        if (foliage == null)
            return;

        _nextFoliageStream = Time.time + 3f;
        PinNearestStoneProspects();
        float radius = combat.workerFoliageRadius > 0f ? combat.workerFoliageRadius : 140f;
        _foliageStreamPoints.Clear();
        for (int i = 0; i < _factions.Count; i++)
        {
            FactionController faction = _factions[i];
            if (faction == null)
                continue;
            AddMergedStreamPoint(faction.GetSafePosition());
            for (int f = 0; f < faction.StreamFocusCount; f++)
                AddMergedStreamPoint(faction.GetStreamFocus(f));
        }

        foliage.ReplaceExternalStreamRequests(_foliageStreamPoints, radius);
    }

    void AddMergedStreamPoint(Vector3 position)
    {
        const float mergeSq = 80f * 80f;
        for (int i = 0; i < _foliageStreamPoints.Count; i++)
        {
            if ((_foliageStreamPoints[i] - position).sqrMagnitude <= mergeSq)
                return;
        }
        _foliageStreamPoints.Add(position);
    }

    /// <summary>
    /// Frame-sliced spawn packing. Keeps the best partial result across separation tiers, then
    /// fills remaining slots so we don't ship with only 2 factions when 8 were requested.
    /// After a full set, runs improvement passes to push sites farther apart.
    /// </summary>
    IEnumerator CoFindSeparatedSpawnAxes(int wanted, List<Vector3> axes)
    {
        axes.Clear();
        if (planet == null || wanted < 2)
            yield break;

        float preferred = Mathf.Max(20f, minimumFactionSeparation);
        float floor = Mathf.Clamp(minimumFactionSeparationFloor, 20f, preferred);
        float[] separations =
        {
            preferred * 1.25f,
            preferred,
            Mathf.Lerp(floor, preferred, 0.66f),
            Mathf.Lerp(floor, preferred, 0.33f),
            floor,
            Mathf.Max(40f, floor * 0.7f)
        };

        var best = new List<Vector3>(wanted);
        float bestMinPair = -1f;
        const int probesPerFrame = 6;

        for (int s = 0; s < separations.Length; s++)
        {
            float separation = separations[s];
            axes.Clear();
            int attempts = Mathf.Max(320, spawnSearchAttempts * Mathf.Max(6, wanted) * (s + 2));
            int probesThisFrame = 0;

            for (int i = 0; i < attempts && axes.Count < wanted; i++)
            {
                if (TryFindMainlandSpawnAxis(Random.onUnitSphere, out Vector3 axis, maxInnerAttempts: 28) &&
                    IsFarFromChosen(axis, axes, separation))
                {
                    axes.Add(axis.normalized);
                }

                probesThisFrame++;
                if (probesThisFrame >= probesPerFrame)
                {
                    probesThisFrame = 0;
                    yield return null;
                }
            }

            float minPair = MinPairwiseDistance(axes);
            bool betterCount = axes.Count > best.Count;
            bool sameCountFarther = axes.Count == best.Count && axes.Count >= 2 && minPair > bestMinPair;
            if (betterCount || sameCountFarther)
            {
                best.Clear();
                best.AddRange(axes);
                bestMinPair = minPair;
            }

            if (axes.Count >= wanted && minPair >= preferred * 0.95f)
            {
                if (verboseEvents && s > 0)
                {
                    Debug.Log(
                        $"[FactionSimulation] Packed {axes.Count} factions with separation {separation:0} " +
                        $"(preferred {preferred:0}, minPair {minPair:0}).",
                        this);
                }
                break;
            }

            yield return null;
        }

        axes.Clear();
        axes.AddRange(best);

        float fillSep = separations[separations.Length - 1];
        int fillGuard = 0;
        const int fillGuardMax = 120;
        while (axes.Count < wanted && fillGuard < fillGuardMax)
        {
            fillGuard++;
            if (TryFindMainlandSpawnAxis(Random.onUnitSphere, out Vector3 axis, maxInnerAttempts: 48) &&
                IsFarFromChosen(axis, axes, fillSep))
            {
                axes.Add(axis.normalized);
            }
            yield return null;
        }

        yield return CoImprovePackSeparation(axes, preferred);

        if (verboseEvents)
        {
            float finalMin = MinPairwiseDistance(axes);
            Debug.Log(
                $"[FactionSimulation] Final spawn pack {axes.Count}/{wanted}, minPair {finalMin:0} " +
                $"(preferred {preferred:0}).",
                this);
            if (axes.Count < wanted)
            {
                Debug.LogWarning(
                    $"[FactionSimulation] Spawn pack incomplete: {axes.Count}/{wanted} sites.",
                    this);
            }
        }
    }

    IEnumerator CoImprovePackSeparation(List<Vector3> axes, float preferred)
    {
        if (planet == null || axes == null || axes.Count < 2)
            yield break;

        int passes = Mathf.Max(0, spawnSeparationImprovePasses);
        float goal = Mathf.Max(preferred, minimumFactionSeparation);
        for (int pass = 0; pass < passes; pass++)
        {
            float before = MinPairwiseDistance(axes);
            for (int i = 0; i < axes.Count; i++)
            {
                Vector3 current = axes[i];
                float bestScore = MinDistanceToOthers(current, axes, i);
                Vector3 bestAxis = current;
                for (int t = 0; t < 36; t++)
                {
                    if (!TryFindMainlandSpawnAxis(Random.onUnitSphere, out Vector3 candidate, maxInnerAttempts: 16))
                        continue;
                    if (!IsFarFromOthers(candidate, axes, i, goal * 0.75f))
                        continue;
                    float score = MinDistanceToOthers(candidate, axes, i);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestAxis = candidate.normalized;
                    }
                }
                axes[i] = bestAxis;
                if ((i & 1) == 1)
                    yield return null;
            }

            float after = MinPairwiseDistance(axes);
            if (verboseEvents)
            {
                Debug.Log(
                    $"[FactionSimulation] Separation improve pass {pass + 1}/{passes}: " +
                    $"minPair {before:0} → {after:0} (goal {goal:0}).",
                    this);
            }
            if (after >= goal * 0.95f)
                yield break;
            yield return null;
        }
    }

    float MinPairwiseDistance(List<Vector3> axes)
    {
        if (planet == null || axes == null || axes.Count < 2)
            return 0f;
        float best = float.PositiveInfinity;
        for (int i = 0; i < axes.Count; i++)
        {
            Vector3 a = planet.GetSurfacePointWorld(axes[i]);
            for (int j = i + 1; j < axes.Count; j++)
            {
                float d = Vector3.Distance(a, planet.GetSurfacePointWorld(axes[j]));
                if (d < best)
                    best = d;
            }
        }
        return float.IsPositiveInfinity(best) ? 0f : best;
    }

    float MinDistanceToOthers(Vector3 axis, List<Vector3> axes, int selfIndex)
    {
        if (planet == null || axes == null)
            return 0f;
        Vector3 pos = planet.GetSurfacePointWorld(axis);
        float best = float.PositiveInfinity;
        for (int i = 0; i < axes.Count; i++)
        {
            if (i == selfIndex)
                continue;
            float d = Vector3.Distance(pos, planet.GetSurfacePointWorld(axes[i]));
            if (d < best)
                best = d;
        }
        return float.IsPositiveInfinity(best) ? 0f : best;
    }

    bool IsFarFromOthers(Vector3 axis, List<Vector3> axes, int selfIndex, float separation)
    {
        Vector3 pos = planet.GetSurfacePointWorld(axis);
        float minDistance = Mathf.Max(20f, separation);
        for (int i = 0; i < axes.Count; i++)
        {
            if (i == selfIndex)
                continue;
            float distance = Vector3.Distance(pos, planet.GetSurfacePointWorld(axes[i]));
            if (distance < minDistance)
                return false;
        }
        return true;
    }

    bool TryFindSeparatedSpawnAxes(int wanted, List<Vector3> axes)
    {
        axes.Clear();
        if (planet == null || wanted < 2)
            return false;

        float preferred = Mathf.Max(20f, minimumFactionSeparation);
        float floor = Mathf.Clamp(minimumFactionSeparationFloor, 20f, preferred);
        // Prefer wide spacing, then relax so denser counts (e.g. 8) can still fit.
        float[] separations =
        {
            preferred,
            Mathf.Lerp(floor, preferred, 0.66f),
            Mathf.Lerp(floor, preferred, 0.33f),
            floor
        };

        for (int s = 0; s < separations.Length; s++)
        {
            float separation = separations[s];
            axes.Clear();
            int attempts = Mathf.Max(128, spawnSearchAttempts * Mathf.Max(4, wanted) * (s + 1));
            for (int i = 0; i < attempts && axes.Count < wanted; i++)
            {
                Vector3 preferredAxis = Random.onUnitSphere;
                if (!TryFindMainlandSpawnAxis(preferredAxis, out Vector3 axis))
                    continue;
                if (!IsFarFromChosen(axis, axes, separation))
                    continue;
                axes.Add(axis.normalized);
            }

            if (axes.Count >= wanted)
            {
                if (verboseEvents && s > 0)
                {
                    Debug.Log(
                        $"[FactionSimulation] Packed {axes.Count} factions with separation {separation:0} " +
                        $"(preferred {preferred:0}).",
                        this);
                }
                return true;
            }
        }

        return axes.Count >= 2;
    }

    bool IsFarFromChosen(Vector3 axis, List<Vector3> chosen, float separation)
    {
        Vector3 pos = planet.GetSurfacePointWorld(axis);
        float minDistance = Mathf.Max(20f, separation);
        for (int i = 0; i < chosen.Count; i++)
        {
            float distance = Vector3.Distance(pos, planet.GetSurfacePointWorld(chosen[i]));
            if (distance < minDistance)
                return false;
        }
        return true;
    }

    bool TryFindDryAxis(Vector3 preferredAxis, out Vector3 axis, int maxSamples = -1)
    {
        axis = default;
        if (planet == null)
            return false;

        int samples = maxSamples > 0 ? maxSamples : Mathf.Max(32, spawnSearchAttempts);
        MeshCollider collider = ZombieAI.ResolvePrimaryTerrainMeshCollider(planet.transform);
        PlanetOceanLayer ocean = planet.GetComponent<PlanetOceanLayer>();
        Vector3 position = PlanetSurfaceSampler.GetDrySurfacePosition(
            preferredAxis,
            planet.transform.position,
            collider,
            planet,
            ocean,
            ~0,
            samples,
            0.1f,
            planet.GetBaseRadiusWorld(),
            1.25f);
        axis = (position - planet.transform.position).normalized;
        return axis.sqrMagnitude > 1e-6f &&
               (ocean == null || ocean.GetDepthBelowSurface(position) <= 0f);
    }

    /// <summary>One dry+mainland check (no inner retry storm) — used by frame-sliced boot packing.</summary>
    bool TryEvaluateOneMainlandCandidate(Vector3 preferredAxis, out Vector3 axis)
    {
        axis = default;
        if (planet == null)
            return false;

        // Cheap dry sample budget so a few of these fit in one frame without hitching jokes.
        if (!TryFindDryAxis(preferredAxis, out Vector3 dry, maxSamples: 12))
            return false;
        if (!BuildingPlacementSystem.IsDryMainlandCampus(planet, dry))
            return false;
        if (!BuildingPlacementSystem.IsSuitableFactionAnchor(
                planet, dry, economy.townHallFlatRadius, spawnSearchRadius))
            return false;
        if (!BuildingPlacementSystem.IsSwarmFriendlyOpsArea(planet, dry, 80f))
            return false;

        axis = dry;
        return true;
    }

    public bool TryFindMainlandSpawnAxis(Vector3 preferredAxis, out Vector3 axis, int maxInnerAttempts = -1)
    {
        axis = default;
        if (planet == null)
            return false;

        int attempts = maxInnerAttempts > 0
            ? Mathf.Max(4, maxInnerAttempts)
            : Mathf.Max(192, spawnSearchAttempts * 6);
        Vector3 start = preferredAxis.sqrMagnitude > 1e-8f ? preferredAxis.normalized : Random.onUnitSphere;
        for (int i = 0; i < attempts; i++)
        {
            Vector3 guess = i == 0 ? start : Random.onUnitSphere;
            if (!TryFindDryAxis(guess, out Vector3 dry))
                continue;
            if (!BuildingPlacementSystem.IsDryMainlandCampus(planet, dry))
                continue;
            if (!BuildingPlacementSystem.IsSuitableFactionAnchor(
                    planet, dry, economy.townHallFlatRadius, spawnSearchRadius))
                continue;
            // Wider ops bubble — alpine pads look fine at the hall but trap haulers on the ring.
            if (!BuildingPlacementSystem.IsSwarmFriendlyOpsArea(planet, dry, 80f))
                continue;
            axis = dry;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Random dry-land axis at the sphere sample itself — for scattering roamers across the globe.
    /// Does NOT coastal-snap via GetDrySurfacePosition (that collapses ocean samples onto one continent).
    /// </summary>
    public bool TryFindScatteredRoamerAxis(out Vector3 axis, int maxAttempts = 96)
    {
        axis = default;
        if (planet == null)
            return false;

        float waterLine = BuildingPadSiteEvaluator.ResolveWaterLine(planet, 1.25f);
        for (int i = 0; i < maxAttempts; i++)
        {
            Vector3 guess = Random.onUnitSphere;
            if (planet.GetSurfaceRadiusWorld(guess) < waterLine)
                continue;
            // Smaller campus radius than town-hall pads so peninsulas/islands still count.
            if (!BuildingPlacementSystem.IsDryMainlandCampus(planet, guess, campusRadius: 55f))
                continue;
            axis = guess.normalized;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Greedy maximin dry sample: farthest from already-placed roamers, still on dry land.
    /// </summary>
    public bool TryFindMaximinRoamerAxis(
        float minAngleDegrees,
        float minChord,
        out Vector3 axis,
        int candidateBudget = 64)
    {
        axis = default;
        if (planet == null)
            return false;

        Vector3 best = default;
        float bestScore = float.NegativeInfinity;
        bool found = false;
        int budget = Mathf.Max(16, candidateBudget);

        for (int i = 0; i < budget; i++)
        {
            if (!TryFindScatteredRoamerAxis(out Vector3 candidate, maxAttempts: 12))
                continue;

            float score = MinRoamerSeparationScore(candidate);
            if (!found || score > bestScore)
            {
                found = true;
                bestScore = score;
                best = candidate;
            }
        }

        if (!found)
            return false;

        // Prefer candidates that clear the spacing floor; otherwise still take the farthest.
        float minAcceptAngle = Mathf.Max(8f, minAngleDegrees * 0.35f);
        if (!IsRoamerScatterAxisFree(best, minAngleDegrees, minChord) &&
            bestScore < minAcceptAngle)
            return false;

        axis = best;
        return true;
    }

    float MinRoamerSeparationScore(Vector3 axis)
    {
        if (_roamerScatterAxes.Count == 0)
            return float.PositiveInfinity;

        axis.Normalize();
        float best = float.PositiveInfinity;
        for (int i = 0; i < _roamerScatterAxes.Count; i++)
        {
            float ang = Vector3.Angle(axis, _roamerScatterAxes[i]);
            if (ang < best)
                best = ang;
        }
        return best;
    }

    public bool IsRoamerScatterAxisFree(Vector3 axis, float minAngleDegrees, float minChord)
    {
        if (axis.sqrMagnitude < 1e-8f)
            return false;
        axis.Normalize();
        float minDot = Mathf.Cos(Mathf.Clamp(minAngleDegrees, 1f, 120f) * Mathf.Deg2Rad);
        float minChordSq = minChord * minChord;
        Vector3 world = planet != null ? planet.GetSurfacePointWorld(axis) : axis;
        for (int i = 0; i < _roamerScatterAxes.Count; i++)
        {
            Vector3 other = _roamerScatterAxes[i];
            if (Vector3.Dot(axis, other) > minDot)
                return false;
            if (planet != null)
            {
                Vector3 otherWorld = planet.GetSurfacePointWorld(other);
                if ((otherWorld - world).sqrMagnitude < minChordSq)
                    return false;
            }
        }
        return true;
    }

    public void RegisterRoamerScatterAxis(Vector3 axis)
    {
        if (axis.sqrMagnitude > 1e-8f)
            _roamerScatterAxes.Add(axis.normalized);
    }

    public int RoamerScatterCount => _roamerScatterAxes.Count;

    void CreateFaction(int index, string runtimeId, FactionDefinition definition, Vector3 axis, string fallbackName)
    {
        var go = new GameObject(fallbackName);
        go.transform.SetParent(transform, false);
        var faction = go.AddComponent<FactionController>();
        faction.Initialize(this, index, runtimeId, definition, axis, fallbackName);
        _factions.Add(faction);
        FactionRegistry.RegisterFaction(faction);
        float stagger = directorTickInterval * ((_factions.Count - 1) / (float)Mathf.Max(2, maxFactionCount));
        _nextFactionTicks.Add(Time.time + stagger);
        faction.SpawnInitialWorkers();
        if (verboseEvents)
            Debug.Log($"[FactionSimulation] {faction.DisplayName} spawned.", faction);
    }

    void Update()
    {
        if (!_started)
        {
            // The planet may finish generation after this component's Start, and some
            // scene bootstrap orders can miss the static ready event. Retry cheaply.
            if (Time.time >= _nextStartCheck)
            {
                _nextStartCheck = Time.time + 0.5f;
                ResolveSceneReferences();
                TryStart();
            }
            return;
        }
        if (Time.time >= _nextFoliageStream)
            RefreshFactionFoliageStreaming();

        TickDueFaction();
        TickClaimableTowns();
        if (TerritorySystem.HasInstance)
            TerritorySystem.Instance.Tick(Time.deltaTime);
        EvaluateEngagement();
        EvaluateBalance();
    }

    void TickClaimableTowns()
    {
        IReadOnlyList<ClaimableTown> towns = FactionRegistry.Towns;
        float dt = Time.deltaTime;
        for (int i = 0; i < towns.Count; i++)
        {
            if (towns[i] != null)
                towns[i].Tick(dt);
        }
    }

    void TickDueFaction()
    {
        int n = _factions.Count;
        if (n == 0)
            return;
        while (_nextFactionTicks.Count < n)
            _nextFactionTicks.Add(Time.time);
        for (int i = 0; i < n; i++)
        {
            if (_factions[i] == null || Time.time < _nextFactionTicks[i])
                continue;
            float interval = Mathf.Max(0.1f, directorTickInterval);
            float dt = Mathf.Max(0.01f, Time.time - (_nextFactionTicks[i] - interval));
            _nextFactionTicks[i] = Time.time + interval;
            _factions[i].SimulationTick(dt);
            return;
        }
    }

    void EvaluateEngagement()
    {
        if (_bothReachedEngagement || _factions.Count < 2)
            return;

        int need = warfare.minSoldiersToPropose > 0
            ? warfare.minSoldiersToPropose
            : (warfare.minimumSoldiersForWar > 0 ? warfare.minimumSoldiersForWar : 6);
        int ready = 0;
        for (int i = 0; i < _factions.Count; i++)
        {
            if (_factions[i] != null && _factions[i].SoldierCount >= need)
                ready++;
        }
        if (ready < 2)
            return;

        _bothReachedEngagement = true;
        FactionRegistry.EnableWarfare();
        if (verboseEvents)
            Debug.Log($"[FactionSimulation] Warfare enabled: {ready} factions have {need}+ soldiers.");
    }

    void EvaluateBalance()
    {
        if (_factions.Count < 2)
            return;

        FactionBalanceSystem.Tick(this);

        float difference = CurrentStrengthDifference;
        if (Mathf.Abs(difference - _lastLoggedBalance) > 100f && verboseEvents)
        {
            _lastLoggedBalance = difference;
            Debug.Log($"[FactionSimulation] Strength spread is {difference:0}.");
        }
    }

    public float GetGrowthModifier(FactionController faction)
    {
        if (faction == null || _factions.Count < 2)
            return 1f;

        float mod = 1f;
        FactionController other = StrongestOther(faction);
        if (other != null && faction.Strength > other.Strength)
        {
            float threshold = Mathf.Max(1f, other.Strength * (1f + warfare.strengthBalanceBuffer));
            float excess = Mathf.Max(0f, faction.Strength - threshold);
            float scale = Mathf.Clamp01(excess / Mathf.Max(1f, warfare.minimumEngagementStrength));
            mod = 1f - warfare.maximumStrongFactionSlowdown * scale;
        }

        mod *= FactionBalanceSystem.GrowthBias(faction);
        if (FactionBalanceSystem.IsCoalitionTarget(faction))
            mod *= 0.85f;
        return Mathf.Clamp(mod, 0.35f, 1.15f);
    }

    FactionController StrongestOther(FactionController faction)
    {
        FactionController best = null;
        float bestStrength = float.NegativeInfinity;
        for (int i = 0; i < _factions.Count; i++)
        {
            FactionController candidate = _factions[i];
            if (candidate == null || candidate == faction)
                continue;
            if (candidate.Strength > bestStrength)
            {
                bestStrength = candidate.Strength;
                best = candidate;
            }
        }
        return best;
    }

    void OnGUI()
    {
        if (!_started)
            return;

        if (useBarEconomyMode)
            DrawBarSpectatorScoreboard();

        if (!debugOverlay)
            return;

        GUILayout.BeginArea(new Rect(12f, 12f, 520f, 640f), GUI.skin.box);
        GUILayout.Label($"Warfare: {FactionRegistry.WarfareEnabled}  Difference: {CurrentStrengthDifference:0}  " +
                        $"Resource nodes: {FactionRegistry.Resources.Count}  Towns: {FactionRegistry.Towns.Count}  " +
                        $"Units: {(Stargrave.Rts2.Rts2UnitSim.HasInstance ? Stargrave.Rts2.Rts2UnitSim.Instance.AliveCount : 0)}");
        if (useBarEconomyMode && TerritorySystem.HasInstance && TerritorySystem.Instance.IsReady)
        {
            TerritorySystem terr = TerritorySystem.Instance;
            int owned = 0;
            int reserved = 0;
            for (int p = 0; p < terr.Pockets.Count; p++)
            {
                if (terr.Pockets[p].ownerFactionId >= 0)
                    owned++;
                else if (terr.Pockets[p].reservedFactionId >= 0)
                    reserved++;
            }
            GUILayout.Label($"Territory: pockets {terr.Pockets.Count}  owned {owned}  reserved {reserved}  " +
                            $"(markers: grey=free, yellow=building, faction=held, orange=contested border)");
        }
        if (FactionBalanceSystem.HasActiveCoalition && FactionBalanceSystem.CoalitionTarget != null)
            GUILayout.Label($"Coalition vs: {FactionBalanceSystem.CoalitionTarget.DisplayName}");
        for (int i = 0; i < _factions.Count; i++)
        {
            FactionController faction = _factions[i];
            if (faction == null)
                continue;
            GUILayout.Label($"{faction.DisplayName} [{faction.State}] " +
                            $"W:{faction.WorkerCount} I:{faction.InfantryCount} A:{faction.ArcherCount} " +
                            $"N:{faction.NobleCount} M:{faction.MerchantCount} " +
                            $"Mex:{faction.MexCount} E:{faction.EnergyGenCount} " +
                            $"Pockets:{(TerritorySystem.HasInstance ? TerritorySystem.Instance.CountOwnedPockets(faction.RuntimeIndex) : 0)} " +
                            $"gathered W:{faction.WoodGathered} S:{faction.StoneGathered} " +
                            $"stock W:{faction.Wood} S:{faction.Stone} G:{faction.Gold} " +
                            $"Strength:{faction.Strength:0} Threat:{FactionBalanceSystem.GetThreat(faction):0.00} " +
                            $"Growth:{GetGrowthModifier(faction):0.00}");
            BuildingConstructionSite site = faction.ActiveConstruction;
            if (site != null && !site.IsComplete)
            {
                GUILayout.Label($"  {site.Kind} W:{site.DeliveredWood}/{site.Cost.wood} " +
                                $"S:{site.DeliveredStone}/{site.Cost.stone}");
            }

            for (int j = 0; j < _factions.Count; j++)
            {
                FactionController other = _factions[j];
                if (other == null || other == faction)
                    continue;
                GUILayout.Label($"  vs {other.DisplayName} trade:{FactionTradeSystem.GetRelation(faction, other):0.00}");
            }

            IReadOnlyList<FactionNpc> workers = faction.Workers;
            for (int w = 0; w < workers.Count; w++)
            {
                FactionNpc npc = workers[w];
                if (npc == null || npc.IsDead || npc.Worker == null)
                    continue;
                GUILayout.Label($"  Worker {w}: {npc.Worker.DebugStatus}");
            }

            IReadOnlyList<FactionNpc> members = faction.Members;
            int merchantIndex = 0;
            for (int m = 0; m < members.Count; m++)
            {
                FactionNpc npc = members[m];
                if (npc == null || npc.IsDead || npc.Role != FactionNpcRole.Merchant || npc.Merchant == null)
                    continue;
                GUILayout.Label($"  Merchant {merchantIndex}: {npc.Merchant.DebugStatus}");
                merchantIndex++;
            }
        }

        IReadOnlyList<ClaimableTown> towns = FactionRegistry.Towns;
        for (int t = 0; t < towns.Count; t++)
        {
            ClaimableTown town = towns[t];
            if (town == null)
                continue;
            string owner = town.Owner != null ? town.Owner.DisplayName : "Neutral";
            GUILayout.Label($"Town {t}: {owner} loyalty:{town.Loyalty:0}");
        }
        GUILayout.EndArea();
    }

    void DrawBarSpectatorScoreboard()
    {
        int n = _factions.Count;
        if (n <= 0)
            return;

        // Score ≈ pockets*2 + army + metal/100 — readable “who’s winning” without sim math.
        int[] order = new int[n];
        int[] scores = new int[n];
        int live = 0;
        for (int i = 0; i < n; i++)
        {
            FactionController f = _factions[i];
            if (f == null)
                continue;
            int pockets = TerritorySystem.HasInstance
                ? TerritorySystem.Instance.CountOwnedPockets(f.RuntimeIndex)
                : 0;
            int army = f.SoldierCount;
            int metal = f.Metal;
            scores[live] = pockets * 2 + army + metal / 100;
            order[live] = i;
            live++;
        }
        if (live <= 0)
            return;

        for (int a = 1; a < live; a++)
        {
            int keyOrder = order[a];
            int keyScore = scores[a];
            int b = a - 1;
            while (b >= 0 && scores[b] < keyScore)
            {
                order[b + 1] = order[b];
                scores[b + 1] = scores[b];
                b--;
            }
            order[b + 1] = keyOrder;
            scores[b + 1] = keyScore;
        }

        float width = 340f;
        float height = 28f + live * 22f + 8f;
        Rect area = new Rect(Screen.width - width - 12f, 12f, width, height);
        GUILayout.BeginArea(area, GUI.skin.box);
        GUILayout.Label("WAR STANDINGS  (pockets / army / metal)");
        for (int r = 0; r < live; r++)
        {
            FactionController f = _factions[order[r]];
            int pockets = TerritorySystem.HasInstance
                ? TerritorySystem.Instance.CountOwnedPockets(f.RuntimeIndex)
                : 0;
            string crown = r == 0 ? "#1 " : $"{r + 1}. ";
            string flag = "";
            if (!f.HasFoundedCampus)
                flag = "  UNFOUNDED";
            else if (f.IsBarAssaultProtected)
            {
                float left = f.BarProtectSecondsRemaining;
                if (f.State == FactionState.Recovering)
                    flag = "  REBUILD";
                else if (f.State == FactionState.Retreating)
                    flag = "  RETREAT";
                else if (left > 0.5f)
                    flag = $"  PROT {left:0}s";
                else if (f.IsInBarAssaultGrace)
                    flag = "  GRACE";
                else
                    flag = "  PROT";
            }
            else if (f.State == FactionState.Attacking || f.State == FactionState.Fighting)
                flag = $"  FRONT{f.AssaultFrontStage}";
            Color prev = GUI.color;
            GUI.color = Color.Lerp(f.UiColor, Color.white, 0.25f);
            GUILayout.Label(
                $"{crown}{f.DisplayName}  [{f.State}]{flag}  " +
                $"Pk:{pockets}  R:{f.InfantryCount} H:{f.ArcherCount}  " +
                $"Army:{f.SoldierCount}  M:{f.Metal}  E:{f.Energy}");
            GUI.color = prev;
        }
        GUILayout.EndArea();
    }
}
