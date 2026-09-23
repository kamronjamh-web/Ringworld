using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

[GlobalClass]
public partial class TreeGpuPreparationSmoke : Node
{
    public Godot.Collections.Dictionary Run()
    {
        var layer = new RingBiomeDecorationLayer { Enabled = false };
        AddChild(layer);
        string detail = layer.RunGpuPreparationRegression();
        layer.QueueFree();
        bool pass = detail.StartsWith("PASS:");
        return new Godot.Collections.Dictionary { ["pass"] = pass, ["detail"] = detail };
    }
}
