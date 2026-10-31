using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Megastorm cloud-history fade (docs/megastorm-design.md 5.6, WO-B): RingSunshineCloudBridge.ResolveMegastormHistoryWeight and the fade slew are pure.
/// Fade 0 returns the input exactly; the fade ramps linearly at 0.25/s (4 s to full), never raises a weight, respects the floor, and snaps to exactly 0.
/// Run: tools/megastorm_history_fade_test.gd (headless). Prints MEGASTORM_HISTORY_FADE=PASS.
/// </summary>
public partial class MegastormHistoryFadeTest : RefCounted
{
    public int Run(bool verbose)
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            const float floor = RingSunshineCloudBridge.MegastormHistoryFloor;
            foreach (float w in new[] { 0f, 0.04f, 0.15f, 0.5f, 0.7f, 0.95f })
            {
                Check(BitConverter.SingleToInt32Bits(RingSunshineCloudBridge.ResolveMegastormHistoryWeight(w, 0f, floor)) == BitConverter.SingleToInt32Bits(w), $"fade 0 must return {w} exactly");
                float full = RingSunshineCloudBridge.ResolveMegastormHistoryWeight(w, 1f, floor);
                Check(Math.Abs(full - Math.Min(floor, w)) < 1e-6f, $"fade 1 gives min(floor, w): {w} -> {full}");
                float prev = w;
                for (float f = 0.05f; f <= 1.0001f; f += 0.05f)
                {
                    float v = RingSunshineCloudBridge.ResolveMegastormHistoryWeight(w, f, floor);
                    Check(v <= prev + 1e-6f && v >= Math.Min(floor, w) - 1e-6f && v <= w + 1e-6f, $"monotonic and floored at w={w} fade={f}: {v}");
                    prev = v;
                }
            }
            Check(RingSunshineCloudBridge.ResolveMegastormHistoryWeight(0.5f, -1f, floor) == 0.5f, "negative fade is off");
            Check(RingSunshineCloudBridge.MegastormHistoryFadeTarget(0.25f) == 0f && RingSunshineCloudBridge.MegastormHistoryFadeTarget(0f) == 0f, "darkness <= 0.25 gives target 0");
            Check(RingSunshineCloudBridge.MegastormHistoryFadeTarget(0.75f) == 1f && Math.Abs(RingSunshineCloudBridge.MegastormHistoryFadeTarget(0.5f) - 0.5f) < 1e-6f, "target ramps 0.25 to 0.75");

            // Slew: 4 s to full at 60 fps, monotonic; 4 s back down; snap to exactly 0.
            float fade = 0f, last = 0f; bool mono = true; int frames = 0;
            while (fade < 1f && frames < 1000) { fade = RingSunshineCloudBridge.StepMegastormHistoryFade(fade, 1f, 1f / 60f); mono &= fade >= last; last = fade; frames++; }
            Check(mono && frames >= 239 && frames <= 241, $"ramp up takes {frames} frames at 60 fps (expected 240)");
            Check(RingSunshineCloudBridge.StepMegastormHistoryFade(1f, 1f, 1f) == 1f, "fade holds at target");
            frames = 0;
            while (fade > 0f && frames < 1000) { float n = RingSunshineCloudBridge.StepMegastormHistoryFade(fade, 0f, 1f / 60f); mono &= n <= fade; fade = n; frames++; }
            Check(mono && fade == 0f && frames >= 239 && frames <= 242, $"ramp down ends at exactly 0 after {frames} frames");
            Check(RingSunshineCloudBridge.StepMegastormHistoryFade(5e-5f, 0f, 0.001f) == 0f, "below 1e-4 with target 0 snaps to 0");
            Check(RingSunshineCloudBridge.StepMegastormHistoryFade(0f, 0f, 1f) == 0f, "0 stays 0");
            Check(RingSunshineCloudBridge.StepMegastormHistoryFade(0.5f, 1f, 0.5f) == 0.625f, "linear 0.25 per second");
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("MEGASTORM_HISTORY_FADE=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("MEGASTORM_HISTORY_FADE=PASS");
        return 0;
    }
}
