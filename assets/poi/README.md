# POI assets: beam emitter and reactor

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
scenes/poi/EmitterReactorTest.tscn         standalone test scene (not the ring)
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

## Test scene

`scenes/poi/EmitterReactorTest.tscn`: ground at Y -0.05 (so facility floor tops do not z-fight) with a
150 m hole, emitter at origin, reactor at (0,-4000,0), shaft-liner placeholder (open tube, r 76 m), sun +
sky environment, a dark environment applied to the camera below Y -30 and reactor-only omni lights (the
reactor is on render layer 2 so the surface sun never lights it).
Free-fly camera: hold right mouse to look, WASD/QE move, Shift x10, Ctrl x0.1, wheel scales speed.
Keys 1-5 jump to bookmarks (ground entry, gallery, 20 km, reactor overlook, shaft top), B toggles the
placeholder beam, O toggles the original unmerged imports. Command line after `--`:
`--bookmark=N --original --warmup=F --measure=F --capture=PATH`.

## Regenerating

After changing a `.glb` (re-copy from `prototypes/`) let Godot import it, then run, from the repo root
with the pinned engine:

```
godot --headless --path . --import
godot --headless --path . --script res://tools/poi/merge_poi_scenes.gd [-- emitter|reactor|all]
godot --headless --path . --script res://tools/poi/build_poi_scenes.gd
godot --headless --path . --script res://tools/poi/verify_emitter_collision.gd
```

`inspect_glb.gd` / `inspect_materials.gd` print mesh/surface/triangle/material stats for any scene.
Floor mesh name patterns for collision live at the top of `build_poi_scenes.gd`; the group classifiers
live in `merge_poi_scenes.gd`. Both tools overwrite their outputs.
