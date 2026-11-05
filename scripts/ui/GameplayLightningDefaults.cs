using System;
using System.IO;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Saved lightning choices shared with the grave-key drawer (user://gameplay_lightning_defaults.tres).</summary>
public sealed class GameplayLightningDefaults
{
    public const string UserPath = "user://gameplay_lightning_defaults.tres";

    private readonly record struct FloatField(string Name, float Default, float Min, float Max);

    private static readonly FloatField[] FloatFields =
    {
        new(nameof(GameplayLightningSettings.LightningFrequency), 1f, 0f, 2f),
        new(nameof(GameplayLightningSettings.UpperAtmosphereLightning), 1f, 0f, 2f),
        new(nameof(GameplayLightningSettings.LightningIllumination), 1f, 0f, 2f),
        new(nameof(GameplayLightningSettings.LightningIlluminationReachKm), 20f, 1f, 40f)
    };

    private static readonly string[] BoolFields = { nameof(GameplayLightningSettings.ReducedFlashes) };

    private readonly string _path;
    public GameplayLightningSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";

    public GameplayLightningDefaults(string? pathOverride = null) => _path = pathOverride ?? UserPath;

    /// <summary>True once the player has saved a choice.</summary>
    public bool HasSavedFile => ResourceLoader.Exists(_path);
    public string GetPath() => _path;

    public static (float Min, float Max) RangeOf(string field)
    {
        foreach (FloatField f in FloatFields) if (f.Name == field) return (f.Min, f.Max);
        throw new ArgumentException("unknown field " + field, nameof(field));
    }

    public void Load()
    {
        Settings = new();
        LastError = "";
        Dirty = false;
        try
        {
            if (!ResourceLoader.Exists(_path)) return;
            using var loaded = ResourceLoader.Load<GameplayLightningSettings>(_path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid lightning settings"; return; }
            using var clean = Sanitize(loaded);
            Copy(clean, Settings);
        }
        catch (Exception error) { LastError = error.Message; }
    }

    /// <summary>Clamps every field; non-finite values fall back to the defaults.</summary>
    public static GameplayLightningSettings Sanitize(GameplayLightningSettings input)
    {
        var result = new GameplayLightningSettings();
        foreach (FloatField f in FloatFields) result.Set(f.Name, Finite((float)input.Get(f.Name).AsDouble(), f.Default, f.Min, f.Max));
        foreach (string name in BoolFields) result.Set(name, input.Get(name).AsBool());
        return result;
    }

    private static void Copy(GameplayLightningSettings from, GameplayLightningSettings to)
    {
        foreach (FloatField f in FloatFields) to.Set(f.Name, from.Get(f.Name));
        foreach (string name in BoolFields) to.Set(name, from.Get(name));
    }

    private static float Finite(float value, float fallback, float min, float max) => float.IsFinite(value) ? Mathf.Clamp(value, min, max) : fallback;

    /// <summary>Clamped float setter by field name; true when the stored value changed.</summary>
    public bool SetFloat(string field, float value)
    {
        foreach (FloatField f in FloatFields)
        {
            if (f.Name != field) continue;
            float v = Finite(value, f.Default, f.Min, f.Max);
            if ((float)Settings.Get(field).AsDouble() == v) return false;
            Settings.Set(field, v);
            Dirty = true;
            return true;
        }
        throw new ArgumentException("unknown float field " + field, nameof(field));
    }

    public bool SetBool(string field, bool value)
    {
        if (Array.IndexOf(BoolFields, field) < 0) throw new ArgumentException("unknown bool field " + field, nameof(field));
        if (Settings.Get(field).AsBool() == value) return false;
        Settings.Set(field, value);
        Dirty = true;
        return true;
    }

    public bool SetLightningFrequency(float value) => SetFloat(nameof(GameplayLightningSettings.LightningFrequency), value);
    public bool SetUpperAtmosphereLightning(float value) => SetFloat(nameof(GameplayLightningSettings.UpperAtmosphereLightning), value);
    public bool SetLightningIllumination(float value) => SetFloat(nameof(GameplayLightningSettings.LightningIllumination), value);
    public bool SetLightningIlluminationReachKm(float value) => SetFloat(nameof(GameplayLightningSettings.LightningIlluminationReachKm), value);
    public bool SetReducedFlashes(bool value) => SetBool(nameof(GameplayLightningSettings.ReducedFlashes), value);

    /// <summary>Publishes the saved choices to the lightning policy read by the scheduler, flash pulse and sprite layer.</summary>
    public void Apply()
    {
        GameplayLightningSettings s = Settings;
        Lightning.LightningVisualPolicy.Apply(s.LightningFrequency, s.UpperAtmosphereLightning, s.ReducedFlashes, s.LightningIllumination, s.LightningIlluminationReachKm);
    }

    public void Reset() { Settings = new(); Dirty = true; LastError = ""; }

    public bool Save()
    {
        if (!Dirty) return true;
        string temporary = "";
        try
        {
            string full = ProjectSettings.GlobalizePath(_path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp.tres";
            if (ResourceSaver.Save(Settings, temporary) != Error.Ok) throw new IOException("Could not save lightning settings");
            File.Move(ProjectSettings.GlobalizePath(temporary), full, true);
            temporary = "";
            Dirty = false;
            LastError = "";
            return true;
        }
        catch (Exception error) { LastError = error.Message; return false; }
        finally { if (temporary.Length > 0) try { File.Delete(ProjectSettings.GlobalizePath(temporary)); } catch { } }
    }
}
