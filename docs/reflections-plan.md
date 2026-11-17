# Reflections plan: faster and better looking

Lead: Fable 5.2. Approved by the user on 2026-11-17 ("make reflections both faster and better looking", "study some clever cheat ways to simulate reflections that other devs have used").

## 0. Handoff

- **Phase A (this document) is done: reading, research and plan. No GPU was used, no game code was changed, nothing is committed.**
- **Nothing here is measured.** Every cost and every look claim about the running game is from code reading and is marked as such. Phase B (section 5) measures before any work order starts.
- Phase B starts only when the primary agent says the GPU queue is free.
- Three questions for the user are in section 8. Work orders R1-1, R1-2 and R3 change the look of water and wet surfaces and need the user's sign-off from before/after stills.
- Files other leads have open and uncommitted that this plan touches later: `scripts/Weather/RingWetWorld.Ssr.cs` (the dry-weather SSR gate), the terrain and proxy shaders (terrain lead). No work order here starts on those files until they land.
- Engine source read: `godotengine/godot` `master` (its `version.py` says 4.8 beta). The main repository has no tag for 4.8-dev7, so the exact dev7 commit was not read. The pass structure matches what the game's own comments say about 4.8 (HiZ trace, roughness cut at 0.7).

## 1. Summary

The reflections look weak for a reason that better screen-space tracing would not fix: **what a missed ray falls back to**.

1. **The fallback is probably flat black** (code reading, to be confirmed by one still in Phase B). `RingFarViewportColorPipeline.ConfigureMainEnvironment` sets the main Environment's background to a black colour so the far viewport can be composited in. `reflected_light_source` is never set, so it is at its default, Background. With a colour background the engine then uses that colour as the whole reflected environment, not the sky radiance map (section 2.5). If so, every miss of the engine SSR and the whole `SPECULAR` sky reflection of the water are black. The sky radiance map is already rendered every frame for ambient light, so using it costs nothing.
2. **The things that make this world a ring cannot be reflected by either tracer.** The arch, the far ring and the far cloud layers are composited as a backdrop without depth, and both tracers reject a hit without depth. The near clouds, rain and lightning are composited after both tracers have taken their colour copy. Even with the fallback fixed, the radiance map holds only haze sky, sun, planet and hub: no clouds, no arch, no terrain.
3. **Both tracers are tuned for a small world.** The engine SSR values (96 to 256 steps, depth tolerance 0.12 m, fade-out 1.6) predate the 4.6 rewrite and mean something different now. The water's own SSR reaches 120 m on High (the user's preset): a lake's far shore is beyond it.

**Recommendation** (section 4), in order of value for effort:

- **Fix the fallback source** and remove the water's constant blue stand-in (R1). Free.
- **Retune both tracers** for 4.8 and for this scale, and stop tracing pixels that cannot show a reflection (R1). Saves time.
- **"Far mirror"**: keep a half-resolution copy of the previous frame's final image (clouds, arch, rain included) and let glossy surfaces look up the reflected direction in it for anything far away. One or two texture reads replace a long ray march, and it is exact for distant things (R3). This is the main cheat, and it is what puts the arch and the clouds into lakes and puddles.
- **A ring-aware radiance map**: add ground, the terrain skyline, an analytic arch and the cloud decks to the sky's radiance pass, so that off-screen and rough reflections are right too (R2).
- **Analytic beam reflections** for emitter, projector and hub beams (R4).
- Kept in reserve behind a decision gate (R5): heightfield ray march for off-screen terrain, a custom SSR compositor pass, SSPR for large lakes.

Rejected for this game: a mirrored-camera planar reflection, "flip the world", time-of-day-blended baked captures, native ReflectionProbes outdoors.

## 2. What exists today (from code; unverified until Phase B)

### 2.1 Frame order of the main viewport, and what each reflection source can see

| Step | What happens | Consequence for reflections |
|---|---|---|
| 1 | Depth prepass with the normal-roughness buffer | Forced on by SSR and SSAO. |
| 2 | Pre-opaque: contact shadows (`RingContactShadows.gd`), SSAO, **engine SSR trace** | The trace reads the **previous frame's** colour, copied at step 8. |
| 3 | Opaque pass | Every opaque fragment reads the SSR buffer and mixes it over the environment reflection. |
| 4 | Sky | The main view draws nothing (black colour background). |
| 5 | Post-sky: `RingFarBackdropComposite.gd` writes the far viewport (sky, far ring, arch, far cloud layers) where the main view has no depth | The backdrop has colour but **no depth**. |
| 6 | Screen-texture and depth-texture copy | This is what the water's `hint_screen_texture` sees: opaque world and backdrop. **No near clouds.** |
| 7 | Transparent pass: water (its own SSR in the fragment shader), containment field | |
| 8 | Copy to the engine's last-frame buffer | Next frame's engine SSR colour: opaque, backdrop and water. **No near clouds, rain or lightning.** |
| 9 | Post-transparent: Sunshine clouds, rain haze, rain streaks, lightning, rainbow, containment composite, shock distortion | Never visible to either tracer. |
| 10 | Anti-aliasing, tonemap | |

Source: `render_forward_clustered.cpp` (`_pre_opaque_render`, "Copy Final Framebuffer (SSIL/SSR)" before "Process Post Transparent Compositor Effects"), and the `effect_callback_type` of each effect under `scripts/render/` and `addons/SunshineClouds2/SunshineClouds.gd`.

### 2.2 The engine SSR in Godot 4.8

Rewritten in 4.6 (PR 111210, "Overhaul screen-space reflections"). Passes, all compute:

1. Depth copy, or at half size a 2x2 downsample of depth and normal-roughness.
2. HiZ pyramid: the full mip chain of depth.
3. Trace, one ray per pixel, against the pyramid. Colour comes from the previous frame, reprojected.
4. Roughness filter: a Gaussian-blurred mip chain of the traced colour.
5. Half size only: a depth, normal and roughness aware upsample to full resolution.

