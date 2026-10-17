using System;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Celestial.Eclipse;

/// <summary>
/// Spire to hub beam look settings (docs/eclipse-false-sun-spec.md E.2, G.1; phase E4b). Visual only: they never feed the timeline, so
/// <see cref="EclipseEventEngine.SetSettings"/> treats a change here like a darkness change (no timeline rebuild). Widths are real metres.
/// Call <see cref="Sanitized"/> before use.
/// </summary>
public sealed record EclipseBeamSettings
{
    /// <summary>Master switch: off removes the near and far beam layers (the hub light and the timeline are unaffected).</summary>
    public bool BeamsEnabled { get; init; } = true;
    public bool FarLayerEnabled { get; init; } = true;
    /// <summary>Depth-writing core next_pass (T2): clouds behind a beam no longer dim it.</summary>
    public bool DepthWriteCore { get; init; } = true;
    /// <summary>Regular spire bore diameter (Gaussian core width) and the exponential halo width.</summary>
    public double CoreWidthMeters { get; init; } = 150d;
    public double HaloWidthMeters { get; init; } = 1200d;
    public double MainCoreWidthMeters { get; init; } = 300d;
    public double MainHaloWidthMeters { get; init; } = 2400d;
    public double MinPixelWidth { get; init; } = 1.5d;
    /// <summary>Energy-conserving gain exponent for beams widened to the pixel footprint (spec E.2: 0.45, lower than the grid's 0.6).</summary>
    public double FarCompensation { get; init; } = 0.45d;
    public double BaseEmission { get; init; } = 12d;
    public double MainEmissionScale { get; init; } = 1.5d;
    public double HaloAmplitude { get; init; } = 0.12d;
    /// <summary>The halo fades in over this many metres above the beam origin so it does not start with a hard horizontal edge.</summary>
    public double BaseFadeMeters { get; init; } = 400d;
    /// <summary>
    /// Where the beams rise from (tweaks T2): the bore iris at the spire base (default, 200 m under the pad), the crown focus or the reactor exhaust top
    /// (a preview until the reactors are placed). A change rebuilds the 595 beam transforms and re-encodes the in-cloud data, nothing else.
    /// </summary>
    public EmitterBeamOrigin BeamOrigin { get; init; } = EmitterBeamOrigin.Base;
    /// <summary>
    /// Close-range softening (tweaks T2): the beam's light fades in over this many metres of view distance (smoothstep from 0), so a camera at the bore rim
    /// is not whited out by the 12x emission core. 0 turns it off.
    /// </summary>
    public double CloseSoftenMeters { get; init; } = 1000d;
    /// <summary>Brightness factor applied while the hub day factor is above 0.5 so the beams do not flatten the hub-lit sky (spec E.2).</summary>
    public double DayVisibility { get; init; } = 0.6d;
    /// <summary>Depth core: coverage x intensity above which the core writes depth, and the minimum full width (pixels) of that footprint.</summary>
    public double CoreDepthThreshold { get; init; } = 0.35d;
    public double CoreDepthMinPixels { get; init; } = 4d;
    /// <summary>Soft tip length as a fraction of the beam (spec E.2: 0.5 %).</summary>
    public double SoftTipFraction { get; init; } = 0.005d;
    /// <summary>Phase E4d: multiplier of the grid's night peak while the ignition fronts are live (spec E.3: 1.5; the background is forced to night then).</summary>
    public double GridEventPeak { get; init; } = 1.5d;
    /// <summary>
    /// Phase E4e (spec E.6): in-cloud diffusion. A near spire's beam crossing the cloud deck widens its halo by 1 + <see cref="CloudWidening"/> k over the deck
    /// span (k = the cloud coverage at the spire, from one weather query per near spire, at most <see cref="EclipseHubBeamLayer.MaxWeatherQueries"/> per eclipse),
    /// and brightens it by 1 + <see cref="CloudHaloBoost"/> k. Where k times the deck mask exceeds <see cref="CloudCoreCut"/> the depth-writing core is switched off,
    /// so Sunshine blends that segment. The default 1.5 never cuts (E4e measurement: a cut under a storm drops the beam light to the halo-only level).
    /// </summary>
    public bool CloudDiffusionEnabled { get; init; } = true;
    public double CloudWidening { get; init; } = 3d;
    public double CloudHaloBoost { get; init; } = 1d;
    public double CloudCoreCut { get; init; } = 1.5d;
    /// <summary>Phase E4e (spec E.7): stand-in crown glow (INSTANCE_CUSTOM.b of the far stand-ins, 595 x 3 writes per eclipse) and its HDR emission.</summary>
    public bool CrownGlowEnabled { get; init; } = true;
    public double CrownGlowEmission { get; init; } = 3d;
    /// <summary>Phase E4e (spec E.7): factor on the near POI bore plug light energy while its spire fires (1 = no boost).</summary>
    public double BoreLightBoost { get; init; } = 4d;

