using System;
using System.IO;
using Godot;
using RingworldTechDemo.Poi;

namespace RingworldTechDemo.UI;

/// <summary>Saved Weather Lung choices shared with the grave-key drawer (user://gameplay_weather_lung_defaults.tres).</summary>
public sealed class GameplayWeatherLungDefaults
{
    public const string UserPath = "user://gameplay_weather_lung_defaults.tres";
    public const float MinimumCycleSeconds = 30f, MaximumCycleSeconds = 1800f;
    public const float MinimumAmplitudeDeg = 0f, MaximumAmplitudeDeg = 35f;
    public const float MinimumTurbineRpm = 0f, MaximumTurbineRpm = 6f;
    public const float MinimumRippleLag = 0f, MaximumRippleLag = 0.05f;
    public const float MinimumEmissionScale = 0f, MaximumEmissionScale = 4f;
    public const int MinimumSunMode = 0, MaximumSunMode = 3;
    public const float MinimumFullRateMeters = 1000f, MaximumFullRateMeters = 20000f;
    public const float MinimumReducedRateMeters = 8000f, MaximumReducedRateMeters = 30000f;
    public const float MinimumReducedRateHz = 1f, MaximumReducedRateHz = 60f;
    public const float MinimumDetailRangeMeters = 10000f, MaximumDetailRangeMeters = 40000f;
    public const float MinimumDressingRangeMeters = 500f, MaximumDressingRangeMeters = 6000f;
    public const float MinimumDressingFadeMeters = 0f, MaximumDressingFadeMeters = 1000f;
    public const float MinimumFarProxyMaxKm = 50f, MaximumFarProxyMaxKm = 850f;
    public const float MinimumDetailLoadMeters = 14000f, MaximumDetailLoadMeters = 60000f;
    public const float MinimumUnloadMarginMeters = 1000f, MaximumUnloadMarginMeters = 20000f;
    public const float MinimumCollisionRadiusMeters = 1000f, MaximumCollisionRadiusMeters = 8000f;
    public const float MinimumAttachBudgetMs = 1f, MaximumAttachBudgetMs = 16f;

    private readonly string _path;
    public GameplayWeatherLungSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";

    public GameplayWeatherLungDefaults(string? pathOverride = null) => _path = pathOverride ?? UserPath;

    /// <summary>True once the player has saved a choice; otherwise the streamer keeps its own defaults.</summary>
    public bool HasSavedFile => ResourceLoader.Exists(_path);

