# Progressive terrain loading: study (2026-11-21)

Study only. No production code was changed. Author: claude-lead.

User request (2026-11-20): "would like to see if progressive terrain loading would help anything. starting all terrain loading closer to the player and then progressively loading out; this would also start with the lowest terrain detail level and then go up in detail when the lower detail is done."

There are two ideas here:

- **(a) Nearest-first:** terrain loads from the player outward.
- **(b) Coarse-first:** the lowest detail level loads everywhere first, then finer levels refine it.

This study overlaps `docs/load-time-plan.md` L2-3d (the visible-first gate) and L3-1, which became L2-3d-3. It folds them in rather than adding a parallel track.

## Summary (plain language)

**The game already loads terrain mostly nearest-first and coarse-first:**

- Inside each detail level, sectors are already sorted nearest-first.
- On Continue, the fly-in turns the levels on from coarse to fine as the camera comes down.
- The coarse levels and the whole-ring terrain always lie underneath the finer ones. Across about 210 captured stills I found no holes.

**Two things are wrong today, and they matter more than the order:**

1. **On the loading screen, the terrain first builds in the wrong place.** For the first 2.3 s, every proxy level builds around the scene's default spawn point, 13,177 km from your save spot. The loader only moves the player to the saved pose after that. This is 39 to 43 % of all sector builds before the reveal, on 8 boosted workers. It also slows the Terrain3D window and the whole-ring terrain that run alongside.
2. **The whole-ring terrain is built in index order, not nearest-first, and the load waits for all 128 segments.** It is the last thing the Continue warm load waits for (ready at 15.3 s). On the loading screen it is ready at 17.7 s, and the gate then waits for the remaining streaming to go idle.

**What fixing them would give (estimates from the timelines):**

| Load | Today | With fix 1 | With fixes 1 and 2 |
|---|---|---|---|
| Loading screen (Load World), first playable frame | 20.8 to 21.2 s | about 19 to 20 s | about 15 to 16.5 s |
| Continue: Continue button usable | 20.1 to 20.3 s | about the same | about 18 s |
| Continue: playable (Continue pressed at once; the fly-in takes 12.1 s) | 32.9 to 33.1 s | about the same | about 31 s |

- Fix 2 is the existing L2-3d-3 order. This study recommends doing it next.
- **Coarse-first as a new loading scheme would not shorten any load.** Behind the loading screen nothing is visible, and the gate waits for everything. On Continue the order is already coarse-to-fine.
- **Where coarse-first could help is after a fast flight.** Terrain sharpens over about 3.5 s after you brake. In play, your Low preset builds proxy terrain on **one** worker thread, one level at a time, finest first. Reordering would bring the visible mid-ground forward by about 1 s. More workers right after a stop would help more, but that is a preset question (Q1).

## 1. What was measured

**Setup:**

- **Machine and settings.** DESKTOP-RAVA9C1, RTX 4070, 32 logical CPUs. VSync on, as shipped; `display_active=Enabled` was checked before every run. Window 1600 x 900.
- **Build.** Optimized C# (bin/OptRelease swapped into the Debug slot inside the GPU queue ticket, as `tools/perf_bench.ps1` does). The working tree was 18088c6 plus another lead's uncommitted scatter-collider edits.
- **World.** A robocopy of the user's slot 3, restored before every run. It is "World 3": generator v9, seed 1918787140, rivers and ground features, saved at the **Low** terrain preset (`Runtime.TerrainQuality 0`). The player stands in forest at around 18,238,708 m, across 198,293 m, 658 m.
- **Profile.** One isolated APPDATA profile, reused, with copies of the user's shader, Vulkan, ring and generated caches (a returning player). The real save was only read.

**The two paths:**

- **Continue.** The real `StartMenu`, the warm world behind it, then Continue pressed as soon as it unlocks (the `tree_visibility_world.gd boot_game` condition), the menu fly-in and the hand-off.
- **Loading screen.** `ring_world_loading.open_saved_world`, which is what Load World uses for a slot that is not warm behind the menu.
- "Cold load" here means a fresh process through the loading screen with warm shader caches. Empty caches were not measured.

**Fast-flight arrival** (Continue runs, after the hand-off and streaming had settled):

- Lift 450 m (or 350 m above the highest ground on the route), then scripted flight at 3.6 km/s (preset 4 + Shift speed) along +around for 60 km.
- Brake at 410 m above the ground, descend in scripted flight (about 3.7 s), end the scripted flight and press V. The landing point is a 2,971 m mountain slope.

