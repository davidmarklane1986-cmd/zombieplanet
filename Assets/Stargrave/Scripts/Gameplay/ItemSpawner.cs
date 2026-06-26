using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Stargrave 1.3-style runtime power-up spawner. Keeps a small number of pickups alive near the player and
/// chooses from the original health / speed / rapid-fire mix.
/// </summary>
public sealed class ItemSpawner : MonoBehaviour
{
    public static ItemSpawner Instance { get; private set; }

    [Header("Spawn Setup")]
    public GameObject pickupPrefab;
    [Tooltip("How many power-ups can exist at once.")]
    public int maxActiveItems = 1;
    [Tooltip("First spawn after this many seconds.")]
    public float initialSpawnDelay = 2f;
    [Tooltip("Random delay range between spawn attempts.")]
    public float minSpawnIntervalSeconds = 30f;
    [Tooltip("Random delay range between spawn attempts.")]
    public float maxSpawnIntervalSeconds = 60f;
    [Tooltip("How long an uncollected pickup stays in the world.")]
    public float pickupLifetimeSeconds = 15f;
    [Tooltip("Approximate dry-surface distance from the player.")]
    public float spawnDistanceFromPlayer = 20f;
    [Range(1f, 90f)] public float spawnConeAngleDegrees = 35f;
    public int maxSpawnRayAttempts = 24;

    [Header("Pickup Mix")]
    [Range(0f, 1f)] public float healthPickupChance = 0.4f;
    [Range(0f, 1f)] public float rapidFirePickupChance = 0.3f;
    [Range(0f, 1f)] public float speedPickupChance = 0.3f;

    [Header("Health Restore")]
    [Range(0f, 1f)] public float healthRestoreFraction = 0.25f;
    [Min(1)] public int minimumHealthRestore = 25;

    [Header("Rapid Fire")]
    public float rapidFireDurationSeconds = 20f;
    [Tooltip("Higher means shorter cooldown while Rapid Fire is active.")]
    public float rapidFireFireRateMultiplier = 3.5f;

    [Header("Speed Boost")]
    public float speedBoostDurationSeconds = 12f;
    public float speedBoostMultiplier = 2f;

    readonly List<PowerUpPickup> _activeItems = new List<PowerUpPickup>();
    Transform _player;
    bool _warnedMissingPrefab;
    bool _warnedNoPlanet;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void CreateSpawnerIfMissing()
    {
        if (Instance != null)
            return;
        if (FindFirstObjectByType<ZombieSpawner>(FindObjectsInactive.Include) == null)
            return;

        var go = new GameObject("ItemSpawner");
        go.AddComponent<ItemSpawner>();
    }

    void Start()
    {
        StartCoroutine(CoSpawnLoop());
    }

    IEnumerator CoSpawnLoop()
    {
        Planet planet = FindFirstObjectByType<Planet>(FindObjectsInactive.Exclude);
        float waitBudget = 6f;
        while (planet != null && !planet.IsGenerated && waitBudget > 0f)
        {
            waitBudget -= Time.deltaTime;
            yield return null;
        }

        yield return new WaitForSeconds(Mathf.Max(0f, initialSpawnDelay));

        while (true)
        {
            CleanupDeadItems();
            if (_activeItems.Count < Mathf.Max(1, maxActiveItems))
                SpawnSingleItem();

            float minDelay = Mathf.Max(0.1f, minSpawnIntervalSeconds);
            float maxDelay = Mathf.Max(minDelay, maxSpawnIntervalSeconds);
            yield return new WaitForSeconds(Random.Range(minDelay, maxDelay));
        }
    }

    public void ResetForNewRun()
    {
        for (int i = _activeItems.Count - 1; i >= 0; i--)
        {
            PowerUpPickup pickup = _activeItems[i];
            if (pickup != null)
                Destroy(pickup.gameObject);
        }

        _activeItems.Clear();
        SpawnSingleItem();
    }

    void SpawnSingleItem()
    {
        Vector3 position = GetDrySurfacePositionNearPlayer();

        GameObject go = pickupPrefab != null
            ? Instantiate(pickupPrefab, position, Quaternion.identity)
            : CreateFallbackPickup(position);
        if (go == null)
            return;

        PowerUpPickup pickup = go.GetComponent<PowerUpPickup>();
        if (pickup == null)
            pickup = go.AddComponent<PowerUpPickup>();

        ConfigurePickup(pickup);
        _activeItems.Add(pickup);
        StartCoroutine(DestroyPickupAfterDelay(pickup, Mathf.Max(0.5f, pickupLifetimeSeconds)));
    }

    GameObject CreateFallbackPickup(Vector3 position)
    {
        if (!_warnedMissingPrefab)
        {
            Debug.LogWarning("ItemSpawner: pickupPrefab is not assigned. Spawning fallback sphere pickups.");
            _warnedMissingPrefab = true;
        }

        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "StargravePowerUp";
        go.transform.position = position;
        go.transform.localScale = Vector3.one * 0.82f;

        Collider col = go.GetComponent<Collider>();
        if (col == null)
            col = go.AddComponent<SphereCollider>();
        col.isTrigger = true;

        return go;
    }

