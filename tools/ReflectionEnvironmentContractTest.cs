using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>
/// R1-0 (docs/work-orders/reflections/R1-1-design.md): ConfigureMainEnvironment gives a black Color background;
/// the main reflected source is Sky unconditionally (reflection model 1 is the only path); the far constants stay Background.
/// Prints REFLECTION_ENVIRONMENT_CONTRACT=PASS. Headless: tools/run_tests.ps1 -Tests reflection_environment_contract_test.
/// </summary>
public partial class ReflectionEnvironmentContractTest : RefCounted
{
    public int Run()
    {
        var failures = new System.Collections.Generic.List<string>();
        try
        {
            var env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Sky, ReflectedLightSource = Godot.Environment.ReflectionSource.Bg };
            new RingFarViewportColorPipeline().ConfigureMainEnvironment(env);
            if (env.BackgroundMode != Godot.Environment.BGMode.Color) failures.Add("background mode " + env.BackgroundMode);
            if (env.BackgroundColor != Colors.Black) failures.Add("background colour " + env.BackgroundColor);
            if (env.ReflectedLightSource != Godot.Environment.ReflectionSource.Sky) failures.Add("main source " + env.ReflectedLightSource);
            if (RingScaledFarViewport.FarReflectionSource != Godot.Environment.ReflectionSource.Bg) failures.Add("far pin");
            if (RingScaledFarViewport.RegionalFarReflectionSource != Godot.Environment.ReflectionSource.Bg) failures.Add("regional pin");
        }
        catch (Exception e) { Console.Error.WriteLine("REFLECTION_ENVIRONMENT_CONTRACT=FAIL exception " + e); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("REFLECTION_ENVIRONMENT_CONTRACT=FAIL " + string.Join(" | ", failures)); return 1; }
        Console.WriteLine("REFLECTION_ENVIRONMENT_CONTRACT=PASS");
        return 0;
    }
}
