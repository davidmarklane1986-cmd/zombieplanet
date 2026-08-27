#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-shot: create Assets/Stargrave/.rebuild_playables flag, then focus Unity.
/// Cleared after Tools/Stargrave/Build Playable Characters runs.
/// </summary>
[InitializeOnLoad]
static class StargravePlayableCharacterRebuildHook
{
    const string FlagPath = "Assets/Stargrave/.rebuild_playables";

    static StargravePlayableCharacterRebuildHook()
    {
        EditorApplication.delayCall += TryRebuild;
    }

    static void TryRebuild()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += TryRebuild;
            return;
        }

        if (!File.Exists(FlagPath))
            return;

        try
        {
            File.Delete(FlagPath);
            if (File.Exists(FlagPath + ".meta"))
                File.Delete(FlagPath + ".meta");
        }
        catch
        {
            // ignore
        }

        Debug.Log("[Stargrave] Auto-rebuild playable characters (flag).");
        StargravePlayableCharacterSetup.BuildPlayableCharacters();
        AssetDatabase.Refresh();
    }
}
#endif
