using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Day/night from an Earth-like sun (planet poles + axial tilt) and opposite moonlight.
/// Sky / ambient / atmosphere follow sun elevation (civil twilight after set).
/// Direct sun/moon intensity also need clear LoS so terrain can cast shade without
/// turning the whole sky to night.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[DefaultExecutionOrder(-100)]
public class PlanetDayNightCycle : MonoBehaviour
{
    const string MoonLightName = "MoonLight";

    [Header("Cycle timing")]
    [Tooltip("Seconds for a full 360° sun orbit (one day).")]
    [Min(0.01f)]
    public float dayLengthSeconds = 180f;

    [Tooltip("1 = normal, 2 = twice as fast, 0 = paused. Negative reverses.")]
    public float speedMultiplier = 1f;

    [Header("Orbit (Earth-like)")]
    [Tooltip("World-space planet north. Zero = planet.transform.up, else Vector3.up.")]
    public Vector3 planetNorthOverride = Vector3.zero;

    [Tooltip("Axial tilt in degrees (Earth ≈ 23.44).")]
    [Range(0f, 45f)]
    public float axialTiltDegrees = 23.44f;

    [Tooltip("0 = equinox, 90 = northern summer solstice. Declination = tilt × sin(phase).")]
    [Range(0f, 360f)]
    public float seasonPhaseDegrees = 35f;

    [Header("Sun (direct light)")]
    [Tooltip("Directional to orbit. If null: RenderSettings.sun, else brightest eligible Directional.")]
    public Light sunLight;

    [Tooltip("Clear-sky sun intensity at mid-day.")]
    [Min(0f)] public float daySunIntensity = 2.4f;

    [ColorUsage(false, false)]
    public Color daySunColor = new Color(1f, 0.98f, 0.94f, 1f);

    [Tooltip("Sun disc tint at sunrise (rose / peach).")]
    [ColorUsage(false, false)]
    public Color dawnSunColor = new Color(1f, 0.62f, 0.52f, 1f);

    [Tooltip("Sun disc tint at sunset (amber / orange).")]
    [ColorUsage(false, false)]
    public Color duskSunColor = new Color(1f, 0.52f, 0.26f, 1f);

    [Range(0f, 1f)] public float sunShadowStrength = 0.9f;

    [Tooltip("Local elev (dot up·sun) where direct sunlight is full. Keep low so the disc and brightness match.")]
    [Range(0.01f, 0.5f)] public float sunDirectFullElev = 0.04f;

    [Tooltip("Local elev where direct sunlight reaches zero.")]
    [Range(-0.2f, 0.05f)] public float sunDirectZeroElev = -0.06f;

    [Tooltip("Advance brightness clocks vs geometric elev so light rises with the visible disc.")]
    [Range(0f, 0.12f)] public float lightingPhaseBias = 0.05f;

    [Tooltip("Extra brightness hold while the sun is setting (delays nightfall).")]
    [Range(0f, 0.25f)] public float eveningBrightnessHold = 0.14f;

    [Tooltip("Extra brightness hold while the sun is rising (mirror of evening; same band).")]
    [Range(0f, 0.25f)] public float morningBrightnessHold = 0.14f;

    [Tooltip("While setting, keep full daylight until the sun reaches this elev (0 = geometric horizon).")]
    [Range(-0.05f, 0.1f)] public float eveningDayUntilElev = 0.02f;

    [Tooltip("While setting, delay twilight/night until the sun drops below this elev (~0.06 ≈ 10s on a 180s day).")]
    [Range(0f, 0.15f)] public float eveningSunsetHoldElev = 0.06f;

    [Tooltip("While setting, sky/direct finish fading by this elev (after geometric set).")]
    [Range(-0.45f, -0.05f)] public float eveningNightByElev = -0.18f;

    [Header("Sky (ambient / atmosphere)")]
    [Tooltip("Local elev where sky daylight is full.")]
    [Range(0f, 0.4f)] public float skyDayFullElev = 0.05f;

    [Tooltip("Local elev where sky daylight dies (~nautical twilight).")]
    [Range(-0.5f, -0.05f)] public float skyDayZeroElev = -0.34f;

    [Tooltip("Evening golden-hour: elev where warmth fades toward noon / afternoon start.")]
    [Range(0.15f, 0.55f)] public float goldenHourHighElev = 0.4f;

    [Tooltip("Morning only: elev where dawn glow has faded to normal day ambient.")]
    [Range(0.1f, 0.4f)] public float morningGoldFadeElev = 0.2f;

