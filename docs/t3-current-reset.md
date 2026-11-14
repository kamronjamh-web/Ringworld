# T3 current, river danger and floating: reset and re-plan

Date: 2026-11-11. Author: Fable 5.2, acting as Lead at the user's request. Branch `terrain-vegetation-revamp`, HEAD `7a2c8ba` (T2R-0 to T2R-6b committed; T2R-7 in progress).
Supersedes: sections 3.2 to 3.7 of `docs/terrain-vegetation-revamp-plan.md` where the two differ, and the previous `docs/work-orders/terrain-vegetation/T3.md`.
Executed through: `docs/work-orders/terrain-vegetation/T3.md` (rewritten with this document; its "Spec" holds the constants).

**Note of 2026-11-13.** This document is the design record of 2026-11-11 and is not edited further. The Spec of `T3.md` was revised on 2026-11-13, after the T2 rules batch and play-test 2, and wins where the two differ. What differs:
- 4.3: the floors of 1.2 m depth and 8 m width are gone (T2R-20), and source stems taper over 1,500 m (T2R-21). The constants were re-chosen in the T3R-3 escalation: mountain roughness 0.18, eta 0.80.
- 4.5: the class mix is counted on rivers alone; the drowned channels inside lakes (T2R-8b) are not rivers to classify. Today's numbers: T3.md Spec E3.
- 4.7: water held in a lake is still apart from inflow jets and the draw at the outlet; the surface is the located level; the head factor replaces the 200 m fade-in (T3.md Spec E5).
- 4.8: the swim entry is 1.15 m and the exit 0.9 m; wading takes its footing from depth times speed (T3.md Spec E6).
- 4.10: the vertex channel is `CUSTOM1`; the roar has no floors; whitewater also rides on the proxy tint (T3.md Spec E7 and E8).
- Section 5: the budgets are re-based (T3.md Spec "Budgets").
- Section 10: four new questions for the user are in T3.md, "Questions for the user (2026-11-13)".
- Scope, later the same day, by the user's decisions: 4.7 to 4.9, the visual half of 4.10 and 4.11 are no longer T3's. They are executed through `T9.md`, group T9i, when the user gives the go, and the Spec sections and questions named above for them (E5 to E7, E9, the budgets, the four questions) are there. The sound half of 4.10 is replaced by `T3.md` Spec W1 to W9, the sound of water as a whole.

## 0. Summary

- **Danger is where the river falls.** The class of a stretch comes from its *danger speed*: the mean centre speed over its pools and chutes together. Pool speed comes from depth and gradient; chutes come from the stored level profile, one per metre of drop. Gentle ground gives calm water, hill country gives swift pool-and-drop rivers, a fall of 5 to 10 % gives a fierce cascade, and steeper or deeper gives a torrent.
- **The interim hydraulics were a lowland law on mountain rivers.** `n = 0.035` with a 10 m/s cap gave sluices: 16 m wide, 3.7 m/s at the median, the cap at p90. Steep rivers are rough staircases. The new law keeps the Manning form and lets roughness rise with gradient (0.05 on plains to 0.20 in the mountains). Rivers become wider and slower, and the steepness shows as chutes.
- **A scratch calibration on the existing graph meets the targets** (seed 1337, whole ring, not production code): calm 39 %, swift 51 %, fierce 9 %, torrent 1.3 % by length, against 9.5 / 24.8 / 28.8 / 36.9 % today. The first work order repeats it in production code on two seeds and shows the user a danger-coloured map before anything else is built.
- **T3 does not move rivers.** Paths, level profiles and lakes stay as T2 baked them. T3 changes width, depth and speed, and adds the class.
- **Hydraulics need a finer unit than the reach.** Reaches are 13 km long at the length-weighted median. T3 adds *spans* of at most 2 km.
- **The exit rule is cheap now.** 87 % of dangerous length already reaches calmer water within 5 km. About 1,050 *landings* (1.5 % of river length) close the rest.
- **Floating bodies are kinematic**, integrated in canonical coordinates as the player is. That removes the rigid-body, gravity-axis and floating-origin questions by construction. This is the one product decision asked of the user that changes the build (Q4).
- Six questions for the user are in section 10. Each has a default, and work can start on the defaults.

## 1. Facts found for this reset

Measured on `.godot/audit_baseline/DESKTOP-RAVA9C1/t2r/bake/20261111_230344_t2r6b_guard/` (seed 1337, whole ring, GPU source). The scratch scripts and their output are in `.godot/audit_baseline/DESKTOP-RAVA9C1/t3r/reset_scratch/` (`summary.txt` first). They parse the graph file directly and are not production code.

