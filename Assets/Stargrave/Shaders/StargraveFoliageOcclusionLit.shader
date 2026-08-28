Shader "Stargrave/Foliage Occlusion Lit"
{
    Properties
    {
        [MainTexture] baseColorTexture ("Albedo", 2D) = "white" {}
        [MainColor] baseColorFactor ("Color", Color) = (1, 1, 1, 1)
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0
        [HideInInspector] _MatchTerrainSun ("Match Terrain Sun", Float) = 1
        [HideInInspector] _AmbientFill ("Ambient Fill", Float) = 1
        [HideInInspector] _DiffuseScale ("Diffuse Scale", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }
        LOD 200

        // Keep depth everywhere except inside the circle. The colour pass also skips the
        // fully transparent centre so it cannot leave depth behind there.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            Cull [_Cull]
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "ScreenCircleOcclusion.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 baseColorTexture_ST;
                half4 baseColorFactor;
                half _Cutoff;
            CBUFFER_END

            TEXTURE2D(baseColorTexture);
            SAMPLER(sampler_baseColorTexture);

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            DepthVaryings DepthOnlyVertex(DepthAttributes input)
            {
                DepthVaryings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.uv = TRANSFORM_TEX(input.uv, baseColorTexture);
                return output;
            }

            half4 DepthOnlyFragment(DepthVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half4 baseSample = SAMPLE_TEXTURE2D(baseColorTexture, sampler_baseColorTexture, input.uv);
                clip(baseSample.a * baseColorFactor.a - _Cutoff);
                float holeCoverage = StargraveScreenCircleOcclusionCoverage(
                    input.positionWS,
                    input.positionCS);
                // Keep depth only outside the fade band. Writing depth through the
                // blended edge makes the player pop out against a hard boundary.
                clip(-holeCoverage);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            // Keep depth for the visible foliage so the selected renderer cannot
            // vanish behind later transparent sorting. The centre is discarded
            // below before this pass can write depth.
            ZWrite On
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

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
                float4 baseColorTexture_ST;
                half4 baseColorFactor;
                half _Cutoff;
                half _MatchTerrainSun;
                half _AmbientFill;
                half _DiffuseScale;
            CBUFFER_END

            #include "ScreenCircleOcclusion.hlsl"

            TEXTURE2D(baseColorTexture);
            SAMPLER(sampler_baseColorTexture);

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
                output.uv = TRANSFORM_TEX(input.uv, baseColorTexture);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float holeCoverage = StargraveScreenCircleOcclusionCoverage(
                    input.positionWS,
                    input.positionCS);
                // Do not write depth for the fully transparent centre of the hole.
                clip(1.0h - holeCoverage - 0.001h);
                half4 baseSample = SAMPLE_TEXTURE2D(baseColorTexture, sampler_baseColorTexture, input.uv);
                clip(baseSample.a * baseColorFactor.a - _Cutoff);

                half3 albedo = baseSample.rgb * baseColorFactor.rgb;
                float3 N = normalize(input.normalWS);
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half NdotL = _MatchTerrainSun > 0.5h
                    ? StargraveAssetNdotLMatchTerrain(input.positionWS, N, mainLight.direction)
                    : StargraveAssetNdotL(input.positionWS, N, mainLight.direction);
                // Keep the source foliage hue independent from the scene light colour. The
                // imported glTF trees are authored with their own albedo colours; applying a
                // blue moonlight tint here makes only the selected trees change colour.
                half mainLightLuma = dot(mainLight.color.rgb, half3(0.2126h, 0.7152h, 0.0722h));
                half3 col = albedo * mainLightLuma * (NdotL * mainLight.shadowAttenuation)
                    * max(_DiffuseScale, 0.0h);
                half3 ambient = SampleSH(N);
                half ambFill = max(_AmbientFill, 0.0h);
                if (_MatchTerrainSun > 0.5h && dot(_PlanetCenterWS, _PlanetCenterWS) >= 1e-4)
                {
                    float3 radialUp = normalize(input.positionWS - _PlanetCenterWS);
                    half groundSun = saturate(dot(radialUp, mainLight.direction));
                    half ambScale = lerp(0.22h, 1.0h, smoothstep(0.02h, 0.45h, groundSun));
                    ambient *= ambScale;
                    ambient *= lerp(0.22h, 1.0h, mainLight.shadowAttenuation);
                }
                half ambientLuma = dot(ambient, half3(0.2126h, 0.7152h, 0.0722h));
                col += albedo * ambientLuma * ambFill;
                half fogFactor = InitializeInputDataFog(float4(input.positionWS, 1), 0);
                col = MixFog(col, fogFactor);
                return half4(col, 1.0h - holeCoverage);
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
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 baseColorTexture_ST;
                half4 baseColorFactor;
                half _Cutoff;
                half _MatchTerrainSun;
                half _AmbientFill;
                half _DiffuseScale;
            CBUFFER_END

            #include "ScreenCircleOcclusion.hlsl"

            TEXTURE2D(baseColorTexture);
            SAMPLER(sampler_baseColorTexture);

            float3 _LightDirection;
            float3 _LightPosition;

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
                float3 positionWS : TEXCOORD1;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float4 GetShadowPositionHClip(Attributes input)
            {
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return positionCS;
            }

            Varyings ShadowPassVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.uv = TRANSFORM_TEX(input.uv, baseColorTexture);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = GetShadowPositionHClip(input);
                return output;
            }

            half4 ShadowPassFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half4 baseSample = SAMPLE_TEXTURE2D(baseColorTexture, sampler_baseColorTexture, input.uv);
                clip(baseSample.a * baseColorFactor.a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
