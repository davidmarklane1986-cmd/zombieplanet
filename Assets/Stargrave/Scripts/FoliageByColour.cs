using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

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

    [Header("Scatter budget")]
    [Tooltip("Raycasts are spread across frames during scatter to avoid a load hang.")]
    [Min(500)] public int scatterPerFrame = 16000;
    [Tooltip("Cap on GameObjectPool instantiations per frame to avoid hitches when placing trees/rocks.")]
    [Min(16)] public int maxInstantiatesPerFrame = 200;
    [Tooltip("Multiplies the attempt budget over the summed target counts. This is a mostly-ocean planet, so most rays land in water and are wasted.")]
    [Min(2)] public int attemptBudgetMultiplier = 20;

    [Header("Area limits (global)")]
    [Tooltip("Reject points below water (uses the planet's water radius).")]
    public bool excludeUnderwater = true;
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
    [Tooltip("World-unit radius around the player within which foliage is generated. The player can only " +
             "see ~tens of units to the horizon on this planet, so a few hundred units covers the visible " +
             "area plus buffer while being a small fraction of the planet. 0 = built-in default (200).")]
    public float loadRadius = 200f;
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
    // surface reads yellow/brown so grass is excluded; at/above 'Hi' it is solid green so density is
    // full; between them density smoothstep-ramps so grass blends out gradually toward the beach
    // (below) and rock (above) bands instead of cutting off hard.
    const float GradientGreenLo = 0.2f;
    const float GradientGreenHi = 0.6f;

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

    // Streaming defaults, used when the matching serialized field is <= 0 (so streaming works with good
    // values and no scene edit on the existing component).
    const float DefaultLoadRadius = 200f;
    const float DefaultUnloadHysteresis = 60f;
    const float DefaultRestreamMoveThreshold = 20f;
    // Safety clamp on the per-axis cell scan range, so an extreme loadRadius can't freeze the recompute.
    const int MaxCellScanRange = 64;

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
                         float frustumMargin, Camera cam, ref int budgetRemaining)
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
                for (int di = 0; di < chunk.batches.Length; di++)
                {
                    var drawBatches = chunk.batches[di];
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
    Planet _planet;
    int _layer;
    bool _ready;
    Camera _cam;
    readonly Plane[] _frustumPlanes = new Plane[6];

    // ---- Streaming state ----
    // Surface sampling params, captured once after the planet has generated (constant thereafter).
    Vector3 _center;
    float _baseRadius, _maxRadius, _waterRadius, _rayStartRadius, _rayLength;
    int _groundMask, _numBiomes;
    // Cells currently populated, cells we WANT populated this eval, and the pending load queue (+ a mirror
    // set so we never enqueue the same cell twice).
    readonly HashSet<Vector3Int> _loadedCells = new HashSet<Vector3Int>();
    readonly HashSet<Vector3Int> _desiredCells = new HashSet<Vector3Int>();
    readonly HashSet<Vector3Int> _queued = new HashSet<Vector3Int>();
    readonly Queue<Vector3Int> _loadQueue = new Queue<Vector3Int>();
    readonly List<Vector3Int> _unloadScratch = new List<Vector3Int>();
    Vector3 _lastEvalPos;
    bool _hasEvalPos;
    // Shared per-frame work budget so loading MANY cells in one frame still can't hitch: these accumulate
    // across consecutive cells and reset only after a frame yield.
    int _streamRayCount;
    int _streamInstCount;

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
            // Soft gate: keep weakly-green EDGE points (so KeepProb can taper them to ~0), but reject
            // clearly non-green ground so yellow/brown never get grass. Lo sits well above beach-yellow
            // (~0.07) and brown (0).
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
        if (rule.prefabs != null && rule.prefabs.Count > 0)
            return rule.prefabs;

        // Grass fallback: reuse the Meadow Grass prefabs from RichPlanetFlora so the grass rule works
        // even before the user assigns prefabs (parity with GpuGrassCarpet.ResolveGrassPrefabs).
        var profile = Resources.Load<FoliageSpawnProfile>("RichPlanetFlora");
        if (profile != null && profile.spawnRules != null)
        {
            foreach (var r in profile.spawnRules)
                if (r != null && r.name == "Meadow Grass" && r.prefabs != null && r.prefabs.Count > 0)
                    return r.prefabs;
        }
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
        _groundMask = LayerMask.GetMask("Default", "Ground");
        if (_groundMask == 0)
            _groundMask = ~0;
        _rayStartRadius = _maxRadius + rayHeightAboveSurface;
        _rayLength = _maxRadius + rayHeightAboveSurface * 3f;
        _waterRadius = excludeUnderwater ? _planet.GetWaterRadiusWorld() : -1f;
        _numBiomes = BiomeCount();
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
        var rules = GetActiveRules();
        if (rules == null || rules.Count == 0)
            return false;

        foreach (var rule in rules)
        {
            if (rule == null || !rule.enabled)
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

            _runtimes.Add(rt);
        }

        return _runtimes.Count > 0;
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
        float waterRadius = excludeUnderwater ? _planet.GetWaterRadiusWorld() : -1f;

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
        int rejNoHit = 0, rejUnderBase = 0, rejWater = 0, rejNoRule = 0;

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

            if (dist < baseRadius - 1f)
            { rejUnderBase++; continue; }
            if (waterRadius > 0f && dist < waterRadius + 0.2f)
            { rejWater++; continue; }

            float slope = Vector3.Angle(hit.normal, radial);
            float elevationNorm = _planet.GetNormalizedElevationAtPosition(pos);
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
                    AddPooledToChunk(best, go, placePos);
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
                      $"water {rejWater}, noRuleMatch {rejNoRule}. Rules: {_runtimes.Count}, maxAttempts {maxAttempts}.");
        }
    }

    void Update()
    {
        if (!_ready)
            return;

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

        for (int i = 0; i < _runtimes.Count; i++)
        {
            var rt = _runtimes[i];
            if (rt.gpu != null)
            {
                rt.gpu.Draw(_layer, cull, camPos, planes, gdd, SmallFrustumMargin, cam, ref grassBudget);
            }
            else if (rt.objChunks != null)
            {
                CullPooled(rt, cull, camPos, planes, odd);
            }
        }
    }

    float EffectiveChunkSize() => chunkSize > 0f ? chunkSize : DefaultChunkSize;

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

            while (_loadQueue.Count > 0)
            {
                var cell = _loadQueue.Dequeue();
                _queued.Remove(cell);
                if (_loadedCells.Contains(cell) || !_desiredCells.Contains(cell))
                    continue; // already loaded, or the player moved away before we got to it
                yield return StartCoroutine(ScatterCell(cell));

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

        for (int dx = -range; dx <= range; dx++)
        for (int dy = -range; dy <= range; dy++)
        for (int dz = -range; dz <= range; dz++)
        {
            var cell = new Vector3Int(pcell.x + dx, pcell.y + dy, pcell.z + dz);
            Vector3 cc = CellCenter(cell);

            // Skip cells that can't contain surface: fully inside the planet, or fully above the surface band.
            float dRadial = (cc - _center).magnitude;
            if (dRadial + halfDiag < _baseRadius - cs) continue;
            if (dRadial - halfDiag > _maxRadius + cs) continue;

            if ((cc - playerPos).sqrMagnitude > loadIncl * loadIncl) continue;

            _desiredCells.Add(cell);
            if (!_loadedCells.Contains(cell) && !_queued.Contains(cell))
            {
                _loadQueue.Enqueue(cell);
                _queued.Add(cell);
            }
        }

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
            Debug.Log($"[FoliageByColour] Stream eval @ {playerPos}: loaded {_loadedCells.Count}, desired {_desiredCells.Count}, queued {_loadQueue.Count}.");
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

    // Scatters one surface cell's foliage: raycasts random points whose surface hit falls inside THIS cell,
    // applies ALL the same placement rules as the legacy scatter (slope, elevation, underwater, biome/
    // greenness match, biome exclusion/dominance, latitude, keepProb density, per-cell spacing grid,
    // orientation, scale, surfaceOffset), then finalizes the cell's grass batch + pooled bounds. Work is
    // spread across frames via the existing scatterPerFrame / maxInstantiatesPerFrame budgets.
    IEnumerator ScatterCell(Vector3Int cell)
    {
        float cs = EffectiveChunkSize();
        Vector3 cellOrigin = new Vector3(cell.x * cs, cell.y * cs, cell.z * cs);
        Vector3 cellCenter = cellOrigin + Vector3.one * (cs * 0.5f);

        // Guard: cells with no surface in their band (shouldn't be queued, but be safe) load as empty.
        float halfDiag = cs * 0.8660254f;
        float dRadial = (cellCenter - _center).magnitude;
        if (dRadial + halfDiag < _baseRadius - cs || dRadial - halfDiag > _maxRadius + cs)
        {
            _loadedCells.Add(cell);
            yield break;
        }

        int n = _runtimes.Count;
        var cellTarget = new int[n];
        var cellPlaced = new int[n];
        var occupied = new HashSet<Vector3Int>[n]; // per-rule local spacing grid (cell-scoped)
        long totalCellTarget = 0;
        for (int i = 0; i < n; i++)
        {
            float f = _runtimes[i].cellTargetF;
            int t = Mathf.FloorToInt(f);
            if (Random.value < f - t) t++; // stochastic rounding keeps sparse rules at correct avg density
            cellTarget[i] = t;
            totalCellTarget += t;
            occupied[i] = new HashSet<Vector3Int>();
        }

        if (totalCellTarget <= 0)
        {
            _loadedCells.Add(cell);
            yield break;
        }

        long maxAttempts = totalCellTarget * attemptBudgetMultiplier + 64;

        for (long attempt = 0; attempt < maxAttempts && !CellFull(cellPlaced, cellTarget); attempt++)
        {
            if (++_streamRayCount >= scatterPerFrame)
            {
                _streamRayCount = 0;
                _streamInstCount = 0;
                yield return null;
            }

            // Random direction toward this cell: sample a point inside the cell cube, shoot inward from above.
            Vector3 p = cellOrigin + new Vector3(Random.value, Random.value, Random.value) * cs;
            Vector3 dir = p - _center;
            float dl = dir.magnitude;
            if (dl < 1e-4f) continue;
            dir /= dl;
            Vector3 rayStart = _center + dir * _rayStartRadius;
            if (!Physics.Raycast(rayStart, -dir, out var hit, _rayLength, _groundMask))
                continue;

            Vector3 pos = hit.point;
            if (WorldToCell(pos) != cell)
                continue; // hit belongs to a neighbouring cell — it will own that point when it loads

            Vector3 radial = (pos - _center).normalized;
            float dist = (pos - _center).magnitude;
            if (dist < _baseRadius - 1f) continue;
            if (_waterRadius > 0f && dist < _waterRadius + 0.2f) continue;

            float slope = Vector3.Angle(hit.normal, radial);
            float elevationNorm = _planet.GetNormalizedElevationAtPosition(pos);
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
                    AddPooledToChunk(best, go, placePos);
                    if (++_streamInstCount >= maxInstantiatesPerFrame)
                    {
                        _streamInstCount = 0;
                        _streamRayCount = 0;
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

    void OnDisable()
    {
        _ready = false;
    }

    void OnDestroy()
    {
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
        float dist = (pos - center).magnitude;
        float waterRadius = planet.GetWaterRadiusWorld();
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

        bool waterOk = !(excludeUnderwater && waterRadius > 0f && dist < waterRadius + 0.2f);
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
        sb.Append($"  underwater    = dist {dist:F1} vs water {waterRadius:F1} -> {(waterOk ? "OK" : "FAIL")}\n");

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
            bool gates = slopeOk && elevOk && waterOk && m >= 0f;
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
        sb.Append($"  WINNER (highest m, gates passed): {bestName} (m={(bestM < 0f ? 0f : bestM):F2}) -> {(bestM >= 0f && waterOk ? "WOULD SPAWN (probabilistic by keepProb)" : "BLOCKED")}");
        Debug.Log(sb.ToString());
    }
}
