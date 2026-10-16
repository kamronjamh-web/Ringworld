using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tests;

/// <summary>Headless counter test for standing-grass streaming hysteresis. It drives the production scheduler
/// through stationary, nudge, oscillation, rebase, cell-crossing and saturated-cap scenarios and reports the
/// build/eviction/upload counts of each. Run via tools/grass_streaming_hysteresis_test.gd.</summary>
public partial class GrassStreamingHysteresisTest : Node
{
    [Signal] public delegate void CompletedEventHandler(bool passed);

    private const long Circumference = 981_748L * 32L;
    private const int Width = 318_000;
    private readonly List<string> _failures = new();
    private readonly List<string> _report = new();

    public override async void _Ready()
    {
        try { EmitSignal(SignalName.Completed, await RunAsync()); }
        catch (Exception exception) { GD.PushError("GRASS_STREAMING_HYSTERESIS=FAIL " + exception); EmitSignal(SignalName.Completed, false); }
    }

    private async Task<bool> RunAsync()
    {
        int previousMaxFps = Engine.MaxFps; Engine.MaxFps = 0;
        var settings = (VoxelTerrainSettings)(GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres")?.Duplicate(true) ?? throw new InvalidOperationException("terrain settings missing"));
        using var sampler = new RingTerrainSampler(settings, true, Circumference, 5_000_000.0, Width, 8000);
        try
        {
            VerifyFilterEquivalence(sampler);
            // "Defaults": layer defaults with the shipped hierarchical LOD enabled; the resident cap is not reached.
            await RunConfig("defaults", sampler, () => new RingStandingGrassLayer
            {
                MaximumTileBuildsInFlight = 2, MaximumTileUploadsPerFrame = 4, MaximumTileUploadMilliseconds = 20,
            }, quality: (true, 48, 115, 12, 1, .45f), saturated: false);
            // "Maxed": the user's saved extreme preferences scaled to a test-sized world: density 96, 1700 roots per
            // tile, three coverage tiers, and a resident cap far below the planning set so the cap ring is saturated.
            await RunConfig("maxed", sampler, () => new RingStandingGrassLayer
            {
                VisibilityRadiusMeters = 230, BladesPerSquareMeter = 96, MaximumInstancesPerTile = 1700, MaximumInstances = 5_000_000,
                MaximumResidentTiles = 40, MaximumTileBuildsInFlight = 4, MaximumTileUploadsPerFrame = 8, MaximumTileUploadMilliseconds = 20,
                LodMidDistanceMeters = 120, LodMidSourceDensity = .49f, LodFarSourceDensity = .09f,
            }, quality: (true, 60, 220, 10, 1, .45f), saturated: true);
        }
        finally { Engine.MaxFps = previousMaxFps; }
        foreach (string line in _report) GD.Print(line);
        if (_failures.Count > 0) { foreach (string failure in _failures) GD.PushError("GRASS_STREAMING_HYSTERESIS fail: " + failure); GD.Print("GRASS_STREAMING_HYSTERESIS=FAIL " + _failures.Count); return false; }
        GD.Print("GRASS_STREAMING_HYSTERESIS=PASS");
        return true;
    }

    /// <summary>A filtered downgrade must hold exactly the roots (and bytes) a fresh build at the lower fraction produces.</summary>
    private void VerifyFilterEquivalence(RingTerrainSampler sampler)
    {
        var clean = new RingLodSurfaceEditSnapshot(1, Circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        RingCanonicalPosition focus = new(186_000.0 + 24, 0, 141_000.0 + 24);
        RingLodGeometrySnapshot geometry = Geometry(Vector3.Zero, focus);
        foreach (bool dense in new[] { false, true })
        {
            var key = new RingGrassTileKey(186_000 / 48, 141_000 / 48);
            RingGrassTileBounds bounds = RingGrassCoordinates.Bounds(key.Around, key.Across, 48, Circumference, Width);
            float density = dense ? 24 : 0;
            RingGrassTileData full = RingGrassTileBuilder.Build(key, bounds, 8, 9, density, 100_000, 25, sampler, clean, geometry, default, sourceFraction: 1, sourceTier: 0, useGrassClumps: dense);
            foreach ((float fraction, byte tier) in new[] { (.49f, (byte)1), (.09f, (byte)2) })
            {
                RingGrassTileData fresh = RingGrassTileBuilder.Build(key, bounds, 8, 9, density, 100_000, 25, sampler, clean, geometry, default, sourceFraction: fraction, sourceTier: tier, useGrassClumps: dense);
                RingGrassTileData? filtered = RingGrassTileBuilder.FilterToTier(full, fraction, tier);
                Require(filtered != null && full.InstanceCount > 0 && fresh.InstanceCount > 0, $"filter fixture empty (dense={dense})");
                bool same = filtered!.InstanceCount == fresh.InstanceCount && filtered.PlacementHash == fresh.PlacementHash
                    && filtered.SourceTier == tier && filtered.SourceFraction == fraction && filtered.Buffer.AsSpan().SequenceEqual(fresh.Buffer);
                _report.Add($"GRASS_HYST[filter] dense={dense} tier={tier}: full={full.InstanceCount} fresh={fresh.InstanceCount} filtered={filtered.InstanceCount} identical={same}");
                if (!same) _failures.Add($"filter/{dense}/{tier}: filtered tile differs from a fresh build");
            }
        }
    }

    private readonly record struct Snapshot(long Builds, long Uploads, int TierRebuilds, long Evictions, long Readds, long ReaddsIdentical, long TierFilters, int NewTile, int Upgrade, int Other);

    private sealed class Tracker
    {
        public Dictionary<(long, long), ulong> Resident = new();
        public Dictionary<(long, long), ulong> Evicted = new();
        public long Evictions, Readds, ReaddsIdentical;
        public void Observe(RingStandingGrassLayer.TileAudit[] audits)
        {
            var now = new Dictionary<(long, long), ulong>();
            foreach (var a in audits) now[(a.Around, a.Across)] = a.PlacementHash;
            foreach (var kv in Resident) if (!now.ContainsKey(kv.Key)) { Evictions++; Evicted[kv.Key] = kv.Value; }
            foreach (var kv in now)
            {
                if (Resident.ContainsKey(kv.Key)) continue;
                if (Evicted.TryGetValue(kv.Key, out ulong old)) { Readds++; if (old == kv.Value) ReaddsIdentical++; Evicted.Remove(kv.Key); }
            }
            Resident = now;
        }
    }

    private async Task RunConfig(string name, RingTerrainSampler sampler, Func<RingStandingGrassLayer> create,
        (bool Enabled, float Near, float Far, float Transition, float NearDensity, float FarDensity) quality, bool saturated)
    {
        RingStandingGrassLayer layer = create();
        AddChild(layer); layer.InitializeForTests();
        layer.ApplyGrassQuality(quality.Enabled, quality.Near, quality.Far, quality.Transition, quality.NearDensity, quality.FarDensity);
        var clean = new RingLodSurfaceEditSnapshot(1, Circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        RingCanonicalPosition home = new(186_000.0 + 24, 0, 141_000.0 + 24);
        RingLodGeometrySnapshot geometry = Geometry(Vector3.Zero, home);
        var tracker = new Tracker();
        try
        {
            await Drive(layer, sampler, clean, home, geometry, tracker, 6000, untilIdle: true);
            Require(layer.ResidentGrassTileCount > 0, name + ": baseline installed nothing");
            _report.Add($"GRASS_HYST[{name}] settled tiles={layer.ResidentGrassTileCount} instances={layer.ResidentGrassInstanceCount} planned={layer.PlanningTilesInRadiusForTests} builds={layer.TotalGrassBuildCount}");
            tracker.Evictions = tracker.Readds = tracker.ReaddsIdentical = 0;

            // 1. Stationary for 3 minutes of 60 fps frames.
            Snapshot a = Capture(layer, tracker);
            await Drive(layer, sampler, clean, home, geometry, tracker, 10800, untilIdle: false);
            Report(name, "stationary_3min", a, Capture(layer, tracker), maxBuilds: 0, maxEvictions: 0);

            // 2. A 0.5 m nudge: at most one cycle of work, then a stationary window with zero.
            RingCanonicalPosition nudged = home with { AroundMeters = home.AroundMeters + .5 };
            a = Capture(layer, tracker);
            await Drive(layer, sampler, clean, nudged, geometry, tracker, 60, untilIdle: true);
            Snapshot nudge = Capture(layer, tracker);
            Report(name, "nudge_0.5m", a, nudge, maxBuilds: 2, maxEvictions: 2);
            await Drive(layer, sampler, clean, nudged, geometry, tracker, 600, untilIdle: false);
            Report(name, "nudge_then_stationary", nudge, Capture(layer, tracker), maxBuilds: 0, maxEvictions: 0);

            // 3. 20 oscillations of +/-0.75 m. The first cycle may do work; later cycles must not.
            a = Capture(layer, tracker);
            for (int i = 0; i < 2; i++)
            {
                RingCanonicalPosition p = home with { AroundMeters = home.AroundMeters + (i % 2 == 0 ? .75 : -.75) };
                await Drive(layer, sampler, clean, p, geometry, tracker, 20, untilIdle: true);
            }
            Snapshot firstCycle = Capture(layer, tracker);
            Report(name, "oscillate_first_cycle", a, firstCycle, maxBuilds: 6, maxEvictions: 6);
            for (int i = 2; i < 40; i++)
            {
                RingCanonicalPosition p = home with { AroundMeters = home.AroundMeters + (i % 2 == 0 ? .75 : -.75) };
                await Drive(layer, sampler, clean, p, geometry, tracker, 20, untilIdle: true);
            }
            Snapshot osc = Capture(layer, tracker);
            Report(name, "oscillate_later_cycles", firstCycle, osc, maxBuilds: 0, maxEvictions: 0);
            Require(osc.ReaddsIdentical - a.ReaddsIdentical == 0, name + ": oscillation re-added identical tiles (" + (osc.ReaddsIdentical - a.ReaddsIdentical) + ")");

            // 4. Floating-origin rebase alone: a pure transform change.
            Snapshot beforeRebase = Capture(layer, tracker);
            RingLodGeometrySnapshot rebased = Geometry(new Vector3(37, 0, -19), home);
            await Drive(layer, sampler, clean, home, rebased, tracker, 120, untilIdle: false);
            Report(name, "rebase", beforeRebase, Capture(layer, tracker), maxBuilds: 0, maxEvictions: 0, maxUploads: 0);
            geometry = rebased;

            // 5. Crossing 60 m: bounded by the swept perimeter, then zero.
            RingCanonicalPosition crossed = home with { AroundMeters = home.AroundMeters + 60 };
            Snapshot beforeCross = Capture(layer, tracker);
            await Drive(layer, sampler, clean, crossed, geometry, tracker, 60, untilIdle: true);
            Snapshot cross = Capture(layer, tracker);
            float tile = layer.SpatialTileMeters;
            float radius = layer.GrassStreamingCoverageRadiusMeters;
            double swept = 2.0 * radius * 60.0 / (tile * tile);
            // Each of the three tier boundaries and the outer ring is swept; tier upgrades re-build, downgrades must not.
            double bound = Math.Ceiling(swept * (saturated ? 2.0 : 1.5)) + 3 * Math.Ceiling(2.0 * Math.Min(radius, quality.Far) * 60.0 / (tile * tile)) + 8;
            Report(name, "cross_60m", beforeCross, cross, maxBuilds: (long)bound, maxEvictions: (long)bound);
            await Drive(layer, sampler, clean, crossed, geometry, tracker, 600, untilIdle: false);
            Report(name, "cross_then_stationary", cross, Capture(layer, tracker), maxBuilds: 0, maxEvictions: 0);

            // 6. Saturated cap (or ordinary ring) + sub-tile oscillation after the cross: no flapping.
            Snapshot beforeFlap = Capture(layer, tracker);
            for (int i = 0; i < 30; i++)
            {
                RingCanonicalPosition p = crossed with { AroundMeters = crossed.AroundMeters + (i % 2 == 0 ? .75 : -.75), AcrossMeters = crossed.AcrossMeters + (i % 4 < 2 ? .4 : -.4) };
                await Drive(layer, sampler, clean, p, geometry, tracker, 20, untilIdle: true);
            }
            Snapshot flap = Capture(layer, tracker);
            Report(name, "oscillate_after_cross", beforeFlap, flap, maxBuilds: 4, maxEvictions: 4);
        }
        finally { layer.QueueFree(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    }

    private async Task Drive(RingStandingGrassLayer layer, RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits,
        RingCanonicalPosition focus, RingLodGeometrySnapshot geometry, Tracker tracker, int frames, bool untilIdle)
    {
        ulong deadline = Time.GetTicksMsec() + 240_000;
        for (int i = 0; ; i++)
        {
            layer.StepForTests(sampler, edits, focus, geometry);
            tracker.Observe(layer.CaptureTileAuditsForTests());
            if (untilIdle ? i >= frames - 1 && layer.PendingGrassTileBuildCount == 0 && layer.PendingGrassUploadCount == 0 : i >= frames - 1) break;
            if (Time.GetTicksMsec() > deadline) throw new TimeoutException("grass streaming did not settle");
            if (!untilIdle && i % 64 != 0) continue;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static Snapshot Capture(RingStandingGrassLayer layer, Tracker tracker)
        => new(layer.TotalGrassBuildCount, layer.TotalGrassUploadCount, layer.GrassTierRebuildCount, tracker.Evictions, tracker.Readds, tracker.ReaddsIdentical, layer.GrassTierFilterCount, layer.GrassBuildNewTileCount, layer.GrassBuildTierUpgradeCount, layer.GrassBuildTierOtherCount);

    private void Report(string config, string scenario, Snapshot before, Snapshot after, long maxBuilds, long maxEvictions, long maxUploads = long.MaxValue)
    {
        long builds = after.Builds - before.Builds, uploads = after.Uploads - before.Uploads, evictions = after.Evictions - before.Evictions;
        long readds = after.Readds - before.Readds, identical = after.ReaddsIdentical - before.ReaddsIdentical;
        int tier = after.TierRebuilds - before.TierRebuilds;
        _report.Add($"GRASS_HYST[{config}] {scenario}: builds={builds} reasons[new={after.NewTile - before.NewTile} upgrade={after.Upgrade - before.Upgrade} other={after.Other - before.Other} filtered={after.TierFilters - before.TierFilters}] tier_rebuilds={tier} uploads={uploads} evictions={evictions} readds={readds} identical_readds={identical}");
        if (builds > maxBuilds) _failures.Add($"{config}/{scenario}: builds {builds} > {maxBuilds}");
        if (evictions > maxEvictions) _failures.Add($"{config}/{scenario}: evictions {evictions} > {maxEvictions}");
        if (uploads > maxUploads) _failures.Add($"{config}/{scenario}: uploads {uploads} > {maxUploads}");
        if (maxBuilds == 0 && identical > 0) _failures.Add($"{config}/{scenario}: identical re-adds {identical}");
    }

    private static RingLodGeometrySnapshot Geometry(Vector3 origin, RingCanonicalPosition anchor)
        => new(Transform3D.Identity, origin, Vector3.Zero, Circumference / Math.Tau, 0, 0, Circumference, -1000, 1000, anchor); // origin = FloatingOriginWorldOffset

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
