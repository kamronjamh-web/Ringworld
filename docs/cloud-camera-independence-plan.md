# Camera-independent Sunshine clouds: plan (2026-10-17)

## Problem

Clouds change shape at fixed places while flying. The cause was confirmed with a headless sweep.

Every 0.25 s, `RingSunshineCloudBridge.RefreshWeather()` queries the weather at the camera and writes that single point's weather into sky-wide Sunshine uniforms. `QueryRawWeatherAtTime` quantizes by `FormationCellMeters` (3200). As a result:

- **Coverage steps.** Along 50 km at the Certified capture pose, coverage stepped 15 times, every time exactly on a 3.2 km cell boundary, by up to 0.073.
- **Storm sculpt toggles.** Sculpt switches on or off per camera cell, because `ResolveFormationProfile` rolls `Hash01(cell, seed, 13)`.
- **Ray bounds jump.** `cloud_floor`/`cloud_ceiling` follow the camera system's cloud top.
- **History resets.** The continuity check hard-resets history on these jumps, which makes the snap more visible.

## User decisions (binding)

1. Cloud shape must not depend on the camera position. Upload the nearest storm systems.
2. A storm profile (Cb or tower sculpt, vertical profile) is chosen once per **whole system**, from its SystemId. It never varies per camera cell.
3. **Add the cluster term (P5).** The shader gets a continuous copy of the CPU 3.2 km cluster term, so clouds line up with rain.
4. Upload caps: Low 40, Medium/High 48, VeryHigh and above 64. Low was raised from 32 on 2026-10-17 after measuring 34–40 systems in range at every sweep position. Overflow fades distant systems smoothly. Expose the caps in the grave-key menu.
5. Ambient colour and storm tint: **measure before deciding.** Compare three variants on the GPU:
   - (a) camera-local and smoothed;
   - (b) evaluated per cloud sample, analytically;
   - (c) the user's idea: subdivide the cells that contain storms, store the cloud/storm density per sub-cell in a low-resolution texture once per refresh, and read it with one fetch per sample.

   Report which variant is efficient.
6. The CPU weather field, rain, lightning, audio, saves and `tools/fixtures/natural_weather_v5_fingerprint_golden.json` stay byte-identical.

## Design summary

The design comes from the lead's read-only review.

- **Systems are already world functions.** `TryBuildWeatherSystem` (RingVolumetricCloudLayer.cs ~3778) keys them by macro cell, seed and lifecycle. Only the camera-cell inputs are quantized: `SampleWeatherNoise`, the profile roll (~3484) and the top roll (~3584).
- **The march ends at `cloud_handoff.y`.** Including every system whose edge lies within handoffEnd + light reach (4 × `lighting_step_distance`) + margin makes a system's entry invisible. A fade is needed only on overflow.
- **Uniform classification.** Each sky-wide uniform currently written from `_currentWeather` changes as follows:
  - `cloud_floor`/`cloud_ceiling`: constant envelope, from 500 m to `StormEnvelopeCeilingMeters` (20 km). Each ray uses its own bounds.
  - `storm_*` and `storm_detail.w`: replaced by the per-system array; the legacy fields are set to constant zero.
  - `clouds_coverage`/`clouds_density`/`lighting_density`: constants. The per-sample `lerp(1.2,1.9,visible)` and the light factor are computed in the shader.
  - `cloud_weather_frame.w`: constant base, `_weather.Coverage + SunshineBaseCoverageOffset + bias`.
  - `cloud_handoff.z`: 0. Storm(x) is computed per sample.
  - Driver wind: `_weather.WeatherWindMetersPerSecond`.
  - Shadow radius and height fraction: maximum over the uploaded sculpt systems.
  - Far clipmap near-edge mix: evaluate the systems in the handoff band.
