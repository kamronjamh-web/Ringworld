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
