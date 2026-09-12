# Simple Worker Job Loop — Design

Approved 2026-09-08 (user: “lets do it”).

## Goal
Workers use one clear loop: leave home → harvest (local) → return → deposit → next job.

## Non-goals
- Lane bias / traffic bands
- Deposit teleport assists / steep-home staging
- Far-side prospect scouting
- Complex multi-tier stuck recovery

## Loop
1. **Deposit** if carrying: goal = town-hall surface; arrive within ~18m; deposit; clear cargo.
2. **Gather** otherwise: claim nearest free resource within ~140m of home; walk there; harvest until full.
3. **Idle** at home if no local job (stream foliage there).

## Pathing
- Steer straight toward goal axis.
- Only water blocks; one shore waypoint if the chord crosses water.
- Light stuck nudge: if no progress ~3s (or 1s in water), slerp toward goal.
- Seat on terrain mesh each tick (radius only; axis from steering).

## Rendering
- `RenderMeshInstanced` with planet-scale `worldBounds` so units stay visible away from the hall.
