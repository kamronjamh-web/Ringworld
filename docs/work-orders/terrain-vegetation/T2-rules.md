# T2 rules. River ends, heads, the minimum river and emitter intakes: work orders (2026-11-13)

Parent: docs/work-orders/terrain-vegetation/T2.md. This file holds the rule orders that follow the T2R-6c and T2R-17 investigations and the user's answers of 2026-11-13. It is a separate file because T2.md was being edited while it was written.
Prerequisite reading: T2.md Phase rules, facts F39 to F50, Spec S5 to S8b and S13, and the Review notes "T2R-6c and T2R-17 results (lead, 2026-11-13)"; docs/work-orders/terrain-vegetation/T3.md Spec E1 to E6; TEMPLATE.md.
Status: written by the temporary lead (Fable) on 2026-11-13. Work orders only: no code was changed and nothing was run.
Revised by the temporary lead on 2026-11-13, after T2R-19 (commit 2f48fcb) and the user's decision "sites remove water instead of taking it in" (end of this file): T2R-23 and Spec RS2 are rewritten, T2R-24 and Spec RS5 are dropped, facts R15 to R21 and questions RQ1 to RQ5 are added, and the execution order is updated. No code was changed and nothing was run for the revision.
Depends on: T2R-12c and T2R-12d and commit point C2b, except T2R-19 (done) and T2R-23 Part A, which change nothing in production.
Freeze: none by itself; part of the "rivers" freeze with T3.

Ids in this file: orders T2R-19 to T2R-25 (free in T2.md when this was written; T2R-24 is dropped and its id is not reused), facts R1 to R36 (R22 to R30 are in the T2R-22 Part A review, R31 to R36 in the Part B review), unverified RU1 to RU4, spec RS1 to RS5 (RS5 is dropped), questions RQ1 to RQ7. T2.md's F, S and D numbers are cited as they are.

## User decisions (2026-11-13)

1. **Dead ends.** Build rule (vi), overflow rivers, together with (c'), the outlet into a passing river. (T2R-22.)
2. **Heads.** Taper them, rule (iv), starting with about 1.5 km. The user sees a picture before the length is final. (T2R-21.)
3. **The D6 minimum river** (8 m wide, 1.2 m deep). Lift it. Small rivers get their true section and become wadeable. (T2R-20.)
4. **Emitters.** Option (A): a site takes in the water that runs towards it, and a river that reaches a site ends at a small built intake structure. The narrower keep-out (E) is measured first, so that fewer rivers are cut off. (T2R-19, T2R-23, T2R-24.) **Superseded on 2026-11-13**, after T2R-19's table, by "User decision 2026-11-13: sites remove water instead of taking it in" at the end of this file: a site removes the water that reaches it, nothing is built there, T2R-23 is rewritten and T2R-24 is dropped.

The realism rule of 2026-11-13 applies to every order here: no floor, minimum, widening or boost. Where an existing clamp is touched or left in place, it is listed under "Open questions for the user".

## Phase rules

The Phase rules of T2.md apply unchanged (executors, production first, construct do not fit, sampling with tolerances, the STOP RULE, testing cadence, pictures). In addition:

- **One builder at a time.** These orders edit the bake, the builder and the operator. Serialise C# edits and builds with T2R-12c, T2R-12d and T2R-18.
- **Versions.** Every order that changes bake output bumps `HydrologyVersions.DrainageAlgorithm` by one; every order that changes operator output bumps `CarveOperator` by one. A graph file is never rebaked while it is valid, and its validity is the version, so a shared bump would let a test profile keep a stale graph between two orders. The saving is made where it costs something: the user's world is rebuilt once, at commit point C2c, after the whole batch.
- **Generation 1.** Every change sits behind `water.rivers`. `world_generation_fingerprint_test` must pass unchanged in every order.
- **No menu entries.** Bake and carve rules are not live tunables. No recipe field is added in this batch.

## Facts

Read from the source on 2026-11-13 (working tree on `b55b76d` with T2R-12c in progress: `DrainageAlgorithm` 6, `CarveOperator` 7, `GraphFormat` 4). Line numbers drift; re-check before editing.

**Overflow (T2R-22)**

- R1. `DrainageLakeBalance.Resolve` (scripts/Terrain/Hydrology/DrainageLakeBalance.cs:226-459). The fill pass is at :286-335. A full pool is pinned at its parent's saddle (`pinned[x] = true`, :316 and :332) and its surplus goes to `PoolOf(LeafOf(nbCell))`, where `nbCell` is `NbA` or `NbB` of the parent: the lowest-key neighbour of the saddle cell on the other side. A merge clears the pins (:311, :334). The tree stores the saddle height (`Saddle`) and the two neighbour cells, not the saddle cell itself (`BuildTree`, :122).
- R2. In a closed basin `down`, `Accumulated` and `Area` come from the descent alone (:381-389). A pinned pool gets `raw = top` (:402) and the level `floor(top)`. A pool with `floor(top) < floor(low) + 1` holds no lake and is counted as a pan (:422): this is where 21 to 23 of the Dead stems per ring come from (F48).
- R3. A spilling basin keeps the links of the flood inside (`OldDown`, :359-370) and passes its outflow and its whole area to the exit (:371-372). This is the construction an overflow lake needs on a smaller scale.
- R4. The builder (`RiverGraphBuilder.Build`, scripts/Terrain/Hydrology/RiverGraphBuilder.cs). A channel cell is land, visited, not a lake cell, with `down >= 0` and area at or above the threshold (:581). `Continues` (:605-609) marks a lake's exit cell only when the flow goes on into a stem head or an outlet; into a cell that already carries a river it stops one cell short. The lead rule skips an outlet cell that has a river upstream (:639). These two lines are case (c) of T2R-6c. Terminals are set at :760-779; anything that is not a confluence, lake, sea, Lung or strip end is Dead.
- R5. The outlet of a lake is the first stem whose first reach has the lake as `UpstreamLake`, is not Dead and whose last cell is not a flooded cell of that lake (`RiverGraph.Build`, RiverGraph.cs:418-427). The outlet hold reads the same field (RiverLevel.cs:332, :350; RiverCarveOperator.cs:624). None of them reads `RiverLake.Basin`, so a closed lake with an outlet needs no new record.
- R6. `drainage_graph_test` has synthetic closed basins with saddles and pans (tools/DrainageGraphTest.cs:200, :258-261) and checks that Dead stems end at a dry pan (:936-945).

**Site intakes and the keep-out (T2R-19; read before the removal decision, see R15 to R21 for the state after it)**

- R7. `DrainageBarriers.Rasterise` (scripts/Terrain/Hydrology/DrainageBarriers.cs:18-62). The clearance is `CorridorRadiusMeters` 300 + `LateralCapMeters` 350 + half a cell diagonal (about 707 m), added to the influence radius of every emitter site, to the Wall Engine stadium and to the Weather Lung rectangle. The grid does not record which structure a barrier cell belongs to.
- R8. The Weather Lung rectangle is kind Lung, an outlet, but the same function surrounds it with barrier cells. Nothing can route into it today (D2: "an isolated receiving body with no synthetic inflow"). The Lung is therefore a precedent for the idea, not for the code.
- R9. Barrier cells are blocked in `DrainageDomain.GetNeighbors` and `NeighborIndex` (DrainageDomain.cs:44-77); outlets are Ocean, Lung and StripOutlet (`IsOutlet`, :42). The flood starts from the outlets at their own height (`DrainageConditioning.Flood`, DrainageConditioning.cs:192-235). Routing noise is added to Land cells only (`DrainageBake.BuildRoute`, DrainageBake.cs:142-161).
- R10. The barrier guard (`GuardBarriers`, RiverGraphBuilder.cs:1099-1166) keeps every sample of the node curve 105 m from barrier cells, so the meander (at most 100 m) never enters one. The last node of a stem never moves. `drainage_graph_test` checks that no point of a displaced centreline lies in a barrier cell or closer than influence radius + 300 m to an emitter site (tools/DrainageGraphTest.cs:1040-1057).
- R11. **Derived, not measured.** A point outside every barrier cell is at least influence radius + 650 m from the site centre. With the guard of R10 the centreline keeps that distance, and the carve reaches 300 m from it (the fade end). The 350 m lateral-cap term of the clearance was written before the guard existed and now seems to be counted twice. If so, a keep-out of 300 m plus half a diagonal needs no change to the builder or the operator. T2R-19 measures it. **After T2R-19:** neither confirmed nor refuted, because the end nodes of Intake stems hid the answer (R17). T2R-23 Part A measures it again without those stems.
- R12. Dangerous water is already excluded near sites: a span within 1,500 m of an emitter disc, a Wall Engine stadium or the Lung footprint that would be fierce or torrent becomes a landing (`RiverExclusions.SiteMarginMeters`, scripts/Terrain/Hydrology/RiverSpanBuilder.cs:16). A river that arrives at an intake is calm or swift.

**Site removal (T2R-23).** Read on 2026-11-13 from the working tree on `90392b9` with T2R-20 in progress (`DrainageAlgorithm` 7, `CarveOperator` 8, `GraphFormat` 4).

- R15. T2R-19 is in the tree (commit 2f48fcb), off by default. `DrainageCellKind.Intake` (DrainageDomain.cs:7) is an outlet (`IsOutlet`, :42) and is not blocked. `DrainageBarriers.MarkIntakes` (DrainageBarriers.cs:80) turns every Barrier cell with a Land or StripOutlet 8-neighbour into Intake, so no Land cell touches a Barrier cell. Sea cells inside a keep-out are Barrier cells too (`Mark`, :105, protects only Lung cells). Options: `DrainageBakeOptions.SiteIntakes` and `KeepOutMeters` (DrainageBake.cs:18, :20). The builder gives a stem whose next cell is an Intake cell `RiverTerminal.Intake` (RiverGraphBuilder.cs:780); `Continues` and the lead rule treat Intake like the sea (:610, :650); the guard already treats Intake cells as barrier cells (:1115). The production callers pass no options (RiverGraphStore.cs:250-252, DrainagePreviewBridge.cs:44), so the option defaults are the production rule.
- R16. **Measured by T2R-19** (tables: `.godot/test_runs/t2r19_tables.md`; seeds 876374626 / 1337 / 4242; whole ring; keep-out 650, 300 and 0 m).
  - Lakes above keep-out ground: 735 / 755 / 705 with the option off, 0 at every keep-out.
  - Stems that end at an edge cell: 227, 224, 226 / 225, 219, 212 / 223, 215, 207 (keep-out 650, 300, 0). That is 3.9 to 4.5 % of river ends. Discharge p50 76 to 101 m^3/s, p90 342 to 526 m^3/s; width p50 50 to 59 m, p90 164 to 194 m, largest 351 to 391 m. They end at 128 to 154 of 595 emitter sites, 0 to 4 at the Lung, none at a Wall Engine.
  - The count does not follow the keep-out: 300 against 0 differs by -2 to +8 stems per ring.
  - Lakes that spill straight into an edge cell: 85 to 105 per ring (94 / 94 / 93 at 300).
  - Runoff that leaves at sites: 1.9 to 2.8 % of the ring's runoff; the mass error stays at or below 5.4e-14.
  - Flooded share of land at 300: 8.34 / 9.43 / 9.26 % (option off: 8.62 / 9.65 / 9.46 %). River length at 300: 0.5 to 0.7 % less than with the option off. Keep-out cells at 300: about 4 % fewer than at 650.
- R17. **The R10 escalation of T2R-19 and its cause.** The end node of a stem that is not a confluence is the node of the cell after its last cell (`endCell = down[last]`, RiverGraphBuilder.cs:880, :898). For an Intake stem that is the lowest sample of an Intake cell, anywhere inside a keep-out cell, and the guard never moves a last node (:1157). So Intake stems ran 542 to 1,216 m into influence shapes, and 533 to 652 centreline samples per ring lay closer than influence radius + 300 m at keep-out 300. With the last 1,500 m of Intake stems left out, the samples closer than influence radius + 300 m were 0 / 21 / 0 at 650, 10 / 2 / 0 at 300 (one stem each) and 17 / 70 / 26 at 0 (4 to 6 stems). The stems behind those remaining samples were not named. **Derived, not measured:** a last node in a Land cell is at least influence radius + keep-out from the site, and the meander is not tapered at a stem end that is not a confluence (`Meander`, :321). At keep-out 300 a stem that ends in a lake or the sea in a cell beside the keep-out can therefore come up to 100 m inside the bar, with no Intake stem involved.
- R18. `RiverGraphFormat` rejects a terminal above `Dead` (RiverGraphFormat.cs:212), so no graph file can hold an Intake stem. Readers of the terminal: the level build gives a receiving level and a tail only to Sea, Lake and Confluence (RiverLevel.cs:381, :478-486); the outlet rule and the outlet hold exclude only Dead (RiverGraph.cs:423, RiverLevel.cs:332, :350, RiverCarveOperator.cs:624); `MetricsEnd` (RiverGraphBuilder.cs:94).
- R19. A lake's id in the graph is its id in the field (`RiverGraph.Build`, RiverGraph.cs:354-391: records and runs are filled through `f.LakeId`). Taking a lake out after the bake would mean remapping every id; not creating it in the balance needs nothing downstream. A spilling basin becomes a lake in one place, the spilling branch of `DrainageLakeBalance.Resolve` (DrainageLakeBalance.cs:347-379), which is handed the exit cell (`exit`, the flood parent of the basin's first cell).
- R20. **Site shapes are large.** An emitter's influence radius is its skirt: 3,000 m around a pad of 350 m, 6,000 m around 1,000 m for the main emitter, more where a site is raised (EmitterSiteRecipe.cs:158-187, EmitterSiteBuilder.cs:204). A Wall Engine's is 8,400 m around a forecourt line of 20 km (ClimateMachineRecipe.cs:84, :106). The Weather Lung's bound lies 6,000 m beyond its lip (WeatherLungRecipe.cs:88-113). The site shaper runs after the river operator (S7 "Model") and blends the ground towards the site with a weight that reaches 0 at the influence radius (EmitterSiteTerrainShaper.cs:147). The keep-out is added to these radii: today the nearest stem node is 866 to 887 m from an emitter disc (T2R-17), about 3.9 km from the centre of a regular emitter.
- R21. Site checks that exist: `drainage_graph_test` tests that no centreline point lies in a Barrier cell (Intake cells are not tested) and that none is closer than influence radius + 300 m to an **emitter** site (tools/DrainageGraphTest.cs:1040-1062); Wall Engines and the Lung are covered only through their Barrier cells. The Dead check is at :936-945 and the end-of-chain check at :951. RU1 stands: no check of water or plug bands against site shapes was found.

