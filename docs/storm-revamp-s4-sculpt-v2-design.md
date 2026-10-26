# Storm revamp S4 sculpt v2: baked storm profile map (lead, 2026-10-25, RTX 4070)

Replaces the march-side shape of S4 WO-A (`docs/storm-revamp-s4-design.md`) and implements S0 WO-5 (storm deck plus background lift, `docs/storm-revamp-s0-storm-light-design.md`). Line references are to the working tree of 2026-10-25, which holds WO-A uncommitted.

## Verdict on WO-A (uncommitted)

**Modify.** Keep the CPU side, rebuild the shape as a baked map, and return the march to HEAD plus one map lookup.

- **Keep:**
  - lifecycle code L in `d.w` (`RingVolumetricCloudLayer` `StageProgress`, `StormLifecycleOverride` / `RING_STORM_L`, `StormLifecycleCodeTest`);
  - the downwind angle in `header1.w`;
  - the "Anvil shear" and "Mammatus" settings (menu, persistence, smoke test);
  - the capture harness (`tools/storm_anvil_capture.*`, `StormAnvilCapture.cs`);
  - the per-cell hashing and lifecycle logic of `RingStormShapes.glslinc`, which moves into the bake shader.
- **Revert to HEAD:** the march-side shape evaluation in both computes.
  - This covers the `RingStormShapes.glslinc` include, the `g_ws_storm_edge` erosion, the 0.985 top fade in `ring_ws_sculpt_density`, and the lifecycle/band globals.
  - HEAD's legacy analytic shape stays as the fallback while the map is invalid; it also serves as the debug A/B.
- **Drop:** the packed `storm_detail.w` settings word. Shear and mammatus go to the bake through its own parameter block.

## Root causes (evidence)

1. **Perf: a static kernel cost from code inlined into the ray march, not per-sample work.**
   - `sampleScene` is inlined at about six call sites: primary samples, the sun light march, point-light and segment-light marches, the ambient sample and sample probes.
   - The WO-A shape inlines three `ring_storm_cell` copies into each of them.
   - Measurements (4070, `.godot/captures/storm_anvils/perf_*`):
     - HEAD: 4.32-4.35 ms GPU.Clouds.
     - **The legacy branch inside the WO-A shader: 4.77-4.87 ms**, the same math as HEAD plus 0.45 ms.
     - A dead-return stub: 4.28 ms.
     - Pillar+dome alone and anvil alone are free; both together cost.
   - A cost that is independent of the branch taken, with a cliff when two code blocks combine, is the signature of register allocation / occupancy (or instruction cache) in the march kernel. Driver register counts are not observable through Godot, so this is inferred from timing.
   - Any further analytic growth in the march (deck, WO-B supercell, funnels) re-arms the cliff. The fix is to take the shape out of the kernel.
2. **Smooth, pale, flat-shaded slabs.** The storm shape bypasses Sunshine's erosion.
   - Background density goes through `remap(mediumshape, 1 - largeShape, 1)` and then the small-noise remap; that is what makes billows.
   - Storm density is only multiplied by `mix(0.62, 1, medium.r)` (WO-A adds an edge term), so a uniform 0.95 slab stays a slab.
   - A 4-step light march into a uniform slab gives near-constant shading.
3. **Rings and terraces in plan view (both shapes) and stripes on anvil faces.** The ray-march step aliases on analytic surfaces that are sharper than the step.
   - The primary step is 500-958 m (`MinStepDistanceMeters` / `MaxStepDistanceMeters` in `gameplay_cloud_defaults.tres`).
   - The anvil top transition is 0.024 x 17.5 km = 420 m, and the WO-A anvil edge is 0.03 band = 525 m.
   - Samples sit on spheres `t = jitter + k*maxstep` around the camera; on a flat top these give rings centred on the camera nadir, which is exactly the pattern in `SHEET_plan_and_before_after.png`.
   - Background clouds have irregular noise isosurfaces and show no rings.
   - The ray start is jittered per frame (`gameplay_dither_time`, a golden-ratio step), yet the history does not average the lattice away. This is inferred, not isolated. The design does not depend on jitter: it removes the sharp surfaces with vertical fades of at least 1.5 x maxstep and noise-defined isosurfaces. V2 acceptance 4 verifies the result.
