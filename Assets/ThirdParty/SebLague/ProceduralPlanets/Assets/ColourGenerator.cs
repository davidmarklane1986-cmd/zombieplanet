using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ColourGenerator
{

    ColourSettings settings;
    Texture2D texture;
    Texture2D biomeLookupTexture;
    Texture2DArray biomeTextureArray;
    Texture2D keyPositionsTexture;
    const int textureResolution = 50;
    const int biomeTextureSize = 256;
    const int biomeLookupWidth = 256;
    const int biomeLookupHeight = 128;
    const int maxGradientKeys = 8;
    INoiseFilter biomeNoiseFilter;

    public void UpdateSettings(ColourSettings settings)
    {
        this.settings = settings;
        if (texture == null || texture.height != settings.biomeColourSettings.biomes.Length)
        {
            texture = new Texture2D(textureResolution, settings.biomeColourSettings.biomes.Length);
        }
        biomeNoiseFilter = NoiseFilterFactory.CreateNoiseFilter(settings.biomeColourSettings.noise);
    }

    public void UpdateElevation(MinMax elevationMinMax)
    {
        settings.planetMaterial.SetVector("_elevationMinMax", new Vector4(elevationMinMax.Min, elevationMinMax.Max));
    }

    void BuildBiomeLookupTexture()
    {
        var biomes = settings.biomeColourSettings?.biomes;
        if (biomes == null || biomes.Length == 0) { biomeLookupTexture = null; return; }
        if (biomeLookupTexture == null || biomeLookupTexture.width != biomeLookupWidth || biomeLookupTexture.height != biomeLookupHeight)
            biomeLookupTexture = new Texture2D(biomeLookupWidth, biomeLookupHeight, TextureFormat.RGBA32, false);
        for (int j = 0; j < biomeLookupHeight; j++)
        {
            float v = (j + 0.5f) / biomeLookupHeight;
            float lat = (v - 0.5f) * Mathf.PI;
            float y = Mathf.Sin(lat);
            float rxz = Mathf.Cos(lat);
            for (int i = 0; i < biomeLookupWidth; i++)
            {
                float u = (i + 0.5f) / biomeLookupWidth;
                float lon = (u - 0.5f) * 2f * Mathf.PI;
                Vector3 point = new Vector3(rxz * Mathf.Cos(lon), y, rxz * Mathf.Sin(lon));
                float biomePercent = Mathf.Clamp01(BiomePercentFromPoint(point));
                biomeLookupTexture.SetPixel(i, j, new Color(biomePercent, 0f, 0f, 1f));
            }
        }
        biomeLookupTexture.Apply();
        biomeLookupTexture.wrapMode = TextureWrapMode.Clamp;
    }

    static bool UseGradientKeyTextures(ColourSettings.BiomeColourSettings.Biome[] biomes)
    {
        if (biomes == null) return false;
        foreach (var b in biomes)
        {
            if (b.texturesPerGradientKey != null)
                for (int i = 0; i < b.texturesPerGradientKey.Count; i++)
                    if (b.texturesPerGradientKey[i] != null) return true;
        }
        return false;
    }

    void BuildBiomeTextureArray()
    {
        var biomes = settings.biomeColourSettings.biomes;
        if (biomes == null || biomes.Length == 0)
        {
            biomeTextureArray = null;
            keyPositionsTexture = null;
            return;
        }
        bool usePerKey = UseGradientKeyTextures(biomes);
        bool anyTexture = false;
        foreach (var b in biomes)
        {
            if (b.terrainTexture != null) { anyTexture = true; break; }
            if (usePerKey && b.texturesPerGradientKey != null) { anyTexture = true; break; }
        }
        if (!anyTexture)
        {
            biomeTextureArray = null;
            keyPositionsTexture = null;
            return;
        }

        if (usePerKey)
        {
            int numBiomes = biomes.Length;
            int totalSlices = numBiomes * maxGradientKeys;
            if (keyPositionsTexture == null || keyPositionsTexture.width != maxGradientKeys || keyPositionsTexture.height != numBiomes)
                keyPositionsTexture = new Texture2D(maxGradientKeys, numBiomes, TextureFormat.RGBA32, false);
            Color[] keyPositions = new Color[maxGradientKeys * numBiomes];
            for (int b = 0; b < numBiomes; b++)
            {
                var grad = biomes[b].gradient;
                var keys = grad != null ? grad.colorKeys : null;
                int keyCount = keys != null ? Mathf.Min(keys.Length, maxGradientKeys) : 0;
                for (int k = 0; k < maxGradientKeys; k++)
                {
                    float pos = (k < keyCount) ? keys[k].time : (k == 0 ? 0f : 1f);
                    keyPositions[b * maxGradientKeys + k] = new Color(pos, 0, 0, 1);
                }
            }
            keyPositionsTexture.SetPixels(keyPositions);
            keyPositionsTexture.Apply();
            keyPositionsTexture.filterMode = FilterMode.Point;

            if (biomeTextureArray == null || biomeTextureArray.depth != totalSlices)
                biomeTextureArray = new Texture2DArray(biomeTextureSize, biomeTextureSize, totalSlices, TextureFormat.RGBA32, true);
            Color[] layerPixels = new Color[biomeTextureSize * biomeTextureSize];
            for (int b = 0; b < numBiomes; b++)
            {
                var biome = biomes[b];
                var perKey = biome.texturesPerGradientKey;
                Texture2D fallback = biome.terrainTexture;
                for (int k = 0; k < maxGradientKeys; k++)
                {
                    Texture2D src = (perKey != null && k < perKey.Count && perKey[k] != null) ? perKey[k] : fallback;
                    if (src != null && src.isReadable)
                    {
                        for (int y = 0; y < biomeTextureSize; y++)
                            for (int x = 0; x < biomeTextureSize; x++)
                                layerPixels[y * biomeTextureSize + x] = src.GetPixelBilinear((float)x / (biomeTextureSize - 1), (float)y / (biomeTextureSize - 1));
                    }
                    else
                    {
                        for (int i = 0; i < layerPixels.Length; i++)
                            layerPixels[i] = Color.white;
                    }
                    biomeTextureArray.SetPixels(layerPixels, b * maxGradientKeys + k);
                }
            }
            biomeTextureArray.Apply();
        }
        else
        {
            keyPositionsTexture = null;
            if (biomeTextureArray == null || biomeTextureArray.depth != biomes.Length)
                biomeTextureArray = new Texture2DArray(biomeTextureSize, biomeTextureSize, biomes.Length, TextureFormat.RGBA32, true);
            Color[] layerPixels = new Color[biomeTextureSize * biomeTextureSize];
            for (int b = 0; b < biomes.Length; b++)
            {
                Texture2D src = biomes[b].terrainTexture;
                if (src != null && src.isReadable)
                {
                    for (int y = 0; y < biomeTextureSize; y++)
                        for (int x = 0; x < biomeTextureSize; x++)
                            layerPixels[y * biomeTextureSize + x] = src.GetPixelBilinear((float)x / (biomeTextureSize - 1), (float)y / (biomeTextureSize - 1));
                }
                else
                {
                    for (int i = 0; i < layerPixels.Length; i++)
                        layerPixels[i] = Color.white;
                }
                biomeTextureArray.SetPixels(layerPixels, b);
            }
            biomeTextureArray.Apply();
        }
    }

    public float BiomePercentFromPoint(Vector3 pointOnUnitSphere)
    {
        float heightPercent = (pointOnUnitSphere.y + 1) / 2f;
        heightPercent += (biomeNoiseFilter.Evaluate(pointOnUnitSphere) - settings.biomeColourSettings.noiseOffset) * settings.biomeColourSettings.noiseStrength;
        float biomeIndex = 0;
        int numBiomes = settings.biomeColourSettings.biomes.Length;
        float blendRange = settings.biomeColourSettings.blendAmount / 2f + .001f;

        for (int i = 0; i < numBiomes; i++)
        {
            float dst = heightPercent - settings.biomeColourSettings.biomes[i].startHeight;
            float weight = Mathf.InverseLerp(-blendRange, blendRange, dst);
            biomeIndex *= (1 - weight);
            biomeIndex += i * weight;
        }

        return biomeIndex / Mathf.Max(1, numBiomes - 1);
    }

    /// <summary>
    /// Blend WEIGHT (0..1) of a single biome in the FINAL surface colour at this point — i.e. how much
    /// biome <paramref name="biomeIndex"/> contributes to the latitude/noise blend the surface is
    /// painted with. Mirrors the two-biome lerp in <see cref="GetColorAtPointWithElevation"/>: only the
    /// two biomes adjacent to this latitude contribute (weights 1-blend and blend); all others are 0.
    /// So the combined influence of every OTHER biome is (1 - this weight). Use to place/exclude foliage
    /// by which biome actually colours the ground (e.g. grass only where biome 0 dominates, fading out
    /// as desert/snow take over).
    /// </summary>
    public float GetBiomeWeight(Vector3 pointOnUnitSphere, int biomeIndex)
    {
        var biomes = settings != null && settings.biomeColourSettings != null
            ? settings.biomeColourSettings.biomes : null;
        if (biomes == null || biomes.Length == 0)
            return 0f;
        int numBiomes = biomes.Length;
        if (numBiomes == 1)
            return biomeIndex == 0 ? 1f : 0f;

        float biomePercent = Mathf.Clamp01(BiomePercentFromPoint(pointOnUnitSphere));
        float biomeIndexFloat = biomePercent * (numBiomes - 1);
        int lower = Mathf.Clamp(Mathf.FloorToInt(biomeIndexFloat), 0, numBiomes - 1);
        int upper = Mathf.Clamp(lower + 1, 0, numBiomes - 1);
        float blend = biomeIndexFloat - lower;

        if (lower == upper)                 // at/above the top biome -> it owns the colour
            return biomeIndex == lower ? 1f : 0f;
        if (biomeIndex == lower)
            return 1f - blend;
        if (biomeIndex == upper)
            return blend;
        return 0f;
    }

    public void UpdateColours()
    {
        Color[] colours = new Color[texture.width * texture.height];
        int colourIndex = 0;
        foreach (var biome in settings.biomeColourSettings.biomes)
        {
            for (int i = 0; i < textureResolution; i++)
            {
                float elevationNorm = i / (textureResolution - 1f);
                Color gradientCol = biome.gradient.Evaluate(elevationNorm);
                Color tintCol = biome.tint;
                Color baseCol = gradientCol * (1 - biome.tintPercent) + tintCol * biome.tintPercent;

                // Height-based tint: blend in band tints by elevation (use default bands if none set)
                if (settings.useHeightBands)
                {
                    var bands = settings.heightBands != null && settings.heightBands.Length > 0
                        ? settings.heightBands
                        : ColourSettings.GetDefaultHeightBands();
                    foreach (var band in bands)
                    {
                        if (elevationNorm >= band.elevationMin && elevationNorm <= band.elevationMax)
                        {
                            float strength = band.blendStrength;
                            baseCol = Color.Lerp(baseCol, baseCol * band.tint, strength);
                            break;
                        }
                    }
                }
                colours[colourIndex] = baseCol;
                colourIndex++;
            }
        }
        texture.SetPixels(colours);
        texture.Apply();
        settings.planetMaterial.SetTexture("_texture", texture);

        BuildBiomeLookupTexture();
        BuildBiomeTextureArray();
        bool useBiomeTextures = biomeTextureArray != null;
        Shader baseShader = Shader.Find("ProceduralPlanets/Planet Colour");
        Shader biomesShader = Shader.Find("ProceduralPlanets/Planet Colour With Biomes");
        if (useBiomeTextures && biomesShader != null)
        {
            settings.planetMaterial.shader = biomesShader;
            settings.planetMaterial.SetTexture("_BiomeTextures", biomeTextureArray);
            settings.planetMaterial.SetFloat("_BiomeCount", settings.biomeColourSettings.biomes.Length);
            settings.planetMaterial.SetVector("_TextureTiling", new Vector4(
                settings.biomeColourSettings.textureTiling.x,
                settings.biomeColourSettings.textureTiling.y, 0f, 0f));
            settings.planetMaterial.SetFloat("_UseGradientKeyTextures", keyPositionsTexture != null ? 1f : 0f);
            settings.planetMaterial.SetFloat("_MaxGradientKeys", maxGradientKeys);
            settings.planetMaterial.SetTexture("_KeyPositions", keyPositionsTexture != null ? keyPositionsTexture : Texture2D.whiteTexture);
            settings.planetMaterial.SetFloat("_BiomeBoundaryBlur", settings.biomeColourSettings.textureBoundaryBlur);
            settings.planetMaterial.SetFloat("_BiomeOverlapWidth", settings.biomeColourSettings.textureOverlapWidth);
        }
        else if (baseShader != null)
        {
            settings.planetMaterial.shader = baseShader;
        }

        if (biomeLookupTexture != null)
        {
            settings.planetMaterial.SetTexture("_BiomeLookup", biomeLookupTexture);
            settings.planetMaterial.SetFloat("_UseBiomeLookup", 1f);
        }
        else
        {
            settings.planetMaterial.SetTexture("_BiomeLookup", null);
            settings.planetMaterial.SetFloat("_UseBiomeLookup", 0f);
        }

        // Slope-based texturing: pass settings to shader (shader uses UV2.x = slope 0–1)
        settings.planetMaterial.SetColor("_SteepSlopeColor", settings.steepSlopeColor);
        settings.planetMaterial.SetFloat("_SteepSlopeAngle", settings.steepSlopeAngleStart);
        settings.planetMaterial.SetFloat("_SteepBlend", settings.useSlopeTint ? settings.steepBlend : 0f);
    }

    // Get the color at a specific point on the planet surface
    public Color GetColorAtPoint(Vector3 pointOnUnitSphere)
    {
        if (settings == null || settings.biomeColourSettings == null || settings.biomeColourSettings.biomes == null || settings.biomeColourSettings.biomes.Length == 0)
        {
            return Color.white; // Default if not initialized
        }

        float biomePercent = BiomePercentFromPoint(pointOnUnitSphere);
        int numBiomes = settings.biomeColourSettings.biomes.Length;
        
        // Convert biome percent to biome index
        float biomeIndexFloat = biomePercent * (numBiomes - 1);
        int biomeIndex = Mathf.Clamp(Mathf.FloorToInt(biomeIndexFloat), 0, numBiomes - 1);
        int nextBiomeIndex = Mathf.Clamp(biomeIndex + 1, 0, numBiomes - 1);
        float blendFactor = biomeIndexFloat - biomeIndex;

        // Get colors from the biomes
        ColourSettings.BiomeColourSettings.Biome biome = settings.biomeColourSettings.biomes[biomeIndex];
        ColourSettings.BiomeColourSettings.Biome nextBiome = settings.biomeColourSettings.biomes[nextBiomeIndex];
        
        // Sample from gradient (using middle of gradient as representative)
        Color biomeColor = biome.gradient.Evaluate(0.5f) * (1 - biome.tintPercent) + biome.tint * biome.tintPercent;
        Color nextBiomeColor = nextBiome.gradient.Evaluate(0.5f) * (1 - nextBiome.tintPercent) + nextBiome.tint * nextBiome.tintPercent;
        
        // Blend between biomes
        return Color.Lerp(biomeColor, nextBiomeColor, blendFactor);
    }

    /// <summary>
    /// Gets the color at a point using the same logic as the planet shader:
    /// biome from latitude+noise, elevation from normalized terrain height.
    /// Use this for foliage density - green areas = dense foliage.
    /// </summary>
    public Color GetColorAtPointWithElevation(Vector3 pointOnUnitSphere, float elevationNormalized)
    {
        if (settings == null || settings.biomeColourSettings == null || settings.biomeColourSettings.biomes == null || settings.biomeColourSettings.biomes.Length == 0)
            return Color.white;

        elevationNormalized = Mathf.Clamp01(elevationNormalized);

        float biomePercent = BiomePercentFromPoint(pointOnUnitSphere);
        int numBiomes = settings.biomeColourSettings.biomes.Length;

        float biomeIndexFloat = biomePercent * (numBiomes - 1);
        int biomeIndex = Mathf.Clamp(Mathf.FloorToInt(biomeIndexFloat), 0, numBiomes - 1);
        int nextBiomeIndex = Mathf.Clamp(biomeIndex + 1, 0, numBiomes - 1);
        float blendFactor = biomeIndexFloat - biomeIndex;

        var biome = settings.biomeColourSettings.biomes[biomeIndex];
        var nextBiome = settings.biomeColourSettings.biomes[nextBiomeIndex];

        Color biomeColor = biome.gradient.Evaluate(elevationNormalized) * (1 - biome.tintPercent) + biome.tint * biome.tintPercent;
        Color nextBiomeColor = nextBiome.gradient.Evaluate(elevationNormalized) * (1 - nextBiome.tintPercent) + nextBiome.tint * nextBiome.tintPercent;

        return Color.Lerp(biomeColor, nextBiomeColor, blendFactor);
    }

    /// <summary>
    /// Color from a specific biome's gradient sampled by normalized terrain elevation,
    /// WITHOUT blending in neighbouring biomes. Use to key foliage off "element 0" (the grass
    /// biome) directly, so grass isn't diluted by the desert/snow biomes at other latitudes.
    /// </summary>
    public Color GetBiomeGradientColorByElevation(int biomeIndex, float elevationNormalized)
    {
        var biomes = settings != null && settings.biomeColourSettings != null
            ? settings.biomeColourSettings.biomes : null;
        if (biomes == null || biomes.Length == 0)
            return Color.white;

        biomeIndex = Mathf.Clamp(biomeIndex, 0, biomes.Length - 1);
        var biome = biomes[biomeIndex];
        if (biome.gradient == null)
            return Color.white;

        elevationNormalized = Mathf.Clamp01(elevationNormalized);
        return biome.gradient.Evaluate(elevationNormalized) * (1 - biome.tintPercent) + biome.tint * biome.tintPercent;
    }

    /// <summary>
    /// Reproduces the EXACT colour the planet shader paints on the surface at this point:
    /// blended biome gradient (biome chosen by latitude+noise) sampled by normalized elevation,
    /// tinted, then height-band tinted — identical to what <see cref="UpdateColours"/> bakes into
    /// the surface texture. This is the single source of truth for "what colour is the ground here".
    /// (Slope/steep-rock tint is intentionally excluded; it is applied per-pixel by the shader only
    /// on steep faces, which foliage already rejects via its slope limit.)
    /// </summary>
    public Color GetSurfaceColorAtPoint(Vector3 pointOnUnitSphere, float elevationNormalized)
    {
        if (settings == null || settings.biomeColourSettings == null ||
            settings.biomeColourSettings.biomes == null || settings.biomeColourSettings.biomes.Length == 0)
            return Color.white;

        elevationNormalized = Mathf.Clamp01(elevationNormalized);
        Color baseCol = GetColorAtPointWithElevation(pointOnUnitSphere, elevationNormalized);

        if (settings.useHeightBands)
        {
            var bands = settings.heightBands != null && settings.heightBands.Length > 0
                ? settings.heightBands
                : ColourSettings.GetDefaultHeightBands();
            foreach (var band in bands)
            {
                if (elevationNormalized >= band.elevationMin && elevationNormalized <= band.elevationMax)
                {
                    baseCol = Color.Lerp(baseCol, baseCol * band.tint, band.blendStrength);
                    break;
                }
            }
        }

        return baseCol;
    }

    /// <summary>
    /// Classifies the surface the SAME WAY the shader paints it, but returns the discrete terrain
    /// "type" instead of a blended colour: the dominant biome (latitude+noise) and the dominant
    /// gradient colour KEY at this elevation. Each gradient key is a terrain band (e.g. shore,
    /// beach, grass, rock) — so this is the hook for "where the gradient is green, put grass; where
    /// it is brown, put rock", driven by exactly the data that colours the planet.
    /// </summary>
    public bool ClassifySurface(Vector3 pointOnUnitSphere, float elevationNormalized,
        out int biomeIndex, out int keyIndex, out Color keyColor)
    {
        biomeIndex = 0;
        keyIndex = 0;
        keyColor = Color.white;

        var biomes = settings != null && settings.biomeColourSettings != null
            ? settings.biomeColourSettings.biomes : null;
        if (biomes == null || biomes.Length == 0)
            return false;

        // Dominant biome: same biome-percent math the shader uses, rounded to the nearest biome.
        float biomePercent = BiomePercentFromPoint(pointOnUnitSphere);
        int numBiomes = biomes.Length;
        biomeIndex = Mathf.Clamp(Mathf.RoundToInt(biomePercent * (numBiomes - 1)), 0, numBiomes - 1);

        var biome = biomes[biomeIndex];
        var grad = biome.gradient;
        if (grad == null)
            return false;

        var keys = grad.colorKeys;
        if (keys == null || keys.Length == 0)
            return false;

        // Dominant gradient key: the colour key whose position is nearest this elevation.
        elevationNormalized = Mathf.Clamp01(elevationNormalized);
        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < keys.Length; i++)
        {
            float d = Mathf.Abs(keys[i].time - elevationNormalized);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }

        keyIndex = best;
        Color raw = keys[best].color;
        keyColor = raw * (1 - biome.tintPercent) + biome.tint * biome.tintPercent;
        return true;
    }

    /// <summary>
    /// Reads the gradient editor's colour-key SLIDER POSITIONS for a biome and returns the
    /// elevation span [minTime, maxTime] covered by the keys that read as green (the grass band).
    /// Drag those keys in the gradient editor and this band moves with them — the slider positions
    /// directly dictate where grass is allowed to spawn. Returns false if the biome has no green keys.
    /// </summary>
    public bool TryGetGreenKeyElevationBand(int biomeIndex, float minGreen, float minGreenOverRed,
        float minGreenOverBlue, out float minTime, out float maxTime)
    {
        minTime = 0f;
        maxTime = 1f;

        var biomes = settings != null && settings.biomeColourSettings != null
            ? settings.biomeColourSettings.biomes : null;
        if (biomes == null || biomes.Length == 0)
            return false;

        biomeIndex = Mathf.Clamp(biomeIndex, 0, biomes.Length - 1);
        var biome = biomes[biomeIndex];
        var grad = biome.gradient;
        if (grad == null)
            return false;

        var keys = grad.colorKeys;
        if (keys == null || keys.Length == 0)
            return false;

        int firstGreen = -1, lastGreen = -1;
        for (int i = 0; i < keys.Length; i++)
        {
            Color col = keys[i].color * (1 - biome.tintPercent) + biome.tint * biome.tintPercent;
            if (col.g >= minGreen && col.g - col.r >= minGreenOverRed && col.g - col.b >= minGreenOverBlue)
            {
                if (firstGreen < 0) firstGreen = i;
                lastGreen = i;
            }
        }

        if (firstGreen < 0)
            return false;

        // Extend to the NEAREST-KEY boundaries: a point belongs to a key until halfway to the next
        // key, so the green region runs from the midpoint below the first green key to the midpoint
        // above the last green key. This matches where the surface actually reads green (incl. the
        // dark-green transition up toward the rock band), instead of stopping at the exact slider.
        minTime = (firstGreen == 0)
            ? 0f
            : 0.5f * (keys[firstGreen - 1].time + keys[firstGreen].time);
        maxTime = (lastGreen >= keys.Length - 1)
            ? 1f
            : 0.5f * (keys[lastGreen].time + keys[lastGreen + 1].time);
        return true;
    }

    /// <summary>
    /// The EXACT final base colour the planet shader paints at this point. Instead of re-deriving
    /// the gradient, this samples the SAME baked colour table (`texture`) at the SAME coordinates the
    /// shader uses: elevation on X, and biome-percent on Y read from the SAME baked biome lookup with
    /// the SAME boundary blur. So "is this green?" is asked of the colour the player actually sees
    /// (minus per-pixel detail/lighting, which don't change hue). This is the source of truth for
    /// placing grass exactly where the surface is painted green.
    /// </summary>
    public Color GetFinalSurfaceColor(Vector3 pointOnUnitSphere, float elevationNormalized)
    {
        if (texture == null || settings == null || settings.biomeColourSettings == null)
            return GetSurfaceColorAtPoint(pointOnUnitSphere, elevationNormalized);

        try
        {
        float biomePercent;
        if (biomeLookupTexture != null)
        {
            Vector3 dir = pointOnUnitSphere.normalized;
            float u = Mathf.Atan2(dir.x, dir.z) / (2f * Mathf.PI) + 0.5f;
            float v = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) / Mathf.PI + 0.5f;
            float r = settings.biomeColourSettings.textureBoundaryBlur;
            if (r <= 0f)
            {
                biomePercent = biomeLookupTexture.GetPixelBilinear(u, v).r;
            }
            else
            {
                // Same 9-tap box blur the shader applies to soften biome boundaries.
                float s = biomeLookupTexture.GetPixelBilinear(u, v).r;
                s += biomeLookupTexture.GetPixelBilinear(u + r, v).r;
                s += biomeLookupTexture.GetPixelBilinear(u - r, v).r;
                s += biomeLookupTexture.GetPixelBilinear(u, v + r).r;
                s += biomeLookupTexture.GetPixelBilinear(u, v - r).r;
                s += biomeLookupTexture.GetPixelBilinear(u + r, v + r).r;
                s += biomeLookupTexture.GetPixelBilinear(u - r, v + r).r;
                s += biomeLookupTexture.GetPixelBilinear(u + r, v - r).r;
                s += biomeLookupTexture.GetPixelBilinear(u - r, v - r).r;
                biomePercent = s / 9f;
            }
            biomePercent = Mathf.Clamp01(biomePercent);
        }
        else
        {
            biomePercent = Mathf.Clamp01(BiomePercentFromPoint(pointOnUnitSphere));
        }

        // Same lookup the shader does: SAMPLE(_texture, (elevationNorm, biomePercent)).
        return texture.GetPixelBilinear(Mathf.Clamp01(elevationNormalized), biomePercent);
        }
        catch (System.Exception)
        {
            // Texture not readable / not ready — fall back to the gradient math so scatter never dies.
            return GetSurfaceColorAtPoint(pointOnUnitSphere, elevationNormalized);
        }
    }

    /// <summary>True if a gradient key colour reads as a green "shade" (clearly green-dominant).</summary>
    public static bool IsGreenKeyColor(Color k)
    {
        // Green must clearly DOMINATE red — not merely edge past it. Yellow/tan have red≈green
        // (g - r ≈ 0), so the old loose "g - r > 0.05" test leaked into the yellow→green gradient
        // transition while the surface still reads yellow, putting grass on the yellow band.
        // Requiring g > r * 1.2 (multiplicative dominance) makes yellow fail (r≈g) while every
        // genuinely green key — where green leads red by a wide margin — still passes.
        return k.g > k.r * 1.2f && k.g - k.r > 0.06f && k.g - k.b > 0.05f && k.g > 0.25f;
    }

    /// <summary>
    /// THE authoritative grass test: take the EXACT colour the shader paints at this point
    /// (<see cref="GetFinalSurfaceColor"/> — the baked gradient table sampled by elevation and the
    /// blurred latitude→biome lookup, identical to the shader) and ask, directly, "is this green?"
    /// Green = the green channel is clearly the largest, which is precisely what the eye reads as
    /// green on the surface. No biome assumptions, no key snapping — so grass lands on every painted
    /// green pixel regardless of which biome/elevation produced it, and stays off tan/brown/grey/blue.
    /// </summary>
    public bool IsPaintedSurfaceGreen(Vector3 pointOnUnitSphere, float elevationNormalized)
    {
        Color c = GetFinalSurfaceColor(pointOnUnitSphere, elevationNormalized);
        return c.g - c.r > 0.015f && c.g - c.b > 0.015f;
    }

    /// <summary>
    /// Snaps the EXACT final painted colour at this point to the nearest gradient KEY shade (across
    /// all biomes) and reports whether that shade is green. This is "assign grass to each solid shade
    /// of green": beach-yellow, tan, brown, grey and blue are distinct non-green shades and excluded
    /// automatically, while every green shade (incl. the dark-green/olive that leans green) qualifies.
    /// </summary>
    public bool IsFinalColorGreen(Vector3 pointOnUnitSphere, float elevationNormalized)
    {
        var biomes = settings != null && settings.biomeColourSettings != null
            ? settings.biomeColourSettings.biomes : null;
        if (biomes == null || biomes.Length == 0)
            return false;

        Color c = GetFinalSurfaceColor(pointOnUnitSphere, elevationNormalized);

        float bestDist = float.MaxValue;
        bool nearestGreen = false;
        for (int b = 0; b < biomes.Length; b++)
        {
            var grad = biomes[b].gradient;
            var keys = grad != null ? grad.colorKeys : null;
            if (keys == null) continue;
            float tintP = biomes[b].tintPercent;
            Color tint = biomes[b].tint;
            for (int i = 0; i < keys.Length; i++)
            {
                Color k = keys[i].color * (1 - tintP) + tint * tintP;
                float dr = c.r - k.r, dg = c.g - k.g, db = c.b - k.b;
                float d = dr * dr + dg * dg + db * db;
                if (d < bestDist)
                {
                    bestDist = d;
                    nearestGreen = IsGreenKeyColor(k);
                }
            }
        }
        return nearestGreen;
    }

    /// <summary>
    /// Crisp grass test that matches the SOLID-BAND view. Picks the DOMINANT biome (latitude+noise,
    /// rounded — no inter-biome blend) and evaluates THAT biome's gradient at this elevation, which is
    /// exactly the band colour the gradient editor shows. Grass goes on every green band.
    ///
    /// Why this fixes "green ground without grass": the old test judged the BLENDED colour and snapped
    /// it to the nearest key across ALL biomes, so a green band's interior averaged toward the
    /// neighbouring desert/snow key and got rejected — coverage tracked where biomes overlapped instead
    /// of where the ground is green. Reading the dominant biome's own band removes that averaging, so a
    /// green band reads green across its whole latitude span.
    /// </summary>
    public bool IsDominantSurfaceGreen(Vector3 pointOnUnitSphere, float elevationNormalized)
    {
        var biomes = settings != null && settings.biomeColourSettings != null
            ? settings.biomeColourSettings.biomes : null;
        if (biomes == null || biomes.Length == 0)
            return false;

        float biomePercent = BiomePercentFromPoint(pointOnUnitSphere);
        int idx = Mathf.Clamp(Mathf.RoundToInt(biomePercent * (biomes.Length - 1)), 0, biomes.Length - 1);
        var biome = biomes[idx];
        if (biome.gradient == null)
            return false;

        Color col = biome.gradient.Evaluate(Mathf.Clamp01(elevationNormalized));
        col = col * (1 - biome.tintPercent) + biome.tint * biome.tintPercent;
        return IsGreenKeyColor(col);
    }

    /// <summary>
    /// Grass rule driven by ONE biome's gradient only (default element 0, the grass biome): grass
    /// wherever that biome's gradient — evaluated at the point's elevation — reads green. Latitude and
    /// biome blending are ignored entirely, so the result is a single, predictable green elevation band
    /// that wraps the whole planet wherever the terrain height lands in biome 0's green keys.
    /// </summary>
    public bool IsBiomeGradientGreen(int biomeIndex, float elevationNormalized)
    {
        var biomes = settings != null && settings.biomeColourSettings != null
            ? settings.biomeColourSettings.biomes : null;
        if (biomes == null || biomes.Length == 0)
            return false;

        Color col = GetBiomeGradientColorByElevation(biomeIndex, elevationNormalized);
        return IsGreenKeyColor(col);
    }

    /// <summary>
    /// Continuous "how green" score in 0..1 for the same rule as <see cref="IsBiomeGradientGreen"/>:
    /// strongest where the gradient colour is most green-dominant (the core green keys) and fading to
    /// 0 as the colour blends toward the beach/rock boundary. Use to vary grass DENSITY — dense in the
    /// greenest ground, gradually sparse toward the edge. Returns 0 where the colour isn't green at all.
    /// </summary>
    public float GetBiomeGradientGreenness(int biomeIndex, float elevationNormalized)
    {
        var biomes = settings != null && settings.biomeColourSettings != null
            ? settings.biomeColourSettings.biomes : null;
        if (biomes == null || biomes.Length == 0)
            return 0f;

        Color c = GetBiomeGradientColorByElevation(biomeIndex, elevationNormalized);
        // Green dominance: how far green leads the stronger of red/blue. ~0.26 at the core green keys,
        // ~0.02 at the beach-yellow edge, <=0 on browns. Normalised so the core greens reach 1.
        float dominance = c.g - Mathf.Max(c.r, c.b);
        return Mathf.Clamp01(dominance / 0.26f);
    }

    /// <summary>Colour of the dominant gradient key at this point — the crisp "terrain band" colour.</summary>
    public Color GetDominantKeyColorAtPoint(Vector3 pointOnUnitSphere, float elevationNormalized)
    {
        if (ClassifySurface(pointOnUnitSphere, elevationNormalized, out _, out _, out Color c))
            return c;
        return GetSurfaceColorAtPoint(pointOnUnitSphere, elevationNormalized);
    }

    // Get the color from biome 0's gradient at a specific point
    // This uses the elevation/height to sample the gradient
    public Color GetBiome0GradientColorAtPoint(Vector3 pointOnUnitSphere)
    {
        if (settings == null || settings.biomeColourSettings == null || settings.biomeColourSettings.biomes == null || settings.biomeColourSettings.biomes.Length == 0)
        {
            return Color.white; // Default if not initialized
        }

        // Calculate height percent (same logic as BiomePercentFromPoint)
        float heightPercent = (pointOnUnitSphere.y + 1) / 2f;
        heightPercent += (biomeNoiseFilter.Evaluate(pointOnUnitSphere) - settings.biomeColourSettings.noiseOffset) * settings.biomeColourSettings.noiseStrength;
        
        // Clamp to valid range
        heightPercent = Mathf.Clamp01(heightPercent);
        
        // Get biome 0
        ColourSettings.BiomeColourSettings.Biome biome0 = settings.biomeColourSettings.biomes[0];
        
        // Sample the gradient at this height percent
        Color gradientColor = biome0.gradient.Evaluate(heightPercent);
        
        // Apply tint if needed
        Color finalColor = gradientColor * (1 - biome0.tintPercent) + biome0.tint * biome0.tintPercent;
        
        return finalColor;
    }
}
