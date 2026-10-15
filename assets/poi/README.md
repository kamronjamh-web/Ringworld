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
