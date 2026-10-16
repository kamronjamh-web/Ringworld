using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>
/// Sparse, cloud-only overrides shared by the bridge and drawer. Profile changes
/// update the reset baseline, without becoming user-authored overrides.
/// </summary>
public sealed class GameplayCloudDefaults
{
    public const string FactoryPath = "res://resources/clouds/gameplay_cloud_defaults.tres";
    public const string UserPath = "user://gameplay_cloud_defaults.tres";
    private static readonly Dictionary<string, (float min, float max, bool integer)> Ranges = new()
    {
        ["CloudCoverageBias"] = (-1, 1, false),
        ["CloudPatchiness"] = (0, 1, false),
        ["CloudPatchScaleMeters"] = (16000, 256000, false),
        ["ClearSkyThreshold"] = (0, .6f, false),
        ["CloudDensityMultiplier"] = (0, 3, false),
        ["CloudWindMultiplier"] = (0, 3, false),
        ["CloudDirectLightMultiplier"] = (0, 2, false),
        ["CloudAmbientMultiplier"] = (0, 2, false),
        ["CloudSharpness"] = (0, 1, false),
        ["CloudResolutionScale"] = (0, 2, true),
        ["RaymarchSteps"] = (64, RingSunshineCloudBridge.MaximumRaymarchSteps, true),
        ["LightingSteps"] = (1, RingSunshineCloudBridge.MaximumLightingSteps, true),
        ["MinStepDistanceMeters"] = (10, RingSunshineCloudBridge.MaximumMinimumStepDistanceMeters, false),
        ["MaxStepDistanceMeters"] = (100, RingSunshineCloudBridge.MaximumStepDistanceMeters, false),
        ["LightingTravelKm"] = (.5f, RingSunshineCloudBridge.MaximumLightingTravelKm, false),
        ["CloudLodBias"] = (.1f, RingSunshineCloudBridge.MaximumCloudLodBias, false),
        ["BlurPower"] = (0, 6, false),
        ["BlurQuality"] = (1, 4, true),
        ["StationaryHistoryWeight"] = (0, .95f, false),
        ["MovingHistoryWeight"] = (0, .95f, false),
        ["DitherSpeed"] = (0, 120, false),
        ["RayStartJitterStrength"] = (0, 1, false),
        ["HandoffStartMeters"] = (5000, RingSunshineCloudBridge.MaximumHandoffStartMeters, false),
        ["HandoffEndMeters"] = (6000, RingSunshineCloudBridge.MaximumHandoffEndMeters, false),
        ["CloudHandoffOverlapMeters"] = (2000, RingSunshineCloudBridge.MaximumCloudHandoffOverlapMeters, false),
        ["NearCloudRangeExtensionMeters"] = (0, RingSunshineCloudBridge.MaximumNearCloudRangeExtensionMeters, false),
        ["NaturalCloudBaseAltitudeMeters"] = (0, 19000, false),
        ["NaturalCloudTopAltitudeMeters"] = (500, 20000, false),
        ["StormFrontCloudBaseAltitudeMeters"] = (0, 5000, false),
        ["FarCloudDetailStrength"] = (0, 1, false),
        ["FarCloudOpacityMultiplier"] = (0, 2, false),
        ["FarCloudRadiusMeters"] = (30000, RingSunshineCloudBridge.MaximumFarCloudRadiusMeters, false),
        ["StormSculptStrength"] = (0, 1, false),
        ["StormPillarRadiusMeters"] = (1000, 8000, false),
        ["AnvilRadiusMultiplier"] = (1.2f, 4, false),
        ["ShadowStrength"] = (0, 1, false),
        ["ShadowExtinctionPerKilometer"] = (.05f, 2, false),
        ["StormAmbientFloor"] = (.02f, .5f, false),
        ["StormAmbientOcclusionStrength"] = (0, 1, false),
        ["ShadowRadiusMeters"] = (8000, 150000, false),
        ["ShadowMapResolution"] = (16, 128, true),
        ["ShadowRefreshSeconds"] = (.1f, 15, false),
        ["GpuNearShadowMapResolution"] = (64, 2048, true),
        ["GpuNearShadowRadiusMeters"] = (8000, 80000, false),
        ["GpuNearShadowUpdatesPerSecond"] = (1, 20, false),
        ["GpuNearShadowSliceCount"] = (0, 16, true),
        ["GpuFarShadowMapResolution"] = (64, 1024, true),
        ["GpuFarShadowRadiusMeters"] = (16000, 200000, false),
        ["GpuFarShadowUpdatesPerSecond"] = (.25f, 5, false),
        ["GpuShadowVerticalSamples"] = (1, 64, true),
        ["TerrainCloudShadowStrength"] = (0, 1, false),
        ["TerrainCloudShadowGlobalAroundResolution"] = (256, 4096, true),
        ["TerrainCloudShadowGlobalAcrossResolution"] = (8, 128, true),
        ["TerrainCloudShadowAltitudeSlices"] = (2, 16, true),
        ["TerrainCloudShadowRegionalResolution"] = (32, 256, true),
        ["TerrainCloudShadowRegionalExtentMeters"] = (8000, 128000, false),
        ["TerrainCloudShadowRefreshSeconds"] = (.1f, 10, false),
        ["TerrainCloudShadowTraceVisitBudget"] = (8, 128, true),
        ["TerrainCloudShadowTexelsPerFrame"] = (4096, 131072, true),
        ["TerrainCloudShadowSunValidityDegrees"] = (.01f, 2, false),
        ["HistoryDepthToleranceMultiplier"] = (.25f, 4, false),
        ["CloudHistoryGateAlphaLow"] = (0, .95f, false),
        ["CloudHistoryGateAlphaHigh"] = (.05f, 1, false),
        ["CloudHistoryContinuityAllowanceScale"] = (.25f, 8, false),
        ["CloudHistoryContinuityHardResetRatio"] = (1.5f, 32, false)
    };
    private static readonly HashSet<string> Bools = new() { "CloudRenderingEnabled", "CastCloudShadows", "PreferGpuCloudShadows", "SculptLocalizedStorms", "FastReconstruction", "FarCloudDetailEnabled", "AutomaticCloudHandoff", "SkipEmptyCloudMaskSamples", "FarCloudPatchCullingEnabled", "TerrainCloudShadowEnabled", "DepthHistoryRejectionEnabled", "CloudHistoryCompatibilityEnabled", "LightningHistoryIsolationEnabled" };
    private static readonly HashSet<string> Colors = new() { "DayCloudAmbientColor", "NightCloudAmbientColor", "NightCloudAmbientTint", "StormCloudAmbientColor" };
    private readonly Dictionary<string, Variant> _baseline = new();
    private readonly string? _pathOverride;

