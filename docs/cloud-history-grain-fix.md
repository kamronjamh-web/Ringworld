# Gameplay cloud history and persistent grain

The September 27, 2026 investigation reproduced speckled cloud interiors while the cloud outline retained history. It affected Sunshine clouds in the gameplay sky, including clouds toward the horizon.

## Measured cause

The history texture was not being continually cleared. In a stationary Certified runtime Terrain3D scene, the bridge requested and packed 0.95 history for 120 compositor callbacks with zero explicit refreshes, accumulation rebuilds, render-target changes, or zero-history callbacks. The scene had one Sunshine compositor effect. Renderer isolation reproduced the grain with Sunshine alone.

The rejection happened per pixel. Ray-start jitter can hit a different part of a cloud, or miss it entirely, between frames. The old coherent-history floor required the distance to the strongest density sample to agree. When this noisy distance changed, alpha disagreement could discard most or all history even though camera reprojection and scene depth remained coherent. Whole-image averages obscured the problem because empty sky retained history correctly.

Two additional sampling issues were addressed:

- Integrated density reached alpha values up to 1.98. It was bounded only at display time, after history rejection and averaging. The accumulation input now uses opacity in [0, 1].
- Gameplay permuted a finite set of noise-volume slices. The revised sampler adds the existing callback-driven phase to a fixed spatial blue-noise seed, distributing the actual scalar ray offset over time. Menu sampling keeps its existing sequence.

Temporal sample quality matters alongside the history weight; NVIDIA's [spatiotemporal blue-noise research](https://research.nvidia.com/publication/2022-07_spatiotemporal-blue-noise-masks) discusses that distinction. This implementation uses a scalar progression over the existing asset, not a newly generated STBN texture.

## Rendering changes

Matching scene-depth state retains the selected gameplay history weight without requiring a noisy cloud-body distance match. The initial fix restricted this path to two cloud pixels of motion per callback and tapered the retained floor between half a pixel and two pixels. Follow-up continuous-camera tests reproduced the user's remaining grain during ordinary turns. Gameplay now uses bilinear color reprojection and the full bridge-selected weight for valid history at every screen-motion speed. Invalid/off-screen reprojection and the existing depth-break rejection still apply. The menu keeps its separate distance-coherence and motion policy.

The fix adds no cloud march samples and retains the existing factory min/max steps of 500/958 m. Stationary history, moving history, ray-start jitter, and blue-noise motion remain configurable through the existing grave-key menu and persistence path. The follow-up found a saved moving-history value of zero; the user selected 0.85 for quicker response, and that preference is now saved. Stationary history remains 0.95.

Gameplay also publishes its current cloud-frame transform and double-precision canonical coordinates. Sunshine pairs that snapshot with the camera from each actual compositor callback, computes the small curved-ring displacement on the CPU, and expresses the previous camera in the current presentation coordinates. This preserves history when the floating origin recenters without enlarging the GPU uniform buffer. Missing/invalid frame pairs, radius changes, and canonical jumps beyond the existing menu-sized discontinuity bound reject history. Standalone Sunshine users without this gameplay contract keep ordinary camera reprojection. Exact cloud-frame field comparisons replace approximate comparisons that could suppress small changes at large canonical coordinates.

The far clipmap/shell is a separate renderer without Sunshine history. Isolation identified Sunshine as the source of the reproduced speckles; this change does not add temporal filtering to the far geometry renderer.

## Validation

Godot 4.7.2 Mono, Forward+, RTX 4070. These are image-quality/correctness results, not a new uncontested FPS benchmark.

The HDR fixture compares cloud regions with a 64-sample reference. Interior means reference alpha >= 0.85; edge means 0.1 < reference alpha < 0.85. The following ablation holds the scalar sampler and opacity clamp fixed, isolating the cloud-distance gate correction:

| Metric | Old distance gate | Corrected gate |
| --- | ---: | ---: |
| Stationary interior luminance RMS error | 0.02897 | 0.00488 |
| Moving-path endpoint interior luminance RMS error | 0.02570 | 0.00663 |
| Stationary edge frame-to-frame luminance RMS | 0.06029 | 0.00453 |

The moving-path error is sampled after reaching the path endpoint; the eight-frame variation series uses a fixed pose. It is not a measurement of continuously moving pixel correspondence. Separate gameplay captures exercise walking and turning.

The image regression requires at least 16 samples in each region, high-history luminance error at most 25% of raw, temporal variation at most 20% of raw, finite HDR values, and raw opacity no greater than 1.001. It passes with blue-noise motion at both zero and 60.

The production alpha-cutout foliage fixture passes at native/half cloud resolution with blur off/on. With history at 0.95, its newly uncovered pixels differ from the cloud reference by about 0.00134 RGB on the first measured frame, versus a cloud signal of 0.226. This checks recovery behind moving foliage as well as the static depth boundary.

The final gameplay readback at factory steps measured mean retained history of 0.94968 across cloud samples and 0.94963 in dense samples, against a requested 0.95. Alpha remained in [0, 1]. The 120-draw route moved 12 m while turning 8 degrees, with zero history resets or buffer rebuilds. A sudden additional 30-degree turn correctly exposes fresh sample noise, then converges again; that transient remains visible in the early-turn capture.

