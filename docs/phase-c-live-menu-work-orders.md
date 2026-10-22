# Phase C: live world behind the start menu — work orders

Parent plan: `docs/main-menu-revamp-plan.md` (Phase C). This document holds the
executable work orders and the facts they depend on.

## Decisions (user, 2026-10-21)
- **Cruise altitude:** about 16 km, with a hard ceiling of 18 km above ground
  during menu flight.
- **Warm-load:** the last-played world loads at boot behind the decorative flyover,
  then crossfades in once ready.
- **Eligible worlds:** only runtime-generation Standard worlds
  (`terrain_backend == "surface_runtime_generation_v1"`, Standard radius/width).
  All other worlds, or no saved world, use the decorative flyover only.
- **Menu camera:** loiters within tens of km of the save spot. Continue is a short
  fly-in followed by a descent to first person.
- **Save & Exit:** ascends to cruise and returns to the menu without a scene change.
- **Loading screen:** kept for New World, Advanced and loading a different slot.
  Tear down the warm world first.
- **Time of day:** show the saved time; pause the celestial and cloud clocks while
  on the menu.
- **Startup stalls:** if frames cannot be split below about 100 ms, the menu stays
  non-interactive (survey readout visible) until the warm-load is past its last stall.
- **Flyover pose:** when a warm-load is expected, the decorative flyover uses the
  cruise altitude (16 km), not 20 km.
- **Continue after Duplicate:** Continue stays bound to the warm world.
- **Continue while still warming:** Continue shows "Preparing NN%" and flies in
  automatically once the world is ready (user, 2026-10-21).
- **Worlds entered through the loading screen:** a live-candidate world loaded via
  the loading screen (for example New World or another slot) is adopted by the warm
  owner, so its Save & Exit also climbs back to the live menu (user, 2026-10-21).
- **Remove the decorative flyover entirely** (user, 2026-10-22). The menu shows the
  real ring from the first frame. This supersedes the warm-load "behind the
  decorative flyover" and the matched-pose decisions above.
  - **Boot screen:** a full-screen snapshot from the last session, captured at
    cruise altitude, shown with a slow drift and then crossfaded to the live view of
    the same spot. If no snapshot exists, show a dark starfield.
  - **No eligible world** (first launch, or a Compact/legacy world last played):
    load a bundled Standard showcase world in the background. It is read-only, never
    saved, and uses its own cache folder.
- **Flyover-removal details** (user, 2026-10-22):
  - **Stale snapshot** (the save changed after capture, e.g. a window close): still
    show the old snapshot, then dissolve to the live view of the actual spot.
    There is no starfield in this case.
  - **Showcase world:** seed 1337, at cruise above the fresh-spawn main-spire
    viewpoint.
  - **A slot that fails to warm-load:** fade to the starfield, then warm-load the
    showcase.
  - **Bundled showcase snapshot** in `res://`, so even the first launch shows an
    image. Regenerate it when the showcase id changes.
  - **Grave key at the menu:** opens only the small "Live world" drawer.
  - **B5 per-screen poses on the live world:** deferred to C10, limited to pitch and
    FOV changes, never above 18 km.
