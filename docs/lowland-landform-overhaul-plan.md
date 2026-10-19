# Lowland Landform Overhaul (recipe v7) — Implementation Plan

Status: **implemented (Steps 0-7), 2026-10-18.** Launcher adoption is live: new Terrain3D runtime-generation
worlds are created as v7 by default (checkbox "Hill-country landforms (v7)", project setting
`ringworld/runtime/surface_landforms_v7`, default true). The v7 formula and defaults are now frozen
(Freeze rule, section 3); any change needs v8. Step 6/7 results are in section 11. Prepared on branch
`perf-hotpath-optimizations`. Scope of this batch is **shape only**: no shader, texture or prop work.

User decisions this plan implements:

- Relief target is "hill country". Plains stay as calm regions for contrast. Hilly regions reach
  200–500 m relief over 2–6 km wavelengths, with plateaus, escarpments and some steep faces, and
  read as silhouettes along the ring arc.
- Only new worlds get the change. A new recipe id, `ringworld_surface_landform_v7`, extends v6.
  Saved worlds keep their frozen recipe JSON and must generate bit-identically to today.
- Every new tunable appears in the grave-key menu, using the existing menu and persistence
  conventions.

---

## 1. Verified current state (evidence)

Code paths, each confirmed by reading the source:

| Fact | Where |
| --- | --- |
| One C# formula is the only height source for every consumer: Terrain3D tiles, collision, proxies, global LOD, clouds, weather, grass, the minimap and emitter registries. No shader or native port of the formula exists. | `scripts/Terrain/Generation/TerrainGenerationHeightModel.cs` via `RingTerrainSampler.SurfaceGeneration.cs` |
| The GDScript "mirror" has two parts. (a) `terrain_generation_recipe.gd` holds the recipe **fields, sanitising and normalized dictionary** only. (b) `tools/fixtures/terrain_height_model_reference.gd` is a **frozen v1-only, test-only** copy of the formula. `terrain_height_model.gd` is a thin adapter over the C# bridge. | `scripts/Terrain/Generation/*.gd`, `tools/fixtures/` |
| Lowland relief = foothill only: `100 m · coast · (1-valley) · (1-.65·belt) · Unit(fbm3 @1500 m)`. Secondary/micro need `alpine>0`, `ridge>0` and `macro ≥ 2000 m` (`DetailMaskFor`), so they are 0 in lowlands. Spurs and gullies are masked by `spurMask = coast·basin·belt`, so they are 0 where belt = 0. | `SampleNaturalDiagnostics` L185-194, `DetailMaskFor` L416 |
| `belt = SmoothStep(.4,.8,Unit(n))` is **exactly 0** across large areas, so the expensive mountain terms are already skipped there. | L159 |
| The runtime gen-17 path has **no rivers or lakes**. Water is `presentationHeight < 0 ⇒ Ocean`, so any inland point pushed to ≤ 0 m floods. | `CreateRuntimeTerrainSampleLocked` |
| Biomes are resolved in `RingTerrainSampler` from **VoxelTerrainSettings-owned** temperature, moisture and erosion noise. That noise is not part of the recipe. Temperature is lapsed by final height (0.075/km default). Alpine selection uses the model's `RidgeWeight`. | `SampleTerrainFieldsLocked`, `GetRuntimeTerrainFieldsLocked`, `ResolveBiomeBlend` |
| A v5/v6 site and climate registry is built from the **unshaped natural** model of the same recipe and cached by `DefinitionHash`. | `EmitterSiteTerrainShaper.GetOrCreate` |
| The launcher turns any v4 preview recipe into v5 for a new world. A climate toggle (default OFF) switches v5↔v6. Existing slots keep their stored JSON. | `world_size_launcher.gd` ~L1380-1395, L913-920 |
| The grave-key recipe controls are on the **"Terrain3D"** tab (`BuildGenerationPreviewTab`). Save writes `user://terrain_generation_preview/recipe_v3.tres` through `TerrainGenerationPreviewStore`. | `RingLiveTuningDrawer.GenerationPreview.cs` |
| Proxy skirts are 8 m. Global and far LODs point-sample the same function at coarse spacing. | `RingTerrainProxySector.SectorSkirtDepthMeters`, `RingAnalyticGlobalLod` |
| Walkable slope defaults to 55°. The emitter viewpoint search rejects slopes > 20°. | `GameplayMotionSettings`, `EmitterSiteNavigator.MaxSlopeDegrees` |

### Measured on the current default (v4 natural = v5/v6 natural), seed 1337, headless

The script sampled 600×40 points over the whole ring. Source: a scratch calibration script with
the 4.8-dev7 console build, not checked in.

| Metric | Value |
| --- | --- |
| Land fraction | 0.517 |
| Continentalness on land, p25/p50/p75/p95 | .086 / .184 / .302 / .471 |
| Height of **pure lowland** (alpine < .02, c > .12), p25/p50/p75/p95 | 178 / 478 / 1112 / 1523 m |
| Relief in a 4 km lowland window (max-min), p25/p50/p75 | 66 / 136 / 235 m (mostly regional tilt) |
| Lowland slope at 8 m, p50/p95 | 1.7° / 5.9° |

**Correction to the known findings, item E.** The continental transfer already produces a broad
**300–1500 m interior band**. Interior lowlands with c > .3 sit at 450–1500 m, but they are
smooth tilted tablelands. A new knot is therefore not needed. What is missing is *structure*:
hills, shelves and escarpments. v7 leaves the continental knots unchanged. As a result,
**coastlines, the land/ocean mask, ocean depths and continentalness are identical between v6 and
v7**. This also keeps moisture (ocean proximity) and site land/ocean classification stable.