**Probe** (scratch; kept with the evidence):

- Every frame it records each of the 15 proxy levels: active, sectors installed, staged uploads, worker slots, build requested, cache hits and rebuilt meshes.
- Every 6 frames it records:
  - which sector nodes hold a mesh, with distance and angle from the player for each new one;
  - the player pose;
  - the global terrain segment counts;
  - the Terrain3D streaming state and revisions;
  - the coordinator's settled flag and worker count.
- Stills were 800 x 450, every 0.5 s (0.33 s while landing), and were taken in separate runs from the timing runs. A still costs about 50 to 150 ms.
- Level-colour runs used the proxy shader's `debug_color`: red is 2 m, then orange, yellow, green and cyan, to blue at 4 km.
- Time of day was set to noon after attach. It does not affect streaming.

**Runs:** Continue + flight × 4 (timing, shaded, level colours, and one pilot run), loading screen × 2. Spread: hand-off 32.9 to 33.1 s, warm ready 15.3 to 15.4 s (loader clock), loading-screen ready 20.8 and 21.2 s (the second run had stills). One run per variant, so treat differences under about 0.5 s as noise.

**Not measured:**

- the A1000 laptop;
- empty shader caches;
- VSync off;
- the far-arch (`ScaledFarViewport`) pending sectors (inferred from the gate code);
- any other world or save spot. The save spot is forest floor, which hides most terrain from the arrival view.

**Evidence** (in `.godot/audit_baseline/common/terrain/progressive_loading/`):

- `continue_descent.png`: the Continue fly-in, shaded and in level colours.
- `fast_flight_arrival.png`: the fast-flight arrival, shaded and in level colours.
- `timelines/{load_t1,cont_t1,cont_d1}.events.json`: the raw timelines.
- `probe/`: `progressive_probe.gd`, `run_probe.ps1`, `analyze.py`, `stills_share.py`, `pair_sheet.py`.

## 2. Results

### 2.1 Time to first playable frame

Times are seconds from process start (probe clock). Loader marks are on the loader clock, which starts 0.0 to 0.4 s later.

| Mark | Continue (3 runs) | Loading screen (2 runs) |
|---|---|---|
| Start menu shown | 0.4 | (none) |
| Scene load + resource warm-up end | 7.5 | 7.6 |
| World attached | 9.1 to 9.4 | 9.0 to 9.2 |
| Global terrain queued | 9.4 to 9.6 | 9.5 to 9.8 |
| Terrain3D window ready (`surface_ready`) | 12.9 to 13.2 | 14.3 to 14.4 |
| Global terrain ready (all 128 segments) | 15.3 to 15.4 | 17.7 to 17.8 |
| Warm world ready / last proxy install before reveal | 15.5 to 15.8 / 14.4 | (none) / 17.9 to 18.0 |
| Reveal quiet (behind the boot cover) | 17.6 to 17.9 | (none) |
| `streaming_settled` (loading gate) | (none) | 20.2 to 20.7 |
| Continue unlocks | 20.1 to 20.3 | (none) |
| **First playable frame** | **32.9 to 33.1** (hand-off; fly-in 12.1 s, hand-off 0.5 s) | **20.8 to 21.2** |
| Proxy levels settled after it | +2.7 s | +1.0 to 1.3 s |

- **Continue: terrain is not on the critical path after the warm load.** The 12.1 s fly-in is the largest single item. Inside the warm load, the global terrain is the last owner (6.0 s from queue to ready).
- **Loading screen:** the global terrain takes 7.9 s from queue to ready, against 6.0 s on the warm path. The Terrain3D window is ready 1.4 s later than on the warm path. Attach happens at the same time on both. The difference is the 8 boosted proxy workers (`LoadingScreenBoost`, cores/4), which compete with both. Section 2.3 shows that a third of their work is wasted.
- **Between the last proxy install (17.9 s) and `streaming_settled` (20.2 to 20.7 s):** the proxy levels show no requests, uploads or workers. Per `RingWorldLoadingBridge.StreamingSettledForReveal` (:685), the rest is the 0.75 s dwell plus the far arch (`_far.GlobalGeometryReady`, `PendingTerrainSectorCount`). This was inferred and not traced.

### 2.2 The order used today (from the code, confirmed by the timelines)

