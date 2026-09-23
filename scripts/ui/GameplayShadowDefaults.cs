using System;
using System.IO;
using Godot;
using RingworldTechDemo.Voxel.LOD;
namespace RingworldTechDemo.UI;
public sealed class GameplayShadowDefaults
{
    public const string UserPath = "user://gameplay_shadow_defaults.tres";
    private readonly string? _pathOverride;
    public GameplayShadowSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";
    public GameplayShadowDefaults(string? pathOverride = null) => _pathOverride = pathOverride;
    public void Initialize(RingGraphicsQualityController? controller) { Load(); Apply(controller); }
    public bool SetOverride(RingGraphicsQualityController? controller, string key, Variant value)
    { if (controller == null || !controller.TryValidateSupportedShadowOverride(key, value, out Variant clean)) return false; if (key == "NearCascadeMeters") Settings.Overrides.Remove("Split1"); else if (key == "Split1") Settings.Overrides.Remove("NearCascadeMeters"); Settings.Overrides[key] = clean; controller.SetShadowTuningOverrides(Settings.Overrides); Dirty = true; return true; }
    public void Reset(RingGraphicsQualityController? controller) { Settings = new(); controller?.SetShadowTuningOverrides(Settings.Overrides); Dirty = true; }
    public bool Save()
    { if (!Dirty) return true; string temp = ""; try { string path = _pathOverride ?? UserPath; string full = ProjectSettings.GlobalizePath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp.tres"; if (ResourceSaver.Save(Settings, temp) != Error.Ok) throw new IOException("Could not save shadow settings"); File.Move(ProjectSettings.GlobalizePath(temp), full, true); Dirty = false; LastError = ""; return true; } catch(Exception error) { LastError = error.Message; return false; } finally { if (temp.Length > 0) try { File.Delete(ProjectSettings.GlobalizePath(temp)); } catch {} } }
    private void Load() { Settings = new(); LastError = ""; try { string path = _pathOverride ?? UserPath; if (!ResourceLoader.Exists(path)) return; var loaded = ResourceLoader.Load<GameplayShadowSettings>(path, cacheMode: ResourceLoader.CacheMode.Ignore); if (loaded == null) { LastError = "Invalid shadow settings"; return; } foreach (Variant key in loaded.Overrides.Keys) if (key.VariantType == Variant.Type.String && RingGraphicsQualityController.TryValidateShadowOverride(key.AsString(), loaded.Overrides[key], out Variant value)) Settings.Overrides[key.AsString()] = value; } catch (Exception error) { LastError = error.Message; } }
    private void Apply(RingGraphicsQualityController? controller) => controller?.SetShadowTuningOverrides(Settings.Overrides);
}
