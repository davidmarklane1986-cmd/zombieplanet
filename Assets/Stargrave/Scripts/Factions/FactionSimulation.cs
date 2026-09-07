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

    [Header("Runtime")]
    public bool autoStart = true;
    public bool debugOverlay = true;
    public bool verboseEvents = true;
    [Min(0.1f)] public float directorTickInterval = 1f;
    [Min(0.1f)] public float spawnSearchRadius = 90f;
    [Min(20f)] public float minimumFactionSeparation = 180f;
    [Min(4)] public int spawnSearchAttempts = 96;

    [Header("Existing scene references (optional auto-resolved)")]
    public Planet planet;
    public FoliageByColour foliage;
    public BuildingSpawner buildingSpawner;

    [Header("Existing visual assets")]
    public PlayableCharacterDef workerCharacter;
    public PlayableCharacterDef soldierCharacter;
    public GameObject townHallPrefab;
    public GameObject barracksPrefab;

    [Header("Faction data")]
    public FactionDefinition[] factionDefinitions;
    public FactionDefinition factionADefinition;
    public FactionDefinition factionBDefinition;
    [Range(2, 8)] public int minFactionCount = 2;
    [Range(2, 8)] public int maxFactionCount = 4;
    public FactionEconomySettings economy = new FactionEconomySettings();
    public FactionCombatSettings combat = new FactionCombatSettings();
    public FactionWarfareSettings warfare = new FactionWarfareSettings();

    readonly List<FactionController> _factions = new List<FactionController>(8);
    readonly List<Vector3> _foliageStreamPoints = new List<Vector3>(32);
    float _nextFoliageStream;
    readonly List<Vector3> _woodProspects = new List<Vector3>(256);
    readonly List<Vector3> _stoneProspects = new List<Vector3>(256);
    float _nextTick;
    float _nextStartCheck;
    bool _started;
    bool _subscribedPlanet;
    bool _bothReachedEngagement;
    float _lastLoggedBalance;

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
            TryStart();
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
        TryStart();
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
        if (workerCharacter != null && soldierCharacter != null)
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
        }

        for (int i = 0; i < defs.Length; i++)
        {
            if (defs[i] == null || defs[i].characterPrefab == null)
                continue;
            if (workerCharacter == null)
                workerCharacter = defs[i];
            if (soldierCharacter == null)
                soldierCharacter = defs[i];
        }
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
    }

    public void TryStart()
    {
        if (_started || !autoStart || planet == null || !planet.IsGenerated)
            return;

        ResolveSceneReferences();
        if (!_subscribedPlanet)
        {
            Planet.OnPlanetReady += OnPlanetReady;
            _subscribedPlanet = true;
        }

        FoliageResourceAdapter.EnsureExists(foliage);
        BuildResourceProspectAtlas();
        CreateFactions();
        _started = true;
        _nextTick = Time.time + directorTickInterval;
    }

    void CreateFactions()
    {
        if (_factions.Count > 0)
            return;

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
        if (verboseEvents)
            Debug.Log($"[FactionSimulation] Spawned {_factions.Count} factions.", this);
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
            if (!TryFindDryAxis(Random.onUnitSphere, out Vector3 axis))
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
            faction.RememberStreamFocus(prospect, true);
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

    bool TryFindSeparatedSpawnAxes(int wanted, List<Vector3> axes)
    {
        axes.Clear();
        if (planet == null || wanted < 2)
            return false;

        int attempts = Mathf.Max(128, spawnSearchAttempts * Mathf.Max(4, wanted));
        for (int i = 0; i < attempts && axes.Count < wanted; i++)
        {
            Vector3 preferred = i == 0 ? planet.transform.right : Random.onUnitSphere;
            if (!TryFindMainlandSpawnAxis(preferred, out Vector3 axis))
                continue;
            if (!IsFarFromChosen(axis, axes))
                continue;
            axes.Add(axis.normalized);
        }

        return axes.Count >= 2;
    }

    bool IsFarFromChosen(Vector3 axis, List<Vector3> chosen)
    {
        Vector3 pos = planet.GetSurfacePointWorld(axis);
        for (int i = 0; i < chosen.Count; i++)
        {
            float distance = Vector3.Distance(pos, planet.GetSurfacePointWorld(chosen[i]));
            if (distance < minimumFactionSeparation)
                return false;
        }
        return true;
    }

    bool TryFindDryAxis(Vector3 preferredAxis, out Vector3 axis)
    {
        axis = default;
        if (planet == null)
            return false;

        MeshCollider collider = ZombieAI.ResolvePrimaryTerrainMeshCollider(planet.transform);
        PlanetOceanLayer ocean = planet.GetComponent<PlanetOceanLayer>();
        Vector3 position = PlanetSurfaceSampler.GetDrySurfacePosition(
            preferredAxis,
            planet.transform.position,
            collider,
            planet,
            ocean,
            ~0,
            Mathf.Max(32, spawnSearchAttempts),
            0.1f,
            planet.GetBaseRadiusWorld(),
            1.25f);
        axis = (position - planet.transform.position).normalized;
        return axis.sqrMagnitude > 1e-6f &&
               (ocean == null || ocean.GetDepthBelowSurface(position) <= 0f);
    }

    public bool TryFindMainlandSpawnAxis(Vector3 preferredAxis, out Vector3 axis)
    {
        axis = default;
        if (planet == null)
            return false;

        int attempts = Mathf.Max(128, spawnSearchAttempts * 4);
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
            axis = dry;
            return true;
        }

        return false;
    }

    void CreateFaction(int index, string runtimeId, FactionDefinition definition, Vector3 axis, string fallbackName)
    {
        var go = new GameObject(fallbackName);
        go.transform.SetParent(transform, false);
        var faction = go.AddComponent<FactionController>();
        faction.Initialize(this, index, runtimeId, definition, axis, fallbackName);
        _factions.Add(faction);
        FactionRegistry.RegisterFaction(faction);
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
        if (Time.time < _nextTick)
            return;

        float dt = Mathf.Max(0.01f, Time.time - (_nextTick - directorTickInterval));
        _nextTick = Time.time + directorTickInterval;
        for (int i = 0; i < _factions.Count; i++)
        {
            if (_factions[i] != null)
                _factions[i].SimulationTick(dt);
        }

        if (Time.time >= _nextFoliageStream)
            RefreshFactionFoliageStreaming();

        EvaluateEngagement();
        EvaluateBalance();
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

        FactionController other = StrongestOther(faction);
        if (other == null || faction.Strength <= other.Strength)
            return 1f;

        float threshold = Mathf.Max(1f, other.Strength * (1f + warfare.strengthBalanceBuffer));
        float excess = Mathf.Max(0f, faction.Strength - threshold);
        float scale = Mathf.Clamp01(excess / Mathf.Max(1f, warfare.minimumEngagementStrength));
        return 1f - warfare.maximumStrongFactionSlowdown * scale;
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
        if (!debugOverlay || !_started)
            return;

        GUILayout.BeginArea(new Rect(12f, 12f, 520f, 640f), GUI.skin.box);
        GUILayout.Label($"Warfare: {FactionRegistry.WarfareEnabled}  Difference: {CurrentStrengthDifference:0}  " +
                        $"Resource nodes: {FactionRegistry.Resources.Count}");
        for (int i = 0; i < _factions.Count; i++)
        {
            FactionController faction = _factions[i];
            if (faction == null)
                continue;
            GUILayout.Label($"{faction.DisplayName} [{faction.State}] " +
                            $"W:{faction.WorkerCount} S:{faction.SoldierCount} " +
                            $"gathered W:{faction.WoodGathered} S:{faction.StoneGathered} " +
                            $"stock W:{faction.Wood} S:{faction.Stone} " +
                            $"sites W:{faction.GetResourceSiteCount(FactionResourceType.Wood)} " +
                            $"S:{faction.GetResourceSiteCount(FactionResourceType.Stone)} " +
                            $"Strength:{faction.Strength:0} " +
                            $"Growth:{GetGrowthModifier(faction):0.00}");
            BuildingConstructionSite site = faction.ActiveConstruction;
            if (site != null && !site.IsComplete)
            {
                GUILayout.Label($"  {site.Kind} W:{site.DeliveredWood}/{site.Cost.wood} " +
                                $"S:{site.DeliveredStone}/{site.Cost.stone}");
            }

            IReadOnlyList<FactionNpc> workers = faction.Workers;
            for (int w = 0; w < workers.Count; w++)
            {
                FactionNpc npc = workers[w];
                if (npc == null || npc.IsDead || npc.Worker == null)
                    continue;
                GUILayout.Label($"  Worker {w}: {npc.Worker.DebugStatus}");
            }
        }
        GUILayout.EndArea();
    }
}