1. **The danger mix in the brief is out of date.** 3.6 / 15.4 / 22.3 / 58.8 % was the T2R-5c graph. After water-balance lakes (T2R-6b) the report says calm 9.5 %, swift 24.8 %, fierce 28.8 %, torrent 36.9 %. Closed basins now keep their water: channel length fell from 175,134 km to 130,730 km, Q p50 from 158 to 106 m^3/s, U p50 from 5.35 to 3.66 m/s. The cap is no longer reached at p90 (7.7 m/s), only at p99.
2. **Reaches are too long to carry danger.** 16,312 reaches; length p50 5.5 km, length-weighted p50 13 km; 63 % of river length lies in reaches longer than 10 km and 25 % in reaches longer than 20 km. Each has one Q and one S. An exit rule "within 5 km" cannot be checked on them.
3. **The stored level profile is steeper than the reach gradient.** The reach `S` is the grid `S_f` over 5 km from the reach's middle cell: p50 0.0087. The drop of the stored profile over each reach: p50 0.0157. Total drop along all rivers: 2.65 million m by the profile, 2.13 million m by `S` times length. The profile is what the player sees, because chutes sit where it crosses whole metres.
4. **Rivers on this ring are steep, and that is real.** Profile gradient over 5 km windows, by length: p10 0.0006, p50 0.0127, p90 0.052. 55 % of length is steeper than 1 %, 26 % steeper than 3 %, 11 % steeper than 5 %, about 1 % steeper than 10 %.
5. **Chutes are already there.** One per metre of drop: p50 9 per km, p75 29 per km, p90 56 per km. On a quarter of all river length, chutes take up more than 29 % of the stretch.
6. **The carve keeps the reach depth through a chute.** S7's core follows the displayed level with a constant depth. The plan's "depth thins to a third" is not in the geometry, so a threefold speed in chutes would not conserve discharge and has no source of energy (a 1 m drop adds at most 4.4 m/s to still water).
7. **Banks are walkable by construction.** S7 builds a bank no steeper than about 19 degrees and a floodplain of at least 10 m on both sides of every reach. A swimmer can always leave the water. What may be unwalkable is the valley side beyond (up to 63 degrees, and 40 m strata risers in deep cuts).
8. **Widths have no upper limit in the hydraulics.** The largest interim width on seed 1337 is 372 m. The operator's fade assumes the core ends inside 250 m of the centreline.
9. **Lakes** (S8b): 51,526 lakes, 6,643 with an outlet stem, 7,976 inlets, 23 stems ending Dead on a dry pan. A lake that spills into the middle of a channel has no outlet stem.
10. **The fresh-world spawn** is the main spire's Far viewpoint: `EmitterSiteNavigator.TryComputeFreshSpawn` (scripts/poi/Emitters/EmitterSiteNavigator.cs:263), called from scripts/Voxel/Save/RingWorldSaveManager.Loading.cs:114. This answers the old V5.

## 2. What the plan got wrong, or must change now

| Plan or first T3 | Verdict | Replacement |
|---|---|---|
| One roughness constant, `n = 0.035` | **Replace** | Roughness rises with gradient: 0.05 on plains, 0.20 from about 1 % up, divided by `river_ferocity` (4.3) |
| 10 m/s cap on the mean pool speed (15 m/s at the centre) | **Replace** | Caps on the centre speed: 9 m/s in pools, 12 m/s in chutes, times ferocity. At ferocity 1 the new law never reaches them; they are guards |
| Class from the centre pool speed alone | **Replace** | Class from the danger speed: pools and chutes together (4.4). Same thresholds, 1.5, 4 and 7 m/s |
| Chute speed "up to threefold" | **Replace** | Energy rule: a chute adds the head it drops. A single 1 m chute is a short tongue of about 3 to 5 m/s in any river; overlapping chutes build up into a cascade (4.7) |
| Gradient from the grid, one per reach | **Replace** | Hydraulic gradient from the stored level profile, per span of at most 2 km (4.2) |
| Confinement squeezes the channel in gorges | **Drop** | The operator builds whatever width the hydraulics ask for, so nothing is confined. A cut through a sill has a flat profile and is a slow canyon pool, which is correct. Width already narrows with speed |
| Exit rule needs a "walkable bank" measured on the ground | **Simplify** | Banks are walkable by construction. The rule is about the ride ending and the valley being leavable (4.6) |
| Softening: insert pools, open the valley or raise roughness, to be designed | **Decide** | A *landing*: one span per failing run is slowed and its valley opened (4.6) |
| Probe whether a `RigidBody3D` survives an origin shift, then choose | **Replace** (Q4) | A canonical integrator, chosen now. No rigid body, no physics-server gravity (4.9) |
| Ambience from speed times discharge | **Replace** | Roar from whitewater per metre of channel, so the sound and the foam cannot disagree (4.10) |
| Flow query under 0.3 us | **Relax** | Target 0.5 us, gate 1.0 us. The operator's own lookup has a 0.5 us gate, and the flow adds three level evaluations |
| Targets "at least 70 % calm or swift, 5-10 % fierce, at most 3 % torrent" | **Keep**, and aim inside them | Calibration window in 4.5. The three bands together mean at least 87 % calm or swift |
| `river_ferocity` scales n and the caps | **Keep** | Same meaning, now editable for new worlds, 0.6 to 1.5 (4.3) |
| Calibration on CPU strips of the 2,000 km window | **Replace** | Whole-ring GPU bakes of two seeds through the report tool (10 s each on the 4070). The gate test still uses CPU strips |

