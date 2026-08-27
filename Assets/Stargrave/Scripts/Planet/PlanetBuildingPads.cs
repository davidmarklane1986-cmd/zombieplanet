using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Blittable pad sample used by managed elevation and Burst foliage sampling.
/// Angles are radians; <see cref="RPad"/> is local (unscaled) mesh radius.
/// Cos* fields enable a cheap cone reject (most directions exit after one dot).
/// </summary>
public struct BuildingPadSample
{
    public Vector3 Axis;
    public float RPad;
    public float InnerAngle;
    public float OuterAngle;
    public float CosInner;
    public float CosOuter;
    public bool SuppressFoliage;
    public float FoliageSuppressWeight;
}

/// <summary>
/// Registry + hybrid pad deformation for the procedural planet.
/// Core = true-flat tangent plane; mid = constant-radius plateau; outer = smooth falloff to natural terrain.
/// </summary>
public static class PlanetBuildingPads
{
    static readonly List<BuildingPadSample> Pads = new List<BuildingPadSample>(16);
    static BuildingPadSample[] _cache = Array.Empty<BuildingPadSample>();
    static bool _dirty = true;

    public static event Action OnPadsBaked;

    public static int Count
    {
        get
        {
            EnsureCache();
            return _cache.Length;
        }
    }

    public static BuildingPadSample[] Samples
    {
        get
        {
            EnsureCache();
            return _cache;
        }
    }

    public static void Clear()
    {
        Pads.Clear();
        _cache = Array.Empty<BuildingPadSample>();
        _dirty = false;
    }

    public static Planet FindPlanet()
    {
        return UnityEngine.Object.FindFirstObjectByType<Planet>();
    }

    public static float WorldScale(Planet planet)
    {
        if (planet == null)
            return 1f;
        Vector3 lossy = planet.transform.lossyScale;
        float s = Mathf.Max(lossy.x, Mathf.Max(lossy.y, lossy.z));
        return s < 1e-6f ? 1f : s;
    }

    /// <summary>
    /// Collect scene <see cref="BuildingPad"/> markers and bake sample data for elevation queries.
    /// Uses noise-only radius for SampleAtCenter so pads do not feed into themselves.
    /// </summary>
    public static void BakeFromScene(Planet planet)
    {
        Pads.Clear();
        _dirty = true;

        if (planet == null || planet.shapeSettings == null)
        {
            FlushCache();
            OnPadsBaked?.Invoke();
            return;
        }

        var gen = new ShapeGenerator();
        gen.UpdateSettings(planet.shapeSettings);

        float scale = WorldScale(planet);
        Vector3 center = planet.transform.position;

        var pads = UnityEngine.Object.FindObjectsByType<BuildingPad>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < pads.Length; i++)
        {
            BuildingPad pad = pads[i];
            if (pad == null || !pad.isActiveAndEnabled)
                continue;

            Vector3 axis = pad.transform.position - center;
            if (axis.sqrMagnitude < 1e-10f)
                continue;
            axis.Normalize();

            if (pad.skipBakeIfUnsuitable)
            {
                var report = BuildingPadSiteEvaluator.Evaluate(
                    planet, axis, BuildingPadSiteEvaluator.Settings.FromPad(pad));
                if (!report.isValid)
                {
                    Debug.LogWarning($"[PlanetBuildingPads] Skipping '{pad.name}': {report.reason}");
                    continue;
                }
            }

            float rPad = pad.heightMode == BuildingPad.HeightMode.FixedLocalRadius
                ? Mathf.Max(1f, pad.fixedLocalRadius)
                : gen.CalculateNaturalUnscaledElevation(axis);

            float rWorld = Mathf.Max(1e-3f, rPad * scale);
            float flat = Mathf.Max(0.5f, pad.flatRadius);
            float outerR = Mathf.Max(flat + 0.5f, pad.OuterRadius);
            float innerAngle = Mathf.Atan(flat / rWorld);
            float outerAngle = Mathf.Atan(outerR / rWorld);
            if (outerAngle <= innerAngle)
                outerAngle = innerAngle + 1e-4f;

            Pads.Add(new BuildingPadSample
            {
                Axis = axis,
                RPad = rPad,
                InnerAngle = innerAngle,
                OuterAngle = outerAngle,
                CosInner = Mathf.Cos(innerAngle),
                CosOuter = Mathf.Cos(outerAngle),
                SuppressFoliage = pad.suppressFoliage,
                FoliageSuppressWeight = Mathf.Clamp01(pad.foliageSuppressWeight)
            });
        }

