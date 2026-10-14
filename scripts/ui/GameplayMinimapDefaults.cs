using System;
using System.IO;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Saved radar (terrain minimap) choices shared by the minimap and the grave-key drawer.</summary>
public sealed class GameplayMinimapDefaults
{
    public const string UserPath = "user://gameplay_minimap_defaults.tres";
    private readonly string _path;
    public GameplayMinimapSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";

    public GameplayMinimapDefaults(string? pathOverride = null) => _path = pathOverride ?? UserPath;

    public void Load()
    {
        Settings = new();
        LastError = "";
        Dirty = false;
        try
        {
            if (!ResourceLoader.Exists(_path)) return;
            using var loaded = ResourceLoader.Load<GameplayMinimapSettings>(_path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid minimap settings"; return; }
            Settings.RenderFramesPerSecond = SanitizeRate(loaded.RenderFramesPerSecond);
        }
        catch (Exception error) { LastError = error.Message; }
    }

    public static float SanitizeRate(float rate) => float.IsFinite(rate) ? Mathf.Clamp(rate, 5f, 60f) : 15f;

    public bool Set(float rate)
    {
        rate = SanitizeRate(rate);
        if (Settings.RenderFramesPerSecond == rate) return false;
        Settings.RenderFramesPerSecond = rate;
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
            if (ResourceSaver.Save(Settings, temporary) != Error.Ok) throw new IOException("Could not save minimap settings");
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
