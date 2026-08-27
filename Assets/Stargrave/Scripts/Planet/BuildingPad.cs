using UnityEngine;

/// <summary>
/// Scene marker for a hybrid building terrain pad: true-flat tangent core, constant-radius plateau,
/// smooth outer falloff into natural hills. Pads are baked into <see cref="PlanetBuildingPads"/> whenever
/// the planet regenerates.
/// </summary>
[DisallowMultipleComponent]
[ExecuteAlways]
public sealed class BuildingPad : MonoBehaviour
{
    public enum HeightMode
    {
        /// <summary>Use the natural (noise) radius at the pad center.</summary>
        SampleAtCenter = 0,
        /// <summary>Use <see cref="fixedLocalRadius"/> in planet-local mesh units.</summary>
        FixedLocalRadius = 1
    }

    [Header("Footprint (world units)")]
    [Tooltip("Radius of the true-flat core under the building.")]
    [Min(0.5f)] public float flatRadius = 8f;
    [Tooltip("Width of the smooth blend ring outside the flat core.")]
    [Min(0.5f)] public float blendWidth = 12f;

    [Header("Height")]
    public HeightMode heightMode = HeightMode.SampleAtCenter;
    [Tooltip("Local (unscaled) surface radius when Height Mode is Fixed Local Radius.")]
    [Min(1f)] public float fixedLocalRadius = 100f;

    [Header("Foliage")]
    [Tooltip("Reject / clear foliage where pad influence is strong.")]
    public bool suppressFoliage = true;
    [Range(0.05f, 1f)]
    [Tooltip("Pad outer-weight above which foliage is suppressed.")]
    public float foliageSuppressWeight = 0.35f;

    [Header("Placement")]
    [Tooltip("Keep this transform on the planet surface along planet-up when snapping.")]
    public bool alignToPlanetUp = true;
    [Tooltip("When snapping / creating, search for dry, already-flatish land instead of accepting any point.")]
    public bool requireSuitableSite = true;
    [Tooltip("Extra height above sea level required for the whole footprint.")]
    [Min(0f)] public float dryClearance = 1.25f;
    [Tooltip("Max surface slope (degrees vs radial) allowed across the flat footprint.")]
    [Range(1f, 45f)] public float maxSlopeDegrees = 14f;
    [Tooltip("Max natural height swing (world units) across the flat footprint — already flattish land.")]
    [Min(0.1f)] public float maxHeightVariation = 3.5f;
    [Min(6)] public int siteSampleCount = 12;
    [Min(16)] public int siteSearchAttempts = 96;
    [Tooltip("Skip baking this pad into the planet mesh when the current site fails suitability.")]
    public bool skipBakeIfUnsuitable = true;

    public float OuterRadius => flatRadius + Mathf.Max(0f, blendWidth);

    public BuildingPadSiteEvaluator.Report EvaluateSite()
    {
        var planet = PlanetBuildingPads.FindPlanet();
        if (planet == null)
            return BuildingPadSiteEvaluator.Report.Fail("No planet.");
        Vector3 axis = transform.position - planet.transform.position;
        if (axis.sqrMagnitude < 1e-8f)
            axis = planet.transform.up;
        return BuildingPadSiteEvaluator.Evaluate(planet, axis.normalized, BuildingPadSiteEvaluator.Settings.FromPad(this));
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        var planet = PlanetBuildingPads.FindPlanet();
        if (planet == null)
            return;

        Vector3 center = planet.transform.position;
        Vector3 axis = (transform.position - center);
        if (axis.sqrMagnitude < 1e-8f)
            return;
        axis.Normalize();

        float scale = PlanetBuildingPads.WorldScale(planet);
        float rLocal = heightMode == HeightMode.FixedLocalRadius
            ? fixedLocalRadius
            : EstimateNaturalRadiusLocal(planet, axis);
        float rWorld = rLocal * scale;
        Vector3 padCenter = center + axis * rWorld;

        var report = BuildingPadSiteEvaluator.Evaluate(planet, axis, BuildingPadSiteEvaluator.Settings.FromPad(this));
        Color core = report.isValid ? new Color(0.2f, 0.9f, 0.4f, 0.9f) : new Color(0.95f, 0.25f, 0.2f, 0.95f);
        Color ring = report.isValid ? new Color(0.95f, 0.75f, 0.2f, 0.7f) : new Color(0.95f, 0.45f, 0.15f, 0.75f);

        DrawRingGizmo(padCenter, axis, flatRadius, core);
        DrawRingGizmo(padCenter, axis, OuterRadius, ring);

        Gizmos.color = report.isValid
            ? new Color(0.2f, 0.9f, 0.4f, 0.15f)
            : new Color(0.95f, 0.2f, 0.15f, 0.18f);
        Gizmos.DrawMesh(GetDiskMesh(), padCenter, Quaternion.FromToRotation(Vector3.up, axis),
            Vector3.one * (flatRadius * 2f));
    }

