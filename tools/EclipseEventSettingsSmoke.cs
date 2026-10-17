using System;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.UI;

/// <summary>Isolated persistence evidence for the eclipse false-sun event preferences (defaults, round-trip, clamps, NaN sanitize, reset, invalid path, core mapping).</summary>
public partial class EclipseEventSettingsSmoke : Node
{
    public string Result { get; private set; } = "RUNNING";
    public string IsolatedPath { get; set; } = "res://.godot/eclipse_event_settings_smoke.tres";
    public override void _Ready()
    {
        try { Run(IsolatedPath); Result = "PASS"; }
        catch (Exception error) { Result = "FAIL " + error.Message; }
    }

    static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    private static readonly string[] FloatNames =
    {
        "ParallaxSafetySeconds", "PreRollSeconds", "DarknessBeatSeconds", "MainClimbSeconds", "MainClimbExponent", "KindleSeconds", "KindleLevel",
        "IgnitionDelaySeconds", "IgnitionSeconds", "RegularClimbSeconds", "RampGamma", "RampStepSmoothSeconds", "IgnitionPlateauLevel", "MeetingFlareSeconds",
        "CrescendoSeconds", "HandoffFadeSeconds", "FlashClimbSeconds", "FlashHoldSeconds", "FlashFadeSeconds", "OrbTravelSeconds", "HeartbeatKick", "HeartbeatTauSeconds",
        "RelaunchKick", "BreathAmplitude", "BreathPeriodSeconds", "CollapseLeadSeconds", "InstabilitySeconds", "FlickerMax", "FlickerHzStart", "FlickerHzEnd",
        "OverloadSeconds", "OverloadGain", "ImplosionSeconds", "BeamRetractSeconds", "ShockwaveSeconds", "ShockwaveArrivalFraction", "ShockFlashLevel",
        "ShockFlashSeconds", "ReturnTailSeconds", "RealSunEnergy", "HubDirectFraction", "HubDayAtFull",
        "HubLightRed", "HubLightGreen", "HubLightBlue", "HubSkyTintStrength", "HubZenithHaloIntensity",
        "EclipseAmbientScale", "EclipseAtmosphereNightScale", "EclipseSkyNightScale", "EclipseCloudAmbientScale", "EclipseStarVisibility",
        "BeamCoreWidthMeters", "BeamHaloWidthMeters", "BeamMainCoreWidthMeters", "BeamMainHaloWidthMeters", "BeamMinPixelWidth", "BeamFarCompensation",
        "BeamBaseEmission", "BeamMainEmissionScale", "BeamHaloAmplitude", "BeamBaseFadeMeters", "BeamCloseSoftenMeters", "BeamDayVisibility", "BeamCoreDepthThreshold", "BeamCoreDepthMinPixels", "BeamSoftTipFraction", "BeamGridEventPeak",
        "BeamCloudWidening", "BeamCloudHaloBoost", "BeamCloudCoreCut", "BeamCrownGlowEmission", "BeamBoreLightBoost",
        "BeamOrbEaseExponent", "BeamOrbRadiusMeters", "BeamOrbHaloMeters", "BeamOrbMinPixelRadius", "BeamOrbEmission",
        "BeamCloudLightIntensity", "BeamCloudLightRadiusMeters", "BeamCloudLightMaxSegments",
        "HubBodyCoreEmission", "HubBodyRadiusKm", "HubBodyMinRadiusPixels", "HubBodyHaloAmplitude", "HubBodyHaloRadiusKm", "HubSkyInScatter"
    };

    private static readonly string[] HubBodyFieldNames =
    {
        "HubBodyEnabled", "HubBodyCoreEmission", "HubBodyRadiusKm", "HubBodyMinRadiusPixels", "HubBodyHaloAmplitude", "HubBodyHaloRadiusKm", "HubSkyInScatter"
    };

    private static readonly string[] BeamFieldNames =
    {
        "BeamsEnabled", "BeamFarLayerEnabled", "BeamDepthWriteCore",
        "BeamCoreWidthMeters", "BeamHaloWidthMeters", "BeamMainCoreWidthMeters", "BeamMainHaloWidthMeters", "BeamMinPixelWidth", "BeamFarCompensation",
        "BeamBaseEmission", "BeamMainEmissionScale", "BeamHaloAmplitude", "BeamBaseFadeMeters", "BeamOrigin", "BeamCloseSoftenMeters", "BeamDayVisibility", "BeamCoreDepthThreshold", "BeamCoreDepthMinPixels", "BeamSoftTipFraction", "BeamGridEventPeak",
        "BeamCloudDiffusionEnabled", "BeamCloudWidening", "BeamCloudHaloBoost", "BeamCloudCoreCut", "BeamCrownGlowEnabled", "BeamCrownGlowEmission", "BeamBoreLightBoost",
        "BeamOrbEaseExponent", "BeamOrbRadiusMeters", "BeamOrbHaloMeters", "BeamOrbMinPixelRadius", "BeamOrbEmission",
        "BeamCloudLightEnabled", "BeamCloudLightIntensity", "BeamCloudLightRadiusMeters", "BeamCloudLightMaxSegments", "GridCloudLightEnabled"
    };

    private static bool IsDefault(GameplayEclipseEventSettings s)
    {
        if (!s.EventEnabled || !s.EclipseDarknessEnabled || !s.BeamsEnabled || !s.BeamFarLayerEnabled || !s.BeamDepthWriteCore || !s.HubBodyEnabled || !s.BeamCloudDiffusionEnabled || !s.BeamCrownGlowEnabled || !s.BeamCloudLightEnabled || !s.GridCloudLightEnabled || s.IgnitionSpeedMode != 0 || s.RampMode != 0 || !s.SuppressGridPulseDuringIgnition || s.RefuelClock != 0 || s.BeamOrigin != 1) return false;
        foreach (string name in FloatNames)
            if (!Mathf.IsEqualApprox((float)s.Get(name).AsDouble(), GameplayEclipseEventDefaults.DefaultOf(name))) return false;
        return true;
    }

