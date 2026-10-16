using System;
using System.IO;
using Godot;
using RingworldTechDemo.Poi;

namespace RingworldTechDemo.UI;

/// <summary>Saved emitter relay grid choices shared with the grave-key drawer (RingLiveTuningDrawer.EmitterGrid.cs).</summary>
public sealed class GameplayEmitterGridDefaults
{
    public const string UserPath = "user://gameplay_emitter_grid_defaults.tres";

    private readonly record struct FloatField(string Name, float Default, float Min, float Max);
    private readonly record struct IntField(string Name, int Default, int Min, int Max);

    private static readonly FloatField[] FloatFields =
    {
        new(nameof(GameplayEmitterGridSettings.PulsePeriodSeconds), 300f, 30f, 1800f),
        new(nameof(GameplayEmitterGridSettings.PulseSpeedKmPerSecond), 104.7f, 5f, 500f),
        new(nameof(GameplayEmitterGridSettings.PulseLeadingEdgeKm), 3f, .5f, 20f),
        new(nameof(GameplayEmitterGridSettings.PulseTrailSeconds), 2.5f, .5f, 20f),
        new(nameof(GameplayEmitterGridSettings.PulseWakeSeconds), 10f, 0f, 60f),
        new(nameof(GameplayEmitterGridSettings.PulseWakeLevel), .05f, 0f, .3f),
        new(nameof(GameplayEmitterGridSettings.RelaunchFlareStrength), 3f, 0f, 6f),
        new(nameof(GameplayEmitterGridSettings.ColumnStaggerSeconds), 0f, 0f, 5f),
        new(nameof(GameplayEmitterGridSettings.NightPeakIntensity), 1f, 0f, 4f),
        new(nameof(GameplayEmitterGridSettings.DayPeakIntensity), .2f, 0f, 1f),
        new(nameof(GameplayEmitterGridSettings.NightResidual), .03f, 0f, .2f),
        new(nameof(GameplayEmitterGridSettings.DayResidual), 0f, 0f, .1f),
        new(nameof(GameplayEmitterGridSettings.BaseEmission), 6f, .5f, 20f),
        new(nameof(GameplayEmitterGridSettings.CoreWidthMeters), 40f, 5f, 200f),
        new(nameof(GameplayEmitterGridSettings.HaloWidthMeters), 600f, 0f, 3000f),
        new(nameof(GameplayEmitterGridSettings.HaloAmplitude), .12f, 0f, 1f),
        new(nameof(GameplayEmitterGridSettings.MinPixelWidth), 1.5f, .5f, 4f),
        new(nameof(GameplayEmitterGridSettings.FarCompensation), .6f, 0f, 1f),
        new(nameof(GameplayEmitterGridSettings.FrontSpriteMinPixels), 3f, 0f, 8f),
        new(nameof(GameplayEmitterGridSettings.HeadSpriteNearKm), 300f, 50f, 2000f),
        new(nameof(GameplayEmitterGridSettings.HeadSpriteFullKm), 600f, 100f, 3000f),
        new(nameof(GameplayEmitterGridSettings.NearLayerMaxKm), 700f, 100f, 850f),
        new(nameof(GameplayEmitterGridSettings.CloudSampleHz), 10f, 2f, 30f),
        new(nameof(GameplayEmitterGridSettings.CrawlRadiusKm), 8f, 1f, 30f),
        new(nameof(GameplayEmitterGridSettings.CrawlLengthKm), 40f, 5f, 150f),
        new(nameof(GameplayEmitterGridSettings.CoreDepthThreshold), .35f, .05f, 2f),
        new(nameof(GameplayEmitterGridSettings.CoreDepthMinPixels), 4f, 1f, 8f),
        new(nameof(GameplayEmitterGridSettings.FarHandoffStartKm), 120f, 10f, 650f),
        new(nameof(GameplayEmitterGridSettings.FarHandoffEndKm), 172f, 20f, 700f)
    };

    private static readonly IntField[] IntFields =
    {
        new(nameof(GameplayEmitterGridSettings.PulseSpeedMode), 0, 0, 1),
        new(nameof(GameplayEmitterGridSettings.PulseDirection), 0, 0, 1),
        new(nameof(GameplayEmitterGridSettings.CloudInteraction), 2, 0, 3)
    };

