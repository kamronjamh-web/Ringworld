using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Reflection;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

/// <summary>
/// Summarises one grass motion capture phase from the live performance monitor (tools/grass_motion_capture.gd).
/// Read-only: it never changes settings, history or preferences.
/// </summary>
public partial class GrassMotionCaptureProbe : RefCounted
{
    private long _allocatedAtStart;
    private int _gen0AtStart, _gen1AtStart, _gen2AtStart;

    public void MarkPhaseStart()
    {
        _allocatedAtStart = GC.GetTotalAllocatedBytes(false);
        _gen0AtStart = GC.CollectionCount(0);
        _gen1AtStart = GC.CollectionCount(1);
        _gen2AtStart = GC.CollectionCount(2);
    }

    /// <summary>Canonical position of the explorer (its struct is not Variant-compatible).</summary>
    public Godot.Collections.Dictionary ExplorerState(Node explorerNode)
    {
        var explorer = (RingworldTechDemo.Voxel.Integration.RingVoxelExplorerController)explorerNode;
        var position = explorer.CanonicalPosition;
        return new Godot.Collections.Dictionary
        {
            ["around"] = position.AroundMeters, ["across"] = position.AcrossMeters, ["vertical"] = position.VerticalMeters,
            ["speed"] = explorer.ActualTravelSpeedMetersPerSecond
        };
    }

    /// <summary>Cumulative GPU grass counters that are not Godot-visible (internal test accessors).</summary>
    public Godot.Collections.Dictionary GrassGpuCounters(Node grassNode)
    {
        var grass = (RingworldTechDemo.Voxel.LOD.RingStandingGrassLayer)grassNode;
        return new Godot.Collections.Dictionary
        {
            ["GpuDispatches"] = grass.GpuDispatchCountForTests,
            ["GpuParameterUploads"] = grass.GpuParameterUploadsForTests,
            ["GrassCullingRelatchesProbe"] = grass.GrassCullingRelatches
        };
    }

    /// <summary>Returns a JSON object: frame percentiles, per-frame scope costs, GC deltas.</summary>
    public string Summarize(Node monitorNode, double collectionSeconds)
    {
        var monitor = (RingPerformanceMonitor)monitorNode;
        RingPerformanceSnapshot snapshot = monitor.GetSnapshot();
        IReadOnlyList<RingPerformanceFrame> frames = snapshot.History;
        int frameCount = frames.Count;
        object Stats(Func<RingPerformanceFrame, double?> select)
        {
            var values = new List<double>(frameCount);
            foreach (RingPerformanceFrame frame in frames)
            {
                double? value = select(frame);
                if (value is double v && double.IsFinite(v)) values.Add(v);
            }
            if (values.Count == 0) return new { count = 0 };
            values.Sort();
            double Pick(double p) => values[(int)Math.Clamp(Math.Ceiling(values.Count * p) - 1, 0, values.Count - 1)];
            return new { count = values.Count, mean = values.Average(), p50 = Pick(.5), p95 = Pick(.95), p99 = Pick(.99), max = values[^1] };
        }
        double window = frameCount > 1 ? frames[^1].Seconds - frames[0].Seconds : collectionSeconds;
        double perFrameDivisor = Math.Max(1, frameCount);
        var rows = snapshot.Rows
            .OrderByDescending(row => row.Gpu ? 0 : row.MeanMilliseconds * row.Count)
            .Select(row => new
            {
                name = row.Name, worker = row.Worker, gpu = row.Gpu, count = row.Count,
                meanMs = row.MeanMilliseconds, p95Ms = row.P95Milliseconds, maxMs = row.MaxMilliseconds,
                // Main-thread cost per rendered frame in the window (calls x mean / frames); not meaningful for worker rows.
                perFrameMs = row.MeanMilliseconds * row.Count / perFrameDivisor,
                callsPerFrame = row.Count / perFrameDivisor
            }).ToArray();
        return JsonSerializer.Serialize(new
        {
            frames = frameCount,
            windowSeconds = window,
            fps = window > 0 ? frameCount / window : 0,
            frameMs = Stats(f => f.FrameMilliseconds),
            processMs = Stats(f => f.ProcessMilliseconds),
            physicsMs = Stats(f => f.PhysicsMilliseconds),
            renderSetupMs = Stats(f => f.SetupMilliseconds),
            mainViewportCpuMs = Stats(f => f.ViewportCpuMilliseconds),
            mainViewportGpuMs = Stats(f => f.ViewportGpuMilliseconds),
            outerFarViewportCpuMs = Stats(f => f.FarViewportCpuMilliseconds),
            outerFarViewportGpuMs = Stats(f => f.FarViewportGpuMilliseconds),
            regionalFarViewportCpuMs = Stats(f => f.RegionalFarViewportCpuMilliseconds),
            regionalFarViewportGpuMs = Stats(f => f.RegionalFarViewportGpuMilliseconds),
            drawCalls = Stats(f => f.DrawCalls >= 0 ? f.DrawCalls : null),
            primitives = Stats(f => f.RenderPrimitives),
            speedMps = Stats(f => f.SpeedMetersPerSecond),
            gc = new
            {
                allocatedBytes = GC.GetTotalAllocatedBytes(false) - _allocatedAtStart,
                gen0 = GC.CollectionCount(0) - _gen0AtStart,
                gen1 = GC.CollectionCount(1) - _gen1AtStart,
                gen2 = GC.CollectionCount(2) - _gen2AtStart
            },
            droppedSamples = snapshot.DroppedSamples,
            rows
        });
    }