    [Tooltip("Warm band: elev where warmth ends after sunset / before sunrise.")]
    [Range(-0.25f, 0.05f)] public float goldenHourLowElev = -0.12f;

    [Tooltip("Extra color lead while the sun is rising (earlier morning flush).")]
    [Range(0f, 0.12f)] public float morningColorLead = 0.04f;

    [Tooltip("Extra color hold while the sun is setting (later evening flush).")]
    [Range(0f, 0.15f)] public float eveningColorHold = 0.07f;

    [Tooltip("How much richer the dawn ambient glow is vs evening golden hour.")]
    [Range(0f, 1f)] public float morningGlowBoost = 0.15f;

    [Header("Moon")]
    public bool enableMoonlight = true;

    [Min(0f)] public float moonIntensity = 0.12f;

    [ColorUsage(false, false)]
    public Color moonColor = new Color(0.62f, 0.72f, 0.95f, 1f);

    [Range(0f, 1f)] public float moonShadowStrength = 0.65f;

    [Tooltip("Moon elev where moonlight begins (toward moon vs local up).")]
    [Range(-0.2f, 0.3f)] public float moonRiseElev = -0.05f;

    [Tooltip("Moon elev where moonlight is full.")]
    [Range(0f, 0.5f)] public float moonFullElev = 0.22f;

    [Header("Line of sight (direct light only)")]
    [Tooltip("Height above the player along planet-up for the LoS ray origin (eye height).")]
    [Min(0.1f)] public float losEyeHeight = 1.6f;

    [Tooltip("Seconds to ease LoS open/close (occlusion by mountains). Larger = softer.")]
    [Min(0.5f)] public float losSmoothTime = 6f;

    [Tooltip("Layers that can block sun/moon (terrain, buildings). Leave empty for Default raycast mask.")]
    public LayerMask losBlockers = ~0;

    [Header("Ambient")]
    public bool adaptAmbientForNight = true;

    [Range(0f, 2f)] public float dayAmbientIntensity = 0.85f;
    [Range(0f, 1f)] public float nightAmbientIntensity = 0.035f;
    [Range(0f, 1.5f)] public float goldenAmbientIntensity = 0.55f;
    [Range(0f, 1f)] public float dayReflectionIntensity = 0.14f;
    [Range(0f, 1f)] public float nightReflectionIntensity = 0f;

    [Tooltip("Open-sky blue fill while the sun is up.")]
    public Color dayAmbientColor = new Color(0.38f, 0.52f, 0.78f, 1f);

    [Tooltip("Deep night fill.")]
    public Color nightAmbientColor = new Color(0.02f, 0.025f, 0.045f, 1f);

    [Tooltip("Warm bounce at sunrise (rose).")]
    [ColorUsage(false, false)]
    public Color dawnAmbientColor = new Color(0.95f, 0.48f, 0.58f, 1f);

    [Tooltip("Warm bounce at sunset (amber).")]
    [ColorUsage(false, false)]
    public Color twilightAmbientColor = new Color(0.92f, 0.52f, 0.28f, 1f);

    [Tooltip("Cool blue afterglow after the sun sets (civil twilight).")]
    public Color civilTwilightAmbientColor = new Color(0.28f, 0.36f, 0.62f, 1f);

    [Range(0f, 1f)] public float goldenAmbientBlend = 0.45f;

    [Header("Editor")]
    public bool playInEditMode = false;

    Light _moonLight;
    bool _warnedNoLight;
    Vector3 _planetNorth = Vector3.up;
    Vector3 _equatorDir = Vector3.right;
    bool _celestialBasisReady;
    float _dayAngleDegrees;
    bool _dayAngleSeeded;
    bool _sunShadowsOn = true;
    bool _moonShadowsOn;
    float _sunLos = 1f;
    float _moonLos;
    float _sunLosVel;
    float _moonLosVel;
    Vector3 _prevTowardSun;
    bool _prevTowardSunReady;
    float _horizonDepress;
    float _horizonDepressVel;
    float _twilightSmooth;
    float _twilightSmoothVel;
    float _cachedLosMaxDistance = 5000f;
    readonly RaycastHit[] _losHits = new RaycastHit[16];
#if UNITY_EDITOR
    double _lastEditorTime;
#endif

