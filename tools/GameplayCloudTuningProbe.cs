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