    public GameplayCloudDefaults(string? pathOverride = null) => _pathOverride = pathOverride;
    public GameplayCloudSettings Settings { get; private set; } = new();
    public string LastError { get; private set; } = "";
    public bool Dirty { get; private set; }
    public bool Initialized { get; private set; }

    public static string ResolvePath(string? pathOverride = null) => pathOverride ?? (OS.HasFeature("template") ? UserPath : FactoryPath);
    public string GetPath() => ResolvePath(_pathOverride);
    public void Initialize(global::RingworldTechDemo.RingSunshineCloudBridge bridge)
    {
        if (Initialized) return;
        CaptureBaseline(bridge, null);
        LoadAndApply(bridge);
        Initialized = true;
    }

    public void OnGraphicsPresetApplied(global::RingworldTechDemo.RingSunshineCloudBridge bridge, string[] modifiedKeys)
    {
        if (!Initialized) return;
        CaptureBaseline(bridge, modifiedKeys);
        ApplyOverrides(bridge);
    }

    public void LoadAndApply(global::RingworldTechDemo.RingSunshineCloudBridge? bridge)
    {
        Load();
        ApplyOverrides(bridge);
    }
    public void Load()
    {
        Settings = new();
        LastError = "";
        if (_pathOverride != null)
            LoadInto(_pathOverride);
        else
        {
            LoadInto(FactoryPath);
            if (OS.HasFeature("template")) LoadInto(UserPath);
        }
        Dirty = false;
    }

    public void ApplyOverrides(global::RingworldTechDemo.RingSunshineCloudBridge? bridge)
    {
        if (bridge == null) return;
        foreach (Variant key in Settings.Overrides.Keys)
        {
            if (TryValidate(key.AsString(), Settings.Overrides[key], out Variant clean))
                bridge.Set(key.AsString(), clean);
        }
        Apply(bridge);
    }

