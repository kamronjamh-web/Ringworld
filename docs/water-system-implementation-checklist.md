# Water implementation checklist

Updated 2026-09-11. Target: 60 FPS on the current PC, with scalable water detail.

## First playable milestone

The nearby ocean uses GodotOceanWaves spectral displacement for its actual shape, including horizontal choppiness. The existing 2 m mesh samples the filtered large cascade; its normals and crest foam follow that displacement. A bounded asynchronous GPU query supplies the swimming waterline. Lakes, rivers, global/scaled-far water, and disabled/unsupported FFT keep their analytic paths. Generated water columns, terrain edits, and world saves retain their existing contracts.

- [x] Audit upstream and pin `2Retr0/GodotOceanWaves` to `a171446f8174348895aaafc426576c26261058b9`.
- [x] Retain the exact MIT license; record provenance in `THIRD_PARTY_ASSETS.md` and `third_party/godot_ocean_waves/README.md`.
- [x] Import the FFT compute passes with 128/256/512/1024 Stockham variants.
- [x] Create, dispatch, and destroy GPU resources on the main RenderingDevice's render thread. Gameplay performs no wave-texture CPU readback.
- [x] Support validated 128/256/512/1024 resolutions and one to four active cascades; debounce rebuilds and reject stale configuration publication. Balanced remains two 128 x 128 layers at 24 Hz.
- [x] Schedule at most one cascade per frame, retaining fractional time without catch-up bursts. Use actual elapsed simulation time per cascade for foam.
- [x] Clear new foam textures and adapt formats, access-qualified bindings, exact push-constant sizes, and the minimum two-layer published texture for Godot 4.7.
- [x] Reduce canonical double coordinates to phases and a cached local ring frame before GPU upload, preserving floating-origin and seam continuity.
- [x] Follow shared weather wind/clock; provide manual wind controls.
- [x] Bind only nearby ocean materials; skip spectral texture reads beyond the fade distance. Keep global/scaled-far water on its existing opaque path.
- [x] Preserve depth/refraction, shoreline calculations, terrain/cloud shadows, water-kind profiles, and water coverage independent of terrain replacement.
- [x] Clear bindings and dispose the provider on disable/scene exit; retain analytic water on unsupported/headless renderers.
- [x] Cache the last FFT snapshot per weakly held ocean material, so unchanged frames do not resend shader parameters. A newly bound or re-enabled material receives a complete snapshot; disable clears its texture bindings.
- [ ] Human acceptance of wave scale, foam, shoreline transitions, and day/night appearance during normal exploration.

Upstream: https://github.com/2Retr0/GodotOceanWaves/

## Grave/backtick Water tab

Open the live tuning drawer with the grave/backtick key and select **Water**. It contains validated settings, sliders with numeric entries, and color controls.

