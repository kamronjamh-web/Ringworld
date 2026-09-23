# Ringworld Tech Demo (Godot 4)

Disposable captures, benchmark reports, and runtime logs are stored outside the
OneDrive checkout. See [Generated data and OneDrive](docs/GENERATED_DATA.md) for
the size report, cleanup, and before-travel workflow.

This project is a playable greybox prototype focused on:

- continuous full-ring traversal
- curved massive-scale ring perception
- custom ring gravity and orientation
- stable movement and debug telemetry

## Folder Layout

- `scenes/Main.tscn`
- `scenes/Player.tscn`
- `scenes/RingWorld.tscn`
- `scenes/RingSegment.tscn`
- `scripts/player_controller.gd`
- `scripts/ringworld_generator.gd`
- `scripts/gravity_controller.gd`
- `scripts/floating_origin.gd`
- `materials/*.tres`

## Tuning Formula

The ring generator uses the relationship:

- `circumference = 2 * PI * ring_radius`
- `player_speed = circumference / traversal_time_seconds`

For this preset, the project keeps a truly large world and on-foot speeds:

- `ring_width = 20000.0` (comfortably fits a GTA-scale map)
- `ring_radius = 80000.0` (exactly 4x width)
- `circumference ~= 502655 m`
- `500 m/s craft` loop time `~= 1005 s` (about 16.8 minutes)
- `2000 m/s craft` loop time `~= 251 s` (about 4.2 minutes)
- `10000 m/s hyper craft` loop time `~= 50 s`
- `8 m/s on-foot` loop time `~= 62832 s` (about 17.5 hours)

Equivalent radius equation for a target craft speed/time:

- `ring_radius = (craft_speed * traversal_time_seconds) / (2 * PI)`

## Quick Start

1. Open this folder in Godot 4.x.
2. Run `scenes/Main.tscn`.
3. Move with WASD, sprint with Shift, jump with Space.
4. Switch mobility tiers with `1`, `2`, `3`:
   - `1`: on foot `8 m/s`
   - `2`: fast craft `500 m/s`
   - `3`: very fast craft `2000 m/s`
5. Press Esc to release/capture mouse.

## Main Tuning Controls

Edit `RingWorld` node in `scenes/Main.tscn`:

- `ring_radius`
- `ring_width`
- `segment_count`
- `traversal_time_seconds`
- `player_speed` (auto-computed by default)
- `gravity_strength`

The player receives runtime settings from `RingWorld` when the scene starts.

## Current Scale Preset

- `RingWorld.ring_radius = 80000.0`
- `RingWorld.ring_width = 20000.0`
- `RingWorld.auto_segment_count_from_arc_length = true`
- `RingWorld.target_segment_arc_length = 2000.0`
- `RingWorld.auto_calculate_player_speed = false`
- `RingWorld.player_speed = 8.0`

## LOD + Streaming Preset

- High LOD ring sectors stream around the player (`high_lod_segment_range = 32`)
- Full-loop far ring shell stays visible for horizon continuity (`build_far_ring_lod = true`)
- Far ring detail is reduced with `far_ring_segment_count = 1024`

This keeps nearby collision/detail stable while preserving the visual cue of the opposite side of the ring.

## Opposite Side Visibility

The certified voxel-ring path keeps opposite-side visibility without extending
the gameplay camera across the full 10,000 km diameter:

- The normal camera is capped at 1,200 km, just beyond regional HLOD.
- Structural edge walls top out at +10 km, retaining a 2 km rim above the
  certified +8 km terrain/build ceiling without increasing the player build limit.
- The analytic surface, structural shell, and full-ring clouds render in an
  isolated `World3D` at a uniform 1:1,000 coordinate scale.
- Near the ground, the scaled analytic surface has a 625 km camera-local cutout
  so its coarse 30 km chords cannot rise above the true horizon. The cutout
  shrinks smoothly from 2-20 km altitude, restoring the fallback for flight.
- A dedicated far camera copies the gameplay camera's canonical transform and
  projection, then composites that scaled render behind nearby terrain.
- Fog density remains capped in `scripts/day_side_atmosphere.gd` so the
  opposite side is not extinguished before the analytic tier shades it.

