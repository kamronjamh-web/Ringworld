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
| M1-6 | Surface consumers: `SiteSurfaceMask` carried on `TerrainSample`; pads/cut faces blend into the Alpine (rock) channel, beaches into Desert (sand); grass, trees and landmark towers gated by `SiteMask.BlocksVegetation` (CPU only; the GPU path just culls CPU placements). v4 fingerprint test proves v4 unchanged. Tree disk-cache key gets `|es1` on site worlds | `TerrainSample.cs`, `RingTerrainSampler.cs` (`ApplySiteSurfaceMask`), `RingGrassTileBuilder.cs`, `RingBiomeDecorationLayer*.cs`, `RingScaleAnchorLayer.cs`, `tools/EmitterSiteTerrainV5Test.cs` |
| M1-4 | Far stand-ins: root node `EmitterStandIns` with 594+1 MultiMesh instances in the main camera, rebuilt absolutely on anchor change, out to min(700 km, global fade, 0.98·camera far). Custom data `.r` = presented (applies the near handoff fade band), `.g` = hidden; unpresented stand-ins draw at any distance (spec §B.1). Underground FarTerrain participant, eclipse `IEmitterBeamSource` (no visuals), grave-key section "Emitter far stand-ins" in the Terrain3D tab. Windowed-verified on pads (shader coverage uses `abs(PROJECTION_MATRIX[1][1])`: the RD projection is Y-flipped) | `scripts/poi/Emitters/*`, `materials/ring_emitter_standin.gdshader`, `scripts/ui/GameplayEmitterSite*.cs`, `RingLiveTuningDrawer.EmitterSites.cs`, `UndergroundParticipants.cs`, `tools/EmitterStandInTest.cs` |
| M1-5 | POI streamer: root node `EmitterSites`, pool of 2 regular + 1 main slot (always resident, threaded load at world start, one instantiation per frame). Per-kind effective load radius `max(setting, detail visible end + 500, stand-in handoff + 500)`: regular 14/16 km, main 32/34 km (user decision), 1 s dwell, collision 1.5/2 km. Assigning marks the stand-in presented; the slot FarProxy stays hidden while stand-ins are active. A failed POI scene degrades that kind to stand-ins and never blocks loading (user decision). Registered with `RingWorldLoadingBridge`; grave-key section "Emitter POI streaming" | `scripts/poi/Emitters/EmitterPoiStreamer*.cs`, `RingWorldLoadingBridge.cs`, `GameplayEmitterSite*.cs`, `RingLiveTuningDrawer.EmitterSites.cs`, `tools/EmitterPoiStreamerTest.cs` |
| M1-7 | Terrain3D hole + collision mask: control bit 2 set where `du²+dw² < R²` (R80 regular / R155 main; POI floor coverage measured 100% to R83 / R158). Mask is a pure function of (registry, frame), built in the pack job; reused regions are always rewritten (holes or blank) so no phantom holes. Collision drops triangles touching a hole; v4 unchanged | `EmitterSiteApertures.cs`, `RingTerrain3DApertureMask.cs`, `RingTerrain3DCollisionGeometry.cs`, `RingTerrain3DSurface*.cs`, `RingTerrain3DGameplayStream.cs`, `tools/EmitterSiteHoleTest.cs` |
| M1-8 | Bores: always-present `EmitterBores` layer (2 slots, code-built liner R76/R151, plug at P−200 with teal ring + point light, rim cap to R82/R157, collision). Single authority `SurfaceApertureConsumersActive` so a hole never exists without a floor. `SampleCollisionHeight`/activation/spawn return the plug top inside the liner; underground mode does not trigger in the open bore. Aperture discard (global shader uniforms, within 16 km) in proxy, global, ocean and proxy-shadow shaders, main world only. Grave-key "Emitter bores": apertures switch (applies on next window swap), range, plug light. Exit by noclip until M1-10 teleport | `scripts/poi/Emitters/EmitterBore*.cs`, `RingTerrain3DGameplay.Apertures.cs`, `materials/ring_surface_aperture.gdshaderinc`, 5 shaders, `project.godot` shader globals, `tools/EmitterBore*`, `tools/emitter_bore_*_test.gd` |
| M1-9/10 | Site navigation: pure `EmitterSiteNavigator` (nearest / class / site N / main; Entry, Gallery, Far 20 km (40 main) with dry-flat land check and 200 m hover fallback, High 150 km), `EmitterSiteTeleport` sequencer on the existing spawn settle, `RingVoxelExplorerController.TryTeleportToCanonical` (clears water/benchmark state). Grave-key "Emitter sites" section (status, buttons, site spinner; menu only, no keybind; "Nearest entry" is the bore escape). Fresh v5 worlds spawn at the main spire Far pose (`RingWorldSaveManager.ApplyFreshWorldSpawn`, only when no saved state) | `scripts/poi/Emitters/EmitterSiteNavigator.cs`, `EmitterSiteTeleport.cs`, `RingLiveTuningDrawer.EmitterNavigation.cs`, `RingWorldSaveManager*.cs`, `tools/emitter_site_navigator_test.gd`, `emitter_site_teleport_test.gd`, `emitter_fresh_spawn_test.gd` |
| GLOD fix | Pre-existing: global terrain hull underside showed as a pale slab where terrain segments had not streamed yet, and regional visibility went stale after teleports. Hull roof/underside/cavity now gated per terrain segment (edge walls still immediate); visibility refreshes on anchor change and segment publish | `scripts/Voxel/LOD/RingTerrainGlobalLod.cs`, `tools/GlobalTerrainPreparationContractTest.cs` |
| M1-11 | Windowed capture route (land, island, mountain, main; entry, bore overview, gallery, far, high, approach flights). Crossover verified: regular assign/present 13.98 km, release 16.0 km; main 31.95 / 33.96 km; no pop, first-visible spike ≤2.6 ms, no lid/slab/ocean disk, 0 shader errors | `tools/emitter_capture_route.gd`, `tools/run_emitter_capture_route.ps1` |

