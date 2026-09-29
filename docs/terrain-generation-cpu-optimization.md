# Terrain generation CPU optimization

Implemented 2026-09-29 for the [v3 detail implementation](terrain-generation-detail-implementation.md). The first pass removes redundant CPU work in the shared height model. Two optimized C# comparisons on the i7-13700H laptop measured **24-26% less generation time for default-mode mountain and foothill tiles**, with bit-identical RF samples. The user's i9-14900KF / RTX 4070 desktop has not been measured.

## Implemented first pass

[TerrainGenerationHeightModel.cs](../scripts/Terrain/Generation/TerrainGenerationHeightModel.cs) now:

- Computes each distinct cylindrical projection once per point and reuses it across noise fields and across-direction derivative samples. The original wrapping, trigonometric operations, and float cast order remain intact.
- Precomputes immutable octave scales, weights, derivative steps, cell widths/counts, and the continental flattening denominator. Legacy and inactive modes allocate no gully cache or octave arrays.
- Caches one 3-by-3 gully pivot stencil per octave: 27 pivot pairs for the default three octaves, at most 36. This is bounded per-model storage with no per-point allocation or dictionary lookup. Cache keys include the octave, around/across cell, and ring turn; traversal order cannot change the result.
- Skips the final gully octave's unused gradient feedback. It cannot steer another octave, so the height calculation retains the same operations and order.

Each generation worker owns its model; shared sampler callers use the existing serialization. A model's mutable gully cache must not be accessed concurrently without that protection. No locks were added to the sampling loop. Terrain3D publication, collision scheduling, recipe serialization, world hashes, detail settings, 2 m spacing, and RF storage retain their existing contracts. Existing saved worlds benefit after rebuilding and restarting; this optimization does not require a new world or recipe.

## Measured before and after

Both the frozen original and final model were compiled into the same isolated Godot Mono project with **C# Optimize=true**; the assembly reported `IsJITOptimizerDisabled=false`. Each process used three warmups and twelve measured pairs per mode/location, alternating before/after order. Every measured grid was checked for finite and bit-identical float samples outside the timer. The workload is a 128-by-128 height grid at 2 m spacing, seed 1337. Model setup, biome work, collision, and GPU/native publication are excluded.

Two sequential runs of the final implementation gave the following median times. Percentages are reductions in elapsed tile-generation time, calculated separately within each run.

| Detail mode / location | Run 1 before -> after (ms) | Run 2 before -> after (ms) | Time reduction across runs |
| --- | ---: | ---: | ---: |
| Ridged spurs / foothill | 33.28 -> 24.36 | 33.23 -> 23.63 | 26.8-28.9% |
| Ridged spurs / mountain | 36.92 -> 26.47 | 35.91 -> 26.53 | 26.1-28.3% |
| Swiss spurs / foothill | 89.69 -> 70.36 | 88.90 -> 70.00 | 21.3-21.6% |
| Swiss spurs / mountain | 95.09 -> 74.06 | 94.81 -> 74.56 | 21.4-22.1% |
| **Default ridged spurs + gullies / foothill** | **67.34 -> 50.59** | **66.51 -> 49.17** | **24.9-26.1%** |
| **Default ridged spurs + gullies / mountain** | **70.57 -> 53.37** | **70.08 -> 52.59** | **24.4-25.0%** |
| Swiss spurs + gullies / foothill | 122.46 -> 97.89 | 119.04 -> 97.27 | 18.3-20.1% |
| Swiss spurs + gullies / mountain | 114.31 -> 98.35 | 125.48 -> 98.79 | 14.0-21.3% |

The [portable raw report](benchmarks/terrain-cpu-optimization-2026-09-29.json) includes all samples, ocean results, extrema, recipe JSON, grid origins, hashes, build metadata, and both complete runs. The local comparison project and frozen source are under `.godot/terrain-generation-tests/cpu-harness/` and are ignored by Git; the report and regression fixture are portable. The existing single-version benchmark below can measure the current implementation on another machine.

Godot was 4.7.2 Mono, the harness targeted .NET 8, and the hosted runtime reported 9.0.20. The editor remained open, with gameplay stopped. Laptop clocks, thermals, power policy, and core affinity were not controlled, so treat the paired reductions as local evidence, not a guaranteed throughput figure. An earlier intermediate implementation measured only 13-17% savings in the expensive modes during a much slower run; its absolute times are not comparable with these final runs. This is an optimized CPU-only harness, not a full exported-game benchmark, and it does not establish a frame-time or 60 FPS improvement.

## Regression validation

