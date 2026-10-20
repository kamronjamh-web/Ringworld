using System;
using Godot;
using RingworldTechDemo.Poi;
using RingworldTechDemo.UI;

/// <summary>Isolated persistence evidence for the Weather Lung preferences (sanitize, clamp, round trip, atomic save, isolated path, tuning bundle).</summary>
public partial class WeatherLungSettingsSmoke : Node
{
    public string Result { get; private set; } = "RUNNING";
    public string IsolatedPath { get; set; } = "res://.godot/weather_lung_settings_smoke.tres";
    public override void _Ready()
    {
        try { Run(IsolatedPath); Result = "PASS"; }
        catch (Exception error) { Result = "FAIL " + error.Message; }
    }

    private static bool Near(float a, float b) => Mathf.IsEqualApprox(a, b);

    private static bool IsDefault(GameplayWeatherLungSettings s) =>
        s.Enabled && s.AnimationEnabled && Near(s.CycleSeconds, 180) && Near(s.VaneAmplitudeDeg, 35) && Near(s.GillAmplitudeDeg, 35) && Near(s.TurbineMaxRpm, 1.5f)
        && Near(s.RippleLag, 0.012f) && Near(s.NozzleEmissionScale, 0.25f) && Near(s.HeatGridEmissionScale, 1) && s.SunMode == 0 && Near(s.SunManual, 1)
        && Near(s.FullRateMeters, 8000) && Near(s.ReducedRateMeters, 28000) && Near(s.ReducedRateHz, 10) && Near(s.DetailRangeMeters, 28000)
        && Near(s.DressingRangeMeters, 2000) && Near(s.DressingFadeMeters, 250) && Near(s.FarProxyMaxKm, 700) && Near(s.DetailLoadRadiusMeters, 36000)
        && Near(s.UnloadMarginMeters, 8000) && Near(s.CollisionRadiusMeters, 3000) && Near(s.DetailAttachBudgetMs, 4);

    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    private static void Run(string path)
    {
        var first = new GameplayWeatherLungDefaults(path); first.Load();
        Require(IsDefault(first.Settings) && !first.HasSavedFile, "missing preferences did not use defaults");
        Require(first.Save(), "clean defaults should save as a no-op");
        Require(Near(first.ToTuning().CycleSeconds, 180) && first.ToTuning().SunMode == WeatherLungSunMode.Auto, "default tuning bundle");
        Require(first.SetEnabled(false) && !first.SetEnabled(false), "Set did not report the changed flag");
        first.SetAnimationEnabled(false); first.SetCycleSeconds(240); first.SetVaneAmplitudeDeg(20); first.SetGillAmplitudeDeg(12); first.SetTurbineMaxRpm(3);
        first.SetRippleLag(0.02f); first.SetNozzleEmissionScale(1.5f); first.SetHeatGridEmissionScale(2); Require(first.SetSunMode(2) && !first.SetSunMode(2), "SunMode Set");
        first.SetSunManual(0.4f); first.SetFullRateMeters(5000); first.SetReducedRateMeters(20000); first.SetReducedRateHz(20); first.SetDressingRangeMeters(3000);
        first.SetDressingFadeMeters(100); first.SetFarProxyMaxKm(400); first.SetUnloadMarginMeters(5000); first.SetCollisionRadiusMeters(2000);
        Require(first.SetDetailAttachBudgetMs(9) && !first.SetDetailAttachBudgetMs(9), "attach budget Set");
        Require(first.SetDetailRangeMeters(20000), "detail range Set");
        Require(Near(first.Settings.DetailLoadRadiusMeters, 36000), "load radius was lowered by a smaller range");
        Require(first.SetDetailLoadRadiusMeters(30000) && Near(first.Settings.DetailLoadRadiusMeters, 30000), "load radius Set");
        Require(first.Dirty && first.Save() && !first.Dirty, "preferences did not save");

        var second = new GameplayWeatherLungDefaults(path); second.Load();
        GameplayWeatherLungSettings s = second.Settings;
        Require(!s.Enabled && !s.AnimationEnabled && Near(s.CycleSeconds, 240) && Near(s.VaneAmplitudeDeg, 20) && Near(s.GillAmplitudeDeg, 12) && Near(s.TurbineMaxRpm, 3)
            && Near(s.RippleLag, 0.02f) && Near(s.NozzleEmissionScale, 1.5f) && Near(s.HeatGridEmissionScale, 2) && s.SunMode == 2 && Near(s.SunManual, 0.4f)
            && Near(s.FullRateMeters, 5000) && Near(s.ReducedRateMeters, 20000) && Near(s.ReducedRateHz, 20) && Near(s.DetailRangeMeters, 20000)
            && Near(s.DressingRangeMeters, 3000) && Near(s.DressingFadeMeters, 100) && Near(s.FarProxyMaxKm, 400) && Near(s.DetailLoadRadiusMeters, 30000)
            && Near(s.UnloadMarginMeters, 5000) && Near(s.CollisionRadiusMeters, 2000) && Near(s.DetailAttachBudgetMs, 9), "preferences did not round-trip");
        Require(Near(second.ToTuning().DetailAttachBudgetMs, 9), "attach budget missing from the tuning bundle");
        WeatherLungTuning tuning = second.ToTuning();
        Require(!tuning.Enabled && tuning.SunMode == WeatherLungSunMode.Night && Near(tuning.EffectiveLoadRadiusMeters, 30000), "tuning bundle after the round trip");

        // Setter bounds, NaN fallbacks and the range + 4 km rule.
        second.SetCycleSeconds(1); second.SetVaneAmplitudeDeg(99); second.SetTurbineMaxRpm(-3); second.SetRippleLag(float.NaN); second.SetSunMode(99); second.SetSunManual(5);
        second.SetFullRateMeters(1); second.SetReducedRateMeters(1e9f); second.SetReducedRateHz(0); second.SetDressingRangeMeters(1); second.SetDressingFadeMeters(1e9f);
        second.SetFarProxyMaxKm(1); second.SetUnloadMarginMeters(1); second.SetCollisionRadiusMeters(1e9f); second.SetNozzleEmissionScale(99); second.SetHeatGridEmissionScale(-1);
        s = second.Settings;
        Require(Near(s.CycleSeconds, GameplayWeatherLungDefaults.MinimumCycleSeconds) && Near(s.VaneAmplitudeDeg, GameplayWeatherLungDefaults.MaximumAmplitudeDeg)
            && Near(s.TurbineMaxRpm, 0) && Near(s.RippleLag, 0.012f) && s.SunMode == GameplayWeatherLungDefaults.MaximumSunMode && Near(s.SunManual, 1), "setter bounds (1)");
        Require(Near(s.FullRateMeters, GameplayWeatherLungDefaults.MinimumFullRateMeters) && Near(s.ReducedRateMeters, GameplayWeatherLungDefaults.MaximumReducedRateMeters)
            && Near(s.ReducedRateHz, GameplayWeatherLungDefaults.MinimumReducedRateHz) && Near(s.DressingRangeMeters, GameplayWeatherLungDefaults.MinimumDressingRangeMeters)
            && Near(s.DressingFadeMeters, GameplayWeatherLungDefaults.MaximumDressingFadeMeters) && Near(s.FarProxyMaxKm, GameplayWeatherLungDefaults.MinimumFarProxyMaxKm)
            && Near(s.UnloadMarginMeters, GameplayWeatherLungDefaults.MinimumUnloadMarginMeters) && Near(s.CollisionRadiusMeters, GameplayWeatherLungDefaults.MaximumCollisionRadiusMeters)
            && Near(s.NozzleEmissionScale, GameplayWeatherLungDefaults.MaximumEmissionScale) && Near(s.HeatGridEmissionScale, 0), "setter bounds (2)");
        second.SetDetailAttachBudgetMs(0); Require(Near(second.Settings.DetailAttachBudgetMs, GameplayWeatherLungDefaults.MinimumAttachBudgetMs), "attach budget lower bound");
        second.SetDetailAttachBudgetMs(99); Require(Near(second.Settings.DetailAttachBudgetMs, GameplayWeatherLungDefaults.MaximumAttachBudgetMs), "attach budget upper bound");
        second.SetDetailAttachBudgetMs(float.NaN); Require(Near(second.Settings.DetailAttachBudgetMs, 4), "attach budget NaN fallback");
        second.SetDetailRangeMeters(40000);
        Require(Near(second.Settings.DetailLoadRadiusMeters, 44000), "raising the range did not raise the load radius to range + 4 km");
        second.SetDetailLoadRadiusMeters(14000);
        Require(Near(second.Settings.DetailLoadRadiusMeters, 44000), "load radius went below range + 4 km");
        second.SetDetailRangeMeters(10000); second.SetDetailLoadRadiusMeters(14000);
        Require(Near(second.Settings.DetailLoadRadiusMeters, 14000), "load radius floor at range 10 km");

        using var dirty = new GameplayWeatherLungSettings { CycleSeconds = 1, VaneAmplitudeDeg = float.NaN, GillAmplitudeDeg = 99, TurbineMaxRpm = float.PositiveInfinity, RippleLag = 5,
            NozzleEmissionScale = -1, HeatGridEmissionScale = float.NaN, SunMode = -4, SunManual = 9, FullRateMeters = 1e9f, ReducedRateMeters = 1, ReducedRateHz = 1e9f,
            DetailRangeMeters = 39000, DressingRangeMeters = float.NaN, DressingFadeMeters = -5, FarProxyMaxKm = 99999, DetailLoadRadiusMeters = 14000, UnloadMarginMeters = 1e9f, CollisionRadiusMeters = 1, DetailAttachBudgetMs = 1e9f };
        using var clean = GameplayWeatherLungDefaults.Sanitize(dirty);
        Require(Near(clean.CycleSeconds, GameplayWeatherLungDefaults.MinimumCycleSeconds) && Near(clean.VaneAmplitudeDeg, 35) && Near(clean.GillAmplitudeDeg, 35) && Near(clean.TurbineMaxRpm, 1.5f)
            && Near(clean.RippleLag, GameplayWeatherLungDefaults.MaximumRippleLag) && Near(clean.NozzleEmissionScale, 0) && Near(clean.HeatGridEmissionScale, 1) && clean.SunMode == 0
            && Near(clean.SunManual, 1), "Sanitize bounds (1)");
        Require(Near(clean.FullRateMeters, GameplayWeatherLungDefaults.MaximumFullRateMeters) && Near(clean.ReducedRateMeters, GameplayWeatherLungDefaults.MinimumReducedRateMeters)
            && Near(clean.ReducedRateHz, GameplayWeatherLungDefaults.MaximumReducedRateHz) && Near(clean.DressingRangeMeters, 2000) && Near(clean.DressingFadeMeters, 0)
            && Near(clean.FarProxyMaxKm, GameplayWeatherLungDefaults.MaximumFarProxyMaxKm) && Near(clean.UnloadMarginMeters, GameplayWeatherLungDefaults.MaximumUnloadMarginMeters)
            && Near(clean.CollisionRadiusMeters, GameplayWeatherLungDefaults.MinimumCollisionRadiusMeters), "Sanitize bounds (2)");
        Require(Near(clean.DetailAttachBudgetMs, GameplayWeatherLungDefaults.MaximumAttachBudgetMs), "Sanitize attach budget");
        Require(Near(clean.DetailLoadRadiusMeters, 43000), "Sanitize did not raise the load radius to range + 4 km");

        second.Reset();
        Require(second.Save(), "reset did not save");
        var reset = new GameplayWeatherLungDefaults(path); reset.Load();
        Require(IsDefault(reset.Settings), "reset did not restore defaults");

        string invalidParent = path + ".invalid-parent.tres";
        Require(ResourceSaver.Save(new GameplayWeatherLungSettings(), invalidParent) == Error.Ok, "invalid-path fixture write failed");
        var invalid = new GameplayWeatherLungDefaults(invalidParent + "/child.tres"); invalid.Load(); invalid.SetCycleSeconds(60);
        Require(!invalid.Save() && invalid.LastError.Length > 0, "invalid preference path did not fail safely");
    }
}
