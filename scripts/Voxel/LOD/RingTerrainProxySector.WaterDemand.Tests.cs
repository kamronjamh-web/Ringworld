using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Test-only fixture surface; it is intentionally kept out of runtime code paths.</summary>
public partial class RingTerrainProxySector
{
    internal void ConfigureWaterDemandFixture(VoxelChunkGridManager grid)
    {
        _grid = grid;
        StreamingActive = true;
        Visible = true;
        SetProcess(false);
    }

    internal void SetWaterDemandFixtureSector(RingTerrainSectorCoordinate coordinate, Rect2 footprint, SurfaceWaterKind kind)
    {
        RemoveFftWaterDemandMetadata(coordinate);
        if (_active.Remove(coordinate, out MeshInstance3D? old)) old.QueueFree();
        var node = new MeshInstance3D { Visible = true };
        AddChild(node);
        _active[coordinate] = node;
        Vector2 min = footprint.Position, max = footprint.End;
        Vector2[] uvs = { min, new(max.X, min.Y), max, new(min.X, max.Y) };
        Color color = RingWaterModel.EncodeWaterKind(Colors.White, kind);
        Color[] colors = { color, color, color, color };
        var data = new SectorData(coordinate, 1, 1, Vector3.Zero,
            new Vector3[4], new Vector3[4], colors, uvs, new Vector2[4],
            new[] { 0, 1, 2, 0, 2, 3 }, new[] { 0, 1, 2, 0, 2, 3 }, null, null, false, null);
        InstallFftWaterDemandMetadata(data);
    }

    internal void SetWaterDemandFixtureVisible(RingTerrainSectorCoordinate coordinate, bool visible)
    {
        if (_active.TryGetValue(coordinate, out MeshInstance3D? node)) node.Visible = visible;
    }

    internal void RemoveWaterDemandFixtureSector(RingTerrainSectorCoordinate coordinate)
    {
        RemoveFftWaterDemandMetadata(coordinate);
        if (_active.Remove(coordinate, out MeshInstance3D? node)) node.QueueFree();
    }

    internal void SetWaterDemandFixtureWarm(RingTerrainSectorCoordinate coordinate, bool warm)
    {
        if (warm) _warmUntil[coordinate] = double.MaxValue;
        else _warmUntil.Remove(coordinate);
    }
}
