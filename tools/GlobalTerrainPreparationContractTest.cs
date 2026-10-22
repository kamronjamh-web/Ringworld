using System;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo;

/// <summary>Headless regression contracts for background global-terrain preparation.</summary>
public partial class GlobalTerrainPreparationContractTest : RefCounted
{
    public bool Run()
    {
        Node3D? fixture = null;
        VoxelChunkGridManager? grid = null;
        RingTerrainGlobalLod? lod = null;
        try
        {
            SceneTree tree = Engine.GetMainLoop() as SceneTree
                ?? throw new InvalidOperationException("scene tree unavailable");
            fixture = new Node3D { Name = "GlobalTerrainPreparationFixture" };
            tree.Root.AddChild(fixture);
            grid = new VoxelChunkGridManager {
                BuildOnReady = false,
                DormantEditableBackend = true,
                EnableChunkStreaming = false,
                UseDeterministicTerrainGeneration = true,
                TerrainSettings = new VoxelTerrainSettings()
            };
            fixture.AddChild(grid);
            using RingTerrainSampler sampler = grid.CreateIndependentTerrainSampler()
                ?? throw new InvalidOperationException("fixture sampler unavailable");
            lod = new RingTerrainGlobalLod { Name = "GlobalTerrainPreparationLod" };
            fixture.AddChild(lod);
            lod.ConfigureGlobalPreparationFixture(grid);

            VerifyWorkerSamplesAndPublishes(lod, grid, sampler);
            VerifyCancellationRestartsWithoutDroppingTheHead(lod, grid);
            VerifyQualityAndPartitionRetention(lod, grid);
            VerifyRuntimeSurfaceSamplingAndEdits(fixture);
            VerifyExitDetachesAndObservesWorker(lod, grid);
            GD.Print("GLOBAL_TERRAIN_PREPARATION_CONTRACT=PASS");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError("GLOBAL_TERRAIN_PREPARATION_CONTRACT=FAIL " + exception.Message);
            return false;
        }
        finally
        {
            lod?.InvalidateGlobalPreparationFixture();
            fixture?.Free();
        }
    }

    private static void VerifyWorkerSamplesAndPublishes(RingTerrainGlobalLod lod, VoxelChunkGridManager grid, RingTerrainSampler sampler)
    {
        RingLodSurfaceEditSnapshot edits = grid.CaptureLodSurfaceEditSnapshot();
        lod.StartGlobalPreparationFixture(0, 1, null, edits);
        RingTerrainGlobalLod.GlobalPreparationProbe pending = lod.GetGlobalPreparationProbeFixture();
        Require(pending.VertexCount == (pending.LocalAroundCount + 1) * (pending.AcrossCount + 1), "worker vertex allocation shape changed");
        Require(lod.ActiveSegmentCount == 0 && lod.HasGlobalPreparationFixture,
            "queue head published before its background sampling completed");

        lod.WaitForGlobalPreparationWorkerFixture();
        pending = lod.GetGlobalPreparationProbeFixture();
        Require(pending.NextVertex == pending.VertexCount, "worker did not complete every prepared vertex");
        RingLodGeometrySnapshot geometry = grid.CaptureLodGeometrySnapshot();
        TerrainSample sample = sampler.GetTerrainSample(0, 0);
        float terrainHeight = edits.TryGetEditedSurfaceHeight(0, 0, out int edited) ? edited : sample.SurfaceHeight;
        Vector3 expectedVertex = geometry.CanonicalToWorld(0.0, terrainHeight + 1.0 - lod.RadialUnderlayOffsetMeters, 0.0);
        Color expectedColor = RingTerrainVisuals.ResolveMacroTextureColor(sample, Mathf.RoundToInt(terrainHeight), sampler, lod.LowTerrainColor, lod.HighTerrainColor);
        Require(pending.FirstTerrainVertex.DistanceTo(expectedVertex) < 0.001f, "worker vertex no longer matches the independent canonical sample");
        Require(pending.FirstTerrainColor.IsEqualApprox(expectedColor), "worker terrain color no longer matches the independent terrain sample");
        Require(pending.FirstDisplayedWater == (sample.HasSurfaceWater && terrainHeight < sample.WaterSurfaceHeight), "worker water classification changed");
        Require(!lod.TryPublishGlobalPreparationAtDeadlineFixture(0, 1, null, edits, 0),
            "completed worker ignored the publication deadline");
        Require(lod.HasGlobalPreparationFixture && lod.GlobalPreparationReadyFixture,
            "deadline deferral discarded a completed worker result");
        Require(lod.PublishGlobalPreparationFixture(0, 1, null, edits), "completed worker did not publish its queue head");
        MeshInstance3D segment = lod.GetGlobalTerrainSegmentFixture(0) ?? throw new InvalidOperationException("published segment missing");
        Require(segment.Mesh is ArrayMesh mesh && mesh.GetSurfaceCount() >= 1, "publication did not create terrain mesh geometry");
        Require(lod.PendingSegmentSampleCount == 0 && !lod.HasGlobalPreparationFixture, "published preparation remained pending");
    }

