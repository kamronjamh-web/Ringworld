# Cave and Deep-Structure Implementation Checklist

Target: preserve the existing ringworld experience while adding an 8,192 m
procedural terrain layer, a dark structural cavity beneath it, volumetric cave
LOD, and deterministic cave feature families.

Items are checked only after the corresponding implementation and automated
validation pass. Human visual checks remain unchecked until the user tests them.

## 0. Preservation and recovery gate

- [x] Inventory the OneDrive conflict copies without modifying them.
- [x] Audit every conflict pair for canonical-only and conflict-only behavior.
- [x] Record an exact keep/merge decision for every conflict pair.
- [x] Preserve recoverable copies before changing canonical files.
- [x] Merge all compatible behavior into canonical files.
- [x] Preserve canonical Godot UIDs unless a reference audit proves otherwise.
- [x] Build and run the existing terrain, construction, editing, save-journal, and LOD smoke tests.
- [x] Move conflict copies out of active source paths under non-compiling archive extensions.
- [ ] Remove archived conflict copies only after the user accepts the merged project.
- [ ] Confirm with the user that current gameplay still behaves as expected.

### OneDrive merge decisions

The audit found 19 conflict files: 10 source/scene copies and 9 matching UID
copies. Every conflict file has a canonical counterpart.

| Canonical path | Decision |
|---|---|
| `scenes/VoxelRingIntegrationTest.tscn` | Manually merge the newer smooth-backend flags; preserve the canonical scene UID. |
| `scripts/start_menu.gd` | Merge the conflict copy's deep-shaft launch routing; preserve the canonical UID. |
| `scripts/world_size_launcher.gd` | Merge deep-shaft routing and the intentional Mega floating-origin enablement; preserve the canonical UID. |
| `scripts/Terrain/Editable/SmoothConstructionMesher.cs` | Keep the conflict winding correction. |
| `scripts/Terrain/Editable/SmoothConstructionOverlaySmoke.cs` | Keep the conflict winding regression coverage. |
| `scripts/Terrain/Editable/SmoothTerrainContracts.cs` | Keep the finite structural-bottom SDF and boundary-aware revision. |
| `scripts/Terrain/Editable/SmoothTerrainGrid.cs` | Keep the hot-patch, atomic edit, deep-residency, lower-boundary, and raycast fixes as one coordinated unit. |
| `scripts/Terrain/Editable/SmoothTerrainMesher.cs` | Keep the bounded parent-local meshing overload used by 16 m patches. |
| `scripts/Terrain/Editable/SmoothTerrainRuntimeSmoke.cs` | Keep the expanded structural, editing, hot-patch, and depth regression coverage. |
| `scripts/Voxel/Integration/RingVoxelExplorerController.cs` | Keep 3D residency gating, vertical focus, and the deep-shaft physical regression. |

All canonical `.uid` files remain authoritative. Conflict UIDs are not merged.
The conflict `.cs` files currently participate in the SDK compile glob and make
the project fail with duplicate type definitions, so canonical merging and
duplicate isolation must be completed atomically before runtime validation.

### Gate 0 - Stable baseline

- [x] One active canonical copy of every scene, script, and UID remains.
- [x] Deep traversal, terrain editing, construction, save-journal, and existing LOD behavior pass automated validation.
- [x] No current feature was intentionally removed by the conflict repair.

## 1. Vertical-domain contract

- [x] Set the intended procedural terrain bottom to canonical Y = -8,192 m.
- [x] Keep the ring's total structural depth at approximately 22.3 km.
- [x] Reserve Y = -7,680 m through -8,192 m as a 512 m solid roof slab with no random caves.
- [x] Reserve the volume below Y = -8,192 m as a future structural cavity.
- [x] Reserve a lower hull slab above the structural underside.
- [x] Add one versioned configuration owner for terrain bottom, cave bottom, cavity bottom, and hull thickness.
- [x] Remove conflicting hard-coded -2,000 m terrain-floor assumptions from runtime configuration.
- [x] Define whether player excavation may penetrate the roof slab and through which tools/rules.
- [x] Define explicit behavior at ring sidewalls and the structural underside.
- [x] Store the vertical-domain version, roof thickness, hull thickness, and terrain bottom in world metadata.
- [x] Include the configuration and generator version in authoritative smooth snapshot/journal identity.
- [x] Include the vertical-domain identity in future volumetric cave LOD cache keys.

### Structural boundary contract

- The habitable across interval is `[0, ring width)`. A finite 32 m bedrock
  sidewall occupies `[-32, 0)` at the left edge and `[ring width, ring width +
  32)` at the right edge.
