# Handoff: caves and voxels for gen-17 worlds (scoping, 2026-10-15)

Read this before starting the cave/voxel work on the other machine. It is a read-only scoping pass; no code was written for it. Related docs:

- [emitter-milestone1-handoff.md](emitter-milestone1-handoff.md): Milestone 1, complete.
- [emitter-milestone2-plan.md](emitter-milestone2-plan.md): Milestone 2 status.
- [underground-mode-spec.md](underground-mode-spec.md): cave roadmap; "item 5" is the cave port.
- [emitter-site-terrain-spec.md](emitter-site-terrain-spec.md): §3.3–3.6 and P5–P7 cover the density hook, carve and cave exclusion.

The direction is one of:

- **A.** A voxel/SDF underground beneath the Terrain3D runtime.
- **B.** The gen-17 terrain generation math running on the smooth voxel runtime.

## 1. Current state

| Backend (launcher id) | Surface generator | Near presentation and physics | Caves, density and digging |
|---|---|---|---|
| `smooth_terrain_v1` | gen-16 `RingTerrainSampler` | `SmoothTerrainGrid` (32 m chunks, 1 m regular-cell mesher; medium, bridge and coarse cave tiers out to about 4 km), `UseSmoothTerrainPhysics` | Full stack. `RingTerrainSmoothBaseline` (`SmoothTerrainContracts.cs:105-258`) takes natural density, max-unions designed caves (`CaveTopologyDensityComposer`), then adds roof, hull and sidewalls. Sphere add/subtract edit journal. Underground mode P1–P5. |
| `terrain3d_surface_v1` | gen-16 heights | Terrain3D | None |
| `surface_runtime_generation_v1` | gen-17 `TerrainGenerationHeightModel` plus `EmitterSiteTerrainShaper` (v5) | Terrain3D window of 384×384 at 2 m (`RingTerrain3DGameplayStream.cs:18`), 112 m proxy handoff (`RingTerrain3DGameplay.cs:27`), `UseTerrain3DSurfacePhysics` | None. The launcher frees `SmoothTerrainGrid` (`world_size_launcher.gd:1840-1845`) and forces the cave flags off (`:1491-1497`). The sampler rejects caves (`RingTerrainSampler.SurfaceGeneration.cs:106-114`). Digging only changes surface height. |

What these findings mean for the choice:

- **The far stack is shared.** Beyond about 112 m every backend renders through the same proxy, global-LOD and far-viewport stack. That stack reads `RingTerrainSampler`, which is already gen-17-aware (`GetPresentationSurfaceHeight`).
- **The smooth baseline already speaks gen-17.** `RingTerrainSmoothBaseline.Sample` calls `GetSmoothSurfaceHeight`. On a gen-17 sampler that call routes to the runtime model's 2 m triangle lattice (`RingTerrainSampler.cs:416-419`), and `SurfaceHeightOffset` is 0. Only the launcher and the sampler contract block the combination.
- **Designed caves ignore the local surface.** Placement uses a global `SurfaceFloorMeters = max(BedrockY+1, DeepOceanFloorY)` of about −1800 m (`VoxelChunkGridManager.cs:3829-3835`, `CaveFeaturePlacementPolicy.cs:74-79`). Gen-17 basins reach −4500 m and trenches −7500 m, so both options need placement that knows the surface.
- **The gen-17 height model is deterministic but not thread-safe per instance.** It holds a mutable gully cache and native FastNoiseLite objects (`TerrainGenerationHeightModel.cs:8,25-28`). Workers already use `CreateIndependentCopy()` (`RingTerrainSampler.cs:186-201`). The site shaper is shared and immutable. Both are safe to drive density.

## 2. Option A: voxel underground beneath Terrain3D

**The field.** One voxel field in `SmoothTerrainGrid`, kept alive in an "underground-only" mode:

- **Outside aperture zones:** d = cave(p) only where y < h − clearance, and solid everywhere else. No ground surface is meshed there.
- **Inside aperture zones** (site bores, cave mouths, dig sites): d = max(y − h_edit, cave, SiteCarve), where h_edit is the gen-17 height plus the `RingSurfaceEditStore` overrides.

**Holes.** Generalise the M1-7 hole mask (`RingTerrain3DApertureMask.Build`, control bit 2, collision drops holed triangles) so it reads an aperture source that includes dynamic dig and mouth records (spec §4.6 `GetSurfaceApertures(window)`). The voxel mesh needs a 1–2 cell skirt under each Terrain3D hole edge.

