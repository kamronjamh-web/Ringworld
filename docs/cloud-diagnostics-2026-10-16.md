# Sunshine cloud diagnostics (2026-10-16)

Measurement only, no production code changed. The runs were on the RTX A1000 at 1600×900 with no throttling, using the factory `resources/clouds/gameplay_cloud_defaults.tres` that editor builds load (`GameplayCloudDefaults.cs:90`) and isolated v5 worlds. Captures and the harness (`cloud_diag.gd`) live in the measuring machine's scratchpad `cloud_diag/`.

## 1. Clouds cover the relay-grid beams (inherent to G2, not a regression)

**Cause.** Sunshine composites after the transparent pass, as `mix(scene, cloud, α)`. α is the cloud density integrated along the whole ray up to the opaque depth (`SunshineClouds.gd:820`, `SunshineCloudsPostCompute.comp:392-476`). The beams are `blend_add, depth_draw_never`, so every cloud on the ray dims them, including clouds behind them.

**Fraction of beam luminance kept under clouds:**

| Pose | Baseline | Whole-ribbon depth write | Core-only depth |
|---|---|---|---|
| Ground, pitch 25° | 0.28 | 0.59 | 0.35 |
| 9 km, looking back | 0.51 | 0.74 | 0.60 |

**T2 result.** Sunshine respects a depth-writing transparent. However, the prepass takes the furthest depth over a 2×2 block (`PreCompute.glsl:36-47`), so cores 1–2 px wide never register. The halo stays dimmed.

**Reverse case.** Sunshine reaches only about 171.5 km (`RingSunshineCloudBridge.cs:925`), but beams draw out to 700 km. Beyond Sunshine's reach they draw over the far-viewport clouds undimmed.

**Options:**
1. A T2 core depth pass (`next_pass`), with the depth footprint widened to the visible bright core (2–3 px).
2. An RD pass after Sunshine that attenuates beams only by the cloud in front of them.
3. A G4 far layer, or capping `NearLayerMaxKm` at Sunshine's reach.

## 2. Grainy clouds over terrain (regression, commit `f22431f`, 2026-10-08)

**Cause.** `ring_cloud_history_compatible_weight` (`RingCloudHistory.gdshaderinc:10-28`, used at `SunshineCloudsCompute.glsl:1599/1678`) gates the history weight by `1−smoothstep(0.02, 0.20, |Δα|)` and by cloud-distance agreement. It exempts only sky pixels and clouds that touch the opaque endpoint. Ray-start jitter alone moves α past 2 % wherever terrain, a spire or the far arch is behind the cloud, so history is rejected.

**Measured** (temporal σ):
- 9 km looking down: 0.0712 (factory), 0.0073 (gate off), 0.161 (history off).
- Ground: 0.017 over terrain against 0.005 over sky.
- History weight used: 0.735–0.90 over opaque, with 22 % of pixels below 0.475; 0.947 with the gate off.

**Secondary cause.** The depth-aware post blur rejects taps when the per-frame `depthBreak` flag differs (`PostCompute.comp:109-160`), and that flag flickers over terrain.

**Ruled out:** weather clock, half resolution, near-shadow slicing.

## 3. Clouds show through terrain at day→night (lighting, not depth)

**Evidence.** With frozen weather, the depth-tested density matte is identical by day and by night.

**Cause.** The factory `.tres` sets `NightCloudAmbientColor = (0,0,0)` (code default 0.18, 0.23, 0.35). Cloud ambient fades from 19° to −13° sun elevation. The shader's night floor `max(ambient, ambientLightColor*0.35)` (`Compute.glsl:1354`) multiplies that 0, so night clouds render at about 0.02. Because Sunshine's α is partial (linear density, soft edges, the 120→171 km handoff fade, and the speckle from §2), the `(1−α)` share of terrain, the far arch, the gas giant and the beams reads through the near-black clouds. With the code-default night ambient, the clouds read solid again.

This is configuration, black since `b1e0cdd`. It is amplified by §2 and by the new beams.

## 4. History "not like out of the box" (`f22431f`)

1. **Moving weight.** `MovingHistoryWeight` went from 0.95 to 0.0 in the factory `.tres`. The bridge blends `lerp(0.95, 0, SmoothUnit(max(v/32 m/s, ω/42°/s)))` (`RingSunshineCloudBridge.cs:1427`). The applied weight is 0.485 when turning ~0.8°/frame and 0.061 at 2°/frame.
2. **The gate also applies to sky pixels** whenever motion exceeds 2 px/frame.
3. **Density-continuity hard reset** (`SunshineClouds.gd:1920`, `:2168`). The allowance is 2.8 m per callback, but the driver advects the small noise layer at 1.8 × 3 × wind (`SunshineCloudsDriver.gd:104`, `RingSunshineCloudBridge.cs:864`).
   - Calm weather: 0 resets.
   - Spawned storm: 120 of 120 callbacks had zero history (relative σ 20–23 %, against 2.2 % with the gate off).
   - Any frame over about 160 ms also resets.

Out of the box, Sunshine uses a constant decay and rejects history only on reprojection or depth-break.

## Recommended fix order

1. **Data only:**
   - `MovingHistoryWeight` → 0.85;
   - `CloudHistoryCompatibilityEnabled` → false;
   - `NightCloudAmbientColor` → code default (0.18, 0.23, 0.35).
2. **Replace the continuity hard reset:** exclude wind advection (or scale the allowance by frame time), and decay instead of resetting.
3. **If a gate is still wanted:** a jitter-tolerant gate (0.20–0.65 band or 3×3 neighbourhood clamp), the sky exemption extended to validated opaque rays, and blur taps that reject on opaque-depth metadata.
4. **Beams:** the T2 core depth pass with a widened footprint, then the G4 far layer.

