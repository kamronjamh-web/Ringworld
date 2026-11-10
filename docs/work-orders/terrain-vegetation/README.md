# Terrain and vegetation revamp: Codex work orders

These are the work orders for every phase of `docs/terrain-vegetation-revamp-plan.md` (revision 4). Each phase file follows [TEMPLATE.md](TEMPLATE.md), and roles map to `.codex/agents/` (`sol_implementer`, `sol_lead`, `sol_deep`). Read `docs/terrain-vegetation-handoff-2026-11-10.md` first.

Written 2026-11-10 by four `claude-lead` agents working in parallel, then cross-checked by the primary agent. File:line facts were verified against the `gpu-offload` working tree on that date. Anything that depends on code not yet written is marked "unverified" in each file's Phase facts.

## Files and order

| File | Phase | WOs | Freezes as | Starts after |
|---|---|---|---|---|
| [T0-wrapup.md](T0-wrapup.md) | T0 leftovers (weather pin, the A1000 run, exit crash, shadow-harness prefs, earlier failures, vastness V0) | T0W-1 to T0W-8 | none | now |
| [T1.md](T1.md) | Recipe v9, generation ledger, test-world tooling | T1-0 to T1-9 | none (plumbing) | now; T1-0 design first |
| [T2.md](T2.md) | Rivers and lakes | T2-1 to T2-15 | (open until T3) | T1 |
| [T3.md](T3.md) | Current, danger, floating | T3-1 to T3-16 | "rivers" (gen 2) | T2 core; T3-2 can start any time |
| [T4.md](T4.md) | Ground surface | T4-1 to T4-8 | "ground" | T1 (alongside T2/T3) |
| [T5.md](T5.md) | Scatter foundation and rocks | T5-1 to T5-12 | "rocks" | T4-1 design |
| [T6.md](T6.md) | Vegetation from existing packs | T6-1 to T6-13 | "vegetation", then gate G3 | T5 designs |
| [T7.md](T7.md) | Lowland meso-relief | T7-1 to T7-14 | "landforms" | rivers frozen |
| [T8.md](T8.md) | New tree species and assets | T8-1 to T8-10 | "species" | G3 forest review |
| [T9.md](T9.md) | Contingent groups T9a-T9h | per group | per group | each needs the user's go |

**Critical path:** T0W-1, then T1, then T2 and T3 (the rivers freeze), then T7. The dressing track (T4, T5, T6) runs alongside T2 and T3 once T1 lands. Its river-dependent WOs (T4-7, T5-11, T6-12) wait for rivers and land in whichever step freezes after them. Vastness V1 and later depend on T1 as well (vastness plan section 11).

Generation numbers are assigned when a phase freezes, in the order phases freeze. If "ground" freezes before "rivers", it becomes generation 2. The test world names (`g<NN>-<step>-s1337`) follow from that.

## Cross-phase rules settled during the cross-check

- **Weather pin.** T0W-1 owns it. It blocks every look golden or comparison capture: T2-15, T3-14, T4-2 onward, T7-13 and T8-9.
- **Feature ids.** T1-0 (decision C3) sets the final feature ids and bits. T2-T6 use these placeholder names: `rivers`, `river_current`, `ground_surface`, `ground_river_beds`, `scatter_rocks`, `scatter_river`, `forest_structure`, `ground_cover`, `far_canopy`, `riparian_vegetation`. When T1-0 lands, rename them in the phase files in one pass. The limit is 31 bits.
- **Adding later recipe fields.** T1 design D9 applies: a field exists in a world's recipe only when its feature is frozen at or below the world's generation, or is listed in the world's review features. T7-3 and T2-3 rely on this.
- **Tree catalog.** T6-3 creates `ImportedTreeSourceTable`. T6-5 defines the rule for appending catalog entries without changing older generations' fingerprints (`tree_catalog_append_test`). T8-3 extends that table; it does not create one.
- **Drainage height source.** The drainage bake and site placement read the natural height (`IDrainageHeightSource`, T2-4). Later relief, including T7's meso terms, is never added to it, so a landforms world has the same rivers and sites as the rivers world of the same seed.
- **GPU recipe buffer.** New height-model words go at index 317 or later. The height block ends at 185, and the climate block runs from 185 to 317.
- **River sectors build on the CPU** (T2-10), because the GPU proxy classes cannot represent inland water. The GLSL carve (T2-14) is contingent on gate G1.
- **Climate fields (T1-0 C1).** The primary agent decided that the sampler reads them from T1 onward, with defaults equal to today's settings, so generation 1 stays identical.

## Decisions waiting on the user

| # | Blocks | Question | Recommendation in the WOs |
|---|---|---|---|
| 1 | T1-7 | Save slots | **Answered 2026-11-10: free a slot; existing saves need not be preserved.** |
| 2 | T0W-4 | Shadow-harness prefs fix | **Answered 2026-11-10: yes, option A.** |
| 3 | T0W-3 | Commit and branch | **Answered 2026-11-10: T0 work committed on branch `terrain-vegetation-revamp`.** |
| 4 | T2 | Lake share target. | 3 % of land (2-5 % accepted). |
| 5 | T3-10 | River sound: do you supply CC0 or CC-BY calm and rapids loops, or may an agent find them? | — |
| 6 | T3-1 | If no calibration reaches the danger targets, relax them? | Asked only if it happens. |
| 7 | G2 | Swim the calm, fierce and torrent reaches and sign off before the rivers freeze. | — |
| 8 | T4-1 | Rock texture: the existing alpine rock now, or wait for CC0 textures from T8? | Existing alpine rock now. |
| 9 | T6 | Split cost gate: new classes at most 0.3 ms and 16 draws, canopy within +5 % of generation 1, ground cover and far canopy at most 0.05 ms each. May trees cost more if the look needs it? | The split as stated. |
| 10 | T5-2 | Does the 24-draw gate count shadow draws, and may it be relaxed? | Asked after T5-2 measures. |
| 11 | all freezes | Freeze on 4070 numbers, or wait for the A1000 baseline? | The A1000 first. |
| 12 | T7 | Plains slope ceiling: 10 or 12 degrees? | 10 degrees. |
| 13 | T7 | Cliffs only raise land, so no coves? Dunes only on coastal strands? | Yes to both. |
| 14 | T8 | Allow an offline tree-generation tool if no CC0 or CC-BY pack fits? | — |
| 15 | T9 | A go per group, plus the cave-field question for T9e. | — |
| 16 | T6-11 | If a visible LOD pop appears at the shipped 25-40 m fade, restore LOD1? That would change generation 1's look. | Asked only if it happens. |
| 17 | T3 | Should old worlds also get the new river sound and flow visuals? | No; both are gated off for old worlds. |
