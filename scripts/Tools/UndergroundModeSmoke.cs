using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Underground;

namespace RingworldTechDemo.Tools;

/// <summary>Headless checks for the underground enclosure state machine and probe, using synthetic density sources.</summary>
[GlobalClass]
public partial class UndergroundModeSmoke : RefCounted
{
    private const double Frame = 1.0 / 60.0;

    private sealed class SyntheticSource : IUndergroundProbeSource
    {
        public Func<double, double, double, float> Density = (_, _, _) => -1f;
        public Func<double, double, float> Surface = (_, _) => 0f;
        public ulong RevisionValue = 1;
        public bool IsAvailable { get; set; } = true;
        public ulong Revision => RevisionValue;
        public bool TryGetSurfaceHeight(double around, double across, out float height) { height = Surface(around, across); return true; }
        public bool TryGetDensity(double around, double vertical, double across, out float density) { density = Density(around, vertical, across); return true; }
    }

    public Godot.Collections.Dictionary Run()
    {
        var result = new Godot.Collections.Dictionary();
        var failures = new List<string>();
        try
        {
            StateMachineChecks(failures);
            ProbeChecks(failures);
        }
        catch (Exception error) { failures.Add(error.ToString()); }
        result["pass"] = failures.Count == 0;
        result["detail"] = failures.Count == 0 ? "state machine and probe checks passed" : string.Join("; ", failures);
        return result;
    }

    private static UndergroundEnclosureStateMachine Settled()
    {
        var machine = new UndergroundEnclosureStateMachine();
        for (int i = 0; i < 60 * 3; i++) machine.Step(Frame, new UndergroundEnclosureInput(100f, 0f, false));
        return machine;
    }

