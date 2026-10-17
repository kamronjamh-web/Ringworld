using System;
using System.IO;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;

namespace RingworldTechDemo.UI;

/// <summary>Saved eclipse false-sun event choices shared with the grave-key drawer (RingLiveTuningDrawer.Eclipse.cs). Mirrors GameplayEmitterGridDefaults.</summary>
public sealed class GameplayEclipseEventDefaults
{
    public const string UserPath = "user://gameplay_eclipse_event_defaults.tres";

    private readonly record struct FloatField(string Name, float Default, float Min, float Max);
    private readonly record struct IntField(string Name, int Default, int Min, int Max);

    private static readonly FloatField[] FloatFields =
    {
        new(nameof(GameplayEclipseEventSettings.ParallaxSafetySeconds), 10f, 0f, 120f),
        new(nameof(GameplayEclipseEventSettings.PreRollSeconds), 300f, 0f, 900f),
        new(nameof(GameplayEclipseEventSettings.DarknessBeatSeconds), 20f, 0f, 600f),
        new(nameof(GameplayEclipseEventSettings.MainClimbSeconds), 4f, 0.5f, 20f),
        new(nameof(GameplayEclipseEventSettings.MainClimbExponent), 2f, 1f, 4f),
        new(nameof(GameplayEclipseEventSettings.KindleSeconds), 2.5f, 0.1f, 10f),
        new(nameof(GameplayEclipseEventSettings.KindleLevel), 0.1f, 0.02f, 0.5f),
        new(nameof(GameplayEclipseEventSettings.IgnitionDelaySeconds), 3f, 0f, 30f),
        new(nameof(GameplayEclipseEventSettings.IgnitionSeconds), 150f, 30f, 600f),
        new(nameof(GameplayEclipseEventSettings.RegularClimbSeconds), 1.5f, 0.2f, 10f),
        new(nameof(GameplayEclipseEventSettings.RampGamma), 1f, 0.5f, 3f),
        new(nameof(GameplayEclipseEventSettings.RampStepSmoothSeconds), 0.5f, 0f, 3f),
        new(nameof(GameplayEclipseEventSettings.IgnitionPlateauLevel), 0.35f, 0f, 1f),
        new(nameof(GameplayEclipseEventSettings.MeetingFlareSeconds), 1.5f, 0f, 10f),
        new(nameof(GameplayEclipseEventSettings.CrescendoSeconds), 8f, 0f, 60f),
        new(nameof(GameplayEclipseEventSettings.HandoffFadeSeconds), 2.5f, 0.2f, 10f),
        new(nameof(GameplayEclipseEventSettings.FlashClimbSeconds), 0.5f, 0.1f, 3f),
        new(nameof(GameplayEclipseEventSettings.FlashHoldSeconds), 0.6f, 0f, 3f),
        new(nameof(GameplayEclipseEventSettings.FlashFadeSeconds), 1.2f, 0.1f, 5f),
        new(nameof(GameplayEclipseEventSettings.HeartbeatKick), 0.02f, 0f, 0.2f),
        new(nameof(GameplayEclipseEventSettings.HeartbeatTauSeconds), 0.8f, 0.1f, 5f),
        new(nameof(GameplayEclipseEventSettings.RelaunchKick), 0.05f, 0f, 0.3f),
        new(nameof(GameplayEclipseEventSettings.BreathAmplitude), 0.015f, 0f, 0.1f),
        new(nameof(GameplayEclipseEventSettings.BreathPeriodSeconds), 11f, 2f, 60f),
        new(nameof(GameplayEclipseEventSettings.CollapseLeadSeconds), 75f, 20f, 600f),
        new(nameof(GameplayEclipseEventSettings.InstabilitySeconds), 45f, 5f, 300f),
        new(nameof(GameplayEclipseEventSettings.FlickerMax), 0.35f, 0f, 0.9f),
        new(nameof(GameplayEclipseEventSettings.FlickerHzStart), 0.5f, 0.1f, 5f),
        new(nameof(GameplayEclipseEventSettings.FlickerHzEnd), 6f, 1f, 20f),
        new(nameof(GameplayEclipseEventSettings.OverloadSeconds), 4f, 0.5f, 20f),
        new(nameof(GameplayEclipseEventSettings.OverloadGain), 2.5f, 1f, 5f),
        new(nameof(GameplayEclipseEventSettings.ImplosionSeconds), 2.5f, 0.3f, 10f),
        new(nameof(GameplayEclipseEventSettings.BeamRetractSeconds), 1f, 0.1f, 5f),
        new(nameof(GameplayEclipseEventSettings.ShockwaveSeconds), 8f, 1f, 30f),
        new(nameof(GameplayEclipseEventSettings.ShockwaveArrivalFraction), 0.75f, 0f, 1f),
        new(nameof(GameplayEclipseEventSettings.ShockFlashLevel), 0.6f, 0f, 2f),
        new(nameof(GameplayEclipseEventSettings.ShockFlashSeconds), 0.4f, 0.05f, 3f),
        new(nameof(GameplayEclipseEventSettings.ReturnTailSeconds), 450f, 0f, 1800f),
        new(nameof(GameplayEclipseEventSettings.RealSunEnergy), 4f, 0f, 16f),
        new(nameof(GameplayEclipseEventSettings.HubDirectFraction), 0.8f, 0f, 1.5f),
        new(nameof(GameplayEclipseEventSettings.HubDayAtFull), 0.8f, 0f, 1f),
        new(nameof(GameplayEclipseEventSettings.HubLightRed), 0.92f, 0f, 1f),
        new(nameof(GameplayEclipseEventSettings.HubLightGreen), 0.97f, 0f, 1f),
        new(nameof(GameplayEclipseEventSettings.HubLightBlue), 1f, 0f, 1f),
        new(nameof(GameplayEclipseEventSettings.HubSkyTintStrength), 0.25f, 0f, 1f),
        new(nameof(GameplayEclipseEventSettings.HubZenithHaloIntensity), 0.35f, 0f, 2f),
        new(nameof(GameplayEclipseEventSettings.EclipseAmbientScale), 0.15f, 0f, 1f),
        new(nameof(GameplayEclipseEventSettings.EclipseAtmosphereNightScale), 0.35f, 0f, 1f),
        new(nameof(GameplayEclipseEventSettings.EclipseSkyNightScale), 0.5f, 0f, 1f),
        new(nameof(GameplayEclipseEventSettings.EclipseCloudAmbientScale), 0.4f, 0.3f, 1f),
        new(nameof(GameplayEclipseEventSettings.EclipseStarVisibility), 1f, 0f, 1f)
    };

