# Terrain and vegetation revamp: Codex work orders

These are the work orders for every phase of `docs/terrain-vegetation-revamp-plan.md` (revision 4). Each phase file follows [TEMPLATE.md](TEMPLATE.md), and roles map to `.codex/agents/` (`sol_implementer`, `sol_lead`, `sol_deep`). Read `docs/terrain-vegetation-handoff-2026-11-10.md` first.

Written 2026-11-10 by four `claude-lead` agents working in parallel, then cross-checked by the primary agent. File:line facts were verified against the `gpu-offload` working tree on that date. Anything that depends on code not yet written is marked "unverified" in each file's Phase facts.

## Files and order

| File | Phase | WOs | Freezes as | Starts after |
|---|---|---|---|---|
| [T0-wrapup.md](T0-wrapup.md) | T0 leftovers (weather pin, the A1000 run, exit crash, shadow-harness prefs, earlier failures, vastness V0) | T0W-1 to T0W-8 | none | now |
| [T1.md](T1.md) | Recipe v9, generation ledger, test-world tooling | T1-0 to T1-9 | none (plumbing) | implemented/reviewed; uncommitted, gate exceptions in handoff |
| [T2.md](T2.md) | Rivers and lakes (reset 2026-11-11, see `docs/t2-river-reset.md`) | T2R-0 to T2R-16 | (open until T3) | T1. The Codex study (old T2-1 to T2-15) is retired. |
| [T3.md](T3.md) | River danger data (done) and water sound (re-scoped 2026-11-13: flow, danger in play and floating moved to T9i) | T3R-1 to T3R-3, T3R-6 and T3R-10a to T3R-10d (done); T3R-10e to T3R-10g moved to the sound project; T3R-12 to T3R-16 (the freeze, kept in T3 by the user on 2026-11-21; T3R-12 in progress, T3R-13 waits for G2) | "rivers" (gen 2, `water.rivers` alone) | T2 core |
| [../sound/README.md](../sound/README.md) | Sound effects, its own project since 2026-11-14 (water loops, T3R-10e to T3R-10g moved from T3, then weather and thunder) | SFX-1, SFX-2, T3R-10e to T3R-10g | none (sound is not world data) | now |
| [T4.md](T4.md) | Ground surface | T4-1 to T4-8 | "ground" | T1 (alongside T2/T3) |
| [T5.md](T5.md) | Scatter foundation and rocks | T5-1 to T5-12 | "rocks" | T4-1 design |
| [T6.md](T6.md) | Vegetation from existing packs | T6-1 to T6-13 | "vegetation", then gate G3 | T5 designs |
| [T7.md](T7.md) | Lowland meso-relief (re-planned 2026-11-21) | T7-1 to T7-15, contingent T7-B | "landforms" | rivers frozen (T7-1 and T7-2 may start earlier) |
| [T8.md](T8.md) | Rock textures and new tree forms (re-planned 2026-11-21) | T8-R1 to T8-R3 (rock), T8-S1 to T8-S10 (trees) | none of its own as proposed: rock lands in "ground" and "rocks", trees in "vegetation" (user question 3 in T8.md) | now; it comes **before** the T4, T5 and T6 freezes and before G3 |
| [T9.md](T9.md) | Contingent groups T9a-T9i (T9i: river flow, danger and floating, moved from T3 on 2026-11-13; its orders keep their T3R numbers) | per group | per group | each needs the user's go |
| [TR.md](TR.md) | Tree rendering: LOD decimation, camera-facing far billboard, billboard match, maximum tree count, distant canopy shadows (added 2026-11-13) | TR-1 to TR-10 | none (renderer only; decision D1) | now; coordinate with T6-8, T6-10, T6-11 |

**Critical path:** T0W-1, then T1, then T2 and T3 (the rivers freeze), then T7. The dressing track (T4, T5, T6) runs alongside T2 and T3 once T1 lands. Its river-dependent WOs (T4-7, T5-11, T6-12) wait for rivers and land in whichever step freezes after them. Vastness V1 and later depend on T1 as well (vastness plan section 11).

Generation numbers are assigned when a phase freezes, in the order phases freeze. If "ground" freezes before "rivers", it becomes generation 2. The test world names (`g<NN>-<step>-s1337`) follow from that.

## Cross-phase rules settled during the cross-check

