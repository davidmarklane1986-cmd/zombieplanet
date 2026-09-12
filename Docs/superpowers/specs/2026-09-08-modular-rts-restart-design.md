# Modular RTS Restart (Rts2) — Design

Approved for implementation 2026-09-08.

## Goals
- Modular, scalable always-on RTS on the planet.
- Bases, training, battles, trade, town capture.
- Scene-visible sim (not proximity-gated).
- Gather from logical tree/rock sites even if foliage is not streamed.
- Soft survival: relocate/rebuild; no hard faction wipe.

## Defaults
- Hybrid nav: coarse global dry graph + dense patches near bases/towns.
- Instanced silhouette units (≤512); character LOD later.
- Path requests queued (cap per frame); unit logic ~18 Hz; draw every frame.
- Target ~60 FPS with 8 factions / ~500 units.

## Layers
1. `PlanetNavGraph` + `PlanetNavBaker` + `PathService` (A* waypoints).
2. `LogicalResourceMap` + baker (wood/stone sites from biome rules).
3. `Rts2UnitSim` — buffers, orders, waypoint follow, Jobs separation, instanced draw.
4. Faction director (existing `FactionController`) issues orders into Rts2.
5. Buildings train into Rts2 only.

## Soft survival
- On TH loss: Relocate strategy picks safe dry site, rebuilds TH, recalls units.
- Assault only inside fairness strength band.
- Never delete last faction permanently.

## Cutover
- New code under `Assets/Stargrave/Scripts/Factions/Rts2/`.
- Boot: bake nav + resources after planet ready, then factions, then dense nav rebuild.
- Legacy free-steer swarm (`Factions/Swarm/`) removed; Rts2 is the only unit sim path.
- Soft survival: TH loss → `BeginRelocateAndRebuild` (new dry campus + seed stock + recall).
- Shared `RtsUnitRole` enum kept at `Factions/RtsUnitRole.cs` for building/brain APIs.

## Status
Implemented and cut over 2026-09-08 on branch `RTS`. Legacy swarm deleted.
