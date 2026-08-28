using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
// Unity.Mathematics also defines a `Random` type; alias keeps every existing `Random.value/Range/...`
// call in this file bound to UnityEngine.Random. The Burst direction RNG uses Unity.Mathematics.Random
// by its fully-qualified name.
using Random = UnityEngine.Random;

/// <summary>
/// "Colour → asset" foliage placement driver for the procedural planet.
///
/// One shared scatter pass raycasts random points on the planet, classifies each point by its
/// dominant gradient KEY colour (<see cref="Planet.GetSurfaceKeyColorAtPosition"/>), and assigns it
/// to the <see cref="FoliageColourRule"/> whose <see cref="FoliageColourRule.targetColour"/> is the
/// closest match. Each rule then thins by a greenness-style density falloff and a per-rule spacing
/// grid before placing an instance.
///
/// Two render backends:
///  • <see cref="FoliageRenderMode.GpuInstanced"/> — GPU-instanced like GpuGrassCarpet (grass/flowers).
///  • <see cref="FoliageRenderMode.GameObjectPool"/> — real instantiated prefabs with colliders/LODs (trees/rocks).
///
/// This is additive and self-contained. With no palette assigned it falls
/// back to a single built-in grass rule, so dropping this component on an object "just works".
/// </summary>
public class FoliageByColour : MonoBehaviour
{
    /// <summary>How a candidate surface point is assigned to a placement rule.</summary>
    public enum FoliagePlacementMode
    {
        [Tooltip("LEGACY (default): each rule's own membership test (colour tolerance / biome-gradient greenness " +
                 "/ biome dominance) is evaluated and the point goes to the STRONGEST match. Rules can overlap " +
                 "and leave gaps. Nothing about the existing behaviour changes.")]
        PerRuleMatch = 0,
        [Tooltip("NEAREST-OF-PALETTE: the point samples the live surface colour and is assigned to the SINGLE " +
                 "rule whose targetColour is nearest by squared RGB distance (winner-take-all — the whole " +
                 "surface partitions into exactly the palette's colour zones, no tolerance gaps/overlaps). " +
                 "A winning rule flagged 'Place Nothing' leaves the point bare. Built by " +
                 "'Tools/Stargrave/Build 16-Colour Foliage Palette'.")]
        NearestPaletteColour = 1,
    }

    [Header("Placement mode")]
    [Tooltip("How surface points are assigned to rules. PerRuleMatch = legacy strongest-match (unchanged). " +
             "NearestPaletteColour = winner-take-all nearest palette colour (the 16-colour zone system).")]
    public FoliagePlacementMode placementMode = FoliagePlacementMode.PerRuleMatch;

    [Header("Rules")]
    [Tooltip("Palette of colour→asset rules. If empty, 'Palette Resource Name' is loaded from Resources, else the inline fallback rules below are used.")]
    public FoliagePalette palette;
    [Tooltip("If 'Palette' is unassigned, load a FoliagePalette with this name from a Resources folder.")]
    public string paletteResourceName = "FoliagePalette";
    [Tooltip("Used only when no palette is found. Defaults to a single grass rule so grass works out of the box.")]
    public List<FoliageColourRule> inlineRules = new List<FoliageColourRule>
    {
        new FoliageColourRule
        {
            name = "Meadow Grass",
            useBiomeGradientRule = true,
            biomeIndex = 0,
            targetColour = new Color(0.22f, 0.45f, 0.02f, 1f),
            colourTolerance = 0.35f,
            targetCount = 600000,
            minSpacing = 0.35f,
            edgeDensity = 0.05f,
            densityFalloff = 1.6f,
            render = FoliageRenderMode.GpuInstanced,
            orient = FoliageOrientMode.AlignToSurface,
            scaleRange = new Vector2(0.8f, 1.5f),
            maxSlope = 80f,
            elevationRange = new Vector2(0f, 1f),
            surfaceOffset = 0.02f,
        }
    };

    [Header("Burst jobs")]
    [Tooltip("DISABLE running the expensive analytic surface sampling (noise elevation + finite-difference " +
             "surface normal + slope + normalized elevation) on Burst worker threads, reverting to the " +
             "original MAIN-THREAD analytic path (identical placement, just slower). OFF by default so " +
             "Burst is ON automatically — including on the existing scene component, which deserializes " +
             "this new field to false (same pattern as 'Disable Culling'). The Burst path is what lets the " +
             "load radius and per-frame throughput be raised without hitching: the noise math (the dominant " +
             "cost, and ALL the wasted ocean probes) moves off-thread; only the colour/biome classification " +
             "+ placement stay on the main thread. Tick ON only if Burst is unavailable or for A/B debugging. " +
             "NOTE: the FIRST streamed cell after entering Play triggers a one-time Burst compile pause (a " +
             "brief hitch); subsequent cells use the cached compiled job.")]
    public bool disableBurstJobs = false;

    [Header("Scatter budget")]
    [Tooltip("Raycasts are spread across frames during scatter to avoid a load hang.")]
    [Min(500)] public int scatterPerFrame = 16000;
    [Tooltip("Reject foliage whose surface radius is below the ocean sea level plus this clearance " +
             "(world units). Stops trees/palms/rocks placing on seabed that still sits above the planet " +
             "base sphere but under the water shell. 0 = exactly at the waterline; ~1–2 keeps trunks dry.")]
    [Min(0f)] public float dryClearanceAboveSea = 1.25f;
    [Tooltip("Cap on GameObjectPool instantiations per frame to avoid hitches when placing trees/rocks. " +
             "Object.Instantiate is expensive and synchronous, so a high value lets one frame stall building " +
             "many prefabs. Default lowered to 50 (a few prefabs per frame, queued nearest-first); RAISE for " +
             "faster tree/rock pop-in, LOWER for smoother frames. NOTE: changing this code default only affects " +
             "components added AFTER the change — set the value on an existing scene component in the Inspector.")]
    [Min(16)] public int maxInstantiatesPerFrame = 50;
    [Tooltip("Multiplies the attempt budget over the summed target counts. This is a mostly-ocean planet, so most rays land in water and are wasted.")]
    [Min(2)] public int attemptBudgetMultiplier = 20;
    [Tooltip("STREAMING ONLY. While there's a BACKLOG of pending cells near the player (initial load, a " +
             "teleport, or fast travel), temporarily multiply the per-frame raycast budget ('Scatter Per " +
             "Frame') by this so the area around the player fills in FAST, then drop back to the steady rate " +
             "once caught up. Cells are generated nearest-first, so this burst is spent on the closest stuff. " +
             "1 = no burst (steady rate always). Higher fills faster but costs more per frame DURING the burst; " +
             "lower it toward 1 if you see frame spikes on load.")]
    [Min(1)] public int streamWarmupBurst = 2;
    [Tooltip("PRIORITY POP-IN. Multiplies the per-frame raycast budget ('Scatter Per Frame') ONLY while " +
             "scattering a cell within 'Near Cell Radius' of the player, so the IMMEDIATE vicinity fills in " +
             "fast while the many distant cells keep draining at the cheap steady rate. Because near cells are " +
             "few and generated one-at-a-time nearest-first, this spikes per-frame cost only briefly and only " +
             "for the stuff the player is standing in — it does NOT raise the sustained cost the way cranking " +
             "'Scatter Per Frame' globally would. RAISE if nearby foliage still pops in too slowly; LOWER " +
             "toward 1 if you get a brief frame hitch when walking into fresh terrain. 1 = no near boost.")]
    [Min(1)] public int nearCellBudgetMultiplier = 3;
    [Tooltip("PRIORITY POP-IN. World-unit radius around the player treated as the 'immediate vicinity' that " +
             "gets the 'Near Cell Budget Multiplier'. Cells whose centre is within this distance fill fast; " +
             "everything farther uses the steady rate. Keep this around the close foreground (a cell or two " +
             "out). Bigger = more cells get the boost (fills more, costs more per frame); smaller = tighter, " +
             "cheaper boost. 0 = built-in default (90).")]
    public float nearCellRadius = 90f;
    [Tooltip("SMOOTHNESS CEILING. Hard cap on TOTAL streaming raycasts in any single frame, AFTER the warmup " +
             "and near-cell multipliers are applied. This is what stops the periodic stutter when you walk " +
             "into fresh terrain: without it, a cell becoming 'near' would dump 'Scatter Per Frame' × 'Near " +
             "Cell Budget Multiplier' rays into ONE frame (e.g. 10000 × 3 = 30000) — a spike every time you " +
             "cross a cell boundary. With this cap the per-frame cost can never exceed the ceiling, so frames " +
             "stay even. 0 = auto (1.5 × Scatter Per Frame). RAISE for faster pop-in (more rays/frame, bigger " +
             "spikes); LOWER toward 'Scatter Per Frame' for maximum smoothness (slower pop-in).")]
    [Min(0)] public int maxStreamRaysPerFrame = 0;
    [Tooltip("SMOOTHNESS CEILING (the main pop-in knob now that placement is analytic, not raycast). Hard cap " +
             "on the number of EXPENSIVE surface evaluations per frame — i.e. attempts that land on dry ground " +
             "and run the full pipeline (surface normal + colour/zone classification + rule density). Ocean " +
             "attempts are now nearly free (one noise sample, then rejected) so they DON'T count here; only the " +
             "costly land work does. Capping it stops a land-heavy cell near the player from collapsing hundreds " +
             "of full surface evaluations into a single frame (the remaining pop-in hitch), spreading them over " +
             "a few frames instead. It does NOT change final density (the same instances are still placed, just " +
             "over more frames) and it does NOT slow ocean cells (their cheap probes still drain at 'Scatter Per " +
             "Frame'). 0 = auto (scatterPerFrame / 8, clamped 1000..4000). RAISE for faster land pop-in (bigger " +
             "hitches); LOWER for maximum smoothness (land fills in over more frames).")]
    [Min(0)] public int maxSurfaceEvalsPerFrame = 0;
    [Tooltip("SMOOTHNESS RAMP. How many frames the per-frame raycast budget takes to ease from the steady rate " +
             "up to the boosted rate when a cell becomes 'near' (or a warmup burst engages), instead of " +
             "jumping straight to the multiplied budget in a single frame. A higher value spreads the boost " +
             "more gently (smoother, slightly slower to reach full speed); 1 = no ramp (instant boost, the old " +
             "cliff behaviour). Only matters when a multiplier is active.")]
    [Min(1)] public int nearCellBudgetRampFrames = 6;
    [Tooltip("SMOOTHNESS CAP. Maximum number of cells whose scatter may COMPLETE back-to-back within a single " +
             "frame before the streamer forces a frame yield. Each finished cell finalizes its grass batch and " +
             "pooled bounds, so letting many tiny cells complete in one frame piles up that finalize/pool work " +
             "into a spike. Capping it spreads that cost across frames. RAISE for faster pop-in (more cells " +
             "settle per frame); LOWER (toward 1) for maximum smoothness.")]
    [Min(1)] public int maxCellsStartedPerFrame = 2;

    [Header("Area limits (global)")]
    [Tooltip("How high above the surface rays start (world units). Should clear the tallest terrain.")]
    public float rayHeightAboveSurface = 80f;

    [Header("Scaling")]
    [Tooltip("If OFF (the default), pooled trees/rocks/palms keep their authored world size (the rule's " +
             "scaleRange) even when the Planet Transform is scaled up: they're parented to a container " +
             "whose scale cancels the planet's, so only MORE of them seat on the larger surface — not " +
             "bigger ones. (GPU grass already uses world-space matrices and never inherits planet scale.) " +
             "Turn ON to let pooled foliage inherit the planet Transform scale (old behaviour: foliage " +
             "grows with the planet). Default is OFF so existing scene drivers get constant-size foliage.")]
    public bool inheritPlanetScale = false;

    [Tooltip("Scatter MORE instances on a larger planet (and fewer on a smaller one) so closeness/density " +
             "stays constant instead of thinning out as the surface area grows. Leave OFF (default) to KEEP " +
             "this area-proportional scaling ON; tick to DISABLE it and always use the authored counts.")]
    public bool dontScaleCountWithArea = false;
    [Tooltip("Baseline planet radius (world units) at which the authored targetCounts give the density you " +
             "like. 0 = auto-capture the planet's UNSCALED authored radius on first scatter, so the authored " +
             "counts are the baseline density at scale 1 and larger planets get proportionally more. Set this " +
             "explicitly to pin a baseline (needed if you densify via the planetRadius regen path).")]
    public float densityReferenceRadius = 0f;
    [Tooltip("Hard cap on the area-scaled instance count per rule, to avoid runaway counts / load hangs on " +
             "very large planets. 0 = use the built-in default (2,000,000).")]
    public int maxInstancesPerRule = 2000000;

    [Header("Phase-in (spawn-in animation)")]
    [Tooltip("Smoothly GROW foliage in (scale 0->full with a smoothstep ease) the moment it is first streamed/" +
             "spawned, instead of popping in abruptly. Robust scale-in only (no transparency), so the matte " +
             "opaque lighting and GPU instancing batching are untouched. NOTE: this field deserializes to FALSE " +
             "on a PRE-EXISTING scene component, so tick it ON in the Inspector on the existing FoliageByColour " +
             "to enable the effect there (new components added after this change default to ON).")]
    public bool phaseInEnabled = true;
    [Tooltip("Seconds a freshly spawned instance/object takes to grow from 'Phase In Start Scale' to its full " +
             "authored size. 0 = built-in default (0.6s). Sensible range ~0.4-0.8s.")]
    public float phaseInDuration = 0.6f;
    [Tooltip("Fraction of full size an instance starts at when it phases in (0 = sprouts from nothing, 0.05 = " +
             "starts at 5%). Clamped to [0, 0.95]. The grow is anchored at the BASE (ground contact along the " +
             "surface normal) so foliage rises out of the ground rather than scaling about its midpoint.")]
    [Range(0f, 0.95f)] public float phaseInStartScale = 0.05f;

    [Header("Culling / LOD")]
    [Tooltip("Disable ALL render-time culling (draw every grass instance + keep every pooled object active " +
             "every frame). OFF by default so culling is ON automatically — including on the existing scene " +
             "component, which deserializes this new field to false. Tick only to debug / compare.")]
    public bool disableCulling = false;
    [Tooltip("Optional explicit camera to cull against. Leave EMPTY to auto-detect the player camera " +
             "(Camera.main, else the highest-depth enabled on-screen camera). Assign your gameplay camera " +
             "only if auto-detection ever picks the wrong one.")]
    public Camera cullingCamera;
    [Tooltip("Max distance (world units) from the camera at which GPU-instanced grass chunks are drawn. " +
             "0 = use the built-in default (200). Lower = more culling / faster; raise to see grass farther.")]
    public float grassDrawDistance = 200f;
    [Tooltip("Max distance (world units) from the camera at which pooled trees/rocks/palms stay active. " +
             "0 = use the built-in default (400). Objects are visible farther than grass by default.")]
    public float objectDrawDistance = 400f;
    [Tooltip("Pooled trees/rocks are also FRUSTUM-culled (deactivated when outside the camera view), not just " +
             "distance-culled. This margin (world units) activates a chunk slightly BEFORE it enters the visible " +
             "frustum so solid objects don't visibly pop in when you turn. 0 = built-in default (15). A small " +
             "extra hysteresis band on top prevents on/off flicker right at the edge.")]
    public float objectFrustumMargin = 0f;
    [Tooltip("World-unit size of the spatial culling cells. Grass + pooled objects are bucketed into this grid " +
             "so whole chunks are distance/frustum culled at once (per-frame work scales with VISIBLE chunks, " +
             "not total instances). 0 = built-in default (40). Bigger = cheaper but coarser culling.")]
    public float chunkSize = 40f;
    [Tooltip("Optional per-frame cap on the number of GPU grass instances submitted, shared across all grass " +
             "rules. Visible chunks are drawn NEAREST-FIRST, so when this cap is hit it's the FARTHEST chunks " +
             "that get skipped — nearby grass always renders. 0 = built-in default (300,000), which only kicks " +
             "in under heavy load. Set very high to effectively disable the cap (ordering still applies).")]
    public int maxVisibleGrassInstancesPerFrame = 0;

    [Header("Streaming (player-centered)")]
    [Tooltip("Stream foliage in/out around the player instead of populating the WHOLE planet up front. " +
             "ON by default: only surface cells within 'Load Radius' of the player exist at any time, so " +
             "instance counts / memory stay bounded and you never see the whole planet fill in. Turn OFF " +
             "to use the legacy one-shot 'scatter the entire planet' behaviour.")]
    public bool streamingEnabled = true;
    [Tooltip("World-unit radius around the player within which foliage is generated. Now that the heavy " +
             "sampling runs on Burst worker threads (see 'Use Burst Jobs'), this can be pushed further out " +
             "so foliage loads further ahead without hitching. Default raised to 350. 0 = built-in default " +
             "(350). NOTE: changing this code default only affects components added AFTER the change — set " +
             "the value on the existing scene component in the Inspector to load further on it.")]
    public float loadRadius = 350f;
    [Tooltip("LOAD AHEAD. Shift the nearest-first generation PRIORITY this many world units along the " +
             "camera's look/move direction, so cells in front of the player are generated before cells " +
             "behind — foliage is ready before you arrive. This biases only the ORDER cells load in, not " +
             "WHICH cells load or unload (so it never causes thrash). 0 = built-in default (80) — applied " +
             "automatically (incl. the existing scene component, which deserializes this new field to 0). " +
             "Set NEGATIVE to disable (pure nearest-first). Keep it under Load Radius.")]
    public float loadAheadDistance = 0f;
    [Tooltip("Extra distance (world units) BEYOND Load Radius before a loaded cell is unloaded. This " +
             "hysteresis band stops cells on the boundary thrashing load/unload as you move. 0 = default (60).")]
    public float unloadHysteresis = 60f;
    [Tooltip("Re-evaluate which cells should be loaded only after the player has moved at least this far " +
             "(world units) since the last evaluation. Keeps streaming cheap while standing still / moving " +
             "slowly. 0 = built-in default (20).")]
    public float restreamMoveThreshold = 20f;
    [Tooltip("Tag used to find the player position when no culling/gameplay camera can be resolved. " +
             "Defaults to 'Player'.")]
    public string playerTag = "Player";

    [Header("Diagnostics")]
    [Tooltip("Paste a world position, then right-click the component header -> Diagnose Probe Position.")]
    public Vector3 debugProbePosition;
    public bool logResults = true;

    const int BatchSize = 1023; // Graphics.DrawMeshInstanced hard limit per call.

    // Greenness window for the grass-parity (biome-gradient) rule's smooth edge feather. Values are
    // in the units of ColourGenerator.GetBiomeGradientGreenness (0 = non-green, ~0.07 on beach-yellow
    // key1, ~0.73 at the first solid-green key2, 1.0 in the core green keys key3/key4). Below 'Lo' the
    // surface reads non-green so grass is excluded; at/above 'Hi' density is full; between them density
    // smoothstep-ramps so grass blends into olive / yellow-green / near-beach bands and thins toward
    // rock instead of cutting off hard at solid green only.
    const float GradientGreenLo = 0.08f;
    const float GradientGreenHi = 0.48f;

