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
| Engine | Godot **4.7.2 .NET**. |
| SDK | .NET **8.0** SDK; [RingworldTechDemo.csproj](RingworldTechDemo.csproj) targets `net8.0`. |
| Renderer | Forward+ graphics renderer. |
| Terrain3D native validation | Current native patch validation is Windows x64 only. |
| Native voxel mesher | Optional Windows/Linux packages and C ABI checks; managed fallback remains available. |

## Quick start

1. Open the repository in Godot 4.7.2 .NET.
2. Press **F5** to open [StartMenu.tscn](scenes/StartMenu.tscn).
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
| SmoothTerrain | Retained default for old slots; selectable when supported | Volumetric edits, construction overlay, and optional designed caves. |
| Legacy voxel rollback | Development only | `--legacy-editable-terrain`. |
| Hybrid smooth pilot | Development only | `--hybrid-smooth-terrain-pilot`. |

New Certified worlds select Terrain3D once when the launcher first recognizes Certified dimensions. Existing world metadata without a backend marker retains SmoothTerrain. An explicit player selection remains respected. No old journal or cave conversion is automatic.

Terrain3D renders a moving nine-region window spanning 768 m, with curved collision and a near-to-proxy transition at 84 m. A native culling patch keeps the curved surface visible; that patch has been validated on Windows x64. Environmental integration and broader traversal validation are still in progress; see [limitations and remaining work](#limitations-and-remaining-work).

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

Terrain3D brushes use falloff over the 2 m edit lattice. Edits persist in the surface store and update both the surface and curved collision while the moving 768 m window follows the player. Broader long-distance and high-speed traversal validation remains pending.

The Terrain3D tab exposes brush radius, brush height step, mesh LOD count, and movement speed. Those settings apply live and have their own per-user defaults. The isolated pilot uses the same surface controls, offers diagnostics for the loaded field, and saves edits before returning to its menu.

SmoothTerrain retains volumetric excavation, construction overlays, optional procedural/designed caves, and underground recovery checkpoints. Validation can make incompatible saves read-only to preserve their data.

### Regional LOD, quality, and ring scale

Retained terrain applies sparse edits and schedules terrain LOD work on workers. Terrain3D currently updates its local surface and collision synchronously in the moving window, and its surface field supplies edit snapshots to the surrounding LOD terrain.

Regional LOD terrain, proxy tiers, analytic/global terrain, scaled far presentation, and ring structural edges are configured by [world_size_launcher.gd](scripts/world_size_launcher.gd). Desktop/laptop profiles and graphics presets expose detail choices. The optional native voxel mesher includes compatibility checks and a managed fallback.

Nearby collision/detail terrain renders in ordinary local coordinates. Regional proxy terrain extends the visible landscape, while analytic/global and scaled-far presentation provide horizon continuity. The Certified Ring uses complete scaled far-ring presentation without putting the full physical ring in one normal gameplay-camera coordinate range; Mega uses its specialized local-bubble/far-view path. Static scene values are not necessarily the values used after launcher configuration.

Certified terrain plus finite wall heightmap shadows are a coarse asynchronous CPU whole-ring prototype with stable canonical UVs and persisted controls. Visual placement remains inconclusive; water/wall receiver heights, sparse GPU tiles, and Mega support are unfinished. Local geometry and cloud shadows are separate active systems.

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

Weather fronts share wind and lightning state with local effects. Temperature and weather drive rain or snow; terrain and construction shelter attenuate precipitation and audio. Lightning combines bolts, flashes, cloud illumination, and distance-delayed thunder. Weather audio includes light/heavy rain, wind, distant storms, and rain impacts on soil, hard surfaces, and water. Wetness accumulates and dries as a shared value, with broader material application still pending.

Terrain3D currently lacks macro/cloud projector and atmosphere-material integration. See [water checklist](docs/water-system-implementation-checklist.md) and [weather checklist](docs/weather-system-checklist.md).

## Celestial lighting and shadows

`RingDayNightCycle` runs a 75-minute default ring day and offers 1×–100× live tuning. The current sky uses [ring_local_sky.gdshader](materials/ring_local_sky.gdshader) with [day_side_atmosphere.gd](scripts/day_side_atmosphere.gd): procedural stars, a banded rotating gas giant with storms, phase, solar eclipses, lighting events, and saved time.

Main and far presentation use AgX tonemapping. Day/night, altitude, storms, and eclipses affect atmosphere and lighting. SSAO adds contact shading; directional shadow cascades expose distance, filtering, resolution, and bias controls. Stepped sunlight updates help stabilize shadows. Projected cloud masks are active on supported materials; the coarse terrain/wall shadow prototype remains limited as described above.

The celestial and shadow controls are available from the live drawer alongside diagnostic HUDs. Time, weather clock data, and the ring day state are retained with each world slot.

## Vegetation and grass

Imported LOLIPOP trees include 30 variants, authored LODs, nearby collision, persistent asynchronous tiles, GPU culling, and CPU fallback. Forest regions place Maple, Fir, and Pine species around deterministic clearings and regional rules. Authored mesh LODs and crossed-card far representations carry the forest through distance transitions.

Standing GPU grass uses deterministic placement, wind motion, player bending and recovery, GPU culling, and a CPU fallback. Optional distance LOD is implemented and defaults off. Live controls expose grass density, distance, appearance, wind, and interaction settings.

References: [Biome workshop](docs/biome-workshop.md), [forest-region checklist](docs/forest-region-implementation-checklist.md), [grass checklist](docs/grass-system-implementation-checklist.md), and [Sunblocker demo](docs/sunblocker-demo.md).

## Menus and live tuning

The Start Menu has its own cinematic moving seeded terrain/cloud flyover and separate persistent menu controls. It manages the three local slots.

The World Foundry stores the selected scale, seed, terrain profile, and eligible backend marker with the slot metadata before it creates the gameplay scene. The normal world is configured dynamically; scene inspector defaults alone are not the final runtime world configuration.

The normal grave-key drawer exposes these tabs when their systems are present:

| Tab | Scope |
| --- | --- |
| Lighting, Shadows, Clouds, Quality | Lighting, weather audio, cloud quality, cloud/shadow controls. |
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

Terrain3D uses `world_state_terrain3d_v1.json` and `surface_terrain3d_edits_v1.json`, separate from retained `world_state.json`, SmoothTerrain/construction journals, and voxel journals. Rejected surface state blocks editing and overwrites to preserve existing data.

Persistent settings are grouped by system; some live controls remain session-only.

| Setting group | Factory/load behavior | Persistent path |
| --- | --- | --- |
| Menu flyover | Project resource in the editor; user resource in exported builds | `res://resources/menu/menu_flyover_defaults.tres` or `user://menu_flyover_defaults.tres` |
| Clouds | Factory resource in the editor; user override in exported builds | `res://resources/clouds/gameplay_cloud_defaults.tres` or `user://gameplay_cloud_defaults.tres` |
| Water | Loads the factory resource then the user override | `user://gameplay_water_defaults.tres` |
| Gameplay groups | Per-system overrides | `user://gameplay_{shadow,grass,vegetation,motion,anti_aliasing,render_resolution,surface_terrain}_defaults.tres` |
| Diagnostics | Per-user telemetry settings | `user://ring_performance_telemetry.tres` |

The menu flyover preferences are separate from gameplay cloud/water settings. Slot generation metadata is separate from runtime state; the backend marker selects a launch path and never converts old journals. Retained world validation includes terrain definition hashes, scale/circumference compatibility, and cave contract data. A mismatch can preserve data read-only rather than fabricate a conversion.

On Windows, `user://` normally resolves to `%APPDATA%\Godot\app_userdata\RingworldTechDemo\`. Generated captures and logs are mostly under `user://generated`, while some Terrain3D test fixtures use `.godot`; see [Generated data](docs/GENERATED_DATA.md).

## Alternate scenes and tools

Open a scene in the editor and use **F6** to run that scene independently.

| Scene/tool | Purpose |
| --- | --- |
| [Terrain3DSurfacePilot.tscn](scenes/Terrain3DSurfacePilot.tscn) | Isolated Certified Terrain3D surface pilot. |
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

# GPU visibility check: run with a working Forward+ GPU, not --headless
& $Godot --path . --script res://tools/terrain3d_ring_visibility_gpu_test.gd
```

Headless checks exercise configuration, data, and scene initialization. Rendering checks require a working Forward+ GPU. For native voxel-mesher validation, pass `--disable-native-voxel-mesher` or `--require-native-voxel-mesher` after Godot's `--` argument separator.

## Limitations and remaining work

- Terrain3D is Certified-only, surface-only, and still needs long-traversal validation.
- Terrain3D caves, construction, full environmental edit propagation, macro/cloud projectors, and atmosphere integration remain incomplete.
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
| Terrain rendering | [scripts/Voxel/LOD](scripts/Voxel/LOD), [far-ring plan](docs/far-ring-heightmap-shadow-plan.md) |
| UI/menu | [main-menu checklist](docs/main-menu-space-checklist.md) |
| Water/weather/grass | [water](docs/water-system-implementation-checklist.md), [weather](docs/weather-system-checklist.md), [grass](docs/grass-system-implementation-checklist.md) |
| Generated data | [GENERATED_DATA.md](docs/GENERATED_DATA.md) |
| Assets | [THIRD_PARTY_ASSETS.md](THIRD_PARTY_ASSETS.md) |

Terrain3D provenance is in [third_party/Terrain3D/PROVENANCE.md](third_party/Terrain3D/PROVENANCE.md), with the native addon license at [addons/terrain_3d/LICENSE.txt](addons/terrain_3d/LICENSE.txt). Ocean-wave licensing is at [third_party/godot_ocean_waves/LICENSE](third_party/godot_ocean_waves/LICENSE). Sunshine Clouds 2 is vendored under [addons/SunshineClouds2](addons/SunshineClouds2). Other imported assets and dependencies are listed in [THIRD_PARTY_ASSETS.md](THIRD_PARTY_ASSETS.md).
