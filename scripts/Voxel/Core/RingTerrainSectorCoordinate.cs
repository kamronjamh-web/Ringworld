namespace RingworldTechDemo.Voxel.Core;

/// <summary>Canonical proxy-sector identity: only the around-ring axis can exceed 32-bit range.</summary>
public readonly record struct RingTerrainSectorCoordinate(
    long AroundSector,
    int AcrossSector);