This keeps the certified 10,000 km diameter out of the gameplay camera's
frustum and avoids nonlinear vertex-depth warping. Nearby voxels and regional
HLOD render over the far backdrop and retain normal coordinates, depth, and
precision. The legacy compressed full-ring meshes are not built for the
certified preset.

### Cloud and weather hierarchy

The camera-local volumetric tier now uses the MIT-licensed Sunshine Clouds 2
2.7 compositor, vendored from Bonkahe's upstream revision
`e4ecc2da53f0b675f5998a08eb7225fb83712054`. A ring-specific bridge maps its
world-space raymarch into canonical around/up/across coordinates, including
the ring's curvature, and feeds it the existing deterministic weather, wind,
cloud-height, and sunlight state. The existing merged clouds remain a fallback;
the curved far shell and projected terrain-shadow mask are unchanged.

The current runtime selects Sunshine for local clouds and retains the merged
mesh hierarchy as a reversible fallback. The older camera-local FogVolume and
its precipitation/lightning companion remain disabled. The bridge yields back
to the seeded curved shell during fast travel and upper-atmosphere flight;
scaled space continues to own distant clouds in every mode.

Cloud placement is deterministic for the selected terrain seed but no longer
reads as a uniform cell grid. Seeded 32 km macro cells can produce cumulus
clusters, elongated cloud banks, major fronts, and rare exceptional systems
with footprints up to 110 km. Individual merged formations use strong position
jitter and a heavy-tail size distribution; neighboring formations inherit the
system orientation so a large bank reads as one weather feature instead of
unrelated ovals.

Touching formations now become one true implicit surface instead of being
replaced by a larger generic cloud or joined by an overlapping connector mesh.
A spatial hash finds connected groups across every cloud profile and weather
system. The union extractor reuses every member's original metaball lobes,
rotation, dimensions, and vertical profile, then smooth-unions the complete
group and extracts one outer surface. Individual silhouettes therefore remain
legible while internal intersections and visible join seams are removed.

True unions are camera-local by design. Groups within the default 14 km union
range are built on a worker; more distant transition clouds retain the cheap
instanced representation until approached, and the continuous seeded far field
owns the horizon and space view. Original instances remain visible while a
worker result is pending, then swap atomically to the union surface. Detailed
and transition grids use separate adaptive cell sizes and per-axis caps.
Completed groups are batched into at most three meshes: conventional-shadow,
detailed, and transition. This keeps expensive remeshing off the main thread
and preserves bounded draw ownership without limiting which clouds are allowed
to merge when they enter the local range.

The merged-mesh pool contains four geometry classes rather than scaling one
generic oval into every role:

- Fair cumulus bases sit 0.8-1.8 km above the local visible surface.
- Low cloud banks begin as low as 350 m and remain broad and shallow.
- Rain-capable storm towers begin around 0.45-1.3 km and can reach 11 km above
  local ground.
- Rare anvil storms can reach 15 km, with broad cauliflower towers and flattened
  caps instead of vertically stretched cumulus meshes.

Each formation samples the edited terrain column and resolves ocean or lake
surface height before placing its base. Clouds therefore retain their intended
ground clearance over mountains and do not follow submerged seafloor beneath
open water.

The same moving hierarchy drives the 3D cloud patch, the 10-50 km transition
clouds, the scaled far-shell density field, and the 50 km projected shadow
mask. The default weather wind is 2.2 m/s around the ring and 0.7 m/s across
it. Existing near clouds, merged surfaces, and their projected-shadow frame
move continuously with that wind. Geometry is rebuilt only when the player or
wind enters another formation cell, relevant terrain edits change, cloud
quality changes, or an explicit coordinate refresh is required. Shared wind
does not change member contact topology, so it no longer causes periodic union
extraction.

Nearby conventional shadow casters are retained in a separate, non-duplicated
instance group inside 6.5 km. Its bounds always contain the player and the full
height and width of every retained storm, so turning the camera away cannot
frustum-cull a cloud whose shadow still reaches the local scene. Clouds between
6.5 and 50 km remain visible through the normal detail/transition hierarchy and
cast through the projected mask instead of consuming directional-shadow
cascades.

