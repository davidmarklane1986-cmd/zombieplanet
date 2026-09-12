# Faction soft immortality & founding stagger — Design

Approved 2026-09-09.

## Goals
- Factions cannot soft-lock when all buildings and NPCs are gone; workers must be able to rebuild.
- Forced first-campus founding waits **3–5 minutes** (random per faction) instead of a flat 10 minutes, so campuses do not all appear at once.

## Soft immortality
- When a **founded** faction has **0 living workers**, after a short delay seed free workers at the current campus pad (`startingWorkers`, capped by `workerMaxCount`).
- Seed enough wood/stone for a Town Hall if stock is empty.
- Enter / stay in `Recovering`; workers use normal gather + construction.
- Also seed immediately after `BeginRelocateAndRebuild` if the faction has no workers left.
- No ghost buildings.

## Forced founding stagger
- Replace flat `foundingForceAfterSeconds` (600) with per-faction random deadline in **[180, 300]** seconds from faction start while unfounded.
- Keep rival separation checks; retries on later ticks if no legal site yet.

## Non-goals
- Preventing combat losses of individual units/buildings.
- Ghost Town Halls or invulnerable bases.
