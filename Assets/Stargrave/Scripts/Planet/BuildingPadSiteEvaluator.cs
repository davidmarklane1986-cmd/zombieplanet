using UnityEngine;

/// <summary>
/// Scores candidate building sites on the procedural planet using natural (pre-pad) terrain:
/// must be dry, gently sloped, and already fairly flat across the footprint.
/// </summary>
public static class BuildingPadSiteEvaluator
{
    public struct Settings
    {
        public float flatRadius;
        public float dryClearance;
        public float maxSlopeDegrees;
        public float maxHeightVariation;
        public int ringSamples;
        public int searchAttempts;

        public static Settings FromPad(BuildingPad pad)
        {
            return new Settings
            {
                flatRadius = Mathf.Max(0.5f, pad.flatRadius),
                dryClearance = Mathf.Max(0f, pad.dryClearance),
                maxSlopeDegrees = Mathf.Max(1f, pad.maxSlopeDegrees),
                maxHeightVariation = Mathf.Max(0.1f, pad.maxHeightVariation),
                ringSamples = Mathf.Clamp(pad.siteSampleCount, 6, 48),
                searchAttempts = Mathf.Clamp(pad.siteSearchAttempts, 16, 512)
            };
        }
    }

    public struct Report
    {
        public bool isValid;
        public bool isDry;
        public float centerSlopeDegrees;
        public float maxSlopeDegrees;
        public float heightRangeWorld;
        public float dryFraction;
        public float score;
        public string reason;

        public static Report Fail(string reason) => new Report
        {
            isValid = false,
            reason = reason,
            score = float.NegativeInfinity
        };
    }

    public static float ResolveWaterLine(Planet planet, float dryClearance)
    {
        if (planet == null)
            return 0f;
        var ocean = planet.GetComponent<PlanetOceanLayer>();
        float clearance = Mathf.Max(0f, dryClearance);
        if (ocean != null)
            return ocean.ResolveOceanRadiusWorld() + clearance;
        return planet.GetBaseRadiusWorld() + clearance;
    }

    public static Report Evaluate(Planet planet, Vector3 axisUnit, Settings settings)
    {
        if (planet == null || planet.shapeSettings == null)
            return Report.Fail("No planet / shape settings.");

        var gen = new ShapeGenerator();
        gen.UpdateSettings(planet.shapeSettings);
        return Evaluate(planet, gen, axisUnit, settings);
    }

    public static Report Evaluate(Planet planet, ShapeGenerator gen, Vector3 axisUnit, Settings settings)
    {
        if (planet == null || gen == null)
            return Report.Fail("No planet / shape settings.");

        if (axisUnit.sqrMagnitude < 1e-10f)
            return Report.Fail("Invalid axis.");
        axisUnit.Normalize();

        float scale = PlanetBuildingPads.WorldScale(planet);
        float waterLine = ResolveWaterLine(planet, settings.dryClearance);

        float centerR = gen.CalculateNaturalUnscaledElevation(axisUnit) * scale;
        if (centerR < waterLine)
            return Report.Fail("Center is underwater / below dry clearance.");

        Vector3 centerN = NaturalNormal(gen, axisUnit, scale, centerR);
        float centerSlope = Vector3.Angle(centerN, axisUnit);
        if (centerSlope > settings.maxSlopeDegrees)
        {
            var steep = Report.Fail($"Center slope {centerSlope:F1}° exceeds {settings.maxSlopeDegrees:F0}°.");
            steep.isDry = true;
            steep.centerSlopeDegrees = centerSlope;
            steep.maxSlopeDegrees = centerSlope;
            return steep;
        }

        Vector3 t1 = Vector3.Cross(axisUnit, Vector3.up);
        if (t1.sqrMagnitude < 1e-6f)
            t1 = Vector3.Cross(axisUnit, Vector3.right);
        t1.Normalize();
        Vector3 t2 = Vector3.Cross(axisUnit, t1);

        // One outer ring only (no per-sample normals) — height swing is the flatness signal.
        int samples = Mathf.Clamp(settings.ringSamples, 4, 24);
        float ang = settings.flatRadius / Mathf.Max(1e-3f, centerR);
        float rMin = centerR;
        float rMax = centerR;
        int dryCount = 1;
        int total = 1;

        for (int i = 0; i < samples; i++)
        {
            float a = (i / (float)samples) * Mathf.PI * 2f;
            Vector3 offset = (t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a)) * ang;
            Vector3 dir = (axisUnit + offset).normalized;
            float r = gen.CalculateNaturalUnscaledElevation(dir) * scale;
            total++;
            if (r >= waterLine)
                dryCount++;
            rMin = Mathf.Min(rMin, r);
            rMax = Mathf.Max(rMax, r);
        }

