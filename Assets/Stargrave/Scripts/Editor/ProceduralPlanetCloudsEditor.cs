#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(ProceduralPlanetClouds))]
public sealed class ProceduralPlanetCloudsEditor : Editor
{
    SerializedProperty _planet;
    SerializedProperty _shaderOverride;
    SerializedProperty _seed;
    SerializedProperty _deriveSeedFromPlanet;
    SerializedProperty _noiseResolution;
    SerializedProperty _useFullscreenVolume;
    SerializedProperty _coverage;
    SerializedProperty _animateWeather;
    SerializedProperty _weatherMinCoverage;
    SerializedProperty _weatherMaxCoverage;
    SerializedProperty _weatherChangeDuration;
    SerializedProperty _weatherHoldDuration;
    SerializedProperty _weatherTimingJitter;
    SerializedProperty _density;
    SerializedProperty _cloudScale;
    SerializedProperty _weatherScale;
    SerializedProperty _detailScale;
    SerializedProperty _erosion;
    SerializedProperty _formationStrength;
    SerializedProperty _mediumDetail;
    SerializedProperty _smallDetail;
    SerializedProperty _cellularBreakup;
    SerializedProperty _cellularScale;
    SerializedProperty _warpStrength;
    SerializedProperty _cloudAltitude;
    SerializedProperty _cloudLayerThickness;
    SerializedProperty _verticalProfile;
    SerializedProperty _windDirection;
    SerializedProperty _cloudSpeed;
    SerializedProperty _windTurbulence;
    SerializedProperty _lowLayerSpeed;
    SerializedProperty _highLayerSpeed;
    SerializedProperty _cloudColor;
    SerializedProperty _sunIntensity;
    SerializedProperty _silverLining;
    SerializedProperty _nightIllumination;
    SerializedProperty _moonInfluence;
    SerializedProperty _interiorDarkness;
    SerializedProperty _enableShadows;
    SerializedProperty _shadowStrength;
    SerializedProperty _shadowSoftness;
    SerializedProperty _shadowQuality;
    SerializedProperty _shadowDistance;
    SerializedProperty _shadowMapUpdateFrequency;
    SerializedProperty _quality;
    SerializedProperty _maximumRaySteps;
    SerializedProperty _updateFrequency;
    SerializedProperty _lodStartDistance;
    SerializedProperty _lodEndDistance;
    SerializedProperty _cullDistance;
    SerializedProperty _showCloudBounds;
    SerializedProperty _showNoise;
    SerializedProperty _showShadowMap;

