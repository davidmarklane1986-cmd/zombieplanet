Shader "Stargrave/Planet Atmosphere Fresnel"
{
    Properties
    {
        _RimColor ("Rim colour", Color) = (0.45, 0.75, 1, 1)
        _FresnelPower ("Fresnel power", Range(0.2, 8)) = 1.65
        _Intensity ("Rim strength", Range(0, 8)) = 1.15
        _NightRimMul ("Night-side rim scale", Range(0, 0.35)) = 0.08
        _DayRimCurve ("Sun-facing rim curve", Range(0.25, 4)) = 1.35
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
            Name "ForwardAtmosphere"
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
                half4 _RimColor;
                half _FresnelPower;
                half _Intensity;
                half _NightRimMul;
                half _DayRimCurve;
            CBUFFER_END

            float3 _PlanetCenterWS;
            float3 _SunDirWS;
            float _PlayerSunAmount;

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
                half3 N = normalize(input.normalWS);
                half3 V = normalize(GetWorldSpaceNormalizeViewDir(input.positionWS));
                // |N·V|: avoids full rim when N·V is negative (e.g. looking up through shell on night side).
                half nd = saturate(abs(dot(N, V)));
                half rim = pow(1.0h - nd, _FresnelPower);

                float3 rad = normalize(input.positionWS - _PlanetCenterWS);
                float3 sun = normalize(_SunDirWS);
                half sunFace = smoothstep(-0.35h, 0.45h, (half)dot(rad, sun));
                sunFace = sunFace * sunFace * (3.0h - 2.0h * sunFace);
                half day = pow(sunFace, _DayRimCurve * 0.85h);
                half playerSun = saturate((half)_PlayerSunAmount);
                day *= playerSun;
                half nightScale = lerp(_NightRimMul, 1.0h, day);

                half3 glow = _RimColor.rgb * rim * _Intensity * nightScale;
                glow = min(glow, half3(4.0h, 4.0h, 4.0h));
                return half4(glow, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
