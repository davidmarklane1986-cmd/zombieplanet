using UnityEngine;

/// <summary>
/// Dry surface spawn positions: ray onto planet terrain, reject underwater (water layer or analytical <see cref="Planet"/> water).
/// </summary>
public static class PlanetSurfaceSampler
{
    public static Vector3 GetDrySurfacePosition(
        Vector3 preferredDirection,
        Vector3 planetCenter,
        MeshCollider planetCollider,
        PlanetWaterLayer waterLayer,
        Planet planet,
        LayerMask groundMask,
        int maxAttempts,
        float normalOffset,
        float fallbackShellRadius,
        float underwaterEpsilon)
    {
        int attempts = Mathf.Max(1, maxAttempts);
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            Vector3 dir = attempt == 0 ? preferredDirection.normalized : Random.onUnitSphere;
            Vector3 spawn = RaycastPlanetSurface(dir, planetCollider, planet, planetCenter, normalOffset, fallbackShellRadius, groundMask);
            if (spawn.sqrMagnitude < 1e-6f)
                continue;
            if (IsUnderwaterWorldPoint(spawn, waterLayer, planet, underwaterEpsilon))
                continue;
            return spawn;
        }

        return RaycastPlanetSurface(preferredDirection.normalized, planetCollider, planet, planetCenter, normalOffset, fallbackShellRadius, groundMask);
    }

    static bool IsUnderwaterWorldPoint(Vector3 worldPoint, PlanetWaterLayer waterLayer, Planet planet, float epsilon)
    {
        if (waterLayer != null)
            return waterLayer.IsUnderwaterWorldPoint(worldPoint, epsilon);
        if (planet != null)
        {
            float wr = planet.GetWaterRadiusWorld();
            if (wr <= 0f)
                return false;
            float d = Vector3.Distance(worldPoint, planet.transform.position);
            return d < wr + Mathf.Max(0f, -epsilon);
        }
        return false;
    }

    public static Vector3 RaycastPlanetSurface(
        Vector3 direction,
        MeshCollider planetCollider,
        Planet planet,
        Vector3 planetCenter,
        float normalOffset,
        float fallbackShellRadius,
        LayerMask groundMask)
    {
        direction.Normalize();
        float margin = Mathf.Max(10f, fallbackShellRadius * 0.15f);

        if (planet != null && planet.TryGetSurfacePoint(direction, groundMask, margin, out Vector3 surfacePoint, out Vector3 surfaceUp))
            return surfacePoint + surfaceUp * normalOffset;

        Ray ray = new Ray(planetCenter + direction * 4000f, -direction);

        if (planetCollider != null && planetCollider.Raycast(ray, out RaycastHit h, 8000f))
            return h.point + h.normal * normalOffset;

        if (Physics.Raycast(ray, out RaycastHit hit, 8000f, groundMask, QueryTriggerInteraction.Ignore))
            return hit.point + hit.normal * normalOffset;

        return planetCenter + direction * fallbackShellRadius;
    }
}
