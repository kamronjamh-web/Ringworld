# Biome Workshop

## Authoring direction

Create reusable biome recipes in a Godot editor dock, inspect them in a
10,000 m by 10,000 m preview, and save them as project `.tres` resources.
World assignment is a separate integration stage. Previewing or saving a recipe
does not change existing worlds, terrain edits, or the persisted `TerrainBiomeId`
values.

## Open the workshop

1. Build the C# project and reopen it in Godot. **Biome Workshop** is enabled in
   Project Settings → Plugins and adds a dock on the right.
2. Choose **Open** and select
   `resources/biomes/temperate_woodland.tres`, or choose **New**.
3. Choose **Open 10km Preview** to open the workshop's large **Biome Preview**
   view inside Godot. Use **Overview** and **Detail** to frame the terrain.
   Hold right mouse to look and use **WASD**, **Q/E**, and **Shift** to fly;
   **O** frames the overview and **F** frames the detail.
4. Change the recipe controls. The last completed preview stays visible while
   a replacement builds. Use **Save As** to create an asset under
   `resources/biomes/`; **Duplicate** starts a new biome identity.
5. Optionally run `scenes/BiomeAuthoringPreview.tscn` with **F6**. The grave key
   opens the same authoring controls. Hold right mouse and use **WASD**, **Q/E**,
   and **Shift** to fly; **O** frames the overview and **F** frames the detail.

The dock edits a working copy. Draft recovery is stored in the project's ignored
`.godot` directory; authored assets are written only by an explicit save.
Deleting `.godot` also removes unsaved recovery drafts.

The first version concentrates on procedural placement rules. Painting individual
trees or sculpting a particular world region is a different authoring operation
and is not part of this version.

### Controls

- Terrain mean elevation, broad variation and feature size, detail amplitude and
  feature size.
- Humidity and temperature as climate inputs to vegetation suitability.
- Tree density, minimum spacing, clustering, clearings, maximum slope, height
  range, and maple/fir/pine proportions.
- Grass density, blade height, and color.
- Ground texture from the existing grass, forest floor, sand, tundra, and rock
  palette, with tint and texture scale.
- Fixed seed for comparing the same layout while changing a recipe.

The overview covers the full 100 square kilometres. A bounded detail patch shows
actual imported tree models and grass blades; it does not instantiate vegetation
over the entire area. The preview is an authoring surface, not a gameplay world
with collision, voxel edits, hydrology simulation, or production vegetation
streaming. Full world material blending and biome assignment need their own
integration and compatibility checks.

The overview uses coarse terrain samples with finer samples around the detail
patch. Trees cover a 256 m square and grass a 48 m square at the center. Grass
uses a simple blade material so height, density, and color can be compared;
production wind and grass shading are a later integration step. The first tree
load can take longer while Godot loads the imported models and shaders.
The detail preview caps vegetation at 1,200 trees and 12,000 grass blades.
Very dense forests are sampled across the whole patch once the tree cap is
reached; their complete placement rules remain in the saved preset.

The existing terrain sampler supplies a seeded moisture contribution. Preview
height comes from the authored elevation and variation controls; setting both
amplitudes to zero produces exactly the chosen mean elevation. Vegetation treats
height at or below zero as water in this isolated preview.

Humidity is not an instantaneous rain intensity. Keeping climate separate from
preview weather lets a future weather control show a dry day in a humid biome
without moving the forest.

## Useful next additions

1. **Diagnostic overlays:** height, slope, moisture, tree suitability, and
   exclusion reasons under the cursor.
2. **Compare and history:** side-by-side saved/current presets, undo/redo, and
   seed bookmarks.
3. **Biome boundaries:** preview adjacent recipes and control gradual terrain,
   material, and vegetation transitions.
4. **More placement rules:** elevation bands, shore setbacks, canopy-dependent
   undergrowth, deadwood, rocks, flowers, and species-specific spacing.
5. **Lighting and weather:** time of day, overcast, rain, fog, wind, and seasons
   using the game's shared atmosphere and weather owners.
6. **Ecology and gameplay:** ambient sounds, wildlife, resources, spawn rules,
   and traversal checks.
7. **World assignment:** map recipe identities to world-generation configuration
   with explicit save/cache versioning and a before/after preview.

For world integration, reuse the existing persistent vegetation tiles. Do not
add another production tree streamer. Existing biome enum values remain stable;
authored recipes have separate identities.

## Validation and acceptance

```powershell
dotnet build .\RingworldTechDemo.csproj --no-restore
godot --headless --path . --script res://tools/biome_authoring_regression_test.gd
godot --headless --path . --script res://tools/biome_authoring_preview_test.gd
godot --headless --editor --path . -- --biome-workshop-editor-smoke
godot --path . --script res://tools/biome_authoring_preview_test.gd -- --capture
godot --editor --path . -- --biome-workshop-editor-smoke --biome-workshop-editor-capture
```

Use the installed Godot Mono console executable if `godot` is not on `PATH`.
Preview captures are written under `.godot/biome_workshop/`. Add
`--biome-workshop-editor-capture` to the editor smoke arguments for an editor
screenshot (requires a rendered editor).

Automated checks should cover resource round trips, stable identity and duplicate
identity, rejection of incompatible/invalid resources, deterministic sampling,
tree spacing and slope rules, 10 km preview bounds, superseded rebuilds,
editor plugin lifecycle, and the runtime grave-key tuning drawer. Rendered
captures must be inspected separately from headless results.

Verified on 2026-09-15 with Godot 4.7.2 Mono:

- C# build: 0 warnings and 0 errors.
- `BIOME_AUTHORING_REGRESSION=PASS`: save/reload including colors and slope,
  stable identities, invalid-resource rejection, immutable snapshots,
  deterministic terrain/trees, spacing, species, water, and climate rules.
- `BIOME_AUTHORING_PREVIEW=PASS` under Vulkan/Forward+: full 10 km bounds,
  preset changes, canceled/superseded rebuilds, actual grave-key control binding,
  water/empty cases, the full grass-density range, and dense forests covering
  all four detail-patch quadrants.
- `BIOME_WORKSHOP_EDITOR=PASS` in the rendered Godot editor: dock construction,
  save/reopen, an isolated preview view, overview/detail framing, mouse look,
  keyboard movement, and movement stopping when a dock text field gains focus.
- Existing forest-sampler and vegetation live-tuning regressions passed.
- Rendered editor and runtime captures were inspected. The editor capture is
  `artifacts/biome-workshop-editor.png`; runtime captures are under
  `.godot/biome_workshop/`.

The editor still reports an existing `EditorPlugin` shutdown leak. The enabled
SunshineClouds2 dock allocates an unfreed helper in
`addons/SunshineClouds2/Dock/CloudsEditorController.gd:125`; that plugin is outside
this authoring change.

- [ ] Human acceptance of editor workflow and preset controls.
- [ ] Human acceptance of terrain, materials, grass, and tree appearance.
- [ ] Sustained editor performance on the target machine.
- [ ] World-generation assignment and compatibility with saved worlds.

The plugin follows Godot's [editor plugin structure](https://docs.godotengine.org/en/stable/tutorials/plugins/editor/making_plugins.html).
