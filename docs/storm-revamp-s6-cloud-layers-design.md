# Storm revamp S6: cloud vertical structure, 500 m to 18 km (lead design, 2026-10-28)

Status (2026-10-29): WO1-WO6 and WO8 built, WO7 diurnal only (front sequence tabled), WO9 and WO10 deferred; closed with the storm revamp for the merge to `main` (see `storm-revamp-plan.md` Handoff and its batch close record). The original status, kept for the line references below: design only, on `storm-revamp-wip` (e2485b7 plus the working tree of 2026-10-28, which held the in-progress wall fix at `SunshineCloudsCompute.glsl:1668-1671`).

User decisions (2026-10-28):
- The cloud range is 500 m to 18 km, layered like real life:
  - low (Cu, Sc, St) 0.5-2 km;
  - mid (Ac, As, Ns) 2-7 km;
  - high (Ci, Cs, Cc) 7-13 km;
  - only storm towers (Cb) and their anvils reach 14-18 km (tropopause about 18 km).
- Each layer has its own look and coverage. Cirrus may be a cheaper thin layer.
- Low cloud comes down to 500 m **only in weather** (overcast, fronts, storms). Fair-weather clouds keep a higher base that respects the playable skyline.
- Revamp direction (plan doc "Direction"): immersion and spectacle, exaggerated storms, travelling fronts, dark closed overcast under storms.

## 1. What the code does today (evidence)

**Background band.** All non-storm cloud lives in one band: `NaturalCloudBaseAltitudeMeters` 2000 m to `NaturalCloudTopAltitudeMeters` 6000 m (`RingSunshineCloudBridge.cs:145-146`, live values in `resources/clouds/gameplay_cloud_defaults.tres`).
- `sampleScene` normalises height over that band and reads one height-gradient row, `texture(heightmask, vec2(h, 0.5))` (`SunshineCloudsCompute.glsl:448-461, 489`).
- The same band drives the light-march gradient (`:815-827`), the shadow compute (`SunshineCloudShadowCompute.glsl:324-330`) and the far shell's mid-plane and extrusion taps (`materials/ring_cloud_layer.gdshaderinc:153-196`, `ring_far_weather_cloud.gdshaderinc:34`).
- One gradient row means one look: everything is cumulus-like, from 2 to 6 km.

**Weather sets coverage only, never altitude.** In camera-independent (CI) mode, a non-storm front gets macro coverage inside 2-6 km and nothing else.
- Natural fronts do carry a storm band of 500 m to the profile top (`RingVolumetricCloudLayer.cs:3987-3995`).
- But only sculpt systems (StormTower, Cumulonimbus) use that band (`ring_ws_build_ray_sculpt`, `SunshineCloudsCompute.glsl:695-771`).
- **So "500 m in weather" exists today only inside storm ellipses.** Nimbostratus and stratus fronts render at 2-6 km.

**The physical catalog already exists.** `CloudPhysicalCatalog.cs:55-94` lists real bases, tops and thicknesses for the 10 genera, and the system profile roll (`RingVolumetricCloudLayer.cs:4023-4087`) picks a genus per system. The renderer ignores the genus except for the Cb/StormTower sculpt.

**Storms.** The baked 2.5D profile map spans the system's storm band (`RingStormShapes.glslinc:211-216`). The CI envelope runs up to `StormEnvelopeCeilingMeters` 20 km (`CameraIndependentClouds.cs:41, 150-180`). The Cb catalog maximum is 20 km, and `ResolveSystemVerticalProfile` caps tops at 18 km except for ExceptionalFront (`RingVolumetricCloudLayer.cs:4096-4099`, `:3604-3607`). **18 km is not a hard limit today.**

**Per-ray band machinery** (`RingWeatherSystems.glslinc:192-372`). This is the part S6 has to generalise.
- The background band is a global constant (UBO `header2.xy`, `CameraIndependentClouds.cs:370-371`).
- The ray hull is that band widened by up to 3 sculpt intervals `[t0, t1] x [floor, ceiling]` (`RING_WS_RAY_BANDS 3`).
- `ring_ws_primary_possible` gives an exact empty-step test, `cloud_entry_skip_steps` a lattice-preserving pre-entry skip (`SunshineCloudsEntrySkip.glslinc`), and the march has a terminal-exit rule (`SunshineCloudsCompute.glsl:1655-1680`).

**Known defects that S6 must not inherit.**
1. **Invisible wall** (fix in progress, `:1668-1671`). The ray terminates when it leaves a storm interval below the background floor. The current fix treats "inside the hull, below or above the background band, moving toward it" as not terminal.
2. **Anvil cut.**
   - The bake's canopy reaches past the ellipse, `canopyMask = 1 - smoothstep(0.76, 1, nd * 0.8)` (`RingStormShapes.glslinc:189`). With the ragged-edge noise of up to +0.15 (`:186-187`), that is up to about 1.44x the ellipse.
   - Every emptiness test assumes zero outside ellipse + 1500 m: the ray intervals (`SunshineCloudsCompute.glsl:1280`, `intervalSlack` 1500), the sub-mask (`RingWeatherSystems.glslinc:286-312`) and the shadow band (`SunshineCloudShadowCompute.glsl:537`).
3. **Lean normalisation.** In CI mode `storm_altitude` is uploaded as zero (`RingSunshineCloudBridge.cs:1004-1008`). The profile sample's lean height `s` therefore falls back to the *background* band (`RingStormProfileSample.glslinc:42-44, 138-139`). Once the background band changes meaning, this silently changes the storm lean.

**Step sizes.** The live primary step is `MaxStepDistanceMeters` **958 m** with `MinStepDistanceMeters` 500 m (the tres overrides the code default of 560). Step counts per preset are 96 / 128 / 192 / 328 (`RingGraphicsQualityController.cs:777-783`), and `ring_cloud_max_step` raises maxstep to `handoffEnd / steps` (`SunshineCloudsHistory.glslinc:39-47`).

## 2. Physical vertical structure (target)

### 2.1 Layers

| Layer | Genera | Base | Top | Render | Look driver |
|---|---|---|---|---|---|
| Low | Cu, Sc, St (+ Ns base, scud) | fair: `FairWeatherCloudBase` (Q1, default 1500 m); weather: 500 m | Cu: base + 0.8-2.8 km (diurnal and coverage driven, capped at 5 km); Sc/St: base + 0.4-1.5 km | volumetric | stratiformity = f(coverage, weatherness): partial fair cover reads as Cu, high cover as Sc, weather as St/Ns |
| Mid | Ac, As, Ns body | 2.4 km fair (+-400 m texel noise); 2.0 km under fronts | base + 1-4 km, capped at 7 km | volumetric | partial cover reads as Ac (cellular, more erosion), high cover as As (smooth sheet) |
| High | Ci, Cs, Cc | shell at 9.5 km (layer table) | optical thickness 0.5-2 km | 2D thin shell (section 3) | partial cover reads as Ci (wind-sheared fibres), high cover as Cs (veil), with a noise-gated Cc sub-variant |
| Storm | Cb tower and anvil, deck | 500 m | towers and anvils to **18 km**; canopy top <= 16.5 km, dome <= 18 km | S4 profile map (unchanged path) | S4 |

In weather the low and mid layers overlap into one deck from 0.5 to about 6-7 km. That deck is nimbostratus, and it is how "dark closed overcast" happens outside the storm map. The S4 deck (`RingStormShapes.glslinc:229-240`, band 0.07-0.38, which is about 1.7-7.1 km at a 0.5-18 km band) stays the storm-owned body. The low layer supplies the 0.5-1.5 km scud base under it.

### 2.2 Coverage from the macro weather map

Each genus contributes to layers through a data table (genus x layer). A system's profile (`c.z` in the UBO) feeds:

