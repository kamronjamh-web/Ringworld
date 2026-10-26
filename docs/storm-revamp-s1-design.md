# Storm revamp S1 design: precipitation foundation (lead, 2026-10-25)

Work orders for `docs/storm-revamp-plan.md` step S1. Line refs are main at 3110b7e. Estimates are unmeasured.

## 0. Why the old rain streaks were retired
Only record: user request "Replace rain drops with soft gray, depth-aware haze" (`docs/shared-wind-implementation-checklist.md:65,73`, commit f22431f, 2026-10-08). The removed code (`git show f22431f -- scripts/RingLocalWeatherEffects.cs`) had:
1. Drops jumped while walking: `LocalCoords = true` (:582) plus re-snapping the emitter every 8 m (:733-757) dragged live drops with the box.
2. No shelter: drops fell through roofs, caves, POIs.
3. Glowing, shimmering lines: unshaded alpha glowed at night; 0.022-0.046 m sub-pixel widths shimmered under FXAA and ghosted under FSR2.
4. Sparse and half wasted: 4096 drops in 110x45x110 m (~0.008/m3); drops fell 75 m inside a 45 m box, so about half were below ground.
The design fixes all four.

**User's own complaints (2026-10-25), which are hard acceptance criteria for WO5/WO6:**
- **Always lit.** Streaks must take scene light: sun/sky ambient, dimmed under cloud and storm shadow (the cloud shadow at the drop, storm ambient), dark at night. They brighten only during the lightning pulse. They never glow in shade or at night.
- **Clouds overdrew the rain.** Near rain must always draw in front of clouds. Find where the Sunshine composite runs relative to the transparent pass. Either draw streaks after the cloud composite (for example in or next to the rain haze compositor, which runs last), or make the cloud composite respect a rain-in-front rule. GPU acceptance: no cloud pixels over streaks within 40 m.
- **Rain came from clear sky.** Rain must exist only under a raining cloud. Gate per drop column by the local precipitation field (not one scalar at the player), fade at storm edges, and clip at cloud base. With no cloud overhead there is no rain. GPU acceptance: standing at a storm edge, streaks appear only on the storm side.
- **It ignored the wind.** Slant and drift must come from the shared surface wind, including travelling gusts (the shared CPU/GPU gust field, `RingWindField`), per drop. Storm wind should give strongly slanted, gusting rain. Acceptance: slant angle tracks `ResolvedWindMetersPerSecond` plus gusts; gust front visible as a wave of slant.

## 1. Precipitation occlusion map
Rejected: `GPUParticlesCollisionHeightField3D` (particles only; believed to ignore rotation, and ring up is not world Y, `RingLodGeometrySnapshot.cs:25-34`); raw RenderingDevice raster (no scene meshes); S4 terrain height-shadow clipmap (procedural terrain only).

