# Storm and local weather revamp plan

Status: approved 2026-10-25. Builds on `docs/weather-system-checklist.md` (phases 1-6), which stays the record of the existing system.

## Handoff (closed 2026-10-29, RTX 4070 desktop)

**State: the storm revamp is closed by user decision (2026-10-29).** The user accepted the result and asked for it to be merged to `main`. Branch `storm-revamp-wip`: the WIP snapshot e2485b7 (over `main` 3110b7e) plus the 2026-10-28/29 working tree, verified at batch close (section "Batch close record" below) and committed and merged by the coordinator. Everything not listed under "What shipped" is in the Deferred list; nothing else is in flight.

Design docs (each has its own status notes):
- [S0 storm light](storm-revamp-s0-storm-light-design.md)
- [S1 precipitation](storm-revamp-s1-design.md)
- [S2 wet world](storm-revamp-s2-wet-world-design.md)
- [S3 storm arrival](storm-revamp-s3-storm-arrival-design.md)
- [S4 exaggerated storms](storm-revamp-s4-design.md) and [S4 sculpt v2](storm-revamp-s4-sculpt-v2-design.md)
- [S5 rain audio](storm-revamp-s5-rain-audio-design.md)
- [S6 cloud vertical structure](storm-revamp-s6-cloud-layers-design.md)

Next projects (design only, nothing implemented): [megastorm](megastorm-design.md) and the [atmospheric containment field](containment-field-design.md).

### What shipped

**S0 storm light.** One per-location storm term D from the macro weather map (`materials/ring_storm_light.gdshaderinc`), read by every receiver: sun and shadows fade with it, ambient darkens, haze and sky darken along the view path, auto exposure is capped under storms (eye cap const 1.25: under-storm ground 0.20-0.27x clear). Plain `StandardMaterial3D` objects darken through `RingStormLightTint`. D ownership (user decision 2026-10-29, "fronts dark by rain"): thunderstorm sculpt systems own D; MajorFront/ExceptionalFront get D from their rain (heavy dark, drizzle grey); dry Cs/As/Ns give D = 0 (macro map layers 4/5, `RenderWeatherFlagFront` 32). Sun-specular keep (1 - F)^16 (no glint under a closed core); far arch not dimmed by a storm at the camera; `ring_storm_level_far` for the far viewport. Settings: "Storm darkness", "Storm shadow fade".

**S1 rain.** Below/in/above-cloud precipitation (`PrecipitationAltitude`, flight hook `QueryPrecipitationColumn`); precipitation occlusion map (render layers 19 NoPrecipitationOcclusion, 20 Lights; map camera not physics-interpolated); raster rain streaks and splashes after the clouds (`RingRainStreaksComposite.gd`, `ring_rain_streaks.glsl`, budgets 4k-20k per preset, 0.04-0.12 ms); the grey veil only in violent cores; snow and distant curtains respect the map. Settings group "Rain".

**S2 wet world.** Stateless wet map (`RingWetWorld`) on every receiver (terrain, POI, trees, grass, water, construction), puddles, ripples, fat-drop spots, lens droplets, "Wet world" settings group (old terrain wetness settings migrate). Perf at 720p Very High: open heavy rain +0.23 ms, forest +0.60 ms (accepted at Very High and up; half-size wet SSR on Low/Medium/High).

**S3 storm arrival (batches 1-3).** `StormArrival.cs` arrival maths (drift-aware), rain history API, `RingWindQuery`, gust-front wind (25-31 m/s held through the core), fat drops, hail, haze gate Core x Severity, post-rain mist (sigma 2e-3, slew 2/32 per publish), rainbows, G1 leading-arc lightning stream, arrival audio, "Storm arrival" settings group. **D1 gust debris** (`RingGustFrontDebris.cs`, `ring_gust_debris*`): leaves, dust puffs and spindrift at the gust front, preset column, no debris under a roof or above 150 m; +0.043 ms at full gust (High), 0 when calm. Time-lapse capture mode in `storm_arrival_capture.gd`.

**S4 exaggerated storms.** Sculpt v2 baked storm profile map (1024² x4, 150 km half extent, map-off fallback), towers and anvils, the Nimbostratus deck under storms, storm light shortcut and per-ray storm ambient; the invisible-wall fix (general band/exit rule, S6 WO3a). WO-E lightning: red sprites and blue jets readable at 150-1000 km, "Reduced flashes" at the pulse source, "Lightning frequency" and "Upper-atmosphere lightning" settings.

**S5 audio.** The rain sound field (stereo bed, 4 directional sectors x materials, roof voices, directional wind, distant-storm voices) replaces the 32 point emitters; shelter classes from the occlusion map (probing continues while post-rain wetness > 0.05, so eave drips continue); thunder muffled by shelter; 68 synthesised assets (`tools/media/weather_sfx_gen.py`); near and far thunder pools; weather audio settings persist.

**S6 cloud vertical structure (WO1-WO8).**
- WO1: 18 km hard cap, layer table (`resources/clouds/cloud_layer_table.tres`, `scripts/clouds/`), CPU column (`RingCloudLayers`), `CloudLayerColumnTest`, fingerprint regenerated (ExceptionalFront Cb tops only).
- WO2: macro map layers 2/3 (layer coverage) and the layers UBO; readback vs the CPU twin within 4.9e-4.
- WO3a: general per-ray band and exit rule (replaces the wall fix), storm support 1.45, column-exact light march (about -0.5 ms under storms).
- WO3b: volumetric low (0.5-2 km) and mid (2-7 km) layers in `sampleScene` and the light march (`RingCloudLayerSample.glslinc`); fair base 2 km, weather base down to 500 m. Section 3.8: S6 layers own the non-storm Ns/As decks (closure, deck AO, column light, deck colour); storms keep the S4 look.
- WO4: cloud shadows sample the layers (terrain range 7000 m).
- WO5: analytic cirrus shell at 9.5 km (`materials/ring_cirrus_shell.gdshaderinc`), storm-owned suppression, closed-form shadow.
- WO6: far parity: the far layer sky (`ring_far_cloud_layer_sky.gdshader`, `ring_far_cloud_layers.gdshaderinc`, `RingScaledFarViewport.CloudLayers.cs`) reads the Sunshine macro map with the complementary handoff weight, plus the camera-centred fine far layer atlas (128 x 64, CPU, 0.5 ms/frame budget, timed refresh 30 s since batch close) and the whole-ring atlas beyond; far camera interpolation bug fixed.
- WO7: diurnal cumulus only (the front sequence is tabled by the user).
- WO8: "Fair-weather cloud base" (800-3000 m) and "Weather cloud base" (300-1500 m) renamed rows; `NaturalCloudTopAltitudeMeters` retired (dropped on load, gone on the next save; migration covered by `gameplay_cloud_tuning_smoke_test`); new "Low/Mid/High cloud amount" and "Daytime cumulus".

**Other.** The local weather cache signature includes `NaturalWeatherLifecyclesEnabled`; POI light cull masks include bit 19; the loading list (`RingWorldLoadingResources`) carries the revamp's `.gdshader` files and compositor scripts (no `.glsl` RDShaderFiles, see the batch close record); terrain regression goldens regenerated on the 4070 (2026-10-29); GPU FIFO queue for parallel agents (`<scratchpad>/gpu_queue.ps1`).

### How to A/B it