4. **The "glassy crystalline column" is the ring arch, not a storm artefact.**
   - It appears only in views along the ring (wind (0.95, 0.30) is almost +around), at the same screen place looking upwind and downwind, and it flares at the horizon (`orbit_new_sheet.png` frames 25, 50, 275).
   - Its brown/cyan mottling is far-arch terrain (desert and water).
   - No action for the storm work.
5. **Thin pillars, small anvils, no mammatus.** These are proportions plus erosion plus aliasing.
   - Pillar radius 0.58-1.0 x 4.5 km inside about 28 km cells.
   - The anvil reaches `1.4*Ra*scale(0.65-1)` downwind, but its density falls to 0.35 at the rim, and the WO-A edge erosion `smoothstep(.75e, .75e+.25, m.r)` removes most of what remains.
   - Mammatus lowers the underside by at most 0.06 band (about 1 km) in a ring band of anvil distance 0.2-0.7, with conical lobes and maturity-gated per cell. That is about one primary step, at 12-16 km altitude, behind the 1-3 km background layer.
   - The `beneath` shot is not aimed at an anvil at all: cells are hashed and the harness only places the system centre.
6. **Blue sky under storms (S0 root cause 3).**
   - StormTower is pillars only (5-10% of the footprint), and the cumulonimbus anvils leave gaps between cells.
   - The background threshold stops at `mix(0.70, 0.90, coverage)`.
7. **Uniform pale grey from beneath, with sharp white shards.**
   - The camera at 1.5 km sits inside the background band. The shards are near background cumulus clipped by depth, fully sunlit because nothing overhead casts a shadow (point 6).
   - The grey is the near rain veil (owned by another workstream; reserved for violent storms by S1 WO5).
   - Captures of the storm shape must therefore run with rain off.

## Options considered

| Option | Perf | Look headroom | Verdict |
|---|---|---|---|
| Specialisation constants | Only helps frames without storms. The gated case (a storm in view) keeps the full kernel. | none | reject |
| Fewer live variables / restructure | Fragile: every later shape feature risks the cliff again, and a single `sampleScene` call site means rewriting Sunshine's march | low | reject |
| Shape in the 1 km macro map | 2D only, 1 km texels: no vertical structure, no towers | none | reject |
| **Baked 2.5D storm profile map** (two height intervals per column at 250-320 m) | 1 bilinear fetch + about 30 ALU per primary sample (measured below); bake amortised | high: any analytic richness at bake time, shared with the shadow compute | **chosen** |
| Baked 3D volume (480x480x128) | 1 trilinear fetch, but 30-60 MB double-buffered and a bake about 50x larger | highest (true overhangs) | fallback if 2.5D cannot represent a later feature |

Why 2.5D is enough:
- A cumulonimbus column holds at most two disjoint cloud intervals: the **body** (deck or tower, from base to top) and the **canopy** (tower flare plus anvil, from underside to top).
- Tower widening is the canopy underside descending toward the core.
- Lean is a height-dependent offset of the lookup position.
- Mammatus is the canopy underside height.
- WO-B features also fit: the wall cloud is a lowered body base, rain bands are a lower body top, rotation is done by the bake.

## Prototype perf (4070, 1600x900 High, storm preview 15 km away, camera 1.5 km, sun 74 deg, clocks paused, 6 windows x 10 s per run)

Method:
- Each variant is a separate shader build (reimported), not a runtime toggle, because the cost under test is static.
- The harness is `storm_anvil_capture.ps1 -Shots perf`, with `--perf-dist` 15000 (camera under the storm, 15 km from the centre) and 40000 (outside, storm in view).
- The value is the median of windows 2-6 of `GPU.Clouds` (ms).
- Rounds were interleaved across four sequences on 2026-10-25 (`.godot/captures/storm_anvils/v2_*`, `v2b_*`, `v2c_*`, `v2d_*`).