    static readonly int SunDirectionId = Shader.PropertyToID("_SunDirection");
    static readonly int PlayerSunAmountId = Shader.PropertyToID("_PlayerSunAmount");
    static readonly int PlayerTwilightId = Shader.PropertyToID("_PlayerTwilight");
    static readonly int SkySunColorId = Shader.PropertyToID("_SkySunColor");
    static readonly int MoonDirectionId = Shader.PropertyToID("_MoonDirection");
    static readonly int SkyMoonColorId = Shader.PropertyToID("_SkyMoonColor");
    static readonly int SkyMoonAmountId = Shader.PropertyToID("_SkyMoonAmount");
    static readonly int MoonLightColorId = Shader.PropertyToID("_MoonLightColor");
    static readonly int MoonLightStrengthId = Shader.PropertyToID("_MoonLightStrength");
    static readonly int PlanetCenterWSId = Shader.PropertyToID("_PlanetCenterWS");
    const float SkySunHdrDay = 2.35f;
    const float SkySunHdrHorizon = 2.9f;
    const float SkyMoonHdr = 1.55f;

    /// <summary>0 = bright sky, 1 = deep night. Lanterns / fog use this (elevation, not shade).</summary>
    public static float NightAmount { get; private set; }

    /// <summary>0–1 direct sunlight at the player (elevation × LoS). Directional sun intensity.</summary>
    public static float SunAmount { get; private set; }

    /// <summary>0–1 open-sky daylight from sun elevation (civil twilight after set). Atmosphere syncs to this.</summary>
    public static float SkyDayAmount { get; private set; }

    /// <summary>0–1 golden-hour warmth (sun near horizon). Atmosphere / fog warm tint.</summary>
    public static float TwilightAmount { get; private set; }

    /// <summary>Orbital sun directional (may be intensity 0 at night).</summary>
    public static Light ActiveSunLight { get; private set; }

    /// <summary>World-space direction toward the sun (= -sun.forward).</summary>
    public static Vector3 TowardSunWS { get; private set; } = Vector3.up;

    /// <summary>True while local sun elevation is increasing (dawn / morning).</summary>
    public static bool SunIsRising { get; private set; } = true;

    /// <summary>Current directional + skybox sun tint (LDR).</summary>
    public static Color ActiveSunColor { get; private set; } = Color.white;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureExistsAtRuntime()
    {
        if (FindAnyObjectByType<PlanetDayNightCycle>() != null)
            return;
        var go = new GameObject("DayNightCycle_SunOrbit");
        go.AddComponent<PlanetDayNightCycle>();
    }

    void OnEnable()
    {
#if UNITY_EDITOR
        _lastEditorTime = EditorApplication.timeSinceStartup;
#endif
        _celestialBasisReady = false;
        _dayAngleSeeded = false;
        _prevTowardSunReady = false;
        _horizonDepress = 0f;
        _horizonDepressVel = 0f;
        _twilightSmooth = 0f;
        _twilightSmoothVel = 0f;
        SunAmount = 1f;
        SkyDayAmount = 1f;
        NightAmount = 0f;
        TwilightAmount = 0f;
        SunIsRising = true;
        ActiveSunColor = daySunColor;
        Shader.SetGlobalFloat(PlayerSunAmountId, 1f);
        Shader.SetGlobalFloat(PlayerTwilightId, 0f);
        Shader.SetGlobalColor(SkySunColorId, new Color(daySunColor.r * SkySunHdrDay, daySunColor.g * SkySunHdrDay, daySunColor.b * SkySunHdrDay, 1f));
        Shader.SetGlobalVector(MoonDirectionId, new Vector4(0f, -1f, 0f, 0f));
        Shader.SetGlobalColor(SkyMoonColorId, new Color(0f, 0f, 0f, 0f));
        Shader.SetGlobalFloat(SkyMoonAmountId, 0f);
        Shader.SetGlobalColor(MoonLightColorId, Color.black);
        Shader.SetGlobalFloat(MoonLightStrengthId, 0f);
        Tick(rotate: false, dt: 0f);
    }

    void Update()
    {
        bool doRotate;
        float dt;
        if (Application.isPlaying)
        {
            doRotate = true;
            dt = Time.unscaledDeltaTime;
        }
        else
        {
#if UNITY_EDITOR
            double now = EditorApplication.timeSinceStartup;
            dt = (float)(now - _lastEditorTime);
            _lastEditorTime = now;
            doRotate = playInEditMode && dt > 0f && dt <= 0.5f;
#else
            doRotate = false;
            dt = 0f;
#endif
        }

        Tick(doRotate, dt);
    }

