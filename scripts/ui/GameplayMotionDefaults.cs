using System;
using System.IO;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Loads, persists, and applies the player's first-person motion preference.</summary>
public sealed class GameplayMotionDefaults
{
    public const string UserPath = "user://gameplay_motion_defaults.tres";
    public const float MinimumWalkableSlopeDegrees = 35.0f;
    public const float MaximumWalkableSlopeDegrees = 70.0f;
    private readonly string? _pathOverride;

    public GameplayMotionSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public bool Initialized { get; private set; }
    public string LastError { get; private set; } = "";

    public GameplayMotionDefaults(string? pathOverride = null) => _pathOverride = pathOverride;
    public string GetPath() => _pathOverride ?? UserPath;

    public void Initialize(SceneTree tree, Node? explorer)
    {
        if (Initialized) return;
        Load();
        Apply(tree, explorer);
        Initialized = true;
    }

    public bool SetPhysicsInterpolation(SceneTree tree, bool enabled)
    {
        if (Settings.PhysicsInterpolationEnabled == enabled) return false;
        Settings.PhysicsInterpolationEnabled = enabled;
        Dirty = true;
        Apply(tree, null);
        return true;
    }

    public bool SetMaxWalkableSlopeDegrees(Node? explorer, float degrees)
    {
        degrees = ClampWalkableSlopeDegrees(degrees);
        if (Mathf.IsEqualApprox(Settings.MaxWalkableSlopeDegrees, degrees)) return false;
        Settings.MaxWalkableSlopeDegrees = degrees;
        Dirty = true;
        ApplyExplorerSlope(explorer);
        return true;
    }

    public void Reset(SceneTree tree, Node? explorer)
    {
        Settings = new GameplayMotionSettings();
        LastError = "";
        Dirty = true;
        Apply(tree, explorer);
    }

    public void Apply(SceneTree tree, Node? explorer)
    {
        if (tree.PhysicsInterpolation != Settings.PhysicsInterpolationEnabled)
            tree.PhysicsInterpolation = Settings.PhysicsInterpolationEnabled;
        ApplyExplorerSlope(explorer);
    }

    public bool Save()
    {
        if (!Dirty) return true;
        string temporary = "";
        try
        {
            Settings.MaxWalkableSlopeDegrees = ClampWalkableSlopeDegrees(Settings.MaxWalkableSlopeDegrees);
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

    private void Load()
    {
        Settings = new GameplayMotionSettings();
        LastError = "";
        string path = GetPath();
        if (!ResourceLoader.Exists(path)) { Dirty = false; return; }
        try
        {
            GameplayMotionSettings? loaded = ResourceLoader.Load<GameplayMotionSettings>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid gameplay motion settings"; return; }
            Settings.PhysicsInterpolationEnabled = loaded.PhysicsInterpolationEnabled;
            Settings.MaxWalkableSlopeDegrees = ClampWalkableSlopeDegrees(loaded.MaxWalkableSlopeDegrees);
        }
        catch (Exception error) { LastError = error.Message; }
        Dirty = false;
    }

    private void ApplyExplorerSlope(Node? explorer)
    {
        explorer?.Call("SetMaxWalkableSlopeDegrees", Settings.MaxWalkableSlopeDegrees);
    }

    public static float ClampWalkableSlopeDegrees(float degrees) => float.IsFinite(degrees)
        ? Mathf.Clamp(degrees, MinimumWalkableSlopeDegrees, MaximumWalkableSlopeDegrees)
        : 55.0f;
}
