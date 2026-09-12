# Autonomous BAR / Zero-K Style War — Design

Approved direction 2026-09-11: **Approach A** — evolve current `Rts2` into a spectator BAR/Zero-K-like planet war. No player UI; no direct player control in this milestone.

## Goals
- Autonomous factions wage a continuous large-scale war on the planet.
- Economy and production feel like BAR/ZK: extractors → energy → factory → unit spam → expand with constructors.
- Player is a spectator (orbit / fly around). No RTS UI, no selection, no orders.
- Reuse planet nav, instanced units, faction soft survival, and founding stagger.

## Non-goals (this milestone)
- Player-controlled commander, build menus, or hotkeys.
- Full ZK unit catalogue / tech tree (Cloaky, Strider hub, etc.).
- Reclaim wrecks as a deep system (optional later).
- True metal map overlays / mex spot UI (full paint). **Metal pockets + debug markers are in** — see `2026-09-11-territory-pockets-borders-design.md`.
- Raising the hard unit ceiling above what 60 FPS can hold (raise gradually; target higher later).

## Fantasy mapping (village → BAR/ZK)

| Village (current) | BAR/ZK analogue (new) |
|---|---|
| Wood / stone gatherers | Metal extractors (mex) + energy generators |
| Town Hall | Commander site / HQ (still the faction anchor) |
| Barracks queue | Factory continuous production |
| Workers build campus ring | Constructors build mex, energy, factories, defences |
| Roamers founding | Initial constructors / commander expand |
| Soft relocate on wipe | Keep (immortal factions) |

Wood/stone can remain the *backing currencies* at first (mex income ticks wood/metal; energy gens tick a new Energy pool), or rename display-only later. Internal sim should use **Metal** + **Energy** names going forward.

## First milestone loop (per faction)
1. **Found** (existing meet / force-found) → HQ pad.
2. Seed **constructors** (not village gatherers as the main eco).
3. Constructors build: **Mex** → **Energy** → **Lab/Factory**.
4. Factory **continuously** queues raiders while metal/energy allow.
5. Excess constructors expand: more mex/energy **onto metal pockets**, then market support / second eco.
6. Combat AI: raid nearest weaker neighbour; defend HQ; soft-relocate if HQ dies.
7. Territory: influence grows until borders stall; merchants prefer owned corridors.

Merchant / noble / claimable towns: **enabled in BAR** (Market after Factory; nobles reclaim with cooldown + ~50% ownership cap; merchants run Market↔Market plus owned-town bonus). Mint remains optional/parked.

## Buildings (minimum set)
- `Hq` (reuse Town Hall mesh/pad for now)
- `Mex` — passive metal income while alive
- `Energy` — passive energy income while alive
- `Factory` — continuous unit production (raiders first)
- Optional later: `Turret`, `Radar`, second factory tier

## Units (minimum set)
- `Constructor` — builds structures, no heavy combat
- `Raider` — cheap spam, early pressure (maps from Infantry)
- `Assault` — slower/stronger (maps from a heavier infantry profile later)
- Keep Archer parked or remap as skirmisher later

## AI brain (spectator)
Replace village “build order + barracks queue” with a simple priority stack each faction tick:
1. If no HQ → recovering / relocate (existing soft immortality)
2. If metal stalled → build mex
3. If energy stalled → build energy
4. If no factory → build factory
5. If factory idle and resources OK → produce raiders
6. If safe eco and constructor count low → build/train constructor
7. If army past threshold → assault nearest rival HQ

No UI events; verbose logs optional.

## Performance
- Keep instanced draw + queued pathfinding.
- Soft unit cap remains (~500) until profiling allows higher.
- Factories rate-limit production so all factions don’t spawn every frame.
- Mex/energy are cheap tick income (no per-frame raycasts).

## Cutover
- New building kinds + income tick beside existing `BuildingSystem`.
- Faction director gains a `BarDirector` (or mode flag `economyMode = BarZk`) so village loop can stay compilable but unused.
- Scene/default: enable BAR mode on `FactionSimulation`.
- Player interact founding can stay but is not required (force-found + meet still work).

## Success criteria
- Start Play Mode, watch 4–8 factions: within a few minutes each has mex/energy/factory and visible unit streams.
- Fights break out without player input.
- Wiped faction relocates and rebuilds (constructors reseed).
- No RTS UI required.

## Follow-ups (later milestones)
- Higher unit caps / LOD
- Tech 2 factories / air
- Reclaim
- Front-line behaviours (skirmish lines, raiding mex behind lines)
- Optional later: lightweight spectator overlays (not control UI)
