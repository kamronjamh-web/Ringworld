using System;

namespace RingworldTechDemo.Terrain.Sites;

public enum EmitterPulseDirection : byte { Prograde = 0, Retrograde = 1 }

/// <summary>Pulse tunables (docs/emitter-grid-climate-machines-spec.md G3 defaults). Speed 0 / <see cref="AutoSpeed"/> derives C / period.</summary>
public sealed record EmitterGridPulseSettings
{
    public double PeriodSeconds { get; init; } = 300d;
    /// <summary>When true the speed is C / period (one front per chain in flight); otherwise <see cref="ManualSpeedMetersPerSecond"/>.</summary>
    public bool AutoSpeed { get; init; } = true;
    public double ManualSpeedMetersPerSecond { get; init; } = 104_719.76d; // clamped to 5-500 km/s
    public EmitterPulseDirection Direction { get; init; } = EmitterPulseDirection.Prograde;
    public double ColumnStaggerSeconds { get; init; } = 0d; // 0..5 s, column c delayed by stagger * |c - 2.5| / 2.5
    public double TrailSeconds { get; init; } = 2.5d;
    public double WakeSeconds { get; init; } = 10d;
    public double WakeWeight { get; init; } = 0.05d;
    public double LeadingEdgeMeters { get; init; } = 3_000d;
    /// <summary>Frame time used for the analytic head-streak window (head covers v * dt so it never steps).</summary>
    public double FrameDeltaSeconds { get; init; } = 1d / 60d;
    /// <summary>Wake cut-off in multiples of v * WakeSeconds behind the head.</summary>
    public double CutoffWakeMultiples { get; init; } = 4d;
    public double RelaunchFlareSeconds { get; init; } = 1.5d;
    public double RelaunchFlareGain { get; init; } = 3d;
    /// <summary>Hard cap on fronts evaluated per chain (slow manual speeds put several in flight).</summary>
    public int MaxFrontsPerChain { get; init; } = 64;
}

/// <summary>One live front: its launch number and head position in the direction-adjusted coordinate q (0..C is on the loop).</summary>
public readonly record struct EmitterPulseFront(long Launch, double HeadQ, double AgeSeconds);

/// <summary>
/// Deterministic pulse clock: a pure function of (seed, time, settings), double precision. The caller supplies the saved weather
/// clock as <c>t</c>. Each period the main spire launches one front per chain; the front runs the closed loop in the
/// direction-adjusted coordinate q (prograde q = p, retrograde q = C - p), trails never wrap through q = 0 = C.
/// </summary>
public sealed class EmitterGridPulseClock
{
    public const int ColumnCount = 6;
    public const double MinSpeedMetersPerSecond = 5_000d, MaxSpeedMetersPerSecond = 500_000d;

    public long Seed { get; }
    public double CircumferenceMeters { get; }
    public EmitterGridPulseSettings Settings { get; }
    public double PeriodSeconds { get; }
    public double SpeedMetersPerSecond { get; }
    /// <summary>Seed-fixed epoch offset in [0, period).</summary>
    public double PhaseSeconds { get; }
    /// <summary>Distance behind a head beyond which the profile is zero.</summary>
    public double CutoffMeters { get; }

    public EmitterGridPulseClock(long seed, double circumferenceMeters, EmitterGridPulseSettings? settings = null)
    {
        Settings = settings ?? new EmitterGridPulseSettings();
        Seed = seed;
        CircumferenceMeters = circumferenceMeters;
        PeriodSeconds = Math.Max(1d, Settings.PeriodSeconds);
        SpeedMetersPerSecond = Settings.AutoSpeed
            ? circumferenceMeters / PeriodSeconds
            : Math.Clamp(Settings.ManualSpeedMetersPerSecond, MinSpeedMetersPerSecond, MaxSpeedMetersPerSecond);
        PhaseSeconds = EmitterSiteHash.Unit(EmitterSiteHash.Derive(seed, "emitter-grid-pulse", 0)) * PeriodSeconds;
        CutoffMeters = Settings.CutoffWakeMultiples * SpeedMetersPerSecond * Settings.WakeSeconds;
    }

    public double SecondsPerLink(double linkLengthMeters) => linkLengthMeters / SpeedMetersPerSecond;

    /// <summary>Direction-adjusted coordinate: a front always runs q = 0 -> C.</summary>
    public double ToQ(double p) => Settings.Direction == EmitterPulseDirection.Prograde ? p : CircumferenceMeters - p;

    /// <summary>Link the front enters first (exit) and the link it finishes on (return) for a chain, in pulse direction.</summary>
    public int ExitLink(EmitterGridTopology t, int column)
        => Settings.Direction == EmitterPulseDirection.Prograde ? t.ChainFirstLink(column) : t.ChainLastLink(column);
    public int ReturnLink(EmitterGridTopology t, int column)
        => Settings.Direction == EmitterPulseDirection.Prograde ? t.ChainLastLink(column) : t.ChainFirstLink(column);