| Profile | Low | Mid | High | Lowers base to 500 m |
|---|---|---|---|---|
| Cumulus | 1.0 | - | - | no |
| Stratocumulus | 1.0 | - | - | only in a front or bank |
| Stratus | 1.0 | - | - | yes |
| Nimbostratus | 1.0 | 1.0 | 0.3 | yes |
| Altocumulus | - | 1.0 | - | no |
| Altostratus | 0.2 | 1.0 | 0.5 | front only |
| Ci / Cs / Cc | - | - | 1.0 | no |
| StormTower | 0.8 (scud) | 0.6 | 0.3 | yes |
| Cumulonimbus | 0.8 | 0.6 | 0.9 downwind (anvil outflow cirrus) | yes |

- **Fair weather** (no system):
  - The cluster term sets low coverage (Cu), as it sets background coverage today.
  - Mid coverage is the cluster term at a second noise salt x 0.35 (sparse Ac).
  - High coverage is the cluster term at a third salt x 0.5 (Ci is common).
  - The base stays at `FairWeatherCloudBase`.
- **Overcast** (cluster coverage > `HeavyOvercastStartCoverage`, no system): an Sc deck at the fair base. Only a weather system lowers the base.
- **Front sequence.** Systems advect downwind (`header1.w`), and storm travel at 10-20 m/s (S4 WO-T) will make the sequence play out at the observer. Let s be the position along the downwind axis, normalised by the ellipse extent (+1 = leading edge).
  - **Warm type** (dominant profile As, Ns or Cs):
    - high ramps up from s = +2.0 to +0.8 (cirrus, then cirrostratus as coverage rises);
    - mid from s = +1.2 to +0.3 (altostratus thickening);
    - low with weatherness from s = +0.5 to the core (Ns; the base lowers to 500 m);
    - behind the core (s < -0.6), the cover breaks into Sc.
  - **Cold type** (Cb, StormTower, Sc): a narrow band, s from +0.4 to -0.4, then quick clearing with post-frontal Cu.
  - The precursor needs support ahead of the ellipse: warm-type fronts get `SupportMultiplier` 2.0. That widens the CPU selection (`CameraIndependentClouds.cs:162-163`) and the macro-map loop's support test, for the high and mid terms only.
- **Storms:** the sculpt map (unchanged) plus the table row. The S4 background threshold lift to 1.05 (`SunshineCloudsCompute.glsl:533-534`) applies to the low and mid layers alike, so overcast under a storm closes at every height.
- **Diurnal cumulus:** the fair-weather low layer only.
  - `d = f(local solar phase)` peaks about 3 h after local noon.
  - The local solar phase is analytic per texel: `dot(up(around), sun_dir)` plus the sign of its time derivative, as in `ring_cloud_layer.gdshaderinc:120-123`. The macro build therefore stays camera independent.
  - Low Cu coverage is multiplied by `0.35 + 0.65 d`. Cu depth follows coverage, so afternoons grow congestus and nights keep only Sc and St.
  - "Daytime cumulus" strength is a setting.
- **Climate and biomes:** not in v1. The table has a per-texel "climate" input slot reserved for it (Q3).

### 2.3 What the macro map stores

The macro map gains a second array layer per buffer: `(lowCov, midCov, highCov, weatherness)`, RGBA16F, built in the same slices from the same snapshot.
- Today it is `cov, storm, precip, influence` with 2 array layers (`SunshineClouds.gd:3383`); it becomes 4.
- Weatherness is computed only from systems flagged `RING_WS_FLAG_LOWERS_BASE` (new bit 16, set by the CPU from the table). That makes it conservative per ray (section 3.3).
- The shader derives the following, so the menu bases need no rebuild:
  - the low floor, `mix(FairWeatherCloudBase, WeatherCloudBase, weatherness)`;
  - per-layer tops from coverage and stratiformity;
  - stratiformity from (coverage, weatherness).
- A CPU twin `CloudLayerColumn RingCloudLayers.Evaluate(...)` mirrors the build exactly, as `GetPrecipitationPotentialAt` mirrors precipitation today.

### 2.4 The 18 km ceiling

`CloudCeilingMeters = 18000` is a constant.
- `StormEnvelopeCeilingMeters` defaults to 18000 and is clamped to it (`GameplayCloudDefaults.cs:103` range).
- In the catalog, the Cb maximum and typical maximum become 18000.
- The ExceptionalFront exemption is deleted in `RingVolumetricCloudLayer.cs:3604-3607` and `:4096-4099`.
- The bake clamps the dome to `18000 - fadeTop` and the canopy top to 16.5 km.
- S4 targets (towers 8-15 km wide and 14-18 km tall, anvils 60-120 km) then sit inside the limit. Superstorm (WO-B) and overshooting tops obey the same cap.

## 3. Rendering architecture

### 3.1 Options

| Option | Cost | Look | Verdict |
|---|---|---|---|
| A. One volumetric march, 0.5-18 km, per-layer density profiles, cirrus included | A 1-2 km cirrus sheet at 958 m steps aliases into the S4 rings and terraces (S4 root cause 3). Grazing rays spend steps in the 7-13 km band, each paying the light march and ambient sample. The shadow march band grows 4x. | volumetric cirrus (only matters when flying at 10 km) | reject for now |
| **B. Volumetric low+mid (0.5-7 km, altitude-dispatched layers), storm map unchanged to 18 km, high layer as an analytic 2D shell composited inside the march at its ray distance, with the shared shell function in the far layer** | The low+mid hull is 1.3-1.6x today's 2-6 km band for oblique rays; empty-layer samples exit after the map fetch, before any noise. The shell is one quadratic plus about 3 texture reads per pixel. | per-layer looks; a noise-free cirrus (no history grain) | **chosen** |
| C. A second Sunshine dispatch for high cloud at low resolution | one more full-screen pass, plus history and reprojection | same as B | reject (complexity, a second history) |

**Upgrade path.** The layer table carries `RenderMode {Volumetric, Shell}` per layer. A later flight WO switches High to Volumetric (a thin band at the shell altitude, using the same 2D field as its horizontal coverage) while the camera is within +-3 km of the shell.

### 3.2 Density: altitude-dispatched layers (`sampleScene`, both computes)

- A new include, `addons/SunshineClouds2/RingCloudLayers.glslinc`, shared by the main and shadow computes and the bake.
  - It holds the UBO `RingCloudLayersBlock`: the layer table (4 slots, 3 used), bases, the 18 km ceiling, storm support factor 1.45, diurnal parameters, the cirrus shell parameters. It binds at main 29 / shadow 13; verify these are free in `SunshineClouds.gd`.
  - `ring_layers_at(alt, layerTexel)` returns at most 2 active layers at that altitude (Cu tops overlap the mid base), each with `h` normalised in its band, coverage, stratiformity, a gradient row and noise modifiers.
- `sampleScene` replaces the single background band (`:448-461`) with a loop over those at most 2 layers. Per layer:
  - Coverage of 0 means the layer is skipped before `heightmask` and the large-noise fetch. Today an empty sample still pays both.
  - Height gradient: `texture(heightmask, vec2(h, row))`. `HeightGradient.tres` becomes a generated N-row texture built from the curves in the layer table: rows Cu, Sc, St/Ns, Ac, As.
  - Threshold: the existing `weatherBackgroundThreshold` formula, fed the layer's coverage instead of the frame coverage.
  - Noise: a per-layer offset on the large/medium/small positions, so the low and mid layers are uncorrelated. Stratiform layers get a horizontal stretch of x2-x4 on the large lookup and less small-scale erosion (`smallscalePower`); Ac gets more.
  - The rest of Sunshine's erosion pipeline (`:540-560`) is unchanged. Background density is the max over the layers.
- The storm path (`ring_ws_sculpt_density`, profile map) is unchanged, except that the profile sample's lean normalises over the storm envelope, not the background band (defect 3).
- A single-layer table equal to today's band reproduces HEAD exactly. That identity is the WO3a gate.