- `CameraIndependentDebugMask` (grave-key menu, Clouds tab, debug row; range 0..511): **64** = no S6 cloud layers (single legacy 2-6 km band), **128** = no cirrus shell, **256** = S6 deck colour off. Older bits: 1 background band only, 2 clear sculpt mask (also clears storm ownership), 4 skip ray mask, 8 constant macro, 16 light march breaks on the background band, 32 legacy shader path.
- Far viewport (environment, read at startup): `RING_FAR_LAYERS_OFF=1` (pre-WO6 far: no layer sky, the clipmap draws its own body), `RING_FAR_FINE_OFF=1` (no fine far atlas: the whole-ring atlas beyond the macro map), `RING_FAR_FINE_EXACT=1` (per-texel system collection), `RING_FAR_FINE_VERIFY=1` (counts fine texels that differ from the exact path), `RING_FAR_FINE_RECENTRE_M=<m>` (short recentre threshold for pop tests).
- Storm shape: `RING_STORM_SHAPE_LEGACY=1` (pre-sculpt-v2 shape); `storm_anvil_capture --perf-cases` also takes `dfetch_off` (storm-light D without its ownership/front fetch, cost A/B only).
- Settings that switch whole features off: "Storm darkness" 0, "Rain" group, "Wet world" group, "Storm arrival" group (gust front strength, debris), weather audio.

### Batch close record (2026-10-29, RTX 4070 desktop)

