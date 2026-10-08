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
viewports are not included. The live summary reports separate outer and regional
far-viewport CPU/GPU render intervals; absent or unsupported measurements show
`n/a`. These renderer measurements may arrive later than the CPU frame and must
not be summed into a device-wide GPU total. Detailed GPU marker mode is not
required for the viewport measurements. The report retains additional engine process,
physics, and render-setup measurements without treating them as additive totals.

Detailed GPU mode adds asynchronous timestamp intervals for:

- FFT cascade dispatches, aggregated by source GPU frame;
- vegetation compute culling/compaction, excluding its CPU buffer uploads;
- the Sunshine view-0 cloud prepass, raymarch, reconstruction, and composition,
  both as the inclusive `GPU.Clouds` interval and as the non-additive child
  intervals `GPU.CloudPrepass`, `GPU.CloudRaymarch`, `GPU.CloudReconstruct`,
  and `GPU.CloudComposite`;
- near/far cloud-to-ground shadow generation (`GPU.CloudShadowNear` and
  `GPU.CloudShadowFar`), when their producers dispatch;
- active rain haze composition (`GPU.RainHaze`);
- terrain sunlight-visibility field generation (`GPU.TerrainCloudSunVisibility`).

These are scoped GPU intervals, not the complete cost of drawing water, trees,
or clouds. Individual tree shadow draws, materials, and SSR are not separately
attributed. An enabled producer may remain inactive, for example a cloud-shadow
producer while the sun is below the horizon; absent markers do not establish zero
cost for an enabled feature. GPU source-frame identifiers are preserved;
delayed GPU results must not be matched to the current CPU frame by array index.
Unsupported GPU timing is unavailable rather than measured zero.

`Diagnostics.Collection`, `Diagnostics.Summary`, and `Diagnostics UI refresh`
measure portions of the observer's CPU cost. They do not measure the full overhead
of all timing hooks, GPU timestamp queries, UI rendering, or report serialization.
Use a controlled profiling-off/on comparison before claiming a total overhead.

## Additive report attribution

Slowdown JSON and far-capture `measurement` now include an `attribution` object;
existing fields remain available. Runtime state records `Engine.MaxFps`, VSync,
display refresh rate, focus, window mode, engine time scale, and command-line
arguments. These values distinguish a configured limiter from rendering or script
work; a frame interval alone does not identify a VSync limit.

Capture-only percentile summaries report count, mean, p50, p95, p99, and maximum
for retained frame, process-monitor, physics-monitor, render-setup, and separate
main/outer/regional viewport intervals. Coverage records the retained frame
window and dropped counts. Detailed GPU category coverage has its own source-frame
count and first/last completion times. CPU and GPU retention may cover different
windows. Missing statistics are JSON `null`, not a measured zero.

The frame history includes global primitive counts alongside draw-call context.
Global engine counters can include other viewports. Main and far viewport
workload snapshots separate visible, shadow, and canvas draws/primitives; these
are last-renderer-frame counters captured at export, without a source-frame token,
and are not a per-frame workload history.

`cloudStages.partitions` groups the four Sunshine stages by GPU source-frame ID.
A complete row requires exactly one retained marker interval for each stage.
Missing, duplicate, invalid, or unknown-frame data cannot be filled from another
frame. Stage percentiles use complete rows only. The inclusive `GPU.Clouds`
parent remains a separate value; shadow generation is outside that partition.
Do not add parent and child intervals, or combine viewport and compute intervals
as a device total. Process monitor samples and inclusive CPU scope rows also
remain separate from renderer intervals; nested or parallel script durations are
not summed into a frame total.

`terrainCloudSunVisibility` records the producer's effective settings, readiness,
generation/edit version, receiver demand and required ranges, pending work, and
GPU dispatch counters. `coverageObserved=false` explicitly means that production
did not read back the atlas to measure coverage. Readiness and requested enable
state do not prove that every traversal is known. The sampled heightfield cannot
represent native overhangs; unknown traversal uses neutral sunlight visibility.
Read producer status together with retained generation markers and settings.

## Capturing a slowdown

Leave monitoring enabled, reproduce the problem, then use **Capture slowdown
report**. Export is manual: no continuous disk recording runs in the background.
The JSON contains retained frames, timestamped completed events, summary rows,
workload context, and a `renderState` capture-time snapshot. `renderState` is
collected only when **Capture slowdown report** is invoked on the main thread;
it adds no per-frame polling and is not a history. It records JSON-safe light,
shadow-handoff, viewport/focus/adapter, canonical explorer, celestial, and
Sunshine cloud-resource settings when those nodes are available. A failed
render-state read is recorded as `renderStateContextError` while the report
still exports. Frame context includes movement speed, terrain workers and
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

## Capturing the far-ring viewports

Use **Prepare far-ring capture** in the grave-key Diagnostics drawer to enable
monitoring, select 60 seconds of history and a 16 MiB telemetry budget, and clear
the previous history. This uses the existing saved diagnostics settings and
preserves the selected **Detailed GPU intervals** mode. Close the drawer while
reproducing the view, then use **Capture far-ring viewports**.

