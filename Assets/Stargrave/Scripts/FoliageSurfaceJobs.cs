using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

// ====================================================================================================
// Burst-jobified ANALYTIC surface sampling for FoliageByColour streaming.
//
// WHY THIS EXISTS
//   FoliageByColour places foliage by analytically evaluating the planet's deterministic shape function
//   (no Physics.Raycast). The dominant per-attempt cost is NOISE: one elevation evaluation for the
//   surface point plus three more for the finite-difference surface normal, each summing several octaves
//   of simplex noise. Run on the main thread that work spiked frames when a land-heavy cell streamed in.
//
//   This file ports the EXACT noise->elevation math (Noise.Evaluate simplex + SimpleNoiseFilter +
//   RidgidNoiseFilter + ShapeGenerator.CalculateUnscaledElevation + Planet.GetSurfaceNormalWorld) to
//   Burst-compatible STATIC functions that operate on blittable data, so the heavy sampling runs on
//   worker threads. The managed planet classes are unchanged; this is a faithful transcription consumed
//   by an IJobParallelFor.
//
// WHAT THE JOB COMPUTES (per candidate direction)
//   - the analytic world surface point (drop-in for the old raycast hit point),
//   - the water / cell-acceptance rejections,
//   - the analytic surface normal + slope,
//   - the normalized elevation.
//   It writes accepted candidates into a reused NativeArray. The main thread then consumes accepted
//   candidates and runs the SURFACE COLOUR / BIOME classification + rule/zone selection + spacing grid +
//   placement (those stay managed — see FoliageByColour for the split rationale).
//
// PRECISION
//   The simplex core uses double internally exactly like Noise.Evaluate, casting to float at the same
//   boundary (`(float)(n0+n1+n2+n3) * 32f`) so results match the managed path bit-for-bit-close.
// ====================================================================================================

/// <summary>Blittable snapshot of one ShapeSettings.NoiseLayer (+ its NoiseSettings) for Burst.</summary>
public struct NoiseLayerData
{
    public int enabled;             // 1 if this layer contributes to the elevation sum
    public int useFirstLayerAsMask; // 1 if this layer is masked by the first layer's value
    public int filterType;          // 0 = Simple, 1 = Ridgid
    public int numLayers;           // octave count
    public float strength;
    public float baseRoughness;
    public float roughness;
    public float persistence;
    public float3 centre;
    public float minValue;
    public float weightMultiplier;  // Ridgid only
}

/// <summary>One sampled candidate result written by <see cref="FoliageScatterJob"/>.</summary>
public struct FoliageCandidate
{
    public int accepted;    // 1 = land point that falls inside the target cell and is above water
    public float3 pos;      // world-space surface point
    public float3 normal;   // world-space outward surface normal
    public float slope;     // degrees between the normal and the radial (straight-up) direction
    public float elevNorm;  // normalized elevation 0..1 (matches Planet.GetNormalizedElevationAtPosition)
}

/// <summary>
/// Burst-compatible port of the planet's noise->elevation->normal math. All functions are static and
/// operate purely on blittable inputs (NativeArrays + scalars), so they are safe to call from a job.
/// </summary>
public static class FoliageNoise
{
    // Initial permutation table from Noise.cs (the libnoise simplex source table). SimpleNoiseFilter and
    // RidgidNoiseFilter both construct `new Noise()` (seed 0), whose Randomize(0) simply copies this table
    // into a 512-entry array (Source duplicated). So the permutation is constant and we bake it once.
    static readonly int[] Source =
    {
        151, 160, 137, 91, 90, 15, 131, 13, 201, 95, 96, 53, 194, 233, 7, 225, 140, 36, 103, 30, 69, 142,
        8, 99, 37, 240, 21, 10, 23, 190, 6, 148, 247, 120, 234, 75, 0, 26, 197, 62, 94, 252, 219, 203,
        117, 35, 11, 32, 57, 177, 33, 88, 237, 149, 56, 87, 174, 20, 125, 136, 171, 168, 68, 175, 74, 165,
        71, 134, 139, 48, 27, 166, 77, 146, 158, 231, 83, 111, 229, 122, 60, 211, 133, 230, 220, 105, 92, 41,
        55, 46, 245, 40, 244, 102, 143, 54, 65, 25, 63, 161, 1, 216, 80, 73, 209, 76, 132, 187, 208, 89,
        18, 169, 200, 196, 135, 130, 116, 188, 159, 86, 164, 100, 109, 198, 173, 186, 3, 64, 52, 217, 226, 250,
        124, 123, 5, 202, 38, 147, 118, 126, 255, 82, 85, 212, 207, 206, 59, 227, 47, 16, 58, 17, 182, 189,
        28, 42, 223, 183, 170, 213, 119, 248, 152, 2, 44, 154, 163, 70, 221, 153, 101, 155, 167, 43, 172, 9,
        129, 22, 39, 253, 19, 98, 108, 110, 79, 113, 224, 232, 178, 185, 112, 104, 218, 246, 97, 228, 251, 34,
        242, 193, 238, 210, 144, 12, 191, 179, 162, 241, 81, 51, 145, 235, 249, 14, 239, 107, 49, 192, 214, 31,
        181, 199, 106, 157, 184, 84, 204, 176, 115, 121, 50, 45, 127, 4, 150, 254, 138, 236, 205, 93, 222, 114,
        67, 29, 24, 72, 243, 141, 128, 195, 78, 66, 215, 61, 156, 180
    };

