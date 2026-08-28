#if UNITY_EDITOR
using Stargrave.CameraOcclusion;
using UnityEditor;
using UnityEngine;

public static class PlayerScreenCircleSetup
{
    [MenuItem("Tools/Stargrave/Camera/Add Player Screen Circle")]
    public static void AddToMainCamera()
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            Debug.LogWarning("[PlayerScreenCircle] No Main Camera in the open scene.");
            return;
        }

        var overlay = cam.GetComponent<PlayerScreenCircleOverlay>();
        if (overlay == null)
            overlay = Undo.AddComponent<PlayerScreenCircleOverlay>(cam.gameObject);

        var occ = cam.GetComponent<ScreenCircleOcclusion>();
        if (occ == null)
            occ = Undo.AddComponent<ScreenCircleOcclusion>(cam.gameObject);

        Undo.RecordObject(overlay, "Setup Player Screen Circle");
        Undo.RecordObject(occ, "Setup Player Screen Circle");

        GameObject player = GameObject.FindGameObjectWithTag("Player");
        SerializedObject so = new SerializedObject(overlay);
        if (player != null)
            so.FindProperty("player").objectReferenceValue = player.transform;
        so.FindProperty("diameterPixels").floatValue = 288f;
        so.ApplyModifiedProperties();

        Shader occShader = AssetDatabase.LoadAssetAtPath<Shader>(
            "Assets/Stargrave/Shaders/StargraveFoliageGltfOcclusion.shadergraph");
        if (occShader == null)
            occShader = AssetDatabase.LoadAssetAtPath<Shader>(
                "Assets/Stargrave/Shaders/StargraveFoliageOcclusionLit.shader");
        if (occShader != null)
        {
            SerializedObject occSo = new SerializedObject(occ);
            occSo.FindProperty("occlusionShader").objectReferenceValue = occShader;
            occSo.ApplyModifiedProperties();
        }

        EditorUtility.SetDirty(overlay);
        EditorUtility.SetDirty(occ);
        Debug.Log("[PlayerScreenCircle] Added to '" + cam.name + "'. Circle tracks player on screen.");
    }
}
#endif