    public void Load()
    {
        Settings = new();
        LastError = "";
        Dirty = false;
        try
        {
            if (!ResourceLoader.Exists(_path)) return;
            using var loaded = ResourceLoader.Load<GameplayWeatherLungSettings>(_path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid Weather Lung settings"; return; }
            using var clean = Sanitize(loaded);
            Copy(clean, Settings);
        }
        catch (Exception error) { LastError = error.Message; }
    }

    private static float Finite(float value, float fallback, float min, float max) => float.IsFinite(value) ? Mathf.Clamp(value, min, max) : fallback;

    /// <summary>The Detail load radius must cover the Detail range (the FarProxy begin) plus 4 km.</summary>
    private static float LoadFor(float load, float range) => Mathf.Max(load, range + WeatherLungTuning.LoadRadiusOverRangeMeters);

    /// <summary>Clamps every field; non-finite values fall back to the defaults; the load radius is raised to detail range + 4 km.</summary>
    public static GameplayWeatherLungSettings Sanitize(GameplayWeatherLungSettings input)
    {
        float range = Finite(input.DetailRangeMeters, 28000f, MinimumDetailRangeMeters, MaximumDetailRangeMeters);
        return new()
        {
            Enabled = input.Enabled,
            AnimationEnabled = input.AnimationEnabled,
            CycleSeconds = Finite(input.CycleSeconds, 180f, MinimumCycleSeconds, MaximumCycleSeconds),
            VaneAmplitudeDeg = Finite(input.VaneAmplitudeDeg, 35f, MinimumAmplitudeDeg, MaximumAmplitudeDeg),
            GillAmplitudeDeg = Finite(input.GillAmplitudeDeg, 35f, MinimumAmplitudeDeg, MaximumAmplitudeDeg),
            TurbineMaxRpm = Finite(input.TurbineMaxRpm, 1.5f, MinimumTurbineRpm, MaximumTurbineRpm),
            RippleLag = Finite(input.RippleLag, 0.012f, MinimumRippleLag, MaximumRippleLag),
            NozzleEmissionScale = Finite(input.NozzleEmissionScale, 0.25f, MinimumEmissionScale, MaximumEmissionScale),
            HeatGridEmissionScale = Finite(input.HeatGridEmissionScale, 1f, MinimumEmissionScale, MaximumEmissionScale),
            SunMode = Mathf.Clamp(input.SunMode, MinimumSunMode, MaximumSunMode),
            SunManual = Finite(input.SunManual, 1f, 0f, 1f),
            FullRateMeters = Finite(input.FullRateMeters, 8000f, MinimumFullRateMeters, MaximumFullRateMeters),
            ReducedRateMeters = Finite(input.ReducedRateMeters, 28000f, MinimumReducedRateMeters, MaximumReducedRateMeters),
            ReducedRateHz = Finite(input.ReducedRateHz, 10f, MinimumReducedRateHz, MaximumReducedRateHz),
            DetailRangeMeters = range,
            DressingRangeMeters = Finite(input.DressingRangeMeters, 2000f, MinimumDressingRangeMeters, MaximumDressingRangeMeters),
            DressingFadeMeters = Finite(input.DressingFadeMeters, 250f, MinimumDressingFadeMeters, MaximumDressingFadeMeters),
            FarProxyMaxKm = Finite(input.FarProxyMaxKm, 700f, MinimumFarProxyMaxKm, MaximumFarProxyMaxKm),
            DetailLoadRadiusMeters = LoadFor(Finite(input.DetailLoadRadiusMeters, 36000f, MinimumDetailLoadMeters, MaximumDetailLoadMeters), range),
            UnloadMarginMeters = Finite(input.UnloadMarginMeters, 8000f, MinimumUnloadMarginMeters, MaximumUnloadMarginMeters),
            CollisionRadiusMeters = Finite(input.CollisionRadiusMeters, 3000f, MinimumCollisionRadiusMeters, MaximumCollisionRadiusMeters),
            DetailAttachBudgetMs = Finite(input.DetailAttachBudgetMs, 4f, MinimumAttachBudgetMs, MaximumAttachBudgetMs)
        };
    }

    private static void Copy(GameplayWeatherLungSettings from, GameplayWeatherLungSettings to)
    {
        to.Enabled = from.Enabled;
        to.AnimationEnabled = from.AnimationEnabled;
        to.CycleSeconds = from.CycleSeconds;
        to.VaneAmplitudeDeg = from.VaneAmplitudeDeg;
        to.GillAmplitudeDeg = from.GillAmplitudeDeg;
        to.TurbineMaxRpm = from.TurbineMaxRpm;
        to.RippleLag = from.RippleLag;
        to.NozzleEmissionScale = from.NozzleEmissionScale;
        to.HeatGridEmissionScale = from.HeatGridEmissionScale;
        to.SunMode = from.SunMode;
        to.SunManual = from.SunManual;
        to.FullRateMeters = from.FullRateMeters;
        to.ReducedRateMeters = from.ReducedRateMeters;
        to.ReducedRateHz = from.ReducedRateHz;
        to.DetailRangeMeters = from.DetailRangeMeters;
        to.DressingRangeMeters = from.DressingRangeMeters;
        to.DressingFadeMeters = from.DressingFadeMeters;
        to.FarProxyMaxKm = from.FarProxyMaxKm;
        to.DetailLoadRadiusMeters = from.DetailLoadRadiusMeters;
        to.UnloadMarginMeters = from.UnloadMarginMeters;
        to.CollisionRadiusMeters = from.CollisionRadiusMeters;
        to.DetailAttachBudgetMs = from.DetailAttachBudgetMs;
    }

    public bool SetEnabled(bool value) => Change(Settings.Enabled != value, () => Settings.Enabled = value);
    public bool SetAnimationEnabled(bool value) => Change(Settings.AnimationEnabled != value, () => Settings.AnimationEnabled = value);
    public bool SetCycleSeconds(float value) { float v = Finite(value, 180f, MinimumCycleSeconds, MaximumCycleSeconds); return Change(Settings.CycleSeconds != v, () => Settings.CycleSeconds = v); }
    public bool SetVaneAmplitudeDeg(float value) { float v = Finite(value, 35f, MinimumAmplitudeDeg, MaximumAmplitudeDeg); return Change(Settings.VaneAmplitudeDeg != v, () => Settings.VaneAmplitudeDeg = v); }
    public bool SetGillAmplitudeDeg(float value) { float v = Finite(value, 35f, MinimumAmplitudeDeg, MaximumAmplitudeDeg); return Change(Settings.GillAmplitudeDeg != v, () => Settings.GillAmplitudeDeg = v); }
    public bool SetTurbineMaxRpm(float value) { float v = Finite(value, 1.5f, MinimumTurbineRpm, MaximumTurbineRpm); return Change(Settings.TurbineMaxRpm != v, () => Settings.TurbineMaxRpm = v); }
    public bool SetRippleLag(float value) { float v = Finite(value, 0.012f, MinimumRippleLag, MaximumRippleLag); return Change(Settings.RippleLag != v, () => Settings.RippleLag = v); }
    public bool SetNozzleEmissionScale(float value) { float v = Finite(value, 0.25f, MinimumEmissionScale, MaximumEmissionScale); return Change(Settings.NozzleEmissionScale != v, () => Settings.NozzleEmissionScale = v); }
    public bool SetHeatGridEmissionScale(float value) { float v = Finite(value, 1f, MinimumEmissionScale, MaximumEmissionScale); return Change(Settings.HeatGridEmissionScale != v, () => Settings.HeatGridEmissionScale = v); }
    public bool SetSunMode(int value) { int v = Mathf.Clamp(value, MinimumSunMode, MaximumSunMode); return Change(Settings.SunMode != v, () => Settings.SunMode = v); }
    public bool SetSunManual(float value) { float v = Finite(value, 1f, 0f, 1f); return Change(Settings.SunManual != v, () => Settings.SunManual = v); }
    public bool SetFullRateMeters(float value) { float v = Finite(value, 8000f, MinimumFullRateMeters, MaximumFullRateMeters); return Change(Settings.FullRateMeters != v, () => Settings.FullRateMeters = v); }
    public bool SetReducedRateMeters(float value) { float v = Finite(value, 28000f, MinimumReducedRateMeters, MaximumReducedRateMeters); return Change(Settings.ReducedRateMeters != v, () => Settings.ReducedRateMeters = v); }
    public bool SetReducedRateHz(float value) { float v = Finite(value, 10f, MinimumReducedRateHz, MaximumReducedRateHz); return Change(Settings.ReducedRateHz != v, () => Settings.ReducedRateHz = v); }
    public bool SetDressingRangeMeters(float value) { float v = Finite(value, 2000f, MinimumDressingRangeMeters, MaximumDressingRangeMeters); return Change(Settings.DressingRangeMeters != v, () => Settings.DressingRangeMeters = v); }
    public bool SetDressingFadeMeters(float value) { float v = Finite(value, 250f, MinimumDressingFadeMeters, MaximumDressingFadeMeters); return Change(Settings.DressingFadeMeters != v, () => Settings.DressingFadeMeters = v); }
    public bool SetFarProxyMaxKm(float value) { float v = Finite(value, 700f, MinimumFarProxyMaxKm, MaximumFarProxyMaxKm); return Change(Settings.FarProxyMaxKm != v, () => Settings.FarProxyMaxKm = v); }
    public bool SetUnloadMarginMeters(float value) { float v = Finite(value, 8000f, MinimumUnloadMarginMeters, MaximumUnloadMarginMeters); return Change(Settings.UnloadMarginMeters != v, () => Settings.UnloadMarginMeters = v); }
    public bool SetCollisionRadiusMeters(float value) { float v = Finite(value, 3000f, MinimumCollisionRadiusMeters, MaximumCollisionRadiusMeters); return Change(Settings.CollisionRadiusMeters != v, () => Settings.CollisionRadiusMeters = v); }
    public bool SetDetailAttachBudgetMs(float value) { float v = Finite(value, 4f, MinimumAttachBudgetMs, MaximumAttachBudgetMs); return Change(Settings.DetailAttachBudgetMs != v, () => Settings.DetailAttachBudgetMs = v); }

    /// <summary>Raising the range also raises the load radius so that it stays at least range + 4 km.</summary>
    public bool SetDetailRangeMeters(float value)
    {
        float v = Finite(value, 28000f, MinimumDetailRangeMeters, MaximumDetailRangeMeters);
        bool changed = Change(Settings.DetailRangeMeters != v, () => Settings.DetailRangeMeters = v);
        float load = LoadFor(Settings.DetailLoadRadiusMeters, v);
        changed |= Change(Settings.DetailLoadRadiusMeters != load, () => Settings.DetailLoadRadiusMeters = load);
        return changed;
    }

    /// <summary>Clamped, and never below the current detail range + 4 km.</summary>
    public bool SetDetailLoadRadiusMeters(float value)
    {
        float v = LoadFor(Finite(value, 36000f, MinimumDetailLoadMeters, MaximumDetailLoadMeters), Settings.DetailRangeMeters);
        return Change(Settings.DetailLoadRadiusMeters != v, () => Settings.DetailLoadRadiusMeters = v);
    }

    private bool Change(bool differs, Action apply)
    {
        if (!differs) return false;
        apply();
        Dirty = true;
        return true;
    }

    /// <summary>The saved choices as the value bundle the streamer and controller consume.</summary>
    public WeatherLungTuning ToTuning()
    {
        GameplayWeatherLungSettings s = Settings;
        return new WeatherLungTuning(s.Enabled, s.AnimationEnabled, s.CycleSeconds, s.VaneAmplitudeDeg, s.GillAmplitudeDeg, s.TurbineMaxRpm, s.RippleLag,
            s.NozzleEmissionScale, s.HeatGridEmissionScale, (WeatherLungSunMode)s.SunMode, s.SunManual, s.FullRateMeters, s.ReducedRateMeters, s.ReducedRateHz,
            s.DetailRangeMeters, s.DressingRangeMeters, s.DressingFadeMeters, s.FarProxyMaxKm, s.DetailLoadRadiusMeters, s.UnloadMarginMeters, s.CollisionRadiusMeters, s.DetailAttachBudgetMs);
    }

    /// <summary>Copies the saved choices onto the world streamer (and through it the controller).</summary>
    public void Apply(WeatherLungStreamer? streamer)
    {
        if (streamer == null || !GodotObject.IsInstanceValid(streamer)) return;
        streamer.Configure(ToTuning());
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
            if (ResourceSaver.Save(Settings, temporary) != Error.Ok) throw new IOException("Could not save Weather Lung settings");
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
