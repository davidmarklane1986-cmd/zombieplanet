using UnityEngine;

/// <summary>
/// Night lantern: local point fill pool + a weak camera-aimed directional for long-range night vision.
/// Off in daylight, full at night, with a smooth dusk/dawn dim. Skips lighting PlayerVisual meshes.
/// </summary>
[DisallowMultipleComponent]
public sealed class PlayerNightLight : MonoBehaviour
{
    const string PlayerVisualLayerName = "PlayerVisual";
    const string FillLightName = "NightLanternFill";
    const string SpotLightName = "NightLanternSpot";

    [Header("Shared")]
    [Tooltip("Height above the player along planet-up.")]
    public float heightOffset = 5f;
    [ColorUsage(false, true)]
    public Color lightColor = new Color(1f, 0.88f, 0.68f, 1f);
    [Tooltip("Intensity of the local point fill pool at full night.")]
    [Min(0f)] public float nightIntensity = 10f;
    [Tooltip("Radius of the local point fill pool (the lit circle around you).")]
    [Min(1f)] public float fillRange = 4000f;

    [Header("Forward directional (camera-aimed)")]
    [Tooltip("Directional light intensity at full night. No distance falloff — lights what you're looking at far away.")]
    [Min(0f)] public float directionalIntensity = 0.07f;

    [Header("Day / Night dimming")]
    [Tooltip("Local sun elevation where the lantern starts fading in (1 = noon, 0 = horizon, -1 = midnight).")]
    [Range(-1f, 1f)] public float fadeInSunElev = 0.20f;
    [Tooltip("Local sun elevation where the lantern reaches full brightness.")]
    [Range(-1f, 1f)] public float fadeFullSunElev = -0.10f;
    [Tooltip("How fast the lantern blend catches the sun (units/sec). Higher = snappier dusk/dawn.")]
    [Min(0.05f)] public float fadeSmoothSpeed = 0.85f;

    // Kept for older scene refs; redirected to the fill light.
    public Light pointLight;

    Light _fillLight;
    Light _spotLight;
    bool _configured;
    bool _layersReady;
    int _playerVisualLayer = -1;
    float _lanternBlend;
    Light _cachedSun;
    float _nextSunResolveTime;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureOnPlayer()
    {
        Transform player = RuntimeSceneRefs.GetPlayerTransform();
        if (player == null)
            return;
        if (player.GetComponent<PlayerNightLight>() != null)
            return;
        player.gameObject.AddComponent<PlayerNightLight>();
    }

    void Awake()
    {
        EnsureLights();
        EnsurePlayerExcludedFromLight();
        _lanternBlend = 0f;
    }

    void LateUpdate()
    {
        EnsureLights();
        EnsurePlayerExcludedFromLight();
        if (_fillLight == null || _spotLight == null)
            return;

        Vector3 up = transform.up;
        Vector3 pos = transform.position + up * heightOffset;
        Vector3 aim = GetCameraAim(up);

        _fillLight.transform.SetPositionAndRotation(pos, Quaternion.identity);
        // Directional: orientation only (position unused). Aim with the camera.
        _spotLight.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(aim, up));

        float target = EvaluateLanternTarget(up);
        _lanternBlend = Mathf.MoveTowards(_lanternBlend, target, fadeSmoothSpeed * Time.deltaTime);
        float t = Mathf.SmoothStep(0f, 1f, _lanternBlend);

        float fillIntensity = nightIntensity * t;
        float dirIntensity = directionalIntensity * t;
        if (t <= 0.001f || (fillIntensity <= 0.001f && dirIntensity <= 0.001f))
        {
            SetBothEnabled(false);
            return;
        }