## 3. What stands

- The user's decisions: current pushes the player and floating bodies; a variance of safe and dangerous rivers; dangerous water displaces and never harms; a generic floating system with no first item; one current-strength scale in the grave-key menu; new worlds only.
- One source of truth, `RingWaterFlow.Sample`, pure and resident (plan 3.2, 3.7).
- `Q = U w d` on every span. Width follows speed, so slow rivers are wide and fast ones narrow.
- T2's hydraulic floors: pool depth at least 1.2 m, width at least 8 m.
- Flat pools and chutes of 10 m per metre of drop, from `RiverLevel`. No chute list is stored.
- Swim target = input plus water velocity; wading push by depth squared; nothing pins the player (plan 3.4).
- Visible flow at the speed of the force; one global scale for both (plan 3.6).
- The freeze: T2 and T3 together as step "rivers", after gate G2, a play test.

## 4. The design

### 4.1 Principles

1. **Danger is where the river falls.** Every dangerous stretch can be seen from the land before the player enters: the ground drops, the water is striped or solid white, and it roars. Nothing is assigned by noise or per river.
2. **T3 does not move rivers.** Centrelines, wander, meander, level profiles, lake levels and lake cells are T2's and stay numerically the same. T3 changes the channel's width and depth, the valley's floodplain and side slope, and adds speed and class. The approved straight-run result (6.08 % and 7.23 %) is re-measured and must not change.
3. **A class is a measurement, not a label.** The danger speed of a span is the mean of the flow field's centre speed along it. A test compares the two.
4. **Construct, do not fit.** The exit rule is met by building landings, and floating bodies survive origin shifts because they never leave canonical coordinates.

### 4.2 Spans

A **span** is a stretch of one reach, at most 2 km long, with its own discharge, hydraulic gradient, section and class. Reaches stay as T2 defined them and keep the path values.

- **Split.** Each reach is cut into `ceil(cells / 2)` spans of equal cell count. Expect about 75,000 spans on the ring.
- **Discharge.** `Q` at the span's last cell (the accumulated runoff the builder already has). It now grows along a long reach.
- **Hydraulic gradient `Sh`.** The drop of the stored profile W over a 5 km window centred on the span's middle, clamped to the stem, divided by the window length, at least 1e-4. Computed after the level profile exists and stored per span. The window keeps widths from jumping between neighbours.
- **Steepness `Sr`.** The span's own drop of W over its own length. Derived at load from the knots. It counts the span's chutes.
- **Path values stay per reach**: wander, meander amplitude and wavelength, and the bank-line offset used for level stations, all from the reach's stored `S` and `Q` through today's S6 formulas, renamed the *path section*. That is what keeps the centreline and the profile unchanged, and it breaks the circle "width needs the profile, the profile needs the width".
- **Channel and valley values come from the span**: half width, bank width, depth, edge depth, floodplain width and valley side slope, blended over the first 200 m of a span as S6 blends reaches today.
- **Bytes.** About 20 B stored and 28 B derived per span: +3.6 MB resident, about 30 MiB against the 32 MiB gate. If it goes over, spans grow to 3 km before anything else.

### 4.3 Hydraulics

Per span, as pure statics (`RiverHydraulics`), from Q, Sh and the recipe:

```
d   = max(1.2, channel_depth * 0.34 * Q^0.4)
n   = clamp(0.05 * (Sh / 0.0003)^0.4, 0.05, 0.20) / river_ferocity
U0  = min(d^(2/3) * sqrt(Sh) / n,  6.0 * river_ferocity)
w   = clamp(Q / (U0 * d), 8, 400);   if the upper clamp acts, d = Q / (U0 * 400)
U   = Q / (w * d)                     so Q = U w d exactly
```

- **Why this roughness.** 0.035 is a lowland gravel bed. Steep channels are boulder steps and pools, with measured values of 0.1 to 0.3. Roughness rising with gradient is how real rivers hold 1 to 3 m/s on slopes of several percent. Between gradients 0.0003 and 0.0096 the exponent 0.4 lets roughness take up most of the extra slope, so speed stays near 1 m/s across the foothills; above that it grows with the square root of the gradient again. Speed is monotone in gradient and in discharge (checked numerically).
- **Depth coefficient 0.34** (was 0.27). Slower water needs more section. Taking part of it as depth keeps the median width near 50 m instead of 60 to 70 m, and gives the swimmer room under the 1.15 m float depth.
- **The caps** are on the centre speed: `1.5 U <= 9` m/s in pools (U at most 6) and 12 m/s in chutes, both times ferocity. The flow field then needs no second cap in pools, so `Q = U w d` holds in the field as well. At ferocity 1 the fastest pool centre on seed 1337 is 7.6 m/s.
- **Width limit 400 m.** Beyond it the river deepens. The operator's core must end inside its 250 m fade radius.
- **`river_ferocity`** (recipe, frozen with the world; default 1; editable 0.6 to 1.5 for new worlds). It divides n and scales the caps, as the plan said. It moves every speed, and through `w = Q / (U d)` every width: a ferocious world has narrower, faster rivers. Scratch mix by ferocity: 0.6 gives 61 / 32 / 6 / 1; 0.8 gives 50 / 41 / 8 / 1; 1.25 gives 26 / 58 / 14 / 2; 1.5 gives 14 / 63 / 19 / 3.5. `current_strength` is different: live, global, scales speeds for physics and visuals only, never the geometry or the class.

What this looks like (Q in m^3/s; danger speed in m/s and width in m, from the scratch model):

| Gradient | Q 40 | Q 100 | Q 400 | Q 2,000 |
|---|---|---|---|---|
| 0.1 % (plain) | 0.8, 53 m | 1.0, 72 m | 1.4, 114 m | 2.2, 195 m |
| 0.5 % (rolling) | 1.0, 45 m | 1.3, 61 m | 1.8, 97 m | 2.7, 166 m |
| 2 % (hill valley) | 1.8, 29 m | 2.2, 40 m | 3.1, 63 m | 4.3, 108 m |
| 5 % (mountain) | 3.2, 18 m | 4.0, 25 m | 5.4, 40 m | 7.3, 68 m |
| 10 % (cascade) | 5.9, 13 m | 7.3, 18 m | 9.7, 28 m | 11.7, 48 m |

Against the interim law, the median river goes from 16 m wide, 1.7 m deep and 3.7 m/s to about 50 m wide, 2.2 m deep and 1.0 m/s. That is what a river of 100 m^3/s looks like, and it reads far better from the air.

### 4.4 Danger speed and classes

A span is pools and chutes. Its **danger speed** is the length-weighted mean of the centre speed over both:

```
Vp  = 1.5 * U                                   centre speed in the pools
c   = min(1, 10 * Sr)                           share of the span that is chute (10 m per metre of drop)
h   = max(1, 30 * Sr)                           head a chute carries: 1 m alone, more where chutes overlap within 30 m
eta = 0.65 * clamp(d / 2.2, 0.5, 1.6)           share of that head that becomes speed; deep water keeps more
Vc  = min(sqrt(Vp^2 + 2 g eta h), 12 * ferocity)
Vd  = (1 - c) * Vp + c * Vc
```

Classes on Vd, with the plan's thresholds and meanings: **calm** under 1.5 m/s, **swift** under 4, **fierce** under 7, **torrent** above.

How velocity, depth and steepness each enter:
- **Velocity**: Vp. A big river on a moderate slope can be fierce on pool speed alone.
- **Steepness**: c and h. Below about 3 % the pools are long and the class is the pool's. From 5 % chutes are half the length and the stretch is a cascade. At 10 % there are no pools left.
- **Depth**: three ways. Deeper pools run faster on the same slope (`d^(2/3)`). Deep water carries its speed through a chute (eta), so a small steep creek tops out at fierce while a large river on the same fall is a torrent. And the 8 m by 1.2 m floor makes the smallest streams slow, because little water fills a fixed section.

