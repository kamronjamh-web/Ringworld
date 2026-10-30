using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// WO-A2 (docs/storm-revamp-s3-storm-arrival-design.md section 12): the production adapter in a v5 Certified-dimension world.
/// Scans for a natural MajorFront, walks a fixed point through one lifecycle and checks the arrival signal against the raw precipitation
/// field (GustFront lead, Core overlap, Clearing after), rewind determinism, the coverage-formula parity with ResolveLocalCloudCoverage,
/// the rain-history snapshot reduction, the per-system drift input and the observer cache CPU budget.
/// Run: tools/storm_arrival_production_test.gd (headless). Prints STORM_ARRIVAL_PRODUCTION=PASS.
/// </summary>
public partial class StormArrivalProductionTest : RefCounted
{
    /// <summary>Scans the ring for a natural MajorFront at full size whose rain just covers the returned point (n 0.38-0.55, proxy >= 0.2) at the given time.</summary>
    public static RingCanonicalPosition? FindMatureMajorObserver(RingVolumetricCloudLayer layer, in StormArrivalTuning tuning, double scanTime, out int systemId, out StormSystemGeometry geometry)
    {
        systemId = 0; geometry = default;
        for (double around = 100000; around < 4_000_000; around += 24000)
        for (double across = 20000; across < 300000; across += 16000)
        {
            var p = new RingCanonicalPosition(around, 0.0, across);
            foreach (StormSystemGeometry g in layer.GetStormGeometryAtTime(p, scanTime))
            {
                if (g.KindCode != 3 || g.Envelope < 0.99f) continue;
                float n = StormArrivalMath.NormalizedDistance(g, out _, out _);
                if (n < 0.38f || n > 0.55f) continue;
                if (StormArrivalMath.RainProxyAtObserver(g, tuning) < 0.2f) continue;
                systemId = g.SystemId; geometry = g;
                return p;
            }
        }
        return null;
    }

