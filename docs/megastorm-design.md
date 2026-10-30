# Megastorm design (lead, 2026-10-29)

Status: design only, nothing implemented. Branch `storm-revamp-wip` (working tree of 2026-10-29, about to merge to main). Line references are to that tree.

Companion design: `docs/containment-field-design.md` (the field doc). It owns the field, its cloud ceiling and the `ContainmentDisturbance` bus. Section 6.3 is the megastorm side of that contract.

User vision (2026-10-29): "a wall of clouds towering from 500 m to the cloud limit all along the width of the ring that sweeps the whole ring. This can act as an ever-present threat once it is triggered somewhere along the game story."

Initial user decisions:
- **Spectacle plus hooks.** Build the wall, darkness, wind, debris, lightning and audio now. Expose clean queries (wind, danger level, time to arrival, position and phase) so damage, shelter and vehicle grounding can come later. No gameplay effects now.
- **Story trigger.** It starts at a ring position and time, persists across save and load, and is deterministic.

## User decisions 2026-10-29 (second round; these supersede the first draft)

1. **Pace.**
   - Default speed is **350 m/s**: a lap of about 20 in-game days (24.9 h of weather clock). It is tunable in the grave menu over **5-500 m/s**.
   - At that speed a pass over a point lasts about a quarter of an in-game day: 1125 s, about 390 km. This doc reads that as **leading edge to the end of the trailing rain = 390 km** (shelf, wall, transition, trailing). The precursor ahead and the aftermath behind fall outside it.
   - Everything derived from speed was reworked (sections 2.3, 4, 5.1).
   - The 350 m/s pipeline checks are in section 3.6. That section is why the near wall now uses a moving-frame strip map instead of the shared storm bake.