The [CPU parity test](../tools/terrain_generation_cpu_parity_test.gd) checks a [frozen pre-optimization fixture](../tools/fixtures/terrain_detail_v3_cpu_golden.json). Its sixteen recipes cover all four modes, three seeds (including a seed beyond 32 bits), custom scales and weights, zero warp/gain, clamped octave counts, and four-octave/headroom extremes. Locations include both sides of the ring seam, ocean, foothill, mountain, and across-ring boundaries. It validates exact RF hashes, diagnostic float64 bytes, canonical JSON/hashes, shuffled cache eviction, and independent samplers. The fixture was captured before rebuilding the modified model; normal test runs never overwrite it.

Final validation passed:

- C# build: zero warnings/errors.
- Frozen v3 CPU parity: 215,794 checks.
- Shared reference math: 79,926 checks; maximum error approximately 4.5e-13 m.
- Coastal profile/transects and detail math: passed; 2,120 detail checks.
- Runtime surface definition: 2,668 checks, including independent worker copies and curved collision agreement.
- Gameplay streaming: 121 checks across both backends.
- Detail playable integration: 13 headless checks.

The existing [travel benchmark](../tools/terrain3d_gameplay_travel_benchmark.gd) also completed 512 m outward and back at a requested 60 m/s on the default v3 world, using two generation workers. Its [portable summary](benchmarks/terrain-cpu-travel-2026-09-29.json) records zero coverage waits, zero support-ray misses across 1,225 probes, and zero ungrounded diagnostic time. The streamer completed six generated tiles and recorded thirty cache hits. This used the main project's Debug build with `--headless --isolate`; it checks traversal and support, not rendered FPS or an alpine route's throughput. Its frame intervals are retained in the summary for transparency but must not be compared with the optimized CPU harness or a rendered game.

Run the new regression with a Godot Mono executable after building:

```text
dotnet build RingworldTechDemo.csproj --no-restore -v:minimal
godot --headless --path . --script res://tools/terrain_generation_cpu_parity_test.gd
godot --headless --path . --script res://tools/terrain3d_gameplay_travel_benchmark.gd -- --isolate --speed=60 --distance=512
```

The certificate-store error, deprecated interpolation warning, and stream-test ObjectDB exit warning remain separate diagnostics; passing the tests does not imply those were repaired. Full-environment cold/warm travel frame-time acceptance on the target desktop remains open.

## Second pass: shared startup sampling

Implemented 2026-09-29 after the first pass. The synchronous gameplay startup cache now shares one runtime sample for height and biome weights when the height field proves that its baseline is the same sampler method. Matching hashes alone are insufficient: custom baseline delegates retain the separate path, along with finite-value handling, height clamping, and edit overlays.

`RingTerrainSampler.GetTerrainSample` also reuses its existing diagnostic height at canonical even 2 m coordinates. Odd, negative, multi-turn, and across-boundary coordinates retain the previous interpolation/wrapping path. This restriction matters because wrapping before a warped model sample can round differently from wrapping after it. A repeated erosion-noise lookup in climate evaluation now reuses the identical earlier result.

The [cache benchmark](../tools/runtime_surface_cache_benchmark.gd) measures the actual private startup cache methods, comparing the fused method-group baseline with an equivalent lambda that deliberately takes the separate path. Two sequential runs on the i7-13700H used the main project's **Debug / Optimize=false** assembly. Each location used two warmups and eight alternating measured pairs, with full height/biome bit comparisons after each pair outside the timer.

| Default v3 location | Run 1 separate -> fused (ms) | Run 2 separate -> fused (ms) | Cache-time reduction |
| --- | ---: | ---: | ---: |
| Foothill | 1210.58 -> 836.74 | 1257.26 -> 869.50 | 30.8-30.9% |
| Mountain | 1281.11 -> 919.60 | 1311.69 -> 936.45 | 28.2-28.6% |

Each cache contains 147,456 samples (384 by 384) at 2 m spacing. These measurements isolate cache fusion in the current sampler. They exclude RF packing, native upload, collision construction, and gameplay frame time; they are not whole-startup or exported-game measurements. The earlier 128-by-128 optimized height-grid benchmark is a different workload, so its percentage improvement must not be added to these reductions. [Both raw reports](benchmarks/terrain-cpu-pass2-2026-09-29.json) preserve all paired timings and metadata.

