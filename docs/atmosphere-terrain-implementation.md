# Atmosphere and terrain implementation

This implements the priorities from [the September 27 diagnostics](atmosphere-terrain-diagnostics-2026-09-27.md). Existing world recipes, terrain heights, biome weights, edits, collision tile size, and streaming cache limits are preserved.

## Validation checklist

- [x] Combine runtime lattice height and biome sampling.
- [x] Verify exact output against the original separate sampling calls.
- [x] Verify gameplay streaming, wrapping, cache reuse, cancellation, and edits.
- [x] Measure proxy allocations before changing vertex buffers.
- [x] Remove intermediate vertex-buffer copies and compare all output hashes.
- [x] Add validated, persistent Natural and Cinematic atmosphere controls.
- [x] Validate atmosphere ray paths on the GPU against an independent reference.
- [x] Validate near, proxy, and scaled far material bindings and visual captures.
- [x] Measure terrain, grass, tree, and shadow rendering independently.
- [x] Run final gameplay movement and rendering checks.
- [x] Extend the shared haze profile to tree mesh/impostor LODs and all cloud renderers.
- [x] Validate cloud opacity preservation, zero-density/off equivalence, and terminal opaque-depth clipping.

## Cloud and tree integration (September 29)

The procedural tree shader, imported tree meshes, and shared far impostors now register with `RingAtmosphereModel`. Their camera-relative ray frames update even when celestial lighting has not changed. Surviving alpha-cutout fragments receive the same extinction and scattering; imported-tree specular light is attenuated as well. Discarded texels do not acquire haze colour.

Sunshine receives a ten-vector atmosphere snapshot in its append-only callback buffer (880 bytes). Its final output applies shared haze after the temporal ping-pong stores, using a representative cloud distance bounded by opaque depth. This keeps accumulated history free of haze and preserves cloud alpha. Raw-density and history-weight diagnostics bypass haze. Portable finite-cylinder path and scattering functions are shared by compute and spatial shaders; compute includes are direct because Godot's compute importer does not expand nested includes.

The nearby-cloud follow-up found that gameplay was not actually delivering this snapshot: the C# bridge passed a managed `Vector4[]`, while the GDScript property requires `Array[Vector4]`. The property remained empty, and the compositor consequently uploaded a disabled profile. The bridge now passes `Godot.Collections.Array<Vector4>` with ten entries, including ten zero entries when haze is disabled. The original isolated test injected a correctly typed profile directly and therefore missed the gameplay boundary. `tools/cloud_haze_gameplay_test.gd` now exercises the real bridge with a fixed overhead camera, frozen cloud sampling, and isolated preferences, comparing haze off against Cinematic visibility of 30 km and 10 km. This corrects delivery of the existing Atmosphere settings without changing haze strength or cloud opacity.

The failing gameplay capture recorded zero profile entries in `.godot/cloud_haze_gameplay/4564081/report.json`. After the fix, `.godot/cloud_haze_gameplay/4654302/report.json` records all ten entries, the expected 30,000/10,000 m visibility distances, and a camera height of 393.706 m below the 2,000 m cloud floor. Cloud-pixel mean RGB differences from haze-off were 0.00716 at 30 km and 0.05365 at 10 km; mean alpha drift was below 0.0014. The existing isolated compute test also passed. A separate spatial `source_color` probe confirmed that the production Vector3 uploads agree with the compute profile's raw RGB convention; no color-space adjustment was needed. The .NET build passed without warnings or errors.

The far cloud clipmap, whole-ring shell, and merged mesh clouds use the same live profile. Their final cloud and lightning radiance receives extinction once, with coverage-aware scattering in the premultiplied paths. The clipmap no longer uses its separate capped colour wash. All paths use the existing grave-key **Atmosphere** controls and persistence.

Focused GPU checks passed for all three tree shaders, all three spatial cloud shaders, Sunshine's enabled/disabled/zero-density output, and 18 air-path cases at both physical and scaled coordinates. The controlled cloud-depth test passed 12 cases, including fractional final-interval integration; see [the terrain-awareness milestone](cloud-terrain-awareness-plan.md).

