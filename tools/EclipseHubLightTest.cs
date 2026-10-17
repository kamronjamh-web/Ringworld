using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Phase E3 of docs/eclipse-false-sun-spec.md (C): the hub "noon everywhere" lighting outputs. Exactly neutral (zero) outside the event; the
/// spec values (kindle 0.32 energy / 0.08 day, full 3.2 / 0.8); the debug level override; the CPU twins of the shader day terms
/// (<c>EmitterGridIntensity.DayAt</c> with a hub term, <c>EclipseHubLightModel.EffectiveDay</c>) are bit exact with the hub at 0; the director
/// publishes the weight (eased, snapped for the override) and the static the macro-shadow producer reads; the day/night clock aims StarLight at
/// the local zenith through <see cref="IPrimaryLightOverride"/> and is untouched at weight 0.
/// </summary>
public partial class EclipseHubLightTest : RefCounted
{
    const double Circumference = EmitterSiteTestWorlds.Circumference;

    static void Expect(List<string> failures, bool ok, string message) { if (!ok) failures.Add(message); }
    static bool Near(double a, double b, double eps = 1e-9) => Math.Abs(a - b) <= eps;

    sealed class StubClock : IEclipseClock
    {
        public double Seconds { get; set; }
        public bool IsPaused { get; set; }
        public double SimulationSeconds => Seconds;
        public float TimeScale => 1f;
        public bool Paused { get => IsPaused; set => IsPaused = value; }
        public void SeekSimulationSeconds(double seconds) => Seconds = seconds;
    }

    sealed class StubOverride : IPrimaryLightOverride { public float Weight; public float PrimaryLightWeight => Weight; }

    static EclipseEventEngine MakeEngine()
    {
        var engine = new EclipseEventEngine { SynchronousSolve = true };
        engine.SetSettings(new EclipseEventSettings());
        engine.SetSolver(new EclipseWindowSolver());
        engine.Bind(EclipseSiteTable.Uniform(Circumference), new EmitterGridPulseClock(1337, Circumference), 1337);
        return engine;
    }

    static EclipseObserverPose Pose() => new(1_000_000d, 0d, 0.7d, 0d, 5000d, true);

    public int Run(bool unused)
    {
        _ = unused;
        var failures = new List<string>();
        try
        {
            CheckModel(failures);
            CheckCpuTwins(failures);
            CheckDirector(failures);
            CheckPrimaryLightOverride(failures);
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("ECLIPSE_HUB_LIGHT=FAIL " + string.Join(" | ", failures.Take(30))); return 1; }
        Console.WriteLine("ECLIPSE_HUB_LIGHT=PASS");
        return 0;
    }

