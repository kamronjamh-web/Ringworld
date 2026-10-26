# Storm and local weather revamp plan

Status: approved 2026-10-25. Builds on `docs/weather-system-checklist.md` (phases 1-6), which stays the record of the existing system.

## Handoff (paused 2026-10-26 ~05:40, RTX 4070 desktop)

**State: nothing is committed.** Everything below sits in the working tree on `main` (base 3110b7e). The C# build is clean (0 errors). The work was paused because the subagents hit the weekly usage limit, which resets 2026-10-28 7pm Central. Several agents stopped mid-task; their partial edits are listed under "Unverified at pause".

Design docs (each has its own status/handoff notes appended):
- [S0 storm light](storm-revamp-s0-storm-light-design.md)
- [S1 precipitation](storm-revamp-s1-design.md)
- [S2 wet world](storm-revamp-s2-wet-world-design.md)
- [S3 storm arrival](storm-revamp-s3-storm-arrival-design.md)
- [S4 exaggerated storms](storm-revamp-s4-design.md)
- [S4 sculpt v2](storm-revamp-s4-sculpt-v2-design.md)
- [S5 rain audio](storm-revamp-s5-rain-audio-design.md)

### Done and accepted (uncommitted)

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

### Implemented but not accepted (needs work)

**S2 wet world**
- Built: wet map (`RingWetWorld`, stateless from the S3 history), all receivers, puddles and ripples, fat-drop spots, lens droplets, and the "Wet world" settings group (old terrain wetness settings migrate).
- Dry identity is exact.
- **Perf gate fails:** +0.62 ms in a heavy storm against a +0.4 ms gate; the breakdown is in the S2 doc.
- **Look bug:** in puddles, the sun disc reflects at full strength during storms.
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

### Unverified at pause (check first next session)

- **`scripts/render/ring_rain_streaks.glsl`:** edited at 05:27 by the S3 batch-3 agent mid-task ("Now write the shaders"). I reimported it at pause and `rain_streaks_gpu_test` then **failed** (exit 1). The compositor became ready, so the shader compiles, but the failing check was not identified before the user stopped the tests (log: `.godot/handoff_rain_streaks_gpu_test.log`).
  - Likely cause: the in-progress perf/hail edit.
  - Diff it against the S1 follow-up behaviour, finish or revert the edit, and re-run.
- **Receiver shaders edited at 05:27 by the storm look agent:** `ring_storm_light.gdshaderinc`, `ring_ocean_global/proxy`, `ring_terrain_global`, `ring_scale_anchor`. `storm_light_gpu_test` passed after these edits. `shadow_compose_identity` and the terrain regression have not been re-run.
- **Sunshine shaders:** imported after their last edit (05:04:50), and `sunshine_shader_compile_gpu_test` passed at pause.
- **New uncommitted files from the debris work:** `ring_gust_debris*` and `RingGustFrontDebris.cs` may be incomplete. Check they are wired, or remove them.
- **APPDATA:** isolation failed for the pause-time test runs, because the profile `appdata_storm_s1` was missing, which left APPDATA empty. As a result, Godot wrote its user data to a repo-local `Godot/app_userdata` folder. That folder has been deleted. The real user profile was not touched.
- **About 400 "Texture needs 2DArray, actual 2D" errors per full-game run** (bindings 1/3/4/7). The source is unidentified, and it is not from the streak pass.

### Remaining scope (not started)

- **S4:** WO-T storm travel (user chose about 10-20 m/s fronts; regenerate the fingerprint golden on purpose), WO-B superstorm, WO-C climate-machine anomalies, WO-D far storm impostors lit by lightning, WO-F funnels, WO-G haboob, WO-H St. Elmo's fire.
  - WO-D is the priority: storms 450 km away at night are invisible, so sprites appear on black.
  - The user was asked whether to defer F/G/H/C/D and has not answered.
- **S3:** H1 shelf cloud (sculpt owner, after the look pass).
- **S2:** WO6 drips.
- **S5:** batch-close fix to keep shelter probing while post-rain wetness is above 0.05, so eave drips continue after the rain.

### Batch close checklist (before any commit; ask the user before committing)

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

### Awaiting the user

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

**S2 wet world: implemented (uncommitted), 2026-10-26, RTX 4070.** Design and implementation notes: [S2](storm-revamp-s2-wet-world-design.md) section 10.
- Built: wet map (`RingWetWorld`, 256 x 256 at 64 m, stateless from S3's rain history snapshot plus closed-form drying), receivers (Terrain3D, voxel, proxy, global, POI, trees, grass, emitter stand-in, scale anchor, construction concrete, water), puddles, ripples, fat-drop spots, lens droplets, the "Wet world" menu group, preset ripple layers. Not built: WO6 drips, WO10 shelter memory, WO11 hail whitening.
- Gates: dry identity is exact (terrain regression 4/4 dry poses; shadow_compose 43/44 plus the known 1 px ocean noise). Wet map vs CPU model within 0.0005 at 64 points. **Perf gate fails**: heavy-storm cost on the 4070 at 720p is +0.62 ms (map + receivers + SSR on glossy ground; 0.34 ms of it is shader work), lens +0.03..0.1 ms. See the S2 notes for the breakdown.
- Known looks: the sun disc is reflected at full strength in puddles during a storm (sky/sun-disc is not storm-dimmed); the concrete overhang casts a sun shadow that hides the wet/dry line except in the film/soak debug views and the +30 s gloss shots.
- Found in S3: observer history stayed stale after a clock jump with a paused clock (fixed by S3).
- Shared files touched: project.godot (11 `ring_wet_*` globals), `scenes/VoxelRingIntegrationTest.tscn` (nodes `WetWorld`, `LensDroplets`), `RingLiveTuningDrawer.cs` (6 hook lines), `RingGraphicsQualityController.cs` (preset rows), `MinimapWindContractTest.cs` (isolated prefs), `QualityPresetContractHeadlessTest.cs`, `RingStormLightTint.cs` (wetness mirror), `RingSunshineCloudBridge.StormLight.cs` (`TryGetStormMapDescription`).
