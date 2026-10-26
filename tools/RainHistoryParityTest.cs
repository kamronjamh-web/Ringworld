using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Headless half of the rain-history parity (docs/storm-revamp-s2-wet-world-design.md 6): a straight C# transcription of scripts/render/ring_rain_history.glslinc
/// (rw_* functions, same operation order) equals StormArrivalMath.RainHistoryAt at 64 points over 9 lags of randomised systems. The GPU half is wet_world_gpu_test.
/// Run: Godot --headless --path . --script res://tools/rain_history_parity_test.gd. Prints RAIN_HISTORY_PARITY=PASS.
/// </summary>
public partial class RainHistoryParityTest : RefCounted
{
    static float Sm(float e0, float e1, float x) { float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f); return t * t * (3f - 2f * t); }
    static float SmUnit(float v) { float t = Math.Clamp(v, 0f, 1f); return t * t * (3f - 2f * t); }
    static float RwProxy(float n, float density, float potential, float baseCoverage)
    {
        if (n >= 1f) return 0f;
        float ci = (1f - SmUnit((n - 0.32f) / 0.68f)) * density;
        float cov = Math.Clamp(baseCoverage + (ci - 0.28f) * 0.46f + ci * (density - 0.5f) * 0.24f, 0.015f, 0.985f);
        return Math.Clamp(potential * ci * SmUnit((cov - 0.35f) / 0.55f), 0f, 1f);
    }
    static float RwN(Vector2 offset, float cos, float sin, float major, float minor)
    {
        float along = cos * offset.X + sin * offset.Y, cross = -sin * offset.X + cos * offset.Y;
        float a = Math.Max(1f, major), b = Math.Max(1f, minor);
        float n = MathF.Sqrt(along * along / (a * a) + cross * cross / (b * b));
        if (n < 1e-5f || offset.Length() < 1e-3f) return 0f;
        return n;
    }

    /// <summary>Transcription of the GLSL: returns (recent, timeSince) with 1e9 for "no rain".</summary>
    static (float Recent, float TimeSince, float Prior) GlslHistory(RainHistorySnapshot snap, Vector2 delta, float start, float baseCoverage)
    {
        var proxies = new float[9];
        for (int l = 0; l < 9; l++)
        {
            float best = 0f;
            for (int s = 0; s < snap.Counts[l]; s++)
            {
                StormSystemGeometry g = snap.Systems[l * RainHistorySnapshot.MaxSystemsPerLag + s];
                float n = RwN(g.OffsetMeters + delta, g.OrientationCos, g.OrientationSin, g.MajorRadiusMeters, g.MinorRadiusMeters);
                best = Math.Max(best, RwProxy(n, g.Density, g.PrecipitationPotential, baseCoverage));
            }
            proxies[l] = best;
        }
        float sum = 0f, weightSum = 0f, running = 1f, previousC = 1f, previousLag = 0f, area = 0f;
        for (int i = 0; i < 9; i++)
        {
            float lag = i == 0 ? 0f : snap.LagSeconds[i];
            float w = MathF.Exp(-lag / 600f);
            sum += Sm(start, start + 0.25f, proxies[i]) * w; weightSum += w;
            running = Math.Min(running, 1f - Sm(0.5f * start, start, proxies[i]));
            if (i > 0) area += 0.5f * (previousC + running) * (lag - previousLag);
            previousC = running; previousLag = lag;
        }
        float timeSince = running > 0.98f ? 1e9f : area;
        float priorSum = 0f, priorWeight = 0f;
        if (timeSince < 1e9f)
            for (int i = 1; i < 9; i++)
            {
                float lag = snap.LagSeconds[i];
                float s = Math.Clamp((lag - timeSince) / (0.25f * lag + 15f), 0f, 1f);
                priorSum += s * Sm(start, start + 0.25f, proxies[i]);
                priorWeight += s;
            }
        float prior = priorWeight > 0f ? priorSum / priorWeight * Math.Min(1f, priorWeight / 1.5f) : 0f;
        return (Math.Clamp(weightSum > 0f ? sum / weightSum : 0f, 0f, 1f), timeSince, prior);
    }

    public int Run()
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            var rng = new RandomNumberGenerator { Seed = 20261025 };
            var tuning = new StormArrivalTuning(60f, 1f, 0.18f, 0.46f);
            var snapshot = new RainHistorySnapshot { LagCount = 9 };
            for (int l = 0; l < 9; l++)
            {
                snapshot.LagSeconds[l] = l == 0 ? 0f : StormArrivalMath.HistoryLagsSeconds[l - 1];
                int count = 1 + (int)(rng.Randf() * 6f);
                snapshot.Counts[l] = count;
                for (int i = 0; i < count; i++)
                {
                    float angle = rng.Randf() * Mathf.Tau;
                    float major = rng.RandfRange(8000f, 40000f), minor = rng.RandfRange(6000f, major);
                    snapshot.Systems[l * RainHistorySnapshot.MaxSystemsPerLag + i] = new StormSystemGeometry(i + 1, 3,
                        new Vector2(rng.RandfRange(-30000f, 30000f), rng.RandfRange(-30000f, 30000f)), major, minor, MathF.Cos(angle), MathF.Sin(angle),
                        1f, 0f, Vector2.Zero, rng.RandfRange(0.4f, 1f), rng.RandfRange(0.3f, 1f), 1f);
                }
            }
            int wet = 0, partial = 0;
            for (int p = 0; p < 64; p++)
            {
                var delta = new Vector2(rng.RandfRange(-8200f, 8200f), rng.RandfRange(-8200f, 8200f));
                RainHistorySample reference = StormArrivalMath.RainHistoryAt(snapshot, delta, tuning);
                (float recent, float tsr, float prior) = GlslHistory(snapshot, delta, tuning.PrecipitationStart, tuning.BaseCoverage);
                float referencePrior = WetnessModel.PriorRain(WetnessModel.LagProxies(snapshot, delta, tuning), reference.TimeSinceRainSeconds, tuning);
                Check(Math.Abs(prior - referencePrior) < 1e-4f, $"point {p}: prior {prior} vs {referencePrior}");
                Check(Math.Abs(recent - reference.RecentRainAmount) < 1e-4f, $"point {p}: recent {recent} vs {reference.RecentRainAmount}");
                float refTsr = float.IsInfinity(reference.TimeSinceRainSeconds) ? 1e9f : reference.TimeSinceRainSeconds;
                Check(Math.Abs(tsr - refTsr) < 1e-2f * Math.Max(1f, refTsr / 1000f) || (tsr >= 1e8f && refTsr >= 1e8f), $"point {p}: time since {tsr} vs {refTsr}");
                // The WetnessModel on both agree (the GLSL model is a transcription too: same closed form on the same history).
                WetnessState a = WetnessModel.Evaluate(new WetnessInputs(recent, tsr, 0f, 0.3f, 2f, 1f, false, prior));
                WetnessState b = WetnessModel.Evaluate(new WetnessInputs(reference.RecentRainAmount, reference.TimeSinceRainSeconds, 0f, 0.3f, 2f, 1f, false, referencePrior));
                Check(Math.Abs(a.Film - b.Film) < 1e-3f && Math.Abs(a.Soak - b.Soak) < 1e-3f && Math.Abs(a.Standing - b.Standing) < 1e-3f, $"point {p}: wetness mismatch");
                if (reference.RecentRainAmount > 0.01f) wet++;
                if (reference.RecentRainAmount > 0.01f && reference.RecentRainAmount < 0.9f && !float.IsInfinity(reference.TimeSinceRainSeconds)) partial++;
            }
            Check(wet > 8, $"fixture too dry ({wet} wet points)");
            Check(partial > 3, $"fixture lacks partial histories ({partial})");
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("RAIN_HISTORY_PARITY=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("RAIN_HISTORY_PARITY=PASS");
        return 0;
    }
}
