using System;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// The player-tunable card settings that change placement, tiers or the atlas (grass-cards plan step S8). A value type so the layer can
/// capture it every step without allocating; equal values give an equal <see cref="RingGrassStyleProfile"/>. The first six fields
/// are the placement class (a change rebuilds every tile through the placement fingerprint); the rest are curve values (coverage
/// fingerprint: tier filter or rebuild, never a reset). Defaults are variant D and equal the layer's export defaults.
/// </summary>
internal readonly record struct RingGrassCardTuning(
    float NearDensity, float LowLayerDensity, float TallHeight, float ShortHeight, float Lean, int AtlasBlades,
    float BaseDensity, float FloorDensity, float FalloffExponent, float NearBoostStartMeters, float NearBoostEndMeters,
    float LowLayerEndMeters, float CompensationMax, float NearWidthScale, float FadeBandPercent)
{
    /// <summary>Use this, not default(...): the all-zero struct means "not provided" (Blades inputs).</summary>
    public static readonly RingGrassCardTuning Default = new(
        14f, 18f, .6f, .3f, .12f, RingGrassCardAtlasParameters.VariantD.BladesPerVariant,
        2.5f, .15f, 1.3f, 8f, 45f, 22f, 2.5f, .7f, 15f);

    /// <summary>Compensation above this would outgrow the instance bounds and the GPU frustum guard (both assume 2.5).</summary>
    public const float MaximumCompensation = RingGrassCardPlacement.BoundsHeightScale;
    /// <summary>The curve's low-layer full-density distance is not tunable; the end distance stays beyond it.</summary>
    public const float LowLayerMinimumEndMeters = 10f;

    public bool IsUnset => NearDensity == 0f && AtlasBlades == 0;

    /// <summary>Finite, clamped to the setting ranges, with the companion orderings (short below tall, boost start below end, floor not above base).</summary>
    public RingGrassCardTuning Normalized()
    {
        RingGrassCardTuning d = Default;
        static float F(float v, float fallback, float min, float max) => Mathf.Clamp(float.IsFinite(v) ? v : fallback, min, max);
        float tall = F(TallHeight, d.TallHeight, .2f, 1.5f);
        float shortHeight = Math.Min(F(ShortHeight, d.ShortHeight, .1f, 1f), tall - .01f);
        float boostStart = F(NearBoostStartMeters, d.NearBoostStartMeters, 0f, 100f);
        float boostEnd = Math.Max(F(NearBoostEndMeters, d.NearBoostEndMeters, 1f, 200f), boostStart + 1f);
        float baseDensity = F(BaseDensity, d.BaseDensity, .5f, 6f);
        return new RingGrassCardTuning(
            F(NearDensity, d.NearDensity, 2f, 24f), F(LowLayerDensity, d.LowLayerDensity, 0f, 30f), tall, shortHeight,
            F(Lean, d.Lean, 0f, .5f), Math.Clamp(AtlasBlades, 4, 96),
            baseDensity, Math.Min(F(FloorDensity, d.FloorDensity, .02f, 1f), baseDensity), F(FalloffExponent, d.FalloffExponent, .5f, 2.5f),
            boostStart, boostEnd, F(LowLayerEndMeters, d.LowLayerEndMeters, LowLayerMinimumEndMeters, 60f),
            F(CompensationMax, d.CompensationMax, 1f, MaximumCompensation), F(NearWidthScale, d.NearWidthScale, .3f, 1f),
            F(FadeBandPercent, d.FadeBandPercent, 1f, 50f));
    }
}

/// <summary>
/// Material-only card settings: cheap uniform writes, no rebuild, no fingerprint. Defaults are read from
/// <see cref="RingGrassCardMaterialConfig"/> (the single source of card uniform defaults).
/// </summary>
internal readonly record struct RingGrassCardMaterialSettings(
    float GreenPull, float AlphaCutoff, float SingleCardStartMeters, float SingleCardEndMeters, float Tilt,
    float NormalOut, float NormalFace, float Backlight, float RootAo, float TipLight, float BladeValueJitter,
    float BladeHueJitter, float DryFraction, float TuftValueJitter, float FragmentColorMeters,
    float ConvergeStartPercent, float ConvergeEndPercent)
{
    public static RingGrassCardMaterialSettings Default => From(new RingGrassCardMaterialConfig());

    public static RingGrassCardMaterialSettings From(RingGrassCardMaterialConfig c) => new(
        c.GreenPull, c.AlphaCutoff, c.SingleCardStartMeters, c.SingleCardEndMeters, c.Tilt, c.NormalOut, c.NormalFace, c.Backlight,
        c.RootAo, c.TipLight, c.BladeValueJitter, c.BladeHueJitter, c.DryFraction, c.TuftValueJitter, c.FragmentColorMeters,
        c.ConvergeStartPercent, c.ConvergeEndPercent);

    public RingGrassCardMaterialSettings Normalized()
    {
        RingGrassCardMaterialSettings d = Default;
        static float F(float v, float fallback, float min, float max) => Mathf.Clamp(float.IsFinite(v) ? v : fallback, min, max);
        float singleStart = F(SingleCardStartMeters, d.SingleCardStartMeters, 20f, 600f);
        float convergeStart = F(ConvergeStartPercent, d.ConvergeStartPercent, 0f, 99f);
        return new RingGrassCardMaterialSettings(
            F(GreenPull, d.GreenPull, 0f, 1f), F(AlphaCutoff, d.AlphaCutoff, .1f, .9f), singleStart,
            Math.Max(F(SingleCardEndMeters, d.SingleCardEndMeters, 30f, 700f), singleStart + 1f), F(Tilt, d.Tilt, 0f, .4f),
            F(NormalOut, d.NormalOut, 0f, 1f), F(NormalFace, d.NormalFace, 0f, 1f), F(Backlight, d.Backlight, 0f, 1f),
            F(RootAo, d.RootAo, 0f, 1f), F(TipLight, d.TipLight, 0f, .5f), F(BladeValueJitter, d.BladeValueJitter, 0f, .6f),
            F(BladeHueJitter, d.BladeHueJitter, 0f, .3f), F(DryFraction, d.DryFraction, 0f, .5f), F(TuftValueJitter, d.TuftValueJitter, 0f, .5f),
            F(FragmentColorMeters, d.FragmentColorMeters, 10f, 400f), convergeStart,
            Math.Max(F(ConvergeEndPercent, d.ConvergeEndPercent, 1f, 100f), convergeStart + 1f));
    }

    public void ApplyTo(RingGrassCardMaterialConfig c)
    {
        c.GreenPull = GreenPull; c.AlphaCutoff = AlphaCutoff; c.SingleCardStartMeters = SingleCardStartMeters;
        c.SingleCardEndMeters = SingleCardEndMeters; c.Tilt = Tilt; c.NormalOut = NormalOut; c.NormalFace = NormalFace;
        c.Backlight = Backlight; c.RootAo = RootAo; c.TipLight = TipLight; c.BladeValueJitter = BladeValueJitter;
        c.BladeHueJitter = BladeHueJitter; c.DryFraction = DryFraction; c.TuftValueJitter = TuftValueJitter;
        c.FragmentColorMeters = FragmentColorMeters; c.ConvergeStartPercent = ConvergeStartPercent; c.ConvergeEndPercent = ConvergeEndPercent;
    }
}
