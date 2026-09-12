using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Shared GPU cross-billboard batches for mid/far tree impostors.
/// Uses Kenny NatureKit side sprites (alpha cutout) when available; otherwise a procedural silhouette.
/// </summary>
public static class FoliageTreeImpostorDraw
{
    const int BatchSize = 1023;
    const string KennySideTreePath = "Assets/ThirdParty/Kenny/NatureKit/Side/tree_default.png";
    const string KennySidePinePath = "Assets/ThirdParty/Kenny/NatureKit/Side/tree_pineDefaultA.png";

    static Mesh _mesh;
    static Material _material;
    static Texture2D _texture;
    static readonly List<Matrix4x4> _matrices = new List<Matrix4x4>(1024);
    static readonly Matrix4x4[] _batch = new Matrix4x4[BatchSize];

    public static void EnsureReady()
    {
        if (_mesh == null)
            _mesh = BuildCrossQuadMesh();
        if (_texture == null)
            _texture = LoadOrBuildTreeTexture();
        if (_material == null)
            _material = BuildCutoutMaterial(_texture);
        else if (_texture != null && _material.HasProperty("_BaseMap") && _material.GetTexture("_BaseMap") == null)
            _material.SetTexture("_BaseMap", _texture);
    }

    public static void ClearBatches() => _matrices.Clear();

    public static void Add(Matrix4x4 matrix) => _matrices.Add(matrix);

    public static Matrix4x4 MakeMatrix(Vector3 pos, Vector3 up, float yawRad, float height, float widthScale)
    {
        if (up.sqrMagnitude < 1e-8f)
            up = Vector3.up;
        else if (Mathf.Abs(up.sqrMagnitude - 1f) > 1e-4f)
            up.Normalize();
        Quaternion rotation = Quaternion.FromToRotation(Vector3.up, up)
                              * Quaternion.AngleAxis(yawRad * Mathf.Rad2Deg, Vector3.up);
        float h = height < 0.05f ? 0.05f : height;
        // Side sprites are taller than wide; keep width a bit under height so canopy reads correctly.
        float w = h * (widthScale < 0.05f ? 0.05f : widthScale);
        return Matrix4x4.TRS(pos, rotation, new Vector3(w, h, w));
    }

    public static void Flush(int layer)
    {
        EnsureReady();
        if (_mesh == null || _material == null || _matrices.Count == 0)
            return;
        int i = 0;
        while (i < _matrices.Count)
        {
            int n = Mathf.Min(BatchSize, _matrices.Count - i);
            for (int k = 0; k < n; k++)
                _batch[k] = _matrices[i + k];
            Graphics.DrawMeshInstanced(
                _mesh, 0, _material, _batch, n, null,
                ShadowCastingMode.Off, true, layer);
            i += n;
        }
        _matrices.Clear();
    }

