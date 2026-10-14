# Ringworld Main Reactor

An enlarged six-module main-installation variant of the approved alien tripod reactor. Its art direction is to read as a master plant handling the work of six reactor/emitter units at once. This is a fictional visual-capacity design, not a physical output rating. This package contains one assembled architecture asset, an editable Blender scene, two previews, and source/validation scripts. No original files or game project files were modified.

## Files

- `Ringworld_Main_Reactor.blend`: current authoritative authoring scene, named mesh objects and collections, materials, separate cave reference, 1.8 m reference explorer, and preview cameras/lights.
- `Main_Reactor_Architecture.glb`: one assembled export with individually named component nodes. Manufactured architecture only.
- `preview_01_entrance_reveal.png`: entrance perspective with the separate reference cavern.
- `preview_02_architecture_cutaway.png`: full architecture with the cavern and explorer hidden.
- `build_main_reactor.py`, `mesh_helpers.py`: repeatable additions builder and mesh helpers; these scripts are also embedded in the Blender file. `render_main_reactor.py` refreshes previews without regenerating geometry.
- `validate_main_reactor.py`: exhaust and primary-approach geometry checks.
- `build_stats.json`, `geometry_validation.json`, `glb_validation.json`, `VALIDATION.md`: measurements, checks, and explicit limits.

## Main-installation design

The three sweeping buttresses, suspended faceted heart, separated containment petals, and split exhaust remain the visual foundation. Every inherited architectural coordinate and dimension was doubled and baked into geometry. Additional geometry is designed at the larger installation scale, not created by subdivision:

1. Three secondary containment tiers made from separated armor arcs, crest plates, recessed channels, and coupling forks.
2. Nine inner sheath segments around the faceted heart, plus channel liners, plasma slits, and 30 outer dorsal armor panels.
3. Paired power-distribution ribs beneath each buttress, with inset bus channels and articulated cheek plates.
4. Six substantial reactor power modules arranged around the master heart. Each has a contained faceted core, six heavy shell petals, visible glowing core apertures, three segmented containment belts, a crown collector, and two service balconies. Each containment body reaches 536 m across; each full module, including its lower keel, is 815 m tall.
5. Six heavy armored power trunks converge into six new collector jaws and thick manifold segments around the central heart. A new upper structural collector reinforces the master-core silhouette.
6. Three tiers of flanged exhaust collars and floating vertical vanes.

There are 626 new mesh objects, on top of the 201 inherited architectural objects. The scene keeps the original dark graphite / ceramic-alloy vocabulary with restrained teal and white emissive details. The structural cutaway uses brighter inspection lighting; materials themselves remain dark.

## Scale and dimensions

All dimensions below are measured in Blender's Z-up coordinates. One unit is one metre. Keep the imported instance at scale `(1, 1, 1)`.

| Measurement | Original architecture | Main reactor |
|---|---:|---:|
| Manufactured X extent | 2,503.999 m | 5,007.997 m |
| Manufactured Y extent | 2,777.714 m | 5,555.428 m |
| Manufactured Z extent | 1,399.000 m | 2,798.000 m |
| Axis-size multiplier | — | 2.000 × each axis |
| Nominal cavern reference | 3,000 m wide × 2,000 m high | 6,000 m wide × 4,000 m high |
| Nominal exhaust diameter | 150 m | 300 m clear |
| Main causeway outer profile | 12 m | 24 m |
| Main causeway broad flat top | 9 m | 18 m |
| Secondary causeway outer profiles | 9 m | 18 m |
| Reference explorer | approximately 1.8 m | 1.800 m |

Manufactured bounds: X `−2503.999..+2503.999`, Y `−2860..+2695.428`, Z `−720..+2078` m. The cavern is an irregular concept reference, not surveyed terrain. Its nominal roof is around Z +2000 m; the roof interface extends to Z +2078 m.

## Reactor / spire registration

