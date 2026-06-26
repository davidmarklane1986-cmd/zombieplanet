using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Slow day/night cycle driven by orbiting the SUN (the main directional light) over time.
/// Only the light is rotated - never the planet or the player.
///
/// Because the project's lighting systems all read the live main directional light direction,
/// rotating this one light automatically drives everything that depends on the sun:
///   (a) the planet terrain terminator - PlanetColourWithBiomes.shader / PlanetColour.shader use
///       GetMainLight() per fragment, so the lit/dark hemisphere sweeps as the light turns.
///   (b) the atmosphere scattering - PlanetAtmosphereLayer.cs pushes the directional light's
///       forward into PlanetAtmosphereScattering.shader each LateUpdate.
///   (c) ambient lighting - DayNightAmbientController.cs reads the sun's forward each LateUpdate.
///
/// Sun resolution matches those systems (RenderSettings.sun first, then the first/brightest active
/// Directional light) so we rotate exactly the light they read and they all stay in sync.
///
/// By default this self-spawns at runtime (RuntimeInitializeOnLoadMethod), so NO scene edit is
/// required. You can also drop it on any GameObject manually if you prefer an inspector instance.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class PlanetDayNightCycle : MonoBehaviour
{
    [Header("Cycle timing")]
    [Tooltip("Seconds for a full 360 degree orbit of the sun (a complete day/night cycle). Larger = slower.")]
    [Min(0.01f)]
    public float dayLengthSeconds = 180f;

    [Tooltip("Multiplies the cycle speed. 1 = normal, 2 = twice as fast, 0 = paused. Negative reverses.")]
    public float speedMultiplier = 1f;

    [Tooltip("World-space axis the sun orbits around. A gentle tilt sweeps the terminator nicely across the visible area.")]
    public Vector3 rotationAxis = new Vector3(0.2f, 0f, 1f);

    [Header("Sun (auto-resolved if left empty)")]
    [Tooltip("The directional light to orbit. If null: RenderSettings.sun, else the brightest active Directional light.")]
    public Light sunLight;

    [Header("Editor")]
    [Tooltip("If true, the sun also rotates in the Editor (edit mode). Off by default so it doesn't disturb your authored sun.")]
    public bool playInEditMode = false;

    [Header("Bootstrap")]
    [Tooltip("Auto-create this rotator at play time if none exists in the scene. No scene edit needed.")]
    public bool autoSpawn = true;

    // Global shader property read by StargraveSpaceSkybox.shader to place the visible sun disc.
    static readonly int SunDirectionId = Shader.PropertyToID("_SunDirection");

    private bool _warnedNoLight;
#if UNITY_EDITOR
    private double _lastEditorTime;
#endif

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
        // Place the skybox sun correctly before the first rotation frame (and in edit mode).
        PushSunDirectionToSkybox(ResolveSun());
    }

    void Update()
    {
        Light sun = ResolveSun();
        if (sun == null)
        {
            if (!_warnedNoLight)
            {
                Debug.LogWarning(
                    "PlanetDayNightCycle: No directional light found to orbit (RenderSettings.sun unset and no active Directional light). " +
                    "Day/night cycle is idle.",
                    this);
                _warnedNoLight = true;
            }
            return;
        }
        _warnedNoLight = false;

        bool doRotate;
        float dt;
        if (Application.isPlaying)
        {
            doRotate = true;
            dt = Time.deltaTime;
        }
        else
        {
#if UNITY_EDITOR
            double now = EditorApplication.timeSinceStartup;
            dt = (float)(now - _lastEditorTime);
            _lastEditorTime = now;
            // Only rotate in edit mode when explicitly enabled, and guard against large catch-up
            // steps when the editor was idle / recompiled.
            doRotate = playInEditMode && dt > 0f && dt <= 0.5f;
#else
            doRotate = false;
            dt = 0f;
#endif
        }

        if (doRotate)
        {
            Vector3 axis = rotationAxis.sqrMagnitude > 1e-8f ? rotationAxis.normalized : Vector3.up;
            float anglePerSecond = 360f / Mathf.Max(0.01f, dayLengthSeconds);
            float deltaAngle = anglePerSecond * speedMultiplier * dt;
            if (Mathf.Abs(deltaAngle) > 1e-7f)
            {
                // Rotating the light's transform changes transform.forward = the light direction, moving the terminator.
                sun.transform.Rotate(axis, deltaAngle, Space.World);
            }
        }

        // Always keep the skybox's visible sun aligned with the actual light direction.
        PushSunDirectionToSkybox(sun);
    }

    void PushSunDirectionToSkybox(Light sun)
    {
        if (sun == null)
            return;
        // Direction TOWARD the sun = opposite the directional light's travel direction.
        Vector3 toSun = -sun.transform.forward;
        Shader.SetGlobalVector(SunDirectionId, new Vector4(toSun.x, toSun.y, toSun.z, 0f));
    }

    Light ResolveSun()
    {
        if (sunLight != null)
            return sunLight;
        if (RenderSettings.sun != null)
        {
            sunLight = RenderSettings.sun;
            return sunLight;
        }

        Light best = null;
        foreach (Light l in FindObjectsByType<Light>(FindObjectsInactive.Exclude))
        {
            if (l == null || l.type != LightType.Directional || !l.isActiveAndEnabled)
                continue;
            if (best == null || l.intensity > best.intensity)
                best = l;
        }
        sunLight = best;
        return sunLight;
    }
}