        ApplyLight(_fillLight, LightType.Point, fillIntensity, Mathf.Lerp(1f, fillRange, t));
        ApplyLight(_spotLight, LightType.Directional, dirIntensity, 0f);
        pointLight = _fillLight;
    }

    void ApplyLight(Light light, LightType type, float intensity, float range)
    {
        if (light == null)
            return;

        bool on = intensity > 0.001f;
        light.enabled = on;
        if (!on)
        {
            light.intensity = 0f;
            return;
        }

        light.type = type;
        light.intensity = intensity;
        light.color = lightColor;
        if (type == LightType.Point || type == LightType.Spot)
            light.range = range;
    }

    void SetBothEnabled(bool on)
    {
        if (_fillLight != null)
        {
            _fillLight.enabled = on;
            if (!on) _fillLight.intensity = 0f;
        }
        if (_spotLight != null)
        {
            _spotLight.enabled = on;
            if (!on) _spotLight.intensity = 0f;
        }
    }

    Vector3 GetCameraAim(Vector3 up)
    {
        Camera cam = Camera.main;
        Vector3 aim = cam != null ? cam.transform.forward : transform.forward;
        if (aim.sqrMagnitude < 1e-6f)
            aim = transform.forward;
        // Slight downward bias so ground ahead still catches the directional.
        aim = (aim.normalized - up.normalized * 0.2f).normalized;
        if (aim.sqrMagnitude < 1e-6f)
            aim = Vector3.ProjectOnPlane(transform.forward, up).normalized;
        return aim.sqrMagnitude > 1e-6f ? aim : transform.forward;
    }

    float EvaluateLanternTarget(Vector3 planetUp)
    {
        Light sun = ResolveSun();
        if (sun == null || !sun.isActiveAndEnabled)
        {
            float ambientNight = PlanetDayNightCycle.NightAmount;
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Mathf.InverseLerp(0.75f, 0.95f, ambientNight)));
        }

        Vector3 toSun = -sun.transform.forward;
        float sunElev = Vector3.Dot(planetUp.normalized, toSun);
        float dusk = fadeInSunElev;
        float fullNight = Mathf.Min(fadeFullSunElev, dusk - 0.01f);
        return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Mathf.InverseLerp(dusk, fullNight, sunElev)));
    }

    Light ResolveSun()
    {
        if (RenderSettings.sun != null)
            return RenderSettings.sun;

        if (_cachedSun != null && _cachedSun.isActiveAndEnabled && _cachedSun.type == LightType.Directional)
            return _cachedSun;

        if (Time.unscaledTime < _nextSunResolveTime)
            return _cachedSun;

        _nextSunResolveTime = Time.unscaledTime + 1f;
        Light best = null;
        float bestIntensity = -1f;
        foreach (Light L in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude))
        {
            if (L == null || L.type != LightType.Directional || !L.isActiveAndEnabled)
                continue;
            if (L.intensity > bestIntensity)
            {
                bestIntensity = L.intensity;
                best = L;
            }
        }
        _cachedSun = best;
        return _cachedSun;
    }

    void EnsureLights()
    {
        _fillLight = EnsureNamedLight(FillLightName, "NightLantern", "NightTorch");
        _spotLight = EnsureNamedLight(SpotLightName, null, null);
        pointLight = _fillLight;

        if (_configured || _fillLight == null || _spotLight == null)
            return;

        ConfigureLight(_fillLight, LightType.Point);
        ConfigureLight(_spotLight, LightType.Directional);
        _configured = true;
    }

    Light EnsureNamedLight(string preferredName, string legacyNameA, string legacyNameB)
    {
        Transform existing = transform.Find(preferredName);
        if (existing == null && !string.IsNullOrEmpty(legacyNameA))
            existing = transform.Find(legacyNameA);
        if (existing == null && !string.IsNullOrEmpty(legacyNameB))
            existing = transform.Find(legacyNameB);

        if (existing != null)
        {
            // Rename legacy single lantern into the fill light when needed.
            if (existing.name != preferredName && preferredName == FillLightName)
                existing.name = preferredName;
            var light = existing.GetComponent<Light>();
            if (light != null)
                return light;
        }

        var go = new GameObject(preferredName);
        go.transform.SetParent(transform, false);
        return go.AddComponent<Light>();
    }

    void ConfigureLight(Light light, LightType type)
    {
        light.type = type;
        light.shadows = LightShadows.None;
        light.renderMode = LightRenderMode.ForcePixel;
        light.color = lightColor;
        light.intensity = 0f;
        light.range = type == LightType.Directional ? 0f : 1f;
        light.enabled = false;
        ApplyLightCullingMask(light);
    }

    void EnsurePlayerExcludedFromLight()
    {
        if (_layersReady)
            return;

        _playerVisualLayer = LayerMask.NameToLayer(PlayerVisualLayerName);
        if (_playerVisualLayer < 0)
            return;

        Transform model = transform.Find("CharacterModel");
        if (model != null)
            SetLayerRecursive(model, _playerVisualLayer);
        else
        {
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (r != null)
                    SetLayerRecursive(r.transform, _playerVisualLayer);
            }
        }

        if (_fillLight != null)
            ApplyLightCullingMask(_fillLight);
        if (_spotLight != null)
            ApplyLightCullingMask(_spotLight);

        _layersReady = true;
    }

    void ApplyLightCullingMask(Light light)
    {
        if (_playerVisualLayer < 0)
            _playerVisualLayer = LayerMask.NameToLayer(PlayerVisualLayerName);
        if (_playerVisualLayer < 0)
            return;

        light.cullingMask = ~(1 << _playerVisualLayer);
    }

    static void SetLayerRecursive(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        for (int i = 0; i < root.childCount; i++)
            SetLayerRecursive(root.GetChild(i), layer);
    }
}
