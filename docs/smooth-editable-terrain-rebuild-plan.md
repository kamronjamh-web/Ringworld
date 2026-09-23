# Smooth Editable Terrain Rebuild

## Scope and decisions

Replace both player-editable terrain implementations:

- the discrete block/greedy natural-terrain path;
- the hybrid smooth-natural pilot and its parallel SDF/override paths.

Keep the non-editable regional proxy rings, global/far backdrop, canonical ring
coordinate system, terrain generator, biome definitions, and LOD coverage
handoff. Keep concrete/glass construction as a separate block overlay so it is
not a second terrain representation.

The replacement lives in `scripts/Terrain/Editable` and is integrated behind an
explicit backend selector. No existing editable implementation is deleted when
the new backend first becomes active. At cutover, both legacy editable backends
remain compiled but dormant as a rollback option. They, their pilot UI, and their
save fields may be deleted only after the user has tested the new backend and
explicitly approved deletion.

## Terrain-overlap correction (2026-09-06)

- [x] Replace whole-sector medium visibility with eight cell-aligned 32 m
  presentation pieces from the same 64 m / 2 m-density capture. Each piece
  yields only to its ready, visible fine surface/near parent; other pieces
  remain available as fallback. No extra density captures or collision tiers.
- [x] Count complete hot-patch presentation even when some patches contain
  no triangles. Keep retained fine meshes authoritative during pending edits.
- [x] Match canonical fine ownership across the ring wrap and retain the
  existing floating-origin placement, material data, and cave stitching.
- [x] Block dormant legacy rebuild/streaming/edit mesh allocation, suppress
  the experimental coarse SDF presenter (including stale completions), and
  disable incompatible legacy controls in Live Tuning. Explicit legacy mode
  remains available; no legacy source or player saves were deleted.
- [x] Independent installed-triangle probe reproduced duplicate coverage
  before the fix (9/25 surface probes; 5/25 hot-patch probes). After the fix,
  the small-ring, Certified-preset, movement, hot-patch, floating-origin, and
  wrap probes reported zero duplicate rays and zero missing surface hits.
- [x] Build, pending-build/edit ownership test, legacy-isolation test, full
  smooth-terrain runtime/cave suite, and Certified Vulkan rendering probe.
- [ ] Player confirmation at the originally reported patch in normal play.

Run `tools/terrain_layer_overlap_probe.gd` (optionally with user argument
`--terrain-overlap-certified` or `--terrain-overlap-wrap`),
`tools/smooth_terrain_overlap_ownership_test.gd` with
`--smooth-terrain-overlap-ownership-test`, and
`tools/legacy_terrain_isolation_test.gd`. The full suite is
`scenes/SmoothTerrainRuntimeSmoke.tscn` with `--smooth-terrain-runtime-smoke`;
its temporary journal now uses a unique workspace `.godot` path instead of
the player's data directory. The GPU probe saves its inspection image to
`.godot/terrain_overlap_certified.png`.

This is an ownership correction, not a new above-ground LOD stitching system.
Existing above-ground boundary stitching and medium-only edit rebuild behavior
are unchanged. Tests use disposable fixtures, not the player's saved session;
the GPU fixture also reports rendering-resource warnings at shutdown, and the
local engine reports a certificate-store warning independently of these checks.

## Target architecture

1. **One canonical density field**
   - The procedural `RingTerrainSampler` supplies the immutable baseline.
   - Terrain is sampled in canonical 64-bit around-ring coordinates so wrap and
     floating-origin movement cannot change results.
   - A 32 m chunk owns 32 cubical cells at 1 m spacing. Worker snapshots contain
     the complete sample lattice plus a gradient/seam halo; neighboring chunks
     always obtain shared boundary values from the same canonical query.
   - Density, material, and revision data are immutable after capture and safe
     to consume on worker threads.