    /// <summary>Builds the 512-entry permutation NativeArray (Persistent). Caller owns disposal.</summary>
    public static NativeArray<int> BuildPermutation(Allocator allocator)
    {
        var perm = new NativeArray<int>(512, allocator, NativeArrayOptions.UninitializedMemory);
        for (int i = 0; i < 256; i++)
        {
            perm[i] = Source[i];
            perm[i + 256] = Source[i];
        }
        return perm;
    }

    static int FastFloor(double x) => x >= 0 ? (int)x : (int)x - 1;

    // Dot of the i-th Grad3 vector with (x,y,z). Grad3 points to the mid-points of the unit cube's edges
    // (exactly Noise.Grad3); the switch avoids a managed jagged array in the job.
    static double GradDot(int gi, double x, double y, double z)
    {
        switch (gi)
        {
            case 0: return x + y;
            case 1: return -x + y;
            case 2: return x - y;
            case 3: return -x - y;
            case 4: return x + z;
            case 5: return -x + z;
            case 6: return x - z;
            case 7: return -x - z;
            case 8: return y + z;
            case 9: return -y + z;
            case 10: return y - z;
            default: return -y - z; // case 11
        }
    }

    /// <summary>3D simplex noise — exact transcription of Noise.Evaluate (seed 0).</summary>
    public static float Simplex(float px, float py, float pz, in NativeArray<int> perm)
    {
        double x = px, y = py, z = pz;
        const double F3 = 1.0 / 3.0;
        const double G3 = 1.0 / 6.0;
        double n0 = 0, n1 = 0, n2 = 0, n3 = 0;

        double s = (x + y + z) * F3;
        int i = FastFloor(x + s);
        int j = FastFloor(y + s);
        int k = FastFloor(z + s);

        double t = (i + j + k) * G3;
        double x0 = x - (i - t);
        double y0 = y - (j - t);
        double z0 = z - (k - t);

        int i1, j1, k1, i2, j2, k2;
        if (x0 >= y0)
        {
            if (y0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
            else if (x0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 0; k2 = 1; }
            else { i1 = 0; j1 = 0; k1 = 1; i2 = 1; j2 = 0; k2 = 1; }
        }
        else
        {
            if (y0 < z0) { i1 = 0; j1 = 0; k1 = 1; i2 = 0; j2 = 1; k2 = 1; }
            else if (x0 < z0) { i1 = 0; j1 = 1; k1 = 0; i2 = 0; j2 = 1; k2 = 1; }
            else { i1 = 0; j1 = 1; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
        }

        double x1 = x0 - i1 + G3;
        double y1 = y0 - j1 + G3;
        double z1 = z0 - k1 + G3;
        double x2 = x0 - i2 + F3;
        double y2 = y0 - j2 + F3;
        double z2 = z0 - k2 + F3;
        double x3 = x0 - 0.5;
        double y3 = y0 - 0.5;
        double z3 = z0 - 0.5;

        int ii = i & 0xff;
        int jj = j & 0xff;
        int kk = k & 0xff;

        double t0 = 0.6 - x0 * x0 - y0 * y0 - z0 * z0;
        if (t0 > 0)
        {
            t0 *= t0;
            int gi0 = perm[ii + perm[jj + perm[kk]]] % 12;
            n0 = t0 * t0 * GradDot(gi0, x0, y0, z0);
        }

        double t1v = 0.6 - x1 * x1 - y1 * y1 - z1 * z1;
        if (t1v > 0)
        {
            t1v *= t1v;
            int gi1 = perm[ii + i1 + perm[jj + j1 + perm[kk + k1]]] % 12;
            n1 = t1v * t1v * GradDot(gi1, x1, y1, z1);
        }

        double t2v = 0.6 - x2 * x2 - y2 * y2 - z2 * z2;
        if (t2v > 0)
        {
            t2v *= t2v;
            int gi2 = perm[ii + i2 + perm[jj + j2 + perm[kk + k2]]] % 12;
            n2 = t2v * t2v * GradDot(gi2, x2, y2, z2);
        }

        double t3v = 0.6 - x3 * x3 - y3 * y3 - z3 * z3;
        if (t3v > 0)
        {
            t3v *= t3v;
            int gi3 = perm[ii + 1 + perm[jj + 1 + perm[kk + 1]]] % 12;
            n3 = t3v * t3v * GradDot(gi3, x3, y3, z3);
        }

        // Match Noise.Evaluate's cast boundary exactly: (float)(sum) THEN * 32.
        return (float)(n0 + n1 + n2 + n3) * 32f;
    }

    // SimpleNoiseFilter.Evaluate
    static float EvalSimple(in NoiseLayerData s, float3 point, in NativeArray<int> perm)
    {
        float noiseValue = 0f;
        float frequency = s.baseRoughness;
        float amplitude = 1f;
        for (int i = 0; i < s.numLayers; i++)
        {
            float3 p = point * frequency + s.centre;
            float v = Simplex(p.x, p.y, p.z, perm);
            noiseValue += (v + 1f) * 0.5f * amplitude;
            frequency *= s.roughness;
            amplitude *= s.persistence;
        }
        noiseValue = math.max(0f, noiseValue - s.minValue);
        return noiseValue * s.strength;
    }

    // RidgidNoiseFilter.Evaluate
    static float EvalRidgid(in NoiseLayerData s, float3 point, in NativeArray<int> perm)
    {
        float noiseValue = 0f;
        float frequency = s.baseRoughness;
        float amplitude = 1f;
        float weight = 1f;
        for (int i = 0; i < s.numLayers; i++)
        {
            float3 p = point * frequency + s.centre;
            float v = 1f - math.abs(Simplex(p.x, p.y, p.z, perm));
            v *= v;
            v *= weight;
            weight = math.clamp(v * s.weightMultiplier, 0f, 1f);
            noiseValue += v * amplitude;
            frequency *= s.roughness;
            amplitude *= s.persistence;
        }
        noiseValue = math.max(0f, noiseValue - s.minValue);
        return noiseValue * s.strength;
    }

    static float EvalLayer(in NoiseLayerData layer, float3 point, in NativeArray<int> perm)
    {
        return layer.filterType == 1 ? EvalRidgid(layer, point, perm) : EvalSimple(layer, point, perm);
    }

    /// <summary>ShapeGenerator.CalculateUnscaledElevation — local (unscaled) surface radius for a unit dir.</summary>
    public static float CalcUnscaledElevation(float3 dir, in NativeArray<NoiseLayerData> layers,
        in NativeArray<int> perm, float planetRadius)
    {
        int len = layers.Length;
        if (len == 0)
            return planetRadius;

        // firstLayerValue is computed from layer 0 regardless of its enabled flag (matches the managed code).
        float firstLayerValue = EvalLayer(layers[0], dir, perm);
        float elevation = 0f;
        if (layers[0].enabled == 1)
            elevation = firstLayerValue;

        for (int i = 1; i < len; i++)
        {
            if (layers[i].enabled == 1)
            {
                float mask = layers[i].useFirstLayerAsMask == 1 ? firstLayerValue : 1f;
                elevation += EvalLayer(layers[i], dir, perm) * mask;
            }
        }
        return planetRadius * (1f + elevation);
    }

    /// <summary>Planet.GetSurfaceNormalWorld — analytic outward normal from two tangential finite differences.</summary>
    public static float3 SurfaceNormal(float3 dir, in NativeArray<NoiseLayerData> layers, in NativeArray<int> perm,
        float planetRadius, float scaleFactor, float3 center, float worldRadiusAtDir)
    {
        float3 up = new float3(0f, 1f, 0f);
        float3 t1 = math.cross(dir, up);
        if (math.lengthsq(t1) < 1e-6f)
            t1 = math.cross(dir, new float3(1f, 0f, 0f));
        t1 = math.normalize(t1);
        float3 t2 = math.cross(dir, t1); // already unit length when dir,t1 are orthonormal

        const float eps = 0.02f;
        float3 p0 = center + dir * worldRadiusAtDir;
        float3 da = math.normalize(dir + t1 * eps);
        float3 db = math.normalize(dir + t2 * eps);
        float3 pa = center + da * (CalcUnscaledElevation(da, layers, perm, planetRadius) * scaleFactor);
        float3 pb = center + db * (CalcUnscaledElevation(db, layers, perm, planetRadius) * scaleFactor);

        float3 n = math.cross(pa - p0, pb - p0);
        if (math.lengthsq(n) < 1e-12f)
            return dir;
        n = math.normalize(n);
        if (math.dot(n, dir) < 0f)
            n = -n;
        return n;
    }
}

/// <summary>
/// Parallel analytic surface sampler. For each candidate direction it computes the surface point, rejects
/// ocean / wrong-cell points, and (for accepted land points) the surface normal, slope and normalized
/// elevation. The expensive noise runs on worker threads; the main thread consumes accepted results.
/// </summary>
// FloatMode.Strict keeps the math close to the managed reference (no reassociation/FMA fast-math), so the
// Burst-sampled elevation/normal match the main-thread analytic path to within last-bit float error —
// well under any placement margin. The simplex core uses double internally exactly like Noise.Evaluate.
[BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
public struct FoliageScatterJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float3> directions;
    [ReadOnly] public NativeArray<NoiseLayerData> layers;
    [ReadOnly] public NativeArray<int> perm;

    public float planetRadius;   // local (unscaled) planet radius
    public float scaleFactor;    // world scale (max lossy axis, guarded)
    public float3 center;        // planet world center
    public float baseRadius;     // world waterline radius (ocean sea level + dry clearance); reject below
    public float invCellSize;    // 1 / chunkSize
    public float elevMin;        // local elevation min (for normalization)
    public float elevMax;        // local elevation max
    public int cellX, cellY, cellZ; // target cell coordinates

    [WriteOnly] public NativeArray<FoliageCandidate> results;

    public void Execute(int index)
    {
        float3 dir = directions[index];
        FoliageCandidate r = default; // accepted defaults to 0

        float localR = FoliageNoise.CalcUnscaledElevation(dir, layers, perm, planetRadius);
        float worldR = localR * scaleFactor; // == |pos - center|
        float3 pos = center + dir * worldR;

        // Cell acceptance: the point must belong to the cell that owns it (matches WorldToCell).
        int cx = (int)math.floor(pos.x * invCellSize);
        int cy = (int)math.floor(pos.y * invCellSize);
        int cz = (int)math.floor(pos.z * invCellSize);
        if (cx != cellX || cy != cellY || cz != cellZ)
        {
            results[index] = r;
            return;
        }

        // Water gate: reject anything at/below the ocean waterline (passed in as baseRadius from the
        // driver — ocean sea level + dry clearance). Do NOT subtract a fudge; the old "base-1" allowed
        // a multi-unit underwater band when sea level sits above the planet base sphere.
        if (worldR < baseRadius)
        {
            results[index] = r;
            return;
        }

        float3 normal = FoliageNoise.SurfaceNormal(dir, layers, perm, planetRadius, scaleFactor, center, worldR);
        float d = math.clamp(math.dot(normal, dir), -1f, 1f); // radial == dir (pos-center normalized)
        float slope = math.degrees(math.acos(d));
        float elevNorm = elevMax > elevMin ? math.saturate((localR - elevMin) / (elevMax - elevMin)) : 0f;

        r.accepted = 1;
        r.pos = pos;
        r.normal = normal;
        r.slope = slope;
        r.elevNorm = elevNorm;
        results[index] = r;
    }
}
