# Terrain3D runtime generation implementation checklist

This checklist tracks [the runtime generation plan](terrain3d-runtime-generation-plan.md). The isolated Certified Ring reference generator now has a regional overview and a playable curved Terrain3D patch. Production terrain definitions, existing world slots, the native curved-culling patch, and the surface edit contract remain in place while the reference is evaluated.

The 2026-09-29 [terrain detail implementation](terrain-generation-detail-implementation.md) extends new recipes with foothills, spurs, and directional gullies while preserving existing world definitions. That document records the new controls, compatibility checks, and current performance limits; the original milestone evidence below remains historical.

## 1. Reference math and regional preview — implemented and validated

- [x] Add a typed, versioned preview recipe with explicit seed, field scales, noise settings, height bounds, and detail controls.
- [x] Snapshot and validate recipes before sampling; keep sampling independent of nodes, frame order, and mutable resources.
- [x] Preserve the Certified periodic noise-domain convention with double-precision wrapping before noise conversion; keep Mega outside this milestone.
- [x] Compose continental basins/shelves/coasts, anchored sea level, flattened lowlands, mountain foundations/ridges, smooth valley corridors, and rare trenches.
- [x] Restrict secondary relief and microdetail to exposed alpine crests; make protected-basin detail exactly zero.
- [x] Keep heights finite and within -8,000 to +8,000 m by allocating headroom; report guard-clamp frequency.
- [x] Add a regional overview spanning 256–512 km around the ring and 318 km across at an initial 512 m sample interval.
- [x] Provide height, zone-weight, detail-mask, and slope views plus representative coast/basin/valley/alpine locations and a selectable 2 m detail sample.
- [x] Extend the existing grave-key Terrain3D menu with preview recipe controls; persist the versioned preview recipe separately from gameplay preferences and world slots.
- [x] Debounce recipe changes and cancel superseded preview revisions.
- [x] Test repeatability, seed variation, sampling-order independence, wrapping (including warped fields), region/lattice addressing, smooth transfer boundaries, bounds, and detail suppression.
- [x] Record regional elevation/coverage/slope distributions and inspect the rendered preview. Statistical and visual outputs are evidence for tuning, not final geography acceptance.

## 2. RF packing, native adapter, and playable curved patch — implemented and validated

- [x] Produce row-major `PackedFloat32Array` buffers and RF images with one common frame-height offset; preserve absolute canonical heights separately.
- [x] Test negative/fractional heights near both range extremes, byte counts, region pitch, negative indices, shared boundary samples, and wrap/rebase orientations.
- [x] Add the preview to the existing C# curved surface/frame/collision owner through bulk handoffs; avoid per-sample cross-language calls.
- [x] Compare direct `Terrain3DRegion` creation/batched updates with the existing import path on the pinned addon.
- [x] Preserve color/control maps, cached image ownership, edited/modified flags, region bounds, and aggregate culling bounds.
- [x] Publish matching render/query/collision revisions and provide a small playable preview patch.
- [x] Run native import, curved collision, and GPU visibility regressions. Preserve the Windows culling patch.

## 3. Bounded asynchronous preparation — implemented and validated

- [x] Private job/result types with immutable recipe, edit, and frame inputs.
- [x] One latest window, nine pending tiles, 1–4 workers (default 2), completion polling, and controlled joins.
- [x] Stale-revision rejection, canonical tile reuse, and a 1–64 MiB absolute 128² float32 LRU payload cache (default 8 MiB).
- [x] Main-thread-only nodes, Terrain3D resources, GPU publication, and physics installation.
- [x] One hidden disabled-collision staged replacement while active render/query/collision remains usable, activated at a physics boundary.
- [x] Collider-overlap priority, actual canonical velocity prediction, rolling 32-sample readiness estimates, 64 m default prefetch (16–160 m), and ±176 m safe coverage boundary.
- [x] Explicit destination staging for teleports, stable stationary handoffs, reversal cancellation, and a supported boundary pause when preparation is late.
- [x] Tests for tuning/edit/rebase races, invalid recipes, cancellation, bounded queues/cache, staged replacement, preference isolation, and controlled teardown.

## 4. Collision and upload optimization — implemented and validated

- [x] Measure sampling, RF packing, native setup/upload, curved vertex projection, shape construction, and installation separately.
- [x] Expose upload/collision budgets and 16/32-cell collision tiles through the existing tuning/preferences path.
- [x] Compare two/four workers on the recorded CPU, with isolated first-visit and return routes.
- [x] Move curved vertex projection into the existing bounded packing job; keep nodes, native data, GPU uploads, physics shape creation, and installation on the main thread.
- [x] Replace the monolithic collider with shared-edge curved tiles after measuring shape/installation spikes; retain the original footprint, triangles, and winding.
- [x] Budget region uploads and shape installation across frames; reuse one inactive native owner and its map identities, with at most two residents.
- [x] Historical M4 decision: retain the validated GDScript height model for the isolated preview because its measurements justified collision/publication changes, not a second preview implementation. M5 later promotes the shared C# authority for its frozen gameplay definition; this is an identity decision, not a performance claim.
- [x] Report latency/frame percentiles, largest hitch, height bytes uploaded, queue/cache/resident bounds, and missed-coverage waits. Preserve supported traversal while a late replacement completes.

## 5. Shared definition and persistence integration — implemented and validated

