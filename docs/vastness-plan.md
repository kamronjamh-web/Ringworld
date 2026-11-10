# Vastness plan

Status: proposal, 2026-11-09. Written by the Fable role (`claude-fable-5-2`) for the primary agent to review. No code, shader, scene or setting was changed. Nothing here is approved until the user answers the open questions in section 9.

Revision note, 2026-11-09: the user set a project-wide rule, "new worlds only", and asked for a new test world for every major visible change. Section 11 applies that rule to this plan, using the world-generation ladder defined in `docs/terrain-vegetation-revamp-plan.md` section 2.2. The design in sections 2-6 is unchanged. Three items moved: the baked haze pick (section 4), forest grain (section 5) and the phase order (section 7).

Scope: the four areas the user picked from the vastness brainstorm (light shafts and shadowed air, aerial perspective, the scale ladder, large shadows and slow motion). Ruled out and not proposed: ringshine, an arch veil, settlement or night lights on the far arch.

How to read this: section 1 is what the code and docs say today, including six places where the brief was out of date. Section 2 is the design thesis. Sections 3-6 take the four areas in the brief's numbering. Section 7 is the phase list in recommended order, which is not the brief's order. Numbers marked "derived" are arithmetic from code defaults; numbers marked "est." are estimates to be replaced by measurement.

## 0. Summary

**Thesis.** The ring is visible across four decades of distance (1 km to 7,000 km), and today each depth cue covers about one. Vastness comes from giving every decade its own cue: a haze layer that grades it, an object of known size or pitch that measures it, and a shadow or light edge that moves across it.

**Recommended order.**

| # | Phase | Kind | Why here |
|---|---|---|---|
| V0 | Vastness audit harness and baseline | new tool, no production change | The instrument every later phase is judged with; finds the empty bands |
| V1 | Aerial perspective calibration | slider work plus a capture matrix | Cheapest large win; decides whether light shafts will be visible at all |
| V2 | Cheap ladder fixes (spire stand-in width, wall courses and engine bays, mast livery) | small shader work | Fills the bands V0 finds empty at near-zero GPU cost |
| V3 | Analytic rim-wall shadow and the terminator | small engineering | The dusk "shadow tide" and a crisp terminator; its closed form is reused by V4 |
| V4 | Path sun visibility (light shafts and shadowed air) | the main new engineering | Needs V1's haze profile and V3's wall function |
| V5 | Rain curtains: haze, side light, range | small-medium engineering | Mid-band weather scale cue |
| V6 | Eclipse shadow sweep on the far arch | medium engineering | Rare, spectacular; independent of the rest |
| V7 | Contingent: mist lid (inversion), chain lines of masts, GPU macro shadow heights, mountain shafts, Wall Engine near meshes | each needs its own go | Only if V0/V1 evidence asks for them |

**Budget.** All phases together: at most 1.0 ms GPU on the RTX A1000 (1920x1080, Medium, P0 / 35 W), no new per-frame main-thread work above 0.1 ms, at most 5 new draw calls. The A1000 is GPU-bound at 40-44 ms, so each phase carries its own gate and is reported back if it misses.

**New worlds only (decided 2026-11-09).** Every visible change in this plan is a feature on the shared world-generation ladder. A world shows a change only if it was created at or after the step that froze it. Each phase with a visible change ends by freezing the next generation and creating a test world. V0 and the V1 capture matrix change nothing and are exempt. Section 11 has the details.

## 1. What exists today

### 1.1 Corrections to the brief

These were found in code and change the plan. The primary agent should confirm them and update the stale docs and memory notes.

1. **Terrain height shadows are on by default.** `RingGraphicsQualityController.PresetHeightShadow` enables them on every preset, and `GameplayShadowDefaults.MigrateHeightShadowDefault` drops a saved explicit `false` once (commit 3110b7e, 2026-10-25, "heightfield default"). The top of `docs/shadow-revamp-plan.md` and the memory note still say off. The plan's 2026-10-22 section lists blockers B1-B4; the doc does not record whether B3 (planet near map, +1.7 ms in the forest fixture) and B4 (authoritative A1000 runs) were closed.
2. **The far-arch remote shell already uses the undimmed sun.** `day_side_atmosphere.gd` publishes `ringworld_haze_sun_irradiance_remote` without the camera-column visibility, and `ring_atmosphere.gdshaderinc` (`ring_haze_sample`) puts it in `p_remote.E_sun`. A storm at the camera no longer dims the far arch's sunlit air. Of the three items the brief says wait on light shafts, only per-target cloud shadow on haze and valley mist pooling remain.
3. **The user's air is pure Rayleigh at half density.** `RingAtmosphereModel.ApplyPhysicalProfile` maps Aerosol 0 to mist extinction 0, and VisibilityFullKm 400 to Koschmieder V = 600 km, where `sigma_A = max(3.912 / V - sigma_R.g - sigma_M, 0)` clamps to 0 (Rayleigh alone is 288 km). With no Mie scatterer present, ForwardScattering has no effect outside storms. SunWarmth 0 is not neutral: neutral is 0.25, and 0 tints the haze sun cool.
4. **Climate machines (Wall Engines) are data and terrain only.** The registry, recipe and forecourt terrain exist (spec phases C1, C2). No mesh builder, stand-in layer, streamer or far-viewport strips exist, and the anomalies are not wired into weather.
5. **Scale anchors already exist.** `RingScaleAnchorLayer` (scene node `ScaleAnchors`, enabled) scatters procedural towers: 360 m minor and 1,600 m major, one candidate per 24 km cell at 19 % chance, within 180 km, main camera only, a teal beacon on top, no shadows, no menu entry and no in-world identity.
6. **The eclipse is rendered as one global scalar.** `ringworld_solar_visibility` is the player-local coverage, so the whole ring darkens at once. The physical umbra crosses the ring in about 390 s; the per-point shadow is listed in `docs/eclipse-false-sun-spec.md` as optional polish (E7) and is not built.

Smaller facts used below: cue lines are 126 per wall (249.3 km apart), not 125. Distant rain curtains are unshaded quads at 3-8 km with no haze, no sun direction and no cloud shadow. The hull shader has conduits, a top rail and a cross grid but no other wall detail, and the hull receives neither macro nor far-cloud shadow. The far regional band sets `analytic_cloud_shadow_enabled = false` (`RingScaledFarViewport.cs:1732`). Emitter stand-ins leave the opt-in minimum pixel width at 0; the projector work found that a 1 px or dithered spike is erased by TAA/FSR beyond about 22 km.

### 1.2 Geometry that shapes everything (derived)