### Prototype of the v7 formula (§3)

The prototype was GDScript on top of the v4 bridge diagnostics. Its new noise used unwarped
coordinates. 3000 pure-lowland points had gate ≥ .5:

| Class (eligible pure lowland) | Coverage | Slope 4 m p50 / p95 / max | 4 km relief p50 / p95 |
| --- | --- | --- | --- |
| Plains (R = 0) | 34 % | 3.1° / 8.2° / 44° | 144 / 522 m* |
| Rolling (0 < R < 1) | ~37 % | 6.8° / 21° / 43° | 280 / 634 m |
| Hill country (R = 1) | 29 % | 13° / 26° / 41° | **271 / 547 m** |
| Plateau interior (Q ≥ .8, R < .3) | 12 % full, 32 % any | 3.6° / 17° / 44° | 240 / 554 m |
| Breaks (Q ≥ .5, R ≥ .7) | ~9 % | 12° / 41° / 64° | 380 / 776 m |
| Plateau edge, max slope along a 6 km transect at 10 m | — | p50 31°, p95 56° | — |

\*Plains windows include neighbouring rolling ground and the regional tilt.

No sample went below sea level. These numbers meet the target and seed the defaults in §4.

---

## 2. Design decisions

| Item | Decision | Reason |
| --- | --- | --- |
| **A. Landform field** | **Adopt**, with two low-frequency selectors. **R** (ruggedness, 32 km, 3 oct) picks plains → rolling → hill country. **Q** (plateau, 64 km, 2 oct) picks uplands and plateaus. Their product **Q·R** gives dissected plateau, i.e. breaks or badlands. | A single 1-D field forces concentric class rings. Two fields give natural combinations: plateau with low R is tableland, plateau with high R is breaks. The cost is one extra noise call. |
| **B. Per-biome modulation** | **Defer, not in v7.** | Biome inputs are owned by `VoxelTerrainSettings` noise in `RingTerrainSampler`, outside the recipe and outside the model used by the preview, Terrain3D jobs and the registry builder. Using them would make terrain depend on non-recipe settings, which breaks frozen-world safety. Resolved biome also depends on height through the lapse rate, which is genuinely circular. Raw pre-lapse climate is acyclic but lives in the wrong owner. Path forward: a later v8 moves the raw temperature and moisture base fields into the recipe, read by both the height model and the biome resolver. Dunes, drumlins and similar features can key off those fields then. |
| **C. Lowland detail floor** | **Adopt.** `detailMask = max(v6 mask, floor)`. The floor is scaled by the gate and by ruggedness, with plains getting 20 % of it. | Secondary and micro detail give walk-scale texture. A low floor on plains keeps them calm (p95 slope 8°). |
| **D. Lowland gullies** | **Adopt behind a performance gate.** Reuse the enhanced gully octave loop, guided by the hill-field gradient. Default strength is 24 m. If the perf gate (§6) fails, the default becomes 0, but the control stays exposed. | The mountain guide comes from the ridge gradient, which means nothing in lowlands. Hill gullies must run down the hill slope. |
| **E. Continental retune** | **No knot change.** Uplands come from Q (lift plus terraces), and the existing interior band is terraced by the plateau term. | §1 measurement. Leaving knots alone keeps coastlines and seas identical to v6. |
| **Climate machines in v7** | A new int field `climate_machines` (0/1). `HasClimateMachines = v6 ‖ (v7 ∧ climate_machines==1)`. | Without it, the launcher's climate toggle would need a second v7 id, which doubles every id list. |
| **Code structure** | v7 gets its **own method**, `SampleLandformNaturalDiagnostics`. It starts as a verbatim copy of the v4+ part of `SampleNaturalDiagnostics`, then adds the v7 terms. v1–v6 code is not refactored. | This guarantees bit-identity for frozen worlds. About 60 duplicated lines is an acceptable price. |

---

## 3. v7 formula (normative)

The v7 method has the same signature and return type as `SampleNaturalDiagnostics`. `CalculateHeight`
and `SampleDiagnostics` dispatch to it when `_p.HasLowlandLandforms`. The site and climate shaper
still runs afterwards, unchanged.