    private static readonly string[] BoolFields =
    {
        nameof(GameplayEmitterGridSettings.GridEnabled), nameof(GameplayEmitterGridSettings.FarLayerEnabled),
        nameof(GameplayEmitterGridSettings.DepthWriteCore), nameof(GameplayEmitterGridSettings.LightningCrawl),
        nameof(GameplayEmitterGridSettings.PulseTriggeredStrikes), nameof(GameplayEmitterGridSettings.FarHandoffFollowsClouds)
    };

    private readonly string _path;
    public GameplayEmitterGridSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";

    public GameplayEmitterGridDefaults(string? pathOverride = null) => _path = pathOverride ?? UserPath;

    /// <summary>True once the player has saved a choice; otherwise the layer keeps its own defaults.</summary>
    public bool HasSavedFile => ResourceLoader.Exists(_path);

    public static (float Min, float Max) RangeOf(string field)
    {
        foreach (FloatField f in FloatFields) if (f.Name == field) return (f.Min, f.Max);
        foreach (IntField f in IntFields) if (f.Name == field) return (f.Min, f.Max);
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
            using var loaded = ResourceLoader.Load<GameplayEmitterGridSettings>(_path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid emitter grid settings"; return; }
            using var clean = Sanitize(loaded);
            Copy(clean, Settings);
        }
        catch (Exception error) { LastError = error.Message; }
    }

    /// <summary>Clamps every field; non-finite values fall back to the defaults.</summary>
    public static GameplayEmitterGridSettings Sanitize(GameplayEmitterGridSettings input)
    {
        var result = new GameplayEmitterGridSettings();
        foreach (FloatField f in FloatFields) result.Set(f.Name, Finite((float)input.Get(f.Name).AsDouble(), f.Default, f.Min, f.Max));
        foreach (IntField f in IntFields) result.Set(f.Name, Mathf.Clamp(input.Get(f.Name).AsInt32(), f.Min, f.Max));
        foreach (string name in BoolFields) result.Set(name, input.Get(name).AsBool());
        return result;
    }

    private static void Copy(GameplayEmitterGridSettings from, GameplayEmitterGridSettings to)
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