Measured consequence (scratch, by the span's own gradient): under 0.5 % nearly all calm; 0.5 to 2 % calm or swift; 2 to 5 % swift (94 %); 5 to 10 % fierce (81 %); over 10 % torrent (87 %). This is the paddler's distinction between pool-drop water, which forgives, and continuous water, which does not.

### 4.5 Calibration

**Scratch result** (seed 1337, whole ring, 73,597 spans cut from the existing graph, reach Q on every span):

| | Calm | Swift | Fierce | Torrent |
|---|---|---|---|---|
| Interim law, today's report | 9.5 % | 24.8 % | 28.8 % | 36.9 % |
| New law, before landings | 39.4 % | 49.1 % | 10.0 % | 1.5 % |
| New law, after landings | 39.4 % | 50.6 % | 8.8 % | 1.3 % |
| Target | at least 70 % together | | 5-10 % | at most 3 % |

The result is stable when spans are 1, 2 or 3 km (fierce 9.3 to 10.0 %, torrent 1.6 to 2.9 %).

**Procedure** (T3R-1 and T3R-2). The production tool evaluates a small grid on two seeds: mountain roughness in {0.16, 0.18, 0.20, 0.22}, depth coefficient in {0.30, 0.34, 0.40}, eta in {0.5, 0.65, 0.8}. A combination is acceptable when, on both seeds and after landings: calm at least 30 %, calm plus swift at least 85 %, fierce 6 to 9 %, torrent 1 to 2.5 %. Among acceptable ones the lead takes the lowest mountain roughness (the narrowest rivers), then the depth coefficient nearest 0.34. The window sits inside the user's bands so a third seed has room. The user sees the danger map and the table and confirms before the bake is changed.

**How the mix is met without implausible mountain rivers.** Nothing is flattened and no gradient is hidden. A mountain river keeps every metre of its fall; it spends it in chutes and rough pools. What was implausible was the interim result: a mean of 10 m/s is faster than any natural river. Only about 12 % of river length falls steeply enough to be dangerous, and that share follows the terrain, so it should hold across seeds.

### 4.6 Exit rule, landings and exclusions

- **Getting out of the water** is always possible: flow runs along the channel, is zero at the banks, and every reach has a walkable bank and a floodplain strip. This is asserted in a test, not softened.
- **The ride ends.** From the downstream end of every fierce or torrent span, at most 5 km of further dangerous water follows before an **exit**: a calm or swift span whose valley side is at most 35 degrees, or a lake, the sea or a dry pan. The walk is downstream along the stem and into the receiving stem at a confluence.
- **Scratch numbers.** 4,388 dangerous runs; length p50 2 km, p90 7 km, longest 26 km. 87 % of dangerous length already exits within 5 km (T0 measured 56 % on the prototype).
- **Landing.** Walking each run from its head, the span that would take the run past 5 km becomes a landing: its roughness is raised and its eta lowered by one stored factor until its danger speed is 3 m/s, and its valley side slope is limited to 0.6 (31 degrees). `Q = U w d` holds, so a landing is wider. In the world it is where the valley opens, the water spreads over boulders and slows. About 1,050 landings, 1.5 % of river length, 13 % of dangerous length. A landing is also an exit when a calm or swift span fails the valley test.
- **Exclusions.** T2's barriers already keep every channel at least 650 m from a site footprint. A span with any point inside 1.5 km of an emitter site disc, the Wall Engine stadium or the Weather Lung footprint, or inside 3 km of the nominal fresh-spawn position (the main site's Far viewpoint before its land check), is a landing if it would be dangerous.
- **Stop condition.** If landings exceed 4 % of river length on either seed, the rule is wrong for this terrain and goes back to the lead.

### 4.7 The flow field

`RingWaterFlow.Sample(around, across, height)` returns: kind (None, River, Lake), surface level, bed (River only), velocity (around, across, m/s), speed, span, class, danger speed and whitewater. It is a pure function of position, the resident graph and `current_strength`.

