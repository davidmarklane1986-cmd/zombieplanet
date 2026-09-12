# Tree LOD / Impostor Rings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Trees/palms use near full prefabs and mid/far GPU billboards so forests stay full farther out without paying GameObject cost.

**Architecture:** Keep streaming placement points. For non-rock `GameObjectPool` rules, store lightweight placement records; near ring instantiates/activates pooled prefabs; mid/far rings draw crossed-quad impostors via `Graphics.DrawMeshInstanced`. Exclusive rings (never both). Rocks unchanged.

**Tech Stack:** Unity C#, existing `FoliageByColour` streaming + `PlanetHorizonCulling`, `Graphics.DrawMeshInstanced`.

## Global Constraints

- v1 = trees/palms only (rule name does **not** match rock/stone/boulder).
- Rocks keep short `rockDrawDistance`; no impostors.
- Exclusive rings: a placement is either prefab **or** billboard, never both.
- Screen-circle / `FoliageOccluder` only on near prefabs.
- Soft sticky hill LoS reused; no portal / baked occlusion work.
- Defaults: near 120, mid 400, far 700, far density 0.5, near hysteresis 12.
- Do not commit unless the user asks.

---

### Task 1: Impostor draw helper (mesh + batch API)

**Files:**
- Create: `Assets/Stargrave/Scripts/FoliageTreeImpostorDraw.cs`

**Interfaces:**
- Produces:
  - `FoliageTreeImpostorDraw.EnsureReady()` — builds shared cross-quad mesh + material once
  - `FoliageTreeImpostorDraw.ClearBatches()`
  - `FoliageTreeImpostorDraw.Add(Matrix4x4 matrix)`
  - `FoliageTreeImpostorDraw.Flush(int layer)` — `Graphics.DrawMeshInstanced` in batches of ≤1023
  - `FoliageTreeImpostorDraw.MakeMatrix(Vector3 pos, Vector3 up, float yawRad, float height, float widthScale)` 

- [ ] **Step 1: Create `FoliageTreeImpostorDraw`**

```csharp
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Shared GPU cross-billboard batches for mid/far tree impostors.
/// </summary>
public static class FoliageTreeImpostorDraw
{
    const int BatchSize = 1023;
    static Mesh _mesh;
    static Material _material;
    static readonly List<Matrix4x4> _matrices = new List<Matrix4x4>(1024);
    static readonly Matrix4x4[] _batch = new Matrix4x4[BatchSize];

    public static void EnsureReady()
    {
        if (_mesh == null)
            _mesh = BuildCrossQuadMesh();
        if (_material == null)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null)
                sh = Shader.Find("Unlit/Color");
            _material = new Material(sh);
            _material.color = new Color(0.18f, 0.38f, 0.12f, 1f);
            _material.enableInstancing = true;
        }
    }

    public static void ClearBatches() => _matrices.Clear();

    public static void Add(Matrix4x4 matrix) => _matrices.Add(matrix);

    public static Matrix4x4 MakeMatrix(Vector3 pos, Vector3 up, float yawRad, float height, float widthScale)
    {
        if (up.sqrMagnitude < 1e-8f)
            up = Vector3.up;
        up.Normalize();
        Quaternion yaw = Quaternion.AngleAxis(yawRad * Mathf.Rad2Deg, up);
        // Pivot at ground: mesh is authored 0..1 on local Y.
        Vector3 scale = new Vector3(Mathf.Max(0.05f, height * widthScale), Mathf.Max(0.05f, height), Mathf.Max(0.05f, height * widthScale));
        return Matrix4x4.TRS(pos, yaw * Quaternion.FromToRotation(Vector3.up, up), scale);
    }

    public static void Flush(int layer)
    {
        EnsureReady();
        if (_mesh == null || _material == null || _matrices.Count == 0)
            return;
        int i = 0;
        while (i < _matrices.Count)
        {
            int n = Mathf.Min(BatchSize, _matrices.Count - i);
            for (int k = 0; k < n; k++)
                _batch[k] = _matrices[i + k];
            Graphics.DrawMeshInstanced(
                _mesh, 0, _material, _batch, n, null,
                ShadowCastingMode.Off, false, layer);
            i += n;
        }
        _matrices.Clear();
    }

    static Mesh BuildCrossQuadMesh()
    {
        // Two vertical quads crossing on Y, bottom at y=0, top at y=1, width [-0.5,0.5].
        var mesh = new Mesh { name = "TreeImpostorCross" };
        var v = new Vector3[]
        {
            new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(0.5f, 1f, 0f), new Vector3(-0.5f, 1f, 0f),
            new Vector3(0f, 0f, -0.5f), new Vector3(0f, 0f, 0.5f), new Vector3(0f, 1f, 0.5f), new Vector3(0f, 1f, -0.5f),
        };
        var n = new Vector3[]
        {
            Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward,
            Vector3.right, Vector3.right, Vector3.right, Vector3.right,
        };
        var uv = new Vector2[]
        {
            new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1),
            new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1),
        };
        var t = new int[]
        {
            0,2,1, 0,3,2, 0,1,2, 0,2,3, // double-sided A
            4,6,5, 4,7,6, 4,5,6, 4,6,7, // double-sided B
        };
        mesh.vertices = v;
        mesh.normals = n;
        mesh.uv = uv;
        mesh.triangles = t;
        mesh.RecalculateBounds();
        return mesh;
    }
}
```

