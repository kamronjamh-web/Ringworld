# Ringworld atmosphere-shield projector

An editable, real-geometry interpretation of the approved alien tower concept. The later requests for a wall-rising base and extended feet are included: shaped wall saddles, layered swept buttresses, broad curved feet and intake hoods rise into the two blades. The corrected feet extend 50m along the wall past each end of the original 80m body, with no outward overhang across the wall. This is not concept art, a shield implementation, or a structural-engineering design.

## Open the asset

- **`atmosphere_shield_projector.blend`** — editable Blender 4.3.2 source; named components and collections, shared repeated emitter meshes, materials, optional wall context, five preview cameras and lighting.
- **`atmosphere_shield_projector.glb`** — assembled glTF 2.0 game asset, with the two physically separate halves batched into **two mesh nodes / fourteen material surfaces**. Two named power-intake socket nodes are included. No wall, cameras, lights, shield plane, beam or power-highway mesh is exported.
- **`previews/`** — actual Blender renders: full height, emitter detail, crown, intake detail and wall-to-tower transition. The optional 100m-wide wall segment is only a scale/context prop.
- **`qa/`** — measured geometry, topology and actual import-test evidence.
- **`build_projector.py`**, **`optimize_export.py`** — reproducible Blender generation and batched export scripts. Run build first, then export. The source scene is not collapsed by the export step.

## Scale and orientation

All dimensions are **meters**, with one Blender unit and one Godot unit equal to one meter. Do not apply an additional 0.01 or 100 import scale.

| Property | Actual design value |
|---|---:|
| Complete asset envelope, including all feet, trim and crown | 80 × 180 × 1600 m |
| Tower body footprint, excluding the extended feet | 80 × 80 m |
| Foot extension past each body end, along the wall | 50 m |
| Blender bounds | X −40…40; Y −90…90; Z 0…1600 |
| Godot / glTF bounds | X −40…40; Y 0…1600; Z −90…90 |
| Number of split spheres | 20 |
| Combined outer sphere envelope | 45 × 45 × 45 m |
| Vertical center pitch | 70 m |
| Clear vertical gap between sphere envelopes | 25 m |
| Sphere center heights | 145, 215, …, 1475 m |
| Minimum central split air clearance | 0.9 m |
| Planned wall width across the tower | 100 m |

**Origin:** the wall-top center, not the wall bottom. Place the asset root on the top surface of the wall. The tower body remains inside its 80m footprint. The centered 100m-wide wall has faces at X±50m; all tower and foot geometry stays within X±40m, leaving 10m clearance to each side. The extended toes reach Y±90m, exactly 50m beyond the original body ends at Y±40m. They sweep along the wall's length, not outward over its faces. The wall is only optional context and is excluded from the GLB.

**Blender:** Z is up; Y runs along the wall; X runs across the wall. The absent shield occupies the vertical **X=0 / YZ plane**. Every physical part belongs wholly to X-positive or X-negative space.

**Godot:** Y is up; −Z follows Blender +Y along the wall. The absent shield still occupies **X=0**. The two sockets face Godot +Z (Blender −Y), at X ±14m, height19m, Z39m. These are attachment markers, not a highway implementation.

The sphere envelope is an engineered, segmented assembly: graphite liner, thick shell panels, cyan split collars and mechanical brackets. Shell shaping at the slit preserves the specified 45m envelope rather than shifting hemispheres outward. The 25m gap is measured between the spherical envelopes, not between every piece of nearby crown/support machinery.

## Design choices

- Pale ceramic/titanium blades, champagne structural trim, graphite machinery and thin cyan emissive circuits.
- Two disconnected tower halves; no tie crosses the shield plane, including at the base or crown.
- Layered, smoothly transitioning wall-side supports and extended curved feet are part of the tower, not baked into the optional wall.
- The topmost sphere has split double containment arcs, gimbal locks, suspension brackets and focus vanes. This visually suggests the fictional 40km-high field; no such field is generated or modeled.
- The full-height view preserves the actual 20:1 height-to-body-width ratio (approximately 8.89:1 height-to-along-wall-foot-span). It is consequently much more slender than the stylized concept illustration.

## Editing and export

Edit the `.blend` for individual plates, sphere halves, mounts and tower parts. Repeated emitter parts share mesh data; make a mesh single-user before changing only one instance. Collections `01`–`05` contain the asset. Collections `90` and `91` contain optional render context, cameras and lights.

The GLB intentionally combines pieces for fewer scene nodes and material surfaces. It retains the physical split, materials, normals, root metadata and intake markers, but not the source's per-panel editability. To regenerate from a terminal with Blender installed:

```sh
blender -b --python build_projector.py
blender -b --python optimize_export.py
```

## Scope and performance limits

The high-detail assembly contains **424,288 evaluated triangles**, with seven texture-free PBR materials. Batching controls node/material overhead; it does **not** make this a finished low-poly crowd asset. For large numbers of distant projectors, create distance LODs or impostors and profile in the target scene. No production LOD chain, UV atlas, lightmap UVs, baked textures, collision, navigation, animation, shader logic or gameplay code is included.

Materials use standard metallic/roughness/emission glTF properties. Glow/bloom in Godot depends on your world environment and renderer settings; the export does not add a world environment. No external texture files are required. Intentional overlaps between armor, mountings and structural members are part of the hard-surface assembly; it is not a single watertight printable solid.

## Verification

Final QA includes a fresh Blender reopen, all-asset measured bounds, all 20 sphere envelopes and all 19 spacings, split-plane separation, closed-mesh topology/orientation checks, GLB structure checks and a real Godot 4.6.3 headless import plus scene instantiation. See the accompanying JSON reports for exact results. The user's actual game project was not accessed or modified; rendering performance and integration in that project are not tested.
