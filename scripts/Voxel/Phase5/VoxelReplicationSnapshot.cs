using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.Phase5;

public sealed class VoxelReplicationSnapshot
{
    public int AuthorityId { get; set; }
    public uint GlobalRevision { get; set; }

    // Key: "around,across,vertical" chunk coordinate.
    // Value: monotonic chunk revision.
    public Dictionary<string, uint> ChunkRevisions { get; set; } = new();

    // Key: "around,across,vertical" chunk coordinate.
    // Value: sparse local voxel overrides (local linear index -> block id).
    public Dictionary<string, Dictionary<int, ushort>> ChunkOverrides { get; set; } = new();

    // Key: canonical "around,across,vertical" voxel coordinate. Only authored
    // water sources are saved; flowing cells are regenerated locally on load.
    public Dictionary<string, byte> WaterSources { get; set; } = new();
}