    private static readonly IntField[] IntFields =
    {
        new(nameof(GameplayEclipseEventSettings.IgnitionSpeedMode), 0, 0, 1),
        new(nameof(GameplayEclipseEventSettings.RampMode), 0, 0, 1),
        new(nameof(GameplayEclipseEventSettings.RefuelClock), 0, 0, 1)
    };

    private static readonly string[] BoolFields =
    {
        nameof(GameplayEclipseEventSettings.EventEnabled), nameof(GameplayEclipseEventSettings.SuppressGridPulseDuringIgnition),
        nameof(GameplayEclipseEventSettings.EclipseDarknessEnabled)
    };

    private readonly string _path;
    public GameplayEclipseEventSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";

    public GameplayEclipseEventDefaults(string? pathOverride = null) => _path = pathOverride ?? UserPath;

    /// <summary>True once the player has saved a choice; otherwise the director keeps its own defaults.</summary>
    public bool HasSavedFile => ResourceLoader.Exists(_path);

    public static (float Min, float Max) RangeOf(string field)
    {
        foreach (FloatField f in FloatFields) if (f.Name == field) return (f.Min, f.Max);
        foreach (IntField f in IntFields) if (f.Name == field) return (f.Min, f.Max);
        throw new ArgumentException("unknown field " + field, nameof(field));
    }

    public static float DefaultOf(string field)
    {
        foreach (FloatField f in FloatFields) if (f.Name == field) return f.Default;
        foreach (IntField f in IntFields) if (f.Name == field) return f.Default;
        throw new ArgumentException("unknown field " + field, nameof(field));
    }

