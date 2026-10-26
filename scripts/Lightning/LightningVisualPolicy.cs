using System;

namespace RingworldTechDemo.Lightning;

/// <summary>
/// Player-facing lightning policy (grave-key menu, Lighting tab): natural strike rate, upper-atmosphere sprite rate and the
/// "Reduced flashes" accessibility option. Static because <see cref="RingLightningEvent.PulseAtAge"/> is the single source
/// every consumer (bolt mesh, LocalStormFlash light, Sunshine history suppression, rain streaks, far flashes) reads.
/// </summary>
public static class LightningVisualPolicy
{
    public const float DefaultFrequency = 1.0f, DefaultUpperAtmosphere = 1.0f, MaxScale = 2.0f;
    /// <summary>Reduced flashes: peak pulse multiplier, share of natural strikes kept, and share of upper-atmosphere events kept.</summary>
    public const float ReducedPeakScale = 0.4f, ReducedStrikeShare = 0.5f, ReducedUpperAtmosphereShare = 0.35f;
    /// <summary>Reduced flashes: ceiling on the summed brightness of simultaneously visible flashes (one reduced flash peaks at <see cref="ReducedPeakScale"/>).</summary>
    public const float ReducedSummedCap = 0.55f;

    /// <summary>Natural strike scheduler multiplier, 0..2.</summary>
    public static float FrequencyScale { get; private set; } = DefaultFrequency;
    /// <summary>Red sprite / blue jet chance multiplier, 0..2.</summary>
    public static float UpperAtmosphereScale { get; private set; } = DefaultUpperAtmosphere;
    public static bool ReducedFlashes { get; private set; }

    public static void Apply(float frequency, float upperAtmosphere, bool reducedFlashes)
    {
        FrequencyScale = Clean(frequency, DefaultFrequency);
        UpperAtmosphereScale = Clean(upperAtmosphere, DefaultUpperAtmosphere);
        ReducedFlashes = reducedFlashes;
    }

    public static void Reset() => Apply(DefaultFrequency, DefaultUpperAtmosphere, false);

    /// <summary>Multiplier on the natural strike probability (frequency, halved when flashes are reduced).</summary>
    public static float NaturalStrikeChanceScale => FrequencyScale * (ReducedFlashes ? ReducedStrikeShare : 1.0f);
    /// <summary>Multiplier on the upper-atmosphere event chance.</summary>
    public static float UpperAtmosphereChanceScale => UpperAtmosphereScale * (ReducedFlashes ? ReducedUpperAtmosphereShare : 1.0f);
    /// <summary>Multiplier on every flash brightness (1 when flashes are not reduced).</summary>
    public static float PeakScale => ReducedFlashes ? ReducedPeakScale : 1.0f;

    /// <summary>
    /// Scales <paramref name="levels"/> in place so their sum stays under <see cref="ReducedSummedCap"/> when flashes are reduced,
    /// so overlapping storms cannot add up to a full-strength flash. Returns the applied scale (1 when nothing changed).
    /// </summary>
    public static float LimitSummed(Span<float> levels)
    {
        if (!ReducedFlashes) return 1.0f;
        float sum = 0.0f;
        for (int i = 0; i < levels.Length; i++) sum += Math.Max(0.0f, levels[i]);
        if (sum <= ReducedSummedCap) return 1.0f;
        float scale = ReducedSummedCap / sum;
        for (int i = 0; i < levels.Length; i++) levels[i] *= scale;
        return scale;
    }

    private static float Clean(float value, float fallback) => float.IsFinite(value) ? Math.Clamp(value, 0.0f, MaxScale) : fallback;
}
