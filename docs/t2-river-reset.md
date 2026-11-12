# T2 rivers and lakes: reset and re-plan

Date: 2026-11-11. Author: Fable 5.2, at the user's request. Branch `terrain-vegetation-revamp`, HEAD `0cce3a6`.
Supersedes: section 3.1 of `docs/terrain-vegetation-revamp-plan.md` where the two differ, the T2 sections of `docs/terrain-vegetation-handoff-2026-11-10.md`, and the previous `docs/work-orders/terrain-vegetation/T2.md`.
Executed through: `docs/work-orders/terrain-vegetation/T2.md` (rewritten with this document).

## 0. Summary

- T2 produced measurements and 54 study files (about 29,000 lines), and no production river code. The cause was structural: a chain of acceptance criteria that could only be met by first solving the hardest problem exactly.
- The reset changes the method from **fitting** to **constructing**. The study looked for water levels and sections that the existing terrain already contains, and tried to prove the fit against the 2 m lattice. The new operator builds the bed, the banks and the lake rim, so containment holds by construction and the bake only has to choose levels that keep the construction small.
- The bake samples heights **on the GPU** with the existing GLSL height model, keeps a CPU path as the deterministic reference, and takes the lowest of 16 samples per cell so lake levels are safe by construction.
- Straight runs are fixed where they are visible, in the **path** (node placement, a smooth curve, wander and meander), and measured with the criterion the user approved.
- Acceptance is by sampling with stated tolerances and pictures. Every work order has a stop rule.
- First visible result: a whole-ring river map from production code at T2R-4. First water in a review world at T2R-11.
- Three decisions are asked of the user in section 10. Each has a default, and work can start on the defaults.

## 1. What happened, and why

T0 and T1 went well: bounded goals, a test per goal, a picture per result. T2 began the same way. The first study (seed, wrap, walls, breach, maps) took hours and produced the maps the user chose from. Then four things combined.

1. **The acceptance chain ran backwards.** The routing decision (D4) was redefined as "judge actual river paths", which is right. But it was written so that only a *complete native candidate* (final bed, banks, joins and lakes on the 2 m lattice) could be scored. T2-1 could not be accepted without D4, T2-2 could not be accepted without T2-1, and no implementation order could start before T2-2. The first line of production code therefore waited for a full solution of the geometry.
2. **The geometry was posed as an exact fit.** My plan said the carve "only lowers ground" and "keeps banks above water". With levels taken from a 1 km grid and ground that varies by tens of metres inside a cell, those two rules together demand a water level that the natural banks already contain at every point. That is a feasibility search, and some sections have no solution: the study proved one (depth and bank bounds 5.4 mm apart at bend 480332). Lakes were posed the same way: find a contour or a rim on the existing ground. The strict-saddle and contour screens left 60-77 % of lakes unresolved.
3. **Each failed fit was answered with a stricter proof, not a looser design.** Byte-exact replays, frozen source hashes, PDB identity checks and independent reviews were applied to study code. Review effort grew while the question stayed the same. The last step tried to bound the engine's noise arithmetic so that terrain reads could be skipped, which led to tracing native instructions.
4. **The bake budget was attacked on the CPU.** Sampling is 26 s of the 40 s bake. About a dozen exact micro-optimisations brought the coarse core to 18.5 s, still over the 15 s study threshold, while a parity-tested GPU copy of the height model sat unused.

No single step was unreasonable. What was missing was a rule that says "stop, this requirement is ours, change it". The self-inflicted requirements are listed in section 3. The work orders now carry stop rules (section 6).

## 2. What stands

**User decisions (unchanged).**
- Lake-rich direction, shallow cuts, a 40 m excavation cap. No fixed lake percentage.
- "Actual river paths": the flagged straight-run share falls by at least 50 %, overall and coastal, against the frozen baseline (20.7150491 % and 37.1484665 %), with fewer flagged kilometres and no regression in sharp or alternating bends.
- New worlds only, behind `water.rivers`. Generation 1 stays byte-identical.
- T3: current, danger from calm to torrent with the plan's targets, displacement and never harm.

