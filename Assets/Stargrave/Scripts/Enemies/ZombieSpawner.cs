using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One weighted zombie prefab entry for multi-type spawning.
/// </summary>
[System.Serializable]
public class ZombieSpawnVariant
{
    public string name;
    public GameObject prefab;
    [Tooltip("Relative spawn chance. Higher = more common.")]
    [Min(0f)] public float weight = 1f;
}

/// <summary>
/// Stargrave 1.3-style population + dry-surface spawn (with <see cref="PlanetSurfaceSampler"/>).
/// Supports multiple zombie types via <see cref="variants"/> (weighted); falls back to <see cref="zombiePrefab"/>.
/// </summary>
public class ZombieSpawner : MonoBehaviour
{
    public static ZombieSpawner Instance { get; private set; }

    [Tooltip("Legacy single prefab. Used when Variants is empty, and as a fallback entry.")]
    public GameObject zombiePrefab;

    [Header("Zombie types")]
    [Tooltip("Weighted list of zombie prefabs (different speeds/health/looks). Empty = use Zombie Prefab only.")]
    public List<ZombieSpawnVariant> variants = new List<ZombieSpawnVariant>();

    public int SteadyPopulationTarget => Mathf.Clamp(zombieCount, 0, Mathf.Max(1, maxAliveZombies));

    [Header("Population")]
    [Min(0)] public int zombieCount = 16;
    [Min(1)]
    [Tooltip("Hard cap for the escalating horde. Kills spawn extras until this many are alive.")]
    public int maxAliveZombies = 10000;
    [Min(0.2f)] public float maintainCheckIntervalSeconds = 3f;
    [Min(1)]
    [Tooltip("Avoid spawning every missing zombie in one frame.")]
    public int maxSpawnsPerMaintainTick = 2;
    [Tooltip("Startup burst spreads zombies over the planet.")]
    public bool initialSpawnGlobal = true;
    [Tooltip("Top-up and respawn bias to the opposite hemisphere from the player.")]
    public bool topUpOppositePlayer = true;
    [Range(0f, 80f)] public float oppositeSpawnConeAngleDegrees = 22f;
    [Min(0f)] public float respawnDelaySeconds = 2f;
    [Min(1)]
    [Tooltip("How many horde agents spawn after each kill. 10 = the swarm grows fast until the 10k cap.")]
    public int respawnsPerKill = 10;
    [Tooltip("Extra world units above sea level required for a valid spawn (keeps feet dry).")]
    [Min(0f)] public float spawnDryClearance = 1.25f;
    [Min(1f)] public float spawnDistanceFromPlayer = 26f;
    [Range(1f, 90f)] public float spawnConeAngleDegrees = 40f;
    [Min(1)]
    [Tooltip("Ray attempts for initial spawn burst (same as legacy field name maxAttempts).")]
    public int maxAttempts = 12;
    [Min(1)] public int maintainSpawnAttempts = 8;
    [Tooltip("Spawn position offset along surface normal (same idea as old heightAboveSurface).")]
    public float heightAboveSurface = 1f;
    public float fallbackShellRadius = 52f;

    [Header("Planet (optional override)")]
    public Transform planet;
    public LayerMask groundMask = ~0;

    [Header("Respawn")]
    public bool respawnOnKill = true;

    [Header("Layers")]
    [Tooltip("If set, spawned instances get this layer (e.g. no collision with water).")]
    public string zombieLayerName = "Zombie";

    Transform _cachedPlanet;
    MeshCollider _cachedPlanetCollider;
    Planet _cachedPlanetComp;
    PlanetOceanLayer _cachedOcean;
    float _nextPlanetCacheTime;
    bool _initialSpawnComplete;
    int _zombieLayer = -1;

