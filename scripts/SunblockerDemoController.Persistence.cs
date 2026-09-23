using System;
using System.IO;
using Godot;

namespace RingworldTechDemo;

public partial class SunblockerDemoController
{
    private const string DefaultsPath = "user://sunblocker_demo_defaults.tres";
    private SunblockerDemoDefaults _defaults = new();
    private bool _dirty;
    private double _saveDelay;
    private void MarkDirty()
    {
        if (_capture || _smoke) return;
        _dirty = true;
        _saveDelay = 0.6;
    }

    private void LoadDefaults()
    {
        if (!ResourceLoader.Exists(DefaultsPath)) return;
        try
        {
            SunblockerDemoDefaults? loaded = ResourceLoader.Load<SunblockerDemoDefaults>(DefaultsPath, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { GD.PushWarning("Sunblocker demo settings could not be read; using defaults."); return; }
            _defaults = loaded;
            _sunElevation = ClampFinite(loaded.Elevation, 38, 8, 80);
            _sunAzimuth = ClampFinite(loaded.Azimuth, 35, 0, 359.999f);
            _atlasSize = NormalizeAtlas(loaded.Atlas);
            _treeCount = Mathf.Clamp(loaded.Trees, 4, 64);
            _distanceLod = ClampFinite(loaded.Distance, 150, 100, 200);
            _distanceHandoff = loaded.Handoff;
            _showProxies = loaded.Debug;
            _singleMethod = loaded.Single;
            _viewMethod = Mathf.Clamp(loaded.Method, 0, 2);
            TreeVariantIndex = Mathf.Clamp(loaded.TreeVariantIndex, 0, global::RingworldTechDemo.Vegetation.ImportedTreeCatalog.VariantCount - 1);
        }
        catch (Exception error)
        {
            GD.PushWarning($"Sunblocker demo settings were ignored: {error.Message}");
            _defaults = new SunblockerDemoDefaults();
        }
    }

    private bool SaveDefaults()
    {
        if (!_dirty || _capture || _smoke) return true;
        try
        {
            ApplyDefaults(_defaults);
            SaveResourceToPath(_defaults, DefaultsPath);
            _dirty = false;
            return true;
        }
        catch (Exception error)
        {
            _saveDelay = 5.0;
            GD.PushWarning($"Sunblocker demo settings were not saved: {error.Message}");
            return false;
        }
    }

    private void ApplyDefaults(SunblockerDemoDefaults target)
    {
        target.Elevation = _sunElevation; target.Azimuth = _sunAzimuth; target.Atlas = _atlasSize;
        target.Trees = _treeCount; target.Distance = _distanceLod; target.Handoff = _distanceHandoff;
        target.Debug = _showProxies; target.Single = _singleMethod; target.Method = _viewMethod; target.TreeVariantIndex = TreeVariantIndex;
    }

    private bool SaveSmokeRoundtrip()
    {
        const string directory = "res://artifacts/sunblocker-demo-review";
        string path = $"{directory}/smoke_defaults_{Guid.NewGuid():N}.tres";
        SunblockerDemoDefaults? test = null;
        SunblockerDemoDefaults? loaded = null;
        try
        {
            DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(directory));
            test = new SunblockerDemoDefaults { Elevation = 63, Azimuth = 217, Atlas = 4096, Trees = 37, Distance = 185, Handoff = true, Debug = true, Single = true, Method = 2, TreeVariantIndex = 19 };
            SaveResourceToPath(test, path);
            loaded = ResourceLoader.Load<SunblockerDemoDefaults>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
            return loaded != null && loaded.Elevation == 63 && loaded.Azimuth == 217 && loaded.Atlas == 4096 && loaded.Trees == 37
                && loaded.Distance == 185 && loaded.Handoff && loaded.Debug && loaded.Single && loaded.Method == 2 && loaded.TreeVariantIndex == 19;
        }
        catch (Exception error) { GD.PushWarning($"Sunblocker smoke settings roundtrip failed: {error.Message}"); return false; }
        finally
        {
            if (File.Exists(ProjectSettings.GlobalizePath(path))) File.Delete(ProjectSettings.GlobalizePath(path));
            loaded?.Dispose();
            test?.Dispose();
        }
    }

    private static float ClampFinite(float value, float fallback, float minimum, float maximum) => float.IsFinite(value) ? Mathf.Clamp(value, minimum, maximum) : fallback;
    private static int NormalizeAtlas(int value) => value <= 1024 ? 1024 : value <= 2048 ? 2048 : 4096;

    private static void SaveResourceToPath(SunblockerDemoDefaults settings, string path)
    {
        string temporaryPath = path[..^5] + ".tmp.tres";
        string temporaryFile = ProjectSettings.GlobalizePath(temporaryPath);
        string targetFile = ProjectSettings.GlobalizePath(path);
        try
        {
            string? parent = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            if (ResourceSaver.Save(settings, temporaryPath) != Error.Ok) throw new IOException("Could not write temporary settings.");
            File.Move(temporaryFile, targetFile, true);
        }
        finally
        {
            if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
        }
    }
}