The unshaded merged-cloud material now supplies its own height-aware internal
lighting: lower lobes darken beneath their sunlit tops, with stronger depth in
storm towers and rain-heavy formations. It also samples the shared 50 km cloud
mask by projecting each cloud fragment along the sun ray into the player-local
ring frame. This gives overlapping upper cloud masses a soft, coarse
cloud-on-cloud occlusion cue without making hundreds of merged meshes receive
the directional shadow map or adding another cloud draw pool. The inspector
exposes `CloudSelfShadowStrength` and `CloudLayerOcclusionStrength` for the
remaining visual-tuning pass.

`RingVolumetricCloudLayer.QueryLocalWeather` exposes the current deterministic
system ID and type, influence, moving center, major/minor radii, orientation,
dominant formation profile, cloud coverage, precipitation potential, wind, and
cloud base/top above local ground at any canonical ring position. This is the
contract for localized rain and lightning, so weather effects can follow the
same fronts instead of using an unrelated random field.

The active Sunshine true-volumetric tier now consumes that full spatial
contract. `StormTower` cells grow kilometer-wide convective pillars through the
weather column; `AnvilStorm` cells add flattened upper caps that spread beyond
their cores while remaining clipped to the moving front ellipse. Defaults are
4.2 km pillar radius, 2.6x anvil radius, a cap beginning at 70% of the local
cloud column, and 92% sculpt strength. These are exposed on
`SunshineCloudBridge` as `StormPillarRadiusMeters`, `AnvilRadiusMultiplier`,
`AnvilStartFraction`, and `StormSculptStrength`. The same GPU density sampler
drives the projected shadow mask, with single-sample anvil shadows taken from
the upper cap rather than only the narrower middle of the tower.

Beyond the 50 km mesh handoff, the scaled shell preserves the weather system's
coverage and movement but does not reproduce individual tower silhouettes.

## Certified terrain elevation contract

The +8,000 m value is a construction ceiling, not the normal terrain target.
Generated terrain uses an absolute sea datum of Y=0 m. Routine mountain belts
are limited to 5,200 m. Each ring seed selects one exceptional continental
summit from 64 candidates; its 40 km footprint can reach 7,600 m while retaining
400 m of construction headroom. The same deterministic sampler supplies local
voxels, the minimap, regional HLOD, and the scaled far ring.

All proxy, regional, and analytic terrain tiers use the same seeded terrain
sampler and shared continent/climate/elevation palette. Global-summary caches
are keyed by a complete terrain-definition fingerprint, not the seed alone.

The older non-voxel prototype still uses the radius-aware clip logic in
`scripts/player_controller.gd` and the legacy far ring in
`scripts/ringworld_generator.gd`.

### Cross-ring flight handoff

Noclip flight now moves through the ring interior in double-precision
ring-centric Cartesian space instead of treating high-altitude movement as a
flat surface coordinate. Near the center, the canonical chart rebases to the
opposite surface while preserving camera heading and physical velocity.

While noclip is active, the mouse wheel adjusts flight speed logarithmically
from 1 m/s to 1,000 km/s and movement eases smoothly toward the selected
target. Keys `1` normal, `2` fast, `3` hyper, `4` warp, and `5` trans-ring
remain as snap presets. On the certified ring, preset 5 targets an
approximately 15-second surface-to-surface crossing before the Shift
multiplier. The selected wheel speed persists in the world save.

The destination voxel window is preloaded before the center crossing. During
arrival, the scaled far surface fades below 500-750 km, regional HLOD takes
over inside the 1,200 km gameplay frustum, and the authoritative seeded terrain
surface becomes physical. On direct arrival, flight mode disengages at the
terrain surface so the normal voxel motor and collision resume.

High-speed noclip now enters a speed-aware streaming mode at 750 m/s. Ordinary
local voxel scheduling, regional proxy rebuilds, structural-wall preparation,
local cloud placement, cloud unions, and projected-shadow refreshes pause while
their already-built terrain/cloud tiers remain visible. A cross-ring landing
focus is exempt from the voxel pause so its collision window can continue
preloading. Leaving fast travel schedules one current-position refresh instead
of processing every obsolete location crossed during flight.

