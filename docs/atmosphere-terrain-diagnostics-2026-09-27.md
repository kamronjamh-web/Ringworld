# Atmosphere and terrain diagnostics — 27 September 2026

This was a diagnostics and research pass. No production shader, generator, gameplay setting, or native addon was changed. Builds, temporary worlds, captures, and benchmark reports were written under `.godot`. The requested visual direction is both natural distance haze and a stronger cinematic option, with adjustable controls.

## Main findings

1. The runtime terrain stream evaluates the complete height model three times per in-bounds lattice texel while producing height and biome data. A fused sampling API is the strongest generation optimization candidate established by the code review.
2. The forested gameplay route is primarily constrained by main-view rendering. Its main-viewport GPU p95 was 110.15 ms. Terrain support remained continuous while walking; improving generation alone will not remove that rendering cost.
3. Atmosphere behavior differs across terrain tiers. Near Terrain3D has no shared atmosphere integration, regional proxies use the curved-shell model, and scaled far materials use a uniform shroud. The current 30 km clear-distance default means the missing near integration is not, by itself, evidence of a visible seam at the normal near handoff.
4. The current 32-cell collision tiles completed preparation faster than 16-cell tiles in the focused native fixture. Smaller tiles reduced individual step duration, but increased total staging time.

## Captures and measurements

The two gameplay captures ran sequentially with the other game closed. Environment: Godot 4.7.2 .NET, Forward+, Vulkan, 1920×1080, RTX 4070, i9-14900KF. Both used seed 1337, coastal-v2 runtime generation, Desktop High, full-resolution clouds, and the normal controller at 12 m/s. The route began at around 16,443,476 m / across 38,692 m and traveled 512 m outward and back. The fixture uses isolated world state and disables swimming for ground-support checks; this route ended on land at approximately 326 m elevation.

| Measurement | Full scene | Reduced scene, exploratory |
| --- | ---: | ---: |
| Frame intervals | 1,204 | 1,575 |
| Frame median | 57.79 ms | 44.10 ms |
| Frame p95 | 111.91 ms | 75.67 ms |
| Frame p99 | 122.67 ms | 76.38 ms |
| Worst frame | 130.31 ms | 77.67 ms |
| Main viewport GPU p95 | 110.15 ms | 74.58 ms |
| Far viewport GPU p95 | 0.72 ms | 0.71 ms |
| Ground probes / misses | 5,171 / 0 | 5,170 / 0 |
| Coverage waits during measured travel | 0 | 0 |
| Cumulative allocation during travel | 27.84 GB | 16.65 GB |
| Cumulative GC pause during travel | 1.99 s | 0.69 s |

The existing `--isolate` switch disables processing for vegetation, selected cloud/weather systems, FFT water, the minimap, and adaptive certification. It leaves the Sunshine compositor and terrain rendering active. The full capture did not fall back from Desktop High, so both ended with 4,096×256 global terrain detail. The reduced capture emitted one `Parameter "particles" is null` renderer error. Treat it as an exploratory subsystem-removal probe, not a clean single-variable benchmark or a standalone Terrain3D performance measurement.

Both runs recorded one coverage wait while establishing the distant route origin, before measured travel. Both ended with zero pending Terrain3D jobs, two-worker high-water marks, 3 MiB of canonical tile cache under the 8 MiB limit, and all 128 global terrain segments ready.

The full scene's retained GPU cloud-compute samples averaged 2.17 ms with 2.60 ms p95. Tree and grass culling compute averaged 0.46 ms and 0.05 ms; these do not include the cost of drawing their geometry or shadows. The final workload snapshot reported about 31.62 million render primitives, 4,334 draw calls, 288,899 grass roots, and a 16,384-pixel shadow atlas. The reduced scene still reported 19.74 million primitives and 3,349 draw calls. This supports further draw/shadow attribution rather than assigning the remaining frame cost to generation.

Retained CPU rows included grass update at 10.57 ms p95 and tree update at 8.52 ms p95. These are inclusive per-call timings. Telemetry dropped older samples, GPU events carry delayed source-frame IDs, and viewport timing can omit custom compute. Do not sum these numbers as a frame total or treat the final telemetry window as the entire route.

Artifacts:

- [Full-scene report](../.godot/terrain-generation-tests/m6-5609683/travel-benchmark.json), [starting view](../.godot/terrain-generation-tests/m6-5609683/terrain-start.png), [return view](../.godot/terrain-generation-tests/m6-5609683/terrain-end.png).
- [Reduced-scene report](../.godot/terrain-generation-tests/m6-4099274/travel-benchmark.json).
- [Focused native/preview report](../.godot/diagnostics-20260927-native-incremental.json).

