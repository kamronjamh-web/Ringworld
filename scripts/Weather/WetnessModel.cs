using System;

namespace RingworldTechDemo.Weather;

/// <summary>Everything the closed-form wetness needs at one location (docs/storm-revamp-s2-wet-world-design.md 2.1).</summary>
public readonly record struct WetnessInputs(
    float RecentRain,            // S3 RecentRainAmount 0..1
    float TimeSinceRainSeconds,  // S3 TimeSinceRainSeconds (continuous, +inf = none inside the horizon)
    float RainNow,               // instantaneous local precipitation 0..1 (storm map B through the streak threshold)
    float Sun,                   // clear-sky fraction 0..1 (already times the day factor)
    float WindMetersPerSecond,
    float DryingSpeed,           // menu: scales k
    bool Snowing,
    float PriorRain = -1.0f);    // how heavy the rain was before it stopped (WetnessModel.PriorRain); negative = unknown, Recent is used

/// <summary>Film (gloss), soak (darkening), standing water (puddle level) and the "raining here now" gate, all 0..1.</summary>
public readonly record struct WetnessState(float Film, float Soak, float Standing, float RainNowTerm)
{
    public static WetnessState Dry => new(0.0f, 0.0f, 0.0f, 0.0f);
}

/// <summary>
/// Pure, stateless wetness model: the same closed form the GPU map evaluates per texel (scripts/render/ring_wet_map.glsl), so each texel is recomputed from S3's
/// rain history and there is no rain integrator, no persistence and no rewind handling. Film (gloss) dries 3x faster than soak (darkening), puddles last longest.
/// Two deviations from the design text. (1) Soak, film and standing also fade out over the last quarter of the history horizon, because at overcast calm
/// (k = 0.3) the 600 s puddle tail is still 0.47 at 1500 s and the history would otherwise end in a visible pop. (2) S3's RecentRainAmount weights recent lags and
/// so collapses about two minutes after the rain stops (a 15 minute storm reads 0.3 at +120 s), which would end puddles and the soak long before their drying tails.
/// The amount driving them is therefore max(Recent, PriorRain), where <see cref="PriorRain"/> is the mean wetness of the lags older than the time since rain
/// (how heavy the rain was before it stopped); standing water uses PriorRain alone, so puddles need sustained rain (they form only after minutes) and then last.
/// </summary>
public static class WetnessModel
{
    public const float FilmTauSeconds = 90.0f;
    public const float SoakTauSeconds = 300.0f;
    public const float StandingTauSeconds = 600.0f;
    public const float HorizonFadeStartSeconds = 1125.0f;
    public const float HorizonSeconds = 1500.0f;

    public static float SmoothStep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    /// <summary>Drying rate k = DryingSpeed x (0.3 + 0.7 sun + 0.05 wind m/s).</summary>
    public static float DryingRate(float sun, float windMetersPerSecond, float dryingSpeed)
        => Math.Max(0.0f, dryingSpeed) * (0.3f + 0.7f * Math.Clamp(sun, 0.0f, 1.0f) + 0.05f * Math.Max(0.0f, windMetersPerSecond));

    public static WetnessState Evaluate(in WetnessInputs input)
    {
        if (input.Snowing) return WetnessState.Dry;          // v1: snow gives no gain, and the history counts as dry
        float recent = Math.Clamp(float.IsFinite(input.RecentRain) ? input.RecentRain : 0.0f, 0.0f, 1.0f);
        float rainNow = Math.Clamp(float.IsFinite(input.RainNow) ? input.RainNow : 0.0f, 0.0f, 1.0f);
        float tsr = float.IsNaN(input.TimeSinceRainSeconds) ? float.PositiveInfinity : Math.Max(0.0f, input.TimeSinceRainSeconds);
        float prior = input.PriorRain < 0.0f ? recent : Math.Clamp(float.IsFinite(input.PriorRain) ? input.PriorRain : 0.0f, 0.0f, 1.0f);
        float amount = Math.Max(recent, prior);
        float k = DryingRate(input.Sun, input.WindMetersPerSecond, input.DryingSpeed);
        float rainNowTerm = SmoothStep(0.0f, 0.3f, rainNow) * SmoothStep(0.0f, 0.05f, recent + rainNow);
        float horizon = 1.0f - SmoothStep(HorizonFadeStartSeconds, HorizonSeconds, tsr);
        float film = Math.Max(rainNowTerm, SmoothStep(0.02f, 0.15f, amount) * Decay(k, tsr, FilmTauSeconds) * horizon);
        float soak = Math.Max(0.8f * rainNowTerm, SmoothStep(0.05f, 0.4f, amount) * Decay(k, tsr, SoakTauSeconds) * horizon);
        float standing = SmoothStep(0.35f, 0.8f, prior) * Decay(k, tsr, StandingTauSeconds) * horizon;
        return new WetnessState(film, soak, standing, rainNowTerm);
    }