Chosen: orthographic SubViewport sharing the main world, after the `RingFarDepthCapture.gd` pattern (installed at `RingScaledFarViewport.cs:1088`).
- Viewport 512^2, `own_world_3d = false`, update Once, MSAA/AA/FSR off, `MeshLodThreshold = 0`, `debug_draw = Unshaded`, positional shadow atlas 0.
- Camera3D with its own environment (clear colour, no fog/sky/GI) and own compositor holding only `scripts/render/RingPrecipOcclusionCapture.gd` + `.glsl` (POST_OPAQUE, resolved depth to R32F `Texture2DRD`).
- Frame: looks along -up from Hc = clamp(cloudBase - observer, 40, 1000) m above the observer; ortho size 128 m; far plane Hc + 64 m; camera up = around axis. Stores occluder height relative to the map plane in metres (R32F). Empty = exposed.
- Curvature: sag at map edge ~0.03 m at the 80 km Normal radius. Drops use the same single-basis frame.
- Snapping: centre snapped in canonical around/across to 2 m (8 texels). Content is map-relative, so a floating-origin rebase only republishes frame vectors.
- Cadence: re-render on recentre (>16 m), on terrain/construction edit revisions (the counters the ambience uses), and every 1 s.
- Geometry: new render-layer bit `NoPrecipitationOcclusion` excluded by the map camera; set on grass (cards and standing), trees and impostors, player body, and our streak/splash/snow/haze meshes. Terrain (voxel, smooth, Terrain3D), POIs and construction block. Transparents never write depth. Water is captured as a surface. Trees do not shelter in v1 (drips under trees are S2).
- Shader access: globals in `project.godot [shader_globals]`: `ring_precip_occlusion` (sampler2D, neutral exposed texture until ready) plus vec4 `_origin`, `_around`, `_across`, `_state`; include `materials/ring_precip_occlusion.gdshaderinc` with `ring_precip_exposure(world)`. Computes get the RID and frame through their UBO, like `RingRainHazeComposite`.
- CPU: 10 Hz probe compute on the render thread (pattern `RingTerrainHeightShadow.Gpu.cs:94-209`) returning cover height above the listener and the open fraction within 2 m via `BufferGetDataAsync`. `RingWeatherAmbience.ListenerHasCover` (:353) uses it when fresh and inside the map, else the existing CPU column resolver (headless and startup always fall back). Also exposes `ListenerCoverDepthMeters` for S5 thunder muffling.
- Spike first (WO2a): (a) is the sun shadow map re-rendered for the map camera (directional shadows render per viewport, ~+2-3 ms per update; check `viewport_get_render_info` SHADOW draw calls; if so, put lights on a layer the map camera excludes)? (b) does `Camera3D.compositor` replace the world compositor so Sunshine does not run? (c) do terrain vertex shaders morph by `CAMERA_POSITION_WORLD`?

## 2. Rain streaks
Stateless instanced shader, not GPUParticles: `scripts/Weather/RingRainStreaks.cs`, two MultiMeshInstance3D layers (near radius 12 m, mid 40 m), `materials/ring_rain_streak.gdshader`.
- Drop position hashed from instance ID on a world-anchored lattice, phase computed on CPU in double: world-stable (no snap pops), deterministic under paused/rewound clocks.
- Wind slant from `ResolvedWindMetersPerSecond`; cut off above cloud base; collapsed in the vertex shader where sheltered.
- Lit by daylight, flash with `CurrentRenderedLightningPulse`; fade within 0.7 m of the camera; minimum width ~1.5 px.
- Tiers: un-retire `RainPrecipitationProfile` (scattered/steady/heavy) as the driver; `visible_instance_count` scales with tier; heavy adds the mid layer and longer, brighter streaks.
- Budget in `PresetWeather` (`RingGraphicsQualityController.cs:708`): Low 2k, Medium 4k, High 6k, Very High 8k, Ultra 12k.
- Snow stays in the pool (`RingLocalWeatherEffects.cs:411`); its draw material (:552-566) becomes a ShaderMaterial hiding sheltered flakes.
- Transparent pass, so the post-transparent haze still fogs distant streaks.

## 3. Splashes
Second MultiMesh in `RingRainStreaks` with `materials/ring_rain_splash.gdshader`. Each splash is tied to a drop ID and plays at that drop's analytic landing time and map height (roofs splash too), normal from the map gradient. Crown/ring quads, 0.12-0.25 s, within 20 m, ~25% of streak count. Water ripples are S2.

## 4. Altitude and flight hook
Extract the cloud-base logic inline in `RingDistantRainCurtains.cs:201-210` into pure `scripts/Weather/PrecipitationAltitude.cs` returning Below / InCloud / Above.
- Below: full precipitation, map active, streaks clipped at base.
- InCloud (e.g. mountaintop in a front; front base 500 m, `RingSunshineCloudBridge.cs:141`): streak density x0.5, haze x2, map off.
- Above: no local precipitation.
The local intensity target (`RingLocalWeatherEffects.cs:399-406`) multiplies in the phase factor; the snow-guaranteed-above rule (:765) stops above cloud top. Flight hook: static `QueryPrecipitationColumn(RingCanonicalPosition)` -> intensity, phase, snow, in-cloud depth.

