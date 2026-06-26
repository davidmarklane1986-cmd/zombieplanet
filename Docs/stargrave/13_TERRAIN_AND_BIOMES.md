# Terrain & Biomes

## Regional Terrain System
- Each planet is divided into **large regions** that define terrain identity.
- Regions are logical (not UI zones) and are used for:
  - Noise profiles
  - Biome identity
  - Content distribution rules

Each region can define:
- Noise type(s) and blend weights
- Noise scale, octaves, lacunarity, persistence
- Height bias / ridges / erosion-like shaping (as needed)
- Ground material blend targets

## Biome Examples
- Smooth rolling plains
- Jagged mountain belts
- Dense forests
- Volcanic badlands
- Ocean floor / seabed-style terrain foundation

## Transition Rules
- Biome borders are soft and blended.
- No hard “tile edges.”

## Purpose (Design Rule)
Terrain is informational, not decorative:
- It guides exploration and traversal decisions.
- It hints at relic/vault presence through subtle silhouettes, glow, terrain shaping.
- It supports sparse/mysterious population distribution.
