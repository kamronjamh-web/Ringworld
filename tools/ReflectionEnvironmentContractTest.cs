using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>
/// R1-0 (docs/work-orders/reflections/R1-1-design.md): ConfigureMainEnvironment gives a black Color background reflecting Background;
/// the reflection model switch moves the main reflected source Background -> Sky -> Background; the far constants stay Background.
/// Prints REFLECTION_ENVIRONMENT_CONTRACT=PASS. Headless: tools/run_tests.ps1 -Tests reflection_environment_contract_test.
/// </summary>
public partial class ReflectionEnvironmentContractTest : RefCounted
{
    public int Run()
    {
        var failures = new System.Collections.Generic.List<string>();
        var model = new RingReflectionModel();
        try
        {
            var env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Sky, ReflectedLightSource = Godot.Environment.ReflectionSource.Sky };
            new RingFarViewportColorPipeline().ConfigureMainEnvironment(env);
            if (env.BackgroundMode != Godot.Environment.BGMode.Color) failures.Add("background mode " + env.BackgroundMode);
            if (env.BackgroundColor != Colors.Black) failures.Add("background colour " + env.BackgroundColor);
            if (env.ReflectedLightSource != Godot.Environment.ReflectionSource.Bg) failures.Add("default source " + env.ReflectedLightSource);
            model.SetModel(1.0f);
            if (env.ReflectedLightSource != Godot.Environment.ReflectionSource.Sky) failures.Add("model 1 source " + env.ReflectedLightSource);
            model.SetModel(0.0f);
            if (env.ReflectedLightSource != Godot.Environment.ReflectionSource.Bg) failures.Add("model 0 source " + env.ReflectedLightSource);
            if (RingScaledFarViewport.FarReflectionSource != Godot.Environment.ReflectionSource.Bg) failures.Add("far pin");
            if (RingScaledFarViewport.RegionalFarReflectionSource != Godot.Environment.ReflectionSource.Bg) failures.Add("regional pin");
            if (RingReflectionModel.SourceFor(float.NaN) != Godot.Environment.ReflectionSource.Bg) failures.Add("NaN not sanitized");
            if (model.GetModel() != 0.0f) failures.Add("model not 0");
        }
        catch (Exception e) { Console.Error.WriteLine("REFLECTION_ENVIRONMENT_CONTRACT=FAIL exception " + e); return 1; }
        finally { model.SetModel(0.0f); RingReflectionModel.RegisterMain(null); }
        if (failures.Count != 0) { Console.Error.WriteLine("REFLECTION_ENVIRONMENT_CONTRACT=FAIL " + string.Join(" | ", failures)); return 1; }
        Console.WriteLine("REFLECTION_ENVIRONMENT_CONTRACT=PASS");
        return 0;
    }
}
