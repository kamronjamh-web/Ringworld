using System;

namespace RingworldTechDemo.Shadows;

/// <summary>
/// User-facing settings of the S4 terrain height-shadow producer (grave-key menu, Shadows tab "Terrain height shadows"). Pure C# so the controller, the
/// scheduler node and headless tests share one definition. The level-0 texel size and the range are the same choice: level 1 is always 4x coarser and the
/// 1024 texel windows give a range of 2000 x t0 (4 m -> 8 km). Normalized() clamps every field to its menu range.
/// </summary>
public sealed record RingHeightShadowSettings
{
    public const float DefaultSunStepDegrees = 0.1f, DefaultPenumbraDegrees = 0.5f, DefaultCrossfadeSeconds = 1.0f, DefaultBudgetMs = 0.15f;
    public const int DefaultL0TexelMeters = 4, DefaultLevels = 2, DefaultRowsPerFrame = 64, DefaultFillWorkers = 2;
    public const int DefaultNearMapDistanceMeters = 400;
    public const int DefaultPlanetNearMapDistanceMeters = 250, MinimumPlanetNearMapDistanceMeters = 100, MaximumPlanetNearMapDistanceMeters = 250;
    public static readonly int[] TexelChoices = { 2, 4, 8 };
    public static readonly int[] NearMapDistanceChoices = { 300, 400, 500 };

    public bool Enabled { get; init; }
    public int L0TexelMeters { get; init; } = DefaultL0TexelMeters;
    public int Levels { get; init; } = DefaultLevels;
    public float SunStepDegrees { get; init; } = DefaultSunStepDegrees;
    public int RowsPerFrame { get; init; } = DefaultRowsPerFrame;
    public float PenumbraDegrees { get; init; } = DefaultPenumbraDegrees;
    public float CrossfadeSeconds { get; init; } = DefaultCrossfadeSeconds;
    public int FillWorkers { get; init; } = DefaultFillWorkers;
    /// <summary>Per-frame GPU time the sliced build may use; the slicing rule shrinks rows per frame until the measured (or modelled) cost fits.</summary>
    public float BudgetMs { get; init; } = DefaultBudgetMs;

    /// <summary>While the heightfield is the published sun light source, shrink the sun shadow map to a near map (it then only owns the first few hundred metres; the heightfield owns the rest). Effective only with Enabled.</summary>
    public bool NearMapEnabled { get; init; } = true;
    public int NearMapDistanceMeters { get; init; } = DefaultNearMapDistanceMeters;

    /// <summary>S7a: the same for the planet light at night (2 cascades, fade 0.8) while the published front layer is a planet layer.</summary>
    public bool PlanetNearMapEnabled { get; init; } = true;
    public int PlanetNearMapDistanceMeters { get; init; } = DefaultPlanetNearMapDistanceMeters;

    public int Level1TexelMeters => RingHeightShadowLattice.Level1Texel(L0TexelMeters);
    /// <summary>Half extent of the coarse window in metres (level 1: 512 texels each way).</summary>
    public int RangeMeters => L0TexelMeters * 2000;

    public static int TexelForRange(int rangeMeters) => rangeMeters switch { 4000 => 2, 8000 => 4, 16000 => 8, _ => 0 };
    public static int NearestTexel(float meters)
    {
        int best = TexelChoices[0];
        foreach (int choice in TexelChoices) if (Math.Abs(choice - meters) < Math.Abs(best - meters)) best = choice;
        return best;
    }

    public RingHeightShadowSettings Normalized() => this with
    {
        L0TexelMeters = NearestTexel(L0TexelMeters),
        Levels = Math.Clamp(Levels, 1, 2),
        SunStepDegrees = Clamp(SunStepDegrees, 0.02f, 0.5f, DefaultSunStepDegrees),
        RowsPerFrame = Math.Clamp(RowsPerFrame, 16, 1024),
        PenumbraDegrees = Clamp(PenumbraDegrees, 0f, 2f, DefaultPenumbraDegrees),
        CrossfadeSeconds = Clamp(CrossfadeSeconds, 0f, 3f, DefaultCrossfadeSeconds),
        FillWorkers = Math.Clamp(FillWorkers, 1, 4),
        BudgetMs = Clamp(BudgetMs, 0.05f, 2f, DefaultBudgetMs),
        NearMapDistanceMeters = NearestNearMapDistance(NearMapDistanceMeters),
        PlanetNearMapDistanceMeters = Math.Clamp(PlanetNearMapDistanceMeters, MinimumPlanetNearMapDistanceMeters, MaximumPlanetNearMapDistanceMeters),
    };

    public static int NearestNearMapDistance(float meters)
    {
        int best = NearMapDistanceChoices[0];
        foreach (int choice in NearMapDistanceChoices) if (Math.Abs(choice - meters) < Math.Abs(best - meters)) best = choice;
        return best;
    }

    private static float Clamp(float value, float min, float max, float fallback) => float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