        float dryFraction = dryCount / (float)total;
        float heightRange = rMax - rMin;
        // Approximate footprint slope from radial height swing across the pad radius.
        float approxFootSlope = Mathf.Atan2(heightRange, Mathf.Max(0.01f, settings.flatRadius)) * Mathf.Rad2Deg;
        float maxSlope = Mathf.Max(centerSlope, approxFootSlope);
        bool isDry = dryFraction >= 0.92f && rMin >= waterLine * 0.999f;

        if (!isDry)
        {
            return new Report
            {
                isValid = false,
                isDry = false,
                centerSlopeDegrees = centerSlope,
                maxSlopeDegrees = maxSlope,
                heightRangeWorld = heightRange,
                dryFraction = dryFraction,
                score = float.NegativeInfinity,
                reason = $"Pad overlaps water ({dryFraction * 100f:F0}% dry)."
            };
        }

        if (maxSlope > settings.maxSlopeDegrees)
        {
            return new Report
            {
                isValid = false,
                isDry = true,
                centerSlopeDegrees = centerSlope,
                maxSlopeDegrees = maxSlope,
                heightRangeWorld = heightRange,
                dryFraction = dryFraction,
                score = float.NegativeInfinity,
                reason = $"Footprint slope {maxSlope:F1}° exceeds {settings.maxSlopeDegrees:F0}°."
            };
        }

        if (heightRange > settings.maxHeightVariation)
        {
            return new Report
            {
                isValid = false,
                isDry = true,
                centerSlopeDegrees = centerSlope,
                maxSlopeDegrees = maxSlope,
                heightRangeWorld = heightRange,
                dryFraction = dryFraction,
                score = float.NegativeInfinity,
                reason = $"Height swing {heightRange:F1} exceeds {settings.maxHeightVariation:F1}."
            };
        }

        float slopeScore = 1f - Mathf.Clamp01(maxSlope / settings.maxSlopeDegrees);
        float flatScore = 1f - Mathf.Clamp01(heightRange / settings.maxHeightVariation);
        float dryScore = dryFraction;
        // Prefer gentle coastal shelves (dry but close to water) over high inland peaks.
        float aboveWater = Mathf.Max(0f, centerR - waterLine);
        float coastBand = Mathf.Max(4f, settings.flatRadius * 3f);
        float coastBonus = 1f - Mathf.Clamp01(aboveWater / coastBand);
        float score = slopeScore * 0.4f + flatScore * 0.35f + dryScore * 0.1f + coastBonus * 0.15f;

