#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-shot: create Assets/Stargrave/.rebuild_weapons flag, then focus Unity.
/// </summary>
[InitializeOnLoad]
static class StargraveWeaponRebuildHook
{
    const string FlagPath = "Assets/Stargrave/.rebuild_weapons";

    static StargraveWeaponRebuildHook()
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

        Debug.Log("[Stargrave] Auto-rebuild weapon prefabs (flag).");
        StargraveWeaponSetup.BuildWeaponPrefabs();
        AssetDatabase.Refresh();
    }
}
#endif
