using System;
using System.Text.RegularExpressions;
using Godot;
using RingworldTechDemo.UI;

/// <summary>Isolated persistence evidence for the weather audio preferences (defaults, round trip, clamps, reset, invalid path, older files).</summary>
public partial class WeatherAudioSettingsSmoke : Node
{
    public string Result { get; private set; } = "RUNNING";
    public string IsolatedPath { get; set; } = "res://.godot/weather_audio_settings_smoke.tres";
    public override void _Ready()
    {
        try { Run(IsolatedPath); Result = "PASS"; }
        catch (Exception error) { Result = "FAIL " + error.Message; }
    }

    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static bool Near(float a, float b) => Mathf.IsEqualApprox(a, b);

    private static bool IsDefault(GameplayWeatherAudioSettings s) =>
        s.ThunderEnabled && Near(s.ThunderVolumeDb, -8) && s.AmbienceEnabled && Near(s.RainVolumeDb, -10) && Near(s.WindVolumeDb, -13)
        && Near(s.DistantStormVolumeDb, -18) && Near(s.SpatialWidth, .6f) && Near(s.NearDetailVolumeDb, 0);

    private static void Run(string path)
    {
        // 1. missing file gives defaults; 2. a clean save is a no-op.
        var first = new GameplayWeatherAudioDefaults(path); first.Load();
        Require(IsDefault(first.Settings) && !first.HasSavedFile, "missing preferences did not use defaults");
        Require(first.Save() && !first.HasSavedFile, "clean defaults should save as a no-op");
        // 3. changed-flag semantics.
        Require(first.SetThunderEnabled(false) && !first.SetThunderEnabled(false), "Set did not report the changed flag");
        Require(first.SetSpatialWidth(.9f) && !first.SetSpatialWidth(.9f), "float Set did not report the changed flag");
        // 4. set, save, reload, compare.
        first.SetThunderVolumeDb(-20); first.SetAmbienceEnabled(false); first.SetRainVolumeDb(-30); first.SetWindVolumeDb(-5); first.SetDistantStormVolumeDb(-40); first.SetNearDetailVolumeDb(-12);
        Require(first.Dirty && first.Save() && !first.Dirty && first.HasSavedFile, "preferences did not save");
        var second = new GameplayWeatherAudioDefaults(path); second.Load();
        GameplayWeatherAudioSettings s = second.Settings;
        Require(!s.ThunderEnabled && Near(s.ThunderVolumeDb, -20) && !s.AmbienceEnabled && Near(s.RainVolumeDb, -30) && Near(s.WindVolumeDb, -5)
            && Near(s.DistantStormVolumeDb, -40) && Near(s.SpatialWidth, .9f) && Near(s.NearDetailVolumeDb, -12), "preferences did not round-trip");
        // 5. clamps and NaN fallback through RangeOf.
        var (tMin, tMax) = GameplayWeatherAudioDefaults.RangeOf(nameof(GameplayWeatherAudioSettings.ThunderVolumeDb));
        var (wMin, wMax) = GameplayWeatherAudioDefaults.RangeOf(nameof(GameplayWeatherAudioSettings.SpatialWidth));
        var (nMin, nMax) = GameplayWeatherAudioDefaults.RangeOf(nameof(GameplayWeatherAudioSettings.NearDetailVolumeDb));
        Require(Near(tMin, -48) && Near(tMax, 12) && Near(wMin, .2f) && Near(wMax, 1f) && Near(nMin, -24) && Near(nMax, 6), "ranges differ from the design table");
        second.SetThunderVolumeDb(-999); Require(Near(second.Settings.ThunderVolumeDb, tMin), "thunder lower bound");
        second.SetThunderVolumeDb(999); Require(Near(second.Settings.ThunderVolumeDb, tMax), "thunder upper bound");
        second.SetRainVolumeDb(99); Require(Near(second.Settings.RainVolumeDb, 6), "rain upper bound");
        second.SetSpatialWidth(0); Require(Near(second.Settings.SpatialWidth, wMin), "width lower bound");
        second.SetSpatialWidth(5); Require(Near(second.Settings.SpatialWidth, wMax), "width upper bound");
        second.SetNearDetailVolumeDb(-99); Require(Near(second.Settings.NearDetailVolumeDb, nMin), "near detail lower bound");
        second.SetWindVolumeDb(float.NaN); Require(Near(second.Settings.WindVolumeDb, -13), "NaN wind did not fall back");
        second.SetDistantStormVolumeDb(float.PositiveInfinity); Require(Near(second.Settings.DistantStormVolumeDb, -18), "infinite distant storm did not fall back");
        // 6. Sanitize.
        using var dirty = new GameplayWeatherAudioSettings { ThunderVolumeDb = 99, RainVolumeDb = float.NaN, WindVolumeDb = -99, DistantStormVolumeDb = float.NegativeInfinity, SpatialWidth = 9, NearDetailVolumeDb = -99, ThunderEnabled = false, AmbienceEnabled = false };
        using var clean = GameplayWeatherAudioDefaults.Sanitize(dirty);
        Require(Near(clean.ThunderVolumeDb, 12) && Near(clean.RainVolumeDb, -10) && Near(clean.WindVolumeDb, -48) && Near(clean.DistantStormVolumeDb, -18)
            && Near(clean.SpatialWidth, 1) && Near(clean.NearDetailVolumeDb, -24) && !clean.ThunderEnabled && !clean.AmbienceEnabled, "Sanitize bounds were not enforced");
        // 7. reset round-trip.
        second.Reset();
        Require(second.Save(), "reset did not save");
        var reset = new GameplayWeatherAudioDefaults(path); reset.Load();
        Require(IsDefault(reset.Settings), "reset did not restore defaults");
        // 8. an invalid path fails safely.
        string invalidParent = path + ".invalid-parent.tres";
        Require(ResourceSaver.Save(new GameplayWeatherAudioSettings(), invalidParent) == Error.Ok, "invalid-path fixture write failed");
        var invalid = new GameplayWeatherAudioDefaults(invalidParent + "/child.tres"); invalid.Load(); invalid.SetRainVolumeDb(-20);
        Require(!invalid.Save() && invalid.LastError.Length > 0, "invalid preference path did not fail safely");
        // 9. an unknown field throws.
        bool threw = false;
        try { invalid.SetFloat("NoSuchField", 1); } catch (ArgumentException) { threw = true; }
        bool threwBool = false;
        try { invalid.SetBool("NoSuchField", true); } catch (ArgumentException) { threwBool = true; }
        Require(threw && threwBool, "unknown field did not throw");
        // 10. an older file without the new fields loads their defaults: strip SpatialWidth and NearDetailVolumeDb from a saved file.
        var older = new GameplayWeatherAudioDefaults(path); older.Load();
        older.SetRainVolumeDb(-25); older.SetSpatialWidth(.3f); older.SetNearDetailVolumeDb(4);
        Require(older.Save(), "older fixture save");
        using (var file = FileAccess.Open(path, FileAccess.ModeFlags.Read))
        {
            Require(file != null, "older fixture read");
            string text = Regex.Replace(file!.GetAsText(), @"^(SpatialWidth|NearDetailVolumeDb) = .*\r?\n", "", RegexOptions.Multiline);
            file.Close();
            using var write = FileAccess.Open(path, FileAccess.ModeFlags.Write);
            Require(write != null, "older fixture rewrite");
            write!.StoreString(text);
        }
        var legacy = new GameplayWeatherAudioDefaults(path); legacy.Load();
        Require(Near(legacy.Settings.RainVolumeDb, -25) && Near(legacy.Settings.SpatialWidth, .6f) && Near(legacy.Settings.NearDetailVolumeDb, 0), "older file did not load the defaults for the new fields");
    }
}