    private static void StateMachineChecks(List<string> fail)
    {
        // Enter requires the full hold, then the blend reaches 1 in BlendSeconds (+/- one frame).
        var machine = new UndergroundEnclosureStateMachine();
        int events = 0;
        machine.ModeChanged += (_, _) => events++;
        int frames = 0;
        while (machine.Mode == UndergroundMode.Surface && frames < 600) { machine.Step(Frame, new UndergroundEnclosureInput(100f, 0f, false)); frames++; }
        double enterSeconds = frames * Frame;
        if (Math.Abs(enterSeconds - 1.0) > 1.5 * Frame) fail.Add($"enter hold took {enterSeconds:0.000}s");
        int ramp = 0;
        bool suspensionEarly = false;
        while (machine.Mode != UndergroundMode.Underground && ramp < 600)
        {
            suspensionEarly |= machine.SuspensionActive;
            machine.Step(Frame, new UndergroundEnclosureInput(100f, 0f, false));
            ramp++;
        }
        if (Math.Abs(ramp * Frame - 1.0) > 1.5 * Frame) fail.Add($"blend ramp took {ramp * Frame:0.000}s");
        if (suspensionEarly) fail.Add("suspension active before blend reached 1");
        if (!machine.SuspensionActive || machine.EnclosureBlend < .999f) fail.Add("suspension not active once fully underground");
        if (events != 2) fail.Add($"expected 2 mode events entering, saw {events}");

        // Exit within ExitHold; resume (SuspensionActive false) on the exit trigger while blend is still 1.
        int exitFrames = 0;
        while (machine.Mode == UndergroundMode.Underground && exitFrames < 600)
        {
            machine.Step(Frame, new UndergroundEnclosureInput(100f, .5f, false));
            exitFrames++;
        }
        bool resumedWithBlendFull = !machine.SuspensionActive && machine.EnclosureBlend > .99f && machine.Mode == UndergroundMode.Exiting;
        if (Math.Abs(exitFrames * Frame - .15) > 1.5 * Frame) fail.Add($"exit hold took {exitFrames * Frame:0.000}s");
        if (!resumedWithBlendFull) fail.Add("exit did not resume suspension while blend was still 1");
        int exitRamp = 0;
        while (machine.Mode != UndergroundMode.Surface && exitRamp < 600) { machine.Step(Frame, new UndergroundEnclosureInput(100f, .5f, false)); exitRamp++; }
        if (exitRamp * Frame > 1.0 + 1.5 * Frame || machine.EnclosureBlend != 0f) fail.Add($"exit ramp {exitRamp * Frame:0.000}s blend {machine.EnclosureBlend}");
        if (events != 4) fail.Add($"expected 4 mode events after exit, saw {events}");

        // Forced instant exit: blend 0 in one step, one event.
        machine = Settled();
        int forcedEvents = 0;
        machine.ModeChanged += (_, _) => forcedEvents++;
        for (int i = 0; i < 90; i++) machine.Step(Frame, new UndergroundEnclosureInput(100f, 0f, false));
        if (machine.Mode != UndergroundMode.Underground) fail.Add("did not settle underground for forced exit test");
        machine.Step(Frame, new UndergroundEnclosureInput(100f, 0f, true));
        if (machine.Mode != UndergroundMode.Surface || machine.EnclosureBlend != 0f || machine.SuspensionActive || forcedEvents != 1)
            fail.Add($"forced exit mode={machine.Mode} blend={machine.EnclosureBlend} events={forcedEvents}");
        machine.Step(Frame, new UndergroundEnclosureInput(100f, 0f, true));
        if (forcedEvents != 1) fail.Add("forced exit fired a repeat event");
        // Forced exit mid-ramp.
        machine = new UndergroundEnclosureStateMachine();
        for (int i = 0; i < 90; i++) machine.Step(Frame, new UndergroundEnclosureInput(100f, 0f, false));
        if (machine.Mode != UndergroundMode.Entering) fail.Add($"expected Entering mid-ramp, mode={machine.Mode}");
        machine.Step(Frame, new UndergroundEnclosureInput(100f, 0f, true));
        if (machine.Mode != UndergroundMode.Surface || machine.EnclosureBlend != 0f) fail.Add("forced exit mid-ramp did not reset");

        // Hysteresis: 60 Hz +/-0.02 openness around both thresholds for 30 s yields at most one transition.
        machine = new UndergroundEnclosureStateMachine();
        int enterJitter = 0;
        machine.ModeChanged += (_, _) => enterJitter++;
        for (int i = 0; i < 60 * 30; i++) machine.Step(Frame, new UndergroundEnclosureInput(100f, i % 2 == 0 ? .04f : .08f, false));
        if (enterJitter > 1) fail.Add($"enter-threshold jitter caused {enterJitter} transitions");
        machine = Settled();
        int exitJitter = 0;
        machine.ModeChanged += (_, _) => exitJitter++;
        for (int i = 0; i < 60 * 30; i++) machine.Step(Frame, new UndergroundEnclosureInput(100f, i % 2 == 0 ? .16f : .20f, false));
        if (exitJitter > 1) fail.Add($"exit-threshold jitter caused {exitJitter} transitions");
        // Depth jitter around the depth thresholds.
        machine = new UndergroundEnclosureStateMachine();
        int depthJitter = 0;
        machine.ModeChanged += (_, _) => depthJitter++;
        for (int i = 0; i < 60 * 30; i++) machine.Step(Frame, new UndergroundEnclosureInput(i % 2 == 0 ? 22f : 26f, 0f, false));
        if (depthJitter > 1) fail.Add($"depth jitter caused {depthJitter} transitions");
        // The dead band between thresholds never changes an established state.
        machine = Settled();
        for (int i = 0; i < 90; i++) machine.Step(Frame, new UndergroundEnclosureInput(100f, 0f, false));
        for (int i = 0; i < 300; i++) machine.Step(Frame, new UndergroundEnclosureInput(18f, .1f, false));
        if (machine.Mode != UndergroundMode.Underground) fail.Add("dead band left underground mode");

        // Mode override.
        machine = new UndergroundEnclosureStateMachine();
        for (int i = 0; i < 120; i++) machine.Step(Frame, new UndergroundEnclosureInput(0f, 1f, false, UndergroundModeOverride.ForceUnderground));
        if (machine.Mode != UndergroundMode.Underground) fail.Add("force underground override did not reach underground");
        machine.Step(Frame, new UndergroundEnclosureInput(0f, 1f, false, UndergroundModeOverride.ForceSurface));
        if (machine.Mode != UndergroundMode.Surface) fail.Add("force surface override did not exit");
    }

    private static int Sweep(UndergroundEnclosureProbe probe, IUndergroundProbeSource source, double a, double v, double c, out int maxPerFrame, out int frames)
    {
        maxPerFrame = 0;
        frames = 0;
        int total = 0;
        do
        {
            int spent = probe.Update(source, a, v, c);
            maxPerFrame = Math.Max(maxPerFrame, spent);
            total += spent;
            frames++;
        } while (probe.SweepInProgress && frames < 200);
        return total;
    }

