using System;
using System.IO;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Weather;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

[GlobalClass]
public partial class WindSettingsSmoke : RefCounted
{
    public Godot.Collections.Dictionary Run(string path)
    {
        if (!path.StartsWith("res://.godot/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
            return new() { ["pass"] = false, ["detail"] = "isolated path required" };
        Directory.CreateDirectory(Path.GetDirectoryName(ProjectSettings.GlobalizePath(path))!);
        RingWindSettings original = RingWindSettings.Current;
        using var runtime = new RingWindSettings();
        RingWindSettings.Current = runtime;
        try
        {
            var defaults = new GameplayWindDefaults(path); defaults.Initialize();
            float baseline = runtime.StormWindSpeed;
            bool oldMissingKeys = TestMissingTreeKeys(path, runtime);
            bool explicitValues = defaults.SetOverride("Enabled", false) && defaults.SetOverride("GustStrength", 0f)
                && defaults.SetOverride("RadarWindArrowEnabled", false)
                && defaults.SetOverride("TreeTrunkBendStrength", 0f) && defaults.SetOverride("TreeCanopyFlexStrength", 0f);
            bool invalidRejected = !defaults.SetOverride("StormWindSpeed", double.NaN)
                && !defaults.SetOverride("GustStrength", double.PositiveInfinity)
                && !defaults.SetOverride("Enabled", 0) && !defaults.SetOverride("unknown", 3)
                && !defaults.SetOverride("TreeTrunkBendStrength", double.NaN)
                && !defaults.SetOverride("TreeCanopyFlexStrength", double.PositiveInfinity);
            defaults.SetOverride("StormWindSpeed", double.MaxValue);
            bool bounded = runtime.StormWindSpeed == 80f
                && GameplayWindDefaults.TryValidate("TreeTrunkBendStrength", 99f, out Variant trunkMax) && trunkMax.AsSingle() == 1f
                && GameplayWindDefaults.TryValidate("TreeCanopyFlexStrength", -99f, out Variant canopyMin) && canopyMin.AsSingle() == 0f;
            bool saved = defaults.Save() && !defaults.Dirty;
            runtime.Enabled = true; runtime.GustStrength = 1; runtime.TreeTrunkBendStrength = .15f; runtime.TreeCanopyFlexStrength = 1f;
            var loaded = new GameplayWindDefaults(path); loaded.Initialize();
            bool roundTrip = !runtime.Enabled && runtime.GustStrength == 0 && !runtime.RadarWindArrowEnabled
                && runtime.TreeTrunkBendStrength == 0 && runtime.TreeCanopyFlexStrength == 0;
            defaults.ApplyDramaticStormPreset();
            bool preset = runtime.Enabled && runtime.StormWindSpeed >= 28 && runtime.GustStrength > .9f
                && runtime.TreeTrunkBendStrength == .15f && runtime.TreeCanopyFlexStrength == 1f;
            defaults.Reset();
            bool reset = runtime.Enabled && runtime.StormWindSpeed == baseline && runtime.TreeTrunkBendStrength == .15f
                && runtime.TreeCanopyFlexStrength == 1f && defaults.Settings.Overrides.Count == 0 && defaults.Save();
            defaults.SetOverride("GustStrength", .7f);
            var blocked = new GameplayWindDefaults(path + "/blocked.tres"); blocked.Initialize(); blocked.SetOverride("Enabled", false);
            bool failureRetained = !blocked.Save() && blocked.Dirty && blocked.LastError.Length > 0;
            bool migration = TestLegacyMigration(path, runtime);
            bool scopedGrass = TestScopedGrassReset(path);
            bool rainHaze = TestRainHazePreferences(path);
            return new() { ["pass"] = oldMissingKeys && explicitValues && invalidRejected && bounded && saved && roundTrip && preset && reset && failureRetained && migration && scopedGrass && rainHaze,
                ["detail"] = $"tree_defaults={oldMissingKeys} explicit={explicitValues} validation={invalidRejected} bounds={bounded} save={saved} roundtrip={roundTrip} preset={preset} reset={reset} failure_retry={failureRetained} migration={migration} scoped_grass={scopedGrass} rain_haze={rainHaze}" };
        }
        finally { RingWindSettings.Current = original; }
    }
    private static bool TestRainHazePreferences(string path)
    {
        string hazePath = path + ".rain-haze.tres";
        RingRainHazeSettings original = RingRainHazeSettings.Current;
        using var runtime = new RingRainHazeSettings();
        RingRainHazeSettings.Current = runtime;
        try
        {
            var prefs = new GameplayRainHazeDefaults(hazePath); prefs.Initialize();
            bool edits = prefs.SetOverride("Enabled", false) && prefs.SetOverride("Density", 0f)
                && prefs.SetOverride("NearClearDistanceMeters", 0f) && prefs.SetOverride("MaximumOpacity", 0f)
                && prefs.SetOverride("VariationStrength", 0f) && prefs.SetOverride("Tint", new Color(.2f,.3f,.4f,.25f));
            bool validation = !prefs.SetOverride("Density", double.NaN) && !prefs.SetOverride("Enabled", 1)
                && !prefs.SetOverride("Tint", new Color(float.PositiveInfinity,0,0))
                && !prefs.SetOverride("Tint", new Color(0,0,0,float.NaN))
                && GameplayRainHazeDefaults.TryValidate("MaximumOpacity", 50f, out Variant opacity) && opacity.AsSingle() == .9f;
            bool save = prefs.Save();
            runtime.Enabled = true; runtime.Density = .02f;
            var loaded = new GameplayRainHazeDefaults(hazePath); loaded.Initialize();
            bool roundtrip = !runtime.Enabled && runtime.Density == 0 && runtime.NearClearDistanceMeters == 0
                && runtime.MaximumOpacity == 0 && runtime.VariationStrength == 0 && runtime.Tint == new Color(.2f,.3f,.4f);
            prefs.Reset();
            bool reset = runtime.Enabled && runtime.Density == .004f && runtime.NearClearDistanceMeters == 8 && prefs.Save();
            var blocked = new GameplayRainHazeDefaults(hazePath + "/blocked.tres"); blocked.Initialize(); blocked.SetOverride("Enabled", false);
            bool failure = !blocked.Save() && blocked.Dirty && blocked.LastError.Length > 0;
            return edits && validation && save && roundtrip && reset && failure;
        }
        finally { RingRainHazeSettings.Current = original; File.Delete(ProjectSettings.GlobalizePath(hazePath)); }
    }
    public bool RunRainHazeControlBindings(RingLiveTuningDrawer drawer)
    {
        var enabled = drawer.FindChild("RainHazeEnabled", true, false) as CheckButton;
        var density = drawer.FindChild("RainHazeDensity", true, false) as HSlider;
        var entry = drawer.FindChild("RainHazeDensityEntry", true, false) as SpinBox;
        var tint = drawer.FindChild("RainHazeTint", true, false) as ColorPickerButton;
        if (enabled == null || density == null || entry == null || tint == null || tint.EditAlpha) return false;
        enabled.ButtonPressed = false; density.Value = 0;
        tint.Color = new Color(.3f,.4f,.5f,.25f); tint.EmitSignal(ColorPickerButton.SignalName.ColorChanged, tint.Color);
        bool edits = !RingRainHazeSettings.Current.Enabled && RingRainHazeSettings.Current.Density == 0 && entry.Value == 0
            && RingRainHazeSettings.Current.Tint == new Color(.3f,.4f,.5f,1f) && tint.Color.A == 1f && drawer.FlushRainHazeDefaults();
        drawer.ResetRainHazeSettings();
        return edits && enabled.ButtonPressed && Math.Abs(density.Value - .004) < .00001
            && !drawer.GetRainHazeTuningDiagnostics()["dirty"].AsBool();
    }
    private static bool TestScopedGrassReset(string path)
    {
        string grassPath = path + ".scoped-grass.tres";
        using var graphics = new RingGraphicsQualityController();
        try
        {
            var prefs = new GameplayGrassDefaults(grassPath); prefs.Initialize(graphics);
            foreach (string key in GameplayGrassDefaults.WindResponseKeys)
                prefs.SetOverride(graphics, key, key == "EnableGrassGustTexture" ? (Variant)false : 0f);
            prefs.SetOverride(graphics, "BladesPerSquareMeter", 21f);
            prefs.Reset(graphics);
            bool grassPreservesWind = prefs.Settings.Overrides.Count == 5
                && !prefs.Settings.Overrides["EnableGrassGustTexture"].AsBool()
                && prefs.Settings.Overrides["GrassWindGustMultiplier"].AsSingle() == 0
                && prefs.Settings.Overrides["GrassGustBaseBreeze"].AsSingle() == 0 && prefs.Save();
            var loaded = new GameplayGrassDefaults(grassPath); loaded.Initialize(graphics);
            bool roundtrip = loaded.Settings.Overrides.Count == 5 && !loaded.Settings.Overrides["EnableGrassGustTexture"].AsBool();
            loaded.SetOverride(graphics, "BladesPerSquareMeter", 21f);
            loaded.ResetWindResponse(graphics);
            bool windPreservesDensity = loaded.Settings.Overrides.Count == 1
                && loaded.Settings.Overrides["BladesPerSquareMeter"].AsSingle() == 21 && loaded.Save();
            return grassPreservesWind && roundtrip && windPreservesDensity;
        }
        finally { File.Delete(ProjectSettings.GlobalizePath(grassPath)); }
    }
    private static bool TestMissingTreeKeys(string path, RingWindSettings runtime)
    {
        string oldPath = path + ".old-tree-keys.tres";
        using var oldPreferences = new GameplayWindSettings();
        oldPreferences.Overrides["StormWindSpeed"] = 31f;
        try
        {
            if (ResourceSaver.Save(oldPreferences, oldPath) != Error.Ok) return false;
            var loaded = new GameplayWindDefaults(oldPath); loaded.Initialize();
            bool preservesDefaults = runtime.TreeTrunkBendStrength == .15f && runtime.TreeCanopyFlexStrength == 1f
                && runtime.StormWindSpeed == 31f && !loaded.Settings.Overrides.ContainsKey("TreeTrunkBendStrength")
                && !loaded.Settings.Overrides.ContainsKey("TreeCanopyFlexStrength");
            loaded.Reset();
            return preservesDefaults;
        }
        finally { File.Delete(ProjectSettings.GlobalizePath(oldPath)); }
    }
    /// <summary>Runs against the already isolated drawer created by the GDScript fixture.</summary>
    public bool RunTreeControlBindings(RingLiveTuningDrawer drawer)
    {
        var trunk = drawer.FindChild("WindTreeTrunkBendStrength", true, false) as HSlider;
        var trunkEntry = drawer.FindChild("WindTreeTrunkBendStrengthEntry", true, false) as SpinBox;
        var canopy = drawer.FindChild("WindTreeCanopyFlexStrength", true, false) as HSlider;
        var canopyEntry = drawer.FindChild("WindTreeCanopyFlexStrengthEntry", true, false) as SpinBox;
        if (trunk == null || trunkEntry == null || canopy == null || canopyEntry == null) return false;
        trunk.Value = 0; canopyEntry.Value = 0;
        bool zeroBinding = trunkEntry.Value == 0 && canopy.Value == 0
            && RingWindSettings.Current.TreeTrunkBendStrength == 0 && RingWindSettings.Current.TreeCanopyFlexStrength == 0;
        bool saved = drawer.FlushWindDefaults();
        drawer.ApplyDramaticStormPreset();
        bool preset = Math.Abs(trunk.Value - .15) < .001 && canopyEntry.Value == 1;
        drawer.ResetWindSettings();
        return zeroBinding && saved && preset && Math.Abs(trunkEntry.Value - .15) < .001 && canopy.Value == 1;
    }
    private static bool TestLegacyMigration(string path, RingWindSettings runtime)
    {
        string legacyPath = path + ".legacy.tres";
        string sharedPath = path + ".migration.tres";
        using var grass = new GameplayGrassSettings();
        grass.Overrides["GrassGustScaleMeters"] = 1234f;
        grass.Overrides["GrassGustTravelSpeedMultiplier"] = 0f;
        grass.Overrides["GrassGustStrength"] = .3f;
        try
        {
            if (ResourceSaver.Save(grass, legacyPath) != Error.Ok) return false;
            string originalGrassText = File.ReadAllText(ProjectSettings.GlobalizePath(legacyPath));
            var explicitWind = new GameplayWindDefaults(sharedPath);
            explicitWind.Initialize(); explicitWind.SetOverride("GustScaleMeters", 777f);
            if (!explicitWind.Save()) return false;
            var migrated = new GameplayWindDefaults(sharedPath, legacyPath); migrated.Initialize();
            bool precedenceAndZero = runtime.GustScaleMeters == 777f && runtime.GustTravelMultiplier == 0
                && !migrated.Settings.Overrides.ContainsKey("GustStrength") && migrated.Dirty;
            if (!migrated.Save()) return false;
            migrated.Reset();
            float resetTravel = runtime.GustTravelMultiplier;
            if (!migrated.Save()) return false;
            var afterReset = new GameplayWindDefaults(sharedPath, legacyPath); afterReset.Initialize();
            bool resetStaysReset = runtime.GustTravelMultiplier == resetTravel && afterReset.Settings.Overrides.Count == 0 && !afterReset.Dirty;
            File.Delete(ProjectSettings.GlobalizePath(sharedPath));
            var fresh = new GameplayWindDefaults(sharedPath, legacyPath); fresh.Initialize();
            bool importedSize = runtime.GustScaleMeters == 1234f && runtime.GustTravelMultiplier == 0;
            return precedenceAndZero && resetStaysReset && importedSize && File.ReadAllText(ProjectSettings.GlobalizePath(legacyPath)) == originalGrassText;
        }
        finally
        {
            File.Delete(ProjectSettings.GlobalizePath(legacyPath));
            File.Delete(ProjectSettings.GlobalizePath(sharedPath));
        }
    }
}