## Implemented (fix-order steps 1–3)

Measured with the same harness (trimmed copy `cloud_fix.gd`, `--cloudy`, RTX A1000 at 1600×900, P0, no thermal throttling). "Before" means the working tree before these changes; "after" means with them. Both are factory defaults, with the compatibility gate on.

**Results:**

| Metric | Before | After |
|---|---|---|
| Temporal σ, 9 km look-down, over opaque | 0.0673 (gate off 0.0076) | 0.0075 (gate off 0.0078) |
| History weight used, 9 km look-down | 0.741, 22 % below half | 0.947, 0 % below half |
| Ground horizon σ, over terrain | 0.0146 | 0.0057 (sky 0.0063) |
| Ground looking up σ, over terrain | 0.0258 | 0.0058 |
| Storm: zero-history callbacks | 120 of 120, in each of 6 windows | 0 of 120, in each of 6 windows |
| Storm relative σ | 0.142 (sky), 0.217 (opaque) | 0.020 (sky), 0.022 (opaque) |
| Six 220 ms stalls, calm and storm | every callback reset | 0 resets |
| Turning 0.8°/frame, applied weight | 0.76 | 0.944 |

**Night.** At sunset +160 s to +1200 s, the night cloud pixels went from matching the scene behind them (lum ratio ≈ 1.0, so the clouds read as see-through) to 1.4–2.3× brighter than that scene. The clouds now read as solid.

**Data (`resources/clouds/gameplay_cloud_defaults.tres`):**
- `MovingHistoryWeight` 0.85.
- `NightCloudAmbientColor` (0.18, 0.23, 0.35).
- `CloudHistoryCompatibilityEnabled` true. The gate stays on because the tolerant gate matches gate-off grain.
- The code default for `MovingHistoryWeight` is now 0.85 (it was 0.13). The graphics presets do not touch history or ambient.

**Continuity (`SunshineClouds.gd`, `SunshineCloudsDriver.gd`):**
- Each advected quantity is judged against a learned velocity × dt. These are the four noise layers (wrap-aware), the storm centre and the weather offset.
- dt comes from a driver-owned advection clock, so steady wind and long frames add no excess.
- Excess at or below 1 keeps history.
- Between 1 and `history_continuity_hard_reset_ratio` (8), the packed history weight fades linearly.
- A hard reset happens only above that ratio, or on texture, seed, effector or structure changes, or shape edits over the ratio.

**Gate (`RingCloudHistory.gdshaderinc`, `SunshineCloudsCompute.glsl`):**
- The alpha band is tunable (default 0.20–0.65; it was 0.02–0.20).
- The jitter exemption now covers sky pairs and opaque pairs whose opaque endpoint is validated.
- It no longer depends on more than 2 px/frame of screen motion. It still requires a continuous field and a camera translation of at most 0.1 × max step.

**Blur:**
- `output_data` alpha is now stable metadata: log2(1 + opaque depth), or −8 for sky. It used to be the flickering `depthBreak` flag.
- The depth-aware bicubic/radial blur rejects a tap whose metadata differs by more than log2(1.5), which still rejects silhouette taps.

**Settings.** The grave-key Clouds → Lighting & history section has four new sliders, persisted through `GameplayCloudDefaults`:
- History gate alpha start (`CloudHistoryGateAlphaLow`);
- History gate alpha end (`CloudHistoryGateAlphaHigh`);
- Continuity allowance (`CloudHistoryContinuityAllowanceScale`);
- Continuity reset ratio (`CloudHistoryContinuityHardResetRatio`).

**Anti-smear check.** These all still pass:
- `cloud_history_motion_gpu_test --regression`: the high/raw error ratios at frame 96 are ≤ 0.61, against limits of 0.85 interior and 0.75 edge, and 0.16–0.22 for translation (limit 0.35).
- The new-occluder rejection in `cloud_terrain_history_gpu_test`.
- `cloud_sky_history_gpu_test` (an abrupt density edit still resets).

## Implemented: stale scene-data UBO broke opaque depth (found via the beam depth core)

**Symptom.** In a fresh session the emitter beam's depth-writing core had no effect on Sunshine. It began to work only after a later `refresh_compute`.

**Mechanism (measured):**
- Sunshine binds `RenderSceneData.get_uniform_buffer()` into the compute (binding 17) and the post pass (binding 8) once, at the accumulation rebuild.
- It reads only `z_far` from it: `if (linear_depth >= z_far) linear_depth *= 100`.
- That RID is a slot in the renderer's per-render-pass pool. After the startup rebuild, the frame's pass order shifted, and the bound slot then held a 4096×4096 shadow pass with z_far = 5.
- Every opaque pixel nearer than about 838 km was therefore multiplied by 100. Terrain, structures and the beam core all landed beyond Sunshine's reach, both in the ray march and in the post depth test. The resized depth did contain the core.
- A refresh rebound whichever slot the main view had at that moment, which is why it sometimes helped.

**Fix:**
- `SunshineClouds.gd` now derives this callback's far plane by unprojecting both depth extremes through the callback's inverse projection, giving 838 860.9 m.
- It packs that value into the reserved `cloud_mask_controls.w`.
- `SunshineCloudsCompute.glsl` and `SunshineCloudsPostCompute.comp` use it, and fall back to the UBO only when it is 0. `Projection.get_z_far()` cannot be used here: it returns the near plane (0.1) for these reversed-Z projections.

**Result.** Fresh session, `--cloudy`, night, `--no-refresh`, core4. The share of beam light kept went from 0.265 to 0.352 at ground 25° and from 0.492 to 0.578 at 9 km looking back. The history numbers are unchanged: 9 km σ 0.0073, ground σ 0.0053/0.0056, and 0 storm and hitch resets.
