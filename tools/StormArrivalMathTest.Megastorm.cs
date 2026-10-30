using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Megastorm (kind 6) part of the arrival maths test (docs/megastorm-design.md WO6a): the time-based bands of 5.1 (the phase table at 350 m/s within 5%,
/// the 2-20 km / 10-60 km clamps at 25 and 500 m/s, ordinary kinds unchanged) and the wall hail placement.
/// </summary>
public partial class StormArrivalMathTest
{
    const float WallCentreXi = StormArrivalMath.MegastormWallCenterXiMeters;

    /// <summary>The W record (kind 6, half depth 24 km, centre xi +18 km) seen by an observer at xi, travelling at <paramref name="speed"/> (signed around speed).</summary>
    static StormSystemGeometry WallAt(double xi, float speed, float centerAcross = float.NaN, int cycle = 0, float cycleSeconds = 600.0f, int kind = StormArrivalMath.MegastormKindCode)
        => new(MegastormModel.WallRecordId, kind, new Vector2((float)((WallCentreXi - xi) * (speed < 0.0f ? -1.0 : 1.0)), 0.0f), 1.0e7f, 24000.0f, 0.0f, 1.0f, 1.0f, 0.0f,
            new Vector2(speed, 0.0f), 1.0f, 1.0f, RingVolumetricCloudLayer.StormLifecycleMature, centerAcross, cycle, cycleSeconds);

    /// <summary>The rain-edge distance (metres, > 0 ahead) at the first point of a descending scan from <paramref name="fromMeters"/> where <paramref name="test"/> holds.</summary>
    static double FirstAheadMeters(float speed, Func<StormArrivalState, bool> test, double fromMeters = 120000.0, int kind = StormArrivalMath.MegastormKindCode)
    {
        var tuning = StormArrivalTuning.Default;
        for (double d = fromMeters; d >= -2000.0; d -= 25.0)
        {
            StormArrivalState s = StormArrivalMath.Evaluate(WallAt(-d, speed, kind: kind), tuning);
            if (test(s)) return s.RimDistanceMeters;
        }
        return double.NaN;
    }

    static void RunMegastorm(Action<bool, string> Check, bool verbose)
    {
        var tuning = StormArrivalTuning.Default;

        // ---- 5.1 phase table at 350 m/s (the model's own geometry: bow, meander, formation) ----
        {
            var model = new MegastormModel();
            const double arrival = 5000.0, across = 159000.0, around = 12_000_000.0;
            Check(model.TriggerToReach(around, across, arrival, 1, "wo6a", 0.0, 350.0f, 7), "megastorm trigger refused");
            var list = new List<MegastormRecord>(4);
            double circumference = model.Settings.CircumferenceMeters;
            var tFirst = new Dictionary<StormArrivalPhase, double>();
            double lastCore = double.NaN, firstFat = double.NaN, tRain = double.NaN;
            var trace = new List<string>(); var diag = new List<string>();
            StormArrivalPhase last = StormArrivalPhase.Clear;
            var systems = new List<StormSystemGeometry>(4);
            // T0 is the instant the wall record's rain rim reaches the observer (its rim distance crosses 0), which the model puts within +-2 km of xi 0.
            for (double t = arrival - 260.0; t <= arrival + 1500.0; t += 0.25)
            {
                model.BuildBundle(t, across, list);
                systems.Clear();
                foreach (MegastormRecord r in list)
                    systems.Add(new StormSystemGeometry(r.Id, r.Kind, new Vector2((float)MegastormModel.WrapSigned(around - r.CenterAroundMeters, circumference), 0.0f),
                        (float)r.SemiAcrossMeters, (float)r.SemiAlongMeters, 0.0f, 1.0f, 1.0f, 0.0f, new Vector2(r.DriftMetersPerSecond, 0.0f), r.Density, r.Precipitation,
                        RingVolumetricCloudLayer.StormLifecycleMature, (float)r.CenterAcrossMeters));
                StormArrivalState s = StormArrivalMath.Evaluate(systems.ToArray(), tuning, RainHistorySample.None);
                if (double.IsNaN(tRain) && StormArrivalMath.Evaluate(systems[0], tuning).RimDistanceMeters <= 0.0f) tRain = t;
                if (!tFirst.ContainsKey(s.Phase)) { tFirst[s.Phase] = t; StormArrivalState w = StormArrivalMath.Evaluate(systems[0], tuning); diag.Add($"{s.Phase}: W rim {w.RimDistanceMeters:0} m speed {w.RimSpeedMetersPerSecond:0.0} dens {systems[0].Density:0.000} prec {systems[0].PrecipitationPotential:0.000}"); }
                if (s.Phase != last) { trace.Add($"{t - tRain:0.0}:{s.Phase}"); last = s.Phase; }
                if (s.Core > StormArrivalMath.PhaseThreshold) lastCore = t;
                if (s.FatDrop > StormArrivalMath.PhaseThreshold && double.IsNaN(firstFat)) firstFat = t;
            }
            Check(!double.IsNaN(tRain) && Math.Abs(tRain - arrival) < 12.0, $"megastorm rain rim reaches the observer at {tRain:0.0} s (trigger target {arrival:0})");
            lastCore -= tRain; firstFat -= tRain;
            double Rel(StormArrivalPhase p) => tFirst.TryGetValue(p, out double v) ? v - tRain : double.NaN;
            if (verbose) Console.WriteLine($"megastorm 350 m/s phases (s relative to the rain edge): {string.Join(" ", trace)} lastCore={lastCore:0.0} fat from {firstFat:0.0}");
            double tApproach = Rel(StormArrivalPhase.Approach), tGust = Rel(StormArrivalPhase.GustFront), tCore = Rel(StormArrivalPhase.Core), tTrail = Rel(StormArrivalPhase.Trailing);
            // The 5.1 table: calm T-194 .. T-45 s, gust front T-45 .. T+16 s (Phase is the largest weight, so Core takes over when the gust has faded), fat drops from T-16 s,
            // core weight to T+71 s, trailing from T+71 s. 5% of the time to the edge, with a 6 s floor (about 2 km at 350 m/s): the table is rounded, its T0 is
            // xi = 0 and the wall's rain rim (this test's T0) sits about 1.8 km inside it (WO3 accepts rain onset within 2 km of xi 0).
            bool Within(double actual, double expected) => Math.Abs(actual - expected) <= Math.Max(6.0, 0.05 * Math.Abs(expected));
            Check(Within(tApproach, -194.0), $"megastorm 350 m/s Approach starts T{tApproach:0.0} s (table T-194 s)");
            Check(Within(tGust, -45.0), $"megastorm 350 m/s GustFront starts T{tGust:0.0} s (table T-45 s) [{string.Join("; ", diag)}]");
            Check(Within(firstFat, -16.0), $"megastorm 350 m/s FatDrop weight starts T{firstFat:0.0} s (table T-16 s)");
            Check(Within(tCore, 16.0), $"megastorm 350 m/s Core phase starts T+{tCore:0.0} s (table: gust front to T+16 s)");
            Check(Within(lastCore, 71.0), $"megastorm 350 m/s Core weight ends T+{lastCore:0.0} s (table T+71 s)");
            Check(Within(tTrail, 71.0), $"megastorm 350 m/s Trailing phase starts T+{tTrail:0.0} s (table T+71 s)");
        }

        // ---- band and reach clamps: gust onset = the band, Approach onset = band + reach (nominal), at 25, 350 and 500 m/s ----
        foreach ((float speed, double band, double reach) in new[] { (25.0f, 2000.0, 10000.0), (350.0f, 15750.0, 52500.0), (500.0f, 20000.0, 60000.0) })
        {
            double gustAt = FirstAheadMeters(speed, s => s.Phase == StormArrivalPhase.GustFront);
            double approachAt = FirstAheadMeters(speed, s => s.Phase == StormArrivalPhase.Approach, 140000.0);
            if (verbose) Console.WriteLine($"megastorm {speed:0} m/s: gust onset {gustAt:0} m (band {band:0}), approach onset {approachAt:0} m (band+reach {band + reach:0})");
            Check(Math.Abs(gustAt - band) <= 0.05 * band, $"megastorm {speed} m/s gust band {gustAt:0} m vs {band:0}");
            Check(Math.Abs(approachAt - (band + reach)) <= 0.05 * (band + reach), $"megastorm {speed} m/s approach reach {approachAt:0} m vs {band + reach:0}");
        }
        // At 25 m/s (design 5.1): the calm starts 480 s out and the gust front 80 s out.
        {
            double approach25 = FirstAheadMeters(25.0f, s => s.Phase == StormArrivalPhase.Approach) / 25.0;
            double gust25 = FirstAheadMeters(25.0f, s => s.Phase == StormArrivalPhase.GustFront) / 25.0;
            Check(Math.Abs(approach25 - 480.0) <= 0.05 * 480.0 && Math.Abs(gust25 - 80.0) <= 0.05 * 80.0, $"megastorm 25 m/s: approach {approach25:0} s (480), gust {gust25:0} s (80)");
        }
        // Ordinary kinds keep the distance caps: a kind-5 slab at 350 m/s has its gust onset at the 6 km cap.
        {
            double gust5 = FirstAheadMeters(350.0f, s => s.Phase == StormArrivalPhase.GustFront, 60000.0, kind: 5);
            Check(gust5 > 4000.0 && gust5 < 6500.0, $"kind 5 gust onset {gust5:0} m must stay at the 6 km cap");
        }

        // ---- wall hail ----
        {
            const float v = 350.0f;
            int found = 0, outsideXi = 0, scaleZero = 0, badSize = 0;
            var swathAcross = new List<float>();
            for (int cycle = 0; cycle < 12; cycle++)
            {
                int cellsHit = 0;
                for (float across = 0.0f; across < 318000.0f; across += 500.0f)
                for (double xi = -4000.0; xi <= 22000.0; xi += 500.0)
                {
                    StormArrivalState s = StormArrivalMath.Evaluate(WallAt(xi, v, across, cycle), tuning);
                    if (s.HailIntensity <= 0.05f) continue;
                    found++; cellsHit++;
                    if (xi < 500.0 || xi > 19000.0) outsideXi++;
                    if (StormArrivalMath.Evaluate(WallAt(xi, v, across, cycle), tuning with { Hail = 0.0f }).HailIntensity != 0.0f) scaleZero++;
                    if (s.HailSize <= 0.0f || s.HailSize > 1.0f) badSize++;
                    if (cycle == 0 && !swathAcross.Contains(across)) swathAcross.Add(across);
                }
                if (verbose) Console.WriteLine($"megastorm hail cycle {cycle}: {cellsHit} hit cells");
            }
            Check(found > 100, $"megastorm hail never appears (found {found} cells)");
            Check(outsideXi == 0, $"megastorm hail outside xi 0.5-19 km ({outsideXi} cells)");
            Check(scaleZero == 0, "megastorm hail must be 0 at Hail 0");
            Check(badSize == 0, "megastorm hail size outside 0..1");
            swathAcross.Sort();
            int groups = swathAcross.Count == 0 ? 0 : 1;
            for (int i = 1; i < swathAcross.Count; i++) if (swathAcross[i] - swathAcross[i - 1] > 8000.0f) groups++;
            Check(groups >= 3 && groups <= 6, $"megastorm hail swath count in cycle 0: {groups} (design 3-6)");
            // Determinism, no hail without an absolute across or off the wall record, mirrored for the other direction.
            StormSystemGeometry g0 = WallAt(8000.0, v, 120000.0f, 3);
            Check(StormArrivalMath.Evaluate(g0, tuning).HailIntensity == StormArrivalMath.Evaluate(g0, tuning).HailIntensity, "megastorm hail not deterministic");
            Check(StormArrivalMath.Evaluate(WallAt(8000.0, v, float.NaN, 3), tuning).HailIntensity == 0.0f, "no megastorm hail without the absolute across position");
            Check(StormArrivalMath.Evaluate(WallAt(8000.0, v, 120000.0f, 3) with { SystemId = MegastormModel.AnvilRecordId }, tuning).HailIntensity == 0.0f, "only the wall record hails");
            Check(StormArrivalMath.Evaluate(WallAt(8000.0, -v, 120000.0f, 3), tuning).HailIntensity == StormArrivalMath.Evaluate(WallAt(8000.0, v, 120000.0f, 3), tuning).HailIntensity,
                "megastorm hail must be the same in the wall's own frame for either direction");
            // Cycle cross-fade: zero at the cycle start, present mid-cycle.
            float peakMid = 0.0f, peakEdge = 0.0f;
            for (float across = 0.0f; across < 318000.0f; across += 500.0f)
            for (double xi = 0.0; xi <= 20000.0; xi += 500.0)
            {
                peakMid = Math.Max(peakMid, StormArrivalMath.Evaluate(WallAt(xi, v, across, 5, 900.0f), tuning).HailIntensity);
                peakEdge = Math.Max(peakEdge, StormArrivalMath.Evaluate(WallAt(xi, v, across, 5, 0.0f), tuning).HailIntensity);
            }
            Check(peakMid > 0.3f && peakEdge == 0.0f, $"megastorm hail cross-fade: mid {peakMid:0.00}, cycle start {peakEdge:0.00}");
            // Swaths of different cycles differ.
            int same = 0, probes = 0;
            for (float across = 0.0f; across < 318000.0f; across += 700.0f)
            {
                probes++;
                if (StormArrivalMath.Evaluate(WallAt(7000.0, v, across, 11), tuning).HailIntensity > 0.05f && StormArrivalMath.Evaluate(WallAt(7000.0, v, across, 12), tuning).HailIntensity > 0.05f) same++;
            }
            Check(same < 5, $"megastorm hail swaths repeat between cycles ({same}/{probes})");
        }
    }
}