        return new Report
        {
            isValid = true,
            isDry = true,
            centerSlopeDegrees = centerSlope,
            maxSlopeDegrees = maxSlope,
            heightRangeWorld = heightRange,
            dryFraction = dryFraction,
            score = score,
            reason = $"OK — slope {maxSlope:F1}°, Δh {heightRange:F1}, dry {dryFraction * 100f:F0}%."
        };
    }

    /// <summary>
    /// Search near <paramref name="preferredAxis"/> for a dry, flatish site. Returns best axis found.
    /// </summary>
    public static bool TryFindSuitableSite(
        Planet planet,
        Vector3 preferredAxis,
        Settings settings,
        out Vector3 bestAxis,
        out Report bestReport)
    {
        bestAxis = preferredAxis.sqrMagnitude > 1e-8f ? preferredAxis.normalized : Vector3.up;
        bestReport = Report.Fail("No planet.");
        if (planet == null || planet.shapeSettings == null)
            return false;

        var gen = new ShapeGenerator();
        gen.UpdateSettings(planet.shapeSettings);

        bestReport = Evaluate(planet, gen, bestAxis, settings);
        if (bestReport.isValid)
            return true;

        Report best = bestReport;
        Vector3 bestDir = bestAxis;
        bool found = false;
        int attempts = Mathf.Max(4, settings.searchAttempts);

        for (int i = 0; i < attempts; i++)
        {
            float cone = Mathf.Lerp(8f, 95f, attempts <= 1 ? 1f : i / (float)(attempts - 1));
            Vector3 dir = i < attempts / 2
                ? RandomDirectionNear(preferredAxis, cone)
                : Random.onUnitSphere;

            Report report = Evaluate(planet, gen, dir, settings);
            if (!report.isValid)
                continue;
            if (!found || report.score > best.score)
            {
                found = true;
                best = report;
                bestDir = dir.normalized;
            }

            if (found && best.score >= 0.72f && i > 4)
                break;
        }

        if (!found)
        {
            int widen = Mathf.Min(attempts, 24);
            for (int i = 0; i < widen; i++)
            {
                Vector3 dir = Random.onUnitSphere;
                if (Vector3.Dot(dir, preferredAxis) < 0f)
                    dir = -dir;
                Report report = Evaluate(planet, gen, dir, settings);
                if (!report.isValid)
                    continue;
                if (!found || report.score > best.score)
                {
                    found = true;
                    best = report;
                    bestDir = dir.normalized;
                }
            }
        }

        bestAxis = bestDir;
        bestReport = best;
        return found;
    }

    static Vector3 NaturalNormal(ShapeGenerator gen, Vector3 dir, float scale, float worldRadiusAtDir)
    {
        dir.Normalize();
        Vector3 up = Vector3.up;
        Vector3 t1 = Vector3.Cross(dir, up);
        if (t1.sqrMagnitude < 1e-6f)
            t1 = Vector3.Cross(dir, Vector3.right);
        t1.Normalize();
        Vector3 t2 = Vector3.Cross(dir, t1);

        const float eps = 0.02f;
        Vector3 p0 = dir * worldRadiusAtDir;
        Vector3 da = (dir + t1 * eps).normalized;
        Vector3 db = (dir + t2 * eps).normalized;
        Vector3 pa = da * (gen.CalculateNaturalUnscaledElevation(da) * scale);
        Vector3 pb = db * (gen.CalculateNaturalUnscaledElevation(db) * scale);

        Vector3 n = Vector3.Cross(pa - p0, pb - p0);
        if (n.sqrMagnitude < 1e-12f)
            return dir;
        n.Normalize();
        if (Vector3.Dot(n, dir) < 0f)
            n = -n;
        return n;
    }

    static Vector3 RandomDirectionNear(Vector3 axis, float coneDegrees)
    {
        if (axis.sqrMagnitude < 1e-8f)
            axis = Vector3.up;
        axis.Normalize();
        Vector3 reference = Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.95f ? Vector3.right : Vector3.up;
        Vector3 tA = Vector3.Cross(axis, reference).normalized;
        Vector3 tB = Vector3.Cross(axis, tA).normalized;
        float angle = Random.Range(0f, Mathf.Max(0.01f, coneDegrees));
        float yaw = Random.Range(0f, 360f);
        Vector3 spin = (Mathf.Cos(yaw * Mathf.Deg2Rad) * tA + Mathf.Sin(yaw * Mathf.Deg2Rad) * tB).normalized;
        return (Quaternion.AngleAxis(angle, spin) * axis).normalized;
    }
}
