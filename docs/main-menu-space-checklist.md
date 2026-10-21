# Main menu: angled ring flyover

The cinematic backdrop lives in `scenes/StartMenu.tscn`, now the project's
startup scene. Press **F5**, or open StartMenu and press **F6**.

## Implemented

The values below describe the built-in factory preset. Your saved project menu
preset takes precedence; this pass preserves existing live-tuned values.

- [x] A random terrain seed per menu visit, using a private copy of the
  Certified preset's terrain settings and the shared terrain sampler.
- [x] A stabilized, forward-facing camera pitched 25 degrees downward at
  approximately 20 km above sampled terrain, traveling at 5 km/s. Height changes
  are smoothed, not banked or tied to individual terrain triangles.
- [x] Deterministic selection of a land-rich starting region for each seed.
- [x] A 125 m displaced, curved near-terrain grid backed by a fixed-size
  scrolling height/color cache, plus a real coarse full-ring terrain mesh beyond
  it. Missing fine rows reveal the far mesh, not a decorative fog substitute.
  No gameplay voxel chunks, LOD tiers, trees, collision, player, or weather
  simulation are instantiated.
- [x] Real SunshineClouds2 menu clouds at half resolution: 128 view steps,
  five light steps, and a curved 2.2-6 km layer.
- [x] A cached 512 x 512 shadow field updated at most 5 times per second, with
  smooth wind offsets between updates. The plugin GPU producer uses the same
  cloud density as the visible volumes. No directional-light shadow-map passes.
- [x] Full real-ring far terrain continues the rising habitat beyond the fine
  cache; haze is limited to at most 12% from roughly 250-1500 km.
- [x] A left-side title and navigation layout with a readability gradient,
  responsive design-space scaling, and the existing three-slot world workflow.
- [x] Isolated high-quality rendering: native resolution up to a
  proportionally scaled 3840 x 2160 cap, 4x MSAA, debanding, filmic tone mapping,
  and restrained glow. The backdrop is refreshed at most 60 times per second;
  this does not cap the game's or menu UI's frame rate.
- [x] Regression coverage and GPU screenshots.
- [x] Recover shadow bindings after viewport resizes and retire menu-owned
  cloud resources after in-flight frames finish.
- [x] A menu-only grave/backtick live-tuning drawer, also opened by the
  **Live Tuning** button. Camera, lighting/haze, cloud structure, cloud quality,
  and cached shadow controls apply without rebuilding terrain.
- [x] Autosave menu edits as project-wide defaults after a 0.5-second idle
  debounce; flush on close/exit, retain the previous file on failed saves,
  report errors in the drawer, and offer a confirmed factory reset.
- [x] Coalesce rapid shadow edits at the selected refresh rate, refresh frozen
  shadows after edits, and keep cloud wind positions continuous when changing speed.
- [x] Fine cloud performance controls: minimum/maximum march distance, sunlight
  sampling distance, density-detail falloff, blur radius/quality, fast reconstruction,
  and cached-shadow vertical sample count.
- [x] Menu-opt-in four-tap reconstruction for tiny blur at half/quarter resolution;
  retain the original radial filter for larger radii or comparison. Skip atmospheric
  integration when its density is zero (the menu uses its own terrain haze), and
  skip shadow noise fetches when cloud density is exactly zero.
- [x] Terrain spacing of 125/250/500 m and independent Low/Medium/High far detail.
  Changes coalesce for 0.75 seconds, rebuild only this preview, and preserve its
  seed, route, travel position, and cloud effect. Motion pauses during preparation.
- [x] Reverse cloud-settings parity: blue-noise motion, ray-start jitter,
  stationary/moving temporal history, and day/night ambient brightness. Night
  brightness follows the menu sun elevation, without starting a gameplay cycle.
- [x] Callback-driven cloud samples continue while paused. Temporal history
  follows the curved ring scroll and bulk wind, with fractional-pixel history
  sampling during motion and jitter-aware rejection for coherent cloud regions.
