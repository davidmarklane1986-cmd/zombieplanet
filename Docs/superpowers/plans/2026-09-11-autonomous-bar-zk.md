# Autonomous BAR/ZK First Milestone — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Spectator-only BAR/Zero-K-style autonomous war on the existing Rts2 planet sim (mex → energy → factory → raider spam → expand/attack).

**Architecture:** Add a `BarEconomyMode` flag on `FactionSimulation`. When on, factions use Metal/Energy income from Mex/Energy buildings, Constructors build those + Factory, Factory continuously produces Raiders, and a thin BAR director replaces village build-order/merchant/noble priority. Soft immortality and founding stay.

**Tech Stack:** Unity C#, existing `FactionController` / `BuildingSystem` / `Rts2UnitSim`, SampleScene.

## Global Constraints

- No player RTS UI or direct control in this milestone.
- Keep soft wipe/rebuild and founding stagger.
- Park merchant / noble / mint / claimable-town priority when BAR mode is on.
- Prefer smallest surface changes; reuse Town Hall as HQ pad/mesh.
- Unit cap stays ~500 until profiled otherwise.
- Do not commit unless the user asks.

---

### Task 1: Metal / Energy economy fields

**Files:**
- Modify: `Assets/Stargrave/Scripts/Factions/FactionTypes.cs`
- Modify: `Assets/Stargrave/Scripts/Factions/FactionController.cs` (Metal/Energy props + spend helpers)
- Modify: `Assets/Stargrave/Scripts/Factions/FactionSimulation.cs` (BAR mode flag + BAR economy defaults)

**Interfaces:**
- Produces: `FactionController.Metal`, `Energy`, `AddMetal`, `AddEnergy`, `TrySpendBar(int metal, int energy)`
- Produces: `FactionSimulation.useBarEconomyMode` (bool, default true for this milestone)

- [ ] **Step 1:** Add `Metal`/`Energy` (and starting/cap rates) to economy settings; alias Metal↔Wood spend only if needed for transition—prefer parallel Metal/Energy fields.
- [ ] **Step 2:** Wire `FactionController` accessors and `TrySpendBar`.
- [ ] **Step 3:** Add `useBarEconomyMode` on `FactionSimulation`; when true, seed starting metal/energy on init.
- [ ] **Step 4:** Play-mode / compile check: enter Play, no errors; factions still spawn.

---

### Task 2: BAR building kinds (Mex, Energy, Factory)

**Files:**
- Modify: `Assets/Stargrave/Scripts/Factions/BuildingSystem.cs` (`BuildingKind`, visual stubs, create helpers)
- Modify: `Assets/Stargrave/Scripts/Factions/FactionController.cs` (lists/refs for Mex/Energy/Factory, notify built/destroyed)
- Modify: `Assets/Stargrave/Scripts/Factions/FactionTypes.cs` (build costs/seconds, income rates)

**Interfaces:**
- Produces: `BuildingKind.Mex`, `EnergyGen`, `Factory`
- Produces: `FactionController.MexCount`, `EnergyGenCount`, `Factory` (primary), income tick

- [ ] **Step 1:** Extend `BuildingKind` + construction factory switch (primitive visuals OK).
- [ ] **Step 2:** Costs/income in economy settings (`mexMetalPerSecond`, `energyPerSecond`, build costs in metal/energy).
- [ ] **Step 3:** On operational: register with faction; SimulationTick adds income from live mex/energy.
- [ ] **Step 4:** Verify: manually/log after founding that BAR director can enqueue Mex construction (next task may be required for auto).

---

### Task 3: Constructor role + build orders

**Files:**
- Modify: `Assets/Stargrave/Scripts/Factions/Rts2/Rts2Types.cs` (`Rts2Role.Constructor`)
- Modify: `Assets/Stargrave/Scripts/Factions/RtsUnitRole.cs` if needed for training API
- Modify: `Assets/Stargrave/Scripts/Factions/Rts2/Rts2UnitSim.cs` (`TickConstructor`: move to site, contribute build)
- Modify: `Assets/Stargrave/Scripts/Factions/FactionController.cs` (seed constructors on found / wipe revive instead of pure gatherers when BAR mode)

