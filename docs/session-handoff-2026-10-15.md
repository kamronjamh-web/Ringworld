# Session handoff (2026-10-15, evening)

This picks up from [emitter-milestone1-handoff.md](emitter-milestone1-handoff.md), [emitter-milestone2-plan.md](emitter-milestone2-plan.md) and [voxel-cave-direction-handoff.md](voxel-cave-direction-handoff.md). Base commit is `48dd040`. **Everything below is uncommitted in the working tree.** The user commits and pushes.

## Machine setup

- Install the .NET 10 SDK system-wide (`winget install --id Microsoft.DotNet.SDK.10 --exact`).
- Pinned engine: `artifacts/engine-upgrade/Godot_v4.8-dev7_mono_win64/`.
- Build with `dotnet build RingworldTechDemo.csproj -p:NuGetAudit=false -v:minimal`. The expected result is 0 warnings and 0 errors.
- Headless tests: `<engine>_console.exe --headless --path . --script res://tools/<name>.gd`. GPU tests and captures run windowed (drop `--headless`).
- Godot runs can delete `addons/terrain_3d/bin/~libterrain.ringworld.windows.debug.x86_64.dll`. Restore it with `git show HEAD:<path> > <path>`.
- Agents must never run `git stash`, `reset`, `checkout` or `apply -R`, and must never kill processes they did not start.
- The test laptop (RTX A1000) throttles to P5/20 W after about 45–60 minutes of load. Check `nvidia-smi` P-state during GPU measurements and discard throttled windows.
- Never measure GPU while another Godot run is active.

## Workstreams and state

### 1. Cave direction B: gen-17 on the smooth voxel backend (spike done, awaiting the user)

The user chose B, gated by a spike, and the spike passes all gates.

- **Parity:** exact.
- **Density cost:** cached density is 0.38–0.51x gen-16.
- **Quiescence:** at or better than gen-16.
- **Regressions:** none.

Details are in [voxel-cave-direction-handoff.md](voxel-cave-direction-handoff.md), under USER DECISIONS plus the spike notes.

Files:
- `scripts/world_size_launcher.gd`: dev flag `--smooth-runtime-generation-spike`.
- `SmoothTerrainContracts.cs` / `SmoothTerrainGrid.cs`: gen-17 surface offset fix.
- `RingTerrainSampler.RuntimeLatticeCache.cs`.
- `tools/SmoothRuntimeSpike*`, `tools/smooth_runtime_spike_*`.
- `tools/fixtures/smooth_runtime_gen17_golden.json`.

Comparison captures from Terrain3D vs the spike live on the measuring machine (scratchpad `spike_b1`), not in the repo. Regenerate them with `tools/smooth_runtime_spike_world.gd`.

**Open:** the user still has to judge the near-surface look. Mountain cut faces render smoother and more faceted on voxels. The user also has to approve B2: a new backend id, persistence, and surface-aware cave placement. Milestone 2 stays paused until then and will be re-planned for B.

### 2. Performance (report done)

[perf-ranking-2026-10-15.md](perf-ranking-2026-10-15.md) ranks the systems. Read its **Correction** section: there is no idle grass/tree churn.

| Rank | Cost (spawn, GPU) |
|---|---|
| 1. Clouds | about 26 ms |
| 2. Grass | 9.6 ms |
| 3. Sun shadows | 7–10 ms |

The CPU floor is about 21 ms, mostly RenderingServer frame setup (8.2 ms) and the far-ring viewport (4.8 ms).

The user chose to do only cloud lever 1 for now.

### 3. Cloud lever 1: sliced near cloud-shadow map (done, verified)