| Part | Rule |
|---|---|
| Location | The carve operator's piece search: owner piece, stem, arc length s, signed offset t, tangent of the displaced centreline. One shared method, no second copy |
| Surface | `RiverLevel` at s. In a lake, the lake level |
| Bed | The S7 core formula at t. Inside the core the operator's upper and lower bounds are both that value, so it equals the terrain without a height-model call. Lakes report no bed; a caller that needs it asks the terrain sampler |
| Direction | The tangent, downstream |
| Across | `(1 - (t / a)^2)` inside the channel, zero in the bank zone |
| Along | Centre speed `vc(s) = sqrt(Vp^2 + 2 g eta B(s))`, capped at 12 m/s times ferocity. `B(s) = sigma(s) * max(1, L(s - 30) - L(s))`, with `sigma(s) = clamp(L(s - 5) - L(s + 5), 0, 1)` and L the displayed level. In a pool B is 0; on a single chute it rises to 1 m; in a cascade it reaches the drop of the last 30 m |
| With depth | 1.0 at the surface to 0.6 at the bed |
| Span joins | Vp and eta blend over the first 200 m of a span, with the section |
| Sources and outlets | Speed fades in over the first 200 m of a stem, or up to `OutletS` on a lake outlet stem. That is the outlet draw |
| Lake inlets | Full speed to the stem's end, then a jet along the end tangent: `exp(-x / (3 w))` along, a Gaussian of width `a + 0.2 x` across |
| Sea and dry pans | Speed fades to zero over the last 500 m of a stem ending in the sea or Dead |
| Confluences | The owner piece's flow. A shear line at the join is accepted |
| Lakes, ocean | Still, apart from the jets and the draw. The ocean has no flow |
| Scale | Every speed times `current_strength` |

Lakes without an outlet stem, including those that spill into the middle of a channel, are simply still. Where such a lake meets a channel, the channel's core owns the water and flows.

### 4.8 Push

- **Swimming.** Target velocity = input velocity plus water velocity, with the existing `SwimAcceleration`. One flow sample per physics frame, only when the controller's water state says in or near inland water.
- **Wading.** Depth between 0 and the swim entry depth: an acceleration toward the water velocity, scaled by (depth / entry depth)^2.
- **Float.** The float target follows the surface down a chute. On boosted water the float depth grows by 0.3 m per metre of B, at most 0.6 m, so a cascade dunks the swimmer **(tune at G2)**.
- **Never pinned.** Flow has no component across the channel, and input always adds.
- **Unchanged**: noclip, flight, ocean swimming, generation 1.
- **What each class does to a swimmer at 5.5 m/s (sprint 8.5):** calm barely drifts; swift carries, and the swimmer can go upstream through single chutes; fierce cannot be held at the centre without sprinting; a torrent sweeps a sprinter down. In every class the bank is seconds away.

### 4.9 Floating

**Decision: a canonical integrator, not a `RigidBody3D`** (Q4).

Reasons:
- The world frame has the ring's across axis on +Y, so engine gravity is wrong everywhere, and the origin moves every 16 km. A body that keeps (around, across, height) and a velocity in those axes, as the player controller does, has neither problem. Its node transform is written each physics frame from the same conversion the controller uses.
- Terrain collision exists only near the player. A kinematic body needs none: the flow sample gives it the surface and the river bed, and on a bank or in a lake it asks the terrain sampler for the ground.
- The whole model runs headless, so its tests are cheap and exact.
- Cost is bounded: one flow sample per body by default.

What is given up: bodies do not tumble, stack or collide with one another. They bob, yaw into the flow, tilt on a chute, strand and sleep. The node can be an `AnimatableBody3D`, so the player can bump or stand on one later (a raft needs exactly this).

Parts:
- `RingBuoyancy` (static): vertical spring toward `surface - draft`, critically damped; horizontal drag toward the water velocity (time constant from the `drag` export); bed friction when the water is shallower than the draft; ground rest out of water.
- `RingFloatingBody` (component): canonical state; exports `mass`, `draft`, `drag`, `sample_points` (1 by default, up to 4), `may_strand`; sleep after 3 s under 0.05 m/s; wake when the water moves it or something calls `Wake()`.
- `RingFloatingManager`: bodies simulate within 300 m of the player, at most 32 awake; beyond that they sleep in place; not saved.
- `RingFloatingItemCatalog`: the row schema (scene, spawn rule, density, allowed classes) and no rows.
- A test-only body and `docs/floating-items.md`.

### 4.10 Visual and audio cues

Every cue reads two numbers from the same flow sample: the local centre speed and the chute head B. They cannot disagree with the force.

