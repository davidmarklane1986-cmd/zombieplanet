using UnityEngine;
using System.Collections;

/// <summary>
/// Places this object on the planet surface at a random point after the planet has generated.
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
    [Tooltip("Only spawn on terrain higher than the base planet radius (e.g. hills, not valleys/water level).")]
    public bool onlyOnElevatedTerrain = true;
    [Tooltip("Max attempts to find an elevated spawn point before using any hit.")]
    public int maxElevatedAttempts = 50;

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
        // Find planet
        if (planet == null)
        {
            var go = GameObject.FindGameObjectWithTag("Planet");
            if (go != null) planet = go.transform;
            if (planet == null)
            {
                var p = Object.FindFirstObjectByType<Planet>();
                if (p != null) planet = p.transform;
            }
        }

        if (planet == null)
        {
            Debug.LogError("SurfaceSpawner: No planet found (tag Planet or Planet component).");
            yield break;
        }

        Planet planetComp = planet.GetComponent<Planet>();
        if (planetComp != null)
        {
            while (!planetComp.IsGenerated)
                yield return null;
        }
        yield return null;
        yield return new WaitForSeconds(0.2f);

        Vector3 center = planet.position;
        float maxRadius = 100f;
        float baseRadius = 100f;
        if (planetComp != null && planetComp.shapeSettings != null)
        {
            maxRadius = planetComp.GetMaxSurfaceRadiusWorld();
            baseRadius = planetComp.GetBaseRadiusWorld();
        }
        if (maxRadius < 10f)
            maxRadius = (planetComp != null && planetComp.shapeSettings != null) ? planetComp.shapeSettings.planetRadius * Mathf.Max(planet.lossyScale.x, planet.lossyScale.y, planet.lossyScale.z) : 100f;
        if (maxRadius < 10f) maxRadius = 100f;
        if (baseRadius < 10f) baseRadius = maxRadius * 0.95f;

        Vector3 dir = Random.onUnitSphere.normalized;
        Vector3 surfacePoint = center + dir * maxRadius;
        Vector3 up = dir;

        if (planetComp != null)
        {
            float rayMargin = Mathf.Max(150f, maxRadius * 0.5f);
            int attempts = 0;
            bool foundElevated = false;
            while (attempts < maxElevatedAttempts)
            {
                dir = Random.onUnitSphere.normalized;
                if (planetComp.TryGetSurfacePoint(dir, groundMask, rayMargin, out surfacePoint, out up))
                {
                    float distFromCenter = (surfacePoint - center).magnitude;
                    if (!onlyOnElevatedTerrain || distFromCenter >= baseRadius)
                    {
                        foundElevated = true;
                        break;
                    }
                }
                attempts++;
                if (onlyOnElevatedTerrain && attempts < maxElevatedAttempts)
                    yield return null;
            }
            if (!foundElevated)
            {
                if (planetComp.TryGetSurfacePoint(Random.onUnitSphere.normalized, groundMask, rayMargin, out surfacePoint, out up))
                {
                    // Use last attempt
                }
                else
                {
                    surfacePoint = center + dir * maxRadius;
                    up = dir;
                }
            }
        }

        Vector3 spawnPos = surfacePoint + up * heightAboveSurface;
        Quaternion spawnRot = Quaternion.FromToRotation(Vector3.up, up);

        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        _rb.position = spawnPos;
        _rb.rotation = spawnRot;
        transform.position = spawnPos;
        transform.rotation = spawnRot;

        yield return new WaitForFixedUpdate();
        _rb.position = spawnPos;
        _rb.rotation = spawnRot;
        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        transform.position = spawnPos;
        transform.rotation = spawnRot;
    }
}