**Measured facts carried forward.**
- CPU sampling dominates the bake (26 s of 40 s at 2x2 on the 4070). Priority-flood, labelling and routing cost about 8.5 s optimised and about 14 s in the normal Debug build.
- The straight runs are on real slopes: 96 % of flagged length is on strictly descending edges. No flat resolver changes them. Barnes, near-tie, facet, valley-corridor and global routing all missed the halving target on the grid.
- Basins are deep (p90 163 m). With a 40 m cap the cut-only lake bound on the 2x2-mean grid is about 10.6 % of land; the measured result was 12.7 %.
- The fine terrain inside a 1 km cell differs from the cell mean by tens of metres in hill country. Any level taken from cell means leaks at 2 m. (This is what the containment probes found: 187 of 200 hard river edges with a bank deficit, 85 of 100 lakes needing more than a 40 m rim raise on cell faces.)
- Exact and worth keeping: interior-ocean pruning (-18 %), batched neighbours, parallel independent scans, flat-crossing repair (22,455 crossings removed, identities preserved), the synthetic fixtures (bowl, seam, blocked diagonals, shelf).
- The integration reviews of D9, D10, D12 and D14 were done against the source and are sound. Section 4 adopts them, minus the estuary and "native" parts.

**The GPU kernel that already exists** (`materials/terrain_gpu/ring_height_model.glslinc`, host `scripts/Terrain/Generation/Gpu/ProxyGpuHeightKernel.cs`).
- It evaluates the *natural* height (no site operators), which is exactly the drainage height source, plus the regional climate (moisture).
- It takes lattices or explicit point lists at integer-metre positions, with offsets up to 2^23 m from an anchor.
- Measured on the 4070 (docs/gpu-offload-plan.md, 2026-11-06): about 2 ns per height sample in batches, about 3 ns with climate. Parity against the CPU model: p99.9 within 2 cm, worst case 0.25 m at terrace risers.
- Derived: a whole ring at 1 km with 16 samples per cell is 160 million samples, well under a second of GPU time on the 4070. The A1000 is unmeasured; a few seconds is the estimate.
- It needs a RenderingDevice, so it does not run under `--headless`. Tests that need it use the manifest mode `gpu-local`.

## 3. Requirements that were ours, and what replaces them