Voxel classification, terrain population, procedural neighbor sampling, greedy
meshing, streaming-window planning, cloud-shadow mask rasterization, seamless
cloud-union extraction, proxy terrain generation, and structural-wall vertex
generation run as cancellable or latest-wins worker jobs. The main thread keeps
only scene-tree/RID work, bounded mesh and texture installation, physics state,
input, and the final transform assignment required by Godot. Floating-origin
refreshes reuse one geometry snapshot across all resident chunks.

The editable tier keeps 32 x 32 x 32 one-metre logical chunks. Its managed
greedy mesher uses direct axis scans and thread-local reusable work buffers,
avoiding per-face delegates and transient list/mask allocations. An optional
Windows x64 C++ GDExtension performs the face scan and greedy merge through one
batched C ABI call per chunk; curved-ring vertex warping and Godot mesh upload
remain in C#. If the native binary is missing or fails validation, workers fall
back to the managed implementation automatically.

Build the native release library with
`./native/ring_voxel_mesher/build_windows.ps1 -Configuration Release`.
`--disable-native-voxel-mesher` forces the managed path, while
`--require-native-voxel-mesher` makes missing/loading failures explicit.
`--voxel-mesher-self-test --voxel-mesher-benchmark` runs deterministic topology,
boundary, collision, warp, non-greedy, and concurrent-worker checks followed by
managed/native microbenchmarks. On the development laptop's fixed rugged-chunk
case, the original managed path measured 4.569 ms/chunk, the optimized managed
path 2.576 ms/chunk, and native 1.514 ms/chunk; all emitted the identical
`0xE8ADBDA7F6F2F0F0` geometry hash. These are mesher-throughput figures rather
than guaranteed whole-frame FPS gains.

Local artificial gravity is altitude-bounded for gameplay. It remains at full
strength through 10 km above the local seeded terrain, fades smoothly between
10 and 20 km, and applies no additional acceleration at or above 20 km.

### 3D terrain minimap

The voxel-ring scene includes a lightweight player-centered 3D scanner in the
bottom right. It builds an independent local heightfield from the authoritative
seeded terrain sampler and runtime edit snapshot; it does not render the voxel
world a second time. The player remains fixed at the scanner origin while
terrain scrolls continuously beneath them and moves vertically with altitude.
The display includes biome/elevation colour, 250 m contours, a player heading
marker, landing tether and surface marker, map span, terrain elevation,
altitude, and current gravity percentage. Terrain leaves the scanner volume at
high altitude rather than being artificially pinned beneath the player.

`RingMinimapTrackable` can be attached to stations, asteroids, ships, waypoints,
or simplified ring proxies. Each trackable supplies its category, map colour,
display size, detection range, and either a live world position or a canonical
position override.

Below a 3 km span, the orthographic map smoothly tilts and frames slightly
screen-up, reaching a 34-degree local terrain-diorama view at 500 m. Close mode
uses a denser 96-cell mesh, 10 m contours, 32 m terrain recentering, and
screen-appropriate player and landing markers. Wide zoom remains the strategic
map view, while high-altitude terrain and trackables retain their vertical
landing tether.

- `M`: show or hide the minimap
- `+` / `-`: zoom in or out from 500 m to 128 km
- `N`: toggle north-up and heading-up

The default display is 288 x 288 pixels and covers a 32 km player-centered
volume. A larger hidden terrain border moves smoothly every frame; terrain is
resampled after 256 m of travel or an edit. Viewport refresh remains capped at
30 FPS.

Tune these for night-side/day-side readability:

- `DaySideAtmosphere.preserve_opposite_side_visibility`
- `DaySideAtmosphere.day_side_opposite_visibility`
- `DaySideAtmosphere.night_side_opposite_visibility`

If the opposite side is still too faint, raise these visibility values (for example `0.30` day, `0.45` night).

## Opposite Day-Side Atmosphere Tint

The far LOD ring now uses a shader (`materials/ring_surface_far.gdshader`) that tints each far segment by:

- local day/night side (dot of local surface up vs sun direction)
- camera distance (stronger atmospheric shroud at long range)

