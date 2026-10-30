using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// WO10 (docs/megastorm-design.md 5.5, section 8): the megastorm hazard model, headless. The boom overpressure and its peak time come from the audio envelope
/// (one source); shelter attenuation 1 / 0.6 / 0.25; the bullet-rain wind profile (41 m/s at the edge, 140 m/s for xi 2-15 km, 40 m/s at 25 km) through
/// <see cref="StormArrivalMath.ModifyWind"/>, <see cref="RingWindQuery.Compose"/> and the exact model override; the wake (reversal between xi 25 and 35 km, flipping
/// back, -8 kPa at 35 km); rain impact power; the wind-aligned cover probe; scythe sweep rate and cut probability; edge events (observer and a stub structure source);
/// zero hazards when untriggered or outside the zones; determinism across a save / load. The PDA order with the hazard lines is in <c>MegastormPdaTest</c>.
/// Run: Godot --headless --path . --script res://tools/megastorm_hazard_test.gd. Prints MEGASTORM_HAZARD=PASS.
/// </summary>
public partial class MegastormHazardTest : RefCounted
{
    private const string PrefsPath = "res://.godot/megastorm-hazard-test/preferences.tres";
    private const double Around = 5.0e6, Across = 159000.0, Speed = 350.0, Arrival = 7200.0;
    private sealed class Wrapper { public MegastormDocument? Megastorm { get; set; } }

    private sealed class StubStructures : IMegastormStructureSource
    {
        public MegastormStructure[] Items = Array.Empty<MegastormStructure>();
        public int CollectNear(RingCanonicalPosition centre, double radiusMeters, Span<MegastormStructure> output)
        {
            int n = Math.Min(Items.Length, output.Length);
            for (int i = 0; i < n; i++) output[i] = Items[i];
            return n;
        }
    }

    private static RingMegastorm NewNode(Node host, Func<double> clock, string name)
    {
        var node = new RingMegastorm { ClockProvider = clock, MegastormPreferencesPath = PrefsPath, Name = name };
        host.AddChild(node);
        return node;
    }

    private static void Trigger(RingMegastorm node)
        => node.Model.TriggerToReach(Around, Across, Arrival, 1, "hazard_test", 0.0, (float)Speed, 3, true);

    /// <summary>Weather-clock time at which the observer position sits at the given xi.</summary>
    private static double TimeAtXi(MegastormModel model, double xi)
    {
        double t = Arrival + xi / Speed;
        for (int i = 0; i < 6; i++) t -= (model.Xi(Around, Across, t) - xi) / Speed;
        return t;
    }

    private static readonly RingCanonicalPosition P = new(Around, 0.0, Across);