- Sidewalls extend from the structural bottom through the configured structural
  wall top. Space beyond either wall slab is exterior air.
- The lower hull is a finite solid slab from the structural bottom through the
  lower-hull top. Space below the structural bottom is exterior air.
- The persistent proxy owns long-range visual coverage; the smooth signed-density
  field owns local wall geometry and collision. Smooth sidewalls are inset by
  half a voxel from the proxy planes to avoid coplanar overlap.
- Structural-shell lighting uses `[0, ring width)` across and
  `[structural bottom, terrain bottom)` vertically. Exterior space beyond
  either sidewall or below the lower hull receives no underground or
  ring-atmosphere influence; any atmosphere feather is contained entirely on
  the interior side of each wall.
- Terrain edits that intersect a sidewall or cross the structural underside are
  rejected. Older journal identities migrate only when every saved edit remains
  valid under the current protected-boundary contract.

### Gate 1 - Unambiguous depth ownership

- [x] Every canonical Y belongs to exactly one of terrain, roof slab, cavity, lower hull, or exterior.
- [x] Existing saves either retain their original generator contract or migrate explicitly.
- [x] Terrain generation and editing cannot silently cross structural safety boundaries.

## 2. Opaque roof and cavity shell

- [x] Compose a real solid-to-air density transition at Y = -8,192 m.
- [x] Generate inward-facing, opaque underside geometry for the terrain roof.
- [x] Keep roof geometry shadow-casting at every applicable LOD.
- [x] Generate the lower cavity hull in the authoritative smooth density field.
- [x] Integrate cavity side boundaries with the smooth backend.
- [x] Keep one sampling/meshing halo beyond each boundary so it cannot become an open chunk edge.
- [x] Add density, winding, normal, and collision-topology tests for the roof and lower hull.
- [x] Add visual opacity, shadow, and cross-chunk seam tests for the roof and lower hull.
- [x] Add fall tests at intact roof, excavated breach, cavity floor, and side boundaries.
- [ ] Human-review the new sidewall and structural-underside contract in-game:
  inner and exterior visibility, smooth/proxy alignment, collision, and absence
  of gaps at both across edges and below the hull.

Automated closure coverage verifies opaque depth rendering, back-face culling,
shadow-casting state, and exact shared seam-edge topology for both around and
across chunk boundaries. The headless runner has no rendering device, so final
pixel and shadow-map appearance remains part of the human-review item above.

### Gate 2 - Closed structural cavity

- [ ] No transparent roof, open chunk edge, inverted surface, or collision hole is visible.
- [x] The player cannot fall into exterior space through an unloaded or unmeshed boundary.

The runtime suite now ray-marches all four structural fall cases. Movement
residency also fails closed below the hull and beyond either sidewall instead of
clamping an exterior probe back into a loaded chunk.

## 3. Underground lighting and atmosphere

- [x] Add a canonical-depth underground environment controller.
- [x] Fade direct ring sunlight to zero below the intact cavity roof.
- [x] Reduce sky ambient contribution to a near-black underground baseline.
- [x] Clamp automatic exposure so darkness is not raised to gray.
- [x] Add a short transition band for deliberate roof openings and access shafts.
- [x] Constrain structural-shell lighting and atmosphere to the canonical ring
  width and structural bottom, with no spatial or frame-blended spill beyond
  either sidewall or below the lower hull.
- [x] Keep local lights, emissive materials, and future machinery lighting functional.
- [x] Add cavity darkness and light-leak runtime captures.

The GPU-only `--capture-cavity-lighting` route uses the Certified world and
writes an intact-cavity baseline, a localized-light comparison, and measured
luminance results to `user://generated/captures/cavity_lighting_capture`. This
keeps disposable evidence outside the OneDrive checkout. It rejects headless or
all-black dummy frames rather than treating them as darkness evidence. The
retained Forward+ reference passed with baseline mean luminance `0.0117`, edge
luminance `0.0080`, and a localized-light center mean of `0.2511`. These metrics
are automated regression guards; the visual Gate 3 judgments below remain for
human review.

### Gate 3 - Dark cavity

- [ ] An unlit cavity is almost completely dark.
- [ ] Sunlight and sky ambient do not leak through intact terrain or coarse LODs.
- [ ] Local light sources remain readable and do not illuminate unloaded geometry.

## 4. Volumetric cave LOD

