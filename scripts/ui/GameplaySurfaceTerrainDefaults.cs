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
    public const int MinimumGenerationWorkerCount = 1;
    public const int MaximumGenerationWorkerCount = 4;
    public const float MinimumGenerationPrefetchMeters = 16.0f;
    public const float MaximumGenerationPrefetchMeters = 160.0f;
    public const int MinimumGenerationCacheMiB = 1;
    public const int MaximumGenerationCacheMiB = 64;
    public const float MinimumGenerationUploadBudgetMs = .1f;
    public const float MaximumGenerationUploadBudgetMs = 8f;
    public const float MinimumGenerationCollisionBudgetMs = .1f;
    public const float MaximumGenerationCollisionBudgetMs = 8f;
    public const float MinimumDryRoughness = .65f;
    public const float MaximumDryRoughness = .98f;
    public const float MinimumWetRoughness = .10f;
    public const float MaximumWetRoughness = .75f;
    public const float MinimumRainWetnessStrength = 0f;
    public const float MaximumRainWetnessStrength = 1f;

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
    public bool SetGenerationWorkerCount(int value) { value = ClampGenerationWorkerCount(value); if (Settings.GenerationWorkerCount == value) return false; Settings.GenerationWorkerCount = value; Dirty = true; return true; }
    public bool SetGenerationPrefetchMeters(float value) { value = ClampGenerationPrefetchMeters(value); if (Mathf.IsEqualApprox(Settings.GenerationPrefetchMeters, value)) return false; Settings.GenerationPrefetchMeters = value; Dirty = true; return true; }
    public bool SetGenerationCacheMiB(int value) { value = ClampGenerationCacheMiB(value); if (Settings.GenerationCacheMiB == value) return false; Settings.GenerationCacheMiB = value; Dirty = true; return true; }
    public bool SetGenerationUploadBudgetMs(float value) { value = ClampGenerationUploadBudgetMs(value); if (Mathf.IsEqualApprox(Settings.GenerationUploadBudgetMs, value)) return false; Settings.GenerationUploadBudgetMs = value; Dirty = true; return true; }
    public bool SetGenerationCollisionBudgetMs(float value) { value = ClampGenerationCollisionBudgetMs(value); if (Mathf.IsEqualApprox(Settings.GenerationCollisionBudgetMs, value)) return false; Settings.GenerationCollisionBudgetMs = value; Dirty = true; return true; }
    public bool SetGenerationColliderTileCells(int value) { value = ClampGenerationColliderTileCells(value); if (Settings.GenerationColliderTileCells == value) return false; Settings.GenerationColliderTileCells = value; Dirty = true; return true; }
    public bool SetDryRoughness(float value) { value = ClampDryRoughness(value); if (Mathf.IsEqualApprox(Settings.DryRoughness, value)) return false; Settings.DryRoughness = value; Dirty = true; return true; }
    public bool SetWetRoughness(float value) { value = ClampWetRoughness(value); if (Mathf.IsEqualApprox(Settings.WetRoughness, value)) return false; Settings.WetRoughness = value; Dirty = true; return true; }
    public bool SetRainWetnessStrength(float value) { value = ClampRainWetnessStrength(value); if (Mathf.IsEqualApprox(Settings.RainWetnessStrength, value)) return false; Settings.RainWetnessStrength = value; Dirty = true; return true; }
    public bool SetSmoothTerrainShading(bool value) { if (Settings.SmoothTerrainShading == value) return false; Settings.SmoothTerrainShading = value; Dirty = true; return true; }

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
            Settings.GenerationWorkerCount = ClampGenerationWorkerCount(loaded.GenerationWorkerCount);
            Settings.GenerationPrefetchMeters = ClampGenerationPrefetchMeters(loaded.GenerationPrefetchMeters);
            Settings.GenerationCacheMiB = ClampGenerationCacheMiB(loaded.GenerationCacheMiB);
            Settings.GenerationUploadBudgetMs = ClampGenerationUploadBudgetMs(loaded.GenerationUploadBudgetMs);
            Settings.GenerationCollisionBudgetMs = ClampGenerationCollisionBudgetMs(loaded.GenerationCollisionBudgetMs);
            Settings.GenerationColliderTileCells = ClampGenerationColliderTileCells(loaded.GenerationColliderTileCells);
            Settings.DryRoughness = ClampDryRoughness(loaded.DryRoughness);
            Settings.WetRoughness = ClampWetRoughness(loaded.WetRoughness);
            Settings.RainWetnessStrength = ClampRainWetnessStrength(loaded.RainWetnessStrength);
            Settings.SmoothTerrainShading = loaded.SmoothTerrainShading;
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
        Settings.GenerationWorkerCount = ClampGenerationWorkerCount(Settings.GenerationWorkerCount);
        Settings.GenerationPrefetchMeters = ClampGenerationPrefetchMeters(Settings.GenerationPrefetchMeters);
        Settings.GenerationCacheMiB = ClampGenerationCacheMiB(Settings.GenerationCacheMiB);
        Settings.GenerationUploadBudgetMs = ClampGenerationUploadBudgetMs(Settings.GenerationUploadBudgetMs);
        Settings.GenerationCollisionBudgetMs = ClampGenerationCollisionBudgetMs(Settings.GenerationCollisionBudgetMs);
        Settings.GenerationColliderTileCells = ClampGenerationColliderTileCells(Settings.GenerationColliderTileCells);
        Settings.DryRoughness = ClampDryRoughness(Settings.DryRoughness);
        Settings.WetRoughness = ClampWetRoughness(Settings.WetRoughness);
        Settings.RainWetnessStrength = ClampRainWetnessStrength(Settings.RainWetnessStrength);
    }

    public static float ClampBrushRadiusMeters(float value) => float.IsFinite(value) ? Mathf.Clamp(value, MinimumBrushRadiusMeters, MaximumBrushRadiusMeters) : 8.0f;
    public static float ClampBrushStepMeters(float value) => float.IsFinite(value) ? Mathf.Clamp(value, MinimumBrushStepMeters, MaximumBrushStepMeters) : 2.0f;
    public static int ClampMeshLods(int value) => Mathf.Clamp(value, MinimumMeshLods, MaximumMeshLods);
    public static float ClampMoveSpeedMetersPerSecond(float value) => float.IsFinite(value) ? Mathf.Clamp(value, MinimumMoveSpeedMetersPerSecond, MaximumMoveSpeedMetersPerSecond) : 12.0f;
    public static int ClampGenerationWorkerCount(int value) => Mathf.Clamp(value, MinimumGenerationWorkerCount, MaximumGenerationWorkerCount);
    public static float ClampGenerationPrefetchMeters(float value) => float.IsFinite(value) ? Mathf.Clamp(value, MinimumGenerationPrefetchMeters, MaximumGenerationPrefetchMeters) : 64.0f;
    public static int ClampGenerationCacheMiB(int value) => Mathf.Clamp(value, MinimumGenerationCacheMiB, MaximumGenerationCacheMiB);
    public static float ClampGenerationUploadBudgetMs(float value) => float.IsFinite(value) ? Mathf.Clamp(value, MinimumGenerationUploadBudgetMs, MaximumGenerationUploadBudgetMs) : 1f;
    public static float ClampGenerationCollisionBudgetMs(float value) => float.IsFinite(value) ? Mathf.Clamp(value, MinimumGenerationCollisionBudgetMs, MaximumGenerationCollisionBudgetMs) : .75f;
    public static int ClampGenerationColliderTileCells(int value) => value == 16 ? 16 : 32;
    public static float ClampDryRoughness(float value) => float.IsFinite(value) ? Mathf.Clamp(value, MinimumDryRoughness, MaximumDryRoughness) : .92f;
    public static float ClampWetRoughness(float value) => float.IsFinite(value) ? Mathf.Clamp(value, MinimumWetRoughness, MaximumWetRoughness) : .35f;
    public static float ClampRainWetnessStrength(float value) => float.IsFinite(value) ? Mathf.Clamp(value, MinimumRainWetnessStrength, MaximumRainWetnessStrength) : 1f;
}
