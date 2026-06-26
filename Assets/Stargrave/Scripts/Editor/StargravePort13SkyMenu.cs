using UnityEditor;
using UnityEngine;

/// <summary>
/// Wires a skybox material dropped into <see cref="PortFolder"/> into the active scene's Lighting settings.
/// Does not copy files from 1.3 — you must paste them into Assets/Stargrave/PortedFrom13 first.
/// </summary>
public static class StargravePort13SkyMenu
{
    const string PortFolder = "Assets/Stargrave/PortedFrom13";
    const string PreferredSkyMat = PortFolder + "/Skybox.mat";

    [MenuItem("Tools/Stargrave/Apply Ported 1.3 Sky (PortedFrom13)", false, 500)]
    public static void ApplyPortedSky()
    {
        Material sky = AssetDatabase.LoadAssetAtPath<Material>(PreferredSkyMat);
        if (sky == null)
        {
            string[] guids = AssetDatabase.FindAssets("t:Material", new[] { PortFolder });
            if (guids.Length > 0)
            {
                string p = AssetDatabase.GUIDToAssetPath(guids[0]);
                sky = AssetDatabase.LoadAssetAtPath<Material>(p);
                Debug.Log($"[{nameof(StargravePort13SkyMenu)}] Using first material in port folder: {p}");
            }
        }

        if (sky == null)
        {
            EditorUtility.DisplayDialog(
                "Stargrave port",
                $"No material found in {PortFolder}.\n\nCreate the folder, copy your 1.3 skybox .mat (and textures) there, " +
                $"preferably named Skybox.mat, then run this again.\n\nSee PORT_HERE.txt in that folder.",
                "OK");
            return;
        }

        RenderSettings.skybox = sky;
        DynamicGI.UpdateEnvironment();

        Debug.Log($"[{nameof(StargravePort13SkyMenu)}] RenderSettings.skybox = '{AssetDatabase.GetAssetPath(sky)}'. " +
                  "Ensure Lighting > Environment > Source is Skybox.");
        EditorUtility.DisplayDialog("Stargrave port", "Skybox applied to RenderSettings.\n\n" +
            "Ensure Lighting > Environment > Source is Skybox.", "OK");
    }

    [MenuItem("Tools/Stargrave/Open PortedFrom13 Folder", false, 501)]
    public static void OpenPortFolder()
    {
        if (!AssetDatabase.IsValidFolder(PortFolder))
            AssetDatabase.CreateFolder("Assets/Stargrave", "PortedFrom13");
        var obj = AssetDatabase.LoadAssetAtPath<Object>(PortFolder);
        EditorGUIUtility.PingObject(obj);
        Selection.activeObject = obj;
    }
}
