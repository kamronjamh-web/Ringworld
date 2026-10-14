# Main Spire: 4 km Staggered Outer-Arm Revision

One assembled Ringworld surface asset, including the original six-stage main spire, its base facility, the new outer arms and connector rings, and the through-arm entrance hall.

## What changed

- Added **eight full-height outer arms**, staggered exactly **22.5°** from the eight original inner arms.
- Outer feet are centered at **730 m radius**. The outer profile wraps the existing tower and continues above its 3,600 m crown to **4,000 m total height**.
- The new arms physically meet an **open annular crown ring** near their pointed tips. The central beam stays unobstructed.
- Added **six outer structural rings** at the existing accelerator elevations: **550, 1,050, 1,550, 2,050, 2,550, and 3,050 m**.
- Each outer ring intersects all eight outer arms. Eight solid radial ties per level overlap the original collector sectors and the outer ring. The preserved inner stage clamps connect the original eight arms, forming a physically connected sixteen-arm assembly.
- Outer arm **07**, at **292.5°**, contains the entrance. Its foundation and lower arm are split into real solid jambs around a traversable opening. A continuous hall joins the new exterior entrance to the existing base facility.

All **2,398 mesh components of the previous main spire remain unchanged** in world-space geometry. The original eight arms, six accelerator stages, facility, materials, and 1.8 m references were retained. The previous delivered files were not overwritten.

## Scale and origin

- **One unit = one metre**. Import the GLB at scale 1.0.
- Blender Z-up; GLB exports Y-up for Godot.
- Ground-center origin: `(0, 0, 0)`.
- Ground-to-tip height: **4,000 m**; the foundation slab extends to **Z -2 m** in Blender.
- Overall assembled footprint: approximately **1,642.78 × 1,642.78 m**; full height including the foundation is **4,002 m**.
- Central beam envelope: a straight **150 m radius / 300 m diameter** cylinder on the vertical axis.
- Eight inner arms: **0°, 45°, 90°, 135°, 180°, 225°, 270°, 315°**.
- Eight outer arms: **22.5°, 67.5°, 112.5°, 157.5°, 202.5°, 247.5°, 292.5°, 337.5°**.
- The original inner architecture was not enlarged again. Only the new outer arms extend the overall height to 4 km.

## Entrance and circulation

The entrance is geometry, not a painted doorway:

- New hall frame: **20 × 20 m nominal**, ground-level floor. Wall guides leave **19.85 m** between their inner faces; ceiling inserts leave **19.99 m** headroom.
- New outer portal frame: **20 × 20 m nominal**. Its decorative reveals leave **19.6 m minimum free width**. The new passage therefore has at least **19.6 × 19.99 m** free cross-section away from the preserved source-door transition.
- Hall aligns with the existing entrance at **292.5°**.
- Added floor/hall span: approximately **R444–882 m**, with a shallow under-apron joint at the inner end to avoid coplanar overlapping floor faces.
- The original facility entrance retains its **16 × 16 m nominal frame**. Its existing reveal details narrow the local width to approximately **15.52 m**. This unchanged doorway is the narrower transition along the combined route.
- Existing circulation, room access, gallery, and ramp remain intact.

The other seven outer arms have solid foundations and lower bodies. Only the designated entrance arm has the ground-level through-opening.

## Editable parts and cutaway

New collections/groups:

- `31_OUTER_STAGGERED_ARMS_EXPORT`
- `32_OUTER_ARM_FOUNDATIONS_EXPORT`
- `33_SIX_STAGE_OUTER_CONNECTOR_RINGS_EXPORT`
- `34_OUTER_ARM_ENTRANCE_HALL_EXPORT`
- `35_OUTER_ENTRANCE_REMOVABLE_ROOF_EXPORT`
- `36_OUTER_4000M_OPEN_CROWN_EXPORT`

For an entrance inspection, hide collection **35** and the upper `Outer_Arm_07_Main_Monolith` body. The lower solid entrance jambs stay in collection **34**, so the real opening and continuous floor remain visible. For the facility cutaway, also hide the original removable roofs in collections **11** and **24**. The supplied cutaway render hides the tall spire components for an unobstructed view of the route; it is a view of the same asset.

## Files

- `Main_Spire_Outer_Ring.blend`: editable Blender 4.3.2 scene.
- `Main_Spire_Outer_Ring_Godot.glb`: **one combined assembled export**, with named group/component hierarchy.
- `previews/`: full-height hero and entrance inspection views.
- `scripts/`: incremental builder, renderer, re-export, validation, and packaging.
- `validation/`: measured geometry, contact tests, beam/route audit, source comparison, and manifests.
- `GODOT_IMPORT_VALIDATION.md`: actual import/instantiation test results when included.

No preview ground, cameras, lights, reference figures, or beam visualization is included in the GLB. The Blender scene contains presentation lights for the hall; add project-specific lighting in Godot.

## Validation

The completed geometric audit verifies all **248 structural contacts**, the full-height beam bore, all **11 walking routes** with **30,264 supported-human clearance checks**, and exact preservation of the previous 2,398 source mesh components. The modeled new frame is nominally 20 × 20 m; its trim-adjusted free opening is explicitly reported as 19.6 × 19.99 m. See the detailed audit and dimensional findings in `validation/`.

The final GLB also passed an actual **Godot 4.6.3 import and instantiation test**, with no warnings or errors: **2,820 meshes, 3,615 surfaces, and 106,204 triangles**.

## Godot and production scope

Instantiate the single GLB as one scene. It contains no collision, navmesh, interactions, terrain integration, or gameplay behavior. Geometric passage tests do not replace player-controller and navigation tests in the actual project.

Components remain separate to support editing. The high object/surface count needs a game-specific batching, LOD, visibility, and collision pass before production use. No frame-rate or shader-parity claim is made. Large-world placement may need an appropriate floating-origin strategy.

## Re-export and reproduce

Re-export the delivered editable scene:

`blender -b --python scripts/export_outer_ring.py`

Render delivery views:

`blender -b -t 4 --python scripts/render_outer_ring.py -- hero entry entrance_exterior`

The optional incremental builder needs the previously delivered `Main_Surface_Spire.blend`, with its `validation/route_samples.json` alongside it. It reads that file without changing it:

`blender -b --python scripts/build_outer_ring.py -- --source /path/to/Main_Surface_Spire.blend`

To repeat the full source comparison, contact, and route audit:

`blender -b --python scripts/validate_outer_ring.py -- --source /path/to/Main_Surface_Spire.blend`

The previous asset is not duplicated in this package. This revision's `.blend` is fully editable without it. All dimensions and contact claims refer to the modeled geometry, not a physical simulation of energy throughput.
