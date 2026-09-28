# Night vegetation and atmosphere follow-up

This follows the [terrain and atmosphere implementation](atmosphere-terrain-implementation.md). It investigates bright night grass, cloud/foliage edges, and the cost and integration requirements of local volumetric mist. An advanced atmosphere renderer remains a separate implementation milestone.

## Corrected grass lighting

The primary directional light intentionally remains active globally so the distant day side of the ring can stay lit while the player is on the night side. Standing grass used Godot's normal material lighting without the local day gate already used by other ring materials. Both front lighting and blade backlighting could therefore remain bright at local night.

`RingStandingGrassLayer.Lighting.cs` now binds the primary sun direction and the local geometric day factor to both grass materials. The shader gates that directional light's diffuse, backlight, and specular contribution. Ambient light and point lights remain under engine control; a secondary directional light outside the sun's angular match remains active. Eclipse attenuation remains in the sun's energy and is not multiplied into the geometric day factor a second time. The layer refreshes lighting while stationary and when an additional LOD material is created.

The shader explicitly selects Lambert diffuse and Schlick-GGX specular to preserve the verified runtime appearance. A GPU comparison established that the former engine-default material matched explicit Lambert exactly in this Godot build; an initial Burley implementation changed the image and was rejected. No grass brightness preference was added: this is a correction to the existing ring day/night contract, using its existing direct-light factors.

`tools/grass_night_lighting_test.gd` compares production custom lighting against Godot's independent built-in material lighting, using the same blade, camera, fragment inputs, and fixed foreground mask. The final run passed with relative daytime RGB error **0.00000060**. Primary-sun-only front and back night cases and zero-energy eclipse totality produced zero foreground luminance. Ambient and point-light parity checks passed; the secondary directional light still illuminated the blade. Evidence: `.godot/grass_night_lighting/report.json` and `.godot/grass-night-gpu.log`.

## Cloud/foliage edge isolation

`tools/cloud_foliage_edge_test.gd` uses the production tree shader, real alpha-cutout geometry, a solid control card, production Sunshine compute/compositing, and measured opaque depth. A cloud-only image provides an independent reference for holes in the leaves. The compositor is attached to the exact viewport whose geometry is measured.

All four final static combinations passed: full/half cloud resolution and blur strength 0/0.7. Each checked 6,364 interior foliage pixels, 17,956 solid pixels, 460 enclosed alpha-hole pixels, and 1,481 clear pixels neighboring a foliage edge. Opaque foliage and solid-card differences were zero. Full-resolution alpha holes matched the cloud-only reference exactly; the largest half-resolution hole difference was 0.003007. With blur 0.7, the neighboring-edge difference was 0.000038 at full resolution and 0.002544 at half resolution.

The fixture disables atmospheric post fog and supplies a constant dither volume while retaining production cloud shape resources, ray-start jitter strength 1, and the normal temporal algorithm. Setting dither speed to zero alone is insufficient because gameplay advances its sampling phase per compositor callback. The final fixture also verifies cloud signal on the exact alpha-hole, edge, and newly uncovered masks, rather than relying on whole-frame cloud visibility. Those masks have approximately 0.225 RGB-vector signal against the cloud-disabled background, so the checks are not merely comparing empty sky. Coverage 1 and density 4 put visible clouds behind the test card; the callback stage explicitly matches gameplay's post-transparent stage.

The temporal comparison moves the real card 0.12 world units after 36 drawn warmup frames. It compares history weight 0.95 with history disabled while holding cloud sampling and drift fixed. Physical depth identifies 2,267 newly uncovered pixels. Captures occur at source frames 1, 2, 8, and 32 rather than after an implicit screenshot warmup.

| Frames after movement | History 0.95: RGB-vector difference from cloud-only reference | History disabled |
| --- | ---: | ---: |
| 1 | 0.000042 | 0.000038 |
| 2 | 0.000038 | 0.000038 |
| 8 | 0.000039 | 0.000038 |
| 32 | 0.000156 | 0.000038 |

The small values do not establish a visible history-related edge defect in this fixture. They also do not prove all moving trees, camera paths, LOD transitions, antialiasing modes, or cloud conditions are artifact-free. In particular, fixed dither isolates foreground motion rather than evaluating animated-noise convergence. The temporal section reports observations rather than declaring a universal temporal-quality pass. Final evidence is `.godot/cloud-foliage-edge/report.json` and its images. Earlier runs, including `cloud-foliage-edge-before-blur.json`, are superseded: they had not yet enforced fixed dither and visible clouds on each measured mask.

## Corrected cloud blur at foreground edges