- **What it does:** the near cloud-shadow refresh is split into N row bands (default 4, grave-key "GPU near slices", 0/1 = legacy). Each band renders to a staging texture, which is copied to the front texture when complete. The result is byte-identical to the legacy path (`tools/sunshine_shadow_slice_equivalence_test.gd`).
- **Starvation fix:** in `RingSunshineCloudBridge.cs`, timed refreshes are held while `_gpuShadowReceiverRebuildPending`. Without this, `sunshine_shadow_toggle_smoke_test` fails at N=4.
- **Result:** the shadow spike drops from 11.5 ms to about 3 ms per frame.
- **Files:** `addons/SunshineClouds2/SunshineCloudShadowCompute.glsl`, `SunshineClouds.gd`, `scripts/RingSunshineCloudBridge.cs`, `scripts/ui/GameplayCloudDefaults.cs`, `scripts/ui/RingLiveTuningDrawer.cs`, plus the equivalence test.
- **Pre-existing failures** (fail identically at N=1): `sunshine_gpu_shadow_smoke_test` and `sunshine_menu_smoke_test`.

### 4. Grass/tree streaming hysteresis (done, verified)

Grass:
- Add-fast/drop-slow cap competition and tier hysteresis, with H = clamp(tile/2, 2, 8) m and a grave-key "Streaming hysteresis".
- Downgrades filter resident data, byte-identical to a fresh build.
- Per-reason build counters.

Trees:
- A keep band with cap = max(setting, desired + 2×perimeter).
- Translation-only rebase, with no replan or GPU refresh.
- Split telemetry.

Tests: `tools/grass_streaming_hysteresis_test.gd`, `tools/tree_streaming_contract_test.gd`.

Result: ±0.75 m oscillation went from 228 grass builds/min to 2, and trees from about 15 evictions plus 14 reloads per minute to 0.

Files:
- `RingStandingGrassLayer.cs` / `.Diagnostics.cs`
- `RingGrassTileBuilder.cs`
- `RingBiomeDecorationLayer.TreeStreaming.cs` / `.GpuRebase.cs` / `.TreeStreamingFixture.cs`
- `GameplayGrassDefaults.cs`
- `RingLiveTuningDrawer.Grass.cs`

Still failing, believed pre-existing: `grass_streaming_contract_smoke`, `grass_clump_validation_test` and `grass_live_tuning_smoke_test`. `grass_adaptive_grouping_test` has not been confirmed against HEAD.

### 5. New grass: cards + hero blades, variant D (in progress)

Design: [grass-cards-redesign.md](grass-cards-redesign.md). Plan: [grass-cards-integration-plan.md](grass-cards-integration-plan.md). Both carry all USER DECISIONS:

- realistic tufts, height by biome;
- variant D, which is C cards plus a short solid-blade hero layer;
- 400 m reach;
- colour matches the ground, keeping green_pull at 0.15 (exposed for tuning);
- first-person camera only;
- Terrain3D colour target;
- the old blade system stays the default behind a switch until the user accepts the in-game A/B.

The standalone spike (`scenes/grass_cards_spike/GrassCardsSpike.tscn`, F1–F6 for variants, 1–6 for bookmarks) proved the look and cost on the A1000. Grass totals about 3.0–3.6 ms GPU, against 9.6 ms for the current blades.

| Step | State | Key files / tests |
|---|---|---|
| S0 streaming hysteresis | done | see §4 |
| S1 shared terrain includes + follower fan-out | done, terrain 0-diff verified | `materials/ring_terrain_surface_albedo.gdshaderinc`, `ring_terrain_lighting.gdshaderinc`, `RingSurfaceFollowerRegistry.cs`, `RingTerrain3DSurface.Followers.cs`; `tools/terrain_surface_include_regression.gd` (windowed, `--resolution 480x270`), `tools/terrain_surface_follower_test.gd` |
| S2 asset factories | done | `RingGrassCardAtlasGenerator.cs`, `RingGrassCardMeshFactory.cs`, `RingGrassHeroTuftMeshFactory.cs`, `RingGrassCardAssets.cs`; `tools/grass_card_assets_test.gd` |
| S3 card placement | done | `RingGrassCardPlacement.cs`, `RingGrassCardCurve.cs`, `RingGrassHeroCellBuilder.cs`, `RingGrassTileBuilder.cs` (card mode); `tools/grass_card_placement_test.gd` (incl. blades golden) |
| S4 shaders | **paused mid-step**, see S4 status below | `materials/ring_grass_cards.gdshader`, `ring_grass_hero.gdshader`, `ring_grass_card_{color,common,curve}.gdshaderinc`, `RingGrassCardMaterialConfig.cs`, `tools/GrassCardShaderFixture.cs`, `tools/grass_card_color_match_gpu_test.gd`, `tools/shaders/` |
| S5 style integration (CPU path) | not started | needs S3 + S4 |
| S6 compute card mode ‖ S7 hero layer | not started | |
| S8 settings, S9 terrain coverage/sheen, S10 invariant suite, S11 in-game A/B + perf, then a separate default flip | not started | |

