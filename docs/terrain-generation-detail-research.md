Terrain generation detail research, prepared 2026-09-29.

This document records the original research and experiment proposal following the [runtime generation plan](terrain3d-runtime-generation-plan.md). Implementation followed on 2026-09-29: see [terrain detail implementation](terrain-generation-detail-implementation.md) for the shipped v3 layers, controls, compatibility policy, measurements, and remaining acceptance. References to current defaults and proposed work below describe the pre-v3 baseline.

The proposal addresses smooth mountains and weak local terrain relief. It retains the selected curved Ringworld domain, 2 m near sampling, floating-point height pipeline, and desktop RTX 4070 / 1080p / 60 FPS target. Performance was unmeasured when this research was prepared.

The user confirmed that the issue is visible in the runtime-generation gameplay world. Focus comparisons on that backend and its frozen recipe. The isolated preview is the proposed experiment environment, not the reported source of the problem.

The recommended direction is to give foothills and mountain slopes their own landform layers, then compare Swiss turbulence with a slope-guided procedural erosion filter. Increasing the existing micro-noise amplitude alone would not address the missing landform structure. Full hydrological simulation is a later regional-generation option.

**What the current code establishes**

The authoritative implementation is now [TerrainGenerationHeightModel.cs](../scripts/Terrain/Generation/TerrainGenerationHeightModel.cs), configured by [TerrainGenerationParameters.cs](../scripts/Terrain/Generation/TerrainGenerationParameters.cs). The [GDScript height model](../scripts/Terrain/Generation/terrain_height_model.gd) delegates to that C# implementation. Extend this shared authority rather than introducing another terrain formula in GDScript or a rendering shader.

The following values are checked-in [recipe defaults](../scripts/Terrain/Generation/terrain_generation_recipe.gd), not a measurement of the user's saved world or tuned preview:

| Component | Current default | Implication to investigate |
| --- | --- | --- |
| Continental base scale | 300 km | Organizes broad geography; cannot supply local hills on its own. |
| Mountain belt scale | 80 km | Large changes in mountain coverage. |
| Valley noise base scale | 36 km, derived as 0.45 times mountain scale | Broad valley modulation rather than a connected network of local drainage channels. |
| Ridge noise | 8 km base scale, four octaves, lacunarity 2 | Nominal octave scales of 8, 4, 2, and 1 km. |
| Secondary FBm | 60 m amplitude; 256 m base scale, three octaves | Nominal scales of 256, 128, and 64 m, strongly attenuated by the detail mask. |
| Micro FBm | 4 m amplitude; 32 m base scale, two octaves | Nominal scales of 32 and 16 m, using the same mask. |
| Detail elevation ramp | Starts at 2,000 m; reaches full elevation weight at 4,500 m | Both secondary and micro detail are zero below 2,000 m, regardless of local relief. |
| Land flattening | Exponential strength 3 | Intentionally lowers intermediate elevations in the continental transfer function. |

Noise scales are characteristic input scales, not exact periodic wavelengths or guaranteed distances between summits.

The current detail mask is the product of mountain coverage, an elevation ramp, ridge strength, and inverse valley weight. Multiplication can suppress detail much more than the amplitude controls suggest. For illustration only, four weights of 0.5 produce 0.0625: the 60 m secondary coefficient becomes 3.75 m before multiplying by noise. This is not a measured distribution of weights in the current scene.

There are therefore two separate hypotheses to test: insufficient local hills outside high alpine terrain, and insufficient organized detail on mountain flanks. There is also a transition between the kilometre-scale ridge octaves and the heavily masked 256 m secondary field that deserves a dedicated spur layer.

The [regional preview](../scripts/Terrain/Generation/terrain_generation_preview.gd) uses 512 m samples; its selected detail patch uses 129 by 129 samples at 2 m spacing. The regional map cannot faithfully show 16-256 m features. Judge experimental local geometry in the detail patch and playable view, then inspect far LOD separately. The overview's sampling limit does not explain the reported gameplay observation. This research did not inspect the affected world's saved recipe, camera location, or active LOD.

The [runtime-generation sampler](../scripts/Voxel/Phase3/RingTerrainSampler.SurfaceGeneration.cs) constructs this shared model from the world's recipe JSON and samples the canonical 2 m presentation lattice. That confirms these shaping functions are relevant to the identified backend; the actual frozen parameter values still need to be recorded for a reproducible comparison.

**Methods worth comparing**

| Method | What it contributes | Proposed role |
| --- | --- | --- |
| Additional FBm / warped ridges at intermediate scales | Local hills, spurs, and varied relief with familiar controls | First, small experiment using the existing sampler architecture. |
| Swiss turbulence | Fine features follow the structure of larger ridges | First candidate for a more alpine mountain shape. |
| Directional procedural erosion | Branching gullies and intervening ridges | Second candidate, especially for mountain flanks. |
| Drainage-network terrain construction | Connected valleys and watersheds across a region | Later macro-generation work if geographic connectivity remains weak. |
| Physically based analytical erosion | Uplift and river erosion shape entire mountain systems | Longer-term regional preprocessing or cached generation. |

