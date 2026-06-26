using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Reusable planet flora layout: biome rules, clustering, and global density.
/// Assign on <see cref="SimpleFoliageSpawner"/> or load from Resources at runtime.
/// </summary>
[CreateAssetMenu(fileName = "RichPlanetFlora", menuName = "Stargrave/Foliage Spawn Profile")]
public class FoliageSpawnProfile : ScriptableObject
{
    public List<SimpleFoliageSpawner.BiomeSpawnRule> spawnRules = new List<SimpleFoliageSpawner.BiomeSpawnRule>();

    [Header("Global")]
    [Range(0.1f, 3f)] public float globalDensityMultiplier = 1.2f;
    public bool excludeUnderwater = true;
    [Range(0f, 1f)] public float patchNoiseStrength = 0.55f;
    public bool forceDoubleSidedAll = true;
    [Min(0f)] public float globalMinSeparation = 0.35f;

    public void ApplyTo(SimpleFoliageSpawner spawner)
    {
        if (spawner == null)
            return;

        spawner.spawnRules = spawnRules;
        spawner.globalDensityMultiplier = globalDensityMultiplier;
        spawner.excludeUnderwater = excludeUnderwater;
        spawner.patchNoiseStrength = patchNoiseStrength;
        spawner.forceDoubleSidedAll = forceDoubleSidedAll;
        spawner.globalMinSeparation = globalMinSeparation;
    }
}
