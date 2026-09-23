using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

/// <summary>Small bridge for the opt-in grass streaming profile. It keeps the
/// scene script independent of private explorer and terrain types.</summary>
public partial class GrassGenerationProfileDriver : Node
{
    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private SmoothTerrainGrid? _smooth;
    private RingCanonicalPosition _initial;
    public bool Prepare(Node game)
    {
        _explorer = game.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        _grid = game.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager");
        _smooth = game.GetNodeOrNull<SmoothTerrainGrid>("SmoothTerrainGrid");
        if (_explorer is null || _grid?.TerrainSampler is null) return false;
        _initial = _explorer.CanonicalPosition;
        Camera3D? camera = FindCamera(_explorer);
        if (camera is null) return false;
        camera.MakeCurrent();
        _explorer.SetPresentationView(0, -10);
        return true;
    }
    public string InitialCanonicalFocus => _initial.ToString();
    public Godot.Collections.Dictionary CaptureFocus()
    {
        RingCanonicalPosition p = _explorer?.CanonicalPosition ?? _initial;
        return new Godot.Collections.Dictionary { ["around"] = p.AroundMeters,
            ["across"] = p.AcrossMeters, ["height"] = p.VerticalMeters };
    }
    public void MoveDiscrete(float acrossMeters)
    {
        if (_explorer is null || _grid?.TerrainSampler is null) return;
        var next = _initial with { AcrossMeters = _initial.AcrossMeters + acrossMeters };
        var hints = _smooth?.CaptureLodSurfaceEditSnapshot() ?? _grid.CaptureLodSurfaceEditSnapshot();
        int height = hints.GetSurfaceHeight(_grid.TerrainSampler, (long)next.AroundMeters, (int)next.AcrossMeters);
        _explorer.SetBenchmarkCanonicalPosition(next with { VerticalMeters = height + 1.0 });
        _explorer.SetPresentationView(0, -10);
    }
    public void Restore()
    {
        _explorer?.SetBenchmarkCanonicalPosition(_initial);
        _explorer?.SetPresentationView(0, -10);
    }
    private static Camera3D? FindCamera(Node node)
    {
        if (node is Camera3D camera) return camera;
        foreach (Node child in node.GetChildren())
            if (FindCamera(child) is Camera3D found) return found;
        return null;
    }
}