    public double StaggerSeconds(int column)
        => Math.Clamp(Settings.ColumnStaggerSeconds, 0d, 5d) * Math.Abs(column - 2.5d) / 2.5d;

    /// <summary>Launch time of front <paramref name="launch"/> on <paramref name="column"/>.</summary>
    public double LaunchTime(long launch, int column = 0) => PhaseSeconds + launch * PeriodSeconds + StaggerSeconds(column);

    /// <summary>Time front <paramref name="launch"/> reaches direction-adjusted coordinate <paramref name="q"/> on a chain (spec ArrivalTime hook).</summary>
    public double ArrivalTime(double q, int column, long launch) => LaunchTime(launch, column) + q / SpeedMetersPerSecond;

    /// <summary>Latest launch number at time <paramref name="t"/> (ignoring stagger).</summary>
    public long LatestLaunch(double t) => LatestLaunch(t, 0);

    private long LatestLaunch(double t, int column)
    {
        long n = (long)Math.Floor((t - PhaseSeconds - StaggerSeconds(column)) / PeriodSeconds);
        while (LaunchTime(n + 1, column) <= t) n++; // exact-boundary safety against floor rounding
        while (LaunchTime(n, column) > t) n--;
        return n;
    }

    /// <summary>
    /// Fronts of a chain that can still light something at <paramref name="t"/> (head not past C + cutoff, already launched).
    /// Newest first. Returns the count written (bounded by the span length and MaxFrontsPerChain).
    /// </summary>
    public int GetFronts(double t, int column, Span<EmitterPulseFront> fronts)
    {
        double v = SpeedMetersPerSecond, c = CircumferenceMeters;
        double stagger = StaggerSeconds(column);
        long n = LatestLaunch(t, column);
        long maxJ = (long)Math.Ceiling((c + CutoffMeters) / (v * PeriodSeconds)) + 1;
        int limit = Math.Min(fronts.Length, Settings.MaxFrontsPerChain);
        int count = 0;
        for (long j = 0; j < maxJ && count < limit; j++)
        {
            double age = t - (PhaseSeconds + (n - j) * PeriodSeconds + stagger);
            if (age < 0d) continue;
            double head = v * age;
            if (head - c > CutoffMeters) break; // older fronts are further still
            fronts[count++] = new EmitterPulseFront(n - j, head, age);
        }
        return count;
    }

    /// <summary>Profile of one front at distance b behind its head (b &lt; 0 is ahead). 0..1, no wrap.</summary>
    public double Profile(double b)
    {
        double v = SpeedMetersPerSecond, e = Settings.LeadingEdgeMeters;
        if (b <= -e) return 0d;
        if (b <= 0d) { double x = (b + e) / e; return x * x * (3d - 2d * x); }
        if (b > CutoffMeters) return 0d;
        double streak = v * Settings.FrameDeltaSeconds;
        double trail = Math.Exp(-Math.Max(0d, b - streak) / (v * Settings.TrailSeconds));
        double wake = Settings.WakeWeight * Math.Exp(-b / (v * Settings.WakeSeconds));
        return Math.Min(1d, trail + wake);
    }

    /// <summary>Pulse brightness (0..1, before day/night scaling) at coordinate q of a chain; q is clamped to the loop, fronts are summed and clamped.</summary>
    public double PulseAtQ(double t, int column, double q)
    {
        if (q < 0d || q > CircumferenceMeters) return 0d;
        Span<EmitterPulseFront> fronts = stackalloc EmitterPulseFront[Math.Min(Settings.MaxFrontsPerChain, 64)];
        int n = GetFronts(t, column, fronts);
        double sum = 0d;
        for (int i = 0; i < n; i++) sum += Profile(fronts[i].HeadQ - q);
        return Math.Min(1d, sum);
    }

    /// <summary>Pulse brightness at fraction <paramref name="f"/> (0..1) along a link, including the knot relaunch flare on knot links.</summary>
    public double PulseAtLink(double t, in EmitterLink link, double f)
    {
        double p = link.P0 + f * (link.P1 - link.P0);
        double pulse = PulseAtQ(t, link.Column, ToQ(p));
        if (link.Kind != EmitterLinkKind.Chain) pulse = Math.Max(pulse, FlareEnvelope(t));
        return pulse;
    }

    /// <summary>Seconds since the latest relaunch (unstaggered: the main-crown flare is one event for all chains).</summary>
    public double SecondsSinceLaunch(double t) => t - LaunchTime(LatestLaunch(t));

    /// <summary>Relaunch flare envelope 1 -> 0 over RelaunchFlareSeconds after each launch (smooth).</summary>
    public double FlareEnvelope(double t)
    {
        double d = Settings.RelaunchFlareSeconds;
        double age = SecondsSinceLaunch(t);
        if (d <= 0d || age >= d) return 0d;
        double x = age / d;
        return 1d - x * x * (3d - 2d * x);
    }

