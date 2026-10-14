using System;
using System.IO;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.UI;

/// <summary>Saved LOD build-start scheduling shared with the grave-key drawer.</summary>
public sealed class GameplayLodSchedulingDefaults
{
    public const string UserPath = "user://gameplay_lod_scheduling_defaults.tres";
    public const int MinimumStarts = 1, MaximumStarts = 8;
    public const float MinimumBudgetMilliseconds = .25f, MaximumBudgetMilliseconds = 16f;
    public const int MinimumStarvationFrames = 5, MaximumStarvationFrames = 300;
    private readonly string _path;
    public GameplayLodSchedulingSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";

    public GameplayLodSchedulingDefaults(string? pathOverride = null) => _path = pathOverride ?? UserPath;

    /// <summary>True once the player has saved a choice; otherwise the coordinator keeps its own defaults.</summary>
    public bool HasSavedFile => ResourceLoader.Exists(_path);

    public void Load()
    {
        Settings = new();
        LastError = "";
        Dirty = false;
        try
        {
            if (!ResourceLoader.Exists(_path)) return;
            using var loaded = ResourceLoader.Load<GameplayLodSchedulingSettings>(_path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid LOD scheduling settings"; return; }
            Settings.MaxBuildStartsPerFrame = SanitizeStarts(loaded.MaxBuildStartsPerFrame);
            Settings.BuildStartBudgetMilliseconds = SanitizeBudget(loaded.BuildStartBudgetMilliseconds);
            Settings.StarvationFrames = SanitizeStarvationFrames(loaded.StarvationFrames);
        }
        catch (Exception error) { LastError = error.Message; }
    }

    public static int SanitizeStarts(int starts) => Math.Clamp(starts, MinimumStarts, MaximumStarts);
    public static float SanitizeBudget(float budget) => float.IsFinite(budget)
        ? Mathf.Clamp(budget, MinimumBudgetMilliseconds, MaximumBudgetMilliseconds) : 2f;
    public static int SanitizeStarvationFrames(int frames) => Math.Clamp(frames, MinimumStarvationFrames, MaximumStarvationFrames);

    public bool Set(int starts, float budget, int starvationFrames)
    {
        starts = SanitizeStarts(starts);
        budget = SanitizeBudget(budget);
        starvationFrames = SanitizeStarvationFrames(starvationFrames);
        if (Settings.MaxBuildStartsPerFrame == starts && Settings.BuildStartBudgetMilliseconds == budget && Settings.StarvationFrames == starvationFrames) return false;
        Settings.MaxBuildStartsPerFrame = starts;
        Settings.BuildStartBudgetMilliseconds = budget;
        Settings.StarvationFrames = starvationFrames;
        Dirty = true;
        return true;
    }

    /// <summary>Copies the saved choices onto a coordinator. Callers only apply once a choice has been saved.</summary>
    public void Apply(RingTerrainLodCoordinator coordinator)
    {
        coordinator.MaxBuildStartsPerFrame = Settings.MaxBuildStartsPerFrame;
        coordinator.MaxBuildStartMillisecondsPerFrame = Settings.BuildStartBudgetMilliseconds;
        coordinator.BuildStartStarvationFrames = Settings.StarvationFrames;
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
            if (ResourceSaver.Save(Settings, temporary) != Error.Ok) throw new IOException("Could not save LOD scheduling settings");
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
