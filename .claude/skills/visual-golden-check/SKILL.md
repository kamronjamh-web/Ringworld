---
name: visual-golden-check
description: Check rendering changes against screenshot goldens or baselines in a machine-aware way (RTX A1000 laptop vs RTX 4070 desktop), produce diff heatmaps and side-by-side images to inspect visually, and decide regression vs expected change. Use for any change that can alter pixels - shaders, lighting, haze, shadows, terrain surface, clouds, POI materials.
---

# Visual golden check

## 0. Which machine am I on? (decides everything)

```powershell
(Get-CimInstance Win32_VideoController).Name   # or the 'Using Device' line in the run log
```

- **RTX A1000 laptop:** authoritative. The committed PNG goldens were captured here.
  - It thermally throttles. For timings see the `perf-capture` skill. Pixels are unaffected.
- **RTX 4070 desktop** (`desktop_mid_high`): non-authoritative.
  - The committed goldens **fail here even with no code change** (max channel diff up to 49).
  - Never "fix" a 4070 mismatch by recapturing the committed goldens.
- `shadow_harness_common.gd` `machine_info()` tags output folders as `a1000`, or a sanitised adapter name such as `rtx4070`.

## 1. Pick the harness

| Harness | Kind | Key flags |
|---|---|---|
| `tools/terrain_surface_include_regression.gd` | byte-exact PNG golden, 6 fixed poses, 480x270 | `-- --capture`, `--golden-dir=`, `--out=`, `--max-diff=N` (default 0) |
| `tools/shadow_compose_identity_gpu_test.gd` | 44 synthetic cases, machine-specific | `-- --capture --out=` / `-- --compare --golden=<dir> --max-diff=N`, `--debug-modes` |
| `tools/shadow_quality_capture.gd` | real-world named poses: full / sun_off / all_off + visibility map | `--poses=a,b`, `--quick`, `--res=`, `--out=` |
| `tools/atmosphere_visual_test.gd` | matched captures (noon/dusk/night/high_alt...), no golden | `--labels=`, `--model=physical`, `--copy-to=` |
| `tools/compositor_temporal_stability_test.gd` | temporal noise ratios (FSR2/TAA on vs off), relative | none |
| `tools/poi_haze_gpu_test.gd` | POI haze identity, tolerance 2/255 | none |
| JSON goldens in `tools/fixtures/*_golden.json` | CPU data, not images | `--capture` refuses to overwrite |

- Named real-world poses live in `shadow_harness_common.gd` `POSES`: `ground_horizon`, `forest_noon`, `emitter_rim`, `dawn_5deg`, `alt_1km`, `alt_12km`, `night_planet_lit`, `eclipse_hub`, `wl_*`, and others.
- All image harnesses are **windowed GPU**. Run them via the `godot-test-run` skill (GPU coordination, prune).

## 2. Baselines

- **Committed:** `tools/fixtures/terrain_surface_include_regression/*.png`.
  - These are stale since the haze P3b lighting fix (`docs/shadow-revamp-plan.md`).
  - Compare locally against `.godot/s5_before/t3d/` instead: `-- --golden-dir=res://.godot/s5_before/t3d`.
- **Local, never committed, protected by prune:** `.godot/shadow_baseline/<tag>/{quality,attribution,contact,height_shadow}/<stamp>/`, `.godot/s5_before/`, `.godot/shadow_compose_identity_head_clouds/`, `.godot/shadow_compose_identity_storm_revamp_4070/` (4070, storm revamp, 2026-10-28). Never delete these.
- **Zero-diff check on the 4070, or any time the right baseline does not exist yet:**
  1. Render goldens from the **unchanged** code first, with `--capture --golden-dir=<scratch>` or `--out=<scratch>/before`. Get that code from a separate worktree or before you edit. Never use `git stash`.
  2. Apply the change and compare against `<scratch>`.

## 3. Inspect the difference

The repo harnesses print counts only, with no diff images. Generate the images with:

```powershell
.\.claude\skills\visual-golden-check\imgdiff.ps1 -A <golden.png|dir> -B <actual.png|dir> -Out <scratch>\diff [-MaxDiff 0] [-Gain 8]
```

- Output per pair: `differing_pixels`, percentage, `max_channel_diff`, p99. The metric is the same as the GDScript harnesses.
- It writes `<name>_diff.png` (diff in red over a dimmed image) and `<name>_side_by_side.png` (golden | actual | diff).
- **Open the side-by-side with the Read tool and actually look at it** before you judge.

## 4. Judge

- **Exact match or diff ≤ 1-2** that is spread uniformly: rounding noise. Godot 4.8 RGBA8 rounds half away from zero; it does not truncate.
- **Diff confined to the area the change targets** (e.g. only haze at distance): an expected change. Describe it and show the side-by-side.
- **Diff outside the targeted area** (wrong objects, the sky changed by a terrain edit, POIs suddenly unhazed): a regression. Investigate with the `shader-debug` skill.
- A **noisy diff everywhere** on a temporal or cloud pose: check that the clocks were held (`hold_world_clocks`) before blaming the code.
- **Updating committed goldens:** only on the A1000, only for an intended change, and only with the user's OK. Say which poses changed and why. Keep the line-ending-only `.png.import` noise out of commits.

## 5. Report

Give:
- the machine
- the harness and baseline used
- the numbers for each pose
- the verdict for each pose
- the path to the side-by-side images

If you could not use the authoritative machine, say so.