- Sun: the ring tilt is 15 degrees, so the sun direction has a constant across-ring component of sin 15 = 0.259 toward one wall. The day is 4,500 s; the terminator moves at 6.98 km/s at TimeScale 1.
- **One wall is always the caster.** The sunward wall's inner face never sees the sun, and the opposite wall's inner face is lit at a steady 15 degrees all day. This is already what the hull `light()` produces. It is a free compass: call them the Shade Wall and the Sun Wall.
- Wall shadow reach across the floor = 10 km x 0.259 / sin(e), e = sun elevation: 2.7 km at noon, 15 km at 10 degrees, 30 km at 5, 74 km at 2, 148 km at 1, the full 318 km at 0.47 degrees. Elevation falls 0.077 degrees per second near the terminator, so the shadow edge crosses the last 290 km of floor in about 59 s. On the arch the same edge is a diagonal line across the ring width, 409 km long.
- Pixel scale at 1080p and 80 degrees FOV is 643 px per radian. The ring width (318 km) is 41 px at 5,000 km and 29 px at 7,000 km: the far arch is a ribbon 30-40 px wide. A 10 km wall is 1.5 px at 4,300 km. A 1.8 km spire is 1.5 px at 770 km.
- By day the visible arch is sunlit terrain out to about 7,000 km (elevation about 37-45 degrees); beyond that it is the night side and reads as sky (accepted, haze decision 2).

## 2. Design thesis

Four mechanisms make a place read as vast. The plan assigns each to systems that exist.

1. **Graded air.** Each doubling of distance should change tone by a visible, non-saturating step. One exponential layer grades about 1.5 decades of distance (T from 0.95 to 0.05 is a 58x range). The ring spans four. Three layers of different height and density cover three decades: ground mist for 1-30 km, the aerosol layer for 10-150 km, Rayleigh for 100-2,000 km. Beyond that the view leaves the air and the arch is seen through about two airmasses. Today only the Rayleigh layer is active (1.1 item 3), so the first 50 km has almost no depth gradation and long views turn blue without first losing saturation.
2. **A ladder of known sizes.** A thing has known size once the player has stood next to it. The ladder should climb by a factor of about four to six per rung, and every rung must survive to the distance where the next rung takes over. Regular pitch works better than isolated objects at long range: a row of identical marks gives a continuous depth gradient.
3. **Edges that move.** A shadow edge or light front crossing known ground at a readable speed turns distance into time. The ring has these for free: the wall shadow tide (about 5 km/s), the terminator (7 km/s), the eclipse edge (26 km/s), the grid pulse (105 km/s), the megastorm (350 m/s), cloud shadows (wind).
4. **Light with volume.** Shadowed and lit air make the space between here and there visible as a thing with size.

**The ladder as it stands and as proposed.**

| Rung | Size | Readable to (3 px) | State |
|---|---|---|---|
| Tree | 25 m | 5 km | exists; billboards end at 5.5 km |
| Mast band | 100 m | 21 km | proposed (V2: livery on the existing towers) |
| Minor mast | 360-400 m | 80 km | exists as "scale anchor", no identity |
| Major mast, spire, projector tower | 1.6-1.8 km | 340-390 km | exist; spire stand-ins vanish early (V2 fix) |
| Wall course | 1 km | 210 km | proposed (V2: hull shader) |
| Rim wall | 10 km | 2,100 km | exists, featureless |
| Wall Engine bay | 24 x 10 km | 2,000 km on the arch (foreshortened) | specified and approved, not drawn (V2) |
| Emitter column pitch | 53 km | whole arch | exists as geometry; marked only by the pulse |
| Row pitch, ring width | 314, 318 km | whole arch | exists |

**What not to do.** Do not add objects for the band beyond about 2,000 km. Everything smaller than the ring width is under a pixel there. That band is served by contrast (V1), the terminator (V3), the eclipse edge (V6) and the pulse, not by geometry.

## 3. Area 1: light shafts and shadowed air

### Recommendation

Build a **path sun visibility volume**: one small scalar texture, indexed by screen direction and distance, holding the in-scatter-weighted mean sun visibility along each view ray up to that distance:

```
V(dir, d) = Integral_0^d vis(s) w(s) ds / Integral_0^d w(s) ds,   w(s) = scattering(h(s)) * T(0 -> s)
```

Every haze consumer multiplies `S_sun` by `V` at its own direction and distance, which is exactly the `vis` slot in the `RingHaze` contract (`L = L_surf * T + S_sun * vis + S_amb`). The near shell then takes the undimmed sun (`ringworld_haze_sun_irradiance_remote` already publishes it) and `V` replaces the camera-column `ringworld_air_sun_visibility`.

One compute pass fills it: one ray per column, marching outward on exponentially spaced steps and writing the running ratio into each distance slice as it passes. `vis(s)` comes from fields that already exist, added in stages:

- **LS-1, clouds.** The same combined cloud-shadow lookup the ground receivers use (Sunshine near and far maps, far-weather bake beyond them), so a shaft always lands on its own ground shadow. Height gate: the maps are ground-projected along the sun, valid below the cloud base; fade to lit between base and top.
- **LS-2, rim wall.** The closed form from V3. This gives the dusk tide in the air: dark below a slanted plane, lit above it.
- **LS-3, near terrain.** The height-shadow clipmap stores a shadow-line height, so "air at height h is shadowed if h < H" is one sample. Covers 8 km.
- **LS-4, far terrain (contingent).** Needs the macro shadow in shadow-height format (V7).

Starting size 160 x 90 x 32 slices, R8 (0.5 MB), slices from 100 m to about 210 km, value held beyond. Frustum-aligned with no history: the far viewport copies the main camera's FOV (`RingScaledFarViewport.cs:1004`), so one screen UV serves both viewports, the sky and Sunshine. Directions outside the frustum (radiance cubemap, CPU ambient) fall back to today's camera-column scalar. Host it like `RingAtmosphereLuts` (render-thread compute from `RDShaderSource`, so no Sunshine-style reimport for the builder). `sampler2DArray` globals are proven in this engine build by the height-shadow work; use that with a manual slice lerp unless the lead verifies a 3D global.