- Reactor local origin: chamber center `(0, 0, 0)`.
- Axis: local Blender Z; glTF/Godot Y after export.
- Exhaust cylinder begins above the emitting crown at local Blender Z = +600 m and remains clear through the modeled roof interface at Z = +2078 m.
- Required radius is 150 m. The measured minimum physical clearance, including polygon faces rather than only vertex radii, is 152.012 m.
- The suspended heart occupies the center below the crown. It is intentionally not an empty vertical shaft through the core.
- For a chamber center approximately 4 km below the surface, place the reactor root at Godot `(0, -4000, 0)`. Do not bake that translation into the meshes.
- The matching surface spire is a separate asset with its own ground-level origin. Align its horizontal axis with this reactor's horizontal origin.
- At that suggested placement the modeled reactor roof reaches approximately world Y = −1922 m. The intervening deep shaft remains future integration. This asset does not include a full underground-to-surface connection.

Blender `(X, Y, Z)` exports to Godot `(X, Z, −Y)`. The arrival side, Blender negative Y, therefore faces Godot positive Z.

## Editable scene organization

Collections 01–07 contain the doubled inherited architecture. Collections 08–12 contain the new main-reactor geometry:

- `08_SECONDARY_CONTAINMENT_HALOS`
- `09_NESTED_CORE_FACETS`
- `10_POWER_DISTRIBUTION_RIBS`
- `11_SIX_REACTOR_POWER_MODULES`
- `12_EXHAUST_CROWN_ARTICULATION`

Collection 80 is reference cave geometry and is excluded from the GLB. Collection 90 is the preview rig and is excluded. Collection 92 contains the unchanged-size reference explorer and is excluded. Registration guides are empties and are excluded. No beam VFX mesh is exported.

All architectural object scales are 1. Geometry remains low-poly and component objects are individually editable. The assembled GLB is one asset with 827 mesh instances and 587 unique mesh resources. Repeated pod components share geometry while remaining independently placed, named objects. Make a component single-user in Blender before making an individual mesh edit that should not propagate to the other pods. The asset has 44,626 instantiated triangles. Node count/draw calls should be optimized for the final engine renderer as needed.

## Import and gameplay integration

1. Import `Main_Reactor_Architecture.glb`, instantiate once beneath a placement Node3D, and leave its scale at 1.
2. Keep its chamber-center origin aligned with game terrain. Replace the reference cavern with the game's SDF terrain.
3. Align the future shaft and surface spire horizontally. Retain the 300 m clear exhaust cylinder.
4. Add game lighting, fog, exposure, emissive glow, sound, and reactor effects. Blender preview lighting is intentionally absent from the GLB.
5. Author simplified collision only where needed. Add traversal, lifts, navigation, encounter layout, and streaming/LODs in the game.
6. Validate the player controller in-engine. The primary route has a tested unobstructed center approach, but this is not a gameplay or navigation certification. The lower secondary route remains deliberately broken.

No collision meshes, animation, LODs, lightmaps, full connecting shaft, completed level, or target-device performance guarantee are included.

## Regeneration

Run beside the supplied `.blend`:

`blender -b -t 8 -P build_main_reactor.py`

To rebuild geometry and export without spending time rendering:

`blender -b -t 8 -P build_main_reactor.py -- --no-render`

To refresh only the hero preview without rebuilding geometry:

`blender -b -t 8 -P render_main_reactor.py -- --hero-only`

Then validate:

`blender -b -t 2 -P validate_main_reactor.py`

The script uses the supplied current Blender scene as its editable baseline. It preserves doubled collections 01–07 and the reference cave, replaces generated collections 08–12, and refreshes the assembled export. It does not require the old reactor package. Back up artistic edits before running: manual changes inside collections 08–12 will be replaced. The optional `--source` argument is only for a first build from an original reactor scene.

Blender 4.3.2 / Cycles CPU was used. Denoising is disabled because this installation lacks OpenImageDenoise. No external textures or add-ons are required.