2. **One edit model**
   - Natural terrain edits are ordered, deterministic signed-distance CSG stamps
     (initially sphere add/subtract, with material and command identity).
   - A spatial index returns only stamps overlapping a requested chunk/halo.
   - Commands, snapshots, replication, undo/replay, and save data use a versioned
     `SmoothTerrainEditCommand`; no natural-terrain edit is represented as a
     block override.
   - Construction placement remains in an independent sparse block overlay and
     is composed visually/collision-wise after the terrain mesh is built.

3. **One production mesher**
   - Use an indexed regular-cell isosurface implementation based on the standard
     Transvoxel regular-cell tables, not the current block fallback or marching-
     tetrahedra pilot.
   - Intersections use the shared canonical density samples. Normals come from
     central density gradients rather than accumulated triangle normals.
   - Vertices carry natural material data for the existing triplanar terrain
     shader. Collision faces are produced from the worker-side indexed result;
     collision never reads mesh data back from Godot.
   - The first milestone is a clear managed reference implementation with
     deterministic tests. A new native backend may replace its hot loop only
     after byte-for-byte/topology parity is established.

4. **Streaming and presentation**
   - A new `SmoothTerrainGrid` owns desired/resident/building chunk state,
     cancellation tokens, monotonically increasing chunk revisions, and pooled
     presentation nodes.
   - Generation, edit composition, meshing, normals, and collision-face expansion
     happen off the main thread. Stale results are discarded by revision.
   - Main-thread mesh/collision installation retains the existing upload budget,
     prewarmed nodes/materials, and proxy-versus-terrain render-queue handshake
     that eliminated the previous terrain-load hitch.
   - A chunk reports visual readiness only after its render mesh is installed.
     The existing per-cell proxy coverage mask consumes that readiness contract.

5. **Dependent systems**
   - Editing raycasts query the density field when physics is one frame behind.
   - Trees, water, navigation invalidation, anchored objects, save/autosave,
     authoritative commands, replay, and replication consume small interfaces;
     none reaches into chunk storage.
   - The proxy/global LOD stack receives a read-only density/edit snapshot. Local
     caves and overhangs remain represented by editable chunks; distant proxies
     remain non-editable height/shell approximations and are hidden by the
     existing coverage handoff when local chunks are ready.

## Delivery stages

### Stage 0: contracts and deterministic tests

- Add the isolated namespace, coordinate/sample/edit/result contracts, and an
  in-process smoke-test entry point.
- Test ring wrap, negative coordinates, shared chunk-boundary density/material
  equality, deterministic edit ordering, and revision invalidation.
- Do not change the live scene yet.

### Stage 1: density snapshots and reference mesher

- Implement baseline-plus-stamps snapshot capture and the indexed regular-cell
  mesher with gradient normals and materials.
- Add fixtures for plane, sphere, cave/tunnel, empty, solid, and edited seams.
- Assert finite vertices/normals, valid winding/indices, deterministic mesh hash,
  and exact cross-chunk boundary agreement.

### Stage 2: streamed runtime slice

- Implement worker scheduling, cancellation/coalescing, pooled chunk presenters,
  collision promotion, upload budgeting, metrics, and a small test scene.
- Add a launch flag that swaps only the editable grid while leaving every
  non-editable LOD unchanged.
- Run stationary, walking, sprinting, flying, edit-spam, wrap-boundary, and
  unload/reload tests.

### Stage 3: gameplay and persistence migration

- Route terrain sculpting through `SmoothTerrainEditCommand`.
- Move construction blocks into the separate overlay.
- Introduce a versioned smooth-terrain save/journal and replication snapshot.
- Provide a one-time legacy importer: natural block removals/additions become
  deterministic density stamps, old natural SDF deltas are imported directly,
  and concrete/glass overrides move to the construction overlay.
- Reconnect water, trees, navigation, anchored objects, and fallback raycasts.

### Stage 4: LOD handoff and certification

- Adapt the existing proxy coverage provider to `SmoothTerrainGrid` readiness.
- Feed a read-only smooth edit snapshot to LOD1 without making proxy/global LOD
  editable.
- Certify no lower LOD shows through, no holes appear while chunks are pending,
  and fast travel/altitude transitions retain their current behavior.

