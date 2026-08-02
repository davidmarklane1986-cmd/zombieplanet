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
/// </summary>
public static class ModelMatteLighting
{
    // URP Lit/Simple Lit local shader features: ENABLING the *_OFF keyword DISABLES the feature,
    // which is exactly what the material inspector does when you untick the toggle.
    const string SpecularHighlightsOffKeyword = "_SPECULARHIGHLIGHTS_OFF";
    const string EnvironmentReflectionsOffKeyword = "_ENVIRONMENTREFLECTIONS_OFF";

    /// <summary>
    /// Make <paramref name="m"/> shade matte/diffuse-only so it matches the planet terrain.
    /// Does NOT touch base colour, base map, normal map, alpha mode or emission, so textured
    /// look and any deliberate emissive effects (e.g. the gun muzzle flash) are preserved.
    /// </summary>
    public static void MakeMatte(Material m)
    {
        if (m == null)
            return;

        // ---- URP Lit / Simple Lit / Standard ----
        // Smoothness 0 + Metallic 0 -> broad, dim, fully-rough response (no tight highlight).
        if (m.HasProperty("_Smoothness"))
            m.SetFloat("_Smoothness", 0f);
        if (m.HasProperty("_Glossiness")) // legacy smoothness mirror some imports still write
            m.SetFloat("_Glossiness", 0f);
        if (m.HasProperty("_GlossMapScale")) // scales smoothness when a metallic/spec gloss map is bound
            m.SetFloat("_GlossMapScale", 0f);
        if (m.HasProperty("_Metallic"))
            m.SetFloat("_Metallic", 0f);

        // Kill the specular highlight entirely (terrain has none).
        if (m.HasProperty("_SpecularHighlights"))
        {
            m.SetFloat("_SpecularHighlights", 0f);
            m.EnableKeyword(SpecularHighlightsOffKeyword);
        }

        // Kill environment/reflection-probe reflections so the bright skybox can't over-brighten
        // the models; ambient still comes from SampleSH like the terrain.
        if (m.HasProperty("_EnvironmentReflections"))
        {
            m.SetFloat("_EnvironmentReflections", 0f);
            m.EnableKeyword(EnvironmentReflectionsOffKeyword);
        }
        if (m.HasProperty("_GlossyReflections")) // legacy env-reflection mirror
            m.SetFloat("_GlossyReflections", 0f);

        // ---- glTFast "glTF-pbrMetallicRoughness" shader graph (Kenney foliage GLBs) ----
        // It has no URP toggle keywords; roughnessFactor = 1 drives smoothness to 0, which both
        // removes the gloss highlight and collapses the reflection to the fully-blurred ambient mip.
        if (m.HasProperty("roughnessFactor"))
            m.SetFloat("roughnessFactor", 1f);
        if (m.HasProperty("metallicFactor"))
            m.SetFloat("metallicFactor", 0f);
    }
}
