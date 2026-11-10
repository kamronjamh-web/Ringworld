# Terrain and vegetation revamp: handoff 2026-11-10 (end of T0)

The project is paused on the user's instruction after T0.

## Branch

**All revamp work is committed to branch `terrain-vegetation-revamp`, and all future revamp work goes on that branch.** The user decided this on 2026-11-10. The branch was cut from `gpu-offload` at `ca97a93`. It holds the grass Cards default, the T0 tools, fixtures and manifest entries, the plan, spec and handoff docs, the Codex work orders in `docs/work-orders/terrain-vegetation/` and the doc fixes.

Other sessions' uncommitted work was deliberately left out of the commits and stays in the working tree. Do not sweep it into a revamp commit. It covers the PDA prototype and its tests, `AGENTS.md`/`CLAUDE.md`, `.claude/agents/claude-fable.md`, `docs/shadow-revamp-plan.md`, `docs/pda-*.md`, the `.import` churn and the deleted terrain dll.

**Codex work orders:** start from `docs/work-orders/terrain-vegetation/README.md`. It gives the order, the cross-phase rules, and the user decisions, both answered and open.

**User answers on 2026-11-10:**
- Save slots may be freed: existing saves need not be preserved (T1-7).
- The shadow-harness prefs fix is approved, option A (T0W-4).
- This branch is the commit branch.

## Read first

1. `docs/terrain-vegetation-revamp-plan.md`, revision 4 (Fable 5.2). Every question is decided (section 13). Read the thesis, section 2.2 (world generations), section 3 (water), and section 11 (phases).
2. `docs/landscape-audit-t0-spec.md`: the T0 spec (claude-lead). Section 8 holds all T0 results.
3. `docs/vastness-plan.md`, section 11, added by Fable: the vastness plan's visible changes are also gated by world generation.

## User decisions (2026-11-09)

- **New worlds only, project-wide** (vastness plan included). Every major visible phase freezes as its own world generation, and each freeze gets a fresh test world named `g<NN>-<step>-s1337`. Older worlds never change. The one global exception is the grass Cards default.
- **Rivers and lakes first.** The current pushes the player, and floating bodies use a generic floating system with no specific first item. River danger varies from calm to swift to fierce to torrent, and is derived from the landscape. Dangerous water carries you and never harms you.
- **Third-party CC0 and CC-BY assets are allowed**, but use the existing LOLIPOP packs fully first.
- **Grass Cards is the default.** This is done (uncommitted): the profile default, the class default, the menu text, and tests pinned to Blades where they measure Blades.
- **Accepted defaults for the rest:** hummocks with ponds, buttes and coastal cliffs or dunes first; natural up close and engineered at map scale; willow/alder, birch/aspen and a broad solitary crown later; the ground-level walk is favoured; one shared audit for both plans; generation 1 plus the last three generations stay loadable; one generation per signed-off phase.
- **Standing instruction:** send key pictures to the user in chat (SendUserFile) throughout the project.

## T0 status: done, except the A1000 cost run

| WO | Result | Where |
|---|---|---|
| Docs | Stale LOLIPOP, forest, lowland and water doc wording fixed | `THIRD_PARTY_ASSETS.md`, `resources/trees/lolipop/README.md`, three docs |
| T0-1 | Pinned generation 1 world, pose fixture (14 poses), band table, GPU harness; the smoke passes | `tools/LandscapeWorld*.cs`, `LandscapePoseFinder.cs`, `landscape_harness_*.gd`, `tools/fixtures/landscape_poses_v8_s1337.json` |
| T0-2 | Relief spectrum (8.1) | `tools/LandscapeReliefAudit.cs` |
| T0-3 | Drainage prototype and river map (8.2) | `tools/LandscapeDrainagePrototype.cs`; map at `.godot/audit_baseline/common/t0/drainage/20261109_233403/` |
| T0-4 | Inland-water gap list (8.3) | `tools/LandscapeInlandWaterAuditTest.cs` |
| T0-5 | Tree pack audit (8.4) | `tools/LandscapeTreePackAuditTest.cs` |
| T0-6 | Generation 1 fingerprint golden (8.5) | `tools/WorldGenerationFingerprint*.cs`, `tools/fixtures/world_generation_fingerprint_golden.json` |
| T0-7 | 4070 look references (18 views), cost baseline (non-authoritative), LOD switch (8.6) | `tools/landscape_{look,cost}_baseline.gd`, `landscape_lod_switch_capture.gd`; outputs at `.godot/audit_baseline/rtx4070/t0/` |

**Final check, 2026-11-10:** all six headless landscape tests passed together (`.godot/test_runs/landscape-t0-final/20261110-004047`), and `validate_manifest` passes. The gate suite was not run before the commit: per the user, testing is reserved for Codex, so the first Codex WO session should run `-Suite gate` on this branch.

## What T0 found