**Fixes at batch close.**
- Fine far layer atlas timed refresh 8 s -> 30 s (`RingScaledFarViewport.CloudLayers.cs:28`); drift, manual-weather and discontinuity triggers unchanged.
- `NaturalCloudTopAltitudeMeters` migration test: `GameplayCloudTuningProbe.VerifyRetiredKeyMigration`, run by `gameplay_cloud_tuning_smoke_test` (a pre-WO8 `.tres` loads with no error, keeps its other keys, does not apply the retired value, and the next save drops the key).
- Stale test expectations: `SunshineCameraIndependenceTest` (manual MajorFront Cb now also carries the Front and LowersBase flags) and `grave_menu_interaction_test` (the Wind tab has 7 sections since S3's "Storm arrival" group).
- Diagnostics: `cloud_layers_capture --shots=fineduty` (fine atlas duty cycle with the cloud clock running) and `storm_anvil_capture --perf-cases=...dfetch_off` (storm-light D without its second map fetch).

**Gates.** C# build clean (0 errors). All 237 headless wrappers in `tools/` plus the weather fingerprint compare (`NATURAL_WEATHER_FINGERPRINT=PASS`), and the GPU gate list (the S6 section 5 table plus the S1-S5 gates), pass on the branch except the failures below, each of which fails identically on `main` 3110b7e (clean worktree import) or on e2485b7:
- `generated_output_policy_test` (30 `res://artifacts` literals in untouched files), `grass_clump_validation_test`, `grass_streaming_contract_smoke`, `grass_quality_gameplay_test`, `distant_rain_curtains_smoke_test`, `legacy_terrain_isolation_test` (GDScript parse error), `rain_surface_column_smoke_test`, `biome_authoring_preview_test`, `world_loading_smoke_test` (the `activation` diagnostics entry added in 425871c has no `ready` key), `terrain3d_gameplay_movement_test` (reverse after handoff 21.5 s vs 10 s limit).
- GPU: `rain_cloud_compositing_gpu_test` (frozen-scene cloud source drift; also on main and e2485b7), `distant_rain_curtains_gpu_test` ("no stable rain contribution"), `water_fft_demand_integration_test`, `cloud_lighting_smoke_test`, `sunshine_gpu_shadow_smoke_test`, `sunshine_storm_persistence_test`, `sunshine_menu_smoke_test`, `sunshine_inside_cloud_seam_test` (stale `FarRingBackdrop/FarRingTexture` node path; hangs), `opposite_ring_lightning_visual_smoke_test` (fails the same localized-contrast check on e2485b7; branch main-view contrast 1.0 vs 3.0 there, gate 4).
- Flake: `quality_preset_contract_test` C6 p=0 (cloud shadow projector not yet enabled) failed once and passed 150/150 on two re-runs.
- Real loading pass (menu Continue through the loading screen into the certified world): `MENU_CONTINUE_TRANSITION=PASS case=main checks=46`; `RingWorldLoadingResources` 59/59 complete, no error; no shader, script or resource errors; F5 boot `MENU_MAIN_SCENE_BOOT=PASS`.
- Testing note: a main worktree seeded with this branch's `.godot/imported` keeps stale Sunshine `.res` (Godot reimports a `.glsl` only when the top file changes, not its includes), so its UBO sizes mismatch and the screen shows uninitialized cloud output. Baselines need all `*.glsl-*` imports deleted and a windowed `--import`.

**Perf record (RTX 4070, 1600x900 High, day, clocks paused, GPU.Clouds mean per 10 s window).**

| Pose (harness) | Windows | GPU.Clouds | Note |
|---|---|---|---|
| Fair, ground pitch 20 (`cloud_layers_capture --shots=perf --fair=-60000,-30000`) | 3 | 1.84 / 1.88 / 2.03 (p95 2.6-2.9) | record 1.9; the auto-picked fair site (10 km, 45 km) is a heavier view, 3.2 |
| Overcast (Ns core over the same site) | 3 | 1.63 / 1.75 / 1.69 (p95 2.3-2.5) | record 1.7 |
| Storm, 40 km outside (`storm_anvil_capture --shots=perf --perf-dist=40000`) | 4 | 3.59 / 3.59 / 3.60 / 3.62 (p95 4.6-4.8) | record 3.6 (S6 3.8 deck ownership measured 3.53, WO5 cirrus +0.07) |
| Storm-light D second fetch, A-B x3 (`dfetch_off`) | 7 | main viewport GPU median 7.24 on vs 7.24 off | below the noise (about 0.05 ms) |

Fine far atlas (clock running, 1600x900 High): stationary at about 62 fps, a build takes about 820 frames (13 s) and then idles until 30 s after its snapshot: duty 0.30, per-frame CPU median 0.003 ms, mean 0.16, p95 0.55 (building frames 0.53 median). Flying at 250 m/s the frame rate fell to about 25 fps, a build took about 890 frames (36 s, longer than the refresh), so it built continuously: duty 0.99, 0.53 ms median, 0.57 p95. The build length is frame-count bound, so below about 30 fps the 30 s refresh no longer caps the duty (deferred).

### Deferred (not in this merge)

**S6**
- WO9: CPU consumers (`PrecipitationAltitude.ResolveCloudBounds` callers, lightning cloud base, storm arrival) still use the legacy band; rain does not yet fall from the rendered layer base. Fingerprint regenerates with it.
- WO10 acceptance items: the faint bright inter-layer streak (`s6fix_c2_40/perf_view.png`); faint concentric shadow arcs at deck edges in the fair near map (step aliasing; jittering the start is a candidate); no numeric shadow alignment test; Ns shadow gaps; the anvil cut at about 1.25x the ellipse is not visually confirmed (preview hero canopy ends at about 0.96); sparkles on dark ground or water on the storm side of the wall shots; Cs radial streaks; the main-view ground layer (ground pixels seen from altitude) gets only the layer-union coverage, not the layer sky.
- WO7 front sequence (warm Ci, Cs, As, Ns phases, cold-front band, precursor reach, `SupportMultiplier` 2): tabled by the user.
- Fair far horizon strip: a 12-row strip at the horizon is +7 to +11 luma brighter than the near-only reference; not lighting (sky gains do not move it); suspect thin or missing cumulus density at grazing rays.
- Fine far atlas: no storm channel (beyond the macro map, storm closure and lighting come from the coarse 245 km atlas); the column superset differs from the per-texel collection at 34 of 8192 texels (max 57/255) at system edges (the superset is the more faithful). After the 30 s refresh the duty is 0.30 stationary at 62 fps but about 1.0 below about 30 fps (a build is about 870 frames); a publish-relative refresh or a time-sliced budget would cap it.
- Far As bank 150 km down the ring not visible on short-handoff presets (`cloud_layers_capture far150`: sky on vs off about 0.4 luma). Unresolved; next step is probing the fine atlas texel at the bank.
- Far deck colour residual: the far sky's deck is about 0.1 bluer than the near at 1.5 km.
- Cs at a short handoff stays about 11 masked MAE off the near-only reference.

**S4**
- WO-T storm travel (user chose about 10-20 m/s fronts; regenerate the fingerprint on purpose, coordinate with WO9).
- WO-D far storm impostors lit by lightning (priority: storms 450 km away at night are invisible, so sprites appear on black). WO-B superstorm, WO-C climate-machine anomalies, WO-F funnels, WO-G haboob, WO-H St. Elmo's fire (the user has not answered whether to defer F/G/H/C/D).
- Look targets from the interrupted look pass: towers 8-15 km wide and 14-18 km tall, anvils with 60-120 km reach, a dark deck top, a dark storm mass from 45 km, visible mammatus.
- The main-world `ring_storm_level` clamps at the map edge, which can stripe distant main-world LODs.
- An unexplained straight edge on the far D patch.

**S3 / S2 / S1**
- S3 H1 shelf cloud (sculpt owner).
- S3 G1: the 0.5x strike-distance criterion is not measurable with a rim-ringing stream.
- S2 WO6 drips (hook ready in the streak pass: push-constant mode 2), WO10 shelter memory, WO11 hail whitening.
- Mist: step ticks (needs per-frame interpolation) and the publish cap stall.
- Front rain ramp: `smoothstep(0.25, 0.65)` on frontal rain is a constant the user may want as a tunable.
- Optional: move the streak vertex lighting after the culls.

**Cross-cutting, pre-existing**
- `Texture ... expected 2DArray, actual 2D` + `Parameter "us" is null` (about 300-400 per run, 1700 in the world-reloading QP contract). Batch-close trace: emitted only when a world is freed or the process quits (in a merged-stream log every one follows the test verdict; a material scan at frames 1-300 found no 2D value on any sampler2DArray). A Texture2DArrayRD-backed global loses its RID and Godot substitutes a 2D default for every material that binds it. Main shows bindings 3/5/7/11 (the `ringworld_height_shadow_l0/l1` globals); the branch adds binding 1 (most likely the S0 `ringworld_storm_map` global; the startup case is already guarded at `RingSunshineCloudBridge.StormLight.cs:401`). No visible effect found.
- Orbit and startup flashes (frames 8/14/20/26/32 of the first about 33 frames, also with the layers off); predate S6.
- The far-flash smoke tests fail at 960x540 on HEAD too (resolution-sensitive).
- The pre-existing test failures listed in the batch close record (all fail the same way on `main` or e2485b7), the `quality_preset_contract_test` C6 p=0 projector timing flake, and `rain_cloud_compositing_gpu_test` needing `--tolerate-cloud-source-drift` (the frozen-scene cloud source is not bitwise stable on main either).

**User-pending**
- Audio audition: `.godot/captures/storm_audio/audition_one_shots.ogg`, `audition_loop_seams.ogg`, `audition_thunder.ogg`, and the in-game rain sound field listening pass (7 steps in the S5 doc).
- Sprite video: scratchpad `storm_sprites_450km_night.mp4`.
- Gust-front lead time: the gust arrives about 150-175 s before the rain; leave or retune.
- Mist strength (about 390 m visibility at full strength; sigma 2e-3).
- Trailing-edge glint (D about 0.5): now (1 - F)^16; confirm.
- Dust puffs read as soft grey orbs (look note).

## Working log (2026-10-26 to 2026-10-29)

Superseded by the handoff above; kept for its evidence and line references. "Uncommitted" and "in progress" below describe the tree at the time of writing.

### Original handoff (paused 2026-10-26 ~05:40) and working notes to 2026-10-29

**State: committed as a WIP snapshot, not merged.** Everything below is in commit e2485b7 on branch `storm-revamp-wip` (pushed to origin), branched from `main` at 3110b7e. Nothing on `main` has changed. The C# build was clean (0 errors) at pause. "Accepted" below means accepted within the revamp; none of it is merged. The work was paused because the subagents hit the weekly usage limit (reset 2026-10-28 7pm Central). Several agents stopped mid-task; their partial edits are in the snapshot and listed under "Unverified at pause".

Design docs (each has its own status/handoff notes appended):
- [S0 storm light](storm-revamp-s0-storm-light-design.md)
- [S1 precipitation](storm-revamp-s1-design.md)
- [S2 wet world](storm-revamp-s2-wet-world-design.md)
- [S3 storm arrival](storm-revamp-s3-storm-arrival-design.md)
- [S4 exaggerated storms](storm-revamp-s4-design.md)
- [S4 sculpt v2](storm-revamp-s4-sculpt-v2-design.md)
- [S5 rain audio](storm-revamp-s5-rain-audio-design.md)

#### Done and accepted (in the WIP snapshot, not merged)

**S0 storm light**
- One per-location storm term `D` from the macro weather map, read through `materials/ring_storm_light.gdshaderinc` by every receiver. Sun and shadows fade with it, ambient darkens, and the haze and sky darken along the view path. Auto exposure is capped under storms.
- Plain `StandardMaterial3D` objects darken through `RingStormLightTint`.
- Settings: "Storm darkness" and "Storm shadow fade".
- Far arch is no longer dimmed by a storm at the camera (`ringworld_haze_sun_irradiance_remote`).
- Gates: haze_consistency 20/20, storm_light_gpu_test 12/12 (re-run PASS at pause), terrain regression 0 px dry. shadow_compose_identity differs only in the intended grass cases plus a known 1 px ocean noise.

**S1 rain**
- `PrecipitationAltitude` gives below/in/above-cloud precipitation, plus the flight hook `QueryPrecipitationColumn`.
- Precipitation occlusion map (`RingPrecipitationOcclusion*`):
  - Render layers: 19 = NoPrecipitationOcclusion, 20 = Lights.
  - Costs 0.38 ms per update; a CPU probe reads it.
- Rain streaks and splashes are drawn as a raster CompositorEffect after the Sunshine clouds (`RingRainStreaksComposite.gd`, `ring_rain_streaks.glsl`).
  - The user's four complaints are all GPU-tested: always lit, clouds drawing over the rain, rain from clear sky, rain ignoring the wind.
  - Per-preset streak budgets: 4k / 8k / 12k / 16k / 20k.
  - The CPU rain field reads the macro-map mirror and costs 0.02 ms per frame.
- The grey rain haze veil is reserved for violent cores.
- Snow and distant curtains respect the occlusion map.
- Settings group "Rain".

**S3 storm arrival (batches 1-2)**
- Pure arrival maths (`StormArrival.cs`) plus an adapter partial. All of it takes each system's drift, so storm travel needs no rework.
- Rain history API: a snapshot plus a continuous TimeSinceRain. S2 reads it.
- Flight wind query: `RingWindQuery`.
- Post-rain mist, now additive: about 390 m visibility at full strength.
- Physically placed rainbows (`RingRainbow*`).
- Gust-front wind (`ModifyWind`) and a wind lock for thread safety.
- Fat drops, hail pellets, and the haze gate `Core x Severity`.
- Arrival audio: gust roar, hail, leaf skitter.
- "Storm arrival" settings group in the Wind tab.
- Low fog patches (E2): NO-GO.

**S4 sculpt v2, V1-V4**
- Baked 2.5D storm profile map, 1024² x4 RGBA16F, 150 km half extent (`RingStormProfileBake.glsl`, `RingStormProfileMap.gd`).
- Map-off fallback behind specialization constant `RING_STORM_FALLBACK`.
- Nimbostratus deck under every storm, plus a background threshold lift to 1.05. The overcast under a storm now closes with no blue sky.
- Storm light shortcut and per-ray storm ambient (GenericData floats 312-316).
- Perf vs HEAD on the 4070: -0.9 ms under a storm, +0.14 ms at 40 km (gate +0.15).

**S4 WO-E (lightning)**
- Red sprites and blue jets in the far viewport: deterministic, air-dimmed, readable at 150-1000 km and across the arch.
- "Reduced flashes" works at the pulse source, with a summed cap.
- New settings: "Lightning frequency" and "Upper-atmosphere lightning".
- The far-flash smoke tests fail at 960x540 on HEAD too. They are resolution-sensitive, not a regression.

**S5 audio**
- The rain sound field replaces the 32 point emitters: a stereo bed, 4 directional sectors x materials, roof voices, directional wind and distant-storm voices.
- Shelter classes with hysteresis, fed by the occlusion map (with a terrain fallback). Thunder is muffled by shelter.
- 68 synthesised assets from `tools/media/weather_sfx_gen.py` (provenance in `resources/audio/weather/LICENSE.md`).
- Pool of near one-shots; near and far thunder pools.
- Weather audio settings now persist.

**Other**
- Fixed a pre-existing bug: the local weather cache signature now includes `NaturalWeatherLifecyclesEnabled` (`RingVolumetricCloudLayer.Wind.cs`).
- New GPU FIFO queue for parallel agents: `<scratchpad>/gpu_queue.ps1`. See memory `agent-watchdog-and-gpu-coordination`.

#### Implemented but not accepted (needs work)

**S2 wet world**
- Built: wet map (`RingWetWorld`, stateless from the S3 history), all receivers, puddles and ripples, fat-drop spots, lens droplets, and the "Wet world" settings group (old terrain wetness settings migrate).
- Dry identity is exact.
- **Perf (follow-up 2026-10-29, S2 doc 10.1):** 720p, Very High.
  - Heavy rain over open ground: +0.23 ms, within the +0.4 ms gate.
  - Forest worst case: +1.13 -> +0.60 ms. The coordinator accepted this for Very High and up (full-size SSR and the terrain film SSR are kept).
  - High: +0.16 / +0.29 ms. Medium: +0.09 ms. Wet-world SSR runs at half size on Low/Medium/High.
- **Sun glint fixed:** the storm's sun-specular keep is now (1 - F)^4, and POI and concrete use it too. No glint at a closed core; at the trailing edge (D about 0.5) the glint is dimmed.
- Not built: WO6 drips (hook is ready in the streak pass), WO10, WO11.

**Storm look pass** (sculpt scale, darkness, glint). It was interrupted mid-iteration. The targets it was working toward:
- towers 8-15 km wide and 14-18 km tall;
- anvils with 60-120 km reach;
- a dark deck top;
- a dark storm mass seen from 45 km;
- visible mammatus;
- under-storm ground at 0.18-0.30x clear with AE on;
- no ocean sun glint under a closed storm, because direct-sun specular must fade by F(D).

  It had begun editing `ring_storm_light.gdshaderinc` and the ocean, terrain-global and scale-anchor materials (05:27). Before the pause the V3 look had: towers reading as tiny posts, a pale deck top, a mid-grey under-deck, and invisible mammatus.

**S3 batch 3** was interrupted. Its tasks:
1. **Rain streak perf regression.** Batch 2 reported GPU.RainStreaks at 0.30 / 0.88 ms (6k / 12k) against 0.041 / 0.075 before. Verify it is real, then fix it.
2. **Gust gap.** Strong outflow wind must persist through fat drops and the core.
3. **D1 gust debris.** New `RingGustFrontDebris.cs` and `ring_gust_debris*` shaders are partially written.
4. **G1 leading-arc lightning.**
5. **One continuous arrival time-lapse mp4.**

#### Unverified at pause (check first next session)

- **`scripts/render/ring_rain_streaks.glsl`:** edited at 05:27 by the S3 batch-3 agent mid-task ("Now write the shaders"). I reimported it at pause and `rain_streaks_gpu_test` then **failed** (exit 1). The compositor became ready, so the shader compiles, but the failing check was not identified before the user stopped the tests (log: `.godot/handoff_rain_streaks_gpu_test.log`).
  - Update 2026-10-28: the log shows every check that ran passed (compositor_ready, chain_order, scene_static, streaks_draw, paused_clock_identical). The run then produced about 3.4 million `get_pixel` out-of-bounds errors (1.1 GB log). `pair()` sized its default region from the root window, but captures come from the 960x540 rig viewport, and with no isolated profile the window was larger. Probable cause: the test harness, not the shader. The test now sizes and clips its sampling regions to the captures. Not yet re-run, so the shader edit is still unconfirmed.
- **Receiver shaders edited at 05:27 by the storm look agent:** `ring_storm_light.gdshaderinc`, `ring_ocean_global/proxy`, `ring_terrain_global`, `ring_scale_anchor`. `storm_light_gpu_test` passed after these edits. `shadow_compose_identity` and the terrain regression have not been re-run.
- **Sunshine shaders:** imported after their last edit (05:04:50), and `sunshine_shader_compile_gpu_test` passed at pause.
- **Debris work files (in the snapshot, possibly incomplete):** `ring_gust_debris*` and `RingGustFrontDebris.cs` may be incomplete. Check they are wired, or remove them.
  - Audit 2026-10-28: mostly written, C# builds, **not wired anywhere** (no scene node, `PresetBudget` never set, not in the loading list, no tests).
  - Bugs: `ring_center` is never published, so the day factor and legacy haze are wrong. `deb_kinds` is passed as an untyped Array (use `Vector4[]`). Off-map points float 1.7 m below the camera (should be dead). There is no AGL < 30 m gate. Leaves pop on and off (binary alive).
  - Missing from spec: preset column, spatial gust, spindrift on snow, all acceptance captures and perf. Removal is simply deleting the 5 files and their `.uid` files.
- **Storm look pass, audit 2026-10-28** (reconstructed from the agent's own transcript):
  - `ring_storm_level_scaled` (`ring_storm_light.gdshaderinc:65`, used by `ring_ocean_global`, `ring_terrain_global`, `ring_scale_anchor`) divides by the coordinate scale. That does not map far-viewport space to the storm frame, which uses a different origin and axes. Rewrite it after `ring_far_weather_shadow.gdshaderinc:61-90`, or revert the 5 call sites.
  - The ocean glint is unresolved. The near `ring_ocean_proxy` still glints; the hypothesis is that GPU D on the proxy reads below the 0.35 fade start while the CPU says 1.0. Check with `ringworld_shadow_debug_mode = 6` before more tuning.
  - `day_side_atmosphere.gd:859`: `STORM_EYE_MIN_SCALE` reads the env var `RING_EYE_MIN_SCALE` (test hack). It must become a const. Under-storm ground reached the 0.18-0.30x target only at scale 4.0 (display luma); the default of 0.4 gives 0.84x.
  - D=0 is an exact identity in every hunk. Two terms react to small D without F gating: proxy `reflection_color *= ring_storm_sky(D)`, and the sun disc hide in `ring_local_sky.gdshader:251`.
  - Sunshine edits (`RingStormShapes` sizeK 2.3, canopy early-out, storm ambient 0.85, haze keep): `storm_profile_map` and the 40 km perf check were not re-run.
  - Targets still open: towers, dark deck/45 km mass, mammatus, glint.
- **APPDATA:** isolation failed for the pause-time test runs, because the profile `appdata_storm_s1` was missing, which left APPDATA empty. As a result, Godot wrote its user data to a repo-local `Godot/app_userdata` folder. That folder has been deleted. The real user profile was not touched.
- **About 400 "Texture needs 2DArray, actual 2D" errors per full-game run** (bindings 1/3/4/7). The source is unidentified, and it is not from the streak pass.

#### Queued 2026-10-28 (in progress)

- **Invisible cloud wall around storms (bug, also latent on main since be98ceb).** In `SunshineCloudsCompute.glsl`, the terminal-exit rule ends a ray when it leaves a storm's widened band (500 m to the storm top, inside the ellipse + 1.5 km) while still below the 2,000 m background floor. Looking out from under a storm, all clouds beyond its edge vanish. Fix: an out-of-band step inside the hull, heading toward the background band, is empty, not terminal.
  - Related: the storm canopy and ragged edge reach about 1.25x the ellipse, but the band and shadow tests assume zero density outside ellipse + slack, so anvils get a hard cut. This one is new in the revamp.
  - The S4 "-0.9 ms under a storm" perf number is probably partly flattered by rays stopping early. Re-measure it after the fix.
- **S6 cloud vertical structure (user decision 2026-10-28).** The cloud range becomes 500 m to 18 km with real-world layering:
  - low (cumulus, stratocumulus, stratus) 0.5-2 km;
  - mid (altocumulus, altostratus, nimbostratus) 2-7 km;
  - high (cirrus, cirrostratus) 7-13 km;
  - only storm towers and anvils reach 14-18 km.

  Low cloud comes down to 500 m only in weather (overcast, fronts, storms); fair-weather clouds keep a higher base. Design: [S6](storm-revamp-s6-cloud-layers-design.md) (being written). The user says this prepares for a further revamp.
- **Done 2026-10-28 (uncommitted, verified on the 4070):**
  - Wall fix: `ciGapToBand` in `SunshineCloudsCompute.glsl` ~:1666. The edge shots in `storm_anvil_capture` "edge" show the clouds beyond the edge restored. Gates sunshine_shader_compile, cloud_density_fastpath and far_cloud_density_parity pass. Perf A-B-A shows no measurable cost (GPU.Clouds about 3.46 ms both ways). S6 WO3a replaces it with the general rule.
  - Look leftovers:
    - **The "ocean glint" was wet land** (`ring_terrain_proxy` puddle specular on the 0.1 sun residual). Fixed with `ring_storm_specular_keep` = 1 - F on terrain proxy, voxel and terrain3d. POI, tree, grass and emitter specular still keep the residual.
    - `ring_storm_level_far` replaces the scaled helper. It is CPU-checked to 0.1 m and fades out beyond the map. `scale_anchor` is back to plain `ring_storm_level`.
    - `STORM_EYE_MIN_SCALE` is a const 1.25, which gives under-storm ground 0.20-0.27x clear (display luma).
    - The proxy reflection darkening and the sun disc hide are gated by F.
    - storm_light 12/12 and haze_consistency 20/20 pass.
  - Note: `--script` runs use the imported shader `.res`. Run `Godot --headless --path . --import` after any `.glsl` change before visual or perf runs.
- **Gate triage 2026-10-28 (4070):**
  - `rain_streaks_gpu_test` passes all 35 checks, splashes included. The S3 "half-edit" was an un-reverted perf ablation: `lit_radiance` had used the CPU scalar light. It is reverted to the per-streak `streak_radiance` (`ring_rain_streaks.glsl:16`).
  - The streak perf regression was not real (the two numbers came from different harnesses). On the real-world harness: 0.043 / 0.076 / 0.119 ms at 6k / 12k / 20k, against 0.041 / 0.075 / 0.12 at S1.
  - `terrain_surface_include_regression`: the tracked A1000 goldens have been stale since 2e22901 (10-18, on main). The revamp adds 0 px on the dry poses. The wet poses differ as S2 expects. **Regenerating the tracked goldens is the user's call.** Meanwhile, gate against renders from main (`--golden-dir`).
  - `shadow_compose_identity`: the proxy, global and ocean diffs come from 065ef57 on main, and the standing grass diff is intended (WO-1). New 4070 goldens: `.godot/shadow_compose_identity_storm_revamp_4070` (44/44 at max diff 0; protected by prune).
- **Open:**
  - The main-world `ring_storm_level` still clamps at the map edge, which can stripe distant main-world LODs.
  - There is an unexplained straight edge on the far D patch.
  - The 2DArray / `"us" is null` errors (about 323 per run) are still unidentified.
  - Optional: move the streak vertex lighting after the culls.
- **S6 progress:**
  - WO1 is done: 18 km hard cap, layer table (`resources/clouds/cloud_layer_table.tres`, `scripts/clouds/`), CPU column, `CloudLayerColumnTest` 50/50. Fingerprint regenerated (only the t2200 hash changed: ExceptionalFront Cb tops).
  - WO2 is done: macro map layers 2/3 and the layers UBO on the build set; readback vs the CPU twin within 4.9e-4; +0.004 ms amortised.
  - WO3a is done (uncommitted):
    - The general band/exit rule replaces `ciGapToBand`, with storm support 1.45 and the in-loop gap skip off (`RING_WS_GAP_SKIP 0`).
    - Perf is recovered with a column-exact light march: map storms leave the sub-mask, the light break ceiling becomes the fetched column top, and the new `ciColumnEmpty` early-out skips empty primaries. GPU.Clouds is 2.87 / 2.93 ms at 15 / 40 km against 3.39 / 3.40 before WO3a (about -0.5 ms).
    - Lean is pinned to the 2-6 km ramp (`RING_STORM_LEAN_FLOOR/TOP_METERS`) and matches the old look.
    - All 6 cloud gates pass. Identity holds outside storms, and the wall shots look right.
    - The anvil cut fix is **not visually confirmed**: the preview hero's canopy ends at about 0.96 of the ellipse. An earlier `plan_AB.png` showed the lean change, not a cut.
    - `storm_anvil_capture` now defaults `--hero-radius` to 30000. The look-pass sizeK change makes cells about 95 km wide.
    - **For WO3b:** `ciColumnEmpty` (~:1504), the light-step skip (~:888) and the `g_ws_sub_band` break band treat everything outside the 2-6 km background band, the storm column and the sub-mask bands as empty. They must use the layer hull, or layer density will drop silently.
    - **To check at acceptance:** sparkles on dark ground or water on the storm side of the wall shots (`scratchpad/lead/wall40_C2.png`, yaw 0 and 270).
  - WO3b is implemented (uncommitted):
    - Low and mid volumetric layers (`RingCloudLayerSample.glslinc`, live UBO binding 29, layer gradient binding 30). The hull feeds the WO3a band machinery. Debug bit 64 turns the layers off.
    - `cloud_layers_gpu_test` passes: fair cloud starts at 2 km, weather cloud reaches down to 750 m, nothing is above 18 km. The gates pass.
    - WO3b fix-up (c2) is done:
      - Perf: weight-preserving stratified light-march subsampling (stride 3, or 4 for stratiform and beyond 15 km) on layer-only primaries. Storm 40 km is +0.157 ms over WO3a (accepted). Fair is -0.04, overcast +0.03.
      - The inter-layer sun shadow is now a true transmittance. The bright streak is much dimmer but still faintly visible (`.godot/captures/storm_anvils/s6fix_c2_40/perf_view.png`); recheck at acceptance.
      - All gates pass.
      - The shader is at a register-pressure cliff, so A/B-measure every addition to the march.
    - The orbit flashes predate S6: frames 8/14/20/26/32 during the first about 33 frames, up to x8-10 mean change, also present with layers off. Separate issue.
    - **Overcast is featureless, and this is an S4 look decision for the user.** The Ns/As decks are S4 sculpt systems, so the per-ray storm ambient paints them one constant colour (layers off is equally flat). Also, the capture ground pose sits 80 m under the 500 m base. Options:
      - (a) structure-aware storm ambient;
      - (b) S6 layers own the non-storm Ns/As decks;
      - (c) accept it and move the pose.
      - **User chose (b) on 2026-10-29:** S6 layers own the non-storm Ns/As decks; storms keep the S4 look exactly. Implemented (uncommitted); see S6 design section 3.8.
        - Only StormTower/Cb sculpt, and the storm look is gated by storm ownership.
        - Weatherness or sheet closure: Ns and thick As are closed with no holes.
        - Deck lighting: unsaturated AO, +-45% patches, column light, own-deck sun shadow.
        - Results: storm deterministic shots are identical within noise. Overcast perf is under the gate. Fair is +0.1-0.28 ms of shader overhead.
        - Natural non-storm systems that used to be storm-painted now look like layer cloud: +0.3 ms around the storm_anvil perf pose.
  - WO4 shadows are done (uncommitted):
    - The shadow compute samples the low and mid layers (layers UBO at 13, gradient at 14) with per-layer ShadowWeight. The terrain range now reaches 7000 m.
    - Amortised near-shadow cost: fair +0.013, overcast +0.035, storm +0.002 ms (gate +0.05).
    - Gates pass. compose_identity shows 1 px of ocean noise.
    - Open: faint concentric arcs at deck edges in the fair near map (step aliasing, so jittering the start is a candidate); no numeric alignment test yet; cirrus shadow hook for WO5 (ShadowWeight 0.12, terrain range up to 18 km).
    - Shadows, far parity and cirrus still use the old band (WO4, WO5, WO6).
  - WO5 cirrus shell is done (uncommitted); see the S6 design WO5 status.
  - WO6 far parity is done (uncommitted); see the S6 design WO6 status. A far layer sky in the far viewport draws the low/mid layers and cirrus beyond the handoff from the near's own macro map and table, with the complementary handoff weight. The far camera physics-interpolation bug is fixed. Parity test layer pass: max delta 8e-6. Cost +0.05 ms in the far viewport. Open: data beyond the macro map is the 245 km atlas.
    - Analytic shell at 9.5 km, composited by ray distance after the march (one loop-carried float), lit with HG + isotropic floor + 22 degree halo, weak closed-form sun shadow on the layers below and on the ground. Debug bit 128 turns it off.
    - Fair-weather overhead of the deck ownership: restoring the coarse AO sample for non-deck primaries measured 0.00 ms, so the +0.10-0.12 ms (fair 1.96-1.97 vs WO3a 1.845) stays; it is inside the +0.25 gate and the coordinator accepted it.
    - The dark horizon line under the As deck is the edge-on low layer cut at the near/far handoff (it vanishes when the handoff moves to 20-26 km): WO6.
- **User decisions 2026-10-29:** `gameplay_cloud_defaults.tres` is reverted to HEAD. Gust debris: finish it (in progress). Terrain regression goldens: regenerate on the RTX 4070 (in progress).
- **Paused 2026-10-29 ~07:45 for the user to play-test.** Nothing is committed; the tree is runnable (C# clean, shaders imported).
  - Overcast decks (S6 3.8):
    - The ownership split is in and verified: only StormTower/Cb are sculpt bodies, and the storm look is gated by `g_ws_storm_owned` (1 inside 1.3 ellipse radii, 0 by 1.45). Headless gates pass with no golden changes.
    - Non-storm decks still look flat. The likely cause is that the opaque deck seen from below is lit only by the per-ray ambient with saturated AO.
    - The look fix (closure, deck AO, column light) is parked in `scratchpad/s6decks/wip_deck/`, not compiled. Next: compile, capture and tune it, then storm identity, perf, gates and sheets (scripts in `scratchpad/s6decks/`).
    - Edge: sculpt strength <= 0.001 or debug bit 2 sets ownership to 0.
  - Gust debris: **done 2026-10-29** (uncommitted). +0.043 ms at full gust on High (gate 0.15), 0 when calm. The flicker check is clean. No debris under a roof or when flying 150 m up. Headless and QP contract pass. Spatial gust is deferred. Look note for the user: dust puffs read as soft grey orbs.
  - Gust debris (pause notes):
    - Code is in and wired (scene node, preset column, loading list, spindrift, all audit fixes). The headless test and the QP contract pass, and the gust captures run.
    - Left: review the `debris_gust3` frames (leaf albedo, high flight, under-roof), flicker/sheet, perf A-B-A on High, and a look check on the puff sun factor.
  - Terrain regression goldens are regenerated on the 4070 (6 PNGs; 0 px diff).
- **Batch-close fix-ups done 2026-10-29** (uncommitted, headless-verified):
  - The POI light cull masks include bit 19 (generator plus `EmitterReactorTest.tscn`).
  - `GrassComputeBenchmark` uses `RingRenderLayers.NoPrecipitationOcclusion`.
  - The loading list has the storm-revamp `.gdshader` files and compositor `.gd` scripts. The `.glsl` RDShaderFiles were left out on purpose: a load failure throws at startup, and they get no warmup. Prove a real loading pass at batch close.
  - S5 post-rain shelter probing runs while wetness > 0.05, with tests updated.
  - Hygiene: no `.import` churn, and `project.godot` is already committed. Only autocrlf warnings remain.
- **S2 wet world follow-up done 2026-10-29** (uncommitted; S2 doc section 10.1):
  - Perf at 720p, Very High: open heavy rain +0.23 ms; forest +0.60 (accepted at Very High+); High +0.16 / +0.29; Medium +0.09.
  - Fixes: map-only tree wet sample; wet foliage SSR roughness floor 0.71 (leaves only); half-size SSR while wet on Low/Medium/High.
  - Glint: `ring_storm_specular_keep` = (1-F)^4, now also on POI and construction surfaces. The core has no glint and the clear-sky glint is unchanged. **The trailing edge (D 0.47) still shows a large soft glow; ask the user whether to raise the exponent.**
  - Gates pass (wet_world, storm_light, shadow_compose 44/44, terrain regression 6/6).
  - Drips (S2 WO6) are deferred.
- **S6 non-storm decks done 2026-10-29** (S6 doc section 3.8). The fair-weather overhead fix and WO5 cirrus are in progress.
- **S3 batch 3 in progress 2026-10-29:** gust gap, G1 leading-arc lightning, arrival time-lapse.
- **User decision 2026-10-29, storm-light D ownership:** "fronts dark by rain". Thunderstorm sculpt systems keep D as now. Natural or manual MajorFront and ExceptionalFront systems get D from their rain intensity: heavy frontal rain is dark, drizzle grey. Dry Cs/As/Ns and other non-storm systems give D = 0, so they no longer dim the sun or sky. Being implemented by storm_light2, which holds its shared-file edits until WO5 hands back. Also: the trailing-edge glint goes dimmer (user decision).
- **S3 batch 3 done 2026-10-29** (uncommitted):
  - Gust gap fixed in `StormArrival.cs Evaluate`: the wind holds 25-31 m/s from the gust front through the core.
  - G1 leading-arc lightning is a second scheduler stream in `RingVolumetricCloudLayer.Lightning.cs`. Pulses and Reduced flashes are unchanged. The 0.5x strike-distance criterion can't be measured with a rim-ringing stream; open.
  - Time-lapse mode is in `storm_arrival_capture.gd`.
  - **Time-lapse defects fixed 2026-10-29** (uncommitted; S3 doc section 16.5):
    - Black blob: a gust leaf at the lens. The near-camera shrink in `ring_gust_debris.gdshader` ran after the quad was built. Fixed.
    - The "sky flip" was two things. (1) A real 1x bug: the precipitation occlusion map camera was physics-interpolated. After a cloud-base jump the first map of the new plane was rendered from about 3% of the way between the old and new plane, so every surface read about 500 m too high for one refresh. The cover probe said "sheltered" and the wet world dried the ground for about 1 s. Fixed with interpolation Off on the map camera and viewport; `precip_occlusion_gpu_test` gained `plane_change_first_map_exact` (fails before the fix at 138 m, passes after). (2) A time-lapse artefact: the wall-clock mist limiter jumped 0.03 to 0.97 in one publish.
    - Mist: retuned to 2e-3 per M (about 1.9 km) and slew-limited to 2/32 per publish.
    - Rainbow: the capture geometry was wrong (storm from the antisolar side). Also a real bug: `SunReach` faded in below the full-storm floor, so a faint bow stayed under the core. Fixed.
    - The time-lapse capture now hides the HUD and uses sub-steps; run it with `--prefer=sun --face=antisolar`.
- **Done 2026-10-29 afternoon** (uncommitted):
  - WO5 cirrus shell (`ring_cirrus_shell.gdshaderinc`; fair +0.07 ms; storm-owned suppression; debug bit 128 = shell off). The only failing gate is `sunshine_camera_independence`, from storm_light2's front flag; that agent fixes it.
  - Time-lapse defects fixed:
    - gust leaf near-camera shrink order;
    - **dry pop: precip occlusion map camera was physics-interpolated** (fix in `RingPrecipitationOcclusion.cs:341-345`, plus a new `plane_change_first_map_exact` GPU test);
    - mist: sigma 2e-3 (about 1.9 km) and slew 2/32 per publish;
    - rainbow `SunReachLow` 0.18.
  - Final time-lapse: `scratchpad/storm_arrival_timelapse_final.mp4`.
  - Open: mist step ticks (per-frame interpolation needed); mist publish cap stall; far-ring camera interpolation check (given to WO6).
- **In progress:** storm_light2 is applying the shared patches (fronts dark by rain). WO6 far parity has started; it reads only until storm_light2 hands back.
- **storm_light2 done 2026-10-29** (uncommitted; S0 doc :115):
  - D = max(owned*max(G,heavy), front, heavy*(1-smoothstep(0,0.25,influence))). Macro map layers 4/5 hold (owned, front); there are 6 array layers. `RenderWeatherFlagFront` = 32.
  - At a fair site, dry Cs/As/Ns now give D 0; a raining Ns front gives 1.0, drizzle 0.16. Storms are unchanged.
  - The glint keep is (1-F)^16.
  - Gates pass with no golden changes.
  - Open: the front rain ramp smoothstep(0.25,0.65) is a constant the user may want to tune; no perf A-B-A was run (one extra fetch per D read, +8 MB macro map).
  - **New look bug:** with camera D no longer storm-tinting the ambient, dry As/Ns decks render deep blue instead of grey. The deck ambient uses sky light. Fix needed in S6 deck lighting.
- **WO6 done 2026-10-29** (uncommitted; S6 doc WO6 Status):
  - New far layer sky pass (`ring_far_cloud_layer_sky.gdshader`, `ring_far_cloud_layers.gdshaderinc`, `RingScaledFarViewport.CloudLayers.cs`) reads the Sunshine macro map. The clipmap drops its cloud body when the sky pass is on. Cirrus continues beyond the handoff; far shadow and bake carry layer and cirrus coverage.
  - **Far camera physics-interpolation bug fixed**: it was 12-19 km off for one frame after a teleport.
  - Parity: overcast and As much closer to the near-only reference; fair about level. Far viewport cost +0.05 ms. Gates pass.
  - Debug: `RING_FAR_LAYERS_OFF=1` restores the pre-WO6 far.
  - Fine camera-centred far layer atlas: done 2026-10-29 (CPU, 128 x 64, about 5 km, 0.5 ms/frame budget, see the S6 design doc WO6 status). Deferred: fair far horizon strip +7..+11 luma vs the near-only reference (the sky gains do not move it; suspected thin or missing cumulus density at grazing rays); Cs at a short handoff stays about 11 masked MAE off the reference; the fine atlas has no storm channel (coarse 245 km storm level beyond the macro map); the main-view ground layer gets no sky pass.
- **In progress:** deck ambient colour (blue decks); WO7 (front sequence, diurnal); finer far layer atlas.
- **User decision 2026-10-29:** the WO7 front sequence (warm Ci→Cs→As→Ns, cold-front band, precursor reach) is **tabled**; the weather system is intricate enough for now. WO7 continues with diurnal cumulus only.
- **User decision 2026-10-29: storm revamp scope is closed.** Finish the 3 running agents (deck colour, WO7 diurnal-only, finer far atlas), do a small WO8 menu pass (AGENTS.md requires tunables), run batch close (full gates, real loading pass, handoff docs, deferred list), then commit and **merge to main**. Everything else is deferred: WO9, WO10 acceptance items, the WO7 front sequence, S4 storm travel/far impostors/look targets/optionals, the S3 shelf cloud, S2 drips, mist polish.
- **Next projects (designing):** megastorm (`docs/megastorm-design.md`) and the atmospheric containment field (`docs/containment-field-design.md`).
- **S6 decisions:** fair base fixed at 2 km; hill fog accepted; climate is a hook for later.

#### Remaining scope (not started)

- **S4:** WO-T storm travel (user chose about 10-20 m/s fronts; regenerate the fingerprint golden on purpose), WO-B superstorm, WO-C climate-machine anomalies, WO-D far storm impostors lit by lightning, WO-F funnels, WO-G haboob, WO-H St. Elmo's fire.
  - WO-D is the priority: storms 450 km away at night are invisible, so sprites appear on black.
  - The user was asked whether to defer F/G/H/C/D and has not answered.
- **S3:** H1 shelf cloud (sculpt owner, after the look pass).
- **S2:** WO6 drips.
- **S5:** batch-close fix to keep shelter probing while post-rain wetness is above 0.05, so eave drips continue after the rain.

#### Batch close checklist (before merging or committing further; ask the user first)

1. Fix the "Unverified at pause" items.
2. Splash re-verify. The primary shrank them on 2026-10-26: life 0.16 s, radius 0.01-0.07 m, fraction 0.3, alpha 0.7. Re-run `rain_streaks_gpu_test` splash checks and recapture.
3. Full GPU gate list once:
   - haze_consistency (20)
   - storm_light
   - shadow_compose_identity
   - terrain_surface_include_regression
   - storm_profile_map
   - sunshine_shader_compile
   - cloud_density_fastpath
   - far_cloud_density_parity
   - rain_streaks (four complaint checks)
   - rain_precipitation_profiles
   - rain_cloud_compositing (without the `--tolerate-cloud-source-drift` flag)
   - wet_world
   - rainbow
   - precip_occlusion
   - distant_rain_curtains (PASS unconfirmed: it crashes at exit)
   - grass, tree and far-viewport gates (render layers changed 1 to 19)
4. Headless suite:
   - S3 math, production and settings
   - natural weather fingerprint and lifecycle
   - the rain sound field tests
   - weather ambience
   - thunder
   - all settings smokes
   - quality preset contract
5. Fix-ups:
   - Lights with restricted `light_cull_mask` (reactor POI scenes, `tools/poi/build_poi_scenes.gd:873`) should include bit 19.
   - `GrassComputeBenchmark` forces `Layers = 1`.
   - Add the new capture and compositor files to `RingWorldLoadingResources`.
6. Revert line-ending-only `.import` churn. Review `project.godot` (the `run/main_scene.qp_perf_harness` line was reordered by an editor save).
7. Run `tools/prune_test_output.ps1 -Apply` (test output was about 16-19 GB at pause).

#### Awaiting the user

- **Audition of synthesised sounds:** `.godot/captures/storm_audio/audition_one_shots.ogg`, `audition_loop_seams.ogg`, `audition_thunder.ogg`.
- **In-game listening pass for the rain sound field.** The 7-step list is in the S5 doc.
- **Sprite video:** scratchpad `storm_sprites_450km_night.mp4`.
- **Decision on deferring the optional S4 features** (WO-C, WO-D, WO-F, WO-G, WO-H).
- **Decision on the gust-front lead:** it currently arrives about 150-175 s before the rain. Leave it or retune.

## Direction (user decisions 2026-10-25)

- **Immersion and spectacle only.** No gameplay effects (movement, traction, damage) in this revamp.
- **Walking now, flying vehicles later.** Build data paths that a vehicle can query later (wind/turbulence by altitude, rain above/inside/below cloud), but no vehicle work.
- **Exaggerated storms.** Superstorms, funnels and upper-atmosphere lightning are in scope; physical plausibility is not a constraint.

## Current gaps this plan addresses

From the 2026-10-25 inventory:
- Rain is only a haze veil near the camera; streak particles were retired. No splashes, puddles, ripples, drips, lens droplets, hail.
- Shelter is not physical: the rain veil uses the audio shelter check; snow and distant curtains ignore roofs, caves and POIs; thunder is not muffled under cover.
- Wetness only reaches Terrain3D ground (not POIs, vegetation, water).
- Storms fade in and out; no gust front, shelf cloud or post-storm phase at the observer.
- Distant storms are flat shell coverage beyond Sunshine's ~72 km; curtains only ~3-8 km. A storm at the camera dims the far arch's sunlit air (haze plan P9/P10 note).
- Climate-machine storm/drought anomalies and the Weather Lung are data only.
- Audio: one thunder recording, no near/far split, beds not directional, no hail/foliage/metal rain.
- Weather audio settings not persisted; lifecycle/spawn/lightning frequency not in the grave-key menu; no reduced-flash option.

## Steps

### S0: Storm light (user request 2026-10-25, top priority)
- No sun shadows under storms: directional shadow contrast fades out with storm optical depth at the receiver.
- Heavy storms darken the ground and the air under them, scaled by storm heaviness and applied per location (also seen from outside the storm). Covers direct sun, ambient/sky, haze in-scatter, vegetation, water and POIs, all through one shared term.
- No clear sky visible above or through storms: overhead overcast closes under storm cells.

### S1: Precipitation foundation
- Precipitation occlusion map: top-down depth around the player, shared by every precipitation consumer (near rain, snow, splashes, distant curtains near the player, wetness, audio shelter, thunder muffling). Replaces `IsRainSheltered` as the source of truth.
- Altitude-aware: no precipitation above the local cloud base, in-cloud precipitation for future flight.
- Near-camera rain streaks restored (intensity tiers) on top of the haze veil; occluded by the map.
- Ground splashes where rain lands.
- **User decision 2026-10-25: the current rain haze veil is reserved for violent storms.** Light and moderate rain are carried by streaks and splashes only; the haze fades in only in the top intensity tier (severe systems / heavy core). Land this with the streaks (S1 WO5) so light rain never goes invisible in between.

### S2: Wet world
- One shared wetness include for terrain, POI architecture and vegetation (POIs get it automatically). Darker diffuse, shinier specular, specular dries first.
- Puddles in cavities/flat ground with intensity-scaled ripples; rain ripples on water surfaces.
- Post-rain drips; lens droplets when looking up in rain.

### S3: Storm arrival sequence
Driven by the observer's position relative to the storm system: shelf cloud and darkening, gust front (wind shift, debris, light drops), fat drops then the core (hail in strong cells, nearing lightning), trailing rain, then ground mist and occasional rainbow.

### S4: Exaggerated storms
- Better cumulonimbus anvils (added 2026-10-25): flat tropopause-capped top sheared downwind, feathered cirrus edge vs dense core, overshooting top, upwind back-shear, mammatus underside, lifecycle-driven growth and detachment.
- Superstorm class above ExceptionalFront (100+ km, slow, rotating wall cloud); natural home for the climate-machine parked storms.
- Visual tornado and waterspout funnels with debris.
- Upper-atmosphere lightning (red sprites, blue jets) above storm tops, readable from across the arch at night.
- St. Elmo's fire on tall POI structures near strikes.
- Far-arch 3D tower/anvil impostors lit by internal lightning; rain curtains out to tens of km.
- Haboob dust walls on the drought side of climate-machine anomalies.

### S5: Audio
- Rain rework (user request 2026-10-25): replace the localisable point emitters (8 around the player + 24 surface voices) with an enveloping directional bed (sector surface/exposure weights panned into a few non-positional layers), near detail only as stochastic one-shots, smooth no-pop transitions while walking.
Thunder library with near crack / far rumble, thunder muffled under cover (from the S1 map), directional wind and distant-storm beds, hail/foliage/metal rain surfaces.

### Cross-cutting
- Persist weather audio settings; put genuine tunables in the grave-key menu (storm frequency, lifecycle length, superstorm odds, lightning frequency); reduced-flash accessibility option.
- Fix far-arch dimming from a storm at the camera.
- Flight-ready API: one CPU query for wind/turbulence by altitude; design notes for flying through cloud interiors.

- **S1 follow-up (real-world look, CPU, storm light, WO7).**
  - Real-world capture harness: `tools/rain_real_world_capture.gd` (`--mode=sheet|perf|cpu|movie|snow`). Output under `.godot/captures/storm_s1/`.
  - Look: streak lit by the storm map per streak (no more `vis` saturation at luma 0.33, which made storm rain invisible), min width 1.6 px, tail fade 1.25, per-drop width and brightness variation. Heavy tier length 1.3, brightness 1.5. Splashes are a ring plus a 12-segment crown wall (no gaps), fraction 0.6 of drops.
  - Budgets raised to Low 4k, Medium 8k, High 12k, Very High 16k, Ultra and Cinematic 20k (coordinator decision). Lattice clamp now 32000.
  - Cost on the 4070 in the real world, heavy rain: 6k 0.041 ms, 12k 0.075 ms, 20k 0.12 ms (GPU.RainStreaks).
  - CPU: the local rain field reads `RingSunshineCloudBridge.GetPrecipitationPotentialAt` (mirror of the macro map precipitation, exact against `QueryLocalWeather`) in one frame. Whole `UpdateRainStreaks` 0.75 -> 0.022 ms per frame while walking and re-sweeping.
  - Hook for S2 drips: push-constant mode 2 in `ring_rain_streaks.glsl` (`drip_vertex()` stub, fragment kind 3), drawn from `counts[4]`, `counts[5]` in `RingRainStreaksComposite.gd`.
  - WO7: `materials/ring_snow_particle.gdshader` (snow flakes hide under shelter) and the curtain shader multiplies by `ring_precip_exposure`.
  - `rain_streaks_gpu_test` exit crash: fixed in the test (free the rig viewport and world before `quit`). Other tests with a live world still crash intermittently at exit (-1073741819 after PASS), an engine teardown issue.
  - Open: ~400 per run "expected 2DArray, actual 2D" uniform errors predate this work (not from the streak pass).

**S2 wet world: implemented (in the e2485b7 snapshot), 2026-10-26, RTX 4070.** Design and implementation notes: [S2](storm-revamp-s2-wet-world-design.md) section 10.
- Built: wet map (`RingWetWorld`, 256 x 256 at 64 m, stateless from S3's rain history snapshot plus closed-form drying), receivers (Terrain3D, voxel, proxy, global, POI, trees, grass, emitter stand-in, scale anchor, construction concrete, water), puddles, ripples, fat-drop spots, lens droplets, the "Wet world" menu group, preset ripple layers. Not built: WO6 drips, WO10 shelter memory, WO11 hail whitening.
- Gates: dry identity is exact (terrain regression 4/4 dry poses; shadow_compose 43/44 plus the known 1 px ocean noise). Wet map vs CPU model within 0.0005 at 64 points. Perf: the first measure was +0.62 ms at 720p. The follow-up on 2026-10-29 (S2 notes 10.1) cut the tree per-vertex sample and wet-foliage SSR, and added half-size wet SSR on Low/Medium/High. Results: heavy rain over open ground +0.23 ms (Very High), forest +0.60 ms (Very High, accepted), lens +0.12 ms while drawing.
- Known looks: the sun disc in puddles during a storm is fixed (follow-up 10.1); the concrete overhang casts a sun shadow that hides the wet/dry line except in the film/soak debug views and the +30 s gloss shots.
- Found in S3: observer history stayed stale after a clock jump with a paused clock (fixed by S3).
- Shared files touched: project.godot (11 `ring_wet_*` globals), `scenes/VoxelRingIntegrationTest.tscn` (nodes `WetWorld`, `LensDroplets`), `RingLiveTuningDrawer.cs` (6 hook lines), `RingGraphicsQualityController.cs` (preset rows), `MinimapWindContractTest.cs` (isolated prefs), `QualityPresetContractHeadlessTest.cs`, `RingStormLightTint.cs` (wetness mirror), `RingSunshineCloudBridge.StormLight.cs` (`TryGetStormMapDescription`).