```
// 1. Verbatim copy of the HasMountainDetail && HasEnhancedErosion path of SampleNaturalDiagnostics
//    up to and including `erosion` (warp, continental + ocean gradient, land, belt, alpine, sea,
//    lowland, valley, ridge, uplift, enhanced trench, macro += uplift - trench, detailMask (v6),
//    coast, basin, foothill, spurMask, spur, erosion). Do NOT compute `detail` yet.
// 2. Landform terms
gate = lowland * SmoothStep(LandformShoreStartM, LandformShoreFullM, macro)   // lowland = land*(1-belt)
landform = 0; lowlandCarve = 0; R = 0; Q = 0
if (gate > 0) {
    R  = SmoothStep(PlainsThreshold,  HillsThreshold,        Unit(Sample(_landform, warped, warpedAcross)))
    Q  = SmoothStep(PlateauThreshold, PlateauFullThreshold,  Unit(Sample(_plateau,  warped, warpedAcross)))
    nb = Sample(_hillBase, warped, warpedAcross)
    nd = HillDetailWeight > 0 ? Sample(_hillDetail, warped, warpedAcross) : 0
    hn = Clamp((nb + HillDetailWeight*nd) / ((1 + HillDetailWeight) * HillNormalization), -1, 1)
    profile = Math.Pow(.5 + .5*hn, HillValleyExponent)
    hillAmp = HillAmplitudeM * Lerp(PlainsHillFraction, 1, R)
    hill = hillAmp * profile * (1 - HillValleyFloor * valley)
    breaksW = BreaksStrength * Q * R
    if (breaksW > 0) { y = macro + hill; hill += breaksW * (Terrace(y, BreaksStepM, TerraceRiserWidth) - y); }
    plateau = 0
    if (Q > 0) {
        lift = PlateauLiftM * Q
        x = macro + lift + (EscarpmentJitterM > 0 ? EscarpmentJitterM * Q * Sample(_escarpment, warped, warpedAcross) : 0)
        plateau = lift + TerraceStrength * SmoothStep(0, .5, Q) * (Terrace(x, PlateauStepM, TerraceRiserWidth) - x)
    }
    landform = gate * (hill + plateau)
    floorMask = LowlandDetailFloor * gate * Lerp(PlainsDetailFraction, 1, R)
    if (floorMask > detailMask) detailMask = floorMask
    if (LowlandErosionStrengthM > 0 && landform > 0)
        lowlandCarve = LowlandGullies(warpedAround, warped, warpedAcross, nb, hillAmp, gate, R, landform)
}
detail = detailMask > 0 ? detailMask*(SecondaryAmplitudeM*Sample(_secondary,..)+MicroAmplitudeM*Sample(_micro,..)) : 0
total  = detail + foothill + spur + erosion + landform + lowlandCarve
raw = macro + total; height = Clamp(raw, Min, Max)
return new(height, macro, continental, sea, lowland, alpine, detailMask, valley, ridge, uplift, trench, total,
           raw != height, foothill, spur, erosion,
           LandformHeight: landform, LowlandErosionHeight: lowlandCarve, Ruggedness: R, PlateauWeight: Q, LandformGate: gate)
```

Helpers (new, `private static`):

```
Terrace(x, s, w) { double q = x/s, b = Math.Floor(q); return s*(b + SmoothStep(0, w, q - b)); }   // C1, riser at step start
```

`LowlandGullies`:

- **Guide.** Use forward differences of `_hillBase` at `ε = Clamp(HillScaleM*.02, 16, 128)`.
  One sample is at `around+ε` with a new `CylindricalProjection`; the other is at `across+ε` with
  the existing projection. The guide is `(-gA, -gZ)`.
- **Slope estimate.** `slope ≈ Hypot(gA,gZ) * hillAmp * .5 / ((1+HillDetailWeight)*HillNormalization)`,
  and `slopeMask = SmoothStep(.03, .25, slope)`. Return 0 if `slopeMask ≤ 0`.
- **Octave loop.** Duplicate the **octave loop** of `EnhancedDirectionalGullies`, including the
  `ErosionRidgePreservation` gating, for `octave < LowlandErosionOctaves`. It calls the existing,
  **unchanged** `EnhancedCellGullyField`. The same octave index and `ErosionScaleM` mean the same
  cells, so the stencil cache is shared without thrash.
- **Result.** `-Min(LowlandErosionStrengthM, .5*landform) * gate * Lerp(.3,1,R) * slopeMask * Clamp(carve/weights,0,1)`.

Invariants the tests must enforce:

- All gates are SmoothStep or product based, and every short-circuit happens only where the skipped
  term is exactly 0, so **height is C0 everywhere**.
- `hill ≥ 0`. `plateau ≥ ~-(w²/12)·s`, which is under 1 m. The carve is at most half the landform.
  The signed detail floor is multiplied by `gate`, which is quadratically 0 at the shore. Together
  these mean **no land point with `macro > 0` reaches ≤ 0 m**, so there is no inland flooding.
- Landform terms are multiplied by `(1-belt)` through `lowland`. Mountains in full belts are
  unchanged, and the existing headroom validation stays sufficient. The final `Clamp` remains.
- New noise uses the shared `warped` projection, so it is seam-periodic through `CylindricalProjection`.

Constants frozen into the v7 algorithm, not in the recipe:

- Octave counts: landform 3, plateau 2, hill base 2, hill detail 2, escarpment 3. All are FBm
  SimplexSmooth with recipe gain and lacunarity.
- Hill detail scale = `HillScaleM/4`.
- `HillNormalization = 0.48`. Re-verify it in Step 3; it may change only before v7 is offered by the launcher.
- Seed offsets: `_landform` 1201, `_plateau` 1301, `_hillBase` 1409, `_hillDetail` 1423,
  `_escarpment` 1511. None collides with 101…1106.

**Freeze rule:** once Step 6 lets the launcher create v7 worlds, the v7 formula and constants are
frozen. Any later change needs `v8`.

---

## 4. Recipe v7 parameters

- Id: `ringworld_surface_landform_v7`.
- Field set: all v6 fields, plus the fields below, appended in **this order** in `Canonical()`.
- Ranges are C# `Range()` bounds. The GDScript `snapshot()` must clamp to exactly the same bounds,
  including the cross-field constraints.

