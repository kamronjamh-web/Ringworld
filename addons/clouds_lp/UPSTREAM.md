# Upstream source

- Project: Clouds for Little Planets
- Repository: https://github.com/BinarySemaphore/clouds_little_planets
- Imported commit: `a97dd22d834bba5aea93b299873d1cc1483abd5d`
- Imported subtree: `addons/clouds_lp`
- License: MIT (see `LICENSE` in this directory)

The addon is kept as a pinned vendor snapshot so the runtime cloud-provider
demo is reproducible. Project-specific integration code lives outside this
directory.

Local compatibility changes to `scripts/clouds_lp_comp.gd` are intentionally
small and documented in place: runtime `.NET` resource loading uses untyped
profile exports, compositor RIDs can be explicitly released during a provider
switch, and light lookup starts at `SceneTree.current_scene` so project
autoloads do not hide scene-owned lights.
