using UnityEditor;
using UnityEngine;

public static class PlanetAtmosphereSetupMenu
{
    [MenuItem("Stargrave/Atmosphere/Setup On Selected Planets")]
    static void SetupOnSelectedPlanets()
    {
        GameObject[] selected = Selection.gameObjects;
        if (selected == null || selected.Length == 0)
        {
            Debug.LogWarning("Select one or more planet GameObjects first.");
            return;
        }

        int updatedCount = 0;
        foreach (GameObject go in selected)
        {
            if (go == null)
                continue;

            PlanetAtmosphereLayer atmosphere = go.GetComponent<PlanetAtmosphereLayer>();
            if (atmosphere == null)
                atmosphere = Undo.AddComponent<PlanetAtmosphereLayer>(go);
            atmosphere.atmosphereMode = PlanetAtmosphereLayer.AtmosphereMode.Scattering;
            atmosphere.atmosphereUniformLocalScale = 0f;
            atmosphere.radiusMultiplier = 1.03f;
            atmosphere.extraPaddingWorld = 2.5f;
            atmosphere.sunIntensity = 2.8f;
            atmosphere.densityFalloff = 3.8f;

            Undo.RecordObject(atmosphere, "Setup Planet Atmosphere");
            atmosphere.CreateOrUpdateAtmosphere();
            EditorUtility.SetDirty(atmosphere);
            updatedCount++;
        }

        Debug.Log($"Atmosphere setup complete on {updatedCount} selected object(s).");
    }

    [MenuItem("Stargrave/Atmosphere/Apply Cinematic Preset (Selected)")]
    static void ApplyCinematicPresetOnSelected()
    {
        GameObject[] selected = Selection.gameObjects;
        if (selected == null || selected.Length == 0)
        {
            Debug.LogWarning("Select one or more planet GameObjects first.");
            return;
        }

        int updatedCount = 0;
        foreach (GameObject go in selected)
        {
            if (go == null)
                continue;

            PlanetAtmosphereLayer atmosphere = go.GetComponent<PlanetAtmosphereLayer>();
            if (atmosphere == null)
                atmosphere = Undo.AddComponent<PlanetAtmosphereLayer>(go);
            Undo.RecordObject(atmosphere, "Apply Cinematic Atmosphere Preset");
            atmosphere.ApplyCinematicScatteringPreset();
            atmosphere.CreateOrUpdateAtmosphere();
            EditorUtility.SetDirty(atmosphere);
            updatedCount++;
        }

        Debug.Log($"Cinematic atmosphere preset applied on {updatedCount} selected object(s).");
    }
}