| JSON field | Default | Range | Menu label |
| --- | --- | --- | --- |
| `climate_machines` (int) | 0 | {0,1} | existing climate checkbox |
| `landform_scale_m` | 32000 | 8000–200000 | Landform region scale (m) |
| `plains_threshold` | 0.445 | 0.05–0.95 | Plains → rolling threshold |
| `hills_threshold` | 0.575 | plains+0.01 – 0.99 | Rolling → hill-country threshold |
| `plateau_scale_m` | 64000 | 16000–400000 | Plateau region scale (m) |
| `plateau_threshold` | 0.58 | 0.05–0.95 | Plateau start |
| `plateau_full_threshold` | 0.68 | plateau+0.01 – 0.99 | Plateau full |
| `hill_amplitude_m` | 480 | 0–900 | Hill amplitude (m) |
| `hill_scale_m` | 5000 | 1000–16000 | Hill wavelength (m) |
| `hill_detail_weight` | 0.35 | 0–1 | Hill detail weight |
| `hill_valley_exponent` | 1.35 | 0.5–3 | Hill valley broadening |
| `plains_hill_fraction` | 0.08 | 0–0.5 | Plains swell fraction |
| `hill_valley_floor` | 0.6 | 0–1 | Vale corridor flattening |
| `plateau_lift_m` | 320 | 0–900 | Plateau lift (m) |
| `plateau_step_m` | 160 | 20–600 | Terrace step (m) |
| `terrace_riser_width` | 0.14 | 0.04–0.5 | Escarpment riser width |
| `terrace_strength` | 0.85 | 0–1 | Terrace strength |
| `escarpment_jitter_m` | 70 | 0–300 | Escarpment meander (m) |
| `escarpment_scale_m` | 12000 | 2000–50000 | Escarpment meander scale (m) |
| `breaks_strength` | 0.6 | 0–1 | Breaks (dissected plateau) strength |
| `breaks_step_m` | 40 | 8–200 | Breaks strata step (m) |
| `landform_shore_start_m` | 20 | 0–500 | Shore buffer start (macro m) |
| `landform_shore_full_m` | 140 | start+1 – 1500 | Shore buffer full (macro m) |
| `lowland_detail_floor` | 0.45 | 0–1 | Lowland detail floor |
| `plains_detail_fraction` | 0.2 | 0–1 | Plains detail fraction |
| `lowland_erosion_strength_m` | 24 (perf-gated) | 0–120 | Lowland gully depth (m) |
| `lowland_erosion_octaves` (int) | 2 | 1–`erosion_octaves` | Lowland gully octaves |

Versioning mechanics in `TerrainGenerationParameters.cs`:

- `LandformGeneratorIdentity`. Add a `LandformFields` set, which is `SitesFields` plus the above.
  Update the id check in the constructor and the `ValidateShape` mapping.
- Feature flags. `HasMountainDetail`, `HasEnhancedErosion` and `HasEmitterSites` add v7.
  `HasClimateMachines` uses the rule in §2. Add a new `HasLowlandLandforms`.
- `Canonical()`. Append the v7 segment **only** when the id is v7. v1–v6 output must stay
  byte-identical, which the Step 0 golden verifies.
- `LandformDefault()` builds from `SitesDefault().CanonicalJson` with the id replaced and the v7
  fields appended.
- Model fields. `RingTerrainSampler.CalculateRuntimeTerrainDefinitionHash` needs no change: the v7
  `DefinitionHash` differs, and the site and climate terms are mixed as today. Generator version stays 17.

`terrain_generation_recipe.gd`:

- Add `LANDFORM_GENERATOR_ID` and `@export` vars with the **v7 defaults as script defaults**. This
  way a v4–v6 preview recipe upgraded to v7 gets sensible values.
- `_is_erosion_family` includes v7. Add a new `_is_landform_family`.
- `snapshot()` clamps the v7 fields. `_normalized_dictionary_fields()` emits them for v7 only, with
  ints as ints. Add `create_landform_default()`.
- `validation_errors()` accepts the v7 id.

`terrain_generation_preview_store.gd` accepts the v7 id. The path `recipe_v3.tres` is unchanged.

---

## 5. Interactions

- **Slopes and walkability.** In the prototype, hill-country p95 slope is 26° and the maximum is
  41°, well under the 55° walk limit. Breaks and plateau edges intentionally reach 56–64°, which
  gives steep faces. The Terrain3D 2 m lattice and collision handle this; v6 mountains are already
  steeper. Step 3 asserts that the hill-country fraction over 55° is below 0.5 %.
- **Coast and beaches.**
  - Landform terms are gated by macro height 20 → 140 m. With the unchanged continental curve, that
    puts hills several km inland.
  - The beach and shelf Hermite (ocean branch) and `CoastalBeachStrength` texture weights are untouched.
  - The coastline is identical to v6 because the land mask and macro are unchanged near the shore.
- **Rivers and lakes.** None exist in gen-17. The only water rule is height < 0 ⇒ ocean, and the
  §3 invariant prevents inland flooding. The valley noise (36 km) now forms "vale corridors" through
  hill country via `hill_valley_floor`. These are natural walk routes and the future home for rivers.
- **Mountain belt transitions.**
  - Landform terms fade with `(1-belt)`. Foothill, spur and mountain gullies are unchanged.
  - In partial belts both systems blend. Lowland and mountain gullies then share stencil cells
    through the same octave indices.
  - Headroom is unchanged, and `Clamp` stays as the backstop.
- **Biome shifts.**
  - Alpine, which keys on `RidgeWeight`, and moisture are unchanged.
  - Temperature drops by up to about 0.07 on 900 m plateau-plus-hill tops (0.075/km lapse). Near
    the tundra (0.28) and desert (0.35) limits this produces hilltop and valley mosaics.
  - The beach weight is unchanged because it depends on continentalness.
  - Step 7 reports the fraction of land samples whose primary biome changes from v6 to v7. This is
    informational, with a loose bound of 20 %.
- **Emitter sites and climate machines.**
  - v7 natural terrain differs, so the registries differ from v5/v6. This is expected for new worlds.
  - Pad cut and fill increase in hills. Placement scoring already penalises cut and fill
    (`CutFillPenaltyScaleM`, `MainMaxCutFillM`).
  - The test asserts that every site builds and that the cut/fill caps hold. It also checks that
    `EmitterSiteNavigator` viewpoints still resolve under the 20° rule, and reports the cold
    registry build time.
