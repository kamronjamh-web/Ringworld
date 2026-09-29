# Terrain detail implementation

Implemented 2026-09-29, following the [terrain detail research](terrain-generation-detail-research.md). The shared C# height model now supports `ringworld_surface_detail_v3`: independent foothills, intermediate mountain spurs, and optional directional gullies. New runtime-generation worlds use this recipe by default. Existing worlds keep their frozen definition.

The follow-up [CPU optimization implementation and measurements](terrain-generation-cpu-optimization.md) removes redundant sampling work while retaining exact heights. Two optimized C# comparisons on the i7-13700H measured 24-26% less generation time for the default-mode mountain and foothill tiles. Existing worlds benefit without a recipe change; gameplay FPS acceptance remains separate.

## Try it

1. Open [TerrainGenerationPreview.tscn](../scenes/TerrainGenerationPreview.tscn) in Godot and press **F6**, or run [TerrainGenerationPlayable.tscn](../scenes/TerrainGenerationPlayable.tscn) for the curved Terrain3D view.
2. Press the **grave key**, select **Terrain3D**, and expand **Detail mountain shaping**. The recipe identity should be `ringworld_surface_detail_v3`. **Reset recipe** creates the new defaults if an explicitly loaded older recipe is active; it discards that preview's current tuning.
3. Start with **Ridged spurs + gullies**. Compare **Ridged spurs** to see the simpler, lower-cost alternative, then try **Swiss spurs + gullies** for warped crest structure. Shape edits regenerate the preview after a short pause.
4. Use **Save recipe**. To see that geography in gameplay, create a **new Certified Ring world** and select **Terrain3D runtime generation (Certified only)** in the advanced launcher. The launcher applies the new world's seed to the saved recipe.

The default preview file is now `user://terrain_generation_preview/recipe_v3.tres`. Older preview files remain available at their original paths; they are not automatically migrated. A saved gameplay world's shape controls remain frozen, and its grave-key status now identifies the recipe version. Reopening an old world will therefore retain its old mountains.

Inspect the playable view or the 2 m detail patch when judging small landforms. The regional overview samples at 512 m and cannot resolve the smaller gullies.

## Controls and composition

| Setting | New default | Purpose |
| --- | --- | --- |
| Mountain detail mode | Ridged spurs + gullies (2) | Modes 0/1 use ridged/Swiss spurs; modes 2/3 additionally carve gullies. |
| Foothill amplitude / scale | 100 m / 1,500 m | Broad inland relief, including terrain below the old 2,000 m detail threshold. |
| Spur amplitude / scale | 220 m / 1,000 m | Intermediate features on mountain flanks. |
| Erosion strength / scale | 60 m / 256 m | Bounded downward carving guided by approximate ridge slope. |
| Erosion octaves | 3 | Successively smaller gully structure; the sanitizer retains a nominal 16 m minimum scale. |
| Swiss warp | 0.35 | Accumulated gradient displacement in the Swiss modes. |

These are bounded coefficients before masks, not promised local relief measurements. Foothills fade in between 50 and 250 m of base height, are reduced in mountain belts, and avoid protected basin floors. Spurs use a separate mountain coverage mask. Gullies additionally use slope guidance and only subtract height. The existing jagged micro-detail remains restricted to exposed alpine terrain. The height sanitizer reserves headroom for the added positive layers within the existing -8,000 to +8,000 m envelope.