## 5. Menu
Extend rain haze settings (`user://gameplay_rain_haze_defaults.tres`; `RingRainHazeSettings.cs`, `GameplayRainHazeSettings/Defaults.cs`, `RingLiveTuningDrawer.RainHaze.cs`; old files load unchanged; live-tuning-setting skill). Rename group to "Rain"; add Streak density (0-2), Streak brightness (0-1), Streak length (0.5-2), Splash amount (0-2). Map resolution/on-off and per-consumer toggles are debug tooling (with a map overlay view); streak count belongs to the graphics preset.

## 6. Tests
Headless: pure C# tests for the map frame (snap stability, rebase invariance, edge curvature bound) and the altitude phase table; extend `rain_precipitation_profiles_test.gd` (tier -> count monotonic, snow unchanged), `QualityPresetContractHeadlessTest.cs:34,117` (streak budget), `WindSettingsSmoke.cs` (new keys round trip/sanitise); `rain_surface_behavior_test` unchanged and asserts `cover_source == cpu_fallback`.
GPU: new `precip_occlusion_gpu_test.gd` (roof/POI/cave texels at expected heights; open texels match CPU terrain within 0.3 m; tree/grass/player absent; zero shadow draws in the map viewport; no re-render on rebase). Extend `rain_precipitation_profiles_gpu_test.gd` (visible rain grows with tier; zero streak pixels under roof; splashes on open ground; paused clock -> identical frames) and `rain_cloud_compositing_gpu_test.gd` (haze outside a roof/cave mouth, not under; near opaque pixels unchanged). Perf A-B-A with timestamps `GPU.PrecipOcclusion`, `GPU.RainStreaks`. ffmpeg `flicker` on shelter walk-in/out and tier ramps; `compare` vs clear-weather baseline for no change when dry.
User check: heavy storm walk from open field under a POI overhang into a cave mouth; looking up; night with lightning; Low preset (FSR2) ghosting; debug-fly up through the cloud base.

## 7. Work orders
1. WO1 altitude helper (`PrecipitationAltitude.cs`, column query, curtains + local effects use it). Accept: headless phase tests; curtain smoke unchanged.
2. WO2a spike (section 1 checks, A1000 numbers). Accept: report; stop if the shadow re-render can't be avoided.
3. WO2b map producer (`scripts/Weather/RingPrecipitationOcclusion.cs`, pure `PrecipitationOcclusionFrame.cs`, capture .gd/.glsl, globals, include, layer constant + tagging, debug overlay). Accept: frame tests + `precip_occlusion_gpu_test`.
4. WO3 CPU probe + audio (`cover_source` diagnostic). Accept: rain_surface_behavior on both paths; GPU probe matches the map.
5. WO4 haze occlusion: grow payload from 240 bytes (`RainFog.cs:205-245`, `RingRainHazeComposite.gd` size checks); integrate only exposed ray parts (6 map samples over the first 64 m); drop the `IsRainSheltered` gate (`RainFog.cs:92`) once the map is ready; fallback shader uses the include. Accept: compositing GPU test.
6. WO5 streaks. Accept: profiles GPU test + determinism.
7. WO6 splashes. Accept: splash pixels on open ground only.
8. WO7 snow + curtain occlusion (curtain shader samples the map within range). Accept: curtain tests unchanged outdoors.
9. WO8 menu + persistence + settings smoke.
10. WO9 perf, visual review, docs; `tools/prune_test_output.ps1 -Apply`.

## 8. Perf (A1000 estimates) and risks
Map render per update GPU 0.15-0.35 ms, CPU cull 0.3-0.8 ms (~1-2 Hz, <0.05 ms/frame avg); probe <0.01 ms; haze map samples +0.1-0.2 ms; heavy streaks (8k) 0.2-0.4 ms; splashes <0.1 ms; heavy rain total ~0.5-0.9 ms.
Risks: sun shadow re-render for the map camera (gates WO2b); Sunshine running for the map camera; POI visibility ranges (end 2000 m) culling at the 1000 m cap; terrain LOD morph relative to the map camera; re-render frame spike; FSR2/TAA ghosting of thin streaks (no motion vectors for transparents, no post-upscale hook); unknown opaque meshes falsely sheltering (overlay + GPU test).