The real forest comparison did reproduce the large bright outlines. In `.godot/night-rendering-comparison/4987106/`, they appear with blur 0.7 and disappear with blur zero. TAA is not required to remove them. The original small card fixture's background was insufficiently sensitive to this particular visual defect; the full-scene comparison identified it and informed the stronger final fixture.

The postprocess blurred cloud color and opacity across rays that stopped at foreground foliage and rays that reached the background clouds. Mixing transparent foreground cloud samples into a neighboring sky ray exposed the bright background around leaves. The postprocess's existing radial filter accepted a depth texture but did not use it to reject these samples.

`SunshineCloudsPostCompute.comp` now checks the ray's opaque depth-break flag when reconstructing cloud color. Each bilinear tap must agree with the original center ray; fractional flags indicate a tap already crossing a foreground boundary and are rejected. Valid weights are renormalized, and an empty neighborhood preserves the center sample. The center metadata stays fixed across radial offsets. Both cheap reconstruction branches and the radial filter use this rule; final per-pixel near/far occlusion remains in place.

The focused GPU test measures a synthetic sharp boundary against the original unguarded filter: bicubic bright-side bleed changed from **0.232734 to 0.080000**, the intended center value. Radial bleed changed from **0.260964 to 0.080000**. A homogeneous neighborhood retained its original result within 0.00000003, and the empty-compatible-neighborhood fallback preserved its supplied center color. The existing seven-case blur regression also passed. All seven Sunshine shader sources compiled, and both updated postprocess imports matched their source bytecode after reimport.

The reviewed post-fix capture set is `.godot/night-rendering-comparison/4912930/`. Large leaf outlines are removed with the gameplay blur strength still at 0.7. Both actual day/night grass bindings and scene phase checks passed. Ordinary leaf edge aliasing and cloud sampling grain remain visible; this fix is specific to the reproduced blur halos.

In the same final capture, the main viewport median measured 10.705 ms for restored night clouds at blur 0.7, 10.295 ms at blur zero, and 10.702 ms at blur zero with TAA. These three cases shared a draw median of 2,931 (range 2,931-2,935). The first night baseline was 11.136 ms, demonstrating enough drift that differences of a few tenths of a millisecond should be treated cautiously. Disabling clouds changed draw counts and is not a clean exclusive cloud-cost measurement. These are stationary viewport timings, not evidence that the existing movement hitches are solved.

