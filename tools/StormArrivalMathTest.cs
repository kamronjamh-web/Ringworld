using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// WO-A1 (docs/storm-revamp-s3-storm-arrival-design.md section 12): pure arrival maths on synthetic systems. Phase order for a growth /
/// mature / dissipate cycle, a drift-only pass at an exaggerated 15 m/s drift, and a walk-in; weight continuity; analytic dn/dt vs a finite
/// difference; rewind determinism; rim-distance sign; RimLeadingWeight reference values; hail; rain history continuity and the snapshot reduction.
/// Run: Godot --headless --path . --script res://tools/storm_arrival_math_test.gd. Prints STORM_ARRIVAL_MATH=PASS.
/// </summary>
public partial class StormArrivalMathTest : RefCounted
{
    // Lifecycle of the synthetic system (same shape as EvaluateWeatherLifecycle with no variation).
    const double Birth = 120, Develop = 300, Mature = 900, Dissipate = 360;
    const float BaseMajor = 40000f, BaseMinor = 15000f, BaseDensity = 0.85f, BasePotential = 0.8f, Orientation = 0.3f;

    static double NDouble(StormSystemGeometry g)
    {
        double a = g.OrientationCos * (double)g.OffsetMeters.X + g.OrientationSin * (double)g.OffsetMeters.Y;
        double c = -g.OrientationSin * (double)g.OffsetMeters.X + g.OrientationCos * (double)g.OffsetMeters.Y;
        return Math.Sqrt(a * a / ((double)g.MajorRadiusMeters * g.MajorRadiusMeters) + c * c / ((double)g.MinorRadiusMeters * g.MinorRadiusMeters));
    }

    /// <summary>Half the distance between the two history lags that bracket t: the best resolution a lag sampling can offer.</summary>
    static double HalfSpacing(double t)
    {
        double prev = 0;
        foreach (float lag in StormArrivalMath.HistoryLagsSeconds) { if (lag >= t) return 0.5 * (lag - prev); prev = lag; }
        return 0.5 * 540;
    }

    static float SmoothUnit(float v) { float t = Math.Clamp(v, 0f, 1f); return t * t * (3f - 2f * t); }

    static float EnvelopeAt(double t)
    {
        if (t <= 0) return 0.0001f;
        if (t < Birth) return SmoothUnit((float)(t / Birth)) * 0.3f;
        t -= Birth;
        if (t < Develop) return Mathf.Lerp(0.3f, 1f, SmoothUnit((float)(t / Develop)));
        t -= Develop;
        if (t < Mature) return 1f;
        t -= Mature;
        if (t < Dissipate) return 1f - SmoothUnit((float)(t / Dissipate));
        return 0f;
    }

    static float CodeAt(double t)
    {
        if (t < Birth) return 0.3f * (float)(t / Birth);
        t -= Birth;
        if (t < Develop) return 0.3f + 0.7f * (float)(t / Develop);
        t -= Develop;
        if (t < Mature) return 1f + (float)(t / Mature);
        t -= Mature;
        if (t < Dissipate) return 2f + (float)(t / Dissipate);
        return 3f;
    }

    /// <summary>Synthetic system at time t; lifecycle=false holds the envelope at 1 (a mature drifting system).</summary>
    static StormSystemGeometry Sys(double t, Vector2 observer, Vector2 centre0, Vector2 drift, bool lifecycle = true, int kind = 3, int id = 4242,
        float potential = BasePotential, float density = BaseDensity)
    {
        float e = lifecycle ? Math.Max(EnvelopeAt(t), 1e-4f) : 1f;
        float rho = 0f;
        if (lifecycle && e > 1e-5f)
            rho = (EnvelopeAt(t + 1) - EnvelopeAt(Math.Max(0, t - 1))) / (float)(t >= 1 ? 2 : 1 + t) / e;
        Vector2 centre = centre0 + drift * (float)t;
        return new StormSystemGeometry(id, kind, observer - centre, BaseMajor * e, BaseMinor * e, Mathf.Cos(Orientation), Mathf.Sin(Orientation),
            e, rho, drift, density * e, potential * e, lifecycle ? CodeAt(t) : 1.5f);
    }

    static RainHistorySample HistoryAt(Func<double, StormSystemGeometry> systemAt, double t, in StormArrivalTuning tuning)
    {
        Span<float> proxies = stackalloc float[StormArrivalMath.HistoryLagsSeconds.Length + 1];
        for (int l = 0; l < proxies.Length; l++)
        {
            float lag = l == 0 ? 0 : StormArrivalMath.HistoryLagsSeconds[l - 1];
            StormSystemGeometry g = systemAt(Math.Max(0, t - lag));
            proxies[l] = StormArrivalMath.RainProxyAtObserver(g, tuning);
        }
        return StormArrivalMath.ReduceHistory(proxies, tuning);
    }

    static StormArrivalState StateAt(Func<double, StormSystemGeometry> systemAt, double t, in StormArrivalTuning tuning)
    {
        var one = new[] { systemAt(t) };
        return StormArrivalMath.Evaluate(one, tuning, HistoryAt(systemAt, t, tuning));
    }