- **Across levels:**
  - The coordinator starts at most one build per frame, within 2 ms. Candidates are ordered finest level first, unless a level has waited 30 frames (`BuildStartComesBefore`, RingTerrainLodCoordinator.cs:1324).
  - **Workers:** in play, the LOD profile's `ConcurrentWorkers` applies, which is **1 on Low** (resources/voxel/ring_lod_low.tres). Behind the loading screen the boost raises it to cores/4 (8 here), with 16 installs and 12 ms per frame (:77). The warm world behind the menu has no boost, so it uses 1.
- **Inside a level:**
  - Missing sectors are sorted nearest-first by sector distance to the current and predicted centre (`CompareSectorPriority`, RingTerrainProxySector.cs:1942; the sort is at :1792).
  - Each finished sector is staged as it completes, so installs are progressive.
  - With more than one worker slot, `Parallel.ForEach` (:4079) splits the sorted list into ranges, which mixes up the order.
  - Measured rank correlation between install time and distance: +0.6 to +0.98 in play (1 slot); +0.34 to +0.93 on the loading screen, counting only installs after the pose was restored.
- **Continue fly-in:** each level switches on when the camera drops below its far handoff. So the order is coarse-to-fine by construction. From `cont_t1`:

  | Level (spacing) | Switches on at (s after Continue) | Installed (first to 90 %) |
  |---|---|---|
  | 256 m | +5.0 (about 10 km above ground) | +5.1 to +5.4 |
  | 128 m | +7.6 | +7.7 to +8.1 |
  | 64 m | +9.1 | +9.2 to +9.6 |
  | 32 m | +10.1 | +10.2 to +10.7 |
  | 16 m | +10.7 | +10.8 to +11.4 |
  | 8 m | +11.2 | +11.5 to +11.7 |
  | 2 m / 4 m | +11.5 to +11.7 | +11.8 to +12.6, then 4 to 16 m refine for 2.7 s after the hand-off |

  Each level is built within 0.1 to 0.6 s of switching on, and the hand-off follows the 2 m level's last install by under 0.1 s.
- **Global terrain:** 128 segments are queued in index order 0 to 127 (RingTerrainGlobalLod.cs:806). They are not nearest-first. `StartupComplete` (:25) needs every segment and the morph snapshot.
- **Terrain3D:** one 3 x 3 region window, replaced as a whole (staged, then activated).
  - It stops following the player above 2 km above ground (RingTerrain3DGameplay.Streaming.cs:13).
  - In the 3.6 km/s flight below 2 km it re-requested its window about every 100 ms (request revision 223 to 276) and committed nothing until the brake.

### 2.3 Per-level timeline on the loading screen: the wrong-place build

`load_t1`. A "far" install is a sector more than 1,000 km from the save spot. Every far install lies about 13,177 km away, at the explorer's position before `RingWorldSaveManager.ApplyDocument` (:1026) restores the saved pose. In the timeline the pose changes at about 11.5 s.

| Level | Far installs (window, s) | Near installs (first to last, s) |
|---|---|---|
| 2 m | 9 (10.0 to 11.8) | 23 (12.4 to 14.9) |
| 4 m | 10 (9.9 to 11.8) | 25 (11.8 to 15.3) |
| 8 m | 18 (10.0 to 11.8) | 23 (12.3 to 14.9) |
| 16 m | 13 (10.0 to 11.8) | 23 (12.4 to 15.9) |
| 32 m | 11 (10.2 to 11.5) | 14 (11.8 to 13.8) |
| 64 m | 10 (10.0 to 11.5) | 9 (11.8 to 13.6) |
| 128 m | 9 (10.2 to 11.0) | 9 (12.6 to 13.6) |
| 256 m | 11 (10.2 to 11.4) | 8 (13.6 to 14.8) |
| 512 m | 10 (10.2 to 11.2) | 8 (13.8 to 14.6) |
| 1 km | 10 (10.8 to 11.4) | 9 (13.8 to 14.8) |
| 2 km | 11 (10.8 to 11.2) | 9 (11.8 to 14.1) |
| 4 km (terminal) | 7 (10.8 to 11.2) | 12 (12.1 to 17.9; rebuilt after `global_terrain_ready`, F2) |
| **Total** | **129 of 301 (43 %)**; second run 112 of 284 (39 %) | real work starts at 11.6 to 12.4 s |