    void Tick(bool rotate, float dt)
    {
        Transform planetXform = PlanetReferenceResolver.ResolvePlanetTransform();
        if (planetXform != null)
            Shader.SetGlobalVector(PlanetCenterWSId, planetXform.position);

        Light sun = ResolveSun();
        if (sun == null)
        {
            if (!_warnedNoLight)
            {
                Debug.LogWarning(
                    "PlanetDayNightCycle: No directional sun found. Day/night cycle is idle.",
                    this);
                _warnedNoLight = true;
            }
            return;
        }
        _warnedNoLight = false;

        Vector3 up = ResolveLocalUp();
        Vector3 north = ResolvePlanetNorth(planetXform);
        EnsureCelestialBasis(north);

        if (!_dayAngleSeeded)
        {
            SeedDayAngleFromSun(sun, _planetNorth);
            _dayAngleSeeded = true;
        }

        if (rotate && _celestialBasisReady)
        {
            float anglePerSecond = 360f / Mathf.Max(0.01f, dayLengthSeconds);
            _dayAngleDegrees += anglePerSecond * speedMultiplier * dt;
            if (_dayAngleDegrees >= 360f || _dayAngleDegrees < 0f)
                _dayAngleDegrees = Mathf.Repeat(_dayAngleDegrees, 360f);
        }

        Vector3 towardSun = EvaluateTowardSun(_planetNorth, _equatorDir, _dayAngleDegrees);
        if (towardSun.sqrMagnitude < 1e-8f)
            towardSun = _planetNorth;
        else
            towardSun.Normalize();

        // Increasing day angle: east → noon → west (negated Unity AngleAxis around north).
        Vector3 sunHint = _planetNorth;
        if (Mathf.Abs(Vector3.Dot(-towardSun, sunHint)) > 0.98f)
            sunHint = _equatorDir;
        sun.transform.rotation = Quaternion.LookRotation(-towardSun, sunHint);

        TowardSunWS = towardSun;
        ActiveSunLight = sun;
        Shader.SetGlobalVector(SunDirectionId, new Vector4(towardSun.x, towardSun.y, towardSun.z, 0f));
        if (RenderSettings.sun != sun)
            RenderSettings.sun = sun;

        // Lighting gates only in play mode; leave authored intensities alone in edit mode.
        if (!Application.isPlaying)
            return;

        Vector3 towardMoon = -towardSun;
        if (towardMoon.sqrMagnitude > 1e-6f)
            towardMoon.Normalize();
        else
            towardMoon = up;

        float sunElevGeom = Vector3.Dot(up, towardSun);
        float moonElev = Vector3.Dot(up, towardMoon);

        Transform player = RuntimeSceneRefs.GetPlayerTransform();
        // LoS + altitude from the body, not the camera — look/bob must not flicker dawn reds.
        Vector3 eye = ResolveStableEye(player, up);
        float maxDist = ResolveLosMaxDistance(eye);

        float sunLosTarget = HasLineOfSight(eye, towardSun, maxDist, player) ? 1f : 0f;
        float moonLosTarget = HasLineOfSight(eye, towardMoon, maxDist, player) ? 1f : 0f;
        float smooth = Mathf.Max(0.5f, losSmoothTime);
        float stepDt = Mathf.Max(dt, 0f);
        _sunLos = Mathf.SmoothDamp(_sunLos, sunLosTarget, ref _sunLosVel, smooth, Mathf.Infinity, stepDt);
        _moonLos = Mathf.SmoothDamp(_moonLos, moonLosTarget, ref _moonLosVel, smooth, Mathf.Infinity, stepDt);

        // --- Elevation clocks: shared twilight band (sunset curve reversed at sunrise) ---
        // Geometric elev is the tangent plane. From altitude the visual limb sits below that,
        // so a still-visible sun would otherwise read as night. Offset by horizon depression.
        float horizonTarget = HorizonDepressionSin(planetXform, eye);
        _horizonDepress = Mathf.SmoothDamp(_horizonDepress, horizonTarget, ref _horizonDepressVel, 0.85f, Mathf.Infinity, stepDt);
        float sunElev = sunElevGeom + _horizonDepress;

        // Rising/setting from sun motion only (same local up both samples). Player travel
        // used to flip elevDelta and thrash dawn rose ↔ dusk amber while walking.
        float elevDelta = 0f;
        if (_prevTowardSunReady)
            elevDelta = Vector3.Dot(up, towardSun) - Vector3.Dot(up, _prevTowardSun);
        _prevTowardSun = towardSun;
        _prevTowardSunReady = true;
        const float risingHysteresis = 1.5e-5f;
        bool sunRising = SunIsRising;
        if (elevDelta > risingHysteresis)
            sunRising = true;
        else if (elevDelta < -risingHysteresis)
            sunRising = false;
        SunIsRising = sunRising;

        float elevLight = sunElev + lightingPhaseBias;
        float brightnessHold = sunRising ? morningBrightnessHold : eveningBrightnessHold;
        elevLight += brightnessHold;

        float colorLead = sunRising ? morningColorLead : eveningColorHold;
        float elevColor = sunElev + lightingPhaseBias + colorLead + brightnessHold * 0.65f;

        float dayUntil = eveningDayUntilElev;
        float fadeStartElev = sunRising ? dayUntil : Mathf.Max(dayUntil, eveningSunsetHoldElev);
        float nightBy = Mathf.Min(eveningNightByElev, fadeStartElev - 0.05f);

        // Direct disc + open sky share the same twilight band as sunset (reversed at sunrise).
        float directZero = Mathf.Min(sunDirectZeroElev, nightBy);
        float directFull = Mathf.Max(sunDirectFullElev, directZero + 0.04f);
        float directElev = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(directZero, directFull, elevLight));

