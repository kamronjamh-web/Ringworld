using System;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Weather;

/// <summary>Storm arrival phase (docs/storm-revamp-s3-storm-arrival-design.md section 2.2). Ties resolve Core > GustFront > FatDrop > Trailing > Approach > Clearing.</summary>
public enum StormArrivalPhase { Clear = 0, Approach = 1, GustFront = 2, FatDrop = 3, Core = 4, Trailing = 5, Clearing = 6 }

/// <summary>
/// One weather system as the arrival maths sees it, all in the ring frame (x = around, y = across) and already advected to the query time.
/// Radii are the ENVELOPED radii; <see cref="DriftMetersPerSecond"/> is this system's own centre velocity (zero for manual/static systems), so
/// per-kind storm travel needs no change here. <see cref="EnvelopeRatePerSecond"/> is rho = E'/E.
/// </summary>
public readonly record struct StormSystemGeometry(
    int SystemId,
    int KindCode,                       // 1 Cumulus, 2 CloudBank, 3 MajorFront, 4 Exceptional, >=5 Superstorm
    Vector2 OffsetMeters,               // observer minus system centre
    float MajorRadiusMeters,
    float MinorRadiusMeters,
    float OrientationCos,
    float OrientationSin,
    float Envelope,
    float EnvelopeRatePerSecond,
    Vector2 DriftMetersPerSecond,
    float Density,                      // already enveloped (as in WeatherSystemDefinition)
    float PrecipitationPotential,       // already enveloped
    float LifecycleCode);               // 0..3, see RingVolumetricCloudLayer.StormLifecycleCode

/// <summary>Per-call tuning. Only Gust front lead, Hail and the coverage baseline matter to the maths; the rest are constants.</summary>
public readonly record struct StormArrivalTuning(
    float GustFrontLeadSeconds = 60.0f,
    float Hail = 1.0f,
    float PrecipitationStart = 0.18f,
    float BaseCoverage = 0.46f)
{
    public static StormArrivalTuning Default => new(60.0f, 1.0f, 0.18f, 0.46f);
}

/// <summary>Deterministic rain history at a point: how long since it last rained and how much recently (shared aftermath clock for mist, rainbow, S2 drips, S5 drip one-shots).</summary>
public readonly record struct RainHistorySample(float TimeSinceRainSeconds, float RecentRainAmount, bool Valid)
{
    /// <summary>No rain within the history horizon.</summary>
    public static RainHistorySample None => new(float.PositiveInfinity, 0.0f, false);
}

/// <summary>
/// Per-lag system geometry the rain history was reduced from (S2 evaluates it per texel). Lag index 0 is "now", then
/// <see cref="StormArrivalMath.HistoryLagsSeconds"/>. Each entry's OffsetMeters is observer minus centre at the snapshot observer; a point
/// displaced by delta uses OffsetMeters + delta. Entries carry drift, lifecycle code and id, so hail can be evaluated from lag 0 too.
/// </summary>
public sealed class RainHistorySnapshot
{
    public const int MaxSystemsPerLag = 32;
    public const int MaxLags = 9;
    public double Time;
    public int LagCount;
    public readonly float[] LagSeconds = new float[MaxLags];
    public readonly int[] Counts = new int[MaxLags];
    public readonly StormSystemGeometry[] Systems = new StormSystemGeometry[MaxLags * MaxSystemsPerLag];
}

public readonly record struct StormArrivalState(
    int SystemId, int Kind, float Severity, StormArrivalPhase Phase,
    float Approach, float GustFront, float FatDrop, float Core, float Trailing, float Clearing,
    float RimDistanceMeters, float RimSpeedMetersPerSecond, float TimeToRainSeconds,
    Vector2 StormBearing, Vector2 OutflowDirection,
    float HailIntensity, float HailSize,
    float CoolingOffset, float TimeSinceRainSeconds, float RecentRainAmount, bool Valid, float Outflow = 0.0f)
{
    public static StormArrivalState Calm => new(0, 0, 0.0f, StormArrivalPhase.Clear, 0, 0, 0, 0, 0, 0,
        float.PositiveInfinity, 0.0f, float.PositiveInfinity, Vector2.Zero, Vector2.Zero, 0, 0, 0.0f, float.PositiveInfinity, 0.0f, false);
}

/// <summary>Pure storm-arrival maths: rim kinematics, bands, hail and the rain history reduction. No node, no world access.</summary>
public static class StormArrivalMath
{
    public const float ApproachReachMeters = 18000.0f;
    public const float MinBandMeters = 400.0f;
    public const float MaxBandMeters = 6000.0f;
    public const float DryRainRimN = 0.85f;
    /// <summary>RecentRain gate of Clearing and the lingering cooling. RecentRain is a mean over nine lags, so a rain that ended 10-20 minutes ago leaves only about 0.01-0.03; the gate is low enough for the aftermath to last its design time constants (1200 s and 600 s).</summary>
    public const float AftermathGateLow = 0.008f, AftermathGateHigh = 0.05f;
    public const float PhaseThreshold = 0.05f;
    /// <summary>Lags (s) of the rain history; lag 0 (now) is always evaluated as well.</summary>
    public static readonly float[] HistoryLagsSeconds = { 15.0f, 30.0f, 60.0f, 120.0f, 240.0f, 480.0f, 960.0f, 1500.0f };
    public const float HistoryTauSeconds = 600.0f;
    public const float HistoryHorizonSeconds = 1500.0f;

