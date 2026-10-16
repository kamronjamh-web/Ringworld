using System;
using System.Threading.Tasks;
using Godot;

namespace RingworldTechDemo.Celestial.Eclipse;

/// <summary>
/// Totality window of one eclipse (docs/eclipse-false-sun-spec.md A.3). All times are absolute celestial seconds.
/// <see cref="RingStart"/>/<see cref="RingEnd"/> are the event window [S, E] with the parallax margin applied;
/// <see cref="RawRingStart"/>/<see cref="RawRingEnd"/> are the whole-ring window before the margin.
/// </summary>
public readonly record struct EclipseWindow(
    int Index, bool Valid, double CentreSeconds,
    double CentredStart, double CentredEnd,
    double RawRingStart, double RawRingEnd,
    double AnyStart, double AnyEnd,
    double MarginSeconds)
{
    public double RingStart => RawRingStart + MarginSeconds;
    public double RingEnd => RawRingEnd - MarginSeconds;
    public double CentredDuration => CentredEnd - CentredStart;
    public double RingDuration => RingEnd - RingStart;
    public double RawRingDuration => RawRingEnd - RawRingStart;
    public double AnyDuration => AnyEnd - AnyStart;
    /// <summary>Spread of second contact over the ring (the umbra sweep time).</summary>
    public double SweepSeconds => RawRingStart - AnyStart;

    public static EclipseWindow Invalid(int index, double centre, double margin)
        => new(index, false, centre, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, margin);
}

/// <summary>Player-local second and third contact of one eclipse (absolute celestial seconds).</summary>
public readonly record struct EclipseLocalContacts(bool Valid, double SecondContact, double ThirdContact);

/// <summary>
/// Pure whole-ring totality solver (spec A.3). Bisects the centred contacts and then the contacts of 36 ring longitudes at three
/// across offsets, using <see cref="CelestialEclipseMath.EvaluateFrame(double, Vector3, Vector3, Vector3, CelestialConfiguration, Vector3, bool)"/>
/// and <c>EclipseType == Total</c>. No scene, no state besides its inputs: the result is identical on a worker thread.
/// </summary>
public sealed class EclipseWindowSolver
{
    public const int LongitudeSamples = 36;
    public const double DefaultRingRadiusKm = 5000d;
    public const double DefaultHalfWidthKm = 159d;
    public const double DefaultMarginSeconds = 10d;
    /// <summary>Epoch sun of the shipped scene: the authored StarLight basis Z (scenes/VoxelRingIntegrationTest.tscn).</summary>
    public static readonly Vector3 DefaultAuthoredEpochSun = new(-0.36868784f, 0.76604444f, 0.5265408f);

    private const double GallopStartSeconds = 120d;
    private const int BisectionSteps = 40;

    private readonly CelestialConfiguration _config;
    private readonly Vector3 _epochSun, _axis, _e1, _e2;

    public double RingRadiusKm { get; }
    public double HalfWidthKm { get; }
    public double OrbitSeconds { get; }
    public double InitialPhaseDegrees { get; }

    public EclipseWindowSolver(CelestialConfiguration? configuration = null, Vector3? authoredEpochSun = null, Vector3? ringAxis = null,
        double ringRadiusKm = DefaultRingRadiusKm, double halfWidthKm = DefaultHalfWidthKm)
    {
        _config = configuration ?? new CelestialConfiguration();
        _epochSun = authoredEpochSun ?? DefaultAuthoredEpochSun;
        Vector3 axis = ringAxis ?? Vector3.Up;
        _axis = axis.LengthSquared() > 1e-12f ? axis.Normalized() : Vector3.Up;
        Vector3 e1 = Vector3.Right - _axis * Vector3.Right.Dot(_axis);
        if (e1.LengthSquared() < 1e-6f) e1 = Vector3.Back - _axis * Vector3.Back.Dot(_axis);
        _e1 = e1.Normalized();
        _e2 = _e1.Cross(_axis).Normalized();
        RingRadiusKm = ringRadiusKm;
        HalfWidthKm = halfWidthKm;
        GasGiantConfiguration giant = _config.GasGiant ?? new GasGiantConfiguration();
        OrbitSeconds = giant.OrbitSeconds > 0d && double.IsFinite(giant.OrbitSeconds) ? giant.OrbitSeconds : GasGiantConfiguration.DefaultOrbitSeconds;
        InitialPhaseDegrees = double.IsFinite(giant.InitialPhaseDegrees) ? giant.InitialPhaseDegrees : GasGiantConfiguration.DefaultInitialPhaseDegrees;
    }

    /// <summary>Conjunction time of eclipse <paramref name="index"/>: orbit angle 0 (T * (k + 1/2) for the default 180 degree phase).</summary>
    public double CentreTime(int index)
    {
        double phase = InitialPhaseDegrees / 360d;
        phase -= Math.Floor(phase);
        return OrbitSeconds * (index + 1d - phase);
    }

    /// <summary>Index of the eclipse whose conjunction is nearest to <paramref name="seconds"/> (never below 0).</summary>
    public int NearestIndex(double seconds)
        => Math.Max(0, (int)Math.Round((seconds - CentreTime(0)) / OrbitSeconds));