The proxies keep HEAD's analytic loop and **add** a map fetch whose result is folded in at 1e-7: the image is unchanged and the fetch cannot be eliminated. That makes them an upper bound for V1, which replaces the loop for storm samples.

| variant | 15 km (under storm) | 40 km (outside) | notes |
|---|---|---|---|
| HEAD | 4.35, 4.37, 4.23, 4.23 (mean 4.29) | 3.24, 3.11, 3.29 (mean 3.21) | round-to-round spread is about 0.15 ms (the noise floor) |
| WO-A (working tree) | 4.87 (WO-A shape), 4.60-4.85 (its legacy branch) | - | +0.58 vs the HEAD mean; the legacy branch alone is +0.3-0.5 (static) |
| 2-fetch proxy, every sample | 4.44 | - | |
| 1-fetch proxy, every sample | 4.61, 4.41 | 3.60, 3.52 | outside: +0.35, the per-sample cost on background samples |
| **1-fetch proxy, gated on the sculpt sub-mask** (V1 structure) | 4.49 | 3.17 | **+0.20 / -0.04** vs the HEAD mean, still including HEAD's loop |
| 2-fetch proxy + analytic deck + background lift (ungated) | 3.38 | - | |
| gated 1-fetch proxy + analytic deck + background lift | 3.25 | 3.50 | **-1.04 under the storm** (the closed deck saturates rays); **+0.29 outside** (more opaque, lit cloud in view) |

Reading:
- The map structure has no material static cost. Gated, outside the storm, it equals HEAD.
- Under the storm, the gated fetch adds about 0.2 ms on top of HEAD's loop. V1 removes that loop for mapped samples; the dead-return stub says it is worth about 0.05 ms.
- The deck lowers the cost under storms by more than everything else adds, but adds about 0.3 ms when a storm is seen from outside. That is density-driven: more opaque, lit pixels, each paying the sun light march and the ambient sample.
- **The outside view is the binding case.** V3 must win back about 0.2 ms there. The lever is the storm-sample light march: samples where storm density dominates take 2 noise light steps plus a closed-form optical depth through the profile map's intervals along the sun direction (one fetch at the sun-offset column). The noise march stays for background samples. The same term gives long-range self-shadowing, so towers darken and the deck darkens under the canopy.
- Independently of the deck, V1 must:
  - gate the fetch on the sub-mask;
  - **let light-march samples reuse the primary sample's profile column** (vertical-only error, lateral offset ≤ 1-2 texels at the light reach), so the fetch runs once per primary sample instead of once per light/ambient sample;
  - measure at least 3 A-B-A rounds per distance, because one round cannot resolve a 0.15 ms gate.
- WO-A reproduced in this session at +0.58 ms, confirming the implementer's finding.

## Architecture: storm profile map

**Texture.**
- `ring_storm_map`: Texture2DArray, RGBA16F, capacity 512x512, 4 layers (front/back x {heights, aux}), allocated once (8 MB).
- Heights texel (the only fetch in the march): (body base, body top, canopy underside, canopy top), in metres.
  - The footprint and influence are encoded by collapsing each interval toward its mid-height: `top' = mid + (top - mid) * coverage`, `base'` likewise. Bilinear filtering then tapers edges vertically, the way cumuliform edges thin out.
  - An empty interval has base' = top'.
- Aux texel: (horizontal depth 0..1, mammatus mask, lifecycle, 0). Read by the bake-side debug views and the harness readback; the march reads it only if V2 shows it is needed and the gate allows.
- Heights are absolute canonical altitudes.

**Frame.**
- World-anchored and square around the camera.
- Half extent = the macro map's `half_around` (handoff end + light reach + margin). Texel = max(250 m, 2*half/500).
- The origin is snapped to texels and wrapped like the macro map.
- Params UBO `RingStormMapBlock`:
  - a = (origin around, origin across, texel, front buffer);
  - b = (used w, used h, circumference, valid);
  - c = (downwind cos, sin, lean m at band top, 0).