Capture creates a unique `ring-far-viewports-*` folder in the diagnostics report
directory. It contains `report.json` plus `outer.png` and `regional.png` for
available far viewports. The images show the separate viewport contents rather
than the composited main screen. Missing viewports or image failures are recorded
in the report; the drawer reports the output folder or export error. The capture
button is disabled while export is running. Capture itself does not change
monitoring, rendering settings, or history; preparation is a separate action.
When regional far rendering is disabled, `regional.png` is omitted and the report
marks that viewport missing. Capture does not enable it.

Far captures have a separate limit of 20 matching `ring-far-viewports-*` bundle
folders or 64 MiB of matching bundle contents. Existing slowdown reports do not
consume this far-capture allowance. Export refuses further captures at the limit
and does not delete old bundles automatically.

The report combines retained telemetry with capture-time far-viewport state and
counters. Capture-only state describes that instant, not a per-frame history.
PNG texture readback can cause a hitch, so retained timing history is frozen
before image readback and excludes that capture hitch. Viewport GPU values remain
renderer-delayed measurements, not GPU costs assigned to the current CPU frame.
Read outer, regional, and main viewport intervals separately.
Viewport CPU intervals include renderer preparation and compositor callbacks.
Global geometry housekeeping can be charged to the first viewport rendered,
so an outer-viewport CPU interval is not an isolated cost for distant meshes.
The separate far-controller CPU scopes identify its script update work; their
inclusive parent already contains the child stages.

## Serial matched A-B-A captures

`tools/far_ring_ablation_capture.gd` performs opt-in controlled comparisons in the
real Certified gameplay configuration. Run one Godot process at a time. Record
other GPU applications externally; the tool does not enumerate or stop them.
For example:

```powershell
& <Godot_console.exe> --path C:\Projects\Ringworld --rendering-method forward_plus --resolution 2548x1320 --script res://tools/far_ring_ablation_capture.gd -- --detailed-gpu --artifact-label matched --cases vsync-off,outer-render-off,far-process-off,grass-process-off,trees-process-off,sunshine-off,terrain-cloud-shadow-off --warmup-seconds 30 --collection-seconds 10 --settle-seconds 1
```

The runner copies current preference resources into private `.godot` paths before
scene startup, including effective factory/user water and cloud overrides under
the existing runtime loading policy. It does not replace preferences with canned
low-quality settings. The Certified 5 Mm radius / 318 km width, Terrain3D backend,
source-report High baseline, and copied sparse overrides are recorded. The exact
source canonical pose is restored after startup and checked at sample boundaries.
WorldSaveManager is excluded; saved world generation/edits and other slot runtime
state are not restored. The manifest identifies those differences and records
copied-resource hashes rather than implying an exact saved-world reproduction.

Normal authoritative celestial and cloud clocks advance during warmup from the
source report's synchronized time. After warmup, celestial `Paused` and the
session-only `CloudSimulationPaused` hold the same clocks throughout all phases.
Weather queries and presentation processing continue. Shader `TIME` and unrelated
engine animation are not frozen. Each phase waits at least one second after a
mutation, clears retained history, samples for 8-10 seconds, then snapshots
telemetry before image readback. Requested and actual durations, runtime state,
clock values, and effective producer settings are written to the manifests.

Each selected nonbaseline case runs **A-before -> B -> A-after**. B changes one
runtime control, with checks that it remains applied at the sample boundaries.
The control is restored before A-after; all mutations and clock pause state are
restored on completion or failure. No user preference or world save is written
by these mutations. Compare both A windows, retention coverage, readiness, and
actual settings before attributing a difference to B.

| Case label | B mutation |
| --- | --- |
| `baseline` | One baseline phase, without a feature mutation. |
| `vsync-off` | Disable window VSync; retain the existing FPS cap. |
| `gpu-markers-off` | Disable detailed GPU markers/readback requests only; retain CPU telemetry and viewport measurements, then restore the exact prior mode. |
| `outer-render-off` | Set the outer far viewport to `UPDATE_DISABLED`, retaining its last texture. |
| `far-process-off` | Stop only the far controller's process callback; retain viewport rendering. |
| `grass-process-off` | Stop only grass manager processing; retain existing grass renderers. |
| `trees-process-off` | Stop only tree manager processing; retain existing tree renderers. |
| `sunshine-off` | Disable local Sunshine rendering and verify the effective cloud resource is disabled. |
| `cloud-shadow-off` | Disable cloud-to-ground GPU shadow generation. |
| `terrain-cloud-shadow-off` | Disable terrain-to-cloud sunlight visibility, apply tuning, and verify the effective producer is disabled/not ready. |
| `grass-render-off` | Set live grass geometry layer masks to zero; retain processing and restore exact masks. |
| `trees-render-off` | Set live tree geometry layer masks to zero; retain processing and restore exact masks. |

