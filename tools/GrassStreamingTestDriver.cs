using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Terrain.Editable;

namespace RingworldTechDemo.Tests;

/// <summary>SceneTree smoke fixture that drives the standing-grass production scheduler.</summary>
public partial class GrassStreamingTestDriver : Node
{
    [Signal] public delegate void CompletedEventHandler(bool passed);

    public override async void _Ready()
    {
        try { EmitSignal(SignalName.Completed, await RunAsync()); }
        catch (Exception exception) { GD.PushError("GRASS_STREAMING_CONTRACT=FAIL " + exception); EmitSignal(SignalName.Completed, false); }
    }

    private async Task<bool> RunAsync()
    {
        int previousMaxFps = Engine.MaxFps; Engine.MaxFps = 120;
        const long circumference = 981_748L * 32L; const int width = 318_000;
        var settings = (VoxelTerrainSettings)(GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres")?.Duplicate(true) ?? throw new InvalidOperationException("terrain settings missing"));
        using var sampler = new RingTerrainSampler(settings, true, circumference, 5_000_000.0, width, 8000);
        var layer = new RingStandingGrassLayer { VisibilityRadiusMeters = 72, SpatialTileMeters = 48, MaximumResidentTiles = 24, MaximumInstances = 12_000, MaximumTileBuildsInFlight = 2, MaximumTileUploadsPerFrame = 2, SuspendStreamingAboveSpeedMetersPerSecond = 100 };
        AddChild(layer); layer.InitializeForTests();
        try
        {
            RingCanonicalPosition focus = new(186_000, 0, 141_000); RingLodGeometrySnapshot geometry = Geometry(circumference, Vector3.Zero, focus);
            var clean = new RingLodSurfaceEditSnapshot(1, circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
            layer.ShowGrassTileBounds = true;
            layer.RefreshLodPresentationForTests(focus, geometry, circumference);
            MeshInstance3D? emptyBounds = layer.GetNodeOrNull<MeshInstance3D>("GrassTileBoundsDebug");
            Require(emptyBounds != null && !emptyBounds.Visible && emptyBounds.Mesh.GetSurfaceCount() == 0,
                "empty grass debug bounds produced a visible surface");
            layer.ShowGrassTileBounds = false;
            await Settle(layer, sampler, clean, focus, geometry);
            RingStandingGrassLayer.TileAudit[] baseline = layer.CaptureTileAuditsForTests();
            Require(baseline.Length > 0 && layer.ResidentGrassInstanceCount > 0, "baseline did not install grass");
            RingStandingGrassLayer.TileAudit populated = baseline[0];
            foreach (RingStandingGrassLayer.TileAudit tile in baseline)
                if (tile.InstanceCount > 0) { populated = tile; break; }
            Require(populated.InstanceCount > 0, "baseline installed only empty grass tiles");
            MultiMeshInstance3D? baselineRenderer = GodotObject.InstanceFromId(populated.RenderInstanceId) as MultiMeshInstance3D;
            Require(baselineRenderer?.Multimesh != null && baselineRenderer.Multimesh.UseColors && baselineRenderer.Multimesh.UseCustomData,
                "production grass MultiMesh did not enable COLOR and custom payloads");
            float[] payload = baselineRenderer!.Multimesh!.Buffer;
            Require(payload.Length >= RingGrassTileData.FloatsPerInstance,
                "production grass MultiMesh did not retain its packed payload");
            Require(payload[12] is >= 0 and <= 1 && payload[13] is >= 0 and <= 1
                && payload[14] is >= 0 and <= 1 && payload[15] is >= 0 and <= 1
                && payload[16] is >= 0 and <= 1 && payload[17] is >= 0 and <= 1
                && payload[18] is >= 0 and < 1 && payload[19] is >= 0 and < 1,
                "runtime grass COLOR/custom payload ordering changed");
            Require(layer.PendingGrassTileBuildCount <= 26 && layer.PendingGrassUploadCount <= 24, "queue exceeded resident bounds");
            RingStandingGrassLayer.TileAudit retained = Closest(baseline, focus, circumference);
            long stationaryBuilds = layer.TotalGrassBuildCount, stationaryUploads = layer.TotalGrassUploadCount;
            for (int step = 0; step < 3; step++) { layer.StepForTests(sampler, clean, focus, geometry); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); AssertBounds(layer); }
            Require(layer.TotalGrassBuildCount == stationaryBuilds && layer.TotalGrassUploadCount == stationaryUploads, "stationary streaming scheduled redundant work");
            // The surface cache affects only resolver lookup reuse. Changing
            // its capacity must preserve already-installed placement.
            long cacheBuilds = layer.TotalGrassBuildCount, cacheUploads = layer.TotalGrassUploadCount;
            layer.SurfaceCacheMegabytes = 0;
            for (int step = 0; step < 3; step++) { layer.StepForTests(sampler, clean, focus, geometry); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            Require(layer.TotalGrassBuildCount == cacheBuilds && layer.TotalGrassUploadCount == cacheUploads,
                "surface-cache cap adjustment rebuilt grass placement");
            layer.SurfaceCacheMegabytes = 4;
            await VerifyDistanceLodPresentation(layer, sampler, clean, focus, geometry, circumference);
            focus = focus with { AroundMeters = focus.AroundMeters + 48 }; await Settle(layer, sampler, clean, focus, geometry);
            RingStandingGrassLayer.TileAudit afterMove = Find(layer.CaptureTileAuditsForTests(), retained);
            Require(afterMove.PlacementHash == retained.PlacementHash && afterMove.RenderInstanceId == retained.RenderInstanceId, "unchanged resident tile rebuilt after movement");
            RingLodGeometrySnapshot rebased = Geometry(circumference, new Vector3(37, 0, -19), focus with { AroundMeters = focus.AroundMeters + 11 }); long frameBuilds = layer.TotalGrassBuildCount, frameUploads = layer.TotalGrassUploadCount; layer.StepForTests(sampler, clean, focus, rebased);
            RingStandingGrassLayer.TileAudit afterRebase = Find(layer.CaptureTileAuditsForTests(), retained);
            Require(afterRebase.PlacementHash == retained.PlacementHash && afterRebase.RenderInstanceId == retained.RenderInstanceId && afterRebase.Transform != retained.Transform && layer.TotalGrassBuildCount == frameBuilds && layer.TotalGrassUploadCount == frameUploads, "frame rebase resampled or did not move tile node");
            var columns = new Dictionary<RingTerrainColumnCoordinate, int> { [new((long)focus.AroundMeters, (int)focus.AcrossMeters)] = sampler.GetSurfaceHeight((long)focus.AroundMeters, (int)focus.AcrossMeters) + 4 };
            long before = layer.TotalGrassBuildCount; await Settle(layer, sampler, new RingLodSurfaceEditSnapshot(2, circumference, columns), focus, rebased);
            Require(layer.TotalGrassBuildCount > before, "latest dirty revision did not rebuild");
            long beforeSkipped = layer.TotalGrassBuildCount; await Settle(layer, sampler, new RingLodSurfaceEditSnapshot(4, circumference, columns), focus, rebased);
            Require(layer.TotalGrassBuildCount > beforeSkipped, "skipped revision was accepted as current");
            long beforeSuspend = layer.TotalGrassBuildCount; for (int step = 0; step < 3; step++) { layer.StepForTests(sampler, new RingLodSurfaceEditSnapshot(4, circumference, columns), focus, rebased, speed: 101); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            Require(layer.StreamingSuspended && layer.TotalGrassBuildCount == beforeSuspend, "fast travel scheduled new work");
            RingCanonicalPosition resumedFocus = focus with { AroundMeters = focus.AroundMeters + 144 }; await Settle(layer, sampler, new RingLodSurfaceEditSnapshot(4, circumference, columns), resumedFocus, rebased); Require(!layer.StreamingSuspended, "streaming did not resume at current location");
            long beforeSettings = layer.TotalGrassBuildCount; layer.CandidatesPerCell = 5; await Settle(layer, sampler, new RingLodSurfaceEditSnapshot(4, circumference, columns), resumedFocus, rebased); Require(layer.TotalGrassBuildCount > beforeSettings, "runtime placement setting did not invalidate resident tiles"); AssertBounds(layer);
            await VerifyHardCapAndStaleWork(sampler, clean, circumference);
            await VerifyDenseDensity(sampler, clean, circumference);
            await VerifyAppearanceProfileStreaming(sampler, circumference);
            await VerifySeamReload(sampler, clean, circumference);
            await VerifyDrawDistanceStreamingClamp(sampler, circumference);
            await VerifyAuthoritativeSurfaceAndExclusions(sampler, circumference);
            await VerifyProductionBaselineWorkerLease(sampler, circumference);
            VerifyWaterAndSlopeEligibility();
            GD.Print($"GRASS_STREAMING_CONTRACT=PASS tiles={layer.ResidentGrassTileCount} instances={layer.ResidentGrassInstanceCount} builds={layer.TotalGrassBuildCount} uploads={layer.TotalGrassUploadCount}"); return true;
        }
        finally { layer.QueueFree(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); Engine.MaxFps = previousMaxFps; }
    }
    private async Task Settle(RingStandingGrassLayer layer, RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits, RingCanonicalPosition focus, RingLodGeometrySnapshot geometry, SmoothTerrainVolumetricDensitySnapshot? field = null, RingGrassExclusionSnapshot? exclusions = null)
    { ulong deadline = Time.GetTicksMsec() + 8000; while (Time.GetTicksMsec() < deadline) { layer.StepForTests(sampler, edits, focus, geometry, field: field, exclusions: exclusions); AssertBounds(layer); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); if (layer.PendingGrassTileBuildCount == 0 && layer.PendingGrassUploadCount == 0) return; } throw new TimeoutException("grass streaming did not settle"); }
    private static RingStandingGrassLayer.TileAudit Find(RingStandingGrassLayer.TileAudit[] tiles, RingStandingGrassLayer.TileAudit expected)
    { foreach (RingStandingGrassLayer.TileAudit tile in tiles) if (tile.Around == expected.Around && tile.Across == expected.Across) return tile; throw new InvalidOperationException("resident tile was unexpectedly evicted"); }
    private static RingLodGeometrySnapshot Geometry(long circumference, Vector3 origin, RingCanonicalPosition anchor) => new(Transform3D.Identity, Vector3.Zero, origin, circumference / Math.Tau, 0, 0, circumference, -1000, 1000, anchor);
    private async Task VerifyDistanceLodPresentation(RingStandingGrassLayer layer, RingTerrainSampler sampler,
        RingLodSurfaceEditSnapshot edits, RingCanonicalPosition focus, RingLodGeometrySnapshot geometry, long circumference)
    {
        RingStandingGrassLayer.TileAudit nearBaseline = Closest(layer.CaptureTileAuditsForTests(), focus, circumference);
        long builds = layer.TotalGrassBuildCount, uploads = layer.TotalGrassUploadCount;
        // Keep the LOD fade inside this fixture's pre-existing 72m window.
        layer.ApplyGrassQuality(true, 24, 60, 12, 1, .30f);
        // Let the normal upload budget install paired passes. This must not
        // rebuild placement while the wanted window is unchanged.
        await Settle(layer, sampler, edits, focus, geometry);
        RingStandingGrassLayer.TileAudit near = Find(layer.CaptureTileAuditsForTests(), nearBaseline);
        Require(near.PlacementHash == nearBaseline.PlacementHash && near.RenderInstanceId == nearBaseline.RenderInstanceId
            && near.IntermediateRenderInstanceId != 0 && near.NearVisibleInstanceCount == near.InstanceCount,
            "near LOD transition rebuilt roots or did not allocate the paired pass");
        Require(layer.ValidateLodInstanceParametersForTests(),
            "grass LOD instance-count uniform was not a full-count float on both passes");
        builds = layer.TotalGrassBuildCount;
        uploads = layer.TotalGrassUploadCount;
        layer.ShowGrassTileBounds = true;
        layer.RefreshLodPresentationForTests(focus, geometry, circumference);
        MeshInstance3D? bounds = layer.GetNodeOrNull<MeshInstance3D>("GrassTileBoundsDebug");
        Require(bounds != null && bounds.Visible && bounds.Mesh.GetSurfaceCount() > 0,
            "populated grass debug bounds did not generate geometry");
        layer.ShowGrassTileBounds = false;
        layer.RefreshLodPresentationForTests(focus, geometry, circumference);
        Require(!bounds!.Visible && layer.TotalGrassBuildCount == builds && layer.TotalGrassUploadCount == uploads,
            "debug bounds did not hide or unnecessarily regenerated grass");
        RingCanonicalPosition farFocus = focus with { AroundMeters = focus.AroundMeters + 80 };
        layer.RefreshLodPresentationForTests(farFocus, Geometry(circumference, new Vector3(19, 0, -11), farFocus), circumference);
        RingStandingGrassLayer.TileAudit far = Find(layer.CaptureTileAuditsForTests(), nearBaseline);
        Require(far.PlacementHash == near.PlacementHash && far.RenderInstanceId == near.RenderInstanceId
            && far.IntermediateRenderInstanceId == near.IntermediateRenderInstanceId,
            "camera LOD transition regenerated a resident root buffer");
        Require(far.NearVisibleInstanceCount <= near.NearVisibleInstanceCount
            && far.IntermediateVisibleInstanceCount <= near.IntermediateVisibleInstanceCount,
            "far LOD did not conservatively reduce MultiMesh draw prefixes");
        Require(layer.InstalledGrassClumpCount <= layer.ResidentGrassInstanceCount * 2
            && layer.TotalGrassBuildCount == builds && layer.TotalGrassUploadCount >= uploads,
            "LOD presentation exceeded paired-buffer accounting or queued a worker rebuild");
        layer.ApplyGrassQuality(false, 24, 60, 12, 1, .30f);
        layer.StepForTests(sampler, edits, focus, geometry);
        RingStandingGrassLayer.TileAudit legacy = Find(layer.CaptureTileAuditsForTests(), nearBaseline);
        Require(legacy.RenderInstanceId == near.RenderInstanceId && legacy.PlacementHash == near.PlacementHash
            && legacy.NearVisibleInstanceCount == legacy.InstanceCount,
            "disabling distance LOD did not restore the legacy single-pass prefix");
    }
    private async Task VerifyHardCapAndStaleWork(RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits, long circumference)
    {
        var layer = new RingStandingGrassLayer { MaximumInstances = 1000, MaximumResidentTiles = 16, CellSizeMeters = 4, CandidatesPerCell = 16, VisibilityRadiusMeters = 115, MaximumTileBuildsInFlight = 2, MaximumTileUploadsPerFrame = 2 };
        AddChild(layer); layer.InitializeForTests();
        RingCanonicalPosition oldFocus = new(186_000, 0, 141_000), currentFocus = oldFocus with { AroundMeters = oldFocus.AroundMeters + 528 };
        try
        {
            layer.StepForTests(sampler, edits, oldFocus, Geometry(circumference, Vector3.Zero, oldFocus));
            layer.CandidatesPerCell = 15; // Changes the generation epoch while old jobs are still queued.
            await Settle(layer, sampler, edits, currentFocus, Geometry(circumference, Vector3.Zero, currentFocus));
            Require(layer.ResidentGrassInstanceCount >= 900 && layer.ResidentGrassInstanceCount <= 1000, "hard-cap stress did not reach a populated bounded result");
            foreach (RingStandingGrassLayer.TileAudit tile in layer.CaptureTileAuditsForTests())
            {
                double around = tile.Around * 48.0 + 24.0, delta = Math.Abs(around - currentFocus.AroundMeters); delta = Math.Min(delta, circumference - delta);
                Require(delta * delta + Math.Pow(tile.Across * 48.0 + 24.0 - currentFocus.AcrossMeters, 2) <= Math.Pow(layer.VisibilityRadiusMeters + layer.SpatialTileMeters, 2), "stale job installed a tile outside current neighborhood");
            }
            long builds = layer.TotalGrassBuildCount, uploads = layer.TotalGrassUploadCount;
            for (int step = 0; step < 3; step++) { layer.StepForTests(sampler, edits, currentFocus, Geometry(circumference, Vector3.Zero, currentFocus)); AssertBounds(layer); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            Require(layer.TotalGrassBuildCount == builds && layer.TotalGrassUploadCount == uploads && layer.PendingGrassTileBuildCount == 0 && layer.PendingGrassUploadCount == 0, "settled cap stress rescheduled impossible work");
        }
        finally { layer.QueueFree(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    }
    private static RingStandingGrassLayer.TileAudit Closest(RingStandingGrassLayer.TileAudit[] tiles, RingCanonicalPosition focus, long circumference)
    { RingStandingGrassLayer.TileAudit result = tiles[0]; double best = double.MaxValue; foreach (var tile in tiles) { double around = tile.Around * 48.0 + 24.0, delta = Math.Abs(around - focus.AroundMeters); delta = Math.Min(delta, circumference - delta); double distance = delta * delta + Math.Pow(tile.Across * 48.0 + 24.0 - focus.AcrossMeters, 2); if (distance < best) { best = distance; result = tile; } } return result; }
    private static void AssertBounds(RingStandingGrassLayer layer) => Require(layer.ResidentGrassTileCount <= layer.MaximumResidentTiles && layer.ResidentGrassInstanceCount <= layer.MaximumInstances && layer.PendingGrassTileBuildCount <= layer.MaximumResidentTiles + layer.MaximumTileBuildsInFlight && layer.PendingGrassUploadCount <= layer.MaximumResidentTiles, "streaming bounds exceeded during convergence");
    private async Task VerifyDenseDensity(RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits, long circumference)
    {
        var layer = new RingStandingGrassLayer
        {
            VisibilityRadiusMeters = 20, SpatialTileMeters = 16, MaximumResidentTiles = 9,
            MaximumInstances = 512, MaximumInstancesPerTile = 512, MaximumTileBuildsInFlight = 1,
            MaximumTileUploadsPerFrame = 2, BladesPerSquareMeter = 64
        };
        AddChild(layer); layer.InitializeForTests();
        RingCanonicalPosition focus = new(186_000, 0, 141_000);
        RingLodGeometrySnapshot geometry = Geometry(circumference, Vector3.Zero, focus);
        try
        {
            await Settle(layer, sampler, edits, focus, geometry);
            Require(layer.ResidentGrassInstanceCount > 0 && layer.ResidentGrassInstanceCount <= 512, "dense placement ignored the global hard cap");
            foreach (RingStandingGrassLayer.TileAudit tile in layer.CaptureTileAuditsForTests())
                Require(tile.InstanceCount <= 512, "dense placement ignored the per-tile hard cap");
            RingStandingGrassLayer.TileAudit retained = Closest(layer.CaptureTileAuditsForTests(), focus, circumference);
            long builds = layer.TotalGrassBuildCount;
            layer.BladesPerSquareMeter = 32;
            await Settle(layer, sampler, edits, focus, geometry);
            Require(layer.TotalGrassBuildCount > builds, "runtime density did not invalidate dense tiles");
            retained = Find(layer.CaptureTileAuditsForTests(), retained);
            ulong hash = retained.PlacementHash;
            layer.StepForTests(sampler, edits, focus, Geometry(circumference, new Vector3(13, 0, -7), focus));
            Require(Find(layer.CaptureTileAuditsForTests(), retained).PlacementHash == hash, "dense placement changed after frame rebase");
        }
        finally { layer.QueueFree(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    }
    private async Task VerifySeamReload(RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits, long circumference)
    {
        var first = new RingStandingGrassLayer { VisibilityRadiusMeters = 72, SpatialTileMeters = 48, MaximumResidentTiles = 24 };
        AddChild(first); first.InitializeForTests();
        RingCanonicalPosition left = new(circumference - 24, 0, 141_000); RingLodGeometrySnapshot geometry = Geometry(circumference, Vector3.Zero, left);
        try
        {
            await Settle(first, sampler, edits, left, geometry); RingStandingGrassLayer.TileAudit[] a = first.CaptureTileAuditsForTests(); Require(a.Length > 0 && first.ResidentGrassInstanceCount > 0, "seam baseline empty");
            first.QueueFree(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var second = new RingStandingGrassLayer { VisibilityRadiusMeters = 72, SpatialTileMeters = 48, MaximumResidentTiles = 24 };
            AddChild(second); second.InitializeForTests();
            try { await Settle(second, sampler, edits, new RingCanonicalPosition(24, 0, 141_000), geometry); RingStandingGrassLayer.TileAudit[] b = second.CaptureTileAuditsForTests(); Require(second.ResidentGrassInstanceCount > 0, "seam reload empty"); foreach (var leftTile in a) foreach (var rightTile in b) if (leftTile.Around == rightTile.Around && leftTile.Across == rightTile.Across && leftTile.InstanceCount > 0 && rightTile.InstanceCount > 0) { Require(leftTile.PlacementHash == rightTile.PlacementHash, "canonical seam/reload placement changed"); return; } throw new InvalidOperationException("seam views had no shared populated canonical tile"); }
            finally { second.QueueFree(); }
        }
        finally { if (GodotObject.IsInstanceValid(first)) first.QueueFree(); }
    }

    /// <summary>Exercises the live biome profile through the production worker, renderer, and material paths.</summary>
    private async Task VerifyAppearanceProfileStreaming(RingTerrainSampler sampler, long circumference)
    {
        const int width = 318_000;
        RingCanonicalPosition focus = new(186_000, 0, 141_000);
        RingLodGeometrySnapshot geometry = Geometry(circumference, Vector3.Zero, focus);
        var heights = new Dictionary<RingTerrainColumnCoordinate, int>();
        for (long around = (long)focus.AroundMeters - 48; around <= focus.AroundMeters + 48; around++)
        for (int across = (int)focus.AcrossMeters - 48; across <= focus.AcrossMeters + 48; across++)
            heights[new RingTerrainColumnCoordinate(around, across)] = 42;
        var hints = new RingLodSurfaceEditSnapshot(711, circumference, heights);
        var field = new SmoothTerrainVolumetricDensitySnapshot(new GrassPlaneBaseline(circumference, width),
            new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(), 711, circumference));
        var layer = new RingStandingGrassLayer
        {
            VisibilityRadiusMeters = 28, SpatialTileMeters = 16, CellSizeMeters = 8,
            BladesPerSquareMeter = 4, MaximumResidentTiles = 16, MaximumInstances = 20_000,
            GrassAppearanceProfile = new RingGrassAppearanceProfile
            {
                Enabled = true, GrasslandDensityMultiplier = 1, ForestDensityMultiplier = 1, SparseDensityMultiplier = 1,
                GrasslandHeightMultiplier = 1, ForestHeightMultiplier = 1, SparseHeightMultiplier = 1
            }
        };
        AddChild(layer); layer.InitializeForTests();
        try
        {
            await Settle(layer, sampler, hints, focus, geometry, field);
            RingStandingGrassLayer.TileAudit[] baseline = layer.CaptureTileAuditsForTests();
            Require(baseline.Length > 0 && layer.ResidentGrassInstanceCount > 0, "appearance fixture did not install flat-field grass");
            long builds = layer.TotalGrassBuildCount, uploads = layer.TotalGrassUploadCount;
            layer.GrasslandBiomeTint = new Color(.71f, .12f, .23f, 1);
            await Settle(layer, sampler, hints, focus, geometry, field);
            RingStandingGrassLayer.TileAudit[] palette = layer.CaptureTileAuditsForTests();
            Require(SamePlacement(baseline, palette) && layer.TotalGrassBuildCount == builds && layer.TotalGrassUploadCount == uploads,
                "palette-only appearance edit rebuilt placement or replaced a renderer");

            layer.GrasslandBiomeDensity = 0; layer.ForestBiomeDensity = 0; layer.SparseBiomeDensity = 0;
            await Settle(layer, sampler, hints, focus, geometry, field);
            Require(layer.ResidentGrassInstanceCount == 0, "zero biome density multipliers retained roots");
            layer.GrasslandBiomeDensity = 1; layer.ForestBiomeDensity = 1; layer.SparseBiomeDensity = 1;
            await Settle(layer, sampler, hints, focus, geometry, field);
            RingStandingGrassLayer.TileAudit[] restored = layer.CaptureTileAuditsForTests();
            Require(layer.ResidentGrassInstanceCount > 0 && SamePlacement(baseline, restored, requireSameRenderer: false),
                "restoring biome density did not deterministically repopulate roots");

            layer.GrasslandBiomeHeight = 1; layer.ForestBiomeHeight = 1; layer.SparseBiomeHeight = 1;
            await Settle(layer, sampler, hints, focus, geometry, field);
            RingStandingGrassLayer.TileAudit[] unitHeight = layer.CaptureTileAuditsForTests();
            RingGrassTileData[] unitRoots = layer.CaptureTileDataForTests();
            float unitBasisY = FirstBladeHeight(unitHeight);
            layer.GrasslandBiomeHeight = 1.5f; layer.ForestBiomeHeight = 1.5f; layer.SparseBiomeHeight = 1.5f;
            await Settle(layer, sampler, hints, focus, geometry, field);
            RingStandingGrassLayer.TileAudit[] tallHeight = layer.CaptureTileAuditsForTests();
            float tallBasisY = FirstBladeHeight(tallHeight);
            Require(SameRootIdentities(unitRoots, layer.CaptureTileDataForTests()) && Mathf.IsEqualApprox(unitHeight.Sum(tile => tile.InstanceCount), tallHeight.Sum(tile => tile.InstanceCount))
                && Mathf.Abs(tallBasisY / unitBasisY - 1.5f) <= .03f,
                "biome height multiplier did not rebuild deterministic roots at the expected Basis.Y scale");
        }
        finally
        {
            layer.QueueFree(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            field.DisposePooledWorkerContexts();
        }
    }

    private static bool SamePlacement(RingStandingGrassLayer.TileAudit[] before, RingStandingGrassLayer.TileAudit[] after, bool requireSameRenderer = true) =>
        before.Length == after.Length && before.All(tile =>
        {
            RingStandingGrassLayer.TileAudit match = after.FirstOrDefault(other => other.Around == tile.Around && other.Across == tile.Across);
            return match.PlacementHash == tile.PlacementHash && (!requireSameRenderer || match.RenderInstanceId == tile.RenderInstanceId) && match.InstanceCount == tile.InstanceCount;
        });

    private static bool SameRootIdentities(RingGrassTileData[] before, RingGrassTileData[] after)
    {
        foreach (RingGrassTileData first in before)
        {
            RingGrassTileData? second = after.FirstOrDefault(other => other.Key == first.Key);
            if (second == null || second.InstanceCount != first.InstanceCount) return false;
            for (int offset = 0; offset < first.Buffer.Length; offset += RingGrassTileData.FloatsPerInstance)
            foreach (int identityOffset in new[] { 3, 7, 11, 12, 13, 14, 15, 16, 17, 18, 19 })
                if (Mathf.Abs(first.Buffer[offset + identityOffset] - second.Buffer[offset + identityOffset]) > .0001f) return false;
        }
        return before.Length == after.Length;
    }

    private static float FirstBladeHeight(IEnumerable<RingStandingGrassLayer.TileAudit> tiles)
    {
        foreach (RingStandingGrassLayer.TileAudit tile in tiles.OrderBy(tile => tile.Around).ThenBy(tile => tile.Across))
        {
            MultiMeshInstance3D? renderer = GodotObject.InstanceFromId(tile.RenderInstanceId) as MultiMeshInstance3D;
            if (renderer?.Multimesh is { InstanceCount: > 0 } mesh)
            {
                // Headless rendering does not guarantee that per-instance
                // getters mirror a bulk Buffer assignment. Validate the same
                // packed transform rows consumed by the renderer instead.
                float[] buffer = mesh.Buffer;
                if (buffer.Length >= RingGrassTileData.FloatsPerInstance)
                    return new Vector3(buffer[1], buffer[5], buffer[9]).Length();
            }
        }
        throw new InvalidOperationException("appearance height fixture had no renderable blade");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static void VerifyWaterAndSlopeEligibility()
    {
        var water = new TerrainSample(40, TerrainRegion.Lowlands, 0, 0, 0, .5f, .5f,
            TerrainBiomeId.Grassland, SurfaceWaterKind.Lake, 48);
        Require(RingGrassTileBuilder.IsWaterCovered(water, 42), "underwater root was accepted");
        Require(RingGrassTileBuilder.IsWaterCovered(water, 48), "water-plane root equality was accepted");
        Require(!RingGrassTileBuilder.IsWaterCovered(water, 50), "raised soil inherited baseline water rejection");
        RingLodGeometrySnapshot geometry = Geometry(628, Vector3.Zero, new RingCanonicalPosition(0, 0, 0));
        Require(RingGrassTileBuilder.IsSlopeSuitable(Vector3.Up, 25, geometry, 25), "flat physical slope was rejected");
        Require(RingGrassTileBuilder.IsSlopeSuitable(new Vector3(.259f, .966f, 0), 25, geometry, 25), "gentle curved-ring slope was rejected");
        Require(!RingGrassTileBuilder.IsSlopeSuitable(new Vector3(.643f, .766f, 0), 25, geometry, 25), "steep curved-ring slope was accepted");
        Require(!RingGrassTileBuilder.IsSlopeSuitable(Vector3.Down, 25, geometry, 25), "downward-facing surface was accepted");
    }

    private sealed class GrassPlaneBaseline : ISmoothTerrainBaseline
    {
        public long AroundLength { get; }
        public int AcrossWidth { get; }
        public ulong Revision => 913;
        public GrassPlaneBaseline(long aroundLength, int acrossWidth) { AroundLength = aroundLength; AcrossWidth = acrossWidth; }
        public SmoothTerrainSample Sample(long around, int vertical, int across)
        {
            const float surface = 42.25f;
            SmoothTerrainMaterial material = vertical < surface - 2 ? SmoothTerrainMaterial.Rock : SmoothTerrainMaterial.Soil;
            return new SmoothTerrainSample(vertical - surface, (int)material, Revision);
        }
    }

    private async Task VerifyAuthoritativeSurfaceAndExclusions(RingTerrainSampler sampler, long circumference)
    {
        const int width = 318_000;
        RingCanonicalPosition focus = new(186_000, 0, 141_000);
        RingLodGeometrySnapshot geometry = Geometry(circumference, Vector3.Zero, focus);
        var heights = new Dictionary<RingTerrainColumnCoordinate, int>();
        for (long around = (long)focus.AroundMeters - 80; around <= focus.AroundMeters + 80; around++)
        for (int across = (int)focus.AcrossMeters - 80; across <= focus.AcrossMeters + 80; across++)
            heights[new RingTerrainColumnCoordinate(around, across)] = 42;
        var hints = new RingLodSurfaceEditSnapshot(91, circumference, heights);
        var dirtyCenter = new[] { new RingTerrainColumnCoordinate((long)focus.AroundMeters, (int)focus.AcrossMeters) };
        var dirtyLarge = new List<RingTerrainColumnCoordinate>();
        for (long around = (long)focus.AroundMeters - 12; around <= focus.AroundMeters + 12; around++)
        for (int across = (int)focus.AcrossMeters - 12; across <= focus.AcrossMeters + 12; across++)
            dirtyLarge.Add(new RingTerrainColumnCoordinate(around, across));
        var hints92 = new RingLodSurfaceEditSnapshot(92, circumference, heights, dirtyCenter);
        var hints93 = new RingLodSurfaceEditSnapshot(93, circumference, heights, dirtyLarge);
        var hints94 = new RingLodSurfaceEditSnapshot(94, circumference, heights, dirtyLarge);
        var baseline = new GrassPlaneBaseline(circumference, width);
        var soil = new SmoothTerrainVolumetricDensitySnapshot(baseline,
            new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(), 91, circumference));
        var layer = new RingStandingGrassLayer { VisibilityRadiusMeters = 28, SpatialTileMeters = 16, CellSizeMeters = 8, CandidatesPerCell = 4, BladesPerSquareMeter = 4, MaximumResidentTiles = 16, MaximumInstances = 20000 };
        AddChild(layer); layer.InitializeForTests();
        try
        {
            await Settle(layer, sampler, hints, focus, geometry, soil);
            Require(layer.ResidentGrassInstanceCount > 0, "authoritative soil field did not place grass");
            VerifyDenseBufferedRoots(layer.CaptureTileDataForTests(), soil, hints, sampler, geometry);
            long stationary = layer.TotalGrassBuildCount;
            layer.StepForTests(sampler, hints, focus, geometry, field: soil);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(layer.TotalGrassBuildCount == stationary, "equal immutable field rebuilt stationary grass");
            var sameIdentityReplacement = new SmoothTerrainVolumetricDensitySnapshot(baseline,
                new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(), 91, circumference));
            await Settle(layer, sampler, hints, focus, geometry, sameIdentityReplacement);
            Require(layer.TotalGrassBuildCount > stationary, "reused field identity replacement retained stale grass");

            var soilFill = new SmoothTerrainVolumetricDensitySnapshot(baseline, new SmoothTerrainEditSnapshot(
                new[] { new SmoothTerrainEditCommand(1, 920, 920, true, (long)focus.AroundMeters, 42, (int)focus.AcrossMeters, .5f, (int)SmoothTerrainMaterial.Soil) }, 92, circumference));
            long beforeFill = layer.TotalGrassBuildCount;
            RingStandingGrassLayer.TileAudit unaffectedFill = layer.CaptureTileAuditsForTests().First(tile =>
                Math.Abs(tile.Around * 16 + 8 - focus.AroundMeters) > 16
                || Math.Abs(tile.Across * 16 + 8 - focus.AcrossMeters) > 16);
            await Settle(layer, sampler, hints92, focus, geometry, soilFill);
            Require(layer.TotalGrassBuildCount > beforeFill, "sub-meter authoritative soil fill did not invalidate without a hint-height change");
            Require(new RingGrassSurfaceResolver(soilFill, hints92, sampler).TryResolve(focus.AroundMeters, focus.AcrossMeters, out RingGrassSurfaceHit raised)
                && raised.Height > 42.35, "sub-meter soil fill did not alter the authoritative root height");
            Require(layer.CaptureTileAuditsForTests().Any(tile => tile.Around == unaffectedFill.Around && tile.Across == unaffectedFill.Across && tile.RenderInstanceId == unaffectedFill.RenderInstanceId),
                "unaffected resident tile rebuilt during sequential sub-meter edit");

            long beforeRock = layer.TotalGrassBuildCount;
            var rockFill = new SmoothTerrainVolumetricDensitySnapshot(baseline, new SmoothTerrainEditSnapshot(
                new[] { new SmoothTerrainEditCommand(1, 921, 921, true, (long)focus.AroundMeters, 47, (int)focus.AcrossMeters, 12, (int)SmoothTerrainMaterial.Rock) }, 93, circumference));
            await Settle(layer, sampler, hints93, focus, geometry, rockFill);
            Require(layer.TotalGrassBuildCount > beforeRock, "exposed rock fill did not invalidate grass tiles");

            var excavation = new SmoothTerrainVolumetricDensitySnapshot(baseline, new SmoothTerrainEditSnapshot(
                new[] { new SmoothTerrainEditCommand(1, 922, 922, false, (long)focus.AroundMeters, 42, (int)focus.AcrossMeters, 12, (int)SmoothTerrainMaterial.Soil) }, 94, circumference));
            await Settle(layer, sampler, hints94, focus, geometry, excavation);
            Require(layer.ResidentGrassInstanceCount > 0, "local excavation incorrectly removed untouched grass");
            Require(new RingGrassSurfaceResolver(excavation, hints94, sampler).TryResolve(focus.AroundMeters, focus.AcrossMeters, out RingGrassSurfaceHit floor)
                && floor.Material == SmoothTerrainMaterial.Rock, "excavation did not expose the authoritative rock floor");

            await Settle(layer, sampler, hints, focus, geometry, soil);
            RingStandingGrassLayer.TileAudit[] stable = layer.CaptureTileAuditsForTests();
            RingStandingGrassLayer.TileAudit unaffected = stable.First(tile =>
                Math.Abs(tile.Around * 16 + 8 - focus.AroundMeters) > 16
                || Math.Abs(tile.Across * 16 + 8 - focus.AcrossMeters) > 16);
            // Change occupancy while the local replacement is queued, then
            // restore it immediately. A stale blocked result may never win.
            var blocked = new RingGrassExclusionSnapshot(1, circumference, new[] { new RingGrassExclusionBox(focus.AroundMeters - 8, focus.AroundMeters + 8, 35, 60, focus.AcrossMeters - 8, focus.AcrossMeters + 8) });
            layer.StepForTests(sampler, hints, focus, geometry, field: soil);
            layer.StepForTests(sampler, hints, focus, geometry, field: soil, exclusions: blocked);
            await Settle(layer, sampler, hints, focus, geometry, soil, blocked);
            Require(layer.ResidentGrassInstanceCount < stable.Sum(tile => tile.InstanceCount), "construction placement did not remove local grass");
            RingStandingGrassLayer.TileAudit unchanged = layer.CaptureTileAuditsForTests().First(tile => tile.Around == unaffected.Around && tile.Across == unaffected.Across);
            Require(unchanged.RenderInstanceId == unaffected.RenderInstanceId, "unrelated resident tile rebuilt for local construction");
            await Settle(layer, sampler, hints, focus, geometry, soil, new RingGrassExclusionSnapshot(2, circumference, Array.Empty<RingGrassExclusionBox>()));
            Require(layer.ResidentGrassInstanceCount == stable.Sum(tile => tile.InstanceCount), "construction removal did not restore the baseline grass count");
        }
        finally { layer.QueueFree(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    }
    private static void VerifyDenseBufferedRoots(IEnumerable<RingGrassTileData> tiles,
        SmoothTerrainVolumetricDensitySnapshot field, RingLodSurfaceEditSnapshot hints, RingTerrainSampler sampler,
        RingLodGeometrySnapshot geometry)
    {
        int checkedRoots = 0;
        foreach (RingGrassTileData tile in tiles)
        {
            double radius = geometry.RingRadiusMeters - geometry.RingVerticalBaseOffsetMeters - tile.AnchorHeight;
            for (int offset = 0; offset < tile.Buffer.Length && checkedRoots < 32; offset += RingGrassTileData.FloatsPerInstance)
            {
                float localAcross = tile.Buffer[offset + 3];
                float localHeight = tile.Buffer[offset + 7];
                float localAround = tile.Buffer[offset + 11];
                double delta = Math.Asin(Math.Clamp(localAround / radius, -1.0f, 1.0f));
                double around = tile.Bounds.AroundCenter + delta * geometry.RingRadiusMeters;
                double across = tile.Bounds.AcrossCenter + localAcross;
                Require(new RingGrassSurfaceResolver(field, hints, sampler).TryResolve(around, across, out RingGrassSurfaceHit hit)
                    && hit.Material == SmoothTerrainMaterial.Soil, "dense buffered root was not on authoritative soil");
                double expectedLocalHeight = hit.Height - tile.AnchorHeight
                    + radius * 2.0 * Math.Sin(delta * .5) * Math.Sin(delta * .5) - .035;
                Require(Math.Abs(localHeight - expectedLocalHeight) < .02, "dense buffered root height diverged from authoritative surface");
                Require(tile.Buffer[offset + 16] is >= 0 and <= 1 && tile.Buffer[offset + 17] is >= 0 and <= 1
                    && tile.Buffer[offset + 18] is >= 0 and < 1 && tile.Buffer[offset + 19] is >= 0 and < 1,
                    "dense custom payload left its normalized canonical range");
                Require(tile.Buffer[offset + 12] is >= 0 and <= 1 && tile.Buffer[offset + 13] is >= 0 and <= 1
                    && tile.Buffer[offset + 14] is >= 0 and <= 1 && tile.Buffer[offset + 15] is >= 0 and <= 1,
                    "dense biome COLOR payload left its normalized range");
                checkedRoots++;
            }
        }
        Require(checkedRoots > 0, "dense buffered-root validation had no roots");
    }

    private async Task VerifyDrawDistanceStreamingClamp(RingTerrainSampler sampler, long circumference)
    {
        // Draw distance is resident-window limited (about 41 m) while the legacy coverage is 200 m; the streaming
        // clamp must keep only what presentation can show.  Variant 0 is untruncated, variant 1 hits the tile cap
        // (with a wider margin) while the cap edge still lies beyond the presentation radius.
        for (int variant = 0; variant < 2; variant++)
        {
            RingCanonicalPosition focus = new(186_000, 0, 141_000);
            RingLodGeometrySnapshot geometry = Geometry(circumference, Vector3.Zero, focus);
            var clean = new RingLodSurfaceEditSnapshot(1, circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
            var layer = new RingStandingGrassLayer
            {
                EnableGrassAppearance = true, EnableCoordinatedGrassHandoff = true, SpatialTileMeters = 16, BladesPerSquareMeter = 1,
                MaximumResidentTiles = variant == 0 ? 200 : 100, MaximumInstancesPerTile = 1000, MaximumInstances = 50_000,
                MaximumTileBuildsInFlight = 4, MaximumTileUploadsPerFrame = 8, EnableHierarchicalCoverage = true, EnableDistanceLod = true,
                LodNearDistanceMeters = 20, LodMidDistanceMeters = 60, LodFarDistanceMeters = 100,
                EnableAdaptiveGrassGrouping = false, VisibilityRadiusMeters = 200, GrassStreamingMarginMeters = variant == 0 ? 16 : 40, ClampGrassStreamingToDrawDistance = true
            };
            AddChild(layer); layer.InitializeForTests();
            try
            {
                await Settle(layer, sampler, clean, focus, geometry);
                float draw = layer.EffectiveGrassDrawDistance(), halfT = layer.LodTransitionWidthMeters * .5f;
                float presentation = draw + halfT + 9f + Mathf.Max(0, layer.GrassFrustumPaddingMeters);
                float coverage = Mathf.Min(Mathf.Max(200f, 100f + halfT), presentation + layer.GrassStreamingMarginMeters);
                Require(draw < 99f && Mathf.IsEqualApprox(layer.GrassStreamingPresentationRadiusMeters, presentation)
                    && Mathf.IsEqualApprox(layer.GrassStreamingCoverageRadiusMeters, coverage),
                    $"streaming clamp coverage {layer.GrassStreamingCoverageRadiusMeters} presentation {layer.GrassStreamingPresentationRadiusMeters} did not match draw {draw}");
                RingStandingGrassLayer.TileAudit[] tiles = layer.CaptureTileAuditsForTests();
                Require(tiles.Length > 0 && layer.ResidentGrassInstanceCount > 0, "clamp fixture installed no grass");
                int beyondPresentation = 0;
                byte expectedTier = presentation <= 20f ? (byte)0 : presentation <= 60f ? (byte)1 : (byte)2;
                foreach (RingStandingGrassLayer.TileAudit tile in tiles)
                {
                    RingGrassTileBounds bounds = RingGrassCoordinates.Bounds(tile.Around, tile.Across, 16, circumference, sampler.RingAcrossWidthMeters);
                    double distance = Math.Sqrt(RingGrassCoordinates.TileDistanceSquared(bounds, focus, circumference));
                    Require(distance <= coverage + 16 * .125 + 1e-3, $"resident tile {distance:F1} m beyond clamped coverage {coverage:F1} m");
                    if (distance > presentation)
                    {
                        beyondPresentation++;
                        Require(layer.ResidentSourceTierForTests(tile.Around, tile.Across) == expectedTier,
                            $"tile at {distance:F1} m beyond presentation radius did not carry the clamped tier {expectedTier}");
                    }
                }
                if (variant == 0)
                {
                    Require(beyondPresentation > 0 && layer.CachedPlanSafeMovementForTests > 0,
                        "untruncated clamp fixture had no tiles beyond the presentation radius");
                    layer.ClampGrassStreamingToDrawDistance = false;
                    layer.StepForTests(sampler, clean, focus, geometry);
                    Require(Mathf.IsEqualApprox(layer.GrassStreamingCoverageRadiusMeters, Mathf.Max(200f, 100f + halfT)),
                        "disabling the streaming clamp did not restore legacy coverage");
                    layer.ClampGrassStreamingToDrawDistance = true;
                    await Settle(layer, sampler, clean, focus, geometry);
                }
                else
                {
                    Require(layer.PlanningTilesInRadiusForTests > layer.MaximumResidentTiles && layer.CachedPlanSafeMovementForTests > .1,
                        $"truncated fixture safe movement {layer.CachedPlanSafeMovementForTests:F2} m too small to reuse the plan");
                }
                long scans = layer.RequestQueueScans;
                RingCanonicalPosition moving = focus;
                for (int step = 0; step < 10; step++)
                {
                    moving = moving with { AroundMeters = moving.AroundMeters + .1 };
                    layer.StepForTests(sampler, clean, moving, geometry);
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }
                Require(layer.RequestQueueScans == scans, $"small movement replanned the grass request queue (variant {variant})");
                GD.Print($"GRASS_STREAMING_CLAMP variant={variant} draw={draw:F1} presentation={presentation:F1} coverage={coverage:F1} tiles={tiles.Length} safe={layer.CachedPlanSafeMovementForTests:F2}");
            }
            finally { layer.QueueFree(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        }
    }
    private async Task VerifyProductionBaselineWorkerLease(RingTerrainSampler sampler, long circumference)
    {
        RingCanonicalPosition focus = new(186_000, 0, 141_000);
        RingLodGeometrySnapshot geometry = Geometry(circumference, Vector3.Zero, focus);
        using var baselineSampler = sampler.CreateIndependentCopy();
        var baseline = new RingTerrainSmoothBaseline(baselineSampler);
        var field = new SmoothTerrainVolumetricDensitySnapshot(baseline,
            new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(), 301, circumference));
        var hints = new RingLodSurfaceEditSnapshot(301, circumference,
            new Dictionary<RingTerrainColumnCoordinate, int>());
        var layer = new RingStandingGrassLayer { MaximumResidentTiles = 4, MaximumInstances = 600, MaximumTileBuildsInFlight = 2 };
        AddChild(layer); layer.InitializeForTests();
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await Settle(layer, sampler, hints, focus, geometry, field);
            await Settle(layer, sampler, hints, focus with { AroundMeters = focus.AroundMeters + 16 }, geometry, field);
            watch.Stop();
            Require(layer.PendingGrassTileBuildCount == 0 && layer.PendingGrassUploadCount == 0,
                "production baseline worker leases did not settle");
            Require(layer.ResidentGrassInstanceCount > 0, "production baseline worker leases produced no grass");
            GD.Print($"GRASS_PRODUCTION_FIELD_WORKER_LEASE=PASS elapsed_ms={watch.Elapsed.TotalMilliseconds:F1} worker_ms={layer.LastWorkerBuildMilliseconds:F1} builds={layer.TotalGrassBuildCount}");
        }
        finally
        {
            layer.QueueFree();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            field.DisposePooledWorkerContexts();
        }
    }
}
