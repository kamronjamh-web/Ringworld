using System;

namespace RingworldTechDemo.Celestial.Eclipse;

/// <summary>
/// Pitch-black eclipse settings (docs/eclipse-false-sun-spec.md D.1). Every scale is the multiplier applied to the matching night term at
/// full darkness weight (w = 1); at w = 0 (any normal night or day) every term is exactly its current value. Call <see cref="Sanitized"/>
/// before use. <see cref="CloudAmbientScale"/> can never go below <see cref="EclipseDarknessModel.CloudAmbientFloor"/> (spec D.2: black
/// clouds make Sunshine's night floor translucent).
/// </summary>
public sealed record EclipseDarknessSettings
{
    /// <summary>Master switch for the darkness model (off = today's lighting in every phase of an eclipse).</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Environment ambient energy (and fog light energy) multiplier at totality: 0.30 becomes 0.045 by default.</summary>
    public double AmbientScale { get; init; } = 0.15d;
    /// <summary>Atmosphere night colour multiplier (haze emission, far-arch glow): global <c>ringworld_night_light_scale</c>.</summary>
    public double AtmosphereNightScale { get; init; } = 0.35d;
    /// <summary>Sky night palette (top and horizon) multiplier.</summary>
    public double SkyNightScale { get; init; } = 0.5d;
    /// <summary>Cloud night ambient multiplier (floor 0.3).</summary>
    public double CloudAmbientScale { get; init; } = 0.4d;
    /// <summary>Star visibility reached at totality (a floor: stars are never dimmed below what the night sky already shows).</summary>
    public double StarVisibility { get; init; } = 1d;

    public EclipseDarknessSettings Sanitized() => this with
    {
        AmbientScale = C(AmbientScale, 0d, 1d, 0.15d),
        AtmosphereNightScale = C(AtmosphereNightScale, 0d, 1d, 0.35d),
        SkyNightScale = C(SkyNightScale, 0d, 1d, 0.5d),
        CloudAmbientScale = C(CloudAmbientScale, EclipseDarknessModel.CloudAmbientFloor, 1d, 0.4d),
        StarVisibility = C(StarVisibility, 0d, 1d, 1d)
    };

    private static double C(double v, double lo, double hi, double fallback) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;
}

/// <summary>The multipliers for one frame (all exactly 1, and <see cref="StarFloor"/> exactly 0, at weight 0).</summary>
public readonly record struct EclipseDarknessOutput(
    double Weight, double AmbientMultiplier, double AtmosphereNightMultiplier, double SkyNightMultiplier,
    double CloudAmbientMultiplier, double StarFloor)
{
    public static EclipseDarknessOutput Neutral => new(0d, 1d, 1d, 1d, 1d, 0d);
}

/// <summary>
/// Pure darkness model (spec D.1). The weight is <c>w = localCoverage x (1 - relief)</c>, smoothed over <see cref="SmoothSeconds"/>, where
/// relief = <c>min(1, L / KindleLevel x 0.5)</c> when the hub lights the ring. Phase E2 only darkens, so relief is applied only when the caller
/// passes <c>hubReliefEnabled</c> (E3 switches it on with the hub lighting); until then the world stays dark through the whole window.
/// The model reads only the raw coverage and the hub level. It never reads <c>GameplayEclipseDarkness</c> or its 0.95 cap (spec D.3), so
/// the cap stays a partial-phase fog/sky tint control and totality darkness does not depend on it.
/// </summary>
public sealed class EclipseDarknessModel
{
    /// <summary>Spec D.2: the cloud ambient multiplier never goes below this.</summary>
    public const double CloudAmbientFloor = 0.3d;
    /// <summary>The weight reaches about 95 % of a step in this many seconds (exponential, time constant SmoothSeconds / 3).</summary>
    public const double SmoothSeconds = 0.5d;
    /// <summary>Set to true by phase E3 when the hub light exists; E2 keeps the world dark for the whole window.</summary>
    public const bool HubReliefAvailable = false;

    private double _weight;
    private bool _primed;

    public double Weight => _weight;

    /// <summary>Instantaneous weight before smoothing.</summary>
    public static double TargetWeight(double localCoverage, double hubLevel, double kindleLevel, bool hubReliefEnabled)
    {
        double coverage = double.IsFinite(localCoverage) ? Math.Clamp(localCoverage, 0d, 1d) : 0d;
        double relief = 0d;
        if (hubReliefEnabled && double.IsFinite(hubLevel) && kindleLevel > 1e-6)
            relief = Math.Min(1d, Math.Max(0d, hubLevel) / kindleLevel * 0.5d);
        return coverage * (1d - relief);
    }

    /// <summary>1 - w (1 - scale): the multiplier for a term whose full-darkness scale is <paramref name="scale"/>.</summary>
    public static double Mix(double weight, double scale) => weight <= 0d ? 1d : 1d - weight * (1d - scale);

    public static EclipseDarknessOutput Evaluate(EclipseDarknessSettings settings, double weight)
    {
        if (!settings.Enabled || !(weight > 0d)) return EclipseDarknessOutput.Neutral;
        double w = Math.Min(1d, weight);
        return new EclipseDarknessOutput(
            w,
            Mix(w, settings.AmbientScale),
            Mix(w, settings.AtmosphereNightScale),
            Mix(w, settings.SkyNightScale),
            Mix(w, Math.Max(settings.CloudAmbientScale, CloudAmbientFloor)),
            w * settings.StarVisibility);
    }

    /// <summary>Advances the smoothed weight by <paramref name="deltaSeconds"/> (real time) and returns the frame output. The first call adopts the target (no fade-in on load).</summary>
    public EclipseDarknessOutput Update(EclipseDarknessSettings settings, double localCoverage, double hubLevel, double kindleLevel, double deltaSeconds, bool hubReliefEnabled = HubReliefAvailable)
    {
        double target = settings.Enabled ? TargetWeight(localCoverage, hubLevel, kindleLevel, hubReliefEnabled) : 0d;
        if (!_primed || !double.IsFinite(deltaSeconds) || deltaSeconds < 0d) { _weight = target; _primed = true; }
        else
        {
            double alpha = 1d - Math.Exp(-deltaSeconds / (SmoothSeconds / 3d));
            _weight += (target - _weight) * alpha;
            if (Math.Abs(_weight - target) < 1e-5) _weight = target;
        }
        return Evaluate(settings, _weight);
    }

    public void Reset() { _weight = 0d; _primed = false; }
}
