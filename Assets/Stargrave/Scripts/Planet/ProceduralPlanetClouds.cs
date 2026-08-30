using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// GPU-rendered procedural cloud shell for a spherical planet.
/// The component is intentionally independent of the planet generator: it only reads the
/// planet centre and surface radius, then renders one collider-free shell around it.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[DefaultExecutionOrder(60)]
public sealed class ProceduralPlanetClouds : MonoBehaviour
{
    public enum CloudQuality
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Ultra = 3
    }

    public enum ShadowQuality
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Ultra = 3
    }

    [Header("References")]
    [Tooltip("Optional. If empty, a Planet on this object or a parent is used automatically.")]
    public Planet planet;

    [Tooltip("Optional shader override. The built-in procedural cloud shader is found automatically.")]
    public Shader shaderOverride;

    [Header("Generation")]
    [Tooltip("Cloud seed. Equal seeds and settings produce the same formations.")]
    public int seed = 18473;

    [Tooltip("Derive the effective seed from the planet's shape asset/name/radius as well as Seed. The current planet generator has no serialized seed field.")]
    public bool deriveSeedFromPlanet = true;

    [Range(24, 96)]
    [Tooltip("Resolution of each generated tileable 3D noise texture. 48 is a good runtime default.")]
    public int noiseResolution = 48;

    [Tooltip("Use the spherical camera volume pass. Disable to use the proxy shell fallback.")]
    public bool useFullscreenVolume = true;

    [Range(0f, 1f)]
    [Tooltip("How much of the sky is cloudy. Low values leave large clear regions; 1 is overcast. This is the starting amount; the weather cycle can drift away from it while playing.")]
    public float coverage = 0.48f;

    [Header("Weather Cycle")]
    [Tooltip("While playing, coverage and wind drift through random states: clear, in-between, cloudy, with no fixed order.")]
    public bool animateWeather = true;

    [Range(0f, 1f)]
    [Tooltip("Clearest the sky is allowed to become during the weather cycle.")]
    public float weatherMinCoverage = 0.12f;

    [Range(0f, 1f)]
    [Tooltip("Cloudiest the sky is allowed to become during the weather cycle.")]
    public float weatherMaxCoverage = 0.78f;

    [Min(5f)]
    [Tooltip("Seconds to ease from one weather state to the next.")]
    public float weatherChangeDuration = 80f;

    [Min(0f)]
    [Tooltip("Seconds to hold a weather state before picking a new one.")]
    public float weatherHoldDuration = 35f;

    [Range(0f, 1f)]
    [Tooltip("Randomises change and hold times so weather does not feel metronomic.")]
    public float weatherTimingJitter = 0.4f;

    [Range(0.05f, 2f)]
    public float density = 1f;

    [Min(0.1f)]
    [Tooltip("World-space size of the largest cloud formations.")]
    public float cloudScale = 110f;

    [Min(1f)]
    [Tooltip("World-space size of cloudy versus clear weather regions. Larger values make bigger patches of open sky.")]
    public float weatherScale = 160f;

    [Min(0.1f)]
    [Tooltip("World-space size of smaller puffs and edge detail.")]
    public float detailScale = 24f;

    [Range(0f, 1f)]
    public float erosion = 0.38f;

    [Range(0f, 1f)]
    public float formationStrength = 0.72f;

    [Range(0f, 1f)]
    [Tooltip("Amount of medium-scale variation mixed into the large formations.")]
    public float mediumDetail = 0.62f;

    [Range(0f, 1f)]
    [Tooltip("Amount of small-scale edge breakup.")]
    public float smallDetail = 0.35f;

    [Range(0f, 1f)]
    [Tooltip("Amount of extra breakup inside cloud masses. Keep this moderate to avoid a regular peppering of similar puffs.")]
    public float cellularBreakup = 0.32f;

    [Range(0.5f, 4f)]
    [Tooltip("Frequency of the cellular cloud cells. Higher values create smaller scattered clouds.")]
    public float cellularScale = 1.65f;

    [Range(0f, 1f)]
    [Tooltip("Domain-warp strength used to bend and break the cloud shapes.")]
    public float warpStrength = 0.52f;

    [Header("Height")]
    [Min(0f)]
    [Tooltip("World-space gap between the highest terrain point and the cloud layer.")]
    public float cloudAltitude = 30f;

    [Min(0.5f)]
    public float cloudLayerThickness = 26f;

    [Range(0.1f, 4f)]
    [Tooltip("Shapes the vertical density profile through the cloud layer.")]
    public float verticalProfile = 1.35f;

    [Header("Wind")]
    [Tooltip("World-space direction of the prevailing wind. The vector is normalized automatically.")]
    public Vector3 windDirection = new Vector3(1f, 0f, 0.25f);

    [Min(0f)]
    public float cloudSpeed = 2.5f;

    [Range(0f, 1f)]
    public float windTurbulence = 0.22f;

    [Min(0f)]
    [Tooltip("Low cloud speed multiplier.")]
    public float lowLayerSpeed = 0.75f;

    [Min(0f)]
    [Tooltip("High cloud speed multiplier.")]
    public float highLayerSpeed = 1.35f;

    [Header("Lighting")]
    [ColorUsage(false, true)]
    public Color cloudColor = new Color(0.92f, 0.94f, 1f, 1f);

    [Min(0f)]
    public float sunIntensity = 1.1f;

    [Range(0f, 2f)]
    public float silverLining = 0.34f;

    [Range(0f, 0.5f)]
    [Tooltip("Very restrained night fill. This is illumination, not emission.")]
    public float nightIllumination = 0.02f;

    [Range(0f, 1f)]
    public float moonInfluence = 0.35f;

    [Range(0f, 1f)]
    [Tooltip("Extra density-dependent darkening inside clouds.")]
    public float interiorDarkness = 0.52f;

    [Header("Shadows")]
    public bool enableShadows = true;

    [Range(0f, 1f)]
    public float shadowStrength = 0.62f;

    [Range(0f, 1f)]
    [Tooltip("Dithered alpha coverage used by the shadow caster to soften thin clouds.")]
    public float shadowSoftness = 0.62f;

    public ShadowQuality shadowQuality = ShadowQuality.Medium;

    [Min(0f)]
    [Tooltip("Maximum distance from the planet centre at which the cloud shell casts shadows. Zero derives a radius-based value.")]
    public float shadowDistance = 0f;

    [Min(0.05f)]
    [Tooltip("Update interval for the low-resolution spherical shadow map. Higher values reduce CPU work.")]
    public float shadowMapUpdateFrequency = 0.1f;

    [Header("Quality")]
    public CloudQuality quality = CloudQuality.High;

    [Range(4, 96)]
    [Tooltip("Upper bound for the raymarch. The active quality and distance LOD may reduce it.")]
    public int maximumRaySteps = 40;

    [Min(0f)]
    [Tooltip("Seconds between GPU parameter updates while playing. Zero updates every frame.")]
    public float updateFrequency = 0.05f;

    [Min(0f)]
    [Tooltip("Distance from the planet centre where full quality is used. Zero derives a radius-based value.")]
    public float lodStartDistance = 0f;

    [Min(0f)]
    [Tooltip("Distance from the planet centre where the shell reaches its cheapest quality. Zero derives a radius-based value.")]
    public float lodEndDistance = 0f;

    [Min(0f)]
    [Tooltip("Distance from the planet centre after which clouds are culled. Zero derives a radius-based value.")]
    public float cullDistance = 0f;

    [Header("Debug")]
    public bool showCloudBounds;
    [Tooltip("Display procedural density instead of lit cloud colour.")]
    public bool showNoise;
    [Tooltip("Display the density used by the shadow representation, not the internal URP shadow atlas.")]
    public bool showShadowMap;

    [SerializeField, HideInInspector]
    int effectiveSeed;

    GameObject _cloudObject;
    Mesh _cloudMesh;
    MeshRenderer _cloudRenderer;
    Material _cloudMaterial;
    MaterialPropertyBlock _propertyBlock;
    Texture3D _baseNoiseTexture;
    Texture3D _detailNoiseTexture;
    Texture2D _shadowMapTexture;
    int _noiseTextureSeed;
    int _noiseTextureResolution;
    int _shadowMapWidth;
    int _shadowMapHeight;
    float _nextShadowMapUpdate;
    Planet _resolvedPlanet;
    Light _resolvedSun;
    bool _dirty = true;
    bool _warnedNoShader;
    bool _reportedRuntimeState;
    float _nextUpdateTime;
    float _nextLightResolveTime;
    float _innerRadius;
    float _outerRadius;
    float _lastParentScale = -1f;
    float _liveCoverage;
    float _weatherFrom;
    float _weatherTo;
    float _weatherBlend = 1f;
    float _weatherHoldRemaining;
    float _activeChangeDuration = 80f;
    bool _weatherCycleStarted;
    int _lastWeatherBand = -1;
    Vector3 _liveWind;
    Vector3 _windFrom;
    Vector3 _windTo;
    float _windBlend = 1f;
    float _windHoldRemaining;
    float _activeWindChangeDuration = 70f;
    float _liveWindSpeed;
    float _windSpeedFrom;
    float _windSpeedTo;

    public static ProceduralPlanetClouds ActiveInstance { get; private set; }
    internal Material FullscreenMaterial => _cloudMaterial;
    public float CurrentCoverage => EffectiveCoverage;

    float EffectiveCoverage =>
        Application.isPlaying && animateWeather ? _liveCoverage : coverage;

    Vector3 InspectorWind =>
        windDirection.sqrMagnitude > 1e-6f ? windDirection.normalized : Vector3.right;

    Vector3 EffectiveWind =>
        Application.isPlaying && animateWeather ? _liveWind : InspectorWind;

    float EffectiveCloudSpeed =>
        Application.isPlaying && animateWeather ? _liveWindSpeed : cloudSpeed;

    static readonly int PlanetCenterId = Shader.PropertyToID("_PlanetCenterWS");
    static readonly int InnerRadiusId = Shader.PropertyToID("_CloudInnerRadius");
    static readonly int OuterRadiusId = Shader.PropertyToID("_CloudOuterRadius");
    static readonly int SeedId = Shader.PropertyToID("_CloudSeed");
    static readonly int CoverageId = Shader.PropertyToID("_Coverage");
    static readonly int DensityId = Shader.PropertyToID("_Density");
    static readonly int CloudScaleId = Shader.PropertyToID("_CloudScale");
    static readonly int WeatherScaleId = Shader.PropertyToID("_WeatherScale");
    static readonly int DetailScaleId = Shader.PropertyToID("_DetailScale");
    static readonly int ErosionId = Shader.PropertyToID("_Erosion");
    static readonly int FormationStrengthId = Shader.PropertyToID("_FormationStrength");
    static readonly int MediumDetailId = Shader.PropertyToID("_MediumDetail");
    static readonly int SmallDetailId = Shader.PropertyToID("_SmallDetail");
    static readonly int CellularBreakupId = Shader.PropertyToID("_CellularBreakup");
    static readonly int CellularScaleId = Shader.PropertyToID("_CellularScale");
    static readonly int WarpStrengthId = Shader.PropertyToID("_WarpStrength");
    static readonly int VerticalProfileId = Shader.PropertyToID("_VerticalProfile");
    static readonly int WindDirectionId = Shader.PropertyToID("_WindDirection");
    static readonly int WindSpeedsId = Shader.PropertyToID("_LayerWindSpeeds");
    static readonly int TimeOffsetId = Shader.PropertyToID("_CloudTime");
    static readonly int CloudColorId = Shader.PropertyToID("_CloudColor");
    static readonly int BaseNoiseTextureId = Shader.PropertyToID("_CloudBaseNoise");
    static readonly int DetailNoiseTextureId = Shader.PropertyToID("_CloudDetailNoise");
    static readonly int ShadowMapTextureId = Shader.PropertyToID("_CloudShadowMap");
    static readonly int SunDirectionId = Shader.PropertyToID("_CloudSunDirection");
    static readonly int SunColorId = Shader.PropertyToID("_CloudSunColor");
    static readonly int SunIntensityId = Shader.PropertyToID("_SunIntensity");
    static readonly int SilverLiningId = Shader.PropertyToID("_SilverLining");
    static readonly int NightIlluminationId = Shader.PropertyToID("_NightIllumination");
    static readonly int MoonDirectionId = Shader.PropertyToID("_CloudMoonDirection");
    static readonly int MoonColorId = Shader.PropertyToID("_CloudMoonColor");
    static readonly int MoonAmountId = Shader.PropertyToID("_CloudMoonAmount");
    static readonly int InteriorDarknessId = Shader.PropertyToID("_InteriorDarkness");
    static readonly int ShadowStrengthId = Shader.PropertyToID("_ShadowStrength");
    static readonly int ShadowSoftnessId = Shader.PropertyToID("_ShadowSoftness");
    static readonly int ShadowQualityId = Shader.PropertyToID("_ShadowQuality");
    static readonly int SampleCountId = Shader.PropertyToID("_SampleCount");
    static readonly int LightSamplesId = Shader.PropertyToID("_LightSamples");
    static readonly int DistanceLodId = Shader.PropertyToID("_DistanceLod");
    static readonly int DebugModeId = Shader.PropertyToID("_DebugMode");

    const string CloudObjectName = "ProceduralCloudLayer";
    const int SphereLatitudeSegments = 32;
    const int SphereLongitudeSegments = 64;

    public void RegenerateClouds()
    {
        _dirty = true;
        EnsureCloudRenderer();
        ApplyCloudState(true);
    }

    public void RandomiseSeed()
    {
        seed = Random.Range(int.MinValue, int.MaxValue);
        deriveSeedFromPlanet = false;
        RegenerateClouds();
    }

    public void MarkDirty()
    {
        _dirty = true;
    }

    internal bool ShouldRenderForCamera(Camera camera)
    {
        if (camera == null)
            return false;
        float radialDistance = Vector3.Distance(camera.transform.position, transform.position);
        float lodStart = lodStartDistance > 0f ? lodStartDistance : Mathf.Max(_outerRadius * 1.1f, 250f);
        float lodEnd = lodEndDistance > lodStart ? lodEndDistance : Mathf.Max(lodStart + 1f, _outerRadius * 8f);
        float cull = cullDistance > 0f ? cullDistance : Mathf.Max(lodEnd * 1.5f, _outerRadius * 14f);
        return radialDistance <= cull;
    }

    void OnEnable()
    {
        ActiveInstance = this;
        _dirty = true;
        ResetWeatherCycle();
        EnsureCloudRenderer();
        ApplyCloudState(true);
    }

    void OnDisable()
    {
        if (ActiveInstance == this)
            ActiveInstance = null;
        DestroyCloudRenderer();
    }

    void OnDestroy()
    {
        if (ActiveInstance == this)
            ActiveInstance = null;
        DestroyCloudRenderer();
        DestroyNoiseTextures();
        if (_cloudMaterial != null)
        {
            DestroyObjectSafe(_cloudMaterial);
            _cloudMaterial = null;
        }
    }

    void Update()
    {
        if (!isActiveAndEnabled)
            return;

        EnsureCloudRenderer();
        if (_cloudRenderer == null)
            return;

        TickWeather();
        bool geometryChanged = ResolvePlanetAndRadii();
        EnsureNoiseTextures();
        if (_cloudMaterial != null)
        {
            _cloudMaterial.SetTexture(BaseNoiseTextureId, _baseNoiseTexture);
            _cloudMaterial.SetTexture(DetailNoiseTextureId, _detailNoiseTexture);
            _cloudMaterial.SetTexture(ShadowMapTextureId, _shadowMapTexture);
        }
        float now = Application.isPlaying ? Time.unscaledTime : -1f;
        bool due = _dirty || geometryChanged || !Application.isPlaying || updateFrequency <= 0f ||
                   now >= _nextUpdateTime;
        if (!due)
            return;

        ApplyCloudState(false);
        _nextUpdateTime = Application.isPlaying
            ? now + Mathf.Max(0.001f, updateFrequency)
            : float.PositiveInfinity;
    }

    void OnValidate()
    {
        maximumRaySteps = Mathf.Clamp(maximumRaySteps, 4, 96);
        cloudScale = Mathf.Max(0.1f, cloudScale);
        weatherScale = Mathf.Max(1f, weatherScale);
        detailScale = Mathf.Max(0.1f, detailScale);
        cloudLayerThickness = Mathf.Max(0.5f, cloudLayerThickness);
        weatherChangeDuration = Mathf.Max(5f, weatherChangeDuration);
        weatherHoldDuration = Mathf.Max(0f, weatherHoldDuration);
        if (weatherMinCoverage > weatherMaxCoverage)
        {
            float swap = weatherMinCoverage;
            weatherMinCoverage = weatherMaxCoverage;
            weatherMaxCoverage = swap;
        }
        shadowMapUpdateFrequency = Mathf.Max(0.05f, shadowMapUpdateFrequency);
        windDirection = windDirection.sqrMagnitude > 1e-6f ? windDirection.normalized : Vector3.right;
        _dirty = true;

#if UNITY_EDITOR
        if (!isActiveAndEnabled || EditorApplication.isPlayingOrWillChangePlaymode)
            return;
        EditorApplication.delayCall += DelayedEditorRefresh;
#endif
    }

