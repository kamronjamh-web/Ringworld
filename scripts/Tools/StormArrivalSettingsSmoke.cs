using System;
using System.IO;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// WO-A3 settings smoke (the live-tuning-setting skill's ten checks) for the storm arrival group: GustFrontStrength, GustFrontLeadSeconds, Hail,
/// PostRainMist, Rainbows. Run: tools/storm_arrival_settings_smoke_test.gd. Prints STORM_ARRIVAL_SETTINGS=PASS.
/// </summary>
[GlobalClass]
public partial class StormArrivalSettingsSmoke : RefCounted
{
    static readonly string[] Keys = { "GustFrontStrength", "GustFrontLeadSeconds", "Hail", "PostRainMist", "Rainbows" };

    public Godot.Collections.Dictionary Run(string path)
    {
        if (!path.StartsWith("res://.godot/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
            return new() { ["pass"] = false, ["detail"] = "isolated path required" };
        Directory.CreateDirectory(Path.GetDirectoryName(ProjectSettings.GlobalizePath(path))!);
        string absolute = ProjectSettings.GlobalizePath(path);
        if (File.Exists(absolute)) File.Delete(absolute);
        RingStormArrivalSettings original = RingStormArrivalSettings.Current;
        using var runtime = new RingStormArrivalSettings();
        RingStormArrivalSettings.Current = runtime;
        try
        {
            // 1. missing file gives defaults
            var defaults = new GameplayStormArrivalDefaults(path); defaults.Initialize();
            bool missing = runtime.GustFrontStrength == 1f && runtime.GustFrontLeadSeconds == 60f && runtime.Hail == 1f && runtime.PostRainMist == 1f && runtime.Rainbows == 1f
                && !defaults.Dirty && defaults.Settings.Overrides.Count == 0;
            // 2. a clean save is a no-op
            bool cleanSave = defaults.Save() && !File.Exists(absolute);
            // 3. changed-flag semantics
            bool changed = defaults.SetOverride("Hail", 0f) && !defaults.SetOverride("Hail", 0f) && defaults.Dirty && runtime.Hail == 0f;
            // 4. set, save, reload, compare
            bool setAll = defaults.SetOverride("GustFrontStrength", 1.5f) && defaults.SetOverride("GustFrontLeadSeconds", 90f)
                && defaults.SetOverride("PostRainMist", 0.25f) && defaults.SetOverride("Rainbows", 0f);
            bool saved = defaults.Save() && !defaults.Dirty && File.Exists(absolute);
            runtime.GustFrontStrength = 1f; runtime.GustFrontLeadSeconds = 60f; runtime.Hail = 1f; runtime.PostRainMist = 1f; runtime.Rainbows = 1f;
            var loaded = new GameplayStormArrivalDefaults(path); loaded.Initialize();
            bool roundTrip = Math.Abs(runtime.GustFrontStrength - 1.5f) < 1e-5f && runtime.GustFrontLeadSeconds == 90f && runtime.Hail == 0f
                && Math.Abs(runtime.PostRainMist - 0.25f) < 1e-5f && runtime.Rainbows == 0f;
            // 5. clamps and NaN fallback
            bool clamps = true;
            foreach (string key in Keys)
            {
                var range = GameplayStormArrivalDefaults.Limits[key];
                clamps &= GameplayStormArrivalDefaults.TryValidate(key, 1e9, out Variant hi) && hi.AsSingle() == range.Max;
                clamps &= GameplayStormArrivalDefaults.TryValidate(key, -1e9, out Variant lo) && lo.AsSingle() == range.Min;
                clamps &= !GameplayStormArrivalDefaults.TryValidate(key, double.NaN, out _) && !GameplayStormArrivalDefaults.TryValidate(key, double.PositiveInfinity, out _)
                    && !GameplayStormArrivalDefaults.TryValidate(key, "text", out _) && !loaded.SetOverride(key, double.NaN);
            }
            // The exported property ranges must match the table (the drawer sliders use the table).
            bool ranges = true;
            foreach (string key in Keys)
            {
                var range = GameplayStormArrivalDefaults.Limits[key];
                foreach (var property in runtime.GetPropertyList())
                    if (property["name"].AsString() == key)
                    {
                        string[] hint = property["hint_string"].AsString().Split(',');
                        ranges &= float.Parse(hint[0], System.Globalization.CultureInfo.InvariantCulture) == range.Min
                            && float.Parse(hint[1], System.Globalization.CultureInfo.InvariantCulture) == range.Max;
                    }
            }
            // 6. Sanitize: an edited file with out-of-range, non-finite and wrongly typed values loads clamped or ignored.
            string dirtyPath = path + ".dirty.tres";
            var dirty = new GameplayStormArrivalSettings();
            dirty.Overrides["GustFrontStrength"] = 99f; dirty.Overrides["Hail"] = double.NaN; dirty.Overrides["Rainbows"] = "x"; dirty.Overrides["Bogus"] = 1f;
            ResourceSaver.Save(dirty, dirtyPath);
            runtime.GustFrontStrength = 1f; runtime.Hail = 1f; runtime.Rainbows = 1f;
            var sanitized = new GameplayStormArrivalDefaults(dirtyPath); sanitized.Initialize();
            bool sanitize = runtime.GustFrontStrength == 2f && runtime.Hail == 1f && runtime.Rainbows == 1f && !sanitized.Settings.Overrides.ContainsKey("Bogus") && sanitized.LastError.Length > 0;
            File.Delete(ProjectSettings.GlobalizePath(dirtyPath));
            // 7. reset round-trip
            loaded.Reset();
            bool reset = runtime.GustFrontStrength == 1f && runtime.GustFrontLeadSeconds == 60f && runtime.Hail == 1f && runtime.PostRainMist == 1f && runtime.Rainbows == 1f
                && loaded.Settings.Overrides.Count == 0 && loaded.Save();
            var afterReset = new GameplayStormArrivalDefaults(path); afterReset.Initialize();
            reset &= runtime.Hail == 1f && afterReset.Settings.Overrides.Count == 0;
            // 8. an invalid path fails safely and keeps the pending change
            var blocked = new GameplayStormArrivalDefaults(path + "/blocked.tres"); blocked.Initialize(); blocked.SetOverride("Hail", 0.5f);
            bool failure = !blocked.Save() && blocked.Dirty && blocked.LastError.Length > 0;
            // 9. an unknown key is rejected
            bool unknown = !loaded.SetOverride("PostRainMistTypo", 1f) && !loaded.SetOverride("", 1f);
            // 10. an older file without a key loads the default for it
            string oldPath = path + ".old.tres";
            var old = new GameplayStormArrivalSettings(); old.Overrides["Rainbows"] = 0.4f;
            ResourceSaver.Save(old, oldPath);
            runtime.GustFrontStrength = 1f; runtime.GustFrontLeadSeconds = 60f; runtime.Hail = 1f; runtime.PostRainMist = 1f; runtime.Rainbows = 1f;
            var older = new GameplayStormArrivalDefaults(oldPath); older.Initialize();
            bool oldFile = Math.Abs(runtime.Rainbows - 0.4f) < 1e-5f && runtime.GustFrontStrength == 1f && runtime.GustFrontLeadSeconds == 60f && runtime.Hail == 1f && runtime.PostRainMist == 1f;
            File.Delete(ProjectSettings.GlobalizePath(oldPath));
            // ToTuning pushes the settings to the maths.
            runtime.GustFrontLeadSeconds = 120f; runtime.Hail = 0.5f;
            StormArrivalTuning tuning = runtime.ToTuning(0.18f, 0.46f);
            bool tuningPush = tuning.GustFrontLeadSeconds == 120f && tuning.Hail == 0.5f && tuning.PrecipitationStart == 0.18f && tuning.BaseCoverage == 0.46f;
            bool pass = missing && cleanSave && changed && setAll && saved && roundTrip && clamps && ranges && sanitize && reset && failure && unknown && oldFile && tuningPush;
            return new() { ["pass"] = pass, ["detail"] = $"missing={missing} clean_save={cleanSave} changed={changed} set_all={setAll} saved={saved} roundtrip={roundTrip} clamps={clamps} ranges={ranges} sanitize={sanitize} reset={reset} failure_retry={failure} unknown={unknown} old_file={oldFile} tuning={tuningPush}" };
        }
        finally
        {
            RingStormArrivalSettings.Current = original;
            if (File.Exists(absolute)) File.Delete(absolute);
        }
    }

    /// <summary>Drives the real drawer controls (Wind tab, "Storm arrival" group): slider, entry, save, reset.</summary>
    public bool RunControlBindings(RingLiveTuningDrawer drawer)
    {
        foreach (string key in Keys)
            if (drawer.FindChild("StormArrival" + key, true, false) is not HSlider || drawer.FindChild("StormArrival" + key + "Entry", true, false) is not SpinBox) return false;
        if (drawer.FindChild("StormArrivalSaveNow", true, false) == null || drawer.FindChild("StormArrivalResetDefaults", true, false) == null) return false;
        var hail = (HSlider)drawer.FindChild("StormArrivalHail", true, false)!;
        var entry = (SpinBox)drawer.FindChild("StormArrivalHailEntry", true, false)!;
        var lead = (HSlider)drawer.FindChild("StormArrivalGustFrontLeadSeconds", true, false)!;
        hail.Value = 0; lead.Value = 100;
        bool edits = RingStormArrivalSettings.Current.Hail == 0f && entry.Value == 0 && RingStormArrivalSettings.Current.GustFrontLeadSeconds == 100f && drawer.FlushStormArrivalDefaults();
        drawer.ResetStormArrivalSettings();
        return edits && Math.Abs(hail.Value - 1.0) < 1e-5 && Math.Abs(lead.Value - 60.0) < 1e-5 && !drawer.GetStormArrivalTuningDiagnostics()["dirty"].AsBool();
    }
}