    private static void VerifyCancellationRestartsWithoutDroppingTheHead(RingTerrainGlobalLod lod, VoxelChunkGridManager grid)
    {
        lod.BuildGlobalRingFixture();
        Require(lod.PendingInitialSegmentCount == 1, "initial queue was not created for cancellation contract");
        lod.ServiceInitialGlobalBuildFixture();
        MeshInstance3D existing = lod.GetGlobalTerrainSegmentFixture(0) ?? throw new InvalidOperationException("replacement fixture missing");
        Mesh? oldMesh = existing.Mesh;
        Require(lod.HasGlobalPreparationFixture && lod.PendingInitialSegmentCount == 1 && ReferenceEquals(existing.Mesh, oldMesh),
            "initial queue head did not remain installed during preparation");
        lod.InvalidateGlobalPreparationFixture();
        lod.WaitForGlobalPreparationWorkerFixture();
        Require(!lod.HasGlobalPreparationFixture && lod.PendingInitialSegmentCount == 1 && ReferenceEquals(existing.Mesh, oldMesh),
            "cancellation dropped the initial queue head or published stale geometry");
        lod.WaitForAllGlobalPreparationWorkersFixture();
        Require(lod.PendingInitialSegmentCount == 0, "canceled initial queue head permanently stalled");
        Require(ReferenceEquals(lod.GetGlobalTerrainSegmentFixture(0), existing), "same-count replacement changed the visible node identity");
    }

    private static void VerifyExitDetachesAndObservesWorker(RingTerrainGlobalLod lod, VoxelChunkGridManager grid)
    {
        MeshInstance3D existing = lod.GetGlobalTerrainSegmentFixture(0) ?? throw new InvalidOperationException("exit fixture missing");
        Mesh? oldMesh = existing.Mesh;
        RingLodSurfaceEditSnapshot edits = grid.CaptureLodSurfaceEditSnapshot();
        lod.StartGlobalPreparationFixture(0, 1, existing, edits);
        System.Threading.Tasks.Task worker = lod.CaptureGlobalPreparationWorkerFixture()
            ?? throw new InvalidOperationException("exit fixture worker missing");
        lod.ExitGlobalPreparationFixture();
        try { Require(worker.Wait(10_000), "detached worker did not complete within ten seconds"); }
        catch (AggregateException exception) when (exception.InnerException is OperationCanceledException) { }
        Require(!lod.HasGlobalPreparationFixture && ReferenceEquals(existing.Mesh, oldMesh),
            "exit allowed pending preparation to replace visible terrain");
    }

