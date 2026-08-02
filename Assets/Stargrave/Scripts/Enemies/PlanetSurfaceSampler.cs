using UnityEngine;

/// <summary>
/// Surface spawn positions on dry land (terrain radius above the ocean sea level).
/// Prefers a caller-supplied direction (e.g. opposite hemisphere from the player), then widens the
/// search to the nearest dry terrain around that pole, then any dry land on the planet.
/// </summary>
public static class PlanetSurfaceSampler
{
    const float DefaultDryClearance = 0.75f;

    public static Vector3 GetDrySurfacePosition(
        Vector3 preferredDirection,
        Vector3 planetCenter,
        MeshCollider planetCollider,
        Planet planet,
        LayerMask groundMask,
        int maxAttempts,
        float normalOffset,
        float fallbackShellRadius)
    {
        return GetDrySurfacePosition(
            preferredDirection,
            planetCenter,
            planetCollider,
            planet,
            null,
            groundMask,
            maxAttempts,
            normalOffset,
            fallbackShellRadius,
            DefaultDryClearance);
    }

    /// <summary>
    /// Find a spawn on terrain above water. Prefers <paramref name="preferredDirection"/> (attempt 0),
    /// then samples around that direction, then falls back to any dry land. Never returns a submerged point
    /// when an ocean layer / planet radius is available.
    /// </summary>
    public static Vector3 GetDrySurfacePosition(
        Vector3 preferredDirection,
        Vector3 planetCenter,
        MeshCollider planetCollider,
        Planet planet,
        PlanetOceanLayer ocean,
        LayerMask groundMask,
        int maxAttempts,
        float normalOffset,
        float fallbackShellRadius,
        float dryClearance)
    {
        if (preferredDirection.sqrMagnitude < 1e-8f)
            preferredDirection = Random.onUnitSphere;
        preferredDirection.Normalize();

        if (ocean == null && planet != null)
            ocean = planet.GetComponent<PlanetOceanLayer>();

        float waterLine = ResolveWaterLine(planet, ocean, dryClearance);
        int attempts = Mathf.Max(8, maxAttempts);

        // 1) Preferred direction, then cone around it (opposite-side respawn path).
        Vector3 best = default;
        float bestDot = float.NegativeInfinity;
        bool found = false;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            Vector3 dir = attempt == 0
                ? preferredDirection
                : RandomDirectionNear(preferredDirection, Mathf.Lerp(12f, 75f, attempt / (float)attempts));

            if (!TryGetDryPoint(dir, planetCenter, planetCollider, planet, groundMask, normalOffset,
                    fallbackShellRadius, waterLine, out Vector3 spawn, out float dotVsPreferred))
                continue;

            // Prefer points that stay closest to the requested opposite pole.
            float score = Vector3.Dot(dir, preferredDirection);
            if (!found || score > bestDot)
            {
                found = true;
                bestDot = score;
                best = spawn;
            }

            // Good enough: on the preferred hemisphere and dry.
            if (attempt == 0 || score > 0.55f)
                return spawn;
        }

        if (found)
            return best;

        // 2) Widen: random points on the preferred hemisphere only.
        for (int i = 0; i < attempts; i++)
        {
            Vector3 dir = Random.onUnitSphere;
            if (Vector3.Dot(dir, preferredDirection) < 0f)
                dir = -dir;

            if (TryGetDryPoint(dir, planetCenter, planetCollider, planet, groundMask, normalOffset,
                    fallbackShellRadius, waterLine, out Vector3 spawn, out _))
                return spawn;
        }

        // 3) Last resort: any dry land on the planet (still never intentionally underwater).
        for (int i = 0; i < attempts * 2; i++)
        {
            Vector3 dir = Random.onUnitSphere;
            if (TryGetDryPoint(dir, planetCenter, planetCollider, planet, groundMask, normalOffset,
                    fallbackShellRadius, waterLine, out Vector3 spawn, out _))
                return spawn;
        }

        // Absolute fallback (may be wet if the whole planet is ocean — still better than failing).
        return RaycastPlanetSurface(preferredDirection, planetCollider, planet, planetCenter, normalOffset,
            fallbackShellRadius, groundMask);
    }

    static float ResolveWaterLine(Planet planet, PlanetOceanLayer ocean, float dryClearance)
    {
        float clearance = Mathf.Max(0f, dryClearance);
        if (ocean != null)
            return ocean.ResolveOceanRadiusWorld() + clearance;
        if (planet != null)
            return planet.GetBaseRadiusWorld() + clearance;
        return 0f;
    }

    static bool TryGetDryPoint(
        Vector3 dir,
        Vector3 planetCenter,
        MeshCollider planetCollider,
        Planet planet,
        LayerMask groundMask,
        float normalOffset,
        float fallbackShellRadius,
        float waterLine,
        out Vector3 spawn,
        out float unusedDot)
    {
        unusedDot = 0f;
        spawn = default;
        dir.Normalize();

        // Analytic dry test first (cheap, matches sea level).
        if (planet != null && waterLine > 1e-3f)
        {
            float surfaceR = planet.GetSurfaceRadiusWorld(dir);
            if (surfaceR < waterLine)
                return false;
        }

        spawn = RaycastPlanetSurface(dir, planetCollider, planet, planetCenter, normalOffset,
            fallbackShellRadius, groundMask);
        if (spawn.sqrMagnitude < 1e-6f)
            return false;

        float radial = Vector3.Distance(spawn, planetCenter);
        if (waterLine > 1e-3f && radial < waterLine)
            return false;

        unusedDot = 1f;
        return true;
    }

    static Vector3 RandomDirectionNear(Vector3 axis, float coneDegrees)
    {
        axis.Normalize();
        Vector3 reference = Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.95f ? Vector3.right : Vector3.up;
        Vector3 tA = Vector3.Cross(axis, reference).normalized;
        Vector3 tB = Vector3.Cross(axis, tA).normalized;
        float angle = Random.Range(0f, Mathf.Max(0.01f, coneDegrees));
        float yaw = Random.Range(0f, 360f);
        Vector3 spin = (Mathf.Cos(yaw * Mathf.Deg2Rad) * tA + Mathf.Sin(yaw * Mathf.Deg2Rad) * tB).normalized;
        return (Quaternion.AngleAxis(angle, spin) * axis).normalized;
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

        // Analytic surface when available (no mesh raycast needed).
        if (planet != null)
        {
            Vector3 p = planet.GetSurfacePointWorld(direction);
            Vector3 up = (p - planetCenter).normalized;
            return p + up * normalOffset;
        }

        Ray ray = new Ray(planetCenter + direction * 4000f, -direction);

        if (planetCollider != null && planetCollider.Raycast(ray, out RaycastHit h, 8000f))
            return h.point + h.normal * normalOffset;

        if (Physics.Raycast(ray, out RaycastHit hit, 8000f, groundMask, QueryTriggerInteraction.Ignore))
            return hit.point + hit.normal * normalOffset;

        return planetCenter + direction * fallbackShellRadius;
    }
}