    // Fallback cap used when maxInstancesPerRule deserializes to 0 on an existing scene component.
    const int DefaultMaxInstancesPerRule = 2000000;

    // Built-in culling defaults, used when the matching field deserializes to 0 on an existing scene
    // component (so culling works with good values and NO scene edit). disableCulling deserializes to
    // false on the existing component => culling is ON by default.
    const float DefaultGrassDrawDistance = 200f;
    const float DefaultObjectDrawDistance = 400f;
    const float DefaultChunkSize = 40f;
    // Small "border" around the camera frustum (world units). The single culling rule is: a chunk draws
    // when it is inside the camera frustum expanded by this small margin, otherwise it is culled.
    const float DefaultObjectFrustumMargin = 4f;
    // Small frustum border used for GPU grass chunks (matches the pooled-object border).
    const float SmallFrustumMargin = 4f;
    // Distance is intentionally NOT used to restrict in-view foliage: anything inside the frustum draws
    // however far it is. This effectively-unlimited value keeps the existing distance test from ever
    // culling a visible chunk, leaving frustum + small border as the sole deciding rule.
    const float EffectivelyUnlimitedDrawDistance = 1e9f;
    // Per-frame grass instance budget used when maxVisibleGrassInstancesPerFrame deserializes to 0. High
    // enough that it only bites under heavy load; nearest-first draw order means skips are the farthest chunks.
    const int DefaultMaxVisibleGrassInstancesPerFrame = 300000;

    // Phase-in default used when phaseInDuration deserializes to 0 on an existing scene component.
    const float DefaultPhaseInDuration = 0.6f;

    // Streaming defaults, used when the matching serialized field is <= 0 (so streaming works with good
    // values and no scene edit on the existing component).
    const float DefaultLoadRadius = 350f;
    const float DefaultUnloadHysteresis = 60f;
    // Look-ahead generation bias used when loadAheadDistance deserializes to 0 (so the bias is on by
    // default, including on the existing scene component). A negative loadAheadDistance disables it.
    const float DefaultLoadAhead = 80f;
    const float DefaultRestreamMoveThreshold = 20f;
    // Safety clamp on the per-axis cell scan range, so an extreme loadRadius can't freeze the recompute.
    const int MaxCellScanRange = 64;
    // Pending-cell count above which the streaming warmup burst engages (initial load / teleport / fast
    // travel). Below it the steady scatterPerFrame rate is used so ordinary slow walking never bursts/spikes.
    const int StreamBurstBacklogCells = 8;
    // Near-cell priority defaults, used when the matching serialized field deserializes to 0 on an existing
    // scene component (so the immediate-vicinity boost works with no scene edit). The boost is applied ONLY
    // to cells within DefaultNearCellRadius of the player, so per-frame cost spikes only briefly and only for
    // the foliage the player is standing in.
    const int DefaultNearCellBudgetMultiplier = 3;
    const float DefaultNearCellRadius = 90f;

    // ---- GPU-instanced batch set (one per GpuInstanced rule). Mirrors GpuGrassCarpet's draw path. ----
    class SubMeshDraw
    {
        public Mesh mesh;
        public int subMesh;
        public Material material;
        public Matrix4x4 relMatrix;
        public int indexInAll; // position within GpuBatchSet.allDraws (used to index per-chunk matrix buckets)
    }

    // A spatial grid cell of GPU grass. Holds this cell's matrices (per draw, pre-split into <=1023 batches)
    // plus a world-space bound so the whole chunk can be distance + frustum culled in one cheap test/frame.
    class GpuChunk
    {
        // During scatter: one growable matrix list per allDraws index (null until first instance hits it).
        public List<Matrix4x4>[] building;
        // After FinalizeBatches: batches[drawIndex][batchIndex] = up-to-1023 world matrices.
        public Matrix4x4[][][] batches;
        public Vector3 min, max;
        public bool hasBounds;
        public Vector3 center;
        public float radius;
        public Bounds bounds;
        public int instanceCount;  // total matrices in this chunk (for the per-frame draw budget)
        public float sortDist;     // scratch: sqr distance to camera this frame (for nearest-first ordering)

        // ---- Phase-in (spawn-in grow) ----
        // Time.time when this chunk was finalized (= first spawned). <0 once the phase-in has completed/settled,
        // so settled chunks pay ZERO phase-in cost. Set on finalize regardless of whether phase-in is enabled.
        public float phaseSpawnTime = -1f;
        // Lazily-allocated scratch holding the current-frame SCALED copy of 'batches' while this chunk is still
        // growing in. Null when the chunk is settled (freed on completion) so settled grass uses 'batches'
        // directly with no extra memory. Reused across frames during the (brief) phase-in window.
        public Matrix4x4[][][] scaled;
    }

    class PrefabModel
    {
        public readonly List<SubMeshDraw> draws = new List<SubMeshDraw>();
    }

    class GpuBatchSet
    {
        public readonly List<PrefabModel> models = new List<PrefabModel>();
        public readonly List<SubMeshDraw> allDraws = new List<SubMeshDraw>();
        public readonly List<Material> ownedMaterials = new List<Material>();
        public ShadowCastingMode shadowCasting = ShadowCastingMode.Off;
        public bool receiveShadows = true;

        // Spatial chunking: grass instances are bucketed by world-position cell so per-frame rendering can
        // skip whole chunks that are too far / off-screen. Keyed by floor(pos / chunkSize).
        public float chunkSize = DefaultChunkSize;
        readonly Dictionary<Vector3Int, GpuChunk> chunks = new Dictionary<Vector3Int, GpuChunk>();

        // Reused each frame to hold the (small) set of visible chunks for nearest-first sorting — avoids
        // per-frame heap allocations. The comparer is a cached static delegate (no per-frame lambda alloc).
        readonly List<GpuChunk> _visible = new List<GpuChunk>();
        static readonly System.Comparison<GpuChunk> NearestFirst = (a, b) => a.sortDist.CompareTo(b.sortDist);

        public bool BuildModels(List<GameObject> prefabs, bool forceDoubleSided)
        {
            if (prefabs == null || prefabs.Count == 0)
                return false;

            foreach (var prefab in prefabs)
            {
                if (prefab == null)
                    continue;

                var model = new PrefabModel();
                var rootToLocal = prefab.transform.worldToLocalMatrix;

                foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
                {
                    var mesh = mf.sharedMesh;
                    if (mesh == null)
                        continue;
                    var renderer = mf.GetComponent<MeshRenderer>();
                    if (renderer == null)
                        continue;

                    var sharedMats = renderer.sharedMaterials;
                    Matrix4x4 rel = rootToLocal * mf.transform.localToWorldMatrix;

                    int subCount = Mathf.Max(1, mesh.subMeshCount);
                    for (int s = 0; s < subCount; s++)
                    {
                        Material src = (sharedMats != null && s < sharedMats.Length) ? sharedMats[s] : null;
                        if (src == null && sharedMats != null && sharedMats.Length > 0)
                            src = sharedMats[0];
                        if (src == null)
                            continue;

                        var instanced = new Material(src) { enableInstancing = true };
                        // Match the matte planet terrain: drop specular/reflections so foliage reads
                        // as the same diffuse-only surface as the ground it sits on (handles both URP
                        // Lit and the glTFast shader the Kenney GLBs import with). Visual-only; does
                        // not affect placement/streaming/culling.
                        ModelMatteLighting.MakeMatte(
                            instanced,
                            matchTerrainTerminator: true,
                            ambientFill: ModelMatteLighting.FoliageAmbientFill,
                            diffuseScale: ModelMatteLighting.FoliageDiffuseScale);
                        // Re-assert instancing after matte tuning (shader stays glTF / URP Lit).
                        instanced.enableInstancing = true;
                        if (forceDoubleSided)
                        {
                            if (instanced.HasProperty("_Cull"))
                                instanced.SetInt("_Cull", 0);
                            else if (instanced.HasProperty("_CullMode"))
                                instanced.SetInt("_CullMode", 0);
                        }
                        ownedMaterials.Add(instanced);

                        var draw = new SubMeshDraw
                        {
                            mesh = mesh,
                            subMesh = s,
                            material = instanced,
                            relMatrix = rel
                        };
                        draw.indexInAll = allDraws.Count;
                        model.draws.Add(draw);
                        allDraws.Add(draw);
                    }
                }

                if (model.draws.Count > 0)
                    models.Add(model);
            }

            return models.Count > 0;
        }

        public void AddInstance(Matrix4x4 baseTRS)
        {
            if (models.Count == 0)
                return;
            var model = models[Random.Range(0, models.Count)];

            // World position lives in the matrix translation column; bucket it into a spatial cell.
            Vector3 pos = new Vector3(baseTRS.m03, baseTRS.m13, baseTRS.m23);
            float inv = 1f / Mathf.Max(0.0001f, chunkSize);
            var cell = new Vector3Int(
                Mathf.FloorToInt(pos.x * inv),
                Mathf.FloorToInt(pos.y * inv),
                Mathf.FloorToInt(pos.z * inv));

            if (!chunks.TryGetValue(cell, out var chunk))
            {
                chunk = new GpuChunk { building = new List<Matrix4x4>[allDraws.Count] };
                chunks[cell] = chunk;
            }
            if (chunk.building == null)
                return; // chunk already finalized (streaming): don't append after batches are built

            if (!chunk.hasBounds) { chunk.min = chunk.max = pos; chunk.hasBounds = true; }
            else { chunk.min = Vector3.Min(chunk.min, pos); chunk.max = Vector3.Max(chunk.max, pos); }

            for (int i = 0; i < model.draws.Count; i++)
            {
                var d = model.draws[i];
                int idx = d.indexInAll;
                var list = chunk.building[idx];
                if (list == null) { list = new List<Matrix4x4>(); chunk.building[idx] = list; }
                list.Add(baseTRS * d.relMatrix);
            }
        }

        // Whole-set finalize (used by the legacy one-shot scatter path when streaming is OFF).
        public void FinalizeBatches()
        {
            foreach (var kv in chunks)
                FinalizeOneChunk(kv.Value);
        }

        // Streaming: finalize just the chunk for one cell as it loads (no-op if the cell has no instances
        // or is already finalized). Draw() can then pick it up immediately.
        public void FinalizeChunk(Vector3Int cell)
        {
            if (chunks.TryGetValue(cell, out var chunk))
                FinalizeOneChunk(chunk);
        }

        // Streaming: drop a cell's grass entirely (matrices are released for GC). The per-rule owned
        // materials are shared across chunks, so they are NOT disposed here.
        public void RemoveChunk(Vector3Int cell)
        {
            chunks.Remove(cell);
        }

        void FinalizeOneChunk(GpuChunk chunk)
        {
            if (chunk.building == null)
                return; // already finalized

            // Pad bounds so meshes that extend past their pivot (and the chunk's own cell extent) are not
            // clipped early by the frustum test.
            float pad = Mathf.Max(2f, chunkSize * 0.5f);
            Vector3 padV = new Vector3(pad, pad, pad);

            chunk.batches = new Matrix4x4[allDraws.Count][][];
            chunk.instanceCount = 0;
            for (int di = 0; di < allDraws.Count; di++)
            {
                var list = chunk.building != null ? chunk.building[di] : null;
                if (list == null || list.Count == 0)
                {
                    chunk.batches[di] = System.Array.Empty<Matrix4x4[]>();
                    continue;
                }
                int total = list.Count;
                chunk.instanceCount += total;
                int batchCount = (total + BatchSize - 1) / BatchSize;
                var b = new Matrix4x4[batchCount][];
                for (int bi = 0; bi < batchCount; bi++)
                {
                    int start = bi * BatchSize;
                    int len = Mathf.Min(BatchSize, total - start);
                    var arr = new Matrix4x4[len];
                    list.CopyTo(start, arr, 0, len);
                    b[bi] = arr;
                }
                chunk.batches[di] = b;
            }
            chunk.building = null;

            Vector3 bmin = chunk.min - padV;
            Vector3 bmax = chunk.max + padV;
            chunk.center = (bmin + bmax) * 0.5f;
            Vector3 ext = (bmax - bmin) * 0.5f;
            chunk.radius = ext.magnitude;
            chunk.bounds = new Bounds(chunk.center, bmax - bmin);

            // Stamp the spawn time so Draw() can grow this chunk's instances in over phaseInDuration.
            // (Cheap float; phase-in is only actually applied if the caller passes phaseEnabled to Draw.)
            chunk.phaseSpawnTime = Time.time;
        }

        // Builds (or reuses) chunk.scaled as a per-frame copy of chunk.batches with every instance's 3x3 basis
        // multiplied by 's' — i.e. each instance uniformly scaled about its OWN origin (the translation column,
        // which for grass is the ground-contact/base point), leaving the translation untouched so the grass
        // grows up out of the surface rather than from the chunk centre or the planet core. Allocates the
        // scratch ONCE on the first phasing frame and reuses it for the rest of the (brief) window.
        static Matrix4x4[][][] BuildScaled(GpuChunk chunk, float s)
        {
            var batches = chunk.batches;
            var dst = chunk.scaled;
            if (dst == null || dst.Length != batches.Length)
            {
                dst = new Matrix4x4[batches.Length][][];
                chunk.scaled = dst;
            }
            for (int di = 0; di < batches.Length; di++)
            {
                var sb = batches[di];
                if (sb == null) { dst[di] = null; continue; }
                var db = dst[di];
                if (db == null || db.Length != sb.Length)
                {
                    db = new Matrix4x4[sb.Length][];
                    dst[di] = db;
                }
                for (int bi = 0; bi < sb.Length; bi++)
                {
                    var sArr = sb[bi];
                    var dArr = db[bi];
                    if (dArr == null || dArr.Length != sArr.Length)
                    {
                        dArr = new Matrix4x4[sArr.Length];
                        db[bi] = dArr;
                    }
                    for (int k = 0; k < sArr.Length; k++)
                    {
                        Matrix4x4 m = sArr[k];
                        m.m00 *= s; m.m01 *= s; m.m02 *= s;
                        m.m10 *= s; m.m11 *= s; m.m12 *= s;
                        m.m20 *= s; m.m21 *= s; m.m22 *= s;
                        dArr[k] = m;
                    }
                }
            }
            return dst;
        }

        public int DrawnInstanceCount()
        {
            int total = 0;
            foreach (var kv in chunks)
            {
                var chunk = kv.Value;
                if (chunk.batches == null)
                    continue;
                foreach (var db in chunk.batches)
                    if (db != null)
                        foreach (var batch in db)
                            total += batch.Length;
            }
            return total;
        }

        // Draws the chunks that pass the camera FRUSTUM (+ small border) test, NEAREST-FIRST so the grass
        // closest to the camera renders first (better early-Z/overdraw + a meaningful priority under budget).
        // Frustum planes are computed once per frame by the caller. Per-frame cost scales with VISIBLE chunks.
        // 'cam' is the resolved gameplay camera: it is passed to Graphics.DrawMeshInstanced so grass renders
        // ONLY into that camera (not every camera / the editor Scene view), which is what made far-side and
        // behind-camera grass appear before. drawDistance is effectively unlimited (see caller) so distance
        // never removes in-view chunks; the frustum + border is the sole deciding rule.
        // budgetRemaining caps total instances submitted this frame across all grass sets; because chunks are
        // sorted near->far, the chunks skipped when the budget runs out are the FARTHEST ones.
        public void Draw(int layer, bool cull, Vector3 camPos, Plane[] planes, float drawDistance,
                         float frustumMargin, Camera cam, ref int budgetRemaining,
                         bool phaseEnabled, float phaseDur, float phaseStartScale, float now)
        {
            // 1) Gather visible chunks into the reused scratch list and stamp each with its sqr-distance.
            _visible.Clear();
            foreach (var kv in chunks)
            {
                var chunk = kv.Value;
                if (chunk.batches == null)
                    continue;

                float sd = (chunk.center - camPos).sqrMagnitude;
                if (cull)
                {
                    float maxD = drawDistance + chunk.radius;
                    if (sd > maxD * maxD)
                        continue;
                    if (planes != null && !GeometryUtility.TestPlanesAABB(planes, Expanded(chunk.bounds, frustumMargin)))
                        continue;
                }
                chunk.sortDist = sd;
                _visible.Add(chunk);
            }

            // 2) Sort the small visible set nearest-first (no allocation: in-place sort, cached comparer).
            if (_visible.Count > 1)
                _visible.Sort(NearestFirst);

            // 3) Submit nearest-first, stopping once the shared per-frame instance budget is exhausted.
            for (int ci = 0; ci < _visible.Count; ci++)
            {
                if (budgetRemaining <= 0)
                    break; // remaining (farthest) chunks are dropped this frame
                var chunk = _visible[ci];

                // Pick the matrices to submit: the settled authored 'batches', or a grown-in scaled copy while
                // this chunk is still within its phase-in window. Settled chunks (the overwhelming majority) take
                // the cheap path with no scaling and no extra memory.
                var srcBatches = chunk.batches;
                if (phaseEnabled && chunk.phaseSpawnTime >= 0f)
                {
                    float age = now - chunk.phaseSpawnTime;
                    if (age < phaseDur)
                    {
                        float t01 = phaseDur > 0f ? Mathf.Clamp01(age / phaseDur) : 1f;
                        float g = t01 * t01 * (3f - 2f * t01); // smoothstep ease
                        float s = Mathf.Lerp(phaseStartScale, 1f, g);
                        srcBatches = BuildScaled(chunk, s);
                    }
                    else
                    {
                        // Window elapsed: settle permanently (free the scratch, stop paying any phase-in cost).
                        chunk.scaled = null;
                        chunk.phaseSpawnTime = -1f;
                    }
                }

                for (int di = 0; di < srcBatches.Length; di++)
                {
                    var drawBatches = srcBatches[di];
                    if (drawBatches == null || drawBatches.Length == 0)
                        continue;
                    var d = allDraws[di];
                    for (int bi = 0; bi < drawBatches.Length; bi++)
                    {
                        var batch = drawBatches[bi];
                        Graphics.DrawMeshInstanced(
                            d.mesh, d.subMesh, d.material,
                            batch, batch.Length, null,
                            shadowCasting, receiveShadows, layer, cam);
                    }
                }
                budgetRemaining -= chunk.instanceCount;
            }
        }

        public void Dispose()
        {
            foreach (var m in ownedMaterials)
                if (m != null)
                    Object.Destroy(m);
            ownedMaterials.Clear();
        }
    }