- [x] Reset cloud history after camera/appearance edits, re-enabling clouds,
  resize/recreation, and discontinuous route jumps. Preserve saved menu tuning.
- [x] Shared-map shadow extinction and 8-200 km radius, 128/256/512/1024/2048
  resolution, and 0.25-20 Hz refresh. Independent generation and receiver switches
  permit cost comparisons. Projection edits clear stale maps until a matching
  texture/center/extent is published, including edits made while generation is paused.

This is a menu-only renderer, not a hidden playable world or a production LOD
controller. The same seed reproduces the terrain; saved-slot edits and custom
generation settings are not part of this decorative preview. Menu lighting and
weather do not change gameplay orbit, eclipse, day length, or weather settings.
The former distant whole-ring/gas-giant composition is not active in this angle.

## What to test in the game

1. Press **F5**, or run `scenes/StartMenu.tscn` with **F6**.
2. The coarse ring appears first; allow approximately 25-30 seconds for the
   complete 125 m cache on the tested machine. Motion starts when it is ready.
   Look for land scrolling underneath the camera, cloud tops below you, and
   cloud shadows following the same weather. The ring should rise into the
   distance without a fog wall. There should be no
   bank/roll, row-sized camera jumps, or repeated strips at cache boundaries.
3. Click **Enter Ringworld**, inspect the three save slots, and go back. Menu
   navigation should remain usable while the backdrop is being generated.
   Loading, creating, copying, and deleting a world retain their existing behavior.
4. Restart the menu to see another terrain seed. For repeatable comparison, set
   the root's **Backdrop Seed** to `1337`; **Freeze Backdrop** stops its motion.
5. Check the layout at 720p and 1440p, and confirm gameplay quality settings are
   still your own when entering a world.
6. Press the **grave/backtick key** (below Escape on a US keyboard), or click
   **Live Tuning**. Try camera pitch/speed, cloud coverage, haze amount, and
   shadow strength. **Escape** or backtick closes the drawer; it does not pause
   the game tree or capture the mouse. The Camera tab has a separate **Paused** control.
7. Wait for **Saved**, restart, and verify the settings return over a new random
   ring. Turn only cloud shadows off: clouds should remain. Turning clouds off
   disables both clouds and their shadows. Changing quality should rebuild the
   affected cloud textures without generating another terrain cache.
8. In **Terrain**, try 250 m or 500 m spacing and a lower far-detail setting.
   Expect a brief terrain disappearance/rebuild, then the same route at lower
   detail. Rapid changes should queue one rebuild. Wait for **Terrain ready**
   and **Saved**, then restart to check that the selected quality persists.
9. In **Quality**, compare fast reconstruction on/off with half-resolution
   clouds and blur at 0.1 or below. Check cloud edges while paused and moving.
   Larger steps/fewer samples trade small cloud detail for cost; increase blur
   above 0.1 to use the original radial filter. In **Shadows**, lower vertical
   samples and watch for missed thin layers or patchier shadow coverage.
10. In **Quality**, compare stationary and moving history while paused, at zero
    camera speed, and during the flyover. Watch for grain versus trails on cloud
    edges; higher history retains more of earlier frames. Try blue-noise motion
    in **Clouds** with ray-start jitter in **Quality**.
    For a clear comparison, keep ray-start jitter above zero and change history
    from 0 to 0.9-0.95, then allow 1-2 seconds of rendered frames to settle.
    Paused clouds should now smooth out even when blue-noise motion is zero.
    Zero blue-noise motion disables extra animation, not temporal sampling.
    Remember these drawer edits autosave; restore whichever values you prefer.
11. In **Shadows**, change radius and resolution: coverage should resize without
    jumping to a mismatched projection. Disable **Receive cloud shadows** to hide
    terrain shading while generation continues. Disable **Generate cloud shadows**
    to retain the last valid map without new dispatches; editing radius/density
    while paused clears that stale map until generation resumes. Restore both switches.
