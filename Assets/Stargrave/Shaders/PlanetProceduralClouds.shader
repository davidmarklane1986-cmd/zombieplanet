Shader "Stargrave/Procedural Planet Clouds"
{
    Properties
    {
        _CloudColor ("Cloud Color", Color) = (0.92, 0.94, 1, 1)
        _Coverage ("Coverage", Range(0, 1)) = 0.58
        _Density ("Density", Range(0.05, 2)) = 1
        _CloudScale ("Cloud Scale", Float) = 55
        _WeatherScale ("Weather Scale", Float) = 300
        _DetailScale ("Detail Scale", Float) = 18
        _Erosion ("Erosion", Range(0, 1)) = 0.38
        _FormationStrength ("Formation Strength", Range(0, 1)) = 0.72
        _MediumDetail ("Medium Detail", Range(0, 1)) = 0.62
        _SmallDetail ("Small Detail", Range(0, 1)) = 0.35
        _CellularBreakup ("Cellular Breakup", Range(0, 1)) = 0.58
        _CellularScale ("Cellular Scale", Range(0.5, 4)) = 1.65
        _WarpStrength ("Warp Strength", Range(0, 1)) = 0.28
        _VerticalProfile ("Vertical Profile", Range(0.1, 4)) = 1.35
        _SunIntensity ("Sun Intensity", Range(0, 4)) = 1.1
        _SilverLining ("Silver Lining", Range(0, 2)) = 0.34
        _NightIllumination ("Night Illumination", Range(0, 0.5)) = 0.02
        _InteriorDarkness ("Interior Darkness", Range(0, 1)) = 0.52
        _ShadowStrength ("Shadow Strength", Range(0, 1)) = 0.62
        _ShadowSoftness ("Shadow Softness", Range(0, 1)) = 0.62
        _CloudBaseNoise ("Cloud Base 3D Noise", 3D) = "" {}
        _CloudDetailNoise ("Cloud Detail 3D Noise", 3D) = "" {}
        _CloudShadowMap ("Cloud Spherical Shadow Map", 2D) = "black" {}
        [HideInInspector] _PlanetCenterWS ("Planet Center", Vector) = (0, 0, 0, 0)
        [HideInInspector] _CloudInnerRadius ("Cloud Inner Radius", Float) = 425
        [HideInInspector] _CloudOuterRadius ("Cloud Outer Radius", Float) = 451
        [HideInInspector] _CloudSeed ("Cloud Seed", Float) = 18473
        [HideInInspector] _WindDirection ("Wind Direction", Vector) = (1, 0, 0, 0)
        [HideInInspector] _LayerWindSpeeds ("Layer Wind Speeds", Vector) = (1, 2.5, 3.4, 0.22)
        [HideInInspector] _CloudTime ("Cloud Time", Float) = 0
        [HideInInspector] _CloudSunDirection ("Cloud Sun Direction", Vector) = (0, 1, 0, 0)
        [HideInInspector] _CloudSunColor ("Cloud Sun Color", Color) = (1, 1, 1, 1)
        [HideInInspector] _CloudMoonDirection ("Cloud Moon Direction", Vector) = (0, 1, 0, 0)
        [HideInInspector] _CloudMoonColor ("Cloud Moon Color", Color) = (0.62, 0.72, 0.95, 1)
        [HideInInspector] _CloudMoonAmount ("Cloud Moon Amount", Float) = 0
        [HideInInspector] _ShadowQuality ("Shadow Quality", Float) = 1
        [HideInInspector] _SampleCount ("Sample Count", Float) = 30
        [HideInInspector] _LightSamples ("Light Samples", Float) = 2
        [HideInInspector] _DistanceLod ("Distance LOD", Float) = 0
        [HideInInspector] _DebugMode ("Debug Mode", Float) = 0
        [HideInInspector] _Twilight ("Twilight", Float) = 0
        [HideInInspector] _DayAmount ("Day Amount", Float) = 1
        [HideInInspector] _NightAmount ("Night Amount", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "CloudVolume"
            Tags { "LightMode" = "UniversalForward" }

            // The raymarch accumulates premultiplied colour front-to-back.
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE3D(_CloudBaseNoise);
            SAMPLER(sampler_CloudBaseNoise);
            TEXTURE3D(_CloudDetailNoise);
            SAMPLER(sampler_CloudDetailNoise);
            TEXTURE2D(_CloudShadowMap);
            SAMPLER(sampler_CloudShadowMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _CloudColor;
                half _Coverage;
                half _Density;
                half _CloudScale;
                half _DetailScale;
                half _Erosion;
                half _FormationStrength;
                half _MediumDetail;
                half _SmallDetail;
                half _CellularBreakup;
                half _CellularScale;
                half _WarpStrength;
                half _VerticalProfile;
                half _SunIntensity;
                half _SilverLining;
                half _NightIllumination;
                half _InteriorDarkness;
                half _ShadowStrength;
                half _ShadowSoftness;
                float3 _PlanetCenterWS;
                float _CloudInnerRadius;
                float _CloudOuterRadius;
                float _CloudSeed;
                float3 _WindDirection;
                float4 _LayerWindSpeeds;
                float _CloudTime;
                float3 _CloudSunDirection;
                float4 _CloudSunColor;
                float3 _CloudMoonDirection;
                float4 _CloudMoonColor;
                float _CloudMoonAmount;
                float _ShadowQuality;
                float _SampleCount;
                float _LightSamples;
                float _DistanceLod;
                float _DebugMode;
                float _Twilight;
                float _DayAmount;
                float _NightAmount;
                float _WeatherScale;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            #include "PlanetProceduralCloudsCommon.hlsl"

            float CloudDensity(float3 worldPosition)
            {
                return PlanetCloudDensity(worldPosition);
            }

            float2 RaySphere(float3 rayOrigin, float3 rayDirection, float3 center, float radius)
            {
                float3 offset = rayOrigin - center;
                float b = dot(offset, rayDirection);
                float c = dot(offset, offset) - radius * radius;
                float h = b * b - c;
                if (h < 0.0)
                    return float2(1.0, 0.0);
                h = sqrt(h);
                return float2(-b - h, -b + h);
            }

            float CloudShadowDensity(float3 worldPosition)
            {
                float3 radial = normalize(worldPosition - _PlanetCenterWS);
                float middleRadius = (_CloudInnerRadius + _CloudOuterRadius) * 0.5;
                return CloudDensity(_PlanetCenterWS + radial * middleRadius);
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 rayOrigin = _WorldSpaceCameraPos;
                float3 rayDirection = normalize(input.positionWS - rayOrigin);
                float3 centerToSurface = input.positionWS - _PlanetCenterWS;
                float radiusAtSurface = length(centerToSurface);
                float3 radial = centerToSurface / max(radiusAtSurface, 0.001);
                bool cameraOutside = length(rayOrigin - _PlanetCenterWS) >= _CloudOuterRadius;

                // One face of the proxy sphere is enough. This avoids drawing the raymarch twice
                // while still selecting the outward intersection when the camera is inside the shell.
                float facing = dot(radial, rayDirection);
                if ((cameraOutside && facing > 0.0) || (!cameraOutside && facing < 0.0))
                    discard;

                float2 outerHit = RaySphere(rayOrigin, rayDirection, _PlanetCenterWS, _CloudOuterRadius);
                float marchStart = max(0.0, outerHit.x);
                float marchEnd = outerHit.y;
                if (marchEnd <= marchStart)
                    discard;

                int sampleCount = clamp((int)_SampleCount, 4, 96);
                float stepLength = (marchEnd - marchStart) / sampleCount;
                float transmittance = 1.0;
                float3 result = 0.0;
                float densitySum = 0.0;
                float3 sunDirection = normalize(_CloudSunDirection);
                float3 moonDirection = normalize(_CloudMoonDirection);
                float3 viewDirection = normalize(rayOrigin - input.positionWS);

                [loop]
                for (int i = 0; i < 96; i++)
                {
                    if (i >= sampleCount || transmittance < 0.012)
                        break;

                    float t = marchStart + (i + 0.5) * stepLength;
                    float3 samplePosition = rayOrigin + rayDirection * t;
                    float sampleRadius = length(samplePosition - _PlanetCenterWS);
                    if (sampleRadius <= _CloudInnerRadius || sampleRadius >= _CloudOuterRadius)
                        continue;

                    float sampleDensity = CloudDensity(samplePosition);
                    if (sampleDensity <= 0.001)
                        continue;

                    float lightTransmission = 1.0;
                    int lightSamples = clamp((int)_LightSamples, 1, 3);
                    [unroll]
                    for (int l = 1; l <= 3; l++)
                    {
                        if (l > lightSamples)
                            break;
                        float3 lightPoint = samplePosition + sunDirection *
                            (l / (float)(lightSamples + 1)) * (_CloudOuterRadius - _CloudInnerRadius) * 0.9;
                        float lightDensity = CloudDensity(lightPoint);
                        lightTransmission *= 1.0 - saturate(lightDensity * 0.32);
                    }

                    float sunFacing = saturate(dot(normalize(samplePosition - _PlanetCenterWS), sunDirection));
                    float backlit = pow(saturate(dot(-viewDirection, sunDirection)), 4.0);
                    float twilightTint = saturate(_Twilight) * (0.35 + 0.65 * sunFacing);
                    float3 warmSun = lerp(_CloudColor.rgb, _CloudSunColor.rgb, twilightTint * 0.65);
                    float direct = _SunIntensity * saturate(_DayAmount) *
                        (0.38 + 0.72 * sunFacing) * lightTransmission;
                    float interior = lerp(1.0, 1.0 - _InteriorDarkness, sampleDensity);
                    float3 dayLight = warmSun * direct * interior;
                    float3 silver = _CloudSunColor.rgb * backlit * _SilverLining *
                        saturate(_DayAmount) * (0.35 + 0.65 * lightTransmission);
                    float3 moonLight = _CloudMoonColor.rgb * _CloudMoonAmount *
                        saturate(dot(normalize(samplePosition - _PlanetCenterWS), moonDirection) * 0.5 + 0.5);
                    float3 nightLight = _CloudColor.rgb * (0.008 + _NightIllumination) +
                        moonLight * 0.18;
                    float3 shaded = lerp(nightLight, dayLight + silver, saturate(_DayAmount));
                    shaded = max(shaded, 0.0);

                    float sampleAlpha = saturate(sampleDensity * stepLength /
                        max((_CloudOuterRadius - _CloudInnerRadius) * 0.42, 0.01));
                    sampleAlpha = saturate(sampleAlpha * (1.05 - 0.35 * _DistanceLod));
                    result += shaded * (sampleAlpha * transmittance);
                    transmittance *= 1.0 - sampleAlpha;
                    densitySum += sampleDensity * sampleAlpha;
                }

                float alpha = saturate(1.0 - transmittance);
                if (_DebugMode > 1.5)
                {
                    float shadow = saturate(densitySum * _ShadowStrength);
                    return half4(shadow.xxx, shadow);
                }
                if (_DebugMode > 0.5)
                    return half4(saturate(densitySum).xxx, alpha);

                // Keep the shell naturally subordinate to the existing atmosphere pass.
                return half4(result, alpha);
            }
            ENDHLSL
        }

        Pass
        {
            Name "CloudFullscreen"
            Tags { "LightMode" = "Always" }

            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex FullscreenVert
            #pragma fragment FullscreenFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D_X(_BlitTexture);
            TEXTURE3D(_CloudBaseNoise);
            SAMPLER(sampler_CloudBaseNoise);
            TEXTURE3D(_CloudDetailNoise);
            SAMPLER(sampler_CloudDetailNoise);

            CBUFFER_START(UnityPerMaterial)
                half4 _CloudColor;
                half _Coverage;
                half _Density;
                half _CloudScale;
                half _DetailScale;
                half _Erosion;
                half _FormationStrength;
                half _MediumDetail;
                half _SmallDetail;
                half _CellularBreakup;
                half _CellularScale;
                half _WarpStrength;
                half _VerticalProfile;
                half _SunIntensity;
                half _SilverLining;
                half _NightIllumination;
                half _InteriorDarkness;
                half _ShadowStrength;
                half _ShadowSoftness;
                float3 _PlanetCenterWS;
                float _CloudInnerRadius;
                float _CloudOuterRadius;
                float _CloudSeed;
                float3 _WindDirection;
                float4 _LayerWindSpeeds;
                float _CloudTime;
                float3 _CloudSunDirection;
                float4 _CloudSunColor;
                float3 _CloudMoonDirection;
                float4 _CloudMoonColor;
                float _CloudMoonAmount;
                float _ShadowQuality;
                float _SampleCount;
                float _LightSamples;
                float _DistanceLod;
                float _DebugMode;
                float _Twilight;
                float _DayAmount;
                float _NightAmount;
                float _WeatherScale;
            CBUFFER_END

            struct FullscreenAttributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct FullscreenVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 viewDirectionWS : TEXCOORD1;
            };

            #include "PlanetProceduralCloudsCommon.hlsl"

            float CloudDensity(float3 worldPosition)
            {
                return PlanetCloudDensity(worldPosition);
            }

            float2 RaySphere(float3 rayOrigin, float3 rayDirection, float3 center, float radius)
            {
                float3 offset = rayOrigin - center;
                float b = dot(offset, rayDirection);
                float c = dot(offset, offset) - radius * radius;
                float h = b * b - c;
                if (h < 0.0)
                    return float2(1.0, 0.0);
                h = sqrt(h);
                return float2(-b - h, -b + h);
            }

            FullscreenVaryings FullscreenVert(FullscreenAttributes input)
            {
                FullscreenVaryings output;
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.uv = input.uv;
#if UNITY_REVERSED_Z
                float farDepth = 0.0;
#else
                float farDepth = 1.0;
#endif
                float3 farWorld = ComputeWorldSpacePosition(input.uv, farDepth, UNITY_MATRIX_I_VP);
                output.viewDirectionWS = farWorld - _WorldSpaceCameraPos;
                return output;
            }

            half4 FullscreenFrag(FullscreenVaryings input) : SV_Target
            {
                half4 source = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.uv);
                float3 rayOrigin = _WorldSpaceCameraPos;
                float3 rayDirection = normalize(input.viewDirectionWS);
                float2 outerHit = RaySphere(rayOrigin, rayDirection, _PlanetCenterWS, _CloudOuterRadius);
                if (outerHit.y <= max(outerHit.x, 0.0))
                    return source;

                float rayStart = max(outerHit.x, 0.0);
                float rayEnd = outerHit.y;
                float rawDepth = SampleSceneDepth(input.uv);
                bool skyDepth;
#if UNITY_REVERSED_Z
                skyDepth = rawDepth <= 0.0001;
#else
                skyDepth = rawDepth >= 0.9999;
#endif
                if (!skyDepth)
                {
                    float3 sceneWorld = ComputeWorldSpacePosition(
                        input.uv, rawDepth, UNITY_MATRIX_I_VP);
                    rayEnd = min(rayEnd, length(sceneWorld - rayOrigin));
                }
                if (rayEnd <= rayStart)
                    return source;

                int sampleCount = clamp((int)_SampleCount, 8, 96);
                float stepLength = (rayEnd - rayStart) / sampleCount;
                float transmittance = 1.0;
                float3 cloudColour = 0.0;
                float3 sunDirection = normalize(_CloudSunDirection);
                float3 moonDirection = normalize(_CloudMoonDirection);

                [loop]
                for (int i = 0; i < 96; i++)
                {
                    if (i >= sampleCount || transmittance < 0.012)
                        break;

                    float3 samplePosition = rayOrigin + rayDirection *
                        (rayStart + (i + 0.5) * stepLength);
                    float sampleDensity = CloudDensity(samplePosition);
                    if (sampleDensity <= 0.001)
                        continue;

                    float lightTransmission = 1.0;
                    int lightSamples = clamp((int)_LightSamples, 1, 3);
                    [unroll]
                    for (int l = 1; l <= 3; l++)
                    {
                        if (l > lightSamples)
                            break;
                        float3 lightPoint = samplePosition + sunDirection *
                            (l / (float)(lightSamples + 1)) *
                            (_CloudOuterRadius - _CloudInnerRadius) * 0.9;
                        lightTransmission *= 1.0 - saturate(CloudDensity(lightPoint) * 0.32);
                    }

                    float3 radial = normalize(samplePosition - _PlanetCenterWS);
                    float sunFacing = saturate(dot(radial, sunDirection));
                    float backlit = pow(saturate(dot(rayDirection, sunDirection)), 4.0);
                    float twilightTint = saturate(_Twilight) * (0.35 + 0.65 * sunFacing);
                    float3 warmSun = lerp(_CloudColor.rgb, _CloudSunColor.rgb,
                        twilightTint * 0.65);
                    float direct = _SunIntensity * saturate(_DayAmount) *
                        (0.38 + 0.72 * sunFacing) * lightTransmission;
                    float interior = lerp(1.0, 1.0 - _InteriorDarkness, sampleDensity);
                    float3 dayLight = warmSun * direct * interior;
                    float3 silver = _CloudSunColor.rgb * backlit * _SilverLining *
                        saturate(_DayAmount) * (0.35 + 0.65 * lightTransmission);
                    float3 moonLight = _CloudMoonColor.rgb * _CloudMoonAmount *
                        saturate(dot(radial, moonDirection) * 0.5 + 0.5);
                    float3 nightLight = _CloudColor.rgb * (0.008 + _NightIllumination) +
                        moonLight * 0.18;
                    float3 shaded = lerp(nightLight, dayLight + silver, saturate(_DayAmount));
                    float sampleAlpha = saturate(sampleDensity * stepLength /
                        max((_CloudOuterRadius - _CloudInnerRadius) * 0.42, 0.01));
                    cloudColour += shaded * sampleAlpha * transmittance;
                    transmittance *= 1.0 - sampleAlpha;
                }

                if (_DebugMode > 0.5)
                    return half4((1.0 - transmittance).xxx, 1.0);
                return half4(cloudColour + source.rgb * transmittance, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "CloudShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            TEXTURE3D(_CloudBaseNoise);
            SAMPLER(sampler_CloudBaseNoise);
            TEXTURE3D(_CloudDetailNoise);
            SAMPLER(sampler_CloudDetailNoise);
            TEXTURE2D(_CloudShadowMap);
            SAMPLER(sampler_CloudShadowMap);

            float3 _LightDirection;
            float3 _LightPosition;

            CBUFFER_START(UnityPerMaterial)
                half4 _CloudColor;
                half _Coverage;
                half _Density;
                half _CloudScale;
                half _DetailScale;
                half _Erosion;
                half _FormationStrength;
                half _MediumDetail;
                half _SmallDetail;
                half _CellularBreakup;
                half _CellularScale;
                half _WarpStrength;
                half _VerticalProfile;
                half _SunIntensity;
                half _SilverLining;
                half _NightIllumination;
                half _InteriorDarkness;
                half _ShadowStrength;
                half _ShadowSoftness;
                float3 _PlanetCenterWS;
                float _CloudInnerRadius;
                float _CloudOuterRadius;
                float _CloudSeed;
                float3 _WindDirection;
                float4 _LayerWindSpeeds;
                float _CloudTime;
                float3 _CloudSunDirection;
                float4 _CloudSunColor;
                float3 _CloudMoonDirection;
                float4 _CloudMoonColor;
                float _CloudMoonAmount;
                float _ShadowQuality;
                float _SampleCount;
                float _LightSamples;
                float _DistanceLod;
                float _DebugMode;
                float _Twilight;
                float _DayAmount;
                float _NightAmount;
                float _WeatherScale;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            #include "PlanetProceduralCloudsCommon.hlsl"

            float CloudDensity(float3 worldPosition)
            {
                float3 radial = normalize(worldPosition - _PlanetCenterWS);
                float middleRadius = (_CloudInnerRadius + _CloudOuterRadius) * 0.5;
                return PlanetCloudDensity(_PlanetCenterWS + radial * middleRadius) *
                    _ShadowStrength;
            }

            Varyings ShadowVert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
#if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
                float3 lightDirectionWS = _LightDirection;
#endif
                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                output.positionCS = ApplyShadowClamping(output.positionCS);
                return output;
            }

            half4 ShadowFrag(Varyings input) : SV_Target
            {
                float3 radial = normalize(input.positionWS - _PlanetCenterWS);
                float longitude = atan2(radial.z, radial.x) * (0.5 / 3.14159265) + 0.5;
                float latitude = acos(clamp(radial.y, -1.0, 1.0)) / 3.14159265;
                float shadowDensity = SAMPLE_TEXTURE2D(
                    _CloudShadowMap, sampler_CloudShadowMap,
                    float2(longitude, latitude)).r;
                float threshold = lerp(0.66, 0.25, _ShadowSoftness);
                float coverage = saturate((shadowDensity - threshold) *
                    (2.0 + _ShadowSoftness * 4.0));
                float dither = frac(sin(dot(input.positionCS.xy,
                    float2(12.9898, 78.233))) * 43758.5453);
                clip(coverage - dither);
                return 0;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