        float skyZero = Mathf.Min(skyDayZeroElev, nightBy);
        float skyFull = Mathf.Max(skyDayFullElev, skyZero + 0.08f);
        float skyDay = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(skyZero, skyFull, elevLight));

        // Twilight: morning uses dayUntil; evening waits until sunsetHold (delays night ~10s on default day).
        float twilightT = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(fadeStartElev, nightBy, sunElev));
        float dayHold = 1f - twilightT;
        skyDay = Mathf.Lerp(skyDay, 1f, dayHold);
        directElev = Mathf.Lerp(directElev, Mathf.Max(directElev, 0.92f), dayHold);

        // Evening: lock daylight while the disc is still above the visual limb (altitude-aware).
        // Clear LoS is a second vote — raised ground can see the sun after geometric set.
        bool discVisiblyUp = sunElev > 0f || (_sunLos > 0.55f && sunElevGeom > eveningNightByElev);
        if (!sunRising && discVisiblyUp)
        {
            twilightT = 0f;
            skyDay = 1f;
            directElev = Mathf.Max(directElev, 0.92f);
        }

        // Near the horizon, LoS can false-block a low sun — skip while the disc is still up at sunset.
        float losWeight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.04f, 0.28f, sunElev));
        if (!sunRising && discVisiblyUp)
            losWeight = 0f;
        else
            losWeight *= twilightT;
        float directSun = directElev * Mathf.Lerp(1f, _sunLos, losWeight);

        // Golden hour: same twilight band both ways; morning uses richer dawn weights.
        float goldLow = Mathf.Min(goldenHourLowElev, nightBy * 0.65f);
        float goldHigh = sunRising
            ? Mathf.Max(morningGoldFadeElev, goldLow + 0.08f)
            : Mathf.Max(goldenHourHighElev, goldLow + 0.08f);
        float goldRise = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(goldLow, goldLow + 0.08f, elevColor));
        float goldFall = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(goldHigh * 0.28f, goldHigh, elevColor));
        float golden = Mathf.Clamp01(goldRise * goldFall);

        // Dawn: push glow hard early, then let goldFall hand off to normal day ambient.
        float morningGlow = sunRising ? golden * (1f + morningGlowBoost) : golden;
        morningGlow = Mathf.Clamp01(morningGlow);

        // Civil afterglow: cool blue sky after the disc is gone, before night.
        float afterglow = Mathf.Clamp01((1f - directElev) * skyDay);

        SkyDayAmount = Mathf.Clamp01(skyDay);
        SunAmount = Mathf.Clamp01(directSun);
        NightAmount = Mathf.Clamp01(1f - SkyDayAmount);
        _twilightSmooth = Mathf.SmoothDamp(
            _twilightSmooth, Mathf.Clamp01(morningGlow), ref _twilightSmoothVel, 0.35f, Mathf.Infinity, stepDt);
        TwilightAmount = Mathf.Clamp01(_twilightSmooth);

        // Atmosphere follows the sky, not mountain shade.
        Shader.SetGlobalFloat(PlayerSunAmountId, SkyDayAmount);
        Shader.SetGlobalFloat(PlayerTwilightId, TwilightAmount);

        float moonSky = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(moonRiseElev, Mathf.Max(moonFullElev, moonRiseElev + 0.05f), moonElev));
        float moonAmount = enableMoonlight ? NightAmount * moonSky * _moonLos : 0f;
        // Same twilight gate both ways: moon off while sun is on the day side of the band.
        moonAmount *= twilightT;

        Shader.SetGlobalVector(MoonDirectionId, new Vector4(towardMoon.x, towardMoon.y, towardMoon.z, 0f));
        Shader.SetGlobalColor(SkyMoonColorId, new Color(
            moonColor.r * SkyMoonHdr, moonColor.g * SkyMoonHdr, moonColor.b * SkyMoonHdr, 1f));
        Shader.SetGlobalFloat(SkyMoonAmountId, moonAmount);
        float moonStrength = enableMoonlight ? moonIntensity * Mathf.Clamp01(moonAmount) : 0f;
        Shader.SetGlobalColor(MoonLightColorId, moonColor);
        Shader.SetGlobalFloat(MoonLightStrengthId, moonStrength);

        // Warm disc: rose at dawn, amber at dusk. Skybox disc uses the same tint.
        // Tint follows twilight only — LoS shade must not pulse horizon reds while moving.
        Color horizonSun = sunRising ? dawnSunColor : duskSunColor;
        float sunTint = TwilightAmount;
        sun.color = Color.Lerp(daySunColor, horizonSun, sunTint);
        ActiveSunColor = sun.color;
        Color skySun = Color.Lerp(daySunColor, horizonSun, TwilightAmount);
        float skyHdr = Mathf.Lerp(SkySunHdrDay, SkySunHdrHorizon, TwilightAmount);
        Shader.SetGlobalColor(SkySunColorId, new Color(skySun.r * skyHdr, skySun.g * skyHdr, skySun.b * skyHdr, 1f));
        sun.intensity = daySunIntensity * SunAmount;
        sun.shadowStrength = sunShadowStrength;
        _sunShadowsOn = sun.intensity > (_sunShadowsOn ? 0.01f : 0.04f);
        sun.shadows = _sunShadowsOn ? LightShadows.Soft : LightShadows.None;
        sun.enabled = true;

        if (adaptAmbientForNight)
        {
            float glowBlend = Mathf.Clamp01(TwilightAmount * goldenAmbientBlend * (sunRising ? (1f + morningGlowBoost) : 1f));
            float glowInt = goldenAmbientIntensity * (sunRising ? (1f + morningGlowBoost * 0.4f) : 1f);
            Color glowColor = sunRising ? dawnAmbientColor : twilightAmbientColor;

            // Ambient = sky dome: rose dawn, then day blue; amber evening holds longer via elevLight.
            Color amb = Color.Lerp(nightAmbientColor, dayAmbientColor, SkyDayAmount);
            amb = Color.Lerp(amb, civilTwilightAmbientColor, afterglow * (1f - TwilightAmount) * 0.85f);
            amb = Color.Lerp(amb, glowColor, glowBlend);

            float ambInt = Mathf.Lerp(nightAmbientIntensity, dayAmbientIntensity, SkyDayAmount);
            ambInt = Mathf.Lerp(ambInt, glowInt, glowBlend);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientSkyColor = amb;
            RenderSettings.ambientIntensity = ambInt;
            RenderSettings.reflectionIntensity = Mathf.Lerp(nightReflectionIntensity, dayReflectionIntensity, SkyDayAmount);
        }

        UpdateMoonlight(towardMoon, up, moonAmount);
    }

    /// <summary>
    /// Sine of the angle the planet limb sits below the local tangent, from <paramref name="eye"/> altitude.
    /// Add to geometric sun elevation so lighting matches a still-visible disc on raised ground.
    /// </summary>
    static float HorizonDepressionSin(Transform planetXform, Vector3 eye)
    {
        if (planetXform == null)
            return 0f;

        Planet planet = planetXform.GetComponent<Planet>();
        float seaLevel = planet != null ? planet.GetBaseRadiusWorld() : 0f;
        if (seaLevel < 1f)
            return 0f;

        float radial = Vector3.Distance(eye, planetXform.position);
        if (radial <= seaLevel)
            return 0f;

        float ratio = seaLevel / radial;
        return Mathf.Sqrt(Mathf.Max(0f, 1f - ratio * ratio));
    }

    /// <summary>Stable eye for LoS / altitude — player body, not the follow camera.</summary>
    Vector3 ResolveStableEye(Transform player, Vector3 localUp)
    {
        if (player != null)
            return player.position + localUp * losEyeHeight;
        if (Camera.main != null)
            return Camera.main.transform.position;
        return transform.position + localUp * losEyeHeight;
    }

    float ResolveLosMaxDistance(Vector3 eye)
    {
        Transform planet = PlanetReferenceResolver.ResolvePlanetTransform();
        if (planet != null)
        {
            float radial = Vector3.Distance(eye, planet.position);
            // Past the far limb of the planet (and then some) so mountains on the horizon count.
            _cachedLosMaxDistance = Mathf.Max(500f, radial * 2.5f);
        }
        return _cachedLosMaxDistance;
    }

    bool HasLineOfSight(Vector3 origin, Vector3 direction, float maxDistance, Transform player)
    {
        if (direction.sqrMagnitude < 1e-8f)
            return false;

        Vector3 start = origin + direction * 0.35f;
        int mask = losBlockers.value == 0 ? Physics.DefaultRaycastLayers : losBlockers.value;

        int count = Physics.RaycastNonAlloc(start, direction, _losHits, maxDistance, mask, QueryTriggerInteraction.Ignore);
        if (count <= 0)
            return true;

        float nearest = float.PositiveInfinity;
        Transform nearestT = null;
        for (int i = 0; i < count; i++)
        {
            Transform t = _losHits[i].transform;
            if (t == null)
                continue;
            if (player != null && t.IsChildOf(player))
                continue;
            if (_losHits[i].distance < nearest)
            {
                nearest = _losHits[i].distance;
                nearestT = t;
            }
        }

        return nearestT == null;
    }

    Vector3 ResolvePlanetNorth(Transform planetXform)
    {
        if (planetNorthOverride.sqrMagnitude > 1e-8f)
            return planetNorthOverride.normalized;
        if (planetXform != null)
            return planetXform.up.normalized;
        return Vector3.up;
    }

    /// <summary>
    /// Cache planet north + a stable equator reference. Rebuilds if north drifts (rare).
    /// </summary>
    void EnsureCelestialBasis(Vector3 north)
    {
        if (north.sqrMagnitude < 1e-8f)
            north = Vector3.up;
        else
            north.Normalize();

        if (_celestialBasisReady && Vector3.Dot(_planetNorth, north) > 0.999f)
            return;

        _planetNorth = north;
        Vector3 equator = Vector3.Cross(north, Vector3.forward);
        if (equator.sqrMagnitude < 1e-4f)
            equator = Vector3.Cross(north, Vector3.right);
        if (equator.sqrMagnitude < 1e-8f)
            equator = Vector3.right;
        _equatorDir = equator.normalized;
        _celestialBasisReady = true;
    }

    /// <summary>
    /// Toward-sun on the Earth-like cone: declination from tilt×season, hour angle around north.
    /// Increasing <paramref name="dayAngleDegrees"/> advances morning → noon → evening
    /// (Unity AngleAxis around north is negated so the disc rises east / sets west).
    /// </summary>
    Vector3 EvaluateTowardSun(Vector3 north, Vector3 equatorDir, float dayAngleDegrees)
    {
        float declRad = axialTiltDegrees * Mathf.Deg2Rad *
                        Mathf.Sin(seasonPhaseDegrees * Mathf.Deg2Rad);
        Vector3 sunDir = Mathf.Cos(declRad) * equatorDir + Mathf.Sin(declRad) * north;
        if (sunDir.sqrMagnitude < 1e-8f)
            sunDir = equatorDir;
        else
            sunDir.Normalize();
        return Quaternion.AngleAxis(-dayAngleDegrees, north) * sunDir;
    }

    /// <summary>Match hour angle to the authored light so enabling the cycle does not snap the disc.</summary>
    void SeedDayAngleFromSun(Light sun, Vector3 north)
    {
        if (sun == null || !_celestialBasisReady)
        {
            _dayAngleDegrees = 0f;
            return;
        }

        Vector3 toward = -sun.transform.forward;
        if (toward.sqrMagnitude < 1e-8f)
        {
            _dayAngleDegrees = 0f;
            return;
        }

        Vector3 flat = Vector3.ProjectOnPlane(toward.normalized, north);
        if (flat.sqrMagnitude < 1e-6f)
        {
            _dayAngleDegrees = 0f;
            return;
        }

        flat.Normalize();
        // Inverse of AngleAxis(-dayAngle, north) applied to equatorDir.
        _dayAngleDegrees = Vector3.SignedAngle(flat, _equatorDir, north);
        if (_dayAngleDegrees < 0f)
            _dayAngleDegrees += 360f;
    }

    void UpdateMoonlight(Vector3 towardMoon, Vector3 up, float moonAmount)
    {
        if (!enableMoonlight)
        {
            if (_moonLight != null)
            {
                _moonLight.intensity = 0f;
                _moonLight.shadows = LightShadows.None;
                _moonShadowsOn = false;
                _moonLight.enabled = true;
            }
            return;
        }

        EnsureMoonLight();
        if (_moonLight == null)
            return;

        Vector3 moonForward = -towardMoon;
        Vector3 moonHint = up;
        if (Mathf.Abs(Vector3.Dot(moonForward, moonHint)) > 0.98f)
            moonHint = Vector3.Cross(moonForward, Vector3.right);
        if (moonHint.sqrMagnitude < 1e-6f)
            moonHint = Vector3.forward;
        _moonLight.transform.rotation = Quaternion.LookRotation(moonForward, moonHint.normalized);

        float intensity = moonIntensity * Mathf.Clamp01(moonAmount);
        _moonLight.color = moonColor;
        _moonLight.intensity = intensity;
        _moonLight.shadowStrength = moonShadowStrength;
        _moonShadowsOn = intensity > (_moonShadowsOn ? 0.01f : 0.03f);
        _moonLight.shadows = _moonShadowsOn ? LightShadows.Soft : LightShadows.None;
        _moonLight.enabled = true;
        _moonLight.gameObject.SetActive(true);
    }

    void EnsureMoonLight()
    {
        if (_moonLight != null)
            return;

        Transform existing = transform.Find(MoonLightName);
        GameObject go = existing != null ? existing.gameObject : null;
        if (go == null)
        {
            go = new GameObject(MoonLightName);
            go.transform.SetParent(transform, false);
        }

        go.SetActive(true);
        _moonLight = go.GetComponent<Light>();
        if (_moonLight == null)
            _moonLight = go.AddComponent<Light>();

        _moonLight.type = LightType.Directional;
        _moonLight.color = moonColor;
        _moonLight.intensity = 0f;
        _moonLight.shadows = LightShadows.Soft;
        _moonLight.shadowStrength = moonShadowStrength;
        _moonLight.cullingMask = ~(1 << 5);
        _moonLight.renderMode = LightRenderMode.Auto;
    }

    Vector3 ResolveLocalUp()
    {
        Transform player = RuntimeSceneRefs.GetPlayerTransform();
        Transform planet = PlanetReferenceResolver.ResolvePlanetTransform();
        if (player != null && planet != null)
        {
            Vector3 up = player.position - planet.position;
            if (up.sqrMagnitude > 1e-6f)
                return up.normalized;
        }
        if (player != null)
            return player.up;
        return Vector3.up;
    }

    Light ResolveSun()
    {
        if (IsOrbitalSun(sunLight))
            return sunLight;

        if (IsOrbitalSun(RenderSettings.sun))
        {
            sunLight = RenderSettings.sun;
            return sunLight;
        }

        Light named = null;
        Light best = null;
        foreach (Light l in FindObjectsByType<Light>(FindObjectsInactive.Exclude))
        {
            if (!IsOrbitalSun(l))
                continue;
            if (l.gameObject.name == "Directional Light")
                named = l;
            if (best == null || l.intensity > best.intensity)
                best = l;
        }

        sunLight = named != null ? named : best;
        return sunLight;
    }

    /// <summary>The orbiting scene sun, even when intensity is 0 at night.</summary>
    public static bool IsOrbitalSun(Light light)
    {
        if (light == null || light.type != LightType.Directional)
            return false;
        return !IsExcludedSunName(light);
    }

    /// <summary>
    /// True for the real scene sun only. Excludes night lanterns, moon, and HUD preview lights.
    /// </summary>
    public static bool IsEligibleSunLight(Light light)
    {
        if (!IsOrbitalSun(light) || !light.isActiveAndEnabled)
            return false;
        if (light.cullingMask == (1 << 5))
            return false;
        return true;
    }

    static bool IsExcludedSunName(Light light)
    {
        string n = light.name;
        if (n.IndexOf("NightLantern", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("MoonLight", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("PreviewLight", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("PreviewCam", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            n.IndexOf("HudPreview", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        Transform root = light.transform.root;
        if (root != null)
        {
            string rn = root.name;
            if (rn.IndexOf("PowerUpHud_PreviewWorld", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                rn.IndexOf("Stargrave_GameHud", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }
}