The opaque shader then reads the result: `indirect_specular = indirect_specular * (1 - ssr.a) + ssr.rgb`.

**Which pixels trace.** Every pixel with depth and with roughness below 0.7. The opaque shader fades the result between 0.6 and 0.7. Nothing else limits it: not distance, not material, not whether the reflection could be seen.

**Settings that matter.**

| Setting | Game value | Meaning in 4.8 |
|---|---|---|
| `ssr_max_steps` | 64 Low, 96 Medium, **128 High**, 160 Very High, 192 Ultra, 256 Cinematic (`RingGraphicsQualityController.ResolveTuning`) | Cap on HiZ iterations per ray. A ray into open sky ends in a few iterations. A ray that skims a surface (wet ground seen at a low angle) stays in the fine mips and spends the whole budget. The PR states 64 covers a long range. The game's values were chosen for the old linear march. |
| `ssr_depth_tolerance` | **0.12** (scene); engine default 0.5 | Thickness in view-space metres, one constant for all distances. Used twice: a ray more than this far behind a surface is taken to pass behind it, and the final confidence is `(1 - smoothstep(0, tolerance, gap))^2`. The PR author found 0.2 "caused too many artifacts" and raised the default to 0.5. On receding terrain the depth step from one pixel to the next is metres, so at 0.12 a ray that crosses the surface between two pixels is taken to pass behind it. Expected result: holes and break-up in reflections of anything distant. |
| `ssr_fade_out` | 1.6 | `pow(1 - ray_length, fade_out)`, with the ray length in screen units. A reflection whose ray crosses half the screen keeps 33 %. Tall reflections fade toward their top. |
| `ssr_fade_in` | 0.12 | `pow(ray_length, fade_in)`. Close to 1 except for the shortest rays. |
| half size | desktop tier: full (`hardware_profile.gd`). Low, Medium and High: half while wet (`RingWetWorld.ApplySsrHalfSize`) | Trace and filter at half resolution, plus the upsample pass. |
| roughness quality | set to 2 in `project.godot` and by `hardware_profile.gd` | **Deprecated, does nothing** since 4.6. |

Fixed in the engine, not settings: a 5 % fade at the screen edges; the 0.7 roughness cut; a pixel without depth (sky, backdrop) is never a valid hit.

**Cost drivers.**
- Fixed while enabled, whatever the scene: depth copy, HiZ chain, the roughness-filter chain over the whole image, the last-frame copy, and one or two extra texture reads in every opaque fragment. At half size add the upsample.
- Per pixel: the number of pixels below roughness 0.7 times the iterations each ray uses. Grazing rays over wet ground are the worst case.
- Measured so far (`docs/perf-pass-2026-11.md` 0.14, dry, 1600 x 900): 0.86 ms at the save spot, 0.19 to 0.35 on the plains, 0.26 to 0.43 in forest. Wet (`docs/storm-revamp-s2-wet-world-design.md` 10.1, 720p): film-gloss SSR on Terrain3D about 0.18 ms, wet foliage about 0.2 ms before it was floored at roughness 0.71.

**Why misses look bad.**
- A miss shows the environment reflection, and that is probably black (2.5).
- The sky and the backdrop are never hits, so the upper part of every reflection is the fallback.
- Screen edges: nothing off screen can be hit. The 5 % edge fade hides the cut.
- Thickness: one constant for 1 m and for 10 km.
- The colour is one frame old and predates the cloud composite.

### 2.3 How the game drives the engine SSR

- Environment values in `scenes/VoxelRingIntegrationTest.tscn` (steps 96, fade-in 0.12, fade-out 1.6, tolerance 0.12); steps per preset from `RingGraphicsQualityController`; `world_size_launcher.gd:1886` also writes steps (128 or 96).
- The far viewports have SSR off (`RingFarViewportColorPipeline.ConfigureFarEnvironment`).
- Dry-weather gate, by another lead, uncommitted (`RingWetWorld.Ssr.cs`): SSR fades out over 3 s when the ground near the camera is dry and no glossy POI is loaded. It fades by raising both fade exponents, because the engine has no SSR strength.
- `ring_wet_ssr_roughness` (`ring_wetness.gdshaderinc`) keeps wet foliage at roughness 0.71 so it does not trace.
- Receivers that go below 0.7 and so trace: wet ground (film gloss to `WetGloss` 0.18, puddles 0.06), wet POI surfaces, POI metals (0.26 to 0.45), Weather Lung mirrors (0.04), far proxy river lines (0.06, `ring_terrain_proxy.gdshader:239`), far stand-ins, the far ocean.

### 2.4 Water, lakes and rivers

One shader, `materials/ring_ocean_proxy.gdshader` (ocean kind 1, lake 2, river 3), transparent pass, `ALPHA = 0.995`.

- **Its own SSR** (`ring_water_ssr`): a linear march in view space with a binary refinement, colour from `hint_screen_texture`.

| Preset | On | Coarse steps | Ray length | Camera distance |
|---|---|---|---|---|
| Low, Medium | off | 12 | 120 m | 250 m |
| **High** | on | 12 | **120 m** | **250 m** |
| Very High and the others | on | 24 | 250 m | 450 m |
| Ultra video | on | 32 | 400 m | 600 m |

  Thickness is 0.35 m near to 2.0 m at the end of the ray. Roughness cutoff 0.32. A sample without depth (sky, backdrop) cannot be a hit. On High one step is 10 m, so thin things (trunks) are missed or flicker.
