# Cloud slowdown investigation — 2026-09-29 laptop capture

## Measured report facts

The capture is `ring-performance-20260929-223708-8850731-9af258b0.json`, taken on the i7-13700H laptop. It retains 746 frames over 29.996 seconds. The first three chronological equal-frame-count bins averaged 34.493, 34.049, and 34.981 ms; the final 75-frame bin averaged 111.894 ms (p95 130.359 ms). The capture includes movement before the slow stationary tail. These bins are equal **frame-count** bins, not equal-duration windows.

The retained frame distribution is mean 40.254 ms, p50 32.578 ms, p95 113.456 ms, p99 146.236 ms, and maximum 184.685 ms. Every retained frame but seven exceeded 16.67 ms.

The measured main-viewport GPU interval was mean 37.015 ms, p95 107.467 ms, and maximum 177.903 ms. The corresponding main-viewport CPU render interval was mean 1.320 ms, p95 1.982 ms, and maximum 3.504 ms. Physics was p95 2.321 ms and render setup p95 1.017 ms. This is evidence that the captured slowdown is GPU-bound within the measured viewport.

`GPU.Clouds` is the largest scoped detailed GPU interval: 251 samples, mean 31.999 ms, p95 82.714 ms, maximum 126.396 ms, and a final completion at the end of the report. `GPU.GrassCulling` was mean 0.021 ms, p95 0.035 ms, maximum 0.045 ms. The final capture context had a Medium preset, 100% render scale, 60 tree shadow casters, 981 draw calls, 2,996,276 primitives, a 16,384 px 16-bit directional atlas with an estimated 512 MiB minimum depth allocation, and 1,728,079,584 reported video-memory bytes.

Terrain and water did not present an active workload in the captured context: terrain showed zero queued uploads, zero active workers, and no chunk loads/rebuilds; water was dormant with zero completed FFT updates. This does not categorically exclude terrain, water, memory pressure, or another uninstrumented system from contributing to the slowdown.

## Measurement limits

`GPU.Clouds` covers the Sunshine cloud prepass, raymarch, reconstruction, and composition. It does not include cloud-shadow generation, individual tree shadows, materials, SSR, or complete scene rendering. The measured main viewport excludes far-ring viewport rendering. GPU results are asynchronous and source-frame scoped, so they must not be paired to CPU frames by array index.

The report dropped 52,648 samples. Missing timing events therefore do not establish that a system did no work. Worker rows are wall durations, not blocking main-thread time. Inclusive rows overlap their child calls and must not be summed. For example, `Grass build` reached 148.950 ms on a worker, but its final completion preceded report end by 8.543 seconds; it does not demonstrate a final-frame block.

The exact weather state and GPU configuration used for this historical capture are absent from the report. The current repository cloud resource values (native resolution, 256 raymarch steps, eight lighting steps) and current mutable cloud-coverage override are current repository state, not confirmed capture state. GPU clocks, temperature, power state, and window focus were not recorded either.

## Controlled cloud attribution

The report supports the hypothesis that Sunshine cloud work is a leading measured GPU contributor. It does not establish a root cause, and no root-cause fix is claimed here.

`tools/cloud_slowdown_attribution_test.gd` passed on the laptop's RTX A1000 6 GB, Godot 4.7.2 Forward+, with a requested 1920x1080 window (Windows supplied a 1920x1061 client capture). It creates an isolated runtime-generation world, copies preferences into the fixture directory, waits for terrain/vegetation/global LOD readiness, and freezes the camera, weather, celestial clock, and cloud driver's wind origins. Screenshots and warmup are excluded from the measured history. The production world and saved preferences are untouched.

The September 29 save supplied around=156205.1497623861, across=254166.4515210524, feet=5848.370188249275, cloud clock=881.3295438888823, and celestial clock=3191.470552888884. The recreated terrain hash matches `E8F7E9C9FAEC4082`. An explicit four-heading horizon sweep selected yaw=180 degrees, pitch=0; this is a departure from the saved yaw=209.52, pitch=6.56. The wind origins are initialized by the new session and cannot reconstruct the old driver's accumulated wind state from the saved weather clock alone.

Successful fixture: `.godot/cloud-slowdown-attribution/5220945/report.json`; log: `.godot/cloud-slowdown-ab2.log`. All times below are milliseconds. This run **did not reproduce the historical 5 FPS state**.

