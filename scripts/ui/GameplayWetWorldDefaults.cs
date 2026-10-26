using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using RingworldTechDemo.Weather;
using RingworldTechDemo.Rendering;
namespace RingworldTechDemo.UI;

/// <summary>
/// Sparse persisted overrides of <see cref="RingWetWorldSettings"/> (docs/storm-revamp-s2-wet-world-design.md section 4). Sibling of GameplayStormArrivalDefaults.
/// When the file is missing, the retired Surface terrain keys are migrated once (Wetness = RainWetnessStrength, Wet gloss = WetRoughness, only when they were changed from their old defaults).
/// </summary>
public sealed class GameplayWetWorldDefaults
{
    public const string UserPath = "user://gameplay_wet_world_defaults.tres";
    public const float LegacyWetRoughnessDefault = .35f, LegacyRainWetnessStrengthDefault = 1.0f;
    public static GameplayWetWorldDefaults? StartupDefaults { get; private set; }
    private readonly string _path;
    private readonly Dictionary<string, Variant> _baseline = new();
    public GameplayWetWorldSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";
    /// <summary>Where the retired Surface terrain keys are read from when this file is missing (tests redirect it to an isolated file).</summary>
    public string LegacySurfaceTerrainPath { get; set; } = GameplaySurfaceTerrainDefaults.UserPath;
    public bool Migrated { get; private set; }
    public string GetPath() => _path;
    public GameplayWetWorldDefaults(string? path = null) => _path = path ?? UserPath;
    public static void ApplyStartup(string? path = null)
    {
        StartupDefaults = new GameplayWetWorldDefaults(path); StartupDefaults.Initialize();
    }
    public void Initialize()
    {
        if (_baseline.Count > 0) return;
        foreach (string key in Keys) _baseline[key] = RingWetWorldSettings.Current.Get(key);
        if (!ResourceLoader.Exists(_path)) { MigrateFromSurfaceTerrain(); return; }
        try
        {
            var loaded = ResourceLoader.Load<GameplayWetWorldSettings>(_path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid wet world preferences"; return; }
            foreach (Variant key in loaded.Overrides.Keys)
                if (key.VariantType == Variant.Type.String && TryValidate(key.AsString(), loaded.Overrides[key], out Variant clean))
                { Settings.Overrides[key] = clean; RingWetWorldSettings.Current.Set(key.AsString(), clean); }
                else LastError = "Ignored invalid wet world override";
        }
        catch (Exception error) { LastError = error.Message; }
    }
    private void MigrateFromSurfaceTerrain()
    {
        try
        {
            if (!ResourceLoader.Exists(LegacySurfaceTerrainPath)) return;
            var legacy = ResourceLoader.Load<GameplaySurfaceTerrainSettings>(LegacySurfaceTerrainPath, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (legacy == null) return;
            if (float.IsFinite(legacy.RainWetnessStrength) && Math.Abs(legacy.RainWetnessStrength - LegacyRainWetnessStrengthDefault) > 1e-3f) { SetOverride("Wetness", legacy.RainWetnessStrength); Migrated = true; }
            if (float.IsFinite(legacy.WetRoughness) && Math.Abs(legacy.WetRoughness - LegacyWetRoughnessDefault) > 1e-3f) { SetOverride("WetGloss", legacy.WetRoughness); Migrated = true; }
        }
        catch (Exception error) { LastError = error.Message; }
    }
    public bool SetOverride(string key, Variant value)
    {
        if (!TryValidate(key, value, out Variant clean)) return false;
        if (Settings.Overrides.TryGetValue(key, out Variant old) && RingVariantEquality.Same(old, clean) && RingVariantEquality.Same(RingWetWorldSettings.Current.Get(key), clean)) return false;
        Settings.Overrides[key] = clean; RingWetWorldSettings.Current.Set(key, clean); Dirty = true; return true;
    }
    public void Reset()
    {
        foreach (var pair in _baseline) RingWetWorldSettings.Current.Set(pair.Key, pair.Value);
        Settings = new(); Dirty = true; LastError = "";
    }
    public bool Save()
    {
        if (!Dirty) return true;
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp.tres";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProjectSettings.GlobalizePath(_path))!);
            Error error = ResourceSaver.Save(Settings, temporary);
            if (error != Error.Ok) throw new IOException(error.ToString());
            File.Move(ProjectSettings.GlobalizePath(temporary), ProjectSettings.GlobalizePath(_path), true);
            Dirty = false; LastError = ""; return true;
        }
        catch (Exception error) { LastError = error.Message; return false; }
        finally { try { File.Delete(ProjectSettings.GlobalizePath(temporary)); } catch { } }
    }
    public static readonly Dictionary<string, (float Min, float Max)> Limits = new()
    {
        ["Wetness"]=(0,1.5f), ["WetGloss"]=(.05f,.6f), ["Puddles"]=(0,2), ["DryingSpeed"]=(.25f,4), ["LensDroplets"]=(0,1)
    };
    public static IEnumerable<string> Keys => Limits.Keys;
    public static bool TryValidate(string key, Variant value, out Variant clean)
    {
        clean = default;
        if (!Limits.TryGetValue(key, out var range) || value.VariantType is not (Variant.Type.Int or Variant.Type.Float) || !double.IsFinite(value.AsDouble())) return false;
        clean = (float)Math.Clamp(value.AsDouble(), range.Min, range.Max); return true;
    }
}
