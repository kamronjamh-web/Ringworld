using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// WO-B2 (docs/storm-revamp-s3-storm-arrival-design.md sections 8 and 12): the flight-ready wind query. Pure part (synthetic columns): surface identity at 2 m,
/// finite from -100 to 60000 m, profile continuity at 10 m / 1 km, gust fade, sigma ordering core > in-cloud > clear, noise calibration, updraft and downdraft,
/// determinism and the cost budget. World part (when a weather layer is given): the sample at 2 m equals the surface snapshot within 0.5 m/s, finite columns,
/// core sigma above clear sigma, SampleColumn equals Sample, determinism and cost.
/// Run: tools/ring_wind_query_test.gd (headless). Prints RING_WIND_QUERY=PASS.
/// </summary>
public partial class RingWindQueryTest : RefCounted
{
    static StormArrivalState Storm(int kind, float core, float gust, float severity, int id = 99)
        => StormArrivalState.Calm with { Valid = true, SystemId = id, Kind = kind, Core = core, GustFront = gust, Severity = severity, Phase = core > gust ? StormArrivalPhase.Core : StormArrivalPhase.GustFront };

    static RingWindColumn Column(StormArrivalState arrival, float ground = 0f)
        => new(new Vector2(5f, 2f), new Vector2(8f, 3f), new Vector2(2.2f, 0.7f), false, ground, arrival, 1500f, 8000f, 1337, true);

    public int Run(Node? layerNode, bool verbose)
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            RunPure(Check, verbose);
            if (layerNode is RingVolumetricCloudLayer layer) RunWorld(layer, Check, verbose);
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("RING_WIND_QUERY=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("RING_WIND_QUERY=PASS");
        return 0;
    }