    void ConfigurePickup(PowerUpPickup pickup)
    {
        float total = Mathf.Max(0.001f, healthPickupChance + rapidFirePickupChance + speedPickupChance);
        float roll = Random.value * total;

        pickup.destroyOnPickup = true;
        pickup.respawnDelaySeconds = 0f;

        if (roll < healthPickupChance)
        {
            pickup.kind = PowerUpPickup.Kind.HealthPack;
            pickup.durationSeconds = 0f;
            pickup.multiplier = 1f;
            pickup.healAmount = Mathf.Max(1, minimumHealthRestore);
            pickup.healFractionOfMaxHealth = Mathf.Clamp01(healthRestoreFraction);
        }
        else if (roll < healthPickupChance + rapidFirePickupChance)
        {
            pickup.kind = PowerUpPickup.Kind.FireRateBoost;
            pickup.durationSeconds = Mathf.Max(0.1f, rapidFireDurationSeconds);
            pickup.multiplier = Mathf.Max(1f, rapidFireFireRateMultiplier);
            pickup.healFractionOfMaxHealth = 0f;
        }
        else
        {
            pickup.kind = PowerUpPickup.Kind.SpeedBoost;
            pickup.durationSeconds = Mathf.Max(0.1f, speedBoostDurationSeconds);
            pickup.multiplier = Mathf.Max(1f, speedBoostMultiplier);
            pickup.healFractionOfMaxHealth = 0f;
        }

        pickup.RefreshVisuals();
    }

    void CleanupDeadItems()
    {
        for (int i = _activeItems.Count - 1; i >= 0; i--)
        {
            if (_activeItems[i] == null)
                _activeItems.RemoveAt(i);
        }
    }

    IEnumerator DestroyPickupAfterDelay(PowerUpPickup pickup, float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (pickup != null)
            Destroy(pickup.gameObject);
    }

    Vector3 GetDrySurfacePositionNearPlayer()
    {
        Transform planetTransform = PlanetReferenceResolver.ResolvePlanetTransform();
        if (planetTransform == null)
            return GetFallbackSpawnNearPlayer();

        Planet planet = planetTransform.GetComponent<Planet>();
        PlanetWaterLayer water = planetTransform.GetComponentInChildren<PlanetWaterLayer>(true);
        MeshCollider planetCollider = ZombieAI.ResolvePrimaryTerrainMeshCollider(planetTransform);
        Vector3 center = planetTransform.position;
        Vector3 direction = GetSpawnDirectionNearPlayer(center);

        return PlanetSurfaceSampler.GetDrySurfacePosition(
            direction,
            center,
            planetCollider,
            water,
            planet,
            ~0,
            maxSpawnRayAttempts,
            1f,
            52f,
            -0.25f);
    }

    Vector3 GetSpawnDirectionNearPlayer(Vector3 planetCenter)
    {
        if (_player == null)
            _player = RuntimeSceneRefs.GetPlayerTransform(0.05f);

        if (_player == null)
            return Random.onUnitSphere;

        Vector3 playerRadial = (_player.position - planetCenter).normalized;
        if (playerRadial.sqrMagnitude < 1e-6f)
            playerRadial = Vector3.up;

        Vector3 reference = Mathf.Abs(Vector3.Dot(playerRadial, Vector3.up)) > 0.95f ? Vector3.right : Vector3.up;
        Vector3 tangentA = Vector3.Cross(playerRadial, reference).normalized;
        Vector3 tangentB = Vector3.Cross(playerRadial, tangentA).normalized;

        float arcRadius = Mathf.Max(0.1f, spawnDistanceFromPlayer);
        float planetRadius = Mathf.Max(1f, Vector3.Distance(_player.position, planetCenter));
        float maxAngle = Mathf.Min(spawnConeAngleDegrees, arcRadius / planetRadius * Mathf.Rad2Deg);
        float angle = Random.Range(0f, Mathf.Max(1f, maxAngle));
        float yaw = Random.Range(0f, 360f);

        Vector3 axis = (Mathf.Cos(yaw * Mathf.Deg2Rad) * tangentA + Mathf.Sin(yaw * Mathf.Deg2Rad) * tangentB).normalized;
        return Quaternion.AngleAxis(angle, axis) * playerRadial;
    }

    Vector3 GetFallbackSpawnNearPlayer()
    {
        if (!_warnedNoPlanet)
        {
            Debug.LogWarning("ItemSpawner: planet not found. Using fallback spawn around the player.");
            _warnedNoPlanet = true;
        }

        if (_player == null)
            _player = RuntimeSceneRefs.GetPlayerTransform(0.05f);

        if (_player == null)
            return transform.position + Random.onUnitSphere * 8f;

        Vector3 around = Random.onUnitSphere;
        around.y = Mathf.Abs(around.y) * 0.25f;
        around.Normalize();
        return _player.position + around * Mathf.Max(6f, spawnDistanceFromPlayer * 0.75f);
    }
}
