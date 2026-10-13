# Authored meadow grass

Original Ringworld meshes created in Blender 5.2.2 with the repository authoring
script. No external models, textures, or baked biome colors are used. The editable
source is `source/meadow_grass.blend`; its folder has `.gdignore` to avoid Godot
auto-importing the Blender source. Runtime consumes the twelve JSON files directly so
per-blade metadata remains intact. Blender authored meshes are the source of the
exported positions, corner normals, UVs, indices, and custom attributes.

| Tier | Blades | Triangles | Exported vertices |
| --- | ---: | ---: | ---: |
| near | 8 | 32 | 48 |
| near_reduced | 8 | 16 | 32 |
| intermediate | 6 | 12 | 24 |
| far | 4 | 8 | 16 |

Each tier has `compact`, `spreading`, and `leaning` variants. Irregular root
spacing, yaw and blade heights reduce repeated star-shaped silhouettes. Near
blades retain a two-section arc; reduced near keeps all eight blades as single
sections at the existing 16-triangle cost option. Intermediate and far tiers retain a subset of
the same planted sources with slightly wider one-section ribbons. Rest bends
remain small because the standing-grass shader supplies the visible curvature
and animation. Tapered opaque geometry supplies silhouettes without alpha cards.

The source is normalized: roots lie at height zero and each mesh reaches height
one. Runtime height settings produce the intended predominantly 10–20 cm meadow.
Prototype roots remain inside radius 2.5 to match the existing procedural clump
footprint. Runtime scales footprint and blade width independently of height.

## Export contract

Each `{variant}_{tier}.json` stores `schemaVersion: 1`, `name`, `variant`, `tier`,
`bladeCount`, and the following flat arrays:

| Field | Per vertex | Meaning |
| --- | --- | --- |
| `positions` | x, y, z | Godot Y-up local position |
| `normals` | x, y, z | Unit rounded ribbon normal |
| `uv` | u, v | u=0/1 strip edges; v=0 at root and 1 at that blade's tip |
| `custom0` | rootX, rootZ, sideX, sideZ | Per-blade planted root and unit rest width axis |
| `indices` | triangle vertex indices | Godot clockwise winding |

Use `SurfaceTool.CustomFormat.RgbaFloat` for CUSTOM0. UV.v represents each
blade's own fractional height even when a shorter blade's position.y is below
one. Apply any footprint change to positions.xz and custom0.xy together; facing
axes remain unit length. Normals and UVs export from Blender corners, with full
attribute tuple deduplication preserving edited seams. No material or color
attribute is embedded.

Blender uses Z-up, with source `(x, y, z)` exported as Godot `(x, z, -y)`.
Blender mesh faces have counterclockwise winding; export reverses them. The
source stores per-point `root_x`, `root_z`, `rest_side_x`, `rest_side_z` attributes,
the `BladeUV` UV map, and custom rounded normals. Mesh object transforms must
remain identity when editing; apply transforms before export if changed.

## Reproduce or edit

Run from the repository root using an installed Blender executable:

```powershell
blender --background --factory-startup --python tools/author_meadow_grass.py
```

On the Windows Store installation used for these assets, the registered alias
works in a normal authorized shell, while direct launch of the package executable
is denied by Windows:

```powershell
& "$env:LOCALAPPDATA/Microsoft/WindowsApps/blender-launcher.exe" --background --factory-startup --python C:/Projects/Ringworld/tools/author_meadow_grass.py
```

To preserve artist edits, load the source and export existing meshes:

```powershell
blender --background resources/grass/meadow/source/meadow_grass.blend --python tools/author_meadow_grass.py -- --export-existing
```

The generator rebuilds all twelve meshes by default and saves the editable `.blend`.
`--export-existing` reads the currently loaded meshes without recreating them.
The source starts with compact near visible; reveal other tiers/variants through
the Outliner. Objects overlap intentionally at identity transforms for export.

Every export checks finite arrays, attribute lengths, index ranges, triangle
limits, normalized height, unit normals and facing axes, root radius, UV height
range, nondegenerate triangles, and Godot winding. `validation.json` records
the Blender version, mesh counts, and bounds. Runtime visual handoff and wind
integration need the game's normal visual checks; mesh validation alone cannot
establish the perceptual terrain-distance seam.

## Runtime integration and packaging

`RingMeadowGrassMeshLibrary` reads the twelve variant/tier JSON files from this
folder, builds and caches `ArrayMesh` surfaces with the custom attributes above,
and selects variants deterministically per tile. Both CPU and GPU presentation
use the authored tiers. The grave-key Grass menu exposes meadow enablement,
height, spread and variant selection, along with the existing density and
terrain matching controls. Sparse user settings survive graphics preset changes;
reset restores 0.10 to 0.20 m, mixed variants and spread 1. Coordinated terrain
handoff uses the supported grass coverage budget to choose an effective draw
distance; disabling coordination retains the configured distance and manual
terrain coverage band.

For packaged builds, add `resources/grass/meadow/*.json` to the export preset's
non-resource include filter. These files are read directly with `FileAccess` and
must be present in the exported PCK; Godot resource dependency scanning does not
infer them. The editable `.blend` source is excluded from Godot import and is
not required at runtime.

Run from the repository root with the .NET Godot executable available as `godot`:

```powershell
dotnet build
godot --headless --path . --script res://tools/meadow_grass_mesh_test.gd
godot --path . --script res://tools/grass_live_tuning_smoke_test.gd
godot --path . --resolution 1280x720 --script res://tools/meadow_grass_comparison.gd
```

The mesh check validates authored tiers, cache behavior, spread, source frames,
deterministic variants and preserved placement budgets. The live tuning check
covers controls, validation, sparse save/reload, preset preservation and reset.
The rendered comparison captures legacy tall, legacy short and meadow short
cases at matching root placements and records timing and geometry diagnostics
under `artifacts/meadow-grass/`. It measures the whole viewport rather than an
isolated grass pass, so its timing is a bounded comparison fixture rather than
a full gameplay performance claim.