    static void RunPure(Action<bool, string> Check, bool verbose)
    {
        RingWindColumn clear = Column(StormArrivalState.Calm);
        // Surface identity: at 2 m the gust wind is the surface instant wind and the mean the surface mean.
        RingWindSample surface = RingWindQuery.Compose(clear, 1000, 2.0, 2000, 10.0);
        Check(Math.Abs(surface.GustWind.X - 8f) < 0.01f && Math.Abs(surface.GustWind.Z - 3f) < 0.01f && Math.Abs(surface.MeanWind.X - 5f) < 0.01f && Math.Abs(surface.HeightAboveGroundMeters - 2f) < 1e-4f, $"surface identity at 2 m: gust={surface.GustWind} mean={surface.MeanWind}");
        // Valid false and calm without a world column.
        Check(!RingWindQuery.Compose(default, 0, 0, 0, 0).Valid && RingWindQuery.Compose(default, 0, 0, 0, 0).MeanWind == Vector3.Zero, "no column must give calm and Valid=false");
        Check(!RingWindQuery.Sample(new RingCanonicalPosition(0, 0, 0), 0).Valid || RingWindQuery.ColumnProvider != null || RingWindQuery.Provider != null, "Sample without a provider must be invalid");

        // Finite from -100 to 60000 m, with and without a storm.
        bool finite = true; float maxSpeed = 0f;
        foreach (RingWindColumn column in new[] { clear, Column(Storm(3, 1f, 0.5f, 0.9f)), Column(Storm(5, 1f, 1f, 1.5f)) })
            for (int i = 0; i <= 200; i++)
            {
                float altitude = -100f + i * (60100f / 200f);
                RingWindSample s = RingWindQuery.Compose(column, 12345.6, altitude, 4321.0, 77.7);
                bool ok = float.IsFinite(s.MeanWind.X) && float.IsFinite(s.MeanWind.Z) && float.IsFinite(s.GustWind.X) && float.IsFinite(s.Turbulence.X) && float.IsFinite(s.Turbulence.Y)
                    && float.IsFinite(s.Turbulence.Z) && float.IsFinite(s.TurbulenceSigma) && float.IsFinite(s.UpdraftMetersPerSecond) && float.IsFinite(s.VerticalShear) && s.TurbulenceLengthMeters >= 50f && s.TurbulenceLengthMeters <= 300f;
                finite &= ok; maxSpeed = Math.Max(maxSpeed, new Vector2(s.MeanWind.X, s.MeanWind.Z).Length());
            }
        Check(finite, "wind sample not finite or length outside 50-300 m somewhere in -100..60000 m");
        Check(maxSpeed <= 25f * 1.8f + 0.5f, $"mean wind above the jet bound: {maxSpeed}");

        // Profile: continuous at 10 m and 1 km (speed), veers to the drift direction, jet aloft, gust fades by 300 m.
        Vector2 Mean(float h) => WindAltitudeProfile.MeanAt(new Vector2(5f, 2f), new Vector2(2.2f, 0.7f), h, false);
        Check((Mean(10.001f) - Mean(9.999f)).Length() < 0.05f, "profile jump at 10 m");
        Check((Mean(1000.5f) - Mean(999.5f)).Length() < 0.1f, $"profile jump at 1 km {(Mean(1000.5f) - Mean(999.5f)).Length()}");
        Check(Mathf.Abs(Mean(500f).Length() - Mean(450f).Length()) < 1.0f, "profile steep inside the boundary layer");
        Check(Mathf.Abs(Mean(9000f).Length() - 25f) < 0.01f, $"jet speed {Mean(9000f).Length()}");
        Check(Mathf.Abs(Mean(1500f).Normalized().AngleTo(new Vector2(2.2f, 0.7f).Normalized())) < 1e-3f, "free wind must point along the weather drift above 1 km");
        Check(WindAltitudeProfile.GustAt(new Vector2(5f, 2f), new Vector2(8f, 3f), Mean(300f), 300f).DistanceTo(Mean(300f)) < 1e-4f, "surface gust deviation must be gone by 300 m");
        Check(WindAltitudeProfile.MeanAt(new Vector2(5f, 2f), new Vector2(2.2f, 0.7f), 100f, true).Length() < WindAltitudeProfile.MeanAt(new Vector2(5f, 2f), new Vector2(2.2f, 0.7f), 100f, false).Length() + 2f, "forest alpha");
        Check(WindAltitudeProfile.JetKindMultiplier(4) == 1.4f && WindAltitudeProfile.JetKindMultiplier(5) == 1.8f && WindAltitudeProfile.JetKindMultiplier(3) == 1f, "jet multipliers");

        // Sigma ordering: core > in-cloud > clear (same altitude, inside the cloud).
        float altitudeInCloud = 4500f;
        float sigmaCore = RingWindQuery.Compose(Column(Storm(3, 1f, 0.2f, 0.9f)), 100, altitudeInCloud, 100, 5).TurbulenceSigma;
        float sigmaInCloud = RingWindQuery.Compose(Column(Storm(3, 0f, 0f, 0.3f)), 100, altitudeInCloud, 100, 5).TurbulenceSigma;
        float sigmaClear = RingWindQuery.Compose(clear, 100, altitudeInCloud, 100, 5).TurbulenceSigma;
        Check(sigmaCore > sigmaInCloud && sigmaInCloud > sigmaClear && sigmaCore >= 5f && sigmaCore <= 12f, $"sigma ordering core={sigmaCore} in-cloud={sigmaInCloud} clear={sigmaClear}");
        float sigmaGustNear = RingWindQuery.Compose(Column(Storm(3, 0f, 1f, 0.9f)), 100, 5f, 100, 5).TurbulenceSigma;
        float sigmaGustFar = RingWindQuery.Compose(Column(Storm(3, 0f, 1f, 0.9f)), 100, 1500f, 100, 5).TurbulenceSigma;
        Check(sigmaGustNear >= 4f && sigmaGustNear <= 9f && sigmaGustFar < sigmaGustNear * 0.6f, $"gust front sigma near={sigmaGustNear} aloft={sigmaGustFar}");
        Check(RingWindQuery.Compose(Column(Storm(3, 1f, 0f, 0.9f)), 0, 4500f, 0, 0).Phase == StormArrivalPhase.Core, "phase passthrough");

        // Noise calibration: the std of one noise component, and of the realised turbulence against sigma.
        double sum = 0, sumSq = 0; int n = 0; var rng = new Random(5);
        for (int i = 0; i < 200_000; i++)
        {
            float v = StormTurbulence.Noise3(rng.NextDouble() * 400, rng.NextDouble() * 400, rng.NextDouble() * 400, 7u);
            sum += v; sumSq += v * v; n++;
        }
        double std = Math.Sqrt(sumSq / n - (sum / n) * (sum / n));
        Check(Math.Abs(std - StormTurbulence.NoiseStd) / StormTurbulence.NoiseStd < 0.05, $"noise std {std:0.0000} vs constant {StormTurbulence.NoiseStd}");
        double rs = 0, rsq = 0; int rn = 0;
        for (int i = 0; i < 60_000; i++)
        {
            Vector3 t = StormTurbulence.Realize(rng.NextDouble() * 20000, 100 + rng.NextDouble() * 3000, rng.NextDouble() * 20000, 12.5, new Vector3(5, 0, 2), 4.0f, 120f, 3);
            rs += t.X; rsq += t.X * t.X; rn++;
        }
        double realStd = Math.Sqrt(rsq / rn - (rs / rn) * (rs / rn));
        if (verbose) Console.WriteLine($"noise std={std:0.0000} realised std (sigma 4)={realStd:0.000}");
        Check(Math.Abs(realStd - 4.0) / 4.0 < 0.15, $"realised turbulence std {realStd} vs sigma 4");

        // Determinism and time evolution.
        RingWindSample a = RingWindQuery.Compose(Column(Storm(3, 1f, 0.2f, 0.9f)), 777, 4000, 888, 100.0);
        for (int i = 0; i < 10; i++) RingWindQuery.Compose(clear, i * 50, i * 300, i * 70, i);
        RingWindSample b = RingWindQuery.Compose(Column(Storm(3, 1f, 0.2f, 0.9f)), 777, 4000, 888, 100.0);
        Check(a == b, "deterministic");
        Check(a.Turbulence != RingWindQuery.Compose(Column(Storm(3, 1f, 0.2f, 0.9f)), 777, 4000, 888, 140.0).Turbulence, "turbulence must evolve in time");

        // Updraft and downdraft.
        RingWindColumn core = Column(Storm(3, 1f, 0f, 0.9f));
        float bestUp = 0f, worstDown = 0f, outsideCloud = 0f;
        for (double x = 0; x < 20000; x += 250) for (double z = 0; z < 20000; z += 250)
        {
            bestUp = Math.Max(bestUp, RingWindQuery.StormUpdraft(core, x, z, 4750f));
            outsideCloud = Math.Max(outsideCloud, Math.Abs(RingWindQuery.StormUpdraft(core, x, z, 9000f)) + Math.Abs(RingWindQuery.StormUpdraft(core, x, z, 1000f)));
        }
        for (float alt = 200; alt < 1500; alt += 50) worstDown = Math.Min(worstDown, RingWindQuery.PrecipitationDownDraft(core, alt, alt));
        Check(bestUp > 8f && bestUp <= 15.01f, $"core updraft peak {bestUp}");
        Check(outsideCloud == 0f, "no updraft outside the cloud layer");
        Check(worstDown <= -4f && worstDown >= -12.01f && RingWindQuery.PrecipitationDownDraft(core, 100f, 100f) == 0f, $"downdraft {worstDown}");
        Check(RingWindQuery.CoreUpdraftPeak(3) == 15f && RingWindQuery.CoreUpdraftPeak(4) == 30f && RingWindQuery.CoreUpdraftPeak(5) == 40f && RingWindQuery.CoreUpdraftPeak(1) == 0f, "updraft peaks by kind");

        // Megastorm (kind 6) wall updraft and rear downdraft (design 5, WO6a): 40 m/s over xi 3-20 km, -12 m/s over xi 15-40 km, in the cloud layer / below the base only.
        {
            static StormArrivalState Wall(double xi) => Storm(6, 1f, 0.2f, 1.5f, -30001) with
            {
                RimDistanceMeters = (float)(xi < 18000.0 ? -xi : xi - 36000.0), RimSpeedMetersPerSecond = xi < 18000.0 ? 350f : -350f, Phase = StormArrivalPhase.Core
            };
            Check(RingWindQuery.MegastormWallXi(Wall(-8000.0)) is > -8001f and < -7999f && RingWindQuery.MegastormWallXi(Wall(30000.0)) is > 29999f and < 30001f, "megastorm xi from rim distance and speed sign");
            Check(RingWindQuery.MegastormWallXi(StormArrivalState.Calm with { RimDistanceMeters = float.PositiveInfinity }) == float.NegativeInfinity, "megastorm xi of an infinite rim distance");
            float msBestUp = 0f, bestUpXi = 0f, minUpInBand = float.MaxValue, outsideBand = 0f, msOutsideCloud = 0f;
            for (double xi = -20000; xi <= 60000; xi += 250)
            {
                RingWindColumn wall = Column(Wall(xi));
                float peakHere = 0f;
                for (double across = 0; across < 40000; across += 500)
                    for (float alt = 1700f; alt < 7800f; alt += 100f) peakHere = Math.Max(peakHere, RingWindQuery.StormUpdraft(wall, 1000.0, across, alt));
                if (peakHere > msBestUp) { msBestUp = peakHere; bestUpXi = (float)xi; }
                if (xi >= 4000 && xi <= 19000) minUpInBand = Math.Min(minUpInBand, peakHere);
                if (xi < 0 || xi > 25000) outsideBand = Math.Max(outsideBand, peakHere);
                msOutsideCloud = Math.Max(msOutsideCloud, Math.Abs(RingWindQuery.StormUpdraft(wall, 1000.0, 5000.0, 9000f)) + Math.Abs(RingWindQuery.StormUpdraft(wall, 1000.0, 5000.0, 1000f)));
            }
            Check(msBestUp > 36f && msBestUp <= 40.01f && bestUpXi >= 3000f && bestUpXi <= 20000f, $"megastorm wall updraft peak {msBestUp} at xi {bestUpXi}");
            Check(minUpInBand > 24f, $"megastorm wall updraft sags to {minUpInBand} inside xi 4-19 km (continuous wall)");
            Check(outsideBand == 0f && msOutsideCloud == 0f, $"megastorm updraft outside xi 1-24 km {outsideBand} / outside the cloud layer {msOutsideCloud}");
            float msWorstDown = 0f, worstDownXi = 0f, downOutside = 0f;
            for (double xi = -20000; xi <= 70000; xi += 250)
            {
                RingWindColumn wall = Column(Wall(xi));
                float d = 0f;
                for (float alt = 350f; alt < 1700f; alt += 50f) d = Math.Min(d, RingWindQuery.PrecipitationDownDraft(wall, alt, alt));
                if (d < msWorstDown) { msWorstDown = d; worstDownXi = (float)xi; }
                if (xi < 9000 || xi > 46000) downOutside = Math.Min(downOutside, d);
            }
            Check(msWorstDown <= -11.5f && msWorstDown >= -12.01f && worstDownXi >= 15000f && worstDownXi <= 40000f, $"megastorm rear downdraft {msWorstDown} at xi {worstDownXi}");
            Check(downOutside == 0f, $"megastorm downdraft outside xi 10-45 km: {downOutside}");
            Check(RingWindQuery.PrecipitationDownDraft(Column(Wall(30000.0)), 100f, 100f) == 0f, "megastorm downdraft stops at 300 m AGL");
            // The jet over the wall is x1.8 (WindAltitudeProfile.JetKindMultiplier) and everything stays finite.
            Check(WindAltitudeProfile.JetKindMultiplier(6) == 1.8f, "megastorm jet multiplier");
            bool wallFinite = true;
            for (double xi = -5000; xi <= 50000; xi += 5000) for (int i = 0; i <= 40; i++)
            {
                RingWindSample sample = RingWindQuery.Compose(Column(Wall(xi)), 777, -100f + i * 1500f, 888, 5);
                wallFinite &= float.IsFinite(sample.UpdraftMetersPerSecond) && float.IsFinite(sample.MeanWind.X) && float.IsFinite(sample.TurbulenceSigma);
            }
            Check(wallFinite, "megastorm samples not finite");
            // Smooth across the 10 km tower lattice: no step above 3 m/s per 100 m along the across axis.
            float jump = 0f; float previous = RingWindQuery.StormUpdraft(Column(Wall(10000.0)), 0.0, 0.0, 4750f);
            for (double across = 100; across < 60000; across += 100)
            {
                float u = RingWindQuery.StormUpdraft(Column(Wall(10000.0)), 0.0, across, 4750f);
                jump = Math.Max(jump, Math.Abs(u - previous)); previous = u;
            }
            Check(jump < 3f, $"megastorm updraft steps {jump} m/s per 100 m along the wall");
            // Ordinary kinds are unchanged: the 5 km lattice with 30% fill still gives at most the kind's peak.
            Check(RingWindQuery.StormUpdraft(Column(Storm(5, 1f, 0f, 1.5f)), 1000.0, 2000.0, 4750f) <= 40.01f, "kind 5 updraft bound");
        }

        // Cost: at most 3 us per sample, a 32-altitude column at most 0.1 ms (pure maths, column given).
        RingWindColumn stormColumn = Column(Storm(3, 1f, 0.5f, 0.9f));
        float sink = 0f;
        for (int i = 0; i < 2000; i++) sink += RingWindQuery.Compose(stormColumn, 1000 + i, 500 + i % 7000, 2000, i * 0.01).TurbulenceSigma;
        var sw = Stopwatch.StartNew();
        const int Samples = 100_000;
        for (int i = 0; i < Samples; i++) sink += RingWindQuery.Compose(stormColumn, 1000 + i * 0.7, 20 + (i % 9000), 2000 + i * 0.3, i * 0.01).TurbulenceSigma;
        double microsecondsPerSample = sw.Elapsed.TotalMilliseconds * 1000.0 / Samples;
        Span<float> altitudes = stackalloc float[32]; for (int i = 0; i < 32; i++) altitudes[i] = i * 300f;
        var results = new RingWindSample[32];
        var previousProvider = RingWindQuery.ColumnProvider;
        RingWindQuery.ColumnProvider = (p, t) => stormColumn;
        try
        {
            sw.Restart();
            for (int i = 0; i < 2000; i++) RingWindQuery.SampleColumn(new RingCanonicalPosition(1000 + i, 0, 2000), i * 0.1, altitudes, results);
            double columnMs = sw.Elapsed.TotalMilliseconds / 2000.0;
            if (verbose) Console.WriteLine($"cost per sample={microsecondsPerSample:0.00} us, 32-altitude column={columnMs:0.000} ms (sink {sink:0})");
            Check(microsecondsPerSample <= 3.0, $"sample cost {microsecondsPerSample:0.00} us exceeds 3 us");
            Check(columnMs <= 0.1, $"32-altitude column {columnMs:0.000} ms exceeds 0.1 ms");
            Check(RingWindQuery.SampleColumn(new RingCanonicalPosition(5, 0, 5), 1.0, altitudes, results.AsSpan(0, 5)) == 5, "SampleColumn count");
            RingWindSample single = RingWindQuery.Sample(new RingCanonicalPosition(5, 600, 5), 1.0);
            RingWindQuery.SampleColumn(new RingCanonicalPosition(5, 0, 5), 1.0, stackalloc float[] { 600f }, results);
            Check(single == results[0], "Sample and SampleColumn must agree");
        }
        finally { RingWindQuery.ColumnProvider = previousProvider; }
    }

