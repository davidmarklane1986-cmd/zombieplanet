#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

[InitializeOnLoad]
static class ProceduralPlanetCloudsRendererSetup
{
    const string PcRendererPath = "Assets/Settings/PC_Renderer.asset";

    static ProceduralPlanetCloudsRendererSetup()
    {
        EditorApplication.delayCall += EnsureFeatureInstalled;
    }

    [MenuItem("Tools/Stargrave/Install Procedural Planet Clouds Renderer")]
    static void InstallFromMenu()
    {
        EnsureFeatureInstalled();
    }

    static void EnsureFeatureInstalled()
    {
        EditorApplication.delayCall -= EnsureFeatureInstalled;

        UniversalRendererData rendererData =
            AssetDatabase.LoadAssetAtPath<UniversalRendererData>(PcRendererPath);
        if (rendererData == null)
            return;

        for (int i = 0; i < rendererData.rendererFeatures.Count; i++)
        {
            if (rendererData.rendererFeatures[i] is ProceduralPlanetCloudsRendererFeature)
                return;
        }

        ProceduralPlanetCloudsRendererFeature feature =
            ScriptableObject.CreateInstance<ProceduralPlanetCloudsRendererFeature>();
        feature.name = "ProceduralPlanetCloudsRendererFeature";
        AssetDatabase.AddObjectToAsset(feature, rendererData);
        rendererData.rendererFeatures.Add(feature);
        EditorUtility.SetDirty(rendererData);
        AssetDatabase.SaveAssets();
    }
}
#endif