`--cases` accepts comma-separated labels and defaults to `baseline`.
`--detailed-gpu` explicitly enables marker collection; without it the copied
monitoring preference remains authoritative. `--artifact-label` labels the unique
output directory. Warmup defaults to 30 seconds and accepts 0-90 seconds;
collection defaults to 10 seconds and the matched runner requires 8-10 seconds.
Settling defaults to one second and accepts 1-10 seconds.

Each phase has its own report directory under
`.godot/far-ring-gameplay/<label>-<unique-id>/reports/<case>-<role>/`.
Bundles contain `main-telemetry.json`, far `report.json`, available viewport PNGs,
`main.png`, `gameplay-run.json`, and `ablation-phase.json`; the suite directory
contains `ablation-suite.json`, updated after completed phases and on failure with
status, reason, active case, and preserved completed results. Render-only B phases
require nonempty captured geometry and zero live geometry with nonzero layer masks
at both sample boundaries. Disabled or missing viewport timings remain
unavailable rather than becoming zero. The runner does not enable an unavailable
regional viewport. The existing single-run `far_ring_gameplay_capture.gd` remains
available for a normal advancing-clock capture without the A-B-A mutations.

## Cloud work controls

The grave-key drawer exposes **Skip empty cloud mask samples** in
**Quality / Raymarch**, and **Cull off-screen distant cloud patches** in
**Clouds / Distant clouds**. Both use the existing saved cloud overrides and
reset behavior. Reports record their state for comparisons.

Empty-sample skipping avoids weather-texture reads outside the local cloud
altitude band. It preserves density, lighting, fog, ray positions, and temporal
history. The cloud passes still dispatch over the screen; this setting does not
remove covered screen tiles from the dispatch.

Distant-cloud culling partitions the existing shell into eight sectors and each
of the three local far-cloud layers into four wedges. It preserves their source
triangles, UVs, shared materials, and ownership fades. Normal frustum culling can
reject these smaller patches; disabling the setting restores the original
meshes. No new terrain occluders are installed in the far viewport. Patch
submission adds draw overhead when many patches are visible, so compare this
toggle at the same viewpoint using the viewport timings and workload counters.

## Cloud history and terrain sunlight controls

**Shadows / Terrain sunlight in clouds** controls terrain-to-cloud sun visibility,
its strength, global/regional resolution, altitude slices, refresh interval, trace
visit/texel budgets, and sun freshness. **Clouds / Lighting** includes depth-history
rejection and tolerance, cloud-history compatibility, and lightning-history
isolation alongside stationary/moving history weights. These follow the existing
sparse cloud override, save/reload, and reset conventions. Cloud-to-ground shadow
controls remain separate. Enabled compatibility rejection invalidates incompatible
density/lighting history; enabled lightning isolation keeps transient illumination
out of persistent normal cloud history.

## Cloud haze and rain composition

Ordinary haze uses the shared curved, finite ring atmosphere. It measures the
first air shell crossed by the camera ray, separates the denser boundary layer
from upper air, and applies the shared visibility fade and opacity caps. Vacuum
between ring arcs contributes no local fog. High clouds can therefore remain
clearer than low terrain at the same geometric distance.

Sunshine applies this haze after storing its temporal cloud history. Its ray
distance is the representative strongest-density cloud sample, blended with
accepted history and capped by opaque terrain depth. This is an approximation
for rays crossing multiple cloud layers. The distant shell and clipmap evaluate
haze per fragment at physical world positions, accounting for the scaled
viewport. The fallback cloud transmittance mask now applies the same atmosphere
to each cloud contribution; it preserves cloud density and alpha.

Liquid rain uses a separate, depth-aware local haze. The main environment's
compositor orders its rain effect after the cloud provider at POST_TRANSPARENT,
before built-in post-processing and the HUD. The effect modifies the final HDR
scene once, including the far-ring background, without production GPU readback.
The transparent quad remains a fallback when the compositor is unavailable and
is hidden while the compositor owns rain. Provider attachment restores the rain
effect to the end of the list; cloud rebuilds retain one rain owner.

With MSAA enabled, Sunshine resolves its completed cloud display into the HDR
color texture after all display draws, including dry frames. Godot's Forward+
renderer resolves before the POST_TRANSPARENT callback list, without an
additional resolve between effects. Publishing the cloud result explicitly
prevents later haze and tonemapping from reading the pre-cloud scene.

Use the existing **Clouds / Rain haze** controls for density, near-clear distance,
maximum opacity, shared-gust variation, and tint. The picker and saved tint use
sRGB; both render paths explicitly convert it to linear RGB before blending.
Rain follows the published shared wind frame and is suppressed for shelter,
underwater views, snow, and inactive weather. Opaque depth bounds the wet path;
sky and more distant surfaces use a cap of 750 m or the camera far distance,
whichever is smaller.

Terrain hiding clouds along a viewing ray is separate from terrain blocking
sunlight toward a cloud. The shared terrain sunlight-visibility field supplies
sampled global/regional heightfield visibility with altitude slices and
invalidation for terrain edits and changed sunlight. It is separate from haze
composition and cloud-to-ground shadow generation. Sample resolution and the lack
of native caves or overhangs limit this representation. Missing, stale, or unknown
traversal remains neutral; use its readiness and generation diagnostics when
interpreting captures.