- **Flat is really "smooth".** Plains rise and fall about 100 m over 4 km, but below 30 m the relief is about 1 cm. There is no micro-relief, and the 30-300 m relief is 0.74 m RMS. The coastal belt is the flattest land (slope p50 0.2 degrees) and the largest lowland, at 11.9 % of the lattice. Plains, rolling ground and hill country together are only about 5 %, so T7 should prioritise the coastal belt.
- **Naive drainage creates too many lakes and too much danger.**
  - Filling every depression floods about 20 % of land with lakes (the largest is 8,100 km², with pits up to 160 m deep).
  - Rivers are short stubs, with D8 straight-line artefacts on flats.
  - The danger mix is 61 % calm or swift and 9 % torrent, against targets of at least 70 % and at most 3 %, and no single constant fixes it.
  - What T2 needs: breaching or carving outlets through shallow rims, a ferocity or longer-baseline gradient calibration, and a better flow-routing method on flats.
  - The bake is fast: about 0.7 s for 2,000 km at 1 km, and about 11 s extrapolated for the whole ring on the 4070.
- **Inland water** (gap list 8.3):
  - Proxy Lake and River rendering still works when it is given samples.
  - The missing pieces are:
    - the gen-17 sampler's water kind and surface (Ocean-only, `RingTerrainSampler.SurfaceGeneration.cs:65`);
    - the controller override that flattens water to Ocean with an integer top (`RingVoxelExplorerController.cs:1150-1156`);
    - the GPU proxy-height classes (Dry, Ocean and Coastal only; needs a check);
    - far-LOD sampling of narrow rivers;
    - water awareness in near terrain;
    - a real river sound loop.
- **Tree packs:**
  - There are 30 variants (12 maple, 3 fir, 15 pine) with 4 LODs. Selection is uniform, and 25 % of maples are saplings.
  - LOD3 is a single billboard, so snags and logs can only use LOD0-2.
  - The far impostor downsamples textures 4-8x, and the pine LOD3 materials lack metallic and roughness maps.
  - The user's saved vegetation prefs use a 25-200 m near-detail fade. No visible LOD pop was seen from 15 to 60 m; the 60-200 m tail is untested.
- **Cost** (4070, 1080p Medium, user prefs, non-authoritative):
  - Trees dominate in forest: 9.6 ms of a 15.9 ms root GPU frame.
  - Near terrain is about 3.5 ms everywhere.
  - Grass is under 0.5 ms and far terrain under 0.25 ms.
  - The A1000 numbers are still to measure. The T4-T6 budgets assumed tree cost matters less, so revisit them against these numbers.

## Open items, in order

1. **Pin the weather in the T0-7 harness.**
   - The coast look reference was captured during a passing storm (rain, dark sky). The harness pins the world, poses, time and prefs, but not weather or storm state.
   - Pin weather (clear, storms off or fixed) and re-capture the 4070 look references before anyone uses them for `compare`.
2. **A1000 cost run.** On the laptop, undocked at P0 / 35 W, run `.\tools\run_tests.ps1 -Label landscape_gpu -Tests landscape_cost_baseline`, then `landscape_look_baseline` (looks are machine-specific). Do this after item 1.
3. **Cost run crashes at exit.** It prints PASS, then crashes with -1073741819. The suspect is the timestamp hooks at shutdown; it is not isolated. Look and LOD runs exit cleanly.
4. **Shadow harness bug (approved 2026-11-10, option A; execute as T0W-4).**
   - `tools/shadow_harness_common.gd` copies non-existent prefs files (`atmosphere_defaults.tres`, `surface_tuning.tres`), so the shadow S0 baselines ran with default haze.
   - Fixing it changes future shadow baselines, so ask the user first.
5. **Pre-existing test failures seen while making Cards the default.** They are not caused by it and not in the known-failures list: `ui_scaling_smoke_test`, `grave_menu_interaction_test` (AtmospherePreset outside a collapsible section), and `imported_tree_prepared_fallback_test` and `quality_preset_contract_test` (both crash after PASS). Screenshot and perf captures that use default grass now show Cards and may need re-baselining: `wet_world_gpu_test`, `terrain_render_attribution_test`, `grass_*_capture` and others.
6. **Commit.** Done 2026-11-10 on branch `terrain-vegetation-revamp` (see Branch above).
7. **Vastness V0-1 and V0-2** (spec section 4) are unstarted. They build on the T0-1 harness.
8. **T1** (recipe v9, the generation ledger and test-world tooling) is next. Notes for it are in spec section 6:
   - Publish the generation from `_configure_game` (`world_size_launcher.gd:1627`, before `add_child`).
   - World slots already have a `name` in `world.json`.
   - `EmitterSiteTestWorlds` uses code-default climate settings (4x the production frequencies) and should take a generation and production settings.
   - The launcher's certified preset falls back to `launcher_default_config.gd` values (ContinentalContrast 1.35, not the resource's 1.45).

## Working notes for the next agent

- **Running tests:**
  - Use `run_tests.ps1 -Tests <names>`. `-Files` pulls in the smoke set whenever `test_manifest.json` is named.
  - Run GPU work on the `landscape_gpu` profile, one run at a time.
- **Catalog warm-up:** `ImportedTreeCatalog.Load()` is not thread safe, so warm it on the main thread before parallel scans.
- **Where outputs live:** `.godot/audit_baseline/` is kept by `prune_test_output.ps1`. It holds the river maps, relief tables and look references.
- **Agent roles:** follow AGENTS.md (lead or implementer). Fable is not in use unless the user asks for it.
