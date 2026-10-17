using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Phase E1 of docs/eclipse-false-sun-spec.md (H, G.3): the director logic against a stub clock. Force, jump-to-phase and scrub move the
/// celestial clock and emit exactly one Resync; the event follows TimeScale (milestones land at 1x time / k, no spurious Resync at high
/// speed, the stream threshold is max(5, 0.5 * TimeScale)); the grid-clock hook is on from MainFire to the end of the window; local
/// contacts bracket the whole-ring window; restoring a time reproduces the same state; the async solve path publishes Idle until cached;
/// the scene node exists and the director node applies settings. No lighting or visual state is touched.
/// </summary>
public partial class EclipseEventDirectorTest : RefCounted
{
    const double Circumference = EmitterSiteTestWorlds.Circumference;

    static void Expect(List<string> failures, bool ok, string message) { if (!ok) failures.Add(message); }

    sealed class StubClock : IEclipseClock
    {
        public double Seconds { get; set; }
        public float Scale { get; set; } = 1f;
        public bool IsPaused { get; set; }
        public int Seeks { get; private set; }
        public double SimulationSeconds => Seconds;
        public float TimeScale => Scale;
        public bool Paused { get => IsPaused; set => IsPaused = value; }
        public void SeekSimulationSeconds(double seconds) { Seconds = seconds; Seeks++; }
        public void Advance(double realDelta) { if (!IsPaused) Seconds += realDelta * Scale; }
    }

    static EclipseEventEngine MakeEngine(EclipseEventSettings? settings = null, bool sync = true)
    {
        var engine = new EclipseEventEngine { SynchronousSolve = sync };
        engine.SetSettings(settings ?? new EclipseEventSettings());
        engine.SetSolver(new EclipseWindowSolver());
        engine.Bind(EclipseSiteTable.Uniform(Circumference), new EmitterGridPulseClock(1337, Circumference), 1337);
        return engine;
    }

    static EclipseObserverPose Pose(double lon = 0.7d) => new(1_000_000d, 0d, lon, 0d, 5000d, true);

    public int Run(bool unused)
    {
        _ = unused;
        var failures = new List<string>();
        try
        {
            CheckUnbound(failures);
            CheckStepThreshold(failures);
            CheckForceScrubJump(failures);
            CheckResyncOnExternalJump(failures);
            CheckTimeScaleFollowing(failures);
            CheckGridHook(failures);
            CheckLocalContacts(failures);
            CheckDeterminism(failures);
            CheckPauseAndDisabled(failures);
            CheckAsyncSolve(failures);
            CheckStatusAndStrip(failures);
            CheckSceneAndNode(failures);
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("ECLIPSE_EVENT_DIRECTOR=FAIL " + string.Join(" | ", failures.Take(30))); return 1; }
        Console.WriteLine("ECLIPSE_EVENT_DIRECTOR=PASS");
        return 0;
    }

    static void CheckUnbound(List<string> failures)
    {
        var engine = new EclipseEventEngine { SynchronousSolve = true };
        var clock = new StubClock { Seconds = 12_100d };
        engine.Update(clock, 0d, Pose());
        Expect(failures, engine.Phase == EclipseEventPhase.Idle && !engine.Bound && !engine.Active, "unbound engine must stay Idle");
        Expect(failures, engine.DescribeStatus().Contains("unavailable", StringComparison.Ordinal), "unbound status text");
        Expect(failures, !engine.ForceEclipseNow(clock) && clock.Seeks == 0, "force must refuse without a bound event");
    }

    static void CheckStepThreshold(List<string> failures)
    {
        Expect(failures, EclipseEventEngine.MaxContinuousStepSeconds(1f) == 5d, "threshold at 1x");
        Expect(failures, EclipseEventEngine.MaxContinuousStepSeconds(10f) == 5d, "threshold at 10x");
        Expect(failures, EclipseEventEngine.MaxContinuousStepSeconds(20f) == 10d, "threshold at 20x");
        Expect(failures, EclipseEventEngine.MaxContinuousStepSeconds(100f) == 50d, "threshold at 100x");
        Expect(failures, EclipseEventEngine.MaxContinuousStepSeconds(float.NaN) == 5d, "threshold NaN");
    }