The ordinary gameplay and menu history suites, synthetic far-terrain occlusion, depth-aware blur regression, and all seven Sunshine shader compile/import comparisons pass. The project builds with zero warnings and zero errors.

Initial stationary-fix artifacts:

- Baseline isolation: `.godot/cloud-history-gameplay/4426769/report.json`.
- Final raw readback and movement captures: `.godot/cloud-history-gameplay/4368154/report.json`.
- Interior regression logs: `.godot/cloud-history-regression-zero2.log` and `.godot/cloud-history-regression-sixty.log`.
- Final gameplay/menu/shader/depth logs: `.godot/cloud-history-final-{gameplay,menu,source,depth}.log`.

## Continuous-motion follow-up

`tools/cloud_history_motion_gpu_test.gd` moves the actual Camera3D on every source callback. It compares history off with 0.85 at frames 48, 72, and 96 of each continuing trajectory, using a separate 64-sample raw reference at each matching camera pose. Neither traversal pauses to settle before the measured images. Motion is specified per callback at a logical 60 Hz; this is not an FPS measurement.

At 640x360, the following RMS luminance errors pool the three target frames by sample count. Both before and after use history 0.85:

| Continuous camera route | Interior before | Interior after | Edge before | Edge after |
| --- | ---: | ---: | ---: | ---: |
| Turn 15 degrees/s | 0.02773 | 0.02765 | 0.03990 | 0.04061 |
| Turn 60 degrees/s | 0.04323 | 0.03411 | 0.08725 | 0.04812 |
| Turn 120 degrees/s | 0.05056 | 0.03389 | 0.08119 | 0.04854 |
| Translate 6 m/s | 0.01107 | 0.01108 | 0.01513 | 0.01503 |
| Translate 6 m/s and turn 60 degrees/s | 0.04243 | 0.03395 | 0.08683 | 0.04771 |

The gain is concentrated in ordinary/fast turns that exceeded the previous two-pixel gate. Slow turns and translation already used that path and remain comparable. A sixth route repeats the combined motion through a 128 m presentation-origin shift at frame 48. It retains 0.85 at the shift, with no additional zero-history callbacks; region errors match the unshifted route within 0.00006.

The regression requires at least 16 samples per region at every target pose. With the responsive 0.85 history setting, interior error must be at most 85% of raw and edge error at most 75%; straight translation requires at most 35% for both regions. These bounds pass at 640x360 with blue-noise motion 60 and at 1280x720 with it set to zero. CPU contract tests call the production remap helper and cover curved frames, rebasing, rigid presentation transforms, circumference wrapping, small movement at large canonical coordinates, and invalid/jumping frame pairs.

The real Certified runtime Terrain3D fixture (`--continuous-motion`) completes five 96-callback routes with zero history resets or buffer rebuilds during each route. At 60 and 120 degrees/s the bridge packs approximately 0.85, while slower motion blends toward stationary history as intended. The production screenshots are captured immediately after the last moving callback. Its optional diagnostic readback occurs one callback later and is labeled separately; it is not used as a matched-pose motion-error measurement. The stationary interior, menu, foliage, and depth-aware blur regressions also pass, and all seven shader imports match freshly compiled source.

Follow-up artifacts:

- Before/after motion reports and captures: `.godot/cloud-motion-before/` and `.godot/cloud-motion-after/`.
- Zero-blue-noise motion at 1280x720: `.godot/cloud-motion-zero-720/`.
- Full Terrain3D continuous-motion report/screenshots: `.godot/cloud-history-gameplay/4332442/`.
- CPU frame contract: `.godot/cloud-motion-frame-final.log`.
- Regression logs: `.godot/cloud-motion-check-*.log` and `.godot/cloud-motion-source-final.log`.

Useful commands, with `godot` pointing to the Mono console executable:

```powershell
dotnet build RingworldTechDemo.csproj --no-restore --nologo
godot --path . --script tools/menu_cloud_history_gpu_test.gd -- --gameplay-history --interior-regression
godot --path . --script tools/menu_cloud_history_gpu_test.gd -- --gameplay-history --interior-regression --dither-speed-zero
godot --path . --script tools/cloud_history_gameplay_diagnostic.gd
godot --path . --script tools/cloud_history_gameplay_diagnostic.gd -- --sampling-diagnostics
godot --path . --script tools/cloud_foliage_edge_test.gd
godot --headless --path . --script tools/cloud_history_frame_contract_test.gd
godot --path . --script tools/cloud_history_motion_gpu_test.gd -- --regression
godot --path . --script tools/cloud_history_motion_gpu_test.gd -- --regression --dither-speed-zero --render-720
godot --path . --script tools/cloud_history_gameplay_diagnostic.gd -- --continuous-motion
```

The gameplay fixture saves isolated worlds, screenshots, raw cloud readbacks, and reports beneath `.godot/cloud-history-gameplay/`. Its optional readback flag adds copy usage only to diagnostic allocations. Raw diagnostic channels must be read before tonemapping; a canvas copy sharing the gameplay environment changes their values and cannot validate history weights.

High history still trades response time for smoothing. Newly exposed regions have no previous visible sample, and weather changes or finite ray resolution can reveal fresh sampling noise. The 0.85 preference deliberately retains more current-frame contribution than 0.95. Valid overlapping regions now accumulate during continuous turns; this is not a claim that all volumetric sampling noise is eliminated.
