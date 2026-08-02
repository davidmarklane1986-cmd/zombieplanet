using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu()]
public class ColourSettings : ScriptableObject
{

    public Material planetMaterial;
    public BiomeColourSettings biomeColourSettings;

    [Header("Height-based tinting (elevation bands)")]
    [Tooltip("Tint colours by elevation. Use custom bands below, or leave empty for default (shore/mid/peak).")]
    public bool useHeightBands;
    [Tooltip("Leave empty to use built-in default bands: low=darker, mid=neutral, high=lighter.")]
    public HeightBand[] heightBands = new HeightBand[0];

    [Header("Slope-based texturing (steep = rock/cliff)")]
    [Tooltip("Blend toward steep colour on slopes. Requires shader to use UV2.x (slope 0-1).")]
    public bool useSlopeTint;
    [Range(15f, 90f)]
    [Tooltip("Slope angle (degrees) above which steep tint starts. Lower = more rock on gentler slopes.")]
    public float steepSlopeAngleStart = 38f;
    [Range(0f, 1f)]
    [Tooltip("How much to blend toward steep colour at vertical cliffs (1 = full rock).")]
    public float steepBlend = 0.75f;
    [Tooltip("Colour for cliffs/rock (typically grey-brown).")]
    public Color steepSlopeColor = new Color(0.42f, 0.38f, 0.33f);

    [System.Serializable]
    public class HeightBand
    {
        [Range(0f, 1f)]
        public float elevationMin = 0f;
        [Range(0f, 1f)]
        public float elevationMax = 1f;
        public Color tint = Color.white;
        [Range(0f, 1f)]
        public float blendStrength = 0.5f;
    }

    /// <summary>Default terrain-style bands when useHeightBands is on but heightBands is empty.</summary>
    public static HeightBand[] GetDefaultHeightBands()
    {
        return new HeightBand[]
        {
            new HeightBand { elevationMin = 0f, elevationMax = 0.25f, tint = new Color(0.7f, 0.75f, 0.85f), blendStrength = 0.4f },   // low: darker/wetter
            new HeightBand { elevationMin = 0.25f, elevationMax = 0.65f, tint = Color.white, blendStrength = 0f },                 // mid: neutral
            new HeightBand { elevationMin = 0.65f, elevationMax = 1f, tint = new Color(1.1f, 1.05f, 1f), blendStrength = 0.35f }     // high: lighter/peak
        };
    }

    [System.Serializable]
    public class BiomeColourSettings
    {
        public Biome[] biomes;
        public NoiseSettings noise;
        public float noiseOffset;
        public float noiseStrength;
        [Range(0,1)]
        public float blendAmount;
        [Range(0f, 0.15f)]
        [Tooltip("How much to blur biome boundaries so textures fade into each other (0 = sharp edge).")]
        public float textureBoundaryBlur = 0.03f;
        [Range(0.05f, 0.5f)]
        [Tooltip("How wide the overlap band is where both textures blend (higher = wider overlap, textures overlap more).")]
        public float textureOverlapWidth = 0.25f;

        [System.Serializable]
        public class Biome
        {
            public Gradient gradient;
            public Color tint;
            [Range(0, 1)]
            public float startHeight;
            [Range(0, 1)]
            public float tintPercent;
            [Tooltip("Optional texture for this biome (fallback when no per-key texture).")]
            public Texture2D terrainTexture;
            [Tooltip("One texture per gradient key (e.g. key 0 = sand, key 1 = grass, key 2 = rock). Index matches gradient color keys. Leave empty to use terrainTexture for whole biome.")]
            public List<Texture2D> texturesPerGradientKey;
        }

        [Tooltip("World-space tiling for per-biome terrain textures (X Z scale).")]
        public Vector2 textureTiling = new Vector2(1f, 1f);

        [Tooltip("Break up obvious tiling by blending three randomly offset/rotated texture samples (Heitz-style triangle-grid stochastic texturing).")]
        public bool useStochasticTexturing = true;
        [Range(1f, 8f)]
        [Tooltip("Higher = sharper blend between stochastic samples (less visible triangle seams, slightly more contrast).")]
        public float stochasticContrast = 4f;
    }

}
