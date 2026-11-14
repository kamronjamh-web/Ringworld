# Sound effects: project and work orders

Started 2026-11-14 at the user's request: sound effects are their own project, separate from the terrain and vegetation revamp. The open water-sound orders of T3 moved here with their numbers and text unchanged (T3R-10e to T3R-10g), with commit points CW1 and CW2 and milestone D3w.
Status: in progress.
Depends on: nothing outside this file. T3R-10a to T3R-10d (T3.md) are done and are this project's starting point.
Freeze: none. Sound is not world data (T3.md, Phase rules): no order here changes a pixel, a mesh, physics or a generation fingerprint.

**Goal.** Every sound in the game should sound like a real recording. The user's complaint of 2026-11-14 started this: the synthesised water loops "resemble too much like 16 bit sounds".

**Scope.** All game sound: water (`RingWaterAmbience`, `WaterSoundField`, `WaterSoundSourceFinder`), weather and rain (`RingWeatherAmbience`, resources/audio/weather), thunder, natural storms and the megastorm, and the containment field. Not the PDA voice (prototypes/pda-voice).

## Rules

- **The user judges by ear.** Numbers prove a loop is steady and seamless; only the user accepts a take. Send auditions as MP3 with SendUserFile, one file per sound with takes A to D, and quote the take IDs.
- **Realism by default.** Ask the user before any floor, minimum, widening or boost.
- **No GPU while the user plays.** The audio studio uses the GPU (about 3.4 GB VRAM). Do not start it, a build or Godot while `tools/play.ps1` has the game running. Stop the studio (`prototypes/audio-studio/stop.ps1`) when a batch is done.
- **Every file has a licence record.** Water: resources/audio/water/LICENSE.md (the AI block between `<!-- ai-water-loops:begin/end -->` is written by `water_sfx_ai.py build`). Weather: resources/audio/weather/LICENSE.md. No non-commercial (NC) licence and no BBC RemArc material. A CC-BY file needs its author and licence in the game's credits.
- **Loops pass the checks.** `python -I tools/media/water_sfx_gen.py --out-dir resources/audio/water --verify --ai` for water loops.
- Commits are local; never push.

## Tools

| Tool | What it does |
|---|---|
| prototypes/audio-studio | Local text-to-audio studio: Stable Audio Open 1.0 (diffusers). Start with `run.ps1`, stop with `stop.ps1`; API at http://127.0.0.1:7861 (`POST /api/jobs`, `GET /api/jobs/<id>`). Takes have permanent IDs (`GEN-0042-03`); outputs in `prototypes/audio-studio/output/<job>/`. At most 8 jobs wait in the queue. |
| tools/media/water_sfx_ai.py | `submit` (prompts and seeds per loop, `--seed-offset` for new seeds), `audition` (one MP3 per loop, takes A to D, processed as they would be built), `build --pick name=GEN-xxxx-nn` (trim 1 s each end, mono or stereo, the texture fix, an equal-power crossfade at the seam (3 s, `--xfade`), -20 dBFS RMS with peaks under -3 dBFS, Ogg Vorbis q5, licence record). |
| tools/media/water_sfx_gen.py | The synthesised loops of T3R-10c, and `--verify [--ai]`: steadiness, seam and length checks per loop. |
| tools/media/weather_sfx_gen.py, megastorm_audio_render.py | The synthesised weather and storm sounds (the same 16-bit-like character as the old water loops). |

**What Stable Audio Open does wrong, and the fixes in the build:**
- A chirp or zigzag texture in steady noise at 1 to 3 kHz ("two different notes playing, oscillating", the user, 2026-11-14). Fix: `renoise`, which keeps the smoothed spectrum and replaces the fine structure with seeded noise inside 900 to 3,300 Hz. It runs on surf_break, surf_far, river_rush, river_roar and waterfall. The user confirmed it: "The pattern is gone and the sound is almost the same." It is not used on loops whose 1 to 3 kHz band holds real bubble pitches (stream_babble, water_lap, open_water, underwater).
- A faint 21.5 Hz block flutter (44,100 / 2,048). Measured, not audible after the texture fix; no fix adopted.
- Surf takes can be metronomic (one wave every few seconds, very even). Not raised by the user; left alone unless they hear it.
- River rush takes are duller than the synthesised loop (less energy at 4 to 8 kHz). Left alone unless the user hears it.

