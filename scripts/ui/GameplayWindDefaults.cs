using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.UI;

/// <summary>Validated wind preferences, shared by startup and the live tuning drawer.</summary>
public sealed class GameplayWindDefaults
{
    public const string UserPath = "user://gameplay_wind_defaults.tres";
    private readonly string _path;
    private readonly string? _legacyGrassPath;
    private readonly Dictionary<string, Variant> _baseline = new();
    public static GameplayWindDefaults? StartupDefaults { get; private set; }
    public GameplayWindSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";
    public GameplayWindDefaults(string? path = null, string? legacyGrassPath = null)
    {
        _path = path ?? UserPath;
        // Isolated preference fixtures never consult the player's grass file implicitly.
        _legacyGrassPath = legacyGrassPath ?? (path == null ? GameplayGrassDefaults.UserPath : null);
    }
    public string GetPath() => _path;
    public static void ApplyStartup(string? windPath = null, string? legacyGrassPath = null)
    {
        // Capture authored values before preferences, once per weather scene startup.
        StartupDefaults = new GameplayWindDefaults(windPath, legacyGrassPath);
        StartupDefaults.Initialize();
    }
    public void Initialize()
    {
        if (_baseline.Count > 0) return;
        foreach (string key in Keys) _baseline[key] = RingWindSettings.Current.Get(key);
        if (ResourceLoader.Exists(_path)) try
        {
            var loaded = ResourceLoader.Load<GameplayWindSettings>(_path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid wind preferences"; return; }
            Settings.LegacyGrassMigrationCompleted = loaded.LegacyGrassMigrationCompleted;
            foreach (Variant key in loaded.Overrides.Keys)
                if (key.VariantType == Variant.Type.String && TryValidate(key.AsString(), loaded.Overrides[key], out Variant value))
                {
                    Settings.Overrides[key] = value;
                    RingWindSettings.Current.Set(key.AsString(), value);
                }
                else LastError = "Ignored invalid wind override";
        }
        catch (Exception error) { LastError = error.Message; }
        if (_legacyGrassPath != null) ImportLegacyGrassOverrides(_legacyGrassPath);
    }
    /// <summary>One-way import: explicit shared preferences always win; never writes grass preferences.</summary>
    public void ImportLegacyGrassOverrides(string grassPath)
    {
        if (Settings.LegacyGrassMigrationCompleted || !ResourceLoader.Exists(grassPath)) return;
        try
        {
            var legacy = ResourceLoader.Load<GameplayGrassSettings>(grassPath, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (legacy == null) return;
            foreach (var (oldKey, sharedKey) in new[] { ("GrassGustScaleMeters", "GustScaleMeters"), ("GrassGustTravelSpeedMultiplier", "GustTravelMultiplier") })
                if (!Settings.Overrides.ContainsKey(sharedKey) && legacy.Overrides.TryGetValue(oldKey, out Variant value))
                    SetOverride(sharedKey, value);
            Settings.LegacyGrassMigrationCompleted = true;
            Dirty = true;
        }
        catch (Exception error) { LastError = error.Message; }
    }
    public bool SetOverride(string key, Variant value)
    {
        if (!TryValidate(key, value, out Variant clean)) return false;
        if (Settings.Overrides.TryGetValue(key, out Variant old) && old.Equals(clean) && RingWindSettings.Current.Get(key).Equals(clean)) return false;
        RingWindSettings.Current.Set(key, clean);
        Settings.Overrides[key] = clean;
        Dirty = true;
        return true;
    }
    public void Reset()
    {
        foreach (var pair in _baseline) RingWindSettings.Current.Set(pair.Key, pair.Value);
        Settings = new() { LegacyGrassMigrationCompleted = true }; Dirty = true; LastError = "";
    }
    public void ApplyDramaticStormPreset()
    {
        foreach (var pair in DramaticStormPreset) SetOverride(pair.Key, pair.Value);
    }
    public bool Save()
    {
        if (!Dirty) return true;
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp.tres";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProjectSettings.GlobalizePath(_path))!);
            Error result = ResourceSaver.Save(Settings, temporary);
            if (result != Error.Ok) throw new IOException(result.ToString());
            File.Move(ProjectSettings.GlobalizePath(temporary), ProjectSettings.GlobalizePath(_path), true);
            Dirty = false; LastError = ""; return true;
        }
        catch (Exception error) { LastError = error.Message; return false; }
        finally { try { File.Delete(ProjectSettings.GlobalizePath(temporary)); } catch { } }
    }
    public static readonly Dictionary<string, (float Min, float Max)> Limits = new()
    {
        ["BaseWindMultiplier"]=(0,4), ["StormWindSpeed"]=(0,80), ["StormDirectionDegrees"]=(0,90),
        ["StormRiseSeconds"]=(.1f,120), ["StormDecaySeconds"]=(.1f,300),
        ["GustStrength"]=(0,2), ["GustScaleMeters"]=(48,8000), ["GustTravelMultiplier"]=(0,4), ["GustBaseBreeze"]=(0,1),
        ["TreeBendStrength"]=(0,4), ["TreeMaximumBendRatio"]=(0,.6f), ["TreeTrunkResponseSeconds"]=(.05f,20), ["TreeCanopyResponseSeconds"]=(.05f,10),
        ["TreeTrunkBendStrength"]=(0,1), ["TreeCanopyFlexStrength"]=(0,3),
        ["WaterResponseSeconds"]=(.1f,120), ["WaterGustResponseStrength"]=(0,3), ["AnalyticWaveResponseStrength"]=(0,4),
        ["RadarWindCalmThreshold"]=(0,5)
    };
    public static IEnumerable<string> Keys { get { yield return "Enabled"; yield return "RadarWindArrowEnabled"; foreach (string key in Limits.Keys) yield return key; } }
    public static bool TryValidate(string key, Variant value, out Variant clean)
    {
        clean = default;
        if (key is "Enabled" or "RadarWindArrowEnabled")
        {
            if (value.VariantType != Variant.Type.Bool) return false;
            clean = value.AsBool(); return true;
        }
        if (!Limits.TryGetValue(key, out var range) || value.VariantType is not (Variant.Type.Int or Variant.Type.Float) || !double.IsFinite(value.AsDouble())) return false;
        clean = (float)Math.Clamp(value.AsDouble(), range.Min, range.Max); return true;
    }
    private static readonly Dictionary<string, Variant> DramaticStormPreset = new()
    {
        ["Enabled"]=true, ["BaseWindMultiplier"]=1f, ["StormWindSpeed"]=38f, ["StormDirectionDegrees"]=24f,
        ["StormRiseSeconds"]=4f, ["StormDecaySeconds"]=22f, ["GustStrength"]=1.2f, ["GustScaleMeters"]=600f,
        ["GustTravelMultiplier"]=1.4f, ["GustBaseBreeze"]=.55f, ["TreeBendStrength"]=2.1f,
        ["TreeMaximumBendRatio"]=.36f, ["TreeTrunkResponseSeconds"]=2f, ["TreeCanopyResponseSeconds"]=.6f,
        ["TreeTrunkBendStrength"]=.15f, ["TreeCanopyFlexStrength"]=1f,
        ["WaterResponseSeconds"]=8f, ["WaterGustResponseStrength"]=1.8f, ["AnalyticWaveResponseStrength"]=1.8f
    };
}
