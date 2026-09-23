# Mountain Terrain Overhaul — Deferred Backlog Plan

## Status and priority

- **Status:** deferred; no implementation has been approved or performed.
- **Current priority:** continue [the weather checklist](weather-system-checklist.md), Phase 3.
- **Confirmed design goals:** a regional mix and at least three combined Perlin
  fractal layers at different scales. The exact meaning of the gradient trick
  still needs confirmation.
- This document preserves the brainstorm for later implementation planning.
  Feature sizes, octave counts, style proportions, and shaping formulas below
  are proposals to profile and tune, not finalized settings.

## Current-state constraints

- `RingTerrainSampler` is the shared deterministic source for voxel terrain and
  far LODs. Its contract currently identifies generator version 16 and a terrain
  definition hash, so a terrain revision must be explicit and cache-aware.
- The existing pipeline already has base/detail terrain, regional fields,
  warped ridge noise, a valley field, and a scalar erosion mask. Noise creation
  explicitly sets only seed and frequency today.
- Current settings include mountain masks, warp distance/frequency, belt
  broadness, eroded-mountain strength, valleys, and escarpments, plus a rare
  peak and exceptional-summit ceiling.
- The sampler preserves the periodic around-ring seam and uses a tiled,
  precision-safe Mega domain with deterministic overlap blending for very large
  rings. Any new field must retain both properties.
- Nearby terrain, collision, proxy/global LODs, and the minimap need one
  sampler-derived terrain definition; none may invent a divergent mountain map.
- Existing ordering around coasts, hydrology, biome assignment, caves, and
  player edits is a compatibility constraint to map before integration.
- Keep the established mountain-height and build ceilings initially. Do not use
  this work as a reason to raise them.

## Target character: regional mix

- Produce a seeded mix of alpine ranges, rounded weathered ranges, and sparse
  escarpment/plateau regions, with smooth regional transitions rather than one
  uniform mountain style.
- Broad regional shape controls where peaks and fine detail may appear; it
  should also leave room for broad foothills, connected valleys, and passes.
- Use ridged transforms only as a proposed shaping tool, with gentle seamless
  domain warping to break artificial bands.
- Do not represent this noise composition as real erosion or real watershed
  simulation. “Weathered” and “valley” describe the visual result unless a
  separately validated physical model is added later.

## Candidate band model (tentative)

Use at least three independently seed-derived Perlin-fractal bands, all sampled
through the sampler's periodic, precision-safe coordinate path:

| Band | Approximate scale | Proposed octaves | Role |
| --- | --- | --- | --- |
| Range uplift | 50–200 km | ~3 | regional mass, broad foothills, range placement |
| Ridges / peaks | 2–20 km | ~4 | alpine spines, peak grouping, passes and connected valleys |
| Secondary landforms | 100–1,500 m | ~3 | subordinate relief that survives only where appropriate |

- Derive each band seed independently from the world seed; document the
  derivation so deterministic worlds remain reproducible.
- Profile actual coverage, amplitude, spectral balance, and LOD appearance
  before selecting frequencies, amplitudes, lacunarity, gain, warp amount, or
  ridged-transform formula.
- Regional masks should blend styles smoothly: alpine can favor strong ridge
  response, weathered ranges softer rounded uplift, and rare plateau regions a
  controlled escarpment contribution.

## Open gradient-trick decision

- The discussed “gradient trick” is **unconfirmed**. It may mean proposed
  slope/derivative-based damping of small detail on steep terrain, or it may
  mean height- or distance-based falloff. Do not collapse those meanings.
- Prototype and compare the alternatives before choosing one. A derivative
  damping path would reduce tiny relief where the macro surface is already
  steep; a height/distance falloff solves a different placement problem.
- If derivatives are practical, prefer analytical derivatives of the relevant
  composed noise/domain transform over repeatedly sampling the entire terrain
  pipeline at neighboring points. Validate its cost and seam behavior first.

## Integration principles

- Keep the mountain composition inside `RingTerrainSampler` (or a sampler-owned
  deterministic helper), then feed the same result to collision, nearby mesh,
  proxy/global LODs, and minimap rendering.
- Define explicit filtering/morphing for smaller bands as LOD coarsens, so
  secondary details neither alias nor pop at LOD boundaries.
- Preserve coast and hydrology behavior, biome contracts, cave carving, and
  player-edit application order. Identify every height consumer before changing
  the composition.
- Version the generator, definition hash, terrain caches, and save compatibility
  deliberately. Existing user saves must not be silently reshaped; migration,
  preserved legacy sampling, or an explicit regeneration choice needs design.

## Deferred implementation and acceptance checklist

- [ ] **Regional-shape prototype:** add no final settings until a seed-stable
  prototype shows smooth regional blends, foothills, connected valleys/passes,
  and the intended alpine/weathered/escarpment mix.
- [ ] **Regional-shape acceptance:** sample representative seeds and verify no
  visible ring seam, precision-tile seam, or unintended uniform banding.
- [ ] **Gradient decision/prototype:** document which interpretation is tested;
  compare derivative detail damping with any height/distance falloff candidate.
- [ ] **Gradient acceptance:** retain only a measured improvement that is
  deterministic, seam-safe, and affordable at terrain-generation rates.
- [ ] **Integration/versioning:** connect the chosen sampler output to all
  terrain representations; bump and validate generator/cache/save contracts
  before any default-world regeneration.
- [ ] **LOD acceptance:** verify consistent filtered/morphed silhouettes across
  nearby, proxy, global, and minimap terrain, including LOD transitions.
- [ ] **Visual tests:** inspect ground, flight, and space views over every style
  and transition; include coast/river/lake/biome/cave/edit interaction cases.
- [ ] **Performance tests:** measure sampling, chunk generation, far-LOD build,
  memory/cache behavior, and frame stability on Certified first, with Normal
  and Mega regression coverage.
- [ ] **Wrap-boundary tests:** inspect canonical ring wrap and precision-safe
  tile overlaps at every applicable LOD and with collision queries.

## References for later evaluation

- [Godot FastNoiseLite documentation](https://docs.godotengine.org/en/stable/classes/class_fastnoiselite.html)
- [Planetary derivative approach precedent (SBGames 2018)](https://www.sbgames.org/sbgames2018/files/papers/ComputacaoShort/188242.pdf)
