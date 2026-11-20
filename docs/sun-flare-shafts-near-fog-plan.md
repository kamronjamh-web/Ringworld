# Sun lens flare, light shafts and near ground fog: feature arc plan

Status: plan, 2026-11-18. The NF-0 and LF-0 spikes are done: both are GO, with work-order changes (see "Spike results" at the end). LF-1 (glare) is implemented ("LF-1 result"). The NF-1 design and the refined NF-2..NF-7 orders are in "NF-1 design"; they override sections C.2 to C.8. Costs marked **est.** are estimates. Every phase begins with a measurement step.

Covers three features:
- (a) sun lens flare and glare;
- (b) god rays and light shafts;
- (c) a new close-range "character fog": local media near the player, built on the physical haze as a separate layer.

Read with:
- `docs/atmosphere-haze-revamp-plan.md` (the RingHaze contract, MIST-1/2, the P6 and P9 deferrals);
- `docs/vastness-plan.md` section 3 (V4 path sun visibility, which already designs the large-scale shafts) and section 4 (V1 air, V7a mist lid);
- `docs/reflections-plan.md` 10.5 and `docs/work-orders/reflections/R3a-design.md` (the published cloud images);
- `docs/forest-density-study.md` (why near fog and shafts matter for forests).

## 0. Summary

| Feature | Physical basis | Recommended technique | Est. 4070 cost | Depends on |
|---|---|---|---|---|
| (a1) Sun glare | Veiling glare and the eye's or lens's point-spread function around a very bright source. Always present. | A sun-anchored glare sprite in HDR before the tonemap, scaled by a measured "visible sun fraction" | 0.02 to 0.06 ms | nothing |
| (a2) Lens flare: ghosts and starburst | Camera artefact: inter-reflections between lens elements, and diffraction at the aperture blades | The same pass: 5 to 8 aperture-shaped ghosts on the sun-to-centre axis and a precomputed starburst texture. **Optional, default off or faint (user).** | +0.02 to 0.05 ms | (a1) |
| (b1) Large shafts: cloud lanes, rim-wall tide, mountain shadows in the air | Shadowed columns of air seen through haze | **V4 as designed** (path visibility volume in the RingHaze `vis` slot). No duplicate here. | V4: 0.5 ms gate on the A1000 | V1 (the air must hold aerosol or mist, or shafts stay invisible) |
| (b2) Near shafts through canopy, terrain gaps and POIs (0 to about 250 m) | The same, but needs a dense near medium and the sun shadow map | **Falls out of (c)**: Godot volumetric fog froxels already sample the directional shadow atlas | included in (c) | (c) |
| (c) Near ground fog ("character fog") | Radiation fog, valley pooling, river and lake steam, mist after rain. World-anchored, terrain-relative, weather-driven. | Godot volumetric fog with a world-shape `FogVolume` fog shader. Density from height above the terrain (height-shadow lattice), a basin field, noise and the climate drivers. A small "fog end" pass lets the far backdrop and Sunshine composites compose with it. | 0.4 to 0.8 ms while active, 0 when inactive | a spike (NF-0) to confirm Godot volumetric fog in the ring frame |

**Order.**
1. NF-0 spike (it decides (c) and (b2)) and LF-0/LF-1 (glare) can start at once and in parallel.
2. Then NF-1 to NF-7.
3. LF-2 (ghosts) only on the user's yes.
4. V4 stays in the vastness plan, after V1.

Three facts shape everything:
1. **Today's air cannot show shafts.** The user's haze is pure Rayleigh at half density (vastness 1.1 item 3). A 2 km beam has an optical depth of 0.014, about 2 % contrast. Near forest shafts need a near medium of about 1e-3 to 1e-2 per metre, which is mist or fog. That is why (b2) belongs with (c).
2. **The last mist layer was removed for being anchored at sea level** (MIST-1, MIST-2, 2026-11-16: terrain sits at 700 to 1,500 m, so the mist was underground). Anything new must be terrain-relative, with a regression test for it.
3. **The sun shadow map is reachable only from Godot's own passes.** No compositor effect can read the directional shadow atlas; RenderSceneBuffersRD does not expose it (to confirm in NF-0). Tree-shadowed shafts are therefore cheapest through Godot volumetric fog, which samples that atlas for free. The cost is compositing it with our three compute composites.

## 1. What exists (facts used by the plan)

- **Haze contract.**
  - `struct RingHaze { vec3 T; vec3 S_sun; vec3 S_amb; }`; a consumer computes L = L_surf x T + S_sun x vis + S_amb.
  - The consumers are forward materials (`ALBEDO *= T`, `EMISSION += S`), per-instance vegetation haze in the GPU culling compute, Sunshine, the sky and the far viewport.
  - There is no froxel volume. The haze plan's P8 rejected one for the far haze (900 km ranges, forward consumers, a second viewport); those reasons do not apply to a 250 m near layer.
- **Sun sources.**
  - `ringworld_haze_sun_irradiance` is the camera-dimmed sun (P9). The undimmed `_remote` copy goes to the far arch.
  - `CameraDirectSunTransmittance` is the cloud shadow at the camera (Sunshine bridge).
  - Storms add grey Mie and cap the air sun visibility at 0.15 (P9).
- **Sky.**
  - `ring_local_sky.gdshader` draws the sun disc at `sun_disk_intensity` 2.55 and radius 0.265 degrees, plus a pow-256 halo at 0.20.
  - `sun_storm_hide` removes both under a closed storm.
  - The giant (gas giant) masks the photosphere geometrically during an eclipse.
  - The hub "false sun" has its own halo.
  - **The sun is not at physical HDR radiance.** The tonemap is AgX, with auto-exposure tied to storms. Glow is off in the game scenes.
- **Compositor order (main view).**
  - `RingContactShadows` (pre-opaque);
  - `RingFarDepthCapture` (post-opaque): the far viewport's depth in metres, as a Texture2DRD;
  - `RingFarBackdropComposite` (**post-sky**: it writes the far ring into pixels where main drew nothing);
  - post-transparent: Sunshine clouds, lightning channels, rainbow, rain streaks, containment field, shock distortion, and `RingRainHazeComposite`, which `EnsureRainHazeCompositorLast` keeps **last**.
  - The root viewport stays LDR 2D; 83 tools read root screenshots.
- **Published cloud data (R3a-1, committed; R3a-2 done).**
  - Two Texture2DRD globals: the accumulated cloud colour (straight, hazed) and the data image (`.b` = transmittance-weighted cloud distance), plus a direction matrix and `valid`.
  - Main view only, one frame late, held during capture hold.
  - Readers take coverage-weighted taps and a harmonic distance (`ring_cloud_reflection.gdshaderinc`).
  - This is the cloud occlusion source for the flare.
- **Shadow fields readable in any shader through globals.**
  - Height-shadow lattice L0 (4 m texels, about ±2 km) and L1 (16 m, about ±8 km): terrain heights and shadow-line heights (`ringworld_height_shadow_*`). TR-10 will add a canopy term.
  - Sunshine near and far cloud-shadow cascades.
  - The wet map `ring_wet_map`: film, soak, standing water, rain now.
- **Godot fog in use.**
  - Environment exponential fog serves caves and underwater (`_apply_underground_fog_coupling`).
  - P6 attenuates Sunshine clouds by that fog through `ring_enclosed_fog` in GenericData (308 floats). That is plumbing a near fog can extend.
  - Godot volumetric fog is forced off (density 0) by `RingVolumetricCloudLayer` unless the legacy external local-cloud renderer owns it (`RingLocalVolumetricCloudRenderer.ConfigureEnvironment`).
- **Climate drivers available per position.**
  - `TerrainSample.Moisture` and `.Temperature`;
  - local solar day and sun elevation (celestial);
  - shared wind;
  - the wet map;
  - after T2, the drainage graph (river and lake proximity, basin floors; plan item T9h valley mist).
- **Camera-artefact precedent.** "Lens droplets" (Wet world tab, `RingLensDroplets`, strength 0 = off) already treats the view as a camera.
- **The demo's methods** (`docs/forest-density-study.md` 1.2):
  - screen-space radial shafts (40 taps, warm tint, added without regard to air);
  - Godot volumetric fog only 6.23 m long with sun volumetric energy 6.2;
  - fog density driven by a forest mask.
  
  The first is stylistic. The second shows that Godot froxels give tree-shadowed streaks cheaply.

## A. Sun lens flare and glare

### A.1 Physical basis

- **Veiling glare and the point-spread function.** Light scattered in the optics (eye: cornea, lens and vitreous; camera: glass, coatings, barrel) spreads a fraction of a bright source over the image. Its falloff is roughly 1/θ² to 1/θ³. Every viewer has it, eye or camera, so it is the realistic part. It must scale with how much of the sun is actually visible.
- **Ghosts.** Reflections between lens surfaces form aperture-shaped images on the line from the source through the image centre, at 1e-3 to 1e-5 of the source each, tinted by the coatings. The eye does not make them. They are a camera artefact.
- **Starburst.** Diffraction at the aperture edges makes spikes (2N spikes for N blades). The eye has a weaker equivalent (the ciliary corona).
- **Verdict.** Glare is physical and should be subtle and on. Ghosts and starburst say "camera" and should be optional. The project already shows lens droplets, so a camera reading is precedented. The user decides the default (question A1).

### A.2 Technique

- **Sun-anchored, not threshold-based.** The displayed sun is not physical HDR (disc 2.55), and lightning, emitters and the containment field are bright too. Thresholding the image (the demo, or Godot glow) would glare whatever happens to be bright, and its strength would depend on art values. The pass therefore knows the sun's direction and draws relative to the sun's displayed luminance.
- **Visible sun fraction F.** A compute pass (8x8 threads, one group) takes 32 to 64 taps over the sun disc and a small rim. A tap counts as visible only if all of these hold:
  1. main depth is at the far plane (reversed-Z 0);
  2. the far viewport has nothing there (`RingFarDepthCapture`), so the far arch and the rim walls occlude;
  3. cloud coverage from the R3a data image (coverage-weighted) is low;
  4. it is outside the giant's disc (analytic, as in the sky shader);
  5. it is not under the storm hide (`ring_storm_shadow_fade` path, as in the sky).

  F also takes the sun colour after haze transmittance at the camera (`ringworld_haze_sun_irradiance` direction), so a low sun's flare is warm through the haze model, not through a tint. F is written to a 1-texel buffer and read by the draw. No CPU readback, and no lag beyond R3a's one frame.
- **Draw.** One post-transparent compute, ordered **after** `RingRainHazeComposite` (the rain is in the scene and the lens sees it; `EnsureRainHazeCompositorLast` needs a "lens effects last" rule). It works in HDR before the tonemap.
  - Glare: radial PSF sprite at the sun, ∝ F x sun luminance x strength, with a soft fade as the sun leaves the frame (real veiling persists slightly off-frame).
  - Optional ghosts: a small fixed table (position along the axis, scale, tint, intensity), drawn as analytic hexagon or disc SDFs in the same compute, restricted to tiles near each ghost.
  - Optional starburst: a precomputed texture (the FFT of a 6-blade aperture), rotated with the camera roll.
- **Rejected.**
  - Godot glow as the glare: full-screen, threshold-based, about 0.3 to 0.6 ms, and it changes every bright pixel.
  - A ray-traced physical lens model (Hullin et al.): realism the user did not ask for, at high cost.
  - Occlusion queries on the CPU: they lag, and they need the far viewport.

