# Player Movement & Camera

## Movement Pillars
- Halo-like FPS feel on spherical planets.
- Rigidbody-based control (gravity-aligned).
- Smooth surface alignment; no harsh snapping.
- Supports uneven terrain, slopes, and jumping.

## Control Requirements
- Walk / run / sprint (as implemented)
- Jump with gravity-aligned up
- Grounding that works on curved surfaces
- Input system compatible with Unity 6 workflows

## Camera
- Toggleable First Person / Third Person.
- Smooth transitions between modes.
- Camera up is aligned to gravity vector.
- Player model visible only in third person.
- Third-person camera collision handling is required (no clipping through planet surface/props).
