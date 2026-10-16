using System;
using Godot;
using RingworldTechDemo.UI;

/// <summary>Isolated persistence evidence for the emitter relay grid preferences (defaults, round-trip, clamps, reset, invalid path).</summary>
public partial class EmitterGridSettingsSmoke : Node
{
    public string Result { get; private set; } = "RUNNING";
    public string IsolatedPath { get; set; } = "res://.godot/emitter_grid_settings_smoke.tres";
    public override void _Ready()
    {
        try { Run(IsolatedPath); Result = "PASS"; }
        catch (Exception error) { Result = "FAIL " + error.Message; }
    }

    static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    private static bool IsDefault(GameplayEmitterGridSettings s) =>
        s.GridEnabled && Mathf.IsEqualApprox(s.PulsePeriodSeconds, 300) && s.PulseSpeedMode == 0 && Mathf.IsEqualApprox(s.PulseSpeedKmPerSecond, 104.7f)
        && s.PulseDirection == 0 && Mathf.IsEqualApprox(s.PulseLeadingEdgeKm, 3) && Mathf.IsEqualApprox(s.PulseTrailSeconds, 2.5f)
        && Mathf.IsEqualApprox(s.PulseWakeSeconds, 10) && Mathf.IsEqualApprox(s.PulseWakeLevel, .05f) && Mathf.IsEqualApprox(s.RelaunchFlareStrength, 3)
        && Mathf.IsEqualApprox(s.ColumnStaggerSeconds, 0) && Mathf.IsEqualApprox(s.NightPeakIntensity, 1) && Mathf.IsEqualApprox(s.DayPeakIntensity, .2f)
        && Mathf.IsEqualApprox(s.NightResidual, .03f) && Mathf.IsEqualApprox(s.DayResidual, 0) && Mathf.IsEqualApprox(s.BaseEmission, 6)
        && Mathf.IsEqualApprox(s.CoreWidthMeters, 40) && Mathf.IsEqualApprox(s.HaloWidthMeters, 600) && Mathf.IsEqualApprox(s.HaloAmplitude, .12f)
        && Mathf.IsEqualApprox(s.MinPixelWidth, 1.5f) && Mathf.IsEqualApprox(s.FarCompensation, .6f) && Mathf.IsEqualApprox(s.FrontSpriteMinPixels, 3)
        && Mathf.IsEqualApprox(s.HeadSpriteNearKm, 300) && Mathf.IsEqualApprox(s.HeadSpriteFullKm, 600) && Mathf.IsEqualApprox(s.NearLayerMaxKm, 700)
        && s.FarLayerEnabled && s.CloudInteraction == 2 && Mathf.IsEqualApprox(s.CloudSampleHz, 10) && s.DepthWriteCore
        && s.LightningCrawl && Mathf.IsEqualApprox(s.CrawlRadiusKm, 8) && Mathf.IsEqualApprox(s.CrawlLengthKm, 40) && !s.PulseTriggeredStrikes
        && Mathf.IsEqualApprox(s.CoreDepthThreshold, .35f) && Mathf.IsEqualApprox(s.CoreDepthMinPixels, 4) && s.FarHandoffFollowsClouds
        && Mathf.IsEqualApprox(s.FarHandoffStartKm, 120) && Mathf.IsEqualApprox(s.FarHandoffEndKm, 172);

