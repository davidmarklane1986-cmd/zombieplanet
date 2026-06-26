# Build World Once (BWO)

## Goal
The universe generates once, becomes stable, and is safe to reference forever.

## Core Rules
- Universe layout is deterministic and persisted after first generation.
- No surprise regeneration between sessions.
- “Fixed” locations remain fixed.

## Persisted Data (Minimum)
- Star positions + IDs
- Planet seeds + IDs
- Biome regions + parameters
- Fixed locations:
  - Vaults
  - True relic sites
  - Libraries
  - Major settlements / anchor POIs

## Runtime Interaction
- Single-player pacing may “pop in” story-critical *duplicates* (shadow copies) near the player **without** changing canonical fixed locations.
- Canonical fixed locations always remain the same and exist for MMO consistency.
