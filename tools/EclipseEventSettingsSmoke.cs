using System;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
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
        "CrescendoSeconds", "HandoffFadeSeconds", "FlashClimbSeconds", "FlashHoldSeconds", "FlashFadeSeconds", "HeartbeatKick", "HeartbeatTauSeconds",
        "RelaunchKick", "BreathAmplitude", "BreathPeriodSeconds", "CollapseLeadSeconds", "InstabilitySeconds", "FlickerMax", "FlickerHzStart", "FlickerHzEnd",
        "OverloadSeconds", "OverloadGain", "ImplosionSeconds", "BeamRetractSeconds", "ShockwaveSeconds", "ShockwaveArrivalFraction", "ShockFlashLevel",
        "ShockFlashSeconds", "ReturnTailSeconds", "RealSunEnergy", "HubDirectFraction", "HubDayAtFull",
        "EclipseAmbientScale", "EclipseAtmosphereNightScale", "EclipseSkyNightScale", "EclipseCloudAmbientScale", "EclipseStarVisibility"
    };

    private static bool IsDefault(GameplayEclipseEventSettings s)
    {
        if (!s.EventEnabled || !s.EclipseDarknessEnabled || s.IgnitionSpeedMode != 0 || s.RampMode != 0 || !s.SuppressGridPulseDuringIgnition || s.RefuelClock != 0) return false;
        foreach (string name in FloatNames)
            if (!Mathf.IsEqualApprox((float)s.Get(name).AsDouble(), GameplayEclipseEventDefaults.DefaultOf(name))) return false;
        return true;
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
        Require(first.Dirty && first.Save() && !first.Dirty, "eclipse preferences did not save");
        var second = new GameplayEclipseEventDefaults(path); second.Load();
        var s = second.Settings;
        Require(!s.EventEnabled && !s.SuppressGridPulseDuringIgnition && s.IgnitionSpeedMode == 1 && s.RampMode == 1 && s.RefuelClock == 1, "flag/enum preferences did not round-trip");
        Require(Mathf.IsEqualApprox(s.PreRollSeconds, 120) && Mathf.IsEqualApprox(s.ParallaxSafetySeconds, 30) && Mathf.IsEqualApprox(s.DarknessBeatSeconds, 45)
            && Mathf.IsEqualApprox(s.KindleLevel, .25f) && Mathf.IsEqualApprox(s.CollapseLeadSeconds, 200) && Mathf.IsEqualApprox(s.HubDirectFraction, 1.1f)
            && Mathf.IsEqualApprox(s.ShockFlashLevel, 1.5f) && Mathf.IsEqualApprox(s.IgnitionSeconds, 300), "numeric preferences did not round-trip");
        EclipseEventSettings mapped2 = GameplayEclipseEventDefaults.ToCore(s);
        Require(!mapped2.EventEnabled && mapped2.IgnitionSpeedMode == EclipseIgnitionSpeedMode.Manual && mapped2.RampMode == EclipseRampMode.Perceptual
            && mapped2.RefuelClock == EclipseRefuelClock.Weather && Math.Abs(mapped2.PreRollSeconds - 120d) < 1e-6, "ToCore mapping");

        // Setter clamps and NaN fallback.
        second.SetPreRollSeconds(1e9f); second.SetParallaxSafetySeconds(-5); second.SetDarknessBeatSeconds(float.NaN); second.SetKindleLevel(float.PositiveInfinity);
        second.SetFloat("MainClimbSeconds", 0); second.SetFloat("OverloadGain", 99); second.SetFloat("FlickerHzEnd", float.NegativeInfinity); second.SetFloat("HubDayAtFull", 2);
        second.SetIgnitionSpeedMode(9); second.SetRampMode(-4); second.SetRefuelClock(5);
        s = second.Settings;
        var (pMin, pMax) = GameplayEclipseEventDefaults.RangeOf("PreRollSeconds");
        Require(Mathf.IsEqualApprox(s.PreRollSeconds, pMax) && pMin == 0 && Mathf.IsEqualApprox(pMax, 900), "pre-roll upper bound not enforced");
        Require(Mathf.IsEqualApprox(s.ParallaxSafetySeconds, 0), "margin lower bound not enforced");
        Require(Mathf.IsEqualApprox(s.DarknessBeatSeconds, 20), "NaN did not fall back to the default");
        Require(Mathf.IsEqualApprox(s.KindleLevel, .10f), "infinity did not fall back to the default");
        Require(Mathf.IsEqualApprox(s.MainClimbSeconds, .5f) && Mathf.IsEqualApprox(s.OverloadGain, 5) && Mathf.IsEqualApprox(s.FlickerHzEnd, 6)
            && Mathf.IsEqualApprox(s.HubDayAtFull, 1), "setter bounds were not enforced");
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

        string invalidParent = path + ".invalid-parent.tres";
        Require(ResourceSaver.Save(new GameplayEclipseEventSettings(), invalidParent) == Error.Ok, "invalid-path fixture write failed");
        var invalid = new GameplayEclipseEventDefaults(invalidParent + "/child.tres"); invalid.Load(); invalid.SetPreRollSeconds(60);
        Require(!invalid.Save() && invalid.LastError.Length > 0, "invalid eclipse preference path did not fail safely");
        bool threw = false;
        try { invalid.SetFloat("NoSuchField", 1); } catch (ArgumentException) { threw = true; }
        Require(threw, "unknown field did not throw");
    }
}
