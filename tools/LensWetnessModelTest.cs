using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Lens wetness model (docs/storm-revamp-s2-wet-world-design.md 3): look-up gain, emergence burst, decay (tau 5 s), underwater and snow give zero.
/// Run: Godot --headless --path . --script res://tools/lens_wetness_model_test.gd. Prints LENS_WETNESS_MODEL=PASS.
/// </summary>
public partial class LensWetnessModelTest : RefCounted
{
    public int Run()
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            // Look-up gain: looking up in rain collects far more than looking ahead.
            var ahead = new LensWetnessModel(); var up = new LensWetnessModel();
            for (int i = 0; i < 60; i++) { ahead.Step(1 / 60f, 0.8f, 1.0f, 0.0f, false); up.Step(1 / 60f, 0.8f, 1.0f, 0.9f, false); }
            Check(up.Wetness > ahead.Wetness * 5.0f && up.Wetness > 0.2f && ahead.Wetness > 0.0f, $"look-up gain {up.Wetness} vs ahead {ahead.Wetness}");
            Check(Math.Abs(LensWetnessModel.GainPerSecond(0.5f, 1.0f, 1.0f) - 0.5f * 0.54f) < 1e-5f, "gain formula");
            // No rain, no gain.
            var dry = new LensWetnessModel();
            for (int i = 0; i < 120; i++) dry.Step(1 / 60f, 0.0f, 1.0f, 1.0f, false);
            Check(dry.Wetness == 0.0f, "no rain no lens water");
            // Sheltered camera collects nothing.
            var sheltered = new LensWetnessModel();
            for (int i = 0; i < 120; i++) sheltered.Step(1 / 60f, 0.9f, 0.0f, 1.0f, false);
            Check(sheltered.Wetness == 0.0f, "sheltered camera stays dry");
            // Emergence burst when leaving cover into rain > 0.2.
            var burst = new LensWetnessModel();
            for (int i = 0; i < 30; i++) burst.Step(1 / 60f, 0.8f, 0.0f, 0.0f, false);
            float before = burst.Wetness;
            burst.Step(1 / 60f, 0.8f, 1.0f, 0.0f, false);
            Check(before == 0.0f && burst.Wetness >= 0.25f + 0.4f * 0.8f - 0.01f, $"emergence burst {burst.Wetness}");
            var light = new LensWetnessModel();
            for (int i = 0; i < 30; i++) light.Step(1 / 60f, 0.1f, 0.0f, 0.0f, false);
            light.Step(1 / 60f, 0.1f, 1.0f, 0.0f, false);
            Check(light.Wetness < 0.05f, $"no burst in drizzle {light.Wetness}");
            // Decay, tau 5 s.
            var decay = new LensWetnessModel();
            decay.Reset(0.8f);
            for (int i = 0; i < 300; i++) decay.Step(1 / 60f, 0.0f, 1.0f, 0.0f, false);
            Check(Math.Abs(decay.Wetness - 0.8f / MathF.E) < 0.02f, $"decay tau 5 s: {decay.Wetness} vs {0.8f / MathF.E}");
            // Underwater / snow: zero immediately.
            var wet = new LensWetnessModel(); wet.Reset(0.9f);
            Check(wet.Step(0.016f, 0.9f, 1.0f, 1.0f, true) == 0.0f && wet.Wetness == 0.0f, "underwater or snow is zero");
            // Clamp and garbage input.
            var clamp = new LensWetnessModel();
            for (int i = 0; i < 2000; i++) clamp.Step(0.05f, 1.0f, 1.0f, 1.0f, false);
            Check(clamp.Wetness <= 1.0f && clamp.Wetness > 0.9f, "saturates at 1");
            Check(float.IsFinite(clamp.Step(float.NaN, float.NaN, 1.0f, 0.0f, false)), "NaN safe");
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("LENS_WETNESS_MODEL=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("LENS_WETNESS_MODEL=PASS");
        return 0;
    }
}
