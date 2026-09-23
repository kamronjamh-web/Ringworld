using System;
using System.IO;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Loads, validates, saves, and applies the player's gameplay AA preference.</summary>
public sealed class GameplayAntiAliasingDefaults
{
    public const string UserPath = "user://gameplay_anti_aliasing_defaults.tres";
    public const string RuntimeModeSetting = "ringworld/runtime/anti_aliasing_mode";
    private readonly string? _pathOverride;

    public GameplayAntiAliasingSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public bool Initialized { get; private set; }
    public string LastError { get; private set; } = "";

    public GameplayAntiAliasingDefaults(string? pathOverride = null) => _pathOverride = pathOverride;
    public string GetPath() => _pathOverride ?? UserPath;

    public void Initialize(Viewport viewport)
    {
        if (Initialized) return;
        Load();
        SetRuntimeMode(Settings.Mode);
        Apply(viewport);
        Initialized = true;
    }

    public bool SetMode(Viewport viewport, AntiAliasingMode mode)
    {
        mode = Validate(mode);
        if (Settings.Mode == mode) return false;
        Settings.Mode = mode;
        SetRuntimeMode(mode);
        Dirty = true;
        Apply(viewport);
        return true;
    }

    public void Reset(Viewport viewport)
    {
        Settings = new GameplayAntiAliasingSettings();
        SetRuntimeMode(Settings.Mode);
        LastError = "";
        Dirty = true;
        Apply(viewport);
    }

    public void Apply(Viewport viewport)
    {
        ApplyMode(viewport, Settings.Mode);
    }

    /// <summary>Reapplies the in-session preference after another menu changes FSR mode.</summary>
    public static void ReapplyRuntimePreference(Viewport viewport)
    {
        Variant stored = ProjectSettings.GetSetting(RuntimeModeSetting, (int)AntiAliasingMode.Fxaa);
        AntiAliasingMode mode = stored.VariantType is Variant.Type.Int or Variant.Type.Float
            ? Validate((AntiAliasingMode)stored.AsInt32())
            : AntiAliasingMode.Fxaa;
        ApplyMode(viewport, mode);
    }

    private static void ApplyMode(Viewport viewport, AntiAliasingMode requested)
    {
        // FSR 2 supplies its own temporal reconstruction. Keeping MSAA or a
        // screen-space pass active here is both wasteful and renderer-dependent.
        if (viewport.Scaling3DMode == Viewport.Scaling3DModeEnum.Fsr2)
        {
            if (viewport.UseTaa) viewport.UseTaa = false;
            if (viewport.ScreenSpaceAA != Viewport.ScreenSpaceAAEnum.Disabled)
                viewport.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled;
            if (viewport.Msaa3D != Viewport.Msaa.Disabled) viewport.Msaa3D = Viewport.Msaa.Disabled;
            return;
        }

        AntiAliasingMode effective = GetRendererSafeMode(requested);
        bool useTaa = effective == AntiAliasingMode.Taa;
        Viewport.ScreenSpaceAAEnum screenSpaceAa = effective switch
        {
            AntiAliasingMode.Fxaa => Viewport.ScreenSpaceAAEnum.Fxaa,
            AntiAliasingMode.Smaa => Viewport.ScreenSpaceAAEnum.Smaa,
            _ => Viewport.ScreenSpaceAAEnum.Disabled
        };
        Viewport.Msaa msaa = effective switch
        {
            AntiAliasingMode.Msaa2X => Viewport.Msaa.Msaa2X,
            AntiAliasingMode.Msaa4X => Viewport.Msaa.Msaa4X,
            AntiAliasingMode.Msaa8X => Viewport.Msaa.Msaa8X,
            _ => Viewport.Msaa.Disabled
        };
        if (viewport.UseTaa != useTaa) viewport.UseTaa = useTaa;
        if (viewport.ScreenSpaceAA != screenSpaceAa) viewport.ScreenSpaceAA = screenSpaceAa;
        if (viewport.Msaa3D != msaa) viewport.Msaa3D = msaa;
    }

    public string GetEffectiveStatus(Viewport viewport)
    {
        string scale = $"{viewport.Scaling3DScale * 100f:0}% render scale";
        if (viewport.Scaling3DMode == Viewport.Scaling3DModeEnum.Fsr2)
            return $"Effective: FSR 2.2 temporal AA at {scale} (requested {GetModeName(Settings.Mode)}).";
        string active = viewport.UseTaa
            ? "TAA"
            : viewport.Msaa3D switch
            {
                Viewport.Msaa.Msaa2X => "MSAA 2x",
                Viewport.Msaa.Msaa4X => "MSAA 4x",
                Viewport.Msaa.Msaa8X => "MSAA 8x",
                _ => viewport.ScreenSpaceAA switch
                {
                    Viewport.ScreenSpaceAAEnum.Fxaa => "FXAA",
                    Viewport.ScreenSpaceAAEnum.Smaa => "SMAA",
                    _ => "Off"
                }
            };
        AntiAliasingMode supported = GetRendererSafeMode(Settings.Mode);
        string availability = supported == Settings.Mode
            ? ""
            : $" Requested {GetModeName(Settings.Mode)} is unavailable on this renderer.";
        return $"Effective: {active} at {scale}.{availability}";
    }

    public static string GetModeName(AntiAliasingMode mode) => mode switch
    {
        AntiAliasingMode.Off => "Off",
        AntiAliasingMode.Fxaa => "FXAA",
        AntiAliasingMode.Smaa => "SMAA",
        AntiAliasingMode.Taa => "TAA",
        AntiAliasingMode.Msaa2X => "MSAA 2x",
        AntiAliasingMode.Msaa4X => "MSAA 4x",
        AntiAliasingMode.Msaa8X => "MSAA 8x",
        _ => "FXAA"
    };

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

    private void Load()
    {
        Settings = new GameplayAntiAliasingSettings();
        LastError = "";
        string path = GetPath();
        if (!ResourceLoader.Exists(path)) { Dirty = false; return; }
        try
        {
            GameplayAntiAliasingSettings? loaded = ResourceLoader.Load<GameplayAntiAliasingSettings>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid anti-aliasing settings"; return; }
            Settings.Mode = Validate(loaded.Mode);
        }
        catch (Exception error) { LastError = error.Message; }
        Dirty = false;
    }

    private static void SetRuntimeMode(AntiAliasingMode mode) => ProjectSettings.SetSetting(RuntimeModeSetting, (int)mode);
    private static AntiAliasingMode Validate(AntiAliasingMode mode) => mode is >= AntiAliasingMode.Off and <= AntiAliasingMode.Msaa8X ? mode : AntiAliasingMode.Fxaa;
    private static AntiAliasingMode GetRendererSafeMode(AntiAliasingMode mode)
    {
        string renderer = RenderingServer.GetCurrentRenderingMethod();
        if (mode == AntiAliasingMode.Taa && renderer != "forward_plus") return AntiAliasingMode.Off;
        if ((mode is AntiAliasingMode.Fxaa or AntiAliasingMode.Smaa) && renderer == "gl_compatibility") return AntiAliasingMode.Off;
        return mode;
    }
}