    static int Rank(StormArrivalPhase p) => p switch
    {
        StormArrivalPhase.Approach => 1, StormArrivalPhase.GustFront => 2, StormArrivalPhase.FatDrop => 3,
        StormArrivalPhase.Core => 4, StormArrivalPhase.Trailing => 5, StormArrivalPhase.Clearing => 6, _ => 0
    };

    /// <summary>First-occurrence times of each phase over [t0, t1] at the given step.</summary>
    static double[] FirstTimes(Func<double, StormSystemGeometry> systemAt, double t0, double t1, double step, in StormArrivalTuning tuning, List<string>? trace = null)
    {
        var first = new double[7]; Array.Fill(first, double.NaN);
        StormArrivalPhase last = StormArrivalPhase.Clear;
        for (double t = t0; t <= t1; t += step)
        {
            StormArrivalState s = StateAt(systemAt, t, tuning);
            int r = Rank(s.Phase);
            if (double.IsNaN(first[r])) first[r] = t;
            if (trace != null && s.Phase != last) { trace.Add($"{t:0}:{s.Phase}"); last = s.Phase; }
        }
        return first;
    }

    static bool Ordered(double[] first, out string detail, params int[] ranks)
    {
        detail = string.Join(",", ranks.Select(r => $"{(StormArrivalPhase)r}@{first[r]:0}"));
        for (int i = 0; i < ranks.Length; i++) if (double.IsNaN(first[ranks[i]])) return false;
        for (int i = 1; i < ranks.Length; i++) if (!(first[ranks[i - 1]] < first[ranks[i]])) return false;
        return true;
    }

    static string Bits(StormArrivalState s)
        => string.Join("|", new[] { s.Approach, s.GustFront, s.FatDrop, s.Core, s.Trailing, s.Clearing, s.RimDistanceMeters, s.RimSpeedMetersPerSecond,
            s.HailIntensity, s.HailSize, s.CoolingOffset, s.Severity }.Select(f => BitConverter.SingleToInt32Bits(f).ToString())) + "|" + s.Phase;