Full runtime Terrain3D captures passed at noon, dusk, night, and 12 km above the ground for Natural/Cinematic presets, plus haze-off and zero-density states. Evidence: `.godot/atmosphere_visual/5122373/`. These establish runtime profile/frame correctness, not a new frame-rate comparison. The captures still show cloud grain and some night foliage-edge artifacts; this pass does not claim to resolve those separately reported visual issues.

The existing foliage-edge GPU test passed, and the six-route continuous-motion/history regression passed at its established 0.85 setting with blue-noise motion set to zero. Saved gameplay preferences were not changed.

A separate run using the current 0.95 moving-history preference exceeded that regression's fast-turn tolerance at 60 degrees/second, frame 48. The original HEAD compute shader, compiled separately and injected only into an isolated comparison fixture, failed at the same point. This remains a history-response limitation, not a regression attributed to this haze/depth change. Logs: `.godot/haze-cloud-motion-errors.log` and `.godot/haze-cloud-motion95-baseline-errors.log`.

## Terrain sampling

Exact runtime 2 m lattice vertices now produce presentation height and biome weights from one height-model evaluation. The previous streamed path performed three evaluations. Other coordinates and retained terrain backends preserve their existing behavior. Startup still obtains heights from the field's independent baseline authority; its runtime biome pass now avoids repeating the model evaluation internally.

The runtime surface contract passed 2,668 checks, including runtime v1/v2, different seeds, wrapping, borders, odd coordinates, climate samples, packed biome bytes, and edited/fractional surfaces. The gameplay stream contract passed 121 checks.

An alternating-order, warmed comparison of 4,096 samples measured 36.014 ms for the original separate calls and 16.611 ms for the combined call (about 2.17 times faster in that focused test). This is a sampling measurement, not a game frame-rate claim.

## Proxy buffers

Smooth proxy sectors now populate their growing vertex-channel buffers directly, then create the final publication-owned arrays. The worker no longer creates an initial array and copies it into a list for each channel. Active mesh and cache ownership remain unchanged; published arrays are not pooled or shared for mutation.

The original and optimized builders were compiled and run against the same frozen fixture. All 18 output comparisons matched, covering land, ocean, shoreline, edits, seam skirts, retained terrain, and 32/64/128-cell sectors. Geometry, color, UV, morph, chord, index, and shoreline payload hashes were unchanged.

Warm-cache allocation reductions ranged from 6.1% to 18.75%. The 128-cell land case fell from 9,976,744 to 8,121,000 allocated bytes. Cold-cache reductions were smaller because sampling/cache allocations remain.

Evidence is stored locally under `.godot`:

- `atmosphere-runtime-sampling-test.log`
- `atmosphere-gameplay-stream-test.log`
- `proxy-allocation-frozen-before.log`
- `proxy-allocation-frozen-after.log`
- `proxy-allocation-frozen-comparison.json`
- `atmosphere-settings-smoke.log`

## Reproducing the checks

Build the C# project before running the tools. Use Godot 4.7.2 .NET with Forward+ for the ray, visual, and render-attribution fixtures. The settings, sampling, stream, and allocation contracts can run headlessly.

```powershell
dotnet build RingworldTechDemo.csproj --no-restore
& $Godot --headless --path . --script tools/runtime_surface_definition_test.gd
& $Godot --headless --path . --script tools/terrain3d_gameplay_stream_test.gd
& $Godot --headless --path . --script tools/proxy_build_allocation_test.gd
& $Godot --headless --path . --script tools/atmosphere_settings_smoke_test.gd
& $Godot --headless --path . --script tools/atmosphere_menu_smoke_test.gd
& $Godot --path . --script tools/ring_atmosphere_ray_frame_test.gd
& $Godot --path . --resolution 1920x1080 --script tools/atmosphere_visual_test.gd
& $Godot --path . --resolution 1920x1080 --script tools/terrain_render_attribution_test.gd
& $Godot --path . --resolution 1920x1080 --script tools/terrain_render_attribution_test.gd -- --haze-only
```

`$Godot` is the path to the Godot .NET console executable. Run GPU captures one at a time with the game closed. The allocation fixture measures the current builder; the recorded frozen before/after logs are required for the original comparison.

