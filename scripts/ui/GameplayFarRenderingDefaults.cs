using System;
using System.IO;
using Godot;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Rendering;

namespace RingworldTechDemo.UI;

/// <summary>Far-only Cinematic detail defaults and sparse persisted player overrides.</summary>
public sealed class GameplayFarRenderingDefaults
{
    public const string UserPath = "user://gameplay_far_rendering_defaults.tres";
    public const float DefaultResolutionScale = 1.0f;
    public const int DefaultAroundSegments = 4096;
    public const int DefaultAcrossSegments = 64;
    private readonly string _path;
    public GameplayFarRenderingSettings Settings { get; private set; } = new();
    public bool Dirty { get; private set; }
    public string LastError { get; private set; } = "";

    public GameplayFarRenderingDefaults(string? pathOverride = null) => _path = pathOverride ?? UserPath;

    public void Load()
    {
        Settings = new();
        Dirty = false;
        LastError = "";
        try
        {
            if (!ResourceLoader.Exists(_path)) return;
            var loaded = ResourceLoader.Load<GameplayFarRenderingSettings>(_path, cacheMode: ResourceLoader.CacheMode.Ignore);
            if (loaded == null) { LastError = "Invalid far rendering settings"; return; }
            foreach (Variant key in loaded.Overrides.Keys)
                if (key.VariantType == Variant.Type.String && TrySanitize(key.AsString(), loaded.Overrides[key], out Variant clean))
                    Settings.Overrides[key] = clean;
            NormalizeMipOverrides();
        }
        catch (Exception error) { LastError = error.Message; }
    }

    public bool SetOverride(string key, Variant value)
    {
        if (!TrySanitize(key, value, out Variant clean)) return false;
        if (Settings.Overrides.TryGetValue(key, out Variant previous) && previous.Equals(clean)) return false;
        Settings.Overrides[key] = clean;
        NormalizeMipOverrides();
        Dirty = true;
        return true;
    }

    public Variant Value(string key, Variant fallback) => Settings.Overrides.TryGetValue(key, out Variant value) ? value : fallback;
    public void Reset() { Settings = new(); Dirty = true; LastError = ""; }

    private void NormalizeMipOverrides()
    {
        int coarsest = Value("NativeTextureStreamingInitialMip", 3).AsInt32();
        int finest = Value("NativeTextureStreamingFinestMip", 0).AsInt32();
        if (finest > coarsest) Settings.Overrides["NativeTextureStreamingFinestMip"] = coarsest;
    }

    public void Apply(RingScaledFarViewport? far)
    {
        if (far == null) return;
        far.GeometryMaxVertices = Value("GeometryMaxVertices", 750000).AsInt32();
        far.GeometryUploadsPerFrame = Value("GeometryUploadsPerFrame", 2).AsInt32();
        far.GeometryUploadBudgetMilliseconds = Value("GeometryUploadBudgetMilliseconds", 2f).AsSingle();
        far.ApplyRuntimeRenderQuality(Value("ResolutionScale", DefaultResolutionScale).AsSingle(),
            Value("AroundSegments", DefaultAroundSegments).AsInt32(), Value("AcrossSegments", DefaultAcrossSegments).AsInt32());
        far.ConfigureAdaptiveGeometry(Value("AdaptiveGeometryEnabled", true).AsBool(),
            Value("GeometryTargetPixelError", 2f).AsSingle(), Value("GeometryMaxAngularSegments", 384).AsInt32(),
            Value("GeometrySectorCount", 16).AsInt32(), Value("GeometryRefinementHysteresis", .25f).AsSingle());
        // Exported tile settings are consumed by the cache on its next update.
        foreach (string key in TileKeys)
            far.Set(key, Value(key, TileDefault(key)));
        NativeTextureStreaming.Apply(Value("NativeTextureStreamingBudgetMiB", 512).AsInt32(),
            Value("NativeTextureStreamingInitialMip", 3).AsInt32(), Value("NativeTextureStreamingFinestMip", 0).AsInt32());
    }

    public static readonly string[] TileKeys = { "FarSurfaceTilesEnabled", "FarSurfaceTileResolution", "FarSurfaceTileCpuBudgetMb", "FarSurfaceTileGpuBudgetMb", "FarSurfaceTileMaximumJobs", "FarSurfaceTileUploadsPerFrame", "FarSurfaceTileUploadBudgetMilliseconds" };
    private static Variant TileDefault(string key) => key switch
    {
        "FarSurfaceTilesEnabled" => true,
        "FarSurfaceTileResolution" => 128,
        "FarSurfaceTileCpuBudgetMb" or "FarSurfaceTileGpuBudgetMb" => 64,
        "FarSurfaceTileMaximumJobs" or "FarSurfaceTileUploadsPerFrame" => 2,
        _ => 2f
    };

    public static bool TrySanitize(string key, Variant value, out Variant clean)
    {
        clean = default;
        if (key is "AdaptiveGeometryEnabled" or "FarSurfaceTilesEnabled")
        {
            if (value.VariantType != Variant.Type.Bool) return false;
            clean = value;
            return true;
        }
        if (value.VariantType is not (Variant.Type.Float or Variant.Type.Int)) return false;
        double number = value.AsDouble();
        if (!double.IsFinite(number)) return false;
        (double min, double max, bool integer) bounds = key switch
        {
            "ResolutionScale" => (.25, 1, false),
            "AroundSegments" => (256, 4096, true),
            "AcrossSegments" => (8, 64, true),
            "GeometryTargetPixelError" => (.5, 16, false),
            "GeometryMaxAngularSegments" => (96, 768, true),
            "GeometrySectorCount" => (4, 32, true),
            "GeometryRefinementHysteresis" => (.1, .5, false),
            "GeometryMaxVertices" => (100000, 2000000, true),
            "GeometryUploadsPerFrame" => (1, 8, true),
            "GeometryUploadBudgetMilliseconds" => (.25, 8, false),
            "FarSurfaceTileResolution" => (32, 256, true),
            "FarSurfaceTileCpuBudgetMb" or "FarSurfaceTileGpuBudgetMb" => (8, 512, true),
            "FarSurfaceTileMaximumJobs" => (1, 4, true),
            "FarSurfaceTileUploadsPerFrame" => (1, 8, true),
            "FarSurfaceTileUploadBudgetMilliseconds" => (.25, 8, false),
            "NativeTextureStreamingBudgetMiB" => (64, 8192, true),
            "NativeTextureStreamingInitialMip" or "NativeTextureStreamingFinestMip" => (0, 13, true),
            _ => (0, 0, false)
        };
        if (bounds.max == 0) return false;
        double bounded = Math.Clamp(number, bounds.min, bounds.max);
        clean = bounds.integer ? Variant.From((int)Math.Round(bounded)) : Variant.From((float)bounded);
        return true;
    }

    public bool Save()
    {
        if (!Dirty) return true;
        string temporary = "";
        try
        {
            string full = ProjectSettings.GlobalizePath(_path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp.tres";
            if (ResourceSaver.Save(Settings, temporary) != Error.Ok) throw new IOException("Could not save far rendering settings");
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