- On the warm (Continue) path only the 2 m and 4 m levels built in the wrong place: 18 of 70 installs.
- The sector disk cache reported 0 hits (`LoadedSectorCacheCount`) on every level in every run, although slot 3 holds 519 MB of sector cache in 27 key folders, many of them empty. This matches L2-3 fact F6 and was not investigated further.

### 2.4 What the player sees

- **Loading screen:**
  - Nothing until the gate. The first revealed frame at the save spot is forest floor.
  - First revealed frame against 9 s later: the heatmap shows wind motion in grass and trees only.
  - No holes or coarse terrain in view. The gate (L2-1b) holds the reveal until the proxies are idle.
- **Continue fly-in** (`continue_descent.png`):
  - The terrain refines as each level switches on, from coarse to fine.
  - No hole and no sky through the ground in any of about 25 fly-in stills.
  - Hand-off frame against 4.5 s later: wind motion only.
- **Fast-flight arrival** (`fast_flight_arrival.png`):
  - At the brake, the 32 to 64 m levels draw 40 to 52 % of the lower half of the frame in level colours. The 2 to 16 m levels are off or stale.
  - The Terrain3D window commits 1.1 s after the brake. In the pilot run this was a visible detail pop on the near slope: smooth grey proxy, then textured rock.
  - The 8 to 16 m levels take over the mid-ground at 3.3 to 3.6 s.
  - All proxy levels are installed 3.5 s after the brake, just before touchdown at 3.7 s.
  - One ridge patch still changes shading in the 4 s after touchdown (heatmap SSIM 0.979).
  - No holes.
- **Frame times** (VSync on, stills off):

  | Phase | p95 | Max |
  |---|---|---|
  | After hand-off | 17.1 ms | 17.8 ms |
  | Continue fly-in | 17.3 ms | one 300 ms frame |
  | 3.6 km/s flight | 17.4 ms | 35 ms |
  | Brake to settled | 17.5 ms | one 126 ms frame |
  | Behind the loading screen | 36.6 ms | 884 ms at attach |

## 3. Answers

### 3.1 What order does loading already use?

**Proxy levels:**

- nearest-first inside each level (somewhat mixed when a level runs several worker slots);
- finest level first across levels, when they compete for workers;
- one worker in play on Low, 8 on the loading screen;
- on Continue, coarse-to-fine by altitude.

**Global terrain:** index order, all 128 segments before the load continues.

**Terrain3D:** one whole window around the player.

**The loading-screen path starts its proxies at the wrong position for its first 2.3 s.**

### 3.2 Would nearest-first help, and by how much?

**N1. Build at the saved pose from the start (fix the wrong-place build).**

- Removes 39 to 43 % of the proxy builds before the reveal, and frees 8 workers for the first 2.3 s of the surroundings work.
- Estimate:
  - The near proxies finish about 2.3 s earlier: about 13.6 s instead of 15.9 s, terminal level aside.
  - Less contention should bring the global terrain and the Terrain3D window 1 to 2 s closer to the warm-path figures. They are 1.9 s and 1.4 s slower today.
- Time to playable on the loading screen: −0.5 to −2 s. The gate still waits for the global terrain, the far arch and the dwell.
- Continue: about 0.
- Cost: none in generation work. Risk is low:
  - The start of streaming moves to after the pose is known.
  - The menu (warm) path places its cruise camera through the same explorer and must keep working.

**N2. Nearest-first global terrain and a gate on the segments in view (L2-3d-3).**

- About 10 of the 128 segments are within the 850 to 1,000 km visibility distance (plan 3.7).
- Estimate:
  - The warm world is ready at about `save_ready` plus a little: about 13.2 to 13.5 s instead of 15.3 to 15.8 s. Continue then unlocks and hands off about 2 s earlier (33.0 s to about 31 s).
  - The loading-screen gate is then bounded by the Terrain3D window (14.3 to 14.4 s), the near proxies and the far arch: first playable frame about 15 to 16.5 s, with N1 as well.
- Cost:
  - The morph snapshot, and the terminal level's rebuild on it (F2), need all segments today. The 4 km terminal level draws the horizon in the arrival view, so rebuilding it after the reveal would be a visible change.
  - Macro shadows and the cloud and planet shadow snapshots read the global terrain.
  - A very fast flight in the first seconds may meet plain hull out of view; the user approved this on 2026-11-12.
  - This is the L2-3d-1 design question, already written down.