- **On a miss**: the constant `reflection_color = vec3(0.34, 0.52, 0.68)`, scaled by the storm sky factor.
- **How the reflection reaches the screen**: `grazing_color = mix(optical_water, reflection_color, fresnel)` goes into `ALBEDO`. So the reflection, the SSR hit included, is **lit again** by the sun and the ambient light. A reflected hill darkens with the sun angle on the water, and at night the reflection goes with the light.
- **Sky reflection**: `SPECULAR` asks the engine for an environment reflection, attenuated by haze and storm factors. If 2.5 holds, that term is black and those factors do nothing.
- **Storms**: because the screen texture has no clouds, the SSR "sees the clear sky behind a closed storm", so it is faded out under storms (`storm_ssr_keep`).
- **Cost**: water is about 1.0 ms at the coast with the user's SSR settings against 0.3 ms at Medium, where SSR is off (`docs/perf-pass-2026-11.md` section 6, fixture world). The difference, about 0.7 ms, is mostly the march; B5 measures it on the real world.
- The far ocean (`ring_ocean_global.gdshader`, far viewport) has no SSR: a constant tint plus `SPECULAR`.

### 2.5 The fallback: the sky radiance map

- `scripts/day_side_atmosphere.gd` installs `ring_local_sky.gdshader` as the sky, realtime, radiance size 256. The radiance pass (`AT_CUBEMAP_PASS`) contains the Physical haze sky, the sun disc and halo (hidden under a closed storm), the planet and the hub halo. It has no stars, no clouds, no arch and no ground. Below the horizon it is the haze along a ray down to the floor.
- The scene Environment has `ambient_light_source = 3` (Sky), so this map is rendered and filtered every frame for ambient light.
- `reflected_light_source` is not set anywhere for the main Environment (a repository-wide search finds only the far viewports copying the main value, and tests setting Disabled). The default is Background.
- `RingScaledFarViewport.ConfigureMainBackground` sets the main background to `BGMode.Color`, black.
- Engine, `render_scene_data_rd.cpp`: reflection source Background with a sky background uses the radiance map; with a colour background it sets `SCENE_DATA_FLAGS_USE_REFLECTION_COLOR` and the reflected environment is that colour.

**Conclusion, to be confirmed:** in gameplay the reflected environment of the main view is flat black. `git log -S` dates the colour background to commit c52aff1 (2026-10-18, haze P0 to P3). The storm work of 2026-10-26 was written as if the radiance map were reflected ("the sun disc in the radiance map is a sharp glint otherwise").

One piece of evidence points the other way: the wet world notes say half-size SSR shows "more sky, fewer dark tree reflections". If the fallback were black, fewer hits should look darker, not more like sky. Hence the first Phase B step is a still with a mirror test sphere (B1).

### 2.6 Defects, ranked by expected visible effect

| # | Defect | Where | Confidence |
|---|---|---|---|
| D1 | SSR misses and the water's `SPECULAR` reflection fall back to black, not the sky | main Environment | high from code; one still decides |
| D2 | Arch, far ring, far clouds and sky can never be a hit (no depth) | both tracers | certain |
| D3 | Near clouds, rain and lightning are in neither colour source | frame order | certain |
| D4 | Lake far shore beyond the water march (120 m ray on High) | water presets | certain |
| D5 | Water reflection is lit again through `ALBEDO`; miss colour is a constant blue | `ring_ocean_proxy.gdshader` | certain |
| D6 | Engine SSR values predate 4.6: tolerance 0.12 m, steps 128 and up, fade-out 1.6 | scene, presets | values certain; visible effect to be captured |
| D7 | The radiance map has no ground, skyline, arch or clouds | `ring_local_sky.gdshader` | certain |
| D8 | Pixels trace that cannot show a reflection: far river lines, far stand-ins, wet ground kilometres away (the wet reach is 5.2 to 6.5 km) | receivers | certain; cost to be measured |
| D9 | Inert `roughness_quality` writes; two writers of `ssr_max_steps` | `project.godot`, `hardware_profile.gd`, launcher | certain |

## 3. Techniques studied, ranked for this game

Look gain and cost are judgements for this game on an RTX 4070 at 1080p, not measurements. Effort: S = one implementer work order, M = a design by the Lead role plus two or three work orders, L = a feature arc.

| Rank | Technique | Look gain here | Frame cost | Effort | Verdict |
|---|---|---|---|---|---|
| 1 | Correct environment fallback (use the sky radiance map) | very high if D1 holds | none (already rendered) | S | **Do first** (R1-1) |
| 2 | Previous-frame colour as the reflection source, with a far-field direction lookup ("far mirror") | very high: arch, clouds, far shore, mountains in water and puddles | est. 0.1 ms for the copy and mips, then 1 to 3 reads per glossy pixel; lets the marches get shorter | M | **Do** (R3) |
| 3 | Sky- and haze-aware radiance map with ground, skyline, arch and cloud decks; horizon and specular occlusion | high: everything off screen, every rough surface, every metal | est. under 0.1 ms, in the radiance pass only | M | **Do** (R2) |
| 4 | Retuned engine SSR, traced only where it shows | medium look, real saving | saves an estimated 0.1 to 0.4 ms while wet | S | **Do** (R1-3, R1-4) |
| 5 | Retuned water SSR (growing steps, thickness relative to distance) | medium to high until R3 exists, then near field only | none, or a saving | S | **Do** (R1-5) |
| 6 | Analytic reflections of beams and landmark billboards | medium by day, high at night and in storms | small | M | **Do after R2 and R3** (R4) |
| 7 | Ray march of the terrain heightfield | medium: off-screen terrain, flying camera, lake seen from above | est. 0.1 to 0.3 ms on lake pixels | M to L | Reserve (R5a) |
| 8 | Roughness-masked half-resolution HiZ SSR with temporal accumulation (custom compositor pass) | medium | about the engine's cost, less with tile masking | L | Reserve (R5b): only if the engine pass cannot be tuned |
| 9 | Screen-space planar reflections (SSPR) | high on large calm lakes only | 0.2 to 0.4 ms | M | Reserve (R5c) |
| 10 | Wet-ground tricks: puddle masks, ripple normals, Fresnel-only sky | already built (wet world) | — | — | Keep; they start to pay once the fallback is right |
| 11 | Box-projected or parallax-corrected cubemaps, local probes | low outdoors | re-capture cost | M | Not outdoors; enclosed POI interiors only |
| 12 | Planar reflection with a low-resolution mirrored camera | high on one flat water body | est. 2 to 4 ms and a second CPU cull | L | **Reject** |
| 13 | "Flip the world" mirrored geometry | as 12 | as 12 | L | **Reject** |
| 14 | Time-of-day-blended baked captures | low | memory, authoring | L | **Reject** |

