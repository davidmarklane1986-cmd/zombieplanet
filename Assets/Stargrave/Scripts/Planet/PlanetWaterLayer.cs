using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Animated water sphere with ripples / offshore mask (from Stargrave 1.3). Add to the same GameObject as <see cref="Planet"/>.
/// When present, <see cref="Planet"/> skips its built-in "Water" child so only this layer renders ocean.
/// </summary>
[ExecuteAlways]
public class PlanetWaterLayer : MonoBehaviour
{
    [Header("Water Settings")]
    [Tooltip("Uniform scale of the WaterSphere child when Overwrite Water Sphere Transform And Material is on. If you only drag the child Transform larger than this number, that larger scale is copied here automatically so it survives Play.")]
    public float waterRadius = 303f;
    [Tooltip("Use a material with shader ProceduralPlanets/Planet Water. Leave empty for auto blue translucent ocean.")]
    public Material waterMaterial;
    [Tooltip("When enabled, Water Radius is recomputed from the planet (sea level sampling or ColourSettings offset) whenever the water layer rebuilds. When disabled, the Water Radius value above is kept as-is.")]
    public bool followPlanetRadius = false;
    [Tooltip("Sample terrain meshes on Planet faces and pick a sea radius so roughly this fraction of samples are land (above sea).")]
    public bool autoSeaLevelByLandFraction = true;
    [Range(0.5f, 0.95f)]
    public float landAreaFractionMin = 0.6f;
    [Range(0.5f, 0.95f)]
    public float landAreaFractionMax = 0.8f;
    [Tooltip("Added after sea level (world units).")]
    public float radiusOffset = 0f;
    [Tooltip("Water sphere mesh resolution.")]
    [Range(8, 256)]
    public int waterMeshResolution = 96;
    [Tooltip("When off, this script no longer overwrites the WaterSphere child's local position/scale or MeshRenderer.sharedMaterial on every Play / OnValidate. Turn off to keep your edits on the WaterSphere (material, scale, shadows). Tune color on the material asset or assign Water Material on this component.")]
    public bool overwriteWaterSphereTransformAndMaterial = true;

    [Header("Ripple Settings")]
    public bool animateRipples = true;
    [Range(0f, 5f)]
    public float rippleAmplitude = 1.1f;
    [Range(0f, 0.03f)]
    public float rippleAmplitudeRatio = 0.0015f;
    [Range(0.01f, 4f)]
    public float rippleFrequency = 0.6f;
    [Range(0f, 8f)]
    public float rippleSpeed = 0.95f;
    [Range(0f, 1f)]
    public float secondaryRippleStrength = 0.25f;
    [Range(0.5f, 4f)]
    public float rippleSharpness = 1.4f;

    [Header("Offshore Waves")]
    [Range(0f, 1f)]
    public float shoreDamping = 0.9f;
    [Min(0.01f)]
    public float shoreDepthStart = 6f;
    [Min(0.1f)]
    public float deepWaterDepth = 28f;
    [Range(0f, 1f)]
    public float offshoreSwellStrength = 0.55f;
    [Range(0.1f, 2f)]
    public float offshoreSwellFrequencyScale = 0.45f;

    GameObject waterObj;
    Mesh runtimeWaterMesh;
    Vector3[] baseVertices;
    Vector3[] baseNormals;
    Vector3[] deformedVertices;
    float[] offshoreMask;
    float rippleTimeOffset;
    Transform _planetRoot;
    int _cachedLandSampleVersion = -1;
    float _cachedTargetLandFraction = -1f;
    static readonly List<float> s_radialScratch = new List<float>(16384);

    const string WaterName = "WaterSphere";
    const int MaxLandSampleVertices = 14000;
    const string ProceduralWaterShaderName = "ProceduralPlanets/Planet Water";

    Material _autoWaterMaterial;

    static bool IsProceduralPlanetWater(Material m) =>
        m != null && m.shader != null && m.shader.name == ProceduralWaterShaderName;

