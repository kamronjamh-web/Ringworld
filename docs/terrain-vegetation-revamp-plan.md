# Terrain generation and vegetation revamp plan

Status: proposal, revision 4, 2026-11-09. Written by the Fable role (`claude-fable-5-2`) for the primary agent to review. No code, shader, scene or setting was changed, and Godot was not launched. The user has answered all thirteen original questions (section 13.1). Revision 3 raised three new points the user has not yet seen (section 13.2).

Scope: the user's brief, "outside of mountains and hills, terrain is pretty flat; for vegetation we only have 3 kinds of trees and grass; no rocks, shrubbery, rivers". The plan covers water, lowland landforms, the ground surface and rocks, vegetation diversity, the asset pipeline, performance and far representation, and gameplay hooks.

How to read this: section 1 is what the code and docs say today, including twelve places where the older docs or the brief differ from the code. Section 2 is the design thesis and the world-generation rule that both this plan and `docs/vastness-plan.md` follow. Sections 3-9 take the areas one at a time, water first. Section 10 says where the two plans meet. Section 11 is the phase list. Numbers marked "derived" are arithmetic from code defaults or asset files; "est." marks estimates to be replaced by measurement; "measured (doc)" marks numbers quoted from an existing doc and not re-run.

## 0. Summary

**Thesis.** The land has relief at 5 km and above and almost nothing between 1 m and 500 m, and what relief there is has no organising cause. Real landscapes are organised by water: drainage sets the shape, and rock, soil, shrubs and trees each show where the water goes. The plan builds one drainage and habitat description of the ground and makes four layers read it: shape, surface material, scatter (rocks, dead wood) and vegetation.

**Decided by the user.**

- New worlds only, project-wide for now, and it covers the vastness plan. All current worlds are test worlds.
- Every major visible change gets its own frozen version step and a fresh test world. Worlds made before a step stay as they were.
- Rivers and lakes are the centrepiece. Rivers have a current that pushes the player and floating bodies. Rivers vary: most are safe, some are dangerous.
- The floating system is built generically first, with no specific first object.
- Third-party CC0 and CC-BY assets are allowed, after the three tree packs already in the repo are used more fully.
- Grass cards are the new default. That change is global: the user's explicit exception to new worlds only.

**How the decisions shape the plan.**

- One mechanism versions both plans: the **world generation**, an integer stored in the terrain recipe. A ledger maps each generation to the features it has. Each phase that changes what a world looks like ends by freezing the next generation and creating a test world for it (section 2.2).
- One new recipe id, v9, carries the generation. No further recipe ids are needed.
- Rivers come first.

| # | Phase | Kind | Ends with |
|---|---|---|---|
| T0 | Landscape audit and baseline | new tools, no production change | nothing frozen |
| T1 | Recipe v9, the generation ledger and test-world tooling | plumbing, no visible change | nothing frozen; a v9 world equals v8 |
| T2 | Rivers and lakes | the main new engineering | review worlds only |
| T3 | Current, river danger and the floating system | medium engineering | generation step "rivers" |
| T4 | Ground surface | shader work | step "ground" |
| T5 | Scatter foundation and rocks | medium engineering | step "rocks" |
| T6 | Vegetation from the packs we have | medium engineering | step "vegetation" |
| T7 | Lowland meso-relief | medium-large engineering | step "landforms" |
| T8 | New tree species and further assets | asset work | step "species" |
| T9 | Contingent items | each needs its own go | a step each, if visible |

T4-T6 can be developed alongside T2-T3 once T1 is in. Steps are numbered in the order they are frozen, on one ladder shared with the vastness plan.

**Budget.** All phases together: at most 1.5 ms GPU on the RTX A1000 (1920x1080, Medium, P0 / 35 W), at most 0.2 ms of new sustained main-thread work, at most 48 new draw calls at Medium, lowland height sampling at most 1.25x the v7 cost, and a one-time drainage bake of at most 20 s per new world on the laptop.

## 1. What exists today

### 1.1 Corrections to the brief and the older docs

These were found in code. The primary agent should confirm them and update the stale docs.

1. **Lowland landforms already exist, and the user's world has them.** `docs/lowland-landform-overhaul-plan.md` (recipe v7: plains, rolling, hill country, plateaus, escarpments, breaks, lowland gullies) is implemented and is the default for new worlds. The user's `slot_1` (seed 1337) stores `ringworld_surface_weather_lung_v8`, which is v7 plus the Weather Lung, with the v7 defaults. So "pretty flat" describes v7 output, not a missing feature. Section 1.3 says why it still reads flat. I checked `slot_1` only.
2. **The height function now has a GPU copy.** The lowland plan says "no shader or native port of the formula exists". On this branch `materials/terrain_gpu/ring_height_model.glslinc` ports the v7/v8 natural path, and GPU heights are the default for the 8, 16 and 32 m proxy levels (commit e470adc). The host rejects every other generator id. A new recipe id must be added to the host's accept list, and any new height term needs a GLSL port and a parity pass.
3. **Climate did not move into the recipe.** The lowland plan deferred it to "a later v8". The v8 id was used for the Weather Lung instead. Temperature and moisture still come from `VoxelTerrainSettings` noise in `RingTerrainSampler`, outside the frozen recipe. The next free id is v9.
4. **The coarse-LOD follow-up is still open.** The lowland plan measured edge mismatches above the 8 m skirt from 64 m spacing upward in hill country. `SectorSkirtDepthMeters` is still 8 m and no footprint-filtered sampling exists.
5. **Rivers and lakes exist in the data model but not in runtime worlds.** `TEMP_TERRAIN_GENERATION_TODO.md` and `docs/water-system-implementation-checklist.md` describe them as done. That is the legacy gen-16 sampler. The gen-17 runtime path has one water rule: `presentationHeight < 0` is ocean (`RingTerrainSampler.SurfaceGeneration.cs:65`). `TerrainSample.WaterKind` (None, Ocean, Lake, River), `WaterSurfaceHeight` (an integer), the proxy water mesh, the water shader's kind profiles and a river ambience kind are still there, unexercised by any gen-17 world.
6. **The player controller hard-codes the ocean rule.** In a runtime-generation world `RingVoxelExplorerController` overwrites the sample's water kind with "ocean if the surface is below 0, else none" (about lines 1150-1156). Inland water would render but not be swimmable until that is changed.
7. **There is no water physics beyond the player's swim motor.** The player is a custom kinematic controller in canonical ring coordinates. Swimming sets a target velocity from input and floats toward a depth below the surface (`ProcessSwimming`). Nothing models flow. No `RigidBody3D` exists anywhere in `scripts/` or `scenes/`, so floating bodies have no precedent on the ring.
8. **The forest checklist understates the tree renderer.** `docs/forest-region-implementation-checklist.md` section 3 describes MultiMesh tiles and leaves LOD items unchecked. The code is GPU-driven indirect drawing, a compact LOD profile, and one shared far impostor built from the authored LOD3 card meshes, out to 5.5 km. "Distant canopy coverage" is the item that is really still missing. `resources/trees/lolipop/README.md` has the same stale "bounded MultiMesh tiles" wording.
9. **The production trees are the LOLIPOP packs.** `THIRD_PARTY_ASSETS.md` still calls them "isolated review assets".
10. **The tree packs are used at a fraction of what they contain.** The packs ship authored size classes (sapling, small, medium, large, and for pine also big). `SelectForestVariant` ignores them and picks uniformly among a species' variants, so a quarter of all maples are saplings, mixed at random with full-grown trees and competing for the same spacing. Section 7.1 has the full audit.
11. **Grass default.** The profile default was Blades when I read the code (`RingGraphicsQualityController.cs:607`); the user has decided Cards is the default and an implementer is making that change. The card atlas has 4 variants (`RingGrassCardPlacement.AtlasVariants`).
12. **The Biome Workshop does not feed the world.** `scripts/Biomes/BiomeAuthoring*` is a preview tool; nothing in production reads its recipes.

Also out of date, with no effect on this plan: `docs/mountain-terrain-overhaul-plan.md` is written against the gen-16 sampler.

Not verified: any A1000 cost figure for trees alone, whether inland water renders correctly in a gen-17 world, how a rigid body behaves across a floating-origin shift, and whether world slots carry a display name. T0 and the T1 and T3 design steps cover these.

### 1.2 Facts that shape the plan

**Height.** One C# formula, `TerrainGenerationHeightModel.SampleLandformNaturalDiagnostics`, a pure function of ring coordinates, followed by site operators (600 emitter sites, optional Wall Engines, the Weather Lung) that replace the natural height inside influence discs. Registries are built once from the unshaped natural height and cached by definition hash.

**Freezing.** A saved world stores its recipe JSON, generator version and definition hash. The hash covers the recipe JSON but not formula code, so a code change under an unchanged recipe silently changes saved worlds. The convention is a new generator id per change, guarded by freeze goldens. Recipe fields can also switch features without a new id: `climate_machines` in v7 is the precedent. Recipe flags already reach every consumer through the sampler.

**Cost.** v7 CPU cost per sample is 1.15 us in ocean, 1.45 us on plains, 1.77 us in hills, 2.5-2.8 us in mountains and foothills, about 90 % of it native noise (measured (doc), `docs/gpu-offload-plan.md`).

**LOD chain.** Terrain3D: 768 m window at 2 m, handing off to proxies at 112 m. Proxy sectors: 2, 4, 8, 16, 32 m spacing out to about 5.5 km, then 64 m and coarser. Global LOD and the far viewport read a min-max pyramid with a 4,096 m base cell.

**Material.** Six albedo textures, triplanar, no normal maps. Biome weights are four packed channels with grass as the remainder. Ground blends to the dirt texture, not rock, as slope rises from about 39 to 63 degrees. The wet-world pass already finds hollows from four height taps and fills puddles.

**Water.** Sea level is 0 everywhere. FFT ocean near, analytic far. Swim speed 5.5 m/s, sprint 8.5 m/s, swim entry at 0.55 m depth.

**Trees.** CPU placement in 240 m tiles on worker threads (jittered candidates in 30 m cells, a regional forest sampler with cover at 12 km, variation at 2 km and clearings at 768 m, a hard-core spacing pass), disk-cached under a key that includes the terrain definition hash, then GPU culling and LOD. Size jitter 0.85-1.15. No per-instance tint. Trunk colliders within 110 m, capped at 192.

**Grass.** CPU placement in 16-48 m tiles, GPU culling. Masks: site mask, biome weights, slope, water. No relation to trees, wetness or landform.

**Vegetation GPU path.** `BatchRegistration` takes any mesh, but the compute shader has two modes, grass and tree, one shared five-slot LOD budget, and haze channels for grass and trees only (other batches fall back to per-vertex haze).