- **New std140 UBO** in `CloudsInc.comp`, bound at 21 (main compute) and 8 (shadow compute). It is updated in place at the 0.25 s refresh, following the `set_segment_lights` precedent.
  - Header: 3 × vec4 — count, enabled, baseCoverage, coverageBias; heavyOvercastStart, sculptStrength, envelopeFloor, envelopeCeiling; bgFloor, bgCeiling, selectionRadius, revision.
  - Each system: 4 × vec4.
    - A: centreAround, centreAcross, major, minor.
    - B: cos, sin, influence, precip.
    - C: stormFloor, stormCeiling, physicalProfile, flags. Flag bits: static/manual, sculpt, manual-falloff, suppressor-reserved.
    - D: manualCoverageTarget, supportMul, reserved, reserved.
  - Up to 64 systems.
- **Shader.**
  - Refactor `localized_storm_density` into a per-system function.
  - Build a per-ray system mask (uvec2) once in `main`.
  - Per-ray band = background band ∪ the storm bands of the masked systems.
  - `sampleScene` loops over the mask bits. Natural systems combine by product + strongest, as `EvaluateMacroWeather` does on the CPU; manual systems combine by max.
  - Light and ambient samples reuse the macro terms of the primary sample.
  - The shadow compute's `sunshine_cloud_possible` must stay conservative.
- **Flag.** `CameraIndependentClouds`. Off reproduces the legacy path bit-identically, so the menu flyover is unchanged. The default flips to on after validation.
- **Continuity.** Add `weather_systems_revision`, bumped only for structural changes.
- **Manual and preview weather** (`_manualWeather`, ids -1003 and -9001) become manual systems in the array.

## Phases

| Phase | Content |
|---|---|
| P0 | `CollectRenderWeatherSystems`; per-system profile and vertical rolls with new salts; leave existing functions untouched. Headless test `tools/sunshine_camera_independence_test.gd`: sweep 50 km, assert constant or continuous uniforms and identical per-system records, log the count distribution. |
| P1 | Shader path behind the flag (main compute + shadow compute). |
| P2 | Bridge packs the systems and writes constants; continuity revision. |
| P3 | Far clipmap and fallback mask use the same per-system data (`materials/ring_cloud_weather_systems.gdshaderinc`). |
| P4 | Settings in the grave-key menu, persistence and presets. |
| P5 | Continuous shader mirror of the CPU cluster term `(weather-0.5)*0.72`: `SampleWeatherNoise` evaluated at the fractional cell coordinate. The CPU field is unchanged. |
| P6 | Ambient variants (a)/(b)/(c) behind a debug setting, GPU A/B. |
| P7 | Docs: emitter-grid spec F15; C4 anomalies must be emitted by `CollectRenderWeatherSystems`. |

P4 settings and defaults:

| Setting | Default |
|---|---|
| `CameraIndependentClouds` | (flag) |
| `WeatherSystemUploadCount` | 16–64 (presets 40/48/64) |
| `WeatherSystemSelectionMarginMeters` | 8000 |
| `OverflowFadeMeters` | 15000 |
| `SunshineBaseCoverageOffset` | 0 |
| `StormEnvelopeCeilingMeters` | 20000 |

Performance gate: at most +5 % Sunshine GPU time, measured with `sunshine_gpu_benchmark.gd` and the noon gameplay A/B. If the cost is over budget, fall back to an 8×8 shared-memory tile mask or a macro-map texture. The texture is the same idea as ambient variant (c).

## Implementation notes: P1, P2 and the P5 shader half (2026-10-17)