    void OnEnable()
    {
        _planet = serializedObject.FindProperty("planet");
        _shaderOverride = serializedObject.FindProperty("shaderOverride");
        _seed = serializedObject.FindProperty("seed");
        _deriveSeedFromPlanet = serializedObject.FindProperty("deriveSeedFromPlanet");
        _noiseResolution = serializedObject.FindProperty("noiseResolution");
        _useFullscreenVolume = serializedObject.FindProperty("useFullscreenVolume");
        _coverage = serializedObject.FindProperty("coverage");
        _animateWeather = serializedObject.FindProperty("animateWeather");
        _weatherMinCoverage = serializedObject.FindProperty("weatherMinCoverage");
        _weatherMaxCoverage = serializedObject.FindProperty("weatherMaxCoverage");
        _weatherChangeDuration = serializedObject.FindProperty("weatherChangeDuration");
        _weatherHoldDuration = serializedObject.FindProperty("weatherHoldDuration");
        _weatherTimingJitter = serializedObject.FindProperty("weatherTimingJitter");
        _density = serializedObject.FindProperty("density");
        _cloudScale = serializedObject.FindProperty("cloudScale");
        _weatherScale = serializedObject.FindProperty("weatherScale");
        _detailScale = serializedObject.FindProperty("detailScale");
        _erosion = serializedObject.FindProperty("erosion");
        _formationStrength = serializedObject.FindProperty("formationStrength");
        _mediumDetail = serializedObject.FindProperty("mediumDetail");
        _smallDetail = serializedObject.FindProperty("smallDetail");
        _cellularBreakup = serializedObject.FindProperty("cellularBreakup");
        _cellularScale = serializedObject.FindProperty("cellularScale");
        _warpStrength = serializedObject.FindProperty("warpStrength");
        _cloudAltitude = serializedObject.FindProperty("cloudAltitude");
        _cloudLayerThickness = serializedObject.FindProperty("cloudLayerThickness");
        _verticalProfile = serializedObject.FindProperty("verticalProfile");
        _windDirection = serializedObject.FindProperty("windDirection");
        _cloudSpeed = serializedObject.FindProperty("cloudSpeed");
        _windTurbulence = serializedObject.FindProperty("windTurbulence");
        _lowLayerSpeed = serializedObject.FindProperty("lowLayerSpeed");
        _highLayerSpeed = serializedObject.FindProperty("highLayerSpeed");
        _cloudColor = serializedObject.FindProperty("cloudColor");
        _sunIntensity = serializedObject.FindProperty("sunIntensity");
        _silverLining = serializedObject.FindProperty("silverLining");
        _nightIllumination = serializedObject.FindProperty("nightIllumination");
        _moonInfluence = serializedObject.FindProperty("moonInfluence");
        _interiorDarkness = serializedObject.FindProperty("interiorDarkness");
        _enableShadows = serializedObject.FindProperty("enableShadows");
        _shadowStrength = serializedObject.FindProperty("shadowStrength");
        _shadowSoftness = serializedObject.FindProperty("shadowSoftness");
        _shadowQuality = serializedObject.FindProperty("shadowQuality");
        _shadowDistance = serializedObject.FindProperty("shadowDistance");
        _shadowMapUpdateFrequency = serializedObject.FindProperty("shadowMapUpdateFrequency");
        _quality = serializedObject.FindProperty("quality");
        _maximumRaySteps = serializedObject.FindProperty("maximumRaySteps");
        _updateFrequency = serializedObject.FindProperty("updateFrequency");
        _lodStartDistance = serializedObject.FindProperty("lodStartDistance");
        _lodEndDistance = serializedObject.FindProperty("lodEndDistance");
        _cullDistance = serializedObject.FindProperty("cullDistance");
        _showCloudBounds = serializedObject.FindProperty("showCloudBounds");
        _showNoise = serializedObject.FindProperty("showNoise");
        _showShadowMap = serializedObject.FindProperty("showShadowMap");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        ProceduralPlanetClouds clouds = (ProceduralPlanetClouds)target;

        EditorGUILayout.HelpBox(
            "Coverage is the starting cloudiness. While playing, the weather cycle slowly drifts " +
            "between clear and cloudy. Turn Animate Weather off to keep a fixed sky.",
            MessageType.Info);

        EditorGUILayout.LabelField("Controls", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Regenerate Clouds"))
            {
                Undo.RecordObject(clouds, "Regenerate Planet Clouds");
                clouds.RegenerateClouds();
                EditorUtility.SetDirty(clouds);
            }
            if (GUILayout.Button("Randomise Seed"))
            {
                Undo.RecordObject(clouds, "Randomise Planet Cloud Seed");
                clouds.RandomiseSeed();
                EditorUtility.SetDirty(clouds);
            }
        }

        DrawSection("References", _planet, _shaderOverride);
        DrawSection("Generation", _seed, _deriveSeedFromPlanet, _noiseResolution, _useFullscreenVolume,
            _coverage, _density, _cloudScale, _weatherScale,
            _detailScale, _erosion, _formationStrength, _mediumDetail, _smallDetail,
            _cellularBreakup, _cellularScale, _warpStrength);
        DrawSection("Weather Cycle", _animateWeather, _weatherMinCoverage, _weatherMaxCoverage,
            _weatherChangeDuration, _weatherHoldDuration, _weatherTimingJitter);
        if (Application.isPlaying && clouds.animateWeather)
        {
            EditorGUILayout.HelpBox(
                $"Current coverage {clouds.CurrentCoverage:0.00}. " +
                "Weather picks clear, in-between, or cloudy at random. Wind heading can shift on its own clock.",
                MessageType.None);
        }
        DrawSection("Height", _cloudAltitude, _cloudLayerThickness, _verticalProfile);
        DrawSection("Wind", _windDirection, _cloudSpeed, _windTurbulence, _lowLayerSpeed, _highLayerSpeed);
        DrawSection("Lighting", _cloudColor, _sunIntensity, _silverLining, _nightIllumination,
            _moonInfluence, _interiorDarkness);
        DrawSection("Shadows", _enableShadows, _shadowStrength, _shadowSoftness, _shadowQuality,
            _shadowDistance, _shadowMapUpdateFrequency);
        DrawSection("Quality", _quality, _maximumRaySteps, _updateFrequency, _lodStartDistance,
            _lodEndDistance, _cullDistance);
        DrawSection("Debug", _showCloudBounds, _showNoise, _showShadowMap);

        if (clouds.planet == null && clouds.GetComponentInParent<Planet>() == null)
        {
            EditorGUILayout.HelpBox(
                "No Planet reference was found. The cloud shell will use a fallback radius until a planet is assigned.",
                MessageType.Warning);
        }

        if (clouds.enableShadows && QualitySettings.shadowDistance < 50f)
        {
            EditorGUILayout.HelpBox(
                "Current project shadow distance is low for cloud shadows. Increase the active URP/quality shadow distance if needed.",
                MessageType.None);
        }

        if (serializedObject.ApplyModifiedProperties())
            clouds.MarkDirty();
        if (Application.isPlaying && clouds.animateWeather)
            Repaint();
    }

    static void DrawSection(string title, params SerializedProperty[] properties)
    {
        EditorGUILayout.Space(3f);
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        for (int i = 0; i < properties.Length; i++)
        {
            if (properties[i] != null)
                EditorGUILayout.PropertyField(properties[i]);
        }
    }
}
#endif
