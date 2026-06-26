using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class MaterialUpgrader : EditorWindow
{
    [MenuItem("Tools/Fix Pink Materials")]
    public static void UpgradeMaterials()
    {
        string[] searchPaths = new[]
        {
            "Assets/Proxy Games/Stylized Nature Kit Lite/Materials",
            "Assets/ThirdParty/Kenny",
            "Assets/GAMWILL",
            "Assets/ArtStore3D"
        };
        List<string> allGuids = new List<string>();
        foreach (var path in searchPaths)
        {
            if (AssetDatabase.IsValidFolder(path) || System.IO.Directory.Exists(path))
                allGuids.AddRange(AssetDatabase.FindAssets("t:Material", new[] { path }));
        }
        string[] materialGuids = allGuids.ToArray();

        Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
        if (urpLit == null)
        {
            Debug.LogError("Could not find URP Lit shader. Is URP installed?");
            return;
        }

        int count = 0;
        foreach (var guid in materialGuids)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (mat == null) continue;
            string shaderName = mat.shader != null ? mat.shader.name : "";
            bool isBuiltIn = shaderName == "Standard" || shaderName.StartsWith("Legacy Shaders/")
                || mat.shader == null || shaderName.Contains("InternalError") || shaderName.Contains("Error");
            if (!isBuiltIn) continue;

            Undo.RecordObject(mat, "Upgrade Material to URP");
            Texture mainTex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
            Color color = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
            float cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0.5f;
            float mode = mat.HasProperty("_Mode") ? mat.GetFloat("_Mode") : 0f;

            mat.shader = urpLit;
            if (mainTex != null) mat.SetTexture("_BaseMap", mainTex);
            mat.SetColor("_BaseColor", color);

            if (mode == 1)
            {
                mat.SetFloat("_AlphaClip", 1);
                mat.SetFloat("_Cutoff", cutoff);
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            }
            else
            {
                mat.SetFloat("_AlphaClip", 0);
                mat.DisableKeyword("_ALPHATEST_ON");
            }

            EditorUtility.SetDirty(mat);
            count++;
        }
        
        AssetDatabase.SaveAssets();
        Debug.Log($"Upgraded {count} materials to URP.");
    }
}
