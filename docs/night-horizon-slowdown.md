# Night horizon slowdown diagnostics

This report documents the investigation of the reported drop from roughly 100 FPS to 8 FPS while looking at the night horizon with clouds, followed by the implemented local sun-shadow handoff. The original attribution captures and the implementation comparisons are separated below.

## Original capture

The original report, user://generated/reports/diagnostics/ring-performance-20260927-202254-8341627-920e8a09.json, recorded two GPU bursts above 80 ms, at 11.274-15.022 s and 19.757-22.799 s. Across 64 slow frames, GPU median was 107.554 ms, p95 123.085 ms, max 125.9 ms, and worst full-frame time 127.125 ms. Fast frames below 15 ms had 8.152 ms GPU median. Slow CPU viewport median was 2.379 ms versus 1.236 ms fast; slow draw median was 2,461 versus 1,199 fast. Movement, workers, and uploads were zero.

Detailed GPU events begin only at 27.070 s, after both bursts. Their GPU.Clouds mean/max of 2.305/2.702 ms cannot attribute those earlier stalls. The report dropped 968,785 samples. It records a 16,384-pixel shadow atlas; the saved shadow preferences separately specify Filter 5 (Soft Ultra) and sun angular size 0.27 degrees. The [raw analysis](../.godot/night-horizon-report-analysis.json) preserves this limitation.

## Controlled night-horizon attribution

Independent runs used Godot 4.7.2 Forward+, RTX 4070, 1920x1080 at native render scale, frozen deepest night, and copied current preferences. They did not alter live preferences or the world. The [flight capture](../.godot/night-horizon-attribution/4244931/report.json) and [ground capture](../.godot/night-horizon-attribution/4298394/report.json) retain per-case state and separate main/far viewport timings.

| Fixed view | Baseline / repeat | Key isolation | Result |
| --- | ---: | ---: | --- |
| Flight: around=29183808.6334, across=101436.3834, height=9690.78994, yaw 0, pitch 0 | 22.647 / 23.581 ms | Sun shadows off 9.072 ms; near + proxy hidden 6.161 ms; far 0.544 ms | Directional-light shadows were the largest measured cost. |
| Ground: around=16443476, across=38692, feet=327.861, yaw 270, pitch 0 | 71.104 / 71.623 ms, 3,060 draws | Sun shadows off 11.523 ms, 1,988 draws; near + proxy hidden 9.071 ms; far 0.504 ms | Reproduces a severe night-horizon GPU slowdown of the same kind. |

Ground controls were atmosphere off 71.063 ms, grass hidden 66.511 ms, trees hidden 56.276 ms, Filter Hard 22.042 ms, and angular size 0 at 20.333 ms. GPU.Clouds was 1.30-1.51 ms in most cases.

The earlier clouds-toggle result of 96.66 ms and 3,193 draws is not all-clouds-off evidence: bridge ownership returned to the legacy mesh fallback and logged rebuilt mesh clouds. It must not be used to attribute the slowdown to Sunshine.

## Completed focused result

The successful focused retry at the same ground view is available in [report.json](../.godot/night-horizon-attribution/4254343/report.json) and its [run log](../.godot/night-horizon-focused-retry.log).

| Case | Main GPU median / p95 (ms) | Draws |
| --- | ---: | ---: |
| Baseline | 71.218 / 71.729 | 3,060 |
| Baseline repeat | 71.461 / 71.950 | 3,060 |
| Sunshine compositor disabled, no fallback | 68.979 / 69.410 | 3,060 |
| Soft Medium filter only | 36.472 / 36.986 | 3,060 |
| 8K atlas only, Filter 5 retained | 15.634 / 15.934 | 3,060 |
| Balanced: 8K atlas and Soft Medium, Filter 3 | 11.808 / 12.037 | 3,056 |
| Sun shadows disabled | 11.624 / 11.822 | 1,988 |

The historical fixture label `directional_shadows_off` changes only `StarLight.shadow_enabled`. It does not disable `PlanetLight` shadows. Those captures did not record the planet's actual light energy or shadow state, so they establish the benefit of removing sun-shadow work without independently proving that planet shadows were active in the captured view.

All active-cloud cases measured GPU.Clouds at 1.31-1.34 ms and far rendering at about 0.509-0.513 ms. The pure compositor-off case kept legacy meshes invisible and FarClipmap plus Shell visible: eight geometry nodes, four invisible local and four visible far. It retained one Sunshine effect with the effect disabled, bridge processing suspended, requested cloud rendering enabled, and no ownership transfer. Two stale GPU events from readback after ClearHistory occurred before measurement and are excluded from the compositor-off interpretation.

A representative GPU profile has an approximately 72 ms block: Opaque about 61 ms, shadow map about 5 ms, and cloud post about 1.3 ms. This points to expensive 16K PCSS filtering on receivers, rather than cloud post processing or shadow-map rendering alone. These rows are controlled comparisons; their differences are not exclusive costs.

The earlier ground baseline and sun-shadows-off images were byte-identical over 37,400 sampled lower foreground pixels (x=80..1839, y=600..939, step 4). That is limited to one deep-night viewport and is not proof for every object or twilight condition.

## Cloud ownership conclusion

The measured baseline had one Sunshine compositor effect at callback 4 (POST_TRANSPARENT), legacyFogRendererEnabled=false, no fog volumes, far clipmap enabled, cloud mask disabled, and Exact Sunshine Backdrop disabled. No duplicate local Sunshine renderer was established. The normal near-compositor and far-clipmap-shell domains remain present.