- [x] Simple/Balanced/Detailed presets, reset, automatic saving, and save status.
- [x] FFT ocean: enable, resolution, layers, target update rate, geometry height/choppiness/distance/filtering, crest lighting, small-ripple strength, foam controls, two patch scales, and detail distance.
- [x] Shoreline FFT transition controls persist terrain wave fade (10 m), earlier chop fade (20 m), and shallow anchor depth (3 m), with ranges 1–64 m / 1–64 m / 0.1–20 m.
- [x] Cache a separate 2 m terrain-contact field from accepted smooth-terrain sector builds. Mixed wet/dry triangle contacts pin displacement; depth and shore distance damp height and horizontal chop, with matching geometric normals and GPU swimming queries. The existing R8 replacement proof remains independent.
- [x] Validate half-float payload/interpolation, periodic coordinates at 1.5 billion metres, clipped atlas copies, GPU dry/shallow/deep queries, pinned-contact silhouettes, and earlier chop damping. Real-terrain fixture found zero contact distance and 16 m shore distance 24 m offshore; inspected capture shows attached water and a still-faceted foam band.
- [ ] Human acceptance of the transition and foam appearance during normal exploration. This pass covers smooth landscape terrain; blocky terrain without a continuous field pins FFT geometry, and placed props do not contribute shoreline obstacles.
- [x] Repeat the 216 m traversal after optimizing shoreline cache validation: 128×128 FFT, two cascades, RTX 4070, 60 FPS cap, 360 frames per window. FFT-on mean 18.585 ms, p99 26.830 ms, maximum 27.491 ms, zero frames over 33.333 ms. These whole-scene timings include terrain/weather and do not certify maximum FFT settings. Report: `artifacts/water_traversal_shoreline_optimized.json`.
- [x] Final moving-swimmer smoke passed: 0.799 m rise, fresh queries on 128/180 observed frames, 109 nonzero samples, maximum surface mismatch 0.0057 m. Missing or changing terrain fields conservatively invalidate queries; continuous availability during streaming remains unclaimed. Queries whose inversion leaves 190 m fall back without clamping rendered waves. Log: `artifacts/water-fft-swimming-shoreline-optimized.log`.
- [x] Validate the expanded runtime envelope: 128/256/512/1024 resolution; 1–4 cascades; 5–120 target updates per second per layer; 0–10 FFT geometry height; 0–3 choppiness; 0.1–60 m/s wind; 4–256 m small and 16–2048 m large patches; 25–4000 m detail distance; and 25–2000 m requested geometry distance. Analytic fallback swell height is 0–10 m.
- [x] Wind: follow weather or manually set speed/direction. Explain why manual controls are disabled when following weather.
- [x] Swells: height, length, speed, detail, domain warp, crest shape, normals, filtering thresholds, and geometry/normal fade distances.
- [x] Appearance: shallow/deep/foam colors, opacity, absorption, refraction, Fresnel, roughness, and specular filtering.
- [x] Shore/crest foam strengths, shore depth, and breaking thresholds.
- [x] Natural variation controls for swell variation, foam patchiness, distant foam visibility, and fine-detail filtering; bind them to near, global, and scaled-far water materials.
- [x] Use 12 m and 79 m FFT patch sizes by default and in presets, preserving an explicit saved 48 m patch size.
- [x] Keep threshold pairs ordered; validate keys/types/finite values/ranges; preserve explicit false values and dirty state after failed saves.
- [x] Synchronize numeric fields after edits/reset and allow typed values. Close the drawer fully while preserving mouse ownership.
- [x] Persist to `user://gameplay_water_defaults.tres`, with factory defaults under `resources/water/`, separately from world saves and unrelated graphics presets.
- [x] Inject isolated test preferences under `user://generated/` before scene startup; never move/delete normal user preferences.

| Preset | Additional simulation | Target updates per layer | Core GPU allocation |
| --- | --- | --- | --- |
| Simple | Disabled; existing analytic water | None | None |
| Balanced (default) | Two 128 x 128 layers | 24 Hz | About 3.2 MiB |
| Detailed | Two 256 x 256 layers | 30 Hz | About 12.7 MiB |

Updates are capped by rendered frames. Memory estimates exclude driver/pipeline overhead and the shared 256 KiB ocean-coverage texture. Ready FFT oceans supply a moving gameplay waterline; the finite water volume and swim motor are shared with fallback water.

The Water tab reports the active provider resolution/cascade count, the one-cascade-per-frame cadence ceiling at 60 FPS, and requested versus effective geometry range. The fine mesh handoff currently clamps effective FFT geometry to about **278 m**, even when a larger distance is requested.

## Aerial pattern pass

- [x] Use two independent, ring-periodic noise fields to bend distant swell and vary wave-group strength. Cell sizes and offsets retain their physical scale in the scaled-far renderer.
- [x] Filter distant wave bands from their projected pixel footprint, including the phase variation's analytic gradient. Use tangent-space derivatives to avoid an artificial filtering strip at the angular seam.
- [x] Move unresolved wave energy into roughness using phase-independent expected energy, avoiding another aliased signal in reflections.
- [x] Replace the crossing-sine foam mask with irregular world-anchored patches for both analytic whitecaps and FFT foam. Shore contact foam remains independent.
- [x] Fade non-shore foam with distance, with a separate visibility control.
- [x] Generate a complete RGBA16F GPU mip chain for each updated FFT cascade: mean gradient, gradient second moment, and foam coverage. Sample with explicit derivatives calculated before UV wrapping.
- [x] Keep the two-layer 128-resolution default, bounded update scheduling, and existing geometric/gameplay water surface.
- [x] Use 12 m / 79 m default FFT patches; retain saved patch sizes. Select **Balanced** in the Water tab to adopt the new preset explicitly.

