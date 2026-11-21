using System.Text;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Read-only determinism probe for the published global morph snapshot (used by global_terrain_determinism_test).</summary>
public partial class RingTerrainGlobalLod
{
    /// <summary>
    /// Returns "total|h0,h1,..." (FNV hashes as 16 hex digits: the snapshot's TopologyAndHeightHash, then one per segment),
    /// or an empty string while no morph snapshot is published. Only reads the immutable snapshot; not called per frame.
    /// </summary>
    public string GetMorphDeterminismReport()
    {
        if (_readyMorphSnapshot is not RingTerrainGlobalMorphSnapshot snapshot) return string.Empty;
        RingTerrainGlobalMorphSnapshot.Segment[] segments = snapshot.CopySegments();
        var builder = new StringBuilder();
        builder.Append(snapshot.SnapshotIdentity.TopologyAndHeightHash.ToString("X16")).Append('|');
        for (int index = 0; index < segments.Length; index++)
        {
            if (index > 0) builder.Append(',');
            builder.Append(ComputeMorphTopologyAndHeightHash(new[] { segments[index] }).ToString("X16"));
        }
        return builder.ToString();
    }
}
