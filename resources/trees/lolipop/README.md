# LOLIPOP tree-pack import

This directory retains the source imports for review and now supplies the
production imported-forest catalog. It does not replace the KayKit assets.

## Contents

`fir/`, `pine/`, and `maple/` retain the supplied `scene.gltf`, `scene.bin`, PNG textures, and `license.txt` verbatim. Each pack has author-named LOD0, LOD1, LOD2, and LOD3 billboard groups. The preview selects exactly one variant and one LOD group per species, so author LODs are never shown over one another.

The extracted runtime source totals 192,045,801 bytes across 105 files. Source archive sizes are 84,950,999 bytes (fir), 42,553,319 bytes (pine), and 54,941,772 bytes (maple); Godot's generated `.godot/` import cache is intentionally excluded and varies by platform.

Fir contains 3 authored variants; pine contains 15; maple contains 12. The author files include one-metre reference/checker geometry in the maple/pine packs and use composed, non-identity imported transform chains. The preview preserves each selected LOD root's composed transform while removing only the source demo-layout translation before placing it at the gallery origin. Inspect the preview before choosing world scale. Maple is the likely dominant species for future placement, with fir and pine also included in the forest mix; no biome ratios or spawn rates are set here.

Foliage uses alpha blending in the pine/maple source and alpha masks in fir. `TreeAssetPreview.gd` makes preview-only duplicated foliage materials alpha-scissor, double-sided, and shadow-cutout friendly while retaining the imported albedo, normal, metallic/roughness, and bark materials. It does not modify the supplied asset files.

Godot currently imports the maple bark base colour on UV0 but does not honour that source material's metallic/roughness texture UV1 assignment. The production catalog composes a material adaptation while leaving the supplied source assets intact.

## Production forest use

`ImportedTreeCatalog` maps the 30 supplied variants (3 fir, 15 pine, 12 maple)
to authored LOD meshes and composed source transforms. `RingBiomeDecorationLayer`
uses those catalog identities in canonical vegetation records, seeded regional
cover, humidity-aware local spacing, bounded MultiMesh tiles, and nearby
independent trunk colliders. The catalog/cache identity includes the forest
algorithm and catalog versions so old vegetation records cannot overlap the
current layout.

The representative entry is `scenes/PlayableForest.tscn`: F6 starts a private,
unsaved Certified seed-1337 tour from clearing to woodland to a dense grove.
See `docs/forest-region-implementation-checklist.md` for coordinates and
targeted runtime/capture commands.

## Preview and validation

Open `scenes/TreeAssetPreview.tscn` directly in Godot with F6 (Current Scene). Drag left mouse to orbit, mouse wheel to zoom, WASD to pan, and press 1 through 4 to select LOD0 through LOD3 (billboard). `F`, `P`, and `M` focus Fir, Pine, and Maple; `R` resets the view. The scene shows a fir, pine, and maple specimen only.

Run `godot --headless -s res://tools/realistic_tree_import_test.gd` from the project directory to verify the three imports, source dependencies, geometry, author LOD grouping, and preview scene. To opt into GPU captures, omit `--headless` and pass `-- --capture` after the script argument while using a Vulkan renderer; it writes gallery LOD0, fir LOD0 closeup, and gallery LOD3 only under `.godot/realistic_tree_import/`, and rejects a renderer without a `RenderingDevice`. Production distribution, long-distance presentation, quality budgets, and worst-weather/full-game visual acceptance remain pending. The shared lighting, wind, cloud-mask, and shadow material paths have automated coverage, but that does not establish the performance or human visual acceptance gates.

## Import verification (2026-09-06)

- All 105 original payload files were independently SHA-256 compared with the supplied archives and are byte-identical.
- `dotnet build RingworldTechDemo.csproj --no-restore` passed with no warnings or errors.
- Headless import validation passed for all 30 variants and their four authored LODs, image/buffer dependencies, and preview geometry/material checks.
- Vulkan Forward+ preview validation passed on the RTX 4070. The gallery, fir closeup, and billboard captures were visually reviewed.
- The existing production `tree_lighting_shadow_gpu_test.gd` also passed, including night, totality, and both terrain shadow receivers. This verifies the existing trees, not production integration of these new assets.

The original ZIPs remain in the project root; exact `.gitignore` entries keep these duplicate archives out of future additions. The generated `.godot/` cache is already ignored. Source/assets occupy approximately 246 MB after this import, excluding archives, generated caches/builds, captures, and the conflict backup directory; this is not a measurement of Git history or exported-game size.