    static void CheckModel(List<string> failures)
    {
        EclipseEventEngine engine = MakeEngine();
        var clock = new StubClock { Seconds = engine.Window.Start - 1000d };
        EclipseEventSettings s = engine.Settings;
        engine.Update(clock, 0d, Pose());
        Expect(failures, EclipseHubLightModel.Evaluate(s, engine.State, -1d) == EclipseHubLightOutput.Neutral, "idle state must give the neutral hub output");
        clock.Seconds = engine.Window.Start + 5d; engine.Update(clock, 0d, Pose());
        EclipseHubLightOutput dark = EclipseHubLightModel.Evaluate(s, engine.State, -1d);
        Expect(failures, dark.Weight == 1d && dark.DirectEnergy == 0d && dark.Day == 0d && dark.Level == 0d, $"Darkness: weight 1, no light yet ({dark})");
        clock.Seconds = engine.Window.Start + 26.6d; engine.Update(clock, 0d, Pose());
        EclipseHubLightOutput kindled = EclipseHubLightModel.Evaluate(s, engine.State, -1d);
        Expect(failures, Near(kindled.DirectEnergy, 0.32, 1e-6) && Near(kindled.Day, 0.08, 1e-6) && Near(kindled.Level, 0.1, 1e-6), $"kindle = 0.32 energy / 0.08 day ({kindled})");
        clock.Seconds = engine.Window.Start + 3000d; engine.Update(clock, 3d, Pose());
        EclipseHubLightOutput sustain = EclipseHubLightModel.Evaluate(s, engine.State, -1d);
        Expect(failures, Near(sustain.DirectEnergy, 3.2, 0.2) && Near(sustain.Day, 0.8, 0.03) && sustain.Weight == 1d && !sustain.Overridden, $"sustain ~ 3.2 / 0.8 ({sustain})");
        Expect(failures, Near(sustain.ColorR, 0.92) && Near(sustain.ColorG, 0.97) && Near(sustain.ColorB, 1d) && Near(sustain.SkyTintStrength, 0.25), "default colour and tint");

        // Override: weight 1 and the level directly, outside the event too.
        clock.Seconds = engine.Window.Start - 1000d; engine.Update(clock, 0d, Pose());
        EclipseHubLightOutput forced = EclipseHubLightModel.Evaluate(s, engine.State, 1d);
        Expect(failures, forced.Overridden && forced.Weight == 1d && Near(forced.DirectEnergy, 3.2) && Near(forced.Day, 0.8) && forced.Level == 1d, $"override 1.0 ({forced})");
        EclipseHubLightOutput over = EclipseHubLightModel.Evaluate(s, engine.State, 9d);
        Expect(failures, over.Level == EclipseHubLightModel.MaxOverrideLevel && Near(over.DirectEnergy, 8d) && over.Day == 1d, $"override clamps at 2.5, day capped at 1 ({over})");
        Expect(failures, EclipseHubLightModel.Evaluate(s, engine.State, double.NaN) == EclipseHubLightOutput.Neutral, "NaN override = no override");
        Expect(failures, EclipseHubLightModel.Evaluate(s, engine.State, -0.5d) == EclipseHubLightOutput.Neutral, "negative override = no override");
        EclipseHubLightOutput zero = EclipseHubLightModel.Evaluate(s, engine.State, 0d);
        Expect(failures, zero.Overridden && zero.Weight == 1d && zero.DirectEnergy == 0d && zero.Day == 0d, "override 0 keeps the weight at 1 with no light");
        EclipseEventSettings tuned = s with { HubDirectFraction = 0.5d, HubDayAtFull = 0.4d, HubLightRed = 0.2d };
        EclipseHubLightOutput t = EclipseHubLightModel.Evaluate(tuned, engine.State, 1d);
        Expect(failures, Near(t.DirectEnergy, 2d) && Near(t.Day, 0.4) && Near(t.ColorR, 0.2), "settings reach the hub outputs");
    }

    static void CheckCpuTwins(List<string> failures)
    {
        // Hub day 0 must reproduce the original expressions bit for bit, over a sweep.
        bool identical = true;
        for (int i = -40; i <= 40; i++)
            foreach (double vis in new[] { 0d, 0.25, 0.8, 1d })
            {
                double dot = i / 40d;
                double original = EmitterGridIntensity.Smoothstep(-0.2d, 0.35d, dot) * Math.Clamp(vis, 0d, 1d);
                if (EmitterGridIntensity.DayAt(dot, vis) != original || EmitterGridIntensity.DayAt(dot, vis, 0d) != original) identical = false;
                if (EclipseHubLightModel.EffectiveDay(EmitterGridIntensity.Smoothstep(-0.2d, 0.35d, dot), vis, 0d) != original) identical = false;
            }
        Expect(failures, identical, "hub day 0 must leave DayAt and EffectiveDay bit exact");
        Expect(failures, EmitterGridIntensity.DayAt(1d, 0d, 0.8d) == 0.8d && EmitterGridIntensity.DayAt(-1d, 0d, 0.8d) == 0.8d, "totality: hub day is the day everywhere");
        Expect(failures, EmitterGridIntensity.DayAt(1d, 1d, 0.8d) == 1d, "real noon beats the hub");
        Expect(failures, EmitterGridIntensity.DayAt(0.1d, 0d, 7d) == 1d, "hub day clamps to 1");
    }

