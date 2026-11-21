# Temporary Voxel Phase Tracker

Status legend:
- TODO: not started
- IN PROGRESS: currently being implemented
- DONE: implemented and tested in project context

## Phase 1 (Current)

Goal: one flat voxel chunk with runtime place/remove, mesh, and collision.

- DONE: Create Voxel core data types (block enum, constants).
- DONE: Create compact chunk voxel storage for 32x32x32.
- DONE: Create chunk mesh builder (prototype face-culling mesher).
- DONE: Create chunk node script that owns mesh + collision.
- DONE: Create runtime edit script with camera raycast place/remove pipeline.
- DONE: Attach chunk and editor scripts to test scene (scenes/VoxelPhase1Test.tscn).
- DONE: Verify in-engine place/remove loop in editor play mode.
- DONE: Add basic color map resource per block type (VoxelBlockColorMap + default .tres).

## Phase 2

Goal: multiple flat chunks, chunk-border edits, greedy meshing, background rebuilds, sparse save deltas.

- DONE: Chunk grid manager for loading adjacent chunks.
- DONE: Border-safe neighbor invalidation on edit.
- DONE: Greedy mesher replacement.
- DONE: Debounced async chunk rebuild queue.
- DONE: Sparse chunk delta save format (local index -> block override).

## Phase 3

Goal: streaming + procedural + 256-depth excavation + bedrock + above-surface build.

- DONE: Deterministic natural terrain generation.
- DONE: Excavation limits and bedrock enforcement.
- DONE: Above-surface build height limits.
- DONE: Streaming radii for visuals and collision.
- DONE: Untouched chunk memory/disk avoidance.

## Phase 4

Goal: ring coordinate conversion + seam wrapping + curved placement + floating origin.

- DONE: Canonical ring coordinate structs and wrap math.
- DONE: Chunk transform placement tangent to ring.
- DONE: Around-ring seam wrap tests.
- DONE: Floating origin bridge with canonical state preservation.

## Phase 5

Goal: nav, multiplayer-ready replication flow, anchored interactive objects, optimization.

- DONE: Chunk-local nav invalidation and delayed rebuild.
- DONE: Server-authoritative block edit command path.
- DONE: Chunk revision and replication structures.
- DONE: Anchored non-voxel build objects (doors/machines/etc.).
- DONE: Performance profiling and memory budget pass.

## Phase 6

Goal: operational tooling and smoke harnesses for authoritative + replication workflows.

- DONE: In-scene live debug HUD for Phase 5 metrics and event counters.
- DONE: Add replication snapshot capture/reapply hotkeys for quick in-editor smoke tests.
- DONE: Add authority-rejection simulation path for command failure validation.
- DONE: Bridge nav invalidation events into concrete nav region update calls.

## Phase 7

Goal: deterministic command replay validation for authoritative voxel edits.

- DONE: Phase 7 command journal (records authoritative edit commands/results with optional rejected-command capture).
- DONE: Replay baseline capture and journal lifecycle controls.
- DONE: Deterministic replay validator (replay commands from baseline, compare outcome signatures, compare final sparse-state hash).
- DONE: In-scene Phase 7 hotkeys for baseline capture, replay validation, and journal reset.

## Ring Integration (Current)

Goal: replace the tech demo's near ring surface with editable streamed voxel terrain while retaining full-ring distant visibility.

- DONE: Add habitable-ring voxel axis layout (around, across width, radial height/depth).
- DONE: Lift around-ring chunk limit for the 80 km-radius test scale.
- DONE: Add exact integer circumference configuration (15,708 chunks) and seam-safe derived radius.
- DONE: Add radial-gravity voxel explorer controller.
- DONE: Add far-ring-only rendering mode to prevent legacy near geometry overlap.
- DONE: Add `scenes/VoxelRingIntegrationTest.tscn` with 20 km width and -256 bedrock.
- DONE: Verify C# build and headless integration-scene startup.
- DONE: Replace player-versus-triangle-mesh movement with swept voxel AABB collision.
- DONE: Add axis-separated collision clipping, grounded detection, and Minecraft-style step selection.
- DONE: Add local proxy, regional proxy, global terrain, and legacy silhouette LOD layers.
- DONE: Propagate launcher-selected ring width through voxel and all terrain LOD representations.
- IN PROGRESS: In-editor visual, movement, collision, and edit smoke test.
- TODO: Validate surface/far-shell radial alignment from multiple positions around the ring.

## LOD Modernization Phase A (Complete)

Goal: improve transition stability, high-speed streaming, seam concealment, distant shadow cost, and diagnostics without replacing the current LOD architecture.

- DONE: Add capped canonical-velocity prediction to voxel streaming while retaining the current player window.
- DONE: Add independent predictive windows to local and regional proxy sectors.
- DONE: Add worker-generated skirts around proxy sector boundaries.
- DONE: Centralize coordinated voxel -> LOD1 -> LOD2 -> global fade ranges.
- DONE: Keep shadows on the local proxy while disabling regional/global terrain shadows.
- DONE: Add F6/F7 telemetry for voxel queues, prediction lead, proxy worker/upload time, and estimated mesh memory.
- DONE: User validation of Phase A behavior and lighting corrections.

