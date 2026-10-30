---
name: shader-debug
description: Debug Ringworld shaders - .gdshader/.gdshaderinc spatial materials, RenderingDevice compute/raster GLSL (SunshineClouds2, far backdrop, rain haze, vegetation GPU, ocean FFT) and CompositorEffects. Covers compile-error triage, existing debug views, readback/parity probes, and ring-scale pitfalls (reversed-Z, RGBA8 rounding, precision, NaN). Use for wrong pixels, black/NaN output, or shader compile failures.
---

# Shader debugging

## 1. Where things live

- **Spatial/canvas:** `materials/*.gdshader` plus about 60 `materials/*.gdshaderinc`.
  - Important includes: `ring_atmosphere`, `ring_haze_shared`, `ring_shadow_compose`, `ring_cloud_shadow`, `ring_poi_architecture`, `ring_poi_lighting`.
  - Test parity shaders are in `tools/shaders/`.
- **RD GLSL** (imported as `RDShaderFile`):
  - `addons/SunshineClouds2/*.glsl`
  - `scripts/render/ring_far_*.glsl`, `ring_rain_haze_composite.glsl`
  - `materials/ring_terrain_cloud_sun_generate.glsl`, `materials/vegetation_gpu/*.glsl`
  - `third_party/godot_ocean_waves/compute/*.glsl`
- **CompositorEffects:** all are GDScript.
  - `scripts/render/RingContactShadows.gd`, `RingFarBackdropComposite.gd`, `RingFarDepthCapture.gd`, `RingRainHazeComposite.gd`
  - `addons/SunshineClouds2/SunshineClouds.gd` (callback around line 1986)
- **C# RenderingDevice users:** `RingGpuVegetationCompute.cs`, `RingWaterFftProvider.cs`, `RingTerrainHeightShadowGpu.cs`, `RingAtmosphereLuts.cs`, `RingPerformanceGpu.cs`.

## 2. Compile errors

Summarise the log with the `log-triage` skill. The patterns:
- **`.gdshader`:** a source excerpt with `E  NN->` marking the failing line, then `SHADER ERROR: <msg>`, then `ERROR: Shader compilation failed.`
  - An error in a `.gdshaderinc` repeats once for every including shader; fix the first one.
  - **Headless trap:** under the dummy renderer (`servers/rendering/dummy/...`), `#include` lines can show up as `Tokenizer: Unknown character #35: '#'`. Confirm in a windowed run before chasing it.
- **RD GLSL:** `Can't create a shader from an errored bytecode` / `Shader failed to compile` at `_shader_create_from_spirv`.
  - Code checks `RDShaderSPIRV.compile_error_compute/vertex/fragment`; some tools dump the source to `user://*.comp`.
  - Dedicated check: `tools/sunshine_shader_compile_gpu_test.gd` (GPU) compiles the SunshineClouds, rain-haze and terrain-cloud-sun GLSL with includes expanded and compares the SPIR-V against the import.
- After editing a `.glsl`, reimport it, or let the editor or a run do so. The `.glsl.import` changes that show in git status are line-ending phantoms; leave them out of commits.
- **Caches:** the runtime cache is `<APPDATA>\Godot\app_userdata\RingworldTechDemo\{shader_cache,vulkan}`; the editor cache is `.godot/shader_cache`.
  - **Never delete** `.godot/shader_cache` (it is protected).
  - Suspect a stale cache only when the source changed and the output did not. Test that in an isolated profile with `-Cache none`, never by wiping the real caches.
- **Warmup:** `scripts/Loading/RingStartupWarmup.cs` compiles the shaders listed in `RingWorldLoadingResources.StartupPaths` on a background thread.
  - Log lines: `WARMUP shaders done`, `WARMUP shader failed <path>`.
  - A new production shader that causes a first-use hitch belongs in `StartupPaths`.

## 3. Debug views that already exist (use these before adding new ones)

| View | How |
|---|---|
| Shadow components | global uniform `ringworld_shadow_debug_mode`: 0 off, 1 sun visibility, 2 geometry, 3 contact, 4 terrain, 5 cloud. Grave menu: "Shadow debug view". `RenderingServer.GlobalShaderParameterSet` in `RingGraphicsQualityController.Shadows.cs`. |
| Haze | per-material `ring_haze_debug_mode`: 1 identity haze, 2 cap the ray at 12 km. No UI; set it per material, as `tools/terrain_render_attribution_test.gd` does. |
| Grass | `grass_debug_mode` 1-5 (LOD, density, wind, interaction, tiles). Grave menu, Grass tab. |
| Water SSR | `water_ssr_debug_mode` (confidence, hit mask, step ratio). Grave menu, Water tab. |
| Height shadow | top-down overlay. Grave menu: "Height shadow overlay (not saved)". |
| Celestial lighting | `materials/celestial_lighting_diagnostic.gdshader`: shader result vs the CPU expected value, side by side. |

**Adding a new debug mode:**
- Add an `int` uniform branch that writes the intermediate value to `ALBEDO`/`EMISSION` (or `COLOR`), and drive it from debug tooling or a test.
- Per AGENTS.md, add it to the menu only if the subsystem already has a debug view selector there (the existing pattern). Never add an A/B toggle to the menu.

## 4. Proving values numerically

- **Parity fixture** (bit-exact GPU vs C#), see `tools/eclipse_hub_beam_shader_parity_test.gd` + `EclipseHubBeamShaderFixture.cs`, or the emitter grid equivalent:
  1. The C# fixture produces the inputs and a reference.
  2. The inputs are packed into an `RGBAF` texture.
  3. A `ColorRect` in an N×1 `SubViewport` runs the parity shader, which writes `floatBitsToUint(v)` across RGBA8 with `render_mode blend_disabled`.
  4. Read the image back, `decode_float`, and compare at a tolerance of 4e-3. NaN or Inf counts as a failure.
- **Compute and texture readback:** `rd.texture_get_data` / `buffer_get_data`. Use the async variants in production code, and `RenderingServer.CallOnRenderThread` from C#. See `tools/atmosphere_lut_gpu_test.gd` and `haze_consistency_gpu_test.gd`.
- **Visual result:** see the `visual-golden-check` skill.

## 5. Ring-scale pitfalls (check these first for "wrong at distance" or "wrong at altitude")

- **Reversed-Z:** `hint_depth_texture` gives 0 = far/backdrop. `Projection.get_z_far()` returns the near plane for reversed-Z projections, so derive the far plane by unprojecting the depth extremes.
- **Precision:** keep frame math in double and absolute, and narrow to float only at the end.
  - Rebuild on `CanonicalFloatingOriginAnchorChanged`; never accumulate deltas.
  - `ring_atmosphere_coordinate_scale` exists; GPU ray tests run at 1:1 and 1:1000.
  - Certified and Mega radii (5,000 km+) break naive float math.
- **RGBA8:** Godot 4.8 rounds half away from zero (C#: `MathF.Round(c*255f, MidpointRounding.AwayFromZero)`).
  - The far SubViewport is RGBA8 LDR (`use_hdr_2d` false, AgX), so HDR values clip there.
- **NaN:** a black or flickering pixel is often a NaN from a normalize of zero, a `pow` of a negative, or division by a zero distance. Sanitise uniforms on the C# side; the settings owners already do this.
- **POIs** must use `ring_poi_architecture*.gdshader` to receive haze, cloud shadows and lighting. See the `poi-asset-import` skill.
