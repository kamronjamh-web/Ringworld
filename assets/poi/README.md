# POI assets: beam emitter, reactor, main spire, main reactor

Blender-authored points of interest, imported into Godot and prepared for runtime. Editable sources
(`.blend`, build scripts, validation notes) stay in `prototypes/Ringworld_*`; Godot ignores that
folder (`prototypes/.gdignore`). Do not edit the `.blend` files from here. These assets are not placed
in the ring world; placement is handled elsewhere.

## Layout

```
assets/poi/
  beam_emitter/
    Surface_Spire_and_Facility_Godot.glb   combined spire + facility (original import, reference)
    Surface_Beam_Spire_Godot.glb           spire only            (same pivot/scale, do not stack with the combined one)
    Surface_Base_Facility_Godot.glb        facility only         (same pivot/scale)
    BeamEmitterPOI.tscn                    RUNTIME WRAPPER: merged geometry, collision, far proxy, markers
    merged/                                generated: BeamEmitterMerged.tscn, meshes/*.res, materials/*.tres
    collision/                             generated: trimesh floors + convex feet
  reactor/
    Reactor_Architecture_Godot.glb         assembled reactor (original import, reference)
    modules/*.glb                          five reusable assemblies (imported, not used by the wrapper)
    ReactorPOI.tscn                        RUNTIME WRAPPER: merged geometry, collision, markers
    merged/, collision/                    generated, same layout as above
  main_spire/
    Main_Spire_Outer_Ring_Godot.glb        4 km, 16-arm main spire + facility + entrance hall (original import, reference)
    MainSpirePOI.tscn                      RUNTIME WRAPPER: merged geometry, collision, far proxy, markers
    merged/, collision/                    generated, same layout as above
  main_reactor/
    Main_Reactor_Architecture.glb          assembled main reactor, 2x the regular one (original import, reference)
    MainReactorPOI.tscn                    RUNTIME WRAPPER: merged geometry, collision, markers
    merged/, collision/                    generated
scenes/poi/EmitterReactorTest.tscn         standalone test scene, regular pair + main pair (not the ring)
scripts/poi/                               test scene + free-fly camera scripts
tools/poi/                                 import/merge/build/verify tools (see "Regenerating")
```

The `.glb.import` files use scale 1, mesh LOD generation on, shadow meshes on, no generated collision,
embedded materials, root type Node3D, animation import off and light baking disabled.

## Pivots and axes

- Units are metres. Blender `(X, Y, Z)` becomes Godot `(X, Z, -Y)`; keep instance scale 1.
- **BeamEmitterPOI**: origin is the ground centre of the spire (bore axis). Ground/floor level is Y 0,
  gallery floor Y 16, facility roof max Y 30, spire tips Y 1800, floor slab bottom Y -1. The 150 m beam
  bore (radius 75 m) is clear; nothing, including the far proxy (minimum 75.5 m), enters it.
  Entry portal axis is Blender 292.5 degrees, which is the +Z side in Godot.
- **ReactorPOI**: origin is the chamber centre. Intended placement is 4,000 m below the local surface
  datum (`position.y = -4000`). Exhaust top is local Y +1039 (world -2961). The arrival side (Blender -Y)
  is Godot +Z.