    static List<EclipseEvent> Collect(EclipseEventEngine engine) { var l = new List<EclipseEvent>(); engine.EventRaised += l.Add; return l; }

    static void CheckForceScrubJump(List<string> failures)
    {
        EclipseEventEngine engine = MakeEngine();
        List<EclipseEvent> events = Collect(engine);
        var clock = new StubClock { Seconds = 100d };
        engine.Update(clock, 0d, Pose());
        Expect(failures, engine.Active && engine.Timeline != null, "k=0 timeline should be active");
        double S = engine.Window.Start, E = engine.Window.End;
        double preRoll = engine.Settings.PreRollSeconds;
        Expect(failures, engine.Phase == EclipseEventPhase.Idle, "phase before the event should be Idle");
        events.Clear();

        // Force: a real clock jump to S - PreRoll, then exactly one Resync.
        Expect(failures, engine.ForceEclipseNow(clock), "force failed");
        Expect(failures, Math.Abs(clock.Seconds - (S - preRoll)) < 1e-9 && clock.Seeks == 1, $"force target {clock.Seconds} vs {S - preRoll}");
        engine.Update(clock, 0d, Pose());
        Expect(failures, events.Count == 1 && events[0].Kind == EclipseEventKind.Resync, $"force should emit one Resync, got {events.Count}");
        Expect(failures, engine.Phase == EclipseEventPhase.PreRoll, $"after force phase {engine.Phase}");
        events.Clear();
        engine.Update(clock, 0d, Pose());
        Expect(failures, events.Count == 0, "no events on a stationary clock");

        // Jump to every phase: lands exactly on (not before) the boundary and reports that phase.
        foreach (EclipsePhaseBoundary b in engine.Timeline!.Boundaries)
        {
            if (b.Phase == EclipseEventPhase.Idle) continue;
            events.Clear();
            Expect(failures, engine.JumpToPhase(clock, b.Phase), $"jump {b.Phase} failed");
            engine.Update(clock, 0d, Pose());
            Expect(failures, engine.Phase == b.Phase, $"jump to {b.Phase} reported {engine.Phase}");
            Expect(failures, clock.Seconds - S >= b.StartTau && clock.Seconds - S - b.StartTau < 1e-6, $"jump {b.Phase} landed off the boundary");
            Expect(failures, events.Count == 1 && events[0].Kind == EclipseEventKind.Resync && events[0].Phase == b.Phase, $"jump {b.Phase} events {events.Count}");
        }
        Expect(failures, engine.JumpToPhase(clock, EclipseEventPhase.Idle), "jump Idle failed");
        engine.Update(clock, 0d, Pose());
        Expect(failures, engine.Phase == EclipseEventPhase.Idle, "jump to Idle should leave the event");

        // Scrub: clamps to the range and Resyncs.
        events.Clear();
        Expect(failures, engine.TryGetScrubRange(out double lo, out double hi) && lo == -preRoll && Math.Abs(hi - (E - S + 120d)) < 1e-9, "scrub range");
        Expect(failures, engine.ScrubToTau(clock, 500d), "scrub failed");
        engine.Update(clock, 0d, Pose());
        Expect(failures, Math.Abs(clock.Seconds - (S + 500d)) < 1e-6 && engine.Phase == EclipseEventPhase.Sustain, $"scrub 500 -> {engine.Phase}");
        Expect(failures, events.Count == 1 && events[0].Kind == EclipseEventKind.Resync, "scrub should emit one Resync");
        engine.ScrubToTau(clock, -99999d);
        Expect(failures, Math.Abs(clock.Seconds - (S - preRoll)) < 1e-6, "scrub low clamp");
        engine.ScrubToTau(clock, 1e9);
        Expect(failures, Math.Abs(clock.Seconds - (E + 120d)) < 1e-6, "scrub high clamp");
        // A tiny scrub (below the 5 s threshold) still resyncs because the debug action marks the discontinuity.
        engine.ScrubToTau(clock, 500d); engine.Update(clock, 0d, Pose()); events.Clear();
        engine.ScrubToTau(clock, 501d); engine.Update(clock, 0d, Pose());
        Expect(failures, events.Count == 1 && events[0].Kind == EclipseEventKind.Resync, "small scrub should still Resync once");
        Expect(failures, !events.Any(e => e.Kind == EclipseEventKind.SiteFired), "scrub must not replay SiteFired");

        // Force when the clock is after the event picks the next eclipse; inside the event it rewinds to this one's pre-roll.
        clock.Seconds = E + 5000d;
        Expect(failures, engine.ForceEclipseNow(clock), "force after event failed");
        engine.Update(clock, 0d, Pose());
        Expect(failures, engine.EventIndex == 1 && engine.Phase == EclipseEventPhase.PreRoll, $"force after event index {engine.EventIndex} phase {engine.Phase}");
        double S1 = engine.Window.Start;
        Expect(failures, S1 > E + 20_000d && S1 < E + 30_000d, "next window one orbit later");
        // Freeze toggles the stub clock's pause flag only.
        EclipseEventEngine.SetFrozen(clock, true);
        Expect(failures, clock.Paused, "freeze");
        EclipseEventEngine.SetFrozen(clock, false);
        Expect(failures, !clock.Paused, "unfreeze");
    }