The screenshots show the current lighting and weather, not a controlled noon/dusk haze comparison. They cannot establish the appearance of a proposed atmosphere model. Existing optional-extension, sandbox log/certificate, UID, and full-scene shutdown resource diagnostics remain in the logs; the focused native benchmark completed with no test failures.

## Terrain generation opportunities

### 1. Fuse runtime height and biome sampling

The exact current call chain is:

- `RingTerrain3DGameplayStream.TileJob.Run` samples a height and then biome weights for every 2 m texel ([stream source](../scripts/Terrain/Surface/RingTerrain3DGameplayStream.cs)).
- The height call reaches the exact-lattice fast path and performs one `TerrainGenerationHeightModel.SampleDiagnostics` evaluation.
- `GetBiomeTextureWeights(around, across)` creates a runtime terrain sample. That evaluates model diagnostics once directly and once again to obtain the presentation height. It also evaluates the existing climate field ([runtime sampler](../scripts/Voxel/Phase3/RingTerrainSampler.SurfaceGeneration.cs), [height model](../scripts/Terrain/Generation/TerrainGenerationHeightModel.cs)).

A cold 384×384 in-bounds window therefore performs 442,368 complete runtime model evaluations, plus climate sampling. A future fused API could return height and packed biome weights from one model result at exact lattice vertices. This is an opportunity to remove redundant work, not a measured threefold speedup: climate work, interpolation, packing, scheduling, and publication remain.

The new full capture's initialization diagnostics recorded 1,044.01 ms total, including 188.11 ms height caching and 673.50 ms biome caching. The two new outward recenter requests took 501.81 / 383.93 ms to produce prepared data, then 4,284.95 / 3,857.98 ms from request to published readiness. Cached return requests produced data in 80.30 / 66.91 ms but still took 2,336.50 / 2,083.93 ms to become ready. These timings overlap and must not be added. `last_generation_ms` is request-to-prepared-payload latency, not an accumulated pure-noise CPU measurement.

Any fusion must preserve the existing 2 m triangle lattice, periodic wrapping, width-edge clamp, float-to-integer rules, RGBA8 truncation, climate altitude adjustment, immutable recipes, and fractional edit snapshots. Existing golden, surface-definition, native, stream, and walking fixtures provide the validation path.

### 2. Reduce measured allocation and packing costs

The stream assembles a full height/biome window, packs 18 images, and prepares collision geometry. Main-thread staging validates and duplicates images and copies heights into its window representation ([packing](../scripts/Terrain/Surface/RingTerrain3DGameplayStream.cs), [staging](../scripts/Terrain/Surface/RingTerrain3DSurface.Budgeted.cs)). Ownership transfer or reusable staging buffers are candidates; removing validation or sharing mutable active buffers is not justified by these diagnostics.

The largest instrumented allocation subtotal on the full route was **17.85 GB in proxy workers**, compared with 27.84 GB process-wide over the measured route. That counter is published at job completion and can straddle the measurement boundary. It is still strong evidence to profile proxy mesh/sample buffer allocations alongside Terrain3D packing, rather than attributing all GC pressure to Terrain3D.

### 3. Preserve the useful cache and current collision granularity

Each new axial window reused six tiles and generated three. Cached returns reused all nine. No measured walking coverage wait occurred, so increasing cache size or worker count is not the first change to make.

The focused native fixture used synthetic heights, 1 ms upload and 0.75 ms collision budgets, eight measured replacements after warmup, and a maximum of two resident owners:

| Collision tile cells | Preparation wall median | Largest advance call | Activation median |
| --- | ---: | ---: | ---: |
| 16 | 193.72 ms | 1.37 ms | 0.118 ms |
| 32 | 155.87 ms | 1.57 ms | 0.088 ms |

Geometry preparation was outside publication timing. This isolated scene rendered at roughly 4 ms intervals; its absolute readiness times are not expected in the full scene. At slow gameplay frame rates, work spread across dozens of process frames can take seconds despite modest total CPU work. Maintain the 32-cell default while profiling scheduling and shape construction under real render load.

The same fixture's preview-only scheduler produced a cold window in 180 ms with two workers and 96 ms with four; new recenter windows were about 71 / 46 ms, while cached revisits were about 21 ms. This preview path does not include the gameplay biome pipeline, ran worker counts in fixed order, and does not establish that four gameplay workers improve frame pacing.