    public bool SetOverride(global::RingworldTechDemo.RingSunshineCloudBridge? bridge, string key, Variant value)
    {
        if (!TryValidate(key, value, out Variant clean)) return false;
        bool hasPrevious = Settings.Overrides.TryGetValue(key, out Variant previous);
        bool previousEqualsClean = hasPrevious && SameValue(key, previous, clean);
        bool bridgeEqualsClean = bridge == null || SameValue(key, bridge.Get(key), clean);
        if (previousEqualsClean && bridgeEqualsClean)
            return false;

        Settings.Overrides[key] = clean;
        Dirty = true;
        if (bridge != null)
        {
            Dictionary<string, Variant> before = Companions(key, bridge);
            bridge.Set(key, clean);
            EnforcePairs(bridge);
            foreach (var pair in before)
            {
                if (!SameValue(pair.Key, bridge.Get(pair.Key), pair.Value))
                    Settings.Overrides[pair.Key] = bridge.Get(pair.Key);
            }
            Apply(bridge);
            // Persist the displayed, pair-constrained value, even when the
            // requested value was clamped back to its previous runtime value.
            Settings.Overrides[key] = bridge.Get(key);
        }
        return true;
    }

    public void Reset(global::RingworldTechDemo.RingSunshineCloudBridge? bridge)
    {
        Settings = new();
        LastError = "";
        if (_pathOverride == null && OS.HasFeature("template")) LoadInto(FactoryPath);
        if (bridge != null)
        {
            foreach (var pair in _baseline) bridge.Set(pair.Key, pair.Value);
            ApplyOverrides(bridge);
        }
        Dirty = true;
    }

