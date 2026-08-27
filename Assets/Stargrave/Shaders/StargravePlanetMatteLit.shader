Shader "Stargrave/Planet Matte Lit"
{
    Properties
    {
        [MainTexture] _BaseMap ("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor ("Color", Color) = (1, 1, 1, 1)
        [HideInInspector] _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        [HideInInspector] _MatchTerrainSun ("Match Terrain Sun", Float) = 0
        [HideInInspector] _AmbientFill ("Ambient Fill", Float) = 1
        [HideInInspector] _DiffuseScale ("Diffuse Scale", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }
        LOD 200

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RealtimeLights.hlsl"
            #include "StargraveSphericalLight.hlsl"
            #include "StargraveAssetAdditionalLights.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _MatchTerrainSun;
                half _AmbientFill;
                half _DiffuseScale;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normInputs = GetVertexNormalInputs(input.normalOS);
                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS = normInputs.normalWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half3 albedo = baseSample.rgb * _BaseColor.rgb;

                float3 N = normalize(input.normalWS);
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half NdotL = _MatchTerrainSun > 0.5h
                    ? StargraveAssetNdotLMatchTerrain(input.positionWS, N, mainLight.direction)
                    : StargraveAssetNdotL(input.positionWS, N, mainLight.direction);
                half3 col = albedo * mainLight.color * (NdotL * mainLight.shadowAttenuation)
                    * max(_DiffuseScale, 0.0h);
                // Shade-side fill. Foliage raises this; player lowers it — meet in the middle.
                half3 ambient = SampleSH(N);
                half ambFill = max(_AmbientFill, 0.0h);
                if (_MatchTerrainSun > 0.5h && dot(_PlanetCenterWS, _PlanetCenterWS) >= 1e-4)
                {
                    float3 radialUp = normalize(input.positionWS - _PlanetCenterWS);
                    half groundSun = saturate(dot(radialUp, mainLight.direction));
                    // Track dawn/dusk: grass should dim with daylight, not stay hot in the band.
                    half ambScale = lerp(0.22h, 1.0h, smoothstep(0.02h, 0.45h, groundSun));
                    ambient *= ambScale;
                    // Tree / building cast shadows: crush fill so the carpet doesn't glow under canopy.
                    ambient *= lerp(0.22h, 1.0h, mainLight.shadowAttenuation);
                }
                col += albedo * ambient * ambFill;
                col += StargraveApplyAdditionalLightsForAssets(
                    input.positionWS, N, GetNormalizedScreenSpaceUV(input.positionCS), albedo);

                half fogFactor = InitializeInputDataFog(float4(input.positionWS, 1.0), 0);
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
            #pragma target 2.0
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }
    }

    FallBack Off
}