    public bool SetGridEnabled(bool value) => SetBool(nameof(GameplayEmitterGridSettings.GridEnabled), value);
    public bool SetPulsePeriodSeconds(float value) => SetFloat(nameof(GameplayEmitterGridSettings.PulsePeriodSeconds), value);
    public bool SetPulseSpeedMode(int value) => SetInt(nameof(GameplayEmitterGridSettings.PulseSpeedMode), value);
    public bool SetPulseSpeedKmPerSecond(float value) => SetFloat(nameof(GameplayEmitterGridSettings.PulseSpeedKmPerSecond), value);
    public bool SetPulseDirection(int value) => SetInt(nameof(GameplayEmitterGridSettings.PulseDirection), value);
    public bool SetPulseLeadingEdgeKm(float value) => SetFloat(nameof(GameplayEmitterGridSettings.PulseLeadingEdgeKm), value);
    public bool SetPulseTrailSeconds(float value) => SetFloat(nameof(GameplayEmitterGridSettings.PulseTrailSeconds), value);
    public bool SetPulseWakeSeconds(float value) => SetFloat(nameof(GameplayEmitterGridSettings.PulseWakeSeconds), value);
    public bool SetPulseWakeLevel(float value) => SetFloat(nameof(GameplayEmitterGridSettings.PulseWakeLevel), value);
    public bool SetRelaunchFlareStrength(float value) => SetFloat(nameof(GameplayEmitterGridSettings.RelaunchFlareStrength), value);
    public bool SetColumnStaggerSeconds(float value) => SetFloat(nameof(GameplayEmitterGridSettings.ColumnStaggerSeconds), value);
    public bool SetNightPeakIntensity(float value) => SetFloat(nameof(GameplayEmitterGridSettings.NightPeakIntensity), value);
    public bool SetDayPeakIntensity(float value) => SetFloat(nameof(GameplayEmitterGridSettings.DayPeakIntensity), value);
    public bool SetNightResidual(float value) => SetFloat(nameof(GameplayEmitterGridSettings.NightResidual), value);
    public bool SetDayResidual(float value) => SetFloat(nameof(GameplayEmitterGridSettings.DayResidual), value);
    public bool SetBaseEmission(float value) => SetFloat(nameof(GameplayEmitterGridSettings.BaseEmission), value);
    public bool SetCoreWidthMeters(float value) => SetFloat(nameof(GameplayEmitterGridSettings.CoreWidthMeters), value);
    public bool SetHaloWidthMeters(float value) => SetFloat(nameof(GameplayEmitterGridSettings.HaloWidthMeters), value);
    public bool SetHaloAmplitude(float value) => SetFloat(nameof(GameplayEmitterGridSettings.HaloAmplitude), value);
    public bool SetMinPixelWidth(float value) => SetFloat(nameof(GameplayEmitterGridSettings.MinPixelWidth), value);
    public bool SetFarCompensation(float value) => SetFloat(nameof(GameplayEmitterGridSettings.FarCompensation), value);
    public bool SetFrontSpriteMinPixels(float value) => SetFloat(nameof(GameplayEmitterGridSettings.FrontSpriteMinPixels), value);
    public bool SetHeadSpriteNearKm(float value) => SetFloat(nameof(GameplayEmitterGridSettings.HeadSpriteNearKm), value);
    public bool SetHeadSpriteFullKm(float value) => SetFloat(nameof(GameplayEmitterGridSettings.HeadSpriteFullKm), value);
    public bool SetNearLayerMaxKm(float value) => SetFloat(nameof(GameplayEmitterGridSettings.NearLayerMaxKm), value);
    public bool SetFarLayerEnabled(bool value) => SetBool(nameof(GameplayEmitterGridSettings.FarLayerEnabled), value);
    public bool SetCloudInteraction(int value) => SetInt(nameof(GameplayEmitterGridSettings.CloudInteraction), value);
    public bool SetCloudSampleHz(float value) => SetFloat(nameof(GameplayEmitterGridSettings.CloudSampleHz), value);
    public bool SetDepthWriteCore(bool value) => SetBool(nameof(GameplayEmitterGridSettings.DepthWriteCore), value);
    public bool SetLightningCrawl(bool value) => SetBool(nameof(GameplayEmitterGridSettings.LightningCrawl), value);
    public bool SetCrawlRadiusKm(float value) => SetFloat(nameof(GameplayEmitterGridSettings.CrawlRadiusKm), value);
    public bool SetCrawlLengthKm(float value) => SetFloat(nameof(GameplayEmitterGridSettings.CrawlLengthKm), value);
    public bool SetPulseTriggeredStrikes(bool value) => SetBool(nameof(GameplayEmitterGridSettings.PulseTriggeredStrikes), value);
    public bool SetCoreDepthThreshold(float value) => SetFloat(nameof(GameplayEmitterGridSettings.CoreDepthThreshold), value);
    public bool SetCoreDepthMinPixels(float value) => SetFloat(nameof(GameplayEmitterGridSettings.CoreDepthMinPixels), value);
    public bool SetFarHandoffFollowsClouds(bool value) => SetBool(nameof(GameplayEmitterGridSettings.FarHandoffFollowsClouds), value);
    public bool SetFarHandoffStartKm(float value) => SetFloat(nameof(GameplayEmitterGridSettings.FarHandoffStartKm), value);
    public bool SetFarHandoffEndKm(float value) => SetFloat(nameof(GameplayEmitterGridSettings.FarHandoffEndKm), value);

    /// <summary>Copies the saved choices onto the beam layer.</summary>
    public void Apply(EmitterGridBeamLayer? layer)
    {
        if (layer == null || !GodotObject.IsInstanceValid(layer)) return;
        layer.Configure(Settings);
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
            if (ResourceSaver.Save(Settings, temporary) != Error.Ok) throw new IOException("Could not save emitter grid settings");
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
