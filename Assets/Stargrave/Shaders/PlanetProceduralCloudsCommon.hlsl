#ifndef PLANET_PROCEDURAL_CLOUDS_COMMON_INCLUDED
#define PLANET_PROCEDURAL_CLOUDS_COMMON_INCLUDED

// Restored from the original visible 3D-noise shape. A second incommensurate
// sample breaks the wallpaper tile; weather only thins, it never zeros the layer.

float Hash31(float3 p)
{
    p += _CloudSeed * float3(0.071, 0.113, 0.173);
    return frac(sin(dot(p, float3(127.1, 311.7, 74.7))) * 43758.5453);
}

float ValueNoise(float3 p)
{
    float3 cell = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = Hash31(cell);
    float n100 = Hash31(cell + float3(1, 0, 0));
    float n010 = Hash31(cell + float3(0, 1, 0));
    float n110 = Hash31(cell + float3(1, 1, 0));
    float n001 = Hash31(cell + float3(0, 0, 1));
    float n101 = Hash31(cell + float3(1, 0, 1));
    float n011 = Hash31(cell + float3(0, 1, 1));
    float n111 = Hash31(cell + float3(1, 1, 1));
    float x00 = lerp(n000, n100, f.x);
    float x10 = lerp(n010, n110, f.x);
    float x01 = lerp(n001, n101, f.x);
    float x11 = lerp(n011, n111, f.x);
    return lerp(lerp(x00, x10, f.y), lerp(x01, x11, f.y), f.z);
}

float Fbm2(float3 p)
{
    float a = ValueNoise(p);
    float b = ValueNoise(p * 2.03 + float3(17.1, 7.3, 29.7));
    return a * 0.67 + b * 0.33;
}

float PlanetCloudShape(float3 worldPosition)
{
    float3 relative = (worldPosition - _PlanetCenterWS) / max(_CloudScale, 0.1);
    float3 windOffset = _WindDirection *
        (_CloudTime * _LayerWindSpeeds.y / max(_CloudScale, 0.1));
    float3 detailWindOffset = _WindDirection *
        (_CloudTime * _LayerWindSpeeds.z / max(_DetailScale, 0.1));
    float3 baseUv = relative + windOffset;
    float3 warpUv = relative * 0.42 + windOffset * 0.42 + float3(0.17, 0.31, 0.47);
    float4 warpNoise = SAMPLE_TEXTURE3D(_CloudBaseNoise, sampler_CloudBaseNoise, warpUv);
    float3 noiseUv = baseUv + (warpNoise.rgb - 0.5) * _WarpStrength * 0.55;
    float detailFrequency = max(0.25, _CloudScale / max(_DetailScale, 0.1));
    float4 baseA = SAMPLE_TEXTURE3D(_CloudBaseNoise, sampler_CloudBaseNoise, noiseUv);
    float4 baseB = SAMPLE_TEXTURE3D(_CloudBaseNoise, sampler_CloudBaseNoise,
        noiseUv.yzx * 0.618 + 0.31);
    float4 detailNoise = SAMPLE_TEXTURE3D(_CloudDetailNoise, sampler_CloudDetailNoise,
        relative * detailFrequency + detailWindOffset + 0.17);

    float large = baseA.r * 0.7 + baseB.r * 0.3;
    float cellularShape = smoothstep(0.38, 0.66, baseA.g * 0.55 + baseB.g * 0.45);
    float medium = detailNoise.r;
    float small = detailNoise.g;
    float c = saturate(_Coverage);
    float massThreshold = lerp(0.67, 0.40, c);
    float mass = smoothstep(massThreshold - 0.10, massThreshold + 0.10, large);
    float brokenMass = mass * lerp(0.42, 1.0, cellularShape);
    float isolatedCells = smoothstep(0.52, 0.76, baseA.g) * 0.82;
    float formation = lerp(mass, max(brokenMass, isolatedCells), saturate(_CellularBreakup));
    formation = lerp(formation, formation * 0.70 + medium * 0.30, saturate(_MediumDetail));
    formation = lerp(formation, formation * 0.82 + medium * 0.18, saturate(_FormationStrength));
    float threshold = lerp(0.63, 0.34, c);
    float edge = smoothstep(threshold - 0.08, threshold + 0.08, formation);
    edge = saturate(edge - (small - 0.46) * _Erosion * _SmallDetail * 0.7);

    float3 radial = worldPosition - _PlanetCenterWS;
    float radius = length(radial);
    radial /= max(radius, 0.001);
    float weatherFreq = max(radius, 1.0) / max(_WeatherScale, 80.0);
    float weather = Fbm2(radial * weatherFreq);
    // Thin cloudy vs clear regions, but keep a visible floor so the sky cannot go empty.
    float weatherMul = lerp(0.55, 1.0, saturate(weather * 0.75 + c * 0.4));
    edge *= weatherMul;
    edge *= smoothstep(0.001, 0.04, c);
    return saturate(edge);
}

float PlanetCloudDensity(float3 worldPosition)
{
    float radius = length(worldPosition - _PlanetCenterWS);
    float thickness = max(_CloudOuterRadius - _CloudInnerRadius, 0.001);
    float height01 = saturate((radius - _CloudInnerRadius) / thickness);
    float vertical = pow(saturate(1.0 - abs(height01 * 2.0 - 1.0)), max(_VerticalProfile, 0.1));
    return saturate(PlanetCloudShape(worldPosition) * vertical * _Density * 1.35);
}

#endif