## Atmosphere scope

The surface haze uses a finite-width cylindrical approximation in camera-relative coordinates. It integrates the first connected air segment along the view ray, excluding a second, remote atmospheric shell across the central vacuum. It is an inexpensive distance-haze approximation, not a full multiple-scattering atmosphere simulation.

Natural and Cinematic profiles share visibility, density, altitude falloff, aerosol, forward scattering, and sun warmth controls in the grave-key Atmosphere tab. Preferences use `user://atmosphere_visual_defaults.tres`; diagnostic fixtures redirect them below `.godot`. The existing runtime haze toggle remains authoritative. Local volumetric fog and an atmosphere lookup-table renderer remain conditional follow-ups after the render budget is established.

The tab now exposes that runtime toggle as **Atmospheric haze** and reports off and zero-density states. The toggle uses the existing world-save `AtmosphericHaze` field; profile sliders remain per-user preferences. The pause menu and grave-key drawer refresh their checkboxes from the same runtime state. Changing a profile does not override an intentionally disabled world haze setting.

### Live-menu startup correction

The gameplay scene readies `LiveTuningDrawer` before `GraphicsQuality`. Previously, the drawer cached a null atmosphere-profile owner and its controls silently did nothing. The controller now initializes its shared owner on first access, and its later `_Ready` does not reload that owner. The drawer also rebinds when an isolated profile replaces the owner. This corrects normal gameplay startup without reordering scene nodes or changing atmosphere shaders.

The headless menu regression preserves drawer-before-controller startup and does not preinstall atmosphere defaults. It checks the initial shared binding read-only, then isolates all profile mutations under `.godot` before exercising actual checkbox, preset, slider, save, and reset signals. It also checks paused input, external haze-state changes, graphics-preset survival, replacement/reload, and unavailable-controller controls. The existing visual fixture preinstalled an isolated profile before startup, which masked the original binding bug.

The menu regression, atmosphere settings checks, and shadow settings checks pass. The C# build completed with zero warnings and errors. Logs are `.godot/atmosphere-menu-smoke.log`, `.godot/atmosphere-menu-settings.log`, and `.godot/atmosphere-menu-shadow.log`.

A fresh 1920 x 1080 Forward+ capture after the menu correction passed all ten atmosphere cases and both menu screenshots in [the visual report](../.godot/atmosphere_visual/4332863/report.json). Terrain3D, proxy, global, and scaled far materials retained valid camera frames. The reviewed menu screenshot shows the new toggle and wrapped status text, and the enabled/disabled views show the distant-terrain haze changing. This is a rendering correctness check, not a performance comparison. The run had no script, C# runtime, or shader compilation errors; the known optional-extension, UID, certificate, deprecation, and shutdown-resource messages remain.

### Renderer validation

The production path include passed 18 GPU ray cases at both 1:1 and 1:1000 coordinate scales. The scalar double reference covers zero-length, outward, tangent, width-axis, signed sidewall/edge, above-air, and Mega-radius rays. Evidence: `.godot/ring-atmosphere-ray-frame/results.json`. This validates the sampled cases, not arbitrary precision at every possible point inside a Mega ring.

Terrain3D, near proxies, main global/analytic terrain, and whole/regional scaled terrain receive dynamic camera frames. Their associated water and hull materials receive the same frame where those terrain renderers own them. Sky retains its separate analytic sky integration; retained smooth terrain and optional local cloud renderers keep their previous path calculation while sharing static profile parameters.

The final visual fixture captured Natural and Cinematic at noon, dusk, night, and 12 km above the ground, plus density-zero and haze-disabled controls. It also checked both live-menu presets and read back the active native Terrain3D, proxy, global, and scaled far material parameters. The camera eye remained above the player, and the native terrain's physical eye height agreed with the canonical camera position within 1 m. The active far renderer in this Certified Ring capture was the whole-ring mesh; regional far-material wiring was reviewed in code but was not exercised by an active regional mesh in these images.