    /// <summary>Spire to hub beam preferences (phase E4b): round trip, clamps, NaN fallback, core mapping, and the defaults an older save without the fields loads with.</summary>
    private static void BeamChecks(string path)
    {
        var beams = new GameplayEclipseEventDefaults(path); beams.Load();
        Require(beams.SetBool("BeamsEnabled", false) && beams.SetBool("BeamFarLayerEnabled", false) && beams.SetBool("BeamDepthWriteCore", false)
            && beams.SetFloat("BeamCoreWidthMeters", 200f) && beams.SetFloat("BeamFarCompensation", .3f) && beams.SetFloat("BeamBaseEmission", 20f)
            && beams.SetFloat("BeamDayVisibility", .25f) && beams.SetFloat("BeamCoreDepthMinPixels", 6f), "beam setters did not report a change");
        Require(beams.Save(), "beam preferences did not save");
        var loaded = new GameplayEclipseEventDefaults(path); loaded.Load();
        EclipseBeamSettings core = GameplayEclipseEventDefaults.ToCore(loaded.Settings).Beams;
        Require(!loaded.Settings.BeamsEnabled && !loaded.Settings.BeamFarLayerEnabled && !loaded.Settings.BeamDepthWriteCore
            && Mathf.IsEqualApprox(loaded.Settings.BeamCoreWidthMeters, 200f) && Mathf.IsEqualApprox(loaded.Settings.BeamBaseEmission, 20f), "beam preferences did not round-trip");
        Require(!core.BeamsEnabled && !core.FarLayerEnabled && !core.DepthWriteCore && Math.Abs(core.CoreWidthMeters - 200d) < 1e-4 && Math.Abs(core.FarCompensation - .3d) < 1e-6
            && Math.Abs(core.BaseEmission - 20d) < 1e-6 && Math.Abs(core.DayVisibility - .25d) < 1e-6 && Math.Abs(core.CoreDepthMinPixels - 6d) < 1e-6, "beam preferences did not map into the core settings");

        // Clamps: setters and a hostile file.
        beams.SetFloat("BeamCoreWidthMeters", 0f); beams.SetFloat("BeamHaloWidthMeters", 1e9f); beams.SetFloat("BeamFarCompensation", 7f);
        beams.SetFloat("BeamBaseEmission", float.NaN); beams.SetFloat("BeamSoftTipFraction", -1f); beams.SetFloat("BeamCoreDepthThreshold", float.PositiveInfinity);
        var s = beams.Settings;
        Require(Mathf.IsEqualApprox(s.BeamCoreWidthMeters, 10f) && Mathf.IsEqualApprox(s.BeamHaloWidthMeters, 20000f) && Mathf.IsEqualApprox(s.BeamFarCompensation, 1f)
            && Mathf.IsEqualApprox(s.BeamBaseEmission, 12f) && Mathf.IsEqualApprox(s.BeamSoftTipFraction, .0005f) && Mathf.IsEqualApprox(s.BeamCoreDepthThreshold, .35f), "beam setter bounds were not enforced");
        using var hostile = new GameplayEclipseEventSettings { BeamMainEmissionScale = float.NaN, BeamMinPixelWidth = -3f, BeamMainHaloWidthMeters = 1e12f, BeamHaloAmplitude = 5f };
        using var clean = GameplayEclipseEventDefaults.Sanitize(hostile);
        Require(Mathf.IsEqualApprox(clean.BeamMainEmissionScale, 1.5f) && Mathf.IsEqualApprox(clean.BeamMinPixelWidth, .5f)
            && Mathf.IsEqualApprox(clean.BeamMainHaloWidthMeters, 40000f) && Mathf.IsEqualApprox(clean.BeamHaloAmplitude, 1f), "beam Sanitize bounds were not enforced");
        // The pure record clamps too (a value that bypassed the resource).
        EclipseBeamSettings wild = new EclipseBeamSettings { CoreWidthMeters = double.NaN, MinPixelWidth = 1e6, DayVisibility = -2d, FarCompensation = double.PositiveInfinity }.Sanitized();
        Require(wild.CoreWidthMeters == 150d && wild.MinPixelWidth == 8d && wild.DayVisibility == 0d && wild.FarCompensation == .45d, "EclipseBeamSettings.Sanitized bounds");

        // Tweaks T2: the beam origin (an int enum, default Base) and the close-range softening.
        Require(new GameplayEclipseEventSettings().BeamOrigin == 1 && GameplayEclipseEventDefaults.ToCore(new GameplayEclipseEventSettings()).Beams.BeamOrigin == EmitterBeamOrigin.Base
            && Math.Abs(GameplayEclipseEventDefaults.ToCore(new GameplayEclipseEventSettings()).Beams.CloseSoftenMeters - 1000d) < 1e-6, "the default beam origin is not Base / softening not 1,000 m");
        var origin = new GameplayEclipseEventDefaults(path); origin.Load();
        Require(origin.SetInt("BeamOrigin", 2) && origin.SetFloat("BeamCloseSoftenMeters", 120f) && !origin.SetInt("BeamOrigin", 2), "beam origin setters did not report a change");
        Require(origin.Save(), "beam origin preferences did not save");
        var originLoaded = new GameplayEclipseEventDefaults(path); originLoaded.Load();
        EclipseBeamSettings originCore = GameplayEclipseEventDefaults.ToCore(originLoaded.Settings).Beams;
        Require(originLoaded.Settings.BeamOrigin == 2 && Mathf.IsEqualApprox(originLoaded.Settings.BeamCloseSoftenMeters, 120f)
            && originCore.BeamOrigin == EmitterBeamOrigin.Reactor && Math.Abs(originCore.CloseSoftenMeters - 120d) < 1e-6, "beam origin did not round-trip or map");
        origin.SetInt("BeamOrigin", 9); origin.SetFloat("BeamCloseSoftenMeters", 1e9f);
        Require(origin.Settings.BeamOrigin == 2 && Mathf.IsEqualApprox(origin.Settings.BeamCloseSoftenMeters, 5000f), "beam origin setter bounds were not enforced");
        origin.SetInt("BeamOrigin", -4);
        Require(origin.Settings.BeamOrigin == 0, "beam origin lower bound");
        using var hostileOrigin = new GameplayEclipseEventSettings { BeamOrigin = 77, BeamCloseSoftenMeters = float.NaN };
        using var cleanOrigin = GameplayEclipseEventDefaults.Sanitize(hostileOrigin);
        Require(cleanOrigin.BeamOrigin == 2 && Mathf.IsEqualApprox(cleanOrigin.BeamCloseSoftenMeters, 1000f), "beam origin Sanitize bounds were not enforced");
        EclipseBeamSettings originWild = new EclipseBeamSettings { BeamOrigin = (EmitterBeamOrigin)200, CloseSoftenMeters = -5d }.Sanitized();
        Require(originWild.BeamOrigin == EmitterBeamOrigin.Base && originWild.CloseSoftenMeters == 0d, "EclipseBeamSettings.Sanitized origin bounds");
        origin.Reset(); origin.Save();

        // Tweaks T3: the Sustain refuel orbs (travel time in the timeline group, look in the beam group).
        var orb = new GameplayEclipseEventDefaults(path); orb.Load();
        Require(Mathf.IsEqualApprox(orb.Settings.OrbTravelSeconds, 30f) && Mathf.IsEqualApprox(orb.Settings.BeamOrbEaseExponent, 3f) && Mathf.IsEqualApprox(orb.Settings.BeamOrbRadiusMeters, 350f)
            && Mathf.IsEqualApprox(orb.Settings.BeamOrbHaloMeters, 1500f) && Mathf.IsEqualApprox(orb.Settings.BeamOrbMinPixelRadius, 2.5f) && Mathf.IsEqualApprox(orb.Settings.BeamOrbEmission, 16f), "orb defaults");
        Require(orb.SetFloat("OrbTravelSeconds", 45f) && orb.SetFloat("BeamOrbEaseExponent", 2f) && orb.SetFloat("BeamOrbRadiusMeters", 500f) && orb.SetFloat("BeamOrbHaloMeters", 2000f)
            && orb.SetFloat("BeamOrbMinPixelRadius", 4f) && orb.SetFloat("BeamOrbEmission", 30f), "orb setters did not report a change");
        Require(orb.Save(), "orb preferences did not save");
        var orbLoaded = new GameplayEclipseEventDefaults(path); orbLoaded.Load();
        EclipseEventSettings orbCore = GameplayEclipseEventDefaults.ToCore(orbLoaded.Settings);
        Require(Mathf.IsEqualApprox(orbLoaded.Settings.OrbTravelSeconds, 45f) && Math.Abs(orbCore.OrbTravelSeconds - 45d) < 1e-6 && Math.Abs(orbCore.Beams.OrbEaseExponent - 2d) < 1e-6
            && Math.Abs(orbCore.Beams.OrbRadiusMeters - 500d) < 1e-6 && Math.Abs(orbCore.Beams.OrbHaloMeters - 2000d) < 1e-6 && Math.Abs(orbCore.Beams.OrbMinPixelRadius - 4d) < 1e-6
            && Math.Abs(orbCore.Beams.OrbEmission - 30d) < 1e-6, "orb preferences did not round-trip or map");
        orb.SetFloat("OrbTravelSeconds", 999f); orb.SetFloat("BeamOrbEaseExponent", 0f); orb.SetFloat("BeamOrbEmission", float.NaN);
        Require(Mathf.IsEqualApprox(orb.Settings.OrbTravelSeconds, 60f) && Mathf.IsEqualApprox(orb.Settings.BeamOrbEaseExponent, 1f) && Mathf.IsEqualApprox(orb.Settings.BeamOrbEmission, 16f), "orb setter bounds were not enforced");
        EclipseEventSettings orbWild = new EclipseEventSettings { OrbTravelSeconds = 1d, Beams = new EclipseBeamSettings { OrbEaseExponent = 9d, OrbRadiusMeters = -1d, OrbHaloMeters = double.NaN, OrbMinPixelRadius = 99d, OrbEmission = 1e6 } }.Sanitized();
        Require(orbWild.OrbTravelSeconds == 5d && orbWild.Beams.OrbEaseExponent == 4d && orbWild.Beams.OrbRadiusMeters == 20d && orbWild.Beams.OrbHaloMeters == 1500d
            && orbWild.Beams.OrbMinPixelRadius == 12d && orbWild.Beams.OrbEmission == 100d, "EclipseEventSettings.Sanitized orb bounds");
        orb.Reset(); orb.Save();

        // Tweaks T4/T5: the beam cloud light (segment lights in the Sunshine raymarch): enable, intensity, radius, max segments, grid links.
        var light = new GameplayEclipseEventDefaults(path); light.Load();
        EclipseBeamSettings lightDefault = GameplayEclipseEventDefaults.ToCore(light.Settings).Beams;
        Require(light.Settings.BeamCloudLightEnabled && light.Settings.GridCloudLightEnabled && Mathf.IsEqualApprox(light.Settings.BeamCloudLightIntensity, 5f) && Mathf.IsEqualApprox(light.Settings.BeamCloudLightRadiusMeters, 4000f)
            && Mathf.IsEqualApprox(light.Settings.BeamCloudLightMaxSegments, 4f) && lightDefault.CloudLightEnabled && lightDefault.CloudLightGridLinks && lightDefault.CloudLightIntensity == 5d && lightDefault.CloudLightRadiusMeters == 4000d
            && lightDefault.CloudLightMaxSegments == 4, "cloud light defaults");
        Require(light.SetBool("BeamCloudLightEnabled", false) && light.SetBool("GridCloudLightEnabled", false) && light.SetFloat("BeamCloudLightIntensity", 9.5f) && light.SetFloat("BeamCloudLightRadiusMeters", 2500f)
            && light.SetFloat("BeamCloudLightMaxSegments", 3f) && !light.SetBool("GridCloudLightEnabled", false), "cloud light setters did not report a change");
        Require(light.Save(), "cloud light preferences did not save");
        var lightLoaded = new GameplayEclipseEventDefaults(path); lightLoaded.Load();
        EclipseBeamSettings lightCore = GameplayEclipseEventDefaults.ToCore(lightLoaded.Settings).Beams;
        Require(!lightLoaded.Settings.BeamCloudLightEnabled && !lightLoaded.Settings.GridCloudLightEnabled && Mathf.IsEqualApprox(lightLoaded.Settings.BeamCloudLightIntensity, 9.5f) && Mathf.IsEqualApprox(lightLoaded.Settings.BeamCloudLightMaxSegments, 3f)
            && !lightCore.CloudLightEnabled && !lightCore.CloudLightGridLinks && lightCore.CloudLightIntensity == 9.5d && lightCore.CloudLightRadiusMeters == 2500d && lightCore.CloudLightMaxSegments == 3, "cloud light preferences did not round-trip or map");
        light.SetFloat("BeamCloudLightIntensity", 1e9f); light.SetFloat("BeamCloudLightRadiusMeters", 1f); light.SetFloat("BeamCloudLightMaxSegments", 99f);
        Require(Mathf.IsEqualApprox(light.Settings.BeamCloudLightIntensity, 20f) && Mathf.IsEqualApprox(light.Settings.BeamCloudLightRadiusMeters, 500f) && Mathf.IsEqualApprox(light.Settings.BeamCloudLightMaxSegments, 8f), "cloud light setter bounds were not enforced");
        light.SetFloat("BeamCloudLightMaxSegments", 0.2f); light.SetFloat("BeamCloudLightIntensity", float.NaN);
        Require(Mathf.IsEqualApprox(light.Settings.BeamCloudLightMaxSegments, 1f) && Mathf.IsEqualApprox(light.Settings.BeamCloudLightIntensity, 5f), "cloud light lower bound / NaN fallback");
        using var hostileLight = new GameplayEclipseEventSettings { BeamCloudLightRadiusMeters = float.PositiveInfinity, BeamCloudLightMaxSegments = -4f, BeamCloudLightIntensity = -2f };
        using var cleanLight = GameplayEclipseEventDefaults.Sanitize(hostileLight);
        Require(Mathf.IsEqualApprox(cleanLight.BeamCloudLightRadiusMeters, 4000f) && Mathf.IsEqualApprox(cleanLight.BeamCloudLightMaxSegments, 1f) && Mathf.IsEqualApprox(cleanLight.BeamCloudLightIntensity, 0f), "cloud light Sanitize bounds were not enforced");
        EclipseBeamSettings lightWild = new EclipseBeamSettings { CloudLightIntensity = double.NaN, CloudLightRadiusMeters = 1e9, CloudLightMaxSegments = 77 }.Sanitized();
        Require(lightWild.CloudLightIntensity == 5d && lightWild.CloudLightRadiusMeters == 20000d && lightWild.CloudLightMaxSegments == 8 && new EclipseBeamSettings { CloudLightMaxSegments = -3 }.Sanitized().CloudLightMaxSegments == 1, "EclipseBeamSettings.Sanitized cloud light bounds");
        light.Reset(); light.Save();

        // Phase E4e: in-cloud diffusion, crown glow and bore light boost.
        var cloud = new GameplayEclipseEventDefaults(path); cloud.Load();
        Require(cloud.SetBool("BeamCloudDiffusionEnabled", false) && cloud.SetBool("BeamCrownGlowEnabled", false) && cloud.SetFloat("BeamCloudWidening", 5f)
            && cloud.SetFloat("BeamCloudHaloBoost", 2f) && cloud.SetFloat("BeamCloudCoreCut", .8f) && cloud.SetFloat("BeamCrownGlowEmission", 6f) && cloud.SetFloat("BeamBoreLightBoost", 2.5f), "cloud setters did not report a change");
        Require(cloud.Save(), "cloud preferences did not save");
        var cloudLoaded = new GameplayEclipseEventDefaults(path); cloudLoaded.Load();
        EclipseBeamSettings cloudCore = GameplayEclipseEventDefaults.ToCore(cloudLoaded.Settings).Beams;
        Require(!cloudLoaded.Settings.BeamCloudDiffusionEnabled && !cloudLoaded.Settings.BeamCrownGlowEnabled && Mathf.IsEqualApprox(cloudLoaded.Settings.BeamCloudWidening, 5f)
            && !cloudCore.CloudDiffusionEnabled && !cloudCore.CrownGlowEnabled && Math.Abs(cloudCore.CloudWidening - 5d) < 1e-6 && Math.Abs(cloudCore.CloudHaloBoost - 2d) < 1e-6
            && Math.Abs(cloudCore.CloudCoreCut - .8d) < 1e-6 && Math.Abs(cloudCore.CrownGlowEmission - 6d) < 1e-6 && Math.Abs(cloudCore.BoreLightBoost - 2.5d) < 1e-6, "cloud preferences did not round-trip or map");
        cloud.SetFloat("BeamCloudWidening", 99f); cloud.SetFloat("BeamBoreLightBoost", 0f); cloud.SetFloat("BeamCloudCoreCut", float.NaN);
        Require(Mathf.IsEqualApprox(cloud.Settings.BeamCloudWidening, 8f) && Mathf.IsEqualApprox(cloud.Settings.BeamBoreLightBoost, 1f) && Mathf.IsEqualApprox(cloud.Settings.BeamCloudCoreCut, 1.5f), "cloud setter bounds were not enforced");
        EclipseBeamSettings cloudWild = new EclipseBeamSettings { CloudWidening = double.NaN, BoreLightBoost = 100d, CloudCoreCut = -1d, CrownGlowEmission = 1e9 }.Sanitized();
        Require(cloudWild.CloudWidening == 3d && cloudWild.BoreLightBoost == 8d && cloudWild.CloudCoreCut == .05d && cloudWild.CrownGlowEmission == 20d, "EclipseBeamSettings.Sanitized cloud bounds");
        cloud.Reset(); cloud.Save();

        // An older save has none of the beam fields: delete their lines from a saved file and the load must give the defaults.
        var old = new GameplayEclipseEventDefaults(path); old.Load();
        old.SetFloat("PreRollSeconds", 111f); old.SetFloat("BeamBaseEmission", 33f); old.SetBool("BeamsEnabled", false);
        Require(old.Save(), "old-save fixture did not save");
        string full = ProjectSettings.GlobalizePath(path);
        string[] lines = System.IO.File.ReadAllLines(full);
        var kept = new System.Collections.Generic.List<string>();
        foreach (string line in lines)
        {
            bool beamLine = false;
            foreach (string name in BeamFieldNames) if (line.StartsWith(name + " =", StringComparison.Ordinal)) beamLine = true;
            if (!beamLine) kept.Add(line);
        }
        Require(kept.Count < lines.Length, "old-save fixture had no beam lines to remove");
        System.IO.File.WriteAllLines(full, kept);
        var legacy = new GameplayEclipseEventDefaults(path); legacy.Load();
        Require(Mathf.IsEqualApprox(legacy.Settings.PreRollSeconds, 111f) && legacy.Settings.BeamsEnabled && legacy.Settings.BeamFarLayerEnabled && legacy.Settings.BeamDepthWriteCore
            && legacy.Settings.BeamOrigin == 1 && Mathf.IsEqualApprox(legacy.Settings.BeamOrbEmission, 16f) && Mathf.IsEqualApprox(legacy.Settings.BeamOrbEaseExponent, 3f) && Mathf.IsEqualApprox(legacy.Settings.BeamCloseSoftenMeters, 1000f) && Mathf.IsEqualApprox(legacy.Settings.BeamBaseEmission, 12f) && legacy.Settings.BeamCloudLightEnabled && legacy.Settings.GridCloudLightEnabled && Mathf.IsEqualApprox(legacy.Settings.BeamCloudLightIntensity, 5f) && Mathf.IsEqualApprox(legacy.Settings.BeamCloudLightMaxSegments, 4f) && Mathf.IsEqualApprox(legacy.Settings.BeamCoreWidthMeters, 150f) && Mathf.IsEqualApprox(legacy.Settings.BeamDayVisibility, .6f), "an older save did not load the beam defaults");
        old.Reset(); old.Save();
    }

