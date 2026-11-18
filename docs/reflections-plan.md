# Reflections plan: faster and better looking

Lead: Fable 5.2. Approved by the user on 2026-11-17 ("make reflections both faster and better looking", "study some clever cheat ways to simulate reflections that other devs have used").

## 0. Handoff

State on 2026-11-18, night (overnight lead: `claude-lead`; this section is kept current as orders finish). On 2026-11-17 the author of this plan (Fable 5.2) handed over here. A fresh lead can continue from this section, section 10 (the current work list) and the four design files under `docs/work-orders/reflections/`. Sections 1 to 8 are the Phase A reasoning; where they differ from section 10, section 10 holds.

### 0.1 What is established

- **The reflected environment of the main view is black** (measured, 9.1). The main Environment has a black colour background and its reflected source at the default, Background, so every engine SSR miss and the water's `SPECULAR` sky term return nothing. The sky radiance map is rendered every frame anyway, for ambient light.
- **Measured costs** (RTX 4070, 1600 x 900, High with the user's frozen settings, clocks held, A-X-A; 9.2 and 9.3):

| Item | Cost |
|---|---|
| Sky radiance map update | **0.38 ms every frame**: two rebuilds of 0.19 ms each, in the far viewport and in the main view, caused by the fog flip between their Environments (R1-6a, 9.4; 9.2 measured only the Incremental half, 0.18) |
| Engine SSR at half size (what High runs) | 0.12 ms dry plains, 0.18 to 0.24 ms wet plains and forest, 0.21 ms Weather Lung, 0.28 ms save spot |
| Engine SSR at full size | a further 0.17 to 0.32 ms where pixels trace |
| Engine SSR steps 64 instead of 128 | -0.06 ms at the Weather Lung and the save spot, about -0.02 ms elsewhere; tolerance and fades: nothing |
| Sky as the reflection source | +0.01 to +0.08 ms |
| Water SSR, lifted coast pose, user's values | 0.09 ms |

- **Look, from the same runs:** switching the engine SSR off changes the wet forest less than cloud noise does (SSIM 0.9984); the sky source changes every glossy frame strongly (0.85 to 0.98). The arch and the clouds are in no reflection today.

### 0.2 User decisions

- Realistic water is approved. **Before/after stills for sign-off; nothing ships before that.**
- **Budget:** on High, reflections may cost at most +0.2 ms over today at any measured pose, and only where stills show a clear look gain. More goes to Very High and above. Cheaper is welcome. Budget table: 10.3.
- Wet ground: trial SSR on puddles and near ground only, corrected fallback for the rest, and show the comparison (R1-4).
- **Cloud data is published for reflections (R3a). Clouds are not written into the main depth buffer. The cloud image must not change, proven by a byte compare.**
- **The Water tab rows Fresnel F0, Fresnel strength and SSR strength are removed at cleanup (R1-8)** and fixed at their physical values, with the settings smoke test and with persistence handled: old saved values are ignored or migrated without errors.
- All look changes are built behind the transitional switch `ringworld_reflection_model` (default 0, not a menu row; 10.2), approved by the primary agent.
- Realism by default: nothing here boosts or floors a reflection; anything that would is a question for the user first.

### 0.3 Orders: done, in progress, parked

| Order | State | Files |
|---|---|---|
| R0 tooling: scenario kinds `reflection_probe` and `ab_matrix`, two scenario files | **Committed** (b00b501) | `tools/perf_bench.gd` (+22/-3), `tools/perf_bench_reflection_probe.gd`, `tools/perf_bench_ab_matrix.gd`, `tools/fixtures/perf_bench_reflections.json`, `tools/fixtures/perf_bench_reflection_costs.json` |
| B1, B3 (two runs) | **Done.** Results in 9.1 to 9.3 | evidence in 0.5 |
| B2 (trace-mask overlay), B5 at a waterline and a lake, B6 "before" movies, B7 | Not done | — |
| R1-0 switch, main reflected source, far pins | **Committed** (5418d13). Model 0 identical to a pre-R1-0 assembly (0.5); far env Sky/Background at both models; tests pass | new `scripts/Voxel/LOD/RingReflectionModel.cs`, new `materials/ring_reflection_model.gdshaderinc`, `scripts/Voxel/LOD/RingFarViewportColorPipeline.cs` (+2), `scripts/Voxel/LOD/RingScaledFarViewport.cs` (+9/-2), `project.godot` (one global, own hunk), new `tools/ReflectionEnvironmentContractTest.cs`, new `tools/reflection_environment_contract_test.gd`, `tools/test_manifest.json` (one entry) |
| R1-1-0 probe tooling (model 0/1 variants in capture mode, env report, more poses) | **Committed** (2b73725). Exposure: frozen by capture mode (time scale 0) instead of AE off (option `exposure: manual` kept) | `tools/perf_bench_reflection_probe.gd`, `tools/fixtures/perf_bench_reflections.json` |
| R1-1a holds (trees, grass cards) | **Committed** (8b9dc4a). Trees, impostors and grass cards identical between model 0 and 1 (heatmap) | `imported_tree.gdshader`, `imported_tree_impostor.gdshader`, `ring_grass_cards.gdshader` |
| R1-1b sky visibility global, POI, construction, stand-ins, anchors, voxel terrain | **Committed** (8c43bc2). Also fixed a pre-existing bug the newly registered test found: the underground exposure writes bypassed the eye-adaptation write cache, so a jump out of a cave kept the cave exposure | `ring_sky_reflection.gdshaderinc` and the receivers of 10.4 |
| R1-1d terrain | Designed. **Parked** until T4-4b and FR-1 release the terrain and proxy shaders | same file |
| R1-2a water composition | **Done, in the tree, reported ready for commit 2026-11-18.** Compiled by Godot; tests below (0.4); model 0 matches the R1-1 stills (noise only: clouds, crowns, horizon band; no structure on water); model 1 costs at most +0.09 ms (0.4) | `docs/work-orders/reflections/R1-2-R1-5-design.md` |
| R1-2c water stills | **Partly done**: coast (lifted), Weather Lung dam, lake 20792 (rivers world, seed 876374626) at noon. Not yet: river mouth, the proxy-to-global handoff, low sun, night water, a real storm | evidence 0.5 |
| Sign-off sheet (interim R1-7) and model cost table | **Done 2026-11-18**: `.godot/audit_baseline/common/reflections/signoff_2026-11-18/` (two sheets, summary with the cost table and the user questions) | 0.5 |
| R1-3 housekeeping | Not started | 10.4 |
| R1-4 trace only where it shows (the wet-ground trial) | **Parked** with R1-1d (same files) | 10.4 |
| R1-5a water march, R1-5c measurements | Designed, not started. After R1-2a | `docs/work-orders/reflections/R1-2-R1-5-design.md` |
| R1-6 radiance cadence | Designed. **R1-6a done 2026-11-18 (9.4):** the map is rebuilt twice per frame (far and main, 0.19 ms each, 0.38 ms in all), and the main/far fog flip alone causes it. Next: R1-6d (fog flip) first, then R1-6b/c | `docs/work-orders/reflections/R1-6-design.md` |
| R1-7 sign-off sheet, R1-8 cleanup | After the above | 10.4 |
| R3a-0 cloud publication design | **Done**, including the capture hold and the main-view filter (its sections 2.5 and 2.6) | `docs/work-orders/reflections/R3a-design.md` |
| R3a-1 publication | **Staged and reviewed 2026-11-18** in `<scratchpad>/refl/r3a1/` (patch `r3a1.patch`; C# compiles in a scratch copy of the sources, 0 errors). Not in the tree, not yet run by Godot. Two Lead corrections applied in staging: the valid ramp runs on unscaled real time (it must finish under time scale 0), and no new manifest tag. Deviations to know: B7b un-holds on any camera change, so case C4 checks "content kept while held; one complete march on a turn"; `compositor_temporal_stability_test` has no manifest entry, so the new entry copies `atmosphere_visual_test` (gpu, extended, certified world, timeout 900 s est.) | same file |
| R3a-2, R3a-3 | Designed, not started | same file |
| R2 (ring radiance map), R3 (far mirror), R4 (beams), R5 (reserve) | Not designed beyond sections 4 and 6 | — |

No helper agent is running. No order was left half-applied in the tree (R1-2a is complete in the tree; R3a-1 exists only in staging).

Next, one at a time: R3a-1 into the tree with its byte-compare test, then R1-6d (fog flip, worth about 0.19 ms), then R1-5a. Parked: R1-1d and R1-4 (terrain/proxy shaders held by T4-4b).

**How model-0 identity is proven (2026-11-18).** Stills of one session are byte-comparable between models (capture mode). Across sessions (before/after a code change) they are not: clouds, tree crowns in wind and a thin band of distant terrain at the horizon differ by a few thousand pixels (SSIM 0.9999+), and a pose without them (night emitter) is byte-identical. So an order is identical at model 0 when the before/after heatmap shows only that noise and no structure on reflective surfaces (test spheres, wet ground, water, metal). Even within one session the repeat frame differs on the horizon band with models [0, 0] (diag runs), so that band is harness noise, not a model effect. A "before" assembly is built from a scratch copy of the C# sources with the order's hunks reverted and run through `perf_bench.ps1 -SkipBuild` after copying it into `bin\OptRelease` (old timestamp so the next bench rebuilds); the probe falls back to `env.reflected_light_source` when `RingReflectionModel` is not in the assembly. Shader-only orders need no such build: run the probe before the edit and after it.

### 0.4 What is uncommitted, and where

Committed: the Phase A plan (97a1a74), the R0 tooling (b00b501), Phase B and the designs (83ebc6a), R1-0 (5418d13), R1-1-0 (2b73725), R1-1a (8b9dc4a), R1-1b (8c43bc2). Uncommitted in the working tree of `terrain-vegetation-revamp`, by this lane:
- **Docs:** `docs/reflections-plan.md` (this section).
- **R1-2a:** `materials/ring_ocean_proxy.gdshader`, `materials/ring_ocean_global.gdshader`, `materials/ring_water_appearance.gdshaderinc`, new `tools/water_reflection_model_contract_test.gd`, and in `tools/test_manifest.json` the entry `water_reflection_model_contract_test` (the only hunk there on 2026-11-18 03:50; stage it alone if others appear).
- **Bench tooling for the sign-off and R1-6a** (separate commit): `tools/perf_bench_ab_matrix.gd` (+21: op `{"reflection_model": 0|1}` through `RingReflectionModel.SetModel`, restored on undo; Color values given as `[r, g, b, a]` arrays), new `tools/fixtures/perf_bench_reflection_model_costs.json` (model 1 against 0 at 12 poses), new `tools/fixtures/perf_bench_sky_radiance_cadence.json` (R1-6a).

Tests for R1-2a (`.godot/test_runs/r1_2a/`):
- `-Files` selection (37): 33 pass, 4 fail, none from R1-2a: `inland_water_sampler_test` (rivers cache version 13, want 12; as in 0.4 of R1-1), `water_ambience_capture_test` (loop lengths; same in `overnight_gate` 2026-11-16), `water_sound_sources_test` (CPU cost gate and shore distances; fails in many earlier runs), `river_far_water_fraction_test` (river head window; fails in many earlier runs). Every GPU test passed, including `inland_water_render_gpu_test` and `water_rendering_smoke_test`.
- The extended 5.2 list (10): 8 pass; `water_depth_visual_test` ("legacy >100 km reference did not whiten") and `water_ssr_live_controls_gpu_test` ("SSR on/off response too small"; its scene renders at night) fail. **Both fail identically with the R1-2a shaders reverted to HEAD** (`.godot/test_runs/r1_2a_head/`: same numbers, old_luma 0.430094 against 0.434520), so they are pre-existing and not R1-2a. They are not in `test_known_failures.json`; whoever owns the water tests should look at them (the SSR test's fixture time of day, the depth test's in-memory legacy helper).

### 0.5 Evidence

- `.godot/audit_baseline/common/reflections/b1/` (kept by the prune): the B1 sheet and stills.
- `.godot/audit_baseline/common/reflections/r1_0/`: `r10_identity_1.png`, `r10_identity_2.png` (pre-R1-0 assembly against R1-0 at model 0, and one model-1 pair; columns before | after | difference x8) and both driver reports (runs `.godot/perf_bench/refl_r10_before/20261118_002141`, `refl_r10_after/20261118_003414`).
- `.godot/audit_baseline/common/reflections/r1_1/`: `r11_identity_1.png` (R1-0 against R1-1a+b at model 0: noise only), `r11_m0m1_1.png` (model 0 against 1 after R1-1a+b: trees and cards unchanged, POI metal and wet ground reflect the sky), and `model_stills/` = the R1-1a+b state at model 0 and 1, all 13 scenarios (the "before" set R1-2a was checked against; superseded by `r1_2/model_stills/`).
- `.godot/audit_baseline/common/reflections/r1_2/`: `r12a_identity_1.png` (R1-1 stills against R1-2a at model 0, coast, lifted coast, night arch, Weather Lung dam: clouds, crowns and the horizon band only) and `model_stills/` = the R1-2a state at model 0 and 1, all scenarios, with the driver report (run `.godot/perf_bench/refl_r12a/20261118_030258`): the "before" set for R1-5a.
- `.godot/audit_baseline/common/reflections/r1_2/lake/`: lake 20792 area stills of the rivers world (seed 876374626), model 0 and 1 (`*_m1.png`), noon, clouds on; north-facing views show the river and the lake (the water mask confirms them), the south-facing `lake20792s_*` show a dry flat that is not water. Taken with a scratch subclass of `tools/river_site_capture.gd` (variant `m1` through `RingReflectionModel`), kept at `.godot/audit_baseline/common/reflections/scratch_tools/refl_lake_capture.gd` (not a tracked tool; `.godot` outside the kept folders is pruned, which deleted its first location).
- `.godot/audit_baseline/common/reflections/signoff_2026-11-18/`: the two sign-off sheets, `README.md` (summary, cost table, user questions), the cost run's report and driver report (run `.godot/perf_bench/refl_model_cost/20261118_030855`).
- `.godot/perf_bench/reflections_b1/20261117_144547/`, `reflections_b3a/20261117_150117/`, `reflections_b3b/20261117_153047/`: reports, stills and heatmaps of the three runs. `.godot/perf_bench/refl_tooling_check/`: the quick run of 0.6.

### 0.6 Handover of the bench tooling to the test-speed lane (B2a)

The test-speed lane adopts the `ab_matrix` kind. Files and hunks for a separate commit:
- `tools/perf_bench_ab_matrix.gd` (new, whole file).
- `tools/perf_bench.gd`, three hunks: the header comment; the two preloads and the two new keys in the initial `report` dictionary; in `_run()` the two filters and the three loops (ab measure, ab stills, probe). The hunks serve both new kinds together, so `tools/perf_bench_reflection_probe.gd` must be in the same commit (the driver preloads it).
- `tools/fixtures/perf_bench_reflection_costs.json` (ab_matrix scenarios) and `tools/fixtures/perf_bench_reflections.json` (probe scenarios).

Stable: yes. The `ab_matrix` code has not changed since its last fix and produced the two cost runs. R1-1-0 will later extend the probe file and its scenario file only.

Check that the old run kinds behave the same: `tools/perf_bench.ps1 -Label refl_tooling_check -Quick` with the edited driver gave `PERF_BENCH=PASS`, exit 0, with all five quick scenarios measured (save spot 9.62 ms, plains 9.00, forest interior 9.20, 9 km 6.20, 240 m/s flight 11.61 frame p50). The generic scenario loop, the VSync pass, the stills and `ssr_fade` are untouched by the diff; the new kinds are only filtered out of them. The `ssr_fade` kind itself was not re-run.

Things to know when using it:
- The runner's own verdict for the new kinds was fixed by the test-speed lane (50864f0). The three reflection runs above predate that and show `PERF_BENCH=FAIL ... did not run` in their runner logs although the driver passed.
- `ab_matrix` restores the engine SSR half-size state to what was in effect before a variant (on High the wet world holds it at half size whenever it is valid), not to the project setting.
- Property checks use `TVW.has_property`: C# properties that are public but not exported are missing from `get_property_list()`.
- The lifted coast pose is a stand-in. Waterline, lake and river poses for the user's world are still to be found.

### 0.7 Other notes

- Engine source read: `godotengine/godot` `master` (4.8 beta). The main repository has no tag for 4.8-dev7, so the exact dev7 commit was not read.
- Files other lanes hold: the terrain and proxy shaders (T4-4b, FR-1). `tools/perf_bench.ps1` belongs to the test-speed lane.

## 1. Summary

The reflections look weak for a reason that better screen-space tracing would not fix: **what a missed ray falls back to**.

1. **The fallback is probably flat black** (code reading, to be confirmed by one still in Phase B). `RingFarViewportColorPipeline.ConfigureMainEnvironment` sets the main Environment's background to a black colour so the far viewport can be composited in. `reflected_light_source` is never set, so it is at its default, Background. With a colour background the engine then uses that colour as the whole reflected environment, not the sky radiance map (section 2.5). If so, every miss of the engine SSR and the whole `SPECULAR` sky reflection of the water are black. The sky radiance map is already rendered every frame for ambient light, so using it costs nothing.
2. **The things that make this world a ring cannot be reflected by either tracer.** The arch, the far ring and the far cloud layers are composited as a backdrop without depth, and both tracers reject a hit without depth. The near clouds, rain and lightning are composited after both tracers have taken their colour copy. Even with the fallback fixed, the radiance map holds only haze sky, sun, planet and hub: no clouds, no arch, no terrain.
3. **Both tracers are tuned for a small world.** The engine SSR values (96 to 256 steps, depth tolerance 0.12 m, fade-out 1.6) predate the 4.6 rewrite and mean something different now. The water's own SSR reaches 120 m on High (the user's preset): a lake's far shore is beyond it.

**Recommendation** (section 4), in order of value for effort:

- **Fix the fallback source** and remove the water's constant blue stand-in (R1). Free.
- **Retune both tracers** for 4.8 and for this scale, and stop tracing pixels that cannot show a reflection (R1). Saves time.
- **"Far mirror"**: keep a half-resolution copy of the previous frame's final image (clouds, arch, rain included) and let glossy surfaces look up the reflected direction in it for anything far away. One or two texture reads replace a long ray march, and it is exact for distant things (R3). This is the main cheat, and it is what puts the arch and the clouds into lakes and puddles.
- **A ring-aware radiance map**: add ground, the terrain skyline, an analytic arch and the cloud decks to the sky's radiance pass, so that off-screen and rough reflections are right too (R2).
- **Analytic beam reflections** for emitter, projector and hub beams (R4).
- Kept in reserve behind a decision gate (R5): heightfield ray march for off-screen terrain, a custom SSR compositor pass, SSPR for large lakes.

Rejected for this game: a mirrored-camera planar reflection, "flip the world", time-of-day-blended baked captures, native ReflectionProbes outdoors.

## 2. What exists today (from code; unverified until Phase B)

### 2.1 Frame order of the main viewport, and what each reflection source can see

| Step | What happens | Consequence for reflections |
|---|---|---|
| 1 | Depth prepass with the normal-roughness buffer | Forced on by SSR and SSAO. |
| 2 | Pre-opaque: contact shadows (`RingContactShadows.gd`), SSAO, **engine SSR trace** | The trace reads the **previous frame's** colour, copied at step 8. |
| 3 | Opaque pass | Every opaque fragment reads the SSR buffer and mixes it over the environment reflection. |
| 4 | Sky | The main view draws nothing (black colour background). |
| 5 | Post-sky: `RingFarBackdropComposite.gd` writes the far viewport (sky, far ring, arch, far cloud layers) where the main view has no depth | The backdrop has colour but **no depth**. |
| 6 | Screen-texture and depth-texture copy | This is what the water's `hint_screen_texture` sees: opaque world and backdrop. **No near clouds.** |
| 7 | Transparent pass: water (its own SSR in the fragment shader), containment field | |
| 8 | Copy to the engine's last-frame buffer | Next frame's engine SSR colour: opaque, backdrop and water. **No near clouds, rain or lightning.** |
| 9 | Post-transparent: Sunshine clouds, rain haze, rain streaks, lightning, rainbow, containment composite, shock distortion | Never visible to either tracer. |
| 10 | Anti-aliasing, tonemap | |

Source: `render_forward_clustered.cpp` (`_pre_opaque_render`, "Copy Final Framebuffer (SSIL/SSR)" before "Process Post Transparent Compositor Effects"), and the `effect_callback_type` of each effect under `scripts/render/` and `addons/SunshineClouds2/SunshineClouds.gd`.

### 2.2 The engine SSR in Godot 4.8

Rewritten in 4.6 (PR 111210, "Overhaul screen-space reflections"). Passes, all compute:

1. Depth copy, or at half size a 2x2 downsample of depth and normal-roughness.
2. HiZ pyramid: the full mip chain of depth.
3. Trace, one ray per pixel, against the pyramid. Colour comes from the previous frame, reprojected.
4. Roughness filter: a Gaussian-blurred mip chain of the traced colour.
5. Half size only: a depth, normal and roughness aware upsample to full resolution.

The opaque shader then reads the result: `indirect_specular = indirect_specular * (1 - ssr.a) + ssr.rgb`.

**Which pixels trace.** Every pixel with depth and with roughness below 0.7. The opaque shader fades the result between 0.6 and 0.7. Nothing else limits it: not distance, not material, not whether the reflection could be seen.

**Settings that matter.**

| Setting | Game value | Meaning in 4.8 |
|---|---|---|
| `ssr_max_steps` | 64 Low, 96 Medium, **128 High**, 160 Very High, 192 Ultra, 256 Cinematic (`RingGraphicsQualityController.ResolveTuning`) | Cap on HiZ iterations per ray. A ray into open sky ends in a few iterations. A ray that skims a surface (wet ground seen at a low angle) stays in the fine mips and spends the whole budget. The PR states 64 covers a long range. The game's values were chosen for the old linear march. |
| `ssr_depth_tolerance` | **0.12** (scene); engine default 0.5 | Thickness in view-space metres, one constant for all distances. Used twice: a ray more than this far behind a surface is taken to pass behind it, and the final confidence is `(1 - smoothstep(0, tolerance, gap))^2`. The PR author found 0.2 "caused too many artifacts" and raised the default to 0.5. On receding terrain the depth step from one pixel to the next is metres, so at 0.12 a ray that crosses the surface between two pixels is taken to pass behind it. Expected result: holes and break-up in reflections of anything distant. |
| `ssr_fade_out` | 1.6 | `pow(1 - ray_length, fade_out)`, with the ray length in screen units. A reflection whose ray crosses half the screen keeps 33 %. Tall reflections fade toward their top. |
| `ssr_fade_in` | 0.12 | `pow(ray_length, fade_in)`. Close to 1 except for the shortest rays. |
| half size | desktop tier: full (`hardware_profile.gd`). Low, Medium and High: half while wet (`RingWetWorld.ApplySsrHalfSize`) | Trace and filter at half resolution, plus the upsample pass. |
| roughness quality | set to 2 in `project.godot` and by `hardware_profile.gd` | **Deprecated, does nothing** since 4.6. |

Fixed in the engine, not settings: a 5 % fade at the screen edges; the 0.7 roughness cut; a pixel without depth (sky, backdrop) is never a valid hit.

**Cost drivers.**
- Fixed while enabled, whatever the scene: depth copy, HiZ chain, the roughness-filter chain over the whole image, the last-frame copy, and one or two extra texture reads in every opaque fragment. At half size add the upsample.
- Per pixel: the number of pixels below roughness 0.7 times the iterations each ray uses. Grazing rays over wet ground are the worst case.
- Measured so far (`docs/perf-pass-2026-11.md` 0.14, dry, 1600 x 900): 0.86 ms at the save spot, 0.19 to 0.35 on the plains, 0.26 to 0.43 in forest. Wet (`docs/storm-revamp-s2-wet-world-design.md` 10.1, 720p): film-gloss SSR on Terrain3D about 0.18 ms, wet foliage about 0.2 ms before it was floored at roughness 0.71.

**Why misses look bad.**
- A miss shows the environment reflection, and that is probably black (2.5).
- The sky and the backdrop are never hits, so the upper part of every reflection is the fallback.
- Screen edges: nothing off screen can be hit. The 5 % edge fade hides the cut.
- Thickness: one constant for 1 m and for 10 km.
- The colour is one frame old and predates the cloud composite.

### 2.3 How the game drives the engine SSR

- Environment values in `scenes/VoxelRingIntegrationTest.tscn` (steps 96, fade-in 0.12, fade-out 1.6, tolerance 0.12); steps per preset from `RingGraphicsQualityController`; `world_size_launcher.gd:1886` also writes steps (128 or 96).
- The far viewports have SSR off (`RingFarViewportColorPipeline.ConfigureFarEnvironment`).
- Dry-weather gate, by another lead, uncommitted (`RingWetWorld.Ssr.cs`): SSR fades out over 3 s when the ground near the camera is dry and no glossy POI is loaded. It fades by raising both fade exponents, because the engine has no SSR strength.
- `ring_wet_ssr_roughness` (`ring_wetness.gdshaderinc`) keeps wet foliage at roughness 0.71 so it does not trace.
- Receivers that go below 0.7 and so trace: wet ground (film gloss to `WetGloss` 0.18, puddles 0.06), wet POI surfaces, POI metals (0.26 to 0.45), Weather Lung mirrors (0.04), far proxy river lines (0.06, `ring_terrain_proxy.gdshader:239`), far stand-ins, the far ocean.

### 2.4 Water, lakes and rivers

One shader, `materials/ring_ocean_proxy.gdshader` (ocean kind 1, lake 2, river 3), transparent pass, `ALPHA = 0.995`.

- **Its own SSR** (`ring_water_ssr`): a linear march in view space with a binary refinement, colour from `hint_screen_texture`.

| Preset | On | Coarse steps | Ray length | Camera distance |
|---|---|---|---|---|
| Low, Medium | off | 12 | 120 m | 250 m |
| **High** | on | 12 | **120 m** | **250 m** |
| Very High and the others | on | 24 | 250 m | 450 m |
| Ultra video | on | 32 | 400 m | 600 m |

  Thickness is 0.35 m near to 2.0 m at the end of the ray. Roughness cutoff 0.32. A sample without depth (sky, backdrop) cannot be a hit. On High one step is 10 m, so thin things (trunks) are missed or flicker.
- **On a miss**: the constant `reflection_color = vec3(0.34, 0.52, 0.68)`, scaled by the storm sky factor.
- **How the reflection reaches the screen**: `grazing_color = mix(optical_water, reflection_color, fresnel)` goes into `ALBEDO`. So the reflection, the SSR hit included, is **lit again** by the sun and the ambient light. A reflected hill darkens with the sun angle on the water, and at night the reflection goes with the light.
- **Sky reflection**: `SPECULAR` asks the engine for an environment reflection, attenuated by haze and storm factors. If 2.5 holds, that term is black and those factors do nothing.
- **Storms**: because the screen texture has no clouds, the SSR "sees the clear sky behind a closed storm", so it is faded out under storms (`storm_ssr_keep`).
- **Cost**: water is about 1.0 ms at the coast with the user's SSR settings against 0.3 ms at Medium, where SSR is off (`docs/perf-pass-2026-11.md` section 6, fixture world). The difference, about 0.7 ms, is mostly the march; B5 measures it on the real world.
- The far ocean (`ring_ocean_global.gdshader`, far viewport) has no SSR: a constant tint plus `SPECULAR`.

### 2.5 The fallback: the sky radiance map

- `scripts/day_side_atmosphere.gd` installs `ring_local_sky.gdshader` as the sky, realtime, radiance size 256. The radiance pass (`AT_CUBEMAP_PASS`) contains the Physical haze sky, the sun disc and halo (hidden under a closed storm), the planet and the hub halo. It has no stars, no clouds, no arch and no ground. Below the horizon it is the haze along a ray down to the floor.
- The scene Environment has `ambient_light_source = 3` (Sky), so this map is rendered and filtered every frame for ambient light.
- `reflected_light_source` is not set anywhere for the main Environment (a repository-wide search finds only the far viewports copying the main value, and tests setting Disabled). The default is Background.
- `RingScaledFarViewport.ConfigureMainBackground` sets the main background to `BGMode.Color`, black.
- Engine, `render_scene_data_rd.cpp`: reflection source Background with a sky background uses the radiance map; with a colour background it sets `SCENE_DATA_FLAGS_USE_REFLECTION_COLOR` and the reflected environment is that colour.

**Conclusion, to be confirmed:** in gameplay the reflected environment of the main view is flat black. `git log -S` dates the colour background to commit c52aff1 (2026-10-18, haze P0 to P3). The storm work of 2026-10-26 was written as if the radiance map were reflected ("the sun disc in the radiance map is a sharp glint otherwise").

One piece of evidence points the other way: the wet world notes say half-size SSR shows "more sky, fewer dark tree reflections". If the fallback were black, fewer hits should look darker, not more like sky. Hence the first Phase B step is a still with a mirror test sphere (B1).

### 2.6 Defects, ranked by expected visible effect

| # | Defect | Where | Confidence |
|---|---|---|---|
| D1 | SSR misses and the water's `SPECULAR` reflection fall back to black, not the sky | main Environment | high from code; one still decides |
| D2 | Arch, far ring, far clouds and sky can never be a hit (no depth) | both tracers | certain |
| D3 | Near clouds, rain and lightning are in neither colour source | frame order | certain |
| D4 | Lake far shore beyond the water march (120 m ray on High) | water presets | certain |
| D5 | Water reflection is lit again through `ALBEDO`; miss colour is a constant blue | `ring_ocean_proxy.gdshader` | certain |
| D6 | Engine SSR values predate 4.6: tolerance 0.12 m, steps 128 and up, fade-out 1.6 | scene, presets | values certain; visible effect to be captured |
| D7 | The radiance map has no ground, skyline, arch or clouds | `ring_local_sky.gdshader` | certain |
| D8 | Pixels trace that cannot show a reflection: far river lines, far stand-ins, wet ground kilometres away (the wet reach is 5.2 to 6.5 km) | receivers | certain; cost to be measured |
| D9 | Inert `roughness_quality` writes; two writers of `ssr_max_steps` | `project.godot`, `hardware_profile.gd`, launcher | certain |

## 3. Techniques studied, ranked for this game

Look gain and cost are judgements for this game on an RTX 4070 at 1080p, not measurements. Effort: S = one implementer work order, M = a design by the Lead role plus two or three work orders, L = a feature arc.

| Rank | Technique | Look gain here | Frame cost | Effort | Verdict |
|---|---|---|---|---|---|
| 1 | Correct environment fallback (use the sky radiance map) | very high if D1 holds | none (already rendered) | S | **Do first** (R1-1) |
| 2 | Previous-frame colour as the reflection source, with a far-field direction lookup ("far mirror") | very high: arch, clouds, far shore, mountains in water and puddles | est. 0.1 ms for the copy and mips, then 1 to 3 reads per glossy pixel; lets the marches get shorter | M | **Do** (R3) |
| 3 | Sky- and haze-aware radiance map with ground, skyline, arch and cloud decks; horizon and specular occlusion | high: everything off screen, every rough surface, every metal | est. under 0.1 ms, in the radiance pass only | M | **Do** (R2) |
| 4 | Retuned engine SSR, traced only where it shows | medium look, real saving | saves an estimated 0.1 to 0.4 ms while wet | S | **Do** (R1-3, R1-4) |
| 5 | Retuned water SSR (growing steps, thickness relative to distance) | medium to high until R3 exists, then near field only | none, or a saving | S | **Do** (R1-5) |
| 6 | Analytic reflections of beams and landmark billboards | medium by day, high at night and in storms | small | M | **Do after R2 and R3** (R4) |
| 7 | Ray march of the terrain heightfield | medium: off-screen terrain, flying camera, lake seen from above | est. 0.1 to 0.3 ms on lake pixels | M to L | Reserve (R5a) |
| 8 | Roughness-masked half-resolution HiZ SSR with temporal accumulation (custom compositor pass) | medium | about the engine's cost, less with tile masking | L | Reserve (R5b): only if the engine pass cannot be tuned |
| 9 | Screen-space planar reflections (SSPR) | high on large calm lakes only | 0.2 to 0.4 ms | M | Reserve (R5c) |
| 10 | Wet-ground tricks: puddle masks, ripple normals, Fresnel-only sky | already built (wet world) | — | — | Keep; they start to pay once the fallback is right |
| 11 | Box-projected or parallax-corrected cubemaps, local probes | low outdoors | re-capture cost | M | Not outdoors; enclosed POI interiors only |
| 12 | Planar reflection with a low-resolution mirrored camera | high on one flat water body | est. 2 to 4 ms and a second CPU cull | L | **Reject** |
| 13 | "Flip the world" mirrored geometry | as 12 | as 12 | L | **Reject** |
| 14 | Time-of-day-blended baked captures | low | memory, authoring | L | **Reject** |

### 3.1 Notes on each

**Screen-space planar reflections (SSPR).** Ghost Recon Wildlands (Genin) and the pixel-projected reflections of Cichocki. Every screen pixel is mirrored about a known water plane and written to its mirrored screen position through a small hash buffer; the water then reads that buffer. No ray march, and the cost does not depend on distance: 0.3 to 0.4 ms on consoles at quarter resolution (Genin); 0.95 ms against 3.2 ms for HiZ SSR (Cichocki). Limits: one plane per pass, no normal maps in the projection itself (ripples are applied as an offset), holes that need filling.
*Here:* the lakes are level and their sag from the ring's curvature is about 0.6 m over 5 km, so one plane per lake holds. Rivers slope, which is why Far Cry 5 left planar methods for SSR (McAuley). Lakes sit at different heights, and the ocean is displaced by the FFT. The far mirror (R3) gets the same result for distant content at lower cost and works on every water kind, so SSPR is held back for large calm lakes should R3 not be clean enough.

**Planar reflection with a mirrored camera.** Half-Life 2 "expensive water" and Unreal's planar reflection render the world again from the mirrored viewpoint. It is the only exact method, off-screen content included.
*Here:* the main view is 4.5 ms of near terrain, 3 to 5 ms of trees and a CPU that is already the limit in flight. A second view at quarter resolution saves fill, not vertices, draws or culling. Rejected.

**Cubemaps: parallax-corrected, box-projected, time-of-day-blended, and a cheap updating panorama.** Remember Me corrected local cubemaps against a box proxy (Lagarde and Zanuttini). GTA V renders a low-resolution environment cubemap every frame from simplified geometry (Courrèges).
*Here:* the engine already renders a panorama each frame, the sky radiance map. The work is to put the right things in it (R2). Native `ReflectionProbe`s render the main world with the main Environment, whose background is black by design (the far image is a compositor effect), so an outdoor probe would most likely capture a black sky and override the radiance map with it. They suit enclosed spaces only. Baked captures blended by time of day do not fit a procedural world with a moving sun, storms and eclipses.

**Sky- and haze-aware fallback, horizon and specular occlusion.** Frostbite (Lagarde and de Rousiers) and Activision's ground-truth specular occlusion (Jimenez et al.) both rest on one idea: a reflection ray that leaves toward a direction the surface cannot see must not return bright sky. The engine has a horizon term (`horizon * horizon`) and a specular occlusion tied to ambient brightness.
*Here:* what is missing is content. The lower half of the radiance map has no ground, and nothing in it stands where the mountains stand. R2 adds a lit ground term and the terrain skyline, so a reflection aimed at a hillside returns hazed hill.

**Ray march of the terrain heightfield.** The UHawk VR flight simulator uploads the terrain heightmap to the water shader and marches the reflected ray against it: 16 steps, and a hit returns a dark terrain tone times the ambient sky, blended over the cubemap. Maximum mipmaps (Tevs et al.) make such a march fast.
*Here:* the heights are already on the GPU for the height-shadow clipmap: two levels of 1,024 x 1,024 at 4 m and 16 m, with five max-mips (`RingTerrainHeightShadowGpu`, `RingHeightShadowLattice`), about 4 km and 16 km across. They are storage buffers, not textures a material can sample, so the first cost is publishing them. The second is the hit colour: no whole-ring albedo texture exists (the global LOD uses vertex colours). At ring distances a far hillside is mostly haze, so "terrain tone times light, then haze" is credible for hits beyond about 2 km and weak closer. What only this technique gives is terrain that is not on screen: a lake seen from a cliff or from the air. For a walking camera the far shore is on screen and R3 covers it. The skyline in R2 uses the same height data in a cheaper form. Reserve.

**Reflection impostors for landmarks.** The Samaritan demo reflected textured billboards ("image based reflections"): the reflected ray is intersected with a few quads. MotoGP drew low-detail, properly mirrored bikes into a flipped copy of the frame.
*Here:* the arch is not a billboard case. It is a cylinder the sky shader can intersect analytically (R2c), and when it is on screen the far mirror shows the real one. The beams (emitter, projector, hub) are line segments with a known glow profile: the reflection is that profile evaluated along the reflected ray, with no texture and no pass (R4). The Weather Lung is the reflector more than the reflected: its mirrors need the fallback (R1, R2), not an impostor.

**"Flip the world".** Mirrored geometry below the water, as old games did for mirrors and floors. The cost of a second scene, and the ring's water is not one plane. Rejected with the mirrored camera.

**Temporal reprojection of the previous frame's colour.** Killzone Shadow Fall (Valient) and the 4.6 engine SSR both read the previous frame. It costs one copy and makes available everything that is composited late.
*Here:* this is the way around D3. The engine's own copy is taken before the cloud composite and cannot be moved, so R3 takes its own copy after the last post-transparent effect.

**Half-resolution HiZ SSR with a roughness mask and temporal accumulation.** Stochastic SSR (Stachowiak), FidelityFX SSSR, HiZ cone tracing (Uludag), McGuire and Mara's screen-space DDA. Tile classification skips non-glossy tiles; temporal accumulation pays for rough reflections.
*Here:* a custom pass would allow a thickness relative to distance, the final image as colour source, the backdrop as a valid hit, and a real trace mask. It can be wired in without engine changes: the fragment built-in `RADIANCE` is mixed over the environment reflection before the engine's SSR term, and the game's receivers all have their own shaders. It is still a new compute pipeline and a new input for every receiver, and R1 to R3 remove most of the reasons for it. Reserve, behind captures.

**Wet-ground tricks.** Lagarde's "Water drop" series (wet surfaces darken, gloss rises, puddles fill hollows first) and Tatarchuk's rain course. MotoGP: a mottled alpha mask on the road varies the reflection so a flat mirror reads as puddles and rivulets, and the reflection is faded near hills and at steep camera angles. Racing games in general: an environment map everywhere, real reflections only where they are noticed.
*Here:* the wet world already has film gloss, puddles that fill cavities first, ripples and drying. What it lacks is something to reflect. If D1 holds, puddles reflect black wherever SSR misses.

**Also found.**
- *The flipped back buffer* (MotoGP): the reflection of distant scenery in a level road is the frame flipped vertically. That is the degenerate form of the far mirror, and its stated limit (wrong for objects close to the road) is exactly the limit handled in R3 by a distance test and a near-field march.
- *Trace only where it is noticed*: Assassin's Creed IV used a reflection mask and half-resolution buffers and reported about 1 ms on average on Xbox One (Wronski). R1-4 is the same idea through the roughness output.
- *Hole filling with alpha-weighted mips*: used by SSR resolves to cover misses with nearby valid colour. R3 uses it.

## 4. Recommended combination

Four layers. Each surface takes the first that has an answer.

| Layer | Answers for | Source | Work order |
|---|---|---|---|
| 1. Near trace | Things within tens to a few hundred metres: banks, trunks, POI walls, the player's surroundings | Engine SSR for opaque surfaces (retuned, masked); the in-shader march for water (retuned) | R1-3, R1-4, R1-5 |
| 2. Far mirror | Anything far away that is on screen: far shore, mountains, arch, far ring, clouds, sky, storm wall, lightning | Half-resolution copy of the previous frame's final image with a distance channel and filled mips; looked up by reflected direction | R3 |
| 3. Ring radiance map | Everything off screen, every rough surface | The sky's radiance pass, extended with ground, skyline, arch and cloud decks | R1-1, R2 |
| 4. Analytic lights | Sun, planet and hub highlights (exist in `light()`); beams | Closed form in the receiver | R4 |

### 4.1 Why the far mirror works

For a level mirror, the camera sees in the water what a second camera would see from the eye's mirror position, 2h below, where h is the eye height above the water. For content at distance D the two views differ by an angle of about 2h/D. With h = 1.7 m that is under half a degree for everything beyond about 400 m. So for a walking camera, the reflection of anything farther than a few hundred metres is the main view sampled in the reflected direction: no march and no depth test, only "is that direction on screen, and is what it shows far away".

The general bound, for rippled water and sloped wet ground: the error is at most s x sin(angle between the view ray and the reflected ray) / D, with s the distance from the camera to the reflecting point.

What it needs:
- **The previous frame's final image**, taken after the last post-transparent effect, at half resolution, with the distance to each pixel in alpha (the backdrop counts as infinitely far). Luminance clamped so the sun disc does not make fireflies.
- **A mip chain built with alpha-weighted downsampling**, so far colour floods under near occluders. A foreground trunk on screen would otherwise punch a hole in the lookup at the place its mirror image is not.
- **The previous frame's view-projection matrix** as a global, to project the reflected direction.
- In each receiver: project the reflected direction; read; accept by on-screen margin, far-ness and the parallax bound; pick the mip from the roughness. One refinement (read the distance, project the point at that distance, read again) brings the middle distances in.

Where it gives way:
- **High camera.** At 300 m altitude the bound admits only what is tens of kilometres away: arch, sky, far cloud. The rest goes to layers 1 and 3.
- **Off screen.** Looking down at water, the reflected directions point above the top of the screen. Layer 3.
- **Near field.** Layer 1 runs first and wins on a hit.

It replaces the long part of both marches. The water's march can be cut to the near field, and the engine SSR's job shrinks to contact reflections.

Precedents: the flipped back buffer of MotoGP; previous-frame reprojection in Killzone Shadow Fall and in the engine's own SSR; SSPR, which makes the same planar argument per pixel. The combination as described is this plan's proposal and has no shipped reference. **It needs a prototype before it is committed to** (R3-0).

### 4.2 Realism

Nothing in this plan boosts, widens or floors a reflection. Every term is the physical one or an approximation of it. If a later tuning step wants a reflection stronger than the model gives (for example the arch in a lake), that is a question for the user first.

### 4.3 Live-tuning menu

No new rows are planned. Reflections follow the existing systems (haze, clouds, storm light, wetness, water appearance). If R1-5 changes the meaning of the Water tab's SSR rows (steps, ray distance), the rows are renamed or removed in the same work order, not duplicated. Debug views (trace mask, far-mirror validity, mirror sphere) go into debug tooling and the existing Water SSR debug selector.

## 5. Phase B: measurements and captures

Machine: RTX 4070 desktop at P0, 1920 x 1080 and 1600 x 900, High preset with the user's settings (snapshot `slot3_2026-11-16`), VSync off, clocks running unless noted. Harness: `tools/perf_bench.ps1` with a new scenario file `tools/fixtures/perf_bench_reflections.json` and `-SettingsOverlay`; forced wetness through the wet world's force call as in `tools/wet_world_capture.gd`. One reused profile; prune at the end.

**Poses** (each as a `still` scenario):

| Pose | Purpose | Source |
|---|---|---|
| wet plains, eye height, pitch -3 and -30 | wet ground at grazing and steep angles; forced wet | `plains_eye` in `perf_bench_visible_opts.json` |
| wet plains with the arch | arch in puddles | `plains_arch` |
| wet plains, 300 m up | far wet ground filling the screen | `alt300_plains` |
| wet forest interior | trunks in wet ground | `forest_interior_eye` |
| puddle close-up beside a tree and beside a POI wall | contact reflections | to be placed |
| Weather Lung dam and overlook | mirrors at roughness 0.04 | `wl_dam`, `wl_overlook` |
| emitter rim, projector tower | metals at 0.26 to 0.45 | `emitter_rim`; projector to be placed |
| river bank looking along the river | sloped water, near bank | `tools/river_site_capture.gd` sites |
| lake, near shore looking across; the same with the arch in view; the same from a cliff looking down | far shore, arch, off-screen case | `tools/inland_water_render_gpu_test.gd` sites or a search of the world |
| coast | ocean with FFT | `coast_eye` |
| lake at night with a beam in view; lake under a closed storm | night and storm fallback | lake pose plus time and forced storm |

**B1. What is the fallback?** At the lake, the wet plains and the Weather Lung: a still as shipped, then the same with `reflected_light_source` set to Sky on the main Environment, then with SSR off in both. Add a mirror test sphere (roughness 0, metallic 1, debug tooling) to each. Decides D1 in a few minutes. Also record what the lower half of the radiance map shows.

**B2. Who traces?** A debug overlay of `roughness < 0.7` from the normal-roughness buffer, dry and wet, at the save spot, plains, forest, 300 m altitude and the Weather Lung. Explains why the save spot costs 0.86 ms dry against 0.19 to 0.35 on the plains, and sizes R1-4.

**B3. Engine SSR cost matrix.** A-B-A: off; on at full and at half size; steps 32, 64 and 128; tolerance 0.12, 0.5 and 2.0; fade-out 1.6, 1.0 and 0.5. At dry and wet plains, wet forest, wet 300 m altitude, the Weather Lung and the save spot. Report median GPU ms with the noise floor (`perf-capture` skill), and one still for each tolerance and fade value at the wet plains and the Lung.

**B4. Radiance map cost.** The sky is realtime at size 256; its radiance pass reads the sky-view LUT, or runs the haze at quality 4 when the LUT is not usable. Measure it by A-B against a sky that does not update. If it is above about 0.1 ms, a lower cadence is a candidate saving, and R2 must hold what it adds within the same figure.

**B5. Water SSR cost and reach.** Water SSR on and off at the coast, lake and river; High against Very High values; the three debug views (confidence, hit mask, step ratio) as stills. Short movies of the lake and the river while walking, with `ffmpeg_tools.ps1 flicker`, for shimmer on thin reflections.

**B6. Today's artifacts.** Stills and 10 s movies at every pose, with a sheet of each. They are the "before" for every later work order.

**B7. Far-mirror prototype** (after B1 to B6, this is R3-0): the capture effect, the lookup in the water shader only, behind a debug switch. Measure the copy and mip cost and take the lake stills. Go or no-go for R3.

Evidence goes under `.godot/perf_bench/reflections_*` and `.godot/audit_baseline/common/reflections/` (kept by the prune; `.godot/captures` is not), the numbers into a new section 9 of this document.

## 6. Work orders

Roles as in `AGENTS.md`: "Lead" is `claude-lead`, "Implementer" is `implementer`. Each order states its verification. Stills are judged with `ffmpeg_tools.ps1 compare` heatmaps. Any order that changes pixels on purpose ends with a before/after sheet for the user.

### R0. Evidence (Phase B)

- **R0-1** Scenario file `tools/fixtures/perf_bench_reflections.json`, the poses above, forced-wet and forced-storm options. Implementer. Verification: a quick run completes and writes stills.
- **R0-2** Debug tooling: mirror test sphere, trace-mask overlay. Debug only, not in the menu. Implementer.
- **R0-3** Run B1 to B6; write section 9. This lead.

### R1. Fix and retune what exists

**Superseded by section 10** (the orders as revised after Phase B and the two Lead designs). The text below is the Phase A version, kept for the reasoning.

Depends on R0. Each is S.

- **R1-1 Reflected environment = sky.** Only if B1 confirms D1. Set `ReflectedLightSource = Sky` in `RingFarViewportColorPipeline.ConfigureMainEnvironment`. The two far environments copy the main value (`RingScaledFarViewport.cs:1037` and `:1155`): pin both to what they resolve to today, so the far image does not change. Then review every receiver whose `SPECULAR` was tuned against a black environment: water, POI architecture, terrain wet gloss, trees. Verification: far viewport stills identical; dry matte terrain identical (compare); glossy poses changed as intended; the user signs off the sheet. No frame cost expected, confirmed by a perf_bench quick run.
- **R1-2 Water reflection composition.** Remove the constant `reflection_color` and the Fresnel mix into `ALBEDO`. Hand the SSR hit to the engine through `RADIANCE` (rgb = hit colour, alpha = confidence), so a miss falls through to the radiance map at the right roughness and the engine applies Fresnel once. The refracted and volume part stays as it is. Re-check the storm and haze factors on `SPECULAR` now that they act. Depends on R1-1. A design note by the Lead role first (which terms move, how the ocean and inland compositions stay continuous at river mouths); then Implementer. Verification: coast, lake and river stills by day, at night and under storm; `water_ssr_contract_test`, `water_rendering_smoke_test`, `inland_water_render_gpu_test`; user sign-off.
- **R1-3 Engine SSR values for 4.8.** Steps, tolerance and fades per preset from B3. Expected direction: steps 48 to 64 on High, tolerance 0.5 or above, fade-out near 1.0. One writer for the steps (remove the launcher write, as `docs/quality-presets-plan.md` item 11 already asks) and remove the inert `roughness_quality` writes. Verification: B3 poses before and after, with a saving at wet grazing views; `QualityPresetContractHeadlessTest`.
- **R1-4 Trace only where it shows.** Through the existing `ring_wet_ssr_roughness` pattern: far proxy river lines and far stand-ins stay above the cut; wet ground beyond a distance from B2 (expected 150 to 400 m, with a fade) stays above the cut while its direct highlight keeps the wet gloss. Whether near film-wet ground keeps its SSR is question 3 for the user. Verification: trace-mask overlay before and after; compare at the wet poses; a perf_bench delta at wet plains and wet 300 m altitude. Terrain shaders are the terrain lead's files: schedule after their work lands.
- **R1-5 Water march for this scale.** Growing step length and a thickness proportional to distance, so the same or fewer steps reach farther; per-preset values re-derived; High gets a far shore. If R3 is approved, this order only fixes the near field and R3 owns the rest. Verification: B5 stills and flicker movies; water tests; cost at the coast not higher than today.

### R2. Ring radiance map

Depends on R1-1. Design by the Lead role (one document section), then two or three Implementer orders. Files: `materials/ring_local_sky.gdshader` and includes; a small producer for the skyline.

- **R2a Ground.** In the radiance pass only, directions below the local skyline return lit ground: an average ground albedo times sun and ambient irradiance, with haze. Ends the "sky from below" on metal walls and rippled water.
- **R2b Skyline.** A panoramic horizon profile around the camera: for 256 or more azimuths, the elevation angle and distance of the terrain skyline, from the height-shadow clipmap heights. Rebuilt on a timer or after the camera moves a set distance. The radiance pass paints hazed terrain tone below it.
- **R2c Arch.** Analytic: intersect the direction with the ring cylinder (the sky shader already has the ring geometry), colour from a land and ocean tone, the whole-ring weather cover (`ring_far_weather_cloud.gdshaderinc`), the day factor at that ring position and the haze along the path. The Lead decides the albedo source: constant tones first, or a small ring colour strip baked from the global LOD vertex colours. Alternative if the look does not match the real arch: a slow-cadence low-resolution panorama rendered from the far world. More exact, but it needs a second camera in two far worlds.
- **R2d Cloud decks.** Where the direction crosses a cloud layer, opacity from the existing cloud shadow and weather maps, colour from the published cloud lighting. A Lead spike decides whether this matches the composited clouds well enough.
- Verification: mirror-sphere stills beside the real view at six poses and four times of day; the main view (not a cubemap pass) unchanged, bit for bit; radiance pass cost within the B4 figure; storm, eclipse and megastorm frames checked for a bright leak.

### R3. Far mirror

Depends on R1-1 and R1-2. M.

- **R3-0 Prototype and decision** (B7). Lead design, Implementer build, behind a debug switch.
- **R3-1 Capture effect.** A post-transparent compositor effect ordered after the last composite: half-resolution colour, distance in alpha, alpha-weighted mips; publishes a `Texture2DRD` global and the previous view-projection. Pattern: `RingContactShadows.gd` with `RingContactShadows.cs`. Main view only; other views skipped by target size as contact shadows do. Handles resize, the first frame and a teleport (no history: validity 0).
- **R3-2 Shared include** `ring_reflection_far_mirror.gdshaderinc`: project, validate, refine once, choose the mip. Parity test for the projection maths (`test-scaffold` skill).
- **R3-3 Water.** Near march first, then far mirror, then radiance map. Shorten the march. Remove `storm_ssr_keep`: the source now contains the storm.
- **R3-4 Wet ground, puddles and POI surfaces**, through `RADIANCE` in `ring_wetness.gdshaderinc` consumers and `ring_poi_architecture.gdshaderinc`. With it, re-run B3: the engine SSR may then go to half size on every preset.
- Verification: lake, arch, storm and night stills; flicker movies while turning fast (one-frame lag, edge disocclusion); lightning frames (a flash in the reflection one frame late is acceptable; a flash that persists is not); a perf_bench delta at every pose, with a net saving at the coast and wet plains; `compositor_temporal_stability_test`.

### R4. Landmarks and beams

Depends on R2 and R3. M.

- **R4-1 Beams.** A shared function: the glow of a line segment as seen along a ray, from the profiles in `ring_beam_orb.gdshaderinc` and the beam layers. Up to four nearest beams published as globals. Used by water and puddles, added to the reflection before Fresnel.
- **R4-2 Weather Lung.** Review the mirrors after R1 to R3 with stills. A local probe is considered only for an enclosed interior.
- Verification: night lake with an emitter beam, storm with a hub beam; no cost when no beam is in range.

### R5. Reserve, behind a decision gate

After the R3 captures, this lead proposes which of these to build, if any, and the user decides.

- **R5a Heightfield reflection march.** Publish the height clipmap as textures; march with the max-mips; hit colour as terrain tone, light and haze. For lakes seen from above and for flight.
- **R5b Custom SSR pass.** Only if B3 and R1-3 show the engine pass cannot serve the near field at an acceptable cost.
- **R5c SSPR for large lakes.** Only if R3 leaves visible errors on calm lakes at middle distances.

### Order and parallelism

R0, then R1-1, then (R1-2 and R1-3 in parallel), then R1-4 and R1-5. R2 and R3 can run in parallel after R1-2. R4 last. R1 alone should already be visibly better and no slower.

## 7. Risks and open technical points

- **D1 may be wrong.** Then R1-1 drops out, and R2 and R3 keep their value. B1 comes first for this reason.
- **Turning the sky reflection on changes every glossy surface at once.** Materials tuned against a black environment may come out too bright. R1-1 and R1-2 are therefore one reviewed batch with a sheet for the user.
- **Far mirror artifacts**: one-frame lag in fast turns; holes behind near occluders (filled from coarse mips, so softer there); feedback, water reflecting last frame's water highlights (luminance clamp); wrong parallax at middle distances from a high camera (handled by the validity bound, but the hand-over to the radiance map must not show as a line).
- **Radiance map resolution.** 256 per face is about 0.35 degrees per texel. The arch overhead is about 2 degrees wide (the ring's width seen across its 10,000 km diameter): a few texels. Enough for rough and rippled surfaces, soft for a still mirror. The far mirror carries the sharp case when the arch is on screen.
- **Transparent-pass details.** That `RADIANCE` behaves in the transparent pass as in the opaque pass is read from the shader source, not tested.
- **Engine version.** `master` was read, not dev7.
- **Shared files.** Terrain, proxy and wet-world files are in other leads' hands.
- **Lens droplets and HUD.** The far-mirror copy must be taken before anything that belongs to the lens and not the world. To be checked in R3-1.

## 8. Questions for the user

1. **Water look.** Fixing the water means its reflections become real: sky, clouds and the arch appear in it, the constant blue goes, and reflections no longer dim with the sun's angle on the water (at night a lake reflects the night sky and the arch). This changes a look that was tuned by eye. May I change it and bring before/after stills for sign-off, or should the present daytime look be held as the reference and only the errors be fixed around it?
2. **Budget.** If speed and look pull apart, which wins? Proposed rule: reflections never cost more than today at any measured pose, and cost at least 0.3 ms less at the coast and on wet plains on High. Anything better looking that would break that rule goes to Very High and above only.
3. **Wet ground.** The storm work kept SSR on all film-wet ground on purpose ("the reflected trunks and haze on wet ground are the wet look"). May I trial tracing only puddles and the ground within a few hundred metres, with the corrected fallback and the far mirror carrying the rest, and show the comparison? If the trial reads worse, the present behaviour stays.

## 9. Phase B results

### 9.1 B1: the fallback (2026-11-17)

Run: `tools/perf_bench.ps1 -Label reflections_b1 -ScenarioFile tools/fixtures/perf_bench_reflections.json`, run id `20261117_144547`. RTX 4070, 1600 x 900, the user's frozen settings (snapshot `slot3_2026-11-16`), noon, clocks held, quiet scene. New scenario kind `reflection_probe` (`tools/perf_bench_reflection_probe.gd`): 8 stills per pose, reflected source as shipped or Sky, engine SSR on or off, with and without two metallic test spheres (roughness 0 and 0.35).

**D1 is confirmed.**
- The live main Environment at all seven poses: `background_mode` 1 (Color), `reflected_light_source` 0 (Background), `ambient_light_source` 3 (Sky), sky realtime, radiance size 256, `ssr_max_steps` 128, tolerance 0.12.
- As shipped the mirror sphere is black apart from the sun glint. With the source set to Sky it shows the haze sky.
- Forced-wet plains: as shipped the wet ground is dark grey and SSR adds a few faint patches in the middle distance. With the sky source the wet plain reflects the sky everywhere.
- In both cases the arch and the far clouds, which fill the upper half of the frame, are absent from the reflection (D2, D7).

**Also seen.**
- The sky source changes dry surfaces too: grass and trees gain a faint blue sheen, clearest inside tree shadows. R1-1 therefore needs a review of every receiver's `SPECULAR`, not only the glossy ones.
- The lower half of the mirror sphere shows blue haze where the ground should be (R2a).
- Maximum forced wetness under a clear noon sky reads as a flooded blue sheet. It is not a natural state. Sign-off stills must come from a real storm and its aftermath.
- `WetWorld.SsrHalfSizeActive` was true at every pose, dry ones included: on High the engine SSR runs at half size whenever the wet world is valid, not only while the ground is wet.

**Evidence** (kept): `.godot/audit_baseline/common/reflections/b1/b1_wet_plains_fallback_sheet.png` (tile 0 as shipped with SSR, 1 sky source with SSR, 2 as shipped with spheres, 3 sky source with spheres) and the single stills beside it. The run folder is `.godot/perf_bench/reflections_b1/20261117_144547/`.

**Tooling notes.**
- In the first two scenarios of this run the "ssr" stills are not valid SSR-on frames: the probe did not find the policy override property and wrote `ssr_enabled` against the dry-weather gate. Fixed after the run. The conclusion above rests on the SSR-off and sphere stills.
- The runner prints `PERF_BENCH=FAIL ... did not run` for scenarios of the new kinds, because they report under their own keys (`reflection_probes`, `ab_matrices`). The driver's verdict in `console.log` is the one to read until `tools/perf_bench.ps1` skips those kinds in that check.
- The `coast_eye` pose shows grass with the ocean as a thin strip. Water needs its own poses (a lifted coast pose for now; lake and river sites to be found).

### 9.2 B3 to B5, first run: what reflections cost today (2026-11-17)

Run: `tools/perf_bench.ps1 -Label reflections_b3a -ScenarioFile tools/fixtures/perf_bench_reflection_costs.json -Scenarios ssr_plains_wet,ssr_alt300_wet,water_coast_lift,sky_radiance_plains`, run id `20261117_150117`. RTX 4070, 1600 x 900, the user's frozen settings, noon, clocks held. New scenario kind `ab_matrix` (`tools/perf_bench_ab_matrix.gd`): baseline and variants interleaved A-X-A, 2 rounds of 5 s windows. The baseline spread of GPU p50 over 12 to 26 windows was 0.04 to 0.10 ms; the two rounds of a variant agreed within 0.03 ms.

Note (2026-11-18): until then `_median` in `perf_bench_ab_matrix.gd` took the upper of the two middle values, so the 2-round "median" deltas in 9.2 and 9.3 are the less favourable round (at most the 0.03 ms round agreement away from the true median, slightly conservative); it now averages them.

Engine SSR, ground forced wet, half size (what High runs whenever the wet world is valid). Delta of GPU p50 in ms against the baseline:

| Variant | wet plains, eye height | wet plains, 300 m up |
|---|---|---|
| SSR off | -0.18 | -0.24 |
| full size instead of half | +0.20 | +0.19 |
| steps 32 / 64 / 128 (baseline 128) | -0.02 / -0.01 / 0.00 | -0.03 / -0.01 / 0.00 |
| tolerance 0.12 / 0.5 / 2.0 | 0.00 / 0.00 / 0.00 | 0.00 / 0.00 / 0.00 |
| fade-out 1.0 / 0.5 | 0.00 / +0.01 | 0.00 / 0.00 |
| reflected source = Sky | +0.05 | +0.07 |

Water at the lifted coast pose (25 m up, pitch -6, ocean over about half the frame), with the user's water SSR values (32 coarse steps, 8 refinements, 1,000 m ray, 695 m camera distance, strength 1.0; these override the High preset):

| Variant | Delta GPU p50 (ms) |
|---|---|
| water SSR off | -0.09 |
| Very High preset values (24 steps, 250 m) | -0.02 |
| 12 steps, 60 m ray | -0.10 |
| reflected source = Sky | +0.08 |
| engine SSR off | -0.22 |

Sky radiance map (dry plains; baseline realtime, size 256):

| Variant | Delta GPU p50 (ms) |
|---|---|
| process mode Incremental (no update while nothing changes) | **-0.18** |
| process mode Quality (full-quality filter every frame) | +0.98 |

**What this says.**
- **The engine SSR costs about 0.2 ms while wet at half size, 0.4 ms at full size.** Steps, tolerance and fades cost nothing measurable. The stills agree: against the baseline, every steps, tolerance and fade variant has SSIM 0.997 or higher, SSR off 0.975 (eye height) and 0.994 (300 m up), and the sky source 0.85 and 0.91. So the engine SSR's settings are not where the look or the time is. R1-3 is reduced to housekeeping.
- **The radiance map update costs 0.18 ms every frame, in every scene, dry or wet.** It is the largest single reflection cost and today it buys only ambient light. Once it also carries the reflections it is well spent, and a lower update cadence becomes the main saving (new order R1-6).
- **Using the radiance map for reflections costs 0.05 to 0.08 ms.**
- **Water SSR is about 0.1 ms at this pose**, far below the 0.7 ms of the fixture-world pass. A waterline pose and a lake are still to be measured.
- The engine SSR runs at the coast and costs 0.22 ms there although the ground is dry: the forced policy weight in this scenario pins it on. In play the dry-weather gate decides.

Evidence: `.godot/perf_bench/reflections_b3a/20261117_150117/` (`driver_report.json` key `ab_matrices`; stills and heatmaps under `ab_matrix/`).

### 9.3 B3, second run: dry plains, wet forest, Weather Lung, save spot (2026-11-17)

Run: `-Label reflections_b3b`, scenarios `ssr_plains_dry,ssr_forest_wet,ssr_wl_dam,ssr_save_spot`, `-Rounds 1`, run id `20261117_153047`. Same machine and settings as 9.2. **One round only**, so each delta is a single A-X-A measurement; the baseline spread over 13 windows was 0.03 to 0.04 ms, except dry plains at 0.12 ms. Engine SSR pinned on in every baseline (in play the dry-weather gate switches it off at the dry plains).

Delta of GPU p50 in ms, and in brackets the SSIM of the variant's quiet still against the baseline still:

| Variant | dry plains | wet forest (forced) | Weather Lung dam | save spot (naturally wet) |
|---|---|---|---|---|
| SSR off | -0.12 (1.0000) | -0.21 (0.9984) | -0.21 (0.9963) | -0.28 |
| full size instead of half | +0.02 (1.0000) | +0.17 (0.9985) | +0.21 (0.9975) | +0.32 |
| steps 32 | -0.02 | -0.04 (0.9999) | -0.06 (0.9983) | -0.08 |
| steps 64 | -0.02 | -0.03 (1.0000) | -0.06 (0.9987) | -0.06 |
| tolerance 2.0 | +0.01 | -0.02 (0.9995) | 0.00 (0.9978) | -0.06 |
| reflected source = Sky | +0.01 (0.9959) | +0.03 (0.9448) | +0.05 (0.9832) | +0.08 |

No stills at the save spot. Tolerance 0.12 and 0.5 and both fade values were within 0.03 ms of zero at every pose.

**What this adds to 9.2.**
- Across seven poses the engine SSR at half size costs 0.12 ms (dry plains, where it shows nothing at all) to 0.28 ms (save spot).
- **In the wet forest the engine SSR costs 0.21 ms and changes the picture less than cloud noise does** (SSIM 0.9984 on against off, quiet scene, forced wetness, black fallback). This is the case the storm work kept it for. It supports the trial of tracing only puddles and near ground, to be judged again once the sky fallback is on.
- Steps are not free everywhere: 64 instead of 128 saves 0.06 ms at the Weather Lung and the save spot, about 0.02 ms elsewhere. At the Lung it also changes the picture a little (0.9987), a third as much as switching SSR off. Too small to chase alone; R1-3 may take it if the Lung stills are judged equal.
- The sky source costs 0.01 to 0.08 ms, most where most of the frame is glossy or near.

Evidence: `.godot/perf_bench/reflections_b3b/20261117_153047/`.

### 9.4 R1-6a: what rebuilds the sky radiance map (2026-11-18)

`ab_matrix` at `plains_eye` (pitch -3), noon, clocks held, High, RTX 4070, 1600 x 900, the user's frozen settings; 3 rounds of 6 s windows, A-X-A; baseline spread 0.06 ms on the total. Scenario file `tools/fixtures/perf_bench_sky_radiance_cadence.json`. The GPU time of the far viewport is measured separately (`vp.*.FarRingViewport`); the main view is the total minus it.

| Variant (against Realtime today) | Total GPU | Far viewport | Main view (difference) |
|---|---|---|---|
| Sky process mode Incremental | -0.17 ms | -0.085 | -0.085 |
| Sky process mode Quality | +1.01 ms | +0.48 | +0.53 |
| DaySideAtmosphere frozen (no sky material writes at all) | -0.01 ms (noise) | 0 | 0 |
| Frozen, plus the main Environment's seven fog values set to the far Environment's (fog stays off; nothing visible changes) | **-0.38 ms** | **-0.19** | **-0.19** |
| Frozen, Incremental | -0.16 ms | -0.085 | -0.075 |

Each round agreed within 0.03 ms.

What this settles (design R1-6, sections 1.4 and 5):
- **Today the map is rebuilt twice per frame**, once when the far viewport renders and once when the main view does. Each rebuild costs about 0.19 ms, so the radiance map costs **0.38 ms per frame**, not the 0.18 ms of 9.2. 9.2 measured the Incremental delta, which removes only half of it.
- **The fog flip is a dirtier on its own.** With every sky-material write stopped, the map is still rebuilt twice per frame. Only when the fog values also match does the cost go away. The two Environments render the same Sky with different fog values, so each viewport's `setup_sky` sees the other's values and marks the map dirty.
- **The material writes would also dirty it.** Freezing them alone saves nothing because the flip already rebuilds every frame. After a fog fix, the uncached `ring_radius_meters` write (and the per-frame inputs) would still cause one rebuild per frame. This is inferred, not measured: fog-match without the freeze could not be measured, because the atmosphere rewrites the fog every frame.
- **Incremental is not free.** It halves the cost, -0.085 ms in each viewport, which matches the design's reading of the engine (one importance-sampled layer per frame per dirty sky).
- **A clean sky costs nothing in Realtime.** With no writes and no flip the total fell by the whole 0.38 ms. This matches design 1.2, so the escalation condition of R1-6a does not apply.

Consequences for R1-6:
- **R1-6d (the fog flip) is needed and comes first.** It is the smaller change and saves about 0.19 ms on its own (est.: one rebuild per frame left).
- R1-6b and R1-6c (cadence) then take the remaining 0.19 ms down to the refresh rate of design 5.
- Total available is about 0.38 ms on High, twice what 10.3 assumed. That leaves room under the user's +0.2 ms rule for R3 (the far mirror).
- The matching values used here are the engine defaults the far Environment has (fog off, density 0.01, light colour (0.518, 0.553, 0.608), energy 1, sun scatter 0, aerial perspective 0, sky affect 1).
- R1-6d must keep the underwater and cave looks. In water and caves the main fog is really on and is applied in the cubemap pass. The design's 4.5 handles that case.

Evidence: `.godot/perf_bench/refl_r16a/20261118_034642/`; `driver_report.json` and `report.md` copied to `.godot/audit_baseline/common/reflections/r1_6a/` (kept by the prune).

## 10. Revised orders after Phase B (2026-11-17)

This section supersedes R1 of section 6 and adds R1-6 and R3a. It rests on the measurements of section 9 and on two Lead designs made the same day (receiver audit for R1-1; water composition and march for R1-2 and R1-5). User decisions of 2026-11-17: the water may change to a realistic look, with before/after stills for sign-off and nothing shipped before that; on High, reflections may cost at most +0.2 ms over today at any measured pose, and only where stills show a clear gain (more goes to Very High and above; cheaper is welcome); wet ground may be trialled with SSR on puddles and near ground only.

### 10.1 What changed in the plan

- **D1 is confirmed** (9.1). Turning the sky on as the reflected environment is the largest look change available and costs 0.05 to 0.08 ms.
- **The switch touches every surface**, so R1-1 is a receiver-by-receiver order, not one line. Engine facts behind the design (read in the engine source):
  - `RADIANCE = vec4(0, 0, 0, 1 - v)` scales only the sky term by `v`; SSR hits and direct light are untouched; `v = 1` is identical to not writing it.
  - The engine's specular occlusion without bent normals is `min(8 x luminance(ambient x AO), 1)`. It is the only thing that dims reflections at night, in storms and in caves.
  - `ambient_light_sky_contribution` does not reach reflections. Underground and underwater therefore need their own fade.
  - `f0 = 0.16 x SPECULAR^2` and `f90 = clamp(50 x f0, metallic, 1)`. The water's helper assumed a linear mapping: its F0 today is 0.0066 with f90 0.33, a third of water's 0.02.
- **The engine SSR's settings matter little** for cost and, so far, for look (9.2, 9.3): steps 64 instead of 128 saves at most 0.06 ms, tolerance and fades nothing. R1-3 shrinks to housekeeping. Its values are looked at again once the sky fallback and the far mirror exist, because only then is there something for a hit to differ from.
- **The radiance map update is the largest reflection cost**: 0.18 ms every frame. New order R1-6.
- **Look changes are built behind one transitional switch** (10.2), as the Physical haze was, so that other leads' captures do not move while this work is in the tree and so that "before" and "after" stills come from one run.
- **Cloud data is published for reflections** (R3a, 10.5), by a user-approved scope addition.

### 10.2 The transitional switch

`ringworld_reflection_model` (global shader parameter, 0 = today, 1 = new), owned by one small C# class. Default 0 until the user signs off. It is not a menu row; the capture tooling sets it. At 1:
- the main Environment's reflected source is Sky (at 0 it stays Background);
- the water uses the new composition and march (at 0 its present code runs unchanged);
- the few receiver changes that are visible even with a black environment (stand-in and scale-anchor `SPECULAR` scaled by haze) apply.

Every other receiver edit is a no-op while the environment is black and needs no branch. At sign-off the default becomes 1 and a cleanup order deletes the switch and the old code paths.

### 10.3 Budget (High preset, RTX 4070, 1600 x 900, user's settings)

Rule: the sum may rise by at most 0.2 ms over today at any measured pose.

| Item | Today | After | Basis | Order |
|---|---|---|---|---|
| Sky radiance map update | 0.18 ms every frame | 0.05 to 0.09 ms at a half to quarter cadence | today measured; after estimated | R1-6 |
| Radiance read for reflections | 0 | +0.05 to +0.08 ms | measured | R1-1 |
| Engine SSR while wet (half size) | 0.18 to 0.24 ms | the same; an estimated 0.05 to 0.10 ms less if the near-only trial is accepted | today measured | R1-4 |
| Water march | 0.09 ms at the lifted coast pose | not above today | today measured; after to be measured at a waterline and a lake | R1-5 |
| Water composition | part of the water shader | no change expected | estimate | R1-2 |
| Cloud data publication | 0 | at most 0.03 ms: no new march, the existing images are published; one small resolve only if the design needs it | estimate | R3a |
| Far mirror: copy, distance channel, mips | 0 | about 0.10 ms | estimate | R3 |
| Far mirror: lookups in receivers | 0 | 0.02 to 0.05 ms | estimate | R3 |
| **Sum, wet plains** | **about 0.4 ms** | **0.45 to 0.6 ms** before the R1-4 trial | | |

R1-6 is what keeps the sum inside the rule once R3 is added. If its saving does not materialise, the far mirror's mip chain and resolution are the first things to cut.

### 10.4 R1, revised

Each order states what must stay identical. "Probe" is `tools/perf_bench.ps1 -ScenarioFile tools/fixtures/perf_bench_reflections.json` (kind `reflection_probe`); "matrix" is `tools/fixtures/perf_bench_reflection_costs.json` (kind `ab_matrix`). Until `tools/perf_bench.ps1` learns the two kinds, the verdict to read is the driver's line in `console.log`.

**R1-0 Switch, source and pins.** Implementer; C#. **Implemented 2026-11-17, uncommitted, model-0 identity stills still to be taken** (status and files in section 0). Full order: `docs/work-orders/reflections/R1-1-design.md`.
- A class that owns `ringworld_reflection_model` and writes the main Environment's reflected source (Sky at 1, Background at 0) from `RingFarViewportColorPipeline.ConfigureMainEnvironment` and on a change. A small include, `materials/ring_reflection_model.gdshaderinc`, declares the global once for every shader that branches on it.
- Pin the far and the regional far Environment to `ReflectionSource.Background` as constants (`RingScaledFarViewport.cs`, the two environment constructors). Today both copy the main value: the whole-ring far resolves to the radiance map either way, the regional far would otherwise pick up a sky it does not have.
- New headless test `reflection_environment_contract_test` with a manifest entry.
- Identical: everything at model 0; the far textures at both values.

**R1-1 Receivers.** Full orders (R1-1-0 probe tooling, R1-1a, R1-1b, R1-1d), the receiver audit and the far-environment analysis: `docs/work-orders/reflections/R1-1-design.md`. Not started.
- **R1-1a Holds** (identical to today at both values): `imported_tree.gdshader` and `imported_tree_impostor.gdshader` write `RADIANCE = vec4(0, 0, 0, 1)`; `ring_grass_cards.gdshader` writes `SPECULAR = 0.0`. Reason: B1 showed the visible change on trees is in crown interiors and on trunks, which cannot see the sky, and the shader has no crown-visibility term; grass cards are a diffuse model that states "no specular". Both holds are lifted only together with a visibility term.
- **R1-1b Sky visibility and haze.** New global `ringworld_sky_reflection` (the share of the sky the camera's surroundings can see): published by `scripts/day_side_atmosphere.gd` as underground sky access times (1 - underwater factor). New include `materials/ring_sky_reflection.gdshaderinc` with `ring_sky_reflection_radiance(visibility)`. Used by `ring_poi_architecture.gdshaderinc`, `ring_construction_surface.gdshader`, `ring_emitter_standin.gdshader`, `ring_scale_anchor.gdshader` (visibility 1; the last two also scale `SPECULAR` by sqrt(mean T) at model 1) and `voxel_ring_terrain.gdshader` (visibility = mean haze T). Extend `underground_environment_lighting_smoke_test.gd`.
- **R1-1d Terrain** (last: the files are in use by T4-4b and FR-1): `terrain3d_ring_surface*.gdshader`, `ring_terrain_proxy*.gdshader` write `RADIANCE` with visibility = mean haze T, so wet ground and river lines kilometres away do not mirror an unhazed sky. `ring_terrain_lighting.gdshaderinc` gains a variant that returns T.
- No change: standing grass, `ring_tree`, gust debris, hull, and the shaders shared with the far viewport.
- Identical at model 0: everything. At model 1: water (until R1-2), trees, grass cards, ambient light, the far textures, sealed caves (black).

**R1-2 Water composition.** Full design and orders (R1-2a, R1-2c): `docs/work-orders/reflections/R1-2-R1-5-design.md`. Not started; R1-2a depends on R1-0. In short:
- `SPECULAR` is built on 0.353553 (F0 0.02, f90 1). The reflection leaves `ALBEDO`. The body (optical water and foam) is weighted by `1 - reflectance`, with the reflectance from the same split-sum fit the engine uses, not Schlick.
- **The march's hit is added through `EMISSION`**, weighted by that reflectance and the hit confidence `a`, and the sky term is scaled by setting `SPECULAR = 0.353553 x sqrt(k x (1 - a))` (k = haze). With F0 exactly 0.02 this scaling is linear on the whole environment term, so it is equivalent to mixing the hit over the environment. The design chose this over the `RADIANCE` built-in and `render_mode specular_occlusion_disabled`: a render mode cannot branch on the switch and would change the far ocean in the scaled far viewport at model 0, and the route avoids a compile risk on dev7. (The Phase A text of this plan names `RADIANCE` for the water; this replaces it.)
- No haze is applied to a hit (the screen pixel already carries the haze of the mirrored path); the camera-path in-scatter is reduced by reflectance x confidence.
- The storm factors leave `SPECULAR` (the radiance pass already darkens the sky and hides the sun under a storm); `storm_ssr_keep` stays until the source contains clouds (R3, R3a).
- `ring_ocean_global.gdshader` gets the same model at model 1, so near and far water agree.
- The water takes `ringworld_sky_reflection` once R1-1b exists (caves, underwater).
- Between R1-0 and R1-2a, water at model 1 counts its reflection twice: no water stills for sign-off before R1-2a.
- Menu rows and persisted settings do not change until R1-8.
- Known consequence to show the user: far water at roughness 0.46 will reflect less sky toward the horizon than today's fixed blue. The physical lever is the far roughness.

**R1-3 Housekeeping.** Remove the inert `roughness_quality` writes and the launcher's second write of `ssr_max_steps`. One candidate value change: 64 steps on High (0.06 ms at the Weather Lung and the save spot, 9.3), only if Lung stills at model 1 are judged equal.

**R1-4 Trace only where it shows** (after R1-1d, same files): far river lines, far stand-ins and wet ground beyond a few hundred metres stay above the 0.7 roughness cut; the user-approved trial compares this against today at the wet poses, by stills and by the matrix.

**R1-5 Water march.** Orders R1-5a and R1-5c in `docs/work-orders/reflections/R1-2-R1-5-design.md`. Not started. In short: a screen-uniform, perspective-correct march to the horizon with an adaptive step count; thickness relative to distance plus a same-surface test for grazing terrain; a ray that reaches its vanishing point on a backdrop pixel returns that pixel as a hit at infinity (guarded for downward rays and for a high camera), which puts the arch and the far ring into the water when they are on screen. High: 12 steps, camera distance to be set by the budget (600 m proposed, 400 m if it measures above +0.2 ms). At model 1 only until sign-off.

**R1-6 Radiance cadence.** New. Design and orders: `docs/work-orders/reflections/R1-6-design.md`. Not started. The realtime radiance map costs 0.18 ms per frame and its content changes slowly. Recommended design: keep the one Sky that the main and the far view share, in Realtime mode; move every sky input that changes between frames into global shader parameters, so the far viewport's visible sky stays identical; the engine then rebuilds the map only when the sky material itself is written, and a small controller writes an epoch value on input-change thresholds, on discrete events, or every 0.5 s. Estimated saving 0.13 to 0.17 ms standing, none in fast twilight flight, in water and in caves. **Its first order (R1-6a) is a measurement**, because two things from reading the engine do not match 9.2: Incremental mode should still cost about 0.1 ms per frame, and the map may be rebuilt twice per frame today (a fog-setting mismatch between the main and the far Environment is the suspected cause). Must not change ambient light visibly and must not touch the far image.

**R1-7 Sign-off.** One sheet, model 0 against model 1, same run: every probe pose; a real storm and its aftermath (`tools/wet_world_capture.gd` trailing-edge views), not forced wetness; night; a cave; a megastorm frame; the emitter bore; the Weather Lung; coast, lake and river. Exposure held during capture.

**R1-8 Cleanup after sign-off.** Default 1; delete the switch and the old code paths. **User decision (2026-11-17): the Water tab rows Fresnel F0, Fresnel strength and SSR strength are removed here and fixed at their physical values** (F0 0.02, strength 1). With it: the matching properties in `RingGraphicsQualityController`, `RingWaterModel` and `GameplayWaterDefaults`; the settings smoke tests (`scripts/Tools/WaterSettingsSmoke.cs`, `tools/water_rendering_smoke_test.gd`, `tools/water_ssr_live_controls_gpu_test.gd`); and persistence: a saved file that still holds the retired keys must load without an error or a `LastError` text, and the keys must be gone after the next save (today an unknown key sets "Ignored invalid gameplay water override", and the user's file holds `WaterSsrStrength`). The smoke test gets a case for exactly that. The R1-5 rows (SSR ray length, far tolerance) follow the same route if R1-5 is accepted. Water part of the order: `docs/work-orders/reflections/R1-2-R1-5-design.md`.

Order: R1-0 first (done, see section 0). Then R1-1a, R1-1b and R1-2a (disjoint files; all three include `ring_reflection_model.gdshaderinc` or the sky-visibility include). R1-5 after R1-2a. R1-1d and R1-4 when the terrain files are free. R1-6a (measurement) any time.

### 10.5 R3a: publish the cloud data

**Goal.** Other passes can read what the cloud pass already computes each frame: cloud colour, coverage, and the transmittance-weighted distance. Reflections then get clouds from the real cloud image, and a hit on a cloud can be placed at the cloud's distance. **Clouds are not written into the main depth buffer.** **The cloud image itself must not change**, proven by a byte compare.

**What exists** (read in `addons/SunshineClouds2/`):
- `SunshineCloudsCompute.glsl` writes `output_color_image` (RGBA16F: colour with haze applied at the cloud distance, alpha = density) and `output_data_image` (RGBA16F: `.b` = `ring_cloud_depth_resolve(...)`, the transmittance-weighted distance in metres from `SunshineCloudsDepth.glslinc`; `.a` = log2(1 + opaque depth), or -8 for sky).
- Resolution is Native, Half, Quarter or Eighth. With interleave (2 x 2 or 4 x 4) one pixel per block is marched per frame and `SunshineCloudsInterleaveFill.glsl` fills the rest. History is accumulated in ping-pong images.
- `SunshineClouds.gd` already publishes the accumulated colour and density as a `Texture2DRD` (`get_cloud_backdrop_texture`), with the wrapper lifetime and invalidation handled.
- `tools/cloud_depth_weighting_gpu_test.gd` tests the distance helpers.
- The cloud pass runs post-transparent, so a reader in the opaque or transparent pass sees the previous frame's clouds.

**Design points the Lead must settle (R3a-0).**
- *Publication point*: the images the cloud display pass itself reads, after the interleave fill and the temporal accumulation. Never the raw march output of an interleaved frame.
- *Form*: two `Texture2DRD` globals (colour and data), the view-projection the clouds were marched with (unjittered), and a parameter vector: texel size, resolution divisor, a valid flag and a generation counter. Valid is 0 on the first dispatch, after a resize, after a teleport or history reset, and while the cloud pass is off.
- *Reduced resolution*: readers sample with coverage-weighted bilinear filtering. Plain bilinear filtering of the distance across a cloud edge averages a near cloud with a far one or with "no cloud", and that is wrong; the distance of a tap counts in proportion to its coverage, and a tap below a coverage threshold has no distance.
- *Temporal reconstruction*: the published image is the accumulated one, so it is already reconstructed. The reader reprojects by direction (clouds are far) with the published matrix.
- *Half-float limits*: `.b` saturates at 65,504 m and has about 1 part in 1,024 of precision (2 m at 2 km, 32 m at 32 to 65 km). The cloud handoff distance on High is of the same order, so the reader treats non-finite values and values at or above 65,000 m as "far" (infinity). That is correct for placement: beyond that distance parallax is nil.
- *Night*: the lighting march is skipped; colour is ambient only and may be near zero. Coverage and distance stay valid. Readers must not infer "no cloud" from a dark colour, and the night lake still must show cloud silhouettes against the planet-lit sky.
- *Where it is consumed*:
  1. The far-mirror capture (R3-1) uses the cloud distance for its distance channel on cloud-covered pixels, so a cloud reflection is placed by parallax like terrain (it matters for a high camera and for water far from the camera). Its colour needs nothing extra: the captured image already contains the clouds.
  2. Any reflection that reads a colour source without clouds (the water march reading the screen texture, including the hit at infinity of R1-5) composites the cloud over it: `colour x (1 - coverage) + cloud colour`. This gives clouds in water before the far mirror exists, and lets `storm_ssr_keep` go.
  3. A ray that ends on a cloud-covered sky pixel takes the cloud distance as its hit distance.
  4. Off-screen directions have no screen-space cloud data. They stay with the radiance map (R2d). Optional later step: a direction-keyed panorama that accumulates the clouds the screen has seen.

**Design done (R3a-0):** `docs/work-orders/reflections/R3a-design.md`. Its main results, which correct the points above where they differ:
- Published are the accumulated colour image and the accumulated data image that the post pass reads. The colour is straight (not premultiplied), with haze and lightning glow already in.
- Two `Texture2DRD` globals and a direction matrix, swapped on the render thread as the contact shadows do. The publication adds no GPU work.
- `valid` drops only when there is no data. A history reset or a resize does not drop it, because the image is complete after every callback.
- Readers take a coverage-weighted harmonic mean of the distance; "far" counts as 1/D = 0.
- The images are allocated as RGBA32F while the shaders declare rgba16f, so the precision of `.b` depends on the driver. No cloud shader is changed: the R3a-1 test measures the precision and readers keep the half-float-safe rules.
- **Capture hold.** Commit 78b8425 (test-speed B7b) added a debug-only `capture_hold` to `SunshineClouds.gd` that skips the march while held. R3a-1 builds on it. The published snapshot has two parts: content (textures, matrix, serial) changes only after a complete march, liveness is refreshed every main-view frame, so `valid` stays 1 during a hold and a half-written image is never published.
- **Main view only.** The cloud pass has no main-view filter of its own. The check sits in the cloud callback and gates only the publication, not where clouds render. When another view's callback runs, the globals stay bound and the matrix stays the main view's last one, but `valid` is 0 until the next complete main-view march and for two frames after the other view last rendered, so readers fall back instead of reading a foreign view's clouds. Open question in the design (its section 9): whether any other view (minimap, capture viewport) renders with the main compositor at all.

**Orders.** R3a-1 to R3a-3 are written out in the design file. None is started. `SunshineClouds.gd` and `RingSunshineCloudBridge.cs` are free since B7b was committed.
- **R3a-0** Lead design of the points above, including the exact premultiplication of the colour image and what the display pass does between these images and the screen. Done.
- **R3a-1** Implementer: publication in `SunshineClouds.gd` and the bridge (`scripts/RingSunshineCloudBridge.cs`), extending the existing backdrop publication. No change to any cloud shader. New GPU test `cloud_publish_identity_gpu_test`: with clocks, noise and jitter held, read back the cloud colour image, the cloud data image and the final frame with publication off and on; the bytes must be equal.
- **R3a-2** Implementer: include `ring_cloud_reflection.gdshaderinc` (sample by direction: premultiplied colour, coverage, distance, valid), with a parity test of the reprojection and of the coverage-weighted filter.
- **R3a-3** Implementer: consumption 2 and 3 in the water (at model 1), then 1 in the far-mirror capture when R3-1 exists.
- Verification: the byte compare; `cloud_depth_weighting_gpu_test` and `compositor_temporal_stability_test` unchanged; `GPU.Clouds` unchanged within noise in the matrix; stills of a lake under broken cloud by day, at night and under a storm; a flicker movie while turning (history resets must fade the cloud layer of the reflection out, not flash).
- Cost: see 10.3. Depends on R1-2a and R1-5 for consumption 2 and 3, on R3-1 for consumption 1. R3a-0 and R3a-1 can start now.

### 10.6 Open decisions

Decided: the Water tab rows Fresnel F0, Fresnel strength and SSR strength are removed at cleanup (user, 2026-11-17; see R1-8).

For the primary agent:
1. Offering the user a reset of their water SSR overrides (32 steps, 8 refinements, 1,000 m) to the new High values once R1-5 exists.
2. R1-6: sky inputs as global shader parameters (recommended by the design; touches four tests) or a snapshot-uniform fallback.
3. R3a: keep the lightning glow in what reflections read (recommended; it arrives one frame late).

For the user, with the sign-off sheet:
1. Trees and grass keep today's look (no sky sheen) until a canopy-visibility term exists. Accept as the interim?
2. Metal undersides and shafts will show haze where ground should be until R2a. Accept as the interim, or hold the switch until R2a is in?
3. Far water will reflect less sky toward the horizon than today's fixed blue. Accept the physical result, or study the far roughness?

## 11. Sources

Engine:
- Godot PR 111210, "Overhaul screen-space reflections" (merged 2025-10-21, milestone 4.6): https://github.com/godotengine/godot/pull/111210
- `godotengine/godot` `master`: `servers/rendering/renderer_rd/shaders/effects/screen_space_reflection*.glsl`, `servers/rendering/renderer_rd/effects/ss_effects.cpp`, `servers/rendering/renderer_rd/forward_clustered/render_forward_clustered.cpp`, `servers/rendering/renderer_rd/shaders/forward_clustered/scene_forward_clustered.glsl`, `servers/rendering/renderer_rd/storage_rd/render_scene_data_rd.cpp`, `doc/classes/Environment.xml`

Screen-space and planar:
- R. Genin, "Screen Space Planar Reflections in Ghost Recon Wildlands": https://remi-genin.github.io/posts/screen-space-planar-reflections-in-ghost-recon-wildlands
- A. Cichocki, "Optimized Pixel-Projected Reflections for Planar Reflectors", SIGGRAPH 2017 Advances in Real-Time Rendering: https://www.advances.realtimerendering.com/s2017/PixelProjectedReflectionsAC_v_1.92_withNotes.pdf
- S. McAuley, "The Challenges of Rendering an Open World in Far Cry 5", SIGGRAPH 2018 (why planar reflections were dropped for SSR): https://www.advances.realtimerendering.com/s2018/The%20Challenges%20of%20Rendering%20an%20Open%20World%20in%20Far%20Cry%205%20(With%20Notes).pdf
- B. Grujic, C. Cutocheras, "Water Rendering in Far Cry 5", GDC 2018 (GDC Vault session 1025033)
- T. Stachowiak, "Stochastic Screen-Space Reflections", SIGGRAPH 2015: https://www.ea.com/frostbite/news/stochastic-screen-space-reflections
- AMD FidelityFX SSSR: https://gpuopen.com/manuals/fidelityfx_sdk/samples/stochastic-screen-space-reflections/
- M. McGuire, M. Mara, "Efficient GPU Screen-Space Ray Tracing", JCGT 2014: https://jcgt.org/published/0003/04/04/paper.pdf
- Y. Uludag, "Hi-Z Screen-Space Cone-Traced Reflections", GPU Pro 5 (book chapter; cited from memory)
- B. Wronski, "The future of screenspace reflections": https://bartwronski.com/2014/01/25/the-future-of-screenspace-reflections/ and the AC4 GDC 2014 slides: https://bartwronski.com/wp-content/uploads/2014/03/ac4_gdc.pdf
- M. Valient, "Reflections and Volumetrics of Killzone Shadow Fall" / Killzone graphics, GDC 2014: https://www.guerrilla-games.com/media/News/Files/GDC2014_Valient_Killzone_Graphics.pdf
- Valve Developer Community, "Expensive water": https://developer.valvesoftware.com/wiki/Expensive_water
- Unreal Engine, planar reflections: https://dev.epicgames.com/documentation/en-us/unreal-engine/planar-reflections-in-unreal-engine

Environment maps, impostors, occlusion:
- S. Lagarde, A. Zanuttini, "Local Image-based Lighting with Parallax-corrected Cubemaps", SIGGRAPH 2012 Talks, DOI 10.1145/2343045.2343094
- A. Courrèges, "GTA V - Graphics Study" (environment cubemap rendered every frame): https://www.adriancourreges.com/blog/2015/11/02/gta-v-graphics-study/
- M. Mittring, B. Dudash, "The Technology Behind the DirectX 11 Unreal Engine Samaritan Demo", GDC 2011 (billboard reflections): https://www.nvidia.com/content/pdf/gdc2011/gdc2011epicnvidiacomposite.pdf and https://docs.unrealengine.com/udk/Three/ImageBasedReflections.html
- S. Lagarde, C. de Rousiers, "Moving Frostbite to Physically Based Rendering", SIGGRAPH 2014: https://blog.selfshadow.com/publications/s2014-shading-course/frostbite/s2014_pbs_frostbite_slides.pdf
- J. Jimenez et al., "Practical Real-Time Strategies for Accurate Indirect Occlusion" (specular occlusion): https://www.activision.com/cdn/research/Practical_Real_Time_Strategies_for_Accurate_Indirect_Occlusion_NEW%20VERSION_COLOR.pdf

Heightfield:
- "Approximating water effects using terrain height maps, part 1: Reflections" (UHawk VR): http://blog.uhawkvr.com/rendering/approximate-terrain-reflections/
- A. Tevs, I. Ihrke, H.-P. Seidel, "Maximum Mipmaps for Fast, Accurate, and Scalable Dynamic Height Field Rendering", I3D 2008 (cited from memory)

Wet surfaces and racing:
- S. Hargreaves, "MotoGP: road reflections": https://shawnhargreaves.com/blog/motogp-road-reflections.html
- S. Lagarde, "Water drop" series, parts 2b and 3a/3b (dynamic rain, physically based wet surfaces); summary: https://www.fxguide.com/?p=43362
- N. Tatarchuk, "Artist-Directable Real-Time Rain Rendering in City Environments", SIGGRAPH 2006: https://advances.realtimerendering.com/s2006/Chapter3-Artist-Directable_Real-Time_Rain_Rendering_in_City_Environments.pdf