Main tuning exports are on `RingWorld`:

- `far_day_atmosphere_color`
- `far_night_atmosphere_color`
- `far_tint_strength`
- `far_tint_min_distance`
- `far_tint_max_distance`
- `far_day_start_dot`
- `far_day_full_dot`

Result: from the night side, the opposite day hemisphere stays visible but appears shrouded blue rather than fully crisp.

## Section Day/Night Lighting Fix

Ring section materials now use `materials/ring_surface_daynight.gdshader`, so local ring hemisphere controls brightness (not only surface normal vs sun).

This prevents walls from staying brightly lit on the ring's night side.

Tune on `RingWorld`:

- `section_day_start_dot`
- `section_day_full_dot`
- `section_night_brightness`
- `section_night_direct_light_factor`

Lower `section_night_brightness` (for example `0.04`) for darker night-side section walls.
Keep `section_night_direct_light_factor = 0.0` for a strong ring-only eclipse shadow where sun-blocked regions receive no direct sunlight.

## Sun Lighting Stack (Near Gameplay)

`scripts/day_side_atmosphere.gd` now drives a practical sun simulation for massive scale:

- One `DirectionalLight3D` for the local gameplay area
- Local shadow bubble distance (not ring-wide shadows)
- Day/night fog and ambient blending with a shared hemisphere day factor

Core exports:

- `day_start_dot`, `day_full_dot` (twilight band width)
- `day_sun_energy`, `night_sun_energy`
- `day_shadow_distance`, `night_shadow_distance`
- `shadow_fade_ratio`
- `keep_half_ring_lit_globally`
- `day_ambient_color`, `night_ambient_color`
- `day_ambient_energy`, `night_ambient_energy`

Default behavior keeps `keep_half_ring_lit_globally = true`, so at least half the ring remains sun-illuminated at all times regardless of player position.

Far ring is kept mostly shader-lit (`materials/ring_surface_far.gdshader` uses `unshaded`) with realtime shadows disabled on far segments.

### HDR and massive-scale sunlight calibration

The local and scaled far viewports now use the same AgX output curve. The
defaults retain the `4.0` directional-sun energy while rolling HDR highlights
off with AgX white `16.29`, contrast `1.20`, and exposure `1.0`. This preserves
a forceful sun without linear-tone-map clipping. `DaySideAtmosphere` also limits
sky-derived ambient contribution to `0.35`, allowing its explicit day/night
ambient color and energy controls to affect the scene instead of the bright
sky and visible sun disk supplying all ambient illumination.

Analytic ring atmosphere now attenuates lit surface albedo and adds
in-scattered radiance through an unlit term. Terrain, ocean, hull, and cloud
materials no longer mix the atmosphere into albedo and then illuminate that
atmospheric color a second time. Day fog energy is `0.85`, sun scatter is
`0.16`, cloud daytime readability emission is reduced, and rough terrain uses
a low explicit specular response.

The existing procedural vertex palettes were checked under the certified
ground and 26 km capture paths. Full sRGB conversion crushed their mountain and
space-view midtones, showing that these palettes are already calibrated for the
current linear vertex-color path. `materials/ring_color_space.gdshaderinc`
keeps conversion available for future imported sRGB palettes but leaves it
disabled for the current procedural terrain.

### Terrain macro sunlight shadows

`RingTerrainGlobalLod` builds one whole-ring R8 direct-sun visibility mask from
the existing min/max terrain hierarchy. It searches unloaded terrain toward the
sun, accounts for ring curvature, wraps the circumference, clips at both
structural edges, and samples by normalized ring coordinates so floating-origin
shifts do not move the shadows.

The mask begins replacing conventional cascades at `3.2 km` and reaches its
default `0.34` strength by `6.5 km`. At certified scale it is `7670 x 78`
(about `0.57 MiB`), traces up to `96 km` in ten increasingly spaced steps, and
builds off-thread in roughly `1.1-1.4 seconds` on the Arc 140V test machine.
Regional terrain, global terrain, oceans, the analytic whole-ring tier, and the
scaled far viewport share this single texture. It multiplies directional
sunlight only; ambient, atmosphere, emission, and local lights remain intact.

