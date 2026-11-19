using System;
using Godot;
using RingworldTechDemo.Atmosphere;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Settings smoke for the Sun group (LF-1): "Sun glare" (SunGlare, 0-1, default 1) and "Lens flare" (LensFlare, 0-1, default 0): defaults, ranges, NaN / out-of-range sanitize,
/// save / load round trip, older files without the keys, apply reaching <see cref="RingSunGlare"/>, and that the debug GlareSourceGain is never persisted.
/// Run: via tools/lens_settings_smoke_test.gd (headless). Prints LENS_SETTINGS_SMOKE=PASS.
/// </summary>
public partial class LensSettingsSmoke : RefCounted
{
    public string Run(string path)
    {
        try
        {
            string abs = ProjectSettings.GlobalizePath(path);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(abs)!);
            void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
            bool Near(float a, float b) => Math.Abs(a - b) < 1e-5f;

            var defaults = new AtmosphereVisualDefaults(path);
            defaults.Initialize();   // missing file: defaults
            Require(Near(defaults.Settings.SunGlare, 1.0f) && Near(defaults.Settings.LensFlare, 0.0f), "defaults are not 1 and 0");
            Require(Near(RingSunGlare.Strength, 1.0f) && Near(RingSunGlare.LensFlare, 0.0f), "apply did not reach RingSunGlare at defaults");
            Require(Near(new AtmosphereVisualSettings().SunGlare, 1.0f) && Near(new AtmosphereVisualSettings().LensFlare, 0.0f), "settings initialisers are not the defaults");

            defaults.Set("SunGlare", 0.4f); defaults.Set("LensFlare", 0.25f);
            Require(Near(RingSunGlare.Strength, 0.4f) && Near(RingSunGlare.LensFlare, 0.25f), "Set did not reach RingSunGlare");
            defaults.Set("SunGlare", float.NaN); defaults.Set("LensFlare", float.NaN);
            Require(Near(defaults.Settings.SunGlare, 1.0f) && Near(defaults.Settings.LensFlare, 0.0f), "NaN did not fall back to the defaults");
            defaults.Set("SunGlare", 7.0f); defaults.Set("LensFlare", 7.0f);
            Require(Near(defaults.Settings.SunGlare, 1.0f) && Near(defaults.Settings.LensFlare, 1.0f), "above-range values were not clamped to 1");
            defaults.Set("SunGlare", -3.0f); defaults.Set("LensFlare", float.NegativeInfinity);
            Require(Near(defaults.Settings.SunGlare, 0.0f) && Near(defaults.Settings.LensFlare, 0.0f), "below-range values were not clamped to 0");
            Require(Near(RingSunGlare.Strength, 0.0f), "strength 0 did not reach RingSunGlare");

            RingSunGlare.GlareSourceGain = 30.0f;
            defaults.Set("SunGlare", 0.37f); defaults.Set("LensFlare", 0.62f);
            Require(defaults.Save(), "save failed: " + defaults.LastError);
            string text = System.IO.File.ReadAllText(abs);
            Require(!text.Contains("GlareSourceGain", StringComparison.OrdinalIgnoreCase), "GlareSourceGain was persisted");
            Require(typeof(AtmosphereVisualSettings).GetProperty("GlareSourceGain") == null, "GlareSourceGain is a settings property");
            RingSunGlare.GlareSourceGain = 1.0f;
            RingSunGlare.SetSettings(1.0f, 0.0f);
            var reload = new AtmosphereVisualDefaults(path);
            reload.Initialize();
            Require(Near(reload.Settings.SunGlare, 0.37f) && Near(reload.Settings.LensFlare, 0.62f), "round trip lost the values");
            Require(Near(RingSunGlare.Strength, 0.37f) && Near(RingSunGlare.LensFlare, 0.62f), "load did not apply to RingSunGlare");

            // An older file without the keys loads the defaults.
            string stripped = string.Join('\n', Array.FindAll(text.Split('\n'), line => !line.StartsWith("SunGlare") && !line.StartsWith("LensFlare")));
            System.IO.File.WriteAllText(abs, stripped);
            var old = new AtmosphereVisualDefaults(path);
            old.Initialize();
            Require(Near(old.Settings.SunGlare, 1.0f) && Near(old.Settings.LensFlare, 0.0f), "old file without the keys did not load the defaults");

            // Corrupt values in the file are sanitised on load.
            System.IO.File.WriteAllText(abs, text.Replace("SunGlare = 0.37", "SunGlare = 9.0").Replace("LensFlare = 0.62", "LensFlare = -2.0"));
            var dirty = new AtmosphereVisualDefaults(path);
            dirty.Initialize();
            Require(Near(dirty.Settings.SunGlare, 1.0f) && Near(dirty.Settings.LensFlare, 0.0f), "out-of-range file values were not sanitised");

            reload.Reset();
            Require(Near(reload.Settings.SunGlare, 1.0f) && Near(reload.Settings.LensFlare, 0.0f) && Near(RingSunGlare.Strength, 1.0f), "reset did not restore the defaults");
            return "PASS";
        }
        catch (Exception e) { return "FAIL " + e.Message; }
        finally { RingSunGlare.GlareSourceGain = 1.0f; RingSunGlare.SetSettings(1.0f, 0.0f); }
    }

    public Vector2 ReadStatics() => new(RingSunGlare.Strength, RingSunGlare.LensFlare);
}