**N3. Load-balanced partitioning inside a multi-slot level.**

- Only matters on the loading screen (several slots per level), where nothing is visible.
- Estimate under 0.3 s. Not worth an order unless N1 and N2 leave the proxies on the critical path.

### 3.3 Would coarse-first help, and by how much?

**Loading screen: no.**

- Every level is hidden until the gate, and the gate waits for all of them. Reordering does not change the total work.
- Coarse-first could only shorten the load if the reveal were allowed before the fine levels finish. That breaks the user's rule from 2026-11-12: "out of view only", and the arrival view must look final.

**Continue: already coarse-first.** The levels switch on by altitude.

**Fast-flight arrival: a small gain from reordering, a larger one from workers.**

- With one worker, the 2 to 16 m levels build one after another after the brake, finest first, in 3.5 s in total.
- Coarse-to-fine (32 m, 16 m, 8 m, 4 m, 2 m) would put the 16 to 32 m levels about 1 to 1.5 s earlier. Those levels own most of the mid-ground at 400 m above the ground.
- The 2 to 8 m levels would arrive correspondingly later, but they sit mostly under the Terrain3D window, which commits independently at +1.1 s.
- The refine still ends at about +3.5 s, before touchdown.
- Running 2 to 4 workers for a few seconds after a fast-travel stop would cut the 3.5 s to an estimated 1 to 2 s. This is unmeasured. It costs CPU during play (Q1).

**"Lowest detail everywhere first, then refine" as a separate pass: not recommended.**

- It adds generation work: each area would be built twice at different detail.
- It is not needed. The coarse proxy levels and the whole-ring terrain already lie under the finer levels, and resident coverage (af01e9e) hides a coarse level only where the finer level is installed. So there is nothing to fill and no hole was observed.

### 3.4 Costs and risks

| Change | Extra generation work | Worker contention | Resident coverage / near handoff risk | Look risk |
|---|---|---|---|---|
| N1 build at the saved pose | none (−129 builds per load) | lower | none: same windows, built once | none |
| N2 nearest-first global + gate in view | none | slightly lower (no waiting segments) | morph target per segment (F2); hull roof out of view | terminal level and horizon if rebuilt after the reveal; needs the L2-3d-1 design |
| N3 load-balanced partitioning | none | none | none | none |
| Coarse-first build order (arrival only) | none | none | Coarse-first suits resident coverage: coarse draws until the finer level is installed, so a finer level that arrives late only leaves coarse terrain showing, never a hole. The finest-level near fade against Terrain3D is readiness-gated and does not depend on the order. | mid-ground sharper sooner; the 2 to 8 m rings later, mostly under Terrain3D |
| Arrival worker burst (2 to 4 workers for a few seconds) | none | yes: competes with Terrain3D generation, trees and scatter in play; frame p95 must be checked | none | none |
| Two-pass coarse-then-fine | about +25 to 50 % sector builds | more | none | none |

## 4. Recommendation

1. **Do N1 now (order PL-1).** Small, low risk, removes a third of the loading-screen proxy work.
2. **Make L2-3d-3 (N2) the next load-time step (order PL-2), with its L2-3d-1 design first.** This is where "nearest-first" saves real seconds: about 4.5 to 6 s on the loading screen together with N1, and about 2 s on Continue. It replaces L3-1 as already agreed. The L2-3 orders 3a, 3b and 3c it depends on are not implemented yet. PL-2 only needs their measurement part (L2-3m, which PL-0 supplies in part) and the design.
3. **Do not build a coarse-first loading system.** If the user wants faster sharpening after fast flights, do the arrival-only order PL-3 (coarse-first build order plus a short worker burst after a fast-travel stop), measured with the fast-flight scenario of this study.
4. Not now: N3, and two-pass loading.

## 5. Work orders

Roles map to `implementer`, `claude-lead` and `claude-deep` (Claude Code) or `sol_*` (Codex).

**Rules for every order:**

- **Measurement and machine.** Measure on slot 3 through the real main-game path (memory "perf on the real world"): VSync on, display awake, optimized C#. Use the PL-0 probe, one reused isolated profile, and a robocopy of the slot before every run.
- **Look.** No look change in the arrival view. Compare the entry or hand-off frame with the frame 4 s later with `tools/media/ffmpeg_tools.ps1 compare`. Hold the wind, clouds and TAA (`TVW.freeze_noise`); without that, grass and trees fail SSIM by themselves.
- **Fingerprint.** `world_generation_fingerprint_test` stays unchanged. No cache version, cache name or menu row changes.
- **Menu path.** `menu_warm_stall_probe` must not get worse.

