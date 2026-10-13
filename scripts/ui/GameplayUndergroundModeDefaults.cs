using System;
using System.IO;
using Godot;
using RingworldTechDemo.Underground;

namespace RingworldTechDemo.UI;

/// <summary>Saved underground-mode enclosure thresholds shared with the grave-key drawer.</summary>
public sealed class GameplayUndergroundModeDefaults
{
    public const string UserPath = "user://gameplay_underground_mode_defaults.tres";
    public const float MinimumDepth = 1f, MaximumDepth = 256f;
    public const float MinimumOpenness = 0f, MaximumOpenness = 1f;
    public const float MaximumEnterHold = 5f, MaximumExitHold = 2f;
    public const float MinimumBlend = .1f, MaximumBlend = 5f;
    private readonly string _path;
    public GameplayUndergroundModeSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";

    public GameplayUndergroundModeDefaults(string? pathOverride = null) => _path = pathOverride ?? UserPath;

    /// <summary>True once the player has saved a choice; otherwise the controller keeps its own defaults.</summary>
    public bool HasSavedFile => ResourceLoader.Exists(_path);

    public void Load()
    {
        Settings = new();
        LastError = "";
        Dirty = false;
        try
        {
            if (!ResourceLoader.Exists(_path)) return;
            using var loaded = ResourceLoader.Load<GameplayUndergroundModeSettings>(_path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid underground mode settings"; return; }
            using var clean = Sanitize(loaded);
            Copy(clean, Settings);
        }
        catch (Exception error) { LastError = error.Message; }
    }

    /// <summary>Clamps every field and keeps exit thresholds on the permissive side of enter thresholds so hysteresis cannot invert.</summary>
    public static GameplayUndergroundModeSettings Sanitize(GameplayUndergroundModeSettings input)
    {
        var result = new GameplayUndergroundModeSettings { Enabled = input.Enabled };
        result.EnterDepthMeters = Finite(input.EnterDepthMeters, 24f, MinimumDepth, MaximumDepth);
        result.ExitDepthMeters = Math.Min(Finite(input.ExitDepthMeters, 12f, MinimumDepth, MaximumDepth), result.EnterDepthMeters);
        result.EnterOpennessMax = Finite(input.EnterOpennessMax, .06f, MinimumOpenness, MaximumOpenness);
        result.ExitOpennessMin = Math.Max(Finite(input.ExitOpennessMin, .18f, MinimumOpenness, MaximumOpenness), result.EnterOpennessMax);
        result.EnterHoldSeconds = Finite(input.EnterHoldSeconds, 1f, 0f, MaximumEnterHold);
        result.ExitHoldSeconds = Finite(input.ExitHoldSeconds, .15f, 0f, MaximumExitHold);
        result.BlendSeconds = Finite(input.BlendSeconds, 1f, MinimumBlend, MaximumBlend);
        return result;
    }

    private static float Finite(float value, float fallback, float min, float max) =>
        float.IsFinite(value) ? Mathf.Clamp(value, min, max) : fallback;

    private static void Copy(GameplayUndergroundModeSettings from, GameplayUndergroundModeSettings to)
    {
        to.Enabled = from.Enabled;
        to.EnterDepthMeters = from.EnterDepthMeters;
        to.ExitDepthMeters = from.ExitDepthMeters;
        to.EnterOpennessMax = from.EnterOpennessMax;
        to.ExitOpennessMin = from.ExitOpennessMin;
        to.EnterHoldSeconds = from.EnterHoldSeconds;
        to.ExitHoldSeconds = from.ExitHoldSeconds;
        to.BlendSeconds = from.BlendSeconds;
    }

    private static bool Same(GameplayUndergroundModeSettings a, GameplayUndergroundModeSettings b) =>
        a.Enabled == b.Enabled && a.EnterDepthMeters == b.EnterDepthMeters && a.ExitDepthMeters == b.ExitDepthMeters
        && a.EnterOpennessMax == b.EnterOpennessMax && a.ExitOpennessMin == b.ExitOpennessMin
        && a.EnterHoldSeconds == b.EnterHoldSeconds && a.ExitHoldSeconds == b.ExitHoldSeconds && a.BlendSeconds == b.BlendSeconds;

    /// <summary>Stores sanitized choices; returns false when nothing changed.</summary>
    public bool Set(GameplayUndergroundModeSettings candidate)
    {
        using var clean = Sanitize(candidate);
        if (Same(Settings, clean)) return false;
        Copy(clean, Settings);
        Dirty = true;
        return true;
    }

    /// <summary>Copies the saved choices onto the controller. Callers only do this once the player has saved a choice.</summary>
    public void Apply(RingUndergroundModeController controller)
    {
        controller.Enabled = Settings.Enabled;
        controller.Parameters = new UndergroundEnclosureParameters
        {
            EnterDepthMeters = Settings.EnterDepthMeters,
            ExitDepthMeters = Settings.ExitDepthMeters,
            EnterOpennessMax = Settings.EnterOpennessMax,
            ExitOpennessMin = Settings.ExitOpennessMin,
            EnterHoldSeconds = Settings.EnterHoldSeconds,
            ExitHoldSeconds = Settings.ExitHoldSeconds,
            BlendSeconds = Settings.BlendSeconds
        };
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
            if (ResourceSaver.Save(Settings, temporary) != Error.Ok) throw new IOException("Could not save underground mode settings");
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