    static void CheckDirector(List<string> failures)
    {
        var director = new EclipseEventDirector { AutoBind = false, LoadSavedDefaults = false };
        try
        {
            Expect(failures, director.HubLight == EclipseHubLightOutput.Neutral && director.HubLightWeight == 0f && !director.HubLevelOverrideActive, "director starts with the hub off");
            director.SetHubLevelOverride(1f);
            Expect(failures, director.HubLevelOverrideActive && director.HubLightWeight == 1f && Math.Abs(director.HubLightDirect - 3.2f) < 1e-5f
                && Math.Abs(director.HubLightDay - 0.8f) < 1e-5f && EclipseHubLighting.Weight == 1f, "override snaps the weight and publishes the static");
            Expect(failures, Math.Abs(director.HubLightColor.R - 0.92f) < 1e-5f && Math.Abs(director.HubLightLevel - 1f) < 1e-5f, "override colour and level");
            // The override relieves the darkness from the kindle level (D.1).
            director.UpdateDarkness(1d, 5d);
            Expect(failures, director.DarknessWeight == 0f, "hub level 1 relieves the darkness completely");
            director.SetHubLevelOverride(0.1f);
            director.UpdateDarkness(1d, 5d);
            Expect(failures, Math.Abs(director.DarknessWeight - 0.5f) < 1e-3f, "kindle level halves the darkness");
            director.SetHubLevelOverride(-1f);
            Expect(failures, !director.HubLevelOverrideActive && director.HubLight == EclipseHubLightOutput.Neutral && director.HubLightWeight == 0f && EclipseHubLighting.Weight == 0f,
                "clearing the override returns the hub to neutral");
            director.UpdateDarkness(1d, 5d);
            Expect(failures, director.DarknessWeight == 1f, "darkness is full again with the hub off");

            // Easing: an event weight of 1 reached from 0 takes about a second; the first evaluation snaps.
            director.Engine.SetSolver(new EclipseWindowSolver());
            director.Engine.SynchronousSolve = true;
            director.Engine.Bind(EclipseSiteTable.Uniform(Circumference), new EmitterGridPulseClock(1337, Circumference), 1337);
            var clock = new StubClock { Seconds = director.Engine.Window.Start - 1000d };
            director.Engine.Update(clock, 0d, Pose());
            director.RefreshHubLight(0.016);
            Expect(failures, director.HubLightWeight == 0f, "outside the window the weight is 0");
            clock.Seconds = director.Engine.Window.Start + 3000d;
            director.Engine.Update(clock, 3d, Pose());
            director.RefreshHubLight(0.1);
            float after100ms = director.HubLightWeight;
            Expect(failures, after100ms > 0.05f && after100ms < 0.5f, $"weight eases in ({after100ms})");
            for (int i = 0; i < 40; i++) director.RefreshHubLight(0.016);
            Expect(failures, director.HubLightWeight > 0.85f && director.HubLightWeight < 0.95f, $"weight is about 89 percent after 0.74 s ({director.HubLightWeight})");
            for (int i = 0; i < 200; i++) director.RefreshHubLight(0.016);
            Expect(failures, director.HubLightWeight == 1f && EclipseHubLighting.Weight == 1f, "weight settles at exactly 1");
            clock.Seconds = director.Engine.Window.Start - 1000d;
            director.Engine.Update(clock, 0d, Pose());
            for (int i = 0; i < 200; i++) director.RefreshHubLight(0.016);
            Expect(failures, director.HubLightWeight == 0f && director.HubLight == EclipseHubLightOutput.Neutral, "weight returns to exactly 0");
        }
        finally { director.Free(); }
        EclipseHubLighting.Reset();
        CheckMacroShadowHold(failures);
    }