    /// <summary>Hub sun body and sky in-scatter preferences (phase E4c): round trip, clamps, NaN fallback, core mapping and the defaults an older save without the fields loads with.</summary>
    private static void HubBodyChecks(string path)
    {
        var hub = new GameplayEclipseEventDefaults(path); hub.Load();
        Require(hub.SetBool("HubBodyEnabled", false) && hub.SetFloat("HubBodyCoreEmission", 25f) && hub.SetFloat("HubBodyRadiusKm", 45f) && hub.SetFloat("HubBodyMinRadiusPixels", 3f)
            && hub.SetFloat("HubBodyHaloAmplitude", 1.25f) && hub.SetFloat("HubBodyHaloRadiusKm", 650f) && hub.SetFloat("HubSkyInScatter", 2.5f), "hub body setters did not report a change");
        Require(hub.Save(), "hub body preferences did not save");
        var loaded = new GameplayEclipseEventDefaults(path); loaded.Load();
        EclipseHubBodySettings core = GameplayEclipseEventDefaults.ToCore(loaded.Settings).HubBody;
        Require(!loaded.Settings.HubBodyEnabled && Mathf.IsEqualApprox(loaded.Settings.HubBodyRadiusKm, 45f) && Mathf.IsEqualApprox(loaded.Settings.HubSkyInScatter, 2.5f), "hub body preferences did not round-trip");
        Require(!core.BodyEnabled && Math.Abs(core.CoreEmission - 25d) < 1e-5 && Math.Abs(core.RadiusKm - 45d) < 1e-5 && Math.Abs(core.MinRadiusPixels - 3d) < 1e-6
            && Math.Abs(core.HaloAmplitude - 1.25d) < 1e-6 && Math.Abs(core.HaloRadiusKm - 650d) < 1e-4 && Math.Abs(core.SkyInScatter - 2.5d) < 1e-6, "hub body preferences did not map into the core settings");

        hub.SetFloat("HubBodyCoreEmission", 0f); hub.SetFloat("HubBodyRadiusKm", 1e9f); hub.SetFloat("HubBodyHaloAmplitude", float.NaN); hub.SetFloat("HubSkyInScatter", -3f);
        var s = hub.Settings;
        Require(Mathf.IsEqualApprox(s.HubBodyCoreEmission, 1f) && Mathf.IsEqualApprox(s.HubBodyRadiusKm, 200f) && Mathf.IsEqualApprox(s.HubBodyHaloAmplitude, .6f) && Mathf.IsEqualApprox(s.HubSkyInScatter, 0f), "hub body setter bounds were not enforced");
        using var hostile = new GameplayEclipseEventSettings { HubBodyHaloRadiusKm = float.PositiveInfinity, HubBodyMinRadiusPixels = -4f, HubBodyCoreEmission = 1e12f };
        using var clean = GameplayEclipseEventDefaults.Sanitize(hostile);
        Require(Mathf.IsEqualApprox(clean.HubBodyHaloRadiusKm, 400f) && Mathf.IsEqualApprox(clean.HubBodyMinRadiusPixels, .5f) && Mathf.IsEqualApprox(clean.HubBodyCoreEmission, 200f), "hub body Sanitize bounds were not enforced");
        EclipseHubBodySettings wild = new EclipseHubBodySettings { CoreEmission = double.NaN, RadiusKm = 1e6, SkyInScatter = double.NegativeInfinity, MinRadiusPixels = 0d }.Sanitized();
        Require(wild.CoreEmission == 40d && wild.RadiusKm == 200d && wild.SkyInScatter == 1d && wild.MinRadiusPixels == .5d, "EclipseHubBodySettings.Sanitized bounds");

        // The pure profile: radius factor 0.5 + 0.5 sqrt(min(L, 1)), emission proportional to the level.
        Require(Math.Abs(EclipseHubBodyMath.RadiusFactor(0d) - .5d) < 1e-12 && Math.Abs(EclipseHubBodyMath.RadiusFactor(1d) - 1d) < 1e-12 && Math.Abs(EclipseHubBodyMath.RadiusFactor(2.5d) - 1d) < 1e-12
            && Math.Abs(EclipseHubBodyMath.RadiusFactor(.1d) - (.5d + .5d * Math.Sqrt(.1d))) < 1e-12, "hub body radius factor");
        var def = new EclipseHubBodySettings();
        Require(Math.Abs(EclipseHubBodyMath.CoreEmission(def, .1d) - 4d) < 1e-9 && Math.Abs(EclipseHubBodyMath.HaloEmission(def, 2d) - 1.2d) < 1e-9 && Math.Abs(EclipseHubBodyMath.CoreRadiusKm(def, 1d) - 30d) < 1e-9, "hub body emission / radius");

        // The engine takes a hub body change (it is excluded from the timeline rebuild, like the beam look).
        var engine = new EclipseEventEngine();
        engine.SetSettings(new EclipseEventSettings());
        engine.SetSettings(new EclipseEventSettings { HubBody = def with { CoreEmission = 60d } });
        Require(Math.Abs(engine.Settings.HubBody.CoreEmission - 60d) < 1e-9, "engine did not take the hub body settings");

        // An older save has none of the hub body fields: delete their lines and the load must give the defaults.
        var old = new GameplayEclipseEventDefaults(path); old.Load();
        old.SetFloat("PreRollSeconds", 123f); old.SetFloat("HubBodyRadiusKm", 77f); old.SetBool("HubBodyEnabled", false);
        Require(old.Save(), "hub body old-save fixture did not save");
        string full = ProjectSettings.GlobalizePath(path);
        string[] lines = System.IO.File.ReadAllLines(full);
        var kept = new System.Collections.Generic.List<string>();
        foreach (string line in lines)
        {
            bool hubLine = false;
            foreach (string name in HubBodyFieldNames) if (line.StartsWith(name + " =", StringComparison.Ordinal)) hubLine = true;
            if (!hubLine) kept.Add(line);
        }
        Require(kept.Count < lines.Length, "hub body old-save fixture had no lines to remove");
        System.IO.File.WriteAllLines(full, kept);
        var legacy = new GameplayEclipseEventDefaults(path); legacy.Load();
        Require(Mathf.IsEqualApprox(legacy.Settings.PreRollSeconds, 123f) && legacy.Settings.HubBodyEnabled && Mathf.IsEqualApprox(legacy.Settings.HubBodyRadiusKm, 30f)
            && Mathf.IsEqualApprox(legacy.Settings.HubBodyCoreEmission, 40f) && Mathf.IsEqualApprox(legacy.Settings.HubSkyInScatter, 1f), "an older save did not load the hub body defaults");
        old.Reset(); old.Save();
    }