#### S4 status at pause

The S4 agent stopped cleanly on the pause request: nothing is half-written, all its files are new, and no existing file was modified. Verified at pause by the primary agent:

- build is 0 warnings / 0 errors;
- terrain regression `TERRAIN_SURFACE_INCLUDE_REGRESSION=PASS poses=6`, still 0-diff.

**Done:**

- **Card shader** (`ring_grass_cards.gdshader`): 3→1 collapse at 90–160 m, edge-on collapse, curve-driven grow-in and compensation, hero thinning, convergence at 60–90% of reach, bent normals + backlight, root AO, tip light, green pull 0.15, dry blades, per-blade jitter, fade over the last 15% of reach.
- **Hero shader** (`ring_grass_hero.gdshader`).
- **Shared includes:** `ring_grass_card_color.gdshaderinc` (2-biome per-vertex far path, all-biome per-fragment path within 70 m), `ring_grass_card_common.gdshaderinc` (S1 lighting include, wind/gust/modifier/interaction, frame uniforms), `ring_grass_card_curve.gdshaderinc` (GLSL twin of `RingGrassCardCurve`).
- **Material config:** `RingGrassCardMaterialConfig.cs` is the single source of uniform defaults. Its `SurfaceFollowerNames` is meant for `RegisterSurfaceFollower`.
- **Instance contract:**
  - Transform: X/Z in the tangent plane, Y = leaned up axis × height.
  - COLOR = biome weights.
  - CUSTOM = (kind·4 + variant + rank·0.999, tuftRandom, windPhase, windPhase).
  - Hero uses CUSTOM0 root offsets.
  - Instance uniforms: 9 of 16, including `grass_terrain_origin_uv` (xy = texture uv of the tile origin, zw = macro-shadow uv origin).
- **Curve parity:** C# vs GLSL over 2048 samples. PASS, worst relative error 1e-6.

**Colour match** (`tools/grass_card_color_match_gpu_test.gd`, mode `color`; CIEDE2000 on block means, neutral settings):

| Pose | Result |
|---|---|
| day_near | pass, 2.35 |
| day_down | pass, 2.38 |
| dusk | pass, 1.73 |
| night | pass, 1.00 |
| day_haze | pass, 2.15 |
| border_forest | pass, 1.44 |
| **border_desert** | **fail, 4.26** (lightness −3.9) |
| far / far_haze | numerically fine (1.46 / 0.86), but fail the gate on too few blocks (5 and 4) |
| **hero vs card** | **fail, 4.35–4.54**: hero blades read brighter (L* 51 vs 47, same hue) |

**Remaining S4 work:**

1. Fix the border_desert lightness and the hero-vs-card brightness gap.
2. Give the far poses at least 8 covered blocks, or relax that gate.
3. Run mode `measure` (GPU ms, never run yet) and mode `weather` (wind path, never run yet). Check nothing else is using the GPU first.
4. Review the production, dusk, night, haze, border and hero screenshots. They are on the measuring machine (scratchpad `grass_s4`); regenerate them with the test.
5. Carry into S5/S9:
   - the fixture still uses a stand-in placer instead of `RingGrassTileBuilder`;
   - the terrain's environment specular (0.18) has no card counterpart;
   - the terrain grass-coverage tint is not accounted for in the match;
   - the macro-shadow uv uses a placeholder ring width in the fixture;
   - the modifier tint path is untested;
   - cloud/macro/planet-shadow uniforms need projector registration;
   - the S6 compute path can reuse `ring_grass_card_curve.gdshaderinc`.