Relevant correctness fixtures are `rain_precipitation_profiles_gpu_test.gd`,
`rain_cloud_compositing_gpu_test.gd`,
`fallback_cloud_mask_atmosphere_gpu_test.gd`, and
`sunshine_shader_compile_gpu_test.gd`. The full cloud fixture freezes its scene
before setting linear tonemapping so the atmosphere controller cannot restore
AgX during quantitative color comparisons. It also disables renderer eye
adaptation on its isolated environment and camera; freezing scene scripts alone
does not stop automatic exposure. Cloud source, dry presentation, and one rain
application are compared with MSAA off, 2X, 4X, and 8X before provider lifecycle
checks. The optional `-- --diagnose-msaa` branch captures raw density, depth-tested
density, and the background separately and explicitly skips the strict suite.
These fixtures validate rendering behavior and do not provide uncontaminated
FPS measurements.

## Correlating a walking hitch

The isolated gameplay travel fixture can export the retained diagnostics at the
end of a route:

```powershell
& $Godot --path . --resolution 1920x1080 --script tools/terrain3d_gameplay_travel_benchmark.gd -- --speed=12 --distance=256 --telemetry
```

`travel-benchmark.json` links the exported report in `telemetry_report`. With
`--telemetry`, each measured frame also records cumulative managed allocation,
GC collection counts and pause duration, proxy main-thread/worker allocations,
and structural-wall worker allocations and maximum requested around-cell count.
Subtract adjacent counter values to examine a frame interval. A worker's
allocation counter is published when its build completes, so its delta can
include work from several preceding frames; it is not an allocation stack trace.
Add `--detailed-gpu` alongside `--telemetry` to include the existing asynchronous
GPU intervals. Those samples retain their source-frame identifiers and may
arrive later than the CPU work that submitted them.

The per-frame `runtime_counters.stopwatch_seconds` and completed CPU events use
the same monotonic clock. `timestamp_usec` uses Godot process uptime instead.
An event's completion time and elapsed duration locate its interval; inclusive
proxy timings contain the structural, camera, coverage, and install child calls.
Global terrain timings separate sampling from mesh construction and installation.
Sampling rows are worker time; mesh construction and installation are main-thread
time. Per-frame `global_terrain` data records active and queued segments, remaining
vertex samples, worker state, detail settings, and estimated installed mesh bytes.
The initial-build duration counters measure the main-thread service call that
publishes a segment, not its total background preparation latency. A completed
worker can wait for the next publication opportunity without blocking the frame.
GC suspension can occur inside any measured call, so consult the pause counter
before assigning a long wall duration entirely to that method's work. Check
dropped samples before treating a missing event as evidence of no work.

The fixture isolates world files, preferences, and reports beneath `.godot`.
Run GPU comparisons sequentially with other game windows closed. It does not
disable adaptive quality protection; its log can report a memory-pressure
fallback, which must be considered when comparing results.

## 2026-10-06 measured results and limits

The primary ran captures serially at 2548x1320 with copied current High settings.
All 14 checked resources remained unchanged: 12 user preference files and two
project default resources. A preexisting Godot process, PID 38720, remained open;
its identity was not confirmed. Total device idle GPU utilization before the tests
was roughly 4-6%, not a per-process measurement. These are not uncontaminated
hardware benchmarks. FPS below is `1000 / mean frame milliseconds`.

| Advancing-clock capture | Frames | Mean frame | p95 frame | FPS |
| --- | ---: | ---: | ---: | ---: |
| Original baseline | 277 | 36.145 ms | 43.280 ms | 27.67 |
| Final build 8, corrected sky/history and caches | 314 | 31.905 ms | 38.783 ms | 31.34 |

Both requested 30 seconds of warmup and 10 seconds of collection at the captured
pose. The final manifest records 35.781 seconds of actual wall-time warmup and
10.0176 seconds of collection. Streaming warm state differs, so this is a useful reproduction comparison, not a
pure causal estimate of the combined changes. The 60 FPS target was not reached.
The [final normal summary](../artifacts/cloud-shadow-history-20261006/final-normal-summary.json)
links its retained telemetry and images. Its mean main GPU interval was 13.05 ms,
outer GPU 0.706 ms, render-setup CPU 10.76 ms, and outer viewport CPU 8.82 ms.
Read these independently; do not sum overlapping/inclusive scopes or delayed
viewport intervals. Remaining renderer CPU work is a leading candidate, but these
measurements do not identify a specific native function as the bottleneck.

A separate frozen-clock late baseline changed from approximately 30.59 to 27.1 ms
(32.7 to 36.9 FPS) after grass/tree caches. It is distinct from the normal-clock
captures above. The [matched render/cache summary](../artifacts/cloud-shadow-history-20261006/matched-render-cache-summary.json)
contains 21 completed phases: build 7 with the earlier sky/history source, 90
seconds of warmup, two seconds of settling, and 10 seconds per sample. Mutations,
restoration, and authoritative clocks were verified. Its findings are scoped to
that source variant and viewpoint:

- Tree render-off reduced mean main GPU time from about 13.03 to 10.90 ms, but
  mean frame time only from the A midpoint 27.31 to 26.81 ms. Main visible draws
  fell from 713 to 366 and primitives from 7,209,448 to 3,353,316; shadow draws
  fell from 1,139 to 659.
- Grass render-off left those workload counters unchanged, with requested grass
  GPU batches already 0/0 in A and B. Later review found that the stationary
  capture disabled explorer physics without refreshing its cached altitude after
  tool pose relocation. Those captures therefore did not validate grass's actual
  altitude-suspension state. They do not establish zero grass cost or explain
  grass lifecycle cleanup; the corrected lifecycle reproduction below supersedes
  that interpretation.
- VSync-off, outer render-off, and cloud-to-ground shadow generation-off produced
  negligible frame changes in these comparisons. Disabling terrain-to-cloud
  visibility changed mean frame time by about 0.15 ms. Its generator averaged
  0.0444 ms on frames with retained dispatch markers, not across every frame and
  not as an atlas-coverage measurement.
- Sunshine-off reduced mean main GPU time from about 13.07 to 10.22 ms while mean
  frame time increased from 27.33 to 28.32 ms. This case activates the merged mesh
  fallback; it is not a comparison against a scene with no clouds.

The final [profiler comparison](../artifacts/cloud-shadow-history-20261006/final-profiler-comparison-summary.json)
used build 9, whose changes since build 8 were tools only. Mean frame times were
29.041 ms in A-before, 28.202 ms with detailed GPU markers off (35.46 FPS), and
27.279 ms in A-after. Baseline drift was 1.762 ms, much larger than B's approximately
0.042 ms difference from the A midpoint. This does not support a precise marker
overhead estimate or causal FPS claim; disabling markers did not produce a large
gain. Viewport measurements stayed enabled. B had zero complete cloud GPU stage
partitions and the detailed-marker flag was false at both boundaries. Build 9
passed with zero warnings/errors; four new phases passed with exact clocks and
restoration. These tool-only results do not relabel the build 8 normal capture or
the build 7 earlier-history comparisons above.

Build 8 passed with zero warnings/errors. Primary correctness runs passed shader
compilation/source-import matching, isolated history policy, actual sky history,
lightning isolation, terrain/history movement and new-occluder cases at 1x/50x,
actual terrain sun-lighting masks, and tree-proxy cache invalidation behavior.
Earlier field, receiver, lifecycle, rain, presentation-cache, and UI checks also
passed. In the sky fixture, variance decreased from 0.000542 to 0.0000148 at 0.95
retained history; power/density changes cleared history. Lightning produced a
transient output of 0.09325 and persistent mean near 2e-9 afterward. These are
controlled fixture results, not gameplay image-quality or FPS guarantees.

The exploratory full-scene targeted-history run, before the final sky fix, did
**not** pass: fixed radiance was stable, but a localized 2.09% of pixels exceeded the 0.01 convergence threshold,
and teardown hung. The primary stopped only that owned process. Passing actual production isolated
fixtures do not erase that earlier failure. The final broad full-scene fixture is
not certified.

### Deliberately inactive source paths

The source audit found several intentional controls rather than missing work.
Interlacing remains commented out because of visible artifacts; do not enable it
as a performance fix. Gameplay addon atmosphere density is zero because the shared
finite atmosphere is applied once. `DitherSpeed=0` does not disable callback-based
sampling jitter. `ExtraLargeUsedAsMask` is false. Menu temporal sampling and fast
reconstruction are optional and off in this gameplay configuration. Reflection
publication has an empty global shader parameter name, and addon diagnostic modes
default off. Although the driver declares continuous updates off, the gameplay bridge
enables them. Judge effective runtime state rather than addon declaration defaults.

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


### Grass lifecycle reproduction, 2026-10-06

The corrected serial gameplay runner refreshes the explorer's cached terrain
altitude from the canonical terrain query after each tool relocation while
holding physics stationary. It uses an explicit baseline canonical height of
297.314976 meters: the original captured height minus 100 meters, with actual
terrain-relative altitude 252.38968 meters, below the 350-meter suspension
threshold. The original source pose remains recorded separately. Elevation is
baseline plus 3000 meters; return captures use exactly the same baseline pose.

The [pre-fix suite manifest](../artifacts/grass-lifecycle-20261006/before/grass-before-lifecycle-15408-2093790/grass-lifecycle-suite.json)
completed all eight phases. Suite `PASS` means capture success and clock/pose
validation, not that grass cleanup was correct. This run used certified High,
2548 x 1320, current copied user preferences, and forced grass `Enabled=true` only
in isolated runtime state to populate the baseline. Other grass settings were
preserved. Normal warmup lasted 30 seconds; authoritative celestial/cloud clocks
were then frozen. Each phase collected 10 seconds after two seconds of settling.

