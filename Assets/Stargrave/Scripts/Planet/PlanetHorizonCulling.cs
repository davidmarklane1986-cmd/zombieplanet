using UnityEngine;

/// <summary>
/// Geometric limb + terrain line-of-sight tests for a spherical planet.
/// Frustum culling alone still draws objects past the horizon and behind hills.
/// </summary>
public static class PlanetHorizonCulling
{
    /// <summary>
    /// True when <paramref name="worldPoint"/> is not occluded by the mean planet body from the camera.
    /// </summary>
    public static bool IsVisible(
        Vector3 camPos,
        Vector3 planetCenter,
        float surfaceRadius,
        Vector3 worldPoint,
        bool sticky = false)
    {
        Vector3 camRel = camPos - planetCenter;
        float camR2 = camRel.sqrMagnitude;
        if (camR2 < 1e-4f)
            return true;

        // Soft limb: only cull well past the geometric horizon (was 0.90 / 0.94 — too eager on ridges).
        float occR = surfaceRadius * (sticky ? 0.84f : 0.88f);
        Vector3 toPoint = worldPoint - camPos;
        float dist = toPoint.magnitude;
        if (dist < 0.01f)
            return true;

        Vector3 dir = toPoint / dist;
        float b = Vector3.Dot(camRel, dir);
        float c = camR2 - occR * occR;
        float disc = b * b - c;
        if (disc <= 0f)
            return true;

        float tHit = -b - Mathf.Sqrt(disc);
        float margin = sticky ? 6f : 3f;
        if (tHit > 0.6f && tHit < dist - margin)
            return false;
        return true;
    }

    /// <summary>
    /// True when the sight line from camera to target clears procedural terrain hills.
    /// Samples the analytic surface along the chord; blocked only when terrain clearly pierces the ray
    /// (gentle grazing / low ridges are allowed so mid-range foliage and buildings don't swiss-cheese).
    /// </summary>
    public static bool HasTerrainLineOfSight(
        Planet planet,
        Vector3 planetCenter,
        Vector3 camPos,
        Vector3 targetPos,
        bool sticky = false)
    {
        if (planet == null)
            return true;

        // Lift both ends well above the crust ("eye / canopy" ray). Higher = less false cull on hills.
        const float EyeHeight = 9f;
        Vector3 camRadial = camPos - planetCenter;
        if (camRadial.sqrMagnitude > 1e-6f)
            camPos += camRadial.normalized * EyeHeight;
        Vector3 tgtRadial = targetPos - planetCenter;
        if (tgtRadial.sqrMagnitude > 1e-6f)
            targetPos += tgtRadial.normalized * EyeHeight;

        Vector3 delta = targetPos - camPos;
        float dist = delta.magnitude;
        // Foreground / mid-range: never hill-cull — player can almost always see this band.
        if (dist < 70f)
            return true;

        int samples = dist < 140f ? 3 : (dist < 260f ? 4 : 5);
        // Terrain must stick THROUGH the ray by this much before we count a pierce.
        // Sticky = only big ridges hide; fresh = still needs a clear crest, not a graze.
        float pierceNeeded = sticky ? 2.4f : 1.2f;
        // Need multiple pierces so a single noisy / crest sample can't wipe a whole chunk.
        int hitsNeeded = sticky ? 2 : 2;
        int hits = 0;

        for (int i = 1; i < samples; i++)
        {
            float t = i / (float)samples;
            // Ignore near both ends (local ground / target feet).
            if (t < 0.22f || t > 0.78f)
                continue;

            Vector3 p = camPos + delta * t;
            Vector3 fromCenter = p - planetCenter;
            float rayR = fromCenter.magnitude;
            if (rayR < 1e-3f)
                continue;

            float surfaceR = planet.GetSurfaceRadiusWorld(fromCenter / rayR);
            if (surfaceR > rayR + pierceNeeded)
            {
                hits++;
                if (hits >= hitsNeeded)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Combined limb + terrain test used by foliage / zombies / unit draws.
    /// </summary>
    public static bool IsVisibleInWorld(
        Planet planet,
        Vector3 planetCenter,
        float meanSurfaceRadius,
        Vector3 camPos,
        Vector3 worldPoint,
        bool sticky = false)
    {
        if (meanSurfaceRadius > 1f &&
            !IsVisible(camPos, planetCenter, meanSurfaceRadius, worldPoint, sticky))
            return false;

        if (planet != null &&
            !HasTerrainLineOfSight(planet, planetCenter, camPos, worldPoint, sticky))
            return false;

        return true;
    }

    public static float ApproximateHorizonDistance(float camRadius, float surfaceRadius)
    {
        float under = Mathf.Max(0f, camRadius * camRadius - surfaceRadius * surfaceRadius * 0.88f);
        float horizon = Mathf.Sqrt(under) + surfaceRadius * 0.12f;
        return Mathf.Max(horizon, surfaceRadius * 0.05f);
    }

    public static float SampleSurfaceRadius(Planet planet, Vector3 planetCenter, Vector3 camPos, float fallback)
    {
        if (planet == null)
            return Mathf.Max(1f, fallback);
        Vector3 axis = camPos - planetCenter;
        if (axis.sqrMagnitude < 1e-8f)
            return Mathf.Max(1f, fallback);
        float r = planet.GetSurfaceRadiusWorld(axis.normalized);
        return r > 1f ? r : Mathf.Max(1f, fallback);
    }
}
