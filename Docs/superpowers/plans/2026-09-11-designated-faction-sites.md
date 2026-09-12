# Designated Faction Sites Implementation Plan

> **For agentic workers:** implement task-by-task from the approved spec `Docs/superpowers/specs/2026-09-11-designated-faction-sites-design.md`.

**Goal:** Each faction keeps a separated boot pad; scattered workers march there; first arrival founds.

**Architecture:** Reuse `SpawnAxis` packing; park meet-founding; march + claim radius in `Rts2UnitSim`; force-found prefers designated pad.

**Tech Stack:** Unity C#, existing FactionSimulation / Rts2UnitSim.

## Global Constraints

- First arrival founds (approved).
- No beacon meshes in this pass.
- Soft relocate unchanged.

---

### Task 1: Economy flags + longer BAR force-found window

**Files:** `Assets/Stargrave/Scripts/Factions/FactionTypes.cs`

- [x] `useDesignatedSiteFounding`, `designatedSiteClaimRadius`
- [x] BAR force-found 150–240s for travel time

### Task 2: March + claim in Rts2

**Files:** `Assets/Stargrave/Scripts/Factions/Rts2/Rts2UnitSim.cs`

- [x] Spawn roamers with Move toward `GetSafePosition()`
- [x] `TickMarchToDesignatedSite` + park `CheckRoamingWorkerMeetings`
- [x] Activate after founding treats Move orders

### Task 3: FoundCampus / force-found stay on pad

**Files:** `Assets/Stargrave/Scripts/Factions/FactionController.cs`

- [x] Trust designated `SpawnAxis` (no mainland hop collapse)
- [x] Force-found tries designated site first

### Task 4: Manual verify

- [ ] Play Mode: multiple factions converge on distinct pads and found independently