## LOD Modernization Phase B (Implementation Complete — Awaiting Test)

Goal: make LOD behavior gameplay-correct and frame-budgeted.

- DONE: Propagate versioned sparse surface edits into LOD1/LOD2 with seam-safe, resolution-aggregated sampling.
- DONE: Invalidate proxy edit snapshots for local, restored, authoritative, and replicated edits, including baseline-reversion tombstones.
- DONE: Add capped braking-distance visual/collision prefetch with a bounded corridor and finite-velocity guards.
- DONE: Rebuild voxel load queues from the latest desired window with collision-first, current/predicted, wrap-aware priority.
- DONE: Add cancellable proxy workers, current-window priority, stale request/edit rejection, bounded uploads, and atomic active-mesh replacement.
- DONE: Disable the overlapping legacy FarRing slab in the integration scene and launcher path.
- DONE: Sparse surface edits now propagate through active proxy levels, the incremental min/max hierarchy, and affected segmented global HLOD meshes.
- VALIDATED: Clean C# build, clean editor diagnostics, and two static lifecycle audits with no remaining blockers.
- TEST GATE: Complete the edit, transition, rapid-direction-change, noclip-speed, seam, and legacy-slab runtime checks as part of the combined Phase B/Phase C in-editor gate before Phase D begins.

## LOD Modernization Phase C (Implementation Complete — Awaiting In-Editor Test)

Goal: replace fixed proxy windows with a scalable clipmap/HLOD architecture.

- DONE: Extend the centralized LOD coordinator with shared upload-count, upload-millisecond, and worker budgets.
- DONE: Convert proxy windows into bounded nested canonical clipmap rings with inner exclusion regions and retained old windows during budgeted replacement.
- DONE: Add resolution/FOV-aware screen-space error bands and smooth-heightfield geomorph targets.
- DONE: Segment the global terrain representation into independently replaceable HLOD meshes.
- DONE: Add a canonical circumference-wrapped min/max terrain hierarchy with incremental edit invalidation.
- DONE: Project sparse edits into proxy morph samples and affected global HLOD segments without rebuilding the full ring.
- DONE: Add fair worker scheduling, budgeted dirty-segment service, Phase C diagnostics, and clean worker-sampler shutdown.
- VALIDATED: Clean C# build, clean 30-frame headless integration startup/shutdown, 11-level/52,454-cell min/max hierarchy self-test, and screen-space LOD configuration self-test.
- TEST GATE: Complete in-editor clipmap color-view, edit propagation, seam crossing, rapid reversal, maximum-speed travel, and sustained frame-time/memory validation before Phase D begins.

## LOD Modernization Phase D (Blocked Until Phase C Test)

Goal: support extreme ring dimensions with stable precision.

- TODO: Complete floating-origin integration for all LOD layers.
- TODO: Store movement and streaming authority in 64-bit canonical coordinates.
- TODO: Reconstruct terrain in camera-relative local frames.
- TODO: Stress-test maximum launcher radius and width configurations.

## Ring Illumination Phase E (Blocked Until Phase D Test)

Goal: simulate sunlight reflected from the illuminated ring interior to create natural-looking ringshine on the night side.

- TODO: Add a dedicated `RinglightController` driven by the existing spatial day factor.
- TODO: Add editor-tweakable ambient ringshine intensity, color, and response curves.
- TODO: Add a weak player-relative, shadowless directional fill from the visible day-side ring.
- TODO: Add opposite-ring emissive glow and atmospheric haze controls.
- TODO: Keep direct sunlight, ringshine, local lights, and future planet light as independent contributions.
- TODO: Add live-refresh tuning for night ambient, ringshine direction, twilight suppression, and glow strength.

## Long-Range Sun Occlusion Phase F (Blocked Until Phase E Test)

Goal: let unloaded mountains and other large terrain features cast stable kilometer-scale sunlight shadows without expanding conventional shadow maps across the ring.

- TODO: Add a dedicated `RingSunOcclusionController` with editor-tweakable receiver range, occluder range, resolution, update rate, bias, strength, and temporal smoothing.
- TODO: Reuse Phase C min/max terrain mipmaps for hierarchical sun-ray queries in canonical ring coordinates.
- TODO: Generate a camera-centered coarse sunlight visibility mask that searches farther toward the sun than its receiver footprint.
- TODO: Account for ring curvature, around-ring wrapping, across-width bounds, floating-origin movement, and unloaded deterministic terrain.
- TODO: Apply far-terrain visibility to direct sunlight only, independently of ambient light, ringshine, local lights, emissive light, and future planetary light.
- TODO: Share far-shadow sampling across terrain, characters, structures, vegetation, and vehicles.
- TODO: Add sun angular-radius penumbra, temporal stabilization, and debug views for the mask, ray steps, and active occluders.
- TODO: Integrate sparse terrain edits through regional mip invalidation without requiring distant scene geometry to remain loaded.
- TEST GATE: Verify a distant mountain can shadow the player and nearby terrain without visible mask swimming, cascade degradation, or lighting contamination.