    static Material BuildCutoutMaterial(Texture2D tex)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null)
            sh = Shader.Find("Unlit/Transparent Cutout");
        if (sh == null)
            sh = Shader.Find("Unlit/Color");

        var mat = new Material(sh) { name = "TreeImpostorCutout", enableInstancing = true };
        mat.color = Color.white;
        if (tex != null)
        {
            if (mat.HasProperty("_BaseMap"))
                mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_MainTex"))
                mat.SetTexture("_MainTex", tex);
        }
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", Color.white);
        if (mat.HasProperty("_Color"))
            mat.SetColor("_Color", Color.white);

        // Opaque + alpha clip (same pattern as foliage fade cutout materials).
        if (mat.HasProperty("_Surface"))
            mat.SetFloat("_Surface", 0f);
        if (mat.HasProperty("_AlphaClip"))
            mat.SetFloat("_AlphaClip", 1f);
        if (mat.HasProperty("_Cutoff"))
            mat.SetFloat("_Cutoff", 0.35f);
        if (mat.HasProperty("_AlphaToMask"))
            mat.SetFloat("_AlphaToMask", 0f);
        if (mat.HasProperty("_ZWrite"))
            mat.SetFloat("_ZWrite", 1f);
        if (mat.HasProperty("_Cull"))
            mat.SetFloat("_Cull", (float)CullMode.Off);

        mat.SetOverrideTag("RenderType", "TransparentCutout");
        mat.renderQueue = (int)RenderQueue.AlphaTest;
        mat.EnableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_SURFACE_TYPE_OPAQUE");
        mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        return mat;
    }

    static Texture2D LoadOrBuildTreeTexture()
    {
        Texture2D tex = null;
#if UNITY_EDITOR
        tex = AssetDatabase.LoadAssetAtPath<Texture2D>(KennySideTreePath);
        if (tex == null)
            tex = AssetDatabase.LoadAssetAtPath<Texture2D>(KennySidePinePath);
#endif
        if (tex == null)
            tex = Resources.Load<Texture2D>("Foliage/tree_impostor");
        if (tex != null)
            return tex;
        return BuildProceduralTreeTexture();
    }

    /// <summary>
    /// Fallback silhouette: trunk + leafy canopy with alpha, readable at mid/far range.
    /// </summary>
    static Texture2D BuildProceduralTreeTexture()
    {
        const int w = 96;
        const int h = 160;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: true)
        {
            name = "TreeImpostorProcedural",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            alphaIsTransparency = true
        };

        var pixels = new Color32[w * h];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Color32(0, 0, 0, 0);

        // Trunk
        int trunkW = Mathf.Max(4, w / 10);
        int trunkH = h / 3;
        int trunkX0 = (w - trunkW) / 2;
        for (int y = 0; y < trunkH; y++)
        {
            for (int x = trunkX0; x < trunkX0 + trunkW; x++)
            {
                byte shade = (byte)(70 + (y * 30) / trunkH);
                pixels[y * w + x] = new Color32(shade, (byte)(shade * 0.65f), (byte)(shade * 0.35f), 255);
            }
        }

        // Canopy: stacked ellipses with noisy edge
        DrawCanopyBlob(pixels, w, h, cx: w * 0.50f, cy: h * 0.62f, rx: w * 0.42f, ry: h * 0.28f,
            new Color32(34, 92, 28, 255));
        DrawCanopyBlob(pixels, w, h, cx: w * 0.38f, cy: h * 0.72f, rx: w * 0.28f, ry: h * 0.22f,
            new Color32(28, 78, 22, 255));
        DrawCanopyBlob(pixels, w, h, cx: w * 0.62f, cy: h * 0.72f, rx: w * 0.28f, ry: h * 0.22f,
            new Color32(42, 110, 34, 255));
        DrawCanopyBlob(pixels, w, h, cx: w * 0.50f, cy: h * 0.84f, rx: w * 0.24f, ry: h * 0.16f,
            new Color32(48, 120, 40, 255));

        tex.SetPixels32(pixels);
        tex.Apply(updateMipmaps: true, makeNoLongerReadable: true);
        return tex;
    }

    static void DrawCanopyBlob(Color32[] pixels, int w, int h, float cx, float cy, float rx, float ry, Color32 col)
    {
        int x0 = Mathf.Max(0, Mathf.FloorToInt(cx - rx - 1));
        int x1 = Mathf.Min(w - 1, Mathf.CeilToInt(cx + rx + 1));
        int y0 = Mathf.Max(0, Mathf.FloorToInt(cy - ry - 1));
        int y1 = Mathf.Min(h - 1, Mathf.CeilToInt(cy + ry + 1));
        float rx2 = Mathf.Max(0.001f, rx * rx);
        float ry2 = Mathf.Max(0.001f, ry * ry);

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float nx = (x + 0.5f - cx);
                float ny = (y + 0.5f - cy);
                float e = (nx * nx) / rx2 + (ny * ny) / ry2;
                // Cheap edge noise so it doesn't look like a perfect oval.
                float n = Mathf.PerlinNoise(x * 0.17f, y * 0.17f) * 0.35f;
                if (e > 1f - n)
                    continue;
                float edge = Mathf.Clamp01((1f - n - e) / 0.25f);
                byte a = (byte)Mathf.Clamp(Mathf.RoundToInt(edge * 255f), 0, 255);
                if (a < 40)
                    continue;
                int idx = y * w + x;
                // Keep existing darker leaf pixels when overlapping.
                if (pixels[idx].a > 0 && pixels[idx].g < col.g)
                    continue;
                col.a = a;
                pixels[idx] = col;
            }
        }
    }

    static Mesh BuildCrossQuadMesh()
    {
        // Two vertical quads crossing on Y, bottom at y=0, top at y=1, width [-0.5,0.5].
        var mesh = new Mesh { name = "TreeImpostorCross" };
        var v = new Vector3[]
        {
            new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(0.5f, 1f, 0f), new Vector3(-0.5f, 1f, 0f),
            new Vector3(0f, 0f, -0.5f), new Vector3(0f, 0f, 0.5f), new Vector3(0f, 1f, 0.5f), new Vector3(0f, 1f, -0.5f),
        };
        var n = new Vector3[]
        {
            Vector3.back, Vector3.back, Vector3.back, Vector3.back,
            Vector3.left, Vector3.left, Vector3.left, Vector3.left,
        };
        // Kenny side sprites: trunk at bottom of image → UV v=0 at ground.
        var uv = new Vector2[]
        {
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
        };
        var t = new int[]
        {
            0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3,
            4, 6, 5, 4, 7, 6, 4, 5, 6, 4, 6, 7,
        };
        mesh.vertices = v;
        mesh.normals = n;
        mesh.uv = uv;
        mesh.triangles = t;
        mesh.RecalculateBounds();
        return mesh;
    }
}
