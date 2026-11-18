# Sun lens flare, light shafts and near ground fog: feature arc plan

Status: read-only plan, 2026-11-18. No code changed and no GPU run was made. Costs marked **est.** are estimates. Every phase begins with a measurement step.

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
| LF-1 | implementer | Visibility pass and glare, the two menu rows (lens flare inert until LF-2), settings | `sun_visibility_gpu_test`: F = 1 in clear sky, 0 behind a ridge, 0 behind the far arch, 0 inside a cloud, crescent area within 3 % during an eclipse, 0 under a storm. `lens_settings_smoke_test`. Strength 0 is a byte-identical frame (`compare`). |
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
| NF-1 | lead | Design: `rho`, the drivers, the basin field, the owner arbitration, the fog-end pass and its GenericData ABI, presets | none |
| NF-2 | implementer | Owner, environment config, FogVolume and fog shader with `rho` (drivers as uniforms; strength default 0 until NF-6), the fast-travel and altitude fade | `near_fog_identity_gpu_test`: amount 0 or inactive gives a byte-identical frame against the feature absent |
| NF-3 | implementer | CPU drivers (time, humidity, wetness, temperature, wind, water proximity) at 2 Hz, smoothed | `near_fog_drivers_test` (headless): monotone in humidity and soak, 0 above the wind cut-off, burn-off with sun elevation, steam only near water. **Terrain-relative regression:** fog density at 1.5 m above ground equals that at sea level on a 1,000 m plateau (the MIST-1 bug). |
| NF-4 | implementer | Fog end; far-backdrop and Sunshine composites (reimport) | `near_fog_composition_gpu_test`: a strip across the near-terrain to far-backdrop boundary and against a cloud agrees within 2 % luminance; `rain_cloud_compositing_gpu_test` and `haze_consistency_gpu_test` unchanged |
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
