using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.UI;

/// <summary>Small, sparse player grass overrides. Absent keys always follow the selected quality profile.</summary>
public sealed class GameplayGrassDefaults
{
    public const string UserPath = "user://gameplay_grass_defaults.tres";
    private readonly string? _pathOverride;
    public GameplayGrassSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";
    public bool Initialized { get; private set; }
    public GameplayGrassDefaults(string? pathOverride = null) => _pathOverride = pathOverride;
    public string GetPath() => _pathOverride ?? UserPath;

    public void Initialize(RingGraphicsQualityController? graphics) { if (Initialized) return; Load(); Apply(graphics); Initialized = true; }
    public bool SetOverride(RingGraphicsQualityController? graphics, string key, Variant value)
    {
        if (graphics == null || !TryValidate(key, value, out Variant clean)) return false;
        float minimumHeight = graphics.GrassLayer?.GrassMinimumHeightMeters ?? .10f;
        float maximumHeight = graphics.GrassLayer?.GrassMaximumHeightMeters ?? .20f;
        if (key == "GrassMinimumHeightMeters") clean = Mathf.Min(clean.AsSingle(), maximumHeight - .01f);
        if (key == "GrassMaximumHeightMeters") clean = Mathf.Max(clean.AsSingle(), minimumHeight + .01f);
        float near = Number("LodNearDistanceMeters", graphics.GrassLayer?.LodNearDistanceMeters ?? 80f);
        float mid = Number("LodMidDistanceMeters", graphics.GrassLayer?.LodMidDistanceMeters ?? 250f);
        float far = Number("LodFarDistanceMeters", graphics.GrassLayer?.LodFarDistanceMeters ?? 500f);
        if (key == "LodNearDistanceMeters") clean = Mathf.Min(clean.AsSingle(), mid - 1f);
        if (key == "LodMidDistanceMeters") clean = Mathf.Clamp(clean.AsSingle(), near + 1f, far - 1f);
        if (key == "LodFarDistanceMeters") clean = Mathf.Max(clean.AsSingle(), mid + 1f);
        if (key == "TerrainGrassCoverageStartMeters") clean = Mathf.Min(clean.AsSingle(), Number("TerrainGrassCoverageEndMeters", 700f) - 1f);
        if (key == "TerrainGrassCoverageEndMeters") clean = Mathf.Max(clean.AsSingle(), Number("TerrainGrassCoverageStartMeters", 200f) + 1f);
        float cameraFacingStart = Number("GrassCameraFacingStartMeters", graphics.GrassLayer?.GrassCameraFacingStartMeters ?? 12f);
        float cameraFacingEnd = Number("GrassCameraFacingEndMeters", graphics.GrassLayer?.GrassCameraFacingEndMeters ?? 48f);
        if (key == "GrassCameraFacingStartMeters") clean = Mathf.Min(clean.AsSingle(), cameraFacingEnd - 1f);
        if (key == "GrassCameraFacingEndMeters") clean = Mathf.Max(clean.AsSingle(), cameraFacingStart + 1f);
        float groupingStart = Number("GrassGroupingStartMeters", graphics.GrassLayer?.GrassGroupingStartMeters ?? 128f);
        float groupingFarStart = Number("GrassGroupingFarStartMeters", graphics.GrassLayer?.GrassGroupingFarStartMeters ?? 256f);
        if (key == "GrassGroupingStartMeters") clean = Mathf.Min(clean.AsSingle(), groupingFarStart - 1f);
        if (key == "GrassGroupingFarStartMeters") clean = Mathf.Max(clean.AsSingle(), groupingStart + 1f);
        float blendStart = Number("GrassTerrainBlendStartPercent", graphics.GrassLayer?.GrassTerrainBlendStartPercent ?? 70f);
        float blendEnd = Number("GrassTerrainBlendEndPercent", graphics.GrassLayer?.GrassTerrainBlendEndPercent ?? 90f);
        if (key == "GrassTerrainBlendStartPercent") clean = Mathf.Clamp(clean.AsSingle(), 0f, blendEnd - 1f);
        if (key == "GrassTerrainBlendEndPercent") clean = Mathf.Clamp(clean.AsSingle(), blendStart + 1f, 100f);
        if (Settings.Overrides.TryGetValue(key, out Variant old) && Same(old, clean)) return false;
        Settings.Overrides[key] = clean; Dirty = true; Apply(graphics); return true;
    }
    public static readonly string[] WindResponseKeys = { "GrassWindStrengthMultiplier", "GrassWindGustMultiplier", "EnableGrassGustTexture", "GrassGustStrength", "GrassGustBaseBreeze" };
    public void Reset(RingGraphicsQualityController? graphics)
    {
        var preserved = new GameplayGrassSettings();
        foreach (string key in WindResponseKeys) if (Settings.Overrides.TryGetValue(key, out Variant value)) preserved.Overrides[key] = value;
        Settings = preserved; ReapplyReset(graphics);
    }
    public void ResetWindResponse(RingGraphicsQualityController? graphics)
    {
        foreach (string key in WindResponseKeys) Settings.Overrides.Remove(key);
        ReapplyReset(graphics);
    }
    private void ReapplyReset(RingGraphicsQualityController? graphics)
    {
        LastError = ""; Dirty = true;
        // SetGrassTuningOverrides restores the profile, then applies these sparse keys.
        // A second profile reset would discard the retained live wind values.
        graphics?.SetGrassTuningOverrides(RuntimeOverrides()); ApplyTerrainCoverage();
    }
    public void Apply(RingGraphicsQualityController? graphics) { graphics?.SetGrassTuningOverrides(RuntimeOverrides()); ApplyTerrainCoverage(); }
    public Variant GetValue(string key, Variant fallback) => Settings.Overrides.TryGetValue(key, out Variant value) ? value : TerrainDefaults.TryGetValue(key, out Variant terrain) ? terrain : fallback;
    private float Number(string key, float fallback) => GetValue(key, fallback).AsSingle();
    private Godot.Collections.Dictionary RuntimeOverrides() { var runtime = new Godot.Collections.Dictionary(); foreach (Variant key in Settings.Overrides.Keys) if (!TerrainDefaults.ContainsKey(key.AsString())) runtime[key] = Settings.Overrides[key]; return runtime; }
    private void ApplyTerrainCoverage()
    {
        RenderingServer.GlobalShaderParameterSet("ring_grass_terrain_coverage_enabled",
            GetValue("EnableTerrainGrassCoverage", true).AsBool() ? 1.0f : 0.0f);
        RenderingServer.GlobalShaderParameterSet("ring_grass_terrain_coverage_start_meters", GetValue("TerrainGrassCoverageStartMeters", 200f));
        RenderingServer.GlobalShaderParameterSet("ring_grass_terrain_coverage_end_meters", GetValue("TerrainGrassCoverageEndMeters", 700f));
        RenderingServer.GlobalShaderParameterSet("ring_grass_terrain_coverage_strength", GetValue("TerrainGrassCoverageStrength", .45f));
    }
    public bool Save()
    {
        if (!Dirty) return true; string temporary = "";
        try { string path = GetPath(); string directory = Path.GetDirectoryName(ProjectSettings.GlobalizePath(path))!; if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp.tres"; Error result = ResourceSaver.Save(Settings, temporary); if (result != Error.Ok) throw new IOException(result.ToString());
            File.Move(ProjectSettings.GlobalizePath(temporary), ProjectSettings.GlobalizePath(path), true); temporary = ""; Dirty = false; LastError = ""; return true; }
        catch (Exception error) { LastError = error.Message; return false; }
        finally { if (temporary.Length > 0) try { File.Delete(ProjectSettings.GlobalizePath(temporary)); } catch { } }
    }
    private void Load()
    {
        Settings = new(); LastError = ""; string path = GetPath(); if (!ResourceLoader.Exists(path)) { Dirty = false; return; }
        try { var loaded = ResourceLoader.Load<GameplayGrassSettings>(path, cacheMode: ResourceLoader.CacheMode.Ignore); if (loaded == null) { LastError = "Invalid grass tuning settings"; return; }
            foreach (Variant k in loaded.Overrides.Keys) if (k.VariantType == Variant.Type.String && TryValidate(k.AsString(), loaded.Overrides[k], out Variant v)) Settings.Overrides[k.AsString()] = v; }
        catch (Exception error) { LastError = error.Message; } Dirty = false;
    }
    public static readonly string[] Keys = { "Enabled", "UseGpuCulling", "GpuFrustumCulling", "SkipUnchangedGpuCulling", "GroupGrassComputePhases", "CullGrassTiles", "EnableAdaptiveGrassGrouping", "GrassGroupingStartMeters", "GrassGroupingFarStartMeters", "GrassGroupingMaximumLevel", "GrassGroupingHysteresisMeters", "MaximumGrassSourcesPerGroup", "CacheGrassPresentation", "UseGrassClumps", "EnableMeadowTufts", "MeadowTuftSpread", "MeadowTuftVariant", "GrassMinimumHeightMeters", "GrassMaximumHeightMeters", "EnableCoordinatedGrassHandoff", "UseReducedNearGrassGeometry", "UseSingleRibbonIntermediateGrass", "UseSimplifiedIntermediateGrassShading", "GrassFrustumPaddingMeters", "GpuGenerationPrototypeTileCount", "BladesPerSquareMeter", "VisibilityRadiusMeters", "EnableHierarchicalCoverage", "LodNearDistanceMeters", "LodMidDistanceMeters", "LodFarDistanceMeters", "LodMidSourceDensity", "LodFarSourceDensity", "LodTransitionWidthMeters", "LodFarDensity", "EnableTerrainGrassCoverage", "TerrainGrassCoverageStartMeters", "TerrainGrassCoverageEndMeters", "TerrainGrassCoverageStrength", "EnableGrassInteraction", "GrassInteractionRadiusMeters", "GrassInteractionStrength", "GrassInteractionRecoverySeconds", "GrassWindStrengthMultiplier", "GrassWindGustMultiplier", "GrassMaximumWidthScale", "EnableGrassGustTexture", "GrassGustStrength", "GrassGustScaleMeters", "GrassGustTravelSpeedMultiplier", "GrassGustBaseBreeze", "EnableGrassModifiers", "GrassModifierDensityStrength", "GrassModifierTintStrength", "GrassModifierPreviewEnabled", "GrassModifierPreviewRadiusMeters", "GrassModifierPreviewFeatherMeters", "GrassModifierPreviewDensity", "GrassModifierPreviewTint", "GrassModifierPreviewTintStrength", "EnableGrassCameraFacing", "GrassCameraFacingStrength", "GrassCameraFacingStartMeters", "GrassCameraFacingEndMeters", "EnableGrassAppearance", "EnableGrassBladeShading", "GrasslandBiomeDensity", "ForestBiomeDensity", "SparseBiomeDensity", "GrasslandBiomeHeight", "ForestBiomeHeight", "SparseBiomeHeight", "GrassTerrainHandoffStrength", "GrassTerrainBlendStartPercent", "GrassTerrainBlendEndPercent", "GrassTerrainLightingMatch", "GrasslandBiomeTint", "ForestBiomeTint", "SparseBiomeTint", "GrassDebugMode", "ShowGrassTileBounds", "MaximumInstancesPerTile", "MaximumInstances", "MaximumResidentTiles", "MaximumTileBuildsInFlight", "MaximumTileUploadsPerFrame", "MaximumTileUploadMilliseconds", "MaximumGpuCullingBatchesPerFrame", "SurfaceCacheMegabytes" };
    private static readonly Dictionary<string, Variant> TerrainDefaults = new() { ["EnableTerrainGrassCoverage"] = true, ["TerrainGrassCoverageStartMeters"] = 200f, ["TerrainGrassCoverageEndMeters"] = 700f, ["TerrainGrassCoverageStrength"] = .45f };
    private static readonly Dictionary<string, (double min, double max, bool integral)> Limits = new() {
        ["GrassDebugMode"]=(0,5,true),
        ["MeadowTuftSpread"]=(.25,2,false), ["MeadowTuftVariant"]=(0,3,true),
        ["GrassMinimumHeightMeters"]=(.03,1.99,false), ["GrassMaximumHeightMeters"]=(.04,2,false),
        ["GpuGenerationPrototypeTileCount"]=(1,4,true),
        ["GrassGroupingStartMeters"]=(16,1500,false), ["GrassGroupingFarStartMeters"]=(32,2000,false),
        ["GrassGroupingMaximumLevel"]=(1,2,true), ["GrassGroupingHysteresisMeters"]=(0,128,false),
        ["MaximumGrassSourcesPerGroup"]=(256,100000,true),
        ["BladesPerSquareMeter"]=(0,96,false), ["VisibilityRadiusMeters"]=(16,350,false), ["GrassFrustumPaddingMeters"]=(0,8,false), ["LodNearDistanceMeters"]=(8,350,false), ["LodMidDistanceMeters"]=(32,750,false), ["LodFarDistanceMeters"]=(32,1500,false), ["LodMidSourceDensity"]=(.05,1,false), ["LodFarSourceDensity"]=(.01,.5,false), ["TerrainGrassCoverageStartMeters"]=(32,2000,false), ["TerrainGrassCoverageEndMeters"]=(64,4000,false), ["TerrainGrassCoverageStrength"]=(0,1,false), ["LodTransitionWidthMeters"]=(1,64,false), ["LodFarDensity"]=(.05,1,false), ["GrassInteractionRadiusMeters"]=(.2,5,false), ["GrassInteractionStrength"]=(0,2,false), ["GrassInteractionRecoverySeconds"]=(.1,20,false), ["GrassWindStrengthMultiplier"]=(0,2,false), ["GrassWindGustMultiplier"]=(0,2,false), ["GrassMaximumWidthScale"]=(1,2,false), ["GrassGustStrength"]=(0,2,false), ["GrassGustScaleMeters"]=(48,2000,false), ["GrassGustTravelSpeedMultiplier"]=(0,4,false), ["GrassGustBaseBreeze"]=(0,1,false), ["GrassModifierDensityStrength"]=(0,1,false), ["GrassModifierTintStrength"]=(0,1,false), ["GrassModifierPreviewRadiusMeters"]=(.25,128,false), ["GrassModifierPreviewFeatherMeters"]=(0,128,false), ["GrassModifierPreviewDensity"]=(0,1,false), ["GrassModifierPreviewTintStrength"]=(0,1,false), ["GrassCameraFacingStrength"]=(0,1,false), ["GrassCameraFacingStartMeters"]=(0,1500,false), ["GrassCameraFacingEndMeters"]=(1,2000,false), ["GrasslandBiomeDensity"]=(0,2,false), ["ForestBiomeDensity"]=(0,2,false), ["SparseBiomeDensity"]=(0,2,false), ["GrasslandBiomeHeight"]=(.25,2,false), ["ForestBiomeHeight"]=(.25,2,false), ["SparseBiomeHeight"]=(.25,2,false), ["GrassTerrainHandoffStrength"]=(0,1,false), ["GrassTerrainBlendStartPercent"]=(0,99,false), ["GrassTerrainBlendEndPercent"]=(1,100,false), ["GrassTerrainLightingMatch"]=(0,1,false), ["MaximumInstancesPerTile"]=(100,100000,true), ["MaximumInstances"]=(1000,5000000,true), ["MaximumResidentTiles"]=(16,1024,true), ["MaximumTileBuildsInFlight"]=(1,8,true), ["MaximumTileUploadsPerFrame"]=(1,16,true), ["MaximumTileUploadMilliseconds"]=(.1,20,false), ["MaximumGpuCullingBatchesPerFrame"]=(16,1024,true), ["SurfaceCacheMegabytes"]=(0,16,false) };
    private static bool TryValidate(string key, Variant value, out Variant clean)
    {
        clean = default; if (key is "Enabled" or "UseGpuCulling" or "GpuFrustumCulling" or "SkipUnchangedGpuCulling" or "GroupGrassComputePhases" or "CullGrassTiles" or "EnableAdaptiveGrassGrouping" or "CacheGrassPresentation" or "UseGrassClumps" or "EnableMeadowTufts" or "EnableCoordinatedGrassHandoff" or "UseReducedNearGrassGeometry" or "UseSingleRibbonIntermediateGrass" or "UseSimplifiedIntermediateGrassShading" or "EnableHierarchicalCoverage" or "EnableTerrainGrassCoverage" or "EnableGrassInteraction" or "EnableGrassGustTexture" or "EnableGrassModifiers" or "GrassModifierPreviewEnabled" or "EnableGrassCameraFacing" or "EnableGrassAppearance" or "EnableGrassBladeShading" or "ShowGrassTileBounds") { if (value.VariantType != Variant.Type.Bool) return false; clean = value.AsBool(); return true; }
        if (key is "GrasslandBiomeTint" or "ForestBiomeTint" or "SparseBiomeTint" or "GrassModifierPreviewTint")
        {
            if (value.VariantType != Variant.Type.Color) return false;
            Color color = value.AsColor();
            if (!float.IsFinite(color.R) || !float.IsFinite(color.G) || !float.IsFinite(color.B)) return false;
            clean = new Color(Mathf.Clamp(color.R, 0, 1), Mathf.Clamp(color.G, 0, 1), Mathf.Clamp(color.B, 0, 1), 1); return true;
        }
        if (!Limits.TryGetValue(key, out var limit) || value.VariantType is not (Variant.Type.Int or Variant.Type.Float) || !double.IsFinite(value.AsDouble())) return false;
        double number = Math.Clamp(value.AsDouble(), limit.min, limit.max); clean = limit.integral ? (Variant)(long)Math.Round(number) : (Variant)(float)number; return true;
    }
    private static bool Same(Variant a, Variant b)
    {
        if (a.VariantType != b.VariantType) return false;
        return a.VariantType switch
        {
            Variant.Type.Bool => a.AsBool() == b.AsBool(),
            Variant.Type.Color => a.AsColor().IsEqualApprox(b.AsColor()),
            _ => Math.Abs(a.AsDouble() - b.AsDouble()) < .0001
        };
    }
}
