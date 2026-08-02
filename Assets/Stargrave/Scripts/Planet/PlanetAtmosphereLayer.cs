using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Soft additive atmospheric rim around the planet (URP). Sized from the terrain mesh bounds so it sits outside geometry.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class PlanetAtmosphereLayer : MonoBehaviour
{
    public enum AtmosphereMode
    {
        FresnelRim = 0,
        Scattering = 1
    }

    static readonly int PlanetCenterId = Shader.PropertyToID("_PlanetCenterWS");
    static readonly int SunDirId = Shader.PropertyToID("_SunDirWS");
    static readonly int NightRimMulId = Shader.PropertyToID("_NightRimMul");
    static readonly int DayRimCurveId = Shader.PropertyToID("_DayRimCurve");
    static readonly int PlanetRadiusId = Shader.PropertyToID("_PlanetRadius");
    static readonly int AtmosphereRadiusId = Shader.PropertyToID("_AtmosphereRadius");
    static readonly int ScatteringColorId = Shader.PropertyToID("_ScatteringColor");
    static readonly int DensityFalloffId = Shader.PropertyToID("_DensityFalloff");
    static readonly int SunIntensityId = Shader.PropertyToID("_SunIntensity");
    static readonly int MieStrengthId = Shader.PropertyToID("_MieStrength");
    static readonly int MiePowerId = Shader.PropertyToID("_MiePower");
    static readonly int NightEmissionId = Shader.PropertyToID("_NightEmission");
    static readonly int NightSkyVisibilityId = Shader.PropertyToID("_NightSkyVisibility");
    static readonly int SunsetColorId = Shader.PropertyToID("_SunsetColor");
    static readonly int SunsetStrengthId = Shader.PropertyToID("_SunsetStrength");

    [Header("Mode")]
    public AtmosphereMode atmosphereMode = AtmosphereMode.Scattering;

    [Header("Size")]
    [Tooltip("Uniform local scale on AtmosphereShell (Unity sphere mesh radius is 0.5). Use 0 for automatic sizing from terrain bounds / Planet.shapeSettings.planetRadius.")]
    [Min(0f)]
    public float atmosphereUniformLocalScale = 0f;
    [Tooltip("Extra world-space padding beyond the terrain renderer bounds before applying the multiplier.")]
    [Min(0f)]
    public float extraPaddingWorld = 2.5f;
    [Tooltip("Scale on top of (terrain bounds + padding). Slightly > 1 so the shell is outside the mesh.")]
    [Min(1.001f)]
    public float radiusMultiplier = 1.02f;

    [Header("Look (additive rim)")]
    public Material atmosphereMaterial;
    public Color rimColor = new Color(0.4f, 0.7f, 1f, 1f);
    [Min(0.2f)] public float fresnelPower = 1.55f;
    [Min(0f)] public float intensity = 1.15f;
    [Range(0f, 0.35f)] public float nightRimMultiplier = 0.08f;
    [Range(0.25f, 4f)] public float dayRimCurve = 1.35f;
    [Tooltip("If the bright atmosphere rim is on the wrong hemisphere, toggle this.")]
    public bool flipDayNightHemisphere = true;

    [Header("Look (scattering)")]
    public Color scatteringColor = new Color(0.55f, 0.72f, 1f, 1f);
    [Range(0.25f, 12f)] public float densityFalloff = 4.5f;
    [Range(0f, 12f)] public float sunIntensity = 2.0f;
    [Range(0f, 2f)] public float mieStrength = 0.25f;
    [Range(1f, 32f)] public float miePower = 8f;
    [Range(0f, 0.6f)] public float nightEmission = 0.05f;
    [Range(0f, 1f)] public float nightSkyVisibility = 0.75f;
    public Color sunsetColor = new Color(1f, 0.52f, 0.2f, 1f);
    [Range(0f, 2f)] public float sunsetStrength = 0.9f;

    [Header("Distance / Atmospheric Fog")]
    [Tooltip("Blend fog settings based on day/night using the sun direction. Soft day haze + denser night fog.")]
    public bool controlFogWithDayNight = true;
    // Start dusk fog while the sun is still a bit above the local horizon (reduces silver wash).
    [Range(-1f, 1f)] public float nightStartsAtSunDot = 0.18f;
    [Range(0f, 1f)] public float transitionWidth = 0.28f;
    [Tooltip("If enabled, daytime distance haze is forced off (not recommended — kills scale cue).")]
    public bool strictNoDayFog = false;
    [Tooltip("Use camera position relative to planet to evaluate local day/night (recommended for spherical worlds).")]
    public bool useCameraLocalDayNight = true;
    [Min(0f)] public float fogBlendSpeed = 2f;
    [Tooltip("Linear start/end gives a clear haze belt for scale. Exp/Exp2 also supported via density.")]
    public FogMode fogMode = FogMode.Linear;
    // Day haze should read as soft sky blue (scale cue); night stays dark.
    [ColorUsage(false, true)] public Color dayFogColor = new Color(0.55f, 0.68f, 0.86f, 1f);
    [ColorUsage(false, true)] public Color nightFogColor = new Color(0.02f, 0.03f, 0.06f, 1f);
    [Tooltip("For testing: force full night fog regardless of sun angle.")]
    public bool forceNightFogDebug = false;
    [Min(0f)] public float dayFogDensity = 0.0025f;
    [Min(0f)] public float nightFogDensity = 0.012f;
    [Min(0f)] public float dayFogStartDistance = 140f;
    [Min(0f)] public float nightFogStartDistance = 50f;
    [Min(0f)] public float dayFogEndDistance = 1200f;
    [Min(0f)] public float nightFogEndDistance = 420f;

    private GameObject atmosphereObj;
    private Material _runtimeMaterial;
    private const string AtmosphereName = "AtmosphereShell";
    private bool _cachedFog;
    private FogMode _cachedFogMode;
    private Color _cachedFogColor;
    private float _cachedFogDensity;
    private float _cachedFogStartDistance;
    private float _cachedFogEndDistance;
    private bool _fogStateCached;
    private float _nightFogBlend;
#if UNITY_EDITOR
    private bool _validateRefreshQueued;
#endif

    void OnEnable()
    {
        EnsureRenderSettingsSun();
        CacheFogSettings();
        CreateOrUpdateAtmosphere();
    }

    void OnDisable()
    {
        RestoreFogSettings();
    }

    public void ApplyCinematicScatteringPreset()
    {
        atmosphereMode = AtmosphereMode.Scattering;
        atmosphereUniformLocalScale = 1200f;
        radiusMultiplier = 1.045f;
        extraPaddingWorld = 3f;

        scatteringColor = new Color(0.52f, 0.74f, 1f, 1f);
        densityFalloff = 3.2f;
        sunIntensity = 4.8f;
        mieStrength = 0.62f;
        miePower = 6.5f;
        nightEmission = 0.09f;
        nightSkyVisibility = 0.82f;
        sunsetColor = new Color(1f, 0.5f, 0.18f, 1f);
        sunsetStrength = 1.1f;
        flipDayNightHemisphere = true;
        controlFogWithDayNight = true;
    }

    static void EnsureRenderSettingsSun()
    {
        if (RenderSettings.sun != null)
            return;
        foreach (Light L in FindObjectsByType<Light>(FindObjectsInactive.Exclude))
        {
            if (L.type != LightType.Directional || !L.isActiveAndEnabled)
                continue;
            RenderSettings.sun = L;
            return;
        }
    }

    void OnValidate()
    {
#if UNITY_EDITOR
        if (!isActiveAndEnabled || _validateRefreshQueued)
            return;
        _validateRefreshQueued = true;
        EditorApplication.delayCall += DelayedValidateRefresh;
#else
        CreateOrUpdateAtmosphere();
#endif
    }

#if UNITY_EDITOR
    private void DelayedValidateRefresh()
    {
        _validateRefreshQueued = false;
        if (this == null)
            return;
        if (!isActiveAndEnabled)
            return;
        CreateOrUpdateAtmosphere();
    }
#endif

    void LateUpdate()
    {
        bool hasActiveDirectional;
        Vector3 sunDir = PushSunAndPlanetToMaterial(out hasActiveDirectional);
        UpdateNightFog(sunDir, hasActiveDirectional);
    }

    /// <summary>
    /// Visual-only shell must never have colliders — otherwise the player walks / raycasts on the atmosphere.
    /// Stripping ran only on newly created primitives before; reused shells from prefabs/scenes kept a sphere collider.
    /// </summary>
    static void DestroyAtmosphereColliders(GameObject go)
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

    /// <summary>
    /// Matches "AtmosphereShell" and Unity duplicate names "AtmosphereShell (1)".
    /// transform.Find(AtmosphereName) only sees the exact string — duplicates led to a second shell and Z-fight / additive brightness pops.
    /// </summary>
    static bool IsAtmosphereShellChildName(string childName)
    {
        if (childName == AtmosphereName)
            return true;
        if (!childName.StartsWith(AtmosphereName + " (", System.StringComparison.Ordinal))
            return false;
        return childName.EndsWith(")", System.StringComparison.Ordinal);
    }

    static void DestroyShellGameObject(GameObject go)
    {
        if (go == null)
            return;
        DestroyObjectSafe(go);
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
        // OnValidate can run while editing prefab assets; persistent objects require allowDestroyingAssets=true.
        if (EditorUtility.IsPersistent(obj))
            DestroyImmediate(obj, true);
        else
            DestroyImmediate(obj);
#else
        DestroyImmediate(obj);
#endif
    }

    /// <summary>
    /// Keep a single atmosphere child: prefer exact name, destroy extras (duplicate additive shells caused visible brightness snaps).
    /// </summary>
    void ResolveSingleAtmosphereShellChild()
    {
        Transform keeper = null;
        for (int i = 0; i < transform.childCount; i++)
        {
            Transform c = transform.GetChild(i);
            if (!IsAtmosphereShellChildName(c.name))
                continue;
            if (keeper == null)
            {
                keeper = c;
                continue;
            }
            if (c.name == AtmosphereName)
            {
                DestroyShellGameObject(keeper.gameObject);
                keeper = c;
            }
            else
                DestroyShellGameObject(c.gameObject);
        }

        if (keeper != null && keeper.name != AtmosphereName)
            keeper.name = AtmosphereName;
    }

    public void CreateOrUpdateAtmosphere()
    {
        ResolveSingleAtmosphereShellChild();

        Transform existing = transform.Find(AtmosphereName);
        if (existing != null)
            atmosphereObj = existing.gameObject;
        else
        {
            atmosphereObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            atmosphereObj.name = AtmosphereName;
            atmosphereObj.transform.SetParent(transform);
        }

        DestroyAtmosphereColliders(atmosphereObj);

        atmosphereObj.transform.localPosition = Vector3.zero;

        float uniformScale = ResolveAtmosphereUniformLocalScale();
        atmosphereObj.transform.localScale = Vector3.one * uniformScale;

        MeshRenderer mr = atmosphereObj.GetComponent<MeshRenderer>();
        mr.enabled = true;
        atmosphereObj.SetActive(true);
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = LightProbeUsage.Off;
        mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
        mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        mr.sortingOrder = 50;

        Shader shader = GetDefaultShaderForMode();
        if (atmosphereMaterial != null)
        {
            mr.sharedMaterial = atmosphereMaterial;
        }
        else if (shader != null)
        {
            if (_runtimeMaterial == null || _runtimeMaterial.shader != shader)
                _runtimeMaterial = new Material(shader);
            mr.sharedMaterial = _runtimeMaterial;
        }
        else if (atmosphereMaterial == null)
        {
            Debug.LogWarning(
                "PlanetAtmosphereLayer: Could not resolve atmosphere shader (Stargrave/Planet Atmosphere Scattering/Fresnel). " +
                "Assign atmosphereMaterial or include the shaders in Graphics settings Always Included.",
                this);
        }

        if (mr.sharedMaterial != null)
            mr.sharedMaterial.renderQueue = 3200;

        bool _;
        PushSunAndPlanetToMaterial(out _);
    }

    Vector3 PushSunAndPlanetToMaterial(out bool hasActiveDirectional)
    {
        hasActiveDirectional = false;
        if (atmosphereObj == null)
            return Vector3.up;
        MeshRenderer mr = atmosphereObj.GetComponent<MeshRenderer>();
        if (mr == null || mr.sharedMaterial == null)
            return Vector3.up;

        Material m = mr.sharedMaterial;
        if (m.shader == null)
            return Vector3.up;

        m.SetVector(PlanetCenterId, transform.position);

        // Hemisphere for atmosphere uses "from planet center toward sun". Unity's directional forward is light-travel axis, so flip if needed.
        Vector3 sunDir = ResolveDirectionalLightDir(out hasActiveDirectional);

        Vector3 materialSunDir = flipDayNightHemisphere ? -sunDir : sunDir;
        m.SetVector(SunDirId, new Vector4(materialSunDir.x, materialSunDir.y, materialSunDir.z, 0f));

        string shaderName = m.shader.name;
        if (shaderName == "Stargrave/Planet Atmosphere Fresnel")
        {
            m.SetColor("_RimColor", rimColor);
            m.SetFloat("_FresnelPower", fresnelPower);
            m.SetFloat("_Intensity", intensity);
            m.SetFloat(NightRimMulId, nightRimMultiplier);
            m.SetFloat(DayRimCurveId, dayRimCurve);
            return sunDir;
        }

        if (shaderName == "Stargrave/Planet Atmosphere Scattering")
        {
            float atmosphereRadius = Mathf.Max(0.5f, atmosphereObj.transform.lossyScale.x * 0.5f);
            float planetRadius = Mathf.Max(0.01f, ComputePlanetRadiusWorld());
            planetRadius = Mathf.Min(planetRadius, atmosphereRadius - 0.001f);

            m.SetFloat(PlanetRadiusId, planetRadius);
            m.SetFloat(AtmosphereRadiusId, atmosphereRadius);
            m.SetColor(ScatteringColorId, scatteringColor);
            m.SetFloat(DensityFalloffId, densityFalloff);
            m.SetFloat(SunIntensityId, sunIntensity);
            m.SetFloat(MieStrengthId, mieStrength);
            m.SetFloat(MiePowerId, miePower);
            m.SetFloat(NightEmissionId, nightEmission);
            m.SetFloat(NightSkyVisibilityId, nightSkyVisibility);
            m.SetColor(SunsetColorId, sunsetColor);
            m.SetFloat(SunsetStrengthId, sunsetStrength);
        }

        return sunDir;
    }

    static Vector3 ResolveDirectionalLightDir(out bool hasActiveDirectional)
    {
        hasActiveDirectional = false;
        if (RenderSettings.sun != null)
        {
            hasActiveDirectional = RenderSettings.sun.isActiveAndEnabled;
            return RenderSettings.sun.transform.forward.normalized;
        }

        Light fallbackAny = null;
        foreach (Light l in FindObjectsByType<Light>(FindObjectsInactive.Include))
        {
            if (l == null || l.type != LightType.Directional)
                continue;
            if (l.isActiveAndEnabled)
            {
                hasActiveDirectional = true;
                return l.transform.forward.normalized;
            }
            if (fallbackAny == null)
                fallbackAny = l;
        }

        if (fallbackAny != null)
            return fallbackAny.transform.forward.normalized;
        return Vector3.up;
    }

    void CacheFogSettings()
    {
        _cachedFog = RenderSettings.fog;
        _cachedFogMode = RenderSettings.fogMode;
        _cachedFogColor = RenderSettings.fogColor;
        _cachedFogDensity = RenderSettings.fogDensity;
        _cachedFogStartDistance = RenderSettings.fogStartDistance;
        _cachedFogEndDistance = RenderSettings.fogEndDistance;
        _fogStateCached = true;
    }

    void RestoreFogSettings()
    {
        if (!_fogStateCached)
            return;
        RenderSettings.fog = _cachedFog;
        RenderSettings.fogMode = _cachedFogMode;
        RenderSettings.fogColor = _cachedFogColor;
        RenderSettings.fogDensity = _cachedFogDensity;
        RenderSettings.fogStartDistance = _cachedFogStartDistance;
        RenderSettings.fogEndDistance = _cachedFogEndDistance;
    }

    void UpdateNightFog(Vector3 sunDir, bool hasActiveDirectional)
    {
        if (!controlFogWithDayNight)
            return;

        // Direction from world point toward sun.
        Vector3 worldToSun = -sunDir;
        float globalSunDotUp = Vector3.Dot(Vector3.up, worldToSun);
        float sunDotUp = globalSunDotUp;
        Camera mainCam = useCameraLocalDayNight ? RuntimeSceneRefs.GetMainCamera() : null;
        if (useCameraLocalDayNight && mainCam != null)
        {
            Vector3 toCamera = (mainCam.transform.position - transform.position).normalized;
            // Local day/night at camera position on spherical worlds.
            sunDotUp = Vector3.Dot(toCamera, worldToSun);
        }
        float width = Mathf.Max(0.0001f, transitionWidth);
        float targetNightBlend;
        if (forceNightFogDebug)
        {
            targetNightBlend = 1f;
        }
        else
        {
            // Day stays at 0 until sunDot <= threshold, then ramps to 1 over transitionWidth.
            // Use both local and global checks and keep the stronger night signal to avoid "stuck day" edge cases.
            float nightTLocal = Mathf.InverseLerp(nightStartsAtSunDot, nightStartsAtSunDot - width, sunDotUp);
            float nightTGlobal = Mathf.InverseLerp(nightStartsAtSunDot, nightStartsAtSunDot - width, globalSunDotUp);
            float nightT = Mathf.Max(nightTLocal, nightTGlobal);
            targetNightBlend = Mathf.SmoothStep(0f, 1f, nightT);
            if (!hasActiveDirectional)
                targetNightBlend = 1f;
            if (strictNoDayFog && hasActiveDirectional && sunDotUp > nightStartsAtSunDot && globalSunDotUp > nightStartsAtSunDot)
                targetNightBlend = 0f;
        }
        float dt = Application.isPlaying ? Time.deltaTime : 0.016f;
        if (strictNoDayFog && hasActiveDirectional && sunDotUp > nightStartsAtSunDot && globalSunDotUp > nightStartsAtSunDot)
            _nightFogBlend = 0f;
        _nightFogBlend = Mathf.MoveTowards(_nightFogBlend, targetNightBlend, fogBlendSpeed * dt);

        float density = Mathf.Lerp(dayFogDensity, nightFogDensity, _nightFogBlend);
        float startDistance = Mathf.Lerp(dayFogStartDistance, nightFogStartDistance, _nightFogBlend);
        float endDistance = Mathf.Lerp(dayFogEndDistance, nightFogEndDistance, _nightFogBlend);

        // Keep fog on whenever day or night haze has any strength (avoids shader fog-keyword flicker).
        bool hasLinearHaze = fogMode == FogMode.Linear && endDistance > startDistance + 1f;
        RenderSettings.fog = density > 0.00005f || hasLinearHaze || _nightFogBlend > 0.001f;
        RenderSettings.fogMode = fogMode;
        // Pull fog colour toward night faster than density so dusk isn't silver-grey.
        float fogColorNight = 1f - (1f - _nightFogBlend) * (1f - _nightFogBlend);
        Color fogCol = Color.Lerp(dayFogColor, nightFogColor, fogColorNight);
        // Day haze may be sky-bright for scale; night stays dark (no silver wash).
        float maxLum = Mathf.Lerp(0.55f, 0.06f, fogColorNight);
        float lum = fogCol.r * 0.2126f + fogCol.g * 0.7152f + fogCol.b * 0.0722f;
        if (lum > maxLum && lum > 1e-4f)
            fogCol *= maxLum / lum;
        RenderSettings.fogColor = fogCol;
        RenderSettings.fogDensity = density;
        RenderSettings.fogStartDistance = startDistance;
        RenderSettings.fogEndDistance = Mathf.Max(startDistance + 0.01f, endDistance);
    }

    Shader GetDefaultShaderForMode()
    {
        if (atmosphereMode == AtmosphereMode.Scattering)
        {
            Shader scatter = Shader.Find("Stargrave/Planet Atmosphere Scattering");
            if (scatter != null)
                return scatter;
        }

        return Shader.Find("Stargrave/Planet Atmosphere Fresnel");
    }

    /// <summary>
    /// Unity's sphere mesh has local radius 0.5. When <see cref="atmosphereUniformLocalScale"/> is 0,
    /// derive uniform local scale so the shell matches <see cref="ComputeShellRadiusWorld"/>.
    /// </summary>
    float ResolveAtmosphereUniformLocalScale()
    {
        const float eps = 1e-4f;
        if (atmosphereUniformLocalScale > eps)
            return atmosphereUniformLocalScale;

        float shellRadiusWorld = ComputeShellRadiusWorld();
        float parentMax = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
        float denom = 0.5f * Mathf.Max(eps, parentMax);
        return Mathf.Max(0.01f, shellRadiusWorld / denom);
    }

    float ComputeShellRadiusWorld()
    {
        MeshRenderer terrainMr = GetComponent<MeshRenderer>();
        MeshFilter mf = GetComponent<MeshFilter>();
        float r = 55f;

        if (terrainMr != null && terrainMr.bounds.size.sqrMagnitude > 1e-6f)
        {
            r = Mathf.Max(terrainMr.bounds.extents.x, terrainMr.bounds.extents.y, terrainMr.bounds.extents.z);
            r += extraPaddingWorld;
        }
        else if (mf != null && mf.sharedMesh != null)
        {
            Bounds b = mf.sharedMesh.bounds;
            float ext = Mathf.Max(b.extents.x, b.extents.y, b.extents.z);
            float s = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
            r = ext * s + extraPaddingWorld;
        }
        else
        {
            Planet planet = GetComponent<Planet>();
            if (planet != null && planet.shapeSettings != null)
            {
                float s = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
                r = planet.shapeSettings.planetRadius * s + extraPaddingWorld;
            }
        }

        return Mathf.Max(0.5f, r * radiusMultiplier);
    }

    float ComputePlanetRadiusWorld()
    {
        MeshRenderer terrainMr = GetComponent<MeshRenderer>();
        MeshFilter mf = GetComponent<MeshFilter>();
        float r = 50f;

        if (terrainMr != null && terrainMr.bounds.size.sqrMagnitude > 1e-6f)
            r = Mathf.Max(terrainMr.bounds.extents.x, terrainMr.bounds.extents.y, terrainMr.bounds.extents.z);
        else if (mf != null && mf.sharedMesh != null)
        {
            Bounds b = mf.sharedMesh.bounds;
            float ext = Mathf.Max(b.extents.x, b.extents.y, b.extents.z);
            float s = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
            r = ext * s;
        }
        else
        {
            Planet planet = GetComponent<Planet>();
            if (planet != null && planet.shapeSettings != null)
            {
                float s = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
                r = planet.shapeSettings.planetRadius * s;
            }
        }

        return Mathf.Max(0.5f, r);
    }
}