Strength is the existing "Cloud shadowing of haze" slider (0 = today's path, pixel identical). No new toggle. Resolution belongs to the quality presets.

### The candidate idea, challenged

The brief's candidate is a low-res view-ray march of the existing shadow fields with no froxel volume. The march is right. "No volume" is not, for three reasons:

1. A 2D result is bound to one depth per pixel. Sunshine hazes clouds at the cloud's depth, the ocean and containment field are transparent, per-instance vegetation haze is evaluated in the culling compute, and the far viewport has its own depth buffer. Each needs `V` at a different distance along the same ray.
2. The object = cloud = sky contracts in `haze_consistency_gpu_test` hold only if every consumer reads the same value for the same direction and distance. A distance axis gives that by construction.
3. The distance axis is nearly free: the march already passes through every slice. The cost is the march, not the storage.

So: keep the march, add 32 slices. It is still not a froxel lighting volume: one scalar, no per-cell lighting, no integration pass.

### Alternatives rejected

- **Full froxel in-scatter volume.** Rejected in the haze plan (P8: "no froxel AP volume") for reasons that still hold: forward per-material consumers, 900 km ranges, a second viewport. It would also replace the analytic haze that the contracts are built on.
- **Screen-space radial blur from the sun.** Works only with the sun on screen, adds light regardless of depth, cannot show a shadowed lane when looking away from the sun, and has no place in the LDR root.
- **World-aligned (lat-long) volume.** Rotation-stable and covers the cubemap, but needs about 7x the rays for the same angular resolution. Keep as the fallback if rotation shimmer shows.

### Will it be visible?

Not with today's air. In pure half-density Rayleigh a 2 km sunbeam has optical depth 0.014: about 2 % contrast, invisible. Only structures of 30 km and more would read (a storm-anvil lane, the wall tide). With a mist layer at 30 km visibility and forward scattering, the same beam has optical depth 0.26 and five to eight times the phase gain toward the sun: classic rays. **V4 therefore depends on the V1 decision.** If the user keeps pure Rayleigh, V4 shrinks to a coarser volume (96 x 54 x 16) for large lanes and the wall tide only.

### Systems touched

`materials/ring_atmosphere.gdshaderinc` (`ring_haze_sample`, the multiply), `ring_haze_shared.gdshaderinc` (contract comment only), `ring_local_sky.gdshader`, `scripts/day_side_atmosphere.gd` (publish the undimmed sun to the near shell when the volume is valid), a new host next to `scripts/Atmosphere/RingAtmosphereLuts.cs`, `RingVegetationHaze` (per-instance sample), `RingPoiHaze` consumers (through the include), `SunshineCloudsCompute.glsl` (one sampler binding: an ABI change with the known reimport gotcha), `project.godot` globals.

If the lead judges the Sunshine binding too risky for a first cut, clouds keep the camera-column scalar and the plan records the cloud contract as "agrees in clear air only" until the binding lands. Decide this in the design step, not during implementation.

### Cost and budget

Est. march: 14,400 rays x 32 steps = 0.46 M samples per frame, 0.15-0.3 ms on the A1000. Consumers: one array tap per haze evaluation, est. 0.1 ms main, 0.05 ms far. **Gate: 0.5 ms total on the A1000; march alone 0.3 ms.** If missed, halve the rays per frame (checkerboard columns) before lowering resolution. Engineering: large (lead design, then about six work orders).

### Verification

- New contracts in `haze_consistency_gpu_test`: `path_visibility_identity` (volume all 1 is bit-identical to shafts off under a clear camera column), `path_visibility_scales_s_sun` (uniform 0.3: object = cloud = sky, S linear), and the existing 15 contracts unchanged.
- `path_visibility_reference_test`: the march against a double-precision CPU reference on a synthetic slab shadow (lane edges within one slice; ratio within 2 %).
- `terrain_render_attribution_test -- --haze-only` with a new label for the budget gate, on the A1000.
- Captures (Movie Maker, `tools/media/ffmpeg_tools.ps1`): a slow pan under broken cumulus looking sunward and away (`sheet`, `flicker` for temporal stability); a dusk time-lapse on the ground (`mp4` for the user); `compare` of strength 0 against the pre-change baseline for the "no visible change" claim.

### User decisions

Whether shafts are wanted if the air stays pure Rayleigh (reduced scope). Whether clouds may lag the volume in a first cut.

## 4. Area 2: aerial perspective

### Recommendation

Treat this as calibration with the sliders that exist, run as a capture matrix, before any new shader work. Two axes.

**Axis A, the sky-to-surface ratio.** The milky look is a ratio problem. Effective ground albedo is 0.006-0.025 (terrain albedo scale 0.15), about ten times darker than Earth, while haze, sky and clouds are lit by the real sun. The user moved one side with Haze brightness 0.52. Clouds were not moved, so with clouds in frame exposure still follows them and far terrain is crushed, which costs exactly the far detail vastness needs. Trial three levels of the ratio:

- today (terrain 0.15, haze brightness 0.52);
- surfaces up: Terrain albedo 0.25 and 0.40 with Grass albedo scaled to match and Haze brightness raised toward 0.75 and 1.0 (both sliders exist; `atmosphere_visual_test` already takes `--terrain-albedo=` and `--haze-brightness=`).

Trees, POIs, hull and water have no albedo slider, so the "surfaces up" cells will show them too dark against the ground. That is expected in the trial and tells us whether the full retune (option C in the haze plan: all surface albedos up about 2.5x, sun energy 4 to about 1.6) is worth doing. Option C is not recommended as the first step: it retunes every lit asset and every golden, and the two-slider trial answers the question first.

**Axis B, the layer profile.** Three cells:

- today: pure Rayleigh, density 0.5;
- plus mist: Aerosol 0.2-0.35 (this is the mist load), Mist height 500-700 m, AltitudeFalloff 0.5 so the heights are not halved;
- plus aerosol and a sun glow: Visibility slider 100-130 (V = 150-195 km, the range where `sigma_A` is above zero), ForwardScattering 0.4-0.55 (g 0.68-0.75), Aerosol height 1,500-2,000 m, SunWarmth 0.25 (neutral).

These are starting values for the matrix, not proposed defaults. Density may need to drop as layers are added.

This covers the three ideas in the brief with no new code. The sun glow is the Mie phase function once a Mie scatterer exists. "Desaturation before bluing" is what grey mist and aerosol do physically: near paths run through the grey low layers, long and high paths through blue Rayleigh. Ridge tiers come from strong vertical stratification: valleys sit in the mist, ridges stand above it. The walls gain from it too: a 10 km wall whose foot fades into haze while its top stays sharp reads as very large.

Then bake the user's pick and retune the Natural and Cinematic presets, which the haze plan left pending. Under the new-worlds-only rule the pick is not written over today's defaults: it becomes the atmosphere settings of the generation step that freezes it (section 11.3), and worlds from before that step keep today's values.

### Contingent engineering: the mist lid (V7a)

An exponential mist layer has no top, so from altitude there is no visible haze line. If the matrix shows the tiers need a lid, generalise the mist profile to a logistic, `sigma_M0 / (1 + exp((h - h_lid) / w))`: flat below the lid, exponential above it. With the piecewise-linear height the plan already uses, the optical depth has a closed form (softplus), so it stays one layer, not a fourth. It touches `ring_haze_shared.gdshaderinc` (layer integral and sun transmittance), `RingAtmosphereModel` (`ApplyStorm` mirror, `PackCloudProfile`), `RingHazeSkyIrradiance` (CPU port) and the sky LUT build, and forces a Sunshine reimport. Lid 0 must take the old code path bit for bit. Two genuine tunables: Mist lid height, Mist lid sharpness. Est. +0.05-0.1 ms; gate 0.1 ms. Medium engineering.

Valley mist pooling proper (mist relative to the local basin floor, not sea level) needs a terrain datum along the ray. With an altitude lid, valleys near sea level already fill. Pooling in high basins stays deferred; if wanted later it is a low-pass "basin floor" field from the min-max pyramid sampled in the V4 march.

### Alternative rejected

A dedicated distance-desaturation term or a colour grade. It is not physical, it would have to be added identically to every consumer to keep object = cloud = sky, and the layered profile gives the same result from the existing model.

### Systems touched

Slider work: none in code until the pick is baked (`AtmosphereVisualSettings`, `AtmosphereVisualDefaults`, `RingAtmosphereModel` statics, `day_side_atmosphere.gd` exports, preset values, a settings-version migration like the 2026-10-23 one).

### Cost and budget

Zero GPU for slider changes. Adding mist and aerosol does not change the haze cost (the layers are always evaluated). One day of capture and review.

### Verification

- The matrix through the V0 audit poses: 3 x 3 cells x (ground horizon, 2 km, 12 km, up the arch, toward each wall) at noon and at 8 degrees of sun, auto-exposure off for matched comparisons and on for the "as played" set. One `sheet` per pose for the user.
- The V0 tier metric per cell: mean luma, saturation and local contrast of terrain pixels per distance band. Target: adjacent bands differ visibly, and the far bands keep local contrast.
- After baking: `haze_consistency_gpu_test --model=physical` (all contracts), `AtmosphereSettingsSmoke` with an old-file migration case, `atmosphere_visual_test` re-baseline on each machine.

### User decisions

Pick a matrix cell. Say what stopped the tuning at Haze brightness 0.52 and Aerosol 0 (if mist or glow was disliked for a specific reason, the matrix should avoid it). Whether a full option C retune is acceptable if the trial points there.

## 5. Area 3: the scale ladder

### Recommendation

**Find the gaps first (V0), then fill them cheapest-first with things that already have a place in the world.**

Expected gaps, to be confirmed by the audit:

- **5-150 km on the floor.** Tree billboards end at 5.5 km and forests become colour. The next man-made thing may be a spire up to 157 km away along the ring.
- **150-850 km.** Spire stand-ins dither out or are erased by TAA; the walls are blank.
- **850-2,000 km.** Only the conduit lines and the pulse.

Fills, in order of value per cost:

1. **Spire stand-ins keep a minimum width.** Turn on the opt-in `object_width_m` / `min_width_pixels` path the projector towers already use. Spires then hold to 700 km. One work order.
2. **Wall courses.** Give the hull shader a hierarchical pattern: a course line every 1 km of height (ten courses to the top), buttress ribs every 5 km along, a 100 m sub-course that appears only near. Each level fades by its pixel footprint so it never aliases. The wall becomes its own ruler in both viewports. In-world: the walls were laid in kilometre courses; the ribs carry the field conduits' load.
3. **Wall Engine bays on the wall.** Draw the approved 24 x 10 km bays (user decision U1) as a procedural pattern in the same hull shader: a recessed darker face, eight intake columns, three gallery lines at 2, 4 and 6 km, at the 100 regular stations per wall from `ClimateMachineRegistry`, the six offline bays darker. From the centre line a bay is 8.6 x 3.6 degrees, the largest known-size object in the world, visible from almost anywhere and readable on the arch to about 2,000 km. This is the far proxy of spec C5 done in the shader, ahead of the mesh. Day-readable material only; the spec's lit vent strips are a separate user call given the night-lights ruling.
4. **The towers get an identity: datum masts.** Proposal: the existing scale-anchor towers are the builders' levelling network, graduated survey staffs. Each carries alternating pale and dark 100 m bands so any mast can be read optically from its neighbours; the teal beacon is the sighting light. Minor masts become 400 m (four bands), major 1,600 m (sixteen), matching the projector towers. The banding makes each mast a ruler on its own and adds the missing 100 m rung. Shader-only change to `ring_scale_anchor.gdshader` plus two size constants.
5. **Contingent (V7b): chain lines.** If the audit still shows the 5-150 km band empty, replace the random scatter with six lines of masts on the ground track of the six emitter chains: a minor mast every 8 km, a major every 24 km. From most places this puts a line of identical objects receding to both along-ring horizons and up the arch, under the path the pulse takes. About 270 instances in view, one MultiMesh. This changes the landscape's character, so it needs the user's yes.
6. **Moved: forest grain.** A crown-scale albedo and normal variation in the far terrain shader that fades by pixel footprint, so forests read as forests to 20-30 km. This is now the far canopy tint in `docs/terrain-vegetation-revamp-plan.md` (phase T6) and is no longer part of this plan.

Already there and likely enough for their bands, to be confirmed by the audit: emitter-site islands form a 53 x 314 km lattice in the oceans; the conduit lines and 50 km cross grid mark the far shell.

All of these must take regular haze, shadows and lighting through the shared includes, with no per-object toggles.

### The audit (V0)

See section 7, V0. In short: fixed poses and headings, a log-spaced distance band table, a registry-driven list of ladder objects per band with their projected size, a fly-out sequence that measures the distance at which each object type actually vanishes, and the tier metric for section 4.

### Alternative rejected

Random landmark scatter as the main ladder (today's scale anchors). An isolated object of unknown identity at unknown spacing measures nothing. Also rejected: any new object class for the far arch (sub-pixel, section 2), and Wall Engine meshes first (spec C5 is about ten work orders and the shader bay gives most of the far and mid read).

### Systems touched

`materials/ring_emitter_standin.gdshader` and `EmitterStandInLayer` (width uniforms), `materials/ring_hull_global.gdshader` (courses and bays, both viewports through `RingTerrainGlobalLod` and `RingScaledFarViewport`), `RingStructuralCues` or a sibling for the station phase, `ClimateMachineRegistry` (read only: station positions and offline flags), `RingScaleAnchorLayer` and `materials/ring_scale_anchor.gdshader`.

### Cost and budget

Hull pattern: a few procedural terms on wall pixels, est. under 0.05 ms. Stand-in width: none. Masts: one existing draw. Gate for all of V2: 0.1 ms on the A1000 at a wall-facing pose, zero new draw calls. Engineering: small (four work orders).

### Verification

- V0 audit before and after: the band coverage table and the vanish-distance table.
- `emitter_far_standin_test` (extended for the width path), `poi_haze_gpu_test`, `shadow_compose_identity_gpu_test` (hull untouched in lighting terms), a new `hull_pattern_gpu_test` (pattern phase matches `ClimateMachineRegistry` stations; no aliasing: `flicker` on a slow pan at 50, 150 and 500 km from the wall).
- `tools/rim_wall_check.gd` views (floor, far50, arch, top_along) at noon, low sun and night, as before/after sheets.

### User decisions

Accept "datum masts" as the towers' identity, or name another. Draw Wall Engine bays now, ahead of the climate phases and the v6 default. Chain lines: yes, no, or wait for the audit.

## 6. Area 4: large shadows and slow motion

### Recommendation

Five pieces, in order of value per cost.

**4a. The rim-wall shadow as a first-class analytic term (V3).** Today the wall's shadow exists only inside the 4 km macro mask, capped at a 96 km trace and applied at strength 0.34. It should be exact, full strength and everywhere. The test is closed form: follow the sun ray from the receiver to the wall plane (`t = across distance / 0.259`), and it is shadowed if the ray's radial distance there is at least `R - 10 km` (`RingTerrainShadowGeometry.RayHitsStructuralWall` has the CPU version). Penumbra width is `t` times the sun's angular radius. Add it to `ring_shadow_compose.gdshaderinc` as part of the terrain term for every receiver in both viewports, and turn off the macro mask's wall component when it is active so the two do not stack.

What the player gets: every dusk a shadow edge leaves the Shade Wall and crosses the floor in about a minute, accelerating; every dawn it retreats. On the arch the terminator stops being a 2,900 km gradient and gains a crisp diagonal edge 409 km long at each end of the day side.

With it, review the direct-light day ramp. `smoothstep(-0.2, 0.35, dot(up, sun))` scales direct sun down by a quarter at 10 degrees of elevation and keeps it alive to 11.5 degrees below the horizon. Raking low sun is the light that shows relief. Proposal: direct light uses a narrow ramp near the horizon and lets N.L and the wall shadow do the rest; the wide ramp stays for ambient, sky and the night blend. This is a look change at dawn and dusk (the user already accepted darker sunsets for the height shadow, B1) and needs a before/after sign-off.

**4b. Mountain shadows at the terminator.** The macro mask already reaches the far arch. Its strength is one slider (0.34; range 0-0.8): capture the terminator at 0.34, 0.6 and 0.8 and let the user pick. That is slider work. The engineering version is contingent (V7c): build the macro on the GPU in the height-shadow format (shadow-line height, not a ground mask), which removes the 190-245 ms CPU rebuild every 6.4 s, lifts the 96 km trace cap, and is what LS-4 needs for mountain shadows in the air. It is S8 in the shadow plan and needs its own go.

**4c. Far cloud shadows.** They exist and reach the opposite arch. Work: verify in the V0 captures that they read at 1,000-5,000 km, close the far regional band gap (1.1), and let the hull receive cloud shadow so the walls carry it too. At low sun, cloud shadows displaced from their clouds are a strong height cue; check the bake's projection keeps that offset.

**4d. Rain curtains (V5).** Give them regular haze (they have none), a sun term (forward-scattering phase so a side-lit or back-lit shaft glows, sun visibility from the cloud-shadow lookup so a curtain under its own cloud is dark and one reached by low sun is bright against the base), and a longer range. Storm cells are 32 km across; rain under them should read to at least 40 km instead of 8.

**4e. Eclipse shadow sweep (V6).** Make solar visibility spatial in the far viewport. At ring scale the giant's limb is a straight edge, so the shadow is a plane crossing the ring: one global (plane normal, offset, penumbra width), evaluated per vertex in far terrain, hull and far clouds, and at the remote midpoint in the far-arch haze shell. The value at the camera is unchanged, so the main viewport, the false-sun event timing and the eclipse tests keep their scalar. The player sees the dark edge (26 km/s, penumbra about 1,140 km) run around the arch for six and a half minutes before and after totality. This is spec item E7 in a reduced form and changes the spec's "global darkness" stance, so it needs the user's yes.

**Slow motion.** Most of it is the edges above. One tunable is missing: the cloud field drifts at (2.2, 0.7) m/s (`WeatherWindMetersPerSecond`, an export, not in the menu), so cloud shadows barely move. Real cumulus shadows cross a hillside in a minute or two at about 10 m/s. Whether to speed the drift is a user decision because storm arrival, the megastorm and the seeded weather clock all read the same wind.

### Alternatives rejected

- **Sharpening the terminator by narrowing the day ramp alone.** It only moves a gradient; the physical edge is the wall shadow.
- **Hardware shadow maps for walls or far terrain.** The far viewport has no shadow maps by design, and a 10 km caster at 300 km is outside any cascade.
- **A full per-pixel umbra in the main viewport for the eclipse.** `ringworld_effective_solar_day` is called without a position in many shaders; the gradient across the main viewport's 1,200 km is small against a 1,140 km penumbra. Far viewport first; extend only if the handoff shows.

### Systems touched

4a: `materials/ring_shadow_compose.gdshaderinc`, `ring_terrain_global.gdshader`, `ring_terrain_proxy`, Terrain3D lighting include, oceans, hull, `RingTerrainGlobalLod` (macro wall flag), `celestial_lighting.gdshaderinc` (direct ramp, if approved). 4c: `RingScaledFarViewport.cs` (regional band flag), `ring_hull_global.gdshader`. 4d: `scripts/RingDistantRainCurtains.cs`, `materials/ring_distant_rain_curtain.gdshader`. 4e: `scripts/day_side_atmosphere.gd` (publish the plane from `CelestialEclipseMath`), far terrain, hull and far cloud shaders, `ring_haze_eval_remote` caller.

### Cost and budget

4a: a few ALU per fragment, est. under 0.05 ms; gate 0.05 ms. 4c: none measurable. 4d: 25 quads today; gate 0.1 ms at a storm pose. 4e: per-vertex in the far viewport; gate 0.05 ms. Engineering: 4a small-medium (lead design for the compose include and the ramp, three work orders), 4c small, 4d small-medium, 4e medium.

### Verification

- 4a: a new `wall_shadow_reference_test` (GLSL against `RayHitsStructuralWall` at sun elevations 0.5-75 degrees, both walls, scale 1 and 1/1000); `shadow_compose_identity_gpu_test` with the term off (pixel identical); `shadow_quality_capture` dusk and dawn poses; a dusk time-lapse from the floor 30 km and 150 km from the Shade Wall and one of the terminator from the night side (`mp4` for the user, `flicker` for pops when the macro wall component switches off).
- 4c: V0 arch captures with and without cloud shadows (`compare` heatmap).
- 4d: `rain_cloud_compositing_gpu_test` (still veiled once by the rain composite), a storm pose at 10, 20 and 40 km with the sun behind, beside and in front.
- 4e: `eclipse` smokes unchanged at the camera; a contract that the far-viewport value at the camera's own position equals the scalar; a time-lapse of ingress from the floor looking up the arch.

### User decisions

The direct-light ramp change at dawn and dusk. Macro shadow strength. The eclipse sweep. Cloud drift speed.

## 7. Phases

World gating and order (decided 2026-11-09): every phase below that changes production code depends on phase T1 of `docs/terrain-vegetation-revamp-plan.md`, which builds the generation ledger, and ends with the freeze described in section 11.4. V0 shares its harness with that plan's T0. The user chose to run that plan's T1-T3 (rivers) before V2 onward; V0 and the V1 capture matrix can start at once.

Each phase is independently testable and committable. "Lead" is `claude-lead`; "impl" is `implementer`. Per work order use `tools/run_tests.ps1 -Since HEAD` or `-Files`; the primary agent runs the gate suite at each commit point. Captures go under `.godot/captures/vastness/<phase>/`; delete raw frames after making sheets and run `tools/prune_test_output.ps1 -Apply`.

### V0. Audit harness and baseline (no production change)

- **Goal.** One tool that says, for a fixed set of views, what is visible in each distance band and how well the air grades it. Its tables are the acceptance evidence for V1-V6.
- **Deliverable.** `tools/vastness_audit.gd` plus a C# helper, with a `tools/test_manifest.json` entry (tags `vastness`, windowed GPU, full tier only). It reuses the boot, pose and time-of-day search code in `tools/shadow_harness_common.gd` and the depth capture effect `tools/shadow_depth_capture_effect.gd`.
- **Poses.** Ground on the centre line; ground 30 km from each wall; a ridge at about 1.5 km; a coast; a forest clearing; 2 km and 12 km altitude; the wall top; the user's saved pose. Headings: along the ring both ways, toward each wall, and 30 degrees up the arch. Times: noon, sun at 8 degrees, sun at 2 degrees, planet-lit night.
- **Outputs per view.**
  1. Colour capture and a distance map (main depth where present; analytic ray-to-ring-floor distance at 240 x 135 for backdrop pixels).
  2. Band table over 0.1-0.5, 0.5-2, 2-8, 8-30, 30-120, 120-500, 500-2,000 and 2,000-7,000 km: share of screen, mean luma, saturation, local contrast (the tier metric).
  3. Ladder table: from the registries (emitter sites, projector anchors, scale anchors, climate stations, tree streamer), every ladder object in the frustum with its band and projected height, and whether it is detectable in the pixels at its projected position.
- **Fly-out.** For each object type (tree, minor and major tower, spire, projector tower, a wall cue line), a Movie Maker dolly back on a log schedule from 10 m to 300 km with the object centred. Output: a `sheet` and the measured vanish distance against the theoretical 1.5 px distance.
- **Result.** `results.md` with the three tables and a short list of empty bands; sheets cited from it. The baseline tables are pasted into a new "Audit baseline" section of this doc, per machine.
- **Proposed acceptance contract for later phases** (the user confirms the thresholds after seeing the baseline): in at least 80 % of ground views, every band that holds 2 % or more of the screen contains a ladder object of 3 px or more, or a regular-pitch feature; adjacent bands differ in mean luma by a visible step; the 500-2,000 km band keeps local contrast above the baseline.
- **Who.** Lead writes the tool spec (half a day); impl builds it in two work orders (capture and tables; fly-out). Capture run about 15 minutes of GPU time.
- **Depends on.** Nothing. V1's matrix can start on `atmosphere_visual_test` while V0 is built.

### V1. Aerial perspective calibration (slider work)

- **Goal.** A haze profile and sky-to-surface ratio the user picks from evidence.
- **Work.** Impl adds any missing haze flags to `atmosphere_visual_test` (it has `--terrain-albedo=` and `--haze-brightness=`); runs the 3 x 3 matrix of section 4 through the V0 views; the primary agent presents sheets and the tier metric. After the pick: bake defaults, preset retune, settings migration.
- **Verification.** Section 4. **Depends on.** V0 for the metric (the sheets alone can start earlier).
- **Gate G1.** The pick decides V4's scope (full, or large-lanes only) and whether V7a (mist lid) and a full option C retune are proposed.

### V2. Cheap ladder fixes (small shader work)

- **Work orders.** (1) Spire stand-in minimum width. (2) Wall courses in the hull shader. (3) Wall Engine bays in the hull shader, fed by the registry's station phase and offline flags. (4) Mast livery and sizes.
- **Verification.** Section 5; V0 before and after. **Depends on.** V0; user answers on bays and masts. Lead reviews (2) and (3) for aliasing before the implementer starts.

### V3. Rim-wall shadow and terminator (small-medium engineering)

- **Work.** Lead designs the analytic term in the compose include, the macro wall hand-off and the direct-ramp proposal. Impl: (1) GLSL function and reference test; (2) wire receivers in both viewports; (3) the ramp change behind a debug switch for the before/after; (4) hull receives cloud shadow and the regional band gap (4c).
- **Verification.** Section 6, 4a and 4c. **Depends on.** Nothing hard; best after V1 so captures use the final haze.

### V4. Path sun visibility (main engineering)

- **LS-0, design and feasibility (lead).** Confirm the global array binding, the consumer call shape for fragment, vertex and compute callers, the Sunshine decision, and measure a stub march on the A1000. **Gate G2:** march at most 0.3 ms or the resolution drops before anything else is built.
- **LS-1 (impl, three work orders).** Host and builder with cloud visibility; consumers in `ring_haze_sample` and the sky; vegetation and POI paths; contracts.
- **LS-2 (impl).** Wall term in the march (V3's function).
- **LS-3 (impl).** Height-shadow clipmap term.
- **LS-S (impl, if approved in LS-0).** Sunshine binding and reimport.
- **Verification.** Section 3. **Depends on.** G1, V3.

### V5. Rain curtains (small-medium engineering)

- **Work.** Haze through the shared include; phase and cloud-shadow sun term; range extension with a count cap. **Verification.** Section 6, 4d. **Depends on.** V1 (the phase function's g).

### V6. Eclipse sweep (medium engineering)

- **Work.** Lead: plane derivation from `CelestialEclipseMath` and the handoff rule. Impl: publish the global; far terrain, hull, far clouds, remote haze shell. **Verification.** Section 6, 4e. **Depends on.** User approval; otherwise independent.

### V7. Contingent items (each needs its own go)

- **V7a Mist lid.** If G1 asks for it. Medium.
- **V7b Chain lines of masts.** If the audit still shows 5-150 km empty after V2 and the user agrees. Small-medium.
- **V7c GPU macro shadow heights** (shadow plan S8) and **LS-4** mountain shafts. Large.
- **V7d Wall Engine near meshes and streamer** (climate spec C5). Large; existing spec.
- **V7e Mast shadows** as analytic casters in the height-shadow clipmap (a 1.6 km gnomon with a 30 km shadow at low sun). Small, after V7b.

## 8. Budget table (A1000, 1920x1080, Medium, P0 / 35 W)

| Phase | Gate | Measured with |
|---|---|---|
| V1 | 0 ms (parameters only) | `terrain_render_attribution_test -- --haze-only` |
| V2 | 0.1 ms, 0 new draws | `perf-capture` A-B-A at a wall-facing pose |
| V3 | 0.05 ms | `shadow_attribution_test` new label |
| V4 | 0.5 ms (march 0.3) | `--haze-only` new label plus a `RingPerformanceGpu` marker |
| V5 | 0.1 ms at a storm pose | A-B-A |
| V6 | 0.05 ms | far-viewport GPU time, A-B-A |
| V7a | 0.1 ms | `--haze-only` |
| **Total V1-V6** | **1.0 ms** | |

Main-viewport run-to-run noise is 3-6 ms, so every gate is judged on per-pass markers and A-B-A medians, never on whole-frame deltas. The 4070 numbers are indicative only.

## 9. Open questions for the user

Decided on 2026-11-09, in answers given for the terrain and vegetation plan:

- New worlds only applies to this plan ("new worlds only is a universal thing at the moment, all of these worlds are for testing"; confirmed "yes").
- Every major visible change gets its own frozen generation and a fresh test world ("make new worlds for every major change that would require a new world").
- Order: one shared audit harness, then the terrain plan's T1-T3 before V2 onward.
- The grass Cards default is a global change and the user's explicit exception to the rule.

The nine questions below are this plan's own and have not been answered.

Blocking for the phase named:

1. **(V1)** May the trial move the sky-to-surface ratio by brightening terrain and grass, and is a full albedo and sun-energy retune (option C) acceptable if the trial points there? What made you stop at Haze brightness 0.52 and Aerosol 0?
2. **(V2)** Are the towers "datum masts" (banded survey staffs), and may the Wall Engine bays be drawn on the walls now as day-readable material, ahead of the climate phases?
3. **(V3)** May direct sunlight at dawn and dusk change: stronger raking light down to the horizon, then a hard wall-shadow edge, instead of today's long soft ramp?

Not blocking yet:

4. **(V4)** If the air stays pure Rayleigh, do you still want the reduced shafts (large lanes and the dusk tide only)?
5. **(V6)** May the eclipse darken the far arch as a moving edge instead of all at once?
6. **(4b)** Macro terrain shadow strength: pick from 0.34, 0.6, 0.8 captures.
7. **(slow motion)** Should the cloud field drift faster so its shadows visibly cross terrain? It changes storm arrival timing.
8. **(V7b)** Chain lines of masts along the six emitter chains: yes, no, or decide after the audit.
9. **(housekeeping)** Were shadow blockers B3 and B4 closed when height shadows became the default? The doc does not say.

## 10. Risks

- **Shafts invisible after all the work.** Mitigated by G1 (haze decided first) and LS-0 (cost measured before the build).
- **Consistency contracts.** V4 and V7a touch the shared haze path. Both keep a bit-identical off path and add contracts before wiring consumers.
- **Sunshine reimport.** Any edit to `ring_haze_shared.gdshaderinc` or the Sunshine compute needs the delete-md5, touch, windowed-import routine in the haze plan's gotchas. V4 keeps its builder out of the imported shaders; V7a cannot avoid it.
- **A1000 headroom.** There is none to spare. Phases are ordered so the free ones (V1, V2, V3) land first and the 0.5 ms phase is gated twice.
- **Hull pattern aliasing.** Regular lines on a surface seen at grazing angles from 150 km shimmer unless every level is footprint-filtered. The lead reviews the filter before implementation; `flicker` on a pan is the test.
- **Look changes stacking.** V1 and V3 both change the dawn and dusk look. Capture V3 on the V1 profile, not on today's, so the user signs off one combined result.
- **Stale docs.** Section 1.1 lists six facts that differ from the handoff docs. Until those docs are corrected, new agents will plan against the wrong state.
- **Feature branches in shared shaders.** The new-worlds-only rule keeps the old path beside the new one in the haze, shadow-compose, hull and rain shaders. Each phase already has an identity contract for its off path; section 11.5 reuses those as the old-world check. A miss would change an old test world's look, which is a small cost because all current worlds are test worlds.
- **Dependency on the terrain plan's T1.** No visible phase here can freeze until the ledger exists. V0 and the V1 matrix are not blocked.
- **Settings are global.** The haze pick needs a second settings file for newer generations (section 11.3). If the load order cannot guarantee that the generation is known before the atmosphere settings load, the lead must fix the order first.

## 11. New worlds only: how this plan is versioned

Added 2026-11-09. The mechanism is shared with `docs/terrain-vegetation-revamp-plan.md`; its section 2.2 is the full definition. This section says what it means for rendering work.

### 11.1 The rule

The user's words: "new worlds only is a universal thing at the moment, all of these worlds are for testing", and "make new worlds for every major change that would require a new world".

- A world keeps the look of the generation it was created at.
- Every major visible change is frozen as the next generation, and a fresh test world is made for it.
- All current worlds are test worlds, so the checks that protect older generations are kept light.
- The grass Cards default is global on purpose: the user's explicit exception.

### 11.2 Why rendering needs a mechanism, and the options

Haze, shadows, the hull and the eclipse are driven by global state: project shader globals, one atmosphere model, and grave-key settings saved in `user://` files that every world shares. Nothing in the renderer knows which world is loaded. A change to a shared shader or a default therefore changes every world unless something tells the renderer the world's version.

| Option | How it gates | Verdict |
|---|---|---|
| A. The world generation from the terrain recipe | The recipe stores `world_generation`. A ledger maps it to features. One owner publishes the feature set at world load as a C# static and a shader global bit mask, `ringworld_world_features` | **Recommended.** Already frozen per world, already designed for the terrain plan, one axis for both plans |
| B. A render profile stored with each save | Each world carries its own copy of the atmosphere and shadow settings and a render version | Rejected. Makes every settings owner per-world, changes how the grave-key menu saves, and adds a second version axis. Out of proportion for test worlds |
| C. No gating; each visible change is an agreed exception | Shared shaders and defaults change for all worlds | Rejected as the general rule; it is what the user ruled out. Kept only for explicit exceptions such as the Cards default |
| D. A build flag or branch per look | Old looks live in old builds | Rejected. Old and new worlds could not be loaded side by side |

### 11.3 How a rendering change is gated

- **Shaders.** A spatial shader branches on a bit of the global `ringworld_world_features`. The branch is a uniform test, so its cost is not measurable. Compute shaders (Sunshine, the path-visibility march, the LUT builders) receive the bit through the parameter buffers they already have.
- **C# systems.** A system asks the ledger whether the loaded world has its feature before it builds resources or publishes globals. A feature that is absent costs nothing: the path-visibility volume is not created, the eclipse plane is not published, the wall term is not evaluated.
- **Worlds without the recipe field.** Every recipe up to v8, and every world that does not use runtime generation (the gen-16 backends, Mega), is generation 1 and has none of this plan's features.
- **Settings stay global.** A slider the user moves applies to every world; that is the user's own action. A new slider that only affects a new feature is harmless in older worlds because the feature is off; the row is shown disabled there, as recipe rows are for older recipes.
- **Defaults do not change for old worlds.** One phase here changes default values: V1's haze pick and preset retune. From the step that freezes it, the atmosphere settings resource loads and saves a second file (created once by copying the current file and applying the pick). Worlds from before that step keep reading the current file. The same rule applies if a later answer bakes a different macro shadow strength (4b) or cloud drift speed as a default.
- **Review before freeze.** Work in progress is an open feature. A review world lists the open features it wants in its recipe, is disposable, and may change with every fix.

### 11.4 Which phases take a step

A phase "takes a step" when it ends with the freeze: the user signs off in a review world, the feature becomes the next generation in the ledger, two or three look goldens are captured at that generation, and `tools/new_generation_world.ps1` creates the test world, named `g<NN>-<step>-s1337`. Numbers are assigned at freeze, on one ladder shared with the terrain plan. Two phases accepted in the same review may share a step.

| Phase | Visible change | Versioning | Off-path check (worlds without the feature) |
|---|---|---|---|
| V0 audit harness | none | exempt | none needed |
| V1 capture matrix | none: harness flags only | exempt | none needed |
| V1 baked pick and preset retune | yes: default haze values | step "air"; second atmosphere settings file | the current settings file is byte-unchanged; `AtmosphereSettingsSmoke` loads it in a generation 1 world |
| V2 spire width, wall courses, Wall Engine bays, mast livery and sizes | yes | step "ladder" | `hull_pattern_gpu_test` and `emitter_far_standin_test` assert the pre-change output without the feature |
| V3 rim-wall shadow, direct-light ramp, hull cloud shadow, regional band | yes | step "wall shadow"; may share a step with V2 | `shadow_compose_identity_gpu_test` with the feature off |
| V4 path sun visibility | yes | step "shafts" | `path_visibility_identity`; the volume is not built |
| V5 rain curtains | yes | step "rain" | `rain_cloud_compositing_gpu_test` on the old curtain path |
| V6 eclipse sweep | yes | step "eclipse" | the eclipse smokes keep the single scalar |
| V7a mist lid | yes | a step | lid 0 takes the old code path bit for bit (already required) |
| V7b chain lines of masts | yes: placement | a step | the scale-anchor scatter is unchanged |
| V7c GPU macro shadow heights | none if its output matches the CPU mask; lifting the 96 km trace cap is visible | exempt as performance work when a parity test holds; the cap lift is a feature and takes a step | parity test against the CPU mask |
| V7d Wall Engine near meshes | yes | a step; also needs climate machines in the recipe | none drawn |
| V7e mast shadows | yes | a step | height-shadow clipmap unchanged |

**Exempt work, in general.** No production change (V0, capture matrices). Performance work whose output is proven equal by an identity or parity test. Fixes to plainly broken behaviour (a crash, a NaN, a black frame), which apply to every generation that has the affected code. Tuning inside an open feature before its freeze.

**After a freeze.** A look change to a frozen feature waits for the next step and is added as a new feature there. It is not edited in place.

### 11.5 Verification, kept in proportion

- **No new old-world GPU suite.** Each phase already defines an identity contract for its off path (sections 3-6). Those contracts run in a world without the feature and are the old-world check.
- **Existing goldens are not re-captured.** They are generation 1 fixtures. A phase that needs to re-capture one has changed an old world and must stop.
- **New goldens are added at the new generation only**, two or three poses per step, per machine.
- **One `compare` heatmap per phase report**: a generation 1 pose before and after the phase, judged on the heatmap, as review evidence.
- **The ledger tests** from the terrain plan (`world_generation_fingerprint_test`, `world_generation_contract_test`) cover the data side and the rule that exactly the ledger's features are reachable at each generation.
- **An exact before and after.** A v9 world with no river feature has the same terrain as the v8 world of the same seed. Any vastness step frozen before the terrain plan's rivers step therefore gives a test world that differs from the old one only in rendering. After the rivers step, compare against the previous generation's test world at the same seed.

### 11.6 What this changes elsewhere in this plan

- **Section 4.** The V1 pick is stored for new generations only (11.3). The "settings-version migration" in the systems list becomes a second file, not a rewrite of the first.
- **Section 5.** Forest grain moved to the terrain plan.
- **Section 7.** Every visible phase depends on the terrain plan's T1 and ends with the freeze. The order is V0 and the V1 matrix first, then the terrain plan's T1-T3, then V1's freeze and V2 onward.
- **Section 8.** Budgets are unchanged. A feature that is absent costs nothing, so older worlds do not pay for newer generations.
- **Old generations.** How many stay loadable is an open point for both plans (terrain plan, section 13.2, item 14).

## 11. User answers (2026-11-09)

1. **V1:** brightening terrain and grass in the trial is allowed, and so is the full option C retune if the trial points there. (No reason given for stopping at Haze brightness 0.52 and Aerosol 0.)
2. **V2 towers:** the user had planned to remove the scale-anchor towers. They may stay if a short brainstorm gives them a solid in-world identity. "Datum masts" is not accepted yet; hold V2 work order 4 until the brainstorm.
   **V2 Wall Engine bays:** no. Wait for the real meshes (climate spec C5). Drop V2 work order 3.
3. **V3:** yes to raking dawn and dusk light with a hard wall-shadow edge, behind a debug switch, signed off from before/after captures.
4. **V4:** yes to reduced shafts (large lanes and the dusk tide) if the air stays pure Rayleigh.
5. **V6:** yes, the eclipse darkens the far arch as a moving edge.
6. **4b:** pick the macro shadow strength from captures at 0.34, 0.6 and 0.8.
7. **Slow motion:** yes, speed up the cloud drift, accepting the change to storm timing.
8. **V7b chain lines:** decide them in the tower-identity brainstorm (answer 2).
9. **Housekeeping:** B3 and B4 were closed before height shadows became the default. Recorded in `docs/shadow-revamp-plan.md`.