**Interfaces:**
- Consumes: construction sites from Task 2
- Produces: constructors that advance `BuildingConstructionSite` when near

- [ ] **Step 1:** Add Constructor role; map speed/HP; draw colour distinct from raider.
- [ ] **Step 2:** On BAR founding / soft revive, spawn constructors (reuse seed path).
- [ ] **Step 3:** Tick: if faction has active construction, path to it and feed build progress (metal already reserved by director).
- [ ] **Step 4:** Verify: constructors walk to first Mex site and complete it.

---

### Task 4: Factory continuous production

**Files:**
- Modify: `Assets/Stargrave/Scripts/Factions/BuildingSystem.cs` (Factory component continuous queue)
- Modify: `Assets/Stargrave/Scripts/Factions/FactionTypes.cs` (raider metal/energy cost, build time)

**Interfaces:**
- Produces: `Factory.TickProduction` spawns `Rts2Role.Infantry` (Raider) via `Rts2UnitSim.TrySpawn` when `TrySpendBar` succeeds

- [ ] **Step 1:** `Factory : Building` with continuous timer (no barracks multi-kind queue).
- [ ] **Step 2:** Spend metal+energy; spawn raider near factory; assign assault/idle via faction orders.
- [ ] **Step 3:** Call from `FactionController.SimulationTick` when BAR mode and factory operational.
- [ ] **Step 4:** Verify: after factory up, unit count climbs without player input.

---

### Task 5: BAR director (replace village strategy when mode on)

**Files:**
- Create: `Assets/Stargrave/Scripts/Factions/Bar/BarFactionDirector.cs` (or methods on controller)
- Modify: `Assets/Stargrave/Scripts/Factions/FactionController.cs` (`TickStrategy` / `TryStartNextConstruction` branch)
- Modify: `Assets/Stargrave/Scripts/Factions/FactionController.cs` (park noble/merchant/claim when BAR)

**Interfaces:**
- Consumes: Metal/Energy, building counts, Worker/Constructor counts
- Produces: construction intents in order Mex → Energy → Factory → more Mex/Energy; assault when raider count ≥ threshold

Priority stack:
1. Recovering / no HQ → existing soft path
2. No mex → build mex near HQ
3. No energy → build energy
4. No factory → build factory
5. Factory exists → (production is automatic)
6. Constructors &lt; target → train/spawn constructor (from factory or seed)
7. Raiders ≥ threshold → assault nearest rival HQ (existing attack plumbing)

- [ ] **Step 1:** Gate village `TryStartNextConstruction` / claim / merchant behind `!useBarEconomyMode`.
- [ ] **Step 2:** Implement BAR construction chooser + placement near HQ (reuse pad distance helpers).
- [ ] **Step 3:** Hook assault threshold to existing `BeginAttack`.
- [ ] **Step 4:** Play Mode 3–5 min: factions build chain and fight autonomously.

---

### Task 6: Scene defaults + soft survival alignment

**Files:**
- Modify: `Assets/Scenes/SampleScene.unity` (`useBarEconomyMode: 1`, BAR starting resources)
- Modify: soft revive to seed constructors + metal/energy in BAR mode
- Modify: force-found / meet founding still works (no UI)

- [ ] **Step 1:** Enable BAR mode in scene FactionSimulation.
- [ ] **Step 2:** Wipe revive seeds constructors + bar stock (not only wood/stone gatherers).
- [ ] **Step 3:** Spectator smoke test: no console spam/errors; war runs alone.

---

## Done when
- Play Mode, no UI interaction: factions found → mex/energy/factory → raider streams → attacks.
- Wiped faction rebuilds with constructors.
- Village merchant/noble paths idle while BAR mode is on.
