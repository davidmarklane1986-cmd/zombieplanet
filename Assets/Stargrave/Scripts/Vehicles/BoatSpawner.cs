using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns Kenny canoes along the shoreline (terrain near ocean radius).
/// </summary>
[DisallowMultipleComponent]
public sealed class BoatSpawner : MonoBehaviour
{
    public static BoatSpawner Instance { get; private set; }

    [Header("Prefab")]
    public GameObject boatPrefab;

    [Header("Placement")]
    [Min(0)] public int targetCount = 10;
    [Min(20f)] public float minSpacing = 55f;
    [Tooltip("Accept sites where terrain elevation is within this band of sea level (world units).")]
    [Min(1f)] public float shoreBand = 8f;
    [Tooltip("Nudge boats slightly into the water from the beach (world units along -shore normal).")]
    public float waterNudge = 2.5f;
    [Min(16)] public int maxAttempts = 220;
    public bool spawnOnPlanetReady = true;
    public bool clearBeforeSpawn = true;
    public string spawnedRootName = "SpawnedBoats";

    Transform _spawnRoot;
    readonly List<Vector3> _spawnedPositions = new List<Vector3>(32);
    bool _subscribed;
    bool _spawnedThisSession;
    static GameObject _runtimeBoatTemplate;


    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void TryCreateForGameScene()
    {
        if (Object.FindFirstObjectByType<BoatSpawner>() != null)
            return;
        if (Object.FindFirstObjectByType<Planet>(FindObjectsInactive.Exclude) == null)
            return;
        var go = new GameObject("BoatSpawner");
        go.AddComponent<BoatSpawner>();
    }
    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void OnEnable()
    {
        TrySubscribePlanet();
    }

    void OnDisable()
    {
        UnsubscribePlanet();
    }

    void Start()
    {
        TrySubscribePlanet();
        if (spawnOnPlanetReady && !_spawnedThisSession)
            SpawnBoats();
    }

    void TrySubscribePlanet()
    {
        if (_subscribed)
            return;
        // Planet has no universal ready event here — spawn in Start is enough for v1.
        _subscribed = true;
    }

    void UnsubscribePlanet()
    {
        _subscribed = false;
    }

    [ContextMenu("Spawn Boats Now")]
    public void SpawnBoats()
    {
        Planet planet = Object.FindFirstObjectByType<Planet>(FindObjectsInactive.Exclude);
        if (planet == null)
        {
            Debug.LogWarning("[BoatSpawner] No Planet found.");
            return;
        }

        if (boatPrefab == null)
            boatPrefab = Resources.Load<GameObject>("Vehicles/Boat_Canoe");
        if (boatPrefab == null)
            boatPrefab = EnsureRuntimeBoatTemplate();
        if (boatPrefab == null)
        {
            Debug.LogWarning("[BoatSpawner] No boatPrefab — run Tools/Stargrave/Build Boat Prefab.");
            return;
        }

        EnsureRoot();
        if (clearBeforeSpawn)
            ClearSpawned();

        var ocean = planet.GetComponent<PlanetOceanLayer>()
                    ?? planet.GetComponentInChildren<PlanetOceanLayer>(true);
        if (ocean == null)
        {
            Debug.LogWarning("[BoatSpawner] No PlanetOceanLayer — cannot place shoreline boats.");
            return;
        }

        float scale = PlanetBuildingPads.WorldScale(planet);
        var gen = new ShapeGenerator();
        if (planet.shapeSettings != null)
            gen.UpdateSettings(planet.shapeSettings);

        float oceanR = ocean.ResolveOceanRadiusWorld();
        Vector3 center = planet.transform.position;
        float minSepSq = minSpacing * minSpacing;
        int placed = 0;

        for (int attempt = 0; attempt < maxAttempts && placed < targetCount; attempt++)
        {
            Vector3 dir = Random.onUnitSphere;
            float elev = gen.CalculateNaturalUnscaledElevation(dir) * scale;
            float delta = elev - oceanR;
            // Beach: slightly above water, or just into the wet band.
            if (delta < -shoreBand * 0.35f || delta > shoreBand)
                continue;

            // Place on the water a short nudge seaward from the beach point.
            // Seaward = toward lower elevation: sample a small ring and pick lowest.
            Vector3 waterDir = FindSeawardDir(gen, scale, dir, oceanR);
            Vector3 pos = center + waterDir * (oceanR - Mathf.Max(0.2f, waterNudge * 0.15f));
            // Float depth: sit near surface.
            pos = center + waterDir * (oceanR - 0.2f);

            if (!IsFarEnough(pos, minSepSq))
                continue;

            Quaternion rot = BuildRadialRotation(waterDir, Random.Range(0f, 360f));
            GameObject instance = Instantiate(boatPrefab, pos, rot, _spawnRoot);
            instance.hideFlags = HideFlags.None;
            instance.SetActive(true);
            instance.name = $"Boat_{placed:00}";
            _spawnedPositions.Add(pos);
            placed++;
        }

        _spawnedThisSession = true;
        Debug.Log($"[BoatSpawner] Placed {placed}/{targetCount} boats along shoreline.");
    }

