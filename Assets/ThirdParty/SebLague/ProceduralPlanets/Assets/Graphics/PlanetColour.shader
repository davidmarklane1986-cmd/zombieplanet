Shader "ProceduralPlanets/Planet Colour"
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
            float4 _DetailTiling;
            float _DetailStrength;
            float _UseBiomeLookup;
            float _UseStochastic;
            float _StochasticContrast;
            TEXTURE2D(_texture);
            SAMPLER(sampler_texture);
            TEXTURE2D(_BiomeLookup);
            SAMPLER(sampler_BiomeLookup);
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
                float biomePercent = _UseBiomeLookup >= 0.5
                    ? saturate(SAMPLE_TEXTURE2D(_BiomeLookup, sampler_BiomeLookup, sphericalUV).r)
                    : saturate(i.uv0.x);

                float elevation = length(i.positionOS);
                float elevationNorm = saturate((elevation - _elevationMinMax.x) / (_elevationMinMax.y - _elevationMinMax.x + 1e-5));
                float2 lookupUV = float2(elevationNorm, biomePercent);
                half3 col = SAMPLE_TEXTURE2D(_texture, sampler_texture, lookupUV).rgb;

                float2 detailUV = sphericalUV * _DetailTiling.xy;
                half3 detail = (_UseStochastic >= 0.5 && _DetailStrength > 1e-4)
                    ? SampleStochastic2D(TEXTURE2D_ARGS(_DetailTex, sampler_DetailTex), detailUV, max(_StochasticContrast, 1.0))
                    : SAMPLE_TEXTURE2D(_DetailTex, sampler_DetailTex, detailUV).rgb;
                col = lerp(col, col * detail, _DetailStrength);

                // --- Natural URP lighting ---
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
    FallBack "Universal Render Pipeline/Lit"
}