### A.3 Interactions

| Case | Behaviour |
|---|---|
| Clouds | F follows the R3a coverage at the sun's pixels. Under capture hold the content holds too. With the sun off-screen there is no flare, and the glare edge fade uses the camera-column `CameraDirectSunTransmittance`. |
| Haze | Sun colour and dimming from the haze transmittance at the camera. Once V1 adds aerosol, the Mie glow in the sky does the atmospheric part; the glare must not double it, so its strength is tied to the disc's visible luminance, not the sky's. |
| Storms | `sun_storm_hide` zeroes F under a closed overcast. Lightning gets no flare in the first cut (question A3). |
| Night | No sun, no flare. The planet light is a 30-degree extended disc, not a point source: no flare by rule. |
| Eclipse | The giant covers part of the disc, so F falls with the crescent's area (physical). The hub false sun flares by the same rule if its disc is compact and bright. No per-source toggle; the rule is "any compact celestial light" (question A3). |
| Ring arch, rim walls, terrain, trees | Through main depth and the far depth capture. Leaves flicker the sun through gaps, so F flickers physically; there is no smoothing beyond one frame, and the `flicker` gate checks only for pops that do not follow F. |
| Underwater, caves | Off when the underwater factor or the cave fog weight is above 0 (depth already blocks caves). |
| Lens droplets | Independent. Later: droplets could pick up glare (realistic). Not in scope. |

### A.4 Cost

- est. 0.005 ms for the visibility pass;
- est. 0.02 to 0.06 ms for the glare;
- est. +0.02 to 0.05 ms for ghosts and starburst.

Gate: **0.1 ms at R (4070, 1080p, High)** with the sun on-screen, and **0.0 ms with strength 0** (the pass is skipped and the frame is byte-identical).

### A.5 Grave-key menu (Atmosphere tab, "Sun" group)

- **Sun glare** (0 to 1). The strength of the veiling glare; a genuine tunable for taste and comfort. Default subtle (question A1).
- **Lens flare** (0 to 1). Ghosts and starburst; 0 = off.

Ghost count, colours and the aperture shape are fixed design data, not menu rows. Debug views (F as a number, a tap overlay) go in debug tooling. Persistence is in `AtmosphereVisualSettings`, following the live-tuning-setting skill.

### A.6 Work orders