    Material GetOrCreateAutoWaterMaterial()
    {
        if (_autoWaterMaterial != null)
            return _autoWaterMaterial;
        Shader sh = Shader.Find(ProceduralWaterShaderName);
        if (sh == null)
            return null;
        _autoWaterMaterial = new Material(sh) { name = "PlanetWater Auto (blue translucent)" };
        _autoWaterMaterial.SetColor("_WaterColor", new Color(0.1f, 0.42f, 0.82f, 0.72f));
        _autoWaterMaterial.SetFloat("_Opacity", 0.5f);
        _autoWaterMaterial.SetFloat("_FresnelPower", 2.2f);
        return _autoWaterMaterial;
    }

    Material ResolveWaterMaterialForRenderer(Planet planet)
    {
        if (IsProceduralPlanetWater(waterMaterial))
            return waterMaterial;
        if (planet != null && planet.colourSettings != null && IsProceduralPlanetWater(planet.colourSettings.waterMaterial))
            return planet.colourSettings.waterMaterial;
        return GetOrCreateAutoWaterMaterial();
    }

    void OnEnable() => CreateOrUpdateWater();

    void OnValidate()
    {
        if (Application.isPlaying)
            return;
        CreateOrUpdateWater();
    }

    void Update()
    {
        if (!animateRipples)
            return;
        if (!EnsureRuntimeAnimationState())
            return;
        float t = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
        AnimateWaterSurface(t + rippleTimeOffset);
    }

    public void CreateOrUpdateWater()
    {
        _planetRoot = transform;
        MaybePromoteWaterRadiusFromWaterSphereChild();
        UpdateWaterRadiusFromPlanet();

        Planet planet = GetComponent<Planet>() ?? GetComponentInParent<Planet>();
        if (planet != null && planet.colourSettings != null && !planet.colourSettings.useWater)
        {
            if (waterObj != null)
                waterObj.SetActive(false);
            return;
        }

        Transform existing = transform.Find(WaterName);
        bool createdNewSphere = false;
        if (existing != null)
            waterObj = existing.gameObject;
        else
        {
            waterObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            waterObj.name = WaterName;
            waterObj.transform.SetParent(transform);
            createdNewSphere = true;
            if (waterObj.TryGetComponent(out Collider c))
            {
                if (Application.isPlaying)
                    Destroy(c);
                else
                    DestroyImmediate(c);
            }
        }

        ApplyHighResolutionWaterMesh();
        EnsureRuntimeWaterMesh();

        if (overwriteWaterSphereTransformAndMaterial || createdNewSphere)
        {
            waterObj.transform.localPosition = Vector3.zero;
            waterObj.transform.localScale = Vector3.one * waterRadius;

            var renderer = waterObj.GetComponent<MeshRenderer>();
            Material mat = ResolveWaterMaterialForRenderer(planet);
            if (mat != null)
                renderer.sharedMaterial = mat;
        }

        if (offshoreMask != null && baseVertices != null && baseVertices.Length > 0)
            BuildOffshoreMask();

        waterObj.SetActive(true);

        if (runtimeWaterMesh != null)
        {
            float t = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
            AnimateWaterSurface(t + rippleTimeOffset);
        }
    }

