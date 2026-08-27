using UnityEngine;

/// <summary>
/// Shared helper that retunes a material so it shades like the matte planet terrain:
/// pure Lambert diffuse (albedo * sun N.L + shadows) plus a flat SampleSH ambient floor, with
/// NO glossy specular highlight and NO environment reflection. This is what makes the gameplay
/// MODELS (player, zombies, power-ups, Kenney foliage) read as the same matte surface as the
/// reworked PlanetColourWithBiomes terrain instead of looking shinier/brighter under the
/// intensity-2 directional sun.
///
/// It is deliberately property-driven (HasProperty gated) so the SAME call works for:
///  • URP "Universal Render Pipeline/Lit" and "Simple Lit" (the player / zombie / power-up mats), and
///  • the glTFast "glTF-pbrMetallicRoughness" shader graph that the imported Kenney foliage GLBs use.
///
/// Runtime-safe (Material API only) so it can be called from both the foliage runtime path and the
/// editor tooling/generators that share this single source of truth.
///
/// Does not swap shaders — keeps URP Lit / glTF so lighting matches a single sun (+ scene moon
/// on URP additional lights). Use <see cref="ApplyPlanetMatteShader"/> only when you explicitly
/// need the custom spherical terminator shader.
/// </summary>
public static class ModelMatteLighting
{
    const string PlanetMatteLitShaderName = "Stargrave/Planet Matte Lit";
    const string SpecularHighlightsOffKeyword = "_SPECULARHIGHLIGHTS_OFF";
    const string EnvironmentReflectionsOffKeyword = "_ENVIRONMENTREFLECTIONS_OFF";
    static readonly int MatchTerrainSunId = Shader.PropertyToID("_MatchTerrainSun");
    static readonly int AmbientFillId = Shader.PropertyToID("_AmbientFill");
    static readonly int DiffuseScaleId = Shader.PropertyToID("_DiffuseScale");

    public const float PlayerAmbientFill = 0.68f;
    public const float FoliageAmbientFill = 1.05f;
    public const float FoliageDiffuseScale = 0.52f;
    public const float CharacterAmbientFill = 0.72f;

    /// <summary>
    /// Matte tuning on the existing shader (no shader swap).
    /// Optional foliage/character tuning args apply only when the material already uses
    /// <see cref="PlanetMatteLitShaderName"/>.
    /// </summary>
    public static void MakeMatte(
        Material m,
        bool matchTerrainTerminator = false,
        float ambientFill = 1f,
        float diffuseScale = 1f)
    {
        if (m == null)
            return;

        if (m.HasProperty("_Smoothness"))
            m.SetFloat("_Smoothness", 0f);
        if (m.HasProperty("_Glossiness"))
            m.SetFloat("_Glossiness", 0f);
        if (m.HasProperty("_GlossMapScale"))
            m.SetFloat("_GlossMapScale", 0f);
        if (m.HasProperty("_Metallic"))
            m.SetFloat("_Metallic", 0f);

        if (m.HasProperty("_SpecularHighlights"))
        {
            m.SetFloat("_SpecularHighlights", 0f);
            m.EnableKeyword(SpecularHighlightsOffKeyword);
        }

        if (m.HasProperty("_EnvironmentReflections"))
        {
            m.SetFloat("_EnvironmentReflections", 0f);
            m.EnableKeyword(EnvironmentReflectionsOffKeyword);
        }
        if (m.HasProperty("_GlossyReflections"))
            m.SetFloat("_GlossyReflections", 0f);

        if (m.HasProperty("roughnessFactor"))
            m.SetFloat("roughnessFactor", 1f);
        if (m.HasProperty("metallicFactor"))
            m.SetFloat("metallicFactor", 0f);

        if (m.shader != null && m.shader.name == PlanetMatteLitShaderName)
        {
            if (m.HasProperty(MatchTerrainSunId))
                m.SetFloat(MatchTerrainSunId, matchTerrainTerminator ? 1f : 0f);
            if (m.HasProperty(AmbientFillId))
                m.SetFloat(AmbientFillId, Mathf.Max(0f, ambientFill));
            if (m.HasProperty(DiffuseScaleId))
                m.SetFloat(DiffuseScaleId, Mathf.Max(0f, diffuseScale));
        }
    }

    /// <summary>
    /// Optional: swap URP Lit / glTF to the custom planet matte shader (spherical terminator + shader moon).
    /// Not used by default gameplay paths — call only when you need that behaviour.
    /// </summary>
    public static bool ApplyPlanetMatteShader(Material m)
    {
        if (m == null || m.shader == null)
            return false;

        string shaderName = m.shader.name;
        if (shaderName == PlanetMatteLitShaderName)
            return true;

        bool convertible = shaderName == "Universal Render Pipeline/Lit"
            || shaderName == "Universal Render Pipeline/Simple Lit"
            || shaderName == "Standard"
            || IsGltfPbrShader(shaderName);
        if (!convertible)
            return false;

        Shader planetMatte = Shader.Find(PlanetMatteLitShaderName);
        if (planetMatte == null)
            return false;

        Texture baseMap = null;
        Color baseColor = Color.white;

        if (m.HasProperty("_BaseMap"))
            baseMap = m.GetTexture("_BaseMap");
        if (baseMap == null && m.HasProperty("_MainTex"))
            baseMap = m.GetTexture("_MainTex");
        if (baseMap == null && m.HasProperty("baseColorTexture"))
            baseMap = m.GetTexture("baseColorTexture");

        if (m.HasProperty("_BaseColor"))
            baseColor = m.GetColor("_BaseColor");
        else if (m.HasProperty("_Color"))
            baseColor = m.GetColor("_Color");
        else if (m.HasProperty("baseColorFactor"))
            baseColor = m.GetColor("baseColorFactor");

        m.shader = planetMatte;
        if (baseMap != null)
            m.SetTexture("_BaseMap", baseMap);
        m.SetColor("_BaseColor", baseColor);
        return true;
    }

    static bool IsGltfPbrShader(string shaderName)
    {
        if (string.IsNullOrEmpty(shaderName))
            return false;
        return shaderName.IndexOf("glTF", System.StringComparison.OrdinalIgnoreCase) >= 0
            || shaderName.IndexOf("glTFast", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