- Bindings: main 24/25, shadow 11/12 (free today).

**Bake** (`addons/SunshineClouds2/RingStormProfileBake.glsl`):
- Reads a snapshot of the weather-systems block plus bake params:
  - origin, texel, shift, buffer;
  - pillar radius, anvil multiplier, anvil start;
  - shear, mammatus;
  - the downwind angle.
- Per texel, loops over the sculpt systems whose ellipse covers the texel. For storm profiles (2, 3; WO-B adds 11) it evaluates the deck and the cells (own cell plus the 8 neighbours for the anvil reach).
- **All height fields are continuous** (cells are composed by polynomial smooth-max; the footprint only collapses the interval), so bilinear filtering never makes skirts.
- It reuses the macro map machinery: a driver helper `RingStormProfileMap.gd` owned by `SunshineClouds.gd` with the same snapshot / slice / front-back swap, built in the same callback after the macro map, refresh 0.25 s over 4 slices.
- Natural systems drift at most about 10 m between swaps (under 4% of a texel).

**Sampling** (`RingStormProfileSample.glslinc`, used by both computes in place of the system loop in `ring_ws_sculpt_density` while the map is valid):
- **One** RGBA16F fetch (body base, body top, canopy underside, canopy top; the footprint is encoded by collapsing the interval) at `p.xz - downwind * lean * s^1.5` (s = normalised height in the band). Primary samples only; light and ambient samples reuse the primary's texel.
- Per interval: `v = smoothstep(base, base + fadeLow, h) * (1 - smoothstep(top - fadeTop, top, h))`, with fadeLow and fadeTop at least 1.5 x maxstep (at least 1.4 km).
- Dimensional profile `P = min((h - base)/fadeLow, (top - h)/fadeTop)` clamped to 0..1. Horizontal depth is implicit, because collapsed intervals are thin near the footprint edge.
- The result is (density, P, topness), passed to `sampleScene`.
- The sub-mask and ray-band machinery stay unchanged (they still bound the work).

**Erosion** (`sampleScene`, storm branch), Nubis-style:
- `storm = saturate(remap(Pd, erodeMedium * (1 - mediumshape), 1, 0, 1))`, then `remap(storm, erodeSmall * smallShape, 1, 0, 1)`.
- `Pd` is P times the interval profile v, times the system influence (the bake folds influence into the collapse).
- Erosion is strong where P is small on tops and sides (billows), weak on canopy undersides (smooth), and absent deep inside (dense core, no holes).
- `smallShape` must be fetched for storm samples as well; today it is skipped when only the storm is present.

**Shape v2 (bake, exaggerated).** Per cell: jitter, existence, size, top 0.88-0.98 band and maturity offset (WO-A hashing). R = pillar radius setting x size.
- **Deck:** under the whole system ellipse (mask 0.76-1.0 x influence x fade).
  - Base at band 0.07 with ±250 m ragged noise; top at band 0.30-0.38 with ±600 m billowed noise; full coverage inside the ellipse, collapsing only across the 0.76-1.0 mask edge.
  - Lifecycle: it grows over develop (L 0.3-1), stays through mature, and thins and lifts in dissipate.
- **Tower (body):** core radius 0.8 R from the deck base to the dome top.
  - The dome is 0.85 R wide and rises 1.5-2.5 km above the canopy top.
  - The top carries ±800 m cauliflower lumps (3-5 lobes per cell).
- **Canopy (flare plus anvil):** a teardrop footprint.
  - Downwind reach `(3 + 3*shear) R`, upwind lip `0.7 R`, half width `2.2 R` widening downwind, so neighbouring anvils merge at maturity.
  - The underside descends from the anvil base (band 0.62-0.70) to the deck top at r = 0.8 R (the flare).
  - Thickness: 4 km near the core to 1.2 km at the rim. The flat top carries ±300 m billows.
