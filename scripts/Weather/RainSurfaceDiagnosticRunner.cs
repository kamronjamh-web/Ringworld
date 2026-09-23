using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase1;

namespace RingworldTechDemo.Weather;

/// <summary>Small fixture bridge for exercising the authoritative rain-column API.</summary>
public partial class RainSurfaceDiagnosticRunner : RefCounted
{
    public string RunColumnEdits(VoxelChunkGridManager grid)
    {
        VoxelChunkDeltaSnapshot original = grid.BuildSparseChunkDeltaSnapshot();
        try
        {
            long around = 10_000;
            int across = Math.Clamp((int)Math.Round(grid.RingAcrossWidthMeters * .5), 1, (int)grid.RingAcrossWidthMeters - 2);
            int baseline = grid.TerrainSampler!.GetTerrainSample(around, across).SurfaceHeight;
            int low = baseline + 200, high = baseline + 201;
            Set(grid, around, low, across, BlockType.Concrete);
            Check(grid, around, across, baseline, low, BlockType.Concrete, "concrete +200");
            Set(grid, around, high, across, BlockType.Glass);
            Check(grid, around, across, baseline, high, BlockType.Glass, "glass higher");
            Set(grid, around, high, across, BlockType.Air);
            Check(grid, around, across, baseline, low, BlockType.Concrete, "remove high");
            Set(grid, around, low, across, BlockType.Air);
            Check(grid, around, across, baseline, baseline, grid.GetCanonicalBlockType(around, baseline, across), "remove all");
            Check(grid, around + (long)Math.Round(grid.GetCanonicalRingCircumferenceMeters64()), across, baseline, baseline, grid.GetCanonicalBlockType(around, baseline, across), "seam");
            if (grid.TryGetHighestRainSurface(around, -1, baseline, out _, out _)) throw new InvalidOperationException("across bounds");
            ExerciseBulkSnapshot(grid, across);
            return "Rain surface column diagnostics: PASS";
        }
        catch (Exception exception) { return "Rain surface column diagnostics: FAIL: " + exception.Message; }
        finally { grid.ApplySparseChunkDeltaSnapshot(original, false); }
    }

    private static void ExerciseBulkSnapshot(VoxelChunkGridManager grid, int across)
    {
        long around = 80_000;
        int baseline = grid.TerrainSampler!.GetTerrainSample(around, across).SurfaceHeight;
        int roof = Math.Max(baseline + 20, 200);
        if (roof > grid.MaxBuildGlobalY) throw new InvalidOperationException($"fixture roof outside build range {roof} ({grid.MinExcavationGlobalY}..{grid.MaxBuildGlobalY})");
        long length = (long)grid.GetCanonicalRingCircumferenceMeters64();
        if (!RingCoordMath.TryToLocalVoxel(new RingVoxelCoordinate(around, across, roof), length, out var roofChunk, out var roofLocal))
            throw new InvalidOperationException("bulk fixture coordinate");
        if (grid.TryGetChunk(roofChunk, out _)) throw new InvalidOperationException("bulk fixture must start unloaded");
        string key = $"{roofChunk.AroundRingChunk},{roofChunk.AcrossRingChunk},{roofChunk.VerticalChunk}";
        var snapshot = new VoxelChunkDeltaSnapshot();
        snapshot.ChunkOverrides[key] = new Dictionary<int, ushort>
        {
            [VoxelChunkData.ToLinearIndex(roofLocal.X, roofLocal.Y, roofLocal.Z)] = (ushort)BlockType.Concrete,
            [-1] = (ushort)BlockType.Glass
        };
        int invalidY = roof + 40;
        if (invalidY > grid.MaxBuildGlobalY) throw new InvalidOperationException("malformed-ID control must be inside build bounds");
        if (!RingCoordMath.TryToLocalVoxel(new RingVoxelCoordinate(around, across, invalidY), length, out var invalidChunk, out var invalidLocal))
            throw new InvalidOperationException("invalid-ID fixture coordinate");
        snapshot.ChunkOverrides[$"{invalidChunk.AroundRingChunk},{invalidChunk.AcrossRingChunk},{invalidChunk.VerticalChunk}"] =
            new Dictionary<int, ushort> { [VoxelChunkData.ToLinearIndex(invalidLocal.X, invalidLocal.Y, invalidLocal.Z)] = 260 };
        ulong before = grid.RainSurfaceEditVersion;
        grid.ApplySparseChunkDeltaSnapshot(snapshot, false);
        if (grid.RainSurfaceEditVersion <= before) throw new InvalidOperationException("bulk revision");
        Check(grid, around, across, baseline, roof, BlockType.Concrete, "unloaded bulk roof");
        if (grid.TryGetChunk(roofChunk, out _)) throw new InvalidOperationException("rain query loaded a distant chunk");
        ulong beforeClear = grid.RainSurfaceEditVersion;
        grid.ApplySparseChunkDeltaSnapshot(new VoxelChunkDeltaSnapshot(), false);
        if (grid.RainSurfaceEditVersion <= beforeClear || !grid.TryGetHighestRainSurface(around, across, baseline, out int top, out _) || top != baseline)
            throw new InvalidOperationException("bulk clear");
    }

    private static void Set(VoxelChunkGridManager grid, long around, int vertical, int across, BlockType type)
    {
        Vector3 world = grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(around + .5, vertical + .5, across + .5));
        if (!grid.TrySetBlockByWorldPosition(world, type, out _, out _)) throw new InvalidOperationException("edit rejected: " + type);
    }
    private static void Check(VoxelChunkGridManager grid, long around, int across, int baseline, int expectedTop, BlockType expectedMaterial, string name)
    {
        bool found = grid.TryGetHighestRainSurface(around, across, baseline, out int top, out BlockType material);
        if (!found || top != expectedTop || material != expectedMaterial)
            throw new InvalidOperationException($"{name} found={found} baseline={baseline} expected={expectedTop}/{expectedMaterial} actual={top}/{material} build={grid.MinExcavationGlobalY}..{grid.MaxBuildGlobalY}");
    }
    public Godot.Collections.Dictionary Query(VoxelChunkGridManager grid, long around, int across, int baseline)
    {
        bool found = grid.TryGetHighestRainSurface(around, across, baseline, out int top, out BlockType material);
        return new Godot.Collections.Dictionary { ["found"] = found, ["top"] = top, ["material"] = material.ToString() };
    }

    public void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