None of the S4 files are referenced by the game yet. Gameplay is unaffected until S5 wires the Cards style in.

## Other open items

- `prototypes/Ringworld_MainSpire_FarProxy_Rings_Update.zip` was applied earlier. Keep or delete it.
- The tracked `~libterrain…dll` temp file should probably be untracked, with `~*.dll` added to `.gitignore`.
- The C# "trampoline" lines in `godot.err` are backtraces on warnings, not exceptions. The native voxel mesher DLL is not built; the managed fallback is used.

## Update (2026-10-15, later): S4 finished, S5 done (uncommitted)

**Machine:** these runs were on the RTX 4070 desktop. The `terrain_surface_include_regression` goldens are byte-exact A1000 captures and fail on the 4070 even with no terrain changes, so re-check them on the A1000. GPU ms below are 4070 numbers and do not compare with the A1000 baselines.

**S4 fixes:**
- Colour-match mask could not see cards beyond 70 m. Fixed in `GrassCardShaderFixture.SetMode`; far poses now cover 53 blocks.
- border_desert framing was parallax-biased. The camera now looks along the border, giving 2.37.
- Atlas G/B were diluted in the mips. They are now coverage-weighted with a push-pull fill, and `GeneratorVersion` is 2. Hero vs card is now 1.83–1.88. `GrassCardAssetsTest` has an anti-dilution check: strict ±0.03 on mips 1–4; from mip 5, R ≥ 128 selection bias toward root texels is expected.
- Colour match passes, 9/9.
- `measure` (4070): cards+hero cost +1.0 ms near and +0.24 ms far.
- `weather`: grass moves.

**S5 (GrassRenderStyle Blades/Cards inside `RingStandingGrassLayer`):**
- New files: `RingGrassStyleProfile.cs`, `RingStandingGrassLayer.CardStyle.cs`, `tools/grass_card_style_test.gd`, `tools/grass_card_layer_gpu_smoke.gd`.
- Grave-key Grass tab "Render style" exposes the style (default Blades), the reach (400) and green pull (0.15).
- Blades fingerprint goldens are unchanged.
- Card origin uv unwraps around the Terrain3D frame anchor, matching the terrain shader across the ring seam. The circumference is 1 mod 5.
- Reach/lookahead/H/cap live in `CoverageFingerprint`, so they change tiers without a reset.
- The instance budget is derived at chance 1 with held tiers.
- Lead review findings are all addressed.
- Tests: `grass_card_style_test`, `grass_card_layer_gpu_smoke` (frustum retire/re-upload, underground hold), colour match, hysteresis, tree contract, placement, assets and follower all PASS.
- Pre-existing failure: `grass_live_tuning_smoke_test` fails identically at HEAD (clipped SectionAdaptivegrassgroups).

**Next:**
- The user does an in-game look at Cards, including walking across the ring seam (around = 0).
- Then S6 (compute card mode; also re-enable adaptive grouping for Cards, which S5 disables) ‖ S7 (hero layer; then drop the `HeroThinMinimum = 1` override in Cards).

## Update 2 (2026-10-15, late): hero scrapped, S6 done (uncommitted)

USER DECISIONS: hero blades scrapped (S7 dropped), card placement on GPU, adaptive grouping on for Cards — recorded in `grass-cards-integration-plan.md`. Design: [grass-cards-s6-gpu-plan.md](grass-cards-s6-gpu-plan.md).

