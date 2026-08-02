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
        [Header(Stochastic Texturing)]
        [Toggle] _UseStochastic ("Use Stochastic Texturing", Float) = 1
        _StochasticContrast ("Stochastic Blend Contrast", Range(1, 8)) = 4
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
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RealtimeLights.hlsl"
            #include "StochasticSampling.hlsl"
            #include "Assets/Stargrave/Shaders/Water/StargraveAdditionalLights.hlsl"

            float4 _elevationMinMax;
            float4 _TextureTiling;
            float4 _DetailTiling;
            float _BiomeCount;
            float _MaxGradientKeys;
            float _UseGradientKeyTextures;
            float _DetailStrength;
            float _UseBiomeLookup;
            float _BiomeBoundaryBlur;
            float _BiomeOverlapWidth;
            float _UseStochastic;
            float _StochasticContrast;
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
                // Continuous UVs (no frac) so stochastic offsets wrap via the sampler.
                float2 tiledUV = sphericalUV * _TextureTiling.xy;
                float stochContrast = max(_StochasticContrast, 1.0);
                half3 biomeTex0, biomeTex1;
                if (_UseStochastic >= 0.5)
                {
                    biomeTex0 = SampleStochastic2DArray(TEXTURE2D_ARRAY_ARGS(_BiomeTextures, sampler_BiomeTextures), tiledUV, slice0, stochContrast);
                    biomeTex1 = SampleStochastic2DArray(TEXTURE2D_ARRAY_ARGS(_BiomeTextures, sampler_BiomeTextures), tiledUV, slice1, stochContrast);
                }
                else
                {
                    biomeTex0 = SAMPLE_TEXTURE2D_ARRAY(_BiomeTextures, sampler_BiomeTextures, tiledUV, slice0).rgb;
                    biomeTex1 = SAMPLE_TEXTURE2D_ARRAY(_BiomeTextures, sampler_BiomeTextures, tiledUV, slice1).rgb;
                }
                half3 biomeTex = lerp(biomeTex0, biomeTex1, blend);
                col = col * biomeTex;

                float2 detailUV = sphericalUV * _DetailTiling.xy;
                half3 detail = (_UseStochastic >= 0.5 && _DetailStrength > 1e-4)
                    ? SampleStochastic2D(TEXTURE2D_ARGS(_DetailTex, sampler_DetailTex), detailUV, stochContrast)
                    : SAMPLE_TEXTURE2D(_DetailTex, sampler_DetailTex, detailUV).rgb;
                col = lerp(col, col * detail, _DetailStrength);

                // --- Natural URP lighting ---
                // 'col' so far is the procedural biome albedo (unchanged). It is now lit by the real main
                // directional light (the sun) plus environment ambient, exactly like a URP/Lit surface, so
                // the day/night terminator falls out of N.L against the sun direction (shared by the ocean
                // and atmosphere) instead of a hand-authored darkening curve. The night side falls dark
                // naturally, with SampleSH(N) providing a small ambient floor so it is never pure black.
                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float3 N = normalize(i.normalWS);
                half NdotL = saturate(dot(N, mainLight.direction));

                half3 diffuse = col * mainLight.color * NdotL * mainLight.shadowAttenuation;
                half3 ambient = col * SampleSH(N);
                half3 additional = StargraveApplyAdditionalLights(
                    i.positionWS, N, GetNormalizedScreenSpaceUV(i.positionCS), col);
                col = diffuse + ambient + additional;
                half fogFactor = InitializeInputDataFog(float4(i.positionWS, 1.0), 0);
                col = MixFog(col, fogFactor);

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
