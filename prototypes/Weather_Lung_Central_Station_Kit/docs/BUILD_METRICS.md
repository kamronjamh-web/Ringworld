# Measured build metrics

- Architecture: 24,872 triangles; 14,632 authoring vertices; 1,098 separate editable mesh objects.
- Export: 12 static sector mesh nodes, 5 vane pivots, 10 vane mesh nodes.
- Overall footprint: 27,908.887 m × 17,859.230 m.
- Lowest foundation: -95.000 m.
- Highest arch point: 4,799.973 m.
- Static export material surfaces: 79; vane surfaces: 10.
- Far proxy: 7,332 triangles, one mesh, four material surfaces. Same horizontal bounds and arch-top height as architecture.
- No image textures or external material dependencies.
- No animation, collision or weather simulation is included.

Counts are measured from the generated geometry. Godot may duplicate vertices across hard normals/material surfaces on import. Rendering cost depends on camera, visibility, lights, materials and the surrounding game; these counts are not frame-rate claims.
