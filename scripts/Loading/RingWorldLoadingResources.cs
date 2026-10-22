using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Loading;

/// <summary>Warms engine resource caches and the exact terrain palette before scene attachment.</summary>
public partial class RingWorldLoadingResources : Node
{
    private static readonly string[] PalettePaths =
    {
        "res://materials/third_party/voxelgame/grass_albedo.png",
        "res://materials/biomes/desert_sand_albedo.png",
        "res://materials/biomes/forest_floor_albedo.png",
        "res://materials/biomes/tundra_ground_albedo.png",
        "res://materials/biomes/alpine_rock_albedo.png"
    };
    private static readonly string[] StartupPaths =
    {
        "res://materials/third_party/voxelgame/grass_albedo.png",
        "res://materials/third_party/voxelgame/dirt_albedo.png",
        "res://materials/biomes/desert_sand_albedo.png",
        "res://materials/biomes/forest_floor_albedo.png",
        "res://materials/biomes/tundra_ground_albedo.png",
        "res://materials/biomes/alpine_rock_albedo.png",
        "res://materials/ring_terrain_proxy.gdshader",
        "res://materials/ring_terrain_proxy_shadow.gdshader",
        "res://materials/ring_ocean_proxy.gdshader",
        "res://materials/ring_terrain_global.gdshader",
        "res://materials/ring_ocean_global.gdshader",
        "res://materials/ring_hull_global.gdshader",
        "res://materials/voxel_ring_terrain.gdshader",
        "res://materials/ring_standing_grass.gdshader",
        "res://materials/ring_tree.gdshader",
        "res://materials/ring_scale_anchor.gdshader",
        "res://materials/ring_terrain_minimap.gdshader",
        "res://materials/ring_mesh_cloud.gdshader",
        "res://materials/ring_cloud_layer.gdshader",
        "res://materials/ring_cloud_canopy.gdshader",
        "res://materials/ring_volumetric_cloud.gdshader",
        "res://materials/ring_local_volumetric_cloud.gdshader",
        "res://materials/ring_far_cloud_clipmap.gdshader",
        "res://materials/ring_cloud_transmittance_mask.gdshader",
        "res://materials/ring_far_backdrop_cloud_composite.gdshader",
        "res://materials/ring_lightning_bolt.gdshader",
        "res://materials/ring_surface_far.gdshader",
        "res://materials/ring_emitter_grid_head.gdshader",
        "res://materials/ring_emitter_grid_head_far.gdshader",
        "res://materials/ring_emitter_grid_beam.gdshader",
        "res://materials/ring_emitter_grid_beam_core.gdshader",
        "res://materials/ring_emitter_grid_beam_far.gdshader",
        "res://materials/ring_grass_cards.gdshader",
        "res://materials/imported_tree.gdshader",
        "res://materials/imported_tree_impostor.gdshader",
        "res://materials/terrain3d_ring_surface.gdshader",
        "res://materials/ring_distant_rain_curtain.gdshader",
        "res://materials/ring_rain_haze.gdshader",
        "res://materials/ring_emitter_standin.gdshader",
        "res://materials/ring_poi_architecture.gdshader",
        "res://materials/ring_poi_architecture_double_sided.gdshader",
        "res://materials/ring_eclipse_hub_beam.gdshader",
        "res://materials/ring_eclipse_hub_beam_core.gdshader",
        "res://materials/ring_eclipse_hub_beam_far.gdshader",
        "res://materials/ring_eclipse_hub_body.gdshader",
        "res://materials/ring_wind_neutral.tres",
        "res://scripts/render/RingFarDepthCapture.gd"
    };
    private readonly Dictionary<string, Resource> _retained = new(StringComparer.Ordinal);
    private readonly RingThreadedResourceClaims _claims = new();
    private IEnumerator<bool>? _preparation;
    private string _phase = "resources";
    private string _error = string.Empty;
    private int _completedResources;
    private bool _cancelled;
    public double LastWorkMilliseconds { get; private set; }
    public double MaximumWorkMilliseconds { get; private set; }
    public bool HasCompleted { get; private set; }
    public bool IsComplete() => HasCompleted;
    public bool IsShutdownReady() => _claims.DrainCompleted() && RingStartupWarmup.IsComplete;
    public Godot.Collections.Dictionary GetLoadingStatus() => new()
    {
        ["ready"] = HasCompleted && _error.Length == 0 && !_cancelled,
        ["completed"] = HasCompleted,
        ["error"] = _error,
        ["cancelled"] = _cancelled,
        ["phase"] = _phase,
        ["completed_resources"] = _completedResources,
        ["total_resources"] = StartupPaths.Length,
        ["pending_requests"] = _claims.PendingCount,
        ["last_work_ms"] = LastWorkMilliseconds,
        ["maximum_work_ms"] = MaximumWorkMilliseconds
    };
    public override void _Ready() => BeginPreparation();
    public void BeginPreparation()
    {
        if (_preparation != null || HasCompleted) return;
        _preparation = Prepare().GetEnumerator();
    }
    public override void _Process(double delta)
    {
        if (_preparation == null)
        {
            if (_cancelled || _error.Length > 0) _claims.DrainCompleted();
            return;
        }
        long start = Stopwatch.GetTimestamp();
        try
        {
            for (int units = 0; units < 4; units++)
            {
                if (!_preparation.MoveNext())
                {
                    _preparation.Dispose(); _preparation = null;
                    HasCompleted = true;
                    break;
                }
                if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= 3) break;
            }
        }
        catch (Exception exception)
        {
            _error = exception.Message;
            _preparation?.Dispose(); _preparation = null;
            HasCompleted = true;
        }
        finally
        {
            LastWorkMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            MaximumWorkMilliseconds = Math.Max(MaximumWorkMilliseconds, LastWorkMilliseconds);
        }
    }
    public void CancelPreparation()
    {
        _cancelled = true;
        RingStartupWarmup.ReleaseShaders();
        _preparation?.Dispose(); _preparation = null;
        HasCompleted = true;
        _claims.DrainCompleted();
    }
    public override void _ExitTree()
    {
        _preparation?.Dispose(); _preparation = null;
        _claims.Dispose();
        _retained.Clear();
    }
    private IEnumerable<bool> Prepare()
    {
        foreach (string path in StartupPaths)
        {
            Error result = _claims.Request(path);
            if (result != Error.Ok) throw new InvalidOperationException($"Startup resource request failed for {path}: {result}.");
            yield return true;
        }
        foreach (string path in StartupPaths)
        {
            while (ResourceLoader.LoadThreadedGetStatus(path) == ResourceLoader.ThreadLoadStatus.InProgress) yield return true;
            if (ResourceLoader.LoadThreadedGetStatus(path) != ResourceLoader.ThreadLoadStatus.Loaded)
                throw new InvalidOperationException($"Startup resource load failed for {path}.");
            _retained[path] = _claims.Take(path)
                ?? throw new InvalidOperationException($"Startup resource result is missing for {path}.");
            _completedResources++;
            yield return true;
        }
        // Compile every retained shader and pre-JIT the game assembly on worker threads; attach follows only after both finish.
        _phase = "background warmup";
        var shaders = new List<Shader>();
        foreach (KeyValuePair<string, Resource> entry in _retained) if (entry.Value is Shader shader) shaders.Add(shader);
        RingStartupWarmup.Start(shaders);
        while (!RingStartupWarmup.IsComplete) { RingStartupWarmup.Flush(); yield return true; }
        RingStartupWarmup.Flush();
        if (RingTerrainVisuals.TexturePaletteReady) yield break;
        _phase = "terrain palette";
        Color[] colors = RingTerrainVisuals.CaptureLoadingPaletteFallbacks();
        for (int textureIndex = 0; textureIndex < PalettePaths.Length; textureIndex++)
        {
            using Image? image = (_retained[PalettePaths[textureIndex]] as Texture2D)?.GetImage();
            if (image == null || image.IsEmpty()) continue;
            // GetImage is a copy. Preserve the existing compressed-image guard.
            if (image.IsCompressed() && image.Decompress() != Error.Ok) continue;
            int width = image.GetWidth(), height = image.GetHeight();
            if (width <= 0 || height <= 0) continue;
            yield return true;
            const int samplesPerAxis = 128;
            Vector3 total = Vector3.Zero;
            int count = 0;
            for (int y = 0; y < samplesPerAxis; y++)
            {
                for (int x = 0; x < samplesPerAxis; x++)
                {
                    int pixelX = Mathf.Min(width - 1, (int)((x + 0.5f) * width / samplesPerAxis));
                    int pixelY = Mathf.Min(height - 1, (int)((y + 0.5f) * height / samplesPerAxis));
                    Color srgb = image.GetPixel(pixelX, pixelY);
                    total += new Vector3(SrgbToLinear(srgb.R), SrgbToLinear(srgb.G), SrgbToLinear(srgb.B));
                    count++;
                }
                yield return true;
            }
            if (count > 0)
            {
                total /= count;
                colors[textureIndex] = new Color(total.X, total.Y, total.Z, 1.0f);
            }
        }
        RingTerrainVisuals.PublishLoadingTexturePalette(colors);
    }
    private static float SrgbToLinear(float value) => value <= 0.04045f
        ? value / 12.92f : Mathf.Pow((value + 0.055f) / 1.055f, 2.4f);
}
