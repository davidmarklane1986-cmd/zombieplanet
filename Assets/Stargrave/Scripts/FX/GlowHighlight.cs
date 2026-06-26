using UnityEngine;

[DisallowMultipleComponent]
public class GlowHighlight : MonoBehaviour
{
    [Header("Renderer Target")]
    public Renderer targetRenderer;

    [Header("Glow Settings")]
    public bool pulse = true;
    public float pulseSpeed = 4f;
    public float glowIntensity = 2.5f;

    [Tooltip("Emission color used for glow.")]
    public Color glowColor = Color.cyan;

    [Header("Optional: also tint base color slightly")]
    public bool tintBaseColor = false;
    public Color tintColor = Color.white;

    bool highlighted;

    static readonly int EmissionColorID = Shader.PropertyToID("_EmissionColor");
    static readonly int BaseColorID = Shader.PropertyToID("_BaseColor");   // URP Lit
    static readonly int ColorID = Shader.PropertyToID("_Color");           // Built-in fallback

    MaterialPropertyBlock mpb;

    void Awake()
    {
        if (targetRenderer == null)
            targetRenderer = GetComponentInChildren<Renderer>();

        mpb = new MaterialPropertyBlock();
        Apply(false, 0f);
    }

    void Update()
    {
        if (!highlighted) return;

        float t = pulse ? (0.5f + 0.5f * Mathf.Sin(Time.time * pulseSpeed)) : 1f;
        Apply(true, t);
    }

    public void SetHighlighted(bool on)
    {
        highlighted = on;

        if (!highlighted)
        {
            Apply(false, 0f);
        }
        else
        {
            Apply(true, pulse ? (0.5f + 0.5f * Mathf.Sin(Time.time * pulseSpeed)) : 1f);
        }
    }

    void Apply(bool on, float pulse01)
    {
        if (targetRenderer == null) return;

        targetRenderer.GetPropertyBlock(mpb);

        if (on)
        {
            // emission intensity scaled by pulse
            float intensity = Mathf.Lerp(0.6f, glowIntensity, pulse01);
            Color emissive = glowColor * intensity;

            mpb.SetColor(EmissionColorID, emissive);

            if (tintBaseColor)
            {
                // URP Lit uses _BaseColor, but some shaders use _Color
                mpb.SetColor(BaseColorID, tintColor);
                mpb.SetColor(ColorID, tintColor);
            }
        }
        else
        {
            // turn emission off
            mpb.SetColor(EmissionColorID, Color.black);

            // don't force base color when off; leave as-is
        }

        targetRenderer.SetPropertyBlock(mpb);

        // Ensure emission keyword is enabled on the material (URP Lit needs it)
        // This doesn't create instances; it changes the shared material keyword.
        // If you prefer not to touch shared materials, enable emission in the material manually.
        if (targetRenderer.sharedMaterial != null)
            targetRenderer.sharedMaterial.EnableKeyword("_EMISSION");
    }
}