    private static float Cavity(double a, double v, double c) =>
        Math.Abs(a - 1) < 20 && Math.Abs(c - 1) < 20 && Math.Abs(v + 100) < 10 ? 1f : -1f;

    private static void ProbeChecks(List<string> fail)
    {
        const double A = .5, V = -100, C = .5;
        // Sealed roof: no ray escapes.
        var source = new SyntheticSource();
        source.Density = (a, v, c) => v > 0 ? 1f : Cavity(a, v, c);
        var probe = new UndergroundEnclosureProbe();
        Sweep(probe, source, A, V, C, out int maxFrame, out _);
        if (probe.Openness != 0f) fail.Add($"sealed roof openness {probe.Openness}");
        if (maxFrame > probe.SampleBudgetPerFrame) fail.Add($"sealed sweep exceeded budget {maxFrame}");

        // Vertical shaft to the surface: only the vertical ray escapes.
        source = new SyntheticSource();
        source.Density = (a, v, c) => v > 0 ? 1f : Math.Abs(a - 1) < 1.5 && Math.Abs(c - 1) < 1.5 && v < 0 ? 1f : Cavity(a, v, c);
        probe = new UndergroundEnclosureProbe();
        Sweep(probe, source, A, V, C, out maxFrame, out int shaftFrames);
        if (probe.Openness < 1f / 17f - 1e-4f || probe.Openness > 3f / 17f) fail.Add($"shaft openness {probe.Openness}");
        if (maxFrame > probe.SampleBudgetPerFrame) fail.Add($"shaft sweep exceeded budget {maxFrame}");

        // Gallery to a hillside mouth: 10 degree rays escape at 200 m, nothing at 600 m (documents the freeze-only tier).
        foreach ((double mouth, bool expectOpen) in new[] { (200.0, true), (600.0, false) })
        {
            double m = mouth;
            source = new SyntheticSource();
            source.Surface = (a, _) => a < m ? 0f : -150f;
            source.Density = (a, v, c) => v > (a < m ? 0.0 : -150.0) ? 1f
                : a > 0 && a < m + 5 && Math.Abs(c - 1) < 20 && v > -130 && v < -30 ? 1f : Cavity(a, v, c);
            probe = new UndergroundEnclosureProbe();
            Sweep(probe, source, A, V, C, out maxFrame, out _);
            if (expectOpen != probe.Openness > 0f) fail.Add($"mouth {mouth} m openness {probe.Openness}");
            if (maxFrame > probe.SampleBudgetPerFrame) fail.Add($"mouth sweep exceeded budget {maxFrame}");
        }

        // Thin 1 m plug between marching samples is a documented fail-safe "open".
        source = new SyntheticSource();
        source.Density = (a, v, c) => v > 0 ? 1f : (Math.Abs(a - 1) < 1.5 && Math.Abs(c - 1) < 1.5 && !(v > -50.6 && v < -49.6) ? 1f : Cavity(a, v, c));
        probe = new UndergroundEnclosureProbe();
        Sweep(probe, source, A, V, C, out _, out _);
        if (probe.Openness <= 0f) fail.Add("1 m plug was not reported open");

        // Cache: same cell and revision costs nothing; revision bump or a new cell resweeps.
        source = new SyntheticSource();
        source.Density = (a, v, c) => v > 0 ? 1f : Cavity(a, v, c);
        probe = new UndergroundEnclosureProbe();
        Sweep(probe, source, A, V, C, out _, out _);
        float sealedOpenness = probe.Openness;
        source.Density = (a, v, c) => 1f;
        int cachedSpend = probe.Update(source, A + .4, V + .4, C + .4);
        if (cachedSpend != 0 || probe.Openness != sealedOpenness || probe.CacheHits == 0) fail.Add($"cache miss for same cell (spent {cachedSpend})");
        source.RevisionValue++;
        Sweep(probe, source, A, V, C, out _, out _);
        if (probe.Openness <= sealedOpenness) fail.Add($"revision bump did not invalidate cache (openness {probe.Openness})");
        long sweepsBefore = probe.TotalSweepsCompleted;
        Sweep(probe, source, A + 10, V, C, out _, out _);
        if (probe.TotalSweepsCompleted != sweepsBefore + 1) fail.Add("new camera cell did not resweep");

        // Unavailable source spends nothing.
        source.IsAvailable = false;
        if (probe.Update(source, A + 50, V, C) != 0) fail.Add("unavailable source was sampled");
    }
}