### 3.1 Notes on each

**Screen-space planar reflections (SSPR).** Ghost Recon Wildlands (Genin) and the pixel-projected reflections of Cichocki. Every screen pixel is mirrored about a known water plane and written to its mirrored screen position through a small hash buffer; the water then reads that buffer. No ray march, and the cost does not depend on distance: 0.3 to 0.4 ms on consoles at quarter resolution (Genin); 0.95 ms against 3.2 ms for HiZ SSR (Cichocki). Limits: one plane per pass, no normal maps in the projection itself (ripples are applied as an offset), holes that need filling.
*Here:* the lakes are level and their sag from the ring's curvature is about 0.6 m over 5 km, so one plane per lake holds. Rivers slope, which is why Far Cry 5 left planar methods for SSR (McAuley). Lakes sit at different heights, and the ocean is displaced by the FFT. The far mirror (R3) gets the same result for distant content at lower cost and works on every water kind, so SSPR is held back for large calm lakes should R3 not be clean enough.

**Planar reflection with a mirrored camera.** Half-Life 2 "expensive water" and Unreal's planar reflection render the world again from the mirrored viewpoint. It is the only exact method, off-screen content included.
*Here:* the main view is 4.5 ms of near terrain, 3 to 5 ms of trees and a CPU that is already the limit in flight. A second view at quarter resolution saves fill, not vertices, draws or culling. Rejected.

**Cubemaps: parallax-corrected, box-projected, time-of-day-blended, and a cheap updating panorama.** Remember Me corrected local cubemaps against a box proxy (Lagarde and Zanuttini). GTA V renders a low-resolution environment cubemap every frame from simplified geometry (Courrèges).
*Here:* the engine already renders a panorama each frame, the sky radiance map. The work is to put the right things in it (R2). Native `ReflectionProbe`s render the main world with the main Environment, whose background is black by design (the far image is a compositor effect), so an outdoor probe would most likely capture a black sky and override the radiance map with it. They suit enclosed spaces only. Baked captures blended by time of day do not fit a procedural world with a moving sun, storms and eclipses.

**Sky- and haze-aware fallback, horizon and specular occlusion.** Frostbite (Lagarde and de Rousiers) and Activision's ground-truth specular occlusion (Jimenez et al.) both rest on one idea: a reflection ray that leaves toward a direction the surface cannot see must not return bright sky. The engine has a horizon term (`horizon * horizon`) and a specular occlusion tied to ambient brightness.
*Here:* what is missing is content. The lower half of the radiance map has no ground, and nothing in it stands where the mountains stand. R2 adds a lit ground term and the terrain skyline, so a reflection aimed at a hillside returns hazed hill.

**Ray march of the terrain heightfield.** The UHawk VR flight simulator uploads the terrain heightmap to the water shader and marches the reflected ray against it: 16 steps, and a hit returns a dark terrain tone times the ambient sky, blended over the cubemap. Maximum mipmaps (Tevs et al.) make such a march fast.
*Here:* the heights are already on the GPU for the height-shadow clipmap: two levels of 1,024 x 1,024 at 4 m and 16 m, with five max-mips (`RingTerrainHeightShadowGpu`, `RingHeightShadowLattice`), about 4 km and 16 km across. They are storage buffers, not textures a material can sample, so the first cost is publishing them. The second is the hit colour: no whole-ring albedo texture exists (the global LOD uses vertex colours). At ring distances a far hillside is mostly haze, so "terrain tone times light, then haze" is credible for hits beyond about 2 km and weak closer. What only this technique gives is terrain that is not on screen: a lake seen from a cliff or from the air. For a walking camera the far shore is on screen and R3 covers it. The skyline in R2 uses the same height data in a cheaper form. Reserve.

**Reflection impostors for landmarks.** The Samaritan demo reflected textured billboards ("image based reflections"): the reflected ray is intersected with a few quads. MotoGP drew low-detail, properly mirrored bikes into a flipped copy of the frame.
*Here:* the arch is not a billboard case. It is a cylinder the sky shader can intersect analytically (R2c), and when it is on screen the far mirror shows the real one. The beams (emitter, projector, hub) are line segments with a known glow profile: the reflection is that profile evaluated along the reflected ray, with no texture and no pass (R4). The Weather Lung is the reflector more than the reflected: its mirrors need the fallback (R1, R2), not an impostor.

**"Flip the world".** Mirrored geometry below the water, as old games did for mirrors and floors. The cost of a second scene, and the ring's water is not one plane. Rejected with the mirrored camera.

**Temporal reprojection of the previous frame's colour.** Killzone Shadow Fall (Valient) and the 4.6 engine SSR both read the previous frame. It costs one copy and makes available everything that is composited late.
*Here:* this is the way around D3. The engine's own copy is taken before the cloud composite and cannot be moved, so R3 takes its own copy after the last post-transparent effect.

**Half-resolution HiZ SSR with a roughness mask and temporal accumulation.** Stochastic SSR (Stachowiak), FidelityFX SSSR, HiZ cone tracing (Uludag), McGuire and Mara's screen-space DDA. Tile classification skips non-glossy tiles; temporal accumulation pays for rough reflections.
*Here:* a custom pass would allow a thickness relative to distance, the final image as colour source, the backdrop as a valid hit, and a real trace mask. It can be wired in without engine changes: the fragment built-in `RADIANCE` is mixed over the environment reflection before the engine's SSR term, and the game's receivers all have their own shaders. It is still a new compute pipeline and a new input for every receiver, and R1 to R3 remove most of the reasons for it. Reserve, behind captures.