| Cue | Rule | System |
|---|---|---|
| Surface streaks and drift | Normals and foam advected by the per-vertex velocity, two-phase | Water mesh vertex channel, `ring_water_common.gdshaderinc` |
| Foam | `white = max(smoothstep(2.5, 7, speed), min(1, B))`: chutes are always white; pools whiten with speed | Third component of the vertex channel |
| What the player sees | Calm: glass. Swift: streaks, a white bar at each chute. Fierce: white stripes closer than the pools between them. Torrent: white bank to bank | |
| Roar | Per span, `roar = wbar * clamp(sqrt(w / 40), 0.5, 1.5)`, with `wbar` the span's mean whiteness; audible radius `100 + 400 * min(1, roar)` m. A calm river keeps only the lapping layer | Controller probe along nearby spans, `water_ambience.gd` |
| From the air | Far whitewater = width-weighted mean whiteness of the spans in the cell. A pool-drop river is a dashed pale line, a torrent a solid white one | T2R-12's far input |
| Bank and valley | Floodplain width and valley side slope from the span: wide and open where calm, a narrow V where it falls. Landings open | The carve (S7), fed by spans |
| Chute rocks | `RiverGraph.QueryChutes` gives T5 the position and strength of each chute | T5 |
| Plants | T6 reads the span class: reeds and gallery trees on calm spans, bare rock margins on fierce and torrent | T6 |
| Map | The preview's drainage mode and the minimap's debug view colour spans by class | Not the grave-key menu |

### 4.11 Settings

One live setting on the Water tab: **Current strength**, key `current_strength`, 0 to 2, default 1, owner `GameplayWaterDefaults`, shader global `ringworld_current_strength`. At 0 the water is still for physics and visuals alike. The class does not change with it. `river_ferocity` is a recipe row on the Terrain3D tab, editable only when authoring a new world. No other toggles.

## 5. Budgets

| Quantity | Target | Gate | Measured by |
|---|---|---|---|
| Flow query | 0.5 us | 1.0 us | `river_flow_field_test` printout |
| Controller, sustained main thread | 0.03 ms | 0.05 ms | CPU telemetry at a river pose |
| Floating bodies, 32 awake | 0.06 ms | 0.1 ms | `floating_body_test` printout, then the GPU validation |
| GPU at a river pose, A1000 | | inside T2's 0.3 ms | A-B-A, current on against off |
| Graph, resident | 30 MiB | 32 MiB | graph report |
| Cold bake, added by spans, classes and landings | 0.5 s | 1.5 s on the 4070 | bake report |
| Sample cost near a river | unchanged | T2's +0.5 us | `river_carve_invariants_test` printout |
| Ambience probes | 0.01 ms | 0.02 ms per frame | CPU telemetry |

## 6. Testing and stop rules

The phase rules of T2.md apply unchanged: production first, sampling with tolerances, the reduced cadence (one to three tests per change with `-Tests` and a dry run first; compatibility checks at the end of an order; `-Suite gate` only at commit points), a stop rule on every order, short escalation, no new requirements from inside. T3 has three commit points: C4 after the bake carries classes (T3R-3), C5 after push and floating (T3R-7), C6 at the review (T3R-12).

User milestones: **D1** the danger map (T3R-1), **D2** a swim down a fierce reach (T3R-5), **D3** visible flow and sound (T3R-8, T3R-10), then gate G2.

## 7. What T3 needs from T2

Short list for the primary; `T2.md` is not edited here.

1. **One location method** in `RiverCarveOperator` (T2R-7): the piece search that yields owner piece, stem, s, signed t, tangent, half width and bank width, callable by `RingWaterFlow` and used by `Evaluate` itself.
2. **The core bed as a pure static** (T2R-7): `bed(level, d, d_e, a, t)`, so the flow's bed is the carve's bed by one formula.
3. **All channel and valley values through one accessor** on `RiverNetwork` (T2R-7, T2R-8, T2R-11, T2R-12): no direct `Sections[reach]` reads. T3R-3 moves those values from reaches to spans behind it.
4. **A runtime lake query** (T2R-8): lake id and the indicator P at a point, public. `RiverLevel.LakeIndicator` is internal and bake-only today.
5. **Dead ends** (T2R-8 or T4): 23 stems end on a dry pan with no tail. T3 fades the flow over the last 500 m; someone has to decide what the water surface and the ground do at the end.
6. **A free vertex channel** (T2R-11): `CUSTOM0` stays unused in the inland water mesh and the compact path, for T3R-8.
7. **Coverage with a per-span weight** (T2R-12): `LineCoverage` takes an optional weight per span, or exposes its piece-in-rectangle loop, so far whitewater does not copy it.
8. **The preview's drainage mode and the ferocity row** (T2R-13): the mode's colouring is switchable by a parameter, and the read-only `river_ferocity` row is built so T3R-3 can make it editable.
9. **No width limit exists in T2.** T3R-3 adds 400 m. Until then the largest width on seed 1337 is 372 m, so nothing breaks today.

