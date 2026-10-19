using System;
using System.IO;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.UI;

public sealed class AtmosphereVisualDefaults
{
    public const string UserPath = "user://atmosphere_visual_defaults.tres";
    public AtmosphereVisualSettings Settings { get; private set; } = new();
    public string Path => _path ?? UserPath;
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";
    private readonly string? _path;
    public AtmosphereVisualDefaults(string? path = null) => _path = path;
    public void Initialize() { Load(); Sanitize(); Apply(); }
    public void Apply()
    {
        RingAtmosphereModel.HazeModel = (RingAtmosphereModel.HazeModelKind)Mathf.Clamp(Settings.HazeModel, 0, 1);
        RingAtmosphereModel.ApplyArtDirection(
            (RingAtmosphereModel.ArtDirectionPreset)Mathf.Clamp(Settings.Preset, 0, 1),
            Settings.VisibilityFullKm, Settings.Density, Settings.AltitudeFalloff,
            Settings.Aerosol, Settings.ForwardScattering, Settings.SunWarmth);
    }
    public void Set(string key, float value)
    {
        switch (key)
        {
            case "Visibility": Settings.VisibilityFullKm = value; break;
            case "Density": Settings.Density = value; break;
            case "Altitude": Settings.AltitudeFalloff = value; break;
            case "Aerosol": Settings.Aerosol = value; break;
            case "Forward": Settings.ForwardScattering = value; break;
            case "Warmth": Settings.SunWarmth = value; break;
            default: return;
        }
        Sanitize(); Dirty = true; Apply();
    }
    public void SetHazeModel(int model)
    {
        Settings.HazeModel = Mathf.Clamp(model, 0, 1);
        Sanitize(); Dirty = true; Apply();
    }
    public void SetPreset(int preset)
    {
        Settings.Preset = Mathf.Clamp(preset, 0, 1);
        if (Settings.Preset == 0) { Settings.VisibilityFullKm=120; Settings.Density=1; Settings.AltitudeFalloff=.5f; Settings.Aerosol=.35f; Settings.ForwardScattering=.22f; Settings.SunWarmth=.25f; }
        else { Settings.VisibilityFullKm=60; Settings.Density=1.5f; Settings.AltitudeFalloff=.5f; Settings.Aerosol=.65f; Settings.ForwardScattering=.55f; Settings.SunWarmth=.6f; }
        Sanitize(); Dirty = true; Apply();
    }
    public void Reset() { Settings = new(); Sanitize(); Dirty = true; Apply(); }
    public bool Save()
    {
        if (!Dirty) return true;
        string temporary = "";
        try { Sanitize(); string path = _path ?? UserPath; string full = ProjectSettings.GlobalizePath(path); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!); temporary = path + ".tmp.tres"; if (ResourceSaver.Save(Settings, temporary) != Error.Ok) throw new IOException("Could not save atmosphere settings"); File.Move(ProjectSettings.GlobalizePath(temporary), full, true); Dirty=false; LastError=""; return true; }
        catch(Exception error) { LastError=error.Message; return false; }
        finally { if (temporary.Length > 0) try { File.Delete(ProjectSettings.GlobalizePath(temporary)); } catch {} }
    }
    private void Load()
    {
        try { string path=_path ?? UserPath; if (ResourceLoader.Exists(path) && ResourceLoader.Load<AtmosphereVisualSettings>(path, cacheMode: ResourceLoader.CacheMode.Ignore) is { } loaded) Settings=loaded; }
        catch(Exception error) { LastError=error.Message; }
    }
    private void Sanitize()
    {
        Settings.Preset = Mathf.Clamp(Settings.Preset, 0, 1);
        Settings.HazeModel = Mathf.Clamp(Settings.HazeModel, 0, 1);
        Settings.VisibilityFullKm = Clean(Settings.VisibilityFullKm, 120, 10, 400);
        Settings.Density = Clean(Settings.Density, 1, 0, 2);
        Settings.AltitudeFalloff = Clean(Settings.AltitudeFalloff, .5f, 0, 1);
        Settings.Aerosol = Clean(Settings.Aerosol, .35f, 0, 1);
        Settings.ForwardScattering = Clean(Settings.ForwardScattering, .22f, 0, .85f);
        Settings.SunWarmth = Clean(Settings.SunWarmth, .25f, 0, 1);
    }
    private static float Clean(float value, float fallback, float minimum, float maximum) => float.IsFinite(value) ? Mathf.Clamp(value, minimum, maximum) : fallback;
}