## Static Sun Visual

`StarCore` now uses `scripts/sun_visual_anchor.gd` so the sun appears in a fixed sky direction (distant-star behavior) instead of orbiting around the character.

Tune on `StarCore`:

- `visual_distance`
- `follow_smoothing`

## On-Foot Anti-Clip Ground Probe

`scripts/player_controller.gd` includes an on-foot ground probe correction pass to reduce slight capsule penetration on the ring surface.

Key exports:

- `enable_ground_probe`
- `ground_probe_start_up_offset`
- `ground_probe_length`
- `ground_probe_push_gain`
- `ground_probe_max_push_per_frame`
- `on_foot_safe_margin`

If foot clipping persists, raise `on_foot_safe_margin` slightly (for example `0.10`) and/or increase `ground_probe_push_gain`.

## Distance Haze Settings

Distance haze and far color shift are configured in `scenes/Main.tscn` on the `Environment` sub-resource:

- `fog_enabled = true`
- `fog_density = 0.00014`
- `fog_aerial_perspective = 0.78`
- `fog_sky_affect = 0.6`
- `fog_light_color = Color(0.29, 0.38, 0.5, 1)`

Increase `fog_density` to compress the horizon harder. Shift `fog_light_color` cooler/warmer to change long-distance color mood.

### Water surface synthesis

The near and global ocean tiers share five directional sine-wave bands with
progressively higher frequencies and lower amplitudes. This fractional
Brownian-motion-style stack supplies broad swells and controlled small-scale
detail without relying on temporally unstable noise. Mild domain warping keeps
the wave trains from looking mechanically regular. Each band's complete warped
phase gradient is tested against the pixel footprint independently; octaves are
retired from shortest to longest before they cross the screen-space Nyquist
limit. `WaterWaveBandlimitStartRadians` and
`WaterWaveBandlimitEndRadians` control that transition.

Each sine is blended with a zero-mean exponential profile, producing narrower,
sharper crests without raising the average water level. The shader evaluates the
closed-form partial derivatives of the complete profile, including the domain
warp, so displaced geometry and per-pixel normals remain synchronized without
extra central-difference samples. `WaterWaveCrestSharpness`,
`WaterWaveCrestBlend`, and `WaterWaveNormalStrength` expose the main shape and
normal controls through `RingGraphicsQualityController`.

Calm open-ocean crests no longer produce foam merely by crossing a repeating
height threshold. Whitecaps require both a high crest and a filtered physical
slope above `WaterBreakingFoamSlopeStart`, then receive broad spatial breakup.
This prevents constructive wave interference from stamping a regular pale-dot
lattice across the surface. Screen-space normal variance also raises the
minimum reflection roughness through `WaterSpecularAaStrength`, while distant
normal detail now fades over `500-3000 m` instead of surviving to `5 km`.

Reflection weighting uses Schlick Fresnel with a clear-water dielectric
reflectance of `F0 = 0.020`: the surface remains transmissive face-on and
becomes strongly reflective toward grazing angles. `WaterFresnelF0` and
`WaterFresnelStrength` tune the physical baseline and its artistic intensity,
and are applied consistently to both ocean rendering tiers.

Surface refraction now uses the same Beer-Lambert extinction as the depth tint,
so copied scene color falls to zero instead of retaining a transmission floor.
Deep-water opacity defaults to `0.995`; kilometer-deep seafloor geometry and
terrain LOD boundaries therefore cannot leak through the ocean surface, while
shallow shore water remains transparent.

### Underwater visibility

`scripts/day_side_atmosphere.gd` replaces the ordinary air haze while the
camera is submerged. Clear-water distance extinction starts at `0.010`, gains
modestly with depth, and is capped at `0.032`, preserving useful near-field
visibility even in deep water. Underwater aerial perspective is disabled so
the bright sky cannot turn the water fog white. Fog, ambient light, and direct
sunlight instead shift from shallow cyan toward a darker blue-green as red
light is absorbed with depth.

`materials/underwater_no_goggles.gdshader` adds a restrained mip-filtered focus
blur and extra red attenuation. It approximates the loss of corneal focusing
power for unaided eyes in water while keeping nearby terrain readable for
gameplay. The effect fades smoothly when the camera crosses the water surface
and renders before the HUD.

