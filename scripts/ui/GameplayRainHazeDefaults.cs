using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using RingworldTechDemo.Weather;
namespace RingworldTechDemo.UI;

public sealed class GameplayRainHazeDefaults
{
    public const string UserPath = "user://gameplay_rain_haze_defaults.tres";
    public static GameplayRainHazeDefaults? StartupDefaults { get; private set; }
    private readonly string _path;
    private readonly Dictionary<string, Variant> _baseline = new();
    public GameplayRainHazeSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";
    public string GetPath() => _path;
    public GameplayRainHazeDefaults(string? path = null) => _path = path ?? UserPath;
    public static void ApplyStartup(string? path = null)
    {
        StartupDefaults = new GameplayRainHazeDefaults(path); StartupDefaults.Initialize();
    }
    public void Initialize()
    {
        if (_baseline.Count > 0) return;
        foreach (string key in Keys) _baseline[key] = RingRainHazeSettings.Current.Get(key);
        if (!ResourceLoader.Exists(_path)) return;
        try
        {
            var loaded = ResourceLoader.Load<GameplayRainHazeSettings>(_path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid rain haze preferences"; return; }
            foreach (Variant key in loaded.Overrides.Keys)
                if (key.VariantType == Variant.Type.String && TryValidate(key.AsString(), loaded.Overrides[key], out Variant clean))
                { Settings.Overrides[key] = clean; RingRainHazeSettings.Current.Set(key.AsString(), clean); }
                else LastError = "Ignored invalid rain haze override";
        }
        catch (Exception error) { LastError = error.Message; }
    }
    public bool SetOverride(string key, Variant value)
    {
        if (!TryValidate(key, value, out Variant clean)) return false;
        if (Settings.Overrides.TryGetValue(key, out Variant old) && old.Equals(clean) && RingRainHazeSettings.Current.Get(key).Equals(clean)) return false;
        Settings.Overrides[key] = clean; RingRainHazeSettings.Current.Set(key, clean); Dirty = true; return true;
    }
    public void Reset()
    {
        foreach (var pair in _baseline) RingRainHazeSettings.Current.Set(pair.Key, pair.Value);
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
        ["Density"]=(0,.03f), ["NearClearDistanceMeters"]=(0,100), ["MaximumOpacity"]=(0,.9f), ["VariationStrength"]=(0,1)
    };
    public static IEnumerable<string> Keys { get { yield return "Enabled"; yield return "Tint"; foreach (string key in Limits.Keys) yield return key; } }
    public static bool TryValidate(string key, Variant value, out Variant clean)
    {
        clean = default;
        if (key == "Enabled") { if (value.VariantType != Variant.Type.Bool) return false; clean = value.AsBool(); return true; }
        if (key == "Tint")
        {
            if (value.VariantType != Variant.Type.Color) return false;
            Color c = value.AsColor();
            if (!float.IsFinite(c.R) || !float.IsFinite(c.G) || !float.IsFinite(c.B) || !float.IsFinite(c.A)) return false;
            clean = new Color(Math.Clamp(c.R,0,1), Math.Clamp(c.G,0,1), Math.Clamp(c.B,0,1), 1f); return true;
        }
        if (!Limits.TryGetValue(key, out var range) || value.VariantType is not (Variant.Type.Int or Variant.Type.Float) || !double.IsFinite(value.AsDouble())) return false;
        clean = (float)Math.Clamp(value.AsDouble(), range.Min, range.Max); return true;
    }
}
