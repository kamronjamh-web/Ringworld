# Beam Emitter POIs, Milestone 2 plan (underground)

Milestone 1 is complete (see [emitter-milestone1-handoff.md](emitter-milestone1-handoff.md)). Milestone 2 adds the shaft below the bore, the reactor cavern and the lift.

## Key finding

Terrain3D (gen-17, runtime-surface) worlds have **no underground volume**. The launcher frees `SmoothTerrainGrid`, and the gen-17 sampler rejects caves, unified 3D density and designed-cave composition (`RingTerrainSampler.SurfaceGeneration.cs:106-114`). There is no density, collision or rendering below the heightfield. The site spec's SDF carve (§3.3–3.6, P5–P7) assumes a voxel underground that does not exist for these worlds.

## Approach: one SDF authority, baked shells

- **SDF library.** A pure, backend-neutral library (`scripts/Terrain/Sites/`) builds the per-kind bore, shaft, cavern and bay primitives from site spec §3.2. It is the single answer to "is this point inside the site's underground air?".
- **Baked shell.** It drives a cavern shell mesh and collision, baked offline once per kind.
- **Queries.** It drives the gameplay, probe and streaming queries.
- **Later carve.** It will drive the real carve and cave exclusion once caves are ported.

## User decisions (2026-10-15)

1. **Descent:** a scripted lift with stations at the surface dock and the arrival overlook. Ride speed is a grave-key setting (default 150 m/s, about 30 s).
2. **Bore fall:** the plug **opens**. Falling into the bore drops the player down the shaft into the reactor cavern. The plug is an open iris and no longer a floor. Inside the site volume nothing snaps the player back up.
3. **Reactors:** every site gets the full reactor (regular `ReactorPOI`, main `MainReactorPOI`), streamed through the existing slot pool.
4. **Deferred to the cave port:**
   - gen-16 hooks (P5)
   - the density hook and carve (P6)
   - designed-cave exclusion (P7)
   - digging the reactor shell (D9)
   Sites stay a Terrain3D/gen-17 feature.
5. **Lead defaults (not asked):**
   - The chamber centre stays at P−4000.
   - The shell shape comes from the §3.2 SDF.
   - Falling off a causeway or below the shell auto-recovers the player to the arrival overlook.

## Status at pause (2026-10-15)

Implementation is **paused** while the voxel/cave direction is decided. Read [voxel-cave-direction-handoff.md](voxel-cave-direction-handoff.md) first. If option B (gen-17 on the smooth voxel backend) is chosen, M2-2's baked shells and most of M2-3 are superseded by the real SDF carve, and M2-4 onwards should be re-planned.

Nothing below is committed. The working tree builds with 0 warnings and 0 errors.

**Done and verified by the primary agent**

| Step | State | Files | Tests (all PASS) |
|---|---|---|---|
| M2-1 | **Done.** `EmitterSiteUnderground` SDF library: bore R76/R151 to −200, shaft R85 (regular) / R151 (main, deliberately not 2× = R170, to clear the exhaust roof ring), core cavern, 3 bays, arrival tunnel. Main is regular scaled 2×. The column is open from the surface through the plug to the cavern. About 50 ns per query, 0 B allocated. Named depths are in `Shape(kind)`: exhaust top P−2961 / P−1922, chamber P−4000, floor P−5000 / P−6000. | `scripts/Terrain/Sites/EmitterSiteUnderground.cs`, `tools/EmitterSiteUndergroundTest.cs`, `tools/emitter_site_underground_test.gd` | `emitter_site_underground_test` |
| M2-2 | **Done.** Baked cavern shell per kind, from the custom surface-nets mesher (`EmitterSdfMesher`) plus analytic shaft and tunnel tubes. LOD0 is 38k triangles (regular) / 75k (main), with 3 LODs. Collision comes from LOD1. Seam ring at −200 on the liner radius; regular gets a 76→85 ledge. Deterministic (byte-identical re-bake). Material `CavernRock.tres`. Not yet placed in the world; that is M2-4. | `scripts/Terrain/Sites/EmitterSdfMesher.cs`, `EmitterUndergroundShell.cs`, `tools/EmitterUndergroundBaker.cs`, `tools/poi/build_emitter_underground.gd`, `tools/EmitterUndergroundBakeTest.cs`, `assets/poi/underground/*` | `emitter_underground_bake_test`, `emitter_site_underground_test` |

**Interrupted: M2-3 (underground gameplay semantics)**

The implementer was stopped while adding a final "no recovery while on the surface" check to the gameplay test. What is in the tree:

- `RingTerrain3DGameplay.Underground.cs` (new): `IsInsideSiteUnderground` and `TryGetSiteUnderground`, gated by the Bore apertures switch. Inside the volume, collision height comes from the SDF floor below the player.
- Window-swap capsule lift, spawn snap and coverage velocity clamp are disabled inside the volume (`RingTerrain3DGameplay.Streaming.cs`, `.Startup.cs`, `RingVoxelExplorerController*.cs`).
- The plug is now an open iris: the disk mesh and cylinder collision are removed, and the teal rim ring and light are kept (`EmitterBoreGeometry.cs`, `EmitterBoreLayer.cs`).
- `Terrain3DUndergroundProbeSource` reports air inside the volume.
- Recovery to the ArrivalOverlook pose, in flight for now, because nothing exists there to stand on until M2-4. There is a grave-key "Underground recovery" toggle (`GameplayEmitterSiteSettings`/`Defaults`, `RingLiveTuningDrawer.EmitterSites.cs`).
- M1-8 tests were updated to the open-plug semantics.

These passed after the stop:

- `emitter_bore_gameplay_test`: regular and main fall through the plug, no upward jump on window swaps, recovery error 0.00, cavern walk unclamped, no recovery while the switch is off.
- `emitter_bore_aperture_test`, `emitter_site_teleport_test` (bore escape still works), `emitter_fresh_spawn_test`, `emitter_site_settings_smoke`, `emitter_site_hole_test`, `underground_mode_smoke_test`, `terrain3d_gameplay_stream_test`, `world_loading_startup_contract`.

**Not finished in M2-3:**

1. The surface-no-recovery assertion the implementer was adding is incomplete. Check the end of `tools/emitter_bore_gameplay_test.gd`.
2. The primary agent has not done a line-by-line code review of the M2-3 diff, especially the explorer controller changes.
3. Not re-run after the stop: `terrain3d_gameplay_movement_test`, the underground round trip (`--headless --path . -- --underground-mode-roundtrip-smoke-test --smooth-terrain-backend`), the smooth-terrain smoke (`res://scenes/SmoothTerrainRuntimeSmoke.tscn -- --smooth-terrain-runtime-smoke`), and `emitter_poi_streamer_test`.
4. Fall speed over the 4 km drop was not reported. Check that the explorer cannot tunnel through floors once M2-4 adds shell collision.
5. Recovery currently arrives hovering at the overlook. Switch it to grounded once the reactor and shell collision exist (TODO tied to M2-4).
6. A known behaviour change from Milestone 1: falling into a bore no longer lands on a plug floor (user decision). Until M2-4, every fall ends in recovery.

**Not started:** M2-4 to M2-10 (table below).

## Steps

| Step | Goal | Depends on | Parallel with |
|---|---|---|---|
| M2-1 | `EmitterSiteUnderground` SDF library: per-kind primitives, `SignedDistance`, `Contains`, AABB, `TryBoundCarve`, local frame and yaw; continuous open column from the surface hole through the plug iris and shaft to the cavern | none | none |
| M2-2 | Bake tool: `CavernShell_{Regular,Main}.res` (inward), coarse collision, seam ring, long liner below the plug | M2-1 | M2-3 |
| M2-3 | Gameplay semantics: `IsInsideSiteUnderground`; no capsule lift, spawn snap or coverage clamp inside the volume; open plug; recovery to the overlook | M2-1 | M2-2 |
| M2-4 | Slot integration: reactor, shell and liner per slot; underground-envelope gating for visibility and collision; render layer 2; failure degrades to a sealed plug with the lift disabled | M2-2 | none |
| M2-5 | Lift: pure `EmitterLiftCore`; scripted explorer drive; interact-to-call; holds for Terrain3D coverage at the surface dock | M2-3, M2-4 | M2-6, M2-7 |
| M2-6 | Underground mode: probe latch in the shaft, deep hide; spire-detail participant; cavern fog; rides do not force-exit | M2-3, M2-4 | M2-5, M2-7 |
| M2-7 | Lighting: up to 4 unshadowed omnis per kind on cull mask 2; shaft rings; cave ambient | M2-4 | M2-5, M2-6 |
| M2-8 | Save and navigation: saves inside the volume restore at the nearest station; viewpoints Lift dock, Shaft mid, Arrival overlook, Cavern floor | M2-5 | none |
| M2-9 | Settings consolidation: "Emitter underground" grave-key section | M2-4 to M2-8 | none |
| M2-10 | Capture route: underground shots, timed ride, performance report | all | none |

## Budgets (RTX A1000, 1600x900)

- Overlook with underground mode latched: ≤4.5 ms regular, ≤5.5 ms main.
- M1 surface shots: within ±0.2 ms of current.
- Shell: ≤40k triangles regular, ≤80k main, after LOD.
- No runtime instantiation.
- No frame over 8 ms during a ride.
- Volume query costs about 50 ns or less.

## Known risks

- The explorer's scripted drive touches a large controller (noclip, teleport, swimming).
- The lift's inclined leg must clear the exhaust pylons.
- It is unverified whether the sun's cull mask excludes shadow casters in 4.8.
- Underground openness threshold: 1 escaping ray out of 17 (0.0588) still latches against `EnterOpennessMax` 0.06. Tests pin the current behaviour.
- The main-site arrival extension is not specified in any spec yet.
- Free fall through the open plug is about 4 km. Check landing behaviour and fall speed.