    static Mesh _diskMesh;
    static Mesh GetDiskMesh()
    {
        if (_diskMesh != null)
            return _diskMesh;
        _diskMesh = new Mesh { name = "BuildingPadDisk" };
        const int seg = 32;
        var verts = new Vector3[seg + 1];
        var tris = new int[seg * 3];
        verts[0] = Vector3.zero;
        for (int i = 0; i < seg; i++)
        {
            float a = (i / (float)seg) * Mathf.PI * 2f;
            verts[i + 1] = new Vector3(Mathf.Cos(a) * 0.5f, 0f, Mathf.Sin(a) * 0.5f);
            tris[i * 3] = 0;
            tris[i * 3 + 1] = i + 1;
            tris[i * 3 + 2] = (i + 1) % seg + 1;
        }
        _diskMesh.vertices = verts;
        _diskMesh.triangles = tris;
        _diskMesh.RecalculateNormals();
        return _diskMesh;
    }

    static void DrawRingGizmo(Vector3 center, Vector3 axis, float radius, Color color)
    {
        Gizmos.color = color;
        Vector3 t1 = Vector3.Cross(axis, Vector3.up);
        if (t1.sqrMagnitude < 1e-6f)
            t1 = Vector3.Cross(axis, Vector3.right);
        t1.Normalize();
        Vector3 t2 = Vector3.Cross(axis, t1);
        const int seg = 48;
        Vector3 prev = center + t1 * radius;
        for (int i = 1; i <= seg; i++)
        {
            float a = (i / (float)seg) * Mathf.PI * 2f;
            Vector3 p = center + (t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a)) * radius;
            Gizmos.DrawLine(prev, p);
            prev = p;
        }
    }

    static float EstimateNaturalRadiusLocal(Planet planet, Vector3 axis)
    {
        if (planet.shapeSettings == null)
            return 100f;
        var gen = new ShapeGenerator();
        gen.UpdateSettings(planet.shapeSettings);
        return gen.CalculateNaturalUnscaledElevation(axis);
    }
#endif

    [ContextMenu("Snap To Planet Surface")]
    public void SnapToPlanetSurface()
    {
        SnapToPlanetSurface(requireSuitableSite);
    }

    public void SnapToPlanetSurface(bool findSuitable)
    {
        var planet = PlanetBuildingPads.FindPlanet();
        if (planet == null)
        {
            Debug.LogWarning("[BuildingPad] No Planet found.");
            return;
        }

        if (!planet.IsGenerated)
            planet.GeneratePlanet();

        Vector3 center = planet.transform.position;
        Vector3 axis = (transform.position - center);
        if (axis.sqrMagnitude < 1e-8f)
            axis = planet.transform.up;
        axis.Normalize();

        if (findSuitable)
        {
            if (!BuildingPadSiteEvaluator.TryFindSuitableSite(
                    planet, axis, BuildingPadSiteEvaluator.Settings.FromPad(this),
                    out Vector3 bestAxis, out var report))
            {
                Debug.LogWarning($"[BuildingPad] No suitable dry/flat site near {name}: {report.reason}");
                // Still seat on the aimed surface so the pad isn't left floating.
            }
            else
            {
                axis = bestAxis;
                Debug.Log($"[BuildingPad] {name} seated: {report.reason}");
            }
        }

        ApplyPoseOnAxis(planet, axis);
    }

    [ContextMenu("Find Suitable Site Nearby")]
    public void FindSuitableSiteNearby()
    {
        SnapToPlanetSurface(true);
    }

    public void ApplyPoseOnAxis(Planet planet, Vector3 axis)
    {
        ApplyPoseOnAxis(planet, axis, preserveYaw: false, yawDegrees: 0f);
    }

    /// <summary>
    /// Seat on the analytic surface along <paramref name="axis"/> with planet-center as down
    /// (building local +Y points away from planet center). Optional yaw spins around that radial.
    /// </summary>
    public void ApplyPoseOnAxis(Planet planet, Vector3 axis, bool preserveYaw, float yawDegrees)
    {
        if (planet == null || axis.sqrMagnitude < 1e-10f)
            return;
        axis.Normalize();

        // Prefer full elevation (includes building pads once baked); fall back to natural pre-bake.
        float scale = PlanetBuildingPads.WorldScale(planet);
        float rWorld;
        if (planet.shapeSettings != null)
        {
            // GetSurfaceRadiusWorld uses pads when the registry is baked.
            rWorld = planet.GetSurfaceRadiusWorld(axis);
            if (rWorld < 1e-3f)
            {
                var gen = new ShapeGenerator();
                gen.UpdateSettings(planet.shapeSettings);
                rWorld = gen.CalculateNaturalUnscaledElevation(axis) * scale;
            }
        }
        else
        {
            rWorld = Vector3.Distance(transform.position, planet.transform.position);
        }

        transform.position = planet.transform.position + axis * rWorld;

        if (alignToPlanetUp)
        {
            // Planet center = down: radial axis is building up (not terrain slope normal).
            Vector3 refForward = Vector3.forward;
            if (Mathf.Abs(Vector3.Dot(refForward, axis)) > 0.95f)
                refForward = Vector3.right;
            Vector3 forward = Vector3.ProjectOnPlane(refForward, axis).normalized;
            Quaternion radial = Quaternion.LookRotation(forward, axis);
            if (preserveYaw)
                radial = Quaternion.AngleAxis(yawDegrees, axis) * radial;
            transform.rotation = radial;
        }
    }

    [ContextMenu("Regenerate Planet With Pads")]
    public void RegeneratePlanetWithPads()
    {
        PlanetBuildingPads.RegeneratePlanetWithPads();
    }
}