The controls are **Distant swell variation**, **Foam patchiness**, **Distant foam visibility**, and **Fine detail filtering**. Filtering at zero exposes unfiltered detail for comparison. Higher settings smooth detail below screen resolution. The distant-swell control changes the global/scaled-far shading; existing near swell geometry retains its own controls.

## Verification

### FFT geometry replacement

- [x] Publish the existing spectral displacement output, including filtered levels, with the same lifetime guarantees as normal/foam textures.
- [x] Replace nearby ocean analytic geometry and its analytic normal/crest overlay with one FFT surface; retain lake/river and unavailable-renderer fallback paths.
- [x] Use the existing 2 m nearby ocean mesh, with a distance limit that respects its actual LOD handoff.
- [x] Prevent coarse water underlays from covering FFT troughs, using independently verified water coverage and a no-hole fallback.
- [x] Give adjacent proxy ocean LODs independent water ownership at coarse-quad resolution, including FFT-disabled/initializing states. Proof comes from resident, visible finer ocean geometry; terrain readiness alone cannot retire water. Packed atlas edges clamp within their tile, the finer water handoff is checked at draw time, and stale or removed providers restore the coarse fallback.
- [x] GPU overlap-count fixture reproduces three simultaneous surfaces (0.59961 additive luminance) and reduces them to the one-surface baseline (0.19995). FFT off, unloaded fine water, partial proof, and stale proof outside the live distance handoff also match one surface. Real shoreline capture passed with 253 visible meshes bound to water ownership; broad polygon overlaps were reduced in the inspected capture. Mixed shore cells and the conservative handoff fringe can still retain an underlay.
- [x] Optimized ownership raster matches the reference rectangle proof for full, mixed, missing, ring-seam, and billion-metre coordinate cases, including the fully wet bulk-fill path. Final build: zero warnings/errors. Swimming regression passed with 0.828 m rise and 0.0061 m maximum surface error.
- [x] Avoid repeated per-quad scene queries when rebuilding ownership: snapshot finer visibility once, fill fully wet tile rows directly, reduce mixed fine-mask blocks with integer indexing, and use the reference rectangle proof for nonaligned/ring-edge cases. Final 216 m traversal at 128×128/two cascades: FFT-on mean 20.317 ms, p99 30.415 ms, maximum 33.526 ms, 1/360 frames over 33.333 ms. The FFT-off windows had maxima 30.097/41.989 ms; these are whole-scene samples rather than isolated FFT timings. Report: `artifacts/water_traversal_lod_ownership_fast.json`.
- [x] Keep displacement, normals, foam coordinates, and crest lighting consistent, including horizontal choppiness, ring coordinates, and floating-origin changes.
- [x] Add a bounded asynchronous GPU surface query for swimming and underwater-camera height; retain finite water columns, dry beds/caves, and dynamic-fluid precedence.
- [x] Expose saved FFT height, choppiness, geometry distance/filtering, and crest-lighting controls in the Water tab.
- [x] Validate displacement output, filtered sampling, query agreement, zero-height behavior, disabled/rebuilt resources, and seam/origin consistency.
- [x] Before replacing FFT resolution/cascades, detach material samplers and acknowledge retirement on the render thread. A separate topology epoch allows coalesced spectrum updates without blocking rebuilds. The surface GPU regression draws a production water material before retirement and passes 256/128 rebuilds, stale-query rejection, and disposal (`artifacts/water-fft-uniform-retirement.log`).
- [x] Re-run relevant settings, water-column, ambience, depth/shore, GPU, and gameplay checks; inspect new surface-level and aerial captures.
- [x] Record fresh gameplay timings with geometry enabled.
- [ ] Human acceptance of FFT wave shape, swimming, shoreline transitions, and sustained performance.

The fine water provider maintains a shared 512 x 512 R8 coverage map at 2 m per
texel (256 KiB). A managed byte buffer records resident ocean-cell proofs before
the R8 texture upload. Invalidation turns off coverage use instead of uploading a
black replacement texture. Every coarser ocean layer can sample this same bounded
map, independent of sector size. A 32 m snapped map center and double-precision
canonical offsets preserve placement during movement and rebasing.

