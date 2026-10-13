using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingTerrainGlobalLod
{
    /// <summary>True while underground mode hides the distant surface terrain. Meshes, morph snapshots and workers are retained.</summary>
    public bool UndergroundHidden { get; private set; }

    /// <summary>
    /// Hides the global surface terrain segments and pauses their service work. The structural hull and edge-wall
    /// segments stay visible so the structural cavity keeps its long-range walls, and nothing is freed.
    /// </summary>
    public void SetUndergroundHidden(bool hidden)
    {
        if (UndergroundHidden == hidden) return;
        UndergroundHidden = hidden;
        _lastRegionalVisibilityEnabled = false; // re-run distance visibility after resume
        foreach (MeshInstance3D segment in _terrainSegments)
            if (GodotObject.IsInstanceValid(segment)) segment.Visible = !hidden;
        foreach (MeshInstance3D segment in _retainedTerrainSegments)
            if (GodotObject.IsInstanceValid(segment)) segment.Visible = !hidden;
    }
}