    const int HardMaxAlive = 10000;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        ClampPopulationSettings();
        if (GetComponent<ZombieHordeSim>() == null)
            gameObject.AddComponent<ZombieHordeSim>();
    }

    void OnValidate()
    {
        ClampPopulationSettings();
    }

    void ClampPopulationSettings()
    {
        maxAliveZombies = Mathf.Clamp(maxAliveZombies, 1, HardMaxAlive);
        zombieCount = Mathf.Clamp(zombieCount, 0, maxAliveZombies);
        respawnsPerKill = Mathf.Clamp(respawnsPerKill, 1, 10);
        maxSpawnsPerMaintainTick = Mathf.Clamp(maxSpawnsPerMaintainTick, 1, 4);
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    void Start()
    {
        _zombieLayer = LayerMask.NameToLayer(zombieLayerName);
        var horde = GetComponent<ZombieHordeSim>();
        if (horde != null)
            horde.Configure(this);
        if (PickPrefab() == null)
        {
            Debug.LogWarning("ZombieSpawner: no zombie prefab / variants assigned.");
            _initialSpawnComplete = true;
            return;
        }

        _initialSpawnComplete = false;
        StartCoroutine(InitialSpawnBurstWhenPlanetReady());
        StartCoroutine(MaintainPopulationLoop());
    }

    /// <summary>Weighted pick from variants, else zombiePrefab.</summary>
    public GameObject PickPrefab()
    {
        float total = 0f;
        if (variants != null)
        {
            for (int i = 0; i < variants.Count; i++)
            {
                var v = variants[i];
                if (v != null && v.prefab != null && v.weight > 0f)
                    total += v.weight;
            }
        }

        if (total > 0f)
        {
            float r = Random.Range(0f, total);
            float acc = 0f;
            for (int i = 0; i < variants.Count; i++)
            {
                var v = variants[i];
                if (v == null || v.prefab == null || v.weight <= 0f)
                    continue;
                acc += v.weight;
                if (r <= acc)
                    return v.prefab;
            }
        }

        return zombiePrefab;
    }

    /// <summary>Clears all zombies and re-runs the initial burst (e.g. after game over without scene reload).</summary>
    public void HardResetPopulationToInitial()
    {
        StopAllCoroutines();
        StartCoroutine(CoHardResetPopulationToInitial());
    }

    IEnumerator CoHardResetPopulationToInitial()
    {
        _initialSpawnComplete = false;
        if (ZombieHordeSim.Instance != null)
            ZombieHordeSim.Instance.Clear();
        ZombieAI[] zombies = Object.FindObjectsByType<ZombieAI>(FindObjectsInactive.Exclude);
        for (int i = 0; i < zombies.Length; i++)
        {
            if (zombies[i] == null)
                continue;
            if (ZombieHordeSim.HasInstance && ZombieHordeSim.Instance.Owns(zombies[i]))
                continue;
            Destroy(zombies[i].gameObject);
        }

        yield return null;
        ZombieAI.RecalculateLivingCountFromScene();

        yield return StartCoroutine(InitialSpawnBurstWhenPlanetReady());
        StartCoroutine(MaintainPopulationLoop());
    }

    IEnumerator InitialSpawnBurstWhenPlanetReady()
    {
        if (planet == null)
        {
            var g = GameObject.FindGameObjectWithTag("Planet");
            if (g != null)
                planet = g.transform;
            if (planet == null)
            {
                var p = Object.FindAnyObjectByType<Planet>();
                if (p != null)
                    planet = p.transform;
            }
        }

        if (planet == null)
        {
            Debug.LogError("ZombieSpawner: No planet (tag Planet or assign Planet transform).");
            _initialSpawnComplete = true;
            yield break;
        }

        _cachedPlanetComp = planet.GetComponent<Planet>();
        if (_cachedPlanetComp != null)
        {
            while (!_cachedPlanetComp.IsGenerated)
                yield return null;
        }

        yield return null;
        yield return null;

        int target = Mathf.Clamp(zombieCount, 0, Mathf.Max(1, maxAliveZombies));
        for (int i = 0; i < target; i++)
        {
            GameObject prefab = PickPrefab();
            if (prefab == null)
                break;
            Vector3 preferredDirection = initialSpawnGlobal ? Random.onUnitSphere : GetSpawnDirectionNearPlayer(GetPlanetCenter());
            TrySpawnOne(prefab, preferredDirection, maxAttempts);
            yield return null;
        }

        _initialSpawnComplete = true;
    }

    IEnumerator MaintainPopulationLoop()
    {
        while (isActiveAndEnabled && !_initialSpawnComplete)
            yield return null;

        WaitForSeconds wait = new WaitForSeconds(Mathf.Max(0.2f, maintainCheckIntervalSeconds));
        int steadyTarget = Mathf.Clamp(zombieCount, 0, Mathf.Max(1, maxAliveZombies));
        while (isActiveAndEnabled)
        {
            if (PickPrefab() != null)
            {
        int alive = ZombieHordeSim.HasInstance ? ZombieHordeSim.Instance.Alive : ZombieAI.LivingCount;
                int needed = Mathf.Max(0, steadyTarget - alive);
                int spawnThisTick = Mathf.Min(needed, maxSpawnsPerMaintainTick);
                for (int i = 0; i < spawnThisTick; i++)
                {
                    GameObject prefab = PickPrefab();
                    if (prefab == null)
                        break;
                    Vector3 preferredDirection = topUpOppositePlayer
                        ? GetSpawnDirectionOppositePlayer(GetPlanetCenter())
                        : GetSpawnDirectionNearPlayer(GetPlanetCenter());
                    TrySpawnOne(prefab, preferredDirection, maintainSpawnAttempts);
                    yield return null;
                }
            }
            yield return wait;
        }
    }

    /// <summary>
    /// Called when a zombie dies. Schedules respawn; the corpse handles its own fall/sink despawn.
    /// </summary>
    public void OnZombieKilled(ZombieAI zombie)
    {
        if (!isActiveAndEnabled || zombie == null)
            return;

        GameObject prefabToSpawn = PickPrefab();

        if (respawnOnKill && prefabToSpawn != null)
            StartCoroutine(RespawnAfterDelay(prefabToSpawn, Mathf.Max(0f, respawnDelaySeconds), Mathf.Max(1, respawnsPerKill)));
    }

    IEnumerator RespawnAfterDelay(GameObject prefab, float delay, int spawnCount)
    {
        spawnCount = Mathf.Max(1, spawnCount);
        if (delay > 0f)
            yield return new WaitForSeconds(delay);

        for (int s = 0; s < spawnCount; s++)
        {
            if (ZombieHordeSim.HasInstance && ZombieHordeSim.Instance.Alive >= maxAliveZombies)
                yield break;
            // Kill respawns always go to the opposite hemisphere on dry land (or nearest dry land there).
            Vector3 preferredDirection = GetSpawnDirectionOppositePlayer(GetPlanetCenter());
            TrySpawnOne(prefab, preferredDirection, Mathf.Max(maintainSpawnAttempts, 24));
            yield return null;
        }
    }

    void RefreshPlanetCache(bool force = false)
    {
        if (!force && Time.time < _nextPlanetCacheTime && _cachedPlanet != null)
            return;

        _nextPlanetCacheTime = Time.time + 0.35f;
        if (planet != null)
            _cachedPlanet = planet;
        else
            _cachedPlanet = PlanetReferenceResolver.ResolvePlanetTransform();

        if (_cachedPlanet == null)
        {
            _cachedPlanetCollider = null;
            _cachedPlanetComp = null;
            _cachedOcean = null;
            return;
        }

        _cachedPlanetComp = _cachedPlanet.GetComponent<Planet>();
        _cachedOcean = _cachedPlanet.GetComponent<PlanetOceanLayer>();
        if (_cachedOcean == null)
            _cachedOcean = Object.FindAnyObjectByType<PlanetOceanLayer>();
        _cachedPlanetCollider = ZombieAI.ResolvePrimaryTerrainMeshCollider(_cachedPlanet);
    }

    void TrySpawnOne(GameObject prefab, Vector3 preferredDirection, int raycastAttempts)
    {
        if (prefab == null)
            return;

        var horde = ZombieHordeSim.Instance ?? GetComponent<ZombieHordeSim>();
        if (horde != null && horde.Alive >= Mathf.Min(maxAliveZombies, ZombieHordeSim.MaxAgents))
            return;

        RefreshPlanetCache(false);
        if (_cachedPlanet == null)
        {
            Debug.LogWarning("ZombieSpawner: No planet — cannot spawn.");
            return;
        }

        Vector3 center = _cachedPlanet.position;

        Vector3 spawnPos = PlanetSurfaceSampler.GetDrySurfacePosition(
            preferredDirection,
            center,
            _cachedPlanetCollider,
            _cachedPlanetComp,
            _cachedOcean,
            groundMask,
            Mathf.Max(1, raycastAttempts),
            heightAboveSurface,
            fallbackShellRadius,
            spawnDryClearance);

        if (horde != null)
        {
            horde.TryAddAgent(spawnPos, 0);
            return;
        }

        Quaternion rot = Quaternion.identity;
        Vector3 up = (spawnPos - center).normalized;
        if (up.sqrMagnitude > 1e-6f)
            rot = Quaternion.FromToRotation(Vector3.up, up);

        GameObject z = Instantiate(prefab, spawnPos, rot);
        z.name = prefab.name + "_" + Random.Range(1000, 99999);
        if (_zombieLayer >= 0)
            z.layer = _zombieLayer;

        if (z.GetComponent<ZombieVisibilityCuller>() == null)
            z.AddComponent<ZombieVisibilityCuller>();
    }

    Vector3 GetPlanetCenter()
    {
        RefreshPlanetCache(true);
        return _cachedPlanet != null ? _cachedPlanet.position : Vector3.zero;
    }

    Vector3 GetSpawnDirectionOppositePlayer(Vector3 planetCenter)
    {
        Transform player = RuntimeSceneRefs.GetPlayerTransform();
        if (player == null)
            return Random.onUnitSphere;

        Vector3 playerRadial = (player.position - planetCenter).normalized;
        if (playerRadial.sqrMagnitude < 1e-6f)
            return Random.onUnitSphere;

        Vector3 opposite = -playerRadial;
        float cone = Mathf.Max(0f, oppositeSpawnConeAngleDegrees);
        if (cone <= 0.01f)
            return opposite;

        Vector3 reference = Mathf.Abs(Vector3.Dot(opposite, Vector3.up)) > 0.95f ? Vector3.right : Vector3.up;
        Vector3 tangentA = Vector3.Cross(opposite, reference).normalized;
        Vector3 tangentB = Vector3.Cross(opposite, tangentA).normalized;

        float angle = Random.Range(0f, cone);
        float yaw = Random.Range(0f, 360f);
        Vector3 axis = (Mathf.Cos(yaw * Mathf.Deg2Rad) * tangentA + Mathf.Sin(yaw * Mathf.Deg2Rad) * tangentB).normalized;
        return Quaternion.AngleAxis(angle, axis) * opposite;
    }

    Vector3 GetSpawnDirectionNearPlayer(Vector3 planetCenter)
    {
        Transform player = RuntimeSceneRefs.GetPlayerTransform();
        if (player == null)
            return Random.onUnitSphere;

        Vector3 playerRadial = (player.position - planetCenter).normalized;
        if (playerRadial.sqrMagnitude < 1e-6f)
            return Random.onUnitSphere;

        Vector3 reference = Mathf.Abs(Vector3.Dot(playerRadial, Vector3.up)) > 0.95f ? Vector3.right : Vector3.up;
        Vector3 tangentA = Vector3.Cross(playerRadial, reference).normalized;
        Vector3 tangentB = Vector3.Cross(playerRadial, tangentA).normalized;

        float arcRadius = Mathf.Max(0.1f, spawnDistanceFromPlayer);
        float planetRadius = Mathf.Max(1f, Vector3.Distance(player.position, planetCenter));
        float maxAngle = Mathf.Min(spawnConeAngleDegrees, arcRadius / planetRadius * Mathf.Rad2Deg);
        float angle = Random.Range(0f, Mathf.Max(1f, maxAngle));
        float yaw = Random.Range(0f, 360f);

        Vector3 axis = (Mathf.Cos(yaw * Mathf.Deg2Rad) * tangentA + Mathf.Sin(yaw * Mathf.Deg2Rad) * tangentB).normalized;
        return Quaternion.AngleAxis(angle, axis) * playerRadial;
    }
}