        FlushCache();
        OnPadsBaked?.Invoke();
    }

    /// <summary>
    /// Hybrid deform: <c>lerp(natural, lerp(R_pad, r_plane, w_flat), w_outer)</c>.
    /// Strongest outer-weight wins when pads overlap.
    /// Uses cos-cone early-outs (no acos) so directions far from pads stay cheap.
    /// </summary>
    public static float Apply(Vector3 dirUnit, float naturalRadius)
    {
        EnsureCache();
        int n = _cache.Length;
        if (n == 0)
            return naturalRadius;

        dirUnit = dirUnit.sqrMagnitude > 1e-12f ? dirUnit.normalized : Vector3.up;

        float bestW = 0f;
        float bestR = naturalRadius;

        for (int i = 0; i < n; i++)
        {
            BuildingPadSample p = _cache[i];
            float d = Vector3.Dot(dirUnit, p.Axis);
            // Outside outer cone — almost all samples hit this and exit after one multiply-add.
            if (d < p.CosOuter)
                continue;

            // cos decreases as angle increases: map [CosOuter..CosInner] → [0..1]
            float wOuter = d >= p.CosInner ? 1f : SmoothStep(p.CosOuter, p.CosInner, d);
            if (wOuter <= bestW)
                continue;

            float wFlat = d >= 0.999999f ? 1f : SmoothStep(p.CosInner, 1f, d);
            float rPlane = p.RPad / Mathf.Max(d, 0.02f);
            float target = Mathf.Lerp(p.RPad, rPlane, wFlat);
            float deformed = Mathf.Lerp(naturalRadius, target, wOuter);

            bestW = wOuter;
            bestR = deformed;
        }

        return bestR;
    }

    /// <summary>Outer blend weight at a unit direction (0 = natural, 1 = full pad). Strongest pad wins.</summary>
    public static float WeightAt(Vector3 dirUnit)
    {
        EnsureCache();
        int n = _cache.Length;
        if (n == 0)
            return 0f;

        dirUnit = dirUnit.sqrMagnitude > 1e-12f ? dirUnit.normalized : Vector3.up;
        float best = 0f;
        for (int i = 0; i < n; i++)
        {
            BuildingPadSample p = _cache[i];
            float d = Vector3.Dot(dirUnit, p.Axis);
            if (d < p.CosOuter)
                continue;
            float w = d >= p.CosInner ? 1f : SmoothStep(p.CosOuter, p.CosInner, d);
            if (w > best)
                best = w;
        }
        return best;
    }

    /// <summary>True if foliage should be rejected at this direction for any suppressing pad.</summary>
    public static bool ShouldSuppressFoliage(Vector3 dirUnit)
    {
        EnsureCache();
        int n = _cache.Length;
        if (n == 0)
            return false;

        dirUnit = dirUnit.sqrMagnitude > 1e-12f ? dirUnit.normalized : Vector3.up;
        for (int i = 0; i < n; i++)
        {
            BuildingPadSample p = _cache[i];
            if (!p.SuppressFoliage)
                continue;

            float d = Vector3.Dot(dirUnit, p.Axis);
            if (d < p.CosOuter)
                continue;
            float w = d >= p.CosInner ? 1f : SmoothStep(p.CosOuter, p.CosInner, d);
            if (w >= p.FoliageSuppressWeight)
                return true;
        }
        return false;
    }

    public static void RegeneratePlanetWithPads()
    {
        Planet planet = FindPlanet();
        if (planet == null)
        {
            Debug.LogWarning("[PlanetBuildingPads] No Planet found.");
            return;
        }

        // GeneratePlanet already bakes pads + notifies foliage.
        planet.GeneratePlanet();
    }

    public static void NotifyFoliageConsumers()
    {
        var foliage = UnityEngine.Object.FindObjectsByType<FoliageByColour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < foliage.Length; i++)
        {
            if (foliage[i] != null)
                foliage[i].NotifyBuildingPadsChanged();
        }
    }

    static void EnsureCache()
    {
        if (_dirty)
            FlushCache();
    }

    static void FlushCache()
    {
        _cache = Pads.Count == 0 ? Array.Empty<BuildingPadSample>() : Pads.ToArray();
        _dirty = false;
    }

    static float SmoothStep(float edge0, float edge1, float x)
    {
        if (edge1 <= edge0)
            return x >= edge1 ? 1f : 0f;
        float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }
}
