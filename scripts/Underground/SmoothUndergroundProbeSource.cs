using System;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Underground;

/// <summary>Smooth-terrain backend: editable density field plus the edited LOD surface heights.</summary>
public sealed class SmoothUndergroundProbeSource : IUndergroundProbeSource
{
    private readonly SmoothTerrainGrid _smooth;
    private readonly VoxelChunkGridManager _grid;
    private RingLodSurfaceEditSnapshot? _surfaceEdits;
    private ulong _surfaceEditsRevision = ulong.MaxValue;

    public SmoothUndergroundProbeSource(SmoothTerrainGrid smooth, VoxelChunkGridManager grid)
    {
        _smooth = smooth;
        _grid = grid;
    }

    public bool IsAvailable => GodotObjectAlive(_smooth) && GodotObjectAlive(_grid)
        && _smooth.IsInsideTree() && _grid.TerrainSampler != null;

    public ulong Revision
    {
        get
        {
            if (!IsAvailable) return 0;
            return unchecked(_smooth.EditRevision * 1000003UL + (ulong)(uint)_grid.GetVerticalDomainConfiguration().GetHashCode());
        }
    }

    public bool TryGetSurfaceHeight(double around, double across, out float height)
    {
        height = 0;
        var sampler = _grid.TerrainSampler;
        if (sampler == null || !double.IsFinite(around) || !double.IsFinite(across)) return false;
        ulong revision = _smooth.EditRevision;
        if (_surfaceEdits == null || _surfaceEditsRevision != revision)
        {
            _surfaceEdits = _smooth.CaptureLodSurfaceEditSnapshot();
            _surfaceEditsRevision = revision;
        }
        int acrossVoxel = Math.Clamp((int)Math.Floor(across), 0, Math.Max(0, (int)Math.Floor(_grid.RingAcrossWidthMeters) - 1));
        height = _surfaceEdits.GetSurfaceHeight(sampler, (long)Math.Floor(around), acrossVoxel);
        return true;
    }

    public bool TryGetDensity(double around, double vertical, double across, out float density) =>
        _smooth.TryQueryEditableDensityAtCanonicalPosition(around, vertical, across, out density);

    private static bool GodotObjectAlive(Godot.GodotObject node) => Godot.GodotObject.IsInstanceValid(node);
}
