using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Planet : MonoBehaviour
{
    /// <summary>Fired when the planet has finished generating at runtime. Subscribe to delay player/asset spawn until ready.</summary>
    public static event Action OnPlanetReady;

    /// <summary>True after GeneratePlanet has completed at runtime.</summary>
    public bool IsGenerated { get; private set; }

    [Range(2, 256)]
    public int resolution = 50;
    public bool autoUpdate = true;
    public enum FaceRenderMask { All, Top, Bottom, Left, Right, Front, Back };
    public FaceRenderMask faceRenderMask;

    public ShapeSettings shapeSettings;
    public ColourSettings colourSettings;

    [HideInInspector]
    public bool shapeSettingsFoldout;
    [HideInInspector]
    public bool colourSettingsFoldout;

    ShapeGenerator shapeGenerator = new ShapeGenerator();
    ColourGenerator colourGenerator = new ColourGenerator();

    [SerializeField, HideInInspector]
    MeshFilter[] meshFilters;
    TerrainFace[] terrainFaces;

    void Initialize()
    {
        shapeGenerator.UpdateSettings(shapeSettings);
        colourGenerator.UpdateSettings(colourSettings);

        if (meshFilters == null || meshFilters.Length == 0)
        {
            meshFilters = new MeshFilter[6];
        }
        terrainFaces = new TerrainFace[6];

        Vector3[] directions = { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };

        for (int i = 0; i < 6; i++)
        {
            if (meshFilters[i] == null)
            {
                GameObject meshObj = new GameObject("mesh");
                meshObj.transform.parent = transform;

                meshObj.AddComponent<MeshRenderer>();
                meshFilters[i] = meshObj.AddComponent<MeshFilter>();
                meshObj.AddComponent<MeshCollider>(); // Add Collider
                meshFilters[i].sharedMesh = new Mesh();
            }
            var renderer = meshFilters[i].GetComponent<MeshRenderer>();
            if (renderer != null)
                renderer.sharedMaterial = colourSettings.planetMaterial;

            terrainFaces[i] = new TerrainFace(shapeGenerator, meshFilters[i].sharedMesh, resolution, directions[i]);
            bool renderFace = faceRenderMask == FaceRenderMask.All || (int)faceRenderMask - 1 == i;
            meshFilters[i].gameObject.SetActive(renderFace);
        }
    }

    public void GeneratePlanet()
    {
        // Bake pads before mesh build so TerrainFace vertices + analytic APIs share the same height field.
        PlanetBuildingPads.BakeFromScene(this);
        Initialize();
        GenerateMesh();
        GenerateColours();
        PlanetBuildingPads.NotifyFoliageConsumers();
    }

    IEnumerator Start()
    {
        if (shapeSettings != null && colourSettings != null)
        {
            GeneratePlanet();
            yield return null;
            yield return null; // Allow mesh colliders and physics to update
            IsGenerated = true;
            OnPlanetReady?.Invoke();
        }
        else
        {
            IsGenerated = true;
            OnPlanetReady?.Invoke();
        }
    }

    public void OnShapeSettingsUpdated()
    {
        if (autoUpdate)
        {
            Initialize();
            GenerateMesh();
        }
    }

    public void OnColourSettingsUpdated()
    {
        if (autoUpdate)
        {
            Initialize();
            GenerateColours();
        }
    }
    
    private void OnValidate()
    {
         // Ensure colliders exist if we are in editor info
         if (meshFilters != null)
         {
             foreach(var mf in meshFilters)
             {
                 if (mf != null && mf.GetComponent<MeshCollider>() == null)
                 {
                     mf.gameObject.AddComponent<MeshCollider>();
                 }
             }
         }
    }

    void GenerateMesh()
    {
        for (int i = 0; i < 6; i++)
        {
            if (meshFilters[i].gameObject.activeSelf)
            {
                terrainFaces[i].ConstructMesh();
                // Refresh Collider
                MeshCollider collider = meshFilters[i].GetComponent<MeshCollider>();
                if (collider != null)
                {
                   collider.sharedMesh = null;
                   collider.sharedMesh = meshFilters[i].sharedMesh;
                }
            }
        }

        colourGenerator.UpdateElevation(shapeGenerator.elevationMinMax);
    }

    void GenerateColours()
    {
        colourGenerator.UpdateColours();
        for (int i = 0; i < 6; i++)
        {
            if (meshFilters[i].gameObject.activeSelf)
            {
                terrainFaces[i].UpdateUVs(colourGenerator);
            }
        }
    }

    // Get the color at a world position on the planet surface
    public Color GetColorAtPosition(Vector3 worldPosition)
    {
        if (colourSettings == null)
        {
            return Color.white;
        }

        Vector3 centerToPoint = worldPosition - transform.position;
        Vector3 pointOnUnitSphere = centerToPoint.normalized;
        return colourGenerator.GetColorAtPoint(pointOnUnitSphere);
    }

    // Check if a position is green (for foliage spawning)
    public bool IsPositionGreen(Vector3 worldPosition, float greenThreshold = 0.4f)
    {
        Color color = GetColorAtPosition(worldPosition);
        // Check if green component is dominant (green > red and green > blue, and green > threshold)
        return color.g > color.r && color.g > color.b && color.g > greenThreshold;
    }

    // Get the greenness factor (0-1) from biome 0's gradient at a position
    // Returns how "green" the position is, where 1.0 = most green, 0.0 = least green
    // This directly uses the green component from biome 0's gradient as the density factor
    public float GetBiome0GreennessFactor(Vector3 worldPosition)
    {
        if (colourSettings == null)
        {
            return 0f;
        }

        Vector3 centerToPoint = worldPosition - transform.position;
        Vector3 pointOnUnitSphere = centerToPoint.normalized;
        Color biome0Color = colourGenerator.GetBiome0GradientColorAtPoint(pointOnUnitSphere);
        
        // Use the green component directly as the greenness factor
        // The green component (0-1) represents how green the gradient is at this point
        // This creates natural density: most green = 1.0 (always spawn), less green = lower value (less likely to spawn)
        float greenness = biome0Color.g;
        
        // Boost greenness if green is the dominant color (green > red and green > blue)
        if (biome0Color.g > biome0Color.r && biome0Color.g > biome0Color.b)
        {
            // Green is dominant - use the green value as-is (already 0-1)
            // Optionally boost it slightly to favor clearly green areas
            greenness = Mathf.Min(1f, greenness * 1.1f);
        }
        else
        {
            // Green is not dominant - reduce greenness significantly
            greenness *= 0.3f;
        }
        
        return Mathf.Clamp01(greenness);
    }

    /// <summary>Biome color at a world position (matches shader). Use for color-based asset matching.</summary>
    public Color GetBiomeColorAtPosition(Vector3 worldPosition)
    {
        if (colourSettings == null) return Color.white;

        Vector3 centerToPoint = worldPosition - transform.position;
        Vector3 pointOnUnitSphere = centerToPoint.normalized;
        float elevation = centerToPoint.magnitude;
        float elevationNorm = Mathf.InverseLerp(shapeGenerator.elevationMinMax.Min, shapeGenerator.elevationMinMax.Max, elevation);
        return colourGenerator.GetColorAtPointWithElevation(pointOnUnitSphere, elevationNorm);
    }

    /// <summary>
    /// Colour from a biome's gradient (default biome 0 = grass) sampled by this position's
    /// normalized elevation, without blending neighbouring biomes. Foliage should key off this
    /// so grass follows the grass biome's gradient everywhere, not just where biomes don't blend.
    /// </summary>
    public Color GetBiomeGradientColorAtPosition(Vector3 worldPosition, int biomeIndex = 0)
    {
        if (colourSettings == null || colourGenerator == null)
            return Color.white;
        float elevNorm = GetNormalizedElevationAtPosition(worldPosition);
        return colourGenerator.GetBiomeGradientColorByElevation(biomeIndex, elevNorm);
    }

    /// <summary>
    /// The EXACT colour the planet shader paints on the ground at this world position
    /// (blended biome gradient by elevation + tint + height bands). This is the single source of
    /// truth for surface colour — foliage/biome logic should key off this so "grass grows where the
    /// ground is painted green" holds everywhere, with no mismatch against what the player sees.
    /// </summary>
    public Color GetSurfaceColorAtPosition(Vector3 worldPosition)
    {
        if (colourSettings == null || colourGenerator == null) return Color.white;

        Vector3 centerToPoint = worldPosition - transform.position;
        Vector3 pointOnUnitSphere = centerToPoint.normalized;
        float elevationNorm = GetNormalizedElevationAtPosition(worldPosition);
        // Sample the SAME baked colour table + blurred biome lookup the shader uses, so this returns
        // the actual colour painted on screen (grass then follows the final green exactly).
        return colourGenerator.GetFinalSurfaceColor(pointOnUnitSphere, elevationNorm);
    }

    /// <summary>
    /// Classifies the terrain at a world position using the EXACT same math that paints the
    /// surface: returns the dominant biome and the dominant gradient colour KEY (a terrain band:
    /// shore/beach/grass/rock/...). Map keys to prefabs to place vegetation where the planet's own
    /// colour data says it belongs (green keys -> grass, brown keys -> rock, etc.).
    /// </summary>
    public bool ClassifySurfaceAtPosition(Vector3 worldPosition, out int biomeIndex, out int keyIndex, out Color keyColor)
    {
        biomeIndex = 0; keyIndex = 0; keyColor = Color.white;
        if (colourSettings == null || colourGenerator == null) return false;
        Vector3 p = (worldPosition - transform.position).normalized;
        float elevNorm = GetNormalizedElevationAtPosition(worldPosition);
        return colourGenerator.ClassifySurface(p, elevNorm, out biomeIndex, out keyIndex, out keyColor);
    }

    /// <summary>
    /// The elevation band [min,max] (0..1) spanned by the GREEN colour keys of a biome's gradient —
    /// i.e. the slider positions of the grass keys in the gradient editor. Use this to drive where
    /// grass is allowed to spawn directly from the gradient sliders.
    /// </summary>
    public bool TryGetGrassElevationBand(int biomeIndex, float minGreen, float minGreenOverRed,
        float minGreenOverBlue, out float minElevation, out float maxElevation)
    {
        minElevation = 0f; maxElevation = 1f;
        if (colourSettings == null || colourGenerator == null) return false;
        return colourGenerator.TryGetGreenKeyElevationBand(biomeIndex, minGreen, minGreenOverRed,
            minGreenOverBlue, out minElevation, out maxElevation);
    }

    /// <summary>
    /// True if the gradient COLOUR KEY governing this point is one of the green keys. Authoritative
    /// "should grass spawn here" test: it follows the green keys in the biome gradients directly.
    /// </summary>
    public bool IsSurfaceGreenAtPosition(Vector3 worldPosition)
    {
        if (colourSettings == null || colourGenerator == null) return false;
        Vector3 p = (worldPosition - transform.position).normalized;
        float elevNorm = GetNormalizedElevationAtPosition(worldPosition);
        // Authoritative rule (user's intent): classify this point to the gradient COLOUR KEY the
        // surface uses here — the dominant biome (by latitude) and the key whose elevation zone
        // contains this point — then grass ONLY if that key is one of the green gradient keys.
        // This is exactly "the green keys in the biome gradients define where grass spawns": beach,
        // brown and snow keys (and the desert/snow biomes that have no green key) are excluded by
        // construction, with no RGB threshold tuning.
        if (colourGenerator.ClassifySurface(p, elevNorm, out _, out _, out Color keyColor))
            return ColourGenerator.IsGreenKeyColor(keyColor);
        return false;
    }

    /// <summary>
    /// Green test driven by a SINGLE biome's gradient only (default element 0): grass wherever that
    /// biome's gradient reads green at the point's elevation, regardless of latitude/biome blend.
    /// </summary>
    public bool IsSurfaceGreenAtPosition(Vector3 worldPosition, int biomeIndex)
    {
        if (colourSettings == null || colourGenerator == null) return false;
        float elevNorm = GetNormalizedElevationAtPosition(worldPosition);
        return colourGenerator.IsBiomeGradientGreen(biomeIndex, elevNorm);
    }

    /// <summary>
    /// Continuous greenness (0..1) of biome <paramref name="biomeIndex"/>'s gradient at this position:
    /// 1 in the greenest ground, fading to 0 toward the green-band boundary. Drives grass density.
    /// </summary>
    public float GetSurfaceGreennessAtPosition(Vector3 worldPosition, int biomeIndex)
    {
        if (colourSettings == null || colourGenerator == null) return 0f;
        float elevNorm = GetNormalizedElevationAtPosition(worldPosition);
        return colourGenerator.GetBiomeGradientGreenness(biomeIndex, elevNorm);
    }

    /// <summary>Crisp colour of the dominant gradient key (terrain band) at a world position.</summary>
    public Color GetSurfaceKeyColorAtPosition(Vector3 worldPosition)
    {
        if (colourSettings == null || colourGenerator == null) return Color.white;
        Vector3 p = (worldPosition - transform.position).normalized;
        float elevNorm = GetNormalizedElevationAtPosition(worldPosition);
        return colourGenerator.GetDominantKeyColorAtPoint(p, elevNorm);
    }

    /// <summary>
    /// Classifies the LAND surface under a world position into a coarse footstep category, reusing the
    /// same authoritative gradient-key colour the shader paints (<see cref="GetSurfaceKeyColorAtPosition"/>)
    /// and <see cref="ColourGenerator.IsGreenKeyColor"/>. Water is NOT decided here (the caller knows the
    /// player's wade/swim state); this only distinguishes Grass / Sand / Snow / Rock. Cheap enough to call
    /// once per footstep. Returns <see cref="FootstepSurfaceKind.Default"/> if the planet isn't coloured yet.
    /// </summary>
    public FootstepSurfaceKind GetFootstepSurface(Vector3 worldPosition)
    {
        if (colourSettings == null || colourGenerator == null)
            return FootstepSurfaceKind.Default;

        Color k = GetSurfaceKeyColorAtPosition(worldPosition);

        // Green keys are the authoritative "grass" shades (excludes tan/brown by construction).
        if (ColourGenerator.IsGreenKeyColor(k))
            return FootstepSurfaceKind.Grass;

        Color.RGBToHSV(k, out float hue, out float sat, out float val);

        // Whitish + bright + low saturation = snow.
        if (val > 0.72f && sat < 0.20f)
            return FootstepSurfaceKind.Snow;

        // Warm yellow/tan hue (~30-65 deg => 0.083-0.18) with some saturation and brightness = sand/beach.
        if (hue >= 0.07f && hue <= 0.19f && sat > 0.22f && val > 0.45f)
            return FootstepSurfaceKind.Sand;

        // Everything else (grey/brown/dark, high-slope rock) = rock/dirt.
        return FootstepSurfaceKind.Rock;
    }

    /// <summary>
    /// Biome blend value at a position: 0 = first biome (grass), increasing toward 1 for later
    /// biomes (desert/snow). Latitude+noise based — matches how the surface picks its biome.
    /// </summary>
    public float GetBiomePercentAtPosition(Vector3 worldPosition)
    {
        if (colourGenerator == null)
            return 0f;
        Vector3 p = (worldPosition - transform.position).normalized;
        return Mathf.Clamp01(colourGenerator.BiomePercentFromPoint(p));
    }

    /// <summary>
    /// Blend weight (0..1) of biome <paramref name="biomeIndex"/> in the final surface colour at this
    /// world position. 1 = this biome alone colours the ground here; 0 = it has no influence. The
    /// combined influence of ALL OTHER biomes is therefore (1 - this weight). Foliage uses this to keep
    /// a rule to its own biome and exclude it where other biomes (e.g. desert/snow) take over the colour.
    /// </summary>
    public float GetBiomeWeightAtPosition(Vector3 worldPosition, int biomeIndex)
    {
        if (colourSettings == null || colourGenerator == null) return 0f;
        Vector3 p = (worldPosition - transform.position).normalized;
        return colourGenerator.GetBiomeWeight(p, biomeIndex);
    }

    /// <summary>Base planet radius in world space (smooth sphere before terrain). Terrain above this is "elevated".</summary>
    public float GetBaseRadiusWorld()
    {
        float scale = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
        return (shapeSettings != null ? shapeSettings.planetRadius : 100f) * scale;
    }

    /// <summary>Max world-space distance from center to terrain (for spawn above surface).</summary>
    public float GetMaxSurfaceRadiusWorld()
    {
        float scale = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
        return shapeGenerator.elevationMinMax.Max * scale;
    }

    /// <summary>
    /// Same method foliage/assets use: ray from outside planet toward center, return surface point.
    /// directionFromCenter should be a unit vector (e.g. Random.onUnitSphere). Returns true if we hit the planet.
    /// </summary>
    public bool TryGetSurfacePoint(Vector3 directionFromCenter, LayerMask groundMask, float rayStartMargin, out Vector3 surfacePoint, out Vector3 surfaceUp)
    {
        surfacePoint = transform.position;
        surfaceUp = directionFromCenter.normalized;

        float radius = (shapeSettings != null) ? shapeSettings.planetRadius : 100f;
        float scale = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
        float worldRadius = radius * scale;
        Vector3 center = transform.position;
        Vector3 dir = directionFromCenter.normalized;
        Vector3 origin = center + dir * (worldRadius + rayStartMargin);
        float maxDist = rayStartMargin * 3f;

        RaycastHit[] hits = Physics.RaycastAll(origin, -dir, maxDist, groundMask, QueryTriggerInteraction.Ignore);
        float closest = float.MaxValue;

        for (int i = 0; i < hits.Length; i++)
        {
            Transform root = hits[i].collider.transform;
            while (root.parent != null) root = root.parent;
            if (root != transform) continue;

            float d = (hits[i].point - origin).magnitude;
            if (d < closest)
            {
                closest = d;
                surfacePoint = hits[i].point;
                surfaceUp = (surfacePoint - center).normalized;
            }
        }

        if (closest < float.MaxValue) return true;

        if (hits.Length > 0)
        {
            surfacePoint = hits[0].point;
            surfaceUp = (surfacePoint - center).normalized;
            return true;
        }

        return false;
    }

    /// <summary>Uniform world scale of the planet (max lossy scale axis), matching how every other lookup
    /// here converts between local mesh units and world space. Guarded to never be ~0.</summary>
    float ScaleFactor()
    {
        float scale = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
        return (scale < 1e-6f) ? 1f : scale;
    }

    /// <summary>
    /// ANALYTIC surface radius (world-space distance from planet center to the terrain surface) along a
    /// direction from the center. This is the deterministic equivalent of raycasting the planet mesh inward:
    /// the surface is <c>shapeGenerator.CalculatePointOnPlanet(dir)</c>, whose magnitude is the local radius,
    /// scaled into world space. No physics query, no per-attempt allocation, and (unlike CalculatePointOnPlanet)
    /// no mutation of elevationMinMax. Because the mesh collider is built from the SAME function (at
    /// resolution-50 tessellation), this matches the collider surface to within the tessellation error — which
    /// is well under a typical foliage surfaceOffset, so foliage seats correctly without floating/sinking.
    /// Rotation is intentionally ignored to stay consistent with the colour/elevation lookups in this class,
    /// which all treat the world direction from center as the unit-sphere sample point.
    /// </summary>
    public float GetSurfaceRadiusWorld(Vector3 directionFromCenter)
    {
        Vector3 dir = directionFromCenter.normalized;
        if (dir.sqrMagnitude < 1e-12f)
            return GetBaseRadiusWorld();
        if (shapeGenerator == null)
            return GetBaseRadiusWorld();
        float localRadius = shapeGenerator.CalculateUnscaledElevation(dir);
        return localRadius * ScaleFactor();
    }

    /// <summary>Analytic world-space surface point along a direction from the planet center (see
    /// <see cref="GetSurfaceRadiusWorld"/>). Drop-in replacement for a raycast hit point in foliage placement.</summary>
    public Vector3 GetSurfacePointWorld(Vector3 directionFromCenter)
    {
        Vector3 dir = directionFromCenter.normalized;
        if (dir.sqrMagnitude < 1e-12f)
            dir = Vector3.up;
        return transform.position + dir * GetSurfaceRadiusWorld(dir);
    }

    /// <summary>
    /// Analytic outward surface normal at the surface point along <paramref name="directionFromCenter"/>,
    /// computed from the gradient of the radial heightfield via two small tangential finite differences.
    /// Replaces the raycast hit normal: foliage uses it for slope rejection and surface orientation. It is the
    /// "true" continuous surface normal, so it can differ slightly from the coarse tessellated mesh normal — an
    /// acceptable (and arguably more accurate) difference for foliage. Falls back to the radial (straight-up)
    /// normal if the planet isn't generated yet.
    /// </summary>
    public Vector3 GetSurfaceNormalWorld(Vector3 directionFromCenter)
    {
        Vector3 dir = directionFromCenter.normalized;
        if (dir.sqrMagnitude < 1e-12f || shapeGenerator == null)
            return (dir.sqrMagnitude < 1e-12f) ? Vector3.up : dir;

        // Build an arbitrary tangent basis around the radial direction.
        Vector3 t1 = Vector3.Cross(dir, Vector3.up);
        if (t1.sqrMagnitude < 1e-6f)
            t1 = Vector3.Cross(dir, Vector3.right);
        t1.Normalize();
        Vector3 t2 = Vector3.Cross(dir, t1); // already unit length

        // Angular step roughly matching the mesh's per-quad angular size (resolution ~50 -> ~2 deg), so the
        // analytic normal tracks terrain slope at a similar scale to the old mesh-hit normal.
        const float eps = 0.02f;
        Vector3 center = transform.position;
        Vector3 p0 = center + dir * GetSurfaceRadiusWorld(dir);
        Vector3 da = (dir + t1 * eps).normalized;
        Vector3 db = (dir + t2 * eps).normalized;
        Vector3 pa = center + da * GetSurfaceRadiusWorld(da);
        Vector3 pb = center + db * GetSurfaceRadiusWorld(db);

        Vector3 n = Vector3.Cross(pa - p0, pb - p0);
        if (n.sqrMagnitude < 1e-12f)
            return dir;
        n.Normalize();
        if (Vector3.Dot(n, dir) < 0f)
            n = -n; // ensure it points outward (away from center)
        return n;
    }

    /// <summary>
    /// LOCAL (unscaled) elevation min/max spanned by the generated surface — the same range
    /// <see cref="GetNormalizedElevationAtPosition"/> normalizes against. Foliage's Burst sampler needs
    /// both ends to reproduce normalized elevation off the main thread. Returns false until the mesh has
    /// generated (elevationMinMax is populated by GenerateMesh).
    /// </summary>
    public bool TryGetLocalElevationMinMax(out float min, out float max)
    {
        min = 0f;
        max = 0f;
        if (shapeGenerator == null || shapeGenerator.elevationMinMax == null)
            return false;
        min = shapeGenerator.elevationMinMax.Min;
        max = shapeGenerator.elevationMinMax.Max;
        return true;
    }

    /// <summary>Normalized elevation (0=lowest, 1=highest) at a world position. For elevation-based spawn filtering.</summary>
    public float GetNormalizedElevationAtPosition(Vector3 worldPosition)
    {
        // elevationMinMax is in LOCAL (unscaled) mesh units, so convert the world-space distance
        // back into local units before normalizing — otherwise a scaled planet transform skews the
        // result and the gradient/elevation lookups disagree with what the shader actually paints.
        float scale = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
        if (scale < 1e-6f) scale = 1f;
        float elevation = (worldPosition - transform.position).magnitude / scale;
        // elevationMinMax is null until the mesh is generated (e.g. probing in edit mode before
        // GeneratePlanet ran). Guard so callers get 0 instead of a NullReferenceException.
        if (shapeGenerator == null || shapeGenerator.elevationMinMax == null)
            return 0f;
        return Mathf.InverseLerp(shapeGenerator.elevationMinMax.Min, shapeGenerator.elevationMinMax.Max, elevation);
    }

    /// <summary>
    /// Gets foliage density (0-1) using the same height/biome logic as the planet shader.
    /// Uses elevation (terrain height) + biome (latitude + noise) - dense foliage where it's green (valleys, grasslands).
    /// </summary>
    public float GetFoliageGreennessAtPosition(Vector3 worldPosition)
    {
        if (colourSettings == null) return 0f;

        Vector3 centerToPoint = worldPosition - transform.position;
        Vector3 pointOnUnitSphere = centerToPoint.normalized;
        float elevation = centerToPoint.magnitude;

        float elevationNorm = Mathf.InverseLerp(shapeGenerator.elevationMinMax.Min, shapeGenerator.elevationMinMax.Max, elevation);
        Color color = colourGenerator.GetColorAtPointWithElevation(pointOnUnitSphere, elevationNorm);

        float greenness = color.g;
        if (color.g > color.r && color.g > color.b)
            greenness = Mathf.Min(1f, greenness * 1.1f);
        else
            greenness *= 0.3f;
        return Mathf.Clamp01(greenness);
    }
}
