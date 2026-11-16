using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Solar cloud-history policy (perf pass E1, 2026-11-16): RingSunshineCloudBridge.AdvanceSunHistoryMotion and
/// ResolveSunHistorySuppression are pure. The stabilised sun (RingDayNightCycle steps of 0.0466 degrees every ~0.58 s at
/// 75 degrees elevation with a 0.05 degree step) must read as its true ~0.08 deg/s at any frame rate and never refresh
/// Sunshine; continuous fast motion still suppresses and refreshes once; stepped fast motion refreshes once instead of
/// every step; a jump of the discontinuity size or more is immediate; a zero or invalid delta never gives an unbounded speed.
/// Run: tools/sun_history_motion_test.gd (headless). Prints SUN_HISTORY_MOTION=PASS.
/// </summary>
public partial class SunHistoryMotionTest : RefCounted
{
    private const float Threshold = 3.0f;       // MinimumHistorySunRotationDegreesPerSecond default
    private const float Discontinuity = 8.0f;   // SunHistoryDiscontinuityDegrees default

    private static readonly Vector3 Axis = new Vector3(0.2f, 1.0f, 0.1f).Normalized();
    // Perpendicular to the axis, so a rotation by a degrees moves the direction by exactly a degrees.
    private static readonly Vector3 Start = (new Vector3(1.0f, 0.3f, -0.2f) - Axis * Axis.Dot(new Vector3(1.0f, 0.3f, -0.2f))).Normalized();
    private static Vector3 SunAt(double degrees) => new Basis(Axis, Mathf.DegToRad((float)degrees)) * Start;

    private sealed record SimResult(int Refreshes, float MaxSuppression, List<float> CommitSpeeds, int SuppressedFrames, int Frames, bool AllFinite);

    // Feeds a sun-angle sequence through the bridge's policy exactly as UpdateSunTemporalHistory does.
    private static SimResult Simulate(Func<int, double> angleAtFrame, Func<int, double> deltaAtFrame, int frames)
    {
        var state = new RingSunshineCloudBridge.SunHistoryMotionState();
        bool was = false;
        int refreshes = 0, suppressedFrames = 0;
        float maxSuppression = 0f;
        bool finite = true;
        var commitSpeeds = new List<float>();
        double lastAngle = double.NaN;
        for (int f = 0; f < frames; f++)
        {
            double angle = angleAtFrame(f);
            float speed = RingSunshineCloudBridge.AdvanceSunHistoryMotion(ref state, SunAt(angle), deltaAtFrame(f), Discontinuity, out float distance);
            finite &= float.IsFinite(speed) && float.IsFinite(distance);
            if (!double.IsNaN(lastAngle) && angle != lastAngle) commitSpeeds.Add(speed);
            lastAngle = angle;
            float suppression = RingSunshineCloudBridge.ResolveSunHistorySuppression(speed, distance, Threshold, Discontinuity);
            maxSuppression = Math.Max(maxSuppression, suppression);
            bool suppressing = suppression >= 0.5f;
            if (suppressing) suppressedFrames++;
            if (suppressing && !was) refreshes++;
            was = suppressing;
        }
        return new SimResult(refreshes, maxSuppression, commitSpeeds, suppressedFrames, frames, finite);
    }

    public int Run(bool verbose)
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            // 1. Stabilised sun at the user's settings: 0.0466 deg every 0.58 s, frames of 9.4 ms (~106 fps), 20 s.
            const double step = 0.0466, period = 0.58, frame = 0.0094;
            SimResult stepped = Simulate(f => Math.Floor(f * frame / period) * step, _ => frame, (int)(20.0 / frame));
            Check(stepped.CommitSpeeds.Count >= 30, $"stepped: expected ~34 sun steps, saw {stepped.CommitSpeeds.Count}");
            Check(stepped.CommitSpeeds.All(s => s > 0.07f && s < 0.09f), $"stepped: step speeds must be ~0.08 deg/s, got {string.Join(",", stepped.CommitSpeeds.Take(6).Select(s => s.ToString("0.0000")))}");
            Check(stepped.Refreshes == 0 && stepped.MaxSuppression == 0f, $"stepped: no refresh and zero suppression (refreshes {stepped.Refreshes}, max {stepped.MaxSuppression})");
            // The same step over one frame (the old per-frame estimate) is above the refresh cutoff: the scenario reproduces E1.
            float perFrame = (float)(step / frame);
            Check(RingSunshineCloudBridge.ResolveSunHistorySuppression(perFrame, (float)step, Threshold, Discontinuity) >= 0.5f, $"scenario check: per-frame estimate {perFrame} deg/s must have refreshed");
            // Same steps at 240 fps and at 30 fps: frame rate independent.
            foreach (double dt in new[] { 1.0 / 240.0, 1.0 / 30.0 })
            {
                SimResult r = Simulate(f => Math.Floor(f * dt / period) * step, _ => dt, (int)(10.0 / dt));
                Check(r.Refreshes == 0 && r.MaxSuppression == 0f && r.CommitSpeeds.All(s => s > 0.06f && s < 0.1f), $"stepped at dt {dt:0.0000}: speeds {string.Join(",", r.CommitSpeeds.Take(4))}, refreshes {r.Refreshes}");
            }

