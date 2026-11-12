# World generations

The terrain and vastness plans share one generation ledger. A generation records the features a world gets. A generator ID identifies the recipe format and model. Every recipe through v8 belongs to generation 1. Recipe v9 uses the same ID, `ringworld_surface_world_v9`, for generation 1 and every later generation. New worlds take the latest frozen generation by default, and saved worlds keep their own recipe.

## Ledger

| Generation | Step | Plan | Features | Frozen date | Test world | Fingerprint row |
|---:|---|---|---|---|---|---|
| 1 | Baseline, every recipe through v8 and v9 at generation 1 | Shared | None | 2026-11-09 fingerprint capture, 2026-11-10 ledger entry | None | `gen1-v8-s1337` |

The production feature table holds only open (unfrozen) features so far; `water.rivers` became open on 2026-11-11 (T2R-2). The following IDs reserve bit positions for their work orders. A feature gets a production entry when its work begins, with `FrozenIn = 0` for open or the generation number once frozen. Generations are assigned in freeze order.

| ID | Bit | Plan | Ledger state |
|---|---:|---|---|
| `water.rivers` | 0 | T2 | Open |
| `water.current` | 1 | T3 | Reserved |
| `ground.surface` | 2 | T4 | Reserved |
| `ground.river_beds` | 3 | T4 | Reserved |
| `scatter.rocks` | 4 | T5 | Reserved |
| `scatter.river` | 5 | T5 | Reserved |
| `vegetation.forest_structure` | 6 | T6 | Reserved |
| `vegetation.ground_cover` | 7 | T6 | Reserved |
| `vegetation.far_canopy` | 8 | T6, including V2 forest grain | Reserved |
| `vegetation.riparian` | 9 | T6 | Reserved |
| `landform.meso_relief` | 10 | T7, including still ponds | Reserved |
| `landform.coastal_variety` | 11 | T7 | Reserved |
| `landform.band_limited_sampling` | 12 | T7 | Reserved |
| `vegetation.new_species` | 13 | T8 | Reserved |
| `air.haze` | 14 | V1 | Reserved |
| `scale.ladder` | 15 | V2 | Reserved |
| `light.wall_shadow` | 16 | V3 | Reserved |
| `light.path_visibility` | 17 | V4 | Reserved |
| `weather.rain_curtains` | 18 | V5 | Reserved |
| `light.eclipse_sweep` | 19 | V6 | Reserved |
| `air.mist_lid` | 20 | V7a contingent | Reserved |
| `scale.mast_chains` | 21 | V7b contingent | Reserved |
| `light.macro_trace_range` | 22 | V7c if the visible trace range grows | Reserved |
| `scale.wall_engine_meshes` | 23 | V7d contingent | Reserved |
| `light.mast_shadows` | 24 | V7e contingent | Reserved |
| `water.small_streams` | 25 | T9a contingent | Reserved |
| `water.deltas_and_falls` | 26 | T9c contingent | Reserved |
| `landform.badland_stamps` | 27 | T9d contingent | Reserved |
| `landform.sinkholes` | 28 | T9e contingent | Reserved |
| `water.headworks` | 29 | T9g contingent | Reserved |
| `air.valley_mist` | 30 | T9h if reviewed separately | Reserved |

Use stable lowercase `domain.capability` IDs, with underscores inside words. Keep each assigned bit for the lifetime of the ledger. For a 32nd capability, add `ringworld_world_features_hi` and extend the ledger and sampler masks together. Use bits 0–30 in the high word and pass it to any compute kernel that needs it. The retirement floor is 1 today. Keep generation 1 and the last three frozen generations loadable.

## What a world loads

The canonical v9 recipe includes the v8 body, a positive integer `world_generation`, a sorted, unique comma-separated `review_features` string, and the base climate fields. A normal world has an empty review string. The ledger resolves the features frozen at or before the recipe generation plus its explicit review features. The launcher validates the stored recipe and uses that generation for an existing slot. Its advanced controls choose the generation for a new Certified runtime world. Open features are selectable in debug builds at the latest frozen generation. World cards show the generation and mark review worlds.