| Case | Main GPU median/p95 | GPU.Clouds mean/p95 | Full-frame p50/p95 | Result |
| --- | --- | --- | --- | --- |
| Native clouds, 16K atlas | 30.581 / 35.284 | 15.314 / 15.399 | 33.300 / 37.852 | Baseline |
| Sunshine compositor disabled | 13.565 / 13.948 | none | 15.793 / 16.394 | Requested Sunshine ownership retained; no local fallback enabled |
| Cloud-shadow maps disabled | 26.569 / 30.052 | 12.947 / 15.381 | 28.964 / 32.577 | Disables production and receiving, not a producer-only control |
| Sun shadows disabled | 22.793 / 27.943 | 14.685 / 15.697 | 25.644 / 34.092 | Sun-only isolation |
| Half-resolution clouds | 21.199 / 25.764 | 6.760 / 6.853 | 23.565 / 28.406 | Existing quality setting; potential image-quality tradeoff |
| Quarter-resolution clouds | 18.539 / 22.742 | 4.383 / 4.582 | 20.926 / 25.434 | Existing quality setting; potential image-quality tradeoff |
| Native baseline repeated | 30.442 / 35.397 | 15.334 / 15.437 | 33.079 / 38.277 | Stable repeat |

Far viewport GPU medians stayed near 1.4–1.6 ms. These are controlled differences, not additive exclusive GPU costs. Half resolution reduces the measured cloud interval by about 56%, but it does not by itself establish 60 FPS or resolve the historical cliff. Lowering terrain-generation quality would not address this measured cloud work.

The first attempted fixture (`5289752`) had invalid zero-argument calls into a C# method with an optional parameter and stale underwater metadata after teleport. Its run failed, and its captures are excluded from final evidence. The successful fixture explicitly clears the stale water metadata for the known dry mountain position and verifies that the atmosphere has returned above water. Startup certificate-store/deprecation messages and shutdown RID warnings remain; the successful measurement has no script or shader compilation errors.

## Earlier sun/planet shadow issue

The existing local sun-shadow handoff remains in `RingGraphicsQualityController.LocalShadows.cs`: it suspends requested sun shadows only when the complete local receiver range is safely below the -18 degree threshold, and restores them near -16 degrees. The saved laptop preferences do not disable this handoff. `day_side_atmosphere.gd` respects graphics ownership of sun shadows and enables planet shadows only when the planet's resulting energy exceeds 0.001. Twilight overlap remains possible; this is not a hard one-light-only switch.

The user tested an 8K directional atlas and reported that it did not resolve the slowdown, then confirmed that disabling cloud rendering restored performance. The subsequent fixture (`10574113`) inherited the already changed 8K preference in every case, so it is not a valid 16K/8K comparison; its timings are also excluded because concurrent gameplay was not ruled out. Its captured light state has sun energy 4 with shadows enabled and planet energy 0 with shadows disabled. Simultaneous sun/planet shadows therefore were not active in that recreated daytime view. See [the earlier measured night-time fix](night-horizon-slowdown.md) for the original PCSS investigation; those desktop results are separate from this laptop capture.

## Reproduction

Capture exports now also include a capture-time `renderState` object: camera pose,
viewport scale and focus, actual sun/planet state, cloud settings, altitude bands,
and driver noise origins. These fields describe the instant of capture, not the
entire history. Detailed GPU profiling additionally splits the inclusive
`GPU.Clouds` row into prepass, raymarch, reconstruction, and composition intervals;
do not add the parent to its children.

An empty-density shader optimization was compared against the pre-change
shader at commit `a85117d748f171bcbd98c02a730b78b280196a75`. It rejects exact-zero
wall masks and empty altitude regions before texture sampling, and skips
medium/fine noise when the large shape and localized storm are both absent.
Fine erosion retains its original pre-warp coordinates. Zero large shape now
explicitly yields empty background rather than dividing by a zero remap range.
No quality setting or wind/weather ownership change is part of this optimization.

The standalone `tools/cloud_density_fastpath_gpu_test.gd` passed on the RTX A1000:
4,096 finite reference samples, zero non-finite reference samples, positive
background and storm-enabled controls, and no detected density difference
(tolerance `1e-5`). This test uses synthetic texture inputs; it does not establish
full-scene visual equivalence or a performance improvement.

### Native-resolution shader comparison

Fixture `5926765` passed with fresh in-memory compilation of both shaders, an
8K atlas, native cloud resolution, 256 raymarch steps, eight lighting steps,
and a frozen field. The viewport was 1920x1061. Clouds were explicitly enabled
in the isolated fixture because the user's comparison had saved them disabled.
The saved preference was preserved. Actual capture values included density
multiplier 1.05, resource density 1.26, resource coverage 0.7, and sharpness 1;
do not assume these match the historical slowdown capture.

| Shader | Main GPU median / p95 (ms) | Cloud GPU mean / p95 (ms) | Full frame median / p95 (ms) |
| --- | --- | --- | --- |
| Original | 26.357 / 31.388 | 16.240 / 16.456 | 29.000 / 34.284 |
| Optimized | 17.668 / 22.493 | 7.719 / 7.923 | 20.350 / 25.391 |
| Original repeated | 26.276 / 31.723 | 16.178 / 16.394 | 29.008 / 34.592 |
| Optimized repeated | 17.804 / 22.822 | 7.846 / 9.287 | 20.460 / 25.628 |