    static void CheckResyncOnExternalJump(List<string> failures)
    {
        EclipseEventEngine engine = MakeEngine();
        List<EclipseEvent> events = Collect(engine);
        var clock = new StubClock();
        engine.Update(clock, 0d, Pose());
        double S = engine.Window.Start;
        clock.Seconds = S - 3d;
        engine.Update(clock, 0d, Pose());
        events.Clear();
        for (int i = 0; i < 600; i++) { clock.Advance(1d / 60d); engine.Update(clock, i / 60d, Pose()); }
        Expect(failures, events.Count(e => e.Kind == EclipseEventKind.Resync) == 0 && events.Any(e => e.Kind == EclipseEventKind.PhaseChanged), "continuous play should not Resync");
        events.Clear();
        clock.SeekSimulationSeconds(S + 1500d); // a save/load or the F9 time buttons
        engine.Update(clock, 0d, Pose());
        Expect(failures, events.Count == 1 && events[0].Kind == EclipseEventKind.Resync, $"external jump events {events.Count}");
        events.Clear();
        clock.SeekSimulationSeconds(S + 100d); // rewind
        engine.Update(clock, 0d, Pose());
        Expect(failures, events.Count == 1 && events[0].Kind == EclipseEventKind.Resync, "rewind should Resync once");
        Expect(failures, engine.ResyncCount >= 2, "resync counter");
    }

