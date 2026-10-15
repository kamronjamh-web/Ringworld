# Handoff: Beam Emitter POIs, Milestone 1 (paused 2026-10-14)

Pick-up notes for continuing on another machine. The design sources of truth are:

- [emitter-site-terrain-spec.md](emitter-site-terrain-spec.md): site placement and planned terrain, with user decisions at the end.
- [emitter-poi-streaming-spec.md](emitter-poi-streaming-spec.md): POI streaming, far stand-ins, menu and tests, with user decisions and the **MAIN SPIRE DECISIONS** at the end.
- [underground-mode-spec.md](underground-mode-spec.md): underground mode status plus the overall cave roadmap.
- [../assets/poi/README.md](../assets/poi/README.md): imported POI assets, pivots, merge/build tools.

## Machine setup

1. Install the .NET 10 SDK system-wide: `winget install --id Microsoft.DotNet.SDK.10 --exact`. The project targets `net10.0`.
2. Use the pinned engine `artifacts/engine-upgrade/Godot_v4.8-dev7_mono_win64/` (not a 4.7 install). Launch the editor with `powershell -ExecutionPolicy Bypass -File C:\<repo>\tools\use_godot_48.ps1 -e`, or by double-clicking the engine exe once .NET 10 is installed.
3. Build: `dotnet build RingworldTechDemo.csproj -p:NuGetAudit=false -v:minimal`. The expected result is 0 warnings and 0 errors.
4. Run headless tests with `<engine>_console.exe --headless --path . --script res://tools/<name>.gd`. Pass user args after a separate `'--'`.
5. Optional, for the PDA voice prototype: run `prototypes/pda-voice/download-models.ps1` (about 2 GB, gitignored), then `run.ps1 all`.

## Done and verified

Each item was reviewed and its tests were re-run by the primary agent.

| Step | What | Key files |
|---|---|---|
| P0 | Golden terrain fingerprints proving existing worlds never change | `tools/fixtures/emitter_site_baseline_golden.json`, `tools/emitter_site_baseline_test.gd` |
| P1 | Site registry: 595 sites (99 rows x 6 regular + 1 main on the 159 km centre line replacing its whole row), O(1) lookups, nudge with cut/fill penalty, main row prefers pads near sea level | `scripts/Terrain/Sites/EmitterSiteRegistry.cs`, `EmitterSiteBuilder.cs`, `EmitterSiteRecipe.cs` |
| P2 | Surface operator: exact pad flatten, natural-dome islands, terraced bowls, skirt fade; per-kind constants (main pad R1000, enclosure R4000) | `scripts/Terrain/Sites/EmitterSiteSurfaceOperator.cs` |
| M1-3 | Placement and frame math (world yaw = 67.5° + β, the game frame has across flipped), beam-to-hub geometry (main beam tilt 0°) | `EmitterSitePlacement.cs`, `EmitterSiteFrameMath.cs` |
| M1-2 | Gen-17 recipe `ringworld_surface_sites_v5`: shaping hooked into the height model; new runtime-surface worlds use v5; existing slots keep their frozen recipe (v4 bit-identical, hash `C62D22E76ED02DFA`) | `TerrainGenerationHeightModel.cs`, `TerrainGenerationParameters.cs`, `EmitterSiteTerrainShaper.cs`, `world_size_launcher.gd`, `terrain_generation_recipe.gd` |
| Assets | Regular spire + reactor and main spire + main reactor imported, merged, wrapped with walkable collision and far proxies; main far proxy updated with connector rings (8,640 tris) | `assets/poi/**`, `scenes/poi/EmitterReactorTest.tscn` (F6; M toggles regular/main; 1-5 bookmarks) |

Passing tests: `emitter_site_baseline_test`, `emitter_site_registry_test`, `emitter_site_surface_operator_test`, `emitter_site_placement_test`, `emitter_site_terrain_v5_test`, `runtime_terrain_cpu_pass2_test`, `terrain3d_gameplay_stream_test`, plus the smooth-terrain runtime smoke (`res://scenes/SmoothTerrainRuntimeSmoke.tscn '--' '--smooth-terrain-runtime-smoke'`).

