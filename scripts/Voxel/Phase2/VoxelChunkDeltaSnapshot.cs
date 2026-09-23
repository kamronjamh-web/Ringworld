using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.Phase2;

public sealed class VoxelChunkDeltaSnapshot
{
    // Key: "around,across,vertical" chunk coordinate.
    // Value: sparse local voxel overrides (local linear index -> block id).
    public Dictionary<string, Dictionary<int, ushort>> ChunkOverrides { get; set; } = new();
    public List<NaturalTerrainSdfDeltaRecord> NaturalTerrainSdfDeltas { get; set; } = new();
}

public sealed class NaturalTerrainSdfDeltaRecord
{
    public long AroundVoxel { get; set; }
    public int VerticalVoxel { get; set; }
    public int AcrossVoxel { get; set; }
    public float DensityDelta { get; set; }
}
