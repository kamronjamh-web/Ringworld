using System;
using System.IO;
using Godot;

namespace RingworldTechDemo.UI;

public sealed class GameplayVegetationDefaults
{
    public const string UserPath = "user://gameplay_vegetation_defaults.tres";
    public const int DefaultMaximumGpuTreeRoots = 65_536;
    public const int DefaultMaximumGpuTreeBatches = 1_024;
    public const int DefaultMaximumTreeSunShadowBatches = 128;
    public const float DefaultTreeStreamingBudgetMilliseconds = 2.0f;
    private readonly string? _path;
    public GameplayVegetationSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";
    public GameplayVegetationDefaults(string? path = null) => _path = path;
    public string Path => _path ?? UserPath;
    public bool UseGpuCulling => Get("UseGpuCulling", true).AsBool();
    public bool SkipUnchangedGpuCulling => Get("SkipUnchangedGpuCulling", true).AsBool();
    public bool OptimizeGpuTreeDispatches => Get("OptimizeGpuTreeDispatches", true).AsBool();
    public bool MergeFarGpuTreeBatches => Get("MergeFarGpuTreeBatches", true).AsBool();
    public bool UseSharedFarTreeImpostors => Get("UseSharedFarTreeImpostors", true).AsBool();
    public bool UseCompactTreeLodProfile => Get("UseCompactTreeLodProfile", true).AsBool();
    public float TreeNearDetailFadeStartMeters => (float)Get("TreeNearDetailFadeStartMeters", 25.0f).AsDouble();
    public float TreeNearDetailFadeEndMeters => (float)Get("TreeNearDetailFadeEndMeters", 40.0f).AsDouble();
    public float TreeBillboardFadeStartMeters => (float)Get("TreeBillboardFadeStartMeters", 300.0f).AsDouble();
    public float TreeBillboardFadeEndMeters => (float)Get("TreeBillboardFadeEndMeters", 350.0f).AsDouble();
    public bool MergeMidGpuTreeBatches => Get("MergeMidGpuTreeBatches", true).AsBool();
    public int MidTreeBatchTileSpan => (int)Get("MidTreeBatchTileSpan", 2).AsInt64();
    public bool PruneEmptyGpuTreeBatches => Get("PruneEmptyGpuTreeBatches", true).AsBool();
    public bool SimplifyDistantTreeMaterials => Get("SimplifyDistantTreeMaterials", true).AsBool();
    public bool UseLowDetailTreeShadows => Get("UseLowDetailTreeShadows", true).AsBool();
    public bool KeepTreeLodFallbacksUntilReady => Get("KeepTreeLodFallbacksUntilReady", true).AsBool();
    public int MaximumGpuTreeRoots => (int)Get("MaximumGpuTreeRoots", DefaultMaximumGpuTreeRoots).AsInt64();
    public int MaximumGpuTreeBatches => (int)Get("MaximumGpuTreeBatches", DefaultMaximumGpuTreeBatches).AsInt64();
	public bool StabilizeTreeSunShadowsDuringFastTravel => Get("StabilizeTreeSunShadowsDuringFastTravel", true).AsBool();
	public float TreeSunShadowSuspendSpeedMetersPerSecond => (float)Get("TreeSunShadowSuspendSpeedMetersPerSecond", 250.0f).AsDouble();
	public float TreeSunShadowResumeSpeedMetersPerSecond => (float)Get("TreeSunShadowResumeSpeedMetersPerSecond", 25.0f).AsDouble();
	public float TreeSunShadowSettleHoldSeconds => (float)Get("TreeSunShadowSettleHoldSeconds", 0.5f).AsDouble();
	public float TreeSunShadowDistanceMeters => (float)Get("TreeSunShadowDistanceMeters", 480.0f).AsDouble();
	public int MaximumTreeSunShadowBatches => (int)Get("MaximumTreeSunShadowBatches", DefaultMaximumTreeSunShadowBatches).AsInt64();
    public float TreeStreamingBudgetMilliseconds => (float)Get("TreeStreamingBudgetMilliseconds", DefaultTreeStreamingBudgetMilliseconds).AsDouble();
    public float TreeCullingMovementThresholdMeters => (float)Get("TreeCullingMovementThresholdMeters", .5f).AsDouble();
    public float TreeCullingMaximumIntervalSeconds => (float)Get("TreeCullingMaximumIntervalSeconds", .1f).AsDouble();
    public void Load()
    {
        Settings = new();
        LastError = "";
        try
        {
            if (!ResourceLoader.Exists(Path)) return;
            var loaded = ResourceLoader.Load<GameplayVegetationSettings>(Path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid vegetation settings"; return; }
            foreach (Variant key in loaded.Overrides.Keys)
                if (key.VariantType == Variant.Type.String && TryValidate(key.AsString(), loaded.Overrides[key], out Variant clean))
                    Settings.Overrides[key.AsString()] = clean;
        }
        catch (Exception error) { LastError = error.Message; }
        finally { Dirty = false; }
    }
    public bool Set(string key, Variant value)
    {
        if (!TryValidate(key, value, out Variant clean)) return false;
        if (Settings.Overrides.TryGetValue(key, out Variant old) && old.Equals(clean)) return false;
        Settings.Overrides[key] = clean;
        Dirty = true;
        return true;
    }
    public void Reset() { Settings = new(); Dirty = true; LastError = ""; }
    public bool Save()
    {
        if (!Dirty) return true;
        string temporary = "";
        try
        {
            string full = ProjectSettings.GlobalizePath(Path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp.tres";
            if (ResourceSaver.Save(Settings, temporary) != Error.Ok) throw new IOException("Could not save vegetation settings");
            File.Move(ProjectSettings.GlobalizePath(temporary), full, true);
            temporary = "";
            Dirty = false;
            LastError = "";
            return true;
        }
        catch (Exception error) { LastError = error.Message; return false; }
        finally
        {
            if (temporary.Length > 0)
                try { File.Delete(ProjectSettings.GlobalizePath(temporary)); }
                catch { /* Retain the save error if temporary-file cleanup fails. */ }
        }
    }
    public static bool TryValidate(string key, Variant value, out Variant clean)
    {
        clean = default;
		if (key is "UseGpuCulling" or "SkipUnchangedGpuCulling" or "OptimizeGpuTreeDispatches" or "MergeFarGpuTreeBatches" or "UseSharedFarTreeImpostors" or "UseCompactTreeLodProfile" or "MergeMidGpuTreeBatches" or "PruneEmptyGpuTreeBatches" or "SimplifyDistantTreeMaterials" or "UseLowDetailTreeShadows" or "KeepTreeLodFallbacksUntilReady" or "StabilizeTreeSunShadowsDuringFastTravel")
        {
            if (value.VariantType != Variant.Type.Bool) return false;
            clean = value.AsBool();
            return true;
        }
		if (key is "TreeSunShadowSuspendSpeedMetersPerSecond" or "TreeSunShadowResumeSpeedMetersPerSecond" or "TreeSunShadowSettleHoldSeconds" or "TreeSunShadowDistanceMeters" or "TreeStreamingBudgetMilliseconds" or "TreeCullingMovementThresholdMeters" or "TreeCullingMaximumIntervalSeconds" or "TreeNearDetailFadeStartMeters" or "TreeNearDetailFadeEndMeters" or "TreeBillboardFadeStartMeters" or "TreeBillboardFadeEndMeters")
		{
			if (value.VariantType is not (Variant.Type.Int or Variant.Type.Float) || !double.IsFinite(value.AsDouble())) return false;
			double floatMax = key is "TreeNearDetailFadeStartMeters" or "TreeNearDetailFadeEndMeters" ? 200 : key is "TreeBillboardFadeStartMeters" or "TreeBillboardFadeEndMeters" ? 2000 : key == "TreeCullingMovementThresholdMeters" ? 4 : key == "TreeCullingMaximumIntervalSeconds" ? .5 : key == "TreeStreamingBudgetMilliseconds" ? 8 : key == "TreeSunShadowSettleHoldSeconds" ? 5 : key == "TreeSunShadowDistanceMeters" ? 2000 : key == "TreeSunShadowResumeSpeedMetersPerSecond" ? 250 : 2000;
            double floatMin = key == "TreeStreamingBudgetMilliseconds" ? .25 : 0;
			clean = Math.Clamp(value.AsDouble(), floatMin, floatMax); return true;
		}
        if (key == "MidTreeBatchTileSpan")
        {
            if (value.VariantType is not (Variant.Type.Int or Variant.Type.Float) || !double.IsFinite(value.AsDouble())) return false;
            clean = (long)Math.Round(Math.Clamp(value.AsDouble(), 1, 4));
            return true;
        }
        if (key is not ("MaximumGpuTreeRoots" or "MaximumGpuTreeBatches" or "MaximumTreeSunShadowBatches")
            || value.VariantType is not (Variant.Type.Int or Variant.Type.Float)
            || !double.IsFinite(value.AsDouble())) return false;
		int min = key == "MaximumGpuTreeRoots" ? 256 : 0;
        int max = key == "MaximumGpuTreeRoots" ? 1_048_576 : key == "MaximumGpuTreeBatches" ? 2_048 : 1_024;
        clean = (long)Math.Round(Math.Clamp(value.AsDouble(), min, max));
        return true;
    }
    private Variant Get(string key, Variant fallback) => Settings.Overrides.TryGetValue(key, out Variant value) ? value : fallback;
}
