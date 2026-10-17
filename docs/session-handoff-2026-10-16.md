# Session handoff (2026-10-16)

This follows [session-handoff-2026-10-15.md](session-handoff-2026-10-15.md); its three "Update" sections hold the grass details. Machine setup and agent rules are unchanged from that doc.

## Machine notes

- This session ran on the **RTX 4070 desktop**. The `terrain_surface_include_regression` goldens are byte-exact A1000 captures and fail on the 4070 even with no terrain changes. GPU ms numbers from the 4070 do not compare with the A1000 baselines.
- Never run windowed or GPU tests while another Godot process (the user's editor or game) is open.

## 1. Grass: closed out for now

Committed in `271763c` and pushed to `main` and `perf-hotpath-optimizations`.

- **Cards render style:** `GrassRenderStyle` Blades/Cards inside `RingStandingGrassLayer`. Blades is still the default and its fingerprints are unchanged.
- **Hero blades:** scrapped (user decision).
- **GPU placement:** card placement runs on the GPU vegetation compute, with the CPU MultiMesh as fallback.
- **Grouping:** adaptive grouping is on for far card tiers, from about 206 m.
- **Batch budget:** the Cards batch budget covers every batch, so nothing is deferred.
- **Settings:** S8 added 34 grave-key card settings and preset card reach/density.
- **Not yet re-run since S8:** the windowed GPU tests. Run them one at a time with Godot closed:
  - `gpu_grass_card_culling_test`
  - `grass_card_layer_gpu_smoke`
  - `grass_card_color_match_gpu_test -- mode=color` (expect 9/9)
  - `gpu_grass_integration_test`
  - `grass_adaptive_grouping_test`
  - `gpu_grass_grouped_phases_test`
  - `grass_live_tuning_smoke_test`
- **Open user question:** the VeryHigh, UltraVideo and Cinematic5090 presets give cards a 450/500/600 m reach. The user's original decision was 400 m. Ask whether to keep these values.
- **Remaining plan when grass reopens:**
  - S9: Cards terrain coverage tint and sheen.
  - S10: full invariant suite.
  - S11: in-game A/B and perf on the A1000, then the default flip.
- **Loose ends:**
  - Frustum churn beyond ±0.75 m sway.
  - The grouped image-parity pose sees no grouped pixels.
  - The A1000 terrain goldens.
- **Uncommitted noise:** `tools/fixtures/terrain_surface_include_regression/*.import` carry line-ending-only changes from Godot runs. They were deliberately left out of the commit.

## 2. Emitter grid and climate machines: spec done, implementation paused

The spec is [emitter-grid-climate-machines-spec.md](emitter-grid-climate-machines-spec.md). Its USER DECISIONS section is binding. Nothing is implemented yet.

**Emitter grid.**
- The existing beam emitters relay along the ring only. There are 6 chains, each a closed loop of 314 km chords through the main spire. Mid-span the chords sit at about 4–6 km, inside the cloud deck.
- Links are near-invisible. They are seen mainly through a **one-way pulse** that leaves the main spire and circles the whole ring about every 5 minutes, at about 105 km/s with auto speed. Period and speed are separate grave-key settings.
- The pulse is faint by day and more visible at night. An eclipse makes it night-bright. It is driven by the saved weather clock.
- Lead pulse defaults:
  - prograde direction (`PulseDirection` setting);
  - all 6 chains in sync;
  - 104.72 km/s, so 3.0 s per link and about 2 s overhead;
  - 2.5 s trail and 10 s wake;
  - the head drawn as analytic motion blur, with sprites only beyond 300–600 km;
  - a 1.5 s relaunch flare at the main spire;
  - at most about 90 lit links.

**Climate machines.**
- A separate structure: **Wall Engines**, 200 paired bays built into both rim walls, one pair per emitter-row gap.
- **6 of the 200 are offline**, selected by the world seed (about 3 storms and 3 droughts, none near the main spire).
- An offline machine produces a parked storm or a drought through the weather system. It has no direct effect on the grid; the grid reacts only through the weather.
- New terrain recipe `ringworld_surface_climate_v6`. It sits behind a launcher toggle until C3, and phase C3b then makes it the default for new worlds. v4 and v5 worlds stay byte-identical.

**Phases** (see the spec's phase table):
- G0 records v5 terrain and weather goldens first.
- After that the G track (grid) and the C track (machines) can run in parallel, but shared files are serialised as the spec lists.
- The critical path is G0 → C1 → C2 → C3 → C4 → V1.

**Technical risks to verify early:**
- T2: a depth-writing beam core versus Sunshine's temporal history. Clouds composite after transparent geometry.
- T3: the cost of a Sunshine point light.
- T4: near-field rim-wall presentation.

## 3. Still pending from earlier sessions

- **Cave direction B:** the user still needs to judge the near-surface voxel look and approve B2. Emitter Milestone 2 (underground) stays paused until then. See [voxel-cave-direction-handoff.md](voxel-cave-direction-handoff.md).
- **Cloud levers 2+:** not started. See [perf-ranking-2026-10-15.md](perf-ranking-2026-10-15.md).
- **The `~libterrain…dll` temp file** should probably be untracked, with `~*.dll` added to `.gitignore`.

## Update (end of 2026-10-16 laptop session): continue on the RTX 4070 desktop

Commits on `main`: `e7ba111` (grid G0–G2 + G4/T2, climate C1–C2, cloud history fixes, eclipse E0) and `ce2b360` (eclipse E1–E2). Nothing uncommitted except a stray `install.cmd` (a Claude Code installer, not project code).

**Clouds** ([cloud-diagnostics-2026-10-16.md](cloud-diagnostics-2026-10-16.md)):
- Fixes are in and were measured on the A1000:
  - `MovingHistoryWeight` back to 0.85 and night ambient back to the code default;
  - a tolerant history gate (alpha band 0.20–0.65);
  - velocity-aware continuity decay instead of hard resets;
  - log-depth blur metadata;
  - per-callback `z_far` instead of the stale scene-data UBO. That bug made opaque depth past ~1.7 km count as beyond Sunshine's reach in some sessions.
- Two failures predate these changes: `sunshine_gpu_shadow_smoke_test` (near slice never authoritative) and `sunshine_menu_smoke_test` (button hit-test). `sunshine_storm_persistence_test` still expects the old 128-step defaults.

**Emitter grid:**
- The T2 depth-writing core (4 px) and the G4 far layer are in.
- **Pending user decision:** the per-pixel near/far beam handoff. Over terrain a beam stays in the near layer; over sky it crossfades across the live 120–172 km cloud band. This replaces a hard 171 km cap. The user asked to note this for later.
- Perf for the core and far layer is not measured, because the A1000 throttled. Rerun against the G7 budgets.

**Eclipse false sun** ([eclipse-false-sun-spec.md](eclipse-false-sun-spec.md); binding decisions at the end, items 1–9):
- E0 (pure core), E1 (director, settings, drawer, debug HUD, force/jump/scrub) and E2 (pitch-black darkness) are done. They are headless-tested only; **no windowed run yet**.
- First on the 4070:
  1. Open the project and check that the shaders compile. `ring_atmosphere.gdshaderinc` now declares the global `ringworld_night_light_scale`.
  2. Grave-key → Lighting → Eclipse debug → Force eclipse, then step through the phases. A/B with the "Eclipse darkness" toggle.
  3. Run `tools/eclipse_darkness_capture.gd`. It has never run, so it may need small fixes; usage is in its header. Check that totality is near black but keeps the stars, the far arch and the pulses, that clouds don't show through, that the far-cloud night terms look right, and that frames outside the event are unchanged.
- **Next phases:** E3 hub-light "noon everywhere" mode, E4 visuals (hub body, hub beams, ignition fronts; the director's `GridTimeSeconds` should feed `EmitterGridBeamLayer`), E5 collapse, E6a/b PDA and audio, E7 capture route and perf.
- **Known blocker for E4 hub beams:** Sunshine only honours a depth-writing transparent after `refresh_compute`. This was fixed by the `z_far` change; re-verify it with `--no-refresh` in `scratchpad eg_t2` style captures.
- The 4070 numbers don't compare with the A1000 baselines; the terrain regression goldens are A1000-exact.