Ordinary foliage edge aliasing must be distinguished from cloud depth leakage. Godot documents that MSAA alone does not smooth alpha-scissor texture edges; alpha-to-coverage or a temporal/postprocess technique is needed for those edges. TAA has its own blur and ghosting tradeoffs. See [Godot's antialiasing guide](https://docs.godotengine.org/en/stable/tutorials/3d/3d_antialiasing.html).

## Optional mist and future atmosphere design

The diagnostic mist fixture uses a local 128 x 24 x 128 m FogVolume, zero global fog density, and the existing Forward+ renderer. It separately measures fog disabled, an enabled empty fog grid, and visible mist. Both day and night baselines are repeated; the fixture checks actual day phase, settled terrain/vegetation, draw counts, and rendering settings. It changes only its isolated game process and redirects user preferences and world data below `.godot`.

The final capture `.godot/local-mist-diagnostics/4965007/` passed at 1920 x 1080 on an RTX 4070. Fog grid settings were size 96 and depth 80, with temporal reprojection 0.9. Each sample window lasted at least three seconds and collected 247-340 frames. The day and night repeated baselines had identical draw medians and matching draw ranges.

| Case | Main GPU median | Main GPU p95 |
| --- | ---: | ---: |
| Day, fog disabled | 7.853 ms | 8.172 ms |
| Day, empty fog grid enabled | 7.970 ms | 8.282 ms |
| Day, local mist density 0.012 / range 512 m | 8.006 ms | 8.365 ms |
| Day, disabled baseline repeated | 7.889 ms | 8.216 ms |
| Night, fog disabled | 11.219 ms | 11.392 ms |
| Night, empty fog grid enabled | 11.113 ms | 11.500 ms |
| Night, local mist density 0.012 / range 512 m | 11.014 ms | 11.347 ms |
| Night, same mist with primary sun fog energy zero | 11.034 ms | 11.343 ms |
| Night, local mist density 0.020 / range 256 m | 11.082 ms | 11.433 ms |
| Night, disabled baseline repeated | 10.922 ms | 11.201 ms |

Daytime mist added about 0.14 ms relative to the midpoint of the repeated disabled baselines. The night baselines drifted by about 0.30 ms and bracketed the mist timings, so this run cannot resolve a precise night mist cost. The far viewport remained around 0.53-0.59 ms. These scoped, stationary timings do not establish a general cost bound or measure moving fog, storms, or many overlapping volumes.

The captures show useful local depth separation, but the uniform box produces a broad wash that still needs density shaping and weather placement. Disabling the primary sun's fog energy at night did not visibly remove that blue-gray wash; this comparison does not support diagnosing it as the grass's sunlight bug repeated in fog. Clouds added later by Sunshine also retain contrast inconsistent with a single foreground mist layer. The prototype therefore remains diagnostic-only pending composition, local lighting policy, and movement validation.

Godot's volumetric fog has finite range. Smaller depth range improves local detail; the fog grid dimensions govern quality and cost. Temporal reprojection can leave trails on moving volumes and lights. These constraints favor a small optional weather effect over trying to fill the whole ring with fog. See [Godot's volumetric fog guide](https://docs.godotengine.org/en/stable/tutorials/3d/volumetric_fog.html).

A production mist feature needs one owner for environment settings, an explicit local-day policy for the primary light's fog energy, lightning handling, and verified cloud composition. Sunshine currently runs after transparent rendering, so simply enabling engine fog does not establish correct attenuation of the cloud color added afterward. Godot defines those callback stages in the [CompositorEffect reference](https://docs.godotengine.org/en/stable/classes/class_compositoreffect.html).

For a more advanced atmosphere, the recommended research path is a ring-specific camera-relative sky-view lookup texture, followed by depth-dependent aerial perspective only after the first stage is validated and timed. [Hillaire's reference implementation](https://github.com/sebh/UnrealEngineSkyAtmosphere) provides a lookup-table technique and path-tracing comparison. [Bruneton's implementation](https://ebruneton.github.io/precomputed_atmospheric_scattering/) provides tested precomputation, spectral comparisons, and configurable density profiles. These are algorithm references, not drop-in ring renderers.

The proposed adaptation must account for the finite cylindrical air layer, position across its width, viewing/sun direction relative to the ring axis, the opposite shell across vacuum, and eclipses. Start with an independently checked single-scattering integrator using the existing finite-air ray paths. Validate ground, high-altitude, tangent, sidewall, day/night, eclipse, Certified, and Mega cases before caching it in textures. Keep one linear-HDR scattering/attenuation contract across sky, terrain, clouds, and both viewport scales to avoid applying haze twice.

Recommended implementation order:

1. Resolve mist/cloud attenuation order and establish one environment owner; keep mist optional during motion and weather measurements.
2. Add ring-aligned density shaping and weather placement, then expose and persist the mist controls once correctness and timing are acceptable.
3. Build a small reference single-scattering atmosphere scene and compare it with the current analytic haze at the important ring viewpoints.
4. Cache the validated sky solution in a sky-view lookup texture. Only add a depth-dependent aerial-perspective volume and multiple-scattering approximation when the measured benefit justifies their cost.

If mist or lookup-table rendering is shipped later, add its relevant quality, density, range, weather response, and temporal controls to the existing grave-key Atmosphere tab and persistence conventions. The current Natural/Cinematic analytic haze remains the working production path.

## Reproduction

Use Godot 4.7.2 Mono with Forward+ and no other running game for the display captures:

```powershell
dotnet build RingworldTechDemo.csproj --no-restore --nologo
& $Godot --path . --script tools/grass_night_lighting_test.gd
& $Godot --path . --script tools/cloud_foliage_edge_test.gd
& $Godot --path . --script tools/sunshine_blur_depth_gpu_test.gd
& $Godot --path . --script tools/sunshine_blur_gpu_test.gd
& $Godot --path . --script tools/sunshine_shader_compile_gpu_test.gd
& $Godot --path . --script tools/sunshine_far_depth_gpu_test.gd
& $Godot --path . --resolution 1920x1080 --script tools/night_rendering_comparison.gd
& $Godot --path . --resolution 1920x1080 --script tools/local_mist_diagnostic_test.gd
```

Inspect the log for shader/script failures as well as each fixture's result. This sandbox emits pre-existing missing optional voxel-native-extension, user-log/certificate/cache, and shutdown-resource warnings; a printed PASS alone must not hide a shader compilation failure. The first exploratory mist capture (`7060624`) loaded an unfinished grass shader and is excluded from performance evidence. Capture `4915023` exposed a fixture error that compared one terminal draw count and then printed PASS after failing; it is also superseded. The fixtures now latch failure status and record sampled draw distributions before evaluating repeated baselines.

The final C# build completed with zero warnings and zero errors. The grass night-lighting and existing grass appearance GPU tests passed, as did the strengthened foliage depth/motion fixture, depth-aware blur test, existing blur regression, Sunshine shader compilation, far-depth GPU capture, full forest comparison, and final mist diagnostic. The shader wrappers were updated and reimported with the shared `.comp` edit so the normal and MSAA imported programs matched freshly compiled source.