- **Mammatus:** at L > 1.4 and 0.25 < anvil distance < 0.8, the underside drops by `mammatus * 900 m * sqrt(1 - (d/r)^2)` in hemispherical pouches of r 0.9-1.4 km (hashed hex lattice).
- **Lifecycle:** as in WO-A A3, with the new proportions.

**Darkness:**
- Per-ray storm ambient (S0 WO-5b): Sunshine's ambient is per ray, so the march accumulates the storm share of the ray's opacity and darkens and tints the ambient by it (see V3). This needs one GenericData slot and a reimport of every compute that includes `CloudsInc.comp`.
- The storm light shortcut (V3) adds long-range self-shadowing through the profile map, so the dark sides of towers and the deck under the canopy are dark.
- The shadow compute samples the same map, so `terrain_cloud_direct_sun_visibility` darkens the deck under the canopy and the background under the deck.

**Sky closure:** `weatherBackgroundThreshold = mix(mix(.70, .90, cov), 1.05, smoothstep(.35, .80, weatherStorm))` in both `sampleScene` copies.

## Work orders

Order: V1 -> V4 -> V2 -> V3. V1, V2 and V3 touch the Sunshine computes, so they run serially with one owner. V4 is harness-only: it needs V1's map and feeds V2's captures.

Every step that changes `.glsl` needs the Sunshine reimport:
1. Delete `.godot/imported/SunshineClouds*.glsl-*.md5`.
2. Touch the `.glsl` files.
3. Run a windowed `--path . --import`, and check that the `.res` timestamps are newer than the start. One import in this session crashed with exit -1073741819 and silently kept the old shader; the `seq2.ps1` pattern in this design's scratch work retries until both `.res` files are fresh.

**Coordination:** `SunshineClouds.gd` is also being edited by the storm-light workstream (macro map exposure). V1 keeps its edit to a minimal hook and does it after that work lands, or main sequences it.

### WO-V1: storm profile map infrastructure plus the march revert
- **Objective:** move storm shape evaluation out of the ray-march kernel into a baked, world-anchored 2.5D map shared by the main and shadow computes.
- **Files:**
  - New: `addons/SunshineClouds2/RingStormProfileBake.glsl`, `RingStormProfileSample.glslinc`, `RingStormProfileMap.gd` (driver helper: resources, snapshot, slices, front/back, params UBO, GPU timestamp scope `GPU.CloudStormMap`).
  - Edit `SunshineClouds.gd`, minimal: instantiate the helper, call `update_storm_profile_map()` right after `update_macro_weather_map()`, add the uniforms at main 24/25 and shadow 11/12, free resources on exit.
  - Edit both computes.
  - Edit `RingSunshineCloudBridge.CameraIndependentClouds.cs`: `PublishStormProfileMap` next to `PublishMacroWeatherMap`, with the same half extent, centred on the camera in both axes, plus the shape params (pillar radius, anvil multiplier, anvil start, shear, mammatus).
  - Edit `RingSunshineCloudBridge.cs`: delete `PackStormShapeSettings` and the `storm_detail.w` write.
  - `RingStormShapes.glslinc` becomes a bake-only include.
- **Required:**
  1. Restore both computes' storm code to HEAD (`git show HEAD:...`). Then, inside `ring_ws_sculpt_density`, when `ring_storm_map_valid()` and the sub-mask is non-empty, replace the per-system loop with **one** RGBA16F fetch: body base, body top, canopy underside, canopy top.
     - The footprint is encoded by collapsing each interval toward its mid-height (see "Texture"); there is no separate density fetch.
     - Density is `max(body(h), canopy(h))`, using the vertical fades from "Sampling". Light and ambient samples reuse the primary sample's fetched texel (a per-primary global next to `g_ws_sub_mask`).
     - The function also outputs P and topness through globals for V2.
     - Invalid map: HEAD's legacy loop, which is also the debug A/B (`RING_STORM_MAP=0` replaces `RING_STORM_SHAPE_LEGACY`).
  2. The bake implements shape v2 geometry (tower, dome, canopy, mammatus, lifecycle) as specified above, with continuous heights (smooth-max). It writes the heights texel and the aux texel. The deck is coded behind a bake constant that stays off until V3, because the deck's outside-view cost needs V3's light shortcut.
  3. The bake reuses the WO-A cell hash and `d.w` lifecycle. Static (manual) systems are not shifted; natural systems use the snapshot shift, exactly as `ring_ws_macro_point` does.
