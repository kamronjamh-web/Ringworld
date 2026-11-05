using System;
using System.Text.RegularExpressions;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.UI;

/// <summary>Isolated persistence evidence for the lightning preferences (defaults, round trip, clamps, reset, invalid path, older files, policy apply).</summary>
public partial class LightningSettingsSmoke : Node
{
    public string Result { get; private set; } = "RUNNING";
    public string IsolatedPath { get; set; } = "res://.godot/lightning_settings_smoke.tres";
    public override void _Ready()
    {
        try { Run(IsolatedPath); Result = "PASS"; }
        catch (Exception error) { Result = "FAIL " + error.Message; }
        finally { LightningVisualPolicy.Reset(); }
    }

    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static bool Near(float a, float b) => Mathf.IsEqualApprox(a, b);

    private static bool IsDefault(GameplayLightningSettings s) => Near(s.LightningFrequency, 1) && Near(s.UpperAtmosphereLightning, 1)
        && Near(s.LightningIllumination, 1) && Near(s.LightningIlluminationReachKm, 20) && !s.ReducedFlashes;

    private static void Run(string path)
    {
        // 1. missing file gives defaults; 2. a clean save is a no-op.
        var first = new GameplayLightningDefaults(path); first.Load();
        Require(IsDefault(first.Settings) && !first.HasSavedFile, "missing preferences did not use defaults");
        Require(first.Save() && !first.HasSavedFile, "clean defaults should save as a no-op");
        // 3. changed-flag semantics.
        Require(first.SetReducedFlashes(true) && !first.SetReducedFlashes(true), "bool Set did not report the changed flag");
        Require(first.SetLightningFrequency(.5f) && !first.SetLightningFrequency(.5f), "float Set did not report the changed flag");
        Require(first.SetLightningIllumination(1.5f) && !first.SetLightningIllumination(1.5f), "illumination Set did not report the changed flag");
        Require(first.SetLightningIlluminationReachKm(30f) && !first.SetLightningIlluminationReachKm(30f), "illumination reach Set did not report the changed flag");
        // 4. set, save, reload, compare.
        first.SetUpperAtmosphereLightning(1.75f);
        Require(first.Dirty && first.Save() && !first.Dirty && first.HasSavedFile, "preferences did not save");
        var second = new GameplayLightningDefaults(path); second.Load();
        GameplayLightningSettings s = second.Settings;
        Require(s.ReducedFlashes && Near(s.LightningFrequency, .5f) && Near(s.UpperAtmosphereLightning, 1.75f), "preferences did not round-trip");
        Require(Near(s.LightningIllumination, 1.5f) && Near(s.LightningIlluminationReachKm, 30f), "illumination preferences did not round-trip");
        // 5. clamps and NaN fallback through RangeOf.
        var (fMin, fMax) = GameplayLightningDefaults.RangeOf(nameof(GameplayLightningSettings.LightningFrequency));
        var (uMin, uMax) = GameplayLightningDefaults.RangeOf(nameof(GameplayLightningSettings.UpperAtmosphereLightning));
        Require(Near(fMin, 0) && Near(fMax, 2) && Near(uMin, 0) && Near(uMax, 2), "ranges differ from the design table");
        second.SetLightningFrequency(-5); Require(Near(second.Settings.LightningFrequency, fMin), "frequency lower bound");
        second.SetLightningFrequency(9); Require(Near(second.Settings.LightningFrequency, fMax), "frequency upper bound");
        second.SetUpperAtmosphereLightning(99); Require(Near(second.Settings.UpperAtmosphereLightning, uMax), "upper-atmosphere upper bound");
        second.SetLightningFrequency(float.NaN); Require(Near(second.Settings.LightningFrequency, 1), "NaN frequency did not fall back");
        second.SetUpperAtmosphereLightning(float.PositiveInfinity); Require(Near(second.Settings.UpperAtmosphereLightning, 1), "infinite upper-atmosphere did not fall back");
        foreach (var field in new[]
        {
            (Name: nameof(GameplayLightningSettings.LightningIllumination), Min: 0f, Max: 2f, Default: 1f),
            (Name: nameof(GameplayLightningSettings.LightningIlluminationReachKm), Min: 1f, Max: 40f, Default: 20f)
        })
        {
            var (min, max) = GameplayLightningDefaults.RangeOf(field.Name);
            Require(Near(min, field.Min) && Near(max, field.Max), field.Name + " range differs from design");
            second.SetFloat(field.Name, -100); Require(Near((float)second.Settings.Get(field.Name).AsDouble(), min), field.Name + " lower bound");
            second.SetFloat(field.Name, 100); Require(Near((float)second.Settings.Get(field.Name).AsDouble(), max), field.Name + " upper bound");
            foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                second.SetFloat(field.Name, value);
                Require(Near((float)second.Settings.Get(field.Name).AsDouble(), field.Default), field.Name + " non-finite fallback");
            }
        }
        // 6. Sanitize.
        using var dirty = new GameplayLightningSettings { LightningFrequency = 9, UpperAtmosphereLightning = float.NaN, LightningIllumination = -1, LightningIlluminationReachKm = float.PositiveInfinity, ReducedFlashes = true };
        using var clean = GameplayLightningDefaults.Sanitize(dirty);
        Require(Near(clean.LightningFrequency, 2) && Near(clean.UpperAtmosphereLightning, 1) && clean.ReducedFlashes, "Sanitize bounds were not enforced");
        Require(Near(clean.LightningIllumination, 0) && Near(clean.LightningIlluminationReachKm, 20), "illumination Sanitize bounds were not enforced");
        // 7. reset round-trip.
        second.Reset();
        Require(second.Save(), "reset did not save");
        var reset = new GameplayLightningDefaults(path); reset.Load();
        Require(IsDefault(reset.Settings), "reset did not restore defaults");
        // 8. an invalid path fails safely.
        string invalidParent = path + ".invalid-parent.tres";
        Require(ResourceSaver.Save(new GameplayLightningSettings(), invalidParent) == Error.Ok, "invalid-path fixture write failed");
        var invalid = new GameplayLightningDefaults(invalidParent + "/child.tres"); invalid.Load(); invalid.SetLightningFrequency(.2f);
        Require(!invalid.Save() && invalid.LastError.Length > 0, "invalid preference path did not fail safely");
        // 9. an unknown field throws.
        bool threw = false;
        try { invalid.SetFloat("NoSuchField", 1); } catch (ArgumentException) { threw = true; }
        bool threwBool = false;
        try { invalid.SetBool("NoSuchField", true); } catch (ArgumentException) { threwBool = true; }
        Require(threw && threwBool, "unknown field did not throw");
        // 10. an older file without the new fields loads their defaults.
        var older = new GameplayLightningDefaults(path); older.Load();
        older.SetLightningFrequency(.25f); older.SetUpperAtmosphereLightning(.3f);
        older.SetLightningIllumination(.4f); older.SetLightningIlluminationReachKm(8f);
        Require(older.Save(), "older fixture save");
        using (var file = FileAccess.Open(path, FileAccess.ModeFlags.Read))
        {
            Require(file != null, "older fixture read");
            string text = Regex.Replace(file!.GetAsText(), @"^(UpperAtmosphereLightning|LightningIllumination|LightningIlluminationReachKm) = .*\r?\n", "", RegexOptions.Multiline);
            file.Close();
            using var write = FileAccess.Open(path, FileAccess.ModeFlags.Write);
            Require(write != null, "older fixture rewrite");
            write!.StoreString(text);
        }
        var legacy = new GameplayLightningDefaults(path); legacy.Load();
        Require(Near(legacy.Settings.LightningFrequency, .25f) && Near(legacy.Settings.UpperAtmosphereLightning, 1), "older file did not load the default for the new field");
        Require(Near(legacy.Settings.LightningIllumination, 1) && Near(legacy.Settings.LightningIlluminationReachKm, 20), "older file did not load illumination defaults");
        // 11. Apply publishes the values to the lightning policy.
        legacy.SetReducedFlashes(true); legacy.SetLightningFrequency(1.5f); legacy.SetUpperAtmosphereLightning(.5f);
        legacy.SetLightningIllumination(1.25f); legacy.SetLightningIlluminationReachKm(32f);
        legacy.Apply();
        Require(LightningVisualPolicy.ReducedFlashes && Near(LightningVisualPolicy.FrequencyScale, 1.5f) && Near(LightningVisualPolicy.UpperAtmosphereScale, .5f), "Apply did not publish to the policy");
        Require(Near(LightningVisualPolicy.NaturalStrikeChanceScale, .75f) && Near(LightningVisualPolicy.UpperAtmosphereChanceScale, .5f * .35f), "derived policy scales");
        Require(Near(LightningVisualPolicy.SurfaceIlluminationStrength, 1.25f) && Near(LightningVisualPolicy.SurfaceIlluminationReachMeters, 32000f), "Apply did not publish illumination strength and metres");
        LightningVisualPolicy.Apply(1.5f, .5f, true, -10f, 100f);
        Require(Near(LightningVisualPolicy.SurfaceIlluminationStrength, 0) && Near(LightningVisualPolicy.SurfaceIlluminationReachMeters, 40000f), "policy illumination bounds");
        LightningVisualPolicy.Apply(1.5f, .5f, true, 10f, -10f);
        Require(Near(LightningVisualPolicy.SurfaceIlluminationStrength, 2) && Near(LightningVisualPolicy.SurfaceIlluminationReachMeters, 1000f), "policy illumination opposite bounds");
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            LightningVisualPolicy.Apply(1.5f, .5f, true, value, value);
            Require(Near(LightningVisualPolicy.SurfaceIlluminationStrength, 1) && Near(LightningVisualPolicy.SurfaceIlluminationReachMeters, 20000f), "policy illumination non-finite fallback");
        }
        Require(Near(LightningVisualPolicy.NaturalStrikeChanceScale, .75f) && Near(LightningVisualPolicy.PeakScale, LightningVisualPolicy.ReducedPeakScale), "illumination changed scheduler or pulse policy");
        LightningVisualPolicy.Apply(1, 1, false, 2, 40);
        LightningVisualPolicy.Apply(1, 1, false);
        Require(Near(LightningVisualPolicy.SurfaceIlluminationStrength, 1) && Near(LightningVisualPolicy.SurfaceIlluminationReachMeters, 20000f), "three-argument Apply did not restore illumination defaults");
        LightningVisualPolicy.Apply(0, 0, true, 0, 1);
        LightningVisualPolicy.Reset();
        Require(!LightningVisualPolicy.ReducedFlashes && Near(LightningVisualPolicy.FrequencyScale, 1) && Near(LightningVisualPolicy.UpperAtmosphereScale, 1)
            && Near(LightningVisualPolicy.SurfaceIlluminationStrength, 1) && Near(LightningVisualPolicy.SurfaceIlluminationReachMeters, 20000f), "policy Reset did not restore defaults");
    }
}