            // 2. Continuous fast motion, 10 deg/s, moving every frame: suppressed throughout, one refresh.
            SimResult fast = Simulate(f => f * frame * 10.0, _ => frame, 300);
            Check(fast.CommitSpeeds.All(s => Math.Abs(s - 10f) < 0.05f), $"continuous: speed must be 10 deg/s, got {string.Join(",", fast.CommitSpeeds.Take(4))}");
            Check(fast.Refreshes == 1, $"continuous: exactly one refresh, got {fast.Refreshes}");
            Check(fast.SuppressedFrames >= fast.Frames - 1, $"continuous: suppressed on {fast.SuppressedFrames}/{fast.Frames} frames");

            // 3. Stepped fast motion: 6 deg/s at 200 fps with a 0.06 deg stabilisation step (a step every 2 frames).
            const double fastFrame = 0.005, fastStep = 0.06;
            SimResult steppedFast = Simulate(f => (f / 2) * fastStep, _ => fastFrame, 400);
            Check(steppedFast.Refreshes == 1, $"stepped fast: one refresh, not one per step; got {steppedFast.Refreshes}");
            Check(steppedFast.SuppressedFrames >= steppedFast.Frames - 3, $"stepped fast: suppression held between steps ({steppedFast.SuppressedFrames}/{steppedFast.Frames})");

            SimResult irregular = Simulate(f => (f / 4 * 2 + (f % 4 == 3 ? 1 : 0)) * fastStep, _ => fastFrame, 400);
            Check(irregular.Refreshes == 1, $"stepped fast, irregular 1/3-frame cadence: one refresh, got {irregular.Refreshes}");

            // 4. Discontinuities: a 9 deg jump after 10 s of stillness is immediate and refreshes once; a 7.9 deg jump after the
            //    same stillness is neither a discontinuity nor fast; neither is held into the following frames.
            SimResult jump = Simulate(f => f < 1000 ? 0.0 : 9.0, _ => 0.01, 1100);
            Check(jump.Refreshes == 1 && jump.SuppressedFrames == 1, $"9 deg jump: one refresh, suppressed one frame (refreshes {jump.Refreshes}, frames {jump.SuppressedFrames})");
            SimResult near = Simulate(f => f < 1000 ? 0.0 : 7.9, _ => 0.01, 1100);
            Check(near.Refreshes == 0 && near.MaxSuppression == 0f, $"7.9 deg after 10 s still: no refresh (max {near.MaxSuppression})");
            SimResult quickJump = Simulate(f => f < 5 ? f * 0.001 : 9.0 + f * 0.001, _ => 0.01, 50);
            Check(quickJump.Refreshes == 1 && quickJump.SuppressedFrames == 1, $"9 deg jump during slow motion: one refresh, one frame ({quickJump.Refreshes}, {quickJump.SuppressedFrames})");

            // 5. Zero and invalid deltas: no time passes, no unbounded or non-finite speed.
            var s0 = new RingSunshineCloudBridge.SunHistoryMotionState();
            RingSunshineCloudBridge.AdvanceSunHistoryMotion(ref s0, SunAt(0), 0.01, Discontinuity, out _);
            float z = RingSunshineCloudBridge.AdvanceSunHistoryMotion(ref s0, SunAt(0.5), 0.0, Discontinuity, out float zd);
            Check(z == 0f && Math.Abs(zd - 0.5f) < 1e-3f, $"zero delta right after a commit gives speed 0 (got {z}, distance {zd})");
            foreach (double bad in new[] { double.NaN, double.PositiveInfinity, -0.01 })
            {
                float b = RingSunshineCloudBridge.AdvanceSunHistoryMotion(ref s0, SunAt(1.0 + s0.SecondsSinceCommit), bad, Discontinuity, out _);
                Check(float.IsFinite(b) && b < 1000f, $"delta {bad} gives a bounded speed (got {b})");
            }
            SimResult zeroRun = Simulate(f => f * 0.01, f => f % 2 == 0 ? 0.0 : 0.01, 200);
            Check(zeroRun.AllFinite && zeroRun.CommitSpeeds.All(s => s < 2.1f), $"alternating zero deltas: speeds bounded ({zeroRun.CommitSpeeds.Max()})");

            // First observation and stillness.
            var first = new RingSunshineCloudBridge.SunHistoryMotionState();
            Check(RingSunshineCloudBridge.AdvanceSunHistoryMotion(ref first, SunAt(30), 0.01, Discontinuity, out float fd) == 0f && fd == 0f && first.HasDirection, "first observation commits with speed and distance 0");
            SimResult still = Simulate(_ => 3.0, _ => 0.01, 100);
            Check(still.Refreshes == 0 && still.MaxSuppression == 0f, "a still sun never suppresses");
            if (verbose) GD.Print($"stepped speeds {string.Join(",", stepped.CommitSpeeds.Take(5))}; per-frame {perFrame}");
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("SUN_HISTORY_MOTION=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("SUN_HISTORY_MOTION=PASS");
        return 0;
    }
}
