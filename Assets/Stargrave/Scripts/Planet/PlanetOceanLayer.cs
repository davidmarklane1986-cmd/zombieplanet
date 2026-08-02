using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Spherical ocean for a procedural planet (URP). Builds a transparent sphere "shell" at the
/// ocean radius and feeds the planet centre + ocean radius to the <c>Stargrave/Planet Ocean</c>
/// shader, which performs Sebastian Lague's per-pixel ray-sphere + scene-depth ocean entirely in
/// the fragment stage (shallow/deep blend, soft shoreline, sun specular, triplanar waves, and a
/// day/night terminator that matches the terrain).
///
/// Mirrors <see cref="PlanetAtmosphereLayer"/>: a single managed child shell, sized from the
/// Planet's base radius, with geometry uniforms pushed every frame. The sun is read in-shader via
/// URP <c>GetMainLight()</c>, so no light wiring is needed here.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class PlanetOceanLayer : MonoBehaviour
{
    public const string ShaderName = "Stargrave/Planet Ocean";
    private const string OceanName = "OceanShell";

    static readonly int OceanCentreId = Shader.PropertyToID("_OceanCentre");
    static readonly int OceanRadiusId = Shader.PropertyToID("_OceanRadius");
    static readonly int PlanetScaleId = Shader.PropertyToID("_PlanetScale");

    // Wave params read from the ocean material so the CPU bob matches whatever the material renders.
    static readonly int WaveHeightId = Shader.PropertyToID("_WaveHeight");
    static readonly int WaveNormalScaleId = Shader.PropertyToID("_WaveNormalScale");
    static readonly int WaveSpeedId = Shader.PropertyToID("_WaveSpeed");
    static readonly int WaveFadeDistanceId = Shader.PropertyToID("_WaveFadeDistance");
    static readonly int WaveShoreDepthId = Shader.PropertyToID("_WaveShoreDepth");
    static readonly int WaveShoreMinCalmId = Shader.PropertyToID("_WaveShoreMinCalm");

    // World-space reference radius the wave look was authored at (must match the shader's kWaveReferenceWorld).
    const float kWaveReferenceWorld = 305.0f;
    // Swell wavelength / shimmer tile (must match the shader's kGeoSizeMul).
    const float kGeoSwellSizeMul = 2.5f;
    const float kTwoPi = 6.28318530718f;

    Planet _planetForWaves;

    public enum RadiusMode
    {
        FromPlanetBaseRadius = 0,
        ManualWorldRadius = 1,
        ManualLocalScale = 2
    }

    [Header("Ocean radius (sea level)")]
    [Tooltip("FromPlanetBaseRadius: ocean sits at Planet.GetBaseRadiusWorld() * multiplier + offset (recommended). " +
             "ManualWorldRadius: use the explicit world radius below.")]
    public RadiusMode radiusMode = RadiusMode.FromPlanetBaseRadius;

    [Tooltip("Sea level as a multiple of the planet's base (sea-level) radius. 1 = exactly the base sphere; " +
             "raise/lower slightly to meet the terrain shoreline.")]
    [Min(0.01f)]
    public float seaLevelMultiplier = 1.0f;

    [Tooltip("Extra world-space units added to the sea-level radius for fine shoreline tuning.")]
    public float seaLevelOffsetWorld = 0f;

    [Tooltip("Explicit ocean radius in world units (used only when RadiusMode = ManualWorldRadius).")]
    [Min(0.01f)]
    public float manualRadiusWorld = 50f;

    [Tooltip("Explicit local scale for the OceanShell transform (used only when RadiusMode = ManualLocalScale). " +
             "The shader's sphere radius is derived from this so it always matches the rendered shell.")]
    [Min(0.01f)]
    public float manualLocalScale = 610f;

    [Header("Mesh")]
    [Tooltip("Icosphere subdivision level for the ocean shell. Besides rounding the horizon silhouette, this " +
             "sets the triangle density that the GEOMETRIC wave displacement (_WaveHeight in the shader) is " +
             "drawn on: finer ripples need more triangles. 7 (~327k tris, ~2.5u edge at the 305u ocean) lets " +
             "the surface ripple read close to the per-pixel shimmer size. 6 halves the cost but the physical " +
             "ripple gets coarser/blockier. 8 would match the finest shimmer but is ~1.3M tris (too heavy).")]
    [Range(1, 8)]
    public int sphereSubdivisions = 7;

    [Header("Material")]
    [Tooltip("Optional. If left empty, a runtime material is created from the Stargrave/Planet Ocean shader.")]
    public Material oceanMaterial;

    private GameObject oceanObj;
    private Material _runtimeMaterial;
    private Mesh _sphereMesh;
    private int _sphereMeshSubdivisions = -1;
#if UNITY_EDITOR
    private bool _validateRefreshQueued;
#endif

    void OnEnable()
    {
        CreateOrUpdateOcean();
    }

    void OnValidate()
    {
#if UNITY_EDITOR
        if (!isActiveAndEnabled || _validateRefreshQueued)
            return;
        _validateRefreshQueued = true;
        EditorApplication.delayCall += DelayedValidateRefresh;
#else
        CreateOrUpdateOcean();
#endif
    }

#if UNITY_EDITOR
    private void DelayedValidateRefresh()
    {
        _validateRefreshQueued = false;
        if (this == null || !isActiveAndEnabled)
            return;
        CreateOrUpdateOcean();
    }
#endif

    void LateUpdate()
    {
        PushUniforms();
    }

    /// <summary>Creates the shell child if needed, sizes it to the ocean radius, assigns the material.</summary>
    public void CreateOrUpdateOcean()
    {
        Transform existing = transform.Find(OceanName);
        if (existing != null)
            oceanObj = existing.gameObject;
        else
        {
            oceanObj = new GameObject(OceanName);
            oceanObj.transform.SetParent(transform, false);
        }

        // Drop any collider (e.g. left over from a previous primitive-sphere shell).
        DestroyColliders(oceanObj);

        oceanObj.transform.localPosition = Vector3.zero;
        oceanObj.transform.localRotation = Quaternion.identity;
        oceanObj.transform.localScale = Vector3.one * ResolveLocalScale();

        // High-resolution icosphere so the horizon silhouette is round, not faceted.
        MeshFilter mf = oceanObj.GetComponent<MeshFilter>();
        if (mf == null)
            mf = oceanObj.AddComponent<MeshFilter>();
        mf.sharedMesh = GetOrBuildSphereMesh();

        MeshRenderer mr = oceanObj.GetComponent<MeshRenderer>();
        if (mr == null)
            mr = oceanObj.AddComponent<MeshRenderer>();
        mr.enabled = true;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = LightProbeUsage.Off;
        mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
        mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        oceanObj.SetActive(true);

        Material mat = ResolveMaterial();
        if (mat != null)
        {
            mr.sharedMaterial = mat;
        }
        else
        {
            Debug.LogWarning(
                $"PlanetOceanLayer: Could not resolve shader '{ShaderName}'. Assign an Ocean Material, " +
                "or make sure the Stargrave/Planet Ocean shader is imported.", this);
        }

        PushUniforms();
    }

    Material ResolveMaterial()
    {
        if (oceanMaterial != null)
            return oceanMaterial;

        Shader shader = Shader.Find(ShaderName);
        if (shader == null)
            return null;

        if (_runtimeMaterial == null || _runtimeMaterial.shader != shader)
            _runtimeMaterial = new Material(shader) { name = "PlanetOcean (runtime)" };
        return _runtimeMaterial;
    }

    void PushUniforms()
    {
        if (oceanObj == null)
            return;
        MeshRenderer mr = oceanObj.GetComponent<MeshRenderer>();
        if (mr == null || mr.sharedMaterial == null || mr.sharedMaterial.shader == null)
            return;

        float oceanRadius = ResolveOceanRadiusWorld();
        Material m = mr.sharedMaterial;
        m.SetVector(OceanCentreId, transform.position);
        m.SetFloat(OceanRadiusId, oceanRadius);
        m.SetFloat(PlanetScaleId, Mathf.Max(0.01f, oceanRadius));
    }

    /// <summary>World-space ocean radius (sea level).</summary>
    public float ResolveOceanRadiusWorld()
    {
        if (radiusMode == RadiusMode.ManualWorldRadius)
            return Mathf.Max(0.01f, manualRadiusWorld);

        if (radiusMode == RadiusMode.ManualLocalScale)
        {
            // World radius of a unit sphere mesh (local radius 0.5) at the given local scale.
            float parentMaxScale = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
            return Mathf.Max(0.01f, manualLocalScale * 0.5f * Mathf.Max(1e-4f, parentMaxScale));
        }

        Planet planet = GetComponent<Planet>();
        if (planet != null)
        {
            float baseRadius = planet.GetBaseRadiusWorld();
            if (baseRadius > 1e-4f)
                return Mathf.Max(0.01f, baseRadius * seaLevelMultiplier + seaLevelOffsetWorld);
        }

        // Fallback: derive from the terrain renderer bounds if there is no Planet component yet.
        MeshRenderer terrainMr = GetComponent<MeshRenderer>();
        if (terrainMr != null && terrainMr.bounds.size.sqrMagnitude > 1e-6f)
        {
            float r = Mathf.Max(terrainMr.bounds.extents.x, terrainMr.bounds.extents.y, terrainMr.bounds.extents.z);
            return Mathf.Max(0.01f, r * seaLevelMultiplier + seaLevelOffsetWorld);
        }

        return Mathf.Max(0.01f, manualRadiusWorld);
    }

    /// <summary>
    /// World-space centre of the ocean sphere (the planet centre). Gameplay reference for "up" and for
    /// measuring submersion. Matches the centre fed to the ocean shader (<c>transform.position</c>).
    /// </summary>
    public Vector3 OceanCentreWorld => transform.position;

    /// <summary>World-space radius of the ocean surface (sea level). Alias of <see cref="ResolveOceanRadiusWorld"/>.</summary>
    public float OceanSurfaceRadiusWorld => ResolveOceanRadiusWorld();

    /// <summary>
    /// How far <paramref name="worldPos"/> sits below the ocean surface, in world units.
    /// Positive = submerged (below the surface), negative = above the surface (in air).
    /// </summary>
    public float GetDepthBelowSurface(Vector3 worldPos)
    {
        return ResolveOceanRadiusWorld() - Vector3.Distance(worldPos, transform.position);
    }

    /// <summary>
    /// Depth below the ANIMATED wave surface (base sea level + wave height at this position/time).
    /// Positive = submerged. Feed this into buoyancy so the float line rides the passing swell.
    /// </summary>
    public float GetDepthBelowAnimatedSurface(Vector3 worldPos, float time)
    {
        return GetDepthBelowSurface(worldPos) + GetWaveHeightAtPosition(worldPos, time);
    }

    /// <summary>
    /// CPU wave displacement (radial, world units) at <paramref name="worldPos"/> for BUOYANCY. Uses the
    /// shader's exact swell pattern (<c>OceanSwellHeight</c>, same waveScale/time clock) and the same
    /// near-shore calming SHAPE, with two deliberate departures from the shader so the player actually
    /// rides the visible swell:
    ///   1. NO camera-distance fade. That fade only stops far-water triangles aliasing on screen; for
    ///      buoyancy we need the true height AT the player, regardless of how far the camera is.
    ///   2. The shore calm is normalized to THIS planet's ocean depth range (oceanRadius - baseRadius),
    ///      not the material's <c>_WaveShoreDepth</c>. The shader compares against a per-pixel VIEW-RAY
    ///      water column (large at grazing angles, so open water renders full waves); that isn't
    ///      reproducible on the CPU, and the analytic VERTICAL column here only spans the ocean depth
    ///      (~5u on this planet), so using _WaveShoreDepth (~60) would crush the bob to ~7% everywhere.
    /// Use <c>Time.time</c> for <paramref name="time"/> (== the shader's <c>_Time.y</c>).
    /// </summary>
    public float GetWaveHeightAtPosition(Vector3 worldPos, float time)
    {
        Material m = ResolveMaterial();
        float waveHeight = ReadMatFloat(m, WaveHeightId, 1.5f);
        if (waveHeight <= 0f)
            return 0f;

        float waveNormalScale = ReadMatFloat(m, WaveNormalScaleId, 18f);
        float waveSpeed = ReadMatFloat(m, WaveSpeedId, 0.12f);
        float waveShoreMinCalm = ReadMatFloat(m, WaveShoreMinCalmId, 0.05f);

        // Match the shader's wrapped clock so long sessions don't drift from float precision.
        float t = time - Mathf.Floor(time / 2400f) * 2400f;
        float swell = OceanSwellHeight(worldPos, t, waveNormalScale, waveSpeed);
        float calm = ComputeShoreCalm(worldPos, waveShoreMinCalm);

        return swell * waveHeight * calm;
    }

    /// <summary>
    /// Near-shore calm (0..1) normalized to the planet's ocean depth range so deep/open water bobs at full
    /// amplitude and only the genuine shoreline flattens. Same smoothstep + min-calm SHAPE as the shader.
    /// </summary>
    float ComputeShoreCalm(Vector3 worldPos, float minCalm)
    {
        float oceanR = ResolveOceanRadiusWorld();
        if (_planetForWaves == null)
            _planetForWaves = GetComponent<Planet>();
        float baseR = _planetForWaves != null ? _planetForWaves.GetBaseRadiusWorld() : oceanR - 1f;
        float depthRange = Mathf.Max(1f, oceanR - baseR);   // shore (0) -> deepest ocean (full)
        float columnDepth = GetWaterColumnDepth(worldPos);
        return WaveDepthCalm(columnDepth, depthRange, minCalm);
    }

    /// <summary>Debug-only: the shore-calm factor (0..1) used for the bob at a position.</summary>
    public float GetShoreCalm01(Vector3 worldPos)
    {
        Material m = ResolveMaterial();
        float minCalm = ReadMatFloat(m, WaveShoreMinCalmId, 0.05f);
        return ComputeShoreCalm(worldPos, minCalm);
    }

    /// <summary>Faithful CPU copy of the shader's <c>OceanSwellHeight</c> (returns roughly [-1, 1]).</summary>
    float OceanSwellHeight(Vector3 worldPos, float time, float waveNormalScale, float waveSpeed)
    {
        Vector3 p = worldPos - transform.position;

        float waveScale = waveNormalScale / kWaveReferenceWorld;     // cycles / world unit
        float baseK = kTwoPi * waveScale / kGeoSwellSizeMul;         // rad / world unit (finest swell)
        float w = kTwoPi * waveSpeed / kGeoSwellSizeMul;            // rad / sec
        float t = time;

        Vector3 d0 = new Vector3(1.0f, 0.4f, 0.7f).normalized;
        Vector3 d1 = new Vector3(-0.6f, -0.3f, 0.9f).normalized;
        Vector3 d2 = new Vector3(0.5f, -0.8f, -0.2f).normalized;
        Vector3 d3 = new Vector3(-0.9f, 0.5f, -0.4f).normalized;

        // Mild domain warp (matches shader): bend crest lines so it reads organic, not a sine grid.
        float warpK = baseK * 0.22f;
        float warpAmp = 0.45f / Mathf.Max(baseK, 1e-4f);
        Vector3 warp = new Vector3(
            Mathf.Sin(warpK * Vector3.Dot(d1, p) - w * 0.22f * t + 0.0f),
            Mathf.Sin(warpK * Vector3.Dot(d2, p) - w * 0.22f * t + 2.0f),
            Mathf.Sin(warpK * Vector3.Dot(d0, p) - w * 0.22f * t + 4.0f));
        p += warp * warpAmp;

        float h = 1.00f * Mathf.Sin(baseK * 1.00f * Vector3.Dot(d0, p) - w * 1.00f * t + 0.0f);
        h += 0.55f * Mathf.Sin(baseK * 0.72f * Vector3.Dot(d1, p) - w * 0.72f * t + 1.3f);
        h += 0.35f * Mathf.Sin(baseK * 0.48f * Vector3.Dot(d2, p) - w * 0.48f * t + 2.1f);
        h += 0.22f * Mathf.Sin(baseK * 0.32f * Vector3.Dot(d3, p) - w * 0.32f * t + 4.2f);

        return h / 2.12f; // normalize by the sum of amplitudes
    }

    /// <summary>Faithful CPU copy of the shader's <c>WaveDepthCalm</c>: 0 = glassy/shallow, 1 = full waves.</summary>
    static float WaveDepthCalm(float columnDepth, float shoreDepth, float minCalm)
    {
        float f = Mathf.Clamp01(columnDepth / Mathf.Max(shoreDepth, 1e-3f));
        f = Mathf.SmoothStep(0f, 1f, f);
        return Mathf.Lerp(minCalm, 1f, f);
    }

    /// <summary>Analytic water-column depth at a position: ocean radius minus terrain radius along that direction.</summary>
    float GetWaterColumnDepth(Vector3 worldPos)
    {
        float oceanR = ResolveOceanRadiusWorld();
        if (_planetForWaves == null)
            _planetForWaves = GetComponent<Planet>();
        if (_planetForWaves == null)
            return oceanR; // no planet -> assume deep water (full waves)

        Vector3 dir = worldPos - transform.position;
        float terrainR = _planetForWaves.GetSurfaceRadiusWorld(dir);
        return Mathf.Max(0f, oceanR - terrainR);
    }

    static float ReadMatFloat(Material m, int id, float fallback)
    {
        if (m != null && m.HasProperty(id))
            return m.GetFloat(id);
        return fallback;
    }

    /// <summary>Unity's sphere mesh has local radius 0.5; convert the desired world radius to local scale.</summary>
    float ResolveLocalScale()
    {
        const float eps = 1e-4f;

        // Direct mode: the transform shows exactly this value; world radius is derived from it.
        if (radiusMode == RadiusMode.ManualLocalScale)
            return Mathf.Max(0.01f, manualLocalScale);

        float worldRadius = ResolveOceanRadiusWorld();
        float parentMax = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
        float denom = 0.5f * Mathf.Max(eps, parentMax);
        return Mathf.Max(0.01f, worldRadius / denom);
    }

    /// <summary>Returns the cached icosphere mesh, rebuilding it if the subdivision level changed.</summary>
    Mesh GetOrBuildSphereMesh()
    {
        int subdiv = Mathf.Clamp(sphereSubdivisions, 1, 8);
        if (_sphereMesh != null && _sphereMeshSubdivisions == subdiv)
            return _sphereMesh;

        _sphereMesh = BuildIcosphere(subdiv, 0.5f);
        _sphereMesh.name = $"OceanIcosphere_{subdiv}";
        _sphereMeshSubdivisions = subdiv;
        return _sphereMesh;
    }

    /// <summary>
    /// Builds a uniform icosphere (subdivided icosahedron) of the given local radius. Uniform triangle
    /// sizes and no pole pinching, so the silhouette is smooth from every angle.
    /// </summary>
    static Mesh BuildIcosphere(int subdivisions, float radius)
    {
        var verts = new List<Vector3>(12);
        var midCache = new Dictionary<long, int>();

        float t = (1f + Mathf.Sqrt(5f)) * 0.5f;
        void AddVertex(float x, float y, float z) => verts.Add(new Vector3(x, y, z).normalized);

        AddVertex(-1, t, 0); AddVertex(1, t, 0); AddVertex(-1, -t, 0); AddVertex(1, -t, 0);
        AddVertex(0, -1, t); AddVertex(0, 1, t); AddVertex(0, -1, -t); AddVertex(0, 1, -t);
        AddVertex(t, 0, -1); AddVertex(t, 0, 1); AddVertex(-t, 0, -1); AddVertex(-t, 0, 1);

        int[] faces =
        {
            0, 11, 5,  0, 5, 1,   0, 1, 7,   0, 7, 10,  0, 10, 11,
            1, 5, 9,   5, 11, 4,  11, 10, 2, 10, 7, 6,  7, 1, 8,
            3, 9, 4,   3, 4, 2,   3, 2, 6,   3, 6, 8,   3, 8, 9,
            4, 9, 5,   2, 4, 11,  6, 2, 10,  8, 6, 7,   9, 8, 1
        };

        var tris = new List<int>(faces);

        for (int s = 0; s < subdivisions; s++)
        {
            var next = new List<int>(tris.Count * 4);
            for (int i = 0; i < tris.Count; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                int ab = MidpointIndex(a, b, verts, midCache);
                int bc = MidpointIndex(b, c, verts, midCache);
                int ca = MidpointIndex(c, a, verts, midCache);

                next.Add(a); next.Add(ab); next.Add(ca);
                next.Add(b); next.Add(bc); next.Add(ab);
                next.Add(c); next.Add(ca); next.Add(bc);
                next.Add(ab); next.Add(bc); next.Add(ca);
            }
            tris = next;
        }

        var finalVerts = new Vector3[verts.Count];
        var normals = new Vector3[verts.Count];
        for (int i = 0; i < verts.Count; i++)
        {
            normals[i] = verts[i];
            finalVerts[i] = verts[i] * radius;
        }

        var mesh = new Mesh
        {
            indexFormat = verts.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16
        };
        mesh.SetVertices(finalVerts);
        mesh.SetNormals(normals);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    static int MidpointIndex(int a, int b, List<Vector3> verts, Dictionary<long, int> cache)
    {
        long key = a < b ? ((long)a << 32) + b : ((long)b << 32) + a;
        if (cache.TryGetValue(key, out int existing))
            return existing;

        Vector3 mid = ((verts[a] + verts[b]) * 0.5f).normalized;
        int index = verts.Count;
        verts.Add(mid);
        cache[key] = index;
        return index;
    }

    static void DestroyColliders(GameObject go)
    {
        if (go == null)
            return;
        Collider[] cols = go.GetComponents<Collider>();
        for (int i = 0; i < cols.Length; i++)
        {
            Collider c = cols[i];
            if (c == null)
                continue;
            DestroyObjectSafe(c);
        }
    }

    static void DestroyObjectSafe(Object obj)
    {
        if (obj == null)
            return;
        if (Application.isPlaying)
        {
            Destroy(obj);
            return;
        }
#if UNITY_EDITOR
        if (EditorUtility.IsPersistent(obj))
            DestroyImmediate(obj, true);
        else
            DestroyImmediate(obj);
#else
        DestroyImmediate(obj);
#endif
    }

    /// <summary>Removes the managed shell child (used by the editor teardown menu).</summary>
    public void RemoveOcean()
    {
        Transform existing = transform.Find(OceanName);
        if (existing != null)
            DestroyObjectSafe(existing.gameObject);
        oceanObj = null;
    }
}
