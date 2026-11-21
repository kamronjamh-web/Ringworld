# Phase 1 Voxel Setup (Temporary)

This project now includes a C# Phase 1 voxel prototype under Scripts/Voxel.

## Files Added

- Scripts/Voxel/Core/BlockType.cs
- Scripts/Voxel/Core/VoxelConstants.cs
- Scripts/Voxel/Core/RingVoxelCoordinate.cs
- Scripts/Voxel/Core/RingChunkCoordinate.cs
- Scripts/Voxel/Core/RingCoordMath.cs
- Scripts/Voxel/Phase1/VoxelChunkData.cs
- Scripts/Voxel/Phase1/VoxelChunkMesher.cs
- Scripts/Voxel/Phase1/MeshBuildData.cs
- Scripts/Voxel/Phase1/VoxelChunkNode.cs
- Scripts/Voxel/Phase1/VoxelEditController.cs
- Scripts/Voxel/Phase1/VoxelBlockColorMap.cs
- materials/voxel_block_color_map_default.tres

## What Phase 1 Implements

- One editable 32x32x32 voxel chunk.
- 1-meter voxels.
- Runtime place/remove edits via camera raycast.
- Mesh generation (prototype face-culling mesher).
- Chunk-level concave collision rebuild.
- Bedrock at local y=0 is non-removable.
- Editor-tweakable per-block color map Resource.

## Scene Wiring Steps

1. Open the prepared scene: scenes/VoxelPhase1Test.tscn.
2. Ensure your Godot project is using .NET and can compile C# scripts.
3. Confirm VoxelEditController exports are set:
   - CameraPath = ../Camera3D
   - ChunkPath = ../VoxelChunk
4. Optional color tuning:
   - Edit materials/voxel_block_color_map_default.tres to tweak Soil/Rock/Bedrock/Concrete/Glass colors.
5. Run scene and use:
   - Left click: remove block
   - Right click: place block

## Notes

- This is intentionally Phase 1 only (single chunk, synchronous rebuild).
- Phase 2 will introduce multi-chunk support, border invalidation, greedy meshing, and background rebuild queue.

## Phase 2 Preview

- New script: scripts/Voxel/Phase2/VoxelChunkGridManager.cs
- New script: scripts/Voxel/Phase2/VoxelGridEditController.cs
- New script: scripts/Voxel/Phase2/VoxelChunkDeltaSnapshot.cs
- New scene: scenes/VoxelPhase2GridTest.tscn
- Current behavior: loads a configurable adjacent chunk grid (default 3x3) with shared block color map.
- Seam edit behavior: border edits trigger neighbor chunk rebuild so shared faces stay correct across chunk boundaries.
- Meshing upgrade: VoxelChunkMesher now uses greedy face merging with cross-chunk neighbor sampling support.
- Rebuild pipeline: debounced async queue in grid manager (UseDebouncedRebuildQueue, RebuildDebounceSeconds, MaxQueuedRebuildsPerFrame).
- Sparse delta format: chunk key "around,across,vertical" -> { localLinearIndex -> blockId }.
- Save/load API on VoxelChunkGridManager:
   - BuildSparseChunkDeltaSnapshot()
   - BuildSparseChunkDeltaJson()
   - SaveSparseChunkDeltaJson(path)
   - LoadSparseChunkDeltaJson(path)
   - TryApplySparseChunkDeltaJson(json)
   - Default path: user://voxel_phase2_chunk_deltas.json

## Phase 3 Preview

- New script: scripts/Voxel/Phase3/VoxelTerrainSettings.cs
- New resource: resources/voxel/voxel_terrain_settings_default.tres
- New scene: scenes/VoxelPhase3TerrainTest.tscn
- Deterministic terrain path: VoxelChunkGridManager can now generate chunk baseline from seeded noise when UseDeterministicTerrainGeneration is enabled.
- Delta baseline compatibility: sparse save/load compares against generated baseline (flat or deterministic based on manager settings).
- Excavation limits: grid manager blocks edits below MinExcavationGlobalY when EnforceExcavationLimits is enabled.
- Bedrock enforcement: grid manager preserves bedrock (from TerrainSettings.BedrockY or FlatTerrainBedrockY) and rejects non-bedrock edits at or below bedrock depth when EnforceBedrockLayer is enabled.
- Above-surface cap: EnforceBuildHeightLimit + MaxBuildHeightAboveSurfaceVoxels limit placement above procedural/flat surface height.
- Streaming windows: EnableChunkStreaming supports separate visual and collision radii with camera/focus-driven chunk loading.
- Untouched chunk memory/disk avoidance: only loaded chunks exist in memory; only touched chunks are retained in override cache and serialized in sparse delta saves.
- Explorer drop-in: VoxelPhase3TerrainTest now includes a CharacterBody3D explorer controller (WASD move, Shift sprint, Space jump, mouse look, Esc release cursor, click recapture cursor).

## Phase 4 Preview

- Extended ring coordinate helpers in scripts/Voxel/Core/RingCoordMath.cs:
   - around-chunk wrap normalization
   - chunk/local <-> voxel coordinate conversion helpers
   - seam-wrap self-test utility (RunSeamWrapSelfTests)
