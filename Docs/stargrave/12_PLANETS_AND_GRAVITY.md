# Planets & Gravity

## Planet Geometry
- Planets use **cube-sphere** geometry: six faces inflated into a sphere.
- Each face is its own mesh and collider.
- Designed to support chunking/LOD later.

## Planet Scale
- Earth-like planets target ~6,000m diameter (performance + traversal balance).
- Moons can be smaller with lower gravity.
- Scale influences movement feel, jump arcs, vehicle handling, and encounter density.

## Gravity Model (Critical)
- Gravity always pulls toward the planet center.
- Gravity is **math-based**, not derived from mesh normals or collision.
- Must function even if colliders are temporarily invalid (generation, streaming, etc.).
- Applies consistently to player, NPCs, wildlife, vehicles, physics props.

## Atmosphere & Time
- Planets may have atmosphere (visual + gameplay impact).
- Day/night cycle supported.
- Tidally locked planets supported.
- Atmosphere scale is configurable per planet (e.g., desired atmosphere scale values are per-planet settings).