- [ ] **Step 2: Confirm script compiles in Unity**

Wait for domain reload; console should have no errors referencing `FoliageTreeImpostorDraw`.

---

### Task 2: Placement records + inspector knobs on `FoliageByColour`

**Files:**
- Modify: `Assets/Stargrave/Scripts/FoliageByColour.cs`
- Modify: `Assets/Scenes/SampleScene.unity` (serialized defaults on the foliage component)

**Interfaces:**
- Produces on `FoliageByColour`:
  - `bool treeImpostorsEnabled = true`
  - `float treeNearDistance = 120f`
  - `float treeMidDistance = 400f`
  - `float treeFarDistance = 700f`
  - `float treeFarDensity = 0.5f`
  - `float treeNearHysteresis = 12f`
- Produces per tree placement (store on chunk or parallel list):
  - `struct TreePlacement { Vector3 pos; Vector3 up; float yaw; float height; float widthScale; int farHash; GameObject go; bool nearActive; }`

- [ ] **Step 1: Add inspector fields under Culling / LOD**

Place after `rockDrawDistance`:

```csharp
[Header("Tree impostor rings (v1)")]
[Tooltip("When ON, trees/palms use near prefabs + mid/far GPU billboards. Rocks unchanged.")]
public bool treeImpostorsEnabled = true;
[Min(10f)] public float treeNearDistance = 120f;
[Min(20f)] public float treeMidDistance = 400f;
[Min(40f)] public float treeFarDistance = 700f;
[Range(0.05f, 1f)] public float treeFarDensity = 0.5f;
[Min(0f)] public float treeNearHysteresis = 12f;
```

- [ ] **Step 2: Add `TreePlacement` + list on `ObjChunk` (or on `RuleRuntime` for tree rules)**

Prefer attaching to each `ObjChunk` so unload stays chunk-local:

```csharp
public struct TreePlacement
{
    public Vector3 pos;
    public Vector3 up;
    public float yaw;
    public float height;
    public float widthScale;
    public int farKey;          // stable hash for far-density thinning
    public GameObject go;       // null until near-instantiated
    public bool nearActive;     // hysteresis state
}

// on ObjChunk:
public readonly List<TreePlacement> treePlacements = new List<TreePlacement>();
```

- [ ] **Step 3: When placing a pooled tree (not rock), record `TreePlacement`**

In every path that currently `Instantiate`s a tree/palm:

1. Compute `height` from prefab renderer bounds (world size at scale 1) × placement scale; fallback `height = 8f * scale`.
2. `widthScale = 0.55f` (tunable constant).
3. `up` = radial / surface up used for the instance.
4. `yaw` = random or from rotation.
5. `farKey = HashPos(pos)` (e.g. `Animator.StringToHash` of quantized cell or `pos.GetHashCode()` stable enough).
6. Add to chunk `treePlacements`.
7. Only parent/activate `go` if currently inside near ring (see Task 3); otherwise keep `go == null` **or** instantiate inactive and leave inactive — prefer **lazy instantiate** on enter-near to save Instantiate budget.

Lazy instantiate is the intended v1 behaviour: mid/far never call `Instantiate`.

- [ ] **Step 4: Patch SampleScene foliage component** with the new defaults (Unity will also deserialize defaults for new fields).

