using System;
using System.IO;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Persistent gameplay 3D render-resolution preference.</summary>
public sealed class GameplayRenderResolutionDefaults
{
    public const string UserPath = "user://gameplay_render_resolution_defaults.tres";
    private readonly string? _pathOverride;
    public GameplayRenderResolutionSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";

    public GameplayRenderResolutionDefaults(string? pathOverride = null) => _pathOverride = pathOverride;
    public string GetPath() => _pathOverride ?? UserPath;

    public void Load(int missingFileFallbackPreset = 0)
    {
        Settings = new GameplayRenderResolutionSettings();
        LastError = "";
        if (!ResourceLoader.Exists(GetPath()))
        {
            Settings.Preset = ValidatePreset(missingFileFallbackPreset);
            Dirty = false;
            return;
        }
        try
        {
            GameplayRenderResolutionSettings? loaded = ResourceLoader.Load<GameplayRenderResolutionSettings>(GetPath(), cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid render-resolution settings"; }
            else
            {
                Settings.Preset = ValidatePreset(loaded.Preset);
                Settings.AdaptiveResolutionEnabled = loaded.AdaptiveResolutionEnabled;
            }
        }
        catch (Exception error) { LastError = error.Message; }
        Dirty = false;
    }

    public bool Set(int preset, bool adaptive)
    {
        preset = ValidatePreset(preset);
        if (Settings.Preset == preset && Settings.AdaptiveResolutionEnabled == adaptive) return false;
        Settings.Preset = preset;
        Settings.AdaptiveResolutionEnabled = adaptive;
        Dirty = true;
        return true;
    }

    public void Reset()
    {
        Settings = new GameplayRenderResolutionSettings();
        Dirty = true;
        LastError = "";
    }

    public bool Save()
    {
        if (!Dirty) return true;
        string temporary = "";
        try
        {
            string path = GetPath();
            string directory = Path.GetDirectoryName(ProjectSettings.GlobalizePath(path))!;
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp.tres";
            Error result = ResourceSaver.Save(Settings, temporary);
            if (result != Error.Ok) throw new IOException(result.ToString());
            File.Move(ProjectSettings.GlobalizePath(temporary), ProjectSettings.GlobalizePath(path), true);
            temporary = "";
            Dirty = false;
            LastError = "";
            return true;
        }
        catch (Exception error) { LastError = error.Message; return false; }
        finally { if (temporary.Length > 0) try { File.Delete(ProjectSettings.GlobalizePath(temporary)); } catch { } }
    }

    public static int ValidatePreset(int preset) => preset is >= 0 and <= 5 ? preset : 0;
}
