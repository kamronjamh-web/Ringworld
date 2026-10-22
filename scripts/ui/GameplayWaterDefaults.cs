using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Validated, sparse water overrides kept separate from world saves and graphics presets.</summary>
public sealed class GameplayWaterDefaults
{
    public const string FactoryPath = "res://resources/water/gameplay_water_defaults.tres";
    public const string UserPath = "user://gameplay_water_defaults.tres";
    private readonly string? _pathOverride;
    private readonly Dictionary<string, Variant> _baseline = new();
    public GameplayWaterSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";
    public bool Initialized { get; private set; }
    public GameplayWaterDefaults(string? pathOverride = null) => _pathOverride = pathOverride;
    public string GetPath() => _pathOverride ?? UserPath;

    public void Initialize(Node? graphics, Node? fft)
    {
        if (Initialized) return;
        CaptureBaseline(graphics, fft); LoadAndApply(graphics, fft); Initialized = true;
    }
    public void LoadAndApply(Node? graphics, Node? fft) { Load(); Apply(graphics, fft); }
    public void Load()
    {
        Settings = new(); LastError = "";
        if (_pathOverride != null) LoadInto(_pathOverride); else { LoadInto(FactoryPath); LoadInto(UserPath); }
        Dirty = false;
    }
    public bool SetOverride(Node? graphics, Node? fft, string key, Variant value)
    {
        if (!TryValidate(key, value, out Variant clean)) return false;
        Node? target = Target(key, graphics, fft); string property = Property(key);
        if (target == null) return false;
        bool same = Settings.Overrides.TryGetValue(key, out Variant old) && Same(old, clean);
        if (same && Same(target.Get(property), clean)) return false;
        target.Set(property, clean);
        Settings.Overrides[key] = target.Get(property);
        EnforceOrderedPairs(graphics, fft);
        Dirty = true;
        Apply(graphics, fft);
        return true;
    }
    public void Reset(Node? graphics, Node? fft)
    {
        Settings = new(); LastError = "";
        foreach (var item in _baseline) { Node? target = Target(item.Key, graphics, fft); if (target != null) target.Set(Property(item.Key), item.Value); }
        Apply(graphics, fft); Dirty = true;
    }
    public bool Save()
    {
        if (!Dirty) return true; string temporary = "";
        try
        {
            string path = GetPath(); string directory = Path.GetDirectoryName(ProjectSettings.GlobalizePath(path))!;
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp.tres";
            Error result = ResourceSaver.Save(Settings, temporary); if (result != Error.Ok) throw new IOException(result.ToString());
            File.Move(ProjectSettings.GlobalizePath(temporary), ProjectSettings.GlobalizePath(path), true);
            temporary = ""; Dirty = false; LastError = ""; return true;
        }
        catch (Exception error) { LastError = error.Message; return false; }
        finally { if (temporary.Length > 0) try { File.Delete(ProjectSettings.GlobalizePath(temporary)); } catch { } }
    }
    private void CaptureBaseline(Node? graphics, Node? fft) { foreach (string key in Keys) { Node? target = Target(key, graphics, fft); if (target != null) _baseline[key] = target.Get(Property(key)); } }
    private void Apply(Node? graphics, Node? fft)
    {
        foreach (Variant item in Settings.Overrides.Keys)
        {
            string key = item.AsString();
            if (!TryValidate(key, Settings.Overrides[item], out Variant clean)) continue;
            Node? target = Target(key, graphics, fft);
            if (target != null) target.Set(Property(key), clean);
        }
        EnforceOrderedPairs(graphics, fft);
        // The controller keeps SSR choices across quality presets, so register them with it rather than relying on one-shot property writes.
        // Register the values as applied (validated and pair-ordered), not the raw stored ones.
        var ssr = new Godot.Collections.Dictionary();
        if (graphics != null)
            foreach (Variant item in Settings.Overrides.Keys)
                if (item.AsString().StartsWith("WaterSsr", StringComparison.Ordinal)) ssr[item] = graphics.Get(item.AsString());
        graphics?.Call("SetWaterSsrOverrides", ssr);
        graphics?.Call("ApplyWaterTuning");
    }
    private void LoadInto(string path)
    {
        if (!ResourceLoader.Exists(path)) return;
        try { var loaded = ResourceLoader.Load<GameplayWaterSettings>(path, cacheMode: ResourceLoader.CacheMode.Ignore); if (loaded == null) { LastError = "Invalid gameplay water defaults"; return; } foreach (Variant k in loaded.Overrides.Keys) if (k.VariantType == Variant.Type.String && TryValidate(k.AsString(), loaded.Overrides[k], out Variant v)) Settings.Overrides[k.AsString()] = v; else LastError = "Ignored invalid gameplay water override"; }
        catch (Exception error) { LastError = error.Message; }
    }
    private static bool Same(Variant a, Variant b) => a.VariantType == b.VariantType && a.VariantType switch { Variant.Type.Bool => a.AsBool() == b.AsBool(), Variant.Type.Color => a.AsColor() == b.AsColor(), Variant.Type.Int => a.AsInt64() == b.AsInt64(), Variant.Type.Float => a.AsDouble() == b.AsDouble(), _ => false };
    private static Node? Target(string key, Node? graphics, Node? fft) => key.StartsWith("Fft", StringComparison.Ordinal) ? fft : graphics;
    private static string Property(string key) => key.StartsWith("Fft", StringComparison.Ordinal) ? key[3..] : key;
    private static readonly string[] BaseKeys = {
        "WaterWaveHeightMeters", "WaterWaveLengthMeters", "WaterWaveSpeed", "WaterWaveDetailStrength", "WaterWaveDomainWarpStrength", "WaterWaveCrestSharpness", "WaterWaveCrestBlend", "WaterWaveNormalStrength", "WaterWaveBandlimitStartRadians", "WaterWaveBandlimitEndRadians", "WaterRefractionStrength", "WaterAbsorptionDensity", "WaterAbsorptionCoefficients", "WaterShallowInscatterStrength", "WaterCrestTransmissionStrength", "WaterShallowAlpha", "WaterDeepAlpha", "WaterSsrEnabled", "WaterSsrStrength", "WaterSsrCoarseSteps", "WaterSsrRefinementSteps", "WaterSsrMaxRayDistance", "WaterSsrCameraDistance", "WaterSsrDepthToleranceNear", "WaterSsrDepthToleranceFar", "WaterSsrEdgeFade", "WaterSsrRoughnessCutoff", "WaterSsrDebugMode", "WaterShallowTint", "WaterDeepTint", "WaterFoamColor", "WaterShoreFoamDepthMeters", "WaterShoreFoamStrength", "WaterBreakingFoamStrength", "WaterBreakingFoamSlopeStart", "WaterBreakingFoamSlopeEnd", "WaterSwellVariation", "WaterFoamPatchiness", "WaterDistantFoamVisibility", "WaterDetailFilteringStrength", "WaterWaveGeometryFadeMeters", "WaterWaveNormalFadeStartMeters", "WaterWaveNormalFadeEndMeters", "WaterFarRoughness", "WaterFresnelF0", "WaterFresnelStrength", "WaterSpecularAaStrength", "FftEnabled", "FftResolution", "FftCascadeCount", "FftUpdatesPerSecond", "FftAdaptiveUpdatesEnabled", "FftMinimumUpdatesPerSecond", "FftFullRateDistanceMeters", "FftFullRateAreaSquareKilometers", "FftIdleGraceSeconds", "FftNormalStrength", "FftFoamStrength", "FftFoamAmount", "FftWhitecap", "FftWindSpeedMetersPerSecond", "FftWindDirectionDegrees", "FftFollowWeatherWind", "FftSmallWaveLengthMeters", "FftLargeWaveLengthMeters", "FftDetailDistanceMeters", "FftGeometryHeightScale", "FftGeometryChoppiness", "FftGeometryDistanceMeters", "FftGeometryFilterMeters", "FftCrestLightingStrength", "FftShorelineFadeDistanceMeters", "FftShorelineChopFadeDistanceMeters", "FftShorelineShallowDepthMeters", "FftDeepWaterWaveDepthMeters" };
    public static readonly string[] Keys = BaseKeys.Concat(new[] { "FftGeometryUpdatesPerSecond", "WaterSsrRayBias" }).ToArray();
    private static readonly Dictionary<string, (float min, float max, bool integral)> Limits = new() {
        ["WaterSsrStrength"]=(0,1,false), ["WaterSsrCoarseSteps"]=(1,32,true), ["WaterSsrRefinementSteps"]=(0,8,true),
        ["WaterSsrMaxRayDistance"]=(10,1000,false), ["WaterSsrCameraDistance"]=(10,1000,false),
        ["WaterSsrDepthToleranceNear"]=(.01f,2,false), ["WaterSsrDepthToleranceFar"]=(.1f,8,false),
        ["WaterSsrRayBias"]=(.01f,1,false), ["FftMinimumUpdatesPerSecond"]=(1,30,false), ["FftFullRateDistanceMeters"]=(25,1000,false), ["FftFullRateAreaSquareKilometers"]=(.01f,16,false), ["FftIdleGraceSeconds"]=(0,5,false),
        ["WaterSsrEdgeFade"]=(.01f,.4f,false), ["WaterSsrRoughnessCutoff"]=(.02f,1,false), ["WaterSsrDebugMode"]=(0,3,true),
        ["WaterWaveHeightMeters"]=(0,10,false), ["WaterWaveLengthMeters"]=(2,200,false), ["WaterWaveSpeed"]=(0,10,false), ["WaterWaveDetailStrength"]=(0,1,false), ["WaterWaveDomainWarpStrength"]=(0,1.5f,false), ["WaterWaveCrestSharpness"]=(.05f,4,false), ["WaterWaveCrestBlend"]=(0,1,false), ["WaterWaveNormalStrength"]=(0,2,false), ["WaterWaveBandlimitStartRadians"]=(.05f,2,false), ["WaterWaveBandlimitEndRadians"]=(.1f,3,false), ["WaterRefractionStrength"]=(0,.08f,false), ["WaterAbsorptionDensity"]=(.001f,2,false), ["WaterShallowInscatterStrength"]=(0,2,false), ["WaterCrestTransmissionStrength"]=(0,2,false), ["WaterShallowAlpha"]=(0,1,false), ["WaterDeepAlpha"]=(0,1,false), ["WaterShoreFoamDepthMeters"]=(.1f,20,false), ["WaterShoreFoamStrength"]=(0,2,false), ["WaterBreakingFoamStrength"]=(0,2,false), ["WaterBreakingFoamSlopeStart"]=(.05f,1.5f,false), ["WaterBreakingFoamSlopeEnd"]=(.1f,2,false), ["WaterSwellVariation"]=(0,1,false), ["WaterFoamPatchiness"]=(0,1,false), ["WaterDistantFoamVisibility"]=(0,1,false), ["WaterDetailFilteringStrength"]=(0,2,false), ["WaterWaveGeometryFadeMeters"]=(100,50000,false), ["WaterWaveNormalFadeStartMeters"]=(100,50000,false), ["WaterWaveNormalFadeEndMeters"]=(200,100000,false), ["WaterFarRoughness"]=(.05f,1,false), ["WaterFresnelF0"]=(0,.12f,false), ["WaterFresnelStrength"]=(0,1.5f,false), ["WaterSpecularAaStrength"]=(0,2,false), ["FftResolution"]=(128,1024,true), ["FftCascadeCount"]=(1,4,true), ["FftUpdatesPerSecond"]=(5,120,false), ["FftNormalStrength"]=(0,1,false), ["FftFoamStrength"]=(0,1,false), ["FftWhitecap"]=(0,2,false), ["FftWindSpeedMetersPerSecond"]=(.1f,60,false), ["FftWindDirectionDegrees"]=(-180,180,false), ["FftSmallWaveLengthMeters"]=(4,256,false), ["FftLargeWaveLengthMeters"]=(16,2048,false), ["FftDetailDistanceMeters"]=(25,4000,false), ["FftGeometryHeightScale"]=(0,10,false), ["FftGeometryChoppiness"]=(0,3,false), ["FftGeometryDistanceMeters"]=(25,2000,false), ["FftGeometryFilterMeters"]=(1,8,false), ["FftCrestLightingStrength"]=(0,1,false), ["FftShorelineFadeDistanceMeters"]=(1,64,false), ["FftShorelineChopFadeDistanceMeters"]=(1,64,false), ["FftShorelineShallowDepthMeters"]=(.1f,20,false), ["FftDeepWaterWaveDepthMeters"]=(5,500,false) };
    private static bool TryValidate(string key, Variant value, out Variant clean)
    {
        clean = default; if (key is "FftEnabled" or "FftAdaptiveUpdatesEnabled" or "FftFollowWeatherWind" or "WaterSsrEnabled") { if (value.VariantType != Variant.Type.Bool) return false; clean = value.AsBool(); return true; }
        if (key == "FftGeometryUpdatesPerSecond") { if (value.VariantType is not (Variant.Type.Int or Variant.Type.Float) || !double.IsFinite(value.AsDouble())) return false; clean = Mathf.Clamp((float)value.AsDouble(), 5, 120); return true; }
        if (key is "WaterShallowTint" or "WaterDeepTint" or "WaterFoamColor" or "WaterAbsorptionCoefficients") { if (value.VariantType != Variant.Type.Color) return false; Color c = value.AsColor(); if (!float.IsFinite(c.R)||!float.IsFinite(c.G)||!float.IsFinite(c.B)||!float.IsFinite(c.A) || c.R < 0 || c.R > 1 || c.G < 0 || c.G > 1 || c.B < 0 || c.B > 1 || c.A < 0 || c.A > 1) return false; clean=c; return true; }
        if (!Limits.TryGetValue(key, out var limit) || value.VariantType is not (Variant.Type.Int or Variant.Type.Float) || !double.IsFinite(value.AsDouble())) return false;
        float raw = (float)value.AsDouble();
        if (key == "FftResolution" && raw is not (128 or 256 or 512 or 1024)) return false;
        float f = Mathf.Clamp(raw, limit.min, limit.max);
        clean = limit.integral ? (Variant)Mathf.RoundToInt(f) : (Variant)f; return true;
    }
    private void EnforceOrderedPairs(Node? graphics, Node? fft)
    {
        EnforcePair(graphics, "WaterBreakingFoamSlopeStart", "WaterBreakingFoamSlopeEnd", .01f);
        EnforcePair(graphics, "WaterWaveBandlimitStartRadians", "WaterWaveBandlimitEndRadians", .01f);
        EnforcePair(graphics, "WaterWaveNormalFadeStartMeters", "WaterWaveNormalFadeEndMeters", 1f);
        EnforcePair(fft, "ShorelineShallowDepthMeters", "DeepWaterWaveDepthMeters", 1f, "Fft");
    }
    private void EnforcePair(Node? target, string startKey, string endKey, float gap, string overridePrefix = "")
    {
        if (target == null) return;
        float start = target.Get(startKey).AsSingle();
        float end = target.Get(endKey).AsSingle();
        if (end >= start + gap) return;
        string startLimitKey = overridePrefix + startKey;
        string endLimitKey = overridePrefix + endKey;
        float correctedEnd = Mathf.Min(Limits[endLimitKey].max, start + gap);
        float correctedStart = correctedEnd >= start + gap ? start : Mathf.Max(Limits[startLimitKey].min, correctedEnd - gap);
        target.Set(startKey, correctedStart); target.Set(endKey, correctedEnd);
        Settings.Overrides[overridePrefix + startKey] = correctedStart; Settings.Overrides[overridePrefix + endKey] = correctedEnd;
    }
}