    static void CheckTimeScaleFollowing(List<string> failures)
    {
        // Frame count to reach Ignition (tau 29.5) and Crescendo (tau 181.0) from tau 0 equals the 1x time divided by k, within one frame.
        foreach (float k in new[] { 1f, 2f, 10f, 100f })
        {
            EclipseEventEngine engine = MakeEngine();
            List<EclipseEvent> events = Collect(engine);
            var clock = new StubClock { Scale = k };
            engine.Update(clock, 0d, Pose());
            double S = engine.Window.Start;
            clock.Seconds = S; engine.Update(clock, 0d, Pose()); events.Clear();
            Expect(failures, engine.Phase == EclipseEventPhase.Darkness, $"x{k}: start phase");
            long ignitionFrame = -1, crescendoFrame = -1;
            for (long f = 1; f < 200_000 && crescendoFrame < 0; f++)
            {
                clock.Advance(1d / 60d); engine.Update(clock, f / 60d, Pose());
                if (ignitionFrame < 0 && engine.Phase >= EclipseEventPhase.Ignition) ignitionFrame = f;
                if (crescendoFrame < 0 && engine.Phase >= EclipseEventPhase.Crescendo) crescendoFrame = f;
            }
            double tl = engine.Timeline!.TauIgnition, tc = engine.Timeline.TauCrescendo;
            Expect(failures, Math.Abs(ignitionFrame / 60d - tl / k) <= 1d / 60d + 1e-9, $"x{k}: ignition at {ignitionFrame / 60d:F3}s real, expected {tl / k:F3}");
            Expect(failures, Math.Abs(crescendoFrame / 60d - tc / k) <= 1d / 60d + 1e-9, $"x{k}: crescendo at {crescendoFrame / 60d:F3}s real, expected {tc / k:F3}");
            Expect(failures, !events.Any(e => e.Kind == EclipseEventKind.Resync), $"x{k}: frame stepping must not Resync");
            int fired = events.Count(e => e.Kind == EclipseEventKind.PhaseChanged);
            Expect(failures, fired >= 4, $"x{k}: phase events {fired}");
        }

        // The Resync threshold follows TimeScale: a 6 s step is a jump at 1x but a normal slow frame at 100x.
        foreach ((float k, bool resync) in new[] { (1f, true), (10f, true), (20f, false), (100f, false) })
        {
            EclipseEventEngine engine = MakeEngine();
            List<EclipseEvent> events = Collect(engine);
            var clock = new StubClock { Scale = k };
            engine.Update(clock, 0d, Pose());
            double S = engine.Window.Start;
            clock.Seconds = S + 1000d; engine.Update(clock, 0d, Pose()); events.Clear();
            clock.Seconds += 6d; engine.Update(clock, 0d, Pose());
            bool got = events.Any(e => e.Kind == EclipseEventKind.Resync);
            Expect(failures, got == resync, $"x{k}: 6 s step resync={got}, expected {resync}");
        }
    }

    static void CheckGridHook(List<string> failures)
    {
        EclipseEventEngine engine = MakeEngine();
        var clock = new StubClock();
        engine.Update(clock, 0d, Pose());
        double S = engine.Window.Start;
        Expect(failures, !engine.GridClockOverrideActive, "grid hook off before the event");
        clock.Seconds = S + 10d; engine.Update(clock, 77d, Pose());
        Expect(failures, !engine.GridClockOverrideActive && engine.GridTimeSeconds == 77d, "grid hook off during Darkness (weather time)");
        clock.Seconds = S + 25d; engine.Update(clock, 77d, Pose());
        Expect(failures, engine.GridClockOverrideActive && engine.GridTimeSeconds == clock.Seconds, "grid hook on from MainFire, celestial time");
        clock.Seconds = S + 3000d; engine.Update(clock, 77d, Pose());
        Expect(failures, engine.GridClockOverrideActive && engine.State.GridPulseWeight >= 0d, "grid hook on in Sustain");
        clock.Seconds = engine.Window.End + 1d; engine.Update(clock, 77d, Pose());
        Expect(failures, !engine.GridClockOverrideActive && engine.GridTimeSeconds == 77d, "grid hook off after the window");

        EclipseEventEngine weather = MakeEngine(new EclipseEventSettings { RefuelClock = EclipseRefuelClock.Weather });
        weather.Update(clock, 0d, Pose());
        clock.Seconds = weather.Window.Start + 3000d; weather.Update(clock, 55d, Pose());
        Expect(failures, !weather.GridClockOverrideActive && weather.GridTimeSeconds == 55d, "weather refuel clock keeps the grid on the weather clock");
    }

