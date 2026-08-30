#ifndef STARGRAVE_SCREEN_CIRCLE_OCCLUSION_INCLUDED
#define STARGRAVE_SCREEN_CIRCLE_OCCLUSION_INCLUDED

// Do not include URP Core/Input here. Shader Graph injects this file into every
// pass (including ShadowCaster); a second Input.hlsl include redefines InputData
// and fails player builds.

float4 _StargraveOccPlayerCenter;
float4 _StargraveOccScreenCenter;
float4 _StargraveOccSightDir;
float _StargraveOccScreenRadius;
float _StargraveOccPlayerViewDepth;
float _StargraveOccEdgeSoftness;
float _StargraveOccDepthMargin;

float2 StargraveScreenUvFromRawPosition(float4 positionCS)
{
    return positionCS.xy / max(positionCS.w, 0.00001f);
}

float StargraveScreenCircleOcclusionCoverage(float3 positionWS, float4 positionCS)
{
    float radiusVp = _StargraveOccScreenRadius;
    if (radiusVp <= 0.0001f)
        return 0.0f;

    float3 playerWS = _StargraveOccPlayerCenter.xyz;
    float3 camWS = _WorldSpaceCameraPos;
    float playerDist = distance(playerWS, camWS);
    if (playerDist < 0.001f)
        return 0.0f;

    float depthMargin = _StargraveOccDepthMargin;
    // The raw screen position's w is the camera-space depth (-view-space z).
    // Using it avoids graph-space conversion differences in imported glTF meshes.
    float pointViewDepth = positionCS.w;
    if (_StargraveOccPlayerViewDepth > 0.001f
        && pointViewDepth >= _StargraveOccPlayerViewDepth - depthMargin)
        return 0.0f;

    float2 screenUV = StargraveScreenUvFromRawPosition(positionCS);
    float2 centerVp = _StargraveOccScreenCenter.xy;

    float2 delta = screenUV - centerVp;
    delta.x *= _ScreenParams.x / _ScreenParams.y;

    float distanceFromCenter = length(delta);
    float edgeWidth = max(_StargraveOccEdgeSoftness, 0.00001f);
    return 1.0f - smoothstep(radiusVp - edgeWidth, radiusVp, distanceFromCenter);
}

float StargraveScreenCircleCoverageFromClip(float4 positionCS)
{
    float radiusVp = _StargraveOccScreenRadius;
    if (radiusVp <= 0.0001f)
        return 0.0f;

    float2 screenUV = StargraveScreenUvFromRawPosition(positionCS);
    float2 delta = screenUV - _StargraveOccScreenCenter.xy;
    delta.x *= _ScreenParams.x / _ScreenParams.y;
    float edgeWidth = max(_StargraveOccEdgeSoftness, 0.00001f);
    return 1.0f - smoothstep(radiusVp - edgeWidth, radiusVp, length(delta));
}

void StargraveScreenCircleAlpha_float(
    float Alpha,
    float3 PositionWS,
    float4 PositionCS,
    out float Result)
{
#ifdef SHADERGRAPH_PREVIEW
    Result = Alpha;
#else
    float circleCoverage = StargraveScreenCircleOcclusionCoverage(PositionWS, PositionCS);

    // glTF foliage uses alpha as a cutout mask. Discard the original
    // transparent texels, then render surviving foliage at full opacity so
    // transparent blending cannot make the horizon foliage ghostly.
    clip(Alpha - 0.5f);

    // Dither the circle transition instead of blending the whole foliage
    // material. At the centre every sample is discarded; at the edge the
    // discard probability follows the smooth circle coverage.
    if (circleCoverage > 0.0001f)
    {
        float noise = InterleavedGradientNoise(PositionCS.xy, 0.0f);
        clip((1.0f - circleCoverage) - noise);
    }

    Result = 1.0f;
#endif
}

#endif
