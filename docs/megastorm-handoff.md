# Megastorm handoff (2026-10-30)

Read this first, then `docs/megastorm-design.md` (user decisions at the top, then the per-WO "status" blocks under section 8). The containment field is designed (`docs/containment-field-design.md`) but not started.

## Where things are

- **Branch:** `megastorm` (pushed). Last checkpoint is `266e76d`. `main` is `b8b175a` (test tooling). Nothing from the megastorm is on main yet.
- **Commits on the branch:**
  - `bf9b81b` and `bb9af39`: WO1-WO4b.
  - `ca2bbfb`: WIP checkpoint.
  - `266e76d`: spawn button, WO5b, WO7, WO8.
- **Uncommitted:** work from the three agents still running when this was written (see "In flight").
- **How to see it in game:** start the game, choose Continue, open the grave key (`) menu, go to Clouds → Megastorm, and press **Spawn megastorm**.
  - **Spawn distance ahead** defaults to 60 km, and the wall travels toward where you face.
  - **Stop megastorm** removes it.
  - **Speed** is 5-500 m/s, default 350; 60 km takes about 3 minutes.
  - A spawned storm is saved with the world.
- **Command-line alternative:** `.\tools\use_godot_48.ps1 --megastorm-xi=-60`. PowerShell drops a bare `--`, so the game reads engine args too.
- **User verdict so far:** they saw the wall in game and called it "very good work so far". They want to rework the hum and the sonic boom, and may supply their own sounds. For the file names and specs, see "Sound" below.

## Work orders

| WO | What | State |
|---|---|---|
| 1 | Pure model (`scripts/Weather/Megastorm/MegastormModel.cs`) | done. The trailing record T is set so rain ends about 383-394 km behind the wall (a quarter-day pass). TTA now solves the true edge crossing (meander). |
| 2 | Node `RingMegastorm.cs`, save/load, settings | done. Spawn and Stop buttons and the distance slider were added later. Speed, trailing length, warning reach and sound volume are in the menu. |
| 3 | Weather injection (kind 6, profiles 12 and 13, natural suppression) | done. Wall-onset D ramps over 0-9 km; WO14 owns the darkness. |
| 4 / 4b | Near wall in Sunshine (strip map, reprojection, cadence), face visible from ahead | done. Near shadow 6.7 Hz floor, pipeline prewarm. |
| 5 / 5b | Far wedge pass, main-view companion over terrain, S6 radius fix, shared day look | done but **look not met** at 150-1000 km (see open issues). At High the near Sunshine march owns the wall to about 120-170 km. |
| 6a | Arrival bands, `StormFactor`, wall updrafts, hail swaths | done |
| 6b | `MegastormSound` maths, subtitles-only PDA core (`scripts/Pda/`), lines and database JSON | done. Line wording is the user's draft, unchanged. |
| 6c | Audio voices (procedural, `tools/media/weather_sfx_gen.py --only megastorm`), airborne gate, tremor (`ring_ground_tremor` global) | done. Levels are untuned; renders are in `.godot/captures/megastorm/wo6c/*.ogg`. |
| 7 | Near strike stream and "bolt from the blue" | done. Far flashes (>50 km) have no sprites; the user tabled that. |
| 8 | Field publishing seam (`MegastormFieldCoupling.cs`, `scripts/ContainmentField/ContainmentDisturbance.cs`) | done against stubs. The hooks for the field are in the doc's WO8 status. |
| 10 | Hazard model (`MegastormHazards.cs`): boom kPa, 140 m/s wind, wake, cover probe, scythe probability, events, PDA hazard lines | done. Planning only: no damage. |
| 9 | Acceptance (captures, perf table, headphone check) | not started |
| 11 | Hazard visuals and audio (blast, bullet-rain streaks, wake fog, ear pop) | not started |
| 12 | Lightning scythes | **in flight** |
| 13 | Field strikes | not started (after 12) |
| 14 | Darkness under the wall | **in flight** |
| — | S6 far layer natural march off (user decision) | **in flight** |

Doc order for what remains: WO14 → WO11 + WO12 → WO13 → the far-look pass (user: "after the rest") → WO9.

## Update (2026-10-30, about 23:30)

The three in-flight agents below all finished and were committed in the checkpoint after `014246e`. Status blocks for each are in `docs/megastorm-design.md`.
- **WO14 (darkness):** done.
  - Real-game noon sheet: `.godot/captures/megastorm/wo14/sheet_real_path_noon.png`.
  - Under the wall it is about 0.05-0.08x a clear day.
  - Ordinary storms are bit-identical.
  - Deviations, all needed to meet the accept list: the eye-lock mechanism; a flash term in the haze; the rain veil and distant curtains now take K.
  - Open: darkness lands a few km after the boom, because D is only about 0.3 at xi 0-5 km.
- **WO12 (scythes):** done.
  - Rate, geometry and the cut table are all within spec.
  - Open:
    - far-view streaks are left for the far-look pass;
    - near the ground the sheet is often hidden by terrain or trees (a user look check);
    - a reduced-flashes capture is unproven.
- **S6 natural far layer march:** off by default (`RING_FAR_LAYERS_NATURAL=1` turns it on). Untriggered cost is back to pre-fix; turning it on costs +0.21 ms on the test shots.

Next is WO11, then WO13, then the far-look pass, perf and WO9.

## In flight when this was written (historical)

Three background agents were started at about 20:10 on 2026-10-30. If this session ended, assume their work may be **partly done and uncommitted**. Check `git status` and `git diff` against `266e76d`.

1. **S6 far layer sky natural march back off** (implementer).
   - User decision: the fix costs 0.4-0.7 ms for little visible change.
   - Approach: keep the radius maths, skip the natural march behind `far_layers_natural_enabled`, with env override `RING_FAR_LAYERS_NATURAL=1`.
   - Files: `materials/ring_far_cloud_layer_sky.gdshader`, `RingScaledFarViewport.CloudLayers.cs`.
2. **WO14 darkness** (lead). Owns the Sunshine `.glsl` files, the storm-light includes, the haze path, `RingMacroWeatherMap.glsl` and `day_side_atmosphere.gd`. Its accept list is in doc 8 WO14. Captures go to `.godot/captures/megastorm/wo14/`.
3. **WO12 scythes** (implementer). Lightning files, new scythe files and a crackle asset. Captures go to `.godot/captures/megastorm/wo12/`.

## Open issues (before merging to main)

- **Far look.** From 200 and 1000 km the wall does not read as a wall; it blends with ordinary clouds and the precursor deck. At 50-120 km the face is a pale haze-coloured mass. The user wants a dedicated far-look pass after the other WOs, with their art direction. The levers WO5b found:
  - face ambient (`OPEN_AMBIENT` 1.8);
  - precursor deck opacity at grazing angles;
  - the far anvil underside lacks cell texture.
- **Perf.**
  - Near the wall Sunshine runs about 0.9-1.05 ms above a natural storm (budget +0.15).
  - The far wedge pass runs +0.20 ms at 200 km (budget 0.10). Part of that is double work with the companion; one option is to feed last frame's main depth to the far pass as a mask.
- **Teardown crash.** `sunshine_shadow_toggle_smoke_test` crashes after PASS (exit -1073741819) more often on the branch (4/12) than on main (0/12). It is not WO2's node, and the cause is unproven. Several tests crash the same way at teardown on main too (the 2DArray "us is null" teardown; see the `docs/storm-revamp-plan.md` Deferred list).
- **Untriggered identity golden.** Fair, overcast and storm were not verified by capture after WO5b (the baseline resolution was wrong). Rebuild it at the batch point.
- **Wind.**
  - The render wind snapshot is still clamped to 80 m/s; only the flight query and weather query carry 140.
  - The power-law height profile has no 300 m cap, so wind above about 300 m exceeds 140 m/s.
- **Small items.**
  - The PDA bearing word assumes +direction = spinward (unverified).
  - `MegastormFieldCoupling` calls `GlowAt` at chance scale 1, so field ripples ignore the lightning-frequency setting.
  - A red upper-atmosphere sprite blob is visible in the noon sky at quarter-ring and antipode distances.
- **Housekeeping.**
  - A leftover worktree folder of about 3.5 GB is at `%TEMP%\claude\...\scratchpad\wtroot\rw`. It needs deleting by hand (filename too long for git or the agent).
  - `.godot/test_runs/appdata_spawncheck*` profiles (about 2 GB each) can be deleted when no longer needed.

## Sound (the user may replace these)

These are in `resources/audio/weather/`; OGG or WAV with the same names drops straight in:
- `megastorm_ground_hum_loop`: seamless, 20-60 Hz plus some audible harmonics, about 24 s.
- `megastorm_wall_sing_loop`: seamless tonal drone, about 30 s.
- `megastorm_boom_1` to `_3`: one-shots, a 3-5 s rolling wave with a hard low hit at the start.
- `megastorm_roar_loop`: seamless, about 30 s.

What the hum represents: the storm's pounding carried through the ring floor and rim walls at about 5-6 km/s, far ahead of the supersonic (Mach 1.02) wall. Airborne sound cannot get ahead of the wall, so the air is silent and only the floor hums until the boom (doc 5.2). The volume slider is "Megastorm sound volume" in the grave menu.

## Testing rules learned this session

- **Use the runner.** Run `tools/run_tests.ps1`; it takes the GPU queue itself. Never wrap it in `gpu_queue.ps1 -Wrap`, which deadlocks.
- **Use a narrow selector on a feature branch.** `-Changed` diffs against main and re-selects the whole branch: about 280 tests, about 60 of them GPU, about an hour. Per work order use `-Tests`, `-Files <your files>` or `-Since HEAD`. `-Since HEAD` is only small when the checkpoint is recent, so commit checkpoints often. Run a branch-wide run once, at a batch point.
- **Prove the real path.** All early "it works" evidence came from `tools/megastorm_capture.gd`, which rewrites the storm's anchor directly, and the user still saw nothing in game. Prove player-facing features through `tools/megastorm_real_path_check.gd`: start menu → Continue → grave menu → Spawn. It only runs on a profile whose path contains `appdata_spawncheck`.
- **What every commit must include:**
  - new `.import` files: a missing `RingMegastormStripBuild.glsl.import` broke the first pull on the other PC;
  - real `project.godot` changes: the `ring_ground_tremor` global was missing from `ca2bbfb`.
- **Line-ending churn.** Leave out modified `.import` files whose only change is line endings.
- **Mouse use breaks a test.** `ui_scaling_smoke_test` fails whenever someone uses the mouse during a run.
- **Pause testing when the user plays.** Tell the agents to stop launching Godot and building until the user says resume.
- **Keep the PC awake.** Run `tools/keep_awake.ps1` in the background while agents work. The runner also keeps the PC awake and uses sleep-proof timers.
- **Prefer the user's way of launching.** When telling the user how to launch, use the grave-menu button rather than command-line options.

## Next steps

1. Review and accept the three in-flight agents' reports (WO14 accept list, WO12 accept list, S6-off perf back to pre-fix). Then commit a checkpoint (see the rules above) and push.
2. WO11 hazard visuals and audio, then WO13 field strikes.
3. The far-look pass, with the user's art direction.
4. Perf pass: near-wall Sunshine cost and the far wedge cost.
5. WO9 acceptance:
   - captures, the perf table and the headphone check with the user;
   - one branch-wide test run;
   - user sign-off;
   - merge to main and push (ask first).
6. Then the containment field (`docs/containment-field-design.md`).