- Marker3D nodes (under `Markers`):
  - `BeamEmitterPOI`: `BeamOrigin` (0,0,0 bore centre at ground datum), `ShaftBottom` (0,-1,0, lowest point of
    the facility/throat where the connecting shaft attaches), `BeamCrownFocus` (0,1743,0 crown aperture),
    `EntryApproach` (outside the entry portal).
  - `ReactorPOI`: `ChamberCenter`, `ExhaustTop` (0,1039,0; align it with the emitter's `ShaftBottom` axis),
    `ArrivalOverlook`.
- `BeamEmitterPOI/PlaceholderBeam` is a hidden emissive cylinder (radius 70 m) for look-dev only.

## Draw-call reduction

The imported combined spire is 937 mesh instances / 1,220 surfaces. `merge_poi_scenes.gd` bakes them into
one surface per (group, material, azimuth sector), with an importer-style LOD chain per mesh:

- Emitter: groups `Spire`, `Facility`, `Roof` (roof = every `*Removable*` object, so it can be hidden for a
  cutaway), 8 azimuth sectors centred on the spire arms so frustum and shadow culling still work.
  126 mesh instances / 126 surfaces, identical triangle count.
- Reactor: groups `Core`, `Routes`, `Exhaust`, no sectors. 13 mesh instances. The 9 "Scale figure" meshes
  (human reference) are dropped.
- Materials are extracted to `merged/materials/*.tres`. Emissive materials that were below the default glow
  threshold (HDR threshold 1.0) get their energy raised so the project's glow picks them up
  (`EMISSION_GLOW_TARGET` in the merge script).

Visibility ranges on `BeamEmitterPOI` (metres): spire detail ends 12,500 (1,000 fade margin), `FarProxy`
(352 triangles, 8 lofted arm columns from the real arm cross-sections) begins 11,500; facility and roof
detail end 6,000.

## Collision scope (BeamEmitterPOI)

Walkable surfaces only, taken from the named floor meshes of the export (not decorations):
ground floors (entry throat and apron, ring floor, room connection and foundation floors, lower ramp
access and landing, ground throat rim) as one trimesh, gallery floor + ramp segments + upper access and
landing as a second trimesh, and one convex hull per arm foot (8). There are no walls, parapets, ceilings
or doorway collision; a controller can step off the paths. ReactorPOI: one trimesh of the terraces,
causeways, docking/arrival tongues and levitating landings (no undersides, glyphs or wayfinding marks).

`verify_emitter_collision.gd` casts rays at all 1,385 route samples from `facility_route_samples.json`
(floor within 0.12 m) and at the radius-75 m bore (must be empty). Last run: 0 misses, 0 bore hits.

## Main spire and main reactor

Sources: `prototypes/Ringworld_Main_Spire_4km_Outer_Ring/` and `prototypes/Ringworld_Main_Reactor/` (read their
README / IMPORT_NOTES / validation). Same pipeline and `.import` settings as the regular assets; the same
merge, build and verify tools handle them (`main_spire`, `main_reactor` arguments).

- **MainSpirePOI**: origin is the ground centre of the spire (bore axis), Y 0 = ground, foundation slab to Y -2,
  tips Y 4000, footprint 1,642.78 m. 8 inner arms at Blender 0..315 deg (step 45), 8 outer arms at 22.5 + 45k deg.
  The 300 m beam bore (R 150) is clear: closest source triangle R 150.08; the far proxy retains the source inner crown's
  64 segments so its triangle interiors also remain outside R 150.
  The entrance is outer arm 07 at Blender 292.5 deg = Godot XZ 67.5 deg (the +Z/+X side); a 20 x 20 m hall
  (R 444..890) leads to the facility. Markers: `BeamOrigin` (0,0,0), `ShaftBottom` (0,-2,0),
  `BeamCrownFocus` (0,3989,0, centre of the outer open crown ring), `EntryApproach` (R 930 on the entry axis).
  `PlaceholderBeam` is a hidden r 140 m cylinder.
- **MainReactorPOI**: origin is the chamber centre, intended placement `position.y = -4000`. Manufactured bounds Y -720..+2078,
  exhaust clear radius >= 150 m (measured 152.01) from local +600 to the roof interface at +2078 (world -1,922).
  Markers: `ChamberCenter`, `ExhaustTop` (0,2078,0; align with the spire's `ShaftBottom` axis), `ArrivalOverlook` (0,160,2770).

Draw-call reduction (`merge_poi_scenes.gd`, one surface per group / material / azimuth sector):

| | source | merged |
|---|---|---|
| main spire | 2,820 mesh instances / 3,615 surfaces / 106,204 tris | 312 mesh instances / 312 surfaces / 106,204 tris |
| main reactor | 827 mesh instances / 827 surfaces / 44,626 tris | 15 mesh instances / 15 surfaces / 44,626 tris |

- Spire groups come from the Blender collection: `Roof` = 11, 24, 35 (every removable roof), `Facility` = 10, 12, 22, 23,
  32, 34 (facility, outer foundations, entrance hall), `Spire` = 00, 20, 21, 25 (six accelerator stages), 31, 33 (outer rings), 36.
  16 azimuth sectors centred on the 16 arms (`sectors=N` on the merge command line overrides it).
  The sector count was chosen by measuring the same ground-level views (A1000, merged, 1600x900, shadows on) with N = 1 / 4 / 8 / 16 / 32:

  | sectors | mesh instances | bookmark 1 (ground entry): draw calls / primitives / frame ms |
  |---|---|---|
  | 1 | 23 | 114 / 534k / 5.27 |
  | 4 | 84 | 302 / 419k / 5.32 |
  | 8 | 168 | 557 / 386k / 5.19 |
  | 16 | 312 | 932 / 355k / 4.93 |
  | 32 | 605 | 1,729 / 344k / 4.50 |

  Primitives (which include the shadow cascades) stop falling meaningfully past 16 sectors (-3% for 2x the draw calls) and 16 matches the arm layout;
  32 is a little faster on this GPU, so it is the option if draw calls turn out to be cheap. Height bands inside the tall `Spire` sectors were not tried.
- Reactor groups `Core`, `Routes`, `Exhaust`, no sectors (same rule as the regular reactor).
- Emissive materials: `Reactor_Teal_Inlay` (peak 1.0, energy 1.785) and the reactor's teal / seam materials (energy 1.68 / 2.4) are already above the
  1.0 glow threshold; the spire's `Facility_Soft_Teal_Guide` and `Facility_Control_Surface` get the same `EMISSION_GLOW_TARGET` boost as the regular emitter.

Visibility ranges on `MainSpirePOI` (metres), the regular emitter's scaled up: spire detail ends 28,000 (2,000 fade margin; 12,500 x the 2.2 height ratio),
`FarProxy` begins 26,000 (= end - margin, as for the regular emitter); facility and roof detail end 12,000 (1,000 margin; 6,000 x 2 for the ~2x footprint).
`FarProxy` is 8,640 triangles in one surface/material: the existing 16 lofted arm columns (8 inner 8-slice, 8 outer 9-slice; the entrance arm starts at Y 26
because its monolith starts at Y 24), six continuous outer connector annuli, 48 radial ties, 48 broken accelerator collector sectors, 48 arm-feed clamps,
and the inner and outer open crown rings. The ties, collectors, and clamps join the outer annuli back to the inner arms. All geometry comes from the source
meshes in root space. Annuli use 32 angular segments (source 64), collectors two spans (source four, retaining the original 32-degree sector and gaps),
and the outer crown 64 segments (source 128); radial/height profiles and cap faces are retained. The inner crown keeps all 64 source segments for bore clearance.
Imported compression noise is welded with bounded radial/height/angular tolerances (0.075 m / 0.002 m / 0.0001 rad), averaging each source profile/row.
`MainReactorPOI` has no visibility ranges (it is only seen from inside the cavern).

Collision: same rule as the regular emitter (named walkable floors only, no walls/ceilings): ground floors + the outer entrance hall floor and its under-apron
join as one trimesh (14 meshes, 1,608 tris), gallery + ramps as a second (39 meshes, 1,232 tris), and one convex hull per foot (8 inner anchors + 9 outer
foundations, the entrance arm having two split foundations). `verify_emitter_collision.gd -- main_spire` casts rays at all 1,671 samples of
`validation/route_samples.json` (11 routes) and at the R 149.5 bore at heights 0.5..31 m: 0 misses, 0 bore hits (9 exact edge rays recovered by the 5 cm retry, max |dy| 0.019 m).
`MainReactorPOI`: one trimesh of the terraces, causeways, docking/arrival tongues and landings (35 meshes, 3,172 tris), same filter as the regular reactor.

Measured on the RTX A1000 laptop GPU, windowed 1600x900, vsync off, 200 frames after 40 warm-up (`--main`, merged vs `--original`).
Frame ms / render CPU ms / render GPU ms / draw calls:

| bookmark | merged | original GLBs |
|---|---|---|
| 1 ground entry | 4.48 / 0.87 / 3.23 / 932 | 8.92 / 6.19 / 2.74 / 7,792 |
| 2 inner gallery | 5.85 / 1.25 / 4.46 / 1,217 | 6.71 / 4.75 / 3.76 / 6,364 |
| 3 40 km | 2.40 / 0.39 / 1.42 / 22 | 9.52 / 6.40 / 1.62 / 4,208 |
| 4 reactor overlook | 3.33 / 0.30 / 2.26 / 45 | 3.14 / 1.29 / 2.02 / 822 |
| 5 shaft top | 4.76 / 0.85 / 3.49 / 676 | 4.55 / 2.75 / 3.33 / 4,861 |

The merged version trades CPU for slightly more GPU work at close range (coarser culling and shadow casting of 4 km tall sector meshes); it wins clearly wherever
the original is CPU bound. The regular pair on the same machine for reference, merged: 4.05 / 4.05 / 2.23 / 3.03 / 3.66 ms at bookmarks 1-5.
Load / instantiate (headless): wrapper MainSpirePOI 190-245 ms / 4 ms (344 nodes) vs the GLB 83-90 ms / 13-15 ms (2,843 nodes);
MainReactorPOI 20 ms / 1.6 ms vs the GLB 18-20 ms / 3.8 ms.

## Test scene

`scenes/poi/EmitterReactorTest.tscn` holds two pairs under `Regular` and `Main`; only one is visible at a time (key M).
Both have ground at Y -0.05 (so facility floor tops do not z-fight) with a hole (R 75 regular, R 150 main), the spire at the origin,
the reactor at (0,-4000,0), a shaft-liner placeholder (open tube, r 76 m regular; r 151 m main, from the exhaust top at -1,922 m to the spire's `ShaftBottom`),
and reactor-only omni lights (the reactor is on render layer 2 so the surface sun never lights it). Shared: sun (shadow distance 700 m regular, 2,000 m main),
sky environment, and a dark environment applied to the camera below Y -30.
Free-fly camera: hold right mouse to look, WASD/QE move, Shift x10, Ctrl x0.1, wheel scales speed.
Keys 1-5 jump to the bookmarks of the active pair, M toggles regular / main, B toggles the placeholder beam, O toggles the original unmerged imports.
Regular bookmarks: ground entry, gallery, 20 km, reactor overlook, shaft top. Main bookmarks: ground entry (R 960 on the entry axis), inner gallery (R 224, Y 33.7),
40 km, reactor overlook (arrival tongue, 2,770 m from the axis), shaft top. Command line after `--`:
`--main --bookmark=N --original --warmup=F --measure=F --capture=PATH`; add `--disable-vsync` to the engine arguments to measure frame times.

## Regenerating

After changing a `.glb` (re-copy from `prototypes/`) let Godot import it, then run, from the repo root
with the pinned engine:

```
godot --headless --path . --import
godot --headless --path . --script res://tools/poi/merge_poi_scenes.gd [-- emitter|reactor|main_spire|main_reactor|all [sectors=N]]
godot --headless --path . --script res://tools/poi/build_poi_scenes.gd [-- emitter|reactor|main_spire|main_spire_proxy|main_reactor|test|all]
godot --headless --path . --script res://tools/poi/verify_emitter_collision.gd [-- main_spire]
```

`inspect_glb.gd` / `inspect_materials.gd` print mesh/surface/triangle/material stats for any scene.
Floor mesh name patterns for collision live at the top of `build_poi_scenes.gd`; the group classifiers
live in `merge_poi_scenes.gd`. Both tools overwrite their outputs.

For a main-spire far-proxy-only update, use `build_poi_scenes.gd -- main_spire_proxy`. This writes only
`assets/poi/main_spire/merged/meshes/MainSpire_FarProxy.res`; wrappers, merged detail, collision, and the regular emitter proxy are untouched.
The main proxy build fails before saving if an expected source mesh is missing, duplicated, or invalid.

Notes: a merge run deletes all `merged/meshes/*.res` of that POI (including the far proxy), so re-run the build step afterwards. Godot writes new `.glb.import`
files with default settings (root type empty, light baking on, animation import on); the POI ones were edited to the settings listed at the top before the first
real import. A headless `--import` may delete the tracked `addons/terrain_3d/bin/~libterrain.ringworld.windows.debug.x86_64.dll`; restore it with `git show HEAD:<path> > <path>`.

## Atmospheric haze (POI ShaderMaterial)

All POI architecture materials (33 merged emitter / spire / reactor / main_reactor `.tres` and the 23 Weather Lung `built/materials`) are `ShaderMaterial`s on
`materials/ring_poi_architecture.gdshader` (cull back, Weather Lung) or `ring_poi_architecture_double_sided.gdshader` (cull disabled, merged). The shader
evaluates `ring_haze_sample` per fragment (POI triangles span km), `ALBEDO * T`, `SPECULAR * sqrt(mean T)`, `EMISSION * energy * T + inscatter`. Uniforms mirror
StandardMaterial3D: `albedo_color`, `metallic_value`, `roughness_value`, `specular_value`, `emission_color`, `emission_energy`. With no ray frame (standalone test scenes) the haze is identity, and
without `poi_ring_lighting_valid` the direct light is plain Lambert + GGX with no night tint, so those scenes look like the old StandardMaterial3D to within a few levels (Lambert versus Burley diffuse).

- Haze and cloud shadows are not settings: POI haze is the regular ring haze (it follows `ring_atmosphere_enabled`, the haze model and every haze control), and POI cloud shadows follow
  the terrain's cloud-shadow projector (every POI-shader material is registered with `RingCloudShadowProjector`).
- Local daylight (`materials/ring_poi_lighting.gdshaderinc`, always on): the sun DirectionalLight runs at full day energy across the whole ring, so terrain and trees fade the primary sun with
  a per-fragment local day factor in their own `light()`. POI architecture does the same: `light()` scales the primary sun by `mix(night_direct, day_direct, poi_day)`, composes the shadow-map
  attenuation, the projected cloud shadow and the S6 contact shadow, and adds a ported GGX specular; the albedo gets the ring night tint and the ambient the cloud AO (`1 - 0.72 * cloud * day`).
  `ring_emitter_standin.gdshader` has the same `light()` gate.
- Base ambient occlusion: per-mesh instance uniforms `poi_base_local` (ground anchor in the mesh's own space) and `poi_base_ao_height` (about 12% of the POI height, 6-40 m; 0 = off) darken the
  ambient to 45% and the sun to 75% at the ground contact, fading out over that height. Set at spawn by the streamers through `PoiBaseAmbientOcclusion`.
- Runtime feed: `RingPoiHaze` (scripts/Atmosphere) registers POI-shader materials (`RegisterTree` from the streamers, `RegisterMaterial` for the stand-in and bore) and
  publishes the camera ray frame and the lighting data (`poi_ring_center_world`, `poi_lighting_params`, `poi_night_tint`, `poi_ring_lighting_valid`, from `VoxelChunkGridManager`) from `_Process`
  (per-field gated, write counter `PoiAtmosphere`). The stand-in gets the same values from `EmitterStandInLayer`. There are no grave-key menu entries for any of this.
- Do not hand-edit the `.tres`: the build tools write them through `tools/poi/poi_material_util.gd` (`to_shader_material`, `strip_runtime_params`). Only the six surface
  parameters are stored; atmosphere uniforms are runtime-owned. To (re)convert existing StandardMaterial3D files in place (idempotent, expects 56):
  `godot --headless --path . --script res://tools/poi/convert_poi_materials.gd`.
- Reading material values from C#: `PoiMaterialParams.TryGetSurface` / `SetEmissionEnergy` (works for both material kinds; shader defaults are used for unset parameters). The rig writes the
  nozzle / heat glow through `emission_energy`.
- Checks: `tools/poi_haze_gpu_test.gd` (windowed, `-- --model=physical` too), `tools/poi_haze_registry_test.gd` (headless). A/B captures: `emitter_capture_route.gd` and
  `weather_lung_world_bind_test.gd` take `--poi-haze=on|off --haze-model=legacy|physical --poi-cloud-shadow=on|off --haze=on|off --settle=N`; the certified launcher preset can leave the master haze off, so the tools force it on.

## Weather Lung

The Weather Lung is the breathing weather station of the Central Station kit (`prototypes/Weather_Lung_Central_Station_Kit`, regenerated to round 3): a 28 x 18 km
basin with an 8 km hood arch, five intake vanes with rams, a two-rotor turbine throat, two guide rings, sixteen gills, twenty-four heliostats and a basin-floor heat
grid. It exists only in **new v8 worlds** (`ringworld_surface_weather_lung_v8`, launcher toggle "Weather Lung (v8)", default on, needs the hill-country landforms toggle).
Terrain carving (basin, pad, forecourt) is a deterministic generator operator in `scripts/Terrain/Sites/WeatherLung*.cs`; the ring world's own water fills the basin
because the datum (asset local Y 0) sits at sea level + 135 m, so the waterline (asset Y -135) is sea level. The kit's water GLB is not imported.

### Layout

```
assets/poi/weather_lung/
  source/                 31 GLBs + .import (sectors/, chunks/, modules/, Weather_Lung_Far_Proxy.glb) and weather_lung_manifest.json (copy of docs/asset_manifest.json)
  built/materials/*.tres  one shared material per name (23); Heat_Exchanger_Glow x1 and Nozzle_Glow_00..11 are emissive, energy 0, driven by the controller
  built/meshes/WL_*.res   structure, dressing tiles, moving pivots, far proxy
  built/sectors/WeatherLungSector_XX.tscn   Structure + Dressing/Tile_<i>_<j>
  built/arch/WeatherLungPiece_*.tscn        Arch pieces: Structure, Tiles_0..3 (dressing), Moving, and one collision body each (Arch_0..4, Sector_XX)
  collision/*.res         ConcavePolygonShape3D of the Structure faces (12 sectors + 5 Arch parts of about 5,000 faces)
  WeatherLungDetail.tscn  Sectors/ (12 sector scenes), Arch/ (Structure, Dressing/Tiles_k, Moving pieces), Collision/ (17 body pieces)
  WeatherLungPOI.tscn     WeatherLungController root, FarProxy, Markers, empty DetailMount
scenes/poi/WeatherLungTest.tscn   standalone look-dev / perf scene (scripts/poi/weather_lung_test.gd)
```

### Composition (no duplicate geometry)

- **Static:** 12 x (`Sector_XX_Structure`, `Sector_XX_Dressing`) at the manifest `origin_godot_m`, plus the five chunk static batches (the `<Chunk>_Structure` / `_Dressing` nodes only) under `Arch` at (-10500, 340, 0).
- **Moving:** the whole `modules/Weather_Lung_Intake_Vanes.glb` under `Arch/Moving`. The chunk GLBs duplicate the moving parts, so they are not used for them. Each pivot's direct meshes are merged into one mesh (one surface per material) in pivot space.
- **Far proxy:** `Weather_Lung_Far_Proxy.glb` (19,212 tris).
- **Not imported:** the combined sector GLBs, the Assembled GLB, the water GLB, the Animated GLB (the rig check reads it straight from `prototypes/`) and the `.blend` files.
- Triangle totals reconcile with the manifest: structure 73,688 (53,588 sectors + 20,100 chunks), dressing 33,164 (22,320 sectors + 9,524 chunk statics + 1,320 ram parts that live inside the moving pivots), moving 8,188.

### Ranges (HLOD) and streaming

- `WeatherLungPOI.tscn` is small and resident once the world binds. `WeatherLungDetail.tscn` is threaded-loaded when the observer is within 36 km (never below detail range + 4 km), and released after the observer has stayed beyond 44 km for 2 s.
- **Sliced attach.** Instantiating the whole Detail scene took 50-80 ms on the main thread (the 17 trimesh bodies alone about 40 ms: creating a `ConcavePolygonShape3D` body registers its mesh shape with the physics server). The Detail scene is therefore a shell that instances 35 piece scenes (12 sectors, Arch structure, 4 dressing groups, moving rig, 17 collision bodies), all loaded by the same threaded request. `WeatherLungStreamer` reads the loaded PackedScene state and assembles it hidden, one piece per step within a per-frame budget (`DetailAttachBudgetMs`, grave-key menu, default 4 ms, 1 to 16), then five controller stages (bind pivots, bind materials, cache tiles, apply ranges, pose) and the reveal. The FarProxy is held visible at every distance (range begin 0) until the last step, then gets its range back and Detail becomes visible; collision gating and the rig only start after that. A disable or unload mid-attach frees the partial scene and restores the proxy range. In a real v8 world the attach takes 10-11 frames with a worst frame of 5.7-6.8 ms (a step can overshoot the budget by its own cost, at most about 4 ms).
- **HLOD:** the controller sets `Detail.visibility_parent` to `FarProxy` (begin 28 km, margin 2 km, fade self, no shadow, end 700 km), so Detail shows only while the proxy is hidden, measured from one point.
- **Structure and Moving have no range end.** This deliberately deviates from the manifest's moving 0-8 km: each gill plate replaces a hood armor plate, so hiding "moving" geometry at 8 km would open 16 holes, and the 380 m heliostat mirrors and 3-8 km rotors would pop. Animation is throttled instead (every frame within 8 km, 10 Hz to 28 km, nothing beyond or while Detail is hidden).
- **Dressing** is re-binned at build time into 1 km POI-local tiles (one mesh per sector and tile, 404 tiles) with range end 2 km, margin 250 m. Godot measures visibility range to the instance AABB centre, so a sector-sized mesh would never satisfy a 2 km range near its edges.
- **Collision:** structure only (walls and floors), one body per sector plus Arch, `ProcessMode` disabled until the observer is within 3 km of the body bounds (off again 500 m farther). No collision on moving parts or dressing. The galleries are not traversal-certified.

### Rig metadata (`wl_*`, written by `build_weather_lung.gd`)

Every pivot under `Arch/Moving` carries `wl_kind` (vane, ramcyl, ramrod, rotor, ring, gill, heliostat), `wl_index`, `wl_axis` (Godot space), `wl_rest_angle` (signed, about the axis; the rod has `wl_rest_x`),
`wl_tris_moving` and `wl_tris_dressing`. Heliostats add `wl_track_angle`, `wl_row`, `wl_col`; RamCyl adds `wl_pin`, `wl_base`, `wl_vane_pos`, `wl_l0`. Axes: vane and RamCyl (0,0,-1);
rings normalize(4475,-2300,0); rotors and gills from the GLB extras `spin_axis_blender` / `hinge_axis_blender` converted (x, z, -y); heliostats from the rest quaternion. The extras are read from the GLB JSON chunk,
not from importer metadata. `WeatherLungRig` writes ABSOLUTE poses (see the rest-pose finding below).

### Findings that differ from the kit README

- **Rest pose.** The static GLBs are NOT the closed phase-0 pose and the heliostats are exported tracking, not stowed. The rest pose is breath phase -1/240 (the frame before frame 1) with ripple lag 0.012: vane rest angles 0 to 0.016 rad, guide ring A -1.5 deg, rotor A about -4.5 deg about its stored axis, heliostats at their tracking angle. The controller therefore never treats the GLB pose as zero.
- **Animated GLB.** The baked tracks run 301 keys at 30 keys/s over 10 s, stepped from the 24 fps timeline (frame 1 = t 0; the first two keys hold 0 instead of wrapping to phase -1/240). The fitted offset is exactly -1 frame with lag 0.012, and there are no heliostat tracks (they follow `sun_available`).
- **Ram kinematics (corrects the integration plan, K8).** The pin A is the `Vane_ram_pin_XX` AABB centre in vane-pivot space and B the RamCyl translation. With the vane rotating about -Z by th, rel = P + R(-Z, th) A - B; the cylinder rotates about -Z by atan2(-rel.y, rel.x), and the rod extends by the **in-plane** length |rel_xy| - L0 (L0 = the in-plane length at th = 0, per ram). A 3D length is 0.11 m off the bake; the in-plane one is within 0.35 mm.
- **INDEPENDENT_QA.md** in the kit predates rounds 2-3 (it reports 24,872 triangles, -95 m foundations and a 7,332-triangle proxy; the current kit has 115,040, -870 m and 19,212). `verify_weather_lung.gd` is the validation of the current exports.

### Placement (revised: any across, ocean platforms allowed)

A deterministic scored 2D search over the station gaps around the main row (several along-gap offsets x several across positions over the usable ring width) picks the site; candidates overlapping an emitter site, a Wall Engine band or the main spire knot links are rejected, and the lowest score wins.
Seed 1337: station 81, **around 25,820,550 m, across 165,400 m, datum 135 m, yaw 180 deg**, a Coast site with a quay and a plinth of at most 285 m, 257 km from the main spire (fresh worlds spawn 40 km from the main spire, so 217 km from the station).
Yaw 180 puts the hood (asset -X) prograde, away from the main spire, so visitors arrive over the dam end looking into the intake. Asset local +X maps to (around, across) = (cos yaw, sin yaw) and asset local +Z (Godot) to (sin yaw, -cos yaw); the root transform is
`EmitterSiteFrameContext.SiteTransform(Around, Across, PadHeight + 0.05, CosYaw, SinYaw)`, recomputed absolutely on every floating-origin change. Terrain at the pad is PadHeight - du^2/2R so the flat POI tangent plane meets the curved ring.

### Runtime

`WeatherLungStreamer` (node `WeatherLung` in `VoxelRingIntegrationTest.tscn`) binds when `TerrainSampler.WeatherLung` is non-null (v8); otherwise it is Unavailable and does nothing. `WeatherLungController` (root of the POI scene) owns the breath phase
(the saved weather clock, `RingVolumetricClouds.CloudSimulationTimeSeconds`, when bound), the sun state (`RingAtmosphereModel.LocalHazeDay`), the rig and the shared nozzle / heat materials. `WeatherLungBreathMath` holds the kit formulas exactly
(vane `rad(amp)(0.5 - 0.5 cos 2pi(p - lag i))`, gill `sin^2` window of 0.38 cycle from 0.5 + lag x column, turbine `2pi revs (p + 0.5 (1 - cos 2pi p)/2pi)` with revs = max(1, round(rpm/1.5 x cycle/60)), ring `2pi p`, nozzle and heat grid glows, heliostat `pi + (th1 - pi) smoothstep(sun)`).
`IWeatherLungWeatherSource` is the data-only hook for a future cloud plume (the SunshineClouds-side constraints are documented there). All settings are in the grave-key menu (Surface terrain tab, "Weather Lung") and persist in `user://gameplay_weather_lung_defaults.tres`; the section includes teleports (dam crest, intake overlook, far 40 km) to the actual site and a status line with the placement.

### Test scene

`scenes/poi/WeatherLungTest.tscn`: keys 1-6 bookmarks (dam crest, basin under the hood, turbine throat, gill close-up, 12 km, 40 km handoff), N sun mode (Auto/Day/Night), T time x1/x10/x60, F freeze, `[` `]` scrub phase, H dressing, C collision debug lines, W test-only water plane (`TestWaterPlaceholder`).
Command line after `--`: `--bookmark=N --warmup=F --measure=F --capture=PATH --sun=day|night --phase=P`. The bookmark framings are provisional; tune them against captures.

### Regenerating and verifying

From the repo root with the pinned engine (never run Godot while the editor is open; a new GLB copied into an open editor is imported immediately with default settings):

```
godot --headless --path . --import
godot --headless --path . --script res://tools/poi/build_weather_lung.gd [-- all|detail|proxy|collision|test]
godot --headless --path . --script res://tools/poi/verify_weather_lung.gd           # triangle totals, 59 pivots, materials, ranges, collision; WEATHER_LUNG_VERIFY=PASS
godot --headless --path . --script res://tools/poi/verify_weather_lung.gd -- rig    # C# rig vs the kit baked animation; WEATHER_LUNG_RIG=PASS
godot --headless --path . --script res://tools/weather_lung_breath_math_test.gd
godot --headless --path . --script res://tools/weather_lung_streamer_test.gd
godot --headless --path . --script res://tools/weather_lung_settings_smoke.gd
godot --headless --path . --script res://tools/weather_lung_world_bind_test.gd      # real v8 world: bind, placement, dam crest teleport, detail attach, animation
```

`build_weather_lung.gd -- all` also attaches `scripts/poi/WeatherLung/WeatherLungController.cs` to the POI root when it exists and the C# project is built (run `dotnet build RingworldTechDemo.csproj -p:NuGetAudit=false -v:minimal` first). Detail and the `WL_*` meshes are regenerated by every `detail` / `all` run; do not hand-edit them.
`verify_weather_lung.gd` escalates (prints ESCALATE) above 450 dressing tiles, above 15 ms warm Detail instantiate and above 40 ms cold. Latest results: 12 sector origins within 1e-3 m; structure 73,688, dressing 33,164, moving 8,188; 59 pivots; 23 materials with one instance each; 17 collision bodies with 73,688 faces;
Detail 607 nodes, 404 tiles, about 3 ms warm / 30 ms cold when instantiated in one go (verify only; the runtime attaches it in slices); rig check max error 0.0007 to 0.001 rad per channel and 0.013 m for the rods at a -1 frame fit (the bake's own 30 keys/s quantisation).