    public void Load()
    {
        Settings = new();
        LastError = "";
        Dirty = false;
        try
        {
            if (!ResourceLoader.Exists(_path)) return;
            using var loaded = ResourceLoader.Load<GameplayEclipseEventSettings>(_path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid eclipse event settings"; return; }
            using var clean = Sanitize(loaded);
            Copy(clean, Settings);
        }
        catch (Exception error) { LastError = error.Message; }
    }

    /// <summary>Clamps every field; non-finite values fall back to the defaults.</summary>
    public static GameplayEclipseEventSettings Sanitize(GameplayEclipseEventSettings input)
    {
        var result = new GameplayEclipseEventSettings();
        foreach (FloatField f in FloatFields) result.Set(f.Name, Finite((float)input.Get(f.Name).AsDouble(), f.Default, f.Min, f.Max));
        foreach (IntField f in IntFields) result.Set(f.Name, Mathf.Clamp(input.Get(f.Name).AsInt32(), f.Min, f.Max));
        foreach (string name in BoolFields) result.Set(name, input.Get(name).AsBool());
        return result;
    }

    private static void Copy(GameplayEclipseEventSettings from, GameplayEclipseEventSettings to)
    {
        foreach (FloatField f in FloatFields) to.Set(f.Name, from.Get(f.Name));
        foreach (IntField f in IntFields) to.Set(f.Name, from.Get(f.Name));
        foreach (string name in BoolFields) to.Set(name, from.Get(name));
    }

    private static float Finite(float value, float fallback, float min, float max) =>
        float.IsFinite(value) ? Mathf.Clamp(value, min, max) : fallback;

    /// <summary>Clamped float setter by field name; returns true when the stored value changed.</summary>
    public bool SetFloat(string field, float value)
    {
        foreach (FloatField f in FloatFields)
        {
            if (f.Name != field) continue;
            float v = Finite(value, f.Default, f.Min, f.Max);
            if ((float)Settings.Get(field).AsDouble() == v) return false;
            Settings.Set(field, v);
            Dirty = true;
            return true;
        }
        throw new ArgumentException("unknown float field " + field, nameof(field));
    }

    public bool SetInt(string field, int value)
    {
        foreach (IntField f in IntFields)
        {
            if (f.Name != field) continue;
            int v = Mathf.Clamp(value, f.Min, f.Max);
            if (Settings.Get(field).AsInt32() == v) return false;
            Settings.Set(field, v);
            Dirty = true;
            return true;
        }
        throw new ArgumentException("unknown int field " + field, nameof(field));
    }

    public bool SetBool(string field, bool value)
    {
        if (Array.IndexOf(BoolFields, field) < 0) throw new ArgumentException("unknown bool field " + field, nameof(field));
        if (Settings.Get(field).AsBool() == value) return false;
        Settings.Set(field, value);
        Dirty = true;
        return true;
    }

    public bool SetEventEnabled(bool value) => SetBool(nameof(GameplayEclipseEventSettings.EventEnabled), value);
    public bool SetSuppressGridPulseDuringIgnition(bool value) => SetBool(nameof(GameplayEclipseEventSettings.SuppressGridPulseDuringIgnition), value);
    public bool SetIgnitionSpeedMode(int value) => SetInt(nameof(GameplayEclipseEventSettings.IgnitionSpeedMode), value);
    public bool SetRampMode(int value) => SetInt(nameof(GameplayEclipseEventSettings.RampMode), value);
    public bool SetRefuelClock(int value) => SetInt(nameof(GameplayEclipseEventSettings.RefuelClock), value);
    public bool SetParallaxSafetySeconds(float value) => SetFloat(nameof(GameplayEclipseEventSettings.ParallaxSafetySeconds), value);
    public bool SetPreRollSeconds(float value) => SetFloat(nameof(GameplayEclipseEventSettings.PreRollSeconds), value);
    public bool SetDarknessBeatSeconds(float value) => SetFloat(nameof(GameplayEclipseEventSettings.DarknessBeatSeconds), value);
    public bool SetKindleLevel(float value) => SetFloat(nameof(GameplayEclipseEventSettings.KindleLevel), value);
    public bool SetCollapseLeadSeconds(float value) => SetFloat(nameof(GameplayEclipseEventSettings.CollapseLeadSeconds), value);

    /// <summary>Maps the saved choices onto the pure timeline settings (sanitised again by the timeline).</summary>
    public static EclipseEventSettings ToCore(GameplayEclipseEventSettings s) => new()
    {
        EventEnabled = s.EventEnabled,
        IgnitionSpeedMode = (EclipseIgnitionSpeedMode)Mathf.Clamp(s.IgnitionSpeedMode, 0, 1),
        RampMode = (EclipseRampMode)Mathf.Clamp(s.RampMode, 0, 1),
        SuppressGridPulseDuringIgnition = s.SuppressGridPulseDuringIgnition,
        RefuelClock = (EclipseRefuelClock)Mathf.Clamp(s.RefuelClock, 0, 1),
        ParallaxSafetySeconds = s.ParallaxSafetySeconds,
        PreRollSeconds = s.PreRollSeconds,
        DarknessBeatSeconds = s.DarknessBeatSeconds,
        MainClimbSeconds = s.MainClimbSeconds,
        MainClimbExponent = s.MainClimbExponent,
        KindleSeconds = s.KindleSeconds,
        KindleLevel = s.KindleLevel,
        IgnitionDelaySeconds = s.IgnitionDelaySeconds,
        IgnitionSeconds = s.IgnitionSeconds,
        RegularClimbSeconds = s.RegularClimbSeconds,
        RampGamma = s.RampGamma,
        RampStepSmoothSeconds = s.RampStepSmoothSeconds,
        IgnitionPlateauLevel = s.IgnitionPlateauLevel,
        MeetingFlareSeconds = s.MeetingFlareSeconds,
        CrescendoSeconds = s.CrescendoSeconds,
        HandoffFadeSeconds = s.HandoffFadeSeconds,
        FlashClimbSeconds = s.FlashClimbSeconds,
        FlashHoldSeconds = s.FlashHoldSeconds,
        FlashFadeSeconds = s.FlashFadeSeconds,
        HeartbeatKick = s.HeartbeatKick,
        HeartbeatTauSeconds = s.HeartbeatTauSeconds,
        RelaunchKick = s.RelaunchKick,
        BreathAmplitude = s.BreathAmplitude,
        BreathPeriodSeconds = s.BreathPeriodSeconds,
        CollapseLeadSeconds = s.CollapseLeadSeconds,
        InstabilitySeconds = s.InstabilitySeconds,
        FlickerMax = s.FlickerMax,
        FlickerHzStart = s.FlickerHzStart,
        FlickerHzEnd = s.FlickerHzEnd,
        OverloadSeconds = s.OverloadSeconds,
        OverloadGain = s.OverloadGain,
        ImplosionSeconds = s.ImplosionSeconds,
        BeamRetractSeconds = s.BeamRetractSeconds,
        ShockwaveSeconds = s.ShockwaveSeconds,
        ShockwaveArrivalFraction = s.ShockwaveArrivalFraction,
        ShockFlashLevel = s.ShockFlashLevel,
        ShockFlashSeconds = s.ShockFlashSeconds,
        ReturnTailSeconds = s.ReturnTailSeconds,
        RealSunEnergy = s.RealSunEnergy,
        HubDirectFraction = s.HubDirectFraction,
        HubDayAtFull = s.HubDayAtFull,
        HubLightRed = s.HubLightRed,
        HubLightGreen = s.HubLightGreen,
        HubLightBlue = s.HubLightBlue,
        HubSkyTintStrength = s.HubSkyTintStrength,
        HubZenithHaloIntensity = s.HubZenithHaloIntensity,
        Darkness = new EclipseDarknessSettings
        {
            Enabled = s.EclipseDarknessEnabled,
            AmbientScale = s.EclipseAmbientScale,
            AtmosphereNightScale = s.EclipseAtmosphereNightScale,
            SkyNightScale = s.EclipseSkyNightScale,
            CloudAmbientScale = s.EclipseCloudAmbientScale,
            StarVisibility = s.EclipseStarVisibility
        }
    };

    /// <summary>Copies the saved choices onto the director.</summary>
    public void Apply(EclipseEventDirector? director)
    {
        if (director == null || !GodotObject.IsInstanceValid(director)) return;
        director.Configure(Settings);
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
            if (ResourceSaver.Save(Settings, temporary) != Error.Ok) throw new IOException("Could not save eclipse event settings");
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