12. In **Clouds**, try ambient day/night brightness, then move **Sun Elevation**
    above/below the horizon in **Lighting**. Wait for **Saved**, restart, and check
    that the new values persist only in the menu preset.

## Live defaults and scope

Development runs save to `res://resources/menu/menu_flyover_defaults.tres`.
This is a normal project resource: include it in source control and builds to
share the tuned start-screen defaults. It is independent of saved worlds and
the gameplay live-tuning drawer. Opening the panel does not itself save anything.
The seed remains random; the preset stores presentation settings, not generated terrain.

The six tabs expose 57 settings:

- Camera: terrain clearance, downward pitch, travel speed, FOV, pause, and
  motion ease-in (seconds to ramp travel speed 0 to full once ready or after a rebuild).
  Screen poses: worlds altitude scale and pitch, detail FOV change, and pose blend
  time (critically damped; 0 snaps). Driven by `SetCameraPose("main"|"worlds"|"detail")`;
  FreezeMotion snaps and defaults to main. Diagnostics: `camera_pose`, `pose_blend`
  (residual, 1 at a pose change down to 0 once settled). Cloud history is treated
  as moving while a blend is in progress.
- Lighting: sun energy/elevation/azimuth, slow sun drift (amplitude 0-60 deg,
  period 1-120 min; 0 amplitude = off), ambient light, haze strength/range,
  and glow. Cloud direct lighting follows sun energy; ambient remains independent.
- Clouds: visibility, coverage/density, base/top altitude, wind drift, direct
  and ambient cloud light, day/night ambient brightness, blue-noise motion,
  and shape sharpness. The menu keeps its existing tint; brightness factors
  of 1 preserve the previous appearance.
- Shadows: visibility, independent generation/receiver switches, receiver strength,
  extinction, 8-200 km map radius (half-span), 128/256/512/1024/2048 resolution,
  0.25-20 Hz refresh, and 1-16 vertical density samples per texel.
- Quality: native/half/quarter cloud resolution, 64-256 view steps, 1-8 light steps,
  10-500 m minimum step, 100-4000 m maximum step (at least 10 m above minimum),
  0.5-20 km sunlight travel, density LOD bias, blur radius/quality, fast reconstruction,
  ray-start jitter, and stationary/moving temporal history (moving capped by stationary).
- Terrain: 125/250/500 m fine spacing and Low/Medium/High full-ring detail.

Terrain defaults to 125 m, with native output up to 4K and 4x MSAA. Native cloud
resolution costs four times as many cloud pixels as half resolution. Very high
travel speeds can outpace the current sampling budget and reveal coarse fallback
rows. The drawer's FPS counter is the UI rate, not the backdrop's capped 60 Hz.
The new high shadow resolution/rate limits are tuning options, not recommended
defaults: 2048 has 16 times the shadow texels of 512. The flyover keeps its
single GPU map rather than adding gameplay cascades or a CPU fallback. Gameplay
cloud handoff, storm sculpting/simulation, CLP provider controls, and preview
buttons are deliberately absent because those systems are not active in the menu.

Edits replace the resource only after a complete temporary-file save succeeds.
An **UNSAVED** message means the previous file is intact; automatic retries back
off for five seconds, and **Save now** retries immediately. **Reset factory
defaults** restores the built-in first-pass values, not a copy of your already
modified preset. Pause, visibility, and shadow-generation switches persist just
like other settings. A frozen shadow texture is not saved: restarting with
generation disabled leaves receivers clear until generation is enabled again.

Exported builds load the bundled project preset but cannot rewrite a packed
resource. Their drawer is explicitly labeled **Local override (exported build)**
and saves `user://menu_flyover_defaults.tres` instead. Project-wide authoring is
done from the writable development project. The packaged-export path still needs
an end-to-end export test; development saving/reloading is covered by the fixture.

