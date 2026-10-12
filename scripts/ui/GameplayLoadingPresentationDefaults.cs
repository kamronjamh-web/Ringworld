using System;
using System.IO;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Saved presentation choices shared by loading screens and the grave-key drawer.</summary>
public sealed class GameplayLoadingPresentationDefaults
{
    public const string UserPath = "user://gameplay_loading_presentation_defaults.tres";
    private readonly string _path;
    public GameplayLoadingPresentationSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";

    public GameplayLoadingPresentationDefaults(string? pathOverride = null) => _path = pathOverride ?? UserPath;

    public void Load()
    {
        Settings = new();
        LastError = "";
        Dirty = false;
        try
        {
            if (!ResourceLoader.Exists(_path)) return;
            using var loaded = ResourceLoader.Load<GameplayLoadingPresentationSettings>(_path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid loading presentation settings"; return; }
            Settings.AnimationSpeed = SanitizeSpeed(loaded.AnimationSpeed);
            Settings.MotionEnabled = loaded.MotionEnabled;
            Settings.HologramBrightness = SanitizeBrightness(loaded.HologramBrightness);
        }
        catch (Exception error) { LastError = error.Message; }
    }

    public static float SanitizeSpeed(float speed) => float.IsFinite(speed) ? Mathf.Clamp(speed, 0, .6f) : .18f;
    public static float SanitizeBrightness(float brightness) => float.IsFinite(brightness) ? Mathf.Clamp(brightness, .35f, 1.5f) : 1f;

    public bool Set(float speed, bool motion, float brightness)
    {
        speed = SanitizeSpeed(speed);
        brightness = SanitizeBrightness(brightness);
        if (Settings.AnimationSpeed == speed && Settings.MotionEnabled == motion && Settings.HologramBrightness == brightness) return false;
        Settings.AnimationSpeed = speed;
        Settings.MotionEnabled = motion;
        Settings.HologramBrightness = brightness;
        Dirty = true;
        return true;
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
            if (ResourceSaver.Save(Settings, temporary) != Error.Ok) throw new IOException("Could not save loading presentation settings");
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