| WO | Role | Content | Tests |
|---|---|---|---|
| LF-0 | lead | Design: the visibility taps and sources, the giant overlap formula, compositor order ("lens last" next to rain-last), and calibration against the displayed sun luminance and exposure | none |
| LF-1 | implementer | Visibility pass and glare, the two menu rows (lens flare inert until LF-2), settings. **After LF-0:** GPU taps on depth, far depth and the live Sunshine accumulation alpha; eclipse coverage and storm hide as CPU scalars; abs(`y.y`) for pixel angles. | `sun_visibility_gpu_test`: F = 1 in clear sky, 0 behind a ridge, 0 behind the far arch, 0 inside a cloud, crescent area within 3 % during an eclipse, 0 under a storm. `lens_settings_smoke_test`. Strength 0 is a byte-identical frame (`compare`). |
| LF-2 | implementer (on the user's yes) | Ghosts and starburst | Capture sheet across the sun's path; `flicker` on a pan past a tree line (no spikes that F does not explain); cost A-B-A |
| LF-3 | implementer | Review pack: stills at noon, sunset, eclipse crescent, hub sun, broken cumulus; a short mp4 for the user | user sign-off |

### A.7 Questions for the user

- A1. Ghosts and starburst are a camera look (the eye makes glare but no ghosts). Do you want them at all? If so, should the default be off, faint or clearly visible? And should glare be on by default at a subtle strength?
- A2. The game's sun is not drawn at physical brightness. Should the glare be calibrated to look like a real camera with auto-exposure (strong around the sun at noon), or kept restrained?
- A3. Should the hub false sun, and later lightning, flare by the same rule as the sun?

## B. God rays and light shafts

### B.1 Physical basis

- Crepuscular rays are lit and shadowed columns of air side by side. Their contrast is about σ_s x path length x phase(θ) x (lit minus shadowed).
- They need a scatterer:
  - Rayleigh air at sea level (σ about 1.2e-5 per metre) shows lanes only at tens of km;
  - aerosol and mist (1e-4 to 4e-3 per metre) give the classic sunbeams through cloud gaps;
  - forest shafts need about 1e-3 per metre or more (mist, smoke, dust, humid morning air) within the first 100 m.
- Forward scattering makes them strongest looking toward the sun (droplet Mie g about 0.8 to 0.9). Anticrepuscular rays converge on the antisolar point.

### B.2 Two scales, two systems

- **(b1) Large scale: V4, unchanged.**
  - The vastness plan already designs the path sun visibility volume (160 x 90 x 32 slices, 100 m to 210 km). It is fed by:
    - clouds (LS-1);
    - the rim wall (LS-2);
    - near terrain through the height-shadow lattice (LS-3);
    - far terrain (LS-4, contingent).
  - It plugs into `vis` for every haze consumer.
  - This plan adds nothing to it except a cross-reference: V4 should start below 100 m only if (c) does not exist. With (c), the near 0 to 250 m is (c)'s job and V4 handles the haze beyond.
  - V4's visibility depends on V1. The user has to choose the air first (question B1).
- **(b2) Near scale: through canopy, POIs and terrain notches, 0 to about 250 m.**
  - It needs the sun shadow map (trees and POIs are in it; the height lattice is 4 m and has no trees until TR-10's canopy term, which is still 4 m and too coarse for leaf gaps).
  - **Godot volumetric fog is the only path that reads the shadow atlas.** The froxels evaluate the DirectionalLight with its shadows and `light_volumetric_fog_energy`.
  - So near shafts are the lighting of the near medium of (c). They appear exactly when there is fog or mist, and the physical rule says that is the only time they should be bright.
  - A faint always-on near aerosol (summer forest dust and pollen, σ about 5e-5 to 2e-4 per metre) would give barely visible shafts in clear weather. That is question B2.
- **Rejected.**
  - Screen-space radial blur (the demo; GPU Gems 3). It adds light with no medium, works only with the sun on screen, cannot show shadowed lanes looking away from the sun, and would break the object = cloud = sky agreement. V4 rejects it for the same reasons.
  - A custom near froxel volume with its own shadow render of trees. A second tree shadow pass would cost about as much as the sun-map tree shadows (1.2 to 2.5 ms, TR-1/TR-8).
  - Epipolar sampling on the depth buffer. Occluders off screen are missing, and it only works sunward.

### B.3 Interactions (b2; b1 is V4's)

- **Clouds.** Godot froxels do not see our cloud shadows: they are shader-composed, not in the shadow atlas. The fog shader multiplies the sun part by the cloud-shadow visibility at `WORLD_POSITION` from the same Sunshine near-cascade global the ground uses. Since `ALBEDO` scales both sun and ambient in-scatter, NF-0 must confirm one of two options:
  - (i) set `volumetric_fog_ambient_inject` to 0 and inject the ambient as `EMISSION` from the haze ambient (`RingHazeSkyIrradiance`), so `ALBEDO` scales only the sun;
  - (ii) scale the light's volumetric energy by the camera-column cloud visibility, which is wrong only at cloud-shadow edges within 250 m.

  Option (i) is preferred.
- **Terrain beyond the shadow map.** The same fog shader multiplies by the height-shadow lattice visibility at the froxel. A mountain then shades a valley's fog beyond the 400 m near map.
- **Haze.** The sun's colour comes from the DirectionalLight. NF-0 checks that `day_side_atmosphere.gd` sets it from the haze sun transmittance; otherwise the fog shader's albedo carries the tint. Once V4 exists, haze in-scatter beyond the fog uses V4's `vis`. The two never overlap, because V4's slices start at the fog's end (B.2).
- **Night.** The planet light is a DirectionalLight. Planet-lit fog and shafts are physical: dim, blue and soft. The hub false sun lights fog from the zenith.
- **Storms.** The storm light fades the sun, so shafts fade. Rain haze composites after.
- **Eclipse.** The sun's energy falls with the crescent, and the fog follows automatically.
- **Fast travel.** Temporal reprojection smears at speed. Fade the volumetric fog out above about 60 m/s ground speed, or when the camera is more than about 2 layer heights above the local fog top (NF-2).

### B.4 Grave-key menu

None new for (b).
- V4 keeps the existing "Cloud shadowing of haze" slider (the vastness plan's rule).
- Near shafts follow the fog of (c) and the sun; the light's volumetric energy stays 1 (physical) and is not a tunable.
- Froxel resolution and length belong to the quality presets.

### B.5 Work orders

None of its own for (b2): it is delivered by NF-2 and NF-5 and reviewed in NF-7. V4's orders stay in the vastness plan.

### B.6 Questions for the user

- B1. V1 first: should the clear-weather air keep pure Rayleigh (no sunbeams through cloud gaps beyond rare big lanes), or gain an aerosol or mist load so V4's shafts can show?
- B2. Near shafts in clear weather: none (physically clean summer air), or a faint near aerosol so forests show weak shafts on bright days?
- B3. Which first: near forest shafts with fog (this plan, NF), or the large cloud-lane shafts (V4)?

## C. Near ground fog ("character fog")

### C.1 Physical basis

| Kind | When and where | Extinction σ (per m) | Thickness |
|---|---|---|---|
| Radiation fog | Forms over clear, calm nights. Peaks around sunrise and burns off 1 to 3 h after. Pools in valleys and basins (cold-air drainage). | 4e-3 to 4e-2 (visibility 100 m to 1 km) | 10 to 100 m, with a flat top under an inversion |
| Mist | Humid air, after rain, under canopy, at dawn | 8e-4 to 4e-3 (1 to 5 km) | tens of m |
| Steam (evaporation) fog | Over rivers and lakes when the air is much colder than the water (dawn, autumn) | 4e-3 to 2e-2 | 1 to 10 m, in wisps |
| Advection fog | Coasts, moist air over cold water | as radiation fog | 50 to 300 m |

- Droplets have a single-scattering albedo of about 0.99 and a strongly forward phase (Mie g about 0.8 to 0.9).
- Wind above about 3 to 5 m/s mixes radiation fog away or lifts it into stratus.
- "Character fog" is read here as fog the player meets close up. **The medium is world-anchored**: it does not slide with the camera, and fog seen from 2 km away is the same fog. Only its *rendering window* follows the camera.

### C.2 Density model (world space, terrain-relative)

```
rho(x) = sigma0 * D(climate, time, wind, wetness, water)        // weather state, CPU, a few uniforms
             * L((h_ground(x) + b * h_basin(x)) / H_layer)       // logistic lid: flat below, smooth top
             * (1 + c * N(x - wind * t))                         // world-space 3D noise, advected
```

- **h_ground**: height above the local terrain, from height-shadow L0 (4 m texels, ±2 km).
- **h_basin**: height above a low-passed basin floor. It is the minimum over about 200 m to 1 km from the L0 and L1 max/min pyramid, or the T2 drainage basin datum once rivers exist. Fog then fills hollows and valleys instead of hugging slopes.
- **Never sea-level-anchored.** There is a regression test for this (C.8).
- **The drivers in D** are evaluated on the CPU at about 2 Hz, near the camera, and smoothed:
  - local solar time (formation from about 2 h before dawn, peak at sunrise, burn-off by sun elevation of about 15 to 25 degrees);
  - humidity, from `TerrainSample.Moisture` raised by the wet map's soak and standing water after rain;
  - temperature (cool favours fog);
  - wind speed (a cut-off);
  - water proximity (wet map standing water now; the drainage graph after T2), for steam fog.

  Strength per kind follows C.1.
- **Determinism.** D is weather state, like the wet world, not world generation. It reads the generation-dependent climate fields, but it adds no ledger feature and needs no generation step. The user should still confirm it may apply to all worlds (question C3 covers the scope).

### C.3 Technique

**Recommended (C-A): Godot volumetric fog plus a world-shape `FogVolume`** with a `shader_type fog` shader evaluating `rho` per froxel.

- **Settings.**
  - Fog length 150 to 250 m with exponential slice spacing (`volumetric_fog_detail_spread`);
  - temporal reprojection on;
  - GI inject 0, global density 0 (all density comes from the FogVolume);
  - anisotropy 0.6 to 0.75 (Godot uses one Henyey-Greenstein lobe; droplets want about 0.85, but froxel aliasing worsens at high g);
  - `volumetric_fog_sky_affect` 1.
- **Lighting.**
  - The sun, planet light and hub sun come in through their DirectionalLights, with shadows (trees and POIs: the near shafts of b2).
  - Cloud and terrain visibility multiply the sun part (B.3).
  - Ambient is injected from the haze ambient (B.3 option i). The fog therefore takes its colour from the same E_sun and S_amb as the haze.
- **Composition with the haze.** Godot applies volumetric fog to every forward material after its own shading: L = S_fog + T_fog x (L_surf x T_haze + S_haze). That is the correct order, because the near fog lies in front of the haze path. The haze's own 0 to 250 m is double-counted, but at 1e-5 per metre against 1e-3 or more that is negligible. Forward consumers are correct for free, including:
  - terrain;
  - trees and grass (their per-instance haze stays ours; Godot fog is per fragment on top);
  - POIs;
  - ocean and water;
  - the sky (through sky affect; NF-0 confirms that our custom sky shader takes it).
  
  Materials with `fog_disabled` (emitter beams, containment field, megastorm and hub beam layers) skip it: beams would cut through the fog. That is acceptable for a first cut and listed as a follow-up.
- **The three composite gaps, and the fix.**
  - `RingFarBackdropComposite` (post-sky) overwrites the fogged sky pixels with the far ring.
  - Sunshine composites the clouds post-transparent.
  - Rain haze runs last and is in the right order already.
  
  Fix: a tiny compute pass, **fog end**, at 1/8 resolution. It integrates the same `rho` analytically or in about 16 steps along each pixel's ray to the fog length, with the noise term faded to 0 over the last 40 % of the window, so it matches the froxels' last slice. It also uses the same lighting minus tree shadows. It publishes `T_end` and `S_end` as a Texture2DRD global, R3a-style.
  - The far backdrop applies `c' = c x T_end + S_end`.
  - Sunshine applies the same through GenericData. The P6 `ring_enclosed_fog` slot shows the path; this is an ABI change with the known reimport gotcha.
  
  A seam test across the near-terrain to far-backdrop boundary (C.8) guards the match.
- **Ownership.** `RingVolumetricCloudLayer` writes `VolumetricFogEnabled = false` and density 0 unless the legacy local-cloud renderer owns the fog. NF-2 adds one owner (the near fog). The legacy path and the near fog must not both run; the legacy renderer is not the production path.
- **When inactive.** If D x sigma0 is about 0 near the camera (fog strength under 1e-4 per metre within the window), volumetric fog is disabled after a fade and the frame is byte-identical to the feature being absent.

**Fallback (C-B), if NF-0 fails:** a custom near-media froxel volume (for example 160 x 90 x 48 over 0 to 250 m, RGBA16F in-scatter and transmittance), filled by our compute and sampled inside `ring_haze_sample`.
- It is consistent by construction with every consumer, the same argument V4 makes.
- It costs a Sunshine binding.
- It has **no tree shadows** (only TR-10's 4 m canopy term), so forest shafts become coarse canopy-gap shafts.

**Rejected:**
- Pulling the physical haze closer (the user excluded it, and it would break the haze calibration and the 800 km sky convergence).
- Godot height fog on the Environment (height is world Y, which is wrong on a curved, rebased ring).
- A camera-attached fog sphere (it slides with the player).
- Particle or billboard fog cards (they sort badly with the forest, do not shadow and cost overdraw).

### C.4 Interactions

| Case | Behaviour |
|---|---|
| Haze | Composes as C.3. Fog lighting uses the haze E_sun (after its transmittance, so dawn fog is warm) and S_amb. V4, later, applies beyond the fog end only. |
| Clouds | Cloud shadows darken fog per froxel. Clouds behind the fog are attenuated through fog end. Clouds inside the fog layer (low stratus) are not this feature. |
| Storms and rain | Rain streaks and rain haze composite after. During heavy rain D falls (rain washes out radiation fog). After rain, soak raises D: post-rain mist, the physical replacement for the removed post-rain mist. |
| Night | Planet-lit fog: dim and blue-grey. No sun shafts. Radiation fog builds overnight, so the player meets it at night and at dawn. |
| Eclipse | Fog darkens with the sun. The hub sun lights it from above. |
| Wet world | The soak and standing channels feed humidity. Standing water feeds steam fog at dawn. |
| Water | Steam fog over rivers and lakes when air is colder than water; advection fog on coasts. Ocean surfaces get Godot fog like any forward material. |
| Forests | Shafts through the canopy (b2). Under a canopy the ambient part is lower once the forest study's canopy sky occlusion exists; the fog shader can read the same canopy cover. |
| Caves and underwater | Off (the enclosure blend and underwater factor fade D to 0). Environment exponential fog stays the enclosed fog. |
| POIs | Fog applies automatically through forward materials (AGENTS.md: no per-subsystem toggle). |
| Altitude and flight | Above the layer top the near window empties and the fog fades out. Seen from a ridge or the air, valley fog needs a far form (C.6). |

### C.5 Cost

- est. 0.4 to 0.8 ms on the 4070 at 1080p (default froxel grid about 114 x 64 x 64; one fog shader with about 4 texture taps; directional shadows);
- est. 0.02 ms for fog end;
- 0 when inactive.

Gate: **0.6 ms at R, High, `forest_interior_eye`, fog forced on**; at most 0.05 ms when inactive (CPU drivers only). NF-0 measures the engine cost first. Forest frames are already over the 11.1 ms target, so the presets decide the reach: Low off, Medium with a short window and coarse froxels (question C3).

### C.6 Far form (contingent, NF-8)

- Fog beyond the window (a fog-filled valley seen from a ridge or from the air) is the same medium seen from afar.
- The first cut extends the fog-end pass to about 2 to 5 km with the slab model, so valleys read white from a ridge.
- The full answer is a terrain-relative mist term in the analytic haze: V7a's logistic lid plus the "basin floor" field that V7a and T9h already call for, at the same sigma0 and D.
- This waits on V1 and V7a and on the user (question C2).

### C.7 Grave-key menu (Atmosphere tab, new "Ground fog" group)

- **Ground fog amount** (0 to 2). Multiplies the physically driven density; 1 = as the weather produces it, 0 = off. The genuine tunable.
- **Fog layer height** (10 to 150 m). The radiation-fog lid height H_layer; a genuine tunable, because inversion depth varies.

Not menu rows:
- per-kind toggles (radiation, steam, post-rain): they follow the weather;
- noise contrast, anisotropy and froxel settings: design data or presets;
- "force fog on" for testing: debug tooling.

Persistence: `AtmosphereVisualSettings`, through the live-tuning-setting skill.

### C.8 Work orders

| WO | Role | Content | Tests |
|---|---|---|---|
| NF-0 | lead (one GPU spike run) | Prove or reject C-A. Godot volumetric fog in the rebased ring frame (reversed-Z, TAA/FSR jitter); a world FogVolume shader reading `ringworld_height_shadow_l0` (`sampler2DArray` global) and `ring_wet_map`; `EMISSION` semantics for ambient injection; `sky_affect` with `ring_local_sky.gdshader`; shadows from the 2-cascade near map in froxels; whether any fog texture is reachable from a CompositorEffect; cost at 1080p on the 4070 at default and half froxel grids. Pick C-A or C-B. | a scratch scene under `.godot/`, deleted after; numbers in this file |
| NF-1 | lead | Design: `rho`, the drivers, the basin field, the owner arbitration, the fog-end pass and its GenericData ABI, presets. **After NF-0:** add the T0 terrain-height texture and a basin min pyramid, the haze sun transmittance in `ALBEDO`, the ambient units, and the gap fix choice (fog end over the whole sky, or the far backdrop inside the sky shader: a decision for the primary agent). | none |
| NF-2 | implementer | Owner, environment config, FogVolume and fog shader with `rho` (drivers as uniforms; strength default 0 until NF-6), the fast-travel and altitude fade. **After NF-0:** publish the T0 height as R32F textures from the height-shadow producer; stop the legacy `VolumetricFogEnabled` writers (`RingVolumetricCloudLayer`, `RingLocalVolumetricCloudRenderer`) when the owner is registered. | `near_fog_identity_gpu_test`: amount 0 or inactive gives a byte-identical frame against the feature absent |
| NF-3 | implementer | CPU drivers (time, humidity, wetness, temperature, wind, water proximity) at 2 Hz, smoothed | `near_fog_drivers_test` (headless): monotone in humidity and soak, 0 above the wind cut-off, burn-off with sun elevation, steam only near water. **Terrain-relative regression:** fog density at 1.5 m above ground equals that at sea level on a 1,000 m plateau (the MIST-1 bug). |
| NF-4 | implementer | Fog end; far-backdrop and Sunshine composites (reimport). **After NF-0:** the scope is every pixel the far backdrop writes, which is the whole sky, not only the far ring. | `near_fog_composition_gpu_test`: a strip across the near-terrain to far-backdrop boundary and against a cloud agrees within 2 % luminance; `rain_cloud_compositing_gpu_test` and `haze_consistency_gpu_test` unchanged |
| NF-5 | implementer | Cloud and terrain visibility in the fog shader; ambient through `EMISSION` (or the NF-0 alternative) | `near_fog_shadow_gpu_test`: fog under a cloud-shadow edge and behind a ridge darkens; tree shafts present at `forest_edge_eye` with fog forced on |
| NF-6 | implementer | Menu rows, presets, persistence | `near_fog_settings_smoke_test`; `atmosphere_menu_smoke_test` extended |
| NF-7 | implementer, lead reviews | Review pack (Movie Maker plus `tools/media/ffmpeg_tools.ps1`): a dawn valley, forest shafts after rain, river steam at dawn, planet-lit night fog, a walk and a fast turn (`flicker`: no reprojection pops), cost A-B-A at the four standard poses | user sign-off |
| NF-8 | contingent | Far form (C.6) | its own design |

Register every new test in `tools/test_manifest.json`. Run with `tools/run_tests.ps1 -Files <changed files> -Label nf-<n>` (`-DryRun` first).

### C.9 Questions for the user

- C1. Should fog be purely weather-driven, so the player meets it at dawn, in valleys, after rain and over cold water, and most clear days have none? Or should some near mist always be present?
- C2. Near only for the first cut, or must valley fog also be visible from ridges and the air from the start (NF-8, which leans on V7a)?
- C3. Is about 0.4 to 0.8 ms acceptable while fog is active, given forests are already over the 90 fps target? Which presets should have it (suggested: off on Low, short and coarse on Medium)? And may it apply to all worlds (it is weather, not generation)?

## D. Overall sequence

1. **In parallel:**
   - NF-0 spike (one GPU run, lead);
   - LF-0 design.
2. **Then:**
   - LF-1 (glare; small);
   - NF-1 design.
3. **NF-2 to NF-6** (about five implementer orders), with b2 coming with NF-2 and NF-5.
4. **LF-2** (ghosts) on the user's yes; **NF-7** review; **NF-8** contingent.
5. **V4** stays in the vastness plan, after V1; B.2 records the hand-off distance.

Total new GPU while active: est. 0.5 to 0.9 ms (fog plus flare); 0 to 0.1 ms in clear weather. Every phase gates on its own A-B-A at R, reports before raising any preset, and asks before any visual floor or boost.

## User answers (2026-11-18)
- (a) Lens flare: physical sun glare on by default. Ghosts and starburst are a menu option, default off.
- (b) Shafts: air stays clean (pure Rayleigh). Shafts appear only with near fog and humid conditions; no clear-weather aerosol.
- (c) Character fog: driven by weather AND location, and terrain-aware: it pools in valleys and hollows and follows the ground, near water, and so on. Cost of 0.4-0.8 ms while active is accepted on High and above. Medium and Low get a reduced version or none.
- Start NF-0 and LF-0 now.
- Open questions not asked yet (defaults: the recommendations in each section): flare calibration, whether the hub false sun and lightning flare, ordering of V4 against near shafts, and near-only against visible from ridges.
- **Later answers (2026-11-18, after LF-1 and NF-1):** glare ships with a fixed source gain of 30 (soft halo; `RingSunGlare.GlareSourceGain` default), not 1. Near fog runs on High and above only: **Medium gets no near fog** (overrides the NF-1 preset that gave Medium a reduced grid). Coastal advection fog stays out of the first version.

## Spike results (2026-11-18)

Both spikes ran as scratch GDScript (a SceneTree driver, a FogVolume fog shader built at run time and a GDScript CompositorEffect) under `.godot/`, deleted afterwards. No production file changed.

Setup for every number below:
- RTX 4070 desktop, 1920 x 1080, High preset (sun map 2 cascades, 400 m), FXAA, VSync off.
- Pinned v8 fixture world (seed 1337) through `tools/landscape_harness_common.gd`, user preference files, clear weather pinned, world clocks held.
- Stills in capture mode (time scale 0, cloud hold).

The costs are fixture-world numbers. NF-7 repeats them on the slot 3 world with `tools/perf_bench.ps1`.

Evidence is in `.godot/audit_baseline/common/near-fog/` and `.../lens-flare/`. The files named below are the cited ones.

### NF-0 result: GO for C-A, with four work-order changes

**What was built.**
- Environment volumetric fog: length 200 to 250 m, detail spread 2, temporal reprojection 0.9, GI inject 0, ambient inject 0, global density 0, anisotropy 0.7, sky affect 1.
- One `FogVolume` with `FOG_VOLUME_SHAPE_WORLD` and a `shader_type fog` shader evaluating `rho` from C.2:
  - h_ground: from a terrain-height texture (R32F, 2 m, 512²) baked on the CPU from `Terrain3DGameplay.SampleHeight` around the pose;
  - h_basin: from a basin-floor texture (16 m grid, 160 m min filter, then a 96 m box blur);
  - logistic lid; two-octave world-anchored value noise;
  - `ALBEDO` = sun tint x height-shadow visibility (`ring_height_shadow_sample`, included as-is); `EMISSION` = ambient.
- `WORLD_POSITION` is mapped through the published `ringworld_height_shadow_frame`, relative to the explorer, with the curvature term h = f.z - f.x²/2R.
  - Calibration: frame height minus canonical vertical = 0.003 m; a 100 m move along the ring = +99.998 m in frame x; ground check 1.700 m.
  - No precision or rebasing artefacts appeared in the rebased ring frame.

**Findings.**

| Question | Result |
|---|---|
| Can any CompositorEffect read the sun shadow atlas? | **No (confirmed).** `RenderSceneBuffersRD` exposes only named textures. A probe of 12 contexts x 20 names found `render_buffers/color`, `render_buffers/depth`, `forward_clustered/normal_roughness` and `rb_ssr/ssr`, with no fog or shadow texture. `RenderSceneData` has no shadow accessor. `RenderingServer` has only `directional_shadow_atlas_set_size`. |
| Do the froxels use the sun shadow map? | **Yes.** Tree-shadowed air shows as dark wedges, with lit columns through the gaps: `shafts_forest_edge_sun_12_rise_+30_mist.png`; the in-scatter-only heatmaps `shafts_sun12_+30_sun_inscatter_heatmap.png` and `forest_edge_+35_sun_inscatter_heatmap.png`. With the sun's volumetric energy at 0, the fog loses almost all its brightness (mean luma 0.43 to 0.29 against 0.29 with no fog), so in daylight the near medium is lit mostly by the sun through the shadow map. |
| Terrain-relative density in the ring frame | **Works.** The fog fills the basin, has a flat lid and leaves the slope clear. The left ridge's terrain shadow falls into it, blue in the shade and white in the sun: `valley_sun_12_rise_pooled_emission_haze_ambient.png` (sun 12°, rising). At sun 4° the whole basin is in terrain shadow: `valley_sun_4_rise_pooled_emission_haze_ambient.png`. |
| Clear day, zero fog: byte-identical to today? | **Yes, five of five checks** (forest edge, noon, compared byte for byte): repeat frame; feature installed but inactive (FogVolume and material present, volumetric fog off); **volumetric fog on at zero density**; after switching off; after detaching. `compare`: SSIM 1.000000, PSNR inf (`clear_day_identity_heatmap.png`). Volumetric fog at zero density still costs its engine passes (below), so the off switch stays. |
| Composition with the physical haze | **Correct order, nothing to add.** Godot fogs every forward material after its own shading (haze included). Fogged terrain, trees and grass show no seams between materials. |
| The far backdrop / Sunshine "fog end" gap | **Larger than planned.** Godot does fog our custom sky: with `RingFarBackdropComposite` disabled, the sky band goes from luma 0.23 to 0.57 with fog on. But the far backdrop composite overwrites **every pixel main left empty, the whole sky as well as the far ring**, with the far viewport's image. In the shipped order the sky band is byte-identical with fog on and off (`gap_hills_uniform_fog_nohud.png` against `gap_hills_uniform_fog_far_off.png`). Every skyline therefore shows a hard seam: fogged hills under a clear sky, and fogged trees glowing white against an unfogged blue sky when looking sunward (`shafts_forest_edge_sun_12_rise_+30_mist.png`, right half). Disabling Sunshine changed nothing in the test band at that pose, but its post-transparent clouds would still sit unfogged in front of any fogged sky. |
| `EMISSION` semantics (ambient injection, B.3 option i) | **Source radiance**, weighted by density inside Godot. Thick-terrain pixels (150 m to 0.9 x far) with `ALBEDO` 0 and `EMISSION` 0.3 give luma 0.630, 0.646 and 0.647 at σ 0.05, 0.1 and 0.2 (it converges and does not fall as 1/σ). `EMISSION` 0.6 gives 0.768; 0 gives 0.011. Option (i) works: `EMISSION` = albedo x in-scattered ambient radiance, independent of σ, while `ALBEDO` scales only the direct lights. |
| Sun colour and units | The `StarLight` colour is constant (1, 1, 1) at energy 4. **It does not carry the haze sun transmittance** (B.3 asked this). `ringworld_haze_sun_irradiance` = (12.57, 12.57, 14.58) = π x energy and is camera-dimmed only. The fog shader must compute the haze sun transmittance to the froxel itself and put it in `ALBEDO`. Since `ALBEDO` would also scale Godot's ambient inject, option (i) is required, not just preferred. |
| Ambient calibration | Open. Raw haze ambient irradiance as `EMISSION` (0.045 to 0.2) makes shaded fog glow saturated blue against near-black shaded terrain (valley stills at 4°). Godot's own ambient inject at dawn changed at most 5/255. The fog ambient must come from the same source and units as the surface ambient (NF-1). |
| Brightness toward the sun | σ 0.008 over 200 m at g 0.7 and energy 4 saturates to white sunward (`forest_forest_clearing_+35_fog.png`). That is physical for backlit mist, but it is a calibration item for NF-1 and NF-7. |
| Ownership arbitration | In production `RingVolumetricClouds.SunshineCloudsPrimaryRenderer = true`, so neither `ApplyPreset` nor `SetCloudsEnabled` touched `VolumetricFogEnabled` (0 resets while a per-frame watcher held it on through a whole run). The writers that remain: `RingVolumetricCloudLayer._Ready` and `SetCloudsEnabled` whenever Sunshine does not own the clouds (Sunshine disabled, failed or cloud quality Off); `RingLocalVolumetricCloudRenderer._Ready` (Enabled = false by default: it writes `false` and density 0 once at start) and its `ConfigureEnvironment` / `RestoreEnvironmentSettings`; `RingScaledFarViewport` writes only its own far Environment (no conflict). |
| Cost on the 4070 at High | Table below: **+0.20 ms** at the project grid of 96 x 96 x 80. That is a third of the 0.6 ms gate. |

Cost: `forest_interior_eye` at noon (sun shadows on, mist σ 0.008, 40 m lid). A-X-A, 2 rounds, 5 s windows after 1 s settle. Root viewport GPU time, median per window. Baseline A 8.25 to 8.33 ms; A-A spread 0.074 ms.

| Variant | ΔGPU p50 round 1 / round 2 (ms) | Δ wall p50 (ms) |
|---|---|---|
| FogVolume shader, grid 96 x 96 x 80 (project setting) | 0.197 / 0.207 | 0.22 |
| Engine only: global density, no FogVolume, 96 x 96 x 80 | 0.153 / 0.173 | 0.15 |
| FogVolume, 64 x 64 x 64 (Godot default) | 0.147 / 0.118 | 0.12 |
| FogVolume, 48 x 48 x 40 | 0.084 / 0.069 (near the floor) | 0.06 |
| FogVolume without the height-shadow taps, 96 x 96 x 80 | 0.188 / 0.193 | 0.22 |

The fog shader with height-shadow sampling adds only about 0.04 ms over the engine passes. The height-shadow taps themselves cost about 0.01 ms.

**Work-order changes from NF-0.**
1. **A terrain-height source is missing (NF-1 / NF-2).**
   - The height-shadow lattice textures hold the shadow-line height relative to T0 and the blocker distance (RG16F). They do not hold the terrain height.
   - T0 exists only as storage buffers (`RingTerrainHeightShadowGpu.HeightBuffer`), which no material can read.
   - NF-2 adds a published terrain-height texture: per level an R32F texture copied from the height buffer on each rebuild by a tiny compute (no CPU cost), exposed as new `ringworld_height_shadow_t0_*` globals next to the existing frame and bounds.
   - The basin field needs a **min** pyramid. The producer builds only max mips today. Basin floor = min over about 160 m, then blurred: L0 within 2 km, L1 out to 8 km.
2. **NF-4 scope is the whole sky, not only the far ring.**
   - Today's far backdrop composite replaces all main-empty pixels.
   - Two fixes are possible:
     - (a) the planned fog-end pass (T_end, S_end at 1/8 resolution) applied by the far backdrop composite to every pixel it writes, and by Sunshine;
     - (b) composite the far viewport image **inside** `ring_local_sky.gdshader` (screen UV, skipped in cubemap passes) so Godot's own sky fog covers it for free; Sunshine still needs (a) or the P6 density path.
   - (b) touches the reflections lane's sky shader and moves the backdrop pipeline. **Architecture decision for the primary agent** before NF-1 closes. The lead recommends (a) for the first cut, with (b) recorded as the cleaner end state.
3. **Lighting inputs (NF-1 / NF-5).**
   - `ALBEDO` = haze sun transmittance at the froxel (from the haze include) x cloud visibility x height-shadow visibility.
   - Ambient inject 0; `EMISSION` = albedo x ambient radiance, in surface-ambient units.
   - The light's colour must not be relied on for the tint.
4. **Ownership (NF-2).**
   - The near-fog owner is the only writer of `VolumetricFogEnabled` on the main Environment.
   - `RingVolumetricCloudLayer` (both writes) and `RingLocalVolumetricCloudRenderer` (`_Ready` with Enabled = false, and its restore path) stop writing it when an owner is registered; the legacy renderer and the near fog are mutually exclusive.
   - Off below threshold stays as planned (it is byte-identical, measured).
5. Smaller items:
   - The presets can be cheaper than C.5 feared: High 96 x 96 x 80 (+0.20 ms); Medium 48 x 48 x 40 (+0.08 ms) is affordable if the user wants a reduced version there (question C3 still open).
   - TAA/FSR2 was not exercised (FXAA default); NF-2's identity test adds a TAA on run.
   - Cloud shadows in the fog shader need the material registered with `RingCloudShadowProjector.Register` (C#, static), which the GDScript spike could not reach. Not tested; NF-5.

### LF-0 result: GO for LF-1, with simpler sources than A.2

**What was built.**
- A post-transparent compute appended last (order: far backdrop, Sunshine, rain streaks, rain haze, depth probe, LF-0).
- One 64-thread group per direction for 18 directions: the sun, the screen centre, and a 4 x 4 screen grid. Each group takes 48 Vogel-spiral disc taps inside 0.265° and 16 annulus taps at 1.9 disc radii.
- Per disc tap: main depth = 0 (reversed-Z far); Sunshine `far_opaque_depth_texture` = 0 (nothing in the far view); 1 - Sunshine accumulation colour alpha (cloud density in front of an infinitely far point, the same rule `ring_lightning_channel.glsl` uses).
- An image channel (the disc and annulus luminance of the resolved HDR colour) as an independent oracle.
- A prototype glare sprite (radial PSF x F x displayed disc radiance, in a 760 px square around the sun) for cost and look only.

| Source | Result |
|---|---|
| Clear sky, sun on screen | F = 1.000 (sun centred, and 14° off centre). Disc luma 2.72 against annulus 0.26; the displayed disc is 2.55 x `sun_color` (1, 1, 1). |
| Main depth | Forest interior at noon looking up at the sun, and the forest at sun 8°: depth_vis 0, F 0. The image agrees: disc 0.006 against annulus 0.18. Through a forest edge at sun 12° and 8°, F = 0.02 and 0.06; from the clearing, 0.88 and 0.93. Fractional occlusion through foliage works. |
| Far depth | Looking along the ring at pitch 10 to 25° (headings 0 and 180), the centre direction has main depth empty and far_vis 0: the arch and far terrain occlude. Sideways (heading 90, pitch 25), F = 1. Hills at sun 2°: main and far both occlude. |
| Cloud coverage | **The Sunshine accumulation images of the same frame** (`accumulation_textures[1].a`, screen UV), not R3a. R3a is a one-frame-late copy of the same images, and the glare pass runs post-transparent in the main view where the live ones are bound. Hills at sun 8°: a cloud over the sun gives cloud_vis 0.00, and the image agrees (disc 0.088, annulus 0.091). Broken cumulus at 0.6 coverage: the grid taps read 0.05 to 1.0. |
| Storm hide | **CPU scalar.** It is a twin of the sky's `sun_storm_hide`: four `RingSunshineCloudBridge.GetStormLevelAt` calls along the sun direction at 0.2 / 0.45 / 0.7 / 0.95 of 40 km, faded with `ringworld_storm_light`. Under the pinned storm the path level is 0.81 and the hide is 0, while the cloud alpha alone gives cloud_vis 0.46. The image shows no disc (disc 2.17 against annulus 2.28). **Without the storm term, 46 % of the flare would leak through a closed storm.** |
| Eclipse | **CPU scalar** `RingDayNightCycle.RawEclipseCoverage` (`CelestialEclipseMath.DiscOverlap`: the analytic covered fraction of the disc). Totality: coverage 1.0 and F_geom 1 (nothing in depth), and the sky has removed the disc (disc luma 1e-4 = annulus), so F = F_geom x (1 - coverage) = 0. Partial (scrubbed to coverage 0.60): a cloud also covered the sun (F_geom 0.08). The product, 0.08 x 0.40 = 0.03, matches the image's disc excess of 0.046 / 1.02 = 0.045 to within the cloud noise. A clean crescent check is left to LF-1's test. |
| Projection | Scene-data `get_cam_projection()` already contains the Vulkan y flip (`y.y` < 0): uv = clip.xy / w x 0.5 + 0.5 maps straight to texels with no flip, confirmed on an off-centre sun. Pixel-angle maths must use abs(`y.y`). That bug blacked out the first glare prototype: a negative angle in `pow` gave NaN. |
| Cost | Visibility, all 18 directions: one GPU timestamp tick (4.1 µs, about 0.004 ms). Glare square of 760² px: two ticks (about 0.008 ms). Root viewport Δ +0.02 ms (noise). Gate 0.1 ms: **pass**. |

Stills:
- `clear_noon_centre_glare_off.png` with `_glare_proto_k0p25.png`, and the same for `edge`: prototype PSF only, not calibrated. At k 0.25 the glare is barely visible (`compare` against glare off: PSNR 63 dB, `clear_noon_edge_glare_heatmap.png`); calibration is LF-1's job (question A2);
- `terrain_sun_8_set_sun.png` (cloud over the sun);
- `storm_sun.png`;
- `eclipse_phase2_sun.png` (totality);
- `far_arch_h0_p10.png`.

**Work-order changes from LF-0.**
1. **Split the sources.**
   - GPU taps: main depth, far depth and Sunshine accumulation alpha, same frame.
   - CPU per frame (one direction, so no GPU port is needed): eclipse coverage (`RawEclipseCoverage`); storm hide (the four-tap path twin above); camera-column cloud transmittance for the off-screen edge fade.
   - F = F_taps x (1 - coverage) x storm_hide.
   - LF-1 needs no GLSL copy of the storm map or the giant disc.
2. **Read Sunshine directly.** LF-1 reads Sunshine's live `accumulation_textures` instead of the R3a globals. R3a is not a dependency of the flare.
3. **`sun_visibility_gpu_test` uses the image channel as its oracle.** Disc minus annulus luminance against 2.55 x `sun_color` x transmittance gives an independent F at every pose, including storm and eclipse, where the sky shader itself hides the disc. The crescent case scrubs `EclipseEventDirector.ScrubToTau` to 0.3 to 0.7 coverage in a cloud-free window and checks against `RawEclipseCoverage` (3 %).
4. **Compositor order.** A "lens effects last" rule beside `EnsureRainHazeCompositorLast`, as planned.
5. **Flicker check.** It needs running clocks (wind): with clocks held, F was constant to 0.01 over 40 frames at the forest edge.

## LF-1 result (2026-11-18): glare on by default, tests pass

Implemented as LF-0 amended it. Uncommitted; evidence in `.godot/audit_baseline/common/atmosphere/lf1/`.

**What runs.**
- `RingSunGlare` (C# node in the game scene) publishes CPU scalars each frame: the sun direction the sky draws, the disc radius, eclipse coverage (`RawEclipseCoverage`), storm hide (`RingSunshineCloudBridge.GetSunStormHide`, the CPU twin of the sky's `sun_storm_hide`, refreshed at 10 Hz), the enclosure gate (underwater or cave fog weight), the camera-column cloud transmittance and the strength.
- `RingSunGlareComposite.gd` runs two computes, last in the compositor ("lens effects last": `RingLocalWeatherEffects.AttachLensEffect` / `EnsureLensEffectsLast`, kept by `EnsureRainHazeCompositorLast` on every path).
  - `ring_sun_visibility.glsl` (one group): 48 disc and 16 annulus taps on main depth, far depth and the live Sunshine accumulation alpha. F = F_taps x (1 - coverage) x storm_hide x gate.
  - Measures the displayed disc radiance L from the resolved HDR colour: the brightest clean disc tap minus the clean annulus mean. It holds L in a persistent buffer while the sun is covered.
  - `ring_sun_glare.glsl` adds strength x F x L x Omega_disc x GSF(theta) in a window around the sun, with a radial fade over the outer quarter of the window. GSF is the CIE 1999 general disability glare function (age 25, pigmentation 0.5).
- Strength 0 removes the effect from the compositor (byte-identical). With the sun off-screen the effect is inactive (no dispatch).
- Menu: Atmosphere tab, "Sun" group: "Sun glare" (0 to 1, default 1) and "Lens flare" (0 to 1, default 0; persisted, draws nothing until LF-2).

**Calibration.** Strength 1 is the human eye's physical glare of the sun as displayed. The displayed disc is only about 10x the sky beside it (L = 2.42 at noon against 2.55 x sun_color expected), while the real sun is about 1e5x. Physical glare of the displayed sun is therefore a faint fringe within about 1 degree: the window is 72 px at 1080p, PSNR 78 dB against glare off. No gain was added (user rule: no boost without approval). A debug-only, non-persisted `RingSunGlare.GlareSourceGain` exists for comparison stills (gain 30 and 1000 in `lf1/`). Question LF-A in the report.

**Tests.** `sun_visibility_gpu_test` (gpu, slow, 144 s) and `lens_settings_smoke_test` pass, with `rainbow_gpu_test`, `rain_streaks_gpu_test`, `atmosphere_menu_smoke_test` and `atmosphere_settings_smoke_test`.

| Case | F | Image oracle |
|---|---|---|
| clear noon, centred / 14 deg off | 1.000 / 1.000 | 1.000 / 1.025 |
| behind a ridge (sun 2 deg) | 0.000 | 0.000 |
| far arch (forced sun direction, main depth empty 1.00, far empty 0.00) | 0.000 | - |
| inside a cloud | 0.000 | 0.001 |
| eclipse coverage 0.524 (F_taps 0.958) | 0.456 = expected 0.456 | 0.385 |
| pinned storm | 0.000 | -0.021 |

- Strength 0 is byte-identical to the effect absent.
- Glare on changes only pixels inside the window.
- L_hold is identical with the draw on and off over 30 frames: no feedback.
- Cost: root viewport A-B-A 2.66 / 2.63 / 2.56 ms, inside noise (LF-0 measured about 0.012 ms by timestamps). 4070, indicative.

**Left for later.** LF-2 ghosts and starburst (user); the hub false sun and lightning (A3 default: no); `sun_disk_intensity` is read from the shader default (no material sets it today).

## NF-1 design (2026-11-18): near ground fog

This section overrides C.2 to C.8 where they differ. The NF-0 numbers stand. The user's answers apply: the fog is weather and location driven and terrain-aware; it runs on High and above at 0.4 to 0.8 ms and is off on Medium and Low (user, 2026-11-18 later answer); clear air stays clean.

### N1. Architecture

| Part | Owner | Runs |
|---|---|---|
| Drivers D (per kind, per camera) | `RingNearFog` (C#), 2 Hz, smoothed | CPU |
| Near-water field (water mask and surface height, 8 m texels, +-256 m) | `RingNearFog`, rebuilt on a worker after a 32 m move | CPU worker, uploaded as a small texture |
| Terrain height T0 and basin floor B | the height-shadow producer, copied on each L0 rebuild | GPU compute, no CPU cost |
| Density rho and lighting per froxel | `FogVolume` (`FOG_VOLUME_SHAPE_WORLD`) with `materials/ring_near_fog.gdshader` (`shader_type fog`) | Godot volumetric fog |
| Fog end for main-empty pixels (the whole sky) | `RingNearFogEndComposite.gd` + `ring_near_fog_end.glsl` | post-transparent compute, 1/8 resolution |
| Environment volumetric fog settings and on/off | `RingNearFog`, the only writer | CPU |

The density and lighting functions live once in `materials/ring_near_fog_common.gdshaderinc`. That file uses plain parameters only (no globals inside the functions), so the fog shader wraps it with globals and the compute expands it with `RingAtmosphereLuts.ExpandIncludes` and feeds it from its UBO and bindings.

### N2. Density (world space, terrain-relative, three kinds)

rho(x) = amount x [ rho_rad(x) + rho_mist(x) + rho_steam(x) ] x (1 + c_k x N(x)) x gate

**Radiation fog: pools in basins with a flat lid.**
rho_rad = sigma_rad x D_rad x Lid((z - B(x)) / H)
- z is the froxel's ring-vertical height.
- B(x) is the basin floor: the min of T0 over a radius of about 320 m, then a 3 x 3 box blur.
- H is the "Fog layer height" menu value, default 40 m.
- In a valley B is the valley floor, so the fog fills the valley to a flat top at B + H whatever the local slope. On a plateau or plain B is close to the ground, so the fog lies H deep. Above B + H there is none, so ridges stand clear.

**Mist: hugs the ground.**
rho_mist = sigma_mist x D_mist x Lid(h_g(x) / H_m)
- h_g = z - T0(x).
- H_m = clamp(0.35 H, 5, 40) m.

**Steam fog: over rivers and lakes.**
rho_steam = sigma_steam x D_steam x W(x) x Lid((z - z_w(x)) / H_s)
- W is the near-water mask and z_w the water surface height.
- H_s = 6 m.

**Lid.** Lid(u) = 1 / (1 + exp((u - 1) / 0.15)): flat below, smooth top, never sea-level anchored.

**Design constants (per metre).** sigma_rad = 0.01, sigma_mist = 0.002, sigma_steam = 0.01.

**Noise.** Two-octave world-anchored value noise at 40 m and 11 m, advected by the shared wind, contrast c = 0.5 (radiation and mist) and 0.9 (steam wisps). It fades to 0 over the last 40 % of the window in both the froxels and the fog end, which avoids aliasing in the large far slices.

**Gate.** gate = (1 - enclosure weight) x fast-travel and altitude fade (N6).

**The map frame.** `WORLD_POSITION` goes to the ring frame through `ringworld_height_shadow_frame`, with the curvature term h = f.z - f.x^2 / 2R (validated in NF-0 to 0.003 m). T0 and B are sampled in the L0 slot space (+-2 km, four times the window), with the existing bounds and params. L1 is not needed until NF-8.

### N3. Drivers D (CPU, 2 Hz, exponentially smoothed with tau 20 s)

Inputs at the camera:
- climate moisture m and temperature t (`TerrainSample.Moisture` / `.Temperature`, 0 to 1);
- wet map soak and rain-now (`RingWetWorld`);
- shared wind speed in m/s;
- the Sunshine coverage at the camera (night cloud cover);
- `CelestialFrame` (sun elevation, time since sunset, night length).

Derived factors:
- **humidity** h = 1 - (1 - m)(1 - 0.8 soak);
- **cool factor** f_T = 1 - smoothstep(0.7, 0.95, t);
- **clear night** f_clr = 1 - 0.8 coverage;
- **wind cut-offs**: radiation 1 - smoothstep(2, 5 m/s); mist 1 - smoothstep(4, 9); steam 1 - smoothstep(3, 8);
- **diurnal**: f_rad_time = smoothstep(0.1, 0.7, p) at night (p = time since sunset / night length). After sunrise it is 1 - smoothstep(3, 20 deg, sun elevation) until noon, then 0 until sunset. It is continuous at sunrise and at sunset, and stateless, so time skips and save and load need no history.

| Kind | D |
|---|---|
| Radiation | smoothstep(0.45, 0.8, h) x f_T x f_clr x f_rad_time x wind_rad x (1 - rain_now) |
| Mist | smoothstep(0.65, 0.9, h) x (0.3 + 0.7 f_rad_time) x wind_mist x (1 - 0.5 rain_now) |
| Steam | f_rad_time x (1 - t) x wind_steam (spatially only where W > 0) |

- A dry climate gets nothing.
- After rain the soak raises h, giving post-rain mist that fades as the wet map dries. This is the physical replacement for the removed MIST-1/2.
- Inactive: when amount x max_k(sigma_k x D_k) < 1e-4 per metre, `RingNearFog` fades the fog out over 2 s and then disables volumetric fog (byte-identical, measured in NF-0).
- D is weather state for every world, not generation.
- Advection (coastal) fog is not in the first cut.

### N4. Lighting (froxels)

**`ALBEDO`** = omega (0.99) x V_light(x).
- Sun term (by day): V_light = T_haze_sun(x) x V_cloud(x) x V_height(x).
  - T_haze_sun comes from the haze include (`ring_haze_sun_transmittance` through a small wrapper in `ring_haze_shared.gdshaderinc`; NF-5 measures its per-froxel cost). The fallback, if it costs over 0.08 ms, is one camera value: the fog is within 250 m of the camera, so T_sun barely varies.
  - V_cloud: the Sunshine near cloud-shadow cascade; register the fog material with `RingCloudShadowProjector.Register`.
  - V_height: `ring_height_shadow_sample`, which follows whichever light the lattice was built for (`ringworld_height_shadow_light`).
- Godot's froxels add the directional shadow atlas, which gives the tree and POI shafts.
- At night the sun factors must not zero the planet light. V_light blends with the day factor between the sun factors and the planet-light factors (cloud only), because `ALBEDO` scales every light.
- NF-5 confirms that the `StarLight` energy carries no camera-column cloud dimming; otherwise the cloud would be counted twice.

**`EMISSION`** = omega x A_fog(x), with Godot's ambient inject at 0. NF-0 showed that `EMISSION` is in-scattered radiance, independent of sigma.
- A_fog = 0.5 (1 + rho_g) A_up + 0.5 rho_g E_sun_ground(x) / pi.
  - The first half is the sky above and the ground reflecting it; the second is the ground bounce of sunlight, which matters for fog over sunlit ground.
  - rho_g = the haze ground albedo (0.11).
- A_up is the radiance a white upward Lambert surface gets from the Environment ambient (colour, energy, sky contribution).
  - day_side_atmosphere publishes it as a new global `ringworld_fog_ambient`, from the same inputs it gives the Environment.
  - That keeps the fog and the shaded terrain in one unit system, and fixes NF-0's saturated blue glow.
- Under the forest canopy, A_fog should take the forest lane's canopy sky occlusion (`ringworld_canopy_cover`) once FL lands. That is a follow-up, not NF-5.

**Settings and lights.**
- Anisotropy 0.7.
- Light volumetric energy 1 on the sun, planet and hub lights.
- Other OmniLights and SpotLights keep Godot's default of 1: lamps glow in fog, which is physical. NF-7 checks the cost near POI lights.

### N5. The sky gap: one fog-end pass after the far-scene composites (option (a))

NF-0 found that `RingFarBackdropComposite` overwrites every main-empty pixel (the whole sky) after Godot fogged it. Option (a) is kept; this design picks where it applies.

**One post-transparent compute, ordered after Sunshine, the lightning channels, the rainbow and the containment field composite, and before shock distortion, the rain streaks, the rain haze and the lens effects.**
- It computes T_end and S_end per pixel at 1/8 resolution: 16 exponentially spaced steps to the fog length, with the shared density and the N4 lighting minus the shadow atlas.
- It applies them, upsampled bilinearly with a depth-aware edge test, as follows:
  - **Main-empty pixels**, only while the far backdrop is active: c' = c x T_end + S_end. After Sunshine, c is exactly the unfogged radiance arriving at the window's end: far ring, sky, clouds and lightning together. So this one formula fogs the backdrop, clouds and bolts correctly.
  - **Main geometry beyond the window with cloud in front** (Sunshine accumulation alpha > 0): c' = c + alpha x S_end - (1 - T_end) x C_cloud. This corrects the cloud that Sunshine composited over already-fogged geometry. NF-4 confirms whether Sunshine's accumulation colour is premultiplied.
  - Every other pixel is untouched: Godot fogged it.

**Why not the planned double path** (the far backdrop applies T_end and S_end, and Sunshine applies them through GenericData):
- A post-Sunshine pass sees the composed result, so the order of fog, cloud and backdrop is exact for sky pixels.
- No Sunshine ABI change, no reimport.
- The far backdrop shader is untouched.

Option (b), the backdrop drawn inside the sky shader, stays the recorded end state.

**Calibration oracle.** With `RingFarBackdropComposite` disabled, Godot fogs the sky itself (NF-0: sky luma 0.23 to 0.57). NF-4 calibrates the fog-end integration, including Godot's phase normalisation and light-energy convention, against that, under uniform fog without noise or shadows, before turning on the shared density.

**Cost.** est. 0.02 to 0.04 ms (240 x 135 x 16 steps, a few taps each). Measured in NF-4.

### N6. Ownership, presets, fades

**Ownership.** `RingNearFog` sets a static `OwnerRegistered`. While it is set:
- `RingVolumetricCloudLayer` (`_Ready` and `SetCloudsEnabled`) does not touch `VolumetricFogEnabled` or `VolumetricFogDensity`;
- `RingLocalVolumetricCloudRenderer` does not touch them either (`_Ready`, `ConfigureEnvironment`, `RestoreEnvironmentSettings`). If someone enables that legacy renderer, the near fog stands down: they are mutually exclusive, and the legacy path is not production.
- `RingScaledFarViewport` writes only its own Environment and is unchanged.

**Environment while active.**
- length: preset;
- detail spread 2;
- temporal reprojection on at 0.9;
- GI inject 0, ambient inject 0, global density 0;
- anisotropy 0.7;
- sky affect 1 (only the skyless pixels matter; the backdrop pass handles the rest).

**Presets** (`environment/volumetric_fog/volume_size` and `volume_depth` are global and set at run time through `RenderingServer.EnvironmentSetVolumetricFogVolumeSize`):

| Preset | Fog | Grid | Length | Noise | NF-0 cost |
|---|---|---|---|---|---|
| Low | off | - | - | - | 0 |
| Medium | off (user) | - | - | - | 0 |
| High, Very High, Ultra | full | 96 x 96 x 80 (project setting) | 220 m | two octaves | +0.20 ms |

- Very High and Ultra stay at High's grid until NF-7 shows a reason; a 128-wide grid costs more for little gain at 220 m.
- Medium is off by the user's answer. A reduced Medium (48 x 48 x 40, 150 m, +0.08 ms in NF-0) stays possible as a one-line preset change.
- `RingGraphicsQualityController` is modified in the tree by another lane. NF-6 waits for that to land, or touches only its own new method.

**Fades** (all multiply `gate`, then the owner disables below threshold):
- ground speed 30 to 60 m/s: temporal reprojection smears;
- camera above max(B + H, T0 + H_m) + 2 H at the camera: the fog is out of reach below;
- enclosure (cave fog or underwater weight).

### N7. Grave-key menu (Atmosphere tab, "Ground fog" group)

- **Ground fog amount**, 0 to 2, default 1. 1 = as the weather produces it; 0 = off and byte-identical.
- **Fog layer height**, 10 to 150 m, default 40.

Kinds, noise, anisotropy and the grid are design data and presets. "Force fog" (a fixed D per kind) is a debug hook on `RingNearFog` for tests and stills, not a menu row.

### N8. Refined work orders (these replace the C.8 table)

Every order: `tools/run_tests.ps1 -Files <changed files> -Label nf-<n> -DryRun` first, then run. Register new tests in `tools/test_manifest.json`. Report the results.md summary and non-pass rows. Do not edit `project.godot`; propose lines. Do not touch other lanes' files.

**Gotcha (found in LF-1).** A `.glsl` RDShaderFile is used through its imported `.res`. The test runner does not reimport, so an edited `.glsl` silently runs the old SPIR-V.
- After every `.glsl` edit, run `tools/use_godot_48.ps1 --headless --import`.
- Then restore `addons/terrain_3d/bin/~libterrain...dll` if the import deleted it (godot-test-run skill).
- Check that the `.godot/imported/<name>.glsl-*.res` timestamp moved.

`-Files` on `scenes/VoxelRingIntegrationTest.tscn` selects about 370 tests (it is a core file). Use `-Tests` for the named set instead.

| WO | Role | Content | Tests and gates |
|---|---|---|---|
| NF-2a | implementer | **T0 and basin textures.** In the height-shadow producer (`RingTerrainHeightShadowGpu` / `RingTerrainHeightShadow.Gpu.cs`), after each L0 height upload and mip rebuild, a tiny compute copies the L0 height buffer into an R32F 1024^2 texture, and builds the basin floor B into R32F 64^2 (64 m texels: an 8 x 8 min pass to 128^2, then 2 x 2 to 64^2, a 5 x 5 min, then a 3 x 3 box, all toroidal in slot space). Publish `ringworld_height_shadow_t0_l0` and `ringworld_near_fog_basin_l0` (Texture2DRD globals; declare them in `project.godot`, proposed lines in the report). A GLSL-compatible sampling helper goes in a new `materials/ring_near_fog_common.gdshaderinc`. | `near_fog_terrain_fields_test` (GPU): T0 at 9 points matches `Terrain3DGameplay.SampleHeight` within 0.05 m, including across a recentre and the toroidal seam; B is no higher than T0 everywhere, and in a known basin pose B is within 2 m of the basin floor. Height-shadow tests unchanged (`-Files` selects them). |
| NF-2b | implementer | **Owner, environment, FogVolume, density.** `RingNearFog` node: the ownership switch (N6) and its two legacy writers; the environment config while active; the FogVolume + `ring_near_fog.gdshader` with the N2 density (drivers as uniforms; a debug "force D" hook); placeholder lighting `ALBEDO` = omega, `EMISSION` = 0; amount default **0** until NF-6; fades (N6); off below threshold. | `near_fog_identity_gpu_test`: amount 0, inactive, and active-then-off are each byte-identical to the feature absent, with FXAA and once with TAA. **Terrain-relative regression** (the MIST-1 bug), headless on the shared function: density 1.5 m above ground on a 1,000 m plateau equals that on a 0 m plain; radiation density at a ridge top 60 m above the basin floor (H 40) is under 5 % of the floor value; the lid is flat (equal density at equal z across a valley floor and its lower slope). |
| NF-3 | implementer | **Drivers and near-water field.** N3 drivers at 2 Hz with smoothing; the near-water field (64^2, 8 m texels, from `TerrainSample.WaterKind` / `ExactWaterLevel`) on a worker after a 32 m move, published as `ringworld_near_fog_water` plus a frame vec4. | `near_fog_drivers_test` (headless): monotone in humidity and soak; 0 above each wind cut-off; radiation 0 at noon, peaking at sunrise, gone by 20 deg; continuous at sunrise and sunset; no radiation fog under rain; steam only where W > 0; a dry climate gives 0. |
| NF-4 | implementer | **Fog end (N5).** `RingNearFogEndComposite.gd` + `ring_near_fog_end.glsl` at 1/8 resolution; the order rule (after Sunshine, the lightning channels, the rainbow and the containment field; before shock distortion, rain and lens) as an `Ensure...` beside the rain and lens rules; both application formulas; calibration against Godot's own sky fog (far backdrop disabled) under uniform fog. | `near_fog_composition_gpu_test`: (1) uniform fog, backdrop on (our pass) against backdrop off (Godot's sky fog): mean sky luma within 2 %; (2) a strip across a near-terrain to sky skyline and across a near-terrain to far-backdrop boundary: luminance step within 2 %; (3) a cloud in front of distant main-view terrain: within 3 % of the reference. `rain_cloud_compositing_gpu_test`, `haze_consistency_gpu_test`, `rainbow_gpu_test` and `sun_visibility_gpu_test` unchanged. Cost by timestamps at 1080p. |
| NF-5 | implementer | **Lighting (N4).** T_haze_sun, V_cloud (register with `RingCloudShadowProjector`), V_height, day and night blend, `ringworld_fog_ambient` from day_side_atmosphere, `EMISSION` = omega x A_fog; the same model in the fog end. | `near_fog_shadow_gpu_test`: fog under a cloud-shadow edge and behind a ridge darkens; tree shafts present at `forest_edge_eye` with fog forced on; **ambient calibration**: a white upward card in shade against thick fog with the sun off (A_fog prediction within 10 %); planet-lit night fog non-zero and blue-grey; cost of T_haze_sun per froxel (fallback if over 0.08 ms). |
| NF-6 | implementer | **Menu, presets, persistence, default on.** The N7 rows (live-tuning-setting skill), the N6 preset table; the fog is on (amount 1) on High and above and off on Medium and Low. | `near_fog_settings_smoke_test`; `atmosphere_menu_smoke_test` extended; preset test: Low and Medium off, High and above 96^2 x 80 at 220 m. |
| NF-7 | implementer, lead reviews | **Review pack.** Slot 3 v9 world, main game path, VSync as shipped. Movie Maker plus `tools/media/ffmpeg_tools.ps1`: a dawn valley from the floor and from the ridge; forest shafts after rain; river steam at dawn; planet-lit night fog; a walk and a fast turn (`flicker`: no reprojection pops); clear noon (must equal fog off). Cost A-B-A at the four standard poses with fog forced on (gate 0.6 ms on High), and inactive (0.05 ms). Calibration check of sunward brightness (physical; no clamp without the user). | user sign-off |
| NF-8 | contingent | Far form (C.6): L1 T0 and basin, the fog end to 2 to 5 km, later V7a. | own design |

**Order and parallelism.** NF-2a and NF-3 can run in parallel (different files). NF-2b needs NF-2a's include. NF-4 and NF-5 need NF-2b. NF-6 comes last before NF-7. Five implementer orders plus the review, about the size the plan estimated.

**Costs (4070, 1080p, High, indicative).**

| Part | Cost |
|---|---|
| Froxels, measured in NF-0 | +0.20 ms |
| T_haze_sun per froxel | est. up to +0.05 ms |
| Fog end | est. 0.02 to 0.04 ms |
| T0 copy and basin build | about 0.01 ms per L0 rebuild (recentres only) |
| CPU drivers | under 0.05 ms per tick |
| Near-water field | about 1 to 3 ms on a worker per 32 m moved |
| **Total while active** | **est. 0.25 to 0.3 ms**, under the 0.6 ms gate |
| Inactive | 0 GPU |

## NF-2a..NF-6 implementation (2026-11-19)

Uncommitted at the time of writing; evidence is in `.godot/audit_baseline/common/atmosphere/nf2a`, `nf2b`, `nf4` and `nf5`.

| WO | State | Main facts |
|---|---|---|
| NF-2a | done | The height-shadow producer copies L0 heights into `ringworld_height_shadow_t0_l0` (R32F 1024^2) and builds the basin floor `ringworld_near_fog_basin_l0` (64^2, 64 m texels) on every L0 upload. T0 matches SampleHeight bit-exactly at texel centres. The frame matrix translates by the ring radius, so a float32 `M x world + t` loses 0.25 to 0.5 m: `ringworld_near_fog_ref_world` / `_ref_frame` (camera reference, computed in double) make `ring_near_fog_frame_point` precise to about 0.03 m. |
| NF-3 | done | Drivers (N3, stateless, smoothed with tau 20 s) and the near-water field (64^2 at 8 m, built on a worker in 20 to 35 ms). `near_fog_drivers_test` passes. |
| NF-2b | done | `RingNearFog` owner, FogVolume (world shape) and `ring_near_fog.gdshader`. Noise is wrapped on a 4096 m lattice so it survives anchor recentres. Legacy fog writers stand down while the owner is registered. Identity (amount 0, inactive, on then off) is byte-identical under FXAA; TAA is not deterministic in this build even without the feature. The MIST-1 regression passes: mist density 1.5 m above ground at 1,657 m and at 5 m agrees to 0.015 %; a ridge 113 m above its basin floor gets 0 radiation fog; the lid is flat to 0.65 %. Cost of the froxels with fog forced on: +0.22 ms. |
| NF-4 | done | Fog-end pass after Sunshine (order: far backdrop, Sunshine, fog end, rain streaks, rain haze, lens). Sunshine's accumulation colour is straight, not premultiplied, so the cloud-over-geometry term is alpha (S - (1 - T) C). Calibrated against Godot's own sky fog (black-sky pixels) to 2.7 % worst band mean (0.2 to 0.8 % per-pixel median; tolerance 3 %). Cost: pass A 0.023 ms, pass B 0.071 ms at 1600x900. `RingRainbow.TailIndex` also stops at the fog end, so the rainbow's own slot rule does not fight it. |
| NF-5 | done (NF-5b) | Lighting; see "NF-5b result" below. |
| NF-6 | done | Menu "Ground fog" group (amount 0 to 2, default 1; layer height 10 to 150 m, default 40), persisted. The preset gate (`RingNearFog.Presets.cs`) reads `RingGraphicsQualityController.CurrentPreset` (no edit to that file): High and above on, Medium and Low off. |

**Engine facts that shaped NF-5** (Godot master `volumetric_fog.glsl` / `volumetric_fog_process.glsl`):
- Froxel density is stored as int(density x 1024), written only above 0.001 per metre.
- Scattering (ALBEDO x density) is stored at 1/2047 (r, g) and 1/1023 (b), truncated. EMISSION x density is stored at 1/511 and 1/255, truncated. EMISSION therefore cannot carry ambient light at fog densities: its floor is about 0.002 per metre.
- Godot's ambient inject samples the sky radiance map at world +Y and along the view ray. In the ring frame world +Y is the ring axis, 90 deg from local up, so it cannot be the fog ambient either.
- **Resulting design:**
  - The ambient comes from a dedicated fill OmniLight at the camera (cull mask 0, no shadows, no attenuation). Every froxel sees it at the same backscatter angle, so its contribution F is constant and is set equal to the target ambient A = 0.5 (L_sky equal-area mean + L_ground bounce).
  - ALBEDO_eff = omega (V S + A) / (S + A), which is exact when only the haze, cloud or terrain visibility V or only Godot's shadow atlas shadows a froxel.
  - Density and albedo use unbiased stochastic rounding, so thin mist below 0.001 per metre survives on average instead of being dropped.
  - The sun light's volumetric energy is gated to 0 below the horizon, because the StarLight stays on at night.

### NF-5b result (2026-11-19): lighting done, all named tests pass

| Item | Result |
|---|---|
| Fill light with cull mask 0 | Works: the froxel pass ignores the cull mask, so no surface is lit and the fallback (tiny surface energy, large fog energy) is not needed. Kept as the `DebugFillCullMaskFallback` hook. |
| Ambient fill | `RingNearFog.UpdateFill`: an OmniLight3D at the camera (no shadow, attenuation 0, specular 0, cull mask 0, range 8 fog windows), colour and energy set so its froxel term F = A at the engine's backscatter phase HG(-1, g). A = 0.5 (equal-area mean sky radiance + ground bounce F1), published by `day_side_atmosphere.gd` `_publish_fog_ambient` (cloud ambient, storm, underground access and night planet/floor as the ground bounce's sky term). Thick fog against omega x A: 0.995 / 0.996 / 0.999. |
| Composition bias found and fixed | The engine stores ALBEDO x (emitted density) but divides by the STORED density floor(rho x 1024) / 1024, so with the stochastic density rounding the effective albedo was rho_out / stored too high (about 10 % at 0.004 per metre). `ring_near_fog_round_albedo` now targets albedo x stored. Calibration bands went from -9 to -12 % to within 2.1 % (yaw 0: +0.44, +2.12, +1.22 %; yaw 180: -1.80, -0.89, -0.23 %); skyline median 0.49 % (was 4.55 %), far boundary 0.83 %, cloud over terrain 0.04 %. Tolerance 5 % (the order's number; 3 % would also pass). |
| Shadows | Cloud shadow: fog on the shaded side 0.800 (ground 0.243) at strength 0.8. Terrain shadow at a 2 deg sun: fog 0.753 (ground 0.645). Tree shafts at forest_edge_eye: 26.5 % of lit pixels differ from their local mean by over 25 %. Night thick fog (0.0100, 0.0142, 0.0243), blue-grey. |
| Thin-mist flicker | Mist only (0.002 per metre), static camera, FXAA, 60 frames: mean per-pixel luma step 0.028 levels (fog off 0.000), largest frame-mean step 0.035 levels, p99 pixel step 0.7 levels; ffmpeg flicker shows only a tiny 16-frame periodic ripple (the engine's froxel jitter cycle). The capture hold freezes shader TIME, so the dither varies with the froxel jitter only; in play it also varies with time. |
| Cost (4070, 1600x900, High, indicative) | A-B-A on root-viewport GPU time at hills_eye, sun 10 deg, fog forced on (H 150): fog on - off 0.369 ms (0.389 in a second run), of which the fill light 0.023 ms (0.012); haze per froxel vs camera value 0.005 ms (no fallback needed); fog end pass A 0.015 ms + pass B 0.062 ms. Noise floor 0.02 ms. Under the 0.6 ms High gate. |
| Clear dry air | Real drivers at noon, amount 1 against amount 0: 0 differing pixels; identity (amount 0, inactive, on then off) byte-identical under FXAA. Identity case (b) (amount 1, drive 0) failed intermittently (1 to 29 px): the owner still sampled the terrain at 2 Hz for the altitude gate, under the sampler's noise lock shared with the streaming workers, and the test grabbed present then absent only. Now clear air (drive 0) does no terrain sampling, and the test is A-B-A (present, absent, present again; a drifted attempt is retried). |

Stills: `.godot/audit_baseline/common/atmosphere/nf5/` (v8 fixture) and `nf5/rivers_world/` (generation 1 with water.rivers): `a_dawn_valley_yaw270/90`, `b_forest_edge_sunward` (forced post-rain mist, D = (0, 1, 0)), `c_river_steam_*` (rivers world only), `d_clear_noon_amount0/1` (byte-identical). Rivers world: `c_river_steam_river_level_eye` and `c_river_nofog_river_level_eye` (a river point from `InlandSwimProbe.FindPoints`, camera 4 m over the level, sun 6 deg rising, steam forced D = (0, 0, 1)); 283 water texels in the field.
- Two near-water fixes: the test's water scan was blind (GDScript cannot read the C# field object; `RingNearFog.DebugWaterTexelCount` / `DebugWaterProbe` now), and the field marked every texel the sampler classifies River or Lake, banks included; it now also requires the bed below the level (`SurfaceHeight < ExactWaterLevel`, the rule `RingLodSurfaceEditSnapshot` uses). `near_fog_drivers_test` gained a bank patch.
- Side observation for the water lane: the harness's resolved `river_bank_eye` in generation 1 put the camera at canonical height 97 m while the sampler reported River with exact level 836 m over the whole +-256 m field (the frame renders as underwater). Not investigated here.

Open for the user: mist strength stays 0.002 per metre (physical) unless they choose 0.004. NF-7 (review pack on the slot 3 world) is next.

### User answers after NF-6 (2026-11-19)
- Mist density stays 0.002 per metre (physical); no move to 0.004.
- Fixtures with no graphics controller keep the gate open (fog amount 1), so test scenes may show near fog in foggy weather. Tests that need clear air set it explicitly.

## NF-7 review pack (2026-11-20)

Evidence: `.godot/audit_baseline/common/atmosphere/nf7/` (mp4 + contact sheet + `flicker` per clip, `stills/` with fog off / on pairs, `cost/report.md`). Captures: `tools/near_fog_review_capture.gd` through `tools/tree_visibility_run.ps1 -Test near_fog_review -Resolution 1280x720 -EngineArgs "--fixed-fps 30" -World <bench_worlds\slot3_2026-11-16\world>` (real start menu + Continue, optimized C#, the user's live settings: High, FXAA, no TAA). The runner gained `-World` because the live slot 3 is now a different world (seed 1568115916, created 2026-11-19), where the perf poses are ocean; the review uses the frozen perf snapshot `slot3_2026-11-16` (v9, seed 2028030306), the same world as the cost table.

| Clip | Place, time | Drive (world's own -> forced) | Result |
|---|---|---|---|
| dawn_floor | mountain_valley_eye, sun 7 deg rising | (0, 0, 0) -> (1, 0.6, 0) | valley filled to a flat lid, bright sunward glow, ridges clear |
| dawn_ridge | ridge 88 m above that floor, 280 m away, looking down | (0, 0, 0) -> (1, 0.6, 0) | fog lies in the hollows within the 220 m window; valleys beyond it stay clear (NF-8) |
| forest_rain | forest_edge_eye facing the sun at 12 deg, wet map soaked, no rain | (0, 0.163, 0.049) -> (0, 1, 0) | tree shadow shafts in the mist; sunward: near-white share 0.03 %, p99 luma 0.90 (off 0.70): no saturation |
| river_steam | bank of a 100 m wide reach (level 2.0 m), sun 6 deg rising | (0.67, 0.34, 0.41) -> (0, 0.3, 1) | a low white band over the water; camera above the level (no underwater frame) |
| night | mountain_valley_eye, planet-lit night (giant 21 deg) | (0, 0, 0) -> (1, 0.6, 0) | blue-grey veil over the dark ground and the far ridges |
| walk_on / walk_off | forest_clearing_eye, sun 10 deg: walk 4 s, two 180 deg turns at 360 deg/s | (0.21, 0, 0.18) -> (1, 0.6, 0) / amount 0 | `flicker`: the only spikes are inside the turns, the same with fog off; the first frames after each turn change no more than the steady hold (0.54 vs 0.55 levels; off 0.84 to 0.86): no reprojection pops or lag |
| noon | plains_eye, sun 75 deg, world drive | (0, 0, 0) for 60 s | amount 1 vs 0: 21,613 px differ, the same as amount 1 vs amount 1 (21,845 px, cloud and grass noise); SSIM 0.99986: clear noon equals fog off |

Cost (`tools/perf_bench.ps1 -Scenarios save_spot,plains,forest_interior,noon_9km -Variants`, in-session A-X-A, 3 rounds x 8 s, RTX 4070 at P0, 1600 x 900 High, VSync off for the windows, clocks held at noon; baseline = owner present with the gate forced to 0):

| Pose | fog forced on (D = (1, 0.5, 0)) dGPU p50 | dwall p50 | drender CPU | owner absent vs inactive dGPU | A-A floor |
|---|---|---|---|---|---|
| save_spot | +0.38 | +0.38 | +0.14 | +0.01 | 0.04 |
| plains | +0.38 | +0.39 | +0.14 | -0.01 | 0.15 |
| forest_interior | +0.37 | +0.36 | +0.13 | 0.00 | 0.04 |
| noon_9km | +0.26 | +0.26 | +0.10 | 0.00 | 0.09 |

Under the 0.6 ms High gate while active; inactive is within noise (gate 0.05 ms). At 60 Hz VSync as shipped the frame is capped at 16.7 ms against 8.4 to 9.8 ms GPU here, so the fog does not change the shipped frame rate on this machine.

Fix found by the review: the activation rate counted the steam driver everywhere, although steam has density only where the near-water mask W > 0. Steam has no humidity term (time x (1 - t) x wind), so on calm nights and mornings the fog volume was on (about +0.38 ms) even on dry land with no water in reach, drawing nothing. `RingNearFog.ActivationRate` now counts the steam term only when the near-water field holds water (natural drive; forced drives unchanged), and the field is kept current while the steam drive and the world gate are non-zero. `RingNearFogWaterField.WaterTexelCount` caches the count on adopt. Tests: `near_fog_drivers_test` (new ActivationRate case), `near_fog_identity_gpu_test`, `near_fog_shadow_gpu_test` pass.
- Check on the slot 3 snapshot with the fix (`--clips=dawn_survey`, world drive at sun 7 deg rising and at a planet-lit night, `nf7/steam_activation_survey_report.json`): the steam-only states (mountain_eye dawn steam 0.168 and night 0.124, mountain_valley_eye 0.165 and 0.149, hill_forest_valley and plains at night, all with 0 water texels) stay off; before the fix the first four would have been on at 0.0012 to 0.0017 per metre. coast_eye (22 water texels) switches on for steam as intended; plains and forest_clearing at dawn are on for their small radiation term (0.024 to 0.063).

Open after NF-7 (questions for the user): the activation threshold (1e-4 per metre is about 2 % opacity over the 220 m window; forest_clearing at night sat at 1.07e-4, paying +0.38 ms for an invisible veil; a threshold of about 5e-4, 10 %, would avoid that), and whether the review should be redone on the new live slot 3 world.

### User sign-off (2026-11-20)
- NF-7 look signed off on the review clips.
- Activation threshold raised from 1e-4 to 5e-4 per metre (about 10 % opacity over the 220 m window).
- Performance stays on the 11-16 snapshot of slot 3 (the live slot 3 is now a different world).