- [x] Add the opt-in Certified-only `surface_runtime_generation_v1` backend. Existing SmoothTerrain, legacy voxel, and `terrain3d_surface_v1` worlds retain their prior definitions and stores.
- [x] Freeze the canonical shared C# recipe at world creation, seeded from the launcher and copied from the saved preview recipe (or the default recipe when absent). Existing runtime-generation worlds require their stored canonical recipe, contract, dimensions, and identity metadata before loading or saving.
- [x] Version runtime terrain identity: generator v17 versus legacy v16, a version-10 proxy cache, a version-5 pyramid, `world_state_surface_runtime_v1.json`, and `surface_runtime_edits_v1.json`.
- [x] Define the runtime surface contract: terrain -8,000 to +8,000 m, sea level 0 m, reserved build ceiling 8,400 m, structural wall top 10,400 m, no caves or natural overhangs. Retained volumetric-domain metadata remains owned by older backends.
- [x] Route the new field through the fractional-height presentation/edit consumers and use the same 2 m positive-diagonal collision lattice as the surface field.
- [x] Validate fractional heights with shared-authority, field, native curved-collider, near-proxy, tree-root, and shadow-pyramid fixtures; review the far-mesh, minimap, water, and weather query paths. Full-environment visual and performance acceptance remains milestone 6. Near Terrain3D atmosphere, cloud-shadow, and sun/planet macro-terrain shadow material integration are implemented. See [near-surface shading parity](terrain3d-surface-port.md#near-surface-shading-parity).

### Milestone 5 implementation status

Milestone 5 introduces an opt-in gameplay surface backend for Certified worlds. Advanced launcher item 2 selects `surface_runtime_generation_v1`; the original Terrain3D surface remains item 1 and legacy defaults are unchanged. The launcher copies the saved preview recipe, applies the launcher seed, canonicalizes it through the shared C# authority, and freezes it in world settings. Existing runtime-generation worlds cannot change backend, seed, dimensions, or geography controls, and missing, malformed, or mismatched definition metadata fails closed.

The new backend uses generator v17 while legacy definitions remain v16. It owns `world_state_surface_runtime_v1.json` and `surface_runtime_edits_v1.json`, separate from the retained Terrain3D, SmoothTerrain, construction, and voxel journals. Its field, presentation consumers, and 2 m positive-diagonal curved collision use fractional heights. The policy is terrain -8,000 to +8,000 m, sea level 0 m, reserved build ceiling 8,400 m, wall top 10,400 m, surface only, no caves, and no natural overhangs.

The grave-key Terrain3D panel retains brush, mesh, and movement controls, while showing the immutable runtime policy and definition hash. Geography controls remain in the preview and affect a new world only. M6 applies bounded asynchronous window preparation to both gameplay Terrain3D backends while retaining their generation and save contracts; the synchronous path is only the guarded benchmark fixture.

Validation passed: shared math 79,926 comparisons (maximum error 4.5e-13 m), stream 102, reference 19,397, budget 64, definition 802 (including 64 parallel worker copies and actual curved-collider triangles), and .NET build with zero warnings/errors. Legacy launcher/settings/gameplay smoke, field/LOD bridge, radial morph, FFT shoreline/distant-ocean, proxy normals/seams, and shadow geometry passed. The full GPU runtime-world smoke passed five boots covering save/reload, corrupt and mismatched definitions, immutable backend/controls, and protected legacy data, with no worker-handle or failed-save errors. The playable preview passed 43 GPU checks after promotion to shared C# math. A separate GPU capture fixture verified the initialized grave-menu recipe hash and policy, and its captures were inspected. These fixtures disable selected expensive environment nodes and do not establish complete environment or performance acceptance.

The runtime definition tests also cover fractional shorelines on both sides of sea level, full-range edits, immutable snapshots, conservative shadow bounds and clearing, cache round trips, material/solid queries without the old protected-roof clamp, and failed recipe/edit identity checks. World teardown records whether edit persistence succeeded before allowing the final player-state write. Expected corrupt-input diagnostics and the existing optional voxel-mesher/certificate warnings are separate from test failures.

Reports and captures are under `.godot/terrain-generation-tests/`; the runtime world fixture creates a fresh `m5-world-*` directory. Run `terrain_generation_world_smoke.gd` for the full save/reload suite; add `-- --capture --capture-only` for a single-world terrain/menu capture in neutral fixture lighting. This capture is a rendering/UI check, not geography acceptance. The generator remains opt-in until milestone 6 acceptance.

## 6. Gameplay streaming — implemented; performance and human acceptance remain open

The September 2026 terrain and cloud bug pass is recorded in [terrain-rendering-fixes.md](terrain-rendering-fixes.md). Its implementation and focused contract coverage do not close Milestone 6: full-scene performance measurement and human visual acceptance remain open.

The follow-up fixes bound structural wall requests across coordinate zero and move global segment sampling to a background worker while preserving replacement coverage. The final 512 m outward/return walk at 12 m/s completed all global replacements, passed 5,161 support probes without a coverage wait, and measured 24.53 ms median / 48.74 ms p95 / 73.28 ms maximum at 1080p with half-resolution clouds. Full measurements, quality changes, remaining allocation costs, and test commands are in the bug-pass report above.

- [x] Use private worker-pool jobs to generate exact height samples and RGBA8 biome payloads without accessing active Terrain3D, scene, or physics state.
- [x] Cache immutable 128 × 128-sample tiles at 2 m spacing (256 m per side), carrying heights and RGBA8 biome data, with an 8 MiB default limit and initial-window priming; reuse six of nine tiles for compatible 256 m axial recenters.
- [x] Pack RF payloads and project curved collision geometry in workers; publish through two prewarmed native owners with distinct active/staged rebased world transforms.
- [x] Expose and persist the six grave-key generation controls: workers, prefetch, cache, upload budget, collision budget, and collider tile size. Defaults are 2 workers, 64 m prefetch, 8 MiB cache, 1 ms upload, 0.75 ms collision, and 32-cell tiles.
- [x] Keep the active render/query/collision revision while a staged replacement prepares; use supported-boundary movement holds when a deadline is missed.
- [x] Apply edits immediately to the authoritative field and persist them through the existing save path; debounce replacement generation for 100 ms while render plus committed collision/query state remain on the published revision. Public field queries may observe pending edits.
- [x] Provide a guarded synchronous baseline only for the `m6-*` fixture directory, so normal world slots cannot enable it.
- [x] Pass stream test (114 checks, including worker limits during cancellation), native gameplay test (20 checks including physical rays through both transforms), legacy gameplay smoke, runtime world smoke, settings persistence, and a .NET build with zero warnings/errors.

A repeatable travel fixture is available for sequential synchronous-baseline and asynchronous runs with per-viewport CPU/GPU profiles. The clean comparison used 1920 × 1080 Forward+, VSync 1, an RTX 4070, Intel Core i9-14900KF, Godot 4.7.2 .NET, seed 1337, runtime generator v17 with the default recipe, and origin around 32/across 159032. The complete environment was enabled. Each run used a 120-frame stationary warmup followed by 256 m outward and 256 m return travel through the normal controller, with swimming disabled for ground traversal.

| 60 m/s comparison | Synchronous baseline | Asynchronous gameplay |
| --- | ---: | ---: |
| Measured frame intervals | 337 | 338 |
| Frame p50 | 31.395 ms | 31.343 ms |
| Frame p95 | 45.086 ms | 43.024 ms |
| Frame p99 | 175.028 ms | 167.334 ms |
| Worst interval | 1255.689 ms | 473.281 ms |
| Intervals above 100 ms | 11 | 10 |
| Intervals above 500 ms | 4 | 0 |
| Support ray misses / samples | 0 / 713 | 0 / 713 |

Reports are `.godot/terrain-generation-tests/m6-4057459/travel-benchmark.json` (synchronous) and `m6-4082986/travel-benchmark.json` in the same directory (asynchronous). The baseline's three movement rebuilds each took 1018–1039 ms: about 644–659 ms for biome samples, 126–133 ms for heights, 155–157 ms for native setup, 11 ms for upload, and 79–80 ms for collision. These synchronous rebuilds explain the repeatable large terrain stalls.

The asynchronous run recorded zero coverage waits, 39 cache hits/6 misses, and 1,966,080 cached bytes under the 8 MiB limit. Maximum request-to-ready latency was 1431.666 ms across multiple frames; the largest measured preparation step was 6.7258 ms and the largest Begin call was 3.2523 ms. These are separate measurements, not additive frame totals. Outward/return frame p50 were 29.297/32.916 ms and p95 were 43.079/43.017 ms; the JSON retains both phases and their cache counters.

Additional sequential asynchronous runs used the same short route and environment:

| Speed | Intervals | Frame p50 / p95 / p99 | Worst interval | Support misses / samples | Coverage waits | Report directory |
| --- | ---: | --- | ---: | ---: | ---: | --- |
| 12 m/s | 1142 | 36.204 / 52.342 / 68.591 ms | 176.880 ms | 0 / 2602 | 0 | `m6-4264650` |
| 108 m/s | 298 | 30.715 / 61.209 / 167.940 ms | 529.046 ms | 0 / 645 | 0 | `m6-4106451` |

Each report directory is under `.godot/terrain-generation-tests/` and contains `travel-benchmark.json`. The 12 m/s run includes the final diagnostic and cancellation-admission correction; the 60/108 m/s runs preceded that correction, which does not change ordinary sampling or publication. The 108 m/s fixture also exited with a packing job in flight, exercising controlled teardown.

The largest residual 60/108 m/s spikes occurred after publication while the terrain scheduler was idle; their exact cause is not established. Monitor correlation is approximate and may not include custom compute work. Earlier concurrent-game figures are excluded. These short routes do not establish 60 FPS acceptance, the production flight envelope, broad edit/travel coverage, or human/geography acceptance.

### Remaining M6 acceptance

- [x] Record the local CPU/GPU configuration and 1920 × 1080 settings for the sequential comparison above.
- [x] Run short fixed-seed outward/return routes at 12, 60, and 108 m/s with the complete environment and separate phase/cache diagnostics.
- [ ] Define the target production CPU and flight envelope; extend travel/edit routes across representative terrain and that envelope.
- [ ] Repeat longer first-visit and warm-cache routes to establish consistent tail latency and memory/task bounds.
- [ ] Meet the 60 FPS target with reported CPU/GPU timings and frame p50/p95/p99, without unexplained hitches.
- [ ] Demonstrate no terrain holes, unsupported collision handoffs, stale edit publication, save regressions, or unbounded memory/task growth.
- [ ] Obtain human visual acceptance at ground, flight, and ring-overview scales before promoting the generator.

## Validation record

Milestones 1–4 are implemented in the isolated Certified preview. M5 adds the opt-in gameplay runtime definition, field edits, and world-save integration without replacing older backends. M6 applies its private immutable jobs, sample/native-window reuse, cancellation, predictive prefetch, curved collision tiles, and budgeted staged publication to runtime gameplay. Mega support, caves, near Terrain3D macro/cloud/atmosphere material integration, GPU geography inspection, and M6 full-stack performance acceptance remain open. The milestone records below describe each stage at the time it was validated; milestone 4 supersedes the synchronous publication details in milestone 3.

The initial validation machine has an Intel Core i9-14900KF and an NVIDIA GeForce RTX 4070 (driver 610.88), running Godot 4.7.2 .NET. These are the local test conditions, not a full-game performance result.

| Check | Result |
| --- | --- |
| .NET build | Passed with zero warnings and zero errors. |
| Reference math suite | Passed 19,397 checks, including periodicity, deterministic sampling, recipe validation, protected masks, height bounds, and headroom. |
| Full regional GPU preview smoke | Passed 37 checks at 1920 × 1080 using Vulkan Forward+, including revision cancellation, diagnostic views, save/reload, corrupt and incompatible recipe protection, reset, tuning controls, and scene teardown. |
| Existing surface settings regression | Passed. |
| Existing Terrain3D surface pilot regression | Passed, including nine native regions, curved collision, 45 edits, recentering, and reload. |
| Visual review | Inspected the overview and a separate open/expanded/closed tuning-drawer capture; the map remained visible and the expanded recipe controls were readable. |

The default seed 1337 overview contains 311,622 samples over 256 km around the ring and 318 km across. It measured -3,605.26 m to +5,322.10 m with zero guard clamps. Ocean samples accounted for 65.44%, lowlands for 29.97%, and alpine terrain for 4.59%. Overview finite-difference slopes measured 2.19° at p50, 13.29° at p95, and 72.04° maximum. These coarse slopes are tuning diagnostics; final geography acceptance remains open.

The initial overview and selected 16,641-sample detail patch completed in approximately 2.87 seconds, including frame yielding. This is preview generation latency, not a 60 FPS gameplay measurement. Reports are written to `.godot/terrain-generation-tests/reference-math-report.json` and `preview-smoke-report.json`; GPU captures and the separate drawer diagnostic are stored in the same ignored directory. These measurements record the milestone 1 full-overview run; subsequent targeted smoke runs overwrite their report with a smaller fixture.

### Milestone 2 validation

| Check | Result |
| --- | --- |
| RF window packing | Passed 108 checks for ordering, pitch, byte counts, common offsets, fractional/extreme heights, invalid inputs, shared canonical samples, wrapping, and reversed across direction. |
| Native adapter | Passed 78 headless checks and 80 GPU checks, including direct/import parity, cached map identity, paint preservation, exact bounds, revision rejection, and curved physics/query agreement. |
| Playable preview | Passed 36 headless checks and 39 GPU checks, including grounded movement, flight/menu input, patch boundaries, tuning/persistence, debounce, both scene-navigation directions, unsaved recipe handoff, corrupt-save protection, and teardown. |
| Prepared terrain GPU visibility | Passed 10 checks covering all four ring quarters with a translated world origin; inspected a rendered capture. |
| Existing regressions | Surface frame, height field, tuning preferences, surface pilot, and four-quarter GPU visibility passed. The regional overview smoke passed 35 targeted headless checks after navigation changes. |
| Build and visual review | .NET build passed with zero warnings/errors. Inspected the playable terrain and open tuning drawer at 1920 × 1080 using Vulkan Forward+. |

The prepared window contains 147,456 absolute float32 samples and nine 128 × 128 RF images at 2 m spacing. Every image uses the same frame-height offset. A single bulk call transfers the window to the existing C# surface, which publishes matching rendering, height-query, and curved-collision revisions. Invalid or stale input is rejected before native mutation. Successful direct updates retain native region and height-image identities, preserve control/color maps, consume edited flags, and retain modified flags.

Exact per-region bounds also handle flat zero and wholly negative RF windows. The adapter supplies these bounds explicitly because the pinned addon's minimum/maximum helper initializes its maximum to a positive `FLT_MIN`, which overestimates the maximum for those windows. Aggregate bounds are refreshed for culling; the existing patched Windows addon binaries remain unchanged.

Direct region updates are the default. A local GPU fixture measured initial/update upload times of **4.60 / 2.21 ms** for one batched direct update, versus **9.41 / 8.72 ms** for nine imports. Both paths passed the correctness checks. These are historical milestone 2 fixture observations, not a benchmark distribution. Collider rebuilding took approximately 61–68 ms in that fixture. One tuned playable-window run measured **927 ms sampling**, **7.2 ms packing**, **2.0 ms upload**, and **83 ms collision rebuilding**. Validation and other overhead are additional. These costs motivated asynchronous preparation in milestone 3 and the remaining upload/collision optimization in milestone 4; no gameplay frame-rate target has been accepted.

Reports are `terrain-generation-window-report.json`, `native-adapter-report.json`, `terrain-generation-playable-report.json`, and `prepared-visibility/report.json` beneath `.godot/terrain-generation-tests/`. The playable GPU captures are `playable-terrain.png` and `playable-drawer.png`. Reports describe the most recent invocation; GPU-only checks and captures require the GPU commands below.

The corrupt-recipe fixture intentionally emits a resource parse error. The local Godot runs also reported the existing missing optional voxel-mesher library and sandbox certificate-store access messages; those did not prevent the checks above from passing.

### Milestone 3 validation

| Check | Result |
| --- | --- |
| Bounded worker and cache suite | Passed 102 checks: synchronous-reference/RF parity, immutable inputs, wrapped and reversed coordinates, recipe/edit/frame identity, six-tile recenter reuse, LRU pressure, cancelled pack work, rapid supersession, changing concurrency, and joining every retained task. |
| Native staging | Passed 52 headless checks: active collision stays usable during staging, hidden candidate collision stays disabled, invalid/stale stages preserve valid replacements, activation reuses the prepared shape, revision agreement, world-frame forwarding, coverage tolerance, bounded residents, and retirement. |
| Streaming playable fixture | Passed 50 GPU checks: fresh defaults and persistence, recipe debounce, repeated automatic recenters, stationary handoff stability, actual-velocity prefetch, staged overlap waiting, reversal cancellation, superseding teleports, edit/rebase races, boundary support, invalid-recipe recovery, preference isolation, and teardown during work. The prior 48-check fixture also passed headlessly. |
| Existing playable flow | Passed 43 GPU checks including selected-coordinate initialization and terrain/recipe/streaming-drawer captures. Covers grounded physics, both navigation directions, unsaved recipes, input ownership, protected saves, and preference isolation. |
| GPU staged visibility | Passed 22 checks across all four ring quarters, including translated/rotated world frames. Inspected terrain and the expanded streaming/travel drawer at 1920 × 1080, Vulkan Forward+, RTX 4070. |
| Regressions and build | Native direct/import adapter passed 78 headless checks; existing surface pilot and preferences smoke checks passed. Final .NET build passed with zero warnings/errors. |

Sampling jobs own recipe and absolute-edit snapshots, noise instances, and float buffers. A separate worker assembles the nine tiles and packs RF images with a common height offset. The scheduler retains cancelled tasks until they complete and are joined; controlled scene teardown cancels and joins all remaining tasks. It permits one latest requested window, at most nine pending tile descriptors, up to four retained worker tasks including packing, and one completed payload. Reducing the worker setting lets existing tasks drain before dispatching more work.

The cache stores immutable absolute 128 × 128 float32 tiles, separately from frame-specific RF images. A compatible 256 m axial recenter reuses six of nine tiles. Recipe/edit identity changes invalidate incompatible entries; frame changes can reuse canonical heights. The configurable cache limit counts sample payload bytes only. Worker copies, RF images, native maps, GPU arrays, and collision memory are additional, bounded by the job/window counts rather than included in that byte limit.

At milestone 3, the surface owner kept at most one active window and one hidden candidate with collision disabled. Sampling and RF packing happened off the main thread, while native map creation/upload, curved vertex projection, shape construction, and installation happened synchronously on the main thread. A physics-boundary handoff published matching rendering, height-query, and collision revisions, then freed the old window. Milestone 4 below replaces those synchronous preparation steps and retains one inactive owner for reuse. A failed request continues to preserve active terrain and report the error.

Prediction uses actual canonical displacement and `max(prefetch, speed * readiness + 16 m)`, with readiness conservatively bounded below by 1.5 seconds and otherwise using the maximum of the last 32 measured readiness samples. This is a provisional estimate, not a measured p99 guarantee. Deliberate waiting outside a prepared window's usable overlap is excluded from preparation latency. The player remains inside the active ±176 m safe area if a deadline is missed. Coordinate travel stages the destination while retaining current support. Abrupt reversals cancel an obsolete forward request.

One milestone 3 standalone headless stream fixture measured **739.199 ms** for its first CPU window and **269.248 ms** for a compatible axial recenter, with **0.191 ms** maximum polling time and **0.003 ms** cancellation-request time. Its pressure case reached the configured 1 MiB sample-cache cap (16 tiles), evicted four tiles, and joined all 129 submitted tasks. These observations exclude native upload/collision and are not full-scene frame-time measurements.

Reports are `stream-report.json`, `staging-report.json`, `m3-streaming/report.json`, and `prepared-visibility-staged/report.json` under `.godot/terrain-generation-tests/`. The GPU playable report uses `terrain-generation-playable-report.json`; captures include `playable-terrain.png`, `playable-drawer.png`, and `playable-streaming-drawer.png`. GPU runs also logged sandbox shader-cache write errors while the rendering checks passed. Human acceptance of geography and full-game performance remains open.

### Milestone 4 validation and measurements

The existing bounded packing worker now creates a private `RingTerrain3DCollisionGeometry` using the same curved frame and canonical samples as rendering and queries. Projection uses one shared 193 × 193 vertex grid, then partitions the original 73,728 triangles into 36 tiles of 32 cells or 144 tiles of 16 cells. No physics shape or scene node is created by a worker. Each request still carries one canonical window, nine RF images, and at most one projected geometry payload; triangle vertex payload is 2,654,208 bytes, excluding the private height copy and temporary grid.

`BeginBudgetedPreparedWindow()` validates/copies a complete result before replacing a pending candidate. `AdvanceBudgetedPreparation()` advances native setup, region uploads, array initialization, individual shape creation/installation, excess-shape retirement, and final verification. Positive budgets allow at least one indivisible unit before checking elapsed time; zero pauses that category in the API. The tuning UI exposes positive budgets only. Validation/copying at Begin is separately measured and is not covered by the upload timer. Cancellation drops unpublished payloads while preserving the active surface.

The owner retains at most one active and one staged **or idle** native surface. Reusing the inactive surface preserves resident height/control/color image identities, updates one edited height layer at a time, and avoids rebuilding native texture arrays after initial allocation. Color/control contents remain intact. Activation publishes all revisions together at a physics boundary, enables the prepared body, and hides/disables the previous body for reuse. Changing tile size grows or retires shapes only on that inactive owner. The compatibility API remains available; both gameplay backends use the asynchronous path outside the guarded benchmark fixture.

Measurements below were collected on September 25–26, 2026 with an Intel Core i9-14900KF (32 logical CPUs), RTX 4070 / driver 610.88, Godot 4.7.2 .NET, Vulkan Forward+. The existing Godot editor remained open at approximately 1.5 GiB working set. Benchmarks ran individually; correctness tests ran separately. Although launched with `--resolution 1920x1080`, the actual render/window size was **1920 × 1061**, as recorded in the reports. These runs do not meet the final true-1080p acceptance requirement. They use the isolated preview without production weather, water, or vegetation, and provide wall-clock process-frame intervals rather than GPU timestamp measurements.

The synchronous baseline measured eight replacements after one warmup. Each replacement created a fresh native surface and monolithic collider. GPU-run native setup was about 74.5 ms, shape construction 37.5 ms, and installation 33.3 ms at the reported p50. The complete synchronous stage call was 155.949 ms p50 / 161.230 ms maximum; its following frame interval was 272.141 ms p50 / 278.571 ms maximum. Headless stage time was 115.196 ms p50 / 117.252 ms maximum. These measurements justified smaller collision units and native-owner reuse.

The incremental comparison retains both initial cold owners in raw output, then measures eight replacements using the two reusable owners. Frame statistics include every preparation step and activation frame; preparation wall time spans multiple frames and is reported separately.

| Reused native publication | 16-cell tiles | 32-cell tiles (default) |
| --- | --- | --- |
| Begin validation/copy wall time, p50 / max | 2.170 / 3.676 ms | 2.288 / 2.584 ms |
| Advance call wall time, p50 / p99 / max | 0.827 / 1.116 / 1.505 ms | 1.201 / 1.483 / 1.536 ms |
| Complete preparation wall time, p50 | 816.837 ms | 632.813 ms |
| Activation call, p50 / max | 0.140 / 0.181 ms | 0.101 / 0.130 ms |
| Frame intervals, count | 403 | 312 |
| Frame intervals, p50 / p95 / p99 / max | 16.685 / 18.902 / 20.876 / 22.857 ms | 16.661 / 18.996 / 21.077 / 24.480 ms |

For 32-cell reuse, per-window native setup/settings cost 1.021 ms p50, layer uploads 0.240 ms, shape construction 22.084 ms, and installation 20.063 ms. Shape and installation totals are spread over frames. Default 32-cell tiles reached readiness sooner than 16-cell tiles, while keeping individual measured Advance calls under 1.54 ms. A single 32-cell shape/install unit exceeded the nominal 0.75 ms collision budget, which diagnostics report as an overrun. This is scheduling granularity, not a hard frame-time guarantee.

Each complete window uploads **589,824 bytes (576 KiB) of RF heights**. This excludes control/color allocation, clipmaps, GPU storage, collision acceleration structures, and managed copies. Reuse retained two native owners. The CPU-only pipeline within the GPU benchmark measured cold axial-recenter latencies of about 300 ms with two workers and 217 ms with four, reusing six of nine tiles. Fully cached revisits took about 33.3 ms, including frame polling, RF repacking, and collision projection. Packing cost about 11.3 ms and projection about 6.2 ms per window; the largest measured main-thread scheduler poll was 0.149 ms. These results support keeping two workers as the conservative default and retaining the reference height sampler.

The travel benchmark drives canonical positions through the real playable controller at **108 m/s**, measures actual displacement after clamping, and records each process-frame interval independently of physics ticks. Every route covers 1,024 m outward and 1,024 m back with seed 1337. Initial loading is excluded; first allocation of the second native owner is included in the outward run. Warm return routes may still sample extra lookahead tiles beyond the visited endpoints.

| Workers / route | Frames | p50 / p95 / p99 / max frame interval | Edge waits | Support failures |
| --- | --- | --- | --- | --- |
| 2 / first visit | 563 | 16.674 / 18.295 / 20.432 / 193.502 ms | 0 | 0 |
| 2 / return | 569 | 16.654 / 18.610 / 20.119 / 22.856 ms | 0 | 0 |
| 4 / first visit | 564 | 16.661 / 18.345 / 19.737 / 194.371 ms | 0 | 0 |
| 4 / return | 569 | 16.665 / 18.837 / 20.594 / 22.398 ms | 0 | 0 |

The two-worker route used at most two in-flight tasks, five pending descriptors, 1,769,472 bytes of canonical cache payload, and two native residents. The four-worker route observed at most three in-flight tasks during travel; its initial nine-tile request separately used the configured concurrency. All tasks joined at teardown. The two-worker process static-memory monitor peaked at approximately 139 MiB; this is not total process/GPU memory. Support probes ran at roughly 50 ms intervals and found no missing active query/collider coverage in either configuration.

`--force-late` deliberately selects 16-cell tiles and a 0.1 ms collision budget. At 108 m/s with two workers, it produced four supported boundary waits outward and four on return, completed both actual 1,024 m routes, and passed all 379 support probes. This verifies the missed-deadline fallback under budgeted preparation rather than assuming prefetch always succeeds.

First-use setup remains a known performance limit: cold native setup units took approximately 75–163 ms in the incremental fixture, and the first outward travel runs reached approximately 194 ms for a complete frame. Native allocation and initial texture-array creation are indivisible in this implementation. Reuse avoids paying that cost on subsequent handoffs, but warming both owners before releasing initial traversal or subdividing native setup remains future performance work. GPU timestamps, lock-wait profiling, longer memory runs, exact 1080p, and the full environment workload remain milestone 6 acceptance work. These small samples are local evidence, not a full-game 60 FPS claim.

| Check | Result |
| --- | --- |
| Budgeted native/collision suite | Passed 64 headless checks: tiny/zero budgets, invalid inputs, hidden/noncollidable preparation, fractional slopes and tile-edge rays, wrapped/reversed/rotated frames, partial-shape cancellation, owner/map reuse, paint preservation, tile growth/retirement, revision agreement, bounded residents, and teardown. |
| Worker/cache suite | Passed 102 headless checks with worker-projected geometry enabled. |
| Synchronous staging compatibility | Passed 52 headless checks. |
| Streaming playable fixture | Passed 56 headless checks, including all three new controls, safe tile-size replacement with nine cached tiles, persisted budget/tile settings, preference isolation, and shutdown. |
| GPU budgeted visibility | Passed 30 checks across four rotated/transformed ring quarters, alternating tile sizes and reusing both owners. Inspected a captured frame. |
| Playable flow and tuning drawer | Passed 43 GPU checks. Inspected the expanded streaming drawer: all new controls and diagnostics are readable, with remaining travel controls accessible by scrolling. |
| Native adapter / legacy surface paths | Direct/import adapter passed 78 checks; existing surface preferences and surface pilot smoke passed. |
| Build | .NET build passed with zero warnings/errors. |

Benchmark reports are `m4-performance-baseline-gpu.json` and `m4-performance-incremental-gpu.json` under `.godot/terrain-generation-tests/`; these archived copies preserve the measured runs. The performance tool itself writes `m4-performance-baseline.json` or `m4-performance-incremental.json`. Travel reports are `m4-travel/108-2/report.json`, `108-4/report.json`, and `108-2-late/report.json`, with isolated recipes/preferences in each directory. Correctness reports include `budgeted-report.json` and `prepared-visibility-budgeted/report.json`; the existing streaming/playable report paths remain as above. Run GPU benchmarks sequentially and retain each report before rerunning a mode.

## Open the reference preview

Open `scenes/TerrainGenerationPreview.tscn` in Godot and press **F6**. The overview samples 256 km around the Certified Ring and its 318 km width at 512 m intervals. Click the overview to inspect a 129 × 129 sample patch at 2 m spacing. Height, zone weights, detail-mask, and slope views expose different parts of the reference model.

Press the **grave key** and use the existing **Terrain3D** tab to tune the preview recipe. The current coastal-v2 recipe is isolated at `user://terrain_generation_preview/recipe_v2.tres`; it does not select a generator for an existing world slot. Frozen v1 worlds keep their original recipes.

Use **Save recipe** to persist changes. An unreadable or incompatible saved recipe is protected from overwrite until **Reset recipe** is explicitly selected.

## Explore the playable patch

Select a point in the overview and choose **Explore selected patch**, or open `scenes/TerrainGenerationPlayable.tscn` directly with **F6**. The surface uses the reference recipe and the existing curved Terrain3D frame/collider. Movement prepares terrain ahead and recenters in 256 m steps as replacement coverage becomes usable. Each active window has a 352 × 352 m safe traversal area within its 384 × 384 m collider; its nine visual regions have a 768 m pitch span. If preparation falls behind, movement pauses at the supported boundary until the next window is ready.

| Input | Action |
| --- | --- |
| Click outside the tuning drawer | Capture the mouse. |
| Mouse / `W A S D` | Look / move. |
| `Shift` / `Space` | Sprint / jump while walking. |
| `F` | Toggle flight. |
| `Q` / `E` | Descend / ascend in flight. |
| `Esc` | Release the mouse. |
| Grave key | Open/close the live tuning drawer. |

The **Terrain3D** tab provides the same recipe controls and save/reset actions as the overview. Changes debounce for 0.35 seconds before asynchronous regeneration. The **Playable** tab provides:

| Control | Range / default |
| --- | --- |
| Movement speed | 2–60 m/s; default 12 m/s. Sprint multiplies speed by 1.8. |
| Mesh detail levels | 2–6; default 4. |
| Generation workers | 1–4; default 2. |
| Prefetch distance | 16–160 m; default 64 m. Actual velocity/readiness can increase the effective lookahead. |
| Generation cache | 1–64 MiB of canonical sample payload; default 8 MiB. |
| Upload budget | 0.1–8 ms per process frame; default 1 ms. An individual native operation can overrun. |
| Collision budget | 0.1–8 ms per process frame; default 0.75 ms. An individual shape/install operation can overrun. |
| Collider tile cells | 16 or 32 cells per side; default 32. Changes stage a replacement without changing the recipe or invalidating canonical samples. |
| Travel to coordinates | Finite around coordinate, wrapped around the Certified circumference; across coordinate 80–317,664 m. Stages the destination before moving the player. |
| Return to overview | Carries current position and unsaved recipe back to the regional preview. |

Movement, mesh, worker, prefetch, cache, publication-budget, and tile-size preferences use the existing surface preference resource at the isolated `user://terrain_generation_preview/playable_preferences.tres` path. The HUD reports generation, travel, waiting, and error state. The expanded Terrain streaming section shows preparation phase/tile progress, largest preparation step, pending/in-flight jobs, cache payload, per-window upload/shape/install totals, budget overruns, and edge waits. `get_preview_diagnostics()` exposes these alongside active/requested revisions, readiness estimates, actual velocity, and worker/cache/native diagnostics.

Navigation carries the selected location, unsaved recipe, and corrupt-save protection in memory. It does not implicitly save the recipe or modify existing world definitions. This preview provides walking/flying and recipe tuning; brush editing is available in Terrain3D gameplay and the surface pilot. To use a preview recipe in a new gameplay world, save it, create an empty Certified slot, and select **Terrain3D runtime generation (Certified only)** in Advanced. The launcher applies the slot's seed and freezes the recipe for that world.

## Run the targeted checks

Targeted checks use the project's Godot .NET executable:

```powershell
$Godot = 'C:\path\to\Godot_v4.7.2-stable_mono_win64_console.exe'
dotnet build RingworldTechDemo.csproj --no-restore
& $Godot --headless --path . --script res://tools/terrain_generation_shared_math_test.gd
& $Godot --headless --path . --script res://tools/runtime_surface_definition_test.gd
& $Godot --headless --path . --script res://tools/terrain_generation_world_smoke.gd
& $Godot --path . --resolution 1920x1080 --script res://tools/terrain_generation_world_smoke.gd -- --capture --capture-only
& $Godot --headless --path . --script res://tools/terrain_generation_reference_test.gd
& $Godot --headless --path . --script res://tools/terrain_generation_preview_smoke.gd
& $Godot --path . --resolution 1920x1080 --script res://tools/terrain_generation_preview_smoke.gd -- --full-overview --capture
& $Godot --headless --path . --script res://tools/terrain_generation_window_test.gd
& $Godot --headless --path . --script res://tools/terrain_generation_native_adapter_test.gd
& $Godot --headless --path . --script res://tools/terrain_generation_playable_smoke.gd
& $Godot --headless --path . --script res://tools/terrain_generation_stream_test.gd
& $Godot --headless --path . --script res://tools/terrain3d_gameplay_stream_test.gd
& $Godot --headless --path . --script res://tools/terrain3d_gameplay_smoke.gd
& $Godot --headless --path . --script res://tools/terrain_generation_staging_test.gd
& $Godot --headless --path . --script res://tools/terrain_generation_streaming_playable_test.gd
& $Godot --headless --path . --script res://tools/terrain_generation_budgeted_test.gd
& $Godot --path . --resolution 1920x1080 --script res://tools/terrain_generation_native_adapter_test.gd
& $Godot --path . --resolution 1920x1080 --script res://tools/terrain_generation_playable_smoke.gd -- --capture
& $Godot --path . --resolution 1920x1080 --script res://tools/terrain_generation_streaming_playable_test.gd
& $Godot --path . --resolution 1920x1080 --script res://tools/terrain_generation_prepared_visibility_gpu_test.gd
& $Godot --path . --resolution 1920x1080 --script res://tools/terrain_generation_prepared_visibility_gpu_test.gd -- --staged
& $Godot --path . --resolution 1920x1080 --script res://tools/terrain_generation_prepared_visibility_gpu_test.gd -- --budgeted
& $Godot --path . --resolution 1920x1080 --script res://tools/terrain_generation_performance_test.gd -- --baseline
& $Godot --path . --resolution 1920x1080 --script res://tools/terrain_generation_performance_test.gd -- --incremental
& $Godot --path . --resolution 1920x1080 --script res://tools/terrain_generation_travel_benchmark.gd -- --speed=108 --workers=2
& $Godot --path . --resolution 1920x1080 --script res://tools/terrain_generation_travel_benchmark.gd -- --speed=108 --workers=4
& $Godot --path . --resolution 1920x1080 --script res://tools/terrain_generation_travel_benchmark.gd -- --speed=108 --workers=2 --force-late

# Gameplay comparison: run each command to completion before starting the next.
& $Godot --path . --resolution 1920x1080 --script res://tools/terrain3d_gameplay_travel_benchmark.gd -- --sync-baseline --speed=60 --distance=256
& $Godot --path . --resolution 1920x1080 --script res://tools/terrain3d_gameplay_travel_benchmark.gd -- --speed=60 --distance=256
& $Godot --path . --resolution 1920x1080 --script res://tools/terrain3d_gameplay_travel_benchmark.gd -- --speed=12 --distance=256
& $Godot --path . --resolution 1920x1080 --script res://tools/terrain3d_gameplay_travel_benchmark.gd -- --speed=108 --distance=256
```

Test recipes and reports use `.godot/terrain-generation-tests/`, separate from player recipes and world slots.

Milestone **6: full gameplay acceptance** remains active. Gameplay streaming, startup mesh prewarming, and the short-route comparison are implemented. Profile the remaining full-scene hitches, extend fixed-seed travel/edit routes and memory checks, validate the production flight envelope, and obtain human visual acceptance before promoting the generator.