| Requirement | Where it came from | Verdict | Replacement |
|---|---|---|---|
| The carve only lowers ground | My plan, 3.1 | **Drop** (needs the user's nod, Q1) | The operator may build banks up. Bank fill is reported and kept small by the choice of level |
| Levels and sections must fit the "native" 2 m lattice with packed replay | T2 study | **Drop** | The operator defines the bed and banks. Tests sample the result |
| Lakes need a constructed contour, owners, a single-outlet quotient and a crest sweep | T2 study | **Drop** | Lake level is conservative by construction (section 4.4). Free shoreline. Plugs only on the dry ring |
| D4 can only be scored on a complete native candidate | T2-1 escalation | **Replace** | Score the production centrelines from the graph. No carve is needed to score a path |
| CPU and GPU heights must agree to the bit for the bake | Never stated; assumed | **Drop** | Each world keeps its own graph file. The CPU bake is the reference for goldens |
| 15 s study threshold on 16 threads; 20 s cold gate on the A1000 | T2-1; plan 12 | **Replace** (Q3) | Target 20 s, gate 30 s on the A1000, once per world, asynchronous with progress |
| 16 MB resident graph | Plan 12 | **Relax** | Target 20 MB, gate 32 MB |
| +0.2 us per sample near rivers | Plan 12 | **Relax** | +0.5 us near rivers (the base model costs 1.2-2.8 us). +0.03 us elsewhere |
| No inland ground at or below 0 outside the estuary | Plan 3.1 | **Replace** | No dry ground below 0 and no Ocean water where the natural ground is above 0 |
| Manufactured estuary ownership, River at +0.0, Ocean owns the seam | T2-2 draft | **Drop** | The river keeps its kind down to the natural coastline; beyond it the sea rule applies |
| Wet-area mips with a 5 % area proof against a native reference | T2-2 draft | **Drop** | River line coverage from the polyline; lakes through the existing point classification |
| Frozen source hashes, PDB identity, MVID checks, independent replays for study runs | T2 study practice | **Drop** | Determinism is one hash compared across two runs and across worker counts |
| Partial drawdown, incremental reflood, compact queue, ocean-midpoint skip | T2 study | **Drop for now** | Not needed with GPU sampling. Kept in the archive |

## 4. The design

### 4.1 Principle: construct, do not fit

Three rules carry the whole design.

1. **Conservative levels.** Every water level the bake chooses is at or below what the real fine terrain can hold. For rivers that is a running minimum of the ground along the path. For lakes it is the spill level of a grid whose cell value is the *lowest* sample in the cell.
2. **Constructed containment.** The operator then clamps the ground between a lower bound (bank, levee, lake plug) and an upper bound (bed, valley side). Wherever the fine terrain disagrees with the bake, the operator fixes it locally. A leak is impossible by construction. A large fix is a visual problem, reported and tuned, never a correctness problem.
3. **One function per quantity.** Level, bed, wetness and corridor weight each come from one function of position and footprint, shared by the height model, the sampler, the water mesh, the far tiles and (in T3) the flow.

### 4.2 The bake

Stages, with the 4070 measurement or estimate and the A1000 estimate. Only the first number in each cell is backed by a measurement of similar work.

| Stage | What it does | 4070 | A1000 (est.) |
|---|---|---|---|
| S1 Grid | 31,416 x 318 cells of about 1 km. 4 x 4 natural-height samples per cell on a 250 m lattice, moisture at the centre. Output per cell: lowest height, index of the lowest sample, moisture | under 1 s on the GPU (derived from the K1 timings) | 2-4 s |
| S2 Barriers | Rasterise site, Wall Engine and Weather Lung footprints with clearance. Rim walls. Wrap at the seam | about 0.3 s | 0.5 s |
| S3 Conditioning | Priority-flood on the valley-floor grid plus a low routing noise. Least-cost breaches up to 40 m and 32 cells, cheapest first; `lake_abundance` is the share left uncut. Final flood. A depression at least 2 m deep is a lake; a shallower one is routing fill | 5-6 s (measured for the same passes, Debug) | 8-10 s |
| S4 Routing | Steepest descent; flood parents inside filled cells; crossing repair; accumulate area and runoff | 3-4.5 s (measured, Debug) | 5-7 s |
| S5 Graph | Channels above the threshold, reaches, centrelines, fine level profiles (GPU point samples along the paths), lakes, hydraulics, index | 2 s (est.) | 3-4 s |
| S6 Pack | Serialise, hash, write | 0.5 s | 1 s |
| Total | | about 12-14 s | about 20-26 s |

**Grid statistic: the lowest sample, not the mean.** A fine path through a set of cells cannot stay below the lowest sample of every cell it crosses, so a spill level computed on lowest samples is a lower bound of the true spill level, up to gaps narrower than the 250 m sample lattice. This is the property the study was trying to prove after the fact. It also makes the grid a valley-floor map, which is where rivers should run, and it needs shallower cuts for the same drainage.

Consequence for the picture the user approved: the lake map changes. Expect lower lake levels, less lake area than 12.7 % and longer rivers at the default. `lake_abundance` (the share of feasible breaches left uncut) moves the result back towards lakes without touching the 40 m cap. The first milestone shows the new whole-ring map and the measured share (Q2). These directions are expectations, not measurements: no run with the lowest-sample statistic exists yet.

**Routing noise.** The routing height is the valley-floor height plus a smooth noise of about 6 m amplitude and 5 km wavelength (seeded by the recipe). On hills it changes nothing. On the coastal plains (slope p50 0.2 degrees) it is comparable to the slope, so flow lines bend and merge and the parallel fans seen in T0 turn into branching networks. It is a starting value: one work order measures it with the amplitude at 0 and at 6 m and keeps whichever map is better. It is not what the D4 criterion rests on (4.5). The noise steers the flood order, the breach search and the flow directions only. A lake's level is always the lowest *unperturbed* value among the cells that border its flooded set, so the conservative property above survives the noise.

**Height source.** `IDrainageHeightSource` has two implementations: a GPU one (a small compute shader that includes `ring_height_model.glslinc` unchanged and writes three values per cell) and a CPU one (the natural `TerrainGenerationHeightModel`, as the site registry uses). Both sample the same positions. The contract from T7 stands: later generations add no relief term to this source.

### 4.3 The river operator

**Centreline.** Each channel cell contributes one node at its lowest sample, moved by a small hashed jitter. The curve is a uniform quadratic B-spline through the nodes: C1, inside the control polygon, closest point in closed form. Two displacements along the normal are added as functions of arc length: a *wander* (kilometre scale, a few hundred metres, strongest on low gradients) and a *meander* (wavelength about 12 widths, amplitude about 2 widths, fading to zero on steep reaches). Total lateral displacement is capped at 350 m so neighbouring channels cannot meet and barrier clearance holds.

**Level.** The bake samples the ground every 50 m along the final centreline and along both bank lines (GPU point lists). The profile W(s) is the running minimum, from the sources downstream, of the lowest of the three minus an incision of about 0.6 m. At a confluence the outgoing level is the lowest incoming one. Where a river meets a lake or the sea, the level is the higher of W(s) and the receiving level, so the mouth is drowned and the surface is continuous. The profile is stored as a few knots per kilometre, never above the fine profile.

The displayed level is W(s) rounded down to whole metres, with a chute of at most 10 m at each crossing: flat pools joined by short chutes, as the plan asked, and the pools sit on a ring-wide metre datum like the plateau benches do. Chutes need no storage; they are where the profile crosses an integer.

The running minimum also realises the breaches. A path that climbs over a sill keeps its upstream level, so the operator cuts the gap. No cut heights travel from the bake to the runtime.

**Section.** With t the distance from the channel centre, a the half width, L the level:
- channel (t under a): the bed, L minus the depth at the centre, rising to a shallow edge;
- bank (a few metres): up to L plus a freeboard of about 0.4 m;
- beyond the bank the natural ground is **clamped between two cones**: not above a valley profile (flat floodplain, then a side slope, with 40 m strata steps where the cut is deep), and not below a levee profile that falls away from the bank top;
- the whole displacement fades to zero by 300 m from the centreline, so the result is C0 with the surrounding ground.

Where several reaches overlap, the upper bounds combine by minimum and the lower bounds by maximum, and a channel always wins over another reach's levee. That one rule makes confluences. In the last stretch of a tributary the level is read from the main stem, so the surfaces meet exactly.

**Coarse samples.** Every river query takes a footprint (the sample spacing, 0 for a point). The channel is widened to at least 1.5 footprints and made proportionally shallower, so a lattice of any spacing has wet vertices and the level is unchanged. Above 64 m the channel shaping fades and only the valley remains.

**What is reported, not proven.** The cut depth along paths (share over 40 m), the bank fill (share over 3 m, maximum), the level step between adjacent wet samples. Starting tolerances are in the work orders. A number outside its tolerance goes to the user as a picture with a proposal.

### 4.4 Lakes

- **Level.** The lowest valley-floor value among the cells that border the depression's flooded set, after breaching. On an unperturbed grid this is the flood level.
- **Region.** The flooded cells (lowest sample below the level). Their neighbours are dry cells: all 16 samples at or above the level. Priority-flood guarantees that flooded cells of different levels, and flooded cells and the sea, are never neighbours.
- **Water.** A point is lake where its ground is below the level and a smooth membership weight (interpolated between cell centres: 1 in flooded cells, 0 at the centres of dry neighbours) is above zero. The shoreline is therefore the real contour of the fine terrain. Islands stay dry.
- **Plug.** Along the line where the weight reaches zero, the ground is clamped to at least the level plus a freeboard. Every sample on that ring is already above the level, so the clamp acts only in gaps narrower than 250 m. It looks like a small natural sill or check dam. The count and heights are reported.
- **Outlet and inlets.** The outlet river holds the lake level until it has crossed the ring, then steps down. An inflowing river drowns into the lake through the "higher of the two levels" rule. Inlet and outlet points are stored for T3.
- **Barriers.** Barrier cells that border a lake count as bordering cells in the level rule, so the ring is dry there too and stays at least 650 m outside the real site footprint. The outlet profile absorbs the difference.

No contour construction, no owner geometry, no single-outlet quotient (a depression has one spill by definition), no crest sweep.

### 4.5 Paths and the D4 criterion

The approved criterion is measured on the centrelines stored in the graph, on the same 2,000 km strip, at the same 50 km^2 threshold, with the same 6 km windows. A small evaluator in production code (`RiverPathMetrics`) does it; it is first validated by re-scoring the frozen baseline graph, which must reproduce 20.72 % and 37.15 %.

A window is flagged only when its chord is at least 99 % of its arc **and** it stays within 150 m of the chord. Wander alone (a few hundred metres over a few kilometres on low gradients) breaks both conditions, and those low gradients are where 96 % of the flagged length is. So the halving does not depend on any routing experiment. Wander is also the part that reads from the air; meander is the part that reads from the bank. Steep straight reaches stay straight, as they should.

Not chosen: D-infinity or multiple-flow-direction accumulation. On a smooth tilted plain they spread the flow so that channels form late or not at all, and the result still needs a single path extracted. Noise before D8, then a curve, is simpler and gives the user the kind of control a recipe field can express (`meander_strength`).

### 4.6 D1-D14 re-decided

| | Topic | Verdict | Decision |
|---|---|---|---|
| D1 | Grid and sampling | **Replace** | About 1 km cells, exact wrap. 4 x 4 samples, lowest value, node at the lowest sample. GPU source in the game, CPU source as reference. No smoothing |
| D2 | Barriers, outlets, seam, walls, moisture | **Keep** | Analytic footprints from the registries with clearance, rim walls, the isolated Weather Lung body, recipe moisture. A cell with any sample below 0 is sea |
| D3 | Conditioning | **Keep, simplified** | Priority-flood and least-cost breach, 40 m and 32 cells. `lake_abundance` is the share of feasible breaches applied. Lake at 2 m depth or more. No partial drawdown |
| D4 | Routing and path shape | **Replace** | D8 with flood parents and routing noise for topology. Shape from the centreline (4.3). Criterion measured on production centrelines |
| D5 | Graph, index, memory | **Decide** | Nodes, reaches, profile knots, lake cells. Index: one bit per cell plus 4 km buckets. Stable ids from the outlet cell and order. Gate 32 MB |
| D6 | Interim hydraulics | **Decide** | Plan 3.2 relations with a runoff scale chosen so the smallest mapped river is swimmable: pool depth at least 1.2 m, width at least 8 m. T3 recalibrates; tests check relations, never values |
| D7 | Carve | **Replace** | The constructive operator of 4.3. Bank fill allowed |
| D8 | Lakes | **Replace** | 4.4 |
| D9 | Sample fields and consumers | **Keep, minus estuary** | Additive float level with a validity flag; the int stays its floor. Classifier order: river or lake from the operator, then the sea rule, then none. River wins inside its channel where the natural ground is at or above 0. Edits reclassify on the interpolated height. Feature worlds use a v12 cache record with the footprint in the key; generation 1 files are untouched |
| D10 | Proxy routing and near water | **Keep** | River and lake sectors build on the CPU, selected by the union of consumer sample envelopes. Coarse levels use the footprint rule. Fallbacks are a ladder (4.8) |
| D11 | Far water and minimap | **Simplify** | Rivers: line coverage per far sample from polyline length times width, with a minimum so a river stays a line; glint; a whitewater input at 0 for T3. Lakes: the existing point classification. Minimap: polylines and the existing colours |
| D12 | Bake lifecycle | **Keep** | As reviewed: a feature-only wait before the world is attached, a worker lease with cancellation, immutable inputs captured on the main thread, no sampler needed to bake. Added: the GPU stage with CPU fallback, and the graph file as world data |
| D13 | Recipe fields | **Keep** | `river_density`, `lake_abundance`, `river_valley_width`, `channel_depth`, `meander_strength`; `river_ferocity` read-only at 1 until T3. Defaults are provisional until the T2 review |
| D14 | Hash, file, versions | **Keep** | A pre-graph input identity finds the file. The graph identity is appended to the runtime definition hash. `DrainageAlgorithm`, `CarveOperator` and the file format are versioned separately |

### 4.7 Determinism, the graph file and the two sources

- The bake is deterministic for a given source: the same hash across two runs and across worker counts.
- GPU and CPU heights differ by millimetres, so on flat ground the two sources can choose different cells in places. They are not required to agree. A `gpu-local` test reports how much channel length differs.
- **The graph file is world data.** It is written once, at world creation, to `<world>/hydrology/river_graph_v<n>.bin`, and never rebaked while it exists and validates. A world's rivers therefore never move.
- **The CPU bake is the reference.** Headless tests, the fingerprint row at the freeze and the generation test world (`g02-rivers-s1337`, created by the headless tool) all use it, so goldens are stable on every machine. A world created in the launcher bakes on the GPU. It can differ in small details from the reference world of the same seed (Q3).

### 4.8 Performance fallbacks, in order

Nothing below is built unless a measurement asks for it.

1. Cold bake over the gate: move the conditioning and routing loops to a small class library built with optimisations in every configuration (they use no Godot types; the normal Debug build runs them about twice as slowly). Then a bucket queue for the flood.
2. Coverage waits over river country: a GPU sector class for sectors wholly inside one lake (water where the height is below a level passed with the job; no carve). Lakes are most of the CPU-routed area.
3. Still waiting: the GLSL carve (plan T9b).
4. Gorges where the path crosses spurs (cut over 15 m on more than 10 % of length, or the review says so): snap the centreline to the valley floor with a lateral search over the bank-line samples the bake already takes.

## 5. Budgets

| Quantity | Target | Gate | Measured by |
|---|---|---|---|
| Cold bake, A1000, normal dev build | 20 s | 30 s (Q3) | `drainage_bake_benchmark` |
| Cold bake, 4070 | 12 s | 20 s | same |
| CPU reference bake | none | none; reported | same, `--source=cpu` |
| Warm load of the graph file | 0.3 s | 1 s | same |
| Main thread during the bake | no hitch over 100 ms | same | loading capture |
| Graph, resident and on disk | 20 MB | 32 MB | bake report |
| Sample cost near a river | +0.3 us | +0.5 us | `river_carve_invariants_test` printout |
| Sample cost elsewhere | +0.02 us | +0.03 us | same |
| GPU at a river pose, A1000 | | 0.3 ms, shared with T3 | A-B-A |
| Coverage waits on the river route | 0 | 0, or the ladder in 4.8 | travel benchmark |
| C# build time | under 40 s | | after the study files leave the build |

## 6. Testing and stop rules

- **Production first.** Every work order lands production code or a test of production code. There are no study-only tools. A number that decides something is printed by the production bake report.
- **Sampling, with tolerances.** An invariant is a test over a fixed, deterministic sample set that passes when at least 99.5 % of samples pass and no sample exceeds a stated hard limit. The failing samples are listed with coordinates so someone can look at them. Exact equality is used only for determinism hashes and generation 1.
- **Cadence** (the user's, from the handoff): one to three tests per change with `-Tests` and a dry run first; affected compatibility checks at the end of a work order; `-Suite gate` once per batch. T2 has three batches: after the bake (map), after first water, and at the review.
- **Stop rule, on every work order.** A budget in watchdog periods (55 minutes each) and in attempts (one attempt is a build plus a test run). When either runs out, or an escalation condition is met, the agent stops and reports in at most 15 lines: what works, what fails, the evidence path, and one proposed next step. It does not add tools, probes or acceptance criteria.
- **Escalation is short.** Implementer to `claude-lead` for one consult. The primary decides. `claude-deep` only with the primary's approval and a question that fits in five lines.
- **No new requirements from inside.** An agent that thinks a criterion is missing proposes it in its report. Only the primary or the user adds one.
- **Evidence.** At most five links per report. Pictures go to the user at the three milestones.

## 7. What changes in T3 and later phases

**T3** (edits made in `T3.md`):
- Work order ids: the old T2-n references map to T2R-n (table at the top of T3.md).
- The level is a function, `RiverLevel`, shared with the carve. Pools are whole-metre levels and chutes are where the stored profile crosses an integer. `RingWaterFlow` (E2, E6) and the chute query (E10, T3-11) derive from it; no chute list is stored.
- T2's hydraulics already guarantee a swimmable minimum (D6). E1 may rescale runoff, roughness and caps; if it changes width or depth the carve follows, which is free before the freeze.
- E3's "walkable bank" reads the operator's constructed bank and valley slope, not a measurement of natural ground.
- The estuary is River kind down to the natural coastline at level 0; E2's fade applies there.
- Calibration (T3-1, T3-3) runs on strips with the CPU source, and takes whole-ring numbers from the bake report. `river_danger_test` uses the 2,000 km strip on two seeds.
- The freeze uses the CPU reference bake for the fingerprint row and the test world; the graph file lives under `<world>/hydrology/`.
- Budgets in the T3 acceptance list follow section 5.
- The phase rules of section 6 apply to T3 as well.

**T4-7 (bed and bank materials).** Depends on T2R-10. `Diagnostics` gives it `InlandWater`, `ChannelOffset`, `RiverCorridorWeight`, the level, and a new `RiverGroundDelta` (negative where cut, positive where a bank or plug was built), so levees and plugs can be dressed as banks and deep cuts as rock.

**T5-11 (cobbles, bars, chute boulders).** Chutes come from the T3-11 query, which is procedural. Bars and cobbles can read `ChannelOffset` and the reach's width. Nothing else changes.

**T6-12 (riparian).** Lakes have a free shoreline and no stored outline, so there is no "distance to the lake shore". Use height above the water level inside the lake region and the corridor (`Diagnostics` level minus ground), which is the better driver for reeds and wet meadow anyway. Rivers still provide `ChannelOffset`.

**T7 (landforms).**
- The no-meso path: the bake's GPU shader includes `ring_height_model.glslinc`. When T7 ports its terms to GLSL they must be switched by a parameter word that the bake sets to zero, not compiled in unconditionally. The CPU source already has the contract.
- The corridor hook: `RiverCorridorWeight` is 1 in the channel, bank and levee band, on the low shore of a lake (up to a few metres above its level) and on the lake's dry ring, and continuous to 0 outside. T7's terms multiply by one minus it, which keeps plugs and banks intact.
- The footprint argument exists on every river query, as F31(a) requires.
- "The same rivers in a landforms world of the same seed" holds per source: same inputs, same graph.

**T9b (GLSL carve).** Still contingent, now third on the ladder in 4.8.

**Vastness plan.** No change. The far water line and glint arrive in T2R-12.

## 8. The study files

Recommendation, for the primary to carry out (nothing here was deleted or committed):

1. **(Superseded 2026-11-11: the user chose "just delete them". There is no archive commit or tag; the port-reference files are copied as non-compiling `.cs.txt` to `.godot/audit_baseline/common/t2/study_port_sources/`, see T2R-0.)** **Archive by history.** Commit the study as it stands in one commit on this branch (the 54 `tools/LandscapeDrainageStudy.*.cs` files, `tools/T2NativeAddressProbe.cs`, `tools/t2_native_address_probe.gd`, the prototype hooks, the old T2.md and handoff), and tag it `t2-study-archive-2026-11-11`. It stays readable with `git show`.
2. **Then remove it from the working tree** in the next commit, with the reset documents. The build drops from about 60 s to under 40 s.

| Group | Files | Action |
|---|---|---|
| Reference for the production port (read from the copies in `.godot/audit_baseline/common/t2/study_port_sources/`; do not keep in the tree) | `Grid.cs`, `Conditioning.cs`, `Routing.cs`, `FlatCrossingRepair.cs`, `IndependentScans.cs`, `DirectMoisture.cs`, and the synthetic fixtures inside `Routing.cs` | Port into `scripts/Terrain/Hydrology/` in T2R-4 and T2R-3, then delete |
| Optional later | `Drawdown.cs`, `IncrementalFlood.cs`, `FloodQueue.cs` | Archive only |
| D4 baseline and evaluator (under `.godot`, untracked) | `.godot/audit_baseline/common/t2/drainage/20261111_055421_5473896_33664/d4_physical_baseline/{graph.json,physical_baseline.json}` | Copy into `tools/fixtures/hydrology/d4_baseline/` (gzip the graph) so the laptop has it |
| Routing experiments | `FacetRouting`, `FacetDiffusion`, `ValleyCorridorRouting`, `GlobalTerrainRouting`, `NativeCenterHeight`, `OceanSampling`, `SamplingProbe` | Delete |
| Lake geometry | `LakeInventory`, `LakeQuotient`, `LakeContourScreen`, `LakeStrictSaddle`, `NativeLakeProbe`, `NativeLakeJoinProbe` | Delete |
| River geometry and probes | `ContainmentProbe`, `GeometryProbe`, `CellRouteProbe`, `PortalProbe`, `NativeRiverProbe`, `NativeSection*` (2), `NativeDryConnector`, `NativeOceanConnector*` (5) | Delete |
| Outlet-first family | `OutletFirstSpecimen*` (15) | Delete |
| Preflight and reporting | `ProductionPreflight*` (2), `D4PhysicalBaselineExport`, `Report.cs` | Delete |
| Native address probe | `tools/T2NativeAddressProbe.cs`, `tools/t2_native_address_probe.gd`, its manifest entry, the helper under `.godot/.../native-address-probe/` | Delete. Do not run the pending export |
| Prototype | `tools/LandscapeDrainagePrototype.cs`, `tools/landscape_drainage_prototype.gd` | Remove the study dispatch. Keep `--seed` and `--out-phase` if they stand alone; otherwise restore the file from HEAD |
| Evidence under `.godot/audit_baseline/common/t2/` (1.1 GB) | `design/` (1.3 MB), the frozen D4 baseline folder, the four maps the user saw | Keep these; the rest can go at the primary's discretion |

## 9. Risks

- **The new lake map disappoints.** Lower levels may give fewer lakes than the user liked. Mitigation: it is the first picture shown; `lake_abundance` and the lake depth threshold are the knobs; the 40 m cap is untouched.
- **Bank fill or plugs look artificial** in hill country. Mitigation: reported from the first bake; the level incision and the plug band are tunable; ladder step 4 reduces cuts and fills together. The ring is an engineered object, so a visible weir is in character where it is rare.
- **Slot gorges.** A 1 km link between two valley floors can cross a spur, and the running minimum cuts it. Mitigation: reported as cut depth along paths; ladder step 4.
- **GPU bake on a worker during loading.** Whether a local RenderingDevice can be created and driven off the main thread at that moment is unverified. Mitigation: T2R-3 checks it first; the fallback is small per-frame dispatches on the main device, or the CPU source with progress.
- **The A1000 is slower than estimated.** The flood is single-threaded. Mitigation: ladder step 1; the gate is asked as Q3.
- **Mid-distance river look.** Widening the channel to the lattice makes rivers read wider at 16-32 m spacing. Mitigation: judged in the fly-out at T2R-12, where the far line coverage takes over.
- **CPU and GPU graphs differ more than "small details".** Mitigation: the `gpu-local` agreement report in T2R-3; if it is large on flats, raise the routing noise (which removes near-ties) before anything else.
- **Design drift returns.** Mitigation: section 6. The primary should refuse any report that asks for a new proof instead of a picture.

## 10. Questions for the user

**Answered 2026-11-11:** (1) bank fill: yes; (2) re-judge the lake-rich look on the new whole-ring map at M1: yes; (3) a 30 s bake gate with a 20 s target, and GPU/CPU river-detail differences: yes to both. The study archive: delete, no tag (see T2R-0).


1. **Bank fill.** May the river and lake operators raise the ground a little where the water needs a bank (levees mostly under 3 m; small plugs across narrow gaps in a lake rim)? The old rule was "the carve only lowers", and it is what made the geometry unsolvable. *Default if unanswered: yes.*
2. **A new lake map.** Lake levels will now come from valley floors, which is what makes them safe. The map will differ from the one you chose from: lower lakes, probably less lake area than 12.7 %, longer rivers. `lake_abundance` restores lakes. May we judge the lake-rich direction on the new whole-ring map at the first milestone? *Default: yes.*
3. **Bake gate and reference.** Is a one-time bake of up to 30 s on the laptop (target 20 s), on the loading screen with progress, acceptable in place of the 20 s gate? And is it acceptable that a world created in the launcher (GPU bake) can differ in small river details from the reference world of the same seed (CPU bake), given that each world keeps its own rivers for good? *Default: yes to both.*