- [x] Treat the 2x2x2 16 m patch bubble as edit/rebuild topology, not cave visibility coverage.
- [x] Separate editing subdivision, near collision coverage, and visual LOD ownership.
- [x] Extend the authoritative signed-density field to every volumetric cave LOD.
- [x] Add near full-resolution volumetric meshes with collision.
- [x] Add medium render-only volumetric meshes.
- [x] Add coarse kilometre-scale meshes for megacavern and supermassive-cavern walls.
- [x] Add min/max density rejection for fully solid and fully empty regions.
- [x] Stitch or transition adjacent cave LOD resolutions without cracks.
- [x] Ensure coarse roof geometry remains resident when fine chunks unload.
- [x] Add visibility, cancellation, upload-budget, and memory-pressure telemetry.

`SmoothTerrainGrid` now computes independent visual, near-collision, and 16 m
edit-subdivision ownership sets, then unions them only for build residency.
Collision-only chunks remain physically installed but hidden from visual/proxy
handoff, while edit-only retention cannot satisfy movement-collision readiness.
`SMOOTH_TERRAIN_COVERAGE_OWNERSHIP_SMOKE_PASS` and the integrated deep-shaft
smoke guard the ownership split and actual-position cave collision coverage.

All future volumetric cave tiers now capture the same immutable smooth baseline
plus ordered edit snapshot through a versioned density-field identity. Validated,
bounded power-of-two lattice keys include the generator, vertical-domain and
sidewall topology revision, edit revision, ring dimensions, region, and spacing.
Aligned 1/2/4/8 m samples, coarse 16-1,024 m samples, negative Y, ring wrapping,
structural bounds, snapshot invalidation, and foreign-key rejection are covered
by deterministic diagnostics and `SMOOTH_TERRAIN_VOLUMETRIC_SDF_LOD_SMOKE_PASS`.

The production 1 m, 32-cell regular-grid mesher now owns a separately configured
focus-centred near-volumetric visual set around the player's actual 3D position.
Its visibility is independent from surface-proxy and 2x2x2 edit ownership, while
the existing collision owner installs matching near physics in the live scene.
Ordinary builds consume the authoritative volumetric snapshot through the
optimized production capture path. `SMOOTH_TERRAIN_NEAR_VOLUMETRIC_SMOKE_PASS`
guards deep roof geometry/collision, negative-Y and vertical-only recentering,
surface/edit ownership isolation, structural-bottom clamping, and space unload.

The medium tier now streams independent 64 m sectors made from 32 regular cells
sampled every 2 m, including a two-cell authoritative-density halo. It is
strictly render-only: medium presenters never build collision and remain outside
surface, near-physics, and edit-subdivision ownership. Medium work queues behind
near collision, shares the per-frame install limit, rebuilds on density identity
changes, follows negative-Y/vertical-only focus movement, retains structural
boundary owner sectors, and unloads in space. Readiness-based handoff keeps an
installed medium sector visible until all eight 1 m child parents are visibly
ready, then restores it immediately when near ownership leaves. The optimized
column capture and `SMOOTH_TERRAIN_MEDIUM_VOLUMETRIC_SMOKE_PASS` guard sampling,
deep geometry, no-collision ownership, edit replacement, handoff, and lifetime.

Authoritative volumetric lattices now classify the cell-owning sample range as
empty, solid, or mixed before mesh input is allocated. Medium and coarse sectors
whose central region cannot contain an isosurface skip snapshot conversion and
the regular-cell mesher, while halo-only sign changes remain irrelevant to the
decision. Deterministic diagnostics cover empty, solid, mixed, negative-Y,
halo-only, and edit-reclassification cases; runtime smoke also verifies rejected
sectors remain render- and collision-free.

The coarse tier streams independent 2,048 m sectors made from 32 regular cells
sampled every 64 m, with the same two-sample authoritative-density halo. Its live
1/1/1 focus envelope covers roughly 6.1 km horizontally and 8.2 km vertically,
including an extra downward structural-boundary owner. Coarse presentation is
render-only, activates 256 m below the local surface to avoid duplicating surface
LOD, preserves near-collision priority and leads the stitched render hierarchy,
rebuilds on density identity changes, follows vertical focus and floating-origin changes,
retains roof, lower-hull, sidewall, and ring-wrap owners, and unloads in space.
`SMOOTH_TERRAIN_COARSE_VOLUMETRIC_SMOKE_PASS` guards the 64/32/2,048 contract,
uniform rejection, ownership isolation, edit invalidation, boundary visibility,
space suspension, and shutdown lifetime.