- VoxelChunkGridManager ring integration:
   - optional curved tangent chunk placement (UseRingCurvedPlacement)
   - optional true per-vertex inward curvature warp (EnableCurvatureVertexWarp + CurvatureVertexWarpStrength)
   - wrapped around-ring chunk addressing via AroundRingChunkLength
   - world->chunk conversion for curved ring geometry
   - seam-safe around-ring neighbor sampling and rebuild invalidation
   - periodic around-ring terrain noise sampling to avoid seam discontinuity
   - optional startup seam self-test (RunAroundRingSeamSelfTestsOnReady)
- VoxelChunkNode/Mesher curvature path:
   - VoxelChunkNode can configure chunk-local ring curvature warp (and inverse for world->voxel hit mapping)
   - VoxelChunkMesher applies optional local vertex warp before mesh/collision generation
   - cylindrical arc-consistent warp aligns chunk end/start boundaries so around-ring chunk seams close
- Floating origin bridge support:
   - manager API: RegisterFloatingOriginShift(Vector3 worldShift)
   - optional compensation for manager motion when both world and manager shift
   - bridge group constant: voxel_floating_origin_bridge
- floating_origin.gd now broadcasts applied world shifts to the bridge group.
- New ring test scene: scenes/VoxelPhase4RingTest.tscn
   - curved placement enabled
   - runtime curvature tuning hotkeys enabled (U decrease, I increase, O reset to 1.0 seam-closing strength)
   - seam-gap debug overlay enabled (color lines bridge around-ring chunk seams; green = tight, red = wide)
   - seam self-test enabled
   - floating-origin bridge enabled

## Phase 5 Preview

- New Phase 5 scripts:
   - scripts/Voxel/Phase5/VoxelBlockEditCommand.cs
   - scripts/Voxel/Phase5/VoxelBlockEditResult.cs
   - scripts/Voxel/Phase5/VoxelReplicationSnapshot.cs
   - scripts/Voxel/Phase5/VoxelAnchoredObjectRecord.cs
   - scripts/Voxel/Phase5/VoxelRuntimeMetricsSnapshot.cs
   - scripts/Voxel/Phase5/VoxelChunkGridManager.Phase5.cs
- VoxelChunkGridManager Phase 5 systems:
   - delayed chunk-local nav invalidation queue with event hook (ChunkNavigationInvalidated)
   - server-authoritative edit command path via TryApplyAuthoritativeEditCommand(...)
   - unified authoritative edit outcome event (AuthoritativeEditProcessed) for success and reject telemetry
   - chunk revision tracking + replication snapshot build/apply APIs
   - anchored non-voxel object placement/removal APIs with per-object records
   - runtime metrics snapshot/report and soft memory budget warning path
- Existing world-position edit flow now routes through authoritative command path when enabled.
- New scene for Phase 5 system testing: scenes/VoxelPhase5SystemsTest.tscn
   - authoritative edits enabled
   - nav invalidation queue enabled
   - anchored object runtime enabled
   - metrics budget knobs exposed
   - debug anchored object placement enabled on middle mouse button

## Phase 6 Preview

- New script: scripts/Voxel/Phase5/VoxelPhase5DebugHud.cs
- New in-scene diagnostics HUD on scenes/VoxelPhase5SystemsTest.tscn
   - F7 toggle visibility
   - F8 print full runtime metrics report
   - F9 capture replication snapshot (revisions + overrides)
   - F10 reapply captured replication snapshot to loaded chunks
   - F11 toggle local authority mismatch simulation for rejection testing
   - live counters for authoritative edits (applied/rejected), nav invalidations, and revision updates
   - live memory budget state + queue depth snapshots
- Nav bridge integration in VoxelChunkGridManager Phase 5 module:
   - optional NavigationServer3D region bridge path driven by chunk nav invalidation queue
   - per-chunk region registration/cache and optional auto-create region nodes under bridge root
   - bridge refresh counter exposed to HUD for smoke-test visibility

## Phase 7 Preview

- New script: scripts/Voxel/Phase7/VoxelCommandJournalEntry.cs
- New script: scripts/Voxel/Phase7/VoxelReplayValidationReport.cs
- New script: scripts/Voxel/Phase7/VoxelChunkGridManager.Phase7.cs
- Deterministic replay systems in VoxelChunkGridManager:
   - command/result journal captured from TryApplyAuthoritativeEditCommand(...) traffic
   - baseline snapshot capture + reset controls
   - replay validation path that:
      - restores baseline
      - replays journaled commands through authoritative path
      - compares deterministic result signatures (applied/reason/chunk/local/prev/new)
      - compares final sparse voxel state hash (expected vs replayed)
      - restores pre-validation world state after validation run
- VoxelPhase5SystemsTest manager now exposes Phase 7 exports:
   - EnablePhase7CommandJournal
   - IncludeRejectedCommandsInPhase7Journal
   - MaxPhase7JournalEntries
   - LogPhase7ReplayValidation
   - ResetPhase7JournalOnGridRebuild
- HUD Phase 7 controls in scenes/VoxelPhase5SystemsTest.tscn:
   - F12 capture replay baseline and clear journal
   - Home validate deterministic replay journal
   - End clear replay journal and baseline