**Nothing exists** for rocks, boulders, shrubs, flowers, ferns, reeds, logs, stumps or ground cover.

**Machine.** The A1000 at Medium runs at 40-44 ms GPU with a 20-23 ms main-thread floor (measured (doc), `docs/perf-ranking-2026-10-15.md`).

### 1.3 Why v7 land still reads flat (derived from code defaults)

Peak contribution of each height term on lowland; typical values are about a third of the peak.

| Wavelength | Term | Plains (34 % of eligible lowland) | Hill country (29 %) |
|---|---|---|---|
| 32 m | micro detail | 0.4 m | 1.8 m |
| 256 m | secondary detail | 5 m | 27 m |
| 128-256 m | lowland gullies | 0 (needs slope) | up to 24 m |
| 1.5 km | foothill, ramping in between 50 and 250 m macro height | up to 100 m | up to 100 m |
| 5 km | hills | 38 m | 480 m |
| 32-64 km | provinces, plateau lift and 160 m terraces | 0-320 m | 0-320 m |
| 300 km | continental tilt | 0-1,500 m | 0-1,500 m |

1. **A scale gap.** On plains, the ground within 30 m of the player varies by decimetres and within 250 m by about 2 m. Even in hill country the shapes are 5 km swells. The coastal belt gets no landform terms below 20 m macro height and only a ramp of them up to 140 m.
2. **No organising cause.** The hills are isotropic noise. No valley leads anywhere and nothing marks the low lines.
3. **Nothing breaks the surface.** No rock, ledge, bank or cut, and no normal detail for raking light to pick out.
4. **Vegetation ignores the ground.** Trees follow climate noise and grass follows biome weights. Neither can reveal relief.

## 2. Design thesis

### 2.1 One cause, four layers

A landscape reads as rich when one causal description of the ground is expressed consistently by its shape, its surface, its loose objects and its plants. The description here is a small set of **habitat fields**, computed from the height function, the climate and the drainage graph:

- slope;
- curvature at 8 m and 64 m (convex ground sheds water and soil, concave ground collects them);
- wetness (climate moisture, concavity, height above the nearest channel);
- rock exposure (steep or convex, thin soil) and its complement, soil depth;
- canopy cover (from the forest sampler).

Every layer reads these fields. Rock shows where exposure is high, in the ground texture and as meshes. Shrubs, reeds and dense grass follow wetness. Trees thin on exposed crests. A player who learns one rule ("water is where the willows are") can read the whole world.

**A relief ladder.** The ground needs something to look at in every decade of size.

| Scale | Today | Proposed | Delivered by |
|---|---|---|---|
| 0.3-3 m | grass only | cobbles, boulders, logs, shrubs, tussocks | T5, T6 |
| 3-30 m | nothing | outcrops, river banks and channels, hummocks, copses | T2, T5, T7 |
| 30-300 m | 5 m of noise | river terraces and bluffs, knolls and ponds, dunes, tors | T2, T7 |
| 0.3-3 km | foothills in places | valleys, lakes, gallery woods, buttes | T2, T6, T7 |
| 3-30 km | hills | unchanged, now cut by valleys | T2 |
| 30-300 km | provinces, plateaus, ranges | unchanged, now linked by rivers | T2 |

**Paint what cannot be drawn.** Wherever an instanced class ends, the terrain shader carries a matching colour or shading term driven by the same field, faded by pixel footprint. Forest becomes canopy tint, boulder fields become speckle, rivers become a water-coloured line with sun glint, and fast rivers a white one.

**Substitute before adding.** Ground cover replaces a share of grass cards within the grass budget. Snags are tree variants within the tree budget. Only rocks and shrubs are new instance budgets, and both are near-only.

### 2.2 World generations: one version ladder for both plans

**Decisions (user).** "New worlds only is a universal thing at the moment, all of these worlds are for testing." And: "make new worlds for every major change that would require a new world."

So: every major visible change is a version step. A world keeps the step it was created at. Each step gets a fresh test world. This applies to this plan and to the vastness plan's rendering changes.

**Mechanism.**

- **One recipe id, one integer.** A new recipe id, `ringworld_surface_world_v9`, has an integer field `world_generation`. Every recipe up to v8, and every world that does not use runtime generation, is generation 1. The field is part of the canonical recipe JSON, so it is stored in the save, covered by the definition hash, and separates caches automatically. Switching features by a recipe field has precedent (`climate_machines` in v7).
- **A ledger.** One table in code, mirrored in this doc, lists what each generation adds: generation N has every feature of N-1 plus its own. Consumers never compare numbers; they ask the ledger whether the loaded world has a feature.
- **Published once.** At world load one owner publishes the generation and the feature set: a C# static, and a global shader parameter holding the features as a bit mask (`ringworld_world_features`). Compute shaders get it through their parameter buffers. Only one world is loaded at a time.
- **Frozen and open.** The ledger's last row is the latest frozen generation. Work in progress is "open": its features exist in code but belong to no generation. A **review world** is a v9 world at the latest frozen generation with an extra `review_features` list in its recipe. Review worlds are disposable, may change with every fix, and are deleted when the step freezes.
- **Freezing a step.** When the user signs off a phase, its features become generation N+1 in the ledger, a fingerprint row is captured, the launcher's default for new worlds becomes N+1, and the tooling creates the step's test world.

Why the recipe and not a per-world render profile or a version field in the save:

1. The recipe is already the per-world frozen record and already reaches every consumer through the sampler.
2. The dressing needs recipe-level data anyway (the drainage graph and the recipe's climate).
3. One axis is enough. A render profile stored with each save would make every settings owner per-world, which is out of proportion for worlds that are all test worlds.

**How fine the steps are.** One step per phase the user signs off, not per work order. Two phases accepted in the same review may share a step. The expected total across both plans is about twelve steps. Not a step: bug fixes to plainly broken behaviour, performance work proven to give the same output, tuning inside an open step, and new settings whose default reproduces the old behaviour. After a step is frozen, a look change to one of its features waits for the next step and is added as a new feature there.

**Expected ladder** (numbers are assigned at freeze, so this order is provisional and vastness steps interleave):

| Generation | Step | Adds |
|---|---|---|
| 1 | today | every recipe up to v8 |
| 2 | rivers | drainage graph, carve, lakes, inland water, current, river danger, floating system (T2, T3) |
| 3 | ground | rock, strata, scree, bed and bank materials, colour variation (T4) |
| 4 | rocks | habitat field grid, scatter classes, boulders, outcrops, cobbles (T5) |
| 5 | vegetation | pack-derived forms, communities, ground-cover cards, far canopy (T6) |
| 6 | landforms | meso-relief, coastal variety, band-limited sampling (T7) |
| 7 | species | new tree species (T8) |
| interleaved | vastness V1-V6 | see `docs/vastness-plan.md` section 11 |

**Height formula.** Rivers and landforms change heights. They are ledger features like any other, gated inside the v9 height method, so no second recipe id is needed. A step that changes heights adds a row to the recipe freeze golden for its generation.

**Test worlds.**

- A tool, `tools/new_generation_world.ps1` with a C# helper, creates a world through the same save path the launcher uses. It is the last work order of every step.
- Default seed 1337 for every step, so the same geography can be compared across generations. A `-Seed` option covers other cases.
- Name: `g<NN>-<step>-s<seed>`, for example `g02-rivers-s1337`. Review worlds are `review-<step>-s<seed>`. If slots have no display name today, the tool adds a `label` to `world.json`.
- The launcher shows each slot's generation and label, creates new worlds at the latest frozen generation, and offers a generation selector under its advanced options (older generations for comparison, and review features in development builds). It replaces the separate v7 and v8 checkboxes for v9 worlds.
- The tool also regenerates the menu showcase world when the user asks; it stays on its current recipe otherwise.

**Fixtures and goldens.**

- A test pins the generation it was written for, the way the launcher harnesses were pinned to v6. `EmitterSiteTestWorlds` gains a generation argument. A later step therefore never invalidates an earlier test.
- Goldens are added, never re-captured. Each step adds two or three look goldens at its own generation, per machine. Existing goldens are generation 1 fixtures and stay valid.
- `world_generation_fingerprint_test` (headless, seconds): one row per frozen generation, holding hashes of heights at sample points, tree, grass and scatter records for eight tiles, and the catalog identity. Append-only. It proves a later step did not change an earlier generation.
- `world_generation_contract_test` (headless): at each frozen generation, exactly the ledger's features are reachable.
- No dedicated GPU regression test for old generations. Each phase report includes one `compare` heatmap of an older-generation pose. All current worlds are test worlds, so a subtle miss costs little, and this is the proportionate level.

**Settings.** Grave-key settings and quality presets are global and stay global. Changing a setting is the user's own action and affects every world. A step may not change a default for all worlds. If a step needs different default values (the vastness haze calibration is the one known case), that settings resource loads and saves a separate file from that generation on, created by copying the old file once, and the old file is left alone. The Cards default is global on purpose: the user's explicit exception.

**Cost of the rule, and retiring generations.** Every step leaves a branch behind. Appended data keeps old data stable: new tree variants go after index 29 so the 30 existing indices and impostor layers never move. Branches and goldens grow with each step, so the plan asks for a floor: generations below it are deleted along with their branches, fixtures and test worlds (question 14).

### 2.3 What a ring implies

- **Engineered ground.** There is no tectonic uplift and no geological history. Every landform was sculpted and drainage was designed, so every valley drains and river sources can be machines. Names such as "moraine" or "karst" are used below for shape only.
- **A tell that already exists.** Plateau terraces are quantised in absolute height (`Terrace(x, 160 m)`), so every plateau on the ring shares the same bench levels: a builder's grading datum. The plan keeps it and extends it to rock strata.
- **Spin gravity** (derived, assuming 1 g at a 5,000 km radius: spin rate 1.4e-3 rad/s, period about 4,490 s, matching the 4,500 s day). Gravity is uniform and all connected seas share one level. Coriolis force acts only on motion along the ring (a 0.3 % weight change at 10 m/s) and on vertical motion (a 100 m waterfall drifts 0.4 m anti-spinward). It does not act on motion across the ring, so rivers have no Coriolis asymmetry and the plan does not invent one.
- **Rim walls.** Water cannot leave across the ring. Drainage that reaches a wall turns and runs along its foot.
- **Scale.** The ring is 318 km wide and the user often flies high (the saved pose is at 18.6 km). River lines, lakes and forest mosaics must read from the air as well as from the ground.

## 3. Area 1: water (the centrepiece)

### Ideas considered

| Idea | Verdict | Reason |
|---|---|---|
| Trunk rivers from a global drainage graph | adopt (T2) | The only way to get rivers that reach the sea |
| Lakes from filled depressions | adopt (T2) | Falls out of the same bake |
| Current that pushes the player and floating bodies | adopt (T3) | User decision |
| Safe and dangerous rivers, readable from the land | adopt (T3) | User decision |
| A generic floating system | adopt (T3) | User decision |
| Estuaries | adopt (T2) | Free: carve the last reach below 0 and the sea floods it |
| Water colour, glint and whitewater on far terrain | adopt (T2, T3) | The far representation of rivers and lakes |
| Wetlands on low-gradient floodplains | adopt (T6) | Material and vegetation, little new shape |
| Ponds in hummock hollows | adopt (T7) | Stateless; arrives with the landforms |
| Small streams (2-20 m) with real water | contingent (T9) | Needs a tile-local network; large |
| Deltas, waterfall meshes | contingent (T9) | Polish on top of T2 and T3 |
| Rivers that rise in storms | reject for now | Dynamic hydrology; static first |

### 3.1 Rivers and lakes: a baked global graph for topology, a local operator for shape

**Why a global graph.** A river must run downhill all the way to the sea, and its size depends on everything upstream. No stateless local function can know that. The project already has the pattern this needs: registries built once from the unshaped natural height, cached by definition hash, and applied as local operators (`EmitterSiteTerrainShaper`).

**Climate in the recipe.** Runoff is weighted by moisture, and the graph is frozen with the world, so the moisture and temperature base fields are recipe fields in v9. Their defaults equal today's settings values, so a v9 world's biomes match what v8 would have produced for the same seed.

**The bake** (once per new world, cached under `ring_cache` like the min-max pyramid):

1. Sample the natural height on a grid of about 1 km: 31,416 x 318, about 10 million cells, 5.2 million of them land. Est. 3-5 s on the laptop's worker pool.
2. Mark site, Wall Engine and Weather Lung influence discs and the rim walls as barriers, so rivers route around pads. The Weather Lung basin and the ocean are outlets.
3. Condition the surface: breach small depressions (the river cuts a gap through the sill) and fill large ones (they become lakes at the sill level). A recipe field, lake abundance, sets the boundary.
4. Route flow to the steepest lower neighbour, accumulate runoff weighted by moisture, and keep cells above a catchment threshold (est. 50-100 km^2 in moist climate) as channel nodes. Est. 0.5-1 million nodes.
5. Per reach, derive the hydraulics (section 3.2): discharge, gradient, depth, speed, width, confinement and danger class. Store position jitter, water level and the downstream index with them. Per lake: level, cell set, inlets, outlet.

Ties break by cell index, so the result is deterministic. The definition hash gains a drainage algorithm version. The whole graph stays resident (gate 16 MB), which is what lets physics query it anywhere.

**The operator** (in the height model, after the natural height and before site operators win inside their discs):

- **Path.** A smooth curve through the nodes, with meander amplitude that grows as gradient falls.
- **Water level: flat pools joined by short chutes.** The level is constant along a pool and drops 1 m over an 8-12 m chute. The reach gradient sets the spacing: a chute every kilometre or two on a lowland river, every 20-50 m on a steep one. The level becomes a float in `TerrainSample` (it is an integer today) so chutes can slope.
- **Cross-section.** Bed below the water by the reach depth, shallower in chutes; banks just above the water; a valley floor whose width grows with discharge and shrinks with gradient; side slopes that meet the natural ground. Where the natural ground is far above, the side-slope limit produces a gorge with the same 40 m strata steps as the breaks. Where it is near, a broad vale with one or two river terraces.
- **Invariants.** The carve only lowers ground inside the corridor, is C0, never produces land at or below 0 outside the estuary reach, and keeps banks above water. Lakes get a rim raise along their boundary so the fine terrain cannot leak below the lake level (no vertical water walls).
- **Lookup.** A flag per 1 km cell ("no channel within reach") makes most samples exit in a few nanoseconds; otherwise distance to at most half a dozen segments. Est. under 0.2 us where a river is near.
- **Coarse LODs.** The carve takes a footprint argument and scales channel depth by width over footprint, so a 30 m channel does not bead at 64 m spacing while the valley survives.

**Classification and rendering.**

- The sampler returns `River` or `Lake` and the level through the fields that already exist. The controller's hard-coded ocean rule (1.1 item 6) is replaced by the sampler's answer where the world has the rivers feature.
- Near water uses `ring_ocean_proxy.gdshader` with its lake and river profiles: no FFT, analytic ripples, flow animation and foam (3.2). It already takes haze, cloud shadows, rain ripples and wetness through the shared includes. No "river haze" or "lake shadow" switch is added.
- Far terrain gets a water fraction per sample (channel area over cell area) and blends water colour, a glint term and a whitewater term by it, so a river stays a continuous line at any distance.
- **GPU proxies.** Sectors that touch a river corridor or lake build on the CPU, as site and coastal sectors do. Est. 10-20 % of land sectors at the 2 km level. If that hurts streaming, T9 ports the carve to GLSL. The rivers step changes no noise term, so the GLSL host only needs v9 on its accept list.

**Storms, edits, world-building.** Hydrology is static: rivers do not rise in rain, and digging a bank does not divert water. Banks get a permanent wetness term. A great river's source is an outfall, not a spring; T2 needs no art for it, and T9 lists headworks and wall-foot spill fans as options.

### 3.2 Current: the flow field

**Decision (user): rivers push the player and floating bodies.**

**One source of truth.** A single function, `RingWaterFlow.Sample(position)`, in the hydrology module, returns water kind, surface height, bed height, velocity (around and across, m/s) and the reach's danger class. It is a pure function of position and the resident graph. Physics, the water mesh, the far shader and audio all call it. Nothing reads flow from a mesh, a texture or a streamed chunk.

**Speed comes from the land.** Width and depth scaled from discharge alone give almost the same speed everywhere, so they cannot produce safe and dangerous rivers. Gradient can. Per reach the bake computes:

- depth d from discharge Q (grows as about Q^0.4);
- mean speed U from depth and gradient S by the standard open-channel relation, U = d^(2/3) S^(1/2) / n, with one roughness constant n;
- width w = Q / (U d), so the water that arrives is the water that leaves;
- confinement: where the valley is narrower than that width (a gorge, or a gap breached through a sill), the channel is squeezed to the valley and deepens and speeds up to carry the same Q.

The carve uses the same w and d. A wide slow river on a plain and a narrow fast one in a gorge are therefore the same rule seen at two gradients.

| Part of the field | Rule |
|---|---|
| Direction | The centreline tangent at the nearest point, downstream |
| Along a reach | Pool: U. Chute: depth thins to about a third, so speed rises up to threefold, capped |
| Across the channel | 1.5 U at the centre falling to zero at the banks (parabolic; the mean is U) |
| With depth | Full at the surface, 0.6 at the bed |
| Lakes | Still, except a draw toward the outlet within about 100 m of it and a decaying jet at each inlet |
| Estuary | Fades to zero over the last reach |
| Ocean | None; waves do not push, as today |

Worked examples with n = 0.035 (derived): depth 3 m at gradient 0.0002 gives 0.8 m/s; depth 2 m at 0.005 gives 3.2 m/s; depth 2.5 m at 0.02 gives 7.4 m/s; depth 2 m at 0.05 gives 10 m/s. Caps: 10 m/s in pools, 12 m/s in chutes, both times the current strength setting.

### 3.3 Safe and dangerous rivers

**Decision (user): "there can be a variance, of safe rivers and dangerous rivers."**

Danger is a class per reach, derived in the bake from the reach's own hydraulics. It is not random and has no per-river switch.

| Class | Centre speed in pools | Where it occurs | For a swimmer (swim 5.5 m/s, sprint 8.5 m/s) |
|---|---|---|---|
| Calm | under 1.5 m/s | lowland meanders, lake outlets, estuaries | Swims anywhere; barely drifts |
| Swift | 1.5-4 m/s | rolling country, the foot of hills | Carried while resting; makes headway by swimming |
| Fierce | 4-7 m/s, chutes to about 9 | hill-country valleys, water gaps through sills | Cannot hold the centre without sprinting; can still work to the slower water at a bank |
| Torrent | over 7 m/s, chutes to 12 | gorges and steep confined reaches | Swept downstream even when sprinting mid-channel; banks are often cliffs |

Targets by river length (est., set from the T0 prototype): at least 70 % calm or swift, 5-10 % fierce, at most 3 % torrent. A recipe field, river ferocity, scales n and the caps, so the mix is a world property frozen with the recipe.

**What dangerous means.** Loss of position and control, never damage and never death. The game has no health system and this plan does not add one.

- A fierce or torrent reach carries the player downstream, possibly far: 10 m/s is a kilometre in under two minutes.
- Chutes dunk the swimmer: the float target drops with the surface and the camera goes under for a moment.
- Where banks are steeper than the walk limit there is no way out until the valley opens.
- It always ends. Gradient falls downstream, so every dangerous run empties into a calmer reach, a lake or the sea. The bake checks that every fierce or torrent reach is followed within 5 km by a calm or swift reach with a walkable bank, and softens any that is not.
- Nothing pins or traps. Flow runs along the channel, and the swimmer can always move sideways relative to the water.
- No dangerous reach touches an emitter site, the Weather Lung or the spawn area.

A torrent is also a road: the fastest way down a mountain valley is the river.

**Reading danger before entering.** Every cue is driven by the same reach speed, so they cannot disagree.

| Cue | Calm | Dangerous | Where it comes from |
|---|---|---|---|
| Surface | glassy, reflective | streaked, then broken white across the channel | foam coverage from per-vertex speed |
| Sound | quiet lapping | a roar audible 300-500 m away | river ambience level from speed times discharge |
| Bank shape | floodplain, bars, low banks | narrow V-valley or gorge, strata walls | the carve, from gradient and confinement |
| Chutes | rare, low | frequent, with standing waves and rocks | chute spacing from gradient; T5 boulders |
| Drift | slow flecks | fast flecks | surface pattern advected at the true speed |
| Plants (T6) | reeds, gallery trees to the water | bare rock margins | communities read the reach class |
| From the air | dark or blue line | white line | far-terrain whitewater term |

The minimap can colour river lines by class later; nothing depends on it.

**Settings.** One live setting, current strength (0-2, default 1), scales every speed for physics and for the visible flow together. There is no per-river toggle. At 0 the water is still everywhere.

### 3.4 How flow reaches the player

- *Swimming.* The swim motor's target velocity becomes input velocity plus water velocity: the swimmer moves relative to the water. With no input the player drifts at the local speed. The existing `SwimAcceleration` sets how fast the current picks the player up.
- *Wading.* In water shallower than the swim entry depth, the player gets an acceleration toward the water velocity scaled by depth over entry depth, squared. Ankle-deep water is harmless; knee-deep fast water shoves.
- *Float.* The float target follows the float surface, including down a chute.

### 3.5 The floating system

**Decision (user): "just put in the system for floating first we will be able to easily add floating items after."** T3 builds the generic system and proves it with a test-only body. No game object ships with it.

**Parts.**

- `RingBuoyancy`: a static force model. Given sample points on a body, its mass and its draft, it returns lift from the submerged fraction and drag toward the water velocity from `RingWaterFlow`.
- `RingFloatingBody`: a component added to a body. It converts the body's position to canonical coordinates, samples the flow at one to four points, and applies the forces. Exported fields: draft, drag, sample points (derived from the collision shape by default), and whether the body may strand.
- **Settling on banks.** Speed is zero at the banks and low over shallows, so bodies slow there by themselves. When the water under a body is shallower than its draft, bed friction takes over; a body that stays nearly still for a few seconds goes to sleep and costs nothing until something moves it.
- **A manager.** Bodies simulate only within a radius of the player (est. 300 m) and under a count cap (est. 32). Beyond the radius they sleep or are removed. They are not saved.

**Check first.** No rigid body exists on the ring today, and the world uses a floating origin. The T3 design step tests whether a `RigidBody3D` survives an origin shift. If it does not, `RingFloatingBody` integrates its own motion in canonical coordinates, as the player controller does, behind the same component interface. Items added later do not care which.

**Adding a floating item later** (to be written up as `docs/floating-items.md` in T3):

1. Make a scene: a body, a collision shape, and a mesh on a shader that uses the shared lighting includes.
2. Add `RingFloatingBody` and set mass and draft, or leave them on automatic.
3. If the item should appear by itself (drift logs, for example), add a row to the floating item catalog with its spawn rule; otherwise spawn it from game code.
4. Add the scene path to `floating_item_validation_test`, which checks that it floats at its draft, drifts at the flow speed, strands and sleeps on a bank, and survives an origin shift.

### 3.6 Visible flow matches the force

- Each water-mesh vertex stores the velocity from `RingWaterFlow.Sample` at build time. The shader advects its normal and foam layers by that velocity in world metres per second (two blended phases, the standard flow-map method), so foam streaks travel at the speed a swimmer is carried.
- Physics and shader multiply by the same global, current strength.
- Foam coverage and the ambience level are functions of the same speed (3.3).
- Beyond a few hundred metres the animation fades by pixel footprint; the water fraction, glint and whitewater terms remain.

### 3.7 Consistency across LODs and streaming

- Physics queries the graph, which is fully resident from world load. A river pushes correctly whether or not the terrain or water mesh under it has streamed in, and at any LOD.
- The carve, the water level and the flow all derive from the same reach width, depth and centreline, so the bed, the surface and the velocity agree at every point. A test asserts that the flow sample's bed and surface equal the terrain sampler's at the same position.
- Coarse LODs shrink the channel only beyond swimming range; the Terrain3D window and the 2 m proxies always carry the full section.

### Rejected alternatives

- **Local carving only** (noise-threshold rivers, or a wider gully filter). No continuity, no monotone level, no discharge, so no meaningful current.
- **Generating all lowland relief from the river network.** It would replace the v7 landforms the user approved.
- **Routing per tile at runtime.** Trunk flow is not local.
- **A continuously sloped water surface.** Pools and chutes keep most water flat for the renderer and the swim logic and put the fast water where the player can see why it is fast.
- **Danger assigned by noise or per river.** It would not be readable from the land, which is what the user asked for.
- **Damage or drowning.** No health system exists; displacement is consequence enough and fits an exploration game.
- **A simulated flow texture** (a 2D fluid solve around the player). It would disagree with the baked discharge and need readback for physics.
- **Flow read from the water mesh.** Physics would then depend on streaming and LOD.
- **A first floating object built into T3.** The user asked for the system first.

### Systems touched

A new `scripts/Terrain/Hydrology/` (bake, graph, cache, carve operator, `RingWaterFlow`, `RingBuoyancy`, `RingFloatingBody`, the floating manager and catalog), `TerrainGenerationHeightModel.cs`, `TerrainGenerationParameters.cs`, `EmitterSiteTerrainShaper.cs` (ordering and hash), `RingTerrainSampler.SurfaceGeneration.cs` (water kind, float level, biome weights, definition hash), `TerrainSample` and the proxy sector cache format, `RingTerrainProxySector.cs` (inland water mesh, per-vertex flow, CPU routing of river sectors), `ProxyGpuHeightKernel.cs` (accept v9), `materials/ring_ocean_proxy.gdshader` and `ring_water_*.gdshaderinc`, `ring_terrain_global.gdshader` and the far tiles (water fraction, glint, whitewater), `RingTerrainMinimap.cs`, `RingTerrainMinMaxPyramid.cs`, `RingVoxelExplorerController.cs` (water state, swim and wade push), the water ambience, the loading screen (bake progress), `world_size_launcher.gd`.

Grave-key menu. Terrain3D tab (recipe, frozen per world): river density, lake abundance, valley width, channel depth, meander strength, river ferocity. Water tab: river and lake colour and turbidity, and current strength. The drainage and danger overlays go in the preview's view modes and the minimap's debug views, not the menu.

### Cost

Bake: gate 20 s on the laptop, once per world, shown on the loading screen. Graph: gate 16 MB resident. Sampling: gate +0.2 us near rivers and under +0.02 us elsewhere. Flow queries: one per frame for the player and up to four per active floating body, est. under 0.3 us each; floating bodies gate 0.1 ms of physics at the cap. Rendering: gate 0.3 ms on the A1000 at a river pose, including flow animation and foam. Engineering: T2 large (lead design, likely with `claude-deep` on depression conditioning and lake containment, about nine work orders); T3 medium-large (lead design, about nine).

### Verification

- `drainage_graph_test` (headless): deterministic across two bakes and worker counts; no cycles; every channel ends in the ocean, a lake or the Lung basin; level never rises downstream; periodic across the ring seam; no node inside a site disc.
- `river_carve_invariants_test` (headless): C0 across corridor edges; banks above water; no vertical water walls on 10,000 lake-boundary samples; zero inland points at or below 0 outside estuaries; sites still build.
- `river_flow_field_test` (headless): U times w times d equals Q along a reach within tolerance; zero at the banks; velocity points downstream; no flow on land or in the ocean; speeds within the caps; bed and surface equal the terrain sampler's; seam periodic; deterministic.
- `river_danger_test` (headless, two seeds): class shares by length inside the target bands; class is monotone in gradient at fixed discharge; every fierce or torrent reach has a calm or swift reach with a walkable bank within 5 km downstream; none touches a site, the Lung or the spawn area.
- `river_current_swim_test` (physics): in a calm reach a resting player barely drifts; in a swift reach the player drifts at 0.8-1.2 times the surface speed and can swim upstream; in a fierce reach the player loses ground at the centre without sprint and gains it near a bank; in a torrent the player is carried to the next calm reach and can then leave the water; the player is never stationary against the flow for more than a few seconds without input; wading in 0.3 m of fast water pushes and in 0.1 m does not.
- `floating_item_validation_test` (physics, takes scene paths): the test body floats at its draft, drifts at the flow speed within 20 %, goes over a chute, strands and sleeps on a bank, and survives an origin shift; the manager respects its radius and cap.
- `river_flow_visual_parity_test`: the per-vertex velocity equals the CPU sample within 5 % at 200 vertices; the shader's advection distance over 10 s equals the test body's drift within 15 %; foam coverage and ambience level are monotone in speed.
- `inland_water_render_gpu_test` (windowed): a river and a lake pose receive haze, cloud shadow and rain ripples; the haze contracts hold on water.
- `river_lod_consistency_gpu_test`: fly-out from 50 m to 200 km over a river; `flicker` shows no beading or popping.
- `drainage_bake_benchmark`; the swim smoke and water ambience regression extended to lakes and rivers.
- Captures: a calm river and a torrent from the bank, from 2 km and from 18 km; a swim down a fierce reach; `sheet`s and an `mp4` for the user.

## 4. Area 2: lowland landforms (the "landforms" step)

### Ideas considered

| Idea | Verdict | Reason |
|---|---|---|
| Rolling ground at 30-300 m | adopt (T7) | Fills the gap; a few noise terms |
| Hummocks with closed hollows that hold ponds | adopt (T7) | Stateless, GPU-portable, ties shape to water |
| Dunes aligned to the prevailing wind | adopt (T7) | Climate is in the recipe from v9; a compass |
| Buttes and mesas near plateau edges | adopt (T7) | Reuses the terrace function; landmarks at 1-30 km |
| Tors and rocky knolls on crests | adopt (T7 shape, T5 meshes) | Shape gives the mound, meshes give the rock |
| Coastal cliffs, headlands, coves | adopt (T7) | The coast is a uniform plain today |
| River valleys, gorges, terraces, bluffs | adopt (T2) | They come from the river carve |
| Dry pits on plateaus | adopt as a hummock variant (T7) | Same function, no pond |
| Asymmetric ridges | later | A hill-profile variant |
| Badlands beyond today's "breaks" | contingent (T9 stamps) | Noise does this poorly |
| True sinkholes opening into caves | contingent (T9) | Gen-17 forbids caves and overhangs |
| Overtly engineered forms (canals, geometric basins) | not planned (decided, question 5) | The builders' hand shows at map scale only |

### Recommendation

A ledger feature in the v9 height method, added after rivers. Rivers come first because the dependency runs one way: rivers are routed on the height without small detail, and the detail must fade out on river beds and valley floors.

1. **A meso-relief layer** inside the existing landform gate, applied after the river carve and fading to zero on beds and valley floors. A baseline everywhere, and province styles chosen by the existing ruggedness and plateau fields, the recipe climate and one new style noise (about 48 km):
   - *baseline roughness*: 2-6 m at 30-120 m on plains, with plains slope p95 held at or under 12 degrees;
   - *hummock and pond*: cellular knolls of 8-25 m at 150-400 m with closed hollows. A hollow's pond level is a constant per cell from the cell hash, so ponds are stateless and still;
   - *dunes*: asymmetric ridges of 5-30 m at 100-400 m spacing, gentle upwind and 32 degrees downwind, in desert climate and on a coastal strand;
   - *tors*: steep-sided knobs of 8-30 m on convex crests where ruggedness is high;
   - *buttes*: flat-topped outliers one or two terrace steps high (40-160 m) near plateau edges;
   - *dry pits*: the hummock function inverted, on moist plateaus.
2. **Coastal variety.** Let the landform gate reach the shore in regions chosen by a coast-style noise, giving cliffed coasts and headlands. The rest keep today's beach profile.
3. **Band-limited sampling of noise terms.** The footprint argument introduced for the carve is extended to the noise terms: a term whose wavelength is under about four times the footprint fades to its mean. This closes the open coarse-LOD mismatch (1.1 item 4) and makes coarse samples cheaper.
4. **GLSL port and parity** for the new terms and the band-limit rule, so the 8-32 m proxy levels stay on the GPU.

The drainage graph does not change: routing ignores meso-relief, so a landforms-generation world has the same rivers as the rivers-generation world of the same seed.

### Rejected alternatives

- **Hydraulic erosion simulation.** The ring is 10 million km^2; tile-local simulation breaks statelessness, seams and GPU parity.
- **More octaves of the same noise everywhere.** Fixes the gap but not the lack of structure. The baseline roughness is this idea kept small.
- **Heightmap stamps.** Strong, but adds an asset pipeline and repetition visible from altitude. Kept as a T9 option.
- **A second recipe id.** The generation ledger already separates the step.

### Systems touched

`TerrainGenerationHeightModel.cs`, `TerrainGenerationParameters.cs`, `terrain_generation_recipe.gd`, the preview and its store, `materials/terrain_gpu/ring_height_model.glslinc`, `ProxyGpuHeightKernel.cs`, the proxy, global LOD, far tile and pyramid callers (footprint), `EmitterSiteTestWorlds`.

Grave-key menu, Terrain3D tab, a section "Lowland detail": baseline roughness amplitude and scale, hummock height and spacing, pond abundance, dune height and spacing, tor frequency, butte frequency, coastal cliff share. Recipe fields, frozen per world, tuned in the preview.

### Cost

Est. +2 to +4 noise calls on eligible lowland, about +10-20 % per sample. Gate: plains and hill tiles at most 1.25x their v7 medians. Engineering: medium-large (lead design, about seven work orders).

### Verification

- `landform_meso_relief_test` (headless; sections recipe, gd_recipe, shape, bandlimit, world): coverage per style; relief per wavelength band against the T0 targets; slope limits; zero inland flooding; C0; seam periodicity; worker determinism; heights equal the previous generation exactly where no new term applies; the drainage graph identical; sites build.
- The `lodgap` section with band-limiting: p99 edge mismatch under the 8 m skirt at every spacing.
- `proxy_gpu_height_parity_test` and `proxy_gpu_sector_parity_test` extended.
- `terrain_recipe_freeze_test`: earlier rows unchanged; a new row at freeze.
- The travel benchmark on the new generation: zero coverage waits and support misses.

## 5. Area 3: ground surface and rocks

### Ideas considered

| Idea | Verdict | Reason |
|---|---|---|
| Rock instead of dirt on steep ground, with strata | adopt (T4) | Near-zero cost; no new channel |
| Scree band below steep ground | adopt (T4) | Marks the foot of every slope |
| River bed gravel, bank mud, wet sand | adopt (T4) | Rivers need a bed that is not grass |
| Colour variation by wetness and soil at 50-500 m | adopt (T4) | Reveals relief from afar |
| Detail normal on rock, near only | adopt (T4), gated | The only added samples |
| Boulders (1-4 m), outcrops (5-40 m), cobbles | adopt (T5) | Human-scale objects and landmarks, missing today |
| Speckle on far terrain where rock fields are | adopt (T5) | Far representation |
| Photogrammetry rocks | later, optional | Texture memory; style mismatch |
| Rock as terrain overhangs | reject | Gen-17 is a heightfield |

### Recommendation

**T4, the surface** (the "ground" step). In the shared terrain albedo include, behind the feature:

- Replace the steep-slope dirt blend with a rock material. Rock exposure is a function of slope and, near the camera, convexity from the height taps the wet pass already uses.
- Horizontal strata on rock: albedo bands in absolute height on the 40 m breaks step, with a fine sub-band near the camera. A cliff or gorge wall reads as layered stone at the same bench levels as the plateaus.
- A scree tone on 28-38 degree slopes below rock.
- Bed and bank materials along rivers and lake shores from distance to water and depth, through the site-mask route that already overrides biome weights with rock and sand.
- Low-frequency colour variation: paler on convex ground, greener and darker in hollows and near water.
- One detail normal on rock within about 60 m.

Rock replaces dirt sample for sample. Rock and scree textures are CC0 imports (section 7.2).

**T5, the scatter foundation** (the "rocks" step).

- **Habitat field grid.** Each vegetation tile computes the section 2.1 fields on an 8 m grid from height samples and the drainage graph. Every scatter class and the grass builder read it.
- **Scatter classes.** Where the world has the feature, the tree tile becomes a table of classes: a response to the habitat fields, a spacing radius, a size distribution, alignment, LOD distances, a collision shape and a far term. The pipeline is the existing one: canonical cells, hashed candidates, rejection, records, disk cache, GPU roots. Older generations keep the current tile build.
- **Rock meshes, procedural.** A tool script generates 16 boulder, 8 outcrop and 6 cobble meshes with three LODs each into a prepared resource. Rocks use the terrain's rock material in world-space triplanar, so they need no unique textures and match the ground. Outcrops keep their bedding horizontal and take the strata bands. The base of each mesh blends to the terrain colour and darkens slightly.
- **Placement rules.** Boulders in hollows, at slope feet below steep ground, and in chutes of fierce and torrent reaches. Outcrops on convex crests, steep faces and gorge walls. Cobbles on scree, river beds and bars.
- **Lighting.** One `ring_scatter_rock.gdshader` built on the shared haze, shadow-compose, cloud-shadow and wetness includes, so rocks take haze, shadows, lighting and rain like terrain with no per-class switch.
- **Collision.** Boulders and outcrops within 110 m get static bodies from a pool, like trunk colliders.
- **Far term.** A speckle and rock tint in the proxy shader where rock density is high.

### Rejected alternatives

- **Terrain3D's built-in instancer.** Tied to the 768 m window; the forest checklist says not to add a second streamer.
- **Imported rock packs first.** Unique 2-4K texture sets strain the A1000 and will not match a triplanar terrain.

### Systems touched

`materials/ring_terrain_surface_albedo.gdshaderinc`, `terrain3d_ring_surface.gdshader`, `ring_terrain_proxy.gdshader`, `ring_terrain_global.gdshader`, `ring_surface_far.gdshader`, new rock albedo and normal textures, `RingBiomeDecorationLayer.TreeStreaming.cs`, `RingGpuVegetationCompute.cs` and `ring_gpu_vegetation_indirect.glsl` (rocks register in tree mode; per-class caps), a rock generator under `tools/`, a new shader, the collider pool.

Grave-key menu. Surface terrain tab: rock exposure threshold, strata contrast, colour variation strength. Vegetation tab: rock density, scatter distance scale.

### Cost

T4: gate 0.4 ms on the A1000 at a hill pose. T5: gate 0.5 ms and 24 draws at Medium, with at most 1,500 boulders, 150 outcrops and 6,000 cobbles resident; tile build at most 1.3x the tree-only tile; under 8 MB of mesh data. Engineering: T4 small (four work orders), T5 medium (seven).

### Verification

- T4: `terrain_rock_material_gpu_test` (rock on a 50 degree synthetic slope, none at 10 degrees; strata phase equal between the Terrain3D and proxy shaders at the hand-off; without the feature the output equals the pre-change shader); `flicker` on a slow pan for strata shimmer.
- T5: `scatter_tile_determinism_test` (identical records across runs, build orders and tile seams); `scatter_rock_mesh_validation_test`; `scatter_haze_shadow_gpu_test` (a boulder matches adjacent terrain haze at 50 m, 500 m and 2 km); `scatter_collision_test`; the travel benchmark with scatter on.
- The fingerprint and contract tests of section 2.2 at each commit point.

## 6. Area 4: vegetation diversity

### Ideas considered

| Idea | Verdict | Reason |
|---|---|---|
| Authored size classes as stand age | adopt (T6) | The packs already contain the age ladder |
| Saplings as an understory class | adopt (T6) | Six authored sapling meshes exist |
| Species by habitat, not noise pockets | adopt (T6) | Makes three species mean something |
| Snags and fallen trees from the bark surfaces | adopt (T6) | Bark and leaves are separate in every variant |
| Shrubs from the leaf-cluster textures | adopt (T6) | Three looks, no new art |
| Per-instance and per-stand colour variation | adopt (T6) | From the existing instance seed |
| Treeline thinning and dwarfing | adopt (T6) | Temperature already exists |
| Riparian gallery woods, wet meadows, reeds | adopt (T6) | Rivers exist by then |
| Flowers, ferns, tall grass, reeds as card variants | adopt (T6) | Cards are the default (decided) |
| Canopy tint on far terrain | adopt (T6) | The unchecked forest checklist item |
| New tree species | adopt (T8), after T6 | User: use the existing packs first |
| Seasons, growth, felling | reject for now | A different feature |

### Recommendation

**Communities, not species noise.** A small table of plant communities chosen by the habitat fields and climate. Each lists what grows in four layers.

| Community | Where | Canopy | Understory | Field layer | Dead wood, rock |
|---|---|---|---|---|---|
| Closed broadleaf forest | moist, deep soil, cover high | maple, mixed ages | maple saplings, sparse shrubs | ferns, litter | fallen trees, snags |
| Conifer stand | cool or high (fir); dry, sandy or rocky (pine) | fir or pine | conifer saplings | needles, few forbs | snags |
| Open woodland | cover medium | scattered large trees | shrubs | tall grass, flowers | few |
| Forest edge | the sampler's Edge class | small and medium trees | dense shrub belt | tall grass, flowers | some |
| Clearing | the Clearing class | none | few shrubs, saplings | flowers, tall grass | boulders, a stump |
| Dry meadow | convex, thin soil | none or lone trees | low shrubs | short grass | cobbles, outcrops |
| Wet meadow | concave, wet | none | broadleaf shrubs | tall grass, reeds | none |
| Riparian gallery | along calm and swift reaches and lake shores | large maples, dense | dense shrubs, saplings | reeds, ferns | drift logs |
| Rocky fell, scree, fierce and torrent margins | exposure high | small pines | pine scrub | sparse | all rock classes |
| Strand and dune | coastal, dune provinces (T7) | none | sparse | dune grass | drift logs |

**Use the packs fully** (details in 7.1):

- **Stand age from the authored size classes.** A stand-age field (about 600 m patches) picks the size class: young stands draw saplings and small trees at close spacing, old stands draw large trees at wide spacing with a few snags. Scale jitter stays small because the classes carry the variety.
- **Understory.** Saplings leave the canopy lottery and become their own class: denser, near-only, under and around their parent species, thickest at edges and in clearings.
- **Species by habitat.** Maple on moist deep soil and along water. Fir on cool and high ground and toward the treeline. Pine on dry, sandy and rocky ground. The existing pocket noise remains as a secondary variation.
- **Treeline.** Density falls and the size class drops with temperature toward the alpine and tundra limits; the last trees are small-class firs and pines.
- **Dead wood.** Snags are variants with the leaf surface dropped. Fallen trees are the bark surface of a medium variant laid along the slope. Stumps are the bark mesh cut at about 0.6 m by the preparation tool.
- **Shrubs.** A procedural class of crossed cards textured from the packs' own leaf-cluster atlases: a broadleaf bush from the maple clusters, pine scrub from the pine clusters, a low fir thicket from the fir boughs.
- **Colour variation.** A per-instance tint derived in the shader from the existing instance seed, plus a stand-level tint from a low-frequency field.
- **Ground cover as cards.** Flowers, ferns, tall grass and reeds as new card atlas variants chosen by community, replacing a share of grass cards in the same tile budget. The atlas art is a CC0 import (7.2). This builds on the Cards default; this plan does not touch the grass default itself.
- **Grass follows the fields.** Thinner under closed canopy, taller in wet hollows, shorter on dry crests, absent on beds and bars.
- **Far canopy.** Fold the forest sampler's cover into the forest biome weight at proxy and far-tile vertices, and add a canopy tint and a darker, rougher shading term past the impostor range. The GLSL climate port gets the same change behind the feature.

**T8, new species.** Only after T6 is reviewed. Candidates, each chosen to say something the three packs cannot: a willow or alder form that marks calm water from a distance, a pale-trunked birch or aspen form for wet and disturbed ground, and a broad solitary crown for open woodland. Each needs four LODs with crossed cards as LOD3, so the shared impostor takes it unchanged. (Decided, question 6.)

### Rejected alternatives

- **More species first.** The user asked for the existing packs to be used better, and the audit shows how much is unused.
- **Octahedral impostors.** The shared LOD3-card impostor already reaches 5.5 km.
- **A separate flower or shrub renderer.** The card path and the scatter table cover both.
- **Wiring the Biome Workshop presets into production now.** Later, as the authoring front end for the community table.

### Systems touched

`ForestRegionSampler.cs` (communities, stand age, habitat species), `RingBiomeDecorationLayer.*` (class table, variants), `ImportedTreeCatalog*.cs` and the prepare pipeline (size-class metadata, appended snag, log and stump variants), `ImportedTreeImpostorCatalog.cs` (more layers, appended), `materials/imported_tree.gdshader` (tint), `RingGrassTileBuilder.cs`, `RingStandingGrassLayer*.cs` and the card atlas, `RingGrassCardPlacement.cs` (more atlas variants), `RingTerrainSampler` biome weights and `ring_regional_climate.glslinc`, the proxy and far shaders (canopy tint).

Grave-key menu, Vegetation tab: understory density, ground-cover share, dead-wood density, stand-age contrast, foliage colour variation. Presets scale distances and caps. No per-class toggles.

### Cost

T6: gate 0.3 ms on the A1000 at a forest-edge pose and 16 draws at Medium, with at most 3,000 shrubs and 400 fallen trees resident; ground cover is budget-neutral by substitution; appended variants add impostor layers (gate 8 MB). T8: gate 24 MB of VRAM for three species. Engineering: T6 medium (about nine work orders), T8 medium plus asset time.

### Verification

- `forest_structure_test` (headless): the size-class mix follows stand age; saplings only appear in the understory class; species follow habitat; treeline falloff is monotone in temperature.
- `understory_placement_test` (headless): shrub density at Edge is at least three times Dense; none on site masks, in water or on slopes over the limit.
- `riparian_vegetation_test` (headless): gallery density falls with distance from a channel; nothing in the channel; reeds only on calm margins.
- `tree_catalog_append_test` (headless): the first 30 variants, their indices, meshes and impostor layers are byte-identical to the pre-change prepared catalog.
- `far_canopy_tint_gpu_test`: the tint boundary matches the tree impostor boundary within one proxy cell at 2, 5 and 20 km.
- Existing `tree_*`, `imported_tree_*`, `gpu_vegetation_*`, `grass_*`, `vegetation_haze` and `vegetation_shadow` tests pass unchanged on their generation 1 fixtures.
- Captures: forest interior, edge, clearing, meadow, river wood; a fly-out for the canopy hand-off.

## 7. Area 5: asset pipeline

### 7.1 Audit of the tree packs in the repo (derived from the glTF files)

Source: three LOLIPOP packs under `resources/trees/lolipop/`, CC-BY-4.0, prepared into `resources/trees/prepared/lolipop_catalog.res`. Every variant has four authored LODs; LOD3 is a crossed-card billboard. Bark and foliage are separate surfaces in every variant and LOD.

| Pack | Variants and authored size classes | LOD0 triangles (bark + foliage) | LOD2 | LOD3 | Textures |
|---|---|---|---|---|---|
| Maple ("Acer") | 12: sapling x3, small x3, medium x3, large x3 | sapling about 500; small 1.7-3.4k; medium 4.8-14.6k; large 13-22.5k | 150-5.3k | 36 | bark 2048 x 2048, leaf clusters 2048 x 2048, billboards 1024 x 1024; each with normal and metallic-roughness |
| Pine | 15: sapling x3, small x3, medium x3, big x3, large x3 | sapling about 600; small 1.9-2.3k; medium 2.9-4.4k; big 4.5-5.5k; large 10.1-11.8k | 270-3.2k | 16 | bark 1024 x 2048, needle clusters 2048 x 2048, billboards 1024 x 1024 (sapling 512) |
| Fir ("Christmas tree") | 3: one large, one medium, one small | 13.0k, 6.8k, 0.6k | 0.3-3.3k | 20 | bark 2048 x 4096, boughs 4096 x 4096, billboards 2048 x 2048 |

What production uses, and what it leaves on the table:

| # | In the packs | Used today | Plan |
|---|---|---|---|
| 1 | Authored size classes: 4 for maple, 5 for pine, 3 for fir | Ignored. `SelectForestVariant` picks uniformly, so sizes are mixed at random everywhere and scale jitter is only 0.85-1.15 | Size class driven by stand age (T6) |
| 2 | Six sapling meshes (3 maple, 3 pine) and one small fir | Placed as canopy trees: same spacing, rank gates and far impostor as a 20 m tree | A separate understory class, near-only (T6) |
| 3 | Bark and foliage as separate surfaces | Always drawn together | Snags (bark only), fallen trees, stumps (T6) |
| 4 | Leaf-cluster atlases for three species | Only as tree foliage | Shrub cards in three looks (T6) |
| 5 | Bark textures with normal maps | Only on trunks | Fallen trees, stumps, drift logs (T6) |
| 6 | Three species | Chosen by pocket noise (maple weight 0.85) unrelated to ground | Species by habitat (T6) |
| 7 | An instance seed in the tree shader | Used for dither only | Per-instance and per-stand tint (T6) |
| 8 | Authored LOD1 | Not drawn in the default compact profile | No change unless T0 shows the LOD0 to LOD2 switch at 25-40 m; then LOD1 fills it |
| 9 | Normal and metallic-roughness maps | Bound when present; distant LODs use simplified shading | No change; T0 confirms per LOD |
| 10 | Fir has only 3 variants | Fir stands repeat most | Size classes help; fir is the first candidate for a supplementary pack in T8 |
| 11 | Checker and reference planes in the maple and pine scenes | Ignored by the catalog | No action |
| 12 | KayKit trees and the procedural tree factory | Legacy path only | No action; removal is a separate cleanup |

Counted this way, the three packs yield roughly: 3 species x 3-5 age classes, alive or dead, plus saplings as understory, 3 shrub looks, fallen trees and stumps. That is more than thirty distinguishable plant forms with no new art.

Constraint on the derivations: the catalog hard-codes 30 variants and 12/3/15 per species, and earlier generations must see exactly those 30. Derived variants are appended after index 29 and are selectable only from the vegetation step on.

### 7.2 New assets (approved: CC0 and CC-BY)

| Class | Source | LODs and far form | Phase |
|---|---|---|---|
| Rocks (boulder, outcrop, cobble) | procedural, generated by a tool script | 3 mesh LODs, then terrain speckle | T5 |
| Rock, scree, gravel and mud textures | CC0 tileable sets (ambientCG, Poly Haven) | mip chain | T4 |
| Snags, fallen trees, stumps, saplings, shrubs | derived from the LOLIPOP packs (7.1) | the tree LOD chain, or 2 LODs for shrubs | T6 |
| Flowers, ferns, tall grass, reeds | CC0 foliage atlases, composed into the card atlas | card LOD, then terrain tint | T6 |
| New tree species | CC0 or CC-BY packs with 4 LODs and a card LOD3, or generated offline in a tree tool and exported the same way | the tree LOD chain and impostor | T8 |

Rules for imports: every new mesh uses the tree shader, the grass card shader or the new rock shader, all built on the shared lighting includes. Accept CC0 and CC-BY only. Do not take NonCommercial, NoDerivatives or ShareAlike licences: the pipeline makes derivatives (LODs, impostors, atlases), and ShareAlike would bind them. Prefer sources with clear provenance.

### 7.3 Licence and attribution

- **LOLIPOP packs (CC-BY-4.0).** Credit the author, link the source and the licence, and say that changes were made. Each pack's `license.txt` is kept verbatim. Everything derived from them (the prepared catalog, impostor layers, snags, fallen trees, stumps, shrub cards) is an adaptation and carries the same credit with a "modified" note.
- **CC0 imports.** No attribution is required. Record the source URL, asset id, retrieval date and licence in `THIRD_PARTY_ASSETS.md` anyway, and keep a licence note beside the files, as `resources/audio/water/LICENSE.md` does.
- **New CC-BY packs.** Same treatment as LOLIPOP: verbatim licence file, a credit line, a note of changes.
- **Distribution.** CC-BY credit has to be available wherever the work is shared. I found no credits screen in `scripts/` or `scenes/`. Before any build is distributed, either ship `THIRD_PARTY_ASSETS.md` with it or add a credits screen. This is a note for the primary agent, not a phase.
- **Doc fixes** (a work order in T0, docs only): `THIRD_PARTY_ASSETS.md` should say the LOLIPOP packs are the production trees, not "isolated review assets", and list the derived assets as they are added; `resources/trees/lolipop/README.md` and the forest checklist should describe the GPU-driven renderer instead of "bounded MultiMesh tiles".

Rejected: authoring hero meshes by hand (no artist in the loop), and runtime procedural tree generation (the import path, wind attachments and impostor all assume authored LODs).

## 8. Area 6: performance and far representation

**Placement stays on the CPU, culling on the GPU.** This keeps determinism, the disk cache and collision records. GPU placement is a T9 option for ground cover only.

**One streamer, per-class reach.** Trees stream to 5.5 km. Other classes live in the same tiles but are instantiated only within their reach: outcrops 2.5 km, boulders 600 m, shrubs 250 m, saplings and fallen trees 150 m, cobbles 50 m. Presets scale these.

**Draw calls matter as much as triangles.** The A1000's main-thread floor is 20-23 ms. New classes batch by mesh and LOD across tiles; the gate is 48 new draws at Medium for all phases.

**Shadows.** Only boulders and outcrops over 2 m cast into the near sun map, within the existing 128-batch cap. Per-instance vegetation shadows stay off below Very High.

**Height sampling.** The carve lookup is negligible away from rivers. Band-limiting (T7) makes coarse samples cheaper.

**Flow and floating.** One graph query per frame for the player and one to four per active floating body, with a body cap. No GPU simulation.

**Far representation, by class.** Forest becomes canopy tint. Rock becomes rock tone by slope plus speckle. Water becomes a water fraction with colour, glint and whitewater.

**Memory.** Drainage graph 16 MB resident. Rock meshes under 8 MB. New textures under 24 MB of VRAM at Medium. Appended tree variants 8 MB. Three new species under 24 MB.

**Feature branches.** A feature test is a uniform bit in a shader or a flag checked once per tile build; est. unmeasurable, and not separately gated.

## 9. Area 7: gameplay and world-building hooks

- **Rivers as roads and barriers.** A swift river carries a swimmer at a walk to a run. A torrent is the fastest way down a mountain valley and a one-way one. Calm reaches are crossings; fierce reaches need a sprint or a detour; gorges cannot be left until they open.
- **Reading water.** White water and noise mean speed. Reeds and low banks mean calm. Section 3.3 lists the cues.
- **Wayfinding by water.** Downstream leads to the sea or a lake; upstream leads to high ground and, for trunk rivers, to an outfall.
- **Readable plants.** Dense maples and shrub belts mean water. Pines mean dry or rocky ground. Firs mean cold or height. Young stands and clearings mean open sky.
- **Floating things.** Once items exist, anything dropped in a river travels: a way to send objects downstream, and a reason to look at bars and bends where they strand.
- **Compasses.** Dune slip faces point downwind (T7). This joins the vastness plan's Shade Wall and Sun Wall.
- **Altitude from the ground.** Bench levels and strata bands sit at fixed heights ring-wide.
- **Landmarks per band.** Lone boulders and tors at 50 m to 1 km, buttes at 1-30 km, lakes and river junctions from the air.
- **Naming.** The drainage graph gives every river, lake and basin a stable id for the minimap and the PDA to label later.

## 10. Interaction with the vastness plan

1. **One version ladder.** The user confirmed that new worlds only covers the vastness plan. Its visible changes are ledger features and take generation steps on the same ladder (its section 11). The vastness phases that change production code therefore depend on T1.
2. **Exact before and after.** A v9 world with no river feature has the same terrain as the v8 world of the same seed. A vastness step frozen before rivers gives a test world that differs from the old one only in rendering.
3. **Audit harness.** V0 and T0 need the same boot, poses, time-of-day search and band table. Build one harness with two reports.
4. **The bottom of the scale ladder.** The vastness ladder starts at a 25 m tree. T5 and T6 add the rungs below it (0.5 m cobble, 2 m shrub, 1-4 m boulder, 10-30 m outcrop).
5. **Forest grain.** Vastness V2 item 6 (contingent) and this plan's far canopy tint are the same work. This plan owns it (T6); the vastness plan drops the item.
6. **The 5-150 km band.** Lakes, river lines and later buttes put structure into the band V0 expects to find empty. Run the V0 audit on a rivers-generation world before deciding on chain lines of masts (V7b).
7. **Aerial perspective (V1).** V1 may raise terrain albedo. T4's rock and colour variation and every new material must read the same terrain albedo scale. Whichever of V1 and T4 freezes second is signed off on top of the first.
8. **Valley mist.** V7a notes that mist pooling needs a "basin floor" field. The drainage graph holds exactly that. River-valley mist becomes a small follow-up once T2 and V4 exist.
9. **Builders' datum.** Wall courses at 1 km, datum masts in 100 m bands, plateau benches at 160 m and strata at 40 m are one visual language.
10. **Budget.** The vastness plan claims 1.0 ms on the A1000 and this plan 1.5 ms, on a GPU that is at 40-44 ms at Medium. Both sets of gates are per-pass and independent, and scatter and ground-cover density scale down on Low and Medium.

## 11. Phases

Each phase is independently testable and committable. "Lead" is `claude-lead`; "impl" is `implementer`. Per work order use `tools/run_tests.ps1 -Since HEAD` or `-Files`; the primary agent runs the gate suite and the two ledger tests at each commit point. Captures go under `.godot/captures/landscape/<phase>/`; delete raw frames after making sheets and run `tools/prune_test_output.ps1 -Apply`. Every new test gets a `tools/test_manifest.json` entry.

Every phase from T3 on ends the same way, called **the freeze** below: the user signs off in a review world; the phase's features become the next generation in the ledger; the fingerprint row and two or three look goldens are captured; `tools/new_generation_world.ps1` creates the step's test world; the launcher default moves. Until then, fixes are free.

Order of work: T0, then T1. After T1, the water track (T2, T3) and the dressing track (T4, T5, T6) can be developed in parallel as open features. River-dependent work orders in T4-T6 land in whichever step freezes after rivers. T7 follows the rivers step. T8 follows the T6 review.

### T0. Landscape audit and baseline (no production change)

- **Goal.** Numbers and pictures that say how flat the land is, what a drainage bake would produce, and what the current systems cost.
- **Deliverables.** `tools/landscape_audit.gd` plus a C# helper (manifest tags `landscape`; a headless part and a windowed GPU part, full tier only).
  1. *Relief spectrum.* On the v8 recipe, seed 1337: height variance per wavelength band and slope percentiles, per class (coastal belt, plains, rolling, hill country, plateau). Sets T7's targets.
  2. *Drainage prototype.* Offline, test-only: sample a 2,000 km strip at 1 km and at 500 m, run breach-and-fill and flow accumulation, and write a map image and statistics: depression count and depth, lake area share at three breach limits, channel length per km^2 at three thresholds, and the distributions of discharge, gradient, depth, speed and danger class. The user sees a river map coloured by danger before anything is built.
  3. *Inland water and swim audit.* Read-only review plus unit tests on `RingTerrainProxySector` with fabricated Lake and River samples: which paths still work in a gen-17 world. Output: a gap list for T2 and T3.
  4. *Tree pack audit in engine.* Confirms 7.1 against the prepared catalog: authored heights per variant and class, which maps bind at each LOD, the visible LOD0 to LOD2 switch.
  5. *Generation 1 fingerprint.* The first row of the fingerprint golden, and a few look captures of a v8 world per machine as the `compare` reference, taken after the Cards default change lands.
  6. *Look and cost baseline.* Poses: plains, hill country, coast, forest interior, edge and clearing at eye height; 300 m, 2 km and 18 km; noon and 8 degrees of sun. Per-pass GPU markers for terrain, trees and grass on the A1000; tree tile build time; draw counts.
  7. *Doc fixes* from 7.3 and section 1.1 (docs only).
- **Shared with vastness V0.** Poses, time search and the band table.
- **Who.** Lead writes the spec (half a day); impl builds it in four work orders. **Depends on.** Nothing.

### T1. Recipe v9, the generation ledger and test-world tooling (plumbing, no visible change)

- **Goal.** The version mechanism exists for both plans, and a v9 world with no features is identical to v8.
- **Design (lead).** The recipe field and review list, the ledger and feature mask, load ordering (the generation must be published before any settings owner or renderer reads it), the launcher selector, slot naming, and how tests pin a generation.
- **Work orders.**
  1. `ringworld_surface_world_v9` in C# and GDScript: the id, field set, `world_generation`, `review_features`, and climate base fields with defaults equal to today's settings values. The model treats v9 as v8. The GPU host accepts v9.
  2. The ledger and its owner: the C# static and the `ringworld_world_features` shader global, published at world load. It reaches the tile builders, the grass builder and the proxy and far builders. Nothing reads a feature yet.
  3. `world_generation_fingerprint_test` with the generation 1 row, and `world_generation_contract_test`.
  4. `tools/new_generation_world.ps1` and its helper: create, name and label a world at a given generation or with review features; delete review worlds.
  5. Launcher: show generation and label per slot; create at the latest frozen generation; the advanced generation selector.
  6. `EmitterSiteTestWorlds` takes a generation; a short `docs/world-generations.md` with the ledger table and the freeze checklist.
- **Verification.** A v9 world with generation 1 features equals the v8 world of the same seed at every sampled point; `terrain_recipe_freeze_test`; the existing gate suite with no golden changes.
- **Depends on.** T0. Nothing is frozen by T1.

### T2. Rivers and lakes (large engineering)

- **Design (lead, with `claude-deep` if conditioning or lake containment proves hard).** Grid size and thresholds from T0, depression conditioning, the hydraulics, the cross-section, pools and chutes, the float water level and cache format, ordering with site operators, the hash, CPU routing of river sectors.
- **Work orders.** (1) Bake and graph with `drainage_graph_test`. (2) Cache and loading-screen step. (3) Carve operator, footprint argument and invariants test. (4) Lakes and rim raise. (5) Float water level; water kind in the sampler; the controller reads it. (6) Inland proxy water mesh and shader profiles; still water. (7) Far water fraction and glint; minimap. (8) Recipe controls and preview overlay. (9) River-sector routing and the travel benchmark.
- **Verification.** Section 3, the shape and rendering tests. **Depends on.** T1.
- **Gate G1.** Bake within 20 s and zero coverage waits in the travel benchmark over river country, or the GLSL carve moves from T9 into T2. T2 does not freeze by itself; it stays open until T3.

### T3. Current, river danger and the floating system (medium-large engineering)

- **Design (lead).** The flow rules and constants, danger classes and their targets, the swim and wade coupling, the rigid-body and floating-origin check and the fallback integrator, the per-vertex flow attribute and shader advection, foam and audio.
- **Work orders.** (1) Reach hydraulics in the bake; `RingWaterFlow`; `river_flow_field_test`. (2) Danger classes, the downstream-exit check, `river_danger_test`. (3) Swim and wade push; `river_current_swim_test`. (4) The origin-shift check; `RingBuoyancy`, `RingFloatingBody`, stranding. (5) The floating manager and catalog, the test body, `floating_item_validation_test`, `docs/floating-items.md`. (6) Per-vertex flow, shader advection and foam; the parity test. (7) Far whitewater; ambience by speed; the current strength setting. (8) Chute rocks hook for T5. (9) The freeze: step "rivers".
- **Verification.** Sections 3.2-3.7. **Depends on.** T2.
- **Gate G2.** The user swims a calm river, a fierce one and a torrent in a review world and signs off. Then the freeze. After it, the drainage algorithm, the hydraulics and the danger mix are fixed for that generation; current strength stays a live setting.

### T4. Ground surface (shader work)

- **Work orders.** (1) Rock, strata and scree in the shared albedo include and both near shaders, behind the feature. (2) Colour variation and the near detail normal. (3) Far shaders and menu rows. (4) Bed and bank materials (needs T2). (5) The freeze: step "ground".
- **Verification.** Section 5. **Depends on.** T1; work order 4 on T2. Lead reviews the strata filter for aliasing first.

### T5. Scatter foundation and rocks (medium engineering)

- **Design (lead).** The class table, the field grid, how classes share the GPU LOD budget, the collider pool, and how the older tile build stays untouched.
- **Work orders.** (1) Habitat field grid. (2) Class table behind the feature. (3) Rock generator tool and prepared resource. (4) Rock shader on the shared includes; GPU registration. (5) Colliders. (6) Far speckle, menu, presets. (7) Wetness from the graph, river cobbles, bars and chute boulders (needs T2). (8) The freeze: step "rocks".
- **Verification.** Section 5. **Depends on.** T4; work order 7 on T2.

### T6. Vegetation from the packs we have (medium engineering)

- **Work orders.** (1) Size-class metadata in the catalog; stand age and habitat species in the sampler. (2) Understory class from the sapling meshes. (3) Preparation tool: snag, fallen-tree and stump variants appended to the catalog and impostor; `tree_catalog_append_test`. (4) Shrub class from the leaf-cluster atlases. (5) Tint. (6) Card atlas variants (CC0 import) and community selection in the grass builder; grass reads wetness and canopy. (7) Riparian and wet-meadow communities, reeds, drift logs (needs T2). (8) Canopy in the forest weight, far tint, GLSL climate port. (9) The freeze: step "vegetation". Licence records are updated with each import and derivation.
- **Verification.** Section 6. **Depends on.** T5; work order 7 on T2.
- **Gate G3.** The user reviews the forest and decides what T8 should add.

### T7. Lowland meso-relief (medium-large engineering)

- **Design (lead).** The terms and constants, the band-limit rule on CPU and GLSL, the menu section.
- **Work orders.** (1) Recipe fields. (2) Meso-relief terms and calibration against T0's targets. (3) Coastal variety. (4) Band-limited noise sampling and callers. (5) GLSL port and parity. (6) Ponds as still lake-kind water; dune and strand community hooks. (7) Menu and preview view mode. (8) The freeze: step "landforms".
- **Verification.** Section 4. **Depends on.** The rivers step.

### T8. New tree species and further assets

- **Work.** Choose packs against 7.2's rules; import through `prepare_tree_assets`; add community rows and impostor layers; update the licence records; the freeze: step "species". **Depends on.** G3.

### T9. Contingent items (each needs its own go, and a step if visible)

- **T9a Small streams.** A tile-local network for catchments below the trunk threshold, with current. Large.
- **T9b GLSL river carve** for GPU proxy sectors. Medium; no step if parity holds.
- **T9c Deltas, waterfall meshes and spray.** Medium.
- **T9d Heightmap stamps** for badlands. Medium, with an asset pipeline.
- **T9e Sinkholes into caves.** Needs gen-17 to allow openings. Large; its own design.
- **T9f GPU placement of ground cover.** Only if tile builds limit travel.
- **T9g Headworks and spill fans.** River-source POIs and wall-foot fans at Wall Engine stations.
- **T9h Valley mist** from the drainage field, with vastness V4 or V7a.
- **T9i Floating items.** Driftwood, crates, a boat: each follows the recipe in 3.5.
- **T9j Biome Workshop as the authoring front end** for the community table.

## 12. Budget table (A1000, 1920x1080, Medium, P0 / 35 W)

| Phase | GPU gate | Other gates | Measured with |
|---|---|---|---|
| T1 | 0 ms | a v9 world equals v8; existing goldens unchanged | the ledger tests, the gate suite |
| T2 | 0.3 ms (shared with T3) | bake at most 20 s; graph 16 MB; +0.2 us near rivers | `drainage_bake_benchmark`, A-B-A at a river pose |
| T3 | inside T2's 0.3 ms | flow query under 0.3 us; floating bodies 0.1 ms at the cap; 0.05 ms main thread | per-pass marker, CPU telemetry |
| T4 | 0.4 ms | 0 new draws | `terrain_render_attribution_test`, A-B-A at a hill pose |
| T5 | 0.5 ms | 24 draws; tile build at most 1.3x; 8 MB meshes | per-pass marker, "Trees build" telemetry |
| T6 | 0.3 ms | 16 draws; ground cover budget-neutral; 8 MB appended variants | A-B-A at a forest-edge pose |
| T7 | 0 ms | height sample at most 1.25x v7; lodgap p99 under 8 m | `terrain_generation_cpu_benchmark`, travel benchmark |
| T8 | 0 ms beyond T6 | 8 draws; 24 MB VRAM | A-B-A at a mixed-forest pose |
| **Total** | **1.5 ms** | **48 draws; 0.2 ms main thread** | |

Main-viewport run-to-run noise is 3-6 ms, so every gate is judged on per-pass markers and A-B-A medians. The 4070 numbers are indicative only. Goldens are machine-specific.

## 13. Decisions and open questions

### 13.1 Decided by the user (2026-11-09)

| # | Question | Answer | Effect on the plan |
|---|---|---|---|
| 1 | May existing worlds change? | "new worlds only" | Every change is a ledger feature (2.2) |
| 2 | Rivers as the centrepiece? Current? | "yes and lets do water that pushes" | Rivers first; the flow field and push (3.2, 3.4) |
| 3 | Third-party assets? | "yes and better utilaze the tree pack we have already" | Pack audit (7.1) and T6 before any new species (T8); licence rules (7.2, 7.3) |
| 7 | Cards as the default grass style? | "grass cards is the new default" | Flowers, ferns and reeds are card variants (T6). The default change is global: the user's explicit exception to new worlds only |
| 10 | What happens to a new-generation world when later phases land? | "make new worlds for every major change that would require a new world" | Each visible phase is its own frozen generation step with a fresh test world; earlier worlds stay as they were (2.2) |
| 11 | How dangerous may the current be? | "there can be a variance, of safe rivers and dangerous rivers" | Danger classes derived from discharge, gradient and confinement, readable from the land; one current strength scale (3.3) |
| 12 | Which object floats first? | "just put in the system for floating first we will be able to easily add floating items after" | A generic floating system and a test-only body; a recipe for adding items (3.5) |
| 13 | Does new worlds only cover the vastness plan? | "new worlds only is a universal thing at the moment, all of these worlds are for testing"; then "yes" | Yes. One shared ladder; `docs/vastness-plan.md` section 11. Old-world checks are kept light |
| 4 | Which lowland characters first? | "i like the rest of your recommendations" | Hummocks with ponds, buttes near plateau edges, coastal cliffs with dunes (T7). Tors, dry pits and badlands follow or stay contingent |
| 5 | How visible is the builders' hand? | same | Natural at walking scale, engineered at map scale: bench levels and rock strata at fixed heights ring-wide, rivers that start at outfalls |
| 6 | Which new tree forms? | same | After the packs are used fully: a willow or alder form that marks water, a pale-trunked birch or aspen form, a broad solitary crown; more fir variants as a fourth candidate (T8) |
| 8 | Ground walk or high flight when the budget forces a choice? | same | Favour the ground-level walk for near scatter; keep the far patterns (water glint, whitewater, canopy tint) because they are nearly free |
| 9 | Order against the vastness plan? | same | Build the audit harness once for both plans, then run this plan's T1-T3 before vastness V2 onward |

### 13.2 Points raised by revision 3 (decided 2026-11-09: the user accepted each recommendation)

The user answered these on 2026-11-09 by accepting the recommendations below; the plan follows them.

14. **(2.2)** How many old generations should stay loadable? Each one keeps its code branches, fixtures and goldens alive. Recommended: keep generation 1 and the last three frozen generations; when a generation falls below that floor, delete its branches, its tests and its test world. The alternative is to keep every generation until both plans close.
15. **(T3)** Dangerous water displaces and never harms: no damage, no drowning, no death. The plan argues for this in 3.3. Say if a harsher rule is wanted.
16. **(2.2)** Step size: one generation per signed-off phase, about twelve across both plans, with two phases sharing a step when they are accepted together. Say if steps should be coarser (for example one per plan area) or finer.

## 14. Risks

- **Rivers are the hard part.** Depression conditioning, lake containment and sub-grid routing are where procedural rivers usually fail (water walls, perched reaches, rivers cutting ridges). Mitigations: T0 prototypes the bake, the invariants are tests written before the operator, and G1 is a hard gate.
- **The danger mix.** Speed from gradient depends on what gradients the v7 terrain actually gives trunk rivers. If nearly everything is calm or nearly everything fierce, the roughness constant and caps will not fix it alone. T0 reports the class distribution before design, and river ferocity is a recipe field.
- **Dangerous water that frustrates.** Being swept a long way can feel like punishment. The downstream-exit rule, the readable cues and the absence of damage are the mitigations; G2 is a play test, not a capture review.
- **Rigid bodies on the ring.** No precedent exists, and floating-origin shifts may break them. T3 checks this first and has a fallback integrator behind the same component.
- **Branch and golden growth.** About twelve generations means twelve sets of branches, fingerprints and look goldens. The mitigations are the feature-mask design (one test per feature, no number comparisons), small per-step goldens, and the retirement floor in question 14.
- **A miss in an old generation.** The checks are deliberately light because all worlds are test worlds. A subtle shader difference in an old world could slip through; the per-phase `compare` heatmap is the backstop.
- **Open features colliding.** Two phases developed in parallel as open features can interact before either is frozen. Review worlds list their features explicitly, and each freeze is tested with exactly the ledger's set.
- **Global settings.** Grave-key settings and presets are global. A step that needs different defaults uses a separate settings file from that generation on (2.2). The Cards default is the one agreed exception.
- **GPU offload regression.** River and lake sectors return to the CPU path. G1 measures it.
- **A third copy of the height function.** The landforms step touches C#, GDScript and GLSL. Keep its terms few and port them in the same phase.
- **A1000 headroom.** There is none. Every phase is gated and densities scale by preset. If a gate is missed the phase is reported back, not tuned by adding a toggle.
- **Asset quality and licences.** Shrubs made from leaf atlases and card atlases composed from CC0 sets can look cheap next to the authored trees; T6 shows captures before wiring them into every community. Each import is recorded when it is added.
- **Stale docs.** Section 1.1 lists twelve differences from older docs. T0 fixes the ones named in 7.3; the rest should be corrected by the primary agent.