The cave renderer now uses a staged 1/2/4/8/16/32/64 m clipmap instead of
attempting a single 2-to-64 m handoff. A shared authoritative-density adapter
feeds the native 2:1 Transvoxel cells at every exposed tier face, preserves
material ids, rejects uniform transition lattices, and keeps every transition
presenter render-only. Aligned 2x2x2 readiness groups install the hierarchy
coarse-to-fine and retain the parent until its complete child group is ready;
locally unwrapped around-ring origins avoid a partial overlapping seam sector.
`SMOOTH_TERRAIN_TRANSITION_CELL_SMOKE_PASS` covers all face orientations,
negative Y, alignment rejection, batching, and uniform rejection, while
`SMOOTH_TERRAIN_CAVE_LOD_STITCH_SMOKE_PASS` exercises the complete live chain,
nonzero bridge transition geometry, collision isolation, ownership handoff, and
space unload. The visual judgments in Gate 4 remain explicitly human-reviewed.

Production cave telemetry now exposes desired, resident, ready, visible, queued,
pooled, and retained-memory counts for every 1/2/4/8/16/32/64 m tier. It also
tracks aggregate active/deferred work, base and auxiliary cancellations,
main-thread install time/budget overruns, queued-result bytes, and current/peak
managed cave memory in certification reports. Runtime smoke holds and drains a
completed result, verifies cancellation accounting, and validates the settled
stitched hierarchy through `SMOOTH_TERRAIN_CAVE_LOD_TELEMETRY_PASS`.

### Gate 4 - Large-volume visibility

- [ ] A 6 km cavern can show its important walls without loading fine collision across 6 km.
- [ ] LOD transitions expose no persistent holes, duplicate surfaces, or proxy terrain.
- [x] Editing rebuilds remain limited to the local patch bubble and affected coarse snapshots.

Same-frame brush stamps are coalesced into one final-revision transaction. Near
parents retain collision until replacement, while the 2 m through 64 m cave
tiers rebuild only when their capture volume overlaps a new stamp. Checked
revision watermarks let unaffected resident sectors release edit history.

## 5. Deterministic cave topology

- [x] Add versioned cave-generation settings derived from the shared terrain/world seed.
- [x] Partition underground space into deterministic macro provinces and cells.
- [x] Keep most cells solid; cave systems occur in bounded provinces.
- [x] Generate chamber anchors independently of chunk load order.
- [x] Connect chambers with a sparse deterministic graph containing branches and limited loops.
- [x] Make neighboring-cell connection decisions symmetric across boundaries.
- [x] Capture a bounded immutable cave-feature list per meshed region.
- [x] Apply wall noise after topology so noise cannot redefine connectivity into spaghetti.
- [x] Compose procedural caves before ordered player-edit stamps.
- [x] Add determinism, ring-wrap, negative-Y, load-order, and chunk-seam tests.

Designed caves now have a validated version-1 settings resource and a stable
platform-independent coordinate/salt key that supports signed 64-bit ring and
depth coordinates. The terrain/world seed is the sole seed exposed by the
launcher and stored in world-slot metadata; caves use that same numeric seed
inside a cave-specific namespace while retaining an independently versioned
generator contract. `RingTerrainSampler` captures the contract immutably, including
across independent copies, while deliberately excluding it from the current
terrain definition hash and legacy density sampler so existing terrain, edits,
and saves do not change before designed topology is composed. Runtime smoke
guards determinism, invalid settings, negative/large coordinates, shared-seed
identity, density compatibility, and copy stability through
`SMOOTH_TERRAIN_SHARED_SEED_CONTRACT_PASS`.

The version-1 cave topology now partitions canonical space into fixed 512 m x
256 m x 512 m cells grouped 32 x 32 x 32 into 16,384 m x 8,192 m x 16,384 m
macro provinces. Every cell and province has a seed-, topology-, ring-size-, and
coordinate-derived stable key, canonical origin and extent, with correct
negative-coordinate floor division and local ownership indices. Around-ring
lookup wraps canonically, including a uniquely owned partial final cell and
province when circumference is not evenly divisible. Sampler copies preserve
the captured partition, and resolution remains stateless and independent of
request order. `SMOOTH_TERRAIN_CAVE_PARTITION_PASS` guards exact boundaries,
large signed coordinates, ring seams, normalized neighbor lookup, invalid
contracts, seed isolation, repeatability, and reversed load order without
changing current density or terrain identity.

Designed-cave eligibility is now sparse and coherent rather than an independent
random decision per cell. Version 1 activates approximately one in eight macro
provinces, then assigns each active province one deterministic candidate
envelope inset by at least two cells from its faces. Horizontal spans are 8-24
cells and vertical spans are 4-16 cells; everything outside the envelope and
every inactive province remains solid by default. Capable partial seam
provinces use their actual around-ring capacity, while undersized remainders
stay solid. The distribution identity includes every fixed probability, inset,
and span constant, rejects cells from foreign world partitions, and remains
excluded from current terrain density and hashes. The runtime distribution
audit activated 242 of 1,920 sampled provinces (12.6%) while permitting only
626,563 of 62,914,560 cells (about 1.0%), with
`SMOOTH_TERRAIN_CAVE_PROVINCE_MASK_PASS` guarding bounds, seam handling,
inactive solidity, seed/ring isolation, and load-order independence.