**The D6 minimum and the heads (T2R-20, T2R-21)**

- R13. The floors exist twice. The path law, `RiverHydraulics.PathSection` (RiverHydraulics.cs:19-20, :45, :49), feeds the wander, the meander, the bank-line stations of the level build and the confluence tails. The channel law, `RiverDanger.Channel` with `RiverDangerConstants.MinDepth` and `MinWidth` (RiverDanger.cs:13-14, :57, :61), gives the carved and drawn section through `RiverSpanTable.Create` (RiverSpans.cs:155-176) and decides the landings, whose factors are stored in the graph file. Other readers: `RiverFootprint.UsesProxyLine` (RiverFootprint.cs:23), `drainage_graph_test` (:1026-1028), `river_danger_test` (tools/RiverDangerTest.cs:110-117), the `--terminals` report (tools/DrainageBakeReportTool.Terminals.cs:487-549), T3.md Spec E2 and the T3R-3 constraint "T2's floors".
- R14. Channel and valley values have one reader, `RiverNetwork.ChannelAt` (RiverGraphBuilder.cs:198-221). The operator (RiverCarveOperator.cs:167), the owner rule (`ValleyOnly(fp, h.A)` per point, :376), the coverage splat (RiverGraph.cs:198-331) and the maps go through it. The first span of a stem does not blend. The player swims when the water stands more than `SwimEnterDepthMeters` 0.55 m over the feet and floats with the feet `SwimSurfaceFloatDepthMeters` 1.15 m under the surface (scripts/Voxel/Integration/RingVoxelExplorerController.cs:79-81, :1218, :1377). `InlandSwimProbe.FindPoints` looks for a river point at least 1.5 m deep (tools/InlandSwimProbe.cs:93).

**Unverified (check before relying on them)**

- RU1. S8 names a bake check that no plug band reaches a site influence disc. No such check was found in the bake; the source has the centreline check of R10 and the clearance lines of the `--terminals` report. T2R-19 reports what exists.
- RU2. How many rivers end at an intake, and how much of the ring's runoff leaves that way. Today 24 to 36 dammed lakes per ring are ones a mapped river ends in; the number after the dams are gone is only known after a bake (T2R-19). **Answered by T2R-19: R16.** Still unknown, and measured by T2R-23 Part A: the length of those rivers, how many tributaries they have, and how many lakes lie beside a keep-out without spilling into it.
- RU3. How the class shares of T3 Spec E3 move when the floors go (T2R-20 prints them).
- RU4. The cost of the taper in `ChannelAt`, which is on the operator's hot path (T2R-21 measures it against the 3.0 us tripwire).

## Order and parallelism

| WO | Role | Size | Depends on | Can run alongside |
|---|---|---|---|---|
| T2R-19 Site intakes in the bake behind an option; the keep-out measurement | implementer | M | none | **done** (commit 2f48fcb; R15 to R17) |
| T2R-20 The D6 minimum lifted | implementer | M | T2R-12c, C2b | T2R-23 Part A (serialise builds) |
| T2R-21 Tapered heads | implementer | M | T2R-20 | T2R-22 Part A, T2R-23 Part A (serialise builds) |
| T2R-22 Overflow rivers and the outlet into a passing river | implementer | L (two parts) | C2b; T2R-23 Part A merged first (both edit `DrainageLakeBalance.Resolve`) | T2R-21 (serialise builds) |
| T2R-23 Part A. Site removal behind options; the measurement | implementer | M | T2R-19 | T2R-20, T2R-21 (serialise builds) |
| T2R-23 Part B. Site removal in production | implementer | M | Part A, T2R-22, the user's answers to RQ1 and RQ2 | T2R-21's pictures, T2R-25 preparation |
| T2R-24 The intake structure | - | - | - | **dropped** (user decision 2026-11-13) |
| T2R-25 Rules review | claude-lead | S | T2R-20 to T2R-23 | T3 work |

**Execution order relative to the play-test orders** (revised 2026-11-13 for the removal decision).

1. **Done:** T2R-19 (commit 2f48fcb). Its tables are in `.godot/test_runs/t2r19_tables.md`; its option is still off by default.
2. **C2b** (gate suite and commit after T2R-12d). Nothing that changes the rivers of the play-test world lands before it: the before and after pairs of T2R-12c and T2R-12d must show the same rivers. T2R-23 Part A is not bound by this (step 4).
3. **T2R-20, then T2R-21.** They are short, T2R-21 needs T2R-20 (RS4), and the head picture goes to the user early because its answer can come while the rest is built. (T2R-20 was in the working tree, uncommitted, when this order was revised.)
4. **T2R-23 Part A, beside step 3 or straight after it.** Like T2R-19 it sits behind options that are off by default, so no bake output, no version and no picture changes. Serialise the C# builds: it shares `RiverGraphBuilder.cs` with T2R-21 (T2R-21 edits `ChannelAt`, Part A edits steps 1 and 2 of `Build`), and with T2R-20 only `tools/test_manifest.json` and one line of `tools/DrainageBakeReportTool.SiteIntakes.cs`. Its table and map crops go to the user at once, so that the answers to RQ1 and RQ2 can come while T2R-22 is built.
5. **T2R-22**, after T2R-23 Part A is merged (both edit `DrainageLakeBalance.Resolve`; Part A's branch is dead code with the options off, so T2R-22's reference hashes do not move). The largest and riskiest order; its Part A has a review checkpoint.
6. **T2R-23 Part B**, once T2R-22 is in and the user has answered RQ1 and RQ2.
7. **T2R-25, then C2c** (gate suite and commit; suggested). The user's play-test world is created again here, once.
8. T2R-14 and T2R-16 as in T2.md, after C2c.

If the builder is busy, T2R-23 Part A can instead run straight before Part B. The only cost is that the user's two answers come later.

T2R-18 has no interaction with this file beyond the shared GPU.

**Versions.** After T2R-12c: `DrainageAlgorithm` 6, `CarveOperator` 7, `GraphFormat` 4.

| Order | DrainageAlgorithm | CarveOperator | GraphFormat | Why |
|---|---|---|---|---|
| T2R-19 | - | - | - | option off by default |
| T2R-20 | +1 | +1 | - | path sections, stored landing factors; the channel section |
| T2R-21 | - | +1 | - | `ChannelAt` only; no rebake |
| T2R-22 | +1 | - | - | links, accumulation, stems |
| T2R-23 Part A | - | - | - | options off by default |
| T2R-23 Part B | +1 | - | - with rule C, +1 with rule L | keep-out, edge cells, lakes and stems; rule L stores the terminal `Cut` (RQ2) |
| T2R-24 | | | | dropped |

Expected at C2c: 9, 9 and 4 (rule C) or 5 (rule L). The operator is not edited by T2R-23: the carve changes because the graph does, and `DrainageAlgorithm` covers that. A rebake of a rivers world takes about 10 s plus the river build; its proxy caches then rebuild through the definition hash.

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

**Amended on 2026-11-13 by the Part A review below.** Where they differ, the review wins: steps A1 to A4 are added to Part A, items 8a to 8e to Part B, validation 4 and 6 and three escalation conditions are rewritten.

#### T2R-22 Part A review (lead, 2026-11-13)

Status: Part A is in the working tree, uncommitted (`DrainageAlgorithm` 8). The fill arithmetic is bit-equal on three seeds and `drainage_overflow_test` passes. **Verdict: Part A is accepted once A1 to A4 are done; Part B then starts, with 8a to 8e added.** The lead changed no code. Evidence: `.godot/t2r22/` (the implementer's logs), `.godot/t2r22_lead/` (the lead's logs and the two diagnostic patches the numbers below came from: `lead_diagnostics_carve.patch`, `lead_diagnostics_graph.patch`), `.godot/captures/landscape/t2r/overflow/`.

**Measured (seeds 876374626 / 1337 / 4242 unless one is named)**