The reviewed capture set is `.godot/atmosphere_visual/4115113/`, with its checks in `report.json`. Earlier experimental capture folders are superseded: fixture corrections were needed to settle the initial water state and reset the camera orientation before teleporting to altitude. The final images show the stronger Cinematic distance haze while retaining near-ground contrast. Those night images showed bright grass and cloud/foliage edge artifacts. The [subsequent night rendering follow-up](night-vegetation-atmosphere-diagnostics.md) corrects the grass's local-day lighting and the reproduced cloud-blur halos; ordinary foliage aliasing and cloud grain remain separate limits.

The C# build completed with zero warnings or errors. Final GPU fixtures completed without shader compilation or script parsing errors. Logs still contain environment/optional-extension warnings and resource-cleanup diagnostics at shutdown, including texture destruction after the rendering server has exited. Those teardown diagnostics remain a separate cleanup item.

## Render attribution

The settled forest fixture uses runtime-v2 seed 1337 at around 16,443,476 m / across 38,692 m, Desktop High, native 1920 x 1080, a 16,384-pixel directional shadow atlas, and a paused daylight view. Grass and trees keep streaming/compute active; their draw cases suppress geometry render layers and verify restoration. Terrain cases hide only the discovered terrain owners. Every case restores the baseline before applying its change.

| Case | Main viewport GPU median | Draw calls | Primitives |
| --- | ---: | ---: | ---: |
| Baseline | 14.164 ms | 2,706 | 22,628,258 |
| Near/proxy terrain hidden | 9.231 ms | 1,340 | 10,692,032 |
| Far terrain hidden | 14.090 ms | 2,705 | 22,497,186 |
| Grass draws hidden | 10.462 ms | 2,406 | 18,983,154 |
| Tree draws hidden | 13.478 ms | 1,717 | 16,008,689 |
| Directional shadows off | 9.276 ms | 1,422 | 16,061,590 |
| Baseline restored | 14.029 ms | 2,706 | 22,628,258 |

These are correlated whole-frame changes, including interactions with shadow passes; their differences are not additive exclusive GPU costs. The far viewport measured about 0.55 ms in this view. Terrain, grass, and shadow submission are the next rendering priorities. These measurements do not justify reducing world quality globally or treating stationary frame times as movement performance. Full evidence is in `.godot/terrain-render-attribution/report.json`.

A separate matched capture after the local-air lighting correction measured main viewport medians of 14.125 ms with haze disabled, 14.168 ms for Natural, 13.953 ms for Cinematic, and 13.951 ms for the restored disabled baseline. Draw counts were identical. The differences are within baseline drift; this capture does not show a clear main-viewport GPU penalty. Far viewport medians were 0.557 / 0.603 / 0.531 / 0.557 ms respectively. GPU intervals are scoped viewport measurements, not a sum of all custom compute/compositor work. Evidence: `.godot/terrain-render-attribution/haze-report.json`.

## Movement regression capture

The full 512 m out / 512 m back route at 12 m/s passed with full-resolution clouds and 1920 x 1080 rendering. All 5,171 support rays hit terrain; coverage waits increased by zero during measured movement (one initial placement wait was excluded). The two new outward windows reused six tiles each and generated three; return windows reused all nine.

The capture recorded 1,289 frames over about 85.94 seconds: median 53.328 ms, p95 105.973 ms, p99 116.072 ms, and maximum 125.686 ms. Main viewport GPU p95 was 103.968 ms. Rendering remains a substantial problem on this route; generation correctness passing does not mean hitch acceptance is complete.

Measured cumulative allocations were 25.05 GB overall and 15.39 GB on proxy workers, compared with 27.84 GB / 17.85 GB in the earlier diagnostic route. GC pause time remained about 2 seconds. This is an observational before/after capture, not a controlled proof that every frame-time change comes from the sampling/buffer changes.

New-window generation measured 326 / 302 ms and readiness 3.69 / 3.58 seconds. Cached returns measured 77 / 59 ms and readiness 2.30 / 1.88 seconds. Native recenter map uploads remained below 0.9 ms; individual staging operations and slow render cadence still affect publication latency. Evidence: `.godot/terrain-generation-tests/m6-4302825/travel-benchmark.json` and its diagnostics directory.
