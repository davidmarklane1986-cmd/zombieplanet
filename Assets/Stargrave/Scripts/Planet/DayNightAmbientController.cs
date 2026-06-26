using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Drives the scene's flat (Color) ambient so it is day/night aware: bright on the planet's
/// sun-facing hemisphere (so URP/Lit objects - the player, props, foliage - get a pleasant fill
/// on their shadowed sides in daylight) and dark on the night hemisphere (so night-side objects
/// stay dark and match the decoupled, dark planet terrain).
///
/// The day/night factor is computed with the SAME terminator math the atmosphere uses
/// (see PlanetAtmosphereScattering.shader / PlanetAtmosphereLayer.cs), so the ambient's lit side
/// lines up with the atmosphere's lit limb:
///   radial    = normalize(viewerPos - planetCenter)
///   towardSun = flipDayNightHemisphere ? -light.forward : light.forward
///   sunFacing = dot(radial, towardSun)
///   t         = smoothstep(terminatorStart, terminatorEnd, sunFacing)
///   ambient   = Lerp(nightAmbient, dayAmbient, t)
///
/// This controller OWNS RenderSettings.ambientLight at runtime/edit time, so the static
/// m_AmbientSkyColor stored in the scene no longer matters (it is overwritten every frame).
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class DayNightAmbientController : MonoBehaviour
{
    [Header("References (auto-resolved if left empty)")]
    [Tooltip("Viewer used to evaluate which hemisphere is in daylight. Defaults to Camera.main.")]
    public Transform viewer;
    [Tooltip("Planet center. Defaults to the Planet component, else the PlanetAtmosphereLayer transform.")]
    public Transform planetCenter;
    [Tooltip("Sun. Defaults to RenderSettings.sun, else the first active Directional light.")]
    public Light sun;

    [Header("Ambient colours")]
    [Tooltip("Flat ambient on the night hemisphere. Keep low so night-side objects stay dark like the terrain.")]
    [ColorUsage(false, false)]
    public Color nightAmbient = new Color(0.06f, 0.07f, 0.09f, 1f);
    [Tooltip("Flat ambient on the day hemisphere. Fills the shadowed sides of URP/Lit objects in daylight.")]
    [ColorUsage(false, false)]
    public Color dayAmbient = new Color(0.34f, 0.36f, 0.42f, 1f);

    [Header("Terminator (match the atmosphere)")]
    [Tooltip("Must match flipDayNightHemisphere on PlanetAtmosphereLayer so the lit side aligns with the atmosphere.")]
    public bool flipDayNightHemisphere = true;
    [Tooltip("smoothstep lower edge on sunFacing = dot(radial, towardSun). Atmosphere uses -0.28.")]
    [Range(-1f, 1f)] public float terminatorStart = -0.28f;
    [Tooltip("smoothstep upper edge on sunFacing. Atmosphere uses 0.42.")]
    [Range(-1f, 1f)] public float terminatorEnd = 0.42f;

    [Header("Bootstrap")]
    [Tooltip("Auto-create this controller at play time if none exists in the scene.")]
    public bool autoSpawn = true;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureExistsAtRuntime()
    {
        if (FindAnyObjectByType<DayNightAmbientController>() != null)
            return;
        var go = new GameObject("LightingController_DayNightAmbient");
        go.AddComponent<DayNightAmbientController>();
    }

    void OnEnable()
    {
        Apply();
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        terminatorEnd = Mathf.Max(terminatorEnd, terminatorStart + 0.001f);
        if (isActiveAndEnabled)
            Apply();
    }
#endif

    void LateUpdate()
    {
        Apply();
    }

    void Apply()
    {
        Transform v = ResolveViewer();
        Transform center = ResolvePlanetCenter();
        Light s = ResolveSun();
        if (v == null || center == null || s == null)
            return;

        Vector3 radial = v.position - center.position;
        if (radial.sqrMagnitude < 1e-8f)
            return;
        radial.Normalize();

        Vector3 towardSun = flipDayNightHemisphere ? -s.transform.forward : s.transform.forward;
        towardSun.Normalize();

        float sunFacing = Vector3.Dot(radial, towardSun);
        float t = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(terminatorStart, terminatorEnd, sunFacing));

        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = Color.Lerp(nightAmbient, dayAmbient, t);
    }

    Transform ResolveViewer()
    {
        if (viewer != null)
            return viewer;
        Camera cam = Camera.main;
        return cam != null ? cam.transform : null;
    }

    Transform ResolvePlanetCenter()
    {
        if (planetCenter != null)
            return planetCenter;

        var planet = FindFirstObjectByType<Planet>(FindObjectsInactive.Exclude);
        if (planet != null)
            return planet.transform;

        var atmosphere = FindFirstObjectByType<PlanetAtmosphereLayer>(FindObjectsInactive.Exclude);
        return atmosphere != null ? atmosphere.transform : null;
    }

    Light ResolveSun()
    {
        if (sun != null)
            return sun;
        if (RenderSettings.sun != null)
            return RenderSettings.sun;

        foreach (Light l in FindObjectsByType<Light>(FindObjectsInactive.Exclude))
        {
            if (l != null && l.type == LightType.Directional && l.isActiveAndEnabled)
                return l;
        }
        return null;
    }
}
