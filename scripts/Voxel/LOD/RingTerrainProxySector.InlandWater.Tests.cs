using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Test-only probes of the installed proxy water (T2R-11); kept out of runtime code paths.</summary>
public partial class RingTerrainProxySector
{
    /// <summary>True when this level has an installed sector holding water geometry at the canonical position (the sector's rectangle, whichever sample spacing the level has).</summary>
    public bool HasActiveWaterSectorAt(double around, double across)
    {
        int size = Mathf.Max(32, SectorSizeMeters);
        long aroundLength = _grid?.TerrainSampler?.AroundRingVoxelLength ?? 0L;
        double wrapped = aroundLength > 0 ? ((around % aroundLength) + aroundLength) % aroundLength : around;
        var coordinate = new RingTerrainSectorCoordinate((long)System.Math.Floor(wrapped / size), (int)System.Math.Floor(across / size));
        return _activeWaterSectors.Contains(coordinate);
    }
}