    static Vector3 FindSeawardDir(ShapeGenerator gen, float scale, Vector3 beachDir, float oceanR)
    {
        beachDir.Normalize();
        Vector3 t1 = Vector3.Cross(beachDir, Vector3.up);
        if (t1.sqrMagnitude < 1e-6f)
            t1 = Vector3.Cross(beachDir, Vector3.right);
        t1.Normalize();
        Vector3 t2 = Vector3.Cross(beachDir, t1);

        Vector3 best = beachDir;
        float bestElev = gen.CalculateNaturalUnscaledElevation(beachDir) * scale;
        const float ang = 0.04f;
        for (int i = 0; i < 8; i++)
        {
            float a = (Mathf.PI * 2f * i) / 8f;
            Vector3 d = (beachDir + (t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a)) * ang).normalized;
            float elev = gen.CalculateNaturalUnscaledElevation(d) * scale;
            if (elev < bestElev)
            {
                bestElev = elev;
                best = d;
            }
        }

        // Prefer a direction that sits at/under sea level when possible.
        if (bestElev > oceanR)
            return beachDir;
        return best;
    }

    static Quaternion BuildRadialRotation(Vector3 radial, float yawDegrees)
    {
        radial.Normalize();
        Vector3 refFwd = Vector3.forward;
        if (Mathf.Abs(Vector3.Dot(refFwd, radial)) > 0.95f)
            refFwd = Vector3.right;
        Vector3 fwd = Vector3.ProjectOnPlane(refFwd, radial).normalized;
        Quaternion q = Quaternion.LookRotation(fwd, radial);
        return Quaternion.AngleAxis(yawDegrees, radial) * q;
    }

    bool IsFarEnough(Vector3 pos, float minSepSq)
    {
        for (int i = 0; i < _spawnedPositions.Count; i++)
        {
            if ((_spawnedPositions[i] - pos).sqrMagnitude < minSepSq)
                return false;
        }
        return true;
    }

    void EnsureRoot()
    {
        if (_spawnRoot != null)
            return;
        var existing = GameObject.Find(spawnedRootName);
        if (existing != null)
        {
            _spawnRoot = existing.transform;
            return;
        }
        var go = new GameObject(spawnedRootName);
        _spawnRoot = go.transform;
    }

    public void ClearSpawned()
    {
        EnsureRoot();
        for (int i = _spawnRoot.childCount - 1; i >= 0; i--)
        {
            Transform child = _spawnRoot.GetChild(i);
            if (Application.isPlaying)
                Destroy(child.gameObject);
            else
                DestroyImmediate(child.gameObject);
        }
        _spawnedPositions.Clear();
    }

    /// <summary>
    /// Capsule stand-in when the Kenny canoe prefab has not been built yet (Tools/Stargrave/Build Boat Prefab).
    /// </summary>
    static GameObject EnsureRuntimeBoatTemplate()
    {
        if (_runtimeBoatTemplate != null)
            return _runtimeBoatTemplate;

        var root = new GameObject("__Boat_Canoe_RuntimeTemplate");
        Object.DontDestroyOnLoad(root);
        root.hideFlags = HideFlags.HideAndDontSave;

        var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        visual.name = "Hull";
        visual.transform.SetParent(root.transform, false);
        visual.transform.localPosition = new Vector3(0f, 0.35f, 0f);
        visual.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
        visual.transform.localScale = new Vector3(0.55f, 1.6f, 0.55f);
        var visCol = visual.GetComponent<Collider>();
        if (visCol != null)
            Object.DestroyImmediate(visCol);

        var hullCol = root.AddComponent<CapsuleCollider>();
        hullCol.radius = 0.55f;
        hullCol.height = 3.2f;
        hullCol.direction = 2; // Z
        hullCol.center = new Vector3(0f, 0.35f, 0f);

        var rb = root.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.mass = 40f;
        rb.linearDamping = 0.2f;
        rb.angularDamping = 1.5f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

        var boat = root.AddComponent<BoatController>();
        var seatGo = new GameObject("Seat");
        seatGo.transform.SetParent(root.transform, false);
        seatGo.transform.localPosition = new Vector3(0f, 0.65f, 0.1f);
        boat.seat = seatGo.transform;

        var exitGo = new GameObject("Exit");
        exitGo.transform.SetParent(root.transform, false);
        exitGo.transform.localPosition = new Vector3(2.2f, 0.4f, 0f);
        boat.exitPoint = exitGo.transform;

        root.AddComponent<BoatInteractable>();
        root.SetActive(false);
        _runtimeBoatTemplate = root;
        return root;
    }
}
