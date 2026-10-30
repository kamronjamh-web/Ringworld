using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using RingworldTechDemo.Weather;
using RingworldTechDemo.Rendering;
namespace RingworldTechDemo.UI;

public sealed class GameplayMegastormDefaults
{
    public const string UserPath = "user://gameplay_megastorm_defaults.tres";
    public static GameplayMegastormDefaults? StartupDefaults { get; private set; }
    private readonly string _path;
    private readonly Dictionary<string, Variant> _baseline = new();
    public GameplayMegastormSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";
    public string GetPath() => _path;
    public GameplayMegastormDefaults(string? path = null) => _path = path ?? UserPath;
    public static void ApplyStartup(string? path = null)
    {
        StartupDefaults = new GameplayMegastormDefaults(path); StartupDefaults.Initialize();
    }
    public void Initialize()
    {
        if (_baseline.Count > 0) return;
        foreach (string key in Keys) _baseline[key] = RingMegastormSettings.Current.Get(key);
        if (!ResourceLoader.Exists(_path)) return;
        try
        {
            var loaded = ResourceLoader.Load<GameplayMegastormSettings>(_path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid megastorm preferences"; return; }
            foreach (Variant key in loaded.Overrides.Keys)
                if (key.VariantType == Variant.Type.String && TryValidate(key.AsString(), loaded.Overrides[key], out Variant clean))
                { Settings.Overrides[key] = clean; RingMegastormSettings.Current.Set(key.AsString(), clean); }
                else LastError = "Ignored invalid megastorm override";
        }
        catch (Exception error) { LastError = error.Message; }
    }
    public bool SetOverride(string key, Variant value)
    {
        if (!TryValidate(key, value, out Variant clean)) return false;
        if (Settings.Overrides.TryGetValue(key, out Variant old) && RingVariantEquality.Same(old, clean) && RingVariantEquality.Same(RingMegastormSettings.Current.Get(key), clean)) return false;
        Settings.Overrides[key] = clean; RingMegastormSettings.Current.Set(key, clean); Dirty = true; return true;
    }
    public void Reset()
    {
        foreach (var pair in _baseline) RingMegastormSettings.Current.Set(pair.Key, pair.Value);
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
        ["SpeedMetersPerSecond"]=(5,500), ["TrailingLengthKm"]=(100,600), ["WarningReachKm"]=(50,600)
    };
    public static IEnumerable<string> Keys => Limits.Keys;
    public static bool TryValidate(string key, Variant value, out Variant clean)
    {
        clean = default;
        if (!Limits.TryGetValue(key, out var range) || value.VariantType is not (Variant.Type.Int or Variant.Type.Float) || !double.IsFinite(value.AsDouble())) return false;
        clean = (float)Math.Clamp(value.AsDouble(), range.Min, range.Max); return true;
    }
}
