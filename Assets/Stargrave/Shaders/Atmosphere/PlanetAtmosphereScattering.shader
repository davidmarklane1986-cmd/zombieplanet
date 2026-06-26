Shader "Stargrave/Planet Atmosphere Scattering"
{
    Properties
    {
        _ScatteringColor ("Scattering Color", Color) = (0.55, 0.72, 1.0, 1)
        _DensityFalloff ("Density Falloff", Range(0.25, 12)) = 4.5
        _SunIntensity ("Sun Intensity", Range(0, 12)) = 2.0
        _MieStrength ("Mie Strength", Range(0, 2)) = 0.25
        _MiePower ("Mie Power", Range(1, 32)) = 8
        _NightEmission ("Night Emission", Range(0, 0.6)) = 0.05
        _NightSkyVisibility ("Night Sky Visibility", Range(0, 1)) = 0.75
        _SunsetColor ("Sunset Color", Color) = (1.0, 0.52, 0.2, 1)
        _SunsetStrength ("Sunset Strength", Range(0, 2)) = 0.9
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+200"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ForwardAtmosphereScattering"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite Off
            ZTest LEqual
            Blend One One
            Cull Off

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _ScatteringColor;
                half _DensityFalloff;
                half _SunIntensity;
                half _MieStrength;
                half _MiePower;
                half _NightEmission;
                half _NightSkyVisibility;
                half4 _SunsetColor;
                half _SunsetStrength;
            CBUFFER_END

            float3 _PlanetCenterWS;
            float3 _SunDirWS;
            float _PlanetRadius;
            float _AtmosphereRadius;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normInputs = GetVertexNormalInputs(input.normalOS);
                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS = normInputs.normalWS;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 centerToPoint = input.positionWS - _PlanetCenterWS;
                float radius = length(centerToPoint);
                float3 radial = centerToPoint / max(radius, 1e-5);
                float3 viewDir = normalize(GetWorldSpaceNormalizeViewDir(input.positionWS));
                float3 sunDir = normalize(_SunDirWS);

                float thicknessDenom = max(_AtmosphereRadius - _PlanetRadius, 1e-4);
                float thicknessRatio = saturate(thicknessDenom / max(_AtmosphereRadius, 1e-4));
                float localDensity = saturate(pow(thicknessRatio * 12.0, 0.75));
                localDensity = pow(localDensity, 1.0 / max(_DensityFalloff, 0.25));

                // Single symmetric view term (avoids a brightness jump when raw dot crosses 0 vs abs(·) elsewhere).
                float vAlign = saturate(abs(dot(radial, viewDir)));
                float horizon = pow(saturate(1.0 - vAlign), 1.6);
                // Zenith must use the same vAlign as horizon (was raw dot — caused a step when pitching on day side).
                float zenith = pow(saturate(vAlign * 0.5 + 0.5), 1.05);
                float sunFacing = dot(radial, sunDir);
                // Wide smooth twilight band (wider input range = gentler derivative near full day).
                float twilight = smoothstep(-0.28, 0.42, sunFacing);
                float dayScatter = pow(saturate(sunFacing * 0.5 + 0.5), 0.9) * _SunIntensity * twilight;
                float nightBase = lerp(_NightEmission, _NightEmission * 0.35, twilight);
                nightBase *= lerp(1.0, 0.14, _NightSkyVisibility);
                // Soft day floor: fades out toward zenith so max(·, hard floor) cannot create a sudden step.
                float dayCoverageFloor = twilight * 0.48 * (1.0 - smoothstep(0.32, 0.97, vAlign));
                float limbToZenith = smoothstep(0.04, 0.96, vAlign);
                float skyCoverage = lerp(horizon, zenith * 0.72, limbToZenith);
                skyCoverage = skyCoverage + (1.0 - skyCoverage) * dayCoverageFloor;
                skyCoverage = saturate(skyCoverage);
                float sunsetBand = smoothstep(-0.22, 0.12, sunFacing) * (1.0 - smoothstep(0.12, 0.48, sunFacing));
                float sunsetAmount = sunsetBand * horizon * _SunsetStrength;

                float mu = saturate(dot(viewDir, sunDir));
                // Broad smooth rolloff toward sun — avoids a narrow band that “pops” when mu crosses a threshold.
                float muEase = smoothstep(0.12, 0.94, mu);
                float mieCore = pow(mu, _MiePower) * _MieStrength * smoothstep(0.0, 0.65, twilight);
                float mie = mieCore * (1.0 - 0.55 * muEase * muEase) * 0.58;

                float dayAmbientZenith = zenith * twilight * _SunIntensity * 0.18 * (1.0 - 0.35 * muEase);
                float brightness = localDensity * (skyCoverage * (dayScatter + mie) + dayAmbientZenith + nightBase);
                float nightFade = 1.0 - twilight;
                float skyboxPreserve = lerp(1.0, 1.0 - 0.82 * nightFade, _NightSkyVisibility);
                brightness *= skyboxPreserve;
                float3 tint = lerp(_ScatteringColor.rgb, _SunsetColor.rgb, saturate(sunsetAmount));
                float3 col = tint * brightness;
                // Single smooth shoulder (no stacked rcp + hard min — those caused visible knees when pitching).
                col = col / (1.0 + col * float3(0.62, 0.58, 0.52));

                return half4(col, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