### 3.3 Per-ray bands and the generalised exit rule (supersedes the wall fix)

The admissible region of a ray at distance `t` is the union U(t) of:
- the **volumetric layer hull** `[rayFloor, 7000]`, where `rayFloor` is `WeatherCloudBase` if any system in `g_ws_mask` carries `LOWERS_BASE` (the ray mask's 2*maxstep + 500 m margin covers the 1 km bilinear bleed), and `FairWeatherCloudBase` otherwise;
- every sculpt interval `[t0, t1] x [floor, ceiling]`, computed with the **true storm support**: the ellipse times `RING_STORM_SUPPORT` 1.45, plus slack. That one constant is shared by `ring_ws_build_ray_sculpt`, `ring_ws_update_sub_mask`, the shadow `ring_ws_band` and the bake, which fixes defect 2.

The high shell is not a band. It is an event at a known `t` (section 3.4).

**Rules**, replacing the else-branch at `:1655-1680` and the entry-skip block at `:1355-1384`:
1. **Inside U(t):** sample as today.
2. **Gap step** (inside `hull(t)`, the hull of everything still reachable from `t`, but outside U(t)): empty. This covers the gap below the fair base when a storm interval is ahead or behind, the gap beside a storm interval, and a future gap between layers in the table. A gap step is never terminal.
   - **Gap skip:** jump to the next boundary of U along the ray using the same curved-cylinder quadratic as `cloud_entry_skip_steps`, applied to the nearest altitude boundary in the direction of motion and to the next interval start `t0`.
   - It skips only whole maxstep lattice intervals, as the entry skip does. That preserves the jitter lattice the temporal history depends on.
3. **Terminal** only when the ray is outside `hull(t)` and moving away from it (above the ceiling and climbing, or below the floor and descending), and no sculpt interval starts beyond `t` (`ring_ws_band_ahead`). Or at the handoff end, at depth, or at density 1 (unchanged).
4. **Apex reversal** (`:1435-1450`): unchanged. A camera-outside ray that starts descending after entering stops. This is the remote-shell guard.

The in-progress wall fix (`ciGapToBand`) is the special case of rule 2 in which the hull equals the background band widened by storm intervals. S6 replaces it with the general predicate `ring_ws_gap_step(t, alt)` and keeps the wall-fix captures as regression shots.

**Light march** (`sampleLighting`, `:815-866`).
- The break band is the layer hull widened by the sub-mask bands, as today.
- The height gradient uses the sample's own layer band, not the background band.
- **Inter-layer sun shadowing (new):** the light march reaches only about 2 km (`LightingTravelKm` 0.5), so a low Cu under an As deck would otherwise be sunlit.
  - Add a closed-form `tau_above = sum over layers above the sample of (k_layer * cov_layer * thickness_layer / sun_up)`, plus the shell's `k_ci * highCov`, read from the primary's layer texel. No new fetch.
  - It is added like `stormTau` (`:798-803, 865`).
  - Ambient for that share is darkened the way the S4 per-ray storm ambient is (`:1488-1492`).

**Thin-layer anti-terracing.** St (0.4-1 km) and Cs-like sheets are thinner than a 958 m step.
- Each in-layer sample is weighted by the fraction of its step interval whose altitude range overlaps the layer band. This uses the ray's analytic altitude rate, the same quantity as `analyticAltitudeMotion` at `:1440-1443`, and is the same idea as `terminalDensityWeight` at `:1408`.
- Layer vertical fades are kept at least 0.5 x maxstep x the typical vertical rate.

### 3.4 High layer: analytic thin shell

- Shared include: `materials/ring_cirrus_shell.gdshaderinc`, plain GLSL usable by the `.gdshader` far layer and the compute.
- `ring_cirrus_tau(canonical_xz, highCov, stratiformity, time)`:
  - Ci fibres: medium noise sampled anisotropically along the high-level wind (1:8), curl-displaced.
  - Cs veil: extra-large noise.
  - Cc: small-noise cells above a threshold.
  - No new textures.
- Lighting is single scattering: HG with g = 0.7 (ice, strong forward lobe), plus sky ambient (`CloudAmbientFromSky`). There is no self-shadow. The storm-light D term darkens it.
- **In the march:** before the loop, intersect the ray with the shell altitude (the same quadratic; up to 2 roots for a camera above it). Inside the loop, the first step whose `traveledDistance` passes `t_shell` composites the shell with the current transmittance. Rays that end early composite at the end if `t_shell` is less than or equal to the handoff end and the depth.
  - The ordering is exact: towers and anvils above 9.5 km in front of the shell occlude it, and low cloud occludes it.
  - It enters `ring_cloud_depth_accumulate` with its alpha, so reprojection and haze distance stay consistent.
  - It is noise-free, so it adds no history grain. Interleave fill reconstructs it like any other contribution.
- **Haze** is applied once with the blended depth (`:2046-2050`). Cirrus seen through gaps in a near low deck gets a slightly short haze distance; haze is negligible at 10 km altitude, and `haze_consistency` confirms it.

### 3.5 Far viewport parity (beyond the handoff)

- **Layer split:** the far weather atlas keeps `(cov, storm)` (`RingScaledFarViewport.cs:829-848`). The far shell derives the layer coverages with the **same function** `ring_layers_from_weather(cov, storm, weatherness = storm proxy, diurnal)`, without the front phase, which a 245 km atlas texel cannot resolve anyway.
- **Volumetric layers:** the existing two extrusion taps (`ring_cloud_layer.gdshaderinc:180-195`) become the low-layer and mid-layer taps at their band middles, and `far_density_background_altitude` becomes the low+mid hull.
- **Cirrus:** the far shell evaluates `ring_cirrus_tau` at the shell altitude using the same parallax offset it already computes (`per_meter * (H_ci - mid)`, `:172-177`). No new mesh.
  - The handoff weight is evaluated at the cirrus intersection distance, not at the fragment distance, so the near/far boundary does not double up or leave a gap.
  - If the parallax reads wrong at the handoff, the fallback is a second shell mesh with a `RING_CLOUD_LAYER_CIRRUS` variant.
- **Far shadow** (`ring_far_weather_shadow.gdshaderinc:78-89`): its band becomes the low+mid hull, plus `highCov * k_ci_shadow`.

### 3.6 Shadows

- **Near and far GPU shadow** (`SunshineCloudShadowCompute.glsl`):
  - The band at `:516-539` becomes the layer hull (`rayFloor` from the mask) unioned with the storm bands (true support), and `sample_sunshine_density` uses the same layer dispatch.
  - Cirrus adds one closed-form term: `highCov` at the sun-ray/shell intersection times `CirrusShadowWeight` 0.12. Cirrus shadows are weak.
  - The adaptive march grows from about 4 km / sun_up to about 5.5 km / sun_up of path. The clear-air probes (`:545-581`) already trim it.
- **Terrain cloud-sun visibility:** the registered range (`RingSunshineCloudBridge.TerrainHistory.cs:73-75`) becomes `[WeatherCloudBase, 18000]`.

### 3.7 Camera altitude

- **Walking now.** Peaks reach about 5 km (`shadow-revamp-plan.md`: 4949 m at a fixture), so the camera can stand inside the low or mid layer.
  - `cameraStartsInsideCloudAltitudeBand` (`:1324-1325`) uses the hull and works unchanged.
  - The in-cloud look is grey saturation within 1-2 steps; an optional fog hand-off is in WO9.
- **Flight later.**
  - Above 7 km, rays enter the hull from above.
  - The shell ordering is by `t`, so a camera above 9.5 km composites the shell first.
  - The shell-to-volumetric switch is the table's RenderMode.
  - `QueryCloudLayerColumn(position)` (WO9) is the flight and precipitation data path.

### 3.8 Deck ownership: S4 storm sculpt vs S6 layers (user decision 2026-10-29)

**Decision.** Only real storms are S4 sculpt bodies. Every other genus renders through the S6 layers, via its genus row (section 2.2).
- **Storm (S4 look unchanged):** physical profiles StormTower (2) and Cumulonimbus (3), natural or manual. They own their towers, anvils, the closed deck under the storm (`RingStormShapes.glslinc` deck block) and the storm look of the background around them: the 1.05 threshold lift and the per-ray storm ambient share.
- **Layers:** everything else, for example Nimbostratus (10), Altostratus (7), Stratus (9) and Stratocumulus (1). They contribute only through the map's layer coverage and weatherness. Plain heavy cluster overcast (coverage above `HeavyOvercastStartCoverage`, no system) keeps its pre-S6 lift and storm share, scaled down by the map's system influence. Natural systems already were layer-only; the manual path (preview, captures, tests) flagged every profile as sculpt, which is why the capture's Ns/As decks were S4 bodies.

**Where.**
- **Sculpt flag:** `RingVolumetricCloudLayer.IsStormSculptProfile` (`RingVolumetricCloudLayer.cs:3978`), used by the manual path (`:3962`) and `BuildNaturalRenderSystem` (`:4005`).
- **Storm ownership:** `g_ws_storm_owned` (`RingWeatherSystems.glslinc:242`).
  - It is 1 within 1.3 ellipse radii of a masked sculpt system and fades to 0 at `RING_STORM_SUPPORT` (1.45). A natural storm's own storm level reaches 1.0, a manual one's 1.3, so inside its own footprint a storm keeps every term exactly.
  - It is computed per primary inside the existing ellipse loop of `ring_ws_update_sub_mask` (`:350, :366`), and per probe or shadow sample by `ring_ws_storm_owned_at` (`:370`).
- **Owned storm level:** `ring_ws_eval_owned_storm_level` (`:251`) = max(storm level x ownership, heavy-overcast term x (1 - map system influence)).
  - With the layers on, the main compute evaluates it once per primary, right after the macro update, and writes it into `g_ws_storm` (`SunshineCloudsCompute.glsl:1615`; probes `:1465`). The primary's light and ambient samples then read it through `ring_ws_storm_level()`, so there is no extra per-ray state.
  - That feeds the layer lift (`:499`) and the storm ambient share (`:1624`). The shadow twin evaluates it per sample (`SunshineCloudShadowCompute.glsl:481-482`).
- **Non-storm decks close.**
  - `ring_layer_closure` (`RingCloudLayerSample.glslinc:102`) = max(weatherness ramp 0.25-0.85, near-full stratiform sheet: coverage 0.75-0.98 x stratiformity 0.5-0.9) x (1 - ownership) x (1 - smoothstep(0.35, 0.80, owned storm level)).
  - It is computed once per primary (`ring_layers_set_closure`, `:111`) and carried in the not-yet-evaluated patch cache as -1 - closure, then packed with the patch factor (`:218`).
  - It floors the patch factor and floors the threshold at 0.97 x closure x coverage (`:224`, `RING_LAYER_WEATHER_CLOSE`). 0.97 sits just below the storm lift, so the gradient's bottom ramp against the large noise still undulates the base.
  - Shadow twin: `SunshineCloudShadowCompute.glsl:353`.
  - Result: Ns and thick As sheets are closed with no blue holes. Thin or partial As and a bank's edges keep gaps (the "watery sun").
- **Deck lighting.** A closed deck seen from below goes opaque within one or two primaries and gets no direct sun, so its colour is the per-ray ambient term. The old ambient occlusion density (`* densityMultiplier * lightingdensityMultiplier`, about 10x) saturated to 1 and painted the deck one flat colour.
  - **Which samples count:** `g_layer_deck` (`SunshineCloudsCompute.glsl:1628`) = stratiformity ramp 0.45-0.85 (`ring_layers_deck`, `RingCloudLayerSample.glslinc:116`) x (1 - ownership) x (1 - smoothstep(0.35, 0.80, owned storm level)), and 0 for storm cloud. Fair cumulus and storms are unchanged.
  - **Unsaturated ambient occlusion:** deck samples take the raw density one minstep above (`:1768`, `RING_LAYER_DECK_AO_SCALE` 1.0).
  - **Patches:** `ring_layers_deck_patch` (`RingCloudLayerSample.glslinc:140`) gives +-45%, brighter under thinner cloud just above.
  - **Column light:** `ring_layers_column_light` (`:126`) is the closed-form vertical transmittance of the layer cloud above, floored at `RING_LAYER_DECK_DARK_FLOOR` 0.35. Thick Ns is darker than thin As.
  - Patch and column light are folded into `g_layer_deck` after the AO sample (`SunshineCloudsCompute.glsl:1771`), averaged per ray with the layer-shade weights (`:1796`), and applied after the ambient floor (`:1931`, clamp 0..2).
  - **Deck colour:** the near ambient chroma follows the overhead cloud fraction (`RING_LAYER_DECK_COLOUR`, debug mask bit 256 turns it off). The far sky mirror is in progress by another agent.
  - **Own-deck sun shadow:** `ring_layers_sun_above` (`RingCloudLayerSample.glslinc:175`) also counts the rest of a deck sample's own layer above the light march's reach (about `LightingTravelKm`, 0.5 km). Without it the base of a 5 km Ns saw the sun through a bright glow.
- **Storm auto exposure:** it lifts a non-storm Ns from about 68 to about 124 luma. The coordinator decided (2026-10-29) to keep that: dark closed overcast is the under-storm look, where the storm eye cap applies.
- **Natural non-storm systems change too.** A natural Cu/Sc/Ns system whose storm level (0.65 influence + 0.55 precipitation) was high used to get the storm lift and paint (a smooth closed slab). It now renders as layer cloud with layer lighting. This is visible in the `storm_anvil_capture` perf pose's surroundings and costs about +0.3 ms there (section 5 note below).
- **Edge:** ownership is computed from the ray's sculpt mask. Two cases clear that mask before the march (per-ray setup): sculpt strength <= 0.001 (`header1.y`, storms have no sculpt body at all) and debug mask bit 2 (no sculpt systems). In both, ownership is 0, so storms also lose the 1.05 lift and the storm ambient share and render as plain layer cloud. This only affects "storms off" or debug states.
- **CPU twin:** this section adds nothing to `RingCloudLayers.EvaluateMap`: ownership, closure and deck lighting are per-sample terms, not map terms, so the column test and fingerprint goldens do not move. (WO8 later added the low/mid/high amount biases and the daytime cumulus factor to `EvaluateMap`; at their defaults, bias 0 and factor 1, its output is bit-identical to before.)
- **Perf (RTX 4070, 1600x900 High, GPU.Clouds ms):**
  - Overcast: about 1.55-1.60 -> 1.57-1.69, under the gate of 1.84.
  - Fair: matched windows 1.69/1.71/1.87 -> 1.97 (+0.1 to +0.28). The images are identical, so this is shader overhead at the register-pressure cliff.
  - Storm 40 km: 3.23 -> 3.53. The same pose with the storm moved off rises 3.01 -> 3.30, so the storm path delta is +0.01. The +0.3 is the natural non-storm decks above.

## 4. Terrain and skyline

**Decided (Q2):** when weather brings the base to 500 m, cloud meeting hills and mountains is acceptable and physical (hill fog, cloud on slopes).
- Terrain clips it exactly: the terminal-interval depth fix is GPU-tested in `cloud_mountain_depth_gpu_test.gd`, including 500 m-band cases.
- The mountain-exclusion map (`cloud-terrain-awareness-plan.md` section 2) stays deferred. Nothing here blocks it; it would multiply into the per-layer coverage.

**Fair weather** keeps a skyline-safe base. `FairWeatherCloudBase` replaces `NaturalCloudBaseAltitudeMeters`.
- Physically, the fair-weather Cu base (the LCL) is about 1-1.5 km in the morning, rising to 1.5-2.5 km by afternoon.
- **Decided (Q1):** a fixed 2000 m, as today. The 1500 m diurnal rise is not used.

**High plateaus.** The base is absolute, against `FixedCloudAltitudeDatumMeters`, so a plateau above 500 m sits inside the weather deck during storms. That is the acceptable hill fog of Q2. A terrain-relative floor needs a terrain-max field and is out of scope.

## 5. Perf budget and gates

**Cost model.**
- The worst case is bounded by the step count, which does not change.
- Deltas come from:
  - in-band fraction: the low+mid hull is 1.3x (fair) to 1.6x (weather) today's band for oblique rays. Vertical columns take about 7 steps at 958 m; a 10-degree ray through 0.5-7 km takes about 39 steps, against 24 today.
  - visible-content cost: more lit cloud. The S4 deck showed +0.29 ms of this kind.
  - the second macro-map fetch per primary sample: S4 measured +0.2-0.35 ms ungated for one extra fetch. Here it is gated to in-hull samples, and it replaces today's large-noise fetch on empty-layer samples.
- **Fallback if over budget:** fold the per-sample `ring_cloud_weather_resolved_coverage` patchiness (`ring_cloud_weather_shared.gdshaderinc:47-60`) into the layer map, which saves work elsewhere in each sample.

**Baseline.** Measure after the wall fix lands; the S4 "-0.9 ms under a storm" was partly early ray exits.

**Measurement setup.**
- Machine and settings: RTX 4070, 1600x900 High.
- Pose and clocks: day pose, clocks paused, cloud settings applied (memory `perf-capture-gotchas`; `perf-capture` skill).
- Metric: `GPU.Clouds + CloudShadowNear + CloudMacroMap + CloudStormMap`, median of windows 2-6, at least 3 A-B-A rounds.

| Scenario | Gate vs post-wall-fix baseline |
|---|---|
| Fair noon (Cu + Ci), ground camera | <= +0.25 ms |
| Overcast / warm-front core (0.5 km base, As, Cs) | <= +0.35 ms |
| Storm, 15 km (under) / 40 km (outside) | <= +0.15 ms each (S4 gate) |
| Cirrus shell ablation (on vs off) | <= 0.08 ms |
| Macro-map layer-1 build | <= 0.03 ms amortised |
| Shadow compute alone | <= +0.05 ms |
| A1000 laptop, High | <= +0.4 ms per scenario. Its own baseline: GPU goldens and ms are machine-specific; never compare across machines. |

**Goldens and tests that must change on purpose.** Each is regenerated in its own commit, with the reason.

| Test | Why it changes | When |
|---|---|---|
| `NaturalWeatherFingerprintTest` (`tools/fixtures/natural_weather_v5_fingerprint_golden.json`) | Cb top cap (ExceptionalFront); `CloudBase/TopAltitudeMeters` become the layer column. The test refuses to overwrite: delete the golden deliberately. Coordinate with S4 WO-T, which also regenerates it: one regeneration if they land close together. | WO1, WO9 |
| `storm_profile_map_gpu_test` | heights <= 18000; band check | WO1 |
| `cloud_density_fastpath_gpu_test` + `tools/fixtures/cloud_density_reference.glslinc` | `sampleScene` layer dispatch; the stubs at `:109-112` need the `ring_layers_*` stubs | WO3b |
| `far_cloud_density_parity_gpu_test` | shared layer split and cirrus include; new parity cases | WO6 |
| `sunshine_shader_compile_gpu_test` | new UBO, new include, every Sunshine compute | every shader WO |
| `cloud_empty_mask_gpu_test` | extend with gap and interval cases (rule 2) | WO3a |
| `cloud_mountain_depth_gpu_test` | re-run (500 m band) | WO3b |
| `sunshine_camera_independence_test` | envelope 18 km, band semantics | WO1, WO3a |
| `haze_consistency_gpu_test --model=physical` (20) | cloud pixels change | WO3b, WO5 |
| Shadow gates: `shadow_compose_identity_gpu_test`, `cloud_shadow_crossfade_test`, `ring_cloud_shadow_receiver_cache_smoke_test` | shadow band change | WO4 |
| Cloud history: `cloud_gameplay_history_gpu_test`, `cloud_history_motion_gpu_test` | gap skip and shell | WO3a, WO5 |
| `storm_light_gpu_test`, `storm_anvil_capture` perf | storm path regression | WO3a, WO10 |
| `gameplay_cloud_tuning_smoke_test`, settings smokes | new and renamed keys | WO8 |

## 6. Work orders

**Every Sunshine `.glsl` edit** needs the reimport procedure in the S4 doc (`storm-revamp-s4-sculpt-v2-design.md`, "Work orders": delete the md5, touch, run a windowed `--import`, and check that the `.res` timestamps are fresh; retry on a -1073741819 crash).

**Coordination.**
- One owner at a time for `SunshineCloudsCompute.glsl` and `SunshineCloudShadowCompute.glsl`.
- WO3a starts only after the wall fix lands.
- Captures go under `.godot/captures/cloud_layers/` with one reused APPDATA profile (`appdata_cloud_layers`, `-Cache reuse`).
- Run `tools/prune_test_output.ps1 -Apply` at the end of each WO.

### WO1: 18 km ceiling, layer table and the CPU column (no visual change except the cap)
- **Files:**
  - new `scripts/clouds/CloudLayerTable.cs` (Resource: per-layer base/top ranges, render mode, gradient-row curves, noise modifiers, ambient/phase, shadow weight; the genus x layer matrix of 2.2), plus `resources/clouds/cloud_layer_table.tres`;
  - new `scripts/clouds/RingCloudLayers.cs` (pure `Evaluate`, `CloudLayerColumn`);
  - `CloudPhysicalCatalog.cs:91-94`;
  - `RingVolumetricCloudLayer.cs:3604-3607, 4096-4099`;
  - `CameraIndependentClouds.cs:41`;
  - `GameplayCloudDefaults.cs:103`;
  - the bake clamps in `RingStormShapes.glslinc` (dome, canopy top).
- **Required:**
  - a hard 18000 m cap everywhere;
  - the CPU column mirrors section 2.2 exactly (front phase, diurnal, weatherness, the LOWERS_BASE flag);
  - the flag is set in `BuildNaturalRenderSystem` and the manual path (`:3955-3966`, `RingVolumetricCloudLayer.cs`).
- **Do not:** touch the march, the sampleScene path or the macro map.
- **Validation:**
  - new headless `CloudLayerColumnTest`: a golden over fair, overcast, a warm-front s-sweep, a cold front, a storm and a diurnal sweep. It asserts that the base is below 1 km only where weatherness > 0, that every top is <= 18000, and that the layers are ordered.
  - `StormLifecycleCodeTest`;
  - fingerprint regenerated deliberately;
  - `storm_profile_map_gpu_test` updated.
- **Escalate if:** the fingerprint changes anywhere beyond Cb tops and CloudBase/Top.

### WO2: macro-map layer 1 and the layers UBO (built, not yet read)
- **Files:**
  - `RingMacroWeatherMap.glsl` (second output layer);
  - `SunshineClouds.gd:3383` (4 array layers), uniform sets and the new UBO binding;
  - new `RingCloudLayers.glslinc`;
  - the bridge publishes the UBO next to `PublishMacroWeatherMap` (`CameraIndependentClouds.cs:408`).
- **Validation:**
  - GPU readback vs the CPU twin at 64 points within 1e-3;
  - the image is unchanged: `compare` SSIM 1.0 on the fair and storm shots;
  - `GPU.CloudMacroMap` +<= 0.03 ms;
  - `sunshine_shader_compile`.

### WO3a: generalised per-ray bands, exit rule, gap skip and true storm support (identity with a single layer)
- **Files:** `SunshineCloudsCompute.glsl:1255-1290, 1355-1384, 1415-1425, 1655-1680`; `RingWeatherSystems.glslinc:286-372` (new `ring_ws_gap_step`, `ring_ws_next_boundary`); `SunshineCloudsEntrySkip.glslinc` (generalised to a boundary-list skip); `RING_STORM_SUPPORT` in `ring_ws_build_ray_sculpt`, the sub-mask and the shadow `ring_ws_band`; lean normalisation in `RingStormProfileSample.glslinc:42-44, 138-139`.
- **Required:** rules 1-4 of section 3.3. The layer table is loaded with one layer equal to today's 2-6 km band.
- **Validation:**
  - identity against post-wall-fix HEAD outside storms (`compare` heatmap; SSIM within the 0.997 cloud noise);
  - wall shots: under a storm, looking out at yaw 0/90/180/270 and pitch 5/15, clouds beyond the edge are present (sheet vs storm-off);
  - anvil edge: plan and side30 from `storm_anvil_capture`, with no straight cut at about 1.25x the ellipse;
  - `cloud_empty_mask` extended;
  - `flicker` on a 10 s orbit;
  - perf at 15 and 40 km.
- **Escalate if:** gap skip breaks lattice stability (history grain in `cloud_gameplay_history`) or rule 3 admits the remote shell.

### WO3b: volumetric low and mid layers in `sampleScene` and the light march
- **Files:** `sampleScene` (`:421-569`), `sampleLighting` (`:815-866`), the generated N-row height gradient (`SunshineClouds.gd:1666-1667` loader), the per-layer noise offsets, thin-layer overlap weighting, the inter-layer `tau_above` and ambient term.
- **Validation:**
  - new `tools/cloud_layers_gpu_test.gd` using SamplePoints probes (`:1332-1350`) in vertical columns at fixed poses:
    - fair: zero density below `FairWeatherCloudBase - 50` and above 7 km outside storm intervals;
    - weather: density allowed down to 500 m;
    - storm: zero above 18000;
    - layer ordering.
  - a new capture harness `tools/cloud_layers_capture.gd/.ps1` with poses: fair zenith / 30 / 10 degrees / horizon; afternoon Cu; Sc overcast; St at a 500 m base from a 300 m camera; As over Cu (shaded Cu); plan and up views for terraces at 1:1. Look review on sheets.
  - perf gates (fair, overcast);
  - fastpath fixture and parity stubs updated;
  - `cloud_mountain_depth`, `haze_consistency`.

### WO4: shadows
- **Files:** `SunshineCloudShadowCompute.glsl:316-409, 516-600`; the far shadow band and cirrus term; `TerrainHistory.cs:73-75`.
- **Validation:** the shadow gates listed in section 5; a ground shot under Cu with cirrus (cirrus shadow <= 15% darkening); `CloudShadowNear` <= +0.05 ms.

### WO5: cirrus shell
- **Files:** new `materials/ring_cirrus_shell.gdshaderinc`; march insertion and depth accumulation in `SunshineCloudsCompute.glsl`; table parameters.
- **Validation:**
  - shots: fair Ci with a low sun (forward scatter), Cs veil, Cc;
  - a tower occluding the shell (a storm at 40 km);
  - camera above the shell (a debug pose at 11 km);
  - `flicker` (no grain or pops);
  - history tests;
  - ablation <= 0.08 ms.
- **Status (2026-10-29, uncommitted): built.**
  - `materials/ring_cirrus_shell.gdshaderinc` (plain GLSL; the includer supplies `RING_CIRRUS_TEX2D(uv, lod)` / `RING_CIRRUS_TEX2D_SIZE`):
    - `ring_cirrus_shell_distance`: the first crossing of the shell altitude (the entry-skip quadratic).
    - `ring_cirrus_density`: Ci streaks from two mip-filtered octaves of the extra-large 2D noise (1:8 along the weather wind, the second rotated 0.21 rad), bent laterally by the 90 km veil noise; Cs veil = coverage x the veil noise; Cc = analytic ripple cells at mid coverage. Three texture reads, all with an explicit LOD from the pixel footprint (the Sunshine linear sampler now has a linear mip filter; every other read through it is LOD 0).
    - `ring_cirrus_alpha` (vertical tau = density x extinction x thickness x 3, over |mu| floored at 0.1), `ring_cirrus_phase` (HG g 0.7 blended 50% with an isotropic multiple-scattering floor, plus the 22 degree halo on stratiform sheets).
  - March (`SunshineCloudsCompute.glsl`): one loop-carried float. `ciShellT` holds the crossing distance; the first step past it stores `-1 - density` (the opacity in front of the shell). After the loop the shell is evaluated once (high coverage from the map's layer texel at the crossing, `ring_cloud_layer_resolve(2, ...)`), weighted by the handoff near weight at the crossing, composited by `alpha x (1 - opacity in front)`, and added to `ring_cloud_depth_accumulate`. Rays that end before the shell composite it with their final opacity.
  - Lighting: sky ambient (no AO, no storm/deck terms) x AmbientScale + pow(sun, 2.2) x phase x 0.5. Not darkened by the storm-light D term: under a storm the shell is behind the storm's own opacity; the D rule change (storm_light2) handles the world side.
  - Storms own their anvils (3.8): the shell's coverage is multiplied by 1 - storm ownership at the crossing (`ring_ws_storm_owned_at` over the ray's sculpt mask), and so are the inter-layer and ground terms. Without it the Cb genus row (High 0.9) drew a bright Cs shield in front of distant towers.
  - Inter-layer: `ring_layers_sun_above` starts from 1 - ShadowWeight x high coverage (the primary's texel, `g_layer_high_cov`) for samples below the shell. Shadow compute: the same closed form at the sun ray's shell crossing, folded into R (both the early clear-air store and the final store).
  - Terrain registered range is not extended: the shell does not read terrain sun visibility.
  - Debug: `CameraIndependentDebugMask` bit 128 = shell off (range 0..511; bit 256 = deck colour off, 3.8). A SamplePoints probe exactly at the shell altitude reads the shell's vertical opacity (`cloud_layers_gpu_test` cirrus case).
  - **For WO6:** the near shell fades out with the handoff near weight, so beyond the handoff (about 48-56 km, i.e. below about 10 degrees across the ring and never along it) there is no cirrus until the far layer evaluates the same include at the shell crossing with the complementary weight. The far atlas has no high channel. The far shadow needs the same `highCov x ShadowWeight` term. The horizon line under the As deck (part 1 of this task) is the edge-on low layer cut at the handoff: also WO6.

### WO6: far viewport parity
- **Files:** `materials/ring_cloud_layer.gdshaderinc:153-196`, `ring_far_weather_cloud.gdshaderinc`, `ring_far_cloud_density.gdshaderinc`, `RingScaledFarViewport.cs` (band uniforms).
- **Validation:**
  - `far_cloud_density_parity` with new layer and cirrus cases;
  - `compare` across the handoff overlap band: SSIM within noise, and no seam on the heatmap;
  - `flicker` on a 60 s flight from 0 to 250 km;
  - `far_cloud_detail`, `far_cloud_patch`.
- **Status (2026-10-29, uncommitted): built.**
  - **What draws beyond the handoff (evidence, `cloud_layers_capture --far-off`):** sky pixels past the Sunshine handoff come from the far *clipmap* (`ring_far_cloud_clipmap.gdshader`, three legacy-band slices), not `ring_cloud_layer.gdshaderinc` (that is the main-view layer over main ground and the whole-ring shell past about 191 km). The far weather atlas is one sample per 245 km around the ring, too coarse for a 60-150 km system.
  - **Far layer sky** (`materials/ring_far_cloud_layer_sky.gdshader`, node `FarCloudLayerSky` in the far viewport, `RingScaledFarViewport.CloudLayers.cs`):
    - It is a camera-centred sphere. Each pixel integrates its view ray analytically over the curved layer hull (0.5-7 km) from the handoff start to the far terrain (depth texture), or to the shell crossfade (0.78-1.0 of the clipmap radius).
    - The weight is the exact complement of the near handoff weight (`smoothstep(start, end, t)`, opened by the Sunshine altitude crossfade).
    - The clipmap keeps only its lightning glow while the sky is on (`far_layers_enabled`).
  - **Same data as the near:**
    - `materials/ring_far_cloud_layers.gdshaderinc` reads the Sunshine macro map itself (a `Texture2DArrayRD` on its RID, front layer and front + 2), the layer table block, the layer gradient and the far density noise.
    - Storm ownership comes from the uploaded sculpt systems, up to 8, with the `ring_ws_storm_owned_at` falloff. The owned storm level drives the lift and closure exactly as in `ring_ws_eval_owned_storm_level`.
    - Beyond the map, a whole-ring layer atlas (RGBA8 low, mid, high x (1 - ownership), weatherness) is built with the far weather atlas from `RingCloudLayers.EvaluateMap` plus a CPU ownership twin.
    - `far_layer_density` mirrors `ring_layer_density` without curl, effectors or the thin-layer overlap. The march uses 3-16 stratified samples per ray, with Sunshine's per-step additive opacity `pow(d x density, sharpness) x dt / maxstep`.
    - Lighting: sky ambient with deck column light or cumulus height AO, and the sun through the layers above with the cirrus shadow (closed form, `far_layers_sun_transmittance`). Haze is sampled at the opacity-weighted depth.
  - **Cirrus:** the same `ring_cirrus_shell.gdshaderinc` is evaluated at the shell crossing by ray order, with the complementary near weight. The whole-ring shell has no cirrus, so the sky keeps the shell out to the far terrain past the disk.
    - The include's second-octave `RING_CIRRUS_TEX2D` call is now on one line. Godot's gdshader preprocessor rejects macro arguments that span lines ("Unknown character '#'"). The compute result is unchanged.
  - **Far shadow and shell:**
    - The bake's R follows the low + mid layer coverages `1 - (1 - low)(1 - mid)`, and its B holds the high coverage.
    - `ring_far_weather_shadow.gdshaderinc` adds the cirrus term `1 - (1 - band) x (1 - 0.12 x high)` at the sun ray's 9.5 km crossing.
    - The shell and the main-view layer take the same layer-union coverage.
    - The band thickness stays the calibrated 2-6 km normalisation (`RING_FAR_WEATHER_SHADOW_BAND_FILL`).
  - **Far camera:** with physics interpolation on (gameplay default), the far camera rendered 12-19 km off its target for one frame after a teleport (`cloud_layers_capture interp`), because it is moved in `_Process` but was interpolated. Now `FarRingViewport` and `FarRingCamera` are interpolation Off and follow `GetGlobalTransformInterpolated()` of the source camera. After the fix it is 0 m on every frame.
  - **Parity:** `far_cloud_density_parity` has a layer pass: `FAR_CLOUD_LAYERS_PARITY=PASS`, 2304 cases, max delta 8e-6, resolve exact for k = 0..2. The legacy pass is unchanged.
  - **Seam captures (`cloud_layers_capture handoff`, low site, near-only reference = handoff 176-183 km):**
    - Overcast and As deck are closer to the reference than the pre-WO6 far: masked MAE overcast 0.1-1.4 vs 2.7-6.7, As deck 1.0-2.2 vs 2.8-3.1.
    - Fair is about level: 3.0-4.6 vs 1.6-4.2, mostly temporal noise, with a slightly brighter cumulus band at the horizon.
    - The WO5 "dark line" did not reproduce in the current tree at any handoff, with or without the sky.
  - **Perf (RTX 4070, 1600x900 High, ground pitch 3, far viewport GPU median, A-B-A-B):** fair 1.208-1.217 vs 1.159-1.178 (+0.05 ms), overcast 1.223-1.229 vs 1.175-1.182 (+0.05 ms). GPU.Clouds is unchanged.
  - **Debug:** environment `RING_FAR_LAYERS_OFF=1` restores the pre-WO6 far; `cloud_layers_capture --far-off=layers` does the same per shot.
  - **Fine far layer atlas (2026-10-29, uncommitted, `RingScaledFarViewport.CloudLayers.cs`, uniforms `far_fine_*` in `ring_far_cloud_layers.gdshaderinc`):**
    - A camera-centred 128 x 64 RGBA8 window (low, mid, high x (1 - ownership), weatherness), CPU-built from `RingCloudLayers.EvaluateMap` plus the ownership twin. It spans the ring's whole width across (about 5 km) and about +-1.3 x the clipmap reach around (texel clamped to 4-8 km). Blend: macro map, then fine atlas (10-texel fade at its around edges), then the whole-ring atlas.
    - Build: column-major, one system collection per column (a superset: radius W/2 + 1 texel from the column's centre line), 0.5 ms/frame budget, published whole. Rebuilds every 30 s of sim time (8 s until batch close, which kept it building every frame: a build is about 870 frames), on a manual-weather revision change, on a time discontinuity, or when the camera drifts 60 km from the window centre. The grid is snapped to whole texels and sampled at texel centres, so overlapping texels of consecutive windows are identical for the same snapshot (no recentre pop). Content is advected by wind x age.
    - The column superset differs from the per-texel (radius 0) collection at 34 of 8192 texels (max 57/255), at system edges where the radius-0 collection truncates the support at the major-radius circle; the superset is the more faithful. CPU: 0.53 ms median / 0.56 p95 / 0.67 max per frame while building (was 1.04 / 1.07 / 2.38 at the 1 ms budget); a build takes about 435 ms of work, so about 870 frames at 0.5 ms. Not threaded: `CollectRenderWeatherSystems`, the weather queries and the manual-weather dictionary are main-thread state.
    - Open: a manual As bank 150 km down the ring (`cloud_layers_capture far150`) shows no visible far-sky contribution (sky on vs sky opacity 0 differs by about 0.4 luma, at the default handoff and at 40-60 km). `probe.LayersAt` reads the macro map, so it returns -1 beyond about 70 km on the short handoff. Not root-caused: needs a probe of the fine atlas texel at the bank.
    - Debug: `RING_FAR_FINE_OFF=1` (A/B), `RING_FAR_FINE_RECENTRE_M` (short recentre threshold), `RING_FAR_FINE_EXACT=1` (per-texel collection), `RING_FAR_FINE_VERIFY=1` (counts texels that differ from the exact path).
    - Parity (masked MAE vs the near-only reference, short 40-60 km handoff): As deck 2.7-3.8 vs 4.2-5.6 with the whole-ring atlas only; fair 3.6-7.8 vs 5.0-8.7; overcast 2.4-4.5 vs 1.5-4.7; Cs unchanged. Overcast ground p03 (4.0 vs 2.9) differs in the upper rows 0-150 (near-owned sky well inside the 40 km handoff, so not a far-pass effect); treated as exposure/temporal noise.
    - Cs at the short handoff stays about 11 masked in the cs_h shots with or without the fine atlas (not investigated; suspect the near shell fade vs the far shell crossing).
    - **Coarse storm channel:** the fine atlas has no storm channel. Beyond the macro map the storm level and ownership come from the 245 km weather atlas (`atlas_storm`), so storm closure and lighting are coarse there (storm ownership of high coverage is already baked into the atlas B channel).
  - **Open:**
    - Beyond the macro map the far used the 245 km atlas; the **fine far layer atlas** (below) now covers it.
    - The main-view layer (ground pixels seen from altitude) only gets the layer-union coverage, not the layer sky.
    - **Fair far horizon strip (deferred, not tuned):** a 12-row strip at the horizon (rows 435-447 of 900 on the ground shot, 443-460 at 1.5 km) is +7 to +11 luma brighter than the near-only reference (about 204 vs 193). `far_layers_sun_gain` (0.8, 0), `far_layers_ambient_gain` 0.8, `far_layers_opacity_gain` (0, 0.8, 1.5, 3) and sun + ambient 0.8 moved it by under 1 luma, and with the sky opacity at 0 the strip is still +8.3 (+7.4 with the sky at defaults). So the sky pass barely draws there: suspect thin or missing cumulus density at grazing rays (hull entry, stratified sample count or the `lowpass` erosion blend), not lighting. Sky rows 380-440 elsewhere are within noise (abs about 2.0, signed -0.4).
    - `tools/far_cloud_detail_gpu_test.gd` now hides the sky and drives the legacy clipmap body; it passes.

### WO7: front sequence and diurnal (macro build and CPU twin)
- **Status 2026-10-29: the front-sequence half (precursor reach, warm Ci/Cs/As/Ns phase, cold band and clearing, breakup behind) is tabled by the user** (weather is intricate enough for now); the WO1/WO2 front-phase code stays as built, `SupportMultiplier` stays 1.0. WO7 delivered the diurnal cumulus only (validated against `CelestialEclipseMath.EvaluateFrame`, the function `RingDayNightCycle` drives the sun with; `CloudLayerColumnTest.CheckRealDayNightCycle`).
- **Required:** the warm/cold phases of 2.2, `SupportMultiplier` 2 for warm-type fronts, and the per-texel diurnal phase.
- **Validation:**
  - the column test extended;
  - a time-lapse with the camera fixed and a warm front advecting, as an mp4 plus sheet, checked against probe readback of layer coverage over time (Ci, then Cs, then As, then Ns, then the 500 m base);
  - a diurnal sheet at 06 / 10 / 14 / 18 local;
  - the upload cap is not exceeded in the 64-system stress pose (`WeatherSystemCandidatePeak`).
- **Coordinate with:** S4 WO-T (storm travel).

### WO8: menu, persistence, presets (`live-tuning-setting` skill)
- **Status 2026-10-29: delivered.**
  - **Renamed (keys kept, so saves load unchanged; stored values are clamped to the new ranges):** `NaturalCloudBaseAltitudeMeters` is "Fair-weather cloud base" (800-3000 m, default 2000); `StormFrontCloudBaseAltitudeMeters` is "Weather cloud base" (300-1500 m, default 500). "Storm envelope ceiling" is capped at 18000 (`RingLiveTuningDrawer.cs:547`, `GameplayCloudDefaults` range).
  - **Retired:** `NaturalCloudTopAltitudeMeters` has no menu row and is no longer an override key. `GameplayCloudDefaults.RetiredKeys` drops a saved value silently on load (no `LastError`), so the next save rewrites the file without it. The bridge property stays at 6000 for the legacy band and CPU weather.
  - **Added (Clouds tab, "Gameplay appearance"):** `LowCloudAmount`, `MidCloudAmount`, `HighCloudAmount` (coverage bias -1..1, default 0) and `DaytimeCumulus` (0..1, default 1), on the bridge, in the `GameplayCloudDefaults` whitelist and the drawer.
  - **Semantics:** the bias applies to the fair-weather coverage of its layer only (weather systems keep their cloud): `+b` gives `c + (1 - c) b`, `-b` gives `c (1 + b)`, exactly 0 returns `c` (bit-identical). `DaytimeCumulus` multiplies `DiurnalStrength` (`CloudLayerTable.EffectiveDiurnalStrength`).
  - **Plumbing:** the bridge pushes the four values into `CloudLayerTable.Shared` (runtime, not exported; `SyncCloudLayerTuning`), so `BuildBlock`, `EvaluateMap` and therefore the far atlas read them with no far-path edit. UBO slots: `header1.w` low bias, `header3.z` mid bias, `header3.w` high bias, `header0.w` carries the effective diurnal strength. The block stays 41 vec4 = 656 bytes. GLSL twin: `ring_cloud_layers_amount_bias` in `RingCloudLayers.glslinc`.
  - **Debug mask:** `CameraIndependentDebugMask` is 0..511 (bit 64 layers off, 128 shell off, 256 deck colour off); the drawer help text lists all three. Not in the menu as rows.
- **Original plan:**
- **Change:**
  - `NaturalCloudBaseAltitudeMeters` becomes "Fair-weather cloud base" (800-3000 m);
  - `StormFrontCloudBaseAltitudeMeters` becomes "Weather cloud base" (300-1500 m, default 500);
  - "Storm envelope ceiling" is capped at 18000 (`RingLiveTuningDrawer.cs:534, 547`).
- **Retire** `NaturalCloudTopAltitudeMeters`; saved values are ignored and migrated.
- **Add:**
  - "Low cloud amount", "Mid cloud amount", "High cloud amount" (coverage bias, -1..1, default 0);
  - "Daytime cumulus" (0..1, default 1).
- **Debug only, not in the menu:**
  - `CameraIndependentDebugMask` bit 64 (layers off: single legacy band) and bit 128 (shell off);
  - extend the range to 0..511 (done);
  - a layer-id false-colour debug view.
- **Validation:** `gameplay_cloud_tuning_smoke_test`, settings smokes, `QualityPresetContractHeadlessTest`.

### WO9: CPU consumers and the flight/precipitation query
- Switch the following to `RingCloudLayers` / `QueryCloudLayerColumn`:
  - `PrecipitationAltitude.ResolveCloudBounds` callers (`RingLocalWeatherEffects.cs:564-568`, `RingDistantRainCurtains.cs:205-206`);
  - the lightning cloud base (`RingVolumetricCloudLayer.Lightning.cs:303`, `RingLocalWeatherEffects.cs:919-920`);
  - storm arrival (`RingVolumetricCloudLayer.StormArrival.cs:395, 428`).
- Optional: an in-cloud fog hand-off to haze (the post-rain mist path) while the camera is inside low-layer density.
- **Validation:** rain falls from the rendered base (S1 rain profile tests, `rain_precipitation_profiles`); the fingerprint as in WO1.

### WO10: acceptance pass (lead review)
- The full capture set, perf A-B-A on the 4070 (and the A1000 when available), the batch GPU gate list of the plan doc, and the doc handoff.

## 7. User decisions (2026-10-28)

- **Q1:** fair-weather base is a **fixed 2000 m** (no diurnal rise). `FairWeatherCloudBase` defaults to 2000 m. Diurnal cumulus varies coverage, not base.
- **Q2:** cloud on hillsides in weather is **accepted** (hill fog; plateaus sit in the weather deck). No terrain-relative floor.
- **Q3:** climate/biome layering is a **reserved hook** for the coming revamp; not built in S6.

Original questions, kept for reference:


1. **Fair-weather base vs skyline.** How high is the playable skyline? If the usual playable hills stay below about 1.3 km, I recommend a fair-weather base of 1.5 km rising to 2 km by afternoon (physical). Otherwise keep a fixed 2 km.
2. **Weather cloud meeting terrain.** With the base at 500 m in weather, is cloud on hillsides acceptable (hill fog; the camera on a 1.5 km plateau would stand in cloud during storms)? The alternative keeps the base at least about 300 m above the local terrain, which needs a terrain-height field (larger scope).
3. **Climate and biomes.** Should layers follow climate now (oceans with low Sc decks, deserts with high, sparse Cu, alpine cap clouds)? Or is the climate input left as a reserved hook until the coming revamp?

## 8. Room for the coming revamp

- The layer table is data, not code: 4 slots with 3 used, per-layer render mode, gradient rows generated from curves, and a genus x layer matrix. A new layer or genus is a table row plus a gradient curve.
- The macro map gains layers as array slices; the CPU column returns a list, not fixed fields.
- The per-ray machinery works on a list of bands and intervals with a general gap predicate, so arbitrary layer gaps need no new march rules.
- The climate input slot and per-layer render mode (Shell to Volumetric for flight) are reserved, not implemented.