    public bool Save()
    {
        if (!Dirty) return true;
        string temporaryPath = "";
        try
        {
            string path = GetPath();
            if (!path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Expected a .tres gameplay cloud preset path.");
            if (!Directory.Exists(Path.GetDirectoryName(ProjectSettings.GlobalizePath(path))))
                throw new DirectoryNotFoundException("Gameplay cloud preset folder does not exist.");
            temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp.tres";
            Error error = ResourceSaver.Save(Settings, temporaryPath);
            if (error != Error.Ok) throw new IOException(error.ToString());
            // A same-filesystem replacement leaves the previous preset intact
            // when serialization or replacement fails.
            File.Move(ProjectSettings.GlobalizePath(temporaryPath), ProjectSettings.GlobalizePath(path), true);
            temporaryPath = "";
            Dirty = false;
            LastError = "";
            return true;
        }
        catch (Exception error)
        {
            LastError = error.Message;
            return false;
        }
        finally
        {
            if (temporaryPath.Length > 0)
            {
                try { File.Delete(ProjectSettings.GlobalizePath(temporaryPath)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void CaptureBaseline(global::RingworldTechDemo.RingSunshineCloudBridge bridge, IEnumerable<string>? keys)
    {
        foreach (string key in keys ?? Keys())
        {
            if (IsKey(key)) _baseline[key] = bridge.Get(key);
        }
    }

    private static Dictionary<string, Variant> Companions(string key, global::RingworldTechDemo.RingSunshineCloudBridge bridge)
    {
        string[] keys = key switch
        {
            "MinStepDistanceMeters" or "MaxStepDistanceMeters" => new[] { "MinStepDistanceMeters", "MaxStepDistanceMeters" },
            "HandoffStartMeters" or "HandoffEndMeters" => new[] { "HandoffStartMeters", "HandoffEndMeters" },
            "NaturalCloudBaseAltitudeMeters" or "NaturalCloudTopAltitudeMeters" => new[] { "NaturalCloudBaseAltitudeMeters", "NaturalCloudTopAltitudeMeters" },
            "StationaryHistoryWeight" or "MovingHistoryWeight" => new[] { "StationaryHistoryWeight", "MovingHistoryWeight" },
            "CloudHistoryGateAlphaLow" or "CloudHistoryGateAlphaHigh" => new[] { "CloudHistoryGateAlphaLow", "CloudHistoryGateAlphaHigh" },
            _ => Array.Empty<string>()
        };
        var before = new Dictionary<string, Variant>();
        foreach (string companion in keys) before[companion] = bridge.Get(companion);
        return before;
    }

    private static IEnumerable<string> Keys()
    {
        foreach (string key in Ranges.Keys) yield return key;
        foreach (string key in Bools) yield return key;
        foreach (string key in Colors) yield return key;
    }

    private static bool IsKey(string key) => Ranges.ContainsKey(key) || Bools.Contains(key) || Colors.Contains(key);

    /// <summary>Compares only the supported primitive override shapes. Godot's
    /// generic Variant.Equals may not preserve boolean value semantics across
    /// managed/native variants, so never use it for persisted gameplay input.</summary>
    private static bool SameValue(string key, Variant left, Variant right)
    {
        if (Bools.Contains(key))
            return left.VariantType == Variant.Type.Bool
                && right.VariantType == Variant.Type.Bool
                && left.AsBool() == right.AsBool();
        if (Colors.Contains(key))
            return left.VariantType == Variant.Type.Color
                && right.VariantType == Variant.Type.Color
                && left.AsColor() == right.AsColor();
        if (!Ranges.TryGetValue(key, out var range)
            || left.VariantType is not (Variant.Type.Int or Variant.Type.Float)
            || right.VariantType is not (Variant.Type.Int or Variant.Type.Float))
            return false;
        return range.integer
            ? left.AsInt64() == right.AsInt64()
            : left.AsDouble() == right.AsDouble();
    }

    private static void Apply(global::RingworldTechDemo.RingSunshineCloudBridge bridge)
    {
        EnforcePairs(bridge);
        bridge.ApplyTemporalQualityTuning();
        bridge.ApplyCloudAppearanceTuning();
    }

    private static void EnforcePairs(global::RingworldTechDemo.RingSunshineCloudBridge bridge)
    {
        bridge.HandoffEndMeters = Mathf.Max(bridge.HandoffEndMeters, bridge.HandoffStartMeters + 500);
        bridge.NaturalCloudTopAltitudeMeters = Mathf.Max(bridge.NaturalCloudTopAltitudeMeters, bridge.NaturalCloudBaseAltitudeMeters + 50);
        bridge.MaxStepDistanceMeters = Mathf.Max(bridge.MaxStepDistanceMeters, bridge.MinStepDistanceMeters + 10);
        bridge.MovingHistoryWeight = Mathf.Min(bridge.MovingHistoryWeight, bridge.StationaryHistoryWeight);
        bridge.CloudHistoryGateAlphaHigh = Mathf.Max(bridge.CloudHistoryGateAlphaHigh, Mathf.Min(bridge.CloudHistoryGateAlphaLow + .05f, 1));
    }

    private void LoadInto(string path)
    {
        if (!ResourceLoader.Exists(path)) return;
        try
        {
            var source = ResourceLoader.Load<GameplayCloudSettings>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (source == null)
            {
                LastError = "Invalid gameplay cloud defaults: " + path;
                return;
            }
            foreach (Variant key in source.Overrides.Keys)
            {
                if (key.VariantType == Variant.Type.String && TryValidate(key.AsString(), source.Overrides[key], out Variant clean))
                    Settings.Overrides[key] = clean;
                else
                    LastError = "Ignored invalid gameplay cloud override in " + path;
            }
        }
        catch (Exception error)
        {
            LastError = "Could not load gameplay cloud defaults: " + error.Message;
        }
    }

    private static bool TryValidate(string key, Variant value, out Variant clean)
    {
        clean = default;
        if (Bools.Contains(key))
        {
            if (value.VariantType != Variant.Type.Bool) return false;
            clean = value.AsBool();
            return true;
        }
        if (Colors.Contains(key))
        {
            if (value.VariantType != Variant.Type.Color) return false;
            Color color = value.AsColor();
            if (!float.IsFinite(color.R) || !float.IsFinite(color.G) || !float.IsFinite(color.B) || !float.IsFinite(color.A)) return false;
            clean = color;
            return true;
        }
        if (!Ranges.TryGetValue(key, out var range) || value.VariantType is not (Variant.Type.Int or Variant.Type.Float)) return false;
        double raw = value.AsDouble();
        if (!double.IsFinite(raw)) return false;
        float number = (float)Math.Clamp(raw, range.min, range.max);
        if (key is "TerrainCloudShadowGlobalAroundResolution" or "TerrainCloudShadowGlobalAcrossResolution" or "TerrainCloudShadowRegionalResolution")
            number = Mathf.Pow(2, Mathf.Ceil(Mathf.Log(number) / Mathf.Log(2)));
        clean = range.integer ? (Variant)Mathf.RoundToInt(number) : (Variant)number;
        return true;
    }
}