### Stage 5: cutover to dormant legacy backends

- Make the new grid the default editable terrain path in all launch profiles.
- Keep the legacy natural block/greedy and hybrid smooth paths compiled but
  dormant behind an explicit development-only rollback selector.
- Prevent dormant backends from creating nodes, scheduling workers, registering
  input, loading saves, or consuming frame time/memory while the new backend is
  selected.
- Keep legacy saves read-only and available to the importer. Never overwrite them
  from the new backend.
- Present the runtime certification results to the user for hands-on testing.

#### Current implementation status (2026-08-12)

- The production scene now contains `SmoothTerrainGrid` and the independent
  `SmoothConstructionOverlay`.  Normal and Mega launch paths select them by
  default; `--legacy-editable-terrain` restores the block/greedy backend and
  `--hybrid-smooth-terrain-pilot` restores the prior hybrid pilot for
  development-only rollback testing.
- In smooth mode `VoxelChunkGridManager.DormantEditableBackend` keeps the
  legacy manager compiled and usable as the canonical sampler/geometry source,
  but prevents legacy chunk creation, pool prewarming, input work, streaming,
  meshing, and per-frame processing.  It must not be used to write saves.
- The gameplay controller uses the smooth density raycast fallback, subtracts
  natural terrain through `SmoothTerrainEditCommand`, routes placement to the
  separate concrete overlay, and removes overlay blocks before falling through
  to terrain sculpting.  `RingWorldSaveManager` autosaves and restores
  separate slot-scoped `smooth_terrain_edits_v1.json` and
  `smooth_construction_v1.json` files; it never writes legacy save files in
  smooth mode. Rollback mode selects only legacy persistence and LOD inputs.
- In smooth mode legacy voxel save files are detected but deliberately not
  loaded into the dormant backend, including region journals. They remain
  untouched as the recoverable backup and migration fails closed with a visible
  warning because the current mixed legacy formats have no lossless, certified
  conversion contract yet.
- The regional LOD1 proxy reads the smooth grid's immutable surface-edit
  snapshot and monotonic visual-readiness revision when the replacement is
  active. The cached projection is seam-canonical and also drives edited-surface
  vertical streaming/collision centering. Completed proxy uploads are validated
  against that same smooth revision rather than the dormant legacy revision, so
  restored or edited smooth journals cannot suppress every LOD ring. Proxy and
  global LOD remain non-editable.
- Smooth chunk presenters use the ring terrain triplanar shader and its grass/
  dirt textures instead of the temporary vertex-color material. Negative-axis
  projection handedness is corrected for the ring's inward-facing habitable
  surface, and emitted triangle indices now use Godot's clockwise front-face
  convention. Back-face culling therefore exposes the habitable surface instead
  of the underground side, while density-gradient normals continue to select
  top versus wall material consistently.
- Smooth terrain now owns the player's physical movement path. The enabled
  capsule moves against two-sided concave shapes built from the exact indexed
  visual topology, including caves and overhangs. Spawn settling waits for the
  complete 3x3 surface-collision neighborhood instead of only the center chunk,
  and movement pauses at an unready physical-mesh boundary rather than allowing
  the player to outrun terrain generation and fall through it.
- Collision-face arrays are expanded lazily only for chunks inside the physical
  radius, collision chunks are scheduled before the outer visual shell, and the
  integrated-GPU profile uses a 5x5x3 local detail window (75 chunks) backed by
  the already-overlapping LOD1 proxy. Desktop profiles retain 7x7x3 (147).
- Snapshot capture caches the production surface solution once per horizontal
  sample column instead of recomputing it for every vertical density/material
  sample. Each streamed column independently selects the vertical chunks that
  contain its surface, so uneven terrain cannot leave visible LOD over a missing
  exact collision mesh. Empty edit snapshots also skip edit-bucket queries.
