Shader "ProceduralPlanets/Planet Water"
{
    Properties
    {
        _WaterColor ("Water Color", Color) = (0.1, 0.42, 0.82, 0.75)
        _Opacity ("Opacity", Range(0, 1)) = 0.55
        _FresnelPower ("Edge (fresnel)", Range(0.5, 8)) = 2
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        LOD 100
        ZWrite Off
        ZTest LEqual
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "Water"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4 _WaterColor;
            float _Opacity;
            float _FresnelPower;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewDirWS : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.viewDirWS = GetWorldSpaceViewDir(positionWS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 N = normalize(i.normalWS);
                float3 V = normalize(i.viewDirWS);
                float rim = 1.0 - saturate(dot(N, V));
                rim = pow(rim, _FresnelPower);
                half3 col = _WaterColor.rgb * (0.5 + 0.5 * rim);
                half a = _WaterColor.a * _Opacity * (0.5 + 0.5 * rim);
                return half4(col, saturate(a));
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
