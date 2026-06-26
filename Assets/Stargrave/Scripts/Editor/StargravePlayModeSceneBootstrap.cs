#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;

/// <summary>
/// Keeps editor Play aligned with the one-scene Stargrave loop by starting from the gameplay scene.
/// </summary>
[InitializeOnLoad]
public static class StargravePlayModeSceneBootstrap
{
    const string GameplayScenePath = "Assets/Scenes/SampleScene.unity";

    static StargravePlayModeSceneBootstrap()
    {
        SceneAsset gameplay = AssetDatabase.LoadAssetAtPath<SceneAsset>(GameplayScenePath);
        if (gameplay != null)
            EditorSceneManager.playModeStartScene = gameplay;
    }
}
#endif