### Order and parallelism

| Order | Role | Size | Depends on | Can run alongside |
|---|---|---|---|---|
| PL-0 Probe tool | implementer | S | none | anything |
| PL-1 Stream at the saved pose | Lead design (S), then implementer (S) | S | PL-0 | PL-2 design |
| PL-2 Visible-first global terrain (= L2-3d-1 design + L2-3d-3) | Lead design (M), then implementer (M) | L | PL-0, PL-1 | none of the L2-3 orders touching RingTerrainGlobalLod.cs |
| PL-3 Arrival refine (optional, Q1) | implementer, Lead review | M | PL-0 | PL-1 |
| PL-4 Load-balanced partitioning (conditional) | implementer | S | PL-1 and PL-2 measured | PL-3 |

### PL-0. Probe tool

ROLE: implementer
SIZE: S
DEPENDS ON: none

OBJECTIVE
The study probe becomes a repo tool. It gives per-level streaming timelines, stills and a fast-flight arrival for the Continue path and for the loading screen on a copy of a real slot.

FILES/SYMBOLS
- New `tools/progressive_loading_probe.gd` and `tools/progressive_loading_probe.ps1`, from `.godot/audit_baseline/common/terrain/progressive_loading/probe/`.
- The analysis scripts as `tools/progressive_loading_report.py`, or folded into the ps1.

REQUIRED CHANGES
1. Profile and launch.
   - Profile under `.godot/progressive_loading/appdata_<label>`, reused, robocopy of the slot each run.
   - The optimized build through `Invoke-OptimizedBuild`.
   - The assembly swap and restore inside the GPU queue ticket, as in `perf_bench.ps1`. Refuse when `Test-UserPlaying`.
2. Output.
   - `events.json` with the same schema.
   - A `summary.md` holding the tables of sections 2.1 to 2.3 of this study: marks, per-level timeline, far/near split, frame stats per phase.
3. Flags: `-Mode continue|load`, `-Stills none|normal|debug`, `-Fly`, `-FlyKm`, `-FlySpeed`, `-Runs`.
4. Quality.
   - Record the `display_active` state and the VSync mode in the summary.
   - Exit with an error when VSync on was requested and the display is off.
   - Give `run_probe.ps1` a real exit code; it returned robocopy's code in the study.
5. Manifest entry: kind `tool`, tier `manual`, tags `loading`, `perf-telemetry`.

DO-NOT-CHANGE CONSTRAINTS
No production code.

VALIDATION STEPS
1. `tools/testing/validate_manifest.ps1`.
2. One run per mode. The summary reproduces this study's marks within 1 s.

STOP RULE
2 periods or 3 attempts.

ESCALATION CONDITIONS
The assembly swap collides with another agent's build lock or test run.

### PL-1. Stream proxy terrain at the saved pose

ROLE: Lead design (S), then implementer (S)
SIZE: S
DEPENDS ON: PL-0

OBJECTIVE
On the loading screen, no proxy level builds a sector before the explorer holds the saved pose. Far installs before the reveal are 0.

CONTEXT / ROOT CAUSE
- Section 2.3: 129 of 301 installs before the reveal are built around the default spawn. Proxy streaming starts at attach (9.2 s); `RingWorldSaveManager.ApplyDocument` (RingWorldSaveManager.cs:1026) restores the pose at about 11.5 s.
- The warm path loses 18 installs (2 m and 4 m levels) the same way.

FILES/SYMBOLS (to be confirmed by the design)
- RingWorldSaveManager.cs (`ApplyDocument`, the staged load).
- RingWorldLoadingBridge.cs (staging, `PlaceAtCruise`).
- RingTerrainLodCoordinator.cs (`_Process`, `StartPendingWorkers`).
- RingTerrainProxySector.cs (`QueueStreamingUpdate` :1572, `StartStreamingUpdate` :1586).

REQUIRED CHANGES
1. **Lead.** Choose one of the following and write a short design note under this order:
   - (a) the coordinator holds build starts until the save manager reports the pose applied (or "no saved state"), with a timeout;
   - (b) the bridge stages the saved pose on the explorer before the world children activate.

   Cover the new-world path (no saved state), the warm path (cruise placement) and a failed or old save.