    private static void VerifyRuntimeSurfaceSamplingAndEdits(Node3D fixture)
    {
        TerrainGenerationParameters recipe = TerrainGenerationParameters.Default();
        using var settings = new VoxelTerrainSettings {
            SurfaceGenerationRecipeJson = recipe.CanonicalJson,
            SurfaceGenerationContractId = RingTerrainSampler.RuntimeSurfaceGenerationContractId,
            BedrockY = -8000,
            SeaLevelY = 0,
            EnableUnified3dDensityTerrain = false,
            EnableProceduralCaves = false,
            EnableNaturalOverhangs = false,
            EnableDesignedCaveDensityComposition = false
        };
        var grid = new VoxelChunkGridManager {
            BuildOnReady = false, DormantEditableBackend = true, EnableChunkStreaming = false,
            UseTerrain3DSurface = true, UseDeterministicTerrainGeneration = true, UseRingCurvedPlacement = true,
            EnableCanonicalFloatingOriginAnchor = true,
            AroundRingChunkLength = CertifiedRingScale.AroundChunkCount,
            RingAcrossWidthMeters = CertifiedRingScale.RingWidthMeters,
            MaxBuildGlobalY = 8400, MinExcavationGlobalY = -8192, TerrainBottomGlobalY = -8192,
            TerrainSettings = settings
        };
        RingTerrainGlobalLod? lod = null;
        RingSurfaceHeightField? field = null;
        try
        {
            fixture.AddChild(grid);
            grid.GlobalTransform = new Transform3D(Basis.FromEuler(new Vector3(.02f, -.04f, .01f)), new Vector3(31, -17, 9));
            grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(0.0, 0.0, 0.0));
            using RingTerrainSampler sampler = grid.CreateIndependentTerrainSampler()
                ?? throw new InvalidOperationException("runtime fixture sampler unavailable");
            Require(sampler.UsesRuntimeSurfaceGeneration, "runtime fixture did not select v2 sampler");
            field = new RingSurfaceHeightField(sampler.TerrainDefinitionHash, sampler.AroundRingVoxelLength,
                Mathf.RoundToInt(grid.RingAcrossWidthMeters), -8000, 8000, sampler.GetPresentationSurfaceHeight,
                useTriangleInterpolation: true);
            grid.AttachSurfaceHeightField(field);
            lod = new RingTerrainGlobalLod { Name = "RuntimeGlobalPreparationLod" };
            fixture.AddChild(lod);
            lod.ConfigureGlobalPreparationFixture(grid, segmentCount: 2);
            lod.GlobalTransform = new Transform3D(Basis.FromEuler(new Vector3(-.03f, .06f, -.02f)), new Vector3(-11, 23, 7));
            // A 1,000km cell produces a compact real pyramid (about 32x1 cells) while
            // still executing the exact edit invalidation path used in production.
            lod.InstallGlobalPreparationPyramidFixture(new RingTerrainMinMaxPyramid(sampler, lod.RingWidthMeters, 1_000_000),
                grid.LodSurfaceEditVersion);
            lod.BuildGlobalRingFixture();
            var gateFocus = new Node3D { Name = "HullGateFocus" };
            fixture.AddChild(gateFocus);
            lod.AddHullRoofGateFixtures(2);
            lod.RunRegionalVisibilityFixture(gateFocus);
            Require(!lod.IsHullRoofVisibleFixture(0) && !lod.IsHullRoofVisibleFixture(1),
                "hull roof was visible before any terrain segment was built");
            int refreshes = lod.RegionalVisibilityRefreshCount;
            lod.RunRegionalVisibilityFixture(gateFocus);
            Require(lod.RegionalVisibilityRefreshCount == refreshes, "unchanged focus and origin re-ran the regional visibility refresh");
            // Floating-origin shift: the node moves, the focus does not.
            lod.GlobalPosition += new Vector3(5000.0f, 0.0f, 0.0f);
            lod.RunRegionalVisibilityFixture(gateFocus);
            Require(lod.RegionalVisibilityRefreshCount == refreshes + 1,
                "an origin shift with an unchanged focus did not refresh regional visibility");
            lod.GlobalPosition -= new Vector3(5000.0f, 0.0f, 0.0f);
            lod.ServiceInitialGlobalBuildFixture();
            lod.WaitForGlobalPreparationWorkerFixture();
            VerifyRuntimePreparedVertices(lod, grid, sampler);
            Vector3 beforeInitialEdit = lod.GetGlobalPreparedVertexFixture(0).TerrainVertex;
            float beforeInitialHeight = grid.CaptureLodSurfaceEditSnapshot().GetPresentationSurfaceHeight(sampler, 0.0, 0.0);
            Require(field.ApplyBrush(0, 0, 1, .25f), "runtime initial edit was rejected");
            ulong initialEditVersion = grid.LodSurfaceEditVersion;
            float editedInitialHeight = grid.CaptureLodSurfaceEditSnapshot().GetPresentationSurfaceHeight(sampler, 0.0, 0.0);
            Require(Mathf.IsEqualApprox(editedInitialHeight, beforeInitialHeight + .25f),
                "initial runtime edit was absent from its immutable LOD snapshot");
            lod.RefreshGlobalPreparationEditsFixture();
            Require(lod.PendingInitialSegmentCount == 2 && lod.HasGlobalPreparationFixture == false,
                "edit during initial preparation did not replace the stale worker snapshot");
            lod.ServiceInitialGlobalBuildFixture();
            lod.WaitForGlobalPreparationWorkerFixture();
            Vector3 afterInitialEdit = lod.GetGlobalPreparedVertexFixture(0).TerrainVertex;
            Require(afterInitialEdit.DistanceTo(beforeInitialEdit) > .01f,
                "initial edit snapshot did not change prepared v2 terrain height");
            lod.ServiceInitialGlobalBuildFixture();
            if (lod.PendingInitialSegmentCount == 1 && lod.ActiveSegmentCount == 1)
            {
                int beforePublishRefreshes = lod.RegionalVisibilityRefreshCount;
                lod.RunRegionalVisibilityFixture(gateFocus);
                Require(lod.RegionalVisibilityRefreshCount == beforePublishRefreshes + 1,
                    "publishing a terrain segment did not refresh regional visibility");
                Require(lod.IsHullRoofVisibleFixture(0) && !lod.IsHullRoofVisibleFixture(1),
                    "hull roof 0 was not shown (or roof 1 not held) after terrain segment 0 published");
            }
            lod.WaitForAllGlobalPreparationWorkersFixture();
            lod.RunRegionalVisibilityFixture(gateFocus);
            Require(lod.IsHullRoofVisibleFixture(0) && lod.IsHullRoofVisibleFixture(1),
                "hull roofs stayed hidden after every terrain segment published");
            Require(lod.PendingInitialSegmentCount == 0 && grid.LodSurfaceEditVersion == initialEditVersion,
                "initial queue stalled after accepting an edit snapshot");
            Require(lod.PublishedGlobalMeshContainsVertexFixture(0, afterInitialEdit),
                "initial edited prepared height was not published into segment geometry");
            VerifyPublishedGlobalMorphSnapshot(lod, grid, sampler);
            RingTerrainGlobalMorphSnapshot initialMorph = CaptureMorphSnapshot(lod);

            MeshInstance3D seamSegment = lod.GetGlobalTerrainSegmentFixture(0) ?? throw new InvalidOperationException("seam segment missing");
            Mesh? beforeSeamMesh = seamSegment.Mesh;
            Require(field.ApplyBrush(0, 0, 1, .25f), "runtime seam edit was rejected");
            ulong editedVersion = grid.LodSurfaceEditVersion;
            lod.RefreshGlobalPreparationEditsFixture();
            Require(lod.PendingSegmentRebuildCount == 2, "seam edit did not queue both wrapped dirty neighbors");
            Require(!lod.TryCapturePublishedGlobalMorphFixture(out _),
                "mixed old/new global segments remained available as a morph receiver");
            Require(lod.InitialGlobalMeshBuildPending == false, "completed runtime initial queue unexpectedly restarted");
            // The edit path must use its new immutable snapshot when the queued
            // replacement runs, rather than retrying the snapshot it captured before the brush.
            lod.ServiceDirtyGlobalSegmentsFixture();
            lod.WaitForGlobalPreparationWorkerFixture();
            Vector3 afterSeamEdit = lod.GetGlobalPreparedVertexFixture(0).TerrainVertex;
            Require(afterSeamEdit.DistanceTo(afterInitialEdit) > .01f,
                "seam edit snapshot did not change dirty prepared terrain height");
            lod.ServiceDirtyGlobalSegmentsFixture();
            Require(lod.LastSegmentRebuildCount == 1 && grid.LodSurfaceEditVersion == editedVersion,
                "runtime edit did not progress the dirty queue with its new snapshot");
            Require(!ReferenceEquals(seamSegment.Mesh, beforeSeamMesh) && lod.PublishedGlobalMeshContainsVertexFixture(0, afterSeamEdit),
                "seam-edited prepared height was not published into replacement geometry");
            while (lod.PendingSegmentRebuildCount > 0)
            {
                lod.ServiceDirtyGlobalSegmentsFixture();
                lod.WaitForGlobalPreparationWorkerFixture();
            }
            RingTerrainGlobalMorphSnapshot editedMorph = CaptureMorphSnapshot(lod);
            Require(editedMorph.SnapshotIdentity.TopologyAndHeightHash != initialMorph.SnapshotIdentity.TopologyAndHeightHash,
                "published global morph checksum ignored an edited receiver height");
            VerifyPublishedGlobalMorphSnapshot(lod, grid, sampler);
            grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(4096.0, 0.0, 0.0));
            VerifyPublishedGlobalMorphSnapshot(lod, grid, sampler, verifyInstalledMeshEndpoints: false);
            VerifyLateRingDirtyMapping(fixture, grid, sampler, lod.RingWidthMeters);
            VerifyMorphSurvivesRestartedGenerations(fixture, grid, sampler, lod.RingWidthMeters);
        }
        finally
        {
            if (lod != null) lod.InvalidateGlobalPreparationFixture();
            if (field != null) grid.DetachSurfaceHeightField(field);
            lod?.Free();
            grid.Free();
        }
    }

    private static void VerifyRuntimePreparedVertices(RingTerrainGlobalLod lod, VoxelChunkGridManager grid, RingTerrainSampler sampler)
    {
        RingLodSurfaceEditSnapshot edits = grid.CaptureLodSurfaceEditSnapshot();
        var sampling = lod.GetGlobalPreparationSamplingFixture();
        int row = sampling.LocalAroundCount + 1;
        foreach (int index in new[] { 0, 1, row + 1, sampling.AcrossCount * row + sampling.LocalAroundCount })
        {
            int z = index / row, x = index % row;
            double around = (sampling.AroundStart + x) * sampling.AroundSpacing;
            double across = z * sampling.AcrossSpacing;
            int sampleAround = (int)Math.Round(around), sampleAcross = (int)Math.Round(across);
            TerrainSample terrain = edits.GetPresentationTerrainSample(sampler, sampleAround, sampleAcross);
            float height = edits.GetPresentationSurfaceHeight(sampler, around, across);
            Vector3 expected = lod.GlobalTransform.AffineInverse() * grid.CaptureLodGeometrySnapshot()
                .CanonicalToWorld(around, height - lod.RadialUnderlayOffsetMeters, across);
            Color expectedColor = RingTerrainVisuals.ResolveMacroTextureColor(terrain, Mathf.RoundToInt(height), sampler, lod.LowTerrainColor, lod.HighTerrainColor);
            RingTerrainGlobalLod.GlobalPreparedVertex actual = lod.GetGlobalPreparedVertexFixture(index);
            Require(actual.TerrainVertex.DistanceTo(expected) < .001f, "runtime prepared vertex diverged from v2 presentation surface");
            Require(actual.TerrainColor.IsEqualApprox(expectedColor), "runtime prepared color diverged from v2 sampler");
            Require(actual.DisplayedWater == (terrain.HasSurfaceWater && height < terrain.WaterSurfaceHeight), "runtime prepared water diverged from v2 sampler");
        }
    }

    private static RingTerrainGlobalMorphSnapshot CaptureMorphSnapshot(RingTerrainGlobalLod lod)
    {
        Require(lod.TryCapturePublishedGlobalMorphFixture(out RingTerrainGlobalMorphSnapshot snapshot),
            "complete global receiver did not publish a morph snapshot");
        Require(snapshot.SnapshotIdentity.TopologyAndHeightHash != 0UL,
            "global morph snapshot omitted deterministic topology/height identity");
        return snapshot;
    }

    private static void VerifyPublishedGlobalMorphSnapshot(RingTerrainGlobalLod lod, VoxelChunkGridManager grid, RingTerrainSampler sampler,
        bool verifyInstalledMeshEndpoints = true)
    {
        RingTerrainGlobalMorphSnapshot snapshot = CaptureMorphSnapshot(lod);
        double aroundSpacing = grid.GetCanonicalRingCircumferenceMeters64() / Mathf.Max(64, lod.AroundSegments);
        double acrossSpacing = lod.RingWidthMeters / (double)Mathf.Max(4, lod.AcrossSegments);
        // Use a nonuniform fractional point in the first cell. The global mesh
        // diagonal is 01->10, so a height-then-bend interpolation would differ
        // from this expected triangle plane on the curved ring.
        double around = aroundSpacing * .75;
        double across = acrossSpacing * .125;
        RingLodGeometrySnapshot geometry = grid.CaptureLodGeometrySnapshot();
        RingLodSurfaceEditSnapshot edits = grid.CaptureLodSurfaceEditSnapshot();
        Vector3 p00 = GlobalReceiverVertex(geometry, edits, sampler, lod, 0.0, 0.0);
        Vector3 p10 = GlobalReceiverVertex(geometry, edits, sampler, lod, aroundSpacing, 0.0);
        Vector3 p01 = GlobalReceiverVertex(geometry, edits, sampler, lod, 0.0, acrossSpacing);
        Vector3 expected = p00 * .125f + p10 * .75f + p01 * .125f;
        Require(snapshot.TrySampleTerrainWorld(geometry, around, across, out Vector3 actual)
                && actual.DistanceTo(expected) < .001f,
            "global morph did not follow the actual curved 01-to-10 receiver triangle");
        if (verifyInstalledMeshEndpoints)
        {
            Require(lod.PublishedGlobalMeshContainsVertexFixture(0, lod.GlobalTransform.AffineInverse() * p00)
                    && lod.PublishedGlobalMeshContainsVertexFixture(0, lod.GlobalTransform.AffineInverse() * p10)
                    && lod.PublishedGlobalMeshContainsVertexFixture(0, lod.GlobalTransform.AffineInverse() * p01),
                "global morph endpoints were not the installed ArrayMesh receiver vertices");
        }

        // Canonical wrapping must select the final receiver cell for a point
        // just below zero, rather than producing a seam gap.
        double circumference = grid.GetCanonicalRingCircumferenceMeters64();
        double seamAround = -aroundSpacing * .25;
        double wrapped = circumference - aroundSpacing * .25;
        Require(snapshot.TrySampleTerrainWorld(geometry, seamAround, across, out Vector3 seam)
                && snapshot.TrySampleTerrainWorld(geometry, wrapped, across, out Vector3 wrappedSeam)
                && seam.DistanceTo(wrappedSeam) < .001f,
            "global morph canonical seam wrapping changed receiver geometry");
    }

    private static Vector3 GlobalReceiverVertex(
        RingLodGeometrySnapshot geometry,
        RingLodSurfaceEditSnapshot edits,
        RingTerrainSampler sampler,
        RingTerrainGlobalLod lod,
        double around,
        double across)
    {
        float height = edits.GetPresentationSurfaceHeight(sampler, around, across) - lod.RadialUnderlayOffsetMeters;
        return geometry.CanonicalToWorld(around, height, across);
    }

    private static void VerifyLateRingDirtyMapping(Node3D fixture, VoxelChunkGridManager grid, RingTerrainSampler sampler, int width)
    {
        var mappingLod = new RingTerrainGlobalLod { Name = "LateRingDirtyMappingLod" };
        var mappingField = new RingSurfaceHeightField(sampler.TerrainDefinitionHash, sampler.AroundRingVoxelLength,
            width, -8000, 8000, sampler.GetPresentationSurfaceHeight, useTriangleInterpolation: true);
        try
        {
            // Replace the earlier seam-edit field and capture its empty snapshot first,
            // which clears prior dirty columns before the late-ring regression begins.
            grid.AttachSurfaceHeightField(mappingField);
            _ = grid.CaptureLodSurfaceEditSnapshot();
            fixture.AddChild(mappingLod);
            mappingLod.ConfigureGlobalPreparationFixture(grid, segmentCount: 128);
            mappingLod.InstallGlobalPreparationPyramidFixture(new RingTerrainMinMaxPyramid(sampler, width, 1_000_000), grid.LodSurfaceEditVersion);
            mappingLod.InstallPublishedGlobalSegmentsFixture(128);
            long lateAround = sampler.AroundRingVoxelLength - 2;
            Require(mappingField.ApplyBrush(lateAround, 0, 1, .25f), "late-ring mapping edit was rejected");
            mappingLod.RefreshGlobalPreparationEditsFixture();
            Require(mappingLod.PendingSegmentRebuildCount == 3
                    && mappingLod.IsDirtyGlobalSegmentQueuedFixture(127)
                    && mappingLod.IsDirtyGlobalSegmentQueuedFixture(126)
                    && mappingLod.IsDirtyGlobalSegmentQueuedFixture(0),
                "late-ring dirty mapping overflowed or failed to wrap its neighbors");
        }
        finally
        {
            grid.DetachSurfaceHeightField(mappingField);
            mappingLod.Free();
        }
    }

    // A morph generation can restart before the previous one publishes: a saved edit applied while the initial
    // build is part-way through, or a second edit while the first edit's segments rebuild. Either way the
    // snapshot must still publish once every queue drains.
    private static void VerifyMorphSurvivesRestartedGenerations(Node3D fixture, VoxelChunkGridManager grid, RingTerrainSampler sampler, int width)
    {
        var stagedLod = new RingTerrainGlobalLod { Name = "RestartedMorphGenerationLod" };
        var stagedField = new RingSurfaceHeightField(sampler.TerrainDefinitionHash, sampler.AroundRingVoxelLength,
            width, -8000, 8000, sampler.GetPresentationSurfaceHeight, useTriangleInterpolation: true);
        try
        {
            grid.AttachSurfaceHeightField(stagedField);
            _ = grid.CaptureLodSurfaceEditSnapshot();
            fixture.AddChild(stagedLod);
            stagedLod.ConfigureGlobalPreparationFixture(grid, segmentCount: 4);
            stagedLod.InstallGlobalPreparationPyramidFixture(new RingTerrainMinMaxPyramid(sampler, width, 1_000_000), grid.LodSurfaceEditVersion);
            stagedLod.BuildGlobalRingFixture();
            for (int attempt = 0; stagedLod.ActiveSegmentCount < 1; attempt++)
            {
                Require(attempt < 64, "restarted-generation fixture never published its first initial segment");
                stagedLod.ServiceInitialGlobalBuildFixture();
                stagedLod.WaitForGlobalPreparationWorkerFixture();
            }
            Require(stagedLod.ActiveSegmentCount == 1 && stagedLod.PendingInitialSegmentCount == 3,
                "restarted-generation fixture published more than one initial segment before the edit");

            // Edit inside the one published segment while three initial segments remain queued. The brush sits
            // mid-segment so only segment 0 and its two neighbours are dirty; segment 2 is never rebuilt.
            long segmentZeroMiddle = sampler.AroundRingVoxelLength / 8;
            Require(stagedField.ApplyBrush(segmentZeroMiddle, 0, 1, .25f), "mid-initial-build edit was rejected");
            stagedLod.RefreshGlobalPreparationEditsFixture();
            stagedLod.WaitForGlobalPreparationWorkerFixture();
            stagedLod.RefreshGlobalPreparationEditsFixture();
            Require(stagedLod.PendingSegmentRebuildCount == 1 && stagedLod.IsDirtyGlobalSegmentQueuedFixture(0),
                "mid-initial-build edit did not queue only its published segment");
            stagedLod.WaitForAllGlobalPreparationWorkersFixture();
            Require(!stagedLod.TryCapturePublishedGlobalMorphFixture(out _),
                "global morph published before the mid-initial-build dirty segment rebuilt");
            DrainDirtyGlobalSegments(stagedLod);
            CaptureMorphSnapshot(stagedLod);
            VerifyPublishedGlobalMorphSnapshot(stagedLod, grid, sampler);

            // First edit dirties segments around 0; publish one replacement, then edit again before the rest finish.
            Require(stagedField.ApplyBrush(segmentZeroMiddle, 0, 1, .25f), "first steady-state edit was rejected");
            stagedLod.RefreshGlobalPreparationEditsFixture();
            Require(stagedLod.PendingSegmentRebuildCount >= 2 && !stagedLod.IsDirtyGlobalSegmentQueuedFixture(2),
                "first steady-state edit queued an unexpected dirty set");
            int rebuildAttempts = 0;
            do
            {
                Require(rebuildAttempts++ < 64, "first steady-state edit never published a replacement segment");
                stagedLod.ServiceDirtyGlobalSegmentsFixture();
                stagedLod.WaitForGlobalPreparationWorkerFixture();
            }
            while (stagedLod.LastSegmentRebuildCount == 0);
            Require(stagedLod.PendingSegmentRebuildCount >= 1, "first steady-state edit finished before the second edit");
            Require(stagedField.ApplyBrush(segmentZeroMiddle, 0, 1, .25f), "second steady-state edit was rejected");
            stagedLod.RefreshGlobalPreparationEditsFixture();
            stagedLod.WaitForGlobalPreparationWorkerFixture();
            stagedLod.RefreshGlobalPreparationEditsFixture();
            Require(!stagedLod.IsDirtyGlobalSegmentQueuedFixture(2), "second steady-state edit dirtied the untouched segment");
            DrainDirtyGlobalSegments(stagedLod);
            CaptureMorphSnapshot(stagedLod);
            VerifyPublishedGlobalMorphSnapshot(stagedLod, grid, sampler);
        }
        finally
        {
            stagedLod.InvalidateGlobalPreparationFixture();
            grid.DetachSurfaceHeightField(stagedField);
            stagedLod.Free();
        }
    }

    private static void DrainDirtyGlobalSegments(RingTerrainGlobalLod lod)
    {
        for (int attempt = 0; lod.PendingSegmentRebuildCount > 0; attempt++)
        {
            Require(attempt < 256, "dirty global segments did not drain after 256 service attempts");
            lod.ServiceDirtyGlobalSegmentsFixture();
            lod.WaitForGlobalPreparationWorkerFixture();
        }
    }

    private static void VerifyQualityAndPartitionRetention(RingTerrainGlobalLod lod, VoxelChunkGridManager grid)
    {
        MeshInstance3D initial = lod.GetGlobalTerrainSegmentFixture(0) ?? throw new InvalidOperationException("quality fixture missing");
        var hull = new MeshInstance3D { Name = "QualityHullIdentity" };
        var occluder = new Node3D { Name = "QualityOccluderIdentity" };
        lod.AddQualityIdentityFixtures(hull, occluder);
        long beforeSameQuality = lod.EstimatedGlobalMeshBytes;
        lod.ApplyRuntimeQuality(lod.AroundSegments, lod.AcrossSegments, lod.FadeStartMeters, lod.FadeEndMeters);
        Require(ReferenceEquals(lod.GetGlobalTerrainSegmentFixture(0), initial) && lod.PendingInitialSegmentCount == 0,
            "identical quality unnecessarily queued a global rebuild");
        Require(lod.EstimatedGlobalMeshBytes == beforeSameQuality, "identical quality changed mesh memory accounting");

        lod.ApplyRuntimeQuality(lod.AroundSegments, lod.AcrossSegments + 1, lod.FadeStartMeters, lod.FadeEndMeters);
        Require(ReferenceEquals(lod.GetGlobalTerrainSegmentFixture(0), initial), "across-only quality discarded the visible terrain node");
        Require(ReferenceEquals(lod.FirstHullFixture, hull) && ReferenceEquals(lod.OccluderFixture, occluder),
            "across-only quality rebuilt hull or occluder identities");
        lod.SetDirtyGlobalEditsFixture(grid.CaptureLodSurfaceEditSnapshot());
        lod.QueueDirtyGlobalSegmentFixture(0);
        lod.ServiceDirtyGlobalSegmentsFixture();
        Require(lod.QueuedDirtyGlobalSegmentCountFixture == 1 && lod.LastSegmentRebuildCount == 0,
            "dirty rebuild ran while initial replacements were still queued");
        lod.WaitForAllGlobalPreparationWorkersFixture();

        lod.GlobalSegmentCount = 2;
        lod.BuildGlobalRingFixture();
        lod.WaitForAllGlobalPreparationWorkersFixture();
        long beforePartitionChange = lod.EstimatedGlobalMeshBytes;
        MeshInstance3D oldFirst = lod.GetGlobalTerrainSegmentFixture(0) ?? throw new InvalidOperationException("partition fixture missing");
        lod.GlobalSegmentCount = 3;
        lod.BuildGlobalRingFixture();
        Require(lod.RetainedGlobalTerrainSegmentCountFixture == 2 && oldFirst.Visible,
            "changed partition did not retain old visible terrain while replacements prepare");
        Require(lod.EstimatedGlobalMeshBytes >= beforePartitionChange, "retained terrain was omitted from the memory estimate");
        lod.GlobalSegmentCount = 4;
        lod.BuildGlobalRingFixture();
        Require(lod.ActiveSegmentCount == 0 && lod.RetainedGlobalTerrainSegmentCountFixture == 2 && oldFirst.Visible,
            "repeated partition change lost the stable fallback before any replacement published");
        lod.WaitForAllGlobalPreparationWorkersFixture();
        Require(lod.ActiveSegmentCount == 4 && lod.RetainedGlobalTerrainSegmentCountFixture == 0,
            "changed partition did not retire retained terrain after every replacement published");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
