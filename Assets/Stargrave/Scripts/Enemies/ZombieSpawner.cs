using System.Collections;
using UnityEngine;

/// <summary>
/// Stargrave 1.3-style population + dry-surface spawn (with <see cref="PlanetSurfaceSampler"/>).
/// Keeps your zombie prefab; assign one with <see cref="ZombieAI"/> + Rigidbody (and GravityBody if you use planet gravity).
/// </summary>
public class ZombieSpawner : MonoBehaviour
{
    public static ZombieSpawner Instance { get; private set; }

    public GameObject zombiePrefab;

    public int SteadyPopulationTarget => Mathf.Clamp(zombieCount, 0, Mathf.Max(1, maxAliveZombies));

    [Header("Population")]
    [Min(0)] public int zombieCount = 8;
    [Min(1)]
    [Tooltip("Max simultaneous zombies from initial burst + maintain top-up.")]
    public int maxAliveZombies = 16;
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
    [Tooltip("How many zombies spawn after each kill.")]
    public int respawnsPerKill = 2;
    [Min(1f)] public float spawnDistanceFromPlayer = 26f;
    [Range(1f, 90f)] public float spawnConeAngleDegrees = 40f;
    [Min(1)]
    [Tooltip("Ray attempts for initial spawn burst (same as legacy field name maxAttempts).")]
    public int maxAttempts = 12;
    [Min(1)] public int maintainSpawnAttempts = 8;
    [Tooltip("Spawn position offset along surface normal (same idea as old heightAboveSurface).")]
    public float heightAboveSurface = 1f;
    public float fallbackShellRadius = 52f;
    public float underwaterEpsilon = -0.25f;

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
    PlanetWaterLayer _cachedWaterLayer;
    Planet _cachedPlanetComp;
    float _nextPlanetCacheTime;
    bool _initialSpawnComplete;
    int _zombieLayer = -1;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    void Start()
    {
        _zombieLayer = LayerMask.NameToLayer(zombieLayerName);
        if (zombiePrefab == null)
        {
            Debug.LogWarning("ZombieSpawner: zombiePrefab is not assigned.");
            _initialSpawnComplete = true;
            return;
        }

        _initialSpawnComplete = false;
        StartCoroutine(InitialSpawnBurstWhenPlanetReady());
        StartCoroutine(MaintainPopulationLoop());
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
        ZombieAI[] zombies = Object.FindObjectsByType<ZombieAI>(FindObjectsInactive.Exclude);
        for (int i = 0; i < zombies.Length; i++)
        {
            if (zombies[i] != null)
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
            Vector3 preferredDirection = initialSpawnGlobal ? Random.onUnitSphere : GetSpawnDirectionNearPlayer(GetPlanetCenter());
            TrySpawnOne(zombiePrefab, preferredDirection, maxAttempts);
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
            if (zombiePrefab != null)
            {
                int alive = ZombieAI.LivingCount;
                int needed = Mathf.Max(0, steadyTarget - alive);
                int spawnThisTick = Mathf.Min(needed, maxSpawnsPerMaintainTick);
                for (int i = 0; i < spawnThisTick; i++)
                {
                    Vector3 preferredDirection = topUpOppositePlayer
                        ? GetSpawnDirectionOppositePlayer(GetPlanetCenter())
                        : GetSpawnDirectionNearPlayer(GetPlanetCenter());
                    TrySpawnOne(zombiePrefab, preferredDirection, maintainSpawnAttempts);
                    yield return null;
                }
            }
            yield return wait;
        }
    }

    public void OnZombieKilled(ZombieAI zombie)
    {
        if (!isActiveAndEnabled || zombie == null)
            return;

        GameObject prefabToSpawn = zombiePrefab;
        Destroy(zombie.gameObject);

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
            if (ZombieAI.LivingCount >= maxAliveZombies)
                yield break;
            Vector3 preferredDirection = topUpOppositePlayer
                ? GetSpawnDirectionOppositePlayer(GetPlanetCenter())
                : GetSpawnDirectionNearPlayer(GetPlanetCenter());
            TrySpawnOne(prefab, preferredDirection, maintainSpawnAttempts);
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
            _cachedWaterLayer = null;
            _cachedPlanetComp = null;
            return;
        }

        _cachedPlanetComp = _cachedPlanet.GetComponent<Planet>();
        _cachedPlanetCollider = ZombieAI.ResolvePrimaryTerrainMeshCollider(_cachedPlanet);
        _cachedWaterLayer = _cachedPlanet.GetComponentInChildren<PlanetWaterLayer>(true);
    }

    void TrySpawnOne(GameObject prefab, Vector3 preferredDirection, int raycastAttempts)
    {
        if (prefab == null)
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
            _cachedWaterLayer,
            _cachedPlanetComp,
            groundMask,
            Mathf.Max(1, raycastAttempts),
            heightAboveSurface,
            fallbackShellRadius,
            underwaterEpsilon);

        Quaternion rot = Quaternion.identity;
        Vector3 up = (spawnPos - center).normalized;
        if (up.sqrMagnitude > 1e-6f)
            rot = Quaternion.FromToRotation(Vector3.up, up);

        GameObject z = Instantiate(prefab, spawnPos, rot);
        z.name = prefab.name + "_" + Random.Range(1000, 99999);
        if (_zombieLayer >= 0)
            z.layer = _zombieLayer;
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
