using System;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Diagnostics;

/// <summary>Canonical placement and deterministic movement bridge for the opt-in tree CPU profile.</summary>
public partial class TreeCpuProfileDriver : Node
{
    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private SmoothTerrainGrid? _smooth;
    private RingCanonicalPosition _forestFocus;
    private RingCanonicalPosition _routeOrigin;
    private readonly RingPerformanceTelemetry.Completed[] _drain = new RingPerformanceTelemetry.Completed[1024];
    private readonly Godot.Collections.Array<Godot.Collections.Dictionary> _timings = new();
    private int _dropped;

    public bool Prepare(Node game)
    {
        _explorer = game.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        _grid = game.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager");
        _smooth = game.GetNodeOrNull<SmoothTerrainGrid>("SmoothTerrainGrid");
        if (_explorer is null || _grid?.TerrainSampler is null || FindCamera(_explorer) is not Camera3D camera) return false;
        camera.MakeCurrent();
        _explorer.SetPresentationView(0, -10);
        return true;
    }

    /// <summary>Searches deterministic samples around the certified land focus for dry, forest-weighted terrain.</summary>
    public Godot.Collections.Dictionary FindAndPlaceForestNearLandFocus()
    {
        if (_explorer is null || _grid?.TerrainSampler is null) return new Godot.Collections.Dictionary { ["valid"] = false };
        const long anchorAround = 186_000;
        const int anchorAcross = 141_000;
        const int extent = 12_000;
        const int step = 128;
        float bestScore = float.NegativeInfinity;
        long bestAround = anchorAround;
        int bestAcross = anchorAcross;
        for (long around = anchorAround - extent; around <= anchorAround + extent; around += step)
        for (int across = anchorAcross - extent; across <= anchorAcross + extent; across += step)
        {
            var sample = _grid.TerrainSampler.GetTerrainSample(around, across);
            if (sample.HasSurfaceWater) continue;
            float forest = _grid.TerrainSampler.GetBiomeTextureWeights(sample).Forest;
            // A small preference for a broad patch makes the fixed walking path reproducible.
            float score = forest;
            for (long da = -32; da <= 32; da += 32)
            for (int dz = -32; dz <= 32; dz += 32)
            {
                var neighbor = _grid.TerrainSampler.GetTerrainSample(around + da, across + dz);
                if (!neighbor.HasSurfaceWater) score += _grid.TerrainSampler.GetBiomeTextureWeights(neighbor).Forest * .1f;
            }
            if (score > bestScore) { bestScore = score; bestAround = around; bestAcross = across; }
        }
        if (bestScore <= .45f) return new Godot.Collections.Dictionary { ["valid"] = false, ["forest_score"] = bestScore };
        PlaceInitial(bestAround, bestAcross);
        _routeOrigin = _forestFocus;
        return new Godot.Collections.Dictionary { ["valid"] = true, ["around"] = bestAround, ["across"] = bestAcross, ["forest_score"] = bestScore };
    }

    public void MoveRoute(float acrossOffsetMeters, float speedMetersPerSecond)
        => PlaceContinuous(_routeOrigin.AroundMeters, _routeOrigin.AcrossMeters + acrossOffsetMeters, speedMetersPerSecond);

    public Godot.Collections.Dictionary CaptureFocus() => new()
    {
        ["around"] = _forestFocus.AroundMeters, ["across"] = _forestFocus.AcrossMeters, ["height"] = _forestFocus.VerticalMeters
    };

    public void BeginTelemetry()
    {
        RingPerformanceTelemetry.Configure(false);
        _timings.Clear(); _dropped = 0;
        RingPerformanceTelemetry.Configure(true, 8192);
    }

    public void SampleTelemetry()
    {
        int count = RingPerformanceTelemetry.Drain(_drain, out int dropped);
        _dropped += dropped;
        for (int i = 0; i < count; i++)
        {
            RingPerformanceTelemetry.Completed item = _drain[i];
            if (item.Gpu || !item.Name.StartsWith("Trees ", StringComparison.Ordinal)) continue;
            _timings.Add(new Godot.Collections.Dictionary {
                ["name"] = item.Name, ["milliseconds"] = item.Milliseconds,
                ["source_frame"] = item.SourceFrameId, ["completed_seconds"] = item.CompletedSeconds
            });
        }
    }

    public Godot.Collections.Dictionary FinishTelemetry()
    {
        SampleTelemetry();
        RingPerformanceTelemetry.Configure(false);
        return new Godot.Collections.Dictionary { ["scopes"] = _timings.Duplicate(true), ["dropped"] = _dropped };
    }

    public Godot.Collections.Dictionary CaptureGc() => new()
    {
        ["allocated_bytes"] = GC.GetTotalAllocatedBytes(false), ["gen0"] = GC.CollectionCount(0),
        ["gen1"] = GC.CollectionCount(1), ["gen2"] = GC.CollectionCount(2)
    };

    private RingCanonicalPosition PositionAt(double around, double across)
    {
        if (_grid?.TerrainSampler is null) return default;
        var edits = _smooth?.CaptureLodSurfaceEditSnapshot() ?? _grid.CaptureLodSurfaceEditSnapshot();
        int height = edits.GetSurfaceHeight(_grid.TerrainSampler, (long)around, (int)across);
        return new RingCanonicalPosition(around, height + 1.0, across);
    }

    private void PlaceInitial(double around, double across)
    {
        if (_explorer is null) return;
        _forestFocus = PositionAt(around, across);
        _explorer.SetBenchmarkCanonicalPosition(_forestFocus);
        _explorer.SetPresentationView(0, -10);
    }

    private void PlaceContinuous(double around, double across, float speed)
    {
        if (_explorer is null) return;
        RingCanonicalPosition position = PositionAt(around, across);
        _explorer.AdvanceTreeCpuBenchmarkCanonicalPosition(position, speed);
        _explorer.SetPresentationView(0, -10);
    }

    private static Camera3D? FindCamera(Node node)
    {
        if (node is Camera3D camera) return camera;
        foreach (Node child in node.GetChildren()) if (FindCamera(child) is Camera3D found) return found;
        return null;
    }
}