- **LOD and far-proxy consistency.**
  - Every consumer samples the same C# function, so they are consistent by construction and need
    no shader or GPU mirror.
  - Risk: 2–5 km hills and terrace risers alias in coarse proxies and the global LOD (point
    sampling), as mountains already do. Larger near-to-proxy height mismatch could exceed the
    8 m skirt.
  - Mitigation in this batch: verify with the existing GPU LOD tests on a v7 hill-country location
    (Step 7).
  - Follow-up, not this batch: a footprint-filtered sampling API for coarse LODs.
- **Seam and precision.** `CylindricalProjection` keeps the new noise periodic. The float X/Y at a
  5,000 km radius has about 0.5 m ulp, which is fine for wavelengths of 625 m and up. Micro detail
  already lives with this.

---

## 6. Performance (perf-sensitive branch)

Native `FastNoiseLite.GetNoise3D` calls per sample, for an eligible lowland point (gate > 0, belt = 0):

| Term | v6 calls / octave evals | v7 adds |
| --- | --- | --- |
| warpX, warpZ, continental, belt, valley, ridge, foothill | 7 / 24 | — |
| landform R, plateau Q | — | 2 / 5 |
| hill base + hill detail | — | 2 / 4 |
| escarpment (only Q > 0, ~32 % of eligible land) | — | 1 / 3 |
| secondary + micro (now active via floor) | 0 | 2 / 5 |
| **Subtotal, no gullies** | **7 / 24** | **+6–7 / +14–17** |
| lowland gullies (hills and rolling with slopeMask > 0) | — | +2 / +4, plus 2 gully octaves × 9 cells (sincos) |

Estimates are from the 2026-09-29 optimized harness numbers (about 1.5 µs/sample for a foothill
tile, about 3.1 µs/sample for the default mountain tile with gullies):

- v7 plains and hill tiles without gullies: about +65–85 % over a v6 lowland tile.
- v7 with lowland gullies: about 1.0–1.15× a v6 default **mountain** tile.
- Ocean (gate = 0), coastal plain and full-belt samples: unchanged, because they short-circuit.

Mitigations built into the design:

1. A gate-first short-circuit means no landform noise in ocean (48 % of the ring), the coastal
   buffer or full belts.
2. The escarpment noise runs only where Q > 0. Breaks terracing runs only where Q·R > 0.
3. The gully guide uses **forward** differences on the 2-octave base: 2 calls, not 4 central
   differences on a 4-octave field. Lowland gullies default to 2 octaves.
4. The shared `warped` projection is reused. There is no per-sample allocation. Noise objects are
   allocated **only for v7 models**, following the `_foothill` pattern.

Perf gates (Step 4, same build, same machine, `tools/terrain_generation_cpu_benchmark.gd` extended):

- The v7 hill-country tile median must be ≤ 1.15× the v6 default **mountain** tile median.
- The v7 plains tile median must be ≤ 1.9× the v6 plains tile median.
- If the hill gate fails with gullies, set the `lowland_erosion_strength_m` default to 0 and record
  the numbers. The control stays.
- Integration: `terrain3d_gameplay_travel_benchmark.gd` at 60 m/s over hill country on a v7 world
  must show zero coverage waits and zero support misses.

All other height consumers (clouds, weather, grass, decorations) inherit the per-sample cost
increase, but GPU ms are unaffected.

---

## 7. Grave-key menu (Terrain3D tab) and persistence

The work is in `RingLiveTuningDrawer.GenerationPreview.cs`:

- Add a new collapsible section **"Lowland landforms (v7)"** using `TuningMenuUi.CreateSection`.
  It contains every §4 slider, plus a spin box for `lowland_erosion_octaves`. Give the
  controls-list tuples a `LandformOnly` flag next to `DetailOnly`, `CoastalOnly` and `EnhancedOnly`.
  Controls are disabled for non-v7 recipes, as `enhancedOnly` controls are today.
- Add a button **"Use hill-country landforms (v7)"**, visible for v4, v5 and v6 recipes. It calls a
  new `upgrade_to_landforms()` in `terrain_generation_preview.gd`, which sets the id to v7 and keeps
  the other settings.
- The climate checkbox maps to the `climate_machines` field for v7 and to the id switch for v5/v6.
  `set_climate_machines` in the preview handles both cases.
- `IsErosionFamilyRecipe` includes v7. `RecipeIdentityText` gets a v7 line.
  `RingLiveTuningDrawer.SurfaceTerrain.cs` gets a `"landform v7"` label.
- Add a preview view mode 4, "Landforms": RGB = (R, Q, gate) from the new diagnostics keys.
  `TerrainGenerationSampler.SampleDiagnostics` adds `landform_height`, `lowland_erosion_height`,
  `ruggedness`, `plateau_weight` and `landform_gate`.
- Persistence follows the existing convention: **Save recipe** writes
  `user://terrain_generation_preview/recipe_v3.tres`, and **Reset recipe** now creates
  `create_landform_default()`. A running world's recipe is frozen, so the controls tune the
  preview and playable patch, and new worlds pick up the saved recipe. This is the established
  convention for recipe settings.
- The launcher's "Hill-country landforms (v7)" checkbox (§8, Step 6) follows the climate toggle
  pattern and is not persisted.

---

## 8. Ordered implementation steps

Setup for every step (PowerShell, from `c:\Projects\Ringworld`):

```
$Godot = 'C:\Users\Kamron\Downloads\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe'
dotnet build RingworldTechDemo.csproj --no-restore -v:minimal      # zero warnings/errors before any Godot run
```

The headless "ring_voxel_mesher.gdextension not found" error seen in this checkout does not affect
these CPU tests. Per the lean test cadence, run only the listed targeted tests in each step, and
the full GPU gate suite once at Step 7. Kill Godot before GPU tests.

### Step 0 — Freeze v4/v5/v6 bit-identity baselines (BEFORE any code change)

