using System;
using System.Reflection;
using Godot;
using RingworldTechDemo.UI;

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