[Giliam de Carpentier's Swiss turbulence article](https://www.decarpentier.nl/scape-procedural-extensions) shows direct comparisons with ordinary ridged noise. It accumulates noise gradients to warp subsequent octaves and modulates their amplitudes. The result has more organized mountain features. The same article's Jordan variant favors rounder, weathered forms; Swiss is the more relevant first comparison for the current complaint. These are erosion-inspired procedural constructions, not simulations of water and sediment. The article supplies comparison images and implementation details.

[Rune Skovbo Johansen's erosion filter](https://blog.runevision.com/2026/03/fast-and-gorgeous-erosion-filter.html), published March 30, 2026, takes a base height and slope direction, then builds successive scales of directional gullies. Its design preserves larger ridges while adding branching detail, with controls for ridge and valley rounding. Individual points can be evaluated independently, making it a promising match for streaming. Its internal slope directions are deliberately modified for appearance; derive collision normals and measured slopes from the resulting height field, not those internal directions. This models erosion's appearance rather than establishing hydrologically valid rivers. An [author-hosted archive copy](https://blog.runevision.com/2026_03_30_archive.html) also contains the article and comparisons.

There is a [C# implementation of the erosion filter](https://github.com/lpmitchell/AdvancedTerrainErosion) targeting Unity/Burst. It is useful reference material, but its dependencies, coordinate mapping, and performance do not transfer automatically to Godot. Any adaptation must retain applicable attribution and notices. No code was copied during this research.

[Génevaux et al., Terrain Generation Using Procedural Models Based on Hydrology](https://doi.org/10.1145/2461912.2461996) constructs a drainage network, derives watersheds and river characteristics, and combines terrain patches around them. This addresses geographic organization beyond surface roughness. For this project, it suggests storing regional drainage decisions above the streamed height tiles, then sampling those decisions consistently from every consumer.

[Tzathas et al., Physically-based analytical erosion for fast terrain generation](https://www-sop.inria.fr/reves/Basilic/2024/TGSC24/) uses an analytical stream-power erosion formulation. Its two-dimensional extension still involves a multigrid-accelerated iterative process. It should not be treated as a cheap, stateless replacement for a per-point noise call. Evaluate it as regional preprocessing if more physical landform evolution becomes a goal.

**Proposed height-field composition**

Keep broad continental shape, sea level, coast shaping, and mountain coverage as the starting definition. Add a distinct local-relief layer and a mountain-shaping stage before final height publication. Treat the following as proposed experiment ranges, not real-world measurements or approved defaults:

| Layer | Characteristic scale to explore | Height coefficient to explore | Placement |
| --- | --- | --- | --- |
| Local hills / foothills | 0.5-3 km | 20-150 m | Selected inland regions, outside protected basin floors. |
| Mountain spurs | 0.25-2 km | 50-300 m | Mountain flanks and range edges. |
| Branching gullies | 64-512 m | 5-80 m | Sloping mountain terrain with a separate gully mask. |
| Rock-scale geometric roughness | 16-64 m | 0.5-4 m | Exposed alpine terrain. |
| Smaller surface texture | Below the geometric limit | Material parameters | Rock materials and normal detail, after geometry is convincing. |

These ranges overlap deliberately; do not enable every maximum simultaneously. Coefficients are not resulting peak-to-trough relief. Broad hills, directional carving, and signed roughness have different output distributions. Track the combined height envelope and slopes before selecting defaults.

The original design intentionally kept jagged micro-detail exclusive to alpine peaks. Preserve that rule for micro-detail while introducing broad, smoother foothill relief as a separate layer. Mountain-flank gullies would be a deliberate extension of the original shaping design, with their own controls. Valley bottoms and coastal zones should retain protection masks; coastal attenuation must prevent new islands or shoreline shifts unless explicitly intended.

Use landform and slope context to determine gully strength. Absolute elevation alone is insufficient: a mountain face can be steep at 1,000 m, and a plateau can be flat at 4,000 m. Keep geological style regional so every landscape does not become an identical serrated range. Local hills must be independently useful even where the alpine mask is zero.

Maintain explicit headroom within -8,000 to +8,000 m. The current sanitizer reserves space for existing secondary and micro amplitudes; it must account for additional bounded layers or a revised composition rule. A final clamp remains a safety net, with clamp counts reported, rather than the routine way to flatten excessive peaks.

**Integration and performance constraints**

FastNoiseLite remains suitable for base geography, masks, and the first local-relief experiment. Godot's [FastNoiseLite](https://docs.godotengine.org/en/stable/classes/class_fastnoiselite.html) and inherited [Noise API](https://docs.godotengine.org/en/stable/classes/class_noise.html) do not expose analytical noise-gradient samples. Swiss turbulence and directional erosion require additional implementation work rather than switching a fractal enum.

For an initial erosion-filter prototype, compute the base-height gradient with a fixed, recipe-defined step in canonical meters. Four central-difference neighbours add substantial cost if the entire expensive terrain function is reevaluated at every sample. Bulk jobs can reuse a canonical base grid and its halo where the exact stencil matches the reference point sampler. This reuse does not directly solve Swiss turbulence's arbitrary, warped per-octave gradient queries; profile those separately. Do not let tile edges, mesh LOD, or scheduling change the derivative definition or authoritative point heights.

Analytical derivatives are an alternative if numerical gradients become the bottleneck. They require consistent differentiation through the actual mapping and shaping functions. An implementation using approximate slopes should label them as such and validate the resulting heights independently.

All new fields and directional patterns must preserve the periodic around-ring boundary. The existing model embeds around-ring positions on a cylinder for noise sampling. A plain two-dimensional cell pattern placed on wrapped coordinates can still jump at the seam. Use a periodic construction or a seam-compatible embedding with tangent-plane directions, and test values and finite-difference slopes across the wrap. Preserve precision through the canonical coordinate mapping; casting multi-million-meter positions to float before high-frequency sampling can lose useful horizontal detail.

Keep worker inputs immutable and worker noise/filter state private. Workers produce heights and diagnostics; the existing main-thread publication path owns Terrain3D resources and curved collision. New geometry must reach the authoritative height field, RF payloads, far terrain, and physics. Shader-only displacement would require a separate consistency design and is not the first experiment.

At 2 m spacing, a nominal 16 m feature spans eight sample intervals. Retain the present geometric scale floor for the first comparison. Far representations need appropriate filtering or a defined LOD approximation and transition; simply evaluating fine noise at widely spaced vertices can alias. Preserve the same world definition while deriving those coarser representations. Normal maps can improve close-up shading but cannot supply mountain silhouettes or physical relief.

The RTX 4070 target does not determine CPU generation throughput. Record the actual CPU, worker count, full graphics settings, route, and cache state. Measure sampling cost separately from native map commits, curved collider preparation/installation, and rendering. Existing worker and upload budgets remain relevant; a heavier height function can miss streaming deadlines even when GPU frame time is unchanged.

Frozen gameplay recipes and generator identities are intentional compatibility boundaries. Changing defaults affects a new preview or newly created world, not an existing world's geography. Any promoted algorithm needs a new supported recipe identity and corresponding terrain-definition/cache versioning as required by the established contract. Preserve the old implementation for old identities, along with saved edits and existing load validation.

Expose experimental shape settings through the existing grave-key Terrain3D menu and recipe persistence. Keep these definition settings separate from user performance preferences. Display each layer's contribution, detail masks, slope, and recipe identity so ineffective sliders and stale recipes are visible.

**Bounded comparison plan**

1. Record the affected runtime-generation world's recipe/hash, canonical location, camera, and graphics settings. Reproduce that definition in the isolated comparison, then capture a plain-material baseline in the 2 m playable view and a regional overview. Do not substitute the original Terrain3D backend or assume current defaults match the frozen recipe.
2. Add a local-relief-only variant using existing noise primitives and separate masks. This is the lower-complexity alternative and establishes whether better scale distribution solves most of the complaint before adding erosion machinery.
3. Compare that variant with a Swiss mountain-shaping variant and a directional-erosion variant. Use matched seeds, locations, approximate relief envelopes, cameras, lighting, and LOD. Compare each in isolation before combining algorithms.
4. Inspect representative coasts, lowlands, foothills, mountain faces, high peaks, and valley floors. Include the ring seam, tile boundaries, and far/near transitions. Human visual acceptance remains an explicit step.
5. Measure height bounds/clamp frequency, detail-mask coverage, local height distributions, detrended local relief over fixed windows, slope distributions, and contribution by band. Use final-height gradients for these metrics.
6. Validate deterministic point/bulk results, worker-order independence, scene exit, stale revision rejection, saved recipe reload, and agreement of committed terrain with curved collision. Retain safe player coverage when generation is late.
7. Measure generation and travel frame-time distributions on identical routes, including first visits and warmed caches, with only one benchmark process running. Record p50/p95/p99 and worst frame time, queue pressure, and missed coverage deadlines. Promote a method only after quality and cost are both understood.

The immediate next implementation should be a bounded preview comparison, beginning with independent foothill/spur relief. Swiss turbulence and directional erosion are competing follow-up candidates, not assumptions that both must ship. Connected drainage and physically based regional erosion remain later options if local detail alone does not produce the desired geography.
