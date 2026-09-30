// Stargrave Planet Ocean - a genuinely spherical, screen-space-style ocean for URP.
//
// Ported from Sebastian Lague's MIT-licensed Solar-System project:
//   https://github.com/SebLague/Solar-System  (OceanEffect.shader, Math.cginc, Triplanar.cginc)
// Original technique (c) Sebastian Lague, MIT License. Adapted to Unity 6 / URP by Stargrave.
//
// Instead of running as a Built-in-RP post effect, this renders on a transparent sphere
// "shell" mesh at the ocean radius. The fragment shader still performs Seb's per-pixel
// ray-sphere intersection from the camera and samples the URP scene depth texture to find
// the water-column thickness between the ocean surface and the terrain/scene behind it.
// Detail ripples, shoreline foam, and shallow caustics are procedural, so the surface
// does not depend on the Git LFS normal maps. Sea level and the swell used for buoyancy
// are unchanged.
Shader "Stargrave/Planet Ocean"
{
    Properties
    {
        [Header(Colour)]
        _ShallowColor ("Shallow Colour", Color) = (0.1, 0.54, 0.66, 1)
        _DeepColor ("Deep Colour", Color) = (0.05, 0.18, 0.4, 1)
        _SpecularColor ("Specular Colour", Color) = (1, 1, 1, 1)

        [Header(Depth blend)]
        _DepthMultiplier ("Depth Colour Falloff", Range(0.1, 60)) = 8
        _AlphaMultiplier ("Shoreline Alpha Falloff", Range(0.1, 120)) = 20

        [Header(Surface)]
        _Smoothness ("Smoothness", Range(0, 1)) = 0.92
        _FresnelPower ("Fresnel Power", Range(0.1, 12)) = 4
        _FresnelStrength ("Fresnel Strength", Range(0, 1)) = 0.35

        [Header(Waves (procedural detail))]
        // Detail ripples are generated in the shader. The old normal maps are Git LFS
        // pointers in this repo, so sampling them shaded the surface with garbage.
        _WaveStrength ("Wave Strength", Range(0, 1)) = 0.55
        _WaveNormalScale ("Wave Normal Scale", Range(0.1, 80)) = 18
        _WaveSpeed ("Wave Speed", Range(0, 1)) = 0.08

        [Header(Shore foam and caustics)]
        _FoamColor ("Foam Colour", Color) = (0.93, 0.98, 1, 1)
        _FoamWidth ("Foam Width (world units)", Range(0.5, 40)) = 6
        _FoamStrength ("Foam Strength", Range(0, 1)) = 0.85
        _CausticStrength ("Caustic Strength", Range(0, 2)) = 0.7

        [Header(Waves (geometric surface swell))]
        // World-space crest height of the physical surface ripple. Locked to world units
        // (constant relative to the player, NOT scaled with the planet) and kept in tune with
        // the triplanar shimmer above: same world-locked scale, same scroll speed, same _Time.y.
        _WaveHeight ("Wave Height (world units)", Range(0, 8)) = 1.5
        // Geometric ripples only need to exist near the camera (distant ocean ripples aren't
        // resolvable and would alias on triangles smaller than the wave). Amplitude fades to 0
        // beyond this world-space distance, which bounds cost and kills far-field aliasing.
        _WaveFadeDistance ("Wave Fade Distance (world units)", Range(20, 2000)) = 220
        // How strongly the LIGHTING reacts to the wave surface. The shader amplifies the SAME triplanar
        // normal-map field that makes the shimmer ("the water effect") and feeds it into the diffuse, so
        // the lit/shaded slopes ("white bits") follow that exact organic pattern instead of a repeating
        // sine grid. 0 = flat shell shading (no relief), higher = bolder, more readable waves.
        _WaveNormalStrength ("Wave Relief Lighting", Range(0, 8)) = 3.0

        [Header(Waves (shore calming by depth))]
        // Waves ramp from calm (shallow water) up to full (deep), driven by the SAME scene-depth water
        // column as the shallow/deep colour. Because depth tracks how close the seabed is to the surface,
        // gentle underwater slopes give a WIDE shallow band -> waves fade out gradually over distance,
        // while steep slopes give a NARROW band -> waves return to full almost immediately. Bigger value
        // = the calming reaches further out into deeper water. Units match the colour depth (world units).
        _WaveShoreDepth ("Wave Shore Calm Depth", Range(1, 400)) = 60
        // Floor so the shallowest water keeps a little life instead of going perfectly mirror-flat. 0 = fully calm.
        _WaveShoreMinCalm ("Wave Shore Min Calm", Range(0, 1)) = 0.05
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }
        LOD 100

        Pass
        {
            Name "ForwardOcean"
            Tags { "LightMode" = "UniversalForward" }

            // ZWrite On so the water SURFACE depth lands in the depth buffer for later transparents
            // (the additive atmosphere shell tests ZTest LEqual against it and is correctly occluded
            // over the ocean instead of "seeing through" to the far floor depth and washing the water
            // out). The shader's own shallow/deep math reads _CameraDepthTexture (opaque floor depth,
            // captured before transparents), so writing depth here does not affect it.
            ZWrite On
            ZTest LEqual
            // Double-sided: the fragment shader keeps exactly one face per pixel based on the ACTUAL
            // rendering camera (front faces from above the surface, back faces from below). This is
            // per-camera correct (Scene view, Game view, reflections) without any shared-material
            // cull state, so the surface always renders from above and is also visible underwater.
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RealtimeLights.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "StargraveAdditionalLights.hlsl"

            // ---- Per-material (SRP batcher compatible) ----
            CBUFFER_START(UnityPerMaterial)
                float4 _ShallowColor;
                float4 _DeepColor;
                float4 _SpecularColor;
                float _DepthMultiplier;
                float _AlphaMultiplier;
                float _Smoothness;
                float _FresnelPower;
                float _FresnelStrength;
                float _WaveStrength;
                float _WaveNormalScale;
                float _WaveSpeed;
                float4 _FoamColor;
                float _FoamWidth;
                float _FoamStrength;
                float _CausticStrength;
                float _WaveHeight;
                float _WaveFadeDistance;
                float _WaveNormalStrength;
                float _WaveShoreDepth;
                float _WaveShoreMinCalm;
            CBUFFER_END

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            // ---- Driver-pushed globals (mirrors PlanetAtmosphereLayer convention) ----
            float3 _OceanCentre;
            float _OceanRadius;
            float _PlanetScale; // world-space scale used to normalize depth / wave size (== ocean radius)

            static const float kMaxFloat = 3.402823466e+38;

            // World-space reference radius the wave look was authored at. Both the per-pixel ripple
            // tiling AND the geometric swell below divide by this so wave SIZE stays constant in
            // world units (relative to the player) instead of growing with the planet.
            static const float kWaveReferenceWorld = 305.0;

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // Geometric surface swell for the VERTEX stage (silhouette only).
            //
            // IMPORTANT: the VISIBLE wave PATTERN the player reads (lit slopes / shimmer / "white bits")
            // is driven entirely by the TRIPLANAR normal-map field in the fragment shader -- that is
            // "the water effect". Reconstructing a true height from a tiling NORMAL map per-vertex is not
            // well-defined (a normal map stores slope, not height), so the silhouette uses this
            // lightweight analytic swell. It is kept consistent with the triplanar field so geometry and
            // shading move together: same world-locked waveScale, same wrapped time clock & speed, and
            // mild domain-warp + multi-octave so the silhouette reads as organic crossing ripples.
            //
            // kGeoSizeMul = wavelength as a multiple of one shimmer tile. Larger = longer, heavier
            // rolling swell that the icosphere can represent cleanly (less sub-triangle chatter).
            // Wrap time so sin/cos phase stays precise after long play sessions (large _Time.y otherwise
            // loses fractional precision and the surface starts to stutter / "glitch").
            float OceanWaveTime()
            {
                return fmod(_Time.y, 2400.0);
            }

            float OceanSwellHeight(float3 worldPos)
            {
                float3 p = worldPos - _OceanCentre;

                // Longer primary swell than the shimmer tile so the silhouette reads as rolling water,
                // not high-frequency mesh chatter. Detail octaves stay coarser (multipliers < 1).
                const float kGeoSizeMul = 2.5;
                const float kTwoPi = 6.28318530718;

                float waveScale = _WaveNormalScale / kWaveReferenceWorld;       // cycles / world unit (shimmer)
                float baseK = kTwoPi * waveScale / kGeoSizeMul;                 // rad / world unit (finest swell)
                float w = kTwoPi * _WaveSpeed / kGeoSizeMul;                    // rad / sec (waveScale cancels)
                float t = OceanWaveTime();

                // Spread of 3D directions so every surface patch sees crossing waves (isotropic-ish).
                float3 d0 = normalize(float3( 1.0,  0.4,  0.7));
                float3 d1 = normalize(float3(-0.6, -0.3,  0.9));
                float3 d2 = normalize(float3( 0.5, -0.8, -0.2));
                float3 d3 = normalize(float3(-0.9,  0.5, -0.4));

                // Mild domain warp: enough to break the sine grid, not so much that crests twitch.
                float warpK = baseK * 0.22;
                float warpAmp = 0.45 / max(baseK, 1e-4);
                float3 warp = float3(
                    sin(warpK * dot(d1, p) - w * 0.22 * t + 0.0),
                    sin(warpK * dot(d2, p) - w * 0.22 * t + 2.0),
                    sin(warpK * dot(d0, p) - w * 0.22 * t + 4.0));
                p += warp * warpAmp;

                // Dominant long swell + softer supporting octaves (heavier water, less chatter).
                float h  = 1.00 * sin(baseK * 1.00 * dot(d0, p) - w * 1.00 * t + 0.0);
                h += 0.55 * sin(baseK * 0.72 * dot(d1, p) - w * 0.72 * t + 1.3);
                h += 0.35 * sin(baseK * 0.48 * dot(d2, p) - w * 0.48 * t + 2.1);
                h += 0.22 * sin(baseK * 0.32 * dot(d3, p) - w * 0.32 * t + 4.2);

                return h / 2.12; // normalize by the sum of amplitudes
            }

            // Shared depth -> calm ramp so the VERTEX displacement and the FRAGMENT shading attenuate
            // identically near shore. columnDepth is the water-column depth in world units (0 at the
            // waterline, growing with deeper water). Returns 0..1: 0 = calm/glassy, 1 = full waves. The
            // smoothstep gives a gentle S-curve; _WaveShoreMinCalm floors how flat the shallowest water gets.
            float WaveDepthCalm(float columnDepth)
            {
                float f = saturate(columnDepth / max(_WaveShoreDepth, 1e-3));
                f = smoothstep(0.0, 1.0, f);
                return lerp(_WaveShoreMinCalm, 1.0, f);
            }

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // Physically ripple the surface: displace each vertex along its radial sphere normal
                // by the analytic swell. Amplitude is in WORLD units (constant relative to the player),
                // so a bigger planet gets more ripples, not taller ones. Small vs the ocean radius, so
                // the fragment's ray-sphere/scene-depth math against the smooth sphere stays coherent.
                //
                // Shore calm is intentionally NOT applied here. Sampling the scene-depth texture in the
                // vertex stage (screen-projected texel fetch) caused visible flicker / popping as
                // adjacent verts hit different depth texels, horizon depth jumped, or render-scale
                // disagreed. Fragment shading still calms by oceanViewDepth; silhouette keeps a stable
                // swell everywhere and fades only with camera distance.
                float3 posWS = GetVertexPositionInputs(input.positionOS.xyz).positionWS;
                float3 radial = posWS - _OceanCentre;
                float3 sphereN = normalize(radial);

                float distToCam = distance(posWS, GetCameraPositionWS());
                float fade = saturate(1.0 - distToCam / max(_WaveFadeDistance, 1e-3));
                fade *= fade; // ease-out so the transition isn't a hard ring

                posWS += sphereN * (OceanSwellHeight(posWS) * _WaveHeight * fade);

                output.positionWS = posWS;
                output.positionCS = TransformWorldToHClip(posWS);
                return output;
            }

            // Returns (dstToSphere, dstThroughSphere). If origin inside sphere, dstToSphere = 0.
            // If ray misses, dstToSphere = kMaxFloat, dstThroughSphere = 0. (Seb Lague, Math.cginc)
            float2 raySphere(float3 sphereCentre, float sphereRadius, float3 rayOrigin, float3 rayDir)
            {
                float3 offset = rayOrigin - sphereCentre;
                float a = 1.0;
                float b = 2.0 * dot(offset, rayDir);
                float c = dot(offset, offset) - sphereRadius * sphereRadius;
                float d = b * b - 4.0 * a * c;
                if (d > 0.0)
                {
                    float s = sqrt(d);
                    float dstToSphereNear = max(0.0, (-b - s) / (2.0 * a));
                    float dstToSphereFar = (-b + s) / (2.0 * a);
                    if (dstToSphereFar >= 0.0)
                        return float2(dstToSphereNear, dstToSphereFar - dstToSphereNear);
                }
                return float2(kMaxFloat, 0.0);
            }

            // Analytic ripple height. Two scrolling layers replace the missing normal maps.
            float RippleHeight(float2 uv)
            {
                float h = sin(uv.x * 1.15 + uv.y * 0.65);
                h += sin(uv.x * 2.35 - uv.y * 1.85 + 1.7) * 0.55;
                h += sin((uv.x + uv.y) * 4.4 + 0.6) * 0.25;
                return h;
            }

            // Triplanar slope from the ripple field, tilted off the geometric wave normal.
            float3 ProceduralWaveNormal(float3 vertPos, float3 normal, float scale, float2 offset, float strength)
            {
                float3 blendWeight = saturate(pow(abs(normal), 4.0));
                blendWeight /= max(dot(blendWeight, 1.0), 1e-5);

                const float e = 0.04;
                float2 uvX = vertPos.zy * scale + offset;
                float2 uvY = vertPos.xz * scale + offset;
                float2 uvZ = vertPos.xy * scale + offset;

                float2 slopeX = float2(RippleHeight(uvX) - RippleHeight(uvX + float2(e, 0)),
                                       RippleHeight(uvX) - RippleHeight(uvX + float2(0, e)));
                float2 slopeY = float2(RippleHeight(uvY) - RippleHeight(uvY + float2(e, 0)),
                                       RippleHeight(uvY) - RippleHeight(uvY + float2(0, e)));
                float2 slopeZ = float2(RippleHeight(uvZ) - RippleHeight(uvZ + float2(e, 0)),
                                       RippleHeight(uvZ) - RippleHeight(uvZ + float2(0, e)));
                float2 slope = (slopeX * blendWeight.x + slopeY * blendWeight.y + slopeZ * blendWeight.z) * strength;

                float3 upRef = (abs(normal.y) < 0.99) ? float3(0, 1, 0) : float3(1, 0, 0);
                float3 tangent = normalize(cross(normal, upRef));
                float3 bitangent = cross(normal, tangent);
                return normalize(normal + tangent * slope.x + bitangent * slope.y);
            }

            float ShoreFoam(float columnDepth, float3 worldPos, float time)
            {
                float band = 1.0 - saturate(columnDepth / max(_FoamWidth, 0.25));
                band = smoothstep(0.05, 0.9, band);
                float streak = RippleHeight(worldPos.xz * 0.09 + float2(time * 0.35, time * 0.1));
                streak = saturate(streak * 0.3 + 0.62);
                return band * streak;
            }

            float CausticMask(float3 worldPos, float time, float scale)
            {
                float2 uv = worldPos.xz * scale * 2.4;
                float a = RippleHeight(uv + time * 0.45);
                float b = RippleHeight(uv * 1.7 - time * 0.3 + 2.2);
                return pow(saturate(a * b * 0.22 + 0.35), 5.0);
            }

            half4 frag(Varyings input, FRONT_FACE_TYPE cullFace : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float3 camPos = GetCameraPositionWS();

                // Keep one face per pixel using the real rendering camera: front faces when the camera
                // is outside the shell (above the surface), back faces when it is inside (below sea
                // level). Bias by wave height so crests near the camera don't flip inside/outside and
                // flicker as displacement crosses the analytic radius.
                float camRadius = distance(camPos, _OceanCentre);
                bool cameraInside = camRadius < (_OceanRadius + _WaveHeight * 0.5);
                bool isFrontFace = IS_FRONT_VFACE(cullFace, true, false);
                if (isFrontFace == cameraInside)
                    discard;

                float3 rayDir = normalize(input.positionWS - camPos);

                // Scene depth -> world position behind this fragment. Use URP's normalized screen UV so
                // render-scale / dynamic resolution don't mis-sample depth (a common sparkle/hole cause).
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float rawDepth = SampleSceneDepth(screenUV);
                float3 sceneWS = ComputeWorldSpacePosition(screenUV, rawDepth, UNITY_MATRIX_I_VP);
                float sceneDist = length(sceneWS - camPos);

                // The visible surface point is the ACTUAL displaced mesh fragment (input.positionWS), not a
                // perfect-sphere ray hit -- so colour, depth, the normal basis and the wave texture all
                // sit on the real moving wave surface. The analytic sphere is still used (cheaply) only as a
                // deep-water cap on the column where no terrain is behind the view (e.g. out to the limb).
                float distToSurface = length(input.positionWS - camPos);
                float2 hitInfo = raySphere(_OceanCentre, _OceanRadius, camPos, rayDir);
                // A displaced crest can poke just outside the analytic radius and make the ray MISS the
                // sphere; fall back to a thin column there instead of discarding, so crests are never clipped.
                float dstThroughOcean = (hitInfo.x < kMaxFloat) ? hitInfo.y : max(_WaveHeight * 2.0, 1.0);

                // Water column from the WAVE SURFACE to the terrain/scene behind it. Clamp to >= 0 so a
                // depth/UV mismatch or crest in front of reconstructed scene never goes negative and
                // sparkles away via clip().
                float oceanViewDepth = min(dstThroughOcean, max(sceneDist - distToSurface, 0.0));

                // Nothing to draw where there is no water column (e.g. land in front of the surface).
                clip(oceanViewDepth - 1e-4);
                if (dstThroughOcean <= 0.0)
                    discard;

                float planetScale = max(_PlanetScale, 1e-4);

                float depthT = 1.0 - exp(-oceanViewDepth / planetScale * _DepthMultiplier);
                float alpha = 1.0 - exp(-oceanViewDepth / planetScale * _AlphaMultiplier);
                half3 oceanCol = lerp(_ShallowColor.rgb, _DeepColor.rgb, saturate(depthT));

                // Shade the REAL displaced surface: the mesh fragment world position carries the vertex wave
                // displacement (+ subdiv-7 tessellation), so oceanIntersect -- used both for the world-space
                // triplanar sampling and as the radial basis -- now moves WITH the waves (parallax included).
                float3 surfaceWS = input.positionWS;
                float3 oceanIntersect = surfaceWS - _OceanCentre;
                float3 sphereNormal = normalize(oceanIntersect);   // smooth radial: macro up / day-night basis

                // Depth-based shore calming, reusing the SAME water-column depth (oceanViewDepth) as the
                // shallow/deep colour -- no second depth system. Gentle seabed slopes give a wide
                // shallow band so waves fade out gradually toward shore; steep slopes give a narrow band so
                // waves stay rough right up to the waterline. Drives the displacement-matched relief AND the
                // shimmer below, so near-shore water reads glassy/smooth and deep water keeps full roughness.
                float waveDepthFactor = WaveDepthCalm(oceanViewDepth);

                float waveScale = _WaveNormalScale / kWaveReferenceWorld;

                // --- Macro WAVE-SURFACE normal from the SAME height field that displaced the vertices ------
                // Reconstruct the true surface slope by finite-differencing OceanSwellHeight around this point
                // (3 cheap evaluations -> a tangent-space gradient). It is scaled by the SAME amplitude as the
                // vertex displacement (_WaveHeight x camera-distance fade x shore calm), so the shaded crests
                // and troughs line up with the real displaced silhouette. THIS is what stops the surface
                // reading as a perfect sphere: the lighting now follows the actual geometry, not a radial.
                float camFade = saturate(1.0 - distToSurface / max(_WaveFadeDistance, 1e-3));
                camFade *= camFade;                                  // match the vertex stage's squared ease-out
                float dispAmp = _WaveHeight * camFade * waveDepthFactor;

                float3 upRef = (abs(sphereNormal.y) < 0.99) ? float3(0, 1, 0) : float3(1, 0, 0);
                float3 tA = normalize(cross(sphereNormal, upRef));   // surface tangents for the slope sampling
                float3 tB = cross(sphereNormal, tA);
                float gEps = 0.1 / max(waveScale, 1e-4);             // ~ a small fraction of a wavelength
                float h0 = OceanSwellHeight(surfaceWS);
                float hA = OceanSwellHeight(surfaceWS + tA * gEps);
                float hB = OceanSwellHeight(surfaceWS + tB * gEps);
                float2 slope = (float2(hA, hB) - h0) / gEps * dispAmp;       // d(displacement)/d(tangent)
                float3 geoNormal = normalize(sphereNormal - (tA * slope.x + tB * slope.y));

                // Procedural detail ripples on the displaced surface. Two scroll directions so the
                // pattern doesn't lock to one sine grid. No normal-map sample: those textures are
                // empty LFS pointers and were shading the old water with garbage.
                float tWave = OceanWaveTime();
                float detailStrength = 14.0 * _WaveStrength * waveDepthFactor;
                float3 waveNormal = ProceduralWaveNormal(oceanIntersect, geoNormal, waveScale,
                    float2(tWave * _WaveSpeed, tWave * _WaveSpeed * 0.75), detailStrength);
                waveNormal = ProceduralWaveNormal(oceanIntersect, waveNormal, waveScale * 1.65,
                    float2(tWave * _WaveSpeed * -0.65, tWave * _WaveSpeed * -0.28), detailStrength * 0.55);
                waveNormal = normalize(lerp(geoNormal, waveNormal, saturate(_WaveStrength * waveDepthFactor)));

                float3 detailDev = waveNormal - dot(waveNormal, geoNormal) * geoNormal;
                float3 reliefNormal = normalize(geoNormal + (_WaveNormalStrength * waveDepthFactor) * detailDev);

                Light mainLight = GetMainLight();
                float3 dirToSun = normalize(mainLight.direction);
                float3 viewDir = -rayDir;
                half nDotL = saturate(dot(reliefNormal, dirToSun));
                half wrap = saturate(dot(reliefNormal, dirToSun) * 0.5 + 0.5);
                half3 sunDiffuse = mainLight.color * lerp(nDotL, wrap, 0.35);

                float3 halfDir = normalize(dirToSun + viewDir);
                float specPower = exp2(10.0 * saturate(_Smoothness));
                half specDot = pow(saturate(dot(waveNormal, halfDir)), specPower);
                half3 sunSpec = specDot * nDotL * mainLight.color * _SpecularColor.rgb;

                half3 addSpec;
                half3 addDiffuse = StargraveApplyAdditionalLightsWater(
                    surfaceWS, reliefNormal, waveNormal, rayDir, screenUV,
                    _SpecularColor.rgb, _Smoothness, addSpec);

                half3 totalDiffuse = sunDiffuse + addDiffuse;
                half fresnel = pow(saturate(1.0 - saturate(dot(reliefNormal, viewDir))), _FresnelPower);
                half3 skyTint = lerp(_DeepColor.rgb, half3(0.62, 0.78, 0.9), 0.65);
                half3 rim = fresnel * _FresnelStrength * (skyTint + totalDiffuse * 0.35) * _SpecularColor.rgb;

                // Shallows show a refracted view of the opaque scene (the seabed). Deeper water
                // takes over with the water colour. Alpha still softens the shoreline.
                float2 refractUv = screenUV + waveNormal.xy * (0.012 * (1.0 - saturate(depthT)));
                half3 seabed = SampleSceneColor(refractUv);
                half3 waterBody = oceanCol * (totalDiffuse + SampleSH(sphereNormal)) + sunSpec + addSpec + rim;
                half shore = saturate(alpha);
                oceanCol = lerp(seabed * lerp(half3(1, 1, 1), oceanCol, 0.45), waterBody, shore);

                float caustics = CausticMask(oceanIntersect, tWave, waveScale);
                oceanCol += _ShallowColor.rgb * caustics * _CausticStrength * (1.0 - saturate(depthT)) * nDotL;

                float foam = ShoreFoam(oceanViewDepth, oceanIntersect, tWave) * _FoamStrength;
                float crest = saturate(OceanSwellHeight(surfaceWS));
                crest = smoothstep(0.55, 0.95, crest) * waveDepthFactor * _FoamStrength * 0.45;
                foam = saturate(foam + crest);
                oceanCol = lerp(oceanCol, _FoamColor.rgb, foam);

                half fogFactor = InitializeInputDataFog(float4(surfaceWS, 1.0), 0);
                oceanCol = MixFog(oceanCol, fogFactor);

                return half4(oceanCol, saturate(max(shore, foam)));
            }
            ENDHLSL
        }
    }
    FallBack Off
}
