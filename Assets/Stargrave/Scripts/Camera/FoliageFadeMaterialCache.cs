using System.Collections.Generic;
using UnityEngine;

namespace Stargrave.CameraOcclusion
{
    public sealed class FoliageFadeMaterialCache
    {
        const string OcclusionShaderName = "Shader Graphs/StargraveFoliageGltfOcclusion";

        // Keep the source Material itself as the key. EntityId is not reliable for every
        // runtime-generated glTF material and can collapse different materials into one cache entry.
        readonly Dictionary<Material, Material> _instances = new Dictionary<Material, Material>(64);
        Shader _occlusionShader;

        public void SetShader(Shader shader)
        {
            if (shader != null)
                _occlusionShader = shader;
        }

        public void WarmShader()
        {
            if (_occlusionShader == null)
                _occlusionShader = Shader.Find(OcclusionShaderName);
        }

        public Material GetOrCreateOcclusionMaterial(Material source)
        {
            if (source == null)
                return null;

            if (_instances.TryGetValue(source, out Material cached))
            {
                if (cached != null && cached.shader == _occlusionShader
                    && cached.HasProperty("baseColorTexture")
                    && cached.HasProperty("baseColorFactor")
                    && (cached.HasProperty("alphaCutoff") || cached.HasProperty("_Cutoff")))
                    return cached;

                if (cached != null)
                    Object.Destroy(cached);
                _instances.Remove(source);
            }

            WarmShader();
            if (_occlusionShader == null)
                return null;

            var inst = BuildOcclusionMaterial(source);
            if (inst != null)
                _instances[source] = inst;
            return inst;
        }

        Material BuildOcclusionMaterial(Material source)
        {
            var inst = new Material(_occlusionShader)
            {
                name = source.name + " (OccCircle)",
                hideFlags = HideFlags.HideAndDontSave,
                enableInstancing = false
            };

            // The project-owned graph uses glTFast's native property names, so the imported
            // material can be copied without rebuilding its PBR inputs or lighting model.
            inst.CopyMatchingPropertiesFromMaterial(source);
            ConfigureOcclusionSurface(inst);
            CopyFirstTexture(inst, source, "baseColorTexture", "baseColorTexture",
                "_BaseColorTexture", "_BaseMap", "_MainTex", "_BaseColorMap");
            CopyVector(inst, source, "baseColorFactor");
            CopyVectorFrom(inst, source, "baseColorFactor", "_BaseColorFactor");
            CopyVectorFrom(inst, source, "baseColorFactor", "_BaseColor");
            CopyFloat(inst, source, "alphaCutoff");
            CopyFloat(inst, source, "_Cutoff");

            return inst;
        }

        static void ConfigureOcclusionSurface(Material material)
        {
            if (material == null)
                return;

            // CopyMatchingPropertiesFromMaterial can copy the source glTF material's
            // opaque/alpha-test surface controls. Override only those pipeline controls;
            // the PBR texture, colour, and cutoff values remain copied from the source.
            SetFloatIfPresent(material, "_Surface", 0f);
            SetFloatIfPresent(material, "_AlphaClip", 1f);
            SetFloatIfPresent(material, "_Blend", 0f);
            SetFloatIfPresent(material, "_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            SetFloatIfPresent(material, "_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
            SetFloatIfPresent(material, "_ZWrite", 1f);
            SetFloatIfPresent(material, "_AlphaToMask", 0f);

            material.SetOverrideTag("RenderType", "TransparentCutout");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            material.EnableKeyword("_ALPHATEST_ON");
            material.EnableKeyword("_SURFACE_TYPE_OPAQUE");
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }

        static void SetFloatIfPresent(Material material, string name, float value)
        {
            if (material.HasProperty(name))
                material.SetFloat(name, value);
        }

        static void CopyVector(Material dst, Material src, string name)
        {
            if (dst.HasProperty(name) && src.HasProperty(name))
                dst.SetVector(name, src.GetVector(name));
        }

        static void CopyFloat(Material dst, Material src, string name)
        {
            if (dst.HasProperty(name) && src.HasProperty(name))
                dst.SetFloat(name, src.GetFloat(name));
        }

        static void CopyVectorFrom(Material dst, Material src, string dstName, string srcName)
        {
            if (dst.HasProperty(dstName) && src.HasProperty(srcName)
                && (!src.HasProperty("baseColorFactor") || srcName == "baseColorFactor"))
                dst.SetVector(dstName, src.GetVector(srcName));
        }

        static void CopyFirstTexture(Material dst, Material src, string destinationName, params string[] names)
        {
            if (!dst.HasProperty(destinationName) || names == null)
                return;

            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                if (!src.HasProperty(name))
                    continue;
                var tex = src.GetTexture(name);
                if (tex == null)
                    continue;

                dst.SetTexture(destinationName, tex);
                dst.SetTextureScale(destinationName, src.GetTextureScale(name));
                dst.SetTextureOffset(destinationName, src.GetTextureOffset(name));
                return;
            }
        }

        public void Dispose()
        {
            foreach (var kv in _instances)
            {
                if (kv.Value != null)
                    Object.Destroy(kv.Value);
            }

            _instances.Clear();
            _occlusionShader = null;
        }
    }
}