    // ---- shared providers (pattern of PrecipitationAltitude.ColumnProvider) ----
    /// <summary>Pure (position, time) arrival query installed by the weather layer; null before a world is running.</summary>
    public static Func<RingCanonicalPosition, double, StormArrivalState>? Provider { get; set; }
    /// <summary>Cached arrival state at the observer (explorer), computed once per process frame; null before a world is running.</summary>
    public static Func<StormArrivalState>? ObserverProvider { get; set; }
    /// <summary>Pure rain history at (position, time). S2 and S5 read this; null before a world is running.</summary>
    public static Func<RingCanonicalPosition, double, RainHistorySample>? RainHistoryProvider { get; set; }

    public static StormArrivalState Query(RingCanonicalPosition position, double time)
        => Provider?.Invoke(position, time) ?? StormArrivalState.Calm;
    public static StormArrivalState Observer => ObserverProvider?.Invoke() ?? StormArrivalState.Calm;
    public static RainHistorySample QueryRainHistory(RingCanonicalPosition position, double time)
        => RainHistoryProvider?.Invoke(position, time) ?? RainHistorySample.None;

    // ---- small helpers ----
    public static float SmoothStep(float e0, float e1, float x)
    {
        float t = Mathf.Clamp((x - e0) / (e1 - e0), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }
    static float SmoothUnit(float v) { float t = Mathf.Clamp(v, 0.0f, 1.0f); return t * t * (3.0f - 2.0f * t); }

    public static float KindWeight(int kind) => kind switch { <= 0 => 0.0f, 1 => 0.15f, 2 => 0.45f, 3 => 1.0f, 4 => 1.25f, _ => 1.5f };
    static float GustKindScale(int kind) => kind switch { <= 0 => 0.0f, 1 => 0.15f, 2 => 0.4f, _ => 1.0f };

    public static float Severity(in StormSystemGeometry g)
        => Mathf.Clamp(g.PrecipitationPotential * g.Density * KindWeight(g.KindCode), 0.0f, 1.5f);

    static float Hash01(int a, int b, int salt)
    {
        unchecked
        {
            uint h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u ^ (uint)salt * 0xC2B2AE3Du;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return (h & 0xFFFFFF) / 16777216.0f;
        }
    }

    // ---- rim kinematics ----
    /// <summary>Normalised elliptical distance n (EvaluateMacroWeather's source-frame maths) and the radial ellipse radius r/n along the ray to the observer.</summary>
    public static float NormalizedDistance(in StormSystemGeometry g, out float rayRadiusMeters, out float distanceMeters)
    {
        float oa = g.OffsetMeters.X, oz = g.OffsetMeters.Y;
        float along = g.OrientationCos * oa + g.OrientationSin * oz;
        float cross = -g.OrientationSin * oa + g.OrientationCos * oz;
        float a = Mathf.Max(1.0f, g.MajorRadiusMeters), b = Mathf.Max(1.0f, g.MinorRadiusMeters);
        float n = Mathf.Sqrt(along * along / (a * a) + cross * cross / (b * b));
        distanceMeters = g.OffsetMeters.Length();
        if (n < 1e-5f || distanceMeters < 1e-3f) { rayRadiusMeters = a; return 0.0f; }
        rayRadiusMeters = distanceMeters / n;
        return n;
    }

    /// <summary>Analytic dn/dt (1/s): -(Q d . w)/(E^2 n) - n rho, with Q = M^T M the unscaled ellipse normaliser. Returns 0 at the centre.</summary>
    public static float NormalizedDistanceRate(in StormSystemGeometry g)
    {
        float n = NormalizedDistance(g, out _, out _);
        if (n < 1e-5f) return 0.0f;
        float e = Mathf.Max(1e-4f, g.Envelope);
        float a0 = Mathf.Max(1.0f, g.MajorRadiusMeters) / e, b0 = Mathf.Max(1.0f, g.MinorRadiusMeters) / e;
        float oa = g.OffsetMeters.X, oz = g.OffsetMeters.Y;
        float along = g.OrientationCos * oa + g.OrientationSin * oz;
        float cross = -g.OrientationSin * oa + g.OrientationCos * oz;
        float wAlong = g.OrientationCos * g.DriftMetersPerSecond.X + g.OrientationSin * g.DriftMetersPerSecond.Y;
        float wCross = -g.OrientationSin * g.DriftMetersPerSecond.X + g.OrientationCos * g.DriftMetersPerSecond.Y;
        float dQw = along * wAlong / (a0 * a0) + cross * wCross / (b0 * b0);
        return -dQw / (e * e * n) - n * g.EnvelopeRatePerSecond;
    }

    /// <summary>
    /// Speed (m/s, positive = the rim advances over the observer) at which the level set n = rimN passes the observer along the radial ray:
    /// R_ray (Qd.w / (E^2 n) + rimN rho + d(rimN)/dt). Evaluated at the rim, not at the observer, so a distant observer sees the rim speed
    /// rather than a speed proportional to its own distance. rimN = 1 gives the cloud edge; pass the rain rim and its rate for the rain bands.
    /// </summary>
    public static float RimSpeed(in StormSystemGeometry g, float rimN = 1.0f, float rimRatePerSecond = 0.0f)
    {
        float n = NormalizedDistance(g, out float ray, out _);
        float e = Mathf.Max(1e-4f, g.Envelope);
        float drift = 0.0f;
        if (n > 1e-5f)
        {
            float a0 = Mathf.Max(1.0f, g.MajorRadiusMeters) / e, b0 = Mathf.Max(1.0f, g.MinorRadiusMeters) / e;
            float oa = g.OffsetMeters.X, oz = g.OffsetMeters.Y;
            float along = g.OrientationCos * oa + g.OrientationSin * oz;
            float cross = -g.OrientationSin * oa + g.OrientationCos * oz;
            float wAlong = g.OrientationCos * g.DriftMetersPerSecond.X + g.OrientationSin * g.DriftMetersPerSecond.Y;
            float wCross = -g.OrientationSin * g.DriftMetersPerSecond.X + g.OrientationCos * g.DriftMetersPerSecond.Y;
            drift = (along * wAlong / (a0 * a0) + cross * wCross / (b0 * b0)) / (e * e * n);
        }
        return ray * (drift + rimN * g.EnvelopeRatePerSecond + rimRatePerSecond);
    }

    /// <summary>The influence falloff of EvaluateMacroWeather.</summary>
    public static float Influence(float n) => 1.0f - SmoothUnit((n - 0.32f) / 0.68f);

    /// <summary>The ResolveLocalCloudCoverage formula with the weather noise fixed at its mean 0.5 (the clustering term vanishes).</summary>
    public static float MeanCoverage(float combinedInfluence, float strongestInfluence, float density, float baseCoverage)
        => Mathf.Clamp(baseCoverage + (combinedInfluence - 0.28f) * 0.46f + strongestInfluence * (density - 0.5f) * 0.24f, 0.015f, 0.985f);

    /// <summary>Mean-field precipitation of one system at normalised distance n (QueryRawWeatherAtTime with mean noise); this is the "rain proxy".</summary>
    public static float RainProxy(float n, float density, float potential, float baseCoverage)
    {
        if (n >= 1.0f) return 0.0f;
        float ci = Influence(n) * density;
        float cov = MeanCoverage(ci, ci, density, baseCoverage);
        return Mathf.Clamp(potential * ci * SmoothUnit((cov - 0.35f) / 0.55f), 0.0f, 1.0f);
    }

    /// <summary>Rain rim n_rain: the largest n where the mean-field precipitation reaches the start threshold (16-step bisection); 0 = dry system.</summary>
    public static float SolveRainRim(float density, float potential, float start, float baseCoverage)
    {
        if (RainProxy(0.0f, density, potential, baseCoverage) < start) return 0.0f;
        float lo = 0.0f, hi = 1.0f;
        for (int i = 0; i < 16; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (RainProxy(mid, density, potential, baseCoverage) >= start) lo = mid; else hi = mid;
        }
        return Mathf.Max(0.02f, lo);
    }

    /// <summary>
    /// The two rims of a system. The RAIN rim is the solved precipitation edge (0.02 minimum), with <paramref name="presence"/> the 0..1 share of rain
    /// weights (0 for a dry system, full once the peak proxy is twice the start threshold). The GUST rim is where the outflow leads the rain: it equals the
    /// dry rim (DryRainRimN) for a system that never rains and relaxes onto the rain rim as the peak proxy grows from 1x to 4x the threshold, so rain onset
    /// never makes the gust bands jump.
    /// </summary>
    public static void ResolveRims(float density, float potential, in StormArrivalTuning tuning, out float gustRim, out float rainRim, out float presence)
    {
        float peak = RainProxy(0.0f, density, potential, tuning.BaseCoverage);
        float ratio = peak / Mathf.Max(1e-4f, tuning.PrecipitationStart);
        presence = SmoothStep(1.0f, 2.0f, ratio);
        if (ratio <= 1.0f) { gustRim = DryRainRimN; rainRim = 0.0f; return; }
        rainRim = Mathf.Max(0.02f, SolveRainRim(density, potential, tuning.PrecipitationStart, tuning.BaseCoverage));
        gustRim = Mathf.Lerp(DryRainRimN, rainRim, SmoothStep(1.0f, 3.0f, ratio));
    }

    /// <summary>Mean-field rain proxy at the observer for one system (0 when unrelated).</summary>
    public static float RainProxyAtObserver(in StormSystemGeometry g, in StormArrivalTuning t)
    {
        float n = NormalizedDistance(g, out _, out _);
        return RainProxy(n, g.Density, g.PrecipitationPotential, t.BaseCoverage);
    }

    /// <summary>The shared leading-edge formula (also the H1 shelf bake and the G1 lightning rejection sampler): smoothstep(0, 3 m/s, outward rim normal speed).</summary>
    public static float RimLeadingWeight(float rimNormalSpeed) => SmoothStep(0.0f, 3.0f, rimNormalSpeed);

    /// <summary>Outward rim normal speed at the rim point p (offset from the centre), dot(w, n_hat) + rho dot(p, n_hat), with n_hat the ellipse normal at p.</summary>
    public static float RimNormalSpeed(Vector2 drift, float rho, Vector2 pointFromCentre, float cosO, float sinO, float majorRadius, float minorRadius)
    {
        float along = cosO * pointFromCentre.X + sinO * pointFromCentre.Y;
        float cross = -sinO * pointFromCentre.X + cosO * pointFromCentre.Y;
        float a = Mathf.Max(1.0f, majorRadius), b = Mathf.Max(1.0f, minorRadius);
        float gx = along / (a * a), gz = cross / (b * b);       // gradient of n^2/2 in the ellipse frame
        float ga = cosO * gx - sinO * gz, gb = sinO * gx + cosO * gz;
        var normal = new Vector2(ga, gb);
        if (normal.LengthSquared() < 1e-20f) return 0.0f;
        normal = normal.Normalized();
        return drift.Dot(normal) + rho * pointFromCentre.Dot(normal);
    }

    public static float RimLeadingWeight(Vector2 drift, float rho, Vector2 pointFromCentre, float cosO, float sinO, float majorRadius, float minorRadius)
        => RimLeadingWeight(RimNormalSpeed(drift, rho, pointFromCentre, cosO, sinO, majorRadius, minorRadius));

    // ---- one system ----
    /// <summary>Weights, rim, bearings, hail and instantaneous cooling of one system (no rain-history terms).</summary>
    public static StormArrivalState Evaluate(in StormSystemGeometry g, in StormArrivalTuning tuning)
    {
        if (g.KindCode <= 0 || g.Envelope <= 0.0001f) return StormArrivalState.Calm;
        float n = NormalizedDistance(g, out float ray, out float dist);
        ResolveRims(g.Density, g.PrecipitationPotential, tuning, out float nGust, out float nRain, out float presence);
        // Both the potential and the density scale with the envelope, so the rims move with rho = E'/E through d(rim)/d(ln E), taken as a central
        // difference over +/-2% (a one-second difference would drown in the bisection's resolution).
        const float Eps = 0.02f;
        ResolveRims(Mathf.Min(1.0f, g.Density * (1.0f + Eps)), g.PrecipitationPotential * (1.0f + Eps), tuning, out float gustHi, out float rainHi, out _);
        ResolveRims(g.Density * (1.0f - Eps), g.PrecipitationPotential * (1.0f - Eps), tuning, out float gustLo, out float rainLo, out _);
        float rho = g.EnvelopeRatePerSecond;
        float gustRate = Mathf.Clamp((gustHi - gustLo) / (2.0f * Eps) * rho, -0.05f, 0.05f);
        float rainRate = nRain > 0.0f && rainLo > 0.0f ? Mathf.Clamp((rainHi - rainLo) / (2.0f * Eps) * rho, -0.05f, 0.05f) : 0.0f;
        float vGust = RimSpeed(g, nGust, gustRate);
        float vRain = RimSpeed(g, Mathf.Max(nRain, 0.02f), rainRate);
        float severity = Severity(g);
        float lag = Mathf.Max(20.0f, tuning.GustFrontLeadSeconds);

        // Gust rim: Approach and GustFront.
        float dGust = dist - nGust * ray;                   // signed rim distance along the radial ray, > 0 outside
        float lead = SmoothStep(0.0f, 3.0f, vGust);
        float uGust = dGust / Mathf.Clamp(Mathf.Abs(vGust) * lag, MinBandMeters, MaxBandMeters);
        float band = Mathf.Clamp(Mathf.Abs(vGust) * lag, MinBandMeters, MaxBandMeters);
        float outside = SmoothStep(-0.15f, 0.0f, uGust);
        float alive = SmoothStep(0.05f, 0.25f, g.Envelope);      // a system that is still a speck (or nearly gone) announces nothing
        float approach = alive * outside * SmoothStep(band + ApproachReachMeters, band, dGust) * lead;
        float gust = alive * SmoothStep(1.0f, 0.88f, uGust) * SmoothStep(-0.35f, 0.0f, uGust) * Mathf.Lerp(0.35f, 1.0f, lead) * GustKindScale(g.KindCode);

        // Rain rim: FatDrop, Core, Trailing.
        float dRain = dist - nRain * ray;
        float leadRain = SmoothStep(0.0f, 3.0f, vRain);
        float trail = SmoothStep(0.5f, 7.0f, -vRain);
        float uRain = dRain / Mathf.Clamp(Mathf.Abs(vRain) * lag, MinBandMeters, MaxBandMeters);
        float proxy = RainProxy(n, g.Density, g.PrecipitationPotential, tuning.BaseCoverage);
        float wet = SmoothStep(0.5f * tuning.PrecipitationStart, tuning.PrecipitationStart, proxy);
        float nearCloud = SmoothStep(1.02f, 0.95f, n);
        float fat = presence * nearCloud * (1.0f - trail) * SmoothStep(0.35f, 0.1f, uRain) * SmoothStep(-0.4f, -0.1f, uRain)
            * Mathf.Lerp(1.0f, Mathf.Lerp(0.0f, 1.0f, leadRain), SmoothStep(-0.1f, 0.1f, uRain));
        float inside = SmoothStep(nRain, nRain - 0.06f, n);
        float core = presence * wet * Mathf.Max(SmoothStep(nRain, nRain - 0.30f, n), SmoothStep(-0.3f, -1.0f, uRain)) * (1.0f - trail);
        float trailing = presence * wet * inside * trail;
        // Sustained outflow: from the front edge on, through the gap before the fat drops and the whole core, until the trailing rain eases it (the wind modifier holds a high wind with this).
        float outflow = presence * alive * SmoothStep(1.0f, 0.88f, uGust) * Mathf.Lerp(0.35f, 1.0f, lead) * GustKindScale(g.KindCode) * (1.0f - trail);

        float rimDistance = Mathf.Lerp(dGust, dRain, presence);
        float rimSpeed = Mathf.Lerp(vGust, vRain, presence);
        Vector2 toward = g.OffsetMeters.LengthSquared() > 1.0f ? -g.OffsetMeters.Normalized()
            : (g.DriftMetersPerSecond.LengthSquared() > 1e-4f ? g.DriftMetersPerSecond.Normalized() : Vector2.Right);
        float tRain = float.PositiveInfinity;
        if (rimSpeed > 0.1f && rimDistance > 0.0f) tRain = rimDistance / rimSpeed;

        float instantCool = Mathf.Min(-0.10f * gust, -0.12f * core) * Mathf.Clamp(severity / 0.4f, 0.0f, 1.0f);
        // The rain gate is relative to the system's own peak proxy (a mature MajorFront rarely reaches an absolute 0.6, which left hail almost never happening in production).
        float peakProxy = Mathf.Max(RainProxy(0.0f, g.Density, g.PrecipitationPotential, tuning.BaseCoverage), 0.1f);
        float hail = HailAt(g, severity, Mathf.Min(1.0f, proxy / peakProxy), instantCool, tuning.Hail, out float hailSize);

        var state = new StormArrivalState(g.SystemId, g.KindCode, severity, StormArrivalPhase.Clear,
            approach, gust, fat, core, trailing, 0.0f, rimDistance, rimSpeed, tRain, toward, -toward, hail, hailSize,
            instantCool, float.PositiveInfinity, 0.0f, true, outflow);
        return state with { Phase = ResolvePhase(state) };
    }

    public static StormArrivalPhase ResolvePhase(in StormArrivalState s)
    {
        float best = PhaseThreshold;
        StormArrivalPhase phase = StormArrivalPhase.Clear;
        void Consider(float w, StormArrivalPhase p) { if (w > best) { best = w; phase = p; } }
        // Highest priority first: a later equal weight never displaces an earlier one.
        Consider(s.Core, StormArrivalPhase.Core);
        Consider(s.GustFront, StormArrivalPhase.GustFront);
        Consider(s.FatDrop, StormArrivalPhase.FatDrop);
        Consider(s.Trailing, StormArrivalPhase.Trailing);
        Consider(s.Approach, StormArrivalPhase.Approach);
        Consider(s.Clearing, StormArrivalPhase.Clearing);
        return phase;
    }

    // ---- several systems ----
    /// <summary>Evaluates all systems, keeps the top two by severity x proximity, takes the per-weight max, bearing/kind/id from the dominant one, then adds the rain-history terms.</summary>
    public static StormArrivalState Evaluate(ReadOnlySpan<StormSystemGeometry> systems, in StormArrivalTuning tuning, in RainHistorySample history)
    {
        StormArrivalState first = default, second = default;
        float scoreFirst = -1.0f, scoreSecond = -1.0f;
        bool haveFirst = false, haveSecond = false;
        for (int i = 0; i < systems.Length; i++)
        {
            StormArrivalState s = Evaluate(systems[i], tuning);
            if (!s.Valid) continue;
            float proximity = Mathf.Exp(-Mathf.Max(0.0f, s.RimDistanceMeters) / 15000.0f);
            float score = Mathf.Max(s.Severity, 1e-3f) * proximity;
            if (score > scoreFirst) { second = first; scoreSecond = scoreFirst; haveSecond = haveFirst; first = s; scoreFirst = score; haveFirst = true; }
            else if (score > scoreSecond) { second = s; scoreSecond = score; haveSecond = true; }
        }
        StormArrivalState merged = haveFirst ? first : StormArrivalState.Calm;
        if (haveSecond)
        {
            merged = merged with
            {
                Approach = Mathf.Max(first.Approach, second.Approach), GustFront = Mathf.Max(first.GustFront, second.GustFront),
                FatDrop = Mathf.Max(first.FatDrop, second.FatDrop), Core = Mathf.Max(first.Core, second.Core),
                Trailing = Mathf.Max(first.Trailing, second.Trailing), Outflow = Mathf.Max(first.Outflow, second.Outflow),
                HailIntensity = Mathf.Max(first.HailIntensity, second.HailIntensity),
                HailSize = first.HailIntensity >= second.HailIntensity ? first.HailSize : second.HailSize,
                CoolingOffset = Mathf.Min(first.CoolingOffset, second.CoolingOffset),
                TimeToRainSeconds = Mathf.Min(first.TimeToRainSeconds, second.TimeToRainSeconds)
            };
        }
        return ApplyAftermath(merged, history, tuning);
    }

    /// <summary>Adds Clearing, the aftermath fields and the lingering cooling; resolves the phase.</summary>
    public static StormArrivalState ApplyAftermath(in StormArrivalState s, in RainHistorySample history, in StormArrivalTuning tuning)
    {
        float inside = Mathf.Min(1.0f, s.Core + s.Trailing);
        float tsr = history.Valid ? history.TimeSinceRainSeconds : float.PositiveInfinity;
        float recent = history.Valid ? history.RecentRainAmount : 0.0f;
        float clearing = float.IsInfinity(tsr) ? 0.0f
            : SmoothStep(AftermathGateLow, AftermathGateHigh, recent) * SmoothStep(0.0f, 60.0f, tsr) * (1.0f - inside) * Mathf.Exp(-tsr / 1200.0f);   // only once the rain has stopped
        float after = float.IsInfinity(tsr) ? 0.0f : -0.12f * SmoothStep(AftermathGateLow, AftermathGateHigh, recent) * Mathf.Exp(-tsr / 600.0f);
        var r = s with { Clearing = clearing, TimeSinceRainSeconds = tsr, RecentRainAmount = recent, CoolingOffset = Mathf.Min(s.CoolingOffset, after) };
        r = r with { Phase = ResolvePhase(r), Valid = s.Valid || clearing > PhaseThreshold || recent > 0.0f };
        return r;
    }

    // ---- rain history ----
    /// <summary>Wetness 0..1 of a rain proxy value: smoothstep(start, start + 0.25).</summary>
    public static float Wetness(float proxy, in StormArrivalTuning tuning) => SmoothStep(tuning.PrecipitationStart, tuning.PrecipitationStart + 0.25f, proxy);

    /// <summary>
    /// Reduces the UNTHRESHOLDED rain proxy at each lag (index 0 = now, then <see cref="HistoryLagsSeconds"/>) to a history sample.
    /// RecentRainAmount is the exp(-lag/tau) weighted mean of <see cref="Wetness"/>.
    /// TimeSinceRain is continuous in the samples (no steps at lag boundaries): with dry(l) = 1 - smoothstep(start/2, start, proxy(l)) and C(l) its
    /// running minimum from lag 0, it is the trapezoid area of C over the lags, i.e. the time until the first wet sample with the transition
    /// interpolated linearly between adjacent lags. 0 while raining; +inf (reads "more than HistoryHorizonSeconds") when no lag was wet.
    /// </summary>
    public static RainHistorySample ReduceHistory(ReadOnlySpan<float> proxyByLag, in StormArrivalTuning tuning)
    {
        int count = Math.Min(proxyByLag.Length, HistoryLagsSeconds.Length + 1);
        if (count <= 0) return RainHistorySample.None;
        float sum = 0.0f, weightSum = 0.0f;
        float start = tuning.PrecipitationStart;
        float running = 1.0f, previousC = 1.0f, previousLag = 0.0f, area = 0.0f;
        for (int i = 0; i < count; i++)
        {
            float lag = i == 0 ? 0.0f : HistoryLagsSeconds[i - 1];
            float w = Mathf.Exp(-lag / HistoryTauSeconds);
            sum += Wetness(proxyByLag[i], tuning) * w; weightSum += w;
            running = Mathf.Min(running, 1.0f - SmoothStep(0.5f * start, start, proxyByLag[i]));
            if (i > 0) area += 0.5f * (previousC + running) * (lag - previousLag);
            previousC = running; previousLag = lag;
        }
        float recent = weightSum > 0.0f ? sum / weightSum : 0.0f;
        float timeSince = running > 0.98f ? float.PositiveInfinity : area;
        return new RainHistorySample(timeSince, Mathf.Clamp(recent, 0.0f, 1.0f), true);
    }

    /// <summary>Rain proxy at the observer for one lag's system list: the maximum mean-field proxy over the systems.</summary>
    public static float ProxyOf(ReadOnlySpan<StormSystemGeometry> systems, in StormArrivalTuning tuning)
    {
        float best = 0.0f;
        for (int i = 0; i < systems.Length; i++)
            best = Mathf.Max(best, RainProxyAtObserver(systems[i], tuning));
        return best;
    }

    /// <summary>
    /// Evaluates the rain history at a point displaced by <paramref name="deltaMeters"/> from the snapshot's observer, from the per-lag geometry.
    /// This is the exact reduction S2 ports to GLSL per texel (parity-tested against this function).
    /// </summary>
    public static RainHistorySample RainHistoryAt(RainHistorySnapshot snapshot, Vector2 deltaMeters, in StormArrivalTuning tuning)
    {
        int lags = Math.Min(snapshot.LagCount, HistoryLagsSeconds.Length + 1);
        Span<float> proxies = stackalloc float[HistoryLagsSeconds.Length + 1];
        for (int l = 0; l < lags; l++)
        {
            float best = 0.0f;
            int n = snapshot.Counts[l];
            for (int i = 0; i < n; i++)
            {
                StormSystemGeometry g = snapshot.Systems[l * RainHistorySnapshot.MaxSystemsPerLag + i];
                g = g with { OffsetMeters = g.OffsetMeters + deltaMeters };
                best = Mathf.Max(best, RainProxyAtObserver(g, tuning));
            }
            proxies[l] = best;
        }
        return ReduceHistory(proxies[..lags], tuning);
    }

    // ---- wind modifier (WO-B1) ----
    /// <summary>Result of <see cref="ModifyWind"/>: the new mean wind and the per-call gust multipliers (strength x, base +).</summary>
    public readonly record struct StormWindModifier(Vector2 Mean, float GustStrengthScale, float GustBaseAdd)
    {
        public static StormWindModifier Identity(Vector2 mean) => new(mean, 1.0f, 0.0f);
    }

    /// <summary>
    /// The arrival wind modifier of the design table (section 3.1), applied once to the history-smoothed mean. <paramref name="strength"/> is "Gust front
    /// strength": 0 returns the input untouched (bit-identical), 1 is the design, 2 doubles every weight. Pure and thread-safe.
    /// </summary>
    public static StormWindModifier ModifyWind(in StormArrivalState s, Vector2 mean, Vector2 baseWind, float stormWindSpeed, float strength)
    {
        if (strength <= 0.0f || !s.Valid) return StormWindModifier.Identity(mean);
        float k = Mathf.Min(strength, 2.0f);
        float a = Mathf.Clamp(s.Approach * k, 0.0f, 1.0f), g = Mathf.Clamp(s.GustFront * k, 0.0f, 1.0f);
        float c = Mathf.Clamp(s.Core * k, 0.0f, 1.0f), t = Mathf.Clamp(s.Trailing * k, 0.0f, 1.0f), cl = Mathf.Clamp(s.Clearing * k, 0.0f, 1.0f);
        float f = Mathf.Clamp(s.FatDrop * k, 0.0f, 1.0f);
        float o = Mathf.Clamp(s.Outflow * k, 0.0f, 1.0f);
        if (a + g + f + o + c + t + cl <= 1e-4f) return StormWindModifier.Identity(mean);
        float speed = mean.Length();
        float angle = speed > 1e-3f ? mean.Angle() : (baseWind.LengthSquared() > 1e-4f ? baseWind.Angle() : 0.0f);
        // Calm before the storm, calm air after it, and a slackening wind in the trailing rain.
        speed *= (1.0f - 0.45f * a) * (1.0f - 0.4f * t) * (1.0f - 0.5f * cl);
        // Gust front: the speed jumps to at least the storm wind, blown out of the storm.
        float front = stormWindSpeed * (1.0f + 0.3f * Mathf.Min(s.Severity, 1.5f));
        speed = Mathf.Lerp(speed, Mathf.Max(speed, front), g);
        // Sustained outflow: the strong wind persists through the fat drops and the core (a little under the front's peak) and eases off in the trailing rain.
        float sustain = Mathf.Max(Mathf.Max(f, o), Mathf.Max(c, g)) * (1.0f - 0.7f * t);
        float sustained = stormWindSpeed * (0.9f + 0.3f * Mathf.Min(s.Severity, 1.5f));
        speed = Mathf.Lerp(speed, Mathf.Max(speed, sustained), sustain);
        if (s.StormBearing.LengthSquared() > 1e-6f)
        {
            angle = Mathf.LerpAngle(angle, s.StormBearing.Angle(), 0.4f * a);
            angle = Mathf.LerpAngle(angle, s.OutflowDirection.Angle(), 0.85f * Mathf.Max(Mathf.Max(g, o), Mathf.Max(f, 0.6f * c)));
        }
        if (baseWind.LengthSquared() > 1e-4f) angle = Mathf.LerpAngle(angle, baseWind.Angle(), 0.5f * t);
        Vector2 result = Vector2.FromAngle(angle) * speed;
        if (s.StormBearing.LengthSquared() > 1e-6f) result += s.StormBearing.Normalized() * (3.0f * a);   // inflow toward the storm
        if (!result.IsFinite()) return StormWindModifier.Identity(mean);
        // Gust texture: x0.7 approaching, x(1 + 0.8 g) and +0.15 g base at the front, gusty through the fat drops and core (x1.5, +0.1 base), x0.6 once clearing (trailing x1).
        float strengthScale = 1.0f + (0.7f - 1.0f) * a + 0.8f * g + 0.5f * Mathf.Max(Mathf.Max(f, o), c) * (1.0f - g) + (0.6f - 1.0f) * cl;
        return new StormWindModifier(result.LimitLength(80.0f), Mathf.Max(0.1f, strengthScale), 0.15f * g + 0.1f * Mathf.Max(Mathf.Max(f, o), c) * (1.0f - t));
    }

    // ---- hail ----
    /// <summary>Semi-axes of hail swath k of a system: 1.5-4 km wide, 6-15 km long, never less than twice as long as wide.</summary>
    public static void HailSwathShape(int systemId, int k, out float semiWideMeters, out float semiLongMeters)
    {
        semiWideMeters = Mathf.Lerp(1500.0f, 4000.0f, Hash01(systemId, k, 203));
        semiLongMeters = Mathf.Max(Mathf.Lerp(6000.0f, 15000.0f, Hash01(systemId, k, 204)), 2.0f * semiWideMeters);
    }

    /// <summary>
    /// Deterministic hail intensity (0..1) at the observer for one system and the hail size (0 pea .. 1 golf ball).
    /// Major+ only, lifecycle code 0.8-2.3. Swaths live in the system frame (they move with the system, own drift included),
    /// elongated along the rim motion: radial while growing (L &lt; 1), along the drift once mature. Each swath is live for a hashed
    /// window of lifecycle code (about 6-12 min of the mature stage), faded at both ends.
    /// </summary>
    public static float HailAt(in StormSystemGeometry g, float severity, float rainProxy, float cooling, float hailScale, out float size)
    {
        size = 0.0f;
        if (hailScale <= 0.0f || g.KindCode < 3 || g.Envelope <= 0.0001f) return 0.0f;
        float L = g.LifecycleCode;
        if (L < 0.8f || L > 2.3f) return 0.0f;
        float h0 = Hash01(g.SystemId, 0, 200);
        int baseCount = g.KindCode switch { 3 => 1 + (int)(h0 * 2.0f), 4 => 1 + (int)(h0 * 4.0f), _ => 2 + (int)(h0 * 4.0f) };
        int count = Math.Min(10, (int)(hailScale * baseCount));
        if (count <= 0) return 0.0f;
        float gate = SmoothStep(0.4f, 0.6f, rainProxy);
        if (gate <= 0.0f) return 0.0f;
        float a = Mathf.Max(1.0f, g.MajorRadiusMeters), b = Mathf.Max(1.0f, g.MinorRadiusMeters);
        bool growing = L < 1.0f;
        Vector2 w = g.DriftMetersPerSecond;
        float driftAngle = w.LengthSquared() > 1e-4f ? Mathf.Atan2(w.Y, w.X) : 0.0f;
        float best = 0.0f; float bestHash = 0.0f;
        for (int k = 0; k < count; k++)
        {
            float theta = growing ? Hash01(g.SystemId, k, 201) * Mathf.Tau : driftAngle + (Hash01(g.SystemId, k, 201) - 0.5f) * Mathf.Pi;
            var dir = new Vector2(Mathf.Cos(theta), Mathf.Sin(theta));
            float da = g.OrientationCos * dir.X + g.OrientationSin * dir.Y;
            float dc = -g.OrientationSin * dir.X + g.OrientationCos * dir.Y;
            float ray = 1.0f / Mathf.Sqrt(da * da / (a * a) + dc * dc / (b * b));
            float nk = 0.15f + 0.40f * Hash01(g.SystemId, k, 202);
            Vector2 centre = dir * (ray * nk);
            Vector2 axis = growing || w.LengthSquared() < 1e-4f ? dir : w.Normalized();
            HailSwathShape(g.SystemId, k, out float semiWide, out float semiLong);
            Vector2 q = g.OffsetMeters - centre;
            float qa = q.Dot(axis), qw = q.Dot(new Vector2(-axis.Y, axis.X));
            float e = Mathf.Sqrt(qa * qa / (semiLong * semiLong) + qw * qw / (semiWide * semiWide));
            float profile = 1.0f - SmoothStep(0.55f, 1.0f, e);
            if (profile <= 0.0f) continue;
            float start = Mathf.Lerp(0.85f, 1.7f, Hash01(g.SystemId, k, 205));
            float end = Mathf.Min(2.3f, start + Mathf.Lerp(0.4f, 0.8f, Hash01(g.SystemId, k, 206)));
            float live = SmoothStep(start, start + 0.06f, L) * SmoothStep(end, end - 0.06f, L);
            float v = profile * live;
            if (v > best) { best = v; bestHash = Hash01(g.SystemId, k, 207); }
        }
        float intensity = Mathf.Clamp(best * Mathf.Clamp(severity * 2.0f, 0.0f, 1.0f) * gate, 0.0f, 1.0f);   // severity 0.5 (a strong MajorFront) is full
        if (intensity <= 0.0f) return 0.0f;
        size = Mathf.Clamp(0.25f + 0.65f * bestHash + (-cooling) * 1.5f, 0.0f, 1.0f);
        return intensity;
    }
}