    static void CheckLocalContacts(List<string> failures)
    {
        EclipseEventEngine engine = MakeEngine();
        var clock = new StubClock();
        engine.Update(clock, 0d, Pose(0d));
        double S = engine.Window.Start, E = engine.Window.End;
        clock.Seconds = S + 100d; engine.Update(clock, 0d, Pose(0d));
        Expect(failures, double.IsFinite(engine.LocalSecondContact) && double.IsFinite(engine.LocalThirdContact), "local contacts should be finite");
        Expect(failures, engine.LocalSecondContact <= S + 1e-6 && engine.LocalThirdContact >= E - 1e-6, "ring window must lie inside the local totality");
        Expect(failures, engine.LocalSecondContact > S - 600d && engine.LocalThirdContact < E + 600d, "local contacts within the sweep spread");
        // No pose: contacts unknown, event still runs.
        EclipseEventEngine blind = MakeEngine();
        blind.Update(clock, 0d, EclipseObserverPose.None);
        Expect(failures, double.IsNaN(blind.LocalSecondContact) && blind.Active, "no pose -> no local contacts");
    }

    static void CheckDeterminism(List<string> failures)
    {
        EclipseEventEngine stepped = MakeEngine();
        var clock = new StubClock();
        stepped.Update(clock, 0d, Pose());
        double S = stepped.Window.Start;
        clock.Seconds = S + 40d;
        for (int i = 0; i < 3000; i++) { clock.Advance(1d / 60d); stepped.Update(clock, 5d, Pose()); }
        EclipseEventEngine loaded = MakeEngine();
        var loadClock = new StubClock { Seconds = clock.Seconds };
        loaded.Update(loadClock, 5d, Pose());
        Expect(failures, stepped.State.Equals(loaded.State), "restoring the same clock value must reproduce the same state");
        Expect(failures, stepped.Phase == loaded.Phase && stepped.Phase != EclipseEventPhase.Idle, "restored phase");
    }

    static void CheckPauseAndDisabled(List<string> failures)
    {
        EclipseEventEngine engine = MakeEngine();
        List<EclipseEvent> events = Collect(engine);
        var clock = new StubClock();
        engine.Update(clock, 0d, Pose());
        clock.Seconds = engine.Window.Start + 200d; engine.Update(clock, 0d, Pose());
        events.Clear();
        clock.IsPaused = true;
        EclipseEventState before = engine.State;
        for (int i = 0; i < 120; i++) { clock.Advance(1d / 60d); engine.Update(clock, i / 60d, Pose()); }
        Expect(failures, events.Count == 0 && before.Equals(engine.State), "a paused celestial clock freezes the event");

        EclipseEventEngine disabled = MakeEngine(new EclipseEventSettings { EventEnabled = false });
        var clock2 = new StubClock { Seconds = 12_100d };
        disabled.Update(clock2, 0d, Pose());
        Expect(failures, !disabled.Active && disabled.Phase == EclipseEventPhase.Idle, "disabled event stays Idle");
        Expect(failures, !disabled.ForceEclipseNow(clock2) && !disabled.JumpToPhase(clock2, EclipseEventPhase.Sustain) && clock2.Seeks == 0, "disabled debug actions refuse");
        Expect(failures, disabled.DescribeStatus().Contains("disabled", StringComparison.Ordinal), "disabled status text");
    }

    static void CheckAsyncSolve(List<string> failures)
    {
        EclipseEventEngine engine = MakeEngine(sync: false);
        var clock = new StubClock { Seconds = 12_500d };
        engine.Update(clock, 0d, Pose());
        Expect(failures, engine.SolverState == EclipseSolverState.Pending || engine.SolverState == EclipseSolverState.Cached, "async solver state");
        bool ready = false;
        for (int i = 0; i < 400 && !ready; i++)
        {
            System.Threading.Thread.Sleep(10);
            engine.Update(clock, 0d, Pose());
            ready = engine.SolverState == EclipseSolverState.Cached && engine.Timeline != null;
        }
        Expect(failures, ready, "async window never arrived");
        Expect(failures, engine.Phase != EclipseEventPhase.Idle || engine.Window.IsValid, "async timeline active");
        EclipseWindow sync = new EclipseWindowSolver().Solve(0);
        Expect(failures, engine.Window.Start == sync.RingStart && engine.Window.End == sync.RingEnd, "async window equals the main-thread solve");
    }

