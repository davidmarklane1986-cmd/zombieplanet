Shader "Stargrave/Space Skybox"
{
    Properties
    {
        [Header(Background)]
        _TopColor ("Top Color", Color) = (0.012, 0.015, 0.035, 1)
        _BottomColor ("Bottom Color", Color) = (0.004, 0.005, 0.014, 1)
        _GradientPower ("Gradient Power", Range(0.1, 4)) = 1.0

        [Header(Stars)]
        _StarDensity ("Star Density", Range(1, 100)) = 42
        _StarBrightness ("Star Brightness", Range(0, 12)) = 4
        _StarSize ("Star Size", Range(0.005, 0.25)) = 0.09
        _StarColorVariance ("Star Color Variance", Range(0, 1)) = 0.6
        _StarColorBlue ("Star Tint Blue (cool)", Color) = (0.6, 0.75, 1.0, 1)
        _StarColorRed ("Star Tint Red (warm)", Color) = (1.0, 0.5, 0.4, 1)
        _StarBrightnessVariance ("Star Brightness Variance", Range(0, 1)) = 0.6
        _Twinkle ("Twinkle Amount", Range(0, 1)) = 0
        _TwinkleSpeed ("Twinkle Speed", Range(0, 10)) = 2

        [Header(Nebula)]
        _NebulaStrength ("Nebula Strength", Range(0, 2)) = 0.25
        _NebulaScale ("Nebula Scale", Range(0.2, 5)) = 1.4
        _NebulaColorA ("Nebula Color A", Color) = (0.15, 0.10, 0.35, 1)
        _NebulaColorB ("Nebula Color B", Color) = (0.00, 0.20, 0.25, 1)
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            fixed4 _TopColor;
            fixed4 _BottomColor;
            float _GradientPower;

            float _StarDensity;
            float _StarBrightness;
            float _StarSize;
            float _StarColorVariance;
            fixed4 _StarColorBlue;
            fixed4 _StarColorRed;
            float _StarBrightnessVariance;
            float _Twinkle;
            float _TwinkleSpeed;

            float _NebulaStrength;
            float _NebulaScale;
            fixed4 _NebulaColorA;
            fixed4 _NebulaColorB;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                // Skybox mesh is centered on the camera with identity rotation, so the
                // object-space vertex direction is the world view direction onto the sky sphere.
                o.dir = v.vertex.xyz;
                return o;
            }

            // Scalar hash in [0,1] from a 3D cell.
            float hash31(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            // Vector hash in [0,1]^3.
            float3 hash33(float3 p)
            {
                float3 q = float3(
                    dot(p, float3(127.1, 311.7, 74.7)),
                    dot(p, float3(269.5, 183.3, 246.1)),
                    dot(p, float3(113.5, 271.9, 124.6)));
                return frac(sin(q) * 43758.5453);
            }

            // Value noise.
            float vnoise(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);

                float n000 = hash31(i + float3(0, 0, 0));
                float n100 = hash31(i + float3(1, 0, 0));
                float n010 = hash31(i + float3(0, 1, 0));
                float n110 = hash31(i + float3(1, 1, 0));
                float n001 = hash31(i + float3(0, 0, 1));
                float n101 = hash31(i + float3(1, 0, 1));
                float n011 = hash31(i + float3(0, 1, 1));
                float n111 = hash31(i + float3(1, 1, 1));

                float nx00 = lerp(n000, n100, f.x);
                float nx10 = lerp(n010, n110, f.x);
                float nx01 = lerp(n001, n101, f.x);
                float nx11 = lerp(n011, n111, f.x);
                float nxy0 = lerp(nx00, nx10, f.y);
                float nxy1 = lerp(nx01, nx11, f.y);
                return lerp(nxy0, nxy1, f.z);
            }

            float fbm(float3 p)
            {
                float a = 0.0;
                float amp = 0.5;
                [unroll]
                for (int k = 0; k < 5; k++)
                {
                    a += amp * vnoise(p);
                    p *= 2.02;
                    amp *= 0.5;
                }
                return a;
            }

            // Crisp procedural starfield. Cell-based nearest-point so stars are round points,
            // 3x3x3 neighbourhood avoids seams at cell borders.
            float3 stars(float3 dir)
            {
                float3 result = float3(0, 0, 0);
                float3 p = dir * _StarDensity;
                float3 baseCell = floor(p);

                // Only a fraction of cells contain a star (keeps the field sparse and crisp).
                const float coverage = 0.12;
                float radius = max(_StarSize, 1e-3);

                [unroll]
                for (int x = -1; x <= 1; x++)
                {
                    [unroll]
                    for (int y = -1; y <= 1; y++)
                    {
                        [unroll]
                        for (int z = -1; z <= 1; z++)
                        {
                            float3 cell = baseCell + float3(x, y, z);
                            float3 rnd = hash33(cell);

                            float exist = step(1.0 - coverage, rnd.x);
                            float3 starPos = cell + float3(rnd.y, rnd.z, frac(rnd.x * 97.13));

                            float d = length(p - starPos);
                            float core = saturate(1.0 - d / radius);
                            // Crisp point with an HDR-bright centre; exponent kept fairly high so stars stay tight.
                            float intensity = pow(core, 5.0) * exist;

                            // Floor keeps faint (high-variance) stars perceptible instead of vanishing to ~0.
                            float bvar = lerp(1.0, 0.5 + 0.5 * frac(rnd.x * 53.17), _StarBrightnessVariance);

                            // Two-sided stellar palette: ~25% lean blue (hot), ~25% lean red (cool),
                            // ~50% near white in the middle so the field reads naturally.
                            float t = frac(rnd.y * 31.7);
                            float3 white = float3(1, 1, 1);
                            float3 hue = (t < 0.5)
                                ? lerp(_StarColorBlue.rgb, white, saturate(t * 2.0))
                                : lerp(white, _StarColorRed.rgb, saturate((t - 0.5) * 2.0));
                            float3 tint = lerp(white, hue, _StarColorVariance);

                            float tw = 1.0 + _Twinkle * sin(_Time.y * _TwinkleSpeed + rnd.z * 6.2831853);

                            result += tint * (intensity * bvar * _StarBrightness * tw);
                        }
                    }
                }
                return result;
            }

            half4 frag(v2f i) : SV_Target
            {
                float3 dir = normalize(i.dir);

                // Vertical background gradient.
                float g = saturate(dir.y * 0.5 + 0.5);
                g = pow(g, _GradientPower);
                float3 col = lerp(_BottomColor.rgb, _TopColor.rgb, g);

                // Faint nebula concentrated along a tilted Milky-Way band.
                if (_NebulaStrength > 0.0001)
                {
                    float neb = fbm(dir * _NebulaScale + 13.0);
                    neb = pow(saturate(neb), 2.0);

                    float3 bandNormal = normalize(float3(0.2, 1.0, 0.35));
                    float band = 1.0 - abs(dot(dir, bandNormal));
                    band = pow(saturate(band), 3.0);

                    float mixT = fbm(dir * _NebulaScale * 0.5 + 5.0);
                    float3 nebCol = lerp(_NebulaColorA.rgb, _NebulaColorB.rgb, saturate(mixT));
                    col += nebCol * (neb * band * _NebulaStrength);
                }

                // Additive crisp stars (HDR: bright cores can exceed 1.0 to pop and catch bloom).
                col += stars(dir);

                return half4(col, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
