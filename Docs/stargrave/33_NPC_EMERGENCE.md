# NPC Emergence (Loiter-Triggered NPCs)

## Goal
Replace static quest hubs with natural-feeling NPC arrivals.

## Loiter Trigger
If the player stays in an area for ~X time (design-configurable):
- Spawn an NPC off-camera at distance.
- NPC approaches the player.
- NPC stops nearby and becomes interactable.

## Role Progression
- Original NPC provides relic missions.
- Each completed mission generates another.
- After ~5 missions:
  - A second NPC arrives and offers vault missions
  - Vault missions have a cooldown between offers

## Despawn Rules
NPCs despawn after inactivity:
- If no interaction for ~10 minutes, NPC leaves and despawns off-screen.
- Loiter-trigger spawns cannot occur too close to existing loiter-NPCs (min distance rule).

## Optional Hex/Region Triggering
A planet may be conceptually divided into large cells/regions (e.g., hex-style) for spawn logic:
- Remaining in a cell can trigger NPC emergence.
- Prevents spam and supports travel-based discovery.
