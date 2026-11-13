# T2 rules. River ends, heads, the minimum river and emitter intakes: work orders (2026-11-13)

Parent: docs/work-orders/terrain-vegetation/T2.md. This file holds the rule orders that follow the T2R-6c and T2R-17 investigations and the user's answers of 2026-11-13. It is a separate file because T2.md was being edited while it was written.
Prerequisite reading: T2.md Phase rules, facts F39 to F50, Spec S5 to S8b and S13, and the Review notes "T2R-6c and T2R-17 results (lead, 2026-11-13)"; docs/work-orders/terrain-vegetation/T3.md Spec E1 to E6; TEMPLATE.md; `.claude/skills/poi-asset-import/SKILL.md` (T2R-24 only).
Status: written by the temporary lead (Fable) on 2026-11-13. Work orders only: no code was changed and nothing was run.
Depends on: T2R-12c and T2R-12d (in flight) and commit point C2b, except T2R-19, which can start at once.
Freeze: none by itself; part of the "rivers" freeze with T3.

Ids in this file: orders T2R-19 to T2R-25 (free in T2.md when this was written), facts R1 to R14, unverified RU1 to RU4, spec RS1 to RS5. T2.md's F, S and D numbers are cited as they are.

## User decisions (2026-11-13)

1. **Dead ends.** Build rule (vi), overflow rivers, together with (c'), the outlet into a passing river. (T2R-22.)
2. **Heads.** Taper them, rule (iv), starting with about 1.5 km. The user sees a picture before the length is final. (T2R-21.)
3. **The D6 minimum river** (8 m wide, 1.2 m deep). Lift it. Small rivers get their true section and become wadeable. (T2R-20.)
4. **Emitters.** Option (A): a site takes in the water that runs towards it, and a river that reaches a site ends at a small built intake structure. The narrower keep-out (E) is measured first, so that fewer rivers are cut off. (T2R-19, T2R-23, T2R-24.)

The realism rule of 2026-11-13 applies to every order here: no floor, minimum, widening or boost. Where an existing clamp is touched or left in place, it is listed under "Open questions for the user".

## Phase rules

The Phase rules of T2.md apply unchanged (executors, production first, construct do not fit, sampling with tolerances, the STOP RULE, testing cadence, pictures). In addition:

- **One builder at a time.** These orders edit the bake, the builder and the operator. Serialise C# edits and builds with T2R-12c, T2R-12d and T2R-18.
- **Versions.** Every order that changes bake output bumps `HydrologyVersions.DrainageAlgorithm` by one; every order that changes operator output bumps `CarveOperator` by one. A graph file is never rebaked while it is valid, and its validity is the version, so a shared bump would let a test profile keep a stale graph between two orders. The saving is made where it costs something: the user's world is rebuilt once, at commit point C2c, after the whole batch.
- **Generation 1.** Every change sits behind `water.rivers`. `world_generation_fingerprint_test` must pass unchanged in every order.
- **No menu entries.** Bake and carve rules are not live tunables, and the intake structure follows haze, shadows and lighting like every POI. No recipe field is added in this batch.

## Facts

Read from the source on 2026-11-13 (working tree on `b55b76d` with T2R-12c in progress: `DrainageAlgorithm` 6, `CarveOperator` 7, `GraphFormat` 4). Line numbers drift; re-check before editing.

**Overflow (T2R-22)**

- R1. `DrainageLakeBalance.Resolve` (scripts/Terrain/Hydrology/DrainageLakeBalance.cs:226-459). The fill pass is at :286-335. A full pool is pinned at its parent's saddle (`pinned[x] = true`, :316 and :332) and its surplus goes to `PoolOf(LeafOf(nbCell))`, where `nbCell` is `NbA` or `NbB` of the parent: the lowest-key neighbour of the saddle cell on the other side. A merge clears the pins (:311, :334). The tree stores the saddle height (`Saddle`) and the two neighbour cells, not the saddle cell itself (`BuildTree`, :122).
- R2. In a closed basin `down`, `Accumulated` and `Area` come from the descent alone (:381-389). A pinned pool gets `raw = top` (:402) and the level `floor(top)`. A pool with `floor(top) < floor(low) + 1` holds no lake and is counted as a pan (:422): this is where 21 to 23 of the Dead stems per ring come from (F48).
- R3. A spilling basin keeps the links of the flood inside (`OldDown`, :359-370) and passes its outflow and its whole area to the exit (:371-372). This is the construction an overflow lake needs on a smaller scale.
- R4. The builder (`RiverGraphBuilder.Build`, scripts/Terrain/Hydrology/RiverGraphBuilder.cs). A channel cell is land, visited, not a lake cell, with `down >= 0` and area at or above the threshold (:581). `Continues` (:605-609) marks a lake's exit cell only when the flow goes on into a stem head or an outlet; into a cell that already carries a river it stops one cell short. The lead rule skips an outlet cell that has a river upstream (:639). These two lines are case (c) of T2R-6c. Terminals are set at :760-779; anything that is not a confluence, lake, sea, Lung or strip end is Dead.
- R5. The outlet of a lake is the first stem whose first reach has the lake as `UpstreamLake`, is not Dead and whose last cell is not a flooded cell of that lake (`RiverGraph.Build`, RiverGraph.cs:418-427). The outlet hold reads the same field (RiverLevel.cs:332, :350; RiverCarveOperator.cs:624). None of them reads `RiverLake.Basin`, so a closed lake with an outlet needs no new record.
- R6. `drainage_graph_test` has synthetic closed basins with saddles and pans (tools/DrainageGraphTest.cs:200, :258-261) and checks that Dead stems end at a dry pan (:936-945).

**Site intakes and the keep-out (T2R-19, T2R-23, T2R-24)**

- R7. `DrainageBarriers.Rasterise` (scripts/Terrain/Hydrology/DrainageBarriers.cs:18-62). The clearance is `CorridorRadiusMeters` 300 + `LateralCapMeters` 350 + half a cell diagonal (about 707 m), added to the influence radius of every emitter site, to the Wall Engine stadium and to the Weather Lung rectangle. The grid does not record which structure a barrier cell belongs to.
- R8. The Weather Lung rectangle is kind Lung, an outlet, but the same function surrounds it with barrier cells. Nothing can route into it today (D2: "an isolated receiving body with no synthetic inflow"). The Lung is therefore a precedent for the idea, not for the code.
- R9. Barrier cells are blocked in `DrainageDomain.GetNeighbors` and `NeighborIndex` (DrainageDomain.cs:44-77); outlets are Ocean, Lung and StripOutlet (`IsOutlet`, :42). The flood starts from the outlets at their own height (`DrainageConditioning.Flood`, DrainageConditioning.cs:192-235). Routing noise is added to Land cells only (`DrainageBake.BuildRoute`, DrainageBake.cs:142-161).
- R10. The barrier guard (`GuardBarriers`, RiverGraphBuilder.cs:1099-1166) keeps every sample of the node curve 105 m from barrier cells, so the meander (at most 100 m) never enters one. The last node of a stem never moves. `drainage_graph_test` checks that no point of a displaced centreline lies in a barrier cell or closer than influence radius + 300 m to an emitter site (tools/DrainageGraphTest.cs:1040-1057).
- R11. **Derived, not measured.** A point outside every barrier cell is at least influence radius + 650 m from the site centre. With the guard of R10 the centreline keeps that distance, and the carve reaches 300 m from it (the fade end). The 350 m lateral-cap term of the clearance was written before the guard existed and now seems to be counted twice. If so, a keep-out of 300 m plus half a diagonal needs no change to the builder or the operator. T2R-19 measures it.
- R12. Dangerous water is already excluded near sites: a span within 1,500 m of an emitter disc, a Wall Engine stadium or the Lung footprint that would be fierce or torrent becomes a landing (`RiverExclusions.SiteMarginMeters`, scripts/Terrain/Hydrology/RiverSpanBuilder.cs:16). A river that arrives at an intake is calm or swift.

**The D6 minimum and the heads (T2R-20, T2R-21)**

- R13. The floors exist twice. The path law, `RiverHydraulics.PathSection` (RiverHydraulics.cs:19-20, :45, :49), feeds the wander, the meander, the bank-line stations of the level build and the confluence tails. The channel law, `RiverDanger.Channel` with `RiverDangerConstants.MinDepth` and `MinWidth` (RiverDanger.cs:13-14, :57, :61), gives the carved and drawn section through `RiverSpanTable.Create` (RiverSpans.cs:155-176) and decides the landings, whose factors are stored in the graph file. Other readers: `RiverFootprint.UsesProxyLine` (RiverFootprint.cs:23), `drainage_graph_test` (:1026-1028), `river_danger_test` (tools/RiverDangerTest.cs:110-117), the `--terminals` report (tools/DrainageBakeReportTool.Terminals.cs:487-549), T3.md Spec E2 and the T3R-3 constraint "T2's floors".
- R14. Channel and valley values have one reader, `RiverNetwork.ChannelAt` (RiverGraphBuilder.cs:198-221). The operator (RiverCarveOperator.cs:167), the owner rule (`ValleyOnly(fp, h.A)` per point, :376), the coverage splat (RiverGraph.cs:198-331) and the maps go through it. The first span of a stem does not blend. The player swims when the water stands more than `SwimEnterDepthMeters` 0.55 m over the feet and floats with the feet `SwimSurfaceFloatDepthMeters` 1.15 m under the surface (scripts/Voxel/Integration/RingVoxelExplorerController.cs:79-81, :1218, :1377). `InlandSwimProbe.FindPoints` looks for a river point at least 1.5 m deep (tools/InlandSwimProbe.cs:93).

**Unverified (check before relying on them)**

- RU1. S8 names a bake check that no plug band reaches a site influence disc. No such check was found in the bake; the source has the centreline check of R10 and the clearance lines of the `--terminals` report. T2R-19 reports what exists.
- RU2. How many rivers end at an intake, and how much of the ring's runoff leaves that way. Today 24 to 36 dammed lakes per ring are ones a mapped river ends in; the number after the dams are gone is only known after a bake (T2R-19).
- RU3. How the class shares of T3 Spec E3 move when the floors go (T2R-20 prints them).
- RU4. The cost of the taper in `ChannelAt`, which is on the operator's hot path (T2R-21 measures it against the 3.0 us tripwire).

## Order and parallelism

| WO | Role | Size | Depends on | Can run alongside |
|---|---|---|---|---|
| T2R-19 Site intakes in the bake behind an option; the keep-out measurement | implementer | M | none | T2R-12c, T2R-12d, T2R-18 (serialise builds) |
| T2R-20 The D6 minimum lifted | implementer | M | T2R-12c, C2b | - |
| T2R-21 Tapered heads | implementer | M | T2R-20 | T2R-22 Part A (serialise builds) |
| T2R-22 Overflow rivers and the outlet into a passing river | implementer | L (two parts) | C2b | T2R-21, T2R-24 (serialise builds) |
| T2R-23 Site intakes switched on | implementer | M | T2R-19, the user's keep-out answer, T2R-22 | T2R-21 |
| T2R-24 The intake structure: sites, streaming and a placeholder | implementer | M | T2R-23 | T2R-25 preparation |
| T2R-25 Rules review | claude-lead | S | T2R-20 to T2R-24 | T3 work |

**Execution order relative to the play-test orders.**

1. **Now, beside T2R-12c and T2R-12d:** T2R-19. Its option is off by default, so no bake output, no version and no picture of T2R-12c or T2R-12d changes. T2R-18 as planned (GPU numbers after T2R-12d).
2. **C2b** (gate suite and commit after T2R-12d). Nothing below lands before it: every later order changes the rivers of the play-test world, and the before and after pairs of T2R-12c and T2R-12d must show the same rivers.
3. **T2R-20, then T2R-21.** They are short, T2R-21 needs T2R-20 (RS4), and the head picture goes to the user early because its answer can come while the rest is built.
4. **T2R-22.** The largest and riskiest order; Part A has a review checkpoint.
5. **T2R-23**, once the user has answered the keep-out question from T2R-19's table, **then T2R-24.**
6. **T2R-25, then C2c** (gate suite and commit; suggested). The user's play-test world is created again here, once.
7. T2R-14 and T2R-16 as in T2.md, after C2c.

T2R-18 has no interaction with this file beyond the shared GPU.

**Versions.** After T2R-12c: `DrainageAlgorithm` 6, `CarveOperator` 7, `GraphFormat` 4.

| Order | DrainageAlgorithm | CarveOperator | GraphFormat | Why |
|---|---|---|---|---|
| T2R-19 | - | - | - | option off by default |
| T2R-20 | +1 | +1 | - | path sections, stored landing factors; the channel section |
| T2R-21 | - | +1 | - | `ChannelAt` only; no rebake |
| T2R-22 | +1 | - | - | links, accumulation, stems |
| T2R-23 | +1 | - | +1 | cell kinds, lakes, stems; the new terminal value |
| T2R-24 | - | - | - | reads the graph |

Expected at C2c: 9, 9 and 5. A rebake of a rivers world takes about 10 s plus the river build; its proxy caches then rebuild through the definition hash.

## Work orders

### T2R-19. Site intakes in the bake behind an option; the keep-out measurement

ROLE: implementer
SIZE: M
DEPENDS ON: none. Can start at once.

OBJECTIVE
The bake can treat the edge of every site keep-out as an outlet (Spec RS2), behind an option that is off by default, and the report tool prints how many rivers end at sites for three keep-out widths. The user and the primary choose the keep-out from that table.

CONTEXT / ROOT CAUSE
- User decision 4: option (A), with (E) measured first.
- F45, F49: water may not enter a barrier cell, so a valley that drains towards a site fills behind the barrier and is held by a plug on 1 km cell lines. 705 to 754 lakes per ring stand above barrier ground, at about half of the 601 emitter sites.
- R11: the keep-out may be 350 m wider than the guard requires. RU2: the number of cut-off rivers is unknown.

FILES/SYMBOLS
- scripts/Terrain/Hydrology/DrainageDomain.cs (`DrainageCellKind`, `IsOutlet`); DrainageBarriers.cs (`Rasterise`); DrainageBake.cs (`DrainageBakeOptions`, `Bake`); DrainageBakeReport.cs.
- scripts/Terrain/Hydrology/RiverGraphBuilder.cs (`RiverTerminal`, the three outlet-kind lists at :608, :648 and :775-778, `RiverBuildReport`).
- tools/DrainageBakeReportTool.cs and DrainageBakeReportTool.Terminals.cs (a `--site-intakes` flag and a `--keep-out=<m>` argument).
- New: tools/DrainageSiteIntakeTest.cs and tools/drainage_site_intake_test.gd.

REQUIRED CHANGES
1. `DrainageCellKind.Intake`, appended after `Basin`. `IsOutlet` includes it. It is not blocked: only Barrier blocks neighbours and diagonals.
2. `DrainageBakeOptions`: `bool SiteIntakes = false` and `double? KeepOutMeters = null` (null is today's 650 m). `Rasterise` takes both. The clearance is `KeepOutMeters + half a cell diagonal`. With `SiteIntakes`, after all barriers are marked, every Barrier cell with a geometric 8-neighbour of kind Land or StripOutlet becomes Intake (RS2).
3. Builder: `RiverTerminal.Intake`, appended after `Dead`. A stem whose next cell is an Intake cell gets it; `Continues` and the lead rule treat Intake like Ocean. No node, guard, level or file work in this order: the measurement runs with `--no-levels`, and the option stays off in production.
4. Report: with `--site-intakes` the bake report and the `--terminals` report add the lines of RS2 "Report". With `--keep-out` the tool bakes at that width.
5. The measurement. Three seeds (876374626, 1337, 4242), whole ring, GPU source, `--no-levels --terminals --site-intakes`, at keep-out 650, 300 and 0, plus one run per seed with the option off as the reference. Write one table per seed (RS2 "Measurement table") into the report folder and into the hand-back.

DO-NOT-CHANGE CONSTRAINTS
- Generation 1 worlds must stay identical: `world_generation_fingerprint_test` must pass unchanged.
- With both options at their defaults the bake is bit-equal to before: `DownHash`, the graph file bytes and every existing test result. No version changes.
- The operator, the level build, the graph file format and the game's bake path are not touched. Production does not set the options.
- No minimum intake size, no rule that keeps a river alive past a site.

VALIDATION STEPS
1. New `drainage_site_intake_test` (headless, synthetic domains through `DrainageBake.BakeDomain`, no sites needed: the fixture marks its own Barrier block and calls the ring conversion):
   - a valley that slopes into a barrier block: with the option off a lake stands against the block above the block's lowest ground (the check can fail: this is today's state); with it on, no lake cell borders an Intake cell whose height is below the lake level, and the valley's cells have terminal Intake;
   - every Barrier cell that touches land is Intake, and no Land cell has a Barrier 8-neighbour;
   - the mass balance closes with the intake outflow counted (relative error at most 1e-9);
   - a closed depression beside the block that is lower than the block's ground keeps its lake.
   Manifest: `"drainage_site_intake_test": {"kind": "test", "mode": "headless", "tier": "gate", "tags": ["terrain", "hydrology"], "marker": "DRAINAGE_SITE_INTAKE", "expected_s": 20, "timeout_s": 180}`.
2. `.\tools\run_tests.ps1 -Tests drainage_site_intake_test,drainage_graph_test,river_graph_cache_test,world_generation_fingerprint_test -Label rivers` (`-DryRun` first). `drainage_graph_test` passes unchanged: that is the "defaults are bit-equal" check.
3. The nine measurement runs and three reference runs through `drainage_bake_report` (gpu-local; one at a time). Evidence under `.godot/audit_baseline/<machine>/t2r/bake/<stamp>_t2r19_s<seed>_k<keep-out>/`. Run `tools/prune_test_output.ps1 -Apply`.
4. One M1-style map crop for the user per keep-out at site 435 (ARC 23,206.5 km, ACROSS 183.5 km, seed 876374626): three pictures, sent with the table.

STOP RULE
4 periods or 5 attempts.

ESCALATION CONDITIONS
- At keep-out 300 the centreline check of R10 fails on any stem (R11 is wrong). Report the stems; do not move the guard.
- Any lake stands above the ground of an Intake cell with the option on.
- More than 15 % of the ring's runoff leaves through intakes at keep-out 650 **(starting value)**. Report it prominently; the primary takes it to the user before T2R-23 is started.
- The flooded share of land leaves the 6 to 10 % band of S8b at any keep-out. Report it; do not touch `E0`.

### T2R-20. The D6 minimum lifted

ROLE: implementer
SIZE: M
DEPENDS ON: T2R-12c (committed at C2b).

OBJECTIVE
No river has a minimum width or depth. Every section is what its discharge and gradient give (Spec RS3). About 5 % of river length gets shallower, by 0.17 m at the median, and can be waded.

CONTEXT / ROOT CAUSE
- User decision 3. T2R-6c: 1.3 to 1.5 % of channel length sits at the 8 m width floor and 4.7 to 5.1 % at the 1.2 m depth floor. Without floors those spans are 0.65 / 1.03 / 1.17 m deep (p10 / p50 / p90) where the depth binds, and 7.2 / 10.1 / 15.9 m wide and 0.43 / 0.74 / 0.98 m deep where the width binds.
- R13: the floors exist in the path law and in the channel law. Both go, so that "D6 is lifted" has one meaning. The batch rebakes anyway.
- T2R-12c made the 4 m proxy level free of coverage because no river was narrower than 8 m. That no longer holds.

FILES/SYMBOLS
- scripts/Terrain/Hydrology/RiverHydraulics.cs (`MinDepth`, `MinWidth`, `PathSection`); RiverDanger.cs (`RiverDangerConstants`, `Channel`); RiverFootprint.cs (`UsesProxyLine`); HydrologyVersions.cs.
- tools/DrainageGraphTest.cs (:1026-1028); tools/RiverDangerTest.cs (:110-117 and the header); tools/RiverFarWaterFractionTest.cs (the `UsesProxyLine(4)` check and the owner-rule spacings); tools/ProxyRiverTrueWidthTest.cs; tools/DrainageBakeReportTool.Terminals.cs (:487-549, the D6 block); tools/InlandSwimProbe.cs; tools/inland_swim_smoke_gpu_test.gd.

REQUIRED CHANGES
1. Delete `RiverHydraulics.MinDepth` and `MinWidth` and `RiverDangerConstants.MinDepth` and `MinWidth`. `PathSection` and `Channel` use the law without them (RS3). A discharge of 0 gives a zero section through an explicit branch; there is no epsilon floor on depth or width.
2. `RiverFootprint.UsesProxyLine(spacing)` is `spacing > 2`: the 4 m level carries coverage for the rivers it does not hold as water (under 8 m wide). The slot payload of T2R-12c Part B already covers every slot level; confirm it for 4 m.
3. The `--terminals` report: the D6 block becomes "smallest sections": the p1, p10 and p50 of width and depth by span length, and the length with a centre depth under 0.55 m and under 1.15 m (the two controller depths of R14).
4. `InlandSwimProbe.FindShallowRiver(game)`: a River point on a reach centreline with a water depth between 0.20 and 0.50 m, with its bank, in the form of `FindPoints`. `inland_swim_smoke_gpu_test` adds a wade pose there (validation 4).
5. `DrainageAlgorithm` + 1 and `CarveOperator` + 1.

DO-NOT-CHANGE CONSTRAINTS
- Generation 1 worlds must stay identical: `world_generation_fingerprint_test` must pass unchanged. Legacy gen-16 rivers are untouched (F19).
- Every other constant of S6 and of T3 Spec E2 and E3 keeps its value: `Kd`, `Np`, `Nm`, `Sa`, `Gn`, `Vpool`, `Wmax`, the bank, floodplain and meander formulas. No recalibration in this order.
- The controller's swim depths are not changed.
- Spans where neither floor acted keep their section bit for bit (compare before and after on the strip graphs).
- No replacement minimum of any kind, for width, depth, coverage or meander.

VALIDATION STEPS
1. `drainage_graph_test`: the relation check becomes `Q = U w d` within 1e-6, `d > 0`, `w > 0`, `d` non-decreasing in Q at fixed S, the speed limits as before. The check can fail: a section built with the old floors breaks `Q = U w d` at a discharge of 2 m^3/s.
2. `river_danger_test`: widths in (0, 400] and depths above 0; `Q = U w d` within 1e-6; monotony, landings and exclusions as before. It prints the class shares of both strips. If a share moves by more than 3 points from the row in T3 Spec E3, that is an escalation, not a new bar.
3. `river_far_water_fraction_test` and `proxy_river_true_width_test`: `UsesProxyLine(4)` is true; the owner rule and conservation hold at spacing 4; the true-width test adds five sections under 8 m wide and checks them at the 4 m level (no water triangle, payload within 10 % of the true area) and at the 2 m level (drawn width within the bars of T2R-12c).
4. `inland_swim_smoke_gpu_test`: the lake and river swim checks pass unchanged. New wade pose: the player stands on the bed, is not swimming, and the water surface is above the feet and equals the sampler level within 0.05 m. If the probe finds no shallow point the pose fails. Manifest: `expected_s` of the existing entry from 90 to 120.
5. `.\tools\run_tests.ps1 -Tests drainage_graph_test,river_danger_test,river_far_water_fraction_test,proxy_river_true_width_test,river_carve_invariants_test,inland_water_sampler_test,world_generation_fingerprint_test -Label rivers`; `.\tools\run_tests.ps1 -Tests inland_swim_smoke_gpu_test,inland_water_render_gpu_test -Label landscape_gpu` (`-DryRun` first).
6. One whole-ring report (seed 1337, `--terminals --danger`): the smallest-sections block, the class table with "the user's bands: inside", landings, bytes.
7. One before and after pair for the user: a stream that sat at the floor near the play-test spot (from the report's list), from 150 m, noon, clear. Captures under `.godot/captures/landscape/t2r/d6/`; delete raw frames; `tools/prune_test_output.ps1 -Apply`.

STOP RULE
4 periods or 6 attempts.

ESCALATION CONDITIONS
- "The user's bands: inside" turns to outside on either whole-ring seed, or a strip share moves by more than 3 points. Report the table; do not retune a constant.
- NaN or infinite values in any section, or the operator divides by a zero half width.
- The 4 m level shows separate dots along a narrow stream. The one allowed fix is the wider splat kernel of S11b. Do not add a minimum.
- At the 2 m level or in the near terrain a stream under about 3 m wide breaks into separate wet patches (F41) on more than the first 50 m of a reach. Report the widths and the places; do not widen.
- The splat cost of the 4 m level breaks the budget of T2R-12c step 7.

### T2R-21. Tapered heads

ROLE: implementer
SIZE: M
DEPENDS ON: T2R-20 (the law must have no floors, RS4).

OBJECTIVE
A source stem starts as a point and grows to its mapped section over its first 1.5 km (Spec RS4). The user sees pictures of one head before the length counts as final.

CONTEXT / ROOT CAUSE
- User decision 2. F50 and T2R-6c finding 2: 6,531 to 7,252 heads per ring start 42 m wide and 1.6 m deep at the median, with a round end, because the mapped network starts where the catchment reaches 100 km^2.
- The taper shows less water than the land would carry on that stretch. Nothing is added. The true answer, headwater streams up to the divide (iv-b), is not built now.
- R14: `ChannelAt` is the only reader, so the carve, the owner rule, the coverage and the maps follow from one change. The stored profile, the nodes and the meander do not change, so there is no rebake.

FILES/SYMBOLS
- scripts/Terrain/Hydrology/RiverGraphBuilder.cs (`RiverNetwork.ChannelAt` :198, a new `IsSource(stem)`, a test hook); RiverHydraulics.cs (`HeadTaperMeters`); HydrologyVersions.cs.
- tools/RiverCarveInvariantsTest.cs (a "heads" part); tools/RiverFarWaterFractionTest.cs (conservation over head pieces); tools/DrainageBakeReportTool.Terminals.cs (the heads line).

REQUIRED CHANGES
1. `RiverHydraulics.HeadTaperMeters = 1500d`, marked provisional until the user's answer.
2. `RiverNetwork.IsSource(stem)`: the first reach has `UpstreamLake < 0`. Lake outlets, in-lake stems and overflow outlets are not sources.
3. `ChannelAt`, after the span blend, on a source stem at `s < T`, `T = min(HeadTaperMeters, stem length)`: apply RS4 (depth, half width, then bank width, edge depth and floodplain from the tapered values through the same statics that `RiverSpanTable.Create` uses). Outside that range the result is bit-equal to before.
4. Test hook `internal double HeadTaperOverride` on the network (negative: unused; 0 switches the taper off) for the negative control.
5. The `--terminals` heads line prints the width and depth at 0, 150, 750 and 1,500 m of the median source.
6. `CarveOperator` + 1.

DO-NOT-CHANGE CONSTRAINTS
- Generation 1 worlds must stay identical: `world_generation_fingerprint_test` must pass unchanged.
- `DrainageAlgorithm` and `GraphFormat` are unchanged. Nodes, knots, levels, spans and the meander are untouched; `PathChannelAt` and `PathGeometry` are untouched.
- Stems that are not sources, and sources beyond T, are bit-equal to before.
- The valley side slope is not tapered. `LateralReach` and `StemLateralReach` stay valid bounds (the taper only narrows).
- No minimum width, depth or coverage at the head. No morph, fade or alpha on the water.

VALIDATION STEPS
1. `river_carve_invariants_test`, new "heads" part, on 200 source stems of the strip graphs:
   - at `s = 0` the width and depth are 0 within 1e-6; at `s >= T` they equal the values with the hook off within 1e-9;
   - width and depth do not decrease along `[0, T]` (sampled every 10 m), apart from the 200 m blend into a narrower second span, printed apart;
   - `U = Q_t / (w d)` with the factors of RS4 never exceeds the span's speed;
   - the wet width across the channel at `s = T / 10` and `T / 2`, sampled every 0.25 m at footprint 0, is within the larger of 1 m and 10 % of the tapered width, at 99.5 %;
   - no NaN in `Evaluate` on a 1 m lattice over the first 100 m of 50 heads;
   - the check can fail: with the hook at 0 the first line fails.
2. `river_far_water_fraction_test`: conservation within 5 % at spacings 8, 32 and 256 on windows that contain a head.
3. The operator cost of `river_carve_invariants_test` stays under the 3.0 us tripwire; print the cost on head sections apart.
4. `.\tools\run_tests.ps1 -Tests river_carve_invariants_test,river_far_water_fraction_test,proxy_river_true_width_test,inland_water_sampler_test,world_generation_fingerprint_test -Label rivers`; `.\tools\run_tests.ps1 -Tests inland_water_render_gpu_test,river_lod_consistency_gpu_test -Label landscape_gpu` (`-DryRun` first).
5. **Pictures for the user** (this is the decision point): the head nearest ARC 23,415.07 km, ACROSS 195.11 km on seed 876374626 (still `d5_head_stem9255` of T2R-6c is the before picture), top down from 600 m and from 3 km, and one view from 20 m above the ground looking downstream from the head. Use `tools/river_site_capture.ps1` with a places file in the format of `.godot/captures/landscape/t2r/dead_ends/places.json`; no view from 8 km (capture notes of T2R-6c). Captures under `.godot/captures/landscape/t2r/heads/`; delete raw frames; `tools/prune_test_output.ps1 -Apply`.

STOP RULE
3 periods or 5 attempts.

ESCALATION CONDITIONS
- The cost tripwire breaks. Report the numbers; the allowed fix is a table of the two power factors, not a shorter taper.
- The head shows separate wet patches beyond the first 50 m (F41). Report; do not add a minimum width.
- A visible step in the valley floor where the taper ends, or at the blend into the second span.
- The user asks for another length or shape: that is a new constant and one more `CarveOperator` bump, by a follow-up order.

### T2R-22. Overflow rivers and the outlet into a passing river

ROLE: implementer
SIZE: L (two parts, one commit; a review checkpoint after Part A)
DEPENDS ON: C2b. Spec RS1.

OBJECTIVE
Inside a closed basin, a pool that is full to its saddle has an outlet over the saddle, and a river carries its surplus to the pool that takes it. A full pool too shallow for a lake becomes plain river. A lake that a river ends in and that spills into a passing river is joined to it. No river ends in a pond that passes most of its water on unseen, and none ends on a dry pan that is merely full.

CONTEXT / ROOT CAUSE
- User decision 1. T2R-6c finding 1 and F48: 292 to 387 lakes per ring hand their surplus over a saddle with no channel, at the end of 325 to 434 stems; 14 to 17 % of the discharge that stems bring to closed lakes leaves unseen. The 21 to 23 Dead stems per ring are the same case with no room for a whole-metre level. Finding 4: 19 to 35 spilling lakes per ring are not joined to the river they spill into.
- R1 to R5. The hand-over is arithmetic only. The arithmetic stays as it is; this order gives it links, accumulation and stems.
- S8b's sentence "Balance lakes have no outlet" and its line for T3 ("closed lakes with inlets and no outlet") no longer hold for full pools (see "Notes for T3 and T4").

FILES/SYMBOLS
- Part A: scripts/Terrain/Hydrology/DrainageLakeBalance.cs (`BasinTree`, `BuildTree`, `Resolve`, `Run`, `DrainageBasinInfo`); DrainageRouting.cs (`DrainageRouted`, `CountBasinCrossings`); DrainageBakeReport.cs; HydrologyVersions.cs. New: tools/DrainageOverflowTest.cs and tools/drainage_overflow_test.gd.
- Part B: scripts/Terrain/Hydrology/RiverGraphBuilder.cs (`Continues` :605, terminals :760-779, `RiverBuildReport`); RiverGraph.cs (the outlet rule :418-427); RiverLevel.cs (the hold :332-350, only if validation requires it); DrainageMapWriter.cs and RiverMapWriter.cs (no new colour; the rivers draw themselves); tools/DrainageGraphTest.cs; tools/DrainageBakeReportTool.Terminals.cs.

REQUIRED CHANGES
Part A: the bake.
1. **Measure first.** One whole-ring report on seed 1337 with `--terminals --part-hashes=<file>`, before any change. Keep the terminals table and the part hashes of lake runs, basins and dry runs.
2. `BasinTree.SaddleCell` (the cell whose insertion created each merge node, -1 for a leaf).
3. In the fill pass, a `handover` value per node: add the surplus at each of the two pin sites, clear it wherever the pins are cleared (RS1 "Hand-over records"). The pass is otherwise unchanged.
4. After the pass, in the closed branch only, the overflows of RS1: links inside each full pool, the link over the saddle, and one accumulation pass over the basin along the final links.
5. Report: the lines of RS1 "Report".
6. `DrainageAlgorithm` + 1.

Checkpoint: hand back the fixture results and the whole-ring report of Part A. The primary or the lead reviews them before Part B starts.

Part B: the builder and the graph.
7. `Continues` accepts a next cell that is any channel cell, not only a stem head. The drowned channel of a lake that a river ends in then runs through the exit and joins the passing river as a confluence. The lead rule (:639) is not changed: a lake with no inlet river gets no new lead.
8. The outlet rule of `RiverGraph.Build` also accepts a stem that starts in the lake, ends as a confluence, and whose receiving cell is not a flooded cell of that lake.
9. `drainage_graph_test`: the checks of validation 3. Replace every assertion that a closed lake has `OutletStem == -1` by the rule of RS1 "Graph".
10. The `--terminals` report prints the T2R-6c table again with the same classes, so before and after can be read side by side.

DO-NOT-CHANGE CONSTRAINTS
- Generation 1 worlds must stay identical: `world_generation_fingerprint_test` must pass unchanged.
- The fill arithmetic: every lake's level, wet cells and inflow, every basin's closed or spilling verdict, the dry floors and the mass balance are bit-equal to before. The part hashes of lake runs, basins and dry runs equal the ones of step 1.
- Spilling basins, conditioning, breaching, `E0` and the barrier rules are untouched.
- `GraphFormat` is unchanged: no new record (R5). `CarveOperator` is unchanged.
- No minimum lake size, no minimum discharge for an overflow river beyond the existing channel threshold, no synthetic inflow.

VALIDATION STEPS
1. New `drainage_overflow_test` (headless, synthetic domains through `BakeDomain`; each case has a hand-computed answer):
   - two pools, the lower one full: the full pool's cells link to the saddle cell, the saddle cell links to the receiving side, and following `down` from any cell of the full pool reaches a wet cell of the receiving lake; R on the first cell after the saddle is at least the hand-over (1e-9 relative);
   - a chain of three pools (A into B into C): both hand-overs have paths, and R on B's outlet is at least B's hand-over, which includes A's;
   - two pools that both fill and merge: one lake, no hand-over, no path between them;
   - a full pool with less than 1 m between floor and saddle: no lake, and the links run through it and over the saddle (today: a pan and a Dead end; the check must fail on the code before the change);
   - a saddle cell whose own descent points into the full pool: its link turns to the receiving side and no cycle exists in `down`;
   - lake levels, wet cells and the mass balance equal those of the same domain with overflows off (a test hook `DrainageLakeBalance.Overflows`, default true).
   Manifest: `"drainage_overflow_test": {"kind": "test", "mode": "headless", "tier": "gate", "tags": ["terrain", "hydrology"], "marker": "DRAINAGE_OVERFLOW", "expected_s": 20, "timeout_s": 180}`.
2. Part A, whole ring, seed 1337: the part hashes of step 1 are equal; the mass error is unchanged; the report's overflow lines are filled; `Conserved` is true.
3. `drainage_graph_test` on both strips:
   - no cycle in `down`; every channel ends in the sea, a lake, the Lung, a strip end or a dry pan, and no Dead stem ends in a pool that was full;
   - every full pool with a lake whose first cell after the saddle is a channel cell has `OutletStem >= 0`, the outlet's first reach has it as `UpstreamLake`, and the outlet hold equals the lake level up to the ring;
   - every stem that ends in such a lake reaches the outlet through the lake's drowned channel;
   - (c') every spilling lake with an inlet river whose exit flows into a channel cell has an outlet stem or a drowned channel that ends as a confluence there;
   - inlet and outlet records agree with the stems (the existing checks at :741 and :882).
4. `river_carve_invariants_test` (lake part, containment, the plug bars) and `river_danger_test` pass with their bars unchanged.
5. `.\tools\run_tests.ps1 -Tests drainage_overflow_test,drainage_graph_test,river_carve_invariants_test,river_danger_test,river_graph_cache_test,inland_water_sampler_test,world_generation_fingerprint_test -Label rivers` (`-DryRun` first). `drainage_bake_benchmark` once: the bake time rises by at most 10 % and the resident graph stays under 32 MiB.
6. Whole-ring `--terminals` reports on the three seeds. Expected: class (f) at 0, Dead at 0 to 2 per ring, class (c) at 0, 300 to 450 new outlet stems per ring.
7. For the user: the M1 map crop around stem 9943 (ARC 23,735.3 km, ACROSS 208.7 km, seed 876374626) before and after, and stills of places d1 to d4 and d6 of T2R-6c again (`tools/river_site_capture.ps1`, the existing `places.json`, views from 600 m and 3 km only). Captures under `.godot/captures/landscape/t2r/overflow/`; delete raw frames; `tools/prune_test_output.ps1 -Apply`.

STOP RULE
8 periods or 10 attempts: at most 4 periods for Part A.

ESCALATION CONDITIONS
- Any part hash of step 1 moves, or the mass error changes: the arithmetic was touched. Stop.
- A cycle in `down`, or a hand-over without a path.
- The saddle redirects of RS1 move more than 1 % of the total hand-over **(starting value)**. Report the count and the largest case; do not change the fill rule.
- More than 5 Dead stems remain on a ring. Report their pools.
- An overflow outlet cuts more than 5 m below its saddle, or the containment or plug bars of S8 break at an overflow lake.
- The (c') outlet shows a level break of more than 1 m between the lake and the passing river that the tail rule does not turn into chutes. Report the lake; do not write a new level rule.
- The resident graph exceeds 32 MiB.

### T2R-23. Site intakes switched on

ROLE: implementer
SIZE: M
DEPENDS ON: T2R-19, the user's answer on the keep-out, T2R-22.

OBJECTIVE
The game's bake treats the edge of every site keep-out as an outlet, at the keep-out the user chose. No lake stands above site ground and no plug holds water on a cell line beside a site. A river that reaches a keep-out ends there as a stem with terminal Intake, at a point the structure of T2R-24 can stand on.

CONTEXT / ROOT CAUSE
- User decision 4 and the table of T2R-19.
- T2R-19 left out the end node, the guard, the level, the file and the checks.

FILES/SYMBOLS
- scripts/Terrain/Hydrology/DrainageBake.cs (the defaults of `SiteIntakes` and `KeepOutMeters`); DrainageBarriers.cs (the constants and the summary text); RiverGraphBuilder.cs (the end node of an Intake stem, `GuardBarriers` :1099, `MetricsEnd` :96); RiverLevel.cs (no tail for Intake); RiverGraphFormat.cs (:212, the terminal range); RiverMapWriter.cs and DrainageMapWriter.cs (an intake mark); HydrologyVersions.cs.
- tools/DrainageGraphTest.cs (:936-951, :1040-1057); tools/RiverCarveInvariantsTest.cs (the plug escalation at :675); tools/DrainageSiteIntakeTest.cs; tools/DrainageBakeReportTool.Terminals.cs.

REQUIRED CHANGES
1. `SiteIntakes` is true by default, and `KeepOutMeters` is the chosen value. If it is not 650, `DrainageBarriers.LateralCapMeters` and the D2 text change with it.
2. The end of an Intake stem (RS2 "Stem end"): the end node keeps the guard's clearance from every Barrier and Intake cell, the guard treats Intake cells as barrier cells, and for these stems the end node is placed before the guard runs so that the guard does not need to move it.
3. Level: an Intake stem has no tail and no receiving level. Its profile runs to the end as it is.
4. `RiverGraphFormat`: the terminal byte accepts Intake. `GraphFormat` + 1.
5. Maps: an Intake end is drawn as a small square in the Lung colour; the legend says "Square: a river taken in by a site".
6. Report: the lines of RS2 "Report" are always printed.
7. `DrainageAlgorithm` + 1.

DO-NOT-CHANGE CONSTRAINTS
- Generation 1 worlds must stay identical: `world_generation_fingerprint_test` must pass unchanged. The site registries, the site shaper and the site identity are not touched.
- The operator is not edited. The S8 plug stays for the gaps between samples.
- The centreline rule of R10 holds at the chosen keep-out with its bar unchanged (influence radius + 300 m).
- No river is led around a site by a new rule, and none is shortened, tapered or faded before its intake in this order.
- `E0` is not recalibrated here, whatever the flooded share does.

VALIDATION STEPS
1. `drainage_site_intake_test` gains a strip part (CPU strip with sites, seed 1337):
   - no lake has a ring cell of kind Intake or Barrier whose lowest sample is below the lake level (hard limit 0; the check fails with the option off);
   - every Intake stem's end point and every sample of its curve keep 105 m from Barrier and Intake cells and influence radius + 300 m from every site;
   - no stem is Dead because of a site;
   - a file round trip keeps the terminal.
2. `drainage_graph_test`: "every channel ends in ..." adds "or an intake"; the centreline check at :1040 passes; the mass balance counts the intake outflow.
3. `river_carve_invariants_test`: the line "ESCALATION (T2R-8): a plug of ..." no longer prints, and the largest plug on the strips is at most 5 m **(starting value)**.
4. `.\tools\run_tests.ps1 -Tests drainage_site_intake_test,drainage_graph_test,river_carve_invariants_test,river_danger_test,river_graph_cache_test,world_generation_fingerprint_test -Label rivers` (`-DryRun` first).
5. Whole-ring `--terminals` reports on the three seeds: lakes above barrier ground 0 (were 705 to 754); the table of T2R-19 at the chosen keep-out is reproduced; flooded share and channel length printed against the reports of T2R-22.
6. For the user: stills of places e1, e2 and e3 of T2R-17 again (sites 441, 435 and 496; `tools/river_site_capture.ps1`, the existing places file, no 8 km view). Captures under `.godot/captures/landscape/t2r/intakes/`; delete raw frames; `tools/prune_test_output.ps1 -Apply`.

STOP RULE
5 periods or 6 attempts.

ESCALATION CONDITIONS
- The end-node rule cannot keep the clearance on some stem. Report the stems with their cells; do not weaken the guard.
- A lake still stands above site ground, or a straight bank is still visible in an after picture. Report the lake and the cells.
- The numbers differ from T2R-19's table by more than 5 % at the same keep-out.
- The flooded share leaves 6 to 10 %. Report it; the primary decides on `E0`.

### T2R-24. The intake structure: sites, streaming and a placeholder

ROLE: implementer
SIZE: M
DEPENDS ON: T2R-23. Spec RS5.

OBJECTIVE
Where a river ends at a site keep-out, a built intake stands across the end of its channel: a front wall with ports and a deck over the round end, sized from the river's section. In this order it is a procedural placeholder in the shared POI material. Final art comes later through the poi-asset-import conventions.

CONTEXT / ROOT CAUSE
- User decision 4: rivers end at a small built intake structure.
- Without it an Intake stem ends in a round pool on open ground, which is the dead end the batch removes.
- **Placeholder, not art.** No GLB exists. The placeholder is a generated mesh, so nothing goes through the import pipeline, but it follows the same material and registration rules (skill poi-asset-import, sections 3 and 4): one ShaderMaterial on `res://materials/ring_poi_architecture.gdshader`, `RingPoiHaze.RegisterTree`, `PoiBaseAmbientOcclusion.Apply`. The final asset replaces the mesh builder, not the site list or the streamer.

FILES/SYMBOLS
- New: scripts/poi/Intakes/RiverIntakeSites.cs (the list, from the graph), RiverIntakeMesh.cs (the placeholder mesh and its collision shapes), RiverIntakeStreamer.cs (slots near the player; the pattern of scripts/poi/Projectors/AtmosphereProjectorStreamer.cs and `EmitterPoiStreamerCore`).
- The game scene that holds the other streamers (one node, `AutoBind` and `LoadSavedDefaults` as the pattern has them).
- New tests: tools/RiverIntakeSitesTest.cs and tools/river_intake_sites_test.gd; tools/river_intake_gpu_test.gd.

REQUIRED CHANGES
1. `RiverIntakeSites.Build(RiverGraph)`: one site per stem with terminal Intake, in stem order: the end point of the displaced centreline, the downstream tangent, the level, and the half width, bank width and depth of `ChannelAt` at the end. Empty without a graph or without Intake stems. Immutable.
2. `RiverIntakeMesh.Build(site)`: the envelope of RS5. One surface, one material, under 600 triangles. Collision: box shapes for the wall, the deck and the wings.
3. `RiverIntakeStreamer`: 8 slots, the nearest sites within 6 km, threaded build, a slot is only a transform, a mesh and visibility. It registers haze and base ambient occlusion on build. No stand-in beyond 6 km, no setting, no menu entry.
4. The structure does not change the terrain, the water or the graph.

DO-NOT-CHANGE CONSTRAINTS
- Generation 1 worlds must stay identical: `world_generation_fingerprint_test` must pass unchanged. A world without `water.rivers` creates no intake and no node cost.
- No per-POI haze, shadow or lighting switch. No forked shader.
- No hydrology version changes.
- At most 0.05 ms of sustained main-thread work with all slots idle.

VALIDATION STEPS
1. New `river_intake_sites_test` (headless, the strip graph of `drainage_site_intake_test`):
   - one site per Intake stem; each lies within 1 m of the stem's end and keeps the clearances of T2R-23;
   - the list is the same for two builds and two worker counts;
   - the deck footprint covers the end cap: on a 1 m lattice, every wet River sample of the operator beyond the stem's end lies under the deck;
   - the material's shader path equals `res://materials/ring_poi_architecture.gdshader` (`PoiMaterialParams.IsPoiShader`);
   - a generation-1 sampler gives an empty list.
   Manifest: `"river_intake_sites_test": {"kind": "test", "mode": "headless", "tier": "gate", "tags": ["poi", "water", "hydrology"], "marker": "RIVER_INTAKE_SITES", "expected_s": 30, "timeout_s": 240}`.
2. New `river_intake_gpu_test` (gpu, the landscape harness with `--generation=1 --review-features=water.rivers`): teleports to the widest and the narrowest intake of the harness world, waits for the slot, and checks that the structure is visible, that no River water pixel shows beyond the wall in a top-down view, and that the player can stand on the deck. Manifest: `"river_intake_gpu_test": {"kind": "test", "mode": "gpu", "tier": "extended", "tags": ["poi", "water", "hydrology"], "marker": "RIVER_INTAKE_GPU", "expected_s": 120, "timeout_s": 600, "user_args": "--generation=1 --review-features=water.rivers"}`.
3. `.\tools\run_tests.ps1 -Tests river_intake_sites_test,poi_haze_registry_test,world_generation_fingerprint_test -Label rivers`; `.\tools\run_tests.ps1 -Tests river_intake_gpu_test,poi_haze_gpu_test -Label landscape_gpu` (`-DryRun` first). Check the tags against `tools/testing/validate_manifest.ps1`.
4. For the user: the two intakes of step 2, each from 150 m at eye height looking downstream and from 600 m top down, at noon, and one at dusk. The structure must take haze and cloud shadow like the ground beside it. Captures under `.godot/captures/landscape/t2r/intakes/`; delete raw frames; `tools/prune_test_output.ps1 -Apply`.

STOP RULE
5 periods or 6 attempts.

ESCALATION CONDITIONS
- The harness world has no Intake stem. Report it; the primary picks a seed or a place.
- The player's collision does not act on generated shapes. Report how the projector towers do it; do not write a second collision path.
- Water shows through or beyond the structure, or the terrain cuts through the deck. Report the section; do not edit the operator.
- The widest intake of a ring is so long that the placeholder reads as a dam. Send the picture; the look is the user's call.

### T2R-25. Rules review

ROLE: claude-lead
SIZE: S
DEPENDS ON: T2R-20 to T2R-24.

OBJECTIVE
The before and after numbers of the batch on three seeds, the pictures at the places of T2R-6c and T2R-17, and a list of what is still open, so the primary can run the gate suite and commit (C2c).

REQUIRED CHANGES
1. Whole-ring `--terminals` reports on seeds 876374626, 1337 and 4242 on the finished tree. One table with the T2R-6c classes before and after, the smallest sections, the heads line, the overflow lines and the intake lines.
2. **Fine-area balance, rule (i), measured again.** The share of river ends in a closed lake that shows under half the area its rivers need. It was 1.5 to 2.0 % before the batch. Ask the user only if it is above 3 %.
3. Review each order's diff against its constraints, in particular: the fill arithmetic of T2R-22, the absence of any minimum in T2R-20 and T2R-21, the clearances of T2R-23.
4. Stills: d1 to d6 and e1 to e3 once more on the finished tree, at most five in the report.
5. Write the results as a "Rules review" section at the end of this file, with the open questions that remain. Propose the text for T2.md and T3.md listed under "Notes for T3 and T4"; the primary applies it.

VALIDATION STEPS
- `.\tools\run_tests.ps1 -Tests drainage_graph_test,drainage_overflow_test,drainage_site_intake_test,river_carve_invariants_test,river_danger_test,river_intake_sites_test -Label rivers`. The gate suite is the primary's, at C2c.
- `tools/prune_test_output.ps1 -Apply`.

STOP RULE
3 periods. No code changes beyond report lines.

ESCALATION CONDITIONS
- Any class of dead end is above its value before the batch.
- A constraint of an order was broken and the tests did not see it.

## Spec

### RS1. Overflow rivers (T2R-22)

**Decision.** The water balance of S8b is unchanged. Where it hands a surplus over a saddle, the bake now gives that hand-over a path: links, accumulated runoff and area, and through them a river.

- **Hand-over records.** `handover[N]` is the surplus pool N has passed over its parent's saddle. It grows at each pin (the two sites of R1) and is cleared when N merges into its parent. After the pass, an **overflow** is a pool A that is still a top-level pool, is pinned, and has `handover[A] > 0`. Its saddle is that of its parent Q; the saddle cell is `SaddleCell[Q]`; the receiving cell `x_out` is the neighbour of the saddle cell on the other side that the arithmetic already used.
- **Order.** Overflows are applied in descending id of Q: the higher saddle first. A hand-over only moves to pools below its saddle, so this is upstream first, and chains (A into B into C) need no iteration.
- **The full pool.** Its level, wet cells and record are those of S8b (level `floor(saddle)`, wet cells below the level). New: every cell of A's subtree gets `down` = its parent in a breadth-first walk from the saddle cell over the subtree (8-neighbours through `GetNeighbors`, first in first out, directions in index order). This is what the flood gives a spilling lake (R3). A full pool with no room for a level has no lake and gets the same links: the river runs through it.
- **The saddle.** `down[saddle cell] = x_out`. From `x_out` on, the descent of S8b is unchanged, so the path ends in the wet cells of the receiving lake or at its pit. Where the saddle cell's own descent pointed somewhere else, its link moves (a **saddle redirect**; counted, with the runoff that moved).
- **Accumulation.** After all links of a basin are final, area and R are accumulated once along them, in topological order. Area accumulates plainly, so the full pool's whole catchment passes over the saddle, as a spilling basin's does. R accumulates plainly except at the step from the pool into the saddle cell, where the pool's cells pass `handover[A]` in total instead of their sum: the lake's evaporation stays in the lake. Runoff of the saddle cell that the arithmetic had already counted into the pool is not added a second time.
- **Invariants** (`drainage_overflow_test`, `drainage_graph_test`): no cycle; every overflow has a path to the receiving pool; R on the first cell after the saddle is at least the hand-over; lake levels, wet cells, basin verdicts and the mass balance are bit-equal to a bake without overflows.
- **Graph.** No new record. The builder sees a lake whose cells flow to an exit and treats it as it treats a spilling lake: the drowned channel of T2R-8b from each inlet to the exit, an outlet stem with the outlet hold, `OutletStem` set. A closed lake (`Basin >= 0`) therefore has an outlet exactly when it is an overflow and the first cell after its saddle is a channel cell (its catchment reaches the channel threshold). An overflow below the threshold has no river, as a small spilling lake has none.
- **What stays Dead.** A pool that is not full and has no room for a level, or an inflow under half a cell's evaporation: water that really ends on a pan. Expected 0 to 2 stems per ring.
- **(c')** A lake that a river ends in and whose exit flows into a channel cell: its drowned channel runs through the exit and joins that river as a confluence, with the outlet hold up to the lake's ring and the confluence tail below it. Lakes with no inlet river are unchanged.
- **Report.** Overflow lakes; full pools without a lake; total hand-over and its share of the discharge that stems bring to closed lakes; overflow rivers (stems and km); saddle redirects and their runoff; overflows below the channel threshold; Dead stems after overflow; (c') joins.
- **Look.** About 300 to 400 end-ponds per ring become lakes with a river out, the same number of short rivers appear between the lakes of one basin, and the lakes that receive the surplus are finally reached by the rivers that fill them.

### RS2. Site intakes and the keep-out (T2R-19, T2R-23)

**Decision.** The outer ring of every site keep-out is an outlet. Water that runs towards a site leaves the surface there. One rule for every barrier kind (emitter discs, Wall Engine stadiums, the Weather Lung ring), because the grid does not tell them apart (R7).

- **Intake cells.** After the barriers are rasterised, every Barrier cell with a geometric 8-neighbour of kind Land or StripOutlet becomes `Intake`. No Land cell then touches a Barrier cell. Interior barrier cells stay blocked. Lung cells are unchanged.
- **Flood and routing.** An Intake cell is an outlet at the height of its lowest sample, without routing noise. The flood starts from it as from the sea. A depression beside a site fills at most to that height, so no lake stands above the ground of a keep-out cell, and the Barrier cap of S8 (T2R-5c) has nothing left to act on there. Cells that drain to an Intake cell have terminal Intake.
- **Stem end.** A stem whose next cell is an Intake cell has terminal `Intake`. Its end node lies on the segment from the last land cell's node towards the Intake cell's lowest sample, at the last point that keeps 105 m from every Barrier and Intake cell rectangle; if the last land node is already inside that distance, the end node is that node moved out along the same line. The guard treats Intake cells as barrier cells. The end therefore lies on ground where rivers may run today, at least influence radius + keep-out + 105 m from the site centre. No tail; the level profile runs to the end.
- **Keep-out.** `clearance = KeepOutMeters + half a cell diagonal`. 650 m is today's rule. 300 m is the corridor radius alone (R11: expected to need no other change). 0 m would need the carve to fade out at the influence disc; it is measured for size only and not built without a design order.
- **Measurement table** (T2R-19; per seed, one column per keep-out and one for today's rule): barrier cells; intake cells; stems ending at an intake (count, share of all river ends; discharge p50, p90 and sum; width p50, p90 and max); sites with at least one such stem (of 601) and Wall Engine and Lung ends counted apart by nearest structure; lakes that spill straight into an intake cell; share of the ring's runoff that leaves through intakes, the sea and closed basins; channel length; stems ending in the sea; lakes and the flooded share of land; lakes above keep-out ground (must be 0 with intakes on); points of a centreline closer than influence radius + 300 m to a site (must be 0).
- **Report** (always, from T2R-23): intake cells; Intake stems and their discharge; the runoff share through intakes; lakes above keep-out ground.
- **For T3.** No fade-out of the flow before an intake. The exclusions of E4 already make the last 1,500 m calm or swift (R12).

### RS3. The channel law without floors (T2R-20)

**Decision.** S6 and T3 Spec E2 without their two floors:

- path law: `d = channel_depth * 0.27 * Q^0.4`, `w = Q / (U_0 * d)`;
- channel law: `d = channel_depth * Kd * Q^0.4`, `w = min(Q / (U_0 * d), Wmax)` with the deepening above `Wmax` as today;
- `Q = U w d` holds exactly; `Q = 0` gives `d = w = U = 0`.

Everything else in S6, E2 and E3 is unchanged. D6 then reads: "Plan 3.2 relations; the section is what discharge and gradient give. T3R-3 calibrated the constants." Without a cap acting, the law is a power law of the discharge: `d ~ Q^(2/5)`, `U ~ Q^(4/15)`, `w ~ Q^(1/3)`. RS4 uses this.

- **Coverage.** A river narrower than 8 m has no water geometry at the 4 m level (S13.2: `w >= 2 * fp`), so the 4 m level carries coverage: `UsesProxyLine(spacing) = spacing > 2`.
- **Gameplay.** The player walks in water up to 0.55 m, swims with the feet on the bed between 0.55 and 1.15 m, and floats above that (R14). The lifted spans are 0.43 to 1.17 m deep at the centre, so most of them are in the middle band: the swim motor is on, the bed is in reach, and the bank is a few steps away. Every wider river is unchanged. Whether the middle band should be walked instead is a controller question (open question 5).
- **Known.** At the 2 m level and in the near terrain a channel under about 3 m wide can break into separate wet patches (F41). After T2R-20 no mapped reach is that narrow at its median; after T2R-21 the first tens of metres of every head are.

### RS4. Tapered heads (T2R-21)

**Decision.** On a source stem the discharge is taken to grow from 0 at the head to the span's discharge at `T = min(1,500 m, stem length)`, and the section follows the law of RS3.

- `x = s / T`. The discharge factor is `g = x^(5/3)`: catchment area grows with stream length to the power 1 / 0.6 (Hack's law), compressed from the roughly 10 to 20 km a real headwater would need into T. The compression is the look choice; (iv-b) is the uncompressed version.
- With the power law of RS3: depth times `x^(2/3)`, width times `x^(5/9)`, speed times `x^(4/9)`. The three exponents add up to 5/3, so `Q = U w d` holds at every s. Where a speed or width cap acts on the span, the factors give a speed at or below the law's.
- Bank width, edge depth and floodplain follow from the tapered width and depth through the existing statics. The valley side slope, the level, the nodes and the meander are unchanged. The round end at `s = 0` has radius 0 plus the bank.
- Example, the median source (42 m, 1.6 m): 3 m wide at 15 m, 12 m wide and 0.35 m deep at 150 m, 29 m and 1.0 m at 750 m, full at 1,500 m.
- The owner rule is per point (R14), so a coarse level holds the head as water from where it is two spacings wide and shows the tint above that.
- **For T3.** On a source stem the speed factor `x^(4/9)` replaces the 200 m fade-in of E5. Lake outlet stems keep theirs.
- **Provisional:** T and the exponent, until the user has seen the pictures of T2R-21.

### RS5. The intake structure (T2R-24)

**Decision.** A built object in the POI family that covers the end of an Intake stem. Frame: origin at the stem's end on the displaced centreline, x downstream along the tangent, y to the left bank, z up; `a`, `b`, `d` and `L` are the half width, bank width, depth and level at the end.

- **Front wall** at `x = 0`, across the channel, from `y = -(a + b)` to `a + b`, from 0.5 m under the bed (`L - d - 0.5`) to 1.5 m above the level. Dark recessed ports on the upstream face between the bed and the level.
- **Deck** at the wall's crest over the half disc of radius `a + b + 2` beyond the wall: it covers the channel's round end, so no water shows behind the wall.
- **Wing walls** from both ends of the front wall 6 m upstream into the banks, falling to the bank top.
- The dimensions of the wall, the deck margin and the wings are design dimensions of a built object. Its width is the river's.
- **Material and registration:** the shared POI shader, haze and base ambient occlusion as every POI. It takes cloud shadows and lighting with no switch of its own.
- **Streaming:** 8 slots within 6 km. Beyond that the river is a tint and the structure is not drawn.
- **Final art** (not in this batch): a modular GLB under `assets/poi/river_intake/` (a centre bay and a wing, repeated across the width), brought in with the poi-asset-import skill: import settings before the first import, materials converted to the shared shader, collision under `collision/`, the streamer unchanged.

## Notes for T3 and T4 (for the primary to apply; not edited from here)

- **T2.md S8b**, "Stems and terminals" and "For later phases": a closed lake can have an outlet (RS1). T3 receives closed lakes with inlets and, where they overflow, an outlet. D6 and S6 lose the floors (RS3). D2 gains the Intake outlet and, if chosen, the new keep-out (RS2). The "Order and parallelism" table and Phase acceptance item 8 gain T2R-19 to T2R-25 and C2c.
- **T3.md Spec E2**: `d = channel_depth Kd Q^0.4` without `max(1.2, ...)`; width without the 8 m floor. The T3R-3 constraint "T2's floors" and the `river_danger_test` bars "8 to 400 m, at least 1.2 m" become "(0, 400], above 0". The E3 rows are re-recorded from T2R-20's report.
- **T3.md Spec E5** (T3R-4 is not built yet): the head factor of RS4 on source stems; no fade-out before an intake; a new terminal `RiverTerminal.Intake`; the outlet rules of a spilling lake apply to an overflow lake.
- **T3.md Spec E6**: wading is "depth above 0 and below the swim entry depth". With RS3 there is now mapped river in that band.
- **T4**: overflow rivers cross dry basin floor, as channels already could (S8b). No new query.

## Open questions for the user

No minimum, floor, widening or boost is proposed anywhere in this file.

1. **Keep-out (after T2R-19's table).** Which width: 650 m (today), 300 m, or a design order for 0 m? The table also shows how much of the ring's river water ends at sites. If that share is large, is it still what you want?
2. **Which structures take water in.** The rule as written applies to emitter sites, Wall Engine forecourts and the Weather Lung alike, because the same straight banks stand at all three. Should the Wall Engines and the Lung get the same intake, or be left as they are until you have seen the emitters?
3. **The intake's look.** The placeholder is a front wall with ports and a deck over the river's end, as wide as the river. Wide rivers give long structures: T2R-19 prints the widths. Is that the right idea, and do you supply the final model or should a later order build it? Beyond 6 km the structure is not drawn. Is that acceptable?
4. **Head taper (after T2R-21's pictures).** Is 1.5 km right, and does the head read as a spring? Longer makes it gentler; shorter makes it blunter.
5. **Wading.** With D6 lifted, water between 0.55 and 1.15 m deep still switches the controller to swimming, with the feet on the bed. Leave that, or raise the swim entry depth so such rivers are walked? That would also change ocean and lake shores.
6. **Existing clamps that stay.** None of them makes water where there is none, but each is a limit on a river's shape: bank width at least 2 m and at most 12 m; floodplain at least 10 m and at most 120 m beyond the bank; meander wavelength at least 300 m; meander amplitude at a quarter for streams under 14 m; river width at most 400 m with the depth raised to carry the discharge (T3 calibration); gradient at least 1e-4. Should any of them go?
7. **Ponds beside rivers.** (c') joins a lake to a passing river only where a river ends in that lake. A pond with no mapped inlet that spills into a river stays without a drawn outlet, since its outflow is below the mapped threshold. Acceptable?

Not asked, only reported: the flooded share of land will move when the dammed lakes go (they are 4.5 % of the lake area on the play-test seed); `E0` is left alone unless the share leaves 6 to 10 %. Zero to two rivers per ring will still end on a dry pan because their water really evaporates there.

## Phase acceptance (additions to T2.md)

9. The rule orders of 2026-11-13 are done: T2R-19 to T2R-24 pass their validation, including `drainage_overflow_test`, `drainage_site_intake_test` and `river_intake_sites_test` (headless). T2R-25 has reported: on three seeds no river ends in a full pool without an outlet, no lake stands above keep-out ground, no section has a floor, and every source tapers. The user has answered open questions 1 and 4, and the head taper constant is no longer marked provisional.

## User answers 2026-11-13

1. **Which structures take water in:** all keep-out sites (emitters, Wall Engine forecourts, Weather Lung ring), as the rule is written, "for now, and we will see the results". Revisit at T2R-25 with pictures.
2. **Wading:** raise the swim entry so shallow rivers are walked (around chest depth, ~1.0-1.2 m; exact value set in T2R-20 against the controller). Applies to all water, including ocean and lake shores. T2R-20 adds this to its scope and to `inland_swim_smoke_gpu_test` (wade pose walks, deep pose swims).
3. **Shape clamps:** keep for now; the lead reviews each against real rivers in T2R-25 and brings proposals.
4. **Intake look:** the procedural placeholder is fine; final art later as a model.
Still open: the keep-out distance (after T2R-19's table) and the head taper length (after T2R-21's pictures). Ponds with no mapped inlet staying without an outlet (question 7) was not asked; treat as accepted unless T2R-25 shows it matters.

## User decision 2026-11-13: sites remove water instead of taking it in (supersedes the T2R-23/T2R-24 direction)

After the T2R-19 table (all ~705-755 dammed lakes per ring go at any keep-out; ~210-230 rivers per ring would end at intakes), the user chose a simpler rule:
- Rivers may run close to a site (the keep-out shrinks; the lead proposes the distance, near 0).
- A river whose path intersects a site is removed, **whole river branch** from its source down. Rivers flowing into that branch upstream stay unless they also hit the site.
- A lake that intersects a site is removed together with its incoming and outgoing rivers.
- **No intake structures**: T2R-24 is dropped. T2R-23 is to be rewritten by the lead for the removal rule (what happens to the removed water in the balance, how valleys look without the river, how the T2R-19 option code is reused or retired, and the R10/R11 guard checks with the smaller keep-out).

## Water look and instance-slot follow-ups (lead investigation 2026-11-13; run after T2R-12d)

Evidence: `.godot/captures/landscape/t2r/cream_and_instances/`. Root causes:
- **Cream rivers.** Inland water uses the clear-ocean extinction, so a 2.5 m river passes 97-100 % of the bed. The wet bed is full-Desert sand. Near the camera the bed only looks dark because the water mesh casts an opaque sun shadow on it, which ends at the 400 m near sun map: teal below ~400 m, cream above.
- **Instance-uniform overflow.** Fixed for now in 3344f78 (buffer 131072 -> 524288).

User decisions 2026-11-13:
1. **Inland clarity:** separate per-metre extinction for rivers and lakes in `ring_ocean_proxy.gdshader` (selected by `inland_weight`; ocean weight 0). Starting visibility about 2 m for rivers and 6 m for lakes, tuned on pictures. Two Water-tab rows (through `RingWaterModel.ApplyMaterial`, `RingGraphicsQualityController`, `GameplayWaterDefaults`). Changes legacy worlds' inland water too; accepted.
2. **No self-shadow from water, ocean included:** water shadow casters (`ConfigureOpaqueShadowCaster`, `ConfigureOceanShadowCaster`) stop shadowing their own bed, for lakes, rivers and the ocean. Generation-1 coast pictures change; accepted.
3. **River and Lake colours become the body colour** of near water (today 20 % of the tint).
- Test: `inland_water_render_gpu_test` checks that the river centre patch matches within tolerance at 300 m and 500 m above (fails today).
- Out of scope: the 16 m sand blocks under water (deferred by the user to the biomes work).

Instance-slot follow-up (structural):
- In `RingTerrainProxySector.cs` / `.VertexTable.cs`, free the water child, the water caster and the opaque caster when a sector has no water, stops casting or is recycled.
- Cap the node pool per level (start at 32).
- Show instance blocks used / capacity in diagnostics.
- `tools/testing/TestRunner.psm1` fails a test whose log has "Too many instances using shader instance variables".

Also noted: the 2 m and 4 m proxy levels overlap (dithered in the levels debug view); not investigated.
