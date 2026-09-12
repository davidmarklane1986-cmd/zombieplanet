# Tree LOD / Impostor Rings — Design

**Date:** 2026-09-11  
**Status:** Approved  
**Goal:** Smooth near FPS **and** fuller far forests, with a small v1 (trees/palms only; rocks later).

## Problem

Pooled Kenny tree prefabs are expensive at distance (GameObjects, renderers, culling toggles). Grass is already GPU-instanced; trees are not. Draw-distance alone either:

- cuts forests too early (empty mid-range), or  
- keeps full meshes too far (FPS cost).

Hill LoS stays separate; this design is about **representation rings**, not portals or Unity baked occlusion.

## Decision

**Approach 1 — Distance swap: full prefab → GPU billboard**

| Ring | Default distance | Representation |
|------|------------------|----------------|
| Near | 0–120 m | Current pooled GameObject prefab (colliders, screen-circle fade) |
| Mid | 120–400 m | GPU-instanced cross-billboard (or single card) at same world points |
| Far | 400–700 m | Sparse GPU billboards only (optional density thin ~50%) |
| Beyond | > far | Not drawn; stream unload as today |

**Out of v1:** rocks, Unity `LODGroup` authoring on Kenny prefabs, BRG / full GPU-driven rebuild, portal/baked occlusion.

## Design

### Placement vs render

- Streaming / scatter still produces **placement points** (position, scale, yaw, rule id) for tree/palm rules.
- **Near ring:** instantiate / keep pooled prefab as today (`GameObjectPool`).
- **Mid/far rings:** do **not** require an active prefab; draw from a lightweight instance buffer via `Graphics.DrawMeshInstanced` (same pattern as grass batches).
- When a point crosses near↔mid: activate or release the pooled prefab; billboard draw includes mid/far only.

### Billboard asset (minimal)

- One shared mesh: crossed quads (2 planes) or one camera-facing card if cheaper to start.
- One unlit/simple lit material; tint optional per-rule later.
- Approximate scale from placement scale × prefab height estimate (constant or baked per prefab later).
- No colliders on billboards.

### Culling

- Reuse existing frustum + soft `PlanetHorizonCulling` (sticky) on **chunk centers** / instance batches.
- Near prefabs: existing `CullPooled` + screen-circle `FoliageOccluder`.
- Mid/far billboards: distance rings + frustum + limb/LoS; no screen-circle.
- Rocks: unchanged short `rockDrawDistance` in v1.

### Streaming interaction

- Keep nearest-first cell streaming.
- Prefer storing placement samples in the cell even when only billboards are needed, so walking outward does not re-scatter.
- Instantiates still budget-capped (`maxInstantiatesPerFrame`); mid/far fill should not wait on Instantiate.

### Tuning knobs (on `FoliageByColour` or a small helper)

- `treeNearDistance` (default 120)
- `treeMidDistance` (default 400) — aligns with current tree draw distance
- `treeFarDistance` (default 700)
- `treeFarDensity` (default 0.5)
- Feature toggle to disable impostors (full prefab only, current behaviour)

### Success criteria

- Walking in dense forest: fewer active tree GameObjects in mid/far; FPS smoother or equal with denser far silhouette.
- Cresting a ridge: mid/far billboards appear without waiting on Instantiate budget.
- Near trees still block / fade for screen-circle and feel solid.
- Rocks and grass behaviour unchanged in v1.

### Non-goals

- Perfect photoreal far trees.
- Per-Kenny-prefab hand-authored LOD meshes in v1.
- Changing building draw distance / faction cullers (already separate).

## Files (expected)

- `Assets/Stargrave/Scripts/FoliageByColour.cs` — ring selection, when to pool vs billboard
- New small helper e.g. `Assets/Stargrave/Scripts/FoliageTreeImpostorDraw.cs` — mesh/material + batch draw
- Optional: simple mesh/material under `Assets/Stargrave/Art/Foliage/` or runtime-built mesh
- Scene / inspector defaults on existing `FoliageByColour` component

## Risks

| Risk | Mitigation |
|------|------------|
| Billboard pop at near boundary | Hysteresis (~10–15 m) and/or short crossfade scale |
| Wrong scale / floating cards | Bake approx height from prefab bounds at runtime init |
| Extra draw calls | One batch set per frame (or per rule), nearest-first budget |
| Double-draw near+mid | Exclusive rings: point is either prefab **or** billboard |

## Later (v2+)

- Rocks on same ring path with shorter mid/far.
- Per-prefab billboard textures / atlas.
- Optional Unity LODGroup as near-ring detail only.