- R22. Overflows per ring 4,460 on seed 876374626: 4,324 with a lake, 136 full pools without one; 3,155 below the channel threshold. Hand-over 15.75 % of the inflow of closed basins.
- R23. Closed lakes with an outlet stem: 1,085 on seed 876374626 (6,341 km of outlet stems), up to 1,565 on the other seeds. Stems +1,646 to +2,291 per ring, length +8,762 to +12,996 km (+5.9 to +7.5 %). On seed 876374626 half of the new length is drowned channel inside lakes (in-lake length 28,781 to 33,115 km), so the river a player sees grows by about 3 %. Dead stems 21 to 23 before, 4 / 4 / 1 after. Lakes that end a stem and overflow with no channel: 292 before, 5 after on seed 876374626. The 5 were not examined; some may be case (c), which item 7 of Part B removes. Any that remain after Part B are reported with their pools.
- R24. The forecast of 300 to 450 outlet stems counted only lakes that a stem ends in. RS1 "Graph" gives an outlet to every full lake whose catchment reaches the channel threshold, and the code does what RS1 says. About 790 of the 1,085 outlets leave lakes with no mapped inlet: their inflow arrives in streams under the threshold and leaves as one mapped river.
- R25. Saddle redirects on seed 876374626: 1,994, of them 1,479 where the saddle cell's own descent pointed into the full pool. "Runoff moved" 6,282.7 units, 2.37 % of the hand-over, counts both kinds.
- R26. **Read from the code, not measured.** In the 1,479 to 1,848 cases per ring of R25 the runoff after the saddle is too large. The arithmetic had put everything that flows through the saddle cell into the pool, so it is part of the hand-over. `ApplyOverflows` leaves out the cell's own runoff but adds what flows into the cell from elsewhere a second time. In the largest case of the report (saddle cell col 1795 row 181, hand-over 381.8) up to 176 units are counted twice. The lake arithmetic is not affected; the width of the outlet river is. The same happens at the 8 to 14 saddle cells per ring that two overflows share, when one of the two hands over into the other.
- R27. The duplicate outlet (`drainage_graph_test`). Lake 214 on strip_9804km (level 169 m, 28 cells) has two patches of water. The cell between them has its lowest sample at 169.01 m: under the saddle (169.67 m) but above the whole-metre level, so it is dry. Stem 97 leaves the small patch, crosses that cell and ends as a confluence on stem 87 inside the same lake. Stem 87 is the true outlet (its last cell is the saddle cell, its receiving cell lies in lake 180). On strip_seam the record is wrong, not only doubled: lake 530 (level 387 m) has stem 150 as `OutletStem`, a stem that returns to lake 530 across a cell at 387.39 m, while the true outlet is stem 155 into lake 490. A stem that is not the recorded outlet is held at the lake level along its whole length, so stem 155 is held past its saddle. Picture: `.godot/captures/landscape/t2r/overflow/strip1337_lake214_strait_40km.png`.
- R28. Lake outlets and the fine terrain (strip_9804km, the outlet's held part plus 3 km, a sample every 5 m).

  | Outlets | Count | Natural ground above the lake level, p10 / p50 / p90 / max | Outlets with ground over 40 m above | Samples cut over 40 m |
  |---|---|---|---|---|
  | Overflow (closed lakes, new) | 27 | 6.6 / 68.5 / 160.2 / 217.5 m | 21 | 16.6 % |
  | Spilling lakes (not touched by this order) | 92 | 11.6 / 54.6 / 121.0 / 172.6 m | 59 | 9.4 % |

  On the grid an outlet stands at most 1 m plus its depth under its saddle: the level is `floor(saddle)` and the saddle cell's lowest sample is the saddle. On the fine terrain nearly every lake outlet, old or new, runs in a cut through ground the grid does not see, because the path joins the lowest samples of neighbouring 1 km cells and crosses the spurs between them (F35). Whole ring, share of path length cut over 40 m: 4.15 to 4.43 %, 4.59 to 4.95 %, 4.41 to 4.71 %. The new length carries about 9 to 10 %.
- R29. `river_carve_invariants_test`, each failure with its cause:
  - "cut over 40 m on 5.83 % (limit 5.5 %)", 5.11 % with overflows off: R28.
  - "a waterline difference worse than the river-only state": one pair of 19,046 at 1.00 m. Stem 78, the outlet of lake 194 (level 213 m), joins the held stem of a lake at 212 m, and the level drops one metre where its hold ends (s 2,471 of 4,605 m). Two lakes one whole metre apart, joined by a river, exist only since this order. The general bar (0.5 % of pairs, largest 1.0 m) is met.
  - "the negative control could not tell": steps over 0.6 m in lake-near sections went from 78 to 1,598 with the lake operator and from 1,235 to 2,808 without it (largest 1.17 m in both). About 1,500 new steps stand in both states, so they belong to the new channels and not to the lake operator, which still removes about 1,200. The general continuity bar is met (0.03 % against 0.5 %, largest 1.17 m against 2 m). Where the new steps are was not located.
  - "true width, fp 16 and fp 32: 4 sections": all four on stem 225 (26 m wide, 2.1 km long, does not start in a lake), s 1,007 to 1,082, more than one cell from any lake cell. The water level at footprint 0 there is 128.00, 128.00, 127.59 and 126.11 m going downstream: a whole-metre plateau and a drop after it. At footprints of 16 m and more the piece is valley-only and its floor lies 0.05 to 1.1 m under that level. **Cause found in the Part B review (R34, R35): a chute, on HEAD too.** The first reading was that the plateau points to a held level of a lake (a held stem beside or below stem 225, or a level raised to one); held stems now run up to 10 km from their lake (lake 55: held to 10,140 m), and the check leaves out such places only within one cell of a lake.
  - "operator cost 3.286 us (limit 3.0)": 3.055 us with overflows off in the same hour, and no failure in the lead's run. The operator is not edited by this order. Machine load.
- R30. Resident graph, before and after: 31.19 to 32.71 MiB, 35.36 to 37.56 MiB, 33.97 to 35.69 MiB. Two of three seeds were over 32 MiB before this order. The 32 MiB came from an estimate of 20 MiB (T2.md S5 "Bytes") made before spans (6.6 MiB), per-stem curve objects (2.8), sections (1.5), tails and metrics (1.2), closed basins and dry runs (1.7) existed. Bake time +6 %, under the 10 % bar.

**Decisions**

1. **The redirect bar stays at 1 %, on a narrower measure.** Where the saddle cell's own descent points into the full pool, the cell is the lake's sill: its slope drains into the lake and the lake leaves across it. The arithmetic already had that water in the lake, and the link only draws the way out. Nothing is redirected, so these cases leave the count. A redirect on the receiving side that ends in the same pool as the hand-over moves the path by one cell and no water between pools. The bar applies to what is left: runoff that the links carry into another pool than the arithmetic filled with it (A3). The fill rule is not changed.
2. **The extra outlet rivers are right, and they are not restricted.** A lake that is full to its saddle and gathers the runoff of 100 km^2 or more has a river out; most lakes on Earth do. Restricting outlets to lakes that a mapped river ends in would leave about 790 full lakes per ring passing water on unseen, which is the fault this order removes. No floor or minimum is involved: the outlet carries the hand-over and has the section the channel law gives it. It is a change of the map the user was told would be smaller, so the user sees the crop pair (RQ6).
3. **The duplicate outlet is a missing rule, fixed in Part B (8a).** A stem that leaves a lake and returns to it is a strait. It is neither outlet nor inlet. The bake is right to route through the dry cell: at the true level (the saddle) that cell is under water.
4. **The deep cuts are not a fault of the overflow links.** They are the state of every lake outlet (R28), and this order adds a quarter more outlets. No change to the links, the levels or the operator is made here. The escalation "cuts more than 5 m below its saddle" was written for the grid, where it cannot happen; it is replaced by a report line (8c). The cuts are a look question for the user once the stills of validation 7 exist (RQ7). A cure, the outlet path and the sill taken from the fine terrain, is a design order of its own.
5. **The resident bar becomes a tripwire at 44 MiB** (proposed to the primary; an engineering budget, not a look value). 32 MiB was passed before this order and has no hardware reason behind it. 44 MiB is the largest seed plus about 17 %, enough to catch a structural mistake. If 32 MiB is wanted back, the first savings are the per-stem curve objects as flat arrays (about 2.5 MiB) and the derived span arrays (up to 6.6 MiB): an order of its own, on the operator's hot path.

**Part A, added steps (implementer; small, all in `ApplyOverflows` and its test)**

- A1. Where the saddle cell's old link pointed into the pool (`dropOwn`), the cell passes the hand-over and nothing else: not its own runoff and nothing that flows into it from outside the pool. In the accumulation:
  `r[i] = dropOwn[i] ? saddleHand[i] : acc[i] + saddleHand[i];` and `if (!dropOwn[v] && !(owner[u] != 0 && v == saddleOf[owner[u] - 1])) r[v] += r[u];`. Area accumulates as it does now. The copy of the runoff formula from `DrainageRouting.Route` goes.
- A2. Shared saddle cells. Mark `owner` and `saddleOf` for every overflow of the basin before the walk. An overflow whose receiving cell lies in the pool of another overflow with the same saddle cell does not add its hand-over to `saddleHand`: it is already part of the other's.
- A3. The record and the report split R25 in three: sill cells (count only); redirects on the receiving side that stay in the same top-level pool (count only); redirects into another pool (count, runoff, share of the hand-over, largest case). The pool of a cell is the pool of the pit its descent reaches, climbed while the parent is a pool (`pitOf`, as `PoolOf` does). Escalate if the third class moves more than 1 % of the hand-over.
- A4. `drainage_overflow_test`: in every case R on the first cell after the saddle **equals** the hand-over plus what else flows into that cell (1e-9 relative), not "at least". New cases: a saddle cell with an upstream cell whose descent enters the full pool through it (fails on the code before A1); three pools that meet in one saddle cell, two of them full, one handing over into the other (fails before A2).
- Then: the three whole-ring reports again (part hashes equal, the new redirect line), `.\tools\run_tests.ps1 -Tests drainage_overflow_test,drainage_graph_test -Label rivers`. `drainage_graph_test` still fails on "several outlets" until 8a.

**Part B, added items**

- 8a. **Straits.** `strait(stem)`: terminal Confluence, the first reach has lake l as `UpstreamLake`, and the receiving cell (`down` of the last cell) is a flooded cell of l. The outlet rule of `RiverGraph.Build` (:418-427) skips a strait. `RiverLevel` (:333) passes `endsInLake = true` for a strait, so it is held at the lake level to its end. The inlet rule needs no change (it already requires another lake). `drainage_graph_test`: the outlet count uses the same rule; every strait's level equals the lake level along its whole length (0.01 m); on strip_9804km lake 214 has outlet 87 and on strip_seam lake 530 has outlet 155 (the cases of R27, by head and last cell, not by id). The report prints the number of straits.
- 8b. **Bars of `river_carve_invariants_test`** (changes to tripwires; each needs the primary's acceptance, none is a look value):
  - Path cut: the 5.5 % bar applies to the reaches of stems that do not start in a lake. If that share is above 5.5 % with overflows on and was not with the hook off, stop and report. Reaches of stems that start in a lake are reported apart, with no bar until T2R-25.
  - Waterline: the comparison with the river-only state keeps the two continuity terms and drops the two waterline terms; the lake-near waterline gets the general bar (0.5 % of pairs over 0.6 m, largest 1.0 m). A pair over 1.0 m is the escalation below.
  - Negative control: the lake operator removes at least 30 % of the steps of the river-only state (43 % now, 94 % before). First print where the new steps are (stem, s, whether the stem starts in a lake, natural ground minus lake level). If they are not on channels cut through ground above a lake level, stop and report.
  - True width: superseded by item 8g of the Part B review.
- 8c. **Outlet line in the `--terminals` report**, for overflow and spilling outlets apart: count; natural ground above the lake level along the held part plus 3 km (p10 / p50 / p90 / max); share of samples cut over 5, 15 and 40 m; the ten largest with position. The lead's diagnostic patch has the loop.
- 8d. **Stills for the user** (validation 7): add the two overflow outlets of seed 876374626 with the largest cut from 8c, from 600 m and 3 km.
- 8e. If the user answers RQ6 with "only lakes a river ends in": the lead rule (:665-712) gives no lead to a closed lake with no inlet stem. A few lines in the builder; the bake does not change.

**Validation and escalation, rewritten**

- Validation 4: `river_carve_invariants_test` passes with the bars of 8b; `river_danger_test` unchanged.
- Validation 5: the resident graph stays under 44 MiB (decision 5), the bake time rises by at most 10 %.
- Validation 6, expected per ring: class (f) 0 (report any that remain, with their pools); class (c) 0; Dead at most 5; 1,000 to 1,600 outlet stems of closed lakes; straits reported.
- Escalation, in place of "the saddle redirects move more than 1 %": A3's third class moves more than 1 % of the hand-over.
- Escalation, in place of "cuts more than 5 m below its saddle": an outlet's level at its saddle cell stands more than 5 m under that cell's lowest sample; or the containment or plug bars of S8 break at an overflow lake.
- Escalation, added: a waterline pair over 1.0 m at an overflow outlet (two lakes more than a metre apart whose drop the tail rule does not turn into chutes). Report the lakes; do not write a new level rule.
- Escalation, in place of "exceeds 32 MiB": exceeds 44 MiB.

#### T2R-22 Part B review (lead, 2026-11-13)

Status: Part B is in the working tree, uncommitted. A1 to A4, item 7 and 8a to 8d are done; `drainage_graph_test` passes; `river_carve_invariants_test` fails on two escalations (run `.godot/test_runs/rivers/20261113-155700`). **Verdict: both are decided below. Item 8f is a small change to the level build, item 8g a change to one check. With both, the lead's scratch build passes `river_carve_invariants_test`, `drainage_graph_test`, `drainage_overflow_test` and `river_danger_test`.** The lead changed no code in the project. Evidence: `.godot/t2r22_lead/` (`probe.py` is the diagnostic, `probe_head.log` the run on HEAD without T2R-22, `probe_b.log` the run on Part B, `trial_8f_RiverLevel.patch` the trial of 8f, `trial_*.log` the four test runs with 8f and 8g).

**Measured**

- R31. Lake outlets on a whole ring (seed 876374626, the implementer's 8c line). Overflow outlets: 1,089; largest cut per outlet p10 / p50 / p90 / max 14.5 / 55.1 / 126.0 / 490.8 m; 687 with ground over 40 m above the lake. Spilling outlets: 5,638; 19.1 / 64.1 / 153.3 / 683.4 m; 3,924 over 40 m. Inside the held part alone the median largest cut is 41.6 and 47.5 m. So the overflow outlets are no worse than the spilling ones, and much of the cut lies inside the lake's own cells, not only at the sill. Straits: 893 per ring.
- R32. **The (c') break.** Lake 237 on strip_9804km (level 90 m, a spilling lake of 15 cells) is joined by item 7 to a passing river, stem 96. Its stem 102 is lake water along its whole length (4,573 m). The river stands at 99 m before the junction and at 89 m after it, and falls through 91.34 m exactly at the junction. Cause: the level build lowers a receiver to an incoming stem's end value from the first station at or after the junction, so the junction itself lies in the middle of the fall. The tail rule then reads 91.34 m as the receiving level and lifts every part of stem 102 that is lower, which is all of it: river water 1.34 m above the lake along 4.5 km. A second effect of the same lines: the end value of a held stem is the lake level plus 0.012 m, the knots lie 0.012 to 0.74 m under the station values and the displayed level is the whole metre below, so the river leaves the junction at 89 m, a metre under the lake.
- R33. On the fine terrain that river is 9 m above the lake where the grid lets the lake spill into it (the centreline's ground is 108 m; the cell's lowest sample is under 90 m). The same mismatch as the outlet cuts (R28, R31), seen at a junction.
- R34. **The true-width failure exists on HEAD.** Stem 225 of Part B is HEAD's stem 192 (head cell 85175, last cell 84775, no lake). Sampled every 25 m, its valley-only centreline at footprint 16 lies below min(G, L) on 14 of 84 samples on HEAD (worst 1.55 m) and on 40 of 85 in Part B (worst 1.56 m). Over every stem of the strip: 51 of 16,568 samples (0.31 %) at footprint 16 and 153 of 35,324 (0.43 %) at footprint 32 on HEAD, on 5 and 18 stems, worst 3.86 and 4.41 m; 0.51 % and 0.54 % in Part B. The test draws about 300 to 600 sections; on HEAD it drew none of them, in Part B it draws four on one stem. `ComputeTails` and `EndsInLake` are not involved: the stem has no lake and neither has its receiver.
- R35. Cause of R34. Stem 225 falls from 128 m to 123 m over 100 m (s 1,045 to 1,145), a chute between two whole-metre pools. At footprint 0 the channel's own core sets the height. At 16 m and more the piece is valley-only, and the height is the lowest valley bound of every piece in reach. The pieces further down the chute have lower levels, and their bounds reach about 60 m upstream. The floor therefore starts to fall at s 990 while the pool level stays 128.00 m until s 1,045: up to 1.26 m under the pool here, and up to 0.13 m under the footprint-0 bed. It is a property of the operator at chutes of narrow streams, not of this order.
- R36. One lake stem still ends in higher water after 8f: the outlet of lake 169 (1,519 m) joins the held stem of lake 163 at 1,522 m and is lifted 3 m along 4.1 km. It is on HEAD too (the test lists it as "a cascade the lake balance left uphill" and does not compare it). Not touched here; listed for T2R-25.

**Decisions**

1. **The (c') break: the river meets the lake at the lake's level (8f).** The join stays. A receiver is already lowered to whatever flows into it; the fault is that this starts one station late and one whole metre low. With 8f the river falls to the lake's level just above the junction, stands at that level at the junction and leaves from it as an outlet would. What the player sees at lake 237: a river that drops 9 m into the lake's level and flows on from it. Refusing such joins was the alternative; it would leave a lake with a river in and no river out, which is the fault this order removes, and it needs the levels before the stems are chosen. The earlier instruction "do not write a new level rule" is lifted for 8f only.
2. **The true-width failure is not a regression (8g).** The check asks that a valley-only floor is not below the water level of footprint 0. That cannot hold just above a chute (R35), and it held on HEAD only by the draw of the sample. The check keeps its purpose, that a valley-only piece digs no bed, and compares with the lowest level the stem has within the operator's reach downstream. The bar stays 0.5 %. The operator is not changed. For the primary's acceptance: it is a change to what a check asserts.

**Part B, further items (implementer)**

- 8f. `RiverLevel`, the station loop of the level build (`while (inc != null ...)`, about :360). Trial: `trial_8f_RiverLevel.patch`; write it cleanly.
  - `lakeEnd(k)` for an incoming stem k: its first reach has a lake u as `UpstreamLake`, it is not Dead, `heldEnd[k] >= Length(k) - 1e-6`, and `endValue[k] <= lakeLevels[u] + BandUpper + 1e-3` (its end was not raised by another lake; the 1e-3 covers the float).
  - `cap(k) = lakeLevels[u] + BandLower + BandUpper`. The knots then lie between 0.012 and 0.74 m above the lake level, and the displayed level is the lake level.
  - Before the loop: for every lake-end incoming stem, `jb` = the last station with `s <= JunctionS` (0 if there is none), and `early[jb] = min(early[jb], cap(k))`.
  - In the loop, at a station that is not a held station of the receiver: `m = min(m, early[j])` and `plain = min(plain, early[j])`. While the receiver holds, the T2R-8b rule stays as it is.
  - In the existing `while`, when the receiver does not hold: `v = lakeEnd(k) ? cap(k) : endValue[k]`.
  - Nothing else changes. Every other confluence keeps today's values. `DrainageAlgorithm` stays 8 (this order has not been committed).
  - Report (`--terminals`): lake-end joins per ring; how many receivers the cap lowered and by how much at the junction (p50 / p90 / max, the ten largest with position); lake stems that still end in water above their lake.
  - `drainage_graph_test`: for every lake-end join whose receiver does not hold, the receiver's level at the junction equals the lake level (0.01 m) and the lake stem's tail drop is 0.
- 8g. `river_carve_invariants_test`, `Footprints()`, the valley-only centreline check: `double floor = Math.Min(g, Math.Min(r0.WaterLevel, RiverLevel.AtStem(_net, sec.Stem, Math.Min(sec.S + RiverCarveOperator.FadeEndMeters, _net.Curves[sec.Stem].Length))));`. Bar and counters unchanged. Print beside it how many reach sections have less than 0.5 m of fall over those 300 m, which is where the check has its old strength; fewer than half of the sections is an escalation. This replaces the true-width bullet of 8b.
- 8h. Stills for RQ7: add one overflow outlet and one spilling outlet near the median cut (about 55 and 64 m) to o1 and o2, which are the two largest. The user should see the usual case beside the worst.
- Then: `.\tools\run_tests.ps1 -Tests drainage_overflow_test,drainage_graph_test,river_carve_invariants_test,river_danger_test,river_graph_cache_test,inland_water_sampler_test,world_generation_fingerprint_test -Label rivers`, and the three whole-ring reports for 8f's line.

**Escalation, added**

- After 8f a lake stem ends in water more than 0.1 m above its lake and its receiver is not held by another lake. Report it; do not widen 8f.
- 8f lowers more than 50 receivers on a ring, or one by more than 25 m **(starting values)**. Report the list. Each is a river standing above the lake that spills into it (R33), and the cut below the junction grows by the same amount.

**RQ7: what a cure for the outlet cuts would be (proposal, not ordered)**

The cause is not the overflow rule. A lake's cells and its saddle come from the lowest of 16 samples per square kilometre. The path of an outlet joins those lowest points cell by cell, and the fine ground between them is higher: a median 55 to 64 m somewhere along each outlet, and over 40 m at two of three (R31).

- **Lake levels from the fine-terrain sill: not recommended.** Lowering does nothing, since the level already lies under the fine sill. Raising the level to the sill would lift most lakes by tens of metres, drown ground the map shows as land, and change the water balance, the flooded share and every lake's shore. Size L, high risk, and it undoes the choice of the reset (the lowest sample keeps levels conservative; F35).
- **The outlet follows the lowest fine pass: the candidate.** Between the lake's water and the first cell past the saddle, and along the drowned channel inside the lake, the centreline is laid by a least-cut search on a fine lattice (62.5 m, a window of about 4 km around the grid path) in place of the straight join of cell minima. Levels, lakes and the balance stay bit-equal; only nodes move or are added. What it cannot do: where the lake truly stands under its lowest fine pass, a cut of that depth remains.
- **So measure first: one study order (T2R-26, size S to M, read-only, low risk).** For every lake outlet on three rings: the lowest pass on the fine lattice against the lake level, the cut along the best path against today's, and the path's length. The GPU kernel samples such windows in seconds. Three outcomes:
  - the lowest pass is within about 10 m of the lake level for most outlets: build the routing (size L in two parts, outlets first and in-lake channels second; medium to high risk, because every centreline test, the D4 measure, the site guard and the graph size move with it; `DrainageAlgorithm` + 1 and a rebake);
  - it is not: the lakes themselves are too low for their ground, and the question becomes whether lakes should be fewer or shallower on rough ground, which is a balance question for the user with pictures;
  - in between: routing plus a look treatment of the cut that remains (a wider, sloped valley in place of a trench), which is operator work and a `CarveOperator` bump.
- Until then the overflow rivers are no reason to wait: they add 1,089 outlets to 5,638 that already look like this.

### T2R-23. Sites remove the water that reaches them

Rewritten on 2026-11-13 for the user's decision "sites remove water instead of taking it in". The order "Site intakes switched on" that stood here is withdrawn; nothing of it was built. Spec RS2.

ROLE: implementer
SIZE: L (two parts; a user checkpoint after Part A)
DEPENDS ON: Part A: T2R-19 (done). Part B: Part A, T2R-22, and the user's answers to RQ1 (keep-out) and RQ2 (tributaries).

OBJECTIVE
A river whose water runs into a site is not on the map, from its source down. A lake that would spill into a site is not there. Rivers and lakes may lie right beside a site's keep-out. No lake stands above site ground, no river ends at a site, and nothing is built at a site. Part A builds the rule behind options and measures, on three seeds, how many rivers, lakes and kilometres go. Part B switches it on.

CONTEXT / ROOT CAUSE
- The user's decision of 2026-11-13 (end of this file). It supersedes decision 4.
- T2R-19 (R15, R16): with the keep-out edge as an outlet, every dammed lake goes (705 to 755 per ring) and 207 to 227 stems per ring end at a site. The user wants neither those river ends nor structures for them: the rivers go instead.
- R17: the R10 escalation of T2R-19 came from the end nodes of Intake stems. Those stems are no longer stored.
- R19: the simplest way to remove a lake is not to create it in the balance.
- The user's rule leaves two things open that change the map: the keep-out (RQ1) and what happens to the tributaries of a removed river (RQ2). Part A measures both readings before the play-test world changes.

FILES/SYMBOLS
- Part A: scripts/Terrain/Hydrology/DrainageLakeBalance.cs (`DrainageBasinInfo`, the spilling branch of `Resolve` :347-379, `Run`); DrainageRouting.cs (`DrainageRouted`); DrainageBakeReport.cs; RiverGraphBuilder.cs (`RiverTerminal`, `RiverBuildOptions` :428, `RiverBuildReport`, steps 1 and 2 of `Build` :578-783, `GuardBarriers` :1102); RiverMapWriter.cs (`WriteWindow`). New: scripts/Terrain/Hydrology/RiverSiteRemoval.cs.
- tools/DrainageBakeReportTool.cs and DrainageBakeReportTool.SiteIntakes.cs (the T2R-19 section becomes the site removal section; a `--site-removal=<branch|catchment>` argument); tools/DrainageSiteIntakeTest.cs.
- Part B: DrainageBake.cs (the option defaults); DrainageBarriers.cs (`DefaultKeepOutMeters`, `LateralCapMeters`, the summary text); RiverGraphFormat.cs (:212, rule L only); DrainageMapWriter.cs; HydrologyVersions.cs. Read only: RiverGraphStore.cs:250-252 and DrainagePreviewBridge.cs:44, which pass no options and so pick the defaults up.
- tools/DrainageGraphTest.cs (:936-951, :1040-1062); tools/RiverCarveInvariantsTest.cs (:675 and a new site part); tools/DrainageBakeReportTool.Terminals.cs; tools/test_manifest.json.

REQUIRED CHANGES
Part A: the rule behind options, and the measurement. Production is unchanged.
1. **Site-drained basins** (RS2 "Lakes"). `DrainageBasinInfo.SiteDrained`. In the spilling branch of `Resolve`, a basin whose `exit` is an Intake cell creates no lake: no lake record and no `LakeTemp`; the inside accumulation along the old links runs as today; the whole inflow (`total`, with no evaporation taken off) and the whole area go to `exit`. Its info is `Closed = false`, `Lakes = 0`, `Evaporation = 0`, `Outflow = total`. Intake cells exist only with `SiteIntakes`, so nothing changes with the option off. `DrainageRouted` and the bake report gain the number of site-drained basins and the runoff that arrives at Intake cells.
2. **`RiverSiteRemoval`** (new file). `enum RiverSiteRemovalRule { None, Branch, Catchment }` and the marking of RS2 "Rivers", written as a function of arrays (the chains, `down`, the cell kinds, `Terminal`, `BasinId` and the `SiteDrained` flags) so that the test calls it without a grid. `RiverBuildOptions.SiteRemoval`, default `None`.
3. **Builder.** Steps 1 and 2 of `Build` (channel cells, lake marks, main links, chains, terminals) become one `internal static` method that takes the field and a mask of removed cells. It needs no grid, so it can run twice and the test can run it on a synthetic field.
   - `Catchment`: one pass. A cell with `Terminal == Intake` is not a channel cell.
   - `Branch`: pass 1 with an empty mask gives the chains and their terminals; `RiverSiteRemoval` marks the cells of the removed chains; pass 2 runs with that mask and is the result. A chain of pass 2 whose next cell is a removed cell gets the new terminal `RiverTerminal.Cut` (appended after `Intake`).
   - With either rule no chain of the result has terminal `Intake`. The builder throws if one has.
   - The end node of a Cut stem is the node of its next cell, placed as a Dead stem's is today. It is neither a junction nor in water, so the guard may move it (`GuardBarriers` skips every last node today, :1157). No other stem's last node becomes movable.
   - `RiverBuildReport` carries, for the report tool: the removed chains (head cell, last cell, cell count, discharge at the last cell, and the reason: ends at a site, inlet of a site-drained basin, or drains to a site), the Cut stems, and the lakes that lost their outlet river (RS2 "What rule L leaves").
4. **Report tool.** `--site-removal=<branch|catchment>` sets `SiteIntakes` in the bake and the rule in the build; `--keep-out=<m>` as before. The section prints the lines of RS2 "Measurement table". The length of a removed river is read from a build with the rule off on the same field (the chains are the same), matched by head cell. `RiverMapWriter.WriteWindow` draws, for the report tool only, removed rivers as thin grey lines, the cells of a site-drained basin in light grey and a Cut end as a small square.
5. **The measurement.** Three seeds (876374626, 1337, 4242), whole ring, GPU source, `--no-levels`: keep-out 300 with `branch`, keep-out 300 with `catchment`, keep-out 0 with `branch`, and one reference run per seed with the rule off. Twelve runs. One table per seed, written to `.godot/test_runs/t2r23_tables.md` and into the hand-back.
6. **Map crops for the user.** `--window` at site 435 (ARC 23,206.5 km, ACROSS 183.5 km) and at site 441 (ARC 23,520 km, ACROSS 186 km), seed 876374626, keep-out 300: rule off, `branch` and `catchment`. Six pictures, sent with the table.

Checkpoint: hand back the tables, the crops and the fixture results. The lead reviews them, and the primary takes RQ1 and RQ2 to the user. Part B does not start before both are answered.

Part B: the rule in production.
7. **Defaults.** `SiteIntakes` is true. `DefaultKeepOutMeters` is the user's value; at 300 it is `CorridorRadiusMeters` alone, and `LateralCapMeters` leaves the clearance (the guard covers the meander, R11). The user's rule becomes the builder's rule. The rule that was not chosen is deleted with its report column and its test case, and `RiverSiteRemovalRule` goes with it. `RiverBuildOptions.SiteRemoval` becomes a bool that is true by default; like the other switches of `RiverBuildOptions` it exists for the report tool, which needs one build without removal on the same field to measure what was removed. Without edge cells (generation 1 has none) the rule removes nothing.
8. **Rule L only:** `RiverGraphFormat` accepts `Cut` and still rejects `Intake` (:212); `GraphFormat` + 1. A Cut stem has no tail and no receiving level, and a Cut stem that starts at a lake keeps the outlet hold; the level code already does both for a terminal it does not name (R18). Confirm it; do not add a rule. **Rule C only:** `RiverTerminal.Cut` is deleted and `GraphFormat` is unchanged.
9. **Names and text.** `DrainageCellKind.Intake`, `MarkIntakes` and `SiteIntakes` keep their names (committed tests and reports use them). Their summaries say what they now are: the edge cell of a keep-out, where water leaves the map, with nothing built there. `RiverTerminal.Intake` stays as a value the builder uses between its passes and never stores. The D2 text in `DrainageBarriers` follows the new keep-out.
10. **Maps** (production): a removed river has no mark, because it is not there. The edge cells keep their colour in the drainage map. Rule L: a Cut end is a small square, and the legend says "Square: a river that ends where the river it joined was removed at a site".
11. **Reports.** The bake report and the `--terminals` report always print the lines of RS2 "Report". For rule L the `--terminals` table of T2R-6c gains the rows "(g) a cut end" and "(h) a spilling lake whose outlet river was removed".
12. **Versions and rebake.** Part A: none. Part B: `DrainageAlgorithm` + 1; `GraphFormat` + 1 with rule L only; `CarveOperator` unchanged (the operator is not edited). Every rivers world rebakes on its next load (about 10 s plus the river build), and test profiles drop their stale graph through the version. The user's play-test world is rebuilt once, at C2c.

DO-NOT-CHANGE CONSTRAINTS
- Generation 1 worlds must stay identical: `world_generation_fingerprint_test` must pass unchanged. The site registries, the site shaper and the site identity are not touched.
- Part A: with the options at their defaults the bake and the graph are bit-equal to before (`DownHash`, the graph file bytes, every existing test result). No version changes. T2R-22's reference hashes do not move.
- The operator is not edited and never queries sites. A removed river or lake leaves nothing in the graph: no bed, no bank, no valley bound, no dry-floor run, no plug.
- The fill arithmetic of S8b and T2R-22 is not touched, apart from the one branch of step 1. Closed basins, overflow lakes and overflow rivers are not removed, shortened or re-routed.
- R10 keeps its bar (influence radius + 300 m) and the guard keeps its clearance (105 m). Neither is weakened to make a number pass.
- No floor, minimum, widening or boost. No river is kept alive past a site, none is led round a site by a new rule, no Cut end is tapered, faded or shortened in this order, and no river below a site is topped up for the water the site removed. `E0` is not recalibrated here.
- A lake that lies beside a keep-out without spilling into it is not removed (RQ4).

VALIDATION STEPS
1. `drainage_site_intake_test` (existing manifest entry; headless, synthetic domains through `BakeDomain`), new cases:
   - a depression against the block whose lowest rim cell is an edge cell and whose inflow exceeds its evaporation: with the option on it holds no lake cell, its basin is `SiteDrained`, its cells have terminal Intake, and R on the exit cell equals the runoff of everything that drains to it within 1e-9 (no evaporation taken). With the option off a lake stands there: the check can fail;
   - the same depression with too little inflow to spill: it keeps its balance lake and is not `SiteDrained`;
   - a valley that runs on to the sea on the far side of the block: R on each of its cells equals the hand-computed runoff of the far side alone;
   - the mass balance closes with the site term (relative error at most 1e-9);
   - the chain method of required change 3 with `RiverSiteRemoval`, on one hand-built field: a trunk T that ends at an edge cell, a tributary t1 of T, a tributary t2 of t1, a stem V that joins T inside a site-drained basin, a lake A on T with an inlet i1, and a stem U that reaches the sea. `Branch`: T and V are removed, t1 is Cut, t2 still joins t1, i1 ends in lake A, A has no outlet, U is unchanged. `Catchment`: T, V, t1, t2 and i1 are removed, no stem is Cut, lake A stays, U is unchanged. With `None` nothing is removed and T has terminal Intake: the check can fail.
   Manifest: `expected_s` of `drainage_site_intake_test` from 20 to 30. No new entry: no new test file is added. Run `tools/testing/validate_manifest.ps1` after the edit.
2. Part A: `.\tools\run_tests.ps1 -Tests drainage_site_intake_test,drainage_graph_test,river_graph_cache_test,world_generation_fingerprint_test -Label rivers` (`-DryRun` first). `drainage_graph_test` and `river_graph_cache_test` pass unchanged: that is the "defaults are bit-equal" check.
3. Part A: the twelve runs through `drainage_bake_report` (gpu-local; one at a time). Evidence under `.godot/audit_baseline/<machine>/t2r/bake/<stamp>_t2r23_s<seed>_k<keep-out>_<rule>/`. Cross-check against T2R-19 at the same seed and keep-out: the site-drained basins equal its "lakes that spill straight into an intake cell" (94 / 94 / 93 at 300; 85 / 93 / 88 at 0); the removed chains that end at a site are at least its "stems ending at an intake" (224 / 219 / 215 at 300), the surplus being chains that start in the bed of a removed lake. Print both. Run `tools/prune_test_output.ps1 -Apply`.
4. Part B, `drainage_graph_test` on both strips (they have sites):
   - no stem has terminal Intake; no channel cell's `down` is an edge cell; no channel cell lies in a site-drained basin; rule C: no channel cell has `Terminal == Intake`;
   - rule L: the end-of-chain check (:951) adds `Cut`; every Cut stem's last cell has `Terminal == Intake`, and its next cell is Land, dry and not a channel cell; a file round trip keeps the terminal;
   - the Dead check (:936) is unchanged: a Cut end is not Dead;
   - no lake has a wet cell whose `down` is an edge cell, and no lake's level is above the lowest sample of an edge cell beside it (hard limit 0; the second fails with the option off, where the Barrier cells that touch land stand in for the edge cells);
   - the centreline check (:1040-1062) passes with its bar unchanged. It now counts Intake cells as barrier cells and tests the Wall Engine stadiums and the Lung bound with the same bar (R21);
   - the mass balance counts the site term.
5. Part B, `river_carve_invariants_test`: the line "ESCALATION (T2R-8): a plug of ..." no longer prints, and the largest plug on the strips is at most 5 m **(starting value)**. New site part, which is the check RU1 did not find: on a 50 m lattice over every site shape of the strips, no sample has kind River or Lake. Negative control: one fabricated flooded cell beside a shape, with the ground under the level across the shape's edge, must be found.
6. Part B: `.\tools\run_tests.ps1 -Tests drainage_site_intake_test,drainage_graph_test,drainage_overflow_test,river_carve_invariants_test,river_danger_test,river_graph_cache_test,inland_water_sampler_test,world_generation_fingerprint_test -Label rivers`; `.\tools\run_tests.ps1 -Tests inland_swim_smoke_gpu_test,inland_water_render_gpu_test -Label landscape_gpu` (`-DryRun` first). The gate suite is the primary's, at C2c.
7. Part B: whole-ring `--terminals` reports on the three seeds. Lakes above keep-out ground: 0 (were 705 to 755). Centreline samples closer than influence radius + 300 m to a site shape: 0. The Part A table at the chosen keep-out and rule is reproduced on the tree with T2R-22. The flooded share, the channel length, the Dead stems, the overflow lines and the spans inside an exclusion zone are printed against the reports of T2R-22.
8. Part B, for the user: stills of places e1, e2 and e3 of T2R-17 again (sites 441, 435 and 496); the valley of the largest removed river within 350 km of the play-test spot, top down from 600 m and from eye height; rule L: one Cut end, top down from 600 m and from its bank. Use `tools/river_site_capture.ps1` with a places file in the existing format; no 8 km view. Captures under `.godot/captures/landscape/t2r/site_removal/`; delete raw frames; `tools/prune_test_output.ps1 -Apply`.

STOP RULE
Part A: 3 periods or 4 attempts. Part B: 4 periods or 5 attempts.

ESCALATION CONDITIONS
Part A:
- With the options off, anything differs from before.
- The site-drained basins differ from T2R-19's count at the same seed and keep-out. Report both numbers and the cause.
- At keep-out 300 with removal on, any centreline sample is closer than influence radius + 300 m to a site shape. Report every such stem with its terminal, its cells and the cause (R17). Do not move the guard, the bar or an end node. The lead's fallback is a keep-out of 400 m (the corridor plus the meander cap), which holds for an end node by construction.
- A lake stands above the ground of an edge cell.
- Removed rivers are more than 8 % of a ring's river length, or more than 5 % of a ring's runoff leaves at sites **(starting values; T2R-19 measured 1.9 to 2.8 %)**. Report it prominently.
- The flooded share of land leaves the 6 to 10 % band of S8b. Report it; do not touch `E0`.
- Not an escalation, but always reported at the top of the hand-back, because RQ2 depends on it: the Cut ends per ring with their widths, and the lakes that lost their outlet river.

Part B:
- A number of the Part A table moves by more than 5 % at the same keep-out and rule. Report it with the cause (T2R-22 is the expected one).
- A check of T2R-22 fails after the switch: `drainage_overflow_test`, the (c') check, or more than 5 Dead stems on a ring.
- A stem with terminal Intake reaches the graph, or a River or Lake sample lies inside a site shape.
- A still shows a straight bank, a river stub at a site, water on site ground, or a bed or a bank line where a river was removed. Report the place and the cells.
- The user asks for Cut ends to be tapered or for the dry bed of a removed river to be kept. Both are new rules (RQ2, RQ3) and a follow-up order.

### T2R-24. The intake structure: dropped

**Dropped by the user's decision of 2026-11-13** ("sites remove water instead of taking it in"): no river ends at a site, so nothing is built there. The id is not reused.

Not built, and not to be built from the earlier text of this order: `scripts/poi/Intakes/` (`RiverIntakeSites`, `RiverIntakeMesh`, `RiverIntakeStreamer`), `river_intake_sites_test`, `river_intake_gpu_test` and their manifest entries, and Spec RS5. None of it was started: the tree holds no file and no manifest entry of that name (checked 2026-11-13). Open question 3 and user answer 4 (the placeholder look) are closed with it. The earlier text is in the history of this file (commit 24d44bd).

### T2R-25. Rules review

ROLE: claude-lead
SIZE: S
DEPENDS ON: T2R-20 to T2R-23 (T2R-24 is dropped).

OBJECTIVE
The before and after numbers of the batch on three seeds, the pictures at the places of T2R-6c and T2R-17, and a list of what is still open, so the primary can run the gate suite and commit (C2c).

REQUIRED CHANGES
1. Whole-ring `--terminals` reports on seeds 876374626, 1337 and 4242 on the finished tree. One table with the T2R-6c classes before and after, the smallest sections, the heads line, the overflow lines and the site removal lines (RS2 "Report").
2. **Fine-area balance, rule (i), measured again.** The share of river ends in a closed lake that shows under half the area its rivers need. It was 1.5 to 2.0 % before the batch. Ask the user only if it is above 3 %.
3. Review each order's diff against its constraints, in particular: the fill arithmetic of T2R-22, the absence of any minimum in T2R-20 and T2R-21, and the site rules of T2R-23 (nothing of a removed river or lake left in the graph, no water inside a site shape, R10's bar and the guard unchanged, the rule that was not chosen deleted).
4. Stills: d1 to d6 and e1 to e3 once more on the finished tree, at most five in the report.
5. **Site removal revisited with pictures** (user answer 1: all structures "for now"; RQ2 to RQ4): what was removed per ring, what rule L or rule C left behind, the lakes beside a keep-out that stayed, and whether Wall Engines and the Lung should keep the same rule. Bring it to the user as a proposal, not as a change.
6. Write the results as a "Rules review" section at the end of this file, with the open questions that remain. Propose the text for T2.md and T3.md listed under "Notes for T3 and T4"; the primary applies it.

VALIDATION STEPS
- `.\tools\run_tests.ps1 -Tests drainage_graph_test,drainage_overflow_test,drainage_site_intake_test,river_carve_invariants_test,river_danger_test -Label rivers`. The gate suite is the primary's, at C2c.
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
- **Accumulation.** After all links of a basin are final, area and R are accumulated once along them, in topological order. Area accumulates plainly, so the full pool's whole catchment passes over the saddle, as a spilling basin's does. R accumulates plainly except at the step from the pool into the saddle cell, where the pool's cells pass `handover[A]` in total instead of their sum: the lake's evaporation stays in the lake. Where the saddle cell's own descent pointed into the pool (the cell is the lake's sill), the arithmetic had counted everything that flows through that cell into the pool: the cell's own runoff and whatever reaches it from upstream. None of it is added a second time, so the cell passes the hand-over alone (review step A1). Two overflows that share a saddle cell, one handing over into the other, pass the second one's hand-over alone (A2).
- **Invariants** (`drainage_overflow_test`, `drainage_graph_test`): no cycle; every overflow has a path to the receiving pool; R on the first cell after the saddle is at least the hand-over; lake levels, wet cells, basin verdicts and the mass balance are bit-equal to a bake without overflows.
- **Graph.** No new record. The builder sees a lake whose cells flow to an exit and treats it as it treats a spilling lake: the drowned channel of T2R-8b from each inlet to the exit, an outlet stem with the outlet hold, `OutletStem` set. A closed lake (`Basin >= 0`) therefore has an outlet exactly when it is an overflow and the first cell after its saddle is a channel cell (its catchment reaches the channel threshold). An overflow below the threshold has no river, as a small spilling lake has none.
- **What stays Dead.** A pool that is not full and has no room for a level, or an inflow under half a cell's evaporation: water that really ends on a pan. Expected 0 to 2 stems per ring.
- **(c')** A lake that a river ends in and whose exit flows into a channel cell: its drowned channel runs through the exit and joins that river as a confluence, with the outlet hold up to the lake's ring and the confluence tail below it. Lakes with no inlet river are unchanged.
- **Report.** Overflow lakes; full pools without a lake; total hand-over and its share of the discharge that stems bring to closed lakes; overflow rivers (stems and km); saddle redirects and their runoff; overflows below the channel threshold; Dead stems after overflow; (c') joins.
- **Straits** (added by the Part A review, item 8a). A lake's whole-metre level can leave a cell dry that lies under its saddle, between two patches of its water. The links cross that cell, and the stem that does so starts in the lake and ends in it. It is a strait: held at the lake level along its whole length, neither outlet nor inlet.
- **Look.** About 300 to 400 end-ponds per ring become lakes with a river out, the same number of short rivers appear between the lakes of one basin, and the lakes that receive the surplus are finally reached by the rivers that fill them. **Measured in Part A (R23, R24):** 1,085 to 1,565 lakes per ring get a river out, because full lakes with no mapped inlet get one too. About half of the added length lies under lake water. Like the outlets of spilling lakes, the new outlets run in a cut through the ground between two lakes (R28).

### RS2. Sites remove the water that reaches them (T2R-19, T2R-23)

Rewritten on 2026-11-13. The former RS2 ("Site intakes and the keep-out") made the keep-out edge an outlet and ended rivers there at a structure. The outlets stay. The river ends and the structure do not.

**Decision** (user, 2026-11-13). Water that reaches a site leaves the map there. A river that runs into a site is not mapped, from its source down. A lake that would spill into a site is not there. Rivers and lakes may lie beside a site. One rule for emitter discs, Wall Engine stadiums and the Weather Lung ring (user answer 1 carries over).

- **Terms.** A **site shape** is the influence shape of a structure: the emitter disc, the Wall Engine stadium, the Lung bound (R20). The **keep-out** is `KeepOutMeters` beyond it. A **keep-out cell** is a cell whose centre is closer to a site shape than the keep-out plus half a cell diagonal (kind Barrier, R7); every point within the keep-out of a shape lies in one. An **edge cell** is a keep-out cell with a geometric 8-neighbour of kind Land or StripOutlet (kind `Intake` in the code, R15). No Land cell touches any other keep-out cell. Sea cells inside a keep-out are keep-out cells.
- **Edge cells** are outlets at the height of their lowest sample, without routing noise, and the flood starts from them as from the sea (T2R-19). A depression beside a site fills at most to that height. So no lake stands above keep-out ground, and the Barrier cap of S8 has nothing left to act on there.

**What "intersects" means.**
- **A river intersects a site** when its water runs into an edge cell: the `down` link of one of its cells is an edge cell. This is a statement about the flow on the 1 km grid, not about the drawn line, for two reasons. Water cannot be routed across site ground, so a centreline never crosses a keep-out and a test on the drawn line would never be true. And the grid is where a river's course is decided: a river that the grid sends into an edge cell has no other course. The price is the grid's resolution. The keep-out cells reach up to one cell diagonal (1.4 km) beyond the keep-out line, so a river whose true course would pass just outside the line can be removed. A river that passes a site through Land cells does not intersect it, however near it runs.
- **A lake intersects a site** when it would spill into an edge cell: it is the lake of a basin that spills and whose exit cell is an edge cell (a **site-drained basin**). Such a lake would stand against the keep-out and pour into it. A lake that lies beside an edge cell and spills somewhere else, or does not spill, does not intersect: its level is at or below the edge cell's ground, its shore is natural ground, and it stays (RQ4).
- The site shape itself is never tested against water by the rule. It is tested by the checks: no River or Lake sample may lie inside a site shape (T2R-23 validation 5).

**Keep-out: 300 m proposed** (RQ1; today 650 m).
- 300 m is how far a river's carve reaches from its centreline (the corridor radius, the operator's fade end). The guard keeps the node curve 105 m from keep-out cells and the meander is at most 100 m, so the centreline stays 305 m from a site shape. The edge of a river's valley can then touch the edge of a site's shaped ground, and neither changes the other. Nothing changes in the builder, the operator or the site shaper, and R10's bar holds as it is written.
- Nearer than that puts the river on site-shaped ground: the channel (up to 200 m half width), the bank, the floodplain and the valley side would lie inside the shape, where the site shaper re-forms the ground after the river operator has run, so water level and ground would disagree. That is a design order across the guard, the operator and the site shaper (which is part of the site identity), with a `CarveOperator` bump.
- It would gain little. T2R-19 counted 224 / 219 / 215 rivers into sites at 300 and 226 / 212 / 207 at 0 (R16). In distance, 0 instead of 300 moves a river from 3.3 km to 3.0 km from the centre of a regular emitter, because the site shape itself is 3 km (R20).
- Against today's 650 m, 300 m brings rivers 350 m nearer and frees about 4 % of the keep-out cells.
- **Consequences.** R10: bar unchanged (influence radius + 300 m), now tested for all three kinds of structure. R11: the 350 m lateral cap leaves the clearance; the guard already covers the meander. The guard itself is unchanged; it already treats edge cells as barrier cells. End nodes are not guarded (R17): if Part A finds a stem inside the bar at 300, the fallback is 400 m (corridor plus meander cap), not a weaker bar. R12: the 1,500 m exclusion zone is unchanged; more river length now lies inside it, so somewhat more spans become landings (printed in Part B).

**Lakes (bake).** A site-drained basin holds no lake: no lake record and no wet cells. Its cells keep the links of the flood, as the cells of a depression under 2 m do, and its whole inflow goes to the exit cell. No evaporation is taken, because there is no water surface. A closed basin beside a site keeps its balance lakes. A removed lake has no outgoing river by its definition: its only way out is into the site. Its incoming rivers are removed by the river rule below.

**Rivers (builder).** The builder first finds its stems as today. A stem is the chain that follows the larger catchment upstream at every confluence (S5), so "the river" is the main course and what joins it is a tributary. The user's rule has two readings, and RQ2 chooses between them.
- **Rule L, "branch"** (the user's wording). Removed whole, from its head cell to its last cell: (1) a stem whose next cell is an edge cell; (2) a stem whose last cell or next cell lies in a site-drained basin, which is an incoming river of a removed lake. Nothing else is removed. The builder then builds again without the removed cells. A stem that joined a removed stem ends at its own last cell with terminal **Cut**: it stays, as the user asked ("rivers flowing into that branch upstream stay"), and it ends on dry ground where the river it joined used to run. Its own tributaries are untouched. A removed stem that began in a lake (a lake's drowned channel and its outlet river are one stem, T2R-8b) is removed from that lake down; the lake and its inlets stay, the inlets end in the lake, and the lake has no outlet river.
- **Rule C, "catchment"** (the alternative). A cell whose water ends at a site (`Terminal == Intake`) is not a channel cell. Every mapped river that drains into a site goes, with all its tributaries, and no stem is Cut. A lake further up that drains towards a site stays, without rivers.
- Under both rules a river whose mouth lies inside a keep-out is removed like any other, since the sea cells there are keep-out cells.

**What rule L leaves** (Part A counts both). Cut ends: a river that stops on dry ground at its full width, some kilometres before a site. And spilling lakes with an inlet river and no outlet river. Both are ends of the kind T2R-22 removes elsewhere (classes (b), (f) and (c) of T2R-6c). Rule C leaves neither and removes more river length.

**Terrain.** A removed river or lake is not in the graph, so the operator does nothing there and the ground is the natural ground, as in a world without rivers. The valley the land has stays. It has no bed, no banks, no floodplain, no cut through rises and no water. The bed of a removed lake is a dry hollow at least 2 m deep. It is plain land, not the dry floor of a closed basin: no dry run, no flag for T4. A dry riverbed, the carve without the water, is not built (RQ3).

**Mass balance.** `sum of r over land = sea + Lung + strip ends + sites + evaporation in basins`. "Sites" is the accumulated runoff that arrives at edge cells, the whole inflow of site-drained basins included. Nothing is added and nothing is scaled. Rain on keep-out cells is not counted, as today (they are not land). The bake and its balance are the same under rule L and rule C; the rules differ only in what is drawn. Under rule L a kept tributary carries its water to its Cut end, and from there on that water is part of the site term.

**Rivers below a site.** There is no rule and no resizing step. R on a cell is the runoff of the cells that drain to it, and water that left at a site is not among them. Today the barrier turns that water round the site. After T2R-23 a river below a site carries only its own catchment: its section is the law of RS3 for that discharge, with no floor, and where the catchment is under the channel threshold there is no mapped river until the threshold is reached. Everything that is removed ends at a site, so nothing that is removed has a receiving river, and no river on the map loses a tributary while keeping its water in the balance.

**Overflow rivers (T2R-22).** An overflow lies inside a closed basin, and a closed basin never drains to a site (its cells have terminal Basin). So neither rule removes an overflow lake or an overflow river, and removal creates none. (c'): a lake whose exit flows into a river that is removed gets no drowned channel through its exit, because the second pass finds no channel cell there; the lake's channel ends one cell short, as at a pit (R4). What does change is the ground T2R-22 was measured on. With the keep-out at 300 m and the edge cells as outlets, valleys that the barrier closed now drain, so the closed basins, the overflows and the Dead stems near sites differ. T2R-22's reference hashes are taken before Part B and are not expected to hold after it; Part B prints T2R-22's report lines again.

**Measurement table** (T2R-23 Part A; per seed; columns: rule off at 650, `branch` at 300, `catchment` at 300, `branch` at 0):
- keep-out cells and edge cells;
- site-drained basins: count, cells, the inflow they pass on, and what share of the reference run's lakes and flooded cells they were;
- lakes with a wet cell beside an edge cell that stay: count, and the smallest distance from such a wet cell to a site shape;
- removed rivers: stems, km and share of the river length, by reason; discharge at the end p50, p90 and sum; width p50, p90 and largest (the law of RS3);
- sites with at least one removed river, with Wall Engines and the Lung counted apart;
- rule L: Cut ends (count; discharge and width p50, p90 and largest; distance along the removed river from the Cut end to the edge cell p50 and p90) and lakes that lost their outlet river (count; inflow p50 and largest);
- rule C: the stems and km it removes beyond rule L;
- rivers left: stems and km; river ends by class (the rows of T2R-6c plus Cut);
- runoff shares: sites, the sea, closed basins, the Lung and strip ends; the mass error;
- lakes; the flooded share of land; lakes above keep-out ground (must be 0);
- centreline samples closer than influence radius + 300 m to a site shape (must be 0 at 300), the stems they lie on, the smallest clearance; guard: nodes moved and the largest move.

**Report** (always, from Part B): edge cells; site-drained basins; removed rivers (count, km); Cut ends (rule L); the runoff share that leaves at sites; lakes above keep-out ground.

**For T3.** No river ends at a site, so E5 needs no rule for one. Rule L: `RiverTerminal.Cut` is a river end with no receiving water. The exclusions of E4 are unchanged (R12).

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

### RS5. The intake structure (T2R-24): dropped

Dropped with T2R-24 by the user's decision of 2026-11-13. Nothing is built at a site for a river. The earlier text is in the history of this file (commit 24d44bd).

## Notes for T3 and T4 (for the primary to apply; not edited from here)

- **T2.md S8b**, "Stems and terminals" and "For later phases": a closed lake can have an outlet (RS1). T3 receives closed lakes with inlets and, where they overflow, an outlet. D6 and S6 lose the floors (RS3). The "Order and parallelism" table and Phase acceptance item 8 gain T2R-19 to T2R-25 (T2R-24 dropped) and C2c.
- **T2.md D2 and S8** (revised 2026-11-13 for RS2): the keep-out is the corridor radius alone (300 m proposed, RQ1) plus half a cell diagonal; its edge cells are outlets where water leaves the map; a river that runs into one is not mapped and a basin that spills into one holds no lake. S8 "Barrier cap": no lake stands above keep-out ground any more, and the site rule it names (no water and no plug band on a site shape) is checked by `river_carve_invariants_test` (T2R-23 validation 5). F45 and F49 then describe the state before T2R-23.
- **T3.md Spec E2**: `d = channel_depth Kd Q^0.4` without `max(1.2, ...)`; width without the 8 m floor. The T3R-3 constraint "T2's floors" and the `river_danger_test` bars "8 to 400 m, at least 1.2 m" become "(0, 400], above 0". The E3 rows are re-recorded from T2R-20's report.
- **T3.md Spec E5** (T3R-4 is not built yet): the head factor of RS4 on source stems; the outlet rules of a spilling lake apply to an overflow lake. No river ends at a site, so there is no intake terminal and no rule for one. With rule L of RS2 there is a new terminal `RiverTerminal.Cut`, a river end with no receiving water; with rule C there is none.
- **T3.md Spec E6**: wading is "depth above 0 and below the swim entry depth". With RS3 there is now mapped river in that band.
- **T3.md Spec E5**, added by the T2R-22 Part A review: a lake can hold strait stems (RS1 "Straits"), which start and end in the same lake and are at its level throughout; they are neither inlets nor outlets. An overflow outlet can drop a whole metre into the next lake right where its hold ends (R29).
- **T3.md Spec E5**, added by the T2R-22 Part B review: where a lake's water runs straight into a passing river, the river stands at the lake's level at the junction and falls to it just upstream (item 8f).
- **T4**: overflow rivers cross dry basin floor, as channels already could (S8b). No new query. The valley of a removed river and the bed of a removed lake are plain land with no flag (RS2 "Terrain").

## Open questions for the user

No minimum, floor, widening or boost is proposed anywhere in this file.

1. **Keep-out (after T2R-19's table).** Which width: 650 m (today), 300 m, or a design order for 0 m? The table also shows how much of the ring's river water ends at sites. If that share is large, is it still what you want? **Closed 2026-11-13: replaced by RQ1 below.**
2. **Which structures take water in.** The rule as written applies to emitter sites, Wall Engine forecourts and the Weather Lung alike, because the same straight banks stand at all three. Should the Wall Engines and the Lung get the same intake, or be left as they are until you have seen the emitters? **Answered (user answer 1); the answer carries over to the removal rule (RQ5).**
3. **The intake's look.** The placeholder is a front wall with ports and a deck over the river's end, as wide as the river. Wide rivers give long structures: T2R-19 prints the widths. Is that the right idea, and do you supply the final model or should a later order build it? Beyond 6 km the structure is not drawn. Is that acceptable? **Closed 2026-11-13: no structure is built (T2R-24 dropped).**
4. **Head taper (after T2R-21's pictures).** Is 1.5 km right, and does the head read as a spring? Longer makes it gentler; shorter makes it blunter.
5. **Wading.** With D6 lifted, water between 0.55 and 1.15 m deep still switches the controller to swimming, with the feet on the bed. Leave that, or raise the swim entry depth so such rivers are walked? That would also change ocean and lake shores.
6. **Existing clamps that stay.** None of them makes water where there is none, but each is a limit on a river's shape: bank width at least 2 m and at most 12 m; floodplain at least 10 m and at most 120 m beyond the bank; meander wavelength at least 300 m; meander amplitude at a quarter for streams under 14 m; river width at most 400 m with the depth raised to carry the discharge (T3 calibration); gradient at least 1e-4. Should any of them go?
7. **Ponds beside rivers.** (c') joins a lake to a passing river only where a river ends in that lake. A pond with no mapped inlet that spills into a river stays without a drawn outlet, since its outflow is below the mapped threshold. Acceptable?

Not asked, only reported: the flooded share of land will move when the dammed lakes go (they are 4.5 % of the lake area on the play-test seed); `E0` is left alone unless the share leaves 6 to 10 %. Zero to two rivers per ring will still end on a dry pan because their water really evaporates there.

### After the removal decision (2026-11-13)

For the rewritten T2R-23 and RS2. No minimum, floor, widening or boost is proposed in any of them. RQ1 and RQ2 are needed before T2R-23 Part B; Part A brings the numbers and the map crops for both.

- **RQ1. Keep-out: is 300 m near enough?** Proposed: 300 m, which is how far a river's carve reaches. The edge of a river's valley can then touch the edge of a site's shaped ground and neither changes the other. A river then passes no nearer than about 3.3 km from the centre of a regular emitter (6.3 km for the main emitter, 8.7 km from a Wall Engine's forecourt line), because the site's own shaped ground reaches 3 km (6 km, 8.4 km). Today the nearest is about 3.9 km. A true 0 gives 3.0 km, saves no rivers (T2R-19: -2 to +8 per ring) and needs a design order across the guard, the operator and the site shaper. If you want a river visibly next to the pad, that is a different piece of work, rivers on a site's skirt: say so and the lead scopes it.
- **RQ2. Tributaries of a removed river.** Your rule keeps them. They then end where they met the removed river: on dry ground, at full width, some kilometres before the site. A lake on a removed river likewise keeps its inlets and loses its outlet. Both are ends of the kind the overflow work (T2R-22) removes elsewhere. Part A counts them and shows them on map crops. The choices:
  - (L) as you said: tributaries stay and end there;
  - (C) everything that drains into a site goes: no such ends, more river length removed;
  - (L with a taper) tributaries stay and dwindle to nothing over their last stretch, the mirror of the head taper. A look choice that shows less water and adds none; a follow-up order.
  The lead will recommend on the numbers: C if rule L leaves hundreds of such ends per ring, L if it leaves few.
- **RQ3. Dry valleys.** Where a river is removed the ground is left natural: the valley the land has, with no bed and no banks. The alternative is a dry riverbed (the carved channel without water), which reads as a river that was drained; it needs operator work, a ground material from T4 and a `CarveOperator` bump. Natural ground is treated as accepted unless you say otherwise.
- **RQ4. Lakes beside a site.** A lake that would spill into a site is removed. A lake that only lies against a keep-out and spills somewhere else, or not at all, stays: its level is not above site ground and its shore is natural. Part A counts them. Removing those too would also remove outlet rivers that flow away from the site, and the rivers below would then either carry water with no visible source or lose it. Keeping them is treated as accepted unless you say otherwise.
- **RQ5. Which structures** (not a new question). User answer 1 carries over: emitters, Wall Engine forecourts and the Weather Lung ring all remove water by the same rule, "for now". T2R-25 revisits it with pictures.

### After the T2R-22 Part A review (2026-11-13)

No minimum, floor, widening or boost is proposed in either.

- **RQ6. More outlet rivers than forecast.** The forecast was 300 to 450 new outlet rivers per ring. The bake gives 1,085 to 1,565, because every lake that is full and gathers 100 km^2 or more gets a river out, also where no mapped river runs in. About half of the added length is under lake water; the river you can see grows by about 3 %. The lead recommends keeping all of them: a full lake of that size has an outlet. The alternative is an outlet only where a mapped river ends in the lake (about 300 per ring); the other full lakes then pass their water on unseen, as today. Map pair, seed 876374626, around stem 9943: `.godot/captures/landscape/t2r/overflow/stem9943_50km_pair.png` (before left, after right; thick cyan is a river that starts in a lake) and `stem9943_120km_pair.png`. Part B does not wait for the answer (item 8e).
- **RQ7. Outlets run in cuts (asked with the stills of validation 7, not before).** On the test strip the ground between a lake and the next one stands a median 55 to 70 m above the lake where the outlet crosses it, and the outlet is carved through. This is so at the outlets of spilling lakes today (92 on the strip) and at the new overflow outlets (27). The order adds about a quarter more of them. Accept this for the batch and have the lead scope a cure (the outlet's path and the lake's sill taken from the fine terrain), or hold the overflow rivers back until then? **Whole ring (R31, seed 876374626):** 1,089 overflow outlets with a median largest cut of 55 m and 5,638 spilling outlets with 64 m; two of three have ground over 40 m above the lake. Stills: `.godot/captures/landscape/t2r/overflow/stills/` (o1 and o2 are the two largest cuts, 491 and 347 m; item 8h adds two near the median). The lead recommends accepting it for this batch and ordering the study of "RQ7: what a cure would be" in the Part B review; raising lake levels to the fine sill is not recommended.

Reported, not asked: about 2 to 3 % of a ring's runoff leaves at sites (T2R-19), and rivers below a site are smaller than today because they no longer carry the water the barrier used to turn round it. Nothing is topped up. The removal test is made on the 1 km grid, so a river whose true course would pass up to 1.4 km outside the keep-out line can be removed (RS2).

## Phase acceptance (additions to T2.md)

9. The rule orders of 2026-11-13 are done: T2R-19 to T2R-23 pass their validation, including `drainage_overflow_test` and `drainage_site_intake_test` (headless); T2R-24 is dropped. T2R-25 has reported: on three seeds no river ends in a full pool without an outlet, no lake stands above keep-out ground, no river ends at a site and no River or Lake sample lies inside a site shape, no section has a floor, and every source tapers. The user has answered open question 4, RQ1 and RQ2, and the head taper constant is no longer marked provisional.

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

**Done by the lead on 2026-11-13.** Where each point is answered:
- The rule and its terms ("intersects" for a river and for a lake, the keep-out of 300 m and its consequences for the guard, R10, R11 and R12): Spec RS2. The order: T2R-23, in two parts with a user checkpoint.
- The removed water: it leaves the balance at the site as a fourth outlet term; nothing downstream carries it and nothing is topped up (RS2 "Mass balance", "Rivers below a site").
- The valleys: natural ground, uncarved, no dry bed (RS2 "Terrain", RQ3).
- Overflow rivers: untouched by removal (RS2 "Overflow rivers").
- T2R-19's code: the bake part is reused whole (edge cells as outlets, `KeepOutMeters`, the guard on edge cells) and becomes the default. `RiverTerminal.Intake` stays as a value the builder uses and never stores. Not built from the withdrawn order: the end node rule for Intake stems, Intake in the graph file, the intake mark on the maps. The intake stem width lines of the T2R-19 report go.
- Two readings of "rivers flowing into that branch upstream stay" exist and change the map; Part A measures both (RQ2).

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

## User answers 2026-11-13 to T2R-23 RQ1-RQ3

- **RQ1 keep-out:** 300 m.
- **RQ2 tributaries:** rule **C**. Everything that drains into a site is removed, tributaries included. Lakes that were connected to removed rivers (and do not touch a site themselves) stay as plain lakes, "lakes that have not formed a river yet": no inlet or outlet river.
- **RQ3:** natural ground where water was removed; no dry riverbed.
- RQ4 and RQ5 stand as written (lakes beside a keep-out without spilling stay; all site kinds for now).
Part A can still measure both readings for the record, but Part B implements C.

**User 2026-11-13 (T2R-21 picture gate):** the head taper at 1,500 m is accepted "fine as is", including the thin first stretch that reads as a sand strip with water patches. `HeadTaperMeters = 1500` is final. Adapted test bars accepted by the primary: wet width against tapered width x untapered ratio at 95 %; source-count floor 50 on the strip.

**Decisions 2026-11-13 on the T2R-22 Part A review.** RQ6 (user): keep all overflow outlets (every full lake whose catchment reaches 100 km²); item 8e is not needed. Primary: the 8b tripwire changes in `river_carve_invariants_test` are accepted. The resident-graph tripwire is set to 44 MiB, with the 32 MiB savings as an optional separate order. RQ7 is asked with the Part B stills.

**Decisions 2026-11-13 on the T2R-22 Part B review.** Primary: 8f and 8g accepted. RQ7 (user): accept the outlet cuts as terrain features, with **no study**. T2R-26 is not ordered and item 8h (median stills) is dropped.

**T2R-22 closed 2026-11-13 (primary).** 7/7 tests pass (run 20261113-162927); fill arithmetic bit-equal; Dead stems 4 / 4 / 1; classes (f) and (c) are 0. 8f escalations accepted for this batch: 15-28 receivers lowered per ring, and falls over 25 m on 0-3 junctions per ring (largest 66.3 m, seed 876374626, receiver stem 7947). These are the same grid-versus-fine mismatch the user accepted for outlet cuts; listed for T2R-25. The 8g strength line (47 of 326 sections keep full strength) is accepted as a tripwire. Lake stems still ending in higher water (21 / 19 / 15 per ring) are listed for T2R-25.

## Play-test 2 (2026-11-13, user world seed 713263263): water findings and decisions

Evidence: `.godot/captures/landscape/t2r/playtest_lakes/`. Lead findings:
1. **Banks stand out of lake water** (lake 20792, ARC 31122.7 / ACROSS 135.1). Inside its own lake a river drops its bank and levee bounds only where `P_own > 0.25` (`Smoothstep(PlugBand, 2*PlugBand, P_own)` in `RiverCarveOperator.Evaluate`), and `LakeDepthRunMeters = 2` keeps banks in shallows. Fix (PT2-1): fade over the plug band, `Smoothstep(0, PlugBand, P_own)`, and set `LakeDepthRunMeters` to 0.3.
2. **Drowned channel looks like a river band through the lake** (lake 20722, ARC 16190.5 / ACROSS 148.5). Fix (PT2-2), user decision: river water held at its own lake's level, inside that lake's membership, is reported as Lake for appearance (colour, clarity, ripples). The reach stays available for T3 flow. Edit the T3.md note on "river-kind water along its drowned channel" to match.
3. **One-frame terrain hole at the altitude level releases** (~550 m and 1,100 m above ground, T2R-18): handed to the streaming lead.
4. **Inland water morph restore** (`BuildRawSector`, about :4060). Lakes and rivers are shifted +0.3 m / -0.5 m against their bed in the 4 m and 8 m morph bands. Fix (PT2-3) after a levels still confirms it: no restore terms for Lake and River vertices; `LodSectorCacheRiverVersion` 12 -> 13. Waits until the streaming lead is done with the proxy files.
5. **Binding-16 texture errors during a world switch:** fixed in d7b8169 (precipitation occlusion global reset).
User decisions: drowned channel looks like the lake (PT2-2). Pale lakes over sand, lake vs sea colour (keep their own colour) and the dark ground around lake 20722 are all left for the biomes work. River ripple drift direction waits for T3 flow.
PT2-1 and PT2-2 share one `CarveOperator` bump (9 -> 10); the user's world is recreated once.

**User re-test 2026-11-13:** PT2-1 (banks in lakes), PT2-2 (lake channel colour) and the binding-16 world-switch fix (d7b8169) all **pass** in game.

**User re-test 2026-11-13 (evening): all points pass.** The re-test covered:
- streaming stutter;
- the altitude hole and flipping;
- rain occlusion gating and the speed freeze;
- the tools/play.ps1 launcher;
- inland water morph (PT2-3).

The user: "performance is largely back to what it was before, hitches are almost gone". T2R-25, T2R-14, T2R-16 and the C2c gate run are still open; the user moved on to T3.
