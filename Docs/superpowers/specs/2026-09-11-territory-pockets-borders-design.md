# Territory Battles (Pockets + Borders + Trade) — Design

Approved direction 2026-09-11: layered territory for BAR spectator war. **V1 = gameplay loop first** (markers / debug tint only).

## Goals
- Factions race to claim **resource-rich metal pockets** with expansion mexes.
- **Influence** grows from HQ, claimed towns, and owned pockets until rival fronts **stall** (contested borders).
- Coarse **nav-cell ownership** drives merchant route bonuses/penalties so owning land between markets matters.
- All three layers exist; pretty territory mesh/shader is deferred.

## Non-goals (v1)
- Full territory overlay shader / Risk-map paint UI.
- Player paint-the-map controls.
- Per-cell combat resolution.
- Deep energy-pocket economy (metal pockets + influence enough).

## Layers

### A — Resource pockets
- Baked at Rts2 start (`TerritorySystem.BakePockets`) with separation and richness tiers.
- Opening campus mexes still ring the HQ.
- After Factory + Market, BAR director places mexes on nearest **unowned** pocket.
- Completing a mex within claim radius owns the pocket; destroying it clears ownership (reclaim cooldown).
- Owned pockets add bonus metal income.

### B — Influence growth
- Seeds: HQ / safe position, owned towns, owned pockets.
- Per-faction radius grows over time (aggression personality biases rate), capped.
- Influence at a point = falloff from seeds inside radius.

### C — Painted cells (gameplay)
- Sample `PlanetNavGraph` nodes on an interval; owner = highest influence, or **contested** when top two are within epsilon (border stall).
- Debug markers: pocket spheres + sparse border/contested nodes tinted by faction color.

### Trade
- Merchant payout scales with owned-cell fraction along the corridor.
- Move speed drops on hostile/contested majority.
- Destination pick prefers rival markets with friendlier owned corridors.

## Key types
- `TerritorySystem` — pockets, influence, cells, queries, markers
- `BarFactionDirector` — pocket mex expansion + pocket income
- Economy knobs under `FactionEconomySettings` (`territory*`)
- `FactionPersonality.InfluenceGrowthMul` / `PocketGreed`

## Success criteria
- After Factory, factions push mexes toward distant rich pockets.
- Two expanding factions show contested markers where influence meets.
- Destroy pocket mex → ownership lost; rival can take it after cooldown.
- Merchants earn more on owned corridors than contested/hostile ones.
- Opening campus eco still works before pocket expansion.
