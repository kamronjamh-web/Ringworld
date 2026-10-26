using System;
using System.IO;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Saved weather audio choices shared with the grave-key drawer (user://gameplay_weather_audio_defaults.tres).</summary>
public sealed class GameplayWeatherAudioDefaults
{
    public const string UserPath = "user://gameplay_weather_audio_defaults.tres";

    private readonly record struct FloatField(string Name, float Default, float Min, float Max);

    private static readonly FloatField[] FloatFields =
    {
        new(nameof(GameplayWeatherAudioSettings.ThunderVolumeDb), -8f, -48f, 12f),
        new(nameof(GameplayWeatherAudioSettings.RainVolumeDb), -10f, -48f, 6f),
        new(nameof(GameplayWeatherAudioSettings.WindVolumeDb), -13f, -48f, 6f),
        new(nameof(GameplayWeatherAudioSettings.DistantStormVolumeDb), -18f, -48f, 6f),
        new(nameof(GameplayWeatherAudioSettings.SpatialWidth), .6f, .2f, 1f),
        new(nameof(GameplayWeatherAudioSettings.NearDetailVolumeDb), 0f, -24f, 6f)
    };

    private static readonly string[] BoolFields =
    {
        nameof(GameplayWeatherAudioSettings.ThunderEnabled), nameof(GameplayWeatherAudioSettings.AmbienceEnabled)
    };

    private readonly string _path;
    public GameplayWeatherAudioSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";

    public GameplayWeatherAudioDefaults(string? pathOverride = null) => _path = pathOverride ?? UserPath;

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
            using var loaded = ResourceLoader.Load<GameplayWeatherAudioSettings>(_path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid weather audio settings"; return; }
            using var clean = Sanitize(loaded);
            Copy(clean, Settings);
        }
        catch (Exception error) { LastError = error.Message; }
    }

    /// <summary>Clamps every field; non-finite values fall back to the defaults.</summary>
    public static GameplayWeatherAudioSettings Sanitize(GameplayWeatherAudioSettings input)
    {
        var result = new GameplayWeatherAudioSettings();
        foreach (FloatField f in FloatFields) result.Set(f.Name, Finite((float)input.Get(f.Name).AsDouble(), f.Default, f.Min, f.Max));
        foreach (string name in BoolFields) result.Set(name, input.Get(name).AsBool());
        return result;
    }

    private static void Copy(GameplayWeatherAudioSettings from, GameplayWeatherAudioSettings to)
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

    public bool SetThunderEnabled(bool value) => SetBool(nameof(GameplayWeatherAudioSettings.ThunderEnabled), value);
    public bool SetThunderVolumeDb(float value) => SetFloat(nameof(GameplayWeatherAudioSettings.ThunderVolumeDb), value);
    public bool SetAmbienceEnabled(bool value) => SetBool(nameof(GameplayWeatherAudioSettings.AmbienceEnabled), value);
    public bool SetRainVolumeDb(float value) => SetFloat(nameof(GameplayWeatherAudioSettings.RainVolumeDb), value);
    public bool SetWindVolumeDb(float value) => SetFloat(nameof(GameplayWeatherAudioSettings.WindVolumeDb), value);
    public bool SetDistantStormVolumeDb(float value) => SetFloat(nameof(GameplayWeatherAudioSettings.DistantStormVolumeDb), value);
    public bool SetSpatialWidth(float value) => SetFloat(nameof(GameplayWeatherAudioSettings.SpatialWidth), value);
    public bool SetNearDetailVolumeDb(float value) => SetFloat(nameof(GameplayWeatherAudioSettings.NearDetailVolumeDb), value);

    /// <summary>Copies the saved choices onto the thunder and ambience nodes.</summary>
    public void Apply(RingWeatherAudio? thunder, RingWeatherAmbience? ambience)
    {
        GameplayWeatherAudioSettings s = Settings;
        if (thunder != null && GodotObject.IsInstanceValid(thunder)) { thunder.Enabled = s.ThunderEnabled; thunder.ThunderVolumeDb = s.ThunderVolumeDb; }
        if (ambience == null || !GodotObject.IsInstanceValid(ambience)) return;
        ambience.Enabled = s.AmbienceEnabled;
        ambience.RainVolumeDb = s.RainVolumeDb;
        ambience.WindVolumeDb = s.WindVolumeDb;
        ambience.DistantStormVolumeDb = s.DistantStormVolumeDb;
        ambience.SpatialWidth = s.SpatialWidth;
        ambience.NearDetailVolumeDb = s.NearDetailVolumeDb;
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
            if (ResourceSaver.Save(Settings, temporary) != Error.Ok) throw new IOException("Could not save weather audio settings");
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