    public int Run(bool verbose)
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        var tuning = StormArrivalTuning.Default;
        try
        {
            RunMegastorm(Check, verbose);
            // ---- analytic dn/dt vs finite difference (envelope changing, drift on) ----
            int ratePoints = 0; double worstRate = 0;
            foreach (double t in new[] { 150.0, 300.0, 410.0, 1500.0, 1800.0 })
            foreach (Vector2 drift in new[] { new Vector2(2.2f, 0.7f), new Vector2(15f, -6f) })
            foreach (Vector2 observer in new[] { new Vector2(16000f, 3000f), new Vector2(-30000f, 9000f), new Vector2(5000f, -22000f) })
            {
                Func<double, StormSystemGeometry> at = x => Sys(x, observer, new Vector2(20000f, 4000f), drift);
                StormSystemGeometry g = at(t);
                float n0 = StormArrivalMath.NormalizedDistance(g, out _, out _);
                if (n0 < 0.05f) continue;
                double h = 0.5;
                double fd = (NDouble(at(t + h)) - NDouble(at(t - h))) / (2 * h);
                double analytic = StormArrivalMath.NormalizedDistanceRate(g);
                // The synthetic envelope is not analytic in rho beyond one step; compare relative to the larger of the two magnitudes.
                double scale = Math.Max(Math.Abs(fd), Math.Abs(analytic));
                if (scale < 1e-6) continue;
                double rel = Math.Abs(fd - analytic) / scale;
                if (verbose && rel > 0.01) Console.WriteLine($"rate t={t} drift={drift} obs={observer} fd={fd:0.000000} analytic={analytic:0.000000}");
                worstRate = Math.Max(worstRate, rel); ratePoints++;
            }
            Check(ratePoints >= 20 && worstRate <= 0.01, $"dn/dt analytic vs finite difference worst={worstRate:0.0000} over {ratePoints} points (limit 0.01)");

            // ---- growth / mature / dissipate cycle at a fixed point ----
            Vector2 centre = new(20000f, 4000f);
            Vector2 stillWind = new(2.2f, 0.7f);
            Vector2 growthObserver = centre + new Vector2(Mathf.Cos(Orientation), Mathf.Sin(Orientation)) * 16000f;   // along the major axis, n = 0.4 at full size
            Func<double, StormSystemGeometry> growth = x => Sys(x, growthObserver, centre, stillWind);
            var trace = new List<string>();
            double[] first = FirstTimes(growth, 0, 1900, 1.0, tuning, trace);
            Check(Ordered(first, out string growthDetail, 1, 2, 3, 4, 5, 6), "growth cycle phase order: " + growthDetail + " trace=" + string.Join(" ", trace));
            if (verbose) Console.WriteLine("growth: " + growthDetail + " trace=" + string.Join(" ", trace));

            // ---- weights continuous (|dw| <= 0.05 per 0.1 s) outside the 12% front edge ----
            double worstJump = 0; string worstAt = "";
            StormArrivalState prev = StateAt(growth, 0, tuning);
            for (double t = 0.1; t <= 1900; t += 0.1)
            {
                StormArrivalState cur = StateAt(growth, t, tuning);
                StormSystemGeometry g = growth(t);
                float n = StormArrivalMath.NormalizedDistance(g, out float ray, out float dist);
                float vr = StormArrivalMath.RimSpeed(g);
                float band = Mathf.Clamp(Mathf.Abs(vr) * tuning.GustFrontLeadSeconds, 400f, 6000f);
                float u = cur.RimDistanceMeters / band;
                bool frontEdge = u > 0.80f && u < 1.08f;
                if (!frontEdge)
                {
                    double jump = new[] { cur.Approach - prev.Approach, cur.GustFront - prev.GustFront, cur.FatDrop - prev.FatDrop, cur.Core - prev.Core,
                        cur.Trailing - prev.Trailing, cur.Clearing - prev.Clearing }.Max(Math.Abs);
                    if (jump > worstJump) { worstJump = jump; worstAt = $"t={t:0.0} u={u:0.00} prev=({prev.Approach:0.00},{prev.GustFront:0.00},{prev.FatDrop:0.00},{prev.Core:0.00},{prev.Trailing:0.00},{prev.Clearing:0.00}) cur=({cur.Approach:0.00},{cur.GustFront:0.00},{cur.FatDrop:0.00},{cur.Core:0.00},{cur.Trailing:0.00},{cur.Clearing:0.00}) vr={cur.RimSpeedMetersPerSecond:0.0}"; }
                }
                prev = cur;
            }
            Check(worstJump <= 0.05, $"weights discontinuous: worst |dw|={worstJump:0.000} at {worstAt} per 0.1 s (limit 0.05)");

            if (verbose) foreach (double dt in new[] { 1673.2, 1673.3, 1673.4, 1673.5 })
            {
                StormSystemGeometry dg = growth(dt); StormArrivalState ds = StateAt(growth, dt, tuning);
                StormArrivalMath.ResolveRims(dg.Density, dg.PrecipitationPotential, tuning, out float gr, out float rr, out float pr);
                Console.WriteLine($"dbg t={dt} E={dg.Envelope:0.00000} rho={dg.EnvelopeRatePerSecond:0.0000} gustRim={gr:0.000} rainRim={rr:0.000} presence={pr:0.000} rimDist={ds.RimDistanceMeters:0.0} v={ds.RimSpeedMetersPerSecond:0.000} appr={ds.Approach:0.000} peakRatio={StormArrivalMath.RainProxy(0, dg.Density, dg.PrecipitationPotential, tuning.BaseCoverage)/tuning.PrecipitationStart:0.000}");
            }
            // ---- rim speed vs the finite difference of the signed rim distance near the rim ----
            int rimPoints = 0; double worstRim = 0;
            for (double t = 130; t < 1900; t += 0.5)
            {
                StormArrivalState s0 = StateAt(growth, t, tuning);
                if (!s0.Valid || Math.Abs(s0.RimDistanceMeters) > 30f || Math.Abs(s0.RimSpeedMetersPerSecond) < 0.5f) continue;
                double fdSpeed = -(StateAt(growth, t + 0.5, tuning).RimDistanceMeters - StateAt(growth, t - 0.5, tuning).RimDistanceMeters) / 1.0;
                worstRim = Math.Max(worstRim, Math.Abs(fdSpeed - s0.RimSpeedMetersPerSecond) / Math.Max(1.0, Math.Abs(s0.RimSpeedMetersPerSecond)));
                rimPoints++;
            }
            Check(rimPoints >= 2 && worstRim <= 0.06, $"rim speed vs finite difference of the rim distance worst={worstRim:0.000} over {rimPoints} samples (limit 0.06)");

            // ---- rim distance sign and rain rim ----
            StormSystemGeometry mature = Sys(Birth + Develop + 100, growthObserver, centre, stillWind);
            Check(StormArrivalMath.Evaluate(mature, tuning).RimDistanceMeters < 0f, "rim distance must be negative inside the rain");
            StormSystemGeometry outside = Sys(Birth + Develop + 100, centre + new Vector2(Mathf.Cos(Orientation), Mathf.Sin(Orientation)) * 38000f, centre, stillWind);
            Check(StormArrivalMath.Evaluate(outside, tuning).RimDistanceMeters > 0f, "rim distance must be positive outside the rain");
            float rim = StormArrivalMath.SolveRainRim(BaseDensity, BasePotential, tuning.PrecipitationStart, tuning.BaseCoverage);
            Check(rim > 0.3f && rim < 0.9f, $"rain rim n={rim}");
            float precipAtRim = StormArrivalMath.RainProxy(rim, BaseDensity, BasePotential, tuning.BaseCoverage);
            Check(Math.Abs(precipAtRim - tuning.PrecipitationStart) < 0.005f, $"rain proxy at the rim {precipAtRim} must equal the start threshold");
            Check(StormArrivalMath.SolveRainRim(0.5f, 0.05f, tuning.PrecipitationStart, tuning.BaseCoverage) == 0f, "dry system must have no rain rim");
            StormSystemGeometry dry = Sys(Birth + Develop + 100, centre, centre, stillWind, true, 1, 77, 0.05f, 0.5f);
            StormArrivalState dryState = StormArrivalMath.Evaluate(dry, tuning);
            Check(dryState.FatDrop == 0f && dryState.Core == 0f && dryState.Trailing == 0f, "dry system must have zero rain weights");

            // ---- drift-only pass at exaggerated drift 15 m/s (storm travels over the observer, no lifecycle) ----
            Vector2 fast = new(15f, 0f);
            Vector2 passObserver = new(0f, 0f);
            Vector2 passStart = new(-70000f, 0f);      // centre starts 70 km behind... moves toward +x, over the observer
            Func<double, StormSystemGeometry> pass = x =>
            {
                StormSystemGeometry g = Sys(x, passObserver, passStart, fast, false);
                return g with { OrientationCos = 1f, OrientationSin = 0f };
            };
            var passTrace = new List<string>();
            double[] passFirst = FirstTimes(pass, 0, 9000, 2.0, tuning, passTrace);
            Check(Ordered(passFirst, out string passDetail, 1, 2, 3, 4, 5, 6), "drift-only pass phase order: " + passDetail + " trace=" + string.Join(" ", passTrace));
            if (verbose) Console.WriteLine("pass: " + passDetail + " trace=" + string.Join(" ", passTrace));

            // ---- walk-in: static mature storm, observer walks inward at 6 m/s from outside the cloud ----
            Func<double, StormSystemGeometry> walk = x =>
            {
                Vector2 observer = centre + new Vector2(Mathf.Cos(Orientation), Mathf.Sin(Orientation)) * (40000f - 6f * (float)x);
                StormSystemGeometry g = Sys(Birth + Develop + 100, observer, centre, new Vector2(2.2f, 0.7f), false);
                return g;
            };
            // History for a walker: the point moves, so evaluate wetness only from the instantaneous proxy (lags reuse the same geometry here).
            var walkFirst = new double[7]; Array.Fill(walkFirst, double.NaN);
            for (double x = 0; x <= 5000; x += 1)
            {
                var one = new[] { walk(x) };
                StormArrivalState s = StormArrivalMath.Evaluate(one, tuning, RainHistorySample.None);
                int r = Rank(s.Phase); if (double.IsNaN(walkFirst[r])) walkFirst[r] = x;
            }
            Check(Ordered(walkFirst, out string walkDetail, 2, 3, 4), "walk-in must reach GustFront, FatDrop then Core: " + walkDetail);
            Check(!double.IsNaN(walkFirst[2]), "walk-in must feel a gust front");

            // ---- lead setting changes the band ----
            StormSystemGeometry fastRim = Sys(300, growthObserver, centre, stillWind);
            StormArrivalState shortLead = StormArrivalMath.Evaluate(fastRim, tuning with { GustFrontLeadSeconds = 20f });
            StormArrivalState longLead = StormArrivalMath.Evaluate(fastRim, tuning with { GustFrontLeadSeconds = 180f });
            Check(shortLead.Valid && longLead.Valid, "lead evaluation");

            // ---- rewind determinism ----
            string a1 = Bits(StateAt(growth, 431.25, tuning));
            for (int i = 0; i < 20; i++) StateAt(growth, 100 + i * 77.7, tuning);
            string a2 = Bits(StateAt(growth, 431.25, tuning));
            Check(a1 == a2, "rewind determinism: evaluation at the same time is not bit-identical");

            // ---- RimLeadingWeight reference values (shared with the H1 shelf bake) ----
            Check(StormArrivalMath.RimLeadingWeight(0f) == 0f && StormArrivalMath.RimLeadingWeight(-5f) == 0f, "RimLeadingWeight(<=0) must be 0");
            Check(Math.Abs(StormArrivalMath.RimLeadingWeight(1.5f) - 0.5f) < 1e-6f && StormArrivalMath.RimLeadingWeight(3f) == 1f && StormArrivalMath.RimLeadingWeight(10f) == 1f, "RimLeadingWeight reference values");
            Vector2 w = new(2f, 0f);
            float east = StormArrivalMath.RimNormalSpeed(w, 0.001f, new Vector2(10000f, 0f), 1f, 0f, 10000f, 10000f);
            float west = StormArrivalMath.RimNormalSpeed(w, 0.001f, new Vector2(-10000f, 0f), 1f, 0f, 10000f, 10000f);
            float north = StormArrivalMath.RimNormalSpeed(w, 0.001f, new Vector2(0f, 10000f), 1f, 0f, 10000f, 10000f);
            Check(Math.Abs(east - 12f) < 1e-3f && Math.Abs(west - 8f) < 1e-3f && Math.Abs(north - 10f) < 1e-3f, $"RimNormalSpeed circle values east={east} west={west} north={north}");
            // Drift only (rho 0): the rim advances on the downwind side and retreats upwind.
            Check(Math.Abs(StormArrivalMath.RimLeadingWeight(w, 0f, new Vector2(10000f, 0f), 1f, 0f, 10000f, 10000f) - StormArrivalMath.SmoothStep(0f, 3f, 2f)) < 1e-6f
                && StormArrivalMath.RimLeadingWeight(w, 0f, new Vector2(-10000f, 0f), 1f, 0f, 10000f, 10000f) == 0f, "RimLeadingWeight leading/trailing side");

            // ---- kind severity ordering ----
            Check(StormArrivalMath.KindWeight(1) < StormArrivalMath.KindWeight(2) && StormArrivalMath.KindWeight(2) < StormArrivalMath.KindWeight(3)
                && StormArrivalMath.KindWeight(3) < StormArrivalMath.KindWeight(4) && StormArrivalMath.KindWeight(4) < StormArrivalMath.KindWeight(5), "kind weight ordering");

            // ---- hail ----
            int hailFound = 0, belowMajor = 0, hailZeroScale = 0;
            float maxHailSize = 0f;
            for (int id = 1; id <= 60; id++)
            {
                for (float dx = -30000f; dx <= 30000f; dx += 2500f)
                for (float dy = -15000f; dy <= 15000f; dy += 2500f)
                {
                    StormSystemGeometry g = Sys(Birth + Develop + 400, new Vector2(dx, dy), Vector2.Zero, stillWind, true, 3, id);
                    StormArrivalState on = StormArrivalMath.Evaluate(g, tuning);
                    if (on.HailIntensity > 0.05f) { hailFound++; maxHailSize = Math.Max(maxHailSize, on.HailSize); }
                    if (StormArrivalMath.Evaluate(g, tuning with { Hail = 0f }).HailIntensity != 0f) hailZeroScale++;
                    StormSystemGeometry low = g with { KindCode = 2 };
                    if (StormArrivalMath.Evaluate(low, tuning).HailIntensity != 0f) belowMajor++;
                    StormArrivalState again = StormArrivalMath.Evaluate(g, tuning);
                    Check(again.HailIntensity == on.HailIntensity && again.HailSize == on.HailSize, "hail not deterministic");
                }
            }
            Check(hailFound > 30, $"hail never appears in mature fronts (found {hailFound} cells)");
            Check(belowMajor == 0, "hail must be 0 below MajorFront");
            Check(hailZeroScale == 0, "hail must be 0 at Hail 0");
            Check(maxHailSize > 0.2f && maxHailSize <= 1f, $"hail size range {maxHailSize}");
            bool aspectOk = true; float minAspect = 99f;
            for (int id = 1; id <= 2000; id++) for (int k = 0; k < 5; k++)
            {
                StormArrivalMath.HailSwathShape(id, k, out float wide, out float lng);
                minAspect = Math.Min(minAspect, lng / wide);
                if (lng / wide < 2f - 1e-4f || wide < 1500f - 1 || wide > 4000f + 1 || lng > 15000f + 8001f) aspectOk = false;
            }
            Check(aspectOk, $"hail swath aspect/size limits (min aspect {minAspect})");
            // Hail only in the lifecycle window.
            StormSystemGeometry early = Sys(Birth + 10, Vector2.Zero, Vector2.Zero, stillWind, true, 3, 5);
            Check(StormArrivalMath.HailAt(early, 1f, 1f, 0f, 1f, out _) == 0f, "no hail in birth");
            Check(StormArrivalMath.HailAt(Sys(Birth + Develop + Mature + Dissipate - 5, Vector2.Zero, Vector2.Zero, stillWind, true, 3, 5), 1f, 1f, 0f, 1f, out _) == 0f, "no hail in late dissipate");

            // ---- rain history: continuity of TimeSinceRain and the snapshot reduction ----
            double lastTsr = 0; double worstStep = 0; double worstErr = 0; int tsrPoints = 0; bool monotone = true;
            float start = tuning.PrecipitationStart;
            for (double T = 0; T <= 1450; T += 1)
            {
                Span<float> proxies = stackalloc float[9];
                // A slow, physical fade: the proxy crosses the start threshold at lag T (rain stopped T seconds ago) and falls linearly over ~1000 s.
                for (int l = 0; l < 9; l++)
                {
                    float lag = l == 0 ? 0 : StormArrivalMath.HistoryLagsSeconds[l - 1];
                    proxies[l] = Math.Clamp(start + 0.3f * (lag - (float)T) / 1000f, 0f, 0.6f);
                }
                RainHistorySample h = StormArrivalMath.ReduceHistory(proxies, tuning);
                if (T >= 120 && T <= 1400)
                {
                    if (!float.IsFinite(h.TimeSinceRainSeconds)) { Check(false, $"TimeSinceRain not finite at T={T}"); continue; }
                    tsrPoints++;
                    worstErr = Math.Max(worstErr, Math.Abs(h.TimeSinceRainSeconds - T) / (HalfSpacing(T) + 150.0));
                    if (T > 120) { worstStep = Math.Max(worstStep, Math.Abs(h.TimeSinceRainSeconds - lastTsr)); if (h.TimeSinceRainSeconds < lastTsr - 1e-3) monotone = false; }
                    if (verbose && T % 50 == 0) Console.WriteLine($"  T={T} tsr={h.TimeSinceRainSeconds:0.0} recent={h.RecentRainAmount:0.000}");
                    lastTsr = h.TimeSinceRainSeconds;
                }
            }
            Check(tsrPoints > 1000 && monotone && worstStep < 3.0 && worstErr < 1.0,
                $"TimeSinceRain continuity: points={tsrPoints} monotone={monotone} worstStep={worstStep:0.00}s per 1 s worstErr={worstErr:0.000} (lag spacing bound)");
            if (verbose) Console.WriteLine($"tsr: worstStep={worstStep:0.00} worstErr={worstErr:0.000} start={start}");
            Check(StormArrivalMath.ReduceHistory(new float[] { 0.5f }, tuning).TimeSinceRainSeconds == 0f, "raining now gives TimeSinceRain 0");
            Check(float.IsInfinity(StormArrivalMath.ReduceHistory(new float[9], tuning).TimeSinceRainSeconds), "no rain gives infinite TimeSinceRain");

            // Snapshot reduction equals the direct reduction, including at displaced texels.
            var snapshot = new RainHistorySnapshot { LagCount = 9 };
            Vector2 histObserver = growthObserver;
            double histTime = Birth + Develop + Mature + Dissipate * 0.7;
            for (int l = 0; l < 9; l++)
            {
                float lag = l == 0 ? 0 : StormArrivalMath.HistoryLagsSeconds[l - 1];
                snapshot.LagSeconds[l] = lag;
                snapshot.Counts[l] = 1;
                snapshot.Systems[l * RainHistorySnapshot.MaxSystemsPerLag] = growth(histTime - lag) with { };
            }
            RainHistorySample viaSnapshot = StormArrivalMath.RainHistoryAt(snapshot, Vector2.Zero, tuning);
            RainHistorySample direct = HistoryAt(growth, histTime, tuning);
            Check(Math.Abs(viaSnapshot.RecentRainAmount - direct.RecentRainAmount) < 1e-6f
                && (viaSnapshot.TimeSinceRainSeconds == direct.TimeSinceRainSeconds || Math.Abs(viaSnapshot.TimeSinceRainSeconds - direct.TimeSinceRainSeconds) < 1e-3f),
                $"snapshot reduction mismatch snapshot=({viaSnapshot.TimeSinceRainSeconds},{viaSnapshot.RecentRainAmount}) direct=({direct.TimeSinceRainSeconds},{direct.RecentRainAmount})");
            Vector2 delta = new(4000f, -2500f);
            Func<double, StormSystemGeometry> shifted = x => Sys(x, growthObserver + delta, centre, stillWind);
            RainHistorySample viaDelta = StormArrivalMath.RainHistoryAt(snapshot, delta, tuning);
            RainHistorySample directShift = HistoryAt(shifted, histTime, tuning);
            Check(Math.Abs(viaDelta.RecentRainAmount - directShift.RecentRainAmount) < 1e-5f, "snapshot displaced evaluation mismatch");

            // ---- per-system drift is an input: the same maths at 15 m/s agrees with a finite difference ----
            StormSystemGeometry travelling = Sys(1200, new Vector2(30000f, 2000f), new Vector2(-10000f, 0f), new Vector2(15f, 3f), false);
            Func<double, StormSystemGeometry> travellingAt = x => Sys(x, new Vector2(30000f, 2000f), new Vector2(-10000f, 0f), new Vector2(15f, 3f), false);
            float nA = StormArrivalMath.NormalizedDistance(travellingAt(1199.5), out _, out _), nB = StormArrivalMath.NormalizedDistance(travellingAt(1200.5), out _, out _);
            Check(Math.Abs((nB - nA) - StormArrivalMath.NormalizedDistanceRate(travelling)) < 2e-5f, "per-system drift rate mismatch");
            Check(StormArrivalMath.RimSpeed(travelling) > 5f, "a storm travelling at 15 m/s toward the observer must have rim speed > 5 m/s");

            // ---- B1 wind modifier: identity at strength 0, jump and shift at the gust front ----
            var calmMean = new Vector2(2.2f, 0.7f); var baseWind = new Vector2(2.2f, 0.7f);
            var front = StormArrivalState.Calm with { Valid = true, Severity = 1f, GustFront = 1f, StormBearing = new Vector2(0f, 1f), OutflowDirection = new Vector2(0f, -1f), Phase = StormArrivalPhase.GustFront };
            var off = StormArrivalMath.ModifyWind(front, calmMean, baseWind, 28f, 0f);
            Check(off.Mean == calmMean && off.GustStrengthScale == 1f && off.GustBaseAdd == 0f, "wind modifier at strength 0 must be the identity");
            var windOn = StormArrivalMath.ModifyWind(front, calmMean, baseWind, 28f, 1f);
            float shift = Mathf.RadToDeg(Mathf.Abs(Mathf.AngleDifference(calmMean.Angle(), windOn.Mean.Angle())));
            Check(windOn.Mean.Length() >= 1.4f * calmMean.Length() && windOn.Mean.Length() >= 28f, $"gust front speed jump {calmMean.Length()} -> {windOn.Mean.Length()}");
            Check(shift >= 30f, $"gust front direction shift {shift} deg");
            Check(windOn.GustStrengthScale > 1.5f && windOn.GustBaseAdd > 0.1f, "gust front must raise the gust texture");
            var approach = StormArrivalMath.ModifyWind(StormArrivalState.Calm with { Valid = true, Approach = 1f, StormBearing = new Vector2(0f, 1f) }, new Vector2(8f, 0f), baseWind, 28f, 1f);
            Check(approach.Mean.Length() < 8f * 0.75f + 3.1f && approach.GustStrengthScale < 1f, "approach must calm the wind and weaken the gusts");
            // Sustained outflow: no calm gap after the front. Fat drops and the core hold a high wind (>= 0.85x the front peak), the fat drops still blow out of the storm, trailing eases it.
            var fat = StormArrivalState.Calm with { Valid = true, Severity = 1f, FatDrop = 1f, StormBearing = new Vector2(0f, 1f), OutflowDirection = new Vector2(0f, -1f), Phase = StormArrivalPhase.FatDrop };
            var core = StormArrivalState.Calm with { Valid = true, Severity = 1f, Core = 1f, StormBearing = new Vector2(0f, 1f), OutflowDirection = new Vector2(0f, -1f), Phase = StormArrivalPhase.Core };
            var trail = StormArrivalState.Calm with { Valid = true, Severity = 1f, Trailing = 1f, StormBearing = new Vector2(0f, 1f), OutflowDirection = new Vector2(0f, -1f), Phase = StormArrivalPhase.Trailing };
            var fatWind = StormArrivalMath.ModifyWind(fat, calmMean, baseWind, 28f, 1f);
            var coreWind = StormArrivalMath.ModifyWind(core, calmMean, baseWind, 28f, 1f);
            var trailWind = StormArrivalMath.ModifyWind(trail, calmMean, baseWind, 28f, 1f);
            float peak = windOn.Mean.Length();
            Check(fatWind.Mean.Length() >= 0.85f * peak && coreWind.Mean.Length() >= 0.85f * peak, $"sustained wind fat {fatWind.Mean.Length()} core {coreWind.Mean.Length()} vs front {peak}");
            Check(Mathf.RadToDeg(Mathf.Abs(Mathf.AngleDifference(new Vector2(0f, -1f).Angle(), fatWind.Mean.Angle()))) < 30f, "fat drop wind must blow out of the storm");
            Check(trailWind.Mean.Length() < 0.6f * fatWind.Mean.Length(), $"trailing must ease the wind ({trailWind.Mean.Length()} vs {fatWind.Mean.Length()})");
            Check(fatWind.GustStrengthScale > 1.3f && coreWind.GustStrengthScale > 1.3f, "fat drops and core must stay gusty");
            var gap = StormArrivalState.Calm with { Valid = true, Severity = 1f, Outflow = 0.9f, StormBearing = new Vector2(0f, 1f), OutflowDirection = new Vector2(0f, -1f), Phase = StormArrivalPhase.Clear };
            Check(StormArrivalMath.ModifyWind(gap, calmMean, baseWind, 28f, 1f).Mean.Length() >= 0.8f * peak, "outflow alone must hold the wind in the gap between the front and the fat drops");

            // G1 leading-arc lightning placement: growth strikes ring the rim, drift-only strikes sit on the advancing side, dissipation thins them, all deterministic.
            {
                float c0 = Mathf.Cos(Orientation), s0 = Mathf.Sin(Orientation);
                int grown = 0, blue = 0, tried = 0; double nSum = 0; float nMin = 9f, nMax = 0f; var quadrants = new int[4];
                for (long slot = 0; slot < 600; slot++)
                {
                    tried++;
                    if (!StormArrivalMath.SampleLeadingArc(4242, slot, new Vector2(2.2f, 0.7f), 0.004f, c0, s0, 30000f, 12000f, out Vector2 lOff, out float n)) continue;
                    grown++; nSum += n; nMin = Math.Min(nMin, n); nMax = Math.Max(nMax, n); if (n > 1f) blue++;
                    float along = c0 * lOff.X + s0 * lOff.Y, cross = -s0 * lOff.X + c0 * lOff.Y;
                    quadrants[(along >= 0 ? 0 : 1) + (cross >= 0 ? 0 : 2)]++;
                    Check(Math.Abs(NDouble(new StormSystemGeometry(1, 3, lOff, 30000f, 12000f, c0, s0, 1f, 0f, Vector2.Zero, 1f, 1f, 1f)) - n) < 1e-3f, "leading arc point must lie at its reported n");
                }
                float meanN = (float)(nSum / Math.Max(1, grown));
                if (verbose) Console.WriteLine($"leadingarc growth: accepted={grown}/{tried} meanN={meanN:0.000} range=[{nMin:0.00},{nMax:0.00}] blue={blue} quadrants=[{string.Join(",", quadrants)}]");
                Check(grown > 0.9f * tried, $"growing storm should place nearly every strike (accepted {grown}/{tried})");
                Check(nMin >= 0.55f - 1e-4f && nMax <= 1.08f + 1e-4f, $"leading arc n range [{nMin},{nMax}]");
                Check(meanN > 0.78f, $"leading arc strikes must crowd the rim (mean n {meanN}; the uniform natural stream has 0.4)");
                Check(blue > 0.05f * grown && blue < 0.16f * grown, $"about 10% bolts from the blue (got {blue}/{grown})");
                Check(quadrants.All(q => q > grown / 10), $"growth strikes must surround the storm ({string.Join(",", quadrants)})");

                var drift = new Vector2(15f, 0f); int driftHits = 0; bool allAhead = true; double dotSum = 0;
                for (long slot = 0; slot < 600; slot++)
                {
                    if (!StormArrivalMath.SampleLeadingArc(777, slot, drift, 0f, c0, s0, 30000f, 12000f, out Vector2 lOff, out float n)) continue;
                    driftHits++; dotSum += lOff.Normalized().Dot(drift.Normalized());
                    allAhead &= StormArrivalMath.RimLeadingWeight(drift, 0f, lOff / n, c0, s0, 30000f, 12000f) > 0f;
                }
                if (verbose) Console.WriteLine($"leadingarc drift: accepted={driftHits} meanCosToDrift={dotSum / Math.Max(1, driftHits):0.00}");
                Check(driftHits > 100 && allAhead && dotSum / Math.Max(1, driftHits) > 0.3, "drift-only strikes must sit on the advancing rim");
                int dying = 0;
                for (long slot = 0; slot < 600; slot++) if (StormArrivalMath.SampleLeadingArc(777, slot, new Vector2(2.2f, 0.7f), -0.005f, c0, s0, 30000f, 12000f, out _, out _)) dying++;
                Check(dying < grown / 3, $"a dissipating storm has no advancing rim (accepted {dying})");
                bool same = StormArrivalMath.SampleLeadingArc(4242, 17, new Vector2(2.2f, 0.7f), 0.004f, c0, s0, 30000f, 12000f, out Vector2 o1, out float n1)
                    & StormArrivalMath.SampleLeadingArc(4242, 17, new Vector2(2.2f, 0.7f), 0.004f, c0, s0, 30000f, 12000f, out Vector2 o2, out float n2);
                Check(same && o1 == o2 && n1 == n2, "leading arc placement must be deterministic");
            }

            // Wind profile across the whole arrival sequence of a growing storm (calm base wind in): no gust gap from the first gust front to the end of the core.
            {
                double tStart = 120, tEnd = Birth + Develop + Mature;
                double tFront = double.NaN, tCoreEnd = double.NaN; float windPeak = 0f, minHeld = float.MaxValue, worstDrop = 0f, prevSpeed = -1f;
                double tMinHeld = 0, tFatStart = double.NaN; bool inFatOrCore = false;
                for (double t = tStart; t <= tEnd; t += 1.0)
                {
                    StormArrivalState st = StateAt(growth, t, tuning);
                    float speed = StormArrivalMath.ModifyWind(st, calmMean, baseWind, 28f, 1f).Mean.Length();
                    if (double.IsNaN(tFront) && st.GustFront > 0.9f) tFront = t;
                    if (!double.IsNaN(tFront)) windPeak = Math.Max(windPeak, speed);
                    if (!double.IsNaN(tFront) && st.Trailing < 0.2f && st.Core > 0.2f) tCoreEnd = t;
                    if (st.FatDrop > 0.05f && double.IsNaN(tFatStart)) tFatStart = t;
                    // From the first full gust front to the last core second, the wind may not fall below 0.6 of the peak, nor drop faster than 4 m/s per second.
                    if (!double.IsNaN(tFront) && (double.IsNaN(tCoreEnd) || t <= tCoreEnd) && st.Trailing < 0.2f)
                    {
                        if (speed < minHeld) { minHeld = speed; tMinHeld = t; }
                        if (prevSpeed >= 0f) worstDrop = Math.Max(worstDrop, prevSpeed - speed);
                        inFatOrCore |= st.FatDrop > 0.3f || st.Core > 0.3f;
                    }
                    prevSpeed = speed;
                    if (verbose && ((int)t % 15) == 0) Console.WriteLine($"wp t={t:0} ph={st.Phase} A={st.Approach:0.00} G={st.GustFront:0.00} O={st.Outflow:0.00} F={st.FatDrop:0.00} C={st.Core:0.00} T={st.Trailing:0.00} v={speed:0.0} rim={st.RimSpeedMetersPerSecond:0.0}");
                }
                if (verbose) Console.WriteLine($"windprofile: front@{tFront:0} fat@{tFatStart:0} coreEnd@{tCoreEnd:0} peak={windPeak:0.0} minHeld={minHeld:0.0}@{tMinHeld:0} worstDrop/s={worstDrop:0.00}");
                Check(!double.IsNaN(tFront) && !double.IsNaN(tCoreEnd) && inFatOrCore, "wind profile: the sequence must contain a gust front followed by fat drops or a core");
                Check(minHeld >= 0.6f * windPeak, $"wind profile: gust gap, min {minHeld} at t={tMinHeld} vs peak {windPeak}");
                Check(worstDrop <= 4.0f, $"wind profile: wind drops {worstDrop} m/s in one second");
            }
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("STORM_ARRIVAL_MATH=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("STORM_ARRIVAL_MATH=PASS");
        return 0;
    }
}