Files: new `tools/TerrainRecipeFreezeTest.cs`, `tools/terrain_recipe_freeze_test.gd`, and the
fixture `tools/fixtures/terrain_v4_v6_freeze_golden.json`.

1. A `--capture` mode writes the fixture. It covers:
   - Recipes `ErosionDefault`, `SitesDefault` and `ClimateDefault` × seeds {1337, 90210, 2147483000}.
   - SHA-256 of `SampleGrid` float32 bytes for 64×64 at 2 m and 64×64 at 512 m, at these origins:
     both sides of the around seam, plains, foothill, mountain, coast, ocean, and across 0 and width-2.
   - Float64 bytes of `SampleDiagnostics` at 64 points, `CanonicalJson`, `DefinitionHash`, and
     `RingTerrainSampler.TerrainDefinitionHash` (via `EmitterSiteTestWorlds`).
2. Normal mode compares against the fixture and never overwrites it.

Verify: `& $Godot --headless --path . --script res://tools/terrain_recipe_freeze_test.gd` passes,
and the fixture is committed. Also record the v6 benchmark baseline with
`& $Godot --headless --path . --script res://tools/terrain_generation_cpu_benchmark.gd -- --csharp-build=Debug --csharp-optimize=false`.

### Step 1 — C# recipe v7 (parameters only; the model treats v7 as v6)

Files:
- `scripts/Terrain/Generation/TerrainGenerationParameters.cs`: §4 work.
- `TerrainGenerationHeightModel.cs`: no formula change yet. Make sure the constructor and flags
  accept v7.
- `tools/EmitterSiteTestWorlds.cs`: add `Gen17LandformRecipe(seed, climate)` and `CreateGen17Landform(seed, climate=false)`.

Do not change: any v1–v6 expression, the field order of earlier segments, `RuntimeSurfaceGeneratorVersion`.

Verify:
- The freeze test passes.
- `climate_terrain_v6_test.gd`, `emitter_site_terrain_v5_test.gd` and `terrain_generation_cpu_parity_test.gd` pass.
- New `tools/LandformTerrainV7Test.cs` and `tools/landform_terrain_v7_test.gd` (section "recipe")
  check: canonical round trip, out-of-range rejection for each field and cross-constraint, flags,
  v7 hash ≠ v6 hash, v7 with `climate_machines` 0 vs 1 giving distinct hashes, and the climate
  registry present only when the flag is 1.

### Step 2 — GDScript recipe, store and preview plumbing

Files:
- `terrain_generation_recipe.gd`, `terrain_generation_preview_store.gd`.
- `terrain_generation_preview.gd`: `reset_recipe`, `set_climate_machines`, `upgrade_to_landforms`.
- `scripts/world_size_launcher.gd`: a **temporary guard** that maps a v7 preview recipe to v5/v6
  for new worlds. The id-driven dictionary drops the v7 fields. This stops worlds being frozen with
  an unfinished formula. Step 6 removes the guard.

Verify, in the landform test section "gd_recipe":
- `create_landform_default().to_normalized_dictionary()` configures the C# bridge.
- Its `CanonicalJson` equals `LandformDefault()` for the same seed.
- 500 randomized out-of-range GDScript resources sanitize to a dictionary the bridge accepts.
  This guards against C#/GDScript range drift.
- Store save/load round-trips all v7 fields.

Also re-run `terrain_generation_preview_smoke.gd`, `terrain_generation_playable_smoke.gd`,
`terrain_generation_erosion_v4_test.gd` and `climate_launcher_toggle_test.gd`.

### Step 3 — v7 height formula (A, C, E) and calibration

Files:
- `TerrainGenerationHeightModel.cs`:
  - Add the `SampleLandformNaturalDiagnostics`, `Terrace` and `CalculateHeight`/`SampleDiagnostics` dispatch.
  - Allocate the v7-only noise and release it in `Dispose`.
  - Append the new `Diagnostics` fields (with defaults) at the end.
- `TerrainGenerationSampler.cs`: diagnostics keys.
- `LandformTerrainV7Test.cs`: section "shape".

The "shape" test section asserts, on seeds 1337 and 90210 and ≥ 3000 eligible pure-lowland points:

1. Coverage: plains 0.29–0.40, full hills 0.24–0.35, plateau-any 0.25–0.40.
2. Hill-country 4 km relief: p50 200–400 m, p95 ≤ 700 m.
3. Slope at 4 m: plains p95 ≤ 10°, hills p95 ≤ 35°, hill-country fraction over 55° below 0.5 %.
4. Plateau-edge max slope along 6 km transects: p50 ≥ 25°.
5. **Zero** points with `continentalness > 0 ∧ macro > 0 ∧ height ≤ 0`.
6. C0 check: 20k random pairs 0.5 m apart with |Δh| < 3 m, and pairs straddling `gate=0` and `Q=0` edges.
7. Seam: `h(a) == h(a+C)` exactly at 256 points, and continuity across `a = 0`.
8. Determinism across two model instances and 8 parallel worker copies.
9. Outside the gate (ocean, coast buffer, full belt), v7 natural height equals v6 natural height **exactly**.

Calibration sub-task: print the percentiles of the raw R and Q unit values, and of |hn| before the
clamp. If the coverage or relief bands fail, adjust **only** the defaults or `HillNormalization`,
then record the final values in this doc.

Verify: `& $Godot --headless --path . --script res://tools/landform_terrain_v7_test.gd`, plus the
freeze test (v4–v6 must still be bit-identical).

### Step 4 — Lowland gullies (D) and performance gate