**Wet-ground tricks.** Lagarde's "Water drop" series (wet surfaces darken, gloss rises, puddles fill hollows first) and Tatarchuk's rain course. MotoGP: a mottled alpha mask on the road varies the reflection so a flat mirror reads as puddles and rivulets, and the reflection is faded near hills and at steep camera angles. Racing games in general: an environment map everywhere, real reflections only where they are noticed.
*Here:* the wet world already has film gloss, puddles that fill cavities first, ripples and drying. What it lacks is something to reflect. If D1 holds, puddles reflect black wherever SSR misses.

**Also found.**
- *The flipped back buffer* (MotoGP): the reflection of distant scenery in a level road is the frame flipped vertically. That is the degenerate form of the far mirror, and its stated limit (wrong for objects close to the road) is exactly the limit handled in R3 by a distance test and a near-field march.
- *Trace only where it is noticed*: Assassin's Creed IV used a reflection mask and half-resolution buffers and reported about 1 ms on average on Xbox One (Wronski). R1-4 is the same idea through the roughness output.
- *Hole filling with alpha-weighted mips*: used by SSR resolves to cover misses with nearby valid colour. R3 uses it.

## 4. Recommended combination

Four layers. Each surface takes the first that has an answer.

| Layer | Answers for | Source | Work order |
|---|---|---|---|
| 1. Near trace | Things within tens to a few hundred metres: banks, trunks, POI walls, the player's surroundings | Engine SSR for opaque surfaces (retuned, masked); the in-shader march for water (retuned) | R1-3, R1-4, R1-5 |
| 2. Far mirror | Anything far away that is on screen: far shore, mountains, arch, far ring, clouds, sky, storm wall, lightning | Half-resolution copy of the previous frame's final image with a distance channel and filled mips; looked up by reflected direction | R3 |
| 3. Ring radiance map | Everything off screen, every rough surface | The sky's radiance pass, extended with ground, skyline, arch and cloud decks | R1-1, R2 |
| 4. Analytic lights | Sun, planet and hub highlights (exist in `light()`); beams | Closed form in the receiver | R4 |

### 4.1 Why the far mirror works

For a level mirror, the camera sees in the water what a second camera would see from the eye's mirror position, 2h below, where h is the eye height above the water. For content at distance D the two views differ by an angle of about 2h/D. With h = 1.7 m that is under half a degree for everything beyond about 400 m. So for a walking camera, the reflection of anything farther than a few hundred metres is the main view sampled in the reflected direction: no march and no depth test, only "is that direction on screen, and is what it shows far away".

The general bound, for rippled water and sloped wet ground: the error is at most s x sin(angle between the view ray and the reflected ray) / D, with s the distance from the camera to the reflecting point.

What it needs:
- **The previous frame's final image**, taken after the last post-transparent effect, at half resolution, with the distance to each pixel in alpha (the backdrop counts as infinitely far). Luminance clamped so the sun disc does not make fireflies.
- **A mip chain built with alpha-weighted downsampling**, so far colour floods under near occluders. A foreground trunk on screen would otherwise punch a hole in the lookup at the place its mirror image is not.
- **The previous frame's view-projection matrix** as a global, to project the reflected direction.
- In each receiver: project the reflected direction; read; accept by on-screen margin, far-ness and the parallax bound; pick the mip from the roughness. One refinement (read the distance, project the point at that distance, read again) brings the middle distances in.

Where it gives way:
- **High camera.** At 300 m altitude the bound admits only what is tens of kilometres away: arch, sky, far cloud. The rest goes to layers 1 and 3.
- **Off screen.** Looking down at water, the reflected directions point above the top of the screen. Layer 3.
- **Near field.** Layer 1 runs first and wins on a hit.

It replaces the long part of both marches. The water's march can be cut to the near field, and the engine SSR's job shrinks to contact reflections.

Precedents: the flipped back buffer of MotoGP; previous-frame reprojection in Killzone Shadow Fall and in the engine's own SSR; SSPR, which makes the same planar argument per pixel. The combination as described is this plan's proposal and has no shipped reference. **It needs a prototype before it is committed to** (R3-0).

### 4.2 Realism

Nothing in this plan boosts, widens or floors a reflection. Every term is the physical one or an approximation of it. If a later tuning step wants a reflection stronger than the model gives (for example the arch in a lake), that is a question for the user first.

### 4.3 Live-tuning menu

No new rows are planned. Reflections follow the existing systems (haze, clouds, storm light, wetness, water appearance). If R1-5 changes the meaning of the Water tab's SSR rows (steps, ray distance), the rows are renamed or removed in the same work order, not duplicated. Debug views (trace mask, far-mirror validity, mirror sphere) go into debug tooling and the existing Water SSR debug selector.

## 5. Phase B: measurements and captures

Machine: RTX 4070 desktop at P0, 1920 x 1080 and 1600 x 900, High preset with the user's settings (snapshot `slot3_2026-11-16`), VSync off, clocks running unless noted. Harness: `tools/perf_bench.ps1` with a new scenario file `tools/fixtures/perf_bench_reflections.json` and `-SettingsOverlay`; forced wetness through the wet world's force call as in `tools/wet_world_capture.gd`. One reused profile; prune at the end.

**Poses** (each as a `still` scenario):

| Pose | Purpose | Source |
|---|---|---|
| wet plains, eye height, pitch -3 and -30 | wet ground at grazing and steep angles; forced wet | `plains_eye` in `perf_bench_visible_opts.json` |
| wet plains with the arch | arch in puddles | `plains_arch` |
| wet plains, 300 m up | far wet ground filling the screen | `alt300_plains` |
| wet forest interior | trunks in wet ground | `forest_interior_eye` |
| puddle close-up beside a tree and beside a POI wall | contact reflections | to be placed |
| Weather Lung dam and overlook | mirrors at roughness 0.04 | `wl_dam`, `wl_overlook` |
| emitter rim, projector tower | metals at 0.26 to 0.45 | `emitter_rim`; projector to be placed |
| river bank looking along the river | sloped water, near bank | `tools/river_site_capture.gd` sites |
| lake, near shore looking across; the same with the arch in view; the same from a cliff looking down | far shore, arch, off-screen case | `tools/inland_water_render_gpu_test.gd` sites or a search of the world |
| coast | ocean with FFT | `coast_eye` |
| lake at night with a beam in view; lake under a closed storm | night and storm fallback | lake pose plus time and forced storm |

