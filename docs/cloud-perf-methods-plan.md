# Cloud performance methods: shadow-map lighting and interleaved raymarch (2026-10-17)

This is a read-only design from the lead review. Nothing here is implemented yet. Every setting defaults to off, and with the flag off the output stays bit-identical.

## Measured ceilings

Measured on the RTX 4070 at noon, flag off, 3 repetitions each:

| Change | GPU.Clouds |
|---|---|
| Baseline | about 4.0 ms |
| LightingSteps 30 → 6 | −1.24 ms (−32%) |
| Half resolution | −1.93 ms (−48%) |
| Quarter resolution | −3.03 ms (−67%) |

## How the current code behaves

- **Raymarch steps.** `RaymarchSteps` (328) is a cap. The march exits early on handoff, depth, alpha ≥ 1, leaving the band, or reversing direction. Only grazing rays reach the cap.
- **Integration.** The primary march sums opacity linearly. It has no analytic per-step integration, and Method 3 (analytic integration) is not recommended.
- **Light march.** `sampleLighting` (SunshineCloudsCompute.glsl ~722-810) spaces samples by `quadraticOut` over `mix(4L, L, lod)`, with equal weights and an early break. Its `mix(1, d, heightGradient)` low-sun darkening must be preserved by any replacement.

## Method 1: shadow-map self-shadowing (altitude-binned light map)

### Producer (shadow compute)

- Binding 11, `image2DArray light_bins` in rgba16f, with K/4 layers (K = 8 or 16 altitude bins over the cascade's [F, C]).
- Each bin holds the mean *light* density: `max(bg_noSmall, storm) * side_wall`, with no sharpness pow, no density scale and no small noise. This matches `sampleScene(..., coarseSunLighting=true)`.
- Values accumulate in the existing march (~:574) using each sample's exact altitude.
- ShadowParams gains a vec4 `(K, F, C, enabled)`, and `GPU_CLOUD_SHADOW_PARAM_BYTES` goes from 112 to 128.
- Front and staging arrays are published with the R/G maps.

### Consumer (main compute)

- New set 2, with binding 24 (UBO) and binding 25 (`sampler2DArray`), following the `_update_terrain_sun_set` pattern.
- The lookup uses the baked sun and the bridge's wind offset (`ResolveGpuShadowUvOffset`, bridge ~2073).
- The ground point is found by **exact ray–cylinder intersection**. The receivers' flat projection is about 1.2 km off at 40 km.

### Hybrid march

1. The first N samples are unchanged (`CloudLightMapLocalSamples`, default 4).
2. The remaining samples follow the same loop, breaks and weights, but read density from bins interpolated along the same ground column. The column is fetched once.
3. The full march is used when the light direction differs from the baked sun by more than 0.5°, when the sample is outside the map uv range [0.02, 0.98], when sun_up < 0.08, or when the step count is small.
4. The transition to the full march is dithered.

Ambient and point lights stay unchanged. A specialization constant `RING_LIGHT_MAP` keeps the code dead when the flag is off.

### Memory

At 1024²:

| K | Front | Staging |
|---|---|---|
| 16 | 32 MB | 32 MB |
| 8 | 16 MB | 16 MB |

### Phases

| Phase | Content |
|---|---|
| 1a | Produce bins only. Measure GPU.CloudShadowNear (+5–15% expected). Extend the slice equivalence test to cover bins. |
| 1b | Consumer, near cascade. Add hit-ratio and \|hybrid − full\| debug modes. Run A/B and screenshots: overcast, broken cumulus, storm with CI on and off, low sun, horizon. |
| 1c | Far-cascade bins (needs about 512 px). Only if the horizon hit ratio is low. |
| 1d | Optional `CloudLightMapTravelScale`, for longer self-shadowing almost free. This changes the look. |

Estimate: light cost falls 75–85% wherever the map hits, and CloudRaymarch falls 40–60% in cloudy daylight near views.

## Method 2: spread the primary march across frames

Branching per pixel inside the existing dispatch saves nothing, so the march must be compacted into a smaller dispatch.

| Phase | Content |
|---|---|
| 2.0 | Measure only. Done: half resolution gives −48%. |
| 2.1 | `CloudResolutionJitter`, Half/Quarter only. A 4-phase ±0.25 low-res-pixel jitter on `depthUV`, plus the matching `historyPixel` correction (~:1829). This is temporal anti-aliasing for the existing Half option at the same cost. |
| 2.2 | `CloudInterleave` (0 = off, 1 = 2×2, 2 = 4×4), the native-resolution interleave. Details below. |

### 2.2 interleave

- **March pass.** Dispatch over ⌈W/n⌉×⌈H/n⌉ with spec constant `RING_INTERLEAVE`. Each thread marches `uv = id·n + phase` (Bayer order ~:110) and runs the existing resolve. It writes block images at bindings 26–28 (steady light, transient light, data). Point probes key on `gl_GlobalInvocationID == 0`.
- **Fill pass** (`SunshineCloudsInterleaveFill.glsl`). Move the reprojection and validation code (~:1764–1880) into a shared glslinc. Validate history, then either clamp it by variance against the 3×3 block samples (`CloudInterleaveHistoryClamp`, γ = 2) or fall back to a depth-aware spatial upsample.
- **History compensation.** History weight = `decay^(n²·CloudInterleaveHistoryCompensation)`, default 0.5.
- **Full-march fallbacks.** The march runs in full on accumulation rebuild, history reset, continuity hard reset, capture modes, the menu, and pixel motion above `CloudInterleaveMaxPixelMotion` (24 px).
- **Timing.** A GPU.CloudInterleave marker.
- **Estimate.** 2×2 cuts CloudRaymarch by about 65–70%; 4×4 by 85% or more, for a near-stationary camera only.
- **Risks.** Ghosting at terrain silhouettes, and slower response to lighting changes.

## Settings

All of these go in the grave-key menu, persistence and presets, default off.

| Setting | Values (default) |
|---|---|
| `CloudLightMapEnabled` | on/off (off) |
| `CloudLightMapSlices` | 8 / 16 (16) |
| `CloudLightMapLocalSamples` | 0–12 (4) |
| `CloudLightMapFarCascade` | on/off (off) |
| `CloudLightMapDebug` | on/off (off) |
| `CloudResolutionJitter` | on/off (off) |
| `CloudInterleave` | 0 / 1 / 2 (0) |
| `CloudInterleaveHistoryCompensation` | 0.5 |
| `CloudInterleaveHistoryClamp` | 2 |
| `CloudInterleaveMaxPixelMotion` | 24 |

## Recommended order

1a → 1b → 2.1 → 2.2. Do 1c, 1d and 4×4 only if measurements justify them.
