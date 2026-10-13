using System;
using System.IO;
using Godot;
using RingworldTechDemo.Terrain.Editable;

namespace RingworldTechDemo.UI;

/// <summary>Saved smooth-terrain streaming throughput shared with the grave-key drawer.</summary>
public sealed class GameplayTerrainStreamingDefaults
{
    public const string UserPath = "user://gameplay_terrain_streaming_defaults.tres";
    public const int MinimumWorkers = 1, MaximumWorkers = 16;
    public const int MinimumInstalls = 1, MaximumInstalls = 32;
    public const float MinimumBudgetMilliseconds = .25f, MaximumBudgetMilliseconds = 8f;
    private readonly string _path;
    public GameplayTerrainStreamingSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";

    public GameplayTerrainStreamingDefaults(string? pathOverride = null) => _path = pathOverride ?? UserPath;

    /// <summary>True once the player has saved a choice; otherwise the grid keeps its own defaults.</summary>
    public bool HasSavedFile => ResourceLoader.Exists(_path);

    public void Load()
    {
        Settings = new();
        LastError = "";
        Dirty = false;
        try
        {
            if (!ResourceLoader.Exists(_path)) return;
            using var loaded = ResourceLoader.Load<GameplayTerrainStreamingSettings>(_path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid terrain streaming settings"; return; }
            Settings.MaxWorkers = SanitizeWorkers(loaded.MaxWorkers);
            Settings.MaxInstallsPerFrame = SanitizeInstalls(loaded.MaxInstallsPerFrame);
            Settings.InstallBudgetMilliseconds = SanitizeBudget(loaded.InstallBudgetMilliseconds);
        }
        catch (Exception error) { LastError = error.Message; }
    }

    public static int SanitizeWorkers(int workers) => Math.Clamp(workers, MinimumWorkers, MaximumWorkers);
    public static int SanitizeInstalls(int installs) => Math.Clamp(installs, MinimumInstalls, MaximumInstalls);
    public static float SanitizeBudget(float budget) => float.IsFinite(budget)
        ? Mathf.Clamp(budget, MinimumBudgetMilliseconds, MaximumBudgetMilliseconds) : 1.5f;

    public bool Set(int workers, int installs, float budget)
    {
        workers = SanitizeWorkers(workers);
        installs = SanitizeInstalls(installs);
        budget = SanitizeBudget(budget);
        if (Settings.MaxWorkers == workers && Settings.MaxInstallsPerFrame == installs && Settings.InstallBudgetMilliseconds == budget) return false;
        Settings.MaxWorkers = workers;
        Settings.MaxInstallsPerFrame = installs;
        Settings.InstallBudgetMilliseconds = budget;
        Dirty = true;
        return true;
    }

    /// <summary>Copies the saved choices onto a grid. A grid is only touched once the player has saved a choice.</summary>
    public void Apply(SmoothTerrainGrid grid)
    {
        grid.MaxWorkers = Settings.MaxWorkers;
        grid.MaxInstallsPerFrame = Settings.MaxInstallsPerFrame;
        grid.InstallBudgetMilliseconds = Settings.InstallBudgetMilliseconds;
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
            if (ResourceSaver.Save(Settings, temporary) != Error.Ok) throw new IOException("Could not save terrain streaming settings");
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
