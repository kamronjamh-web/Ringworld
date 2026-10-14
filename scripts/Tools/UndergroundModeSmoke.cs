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
            CaveProfilePolicyChecks(failures);
            FogCouplingChecks(failures);
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

    /// <summary>Stand-in for the terrain backend: counts how often the controller would change the override.</summary>
    private sealed class FakeCaveTarget
    {
        public CaveLodProfile? Applied;
        public int SetCalls;
        public void Drive(CaveLodProfilePolicy policy, double delta, UndergroundMode mode, bool disabled, in CaveLodProfile profile, float delay)
        {
            CaveLodProfile? wanted = policy.Step(delta, mode, disabled, profile, delay);
            if (wanted != Applied) { Applied = wanted; SetCalls++; }
        }
    }

    private static void CaveProfilePolicyChecks(List<string> fail)
    {
        CaveLodProfile profile = CaveLodProfile.UndergroundDefault;
        if (profile != new CaveLodProfile(2, 2, 1, 2, 3f, 12)) fail.Add("underground default profile changed");
        // Entering applies on the first frame.
        var policy = new CaveLodProfilePolicy();
        var target = new FakeCaveTarget();
        target.Drive(policy, Frame, UndergroundMode.Surface, false, profile, 10f);
        if (target.SetCalls != 0 || target.Applied != null || policy.Active) fail.Add("surface before entering applied the cave profile");
        target.Drive(policy, Frame, UndergroundMode.Entering, false, profile, 10f);
        if (target.SetCalls != 1 || target.Applied != profile) fail.Add($"entering did not apply the profile on its first frame (sets={target.SetCalls})");
        for (int i = 0; i < 120; i++) target.Drive(policy, Frame, UndergroundMode.Underground, false, profile, 10f);
        if (target.SetCalls != 1) fail.Add($"steady underground reapplied the profile (sets={target.SetCalls})");
        // Surface keeps the profile for the whole delay, then clears once.
        int surfaceFrames = 0;
        while (target.Applied != null && surfaceFrames < 2000) { target.Drive(policy, Frame, UndergroundMode.Surface, false, profile, 10f); surfaceFrames++; }
        double cleared = surfaceFrames * Frame;
        if (cleared < 10.0 - 1.5 * Frame || cleared > 10.0 + 1.5 * Frame) fail.Add($"profile cleared after {cleared:0.000}s instead of 10 s");
        if (target.SetCalls != 2 || policy.Active) fail.Add($"shrink delay expiry calls={target.SetCalls} active={policy.Active}");
        for (int i = 0; i < 60; i++) target.Drive(policy, Frame, UndergroundMode.Surface, false, profile, 10f);
        if (target.SetCalls != 2) fail.Add("surface after expiry reapplied the profile");
        // Re-entry inside the delay resets the timer and does not touch the target.
        policy = new CaveLodProfilePolicy();
        target = new FakeCaveTarget();
        target.Drive(policy, Frame, UndergroundMode.Underground, false, profile, 10f);
        for (int i = 0; i < 60 * 5; i++) target.Drive(policy, Frame, UndergroundMode.Surface, false, profile, 10f);
        target.Drive(policy, Frame, UndergroundMode.Entering, false, profile, 10f);
        if (target.SetCalls != 1 || policy.SurfaceSeconds != 0 || target.Applied != profile) fail.Add($"re-entry in delay calls={target.SetCalls} surfaceSeconds={policy.SurfaceSeconds}");
        for (int i = 0; i < 60 * 9; i++) target.Drive(policy, Frame, UndergroundMode.Surface, false, profile, 10f);
        if (target.Applied == null || target.SetCalls != 1) fail.Add("timer was not reset by re-entry");
        // Master toggle off reverts immediately and stays reverted.
        target.Drive(policy, Frame, UndergroundMode.Underground, true, profile, 10f);
        if (target.Applied != null || target.SetCalls != 2 || policy.Active) fail.Add("disabling did not revert the profile immediately");
        target.Drive(policy, Frame, UndergroundMode.Underground, true, profile, 10f);
        if (target.SetCalls != 2) fail.Add("disabled controller kept setting the profile");
        // Editing the profile while active reapplies exactly once.
        policy = new CaveLodProfilePolicy();
        target = new FakeCaveTarget();
        for (int i = 0; i < 10; i++) target.Drive(policy, Frame, UndergroundMode.Underground, false, profile, 10f);
        CaveLodProfile edited = profile with { CoarseRadiusAround = 3 };
        for (int i = 0; i < 10; i++) target.Drive(policy, Frame, UndergroundMode.Underground, false, edited, 10f);
        if (target.SetCalls != 2 || target.Applied != edited) fail.Add($"profile edit calls={target.SetCalls}");
        // A zero delay clears on the first surface frame.
        policy = new CaveLodProfilePolicy();
        target = new FakeCaveTarget();
        target.Drive(policy, Frame, UndergroundMode.Underground, false, profile, 0f);
        target.Drive(policy, Frame, UndergroundMode.Surface, false, profile, 0f);
        if (target.Applied != null) fail.Add("zero shrink delay kept the profile");
    }

    private static void FogCouplingChecks(List<string> fail)
    {
        // Density gives 2% transmittance at the visible distance, and the end fraction clamps to 0.5..1.
        float visible = UndergroundFogCoupling.VisibleDistance(1024f, .9f);
        float transmittance = MathF.Exp(-UndergroundFogCoupling.DensityForVisibleDistance(visible) * visible);
        if (Math.Abs(transmittance - .02f) > 1e-4f) fail.Add($"fog transmittance at the visible distance was {transmittance:0.00000}");
        if (UndergroundFogCoupling.VisibleDistance(1000f, .1f) != 500f || UndergroundFogCoupling.VisibleDistance(1000f, 5f) != 1000f) fail.Add("fog end fraction did not clamp to 0.5..1");
        // Deep caves only: no coupling without the latch, however deep; spatial fade 0 at release depth and 1 at +32 m.
        const float release = 192f;
        if (UndergroundFogCoupling.TargetCoupling(false, 1000f, release) != 0f) fail.Add("coupling target nonzero without the coarse latch");
        if (UndergroundFogCoupling.TargetCoupling(true, release, release) != 0f || UndergroundFogCoupling.TargetCoupling(true, release + 32f, release) != 1f) fail.Add("spatial fade is not 0 at the release depth and 1 at release + 32 m");
        var shallow = new UndergroundFogCoupling();
        for (int i = 0; i < 300; i++) shallow.Step(Frame, true, false, 100f, release, 0f, 1024f, .9f);
        if (shallow.CouplingWeight != 0f) fail.Add($"shallow cave fog weight {shallow.CouplingWeight:0.000}");
        // Latching on at depth fades in smoothly instead of popping.
        var rise = new UndergroundFogCoupling();
        for (int i = 0; i < 60; i++) rise.Step(Frame, true, false, 300f, release, 0f, 1024f, .9f);
        float previous = rise.CouplingWeight, maxJump = 0f;
        for (int i = 0; i < 600; i++)
        {
            rise.Step(Frame, true, true, 300f, release, 0f, 1024f, .9f);
            maxJump = MathF.Max(maxJump, Math.Abs(rise.CouplingWeight - previous));
            previous = rise.CouplingWeight;
        }
        if (maxJump > .05f) fail.Add($"fog weight jumped {maxJump:0.000} in one frame after latching");
        if (rise.CouplingWeight < .999f) fail.Add($"fog weight settled at {rise.CouplingWeight:0.000} instead of 1");
        // Ascending at 10 m/s: the spatial fade finishes before the release depth.
        var ascent = new UndergroundFogCoupling();
        for (int i = 0; i < 300; i++) ascent.Step(Frame, true, true, 300f, release, 0f, 1024f, .9f);
        float depth = 300f;
        bool latched = true;
        while (depth > release - 20f)
        {
            depth -= 10f * (float)Frame;
            latched = depth > release;
            ascent.Step(Frame, true, latched, depth, release, 0f, 1024f, .9f);
            if (depth <= release && ascent.CouplingWeight > .05f) { fail.Add($"fog weight {ascent.CouplingWeight:0.000} at the release depth while ascending"); break; }
        }
        // Filled reach is sticky against ready drops at the same radius, grows with the radius, takes the minimum on shrink.
        if (UndergroundFogCoupling.NextCoarseFilledReach(0f, true, 10, 10, 4096f) != 4096f) fail.Add("filled reach not granted once every desired sector is ready");
        if (UndergroundFogCoupling.NextCoarseFilledReach(4096f, true, 10, 3, 4096f) != 4096f) fail.Add("filled reach dropped on a transient ready drop");
        if (UndergroundFogCoupling.NextCoarseFilledReach(4096f, true, 10, 3, 6144f) != 4096f) fail.Add("filled reach grew before the larger ring filled");
        if (UndergroundFogCoupling.NextCoarseFilledReach(6144f, true, 10, 3, 4096f) != 4096f) fail.Add("filled reach did not take the minimum on shrink");
        if (UndergroundFogCoupling.NextCoarseFilledReach(4096f, false, 10, 10, 4096f) != 0f || UndergroundFogCoupling.NextCoarseFilledReach(4096f, true, 0, 0, 4096f) != 0f) fail.Add("filled reach not zero when inactive or nothing desired");
        // Unfilled floor, then a monotonic, slow reach filter that shrinks faster than it grows.
        var reach = new UndergroundFogCoupling();
        reach.Step(Frame, true, true, 400f, release, 0f, 1024f, .9f);
        if (Math.Abs(reach.ReachMeters - 1024f) > 1f) fail.Add($"unfilled reach was {reach.ReachMeters:0.0}");
        for (int i = 0; i < 120; i++) reach.Step(Frame, true, true, 400f, release, 0f, 1024f, .9f);
        float lastLog = MathF.Log(reach.ReachMeters), maxStep = 0f;
        int growFrames = (int)(5 * UndergroundFogCoupling.ReachGrowSeconds / Frame);
        for (int i = 0; i < growFrames; i++)
        {
            reach.Step(Frame, true, true, 400f, release, 4096f, 1024f, .9f);
            float log = MathF.Log(reach.ReachMeters);
            if (log < lastLog - 1e-6f) { fail.Add("reach shrank while growing"); break; }
            maxStep = MathF.Max(maxStep, log - lastLog);
            lastLog = log;
        }
        if (maxStep > .06f) fail.Add($"reach changed {maxStep:0.0000} ln per frame");
        if (Math.Abs(reach.ReachMeters / 4096f - 1f) > .01f) fail.Add($"reach was {reach.ReachMeters:0.0} after 5 tau");
        int shrinkFrames = 0;
        while (reach.ReachMeters > 1024f * 1.0067f && shrinkFrames < 2000) { reach.Step(Frame, true, true, 400f, release, 0f, 1024f, .9f); shrinkFrames++; }
        if (shrinkFrames >= growFrames) fail.Add($"reach shrink took {shrinkFrames} frames, not faster than growth ({growFrames})");
        // Disabled gives no fog at all; NaN inputs stay finite.
        reach.Step(Frame, false, true, 400f, release, 4096f, 1024f, .9f);
        if (reach.Density != 0f || reach.CouplingWeight != 0f) fail.Add("disabled fog coupling still produced density or weight");
        var junk = new UndergroundFogCoupling();
        for (int i = 0; i < 5; i++) junk.Step(double.NaN, true, true, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN);
        junk.Step(Frame, true, true, 400f, release, float.PositiveInfinity, float.NaN, float.NaN);
        if (!float.IsFinite(junk.Density) || !float.IsFinite(junk.CouplingWeight) || !float.IsFinite(junk.ReachMeters) || !float.IsFinite(junk.VisibleDistanceMeters)) fail.Add("fog coupling produced a non-finite value from NaN inputs");
    }

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