**Physics.**

- Both collider sets go into one `MoveAndSlide`.
- Coverage gating must accept Terrain3D coverage or voxel residency.
- Generalise `IsInsideSiteUnderground` (M2-3) to voxel zones.
- `Terrain3DUndergroundProbeSource` must read voxel density.
- Add a Terrain3D underground-mode participant (spec D5).

**Reused as-is:** the smooth mesher and tiers, the cave composer, `EmitterSiteUnderground` (M2-1), `SurfaceAperture`, and the underground-mode controller.

**Hard problems.**

- Two surface authorities (a height journal and a voxel journal) with no shared transaction.
- The dig handoff from a height edit to a hole plus a voxel subtract.
- Crack-free seams.
- Far proxies only discard site apertures within 16 km, so dig and mouth holes show as solid beyond the 768 m window.

**Phases** (risk in brackets):

1. Gen-17 voxel baseline (low).
2. Underground-only grid mode (medium).
3. Aperture service with dynamic Terrain3D holes (medium).
4. Hybrid physics and probe (high).
5. Dig handoff and journals (high).
6. Surface-aware caves (medium).
7. Mouths and skirts (medium).

## 3. Option B: gen-17 generation on the smooth voxel backend

**The field.** Density = y − h17(x,z), using the 2 m triangle-interpolated lattice, max-unioned with caves, SiteCarve and overhangs. This already exists through `SampleNaturalTerrainDensityWithSurface`, and spec §3.3–3.5 (P6/P7) was written for this hook.

**Required work.**

- A **new** backend id (e.g. `smooth_runtime_generation_v1`) and a sampler contract variant that allows caves. Do **not** loosen `surface_runtime_generation_v1`; that keeps existing hashes and goldens intact.
- Build ceiling 8400.
- The save manager must persist the recipe and contract for smooth worlds. Today it only checks them under `UseTerrain3DSurface` (`RingWorldSaveManager.cs:137-139`).
- **A per-chunk lattice cache is required.** Smooth chunks sample at 1 m, and each off-lattice column costs 4 model evaluations at about 2–6 µs each. A 19×19 2 m lattice per chunk needs about 361 evaluations instead of about 5,000; without it gen-17 chunks are roughly 10× slower.

**Effect on Milestone 1 and 2 work.**

| Status | Items |
|---|---|
| Still works | Site registry, surface operator, masks, biomes and vegetation gating, stand-ins, POI streamer (except the Terrain3D coverage hold), proxy aperture shaders |
| Replaced | M1-7 Terrain3D holes, by the real SDF carve (P6). **M2-2's baked cavern shells and most of `RingTerrain3DGameplay.Underground.cs` (M2-3) become unnecessary.** |
| Needs porting | `SurfaceApertureConsumersActive`, bores, `EmitterSiteTeleport`, `RingWorldLoadingBridge`, the edit tool |
| Lost on these worlds | Terrain3D near-surface shading parity, wetness, and the M6 Terrain3D streaming work |
| Inherited from the smooth backend | Known cave costs: roadmap items 2–4, including about 6 s for the Supermassive composer and 12–24 ms install hitches |

**Scale.** Surface reach is the same as Terrain3D, because both backends hand off to the shared proxies at about 100 m. Underground, the smooth tiers reach about 4 km.

**Phases** (risk in brackets):

1. Spike with caves off (low).
2. Persistence (low).
3. Cave contract plus surface-aware placement (medium).
4. P6 site carve and P7 exclusion (medium).
5. Port the M1 consumers that are coupled to Terrain3D (medium).
6. Performance and visual parity (medium–high).

## 4. Comparison

| | A: voxel under Terrain3D | B: gen-17 on smooth |
|---|---|---|
| Effort | XL (two terrain authorities) | M–L |
| Risk | High: seams, dig handoff, hybrid physics | Medium: performance, near-surface look |
| What you get | Keeps the Terrain3D surface; existing gen-17 slots could gain caves | Caves, overhangs, digging and underground mode, all already proven; one authority; spec P5–P7 as designed |
| What you lose | Nothing now; long-term upkeep of two stacks | Terrain3D near rendering and editing on new worlds; existing gen-17 slots stay cave-less |

## 5. Recommendation

The lead recommends **B, gated by a benchmark.** B's first task is also A's first task, so nothing is wasted if the benchmark fails.

### First three tasks (B)

