using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ShapeGenerator {

    ShapeSettings settings;
    INoiseFilter[] noiseFilters;
    public MinMax elevationMinMax;

    public void UpdateSettings(ShapeSettings settings)
    {
        this.settings = settings;
        noiseFilters = new INoiseFilter[settings.noiseLayers.Length];
        for (int i = 0; i < noiseFilters.Length; i++)
        {
            noiseFilters[i] = NoiseFilterFactory.CreateNoiseFilter(settings.noiseLayers[i].noiseSettings);
        }
        elevationMinMax = new MinMax();
    }

    public Vector3 CalculatePointOnPlanet(Vector3 pointOnUnitSphere)
    {
        float elevation = CalculateUnscaledElevation(pointOnUnitSphere);
        elevationMinMax.AddValue(elevation);
        return pointOnUnitSphere * elevation;
    }

    /// <summary>
    /// Noise-only local radius (no building pads). Used when baking pad target heights so pads do not
    /// self-reference.
    /// </summary>
    public float CalculateNaturalUnscaledElevation(Vector3 pointOnUnitSphere)
    {
        if (noiseFilters == null)
            return (settings != null) ? settings.planetRadius : 0f;

        float firstLayerValue = 0;
        float elevation = 0;

        if (noiseFilters.Length > 0)
        {
            firstLayerValue = noiseFilters[0].Evaluate(pointOnUnitSphere);
            if (settings.noiseLayers[0].enabled)
            {
                elevation = firstLayerValue;
            }
        }

        for (int i = 1; i < noiseFilters.Length; i++)
        {
            if (settings.noiseLayers[i].enabled)
            {
                float mask = (settings.noiseLayers[i].useFirstLayerAsMask) ? firstLayerValue : 1;
                elevation += noiseFilters[i].Evaluate(pointOnUnitSphere) * mask;
            }
        }
        return settings.planetRadius * (1 + elevation);
    }

    /// <summary>
    /// Returns the surface radius (distance from planet center, in LOCAL/unscaled mesh units) for a unit-sphere
    /// direction — i.e. the magnitude of <see cref="CalculatePointOnPlanet"/> — WITHOUT mutating
    /// <see cref="elevationMinMax"/>. This is the analytic, side-effect-free equivalent of raycasting the mesh:
    /// the planet surface is a deterministic function of direction, so foliage placement can evaluate it directly.
    /// It must NOT touch elevationMinMax, otherwise repeated runtime sampling would shift the Min/Max that the
    /// shader and GetNormalizedElevationAtPosition normalize against.
    /// Applies hybrid <see cref="PlanetBuildingPads"/> deformation after noise.
    /// </summary>
    public float CalculateUnscaledElevation(Vector3 pointOnUnitSphere)
    {
        float natural = CalculateNaturalUnscaledElevation(pointOnUnitSphere);
        return PlanetBuildingPads.Apply(pointOnUnitSphere, natural);
    }
}
 