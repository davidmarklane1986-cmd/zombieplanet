// Triangle-grid stochastic texture sampling (Heitz / Deliot style).
// Breaks up obvious tiling by blending three randomly offset samples per triangle.

#ifndef STARGAVE_STOCHASTIC_SAMPLING_INCLUDED
#define STARGAVE_STOCHASTIC_SAMPLING_INCLUDED

void StochasticTriangleGrid(
    float2 uv,
    out float w1, out float w2, out float w3,
    out float2 vertex1, out float2 vertex2, out float2 vertex3)
{
    // Scale into a simplex/triangle grid (2 * sqrt(3) ≈ 3.464)
    uv *= 3.46410162;
    const float2x2 gridToSkewedGrid = float2x2(1.0, 0.0, -0.57735027, 1.15470054);
    float2 skewedCoord = mul(gridToSkewedGrid, uv);
    float2 baseId = floor(skewedCoord);
    float3 temp = float3(frac(skewedCoord), 0.0);
    temp.z = 1.0 - temp.x - temp.y;

    if (temp.z > 0.0)
    {
        w1 = temp.z;
        w2 = temp.y;
        w3 = temp.x;
        vertex1 = baseId;
        vertex2 = baseId + float2(0, 1);
        vertex3 = baseId + float2(1, 0);
    }
    else
    {
        w1 = -temp.z;
        w2 = 1.0 - temp.y;
        w3 = 1.0 - temp.x;
        vertex1 = baseId + float2(1, 1);
        vertex2 = baseId + float2(1, 0);
        vertex3 = baseId + float2(0, 1);
    }
}

float2 StochasticHash2D(float2 p)
{
    return frac(sin(float2(
        dot(p, float2(127.1, 311.7)),
        dot(p, float2(269.5, 183.3)))) * 43758.5453);
}

// Random 2D rotation matrix from a hash seed in [0,1]^2.
float2x2 StochasticRotation(float2 h)
{
    float angle = h.x * 6.2831853;
    float s, c;
    sincos(angle, s, c);
    return float2x2(c, -s, s, c);
}

half3 SampleStochastic2D(TEXTURE2D_PARAM(tex, samp), float2 uv, float contrast)
{
    float w1, w2, w3;
    float2 v1, v2, v3;
    StochasticTriangleGrid(uv, w1, w2, w3, v1, v2, v3);

    float2 h1 = StochasticHash2D(v1);
    float2 h2 = StochasticHash2D(v2);
    float2 h3 = StochasticHash2D(v3);

    float2 uv1 = mul(StochasticRotation(h1), uv) + h1;
    float2 uv2 = mul(StochasticRotation(h2), uv) + h2;
    float2 uv3 = mul(StochasticRotation(h3), uv) + h3;

    half3 c1 = SAMPLE_TEXTURE2D(tex, samp, uv1).rgb;
    half3 c2 = SAMPLE_TEXTURE2D(tex, samp, uv2).rgb;
    half3 c3 = SAMPLE_TEXTURE2D(tex, samp, uv3).rgb;

    float3 w = float3(w1, w2, w3);
    w = pow(max(w, 1e-4), max(contrast, 1.0));
    return (c1 * w.x + c2 * w.y + c3 * w.z) / (w.x + w.y + w.z + 1e-5);
}

half3 SampleStochastic2DArray(TEXTURE2D_ARRAY_PARAM(tex, samp), float2 uv, int slice, float contrast)
{
    float w1, w2, w3;
    float2 v1, v2, v3;
    StochasticTriangleGrid(uv, w1, w2, w3, v1, v2, v3);

    float2 h1 = StochasticHash2D(v1);
    float2 h2 = StochasticHash2D(v2);
    float2 h3 = StochasticHash2D(v3);

    float2 uv1 = mul(StochasticRotation(h1), uv) + h1;
    float2 uv2 = mul(StochasticRotation(h2), uv) + h2;
    float2 uv3 = mul(StochasticRotation(h3), uv) + h3;

    half3 c1 = SAMPLE_TEXTURE2D_ARRAY(tex, samp, uv1, slice).rgb;
    half3 c2 = SAMPLE_TEXTURE2D_ARRAY(tex, samp, uv2, slice).rgb;
    half3 c3 = SAMPLE_TEXTURE2D_ARRAY(tex, samp, uv3, slice).rgb;

    float3 w = float3(w1, w2, w3);
    w = pow(max(w, 1e-4), max(contrast, 1.0));
    return (c1 * w.x + c2 * w.y + c3 * w.z) / (w.x + w.y + w.z + 1e-5);
}

#endif