## 8. Risks

- **Wider channels in hill country** mean more cut on the uphill side and more bank fill on the downhill side. Mitigation: T3R-3 reports cut and fill beside T2R-7's numbers; the depth coefficient trades width for depth; M2 and M3 show it.
- **The class map looks patchy**, with a fierce span between swift ones. That is a single cascade and is correct; the map at D1 shows whether it reads as noise. If it does, the gradient window for chutes grows from the span to 3 km.
- **Second seed outside the window.** The mix follows the terrain's slope distribution, which the recipe fixes, so the risk is small. If it happens the lead widens the grid once, then asks the user.
- **Landings look arbitrary**: slow water in a steep valley. They are 1.5 % of length, the valley opens there, and T5 can dress them with boulders. Judged at G2.
- **Calm rivers feel dull.** About 40 % of length barely moves a swimmer. Single chutes keep them alive, and `river_ferocity` and `current_strength` are the knobs.
- **Being swept away feels like punishment** (plan 14). No damage, a bank always in reach, a ride of at most 5 km plus the span, and cues before entry. G2 is a play test.
- **The player outruns collision coverage** at 12 m/s. T3R-5 escalates on coverage waits.
- **Memory.** About 30 MiB of 32. Spans of 3 km are the fallback.
- **Kinematic bodies feel stiff.** Bob, yaw and tilt are cheap to add; the test body at C5 is the check. A rigid backend can still be added behind the same component if an item ever needs it.
- **The scratch numbers differ from production.** They used the reach's Q on every span, which overstates upstream discharge, and a Python copy of the formulas. T3R-1 exists to replace them.
- **Design drift.** As in T2: the primary refuses any report that asks for a new proof in place of a picture.

## 9. What changes for later phases

- **T4** (bed and bank materials): landings and cascades are flagged per span; `RiverGroundDelta` is unchanged.
- **T5** (chute rocks, cobbles): `QueryChutes` as before, now with the chute head as its strength. Landings want boulder fields.
- **T6** (riparian): reads the span class and whiteness instead of a reach class.
- **T7**: no change. T3 adds nothing to the drainage height source.
- **T9**: deltas and waterfall meshes would attach to torrent spans steeper than 10 %.

## 10. Questions for the user

1. **Danger is where the river falls.** Calm on gentle ground, swift in hill country, fierce where the river drops 5 to 10 % in a cascade, torrent where it drops more or carries a lot of water. May the class be defined this way, from pool speed and chutes together? *Default if unanswered: yes.*
2. **The mix to aim for.** On seed 1337 this gives about calm 39 %, swift 51 %, fierce 9 %, torrent 1.3 % (today: 9.5 / 24.8 / 28.8 / 36.9). It is inside your bands. Shall we aim there and judge it on the danger map at the first milestone? *Default: yes.*
3. **Wider, slower rivers.** The median river goes from 16 m wide and 3.7 m/s to about 50 m wide and 1 m/s, and from 1.7 m to 2.2 m deep. Mountain rivers narrow to 15-25 m and are staircases of pools and chutes. Is that the look you want? *Default: yes. The alternative keeps rivers nearer 30 m by making them about 3 m deep.*
4. **Kinematic floating bodies.** Floating things drift, bob, turn with the flow, strand on banks and can later carry the player, but they do not tumble or collide with each other. This avoids the rigid-body and floating-origin problems entirely. Acceptable for the generic system? *Default: yes.*
5. **Landings.** Where a dangerous run would last more than 5 km, one stretch of it is made a landing: the valley opens and the water slows, by rule. About 1.5 % of river length. Acceptable? *Default: yes.*
6. **River sound.** T3 needs a calm-babble loop and a rapids-roar loop. May the agent fetch CC0 recordings (CC-BY only if no CC0 fits) and record the licence, as with earlier audio? *Default: yes.*

### User answers (2026-11-11)

All six questions were accepted at their defaults:
1. Danger is defined by where the river falls, using pool speed and chutes together.
2. Aim for about calm 39 %, swift 51 %, fierce 9 % and torrent 1.3 %, judged on the D1 danger map.
3. Rivers become wider and slower (median about 50 m wide at about 1 m/s); mountain rivers are narrow staircases.
4. Floating bodies are kinematic, with no tumbling or body-to-body collisions.
5. Landings are applied to about 1.5 % of river length.
6. An agent may fetch CC0 river loops (CC-BY only if no CC0 fits) and record their licences.
