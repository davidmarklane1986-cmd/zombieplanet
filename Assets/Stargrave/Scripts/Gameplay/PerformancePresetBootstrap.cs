using UnityEngine;

/// <summary>
/// Small runtime graphics/performance preset layer for the one-scene loop.
/// Persists the current profile and reapplies it after scene loads.
/// </summary>
public static class PerformancePresetBootstrap
{
    public enum GraphicsProfile
    {
        Performance = 0,
        Balanced = 1,
        Quality = 2
    }

    const string GraphicsProfileKey = "GraphicsProfile";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void ApplyPresetAfterSceneLoad()
    {
        ApplyCurrentProfile();
    }

    public static GraphicsProfile GetCurrentProfile()
    {
        int raw = PlayerPrefs.GetInt(GraphicsProfileKey, (int)GraphicsProfile.Balanced);
        if (!System.Enum.IsDefined(typeof(GraphicsProfile), raw))
            raw = (int)GraphicsProfile.Balanced;
        return (GraphicsProfile)raw;
    }

    public static void SetProfile(GraphicsProfile profile)
    {
        PlayerPrefs.SetInt(GraphicsProfileKey, (int)profile);
        PlayerPrefs.Save();
        ApplyCurrentProfile();
    }

    public static void ApplyCurrentProfile()
    {
        GraphicsProfile profile = GetCurrentProfile();
        ApplyZombiePreset(profile);
        ApplyGlobalQualityPreset(profile);
    }

    static void ApplyZombiePreset(GraphicsProfile profile)
    {
        ZombieAI[] all = Object.FindObjectsByType<ZombieAI>(FindObjectsInactive.Include);
        for (int i = 0; i < all.Length; i++)
            ApplyZombiePreset(all[i], profile);
    }

    public static void ApplyZombiePreset(ZombieAI z, GraphicsProfile? profile = null)
    {
        if (z == null)
            return;
        GraphicsProfile p = profile ?? GetCurrentProfile();
        bool quality = p == GraphicsProfile.Quality;
        bool balanced = p == GraphicsProfile.Balanced;

        z.aiDecisionPeriod = quality ? 2 : (balanced ? 2 : 3);
        z.farDecisionPeriodMultiplier = quality ? 2f : (balanced ? 2.3f : 2.7f);
        z.cheapDecisionPeriodMultiplier = quality ? 2.5f : (balanced ? 3f : 3.7f);
        z.maxFullAiEnemiesNearPlayer = quality ? 28 : (balanced ? 22 : 16);
        z.fullAiNearDistance = quality ? 36f : (balanced ? 32f : 28f);
        z.surfaceStickRaycastPeriod = quality ? 2 : (balanced ? 2 : 3);
        z.cheapIdleSpeedMultiplier = quality ? 0.45f : (balanced ? 0.4f : 0.34f);
    }

    static void ApplyGlobalQualityPreset(GraphicsProfile profile)
    {
        if (profile == GraphicsProfile.Quality)
        {
            QualitySettings.shadowDistance = Mathf.Max(QualitySettings.shadowDistance, 90f);
            QualitySettings.shadowResolution = ShadowResolution.High;
            QualitySettings.softParticles = true;
            QualitySettings.realtimeReflectionProbes = true;
            return;
        }

        if (profile == GraphicsProfile.Balanced)
        {
            QualitySettings.shadowDistance = 70f;
            QualitySettings.shadowResolution = ShadowResolution.Medium;
            QualitySettings.softParticles = false;
            QualitySettings.realtimeReflectionProbes = false;
            return;
        }

        QualitySettings.shadowDistance = 55f;
        QualitySettings.shadowResolution = ShadowResolution.Medium;
        QualitySettings.softParticles = false;
        QualitySettings.realtimeReflectionProbes = false;
    }
}
