using System;
using System.IO;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Megastorm WO2 settings smoke (the live-tuning-setting skill checks) for SpeedMetersPerSecond (5-500, 350), TrailingLengthKm (100-600, 340) and
/// WarningReachKm (50-600, 220), plus the drawer controls in the Clouds tab. Run: tools/megastorm_settings_smoke_test.gd. Prints MEGASTORM_SETTINGS=PASS.
/// </summary>
[GlobalClass]
public partial class MegastormSettingsSmoke : RefCounted
{
    static readonly string[] Keys = { "SpeedMetersPerSecond", "TrailingLengthKm", "WarningReachKm", "SoundVolumeDb" };

    private static bool Defaults(RingMegastormSettings r) => r.SpeedMetersPerSecond == 350f && r.TrailingLengthKm == 340f && r.WarningReachKm == 220f && r.SoundVolumeDb == 0f;

    public Godot.Collections.Dictionary Run(string path)
    {
        if (!path.StartsWith("res://.godot/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
            return new() { ["pass"] = false, ["detail"] = "isolated path required" };
        Directory.CreateDirectory(Path.GetDirectoryName(ProjectSettings.GlobalizePath(path))!);
        string absolute = ProjectSettings.GlobalizePath(path);
        if (File.Exists(absolute)) File.Delete(absolute);
        RingMegastormSettings original = RingMegastormSettings.Current;
        using var runtime = new RingMegastormSettings();
        RingMegastormSettings.Current = runtime;
        try
        {
            // 1. missing file gives defaults
            var defaults = new GameplayMegastormDefaults(path); defaults.Initialize();
            bool missing = Defaults(runtime) && !defaults.Dirty && defaults.Settings.Overrides.Count == 0;
            // 2. a clean save is a no-op
            bool cleanSave = defaults.Save() && !File.Exists(absolute);
            // 3. changed-flag semantics
            bool changed = defaults.SetOverride("SpeedMetersPerSecond", 120f) && !defaults.SetOverride("SpeedMetersPerSecond", 120f) && defaults.Dirty && runtime.SpeedMetersPerSecond == 120f;
            // 4. set, save, reload, compare
            bool setAll = defaults.SetOverride("TrailingLengthKm", 250f) && defaults.SetOverride("WarningReachKm", 90f) && defaults.SetOverride("SoundVolumeDb", -6f);
            bool saved = defaults.Save() && !defaults.Dirty && File.Exists(absolute);
            runtime.SpeedMetersPerSecond = 350f; runtime.TrailingLengthKm = 340f; runtime.WarningReachKm = 220f; runtime.SoundVolumeDb = 0f;
            var loaded = new GameplayMegastormDefaults(path); loaded.Initialize();
            bool roundTrip = runtime.SpeedMetersPerSecond == 120f && runtime.TrailingLengthKm == 250f && runtime.WarningReachKm == 90f && runtime.SoundVolumeDb == -6f;
            // 5. clamps and NaN fallback
            bool clamps = true;
            foreach (string key in Keys)
            {
                var range = GameplayMegastormDefaults.Limits[key];
                clamps &= GameplayMegastormDefaults.TryValidate(key, 1e9, out Variant hi) && hi.AsSingle() == range.Max;
                clamps &= GameplayMegastormDefaults.TryValidate(key, -1e9, out Variant lo) && lo.AsSingle() == range.Min;
                clamps &= !GameplayMegastormDefaults.TryValidate(key, double.NaN, out _) && !GameplayMegastormDefaults.TryValidate(key, double.PositiveInfinity, out _)
                    && !GameplayMegastormDefaults.TryValidate(key, "text", out _) && !loaded.SetOverride(key, double.NaN);
            }
            // The documented ranges and the exported property ranges must match the table (the drawer sliders use the table).
            bool ranges = GameplayMegastormDefaults.Limits["SpeedMetersPerSecond"] == (5f, 500f) && GameplayMegastormDefaults.Limits["TrailingLengthKm"] == (100f, 600f)
                && GameplayMegastormDefaults.Limits["WarningReachKm"] == (50f, 600f) && GameplayMegastormDefaults.Limits["SoundVolumeDb"] == (-24f, 6f);
            foreach (string key in Keys)
            {
                var range = GameplayMegastormDefaults.Limits[key];
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
            var dirty = new GameplayMegastormSettings();
            dirty.Overrides["SpeedMetersPerSecond"] = 9999f; dirty.Overrides["TrailingLengthKm"] = double.NaN; dirty.Overrides["WarningReachKm"] = "x"; dirty.Overrides["Bogus"] = 1f;
            ResourceSaver.Save(dirty, dirtyPath);
            runtime.SpeedMetersPerSecond = 350f; runtime.TrailingLengthKm = 340f; runtime.WarningReachKm = 220f;
            var sanitized = new GameplayMegastormDefaults(dirtyPath); sanitized.Initialize();
            bool sanitize = runtime.SpeedMetersPerSecond == 500f && runtime.TrailingLengthKm == 340f && runtime.WarningReachKm == 220f
                && !sanitized.Settings.Overrides.ContainsKey("Bogus") && sanitized.LastError.Length > 0;
            File.Delete(ProjectSettings.GlobalizePath(dirtyPath));
            // 7. reset round-trip
            loaded.Reset();
            bool reset = Defaults(runtime) && loaded.Settings.Overrides.Count == 0 && loaded.Save();
            var afterReset = new GameplayMegastormDefaults(path); afterReset.Initialize();
            reset &= Defaults(runtime) && afterReset.Settings.Overrides.Count == 0;
            // 8. an invalid path fails safely and keeps the pending change
            var blocked = new GameplayMegastormDefaults(path + "/blocked.tres"); blocked.Initialize(); blocked.SetOverride("WarningReachKm", 100f);
            bool failure = !blocked.Save() && blocked.Dirty && blocked.LastError.Length > 0;
            runtime.WarningReachKm = 220f;
            // 9. an unknown key is rejected
            bool unknown = !loaded.SetOverride("SpeedTypo", 1f) && !loaded.SetOverride("", 1f);
            // 10. an older file without a key loads the default for it
            string oldPath = path + ".old.tres";
            var old = new GameplayMegastormSettings(); old.Overrides["TrailingLengthKm"] = 400f;
            ResourceSaver.Save(old, oldPath);
            runtime.SpeedMetersPerSecond = 350f; runtime.TrailingLengthKm = 340f; runtime.WarningReachKm = 220f;
            var older = new GameplayMegastormDefaults(oldPath); older.Initialize();
            bool oldFile = runtime.TrailingLengthKm == 400f && runtime.SpeedMetersPerSecond == 350f && runtime.WarningReachKm == 220f;
            File.Delete(ProjectSettings.GlobalizePath(oldPath));
            // The runtime node pushes the values into the model: speed re-anchors, lengths convert km to metres. Entering the tree swaps in its own settings object.
            double clock = 100.0;
            var node = new RingMegastorm { ClockProvider = () => clock, MegastormPreferencesPath = path + ".node.tres" };
            ((SceneTree)Engine.GetMainLoop()).Root.AddChild(node);
            node.Trigger(new MegastormTrigger("smoke", 1.0e6, 1, 100.0));
            clock = 200.0;
            RingMegastormSettings live = RingMegastormSettings.Current;
            live.SpeedMetersPerSecond = 80f; live.TrailingLengthKm = 150f; live.WarningReachKm = 400f;
            node.ApplySettings();
            bool nodePush = node.Model.SpeedAt(200.0) == 80f && node.Model.Settings.TrailingLengthMeters == 150000.0 && node.Model.Settings.WarningReachMeters == 400000.0
                && node.Model.State.Anchors.Count == 2;
            node.QueueFree();
            try { File.Delete(ProjectSettings.GlobalizePath(path + ".node.tres")); } catch { }
            bool pass = missing && cleanSave && changed && setAll && saved && roundTrip && clamps && ranges && sanitize && reset && failure && unknown && oldFile && nodePush;
            return new() { ["pass"] = pass, ["detail"] = $"missing={missing} clean_save={cleanSave} changed={changed} set_all={setAll} saved={saved} roundtrip={roundTrip} clamps={clamps} ranges={ranges} sanitize={sanitize} reset={reset} failure_retry={failure} unknown={unknown} old_file={oldFile} node_push={nodePush}" };
        }
        finally
        {
            RingMegastormSettings.Current = original;
            if (File.Exists(absolute)) File.Delete(absolute);
        }
    }

    /// <summary>Drives the real drawer controls (Clouds tab, "Megastorm" group): slider, entry, save, reset.</summary>
    public bool RunControlBindings(RingLiveTuningDrawer drawer)
    {
        foreach (string key in Keys)
            if (drawer.FindChild("Megastorm" + key, true, false) is not HSlider || drawer.FindChild("Megastorm" + key + "Entry", true, false) is not SpinBox) return false;
        if (drawer.FindChild("MegastormSaveNow", true, false) == null || drawer.FindChild("MegastormResetDefaults", true, false) == null) return false;
        var speed = (HSlider)drawer.FindChild("MegastormSpeedMetersPerSecond", true, false)!;
        var entry = (SpinBox)drawer.FindChild("MegastormSpeedMetersPerSecondEntry", true, false)!;
        var reach = (HSlider)drawer.FindChild("MegastormWarningReachKm", true, false)!;
        if (speed.MinValue != 5 || speed.MaxValue != 500 || Math.Abs(speed.Value - 350) > 1e-5) return false;
        speed.Value = 120; reach.Value = 300;
        bool edits = RingMegastormSettings.Current.SpeedMetersPerSecond == 120f && entry.Value == 120 && RingMegastormSettings.Current.WarningReachKm == 300f && drawer.FlushMegastormDefaults();
        drawer.ResetMegastormSettings();
        return edits && Math.Abs(speed.Value - 350.0) < 1e-5 && Math.Abs(reach.Value - 220.0) < 1e-5 && !drawer.GetMegastormTuningDiagnostics()["dirty"].AsBool();
    }
}
