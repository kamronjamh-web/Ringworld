using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>
/// WO14 (docs/megastorm-design.md 5.6): the megastorm darkness profile E(xi), its scale and the K multiplier. Profile values at the knots, continuity,
/// DarknessScale, DarknessAt before the trigger, DarknessMultiplier identity, and a different trailing length moving the end.
/// Run: Godot --headless --path . --script res://tools/megastorm_darkness_test.gd. Prints MEGASTORM_DARKNESS=PASS.
/// </summary>
public partial class MegastormDarknessTest : RefCounted
{
    public int Run()
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            var m = new MegastormModel();
            m.Trigger(new MegastormTrigger("t", 1.0e6, 1, 100.0, 350f, 1800f, 1234));
            (double Km, double Expected)[] knots =
            {
                (-60, 0), (-45, 0), (-25, 0.275), (-5, 0.55), (-1.5, 0.775), (2, 1), (10, 1), (25, 1), (27.5, 0.95), (30, 0.9), (45, 0.9),
                (60, 0.9), (70, 0.8), (80, 0.7), (235, 0.35), (390, 0), (450, 0),
            };
            foreach ((double km, double expected) in knots)
                Check(Math.Abs(m.Darkness(km * 1000.0) - expected) < 1e-4, $"E({km} km) = {m.Darkness(km * 1000.0)}, expected {expected}");
            double maxStep = 0.0;
            for (double xi = -60000.0; xi < 400000.0; xi += 7.0)
                maxStep = Math.Max(maxStep, Math.Abs(m.Darkness(xi + 29.0) - m.Darkness(xi)));
            Check(maxStep < 0.0045, $"profile step over 29 m = {maxStep}");

            Check(MegastormModel.DarknessScale(0f, 1f) == 0f, "scale(0, 1) != 0");
            Check(MegastormModel.DarknessScale(0.9f, 1f) == 1f, "scale(0.9, 1) != 1");
            Check(Math.Abs(MegastormModel.DarknessScale(0.45f, 1f) - 0.5f) < 1e-6, "scale(0.45, 1) != 0.5");
            Check(MegastormModel.DarknessScale(0.9f, 0f) == 0f, "scale(0.9, 0) != 0");

            Check(m.DarknessAt(1.0e6, 0.0, 50.0, 0.9f) == 0f, "DarknessAt before the trigger != 0");
            Check(new MegastormModel().DarknessAt(1.0e6, 0.0, 50.0, 0.9f) == 0f, "DarknessAt untriggered != 0");
            double t = 100.0 + 4000.0;
            double xi0 = m.Xi(1.0e6, 100000.0, t);
            float e = m.DarknessAt(1.0e6, 100000.0, t, 0.9f);
            Check(Math.Abs(e - m.Darkness(xi0)) < 1e-6, $"DarknessAt after formation {e} vs profile {m.Darkness(xi0)}");
            Check(m.DarknessAt(1.0e6, 100000.0, t, 0.0f) == 0f, "Storm darkness 0 did not switch E off");

            Check(MegastormModel.DarknessMultiplier(0f, 0.06f) == 1f, "K(0) != 1 exactly");
            Check(Math.Abs(MegastormModel.DarknessMultiplier(1f, 0.06f) - 0.06f) < 1e-6, "K(1, 0.06) != 0.06");
            Check(Math.Abs(MegastormModel.DarknessMultiplier(2f, 0.4f) - 0.4f) < 1e-6, "K clamps extra to 1");

            var shorter = new MegastormModel(new MegastormSettings { TrailingLengthMeters = 100000f });
            shorter.Trigger(new MegastormTrigger("t", 1.0e6, 1, 100.0, 350f, 1800f, 1234));
            Check(Math.Abs(shorter.TrailingEndXi - (m.TrailingEndXi - 240000.0)) < 1e-6, "trailing length did not move the end");
            Check(shorter.Darkness(shorter.TrailingEndXi) == 0f && shorter.Darkness(150000.0) < 0.35f, "shorter trailing profile wrong");
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); Console.Error.WriteLine("MEGASTORM_DARKNESS=FAIL exception " + e.Message); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("MEGASTORM_DARKNESS=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("MEGASTORM_DARKNESS=PASS");
        return 0;
    }
}
