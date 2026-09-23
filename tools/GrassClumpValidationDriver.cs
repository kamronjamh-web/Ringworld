using Godot;

namespace RingworldTechDemo;

public partial class GrassClumpValidationDriver : Node
{
    public string Result { get; private set; } = "RUNNING";

    public override void _Ready()
    {
        try { GrassClumpValidation.Verify(); Result = "PASS"; GD.Print("GRASS_CLUMP_VALIDATION=PASS"); }
        catch (System.Exception error) { Result = "FAIL " + error.Message; GD.PushError("GRASS_CLUMP_VALIDATION=" + Result); }
    }
}
