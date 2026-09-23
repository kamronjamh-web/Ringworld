using System;
using Godot;

namespace RingworldTechDemo.Voxel.Integration;

/// <summary>Small headless entry point for the roof light-leak model.</summary>
public partial class UndergroundEnvironmentRuntimeSmoke : Node
{
    public override void _Ready()
    {
        try
        {
            RingUndergroundEnvironmentController.RunAutomatedTests();
            GD.Print("UNDERGROUND_ENVIRONMENT_SMOKE_TEST=PASS");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PushError($"UNDERGROUND_ENVIRONMENT_SMOKE_TEST=FAIL: {exception.Message}");
            GetTree().Quit(1);
        }
    }
}