    // A spatial grid cell of pooled GameObjects. Toggled active/inactive as a unit by distance culling so we
    // never SetActive() thousands of individual objects per frame — only on chunk visibility transitions.
    class ObjChunk
    {
        public readonly List<GameObject> objects = new List<GameObject>();
        public Vector3 min, max;
        public bool hasBounds;
        public Vector3 center;
        public float radius;
        public Bounds bounds; // world AABB for frustum testing
        public bool active = true;
        public float sortDist; // scratch: sqr distance to camera this frame (for nearest-first activation)
        public Vector3Int cell;  // owning streaming cell (for unload bookkeeping)
    }

    // Cached comparer so the per-frame pooled-chunk sort allocates no delegate.
    static readonly System.Comparison<ObjChunk> ObjNearestFirst = (a, b) => a.sortDist.CompareTo(b.sortDist);

    // ---- Per-rule runtime state for the shared scatter pass ----
    class RuleRuntime
    {
        public FoliageColourRule rule;
        public int runtimeIndex;              // position within _runtimes (so zone lookups map back to per-cell arrays)
        public List<GameObject> prefabs;      // resolved prefabs (may differ from rule.prefabs for grass fallback)
        public GpuBatchSet gpu;               // non-null for GpuInstanced
        public Transform poolContainer;       // non-null for GameObjectPool
        public HashSet<Vector3Int> occupied = new HashSet<Vector3Int>();
        public float invCell = 1f;
        public int placed;
        public int effectiveTarget; // rule.targetCount scaled by planet surface area (see Scatter)
        public float cellTargetF;   // streaming: expected instances of this rule per surface cell (fractional)
        // Pooled-object spatial buckets for distance culling (GameObjectPool rules only).
        public Dictionary<Vector3Int, ObjChunk> objChunkMap;
        public List<ObjChunk> objChunks; // flat list built at finalize time for cheap per-frame iteration
        // telemetry
        public int rejSlope, rejElev, rejDensity, rejSpacing;
    }

    readonly List<RuleRuntime> _runtimes = new List<RuleRuntime>();

    // ---- Nearest-of-palette zone state (NearestPaletteColour mode) ----
    // One entry per DISTINCT rule targetColour (the palette's colour zones). _zoneRuntimes[z] holds every
    // runtime that places for that colour (e.g. grass + scattered trees share a green zone); an empty list
    // = a 'place nothing' zone (water/blue). A surface point is classified to its nearest _zoneColours[z].
    List<Color> _zoneColours;
    List<List<RuleRuntime>> _zoneRuntimes;

    // Gradient "carpet" rules (useBiomeGradientRule). Placed independently of colour-zone winner-take-all
    // so grass can blanket green land and feather out at beach/desert/snow instead of living in discrete
    // palette-colour patches. Not registered in _zoneColours.
    List<RuleRuntime> _carpetRuntimes;

    // Result of a single placement attempt (lets the shared helper report a pooled instantiate so the
    // caller can charge the per-frame instantiate budget without the helper needing to be a coroutine).
    enum PlaceResult { Skipped, PlacedGpu, PlacedPooled }

    Planet _planet;
    int _layer;
    bool _ready;
    Camera _cam;
    readonly Plane[] _frustumPlanes = new Plane[6];

    // ---- Streaming state ----
    // Surface sampling params, captured once after the planet has generated (constant thereafter).
    Vector3 _center;
    float _baseRadius, _maxRadius, _rayStartRadius, _rayLength;
    // Minimum world radius at which foliage may place. Uses the ocean surface (not the planet base
    // sphere): this planet's sea level sits ABOVE the shape base (~305 vs ~300), so gating on
    // baseRadius alone left a multi-unit underwater band that still got trees/palms.
    float _waterLineRadius;
    int _groundMask, _numBiomes;
    // Cells currently populated, cells we WANT populated this eval, and the pending load queue (+ a mirror
    // set so we never enqueue the same cell twice).
    readonly HashSet<Vector3Int> _loadedCells = new HashSet<Vector3Int>();
    readonly HashSet<Vector3Int> _desiredCells = new HashSet<Vector3Int>();
    readonly HashSet<Vector3Int> _queued = new HashSet<Vector3Int>();
    // Pending cells awaiting generation, kept in NEAREST-FIRST priority order. The list is sorted
    // FARTHEST-first against the live player position so the NEAREST pending cell sits at the END and pops
    // off in O(1) (RemoveAt(last)). Re-sorted on every streaming recompute (cell-cross / move threshold) so
    // as the player moves the closest unloaded cells are always generated before farther ones. This replaces
    // the old FIFO Queue, which drained in raster grid-scan order and therefore generated the far CORNER of
    // the load region before the player's immediate surroundings — the reason near foliage was slow to pop in.
    readonly List<Vector3Int> _loadList = new List<Vector3Int>();
    Vector3 _sortPlayerPos;                          // player position captured for the current pending re-sort
    Vector3 _lastSortPlayerPos;                       // player position at the last actual re-sort (skip guard)
    bool _hasSortPos;                                 // whether _lastSortPlayerPos is valid yet
    System.Comparison<Vector3Int> _farthestFirst;    // cached comparer (no per-sort delegate allocation)
    readonly List<Vector3Int> _unloadScratch = new List<Vector3Int>();
    Vector3 _lastEvalPos;
    bool _hasEvalPos;
    // Shared per-frame work budget so loading MANY cells in one frame still can't hitch: these accumulate
    // across consecutive cells and reset only after a frame yield.
    int _streamRayCount;
    int _streamInstCount;
    // Counts EXPENSIVE surface evaluations (land attempts that passed the water gate and ran the full
    // normal + colour/zone + density pipeline) since the last frame yield. Bounded by
    // EffectiveMaxSurfaceEvalsPerFrame so a land-heavy near cell can't dump its whole surface-evaluation
    // cost into one frame — the remaining pop-in hitch. Cheap ocean rejects don't increment this.
    int _streamSurfaceEvalCount;
    // Smoothness ramp: the per-frame raycast budget multiplier currently in effect, eased toward the target
    // multiplier (warmup burst / near-cell boost) by AdvanceStreamRamp() at each frame yield. Starting at 1
    // and ramping up over nearCellBudgetRampFrames frames converts the old single-frame "× multiplier" cliff
    // into a gentle climb, so crossing into fresh terrain no longer spikes one frame. Decays back to 1 when no
    // boost is active.
    float _streamRayBudgetRamp = 1f;
    // Set at the start of each ScatterCell: is the cell currently being scattered inside the near-cell radius
    // of the player? When true, EffectiveStreamRayBudget boosts the per-frame ray budget so the immediate
    // vicinity fills fast. Reset to false when not streaming a near cell so distant cells stay cheap.
    bool _currentCellNear;

    // Reusable per-cell scratch for ScatterCell (sized to _runtimes.Count, which is constant after
    // BuildRuntimes). Only ONE ScatterCell runs at a time (StreamingRoutine awaits each), so sharing these
    // is safe and removes a per-cell heap allocation (int[]s + a HashSet per rule) that otherwise churned
    // the GC every time a cell streamed in — a stutter source while walking. HashSets are Clear()ed, not
    // reallocated, between cells.
    int[] _cellTargetScratch;
    int[] _cellPlacedScratch;
    HashSet<Vector3Int>[] _occupiedScratch;

    // ---- Burst job state ----
    // Blittable snapshot of the planet's shape/noise settings, built ONCE after the planet resolves (and
    // rebuilt if the planet changes). _perm is the constant 512-entry simplex permutation; _noiseLayers
    // mirrors shapeSettings.noiseLayers. The candidate direction / result buffers are allocated Persistent
    // and REUSED across cells (grown only when a denser cell needs more attempts), so streaming a cell
    // allocates no native garbage. Everything is disposed in OnDestroy / when the snapshot is rebuilt.
    NativeArray<int> _perm;
    NativeArray<NoiseLayerData> _noiseLayers;
    NativeArray<BuildingPadBurstData> _pads;
    NativeArray<float3> _dirArray;
    NativeArray<FoliageCandidate> _resArray;
    FoliageCandidate[] _acceptedScratch; // managed copy of accepted job hits (safe across yields)
    int _jobCapacity;
    JobHandle _pendingHandle;         // last scheduled sampling job (completed before any native disposal)
    bool _burstReady;                 // true when the snapshot is valid AND Burst isn't disabled
    float _planetRadiusLocal;         // shapeSettings.planetRadius
    float _scaleFactor;               // planet world scale (max lossy axis, guarded)
    float _elevMinLocal, _elevMaxLocal;
    // Per-cell deterministic seed base, so candidate directions are reproducible regardless of the order
    // cells stream in (XORed with the cell coords). Const => same planet always scatters the same way.
    const uint CellRngSeedBase = 0x9E3779B9u;
    // Hard cap on a single cell's candidate count (protects the native buffers from a pathological target).
    const int MaxCellCandidates = 200000;

    /// <summary>Active rule list: palette (assigned or Resources) first, else the inline fallback.</summary>
    public List<FoliageColourRule> GetActiveRules()
    {
        if (palette != null && palette.rules != null && palette.rules.Count > 0)
            return palette.rules;

        if (palette == null && !string.IsNullOrEmpty(paletteResourceName))
        {
            var loaded = Resources.Load<FoliagePalette>(paletteResourceName);
            if (loaded != null && loaded.rules != null && loaded.rules.Count > 0)
            {
                palette = loaded;
                return loaded.rules;
            }
        }

        return inlineRules;
    }

    /// <summary>Simple RGB euclidean colour distance. Chosen for predictability: tolerance reads as
    /// "how far in RGB space" and matches what the gradient editor shows. (Perceptual/HSV distance
    /// would weight hue differently and make the tolerance slider less intuitive.)</summary>
    public static float ColourDistance(Color a, Color b)
    {
        float dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
        return Mathf.Sqrt(dr * dr + dg * dg + db * db);
    }

    /// <summary>Componentwise reciprocal of a scale vector (1/x per axis; 1 where an axis is ~0). Used to
    /// build a child localScale that cancels a parent's lossy scale, so its children render at world size.</summary>
    static Vector3 InverseScale(Vector3 s)
    {
        return new Vector3(
            Mathf.Abs(s.x) < 1e-6f ? 1f : 1f / s.x,
            Mathf.Abs(s.y) < 1e-6f ? 1f : 1f / s.y,
            Mathf.Abs(s.z) < 1e-6f ? 1f : 1f / s.z);
    }

    /// <summary>Colour-swatch band membership strength m∈(0,1] for a rule at a point's key colour. -1 if no match.</summary>
    static float MatchStrength(FoliageColourRule rule, Color keyColour, int biomeIndex, int keyIndex)
    {
        if (rule.matchByKey)
            return (biomeIndex == rule.biomeIndex && keyIndex == rule.keyIndex) ? 1f : -1f;

        float tol = Mathf.Max(1e-4f, rule.colourTolerance);
        float dist = ColourDistance(keyColour, rule.targetColour);
        float m = 1f - Mathf.Clamp01(dist / tol);
        return m > 0f ? m : -1f;
    }

    /// <summary>
    /// Membership strength m for a rule at a world point. -1 means "does not qualify".
    ///  • useBiomeGradientRule (GRASS PARITY): m = biome 'biomeIndex's CONTINUOUS greenness (0..1) by
    ///    elevation (latitude-INDEPENDENT, via Planet.GetSurfaceGreennessAtPosition). A SOFT gate keeps
    ///    every point whose greenness is at least <see cref="GradientGreenLo"/> so the band edges can
    ///    feather smoothly; clearly yellow/brown ground (greenness below Lo) is rejected. (The old hard
    ///    boolean IsGreenKeyColor gate clipped the fade — it rejected everything below greenness ≈0.48
    ///    at the lower edge, so density cliffed instead of tapering.)
    ///  • otherwise: colour-swatch distance / matchByKey (see <see cref="MatchStrength"/>).
    /// Returns m in [0,1] when qualified, or -1 when it does not qualify.
    /// </summary>
    static float RuleMatchStrength(Planet planet, FoliageColourRule rule, Vector3 pos, Color keyColour, int biomeIndex, int keyIndex)
    {
        // Biome-dominance gate (ANY rule): restrict this rule to where 'requiredBiomeIndex' dominates the
        // surface colour. Used by the desert (biome 1) and snow (biome 2) areas. Reject below the weight
        // threshold; qualifying points return full membership (m=1) — area is defined by biome + the
        // rule's elevation/slope, density/borders by BiomeDominanceFactor + targetCount/spacing. Rules
        // with requireBiomeDominance=false (grass, forest, palms, rocks) skip this entirely → unchanged.
        if (rule.requireBiomeDominance)
        {
            if (planet == null)
                return -1f;
            float w = Mathf.Clamp01(planet.GetBiomeWeightAtPosition(pos, rule.requiredBiomeIndex));
            if (w < rule.minRequiredBiomeWeight)
                return -1f;
            return 1f;
        }

        if (rule.useBiomeGradientRule)
        {
            if (planet == null)
                return -1f;
            // Biome exclusion: reject where OTHER biomes (desert/snow) meaningfully colour the ground so
            // grass stays in its own biome. Hard-reject past the threshold (not just keepProb=0) so this
            // point stops being a grass CANDIDATE and other rules (e.g. rocks) can claim it instead.
            float other = 1f - Mathf.Clamp01(planet.GetBiomeWeightAtPosition(pos, rule.biomeIndex));
            if (rule.maxOtherBiomeInfluence < 1f && other >= rule.maxOtherBiomeInfluence)
                return -1f;
            float greenness = Mathf.Clamp01(planet.GetSurfaceGreennessAtPosition(pos, rule.biomeIndex));
            // Soft gate: keep weakly-green EDGE points (olive / near-beach / desert-blend) so KeepProb
            // can taper them, but reject clearly non-green ground (brown rock, deep beach yellow).
            return greenness >= GradientGreenLo ? greenness : -1f;
        }
        return MatchStrength(rule, keyColour, biomeIndex, keyIndex);
    }

    /// <summary>
    /// Density multiplier (0..1) that feathers a gradient rule out as OTHER biomes take over the surface
    /// colour: 1 while biome 'biomeIndex' clearly dominates, smoothstep down to 0 as the combined
    /// other-biome influence rises to <see cref="FoliageColourRule.maxOtherBiomeInfluence"/> — so grass
    /// blends into the desert/snow border instead of cutting off. 1 for non-gradient rules / when disabled.
    /// </summary>
    static float BiomeExclusionFactor(Planet planet, FoliageColourRule rule, Vector3 pos)
    {
        if (!rule.useBiomeGradientRule || planet == null)
            return 1f;
        float threshold = Mathf.Clamp01(rule.maxOtherBiomeInfluence);
        if (threshold >= 1f)
            return 1f; // exclusion disabled
        float other = 1f - Mathf.Clamp01(planet.GetBiomeWeightAtPosition(pos, rule.biomeIndex));
        if (other >= threshold)
            return 0f;
        // Feather over the top 40% of the allowed range (smoothstep -> 0 slope at both ends, no cliff).
        float fadeStart = threshold * 0.6f;
        float t = Mathf.InverseLerp(threshold, fadeStart, other); // 1 below fadeStart, 0 at threshold
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Density multiplier (0..1) for a biome-dominance rule (desert/snow): 0 at the required-biome weight
    /// threshold, smoothstep up to 1 as that biome takes firm hold — so the new area feathers in at its
    /// border instead of a hard edge, mirroring the grass exclusion feather on the other side of the
    /// boundary. 1 for rules without requireBiomeDominance.
    /// </summary>
    static float BiomeDominanceFactor(Planet planet, FoliageColourRule rule, Vector3 pos)
    {
        if (!rule.requireBiomeDominance || planet == null)
            return 1f;
        float threshold = Mathf.Clamp01(rule.minRequiredBiomeWeight);
        float w = Mathf.Clamp01(planet.GetBiomeWeightAtPosition(pos, rule.requiredBiomeIndex));
        if (w <= threshold)
            return 0f;
        // Reach full density 40% of the way from the threshold up to a fully-owned biome (smoothstep).
        float fadeEnd = Mathf.Lerp(threshold, 1f, 0.4f);
        float t = Mathf.InverseLerp(threshold, fadeEnd, w); // 0 at threshold, 1 at fadeEnd
        return t * t * (3f - 2f * t);
    }

    static float KeepProb(FoliageColourRule rule, float m)
    {
        if (rule.useBiomeGradientRule)
        {
            // m is continuous greenness. Smoothstep-ramp it across the [Lo,Hi] window: 0 at/below the
            // band border, 1 in the solid-green core. Multiplying by 'feather' guarantees the keep
            // probability reaches EXACTLY zero at the border (no residual grass on yellow/brown), while
            // edgeDensity lifts the toe and densityFalloff shapes the ramp.
            float t = Mathf.InverseLerp(GradientGreenLo, GradientGreenHi, m);
            float feather = t * t * (3f - 2f * t); // smoothstep (0 slope at both ends -> no hard edge)
            return feather * Mathf.Lerp(rule.edgeDensity, 1f, Mathf.Pow(feather, rule.densityFalloff));
        }
        return Mathf.Lerp(rule.edgeDensity, 1f, Mathf.Pow(Mathf.Clamp01(m), rule.densityFalloff));
    }

    /// <summary>How close this point's biome-latitude is to the rule's home biome (biomeIndex), 0..1.
    /// 1 at the home latitude, fading to 0 once it is `latitudeWidth` (in biome-percent) away.</summary>
    static float LatitudeWeight(FoliageColourRule rule, float biomePercent, int numBiomes)
    {
        float home = numBiomes > 1 ? rule.biomeIndex / (float)(numBiomes - 1) : 0f;
        float width = Mathf.Max(0.05f, rule.latitudeWidth);
        return 1f - Mathf.Clamp01(Mathf.Abs(biomePercent - home) / width);
    }

    /// <summary>Multiplicative latitude density factor for keepProb. 1 when latitudeInfluence is 0
    /// (approved behaviour unchanged); blends toward <see cref="LatitudeWeight"/> as influence rises.</summary>
    static float LatitudeFactor(FoliageColourRule rule, float biomePercent, int numBiomes)
    {
        if (rule.latitudeInfluence <= 0f)
            return 1f;
        return Mathf.Lerp(1f, LatitudeWeight(rule, biomePercent, numBiomes), Mathf.Clamp01(rule.latitudeInfluence));
    }

    List<GameObject> ResolvePrefabs(FoliageColourRule rule)
    {
        // Each rule supplies its own prefabs/meshes. The palette built by Build16ColourFoliagePalette /
        // FoliageColourSetup already assigns grass/tree/rock prefabs to every placing rule (it reuses
        // FoliageColourSetup's grass references), so there is no longer a legacy profile fallback here.
        // A rule with no prefabs simply contributes no runtime (BuildRuntimes skips it gracefully).
        if (rule.prefabs != null && rule.prefabs.Count > 0)
            return rule.prefabs;
        return null;
    }

    IEnumerator Start()
    {
        _layer = gameObject.layer;

        _planet = Object.FindFirstObjectByType<Planet>();
        if (_planet == null)
        {
            Debug.LogWarning("[FoliageByColour] No Planet found; disabling.");
            enabled = false;
            yield break;
        }

        int waited = 0;
        while (!_planet.IsGenerated)
        {
            if (logResults && waited == 300)
                Debug.LogWarning("[FoliageByColour] Still waiting for Planet.IsGenerated after ~5s — scatter is blocked on planet generation.");
            waited++;
            yield return null;
        }
        yield return null;

        if (!BuildRuntimes())
        {
            Debug.LogWarning("[FoliageByColour] No usable rules (no prefabs / meshes available); disabling.");
            enabled = false;
            yield break;
        }

        // Capture the surface sampling params once (constant after the planet has generated).
        SetupSurfaceParams();

        if (streamingEnabled)
        {
            // PLAYER-CENTERED STREAMING: do NOT scatter the whole planet. Compute per-cell density targets,
            // mark ready, and let the streaming loop populate/release cells around the player as they move.
            ComputeTargets();
            _ready = true;
            _streamRayBudgetRamp = 1f;
            _hasSortPos = false;
            if (logResults)
                Debug.Log($"[FoliageByColour] Streaming ON for {_runtimes.Count} rule(s): loadRadius {EffectiveLoadRadius():F0}, " +
                          $"unloadRadius {EffectiveUnloadRadius():F0}, cell {EffectiveChunkSize():F0}. Foliage populates only around the player.");
            StartCoroutine(StreamingRoutine());
            yield break;
        }

        // LEGACY one-shot path: scatter the entire planet up front (used when streamingEnabled is off).
        if (logResults)
            Debug.Log($"[FoliageByColour] Planet generated — scattering {_runtimes.Count} rule(s).");

        yield return StartCoroutine(Scatter());

        foreach (var rt in _runtimes)
        {
            rt.gpu?.FinalizeBatches();
            FinalizeObjChunks(rt);
        }
        _ready = true;

        if (logResults)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[FoliageByColour] Ready.\n");
            foreach (var rt in _runtimes)
            {
                int drawn = rt.gpu != null ? rt.gpu.DrawnInstanceCount() : rt.placed;
                sb.Append($"  rule '{rt.rule.name}' ({rt.rule.render}): placed {rt.placed}/{rt.effectiveTarget} (authored {rt.rule.targetCount}), drawn {drawn}. " +
                          $"rejected -> slope {rt.rejSlope}, elev {rt.rejElev}, density {rt.rejDensity}, spacing {rt.rejSpacing}\n");
            }
            Debug.Log(sb.ToString());
        }
    }

