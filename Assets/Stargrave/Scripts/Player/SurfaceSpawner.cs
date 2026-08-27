using UnityEngine;
using System.Collections;

/// <summary>
/// Places this object on dry planet land after the planet has generated.
/// Uses Rigidbody.position so physics respects the spawn. Add to the Player (same object as Rigidbody).
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class SurfaceSpawner : MonoBehaviour
{
    [Header("Planet")]
    public Transform planet;
    public LayerMask groundMask = -1;

    [Header("Spawn")]
    [Tooltip("World units above the surface hit point.")]
    public float heightAboveSurface = 2f;
    [Tooltip("Extra world units above sea level so feet stay dry.")]
    [Min(0f)] public float spawnDryClearance = 1.25f;
    [Tooltip("Prefer terrain above the planet base radius (hills) in addition to being above water.")]
    public bool onlyOnElevatedTerrain = true;
    [Tooltip("Max attempts to find a dry (and optionally elevated) spawn before accepting any dry land.")]
    public int maxElevatedAttempts = 80;
    [Tooltip("Fallback shell radius when planet mesh / analytic surface is unavailable.")]
    public float fallbackShellRadius = 120f;

    Rigidbody _rb;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
    }

    void Start()
    {
        StartCoroutine(SpawnWhenReady());
    }

    IEnumerator SpawnWhenReady()
    {
        if (!ResolvePlanet())
            yield break;

        Planet planetComp = planet.GetComponent<Planet>();
        if (planetComp != null)
        {
            while (!planetComp.IsGenerated)
                yield return null;
        }
        yield return null;
        yield return new WaitForSeconds(0.2f);

        if (!TryPickRandomSurface(out Vector3 spawnPos, out Quaternion spawnRot))
            yield break;

        ApplyPose(spawnPos, spawnRot);
        yield return new WaitForFixedUpdate();
        ApplyPose(spawnPos, spawnRot);
    }

    /// <summary>
    /// Immediately moves to a new random dry surface point and updates <see cref="PlayerHealth"/> spawn.
    /// Planet must already be generated (true after boot).
    /// </summary>
    public bool RelocateToRandomSurface()
    {
        if (!ResolvePlanet())
            return false;
        if (!TryPickRandomSurface(out Vector3 spawnPos, out Quaternion spawnRot))
            return false;
        ApplyPose(spawnPos, spawnRot);
        if (TryGetComponent(out PlayerHealth health))
            health.SetSpawnPose(spawnPos, spawnRot);
        return true;
    }

    bool ResolvePlanet()
    {
        if (planet == null)
        {
            var go = GameObject.FindGameObjectWithTag("Planet");
            if (go != null)
                planet = go.transform;
            if (planet == null)
            {
                var p = Object.FindFirstObjectByType<Planet>();
                if (p != null)
                    planet = p.transform;
            }
        }

        if (planet == null)
        {
            Debug.LogError("SurfaceSpawner: No planet found (tag Planet or Planet component).");
            return false;
        }
        return true;
    }

    bool TryPickRandomSurface(out Vector3 spawnPos, out Quaternion spawnRot)
    {
        spawnPos = transform.position;
        spawnRot = transform.rotation;

        Planet planetComp = planet.GetComponent<Planet>();
        PlanetOceanLayer ocean = planet.GetComponent<PlanetOceanLayer>();
        if (ocean == null)
            ocean = Object.FindFirstObjectByType<PlanetOceanLayer>();

        Vector3 center = planet.position;
        MeshCollider planetCollider = ZombieAI.ResolvePrimaryTerrainMeshCollider(planet);

        float baseRadius = planetComp != null ? planetComp.GetBaseRadiusWorld() : 0f;
        float waterLine = ResolveWaterLine(planetComp, ocean, spawnDryClearance);
        int attempts = Mathf.Max(16, maxElevatedAttempts);

        Vector3 bestDry = default;
        bool foundDry = false;

        for (int i = 0; i < attempts; i++)
        {
            Vector3 candidate = PlanetSurfaceSampler.GetDrySurfacePosition(
                Random.onUnitSphere,
                center,
                planetCollider,
                planetComp,
                ocean,
                groundMask,
                16,
                heightAboveSurface,
                fallbackShellRadius,
                spawnDryClearance);

            Vector3 up = (candidate - center).normalized;
            if (up.sqrMagnitude < 1e-8f)
                continue;

            float surfaceRadial = Vector3.Distance(candidate - up * heightAboveSurface, center);
            if (waterLine > 1e-3f && surfaceRadial < waterLine)
                continue;

            if (!foundDry)
            {
                foundDry = true;
                bestDry = candidate;
            }

            // Prefer hills when asked; otherwise take the first dry land.
            if (!onlyOnElevatedTerrain || baseRadius < 1e-3f || surfaceRadial >= baseRadius)
            {
                spawnPos = candidate;
                spawnRot = Quaternion.FromToRotation(Vector3.up, up);
                return true;
            }
        }

        if (!foundDry)
            return false;

        Vector3 dryUp = (bestDry - center).normalized;
        if (dryUp.sqrMagnitude < 1e-8f)
            dryUp = Vector3.up;
        spawnPos = bestDry;
        spawnRot = Quaternion.FromToRotation(Vector3.up, dryUp);
        return true;
    }

    static float ResolveWaterLine(Planet planetComp, PlanetOceanLayer ocean, float dryClearance)
    {
        float clearance = Mathf.Max(0f, dryClearance);
        if (ocean != null)
            return ocean.ResolveOceanRadiusWorld() + clearance;
        if (planetComp != null)
            return planetComp.GetBaseRadiusWorld() + clearance;
        return 0f;
    }

    void ApplyPose(Vector3 spawnPos, Quaternion spawnRot)
    {
        if (_rb == null)
            _rb = GetComponent<Rigidbody>();
        if (_rb != null)
        {
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.position = spawnPos;
            _rb.rotation = spawnRot;
        }
        transform.position = spawnPos;
        transform.rotation = spawnRot;
    }
}