2. **Day look from across the arch: physical.** By day the far side shows the sunlit tops, a bright streak. At night it shows the flicker band and sprites.
3. **Field ceiling: the field doc's soft cloud ceiling is the single ceiling.**
   - The field sits on the real rim-wall top: 10.0 km on Certified and 10.4 km on runtime-surface worlds (field doc E1/E2). From there a semi-ellipse rises to 40 km at the centreline.
   - The ceiling is `min(18 km, H(across) - 300 m)` with a 500 m feather. It is **defined once in the field geometry code** (field doc 2.2). Sunshine, the CPU cloud column, the storm profile bake and the megastorm wall (strip build, far wedge pass, C# twin) all call that one function; no consumer keeps its own copy.
   - The field reaches 18 km within **6.2 km of each wall (Certified, 10.0 km wall top)** and **5.8 km (runtime-surface, 10.4 km)**. The ceiling is exactly 1.0 beyond 7 km. The megastorm flattens against it in that thin rim band.
   - The first draft's "underside minus 1 km", "outer ~30 km", "6 km rim tops" and its cirrus-shell note were wrong and are removed. The 9.5 km cirrus shell is below the 10 km wall top, so the field never touches it.
4. **Sprites and jets are drawn as they are, with no clamp under the field.** Megastorm discharges go through the natural sprite layer (`UpperAtmosphereLightning`) and share its event id. The field's sprite adapter therefore ripples each discharge at most once (section 6.3).

## 1. Facts from the code that shape the design

| Fact | Evidence | Consequence |
|---|---|---|
| Ring circumference 31,415,936 m (R = 5,000 km), width 318 km | `scripts/Terrain/Generation/TerrainGenerationParameters.cs:23` | A 350 m/s lap is 89,760 s |
| In-game day = 4500 s | `scripts/Celestial/CelestialConfiguration.cs:8` | A 350 m/s lap is 19.9 in-game days. A quarter day is 1125 s, about 394 km |
| The weather clock is `CloudSimulationTimeSeconds`, advanced at 1x and saved | `RingVolumetricCloudLayer.cs:21, 582`; `RingWorldSaveManager.cs:171, 338, 1078` | The megastorm runs on the weather clock, so it stays consistent with every cloud, rain and lightning consumer. The celestial clock (TimeScale 1-100, `:1085`) is separate |
| The whole weather field is a pure function of (position, time), and every consumer reads it through a few system collections | `BuildNearbyWeatherSystems` (`RingVolumetricCloudLayer.cs:3787`), `CollectRenderWeatherSystems` (`:3890`), `CollectStormGeometry` (`RingVolumetricCloudLayer.StormArrival.cs:94`) | Injecting the megastorm as system records at these three sites gives most consumers for free: macro map, storm-light D (owned and front layers), S6 layers and weatherness, rain, curtains, wet world (S3 rain history), arrival, wind, audio |
| Systems are ellipses everywhere (CPU and GPU); membership is the unit-ellipse norm `q` | `ring_ws_ellipse_normalize` (`RingWeatherSystems.glslinc:320`), sub-mask and ownership (`:334-391`), ray intervals (`SunshineCloudsCompute.glsl:734`), `StormArrivalMath` | Encode the full-width wall as an ellipse with a 1e7 m across semi-axis. `q` then reduces to the around offset divided by the half depth (section 3.2) |
| Storm bodies come from the baked storm profile map: a **world-anchored snapshot**, camera-centred, 1024², rebuilt over 8 slices after `MacroMapRefreshSeconds` | `RingStormProfileMap.gd:1-11`, `RingStormProfileBake.glsl:68-79`, `CameraIndependentClouds.cs:25, 59-62` | At 350 m/s a world-anchored snapshot steps about 130 m per rebuild. The wall needs a moving-frame map (section 3.6) |
| Cloud history reprojection is camera-only: clouds are assumed static | `SunshineCloudsHistory.glslinc:168-198` | A wall moving 350 m/s ghosts unless reprojection knows its motion (section 3.6) |
| Macro map refresh is 0.25 s plus 8 slices; weather refresh 0.25 s; near shadow refresh 0.5 s in 4 slices; far atlases refresh every 8 s | `CameraIndependentClouds.cs:59-62`; `RingSunshineCloudBridge.cs:75, 90, 111`; `RingScaledFarViewport.cs:155` | Each cadence is checked against 350 m/s in section 3.6 |
| The storm-map profile predicate is hard-coded `2 \|\| 3` in 5 places | `RingStormProfileBake.glsl:73`, `RingStormProfileSample.glslinc:36`, `RingWeatherSystems.glslinc:354`, `SunshineCloudsCompute.glsl:380`, `SunshineCloudShadowCompute.glsl:221` | The new wall profile (12) gets its own branch: a strip fetch |
| Beyond the near handoff (about 48-56 km), no storm body is drawn. The far layer sky draws only the 0.5-7 km layers; the whole-ring atlas is 128 x 32 samples (about 245 km per sample around) | `RingScaledFarViewport.cs:830-845`; S6 doc WO6; S4 WO-D | The far wall needs its own pass (section 3.3) |
| Hard cloud ceiling 18 km; the shared field ceiling under it near the walls | `RingStormShapes.glslinc:16-21`; field doc 2.2 and WO6 (the shared ceiling function; `ring_containment_ceiling` on the GPU) | The wall top is the shared field ceiling (section 2.2) |
| Rim walls are 10.0 km (Certified) / 10.4 km (runtime-surface) | field doc E1/E2: `CertifiedRingScale.cs:13-24`, `VoxelChunkGridManager.cs:1600-1604` | The rim buttress climbs a 10 km face. Field contact is within 6.2 km of each wall (5.8 km on runtime-surface worlds) |
| Arrival maths measures rim distance along the radial ray from the system centre | `StormArrival.cs:303` (`dGust = dist - nGust * ray`) | CPU slab records put their across centre at the query point (section 3.2) |
| Arrival bands are distance-capped: 400 m to 6 km, approach reach 18 km | `StormArrival.cs:78, 296-310` | At 350 m/s the caps bind: the gust front would last 17 s and the calm 51 s. The megastorm needs time-based bands (section 5.1) |
| Arrival kind weight is already 1.5 for kinds >= 5 | `StormArrival.cs:112` | Megastorm severity needs no new table |
| Speed of sound in air is about 343 m/s; through the ring structure (floor, walls) about 5-6 km/s | physics | At 350 m/s (Mach 1.02) airborne sound cannot get ahead of the wall, but structure-borne sound outruns it about 15x. That is the basis of the sound design (section 5.2) |

## 2. What it is

### 2.1 Structure (an exaggerated squall line / mesoscale convective system)

**Moving frame.**
- `s(t)` is the around coordinate of the reference leading line; `dir` is +1 or -1.
- `x(p, t) = dir * wrap(s(t) - p.around)`: negative ahead of the line, positive behind it.
- The leading edge bows and meanders: `x_lead(across, t) = -Bow * (1 - u^2) - Meander(across, t)`, with `u = 2 * across / W - 1`. Defaults: Bow 8 km; Meander 3 km amplitude and 40 km wavelength, with the phase drifting at 1 rad/h.
- `xi = x - x_lead`: distance behind the actual edge.

**Zones** (defaults; the times are what a player standing still feels at 350 m/s; T = 0 when the rain edge arrives):

| Zone | xi (km) | Lasts at a point | When | Content | Owner |
|---|---|---|---|---|---|
| Precursor | -260 .. -40 | 10.5 min | T-12.4 .. T-1.9 min | Cs veil with a halo, then As thickening (S6 high and mid rows); dry | layers (record P), analytic far |
| Leading anvil overhang | -60 .. 0 | 2.9 min | T-171 s .. T0 | Canopy, underside 9 km rising to 12 km ahead, top 16.5 km; mammatus at -25 .. -3 (T-71 s .. T-9 s); virga | strip (record A) |
| Shelf / arcus | -4 .. +1 | 14 s | T-11 s .. T+3 s | Base lowered to 300-500 m, 2-3 noise tiers, a lighter lip on a dark underside | strip (record W) |
| Convective wall | 0 .. 25 | 71 s | T0 .. T+71 s | Continuous body from 500 m to 15-18 km (or the field ceiling). Towers every 8-15 km along the across axis (hashed); overshooting domes; rain shafts; hail | strip (record W) |
| Transition zone | 25 .. 50 | 71 s | T+71 .. T+143 s | Top drops to 7-10 km and thins; lighter rain | strip (W) plus layers |
| Trailing stratiform | 50 .. 390 | 16.2 min | T+2.4 .. T+18.6 min | Closed Ns deck 0.5-7 km, steady heavy rain, Cs sheet above (trailing anvil) | layers (record T), analytic far |
| Aftermath | 390 .. 990 | 28.6 min | T+18.6 .. T+47 min | Clearing; post-frontal Cu; natural weather recovering (section 4.3) | natural weather |

The pass, from the edge to the end of the trailing rain, is 390 km: 1114 s at 350 m/s, about a quarter day.

- **Full width.** The wall spans across 0..W up to the rim walls; terrain depth clips it against the 10 km rim faces. Within 15 km of a rim, towers merge into one continuous "rim buttress" climbing the wall face. Its top flattens under the field ceiling in the outer 6.2 km (5.8 km on runtime-surface worlds).
- **Formation** after the trigger, default **1800 s**: density, precipitation and top grow from 6 km to full height over the whole width (`f = smoothstep(0, 1, age / Formation)`).
  - It travels about 630 km while forming. A trigger 600-2,000 km from the player shows the line erupting on the arch while it closes in.
- **Internal life.** Tower cells re-seed on a 20-40 min hash cycle with crossfades, and a 6-12 h breathing (strength 0.8-1.0). All shape and **all noise** are evaluated in the moving frame (section 3.6), so the wall reads as one travelling object rather than a mask sliding over static cloud texture.

### 2.2 Height: the single cloud ceiling
- Everywhere: the S6 hard cap of 18 km. Raising it would raise every ray's envelope; above 18 km the spectacle comes from sprites and jets and from field ripples.
- Within 6.2 km of each rim wall (5.8 km on runtime-surface worlds): the shared field ceiling `min(18 km, H(across) - 300 m)` (500 m feather). H is the semi-ellipse from the 10.0/10.4 km wall top to 40 km, read from the live `StructuralWallTopGlobalY`, never a constant.
  - The megastorm adds no ceiling of its own. It calls the field geometry code's single ceiling function, like Sunshine, the CPU column and the storm profile bake do.
  - The strip build clamps its column tops with that function, so the light-march column top and the density agree.
  - The C# twin `MegastormProfile.Column` clamps through `FieldUndersideProvider` (section 6.3).
  - The far wedge pass applies the same function (one compare; exactly 1.0 beyond 7 km). This deviates from the field doc's "far tiers skip it", for this pass only: near the rim the wall top drops from 18 km to about 9.7 km, which is visible at 50-200 km.
- Result: in the thin rim band the wall top spreads as a flattened shelf under the field, the field doc's `MegastormContact` source (section 6.3).

### 2.3 What the player sees (350 m/s, the observer standing still)

| Time to the rain edge | Distance | What happens |
|---|---|---|
| T-105 min | 2,200 km (10 x warning reach) | A faint structure-borne ground hum (20-60 Hz) begins and the rim walls faintly sing. Barely noticeable at first (the PDA logs it once it is audible, about T-73 min) |
| days to hours | 1,000s of km | Always somewhere on the arch. By day a dark face at 50-2,000 km and a bright streak of sunlit tops from across the arch (physical, decision 2); at night a flicker band plus sprites. Its slide along the arch is visible over minutes |
| T-48 min | 1000 km | A dark bar 5.7-6.8 degrees above the local horizon, spanning about 18 degrees. The line of sight climbs about 25 km mid-path, so only the near legs are hazed |
| T-21 min | 440 km (2 x warning reach) | The hum grows. Still water shivers, wildlife goes quiet, and the wall flashes silently on the horizon: no airborne sound can arrive ahead of it (section 5.2) |
| T-12.4 min | 260 km | The Cs veil and halo arrive overhead; the sky goes milky |
| T-9.5 min | 200 km | The wall fills about 77 degrees of the forward view and stands 5 degrees high; it is visibly growing |
| T-2.4 min | 50 km | The face rises 20 degrees and fills the whole forward view (plus or minus 72 degrees) |
| T-171 s | 60 km | The anvil overhang starts passing overhead. **Fast dimming to a deep, eerie gloom** (megastorm darkness E 0 to 0.55 by T-14 s, 5.6) |
| T-194 s .. T-45 s | 68 .. 16 km | The calm (Approach), with light inflow toward the wall. The air is dead quiet; only the floor hums, with a subtle camera micro-shake |
| T-71 s .. T-9 s | 25 .. 3 km | Mammatus racing overhead |
| T-45 s | 15.7 km | Gust front at about 41 m/s, with a brief sound swell riding the outflow (sound moves at 343 + 41 m/s in it, faster than the wall); debris and dust; lightning closing in ("bolt from the blue" at T-43 s .. T-14 s) |
| T-16 s | 5.5 km | Fat drops |
| T-11 s .. T+3 s | -4 .. +1 km | The shelf sweeps overhead in about 14 s |
| T0 | 0 | **The boom**: every sound the wall made while closing in arrives at once (Mach pile-up). A rolling thunder wave of about 3-5 s, then the roar |
| T0 .. T+71 s | 0 .. 25 km | The wall: **near-night darkness** (E = 1: ground at about 6% of an ordinary storm's light, AE locked; the world is seen in flashes, 5.6), torrential rain, hail, lightning all around, **lightning scythes** skimming the ground (T-43 s to T+57 s, densest T0 to T+29 s); thunder now has normal physical delays. **Hazards (5.5):** the boom overpressure at T0; bullet rain peaking from T+6 s to T+43 s |
| T+71 .. T+171 s | 25 .. 60 km | Very dark (E 0.9); the condensation fog catches the flashes. **Vacuum wake (5.5):** the wind reverses (T+71 s to T+100 s), the pressure trough with condensation fog and ear pop (T+100 s), then inflow rushing toward the departing wall (T+100 s to T+171 s) |
| T+71 .. T+143 s | 25 .. 50 km | Transition: lighter rain, gusty (overlaps the wake) |
| T+2.4 .. T+18.6 min | 50 .. 390 km | Trailing Ns rain; the front is dark by its rain (S0). A heavy overcast gloom (E 0.7) that lightens gradually over the 16 min as the deck thins |
| T+18.6 .. T+47 min | 390 .. 990 km | Aftermath: light returns (E 0; ordinary D fades with the rain); clearing, post-rain mist, drying. A rainbow on the departing wall when the sun is low behind the observer |

Other views:
- **Quarter ring (about 7,000 km).** The face is seen 45 degrees off its normal: a ribbon of towers high on the arch.
- **Antipode.** The view is of the footprint from above: about 1.8 degrees by 1.6 degrees (the 50 km wall plus the 340 km trailing deck, foreshortened).
- **Rim.** The buttress on the 10 km rim face, with its top flattened under the field in the outer 6.2 km (5.8 km on runtime-surface worlds). From 50-200 km the two ends read as stepped-down shoulders.

## 3. Representation and rendering

### 3.1 Ownership by distance

| Range | What draws the wall | Reuse / new |
|---|---|---|
| 0 .. near handoff (about 50 km) | Sunshine march. Records W/A give the ray intervals and ownership; the **moving-frame megastorm strip map** gives the column (one gated fetch); shadows use the same strip in the shadow compute | new strip map and fetch; reprojection term |
| handoff .. far terrain / disk fade (about 245 km) | **New wedge pass** (wall and anvil) plus the far layer sky. The layer sky truncates at the wall body; precursor and trailing coverage come from the analytic xi profile, not the atlases | new pass; layer-sky edits |
| beyond the disk fade, to the antipode | The same wedge pass. Beyond the disk fade it also draws the trailing deck and precursor as low/mid slabs, because the atlases exclude the megastorm (section 3.6) | same new pass |
| lightning, near (< 80 km) | `RingLightningEvent` stream (bolts, flash, thunder) | reuse event path; new stream |
| lightning, far | Analytic flash field per 5 km wall segment in the moving frame | new, in the wedge shader |

### 3.2 Near: records, strip map and march

**Records: "the megastorm bundle".** Four records come from `MegastormModel` at the query time. Natural semantics: source frame = world - weather wind x t; each record's own drift is `dir * v` around.

| Id | Role | Kind | Profile | Flags | Centre xi | Half depth | Density / precip |
|---|---|---|---|---|---|---|---|
| -30001 | W wall | `Megastorm` (new, 6) | `MegastormWall` (new, 12) | Sculpt, LowersBase | +18 km | 24 km | 1.0 / 1.0 |
| -30002 | A leading anvil | `Megastorm` | `MegastormWall` | Sculpt | -25 km | 30 km | 0.5 / 0.0 |
| -30003 | T trailing | `ExceptionalFront` | `MegastormTrailing` (table row: Ns with High 0.9) | Front, LowersBase | 50 + L/2 (220 km) | L/2 / 0.8 (212.5 km) | 0.9 / 0.75 |
| -30004 | P precursor | `CloudBank` | Altostratus (Cs row) | none | -150 km | 110 km | 0.7 / 0.0 |

- **W and A.**
  - W's rain rim (n about 0.75) falls at xi = 18 - 0.75 x 24 = 0, so the arrival maths puts the rain edge under the shelf.
  - The W/A supports (x 1.45) cover xi -68 .. +53. Ownership (x 1.3) covers -64 .. +49, so D, the 1.05 lift and the storm ambient apply under the anvil and in the wall.
- **T.** Rain runs from about xi 60 to 390 (n about 0.8). The gap between the W rain (to about 36 km) and the T rain is the lighter transition zone.
- **Slab encoding.**
  - Orientation pi/2, so the major axis runs across: a.z = 1e7 m, a.w = half depth. Then `q = (d_across / 1e7, d_around / halfDepth)`, and the edge error is at most about 6 m across the full width.
  - The mask circle (`RingWeatherSystems.glslinc:476`) always admits the records. `ring_ws_sculpt_ellipse_subset` (`:512`) and `ring_ws_build_ray_sculpt` (`SunshineCloudsCompute.glsl:734`) cull them exactly to the slab.
  - Upload sorting by edge distance (`CameraIndependentClouds.cs:180-200`) keeps them first.
  - CPU records put the across centre at the query point (arrival, history, `QueryRawWeatherAtTime`); the GPU puts it at the camera's across.
  - Fallback if a consumer misbehaves: an explicit `RING_WS_FLAG_BAND` (bit 64) in `ring_ws_ellipse_normalize` and `ring_ws_mask`.
- **Megastorm strip map (new; replaces the first draft's "bake it into the storm profile map").**
  - **Texture.** `Texture2DArray` RGBA16F, 1024 x 1024 x 2 layers (heights `(body base, body top, canopy underside, canopy top)` and aux `(canopy coverage, mammatus, tower coverage, rain shaft)`), double-buffered (32 MB total).
  - **Indexing.** Moving-frame coordinates: around axis `x` (reference-line distance) over -70 .. +56 km at about 123 m per texel; across axis 0..W at about 311 m per texel. Bow and meander are baked in.
  - **Build.** New `addons/SunshineClouds2/RingMegastormStripBuild.glsl` and `RingMegastormStrip.gd` (owned by `SunshineClouds.gd` like `RingStormProfileMap.gd`). It evaluates `ring_megastorm_column(x, across, t, params)` from the shared texture-free include `materials/ring_megastorm_profile.gdshaderinc`; the far wedge pass and the C# twin `MegastormProfile.Column` use the same include.
    - The bake already clamps to 18 km and to the field ceiling, so the column top is the true top.
    - It is rebuilt every 2 s in 8 slices, front/back swap. In the moving frame only cell life and meander change (under 0.1% per 2 s), so a swap is invisible.
  - **Fetch.** In `sampleScene` and the shadow compute, profile 12 takes a new branch.
    - When the primary sample is inside the W/A support (the ray's sculpt mask holds a profile-12 record, known per ray), compute `x` from a per-frame uniform. The CPU computes `s(t_now)` in double and uploads it camera-relative, so float precision stays at metres.
    - Fetch the strip column. Merge with the storm-map column by thickness-weighted choice: natural storms are fully suppressed there before the strip support arrives (section 4.3), so overlap is empty in practice.
    - Profile 12 never enters the storm profile bake (`RingStormProfileBake.glsl:73` keeps `2 || 3`).
    - The remaining 4 predicate sites treat 12 as a map storm, with the strip as its column. The analytic fallback (`RING_STORM_FALLBACK`, map off) returns 0.
- **Moving-frame noise.** For strip density, Sunshine's large/medium/small noise positions are offset by `-dir * v * t` along the around axis, so the erosion travels with the wall.
  - Strip edges collapse to zero density, so the noise-frame discontinuity at the strip boundary sits where there is no density.
  - Background layer scud inside the wall keeps world noise: scud streaming under a moving wall is acceptable.
- **Not changed:** `ring_ws_macro_point` (the bundle uses natural falloff), the macro map shader, the S6 layer code. Records T and P get their look from CloudLayerTable genus rows.

### 3.3 Mid and arch scale: the megastorm wedge pass (new)

**Geometry.** A surface of constant around angle is a half-plane that contains the ring axis. In the far viewport (axis +Y, `ring_far_cloud_layer_sky.gdshader:44-60`), the wall body is the intersection of:
- the wedge between the angles at `xi_min` and `xi_max`;
- the annulus between radii R - 18 km and R - 0.3 km;
- the across slab [0, W].

All of it is analytic per pixel and exact at any distance. The angles come from the per-frame `s(t)` uniform, so the pass moves continuously.

**Pass.**
- Files:
  - `scripts/Voxel/LOD/RingScaledFarViewport.Megastorm.cs` (node `FarMegastorm`);
  - `materials/ring_far_megastorm.gdshader`, including `ring_megastorm_profile.gdshaderinc`, `ring_atmosphere.gdshaderinc` and a new `ring_far_storm_lighting.gdshaderinc` (wrap lighting, remote-day factor, lightning glow; WO-D reuses it).
- **Proxy hull mesh.** A low-poly curved box around the wedge. It is transformed every frame by the around angle; the mesh is rebuilt only when the trailing length setting changes. Only covered pixels pay; when inactive the node is hidden, at 0 cost.
- **March.** 12-24 stratified samples in the clipped segment:
  - wall and anvil density = profile column x moving-frame fbm fray;
  - below the base: a rain-shaft medium (precipitation profile x hashed shafts 2-6 km wide);
  - the field ceiling helper on the column top.
- **Beyond the disk fade** it also integrates the trailing deck (xi 50-390, 0.5-7 km) and the precursor veil (single-scatter sheet at 9.5 km), because the atlases exclude the megastorm.
- **Lighting and haze.** Sun through the column top (closed form) plus sky ambient, darkened by the implied D. Haze is `ring_haze_sample` at the opacity-weighted depth.
- **Handoff.** Weight is `1 - near_weight(t)` (the layer sky's `near_handoff`).
- **Ordering.**
  - The wedge pass draws before the far layer sky (RenderPriority 15 vs 16, `RingScaledFarViewport.CloudLayers.cs:275`).
  - The layer sky ends its integration at the analytic wall-body entry (`megastorm_body_entry`), so cloud in front composites over the wall and the deck behind it is not double counted.
  - Against the whole-ring shell (priority 10-14), the wall draws over it.
- **Night: flash field.**
  - 64 segments of 5 km across. Each segment fires from `hash(seed, segment, slot)` (slot 2 s, mean interval 20 s per segment, so about 3 flashes/s along the line), using `RingLightningEvent.PulseAtAge` maths and the reduced-flash envelope.
  - Segments live in the moving frame, so flashes travel with the wall.
  - The CPU twin `MegastormLightning.FlashesInWindow` drives sprites, rumble density and field ripples.

### 3.4 Far layer sky and atlases
- **Megastorm records are excluded from the far atlas builds.** Add an `includeMegastorm: false` path through `QueryLocalWeatherCoverageAtTime` (`RingScaledFarViewport.cs:840`) and `CollectRenderWeatherSystems` (`RingScaledFarViewport.CloudLayers.cs:468`).
  - Natural-weather suppression stays in the atlases: it is natural state, and its gradients are wide.
- **Analytic coverage.** The far layer sky adds the megastorm's T and P layer coverage analytically from xi per sample: the same falloff as the records, via `MegastormProfile`, ported into `ring_far_cloud_layers.gdshaderinc` behind one uniform branch.
  - Inside the Sunshine macro map it keeps using the map, which already holds the records.
  - Across the map edge it blends map and analytic over the last 10% of the map.
- The far sculpt list (`FarLayerMaximumSculpts` 8) receives W and A for ownership (lift and closure).

### 3.5 Perf budget (RTX 4070, 1600x900 High; A1000 at about 2x per S6 practice)

| Path | Not triggered | Triggered, wall beyond the selection radius | Triggered, near / inside |
|---|---|---|---|
| CPU model and injection | one bool check | <= 0.02 ms/frame | <= 0.05 ms/frame |
| Sunshine `GPU.Clouds` (strip fetch gated per ray, moving noise, reprojection term) | identity (byte-exact image) | 0 | <= +0.15 ms vs a natural storm at the same pose |
| Strip build | 0 (not dispatched) | 0 (not dispatched beyond the selection radius) | <= 0.03 ms amortised |
| `GPU.CloudMacroMap` with the fast cadence (3.6) | 0 | 0 | <= +0.02 ms |
| Shadow compute | 0 | 0 | <= +0.05 ms |
| Far wedge pass | 0 (node hidden) | <= 0.10 ms typical, <= 0.25 ms when the wall fills more than 30% of the view | <= 0.25 ms |
| Far layer sky (truncation plus analytic coverage) | 0 (uniform branch) | <= 0.02 ms | <= 0.02 ms |
| Lightning streams | 0 | <= 0.01 ms CPU | <= 0.02 ms CPU |

**"Zero cost when inactive"** is tested as:
- an A-B-A against HEAD on the fair, overcast and storm poses, within noise;
- `compare` SSIM 1.000 with a clean heatmap on fixed-clock shots;
- `NaturalWeatherFingerprintTest` bit-identical.

### 3.6 Coping with 350 m/s (11.7 m per frame at 30 fps)

| Stage | Cadence today | Artefact at 350 m/s (500 m/s worst) | Fix |
|---|---|---|---|
| Storm profile bake (if the wall were baked there) | 0.25 s + 8 slices, about 0.38 s per cycle | About 130 m lag and a step every cycle at the edge: about 4 degrees, 70 px at 2 km. **Pops** | Moving-frame strip map (3.2): translation comes from a per-frame uniform, the map changes only by cell life. No step |
| Cloud history reprojection | camera-only (`SunshineCloudsHistory.glslinc:168-198`) | Wall pixels move up to about 6 px/frame at 2 km. History **smears and ghosts** the edge and the shelf | In `ring_cloud_history_reproject`: if the reprojected density point (`worldFinalPos`) is inside the megastorm strip column (analytic slab test plus the column top), offset `previousWorldFinalPos` by `-dir * v * dt_weather` along the around axis. This is shared by the interleave fill. If the residual `reprojectedPixelMotion` is over 2 px, raise the current-frame weight. Uniform branch; 0 when inactive |
| Sunshine detail noise | world-anchored (drifts at the weather wind) | Wall shape slides over static texture, a **"wipe"** look | Moving-frame noise for strip density (3.2) |
| Weather record upload | 0.25 s | Support lags up to 125 m | Absorbed: the support slack beyond the densest column is at least 8 km. The strip fetch itself uses the per-frame `s(t)` |
| Macro weather map (coverage, D layers, precipitation, S6 layers) | 0.25 s + 8 slices | D, rain and deck coverage at receivers step about 130 m per build. With the D ramp about 3.6 km wide that is about 4% luma steps at 2.6 Hz while the edge passes. **Visible stepping** | While the wall's support is within the map: `MacroMapRefreshSeconds` 0.05 and `MacroMapSlices` 2 (about 0.08 s per cycle, about 29 m steps, under 1% D), and build each snapshot at the predicted publish time (snapshot + cycle). Restore the defaults when the wall leaves. About +0.02 ms |
| Near cloud shadow map | 0.5 s, 4 slices | Anvil-overhang shadow edge steps 175 m every 0.5 s ahead of the wall (D is low there, so the shadow is visible). Under the wall F(D) removes the sun anyway | While the wall is within the shadow radius, `ShadowRefreshSeconds` 0.1 (4 slices kept). Verify the cost on the A1000 |
| Far atlases (fine 4-8 km, whole-ring 245 km) | 8 s | Deck and precursor edges jump 2.8 km (4 km at 500 m/s) every 8 s: about 1.6 degrees at 100 km. **Pop** | Exclude the megastorm from atlases; analytic coverage in the layer sky and wedge pass (3.4). Natural suppression gradients in the atlases move under 3.5% per refresh; check with `flicker` |
| CPU arrival cache (0.1 s lattice / 16 m), wind cache (0.25 s / 32 m) | - | 35-125 m between refreshes against 15.7 km bands | Fine |
| Rain history (lags 15-1500 s), wet map, post-rain mist (32 s minimum ramp) | - | The wall passes in 71 s and the trailing rain in 16 min | Fine |
| Lightning | 0.72 s events | A strike drifts 250 m during its life | Near strikes are fixed at spawn, which is physical. Far flash segments are in the moving frame |
| Sound | thunder delayed by distance | 350 m/s is faster than sound: nothing airborne from the wall can reach an observer ahead of it | Embraced as the design (section 5.2): a silent approach carried by a structure-borne hum, then a Mach pile-up boom at the edge. Air-sound behaviour follows the menu speed |
| Float precision | - | `s(t)` up to 3.1e7 m is 2 m precision in float | All GPU uniforms are camera-relative offsets computed in double on the CPU |

## 4. Motion, lifecycle and natural weather

### 4.1 Sweep
- **Position.** `s(t) = wrap(s_k + dir * v_k * (t - t_k))` for the last anchor k with t_k <= t.
  - Anchors are written by Trigger and by speed changes. The newest 16 are kept and persisted, so S3's 1500 s history lags and rewinds stay exact.
  - Before `TriggerTime` the megastorm does not exist.
- **Default 350 m/s; range 5-500 m/s.**

| v (m/s) | Lap (h of weather clock) | Lap (in-game days) | 390 km pass at a point | Warning from 1000 km |
|---|---|---|---|---|
| 5 | 1745 | 1396 | 21.7 h | 55.6 h |
| 25 | 349 | 279 | 4.3 h | 11.1 h |
| 100 | 87.3 | 69.8 | 65 min | 2.8 h |
| **350** | **24.9** | **19.9** | **18.6 min (about a quarter day)** | **48 min** |
| 500 | 17.5 | 14.0 | 13 min | 33 min |

- **Fixed in km.** Zone lengths are in km, so the look on the arch does not change with speed; pass time scales as 1/v. The user chose this on 2026-10-29: the size stays fixed and the pass time follows the speed.
- **"Ever-present"** at 350 m/s: it laps every 25 h of play and is always visible somewhere on the arch. The story places the first encounter.
- **Direction.** Around only, `dir` = +1 by default.
- **Pause.** `CloudSimulationPaused` freezes it like all weather.

### 4.2 Lifecycle
- Formation 1800 s, then steady lapping forever, with cell cycling and breathing.
- **`Stop(atTime, fadeSeconds)`** exists for story flexibility: densities fade, tops sink to 6 km, then it is inactive. Stopped state persists.
- **One instance.** A list later is a schema bump.

### 4.3 Natural weather (deterministic, single site)
- **Suppression weight** `S(xi)`:
  - 0 beyond 150 km ahead;
  - ramps to 1 by xi = -70 km. Natural cells are absorbed in the 3.8 min before the anvil support (-68 km) arrives, so a natural tower never pops when the strip takes over;
  - 1 through xi 390;
  - decays to 0 from 390 to 990 km (post-frontal Cu from the fair cluster term stays).
- **Where.** `TryBuildWeatherSystem` (`RingVolumetricCloudLayer.cs:4191`; output at `:4304`) multiplies the lifecycle envelope by `1 - S(xi at the advected centre, t)`.
  - The same effective envelope feeds `CollectStormGeometry` (`RingVolumetricCloudLayer.StormArrival.cs:121-130`, `life.Envelope` and the rho central difference) and `BuildNaturalRenderSystem` (`:3986`).
  - S has exactly 1.0 identity when untriggered, so the fingerprint golden does not move.

## 5. Integration with the storm-revamp systems

| Area | Reuse | New |
|---|---|---|
| Storm light D, darkness, AE | W/A ownership gives D = owned x max(G, heavy). T is a Front, so D follows its rain. S0 path darkening, eye cap, StandardMaterial3D tint, receivers | **Megastorm darkness beyond D = 1** (5.6): extended level `D + E` (0..2) through the same include and map fetch, darkness multiplier K, harder eye lock, night floor. Fast macro cadence near the wall (3.6) |
| Rain, hail, haze veil | Macro precipitation, streaks, splashes, curtains (`RingDistantRainCurtains.cs:197-203`, `naturalFront` becomes `IsSevere`), veil gate `Core x Severity` (Severity 1.5) | Megastorm hail placement: across uniform in [0, W], xi 2-15 km, moving with the wall, 3-6 swaths. At 350 m/s a swath hits a point for 5-40 s |
| Arrival sequence (S3) | `StormArrivalMath.Evaluate` on the slab records; v_r = v exactly; `TimeToRainSeconds = xi / v` | Time-based megastorm bands (5.1) |
| Wind (`ModifyWind`, `RingWindQuery`) | GustFront target `28 x (1 + 0.3 x 1.5)` = 40.6 m/s (`StormArrival.cs:504`); sustained 37 m/s (kind 6 replaces the in-wall wind with the 5.5.2 bullet-rain profile, peak 140 m/s, and adds the 5.5.3 wake reversal). The wind history rise and decay (5 s / 18 s) fit a 45 s gust front | `StormFactor` (`RingVolumetricCloudLayer.Wind.cs:175`) weight 1.25 for kind 6. `RingWindQuery`: wall updrafts 40 m/s for xi 3-20 km, downdrafts -12 m/s at 15-40 km, jet x1.8 (`WindAltitudeProfile.cs:25`). These are wind speeds, not the wall speed: the megastorm is a propagating pattern, not an air mass moving at 350 m/s |
| Debris | `RingGustFrontDebris` reads GustFront: automatic | none |
| Wet world, mist, rainbow | S3 rain history, S2 wet map, post-rain mist, rainbow: automatic | none |
| Lightning | `RingLightningEvent` renderers, thunder pools, Sunshine flash, sprite layer (**unchanged, no field clamp**) | `TryScheduleMegastormLightning` in `RingVolumetricCloudLayer.Lightning.cs` (beside `:155-157`): near strikes within 80 km at 0.3-1/s, xi -2..30 km; 10% "bolt from the blue" at xi -15..-5. Far flash field (3.3). Megastorm records are excluded from the natural scanner (`:184`). Sprite chance for kind >= 5 is 0.45 |
| Audio (S5) | Distant-storm sector gains (`RainSoundField.cs:237-256`) with the megastorm bearing; thunder pools with physical delays; shelter muffling; S3 gust-roar voices | Section 5.2: ground hum, wall singing, speed-aware air sound, the edge boom, tremor and wildlife hooks, PDA lines |
| Kind audit (from S4 WO-B) | | Helpers `IsFrontKind` (add 6) and `IsSevere(kind) => kind >= ExceptionalFront`. Sites: `RingVolumetricCloudLayer.cs:3426, 3461, 3505, 3671, 3679, 3998, 4044`; `.Lightning.cs:256, 346`; `RingLocalWeatherEffects.cs:562`; `RingSunshineCloudBridge.cs:1899`; `RingDistantRainCurtains.cs:203`; `CloudLayerTable.cs:104` |

### 5.1 Does the S3 arrival sequence still work at 350 m/s?
- **The maths is speed-agnostic** (everything goes through v_r), but its **distance caps** bind at 350 m/s:
  - band 6 km, so the gust front lasts 17 s;
  - approach reach 18 km, so the calm lasts 51 s;
  - the fat-drop band (a fraction of the band width) lasts a few seconds.
  - That is too short to read.
- **Decision: the megastorm gets time-based bands**, a per-kind rule in `StormArrivalMath` for kind 6 driven by "Gust front lead" (default 60 s):
  - band width `W = clamp(v x 0.75 x Lead, 2 km, 20 km)`: 15.75 km, about 45 s;
  - approach reach `clamp(v x 2.5 x Lead, 10 km, 60 km)`: 52.5 km, about 150 s.
- **The core and trailing durations stay geometric** (zone length / v), which is the point of a fast wall: a 71 s wall and a 16 min trailing rain.
- **Resulting phases at a fixed point:**

  | Phase | From | To | Length |
  |---|---|---|---|
  | Approach (calm) | T-194 s | T-45 s | 150 s |
  | GustFront | T-45 s | T+16 s | (0.35 of the band continues behind the edge) |
  | FatDrop | T-16 s | T+18 s | |
  | Core | T0 | T+71 s | |
  | Trailing | T+71 s | T+18.6 min | |
  | Clearing | T+18.6 min | | mist ramp at least 32 s; mist tau 20 min |

- At other speeds the 2-20 km and 10-60 km clamps keep the bands sane. At 25 m/s: band 2 km (80 s), calm reach 10 km (6.7 min).

### 5.2 Sound design (user decision 2026-10-29)
This replaces the first draft's non-physical choice (an undelayed roar and thunder delay capped at 4 s). The user chose this mix.

**Physics basis.**
- At 350 m/s the wall is slightly supersonic (Mach 1.02 against 343 m/s), so airborne sound cannot get ahead of it.
- Sound through the ring structure (floor and rim walls) travels at about 5-6 km/s, roughly 15x faster than the storm. The structure announces the wall long before anything airborne can.
- The structure-borne reach is exaggerated for drama.

**Phases.** Inputs are the distance to the edge and the time to arrival from the megastorm query. R is "Megastorm warning reach" (default 220 km); times are for 350 m/s.

| # | When | Distance | Heard and felt |
|---|---|---|---|
| 1 | from T-105 min | <= 10R (2,200 km) | A faint structure-borne **ground hum**, a 20-60 Hz bed. The **rim walls faintly sing**: a tonal layer, directional from the nearer wall and louder within 20 km of it. Barely noticeable at first: `HumLevel = smoothstep(10R, 0, d)^2` |
| 2 | from T-21 min | <= 2R (440 km) | The hum grows. **Still water shivers** (tremor hook). **Wildlife goes quiet**: `WildlifeQuiet` ramps 0 to 1 between 2R and R. The wall **flashes silently** on the horizon: the flash field is visible, but its thunder is not played while `AirSound` is Silent |
| 3 | T-194 s .. T-45 s | the existing calm (5.1) | The air is dead quiet: wind and ambient beds are ducked by the Approach weight. **Only the floor hums**, with subtle camera micro-shake |
| 4 | T-45 s | gust front | A brief **swell riding the outflow**. In about 41 m/s of forward outflow, sound moves at 384 m/s, faster than the wall. Reuses the S3 gust-roar voices (S3 doc 16.3) |
| 5 | T0 | the edge | **The boom (Mach pile-up)**: the accumulated sound arrives at once as a **rolling thunder wave of about 3-5 s**, then the wall roar bed takes over (1-2 s crossfade) |
| 6 | T0 onward | inside and behind | **Normal physical thunder delays** (`d / 343`, no cap) from the near strike stream and the thunder pools. The roar recedes with the wall. The hum decays to half at the edge and to 0 by xi 390 km |

**Speed-aware air sound.** It follows the menu speed through `M = v / 343`. `AirSound` is a pure function of (v, xi, outflow) with no stored state, so save/load and rewinds stay exact.

| Menu speed | Ahead of the wall | At the edge |
|---|---|---|
| M < 0.94 (below about 322 m/s) | `Leading`: the airborne roar (audible from about 80 km ahead, Doppler-compressed rise) and the distant thunder lead the storm with physical delays | no boom; the roar continues |
| 0.94 <= M < 1.0 | blend of Leading into Silent, so crossing 343 m/s on the menu is never a hard flip | partial pile-up; boom gain `smoothstep(0.94, 1.0, M)` |
| M >= 1.0 (default 350 m/s, M 1.02) | `Silent`: only the hum, plus the gust swell while `v < 343 + U_outflow` (about 384 m/s). Faster than that, the swell also joins the boom | `Boom` for `BoomSeconds`, then `Inside` |

The ground hum is independent of M and works at any speed. At slow speeds it can run for many hours before arrival, which is the intended long build-up.

**Defaults the user can change later** (not menu entries):
- The hum is audio plus subtle **camera micro-shake** (at most about 0.02 degrees at full hum; the boom adds a 3-5 s envelope peaking at about 0.1 degrees) and **water and foliage tremor**. **No controller rumble yet.**
- The boom is a **rolling wave of about 3-5 s** (`BoomSeconds` 4), not a single crack. 3 synthesised variants, picked by the passage hash.
- **No far-side echo around the ring.** An optional idea, recorded and rejected for now: airborne sound going backward takes about 25 h to come round (31,416 km at 343 m/s), so the storm would leave a constant faint rumble everywhere. That would undercut the build-up.

**Implementation** (S5 owner files):
- **Voices** in `scripts/Weather/RainSoundField.cs` and `RingWeatherAmbience.cs`:
  - a hum bed (one non-positional low-frequency voice);
  - wall singing (2 directional voices, one per rim wall);
  - a boom one-shot pool (3 variants);
  - the distant-storm sectors and the roar bed, gated by `AirSound`.
  - Shelter muffles airborne sound only. The hum and the singing are structure-borne and are not muffled indoors, which is where the hum reads best.
- **Assets** generated with `tools/media/weather_sfx_gen.py`, provenance in `resources/audio/weather/LICENSE.md`:
  - `megastorm_ground_hum_loop`, `megastorm_wall_sing_loop`;
  - `megastorm_boom_[1-3]` (3-5 s rolling thunder with a low-frequency transient);
  - `megastorm_roar_loop`.
- **Tremor.** One global, `ring_ground_tremor` (amplitude, frequency) = `HumLevel x 0.6` plus the boom envelope. It is 0 when inactive, so the identity is exact. Readers:
  - the S2 water ripple layer (puddles and still water; the `RingWetWorld` ripple atlas): a high-frequency shiver;
  - the ocean proxy and global materials: a fine normal jitter;
  - the vegetation wind path (tree and grass gust term): a small high-frequency sway.
  - The camera micro-shake is a new small additive term in the explorer camera driven by the same value. No existing camera-shake or reduced-motion path was found, so that is a gap to note.
- **Wildlife.** No bird or insect ambience beds exist in code today. `WildlifeQuiet` is published for any present or future life bed to multiply in; the S3 `birds_after_rain_loop` idea should honour it.

### 5.3 Lore hooks (hooks, not canon; the user will develop the lore)
- "When the floor hums, the wall is coming." The ring structure carries the storm's pounding ahead of it, and the walls sing.
- The singing walls are a natural seam for later lore (Wall Engines, resonance, old warnings). Nothing here fixes canon.

### 5.4 PDA lines and database entry
The suit PDA explains the hum. This reuses the announcer from `docs/eclipse-false-sun-spec.md` sections F.3-F.4:
- `PdaAnnouncer` in `scripts/Pda/`;
- `IPdaLineProvider`, `PdaRequest`, `IPdaVoiceOutput`;
- authored tables in `resources/pda/*.json`, with the schema of `prototypes/pda-voice/events.json`: id, category, priority (0 critical, 1 high, 2 normal, 3 low), fallback, facts, terms;
- critical (priority 0) lines skip the LLM rewrite.

The voice follows `prototypes/pda-voice/README.md`: calm, concise, dry, slightly clinical.

**Triggers and rate limits.** Triggers read `MegastormSample` for the player's position. One approach is an **episode**, keyed by the passage (anchor plus lap index). Each id speaks at most once per episode, except `megastorm_resonance_rising` (at most 2, at least 10 min apart).

| id | Trigger | Priority | Critical | Facts |
|---|---|---|---|---|
| `megastorm_resonance_detected` | `HumLevel` first above 0.05 in the episode (1,530 km, about T-73 min at 350 m/s). Also unlocks the database entry | 2 | no | distance_km, bearing |
| `megastorm_resonance_rising` | `HumLevel` crosses 0.25, then 0.5 (about T-52 and T-38 min) | 3 | no | distance_km, eta_minutes |
| `megastorm_resonance_rising_far` | same thresholds when the ETA is over 180 min (slow menu speeds) | 3 | no | distance_km, eta_hours |
| `megastorm_fauna_silent` | `WildlifeQuiet` first above 0.8 (about T-13 min) | 3 | no | eta_minutes |
| `megastorm_airborne_approach` | `AirSound` Leading and the roar audible (subsonic menu speeds only) | 2 | no | distance_km |
| `megastorm_acoustic_dead_zone` | Approach weight above 0.3 while `AirSound` is Silent (about T-3 min) | 2 | no | seconds |
| `megastorm_gust_warning` | GustFront weight rising past 0.3 (about T-45 s) | 0 | yes | wind_ms |
| `megastorm_shelter_inadequate` | T-60 s while the observer's shelter class is Open (5.5.1) | 0 | yes | kpa |
| `megastorm_wall_arrival` | `TimeToArrival` <= 10 s, spoken before the boom so it is not drowned | 0 | yes | seconds, kpa |
| `megastorm_bullet_rain` | rain impact power above 2 kW/m² and exposure above 0.3 (5.5.2) | 0 | yes | speed_ms |
| `megastorm_wake_warning` | xi 20 km, about T+57 s (5.5.3) | 0 | yes | kpa |
| `megastorm_scythe_warning` | first scythe within 5 km while the observer's exposure is above 0.3 (5.5.4) | 0 | yes | none |
| `megastorm_passage` | entering Trailing (xi 50 km, about T+143 s) | 2 | no | minutes |
| `megastorm_all_clear` | entering Aftermath (about T+18.6 min) | 2 | no | distance_km |
| `megastorm_event_resync` | load or jump into an episode | 3 | no | phase |

**Rate limits** on top of the eclipse queue rules (stale after 8 s, 4 s between lines, 60 s per id, critical lines pre-empt, subtitles always shown, on Resync only the resync line and no backlog):
- At most one non-critical megastorm line per 5 min during the approach. A 105-minute approach at 350 m/s yields about 9 lines in total.
- Critical lines are never rate-limited.
- Inside a shelter, the gust and wall lines still play; they are suit warnings.

**Draft `resources/pda/megastorm_lines.json`:**

```json
{
  "_note": "Megastorm PDA lines (draft, docs/megastorm-design.md 5.4). priority: 0 critical (skips LLM), 1 high, 2 normal, 3 low. Triggers live in the doc, not here.",
  "events": [
    { "id": "megastorm_resonance_detected", "category": "megastorm", "priority": 2,
      "fallback": "Low-frequency structural resonance detected in the ring floor. Source bearing {bearing}, about {distance_km} kilometres.",
      "facts": { "distance_km": 2000, "bearing": "spinward" }, "terms": ["resonance"] },
    { "id": "megastorm_resonance_rising", "category": "megastorm", "priority": 3,
      "fallback": "Structural resonance increasing. Source {distance_km} kilometres out. Estimated arrival in {eta_minutes} minutes.",
      "facts": { "distance_km": 1100, "eta_minutes": 52 }, "terms": ["resonance", "arrival"] },
    { "id": "megastorm_resonance_rising_far", "category": "megastorm", "priority": 3,
      "fallback": "Structural resonance increasing. Source {distance_km} kilometres out. Estimated arrival in {eta_hours} hours.",
      "facts": { "distance_km": 1100, "eta_hours": 12 }, "terms": ["resonance", "arrival"] },
    { "id": "megastorm_fauna_silent", "category": "megastorm", "priority": 3,
      "fallback": "Local fauna has gone silent. Standing water is showing surface tremor. Arrival in {eta_minutes} minutes.",
      "facts": { "eta_minutes": 13 }, "terms": ["fauna", "tremor"] },
    { "id": "megastorm_airborne_approach", "category": "megastorm", "priority": 2,
      "fallback": "Airborne storm noise detected, {distance_km} kilometres. Front approaching.",
      "facts": { "distance_km": 80 }, "terms": ["storm", "front"] },
    { "id": "megastorm_acoustic_dead_zone", "category": "megastorm", "priority": 2,
      "fallback": "Airborne acoustic dead zone. The front is outrunning its own sound. Arrival in {seconds} seconds.",
      "facts": { "seconds": 180 }, "terms": ["front", "sound"] },
    { "id": "megastorm_gust_warning", "category": "megastorm", "priority": 0,
      "fallback": "Warning: gust front. Wind rising to {wind_ms} metres per second. Seek cover.",
      "facts": { "wind_ms": 41 }, "terms": ["gust", "cover"] },
    { "id": "megastorm_shelter_inadequate", "category": "megastorm", "priority": 0,
      "fallback": "Current position is exposed. Predicted overpressure {kpa} kilopascals. Find enclosed shelter.",
      "facts": { "kpa": 60 }, "terms": ["overpressure", "shelter"] },
    { "id": "megastorm_wall_arrival", "category": "megastorm", "priority": 0,
      "fallback": "Storm wall in {seconds} seconds. Overpressure wave of {kpa} kilopascals incoming.",
      "facts": { "seconds": 10, "kpa": 60 }, "terms": ["wall", "overpressure"] },
    { "id": "megastorm_bullet_rain", "category": "megastorm", "priority": 0,
      "fallback": "Warning: rain impact velocity {speed_ms} metres per second. Exposed surfaces will erode. Get behind cover.",
      "facts": { "speed_ms": 140 }, "terms": ["rain", "cover"] },
    { "id": "megastorm_wake_warning", "category": "megastorm", "priority": 0,
      "fallback": "Pressure trough behind the wall. Wind reversal imminent. Pressure dropping by {kpa} kilopascals. Stay low.",
      "facts": { "kpa": 8 }, "terms": ["pressure", "reversal"] },
    { "id": "megastorm_scythe_warning", "category": "megastorm", "priority": 0,
      "fallback": "Lateral discharge activity. Plasma sweeps at ground level. Get low.",
      "facts": {}, "terms": ["plasma", "low"] },
    { "id": "megastorm_passage", "category": "megastorm", "priority": 2,
      "fallback": "Storm wall has passed. Trailing rain expected for {minutes} minutes.",
      "facts": { "minutes": 16 }, "terms": ["wall", "rain"] },
    { "id": "megastorm_all_clear", "category": "megastorm", "priority": 2,
      "fallback": "Precipitation has ended. The front is {distance_km} kilometres past and receding. Structural resonance fading.",
      "facts": { "distance_km": 390 }, "terms": ["front", "resonance"] },
    { "id": "megastorm_event_resync", "category": "megastorm", "priority": 3,
      "fallback": "Storm front event in progress: {phase}.",
      "facts": { "phase": "trailing rain" }, "terms": ["storm"] }
  ]
}
```

The fact values above are example values for the LLM validator. At runtime they are filled from the query.

**PDA database entry (DRAFT, for the user to edit)**, `resources/pda/megastorm_database.json`:
- id `megastorm_structural_resonance`, title "Structural resonance";
- unlocked by the first `megastorm_resonance_detected`. It is a hook only: until a PDA database exists, the unlock is persisted as `MegastormDocument.ResonanceEntryUnlocked`.

> **Structural resonance.** *(Draft.)*
> The ring floor is a continuous solid structure. It conducts mechanical energy at five to six kilometres per second, about fifteen times faster than sound moves through air.
> Storm fronts of sufficient scale load the floor continuously: pressure, downbursts, lightning. The structure carries that energy far ahead of the front. It is perceived as a low hum underfoot. The rim walls resonate at higher frequencies and are often heard first.
> Field note: when the floor hums, the wall is coming.
> Airborne sound behaves differently. The observed front advances at approximately 350 metres per second, slightly faster than sound in air, so nothing it produces can travel ahead of it. Observers in its path report an acoustic dead zone in the final minutes: still air, no thunder, only the floor.
> Every sound the front produced while closing on an observer reaches that observer at once, as the wall arrives. Expect a rolling pressure wave several seconds long, followed by the sustained roar of the storm.
> The arrival is not only sound. The pile-up is a true overpressure wave, strong enough to flatten unreinforced structures. Unsheltered exposure is not considered survivable. Enclosed shelter, or at minimum the lee of solid terrain, reduces it to tolerable levels.
> Inside the wall, wind-driven rain strikes at projectile speeds. Exposed surfaces erode; poorly maintained structures fail first.
> Behind the wall the air pressure collapses. Expect a violent wind reversal, sudden fog, and pressure pain in the ears, followed by inflow toward the departing wall. Stay low and anchored until the trough has passed.
> Lightning inside the wall does not stay vertical. The front outruns its own discharges and drags each channel sideways into a sheet of plasma that skims the ground for kilometres. Anything standing is in its path. Get low; a trench or hollow is better.
> Discharges from the tops of the wall do not escape upward. They climb to the canopy and end there. The canopy visibly takes the lightning, flaring along its underside where each bolt lands.
> Recommendation: treat any detectable floor resonance as advance warning. Reach enclosed, rated shelter before the dead zone begins, and stay there until the pressure recovers.

**Dependency.** The lines need `PdaAnnouncer` from the eclipse spec, which has not landed (`scripts/Pda/` and `resources/pda/` do not exist yet).
- **Recommendation: ship subtitles-only through the same interfaces rather than defer.** The hum's meaning depends on the PDA naming it; without the lines the build-up is just an unexplained noise.
- If the announcer is still missing when megastorm audio is built, the megastorm WO builds the minimal, non-eclipse-specific core the spec already defines:
  - the `PdaAnnouncer` queue and rate rules;
  - `AuthoredPdaLineProvider`;
  - `SubtitleOnlyVoiceOutput`;
  - the JSON loader;
  - the `PdaEnabled` and `PdaSubtitles` settings.
- No LLM and no TTS. The eclipse work then adds its table, the LLM provider and voice output on top, with no rework.

### 5.5 Hazards (user input 2026-10-29)

Status by hazard:
- the boom (5.5.1): **decided** 2026-10-29 (60 kPa open; x0.6 lee, x0.25 indoors; about 50 kPa unsheltered lethal; glass 7, concrete 25, reinforced 80 kPa);
- bullet rain (5.5.2) and the vacuum wake (5.5.3): **decided** 2026-10-29;
- lightning scythes (5.5.4) and field strikes (5.5.5): the user's design with draft numbers.

> User, verbatim: "if the player survives the initial sonic boom that would instantly flatten any non-viable infrastrucutre. rain would be as fast as bullets tearing through player the player and player made infrastructure if not well maintained. the vacuum wake after the storm would also be another issue to worry about. virtical lightning would be another thing."

**Decided (user, 2026-10-29): "this is all just planning for now."** Hazards stay hooks plus visuals and audio. No damage, health or survival systems are built.

**Scope.** "Spectacle plus hooks" still holds. Each hazard is designed fully, but what ships now is query values, events, visuals and audio. Future damage, survival and building systems consume those values.

**What exists today:**
- **No player health, damage, vitals or survival system.** Grep finds no health, damage, HP or vitals code; `scripts/player_controller.gd` has none.
- **Player-made structures do exist**: SmoothConstruction blocks (`scripts/Terrain/Editable/SmoothConstructionContracts.cs:10-17`, materials Concrete and Glass, journaled `SmoothConstructionCommand`s persisted by `RingWorldSaveManager`). They carry **no rating, condition or maintenance state**.
- So every hazard below is hooks plus visuals and audio. Nothing is damaged or removed.

**Structure hook** (for the future building system; not implemented by the megastorm):

```csharp
public interface IMegastormStructureSource {   // registered by a future building/survival system; null today
    int CollectNear(RingCanonicalPosition centre, double radiusMeters, Span<MegastormStructure> output);
}
public readonly record struct MegastormStructure(long Id, RingCanonicalPosition Base, float HeightAboveGroundMeters,
    float RatedOverpressureKPa /* "viable" when >= the local peak */, float MaintenanceFactor /* 0 neglected .. 1 well kept */, bool Enclosed);
```

Ratings for when a consumer exists (decided 2026-10-29):
- Glass 7 kPa; Concrete block 25 kPa; a future "reinforced" class 80 kPa.
- Authored POIs are ancient ring structures and count as viable: they are never flattened.

#### 5.5.1 The boom (shock overpressure)
- **Zone and timing.** At the edge, xi = 0 (T0). It is a rolling 3-5 s wave (the 5.2 boom) with three pulses at t = 0.3 s (peak), 1.2 s (0.5x) and 2.6 s (0.3x). At 350 m/s the shock band is about 1.4 km thick. It only exists when M >= 0.94; boom gain as in 5.2.
- **Numbers (decided 2026-10-29)** (open ground):
  - peak overpressure **60 kPa**, positive phase 0.6 s, impulse about **18 kPa·s**;
  - +-15% along the wall by cell hash; strongest under overshooting cells.
  - Draft shelter attenuation (via the S5 shelter class): enclosed x0.25 (15 kPa), lee of terrain or a wall x0.6 (36 kPa), open x1.
  - Draft survival line for the future health system: about 50 kPa unsheltered is lethal. So "survive the boom" means: be in the lee or inside.
- **Structures.** A structure is flattened when `RatedOverpressureKPa` is below the local peak. That is a consumer decision; the megastorm only publishes the peak and the impulse.
- **Query** (`MegastormSample.Hazards`): `OverpressureKPa` (instantaneous, open-air), `BoomPeakKPa`, `BoomImpulseKPaS` for the passage at that position, `BoomTimeSeconds` (when the peak hits that position).
- **Events.** `BoomPassed(EpisodeId, position, peakKPa, impulseKPaS, time)`, edge-triggered for the observer and the registered structures. There is also a pure `BoomAt(position)` for any point.
- **Visuals.**
  - A dust and debris shock ring on the ground travelling with the edge: a burst mode of `RingGustFrontDebris`, which spawns a dense 4 s sheet at the observer's xi = 0.
  - A pressure-wave distortion: a short screen-space refraction band, new `scripts/render/RingShockDistortionComposite.gd`, dispatched only during the 4 s window.
  - **Trees flattened in a band:** a new global `ring_megastorm_blast` (front position, strength) read by the tree and grass wind geometry path (`ring_tree_wind_geometry`, `materials/ring_tree.gdshader:39-42`; the standing-grass wind). It drives a bend impulse to about 75 degrees with a slow, partial spring-back while the bullet-rain wind holds them down. Permanent felling would be a future world edit.
  - Water surfaces slammed: a ripple burst on the S2 ripple layer and an ocean normal impulse through `ring_ground_tremor`.
- **Audio and shake.** The first boom pulse, the camera-shake peak (0.1 degrees) and `OverpressureKPa`'s peak all come from one `BoomEnvelope(t)`, so they coincide by construction.
- **Draft PDA lines (critical):**
  - `megastorm_shelter_inadequate` (T-60 s, only while the observer's shelter class is Open): "Current position is exposed. Predicted overpressure {kpa} kilopascals. Find enclosed shelter."
  - `megastorm_wall_arrival` (now also carries kpa): "Storm wall in {seconds} seconds. Overpressure wave of {kpa} kilopascals incoming."
- **Lands in:** WO10 (maths, query, events), WO11 (visuals).
- **Cost.** Debris burst within the existing gust debris budget (<= 0.15 ms, 4 s only); distortion pass <= 0.1 ms during the window; blast global 0 when inactive.

#### 5.5.2 Bullet rain
- **Zone and timing.**
  - Wind ramps from the 41 m/s gust front to the peak by xi 2 km (T+6 s).
  - It holds through xi 15 km (T+43 s), then decays to 40 m/s by xi 25 km (T+71 s).
  - Rain intensity follows the W record (heavy tier, veil on).
- **Wind profile (DECIDED 2026-10-29: peak 140 m/s)** (replaces the S3 sustained 37 m/s inside the wall for kind 6):
  - peak **140 m/s** at 10 m above ground, directed forward (outflow / rear-inflow jet: from the wall into the face of an observer who watched it approach);
  - `RingWindQuery` carries it with height (power law to 300 m, the jet above).
  - Drop speed is about the wind speed: a 3 mm drop at 140 m/s carries about 0.14 J.
  - At an extreme 200 mm/h (liquid water about 8 g/m³), the impact power on a windward surface is `0.5 x LWC x v^3`, about **11 kW/m²**.
  - "As fast as bullets" taken literally is 300+ m/s. The peak is a single model constant, so the user can push it; 140 m/s is the draft.
- **Damage hook (consumer side).** `damage rate ∝ RainExposure x RainImpactPowerWm2 x (1 - 0.8 x MaintenanceFactor)` for structures, and `∝ RainExposure x power` for the player (shelter and lee reduce exposure).
- **Query:** `RainImpactPowerWm2`, `RainImpactDirection` (ring frame), `RainDropSpeedMs`, `RainExposure`. Exposure is observer-only, from the wind-aligned cover probe below.
- **Events:** none (continuous).
- **Renderer work** (rain-streak owner files):
  - `ring_rain_streaks.glsl:158-161` clamps the slope at `F.rain2.y`. Raise the clamp for kind 6 to about 16 (140 m/s over about 9 m/s fall).
  - Above a slope of about 3, switch the lattice to a **wind-aligned mode**: the drop period runs along the wind, not vertically, so drops do not spawn outside the near footprint and leave gaps.
  - Streak length = speed x 1/60 s, clamped to 2.5 m, with the min width kept.
  - Sheltering: the top-down precipitation occlusion map cannot shelter the lee side of a wall from horizontal rain. Add a wind-aligned depth test (streak fragments behind scene depth along the travel) for visuals. For `RainExposure`, extend `PrecipitationCoverProbe` with 8 rays along the wind.
  - Impact spray on windward faces: screen-space, from depth-buffer normals in the streak composite (a new draw mode beside the S2 drip hook), shown as bright spray and spark-like flecks.
- **Audio.** A "rain hammer" bed on structures, from the windward sector, using the S5 roof and metal voices at a new top tier plus `megastorm_rain_hammer_loop`. Wind roar at full scale.
- **Draft PDA line (critical):** `megastorm_bullet_rain` (rain power above 2 kW/m² and exposure above 0.3): "Warning: rain impact velocity {speed_ms} metres per second. Exposed surfaces will erode. Get behind cover."
- **Lands in:** WO10 (profile, query, cover probe), WO11 (streak modes, spray, audio).
- **Cost.** The streak budget is unchanged (same counts; the wind-aligned mode replaces the vertical one). Spray <= 0.1 ms in the core only. Cover probe <= 0.05 ms CPU at 4 Hz.

#### 5.5.3 The vacuum wake
- **Zone and timing:** xi 25-60 km behind the wall, T+71 s to T+171 s at 350 m/s. The trough centre is at xi 35 km (T+100 s).
- **Model (DECIDED 2026-10-29: directions backward on the wall side, then inflow toward the departing wall).** The wall's updrafts evacuate air faster than it refills behind the moving wall. That leaves a deep trough just behind its back face, and air rushes into it from both sides. For an observer:
  - xi 25-35 km (about 29 s): the wind **reverses**. It swings from the 140 m/s forward core flow to a **backward** flow of up to 60 m/s, toward the trough.
  - Trough centre: brief calm; pressure deficit about **-8 kPa** (draft, exaggerated); **condensation fog**.
  - xi 35-60 km (about 71 s): trailing air rushes **forward toward the departing wall** at up to 50 m/s. That is the suction / pull toward the wall.
  - The pressure falls at about 0.3 kPa/s into the trough, which drives the ear-pop hook.
- **Query:**
  - `PressureDeficitKPa`, `PressureRateKPaPerS` (ear pop and breathing hooks);
  - `WakeFog` (0..1);
  - the reversed wind is already in `WindRingFrame` and `RingWindQuery`.
  - Danger adds `0.6 x smoothstep(2, 6, deficit)`.
- **Events:** `WakeEntered` and `WakeExited` (observer, edge-triggered from xi).
- **Visuals.**
  - Fog via the S3 post-rain mist path (`RingAtmosphereModel.SetWeatherMist`; slew 2/32 per publish, so onset is at least 16 s). The megastorm contributes `M = WakeFog` alongside Clearing, and the larger wins.
  - Dust and debris are dragged backwards automatically: gust debris follows the wind query, including direction.
  - Vegetation snaps from the flattened pose toward the reversed wind.
- **Audio.** A sucking roar on the reversal, then a dull low-passed hush plus an ear-pop one-shot when `|PressureRateKPaPerS|` exceeds 0.2. The ear pop is cosmetic and on by default (default the user can change).
- **Draft PDA line (critical):** `megastorm_wake_warning` (xi 20 km, about T+57 s): "Pressure trough behind the wall. Wind reversal imminent. Pressure dropping by {kpa} kilopascals. Stay low."
- **Lands in:** WO10 (wake wind, pressure, query), WO11 (fog, audio).
- **Cost:** 0 GPU (mist path); one more term in the wind modifier (< 5 us).

#### 5.5.4 Lightning scythes (user design 2026-10-29; replaces "vertical lightning")

> User, verbatim: "Lightning usually travels vertically from cloud to ground. At Mach 1, the cloud moves faster than the plasma channel can discharge. Horizontal Lighting Scythes: The lightning strikes would get dragged sideways, stretching out into miles-long, horizontal sheets of plasma that skim just above the ground."

- **Mechanism.**
  - A strike attaches cloud-to-ground at point A.
  - The wall carries the channel's cloud end forward at about 350 m/s. The channel is dragged sideways and lies down into a near-horizontal sheet or arc of plasma skimming just above the ground.
  - Continuing current and repeated return strokes keep it alive and lengthen it. The sheet runs from A **forward, in the storm's direction of travel**, trailing behind the moving cloud foot; its leading tip is the cutting edge.
  - **Honesty note:** real continuing current lasts about 0.1-0.5 s, so a 350 m/s cloud would stretch a real channel a few hundred metres at most, and it would not hug the ground. Kilometre-long, ground-skimming sheets are our exaggeration, in the same spirit as the hum's reach.
- **Draft numbers:**

  | Item | Draft value |
  |---|---|
  | Sheet length | 2-5 km (median 3.5 km) |
  | Tip sweep speed | 1.5-2.0 km/s along the ground. Exaggerated: the wall's drag sets the direction, not the speed |
  | Lifetime | 1-3 s (median 2 s) |
  | Re-strokes | 3-8 return strokes, 0.2-0.5 s apart; each re-brightens the whole sheet and extends the tip |
  | Sheet shape | A horizontal sheet: 2-5 km long (along the motion) by 50-200 m wide, vertical thickness 2-8 m |
| Skim height | Lower edge 0.5-4 m above the *smoothed* ground (100 m smoothing), so trenches, hollows and ditches lie below it |
  | Attachment zone | xi -15..20 km: the gust front, shelf and wall, T-43 s to T+57 s. Density peaks at xi 0-10 (T0 to T+29 s, inside the bullet rain) |
  | Rate | 20 per second along the whole 318 km wall (draft, exaggerated), scaled by "Lightning frequency" and its "Reduced flashes" policy. About one every 7 s within 5 km of the observer during the zone |
  | Per-passage cut odds (exposed) | standing about 2.6%, crouched about 0.4%, prone or in a trench 0, a 10 m tree or structure about 8% (draft; a future consumer can scale them) |

- **Hazard: a sweep test** (replaces the upward-strike / height-squared mechanic). A scythe cuts anything whose top stands above the sheet's lower edge along its path: tall structures, trees, a standing player. Staying low or in a trench helps, which ties into the "stay low" theme of the wake.
  - `ScytheCutProbability(position, heightAboveGroundMeters, exposure, t, dt) = ScytheSweepRatePerSecond(position, t) x dt x F(h_eff) x exposure`.
  - `h_eff` = height above ground minus the depth of the local ground below the smoothed skim surface (a trench is negative).
  - `F` = the share of scythes whose lower edge lies below `h_eff`: `smoothstep(0.5, 4, h_eff)`.
  - So: prone (0.4 m) or in a trench 0; crouched (1 m) about 0.05; standing (1.8 m) about 0.33; a 10 m tree or structure 1.
  - Swept footprint per scythe is about 3.5 km x 125 m. At 20/s over the 35 km x 318 km zone, a point is crossed about 0.08 times per passage. That gives the per-passage odds in the table.
  - Enclosed shelter gives exposure 0. Structures report their height through `IMegastormStructureSource`.
- **Query:**
  - `LocalStrikeRatePerSecond` (ordinary strikes within 30 km);
  - `ScytheSweepRatePerSecond` (expected scythe paths crossing this point per second);
  - `NearestScytheDistanceMeters`;
  - method `ScytheCutProbability(...)`.
- **Events.** `RingLightningEvent` gets a new `Scythe` flag (the attachment strike).
  - `ScytheStarted(EventId, attachment, direction, start)`.
  - Pure `MegastormLightning.ScythePath(EventId, t)`: a 12-16 point polyline with skim heights and the tip position at t, plus the stroke pulse.
  - `ScytheEnded(EventId)`.
  - The window query `ScythesInWindow(t0, t1)`.
  - All deterministic and rewind-safe (WO7 rules).
- **Containment field.** Scythes skim the ground and never reach the field. The wall's discharges into the field are the separate **field strikes** class (5.5.5). Besides those:
  - the wall's ordinary cloud-to-ground strikes near the rims couple through the field's `LightningDisturbanceAdapter` (parent top = the ceiling there, gap about 0);
  - the near-rim contact arcs (5.5.5) light the `MegastormContact` ripples through `FlashLevel` (6.3).
  - The old giant-bolt `ReachesField` flag is withdrawn.
- **Rendering** (new; the existing bolt renderer only draws cloud-base-to-ground channels):
  - A **persistent sheet/arc mode**: a terrain-following horizontal ribbon (50-200 m wide) along the path polyline at skim height, with fractal branch filaments off both edges and a per-re-stroke flicker from the `RingLightningEvent` pulse maths (reduced-flash envelope respected). The attachment stroke itself uses the existing bolt renderer.
  - **Ground illumination along the path:** for the nearest scythe only, 3 shadowless `OmniLight3D`s ride the polyline (tip, middle, attachment). The Sunshine and haze lightning flash gets the sheet's centroid as its flash position, so the light visibly sweeps.
  - A **scorch and dust line**: a transient emissive-then-dark decal strip along the skim line, fading over 30 s, and a gust-debris dust burst along it.
  - Far: within the wedge pass, the flash field draws scythes as short bright horizontal streaks at the wall base.
  - Cap: 4 detailed scythes (nearest); others as cheap glow lines.
- **Audio.** A **ripping, tearing crackle along the path** rather than one clap:
  - a crackle voice travels with the tip;
  - each re-stroke fires a tear one-shot per path segment, and each segment's sound arrives at its own `d / 343`, so the rip smears naturally;
  - the far rumble pool handles scythes beyond 5 km.
- **Draft PDA line (critical when exposed):** `megastorm_scythe_warning`, fired by the first scythe within 5 km while the observer's exposure is above 0.3 (once per episode): "Lateral discharge activity. Plasma sweeps at ground level. Get low."
- **Lands in:** WO12.
- **Cost.**
  - Ribbon and branches <= 0.05 ms per detailed scythe; the 3 omni lights about 0.1 ms for the nearest; the decal strip negligible.
  - Cap 0.3 ms total, and only while the observer is within 25 km of the attachment zone.
  - CPU path generation <= 0.02 ms per event.
  - 0 when untriggered.

#### 5.5.5 Field strikes (user design 2026-10-29: "we can also have lightning touch the containment field")
- **What.** A megastorm discharge class: **upward discharges from the wall's 15-18 km tops that terminate on the containment field.** Think gigantic jets that end on the canopy.
  - Real gigantic jets reach 70-90 km, so a field at 18-40 km is well within reach.
  - Not limited to the rims: they happen anywhere across the width where the field is above the tops.
  - Channel length `L = H(across) - top`, with H from `FieldUndersideProvider` (6.3) and top = the local strip column top.
  - With no field (null provider) there are no field strikes.
- **Channel length across the width** (Certified, 10.0 km wall top, apex 40 km, tops 18 km):

  | Distance from the rim wall | Field H | L |
  |---|---|---|
  | 6.2 km (the ceiling contact) | about 18.3 km | about 0.3 km: contact arcs |
  | 10 km | about 20.5 km | about 2.5 km |
  | 20 km | about 24.6 km | about 6.6 km |
  | 40 km | about 29.9 km | about 11.9 km |
  | centre (159 km) | 40 km | about 22 km |

- **Near the rims: contact arcs.** Where the gap is under 0.5 km (the band where the wall presses the field), field strikes become **short, frequent contact arcs**:
  - 50-500 m long, 0.2-0.5 s, about 2 per second per side;
  - **visual-only**: they set the `FlashLevel` of the `MegastormContact` ripples and publish no separate disturbance, which respects the field's 4/s contact cap and 6/s spawn budget.
- **Draft numbers (gap >= 0.5 km):**

  | Item | Draft value |
  |---|---|
  | Rate law | `0.05 per minute per km of wall x (1 + 9 x exp(-gap / 2 km))`: 0.05 at the centre, 0.18 at 10 km from a rim, 0.35 at 7 km |
  | Total | about 18 per minute along the whole 318 km wall: one every ~3 s somewhere along its skyline |
  | Rate scaling | "Lightning frequency" and "Reduced flashes" |
  | Attachment band | xi 3-18 km (overshooting cells; the base point is the hashed dome top) |
  | Leader rise | about 50 km/s (draft), so 0.05-0.45 s depending on L |
  | Attached channel | 0.4-1.0 s with 1-3 re-pulses |
  | Fade | 0.4 s |
  | Total lifetime | about 0.8-1.9 s |
  | Look | Lower 30% of the channel a white-blue leader out of the dome top. Upper section deep blue, widening into a gigantic-jet "tree" fan 3-8 km wide just below the field. A violet-white terminal spray where it meets the field. Much brighter than sprites |
  | Field response | Designed in `docs/containment-field-design.md` (the `FieldStrike` response, by the field doc's author). The channel stops 50 m below H; the field draws what happens on it |

- **Relation to the existing sprite/jet layer.** This is a separate megastorm class with its own id space (salt) and its own render layer. It is not derived from `UpperAtmosphereLightning`, and sprites and natural jets stay unchanged (user decision).
  - **Decided (user, 2026-10-29):** blue and gigantic jets above the megastorm are kept and keep passing through the field, as all sprites and jets do today. Field strikes are the only discharges that end on the canopy.
- **Shared with natural storms** (field doc 3.10, 3.10.6, WO14; user decision "ordinary storms can do this if close to a wall"). The `FieldStrike` flag and the channel layer are **shared**. The natural scheduler also sets the flag, through a deterministic roll in `CreateLightningEvent`, for strong Cb within about 10 km of a wall. Natural channels are short (0.3-3 km).
- **Event convention** (one convention for every source): for a `RingLightningEvent` with the `FieldStrike` flag,
  - **`GroundPosition` = the field contact point** (around, H(across), across);
  - **`CloudPosition` = the channel base** (the tower or dome top);
  - **per-event duration.** `RingLightningEvent.LifetimeSeconds` is a constant 0.72 s (`scripts/Lightning/RingLightningEvent.cs:19`), and active events are pruned by it (`RingVolumetricCloudLayer.Lightning.cs:146`). Flagged events need a per-event `DurationSeconds` (default `LifetimeSeconds`), so a megastorm field strike lives its 0.8-1.9 s. Prune by it and give `PulseAtAge` a field-strike envelope (re-pulses, reduced-flash aware). This is a small shared change in WO13.
- **ContainmentDisturbance.** Kind **`FieldStrike`** (the field doc owns the enum).
  - The field's `LightningDisturbanceAdapter` publishes it for **any flagged event** in `ActiveLightningEvents`, whatever the source:
    - `Position` = `GroundPosition` (the contact point);
    - `Energy`;
    - `FlashLevel` = the event's own pulse;
    - `SourceId` = EventId.
  - The field picks the patch size itself (Rmax 6-15 km, T 3 s, crawl and glow <= 1 s; field doc 3.5 / 3.10).
  - **Megastorm field strikes are therefore published through the adapter.** Every megastorm field strike within reach is added to `ActiveLightningEvents` with the flag. A direct push with the same `SourceId` is optional, because the bus dedupes by SourceId. This design drops it, keeping a single path.
  - About 0.3/s for the whole wall, inside the field's strike budget (field doc 3.10.4: 2/s, 4 reserved slots).
- **Query:**
  - `FieldStrikeRatePerSecond`: the rate law integrated over +-30 km of wall around the position's across;
  - `FieldGapMeters`: H minus the top at this across; NaN without a field.
- **Events.**
  - `FieldStrikeStarted(EventId, topPosition, contactPosition, lengthMeters, energy, start)` and `FieldStrikeEnded(EventId)`;
  - a `FieldStrike` flag on `RingLightningEvent`;
  - the pure `MegastormLightning.FieldStrikesInWindow(t0, t1)`.
  - Deterministic and rewind-safe (WO7 rules).
- **Visuals.**
  - New **source-agnostic** `scripts/Lightning/RingFieldStrikeLayer.cs` plus `materials/ring_field_strike.gdshader`. It draws **every flagged event in `ActiveLightningEvents`**, whatever the system kind: megastorm channels (2.5-22 km, fan and terminal spray) and natural channels (0.3-3 km, a scaled-down fan).
  - It is a billboard-chain channel from `CloudPosition` to `GroundPosition`, following the `RingUpperAtmosphereLightningLayer` pattern (air-dimmed, remote-day factor, regular haze), with **two instance sets**:
    - **main view** for strikes within the near handoff, seen above the wall from ahead;
    - **far viewport** beyond it, out to the antipode, with a minimum angular size of 0.3 degrees.
  - The wedge pass adds a dome-top glow at the base point.
  - **The reveal.** At night, from the ground ahead of the wall and across the arch, the canopy's existence shows as bolts that stop dead in empty sky and flare.
    - The canopy flare beyond about 400 km is drawn by the field's own far strike layer (field doc 3.10.3, field WO13). This layer's far set draws only the channel.
- **Audio.** Strikes 20+ km overhead are heard as a delayed long roll, through the far thunder pool with physical delays. Contact arcs near the rims give a continuous sizzle bed when within 10 km of a rim wall.
- **PDA.** No new line (not requested). The lore entry covers it.
- **Lands in:** WO13.
- **Cost.**
  - <= 0.03 ms per detailed channel, at most 4 at once; the far instance set <= 0.05 ms;
  - CPU scheduling <= 0.01 ms per frame;
  - 0 when untriggered or when there is no field.

**Danger** (6.1) adds `max(OverpressureKPa / 50, RainExposure x smoothstep(1, 8, power kW/m²), wake term)`, so a future HUD can use one number.

### 5.6 Megastorm darkness (user decision 2026-10-29: "add the earlier design to the doc make it very dark")
This is a megastorm-only darkness curve beyond today's natural maximum (D = 1). Ordinary storms keep their look exactly. It reaches every storm-light receiver:
- terrain, ocean, trees, grass, POIs and construction;
- the StandardMaterial3D tint;
- the sky and haze;
- the Sunshine cloud lighting at the camera.

**Today's maxima** (S0, defaults; "Storm darkness" s = 0.9; `materials/ring_storm_light.gdshaderinc:132-166`):

| Term | At D = 1 |
|---|---|
| Sun residual (1 - s) | 0.10 |
| Ambient A(D) = 1 - 0.55 s D | 0.505 |
| Sky / haze radiance 1 - 0.97 s D | 0.127 |
| Eye cap (max sensitivity pulled 0.9 x F x s toward min) | AE can still brighten about 1.4x above daylight exposure |
| Under-storm ground | 0.20-0.27x clear display luma |

**Model: an extended level, not a fork.**
- **The megastorm darkness term E** (0..1) is a pure function of xi (draft profile below), scaled by s / 0.9 (clamped 0..1), so "Storm darkness" scales it too and s = 0 turns it off.
  - It is written by the macro build into the **spare z channel of the storm-light layers 4/5**, which today hold (owned, front, 0, 0) (`RingMacroWeatherMap.glsl`; S0 doc). It comes from a small megastorm uniform (camera-relative `s(t)`, dir, zone lengths).
  - Receivers already fetch that texel (`ring_storm_level_at_map`, `:58`), so there is **no extra fetch**.
  - CPU twin: `MegastormModel.Darkness(xi)`, used by `GetStormLevelAt` and the camera level.
  - Beyond the map, `ring_storm_level_far` evaluates E analytically from the same uniform, so distant ground under the wall reads dark too.
- **Extended level** `L = D + E` (0..2). The include's functions split it as `D = min(L, 1)` and `E = max(L - 1, 0)`.
  - For every ordinary storm L <= 1, so `min(L, 1) = L` and **all terms are bitwise unchanged**.
  - Receivers keep their signatures; only the include changes.
- **Darkness multiplier** `K(E) = 1 - (1 - k_min) x E`. It multiplies the sun residual, the ambient A, the sky and haze radiance, the Sunshine sun factor and the camera cloud ambient, and the StandardMaterial3D tint.
  - **`k_min` = 0.06 by day.** At E = 1 the wall's ground light is about **6% of an ordinary storm's D = 1 level**: about 0.25% of clear-day radiance (linear), about **0.05-0.08x clear display luma** with AE locked (clear about 170 luma gives about 9-14).
  - So shapes are only faintly readable between flashes.
- **Lightning is not darkened.** K scales the sun, ambient and sky terms only. Bolt, scythe and field-strike lights and the lightning flash are separate light sources, so they now dominate: the world is seen in flashes.
  - Exception: `ring_emitter_standin` and `ring_scale_anchor` darken through `AO_LIGHT_AFFECT = 1` (S0 WO-3), which would also dim flashes on them. Give those two an albedo-side K instead.
- **Harder eye lock.** The cap target becomes `max(0.9 x F x s, E_cam)` (`day_side_atmosphere.gd:606-608, 823, 843`). At E = 1 the maximum sensitivity equals the minimum (ISO 50, daylight exposure), so AE cannot brighten the wall back up. Its blend keeps today's smoothing (no pumping).
- **Night floor.** K multiplies whatever light there is, and the night is already dark, so a day-tuned 0.06 would crush night to black.
  - The CPU publishes `k_min_eff = mix(0.06, 0.40, night)` with the eye lock, in a new global `ringworld_storm_light_mega` (x = k_min_eff, y = eye-lock strength, z/w spare; declared in `project.godot` like `ringworld_storm_light`). `night` comes from the published haze sun irradiance.
  - Night under the megastorm keeps 40% of an ordinary night storm's light, plus the flashes. Never zero.
- **D-consumer audit** (anything that assumed D <= 1 must use `min(L, 1)` or the new semantics):
  - `RingSunshineCloudBridge.StormLight.cs` (`GetStormLevelAt`, camera level);
  - `ring_rain_streaks.glslinc` `storm_level`;
  - `ring_wet_map.glsl`;
  - haze path darkening (`ring_storm_path_level`);
  - `RingStormLightTint`;
  - `day_side_atmosphere.gd`;
  - `UpdateStormCloudLighting`.

**Draft profile E(xi)** (continuous, no steps; times at 350 m/s):

| Zone | xi (km) | E | Time | Look |
|---|---|---|---|---|
| Under the approaching anvil | -45 .. -5 | 0 to 0.55 | T-129 s .. T-14 s | Fast dimming to a deep, eerie gloom (on top of the ordinary D from the anvil record) |
| Shelf into the wall | -5 .. +2 | 0.55 to 1 | T-14 s .. T+6 s | The light goes with the boom |
| The wall | 2 .. 25 | 1 | T+6 s .. T+71 s | Near-night; flashes are the main light |
| Vacuum wake | 25 .. 60 | 1 to 0.9 over 25-30, then 0.9 | T+71 s .. T+171 s | Very dark; the wake fog catches the flashes |
| Trailing rain | 60 .. 390 | 0.9 to 0.7 over 60-80, then `0.7 x (1 - smoothstep(80, 390, xi))` | T+171 s .. T+18.6 min | Heavy overcast gloom lightening over about 16 min as the deck thins (ordinary front D continues under it) |
| After it passes | > 390 | 0 | from T+18.6 min | Light returns as the rain-driven D fades |

- **Wake fog and flashes.** The wake fog is haze (post-rain mist path, 5.5.3). It catches the flashes only if the haze in-scatter takes the lightning flash.
  - Verify the haze has a lightning term. If not, add the active flash (`RingLightningEvent` pulse x proximity) to the haze ambient while `WakeFog` is above 0. It is one CPU-published scalar, 0 elsewhere.
- **No pops at 350 m/s.** E moves through the map at 350 m/s. The fast macro cadence near the wall (3.6: about 29 m steps) keeps each step under 1% of E's steepest ramp (0.45 over 7 km). The far analytic E is per frame.
- **Cost.** One more multiply per receiver term and no extra fetch; the macro build gains one analytic term per texel (< 0.005 ms); the CPU twin is negligible. 0 when untriggered: E = 0, so L = D, which is identity.
- **Query.** `MegastormSample` gains `ExtraDarkness` (E) and `DarknessMultiplier` (K at the position, with the current night floor).

## 6. Hooks and API

### 6.1 Model and runtime
- **Pure model:** `scripts/Weather/Megastorm/MegastormModel.cs`:
  - `MegastormState`: trigger, anchors, stop, seed, settings snapshot;
  - `Evaluate(t)` gives the frame: `s`, f, strength, active;
  - `Xi(around, across, t)`;
  - `BuildBundle(t, queryAcross, list)`;
  - `Suppression(xi)`;
  - `MegastormProfile.Column(x, across, t)`, the C# twin of the include;
  - `Danger`.
- **Node:** `scripts/Weather/Megastorm/RingMegastorm.cs`, a sibling of `RingVolumetricClouds`.

```csharp
public readonly record struct MegastormTrigger(string TriggerId, double LeadingAroundMeters, int Direction,
    double StartTimeSeconds, float SpeedMetersPerSecond = float.NaN /* NaN = setting */, float FormationSeconds = 1800f, int Seed = 0);
public enum MegastormPhase { Inactive, Forming, Clear, Precursor, Anvil, Approach, GustFront, Wall, Transition, Trailing, Aftermath }
public readonly record struct MegastormSample(bool Active, MegastormPhase Phase, double DistanceToEdgeMeters /* > 0 ahead */,
    double TimeToArrivalSeconds, double TimeToClearSeconds, double TimeSincePassageSeconds, float Danger /* 0..1 */,
    float Precipitation, float StormLight /* D */, Vector3 WindRingFrame, float UpdraftMetersPerSecond, float LightningRatePerSecond,
    float HumLevel /* 0..1, structure-borne, any speed */, float WallSingLevel, MegastormAirSound AirSound, float BoomEnvelope /* 0..1 */,
    float TremorLevel, float WildlifeQuiet, long EpisodeId /* passage key for PDA once-per-approach rules */,
    MegastormHazards Hazards /* 5.5 */, float ExtraDarkness /* E 0..1, 5.6 */, float DarknessMultiplier /* K, 5.6 */);
public readonly record struct MegastormHazards(float OverpressureKPa, float BoomPeakKPa, float BoomImpulseKPaS, double BoomTimeSeconds,
    float RainImpactPowerWm2, Vector3 RainImpactDirection, float RainDropSpeedMs, float RainExposure /* observer only */,
    float PressureDeficitKPa, float PressureRateKPaPerS, float WakeFog, float LocalStrikeRatePerSecond, float ScytheSweepRatePerSecond,
    float NearestScytheDistanceMeters, float FieldStrikeRatePerSecond, float FieldGapMeters /* NaN without a field */);
// also: float ScytheCutProbability(RingCanonicalPosition p, float heightAboveGroundMeters, float exposure, double t, double dt);
//       MegastormBoom BoomAt(RingCanonicalPosition p);  events: BoomPassed, WakeEntered, WakeExited, ScytheStarted, ScytheEnded, FieldStrikeStarted, FieldStrikeEnded (observer and registered structures)
public enum MegastormAirSound { None, Leading /* subsonic: airborne roar leads */, Silent /* supersonic, ahead of the wall */,
    Boom /* edge pile-up, BoomSeconds */, Inside, Receding }

bool Trigger(in MegastormTrigger t);                       // idempotent per TriggerId; false if one is already active
bool TriggerToReach(RingCanonicalPosition target, double arrivalSeconds, int direction, string triggerId, bool force = false);
void SetSpeed(float metersPerSecond);                      // re-anchors at now; position continuous
void Stop(double atTimeSeconds, float fadeSeconds);
MegastormSample Query(RingCanonicalPosition p, double t);  // pure, thread-safe, allocation-free
MegastormSample QueryNow(RingCanonicalPosition p);
event Action<MegastormPhase, MegastormPhase>? ObserverPhaseChanged;
```

- **`TriggerToReach`** refuses (unless forced) when `arrivalSeconds < Formation + 260 km / v` (2,543 s at the defaults), so the wall never appears fully formed inside the precursor reach.
- **GDScript wrappers** follow `register_manual_weather_at` (snake_case).
- **Danger** (documented, unused now) = max of:
  - wall `smoothstep(-2, 2, xi) x (1 - smoothstep(25, 50, xi))`;
  - gust front 0.75 x GustFront;
  - transition 0.5;
  - trailing 0.25;
  - precursor `0.15 x smoothstep(600, 0, TimeToArrival)`.

  Each term is multiplied by `0.5 + 0.5 x shelterless`. Flight later adds updraft and turbulence from `RingWindQuery`, which already carries the megastorm through the arrival injection.

### 6.2 Save and load
- `RingWorldSaveManager.RuntimeDocument` (`:160-176`) gets an optional `MegastormDocument? Megastorm` with:
  - `Version`, `TriggerId`, `TriggerTimeSeconds`, `Direction`, `Seed`, `FormationSeconds`;
  - `Anchors[] {TimeSeconds, AroundMeters, Speed}`;
  - `StopTimeSeconds` (NaN), `StopFadeSeconds`;
  - `ResonanceEntryUnlocked` (the PDA database hook, 5.4).
- Written beside `CloudSimulationTimeSeconds` (`:338`); restored right after the clock (`:1078`), before the first weather refresh.
- Absent means not triggered, so old saves load unchanged; `SaveFormatVersion` stays 3.
- A save, reload and advance reproduces positions, records and lightning ids bit-exactly.

### 6.3 Containment field: publishing into `ContainmentDisturbance`
This follows the final field doc section 4. The megastorm keeps one small hook of its own, the field installs it, and every ripple goes through the field's bus (`RingContainmentField.Disturbances.Publish`, main thread, `_Process`, weather clock).

**Provider (installed by the field).**

```csharp
// Set by RingContainmentField at startup with the true underside height H(across) from ContainmentFieldGeometry.HeightAt;
// null = no field (ceiling 18 km, nothing published).
public static Func<double /*across m*/, double /*field underside m*/>? FieldUndersideProvider { get; set; }   // on RingMegastorm
```

The megastorm uses it for:
- the CPU twin's column clamp, by passing H to the field geometry code's single ceiling function. The GPU calls the same function (`ring_containment_ceiling`);
- the contact band;
- the gap attenuation of updraft pulses.

The megastorm never defines its own ceiling formula.

**Hooks and how they map onto `ContainmentDisturbance`:**

| Megastorm hook | Field kind | How |
|---|---|---|
| Strong lightning | `Lightning` | Couples **like natural lightning**. Near megastorm strikes are ordinary `ActiveLightningEvents`. The field's `LightningDisturbanceAdapter` couples them by the parent's top: record W, id -30001, `StormCeilingMeters`, from `CollectRenderWeatherSystems`, with `gap = H - top` attenuation. The megastorm publishes nothing extra |
| Sprites and jets | `UpperAtmosphere` | Drawn unclamped (decision 4). Megastorm discharges are created through the natural sprite layer (`UpperAtmosphereLightning.TryDerive`) and **share its event id**, so the field's sprite adapter ripples one discharge at most once. The megastorm publishes nothing extra |
| Updraft bursts (overshooting tops) | `UpdraftBurst` (= 7, field doc) | **Repeated pulses that drift with the storm** (details below) |
| Contact band (wall top pressing the ceiling) | `MegastormContact` | **At most 4 ripples per second in total**, both sides combined (details below) |
| Natural storm contact | `StormContact` | The field's `StormContactAdapter` **skips kind `Megastorm`** records, so the wall is never published twice |
| Field strikes (5.5.5) | **`FieldStrike`** | Published by the field's `LightningDisturbanceAdapter` for every flagged `RingLightningEvent` (any source, megastorm or natural): `GroundPosition` = contact point, `CloudPosition` = channel base, energy 0.6-1.0, the event's own pulse, `SourceId` = EventId. The field picks the patch size. About 0.3/s for the whole wall. Near-rim contact arcs publish nothing of their own; they drive `MegastormContact` `FlashLevel`. The field's response is in `docs/containment-field-design.md` 3.10 |

**Updraft bursts.**
- Overshooting tops along the wall: about 1 per 5 min per 50 km of wall, at xi 3-18 km, each living 120-600 s.
- A live burst re-publishes a pulse every 1.5 s at its **current** position, which advances with `s(t)`, so the ripple train drifts with the storm.
  - `SourceId = hash(burstId, pulseIndex)`.
  - `Energy = strength x exp(-max(H - 18 km, 0) / 2 km)`, the field's coupling rule. Only bursts within about 6 km of a wall reach the field visibly; mid-ring bursts (gap about 22 km) publish nothing below the field's E >= 0.1 floor.
  - `RadiusMeters` 10-20 km, `DurationSeconds` 5.
  - `FlashLevel` = the nearest flash-field segment pulse, with `FlashCoupling` 0.6.
- Bursts count against the contact cap below.

**Contact band.**
- **Where.** The wall body (xi 0-25 km plus domes) where the strip column top reaches `ceiling - 500 m`: within 6.2 km of each wall (5.8 km on runtime-surface worlds).
- **Cadence.** At most **4 per second in total**, within 400 km of the camera, at hashed positions along the contact arcs in the moving frame.
  - `SourceId = hash(side, xi cell, time bucket)`: deterministic, and identical on a paused clock.
  - At 350 m/s each 5 s ripple is anchored at spawn while the contact moves on 1.75 km, so the field shows a travelling wake along the wall's rim ends.
- **Payload.**
  - `Energy` = wall strength x penetration;
  - `RadiusMeters` 10-30 km, `DurationSeconds` 5;
  - `FlashCoupling` 0.6, with `FlashLevel` = the nearest flash-field segment pulse;
  - `OccluderCenter` = the contact point, `OccluderRadiusMeters` 12 km, `OccluderTopMeters` = local ceiling.

**Sequencing.**
- The field's coupling work lands **after megastorm WO8**: the megastorm publisher and the provider seam come first and are tested against a stub field.
- Both designs share one test fixture, `tools/fixtures/containment_megastorm_fixture.json` (proposed name). It holds a triggered megastorm at a fixed weather time with the wall crossing a rim on a Certified and a runtime-surface profile, plus the expected contact band, pulse ids and per-second counts.
- The megastorm WO8 test and the field's coupling test both assert against it.

## 7. Grave-key menu (live-tuning-setting skill)

Group **"Megastorm"** in the Clouds tab:
- new `scripts/ui/RingLiveTuningDrawer.Megastorm.cs`, chained like `BuildStormArrivalControls` (`RingLiveTuningDrawer.Wind.cs:62`);
- `GameplayMegastormSettings` / `GameplayMegastormDefaults` with `user://gameplay_megastorm_defaults.tres`, following `GameplayStormArrivalDefaults.cs`;
- runtime `RingMegastormSettings.Current`.

| Setting | Range | Default | Why it is a genuine tunable |
|---|---|---|---|
| Megastorm speed (m/s) | 5-500 | 350 | Campaign pacing (lap about 20 days). A change re-anchors at "now", so the position stays continuous |
| Megastorm trailing length (km) | 100-600 | 340 | Pass length: 50 + L = 390 km, a quarter day at 350 m/s |
| Megastorm warning reach (km) | 50-600 | 220 | How early the world warns. It sets the precursor P extent (sky; about 12 min at 350 m/s) **and** the ground-hum stages: hum onset at 10x (2,200 km, about 105 min) and water/wildlife at 2x (440 km, about 21 min). One entry serves both, so the sound adds no menu entry |

- **Not in the menu (sound):** hum gain, `BoomSeconds` (4) and the micro-shake amplitude are defaults the user can change later. The hum, singing and boom follow the existing weather audio volumes. PDA settings (`PdaEnabled`, `PdaSubtitles`, ...) belong to the eclipse spec's PDA group.
- **Not in the menu**, because global settings already cover them: "Storm darkness", "Gust front strength" (and "Gust front lead", which drives the 5.1 bands), "Lightning frequency", "Upper-atmosphere lightning", "Reduced flashes", "Hail", and the audio volumes. No on/off toggle: the story owns it.
- **Debug tooling only:**
  - `RING_MEGASTORM=<around_km>,<dir>,<age_s>[,<speed>]` and `RING_MEGASTORM_XI=<km>`;
  - `GetMegastormDiagnostic()` in the weather HUD;
  - `RING_MEGASTORM_FAR_OFF=1`;
  - `RING_MEGASTORM_REPROJECT_OFF=1` (A/B for the history term);
  - `CameraIndependentDebugMask` bit 256: strip fetch off. This widens the mask range to 0..511.

## 8. Work orders

Conventions:
- Sunshine `.glsl` edits follow the reimport procedure in `storm-revamp-s4-sculpt-v2-design.md` ("Work orders"), then a windowed `--import`.
- Captures go to `.godot/captures/megastorm/<wo>/` under one profile `appdata_megastorm` (`-Cache reuse`); use the GPU queue and the godot-test-run skill. Run `tools/prune_test_output.ps1 -Apply` at the end of each WO, and delete raw frames after sheets and mp4s are made.
- One owner at a time for `SunshineCloudsCompute.glsl`, `SunshineCloudShadowCompute.glsl`, `SunshineCloudsHistory.glslinc` and `RingVolumetricCloudLayer.cs`.
- **Motion acceptance at 350 m/s** (used by WO4/WO5): a fixed ground camera through the edge passage, and an orbit at 2 km under the shelf, recorded with Movie Maker at 30 fps.
  - `flicker`: no single-frame spikes above the natural-storm baseline.
  - `sheet`: no ghost trail behind the shelf lip.
  - A/B with `RING_MEGASTORM_REPROJECT_OFF=1`, which must show the ghost the fix removes.

**WO1: pure model.**
- `MegastormModel.cs`: anchors, `s(t)`, wrap, xi with bow and meander, formation, stop, zones, `Suppression`, `BuildBundle`, `MegastormProfile.Column`, `Danger`, kind-6 time-based band constants.
- Accept: headless `MegastormModelTest`:
  - re-anchor continuity (jump < 1 mm);
  - wrap across 0/C;
  - before-trigger inactive;
  - `TimeToArrival` within 0.1% of xi/v at 5, 350 and 500 m/s;
  - slab error < 10 m across the width;
  - bit-identical across runs and a JSON round trip.

**WO2: runtime node, trigger API, save/load, settings.**
- `RingMegastorm.cs`, GDScript wrappers, `RuntimeDocument.Megastorm`, the three settings (skill), debug env.
- Accept:
  - `MegastormSaveLoadTest`: save at T, run to T + 600; reload, run to T + 600; s, records and lightning ids are identical. An old save loads as untriggered;
  - settings smoke; QP contract.

**WO3: weather injection (CPU).**
- Kind `Megastorm = 6`; profile `MegastormWall = 12` (`ToShaderProfile`, `IsStormSculptProfile`); table rows `MegastormWall` and `MegastormTrailing`.
- `AppendMegastormSystems` at `RingVolumetricCloudLayer.cs:3787` and `:3890`, and `RingVolumetricCloudLayer.StormArrival.cs:94`, with an `includeMegastorm` switch for the far atlases.
- Per-record drift seam; suppression and effective envelope (4.3); kind audit (section 5).
- Accept:
  - fingerprint bit-identical untriggered; `StormLifecycleCodeTest`; `CloudLayerColumnTest`.
  - `MegastormWeatherTest` (sweep xi -300..1000 km at 3 across positions):
    - rain onset within +-2 km of xi 0; rain end within +-15 km of 390;
    - D at least 0.95 for xi 0-25 and at least 0.6 in the trailing rain; the precursor is dry;
    - natural systems 0 by xi -70 and recovered by 990.
  - CPU budget.
- Escalate if any consumer misreads the slab (then use the band flag).

**WO4: near wall (strip map, march fetch, moving noise, reprojection, cadences).**
- `RingMegastormStripBuild.glsl`, `RingMegastormStrip.gd`, `ring_megastorm_profile.gdshaderinc`.
- Profile-12 strip branch at the predicate sites, in `sampleScene` and the shadow compute; moving-frame noise; the reprojection term in `ring_cloud_history_reproject`.
- Fast macro-map and near-shadow cadence while the wall is near (3.6); the field ceiling via the field doc's helper (or 18 km if the field WO6 has not landed); reimport.
- Accept:
  - `sunshine_shader_compile`;
  - strip readback vs `MegastormProfile.Column` within 50 m at 64 points; nothing above 18 km or the field ceiling;
  - untriggered identity (SSIM 1.000, clean heatmap) on fair, overcast and storm poses; the S6 cloud gate list; `cloud_gameplay_history` and `cloud_history_motion` unchanged untriggered;
  - motion acceptance;
  - perf A-B-A (3.5);
  - captures: inside (up / forward / back), under the leading edge, ahead 15 km, rim (within 6 km of a wall).

**WO5: far wedge pass and analytic far coverage.**
- `RingScaledFarViewport.Megastorm.cs`, `ring_far_megastorm.gdshader`, `ring_far_storm_lighting.gdshaderinc`.
- Layer-sky truncation and analytic T/P coverage; atlas exclusion; flash field and its CPU twin; shaders added to `RingWorldLoadingResources`.
- Accept:
  - captures at 50, 200 and 1000 km, quarter ring, antipode, and night at 200 km and the antipode;
  - handoff `compare` with no seam, plus `flicker` on a 30-to-80 km flight with the wall moving at 350 m/s;
  - no 8 s pops on a 60 s fixed-camera far shot (`flicker`);
  - include parity (shader vs C#);
  - perf; node hidden when untriggered.

**WO6: arrival, wind, hail, sound design, PDA.**
- Kind-6 time-based bands (5.1); `StormFactor`; `RingWindQuery` wall updrafts; hail placement.
- Sound (5.2, S5 owner files):
  - pure `MegastormSound` maths in the model (`HumLevel`, `WallSingLevel`, `AirSound` from M and xi, `BoomEnvelope`, `TremorLevel`, `WildlifeQuiet`, `EpisodeId`), exposed on `MegastormSample`;
  - the hum, wall-singing, boom and roar voices and assets;
  - airborne gating of the distant-storm sectors, the roar and the far thunder;
  - physical thunder delays with no cap;
  - `ring_ground_tremor` and its readers (water ripples, ocean, vegetation, camera micro-shake);
  - `WildlifeQuiet`.
- PDA (5.4): `resources/pda/megastorm_lines.json`, `megastorm_database.json`, the trigger and episode rules. If `PdaAnnouncer` has not landed, the minimal subtitles-only core described in 5.4.
- Accept:
  - `StormArrivalMathTest` extended: the 5.1 phase table within 5% at 350 m/s, and clamps at 25 and 500 m/s;
  - `RingWindQuery` tests;
  - `MegastormSoundTest` (headless, pure):
    - hum onset at 10R and stage 2 at 2R, within 0.5%;
    - `AirSound` Silent ahead at 350 and 500 m/s, Leading at 25 and 300 m/s, and continuous across 322-343 m/s (no step over 0.05 per 1 m/s);
    - Boom only when M >= 0.94, lasting `BoomSeconds`, starting exactly at xi = 0;
    - the gust swell allowed at 350 m/s and suppressed at 500 m/s;
    - a reload behind the edge gives no second boom unless still inside the window;
  - `MegastormPdaTest` (headless, fake clock and subtitle sink, a 350 m/s approach from 2,500 km to the aftermath):
    - the lines fire in the 5.4 order with the right facts;
    - each id at most once per episode (rising at most 2, at least 10 min apart);
    - at most one non-critical line per 5 min;
    - critical lines pre-empt;
    - Resync speaks only the resync line;
    - a second lap starts a new episode;
    - the database unlock persists through save/load;
  - audio diagnostics on a 1x passage at 350 m/s (record T-4 min to T+3 min, then the trailing rain and clearing at x10):
    - airborne gains (roar, distant sectors, far thunder) are 0 until the edge; the hum ramps with no pops; the boom fires once;
    - shelter muffles the roar but not the hum;
  - the same passage at 300 m/s: the roar leads and there is no boom;
  - tremor identity: `ring_ground_tremor` = 0 untriggered (water and vegetation captures SSIM 1.000). At full hum the puddle shiver shows in a 2 s capture, and `flicker` shows no frame spikes from the camera shake.

**WO7: lightning streams.**
- Near strike stream, sprite source (unchanged layer), CPU flash field.
- Accept: determinism and rewind; rates within 10%; "Lightning frequency" and "Reduced flashes" scale it; fingerprint unchanged.

**WO8: field publishing seam** (lands before the field's coupling work).
- `FieldUndersideProvider`; the publisher for contact ripples and drifting updraft pulses into an `IContainmentDisturbanceSink`; megastorm sprites created through `UpperAtmosphereLightning.TryDerive` with the shared event id.
- The shared fixture `tools/fixtures/containment_megastorm_fixture.json`.
- Tested against a stub sink and provider. The field later swaps in the real bus and adds the `StormContactAdapter` kind-6 skip in its own file.
- Accept: headless test against the shared fixture:
  - deterministic SourceIds;
  - contact positions within 6.2 km of a wall on the Certified profile and 5.8 km on the runtime-surface profile, and on the wall (xi 0-25 km);
  - updraft pulse positions advance by `v x 1.5 s` per pulse;
  - at most 4 contact and burst ripples per second in total;
  - none when the provider is null or the wall is beyond 400 km;
  - one id per sprite discharge, shared with the natural sprite layer.
- After the field lands (field owner): a capture of the rim from 30 km at night showing a ripple wake along the flattened top.

**WO9: acceptance.**
- `tools/megastorm_capture.gd/.ps1` with poses `inside`, `leading`, `ahead15`, `ahead50`, `ahead200`, `ahead1000`, `quarter`, `antipode`, `rim`, `trailing`, `aftermath`, plus night variants and the motion set.
- Full batch gate list, perf table, doc handoff. Hazard captures from WO11 and WO12 are included in the sheets.
- User check (headphones):
  - the hum build-up at x10 from T-105 min to T-4 min, indoors, outdoors and next to a rim wall, with the PDA subtitles;
  - then 1x from T-4 min to T+3 min (dead calm, gust swell, boom, roar);
  - then once at 300 m/s to hear the subsonic contrast.

**WO10: hazard model, query and events** (5.5; pure plus the observer adapter).
- Boom envelope and overpressure; bullet-rain wind profile (kind-6 override in `ModifyWind` and `RingWindQuery`); rain impact power and direction; the wind-aligned cover probe (`PrecipitationCoverProbe`); wake pressure and reversal winds.
- `MegastormHazards` on the sample; `BoomAt`; `ScytheCutProbability`; the events; the `IMegastormStructureSource` seam (null today); the 5 hazard PDA lines; the Danger terms.
- Accept: headless `MegastormHazardTest`:
  - the boom peak is exactly at the xi = 0 crossing, and its time equals the boom audio envelope peak (one source);
  - open / lee / enclosed attenuation 1 / 0.6 / 0.25;
  - wind at 10 m: 41 m/s at the gust front, 140 m/s for xi 2-15 km, 40 m/s at xi 25 km;
  - the reversal flips the around component's sign between xi 25 and 35, then flips back;
  - the pressure minimum (-8 kPa) at xi 35;
  - all hazards 0 when untriggered and outside the zones;
  - deterministic across save/load;
  - cover probe: lee of a 10 m wall gives exposure under 0.2;
  - the PDA order with the hazard lines (extends `MegastormPdaTest`).

**WO11: hazard visuals and audio.**
- Shock debris burst; the `RingShockDistortionComposite` pass; `ring_megastorm_blast` in the tree and grass wind path; the water slam; streak slope and wind-aligned lattice mode with the length clamp; windward spray mode; rain-hammer bed; wake fog through the mist path; the ear-pop one-shot and low-pass.
- Accept:
  - untriggered identity (SSIM 1.000 on vegetation, water and rain captures);
  - motion captures at 30 fps: the boom pass (trees flatten in a band, a dust ring, a water slam, with camera shake and audio peaks in the same frame window, within 2 frames);
  - bullet rain near-horizontal with no lattice gaps (`sheet`), no rain in a 10 m wall's lee (`compare` against the windward side);
  - the wake: fog onset within 20 s of the trough and debris moving backwards;
  - `flicker` with no single-frame spikes outside the boom window;
  - perf per the 5.5 cost notes (A-B-A);
  - "Reduced flashes" also damps the distortion band.

**WO12: lightning scythes.**
- Scythe stream in `RingVolumetricCloudLayer.Lightning.cs` (`Scythe` flag, attachment strike); pure `ScythePath`, `ScythesInWindow`, `ScytheSweepRatePerSecond` and `ScytheCutProbability` against the smoothed ground (100 m smoothing).
- Rendering: the sheet/arc ribbon mode with branches and re-stroke flicker; the omni-light ride and the flash centroid; the scorch and dust strip; far streaks in the wedge pass; crackle audio.
- Field coupling unchanged (ordinary strikes and contact `FlashLevel`); the shared fixture confirms scythes publish nothing to the field.
- Accept:
  - determinism and rewind; rate within 10% of 20/s (times the frequency setting);
  - path length 2-5 km, width 50-200 m, lifetime 1-3 s, lower edge 0.5-4 m above the smoothed ground at every polyline point;
  - the tip moves in the storm's direction;
  - cut-probability table: prone or trench 0, crouched (1 m) about 0.05, standing about 0.33, 10 m about 1 (times the sweep rate and exposure); enclosed exposure 0;
  - Monte-Carlo over 10,000 simulated passages: standing-exposed about 2.6% +-0.5%, 10 m about 8% +-1%;
  - captures by day and night at 1 km and 5 km: sheet visibly skimming, branches, flicker per stroke, ground light sweeping, scorch line fading;
  - `flicker` respects "Reduced flashes";
  - audio: the crackle travels and is smeared by per-segment delays;
  - perf <= 0.3 ms total (A-B-A); 0 when untriggered.

**WO13: field strikes** (5.5.5; after WO12 and WO8; the real-bus check after the field adds the `FieldStrike` kind). **Field WO14 (natural near-wall field strikes) is ordered after this WO**, because it depends on the shared flag, the event convention and the channel layer.
- Shared pieces:
  - the `FieldStrike` flag on `RingLightningEvent`;
  - the convention `GroundPosition` = contact point, `CloudPosition` = channel base;
  - per-event `DurationSeconds` and the pruning by it;
  - the field-strike `PulseAtAge` envelope.
- Megastorm field-strike stream and contact arcs in `RingVolumetricCloudLayer.Lightning.cs` (flagged events in `ActiveLightningEvents`); the rate law; `FieldStrikesInWindow`; the query fields.
- The source-agnostic `RingFieldStrikeLayer` (main and far channel sets) and `ring_field_strike.gdshader`; the dome-top glow in the wedge pass.
- Publishing goes through the field's adapter (no direct push); contact-arc `FlashLevel` into `MegastormContact`; far-thunder roll and rim sizzle.
- Accept:
  - determinism and rewind; the rate law within 10% at 5 across positions; total about 18/min at default frequency;
  - every channel ends within 100 m below H(across) (provider-driven, Certified and runtime-surface fixture profiles);
  - no field strike where the gap is under 0.5 km (contact arcs only there); none with a null provider;
  - one `FieldStrike` disturbance per strike via the adapter (SourceId = EventId, no duplicates); contact arcs publish none;
  - the layer draws a synthetic flagged natural event (kind 3, a 1 km channel) exactly like a megastorm one, from `CloudPosition` to `GroundPosition`;
  - a flagged 1.9 s event stays in `ActiveLightningEvents` for its full duration; unflagged events still prune at 0.72 s (fingerprint unchanged);
  - the field bus budget is never exceeded in a 10 min run (shared fixture);
  - captures:
    - night from 50 km and 200 km ahead of the wall (channels end in empty sky at the field);
    - night across the arch (the minimum angular size holds; with the field doc's far hint on, the flare shows);
    - day from 50 km;
    - under the wall (channels hidden by the deck, the flash lights the deck top only through the dome glow; no leak);
  - "Reduced flashes" envelope respected (`flicker`);
  - perf per the 5.5.5 cost note (A-B-A); 0 untriggered.

**WO14: megastorm darkness** (5.6; after WO4, which brings the fast macro cadence, and WO5, which brings the far uniform).
- E written to the storm-light layer z by `RingMacroWeatherMap.glsl` from the megastorm uniform; CPU twin `MegastormModel.Darkness`.
- `ring_storm_light.gdshaderinc`: the extended level `L = D + E`, `K(E)`, and K applied in the sun residual, A, the sky, and `ring_shadow_compose`'s storm field. GLSL twins: streaks, wet map, haze path.
- The far analytic E in `ring_storm_level_far`.
- `ringworld_storm_light_mega` global (k_min_eff, eye lock) published by `day_side_atmosphere.gd`; the eye-lock target `max(0.9 F s, E_cam)`; the Sunshine sun factor and camera ambient x K; `RingStormLightTint` x K.
- The emitter stand-in / scale-anchor albedo-side K; the wake-fog flash term if the haze lacks one; the D-consumer audit.
- Accept:
  - **ordinary storms unchanged:** `storm_light_gpu_test` 12/12, `haze_consistency` 20/20 and `shadow_compose_identity` 44/44 bitwise, with a natural ExceptionalFront at D = 1 identical to HEAD;
  - **noon under the wall** (E = 1, AE on, rain veil on and off):
    - ground mean display luma 0.04-0.10x the clear-day pose and at most 0.35x the ordinary-storm pose;
    - shapes still faintly readable: the local-contrast check on the capture sheet;
    - **a flash visibly lights the scene:** flash frames at least 4x the wall mean luma, by `flicker` peaks;
  - **gradient:** captures at xi -40, -10, 0, 10, 40 (wake), 100, 300 and 450 km; mean luma monotonic down to the wall, then up through the trailing region, matching the E table within 10%;
  - **no pop at 350 m/s:** a fixed ground camera through the whole pass at 30 fps; `flicker` shows no single-frame steps beyond the noise floor outside lightning frames;
  - **night:** under the wall at midnight the ground is not zero (at least 0.3x an ordinary night storm), and flashes light it;
  - eye lock: AE sensitivity at E = 1 equals the minimum, with no pumping across zone edges;
  - "Storm darkness" 0 turns all of it off (identity with an untriggered capture on non-megastorm pixels);
  - perf: receiver A-B-A within noise; macro build at most +0.005 ms.

**Order:** WO1, then WO2, then WO3, then WO4 and WO7 in parallel, then WO5, then WO14, then WO6, then WO8 (field seam, stub-tested; the field's coupling lands after it), then WO10, then WO11 and WO12 in parallel, then WO13, then WO9 (acceptance last, now also covering the hazards and the darkness).

## 9. Risks
- **The reprojection term** treats the whole ray's history as moving when the density centroid is in the wall. Rays that mix near static scud with the far wall get a compromise. Fallback: the current-frame weight raise.
- **The march fetch** sits at the register-pressure cliff (S6 WO3b note). Gate per ray; A/B every addition. Escalate if it costs more than +0.15 ms under the wall.
- **Fast macro cadence** cost on the A1000; the fallback is 0.1 s and 4 slices with a slightly visible D step.
- **Near/far look mismatch** at the handoff: tune `ring_far_storm_lighting` gains.
- **Slab encoding** in a consumer that assumes the major axis is about equal to the minor: WO3 sweeps `MajorRadiusMeters` consumers.
- **Story placement:** `TriggerToReach` refuses windows shorter than formation plus the precursor travel.

## 10. Open questions
1. **Quarter-day pass versus the speed setting.** This doc fixes the zones in km (pass 390 km, a quarter day at the 350 m/s default). At other menu speeds the pass time changes (65 min at 100 m/s), but the storm looks the same on the arch. The alternative keeps the pass a quarter day at every speed by scaling the trailing length with speed (the wall depth would stay fixed), so the arch footprint would grow or shrink with the setting. **Resolved 2026-10-29: fixed km (user).**

Resolved 2026-10-29: pace (350 m/s), day look (physical), field ceiling (the shared field ceiling function is the single ceiling: 6.2 km / 5.8 km contact band), sprites (drawn unchanged, ripple once via the shared id).