    private static readonly PropertyInfo AltitudeProperty = typeof(RingVoxelExplorerController)
        .GetProperty(nameof(RingVoxelExplorerController.CurrentAltitudeAboveTerrainMeters))!;

    /// <summary>Refreshes the explorer's cached AGL (normally physics-updated) so altitude-gated layers see the new pose.</summary>
    public float RefreshAltitude(Node explorerNode)
    {
        var explorer = (RingVoxelExplorerController)explorerNode;
        float computed = explorer.GetCanonicalAtmosphereHeightMeters();
        if (float.IsFinite(computed)) AltitudeProperty.GetSetMethod(nonPublic: true)!.Invoke(explorer, new object[] { computed });
        return explorer.CurrentAltitudeAboveTerrainMeters;
    }

    /// <summary>Places the explorer <paramref name="clearance"/> m above the deterministic surface at a canonical (around, across).</summary>
    public Godot.Collections.Dictionary PlaceExplorer(Node explorerNode, Node gridNode, double around, double across, double clearance, float yawDegrees, float pitchDegrees)
    {
        var explorer = (RingVoxelExplorerController)explorerNode;
        var grid = (VoxelChunkGridManager)gridNode;
        var sample = grid.TerrainSampler!.GetTerrainSample((long)Math.Round(around), (int)Math.Round(across));
        double vertical = sample.SurfaceHeight + clearance;
        explorer.SetCanonicalSpawnMeters(around, vertical, across);
        explorer.RestoreSavedState(new RingCanonicalPosition(around, vertical, across), Vector3.Zero, Mathf.DegToRad(yawDegrees), pitchDegrees, false, 0);
        explorer.SetPhysicsProcess(false);
        float altitude = RefreshAltitude(explorer);
        return new Godot.Collections.Dictionary { ["around"] = around, ["across"] = across, ["vertical"] = vertical, ["surface"] = sample.SurfaceHeight, ["water"] = sample.HasSurfaceWater, ["altitude"] = altitude };
    }

    /// <summary>Restores an exact canonical pose (used to reset between phases at a custom pose).</summary>
    public void RestoreExplorer(Node explorerNode, double around, double vertical, double across, float yawDegrees, float pitchDegrees)
    {
        var explorer = (RingVoxelExplorerController)explorerNode;
        explorer.RestoreSavedState(new RingCanonicalPosition(around, vertical, across), Vector3.Zero, Mathf.DegToRad(yawDegrees), pitchDegrees, false, 0);
        RefreshAltitude(explorer);
    }

