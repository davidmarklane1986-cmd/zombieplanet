#ifndef STARGRAVE_MOON_LIGHT_INCLUDED
#define STARGRAVE_MOON_LIGHT_INCLUDED

#include "StargraveSphericalLight.hlsl"

// Driven globally from PlanetDayNightCycle (gameplay = moon only, no point fills).
float4 _MoonDirection;
float4 _MoonLightColor;
float _MoonLightStrength;

half StargraveMoonNdotL(float3 positionWS, float3 normalWS)
{
    float3 dir = _MoonDirection.xyz;
    if (_MoonLightStrength <= 0.001 || dot(dir, dir) < 0.25)
        return 0;
    return StargraveAssetNdotL(positionWS, normalWS, normalize(dir));
}

half3 StargraveMoonDiffuse(float3 positionWS, float3 normalWS, half3 albedo)
{
    half ndl = StargraveMoonNdotL(positionWS, normalWS);
    return albedo * _MoonLightColor.rgb * (ndl * _MoonLightStrength);
}

void StargraveMoonSpecular(
    float3 positionWS, float3 normalWS, float3 specNormal, float3 rayDir,
    half3 specColor, float smoothness,
    out half3 specular)
{
    specular = half3(0, 0, 0);
    half ndl = StargraveMoonNdotL(positionWS, normalWS);
    if (ndl <= 0.001)
        return;

    float3 dir = normalize(_MoonDirection.xyz);
    float specAngle = acos(saturate(dot(normalize(dir - rayDir), specNormal)));
    float specExp = specAngle / max(1.0 - smoothness, 1e-3);
    half3 lit = _MoonLightColor.rgb * (ndl * _MoonLightStrength);
    specular = exp(-specExp * specExp) * lit * specColor;
}

#endif
