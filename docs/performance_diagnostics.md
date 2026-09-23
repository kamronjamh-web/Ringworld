# Runtime performance diagnostics

Restart the integration scene after rebuilding the C# project.

- **F6** toggles the compact performance overlay.
- **F7** adds the existing detailed streaming display.
- **Grave/backtick → Diagnostics** opens the collapsible live controls, graphs,
  sortable timing rows, workload context, and capture actions.

Lightweight recording starts enabled. Detailed GPU intervals start disabled.
Monitoring settings are saved to `user://ring_performance_telemetry.tres`.
The overlay visibility is temporary rather than a saved graphics preference.

## Reading the measurements

CPU rows are elapsed wall time per completed call or job, not percentages of
hardware utilization. A worker can take a long time without blocking a rendered
frame. Rows marked **inclusive** contain their child calls: do not add parent and
child timings together, or sum parallel jobs as a frame total.

The live frame summary and graphs use up to the last 300 retained frames and show
the actual elapsed window. Timing rows summarize retained completed events, with
mean, p95, maximum, count, and freshness. Capacity limits can shorten the retained
event window; the Memory and workload section shows dropped samples and storage.
An old worst call is not the current cost of that system.

Main-viewport CPU timing measures rendering work, not all C# scripts or physics.
Main-viewport GPU timing is also not the whole scene: the separate far-ring
viewports are not included. The report retains additional engine process,
physics, and render-setup measurements without treating them as additive totals.

Detailed GPU mode adds asynchronous timestamp intervals for:

- FFT cascade dispatches, aggregated by source GPU frame;
- vegetation compute culling/compaction, excluding its CPU buffer uploads;
- the Sunshine view-0 cloud prepass, raymarch, reconstruction, and composition.

These are scoped GPU intervals, not the complete cost of drawing water, trees,
or clouds. Cloud-shadow generation, individual tree shadows, materials, and SSR
are not separately attributed. GPU source-frame identifiers are preserved;
delayed GPU results must not be matched to the current CPU frame by array index.
Unsupported GPU timing is unavailable rather than measured zero.

`Diagnostics.Collection`, `Diagnostics.Summary`, and `Diagnostics UI refresh`
measure portions of the observer's CPU cost. They do not measure the full overhead
of all timing hooks, GPU timestamp queries, UI rendering, or report serialization.
Use a controlled profiling-off/on comparison before claiming a total overhead.

## Capturing a slowdown

Leave monitoring enabled, reproduce the problem, then use **Capture slowdown
report**. Export is manual: no continuous disk recording runs in the background.
The JSON contains retained frames, timestamped completed events, summary rows,
and workload context. Frame context includes movement speed, terrain workers and
upload queues, tree-shadow renderer counts, shadow atlas size, and render scale.
Context is refreshed at 5 Hz, not independently measured every frame.

The default history target is 30 seconds with a 4 MiB persistent-buffer budget.
The memory setting controls bounded frame/event/producer storage; transient
snapshot, UI, and JSON serialization allocations are additional. Lower caps or
high event rates may retain less history. Changing the storage budget resets the
affected buffers. Memory polling runs at 1 Hz and display refresh defaults to
5 Hz. Computed cache/mesh/shadow allocation sizes are estimates, not GPU timings.

Reports use unique `ring-performance-*.json` names under
`user://generated/reports/diagnostics`. Export refuses to exceed 20 reports or
64 MiB of matching report files. It does not delete old reports automatically.
The capture action reports the output path or error in the drawer.

## Validation

- Build: `dotnet build RingworldTechDemo.csproj --no-restore`.
- Headless behavioral checks: run Godot with
  `--headless --path . --script res://tools/performance_diagnostics_contract_test.gd`.
- Rendered Certified Ring check: run Godot with
  `--path . --rendering-method forward_plus --resolution 1280x720 --script res://tools/certified_performance_diagnostics_smoke.gd`.

The rendered check configures the Certified Ring preset, removes its temporary
scene's save manager, and isolates diagnostics settings/reports under `.godot`.
It uses stationary/fast-travel/stationary benchmark states and validates live
CPU history, FFT GPU timestamps, UI availability, and JSON export. This is a
functional test, not an uncontaminated FPS benchmark or proof of a specific
performance bottleneck.
