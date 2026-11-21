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
            VerifyRuntimeSurfaceIndependentOfFieldAttach(fixture);
            VerifyGlobalTerrainDiskCache(fixture);
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

    /// <summary>
    /// Global segments sampled before the gameplay height field attaches must match those sampled after it: an unedited runtime world has one
    /// surface (the 2 m lattice), whatever UsesFloatSurface says. Regression for the load-timing race in the global terrain heights.
    /// </summary>
    private static void VerifyRuntimeSurfaceIndependentOfFieldAttach(Node3D fixture)
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
            grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(0.0, 0.0, 0.0));
            using RingTerrainSampler sampler = grid.CreateIndependentTerrainSampler()
                ?? throw new InvalidOperationException("attach fixture sampler unavailable");
            Require(sampler.UsesRuntimeSurfaceGeneration, "attach fixture did not select the runtime sampler");
            lod = new RingTerrainGlobalLod { Name = "AttachIndependenceLod" };
            fixture.AddChild(lod);
            lod.ConfigureGlobalPreparationFixture(grid, aroundSegments: 497, segmentCount: 2);
            lod.InstallGlobalPreparationPyramidFixture(new RingTerrainMinMaxPyramid(sampler, lod.RingWidthMeters, 1_000_000),
                grid.LodSurfaceEditVersion);

            RingLodSurfaceEditSnapshot before = grid.CaptureLodSurfaceEditSnapshot();
            lod.BuildGlobalRingFixture();
            lod.ServiceInitialGlobalBuildFixture();
            lod.WaitForGlobalPreparationWorkerFixture();
            var sampling = lod.GetGlobalPreparationSamplingFixture();
            int row = sampling.LocalAroundCount + 1;
            int[] indices = { 0, 1, 2, row + 1, row + 2, 3 * row + 1, sampling.AcrossCount * row + sampling.LocalAroundCount };
            Vector3[] heightsBefore = new Vector3[indices.Length];
            for (int i = 0; i < indices.Length; i++) heightsBefore[i] = lod.GetGlobalPreparedVertexFixture(indices[i]).TerrainVertex;

            field = new RingSurfaceHeightField(sampler.TerrainDefinitionHash, sampler.AroundRingVoxelLength,
                Mathf.RoundToInt(grid.RingAcrossWidthMeters), -8000, 8000, sampler.GetPresentationSurfaceHeight,
                useTriangleInterpolation: true);
            grid.AttachSurfaceHeightField(field);
            RingLodSurfaceEditSnapshot after = grid.CaptureLodSurfaceEditSnapshot();
            Require(!before.UsesFloatSurface && after.UsesFloatSurface && !before.HasEdits && !after.HasEdits,
                "attach fixture did not capture an unattached and an attached unedited snapshot");

            for (int i = 0; i < indices.Length; i++)
            {
                int index = indices[i], z = index / row, x = index % row;
                double around = (sampling.AroundStart + x) * sampling.AroundSpacing;
                double across = z * sampling.AcrossSpacing;
                float a = before.GetPresentationSurfaceHeight(sampler, around, across);
                float b = after.GetPresentationSurfaceHeight(sampler, around, across);
                Require(BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b),
                    $"presentation height at ({around}, {across}) depends on the height field attach: {a} before, {b} after");
            }
            // Explicit fractional coordinates, in case the segment spacing happens to land on the 2 m lattice.
            // The 2 m lattice and 1 m bilinear surfaces only differ where a cell is not planar, so scan many scattered points.
            var probes = new System.Collections.Generic.List<(double, double)> { (123.37, 4567.81), (1001.5, 9.25), (31.999, 31.001) };
            var random = new Random(20261121);
            for (int i = 0; i < 4000; i++)
                probes.Add((random.NextDouble() * sampler.AroundRingVoxelLength, random.NextDouble() * grid.RingAcrossWidthMeters));
            foreach ((double around, double across) in probes)
            {
                float a = before.GetPresentationSurfaceHeight(sampler, around, across);
                float b = after.GetPresentationSurfaceHeight(sampler, around, across);
                Require(BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b),
                    $"presentation height at ({around}, {across}) depends on the height field attach: {a} before, {b} after");
            }
            // The prepared global vertices (built from the unattached snapshot above) must equal those of a second global terrain built after the attach.
            var lodAfter = new RingTerrainGlobalLod { Name = "AttachIndependenceLodAttached" };
            fixture.AddChild(lodAfter);
            try
            {
                lodAfter.ConfigureGlobalPreparationFixture(grid, aroundSegments: 497, segmentCount: 2);
                lodAfter.InstallGlobalPreparationPyramidFixture(new RingTerrainMinMaxPyramid(sampler, lodAfter.RingWidthMeters, 1_000_000),
                    grid.LodSurfaceEditVersion);
                lodAfter.BuildGlobalRingFixture();
                lodAfter.ServiceInitialGlobalBuildFixture();
                lodAfter.WaitForGlobalPreparationWorkerFixture();
                for (int i = 0; i < indices.Length; i++)
                {
                    Vector3 now = lodAfter.GetGlobalPreparedVertexFixture(indices[i]).TerrainVertex;
                    Require(now == heightsBefore[i], $"prepared global vertex {indices[i]} depends on the height field attach: {heightsBefore[i]} -> {now}");
                }
            }
            finally
            {
                lodAfter.InvalidateGlobalPreparationFixture();
                lodAfter.WaitForGlobalPreparationWorkerFixture();
                lodAfter.Free();
            }
        }
        finally
        {
            if (lod != null) lod.InvalidateGlobalPreparationFixture();
            if (field != null) grid.DetachSurfaceHeightField(field);
            lod?.Free();
            grid.Free();
        }
    }

    private sealed class CacheBuildResult
    {
        public string State = "";
        public int Cached, Sampled, Eligible;
        public bool Wrote;
        public ulong[] Hashes = Array.Empty<ulong>();
        public string Morph = "";
        public float[][] Heights = Array.Empty<float[]>();
        public Vector3[][] MeshVertices = Array.Empty<Vector3[]>();
        public Color[][] MeshColors = Array.Empty<Color[]>();
    }

    /// <summary>
    /// Global terrain disk cache (#12): a build replayed from the cache is byte-identical to the sampled one, and any damaged file
    /// (wrong key, wrong version, truncated, flipped payload byte) falls back to a full resample and is rewritten.
    /// </summary>
    private static void VerifyGlobalTerrainDiskCache(Node3D fixture)
    {
        string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ringworld_gtcache_" + Guid.NewGuid().ToString("N"));
        string file = System.IO.Path.Combine(scratch, "global_terrain.bin");
        System.IO.Directory.CreateDirectory(scratch);
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
        try
        {
            fixture.AddChild(grid);
            grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(0.0, 0.0, 0.0));
            using RingTerrainSampler sampler = grid.CreateIndependentTerrainSampler()
                ?? throw new InvalidOperationException("cache fixture sampler unavailable");
            Require(sampler.UsesRuntimeSurfaceGeneration, "cache fixture did not select the runtime sampler");

            CacheBuildResult sampled = RunCacheBuild(fixture, grid, sampler, scratch);
            Require(sampled.State == "miss" && sampled.Cached == 0 && sampled.Sampled == 4 && sampled.Eligible == 4,
                $"first build must sample every segment with no cache file, got {sampled.State} cached={sampled.Cached} sampled={sampled.Sampled}");
            Require(sampled.Wrote && System.IO.File.Exists(file), "first build did not write the global terrain cache");
            byte[] good = System.IO.File.ReadAllBytes(file);
            Require(!System.IO.Directory.Exists(System.IO.Path.Combine(scratch, "lod_sectors")), "cache build created a lod_sectors folder");

            CacheBuildResult replayed = RunCacheBuild(fixture, grid, sampler, scratch);
            Require(replayed.State == "hit" && replayed.Cached == 4 && replayed.Sampled == 0 && !replayed.Wrote,
                $"second build must replay every segment from the cache, got {replayed.State} cached={replayed.Cached} sampled={replayed.Sampled} wrote={replayed.Wrote}");
            RequireSameCacheBuild(sampled, replayed, "cached build");
            Require(System.IO.File.ReadAllBytes(file).AsSpan().SequenceEqual(good), "a cache hit rewrote the file");

            RequireTotalHeader(good, out int version, out int keyLength, out int keyOffset, out int payloadOffset, out int payloadLength);
            var corruptions = new (string Name, Func<byte[]> Corrupt)[]
            {
                ("wrong key", () => { byte[] b = (byte[])good.Clone(); b[keyOffset + keyLength / 2] ^= 0x01; return b; }),
                ("wrong version", () => { byte[] b = (byte[])good.Clone(); BitConverter.GetBytes(version + 1).CopyTo(b, 4); return b; }),
                ("truncated file", () => good.AsSpan(0, good.Length / 2).ToArray()),
                ("flipped payload byte", () => { byte[] b = (byte[])good.Clone(); b[payloadOffset + payloadLength / 2] ^= 0x40; return b; }),
            };
            foreach ((string name, Func<byte[]> corrupt) in corruptions)
            {
                System.IO.File.WriteAllBytes(file, corrupt());
                CacheBuildResult fallback = RunCacheBuild(fixture, grid, sampler, scratch);
                Require(fallback.State == "stale" && fallback.Cached == 0 && fallback.Sampled == 4,
                    $"{name}: expected a full resample, got {fallback.State} cached={fallback.Cached} sampled={fallback.Sampled}");
                Require(fallback.Wrote, $"{name}: the stale file was not rewritten");
                RequireSameCacheBuild(sampled, fallback, name);
                Require(System.IO.File.ReadAllBytes(file).AsSpan().SequenceEqual(good), $"{name}: the rewritten file differs from the original");
            }
            CacheBuildResult recovered = RunCacheBuild(fixture, grid, sampler, scratch);
            Require(recovered.State == "hit" && recovered.Cached == 4, "the rewritten cache was not usable");
            // The same through an edit-version bump in the middle of the build (the height field attaching), which swaps the initial snapshot.
            var attachField = new RingSurfaceHeightField(sampler.TerrainDefinitionHash, sampler.AroundRingVoxelLength,
                Mathf.RoundToInt(grid.RingAcrossWidthMeters), -8000, 8000, sampler.GetPresentationSurfaceHeight, useTriangleInterpolation: true);
            try
            {
                System.IO.File.Delete(file);
                CacheBuildResult swappedMiss = RunCacheBuild(fixture, grid, sampler, scratch, attachField);
                Require(swappedMiss.State == "miss" && swappedMiss.Sampled == 4 && swappedMiss.Wrote && System.IO.File.Exists(file),
                    $"snapshot swap during a miss build lost the cache write: {swappedMiss.State} sampled={swappedMiss.Sampled} wrote={swappedMiss.Wrote}");
                RequireSameCacheBuild(sampled, swappedMiss, "snapshot swap (miss)");
                Require(System.IO.File.ReadAllBytes(file).AsSpan().SequenceEqual(good), "snapshot swap wrote a different file");
                grid.DetachSurfaceHeightField(attachField);
                CacheBuildResult swappedHit = RunCacheBuild(fixture, grid, sampler, scratch, attachField);
                Require(swappedHit.State == "hit" && swappedHit.Cached == 4 && swappedHit.Sampled == 0 && !swappedHit.Wrote,
                    $"snapshot swap during a hit build dropped the cache: {swappedHit.State} cached={swappedHit.Cached} sampled={swappedHit.Sampled}");
                RequireSameCacheBuild(sampled, swappedHit, "snapshot swap (hit)");
            }
            finally
            {
                grid.DetachSurfaceHeightField(attachField);
            }
        }
        finally
        {
            grid.Free();
            try { System.IO.Directory.Delete(scratch, true); } catch (Exception) { }
        }
    }

    private static void RequireTotalHeader(byte[] file, out int version, out int keyLength, out int keyOffset, out int payloadOffset, out int payloadLength)
    {
        Require(RingTerrainGlobalLod.TryReadGlobalTerrainCacheHeader(file, out version, out keyLength, out keyOffset, out _, out _, out payloadOffset, out payloadLength),
            "written cache file has no readable header");
        Require(version == RingTerrainGlobalLod.GlobalTerrainCacheVersion && payloadLength > 0 && file.Length == payloadOffset + payloadLength,
            "written cache file header is inconsistent");
    }

    private static CacheBuildResult RunCacheBuild(Node3D fixture, VoxelChunkGridManager grid, RingTerrainSampler sampler, string directory, RingSurfaceHeightField? attachDuringBuild = null)
    {
        var lod = new RingTerrainGlobalLod { Name = "GlobalTerrainCacheLod" };
        fixture.AddChild(lod);
        try
        {
            lod.ConfigureGlobalPreparationFixture(grid, aroundSegments: 256, segmentCount: 4);
            lod.EnableGlobalTerrainDiskCache = true;
            lod.GlobalTerrainCacheDirectoryOverride = directory;
            lod.InstallGlobalPreparationPyramidFixture(new RingTerrainMinMaxPyramid(sampler, lod.RingWidthMeters, 1_000_000), grid.LodSurfaceEditVersion);
            lod.BuildGlobalRingFixture();
            if (attachDuringBuild != null)
            {
                // The height field attaching bumps the edit version with no edit; the build must keep using (or writing) the cache.
                lod.ServiceInitialGlobalBuildFixture();
                grid.AttachSurfaceHeightField(attachDuringBuild);
                lod.RefreshGlobalPreparationEditsFixture();
                lod.WaitForGlobalPreparationWorkerFixture();
                lod.RefreshGlobalPreparationEditsFixture();
            }
            lod.WaitForAllGlobalPreparationWorkersFixture();
            System.Threading.Tasks.Task? write = lod.GlobalTerrainCacheWriteTaskFixture;
            if (write != null) Require(write.Wait(60_000), "global terrain cache write did not finish");
            var result = new CacheBuildResult { Wrote = write != null };
            (result.State, result.Cached, result.Sampled, result.Eligible) = lod.LastGlobalTerrainCacheResult;
            result.Hashes = lod.GetSegmentOutputHashesFixture();
            result.Morph = lod.GetMorphDeterminismReport();
            Require(lod.TryCapturePublishedGlobalMorphFixture(out RingTerrainGlobalMorphSnapshot snapshot), "cache build did not publish a morph snapshot");
            RingTerrainGlobalMorphSnapshot.Segment[] segments = snapshot.CopySegments();
            result.Heights = new float[segments.Length][];
            for (int i = 0; i < segments.Length; i++) result.Heights[i] = segments[i].CopyCanonicalHeights();
            var vertices = new System.Collections.Generic.List<Vector3[]>();
            var colors = new System.Collections.Generic.List<Color[]>();
            for (int i = 0; i < segments.Length; i++)
            {
                ArrayMesh mesh = lod.GetGlobalTerrainSegmentFixture(i)?.Mesh as ArrayMesh ?? throw new InvalidOperationException("cache build segment mesh missing");
                for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                {
                    var arrays = mesh.SurfaceGetArrays(surface);
                    vertices.Add(arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array());
                    colors.Add(arrays[(int)Mesh.ArrayType.Color].AsColorArray());
                }
            }
            result.MeshVertices = vertices.ToArray();
            result.MeshColors = colors.ToArray();
            return result;
        }
        finally
        {
            lod.InvalidateGlobalPreparationFixture();
            lod.WaitForGlobalPreparationWorkerFixture();
            lod.Free();
        }
    }

    private static void RequireSameCacheBuild(CacheBuildResult expected, CacheBuildResult actual, string label)
    {
        Require(expected.Hashes.AsSpan().SequenceEqual(actual.Hashes), $"{label}: per-segment output hashes (heights, colours, water flags) differ");
        Require(expected.Morph == actual.Morph && expected.Morph.Length > 0, $"{label}: morph TopologyAndHeightHash differs");
        Require(expected.Heights.Length == actual.Heights.Length, $"{label}: segment count differs");
        for (int i = 0; i < expected.Heights.Length; i++)
        {
            Require(expected.Heights[i].Length == actual.Heights[i].Length, $"{label}: segment {i} canonical height count differs");
            for (int v = 0; v < expected.Heights[i].Length; v++)
                Require(BitConverter.SingleToInt32Bits(expected.Heights[i][v]) == BitConverter.SingleToInt32Bits(actual.Heights[i][v]),
                    $"{label}: segment {i} canonical height {v} differs");
        }
        Require(expected.MeshVertices.Length == actual.MeshVertices.Length, $"{label}: published surface count differs");
        for (int s = 0; s < expected.MeshVertices.Length; s++)
        {
            Require(expected.MeshVertices[s].AsSpan().SequenceEqual(actual.MeshVertices[s]), $"{label}: published mesh vertices of surface {s} differ");
            Require(expected.MeshColors[s].AsSpan().SequenceEqual(actual.MeshColors[s]), $"{label}: published mesh colours of surface {s} differ");
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