The fine cache contains 2049 rows x 1537 columns at 125 m spacing, covering
256 x 192 km (48 km behind and 208 km ahead). It contains 3,149,313 samples and
6,291,456 triangles. At 250 m the cache is 1025 x 769 (788,225 samples,
1,572,864 triangles); at 500 m it is 513 x 385 (197,505 samples, 393,216
triangles). All three cover the same physical area. The coarse full-ring atlas
at High is 2048 x 65 (133,120 samples,
262,144 triangles). At 125 m plus High, the menu uses two terrain meshes and
6,553,600 triangles. Height is stored in an RF float texture. Changing 500 m to 125 m
spacing gives approximately 16 times as many fine-cache samples and triangles;
it does not multiply every rendering cost by 16. At 5 km/s, scrolling requires
about 61,480 new fine samples per second (40 rows), not a full rebuild.
Far Medium uses 1024 x 33 (33,792 samples, 65,536 triangles), and Low uses
512 x 17 (8,704 samples, 16,384 triangles). Reducing far detail does not change
the near cache spacing. Terrain quality edits currently regenerate both meshes;
visible-first generation and background workers remain separate future work.

Fine sampling is limited to 2,048 samples and approximately 2 ms per process
frame; initial far sampling is 512 samples / approximately 2 ms. Texture uploads
are batched to roughly 10 Hz.
This is a soft budget: setup, an individual sample, row commits, and texture
uploads can exceed it. Completed rows publish atomically; invalidated rows fall
back to the far terrain until ready. The queue and textures remain bounded even after
large jumps. The sampler is retained while scrolling and disposed on exit.
Terrain/UI remain native resolution while Sunshine cloud color/opacity is
rendered at the selected cloud scale using the plugin's noise resources. Only a
private compositor duplicate is configured; the production bridge, driver and
storm simulation are not instantiated. The menu's surfaces are opaque, allowing
pre-transparent cloud composition before the 4x MSAA resolve. Production
gameplay keeps its existing compositor stage and settings.

## Verification

- `dotnet build RingworldTechDemo.csproj --no-restore`
- Godot headless: `--path . --headless --script res://tools/main_menu_space_smoke_test.gd -- --extended`
- Godot Forward+/Vulkan: `--path . --rendering-method forward_plus --rendering-driver vulkan --script res://tools/main_menu_space_smoke_test.gd -- --capture`
- Add `--benchmark` after `--` for a 15-second moving-view timing sample. It reports
  root-frame timing separately from the backdrop's capped refresh count. Missing
  per-viewport GPU timestamps are reported as unavailable, not zero-cost passes.
- Add `--diagnostic-clouds` with `--capture` to assert that cloud density mattes
  reach the final viewport, rather than merely existing in an internal buffer.
- Add `--resize-check` for two live target resizes, and `--render-1440` to force
  the isolated 3D target to exactly 2560 x 1440 despite Windows work-area limits.
- Add `--tuning-check` to exercise the drawer, 57 initialized controls,
  focused-field hotkeys, live receiver/camera parameters, save/reload into a
  fresh menu instance, failure recovery, reset, GPU cloud/shadow switches,
  advanced Sunshine parameters, and debounced 250/500 m terrain rebuilds.
  It writes only `.godot/menu_tuning_fixture.tres` and related test fixtures;
  production defaults are hash-checked and never used as test output.

The smoke test temporarily replaces the save service with an in-memory fixture;
it never selects, copies, deletes, or loads a real saved world. It checks menu
navigation, backdrop readiness, mesh/sample counts, isolated viewport quality,
unchanged global quality settings, frozen/moving camera behavior, cloud buffers,
local render/shadow update budgets, absence of gameplay nodes, and teardown
during cache work. `--extended` exercises large jumps and circumference wrapping.
GPU captures are written only under the ignored `.godot/menu_space/` folder.
Visual checks cover 1280 x 720 and an exact 2560 x 1440 3D target on the RTX 4070.
The main window/UI capture can still be 2560 x 1421 due to the Windows work area.
These are menu checks, not a full-game 60 FPS performance certification.

