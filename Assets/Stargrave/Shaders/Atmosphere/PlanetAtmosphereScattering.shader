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
        _SunsetColor ("Sunset Color", Color) = (1.0, 0.62, 0.38, 1)
        _SunsetStrength ("Sunset Strength", Range(0, 2)) = 0.55
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
            // Open-sky daylight from sun elevation (0..1). Not player LoS shade.
            float _PlayerSunAmount;
            // Golden-hour warmth (0..1) when the sun is near the horizon.
            float _PlayerTwilight;

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

                float vAlign = saturate(abs(dot(radial, viewDir)));
                float horizon = pow(saturate(1.0 - vAlign), 1.55);
                float zenith = pow(saturate(vAlign * 0.5 + 0.5), 1.05);
                float sunFacing = dot(radial, sunDir);

                // Sky daylight clock (elevation). Shade must not kill the sky.
                float skyDay = saturate(_PlayerSunAmount);
                float golden = saturate(_PlayerTwilight);

                // Soft day limb from geometry; brightness still follows skyDay.
                float dayLimb = smoothstep(-0.35, 0.45, sunFacing);
                dayLimb = dayLimb * dayLimb * (3.0 - 2.0 * dayLimb);
                float dayScatter = pow(saturate(sunFacing * 0.5 + 0.5), 0.9) * _SunIntensity * dayLimb * skyDay;

                // Night residual: rises as skyDay falls.
                float nightBase = _NightEmission * (1.0 - skyDay * 0.92);
                nightBase *= lerp(1.0, 0.14, _NightSkyVisibility);

                float limbToZenith = smoothstep(0.04, 0.96, vAlign);
                float skyCoverage = lerp(horizon, zenith * 0.7, limbToZenith);
                // Soft day floor so looking up on the day side stays filled.
                float dayFloor = skyDay * 0.38 * (1.0 - smoothstep(0.35, 0.98, vAlign));
                skyCoverage = saturate(skyCoverage + (1.0 - skyCoverage) * dayFloor);

                // Golden hour: warm flush on the sun-side horizon (rose at dawn, amber at dusk).
                float sunsetBand = smoothstep(-0.35, 0.15, sunFacing) * (1.0 - smoothstep(0.15, 0.65, sunFacing));
                float sunsetAmount = sunsetBand * (horizon * 0.9 + 0.25) * _SunsetStrength * golden;

                float mu = saturate(dot(viewDir, sunDir));
                float muEase = smoothstep(0.12, 0.94, mu);
                float mie = pow(mu, _MiePower) * _MieStrength * skyDay * (0.45 + 0.55 * dayLimb);
                mie *= (1.0 - 0.5 * muEase * muEase);
                // Soft warm glow around the sun during golden hour.
                mie += pow(mu, max(_MiePower * 0.5, 2.0)) * _MieStrength * 0.28 * golden * sunsetBand;

                float dayZenith = zenith * skyDay * _SunIntensity * 0.16 * (1.0 - 0.3 * muEase);
                float goldenWash = (horizon * 0.45 + zenith * 0.08) * golden * _SunsetStrength * 0.22;

                float brightness = localDensity * (skyCoverage * (dayScatter + mie) + dayZenith + nightBase + goldenWash);

                float nightFade = 1.0 - skyDay;
                nightFade = nightFade * nightFade * (3.0 - 2.0 * nightFade);
                float skyboxPreserve = lerp(1.0, 1.0 - 0.8 * nightFade, _NightSkyVisibility);
                brightness *= skyboxPreserve;

                float3 tint = lerp(_ScatteringColor.rgb, _SunsetColor.rgb, saturate(sunsetAmount));
                tint = lerp(tint, _SunsetColor.rgb, saturate(golden * 0.22));
                float3 col = tint * brightness;
                col = col / (1.0 + col * float3(0.62, 0.58, 0.52));

                return half4(col, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