- **Weather pin.** T0W-1 owns it. It blocks every look golden or comparison capture: T2R-16, T3-14, T4-2 onward, T7-14, T8-R1, T8-R3 and T8-S9.
- **Feature IDs.** Use the final IDs and bit assignments in [T1-0 C3](T1.md#c3-shared-feature-ids-and-bits). The low word supports 31 features. The table also defines the overflow rule.
- **Adding later recipe fields.** T1 design D9 applies: a field exists in a world's recipe only when its feature is frozen at or below the world's generation, or is listed in the world's review features. T7-3 and T2R-2 rely on this.
- **Tree catalog.** T6-3 creates `ImportedTreeSourceTable`. T6-5 defines the rule for appending catalog entries without changing older generations' fingerprints (`tree_catalog_append_test`). T8-S3 appends rows from index 55 for a third catalog (`T8Species`); it does not create a second table and does not touch the base catalog or its pipeline.
- **Drainage height source.** The drainage bake and site placement read the natural height (`IDrainageHeightSource`, T2R-3). Later relief, including T7's meso terms, is never added to it, so a landforms world has the same rivers and sites as the rivers world of the same seed.
- **GPU recipe buffer.** New height-model words go at index **320** or later. The height block ends at 185, the climate block runs from 185 to 317, and T6-10 took 317 to 319 (world features, forest seed, forest circumference; `RC_P_COUNT` is 320, checked 2026-11-21).
- **Meso relief is a stage, not a natural-height term** (T7 decision P1, 2026-11-21). The GPU drainage bake calls the same `hm_sample` as the proxy kernels, so T7's terms live in a separate function that the drainage shader never includes.
- **River sectors build on the CPU** (T2R-11), because the GPU proxy classes cannot represent inland water. A GPU lake class and the GLSL carve are rungs of the T2R-15 fallback ladder, used only on a measured trigger.
- **Climate fields (T1-0 C1).** The primary agent decided that the sampler reads them from T1 onward, with defaults equal to today's settings, so generation 1 stays identical.

## Decisions waiting on the user

| # | Blocks | Question | Recommendation in the WOs |
|---|---|---|---|
| 1 | T1-7 | Save slots | **Answered 2026-11-10: free a slot; existing saves need not be preserved.** |
| 2 | T0W-4 | Shadow-harness prefs fix | **Answered 2026-11-10: yes, option A.** |
| 3 | T0W-3 | Commit and branch | **Answered 2026-11-10: T0 work committed on branch `terrain-vegetation-revamp`.** |
| 4 | T2 | Lake coverage and excavation | **Answered 2026-11-11 after map review.** Keep the 40 m excavation cap and accept a lake-rich direction. Report measured coverage. The 3 % and 2-5 % figures remain study references. |
| 5 | T3-10 | River sound: do you supply CC0 or CC-BY calm and rapids loops, or may an agent find them? | **Superseded 2026-11-14:** the loops are Stable Audio Open takes the user picked; real recordings are an option in the sound project (SFX-1). |
| 6 | T3-1 | If no calibration reaches the danger targets, relax them? | Asked only if it happens. |
| 7 | G2 | Swim the calm, fierce and torrent reaches and sign off before the rivers freeze. | **Changed 2026-11-21 (user):** G2 is the review of how the rivers look and sound, plus the danger map; the swim through the classes moved to T9i's review. |
| 8 | T4-1 | Rock texture: the existing alpine rock now, or wait for CC0 textures from T8? | **Answered 2026-11-17: wait for T8 rock textures** (keep the alpine texture meanwhile; the T4 freeze waits). |
| 9 | T6 | Split cost gate: new classes at most 0.3 ms and 16 draws, canopy within +5 % of generation 1, ground cover and far canopy at most 0.05 ms each. May trees cost more if the look needs it? | The split as stated. |
| 10 | T5-2 | Does the 24-draw gate count shadow draws, and may it be relaxed? | **Answered 2026-11-18: 20 draws, counting the depth prepass, colour and both sun cascades (24 while a large POI widens the sun map to 4 cascades); the 24 gate is kept (T5 design B3).** |
| 11 | all freezes | Freeze on 4070 numbers, or wait for the A1000 baseline? | The A1000 first. |
| 12 | T7 | Plains slope ceiling: 10 or 12 degrees? | **Answered 2026-11-21: 10 degrees on ordinary plains; hummock country up to 14** (T7.md, target R3). |
| 13 | T7 | Cliffs only raise land, so no coves? Dunes only on coastal strands? | **Answered 2026-11-21: coves are allowed, and cliffs may also cut into land** (T7.md decision P10, order T7-7b). Dunes: coastal strands only, **assumed**; the user did not answer on dunes separately. |
| 14 | T8 | Allow an offline tree-generation tool if no CC0 or CC-BY pack fits? | **Answered 2026-11-21: all three routes, in order:** derived in-repo from the LOLIPOP parts, then a pack, then a Blender generator (Blender may be installed when needed). |
| 15 | T9 | A go per group, plus the cave-field question for T9e. | — |
| 16 | T6-11 | If a visible LOD pop appears at the shipped 25-40 m fade, restore LOD1? That would change generation 1's look. | Asked only if it happens. |
| 17 | T3 | Should old worlds also get the new river sound and flow visuals? | No; both are gated off for old worlds. |
| 18 | T3R-12 to T3R-16; T9i question 1 | Keep the rivers freeze in T3? Keep the river danger constants or choose them again? | **Answered 2026-11-21: keep the freeze in T3 (generation 2 "rivers", `water.rivers` alone); keep `RiverDangerConstants.Default` (Nm 0.18, Kd 0.34, E0 0.80).** |
| 19 | T8 | Scope: rock face, dense broadleaf crowns and the tall conifer now; willow, birch and the lone crown later? | **Answered 2026-11-21: as proposed.** |
| 20 | T8, T6-13 | G3 reviews the forest with the new trees, and vegetation freezes with them in one generation? | **Answered 2026-11-21: yes.** |
| 21 | T8 | May the denser canopy cost up to +10 % canopy GPU instead of +5 %? | **Answered 2026-11-21: up to +10 % is allowed** (T8.md budget B4, against the v2 review forest; for the T8 forest this replaces the +5 % of decision 9). |
| 22 | T7 | How big may a cove be? Proposed: 200 m to 1 km across, 3 to 10 per 100 km of coast, none within 300 m of a river, lake or site (so no bays at river mouths). | **Answered 2026-11-21: bigger coves** (provisional bound about 3 km, confirmed from T7-1 pictures; keep-out from rivers kept). |