2. **Implementer:** as designed. Add a load mark `proxy_streaming_released`.

DO-NOT-CHANGE CONSTRAINTS
- The gate (`StreamingSettledForReveal`), its dwell and its timeout.
- The boost values.
- The set of installed sectors at the gate: the same window around the same pose.
- New worlds load no slower than before (L0 benchmark `new`, `-Runs 1`, within noise).

VALIDATION STEPS
1. `.\tools\run_tests.ps1 -Tests world_loading_startup_contract,world_loading_lifecycle_test,menu_warm_world_lifecycle_test,lod_build_start_scheduling_test,proxy_seam_geometry_contract_test -Label pl1` (`-DryRun` first).
2. `.\tools\run_tests.ps1 -Tests world_loading_smoke_test,menu_continue_transition_test,terrain_altitude_lod_flight_test,proxy_level_ownership_gpu_test -Label pl1`, then `menu_warm_stall_probe`.
3. PL-0 probe, loading screen, 2 runs. Pass:
   - 0 far installs before the reveal;
   - median first playable frame at least 0.5 s earlier than 20.8 to 21.2 s;
   - `global_terrain_ready` and `surface_ready` not later;
   - entry frame against +4 s shows noise only.
4. PL-0 probe, Continue, 1 run: hand-off not later.

STOP RULE
Design: 1 period. Implementer: 3 periods or 5 attempts.

ESCALATION CONDITIONS
- The pose is not known before attach on some path (new world, cave void restore, underground checkpoint).
- The gate's set of installed sectors changes.

### PL-2. Visible-first global terrain (L2-3d-1 design and L2-3d-3)

ROLE: Lead design (M), then implementer (M)
SIZE: L
DEPENDS ON: PL-0, PL-1

OBJECTIVE
The global terrain builds nearest-first. The warm world and the loading-screen gate wait only for the segments the arrival view needs. The rest finishes after entry at gameplay budgets.

CONTEXT / ROOT CAUSE
- Section 2.1: the global terrain is the last owner of the warm load (6.0 s after queue) and holds the loading gate until 17.7 s. Segments are queued in index order (RingTerrainGlobalLod.cs:806). `StartupComplete` (:25) needs all 128 segments and the morph snapshot.
- The plan's L2-3d-1 items 2, 3 and 7 are the design. This order is L2-3d-3, moved up and narrowed to the global terrain. The proxy part of L2-3d-2 is not needed: PL-1 and the existing gate keep the proxies off the critical path. Re-check after PL-1.

REQUIRED CHANGES
1. **Lead design, under this order:**
   - which segments the first frame needs (the arrival view and the hull roof);
   - whether the terminal proxy level can take its morph target per segment (F2), so the horizon does not rebuild after the reveal;
   - which consumers (macro shadows, cloud and planet shadow snapshots, coverage height source) must wait for all segments, and which can take a partial set.
2. **Implementer:**
   - segments queued nearest-first around the arrival pose;
   - a readiness split: segments in view, then all segments;
   - the warm world and the gate use the in-view readiness;
   - the rest publishes at gameplay budgets after entry.

DO-NOT-CHANGE CONSTRAINTS
- The segment output hash (`global_terrain_ready` `outputHash`) once all segments are built.
- The arrival view equals the frame 20 s later (clocks frozen).
- Plain hull is allowed out of view only (user, 2026-11-12).

VALIDATION STEPS
1. `.\tools\run_tests.ps1 -Tests global_terrain_preparation_contract_test,world_loading_startup_contract,menu_warm_world_lifecycle_test,world_generation_fingerprint_test -Label pl2`.
2. `.\tools\run_tests.ps1 -Tests world_loading_smoke_test,menu_continue_transition_test,terrain_altitude_lod_flight_test,shadow_compose_identity_gpu_test,proxy_level_ownership_gpu_test -Label pl2`, then `menu_warm_stall_probe`.
3. PL-0 probe, 2 runs per mode. Pass:
   - warm world ready at 14.0 s or earlier, and hand-off at least 1.5 s earlier than 32.9 to 33.1 s;
   - loading-screen first playable frame at 17.0 s or earlier;
   - post-entry frame p95 within 1 ms of the baseline, and largest frame at most 45 ms;
   - entry frame against +20 s shows noise only.
4. A 360-degree turn-around clip from the entry frame (mp4 and sheet), sent to the user.