| Pre-fix phase at the same baseline pose | Mean frame | FPS |
| --- | ---: | ---: |
| Enabled, populated baseline | 34.916 ms | 28.64 |
| Directly disabled | 31.358 ms | 31.89 |
| Disabled after elevated clump toggle and return | 16.813 ms | 59.48 |
| Reenabled and regenerated | 28.076 ms | 35.62 |

The direct-disable versus disabled-after-reset comparison reproduces the reported
need for the clump-toggle workaround at the same baseline view. The enabled
samples contain streaming/cache drift, so they are not a precise causal estimate
of grass draw cost.

At the identical elevated view, clump reset changed mean frame time from 28.575
to 10.522 ms (35.00 to 95.04 FPS), while the main GPU interval stayed at 8.630 to
8.625 ms. Render-setup CPU changed from 11.046 to 3.322 ms and outer viewport CPU
from 8.891 to 2.968 ms. These overlapping/inclusive scopes must not be added.
Comparisons across baseline and elevated views do not isolate cleanup cost.

Before reset, snapshots recorded 311,015 resident roots, 1024 resident tiles,
2048 renderers, and 622,030 native MultiMesh slots. The old public GPU fields
reported approximately 87.75 MB. After clump reset, resident roots and native
slots were zero, but the old GPU counters remained stale. Their retained values
are not evidence that those GPU allocations remained live. Updated diagnostics
separate registered batches/reported buffer bytes from requested, dispatched,
and deferred work, and refresh counters at capture boundaries. Native slot and
CPU visibility counts do not read back indirect GPU output occupancy.

The [primary cleanup summary](../artifacts/grass-lifecycle-20261006/primary-summary.json)
and [pre-fix summary](../artifacts/grass-lifecycle-20261006/before-summary.json)
retain matched eight-phase captures. Primary direct-disable averaged 18.871 ms
(52.99 FPS), and disabled-after-reset at the baseline averaged 17.145 ms
(58.33 FPS). Both had zero roots, native slots, registered GPU batches, and
reported buffer bytes. Their remaining difference includes warmup/streaming
and scene drift; it is not a measured residual grass cleanup cost. Primary
late enabled/regenerated grass retained 2048 outputs and averaged 35.26 FPS,
which motivated the subsequent retirement of outputs for enabled but culled
grass.

The 252-meter baseline validates lifecycle behavior at that pose, but does not
establish the performance of visible grass near the ground. The runner also
supports `--baseline-only` for three serial enabled/disabled/reenabled phases,
and `--baseline-altitude-offset-meters -340` for a canonical height of
57.314976 meters (approximately 12.39 meters above terrain). This comparison
uses the same pose, settings, and frozen clocks for all three phases; populated
CPU roots alone do not prove visible native outputs. The completed eight-phase
comparison below measures the 252-meter view; the subsequent ground-level
comparison verifies visible grass separately.

The [final production summary](../artifacts/grass-lifecycle-20261006/final-summary.json)
records all eight phases passing. It combines suspension cleanup with retirement
of unused outputs while the existing `CullGrassTiles` setting remains enabled.
Source suite: `.godot/far-ring-gameplay/grass-final-lifecycle-58972-2387097`.

| Final production phase | Mean frame | FPS | CPU roots | Native grass geometries / slots |
| --- | ---: | ---: | ---: | ---: |
| Baseline enabled | 19.834 ms | 50.42 | 311,015 | 0 / 0 |
| Baseline directly disabled | 18.260 ms | 54.76 | 0 | 0 / 0 |
| Baseline reenabled | 16.966 ms | 58.94 | 311,015 | 0 / 0 |
| Elevated before clump reset | 11.117 ms | 89.95 | 0 | 0 / 0 |
| Elevated after clump reset | 10.648 ms | 93.92 | 0 | 0 / 0 |
| Elevated disabled after reset | 10.647 ms | 93.92 | 0 | 0 / 0 |
| Baseline disabled after reset | 16.213 ms | 61.68 | 0 | 0 / 0 |
| Baseline final regenerated | 16.347 ms | 61.17 | 311,015 | 0 / 0 |

Registered GPU batches and reported buffer bytes were zero in every final phase.
The late regenerated samples at the same horizontal point and terrain-relative
altitude changed from pre-fix 28.076 ms / 35.62 FPS to final 16.347 ms / 61.17 FPS.
Both retained 311,015 CPU roots; the pre-fix sample retained 2048 native grass
geometries and 622,030 slots, whereas final retained none. This demonstrates
retirement of native outputs for grass outside the visible volume while keeping
physical roots resident. It does not measure the cost of visible grass, isolate
all timing changes from scene drift, or guarantee 60 FPS at other views. The
initial final baseline was still warming relative to later phases, as its
50.42 FPS versus late 61.17 FPS shows. Elevation now automatically clears roots
and outputs before any clump toggle, so that workaround is unnecessary for the
measured suspension cleanup.