    static void RunWorld(RingVolumetricCloudLayer layer, Action<bool, string> Check, bool verbose)
    {
        StormArrivalTuning tuning = RingStormArrivalSettings.Current.ToTuning(RingVolumetricCloudLayer.StormArrivalPrecipitationStart, layer.Coverage);
        Check(RingWindQuery.ColumnProvider != null, "world: column provider not installed");
        RingCanonicalPosition? found = StormArrivalProductionTest.FindMatureMajorObserver(layer, tuning, 1800.0, out _, out _);
        Check(found != null, "world: no observer found");
        if (found == null) return;
        RingCanonicalPosition observer = found.Value;
        double time = 1800.0;
        RingWindColumn column = RingWindQuery.ColumnProvider!(observer, time);
        Check(column.Valid, "world: column invalid");
        float ground = column.GroundAltitudeMeters;
        // Surface equality at 2 m AGL.
        RingWindSample at2 = RingWindQuery.Sample(observer with { VerticalMeters = ground + 2.0 }, time);
        RingWindSnapshot snapshot = layer.QueryWindSnapshotAtTime(new RingCanonicalPosition(observer.AroundMeters, 0.0, observer.AcrossMeters), time);
        float dMean = new Vector2(at2.MeanWind.X - snapshot.MeanWind.X, at2.MeanWind.Z - snapshot.MeanWind.Y).Length();
        float dGust = new Vector2(at2.GustWind.X - snapshot.InstantWind.X, at2.GustWind.Z - snapshot.InstantWind.Y).Length();
        Check(at2.Valid && dMean <= 0.5f && dGust <= 0.5f, $"world: 2 m sample differs from the surface snapshot (mean {dMean:0.00}, gust {dGust:0.00})");
        if (verbose) Console.WriteLine($"world: ground={ground:0.0} mean={snapshot.MeanWind} instant={snapshot.InstantWind} d={dMean:0.000}/{dGust:0.000} arrival phase={column.Arrival.Phase} core={column.Arrival.Core:0.00} cloud={column.CloudBaseMeters:0}..{column.CloudTopMeters:0}");
        // Finite column -100..60000 m.
        var altitudes = new float[64]; for (int i = 0; i < altitudes.Length; i++) altitudes[i] = -100f + i * (60100f / 63f);
        var results = new RingWindSample[64];
        RingWindQuery.SampleColumn(observer, time, altitudes, results);
        bool finite = results.All(s => s.Valid && float.IsFinite(s.MeanWind.X) && float.IsFinite(s.TurbulenceSigma) && float.IsFinite(s.UpdraftMetersPerSecond) && float.IsFinite(s.Turbulence.X));
        Check(finite, "world: non-finite sample in the -100..60000 m column");
        // Sigma ordering in the real storm: inside the core and cloud above the clear air over the top.
        float mid = 0.5f * (column.CloudBaseMeters + column.CloudTopMeters);
        RingWindSample inCore = RingWindQuery.Sample(observer with { VerticalMeters = mid }, time);
        RingWindSample aloft = RingWindQuery.Sample(observer with { VerticalMeters = column.CloudTopMeters + 6000.0 }, time);
        Check(column.Arrival.Core > 0.4f && inCore.TurbulenceSigma > aloft.TurbulenceSigma, $"world: core sigma {inCore.TurbulenceSigma:0.00} must exceed clear aloft {aloft.TurbulenceSigma:0.00} (core weight {column.Arrival.Core:0.00})");
        Check(inCore.Precipitation == PrecipitationPhase.InCloud && aloft.Precipitation == PrecipitationPhase.Above, "world: precipitation phases through the column");
        // Determinism, SampleColumn == Sample.
        Check(RingWindQuery.Sample(observer with { VerticalMeters = mid }, time) == inCore, "world: not deterministic");
        var one = new RingWindSample[1];
        RingWindQuery.SampleColumn(observer, time, new float[] { mid }, one);
        Check(one[0] == inCore, "world: SampleColumn differs from Sample");
        // Cost with the column cached (repeated position and time): a column of 32 altitudes.
        var alts = new float[32]; for (int i = 0; i < 32; i++) alts[i] = ground + i * 250f;
        var results32 = new RingWindSample[32];
        RingWindQuery.SampleColumn(observer, time, alts, results32);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 500; i++) RingWindQuery.SampleColumn(observer, time, alts, results32);
        double columnMs = sw.Elapsed.TotalMilliseconds / 500.0;
        sw.Restart();
        for (int i = 0; i < 20; i++) RingWindQuery.SampleColumn(observer with { AroundMeters = observer.AroundMeters + i * 100.0 }, time + i, alts, results32);
        double freshMs = sw.Elapsed.TotalMilliseconds / 20.0;
        if (verbose) Console.WriteLine($"world cost: cached column of 32 = {columnMs:0.000} ms, fresh position/time column = {freshMs:0.000} ms");
        Check(columnMs <= 0.1, $"world: cached 32-altitude column {columnMs:0.000} ms exceeds 0.1 ms");
    }
}