Active province envelopes now produce bounded immutable chamber-anchor sets
without consulting chunk state. Version 1 evaluates exactly 64 integer-only
candidate slots arranged as a 4 x 4 x 4 stratification, jitters each candidate
inside the central half of its stratum, and selects 4-24 anchors by stable
priority according to envelope volume. Every anchor carries a stable key,
priority, rank, slot, canonical position, province address, and owning cell;
production generation verifies that the owning cell is eligible. Anchor-set
identity includes every fixed count, stratification, density, and jitter
constant, while foreign or forged province plans are rejected. Diagnostics
cover unique keys/slots/positions, negative-depth ownership, capable partial
seam provinces, ring normalization, repeated generators, seed/ring isolation,
reverse request order, immutable-set identity, and sampler-copy equivalence
through `SMOOTH_TERRAIN_CAVE_ANCHOR_PASS`, without yet assigning cave families,
connections, shapes, or density carving.

Each active province's chamber anchors now form a bounded deterministic graph.
Version 1 evaluates at most 276 unordered candidate edges, builds an
integer-distance minimum spanning tree with Kruskal, then adds 1-3 of the
shortest stable non-tree edges according to anchor count. The tree guarantees
that every chamber is reachable; loop selection guarantees an actual
degree-three branch even when the MST begins as a path, while keeping total
edge count at no more than nodes plus two. Edge and graph identities use ordered
endpoint hashing and include lengths, kinds, upstream anchor identity, and all
fixed strategy constants. `SMOOTH_TERRAIN_CAVE_GRAPH_PASS` independently checks
minimum spanning weight, tree acyclicity, full connectivity, exact cyclomatic
loop count, branch degree, edge uniqueness and lengths, negative-depth and
partial-seam plans, ring normalization, foreign-plan rejection, reversed
request order, and sampler-copy equivalence. Connections remain planning data;
no tunnel SDF or terrain density is changed yet.

Sparse graph edges now resolve into immutable cell-boundary route maps rather
than independent random face openings. Each route interleaves its required
around, vertical, and across steps by integer rational progress, producing a
balanced monotonic path whose length exactly equals cell Manhattan distance.
Every crossed boundary has one canonical positive-face owner, ordered owner and
neighbor cell identities, and a stable key; querying from either neighboring
cell and the opposite direction returns the same face, including the global
around-ring seam. Overlapping graph routes merge into one open connection with
separate tree and loop provenance counts, while unreferenced eligible faces
remain symmetrically closed. Map identity includes every route sequence and
merged connection. `SMOOTH_TERRAIN_CAVE_CELL_BOUNDARY_PASS` checks all axes and
directions, open and closed decisions, route balance/adjacency/endpoints,
eligible-envelope containment, merged provenance, negative depth, capable
partial seams, ring normalization, foreign-plan rejection, reverse request
order, and sampler-copy equivalence. These are still topology decisions only;
they do not carve tunnel geometry yet.

Meshed regions can now capture a bounded immutable snapshot of only the cave
topology that can influence their volume. A canonical half-open region request
supplies an explicit halo; the stateless capture enumerates at most 64 spatially
overlapping provinces and returns at most 4,096 unique chamber-anchor points and
open-face corridor segments, failing instead of silently truncating either cap.
Candidate enumeration handles negative depth, the around-ring seam, complete
ring coverage, and the partial final province without duplicate ownership.
Feature keys use separate chamber and corridor domains, corridor descriptors
retain tree/loop provenance and canonical cell-center endpoints, and snapshot
identity includes the complete request, every candidate plan and active map,
and every returned descriptor. `SMOOTH_TERRAIN_CAVE_REGION_FEATURE_PASS`
guards exact and halo filtering, inactive empty captures, negative-depth and
partial-seam sources, seam-crossing visibility, full-ring enumeration,
deterministic ordering, source uniqueness, seed/ring isolation, reverse request
order, declared caps, and sampler-copy equivalence. This is still immutable
planning data; cave radii, SDF composition, and terrain carving are not active
yet.