**B1. What is the fallback?** At the lake, the wet plains and the Weather Lung: a still as shipped, then the same with `reflected_light_source` set to Sky on the main Environment, then with SSR off in both. Add a mirror test sphere (roughness 0, metallic 1, debug tooling) to each. Decides D1 in a few minutes. Also record what the lower half of the radiance map shows.

**B2. Who traces?** A debug overlay of `roughness < 0.7` from the normal-roughness buffer, dry and wet, at the save spot, plains, forest, 300 m altitude and the Weather Lung. Explains why the save spot costs 0.86 ms dry against 0.19 to 0.35 on the plains, and sizes R1-4.

**B3. Engine SSR cost matrix.** A-B-A: off; on at full and at half size; steps 32, 64 and 128; tolerance 0.12, 0.5 and 2.0; fade-out 1.6, 1.0 and 0.5. At dry and wet plains, wet forest, wet 300 m altitude, the Weather Lung and the save spot. Report median GPU ms with the noise floor (`perf-capture` skill), and one still for each tolerance and fade value at the wet plains and the Lung.

**B4. Radiance map cost.** The sky is realtime at size 256; its radiance pass reads the sky-view LUT, or runs the haze at quality 4 when the LUT is not usable. Measure it by A-B against a sky that does not update. If it is above about 0.1 ms, a lower cadence is a candidate saving, and R2 must hold what it adds within the same figure.

**B5. Water SSR cost and reach.** Water SSR on and off at the coast, lake and river; High against Very High values; the three debug views (confidence, hit mask, step ratio) as stills. Short movies of the lake and the river while walking, with `ffmpeg_tools.ps1 flicker`, for shimmer on thin reflections.

**B6. Today's artifacts.** Stills and 10 s movies at every pose, with a sheet of each. They are the "before" for every later work order.

**B7. Far-mirror prototype** (after B1 to B6, this is R3-0): the capture effect, the lookup in the water shader only, behind a debug switch. Measure the copy and mip cost and take the lake stills. Go or no-go for R3.

Evidence goes under `.godot/perf_bench/reflections_*` and `.godot/captures/reflections/`, the numbers into a new section 9 of this document.

## 6. Work orders

Roles as in `AGENTS.md`: "Lead" is `claude-lead`, "Implementer" is `implementer`. Each order states its verification. Stills are judged with `ffmpeg_tools.ps1 compare` heatmaps. Any order that changes pixels on purpose ends with a before/after sheet for the user.

### R0. Evidence (Phase B)

- **R0-1** Scenario file `tools/fixtures/perf_bench_reflections.json`, the poses above, forced-wet and forced-storm options. Implementer. Verification: a quick run completes and writes stills.
- **R0-2** Debug tooling: mirror test sphere, trace-mask overlay. Debug only, not in the menu. Implementer.
- **R0-3** Run B1 to B6; write section 9. This lead.

### R1. Fix and retune what exists

Depends on R0. Each is S.

- **R1-1 Reflected environment = sky.** Only if B1 confirms D1. Set `ReflectedLightSource = Sky` in `RingFarViewportColorPipeline.ConfigureMainEnvironment`. The two far environments copy the main value (`RingScaledFarViewport.cs:1037` and `:1155`): pin both to what they resolve to today, so the far image does not change. Then review every receiver whose `SPECULAR` was tuned against a black environment: water, POI architecture, terrain wet gloss, trees. Verification: far viewport stills identical; dry matte terrain identical (compare); glossy poses changed as intended; the user signs off the sheet. No frame cost expected, confirmed by a perf_bench quick run.
- **R1-2 Water reflection composition.** Remove the constant `reflection_color` and the Fresnel mix into `ALBEDO`. Hand the SSR hit to the engine through `RADIANCE` (rgb = hit colour, alpha = confidence), so a miss falls through to the radiance map at the right roughness and the engine applies Fresnel once. The refracted and volume part stays as it is. Re-check the storm and haze factors on `SPECULAR` now that they act. Depends on R1-1. A design note by the Lead role first (which terms move, how the ocean and inland compositions stay continuous at river mouths); then Implementer. Verification: coast, lake and river stills by day, at night and under storm; `water_ssr_contract_test`, `water_rendering_smoke_test`, `inland_water_render_gpu_test`; user sign-off.
- **R1-3 Engine SSR values for 4.8.** Steps, tolerance and fades per preset from B3. Expected direction: steps 48 to 64 on High, tolerance 0.5 or above, fade-out near 1.0. One writer for the steps (remove the launcher write, as `docs/quality-presets-plan.md` item 11 already asks) and remove the inert `roughness_quality` writes. Verification: B3 poses before and after, with a saving at wet grazing views; `QualityPresetContractHeadlessTest`.
- **R1-4 Trace only where it shows.** Through the existing `ring_wet_ssr_roughness` pattern: far proxy river lines and far stand-ins stay above the cut; wet ground beyond a distance from B2 (expected 150 to 400 m, with a fade) stays above the cut while its direct highlight keeps the wet gloss. Whether near film-wet ground keeps its SSR is question 3 for the user. Verification: trace-mask overlay before and after; compare at the wet poses; a perf_bench delta at wet plains and wet 300 m altitude. Terrain shaders are the terrain lead's files: schedule after their work lands.
- **R1-5 Water march for this scale.** Growing step length and a thickness proportional to distance, so the same or fewer steps reach farther; per-preset values re-derived; High gets a far shore. If R3 is approved, this order only fixes the near field and R3 owns the rest. Verification: B5 stills and flicker movies; water tests; cost at the coast not higher than today.

### R2. Ring radiance map