Correctness checks passed the real worker-completion race, actual GPU retirement,
three suspension cycles, root parity, integration, modifiers, and live tuning.
Live-tuning logs include GPU resource teardown leak warnings; test PASS does not
mean those logs are warning-free.
The output-reactivation GPU check retained the same 71 rendered grass pixels
before and after retirement/recreation. These correctness results complement the
capture-state evidence; they are not a ground-level gameplay timing result.

The legacy `Grass presentation` CPU scope changed its inclusive span during this
work, so its old and new values should not be compared as identical operations.
Use inclusive `Grass update` for the overall manager CPU interval and the new
`Grass output visibility` scope for the output-visibility stage. These scopes
are nested and must not be added.

The [ground-level summary](../artifacts/grass-lifecycle-20261006/ground-summary.json)
and [retained ground suite](../artifacts/grass-lifecycle-20261006/ground/grass-ground-lifecycle-24968-2179556/grass-lifecycle-suite.json)
record three enabled/disabled/reenabled phases at the same horizontal source
location, with baseline offset -340 meters and actual terrain-relative altitude
12.38968277 meters. This run used certified High at 2548 x 1320, 90 seconds of
normal warmup, 10-second collections, and two seconds of settling. Only the
authoritative celestial/cloud clocks were frozen; grass and rendering continued
normally. Current copied settings were preserved apart from the isolated runtime
`Enabled` toggle.

| Visible ground-level phase | Mean frame | FPS | Mean main GPU | Mean render-setup CPU |
| --- | ---: | ---: | ---: | ---: |
| A: enabled | 20.641 ms | 48.45 | 15.869 ms | 5.243 ms |
| B: directly disabled | 15.839 ms | 63.14 | 14.419 ms | 3.361 ms |
| A: reenabled | 18.163 ms | 55.06 | 15.743 ms | 5.110 ms |

Both enabled phases retained 311,015 CPU roots, 454 visible native grass
geometries, 171,560 native slots, and 454 registered/requested GPU batches at
both boundaries. The disabled phase had zero roots, geometries, slots, and
registered/requested batches. Visual inspection of the enabled and disabled
images confirmed visible grass and its recreation after reenabling.

This is current visible-grass timing, with no pre-fix ground-level comparison.
The initial-to-final enabled drift from 48.45 to 55.06 FPS prevents a precise
causal estimate of per-grass overhead. Main GPU and render-setup intervals also
overlap other scene work and must not be summed. Visible enabled grass did not
consistently reach 60 FPS in this view; the result makes no general 60 FPS
guarantee. The separate 252-meter lifecycle results establish cleanup and
retirement outside the visible volume rather than ground-level draw cost.


### Grass cost investigation, 2026-10-06

The [seven-phase cost summary](../artifacts/grass-cost-20261006/cost-summary.json)
supports the concern that grass costs include work beyond blade geometry. These
captures used the same 12.38968-meter ground pose, Certified High at 2548 x 1320,
90 seconds of normal warmup, 10-second collections, and two seconds of settling.
Authoritative celestial/cloud clocks were frozen; camera, placement, and retained
native resource identities were checked at phase boundaries.

| Matched manager comparison | Mean frame | FPS |
| --- | ---: | ---: |
| A: normal enabled | 20.238 ms | 49.41 |
| B: manager paused, drawing retained | 18.190 ms | 54.97 |
| A2: manager restored | 20.195 ms | 49.52 |

All three retained 311,015 roots, 454 native geometries, 171,560 native slots,
and 26.64 MB of reported GPU buffers. Endpoint drift was only -0.042 ms; the
matched manager-pause benefit was 2.026 ms. Inclusive manager CPU scopes measured
1.895/1.931 ms in the normal endpoints, including approximately 1.25 ms of output
visibility work. Source inspection identifies full 1024-tile visibility sweeps
and repeated preparation of GPU requests even when the backend skips unchanged
work. Every collection had zero completed grass-compute, build, and upload
deltas; resident placement hashes also matched through regeneration. Manager
pause therefore removed ongoing CPU work while retaining the same draw resources,
rather than eliminating new compute dispatches in this stationary comparison.

With the manager paused and all grass layers zero, retained resources measured
16.719 ms frame, 5.318 ms render-setup CPU, and 4.494 ms outer CPU. Released
resources measured 16.470, 3.515, and 3.103 ms respectively. The 1.803 ms setup
difference occurred without grass draws, but this C/E comparison was not
bracketed: its frame difference was only 0.249 ms. Setup and outer scopes overlap
and must not be added or translated directly into an equal frame-time benefit.

The [three-phase scenario summary](../artifacts/grass-cost-20261006/scenario-summary.json)
tested scenario attachment separately, retaining identical masked geometry,
manager pause, roots, MultiMeshes, and GPU buffers throughout. Requested native
scenario attachment/detachment/restoration measured 5.198/5.181/5.203 ms of setup
CPU: the matched detachment difference was only 0.019 ms. This does not support
scenario registration as a blanket explanation for the retained-resource cost.
Scenario mutations were recorded without backend readback, and engine logs had
no scenario API errors. The exact native function remains unidentified; these
results do not prove mandatory per-frame MultiMesh maintenance or GPU readback.
Actual grass shader/material draw cost remains separate and cannot be inferred
from resource allocation alone.

