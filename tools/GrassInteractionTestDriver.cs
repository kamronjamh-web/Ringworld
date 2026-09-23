using System;
using Godot;

namespace RingworldTechDemo.Tests;

/// <summary>Callable CPU contract entry point paired with the GPU shader fixture.</summary>
public partial class GrassInteractionTestDriver : Node
{
    public bool VerifyCpuContract()
    {
        try
        {
            GrassInteractionFieldTests.Verify();
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError("GRASS_INTERACTION_CPU=FAIL " + exception.Message);
            return false;
        }
    }
}
