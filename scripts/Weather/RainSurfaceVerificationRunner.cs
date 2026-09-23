using System;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Weather;

/// <summary>Disposable-scene verification only. Never saves or changes the terrain generator.</summary>
public partial class RainSurfaceVerificationRunner : RefCounted
{
    private ulong _nextCommand = 900_000;

    public Godot.Collections.Dictionary FindSurface(VoxelChunkGridManager grid, bool water)
    {
        int across = (int)(grid.RingAcrossWidthMeters * .5);
        long length = (long)grid.GetCanonicalRingCircumferenceMeters64();
        for (int i = 0; i < 256; i++)
        {
            long around = (10_000L + i * 2_000L) % length;
            var sample = grid.TerrainSampler!.GetTerrainSample(around, across);
            if (sample.HasSurfaceWater != water) continue;
            float natural = grid.TerrainSampler.GetSmoothSurfaceHeight(around, across) + .5f;
            if (!water && natural < 5) continue;
            return new Godot.Collections.Dictionary
            {
                ["found"] = true, ["around"] = around, ["across"] = across,
                ["natural"] = natural, ["top"] = water ? sample.WaterSurfaceHeight + 1f : natural
            };
        }
        return new Godot.Collections.Dictionary { ["found"] = false };
    }

    public bool Roof(VoxelChunkGridManager grid, long around, int across, int height, bool remove)
    {
        for (int x = -2; x <= 2; x++)
        for (int z = -2; z <= 2; z++)
        {
            Vector3 world = grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(around + x + .5, height + .5, across + z + .5));
            if (!grid.TrySetBlockByWorldPosition(world, remove ? BlockType.Air : BlockType.Concrete, out _, out _)) return false;
        }
        return true;
    }

    public void Stamp(SmoothTerrainGrid smooth, long around, int across, int height, float radius, bool add)
    {
        ulong id = _nextCommand++;
        smooth.AddEdit(new SmoothTerrainEditCommand(SmoothTerrainEditCommand.CurrentVersion,
            id, (long)id, add, around, height, across, radius, (int)SmoothTerrainMaterial.Rock));
    }

    public bool WaterCell(VoxelChunkGridManager grid, long around, int across, int height, bool remove)
    {
        Vector3 world = grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(around + .5, height + .5, across + .5));
        return remove ? grid.TryRemoveWaterByWorldPosition(world) : grid.TryPlaceWaterSourceByWorldPosition(world);
    }

    public bool IsAir(SmoothTerrainGrid smooth, long around, int across, double height) =>
        smooth.TryQueryEditableDensityAtCanonicalPosition(around, height, across, out float density) && density > 0;
}