Files:
- `TerrainGenerationHeightModel.cs`:
  - Add `LowlandGullies`.
  - Extend the `gullyEnabled` allocation condition with `|| (_p.HasLowlandLandforms && _p.LowlandErosionStrengthM > 0)`.
    This is an identical boolean for v1–v6.
  - Add `_lowlandGuideEpsilon`.
- `tools/terrain_generation_cpu_benchmark.gd`: add v7 origins for plains, hill country, plateau
  edge and mountain. Choose them in Step 3 from the diagnostics, then hard-code them.

Do not change: `EnhancedCellGullyField`, `EnhancedDirectionalGullies`, `DirectionalGullies`, `CellGullyField`.

Verify:
- The landform test "shape" still passes, plus a new "gullies" section:
  - Carve ≤ 0 and ≥ −min(strength, .5·landform).
  - Plains carve is 0 where `slopeMask` is 0.
  - C0 check repeated.
- The freeze test passes.
- Benchmark gates from §6. If they fail, change the `lowland_erosion_strength_m` default to 0 in
  both C# and GDScript and document the numbers.

### Step 5 — Grave-key menu and preview diagnostics

Files:
- `RingLiveTuningDrawer.GenerationPreview.cs` (§7) and `RingLiveTuningDrawer.SurfaceTerrain.cs` (label).
- `terrain_generation_preview.gd`: view mode 4 and the upgrade button handler.
- A new `tools/terrain_generation_landform_ui_test.gd`, modelled on `terrain_generation_enhanced_erosion_ui_test.gd`.
  It uses an isolated fixture path under `res://.godot/terrain-generation-tests/` and checks:
  - The v6 fixture shows the landform sliders disabled, and the upgrade button preserves v6 settings.
  - The sliders become editable after the upgrade.
  - Explicit save, then reload, preserves the edited values.
  - The climate checkbox toggles `climate_machines` on v7.
  - The playable player settles on committed collision.

Verify: the new UI test, `terrain_generation_enhanced_erosion_ui_test.gd` and `terrain_generation_preview_smoke.gd`.

### Step 6 — Launcher adoption (new worlds get v7)

Files:
- `scripts/world_size_launcher.gd`:
  - Remove the Step 2 guard.
  - Add a `landforms_toggle` CheckBox "Hill-country landforms (v7)". Its default comes from a new
    ProjectSetting `ringworld/runtime/surface_landforms_v7`, default `true`.
  - The toggle is enabled only for new runtime-surface worlds. For existing slots it shows the
    frozen state (stored JSON contains the v7 id).
  - When ON: any v4/v5/v6/v7 preview recipe becomes v7, with `climate_machines` taken from the
    climate toggle. When OFF: the current v5/v6 logic.
  - The climate display for existing slots becomes: v6 id, or v7 id with `"climate_machines":1`.
- The relevant scene file, if the checkbox is declared there.
- `tools/climate_launcher_toggle_test.gd`: cover both toggles and the four combinations, and keep
  the v5↔v6 checks under landforms OFF.
- **Pin launcher-driven baseline harnesses to v6.** Add one line next to their existing
  `ringworld/runtime/terrain_backend` setting: `ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", false)`.
  The files are:
  - `atmosphere_visual_test.gd`, `cloud_haze_gameplay_test.gd`, `eclipse_darkness_capture.gd`, `eclipse_hub_capture.gd`
  - `emitter_bore_gameplay_test.gd`, `emitter_capture_route.gd`, `emitter_fresh_spawn_test.gd`, `emitter_site_teleport_test.gd`
  - `global_lod_hull_gating_capture.gd`, `local_mist_diagnostic_test.gd`, `night_horizon_attribution_test.gd`, `night_rendering_comparison.gd`
  - `smooth_runtime_spike_world.gd`, `terrain3d_gameplay_movement_test.gd`, `terrain_generation_world_smoke.gd`, `terrain_near_color_diagnostic.gd`
  - `terrain_render_attribution_test.gd`, `terrain_shadow_hitch_test.gd`, `terrain_shadow_lod_gameplay_test.gd`
  - `terrain3d_gameplay_travel_benchmark.gd`, which gets a `--landforms` flag instead, default off.

Verify: `climate_launcher_toggle_test.gd` and `runtime_surface_definition_test.gd`. Also run
`terrain_generation_world_smoke.gd` once with landforms ON, as a temporary local edit or flag.

### Step 7 — Integration validation and docs (once per batch)

Checks:
- `LandformTerrainV7Test` section "world": a v7 `RingTerrainSampler`, with and without climate machines.
  - All sites and machines build, and the cut/fill caps hold.
  - Every emitter viewpoint resolves.
  - Report the cold registry build time versus v6.
  - Report the v6→v7 primary-biome change fraction, which must be ≤ 20 %.
  - Independent worker copies agree.
- `& $Godot --headless --path . --script res://tools/terrain3d_gameplay_travel_benchmark.gd -- --isolate --speed=60 --distance=512 --landforms`
  must show zero coverage waits and zero support misses.
- GPU, once, on this machine only (goldens are machine-specific): run `terrain_near_lod_parity_gpu_test.gd`,
  `terrain_skirt_coverage_gpu_test.gd` and `terrain_vertical_lod_coverage_test.gd` against a v7
  hill-country pose. Record the results; there is no committed golden for v7.
- Full existing GPU gate suite with harnesses pinned to v6: every existing golden must be unchanged.

Docs: update this file's status and final defaults, `docs/terrain-generation-cpu-optimization.md`
(the v7 benchmark rows) and `docs/GENERATED_DATA.md` if fixtures are listed there.

---

## 9. Tests and goldens summary

