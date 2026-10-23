# Ringworld Tech Demo

Ringworld Tech Demo is a Godot 4 .NET game prototype for exploring a procedurally generated curved ring habitat. It combines large-world traversal, configurable terrain backends, local editing, distant ring rendering, weather, water, vegetation, celestial lighting, and local save slots.

The normal path is **Start Menu → local world slot → World Foundry → configured gameplay world**. This is an evolving technical demo; isolated pilots and retained development backends are deliberate parts of the repository.

## Features at a glance

- Three local world slots with create, load, duplicate, and delete flows.
- Custom, Certified halo-scale, and specialized Mega Ring world configurations.
- Canonical double-precision positions, floating-origin support, radial gravity, swimming, and high-speed cross-ring noclip travel.
- Certified Terrain3D surface editing for new Certified worlds; retained SmoothTerrain construction/cave path; legacy voxel rollback switches.
- Seeded terrain/biomes, regional LODs, analytic/scaled far-ring presentation, cloud and terrain-shadow prototypes.
- Ocean, lakes, rivers, local weather/audio, volumetric clouds, celestial day/night, imported trees, and GPU grass.
- Grave-key live tuning, diagnostic HUDs, alternate scenes, and targeted smoke scripts.

## Requirements

| Requirement | Details |
| --- | --- |
| Engine | Godot **4.8 dev7 .NET**, commit `c971f93e7`. |
| SDK | .NET **10.0** SDK; [RingworldTechDemo.csproj](RingworldTechDemo.csproj) targets `net10.0` with `Godot.NET.Sdk/4.8.0-dev.7`. |
| Renderer | Forward+ graphics renderer. |
| Terrain3D native validation | Current native patch validation is Windows x64 only. |
| Native voxel mesher | Optional Windows/Linux packages and C ABI checks; managed fallback remains available. |

## Quick start

1. Prepare the workspace-local Godot 4.8 dev7 and .NET 10 toolchain using [the upgrade guide](docs/godot-48-native-texture-streaming.md), then run `./tools/use_godot_48.ps1 -Build` and `./tools/use_godot_48.ps1 --editor --import`.
2. Open [StartMenu.tscn](scenes/StartMenu.tscn) and press **F6**, or press **F5** for the configured Start Menu main scene. The [playable terrain generation preview](scenes/TerrainGenerationPlayable.tscn) remains available as an isolated scene.
3. Select **Enter Ringworld**, then create or load one of the three local world slots.
4. Use the World Foundry to select a scale or **HALO-SCALE PRESET**.
5. Use **Advanced** for generation and eligible terrain-backend settings.
6. Start the world. [world_size_launcher.gd](scripts/world_size_launcher.gd) configures [VoxelRingIntegrationTest.tscn](scenes/VoxelRingIntegrationTest.tscn) before it enters the tree.

[Main.tscn](scenes/Main.tscn) is a retained legacy prototype, not the normal launch scene.

The initial World Foundry choice is the **Development Ring** at 80 km radius and 20 km width. Press **HALO-SCALE PRESET** to select the Certified 5,000 km / 318 km configuration.

## World scales and backends

| Mode | Scale | Notes |
| --- | --- | --- |
| Custom | Radius 80–8,000 km; width 5–318 km | Configured from the World Foundry slider and advanced controls. |
| Certified Ring | Nominal radius 5,000 km; width 318 km | 981,748 × 32 m chunks; circumference **31,415,936 m**. Enables Certified presentation paths. |
| Mega Ring | Radius 75,000,000 km; width 1,600,000 km | Specialized local-bubble/scaled-far backend; Terrain3D is unavailable. |

| Backend | Use | Edits and compatibility |
| --- | --- | --- |
| Terrain3D surface | Certified worlds only | 2 m raise/lower surface lattice, curved collision, recentering, separate surface/player-state saves. Caves and construction are unavailable. |
| Terrain3D runtime generation | Certified worlds only; Advanced backend item 2 | Opt-in `surface_runtime_generation_v1`: frozen canonical preview recipe, generator v17, fractional 2 m surface/collision edits, and isolated runtime state. |
| SmoothTerrain | Retained default for old slots; selectable when supported | Volumetric edits, construction overlay, and optional designed caves. |
| Legacy voxel rollback | Development only | `--legacy-editable-terrain`. |
| Hybrid smooth pilot | Development only | `--hybrid-smooth-terrain-pilot`. |

