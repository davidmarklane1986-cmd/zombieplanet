#ifndef STARGRAVE_ADDITIONAL_LIGHTS_INCLUDED
#define STARGRAVE_ADDITIONAL_LIGHTS_INCLUDED

// Requires Lighting.hlsl / RealtimeLights.hlsl already included.
// Variable must be named inputData for URP Forward+ LIGHT_LOOP macros.

half3 StargraveApplyAdditionalLights(float3 positionWS, float3 normalWS, float2 normalizedScreenSpaceUV, half3 albedo)
{
    half3 add = half3(0, 0, 0);
#if defined(_ADDITIONAL_LIGHTS)
    InputData inputData = (InputData)0;
    inputData.positionWS = positionWS;
    inputData.normalWS = normalWS;
    inputData.normalizedScreenSpaceUV = normalizedScreenSpaceUV;
    inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(positionWS);

    uint pixelLightCount = GetAdditionalLightsCount();
    half4 shadowMask = half4(1, 1, 1, 1);

#if USE_CLUSTER_LIGHT_LOOP
    [loop] for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
    {
        CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK
        Light clusterDir = GetAdditionalLight(lightIndex, positionWS, shadowMask);
        half ndlCluster = saturate(dot(normalWS, clusterDir.direction));
        add += albedo * clusterDir.color * (ndlCluster * clusterDir.distanceAttenuation * clusterDir.shadowAttenuation);
    }
#endif

    LIGHT_LOOP_BEGIN(pixelLightCount)
        Light light = GetAdditionalLight(lightIndex, positionWS, shadowMask);
        half ndl = saturate(dot(normalWS, light.direction));
        add += albedo * light.color * (ndl * light.distanceAttenuation * light.shadowAttenuation);
    LIGHT_LOOP_END
#endif
    return add;
}

#endif