    float EffectiveLoadRadius() => loadRadius > 0f ? loadRadius : DefaultLoadRadius;
    float EffectiveUnloadRadius() => EffectiveLoadRadius() + (unloadHysteresis > 0f ? unloadHysteresis : DefaultUnloadHysteresis);
    float EffectiveRestreamThreshold() => restreamMoveThreshold > 0f ? restreamMoveThreshold : DefaultRestreamMoveThreshold;

    // Captures the constant surface-sampling parameters used by both the legacy scatter and streaming.
    void SetupSurfaceParams()
    {
        _center = _planet.transform.position;
        _baseRadius = (_planet.shapeSettings != null) ? _planet.GetBaseRadiusWorld() : 400f;
        _maxRadius = (_planet.shapeSettings != null) ? _planet.GetMaxSurfaceRadiusWorld() : _baseRadius;
        if (_maxRadius < _baseRadius)
            _maxRadius = _baseRadius;

        // Waterline = ocean shell radius when present (sea level is often ABOVE the shape base sphere on
        // this project). Fall back to base radius if there is no ocean layer.
        float seaLevel = _baseRadius;
        var ocean = _planet.GetComponent<PlanetOceanLayer>();
        if (ocean == null)
            ocean = Object.FindFirstObjectByType<PlanetOceanLayer>();
        if (ocean != null)
            seaLevel = Mathf.Max(seaLevel, ocean.ResolveOceanRadiusWorld());
        _waterLineRadius = seaLevel + Mathf.Max(0f, dryClearanceAboveSea);

        _groundMask = LayerMask.GetMask("Default", "Ground");
        if (_groundMask == 0)
            _groundMask = ~0;
        _rayStartRadius = _maxRadius + rayHeightAboveSurface;
        _rayLength = _maxRadius + rayHeightAboveSurface * 3f;
        _numBiomes = BiomeCount();

        BuildBurstSnapshot();
    }

    // Builds (or rebuilds) the blittable noise/shape snapshot the Burst sampler reads. Safe to call again
    // if the planet/settings change — it disposes the previous native data first. Sets _burstReady=false
    // (falls back to the main-thread analytic path) if Burst is disabled or the planet isn't usable yet.
    void BuildBurstSnapshot()
    {
        DisposeNative();
        _burstReady = false;

        if (disableBurstJobs)
            return;
        if (_planet == null || _planet.shapeSettings == null || _planet.shapeSettings.noiseLayers == null)
            return;
        if (!_planet.TryGetLocalElevationMinMax(out _elevMinLocal, out _elevMaxLocal) || _elevMaxLocal <= _elevMinLocal)
            return;

        var layers = _planet.shapeSettings.noiseLayers;
        _noiseLayers = new NativeArray<NoiseLayerData>(layers.Length, Allocator.Persistent);
        for (int i = 0; i < layers.Length; i++)
        {
            var layer = layers[i];
            var ns = layer != null ? layer.noiseSettings : null;
            var data = new NoiseLayerData
            {
                enabled = (layer != null && layer.enabled) ? 1 : 0,
                useFirstLayerAsMask = (layer != null && layer.useFirstLayerAsMask) ? 1 : 0,
                filterType = 0,
                numLayers = 1,
                strength = 1f,
                baseRoughness = 1f,
                roughness = 2f,
                persistence = 0.5f,
                centre = float3.zero,
                minValue = 0f,
                weightMultiplier = 0.8f,
            };
            if (ns != null)
            {
                if (ns.filterType == NoiseSettings.FilterType.Ridgid && ns.ridgidNoiseSettings != null)
                {
                    var s = ns.ridgidNoiseSettings;
                    data.filterType = 1;
                    data.numLayers = s.numLayers;
                    data.strength = s.strength;
                    data.baseRoughness = s.baseRoughness;
                    data.roughness = s.roughness;
                    data.persistence = s.persistence;
                    data.centre = s.centre;
                    data.minValue = s.minValue;
                    data.weightMultiplier = s.weightMultiplier;
                }
                else if (ns.simpleNoiseSettings != null)
                {
                    var s = ns.simpleNoiseSettings;
                    data.filterType = 0;
                    data.numLayers = s.numLayers;
                    data.strength = s.strength;
                    data.baseRoughness = s.baseRoughness;
                    data.roughness = s.roughness;
                    data.persistence = s.persistence;
                    data.centre = s.centre;
                    data.minValue = s.minValue;
                }
            }
            _noiseLayers[i] = data;
        }

        _perm = FoliageNoise.BuildPermutation(Allocator.Persistent);
        _planetRadiusLocal = _planet.shapeSettings.planetRadius;
        Vector3 lossy = _planet.transform.lossyScale;
        _scaleFactor = Mathf.Max(lossy.x, Mathf.Max(lossy.y, lossy.z));
        if (_scaleFactor < 1e-6f)
            _scaleFactor = 1f;

        // Mirror PlanetBuildingPads into Burst so jobbed elevation matches mesh + managed analytic.
        BuildingPadSample[] padSamples = PlanetBuildingPads.Samples;
        _pads = new NativeArray<BuildingPadBurstData>(padSamples.Length, Allocator.Persistent);
        for (int i = 0; i < padSamples.Length; i++)
        {
            BuildingPadSample s = padSamples[i];
            _pads[i] = new BuildingPadBurstData
            {
                axis = s.Axis,
                rPad = s.RPad,
                cosInner = s.CosInner,
                cosOuter = s.CosOuter
            };
        }

        _burstReady = true;

        if (logResults)
            Debug.Log($"[FoliageByColour] Burst sampler ready: {_noiseLayers.Length} noise layer(s), " +
                      $"pads {_pads.Length}, planetRadius {_planetRadiusLocal:F1}, scale {_scaleFactor:F2}, " +
                      $"elev [{_elevMinLocal:F1}..{_elevMaxLocal:F1}].");
    }