## Day-Side Blue Atmosphere Only

Day-side-only atmosphere blending is handled by `scripts/day_side_atmosphere.gd` and attached in `scenes/Main.tscn` as `DaySideAtmosphere`.

The controller computes a local day factor using:

- player local ring surface up direction
- directional sun light direction
- smoothstep blend from night side to day side

Tune these exports on `DaySideAtmosphere`:

- `day_start_dot`, `day_full_dot`: where day-side blend begins/fully applies
- `day_fog_color`, `day_fog_density`: blue daytime atmosphere look
- `night_fog_color`, `night_fog_density`: dark/night-side thin atmosphere look
- `blend_speed`: transition responsiveness while moving around the ring
- `invert_day_side`: flip if day/night appears reversed

## LOLIPOP Tree Asset Preview

`scenes/TreeAssetPreview.tscn` is an isolated import-review gallery for the
Fir, Pine, and Maple LOLIPOP packs; it is not wired into production vegetation.
Run it with **F6** (Current Scene). Keys `1`–`4` select author LOD0–LOD3;
`F`, `P`, and `M` focus Fir, Pine, and Maple; `R` resets the view. Drag with
the left mouse button to orbit, use the wheel to zoom, and use WASD to pan.

The gallery preserves each imported LOD root's composed, non-identity transform
chain while removing the source demo-layout translation before placing the
specimen at its gallery origin. It does not assume identity wrapper transforms.

Remaining production work includes distribution/placement, render shader and
wind integration, day/night/eclipse/cloud-shadow response, batching and LOD
budgets. Maple bark also needs an adaptation for Godot's mixed-UV material
import limitation; see `resources/trees/lolipop/README.md`.

## Regional Forest Map Preview

Open `scenes/ForestRegionPreview.tscn` and run the current scene with **F6**.
This isolated map previews seeded woodland, dense forest, clearings, and
maple/fir/pine preferences over the Certified preset's terrain. Use the map-mode,
pan, zoom, and seed controls; hover for sample details. Production trees are
unchanged. See [the forest implementation checklist](docs/forest-region-implementation-checklist.md)
for tests, milestone status, and remaining playable-forest/performance work.

## Cinematic Space Main Menu

Press **F5**, or open `scenes/StartMenu.tscn` and press **F6**. The menu now flies
over a random Certified-style ring at approximately 20 km above terrain and
5 km/s, looking forward/down at 25 degrees. The menu uses a 125 m fine cache
(2049 x 1537, 3,149,313 samples) over a real 2048 x 65 full-ring terrain backdrop;
the two indexed meshes total 6,553,600 triangles. Fine sampling is bounded to
about 2 ms/2,048 samples per frame, far initial sampling to 2 ms/512 samples,
and texture uploads are batched near 10 Hz. Missing fine rows fall back to the
far ring. Initial fine-detail preparation took about 26 seconds in the local
1440p test; the coarse ring appears first and navigation remains available.

Menu-only SunshineClouds2 renders at half resolution with 128 view steps, five
light steps, a 2.2-6 km layer, and cached 512 x 512 RG cloud shadows at up to 5 Hz;
it uses no directional shadow-map pass. Haze is limited to 12% across roughly
250-1500 km. Rendering is isolated at native resolution up to 4K with 4x MSAA,
debanding, tone mapping, and restrained glow; it never overwrites gameplay
settings. Gameplay voxel/LOD streaming, trees, physics, and weather simulation
remain off. The local RTX 4070 test delivered about 60 backdrop updates/second
at 2560 x 1440 over a 15-second moving sample; this is not a full-game or
all-hardware performance guarantee.