    public int Run(Node host)
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProjectSettings.GlobalizePath(PrefsPath))!);
            if (File.Exists(ProjectSettings.GlobalizePath(PrefsPath))) File.Delete(ProjectSettings.GlobalizePath(PrefsPath));
            double now = 0.0;
            RingMegastorm node = NewNode(host, () => now, "RingMegastormHazardTest");
            Untriggered(node, Check);
            Trigger(node);
            Boom(node, Check);
            Shelter(node, Check);
            WindProfile(node, Check);
            WindThroughQuery(node, Check);
            Wake(node, Check);
            Rain(node, Check);
            CoverProbe(Check);
            Scythe(node, Check);
            Zones(node, Check);
            SaveLoad(host, node, Check);
            Events(host, Check);
            node.QueueFree();
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); Console.Error.WriteLine("MEGASTORM_HAZARD=FAIL exception " + e.Message); return 1; }
        finally { try { File.Delete(ProjectSettings.GlobalizePath(PrefsPath)); } catch { } }
        if (failures.Count != 0) { Console.Error.WriteLine("MEGASTORM_HAZARD=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("MEGASTORM_HAZARD=PASS");
        return 0;
    }

    // ---- Untriggered ------------------------------------------------------------------------------------------------------------
    private static void Untriggered(RingMegastorm node, Action<bool, string> check)
    {
        MegastormSample s = node.Query(P, 100.0);
        MegastormHazards h = s.Hazards;
        check(!s.Active && h.OverpressureKPa == 0 && h.BoomPeakKPa == 0 && h.RainImpactPowerWm2 == 0 && h.PressureDeficitKPa == 0 && h.PressureRateKPaPerS == 0
            && h.WakeFog == 0 && h.ScytheSweepRatePerSecond == 0 && h.RainDropSpeedMs == 0 && h.RainExposure == 0, "untriggered hazards are not zero");
        check(node.BoomAt(P, 100.0) == default && node.ScytheCutProbability(P, 1.8f, 1.0f, 100.0, 1.0) == 0.0f, "untriggered BoomAt / cut probability");
        check(!MegastormHazardMath.WindOverride(node.Model, Around, Across, 100.0, out _, out float w) && w == 0.0f, "untriggered wind override");
    }

    // ---- Boom (5.5.1) -----------------------------------------------------------------------------------------------------------
    private static void Boom(RingMegastorm node, Action<bool, string> check)
    {
        MegastormModel model = node.Model;
        double tc = MegastormHazardMath.CrossingTime(model, Around, Across, Arrival - 30.0);
        check(Math.Abs(model.Xi(Around, Across, tc)) < 0.5, $"crossing time xi = {model.Xi(Around, Across, tc)}");
        check(Math.Abs(tc - Arrival) < 2.0, $"crossing {tc} vs requested arrival {Arrival}");

        // Scan 0.5 ms steps over the whole window: onset exactly at the crossing, peak where the audio envelope peaks.
        double bestP = -1.0, bestPt = 0.0, bestE = -1.0, bestEt = 0.0;
        bool zeroBefore = true;
        for (double t = tc - 3.0; t <= tc + 6.0; t += 0.0005)
        {
            MegastormSample s = node.Query(P, t);
            if (t < tc - 1e-4 && (s.Hazards.OverpressureKPa != 0.0f || s.BoomEnvelope != 0.0f)) zeroBefore = false;
            if (s.Hazards.OverpressureKPa > bestP) { bestP = s.Hazards.OverpressureKPa; bestPt = t; }
            if (s.BoomEnvelope > bestE) { bestE = s.BoomEnvelope; bestEt = t; }
        }
        MegastormSample at = node.Query(P, tc - 5.0);
        MegastormHazards h = at.Hazards;
        check(zeroBefore, "overpressure or envelope before the xi = 0 crossing");
        check(Math.Abs(bestPt - bestEt) < 0.005, $"overpressure peak at {bestPt - tc:F4} s vs envelope peak {bestEt - tc:F4} s");
        check(Math.Abs(h.BoomTimeSeconds - tc - MegastormHazardMath.BoomPeakSeconds) < 1e-6 && Math.Abs(h.BoomTimeSeconds - bestPt) < 0.005, $"BoomTimeSeconds {h.BoomTimeSeconds - tc:F4} s after the crossing, scan {bestPt - tc:F4}");
        check(bestPt - tc > 0.0 && bestPt - tc < 0.6, $"boom peak {bestPt - tc:F3} s after the edge");
        check(Math.Abs(bestP - h.BoomPeakKPa) < 0.01 * h.BoomPeakKPa && h.BoomPeakKPa >= 51.0f && h.BoomPeakKPa <= 69.0f, $"peak {bestP} vs BoomPeakKPa {h.BoomPeakKPa} (60 kPa +-15%)");
        check(Math.Abs(h.BoomImpulseKPaS - 0.3f * h.BoomPeakKPa) < 1e-3f, $"impulse {h.BoomImpulseKPaS}");
        check(node.Query(P, tc + 0.05).Hazards.OverpressureKPa > 0.0f && node.Query(P, tc + 4.5).Hazards.OverpressureKPa == 0.0f, "boom window shape");
        MegastormBoom boom = node.BoomAt(P, tc - 20.0);
        check(Math.Abs(boom.PeakKPa - h.BoomPeakKPa) < 1e-3f && Math.Abs(boom.TimeSeconds - h.BoomTimeSeconds) < 1e-6, $"BoomAt {boom.PeakKPa}/{boom.TimeSeconds} vs the sample {h.BoomPeakKPa}/{h.BoomTimeSeconds}");
        MegastormBoom after = node.BoomAt(P, tc + 60.0);
        check(Math.Abs(after.TimeSeconds - boom.TimeSeconds) < 1e-3, "BoomAt time is stable after the passage");
        // Cells differ along the wall (+-15%), and the figure at another across stays inside the band.
        float lo = float.MaxValue, hi = 0.0f;
        for (double a = 5000.0; a < 313000.0; a += 7000.0)
        {
            float peak = node.BoomAt(new RingCanonicalPosition(Around, 0.0, a), Arrival - 30.0).PeakKPa;
            lo = Math.Min(lo, peak); hi = Math.Max(hi, peak);
        }
        check(lo >= 50.9f && hi <= 69.1f && hi - lo > 3.0f, $"boom peak across the wall {lo}..{hi}");
        // Subsonic: no boom (the audio envelope is 0 below M 0.94, so the overpressure is too).
        var slow = new MegastormModel(new MegastormSettings { SpeedMetersPerSecond = 300.0f });
        slow.TriggerToReach(Around, Across, 9000.0, 1, "slow", 0.0, 300.0f, 3, true);
        check(!MegastormHazardMath.BoomAt(slow, Around, Across, 8000.0).Exists, "boom at 300 m/s");
    }

    // ---- Shelter (5.5.1) --------------------------------------------------------------------------------------------------------
    private static void Shelter(RingMegastorm node, Action<bool, string> check)
    {
        check(MegastormHazardMath.ShelterFactor(MegastormShelter.Open) == 1.0f && MegastormHazardMath.ShelterFactor(MegastormShelter.Lee) == 0.6f
            && MegastormHazardMath.ShelterFactor(MegastormShelter.Enclosed) == 0.25f, "shelter factors");
        MegastormSample s = node.Query(P, Arrival - 20.0);
        float open = s.Hazards.BoomPeakKPa;
        float lee = MegastormPdaHazards.FromSample(s, MegastormShelter.Lee).WallOverpressureKpa, enclosed = MegastormPdaHazards.FromSample(s, MegastormShelter.Enclosed).WallOverpressureKpa;
        check(Math.Abs(lee / open - 0.6f) < 1e-5f && Math.Abs(enclosed / open - 0.25f) < 1e-5f && MegastormPdaHazards.FromSample(s, MegastormShelter.Open).WallOverpressureKpa == open,
            $"attenuation open {open} lee {lee} enclosed {enclosed}");
        check(MegastormPdaHazards.FromSample(s, MegastormShelter.Open).ShelterOpen && !MegastormPdaHazards.FromSample(s, MegastormShelter.Lee).ShelterOpen
            && !MegastormPdaHazards.FromSample(s, MegastormShelter.Enclosed).ShelterOpen, "shelter-inadequate flag only when Open");
        check(MegastormHazardMath.BoomLethalKPa == 50.0f && MegastormHazardMath.RatedGlassKPa == 7.0f && MegastormHazardMath.RatedConcreteKPa == 25.0f && MegastormHazardMath.RatedReinforcedKPa == 80.0f, "ratings");
    }

    // ---- Wind profile ------------------------------------------------------------------------------------------------------------
    private static void WindProfile(RingMegastorm node, Action<bool, string> check)
    {
        check(Math.Abs(MegastormHazardMath.WindAlong(0.0) - 41.0f) < 1e-4f && Math.Abs(MegastormHazardMath.WindAlong(-5000.0) - 41.0f) < 1e-4f, "gust front 41 m/s");
        bool flat = true;
        for (double xi = 2000.0; xi <= 15000.0; xi += 250.0) flat &= Math.Abs(MegastormHazardMath.WindAlong(xi) - 140.0f) < 1e-3f;
        check(flat, "140 m/s for xi 2-15 km");
        check(Math.Abs(MegastormHazardMath.WindAlong(25000.0) - 40.0f) < 1e-3f, $"40 m/s at xi 25 km: {MegastormHazardMath.WindAlong(25000.0)}");
        // Reversal: positive at 25 km, one flip to negative, one flip back by 35-50 km, none after.
        int flips = 0; float prev = MegastormHazardMath.WindAlong(25000.0); double firstFlip = 0.0, secondFlip = 0.0;
        for (double xi = 25050.0; xi <= 60000.0; xi += 50.0)
        {
            float w = MegastormHazardMath.WindAlong(xi);
            if (prev != 0.0f && w != 0.0f && Math.Sign(w) != Math.Sign(prev)) { flips++; if (flips == 1) firstFlip = xi; else secondFlip = xi; }
            prev = w != 0.0f ? w : prev;
        }
        check(MegastormHazardMath.WindAlong(30000.0) < -59.0f && MegastormHazardMath.WindAlong(26000.0) > 0.0f && MegastormHazardMath.WindAlong(40000.0) > 0.0f, "wake signs");
        check(flips == 2 && firstFlip > 25000.0 && firstFlip < 35000.0 && secondFlip >= 35000.0, $"reversal flips {flips} at {firstFlip} / {secondFlip}");
        check(Math.Abs(MegastormHazardMath.WindAlong(35000.0)) < 1e-3f && Math.Abs(MegastormHazardMath.WindAlong(47000.0) - 50.0f) < 1e-3f && MegastormHazardMath.WindAlong(60000.0) == 0.0f, "trough calm, inflow 50 m/s, end");

        // The same profile through the exact model override (around component, direction +1).
        MegastormModel model = node.Model;
        foreach (double xi in new[] { 8000.0, 25000.0, 30000.0, 40000.0 })
        {
            double t = TimeAtXi(model, xi);
            bool ok = MegastormHazardMath.WindOverride(model, Around, Across, t, out Vector2 target, out float weight);
            check(ok && weight > 0.999f && Math.Abs(target.X - MegastormHazardMath.WindAlong(model.Xi(Around, Across, t)) * model.Evaluate(t).Level) < 1e-3f && target.Y == 0.0f, $"override at xi {xi}: {target} w={weight}");
        }
        check(!MegastormHazardMath.WindOverride(model, Around, Across, TimeAtXi(model, -20000.0), out _, out float ahead) && ahead == 0.0f, "no override ahead of the edge");
        check(!MegastormHazardMath.WindOverride(model, Around, Across, TimeAtXi(model, 80000.0), out _, out _), "no override beyond the wake");
    }

    private static StormArrivalState Wall(double xi, float core = 1.0f, float gust = 0.0f, float front = 0.0f)
    {
        bool approaching = xi < 18000.0;
        return StormArrivalState.Calm with
        {
            Valid = true, SystemId = -30001, Kind = 6, Severity = 1.5f, Core = core, GustFront = gust, FatDrop = front, Outflow = 1.0f,
            RimDistanceMeters = (float)(xi < 18000.0 ? -xi : xi - 36000.0), RimSpeedMetersPerSecond = approaching ? 350f : -350f,
            StormBearing = approaching ? new Vector2(-1f, 0f) : new Vector2(1f, 0f), OutflowDirection = approaching ? new Vector2(1f, 0f) : new Vector2(-1f, 0f),
            Phase = StormArrivalPhase.Core
        };
    }

    // ---- Wind through ModifyWind and RingWindQuery -------------------------------------------------------------------------------
    private static void WindThroughQuery(RingMegastorm node, Action<bool, string> check)
    {
        Vector2 baseWind = new(2f, 0f), mean = new(3f, 0f);
        // Gust front (xi -1 km: ahead of the rain edge, the existing front maths): about 41 m/s.
        StormArrivalState gustState = Wall(-1000.0, core: 0.0f, gust: 1.0f);
        Vector2 gustWind = StormArrivalMath.ModifyWind(gustState, mean, baseWind, 28.0f, 1.0f).Mean;
        check(Math.Abs(gustWind.Length() - 41.0f) < 1.5f && gustWind.X > 0.0f, $"gust front wind {gustWind}");
        foreach (double xi in new[] { 2500.0, 8000.0, 14500.0 })
        {
            Vector2 w = StormArrivalMath.ModifyWind(Wall(xi), mean, baseWind, 28.0f, 1.0f).Mean;
            check(Math.Abs(w.Length() - 140.0f) < 1.5f && w.X > 0.0f, $"ModifyWind at xi {xi}: {w}");
        }
        Vector2 w25 = StormArrivalMath.ModifyWind(Wall(25000.0), mean, baseWind, 28.0f, 1.0f).Mean;
        check(Math.Abs(w25.Length() - 40.0f) < 2.0f && w25.X > 0.0f, $"ModifyWind at xi 25 km: {w25}");
        Vector2 w30 = StormArrivalMath.ModifyWind(Wall(30000.0), mean, baseWind, 28.0f, 1.0f).Mean;
        Vector2 w33 = StormArrivalMath.ModifyWind(Wall(33000.0), mean, baseWind, 28.0f, 1.0f).Mean;
        Vector2 w40 = StormArrivalMath.ModifyWind(Wall(40000.0), mean, baseWind, 28.0f, 1.0f).Mean;
        check(w30.X < -50.0f && w33.X < 0.0f && w40.X > 0.0f, $"reversal through ModifyWind {w30} {w33} {w40}");
        // Other kinds keep the 80 m/s limit and are untouched.
        StormArrivalState major = Wall(8000.0) with { Kind = 3, SystemId = 12 };
        check(StormArrivalMath.ModifyWind(major, mean, baseWind, 28.0f, 1.0f).Mean.Length() <= 80.01f, "kind 3 wind exceeds 80 m/s");

        // RingWindQuery: the surface mean from the modifier is what the column carries; the 10 m sample keeps it and the profile above stays finite.
        RingWindColumn column = new(StormArrivalMath.ModifyWind(Wall(8000.0), mean, baseWind, 28.0f, 1.0f).Mean, new Vector2(80f, 0f), new Vector2(2.2f, 0.7f), false, 0f, Wall(8000.0), 600f, 16000f, 1337, true);
        RingWindSample at10 = RingWindQuery.Compose(column, Around, 10.0, Across, 100.0);
        check(Math.Abs(at10.MeanWind.X - 140.0f) < 1.5f, $"RingWindQuery mean at 10 m: {at10.MeanWind.X}");
        bool finite = true;
        for (float alt = 0; alt <= 12000f; alt += 100f)
        {
            RingWindSample s = RingWindQuery.Compose(column, Around, alt, Across, 100.0);
            finite &= float.IsFinite(s.MeanWind.X) && float.IsFinite(s.GustWind.X) && float.IsFinite(s.VerticalShear);
        }
        check(finite, "RingWindQuery column finite through the wall wind");
        RingWindColumn wake = column with { SurfaceMean = w30, Arrival = Wall(30000.0) };
        check(RingWindQuery.Compose(wake, Around, 10.0, Across, 100.0).MeanWind.X < -50.0f, "RingWindQuery reversed wind in the wake");
    }

    // ---- Wake (5.5.3) ------------------------------------------------------------------------------------------------------------
    private static void Wake(RingMegastorm node, Action<bool, string> check)
    {
        float best = 0.0f; double bestXi = 0.0; bool nonNegative = true;
        for (double xi = 0.0; xi <= 90000.0; xi += 50.0)
        {
            float d = MegastormHazardMath.PressureDeficitKPa(xi);
            nonNegative &= d >= 0.0f;
            if (d > best) { best = d; bestXi = xi; }
        }
        check(nonNegative && Math.Abs(best - 8.0f) < 1e-4f && Math.Abs(bestXi - 35000.0) <= 50.0, $"pressure minimum -{best} kPa at xi {bestXi}");
        check(MegastormHazardMath.PressureDeficitKPa(25000.0) == 0.0f && MegastormHazardMath.PressureDeficitKPa(60000.0) == 0.0f, "wake pressure at its edges");
        float rate = MegastormHazardMath.PressureRateKPaPerS(30000.0, Speed);
        check(rate < -0.2f && rate > -0.6f && MegastormHazardMath.PressureRateKPaPerS(47000.0, Speed) > 0.0f, $"pressure rate {rate} kPa/s into the trough");
        check(MegastormHazardMath.WakeFog(8.0f) > 0.999f && MegastormHazardMath.WakeFog(1.0f) == 0.0f, "wake fog");
        MegastormModel model = node.Model;
        MegastormHazards at35 = node.Query(P, TimeAtXi(model, 35000.0)).Hazards;
        check(Math.Abs(at35.PressureDeficitKPa - 8.0f) < 0.02f && at35.WakeFog > 0.99f, $"sample at the trough: {at35.PressureDeficitKPa} kPa fog {at35.WakeFog}");
        // Danger adds 0.6 x smoothstep(2, 6, deficit).
        check(Math.Abs(MegastormHazardMath.WakeDanger(8.0f) - 0.6f) < 1e-5f && MegastormHazardMath.WakeDanger(2.0f) == 0.0f, "wake danger term");
        float danger = node.Query(P, TimeAtXi(model, 35000.0)).Danger;
        check(danger >= 0.6f - 1e-4f, $"Danger at the trough {danger}");
    }

    // ---- Rain impact (5.5.2) -----------------------------------------------------------------------------------------------------
    private static void Rain(RingMegastorm node, Action<bool, string> check)
    {
        check(Math.Abs(MegastormHazardMath.RainImpactPowerWm2(200.0f, 140.0f) - 10976.0f) < 5.0f, $"impact power at 200 mm/h and 140 m/s: {MegastormHazardMath.RainImpactPowerWm2(200.0f, 140.0f)}");
        MegastormModel model = node.Model;
        MegastormHazards core = node.Query(P, TimeAtXi(model, 8000.0)).Hazards;
        check(core.RainImpactPowerWm2 > 10000.0f && core.RainImpactPowerWm2 < 12000.0f && core.RainDropSpeedMs > 139.9f && core.RainDropSpeedMs < 141.0f && core.RainExposure == 1.0f,
            $"core power {core.RainImpactPowerWm2} speed {core.RainDropSpeedMs}");
        check(Math.Abs(core.RainImpactDirection.Length() - 1.0f) < 1e-4f && core.RainImpactDirection.X > 0.99f && core.RainImpactDirection.Y < 0.0f && core.RainImpactDirection.Z == 0.0f,
            $"impact direction {core.RainImpactDirection}");
        MegastormHazards wake = node.Query(P, TimeAtXi(model, 30000.0)).Hazards;
        check(wake.RainImpactDirection.X < 0.0f, $"wake rain travels backwards: {wake.RainImpactDirection}");
        float peak = 0.0f; double peakXi = 0.0;
        for (double xi = -1000.0; xi <= 60000.0; xi += 250.0)
        {
            float p = node.Query(P, TimeAtXi(model, xi)).Hazards.RainImpactPowerWm2;
            if (p > peak) { peak = p; peakXi = xi; }
        }
        check(peakXi >= 2000.0 && peakXi <= 15000.0, $"rain impact peaks at xi {peakXi}");
        // The observer's exposure rides in with the observer context.
        check(node.Query(P, TimeAtXi(model, 8000.0), new MegastormObserver(MegastormShelter.Lee, 0.1f)).Hazards.RainExposure == 0.1f, "observer exposure");
        MegastormPdaHazards pda = MegastormPdaHazards.FromSample(node.Query(P, TimeAtXi(model, 8000.0)), MegastormShelter.Open);
        check(pda.RainImpactKilowattsPerSquareMeter > 2.0f && pda.Exposure > 0.3f && Math.Abs(pda.RainSpeedMetersPerSecond - 140.0f) < 1.0f, "PDA rain inputs");
    }

    // ---- Wind-aligned cover probe -----------------------------------------------------------------------------------------------
    private static void CoverProbe(Action<bool, string> check)
    {
        // Wind blows toward +around: the rain comes from -around. A 10 m wall occupies around 995-996 (4-5 m upwind of the observer at 1000).
        Func<double, double, double, bool> wall10 = (a, y, c) => a >= 995.0 && a <= 996.0 && y < 10.0;
        Func<double, double, double, bool> open = (a, y, c) => false;
        Func<double, double, double, bool> wall1 = (a, y, c) => a >= 995.0 && a <= 996.0 && y < 1.0;
        var wind = new Vector2(140f, 0f);
        float lee = WindAlignedCoverProbe.Exposure(1000.0, 1.7, 5000.0, wind, wall10);
        float free = WindAlignedCoverProbe.Exposure(1000.0, 1.7, 5000.0, wind, open);
        float low = WindAlignedCoverProbe.Exposure(1000.0, 1.7, 5000.0, wind, wall1);
        float windward = WindAlignedCoverProbe.Exposure(1000.0, 1.7, 5000.0, new Vector2(-140f, 0f), wall10);
        check(lee < 0.2f, $"lee of a 10 m wall exposure {lee}");
        check(free == 1.0f && windward == 1.0f, $"open {free} / other side of the wall {windward}");
        check(low < 1.0f && low > lee, $"a 1 m wall shelters part: {low}");
        check(WindAlignedCoverProbe.Exposure(1000.0, 1.7, 5000.0, Vector2.Zero, wall10) == 1.0f, "no wind: the overhead probe decides");
    }

    // ---- Scythes (5.5.4) ---------------------------------------------------------------------------------------------------------
    private static void Scythe(RingMegastorm node, Action<bool, string> check)
    {
        MegastormModel model = node.Model;
        double width = model.Settings.WidthMeters, sum = 0.0;
        for (double xi = -20000.0; xi <= 25000.0; xi += 10.0) sum += MegastormHazardMath.ScytheSweepRatePerSecond(xi, width) * 10.0 / Speed;
        check(Math.Abs(sum - 0.0786) < 0.0025, $"expected crossings per passage {sum:F4}");
        check(MegastormHazardMath.ScytheSweepRatePerSecond(-16000.0, width) == 0.0f && MegastormHazardMath.ScytheSweepRatePerSecond(21000.0, width) == 0.0f
            && MegastormHazardMath.ScytheSweepRatePerSecond(5000.0, width) > MegastormHazardMath.ScytheSweepRatePerSecond(-10000.0, width), "scythe zone shape");
        double t = TimeAtXi(model, 5000.0);
        float rate = node.Query(P, t).Hazards.ScytheSweepRatePerSecond;
        check(rate > 0.0f, "sweep rate in the zone");
        float Cut(float h, float exposure = 1.0f, float below = 0.0f) => node.ScytheCutProbability(P, h, exposure, t, 1.0, below);
        float prone = Cut(0.4f), crouch = Cut(1.0f), standing = Cut(1.8f), tree = Cut(10.0f), trench = Cut(1.8f, 1.0f, 3.0f);
        check(prone == 0.0f && trench == 0.0f, $"prone {prone} / trench {trench}");
        check(Math.Abs(crouch / rate - 0.05f) < 0.015f && Math.Abs(standing / rate - 0.33f) < 0.04f && Math.Abs(tree / rate - 1.0f) < 1e-4f, $"cut shares crouch {crouch / rate} standing {standing / rate} tree {tree / rate}");
        check(Cut(1.8f, 0.0f) == 0.0f && Math.Abs(Cut(10.0f, 0.5f) / rate - 0.5f) < 1e-4f, "enclosed exposure 0 / half exposure halves it");
        check(node.ScytheCutProbability(P, 10.0f, 1.0f, TimeAtXi(model, 40000.0), 1.0) == 0.0f, "no cut chance outside the zone");
    }

    // ---- Zones: everything 0 far outside ------------------------------------------------------------------------------------------
    private static void Zones(RingMegastorm node, Action<bool, string> check)
    {
        MegastormModel model = node.Model;
        foreach (double xi in new[] { -500000.0, -150000.0, 70000.0, 100000.0, 300000.0 })
        {
            MegastormHazards h = node.Query(P, TimeAtXi(model, xi)).Hazards;
            bool zero = h.OverpressureKPa == 0 && h.BoomPeakKPa == 0 && h.BoomImpulseKPaS == 0 && h.RainImpactPowerWm2 == 0 && h.PressureDeficitKPa == 0 && h.PressureRateKPaPerS == 0 && h.WakeFog == 0
                && h.ScytheSweepRatePerSecond == 0 && h.RainDropSpeedMs == 0 && h.RainImpactDirection == Vector3.Zero && h.LocalStrikeRatePerSecond == 0 && h.FieldStrikeRatePerSecond == 0;
            check(zero, $"hazards at xi {xi} are not zero: {h}");
        }
        // Danger of the ordinary terms is unchanged ahead of the wake.
        check(Math.Abs(model.Danger(10000.0, 0.0, Speed, 1.0f) - 1.0f) < 1e-4f && Math.Abs(model.Danger(-2000.0, 5.0, Speed) - model.Danger(-2000.0, 5.0, Speed, 1.0f)) < 1e-6f, "wall danger");
    }

    // ---- Save / load ---------------------------------------------------------------------------------------------------------------
    private static void SaveLoad(Node host, RingMegastorm a, Action<bool, string> check)
    {
        string json = JsonSerializer.Serialize(new Wrapper { Megastorm = a.ToDocument() });
        double now = 0.0;
        RingMegastorm b = NewNode(host, () => now, "RingMegastormHazardTestB");
        b.RestoreFrom(JsonSerializer.Deserialize<Wrapper>(json)!.Megastorm);
        int differs = 0, n = 0;
        foreach (double xi in new[] { -90000.0, -20000.0, -3000.0, 0.0, 1500.0, 9000.0, 24000.0, 29000.0, 35000.0, 47000.0, 58000.0 })
            for (int k = 0; k < 3; k++)
            {
                double t = TimeAtXi(a.Model, xi) + k * 0.123;
                n++;
                if (a.Query(P, t) != b.Query(P, t)) differs++;
                if (a.BoomAt(P, t) != b.BoomAt(P, t)) differs++;
            }
        check(differs == 0 && n == 33, $"{differs} of {n} hazard samples differ after save / load");
        b.QueueFree();
    }

    // ---- Events ---------------------------------------------------------------------------------------------------------------------
    private static void Events(Node host, Action<bool, string> check)
    {
        double now = 0.0;
        RingMegastorm node = NewNode(host, () => now, "RingMegastormHazardEvents");
        Trigger(node);
        var stub = new StubStructures { Items = new[] { new MegastormStructure(7, new RingCanonicalPosition(Around + 1500.0, 0.0, Across), 6.0f, 25.0f, 0.5f, false) } };
        node.StructureSource = stub;
        node.ObserverPositionProvider = () => P;
        var shelter = RainShelterClass.Open;
        node.ShelterClassProvider = () => shelter;
        node.SolidProbe = (a, y, c) => a >= Around - 5.0 && a <= Around - 4.0 && y < 10.0;   // a 10 m wall upwind of the observer along the wind
        int booms = 0, enters = 0, exits = 0, structure = 0; MegastormBoomEvent? last = null; long episode = 0;
        node.BoomPassed += e => { booms++; last = e; };
        node.WakeEntered += e => { enters++; episode = e; };
        node.WakeExited += e => exits++;
        node.StructureBoomPassed += (s, b) => { structure++; };
        double tc = MegastormHazardMath.CrossingTime(node.Model, Around, Across, Arrival);
        ObserverSample[] sampled = new ObserverSample[4];
        MegastormShelter leeContext = MegastormShelter.Open;
        for (double t = tc - 60.0; t <= tc + 220.0; t += 0.25)
        {
            now = t;
            node._Process(0.25);   // driven by hand: the node does not tick in a headless SceneTree script
            if (Math.Abs(t - (tc - 5.0)) < 0.125) leeContext = node.ObserverContext.Shelter;
            if (Math.Abs(t - (tc + 8.0)) < 0.125) sampled[0] = new ObserverSample(node.ObserverContext, node.QueryNow(P).Hazards.RainExposure);
        }
        check(booms == 1 && enters == 1 && exits == 1 && structure == 1, $"events boom {booms} wake in {enters} out {exits} structure {structure}");
        MegastormBoom expect = node.BoomAt(P, tc);
        check(last != null && Math.Abs(last.Value.PeakKPa - expect.PeakKPa) < 0.5f && last.Value.EpisodeId == episode && episode != 0 && last.Value.TimeSeconds > tc, $"boom event {last}");
        check(leeContext == MegastormShelter.Lee, $"shelter class behind the upwind wall before the boom: {leeContext}");
        // In the wall the wind blows toward +around, the 10 m wall 4-5 m upwind shelters the observer.
        check(sampled[0].Context.RainExposure < 0.2f && sampled[0].Exposure < 0.2f, $"observer exposure in the lee {sampled[0].Context.RainExposure}");
        // Enclosed shelter: exposure 0 and the enclosed class reaches the PDA inputs.
        shelter = RainShelterClass.Indoors;
        now = tc + 10.0; node._Process(0.5); node._Process(0.5);
        check(node.ObserverContext.Shelter == MegastormShelter.Enclosed && node.ObserverContext.RainExposure == 0.0f, $"enclosed context {node.ObserverContext}");
        // A load into the middle of the passage has no history and raises nothing.
        string json = JsonSerializer.Serialize(new Wrapper { Megastorm = node.ToDocument() });
        node.RestoreFrom(JsonSerializer.Deserialize<Wrapper>(json)!.Megastorm);
        int before = booms + enters + exits;
        for (double t = tc + 100.0; t <= tc + 101.0; t += 0.25) { now = t; node._Process(0.25); }
        check(booms + enters + exits == before, "events fired on a load inside the wake");
        node.QueueFree();
    }

    private readonly record struct ObserverSample(MegastormObserver Context, float Exposure);
}
