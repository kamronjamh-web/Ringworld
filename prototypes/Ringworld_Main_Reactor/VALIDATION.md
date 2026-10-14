# Main Reactor Validation

## Passed

The latest revision contains six full reactor power modules feeding one master core. Each module has its own substantial containment body and glowing core aperture. This is visual design, not a simulated six-unit power rating.

- Blender 4.3.2 scene opens and exports successfully.
- Godot 4.6.3 imports and instantiates the final six-module GLB with no import warnings or errors.
- Manufactured architecture measures 5,007.997 × 5,555.428 × 2,798.000 m in Blender XYZ: exactly 2.000× the original measured architecture on each axis.
- All 827 architectural mesh objects use unit scale. One unit represents one metre.
- 626 newly modeled objects supplement 201 inherited architectural objects.
- 44,626 instantiated triangles, 827 mesh nodes / surfaces, 587 unique shared mesh resources, 7 exported materials.
- The GLB has no external buffer references, texture/image dependencies, cameras, lights, cave, atmosphere, or reference explorer.
- The reference explorer measures 1.800003 m, with floating-point precision accounting for the approximately 0.003 mm difference from 1.8 m.
- Every architectural triangle was tested against the requested exhaust cylinder, clipping triangles to Z = 600..2078 m. Minimum radial clearance is 152.012 m; required radius is 150 m. No intersections were found.
- The primary entry's center approach has no architectural obstruction in a tested 8 m-wide × 2.1 m-high corridor, from Blender Y = −2780 to −545 m, above the top surface at Z = 162.31..164.41 m. This checks a conservative central part of the 18 m-wide flat walking surface.
- No nonfinite vertex coordinates were found.

## Godot measured bounds

Godot Y-up AABB position: `(−2503.999, −720, −2695.428)` m.

Godot Y-up AABB size: `(5007.997, 2798, 5555.428)` m.

This is the expected axis conversion from Blender Z-up coordinates.

## Rebuilding and inspection

The additions were rebuilt in an isolated copy using only the delivered current Blender scene and its two source scripts. Counts and measured bounds match the delivered asset. The builder does not need the original reactor package, does not repeatedly double the baseline, and supports `--no-render`.

Both previews were visually inspected. The cutaway is an architectural inspection view; the entrance image uses the separate cavern to communicate scale. Preview materials and lighting are not a promise of identical appearance in Godot.

## Scope limits

The Godot check verifies import and scene instantiation, not collision, gameplay, renderer appearance, navigation, player movement, memory/performance on target hardware, or streaming. No collision shapes, animation, LODs, lightmaps, player logic, full deep shaft, or finished game terrain are provided. The damaged lower secondary route is deliberately discontinuous. Secondary service terraces, vertical transitions and isolated ledges require gameplay design and validation.

Detailed machine-readable measurements are in `build_stats.json`, `geometry_validation.json` and `glb_validation.json`.