Designed cave walls now have a stateless versioned noise applicator that can
only run on an already-resolved topology density. Its three integer-owned value
noise octaves remain exactly periodic around the ring, including a blended
partial final lattice span, while retaining stable signed-coordinate sampling
at negative depth and large world coordinates. Per-feature profiles hard-cap
displacement at 64 m, require a positive protected-open-core distance, and
limit noise to a finite solid-side wall band. The mask peaks at the topology
wall, becomes exactly zero throughout the protected open core and remote solid,
and cannot open material farther from the original wall than its declared
displacement. Applicator identity includes the shared terrain seed in its
cave-specific namespace, ring circumference,
algorithm versions, wavelengths, weights, and safety caps; independent sampler
copies retain it while current terrain density and terrain hashes remain
unchanged. `SMOOTH_TERRAIN_CAVE_WALL_NOISE_PASS` guards bounded output,
partial-ring seam continuity, exact ring periodicity, core and far-solid
preservation, invalid/disabled profiles, seed/ring isolation, large signed
coordinates, reverse request order, and sampler-copy equivalence. The
applicator does not change visible terrain on its own.

Designed topology can now become real smooth-terrain density through an
explicit default-off composition layer. The versioned connectivity scaffold
turns chamber anchors into 48 m positive-density cores and routed open faces
into 16 m capsules, max-unions the recognizable family SDFs, then applies the
bounded wall-noise profile to the combined topology. Each regular, medium,
coarse, and native lattice keeps ordinary route evaluation in a 56 m local
snapshot while a separately filtered 3,200 m anchor capture resolves massive
family descriptors once per region. Topology maps are not rebuilt per voxel,
distant corridor segments are excluded from the hot sampling loop, and
neighboring captures agree throughout the complete wall band. Enabled baseline
identity includes the composer while the disabled default preserves the exact
previous revision, density, and legacy terrain hash.

The authoritative order is now natural terrain, designed cave density,
structural roof/underside/hull/sidewall constraints, and finally the immutable
sequence-ordered player edit journal. Cave-created boundaries use rock, cannot
alter protected structure or exterior samples, and cached lattices invalidate
when the cave baseline changes. Existing edit journals may migrate from the
pre-designed-cave baseline so player work remains last rather than being
overwritten by regenerated caves. `SMOOTH_TERRAIN_CAVE_TOPOLOGY_DENSITY_PASS`
guards bounded sphere/capsule density, seam/partial-province behavior,
wall-band agreement, identities, and capture order;
`SMOOTH_TERRAIN_CAVE_DENSITY_COMPOSITION_PASS` guards default-off compatibility,
opt-in carving, regional capture bounds, protected structures, cache revisions,
and identical edit ordering across direct, regular-chunk, and volumetric-lattice
sampling. Distribution, depth weighting, per-family exclusion audits, and human
traversal certification are still pending, so existing worlds keep this layer
disabled unless `EnableDesignedCaveDensityComposition` is explicitly set.
The live structural gate now treats the configured wall top only as the upper
extent of the exterior sidewall slab. It no longer compares negative-depth cave
samples against that positive wall-top coordinate, which had suppressed all
designed cave density in production. Complete-volume placement and the final
terrain/hull SDF continue to preserve the surface, protected roof, sidewalls,
and lower hull; the composition diagnostic reproduces the production coordinate
layout so this failure cannot silently return.
The in-game pause menu now provides `Teleport to Nearest Cave` beside surface
recovery. Its deterministic locator searches at most 96 unique nearby topology
provinces, derives valid depth and sidewall limits from the active placement
contract, requires a real 48 m carved chamber core, then teleports canonically
in noclip and immediately retargets smooth-terrain streaming. Disabled worlds
and bounded-search misses report a status instead of moving the player.

The final integrated topology suite now builds two independent same-contract
generation chains and compares complete immutable feature lists plus every
sample in forward- and reverse-loaded 37-cubed production chunk snapshots. It
uses real contributing negative-depth and capable partial-final-province cave
anchors, verifies exact periodic density in both ring directions, compares all
three cave-crossing 33-by-33 shared chunk faces, and checks the physical final-
chunk/first-chunk ring seam. Runtime validation reports this coverage through
`SMOOTH_TERRAIN_CAVE_TOPOLOGY_FINAL_PASS`.

### Gate 5 - Designed rather than noisy topology

- [x] Cave connectivity is stable for identical seed/configuration pairs.
- [x] Cave systems contain recognizable chambers, routes, branches, loops, and landmarks.
- [x] Noise adds surface character without creating dominant worm networks.

Production-density diagnostics now require all five topology roles in the
sampled design graph. Wall noise is bounded to a narrow cave-wall band and is
forbidden from changing protected open cores or remote solid space.

## 6. Cave feature families

