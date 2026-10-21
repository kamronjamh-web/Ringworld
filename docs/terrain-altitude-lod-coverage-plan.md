# Terrain LOD coverage at altitude

**Problem.** When flying high, very coarse terrain appears close to the camera,
with sawtooth coastlines and a hard seam (see `docs/phase-c-p0-measurements.md`).
Visible terrain coarseness must scale with distance from the camera.

## Root cause
- **Bridge tiers are never built on Certified.** `RingTerrainLodCoordinator._Ready`
  registers the 512 m / 1 km / 2 km / 4 km bridges and LOD9–11 only for Mega
  (`ShouldBuildAbsoluteWorldMegaProxyTiers`). The proxy chain therefore ends at
  128–256 m, then hands directly to `RingTerrainGlobalLod`.
- **The global mesh is far too coarse where it starts.** It spans
  2048×64 to 4096×256 cells, i.e. 7.7–30 km edges, and starts 17–100 km from
  the camera. It only meets the pixel-error budget beyond about 1000–2000 km,
  so near the camera it is 30–240× over budget.
- **The sawtooth coastline** comes from the global (and far-viewport) water rule:
  a triangle is water when at least 2 of its 3 vertices are wet
  (`RingTerrainGlobalLod.cs` ~1072). Proxies already clip the shoreline exactly
  (`AppendClippedWaterTriangle`).
- **The fixed switch at 20 km** (`UpdateHighAltitudeProxySuspension`) suspends
  LOD1–5. Tiers whose far handoff is below the camera altitude keep streaming
  full windows that are never seen (about 300 MB at 16 km on High).
- **Re-anchor rebuilds everything.** Every ~32.8 km re-anchor force-rebuilds every
  sector (`RebuildExistingMeshesOnCanonicalAnchorChange` is true on Certified;
  the transforms are translation-only).

**Experiment** (4070, High, bridges enabled at runtime): the seam and the teeth are
gone, and the coastline is continuous. Frame cost rose 1.5–3 ms before the
savings from per-level activation and no-rebuild.

## Invariant
For each rendered tier T:
- s_T = edge spacing;
- visible slant interval = [previous tier's far handoff, own far handoff];
- T is visible when its far handoff > h (altitude above ground);
- projected error ε_T = s_T × P / max(n_T, h), where P = projScale and n_T is
  the near end of the visible interval.

Limits, where e = TargetPixelError:
- proxy tiers: ε ≤ 2.5e;
- global mesh / far viewport: ε ≤ 5e;
- coverage must have no gaps between h and the backdrop's near handoff.

It must hold for Low, Medium, High and Desktop High.

## Work orders
- **WO-A, chain planner + headless contract.** Add a pure
  `RingTerrainLodChainPlanner`. The coordinator band/active logic delegates to it,
  with no behaviour change in this step. Add
  `tools/lod_projected_error_contract_test.gd`; it is known-failing until WO-B.
- **WO-B, extend the chain on Certified.**
  - Build the bridge tiers and LOD9+ when not routed to the Mega scaled viewport.
  - The launcher sets the bridge widths.
  - The terminal tier is chosen by backdrop ε ≤ 5e.
  - The global mesh resolution floor is 4096×64.
- **WO-C, per-level altitude activation.** A tier is active when its far handoff
  > h, with hysteresis (activate below 1.25×, deactivate above 1.10×). Trim outer
  windows to √(f² − h²). This replaces the fixed 20 km switch.
- **WO-D, no mesh rebuild on re-anchor on Certified.**
- **WO-E, live flight invariant test.** Fly to 0.5, 2, 8, 16, 23 and 60 km at
  pitches −15, −45 and −89, plus a moving leg at 1.1 km/s. It fails on holes or
  ε violations.
- **WO-F, shared shoreline clip** for the global mesh and far-viewport water.
- **The "intermediate LOD tiers" grave-key toggle** moves to debug tooling
  (AGENTS.md).

## Later
- **Macro surface atlas** at 1024 m (water depth, kind, biome; about 20 MB per world)
  for crisp far-field coasts.
- **Camera-centred geometry clipmap** (about 5–7 weeks), computed on demand with
  no whole-ring bake. Decide after the core fix is measured.
- **A1000 measurement** of the new defaults, then tune Low/Medium.

## Implementation notes (WO-A to WO-F)
- **Planner:** `scripts/Voxel/LOD/RingTerrainLodChainPlanner.cs` holds the band, error, terminal-tier,
  activation and trim math; `RingTerrainLodCoordinator` delegates to it.
  `tools/lod_projected_error_contract_test.gd` checks the invariant for Low, Medium, High and Desktop High
  at 0.5, 2, 8, 16, 23 and 60 km; `tools/lod_altitude_activation_contract_test.gd` checks hysteresis and trim.
- **Terminal tier:** the first tier whose far handoff lets the global backdrop meet 5 x TargetPixelError.
  On the Certified ring all four presets end at LOD9 2 km (far handoff 329 to 1054 km).
- **Global mesh floor:** cells no larger than about 7.7 km x 5 km, which is 4096 x 64 on the Certified ring
  (`GlobalResolutionFloor`; smaller rings get a proportionally smaller floor).
- **Activation hysteresis:** a tier activates below 1.25 x its far handoff and releases above 1.40 x. The
  work order's 1.25 / 1.10 would release a tier before it activates, so the release ratio is raised.
- **Window trim:** the horizontal reach is sqrt(f^2 - c^2) with c = viewer height above sea level minus an
  8 km terrain ceiling, so peaks beside a high viewer are never clipped. Reach is rounded up to whole sectors.
- **Re-anchor:** Certified proxy levels no longer rebuild every sector on a canonical anchor change.
- **Shoreline:** `RingShorelineClip` is shared by proxy sectors, the global mesh and the scaled far viewport.
- **Intermediate tiers toggle:** removed from the grave-key drawer and the Esc menu; Shift+F6 in the LOD
  diagnostics HUD toggles it for the session. The old `gameplay_lod_transition_defaults.tres` is no longer read.
- **Live test:** `tools/terrain_altitude_lod_flight_test.ps1` (capture notes for the A1000 in
  `tools/terrain_altitude_lod_a1000_capture.md`).