    /// <summary>Main crown brightness multiplier: 1 at rest, RelaunchFlareGain at the instant of launch.</summary>
    public double CrownFlareGain(double t) => 1d + (Settings.RelaunchFlareGain - 1d) * FlareEnvelope(t);

    /// <summary>
    /// Counts links that overlap a lit window [headQ - cutoff, headQ + edge] of any front (or a knot link during the flare), i.e.
    /// the links the vertex stage would not collapse. Pure CPU twin of the shader collapse rule.
    /// </summary>
    public int CountLitLinks(EmitterGridTopology topology, double t)
    {
        int lit = 0;
        Span<EmitterPulseFront> fronts = stackalloc EmitterPulseFront[Math.Min(Settings.MaxFrontsPerChain, 64)];
        double c = CircumferenceMeters, edge = Settings.LeadingEdgeMeters;
        bool flare = FlareEnvelope(t) > 0d;
        for (int col = 0; col < topology.Columns; col++)
        {
            int n = GetFronts(t, col, fronts);
            for (int k = 0; k < topology.LinksPerChain; k++)
            {
                ref readonly EmitterLink l = ref topology.Links[col * topology.LinksPerChain + k];
                double qa = ToQ(l.P0), qb = ToQ(l.P1);
                double lo = Math.Min(qa, qb), hi = Math.Max(qa, qb);
                bool on = flare && l.Kind != EmitterLinkKind.Chain;
                for (int i = 0; i < n && !on; i++)
                {
                    double head = fronts[i].HeadQ;
                    double wLo = Math.Max(0d, head - CutoffMeters), wHi = Math.Min(c, head + edge);
                    if (wHi > wLo && hi > wLo && lo < wHi) on = true;
                }
                if (on) lit++;
            }
        }
        return lit;
    }
}

/// <summary>Tunables for the CPU reference intensity (G2); the G2 shader mirrors these.</summary>
public sealed record EmitterGridVisibilitySettings
{
    public double NightPeak { get; init; } = 1d;
    public double DayPeak { get; init; } = 0.2d;
    public double NightResidual { get; init; } = 0.03d;
    public double DayResidual { get; init; } = 0d;
    public double BaseEmission { get; init; } = 6d;
    public double DayDistanceNearMeters { get; init; } = 50_000d;
    public double DayDistanceFarMeters { get; init; } = 500_000d;
}

/// <summary>CPU reference for the beam brightness (spec G2): I = Peak(B) * Pulse + Residual(B) + Crawl, times atmosphere transmittance.</summary>
public static class EmitterGridIntensity
{
    public static double Smoothstep(double e0, double e1, double x)
    {
        double t = Math.Clamp((x - e0) / (e1 - e0), 0d, 1d);
        return t * t * (3d - 2d * t);
    }

    /// <summary>Per-point effective day: smoothstep(-0.2, 0.35, dot(surfaceUp, sun)) times the published solar visibility (0 in eclipse).</summary>
    public static double DayAt(double dotSurfaceUpSun, double solarVisibility)
        => Smoothstep(-0.2d, 0.35d, dotSurfaceUpSun) * Math.Clamp(solarVisibility, 0d, 1d);

    /// <summary>B = max(dayAt(camera), dayAt(fragment) * smoothstep(50 km, 500 km, viewDistance)).</summary>
    public static double Background(double dayAtCamera, double dayAtFragment, double viewDistanceMeters, EmitterGridVisibilitySettings? s = null)
    {
        s ??= new EmitterGridVisibilitySettings();
        return Math.Max(dayAtCamera, dayAtFragment * Smoothstep(s.DayDistanceNearMeters, s.DayDistanceFarMeters, viewDistanceMeters));
    }

    public static double Peak(double b, EmitterGridVisibilitySettings? s = null)
    { s ??= new EmitterGridVisibilitySettings(); return s.NightPeak + (s.DayPeak - s.NightPeak) * b; }

    public static double Residual(double b, EmitterGridVisibilitySettings? s = null)
    { s ??= new EmitterGridVisibilitySettings(); return s.NightResidual + (s.DayResidual - s.NightResidual) * b; }

    /// <summary>Unitless brightness (multiply by BaseEmission for HDR). <paramref name="atmosphereAlpha"/> is the haze extinction in [0, 1].</summary>
    public static double Compute(double pulse, double b, double crawl = 0d, double atmosphereAlpha = 0d, EmitterGridVisibilitySettings? s = null)
        => (Peak(b, s) * pulse + Residual(b, s) + crawl) * (1d - Math.Clamp(atmosphereAlpha, 0d, 1d));

    /// <summary>Brightness at fraction f along a link at time t.</summary>
    public static double AtLink(EmitterGridPulseClock clock, in EmitterLink link, double f, double t, double b, double crawl = 0d,
        double atmosphereAlpha = 0d, EmitterGridVisibilitySettings? s = null)
        => Compute(clock.PulseAtLink(t, link, f), b, crawl, atmosphereAlpha, s);
}