STOP RULE
Design: 2 periods. Implementer: 5 periods or 7 attempts.

ESCALATION CONDITIONS
- The first frame needs every segment (for example, the terminal morph target cannot be per segment without a horizon change).
- A difference inside the arrival view after two fixes.

### PL-3. Arrival refine after fast travel (optional; needs the user's answer to Q1)

ROLE: implementer, Lead review
SIZE: M
DEPENDS ON: PL-0

OBJECTIVE
After a fast-travel stop, the near proxy levels are installed within 2 s instead of 3.5 s, with no frame cost the player can feel.

CONTEXT / ROOT CAUSE
Section 2.4 and 3.3. On Low, one worker builds the 2 to 32 m levels one after another, finest first (`BuildStartComesBefore`, RingTerrainLodCoordinator.cs:1324).

FILES/SYMBOLS
- RingTerrainLodCoordinator.cs: `StartPendingWorkers` :1338, `BuildStartComesBefore`, `EffectiveConcurrentWorkerBuilds` :77, and the fast-travel state.

REQUIRED CHANGES
1. **Arrival window.** For N seconds after the player's speed drops below the fast-travel threshold (existing `FastTravel*` fields):
   - order build starts coarse-to-fine among the levels that are active and requested;
   - raise the worker limit to the value the user picks in Q1.
2. No menu row. It follows the existing fast-travel state.

DO-NOT-CHANGE CONSTRAINTS
- Outside the arrival window, ordering and worker counts are unchanged.
- Gameplay frame p95 in the PL-0 fast-flight scenario at most 1 ms worse, and largest frame at most 45 ms.

VALIDATION STEPS
1. `.\tools\run_tests.ps1 -Tests lod_build_start_scheduling_test,terrain_altitude_lod_flight_test,flight_landing_walk_test,proxy_level_ownership_gpu_test -Label pl3`. Extend `lod_build_start_scheduling_test` with the arrival order (it must fail on the old code).
2. PL-0 probe, Continue + fly, 2 runs. Pass:
   - brake to all near levels installed in 2.0 s or less (today 3.5 s);
   - no holes in the level-colour stills;
   - send the user the arrival sheet before and after.

STOP RULE
3 periods or 5 attempts.

ESCALATION CONDITIONS
- Frame p95 or largest frame over the limits at the worker count chosen.
- Terrain3D window commit time gets later.

### PL-4. Load-balanced sector order inside a level (conditional)

ROLE: implementer
SIZE: S
DEPENDS ON: PL-1 and PL-2 measured

OBJECTIVE
With several slots, a level installs its sectors close to nearest-first. Only if PL-0 shows the proxies still on the critical path after PL-1 and PL-2.

REQUIRED CHANGES
1. In `BuildRawBatch`, `Parallel.ForEach` (RingTerrainProxySector.cs:4079) uses a load-balanced, chunk-size-1 partitioner over the sorted list.

DO-NOT-CHANGE CONSTRAINTS
Sector bytes (hash) unchanged.

VALIDATION STEPS
1. `proxy_seam_geometry_contract_test` and `lod_build_start_scheduling_test`.
2. PL-0: the install-time against distance rank correlation on the loading screen rises above +0.8.

STOP RULE
1 period or 2 attempts.

## 6. Open questions for the user

1. **After a fast flight, should terrain sharpen faster at the cost of some CPU in the first seconds after you stop (PL-3)?**
   - Today your Low preset builds proxy terrain on one CPU thread, so the ground near you takes about 3.5 s to sharpen after braking. That is usually just before touchdown.
   - Option (a): 2 workers for about 5 s after a stop.
   - Option (b): 4 workers.
   - Option (c): leave it.
   - Recommended: (a), measured first.
2. **May the loading screen and the Continue menu open before the far side of the ring is built (PL-2)?**
   - Only the part of the ring you can see from your arrival point would be ready. This is about 10 of 128 pieces, and the rest would fill in within seconds.
   - This saves about 2 s on Continue and, with PL-1, about 4.5 to 6 s on Load World.
   - You approved this kind of change on 2026-11-12 ("out of view only"). This confirms it applies to the menu and Continue path as well.
   - Recommended: yes.
3. **Is your save spot typical?** All numbers come from slot 3 in a forest, where trees hide most distant terrain. Should PL-2 and PL-3 also be checked at an open spot (a plain or a mountain view) before they count as done? Recommended: yes, one extra pose.