Depends on R1-1. Design by the Lead role (one document section), then two or three Implementer orders. Files: `materials/ring_local_sky.gdshader` and includes; a small producer for the skyline.

- **R2a Ground.** In the radiance pass only, directions below the local skyline return lit ground: an average ground albedo times sun and ambient irradiance, with haze. Ends the "sky from below" on metal walls and rippled water.
- **R2b Skyline.** A panoramic horizon profile around the camera: for 256 or more azimuths, the elevation angle and distance of the terrain skyline, from the height-shadow clipmap heights. Rebuilt on a timer or after the camera moves a set distance. The radiance pass paints hazed terrain tone below it.
- **R2c Arch.** Analytic: intersect the direction with the ring cylinder (the sky shader already has the ring geometry), colour from a land and ocean tone, the whole-ring weather cover (`ring_far_weather_cloud.gdshaderinc`), the day factor at that ring position and the haze along the path. The Lead decides the albedo source: constant tones first, or a small ring colour strip baked from the global LOD vertex colours. Alternative if the look does not match the real arch: a slow-cadence low-resolution panorama rendered from the far world. More exact, but it needs a second camera in two far worlds.
- **R2d Cloud decks.** Where the direction crosses a cloud layer, opacity from the existing cloud shadow and weather maps, colour from the published cloud lighting. A Lead spike decides whether this matches the composited clouds well enough.
- Verification: mirror-sphere stills beside the real view at six poses and four times of day; the main view (not a cubemap pass) unchanged, bit for bit; radiance pass cost within the B4 figure; storm, eclipse and megastorm frames checked for a bright leak.

### R3. Far mirror

Depends on R1-1 and R1-2. M.

- **R3-0 Prototype and decision** (B7). Lead design, Implementer build, behind a debug switch.
- **R3-1 Capture effect.** A post-transparent compositor effect ordered after the last composite: half-resolution colour, distance in alpha, alpha-weighted mips; publishes a `Texture2DRD` global and the previous view-projection. Pattern: `RingContactShadows.gd` with `RingContactShadows.cs`. Main view only; other views skipped by target size as contact shadows do. Handles resize, the first frame and a teleport (no history: validity 0).
- **R3-2 Shared include** `ring_reflection_far_mirror.gdshaderinc`: project, validate, refine once, choose the mip. Parity test for the projection maths (`test-scaffold` skill).
- **R3-3 Water.** Near march first, then far mirror, then radiance map. Shorten the march. Remove `storm_ssr_keep`: the source now contains the storm.
- **R3-4 Wet ground, puddles and POI surfaces**, through `RADIANCE` in `ring_wetness.gdshaderinc` consumers and `ring_poi_architecture.gdshaderinc`. With it, re-run B3: the engine SSR may then go to half size on every preset.
- Verification: lake, arch, storm and night stills; flicker movies while turning fast (one-frame lag, edge disocclusion); lightning frames (a flash in the reflection one frame late is acceptable; a flash that persists is not); a perf_bench delta at every pose, with a net saving at the coast and wet plains; `compositor_temporal_stability_test`.

### R4. Landmarks and beams

Depends on R2 and R3. M.

- **R4-1 Beams.** A shared function: the glow of a line segment as seen along a ray, from the profiles in `ring_beam_orb.gdshaderinc` and the beam layers. Up to four nearest beams published as globals. Used by water and puddles, added to the reflection before Fresnel.
- **R4-2 Weather Lung.** Review the mirrors after R1 to R3 with stills. A local probe is considered only for an enclosed interior.
- Verification: night lake with an emitter beam, storm with a hub beam; no cost when no beam is in range.

### R5. Reserve, behind a decision gate

After the R3 captures, this lead proposes which of these to build, if any, and the user decides.

- **R5a Heightfield reflection march.** Publish the height clipmap as textures; march with the max-mips; hit colour as terrain tone, light and haze. For lakes seen from above and for flight.
- **R5b Custom SSR pass.** Only if B3 and R1-3 show the engine pass cannot serve the near field at an acceptable cost.
- **R5c SSPR for large lakes.** Only if R3 leaves visible errors on calm lakes at middle distances.

### Order and parallelism

R0, then R1-1, then (R1-2 and R1-3 in parallel), then R1-4 and R1-5. R2 and R3 can run in parallel after R1-2. R4 last. R1 alone should already be visibly better and no slower.

## 7. Risks and open technical points