    /// <summary>Solves eclipse <paramref name="index"/>; <see cref="EclipseWindow.Valid"/> is false without a total eclipse at every sample.</summary>
    public EclipseWindow Solve(int index, double marginSeconds = DefaultMarginSeconds)
    {
        double centre = CentreTime(index);
        double margin = double.IsFinite(marginSeconds) ? Math.Clamp(marginSeconds, 0d, 120d) : DefaultMarginSeconds;
        if (_config.GasGiant?.Enabled != true) return EclipseWindow.Invalid(index, centre, margin);
        if (!FindTotality(centre, Vector3.Zero, -_e1, out double cs, out double ce)) return EclipseWindow.Invalid(index, centre, margin);

        double s = double.NegativeInfinity, e = double.PositiveInfinity, anyS = double.PositiveInfinity, anyE = double.NegativeInfinity;
        for (int i = 0; i < LongitudeSamples; i++)
        {
            double lon = Math.Tau * i / LongitudeSamples;
            for (int a = -1; a <= 1; a++)
            {
                GetObserver(lon, a * HalfWidthKm, RingRadiusKm, out Vector3 up, out Vector3 offset);
                if (!FindTotality(centre, offset, up, out double start, out double end))
                    return EclipseWindow.Invalid(index, centre, margin);
                s = Math.Max(s, start); e = Math.Min(e, end);
                anyS = Math.Min(anyS, start); anyE = Math.Max(anyE, end);
            }
        }
        var window = new EclipseWindow(index, true, centre, cs, ce, s, e, anyS, anyE, margin);
        return window.RingEnd > window.RingStart ? window : EclipseWindow.Invalid(index, centre, margin);
    }

    /// <summary>Runs <see cref="Solve"/> on a worker (about 7 ms); the solver has no mutable state.</summary>
    public Task<EclipseWindow> SolveAsync(int index, double marginSeconds = DefaultMarginSeconds)
        => Task.Run(() => Solve(index, marginSeconds));

    /// <summary>Player-local contacts for an observer at ring longitude / across / radius (km).</summary>
    public EclipseLocalContacts LocalContacts(int index, double longitudeRadians, double acrossKm, double radiusKm)
    {
        GetObserver(longitudeRadians, acrossKm, radiusKm, out Vector3 up, out Vector3 offset);
        return LocalContacts(index, offset, up);
    }

    /// <summary>Player-local contacts for an explicit observer offset (km, from the ring centre) and surface up.</summary>
    public EclipseLocalContacts LocalContacts(int index, Vector3 observerOffsetKm, Vector3 surfaceUp)
    {
        if (_config.GasGiant?.Enabled != true) return new EclipseLocalContacts(false, double.NaN, double.NaN);
        bool ok = FindTotality(CentreTime(index), observerOffsetKm, surfaceUp, out double start, out double end);
        return new EclipseLocalContacts(ok, ok ? start : double.NaN, ok ? end : double.NaN);
    }

    /// <summary>True when the observer is inside total eclipse at <paramref name="seconds"/> (direct frame evaluation).</summary>
    public bool IsTotalAt(double seconds, Vector3 observerOffsetKm, Vector3 surfaceUp)
        => CelestialEclipseMath.EvaluateFrame(seconds, _epochSun, surfaceUp, _axis, _config, observerOffsetKm, calculateGiantEvents: false)
            .EclipseType == CelestialEclipseType.Total;

    /// <summary>Observer geometry in the ring frame (longitude about the ring axis, across along it, radius from the axis).</summary>
    public void GetObserver(double longitudeRadians, double acrossKm, double radiusKm, out Vector3 surfaceUp, out Vector3 offsetKm)
    {
        Vector3 radial = _e1 * (float)Math.Cos(longitudeRadians) + _e2 * (float)Math.Sin(longitudeRadians);
        surfaceUp = -radial;
        offsetKm = radial * (float)radiusKm + _axis * (float)acrossKm;
    }

    private bool FindTotality(double centre, Vector3 offset, Vector3 up, out double start, out double end)
    {
        start = end = double.NaN;
        if (centre < 0d || !IsTotalAt(centre, offset, up)) return false;
        start = SearchEdge(centre, -1d, offset, up);
        end = SearchEdge(centre, +1d, offset, up);
        return double.IsFinite(start) && double.IsFinite(end);
    }

    /// <summary>Galloping search from a total sample, then bisection; returns the total-side boundary (so IsTotal(edge) holds).</summary>
    private double SearchEdge(double from, double direction, Vector3 offset, Vector3 up)
    {
        double inside = from, step = GallopStartSeconds, outside = double.NaN;
        while (Math.Abs(inside - from) <= OrbitSeconds)
        {
            double next = inside + direction * step;
            if (direction < 0d && next < 0d) { outside = Math.Max(0d, next); if (IsTotalAt(0d, offset, up)) return 0d; break; }
            if (IsTotalAt(next, offset, up)) { inside = next; step *= 2d; }
            else { outside = next; break; }
        }
        if (!double.IsFinite(outside)) return double.NaN;
        for (int i = 0; i < BisectionSteps; i++)
        {
            double mid = (inside + outside) * 0.5d;
            if (IsTotalAt(mid, offset, up)) inside = mid; else outside = mid;
        }
        return inside;
    }
}
