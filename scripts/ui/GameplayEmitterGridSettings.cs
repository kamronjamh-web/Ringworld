using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Persisted emitter relay grid choices (docs/emitter-grid-climate-machines-spec.md C8). Enums are stored as ints.</summary>
[GlobalClass]
public partial class GameplayEmitterGridSettings : Resource
{
    [Export] public bool GridEnabled { get; set; } = true;
    [Export] public float PulsePeriodSeconds { get; set; } = 300f;
    /// <summary>0 = Auto (C / period), 1 = Manual.</summary>
    [Export] public int PulseSpeedMode { get; set; }
    [Export] public float PulseSpeedKmPerSecond { get; set; } = 104.7f;
    /// <summary>0 = Prograde, 1 = Retrograde.</summary>
    [Export] public int PulseDirection { get; set; }
    [Export] public float PulseLeadingEdgeKm { get; set; } = 3f;
    [Export] public float PulseTrailSeconds { get; set; } = 2.5f;
    [Export] public float PulseWakeSeconds { get; set; } = 10f;
    [Export] public float PulseWakeLevel { get; set; } = 0.05f;
    [Export] public float RelaunchFlareStrength { get; set; } = 3f;
    [Export] public float ColumnStaggerSeconds { get; set; }
    [Export] public float NightPeakIntensity { get; set; } = 1f;
    [Export] public float DayPeakIntensity { get; set; } = 0.2f;
    [Export] public float NightResidual { get; set; } = 0.03f;
    [Export] public float DayResidual { get; set; }
    [Export] public float BaseEmission { get; set; } = 6f;
    [Export] public float CoreWidthMeters { get; set; } = 40f;
    [Export] public float HaloWidthMeters { get; set; } = 600f;
    [Export] public float HaloAmplitude { get; set; } = 0.12f;
    [Export] public float MinPixelWidth { get; set; } = 1.5f;
    [Export] public float FarCompensation { get; set; } = 0.6f;
    [Export] public float FrontSpriteMinPixels { get; set; } = 3f;
    [Export] public float HeadSpriteNearKm { get; set; } = 300f;
    [Export] public float HeadSpriteFullKm { get; set; } = 600f;
    [Export] public float NearLayerMaxKm { get; set; } = 700f;
    [Export] public bool FarLayerEnabled { get; set; } = true;
    /// <summary>0 = Off, 1 = Dim, 2 = DimAndFarGlow, 3 = Full. Consumed by G5.</summary>
    [Export] public int CloudInteraction { get; set; } = 2;
    [Export] public float CloudSampleHz { get; set; } = 10f;
    [Export] public bool DepthWriteCore { get; set; } = true;
    /// <summary>Depth-writing core pass (spec G4.4): a beam core writes depth only where its coverage exceeds this threshold.</summary>
    [Export] public float CoreDepthThreshold { get; set; } = 0.35f;
    /// <summary>Depth-writing core pass: the core is widened to at least this many pixels so thin distant cores still write depth.</summary>
    [Export] public float CoreDepthMinPixels { get; set; } = 4f;
    /// <summary>When true the near-to-far beam handoff band follows the cloud handoff (spec G4.1); otherwise the manual band below is used.</summary>
    [Export] public bool FarHandoffFollowsClouds { get; set; } = true;
    /// <summary>Manual handoff band start in km (used when <see cref="FarHandoffFollowsClouds"/> is off).</summary>
    [Export] public float FarHandoffStartKm { get; set; } = 120f;
    /// <summary>Manual handoff band end in km (used when <see cref="FarHandoffFollowsClouds"/> is off).</summary>
    [Export] public float FarHandoffEndKm { get; set; } = 172f;
    [Export] public bool LightningCrawl { get; set; } = true;
    [Export] public float CrawlRadiusKm { get; set; } = 8f;
    [Export] public float CrawlLengthKm { get; set; } = 40f;
    [Export] public bool PulseTriggeredStrikes { get; set; }
}