    private static float Decay(float k, float tsr, float tau) => float.IsPositiveInfinity(tsr) ? 0.0f : MathF.Exp(-k * tsr / tau);

    /// <summary>
    /// Mean wetness (StormArrivalMath.Wetness of the lag proxy) over the history lags older than the time since rain, each weighted by smoothstep-free ramp
    /// clamp((lag - timeSince) / (0.25 lag + 15), 0, 1) so it is continuous in the time since rain; faded in over a total weight of 1.5. Zero when nothing is that old.
    /// <paramref name="proxyByLag"/> is indexed like S3's history (0 = now, then the eight lags).
    /// </summary>
    public static float PriorRain(ReadOnlySpan<float> proxyByLag, float timeSinceRainSeconds, in StormArrivalTuning tuning)
    {
        if (float.IsNaN(timeSinceRainSeconds) || float.IsInfinity(timeSinceRainSeconds)) return 0.0f;
        float sum = 0.0f, weight = 0.0f;
        int count = Math.Min(proxyByLag.Length, StormArrivalMath.HistoryLagsSeconds.Length + 1);
        for (int i = 1; i < count; i++)
        {
            float lag = StormArrivalMath.HistoryLagsSeconds[i - 1];
            float s = Math.Clamp((lag - timeSinceRainSeconds) / (0.25f * lag + 15.0f), 0.0f, 1.0f);
            sum += s * StormArrivalMath.Wetness(proxyByLag[i], tuning);
            weight += s;
        }
        return weight > 0.0f ? sum / weight * Math.Min(1.0f, weight / 1.5f) : 0.0f;
    }

    /// <summary>Per-lag rain proxies at a point displaced by deltaMeters from the snapshot observer (the input of <see cref="PriorRain"/>).</summary>
    public static float[] LagProxies(RainHistorySnapshot snapshot, Godot.Vector2 deltaMeters, in StormArrivalTuning tuning)
    {
        int lags = Math.Min(snapshot.LagCount, StormArrivalMath.HistoryLagsSeconds.Length + 1);
        var proxies = new float[lags];
        for (int l = 0; l < lags; l++)
        {
            float best = 0.0f;
            for (int i = 0; i < snapshot.Counts[l]; i++)
            {
                StormSystemGeometry g = snapshot.Systems[l * RainHistorySnapshot.MaxSystemsPerLag + i];
                best = Math.Max(best, StormArrivalMath.RainProxyAtObserver(g with { OffsetMeters = g.OffsetMeters + deltaMeters }, tuning));
            }
            proxies[l] = best;
        }
        return proxies;
    }

    /// <summary>The same reduction S3 does for the observer, fed from a snapshot at a displaced point (CPU reference of the GLSL history reduction).</summary>
    public static WetnessState EvaluateAt(RainHistorySnapshot snapshot, Godot.Vector2 deltaMeters, in StormArrivalTuning tuning, float rainNow, float sun, float windMetersPerSecond, float dryingSpeed, bool snowing)
    {
        RainHistorySample history = StormArrivalMath.RainHistoryAt(snapshot, deltaMeters, tuning);
        float prior = PriorRain(LagProxies(snapshot, deltaMeters, tuning), history.TimeSinceRainSeconds, tuning);
        return Evaluate(new WetnessInputs(history.RecentRainAmount, history.TimeSinceRainSeconds, rainNow, sun, windMetersPerSecond, dryingSpeed, snowing, prior));
    }
}