- **Do not change:**
  - the weather-systems record layout, `RingWeatherSystems.glslinc` formulas, the macro map, non-storm profiles, `gameplay_cloud_defaults.tres`;
  - the storm-light files (`ring_shadow_compose`, `day_side_atmosphere.gd`, `RingSunshineCloudBridge.StormLight.cs`) and the rain files.
- **Validation:**
  - `StormLifecycleCodeTest`, the natural weather v5 fingerprint (bit-identical), `gameplay_cloud_tuning_smoke_test`.
  - New `tools/storm_profile_map_gpu_test.gd`, which reads back the front layers with `texture_get_data`:
    - two bakes of one snapshot are byte-identical;
    - heights are finite and inside the band;
    - neighbouring texels with coverage > 0.05 differ by at most 1.5 km in base and top (continuity);
    - the map is invalid when camera-independent clouds are off.
  - Perf: A-B-A against HEAD shaders with `storm_anvil_capture.ps1 -Shots perf` at `--perf-dist` 15000 and 40000. `GPU.Clouds` + `CloudShadowNear` + `CloudStormMap` ≤ HEAD + 0.15 ms (median of windows 2-6).
  - `flicker` on a 10 s orbit: no spike at map swaps.
- **Escalate if:**
  - two fetches are needed and the gate fails, or the one-fetch proxy measurement (below) does not hold;
  - the shadow compute needs a different map resolution.

### WO-V2: look (erosion, proportions, mammatus, anti-terrace)
- **Objective:** a billowy cauliflower tower, a broad sheared canopy with a billowy top and a smooth mammatus underside, and no rings.
- **Files:** `SunshineCloudsCompute.glsl` (`sampleScene` storm branch), `SunshineCloudShadowCompute.glsl` (same erosion, without small noise), the bake.
- **Required:**
  - Replace `stormDensity *= mix(.62, 1, medium.r)` with the profile-driven remap erosion. Fetch `smallShape` for storm samples. Underside erosion ≤ 25% of top erosion.
  - Lean as a lookup offset.
  - Bake: proportions and mammatus as specified; ±300-800 m lumps on tops; vertical fades ≥ 1.5 x maxstep.
  - Retune only through constants in the bake, plus the two existing settings.
- **Do not:** add settings; change the background erosion path.
- **Validation:**
  - V4 captures against the WO-A sheets: criteria 2, 3 and 4 below, except the deck parts.
  - The perf gate as in V1. The erosion adds one small-noise fetch per storm primary; if it busts the gate, erode with the medium channel only and report.
- **Escalate if:** 2.5D cannot produce an acceptable silhouette (for example the tower flare reads as a cone). That is the trigger for the 3D-volume fallback, an architecture decision for main.

### WO-V3: sky closure and darkness (S0 WO-5 + 5b)
- **Required:**
  - Background threshold lift in both `sampleScene` copies (see "Sky closure").
  - The deck's density and lifecycle are tuned so that a storm level of at least 0.6 closes overhead.
  - Per-ray storm ambient:
    - accumulate `stormShare = Σ(stormDensity·T) / Σ(density·T)` along the ray;
    - `ambientLight = mix(ambientLight, StormCloudAmbientColor * darkness, stormShare * g_storm)`;
    - remove the 0.35 ambient floor for that share.
    - This needs one new GenericData vec4 (colour + strength) in `CloudsInc.comp`, so reimport every compute that includes it, the macro map shader included. The bridge writes it from the existing `StormCloudAmbientColor` and the S0 "Storm darkness" value (read only, no new setting).
  - Storm light shortcut in `sampleLighting`, for primaries whose storm density exceeds their background density:
    - `min(2, steps)` noise steps, plus `exp(-k * OD)`;
    - OD = the closed-form overlap of the sun ray with the body and canopy intervals of the column at `p + sunDir * (canopyTop - h) / sunDir.y`;
    - one extra fetch per primary sample;
    - `k` is tuned so that a 4 km chord is at least 99% opaque.
