using System;
using System.Collections.Generic;
using RingworldTechDemo.Celestial.Eclipse;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Phase E4d of docs/eclipse-false-sun-spec.md (E1 grid-clock hook, E.3, B.2): the director's override window, over the whole event at TimeScale 1 and
/// 10. <c>GridClockOverrideActive</c> is true exactly from MainFire to E while the refuel clock is Celestial, <c>GridTimeSeconds</c> is the celestial
/// time inside it and the weather clock outside (Return and Idle included), and the normal pulse weight is 0 from MainFire to the end of Hand-off
/// (Sustain starts) only. The flag changes exactly twice per event: no flicker at the edges.
/// </summary>
public partial class EclipseEventDirectorTest
{
    static void CheckGridOverrideWindow(List<string> failures)
    {
        foreach (float scale in new[] { 1f, 10f })
        {
            EclipseEventEngine engine = MakeEngine();
            var clock = new StubClock { Scale = scale };
            engine.Update(clock, 0d, Pose());
            EclipseEventTimeline t = engine.Timeline!;
            double s = engine.Window.Start, weather = 1000d;
            int changes = 0, frames = 0, badOverride = 0, badTime = 0, badWeight = 0, returnFrames = 0, returnBad = 0;
            bool previous = false;
            // Start before the pre-roll and run through the Return tail at real 0.1 s frames (celestial 0.1 * scale per frame).
            clock.Seconds = s - t.Settings.PreRollSeconds - 5d;
            engine.Update(clock, weather, Pose());
            double end = s + t.ReturnEndTau() + 20d;
            while (clock.Seconds < end)
            {
                clock.Advance(0.1d); weather += 0.1d;
                engine.Update(clock, weather, Pose());
                frames++;
                double tau = clock.Seconds - s;
                EclipseEventState st = engine.State;
                bool expectOn = tau >= t.TauMainFire && tau < t.TauEnd;
                if (engine.GridClockOverrideActive != expectOn) badOverride++;
                if (engine.GridClockOverrideActive != previous) { changes++; previous = engine.GridClockOverrideActive; }
                double expectedTime = expectOn ? clock.Seconds : weather;
                if (engine.GridTimeSeconds != expectedTime) badTime++;
                if (st.Active)
                {
                    double expectedWeight = tau >= t.TauMainFire && tau < t.TauSustain ? 0d : 1d;
                    if (st.GridPulseWeight != expectedWeight) badWeight++;
                    if (st.GridTime != clock.Seconds) badTime++; // the timeline always reads the celestial clock (refuel clock Celestial)
                }
                if (st.Phase == EclipseEventPhase.Return) { returnFrames++; if (engine.GridClockOverrideActive || engine.GridTimeSeconds != weather) returnBad++; }
            }
            Expect(failures, frames > 1000 && badOverride == 0, $"x{scale}: override flag wrong in {badOverride}/{frames} frames");
            Expect(failures, changes == 2, $"x{scale}: override flag changed {changes} times, expected 2 (on at MainFire, off at E)");
            Expect(failures, badTime == 0, $"x{scale}: GridTimeSeconds/State.GridTime wrong in {badTime} frames");
            Expect(failures, badWeight == 0, $"x{scale}: normal pulse weight wrong in {badWeight} frames");
            Expect(failures, returnFrames > 20 && returnBad == 0, $"x{scale}: Return phase not on the weather clock ({returnBad}/{returnFrames})");

            // Exact edges.
            clock.Seconds = BitDecrement(s + t.TauMainFire); engine.Update(clock, 7d, Pose());
            Expect(failures, !engine.GridClockOverrideActive && engine.GridTimeSeconds == 7d && engine.State.GridPulseWeight == 1d, $"x{scale}: just before MainFire");
            clock.Seconds = s + t.TauMainFire; while (clock.Seconds - s < t.TauMainFire) clock.Seconds = Math.BitIncrement(clock.Seconds);
            engine.Update(clock, 7d, Pose());
            Expect(failures, engine.GridClockOverrideActive && engine.GridTimeSeconds == clock.Seconds && engine.State.GridPulseWeight == 0d, $"x{scale}: at MainFire");
            clock.Seconds = s + t.TauSustain; while (clock.Seconds - s < t.TauSustain) clock.Seconds = Math.BitIncrement(clock.Seconds);
            engine.Update(clock, 7d, Pose());
            Expect(failures, engine.GridClockOverrideActive && engine.State.GridPulseWeight == 1d && engine.State.Phase == EclipseEventPhase.Sustain, $"x{scale}: at Sustain the normal pulse returns on the celestial clock");
            clock.Seconds = s + t.TauEnd; while (clock.Seconds - s < t.TauEnd) clock.Seconds = Math.BitIncrement(clock.Seconds);
            engine.Update(clock, 7d, Pose());
            Expect(failures, !engine.GridClockOverrideActive && engine.GridTimeSeconds == 7d && engine.State.Phase == EclipseEventPhase.Return, $"x{scale}: at E the weather clock returns");

            // The settings that opt out: the weather refuel clock never overrides; SuppressGridPulseDuringIgnition=false keeps the normal pulse.
            EclipseEventEngine weatherEngine = MakeEngine(new EclipseEventSettings { RefuelClock = EclipseRefuelClock.Weather });
            var clock2 = new StubClock { Scale = scale };
            weatherEngine.Update(clock2, 0d, Pose());
            int weatherOn = 0;
            for (double tau = -10d; tau < 4200d; tau += 7.3d)
            {
                clock2.Seconds = weatherEngine.Window.Start + tau; weatherEngine.Update(clock2, 9d, Pose());
                if (weatherEngine.GridClockOverrideActive || weatherEngine.GridTimeSeconds != 9d) weatherOn++;
            }
            Expect(failures, weatherOn == 0, $"x{scale}: the weather refuel clock must never override the grid");
            EclipseEventEngine keep = MakeEngine(new EclipseEventSettings { SuppressGridPulseDuringIgnition = false });
            keep.Update(clock2, 0d, Pose());
            clock2.Seconds = keep.Window.Start + keep.Timeline!.TauIgnition + 30d; keep.Update(clock2, 9d, Pose());
            Expect(failures, keep.State.GridPulseWeight == 1d && keep.GridClockOverrideActive, $"x{scale}: suppression off keeps the normal pulse");
        }
    }

    static double BitDecrement(double v) => Math.BitDecrement(v);
}
