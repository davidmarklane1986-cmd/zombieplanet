#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;

public static class FixProceduralCloudsPink
{
    const string ShaderPath = "Assets/Stargrave/Shaders/PlanetProceduralClouds.shader";
    const string LogPath = "Assets/Stargrave/Scripts/Editor/_cloud_fix_log.txt";
    const string CapturePath = "Assets/Stargrave/Scripts/Editor/_scene_view_capture.png";

    [MenuItem("Tools/Stargrave/Fix Procedural Clouds Pink Shell")]
    public static void Fix()
    {
        var sb = new StringBuilder();
        sb.AppendLine("fix_version=3");
        AssetDatabase.ImportAsset(ShaderPath, ImportAssetOptions.ForceUpdate);

        Shader cloudShader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
        sb.AppendLine($"cloudShader={(cloudShader != null ? cloudShader.name : "null")} supported={(cloudShader != null && cloudShader.isSupported)}");
        if (cloudShader != null)
        {
            var msgs = ShaderUtil.GetShaderMessages(cloudShader);
            sb.AppendLine($"messages={msgs.Length}");
            for (int i = 0; i < msgs.Length; i++)
                sb.AppendLine($"{msgs[i].severity}: {msgs[i].message} @{msgs[i].line}");
        }

        var clouds = Object.FindObjectsByType<ProceduralPlanetClouds>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        sb.AppendLine($"clouds={clouds.Length}");

        for (int c = 0; c < clouds.Length; c++)
        {
            var cloud = clouds[c];
            cloud.useFullscreenVolume = false;
            cloud.enableShadows = false;
            cloud.enabled = false;
            cloud.enabled = true;

            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var mat = typeof(ProceduralPlanetClouds).GetField("_cloudMaterial", flags)?.GetValue(cloud) as Material;
            var rend = typeof(ProceduralPlanetClouds).GetField("_cloudRenderer", flags)?.GetValue(cloud) as MeshRenderer;
            var obj = typeof(ProceduralPlanetClouds).GetField("_cloudObject", flags)?.GetValue(cloud) as GameObject;
            float inner = (float)(typeof(ProceduralPlanetClouds).GetField("_innerRadius", flags)?.GetValue(cloud) ?? 0f);
            float outer = (float)(typeof(ProceduralPlanetClouds).GetField("_outerRadius", flags)?.GetValue(cloud) ?? 0f);

            // Always restore the real cloud material. An earlier probe path left URP Unlit
            // on the shell and made the procedural clouds disappear.
            if (rend != null && mat != null)
            {
                rend.SetPropertyBlock(null);
                rend.sharedMaterial = mat;
                rend.enabled = true;
                var apply = typeof(ProceduralPlanetClouds).GetMethod(
                    "ApplyCloudState",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                apply?.Invoke(cloud, new object[] { true });
                sb.AppendLine("RESTORE: reassigned Stargrave cloud material");
                if (obj != null)
                {
                    Selection.activeGameObject = obj;
                    SceneView.lastActiveSceneView?.FrameSelected();
                }
            }

            sb.AppendLine($"runtime radii={inner:F1}-{outer:F1} center={cloud.transform.position}");
            sb.AppendLine($"_cloudMaterial={(mat != null ? mat.shader.name : "null")}");
            if (rend != null)
                sb.AppendLine($"renderer.shared={(rend.sharedMaterial != null ? rend.sharedMaterial.shader.name : "null")} enabled={rend.enabled}");
            if (obj != null)
                sb.AppendLine($"layer scale={obj.transform.lossyScale}");
        }

        // Also list other large shell-like renderers that could be the magenta disc.
        var renderers = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        int listed = 0;
        for (int i = 0; i < renderers.Length && listed < 12; i++)
        {
            var r = renderers[i];
            if (r == null || r.sharedMaterial == null) continue;
            string sn = r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "null";
            bool suspicious = sn.Contains("Error") || sn.Contains("Internal") ||
                              r.name.Contains("Cloud") || r.name.Contains("Atmosphere") || r.name.Contains("Ocean");
            float extent = r.bounds.extents.magnitude;
            if (!suspicious && extent < 50f) continue;
            sb.AppendLine($"renderer[{listed}] {GetPath(r.transform)} shader={sn} extent={extent:F1} enabled={r.enabled}");
            listed++;
        }

        CaptureSceneView(sb);
        File.WriteAllText(LogPath, sb.ToString());
        AssetDatabase.ImportAsset(LogPath);
        AssetDatabase.Refresh();
        SceneView.RepaintAll();
        InternalEditorUtility.RepaintAllViews();
        Debug.Log("[CloudFix]\n" + sb);
    }

    static string GetPath(Transform t)
    {
        string p = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            p = t.name + "/" + p;
        }
        return p;
    }

    static void CaptureSceneView(StringBuilder sb)
    {
        SceneView view = SceneView.lastActiveSceneView;
        if (view == null || view.camera == null)
        {
            sb.AppendLine("capture=FAIL");
            return;
        }

        view.Repaint();
        Camera cam = view.camera;
        int w = Mathf.Clamp(Mathf.RoundToInt(view.position.width), 256, 1600);
        int h = Mathf.Clamp(Mathf.RoundToInt(view.position.height), 256, 1000);
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        var prev = cam.targetTexture;
        cam.targetTexture = rt;
        cam.Render();
        cam.targetTexture = prev;
        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        Object.DestroyImmediate(rt);

        Color c = tex.GetPixel(w / 2, h / 2);
        sb.AppendLine($"sample center=({c.r:F2},{c.g:F2},{c.b:F2})");
        bool looksMagenta = c.r > 0.8f && c.b > 0.8f && c.g < 0.35f;
        sb.AppendLine($"looksMagenta={looksMagenta}");

        File.WriteAllBytes(Path.GetFullPath(CapturePath), tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(CapturePath);
        sb.AppendLine($"capture={CapturePath} {w}x{h}");
    }
}
#endif