SetExternalLocalVolumetricCloudsState calls ReleaseFallbackMeshArchetypes when external ownership is true, explaining the fallback behavior of the earlier clouds-toggle control.

## Reproduction

Run the focused ground case with a Godot executable path:

```powershell
& $Godot --path . --resolution 1920x1080 --gpu-profile --script tools/night_horizon_attribution_test.gd -- --ground --around=16443476 --across=38692 --focused --no-sweep --yaw=270 --pitch=0
```

The retry passed with no script or shader-compile errors. Its log has one startup `Parameter "multimesh" is null` renderer error, before measurement; the original ground run had a startup particles-null error. The measured baseline draw counts agreed across the successful ground runs. Missing optional native-extension, UID, certificate-store, and shutdown-resource diagnostics also remain in the logs. A prior attempted fixture run at 4064605 crashed during startup with `Malformed smolv input on shader stage Fragment` and is excluded from the evidence. The C# build passed with zero warnings/errors, and the final fixture passed a separate parse check after small argument/error-path corrections.

## Implemented local shadow handoff

The graphics controller now separates the requested sun-shadow preference from the effective runtime shadow state. With automatic handoff enabled, it suspends `StarLight` shadows once the entire local shadow coverage is safely in deep night. `PlanetLight` retains its existing illumination, direction, softness, and energy-dependent shadow behavior. Neither light is repurposed, and sun energy/direction remain available to the clouds and distant daytime ring. Godot already shares its directional-shadow atlas between the active shadow-casting directional lights.

The default suspend/resume thresholds are -18/-16 degrees. The controller expands the actual sun elevation by the local receiver range's angular extent and constrains the resume threshold to precede the ring material's day-lighting threshold. Separate thresholds prevent toggling while the sun remains between them. Invalid geometry, missing providers, exterior observers, nonzero nighttime direct lighting, and unsupported ring layouts retain the requested sun shadows. Explicitly disabling sun shadows remains effective during day/night changes and preset reapplication.

The grave-key **Shadows** tab saves the automatic handoff toggle and both elevation thresholds through the existing shadow-preference resource. Its sun-shadow checkbox shows the saved preference; the runtime status explains when automatic suspension is active. The implementation does not lower atlas resolution, filtering quality, or either light's softness.

## Implementation comparisons and validation

The completed [16K comparison](../.godot/night-horizon-attribution/4091764/report.json) and [8K comparison](../.godot/night-horizon-attribution/4111487/report.json) used the same stationary Certified runtime-generation ground pose, 1920x1080 Forward+, RTX 4070, Soft Ultra filtering, and 0.27-degree sun softness. The atlas override was confined to each isolated fixture. The user's current preference was already 8K; the 16K run specifically retested the original expensive setting.

| Atlas / phase | Legacy main GPU median / p95 (ms) | Automatic handoff median / p95 (ms) | Planet shadows |
| --- | ---: | ---: | --- |
| 16K night | 69.717 / 70.565 | 11.931 / 12.142 | Enabled in both |
| 16K day | 12.386 / 12.584 | 12.376 / 12.530 | Disabled by existing daylight behavior |
| 8K night | 16.291 / 16.556 | 10.907 / 11.141 | Enabled in both |
| 8K day | 10.384 / 10.751 | 10.351 / 10.684 | Disabled by existing daylight behavior |

Repeated legacy night baselines were 69.820 ms at 16K and 16.322 ms at 8K. The explicit sun-shadow-off controls measured 11.926 and 10.990 ms respectively. Night draw counts dropped from 3,060 to 1,988; planet light energy remained 0.0268453 with shadows enabled, and sun/planet direction, energy, and softness were unchanged within each comparison pair. The final 8K capture also explicitly resolves `FarRingSun` and verifies that its direction and energy remain unchanged. The earlier 16K fixture did not resolve that diagnostic node, so its empty far-sun entry is not independent far-light validation.

Both completed runs passed real-scene assertions for suspend/resume hysteresis, restoring sun shadows before daylight, preset reapplication, nighttime direct-light overrides, disabling automatic handoff, and manual sun-shadow disable. Separate headless shadow settings, ownership, and sun-stability checks passed; the settings check covers persistence round trips, reset, invalid values, and missing-input behavior. The C# build passed with zero warnings and errors. Root review also compared 37,400 sampled lower-foreground pixels in the 16K night pair; they were exactly equal. This image check covers that fixed ground view, not every object or twilight angle.

The initial 8K implementation capture at `4288602` completed its six timing cases but encountered an invalid clock-property read in the later test fixture. Its emitted PASS marker was incorrect. The fixture now requires an explicit successful runtime-contract result before reporting PASS; the two completed runs above supersede that attempt. Known optional-extension/UID/certificate startup messages and shutdown resource warnings remain in the logs; the completed runs had no script or shader compilation errors.

Reproduce either completed comparison without changing saved preferences:

```powershell
& $Godot --path . --resolution 1920x1080 --script tools/night_horizon_attribution_test.gd -- --handoff-comparison --shadow-atlas=16384
& $Godot --path . --resolution 1920x1080 --script tools/night_horizon_attribution_test.gd -- --handoff-comparison --shadow-atlas=8192
```

These measurements address the reproduced sun-shadow bottleneck. The exact camera pose that produced the original report's 125.9 ms maximum was not recovered.

Godot describes the cost of PCSS and angular light size in its [PCSS recommendations](https://docs.godotengine.org/en/latest/tutorials/3d/lights_and_shadows.html#pcss-recommendations).
