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

    private static bool IsDefault(GameplayLightningSettings s) => Near(s.LightningFrequency, 1) && Near(s.UpperAtmosphereLightning, 1) && !s.ReducedFlashes;

    private static void Run(string path)
    {
        // 1. missing file gives defaults; 2. a clean save is a no-op.
        var first = new GameplayLightningDefaults(path); first.Load();
        Require(IsDefault(first.Settings) && !first.HasSavedFile, "missing preferences did not use defaults");
        Require(first.Save() && !first.HasSavedFile, "clean defaults should save as a no-op");
        // 3. changed-flag semantics.
        Require(first.SetReducedFlashes(true) && !first.SetReducedFlashes(true), "bool Set did not report the changed flag");
        Require(first.SetLightningFrequency(.5f) && !first.SetLightningFrequency(.5f), "float Set did not report the changed flag");
        // 4. set, save, reload, compare.
        first.SetUpperAtmosphereLightning(1.75f);
        Require(first.Dirty && first.Save() && !first.Dirty && first.HasSavedFile, "preferences did not save");
        var second = new GameplayLightningDefaults(path); second.Load();
        GameplayLightningSettings s = second.Settings;
        Require(s.ReducedFlashes && Near(s.LightningFrequency, .5f) && Near(s.UpperAtmosphereLightning, 1.75f), "preferences did not round-trip");
        // 5. clamps and NaN fallback through RangeOf.
        var (fMin, fMax) = GameplayLightningDefaults.RangeOf(nameof(GameplayLightningSettings.LightningFrequency));
        var (uMin, uMax) = GameplayLightningDefaults.RangeOf(nameof(GameplayLightningSettings.UpperAtmosphereLightning));
        Require(Near(fMin, 0) && Near(fMax, 2) && Near(uMin, 0) && Near(uMax, 2), "ranges differ from the design table");
        second.SetLightningFrequency(-5); Require(Near(second.Settings.LightningFrequency, fMin), "frequency lower bound");
        second.SetLightningFrequency(9); Require(Near(second.Settings.LightningFrequency, fMax), "frequency upper bound");
        second.SetUpperAtmosphereLightning(99); Require(Near(second.Settings.UpperAtmosphereLightning, uMax), "upper-atmosphere upper bound");
        second.SetLightningFrequency(float.NaN); Require(Near(second.Settings.LightningFrequency, 1), "NaN frequency did not fall back");
        second.SetUpperAtmosphereLightning(float.PositiveInfinity); Require(Near(second.Settings.UpperAtmosphereLightning, 1), "infinite upper-atmosphere did not fall back");
        // 6. Sanitize.
        using var dirty = new GameplayLightningSettings { LightningFrequency = 9, UpperAtmosphereLightning = float.NaN, ReducedFlashes = true };
        using var clean = GameplayLightningDefaults.Sanitize(dirty);
        Require(Near(clean.LightningFrequency, 2) && Near(clean.UpperAtmosphereLightning, 1) && clean.ReducedFlashes, "Sanitize bounds were not enforced");
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
        // 10. an older file without the new fields loads their defaults: strip UpperAtmosphereLightning from a saved file.
        var older = new GameplayLightningDefaults(path); older.Load();
        older.SetLightningFrequency(.25f); older.SetUpperAtmosphereLightning(.3f);
        Require(older.Save(), "older fixture save");
        using (var file = FileAccess.Open(path, FileAccess.ModeFlags.Read))
        {
            Require(file != null, "older fixture read");
            string text = Regex.Replace(file!.GetAsText(), @"^UpperAtmosphereLightning = .*\r?\n", "", RegexOptions.Multiline);
            file.Close();
            using var write = FileAccess.Open(path, FileAccess.ModeFlags.Write);
            Require(write != null, "older fixture rewrite");
            write!.StoreString(text);
        }
        var legacy = new GameplayLightningDefaults(path); legacy.Load();
        Require(Near(legacy.Settings.LightningFrequency, .25f) && Near(legacy.Settings.UpperAtmosphereLightning, 1), "older file did not load the default for the new field");
        // 11. Apply publishes the values to the lightning policy.
        legacy.SetReducedFlashes(true); legacy.SetLightningFrequency(1.5f); legacy.SetUpperAtmosphereLightning(.5f);
        legacy.Apply();
        Require(LightningVisualPolicy.ReducedFlashes && Near(LightningVisualPolicy.FrequencyScale, 1.5f) && Near(LightningVisualPolicy.UpperAtmosphereScale, .5f), "Apply did not publish to the policy");
        Require(Near(LightningVisualPolicy.NaturalStrikeChanceScale, .75f) && Near(LightningVisualPolicy.UpperAtmosphereChanceScale, .5f * .35f), "derived policy scales");
    }
}