    private static void Run(string path)
    {
        var first = new GameplayEmitterGridDefaults(path); first.Load();
        Require(IsDefault(first.Settings) && !first.HasSavedFile, "missing grid preferences did not use defaults");
        Require(first.Save(), "clean defaults should save as a no-op");
        Require(first.SetGridEnabled(false) && !first.SetGridEnabled(false), "Set did not report the changed flag");
        first.SetPulsePeriodSeconds(120); first.SetPulseSpeedMode(1); first.SetPulseSpeedKmPerSecond(250); first.SetPulseDirection(1);
        first.SetPulseLeadingEdgeKm(5); first.SetPulseTrailSeconds(4); first.SetPulseWakeSeconds(20); first.SetPulseWakeLevel(.1f);
        first.SetRelaunchFlareStrength(4.5f); first.SetColumnStaggerSeconds(2); first.SetNightPeakIntensity(2); first.SetDayPeakIntensity(.5f);
        first.SetNightResidual(.1f); first.SetDayResidual(.05f); first.SetBaseEmission(9); first.SetCoreWidthMeters(80); first.SetHaloWidthMeters(1200);
        first.SetHaloAmplitude(.3f); first.SetMinPixelWidth(2.5f); first.SetFarCompensation(.2f); first.SetFrontSpriteMinPixels(5);
        first.SetHeadSpriteNearKm(200); first.SetHeadSpriteFullKm(900); first.SetNearLayerMaxKm(500); first.SetFarLayerEnabled(false);
        first.SetCloudInteraction(3); first.SetCloudSampleHz(20); first.SetDepthWriteCore(false); first.SetLightningCrawl(false);
        first.SetCrawlRadiusKm(12); first.SetCrawlLengthKm(80); first.SetPulseTriggeredStrikes(true);
        first.SetCoreDepthThreshold(.8f); first.SetCoreDepthMinPixels(6); first.SetFarHandoffFollowsClouds(false); first.SetFarHandoffStartKm(90); first.SetFarHandoffEndKm(200);
        Require(first.Dirty && first.Save() && !first.Dirty, "grid preferences did not save");
        var second = new GameplayEmitterGridDefaults(path); second.Load();
        var s = second.Settings;
        Require(!s.GridEnabled && Mathf.IsEqualApprox(s.PulsePeriodSeconds, 120) && s.PulseSpeedMode == 1 && Mathf.IsEqualApprox(s.PulseSpeedKmPerSecond, 250)
            && s.PulseDirection == 1 && Mathf.IsEqualApprox(s.PulseLeadingEdgeKm, 5) && Mathf.IsEqualApprox(s.PulseTrailSeconds, 4)
            && Mathf.IsEqualApprox(s.PulseWakeSeconds, 20) && Mathf.IsEqualApprox(s.PulseWakeLevel, .1f) && Mathf.IsEqualApprox(s.RelaunchFlareStrength, 4.5f)
            && Mathf.IsEqualApprox(s.ColumnStaggerSeconds, 2), "pulse preferences did not round-trip");
        Require(Mathf.IsEqualApprox(s.NightPeakIntensity, 2) && Mathf.IsEqualApprox(s.DayPeakIntensity, .5f) && Mathf.IsEqualApprox(s.NightResidual, .1f)
            && Mathf.IsEqualApprox(s.DayResidual, .05f) && Mathf.IsEqualApprox(s.BaseEmission, 9) && Mathf.IsEqualApprox(s.CoreWidthMeters, 80)
            && Mathf.IsEqualApprox(s.HaloWidthMeters, 1200) && Mathf.IsEqualApprox(s.HaloAmplitude, .3f) && Mathf.IsEqualApprox(s.MinPixelWidth, 2.5f)
            && Mathf.IsEqualApprox(s.FarCompensation, .2f) && Mathf.IsEqualApprox(s.FrontSpriteMinPixels, 5), "look preferences did not round-trip");
        Require(Mathf.IsEqualApprox(s.HeadSpriteNearKm, 200) && Mathf.IsEqualApprox(s.HeadSpriteFullKm, 900) && Mathf.IsEqualApprox(s.NearLayerMaxKm, 500)
            && !s.FarLayerEnabled && s.CloudInteraction == 3 && Mathf.IsEqualApprox(s.CloudSampleHz, 20) && !s.DepthWriteCore && !s.LightningCrawl
            && Mathf.IsEqualApprox(s.CrawlRadiusKm, 12) && Mathf.IsEqualApprox(s.CrawlLengthKm, 80) && s.PulseTriggeredStrikes, "range/cloud/crawl preferences did not round-trip");
        Require(Mathf.IsEqualApprox(s.CoreDepthThreshold, .8f) && Mathf.IsEqualApprox(s.CoreDepthMinPixels, 6) && !s.FarHandoffFollowsClouds
            && Mathf.IsEqualApprox(s.FarHandoffStartKm, 90) && Mathf.IsEqualApprox(s.FarHandoffEndKm, 200), "core/handoff preferences did not round-trip");

        // Setter clamps and NaN fallback.
        second.SetPulsePeriodSeconds(1); second.SetPulseSpeedKmPerSecond(1e9f); second.SetPulseSpeedMode(9); second.SetPulseDirection(-3);
        second.SetPulseLeadingEdgeKm(float.NaN); second.SetColumnStaggerSeconds(99); second.SetNightPeakIntensity(-1); second.SetCoreWidthMeters(0);
        second.SetHaloWidthMeters(1e9f); second.SetMinPixelWidth(float.PositiveInfinity); second.SetNearLayerMaxKm(5000); second.SetCloudInteraction(77);
        second.SetCloudSampleHz(0); second.SetCrawlLengthKm(1e9f);
        second.SetCoreDepthThreshold(0); second.SetCoreDepthMinPixels(99); second.SetFarHandoffStartKm(1e9f); second.SetFarHandoffEndKm(float.NaN);
        s = second.Settings;
        var (pMin, pMax) = GameplayEmitterGridDefaults.RangeOf(nameof(GameplayEmitterGridSettings.PulsePeriodSeconds));
        Require(Mathf.IsEqualApprox(s.PulsePeriodSeconds, pMin) && Mathf.IsEqualApprox(pMax, 1800), "period lower bound not enforced");
        Require(Mathf.IsEqualApprox(s.PulseSpeedKmPerSecond, 500) && s.PulseSpeedMode == 1 && s.PulseDirection == 0, "speed/enum bounds not enforced");
        Require(Mathf.IsEqualApprox(s.PulseLeadingEdgeKm, 3), "NaN did not fall back to the default");
        Require(Mathf.IsEqualApprox(s.ColumnStaggerSeconds, 5) && Mathf.IsEqualApprox(s.NightPeakIntensity, 0) && Mathf.IsEqualApprox(s.CoreWidthMeters, 5)
            && Mathf.IsEqualApprox(s.HaloWidthMeters, 3000) && Mathf.IsEqualApprox(s.MinPixelWidth, 1.5f) && Mathf.IsEqualApprox(s.NearLayerMaxKm, 850)
            && s.CloudInteraction == 3 && Mathf.IsEqualApprox(s.CloudSampleHz, 2) && Mathf.IsEqualApprox(s.CrawlLengthKm, 150), "setter bounds were not enforced");
        Require(Mathf.IsEqualApprox(s.CoreDepthThreshold, .05f) && Mathf.IsEqualApprox(s.CoreDepthMinPixels, 8) && Mathf.IsEqualApprox(s.FarHandoffStartKm, 650)
            && Mathf.IsEqualApprox(s.FarHandoffEndKm, 172), "core/handoff setter bounds were not enforced");

        using var dirty = new GameplayEmitterGridSettings { PulsePeriodSeconds = 99999, PulseSpeedKmPerSecond = float.NaN, PulseSpeedMode = 5, PulseDirection = -2,
            PulseWakeLevel = float.PositiveInfinity, DayPeakIntensity = -3, BaseEmission = 1e9f, HaloAmplitude = float.NaN, FarCompensation = 9, HeadSpriteNearKm = 1,
            CloudSampleHz = 1e9f, CrawlRadiusKm = -1, GridEnabled = false, DepthWriteCore = false, CoreDepthThreshold = float.NaN, CoreDepthMinPixels = -4, FarHandoffStartKm = float.NaN, FarHandoffEndKm = 1e9f, FarHandoffFollowsClouds = false };
        using var clean = GameplayEmitterGridDefaults.Sanitize(dirty);
        Require(Mathf.IsEqualApprox(clean.PulsePeriodSeconds, 1800) && Mathf.IsEqualApprox(clean.PulseSpeedKmPerSecond, 104.7f) && clean.PulseSpeedMode == 1 && clean.PulseDirection == 0
            && Mathf.IsEqualApprox(clean.PulseWakeLevel, .05f) && Mathf.IsEqualApprox(clean.DayPeakIntensity, 0) && Mathf.IsEqualApprox(clean.BaseEmission, 20)
            && Mathf.IsEqualApprox(clean.HaloAmplitude, .12f) && Mathf.IsEqualApprox(clean.FarCompensation, 1) && Mathf.IsEqualApprox(clean.HeadSpriteNearKm, 50)
            && Mathf.IsEqualApprox(clean.CloudSampleHz, 30) && Mathf.IsEqualApprox(clean.CrawlRadiusKm, 1) && !clean.GridEnabled && !clean.DepthWriteCore
            && Mathf.IsEqualApprox(clean.CoreDepthThreshold, .35f) && Mathf.IsEqualApprox(clean.CoreDepthMinPixels, 1) && Mathf.IsEqualApprox(clean.FarHandoffStartKm, 120)
            && Mathf.IsEqualApprox(clean.FarHandoffEndKm, 700) && !clean.FarHandoffFollowsClouds, "Sanitize bounds were not enforced");

        second.Reset();
        Require(second.Save(), "grid reset did not save");
        var reset = new GameplayEmitterGridDefaults(path); reset.Load();
        Require(IsDefault(reset.Settings), "grid reset did not restore defaults");

        string invalidParent = path + ".invalid-parent.tres";
        Require(ResourceSaver.Save(new GameplayEmitterGridSettings(), invalidParent) == Error.Ok, "invalid-path fixture write failed");
        var invalid = new GameplayEmitterGridDefaults(invalidParent + "/child.tres"); invalid.Load(); invalid.SetPulsePeriodSeconds(60);
        Require(!invalid.Save() && invalid.LastError.Length > 0, "invalid grid preference path did not fail safely");
        bool threw = false;
        try { invalid.SetFloat("NoSuchField", 1); } catch (ArgumentException) { threw = true; }
        Require(threw, "unknown field did not throw");
    }
}
