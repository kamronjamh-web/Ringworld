using System;
using System.IO;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Atomically persists the shared proxy and scaled-far transition-tier preference.</summary>
public sealed class GameplayLodTransitionDefaults
{
    public const string UserPath = "user://gameplay_lod_transition_defaults.tres";
    private readonly string? _pathOverride;

    public GameplayLodTransitionSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";

    public GameplayLodTransitionDefaults(string? pathOverride = null) => _pathOverride = pathOverride;

    public string GetPath() => _pathOverride ?? UserPath;

    public void Load()
    {
        Settings = new GameplayLodTransitionSettings();
        LastError = "";
        if (!ResourceLoader.Exists(GetPath()))
        {
            Dirty = false;
            return;
        }

        try
        {
            GameplayLodTransitionSettings? loaded = ResourceLoader.Load<GameplayLodTransitionSettings>(
                GetPath(), cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) LastError = "Invalid LOD transition settings";
            else Settings.IntermediateLodTiersEnabled = loaded.IntermediateLodTiersEnabled;
        }
        catch (Exception error) { LastError = error.Message; }
        Dirty = false;
    }

    public bool SetIntermediateLodTiersEnabled(bool enabled)
    {
        if (Settings.IntermediateLodTiersEnabled == enabled) return false;
        Settings.IntermediateLodTiersEnabled = enabled;
        Dirty = true;
        return true;
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
}