    private static ForestRegionSampler CreateForestSampler(VoxelChunkGridManager grid)
        => new(grid.TerrainSampler!.Seed, (long)Math.Round(grid.GetCanonicalRingCircumferenceMeters64()), Mathf.RoundToInt(grid.RingAcrossWidthMeters));

    private static ForestRegionSample SampleForest(ForestRegionSampler forest, VoxelChunkGridManager grid, long around, int across, out bool water, out float slope)
    {
        var terrain = grid.TerrainSampler!;
        var sample = terrain.GetTerrainSample(around, across);
        int plus = terrain.GetTerrainSample(around + 8, across).SurfaceHeight, minus = terrain.GetTerrainSample(around - 8, across).SurfaceHeight;
        int sidePlus = terrain.GetTerrainSample(around, across + 8).SurfaceHeight, sideMinus = terrain.GetTerrainSample(around, across - 8).SurfaceHeight;
        slope = Mathf.RadToDeg(Mathf.Atan(Mathf.Max(Math.Abs(plus - minus), Math.Abs(sidePlus - sideMinus)) / 16f));
        water = sample.HasSurfaceWater;
        return forest.Sample(around, across, sample, slope);
    }

    /// <summary>Forest-region sample (habitat, density) at a canonical point.</summary>
    public Godot.Collections.Dictionary ForestAt(Node gridNode, double around, double across)
    {
        var grid = (VoxelChunkGridManager)gridNode;
        var sample = SampleForest(CreateForestSampler(grid), grid, (long)Math.Round(around), (int)Math.Round(across), out bool water, out float slope);
        return new Godot.Collections.Dictionary { ["habitat"] = sample.Habitat.ToString(), ["density"] = sample.Density, ["water"] = water, ["slope"] = slope };
    }

    /// <summary>Top dense-forest points in a window, scored by mean forest density over a 96 m neighbourhood (walkable, dry).</summary>
    public Godot.Collections.Array RankForestCandidates(Node gridNode, double centerAround, double centerAcross, int extent, int step, int count, int minimumSpacing)
    {
        var grid = (VoxelChunkGridManager)gridNode;
        var forest = CreateForestSampler(grid);
        var scored = new List<(double score, long around, int across)>();
        for (long around = (long)centerAround - extent; around <= (long)centerAround + extent; around += step)
        for (int across = (int)centerAcross - extent; across <= (int)centerAcross + extent; across += step)
        {
            var center = SampleForest(forest, grid, around, across, out bool water, out float slope);
            if (water || slope > 18 || center.Habitat != ForestHabitat.DenseForest) continue;
            double score = 0;
            for (int da = -48; da <= 48; da += 24)
            for (int dc = -48; dc <= 48; dc += 24)
            {
                var neighbor = SampleForest(forest, grid, around + da, across + dc, out bool wet, out _);
                if (!wet && neighbor.Habitat is ForestHabitat.DenseForest or ForestHabitat.OpenWoodland) score += neighbor.Density;
            }
            scored.Add((score / 25.0, around, across));
        }
        var result = new Godot.Collections.Array();
        foreach (var item in scored.OrderByDescending(x => x.score))
        {
            bool near = false;
            foreach (Godot.Collections.Dictionary chosen in result)
                if (Math.Abs((long)chosen["around"] - item.around) < minimumSpacing && Math.Abs((int)chosen["across"] - item.across) < minimumSpacing) { near = true; break; }
            if (near) continue;
            result.Add(new Godot.Collections.Dictionary { ["around"] = item.around, ["across"] = item.across, ["score"] = item.score });
            if (result.Count >= count) break;
        }
        return result;
    }

    /// <summary>Tree population and cumulative GPU tree culling dispatches.</summary>
    public Godot.Collections.Dictionary TreeState(Node decorationsNode)
    {
        var trees = (RingBiomeDecorationLayer)decorationsNode;
        var result = trees.GetImportedForestRuntimeDiagnostics();
        result["gpu_tree_dispatches"] = trees.GpuTreeBenchmarkDispatchCount;
        result["vegetation_plans"] = trees.VegetationPlanCount;
        result["collision_radius_m"] = trees.ImportedForestCollisionRadiusMeters;
        return result;
    }
}