The [pass-2 frozen fixture](../tools/fixtures/runtime_terrain_cpu_pass2_golden.json) was captured using the compiled pre-change game assembly before rebuilding. Its regression passed all twelve cases: v1, v2, and every v3 detail mode with two seeds, exact RF and packed-biome hashes, point diagnostics, canonical recipe/hash identity, and seam/boundary coordinates. The cache test passed 6,580,246 comparisons per benchmark run, including both across directions, seams, out-of-range values, custom same-hash baselines, edit overlays, and `Clear()` restoration. The C# build passed with zero warnings/errors; runtime surface definition (2,668 checks), gameplay streaming (121 checks), and surface preference persistence also passed after the production changes.

```text
godot --headless --path . --script res://tools/runtime_terrain_cpu_pass2_test.gd
godot --headless --path . --script res://tools/runtime_surface_cache_benchmark.gd -- --benchmark
```

The related [surface smoothing investigation](terrain-surface-smoothing.md) addresses visible grid-cell lighting separately. It adds shader work over existing samples and does not increase terrain-generation resolution.

## Original assessment: establish the right baseline

The earlier 17.521 ms v2 / 47.972 ms v3 comparison measured one 128-by-128 height grid in the checkout's development build. MSBuild evaluates `Configuration=Debug`, `Optimize=false`; `Configuration=ExportRelease` evaluates `Optimize=true`. Measure the optimized export before treating development timings as a shipping limit. The comparison above holds optimization enabled for both models; it does not measure a Debug-to-release speedup.

Height-grid time is not frame time: tile jobs already run through `WorkerThreadPool`, while native map publication and collider installation have separate main-thread budgets. Profile sampling, climate/biome work, packing, collider preparation, uploads, and installation separately. On a cold visit, additional CPU work can delay coverage or compete with gameplay even when it is off the main thread.

## Historical comparison before optimization

A headless comparison passed on the i7-13700H with three warmup rounds and twelve measured rounds per recipe/location. Models were configured before timing; variants were interleaved, and repeated RF buffers were checked for finite values and identical SHA-256 hashes within each variant. The timed bridge call includes returning its float array; it excludes recipe setup, hash validation, biome generation, GPU publication, and collision. This used the current **Debug / Optimize=false** C# build.

| Recipe variant | Mountain median (ms) | Foothill median (ms) |
| --- | ---: | ---: |
| Old v2 | 21.10 | 19.31 |
| v3 with new layers disabled | 21.04 | 19.35 |
| v3 foothills only | 23.54 | 22.01 |
| Ridged spurs | 26.99 | 26.50 |
| Swiss spurs | 52.06 | 46.97 |
| Ridged spurs + gullies, 3 octaves | 60.38 | 74.47 |
| Ridged spurs + gullies, 2 octaves | 51.05 | 49.16 |
| Swiss spurs + gullies | 85.84 | 87.00 |

All grids contain 16,384 samples at 2 m spacing. Seed is 1337; the mountain origin is around 1,570,796.925 m / across 159,000.125 m and the foothill origin is around 2,356,195.325 m / across 159,000.125 m. An ocean candidate was also measured; new layers are masked there, and medians across variants ranged approximately 28.6-34.9 ms.

These are diagnostic comparisons, not controlled shipping benchmarks. Variance was substantial: the default mountain variant ranged from 53.75 to 106.60 ms. System background activity was not independently verified absent, and no laptop power/thermal policy was standardized. Twelve measurements are insufficient for meaningful tail-latency acceptance. The strongest useful result is that the gully and Swiss paths deserve investigation before sacrificing the comparatively inexpensive foothills. Differences of a few milliseconds between variants should not be treated as precise component costs.

The portable [CPU benchmark](../tools/terrain_generation_cpu_benchmark.gd) recreates these workloads. Its report is written to `.godot/terrain-generation-tests/detail-cpu-benchmark-report.json`. Run with a Godot Mono executable after building; metadata arguments describe the build the operator actually prepared, rather than detecting it:

```text
godot --headless --path . --script res://tools/terrain_generation_cpu_benchmark.gd -- --csharp-build=Debug --csharp-optimize=false
```

## Original implementation order and remaining work

Steps 1-3, the bounded gully-cell cache in step 4, and startup's duplicate height/biome sampling are implemented above. Native-call batching, worker-count experiments, and full-game performance acceptance remain follow-up work. Prefer measuring batch sampling at the C#/native boundary before increasing worker counts or sacrificing terrain detail.

