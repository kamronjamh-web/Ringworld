# Validation — alien revision

## Source and export

- Source authored and saved with Blender 4.3.2.
- Metric units: one unit = one meter. Chamber-center origin preserved.
- Architecture exported as glTF 2.0 binary (`.glb`) with the standard Y-up conversion.
- Final assembled architecture: **210 mesh nodes / 210 surfaces / 13,394 triangles**.
- Reference cavern, atmosphere, preview cameras and lights excluded from the architecture export.
- Five modular assemblies refreshed from the alien source scene.
- No external textures or linked asset files required.
- `glb_validation.json` records byte size, SHA-256, structural counts and binary-data checks.

## Actual engine check

The final 210-object architecture GLB was imported into a clean, isolated Godot **4.6.3** test project and instantiated successfully. The clean import/instantiation check reported **no warnings or errors**.

Measured Godot world AABB:

- Position: approximately `(-1251.999, -360, -1347.714)` meters
- Size: approximately `(2503.999, 1399, 2777.714)` meters

This measures manufactured architecture only. It is intentionally smaller than the nominal 3 km-wide, 2 km-tall cavern reference.

The in-engine check is an import/scene-instantiation check, **not** a gameplay, navigation, collision, lighting-parity or target-device performance test. Individual module GLBs were exported and structurally inspected; they were not separately instantiated in Godot.

## Visual inspection

- The earlier industrial geometry was replaced, not merely renamed or recolored.
- Revised cutaway shows the three tapered supports, faceted suspended heart, separated containment arcs, split exhaust pylons and three wall service levels.
- The main entry ribbon connects to an added core vestibule arc; the separate lower secondary route retains an intentional damaged gap.
- Entrance camera moved clear of foreground reference rock after an obstructed first pass.
- Preview lighting reduced to preserve dark graphite forms and restrained energy channels.

Final previews use Blender's actual scene geometry. This remains an editable visual blockout requiring an in-game walkthrough and production detailing.