    static void CheckStatusAndStrip(List<string> failures)
    {
        EclipseEventEngine engine = MakeEngine();
        var clock = new StubClock();
        engine.Update(clock, 0d, Pose());
        clock.Seconds = engine.Window.Start + 100d; engine.Update(clock, 3d, Pose());
        string status = engine.DescribeStatus();
        foreach (string needle in new[] { "Eclipse k=0", "phase Ignition", "hub L", "beams fired", "TimeScale 1x", "solver Cached", "local 2nd" })
            Expect(failures, status.Contains(needle, StringComparison.Ordinal), $"status missing '{needle}': {status}");
        string strip = engine.BuildRingStrip(50);
        Expect(failures, strip.Length == 52 && strip[0] == '[' && strip[^1] == ']' && strip.Any(c => c != ' ' && c != '[' && c != ']'), $"strip '{strip}'");
        clock.Seconds = engine.Window.Start + 3000d; engine.Update(clock, 3d, Pose());
        Expect(failures, engine.DescribeStatus().Contains("phase Sustain", StringComparison.Ordinal), "sustain status");
        Expect(failures, engine.BuildRingStrip(50).Count(c => c == '@') >= 40, "all sites fired in Sustain");
        clock.Scale = 25f; engine.Update(clock, 3d, Pose());
        Expect(failures, engine.DescribeStatus().Contains("TimeScale 25x", StringComparison.Ordinal), "status shows the effective TimeScale");
    }

    static void CheckSceneAndNode(List<string> failures)
    {
        string scene = File.ReadAllText(ProjectSettings.GlobalizePath("res://scenes/VoxelRingIntegrationTest.tscn"));
        Expect(failures, scene.Contains("[node name=\"EclipseEvent\" type=\"Node\" parent=\".\"]", StringComparison.Ordinal)
            && scene.Contains("res://scripts/Celestial/Eclipse/EclipseEventDirector.cs", StringComparison.Ordinal), "scene is missing the EclipseEvent director node");

        SceneTree tree = (Engine.GetMainLoop() as SceneTree) ?? throw new InvalidOperationException("scene tree unavailable");
        var director = new EclipseEventDirector { Name = "EclipseEvent", AutoBind = false, LoadSavedDefaults = false };
        tree.Root.AddChild(director);
        try
        {
            Expect(failures, director.ProcessPriority == -99, "director priority must be -99 (after RingDayNightCycle at -100)");
            Expect(failures, !director.Bound && director.Phase == EclipseEventPhase.Idle && director.DescribeStatus().Contains("unavailable", StringComparison.Ordinal), "unbound director");
            Expect(failures, !director.ForceEclipseNow() && !director.JumpToPhase(EclipseEventPhase.Sustain) && !director.ScrubToTau(10d), "director debug actions without a clock");
            using var s = new GameplayEclipseEventSettings { PreRollSeconds = 123f, EventEnabled = true, KindleLevel = 0.2f, RefuelClock = 1 };
            director.Configure(s);
            Expect(failures, director.Engine.Settings.PreRollSeconds == 123d && director.Engine.Settings.RefuelClock == EclipseRefuelClock.Weather
                && Math.Abs(director.Engine.Settings.KindleLevel - 0.2d) < 1e-6, "Configure should reach the engine");
            var seen = new List<EclipseEvent>();
            director.EventRaised += seen.Add;
            director.Engine.SetSolver(new EclipseWindowSolver());
            director.Engine.SynchronousSolve = true;
            director.Engine.Bind(EclipseSiteTable.Uniform(Circumference), new EmitterGridPulseClock(1337, Circumference), 1337);
            var clock = new StubClock { Seconds = director.Engine.Solver!.CentreTime(0) };
            director.Engine.Update(clock, 0d, Pose());
            Expect(failures, seen.Count == 1 && seen[0].Kind == EclipseEventKind.Resync, "director should relay the engine's Resync");
        }
        finally { director.QueueFree(); }
    }
}

