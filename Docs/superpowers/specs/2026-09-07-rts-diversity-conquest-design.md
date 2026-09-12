# RTS Diversity & Town Conquest — Design

Approved 2026-09-07.

## Goals
- More unit diversity: Infantry + Archer (replace single Soldier profile).
- Clear command chain: faction strategy → buildings execute → NPCs receive orders.
- New building: Mint converts wood/stone into gold.
- New NPC: Noble (Tribal Wars–lite) claims neutral map towns via loyalty drain.
- Factions compete for town ownership; owned towns grant passive wood/stone/gold trickle.

## Non-goals
- Cavalry or additional soldier classes.
- Training/production at claimed towns (no second full bases).
- ScriptableObject unit catalogs.
- Player-controlled claiming UI.

## Command chain
Each faction `SimulationTick`:
1. **Faction** — strategy (defend owned towns, finish build order, claim, army/war/economy).
2. **Buildings** — construction feed, Mint conversion, Barracks production.
3. **NPCs** — issue orders (escort noble, garrison town, assault/claim); micro AI handles pathing/combat.

Perception (deaths, loyalty, stockpiles) feeds the next strategy pick.

## Mint
- Build order: Town Hall → Barracks → Market → Mint.
- Each tick: if wood/stone above reserves, convert a batch into gold (tunable rates/cap).
- Gold supports merchants and nobles (no longer trade-only).

## Soldiers
- Roles: `Infantry`, `Archer` (workers/merchants unchanged).
- Infantry ≈ current soldier stats; Archer: lower HP/dmg, longer shoot range.
- Barracks targets ~2 infantry : 1 archer; both count toward warfare muster/strength.

## Neutral towns
- Director spawns several `ClaimableTown` sites on mainland, separate from home bases.
- Loyalty starts at 100; recovers slowly when uncontested.
- Owned towns pay passive wood/stone/gold to the owner each faction tick.

## Nobles
- Trained at Barracks; cost wood + stone + gold; low cap (1–2).
- Weak combat; escorted when claiming.
- In town radius: drain loyalty; at 0 ownership flips and loyalty resets.
- Killing/interrupting the noble stops the drain.

## Approach
Extend the existing faction layer (`FactionSimulation` / `FactionController` / `BuildingSystem` / NPC pipeline). No parallel conquest subsystem.