1. **Freeze the current v3 output and benchmark an optimized build.** Retain reference heights and RF grid hashes for all four modes, several seeds, coast/basin/alpine locations, ring seams, tile overlaps, and edit/collision cases. Check final float samples exactly when claiming an existing world is unchanged. Record medians and spread after warmup, then use full-route tests for p95/p99 frame time and coverage deadlines.
2. **Reuse identical coordinate transforms.** `TerrainGenerationHeightModel.Sample()` wraps around-ring coordinates, divides by circumference, and calls sine/cosine for every noise lookup. Most fields at a point use the same warped coordinates; the two warp fields also share their input coordinates. Compute each distinct projection once and reuse the exact float inputs to noise. Derivative samples along the across direction share the same around projection too. Retain the existing wrap, double-coordinate arithmetic, and cast order.
3. **Precompute immutable recipe quantities.** Move Swiss/gully octave scales, weights, derivative steps, around-cell counts and widths, and other recipe-only factors into model initialization. `TerrainGenerationParameters.FlattenDenominator` already stores a value that the hot flattening path recomputes. Cache the same expressions without changing arithmetic elsewhere. Replacing cubic `Math.Pow` or changing trigonometric functions requires output comparisons because mathematically equivalent floating-point expressions can round differently.
4. **Reuse cell data and reduce redundant calls.** Gully cells repeatedly hash the same jittered pivots across nearby samples. Evaluate a small bounded per-worker cache of cell data, keyed by octave and canonical cell coordinates. Keep point results independent of cache state, traversal order, and tile boundaries. Separately investigate startup's second model evaluation for biome data: the traveling stream already combines height and biome sampling, but the field-owned startup baseline and biome path still evaluate the model separately. Preserve custom baseline and edit ownership when sharing those results.
5. **Profile the native boundary before choosing a larger port.** Each noise lookup currently calls Godot's native FastNoiseLite from C#. Compare a pinned managed FastNoiseLite implementation with a small native batch sampler if call overhead remains significant. A C++ candidate should generate an entire height tile per call; moving only a scalar noise wrapper does little to reduce call count. Keep Terrain3D publication and world-definition ownership in their current systems. Require parity across the exact noise version, settings, coordinate transforms, and seed conversion before replacing an existing world's evaluator.
6. **Tune streaming with measured frame times.** Compare two and four generation workers separately on the laptop and desktop. Larger concurrency reduces queue latency only when spare CPU capacity exists; it does not reduce total work. Increase cache capacity for revisited routes only when misses justify it. The existing 8 MiB cache accounts for 64 tiles of height/biome payload; completed tile reuse and six-of-nine overlap on a compatible axial recenter already exist. A larger cache cannot avoid first-visit generation.

Godot documents the native interoperability cost of C# access to engine objects in its [C# performance guidance](https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/c_sharp_basics.html#performance-of-c-in-godot). FastNoiseLite has an [upstream C# implementation](https://github.com/Auburn/FastNoiseLite/blob/master/CSharp/FastNoiseLite.cs); its availability is an experiment option, not evidence that it will outperform the current engine-backed implementation or match it bit for bit.

## Changes that trade terrain shape for generation speed

The current default is ridged spurs plus three gully octaves. A cheaper saved recipe can retain foothills and spurs while reducing gully octaves, or choose ridged spurs without gullies. Swiss modes add finite-difference noise calls and should be measured separately. These settings change geography and therefore apply through the existing preview/save/new-world flow; they are not a transparent performance switch for a frozen world.

Coarse gradient grids, interpolation of the broad noise fields, fewer derivative samples, and reduced geometric sampling may produce larger savings, but they also change the authoritative height function. In particular, the current gully derivative is sampled at offsets in warped coordinates. Reading adjacent values from an unwarped tile grid is not the same derivative. Any approximation needs explicit error bounds, seam/collision validation, visual comparisons, and a separately versioned terrain definition where required.

A GPU generator is a later option. Collision and CPU terrain queries still need consistent heights, so GPU work requires a deliberate readback/cache strategy and CPU/GPU parity checks. The first pass can retain the current 2 m geometry, RF data, and visual result by eliminating redundant CPU work.

## Acceptance status

- [x] Identical canonical recipe JSON/hash and unchanged saved-world definition contracts.
- [x] Exact RF output comparisons against the frozen v3 baseline, plus legacy checks.
- [x] Deterministic point/grid results, periodic seams, cache eviction, and independent worker results.
- [x] Edit, committed-height, curved collision, and safe-coverage regression checks.
- [x] Repeated optimized C# before/after tile benchmarks, recorded separately from frame time and GPU time.
- [x] Headless default-world outward/return traversal with no support misses or coverage waits.
- [ ] Full-environment cold/warm travel p95/p99 frame times and 60 FPS acceptance on the i9-14900KF / RTX 4070 desktop at 1080p.
