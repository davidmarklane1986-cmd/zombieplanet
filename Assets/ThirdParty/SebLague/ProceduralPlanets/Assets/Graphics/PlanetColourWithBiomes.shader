Shader "ProceduralPlanets/Planet Colour With Biomes"
{
    Properties
    {
        _elevationMinMax ("Elevation Min Max", Vector) = (0, 1, 0, 0)
        _texture ("Planet Texture (lookup)", 2D) = "white" {}
        _BiomeLookup ("Biome Lookup (lat/long)", 2D) = "white" {}
        [Header(Detail Texture)]
        _DetailTex ("Detail Texture", 2D) = "white" {}
        _DetailTiling ("Detail Tiling (X Z)", Vector) = (4, 4, 0, 0)
        _DetailStrength ("Detail Strength", Range(0, 1)) = 0
        [Header(Shadows)]
        _ShadowMin ("Shadow Min Brightness", Range(0, 1)) = 0.4
        _ShadowFadeStrength ("Shadow Fade At Distance", Range(0, 1)) = 0.5
        _ShadowHardness ("Shadow Hardness", Range(0, 1)) = 0
        _NightDarkness ("Night Side Darkness", Range(0, 1)) = 0.03
        _TerminatorSoftness ("Terminator Softness (match atmosphere)", Range(0.01, 0.6)) = 0.35
        [Header(Biome boundaries)]
        _BiomeBoundaryBlur ("Texture Boundary Blur (fade width)", Range(0, 0.15)) = 0.03
        _BiomeOverlapWidth ("Texture Overlap Width (blend band)", Range(0.05, 0.5)) = 0.25
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RealtimeLights.hlsl"

            float4 _elevationMinMax;
            float4 _TextureTiling;
            float4 _DetailTiling;
            float _BiomeCount;
            float _MaxGradientKeys;
            float _UseGradientKeyTextures;
            float _DetailStrength;
            float _ShadowMin;
            float _ShadowFadeStrength;
            float _ShadowHardness;
            float _NightDarkness;
            float _TerminatorSoftness;
            float _UseBiomeLookup;
            float _BiomeBoundaryBlur;
            float _BiomeOverlapWidth;
            TEXTURE2D(_texture);
            SAMPLER(sampler_texture);
            TEXTURE2D(_BiomeLookup);
            SAMPLER(sampler_BiomeLookup);
            TEXTURE2D_ARRAY(_BiomeTextures);
            SAMPLER(sampler_BiomeTextures);
            TEXTURE2D(_KeyPositions);
            SAMPLER(sampler_KeyPositions);
            TEXTURE2D(_DetailTex);
            SAMPLER(sampler_DetailTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv0 : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionOS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float2 uv0 : TEXCOORD2;
                float3 normalWS : TEXCOORD3;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.positionOS = v.positionOS.xyz;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.uv0 = v.uv0;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 dir = normalize(i.positionOS);
                float2 sphericalUV;
                sphericalUV.x = (atan2(dir.x, dir.z) / 6.28318531) + 0.5;
                sphericalUV.y = (asin(clamp(dir.y, -1.0, 1.0)) / 3.14159265) + 0.5;
                float biomePercent;
                if (_UseBiomeLookup >= 0.5)
                {
                    float r = _BiomeBoundaryBlur;
                    if (r <= 0.0)
                    {
                        biomePercent = saturate(SAMPLE_TEXTURE2D(_BiomeLookup, sampler_BiomeLookup, sphericalUV).r);
                    }
                    else
                    {
                        float2 uv = sphericalUV;
                        float s = SAMPLE_TEXTURE2D(_BiomeLookup, sampler_BiomeLookup, uv).r;
                        s += SAMPLE_TEXTURE2D(_BiomeLookup, sampler_BiomeLookup, uv + float2( r, 0)).r;
                        s += SAMPLE_TEXTURE2D(_BiomeLookup, sampler_BiomeLookup, uv + float2(-r, 0)).r;
                        s += SAMPLE_TEXTURE2D(_BiomeLookup, sampler_BiomeLookup, uv + float2(0, r)).r;
                        s += SAMPLE_TEXTURE2D(_BiomeLookup, sampler_BiomeLookup, uv + float2(0,-r)).r;
                        s += SAMPLE_TEXTURE2D(_BiomeLookup, sampler_BiomeLookup, uv + float2( r, r)).r;
                        s += SAMPLE_TEXTURE2D(_BiomeLookup, sampler_BiomeLookup, uv + float2(-r, r)).r;
                        s += SAMPLE_TEXTURE2D(_BiomeLookup, sampler_BiomeLookup, uv + float2( r,-r)).r;
                        s += SAMPLE_TEXTURE2D(_BiomeLookup, sampler_BiomeLookup, uv + float2(-r,-r)).r;
                        biomePercent = saturate(s / 9.0);
                    }
                }
                else
                {
                    biomePercent = saturate(i.uv0.x);
                }

                float elevation = length(i.positionOS);
                float elevationNorm = saturate((elevation - _elevationMinMax.x) / (_elevationMinMax.y - _elevationMinMax.x + 1e-5));
                float2 lookupUV = float2(elevationNorm, biomePercent);
                half3 col = SAMPLE_TEXTURE2D(_texture, sampler_texture, lookupUV).rgb;

                float biomeIndexFloat = biomePercent * max(1.0, _BiomeCount - 1);
                int biomeIndex0 = (int)clamp(floor(biomeIndexFloat), 0.0, _BiomeCount - 1);
                int biomeIndex1 = (int)min(biomeIndex0 + 1, _BiomeCount - 1);
                float fracVal = frac(biomeIndexFloat);
                float w = max(0.05, _BiomeOverlapWidth);
                float blend = smoothstep(0.5 - w, 0.5 + w, fracVal);

                int slice0, slice1;
                if (_UseGradientKeyTextures >= 0.5 && _MaxGradientKeys >= 1.0)
                {
                    int keyIndex0 = 0, keyIndex1 = 0;
                    for (int k = 0; k < 8; k++)
                    {
                        if (k >= (int)_MaxGradientKeys) break;
                        float2 keyUV0 = float2((float)(k) + 0.5, (float)(biomeIndex0) + 0.5);
                        keyUV0.x /= _MaxGradientKeys;
                        keyUV0.y /= _BiomeCount;
                        float keyTime0 = SAMPLE_TEXTURE2D(_KeyPositions, sampler_KeyPositions, keyUV0).r;
                        if (elevationNorm >= keyTime0) keyIndex0 = k;
                        float2 keyUV1 = float2((float)(k) + 0.5, (float)(biomeIndex1) + 0.5);
                        keyUV1.x /= _MaxGradientKeys;
                        keyUV1.y /= _BiomeCount;
                        float keyTime1 = SAMPLE_TEXTURE2D(_KeyPositions, sampler_KeyPositions, keyUV1).r;
                        if (elevationNorm >= keyTime1) keyIndex1 = k;
                    }
                    int totalSlices = (int)(_BiomeCount * _MaxGradientKeys);
                    slice0 = clamp(biomeIndex0 * (int)_MaxGradientKeys + keyIndex0, 0, totalSlices - 1);
                    slice1 = clamp(biomeIndex1 * (int)_MaxGradientKeys + keyIndex1, 0, totalSlices - 1);
                }
                else
                {
                    slice0 = clamp(biomeIndex0, 0, (int)_BiomeCount - 1);
                    slice1 = clamp(biomeIndex1, 0, (int)_BiomeCount - 1);
                }
                float2 tiledUV = frac(sphericalUV * _TextureTiling.xy);
                half3 biomeTex0 = SAMPLE_TEXTURE2D_ARRAY(_BiomeTextures, sampler_BiomeTextures, tiledUV, slice0).rgb;
                half3 biomeTex1 = SAMPLE_TEXTURE2D_ARRAY(_BiomeTextures, sampler_BiomeTextures, tiledUV, slice1).rgb;
                half3 biomeTex = lerp(biomeTex0, biomeTex1, blend);
                col = col * biomeTex;

                float2 detailUV = frac(sphericalUV * _DetailTiling.xy);
                half3 detail = SAMPLE_TEXTURE2D(_DetailTex, sampler_DetailTex, detailUV).rgb;
                col = lerp(col, col * detail, _DetailStrength);

                // --- URP day/night lighting: bright, even daytime (original look); night falls to _NightDarkness ---
                // 'col' so far is the biome albedo (unchanged).
                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float3 N = normalize(i.normalWS);
                half NdotL = dot(N, mainLight.direction);

                // Day/night terminator. The night hemisphere (N.L <= 0, including the terminator itself) is
                // fully dark at _NightDarkness, exactly like the approved night look. The day side then ramps
                // smoothly up to full albedo across the lit limb over a band of width _TerminatorSoftness, so
                // the dark edge sits right at the geometric terminator and still reads as one with the
                // atmosphere's limb glow (which lingers over the now-dark night ground near the terminator).
                half f = smoothstep(0.0, _TerminatorSoftness, NdotL);

                // Cast-shadow softening still driven by the existing material knobs.
                half shadowAtten = mainLight.shadowAttenuation;
                half sharpen = 1.0 + _ShadowHardness * 4.0;
                shadowAtten = saturate((shadowAtten - 0.5) * sharpen + 0.5);
                half shadowFactor = lerp(_ShadowMin, 1.0, shadowAtten);
                half shadowFade = GetMainLightShadowFade(i.positionWS) * _ShadowFadeStrength;
                shadowFactor = lerp(shadowFactor, 1.0, shadowFade);

                // Bounded day/night in [_NightDarkness .. 1]: the fully lit side reproduces the original
                // albedo exactly (no intensity-2 blow-out, no light-colour tint); night side == _NightDarkness.
                half dayNight = lerp(_NightDarkness, 1.0, f * shadowFactor);
                col *= dayNight;

                return half4(col, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }
    }
    FallBack "ProceduralPlanets/Planet Colour"
}