- Coordinate with the storm-light owner on who sets the colour.
- **Validation:**
  - S0 WO-5 captures with `storm_light_capture.ps1`: no blue sky; no tree shadows at 6 and 12 km; the 45 km outside view is dark.
  - Perf: 3 A-B-A rounds against HEAD at 15 and 40 km. The gate is the binding outside case: if 40 km exceeds +0.15 ms after the light shortcut, stop and report the numbers. Do not thin the deck to pass.

### WO-V4: acceptance captures and gate (lead reviews)
- Harness additions in `storm_anvil_capture.gd` / `StormAnvilCapture.cs`:
  - `--sun=<deg>`: set the cycle so the sun is at 35 deg.
  - Rain off: `PrecipitationStart` 0.99.
  - Hero-cell targeting: read back canopy layer 1 and pick the texel with the greatest downwind canopy reach in the preview system.
  - Shots relative to the hero cell: side30/side60 across the shear, side60hi, downwind looking upwind, beneath (2 km under the downwind canopy at anvil distance 0.5, pitch 60, mammatus 0 vs 1), plan and plan_wide, life strip, 10 s orbit for `flicker` and `mp4`.
- Gate run: S0 `storm_light_capture.ps1` distance sweep with rain off.

## Acceptance criteria (all on the RTX 4070; captures at sun 35 deg, clocks paused, rain off)
1. **No blue sky under a storm.**
   - up60, up85 and y180 at 0, 6, 12 and 18 km inside a natural StormTower front and the Cumulonimbus preview: at least 99% of sky pixels fail the S0 blue-minus-red chroma threshold.
   - No tree shadows at 6 and 12 km.
2. **Dark, massive towers.**
   - In side30, the tower is opaque: no background visible through its core.
   - Its silhouette is at least 2.5x the core diameter of the WO-A capture.
   - The shadowed flank's median linear luma is at most 0.5x the sunlit flank's.
   - The deck base is darker than the clear-sky horizon.
   - The 45 km outside view reads as a dark mass (S0 criterion).
3. **Broad sheared anvil.**
   - From the map readback at maturity: downwind canopy reach at least 3x the tower core radius and at least 3x the upwind lip; canopy width at least 2x the tower core diameter.
   - Visually, billowed tops (side60hi) and a smooth underside.
   - Mammatus visible in beneath at L = 1.6: the `compare` of mammatus 1 vs 0 in the underside region has SSIM ≤ 0.97, and the pouches are visible on the sheet.
4. **No rings or terraces.** plan, plan_wide and the anvil faces in side30 show no camera-centred concentric bands or horizontal striations at 1:1 crop. Lead review against the WO-A sheets.
5. **No pops.** `flicker` on the 10 s orbit and over a map rebuild shows no new spikes compared with HEAD.
6. **Perf.**
   - Sunshine GPU (`GPU.Clouds` + `CloudShadowNear` + `CloudStormMap`) ≤ HEAD + 0.15 ms, by A-B-A at perf-dist 15 km (under the storm) and 40 km (outside).
   - A1000 High ≤ +0.3 ms when the laptop is available.
7. **Regressions.** Natural weather fingerprint bit-identical, `STORM_LIFECYCLE_CODE`, cloud tuning smoke, `haze_consistency_gpu_test --model=physical`, the cloud shadow gate tests, and SSIM about 0.997 against HEAD outside storms.