- The LOD1 coverage atlas now requires the complete sampled vertical surface
  span for every covered horizontal chunk, including ready empty chunks, and at
  least one real render mesh. This is conservative when a 32 m column crosses
  more than one vertical chunk. The earlier center-only legacy/smooth height
  correction was insufficient and was not the cause of the reported ribbons.
- The actual arch-facing ribbon defect was certified-scale precision loss in the
  smooth mesh transform. At arc 28,779,700 m, adding local one-metre vertices to
  a float canonical origin rounded alternate lattice rows onto each other
  because float spacing was already two metres. Canonical origin plus local
  vertex math now stays in double precision until after floating-origin
  subtraction. A Forward+ capture at the exact failing 5,000 km-ring coordinate
  is clean, and a deterministic regression rejects any collapsed transformed
  triangle there.
- Interactive natural edits retain their last proven render and collision mesh
  until the replacement installs atomically. Each resident entry retains its
  latest immutable 37-cubed density/material lattice; an append-ordered brush is
  composed onto that cached lattice instead of resampling procedural terrain and
  replaying the complete journal. Rapid clicks accumulate on the newest target
  snapshot even when an older mesh job is canceled, eliminating rebuild
  starvation. The proxy surface projection likewise recalculates only columns
  touched by the appended brush. The runtime smoke measures one replacement at
  46.23 ms and a 16-edit no-wait burst at 191.88 ms total on the Arc 140V laptop,
  both under a 500 ms regression ceiling with the final visual/collision revision
  installed.
- Smooth gameplay removal now raycasts the authoritative smooth density field
  after checking the separate construction overlay. Decorative physics bodies
  and stale concave terrain shapes can no longer choose a natural-brush center.
- Steady-state maintenance is bounded: collision ownership is recomputed only
  when the desired window changes, material uniforms update at 10 Hz, metrics at
  4 Hz, and historical diagnostics are pruned. These changes reduced the full
  smooth runtime smoke on the development machine from 22.2 seconds to 4.0
  seconds while retaining the 75-chunk laptop detail window.
- Automated verification now covers deterministic density/mesh/seam fixtures,
  runtime streaming/cancellation/pooling/re-anchoring, automatic focus,
  lazy collision promotion/reuse, real physics-world body contact, edit replay,
  incremental-versus-full snapshot equivalence, a 16-edit cancellation burst,
  certified-scale transform precision at the reported site,
  journal compatibility and file round-trips, construction file replay,
  seam-aware readiness, edited vertical recentering, a launcher-level smooth
  physical-spawn smoke, a five-chunk physical traversal smoke, and the existing
  LOD coverage-atlas handoff. The traversal crossed 164.5 m while maintaining
  all nine nearby collision chunks and never falling below the generated
  surface. Hands-on visual certification of altitude transitions and proxy
  appearance is still required before production certification.

### Stage 6: deletion after explicit user approval

- Only after explicit approval, remove the legacy natural block/greedy path,
  `VoxelSmoothTerrainMesher`, hybrid pilot flags/UI, duplicated natural SDF
  dictionaries/snapshots, obsolete edit commands, and experimental coarse
  editable pilot integration.
- Retain only the independent construction overlay and non-editable LOD stack.
- Remove the development rollback selector after the approved deletion.

## Acceptance gates

- `dotnet build RingworldTechDemo.csproj --no-restore`: zero warnings/errors.
- Deterministic smoke suite passes for density, edits, meshing, seams, saves, and
  replay; identical seed/commands produce identical hashes.
- No visible cracks at chunk seams or ring wrap, including after edits that cross
  two or more chunks.
- No lower-resolution proxy is visible beneath a ready editable chunk and no hole
  appears before readiness.
- Main-thread terrain installation stays within the existing 1.5 ms budget when
  work can be deferred; a single install is targeted below 4 ms on the current
  development machine, with no mesh readback for collision.
- Streaming remains bounded under cancellation, rapid travel, and edit spam;
  metrics expose worker time, queue depth, stale results, upload time, resident
  memory, and collision promotion time.
- Existing saves either import successfully once or fail closed with an explicit
  backup and error; they are never silently overwritten.