Pre-tuning GPU baseline (2026-09-07): the exact-1440p Forward+/Vulkan test passed
cloud-matte composition, frozen/moving camera behavior, resize recovery, save
isolation, and teardown with no rendering errors. The initial fine cache took
25.8 seconds. A 15.011-second moving sample produced 899 real cloud compositor
callbacks and backdrop updates (about 59.9 Hz). Backdrop GPU time was 13.45 ms
median / 15.11 ms p95; root-frame intervals were 12.93 ms median / 17.10 ms p95.
The run ended with eight fine rows being prepared (2041 of 2049 resident).
These are short, single-seed local measurements, not a minimum-FPS guarantee.

The final C# build completed with zero warnings/errors. The 720p GPU test and
extended headless test also passed, including 300 km relocation, circumference
wrapping, bounded cache residency, and aborting initial generation. Headless
startup emits the sandbox's certificate-store warning and the plugin's expected
"No rendering device on load" messages; visual assertions run on the GPU tests.
At that pre-tuning baseline, production Sunshine source/resource, terrain presets, save
service, and global project settings retained their pre-change file hashes.

Live-tuning verification (2026-09-07): C# build and headless save/reload checks
passed, along with 720p and exact-1440p Forward+/Vulkan checks. The 1440p run
included final cloud-matte composition and resize recovery. A new 15.001-second
moving sample produced 900 actual compositor callbacks; backdrop GPU timestamps
were unavailable on that run, so this is not evidence of a GPU-cost improvement.
That initial live-tuning change did not include startup optimizations.

Advanced-quality pass (2026-09-07): the private menu effect enables the new
reconstruction flag; its default is false for other Sunshine users. The shared
UBO gains one final vec4 (704 bytes total), with previous offsets unchanged.
Zero-density atmosphere work is skipped without changing positive-density
atmosphere behavior. Previous cloud-entry skipping, coarse sunlight sampling,
no-sun shortcuts, and native-resolution tiny-blur reconstruction were already
implemented and are not new in this pass. Shader wrapper updates also force
Godot to refresh cached post/pre shaders that missed include-only edits.

The updated exact-1440p GPU smoke/resize/cloud-matte run passed. A 15.001-second
moving sample produced 900 real compositor callbacks; backdrop GPU time was
12.73 ms median / 13.10 ms p95. Full 125 m readiness took 24.96 seconds.
These are single-run factory-preset measurements, not a controlled before/after
speedup claim and not measurements of your edited project preset.

All seven Sunshine shader variants compiled from fresh source and matched their
imported bytecode. The reconstruction GPU fixture passed seven cases, including
legacy half-resolution filtering, opt-in half/quarter filtering, stronger blur,
raw diagnostics, and zero blur. Its synthetic edge test measured a maximum
0.001879 channel difference between the tiny-radius radial and four-tap results.
This is a filter guard, not a substitute for reviewing cloud silhouettes in-game.

The 720p and final exact-1440p live-tuning GPU runs passed. Coverage includes all
40 controls, the actual packed cloud optimization flag, shadow-sample refreshes,
cancelled/coalesced terrain edits, both lower-resolution caches reaching full
readiness, matching receiver parameters, stable seed/route/cloud ownership,
saved terrain quality loading into a fresh preview, and exit during pending work.
Review caught a readiness/visibility ordering issue; near visibility now updates
with cache readiness before the independent render throttle. The final GPU run
exited without rendering errors. Existing project menu edits were not replaced;
gameplay presets, Sunshine bridge/resource, save service, and global settings
retain their original hashes.
The final extended headless run also passed, including 300 km relocation,
circumference wrapping, bounded cache residency, lower-quality save/reload, and
exit during pending generation. Its certificate-store warning and no-rendering-device
messages are expected environment limitations; GPU checks above verify rendering.

