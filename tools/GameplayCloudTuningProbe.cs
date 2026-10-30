using System;
using System.Reflection;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo;

/// <summary>Test-only isolated persistence adapter for gameplay cloud tuning.</summary>
public partial class GameplayCloudTuningProbe : RefCounted
{
    private GameplayCloudDefaults? _defaults;

    public bool Open(Node bridgeNode, string isolatedPath)
    {
        if (bridgeNode is not RingSunshineCloudBridge bridge
            || !isolatedPath.StartsWith("res://.godot/", StringComparison.Ordinal))
            return false;

        _defaults = new GameplayCloudDefaults(isolatedPath);
        _defaults.Initialize(bridge);
        return true;
    }

    /// <summary>Installs an isolated service before the bridge and drawer enter the tree.</summary>
    public bool InstallShared(Node bridgeNode, string isolatedPath)
    {
        if (bridgeNode is not RingSunshineCloudBridge bridge
            || !isolatedPath.StartsWith("res://.godot/", StringComparison.Ordinal))
            return false;

        _defaults = new GameplayCloudDefaults(isolatedPath);
        FieldInfo? field = typeof(RingSunshineCloudBridge).GetField(
            "<GameplayCloudDefaults>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null) return false;
        field.SetValue(bridge, _defaults);
        return true;
    }

    public bool Set(Node bridgeNode, string key, Variant value) =>
        bridgeNode is RingSunshineCloudBridge bridge
        && _defaults?.SetOverride(bridge, key, value) == true;

    /// <summary>Exercises the persisted override equality contract without
    /// changing the gameplay scene or its isolated saved settings.</summary>
    public bool VerifySupportedOverrideEquality()
    {
        using var bridge = new RingSunshineCloudBridge();
        var defaults = new GameplayCloudDefaults();
        return Verify(defaults, bridge, "CloudRenderingEnabled", false)
            && !defaults.SetOverride(bridge, "CloudRenderingEnabled", false)
            && Verify(defaults, bridge, "CloudRenderingEnabled", true)
            && !defaults.SetOverride(bridge, "CloudRenderingEnabled", true)
            && Verify(defaults, bridge, "RaymarchSteps", 192)
            && Verify(defaults, bridge, "RaymarchSteps", 200)
            && !defaults.SetOverride(bridge, "RaymarchSteps", 200)
            && Verify(defaults, bridge, "CloudDensityMultiplier", 1.17f)
            && Verify(defaults, bridge, "CloudDensityMultiplier", 1.25f)
            && !defaults.SetOverride(bridge, "CloudDensityMultiplier", 1.25f)
            && Verify(defaults, bridge, "DayCloudAmbientColor", new Color(.71f, .63f, .54f, 1.0f))
            && Verify(defaults, bridge, "DayCloudAmbientColor", new Color(.62f, .74f, .83f, 1.0f))
            && !defaults.SetOverride(bridge, "DayCloudAmbientColor", new Color(.62f, .74f, .83f, 1.0f));
    }

    /// <summary>
    /// Preset ownership of the 12 profile keys: with the factory file loaded read-only, every graphics preset leaves the bridge on that
    /// preset's tuple (the factory file no longer owns them), and a sparse user override survives ApplyUserPreset.
    /// </summary>
    public string VerifyPresetOwnership(string isolatedPath)
    {
        if (!isolatedPath.StartsWith("res://.godot/", StringComparison.Ordinal) || isolatedPath.Contains("..", StringComparison.Ordinal)) return "bad path";
        FieldInfo? defaultsField = typeof(RingSunshineCloudBridge).GetField("<GameplayCloudDefaults>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? cloudsField = typeof(RingGraphicsQualityController).GetField("_sunshineClouds", BindingFlags.Instance | BindingFlags.NonPublic);
        if (defaultsField is null || cloudsField is null) return "reflection fields missing";
        string absolute = ProjectSettings.GlobalizePath(isolatedPath);
        using var bridge = new RingSunshineCloudBridge();
        using var graphics = new RingGraphicsQualityController();
        try
        {
            System.IO.File.Delete(absolute);
            // Factory file, never saved from here: no SetOverride is called on this instance.
            var factory = new GameplayCloudDefaults();
            defaultsField.SetValue(bridge, factory);
            cloudsField.SetValue(graphics, bridge);
            factory.Initialize(bridge);
            foreach (string key in new[] { "RaymarchSteps", "LightingSteps", "GpuNearShadowMapResolution", "GpuNearShadowUpdatesPerSecond", "GpuFarShadowMapResolution", "GpuFarShadowUpdatesPerSecond", "GpuShadowVerticalSamples", "CloudResolutionScale", "CloudResolutionJitter", "CloudInterleave", "WeatherSystemUploadCount" })
                if (factory.Settings.Overrides.ContainsKey(key)) return "factory file still owns " + key;
            foreach (RingTerrainLodCoordinator.QualityPreset preset in Enum.GetValues<RingTerrainLodCoordinator.QualityPreset>())
            {
                graphics.ApplyPreset(preset, applyTerrainPreset: false);
                (float stepMeters, int maxSamples) = preset switch
                {
                    RingTerrainLodCoordinator.QualityPreset.Low => (400f, 32),
                    RingTerrainLodCoordinator.QualityPreset.VeryHigh => (200f, 96),
                    RingTerrainLodCoordinator.QualityPreset.UltraVideo or RingTerrainLodCoordinator.QualityPreset.Cinematic5090 => (150f, 128),
                    _ => (250f, 64)
                };
                bool lowTier = preset is RingTerrainLodCoordinator.QualityPreset.Low or RingTerrainLodCoordinator.QualityPreset.Medium;
                (int raymarch, int nearRes, int farRes, float nearHz, float farHz, int vertical, int uploads, int lighting) = ExpectedPresetCloudTuple(preset);
                bool tuple = bridge.CloudResolutionScale == (lowTier ? 1 : 0) && bridge.RaymarchSteps == raymarch && bridge.GpuNearShadowMapResolution == nearRes
                    && bridge.GpuFarShadowMapResolution == farRes && bridge.GpuNearShadowUpdatesPerSecond == nearHz && bridge.GpuFarShadowUpdatesPerSecond == farHz
                    && bridge.GpuShadowVerticalSamples == vertical && bridge.GpuShadowStepMeters == stepMeters && bridge.GpuShadowMaxSamples == maxSamples
                    && bridge.CloudResolutionJitter == lowTier && bridge.CloudInterleave == (lowTier ? 2 : 1) && bridge.WeatherSystemUploadCount == uploads && bridge.LightingSteps == lighting;
                if (!tuple) return $"preset {preset} tuple mismatch raymarch={bridge.RaymarchSteps} lighting={bridge.LightingSteps} near={bridge.GpuNearShadowMapResolution} far={bridge.GpuFarShadowMapResolution} vertical={bridge.GpuShadowVerticalSamples} upload={bridge.WeatherSystemUploadCount}";
            }
            // A sparse override (saved in an isolated file, as the drawer does) wins over the preset.
            var sparse = new GameplayCloudDefaults(isolatedPath);
            defaultsField.SetValue(bridge, sparse);
            sparse.Initialize(bridge);
            if (!sparse.SetOverride(bridge, "RaymarchSteps", 200)) return "sparse override rejected";
            foreach (RingTerrainLodCoordinator.QualityPreset preset in Enum.GetValues<RingTerrainLodCoordinator.QualityPreset>())
            {
                graphics.ApplyUserPreset(preset);
                if (bridge.RaymarchSteps != 200 || bridge.LightingSteps != ExpectedPresetCloudTuple(preset).lighting) return $"sparse override lost on {preset}: raymarch={bridge.RaymarchSteps} lighting={bridge.LightingSteps}";
            }
            return "";
        }
        finally { System.IO.File.Delete(absolute); }
    }

    /// <summary>WO-3 preset tuple (raymarch steps, near/far shadow map px, near/far Hz, vertical samples, weather uploads, lighting steps); Cinematic keeps the Very High base.</summary>
    private static (int raymarch, int nearRes, int farRes, float nearHz, float farHz, int vertical, int uploads, int lighting) ExpectedPresetCloudTuple(RingTerrainLodCoordinator.QualityPreset preset) => preset switch
    {
        RingTerrainLodCoordinator.QualityPreset.Low => (96, 256, 128, 2f, .25f, 8, 40, 8),
        RingTerrainLodCoordinator.QualityPreset.Medium => (128, 512, 128, 5f, .5f, 12, 48, 12),
        RingTerrainLodCoordinator.QualityPreset.High => (192, 512, 128, 5f, 1f, 16, 48, 16),
        RingTerrainLodCoordinator.QualityPreset.UltraVideo => (328, 1024, 256, 10f, 2.5f, 24, 64, 30),
        _ => (328, 512, 128, 5f, 2.5f, 24, 64, 30)
    };

    public bool VerifyCullingPreferenceContract(string isolatedPath)
    {
        if (!isolatedPath.StartsWith("res://.godot/", StringComparison.Ordinal) || isolatedPath.Contains("..", StringComparison.Ordinal)) return false;
        using var bridge = new RingSunshineCloudBridge();
        try
        {
            System.IO.File.Delete(ProjectSettings.GlobalizePath(isolatedPath));
            var defaults = new GameplayCloudDefaults(isolatedPath); defaults.Initialize(bridge);
            bool authored = bridge.SkipEmptyCloudMaskSamples && bridge.FarCloudPatchCullingEnabled;
            bool edits = defaults.SetOverride(bridge, "SkipEmptyCloudMaskSamples", false)
                && defaults.SetOverride(bridge, "FarCloudPatchCullingEnabled", false)
                && !defaults.SetOverride(bridge, "SkipEmptyCloudMaskSamples", 0)
                && !defaults.SetOverride(bridge, "FarCloudPatchCullingEnabled", 1);
            defaults.OnGraphicsPresetApplied(bridge, new[] { "RaymarchSteps", "LightingSteps" });
            bool preset = !bridge.SkipEmptyCloudMaskSamples && !bridge.FarCloudPatchCullingEnabled;
            bool saved = defaults.Save();
            bridge.SkipEmptyCloudMaskSamples = bridge.FarCloudPatchCullingEnabled = true;
            defaults.LoadAndApply(bridge);
            bool reloaded = !bridge.SkipEmptyCloudMaskSamples && !bridge.FarCloudPatchCullingEnabled
                && defaults.Settings.Overrides.ContainsKey("SkipEmptyCloudMaskSamples") && defaults.Settings.Overrides.ContainsKey("FarCloudPatchCullingEnabled");
            defaults.Reset(bridge);
            return authored && edits && preset && saved && reloaded && bridge.SkipEmptyCloudMaskSamples
                && bridge.FarCloudPatchCullingEnabled && defaults.Settings.Overrides.Count == 0;
        }
        finally { System.IO.File.Delete(ProjectSettings.GlobalizePath(isolatedPath)); }
    }

    /// <summary>S6 WO8 migration: a saved file holding the retired NaturalCloudTopAltitudeMeters loads with no error, keeps its
    /// other overrides, does not apply the retired value, and the next save rewrites the file without the key.</summary>
    public string VerifyRetiredKeyMigration(string isolatedPath)
    {
        if (!isolatedPath.StartsWith("res://.godot/", StringComparison.Ordinal) || isolatedPath.Contains("..", StringComparison.Ordinal)) return "bad path";
        string absolute = ProjectSettings.GlobalizePath(isolatedPath);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(absolute)!);
        using var bridge = new RingSunshineCloudBridge();
        try
        {
            // A pre-WO8 save, written as text the way ResourceSaver wrote it before the key was retired.
            System.IO.File.WriteAllText(absolute,
                "[gd_resource type=\"Resource\" script_class=\"GameplayCloudSettings\" format=3]\n\n"
                + "[ext_resource type=\"Script\" path=\"res://scripts/ui/GameplayCloudSettings.cs\" id=\"1_d41de\"]\n\n"
                + "[resource]\nscript = ExtResource(\"1_d41de\")\nOverrides = {\n"
                + "\"CloudSharpness\": 0.4,\n\"NaturalCloudBaseAltitudeMeters\": 2000.0,\n\"NaturalCloudTopAltitudeMeters\": 7000.0\n}\n");
            float topBefore = bridge.NaturalCloudTopAltitudeMeters;
            var defaults = new GameplayCloudDefaults(isolatedPath); defaults.Initialize(bridge);
            if (defaults.LastError != "") return "load error: " + defaults.LastError;
            if (defaults.Settings.Overrides.ContainsKey("NaturalCloudTopAltitudeMeters")) return "retired key kept in overrides";
            if (!defaults.Settings.Overrides.ContainsKey("CloudSharpness") || !defaults.Settings.Overrides.ContainsKey("NaturalCloudBaseAltitudeMeters"))
                return "valid overrides dropped with the retired key";
            if (Math.Abs(bridge.NaturalCloudTopAltitudeMeters - topBefore) > 0.01f) return "retired value applied: " + bridge.NaturalCloudTopAltitudeMeters;
            if (defaults.Dirty) return "load marked the defaults dirty";
            if (!defaults.SetOverride(bridge, "CloudSharpness", 0.45f) || !defaults.Save()) return "save failed: " + defaults.LastError;
            string text = System.IO.File.ReadAllText(absolute);
            if (text.Contains("NaturalCloudTopAltitudeMeters", StringComparison.Ordinal)) return "retired key still in the saved file";
            if (!text.Contains("NaturalCloudBaseAltitudeMeters", StringComparison.Ordinal)) return "saved file lost a valid key";
            var reloaded = new GameplayCloudDefaults(isolatedPath); reloaded.Load();
            if (reloaded.LastError != "" || reloaded.Settings.Overrides.ContainsKey("NaturalCloudTopAltitudeMeters")) return "reload after save: " + reloaded.LastError;
            return "";
        }
        catch (Exception error) { return error.Message; }
        finally { System.IO.File.Delete(absolute); }
    }

    private static bool Verify(GameplayCloudDefaults defaults, RingSunshineCloudBridge bridge, string key, Variant value)
    {
        if (!defaults.SetOverride(bridge, key, value)) return false;
        Variant current = bridge.Get(key);
        return value.VariantType switch
        {
            Variant.Type.Bool => current.VariantType == Variant.Type.Bool && current.AsBool() == value.AsBool(),
            Variant.Type.Int => current.VariantType is Variant.Type.Int or Variant.Type.Float && current.AsInt64() == value.AsInt64(),
            Variant.Type.Float => current.VariantType is Variant.Type.Int or Variant.Type.Float && current.AsDouble() == value.AsDouble(),
            Variant.Type.Color => current.VariantType == Variant.Type.Color && current.AsColor() == value.AsColor(),
            _ => false
        };
    }

    public bool VerifyTerrainHistoryPreferenceContract(string isolatedPath)
    {
        if (!isolatedPath.StartsWith("res://.godot/", StringComparison.Ordinal) || isolatedPath.Contains("..", StringComparison.Ordinal)) return false;
        string absolute = ProjectSettings.GlobalizePath(isolatedPath);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(absolute)!);
        using var bridge = new RingSunshineCloudBridge();
        try
        {
            System.IO.File.Delete(absolute);
            var defaults = new GameplayCloudDefaults(isolatedPath); defaults.Initialize(bridge);
            bool baseline = bridge.TerrainCloudShadowEnabled && bridge.DepthHistoryRejectionEnabled
                && bridge.CloudHistoryCompatibilityEnabled && bridge.LightningHistoryIsolationEnabled
                && bridge.HistoryDepthToleranceMultiplier == 1 && bridge.StationaryHistoryWeight == .95f;
            foreach (string key in new[] { "TerrainCloudShadowEnabled", "DepthHistoryRejectionEnabled", "CloudHistoryCompatibilityEnabled", "LightningHistoryIsolationEnabled" })
                if (!defaults.SetOverride(bridge, key, false) || defaults.SetOverride(bridge, key, 0)) return false;
            if (!defaults.SetOverride(bridge, "TerrainCloudShadowStrength", 0)
                || !defaults.SetOverride(bridge, "HistoryDepthToleranceMultiplier", .25f)
                || !defaults.SetOverride(bridge, "TerrainCloudShadowGlobalAroundResolution", 768)
                || bridge.TerrainCloudShadowGlobalAroundResolution != 1024
                || defaults.SetOverride(bridge, "TerrainCloudShadowRefreshSeconds", double.NaN)) return false;
            defaults.OnGraphicsPresetApplied(bridge, new[] { "RaymarchSteps", "LightingSteps" });
            if (!defaults.Save()) return false;
            var loaded = new GameplayCloudDefaults(isolatedPath); loaded.Initialize(bridge);
            bool preserved = !bridge.TerrainCloudShadowEnabled && bridge.TerrainCloudShadowStrength == 0
                && !bridge.DepthHistoryRejectionEnabled && !bridge.CloudHistoryCompatibilityEnabled
                && !bridge.LightningHistoryIsolationEnabled && bridge.HistoryDepthToleranceMultiplier == .25f
                && loaded.Settings.Overrides["TerrainCloudShadowStrength"].AsDouble() == 0
                && loaded.Settings.Overrides["TerrainCloudShadowEnabled"].VariantType == Variant.Type.Bool;
            defaults.Reset(bridge);
            return baseline && preserved && bridge.TerrainCloudShadowEnabled && bridge.TerrainCloudShadowStrength == 1
                && bridge.DepthHistoryRejectionEnabled && bridge.CloudHistoryCompatibilityEnabled
                && bridge.LightningHistoryIsolationEnabled && bridge.HistoryDepthToleranceMultiplier == 1
                && bridge.StationaryHistoryWeight == .95f && defaults.Settings.Overrides.Count == 0;
        }
        finally { System.IO.File.Delete(absolute); }
    }

    public bool Save() => _defaults?.Save() == true;

    public void Reload(Node bridgeNode)
    {
        if (bridgeNode is RingSunshineCloudBridge bridge)
            _defaults?.LoadAndApply(bridge);
    }

    public bool Reset(Node bridgeNode)
    {
        if (bridgeNode is not RingSunshineCloudBridge bridge || _defaults is null) return false;
        _defaults.Reset(bridge);
        return true;
    }

    public Godot.Collections.Dictionary Status()
    {
        var result = new Godot.Collections.Dictionary();
        if (_defaults is null) return result;
        result["initialized"] = _defaults.Initialized;
        result["dirty"] = _defaults.Dirty;
        result["path"] = _defaults.GetPath();
        result["error"] = _defaults.LastError;
        result["override_count"] = _defaults.Settings.Overrides.Count;
        return result;
    }

    public Variant ReadOverride(string key) => _defaults?.Settings.Overrides.TryGetValue(key, out Variant value) == true
        ? value
        : default;
}
