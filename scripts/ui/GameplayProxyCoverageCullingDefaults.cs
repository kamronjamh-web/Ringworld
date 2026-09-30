using System;
using System.IO;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Persistent gameplay preference for rebuilding partially covered proxy terrain with fewer triangles.</summary>
public sealed class GameplayProxyCoverageCullingDefaults
{
    public const string UserPath = "user://gameplay_proxy_coverage_culling_defaults.tres";
    private readonly string? _pathOverride;

    public GameplayProxyCoverageCullingSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";

    public GameplayProxyCoverageCullingDefaults(string? pathOverride = null) => _pathOverride = pathOverride;

    public string GetPath() => _pathOverride ?? UserPath;

    public void Load()
    {
        Settings = new GameplayProxyCoverageCullingSettings();
        LastError = "";
        if (!ResourceLoader.Exists(GetPath()))
        {
            Dirty = false;
            return;
        }

        try
        {
            GameplayProxyCoverageCullingSettings? loaded = ResourceLoader.Load<GameplayProxyCoverageCullingSettings>(
                GetPath(), cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) LastError = "Invalid proxy coverage culling settings";
            else Settings.PartialCoverageMeshCullingEnabled = loaded.PartialCoverageMeshCullingEnabled;
        }
        catch (Exception error) { LastError = error.Message; }
        Dirty = false;
    }

    public bool Set(bool enabled)
    {
        if (Settings.PartialCoverageMeshCullingEnabled == enabled) return false;
        Settings.PartialCoverageMeshCullingEnabled = enabled;
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