Reverse cloud-settings verification (2026-09-07): the final C# build passed with
zero warnings/errors. Headless menu save/reload and the exact-2560x1440
Forward+/Vulkan smoke test passed, including resize recovery, final cloud-matte
composition, all 50 controls, paired-value clamping/editor refresh, saved defaults
loading into a fresh preview, and isolated terrain rebuild/teardown checks.
The GPU checks exercised actual 128/256/512/1024/2048 shadow textures, matching
terrain projection span and the original 64 km map lead, generation pause versus
hidden receivers, paused-map invalidation/resume, and frozen/zero-speed/moving
cloud-history selection. Day/night ambient brightness preserved the menu palette.
The Clouds, Quality, and Shadows drawer screenshots were reviewed at 1440p.
The GPU run exited without rendering errors; the headless certificate-store and
no-rendering-device messages are expected environment limitations. Production
menu/gameplay cloud presets, the source Sunshine resource, and global project
settings were not changed by these tests. This is correctness verification, not
a new FPS benchmark or startup optimization. Packaged-export persistence and
broader hardware/visual tuning remain unchecked.

## Cloud temporal-history correction (2026-09-07)

The menu previously reused its paused animation time as the noise clock, ignored
the blue-noise motion control, and reprojected only the locally stationary camera
without accounting for the ring scrolling beneath it. High history therefore
repeated identical samples while paused and misaligned samples while moving.

The private menu effect now advances a 64-slice low-discrepancy sequence inside
actual compositor callbacks. Extra noise motion uses its own precise animation
input; wind and terrain stay frozen while paused. Previous ring/wind positions
are paired with those same callbacks, with small double-derived displacements
uploaded for curved reprojection. Static views avoid treating random coverage
variation as a new silhouette. Moving views use bilinear color history and a
bounded history floor only for subpixel motion with coherent cloud depth.
Fast motion, incompatible depth, offscreen samples, and invalid projections
retain conservative rejection. Gameplay does not opt into these menu changes.

The append-only shared buffer is now 720 bytes. Shader wrappers explicitly
refresh the matching pre/post/shadow imports. The diagnostic-only history-weight
output is disabled by default and never feeds back into accumulated history.
No cloud resolution, blur, jitter, or history preset values were changed.

Rendered regression commands (Forward+/Vulkan, not headless):

```text
godot --path . --script res://tools/menu_cloud_history_gpu_test.gd
godot --path . --script res://tools/menu_cloud_history_gpu_test.gd -- --user-quality --render-1440
godot --path . --script res://tools/menu_cloud_history_gpu_test.gd -- --history-diagnostics --user-quality
```

The isolated fixture uses explicit settings, never the production menu preset.
It compares history 0 and 0.95 at the same final pose/sample phase against a
64-sample raw reference, without added blur. At native 2560x1440 with 256 view
steps, 4 km maximum steps, LOD 2, and full jitter, opacity error fell about 77%
while paused and 39% at a simulated 2.5 km/s. Composited-luminance error fell
about 77% and 36%, respectively. These are controlled noise-error measurements,
not FPS gains or a guarantee that every cloud edge will be grain-free.
The test also passes callback-only sampling, blue-noise control independence,
5 mm precise travel, wind tracking, teleport/re-enable recovery, and empty-cloud
history clearance. Broader seed/fast-motion visual tuning remains a user check.

Final verification: C# build passed with zero warnings/errors; all seven fresh
Sunshine shader variants compiled and matched their imported bytecode; the blur
GPU fixture passed. The final exact-1440p full-menu smoke test passed all 50
controls, save/reload, resize recovery, cloud mattes, shadow quality changes,
and terrain rebuild/teardown checks, and exited without rendering errors.
The existing gameplay tuning and sun/history headless checks passed (headless
certificate/no-device and gameplay-fixture teardown warnings remain).
One earlier full-menu attempt hit a save-flush assertion; the final repeat
passed without changing persistence code. Production menu/gameplay presets,
the source Sunshine resource, world-session code, and project settings retain
their pre-fix hashes. This is not a new gameplay GPU/FPS benchmark.