The base climate segment records the frequencies, terrain and climate influence settings, altitude lapse, rain-shadow strength and macro-region size listed in the design. The scalar climate values use float32 canonicalization except for the integer `macro_region_size_m`. The root sampler applies them to its private settings before building noise, packing GPU climate inputs and computing the terrain hash. The saved top-level `continental_contrast` mirrors the recipe value at float32 precision. [Climate field table](work-orders/terrain-vegetation/T1.md#c1-climate-is-part-of-the-world-recipe) · [Sampler setup](../scripts/Voxel/Phase3/RingTerrainSampler.SurfaceGeneration.cs)

The launcher publishes `WorldGeneration.Current` through `WorldGenerationBridge` on the main thread before the configured game enters the tree. Legacy loads publish generation 1 with mask 0, which is also the default before the first publication. The renderer receives the mask in the integer shader global `ringworld_world_features`. Worker threads read `WorldGenerationNumber` and `WorldFeatureMask` from their own sampler, including independent copies. Later features pass bits to their compute kernels as needed. [Launcher](../scripts/world_size_launcher.gd) · [Publication](../scripts/Terrain/Generation/WorldGeneration.cs) · [Bridge](../scripts/Terrain/Generation/WorldGenerationBridge.cs) · [Sampler](../scripts/Voxel/Phase3/RingTerrainSampler.cs) · [Shader globals](../project.godot)

A settings owner can call `WorldGenerationSettingsFiles.Resolve(userPath, fromGeneration)` after publication. Older generations use the base path. Starting at the specified generation, the owner uses the sibling `<stem>_g<NN>.tres`. The helper seeds that sibling once from the base file when one exists and preserves edits to either file. The feature owner handles first-use defaults. Harnesses that install explicit settings paths bypass the helper. [Settings path helper](../scripts/Terrain/Generation/WorldGenerationSettingsFiles.cs)

The full v9 recipe hash identifies saves and derived caches. Site placement and schedules use `NaturalSiteIdentity`, derived from the actual v8-compatible body of a v9 recipe. This keeps emitter sites and shots, climate-machine placement and Weather Lung placement aligned with the equivalent v8 world. Keep site and drainage height inputs on their documented natural height path when adding visible shaping features. [Recipe identity](../scripts/Terrain/Generation/TerrainGenerationParameters.cs) · [Site identity audit](work-orders/terrain-vegetation/T1.md#c5-content-and-identity-audit)

## Adding recipe fields

Append a `RecipeFieldSpec(Name, FeatureId, Kind, Default, Min, Max)` for each extension field. The field is active when the recipe has the owning feature. Canonical JSON appends active fields in table order. Parsing requires every active field with its declared type and range and rejects inactive fields. Only the factory supplies defaults. Gate the model with `HasWorldFeature` before reading the field. GDScript gets the active schema from the C# bridge. [Field schema](../scripts/Terrain/Generation/WorldRecipeFields.cs) · [GDScript recipe](../scripts/Terrain/Generation/terrain_generation_recipe.gd)

For example, a synthetic test feature `test.relief` frozen at generation 2 could declare `RecipeFieldSpec("test_relief_m", "test.relief", RecipeFieldKind.Float, 3.5, 0, 20)` and `RecipeFieldSpec("test_relief_octaves", "test.relief", RecipeFieldKind.Int, 2, 1, 4)`. These abbreviated JSON fragments omit the rest of the base recipe.

```json
{"world_generation":1,"review_features":""}
{"world_generation":2,"review_features":"","test_relief_m":3.5,"test_relief_octaves":2}
```

An eligible review recipe includes the same fields while the feature is open. Once the feature freezes, keep its fields, order, kinds, defaults and ranges fixed. A later change to the feature takes a new step with its own fields.

`TerrainGenerationParameters.WorldDefault(generation, seed, review)` builds a validated recipe with defaults for active fields. Use `WithFields(overrides)` to make a validated copy for calibration. It accepts overrides to known active fields. The recipe golden stores canonical JSON SHA-256 and definition hashes for seeds 1337, 90210 and 2147483000. Each capture adds a row for a frozen generation. The fingerprint golden separately records sampled world content. Its first row is v8, and the test also checks that v9 generation 1 matches that row's content. [Recipe golden](../tools/fixtures/world_generation_recipe_golden.json) · [Fingerprint golden](../tools/fixtures/world_generation_fingerprint_golden.json)

## Pinning tests

Name a generation explicitly in tests. Launcher-driven tests call `world_generation_pin.pin(N, review_csv)` before adding the launcher to the scene tree. The launcher reads the selection in `_ready`. The optional review CSV defaults to empty. The helper sets `ringworld/runtime/surface_world_generation` and `ringworld/runtime/surface_review_features`, and `unpin()` restores their prior values. Pin 0 for legacy recipes, including the showcase. A positive pin selects v9 at that generation. Unset or a negative value takes the default, which is legacy when either of the v7 landforms or v8 Weather Lung project settings is false. For headless C# fixtures, use `EmitterSiteTestWorlds.Gen17WorldRecipe`, `CreateGen17World` or `CreateProductionSettings`. [Pin helper](../tools/world_generation_pin.gd) · [Test fixtures](../tools/EmitterSiteTestWorlds.cs)

Landscape audits accept `--generation=N` and `--review-features=a,b` after Godot's `--` separator. Review IDs require an explicit latest frozen generation. No flags or `--generation=0` selects the pinned v8 world. An explicit `--generation=1` selects v9 generation 1. A v9 report records the selected generation and review features, and its folder ends in `_gNN` or `_gNN-review-a+b`. The same pose coordinates and camera angles apply across generations. The harness uses the selected world's ground height for poses specified relative to the ground. [Landscape work order](work-orders/terrain-vegetation/T1.md#t1-9-generation-flags-in-the-landscape-harness-and-the-relief-and-drainage-tools)

## Freezing a step

A step corresponds to a phase the user signs off on. Phases signed off together can share a step. Bug fixes to broken behavior, performance work with proven parity, tuning inside an open step and new settings whose default reproduces prior behavior are exempt. A visible change to a frozen feature takes a later step. The grass Cards default is the approved global exception. [Terrain plan](terrain-vegetation-revamp-plan.md#22-world-generations-one-version-ladder-for-both-plans) · [Vastness freeze rules](vastness-plan.md#114-which-phases-take-a-step)

1. Get the user's signoff in a review world.
2. Append the next row in `WorldGenerationLedger.Production` and set each included feature's `FrozenIn` to that row's generation. The last row determines `LatestFrozen`, and the included extension fields are now final. [Ledger](../scripts/Terrain/Generation/WorldGenerationLedger.cs)
3. Build with `./tools/use_godot_48.ps1 -Build`. Set `$generation` to the new generation number and `$godot` to the pinned console executable `./artifacts/engine-upgrade/Godot_v4.8-dev7_mono_win64/Godot_v4.8-dev7_mono_win64_console.exe`. Capture the two new rows, then check them in the normal runner. Each capture refuses an existing row.

   ```powershell
   & $godot --headless --path . --script res://tools/world_generation_fingerprint_test.gd -- --capture "--generation=$generation"
   & $godot --headless --path . --script res://tools/world_generation_recipe_test.gd -- --capture "--generation=$generation"
   ./tools/run_tests.ps1 -Label world_freeze -Tests world_generation_fingerprint_test,world_generation_recipe_test
   ```

4. Run `./tools/run_tests.ps1 -Label world_freeze -Tests world_generation_contract_test`.
5. Capture two or three reference views per machine for the new generation, using the phase's poses and the clear weather pin. Keep older references. Set `$poses` to the comma-separated pose IDs, use an isolated APPDATA profile for the capture, and take the GPU queue for this direct invocation.

   ```powershell
   ./tools/gpu_queue.ps1 -Label world_freeze_look -Wrap {
       & $godot --path . --resolution 1920x1080 --script res://tools/landscape_look_baseline.gd -- "--generation=$generation" "--poses=$poses" --times=noon --weather=clear
   }
   ```

6. Have the user choose one of the three slots and free it through the existing UI if it holds an ordinary world. Close the project's windowed Godot sessions before using the tool on the real profile. Set `$step` to the ledger step name and `$slot` to the chosen slot number. Preview with `-DryRun`, then run `./tools/new_generation_world.ps1 -Step $step -Slot $slot` to create `g<NN>-<step>-s1337`. The tool requires explicit `-Replace` to replace a slot already named with a `gNN-` or `review-` prefix. Use `-AppData` for isolated tool validation.
7. Remove the step's review worlds with `./tools/new_generation_world.ps1 -Step $step -DeleteReview`. This matches the `review-<step>-` prefix. Add `-DryRun` to list the matching slots before removal.
8. Update the ledger and feature tables in this document with the freeze date, test world and fingerprint row.
9. Compare one generation 1 pose against its prior reference with `./tools/media/ffmpeg_tools.ps1 compare -Path $current -Reference $reference`. Include the heatmap in the phase report and share it and the images that show visible changes in chat. Use the same pose, time and weather, and judge the heatmap against same-weather repeat variation. Keep the cited evidence under `.godot/` and clean up intermediate captures.
10. Advance the retirement floor when the retention window moves. Remove retired-only code branches and fixtures and retire the corresponding test worlds. Keep shared paths that surviving worlds still need, along with historical ledger and golden rows. Resolve fingerprint verification for retired rows before the first retirement, since the public factory rejects retired generations.
11. Run `./tools/run_tests.ps1 -Label world_freeze -Suite gate` and finish with `./tools/prune_test_output.ps1 -Apply`.