## Next steps (Milestone 1, in order)

Two steps were started and then stopped before they edited any files. Start them fresh:

1. **M1-6 surface consumers (spec P4a).** Add the site mask to `TerrainSample`. Pads and cut faces use the rock channel and beaches use sand. Gate vegetation (trees in `RingBiomeDecorationLayer`, grass in `RingStandingGrassLayer`, and the GPU grass/vegetation path, which may bypass the CPU gate, so verify it). v4 must stay bit-identical. Data source: `RingTerrainSampler.TryGetEmitterSiteSurfaceMask` and `Diagnostics.SiteMask`. **Pads currently still grow grass and trees.**
2. **M1-4 far stand-ins (streaming spec §B).** Two MultiMeshes rendered by the main camera, not the scaled far viewport: 594 regular `BeamEmitter_FarProxy` plus 1 `MainSpire_FarProxy`. Transforms come from `EmitterSitePlacementSource.FillWorldTransforms` and are rebuilt on the canonical anchor-change event. Hand off at 11.5 km (regular) and 26 km (main), out to about 700 km, never past the global terrain fade. Include a per-instance hide API for the streamer, an underground FarTerrain participant, a grave-key setting, and an eclipse beam API (no visuals).

These two can run in parallel. After them:

3. **M1-5 POI streamer.** Load at 14 km and unload at 16 km with a 1 s dwell. Use a pre-instantiated 2-slot pool. Turn collision on at 1.5 km and off at 2 km. Register with the loading bridge.
4. **M1-7 Terrain3D hole and collision mask** (control-map bit 2, R80 regular / R155 main).
5. **M1-8 bore liner and plug** (−200 m, no guard collar, so players can fall in). Add an aperture discard in the proxy, global terrain and ocean shaders. Make the hole-aware `SampleCollisionHeight` and Terrain3D underground probe treat the hole correctly.
6. **M1-9 / M1-10 settings and grave-key menu.** Show nearest-site status. Add teleport to nearest / site N / nearest island / nearest mountain / main, at entry, gallery, 20 km and high-altitude viewpoints. Until this exists you cannot easily find a site in game.
7. **M1-11 windowed capture route.** Visit a land site, an island site, a mountain site and the main site, from ground, 20 km and altitude.

Milestone 2 covers the underground parts: the SDF carve of bore, shaft and reactor cavern (structures may go below bedrock), placing `ReactorPOI`, and excluding designed caves. Gen-16 hooks (spec P5) also belong there.

## Open items and known issues

- Gen-17 site-area sampling overhead is +17% against a spec target of ≤10%. Optimise later.
- Some gen-17 mountain sites still need terraced cuts up to about 950 m (penalty scale `CutFillPenaltyScaleM = 200`). The user has not decided whether to keep these as dramatic pits.
- Islands keep `TerrainSample.Region = Ocean*` because region comes from unshaped continentalness. Decide whether that matters to any consumer during M1-6.
- POI collision covers walkable floors only. There is no wall, doorway or parapet collision yet, and the reactor collision was selected by name pattern, so check it in the editor.
- A Godot `--import` can delete the tracked temporary file `addons/terrain_3d/bin/~libterrain.ringworld.windows.debug.x86_64.dll`. Restore it with `git show HEAD:<path> > <path>`, or untrack it and ignore `~*.dll`.
- Startup errors about the missing `ring_voxel_mesher.gdextension` are pre-existing.
- Pre-existing failing tests unrelated to this work: `smooth_terrain_pilot_smoke_test.gd`, `sunshine_menu_smoke`, `sunshine_shadow_toggle_smoke`, `distant_rain_curtains_smoke`, `rain_surface_behavior`.
- `prototypes/Ringworld_MainSpire_FarProxy_Rings_Update.zip` was applied (3 files). Keep it or delete it.

## Workflow notes

`AGENTS.md` defines the roles. The primary agent plans and reviews. Implementers make bounded changes, a lead designs anything non-obvious, and the primary re-runs tests before reporting. Agents must never run `git stash`, `reset` or `checkout` on the working tree. The user commits and pushes. New gameplay and rendering features expose settings in the grave-key menu with persistence.