**Real recordings (free licences).** Freesound.org (per-file CC0, CC-BY or CC-BY-NC; filter out NC; needs an account), the Sonniss GDC Game Audio Bundle (royalty-free, commercial use, no credit; files may not be redistributed on their own), OpenGameArt.org (mostly CC0 or CC-BY), Pixabay sound effects (Pixabay licence, no credit) and Zapsplat (free tier needs credit). Not BBC Sound Effects (RemArc: personal, educational and research use only). Downloads need the user's account or a manual download; an agent can make shortlists with links and licences.

## State on 2026-11-14

| Loop | In the game | Source |
|---|---|---|
| water_lap_loop | yes | Stable Audio Open, GEN-0036-03 (user's pick C) |
| open_water_loop | yes | Stable Audio Open, GEN-0039-03 (C), stereo |
| stream_babble_loop | yes | Stable Audio Open, GEN-0040-02 (B) |
| river_roar_loop | yes | Stable Audio Open, GEN-0042-03 (C), texture fix |
| waterfall_loop | yes | Stable Audio Open, GEN-0043-03 (C), texture fix |
| underwater_loop | yes | Stable Audio Open, GEN-0044-04 (D), stereo, 5 s crossfade |
| surf_break_loop | yes, **still synthesised** | SFX-1 |
| surf_far_loop | yes, **still synthesised** | SFX-1 |
| river_rush_loop | yes, **still synthesised** | SFX-1 |
| shore_wave_cc0.ogg | no longer played | removed by T3R-10g step 7 |

## Order and parallelism

| WO | Role | Size | Depends on | Files | Can run alongside |
|---|---|---|---|---|---|
| SFX-1 The last three water loops | primary (no build) | S | the user's picks or recordings | resources/audio/water/{surf_break,surf_far,river_rush}_loop.ogg, LICENSE.md | any |
| T3R-10e Volume rows | implementer | S | none (T3R-10d done) | GameplayWaterDefaults.cs, GameplayWaterSettings.cs, RingLiveTuningDrawer.Water.cs, the settings smoke | T3R-10f |
| T3R-10f Ground between the listener and the water | implementer | S | none (T3R-10d done) | WaterSoundField.cs, WaterSoundSources.cs, RingWaterAmbience.cs (one call) | T3R-10e |
| T3R-10g Review: numbers, recordings, the user's ear (**D3w**) | claude-lead (Fable while it is the temporary lead) | M | T3R-10e, T3R-10f, SFX-1 | none (one asset removal after the sign-off) | - |
| SFX-2 Weather, rain and thunder in the same way | lead plans, primary runs the studio | M | **the user's go**; SFX-1 done | resources/audio/weather/, a new tools/media/weather_sfx_ai.py | - |

**Commit points.** **CW1** after T3R-10e (the primary runs `-Suite gate` and commits). **CW2** after T3R-10g.

The Spec these orders cite (W1 to W9), the findings (H39 to H51), the budgets and the validation facts V10 to V13 stay in [T3.md](../terrain-vegetation/T3.md); read its "Phase rules" and "Spec W" before T3R-10e to T3R-10g. The tests use the label `rivers` and the `landscape_gpu` profile, as there.

## Work orders

### SFX-1. The last three water loops

ROLE: primary (Python and ffmpeg; no build, no Godot)
SIZE: S
DEPENDS ON: the user's choice.

OBJECTIVE
surf_break, surf_far and river_rush are replaced by takes the user picked, either Stable Audio Open takes or real recordings.

CONTEXT
On 2026-11-14 the user rejected the first takes for these three (the texture of the model, since fixed) and asked for new seeds. Two sets wait in the studio, each takes A to D, `-01` to `-04`:
- set 1 (the first takes): surf_break GEN-0037, surf_far GEN-0038, river_rush GEN-0041;
- set 2 (new seeds): surf_break GEN-0045, surf_far GEN-0046, river_rush GEN-0047.
- set 3 (seed offset 200, generated but not yet auditioned): surf_break GEN-0048, surf_far GEN-0049, river_rush GEN-0050.
The manifests are kept in prototypes/audio-studio/output/sfx1/set1 to set3 (git-ignored, not pruned). An audition file per loop is rebuilt with the texture fix applied: `python -I tools/media/water_sfx_ai.py audition --scratch prototypes/audio-studio/output/sfx1/set2 --only surf_break surf_far river_rush` (it writes the MP3s to an `audition` folder beside the manifest; delete them after use). The user may instead supply real recordings (see "Real recordings").

REQUIRED CHANGES
1. AI picks: `python -I tools/media/water_sfx_ai.py build --pick surf_break=GEN-00xx-0n surf_far=... river_rush=...`.
2. Real recordings: the same build steps (trim, crossfade, level, Ogg) without the texture fix, and a licence record with the source URL, the author and the licence.
3. Update the synthesised section of resources/audio/water/LICENSE.md, which lists these three.

VALIDATION STEPS
1. `python -I tools/media/water_sfx_gen.py --out-dir resources/audio/water --verify --ai`. The range limit for surf_far (18 dB) was set from the synthesised loop; widen it only to the picked take's measured value plus 2 dB, and say so.
2. Send the user an in-game re-test list: sea shore, rising over the sea, a lake, a stream, a fast river, a calm river, under water, fast flight.

### T3R-10e. Volume rows

ROLE: implementer
SIZE: S
DEPENDS ON: T3R-10d. Can run alongside T3R-10f.

OBJECTIVE
The Water tab of the grave-key menu has a "Water sound" section with five volumes, saved with the other water settings.

CONTEXT / ROOT CAUSE
AGENTS.md "Feature conventions": genuine tunables only. A volume per kind of water is one; the constants of the law are not, and a row for them would let a setting undo the realism rule. H13, H51.

FILES/SYMBOLS
- scripts/ui/GameplayWaterDefaults.cs, GameplayWaterSettings.cs, RingLiveTuningDrawer.Water.cs. The owner applies to two nodes today (graphics and the FFT controller); the sound rows need the `WaterAmbience` node as a third.
- scripts/Tools/WaterSettingsSmoke.cs (`water_settings_smoke_test`).

REQUIRED CHANGES
1. Five rows, in dB, range -30 to +10, step 0.5, following the live-tuning-setting skill:
   - `WaterSoundVolumeDb` "Water sound volume (dB)", every water sound, default 0;
   - `WaterSeaSoundVolumeDb` "Sea and surf (dB)", default 0;
   - `WaterLakeSoundVolumeDb` "Lakes (dB)", default 0;
   - `WaterRiverSoundVolumeDb` "Rivers and falls (dB)", default 0;
   - `WaterUnderwaterSoundVolumeDb` "Under water (dB)", default -12.
2. No row for reach, the law's constants, the masker or the cutoffs, and no toggle.
3. Extend the settings smoke: set, save, reload, sanitise (NaN and out of range refused).

DO-NOT-CHANGE CONSTRAINTS
- Generation 1 worlds must stay identical: `world_generation_fingerprint_test` must pass unchanged.
- The existing water rows, their keys and their saved files.

VALIDATION STEPS
1. `.\tools\run_tests.ps1 -Tests water_settings_smoke_test,water_ambience_capture_test -Label rivers`.
2. One screenshot of the section for the user. The primary runs `-Suite gate` and commits (CW1).

ESCALATION CONDITIONS
- The owner cannot take a third node without changing how the existing rows are saved.

STOP RULE
2 periods or 3 attempts.

### T3R-10f. Ground between the listener and the water

ROLE: implementer
SIZE: S
DEPENDS ON: T3R-10d. Can run alongside T3R-10e.

OBJECTIVE
A river behind a ridge, and any water heard from inside a tunnel under it, is quieter and duller than the same water in the open, by Spec W8.

CONTEXT / ROOT CAUSE
H40: under the bed of a lake or the sea the old rule played at full level. T3R-10d's distance already ends that at any depth beyond a source's reach; this order adds the ground itself. The rain system's shelter classes only run while it rains (scripts/RingWeatherAmbience.cs:732-737), so they cannot serve water.

FILES/SYMBOLS
- scripts/Water/WaterSoundField.cs (`Shield(excess)`), scripts/Water/WaterSoundSources.cs (the three ground samples per source, in the finder's budget), scripts/RingWaterAmbience.cs (one call).
- tools/WaterSoundFieldTest.cs, tools/WaterSoundSourcesTest.cs, tools/WaterAmbienceCaptureTest.cs (rows added).

REQUIRED CHANGES
1. W8: the ground's height above the straight line at a quarter, a half and three quarters of the way; the shield in dB and on the cutoff; one audible source per slice, in turn; the full shield for a listener more than 2 m under the ground of their own column.
2. The diagnostics carry the shield per voice.

DO-NOT-CHANGE CONSTRAINTS
- Generation 1 worlds must stay identical: `world_generation_fingerprint_test` must pass unchanged.
- In the open (no ground above the line) every level of T3R-10d is unchanged to 0.01 dB.
- The finder's budget (Spec "Budgets").

VALIDATION STEPS
1. Field test: the shield is 0 for no excess, monotone, 20 dB at 15 m and beyond. Sources test: a fixed ridge 20 m above the line between a listener and a river gives the full shield, and the same listener on the ridge gives none. Capture test: a shielded torrent at 100 m is 18 to 22 dB under the open one and has less energy above 2 kHz.
2. `.\tools\run_tests.ps1 -Tests water_sound_field_test,water_sound_sources_test,water_ambience_capture_test -Label rivers`.

ESCALATION CONDITIONS
- The ground samples push the finder over its budget.

STOP RULE
3 periods or 4 attempts.

### T3R-10g. Review: numbers, recordings and the user's ear

ROLE: claude-lead
SIZE: M
DEPENDS ON: T3R-10d, T3R-10e and T3R-10f; the audition answered.

OBJECTIVE
The user can judge the water sound: a table of levels from the play world, four short recordings with sound, the measured loudness of those recordings against the law, and a list of every constant still marked "(tune at review)" with a proposal. **Milestone D3w. Commit point CW2.**

CONTEXT / ROOT CAUSE
Phase rules: numbers prove, the ear judges. The constants of W2 and W3 are estimates of real hearing distances, not measurements; this is where they are tuned.

FILES/SYMBOLS
- The diagnostics of `RingWaterAmbience`; Movie Maker; `tools/media/ffmpeg_tools.ps1`; captures under `.godot/captures/landscape/t3r/sound/`.
- After the sign-off: resources/audio/water/shore_wave_cc0.ogg, its `.import` file and its part of LICENSE.md.

REQUIRED CHANGES
1. **First** V13. If the tool's `mp4` drops the sound, join the track by hand with ffmpeg and tell the primary, for the tool's owner.
2. The table, from the play world: at a sea shore, in open sea, at a lake shore, a brook, a mountain river, the widest fast river in reach, a chute and a fall if one is within reach, and a wide calm river: the level and the gain at the water's edge, at 30, 100, 300 and 1,000 m to the side, and at the same distances above.
3. Four recordings with sound, each 60 s at most: (a) from the sea shore 600 m inland; (b) along one river from a brook to its fastest stretch (teleports allowed); (c) straight up from a lake shore to 500 m and from the fastest river to 1,500 m; (d) rain beginning at a lake. An `mp4` of each; the frames are deleted.
4. From the sound tracks of (a) and (c), the loudness over time (`ffmpeg ebur128`, the momentary value each second) beside the level the law gives for the path. They agree within 3 dB or the difference is explained.
5. The list of open constants (P0, P1, A, the riffle share, the open-water factor, the masker terms, the blends, the shield) with a proposal each, and the user's answers to the questions.
6. Costs on the 4070 from the diagnostics: the finder's average and worst frame at the four places and in flight. The exact command for the A1000.
7. After the user's sign-off, and only if question 1 was answered "every world": remove `shore_wave_cc0.ogg`, its import file and its notice.

DO-NOT-CHANGE CONSTRAINTS
- Generation 1 worlds must stay identical: `world_generation_fingerprint_test` must pass unchanged.
- No tuning during the review. A changed constant goes back to T3R-10a with the test's table.

VALIDATION STEPS
- Every number cites the diagnostics or a results file.
- `.\tools\run_tests.ps1 -Suite gate -Label rivers`; clean up; `tools/prune_test_output.ps1 -Apply`.

ESCALATION CONDITIONS
- A budget is missed.
- The user finds a water sound heard from too far or too high. Collect the place and the distance; the constant goes back to T3R-10a.

STOP RULE
4 periods. Each recording is made at most twice.

### SFX-2. Weather, rain and thunder in the same way

ROLE: lead plans; the primary runs the studio and the auditions
SIZE: M
DEPENDS ON: the user's go; SFX-1 done.

OBJECTIVE
The weather sounds (rain on surfaces, hail, wind in foliage, gutters, drips, thunder) are replaced by Stable Audio Open takes or real recordings the user picked, the way the water loops were.

REQUIRED CHANGES
1. An inventory of resources/audio/weather (49 loops and 54 one-shots) with the system that plays each and its required length, channels and loop or one-shot use.
2. A `weather_sfx_ai.py` built on `water_sfx_ai.py` (shared code moved into one module, not copied), with one-shots trimmed to their onset rather than looped.
3. Auditions to the user in groups; builds of the picks; the weather LICENSE.md updated.

DO-NOT-CHANGE CONSTRAINTS
- `thunder_audio_capture_test` and the outside and weather ambience tests stay green.
- File names stay the same, so no scene changes.

## Questions for the user

1. SFX-1: pick from the two Stable Audio Open sets, or use real recordings (for example from Freesound or the Sonniss bundle) for surf_break, surf_far and river_rush?
2. SFX-2: redo the weather and thunder sounds the same way, and when?