Passing tests: `emitter_bore_aperture_test`, `emitter_bore_gameplay_test`, `emitter_site_hole_test`, `emitter_poi_streamer_test`, `world_loading_startup_contract`, `emitter_far_standin_test`, `emitter_site_settings_smoke`, `emitter_site_baseline_test`, `emitter_site_registry_test`, `emitter_site_surface_operator_test`, `emitter_site_placement_test`, `emitter_site_terrain_v5_test`, `runtime_terrain_cpu_pass2_test`, `terrain3d_gameplay_stream_test`, plus the smooth-terrain runtime smoke (`res://scenes/SmoothTerrainRuntimeSmoke.tscn '--' '--smooth-terrain-runtime-smoke'`).

## Next steps

Milestone 1 is complete (all steps in the table above). Re-run the visual check any time with `tools/run_emitter_capture_route.ps1 [-Out DIR] [-Quick]`.

Milestone 2 covers the underground parts: the SDF carve of bore, shaft and reactor cavern (structures may go below bedrock), placing `ReactorPOI`, and excluding designed caves. Gen-16 hooks (spec P5) also belong there.

## Open items and known issues

- Gen-17 site-area sampling overhead is +17% against a spec target of ≤10%. Optimise later.
- Some gen-17 mountain sites still need terraced cuts up to about 950 m (penalty scale `CutFillPenaltyScaleM = 200`). The user has not decided whether to keep these as dramatic pits.
- Islands keep `TerrainSample.Region = Ocean*` (unshaped continentalness). Checked in M1-6: no consumer is harmed.
- POI collision covers walkable floors only. There is no wall, doorway or parapet collision yet, and the reactor collision was selected by name pattern, so check it in the editor.
- A Godot `--import` can delete the tracked temporary file `addons/terrain_3d/bin/~libterrain.ringworld.windows.debug.x86_64.dll`. Restore it with `git show HEAD:<path> > <path>`, or untrack it and ignore `~*.dll`.
- Startup errors about the missing `ring_voxel_mesher.gdextension` are pre-existing.
- `world_loading_smoke_test` uses the real save slot 1; it fails with "immutable backend" when that slot already holds a runtime-surface world.
- Pre-existing failing tests unrelated to this work: `smooth_terrain_pilot_smoke_test.gd`, `sunshine_menu_smoke`, `sunshine_shadow_toggle_smoke`, `distant_rain_curtains_smoke`, `rain_surface_behavior`, `grass_clump_validation_test`, `grass_streaming_contract_smoke` (the last two reproduced on an unmodified HEAD).
- `prototypes/Ringworld_MainSpire_FarProxy_Rings_Update.zip` was applied (3 files). Keep it or delete it.
- Minor visuals from the M1-11 route: a thin dark terrain line in `land_approach_11–13`, faint grey horizontal seams on the ocean in `island_approach_*`. Unverified whether pre-existing.
- Global terrain still builds its 128 segments in index order (~60 s); after an early teleport, distant areas show the far backdrop until built. Nearest-first ordering was not safe to add.
- Saved altitude is not restored exactly (saved 6000 m came back as 481 m); around/across restore correctly. Pre-existing save behaviour, not investigated.
- Bore look: teal plug ring blooms near-white and the rim cap reads as a bright annulus in daylight; tweak materials if wanted.

## Workflow notes

`AGENTS.md` defines the roles. The primary agent plans and reviews. Implementers make bounded changes, a lead designs anything non-obvious, and the primary re-runs tests before reporting. Agents must never run `git stash`, `reset` or `checkout` on the working tree. The user commits and pushes. New gameplay and rendering features expose settings in the grave-key menu with persistence.
