using Godot;
using RingworldTechDemo.Voxel.LOD;

public partial class TreeGpuFrameCacheSmoke : Node
{
    public Godot.Collections.Dictionary Run()
    {
        var layer = new RingBiomeDecorationLayer();
        AddChild(layer);
        string detail = layer.RunGpuTreeFrameCacheRegression();
        layer.QueueFree();
        return new Godot.Collections.Dictionary { ["pass"] = detail.StartsWith("PASS:"), ["detail"] = detail };
    }
}