    // Ensures the reused candidate buffers can hold 'count' entries (grows by realloc; never shrinks).
    void EnsureJobCapacity(int count)
    {
        if (_dirArray.IsCreated && _resArray.IsCreated && _jobCapacity >= count)
            return;
        // Never dispose while a sampling job (or a coroutine still reading results) owns the buffers.
        _pendingHandle.Complete();
        _pendingHandle = default;
        if (_dirArray.IsCreated) _dirArray.Dispose();
        if (_resArray.IsCreated) _resArray.Dispose();
        // Grow with headroom so a slightly denser neighbour doesn't realloc every cell.
        int cap = Mathf.Max(count, Mathf.CeilToInt(_jobCapacity * 1.5f));
        cap = Mathf.Clamp(cap, 1024, MaxCellCandidates);
        cap = Mathf.Max(cap, count);
        _dirArray = new NativeArray<float3>(cap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        _resArray = new NativeArray<FoliageCandidate>(cap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        _jobCapacity = cap;
    }

    // Releases every native buffer. Idempotent (guards each .IsCreated), so it's safe from both
    // BuildBurstSnapshot (rebuild) and OnDestroy (teardown).
    void DisposeNative()
    {
        // Make sure no sampling job is still reading the buffers before we free them (e.g. a coroutine
        // suspended between Schedule and Complete). Completing a default/finished handle is a no-op.
        // ScatterCellJobbed snapshots accepted hits to managed memory before yielding, so freeing
        // _resArray here cannot throw ObjectDisposedException in the consumer loop.
        _pendingHandle.Complete();
        _pendingHandle = default;
        if (_perm.IsCreated) _perm.Dispose();
        if (_noiseLayers.IsCreated) _noiseLayers.Dispose();
        if (_pads.IsCreated) _pads.Dispose();
        if (_dirArray.IsCreated) _dirArray.Dispose();
        if (_resArray.IsCreated) _resArray.Dispose();
        _jobCapacity = 0;
        _burstReady = false;
    }

    // Computes per-rule whole-planet effectiveTarget (area-scaled like the legacy scatter) AND the expected
    // per-CELL fractional count so a streamed region looks exactly as dense as the one-shot scatter. The
    // fractional per-cell count is rounded stochastically per cell so even very sparse rules (e.g. a handful
    // of palms over the whole planet) still appear at the correct average density instead of rounding to 0.
    void ComputeTargets()
    {
        float worldRadius = _baseRadius;
        if (densityReferenceRadius <= 0f)
            densityReferenceRadius = (_planet.shapeSettings != null) ? _planet.shapeSettings.planetRadius : worldRadius;
        float radiusRatio = worldRadius / Mathf.Max(1e-3f, densityReferenceRadius);
        float areaMul = dontScaleCountWithArea ? 1f : radiusRatio * radiusRatio;
        int maxPerRule = maxInstancesPerRule > 0 ? maxInstancesPerRule : DefaultMaxInstancesPerRule;

        // Fraction of the whole planet's surface area that one cubic cell's surface patch covers (~cs^2).
        float cs = EffectiveChunkSize();
        float surfaceArea = 4f * Mathf.PI * Mathf.Max(1f, worldRadius * worldRadius);
        float cellFraction = Mathf.Clamp01((cs * cs) / surfaceArea);

        foreach (var rt in _runtimes)
        {
            if (rt.rule.targetCount <= 0)
            {
                rt.effectiveTarget = 0;
                rt.cellTargetF = 0f;
                continue;
            }
            long scaled = (long)System.Math.Round(rt.rule.targetCount * (double)areaMul);
            scaled = System.Math.Max(1L, System.Math.Min(scaled, (long)maxPerRule));
            rt.effectiveTarget = (int)scaled;
            rt.cellTargetF = rt.effectiveTarget * cellFraction;
        }

        if (logResults && !dontScaleCountWithArea)
            Debug.Log($"[FoliageByColour] Area-density: worldRadius {worldRadius:F0}, reference {densityReferenceRadius:F0}, " +
                      $"multiplier x{areaMul:F2} (cap {maxPerRule}/rule). Per-cell density derived from a {cs:F0}-unit cell.");
    }

    bool BuildRuntimes()
    {
        _runtimes.Clear();
        _zoneColours = new List<Color>();
        _zoneRuntimes = new List<List<RuleRuntime>>();
        _carpetRuntimes = new List<RuleRuntime>();
        var rules = GetActiveRules();
        if (rules == null || rules.Count == 0)
            return false;

        foreach (var rule in rules)
        {
            if (rule == null)
                continue;

            // Gradient carpet rules are NOT colour zones — they blanket by greenness and must not steal
            // winner-take-all classification from trees/rocks/beach/water swatches.
            bool isCarpet = rule.useBiomeGradientRule;

            // Zone bookkeeping for NearestPaletteColour mode: EVERY colour-swatch rule defines or joins a
            // colour zone (grouped by exact targetColour), even disabled / 'place nothing' ones, so the
            // surface still partitions into the full palette and a winning empty zone correctly places
            // nothing. Carpet rules are excluded so their targetColour never becomes a zone winner.
            int zone = -1;
            if (!isCarpet)
            {
                zone = FindZoneIndex(rule.targetColour);
                if (zone < 0)
                {
                    zone = _zoneColours.Count;
                    _zoneColours.Add(rule.targetColour);
                    _zoneRuntimes.Add(new List<RuleRuntime>());
                }
            }

            // A rule contributes NO runtime (but still owns/joins its zone) when disabled or flagged empty.
            if (!rule.enabled || rule.placeNothing)
                continue;

            var prefabs = ResolvePrefabs(rule);
            var rt = new RuleRuntime
            {
                rule = rule,
                prefabs = prefabs,
                invCell = 1f / Mathf.Max(0.05f, rule.minSpacing),
            };

            if (rule.render == FoliageRenderMode.GpuInstanced)
            {
                var gpu = new GpuBatchSet
                {
                    shadowCasting = rule.shadowCasting,
                    receiveShadows = rule.receiveShadows,
                    chunkSize = EffectiveChunkSize(),
                };
                if (!gpu.BuildModels(prefabs, rule.forceDoubleSided))
                {
                    if (logResults)
                        Debug.LogWarning($"[FoliageByColour] Rule '{rule.name}' (GpuInstanced) has no usable meshes — skipped.");
                    continue;
                }
                rt.gpu = gpu;
            }
            else // GameObjectPool
            {
                if (prefabs == null || prefabs.Count == 0)
                {
                    if (logResults)
                        Debug.LogWarning($"[FoliageByColour] Rule '{rule.name}' (GameObjectPool) has no prefabs — skipped.");
                    continue;
                }
                var container = new GameObject($"Foliage_{rule.name}");
                container.transform.SetParent(transform, false);
                // Keep pooled instances at their AUTHORED world size regardless of the planet's Transform
                // scale: cancel the parent's lossy scale on the container so each child's localScale equals
                // its world scale. Positions are world-space (from the raycast), so foliage still seats on
                // the scaled surface — only the per-asset SIZE is decoupled. No-op when the planet isn't
                // transform-scaled (lossyScale == 1). Assumes uniform planet scale (the project's case).
                if (!inheritPlanetScale)
                {
                    Vector3 parentLossy = container.transform.parent != null
                        ? container.transform.parent.lossyScale : Vector3.one;
                    container.transform.localScale = InverseScale(parentLossy);
                }
                rt.poolContainer = container.transform;
            }

            rt.runtimeIndex = _runtimes.Count;
            _runtimes.Add(rt);
            if (isCarpet)
                _carpetRuntimes.Add(rt);
            else if (zone >= 0)
                _zoneRuntimes[zone].Add(rt);
        }

        return _runtimes.Count > 0;
    }

    /// <summary>Index of an existing zone whose colour exactly matches <paramref name="c"/> (RGB), else -1.
    /// Exact match is intentional: the palette builder sets each zone's rules to the SAME Color value, so
    /// grass + trees of one green zone group together, while distinct palette colours stay separate.</summary>
    int FindZoneIndex(Color c)
    {
        if (_zoneColours == null)
            return -1;
        for (int i = 0; i < _zoneColours.Count; i++)
        {
            Color z = _zoneColours[i];
            if (z.r == c.r && z.g == c.g && z.b == c.b)
                return i;
        }
        return -1;
    }

    /// <summary>Runtimes that place for the zone nearest this point's live surface colour (NearestPaletteColour
    /// mode). Null/empty = the winning zone is a 'place nothing' zone (e.g. water) — the point stays bare and
    /// is NOT reassigned to another zone.</summary>
    List<RuleRuntime> ChooseZoneRuntimes(Vector3 pos)
    {
        int zone = ClassifyNearestZone(_planet.GetSurfaceColorAtPosition(pos));
        return zone >= 0 ? _zoneRuntimes[zone] : null;
    }

    /// <summary>Winner-take-all classification for NearestPaletteColour mode: the zone whose colour is
    /// nearest to <paramref name="surface"/> by squared RGB distance. -1 if no zones exist.</summary>
    int ClassifyNearestZone(Color surface)
    {
        if (_zoneColours == null || _zoneColours.Count == 0)
            return -1;
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < _zoneColours.Count; i++)
        {
            Color z = _zoneColours[i];
            float dr = surface.r - z.r, dg = surface.g - z.g, db = surface.b - z.b;
            float d = dr * dr + dg * dg + db * db;
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// Grove clustering keep-probability for colour-zone rules (trees). 1 when clustering is off.
    /// With strength &gt; 0, density rises in Perlin "grove cores" so existing forest patches thicken
    /// without spraying trees into new terrain colours.
    /// </summary>
    static float ClusterKeepProb(FoliageColourRule rule, Vector3 pos)
    {
        if (rule == null || rule.clusterStrength <= 0f)
            return 1f;
        float s = Mathf.Max(0.005f, rule.clusterScale);
        float n = Mathf.PerlinNoise(pos.x * s + 17.13f, pos.z * s + 9.31f);
        float n2 = Mathf.PerlinNoise(pos.y * s + 3.71f, pos.x * s + 11.27f);
        float grove = Mathf.SmoothStep(0.40f, 0.72f, (n + n2) * 0.5f); // 0 = gap, 1 = core
        float baseFill = 1f - rule.clusterStrength * 0.55f;             // still some trees between groves
        return Mathf.Lerp(baseFill, 1f, grove);
    }

    /// <summary>
    /// Places ONE instance of <paramref name="rt"/> at a resolved surface point, applying the rule's density
    /// (keepProb), per-rule spacing grid (<paramref name="occupied"/>), orientation, scale and surface offset.
    /// Shared by the NearestPaletteColour scatter paths (legacy + streaming). Returns what was placed so the
    /// caller can charge its per-frame instantiate budget. Slope / elevation / target gating is done by the
    /// caller before this is invoked.
    /// </summary>
    PlaceResult TryPlace(RuleRuntime rt, Vector3 pos, Vector3 hitNormal, Vector3 radial,
                         HashSet<Vector3Int> occupied, float keepProb)
    {
        if (PlanetBuildingPads.ShouldSuppressFoliage(radial))
            return PlaceResult.Skipped;

        if (keepProb < 1f && Random.value > keepProb)
        {
            rt.rejDensity++;
            return PlaceResult.Skipped;
        }

        var cell = new Vector3Int(
            Mathf.FloorToInt(pos.x * rt.invCell),
            Mathf.FloorToInt(pos.y * rt.invCell),
            Mathf.FloorToInt(pos.z * rt.invCell));
        if (!occupied.Add(cell))
        {
            rt.rejSpacing++;
            return PlaceResult.Skipped;
        }

        Vector3 up = rt.rule.orient == FoliageOrientMode.Upright ? radial : hitNormal;
        Quaternion rot = Quaternion.FromToRotation(Vector3.up, up)
                         * Quaternion.AngleAxis(Random.value * 360f, Vector3.up);
        float scale = Random.Range(rt.rule.scaleRange.x, rt.rule.scaleRange.y);
        Vector3 placePos = pos + hitNormal * rt.rule.surfaceOffset;

        if (rt.gpu != null)
        {
            Matrix4x4 baseTRS = Matrix4x4.TRS(placePos, rot, Vector3.one * scale);
            rt.gpu.AddInstance(baseTRS);
            rt.placed++;
            return PlaceResult.PlacedGpu;
        }

        if (rt.poolContainer != null && rt.prefabs != null && rt.prefabs.Count > 0)
        {
            var prefab = rt.prefabs[Random.Range(0, rt.prefabs.Count)];
            if (prefab != null)
            {
                var go = Object.Instantiate(prefab, placePos, rot, rt.poolContainer);
                go.transform.localScale = prefab.transform.localScale * scale;
                Stargrave.CameraOcclusion.FoliageOccluder.EnsureOn(go);
                AddPooledToChunk(rt, go, placePos);
                BeginPooledPhaseIn(go.transform);
                rt.placed++;
                return PlaceResult.PlacedPooled;
            }
        }

        return PlaceResult.Skipped;
    }

    /// <summary>
    /// Places gradient-carpet rules (typically Meadow Grass) at a surface point using continuous greenness
    /// membership + density feathering. Independent of colour-zone winner-take-all so grass blankets green
    /// land and blends out toward beach / desert / snow instead of forming discrete palette patches.
    /// Returns true if a pooled instantiate happened (caller charges instantiate budget).
    /// When <paramref name="cellPlaced"/>/<paramref name="cellOccupied"/> are non-null, placement is
    /// counted against the streaming cell budgets; otherwise global scatter targets are used.
    /// </summary>
    bool TryPlaceCarpetAt(Vector3 pos, Vector3 hitNormal, Vector3 radial, float slope, float elevationNorm,
                          int[] cellPlaced, int[] cellTarget, HashSet<Vector3Int>[] cellOccupied)
    {
        if (_carpetRuntimes == null || _carpetRuntimes.Count == 0)
            return false;

        bool placedPooled = false;
        for (int c = 0; c < _carpetRuntimes.Count; c++)
        {
            var rt = _carpetRuntimes[c];
            int idx = rt.runtimeIndex;
            if (cellPlaced != null)
            {
                if (cellPlaced[idx] >= cellTarget[idx])
                    continue;
            }
            else if (rt.placed >= rt.effectiveTarget)
                continue;

            if (slope > rt.rule.maxSlope)
            { rt.rejSlope++; continue; }
            if (elevationNorm < rt.rule.elevationRange.x || elevationNorm > rt.rule.elevationRange.y)
            { rt.rejElev++; continue; }

            float m = RuleMatchStrength(_planet, rt.rule, pos, default, 0, 0);
            if (m < 0f)
                continue;

            float keepProb = KeepProb(rt.rule, m);
            keepProb *= BiomeExclusionFactor(_planet, rt.rule, pos);
            keepProb *= ClusterKeepProb(rt.rule, pos);
            if (rt.rule.latitudeInfluence > 0f)
            {
                float biomePercent = _planet.GetBiomePercentAtPosition(pos);
                keepProb *= LatitudeFactor(rt.rule, biomePercent, _numBiomes > 0 ? _numBiomes : BiomeCount());
            }

            var occupied = cellOccupied != null ? cellOccupied[idx] : rt.occupied;
            var res = TryPlace(rt, pos, hitNormal, radial, occupied, keepProb);
            if (res != PlaceResult.Skipped && cellPlaced != null)
                cellPlaced[idx]++;
            if (res == PlaceResult.PlacedPooled)
                placedPooled = true;
        }
        return placedPooled;
    }

    /// <summary>Number of biomes from the planet's colour settings (public fields — no Planet.cs change). Min 1.</summary>
    int BiomeCount()
    {
        var biomes = _planet != null && _planet.colourSettings != null && _planet.colourSettings.biomeColourSettings != null
            ? _planet.colourSettings.biomeColourSettings.biomes : null;
        return (biomes != null && biomes.Length > 0) ? biomes.Length : 1;
    }

    bool AllRulesFull()
    {
        foreach (var rt in _runtimes)
            if (rt.placed < rt.effectiveTarget)
                return false;
        return true;
    }

    IEnumerator Scatter()
    {
        Vector3 center = _planet.transform.position;
        float baseRadius = (_planet.shapeSettings != null) ? _planet.GetBaseRadiusWorld() : 400f;
        float maxRadius = (_planet.shapeSettings != null) ? _planet.GetMaxSurfaceRadiusWorld() : baseRadius;
        if (maxRadius < baseRadius)
            maxRadius = baseRadius;

        var groundMask = LayerMask.GetMask("Default", "Ground");
        if (groundMask == 0)
            groundMask = ~0;

        float rayStartRadius = maxRadius + rayHeightAboveSurface;
        float rayLength = maxRadius + rayHeightAboveSurface * 3f;

        // Area-proportional density: keep closeness (minSpacing) constant by scattering MORE instances on a
        // larger planet and fewer on a smaller one. worldRadius reflects BOTH scale paths — the Transform
        // lossyScale and the planetRadius regen — via GetBaseRadiusWorld (= planetRadius * maxLossyScale).
        // The reference is the planet's UNSCALED authored radius (auto-captured once when <= 0): the authored
        // targetCount is the baseline density at scale 1, and a bigger planet densifies by surface area (r^2).
        float worldRadius = baseRadius;
        if (densityReferenceRadius <= 0f)
            densityReferenceRadius = (_planet.shapeSettings != null) ? _planet.shapeSettings.planetRadius : worldRadius;
        float radiusRatio = worldRadius / Mathf.Max(1e-3f, densityReferenceRadius);
        float areaMul = dontScaleCountWithArea ? 1f : radiusRatio * radiusRatio;
        int maxPerRule = maxInstancesPerRule > 0 ? maxInstancesPerRule : DefaultMaxInstancesPerRule;
        foreach (var rt in _runtimes)
        {
            if (rt.rule.targetCount <= 0)
            {
                rt.effectiveTarget = 0;
                continue;
            }
            long scaled = (long)System.Math.Round(rt.rule.targetCount * (double)areaMul);
            scaled = System.Math.Max(1L, System.Math.Min(scaled, (long)maxPerRule));
            rt.effectiveTarget = (int)scaled;
        }
        if (logResults && !dontScaleCountWithArea)
            Debug.Log($"[FoliageByColour] Area-density: worldRadius {worldRadius:F0}, reference {densityReferenceRadius:F0}, " +
                      $"multiplier x{areaMul:F2} (cap {maxPerRule}/rule). Authored counts scaled by surface area.");

        long totalTarget = 0;
        foreach (var rt in _runtimes)
            totalTarget += rt.effectiveTarget;
        long maxAttempts = totalTarget * attemptBudgetMultiplier + 8000;

        int numBiomes = BiomeCount();

        int budget = 0;
        int instThisFrame = 0;
        int rejNoHit = 0, rejUnderBase = 0, rejNoRule = 0;

        for (long attempt = 0; attempt < maxAttempts && !AllRulesFull(); attempt++)
        {
            if (++budget >= scatterPerFrame)
            {
                budget = 0;
                instThisFrame = 0;
                yield return null;
            }

            Vector3 dir = Random.onUnitSphere;
            Vector3 rayStart = center + dir * rayStartRadius;
            if (!Physics.Raycast(rayStart, -dir, out var hit, rayLength, groundMask))
            { rejNoHit++; continue; }

            Vector3 pos = hit.point;
            Vector3 radial = (pos - center).normalized;
            float dist = (pos - center).magnitude;

            if (dist < _waterLineRadius)
            { rejUnderBase++; continue; }

            float slope = Vector3.Angle(hit.normal, radial);
            float elevationNorm = _planet.GetNormalizedElevationAtPosition(pos);

            // NEAREST-OF-PALETTE: classify the point to its nearest palette colour zone (winner-take-all) and
            // place every runtime of that zone (e.g. trees/rocks). Carpet grass (biome-gradient) places
            // independently afterward so green land gets continuous coverage that feathers out at borders.
            if (placementMode == FoliagePlacementMode.NearestPaletteColour)
            {
                var zoneRts = ChooseZoneRuntimes(pos);
                if (zoneRts != null && zoneRts.Count > 0)
                {
                    for (int z = 0; z < zoneRts.Count; z++)
                    {
                        var rt = zoneRts[z];
                        if (rt.placed >= rt.effectiveTarget)
                            continue;
                        if (slope > rt.rule.maxSlope)
                        { rt.rejSlope++; continue; }
                        if (elevationNorm < rt.rule.elevationRange.x || elevationNorm > rt.rule.elevationRange.y)
                        { rt.rejElev++; continue; }
                        if (TryPlace(rt, pos, hit.normal, radial, rt.occupied, ClusterKeepProb(rt.rule, pos)) == PlaceResult.PlacedPooled)
                        {
                            if (++instThisFrame >= maxInstantiatesPerFrame)
                            { instThisFrame = 0; budget = 0; yield return null; }
                        }
                    }
                }
                if (TryPlaceCarpetAt(pos, hit.normal, radial, slope, elevationNorm, null, null, null))
                {
                    if (++instThisFrame >= maxInstantiatesPerFrame)
                    { instThisFrame = 0; budget = 0; yield return null; }
                }
                continue;
            }

            Color keyColour = _planet.GetSurfaceKeyColorAtPosition(pos);
            _planet.ClassifySurfaceAtPosition(pos, out int clsBiome, out int clsKey, out _);

            // Assign to the rule with the strongest membership (best match wins so bands don't fight).
            // Colour rules use colour-distance membership; gradient (grass-parity) rules use biome
            // greenness — compared uniformly here. bestM starts at -1 so a qualified gradient rule with
            // greenness exactly 0 (green-band edge) is still a candidate.
            RuleRuntime best = null;
            float bestM = -1f;
            foreach (var rt in _runtimes)
            {
                if (rt.placed >= rt.effectiveTarget)
                    continue;
                if (slope > rt.rule.maxSlope)
                { rt.rejSlope++; continue; }
                if (elevationNorm < rt.rule.elevationRange.x || elevationNorm > rt.rule.elevationRange.y)
                { rt.rejElev++; continue; }

                float m = RuleMatchStrength(_planet, rt.rule, pos, keyColour, clsBiome, clsKey);
                if (m >= 0f && m > bestM)
                {
                    bestM = m;
                    best = rt;
                }
            }

            if (best == null)
            { rejNoRule++; continue; }

            // Density falloff: thin by colour-match/greenness strength (dense in the best match, sparse
            // at edges), then optionally bias by latitude proximity to the rule's home biome.
            float keepProb = KeepProb(best.rule, bestM);
            // Feather grass out where biome 1/2 colour the ground (gradient rules only; 1f otherwise).
            keepProb *= BiomeExclusionFactor(_planet, best.rule, pos);
            // Feather biome-dominance areas (desert/snow) in at their borders (1f for other rules).
            keepProb *= BiomeDominanceFactor(_planet, best.rule, pos);
            if (best.rule.latitudeInfluence > 0f)
            {
                float biomePercent = _planet.GetBiomePercentAtPosition(pos);
                keepProb *= LatitudeFactor(best.rule, biomePercent, numBiomes);
            }
            if (Random.value > keepProb)
            { best.rejDensity++; continue; }

            // Per-rule spacing grid prevents stacking within a rule.
            var cell = new Vector3Int(
                Mathf.FloorToInt(pos.x * best.invCell),
                Mathf.FloorToInt(pos.y * best.invCell),
                Mathf.FloorToInt(pos.z * best.invCell));
            if (!best.occupied.Add(cell))
            { best.rejSpacing++; continue; }

            Vector3 up = best.rule.orient == FoliageOrientMode.Upright ? radial : hit.normal;
            Quaternion rot = Quaternion.FromToRotation(Vector3.up, up)
                             * Quaternion.AngleAxis(Random.value * 360f, Vector3.up);
            float scale = Random.Range(best.rule.scaleRange.x, best.rule.scaleRange.y);
            Vector3 placePos = pos + hit.normal * best.rule.surfaceOffset;

            if (best.gpu != null)
            {
                Matrix4x4 baseTRS = Matrix4x4.TRS(placePos, rot, Vector3.one * scale);
                best.gpu.AddInstance(baseTRS);
            }
            else if (best.poolContainer != null && best.prefabs != null && best.prefabs.Count > 0)
            {
                var prefab = best.prefabs[Random.Range(0, best.prefabs.Count)];
                if (prefab != null)
                {
                    var go = Object.Instantiate(prefab, placePos, rot, best.poolContainer);
                    go.transform.localScale = prefab.transform.localScale * scale;
                    Stargrave.CameraOcclusion.FoliageOccluder.EnsureOn(go);
                    AddPooledToChunk(best, go, placePos);
                    BeginPooledPhaseIn(go.transform);
                    if (++instThisFrame >= maxInstantiatesPerFrame)
                    {
                        instThisFrame = 0;
                        budget = 0;
                        yield return null;
                    }
                }
            }

            best.placed++;
        }

        if (logResults)
        {
            Debug.Log($"[FoliageByColour] Scatter done. Global rejects -> noHit {rejNoHit}, underBase {rejUnderBase}, " +
                      $"noRuleMatch {rejNoRule}. Rules: {_runtimes.Count}, maxAttempts {maxAttempts}.");
        }
    }

    void Update()
    {
        if (!_ready)
            return;

        // Advance any pooled trees/rocks that are currently growing in (independent of culling).
        UpdatePooledPhaseIn();

        bool cull = !disableCulling;
        // Single rule = camera frustum + small border. Distance is made effectively unlimited so it never
        // removes foliage that is still inside the view (the serialized grass/objectDrawDistance fields are
        // deliberately bypassed for this reason).
        float gdd = EffectivelyUnlimitedDrawDistance;
        float odd = EffectivelyUnlimitedDrawDistance;

        Camera cam = null;
        Vector3 camPos = Vector3.zero;
        Plane[] planes = null;
        if (cull)
        {
            cam = ResolveCamera();
            if (cam == null)
            {
                // No camera resolvable this frame: fail safe to drawing everything (no culling).
                cull = false;
            }
            else
            {
                camPos = cam.transform.position;
                GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes); // once per frame, reused by all chunks
                planes = _frustumPlanes;
            }
        }

        // Shared nearest-first grass budget for this frame. Unlimited when culling is off (draw everything);
        // otherwise <=0 falls back to the built-in default. Decremented as each grass set draws near->far.
        int grassBudget = cull
            ? (maxVisibleGrassInstancesPerFrame > 0 ? maxVisibleGrassInstancesPerFrame
                                                    : DefaultMaxVisibleGrassInstancesPerFrame)
            : int.MaxValue;

        // Phase-in parameters resolved once per frame (cheap), passed by value into the grass draw.
        bool phaseEnabled = phaseInEnabled;
        float phaseDur = EffectivePhaseInDuration();
        float phaseStartScale = Mathf.Clamp(phaseInStartScale, 0f, 0.95f);
        float now = Time.time;

        for (int i = 0; i < _runtimes.Count; i++)
        {
            var rt = _runtimes[i];
            if (rt.gpu != null)
            {
                rt.gpu.Draw(_layer, cull, camPos, planes, gdd, SmallFrustumMargin, cam, ref grassBudget,
                            phaseEnabled, phaseDur, phaseStartScale, now);
            }
            else if (rt.objChunks != null)
            {
                CullPooled(rt, cull, camPos, planes, odd);
            }
        }
    }

    float EffectiveChunkSize() => chunkSize > 0f ? chunkSize : DefaultChunkSize;

    float EffectivePhaseInDuration() => phaseInDuration > 0f ? phaseInDuration : DefaultPhaseInDuration;

    // ---- Pooled (GameObject) phase-in: a centralized list of objects currently growing in. ----
    // Entries are added ONLY at genuine first instantiate (TryPlace / streaming scatter), never on a cull/
    // uncull SetActive toggle, so an object never re-grows just because it scrolled back into view. Each entry
    // is advanced every frame until it reaches full scale, then removed (swap-back) so settled trees/rocks cost
    // nothing. No per-frame allocation.
    struct PooledPhase
    {
        public Transform t;
        public Vector3 finalScale; // authored localScale to land on exactly (respects constant-world-size container)
        public float startTime;
    }
    readonly List<PooledPhase> _pooledPhasing = new List<PooledPhase>();

    // Begins a base-anchored grow for a freshly-instantiated pooled object. Captures its current (authored)
    // localScale as the target, then shrinks it to the start fraction. Scaling localScale grows the object from
    // its transform pivot — for trees/rocks/palms whose pivot is at the base, that's the ground contact, so it
    // rises out of the surface. Called immediately after the object's final localScale has been assigned.
    void BeginPooledPhaseIn(Transform t)
    {
        if (!phaseInEnabled || t == null)
            return;
        Vector3 finalScale = t.localScale;
        float s0 = Mathf.Clamp(phaseInStartScale, 0f, 0.95f);
        t.localScale = finalScale * s0;
        _pooledPhasing.Add(new PooledPhase { t = t, finalScale = finalScale, startTime = Time.time });
    }

    // Advances every in-progress pooled grow by one frame; removes finished/destroyed entries. O(currently
    // phasing objects) per frame — typically a handful streaming in near the player, zero when nothing is
    // spawning. Settled objects are not in the list so they cost nothing.
    void UpdatePooledPhaseIn()
    {
        int n = _pooledPhasing.Count;
        if (n == 0)
            return;
        float now = Time.time;
        float dur = EffectivePhaseInDuration();
        float s0 = Mathf.Clamp(phaseInStartScale, 0f, 0.95f);
        for (int i = n - 1; i >= 0; i--)
        {
            var p = _pooledPhasing[i];
            if (p.t == null) // object unloaded/destroyed mid-grow
            {
                _pooledPhasing[i] = _pooledPhasing[_pooledPhasing.Count - 1];
                _pooledPhasing.RemoveAt(_pooledPhasing.Count - 1);
                continue;
            }
            float age = now - p.startTime;
            if (age >= dur)
            {
                p.t.localScale = p.finalScale; // land EXACTLY on the authored scale (no drift vs the size system)
                Stargrave.CameraOcclusion.FoliageOccluder.EnsureOn(p.t.gameObject);
                _pooledPhasing[i] = _pooledPhasing[_pooledPhasing.Count - 1];
                _pooledPhasing.RemoveAt(_pooledPhasing.Count - 1);
                continue;
            }
            float t01 = dur > 0f ? age / dur : 1f;
            float g = t01 * t01 * (3f - 2f * t01); // smoothstep ease
            p.t.localScale = p.finalScale * Mathf.Lerp(s0, 1f, g);
        }
    }

    Camera ResolveCamera()
    {
        if (cullingCamera != null)
            return cullingCamera;
        if (_cam != null && _cam.isActiveAndEnabled && _cam.targetTexture == null)
            return _cam;
        _cam = Camera.main;
        if (_cam == null)
            _cam = PickBestOnScreenCamera();
        return _cam;
    }

    // Fallback when Camera.main is null (player camera not tagged MainCamera): the highest-depth camera that
    // renders to the screen (the one drawn on top / last), rather than an arbitrary first-found camera.
    static Camera PickBestOnScreenCamera()
    {
        var cams = Camera.allCameras; // enabled cameras only
        Camera best = null;
        float bestDepth = float.NegativeInfinity;
        for (int i = 0; i < cams.Length; i++)
        {
            var c = cams[i];
            if (c == null || c.targetTexture != null)
                continue;
            if (c.depth >= bestDepth) { bestDepth = c.depth; best = c; }
        }
        return best;
    }

    // Buckets a freshly-instantiated pooled object into its spatial cell for whole-chunk distance culling.
    void AddPooledToChunk(RuleRuntime rt, GameObject go, Vector3 pos)
    {
        if (rt.objChunkMap == null)
            rt.objChunkMap = new Dictionary<Vector3Int, ObjChunk>();

        float inv = 1f / Mathf.Max(0.0001f, EffectiveChunkSize());
        var cell = new Vector3Int(
            Mathf.FloorToInt(pos.x * inv),
            Mathf.FloorToInt(pos.y * inv),
            Mathf.FloorToInt(pos.z * inv));

        if (!rt.objChunkMap.TryGetValue(cell, out var chunk))
        {
            chunk = new ObjChunk { cell = cell };
            rt.objChunkMap[cell] = chunk;
        }
        chunk.objects.Add(go);
        if (!chunk.hasBounds) { chunk.min = chunk.max = pos; chunk.hasBounds = true; }
        else { chunk.min = Vector3.Min(chunk.min, pos); chunk.max = Vector3.Max(chunk.max, pos); }
    }

    // Streaming: finalize bounds for a single pooled cell as it loads and add it to the live cull list.
    void FinalizeObjChunk(RuleRuntime rt, Vector3Int cell)
    {
        if (rt.objChunkMap == null || !rt.objChunkMap.TryGetValue(cell, out var chunk))
            return;
        if (rt.objChunks == null)
            rt.objChunks = new List<ObjChunk>();
        if (rt.objChunks.Contains(chunk))
            return; // already finalized

        float pad = Mathf.Max(2f, EffectiveChunkSize() * 0.5f);
        Vector3 padV = new Vector3(pad, pad, pad);
        Vector3 bmin = chunk.min - padV;
        Vector3 bmax = chunk.max + padV;
        chunk.center = (bmin + bmax) * 0.5f;
        chunk.radius = ((bmax - bmin) * 0.5f).magnitude;
        chunk.bounds = new Bounds(chunk.center, bmax - bmin);
        rt.objChunks.Add(chunk);
    }

    // ----------------------------------------------------------------------------------------------------
    // Player-centered streaming
    // ----------------------------------------------------------------------------------------------------

    Vector3Int WorldToCell(Vector3 pos)
    {
        float inv = 1f / Mathf.Max(0.0001f, EffectiveChunkSize());
        return new Vector3Int(
            Mathf.FloorToInt(pos.x * inv),
            Mathf.FloorToInt(pos.y * inv),
            Mathf.FloorToInt(pos.z * inv));
    }

    Vector3 CellCenter(Vector3Int cell)
    {
        float cs = EffectiveChunkSize();
        return new Vector3((cell.x + 0.5f) * cs, (cell.y + 0.5f) * cs, (cell.z + 0.5f) * cs);
    }

    // Orders cells FARTHEST-first by squared distance from _sortPlayerPos to the cell center. _loadList is
    // sorted with this so the NEAREST pending cell sits at the tail and is popped first (O(1) removal from the
    // end). Cached as a field (_farthestFirst) so sorting allocates no delegate.
    int CompareFarthestFirst(Vector3Int a, Vector3Int b)
    {
        float da = (CellCenter(a) - _sortPlayerPos).sqrMagnitude;
        float db = (CellCenter(b) - _sortPlayerPos).sqrMagnitude;
        return db.CompareTo(da);
    }

    // Re-prioritise the pending load list nearest-first for the given player position. Called ONLY from a
    // streaming recompute (player moved past the restream threshold), never per frame — so this O(n log n)
    // sort runs on a small surface-shell cell set at most a few times a second, with no allocation.
    // Skipped when nothing meaningful changed (no cells added AND the player barely moved since the last sort)
    // so a recompute that only trims far cells doesn't pay for a redundant re-sort of an unchanged order.
    void SortPendingNearestFirst(Vector3 playerPos, bool addedCells)
    {
        if (_loadList.Count < 2)
            return;
        // If no new cells were enqueued and the player hasn't moved far enough to change the nearest-first
        // ordering, the existing order is still correct — skip the sort.
        if (!addedCells && _hasSortPos)
        {
            float reorderThr = EffectiveChunkSize() * 0.5f;
            if ((playerPos - _lastSortPlayerPos).sqrMagnitude < reorderThr * reorderThr)
                return;
        }
        // Bias the sort reference AHEAD of the player (along the look/move direction projected onto the
        // surface) so cells in front generate first — foliage is ready before the player arrives. This
        // changes only generation PRIORITY, never which cells load/unload, so it can't cause thrash.
        _sortPlayerPos = ComputeSortBiasPos(playerPos);
        _lastSortPlayerPos = playerPos;
        _hasSortPos = true;
        if (_farthestFirst == null)
            _farthestFirst = CompareFarthestFirst;
        _loadList.Sort(_farthestFirst);
    }

    // Reference position used to PRIORITISE pending cells: the player position shifted forward along the
    // camera's look direction (projected onto the local surface tangent so the bias stays on the terrain,
    // not into the sky/ground). Returns the raw player position when look-ahead is disabled or no camera.
    Vector3 ComputeSortBiasPos(Vector3 playerPos)
    {
        // 0 => auto default; negative => disabled (pure nearest-first).
        float ahead = loadAheadDistance < 0f ? 0f : (loadAheadDistance > 0f ? loadAheadDistance : DefaultLoadAhead);
        if (ahead <= 0f)
            return playerPos;
        var cam = ResolveCamera();
        if (cam == null)
            return playerPos;
        Vector3 radial = (playerPos - _center).normalized;
        Vector3 fwd = cam.transform.forward;
        Vector3 tangentFwd = fwd - Vector3.Dot(fwd, radial) * radial;
        if (tangentFwd.sqrMagnitude < 1e-6f)
            return playerPos;
        return playerPos + tangentFwd.normalized * ahead;
    }

    // The multiplier the per-frame raycast budget is currently easing TOWARD (not the live value — see
    // _streamRayBudgetRamp). Warmup burst engages only while a large near-player backlog exists (initial load
    // / teleport); the near-cell boost engages while scattering a cell in the player's immediate vicinity.
    // Combined via Max (not product) so peak cost stays bounded — a near cell during a warmup burst doesn't
    // get burst*near.
    int TargetStreamMultiplier()
    {
        int mult = 1;
        int burst = Mathf.Max(1, streamWarmupBurst);
        if (burst > 1 && _loadList.Count >= StreamBurstBacklogCells)
            mult = burst;
        if (_currentCellNear)
            mult = Mathf.Max(mult, EffectiveNearCellMultiplier());
        return mult;
    }

    // Hard per-frame ceiling on streaming raycasts AFTER multipliers. This is the knob that kills the periodic
    // stutter: it bounds the absolute spike no matter what the warmup/near multipliers ask for. 0 => auto
    // (1.5 × scatterPerFrame): a modest boost for pop-in that never explodes into a full multiplied burst.
    int EffectiveMaxStreamRaysPerFrame()
    {
        int baseBudget = Mathf.Max(500, scatterPerFrame);
        if (maxStreamRaysPerFrame > 0)
            return Mathf.Max(baseBudget, maxStreamRaysPerFrame);
        return Mathf.CeilToInt(baseBudget * 1.5f);
    }

    // Hard per-frame ceiling on EXPENSIVE surface evaluations (land attempts that run the full normal +
    // colour/zone + density pipeline). This is the post-raycast pop-in knob: ocean attempts are now nearly
    // free, so the residual hitch is many full land evaluations landing in one frame. Bounding them spreads
    // that cost across frames WITHOUT reducing density (same instances, more frames). 0 => auto
    // (scatterPerFrame / 8, clamped to a smooth 1000..4000), independent of the near-cell raycast boost so the
    // expensive work stays even while cheap ocean probing can still burst.
    int EffectiveMaxSurfaceEvalsPerFrame()
    {
        if (maxSurfaceEvalsPerFrame > 0)
            return maxSurfaceEvalsPerFrame;
        int baseBudget = Mathf.Max(500, scatterPerFrame);
        // With Burst on, this budget governs only the MANAGED classification+placement (the noise/normal
        // cost moved off-thread), so the same per-frame count buys far more real throughput. The auto
        // ceiling is therefore higher than the old raycast-era value so land fills in faster. The Burst
        // consumer also boosts this for near cells (see ScatterCellJobbed). 0 = auto (scatterPerFrame / 6,
        // clamped 1500..6000); set explicitly to override.
        return Mathf.Clamp(baseBudget / 6, 1500, 6000);
    }

    // Eases the live budget multiplier toward the target by a fixed step per frame, so a cell becoming "near"
    // (or a warmup burst engaging) ramps the per-frame cost up smoothly instead of cliff-spiking in one frame.
    // Called at every streaming frame yield (the only places a frame actually advances). Ramps DOWN the same
    // way when the boost ends so the cost trails off gently too.
    void AdvanceStreamRamp()
    {
        float target = TargetStreamMultiplier();
        int rampFrames = Mathf.Max(1, nearCellBudgetRampFrames);
        // Step is paced against the largest configured boost so the ramp always takes ~rampFrames frames to
        // span the full range, regardless of which multiplier is currently active.
        float span = Mathf.Max(EffectiveNearCellMultiplier(), Mathf.Max(1, streamWarmupBurst)) - 1f;
        float step = span <= 0f ? float.MaxValue : span / rampFrames;
        if (_streamRayBudgetRamp < target)
            _streamRayBudgetRamp = Mathf.Min(target, _streamRayBudgetRamp + step);
        else if (_streamRayBudgetRamp > target)
            _streamRayBudgetRamp = Mathf.Max(target, _streamRayBudgetRamp - step);
    }

    // Live per-frame raycast budget: the steady scatterPerFrame scaled by the eased ramp multiplier, then
    // clamped to the hard ceiling. Cheap to call every attempt (just reads the cached ramp value).
    int EffectiveStreamRayBudget()
    {
        int baseBudget = Mathf.Max(500, scatterPerFrame);
        int budget = Mathf.RoundToInt(baseBudget * Mathf.Max(1f, _streamRayBudgetRamp));
        return Mathf.Min(budget, EffectiveMaxStreamRaysPerFrame());
    }

    int EffectiveNearCellMultiplier() =>
        nearCellBudgetMultiplier > 0 ? nearCellBudgetMultiplier : DefaultNearCellBudgetMultiplier;

    float EffectiveNearCellRadius() =>
        nearCellRadius > 0f ? nearCellRadius : DefaultNearCellRadius;

    // Player position = the same camera we cull against (so foliage follows the gameplay view), else a
    // tagged Player object. Returns false if neither resolves this frame (caller just waits).
    bool TryResolvePlayerPos(out Vector3 pos)
    {
        var cam = ResolveCamera();
        if (cam != null)
        {
            pos = cam.transform.position;
            return true;
        }
        if (!string.IsNullOrEmpty(playerTag))
        {
            var go = GameObject.FindGameObjectWithTag(playerTag);
            if (go != null)
            {
                pos = go.transform.position;
                return true;
            }
        }
        pos = Vector3.zero;
        return false;
    }

    // Persistent streaming driver: re-evaluates the loaded set when the player moves, then drains the load
    // queue scattering one cell at a time (each cell yields internally to spread work across frames).
    IEnumerator StreamingRoutine()
    {
        while (_ready)
        {
            if (TryResolvePlayerPos(out var ppos))
            {
                float thr = EffectiveRestreamThreshold();
                if (!_hasEvalPos || _loadedCells.Count == 0 ||
                    (ppos - _lastEvalPos).sqrMagnitude >= thr * thr)
                {
                    StreamingRecompute(ppos);
                    _lastEvalPos = ppos;
                    _hasEvalPos = true;
                }
            }

            // Count cells that FINISH back-to-back within this frame. Each completed cell finalizes its grass
            // batch + pooled bounds, so allowing many tiny cells to settle in one frame piles that finalize
            // work into a spike — cap it and force a yield so the cost spreads across frames.
            int cellsThisFrame = 0;
            int cellStartCap = Mathf.Max(1, maxCellsStartedPerFrame);

            while (_loadList.Count > 0)
            {
                // Pop the NEAREST pending cell: the list is kept sorted farthest-first, so the closest cell to
                // the player is at the tail and removing it is O(1). This is what makes generation expand
                // outward from the player instead of sweeping the load box in raster order.
                int last = _loadList.Count - 1;
                var cell = _loadList[last];
                _loadList.RemoveAt(last);
                _queued.Remove(cell);
                if (_loadedCells.Contains(cell) || !_desiredCells.Contains(cell))
                    continue; // already loaded, or the player moved away before we got to it
                yield return StartCoroutine(ScatterCell(cell));

                // Spread finalize/pool-spawn bursts: after a cell completes, force a frame yield once we've
                // settled cellStartCap cells without one. (A large cell that yielded internally still counts
                // as one; this only bounds how many SMALL cells can collapse into a single frame.)
                if (++cellsThisFrame >= cellStartCap)
                {
                    cellsThisFrame = 0;
                    AdvanceStreamRamp();
                    yield return null;
                }

                // Keep the desired set fresh while draining a long queue so foliage tracks fast movement.
                if (TryResolvePlayerPos(out var pp))
                {
                    float thr = EffectiveRestreamThreshold();
                    if ((pp - _lastEvalPos).sqrMagnitude >= thr * thr)
                    {
                        StreamingRecompute(pp);
                        _lastEvalPos = pp;
                        _hasEvalPos = true;
                    }
                }
            }

            // Idle (queue drained): clear the near-cell flag so the target multiplier drops, then let the ramp
            // decay back toward the steady rate so the next boost starts from a low baseline rather than jumping.
            _currentCellNear = false;
            AdvanceStreamRamp();
            yield return null;
        }
    }

    // Decides which cells should exist around the player: enqueues newly-desired cells for loading and
    // unloads loaded cells that have passed the (load + hysteresis) unload radius.
    void StreamingRecompute(Vector3 playerPos)
    {
        float cs = EffectiveChunkSize();
        float loadR = EffectiveLoadRadius();
        float unloadR = EffectiveUnloadRadius();
        float halfDiag = cs * 0.8660254f; // (sqrt 3)/2 * cs: a cell cube's half-diagonal
        float loadIncl = loadR + halfDiag;
        float unloadExcl = unloadR + halfDiag;

        _desiredCells.Clear();
        Vector3Int pcell = WorldToCell(playerPos);
        int range = Mathf.Min(MaxCellScanRange, Mathf.CeilToInt(loadR / cs) + 1);

        bool addedCells = false;
        for (int dx = -range; dx <= range; dx++)
        for (int dy = -range; dy <= range; dy++)
        for (int dz = -range; dz <= range; dz++)
        {
            var cell = new Vector3Int(pcell.x + dx, pcell.y + dy, pcell.z + dz);
            Vector3 cc = CellCenter(cell);

            // Skip cells that can't contain surface: fully inside the planet, or fully above the surface band.
            float dRadial = (cc - _center).magnitude;
            if (dRadial + halfDiag < _waterLineRadius - cs) continue;
            if (dRadial - halfDiag > _maxRadius + cs) continue;

            if ((cc - playerPos).sqrMagnitude > loadIncl * loadIncl) continue;

            _desiredCells.Add(cell);
            if (!_loadedCells.Contains(cell) && !_queued.Contains(cell))
            {
                _loadList.Add(cell);
                _queued.Add(cell);
                addedCells = true;
            }
        }

        // Re-prioritise the pending set nearest-first for the player's CURRENT position. Newly-added cells
        // (above) and any cells still pending from a previous eval are all re-ordered together, so the closest
        // unloaded cell is always generated next as the player moves. Cheap: runs only on a recompute, and
        // skipped entirely when nothing was added and the player barely moved (see SortPendingNearestFirst).
        SortPendingNearestFirst(playerPos, addedCells);

        // Unload loaded cells that have drifted beyond the unload radius (hysteresis prevents thrash).
        if (_loadedCells.Count > 0)
        {
            _unloadScratch.Clear();
            foreach (var cell in _loadedCells)
            {
                Vector3 cc = CellCenter(cell);
                if ((cc - playerPos).sqrMagnitude > unloadExcl * unloadExcl)
                    _unloadScratch.Add(cell);
            }
            for (int i = 0; i < _unloadScratch.Count; i++)
                UnloadCell(_unloadScratch[i]);
        }

        if (logResults)
            Debug.Log($"[FoliageByColour] Stream eval @ {playerPos}: loaded {_loadedCells.Count}, desired {_desiredCells.Count}, pending {_loadList.Count} (nearest-first).");
    }

    // Releases all foliage owned by a cell across every rule (grass matrices freed, pooled objects destroyed).
    void UnloadCell(Vector3Int cell)
    {
        foreach (var rt in _runtimes)
        {
            rt.gpu?.RemoveChunk(cell);

            if (rt.objChunkMap != null && rt.objChunkMap.TryGetValue(cell, out var oc))
            {
                var objs = oc.objects;
                for (int o = 0; o < objs.Count; o++)
                    if (objs[o] != null)
                        Object.Destroy(objs[o]);
                rt.objChunkMap.Remove(cell);
                rt.objChunks?.Remove(oc);
            }
        }
        _loadedCells.Remove(cell);
    }

    static bool CellFull(int[] placed, int[] target)
    {
        for (int i = 0; i < placed.Length; i++)
            if (placed[i] < target[i])
                return false;
        return true;
    }

    // Dispatcher: route a cell to the Burst-jobbed sampler (heavy noise off the main thread) when the
    // snapshot is ready, otherwise the original main-thread analytic path. Both produce identical
    // placement semantics (same rules/zones/density/spacing); only WHERE the surface sampling runs differs.
    IEnumerator ScatterCell(Vector3Int cell)
    {
        if (_burstReady)
            yield return StartCoroutine(ScatterCellJobbed(cell));
        else
            yield return StartCoroutine(ScatterCellMainThread(cell));
    }

    // Scatters one surface cell's foliage: ANALYTICALLY evaluates the planet surface for random directions
    // whose surface point falls inside THIS cell (no Physics.Raycast), then
    // applies ALL the same placement rules as the legacy scatter (slope, elevation, biome/
    // greenness match, biome exclusion/dominance, latitude, keepProb density, per-cell spacing grid,
    // orientation, scale, surfaceOffset), then finalizes the cell's grass batch + pooled bounds. Work is
    // spread across frames via the existing scatterPerFrame / maxInstantiatesPerFrame budgets.
    //
    // MAIN-THREAD FALLBACK: used when Burst is disabled/unavailable (_burstReady == false). The Burst path
    // (ScatterCellJobbed) mirrors this exactly but moves the per-attempt surface sampling onto worker threads.
    IEnumerator ScatterCellMainThread(Vector3Int cell)
    {
        float cs = EffectiveChunkSize();
        Vector3 cellOrigin = new Vector3(cell.x * cs, cell.y * cs, cell.z * cs);
        Vector3 cellCenter = cellOrigin + Vector3.one * (cs * 0.5f);

        // Near-cell priority: if this cell is in the player's immediate vicinity, EffectiveStreamRayBudget
        // will boost its per-frame ray budget so the foreground fills fast. Distant cells leave this false and
        // drain at the cheap steady rate, so the sustained per-frame cost (the stutter source) stays low.
        float nearR = EffectiveNearCellRadius();
        _currentCellNear = TryResolvePlayerPos(out var nearProbePos) &&
                           (cellCenter - nearProbePos).sqrMagnitude <= nearR * nearR;

        // Guard: cells with no surface in their band (shouldn't be queued, but be safe) load as empty.
        float halfDiag = cs * 0.8660254f;
        float dRadial = (cellCenter - _center).magnitude;
        if (dRadial + halfDiag < _waterLineRadius - cs || dRadial - halfDiag > _maxRadius + cs)
        {
            _loadedCells.Add(cell);
            yield break;
        }

        int n = _runtimes.Count;
        // Reuse cell scratch across cells (allocate once, then clear) so streaming a cell allocates no
        // garbage. Only one ScatterCell runs at a time, so these shared buffers can't be clobbered.
        if (_cellTargetScratch == null || _cellTargetScratch.Length < n)
        {
            _cellTargetScratch = new int[n];
            _cellPlacedScratch = new int[n];
            _occupiedScratch = new HashSet<Vector3Int>[n];
            for (int i = 0; i < n; i++)
                _occupiedScratch[i] = new HashSet<Vector3Int>();
        }
        int[] cellTarget = _cellTargetScratch;
        int[] cellPlaced = _cellPlacedScratch;
        HashSet<Vector3Int>[] occupied = _occupiedScratch; // per-rule local spacing grid (cell-scoped)
        long totalCellTarget = 0;
        for (int i = 0; i < n; i++)
        {
            float f = _runtimes[i].cellTargetF;
            int t = Mathf.FloorToInt(f);
            if (Random.value < f - t) t++; // stochastic rounding keeps sparse rules at correct avg density
            cellTarget[i] = t;
            cellPlaced[i] = 0;
            totalCellTarget += t;
            occupied[i].Clear();
        }

        if (totalCellTarget <= 0)
        {
            _loadedCells.Add(cell);
            yield break;
        }

        long maxAttempts = totalCellTarget * attemptBudgetMultiplier + 64;

        for (long attempt = 0; attempt < maxAttempts && !CellFull(cellPlaced, cellTarget); attempt++)
        {
            // Per-frame raycast budget bursts above the steady rate while a near-player backlog exists, so the
            // immediate area (generated first) fills fast; steady rate otherwise (no spikes on normal walking).
            if (++_streamRayCount >= EffectiveStreamRayBudget())
            {
                _streamRayCount = 0;
                _streamInstCount = 0;
                _streamSurfaceEvalCount = 0;
                AdvanceStreamRamp();
                yield return null;
            }

            // Random direction toward this cell: sample a point inside the cell cube, take its direction from
            // the planet center. The surface point along that direction is evaluated ANALYTICALLY from the
            // planet's deterministic shape function (no Physics.Raycast) — this removes the per-attempt mesh
            // query that spiked on cell crossings, and makes "wasted" ocean attempts (rejected below) nearly free.
            Vector3 p = cellOrigin + new Vector3(Random.value, Random.value, Random.value) * cs;
            Vector3 dir = p - _center;
            float dl = dir.magnitude;
            if (dl < 1e-4f) continue;
            dir /= dl;

            // Analytic surface point along this direction (drop-in for the old raycast hit.point). The surface
            // is star-shaped (one radius per direction), so this lands at the same place the inward ray hit the
            // collider mesh, to within tessellation error.
            Vector3 pos = _planet.GetSurfacePointWorld(dir);
            if (WorldToCell(pos) != cell)
                continue; // point belongs to a neighbouring cell — it will own that point when it loads

            Vector3 radial = (pos - _center).normalized;
            float dist = (pos - _center).magnitude;
            // Ocean/water rejection: anything at or below the ocean sea level (+ dry clearance) is
            // underwater. Must use the ocean shell radius, not the planet base sphere — sea level sits
            // above the base on this planet, and the old baseRadius-1 gate left a thick submerged band.
            if (dist < _waterLineRadius) continue;

            // Past the water gate this attempt is committed to the EXPENSIVE pipeline (surface normal +
            // colour/zone classification + per-rule density). Charge the per-frame surface-eval budget and
            // yield when it's exhausted, so a land-heavy near cell spreads its costly work over several frames
            // instead of collapsing it into one (the residual pop-in hitch). Cheap ocean rejects above never
            // reach here, so they stay free and keep draining at the raycast rate — coastal density is unaffected.
            if (++_streamSurfaceEvalCount >= EffectiveMaxSurfaceEvalsPerFrame())
            {
                _streamSurfaceEvalCount = 0;
                _streamRayCount = 0;
                _streamInstCount = 0;
                AdvanceStreamRamp();
                yield return null;
            }

            float elevationNorm = _planet.GetNormalizedElevationAtPosition(pos);
            // Analytic surface normal/slope (3 noise samples, via GetSurfaceNormalWorld) are computed LAZILY,
            // only once a candidate rule actually reaches its slope test below. Attempts whose surface point
            // classifies to an empty 'place nothing' zone, or where every candidate rule is already full for
            // this cell, never touch it. 'radial' is a safe fallback that is always overwritten before real use.
            bool haveNormal = false;
            Vector3 hitNormal = radial;
            float slope = 0f;

            // NEAREST-OF-PALETTE (streaming): same winner-take-all classification as the legacy scatter,
            // but counted against this cell's per-rule targets (cellTarget) and cell-scoped spacing grids.
            // Carpet grass overlays continuously by greenness (not locked to colour-zone patches).
            if (placementMode == FoliagePlacementMode.NearestPaletteColour)
            {
                var zoneRts = ChooseZoneRuntimes(pos);
                if (zoneRts != null && zoneRts.Count > 0)
                {
                    for (int z = 0; z < zoneRts.Count; z++)
                    {
                        var rt = zoneRts[z];
                        int idx = rt.runtimeIndex;
                        if (cellPlaced[idx] >= cellTarget[idx])
                            continue;
                        if (!haveNormal)
                        {
                            hitNormal = _planet.GetSurfaceNormalWorld(dir);
                            slope = Vector3.Angle(hitNormal, radial);
                            haveNormal = true;
                        }
                        if (slope > rt.rule.maxSlope)
                        { rt.rejSlope++; continue; }
                        if (elevationNorm < rt.rule.elevationRange.x || elevationNorm > rt.rule.elevationRange.y)
                        { rt.rejElev++; continue; }
                        var res = TryPlace(rt, pos, hitNormal, radial, occupied[idx], ClusterKeepProb(rt.rule, pos));
                        if (res != PlaceResult.Skipped)
                            cellPlaced[idx]++;
                        if (res == PlaceResult.PlacedPooled && ++_streamInstCount >= maxInstantiatesPerFrame)
                        { _streamInstCount = 0; _streamRayCount = 0; _streamSurfaceEvalCount = 0; AdvanceStreamRamp(); yield return null; }
                    }
                }
                if (_carpetRuntimes != null && _carpetRuntimes.Count > 0)
                {
                    if (!haveNormal)
                    {
                        hitNormal = _planet.GetSurfaceNormalWorld(dir);
                        slope = Vector3.Angle(hitNormal, radial);
                        haveNormal = true;
                    }
                    if (TryPlaceCarpetAt(pos, hitNormal, radial, slope, elevationNorm, cellPlaced, cellTarget, occupied)
                        && ++_streamInstCount >= maxInstantiatesPerFrame)
                    { _streamInstCount = 0; _streamRayCount = 0; _streamSurfaceEvalCount = 0; AdvanceStreamRamp(); yield return null; }
                }
                continue;
            }

            Color keyColour = _planet.GetSurfaceKeyColorAtPosition(pos);
            _planet.ClassifySurfaceAtPosition(pos, out int clsBiome, out int clsKey, out _);

            RuleRuntime best = null;
            int bestIdx = -1;
            float bestM = -1f;
            for (int i = 0; i < n; i++)
            {
                var rt = _runtimes[i];
                if (cellPlaced[i] >= cellTarget[i]) continue;
                if (!haveNormal)
                {
                    hitNormal = _planet.GetSurfaceNormalWorld(dir);
                    slope = Vector3.Angle(hitNormal, radial);
                    haveNormal = true;
                }
                if (slope > rt.rule.maxSlope) { rt.rejSlope++; continue; }
                if (elevationNorm < rt.rule.elevationRange.x || elevationNorm > rt.rule.elevationRange.y)
                { rt.rejElev++; continue; }

                float m = RuleMatchStrength(_planet, rt.rule, pos, keyColour, clsBiome, clsKey);
                if (m >= 0f && m > bestM)
                {
                    bestM = m;
                    best = rt;
                    bestIdx = i;
                }
            }

            if (best == null) continue;

            float keepProb = KeepProb(best.rule, bestM);
            keepProb *= BiomeExclusionFactor(_planet, best.rule, pos);
            keepProb *= BiomeDominanceFactor(_planet, best.rule, pos);
            if (best.rule.latitudeInfluence > 0f)
            {
                float biomePercent = _planet.GetBiomePercentAtPosition(pos);
                keepProb *= LatitudeFactor(best.rule, biomePercent, _numBiomes);
            }
            if (Random.value > keepProb) { best.rejDensity++; continue; }

            var scell = new Vector3Int(
                Mathf.FloorToInt(pos.x * best.invCell),
                Mathf.FloorToInt(pos.y * best.invCell),
                Mathf.FloorToInt(pos.z * best.invCell));
            if (!occupied[bestIdx].Add(scell)) { best.rejSpacing++; continue; }

            Vector3 up = best.rule.orient == FoliageOrientMode.Upright ? radial : hitNormal;
            Quaternion rot = Quaternion.FromToRotation(Vector3.up, up)
                             * Quaternion.AngleAxis(Random.value * 360f, Vector3.up);
            float scale = Random.Range(best.rule.scaleRange.x, best.rule.scaleRange.y);
            Vector3 placePos = pos + hitNormal * best.rule.surfaceOffset;

            if (best.gpu != null)
            {
                Matrix4x4 baseTRS = Matrix4x4.TRS(placePos, rot, Vector3.one * scale);
                best.gpu.AddInstance(baseTRS);
            }
            else if (best.poolContainer != null && best.prefabs != null && best.prefabs.Count > 0)
            {
                var prefab = best.prefabs[Random.Range(0, best.prefabs.Count)];
                if (prefab != null)
                {
                    var go = Object.Instantiate(prefab, placePos, rot, best.poolContainer);
                    go.transform.localScale = prefab.transform.localScale * scale;
                    Stargrave.CameraOcclusion.FoliageOccluder.EnsureOn(go);
                    AddPooledToChunk(best, go, placePos);
                    BeginPooledPhaseIn(go.transform);
                    if (++_streamInstCount >= maxInstantiatesPerFrame)
                    {
                        _streamInstCount = 0;
                        _streamRayCount = 0;
                        _streamSurfaceEvalCount = 0;
                        AdvanceStreamRamp();
                        yield return null;
                    }
                }
            }

            cellPlaced[bestIdx]++;
            best.placed++;
        }

        // Finalize this cell so Draw()/CullPooled() pick it up next frame.
        foreach (var rt in _runtimes)
        {
            rt.gpu?.FinalizeChunk(cell);
            if (rt.poolContainer != null)
                FinalizeObjChunk(rt, cell);
        }
        _loadedCells.Add(cell);
    }

    // BURST PATH. Identical placement to ScatterCellMainThread, but the per-candidate surface SAMPLING
    // (elevation noise + surface point + water/cell reject + finite-difference normal + slope + normalized
    // elevation) is computed by FoliageScatterJob on worker threads. The main thread only generates the
    // (deterministic) candidate directions, then consumes accepted candidates and runs the managed
    // colour/biome classification + rule/zone selection + spacing grid + placement — capped per frame so
    // that managed work can't hitch. Density/look match the main-thread path; only the surface math moved
    // off-thread and the wasted ocean probes now cost the main thread nothing (the job rejects them).
    IEnumerator ScatterCellJobbed(Vector3Int cell)
    {
        float cs = EffectiveChunkSize();
        Vector3 cellOrigin = new Vector3(cell.x * cs, cell.y * cs, cell.z * cs);
        Vector3 cellCenter = cellOrigin + Vector3.one * (cs * 0.5f);

        float nearR = EffectiveNearCellRadius();
        _currentCellNear = TryResolvePlayerPos(out var nearProbePos) &&
                           (cellCenter - nearProbePos).sqrMagnitude <= nearR * nearR;

        float halfDiag = cs * 0.8660254f;
        float dRadial = (cellCenter - _center).magnitude;
        if (dRadial + halfDiag < _waterLineRadius - cs || dRadial - halfDiag > _maxRadius + cs)
        {
            _loadedCells.Add(cell);
            yield break;
        }

        int n = _runtimes.Count;
        if (_cellTargetScratch == null || _cellTargetScratch.Length < n)
        {
            _cellTargetScratch = new int[n];
            _cellPlacedScratch = new int[n];
            _occupiedScratch = new HashSet<Vector3Int>[n];
            for (int i = 0; i < n; i++)
                _occupiedScratch[i] = new HashSet<Vector3Int>();
        }
        int[] cellTarget = _cellTargetScratch;
        int[] cellPlaced = _cellPlacedScratch;
        HashSet<Vector3Int>[] occupied = _occupiedScratch;
        long totalCellTarget = 0;
        for (int i = 0; i < n; i++)
        {
            float f = _runtimes[i].cellTargetF;
            int t = Mathf.FloorToInt(f);
            if (Random.value < f - t) t++; // stochastic rounding (same as the main-thread path)
            cellTarget[i] = t;
            cellPlaced[i] = 0;
            totalCellTarget += t;
            occupied[i].Clear();
        }

        if (totalCellTarget <= 0)
        {
            _loadedCells.Add(cell);
            yield break;
        }

        long maxAttemptsL = totalCellTarget * attemptBudgetMultiplier + 64;
        int count = (int)System.Math.Min(maxAttemptsL, MaxCellCandidates);
        EnsureJobCapacity(count);

        // Deterministic per-cell candidate directions: a point uniformly inside the cell cube, taken as a
        // direction from the planet center. Seeded from the cell coords so a cell scatters identically
        // regardless of streaming order / reloads. (The PLACEMENT rolls below still use UnityEngine.Random,
        // exactly as the main-thread path — only direction generation is the dedicated reproducible stream.)
        uint seed = CellRngSeedBase
                    ^ (uint)(cell.x * 73856093)
                    ^ (uint)(cell.y * 19349663)
                    ^ (uint)(cell.z * 83492791);
        if (seed == 0u) seed = 1u;
        var rng = new Unity.Mathematics.Random(seed);
        float3 centerF = _center;
        float3 originF = cellOrigin;
        for (int k = 0; k < count; k++)
        {
            float3 p = originF + new float3(rng.NextFloat(), rng.NextFloat(), rng.NextFloat()) * cs;
            float3 dir = p - centerF;
            float dl = math.length(dir);
            _dirArray[k] = dl < 1e-4f ? new float3(0f, 1f, 0f) : dir / dl;
        }

        var job = new FoliageScatterJob
        {
            directions = _dirArray,
            layers = _noiseLayers,
            perm = _perm,
            pads = _pads,
            planetRadius = _planetRadiusLocal,
            scaleFactor = _scaleFactor,
            center = centerF,
            baseRadius = _waterLineRadius,
            invCellSize = 1f / cs,
            elevMin = _elevMinLocal,
            elevMax = _elevMaxLocal,
            cellX = cell.x,
            cellY = cell.y,
            cellZ = cell.z,
            results = _resArray,
        };

        // Schedule, then let the workers run this frame (rendering/culling continue on the main thread),
        // and complete next frame. This keeps the one-cell-at-a-time, nearest-first model while moving the
        // heavy noise off the main thread.
        JobHandle handle = job.Schedule(count, 64);
        _pendingHandle = handle;
        yield return null;
        handle.Complete();
        _pendingHandle = default;

        // Snapshot accepted hits into managed memory BEFORE any yield. DisposeNative / EnsureJobCapacity
        // (pad rebuild, teardown) can free _resArray while this coroutine is suspended.
        if (!_resArray.IsCreated)
        {
            _loadedCells.Add(cell);
            yield break;
        }
        if (_acceptedScratch == null || _acceptedScratch.Length < count)
            _acceptedScratch = new FoliageCandidate[Mathf.Max(count, 1024)];
        int acceptedCount = 0;
        for (int k = 0; k < count; k++)
        {
            FoliageCandidate c = _resArray[k];
            if (c.accepted != 0)
                _acceptedScratch[acceptedCount++] = c;
        }

        // Per-frame cap on CONSUMED accepted candidates (the managed classification + placement). Near
        // cells consume faster so the immediate vicinity fills quickly; distant cells drain at the steady
        // rate. The ocean rejects produced nothing here, so they cost the main thread nothing.
        int consumeBudget = Mathf.Max(1, EffectiveMaxSurfaceEvalsPerFrame());
        if (_currentCellNear)
            consumeBudget = Mathf.Min(consumeBudget * EffectiveNearCellMultiplier(), EffectiveMaxStreamRaysPerFrame());
        int consumed = 0;
        _streamInstCount = 0;

        for (int k = 0; k < acceptedCount && !CellFull(cellPlaced, cellTarget); k++)
        {
            FoliageCandidate cand = _acceptedScratch[k];

            Vector3 pos = cand.pos;
            Vector3 hitNormal = cand.normal;
            Vector3 radial = (pos - _center).normalized;
            float slope = cand.slope;
            float elevationNorm = cand.elevNorm;

            // Amortize the managed classification across frames so a land-heavy cell can't hitch.
            if (++consumed >= consumeBudget)
            {
                consumed = 0;
                _streamInstCount = 0;
                AdvanceStreamRamp();
                yield return null;
            }

            if (placementMode == FoliagePlacementMode.NearestPaletteColour)
            {
                var zoneRts = ChooseZoneRuntimes(pos);
                if (zoneRts != null && zoneRts.Count > 0)
                {
                    for (int z = 0; z < zoneRts.Count; z++)
                    {
                        var rt = zoneRts[z];
                        int idx = rt.runtimeIndex;
                        if (cellPlaced[idx] >= cellTarget[idx])
                            continue;
                        if (slope > rt.rule.maxSlope)
                        { rt.rejSlope++; continue; }
                        if (elevationNorm < rt.rule.elevationRange.x || elevationNorm > rt.rule.elevationRange.y)
                        { rt.rejElev++; continue; }
                        var res = TryPlace(rt, pos, hitNormal, radial, occupied[idx], ClusterKeepProb(rt.rule, pos));
                        if (res != PlaceResult.Skipped)
                            cellPlaced[idx]++;
                        if (res == PlaceResult.PlacedPooled && ++_streamInstCount >= maxInstantiatesPerFrame)
                        { _streamInstCount = 0; AdvanceStreamRamp(); yield return null; }
                    }
                }
                if (TryPlaceCarpetAt(pos, hitNormal, radial, slope, elevationNorm, cellPlaced, cellTarget, occupied)
                    && ++_streamInstCount >= maxInstantiatesPerFrame)
                { _streamInstCount = 0; AdvanceStreamRamp(); yield return null; }
                continue;
            }

            Color keyColour = _planet.GetSurfaceKeyColorAtPosition(pos);
            _planet.ClassifySurfaceAtPosition(pos, out int clsBiome, out int clsKey, out _);

            RuleRuntime best = null;
            int bestIdx = -1;
            float bestM = -1f;
            for (int i = 0; i < n; i++)
            {
                var rt = _runtimes[i];
                if (cellPlaced[i] >= cellTarget[i]) continue;
                if (slope > rt.rule.maxSlope) { rt.rejSlope++; continue; }
                if (elevationNorm < rt.rule.elevationRange.x || elevationNorm > rt.rule.elevationRange.y)
                { rt.rejElev++; continue; }

                float m = RuleMatchStrength(_planet, rt.rule, pos, keyColour, clsBiome, clsKey);
                if (m >= 0f && m > bestM)
                {
                    bestM = m;
                    best = rt;
                    bestIdx = i;
                }
            }

            if (best == null) continue;

            float keepProb = KeepProb(best.rule, bestM);
            keepProb *= BiomeExclusionFactor(_planet, best.rule, pos);
            keepProb *= BiomeDominanceFactor(_planet, best.rule, pos);
            if (best.rule.latitudeInfluence > 0f)
            {
                float biomePercent = _planet.GetBiomePercentAtPosition(pos);
                keepProb *= LatitudeFactor(best.rule, biomePercent, _numBiomes);
            }
            if (Random.value > keepProb) { best.rejDensity++; continue; }

            var scell = new Vector3Int(
                Mathf.FloorToInt(pos.x * best.invCell),
                Mathf.FloorToInt(pos.y * best.invCell),
                Mathf.FloorToInt(pos.z * best.invCell));
            if (!occupied[bestIdx].Add(scell)) { best.rejSpacing++; continue; }

            Vector3 up = best.rule.orient == FoliageOrientMode.Upright ? radial : hitNormal;
            Quaternion rot = Quaternion.FromToRotation(Vector3.up, up)
                             * Quaternion.AngleAxis(Random.value * 360f, Vector3.up);
            float scale = Random.Range(best.rule.scaleRange.x, best.rule.scaleRange.y);
            Vector3 placePos = pos + hitNormal * best.rule.surfaceOffset;

            if (best.gpu != null)
            {
                Matrix4x4 baseTRS = Matrix4x4.TRS(placePos, rot, Vector3.one * scale);
                best.gpu.AddInstance(baseTRS);
            }
            else if (best.poolContainer != null && best.prefabs != null && best.prefabs.Count > 0)
            {
                var prefab = best.prefabs[Random.Range(0, best.prefabs.Count)];
                if (prefab != null)
                {
                    var go = Object.Instantiate(prefab, placePos, rot, best.poolContainer);
                    go.transform.localScale = prefab.transform.localScale * scale;
                    Stargrave.CameraOcclusion.FoliageOccluder.EnsureOn(go);
                    AddPooledToChunk(best, go, placePos);
                    BeginPooledPhaseIn(go.transform);
                    if (++_streamInstCount >= maxInstantiatesPerFrame)
                    {
                        _streamInstCount = 0;
                        AdvanceStreamRamp();
                        yield return null;
                    }
                }
            }

            cellPlaced[bestIdx]++;
            best.placed++;
        }

        foreach (var rt in _runtimes)
        {
            rt.gpu?.FinalizeChunk(cell);
            if (rt.poolContainer != null)
                FinalizeObjChunk(rt, cell);
        }
        _loadedCells.Add(cell);
    }

    // Builds the flat chunk list + bounds for a pooled rule (no-op for GPU/grass rules).
    void FinalizeObjChunks(RuleRuntime rt)
    {
        if (rt.objChunkMap == null || rt.objChunkMap.Count == 0)
            return;

        float pad = Mathf.Max(2f, EffectiveChunkSize() * 0.5f);
        Vector3 padV = new Vector3(pad, pad, pad);

        rt.objChunks = new List<ObjChunk>(rt.objChunkMap.Count);
        foreach (var kv in rt.objChunkMap)
        {
            var chunk = kv.Value;
            Vector3 bmin = chunk.min - padV;
            Vector3 bmax = chunk.max + padV;
            chunk.center = (bmin + bmax) * 0.5f;
            chunk.radius = ((bmax - bmin) * 0.5f).magnitude;
            chunk.bounds = new Bounds(chunk.center, bmax - bmin);
            rt.objChunks.Add(chunk);
        }
    }

    // Frustum-culls pooled objects per chunk: a chunk is active when it is inside the camera frustum
    // expanded by a small border, otherwise it is deactivated. 'drawDistance' is effectively unlimited
    // (see Update) so distance never removes an in-view chunk. SetActive() is only called when a chunk
    // crosses the visibility threshold, so steady-state cost is one frustum test per chunk (not per
    // object) with zero churn.
    void CullPooled(RuleRuntime rt, bool cull, Vector3 camPos, Plane[] planes, float drawDistance)
    {
        var list = rt.objChunks;

        // Stamp distances and sort nearest-first so, when many chunks change state in one frame (e.g. on load
        // or a big camera jump), the nearest objects activate before far ones. In-place sort + cached comparer
        // => no per-frame allocation. SetActive is still only called on a visibility TRANSITION.
        for (int i = 0; i < list.Count; i++)
            list[i].sortDist = (list[i].center - camPos).sqrMagnitude;
        if (list.Count > 1)
            list.Sort(ObjNearestFirst);

        // Hysteresis: activate a chunk when it enters the (margin-expanded) view region, but only deactivate
        // once it leaves a slightly LARGER region. This makes objects ready just outside the visible edge
        // (no pop-in on rotate) and prevents on/off flicker for chunks sitting on the boundary.
        float onMargin = objectFrustumMargin > 0f ? objectFrustumMargin : DefaultObjectFrustumMargin;
        float offMargin = onMargin + Mathf.Max(4f, onMargin * 0.4f);

        for (int i = 0; i < list.Count; i++)
        {
            var chunk = list[i];
            bool want;
            if (!cull)
            {
                want = true; // culling off => everything visible
            }
            else if (chunk.active)
            {
                // Stay active until the chunk leaves the larger off-region (distance OR frustum).
                float maxD = drawDistance + offMargin + chunk.radius;
                want = chunk.sortDist <= maxD * maxD
                       && (planes == null || GeometryUtility.TestPlanesAABB(planes, Expanded(chunk.bounds, offMargin)));
            }
            else
            {
                // Activate when the chunk enters the on-region (in range AND inside the margin-expanded frustum).
                float maxD = drawDistance + onMargin + chunk.radius;
                want = chunk.sortDist <= maxD * maxD
                       && (planes == null || GeometryUtility.TestPlanesAABB(planes, Expanded(chunk.bounds, onMargin)));
            }

            if (want == chunk.active)
                continue;
            chunk.active = want;
            var objs = chunk.objects;
            for (int o = 0; o < objs.Count; o++)
                if (objs[o] != null)
                    objs[o].SetActive(want);
        }
    }

    // Returns a copy of the bounds grown by 'margin' on every side (struct value -> no allocation).
    static Bounds Expanded(Bounds b, float margin)
    {
        b.Expand(2f * margin);
        return b;
    }

    /// <summary>
    /// Call after building pads reshape the planet: refresh Burst elevation snapshot and clear foliage
    /// that now sits on pad plazas so streaming can refill around (not on) the pad.
    /// </summary>
    public void NotifyBuildingPadsChanged()
    {
        if (_planet == null)
            _planet = Object.FindFirstObjectByType<Planet>();

        BuildBurstSnapshot();
        ClearFoliageOnBuildingPads();
    }

    /// <summary>
    /// Destroys pooled instances on suppressing pads and unloads streaming cells whose center falls on a
    /// pad so they restream without plaza foliage.
    /// </summary>
    void ClearFoliageOnBuildingPads()
    {
        if (PlanetBuildingPads.Count == 0 || _planet == null)
            return;

        Vector3 center = _planet.transform.position;

        // Unload streamed cells that sit on pads (GPU + pooled), so restream respects pad suppression.
        _unloadScratch.Clear();
        foreach (var cell in _loadedCells)
        {
            Vector3 cc = CellCenter(cell);
            Vector3 radial = cc - center;
            if (radial.sqrMagnitude < 1e-8f)
                continue;
            if (PlanetBuildingPads.ShouldSuppressFoliage(radial.normalized))
                _unloadScratch.Add(cell);
        }
        for (int i = 0; i < _unloadScratch.Count; i++)
            UnloadCell(_unloadScratch[i]);

        // Safety pass: destroy any pooled leftovers still sitting on a pad (e.g. non-streamed / edge).
        for (int r = 0; r < _runtimes.Count; r++)
        {
            RuleRuntime rt = _runtimes[r];
            if (rt.objChunkMap == null)
                continue;

            _unloadScratch.Clear();
            foreach (var kv in rt.objChunkMap)
            {
                ObjChunk oc = kv.Value;
                var objs = oc.objects;
                for (int o = objs.Count - 1; o >= 0; o--)
                {
                    GameObject go = objs[o];
                    if (go == null)
                    {
                        objs.RemoveAt(o);
                        continue;
                    }
                    Vector3 radial = go.transform.position - center;
                    if (radial.sqrMagnitude < 1e-8f)
                        continue;
                    if (!PlanetBuildingPads.ShouldSuppressFoliage(radial.normalized))
                        continue;
                    Object.Destroy(go);
                    objs.RemoveAt(o);
                    rt.placed = Mathf.Max(0, rt.placed - 1);
                }
                if (objs.Count == 0)
                    _unloadScratch.Add(kv.Key);
            }
            for (int i = 0; i < _unloadScratch.Count; i++)
            {
                Vector3Int key = _unloadScratch[i];
                if (rt.objChunkMap.TryGetValue(key, out var empty) && empty.objects.Count == 0)
                {
                    rt.objChunkMap.Remove(key);
                    rt.objChunks?.Remove(empty);
                }
            }
        }
    }

    void OnDisable()
    {
        _ready = false;
    }

    void OnDestroy()
    {
        DisposeNative();
        foreach (var rt in _runtimes)
            rt.gpu?.Dispose();
        _runtimes.Clear();
    }

    [ContextMenu("Diagnose Probe Position")]
    void DiagnoseProbePosition()
    {
        var planet = Object.FindFirstObjectByType<Planet>();
        if (planet == null)
        {
            Debug.LogWarning("[FoliageByColour] Diagnose: no Planet found.");
            return;
        }

        // In edit mode the planet hasn't generated, so build it first — otherwise every query is garbage.
        if (!planet.IsGenerated)
            planet.GeneratePlanet();

        Vector3 pos = debugProbePosition;
        Vector3 center = planet.transform.position;
        Vector3 radial = (pos - center).normalized;
        float elevNorm = planet.GetNormalizedElevationAtPosition(pos);
        Color keyColour = planet.GetSurfaceKeyColorAtPosition(pos);
        planet.ClassifySurfaceAtPosition(pos, out int clsBiome, out int clsKey, out _);

        float maxRadius = planet.shapeSettings != null ? planet.GetMaxSurfaceRadiusWorld() : 400f;
        var mask = LayerMask.GetMask("Default", "Ground");
        if (mask == 0) mask = ~0;
        float slope = -1f;
        if (Physics.Raycast(center + radial * (maxRadius + rayHeightAboveSurface), -radial,
                out var hit, maxRadius + rayHeightAboveSurface * 3f, mask))
            slope = Vector3.Angle(hit.normal, radial);

        float biomePercent = planet.GetBiomePercentAtPosition(pos);
        int numBiomes = (planet.colourSettings != null && planet.colourSettings.biomeColourSettings != null &&
                         planet.colourSettings.biomeColourSettings.biomes != null &&
                         planet.colourSettings.biomeColourSettings.biomes.Length > 0)
            ? planet.colourSettings.biomeColourSettings.biomes.Length : 1;

        var sb = new System.Text.StringBuilder();
        sb.Append($"[FoliageByColour] Probe {pos}\n");
        sb.Append($"  dominant key  = biome {clsBiome}, key {clsKey}, RGB ({keyColour.r:F2},{keyColour.g:F2},{keyColour.b:F2})\n");
        sb.Append($"  elevation     = {elevNorm:F3}\n");
        sb.Append($"  slope         = {(slope < 0f ? "no-hit" : slope.ToString("F1"))}\n");
        sb.Append($"  biomePercent  = {biomePercent:F3} (0=first biome latitude .. 1=last), numBiomes {numBiomes}\n");

        var rules = GetActiveRules();
        float bestM = -1f; string bestName = "(none)";
        foreach (var rule in rules)
        {
            if (rule == null || !rule.enabled) continue;
            bool slopeOk = slope < 0f || slope <= rule.maxSlope;
            bool elevOk = elevNorm >= rule.elevationRange.x && elevNorm <= rule.elevationRange.y;
            float m = RuleMatchStrength(planet, rule, pos, keyColour, clsBiome, clsKey);
            float baseKeep = m >= 0f ? KeepProb(rule, m) : 0f;
            float biomeFactor = BiomeExclusionFactor(planet, rule, pos);
            float domFactor = BiomeDominanceFactor(planet, rule, pos);
            float latWeight = LatitudeWeight(rule, biomePercent, numBiomes);
            float latFactor = LatitudeFactor(rule, biomePercent, numBiomes);
            float keep = baseKeep * biomeFactor * domFactor * latFactor;
            bool gates = slopeOk && elevOk && m >= 0f;
            if (gates && m > bestM) { bestM = m; bestName = rule.name; }

            string latLine = $"      latitude: biomePercent {biomePercent:F2}, weight {latWeight:F2}, influence {rule.latitudeInfluence:F2} (width {rule.latitudeWidth:F2}) -> baseKeep {baseKeep:F2} * exclude {biomeFactor:F2} * dominance {domFactor:F2} * lat {latFactor:F2} = keepProb {keep:F2}\n";
            if (rule.requireBiomeDominance)
            {
                float reqW = planet.GetBiomeWeightAtPosition(pos, rule.requiredBiomeIndex);
                sb.Append($"      biome-dominance: require biome {rule.requiredBiomeIndex} weight>={rule.minRequiredBiomeWeight:F2}, here={reqW:F2} -> {(reqW >= rule.minRequiredBiomeWeight ? "in-area" : "out")} (factor {domFactor:F2})\n");
            }
            if (rule.useBiomeGradientRule)
            {
                bool greenOk = m >= 0f;
                float greenness = planet.GetSurfaceGreennessAtPosition(pos, rule.biomeIndex);
                float otherInfluence = 1f - Mathf.Clamp01(planet.GetBiomeWeightAtPosition(pos, rule.biomeIndex));
                sb.Append($"  rule '{rule.name}' [{rule.render}] BIOME-GRADIENT(green) biome {rule.biomeIndex} (latitude-independent coverage)\n");
                sb.Append($"      green={(greenOk ? "OK" : "FAIL")} greenness={greenness:F2} | biome{rule.biomeIndex} weight={1f - otherInfluence:F2} other={otherInfluence:F2} (max {rule.maxOtherBiomeInfluence:F2}, factor {biomeFactor:F2}) | slope {(slopeOk ? "OK" : "FAIL")} elev {(elevOk ? "OK" : "FAIL")} -> {(gates ? "candidate" : "blocked")}\n");
                sb.Append(latLine);
            }
            else
            {
                sb.Append($"  rule '{rule.name}' [{rule.render}] COLOUR target ({rule.targetColour.r:F2},{rule.targetColour.g:F2},{rule.targetColour.b:F2}) tol {rule.colourTolerance:F2}\n");
                if (rule.matchByKey)
                    sb.Append($"      matchByKey biome {rule.biomeIndex}/key {rule.keyIndex} vs here {clsBiome}/{clsKey} -> m={(m < 0f ? 0f : m):F2} | slope {(slopeOk ? "OK" : "FAIL")} elev {(elevOk ? "OK" : "FAIL")} -> {(gates ? "candidate" : "blocked")}\n");
                else
                    sb.Append($"      dist={ColourDistance(keyColour, rule.targetColour):F2} m={(m < 0f ? 0f : m):F2} | slope {(slopeOk ? "OK" : "FAIL")} elev {(elevOk ? "OK" : "FAIL")} -> {(gates ? "candidate" : "blocked")}\n");
                sb.Append(latLine);
            }
        }
        sb.Append($"  WINNER (highest m, gates passed): {bestName} (m={(bestM < 0f ? 0f : bestM):F2}) -> {(bestM >= 0f ? "WOULD SPAWN (probabilistic by keepProb)" : "BLOCKED")}");
        Debug.Log(sb.ToString());
    }
}