- **D1 may be wrong.** Then R1-1 drops out, and R2 and R3 keep their value. B1 comes first for this reason.
- **Turning the sky reflection on changes every glossy surface at once.** Materials tuned against a black environment may come out too bright. R1-1 and R1-2 are therefore one reviewed batch with a sheet for the user.
- **Far mirror artifacts**: one-frame lag in fast turns; holes behind near occluders (filled from coarse mips, so softer there); feedback, water reflecting last frame's water highlights (luminance clamp); wrong parallax at middle distances from a high camera (handled by the validity bound, but the hand-over to the radiance map must not show as a line).
- **Radiance map resolution.** 256 per face is about 0.35 degrees per texel. The arch overhead is about 2 degrees wide (the ring's width seen across its 10,000 km diameter): a few texels. Enough for rough and rippled surfaces, soft for a still mirror. The far mirror carries the sharp case when the arch is on screen.
- **Transparent-pass details.** That `RADIANCE` behaves in the transparent pass as in the opaque pass is read from the shader source, not tested.
- **Engine version.** `master` was read, not dev7.
- **Shared files.** Terrain, proxy and wet-world files are in other leads' hands.
- **Lens droplets and HUD.** The far-mirror copy must be taken before anything that belongs to the lens and not the world. To be checked in R3-1.

## 8. Questions for the user

1. **Water look.** Fixing the water means its reflections become real: sky, clouds and the arch appear in it, the constant blue goes, and reflections no longer dim with the sun's angle on the water (at night a lake reflects the night sky and the arch). This changes a look that was tuned by eye. May I change it and bring before/after stills for sign-off, or should the present daytime look be held as the reference and only the errors be fixed around it?
2. **Budget.** If speed and look pull apart, which wins? Proposed rule: reflections never cost more than today at any measured pose, and cost at least 0.3 ms less at the coast and on wet plains on High. Anything better looking that would break that rule goes to Very High and above only.
3. **Wet ground.** The storm work kept SSR on all film-wet ground on purpose ("the reflected trunks and haze on wet ground are the wet look"). May I trial tracing only puddles and the ground within a few hundred metres, with the corrected fallback and the far mirror carrying the rest, and show the comparison? If the trial reads worse, the present behaviour stays.

## 9. Phase B results

(Empty until Phase B.)

## 10. Sources

Engine:
- Godot PR 111210, "Overhaul screen-space reflections" (merged 2025-10-21, milestone 4.6): https://github.com/godotengine/godot/pull/111210
- `godotengine/godot` `master`: `servers/rendering/renderer_rd/shaders/effects/screen_space_reflection*.glsl`, `servers/rendering/renderer_rd/effects/ss_effects.cpp`, `servers/rendering/renderer_rd/forward_clustered/render_forward_clustered.cpp`, `servers/rendering/renderer_rd/shaders/forward_clustered/scene_forward_clustered.glsl`, `servers/rendering/renderer_rd/storage_rd/render_scene_data_rd.cpp`, `doc/classes/Environment.xml`

Screen-space and planar:
- R. Genin, "Screen Space Planar Reflections in Ghost Recon Wildlands": https://remi-genin.github.io/posts/screen-space-planar-reflections-in-ghost-recon-wildlands
- A. Cichocki, "Optimized Pixel-Projected Reflections for Planar Reflectors", SIGGRAPH 2017 Advances in Real-Time Rendering: https://www.advances.realtimerendering.com/s2017/PixelProjectedReflectionsAC_v_1.92_withNotes.pdf
- S. McAuley, "The Challenges of Rendering an Open World in Far Cry 5", SIGGRAPH 2018 (why planar reflections were dropped for SSR): https://www.advances.realtimerendering.com/s2018/The%20Challenges%20of%20Rendering%20an%20Open%20World%20in%20Far%20Cry%205%20(With%20Notes).pdf
- B. Grujic, C. Cutocheras, "Water Rendering in Far Cry 5", GDC 2018 (GDC Vault session 1025033)
- T. Stachowiak, "Stochastic Screen-Space Reflections", SIGGRAPH 2015: https://www.ea.com/frostbite/news/stochastic-screen-space-reflections
- AMD FidelityFX SSSR: https://gpuopen.com/manuals/fidelityfx_sdk/samples/stochastic-screen-space-reflections/
- M. McGuire, M. Mara, "Efficient GPU Screen-Space Ray Tracing", JCGT 2014: https://jcgt.org/published/0003/04/04/paper.pdf
- Y. Uludag, "Hi-Z Screen-Space Cone-Traced Reflections", GPU Pro 5 (book chapter; cited from memory)
- B. Wronski, "The future of screenspace reflections": https://bartwronski.com/2014/01/25/the-future-of-screenspace-reflections/ and the AC4 GDC 2014 slides: https://bartwronski.com/wp-content/uploads/2014/03/ac4_gdc.pdf
- M. Valient, "Reflections and Volumetrics of Killzone Shadow Fall" / Killzone graphics, GDC 2014: https://www.guerrilla-games.com/media/News/Files/GDC2014_Valient_Killzone_Graphics.pdf
- Valve Developer Community, "Expensive water": https://developer.valvesoftware.com/wiki/Expensive_water
- Unreal Engine, planar reflections: https://dev.epicgames.com/documentation/en-us/unreal-engine/planar-reflections-in-unreal-engine

Environment maps, impostors, occlusion:
- S. Lagarde, A. Zanuttini, "Local Image-based Lighting with Parallax-corrected Cubemaps", SIGGRAPH 2012 Talks, DOI 10.1145/2343045.2343094
- A. Courrèges, "GTA V - Graphics Study" (environment cubemap rendered every frame): https://www.adriancourreges.com/blog/2015/11/02/gta-v-graphics-study/
- M. Mittring, B. Dudash, "The Technology Behind the DirectX 11 Unreal Engine Samaritan Demo", GDC 2011 (billboard reflections): https://www.nvidia.com/content/pdf/gdc2011/gdc2011epicnvidiacomposite.pdf and https://docs.unrealengine.com/udk/Three/ImageBasedReflections.html
- S. Lagarde, C. de Rousiers, "Moving Frostbite to Physically Based Rendering", SIGGRAPH 2014: https://blog.selfshadow.com/publications/s2014-shading-course/frostbite/s2014_pbs_frostbite_slides.pdf
- J. Jimenez et al., "Practical Real-Time Strategies for Accurate Indirect Occlusion" (specular occlusion): https://www.activision.com/cdn/research/Practical_Real_Time_Strategies_for_Accurate_Indirect_Occlusion_NEW%20VERSION_COLOR.pdf

Heightfield:
- "Approximating water effects using terrain height maps, part 1: Reflections" (UHawk VR): http://blog.uhawkvr.com/rendering/approximate-terrain-reflections/
- A. Tevs, I. Ihrke, H.-P. Seidel, "Maximum Mipmaps for Fast, Accurate, and Scalable Dynamic Height Field Rendering", I3D 2008 (cited from memory)

Wet surfaces and racing:
- S. Hargreaves, "MotoGP: road reflections": https://shawnhargreaves.com/blog/motogp-road-reflections.html
- S. Lagarde, "Water drop" series, parts 2b and 3a/3b (dynamic rain, physically based wet surfaces); summary: https://www.fxguide.com/?p=43362
- N. Tatarchuk, "Artist-Directable Real-Time Rain Rendering in City Environments", SIGGRAPH 2006: https://advances.realtimerendering.com/s2006/Chapter3-Artist-Directable_Real-Time_Rain_Rendering_in_City_Environments.pdf