    /// <summary>
    /// When <see cref="overwriteWaterSphereTransformAndMaterial"/> applies scale from <see cref="waterRadius"/>,
    /// editing only the WaterSphere child's Transform leaves <see cref="waterRadius"/> stale (often 300).
    /// If the child is uniformly scaled larger than <see cref="waterRadius"/>, adopt that value so Play mode matches the hierarchy.
    /// </summary>
    void MaybePromoteWaterRadiusFromWaterSphereChild()
    {
        if (followPlanetRadius)
            return;
        Transform t = transform.Find(WaterName);
        if (t == null)
            return;
        Vector3 ls = t.localScale;
        float u = Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));
        if (u > waterRadius + 0.01f)
            waterRadius = u;
    }

    void UpdateWaterRadiusFromPlanet()
    {
        if (!followPlanetRadius)
            return;

        Planet planet = GetComponent<Planet>() ?? GetComponentInParent<Planet>();
        if (planet == null)
            return;

        if (planet.colourSettings == null || !planet.colourSettings.useWater)
            return;

        float uniformScale = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
        float offsetWorld = radiusOffset;
        float offsetLocal = offsetWorld / Mathf.Max(1e-4f, uniformScale);

        if (autoSeaLevelByLandFraction && TryComputeSeaRadiusFromPlanet(planet, out float seaWorld))
            waterRadius = Mathf.Max(0.01f, seaWorld / Mathf.Max(1e-4f, uniformScale) + offsetLocal);
        else
        {
            float w = planet.GetWaterRadiusWorld();
            if (w <= 0f)
                return;
            waterRadius = Mathf.Max(0.01f, w / Mathf.Max(1e-4f, uniformScale) + offsetLocal);
        }

        if (waterMaterial == null && planet.colourSettings.waterMaterial != null &&
            IsProceduralPlanetWater(planet.colourSettings.waterMaterial))
            waterMaterial = planet.colourSettings.waterMaterial;
    }

    bool TryComputeSeaRadiusFromPlanet(Planet planet, out float seaRadiusWorld)
    {
        seaRadiusWorld = 0f;
        if (planet == null)
            return false;

        Vector3 center = planet.transform.position;
        var verts = new List<Vector3>(4096);
        s_radialScratch.Clear();

        int landVersion = 0;
        foreach (var mf in planet.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null)
                continue;
            string goName = mf.gameObject.name;
            if (goName.Contains("Water") || goName.Contains("Atmosphere") || goName.Contains("Clouds"))
                continue;
            if (mf.GetComponent<PlanetWaterLayer>() != null)
                continue;

            Mesh mesh = mf.sharedMesh;
            mesh.GetVertices(verts);
            int vCount = verts.Count;
            landVersion += vCount ^ (goName.GetHashCode() * 397);
            if (vCount == 0)
                continue;
            int stride = Mathf.Max(1, vCount / MaxLandSampleVertices);
            for (int i = 0; i < vCount; i += stride)
            {
                Vector3 world = mf.transform.TransformPoint(verts[i]);
                s_radialScratch.Add(Vector3.Distance(world, center));
            }
        }

        if (s_radialScratch.Count < 16)
            return false;

        if (landVersion != _cachedLandSampleVersion)
        {
            _cachedLandSampleVersion = landVersion;
            float lo = Mathf.Min(landAreaFractionMin, landAreaFractionMax);
            float hi = Mathf.Max(landAreaFractionMin, landAreaFractionMax);
            _cachedTargetLandFraction = Mathf.Clamp(Random.Range(lo, hi), 0.05f, 0.95f);
        }

        float landFraction = Mathf.Clamp(_cachedTargetLandFraction, 0.05f, 0.95f);
        s_radialScratch.Sort();
        int m = s_radialScratch.Count;
        float underwaterFraction = 1f - landFraction;
        float t = underwaterFraction * (m - 1);
        int i0 = Mathf.Clamp(Mathf.FloorToInt(t), 0, m - 1);
        int i1 = Mathf.Clamp(i0 + 1, 0, m - 1);
        float f = t - Mathf.Floor(t);
        seaRadiusWorld = Mathf.Lerp(s_radialScratch[i0], s_radialScratch[i1], f);
        return true;
    }

    public Vector3 GetWaterShellWorldCenter()
    {
        if (waterObj == null)
            return transform.position;
        if (waterObj.TryGetComponent(out MeshRenderer renderer))
            return renderer.bounds.center;
        return transform.position;
    }

    public float GetWorldWaterShellRadius()
    {
        if (waterObj == null)
            return -1f;
        if (waterObj.TryGetComponent(out MeshRenderer renderer))
            return Mathf.Max(renderer.bounds.extents.x, renderer.bounds.extents.y, renderer.bounds.extents.z);
        float sMax = Mathf.Max(
            Mathf.Abs(waterObj.transform.lossyScale.x),
            Mathf.Abs(waterObj.transform.lossyScale.y),
            Mathf.Abs(waterObj.transform.lossyScale.z));
        return 0.5f * sMax;
    }

    public bool IsUnderwaterWorldPoint(Vector3 worldPoint, float epsilon = 0f)
    {
        float shell = GetWorldWaterShellRadius();
        if (shell <= 0f)
            return false;
        float d = Vector3.Distance(worldPoint, GetWaterShellWorldCenter());
        return d < shell - epsilon;
    }

    void ApplyHighResolutionWaterMesh()
    {
        if (waterObj == null)
            return;
        MeshFilter mf = waterObj.GetComponent<MeshFilter>();
        if (mf == null)
            mf = waterObj.AddComponent<MeshFilter>();
        int res = Mathf.Clamp(waterMeshResolution, 8, 256);
        mf.sharedMesh = BuildUvSphere(res);
    }

    bool EnsureRuntimeAnimationState()
    {
        if (waterObj == null)
        {
            Transform existing = transform.Find(WaterName);
            if (existing == null)
                return false;
            waterObj = existing.gameObject;
        }

        if (runtimeWaterMesh == null || baseVertices == null || baseVertices.Length == 0)
            EnsureRuntimeWaterMesh();

        if (runtimeWaterMesh == null)
            return false;

        if (baseVertices == null || baseVertices.Length == 0 || baseNormals == null || baseNormals.Length != baseVertices.Length)
            CacheMeshDataIfNeeded();

        return baseVertices != null && baseNormals != null && deformedVertices != null && baseVertices.Length > 0;
    }

    void EnsureRuntimeWaterMesh()
    {
        if (waterObj == null)
            return;
        MeshFilter mf = waterObj.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null)
            return;

        if (Application.isPlaying)
        {
            runtimeWaterMesh = mf.mesh;
            if (runtimeWaterMesh != null)
                runtimeWaterMesh.MarkDynamic();
        }
        else
        {
            Mesh source = mf.sharedMesh;
            if (runtimeWaterMesh == null || runtimeWaterMesh.vertexCount != source.vertexCount)
            {
                runtimeWaterMesh = Instantiate(source);
                runtimeWaterMesh.name = $"{source.name}_RuntimeRipple";
                runtimeWaterMesh.MarkDynamic();
                mf.sharedMesh = runtimeWaterMesh;
            }
        }

        CacheMeshDataIfNeeded();
    }

    void CacheMeshDataIfNeeded()
    {
        if (runtimeWaterMesh == null)
            return;
        baseVertices = runtimeWaterMesh.vertices;
        if (baseVertices == null || baseVertices.Length == 0)
            return;
        baseNormals = runtimeWaterMesh.normals;
        if (baseNormals == null || baseNormals.Length != baseVertices.Length)
        {
            runtimeWaterMesh.RecalculateNormals();
            baseNormals = runtimeWaterMesh.normals;
        }
        if (deformedVertices == null || deformedVertices.Length != baseVertices.Length)
            deformedVertices = new Vector3[baseVertices.Length];
        if (offshoreMask == null || offshoreMask.Length != baseVertices.Length)
            offshoreMask = new float[baseVertices.Length];
        if (Mathf.Approximately(rippleTimeOffset, 0f))
            rippleTimeOffset = Random.value * 100f;
        BuildOffshoreMask();
    }

    void AnimateWaterSurface(float timeValue)
    {
        if (runtimeWaterMesh == null || baseVertices == null || baseNormals == null || deformedVertices == null)
            return;

        float minScaledAmplitude = Mathf.Max(0f, waterRadius * rippleAmplitudeRatio);
        float effectiveAmplitude = Mathf.Max(Mathf.Max(0f, rippleAmplitude), minScaledAmplitude);
        float amplitudeOnUnitSphere = effectiveAmplitude / Mathf.Max(0.01f, waterRadius);
        float frequency = Mathf.Max(0.01f, rippleFrequency);
        float speed = Mathf.Max(0f, rippleSpeed);
        float secondStrength = Mathf.Clamp01(secondaryRippleStrength);
        float sharpness = Mathf.Max(0.5f, rippleSharpness);
        float swellStrength = Mathf.Clamp01(offshoreSwellStrength);
        float swellFrequency = frequency * Mathf.Max(0.1f, offshoreSwellFrequencyScale);
        float shoreCalmMultiplier = Mathf.Clamp01(1f - shoreDamping);

        float t = timeValue * speed;
        for (int i = 0; i < baseVertices.Length; i++)
        {
            Vector3 p = baseVertices[i];
            Vector3 n = baseNormals[i].normalized;
            float offshore = offshoreMask != null && i < offshoreMask.Length ? offshoreMask[i] : 1f;
            float nearShoreFactor = Mathf.Lerp(shoreCalmMultiplier, 1f, offshore);

            float primary = Mathf.Sin((p.x + p.z) * frequency * 9.5f + t);
            float secondary = Mathf.Sin((p.x * 1.53f - p.y * 1.08f + p.z * 0.59f) * frequency * 7.0f - t * 0.8f);
            float wave = primary + (secondary * secondStrength);
            float shapedWave = Mathf.Sign(wave) * Mathf.Pow(Mathf.Abs(wave), sharpness);
            float swell = Mathf.Sin((p.x * 0.81f + p.z * 1.17f - p.y * 0.22f) * swellFrequency * 5.5f + t * 0.55f);
            swell *= swellStrength * offshore * offshore;
            float rippleTerm = shapedWave * nearShoreFactor * 0.75f;
            float swellTerm = swell * 1.35f;
            deformedVertices[i] = p + n * ((rippleTerm + swellTerm) * amplitudeOnUnitSphere);
        }

        runtimeWaterMesh.vertices = deformedVertices;
        runtimeWaterMesh.RecalculateNormals();
        runtimeWaterMesh.RecalculateBounds();
    }

    void BuildOffshoreMask()
    {
        if (offshoreMask == null || baseVertices == null || waterObj == null || _planetRoot == null)
            return;

        float depthStart = Mathf.Max(0.01f, shoreDepthStart);
        float depthEnd = Mathf.Max(depthStart + 0.01f, deepWaterDepth);
        Vector3 center = _planetRoot.position;
        float shellExtent = waterRadius;
        if (waterObj.TryGetComponent(out MeshRenderer shellR))
        {
            float ext = Mathf.Max(shellR.bounds.extents.x, shellR.bounds.extents.y, shellR.bounds.extents.z);
            if (ext > 0.01f)
                shellExtent = ext;
        }
        float rayLength = Mathf.Max(1f, shellExtent * 2.5f);

        for (int i = 0; i < baseVertices.Length; i++)
        {
            Vector3 worldWater = waterObj.transform.TransformPoint(baseVertices[i]);
            Vector3 dirToCenter = (center - worldWater).normalized;
            Ray ray = new Ray(worldWater, dirToCenter);

            if (Physics.Raycast(ray, out RaycastHit hit, rayLength, ~0, QueryTriggerInteraction.Ignore) &&
                hit.collider != null &&
                hit.collider.transform.IsChildOf(_planetRoot))
            {
                float depth = Mathf.Max(0f, Vector3.Distance(worldWater, hit.point));
                offshoreMask[i] = Mathf.InverseLerp(depthStart, depthEnd, depth);
            }
            else
                offshoreMask[i] = 1f;
        }
    }

    static Mesh BuildUvSphere(int resolution)
    {
        int latitudeSegments = Mathf.Clamp(resolution, 8, 256);
        int longitudeSegments = latitudeSegments * 2;

        var vertices = new List<Vector3>((latitudeSegments + 1) * (longitudeSegments + 1));
        var normals = new List<Vector3>((latitudeSegments + 1) * (longitudeSegments + 1));
        var uvs = new List<Vector2>((latitudeSegments + 1) * (longitudeSegments + 1));
        var triangles = new List<int>(latitudeSegments * longitudeSegments * 6);

        for (int lat = 0; lat <= latitudeSegments; lat++)
        {
            float v = lat / (float)latitudeSegments;
            float phi = Mathf.PI * v;
            float y = Mathf.Cos(phi);
            float r = Mathf.Sin(phi);

            for (int lon = 0; lon <= longitudeSegments; lon++)
            {
                float u = lon / (float)longitudeSegments;
                float theta = 2f * Mathf.PI * u;
                float x = Mathf.Cos(theta) * r;
                float z = Mathf.Sin(theta) * r;
                Vector3 p = new Vector3(x, y, z);
                vertices.Add(p);
                normals.Add(p.normalized);
                uvs.Add(new Vector2(u, 1f - v));
            }
        }

        int row = longitudeSegments + 1;
        for (int lat = 0; lat < latitudeSegments; lat++)
        {
            for (int lon = 0; lon < longitudeSegments; lon++)
            {
                int current = lat * row + lon;
                int next = current + row;

                triangles.Add(current);
                triangles.Add(next + 1);
                triangles.Add(next);

                triangles.Add(current);
                triangles.Add(current + 1);
                triangles.Add(next + 1);
            }
        }

        var mesh = new Mesh();
        mesh.name = $"WaterSphere_UV_{latitudeSegments}";
        if (vertices.Count > 65535)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }
}