On the 2026-09-11 RTX 4070 run, eight equivalent coverage refreshes fell from
**9.37 ms** to **3.95 ms**. This is CPU-side coverage work, not isolated GPU timing.

Swimming reads nine GPU surface samples (144 bytes), with horizontal inversion,
the geometry mip and fade, and a 3 cm inversion-residual limit. There is at most
one outstanding query, with requests capped at 25 Hz near the water. Cached results expire after 0.35 seconds and are rejected
after relevant settings, provider, or coordinate changes. This is an approximate
asynchronous waterline; it is not a mesh collider or a boat-buoyancy system.

The four-cascade CPU comparison uses the existing 3 cm physical query-accuracy
budget. Texel-center samples match; off-center hardware-filtered samples differ
from the ideal CPU float reconstruction by about 0.0097 m of summed chop. This is
an observed comparison, not an exact hardware-filter emulation: Vulkan retains
sub-texel filtering precision through device limits and permits implementation-
dependent filtering behavior. [Vulkan sampling specification](https://docs.vulkan.org/spec/latest/chapters/textures.html)
The strict GPU zero-chop additivity check for layers 1–3 supplements that comparison;
the existing single-layer tolerance is unchanged. Final results are recorded below.

The production swimming fixture passed after honoring the existing spawn-collision
settle window: 0.821 m passive rise, fresh queries on 180/180 measured frames,
140 nonzero offsets, and maximum gameplay-to-cached-query difference of 0.0037 m.
That last metric measures use of the cache, not GPU latency or exact triangle-surface
agreement. The fixture teleports over a 467 m deep ocean; the existing terrain
settle guard times out there before normal swimming starts. It also reports the
known full-scene renderer-resource warnings on exit. The isolated FFT provider and
surface-query fixtures exit cleanly.

### Traversal and high-detail verification — 2026-09-11

RTX 4070, full scene with weather and terrain enabled. The traversal comparison
uses 360 frames over the same 216 m route with 128 x 128 resolution, two cascades,
and a 24 Hz target update rate. It is a whole-frame diagnostic, not isolated GPU
timing and not a guarantee for every water setting.

| Artifact | Mean frame | p99 | Max | Frames over 33 ms |
| --- | ---: | ---: | ---: | ---: |
| `water_traversal_before.json` | 24.882 ms | 199.302 ms | 208.696 ms | 9 / 360 |
| `water_traversal_after.json` | 19.563 ms | 33.251 ms | 33.776 ms | 2 / 360 |

The route cap was 60 FPS. The lower p99/max and fewer over-budget frames are
evidence for this traversal scenario only. Separate GPU checks passed at 512 x 512
with four cascades, 1024 x 1024 with four cascades, and with FFT geometry enabled.
Settings persistence and validation also pass. The final four-cascade query test
passes GPU layer additivity, finite results, lifecycle checks, and the 3 cm
comparison budget: the measured ideal-CPU difference is 0.009674 m and GPU
inversion residual is below the printed 0.000001 m precision. The real-player
swimming regression passes with 0.821 m rise, fresh queries on 180/180 frames,
140 nonzero offsets, and 0.0037 m maximum gameplay-to-cache difference. Both
fixtures exit with code 0; the full scene retains the known renderer shutdown
warnings. Logs: `artifacts/water-fft-{expanded-gpu,surface-expanded,swimming-expanded}.log`
and `artifacts/water-settings-expanded.log`. The final C# build has zero warnings
and errors.

### FFT geometry timing sample — 2026-09-11

RTX A1000 6 GB Laptop GPU, Vulkan Forward+, Medium graphics, 1280 x 720;
Balanced water, two 128 x 128 layers at 24 Hz per layer. Each window contains
180 warmup frames and 240 measured frames.

| Window | Mean frame | p95 | p99 |
| --- | --- | --- | --- |
| FFT off before | 20.244 ms | 23.237 ms | 46.562 ms |
| FFT geometry on | 16.703 ms | 20.261 ms | 24.567 ms |
| FFT off afterward | 22.701 ms | 35.555 ms | 51.214 ms |

The enabled window averaged **59.9 FPS**. The varying off windows show why this
does not isolate FFT cost or prove a speedup. Sustained 60 FPS across movement,
weather, and higher resolutions remains unchecked. The integration fixture
confirmed 24 visible fine water meshes, 155 coarse coverage bindings, fresh
surface queries, disable cleanup, and re-enable. Logs are
`artifacts/water-geometry-*.log`.

Root inspected current ocean off/on, 100 m and 1 km aerial, and Water-tab captures.
The 6 km view is obscured by clouds, so it does not establish water appearance at
that altitude. The separate production-shader fixture verifies height/chop using
opaque silhouette captures, analytic isolation, origin translation, and coverage
enable/disable. User acceptance of the visual result remains open.

### Completed earlier passes

- [x] .NET build: zero errors and zero warnings.
- [x] Godot 4.7.2 Mono import, compute compilation, and production ocean shader compilation on Vulkan.
- [x] Historic settings coverage: roundtrip, false/true, invalid keys/types/NaN/resolution/color, failed-write dirty state, reset, and double-period seam helper checks.
- [x] New anti-repetition settings smoke: variation persistence and validation, saved 48 m large-patch compatibility, reset to 79 m, and production near/global/scaled-far material binding.
- [x] New anti-repetition shader compilation and Vulkan capture checks: finite pixels, visible control effects, equivalent translated origin, periodic patch/far-wave values, and scaled coordinate equivalence.
- [x] Frozen GPU mip readbacks at levels 0, 1, 3, and 7: dimensions, finite values, mean gradient/energy/foam preservation, and valid filtered slope energy; then resume temporal/foam/rebuild checks.
- [x] Full-game aerial captures at 100 m, 1 km, and 6 km; root inspected the water, the new grave-menu controls, and current frame timings. Clouds obscure the water in the 6 km gameplay capture; isolated global-material captures provide the unobstructed shader check.
- [x] Headless controller uses the analytic fallback without GPU calls.
- [x] GPU readback: finite, spatially varying and time-varying gradients; foam range, accumulation, persistence, and decay; both layers; resolution rebuilds; dispose/recreate.
- [x] Existing WaterColumnRegressionSmoke and WaterAmbienceRegressionSmoke.
- [x] Existing water-depth GPU regression: 22 cases, max error 0.007994 m.
- [x] Production depth/shore capture regression: shore foam responds at contact and stays absent in the deep-water/sky cases.
- [x] Sunshine/water-order fixture completed and its capture was inspected.
- [x] Gameplay scene: typed ocean placement, ready two-layer provider, actual near-ocean texture binding, disable cleanup, and re-enable.
- [x] Root inspected ocean off/on and top/bottom drawer captures at 1280x720.
- [x] Record whole-frame off/on/off timings with terrain and Sunshine active.
- [ ] Sustained 60 FPS across traversal, forests, weather, and higher resolutions.
- [ ] Direct GPU timing against the provisional 1-2 ms additional-water budget.
- [ ] Human visual acceptance; automated captures do not complete this gate.
- [ ] Human acceptance that broad swells and foam no longer show obvious repeated patterns at near, global, and scaled-far distances.

The ambience regression exposed a stale UID-only project reference to the existing audio-bus layout. The project now uses that resource's stable path, preserving its UID and restoring shore/underwater buses. The regression now retains failures instead of printing a false PASS during shutdown.

## Historic performance record

This is a pre-variation-controls whole-frame measurement. It does not certify the new anti-repetition pass.

RTX A1000 6 GB Laptop GPU, Vulkan Forward+, 1280x720, Medium game graphics, two 128 x 128 FFT layers at a target of 24 Hz per layer. Fixed canonical ocean camera above the water; mouse-look disabled. Each window has 180 warmup frames and 240 measured frames.

| Window | Mean frame time | p95 | p99 |
| --- | --- | --- | --- |
| FFT off before | 23.668 ms | 37.156 ms | 81.262 ms |
| FFT on | 23.398 ms | 40.750 ms | 48.729 ms |
| FFT off afterward | 18.990 ms | 29.812 ms | 39.345 ms |

That earlier controlled run averaged about 43 FPS with FFT enabled. Startup/cache/weather and other machine load affect these short whole-frame windows; they do not isolate FFT GPU cost. Earlier exploratory captures were discarded after fixing the camera/drawer fixture.

## Aerial pass performance record

Final run on the same RTX A1000, Vulkan Forward+, 1280x720, Medium graphics setup. Balanced water uses two 128 x 128 FFT layers, 12 m / 79 m patches, and 24 Hz target updates per layer. The measurement holds the camera over the same ocean; aerial movement occurs only after all three timing windows.

| Window | Mean frame time | p95 | p99 |
| --- | --- | --- | --- |
| FFT off before | 20.318 ms | 24.418 ms | 36.173 ms |
| FFT on | 17.383 ms | 20.915 ms | 29.864 ms |
| FFT off afterward | 16.770 ms | 17.520 ms | 22.740 ms |

The FFT-on window averaged about **58 FPS**. These short runs do not establish a performance improvement over the historic run or isolate FFT GPU cost. A sustained 60 FPS remains **uncertified**; use Simple for additional rendering headroom. Current logs are `artifacts/water-pattern-{build,settings,columns,ambience,fft-gpu,gpu,depth-visual,gameplay}*.log`.

## Commands and artifacts

Build with `dotnet build --no-restore`. Run the following with the Godot Mono executable from the repository root (`--path .`). GPU checks require Vulkan Forward+ and must not use `--headless`.

| Check | Scene or script argument |
| --- | --- |
| Settings / fallback | `--headless --script tools/water_settings_smoke_test.gd` |
| Water columns | `--headless res://scenes/WaterColumnRegressionSmoke.tscn` |
| Water ambience | `--headless res://scenes/WaterAmbienceRegressionSmoke.tscn` |
| GPU wave provider | `--script tools/water_fft_gpu_smoke_test.gd` |
| Aerial pattern / filtering | `--script tools/water_pattern_gpu_test.gd` |
| FFT displacement and height query | `--script tools/water_fft_surface_gpu_test.gd` |
| FFT geometry silhouettes / coverage | `--script tools/water_fft_geometry_visual_test.gd` |
| Shoreline field CPU contracts | `--headless --script tools/fft_shoreline_field_contract_test.gd` |
| Shoreline displacement / chop factors | `--script tools/water_fft_shoreline_visual_test.gd` |
| Water LOD ownership CPU proof | `--headless --script tools/ocean_replacement_ownership_contract_test.gd` |
| Water LOD overlap count / handoff | `--script tools/water_fft_lod_ownership_visual_test.gd` |
| Real terrain shoreline contact | `--script tools/water_fft_terrain_shore_smoke.gd` |
| Moving FFT swimming | `--resolution 1280x720 --script tools/water_fft_swimming_smoke_test.gd` |
| GPU depth | `--script tools/water_depth_gpu_test.gd` |
| Depth/shore captures | `--script tools/water_depth_visual_test.gd` |
| Sunshine ordering | `--script tools/sunshine_water_order_test.gd` |
| Gameplay and drawer | `--resolution 1280x720 --script tools/water_rendering_smoke_test.gd` |

Disposable logs: `artifacts/water-*.log`. Gameplay captures: `user://generated/captures/smoke/water_fft_{off,on,drawer,drawer_detail}.png`. The research copy is under `artifacts/repo-context/`, excluded from Godot import by `artifacts/.gdignore`.

The pattern fixture writes `.godot/water_pattern_gpu/`; it freezes shader time for off/on and equivalent-origin comparisons. Its final filtering probe measured screen-frequency energy of 0.022096 unfiltered versus 0.000028 filtered. This deliberately stresses short waves and establishes filtering behavior, not subjective visual quality. The full-game fixture also writes `water_aerial_{100m,1000m,6000m}.png` and `water_variation_controls.png` under the generated smoke-capture directory.

Verification limits: the existing Sunshine example reports renderer resource warnings during shutdown; the full scene reports similar warnings. The isolated FFT provider exits without those warnings. The short settings fixture reports four managed ObjectDB instances at exit. Bright distant sky/ring bands appear with FFT both off and on; acceptance of that background remains open.

## Later milestones

- [x] Add a consistent rendered/gameplay surface-height query before large spectral swells and swimming/camera changes.
- [x] Supply nearby water mesh detail and independent replacement coverage before retiring coarse proxy water.
- [x] Match FFT geometry and gameplay without per-frame full-map CPU readback.
- [ ] Extend suitable profiles to large lakes; preserve appropriate rivers, shallow water, and local fluids.
- [ ] Add bounded optional spray after measuring GPU and transparency costs.