- **Menu interactivity during warm-load** (user, 2026-10-22; replaces "delay
  interactivity until the last stall"):
  - Lock the menu buttons only through the worst early freezes, about 10 s, showing
    "PREPARING NN%".
  - Unlock once the load is past the terrain stage and the last second had no frame
    over 100 ms. Unlock anyway after a safety cap.
  - Quit stays usable.
  - Minor later hitches are accepted.
- **Audio is out of scope for Phase C** (user, 2026-10-21). Leave world and weather
  audio as they are; C5 does not suspend AudioWeather.
- **Quitting from the live menu writes nothing.** Saves are held; the last save is
  from Save & Exit or the previous session.

## Measurements (4070, current main after the LOD fix)
| Measure | Value |
|---|---|
| 16 km stationary | 8.0 ms wall (p95 9.9), GPU 6.5 ms |
| 16 km moving at 1.1 km/s | 9.6 ms wall (p95 12.4) |
| Load to ready | 22.7 s (preparing 4.5, terrain 6.4, surroundings 11.2, ready 0.5) |
| Process dedicated VRAM | about 2.1 GB |
| Working set | 2.7–2.9 GB |

- No LOD Low preset or low-cost clouds are needed. Clouds are not the main cost at
  16 km on the 4070.
- The real problem is main-thread startup stalls:
  - a 2.63 s frame in the surroundings stage, with no pipeline compiles;
  - frames of 0.57–0.94 s in the terrain stage;
  - a 409 ms tree work frame.

## Facts the design depends on
- **`ringworld/runtime/active_world_directory` is global.** The proxy mesh cache
  and the vegetation cache read it at runtime. While a warm world exists, nothing
  may call `select_slot` or `delete_slot`; tear down first (assert this).
- **`copy_slot` stamps `last_played` on the copy.** Continue must bind to `warm_slot`.
- **The Explorer polls movement keys directly**
  (`RingVoxelExplorerController.cs` ~2903) and handles V, 1–5, wheel, capture and
  Esc in `_UnhandledInput`. Cinematic mode must ignore the keyboard entirely.
- **`SetBenchmarkCanonicalPosition` and `RestoreSavedState` force a re-anchor.**
  Never call them per frame.
- **Terrain3D chases the Explorer.** `RingTerrain3DGameplay._Process` →
  `ServiceStreaming(explorer)`, so it needs a coverage-focus override. Readiness:
  `HasCollisionCoverage` / `HasVisualCoverage`. Pattern to follow: `EmitterSiteTeleport.cs`.
- **`HoldSavesUntilWorldReady` gates all saves**, including autosave,
  `SaveForSceneTransition` (which returns Busy) and the `_ExitTree` save.
- **The underground switchboard rewrites its flags every frame.** Reuse it only
  through a new `PresentationSuspendGroups` mask (AudioWeather | Water).
- **World HUD CanvasLayers draw over StartMenu.** Use
  `RingLodPauseMenu.SuppressGameplayHud` / `RestoreGameplayHud`. The world's
  `LiveTuningDrawer` must not take the grave key.
- **The flyover is drawn by a single TextureRect** (`RingMenuSpaceBackdrop.cs`
  ~364), so the crossfade is that rect's alpha. Retire the flyover afterwards
  (about 6.5 M triangles).
- **The launcher already adds the world under `/root`** as a sibling of the
  current scene.

## Work orders (execution order)
**Vertical slice first: C1 + C2.** Warm-load, place the camera at cruise above the
save spot, and crossfade. In the slice, Continue still tears down and loads
through the loading screen.

### C1. Background loader mode and warm-world owner
- **Loader API:**
  - `loader.warm_saved_world(info)`
  - signals `world_warm(world)` and `warm_failed(msg)`
  - properties `warm_world`, `warm_slot`, `is_background`
  - async `tear_down_warm_world()`
- **Background mode:**
  - no overlay or screen; null-safe `set_stage` and failure path;
  - StartMenu stays current;
  - the launcher is added hidden, with meta `world_loading_background`;
  - keep `disable_3d` and the SubViewport holds until C2;
  - hold Explorer, GridEditController, LiveTuningDrawer and LodPauseMenu.
- **Ready:**
  - the same gates as today, minus the spawn gate;
  - no `current_scene`, `begin_play_session`, `ReleaseWorld` or mouse capture;
  - save the slot settings, free the launcher, keep the bridge as the holder.
- **Eligibility:** `WorldSession.get_live_menu_slot()`, a pure helper.
  `--no-warm-world` disables warm-loading for tests.
- **Teardown:** the `_cancel_and_return` drain without changing scene, then clear
  `active_world_directory` and `active_slot`. Verify `CancelStartup` is safe on a
  ready surface; otherwise add `BeginShutdown()`.
- **Menu hooks:** `_load_slot`, `_create_new_world` (both branches), deleting
  `warm_slot` and Quit all await teardown first. Continue binds to `warm_slot`.
- **Validation:**
  - existing loading and save-exit lifecycle tests unchanged;
  - new `tools/menu_warm_world_lifecycle_test.gd` (isolated APPDATA with a refusal guard):
    - warm at boot; StartMenu is current; no overlay;
    - the state file is unchanged after more than 35 s;
    - teardown is clean;
    - another slot loads through the loading screen.
- **Escalate if:** the launcher cannot run hidden and non-current, or the proxy
  `_ExitTree` 5 s waits fire after the drain.

### C2. Compositing, presentation hold and crossfade
On `world_warm`:
1. `SuppressGameplayHud`.
2. One `TeleportToCanonicalMeters(flight)` to cruise above the save spot, then
   clear the focus override.
3. `PrepareReveal`, with the minimap viewport kept off.
4. Set `disable_3d = false` while the flyover is still opaque.
5. Wait for the first frame, a 2 s settle, and 1 s with no frame over 100 ms.
6. Tween the backdrop alpha over the crossfade time.
7. Retire the flyover (`RingMenuSpaceBackdrop.Retire()`).

- **Survey readout:** reads Explorer telemetry once the flyover is retired.
- **Celestial and cloud clocks:** paused while on the menu.
- **Flyover pose:** cruise altitude when a warm-load is expected.
- **Validation:**
  - backdrop retired, `disable_3d` false, HUD hidden;
  - grave opens only the menu drawer;
  - W/A/S/D do not move the Explorer;
  - the mouse stays Visible;
  - a GPU capture screenshot.

### C3. Startup stall attribution and splitting
- **Target:** no main-thread frame over about 100 ms after the menu is interactive.
- **Attribution:** per-owner `_Process` ms, GC deltas and RS sync markers in
  `RingWorldLoadingBridge.GetStartupDiagnostics`.
- **Suspects:**
  - `Task.Wait(5000)` paths;
  - synchronous uploads (global LOD, far ring);
  - the trees' 409 ms frame;
  - Debug-build GC.
- **Fix:** split the work using the existing staged-startup and budget patterns. In
  background mode, do not gate warm on trees.
- **Fallback:** if stalls remain, keep the menu non-interactive until the last one
  (user decision).
- **Escalate** to the Deep role if a stall is inside the engine.

### C4. Explorer scripted flight and menu flight director
- **Explorer partial `RingVoxelExplorerController.ScriptedFlight.cs`:**
  - `BeginScriptedFlight`, which ignores all input;
  - `SetScriptedFlightCommand(canonicalVelocity, yaw, pitch)`, integrated through the
    noclip physical-ring path with `RecenterCollisionOrigin(force:false)`;
  - `PlaceScriptedFlight`;
  - `EndScriptedFlight`;
  - continuous horizon correction while scripted.
- **Save manager:** `TryGetStagedPlayerPose` (captured in `ApplyDocument`, refreshed
  on save) and `HoldSaves(bool)`.
- **`scripts/ui/RingMenuFlightDirector.cs`:**
  - `StartLoiter`, `Rush`, `Descend`, `Ascend`, `Hold`;
  - signals `arrived` and `failed`;
  - an altitude P-controller with a hard ceiling of 18 km above ground.
- **Validation:** `tools/menu_flight_director_test.gd` over 120 s of loiter:
  - altitude within ±500 m and always below 18 km;
  - a re-anchor without a position jump;
  - saves held;
  - keys ignored.

### C5. What to suspend at menu altitude
- **Water:** via `PresentationSuspendGroups`. AudioWeather is deferred, since audio
  is out of scope.
- **Sun PSSM, contact and height shadows:** a general rule, suspended when camera
  altitude above ground exceeds the shadow max distance × fade start. Verify the
  preset distances and that cloud shadows do not depend on PSSM.
- **Terrain3D:** `SetCoverageFocusOverride(saved spot)`, which stops the chase and
  pre-streams the destination.
- **Voxel/smooth grid:** `SetStreamingCanonicalFocusOverride(saved spot)`.
- **Leave unchanged:** grass and trees (already self-suspending), the LOD preset and
  cloud quality.
- **Target:** at least −0.5 ms stationary with no visible difference. The underground
  round-trip smoke test must pass.

### C6. Continue transition
1. The UI fades out.
2. `Rush(staged pose)`.
3. Gate: collision and visual coverage at the target. Hold at about 2 km if not ready.
4. `Descend`.
5. Handoff:
   - `EndScriptedFlight` → `RestoreSavedState`;
   - settle loop;
   - clear overrides and suspend groups;
   - `current_scene = world` and free StartMenu;
   - `begin_play_session`, `ReleaseWorld`;
   - HUD fade-in, then mouse captured.
6. Resume the clocks.

- **Timeout:** 60 s, then a 0.3 s fade, placing the player directly.
- **Test:** `tools/menu_continue_transition_test.gd`:
  - final pose within 0.5 m;
  - an autosave within 35 s;
  - world is the current scene;
  - mouse captured, HUD visible, play session active;
  - the player never goes below terrain.

### C7. Save & Exit without a scene change
1. `SaveForSceneTransition`.
2. `end_play_session`.
3. `HoldSaves(true)` and refresh the staged pose.
4. `bridge.HoldWorld()` (new).
5. Unpause.
6. Suppress the HUD.
7. Ascend.
8. Apply the suspend set.
9. Instantiate StartMenu with `live_world=true`, make it current, and fade the UI in.
10. Pause the clocks.

Ineligible worlds keep the scene-change path. Rewrite
`world_save_exit_lifecycle_test` for both paths.

### C8. Grave-key tunables (menu drawer Camera tab, "Live world" group)
| Setting | Default | Range |
|---|---|---|
| `CruiseAltitudeKm` | 16 | 8–18 |
| `LoiterRadiusKm` | 30 | 5–60 |
| `RushSpeedKmPerSecond` | 1.2 | 0.3–3 |
| `DescentSeconds` | 12 | 4–30 |
| `CrossfadeSeconds` | 2.5 | 0.5–6 |

- The control count goes from 57 to 62.
- Update `menu_flyover_tuning_checks.gd`, `grave_menu_interaction_test.gd` (stale:
  it expects 50) and the checklist.

### C9. Tests and docs
- `main_menu_space_smoke_test`: the fake slot must be ineligible; add a no-slot fixture.
- `world_loading_lifecycle_test` and `ui_scaling_smoke_test`: pass `--no-warm-world`.
- New lifecycle tests from C1, C4, C6 and C7.
- `tools/run_menu_lifecycle_gates.ps1`: kills Godot once and gives each test an
  isolated APPDATA.
- Update the Phase C section of the plan with these measurements and corrections.

## Refinements (lead's final pass)
- **Persistent owner:** `/root/RingWarmWorld` (`scripts/Loading/ring_warm_world.gd`)
  with a state machine:
  - main cycle: NONE → WARMING → MENU → CONTINUING → PLAYING → RETURNING → MENU;
  - also TEARING_DOWN and FAILED.
- **`WorldSession.select_slot_for_preview(slot)`:** no `_touch_slot`, no
  `end_play_session`. Plain `select_slot` runs only at the Continue handoff.
- **Placement:** move to cruise right after `IsWorldLoadComplete` and before
  surroundings completes, so far/proxy owners build for the cruise view.
- **Background startup slices:** a `StartupSliceMilliseconds` property on the
  far viewport, global LODs, the cloud layer, trees and POI streamer, set to 1 ms
  by the bridge in background mode. This is a constant, not a tunable.
- **Shadow rule (C5):** a general "no receiver in range" gate in
  `RingGraphicsQualityController.LocalShadows.cs`. The Terrain3D window stops
  chasing above about 2 km above ground unless `EnsureCoverage` is pending.
- **Foreground-loaded worlds:** a live-candidate world entered through the loading
  screen can be adopted (`adopt(world, bridge)`), so its Save & Exit also takes the
  live path.
- **Test isolation:** `ui_scaling_smoke_test` uses real saves and must disable
  warm-loading.

## Risks
- Stalls that cannot be split.
- A missed `select_slot` path while warm.
- A world `_Ready` touching `current_scene`, the mouse or the root viewport.
- Flyover and world memory together on the A1000.
- `GetTree().Paused` during the ascent.
- GC in the Debug build.

## Revision: flyover removal (supersedes C2's flyover parts and C8)
Lead design, 2026-10-22. Execution order: C3 (in flight; tag and skip any
flyover-owned stalls, then re-baseline after D1) → D0 → D1 → D2 (can overlap D1
once the warm-world interfaces are fixed) → D3 → D4 → C4 → C5 → C6 → C7 → C9 → C10.

- **D0, housekeeping:** drop the uncommitted sun-match patch (saved to the scratchpad).
- **D1, boot cover and live reveal with no flyover (vertical slice).**
  - New files:
    - `scripts/ui/menu_boot_cover.gd`: snapshot with a Ken Burns drift that converges
      to identity, or starfield; `settle`, `crossfade`, `retire`.
    - `scripts/ui/menu_starfield.gdshader`.
    - `scripts/ui/menu_snapshot.gd`:
      - `load_valid`, `capture_world_only` (`force_draw(false)` with the menu hidden;
        skipped headless), `write_async`;
      - PNG ≤ 1080 px tall, plus a sidecar json holding format, render epoch, kind,
        state stamp, pose, sun and `last_warm_ms`.
  - **Bridge:** `SetPlacementPose`, `PlacementUsedSnapshot`, `GetCameraPose`.
  - **Placement:** at the snapshot pose when it is valid (finite, ≤ loiter radius
    from the save, 2–18 km above ground). Otherwise at cruise above the save spot.
  - **Stale snapshot** (state stamp mismatch): show it anyway, place at the actual
    spot, dissolve, then recapture.
  - **Capture:** at reveal when there was no snapshot, or it was stale or placement
    fell back. Later also at C7 Save & Exit once at cruise.
  - **Remove the menu's use of the flyover:** backdrop, flyover drawer, the survey
    readout's flyover source, and its caption. The survey readout shows
    "PREPARING NN%" (from `RingWorldLoading.get_progress`), then the Explorer
    telemetry.
  - The flyover files stay on disk until D3.
  - **New exports:** `live_world`, `freeze_cover`, `snapshot_override_path`.
- **D2, showcase world.**
  - `res://resources/menu/showcase_world.json` holds the frozen Standard runtime
    `generation_settings` for seed 1337 and a `showcase_id`.
  - Tools: a generator tool and a headless definition guard test.
  - Directory: `user://showcase/<id>/`; `active_slot` stays 0.
  - `WorldSession.get_live_menu_target` and `select_showcase_for_preview`;
    `request_load_selected_world` accepts the showcase.
  - `RingWorldSaveManager.PermanentlyReadOnly`, so the showcase never writes saves.
  - `RingWarmWorld.start_target`.
  - A failed slot warm-load falls back to the showcase.
  - Continue binds to the most recent slot, Compact included, and loads it through
    the loading screen.
  - A bundled showcase snapshot ships in `res://`.
- **D3, delete the flyover code.**
  - `RingMenuSpaceBackdrop`, `MenuFlyover*` (except the move below), the
    `menu_flyover_*` and unreferenced menu shaders, `menu_flyover_defaults.tres`
    and `menu_flyover_tuning_checks.gd`.
  - Move `MenuFlyoverSunshineLayer` to `tools/support/SunshineTestRig.cs` for the
    four cloud GPU tests. Split out `menu_cloud_history_gpu_test --gameplay-history`.
  - Retarget the menu smoke, grave-menu and docs/README references.
- **D4, live-world drawer.**
  - `MenuLiveWorldSettings` / `MenuLiveWorldTuningDrawer`, persisted to
    `resources/menu/menu_live_world_defaults.tres`.
  - Rows: cruise altitude 16 (8–18), cruise pitch −25, crossfade 2.5 s, snapshot
    drift 8%. Later work orders add loiter radius (C4), and rush speed and descent
    seconds (C6).
- **C7 change:**
  1. pause clocks right after `SaveForSceneTransition`;
  2. ascend;
  3. capture the snapshot at cruise before the menu UI exists.
