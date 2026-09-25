using System;
using System.IO;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Loads and atomically persists isolated Certified Terrain3D surface preferences.</summary>
public sealed class GameplaySurfaceTerrainDefaults
{
    public const string UserPath = "user://gameplay_surface_terrain_defaults.tres";
    public const float MinimumBrushRadiusMeters = 2.0f;
    public const float MaximumBrushRadiusMeters = 48.0f;
    public const float MinimumBrushStepMeters = .25f;
    public const float MaximumBrushStepMeters = 8.0f;
    public const int MinimumMeshLods = 2;
    public const int MaximumMeshLods = 6;
    public const float MinimumMoveSpeedMetersPerSecond = 2.0f;
    public const float MaximumMoveSpeedMetersPerSecond = 60.0f;

    private readonly string? _pathOverride;
    public GameplaySurfaceTerrainSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public bool Initialized { get; private set; }
    public string LastError { get; private set; } = "";

    public GameplaySurfaceTerrainDefaults(string? pathOverride = null) => _pathOverride = pathOverride;
    public string GetPath() => _pathOverride ?? UserPath;

    public void Initialize()
    {
        if (Initialized) return;
        Load();
        Initialized = true;
    }

    public bool SetBrushRadiusMeters(float value)
    {
        value = ClampBrushRadiusMeters(value);
        if (Mathf.IsEqualApprox(Settings.BrushRadiusMeters, value)) return false;
        Settings.BrushRadiusMeters = value; Dirty = true; return true;
    }

    public bool SetBrushStepMeters(float value)
    {
        value = ClampBrushStepMeters(value);
        if (Mathf.IsEqualApprox(Settings.BrushStepMeters, value)) return false;
        Settings.BrushStepMeters = value; Dirty = true; return true;
    }

    public bool SetMeshLods(int value)
    {
        value = ClampMeshLods(value);
        if (Settings.MeshLods == value) return false;
        Settings.MeshLods = value; Dirty = true; return true;
    }

    public bool SetMoveSpeedMetersPerSecond(float value)
    {
        value = ClampMoveSpeedMetersPerSecond(value);
        if (Mathf.IsEqualApprox(Settings.MoveSpeedMetersPerSecond, value)) return false;
        Settings.MoveSpeedMetersPerSecond = value; Dirty = true; return true;
    }

    public void Reset()
    {
        Settings = new GameplaySurfaceTerrainSettings();
        LastError = "";
        Dirty = true;
    }

    public bool Save()
    {
        if (!Dirty) return true;
        string temporary = "";
        try
        {
            ClampSettings();
            string path = GetPath();
            string fullPath = ProjectSettings.GlobalizePath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp.tres";
            Error result = ResourceSaver.Save(Settings, temporary);
            if (result != Error.Ok) throw new IOException(result.ToString());
            File.Move(ProjectSettings.GlobalizePath(temporary), fullPath, true);
            temporary = "";
            Dirty = false;
            LastError = "";
            return true;
        }
        catch (Exception error) { LastError = error.Message; return false; }
        finally { if (temporary.Length > 0) try { File.Delete(ProjectSettings.GlobalizePath(temporary)); } catch { } }
    }

    private void Load()
    {
        Settings = new GameplaySurfaceTerrainSettings();
        LastError = "";
        string path = GetPath();
        if (!ResourceLoader.Exists(path)) { Dirty = false; return; }
        try
        {
            GameplaySurfaceTerrainSettings? loaded = ResourceLoader.Load<GameplaySurfaceTerrainSettings>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid surface terrain settings"; return; }
            Settings.BrushRadiusMeters = ClampBrushRadiusMeters(loaded.BrushRadiusMeters);
            Settings.BrushStepMeters = ClampBrushStepMeters(loaded.BrushStepMeters);
            Settings.MeshLods = ClampMeshLods(loaded.MeshLods);
            Settings.MoveSpeedMetersPerSecond = ClampMoveSpeedMetersPerSecond(loaded.MoveSpeedMetersPerSecond);
        }
        catch (Exception error) { LastError = error.Message; }
        Dirty = false;
    }

    private void ClampSettings()
    {
        Settings.BrushRadiusMeters = ClampBrushRadiusMeters(Settings.BrushRadiusMeters);
        Settings.BrushStepMeters = ClampBrushStepMeters(Settings.BrushStepMeters);
        Settings.MeshLods = ClampMeshLods(Settings.MeshLods);
        Settings.MoveSpeedMetersPerSecond = ClampMoveSpeedMetersPerSecond(Settings.MoveSpeedMetersPerSecond);
    }

    public static float ClampBrushRadiusMeters(float value) => float.IsFinite(value) ? Mathf.Clamp(value, MinimumBrushRadiusMeters, MaximumBrushRadiusMeters) : 8.0f;
    public static float ClampBrushStepMeters(float value) => float.IsFinite(value) ? Mathf.Clamp(value, MinimumBrushStepMeters, MaximumBrushStepMeters) : 2.0f;
    public static int ClampMeshLods(int value) => Mathf.Clamp(value, MinimumMeshLods, MaximumMeshLods);
    public static float ClampMoveSpeedMetersPerSecond(float value) => float.IsFinite(value) ? Mathf.Clamp(value, MinimumMoveSpeedMetersPerSecond, MaximumMoveSpeedMetersPerSecond) : 12.0f;
}
