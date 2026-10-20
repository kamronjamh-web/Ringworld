using System;

namespace RingworldTechDemo.Shadows;

/// <summary>
/// User-facing settings of the S6 screen-space contact shadows (grave-key menu, Shadows tab "Contact shadows"). Pure C# so the controller, the node and headless
/// tests share one definition. Normalized() clamps every field to its menu range. Default OFF until the A1000 gate (docs/shadow-revamp-plan.md S6).
/// </summary>
public sealed record RingContactShadowSettings
{
    public const float DefaultLengthMeters = 1.5f, DefaultThicknessMeters = 0.25f, DefaultStrength = 1.0f, DefaultFadeMeters = 150.0f, DefaultGrassStrength = 0.35f;
    public const int DefaultSteps = 16;
    public static readonly int[] StepChoices = { 8, 12, 16, 24, 32 };

    public bool Enabled { get; init; }
    public float LengthMeters { get; init; } = DefaultLengthMeters;
    public float ThicknessMeters { get; init; } = DefaultThicknessMeters;
    public float Strength { get; init; } = DefaultStrength;
    public float FadeMeters { get; init; } = DefaultFadeMeters;
    public float GrassStrength { get; init; } = DefaultGrassStrength;
    public int Steps { get; init; } = DefaultSteps;

    public RingContactShadowSettings Normalized() => this with
    {
        LengthMeters = Clamp(LengthMeters, 0.25f, 4f, DefaultLengthMeters),
        ThicknessMeters = Clamp(ThicknessMeters, 0.05f, 1f, DefaultThicknessMeters),
        Strength = Clamp(Strength, 0f, 1f, DefaultStrength),
        FadeMeters = Clamp(FadeMeters, 25f, 400f, DefaultFadeMeters),
        GrassStrength = Clamp(GrassStrength, 0f, 1f, DefaultGrassStrength),
        Steps = NearestSteps(Steps),
    };

    public static int NearestSteps(float steps)
    {
        int best = StepChoices[0];
        foreach (int choice in StepChoices) if (Math.Abs(choice - steps) < Math.Abs(best - steps)) best = choice;
        return best;
    }

    private static float Clamp(float value, float min, float max, float fallback) => float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