New Certified worlds select the original Terrain3D surface once when the launcher first recognizes Certified dimensions. Advanced item 2 explicitly selects runtime generation. It copies the saved preview recipe (or its default), applies the launcher seed, canonicalizes it through the shared C# generator, and freezes it in the new world. Existing runtime-generation worlds cannot convert backend or alter geography; existing metadata without a backend marker retains SmoothTerrain. No old journal or cave conversion is automatic.

Terrain3D renders a moving nine-region window spanning 768 m, with curved collision and a near-to-proxy transition at 84 m. A native culling patch keeps the curved surface visible; that patch has been validated on Windows x64. Environmental integration and broader traversal validation are still in progress; see [limitations and remaining work](#limitations-and-remaining-work).

Near Terrain3D shares the proxy terrain's projected cloud shadows, day/night lighting controls, night tint, and distance-based grass coverage tint. The former near-only altitude snow coating has been removed, so high terrain keeps its biome textures across the handoff. Existing grave-key lighting, cloud-shadow, grass, and rain-wetness controls apply to the near surface. Dry near terrain has no direct specular highlight, matching the proxy LOD; highlights fade in with accumulated rain wetness and fade out as the surface dries. See [surface shading validation](docs/terrain3d-surface-port.md#near-surface-shading-parity).

## Main gameplay controls

The table applies to normal gameplay. Isolated pilots and preview scenes have their own test controls.

| Input | Action |
| --- | --- |
| `W A S D` | Move. |
| `Shift` | Sprint. |
| `Space` | Jump; rise while swimming/flying. |
| `V` | Toggle flight/noclip. |
| `E` or `Space` | Rise while swimming/flying. |
| `C` or `Q` | Descend while swimming/flying. |
| Mouse wheel | Adjust flight speed. |
| `1`–`5` | Normal, fast, hyper, warp, and trans-ring flight presets. |
| Left / right mouse | Lower / raise Terrain3D; excavate / place construction in SmoothTerrain; remove / place blocks in legacy voxel terrain. |
| Grave key | Toggle live tuning. |
| `Esc` | Close grave drawer first, otherwise open/close the pause menu and release mouse. |
| Click, with menus closed | Recapture mouse. |
| `M` | Toggle minimap. |
| `N` | Switch between heading-up and north-up minimap views. |
| `+` / `-` | Zoom the minimap in / out. |
| `F1` | Toggle control hints. |
| `F6` / `F7` | Toggle performance diagnostics / detailed diagnostics. |
| `F8` | Toggle the presentation HUD for a clearer view. |
| `F9` | Toggle celestial diagnostics. |

The pause menu provides resume, nearest-surface return, graphics presets, display resolution/aspect settings, FSR/internal resolution, and worker/chunk controls. Underground recovery/checkpoints belong to retained cave backends.

## Terrain generation and rendering

### Terrain generation and editable surfaces

The launcher duplicates and configures runtime terrain settings: seed, vertical domain, terrain profile, cave contract, and world scale. Canonical terrain samples are shared with systems that need deterministic height or biome data.

Terrain generation combines continental structure, coasts, erosion, mountain belts, ridges, valleys, and regional detail. Current biome coverage includes grassland, forest, desert, tundra, alpine, and water, with blended grass/forest-floor/sand/tundra/rock terrain textures. The Certified profile uses sea datum 0 m, terrain floor -8192 m, routine mountains at or below 5200 m, one exceptional summit at or below 7600 m, build ceiling 8000 m, and structural walls to 10000 m.

The launcher installs the selected terrain backend before gameplay initializes. In Terrain3D mode, the surface field supplies terrain editing and player collision and owns a separate edit store. Retained smooth and voxel journals remain available to their original backends.

Runtime-generation Terrain3D worlds use terrain -8000 to +8000 m, sea level 0 m, an 8400 m reserved build ceiling, and a 10400 m structural wall top. They are surface-only: caves and natural overhangs are disabled. The grave-key Terrain3D panel shows the immutable policy and definition hash; change geography in the preview before creating a new world. The shared C# math authority has a GDScript wrapper for bulk 8-row strips. Production-local runtime generation is separate from the bounded asynchronous M1–M4 preview window.

Terrain3D brushes use falloff over the 2 m edit lattice. Edits persist in the surface store and update both the surface and curved collision while the moving 768 m window follows the player. Broader long-distance and high-speed traversal validation remains pending.

Milestone 6 applies bounded asynchronous preparation to both gameplay Terrain3D backends while retaining their generation and save contracts. Private worker-pool jobs produce exact height and RGBA8 biome payloads; each immutable cache tile is 128 × 128 samples at 2 m spacing, or 256 m per side, and the cache defaults to 8 MiB. It is primed from the synchronously loaded initial window, so a compatible 256 m axial recenter can reuse six of nine tiles. Workers pack RF payloads and project curved collision geometry. Two initial mesh owners are prewarmed and publish distinct active/staged rebased world transforms. Native upload is budgeted at 1 ms and collision work at 0.75 ms by default, although an indivisible native operation can exceed either budget. The rendered surface plus committed collision/query revision stays authoritative while a replacement prepares; movement holds at supported coverage when it is late. Edits apply immediately to the authoritative field and persist through the existing save path, debounce replacement work for 100 ms, and public field queries can observe pending edits before committed collision publication.

The grave-key Terrain3D controls persist workers, prefetch, cache, upload budget, collision budget, and tile size separately from world definition settings. A guarded synchronous-baseline mode exists only for the `m6-*` test fixture and cannot be enabled by normal world slots. A clean sequential 60 m/s out/back fixture at 1920 × 1080 Forward+ with VSync 1 reduced the worst measured interval from 1256 ms to 473 ms; intervals above 500 ms dropped from four to zero. Both runs had 713 support rays with no misses. Short asynchronous routes at 12 and 108 m/s also had no missing support or coverage waits. These runs do not establish 60 FPS: the 60 m/s asynchronous median was 31.343 ms, and substantial residual spikes occurred while terrain streaming was idle. See the [M6 measurements and remaining acceptance](docs/terrain3d-runtime-generation-checklist.md#6-gameplay-streaming--implemented-performance-and-human-acceptance-remain-open) for the full results and reproduction commands. Longer travel/edit routes, production-flight validation, human inspection, Mega support, and caves remain open.

The Terrain3D tab exposes brush radius, brush height step, mesh LOD count, and movement speed. Gameplay also exposes dry roughness, wet roughness, and rain wetness strength. Rain gradually darkens and smooths the near terrain material; dry terrain uses a matte surface, snow does not add liquid wetness, and biome-map alpha remains the alpine weight. These appearance and traversal settings apply live and have their own per-user defaults. The isolated pilot offers diagnostics for the loaded field and saves edits before returning to its menu.

New runtime-generation recipes use `ringworld_surface_coastal_v2`. Beach slope, approximate shelf width, shelf depth, and continental slope width are adjustable in the preview's grave-key menu and saved with the recipe. The default profile reaches 2 m depth at about 90 m and 6 m at about 280 m of estimated shore distance, with a 12 km shelf and a 30 km transition toward the basin. Shore distance is an approximation from the continental field, so actual transects vary. Previously frozen `ringworld_surface_reference_v1` recipes retain their exact geography and canonical identity; the new preview uses a separate `recipe_v2.tres` path. Coast changes apply when creating a new world, not when loading an existing one.

Runtime terrain proxy transitions use the same triangle diagonal as the 2 m surface lattice and morph to the parent's full curved triangle position. Terrain3D suppresses surrounding proxies only while its uploaded map covers the complete handoff area. Structural wall streaming uses the nearest periodic prediction across coordinate zero, preventing an accidental full-ring mesh request and its large allocation/upload stalls. See the [terrain and cloud fix validation](docs/terrain-rendering-fixes.md) for the regression tests and remaining visual/performance acceptance.

Global terrain samples replacement segments on a background worker, then constructs and installs their meshes on the main thread. Edits, cancellation, and floating-origin changes invalidate stale preparations while visible terrain remains installed. Quality changes rebuild only the affected geometry. Distant terrain still fills progressively during startup, and individual mesh commits can exceed the service budget.

Runtime lattice sampling combines height and biome evaluation, avoiding duplicate procedural-model work when preparing Terrain3D tiles. Smooth proxy builds also avoid intermediate vertex-buffer copies while retaining independently owned published mesh arrays. Exact sample comparisons and all 18 proxy payload comparisons matched the original outputs; focused sampling and allocation measurements are recorded in [the optimization report](docs/atmosphere-terrain-implementation.md).

The separate runtime-generation preview supports a regional overview and a playable curved patch. Select a point in the overview and choose **Explore selected patch** to walk or fly over the reference terrain. Bounded background workers sample terrain, pack RF images, and project curved collision vertices. The surface recenters in 256 m steps, reuses compatible cached samples and two native terrain windows, and keeps the current terrain and collision usable until a replacement is ready. Native region uploads and 16- or 32-cell curved collision tiles install over multiple frames. Predictive prefetch follows actual movement; missed deadlines pause traversal at supported coverage.

The grave-key **Playable** tab provides worker count, prefetch distance, cache size, upload/collision budgets, collider tile size, and staged coordinate travel alongside movement/mesh controls. Its diagnostics show preparation progress, publication timings, budget overruns, cache use, and boundary waits. Preferences persist separately from world slots. Milestones 1–4 remain this Certified preview, while M5 adds the separate opt-in gameplay runtime definition and persistence path described above. Mega, caves, preview brush editing, near Terrain3D macro/cloud projector integration, GPU geography inspection, and M6 full-stack performance acceptance remain open. First-use native setup can still hitch, and per-frame budgets cannot interrupt an individual native operation. See the [implementation checklist](docs/terrain3d-runtime-generation-checklist.md) for measurements, validation, controls, and remaining acceptance work.

SmoothTerrain retains volumetric excavation, construction overlays, optional procedural/designed caves, and underground recovery checkpoints. Validation can make incompatible saves read-only to preserve their data.

### Regional LOD, quality, and ring scale

Retained terrain applies sparse edits and schedules terrain LOD work on workers. Both production Terrain3D backends use bounded asynchronous local-surface preparation while their surface fields supply edit snapshots to surrounding LOD terrain. The separate runtime-generation preview uses the same publication model.

Regional LOD terrain, proxy tiers, analytic/global terrain, scaled far presentation, and ring structural edges are configured by [world_size_launcher.gd](scripts/world_size_launcher.gd). Desktop/laptop profiles and graphics presets expose detail choices. The optional native voxel mesher includes compatibility checks and a managed fallback.

Nearby collision/detail terrain renders in ordinary local coordinates. Regional proxy terrain extends the visible landscape, while analytic/global and scaled-far presentation provide horizon continuity. The Certified Ring uses complete scaled far-ring presentation without putting the full physical ring in one normal gameplay-camera coordinate range; Mega uses its specialized local-bubble/far-view path. Static scene values are not necessarily the values used after launcher configuration.

Runtime Terrain3D and its proxy tiers share the surface lattice's triangle orientation, and proxy transitions match the parent's curved triangles and underlay. The pinned Windows addon includes the matching standard-grid diagonal; the near shader uses stable small-angle curvature to align its visible surface with collision. The rendered spike fixture agrees with physics within 2.55 mm across its sampled rays. Restart Godot after updating the native addon. See the [terrain and cloud fixes](docs/terrain-rendering-fixes.md) for regression results, coast research, and remaining frame-time limitations.

Certified terrain plus finite wall heightmap shadows are a coarse asynchronous CPU whole-ring prototype with stable canonical UVs and persisted controls. Terrain receives separate sunlight and nighttime planet-light masks, including the near Terrain3D material and scaled far terrain. Local hardware shadow casting includes every active proxy tier through the current sun/planet shadow range; more distant tiers use the cached masks. Exact mountain/wall placement still needs broader visual acceptance; water/wall receiver heights, sparse GPU tiles, and Mega support are unfinished. Cloud shadows remain a separate system.

Deterministic landmark towers, foundations, and beacons provide visual scale references in main gameplay.

References: [Terrain3D surface port](docs/terrain3d-surface-port.md), [far-ring shadow plan](docs/far-ring-heightmap-shadow-plan.md), and [performance diagnostics](docs/performance_diagnostics.md).

## Traversal and minimap

Explorer movement keeps a double-precision position in around/up/across ring coordinates while local rendering recenters around the player. Walking, jumping, sprinting, swimming, flight, and cross-ring noclip support exploration at different scales. Radial gravity is fully active near the terrain, fades between approximately 10 and 20 km above the local surface, and stops adding acceleration above that range. Cross-ring noclip preserves heading and velocity through coordinate changes.

The player-centered 3D minimap derives seeded terrain plus current edit snapshots and shows biome/elevation data, contours, heading, landing markers, altitude, and gravity information. Its zoom spans roughly 500 m–128 km, with a tilted close view and heading-up or north-up orientation. The marker system also supports registered trackable objects.

## Water

The water stack supports ocean, lake, and river presentation. FFT simulation is ocean-only and follows shared wind when available, with an analytical fallback. Shore, open-water, river, and underwater audio paths are active.

Water rendering includes depth absorption, Fresnel response, refraction, screen-space reflections, shoreline foam, and breaking-wave foam. Underwater presentation adds depth-responsive fog, color absorption, and focus blur. Lakes and rivers use their own surface paths rather than the ocean FFT simulation.

The live Water controls cover analytical and FFT detail, absorption, foam, refraction, and reflection settings. Swimming and buoyancy are implemented for the explorer; general rigid-body buoyancy remains outside the current water system.

## Clouds, weather, and audio

SunshineClouds2 is the primary local volumetric cloud path, driven by deterministic weather and ring coordinates: wind, fronts, cumulus, cloud banks, storms, and anvils. A curved far shell and merged-mesh fallback extend cloud coverage. Distant rain curtains carry visible storm precipitation beyond the local particle field.

Cloud reconstruction rejects blur samples that cross foreground depth boundaries, removing the reproduced bright outlines around tree leaves while keeping blur enabled. Gameplay temporal filtering also retains valid history when jitter changes which part of a cloud a ray hits, smoothing the reproduced persistent interior grain. Ordinary foliage aliasing remains a separate quality limit. See the [night rendering diagnostics](docs/night-vegetation-atmosphere-diagnostics.md) and [cloud-history investigation](docs/cloud-history-grain-fix.md).

Weather fronts share wind and lightning state with local effects. Temperature and weather drive rain or snow; terrain and construction shelter attenuate precipitation and audio. Lightning combines bolts, flashes, cloud illumination, and distance-delayed thunder. Weather audio includes light/heavy rain, wind, distant storms, and rain impacts on soil, hard surfaces, and water. Rain wetness accumulates and dries as a shared value and now drives the gameplay Terrain3D surface material; broader material application remains pending.

Gameplay cloud history uses fractional-pixel color reprojection with opaque-depth validation for each history tap. Valid history retains the selected moving weight during continuous camera turns, without the former two-pixel cutoff or extra motion taper. Callback-paired cloud frames preserve correspondence across floating-origin recentering; invalid frame changes, offscreen samples, and foreground depth changes still reject history. Opacity is bounded before accumulation. A fixed blue-noise seed plus a changing scalar phase distributes ray-start samples over time, including when blue-noise motion is zero. Existing stationary/moving history and jitter controls remain live in the grave-key menu; the moving-history preference persists with the other cloud settings. The scaled far viewport publishes opaque depth in physical metres directly on the GPU, allowing both the cloud march and final composition to stop at far terrain. This depth path also handles viewport resize and keeps old GPU resources alive while submitted frames finish.

Terrain3D now receives the shared terrain haze model; macro/cloud projector integration remains open. See [water checklist](docs/water-system-implementation-checklist.md) and [weather checklist](docs/weather-system-checklist.md).

## Celestial lighting and shadows

`RingDayNightCycle` runs a 75-minute default ring day and offers 1×–100× live tuning. The current sky uses [ring_local_sky.gdshader](materials/ring_local_sky.gdshader) with [day_side_atmosphere.gd](scripts/day_side_atmosphere.gd): procedural stars, a banded rotating gas giant with storms, phase, solar eclipses, lighting events, and saved time.

Main and far presentation use AgX tonemapping. Day/night, altitude, storms, and eclipses affect atmosphere and lighting. SSAO adds contact shading; directional shadow cascades expose distance, filtering, resolution, and bias controls. Stepped sunlight updates help stabilize shadows. Projected cloud masks are active on supported materials; the coarse terrain/wall shadow prototype remains limited as described above.

Local sun shadows automatically suspend during deep night while planet illumination and planet shadows remain available. The handoff uses the sun's elevation over the local shadow coverage, with separate suspend/resume thresholds to keep twilight stable. It preserves sun direction and energy for clouds and the distant daytime ring, respects the saved sun-shadow preference, and keeps sun shadows when custom nighttime direct lighting or uncertain geometry requires them. The grave-key **Shadows** tab persists the automatic handoff toggle and elevation thresholds and reports the current state. See the [night-horizon investigation](docs/night-horizon-slowdown.md).

The celestial and shadow controls are available from the live drawer alongside diagnostic HUDs. Time, weather clock data, and the ring day state are retained with each world slot.

The **Atmosphere** tab provides an **Atmospheric haze** switch, Natural and Cinematic presets, visibility distance, density, altitude falloff, aerosol strength, forward scattering, and sun warmth. Its status reports when haze is off or density is zero. The switch follows the world's saved haze setting and stays synchronized with the pause menu; profile controls apply live and save as user preferences. Terrain, tree mesh/impostor LODs, Sunshine clouds, and the far cloud clipmap/shell use the same profile and camera-relative paths through the ring's finite-width air layer. Sunshine applies haze after storing temporal history, preserving cloud opacity and avoiding repeated haze accumulation. This is an analytic distance-haze approximation; a second remote air shell across the central vacuum is excluded. Local volumetric fog and a lookup-table atmosphere renderer remain future options. See [implementation and validation](docs/atmosphere-terrain-implementation.md).

The cloud march clips its final interval to the nearer local or far opaque surface, samples inside the visible portion, and weights that portion's density by its remaining length. This prevents behind-terrain samples without simply dropping foreground cloud. Authored cloud heights are unchanged. Mountain footprints that exclude clouds at every altitude, including above peaks, remain the next stage of the [cloud terrain-awareness plan](docs/cloud-terrain-awareness-plan.md).

Gameplay cloud history validates the stored opaque depth separately from the cloud march's stopping condition. Clouds in front of terrain and ring walls retain the selected stationary/moving history weight when jitter changes their apparent opacity. Each bilinear history tap is checked before blending; newly occluding terrain rejects old cloud history. The existing history textures hold this metadata, without additional cloud ray samples or texture allocations. Controlled GPU comparisons cover stationary and moving views at 1.2 km and 60 km, plus foreground occlusion.

## Vegetation and grass

Imported LOLIPOP trees include 30 variants, authored LODs, nearby collision, persistent asynchronous tiles, GPU culling, and CPU fallback. Forest regions place Maple, Fir, and Pine species around deterministic clearings and regional rules. Authored mesh LODs and crossed-card far representations carry the forest through distance transitions.

Standing GPU grass uses deterministic placement, wind motion, player bending and recovery, GPU culling, and a CPU fallback. Optional distance LOD is implemented and defaults off. Live controls expose grass density, distance, appearance, wind, and interaction settings.

Grass follows the local ring day/night lighting: the globally active sun no longer lights blades on the local night side. Ambient and nearby lights remain active. Daytime shading is checked against Godot's built-in material lighting. See the [night vegetation and atmosphere follow-up](docs/night-vegetation-atmosphere-diagnostics.md) for the fix, cloud-edge diagnostics, and optional mist investigation.

References: [Biome workshop](docs/biome-workshop.md), [forest-region checklist](docs/forest-region-implementation-checklist.md), [grass checklist](docs/grass-system-implementation-checklist.md), and [Sunblocker demo](docs/sunblocker-demo.md).

## Menus and live tuning

The Start Menu shows the last-session snapshot (or a starfield) over a warm live world; it has no moving flyover. It manages the three local slots. Live-world menu tuning arrives with the D4 Live world drawer (`docs/phase-c-live-menu-work-orders.md`).

The World Foundry stores the selected scale, seed, terrain profile, and eligible backend marker with the slot metadata before it creates the gameplay scene. The normal world is configured dynamically; scene inspector defaults alone are not the final runtime world configuration.

The normal grave-key drawer exposes these tabs when their systems are present:

| Tab | Scope |
| --- | --- |
| Lighting, Shadows, Clouds, Quality | Lighting, weather audio, cloud quality, cloud/shadow controls. |
| Atmosphere | Haze enable switch and status, Natural/Cinematic presets, and persistent visibility, density, altitude, aerosol, scattering, and warmth controls. |
| Anti-aliasing | FXAA, SMAA, TAA, MSAA, plus FSR 2 render-resolution presets. Native is default; adaptive resolution defaults off; AA resumes on leaving FSR. |
| Motion | Player physics interpolation and maximum walkable slope. |
| LODs, Terrain | Streaming/proxy and retained terrain controls where active backend permits them. |
| Terrain3D | Active Certified Terrain3D surface controls, or isolated prototype launcher. |
| Water, Grass, Vegetation | Render, interaction, tree, and grass controls. |
| Diagnostics | Rolling frame/CPU/GPU graphs, mean/p95/worst timing, workload/memory, and manual JSON slowdown capture. GPU intervals are scoped, not whole-game cost. |

Some lighting, weather-audio, and LOD controls are session-only. Persistence varies by system; not every control autosaves.

The pause menu is separate from the live drawer. It can resume play, return the explorer to the nearest surface, select graphics presets, adjust display resolution or aspect ratio, configure FSR/internal resolution, and adjust worker/chunk settings.

## Saves and preferences

Slots live at `user://worlds/slot_1` through `slot_3`; metadata is `world.json`. The standard manager defaults to a 30-second autosave and records player pose, velocity, flight state, runtime state, and celestial/weather clock data.

The original Terrain3D surface uses `world_state_terrain3d_v1.json` and `surface_terrain3d_edits_v1.json`. Runtime generation uses `world_state_surface_runtime_v1.json` and `surface_runtime_edits_v1.json`. Both remain separate from retained `world_state.json`, SmoothTerrain/construction journals, and voxel journals. Rejected surface state blocks editing and overwrites to preserve existing data.

Persistent settings are grouped by system; some live controls remain session-only.

| Setting group | Factory/load behavior | Persistent path |
| --- | --- | --- |
| Clouds | Factory resource in the editor; user override in exported builds | `res://resources/clouds/gameplay_cloud_defaults.tres` or `user://gameplay_cloud_defaults.tres` |
| Water | Loads the factory resource then the user override | `user://gameplay_water_defaults.tres` |
| Atmosphere | Per-user haze profile and tuning | `user://atmosphere_visual_defaults.tres` |
| Gameplay groups | Per-system overrides | `user://gameplay_{shadow,grass,vegetation,motion,anti_aliasing,render_resolution,surface_terrain}_defaults.tres` |
| Diagnostics | Per-user telemetry settings | `user://ring_performance_telemetry.tres` |

Slot generation metadata is separate from runtime state; the backend marker selects a launch path and never converts old journals. Retained world validation includes terrain definition hashes, scale/circumference compatibility, and cave contract data. A mismatch can preserve data read-only rather than fabricate a conversion.

On Windows, `user://` normally resolves to `%APPDATA%\Godot\app_userdata\RingworldTechDemo\`. Generated captures and logs are mostly under `user://generated`, while some Terrain3D test fixtures use `.godot`; see [Generated data](docs/GENERATED_DATA.md).

## Alternate scenes and tools

Open a scene in the editor and use **F6** to run that scene independently.

| Scene/tool | Purpose |
| --- | --- |
| [Terrain3DSurfacePilot.tscn](scenes/Terrain3DSurfacePilot.tscn) | Isolated Certified Terrain3D surface pilot. |
| [TerrainGenerationPreview.tscn](scenes/TerrainGenerationPreview.tscn) | Isolated reference generator with regional height/zone/detail/slope maps, selectable 2 m detail samples, saved grave-key recipe controls, and **Explore selected patch** navigation. |
| [TerrainGenerationPlayable.tscn](scenes/TerrainGenerationPlayable.tscn) | Curved Terrain3D walking/flying preview with bounded asynchronous preparation, reusable native windows, budgeted uploads and tiled collision, safe handoffs, coordinate travel, live recipe tuning, diagnostics, and saved performance preferences. See the [implementation checklist](docs/terrain3d-runtime-generation-checklist.md). |
| [BiomeAuthoringPreview.tscn](scenes/BiomeAuthoringPreview.tscn) | Rule-based biome preset authoring; world assignment is separate. |
| [ForestRegionPreview.tscn](scenes/ForestRegionPreview.tscn) | Forest-region preview/validation. |
| [Biome Workshop plugin](addons/biome_workshop) | Active rule-based biome-preset editor; world assignment remains separate. |
| [PlayableForest.tscn](scenes/PlayableForest.tscn) | Alternate forest route validation. |
| [TreeAssetPreview.tscn](scenes/TreeAssetPreview.tscn) | Asset inspection. |
| [SunblockerDemo.tscn](scenes/SunblockerDemo.tscn) | Dedicated shadow-caster comparison with separate controls. |
| [GrassMeadowBenchmark.tscn](scenes/GrassMeadowBenchmark.tscn) | Grass-focused benchmark. |
| `VoxelPhase1`–`VoxelPhase5` scenes | Retained legacy voxel development scenes. |

The retained Voxel Phase 5 tools include optional anchored build objects, chunk navigation invalidation with a `NavigationRegion` bridge, revisioned edit commands, and replication snapshots. The retained voxel backend also has dynamic fluid-source propagation, with `F` and `G` placing or removing fluid sources in loaded voxel data. These systems are available as development infrastructure; full multiplayer remains unfinished. Terrain3D does not use the voxel-fluid tooling.

## Build and targeted checks

```powershell
$Godot = 'C:\path\to\Godot_v4.7.2-stable_mono_win64_console.exe'

dotnet restore RingworldTechDemo.csproj
dotnet build RingworldTechDemo.csproj --no-restore

# Configured gameplay Terrain3D smoke
& $Godot --headless --path . --script res://tools/terrain3d_gameplay_smoke.gd

# Isolated pilot, launcher selection, and surface-settings persistence
& $Godot --headless --path . --script res://tools/terrain3d_surface_pilot_smoke.gd
& $Godot --headless --path . --script res://tools/terrain3d_launcher_smoke.gd
& $Godot --headless --path . --script res://tools/surface_terrain_settings_smoke.gd

# Surface data, ring coordinate transforms, and LOD edit propagation
& $Godot --headless --path . --script res://tools/ring_surface_height_field_test.gd
& $Godot --headless --path . --script res://tools/ring_surface_frame_test.gd
& $Godot --headless --path . --script res://tools/ring_surface_lod_bridge_test.gd

# M6 runtime gameplay streaming and persistence
& $Godot --headless --path . --script res://tools/terrain_generation_stream_test.gd
& $Godot --headless --path . --script res://tools/terrain3d_gameplay_stream_test.gd
& $Godot --headless --path . --script res://tools/terrain3d_gameplay_smoke.gd
& $Godot --headless --path . --script res://tools/terrain_generation_world_smoke.gd

# GPU visibility check: run with a working Forward+ GPU, not --headless
& $Godot --path . --script res://tools/terrain3d_ring_visibility_gpu_test.gd
```

Headless checks exercise configuration, data, and scene initialization. Rendering checks require a working Forward+ GPU. For native voxel-mesher validation, pass `--disable-native-voxel-mesher` or `--require-native-voxel-mesher` after Godot's `--` argument separator.

## Limitations and remaining work

- Terrain3D is Certified-only, surface-only, and still needs long-traversal validation.
- Terrain3D caves, construction, full environmental edit propagation, and macro/cloud projectors remain incomplete.
- Terrain3D and retained terrain stores are intentionally separate; old data is not automatically converted.
- Mega Ring cannot use Terrain3D.
- Whole-ring heightmap terrain/wall shadows remain a coarse CPU prototype with known unfinished receiver/GPU/Mega work.
- Development scenes, rollback switches, and isolated tools are retained for verification rather than polished gameplay.
- Wetness is not yet broadly applied to materials, and water does not provide general rigid-body buoyancy.

## Documentation, source, and attribution

| Area | Starting points |
| --- | --- |
| Launch and slots | [start_menu.gd](scripts/start_menu.gd), [world_session.gd](scripts/world_session.gd), [world_size_launcher.gd](scripts/world_size_launcher.gd) |
| Gameplay scene | [VoxelRingIntegrationTest.tscn](scenes/VoxelRingIntegrationTest.tscn) |
| Terrain3D | [terrain3d-surface-port.md](docs/terrain3d-surface-port.md), [scripts/Terrain/Surface](scripts/Terrain/Surface) |
| Runtime generation preview | [generation plan](docs/terrain3d-runtime-generation-plan.md), [implementation checklist](docs/terrain3d-runtime-generation-checklist.md), [scripts/Terrain/Generation](scripts/Terrain/Generation) |
| Terrain rendering | [scripts/Voxel/LOD](scripts/Voxel/LOD), [far-ring plan](docs/far-ring-heightmap-shadow-plan.md) |
| UI/menu | [main-menu checklist](docs/main-menu-space-checklist.md) |
| Water/weather/grass | [water](docs/water-system-implementation-checklist.md), [weather](docs/weather-system-checklist.md), [grass](docs/grass-system-implementation-checklist.md) |
| Generated data | [GENERATED_DATA.md](docs/GENERATED_DATA.md) |
| Assets | [THIRD_PARTY_ASSETS.md](THIRD_PARTY_ASSETS.md) |

Terrain3D provenance is in [third_party/Terrain3D/PROVENANCE.md](third_party/Terrain3D/PROVENANCE.md), with the native addon license at [addons/terrain_3d/LICENSE.txt](addons/terrain_3d/LICENSE.txt). Ocean-wave licensing is at [third_party/godot_ocean_waves/LICENSE](third_party/godot_ocean_waves/LICENSE). Sunshine Clouds 2 is vendored under [addons/SunshineClouds2](addons/SunshineClouds2). Other imported assets and dependencies are listed in [THIRD_PARTY_ASSETS.md](THIRD_PARTY_ASSETS.md).