    /// <summary>Tweaks T3 (Sustain refuel orbs): position ease exponent (1 = constant speed, 3 = a visible lift-off over about 3 s), orb core radius and halo e-fold (metres), minimum core radius in pixels and HDR emission.</summary>
    public double OrbEaseExponent { get; init; } = 3d;
    public double OrbRadiusMeters { get; init; } = 350d;
    public double OrbHaloMeters { get; init; } = 1500d;
    public double OrbMinPixelRadius { get; init; } = 2.5d;
    public double OrbEmission { get; init; } = 16d;

    /// <summary>
    /// Beam cloud light (tweaks T4/T5): the beams, the refuel orbs and (optionally) the relay grid links light the Sunshine clouds they pass through, as
    /// segment lights inside the cloud raymarch. At most <see cref="CloudLightMaxSegments"/> (1-8) nearest to the camera, each reaching
    /// <see cref="CloudLightRadiusMeters"/> from its line; <see cref="CloudLightIntensity"/> scales the colour. With the feature off, or outside the event,
    /// nothing is pushed and the clouds are exactly as before. Visual only.
    /// </summary>
    public bool CloudLightEnabled { get; init; } = true;
    public double CloudLightIntensity { get; init; } = 5d;
    public double CloudLightRadiusMeters { get; init; } = 4000d;
    public int CloudLightMaxSegments { get; init; } = 4;
    /// <summary>Hub beams and orbs light the clouds first; this adds the relay grid links (pulses) after them.</summary>
    public bool CloudLightGridLinks { get; init; } = true;

    public EclipseBeamSettings Sanitized() => this with
    {
        CoreWidthMeters = C(CoreWidthMeters, 10d, 2000d, 150d),
        HaloWidthMeters = C(HaloWidthMeters, 50d, 20000d, 1200d),
        MainCoreWidthMeters = C(MainCoreWidthMeters, 10d, 4000d, 300d),
        MainHaloWidthMeters = C(MainHaloWidthMeters, 50d, 40000d, 2400d),
        MinPixelWidth = C(MinPixelWidth, 0.5d, 8d, 1.5d),
        FarCompensation = C(FarCompensation, 0d, 1d, 0.45d),
        BaseEmission = C(BaseEmission, 0d, 60d, 12d),
        MainEmissionScale = C(MainEmissionScale, 0.1d, 6d, 1.5d),
        HaloAmplitude = C(HaloAmplitude, 0d, 1d, 0.12d),
        BaseFadeMeters = C(BaseFadeMeters, 0d, 5000d, 400d),
        BeamOrigin = Enum.IsDefined(BeamOrigin) ? BeamOrigin : EmitterBeamOrigin.Base,
        CloseSoftenMeters = C(CloseSoftenMeters, 0d, 5000d, 1000d),
        DayVisibility = C(DayVisibility, 0d, 1d, 0.6d),
        CoreDepthThreshold = C(CoreDepthThreshold, 0.01d, 2d, 0.35d),
        CoreDepthMinPixels = C(CoreDepthMinPixels, 1d, 16d, 4d),
        SoftTipFraction = C(SoftTipFraction, 0.0005d, 0.05d, 0.005d),
        GridEventPeak = C(GridEventPeak, 0.25d, 4d, 1.5d),
        CloudWidening = C(CloudWidening, 0d, 8d, 3d),
        CloudHaloBoost = C(CloudHaloBoost, 0d, 4d, 1d),
        CloudCoreCut = C(CloudCoreCut, 0.05d, 1.5d, 1.5d),
        CrownGlowEmission = C(CrownGlowEmission, 0d, 20d, 3d),
        BoreLightBoost = C(BoreLightBoost, 1d, 8d, 4d),
        OrbEaseExponent = C(OrbEaseExponent, 1d, 4d, 3d),
        OrbRadiusMeters = C(OrbRadiusMeters, 20d, 5000d, 350d),
        OrbHaloMeters = C(OrbHaloMeters, 50d, 20000d, 1500d),
        OrbMinPixelRadius = C(OrbMinPixelRadius, 0.5d, 12d, 2.5d),
        OrbEmission = C(OrbEmission, 0d, 100d, 16d),
        CloudLightIntensity = C(CloudLightIntensity, 0d, 20d, 5d),
        CloudLightRadiusMeters = C(CloudLightRadiusMeters, 500d, 20000d, 4000d),
        CloudLightMaxSegments = Math.Clamp(CloudLightMaxSegments, 1, 8)
    };

    private static double C(double v, double lo, double hi, double fallback) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;
}