    static void CheckMacroShadowHold(List<string> failures)
    {
        EclipseHubLighting.Reset();
        Expect(failures, !EclipseHubLighting.HoldMacroShadows, "no hold before any event");
        EclipseHubLighting.Weight = 0.4f; EclipseHubLighting.Tick();
        Expect(failures, EclipseHubLighting.HoldMacroShadows, "hold while the weight is above 0");
        EclipseHubLighting.Weight = 1e-6f; EclipseHubLighting.Tick();
        Expect(failures, EclipseHubLighting.HoldMacroShadows, "even a tiny weight holds (the light is not exactly on the sun)");
        EclipseHubLighting.Weight = 0f;
        for (int i = 0; i < EclipseHubLighting.QuietFramesBeforeRelease - 1; i++)
        {
            EclipseHubLighting.Tick();
            Expect(failures, EclipseHubLighting.HoldMacroShadows, $"hold continues for the quiet frames after the weight reaches 0 (frame {i})");
        }
        EclipseHubLighting.Tick();
        Expect(failures, !EclipseHubLighting.HoldMacroShadows, "hold releases after the quiet frames");
        EclipseHubLighting.Reset();
    }

    static void CheckPrimaryLightOverride(List<string> failures)
    {
        SceneTree tree = (Engine.GetMainLoop() as SceneTree) ?? throw new InvalidOperationException("scene tree unavailable");
        var holder = new Node3D { Name = "HubLightHolder" };
        var light = new DirectionalLight3D { Name = "StarLight" };
        var cycle = new RingDayNightCycle { Name = "RingDayNightCycle", SunLightPath = new NodePath("../StarLight") };
        holder.AddChild(light);
        holder.AddChild(cycle);
        tree.Root.AddChild(holder);
        try
        {
            cycle.SetSimulationSeconds(12_000d);
            Vector3 toSun = light.GlobalBasis.Z.Normalized();
            Vector3 frameSun = cycle.CurrentFrame.ToSun.Normalized();
            Expect(failures, toSun.DistanceTo(frameSun) < 1e-4f, $"no override: StarLight follows ToSun ({toSun} vs {frameSun})");
            var stub = new StubOverride { Weight = 0f };
            cycle.PrimaryLightOverride = stub;
            cycle.SetSimulationSeconds(12_000d);
            Expect(failures, light.GlobalBasis.Z.Normalized().DistanceTo(toSun) < 1e-6f, "weight 0 leaves the light exactly where it was");
            // Without an explorer the cycle's surface up is -X (documented fallback in UpdateFrameAndLight).
            Vector3 zenith = -Vector3.Right;
            stub.Weight = 1f;
            cycle.SetSimulationSeconds(12_000d);
            Expect(failures, light.GlobalBasis.Z.Normalized().DistanceTo(zenith) < 1e-4f, $"weight 1 aims the light at the local zenith ({light.GlobalBasis.Z})");
            Basis basis = light.GlobalBasis;
            Expect(failures, Math.Abs(basis.X.Length() - 1f) < 1e-4f && Math.Abs(basis.Z.Dot(basis.X)) < 1e-4f, "the light basis stays orthonormal");
            stub.Weight = 0.5f;
            cycle.SetSimulationSeconds(12_000d);
            Vector3 mid = light.GlobalBasis.Z.Normalized();
            float sunToZenith = toSun.DistanceTo(zenith);
            Expect(failures, mid.DistanceTo(toSun) > 0.01f && mid.DistanceTo(zenith) > 0.01f && mid.DistanceTo(toSun) < sunToZenith && mid.DistanceTo(zenith) < sunToZenith,
                "weight 0.5 lies between the sun and the zenith");
            stub.Weight = 0f;
            cycle.SetSimulationSeconds(12_000d);
            Expect(failures, light.GlobalBasis.Z.Normalized().DistanceTo(toSun) < 1e-6f, "weight back to 0 restores the sun direction exactly");
            cycle.PrimaryLightOverride = null;
        }
        finally { holder.QueueFree(); }
    }
}