The first comparison reduced the inclusive cloud interval by 52.5% and median
full-frame time by 29.8%. The new per-pass instrumentation places the saving in
raymarching (13.474 to 4.960 ms mean); optimized reconstruction remained 2.439 ms.
All five cloud timing rows arrived, and the capture-time render state serialized
without an error. Reference/optimized screenshots were inspected for gross
cloud-shape changes; final human visual acceptance remains open.

This is a measured optimization, **not proof that the delayed 5 FPS failure is
fixed**. The user subsequently clarified a 10–30 second delay followed by looking
into the sky and back to the horizon; the separate run below covers that camera
transition after the scene has settled.
Portable outputs: [fixture report](benchmarks/cloud-density-fastpath-20260929-attribution.json)
and [timing summary](benchmarks/cloud-density-fastpath-20260929-summary.txt).

The aborted `4725487` run inherited disabled clouds before the fixture gained an
explicit provider check. Run `4620677` failed when the new shader-switching test
used the wrong RDShaderFile setter. Neither is performance evidence; the fixture
now uses `set_bytecode` and compiles variants before its readiness wait.

### Sky-to-horizon transition

Fixture `4951638` passed at the newer saved position (around 156462.3184,
across 253942.7414, feet 5912.9323 m, yaw 297.84004 degrees), with saved cloud
clock 1229.8085 and celestial clock 3539.9495 seconds. After scene readiness,
each shader case rotated the existing camera to +75 degrees for at least three
seconds, then returned to zero-degree pitch and measured ten seconds immediately.
The measured sky forward/up dot product was 0.965926. No teleport or compositor
refresh occurred between the sky hold and horizon return. Wind/weather were
frozen for repeatability, so this is not an exact replay of a moving weather field
10–30 seconds into the user's session.

| Horizon-return window | Cloud GPU mean / p95 (ms) | Full frame median / p95 (ms) |
| --- | --- | --- |
| Original | 25.940 / 26.158 | 37.317 / 42.423 |
| Optimized | 9.234 / 10.620 | 20.608 / 25.840 |
| Original repeated | 26.239 / 26.033 | 37.298 / 42.699 |
| Optimized repeated | 8.221 / 9.277 | 20.235 / 25.580 |

All windows completed without reaching their measurement cap. The original
shader did not reproduce the historical 5 FPS state in this controlled setup;
the optimized shader retained its improvement after the camera transition. The
first optimized *post-transient stationary* window had a higher p95 than its
repeat (see the full report), so the transient table must not be read as a
guarantee of hitch-free gameplay. An analysis-only process fault overlapped part
of this run; this was not a second gameplay/GPU session, but CPU statistics are
less controlled than the earlier stationary A/B.

Portable outputs: [sky-return report](benchmarks/cloud-sky-return-20260929-attribution.json)
and [sky-return timings](benchmarks/cloud-sky-return-20260929-summary.txt).
Run `4985405` was aborted and excluded: its initial test used negative pitch,
which looks downward in this controller. The final fixture checks the actual
camera direction before accepting a sky-return sample.

Validation completed: .NET build (zero warnings/errors), diagnostics contract,
4,096-case density GPU comparison, fresh compilation of all seven Sunshine
shaders, and imported/source bytecode agreement. Existing certificate-store,
deprecated-interpolation, and shutdown RID warnings remain; these runs do not
establish whether any separate long-session resource leak exists. The user's
saved cloud-rendering preference remains disabled and must be re-enabled for a
manual gameplay retest.

Run one game at a time, leaving the editor idle. Substitute the local Mono Godot executable for `godot`:

```text
godot --path . --rendering-method forward_plus --resolution 1920x1080 --disable-vsync --script res://tools/cloud_slowdown_attribution_test.gd -- --sweep --cases=baseline,sunshine_compositor_off,cloud_shadow_maps_off,sun_shadows_off,cloud_half_resolution,cloud_quarter_resolution,baseline_repeat
```

The script records the effective viewport dimensions, selected view, weather clocks, initialized noise origins, settings, and source report paths. Generated worlds and raw captures stay under `.godot/cloud-slowdown-attribution/`; portable measured summaries are in [benchmarks](benchmarks/cloud-slowdown-20260929-report-summary.txt).

To recreate the reference shader in Windows `cmd.exe`, use:

```bat
git show a85117d748f171bcbd98c02a730b78b280196a75:addons/SunshineClouds2/SunshineCloudsCompute.glsl > .godot\cloud-slowdown-reference.glsl
```

Then add `--reference-shader=res://.godot/cloud-slowdown-reference.glsl` and
`--cases=cloud_reference,cloud_optimized,cloud_reference_repeat,cloud_optimized_repeat`
to the fixture command. `--sky-return` enables the camera transition; position,
view, and clock overrides are recorded in the sky-return report. Shader variants
are assigned with Godot's documented
[`RDShaderFile.set_bytecode`](https://docs.godotengine.org/en/stable/classes/class_rdshaderfile.html#class-rdshaderfile-method-set-bytecode)
API and refreshed through the existing bridge resource-retirement path.
