using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tests;

/// <summary>Production scheduler regression test for localized grass modifier invalidation.</summary>
public partial class GrassModifierStreamingTest : Node
{
    [Signal] public delegate void CompletedEventHandler(bool passed);

    public override async void _Ready()
    {
        try { EmitSignal(SignalName.Completed, await RunAsync()); }
        catch (Exception exception) { GD.PushError("GRASS_MODIFIER_STREAMING=FAIL " + exception); EmitSignal(SignalName.Completed, false); }
    }

    private async Task<bool> RunAsync()
    {
        const long circumference = 981_748L * 32L;
        const int width = 318_000;
        var settings = (VoxelTerrainSettings)(GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres")?.Duplicate(true)
            ?? throw new InvalidOperationException("terrain settings missing"));
        using var sampler = new RingTerrainSampler(settings, true, circumference, 5_000_000.0, width, 8000);
        var layer = new RingStandingGrassLayer
        {
            VisibilityRadiusMeters = 72, SpatialTileMeters = 48, MaximumResidentTiles = 24,
            MaximumInstances = 12_000, MaximumTileBuildsInFlight = 2, MaximumTileUploadsPerFrame = 2,
            SuspendStreamingAboveSpeedMetersPerSecond = 100, EnableGrassModifiers = true,
            GrassModifierDensityStrength = 1
        };
        AddChild(layer);
        layer.InitializeForTests();
        RingCanonicalPosition focus = new(186_000, 0, 141_000);
        RingLodGeometrySnapshot geometry = Geometry(circumference, focus);
        var edits = new RingLodSurfaceEditSnapshot(1, circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        try
        {
            await Settle(layer, sampler, edits, focus, geometry);
            VerifyPresentationCache(layer, geometry, circumference);
            layer.StepForTests(sampler, edits, focus, geometry);
            long queueScans = layer.RequestQueueScans;
            for (int frame = 0; frame < 10; frame++) layer.StepForTests(sampler, edits, focus, geometry);
            Require(layer.RequestQueueScans == queueScans, "settled stationary field repeated full request scans");
            layer.StepForTests(sampler, edits, focus, geometry, altitudeAboveTerrain: layer.MaximumViewerAltitudeAboveTerrainMeters + 1);
            layer.StepForTests(sampler, edits, focus, geometry);
            Require(layer.RequestQueueScans > queueScans, "resuming streaming failed to refresh request eligibility");
            await Settle(layer, sampler, edits, focus, geometry);
            RingStandingGrassLayer.TileAudit[] baseline = layer.CaptureTileAuditsForTests();
            Require(baseline.Length >= 3, "baseline did not retain enough tiles");
            RingStandingGrassLayer.TileAudit target = baseline.FirstOrDefault(t => t.InstanceCount > 0);
            Require(target.InstanceCount > 0, "baseline has no populated target tile");
            RingStandingGrassLayer.TileAudit neighbor = baseline
                .Where(t => t.InstanceCount > 0 && t.Around != target.Around && t.Across != target.Across)
                .OrderByDescending(t => DistanceSquared(t, target)).FirstOrDefault();
            Require(neighbor.InstanceCount > 0, "baseline has no populated unaffected neighbor");

            RingGrassModifierZone zone = ZoneAt(target, density: 0);
            long before = layer.TotalGrassBuildCount;
            layer.SetGrassModifier("test", zone);
            await DiscoverAndSettle(layer, sampler, edits, focus, geometry);
            RingStandingGrassLayer.TileAudit affected = Find(layer, target);
            RingStandingGrassLayer.TileAudit unchanged = Find(layer, neighbor);
            Require(layer.TotalGrassBuildCount > before && affected.PlacementHash != target.PlacementHash,
                "density modifier did not rebuild and alter its target tile");
            Require(unchanged.PlacementHash == neighbor.PlacementHash && unchanged.RenderInstanceId == neighbor.RenderInstanceId,
                "localized modifier rebuilt an unchanged neighbor");

            RingGrassModifierZone fullClear = new(target.Around * 48.0 + 24, target.Across * 48.0 + 24,
                new Vector2(40, 40), 0, 0, Colors.White, 0);
            layer.SetGrassModifier("test", fullClear);
            await DiscoverAndSettle(layer, sampler, edits, focus, geometry);
            Require(Find(layer, target).InstanceCount == 0, "full zero-density modifier did not clear every target root");
            layer.SetGrassModifier("test", zone);
            await DiscoverAndSettle(layer, sampler, edits, focus, geometry);
            Require(Find(layer, target).PlacementHash == affected.PlacementHash,
                "restoring localized modifier did not reproduce its placement hash");

            long noOpBuilds = layer.TotalGrassBuildCount;
            layer.SetGrassModifier("test", zone);
            await DiscoverAndSettle(layer, sampler, edits, focus, geometry);
            Require(layer.TotalGrassBuildCount == noOpBuilds, "identical modifier upsert scheduled redundant work");

            layer.GrassModifierDensityStrength = 0;
            await DiscoverAndSettle(layer, sampler, edits, focus, geometry);
            Require(Find(layer, target).PlacementHash == target.PlacementHash, "zero density strength did not restore baseline hash");

            layer.GrassModifierDensityStrength = 1;
            await DiscoverAndSettle(layer, sampler, edits, focus, geometry);
            Require(Find(layer, target).PlacementHash == affected.PlacementHash, "restored density strength did not reproduce modifier hash");

            layer.EnableGrassModifiers = false;
            await DiscoverAndSettle(layer, sampler, edits, focus, geometry);
            Require(Find(layer, target).PlacementHash == target.PlacementHash, "disabling modifiers did not restore baseline hash");
            layer.EnableGrassModifiers = true;
            await DiscoverAndSettle(layer, sampler, edits, focus, geometry);

            RingGrassModifierZone moved = ZoneAt(neighbor, density: 0);
            layer.SetGrassModifier("test", moved);
            await DiscoverAndSettle(layer, sampler, edits, focus, geometry);
            Require(Find(layer, target).PlacementHash == target.PlacementHash, "moving modifier did not restore old tile");
            Require(Find(layer, neighbor).PlacementHash != neighbor.PlacementHash, "moving modifier did not alter new tile");

            Require(layer.RemoveGrassModifier("test"), "modifier removal rejected known id");
            await DiscoverAndSettle(layer, sampler, edits, focus, geometry);
            Require(Find(layer, neighbor).PlacementHash == neighbor.PlacementHash, "removing modifier did not restore moved tile");
            Require(!layer.RemoveGrassModifier("test"), "repeated modifier removal was not a no-op");

            layer.GrassModifierPreviewRadiusMeters = 10;
            layer.GrassModifierPreviewFeatherMeters = 0;
            layer.GrassModifierPreviewDensity = 0;
            layer.SetGrassModifierPreviewForTests(target.Around * 48.0 + 24, target.Across * 48.0 + 24);
            await DiscoverAndSettle(layer, sampler, edits, focus, geometry);
            Require(Find(layer, target).PlacementHash != target.PlacementHash, "anchored preview did not alter its tile");
            layer.GrassModifierPreviewRadiusMeters = 14;
            await DiscoverAndSettle(layer, sampler, edits, focus, geometry);
            Require(Find(layer, target).PlacementHash != target.PlacementHash, "preview property edit removed anchored zone");
            layer.SetGrassModifierPreviewForTests(neighbor.Around * 48.0 + 24, neighbor.Across * 48.0 + 24);
            await DiscoverAndSettle(layer, sampler, edits, focus, geometry);
            Require(Find(layer, target).PlacementHash == target.PlacementHash, "moving preview did not restore old tile");
            Require(Find(layer, neighbor).PlacementHash != neighbor.PlacementHash, "moving preview did not alter new tile");
            layer.GrassModifierPreviewEnabled = false;
            await DiscoverAndSettle(layer, sampler, edits, focus, geometry);
            Require(Find(layer, neighbor).PlacementHash == neighbor.PlacementHash, "disabling preview did not restore moved tile");

            GD.Print($"GRASS_MODIFIER_STREAMING=PASS tiles={layer.ResidentGrassTileCount} builds={layer.TotalGrassBuildCount}");
            return true;
        }
        finally
        {
            layer.QueueFree();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private async Task DiscoverAndSettle(RingStandingGrassLayer layer, RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits,
        RingCanonicalPosition focus, RingLodGeometrySnapshot geometry)
    {
        await ToSignal(GetTree().CreateTimer(.3), SceneTreeTimer.SignalName.Timeout);
        await Settle(layer, sampler, edits, focus, geometry);
    }

    private async Task Settle(RingStandingGrassLayer layer, RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits,
        RingCanonicalPosition focus, RingLodGeometrySnapshot geometry)
    {
        ulong deadline = Time.GetTicksMsec() + 8000;
        while (Time.GetTicksMsec() < deadline)
        {
            layer.StepForTests(sampler, edits, focus, geometry);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (layer.PendingGrassTileBuildCount == 0 && layer.PendingGrassUploadCount == 0) return;
        }
        throw new TimeoutException("grass modifier streaming did not settle");
    }

    private static RingGrassModifierZone ZoneAt(RingStandingGrassLayer.TileAudit tile, float density) =>
        new(tile.Around * 48.0 + 24, tile.Across * 48.0 + 24, new Vector2(10, 10), 0, density, Colors.White, 0);

    private static RingStandingGrassLayer.TileAudit Find(RingStandingGrassLayer layer, RingStandingGrassLayer.TileAudit expected)
    {
        foreach (RingStandingGrassLayer.TileAudit current in layer.CaptureTileAuditsForTests())
            if (current.Around == expected.Around && current.Across == expected.Across) return current;
        throw new InvalidOperationException("resident tile was unexpectedly evicted");
    }

    private static double DistanceSquared(RingStandingGrassLayer.TileAudit a, RingStandingGrassLayer.TileAudit b)
    {
        double around = a.Around - b.Around, across = a.Across - b.Across;
        return around * around + across * across;
    }

    private static RingLodGeometrySnapshot Geometry(long circumference, RingCanonicalPosition anchor) =>
        new(Transform3D.Identity, Vector3.Zero, Vector3.Zero, circumference / Math.Tau, 0, 0, circumference, -1000, 1000, anchor);

    private static void VerifyPresentationCache(RingStandingGrassLayer layer, RingLodGeometrySnapshot geometry, long circumference)
    {
        RingGrassTileData data = layer.CaptureTileDataForTests().First(t => t.InstanceCount > 0);
        MultiMeshInstance3D source = layer.GetChildren().OfType<MultiMeshInstance3D>().First();
        var near = new GrassPresentationRenderer { Multimesh = source.Multimesh, MaterialOverride = source.MaterialOverride };
        var intermediate = new GrassPresentationRenderer { Multimesh = source.Multimesh, MaterialOverride = source.MaterialOverride };
        layer.AddChild(near); layer.AddChild(intermediate);
        float originalGustScale = layer.GrassGustScaleMeters;
        try
        {
            var cache = new RingStandingGrassLayer.TilePresentationCache();
            Transform3D inverse = layer.GlobalTransform.AffineInverse();
            long builds = layer.PresentationMappingBuilds;
            layer.RefreshTilePresentation(cache, near, data, geometry, inverse, false);
            layer.RefreshTilePresentation(cache, intermediate, data, geometry, inverse, true);
            Require(layer.PresentationMappingBuilds == builds + 1, "near/intermediate did not share the ground mapping");
            long writes = layer.PresentationParameterWrites;
            long nativeReads = layer.PresentationWorldSnapshotReads;
            layer.RefreshTilePresentation(cache, near, data, geometry, inverse, false);
            layer.RefreshTilePresentation(cache, intermediate, data, geometry, inverse, true);
            Require(layer.PresentationParameterWrites == writes, "stationary presentation repeated shader writes");
            Require(layer.PresentationWorldSnapshotReads == nativeReads, "stationary mapping repeated native world reads");
            layer.SetGrassGustWeatherForTests(new Vector2(8, 3), 1, circumference);
            layer.SetGrassGustWeatherForTests(new Vector2(8, 3), 1.1, circumference);
            layer.RefreshTilePresentation(cache, near, data, geometry, inverse, false);
            Require(layer.PresentationParameterWrites == writes + 1, "wind did not update only the dynamic gust UV");
            layer.GrassGustScaleMeters *= 1.5f;
            layer.RefreshTilePresentation(cache, near, data, geometry, inverse, false);
            Require(layer.PresentationParameterWrites > writes + 1, "gust scale change did not refresh mapping");
            var moved = geometry with { GridGlobalTransform = new Transform3D(
                new Basis(Vector3.Up, .4f).Scaled(new Vector3(1.2f, .8f, 1.1f)), new Vector3(10, 2, -4)) };
            layer.RefreshTilePresentation(cache, near, data, moved, inverse, false);
            Require(layer.PresentationMappingBuilds == builds + 2, "geometry change reused stale mapping");
            // Compare against the uncached production calculation in the same world frame.
            intermediate.Transform = near.Transform;
            layer.ConfigureGrassModifierRenderer(intermediate, data, moved);
            foreach (string name in new[] { "grass_modifier_origin_world", "grass_modifier_around_world", "grass_modifier_across_world" })
            {
                Require(near.GetInstanceShaderParameter(name).Equals(intermediate.GetInstanceShaderParameter(name)), "cached mapping differs for " + name);
            }
            var replacementData = new RingGrassTileData(data.Key, data.Bounds, data.AnchorHeight + 2,
                data.Buffer, data.LocalAabb, data.PlacementHash, data.SourceFraction, data.SourceTier, data.BladesPerSource, data.ModifierTintBytes);
            long beforeReplacement = layer.PresentationWorldSnapshotReads;
            layer.RefreshTilePresentation(cache, near, replacementData, moved, inverse, false);
            Require(layer.PresentationWorldSnapshotReads > beforeReplacement && cache.Data == replacementData,
                "data replacement retained old world snapshot");
            var replacementRenderer = new GrassPresentationRenderer { Multimesh = source.Multimesh, MaterialOverride = source.MaterialOverride };
            layer.AddChild(replacementRenderer);
            try
            {
                beforeReplacement = layer.PresentationWorldSnapshotReads;
                layer.RefreshTilePresentation(cache, replacementRenderer, replacementData, moved, inverse, false);
                Require(layer.PresentationWorldSnapshotReads > beforeReplacement
                    && cache.Near.WorldSnapshot == replacementRenderer.GlobalTransform,
                    "renderer replacement retained old native snapshot");
            }
            finally { replacementRenderer.QueueFree(); }
            GD.Print("GRASS_PRESENTATION_CACHE=PASS shared mapping, unchanged reads/writes, wind, tuning, transformed geometry, data/renderer replacement");
        }
        finally { layer.GrassGustScaleMeters = originalGustScale; near.QueueFree(); intermediate.QueueFree(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