- [x] Grand chambers: 100-400 m wide with optional vertical layering.
- [x] Rare megacaverns: 500-1,500 m wide and 100-600 m tall.
- [x] Super-rare supermassive caverns: 3,000-6,000 m wide and 1,200-2,400 m tall.
- [x] Fault caverns: broad, tilted fractures rather than round tunnels.
- [x] Pillar halls: retained structural columns with bounded spacing and thickness.
- [x] Chasm systems: vertically dominant spaces with shelves and bridges.
- [x] Underground basins: broad floors suitable for later lakes.
- [x] Cathedral caverns: domed ceilings and dominant central volumes.
- [x] Collapsed regions: rubble slopes, partial chambers, and blocked routes.
- [x] Engineered voids: maintenance trunks, reservoirs, habitats, and transit galleries.
- [x] Add authored SDF landmark templates to reduce procedural repetition.
- [x] Enforce roof, surface, sidewall, and lower-hull exclusion distances for every family.

Every chamber anchor now owns one immutable version-3 family descriptor whose
identity includes its seed domain, owner province, dimensions, operation
constants, and explicit variant. Grand chambers select layered or unlayered
forms; mega and supermassive radii enforce the declared width and height bands;
faults have a nonzero bounded tilt and thin fracture axis. Pillar grids retain
bounded columns, chasms retain side shelves and central bridges, basins expose
flat floors, cathedrals use tall central domes, and collapsed regions retain
rubble slopes and route-margin blockages. Engineered descriptors explicitly
select maintenance-trunk, reservoir, habitat, or transit-gallery geometry. The
48 m anchor core and 16 m graph routes remain max-unioned so these solids can
narrow spaces without accidentally disconnecting the authored topology.

Each descriptor also preselects one of eleven versioned authored SDF landmarks:
terraces, buttresses, nested domes, basins, ribbed galleries, bridge landings,
or bounded ruin masses. These operations alter the real family density field,
remain inside the descriptor envelope, and are part of snapshot identity.
Production placement uses the live ring width, conservative minimum terrain
surface, protected-roof top, lower-hull top, and structural bottom. Complete
tilted feature volumes and both corridor endpoints must clear every protected
surface; unsafe features are rejected rather than clipped through structure.

`SMOOTH_TERRAIN_CAVE_FEATURE_FAMILY_PASS` probes every family SDF and engineered
variant, both grand-layer states, stable ownership, seed isolation, finite
bounds, forward/reverse/shuffled resolution, negative depth, partial-ring and
ring-wrap captures, and bounded snapshot counts. Its fixed 50,000-anchor sanity
sample produced 478 megacaverns and 108 supermassive candidates.
`SMOOTH_TERRAIN_CAVE_FEATURE_PLACEMENT_PASS` additionally verifies that all ten
families receive stable, density-changing landmarks and that their complete
volumes obey the live placement contract.

### Gate 6 - Feature distribution

- [x] Automated distribution audits match configured rarity and size bands.
- [x] Supermassive caverns obey world-scale spacing and do not appear from per-chunk chance.
- [x] No random feature breaches the protected roof or structural hull.

The raw 50,000-anchor family audit continues to hold megacaverns near 1% and
supermassive candidates near 0.2%, while a further 140,000 placement samples
cover every configured depth band and recheck family size envelopes after
placement. Supermassive opportunities are owned by a deterministic horizontal
8,192 m macro lattice with 16,384 m minimum opportunity spacing; moving between
ordinary chunks inside one macro cannot reroll ownership. Per-family boundary
probes reject surface, deep-ocean floor, both sidewalls, protected roof, and
lower-hull breaches, including an unsafe corridor B endpoint.

## 7. Depth-band placement

- [x] 24-300 m: entrances, collapsed regions, small grand chambers, and sinkholes.
- [x] 300-2,000 m: grand chambers, pillar halls, cathedral caverns, and basins.
- [x] 2,000-4,500 m: fault caverns, major chasms, megacaverns, and engineered voids.
- [x] 4,500-7,000 m: rare megacaverns, supermassive caverns, deep faults, and ancient infrastructure.
- [x] 7,000-7,680 m: sparse terminal systems and designed access routes.
- [x] 7,680-8,192 m: protected roof slab with no random caves.
- [x] 8,192 m to lower hull: future structural cavity content.
- [x] Implement depth-weighted family selection and smooth band transitions.
- [x] Add placement audits for every depth band.

Family descriptors now receive a stable anchor-owned acceptance rank against a
continuous 128 m smoothstep weight at documented band boundaries. This changes
occurrence probability gradually without rerolling a rejected specialist into
an unrelated grand chamber. Audits cover pure shallow, middle, deep, abyssal,
terminal, protected-roof, and structural-cavity samples; the last two admit no
random family volume.