## Startup diagnosis and proposed next pass

At 125 m, the near grid must finish all **3,149,313** terrain samples before motion and
fine terrain are enabled. It is sampled on the main thread under a small
per-frame budget. The coarse far ring appears earlier, but the entire fine
window includes 48 km behind the forward-looking camera. This is the major
avoidable wait, separate from cloud rendering quality.
The new 250/500 m settings reduce that work by about 4x/16x respectively, at the
cost of terrain detail. They do not implement the visible-first/background-worker plan.

Recommended order, not yet implemented:

1. Prioritize the initial visible footprint and a safe ahead buffer; reveal
   completed detail progressively over the coarse ring and start motion once
   that buffer is ready, instead of waiting for all 256 x 192 km.
2. Use a bounded background worker pool with independent sampler copies.
   Keep texture uploads/scene changes on the main thread, reject stale jobs,
   and retain bounded memory and cancellation on menu exit.
3. Profile and reduce repeated whole-atlas uploads while detail is hidden.
4. Optionally cache or pre-bake a small bank of preview seeds for fast warm
   starts. This trades disk space and seed/cache-version management for speed;
   it also changes the current fresh-random-seed-per-visit behavior.

These options preserve the requested 125 m detail. No unmeasured target startup
time is promised; measure time to usable UI, first landscape, first motion, and
full detail separately when implementing this pass.

## Remaining / later passes

Gameplay parity follow-up (2026-09-07): the in-game grave drawer now exposes
Sunshine appearance, shadow, and advanced quality controls too. Gameplay has its
own sparse, autosaved project resource (`resources/clouds/gameplay_cloud_defaults.tres`),
not the menu preset. Coverage/density/wind/light adjustments remain relative to
the evolving gameplay weather. Native/Half/Quarter now reach the real gameplay
compositor. See the gameplay cloud-tuning entry in `weather-system-checklist.md`
for verification and outstanding visual/performance checks. This does not change
the menu startup optimization work listed above.

- [ ] Review the flyover composition in-game and tune the angle/cloud cover to taste.
- [ ] Optional startup optimization: reduce the roughly 26-second fine-cache
  warmup without weakening the selected 125 m detail or blocking navigation.
- [ ] Optional higher-detail coastlines and distant ring coloring: the menu uses
  a coarse heightfield, not near-ground voxel or forest detail.
- [ ] Optional later polish: additional camera compositions, distant lightning,
  and music/ambience. Reduced-motion pause/speed controls are now available.
- [ ] Verify local override persistence in a packaged export.
- [ ] Broader hardware/aspect-ratio profiling before treating the quality preset
  as a shipping default on every device.

Those optional features are not required for this first pass. No new artwork or
audio pack was imported, and the playable forest milestone remains separate.

## Phase B menu UI (world cards, New World, delete, navigation)

- [x] World slots are expandable cards (thumbnail or seed-tinted ring, name,
  last played, playtime, size). Load/Duplicate/Rename/Details/Delete live inside the
  selected card; empty slots open the New World screen, unfinished slots offer
  Continue Setup.
- [x] New World screen: name, seed with reroll, Standard/Compact preset, Create
  or Advanced. The menu only creates the slot and posts a request; the launcher
  writes the generation settings.
- [x] Delete is an in-canvas modal with a 1.5 s hold-to-delete button.
- [x] Esc/B unwinds modal, rename, copy targets, New World, card, then screen;
  explicit focus neighbors on every screen.
- [x] Camera poses: main, world list ("worlds"), expanded card or New World ("detail").
- [x] Type scale and variations in `ring_ui_theme.gd`; mono footer with live
  ALT / speed / sun readout after the survey completes.
- [ ] In-game visual check of card layout, focus ring, and footer readability.