- **Must stay bit-identical** (v1–v6):
  - `terrain_generation_cpu_parity_test` (v3 golden) and `runtime_terrain_cpu_pass2_test` (v1–v3).
  - `terrain_generation_reference_test` and `shared_math_test` (v1 reference).
  - `emitter_site_baseline_test` (v5 golden) and `climate_terrain_v6_test` (v4/v5 hash constants).
  - The `climate_machine_registry_golden` and `natural_weather_v5_fingerprint_golden` fixtures.
  - The new Step 0 freeze golden.
- **Updated**: `climate_launcher_toggle_test.gd`, and the pinned launcher harnesses (one line each).
  `terrain_generation_preview_smoke.gd` and `playable_smoke` only if they assert the reset id;
  they currently do not.
- **New**: `TerrainRecipeFreezeTest`, `LandformTerrainV7Test` (sections recipe, gd_recipe, shape,
  gullies, world), `terrain_generation_landform_ui_test`, and the v7 rows in the CPU benchmark.
- **GPU goldens**: no existing golden should change, because launcher harnesses are pinned to v6
  and C# tests use the explicit `SitesDefault()`/`ClimateDefault()` factories.
  `terrain_surface_include_regression` fixtures are synthetic and unaffected. Any v7 GPU capture
  is new and valid only on the capturing machine.

---

## 10. Risks and open questions

1. **Far-LOD aliasing and skirts.** Hill and terrace frequencies alias in coarse point-sampled LODs,
   and the near-to-proxy mismatch may exceed the 8 m skirts. Step 7 measures this. A filtered
   coarse-LOD sampler is the follow-up if needed.
2. **Perf gate on lowland gullies** may force a default of 0. The plan accepts shipping D disabled.
3. **Per-biome landforms (B)** need a v8 that moves raw climate into the recipe. Confirm deferral.
4. **Coastal plain width.** The 20→140 m macro buffer gives a coastal plain several km wide
   everywhere. Say so if occasional coastal hills or cliffs are wanted.
5. **Default for new worlds.** v7 is ON by default through the launcher toggle. Existing slots and
   pinned harnesses stay v6. Confirm that an opt-out toggle is wanted.
6. Once Step 6 ships, any formula tweak needs v8. Do not create user worlds from v7 previews before Step 6.

---

## 11. Step 6 and 7 results (2026-10-18, this machine: laptop_140v_baseline)

Launcher (Step 6):
- `landforms_toggle` "Hill-country landforms (v7)" sits next to the climate toggle. Default comes from
  `ringworld/runtime/surface_landforms_v7` (true). Enabled only for a NEW Terrain3D runtime-generation world;
  for existing slots it displays whether the stored recipe is v7; on other backends it is disabled and cleared,
  and returns to the default when runtime generation is selected again.
- ON: a v5/v6/v7 preview recipe is created as v7 and `climate_machines` follows the climate toggle. OFF: the old
  v5/v6 logic (a previewed v7 recipe is created as v5/v6). The climate display for existing slots is the v6 id, or
  the v7 id with `"climate_machines":1`.
- The 19 launcher-driven harnesses listed in Step 6 set the project setting to false next to their
  `terrain_backend` line, so they stay on v6. `terrain3d_gameplay_travel_benchmark.gd` takes `--landforms`
  (default off).

Step 7 measurements:
- `LandformTerrainV7Test` section "world" (new): 595 emitter sites and 200 climate machines build; the existing
  v5 operator-agreement and pad-flatness checks and the v6 forecourt cut/fill checks pass on the v7 sampler;
  2380 emitter viewpoints resolve; cold sampler construction v6 341 ms, v7 380 ms, v7+climate 390 ms; primary
  biome change v6 to v7 0.48 % of 20000 ring samples (0.76 % of land samples; limit 20 %); 8 independent worker copies agree.
- Travel benchmark, 60 m/s, 512 m out and back, `--isolate --landforms`: spawn, and two hill-country tiles
  (around 8011003 and 10053024): zero coverage waits, zero support misses (0/1225, 0/1268, 0/1209 ray misses),
  p99 7.7-8.0 ms (v6 reference on the same hill tile: 7.8 ms).
- Risk 1, coarse-LOD edge mismatch (`-- --section=lodgap`; vertical difference between the midpoint height and the
  linear coarse edge, p99 / max in metres, hill tiles vs v6; the skirt is 8 m):
  spacing 16 m: 1.1-1.3 / 3.2 (v6 0.2); 32 m: 2.8-3.3 / 5.9 (v6 0.5); 64 m: 6.5-7.7 / 12.1 (v6 1.3),
  0.1-0.8 % of points above 8 m; 128 m: 15-16 / 23.7 (v6 3.5), 10-18 % above 8 m; 256 m: 27-30 / 56 (v6 7.8),
  32-45 % above 8 m. Plains tile stays at or below v6-like values until 256 m (p99 9.1 m).
  Conclusion: skirts cover seams up to 32 m spacing everywhere; from 64 m up, v7 hill country can open gaps
  larger than the 8 m skirt unless geomorph hides them. Proposed mitigation (not applied): a filtered/low-pass
  coarse-LOD sampler for spacing >= 64 m, or scale the skirt depth with LOD spacing for v7.
- CPU benchmark (128x128 at 2 m, median ms): v4/v6 default plains 13.1, hills 13.4, mountain 29.8; v7 default
  plains 23.8 (1.81x v6, gate 1.9x), hills 28.7 (0.96x v6 mountain, gate 1.15x), plateau edge 30.7;
  v7 without lowland gullies plains 21.3, hills 21.7.
- GPU (windowed): `terrain_near_lod_parity_gpu_test`, `terrain_skirt_coverage_gpu_test` and
  `terrain_vertical_lod_coverage_test` pass. They use synthetic fixtures, so they are independent of the recipe id.
