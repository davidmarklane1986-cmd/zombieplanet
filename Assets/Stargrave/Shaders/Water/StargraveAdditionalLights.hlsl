#ifndef STARGRAVE_ADDITIONAL_LIGHTS_INCLUDED
#define STARGRAVE_ADDITIONAL_LIGHTS_INCLUDED

#include "../StargraveMoonLight.hlsl"

half3 StargraveApplyAdditionalLights(float3 positionWS, float3 normalWS, float2 normalizedScreenSpaceUV, half3 albedo)
{
    return StargraveMoonDiffuse(positionWS, normalWS, albedo);
}

half3 StargraveApplyAdditionalLightsWater(
    float3 positionWS, float3 normalWS, float3 specNormal, float3 rayDir,
    float2 normalizedScreenSpaceUV, half3 specColor, float smoothness,
    out half3 specular)
{
    half3 lightRgb = StargraveMoonDiffuse(positionWS, normalWS, half3(1, 1, 1));
    StargraveMoonSpecular(positionWS, normalWS, specNormal, rayDir, specColor, smoothness, specular);
    return lightRgb;
}

#endif
