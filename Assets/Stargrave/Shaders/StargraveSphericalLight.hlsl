#ifndef STARGRAVE_SPHERICAL_LIGHT_INCLUDED
#define STARGRAVE_SPHERICAL_LIGHT_INCLUDED

// World-space planet centre (pushed globally from PlanetDayNightCycle).
float3 _PlanetCenterWS;

// Character / prop meshes only: inward-facing normals (toward planet core) must not
// catch sun/moon from the far side. Terrain/ocean keep standard N·L — no horizon gate
// (that was forcing night too early on the planet surface).
half StargraveAssetNdotL(float3 positionWS, float3 normalWS, float3 lightDir)
{
    half ndl = saturate(dot(normalWS, lightDir));
    if (dot(_PlanetCenterWS, _PlanetCenterWS) < 1e-4)
        return ndl;

    float3 radialUp = normalize(positionWS - _PlanetCenterWS);
    half outward = saturate(dot(normalWS, radialUp));
    return ndl * outward;
}

// Foliage / upright props: blade normals stay sunlit at dawn while flat ground goes dark.
// Scale by the terrain terminator (radial · light) so grass tracks the ground.
half StargraveAssetNdotLMatchTerrain(float3 positionWS, float3 normalWS, float3 lightDir)
{
    half ndl = StargraveAssetNdotL(positionWS, normalWS, lightDir);
    if (dot(_PlanetCenterWS, _PlanetCenterWS) < 1e-4)
        return ndl;

    float3 radialUp = normalize(positionWS - _PlanetCenterWS);
    half groundSun = saturate(dot(radialUp, lightDir));
    // Soft knee through civil twilight so the carpet doesn't hard-cut.
    groundSun = smoothstep(0.0h, 0.28h, groundSun);
    return ndl * groundSun;
}

#endif