The Swiss mode is a project-specific adaptation inspired by [de Carpentier's procedural extensions](https://www.decarpentier.nl/scape-procedural-extensions): finite-difference noise gradients progressively warp later ridged octaves. It is not an exact implementation of that article's formula. The gully mode is an original periodic directional-cell filter informed by the research comparison; it is not a port of Runevision's complete erosion filter. Neither method simulates water, sediment, or connected river drainage.

## Architecture and compatibility

- [TerrainGenerationHeightModel.cs](../scripts/Terrain/Generation/TerrainGenerationHeightModel.cs) remains the single height authority. GDScript previews, runtime samples, RF payloads, and curved collision consume that result. No separate shader displacement or GDScript terrain formula was added.
- [TerrainGenerationParameters.cs](../scripts/Terrain/Generation/TerrainGenerationParameters.cs) validates the v3 schema and includes all new settings in the canonical recipe hash. The v1/v2 formulas, canonical serialization, and hashes remain intact. The outer gameplay generator stays at version 17; the frozen recipe and hash distinguish v3 geography.
- [terrain_generation_recipe.gd](../scripts/Terrain/Generation/terrain_generation_recipe.gd) supplies `create_detail_default()` for new work. Its plain resource constructor retains the historical v2 identity so older `.tres` files with omitted default properties keep their meaning.
- The existing worker, cache, native publication, edit persistence, and collision scheduling remain authoritative. New noise objects belong to each height-model instance. Directional cells wrap periodically around the ring; worker code does not modify Terrain3D resources.
- [RingLiveTuningDrawer.GenerationPreview.cs](../scripts/ui/RingLiveTuningDrawer.GenerationPreview.cs) exposes the settings using existing recipe persistence and debounced regeneration. The sampler also reports `foothill_height`, `spur_height`, and `erosion_height` for diagnosis.

## Validation

The implementation was reviewed and tested with Godot 4.7.2 Mono and .NET 8. The following passed:

| Check | Result |
| --- | --- |
| C# build | Zero warnings and errors. |
| Shared reference math | 79,926 comparisons; maximum error approximately 4.5e-13 m. |
| Existing coastal checks | Profile and eight transects passed. |
| New detail math | 2,120 checks: frozen legacy samples/hashes, schema validation, headroom, periodicity, grid overlap, point/grid agreement, layer activation, and protection masks. |
| New playable integration | 13 headless checks; 15 GPU checks including captures, actual menu events, saved recipe reload, and curved collision agreement. |
| Existing preview smoke | 35 checks, including saved recipe loading, explicit reset, drawer behavior, superseded requests, and scene exit. |
| Runtime world smoke | New v3 world creation, edits, save/reload, and rejection of mismatched/corrupt definitions passed. |
| Gameplay stream test | 121 checks across the legacy and runtime-generation backends. |

The legacy golden fixture contains 128 samples captured before the model change, plus the original recipe serialization and hashes. Corrupt-input smoke cases intentionally log load errors. The local sandbox also reports a certificate-store error; older world/stream fixtures report an ObjectDB exit warning. Passing assertions do not establish that these unrelated diagnostics were repaired.

Build with `dotnet build RingworldTechDemo.csproj --no-restore -v:minimal`. Run the headless checks with a Godot Mono executable, replacing `godot` below with its path:

```text
godot --headless --path . --script res://tools/terrain_generation_shared_math_test.gd
godot --headless --path . --script res://tools/terrain_generation_coastal_test.gd
godot --headless --path . --script res://tools/terrain_generation_detail_test.gd
godot --headless --path . --script res://tools/terrain_generation_detail_playable_test.gd
godot --headless --path . --script res://tools/terrain_generation_preview_smoke.gd
godot --headless --path . --script res://tools/terrain_generation_world_smoke.gd
godot --headless --path . --script res://tools/terrain3d_gameplay_stream_test.gd
godot --path . --resolution 1920x1080 --script res://tools/terrain_generation_detail_playable_test.gd -- --capture
```

Tests write isolated fixtures and reports under `.godot/terrain-generation-tests/`. Those generated files are ignored by Git; the tests and golden fixture are portable repository files.

## Observations and remaining acceptance

Matched plain-material captures of v2 and v3 at the same mountain location showed additional channels and narrower ridge structure with gullies enabled. Swiss mode further changed spur and crest shape. Existing small square facets were visible in the v2 baseline too; that material/normal appearance was not changed by this height-field work. These captures were inspected by the implementing agent; user visual acceptance remains open.

At one seed-1337 foothill sample with base height 1,336.51 m, the old alpine detail mask was zero. The new foothill, spur, and erosion contributions were +16.36, +41.80, and -4.34 m, respectively. This confirms that meaningful local relief can now exist below the old elevation cutoff.

On the test machine (Intel Core i7-13700H, RTX A1000 6 GB laptop), a single 128-by-128 height-grid timing was 17.521 ms for v2 and 47.972 ms for default v3. This is CPU generation time for one selected mountain patch, not a frame-time benchmark or an average. Its along-around RMS neighbor slope increased from 0.257 to 0.350, while the patch's peak-to-trough relief changed from 234.02 to 230.92 m: the added structure does not simply raise the entire height range.

- [x] Implement versioned shape layers and integrate the existing preview/gameplay paths.
- [x] Validate legacy definitions, menu persistence, RF/curved collision integration, and streaming contracts.
- [x] Inspect controlled close-range comparisons.
- [ ] Obtain user visual acceptance across representative landscapes and far/near transitions.
- [ ] Measure cold/warm travel frame times and coverage deadlines on the target desktop RTX 4070 system at 1080p. No 60 FPS claim is made by this change.

For a cheaper comparison, select **Ridged spurs** first; it avoids both Swiss finite-difference work and gully evaluation. If CPU generation limits travel speed, optimize measured sampling hotspots or reduce gully octaves before increasing worker counts. Connected drainage, physical erosion, and final material polish remain later work.