## 8. Persistence, gameplay, and certification

- [x] Store cave generator version, seed, and vertical-domain settings in each world.
- [x] Preserve old worlds with their original cave-generation contract.
- [x] Define an explicit migration path before changing existing terrain hashes.
- [x] Keep terrain-edit journals deterministic over the procedural cave baseline.
- [x] Reconnect water, navigation, resources, spawning, and anchored objects incrementally.
- [x] Add underground spawn/recovery rules and safe traversal checkpoints.
- [x] Benchmark grand, mega, and supermassive cavern viewpoints.
- [x] Run clean build, project parse, runtime smoke, save/reload, and long-traversal tests.
- [ ] Human-test darkness, scale, LOD transitions, editing, collision, and feature variety.

The gameplay handoff is a bounded, stable-key incremental contract for water,
navigation, resources, player spawning, and anchored objects; it supplies
deterministic additions/removals to those owners without pretending that later
content population is already authored. Underground saves and explicit pause-
menu checkpoints now require an enabled cave field, full structural bounds,
body clearance, a nearby floor for checkpoints, and resident movement collision
before control resumes.

The 2026-09-02 full certification route passed its clean build, editor parse,
water and underground-environment regressions, procedural/edit journal replay,
save/reload, streaming traversal, and all runtime smoke checks. Cold viewpoint
results were 73.1 s / 33.4 MB for Grand Chamber, 48.5 s / 38.4 MB for
Megacavern, and 100.2 s / 34.3 MB for Supermassive Cavern. These measurements
complete the benchmark task but do not satisfy the separate human frame-time and
visual-coherence acceptance gate below.

The same-day cave-generation optimization pass now shares immutable province
topology between worker samplers, removes cross-worker noise locking, culls only
SDF candidates proven unable to affect terrain, reuses captured fine transition
lattices, and shares coarse transition lattices through a strict 32-entry cache.
The full cold rerun passed at 29.5 s / 39.2 MB for Grand Chamber, 50.9 s /
44.1 MB for Megacavern, and 72.7 s / 40.0 MB for Supermassive Cavern. The memory
figures include the cache's bounded 5.75 MB. Grand and Supermassive are about
59.6% and 27.5% faster than the certified baseline; Megacavern remains about
2.4 s slower, so the human performance gate remains open and the next justified
optimization is a batched density-sampling path rather than reduced LOD coverage.

The follow-on generation pass completed four coverage-preserving changes. Each
requested lattice now compiles an exact terrain-only evaluation plan while the
public discovery/topology snapshot remains complete; aligned 1 m transition
faces and their owning 37-cubed parent coalesce through one strict 32-entry
cache; sectors proven uniform reject before allocating or fully sampling their
lattice; and underground admission runs collision/edit parents first, the 64 m
coarse fallback second, then the unchanged refinement tiers. The exactness smoke
caught and removed a cached-reciprocal rounding change in corridor evaluation,
and full terrain/reference equality now passes. The focused runtime benchmark
passed at 9.6 s / 52.1 MB for Grand Chamber, 20.7 s / 57.1 MB for Megacavern,
and 34.1 s / 53.0 MB for Supermassive Cavern. Against the immediately preceding
29.5/50.9/72.7 s pass, those are 67.4%, 59.4%, and 53.1% generation-time
reductions. The memory estimates now include both bounded density caches: 5.75
MB of coarse transition lattices plus at most 12.97 MB of coalesced regular
parents. No speed, distance, family, corridor, or LOD-coverage cutoff was added.

World save format 3 records the shared terrain seed and derived cave identity,
family-generator version,
placement-policy version/identity, enabled state, and complete vertical-domain
configuration. Loading validates this contract before any edit journal is
restored. Legacy format-1 worlds migrate only when designed caves are disabled;
format-2 independently seeded cave contracts, current-format mismatches, and
unknown terrain hashes remain untouched and
read-only instead of being reinterpreted or autosaved. Accepted legacy terrain
hashes provide the explicit safe rewrite path. Any future cave or terrain-hash
change must either retain its old contract implementation or add a deliberate
migrator before compatibility policy will load it.

### Gate 8 - Playable cave foundation

- [ ] Existing surface gameplay remains intact.
- [ ] A player can enter, edit, leave, reload, and revisit representative cave systems.
- [ ] Massive caverns remain visually coherent within the agreed frame-time and memory budgets.
- [ ] The dark structural cavity is closed, stable, and ready for later content.
