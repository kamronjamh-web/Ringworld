using System;
using System.IO;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Settings smoke (the live-tuning-setting skill's ten checks plus the migration) for the wet world group: Wetness, WetGloss, Puddles, DryingSpeed, LensDroplets.
/// Run: tools/wet_world_settings_smoke_test.gd. Prints WET_WORLD_SETTINGS=PASS.
/// </summary>
[GlobalClass]
public partial class WetWorldSettingsSmoke : RefCounted
{
    static readonly string[] Keys = { "Wetness", "WetGloss", "Puddles", "DryingSpeed", "LensDroplets" };

    static void Reset(RingWetWorldSettings s) { s.Wetness = 1f; s.WetGloss = 0.18f; s.Puddles = 1f; s.DryingSpeed = 1f; s.LensDroplets = 0.7f; s.SsrOnlyWhileWet = true; }

    public Godot.Collections.Dictionary Run(string path, string legacyPath)
    {
        if (!path.StartsWith("res://.godot/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
            return new() { ["pass"] = false, ["detail"] = "isolated path required" };
        Directory.CreateDirectory(Path.GetDirectoryName(ProjectSettings.GlobalizePath(path))!);
        string absolute = ProjectSettings.GlobalizePath(path);
        if (File.Exists(absolute)) File.Delete(absolute);
        if (File.Exists(ProjectSettings.GlobalizePath(legacyPath))) File.Delete(ProjectSettings.GlobalizePath(legacyPath));
        RingWetWorldSettings original = RingWetWorldSettings.Current;
        using var runtime = new RingWetWorldSettings();
        RingWetWorldSettings.Current = runtime;
        try
        {
            // 1. missing file (and no legacy file) gives defaults
            var defaults = new GameplayWetWorldDefaults(path) { LegacySurfaceTerrainPath = legacyPath }; defaults.Initialize();
            bool missing = runtime.Wetness == 1f && Math.Abs(runtime.WetGloss - 0.18f) < 1e-6f && runtime.Puddles == 1f && runtime.DryingSpeed == 1f && Math.Abs(runtime.LensDroplets - 0.7f) < 1e-6f
                && !defaults.Dirty && defaults.Settings.Overrides.Count == 0 && !defaults.Migrated;
            // 2. a clean save is a no-op
            bool cleanSave = defaults.Save() && !File.Exists(absolute);
            // 3. changed-flag semantics
            bool changed = defaults.SetOverride("Puddles", 0f) && !defaults.SetOverride("Puddles", 0f) && defaults.Dirty && runtime.Puddles == 0f;
            // 4. set, save, reload, compare
            bool setAll = defaults.SetOverride("Wetness", 1.4f) && defaults.SetOverride("WetGloss", 0.3f) && defaults.SetOverride("DryingSpeed", 2.5f) && defaults.SetOverride("LensDroplets", 0f);
            bool saved = defaults.Save() && !defaults.Dirty && File.Exists(absolute);
            Reset(runtime);
            var loaded = new GameplayWetWorldDefaults(path) { LegacySurfaceTerrainPath = legacyPath }; loaded.Initialize();
            bool roundTrip = Math.Abs(runtime.Wetness - 1.4f) < 1e-5f && Math.Abs(runtime.WetGloss - 0.3f) < 1e-5f && runtime.Puddles == 0f && Math.Abs(runtime.DryingSpeed - 2.5f) < 1e-5f && runtime.LensDroplets == 0f;
            // 5. clamps and NaN fallback
            bool clamps = true;
            foreach (string key in Keys)
            {
                var range = GameplayWetWorldDefaults.Limits[key];
                clamps &= GameplayWetWorldDefaults.TryValidate(key, 1e9, out Variant hi) && hi.AsSingle() == range.Max;
                clamps &= GameplayWetWorldDefaults.TryValidate(key, -1e9, out Variant lo) && lo.AsSingle() == range.Min;
                clamps &= !GameplayWetWorldDefaults.TryValidate(key, double.NaN, out _) && !GameplayWetWorldDefaults.TryValidate(key, double.PositiveInfinity, out _)
                    && !GameplayWetWorldDefaults.TryValidate(key, "text", out _) && !loaded.SetOverride(key, double.NaN);
            }
            bool ranges = true;
            foreach (string key in Keys)
            {
                var range = GameplayWetWorldDefaults.Limits[key];
                foreach (var property in runtime.GetPropertyList())
                    if (property["name"].AsString() == key)
                    {
                        string[] hint = property["hint_string"].AsString().Split(',');
                        ranges &= float.Parse(hint[0], System.Globalization.CultureInfo.InvariantCulture) == range.Min
                            && float.Parse(hint[1], System.Globalization.CultureInfo.InvariantCulture) == range.Max;
                    }
            }
            // 6. Sanitize
            string dirtyPath = path + ".dirty.tres";
            var dirty = new GameplayWetWorldSettings();
            dirty.Overrides["Wetness"] = 99f; dirty.Overrides["Puddles"] = double.NaN; dirty.Overrides["LensDroplets"] = "x"; dirty.Overrides["Bogus"] = 1f;
            ResourceSaver.Save(dirty, dirtyPath);
            Reset(runtime);
            var sanitized = new GameplayWetWorldDefaults(dirtyPath) { LegacySurfaceTerrainPath = legacyPath }; sanitized.Initialize();
            bool sanitize = runtime.Wetness == 1.5f && runtime.Puddles == 1f && Math.Abs(runtime.LensDroplets - 0.7f) < 1e-6f && !sanitized.Settings.Overrides.ContainsKey("Bogus") && sanitized.LastError.Length > 0;
            File.Delete(ProjectSettings.GlobalizePath(dirtyPath));
            // 7. reset round-trip
            loaded.Reset();
            bool reset = runtime.Wetness == 1f && Math.Abs(runtime.WetGloss - 0.18f) < 1e-6f && runtime.Puddles == 1f && runtime.DryingSpeed == 1f && Math.Abs(runtime.LensDroplets - 0.7f) < 1e-6f
                && loaded.Settings.Overrides.Count == 0 && loaded.Save();
            var afterReset = new GameplayWetWorldDefaults(path) { LegacySurfaceTerrainPath = legacyPath }; afterReset.Initialize();
            reset &= runtime.Puddles == 1f && afterReset.Settings.Overrides.Count == 0;
            // 8. an invalid path fails safely and keeps the pending change
            var blocked = new GameplayWetWorldDefaults(path + "/blocked.tres") { LegacySurfaceTerrainPath = legacyPath }; blocked.Initialize(); blocked.SetOverride("Puddles", 0.5f);
            bool failure = !blocked.Save() && blocked.Dirty && blocked.LastError.Length > 0;
            // 9. an unknown key is rejected
            bool unknown = !loaded.SetOverride("PuddlesTypo", 1f) && !loaded.SetOverride("", 1f);
            // 10. an older file without a key loads the default for it
            string oldPath = path + ".old.tres";
            var old = new GameplayWetWorldSettings(); old.Overrides["LensDroplets"] = 0.4f;
            ResourceSaver.Save(old, oldPath);
            Reset(runtime);
            var older = new GameplayWetWorldDefaults(oldPath) { LegacySurfaceTerrainPath = legacyPath }; older.Initialize();
            bool oldFile = Math.Abs(runtime.LensDroplets - 0.4f) < 1e-5f && runtime.Wetness == 1f && Math.Abs(runtime.WetGloss - 0.18f) < 1e-6f && runtime.Puddles == 1f && runtime.DryingSpeed == 1f;
            File.Delete(ProjectSettings.GlobalizePath(oldPath));
            // 11. migration from the retired Surface terrain keys: only changed values move, once.
            string migratePath = path + ".migrate.tres";
            if (File.Exists(ProjectSettings.GlobalizePath(migratePath))) File.Delete(ProjectSettings.GlobalizePath(migratePath));
            var legacy = new GameplaySurfaceTerrainSettings { RainWetnessStrength = 0.5f, WetRoughness = 0.22f };
            ResourceSaver.Save(legacy, legacyPath);
            Reset(runtime);
            var migrated = new GameplayWetWorldDefaults(migratePath) { LegacySurfaceTerrainPath = legacyPath }; migrated.Initialize();
            bool migration = migrated.Migrated && migrated.Dirty && Math.Abs(runtime.Wetness - 0.5f) < 1e-5f && Math.Abs(runtime.WetGloss - 0.22f) < 1e-5f && runtime.Puddles == 1f && migrated.Save() && File.Exists(ProjectSettings.GlobalizePath(migratePath));
            // The migrated file now wins: changing the legacy file afterwards changes nothing.
            ResourceSaver.Save(new GameplaySurfaceTerrainSettings { RainWetnessStrength = 0.9f, WetRoughness = 0.4f }, legacyPath);
            Reset(runtime);
            var again = new GameplayWetWorldDefaults(migratePath) { LegacySurfaceTerrainPath = legacyPath }; again.Initialize();
            migration &= !again.Migrated && !again.Dirty && Math.Abs(runtime.Wetness - 0.5f) < 1e-5f && Math.Abs(runtime.WetGloss - 0.22f) < 1e-5f;
            // Untouched legacy defaults (1.0 / 0.35) leave the new defaults alone; out-of-range legacy values clamp.
            ResourceSaver.Save(new GameplaySurfaceTerrainSettings(), legacyPath);
            Reset(runtime);
            string untouchedPath = path + ".untouched.tres";
            var untouched = new GameplayWetWorldDefaults(untouchedPath) { LegacySurfaceTerrainPath = legacyPath }; untouched.Initialize();
            migration &= !untouched.Migrated && !untouched.Dirty && Math.Abs(runtime.WetGloss - 0.18f) < 1e-6f && runtime.Wetness == 1f;
            ResourceSaver.Save(new GameplaySurfaceTerrainSettings { WetRoughness = 0.75f }, legacyPath);
            Reset(runtime);
            string clampPath = path + ".clamp.tres";
            var clamped = new GameplayWetWorldDefaults(clampPath) { LegacySurfaceTerrainPath = legacyPath }; clamped.Initialize();
            migration &= clamped.Migrated && Math.Abs(runtime.WetGloss - 0.6f) < 1e-5f;
            foreach (string cleanup in new[] { migratePath, untouchedPath, clampPath, legacyPath }) if (File.Exists(ProjectSettings.GlobalizePath(cleanup))) File.Delete(ProjectSettings.GlobalizePath(cleanup));
            // The preset-owned (non menu) values keep their own defaults.
            bool presetOwned = runtime.RippleLayers == 4 && runtime.DripBudget == 512;
            // 12. SsrOnlyWhileWet (bool key): default on, set false/save/reload, type check, reset, older file, and the policy math.
            string ssrPath = path + ".ssr.tres";
            Reset(runtime);
            bool ssr = runtime.SsrOnlyWhileWet && new RingWetWorldSettings().SsrOnlyWhileWet;
            var ssrDefaults = new GameplayWetWorldDefaults(ssrPath) { LegacySurfaceTerrainPath = legacyPath }; ssrDefaults.Initialize();
            ssr &= ssrDefaults.SetOverride("SsrOnlyWhileWet", false) && ssrDefaults.Dirty && !runtime.SsrOnlyWhileWet && !ssrDefaults.SetOverride("SsrOnlyWhileWet", false) && ssrDefaults.Save();
            Reset(runtime);
            var ssrLoaded = new GameplayWetWorldDefaults(ssrPath) { LegacySurfaceTerrainPath = legacyPath }; ssrLoaded.Initialize();
            ssr &= !runtime.SsrOnlyWhileWet && !RingWetWorldSettings.Current.SsrOnlyWhileWet;
            ssr &= !GameplayWetWorldDefaults.TryValidate("SsrOnlyWhileWet", 1, out _) && !GameplayWetWorldDefaults.TryValidate("SsrOnlyWhileWet", 1.0f, out _)
                && !GameplayWetWorldDefaults.TryValidate("SsrOnlyWhileWet", "true", out _) && GameplayWetWorldDefaults.TryValidate("SsrOnlyWhileWet", false, out Variant ssrClean) && !ssrClean.AsBool()
                && !ssrLoaded.SetOverride("SsrOnlyWhileWet", 1);
            ssrLoaded.Reset();
            ssr &= runtime.SsrOnlyWhileWet && ssrLoaded.Save();
            var ssrOld = new GameplayWetWorldSettings(); ssrOld.Overrides["LensDroplets"] = 0.4f;
            ResourceSaver.Save(ssrOld, ssrPath);
            Reset(runtime);
            new GameplayWetWorldDefaults(ssrPath) { LegacySurfaceTerrainPath = legacyPath }.Initialize();
            ssr &= runtime.SsrOnlyWhileWet;
            if (File.Exists(ProjectSettings.GlobalizePath(ssrPath))) File.Delete(ProjectSettings.GlobalizePath(ssrPath));
            bool ssrMath = RingWetWorld.SsrFadeExtraExponent(1f) == 0f && RingWetWorld.SsrFadeExtraExponent(2f) == 0f && float.IsFinite(RingWetWorld.SsrFadeExtraExponent(0f))
                && RingWetWorld.SsrFadeExtraExponent(0.5f) > 0f && RingWetWorld.SsrFadeExtraExponent(0.25f) > RingWetWorld.SsrFadeExtraExponent(0.5f)
                && RingWetWorld.SsrFadeExtraExponent(0.05f) > RingWetWorld.SsrFadeExtraExponent(0.25f) && RingWetWorld.SsrFadeExtraExponent(0f) >= RingWetWorld.SsrFadeExtraExponent(0.05f);
            float stepped = 0f;
            for (int i = 0; i < 30; i++) stepped = RingWetWorld.StepSsrWeight(stepped, 1f, 0.1f, 3f);
            ssrMath &= Math.Abs(stepped - 1f) < 1e-4f && RingWetWorld.StepSsrWeight(0.9f, 1f, 10f, 3f) == 1f && RingWetWorld.StepSsrWeight(0.1f, 0f, 10f, 3f) == 0f
                && Math.Abs(RingWetWorld.StepSsrWeight(1f, 0f, 1.5f, 3f) - 0.5f) < 1e-5f && RingWetWorld.StepSsrWeight(float.NaN, 0.5f, 0.1f, 3f) >= 0f
                && float.IsFinite(RingWetWorld.StepSsrWeight(0.5f, float.NaN, float.NaN, 0f)) && RingWetWorld.StepSsrWeight(0.5f, 2f, 100f, 3f) == 1f;
            // Wet gate hysteresis.
            bool gate = RingWetWorld.StepSsrWetGate(false, 0.049f, 0f, out _, 0.5f) == false && RingWetWorld.StepSsrWetGate(false, 0.05f, 0f, out float held0, 0.5f) && held0 == 0f;
            bool gateOn = true; float held = 0f; int steps = 0;
            gateOn = RingWetWorld.StepSsrWetGate(gateOn, 0.03f, held, out held, 0.5f) && held == 0f; // stays on at 0.03
            for (int i = 0; i < 19; i++) gateOn = RingWetWorld.StepSsrWetGate(gateOn, 0.01f, held, out held, 0.5f);
            gate &= gateOn && held >= 9.4f; // 9.5 s below 0.02: still on
            gateOn = RingWetWorld.StepSsrWetGate(gateOn, 0.03f, held, out held, 0.5f);
            gate &= gateOn && held == 0f; // timer reset
            for (int i = 0; i < 19; i++) { gateOn = RingWetWorld.StepSsrWetGate(gateOn, 0.0f, held, out held, 0.5f); steps++; }
            gate &= gateOn;
            gateOn = RingWetWorld.StepSsrWetGate(gateOn, 0.0f, held, out held, 0.5f);
            gate &= !gateOn && steps == 19; // off after 10 s
            gateOn = true; held = 0f;
            for (int i = 0; i < 20; i++) gateOn = RingWetWorld.StepSsrWetGate(gateOn, float.NaN, held, out held, 0.5f);
            gate &= !gateOn && !RingWetWorld.StepSsrWetGate(false, float.NaN, 0f, out _, 0.5f);
            ssrMath &= gate;
            bool pass = ssr && ssrMath && missing && cleanSave && changed && setAll && saved && roundTrip && clamps && ranges && sanitize && reset && failure && unknown && oldFile && migration && presetOwned;
            return new() { ["pass"] = pass, ["detail"] = $"missing={missing} clean_save={cleanSave} changed={changed} set_all={setAll} saved={saved} roundtrip={roundTrip} clamps={clamps} ranges={ranges} sanitize={sanitize} reset={reset} failure_retry={failure} unknown={unknown} old_file={oldFile} migration={migration} preset_owned={presetOwned} ssr_setting={ssr} ssr_math={ssrMath}" };
        }
        finally
        {
            RingWetWorldSettings.Current = original;
            if (File.Exists(absolute)) File.Delete(absolute);
        }
    }

    /// <summary>Drives the real drawer controls (Clouds tab, "Wet world" group): slider, entry, save, reset.</summary>
    public bool RunControlBindings(RingLiveTuningDrawer drawer)
    {
        foreach (string key in Keys)
            if (drawer.FindChild("WetWorld" + key, true, false) is not HSlider || drawer.FindChild("WetWorld" + key + "Entry", true, false) is not SpinBox) return false;
        if (drawer.FindChild("WetWorldSaveNow", true, false) == null || drawer.FindChild("WetWorldResetDefaults", true, false) == null) return false;
        var puddles = (HSlider)drawer.FindChild("WetWorldPuddles", true, false)!;
        var entry = (SpinBox)drawer.FindChild("WetWorldPuddlesEntry", true, false)!;
        var gloss = (HSlider)drawer.FindChild("WetWorldWetGloss", true, false)!;
        puddles.Value = 0; gloss.Value = 0.3;
        bool edits = RingWetWorldSettings.Current.Puddles == 0f && entry.Value == 0 && Math.Abs(RingWetWorldSettings.Current.WetGloss - 0.3f) < 1e-5f && drawer.FlushWetWorldDefaults();
        if (drawer.FindChild("WetWorldSsrOnlyWhileWet", true, false) is not CheckButton ssrToggle || !ssrToggle.ButtonPressed) return false;
        ssrToggle.ButtonPressed = false;
        edits &= !RingWetWorldSettings.Current.SsrOnlyWhileWet && drawer.FlushWetWorldDefaults();
        drawer.ResetWetWorldSettings();
        edits &= RingWetWorldSettings.Current.SsrOnlyWhileWet && ssrToggle.ButtonPressed;
        return edits && Math.Abs(puddles.Value - 1.0) < 1e-5 && Math.Abs(gloss.Value - 0.18) < 1e-5 && !drawer.GetWetWorldTuningDiagnostics()["dirty"].AsBool();
    }
}