---

### Task 3: Near prefab ring with hysteresis

**Files:**
- Modify: `Assets/Stargrave/Scripts/FoliageByColour.cs` (`CullPooled` / new `CullTreeRings`)

**Interfaces:**
- Consumes: `TreePlacement`, ring distances, `FoliageTreeImpostorDraw`
- Produces: per-frame exclusive near vs billboard decisions

- [ ] **Step 1: Split cull path**

In `Update()`, for pooled rules:

```csharp
if (rt.isRockRule || !treeImpostorsEnabled)
    CullPooled(rt, cull, camPos, planes, rockDd or treeDd, ...);
else
    CullTreeRings(rt, cull, camPos, planes, _planet, planetCenter, surfaceRadius, horizonCull);
```

- [ ] **Step 2: Implement `CullTreeRings`**

Pseudo-logic per placement (chunk-sorted nearest-first still OK):

```text
d = distance(cam, pos)
nearOn  = treeNearDistance
nearOff = treeNearDistance + treeNearHysteresis

if placement.nearActive:
    wantNear = d <= nearOff
else:
    wantNear = d <= nearOn

if wantNear:
    EnsurePrefab(placement)   // Instantiate if null, budget-limited
    if go != null: go.SetActive(true)
    // do NOT billboard this placement
else:
    if go != null: go.SetActive(false)  // or Destroy/pool later; SetActive false is fine for v1
    if d <= treeMidDistance:
        Add billboard
    else if d <= treeFarDistance:
        if (farKey & 0xFFFF) / 65535f < treeFarDensity: Add billboard
    else:
        // beyond: nothing
```

Frustum + sticky LoS: test once per **chunk** (existing pattern); if chunk fails, skip all placements in chunk (no prefab on, no billboards).

- [ ] **Step 3: `EnsurePrefab` respects `maxInstantiatesPerFrame`**

If budget exhausted, leave `go == null` this frame (billboard may still draw if outside near — but if `wantNear` and no go yet, optionally draw billboard as temporary stand-in until instantiate lands). Prefer temporary billboard stand-in so near ring never holes.

---

### Task 4: Draw impostors each frame

**Files:**
- Modify: `Assets/Stargrave/Scripts/FoliageByColour.cs` (`Update`)

- [ ] **Step 1: At start of foliage draw section**

```csharp
if (treeImpostorsEnabled)
    FoliageTreeImpostorDraw.ClearBatches();
```

- [ ] **Step 2: Inside `CullTreeRings`, call `Add(MakeMatrix(...))` for mid/far**

- [ ] **Step 3: After all rules processed**

```csharp
if (treeImpostorsEnabled)
    FoliageTreeImpostorDraw.Flush(_layer);
```

- [ ] **Step 4: Manual Play Mode check**

1. Enter Play, stand in forest.
2. Near trees = full Kenny meshes.
3. Look mid-distance: green cross-cards, not empty.
4. Far: sparser cards out to ~700m.
5. Rocks still drop earlier (~180m).
6. Walk forward: prefabs appear as you approach; no double-draw (mesh + card stacked).

---

### Task 5: Unload / streaming safety

**Files:**
- Modify: `Assets/Stargrave/Scripts/FoliageByColour.cs` (cell unload paths that clear `objChunks`)

- [ ] **Step 1: When unloading a cell/chunk, clear `treePlacements` and destroy/pool any `go`**

Same place that currently destroys pooled objects — also `treePlacements.Clear()`.

- [ ] **Step 2: Confirm streaming reload recreates placements without leaking GameObjects**

Play Mode: sprint outward then return; near trees repopulate; no runaway hierarchy growth under foliage pool containers.

---

## Spec coverage

| Spec item | Task |
|-----------|------|
| Near prefab / mid billboard / far sparse | 3–4 |
| Exclusive rings | 3 |
| Shared cross-quad helper | 1 |
| Knobs + toggle | 2 |
| Rocks unchanged | 3 (`isRockRule` path) |
| Streaming keeps points / lazy near instantiate | 2–3, 5 |
| Soft LoS / frustum | 3 |
| Screen-circle near only | 3 (impostors skip `FoliageOccluder`) |

## Self-review notes

- No BRG / LODGroup / rock impostors in this plan (v2).
- Commit steps omitted (repo rule: commit only when user asks).
