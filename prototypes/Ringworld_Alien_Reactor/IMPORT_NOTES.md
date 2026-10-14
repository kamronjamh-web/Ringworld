# Ringworld — Alien Tripod Reactor

Original, editable alien-megastructure blockout authored in Blender 4.3.2. This is the revised design: sweeping monolithic supports, separated containment petals around a suspended faceted heart, and a sculptural split exhaust throat. It is a design/scale starting point, not a production-ready level.

## Design

A natural opening in warm, fractured rock reveals a functioning power cathedral. Three vast tapered blades connect the reactor to embedded alien stelae. Deliberate negative space separates the core, containment segments, exhaust pylons, and partial collars. Thin recessed teal/white energy traces indicate a live reactor without turning every edge into neon.

Three service levels and narrow hovering causeways give the space a route structure. The lower secondary route is broken, while the main entry reaches a connected vestibule on the core terrace. An approximately 1.8 m explorer supplies the human-scale reference. The cave is deliberately raw and irregular; the machinery uses precise faceted ceramic/alloy forms.

## Contents

- `Ringworld_Tripod_Reactor_Blockout.blend`: authoritative editable source. Named component objects, local materials, reference cavern, preview lighting, atmosphere, four cameras, and embedded build scripts.
- `Reactor_Architecture_Godot.glb`: complete assembled alien architecture, preserving component nodes. Excludes cave, atmospheric volume, cameras, and preview lights.
- `modules/`: five reusable architecture assemblies and a pivot manifest.
- `preview_01_entrance_reveal.png`: wide entrance view.
- `preview_02_architecture_cutaway.png`: layout with reference geology hidden.
- `preview_03_reactor_detail.png`: closer look at the containment structure.
- `build_alien.py`, `geometry_helpers.py`: regenerate the revised architecture inside the supplied source scene, retaining its reference cavern and preview rig.
- `render_alien.py`, `export_modules.py`: refresh previews and reusable assemblies.
- `VALIDATION.md`, `glb_validation.json`, `build_stats.json`: measured counts, checks, and verification limits.

There are no external textures, linked asset libraries, or installed add-on dependencies.

## Dimensions and origin

| Element | Nominal blockout dimensions |
|---|---:|
| Cavern envelope | 3,000 m across × 2,000 m high |
| Chamber center / asset origin | `(0, 0, 0)` |
| Reference roof / floor | approximately Blender `Z +1,000 / -1,000 m` |
| Suspended heart / containment assembly | approximately 600 m tall |
| Widest core service terrace | 528 m diameter |
| Exhaust opening | nominal 150 m diameter |
| Modeled exhaust segment | reactor crown to cavern roof, approximately `Z 302–1,039 m` |
| Wall service levels | Blender `Z -180, +80, +330 m` |
| Core service levels | Blender `Z -140, +80, +225 m` |
| Main entry causeway | 12 m wide, approximately 1,156 m long |
| Secondary causeways | 9 m wide |
| Human reference | approximately 1.8 m tall |

The organic cave is an approximate envelope, with local protrusions and irregularities. It is not a surveyed cylinder.

One Blender unit is one meter. Blender uses Z-up; glTF/Godot uses Y-up. Blender `(X, Y, Z)` becomes Godot `(X, Z, -Y)`. The arrival side, Blender negative Y, becomes Godot positive Z. Keep instance scale at 1.0.

For a proposed chamber center about 4 km below a local surface datum, place the exported scene root around Godot `(0, -4000, 0)`. Do not bake that translation into each component. The present exhaust ends at the chamber roof, still approximately 3 km below that surface datum. A later connecting shaft and surface POI must span the remaining distance; no surface structure is built here.

## Editable organization

Collections `01–07` contain exportable architecture:

1. Reactor pressure vessel: suspended spindle, disconnected containment petals and segmented arcs.
2. Three suspension buttresses: three sweeping monolithic blade assemblies.
3. Service terraces: wall arcs, partial core terraces, entry docking tongue and vestibule.
4. Maintenance bridges: primary hovering causeway, upper secondary route, fractured lower route.
5. Wall anchors: faceted stelae, landing platforms, recessed future transfer/lift interfaces.
6. Entrance and human scale: faceted landing tongue and reference explorer.
7. Exhaust link: split sculptural pylons, partial aligned collars, open roof interface.

Collection `80_CAVE_SDF_REFERENCE_ONLY` is composition/reference geology. Replace it with the game's generated SDF terrain. It is deliberately absent from all architecture exports.

Collection `90_LIGHTS_AND_CAMERAS` contains Blender-only preview lighting and haze. The saved scene opens on `CAM_01 | entrance reveal`. Other cameras show a layout cutaway, explorer reference and reactor detail. Hide collection 80 and the preview atmosphere for a clean architecture inspection.

## Reusable assemblies

The separate GLBs in `modules/` are editable structural assemblies, not finalized grid-snap tiles:

- `Reactor_Vessel.glb`: the contained heart and separated containment pieces, pivot at chamber center.
- `Suspension_Buttress.glb`: one blade assembly, pointing along Blender positive Y / Godot negative Z. Its pivot is chamber center; rotate 120° copies for the other arms.
- `Wall_Anchor_and_Lift.glb`: one stele, landings and reserved transfer interface. The filename denotes a future lift interface, not an implemented moving elevator.
- `Arrival_Overlook.glb`: arrival tongue and explorer reference, with a local entrance pivot.
- `Exhaust_Cavern_Segment.glb`: the cavern-length exhaust assembly, pivot at the reactor crown.

`module_manifest.json` records the exact source-space pivots. Those offsets let the modular exports be returned to their assembly positions. Modules were refreshed from this alien revision; the earlier industrial version is not part of this package.

## Godot import

1. Import the assembled GLB as a scene in a test folder, then instantiate it under a dedicated `Node3D`. Leave scale at 1.0.
2. Align the SDF cavern to the chamber-center origin, using the Blender rock only as a reference volume and composition guide.
3. Add the project's own lighting, fog, exposure and GI. Blender lights and volumetric atmosphere do not transfer. Materials carry their emission; configure renderer glow separately.
4. Keep the exhaust horizontal origin aligned with the later shaft and surface vent.
5. Author simplified collision for intended routes and terraces. Do not generate concave collision on every decorative component by default.
6. Tune traversal speed, route widths/joins, vertical transitions, encounter pacing, streaming, LODs and visibility ranges after an in-engine walkthrough. Geometry connectivity is not a tested walkability guarantee.
7. Add lift behavior, damage states, sound, reactor/exhaust VFX and animation later. This blockout is static.

The assembled export is approximately 0.69 MB with 210 mesh nodes/surfaces and 13,394 triangles. The low triangle count does not constitute a performance guarantee; batch or instance components according to the target renderer and hardware.

## Rebuilding

Run the scripts beside the supplied `.blend`:

`blender -b -t 8 -P build_alien.py`

`blender -b -P export_modules.py`

`blender -b -t 8 -P render_alien.py`

`build_alien.py` replaces architecture collections 01–07 inside the supplied scene and rewrites the assembled GLB. It preserves the reference cavern and camera/light rig; it is not a standalone terrain generator. Back up artistic edits before regenerating. `geometry_helpers.py` must remain beside the builder.

The supplied Blender installation lacks OpenImageDenoise, so the render scripts explicitly use ordinary Cycles CPU sampling. Large atmospheric views take a few minutes. The source `.blend` is authoritative for manual edits.

## Scope limits

No game repository changes, generated SDF terrain, completed surface POI, gameplay implementation, collision meshes, baked lightmaps, animation, LODs or target-device profiling are included. The main import was tested in Godot; collision, navigation, movement, lighting equivalence and game performance were not.