Resident CPU root float arrays occupy approximately 24.88 MB, excluding other
CPU overhead; reported GPU buffers occupy 26.64 MB. This is resident allocation,
not preallocation of the configured five-million-root cap. The next focused
optimization is caching unchanged visibility and prepared GPU requests, then
investigating smaller batch counts without lowering blade detail. No production
fixes were made during this cost investigation.

These were controlled serial comparisons, not clean hardware benchmarks. Another
Godot process remained open and pre-start GPU utilization varied from 14 to 41%;
main GPU intervals around 20 ms in the scenario suite differ from the earlier
15 to 16 ms ground capture. Do not compare FPS across those suites. The seven-phase
run also drifted from initial 49.41 to final regenerated 53.84 FPS, preventing an
exact global grass-overhead estimate or target-FPS guarantee.

The [validation summary](../artifacts/grass-cost-20261006/validation-summary.json)
records a build with zero warnings/errors, successful syntax checks, ten completed
capture phases, unchanged camera/clocks/resource identities and placement hashes,
and all 14 checked preference resources unchanged. Existing startup messages
remain in Godot logs; capture PASS is not a warning-free-log claim.

### Adaptive grass grouping and stationary work cache, 2026-10-07/08

The [matched comparison](../artifacts/grass-grouping-20261007/comparison-summary.json)
completed five phases on Godot 4.7.2 .NET / RTX 4070 / Forward+ at 2548x1320.
The camera stayed approximately 12.39 m above terrain with authoritative weather
and celestial clocks fixed. All phases retained the same 311,015 roots across
1,024 leaves, source/rank/wind bytes and canonical world-pose hashes. Each settled
collection performed zero grass builds, uploads or completed compute updates.
Raw reports and screenshots are archived in
[capture-v2](../artifacts/grass-grouping-20261007/capture-v2/).

| Phase | Cache / maximum group | Grass manager CPU, ms | FPS from mean frame | Native batches | Active groups |
| --- | --- | ---: | ---: | ---: | --- |
| A | Off / ungrouped | 1.974 | 49.824 | 454 | 0 |
| B | On / ungrouped | 0.197 | 55.445 | 454 | 0 |
| C | On / 32 m | 0.161 | 55.963 | 313 | 51 level 1 |
| D | On / 64 m; test far threshold 160 m | 0.163 | 56.923 | 306 | 41 level 1, 3 level 2 |
| B2 | On / ungrouped, restored | 0.202 | 57.208 | 454 | 0 |

Caching saved 1.777 ms of measured grass manager CPU between A and B. Default
32 m grouping reduced native batches by 31.1% and active presentations from
1,024 to 871 while retaining every source-cell identity. The additional FPS
benefit of grouping is not established: the restored ungrouped B2 endpoint was
0.556 ms faster than B, exceeding the B-to-C frame difference. Render setup CPU
measured 4.936/4.906 ms in B/B2 and 4.493 ms in C; this scope overlaps other work
and does not provide an independent additive frame saving.

Source cells remain 16 m with 1,024 bookkeeping identities in this capture.
Default grouping combines four compatible cells into a 32 m output, with a
128 m start distance, 16 m hysteresis and a 16,384-source cap. Optional maximum
level 2 combines sixteen cells into a 64 m output; its configured far start is
256 m. D lowered that test-only threshold to 160 m to exercise three actual
level 2 groups in the retained footprint. Level 1 remains the default. Default
GPU buffers increased from 26.643 to 27.043 MB (+0.400 MB), and merging also
requires CPU copies; the batch reduction is not a memory-saving claim. Grave >
Grass > Adaptive grass groups provides the settings through normal save/reset
conventions.

The primary agent reviewed A, C, D and restored B2 screenshots and saw consistent
grass coverage with no new gaps. The build passed with zero warnings/errors;
Forward+ grouping/cache contracts, GPU identity parity, candidate-budget and
production movement/hysteresis/edit/disable tests passed. Existing live tuning,
output residency, lifecycle, grouped compute phases, registration and material
cache suites also passed; logs are under
[grass-grouping-20261007](../artifacts/grass-grouping-20261007/).

This is a controlled stationary comparison with the Godot editor still open and
detailed GPU telemetry enabled. It does not establish moving-route performance,
cross-run FPS or a 60 FPS guarantee.

The [validation summary](../artifacts/grass-grouping-20261007/validation-summary.json)
confirms all 14 checked preference resources were unchanged, all 16 captured
source hashes remained stable during measurement, and the assembly matches the
one launched for the run. Agent-started Godot processes exited; the user's editor
remained open. Existing extension/certificate startup errors and full-scene
shutdown resource warnings remain in the logs; a passing capture is not a
warning-free-log claim.