#if UNITY_EDITOR
    void DelayedEditorRefresh()
    {
        if (this == null || !isActiveAndEnabled)
            return;
        EnsureCloudRenderer();
        ApplyCloudState(true);
    }
#endif

    void ResetWeatherCycle()
    {
        _liveCoverage = Mathf.Clamp01(coverage);
        _weatherFrom = _liveCoverage;
        _weatherTo = _liveCoverage;
        _weatherBlend = 1f;
        _weatherHoldRemaining = 0f;
        _activeChangeDuration = Mathf.Max(5f, weatherChangeDuration);
        _weatherCycleStarted = false;
        _lastWeatherBand = -1;
        _liveWind = InspectorWind;
        _windFrom = _liveWind;
        _windTo = _liveWind;
        _windBlend = 1f;
        _windHoldRemaining = 0f;
        _activeWindChangeDuration = Mathf.Max(8f, weatherChangeDuration);
        _liveWindSpeed = Mathf.Max(0f, cloudSpeed);
        _windSpeedFrom = _liveWindSpeed;
        _windSpeedTo = _liveWindSpeed;
    }

    void TickWeather()
    {
        if (!Application.isPlaying || !animateWeather)
        {
            _liveCoverage = Mathf.Clamp01(coverage);
            _liveWind = InspectorWind;
            _liveWindSpeed = Mathf.Max(0f, cloudSpeed);
            _weatherCycleStarted = false;
            return;
        }

        if (!_weatherCycleStarted)
        {
            _liveCoverage = Mathf.Clamp01(coverage);
            _weatherFrom = _liveCoverage;
            _weatherTo = PickWeatherTarget();
            _weatherBlend = 0f;
            _activeChangeDuration = JitteredDuration(weatherChangeDuration, 5f);
            _weatherHoldRemaining = 0f;
            _liveWind = InspectorWind;
            _windFrom = _liveWind;
            _windTo = PickWindTarget();
            _windBlend = 0f;
            _activeWindChangeDuration = JitteredDuration(weatherChangeDuration, 8f);
            _windHoldRemaining = 0f;
            _liveWindSpeed = Mathf.Max(0f, cloudSpeed);
            _windSpeedFrom = _liveWindSpeed;
            _windSpeedTo = PickWindSpeed();
            _weatherCycleStarted = true;
        }

        float dt = Time.deltaTime;
        TickCoverage(dt);
        TickWind(dt);
    }

    void TickCoverage(float dt)
    {
        if (_weatherHoldRemaining > 0f)
        {
            _weatherHoldRemaining -= dt;
            return;
        }

        float duration = Mathf.Max(5f, _activeChangeDuration);
        _weatherBlend = Mathf.MoveTowards(_weatherBlend, 1f, dt / duration);
        float t = _weatherBlend * _weatherBlend * (3f - 2f * _weatherBlend);
        _liveCoverage = Mathf.Lerp(_weatherFrom, _weatherTo, t);
        if (_weatherBlend < 1f)
            return;

        _liveCoverage = _weatherTo;
        _weatherFrom = _weatherTo;
        _weatherTo = PickWeatherTarget();
        _weatherBlend = 0f;
        _activeChangeDuration = Random.value < 0.2f
            ? JitteredDuration(Mathf.Max(18f, weatherChangeDuration * 0.4f), 12f)
            : JitteredDuration(weatherChangeDuration, 5f);
        _weatherHoldRemaining = JitteredDuration(weatherHoldDuration, 0f);
    }

    void TickWind(float dt)
    {
        if (_windHoldRemaining > 0f)
        {
            _windHoldRemaining -= dt;
            return;
        }

        float duration = Mathf.Max(8f, _activeWindChangeDuration);
        _windBlend = Mathf.MoveTowards(_windBlend, 1f, dt / duration);
        float t = _windBlend * _windBlend * (3f - 2f * _windBlend);
        _liveWind = Vector3.Slerp(_windFrom, _windTo, t).normalized;
        _liveWindSpeed = Mathf.Lerp(_windSpeedFrom, _windSpeedTo, t);
        if (_windBlend < 1f)
            return;

        _liveWind = _windTo;
        _windFrom = _windTo;
        _windSpeedFrom = _windSpeedTo;
        _liveWindSpeed = _windSpeedTo;
        _windBlend = 0f;
        _windHoldRemaining = JitteredDuration(weatherHoldDuration * Random.Range(0.6f, 1.8f), 0f);
        _activeWindChangeDuration = JitteredDuration(weatherChangeDuration, 8f);
        if (Random.value < 0.28f)
        {
            _windTo = _windFrom;
            _windSpeedTo = _windSpeedFrom;
            _windBlend = 1f;
            return;
        }

        _windTo = PickWindTarget();
        _windSpeedTo = PickWindSpeed();
    }

    float PickWeatherTarget()
    {
        float min = Mathf.Clamp01(Mathf.Min(weatherMinCoverage, weatherMaxCoverage));
        float max = Mathf.Clamp01(Mathf.Max(weatherMinCoverage, weatherMaxCoverage));
        if (max - min < 0.02f)
            return min;

        int band = PickWeatherBand();
        float span = max - min;
        float bandMin = min + span * (band * 0.25f);
        float bandMax = min + span * ((band + 1) * 0.25f);
        return Random.Range(bandMin, bandMax);
    }

    int PickWeatherBand()
    {
        float w0 = 1f;
        float w1 = 1.25f;
        float w2 = 1.25f;
        float w3 = 1f;
        if (_lastWeatherBand == 0) w0 *= 0.5f;
        else if (_lastWeatherBand == 1) w1 *= 0.5f;
        else if (_lastWeatherBand == 2) w2 *= 0.5f;
        else if (_lastWeatherBand == 3) w3 *= 0.5f;

        float sum = w0 + w1 + w2 + w3;
        float roll = Random.Range(0f, sum);
        int band;
        if (roll < w0) band = 0;
        else if (roll < w0 + w1) band = 1;
        else if (roll < w0 + w1 + w2) band = 2;
        else band = 3;
        _lastWeatherBand = band;
        return band;
    }

    Vector3 PickWindTarget()
    {
        Vector3 current = _liveWind.sqrMagnitude > 1e-6f ? _liveWind.normalized : InspectorWind;
        Vector3 next;
        if (Random.value < 0.5f)
        {
            float yaw = Random.Range(-55f, 55f);
            next = Quaternion.AngleAxis(yaw, Vector3.up) * current;
        }
        else
        {
            float yaw = Random.Range(0f, 360f);
            next = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        }

        next.y = Mathf.Clamp(next.y + Random.Range(-0.08f, 0.08f), -0.18f, 0.18f);
        if (next.sqrMagnitude < 1e-6f)
            next = Vector3.right;
        return next.normalized;
    }

    float PickWindSpeed()
    {
        float baseSpeed = Mathf.Max(0.15f, cloudSpeed);
        return Random.Range(baseSpeed * 0.55f, baseSpeed * 1.45f);
    }

    float JitteredDuration(float seconds, float minimum)
    {
        float jitter = Mathf.Clamp01(weatherTimingJitter);
        float scale = Random.Range(1f - jitter, 1f + jitter);
        return Mathf.Max(minimum, seconds * scale);
    }

    void EnsureCloudRenderer()
    {
        if (_cloudRenderer != null && _cloudMaterial != null && _cloudObject != null)
        {
            _cloudRenderer.enabled = !useFullscreenVolume || enableShadows;
            if (_cloudMaterial != null)
                _cloudMaterial.SetShaderPassEnabled("UniversalForward", !useFullscreenVolume);
            return;
        }

        if (_cloudObject == null)
        {
            Transform existing = transform.Find(CloudObjectName);
            _cloudObject = existing != null ? existing.gameObject : null;
        }

        if (_cloudObject == null)
        {
            _cloudObject = new GameObject(CloudObjectName);
            _cloudObject.transform.SetParent(transform, false);
        }

        _cloudObject.layer = gameObject.layer;
        _cloudObject.transform.localPosition = Vector3.zero;
        _cloudObject.transform.localRotation = Quaternion.identity;
        _cloudObject.hideFlags = HideFlags.DontSave;

        _cloudMesh = _cloudObject.GetComponent<MeshFilter>() != null
            ? _cloudObject.GetComponent<MeshFilter>().sharedMesh
            : null;
        if (_cloudMesh == null)
        {
            _cloudMesh = BuildSphereMesh();
            MeshFilter filter = _cloudObject.GetComponent<MeshFilter>();
            if (filter == null)
                filter = _cloudObject.AddComponent<MeshFilter>();
            filter.sharedMesh = _cloudMesh;
        }

        _cloudRenderer = _cloudObject.GetComponent<MeshRenderer>();
        if (_cloudRenderer == null)
            _cloudRenderer = _cloudObject.AddComponent<MeshRenderer>();

        _cloudRenderer.receiveShadows = false;
        _cloudRenderer.lightProbeUsage = LightProbeUsage.Off;
        _cloudRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        _cloudRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        _cloudRenderer.shadowCastingMode = ShadowCastingMode.Off;
        // The proxy is hidden when the fullscreen volume is active, but remains
        // available as a shadow-only fallback for the planet surface.
        _cloudRenderer.enabled = !useFullscreenVolume || enableShadows;

        if (_propertyBlock == null)
            _propertyBlock = new MaterialPropertyBlock();

        Shader shader = shaderOverride != null ? shaderOverride : Shader.Find("Stargrave/Procedural Planet Clouds");
        if (shader == null)
        {
            if (!_warnedNoShader)
            {
                Debug.LogWarning("ProceduralPlanetClouds: Could not find Stargrave/Procedural Planet Clouds.", this);
                _warnedNoShader = true;
            }
            return;
        }

        if (_cloudMaterial == null || _cloudMaterial.shader != shader)
        {
            if (_cloudMaterial != null)
                DestroyObjectSafe(_cloudMaterial);
            _cloudMaterial = new Material(shader)
            {
                name = "ProceduralPlanetClouds (Runtime)",
                // Draw immediately before the ocean. This lets the water blend over
                // clouds when the camera is underwater while preserving terrain depth.
                renderQueue = 2975,
                enableInstancing = true
            };
        }

        // Re-assert this on every refresh so a material surviving a domain reload
        // cannot move the cloud layer back behind/after the ocean.
        _cloudMaterial.renderQueue = 2975;
        _cloudRenderer.sharedMaterial = _cloudMaterial;
        ResolvePlanetAndRadii();
        EnsureNoiseTextures();
        _cloudMaterial.SetShaderPassEnabled("UniversalForward", !useFullscreenVolume);
        _cloudMaterial.SetTexture(BaseNoiseTextureId, _baseNoiseTexture);
        _cloudMaterial.SetTexture(DetailNoiseTextureId, _detailNoiseTexture);
        _cloudMaterial.SetTexture(ShadowMapTextureId, _shadowMapTexture);
    }

    bool ResolvePlanetAndRadii()
    {
        Planet previousPlanet = _resolvedPlanet;
        float previousInner = _innerRadius;
        float previousOuter = _outerRadius;

        if (planet == null)
            planet = GetComponent<Planet>() ?? GetComponentInParent<Planet>();
        if (planet == null)
        {
            Planet[] planets = FindObjectsByType<Planet>(FindObjectsInactive.Exclude);
            if (planets.Length == 1)
                planet = planets[0];
        }

        _resolvedPlanet = planet;
        float surfaceRadius = 100f;
        if (planet != null)
        {
            surfaceRadius = Mathf.Max(0.1f, planet.GetBaseRadiusWorld());
            if (planet.IsGenerated &&
                planet.TryGetLocalElevationMinMax(out _, out float localMaxElevation))
            {
                float planetScale = Mathf.Max(
                    planet.transform.lossyScale.x,
                    planet.transform.lossyScale.y,
                    planet.transform.lossyScale.z);
                surfaceRadius = Mathf.Max(0.1f, localMaxElevation * planetScale);
            }
        }
        else
        {
            float scale = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
            surfaceRadius = Mathf.Max(0.1f, 0.5f * scale);
        }

        _innerRadius = surfaceRadius + Mathf.Max(0f, cloudAltitude);
        _outerRadius = _innerRadius + Mathf.Max(0.5f, cloudLayerThickness);

        float parentScale = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
        if (parentScale < 1e-5f)
            parentScale = 1f;
        float localRadius = _outerRadius / parentScale;
        if (_cloudObject != null)
        {
            _cloudObject.transform.localScale = Vector3.one * localRadius;
            _cloudObject.transform.localPosition = Vector3.zero;
        }

        return previousPlanet != _resolvedPlanet ||
               Mathf.Abs(previousInner - _innerRadius) > 0.001f ||
               Mathf.Abs(previousOuter - _outerRadius) > 0.001f ||
               Mathf.Abs(parentScale - _lastParentScale) > 0.001f;
    }

    void ApplyCloudState(bool force)
    {
        if (_cloudRenderer == null)
            return;

        ResolvePlanetAndRadii();
        if (_cloudMaterial == null)
            return;
        _lastParentScale = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
        effectiveSeed = ResolveEffectiveSeed();

        Camera camera = Camera.main;
        if (camera == null)
            camera = FindAnyObjectByType<Camera>();

        float radialDistance = camera != null
            ? Vector3.Distance(camera.transform.position, transform.position)
            : _outerRadius;
        float lodStart = lodStartDistance > 0f ? lodStartDistance : Mathf.Max(_outerRadius * 1.1f, 250f);
        float lodEnd = lodEndDistance > lodStart ? lodEndDistance : Mathf.Max(lodStart + 1f, _outerRadius * 8f);
        float cull = cullDistance > 0f ? cullDistance : Mathf.Max(lodEnd * 1.5f, _outerRadius * 14f);
        float lod = Mathf.Clamp01(Mathf.InverseLerp(lodStart, lodEnd, radialDistance));
        _cloudRenderer.enabled = !useFullscreenVolume || enableShadows;
        _cloudMaterial.SetShaderPassEnabled("UniversalForward", !useFullscreenVolume);

        int[] qualitySteps = { 10, 18, 30, 48 };
        int baseSteps = Mathf.Min(maximumRaySteps, qualitySteps[(int)quality]);
        int sampleCount = Mathf.Max(4, Mathf.RoundToInt(Mathf.Lerp(baseSteps, 4f, lod)));
        int lightSamples = quality == CloudQuality.Low ? 1 : (quality == CloudQuality.Medium ? 2 : 3);
        int shadowLevel = (int)shadowQuality;
        float configuredShadowDistance = shadowDistance > 0f ? shadowDistance : _outerRadius * 2.6f;
        bool shadows = enableShadows && radialDistance <= configuredShadowDistance;
        _cloudRenderer.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;

        ResolveSunAndDayNight();
        Vector3 towardSun = ResolveSunDirection();
        Color sunColor = ResolveSunColor();
        Vector3 towardMoon = Shader.GetGlobalVector("_MoonDirection");
        if (towardMoon.sqrMagnitude < 1e-6f)
            towardMoon = -towardSun;
        towardMoon.Normalize();
        Color moonColor = Shader.GetGlobalColor("_MoonLightColor");
        if (moonColor.maxColorComponent < 1e-4f)
            moonColor = new Color(0.62f, 0.72f, 0.95f, 1f);
        float moonAmount = Mathf.Clamp01(Shader.GetGlobalFloat("_MoonLightStrength") * 4f);

        float time = Application.isPlaying ? Time.time : 0f;
        Vector3 wind = EffectiveWind;
        float dayAmount = Application.isPlaying ? Mathf.Clamp01(PlanetDayNightCycle.SkyDayAmount) : 1f;
        float nightAmount = Application.isPlaying ? Mathf.Clamp01(PlanetDayNightCycle.NightAmount) : 0f;
        float twilight = Application.isPlaying ? Mathf.Clamp01(PlanetDayNightCycle.TwilightAmount) : 0f;
        int debugMode = showShadowMap ? 2 : (showNoise ? 1 : 0);
        EnsureShadowMapTexture();
        if (_shadowMapTexture == null || _dirty ||
            (Application.isPlaying && Time.unscaledTime >= _nextShadowMapUpdate))
        {
            UpdateShadowMap(time);
            _nextShadowMapUpdate = Application.isPlaying
                ? Time.unscaledTime + Mathf.Max(0.05f, shadowMapUpdateFrequency)
                : float.PositiveInfinity;
        }

        _propertyBlock.Clear();
        _propertyBlock.SetVector(PlanetCenterId, transform.position);
        _propertyBlock.SetFloat(InnerRadiusId, _innerRadius);
        _propertyBlock.SetFloat(OuterRadiusId, _outerRadius);
        _propertyBlock.SetFloat(SeedId, effectiveSeed);
        _propertyBlock.SetFloat(CoverageId, EffectiveCoverage);
        _propertyBlock.SetFloat(DensityId, density);
        _propertyBlock.SetFloat(CloudScaleId, cloudScale);
        _propertyBlock.SetFloat(WeatherScaleId, weatherScale);
        _propertyBlock.SetFloat(DetailScaleId, detailScale);
        _propertyBlock.SetFloat(ErosionId, erosion);
        _propertyBlock.SetFloat(FormationStrengthId, formationStrength);
        _propertyBlock.SetFloat(MediumDetailId, mediumDetail);
        _propertyBlock.SetFloat(SmallDetailId, smallDetail);
        _propertyBlock.SetFloat(CellularBreakupId, cellularBreakup);
        _propertyBlock.SetFloat(CellularScaleId, cellularScale);
        _propertyBlock.SetFloat(WarpStrengthId, warpStrength);
        _propertyBlock.SetFloat(VerticalProfileId, verticalProfile);
        _propertyBlock.SetVector(WindDirectionId, new Vector4(wind.x, wind.y, wind.z, 0f));
        _propertyBlock.SetVector(WindSpeedsId, new Vector4(
            EffectiveCloudSpeed * lowLayerSpeed,
            EffectiveCloudSpeed,
            EffectiveCloudSpeed * highLayerSpeed,
            windTurbulence));
        _propertyBlock.SetFloat(TimeOffsetId, time);
        _propertyBlock.SetColor(CloudColorId, cloudColor);
        _propertyBlock.SetTexture(BaseNoiseTextureId, _baseNoiseTexture);
        _propertyBlock.SetTexture(DetailNoiseTextureId, _detailNoiseTexture);
        _propertyBlock.SetTexture(ShadowMapTextureId, _shadowMapTexture);
        _propertyBlock.SetVector(SunDirectionId, new Vector4(towardSun.x, towardSun.y, towardSun.z, 0f));
        _propertyBlock.SetColor(SunColorId, sunColor);
        _propertyBlock.SetFloat(SunIntensityId, sunIntensity * Mathf.Lerp(0.12f, 1f, dayAmount));
        _propertyBlock.SetFloat(SilverLiningId, silverLining * Mathf.Lerp(0.4f, 1f, dayAmount));
        _propertyBlock.SetFloat(NightIlluminationId, nightIllumination * nightAmount);
        _propertyBlock.SetVector(MoonDirectionId, new Vector4(towardMoon.x, towardMoon.y, towardMoon.z, 0f));
        _propertyBlock.SetColor(MoonColorId, moonColor);
        _propertyBlock.SetFloat(MoonAmountId, moonAmount * moonInfluence);
        _propertyBlock.SetFloat(InteriorDarknessId, interiorDarkness);
        _propertyBlock.SetFloat(ShadowStrengthId, shadows ? shadowStrength : 0f);
        _propertyBlock.SetFloat(ShadowSoftnessId, shadowSoftness);
        _propertyBlock.SetFloat(ShadowQualityId, shadowLevel);
        _propertyBlock.SetInt(SampleCountId, sampleCount);
        _propertyBlock.SetInt(LightSamplesId, lightSamples);
        _propertyBlock.SetFloat(DistanceLodId, lod);
        _propertyBlock.SetFloat(DebugModeId, debugMode);
        _propertyBlock.SetFloat("_Twilight", twilight);
        _propertyBlock.SetFloat("_DayAmount", dayAmount);
        _propertyBlock.SetFloat("_NightAmount", nightAmount);
        // Keep the runtime material itself in sync as well as the property block. This is
        // important for URP configurations that batch transparent proxy passes without
        // applying every non-texture property from a MaterialPropertyBlock.
        _cloudMaterial.SetVector(PlanetCenterId, transform.position);
        _cloudMaterial.SetFloat(InnerRadiusId, _innerRadius);
        _cloudMaterial.SetFloat(OuterRadiusId, _outerRadius);
        _cloudMaterial.SetFloat(SeedId, effectiveSeed);
        _cloudMaterial.SetFloat(CoverageId, EffectiveCoverage);
        _cloudMaterial.SetFloat(DensityId, density);
        _cloudMaterial.SetFloat(CloudScaleId, cloudScale);
        _cloudMaterial.SetFloat(WeatherScaleId, weatherScale);
        _cloudMaterial.SetFloat(DetailScaleId, detailScale);
        _cloudMaterial.SetFloat(ErosionId, erosion);
        _cloudMaterial.SetFloat(FormationStrengthId, formationStrength);
        _cloudMaterial.SetFloat(MediumDetailId, mediumDetail);
        _cloudMaterial.SetFloat(SmallDetailId, smallDetail);
        _cloudMaterial.SetFloat(CellularBreakupId, cellularBreakup);
        _cloudMaterial.SetFloat(CellularScaleId, cellularScale);
        _cloudMaterial.SetFloat(WarpStrengthId, warpStrength);
        _cloudRenderer.SetPropertyBlock(_propertyBlock);
        if (Application.isPlaying && !_reportedRuntimeState)
        {
            _reportedRuntimeState = true;
            Debug.Log(
                $"ProceduralPlanetClouds runtime: renderer={_cloudRenderer.enabled}, " +
                $"shader={_cloudMaterial.shader.name}, radii={_innerRadius:F1}-{_outerRadius:F1}, " +
                $"coverage={EffectiveCoverage:F2}, density={density:F2}, scale={cloudScale:F1}, " +
                $"cameraDistance={radialDistance:F1}, cullDistance={cull:F1}.",
                this);
        }

        _dirty = false;
    }

    void ResolveSunAndDayNight()
    {
        if (Time.realtimeSinceStartup < _nextLightResolveTime && _resolvedSun != null)
            return;

        _nextLightResolveTime = Time.realtimeSinceStartup + 0.5f;
        if (PlanetDayNightCycle.IsEligibleSunLight(PlanetDayNightCycle.ActiveSunLight))
        {
            _resolvedSun = PlanetDayNightCycle.ActiveSunLight;
            return;
        }
        if (PlanetDayNightCycle.IsEligibleSunLight(RenderSettings.sun))
        {
            _resolvedSun = RenderSettings.sun;
            return;
        }

        Light best = null;
        Light[] lights = FindObjectsByType<Light>(FindObjectsInactive.Exclude);
        for (int i = 0; i < lights.Length; i++)
        {
            if (!PlanetDayNightCycle.IsEligibleSunLight(lights[i]))
                continue;
            if (best == null || lights[i].intensity > best.intensity)
                best = lights[i];
        }
        _resolvedSun = best;
    }

    Vector3 ResolveSunDirection()
    {
        if (PlanetDayNightCycle.ActiveSunLight != null &&
            PlanetDayNightCycle.TowardSunWS.sqrMagnitude > 1e-6f)
            return PlanetDayNightCycle.TowardSunWS.normalized;
        if (_resolvedSun != null)
            return (-_resolvedSun.transform.forward).normalized;
        return Vector3.up;
    }

    Color ResolveSunColor()
    {
        if (PlanetDayNightCycle.ActiveSunLight != null)
            return PlanetDayNightCycle.ActiveSunColor;
        return _resolvedSun != null ? _resolvedSun.color : Color.white;
    }

    int ResolveEffectiveSeed()
    {
        unchecked
        {
            int result = seed;
            if (!deriveSeedFromPlanet || planet == null)
                return result;

            result ^= StableHash(planet.name);
            if (planet.shapeSettings != null)
            {
                result ^= StableHash(planet.shapeSettings.name);
                result ^= Mathf.RoundToInt(planet.shapeSettings.planetRadius * 100f);
            }
            return result;
        }
    }

    static int StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261u;
            if (!string.IsNullOrEmpty(value))
            {
                for (int i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= 16777619u;
                }
            }
            return (int)hash;
        }
    }

    Mesh BuildSphereMesh()
    {
        int vertexCount = (SphereLatitudeSegments + 1) * (SphereLongitudeSegments + 1);
        Vector3[] vertices = new Vector3[vertexCount];
        Vector3[] normals = new Vector3[vertexCount];
        int[] triangles = new int[SphereLatitudeSegments * SphereLongitudeSegments * 6];

        int v = 0;
        for (int y = 0; y <= SphereLatitudeSegments; y++)
        {
            float vertical = y / (float)SphereLatitudeSegments;
            float phi = vertical * Mathf.PI;
            float sinPhi = Mathf.Sin(phi);
            float cosPhi = Mathf.Cos(phi);
            for (int x = 0; x <= SphereLongitudeSegments; x++)
            {
                float theta = x / (float)SphereLongitudeSegments * Mathf.PI * 2f;
                Vector3 p = new Vector3(
                    sinPhi * Mathf.Cos(theta),
                    cosPhi,
                    sinPhi * Mathf.Sin(theta));
                vertices[v] = p;
                normals[v] = p;
                v++;
            }
        }

        int t = 0;
        for (int y = 0; y < SphereLatitudeSegments; y++)
        {
            for (int x = 0; x < SphereLongitudeSegments; x++)
            {
                int a = y * (SphereLongitudeSegments + 1) + x;
                int b = a + SphereLongitudeSegments + 1;
                triangles[t++] = a;
                triangles[t++] = a + 1;
                triangles[t++] = b;
                triangles[t++] = a + 1;
                triangles[t++] = b + 1;
                triangles[t++] = b;
            }
        }

        Mesh mesh = new Mesh
        {
            name = "ProceduralPlanetClouds Sphere"
        };
        mesh.indexFormat = vertexCount > 65535
            ? IndexFormat.UInt32
            : IndexFormat.UInt16;
        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
        mesh.UploadMeshData(false);
        return mesh;
    }

    void EnsureNoiseTextures()
    {
        int resolution = Mathf.Clamp(noiseResolution, 24, 96);
        int resolvedSeed = ResolveEffectiveSeed();
        if (_baseNoiseTexture != null && _detailNoiseTexture != null &&
            _noiseTextureSeed == resolvedSeed && _noiseTextureResolution == resolution)
        {
            EnsureShadowMapTexture();
            return;
        }

        DestroyNoiseTextures();
        int voxelCount = resolution * resolution * resolution;
        Color32[] basePixels = new Color32[voxelCount];
        Color32[] detailPixels = new Color32[voxelCount];
        int index = 0;

        for (int z = 0; z < resolution; z++)
        {
            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++, index++)
                {
                    Vector3 uv = new Vector3(
                        x / (float)resolution,
                        y / (float)resolution,
                        z / (float)resolution);

                    float baseFbm = FractalValueNoise(uv, resolvedSeed, 4, 4);
                    float baseCells = WorleyNoise(uv * 3f, 3, resolvedSeed + 101);
                    float warpFbm = FractalValueNoise(uv, resolvedSeed + 509, 3, 2);
                    float warpCells = WorleyNoise(uv * 2f, 2, resolvedSeed + 601);
                    float detailFbm = FractalValueNoise(uv, resolvedSeed + 211, 4, 8);
                    float detailCells = WorleyNoise(uv * 12f, 12, resolvedSeed + 307);

                    basePixels[index] = new Color32(
                        ToByte(baseFbm),
                        ToByte(baseCells),
                        ToByte(warpFbm),
                        ToByte(warpCells));
                    detailPixels[index] = new Color32(
                        ToByte(detailFbm),
                        ToByte(detailCells),
                        0,
                        255);
                }
            }
        }

        _baseNoiseTexture = CreateNoiseTexture("ProceduralClouds Base 3D Noise", resolution, basePixels);
        _detailNoiseTexture = CreateNoiseTexture("ProceduralClouds Detail 3D Noise", resolution, detailPixels);
        effectiveSeed = resolvedSeed;
        _noiseTextureSeed = resolvedSeed;
        _noiseTextureResolution = resolution;
        EnsureShadowMapTexture();
        UpdateShadowMap(0f);
    }

    static Texture3D CreateNoiseTexture(string textureName, int resolution, Color32[] pixels)
    {
        Texture3D texture = new Texture3D(resolution, resolution, resolution,
            TextureFormat.RGBA32, false)
        {
            name = textureName,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            hideFlags = HideFlags.HideAndDontSave
        };
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return texture;
    }

    void EnsureShadowMapTexture()
    {
        GetShadowMapDimensions(out int width, out int height);
        if (_shadowMapTexture != null && _shadowMapWidth == width && _shadowMapHeight == height)
            return;

        if (_shadowMapTexture != null)
            DestroyObjectSafe(_shadowMapTexture);

        _shadowMapTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
        {
            name = "ProceduralClouds Spherical Shadow Map",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave
        };
        _shadowMapWidth = width;
        _shadowMapHeight = height;
        _nextShadowMapUpdate = 0f;
    }

    void GetShadowMapDimensions(out int width, out int height)
    {
        switch (shadowQuality)
        {
            case ShadowQuality.Low:
                width = 32;
                height = 16;
                break;
            case ShadowQuality.High:
                width = 128;
                height = 64;
                break;
            case ShadowQuality.Ultra:
                width = 256;
                height = 128;
                break;
            default:
                width = 64;
                height = 32;
                break;
        }
    }

    void UpdateShadowMap(float time)
    {
        if (_shadowMapTexture == null || _shadowMapWidth <= 0 || _shadowMapHeight <= 0)
            return;

        Color32[] pixels = new Color32[_shadowMapWidth * _shadowMapHeight];
        int index = 0;
        for (int y = 0; y < _shadowMapHeight; y++)
        {
            float latitude = y / (float)Mathf.Max(1, _shadowMapHeight - 1);
            float phi = latitude * Mathf.PI;
            float sinPhi = Mathf.Sin(phi);
            float cosPhi = Mathf.Cos(phi);
            for (int x = 0; x < _shadowMapWidth; x++, index++)
            {
                float longitude = x / (float)_shadowMapWidth * Mathf.PI * 2f;
                Vector3 radial = new Vector3(
                    sinPhi * Mathf.Cos(longitude),
                    cosPhi,
                    sinPhi * Mathf.Sin(longitude));
                pixels[index] = new Color32(
                    ToByte(EvaluateCloudShape(radial, time) * shadowStrength),
                    0,
                    0,
                    255);
            }
        }
        _shadowMapTexture.SetPixels32(pixels);
        _shadowMapTexture.Apply(false, false);
    }

    float EvaluateCloudShape(Vector3 radial, float time)
    {
        float scale = Mathf.Max(cloudScale, 0.1f);
        float middleRadius = (_innerRadius + _outerRadius) * 0.5f;
        Vector3 toPoint = radial * middleRadius;
        Vector3 wind = EffectiveWind * (time * EffectiveCloudSpeed / scale);
        Vector3 p = CheapWarp(RotateNoisePoint(toPoint / scale + wind));

        float weatherFreq = Mathf.Max(middleRadius, 1f) / Mathf.Max(weatherScale, 1f);
        float weather = Fbm2Unbounded(RotateNoisePoint(radial * weatherFreq));
        weather = Mathf.Clamp01((weather - 0.22f) * 2.2f);

        float c = Mathf.Clamp01(EffectiveCoverage);
        float weatherMask = Mathf.Clamp01(weather - (1f - c) * 0.62f);
        weatherMask = SmoothStep(0f, 0.22f, weatherMask);
        weatherMask *= SmoothStep(0f, 0.02f, c);

        float large = Fbm2Unbounded(p);
        float small = ValueNoiseUnbounded(p * 1.7f + Vector3.one * 13.7f);
        float mass = SmoothStep(0.28f, 0.55f, large);
        float smallPuffs = SmoothStep(0.46f, 0.68f, small) * (1f - Mathf.Clamp01(mass * 1.15f));
        float shape = Mathf.Max(mass, smallPuffs * 0.7f);

        float breakup = ValueNoiseUnbounded(p * cellularScale * 0.55f + Vector3.one * 4.1f);
        shape *= Mathf.Lerp(1f, Mathf.Lerp(0.5f, 1f, breakup), Mathf.Clamp01(cellularBreakup) * 0.65f);
        shape *= weatherMask;

        Vector3 detailUv = RepeatVector(toPoint / Mathf.Max(detailScale, 0.1f) +
            EffectiveWind * (time * EffectiveCloudSpeed * highLayerSpeed / Mathf.Max(detailScale, 0.1f)));
        float detailCells = WorleyNoise(detailUv * 12f, 12, effectiveSeed + 307);
        return Mathf.Clamp01(shape - (detailCells - 0.48f) * erosion * smallDetail * 0.4f);
    }

    static Vector3 RotateNoisePoint(Vector3 p)
    {
        return new Vector3(
            Vector3.Dot(p, new Vector3(0f, 0.80f, 0.60f)),
            Vector3.Dot(p, new Vector3(-0.80f, 0.36f, -0.48f)),
            Vector3.Dot(p, new Vector3(-0.60f, -0.48f, 0.64f)));
    }

    Vector3 CheapWarp(Vector3 p)
    {
        float n = ValueNoiseUnbounded(p * 0.71f + new Vector3(4.7f, 1.3f, 8.1f));
        return p + Vector3.one * ((n - 0.5f) * (2.6f * warpStrength));
    }

    float Fbm2Unbounded(Vector3 p)
    {
        float a = ValueNoiseUnbounded(p);
        float b = ValueNoiseUnbounded(p * 2.03f + new Vector3(17.1f, 7.3f, 29.7f));
        return a * 0.67f + b * 0.33f;
    }

    float ValueNoiseUnbounded(Vector3 p)
    {
        Vector3 cell = new Vector3(Mathf.Floor(p.x), Mathf.Floor(p.y), Mathf.Floor(p.z));
        Vector3 f = new Vector3(p.x - cell.x, p.y - cell.y, p.z - cell.z);
        f = new Vector3(
            f.x * f.x * (3f - 2f * f.x),
            f.y * f.y * (3f - 2f * f.y),
            f.z * f.z * (3f - 2f * f.z));

        float n000 = Hash31Sin(cell);
        float n100 = Hash31Sin(cell + new Vector3(1f, 0f, 0f));
        float n010 = Hash31Sin(cell + new Vector3(0f, 1f, 0f));
        float n110 = Hash31Sin(cell + new Vector3(1f, 1f, 0f));
        float n001 = Hash31Sin(cell + new Vector3(0f, 0f, 1f));
        float n101 = Hash31Sin(cell + new Vector3(1f, 0f, 1f));
        float n011 = Hash31Sin(cell + new Vector3(0f, 1f, 1f));
        float n111 = Hash31Sin(cell + new Vector3(1f, 1f, 1f));
        float x00 = Mathf.Lerp(n000, n100, f.x);
        float x10 = Mathf.Lerp(n010, n110, f.x);
        float x01 = Mathf.Lerp(n001, n101, f.x);
        float x11 = Mathf.Lerp(n011, n111, f.x);
        return Mathf.Lerp(Mathf.Lerp(x00, x10, f.y), Mathf.Lerp(x01, x11, f.y), f.z);
    }

    float Hash31Sin(Vector3 p)
    {
        p += effectiveSeed * new Vector3(0.071f, 0.113f, 0.173f);
        float n = Mathf.Sin(Vector3.Dot(p, new Vector3(127.1f, 311.7f, 74.7f))) * 43758.5453f;
        return n - Mathf.Floor(n);
    }

    static Vector3 RepeatVector(Vector3 value)
    {
        return new Vector3(
            Mathf.Repeat(value.x, 1f),
            Mathf.Repeat(value.y, 1f),
            Mathf.Repeat(value.z, 1f));
    }

    static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Mathf.Clamp01((value - edge0) / Mathf.Max(edge1 - edge0, 0.0001f));
        return t * t * (3f - 2f * t);
    }

    void DestroyNoiseTextures()
    {
        if (_baseNoiseTexture != null)
        {
            DestroyObjectSafe(_baseNoiseTexture);
            _baseNoiseTexture = null;
        }
        if (_detailNoiseTexture != null)
        {
            DestroyObjectSafe(_detailNoiseTexture);
            _detailNoiseTexture = null;
        }
        if (_shadowMapTexture != null)
        {
            DestroyObjectSafe(_shadowMapTexture);
            _shadowMapTexture = null;
        }
        _noiseTextureSeed = 0;
        _noiseTextureResolution = 0;
        _shadowMapWidth = 0;
        _shadowMapHeight = 0;
    }

    static byte ToByte(float value)
    {
        return (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(value) * 255f), 0, 255);
    }

    static float FractalValueNoise(Vector3 uv, int noiseSeed, int layers, int startingFrequency)
    {
        float value = 0f;
        float amplitude = 0.5f;
        float normalizer = 0f;
        int frequency = Mathf.Max(1, startingFrequency);
        for (int i = 0; i < layers; i++)
        {
            value += PeriodicValueNoise(uv * frequency, frequency, noiseSeed + i * 977) * amplitude;
            normalizer += amplitude;
            frequency *= 2;
            amplitude *= 0.5f;
        }
        return value / Mathf.Max(normalizer, 0.001f);
    }

    static float PeriodicValueNoise(Vector3 point, int period, int noiseSeed)
    {
        int x0 = Mathf.FloorToInt(point.x);
        int y0 = Mathf.FloorToInt(point.y);
        int z0 = Mathf.FloorToInt(point.z);
        int x1 = x0 + 1;
        int y1 = y0 + 1;
        int z1 = z0 + 1;
        Vector3 f = new Vector3(
            point.x - x0,
            point.y - y0,
            point.z - z0);
        f = new Vector3(
            f.x * f.x * (3f - 2f * f.x),
            f.y * f.y * (3f - 2f * f.y),
            f.z * f.z * (3f - 2f * f.z));

        float n000 = HashTo01(x0, y0, z0, period, noiseSeed);
        float n100 = HashTo01(x1, y0, z0, period, noiseSeed);
        float n010 = HashTo01(x0, y1, z0, period, noiseSeed);
        float n110 = HashTo01(x1, y1, z0, period, noiseSeed);
        float n001 = HashTo01(x0, y0, z1, period, noiseSeed);
        float n101 = HashTo01(x1, y0, z1, period, noiseSeed);
        float n011 = HashTo01(x0, y1, z1, period, noiseSeed);
        float n111 = HashTo01(x1, y1, z1, period, noiseSeed);
        float x00 = Mathf.Lerp(n000, n100, f.x);
        float x10 = Mathf.Lerp(n010, n110, f.x);
        float x01 = Mathf.Lerp(n001, n101, f.x);
        float x11 = Mathf.Lerp(n011, n111, f.x);
        return Mathf.Lerp(Mathf.Lerp(x00, x10, f.y), Mathf.Lerp(x01, x11, f.y), f.z);
    }

    static float WorleyNoise(Vector3 point, int period, int noiseSeed)
    {
        int cellX = Mathf.FloorToInt(point.x);
        int cellY = Mathf.FloorToInt(point.y);
        int cellZ = Mathf.FloorToInt(point.z);
        Vector3 fraction = new Vector3(
            point.x - cellX,
            point.y - cellY,
            point.z - cellZ);
        float nearest = 10f;

        for (int z = -1; z <= 1; z++)
        {
            for (int y = -1; y <= 1; y++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    int sampleX = cellX + x;
                    int sampleY = cellY + y;
                    int sampleZ = cellZ + z;
                    Vector3 feature = new Vector3(
                        x + HashTo01(sampleX, sampleY, sampleZ, period, noiseSeed),
                        y + HashTo01(sampleX, sampleY, sampleZ, period, noiseSeed + 17),
                        z + HashTo01(sampleX, sampleY, sampleZ, period, noiseSeed + 31));
                    nearest = Mathf.Min(nearest, (feature - fraction).magnitude);
                }
            }
        }
        return Mathf.Clamp01(1f - nearest / 1.25f);
    }

    static float HashTo01(int x, int y, int z, int period, int noiseSeed)
    {
        x = Mod(x, period);
        y = Mod(y, period);
        z = Mod(z, period);
        unchecked
        {
            uint h = (uint)noiseSeed;
            h ^= (uint)(x * 374761393);
            h = (h << 13) | (h >> 19);
            h ^= (uint)(y * 668265263);
            h = h * 1274126177u + (uint)z * 2246822519u;
            h ^= h >> 16;
            return (h & 0x00ffffffu) / 16777215f;
        }
    }

    static int Mod(int value, int period)
    {
        int result = value % period;
        return result < 0 ? result + period : result;
    }

    void DestroyCloudRenderer()
    {
        if (_cloudObject != null)
        {
            DestroyObjectSafe(_cloudObject);
            _cloudObject = null;
        }
        _cloudRenderer = null;
        _cloudMesh = null;
    }

    static void DestroyObjectSafe(Object obj)
    {
        if (obj == null)
            return;
        if (Application.isPlaying)
            Destroy(obj);
        else
            DestroyImmediate(obj);
    }

    void OnDrawGizmosSelected()
    {
        if (!showCloudBounds)
            return;
        if (_innerRadius <= 0f || _outerRadius <= 0f)
            ResolvePlanetAndRadii();
        Gizmos.color = new Color(0.55f, 0.75f, 1f, 0.7f);
        Gizmos.DrawWireSphere(transform.position, _innerRadius);
        Gizmos.color = new Color(0.85f, 0.95f, 1f, 0.9f);
        Gizmos.DrawWireSphere(transform.position, _outerRadius);
    }
}