Upstream Terrain3D recommends bulk region setup followed by map updates, direct image editing for large changes, and updating only modified map types/regions. The project already uses incremental region updates after initial setup; measured recenter uploads were about 0.48–0.83 ms on this route. Native upload is a lower priority than sampling and staging latency here. [Terrain3D data API](https://terrain3d.readthedocs.io/en/stable/api/class_terrain3ddata.html).

Some generic upstream advice does not transfer directly: the project already has a custom minimal shader, its curvature needs the patched culling margin, and native planar collision is deliberately disabled in favor of curved collision. Turning those features off or replacing curved collision with the upstream default could reintroduce visibility/support errors. [Terrain3D technical tips](https://terrain3d.readthedocs.io/en/stable/docs/tips_technical.html), [collision documentation](https://terrain3d.readthedocs.io/en/stable/docs/collision.html).

## Atmospheric haze diagnosis and direction

The shared model has a 2.5 km boundary layer, a 20 km upper atmosphere, a ground visibility ramp from 30 to 220 km, and an additional distance-opacity cap of 0.68 ([model](../scripts/Voxel/LOD/RingAtmosphereModel.cs), [shader](../materials/ring_atmosphere.gdshaderinc)). It computes finite-cylinder path lengths, then uses a day/night color blend. It does not model the directional sun/view scattering response needed for convincing forward haze and sunset coloration.

The active [Terrain3D shader](../materials/terrain3d_ring_surface.gdshader) and [material setup](../scripts/Terrain/Surface/RingTerrain3DSurface.cs) do not bind this model. Adding that interface would make future closer haze consistent, but with the current 30 km clear zone it should not visibly fog nearby terrain. Regional proxies already use the model. Scaled far terrain, water, hull, and clouds select the constant-shroud branch, with width gating and a viewer-altitude fade ([far viewport](../scripts/Voxel/LOD/RingScaledFarViewport.cs)). That branch sacrifices per-ray depth variation for precision and cost.

Ordinary environment fog is normally off. The sky and terrain use separately authored palettes, and far color is rendered through a separate environment before becoming the main background canvas. Any unified atmosphere pass must account for physical distance, exposure/AgX, cloud ordering, and existing material haze to avoid applying extinction twice. The recently added far-depth texture is useful infrastructure, but does not by itself make a new atmosphere compositor correct.

Recommended sequence, for later implementation:

1. **Establish consistent inputs and controls.** Give Terrain3D, proxies, far terrain, water, and sky a common atmosphere parameter set in physical metres. Expose persisted grave-menu controls and natural/cinematic presets. Include visibility, density, altitude falloff, aerosol strength, forward scattering, sun warmth, and a separately budgeted local-fog option.
2. **Improve distance haze first.** Preserve nearby contrast while adding gradual depth separation and sky/sun-aware scattering. Replace the far uniform tint with a stable ring-aware approximation evaluated from camera-relative/canonical data. Match the main and far views under one exposure before judging colors. Godot's aerial-perspective and sun-scatter controls demonstrate these visual goals, but its ordinary depth fog needs adaptation for this project's scaled view. [Godot environment fog](https://docs.godotengine.org/en/4.7/tutorials/3d/environment_and_post_processing.html#fog).
3. **Add optional local cinematic fog.** Restrict volumetric mist/light shafts to a modest local range and give them an independent quality control. Godot volumetric fog has finite range; extending it spreads its resolution, while extra slices/resolution cost GPU time. This is an optional layer over coherent distant haze. [Godot volumetric fog](https://docs.godotengine.org/en/4.7/tutorials/3d/volumetric_fog.html).
4. **Evaluate a LUT atmosphere only if the simpler model falls short.** Hillaire's reference implementation builds atmosphere lookup tables and provides comparison with a path tracer. Bruneton's implementation adds tested precomputed scattering and configurable density profiles. Both are useful references, but their planetary geometry is not a drop-in fit for an inhabited ring's inner cylindrical air volume and finite width. Adapting ray paths and sun visibility is part of the work. [Hillaire reference implementation](https://github.com/sebh/UnrealEngineSkyAtmosphere), [Bruneton implementation](https://ebruneton.github.io/precomputed_atmospheric_scattering/).

Rayleigh/Mie-style controls can support both requested looks: clearer air and subtle distant blue scattering for the natural preset; stronger aerosol scattering and forward sunlight for the cinematic preset. This is a proposed visual direction, not a result already implemented or benchmarked. [Epic's atmosphere model and artistic controls](https://dev.epicgames.com/documentation/en-us/unreal-engine/sky-atmosphere-component-in-unreal-engine).

Acceptance should use matched noon, sunset, night, storm, and high-altitude captures; fixed camera views crossing near/proxy/far ownership; and before/after GPU timings. Do not add an expensive full-screen effect before establishing a render budget on the current forest route.

## Next work to prioritize

1. Fuse the runtime lattice sample and verify identical terrain/biome outputs.
2. Attribute main-view terrain, vegetation drawing, and shadows independently; the reduced scene is not enough to assign their individual costs.
3. Profile proxy allocation and staged publication, then remove demonstrated redundant copies/work while retaining revision and ownership guarantees.
4. Implement the shared haze controls and a ring-aware natural/cinematic model, followed by optional local volumetrics within a measured budget.

No part of this proposed implementation was applied during this diagnostics pass.