1. **Spike.**
   - Add a development flag in `world_size_launcher.gd` (around 1430–1500 and 1835) for a smooth backend with the runtime recipe, caves off.
   - Add a gen-17 2 m lattice cache to `RingTerrainSmoothBaseline`.
   - Parity test: the voxel zero-crossing must match `GetPresentationSurfaceHeight` at pads, seams and coasts, within the mesher's interpolation error.
   - Report density ms per chunk for gen-16 vs gen-17.
2. **Contract and persistence.**
   - A new backend id and a sampler contract that admits `EnableDesignedCaveDensityComposition`.
   - Placement policy v2: minimum surface over the cave footprint minus 64 m, plus the site exclusion (spec §3.5).
   - Save-manager identity for smooth runtime worlds.
3. **P6 site carve.**
   - Max-union `SiteCarve` in `SampleNaturalTerrainDensityWithSurface`.
   - Add `TryBoundCarve` in `TryClassifyUniformRegularRegion`.
   - Both reuse `EmitterSiteUnderground` (`scripts/Terrain/Sites/EmitterSiteUnderground.cs`).

### First three tasks (A)

1. The same spike as B task 1.
2. Generalise the aperture source so it feeds both the Terrain3D hole mask and the voxel aperture zones.
3. Hybrid physics and probe.

### Regression guards

Run these before and after every step.

**Must stay bit-identical:**

- `tools/emitter_site_baseline_test.gd` (gen16 and gen17 v4)
- `emitter_site_terrain_v5_test`
- `emitter_site_registry_test`
- `runtime_terrain_cpu_pass2_test`
- `terrain_generation_cpu_parity_test`
- `runtime_surface_definition_test`

**Behaviour:**

- `emitter_site_hole_test`
- `terrain3d_gameplay_stream_test`
- `emitter_site_underground_test`
- the smooth runtime smoke: `res://scenes/SmoothTerrainRuntimeSmoke.tscn -- --smooth-terrain-runtime-smoke`, run as a scene
- `smooth_terrain_overlap_ownership_test`
- `tools/run_cave_certification.ps1`
- the `underground_mode_*` smokes
- `--underground-mode-roundtrip-smoke-test --smooth-terrain-backend`

**New:** add a gen-17 smooth fingerprint fixture (chunk `SmoothTerrainMeshHash` plus density samples) in `tools/fixtures/`.

## 6. Open questions to answer first

1. Must the Terrain3D near-surface look and editing be kept, or is the smooth mesh with triplanar materials acceptable for gen-17 worlds?
2. Is it acceptable that only new worlds get caves, or must existing gen-17 save slots gain caves? Only A can do the latter in place.
3. Should designed caves avoid ocean basins entirely, or may they exist under deep seabeds behind a sealed roof?
4. Milestone 2 is paused with M2-1 and M2-2 done and M2-3 nearly done (see the status section of emitter-milestone2-plan.md). Its Terrain3D-specific steps (M2-4 onwards, the baked shells and the lift) only make sense if Terrain3D stays the runtime for site worlds. Choose A or B before continuing Milestone 2.

## Machine setup reminders

- .NET 10 SDK.
- The pinned engine is `artifacts/engine-upgrade/Godot_v4.8-dev7_mono_win64/`.
- Build with `dotnet build RingworldTechDemo.csproj -p:NuGetAudit=false -v:minimal`. The expected result is 0 warnings and 0 errors.
- Run headless tests with `<engine>_console.exe --headless --path . --script res://tools/<name>.gd`.
- A Godot run can delete `addons/terrain_3d/bin/~libterrain.ringworld.windows.debug.x86_64.dll`. Restore it with `git show HEAD:<path> > <path>`.
- Agents must never run `git stash`, `reset`, `checkout` or `apply -R` on the working tree. The user commits.

## USER DECISIONS (2026-10-15) — binding

- Direction: **B (gen-17 generation on the smooth voxel backend), gated by the spike benchmark**. If the spike fails its gate, revisit A; the spike is shared work.
- Near-surface look (Q1): judge after the spike, from side-by-side captures of gen-17 on the voxel mesh vs Terrain3D.
- Existing gen-17 slots (Q2): stay cave-less; only new worlds get caves.
- Ocean basins (Q3): designed caves may exist under deep seabeds behind a sealed rock roof.
- Milestone 2 (Q4): stays paused until the spike result; M2-4 onwards will be re-planned for B.
