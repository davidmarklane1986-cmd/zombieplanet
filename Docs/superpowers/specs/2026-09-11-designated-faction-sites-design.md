# Designated Faction Sites — Design

Approved 2026-09-11: **Approach 1** — assigned separated pads; workers scatter then march to site; first arrival founds.

## Goals
- Each faction receives one **designated campus site** at boot, well separated from other factions.
- Initial workers still **spawn scattered** across dry land (not stacked on the pad).
- Workers **travel to their site**; the **first arrival** founds the campus and starts HQ / Town Hall construction.
- Spectator-readable BAR/village loop: clear home bases, no meet-clump founding.

## Non-goals
- Visible beacon meshes / claim markers (optional follow-up).
- Multiple sites per faction or player-chosen pads.
- Changing soft immortality / relocate after HQ loss (keep existing relocate search).

## Boot
1. Pack N dry mainland axes with existing separation (`minimumFactionSeparation` / floor) — same packing used today for `SpawnAxis`.
2. Assign one axis per faction as **DesignatedSiteAxis** / `SpawnAxis` before workers spawn.
3. Sites must remain mutually separated; a faction does not re-roll its site unless soft-relocate after wipe.

## Worker spawn
1. Scatter `startingWorkers` with maximin dry-at-sample placement (no ocean→coast snap collapse).
2. Immediately give each roamer a **Move-to-designated-site** order (path to site surface point).
3. Do **not** use free roam + same-faction meet as the primary founding trigger while this mode is active.

## Founding rule
1. When any living worker of faction F enters **site claim radius** of F’s designated site, call `FoundCampus` at that site.
2. First successful claim wins; subsequent arrivals join the founded campus (gather / build).
3. Start Town Hall / HQ construction via existing post-found construction path.
4. Park `CheckRoamingWorkerMeetings` founding while designated-site mode is on.
5. Keep a **long** force-found fallback only if no worker reaches the site (unreachable / stuck), using the **same** designated axis when possible rather than a random new continent.

## Combat / BAR interaction
- Unfounded factions remain non-war-ready (existing grace / war-ready gates).
- Designated sites do not grant combat; only founding + buildings do.

## Success criteria
- Play Mode: each faction’s workers are visibly spread, then converge on distinct distant pads.
- Pads are not clustered in one region when packing succeeds.
- First worker at a pad founds; construction begins without player input or worker-meet.
- Rival pads stay outside founding min separation.

## Follow-ups
- Optional site beacon / pad decal for spectators.
- Tune claim radius and path retries if workers strand on cliffs.
