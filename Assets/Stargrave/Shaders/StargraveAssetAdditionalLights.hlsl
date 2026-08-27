#ifndef STARGRAVE_ASSET_ADDITIONAL_LIGHTS_INCLUDED
#define STARGRAVE_ASSET_ADDITIONAL_LIGHTS_INCLUDED

#include "StargraveMoonLight.hlsl"

// Moon only — ignores URP point/spot fills (player lantern, pickup glow, etc.).

half3 StargraveApplyAdditionalLightsForAssets(
    float3 positionWS, float3 normalWS, float2 normalizedScreenSpaceUV, half3 albedo)
{
    return StargraveMoonDiffuse(positionWS, normalWS, albedo);
}

#endif