Press **grave/backtick (`)** or click **Live Tuning** for menu-only Camera,
Lighting, Clouds, Shadows, Quality, and Terrain tabs (50 controls). Edits apply
live and autosave after 0.5 seconds to `resources/menu/menu_flyover_defaults.tres`, the project-wide
preset that ships with the game. Watch for **Saved**, then restart to confirm.
**Escape** closes the drawer; **Reset factory defaults** requires confirmation.
Exported builds use a labeled local override because packed resources are
read-only. Gameplay settings and world saves are unaffected. The drawer
offers 125/250/500 m terrain spacing and independent distant-ring detail. Choosing
250/500 m cuts fine sample/triangle counts by roughly 4x/16x; changes rebuild
the preview after a short debounce without rerolling the seed or route. Motion
pauses while detail is prepared. Visible-first generation, background workers,
and optional preview caching remain proposed.

Advanced cloud controls expose march distances, sunlight sampling reach, detail
falloff, reconstruction blur, and shadow vertical samples. The menu can use
four-tap reconstruction for tiny blur at half/quarter resolution, and disabled
atmospheric integration is skipped. Existing saved menu tuning is preserved;
factory camera/cloud values above apply only when not overridden by your preset.

The menu also exposes gameplay's compatible blue-noise motion, ray-start jitter,
stationary/moving history, and day/night cloud ambient brightness controls.
Ambient brightness follows the manually tuned sun elevation. Shadow controls add
extinction, an 8-200 km map radius, 128-2048 resolution choices, 0.25-20 Hz refresh,
and independent generation/receiver switches. The menu uses one shared GPU map,
so gameplay's separate near/far cascades, CPU fallback, cloud handoff, storm
sculpting, and preview buttons are not included. These menu controls autosave
only to the menu preset; gameplay cloud defaults remain separate.

Menu cloud history now gets fresh samples on every rendered frame, even with
the flyover paused and blue-noise motion set to zero. History follows ring
scrolling and bulk cloud drift; camera/appearance edits and route jumps clear
stale samples. With nonzero ray-start jitter, compare history 0 and 0.9-0.95 in
Quality, allowing 1-2 seconds to settle. Stationary history controls a paused
view; moving history controls the flyover. Higher values reduce grain but can
soften moving edges. This fix preserves saved tuning and gameplay behavior.

StartMenu is the current startup scene. See the
[space-menu checklist](docs/main-menu-space-checklist.md) for what to look for,
test commands, rendering limits, and remaining optional polish.

In gameplay, **grave/backtick (`)** now exposes matching Sunshine **Clouds**,
**Shadows**, and **Quality** controls, including Native/Half/Quarter resolution,
view/light samples, march distances, detail falloff, blur, and fast reconstruction.
Appearance controls bias the live weather instead of freezing its current state.
Only cloud edits autosave, after 0.5 seconds, to the separate project preset
`resources/clouds/gameplay_cloud_defaults.tres`. The shipped empty resource keeps
existing graphics-profile defaults until you edit a control. Your explicit cloud
overrides survive graphics-profile changes; confirmed **Reset cloud overrides**
restores the current profile/scene baseline. Exported builds use a local override.
Provider selection, profiling toggles, audio, terrain, and world saves are not
included in cloud autosave. Check the saved status, then restart gameplay to test
persistence. Use the tab arrows if later tabs are hidden on a narrow drawer.

## Ring/Voxel Integration Test

- New scene: `scenes/VoxelRingIntegrationTest.tscn`
- Uses the tech-demo scale: approximately 80 km radius and 20 km width.
- Uses 15,708 32-meter chunks around the circumference; the exact voxel radius is derived from that integer seam-closing length.
- `UseHabitableRingAxisLayout` maps voxel Y to radial terrain height/depth and voxel Z across the ring width.
- Terrain bedrock is at canonical voxel Y `-256`; the surface baseline remains near Y `+16`.
- Local voxel chunks provide terrain mesh and collision. `RingWorld` runs in far-ring-only mode so the opposite side remains visible without overlapping legacy near geometry.
- The integration explorer uses radial gravity and surface-relative movement.
- Player movement now uses a Minecraft-style swept AABB against authoritative voxel cells. The rendered concave chunk mesh is not used for player collision.
- Baseline height and block generation now come from the shared `RingTerrainSampler`; future proxy and far LOD meshes can consume the same seed and canonical samples as voxel chunks.
- The motor resolves radial, around-ring, and across-ring movement separately and compares normal movement with a 0.6-meter step-up route when blocked.
- Run this scene directly while integration remains under test; the project default scene is intentionally unchanged.
