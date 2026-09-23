# Grass compute benchmark

`tools/grass_compute_benchmark.gd` is an isolated rendered grass-compute fixture, not a full-game FPS benchmark. It requests Forward+ at 1280x720 with vsync and the FPS cap disabled, and writes only to `artifacts/grass-compute-benchmark/`.

It uses a deterministic 64-tile field with flat and sloped ground, an excluded central path, fixed light/wind, and a normalized walk/pan/fast-turn camera path. Default settings are 262,144 requested roots, 60 warmup frames, 180 timed frames, and three alternating-order repetitions. A production-scale comparison should use 1,048,576 roots. The fixture has 128 possible compute outputs; production reference capture `artifacts/grass-registration-fix/instancing-profile.json` has 1,118,430 unique roots, 464 outputs, and 232 tiles with scheduling budgets. The benchmark refreshes its baseline outputs each moving frame to isolate compute/render behavior rather than streamer scheduling.

## Cases

1. `baseline_per_batch_3_phase`: production per-batch three-phase compute, dense single-blade sources, near and intermediate indirect outputs.
2. `tile_frustum_whole_tile_impossible_lod`: baseline plus conservative whole-tile frustum and impossible-LOD rejection, intended to preserve the baseline image.
3. `solid_four_blade_clumps`: quarter sources, with four-blade solid clumps: 32 near-pass triangles and 16 intermediate-pass triangles per clump, retaining coverage.
4. `sparse_far_static_multimesh`: near compute on every tile plus static intermediate grass from every fourth source. This is a simple LOD-handoff approximation using a wider four-triangle far prototype, not a production streaming replacement.
5. `alpha_card_clumps`: quarter sources with procedural-alpha card clumps in both near and intermediate passes. The alpha mask is diagnostic coverage work, not final art.
6. `grouped_grass_compute_phases`: optional grouped-phase diagnostic backend on the baseline fixture.

Quarter-source variants select every fourth source from the same deterministic dense sequence. Baseline, conservative rejection, and grouped-phase images use a shared canonical walking-height camera after timing ends for parity review.

## Results

`report.json` records source counts, active/submitted batches, candidate tests, dispatch deltas, buffer estimates, CPU submission, wall timing, viewport timings, draw calls, primitives, and GPU timestamp status. `raw-samples.json` includes the frame samples and asynchronous GPU timestamps with source-frame IDs; canonical PNGs are stored beside them.

`GPU.GrassCulling` is an asynchronous RenderingDevice timestamp around only the grass compute scope, not a full-frame GPU time. Missing GPU timestamps stay unavailable and are never replaced with CPU duration. Viewport CPU/GPU intervals cover broader viewport render work. Wall-frame time also includes main-loop and pacing effects. Draw calls and primitives are Godot-reported counters; indirect MultiMesh primitives can represent command/capacity information rather than only visible roots.

## Windows PowerShell

Use the project’s Godot Mono executable and run the SceneTree script in Forward+:

```powershell
$grassGodot = 'C:\Users\Kamron\Downloads\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
dotnet build RingworldTechDemo.csproj --no-restore
& $grassGodot --path . --rendering-method forward_plus --resolution 1280x720 --script res://tools/grass_compute_benchmark.gd -- --grass-compute-roots 1048576
```

For a short validation run:

```powershell
& $grassGodot --path . --rendering-method forward_plus --resolution 1280x720 --script res://tools/grass_compute_benchmark.gd -- --grass-compute-benchmark-quick
```

Supported controls after Godot's `--` separator: `--grass-compute-roots 32768..2097152`, `--grass-compute-warmup N`, `--grass-compute-samples N`, `--grass-compute-repetitions N`, and `--grass-compute-benchmark-quick`. Runs replace reports and matching screenshots in the output directory; copy results first when preserving a comparison.
