using System;
using System.IO;
using Godot;
using RingworldTechDemo.ContainmentField;

namespace RingworldTechDemo.UI;

/// <summary>Saved containment field choices shared with the grave-key drawer (RingLiveTuningDrawer.ContainmentField.cs).</summary>
public sealed class GameplayContainmentFieldDefaults
{
    public const string UserPath = "user://gameplay_containment_field_defaults.tres";

    private readonly record struct FloatField(string Name, float Default, float Min, float Max);

    // Slider ranges in RingLiveTuningDrawer.ContainmentField.cs must match this table.
    private static readonly FloatField[] FloatFields =
    {
        new(nameof(GameplayContainmentFieldSettings.ShimmerStrength), 1f, 0f, 2f),
        new(nameof(GameplayContainmentFieldSettings.ShimmerRangeKm), 3f, .5f, 10f),
        new(nameof(GameplayContainmentFieldSettings.SheenStrength), 1f, 0f, 2f),
        new(nameof(GameplayContainmentFieldSettings.LatticeStrength), 1f, 0f, 2f),
        new(nameof(GameplayContainmentFieldSettings.RippleStrength), 1f, 0f, 2f),
        new(nameof(GameplayContainmentFieldSettings.FieldHumVolumeDb), 0f, -30f, 10f)
    };

    private readonly string _path;
    public GameplayContainmentFieldSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";

    public GameplayContainmentFieldDefaults(string? pathOverride = null) => _path = pathOverride ?? UserPath;

    /// <summary>True once the player has saved a choice; otherwise the field keeps its own defaults.</summary>
    public bool HasSavedFile => ResourceLoader.Exists(_path);

    public static (float Min, float Max) RangeOf(string field)
    {
        foreach (FloatField f in FloatFields) if (f.Name == field) return (f.Min, f.Max);
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
            using var loaded = ResourceLoader.Load<GameplayContainmentFieldSettings>(_path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid containment field settings"; return; }
            using var clean = Sanitize(loaded);
            foreach (FloatField f in FloatFields) Settings.Set(f.Name, clean.Get(f.Name));
        }
        catch (Exception error) { LastError = error.Message; }
    }

    /// <summary>Clamps every field; non-finite values fall back to the defaults.</summary>
    public static GameplayContainmentFieldSettings Sanitize(GameplayContainmentFieldSettings input)
    {
        var result = new GameplayContainmentFieldSettings();
        foreach (FloatField f in FloatFields) result.Set(f.Name, Finite((float)input.Get(f.Name).AsDouble(), f.Default, f.Min, f.Max));
        return result;
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

    public bool SetShimmerStrength(float value) => SetFloat(nameof(GameplayContainmentFieldSettings.ShimmerStrength), value);
    public bool SetShimmerRangeKm(float value) => SetFloat(nameof(GameplayContainmentFieldSettings.ShimmerRangeKm), value);
    public bool SetSheenStrength(float value) => SetFloat(nameof(GameplayContainmentFieldSettings.SheenStrength), value);
    public bool SetLatticeStrength(float value) => SetFloat(nameof(GameplayContainmentFieldSettings.LatticeStrength), value);
    public bool SetRippleStrength(float value) => SetFloat(nameof(GameplayContainmentFieldSettings.RippleStrength), value);
    public bool SetFieldHumVolumeDb(float value) => SetFloat(nameof(GameplayContainmentFieldSettings.FieldHumVolumeDb), value);

    /// <summary>Copies the saved choices onto the containment field node; other nodes are ignored.</summary>
    public void Apply(Node? node)
    {
        if (node is not RingContainmentField field || !GodotObject.IsInstanceValid(field)) return;
        field.ShimmerStrength = Settings.ShimmerStrength;
        field.ShimmerRangeKm = Settings.ShimmerRangeKm;
        field.SheenStrength = Settings.SheenStrength;
        field.LatticeStrength = Settings.LatticeStrength;
        field.RippleStrength = Settings.RippleStrength;
        RingContainmentFieldAudio.HumVolumeDb = Settings.FieldHumVolumeDb;
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
            if (ResourceSaver.Save(Settings, temporary) != Error.Ok) throw new IOException("Could not save containment field settings");
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