    private static void Run(string path)
    {
        // Every settings field maps onto the pure record with the E0 defaults (no drift between the resource and the core).
        using (var fresh = new GameplayEclipseEventSettings())
        {
            Require(IsDefault(fresh), "fresh resource is not at the declared defaults");
            EclipseEventSettings core = GameplayEclipseEventDefaults.ToCore(fresh), reference = new EclipseEventSettings().Sanitized();
            foreach (var property in typeof(EclipseEventSettings).GetProperties())
            {
                object? mapped = property.GetValue(core.Sanitized()), expected = property.GetValue(reference);
                if (mapped is EclipseDarknessSettings md && expected is EclipseDarknessSettings ed)
                    Require(md.Enabled == ed.Enabled && Math.Abs(md.AmbientScale - ed.AmbientScale) < 1e-5 && Math.Abs(md.AtmosphereNightScale - ed.AtmosphereNightScale) < 1e-5 && Math.Abs(md.SkyNightScale - ed.SkyNightScale) < 1e-5
                        && Math.Abs(md.CloudAmbientScale - ed.CloudAmbientScale) < 1e-5 && Math.Abs(md.StarVisibility - ed.StarVisibility) < 1e-5, "core default drift in Darkness");
                else if (mapped is EclipseBeamSettings mb && expected is EclipseBeamSettings eb)
                {
                    foreach (var beamProperty in typeof(EclipseBeamSettings).GetProperties())
                    {
                        object? bm = beamProperty.GetValue(mb), be = beamProperty.GetValue(eb);
                        if (bm is double x && be is double y) Require(Math.Abs(x - y) <= 1e-5 * Math.Max(1d, Math.Abs(y)), "core default drift in Beams." + beamProperty.Name);
                        else Require(Equals(bm, be), "core default drift in Beams." + beamProperty.Name);
                    }
                }
                else if (mapped is EclipseHubBodySettings mh && expected is EclipseHubBodySettings eh)
                {
                    foreach (var hubProperty in typeof(EclipseHubBodySettings).GetProperties())
                    {
                        object? hm = hubProperty.GetValue(mh), he = hubProperty.GetValue(eh);
                        if (hm is double x && he is double y) Require(Math.Abs(x - y) <= 1e-5 * Math.Max(1d, Math.Abs(y)), "core default drift in HubBody." + hubProperty.Name);
                        else Require(Equals(hm, he), "core default drift in HubBody." + hubProperty.Name);
                    }
                }
                else if (mapped is double a && expected is double b) Require(Math.Abs(a - b) <= 1e-5 * Math.Max(1d, Math.Abs(b)), "core default drift in " + property.Name);
                else Require(Equals(mapped, expected), "core default drift in " + property.Name);
            }
        }

        var first = new GameplayEclipseEventDefaults(path); first.Load();
        Require(IsDefault(first.Settings) && !first.HasSavedFile, "missing eclipse preferences did not use defaults");
        Require(first.Save(), "clean defaults should save as a no-op");
        Require(first.SetEventEnabled(false) && !first.SetEventEnabled(false), "Set did not report the changed flag");
        first.SetSuppressGridPulseDuringIgnition(false); first.SetIgnitionSpeedMode(1); first.SetRampMode(1); first.SetRefuelClock(1);
        first.SetPreRollSeconds(120); first.SetParallaxSafetySeconds(30); first.SetDarknessBeatSeconds(45); first.SetKindleLevel(.25f); first.SetCollapseLeadSeconds(200);
        first.SetFloat("HubDirectFraction", 1.1f); first.SetFloat("ShockFlashLevel", 1.5f); first.SetFloat("IgnitionSeconds", 300);
        first.SetFloat("HubLightRed", .5f); first.SetFloat("HubLightBlue", .75f); first.SetFloat("HubSkyTintStrength", .6f); first.SetFloat("HubZenithHaloIntensity", 1.25f);
        Require(first.Dirty && first.Save() && !first.Dirty, "eclipse preferences did not save");
        var second = new GameplayEclipseEventDefaults(path); second.Load();
        var s = second.Settings;
        Require(!s.EventEnabled && !s.SuppressGridPulseDuringIgnition && s.IgnitionSpeedMode == 1 && s.RampMode == 1 && s.RefuelClock == 1, "flag/enum preferences did not round-trip");
        Require(Mathf.IsEqualApprox(s.PreRollSeconds, 120) && Mathf.IsEqualApprox(s.ParallaxSafetySeconds, 30) && Mathf.IsEqualApprox(s.DarknessBeatSeconds, 45)
            && Mathf.IsEqualApprox(s.KindleLevel, .25f) && Mathf.IsEqualApprox(s.CollapseLeadSeconds, 200) && Mathf.IsEqualApprox(s.HubDirectFraction, 1.1f)
            && Mathf.IsEqualApprox(s.ShockFlashLevel, 1.5f) && Mathf.IsEqualApprox(s.IgnitionSeconds, 300), "numeric preferences did not round-trip");
        Require(Mathf.IsEqualApprox(s.HubLightRed, .5f) && Mathf.IsEqualApprox(s.HubLightGreen, .97f) && Mathf.IsEqualApprox(s.HubLightBlue, .75f)
            && Mathf.IsEqualApprox(s.HubSkyTintStrength, .6f) && Mathf.IsEqualApprox(s.HubZenithHaloIntensity, 1.25f), "hub lighting preferences did not round-trip");
        EclipseEventSettings mapped2 = GameplayEclipseEventDefaults.ToCore(s);
        Require(Math.Abs(mapped2.HubLightRed - .5d) < 1e-6 && Math.Abs(mapped2.HubLightBlue - .75d) < 1e-6 && Math.Abs(mapped2.HubSkyTintStrength - .6d) < 1e-6
            && Math.Abs(mapped2.HubZenithHaloIntensity - 1.25d) < 1e-6, "hub lighting ToCore mapping");
        Require(!mapped2.EventEnabled && mapped2.IgnitionSpeedMode == EclipseIgnitionSpeedMode.Manual && mapped2.RampMode == EclipseRampMode.Perceptual
            && mapped2.RefuelClock == EclipseRefuelClock.Weather && Math.Abs(mapped2.PreRollSeconds - 120d) < 1e-6, "ToCore mapping");

        // Setter clamps and NaN fallback.
        second.SetPreRollSeconds(1e9f); second.SetParallaxSafetySeconds(-5); second.SetDarknessBeatSeconds(float.NaN); second.SetKindleLevel(float.PositiveInfinity);
        second.SetFloat("MainClimbSeconds", 0); second.SetFloat("OverloadGain", 99); second.SetFloat("FlickerHzEnd", float.NegativeInfinity); second.SetFloat("HubDayAtFull", 2); second.SetFloat("HubLightGreen", 3); second.SetFloat("HubZenithHaloIntensity", float.NaN); second.SetFloat("HubSkyTintStrength", -1);
        second.SetIgnitionSpeedMode(9); second.SetRampMode(-4); second.SetRefuelClock(5);
        s = second.Settings;
        var (pMin, pMax) = GameplayEclipseEventDefaults.RangeOf("PreRollSeconds");
        Require(Mathf.IsEqualApprox(s.PreRollSeconds, pMax) && pMin == 0 && Mathf.IsEqualApprox(pMax, 900), "pre-roll upper bound not enforced");
        Require(Mathf.IsEqualApprox(s.ParallaxSafetySeconds, 0), "margin lower bound not enforced");
        Require(Mathf.IsEqualApprox(s.DarknessBeatSeconds, 20), "NaN did not fall back to the default");
        Require(Mathf.IsEqualApprox(s.KindleLevel, .10f), "infinity did not fall back to the default");
        Require(Mathf.IsEqualApprox(s.MainClimbSeconds, .5f) && Mathf.IsEqualApprox(s.OverloadGain, 5) && Mathf.IsEqualApprox(s.FlickerHzEnd, 6)
            && Mathf.IsEqualApprox(s.HubDayAtFull, 1), "setter bounds were not enforced");
        Require(Mathf.IsEqualApprox(s.HubLightGreen, 1) && Mathf.IsEqualApprox(s.HubZenithHaloIntensity, .15f) && Mathf.IsEqualApprox(s.HubSkyTintStrength, 0), "hub lighting setter bounds were not enforced");
        Require(s.IgnitionSpeedMode == 1 && s.RampMode == 0 && s.RefuelClock == 1, "enum bounds were not enforced");

        using var dirty = new GameplayEclipseEventSettings { PreRollSeconds = 99999, KindleLevel = float.NaN, ShockFlashLevel = -3, HubDirectFraction = float.PositiveInfinity,
            RealSunEnergy = float.NaN, IgnitionSeconds = 1, FlickerMax = 5, IgnitionSpeedMode = 7, RampMode = -2, RefuelClock = 3, EventEnabled = false, SuppressGridPulseDuringIgnition = false };
        using var clean = GameplayEclipseEventDefaults.Sanitize(dirty);
        Require(Mathf.IsEqualApprox(clean.PreRollSeconds, 900) && Mathf.IsEqualApprox(clean.KindleLevel, .10f) && Mathf.IsEqualApprox(clean.ShockFlashLevel, 0)
            && Mathf.IsEqualApprox(clean.HubDirectFraction, .8f) && Mathf.IsEqualApprox(clean.RealSunEnergy, 4) && Mathf.IsEqualApprox(clean.IgnitionSeconds, 30)
            && Mathf.IsEqualApprox(clean.FlickerMax, .9f) && clean.IgnitionSpeedMode == 1 && clean.RampMode == 0 && clean.RefuelClock == 1
            && !clean.EventEnabled && !clean.SuppressGridPulseDuringIgnition, "Sanitize bounds were not enforced");

        // A hand-edited file on disk is sanitised when loaded.
        var hostile = new GameplayEclipseEventSettings { PreRollSeconds = float.NaN, CollapseLeadSeconds = 1e9f, RefuelClock = 44 };
        Require(ResourceSaver.Save(hostile, path) == Error.Ok, "hostile fixture write failed");
        var reloaded = new GameplayEclipseEventDefaults(path); reloaded.Load();
        Require(Mathf.IsEqualApprox(reloaded.Settings.PreRollSeconds, 300) && Mathf.IsEqualApprox(reloaded.Settings.CollapseLeadSeconds, 600) && reloaded.Settings.RefuelClock == 1, "load did not sanitize the file");


        // Pitch-black eclipse darkness (phase E2): round trip, clamps (cloud floor 0.3), core mapping.
        var dark = new GameplayEclipseEventDefaults(path); dark.Load();
        Require(dark.SetBool("EclipseDarknessEnabled", false) && dark.SetFloat("EclipseAmbientScale", .05f) && dark.SetFloat("EclipseCloudAmbientScale", .0f)
            && dark.SetFloat("EclipseStarVisibility", .5f) && dark.SetFloat("EclipseSkyNightScale", 7f), "darkness setters did not report a change");
        Require(Mathf.IsEqualApprox(dark.Settings.EclipseCloudAmbientScale, .3f) && Mathf.IsEqualApprox(dark.Settings.EclipseSkyNightScale, 1f), "darkness setter bounds (cloud floor 0.3) not enforced");
        Require(dark.Save(), "darkness preferences did not save");
        var darkLoaded = new GameplayEclipseEventDefaults(path); darkLoaded.Load();
        EclipseDarknessSettings darkCore = GameplayEclipseEventDefaults.ToCore(darkLoaded.Settings).Darkness;
        Require(!darkLoaded.Settings.EclipseDarknessEnabled && !darkCore.Enabled && Math.Abs(darkCore.AmbientScale - .05d) < 1e-6 && Math.Abs(darkCore.CloudAmbientScale - .3d) < 1e-6
            && Math.Abs(darkCore.StarVisibility - .5d) < 1e-6 && Math.Abs(darkCore.SkyNightScale - 1d) < 1e-6, "darkness preferences did not round-trip into the core settings");
        using var hostileDark = new GameplayEclipseEventSettings { EclipseAmbientScale = float.NaN, EclipseCloudAmbientScale = -4f, EclipseAtmosphereNightScale = 9f };
        using var cleanDark = GameplayEclipseEventDefaults.Sanitize(hostileDark);
        Require(Mathf.IsEqualApprox(cleanDark.EclipseAmbientScale, .15f) && Mathf.IsEqualApprox(cleanDark.EclipseCloudAmbientScale, .3f) && Mathf.IsEqualApprox(cleanDark.EclipseAtmosphereNightScale, 1f), "darkness Sanitize bounds not enforced");
        dark.Reset(); Require(dark.Save(), "darkness reset did not save");
        second.Reset();
        Require(second.Save(), "eclipse reset did not save");
        var reset = new GameplayEclipseEventDefaults(path); reset.Load();
        Require(IsDefault(reset.Settings), "eclipse reset did not restore defaults");

        BeamChecks(path);
        HubBodyChecks(path);

        string invalidParent = path + ".invalid-parent.tres";
        Require(ResourceSaver.Save(new GameplayEclipseEventSettings(), invalidParent) == Error.Ok, "invalid-path fixture write failed");
        var invalid = new GameplayEclipseEventDefaults(invalidParent + "/child.tres"); invalid.Load(); invalid.SetPreRollSeconds(60);
        Require(!invalid.Save() && invalid.LastError.Length > 0, "invalid eclipse preference path did not fail safely");
        bool threw = false;
        try { invalid.SetFloat("NoSuchField", 1); } catch (ArgumentException) { threw = true; }
        Require(threw, "unknown field did not throw");
    }
}