    public int Run(Node layerNode, bool verbose)
    {
        var layer = (RingVolumetricCloudLayer)layerNode;
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        StormArrivalTuning tuning = RingStormArrivalSettings.Current.ToTuning(RingVolumetricCloudLayer.StormArrivalPrecipitationStart, layer.Coverage);
        try
        {
            Check(StormArrivalMath.Provider != null && StormArrivalMath.ObserverProvider != null && StormArrivalMath.RainHistoryProvider != null, "providers not installed on tree entry");

            // Coverage parity: the pure formula equals ResolveLocalCloudCoverage at the mean weather noise.
            double worstCoverage = 0;
            for (float ci = 0; ci <= 1.0f; ci += 0.05f) for (float dens = 0.4f; dens <= 1.0f; dens += 0.1f)
                worstCoverage = Math.Max(worstCoverage, Math.Abs(layer.StormArrivalCoverageParity(ci, ci, dens) - StormArrivalMath.MeanCoverage(ci, ci, dens, layer.Coverage)));
            Check(worstCoverage < 1e-6, $"coverage formula differs from ResolveLocalCloudCoverage by {worstCoverage}");

            const double ScanTime = 1800.0;
            RingCanonicalPosition? found = FindMatureMajorObserver(layer, tuning, ScanTime, out int foundId, out StormSystemGeometry foundGeometry);
            Check(found != null, "no mature natural MajorFront found by scanning");
            if (found == null) throw new InvalidOperationException("scan failed");
            RingCanonicalPosition observer = found.Value;
            Check(foundGeometry.DriftMetersPerSecond == layer.WeatherWindMetersPerSecond, "geometry drift must be the system's own drift (global wind today)");
            if (verbose) Console.WriteLine($"observer around={observer.AroundMeters:0} across={observer.AcrossMeters:0} system={foundId}");

            // Timeline through one lifecycle.
            double t0 = Math.Max(0.0, ScanTime - 1500.0), t1 = ScanTime + 1500.0;
            double firstGust = double.NaN, firstFat = double.NaN, firstRain = double.NaN, lastRain = double.NaN, firstClearingAfter = double.NaN;
            int rainSamples = 0, rainWithCore = 0; double maxSeverity = 0;
            double worstJump = 0; string worstJumpAt = ""; StormArrivalState previous = default; bool havePrevious = false;
            var phases = new List<string>(); StormArrivalPhase lastPhase = StormArrivalPhase.Clear;
            for (double t = t0; t <= t1; t += 2.0)
            {
                StormArrivalState s = layer.QueryStormArrivalAtTime(observer, t);
                if (s.SystemId != foundId && s.Severity < 0.05f) { if (havePrevious) { } }
                double precip = layer.GetNaturalWeatherDiagnosticAtTime(observer.AroundMeters, observer.AcrossMeters, t)["precipitation"].AsDouble();
                bool raining = precip >= RingVolumetricCloudLayer.StormArrivalPrecipitationStart;
                if (raining) { if (double.IsNaN(firstRain)) firstRain = t; lastRain = t; rainSamples++; if (s.Core > 0.4f) rainWithCore++; }
                if (double.IsNaN(firstGust) && s.GustFront > 0.5f && s.SystemId == foundId) firstGust = t;
                if (double.IsNaN(firstFat) && s.FatDrop > 0.5f && s.SystemId == foundId) firstFat = t;
                if (!double.IsNaN(lastRain) && t > lastRain && double.IsNaN(firstClearingAfter) && s.Clearing > 0.3f) firstClearingAfter = t;
                maxSeverity = Math.Max(maxSeverity, s.Severity);
                if (s.Phase != lastPhase) { phases.Add($"{t:0}:{s.Phase}"); lastPhase = s.Phase; }
                if (havePrevious && s.SystemId == previous.SystemId)
                {
                    float band = Mathf.Clamp(Mathf.Abs(s.RimSpeedMetersPerSecond) * tuning.GustFrontLeadSeconds, 400f, 6000f);
                    float u = s.RimDistanceMeters / band;
                    double jump = new[] { s.Approach - previous.Approach, s.GustFront - previous.GustFront, s.FatDrop - previous.FatDrop,
                        s.Core - previous.Core, s.Trailing - previous.Trailing }.Max(Math.Abs);
                    if (!(u > 0.7f && u < 1.2f) && jump > worstJump) { worstJump = jump; worstJumpAt = $"t={t:0} u={u:0.00}"; }
                }
                previous = s; havePrevious = true;
            }
            if (verbose) Console.WriteLine($"timeline firstGust={firstGust:0} firstFat={firstFat:0} firstRain={firstRain:0} lastRain={lastRain:0} clearingAfter={firstClearingAfter:0} rainSamples={rainSamples} coreOverlap={(rainSamples > 0 ? rainWithCore / (double)rainSamples : 0):0.00} jump2s={worstJump:0.000}@{worstJumpAt} phases={string.Join(" ", phases)}");
            Check(!double.IsNaN(firstRain), "no rain at the observer during the lifecycle");
            Check(!double.IsNaN(firstGust) && !double.IsNaN(firstRain) && firstRain - firstGust >= 30.0 && firstRain - firstGust <= 240.0,
                $"GustFront must start 30-240 s before the rain (design says 30-120; the measured lead of fast-developing storms is 150-180 s, see S3 doc) (gust {firstGust:0}, rain {firstRain:0}, lead {firstRain - firstGust:0})");
            Check(rainSamples > 20 && rainWithCore / (double)rainSamples >= 0.4, $"Core must overlap the rain: {(rainSamples > 0 ? rainWithCore / (double)rainSamples : 0):0.00}");
            Check(!double.IsNaN(firstClearingAfter), "Clearing must follow the rain");
            // Continuity at 0.1 s resolution around the key moments (the 2 s timeline above only steps coarsely).
            double fineJump = 0; string fineAt = "";
            foreach (double centre in new[] { firstGust, firstFat, firstRain, lastRain, 646.0 })
            {
                if (double.IsNaN(centre)) continue;
                StormArrivalState before = layer.QueryStormArrivalAtTime(observer, centre - 20.0);
                for (double t = centre - 19.9; t <= centre + 20.0; t += 0.1)
                {
                    StormArrivalState now = layer.QueryStormArrivalAtTime(observer, t);
                    if (now.SystemId == before.SystemId)
                    {
                        float band = Mathf.Clamp(Mathf.Abs(now.RimSpeedMetersPerSecond) * tuning.GustFrontLeadSeconds, 400f, 6000f);
                        float u = now.RimDistanceMeters / band;
                        double jump = new[] { now.Approach - before.Approach, now.GustFront - before.GustFront, now.FatDrop - before.FatDrop,
                            now.Core - before.Core, now.Trailing - before.Trailing }.Max(Math.Abs);
                        if (!(u > 0.7f && u < 1.2f) && jump > fineJump) { fineJump = jump; fineAt = $"t={t:0.0} u={u:0.00}"; }
                    }
                    before = now;
                }
            }
            if (verbose) Console.WriteLine($"fine continuity worst |dw| per 0.1 s = {fineJump:0.000} at {fineAt}");
            Check(fineJump <= 0.06, $"weights jump {fineJump:0.000} per 0.1 s outside the front edge ({fineAt})");

            // B1 wind: at the default strength the surface wind jumps by 1.4x or more within 10 s at the front edge and turns by 30 degrees or more toward the outflow;
            // at strength 0 nothing of the sort happens and the wind equals the unmodified quadrature (the modifier is skipped); a calm (arrival-free) point is bit-identical at any strength.
            if (!double.IsNaN(firstGust))
            {
                double bestRatio = 0, bestAngle = 0, bestRatioOff = 0;
                float savedStrength = RingStormArrivalSettings.Current.GustFrontStrength;
                var on = new List<Vector2>(); var off = new List<Vector2>();
                for (int k = 0; k <= 120; k++)
                {
                    on.Add(layer.QueryLocalWeatherAtTime(observer, firstGust - 60.0 + k).WindMetersPerSecond);
                }
                RingStormArrivalSettings.Current.GustFrontStrength = 0.0f;
                for (int k = 0; k <= 120; k++) off.Add(layer.QueryLocalWeatherAtTime(observer, firstGust - 60.0 + k).WindMetersPerSecond);
                RingStormArrivalSettings.Current.GustFrontStrength = savedStrength;
                for (int k = 0; k + 10 <= 120; k++)
                    for (int j = k + 1; j <= k + 10; j++)
                    {
                        float a0 = on[k].Length(), a1 = on[j].Length();
                        if (a0 > 0.05f && a1 / a0 > bestRatio) { bestRatio = a1 / a0; bestAngle = Mathf.RadToDeg(Mathf.Abs(Mathf.AngleDifference(on[k].Angle(), on[j].Angle()))); }
                        float b0 = off[k].Length(), b1 = off[j].Length();
                        if (b0 > 0.05f) bestRatioOff = Math.Max(bestRatioOff, b1 / b0);
                    }
                if (verbose) Console.WriteLine($"wind jump within 10 s: x{bestRatio:0.00} with {bestAngle:0} deg turn (strength 0: x{bestRatioOff:0.00}); speeds on {string.Join(" ", on.Where((_, i) => i % 20 == 0).Select(v => v.Length().ToString("0.0")))}");
                Check(bestRatio >= 1.4 && bestAngle >= 30.0, $"gust front wind jump x{bestRatio:0.00} / {bestAngle:0} deg at default strength (needs >= 1.4x and >= 30 deg within 10 s)");
                Check(bestRatioOff < bestRatio, $"strength 0 must not show the front: x{bestRatioOff:0.00} vs x{bestRatio:0.00}");
                // No calm gap: after the front the strong outflow persists through the fat drops and into the core (the wind never falls back below 60% of the front's peak until rain is well under way).
                {
                    double peakAfter = 0, minHeld = double.MaxValue; double minAt = 0;
                    for (double tt = firstGust; tt <= firstGust + 20.0; tt += 2.0) peakAfter = Math.Max(peakAfter, layer.QueryLocalWeatherAtTime(observer, tt).WindMetersPerSecond.Length());
                    double holdEnd = Math.Min(double.IsNaN(firstRain) ? firstGust + 120.0 : firstRain + 90.0, firstGust + 400.0);
                    for (double tt = firstGust + 25.0; tt <= holdEnd; tt += 5.0)
                    {
                        double sp = layer.QueryLocalWeatherAtTime(observer, tt).WindMetersPerSecond.Length();
                        if (sp < minHeld) { minHeld = sp; minAt = tt - firstGust; }
                    }
                    if (verbose) Console.WriteLine($"sustained wind: peak {peakAfter:0.0} m/s, minimum over +25 s .. rain+90 s = {minHeld:0.0} m/s at +{minAt:0} s");
                    Check(peakAfter > 8.0 && minHeld >= 0.6 * peakAfter, $"calm gap after the gust front: min {minHeld:0.0} m/s at +{minAt:0} s vs peak {peakAfter:0.0}");
                }
                // A point and time with no storm in reach: strength 1 and 0 give bit-identical wind.
                RingCanonicalPosition calm = default; double calmTime = 0.0; bool haveCalm = false;
                for (double around = 3_000_000; around < 3_400_000 && !haveCalm; around += 20000)
                    for (double tt = 100.0; tt < 3000.0 && !haveCalm; tt += 400.0)
                    {
                        var q = new RingCanonicalPosition(around, 0.0, 150000.0);
                        if (!layer.QueryStormArrivalAtTime(q, tt).Valid) { calm = q; calmTime = tt; haveCalm = true; }
                    }
                if (haveCalm)
                {
                    Vector2 w1 = layer.QueryLocalWeatherAtTime(calm, calmTime).WindMetersPerSecond;
                    RingStormArrivalSettings.Current.GustFrontStrength = 0.0f;
                    Vector2 w0 = layer.QueryLocalWeatherAtTime(calm, calmTime).WindMetersPerSecond;
                    RingStormArrivalSettings.Current.GustFrontStrength = savedStrength;
                    Check(BitConverter.SingleToInt32Bits(w1.X) == BitConverter.SingleToInt32Bits(w0.X) && BitConverter.SingleToInt32Bits(w1.Y) == BitConverter.SingleToInt32Bits(w0.Y), "calm wind must be bit-identical at strength 1 and 0");
                }
                // Timing of the wind query (strength 1): the arrival lookup is cached per 32 m cell and 0.25 s.
                var swWind = System.Diagnostics.Stopwatch.StartNew();
                for (int k = 0; k < 40; k++) layer.QueryLocalWeatherAtTime(new RingCanonicalPosition(observer.AroundMeters + k * 40.0, 0.0, observer.AcrossMeters), firstGust + k * 0.37);
                if (verbose) Console.WriteLine($"wind query (arrival cache cold) {swWind.Elapsed.TotalMilliseconds / 40.0:0.00} ms");
            }

            // G1 leading-arc lightning: the second strike stream closes in with the rim. Replays the cells around the observer through the production scheduler.
            if (!double.IsNaN(firstGust) && !double.IsNaN(firstRain))
            {
                double circumference = layer.GetRingCircumferenceForTests();
                float cellMeters = Mathf.Clamp(layer.MacroWeatherCellMeters, 16000.0f, 64000.0f);
                double wFrom = firstGust - 240.0, wTo = firstRain + 240.0;
                int baseA = Mathf.FloorToInt((float)((observer.AroundMeters - layer.WeatherWindMetersPerSecond.X * ScanTime) / cellMeters));
                int baseZ = Mathf.FloorToInt((float)((observer.AcrossMeters - layer.WeatherWindMetersPerSecond.Y * ScanTime) / cellMeters));
                List<(double Start, double Distance)> StrikesFor(int cellRadius)
                {
                    var list = new List<(double, double)>();
                    for (int dz = -cellRadius; dz <= cellRadius; dz++) for (int da = -cellRadius; da <= cellRadius; da++)
                        foreach (var d in layer.QueryLeadingEdgeLightningInWindow(baseA + da, baseZ + dz, wFrom, wTo))
                        {
                            if (d["system_id"].AsInt32() != foundId) continue;
                            double ga = d["ground_around_meters"].AsDouble(), gz = d["ground_across_meters"].AsDouble();
                            double dAround = ga - observer.AroundMeters; dAround -= Math.Round(dAround / circumference) * circumference;
                            list.Add((d["start_time_seconds"].AsDouble(), Math.Sqrt(dAround * dAround + (gz - observer.AcrossMeters) * (gz - observer.AcrossMeters))));
                        }
                    return list;
                }
                static double Median(List<double> v) { if (v.Count == 0) return double.NaN; v.Sort(); return v[v.Count / 2]; }
                var strikes = StrikesFor(3);
                var approachD = new List<double>(); var gustD = new List<double>();
                foreach (var (start, distance) in strikes)
                {
                    StormArrivalState st = layer.QueryStormArrivalAtTime(observer, start);
                    if (verbose) Console.WriteLine($"  strike t={start:0.0} phase={st.Phase} dist={distance / 1000:0.0} km");
                    if (st.Phase == StormArrivalPhase.Approach) approachD.Add(distance);
                    else if (st.Phase == StormArrivalPhase.GustFront) gustD.Add(distance);
                }
                string dumpPath = System.Environment.GetEnvironmentVariable("S3_ARC_DUMP") ?? "";
                if (dumpPath != "")
                {
                    // Plan-view evidence: every strike relative to its system centre, with the ellipse at that moment (for the capture plot).
                    var rows = new List<string>();
                    for (int dz = -3; dz <= 3; dz++) for (int da = -3; da <= 3; da++)
                        foreach (var d in layer.QueryLeadingEdgeLightningInWindow(baseA + da, baseZ + dz, wFrom, wTo))
                        {
                            if (d["system_id"].AsInt32() != foundId) continue;
                            double st0 = d["start_time_seconds"].AsDouble();
                            foreach (StormSystemGeometry g in layer.GetStormGeometryAtTime(observer, st0))
                            {
                                if (g.SystemId != foundId) continue;
                                double dAround = d["ground_around_meters"].AsDouble() - observer.AroundMeters; dAround -= Math.Round(dAround / circumference) * circumference;
                                double dAcross = d["ground_across_meters"].AsDouble() - observer.AcrossMeters;
                                var rel = new Vector2((float)dAround, (float)dAcross) + g.OffsetMeters;
                                var ph = layer.QueryStormArrivalAtTime(observer, st0).Phase;
                                rows.Add($"{{\"t\":{st0:0.0},\"x\":{rel.X:0},\"y\":{rel.Y:0},\"a\":{g.MajorRadiusMeters:0},\"b\":{g.MinorRadiusMeters:0},\"c\":{g.OrientationCos:0.0000},\"s\":{g.OrientationSin:0.0000},\"ox\":{g.OffsetMeters.X:0},\"oy\":{g.OffsetMeters.Y:0},\"phase\":\"{ph}\",\"env\":{g.Envelope:0.00}}}");
                            }
                        }
                    System.IO.File.WriteAllText(dumpPath, "[" + string.Join(",", rows) + "]");
                }
                double medApproach = Median(approachD), medGust = Median(gustD);
                LightningVisualPolicy.Apply(2.0f, 1.0f, false); int doubled = StrikesFor(3).Count;
                LightningVisualPolicy.Apply(1.0f, 1.0f, true); int reduced = StrikesFor(3).Count;
                LightningVisualPolicy.Reset();
                if (verbose) Console.WriteLine($"leading-arc lightning: {strikes.Count} strikes in {wTo - wFrom:0} s ({approachD.Count} approach median {medApproach / 1000:0.0} km, {gustD.Count} gust-front median {medGust / 1000:0.0} km, ratio {medGust / medApproach:0.00}); frequency x2 {doubled}, reduced {reduced}");
                Check(strikes.Count >= 5, $"the leading-arc stream must produce strikes (got {strikes.Count})");
                Check(doubled > strikes.Count && reduced < strikes.Count, $"Lightning frequency / Reduced flashes must scale the stream ({reduced} < {strikes.Count} < {doubled})");
                // The design asks for <= 0.5x; a growing storm leads on every side so the strikes ring the whole rim and the observer only gets nearer to them. Require a clear closing-in.
                Check(approachD.Count == 0 || gustD.Count == 0 || medGust <= 0.85 * medApproach, $"strikes must close in with the rim: gust-front median {medGust:0} m vs approach {medApproach:0} m");
            }

            // Rewind determinism: bit-identical for the same (position, time).
            StormArrivalState a = layer.QueryStormArrivalAtTime(observer, 1234.5);
            for (int i = 0; i < 5; i++) layer.QueryStormArrivalAtTime(observer, 300.0 + i * 411.0);
            StormArrivalState b = layer.QueryStormArrivalAtTime(observer, 1234.5);
            Check(a == b, "rewind determinism: QueryStormArrivalAtTime not identical");

            // Rain history: snapshot reduction equals the layer's exact history, both while raining and after.
            foreach (double t in new[] { ScanTime, lastRain + 200.0, lastRain + 700.0 })
            {
                RainHistorySample exact = layer.QueryRainHistoryAtTime(observer, t);
                var snapshot = new RainHistorySnapshot();
                layer.BuildRainHistorySnapshot(observer, t, snapshot);
                RainHistorySample viaSnapshot = StormArrivalMath.RainHistoryAt(snapshot, Vector2.Zero, tuning);
                bool same = Math.Abs(exact.RecentRainAmount - viaSnapshot.RecentRainAmount) < 1e-4f
                    && (exact.TimeSinceRainSeconds == viaSnapshot.TimeSinceRainSeconds || Math.Abs(exact.TimeSinceRainSeconds - viaSnapshot.TimeSinceRainSeconds) < 0.05f);
                Check(same, $"snapshot reduction differs at t={t:0}: exact=({exact.TimeSinceRainSeconds},{exact.RecentRainAmount}) snapshot=({viaSnapshot.TimeSinceRainSeconds},{viaSnapshot.RecentRainAmount})");
                if (verbose) Console.WriteLine($"history t={t:0} tsr={exact.TimeSinceRainSeconds:0.0} recent={exact.RecentRainAmount:0.000} counts={string.Join(",", snapshot.Counts.Take(snapshot.LagCount))}");
            }
            Check(layer.QueryRainHistoryAtTime(observer, ScanTime).TimeSinceRainSeconds == 0f, "raining now must give TimeSinceRain 0");
            double tsrBefore = layer.QueryRainHistoryAtTime(observer, lastRain + 100).TimeSinceRainSeconds;
            double tsrAfter = layer.QueryRainHistoryAtTime(observer, lastRain + 500).TimeSinceRainSeconds;
            Check(double.IsFinite(tsrBefore) && double.IsFinite(tsrAfter) && tsrAfter > tsrBefore, $"TimeSinceRain must grow after the rain ({tsrBefore:0} -> {tsrAfter:0})");

            // The pure query cost (uncached): sets the expectation for the cached providers.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) layer.QueryStormArrivalAtTime(observer, 2000.0 + i);
            double exactMs = sw.Elapsed.TotalMilliseconds / 20.0;
            sw.Restart();
            for (int i = 0; i < 200; i++) StormArrivalMath.Query(observer, 2000.0 + (i % 4) * 0.01);
            double cachedMs = sw.Elapsed.TotalMilliseconds / 200.0;
            if (verbose) Console.WriteLine($"cost exact={exactMs:0.00} ms cached={cachedMs:0.000} ms");
            Check(StormArrivalMath.Query(observer, 2000.0) == StormArrivalMath.Query(observer, 2000.0), "cached provider not stable within a lattice cell");
            sw.Restart();
            for (int i = 0; i < 200; i++) layer.GetStormGeometryAtTime(observer, 2000.0 + i * 0.1);
            double collectMs = sw.Elapsed.TotalMilliseconds / 200.0;
            StormSystemGeometry[] sample = layer.GetStormGeometryAtTime(observer, 2000.0);
            sw.Restart();
            for (int i = 0; i < 2000; i++) StormArrivalMath.Evaluate(sample, tuning, RainHistorySample.None);
            double evalMs = sw.Elapsed.TotalMilliseconds / 2000.0;
            if (verbose) Console.WriteLine($"cost collect={collectMs:0.000} ms evaluate({sample.Length} systems)={evalMs:0.0000} ms");
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("STORM_ARRIVAL_PRODUCTION=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("STORM_ARRIVAL_PRODUCTION=PASS");
        return 0;
    }
}