- **UBO layout as built.** `addons/SunshineClouds2/RingWeatherSystems.glslinc` (shared) plus the `RingWeatherSystem` struct in `CloudsInc.comp`. The header is 4 x vec4, not 3: header3 = (cluster cell metres, cluster strength, small + 256 x large cluster cells, terrain seed as raw int bits). Systems start at byte 64; total 4160 bytes. Binding 21 (main compute, set 0) and 8 (shadow compute). Record `d.z` is the overflow fade weight (1 normally); it scales the system's macro influence and its sculpt silhouette.
- **Flag off** keeps the legacy path: the bridge uploads a zeroed block (`enabled` = 0) and every new branch is behind `ring_ws_enabled()`. Legacy uniforms are untouched.
- **Flag on, shader.** One mask (uvec2) per ray (main) or per pixel (shadow); per-ray band = background band union the storm bands of masked *sculpt* systems; one macro-weather evaluation per primary sample (coverage, storm, density and light multipliers, P5 cluster term), reused by light and ambient samples. A sculpt system without a storm band (a CumulusCluster that rolled Cumulonimbus) sculpts inside the background band.
- **Flag on, bridge.** Systems are sorted by edge distance (manual first), capped at `WeatherSystemUploadCount`, with a smoothstep fade over `OverflowFadeMeters` against the (N+1)-th system. The ray envelope is constant; legacy `storm_*`, `cloud_handoff.z` and `lighting_density` are constants; ambient colour uses a camera-local, continuous, 1 s smoothed mirror of the shader formula (variant (a)).
- **Still camera-dependent with the flag on (P3):** the far clipmap and fallback mask read `cloud_floor`/`cloud_ceiling`, `clouds_coverage` and `clouds_density` from the resource (now envelope and constants), and `RingSunshineCloudBridge.TerrainHistory.cs` reads the envelope.

## Implementation notes: macro weather map (2026-10-17)

Measured problem: with the flag on, every raymarch sample looped over the ray's masked systems (`ring_ws_update_macro`), +1.1-2.0 ms of Sunshine GPU time on the RTX 4070 (budget +5 %). The macro terms are pure functions of (position, system list, snapshot time), so they now live in a world-anchored texture.

- **One formula.** `ring_ws_macro_point(canonical, mask, shift, circumference)` in `RingWeatherSystems.glslinc` returns `(cov, storm, precip, systemInfluence)`; the bias-dependent part (`ring_ws_apply_macro`: visible coverage, heavy overcast, density and light multipliers) is a separate cheap function. The per-sample direct path, the point probes, the out-of-map fallback and the build shader all call the same function. `RING_WS_MACRO_BUILD` compiles only the position-pure section so the build shader needs no `genericData`.
- **Build shader.** `RingMacroWeatherMap.glsl` writes RGBA16F texels: R = coverage before the user bias (includes base, continuous P5 cluster term, macro/density bias, manual systems), G = storm level (0.65 influence + 0.55 precipitation), B = precipitation, A = system influence (the P6 ambient/tint factor). RGBA16F error is <= 4.9e-4 absolute on [0,1] values, far below the 0.01-level thresholds they feed.
- **Region and resolution.** World-anchored: the origin is snapped to whole texels when a build starts. Covers `_ciSelectionRadiusMeters` (handoff end + light reach + 8 km margin, about 207 km) either side of the camera around the ring and the whole ring width (-1 km .. width + 1 km). Default texel 1 km (`MacroMapResolution`) gives about 417 x 322 texels (0.13 M, 1.1 MB per layer). The texture is allocated once at 1024 x 512 x 2 layers (8 MB); a request that does not fit is coarsened (finest possible texel about 0.64 km). The ring around seam is handled by wrapping `canonical.x - origin` into [0, circumference) in the sampler and by the wrap-aware deltas in the formulas; the origin is stored wrapped.
- **Bilinear error.** Features: cluster lattice spacing 16 km (small) / 41.6 km (large), system edge smoothstep over 0.68 R. Per axis the error is at most h^2/8 * max|f''|. For the cluster term (strength 0.72, weight 0.62, smoothstep'' <= 6 per cell^2) that is <= 0.003; for a system with radius R the coverage error is <= 0.746 (h/R)^2 (0.0117 at R = 8 km, 0.002 at R = 20 km). `SunshineCameraIndependenceTest` measures the maxima.
- **Slicing and double buffering.** Texture array with two layers. `update_macro_weather_map` runs once per render callback before the shadow and cloud dispatches: it starts a build when `MacroMapRefreshSeconds` (default 0.25) has elapsed since the previous start (or the structural revision changed), copies the system block bytes and the wind frame offset into the build snapshot (one `buffer_update`), and then dispatches one slice (a band of 8-row groups, `MacroMapSlices` default 8) per callback into the BACK layer. After the last slice the front/back layers swap atomically by rewriting the 32-byte params UBO; the cloud and shadow dispatches later in the same callback already read the new front. Slow frames simply run builds back to back. No uniform set is ever rebuilt (fixed-capacity texture, in-place UBOs, same pattern as `weather_systems_buffer`).
- **No crossfade (decision).** A swap changes each texel by at most (wind speed x build latency) x |grad f|: about 10 m at 30 m/s against feature scales of 8-40 km, i.e. under 1e-3 in coverage. A crossfade would double the per-sample fetches, the very cost being removed, for an invisible step.
- **Outside the map** (not expected inside the march) the sampler evaluates `ring_ws_macro_point` with an empty system mask, i.e. base coverage plus the continuous cluster term. When the map is off or not yet built, the shaders use the direct per-system path (full ray mask).
- **Raymarch.** One `textureLod` per primary sample; light and ambient samples reuse the primary sample's terms exactly as before. The per-ray list shrinks to the sculpt-flagged systems (`ring_ws_mask(..., sculptOnly)`), so the sculpt loop (`ring_ws_sculpt_density`) touches usually 0-1 systems.
- **Shadow compute.** Density uses the same map. `sunshine_cloud_possible` keeps its analytic conservative argument and needs no max texture: away from every system the map value is the function with no system term, bounded by `base + 0.5 |strength| - 0.28 * 0.46`; a bilinear value only mixes the 4 texels within sqrt(2) texels of the sample, and the build snapshot is at most one build period old, so the "does a system reach this probe" test is widened by `1.5 * texel + 250 m` (the ray mask margin grows by the same amount).
- **Diagnostics.** `GPU.CloudMacroMap` timestamp scope (one begin/end pair per slice), `MacroMapBuildsCompleted`, `MacroMapSlicesDispatched`, `MacroMapActive` on the bridge. Exports `MacroWeatherMap`, `MacroMapResolution`, `MacroMapSlices`, `MacroMapRefreshSeconds` (P4 adds menu and persistence).