- **A (hero removal):** hero files deleted, hero code stripped. New card placement goldens in `GrassCardPlacementTest` (`CardGolden`, 6 cases). `tools/grass_cards_spike_measure.gd` still mentions hero (spike tooling, left alone).
- **B1 (compute):** `ParameterBytes` 256→512; card flag p[62], card values p[64..77]; `BatchFrameParameters.GrassCard` is optional. Card accept branch plus GLSL curve copy in `ring_gpu_vegetation_indirect.glsl`. Words 0..63 are pinned by `gpu_vegetation_parameter_layout_test`. GPU vs C# reference parity: `gpu_grass_card_culling_test`.
- **B2 (layer):** Cards uses GPU culling when `UseGpuCulling` is on, with CPU MultiMesh as the fallback. Grave-key "Cards GPU distance margin" (0–8, default 2). GPU vs CPU image parity is exact. Idle shows 0 builds.
- **C (grouping):** Cards groups start at ≥ last tier boundary + H + 4 + hysteresis (≈206 m), so the 2×2 start slider is a lower bound in Cards. The group output uses the intermediate slot. There is a 2 m Cards frustum hold. At reach 400 there are 122 batches (179 ungrouped).
- **Lead review fixes:**
  - F1: Cards re-captures GPU readiness after `RequestFrame`. This fixes a one-frame hole on every tier change. It relies on the default (non-separate) render thread model, and Blades still has the same hole.
  - F2: when an active Cards group loses a leaf at the reach edge, it splits instead of being retired.
- **Walk test:** `tools/grass_card_grouping_walk_test.gd` (about 12 min; `level4x4=0` shortens it). Walking 300 m out and back gives 0 holes, 0 double draws and at most 1 split per group.
- **Pre-existing test failures**, confirmed at HEAD and fixed in the tests: `gpu_grass_integration_test` (meadow tufts default), `grass_adaptive_grouping_test` (EnableDistanceLod default).
- **Open:**
  - 122 batches exceed the 96 per-frame budget (`MaximumGpuCullingBatchesPerFrame`), so deferred tier handoffs can still drop for a frame.
  - Frustum churn beyond ±0.75 m sway.
  - The smoke's grouped-vs-ungrouped image parity probably sees no grouped pixels (camera pitched down).
  - GPU memory for cards roughly doubles.
  - Nothing measured on the A1000.
- **Next:** S8 (settings/profiles/migration), S9 (Cards terrain coverage/sheen), S10, S11 (in-game A/B and perf on the A1000).

## Update 3 (2026-10-16): Cards batch budget, S8 settings — grass closed out for now (uncommitted)

- **Cards batch budget:** in Cards the GPU culling batch budget is `EffectiveGpuCullingBatchesPerFrame` = max(setting, batches + 16). At reach 400 that is 195, so there are 0 deferred batches. Blades keeps the setting. The F1 tier-handoff readiness fix stays Cards-only (user decision). The walk test passes with 0 holes and 0 doubles both ways.
- **S8:**
  - 34 card keys, from `GrassCardTileMeters` to `GrassCardConvergeEndPercent`. They live in `RingGrassCardSettings.cs` and `RingStandingGrassLayer.CardSettings.cs`.
  - The drawer partial is `RingLiveTuningDrawer.GrassCards.cs`, with groups "Grass cards — coverage / form / colour".
  - Rebuild classes:
    - placement: tile, near/low density, heights, lean, atlas blades;
    - coverage: curve, reach, fade, near width (no reset);
    - material only: everything else.
  - Quality presets carry the card reach and near density: Low 300/14, Laptop/Medium/High 400/14, VeryHigh 450/16, UltraVideo 500/18, Cinematic5090 600/20.
  - The profile default is still Blades.
  - `GrassCardTerrainSheen` is deferred to S9. Scale compensation is capped at 2.5, because the bounds and the GPU guard assume it.
- **S8 verification:** headless tests all pass. The windowed suite has not been re-run since S8: card culling, layer smoke, colour match, integration, adaptive grouping, grouped phases and live tuning. Run it with `sh <scratchpad>/s8_suite.sh ...` or individually.
- **Grass is closed out for now.** Remaining when it is reopened:
  - S9 (Cards terrain coverage tint/sheen);
  - S10 (full invariant suite);
  - S11 (in-game A/B and perf on the A1000, then the default flip to Cards);
  - frustum churn beyond ±0.75 m sway;
  - the grouped image parity pose;
  - the A1000 terrain goldens.