## Implementation notes: tight per-ray band, exact empty-step skip (2026-10-17)

Measured: the tall 500 m..20 km envelope (debug bit 32) costs +2.0 ms; the old per-ray band (background band union the bands of every masked sculpt system) was still 500..~16 km for most rays because Cb systems 150-200 km away were masked.

- **Per-ray sculpt intervals** (`ring_ws_build_ray_sculpt`, SunshineCloudsCompute.glsl). An 8-segment cloud-space polyline of the ray is tested against each masked sculpt system's ELLIPSE (orientation included; the sculpt density is exactly zero outside it). The kept systems form `g_ws_sculpt_mask` (ellipse within light reach of the ray); a system with its own storm band records the ray-distance interval where the ray is within 1.5 km of its ellipse (<= 3 slots, extra systems merge into the last slot). The band at ray distance t is the background band widened only by the intervals containing t. The exit branch is deferred while an interval has yet to start; `cloudfloor/cloudceiling` hold the hull of all intervals (camera-inside and exit-side tests).
- **Exact empty steps.** A primary sample outside the background band and outside every active interval band is exactly empty (edge fade and storm altitude masks are 0): no mask fetch, no map fetch, no sampleScene, `newStep = maxstep`. Same test in the light march (`ring_ws_density_possible`) with the sub-mask bands.
- **Per-primary sub-mask** (`ring_ws_update_sub_mask`): sculpt systems whose ellipse is within the light reach (4.2 x max(lighting step, maxstep) + 1 km) of the primary sample; the pillar/anvil sum, the ambient sample and the light march's break band use only these.
- **Entry skip:** the minimum over the background band and, per interval, max(entry skip of the interval band, steps before the interval starts).
- **Shadow compute:** the sculpt mask is filtered by ellipse-vs-polyline (`ring_ws_sculpt_ellipse_subset`), so only systems whose ellipse reaches the sun ray widen the march band. The march itself is still one contiguous segment.
- **Debug bits:** 1 = background band only (no intervals, no sub-mask widening), 16 = light march breaks on the background band; the others are unchanged.
