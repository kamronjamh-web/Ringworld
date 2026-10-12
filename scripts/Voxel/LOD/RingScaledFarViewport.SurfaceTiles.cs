using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingScaledFarViewport
{
    [ExportGroup("Far terrain surface tiles")]
    [Export] public bool FarSurfaceTilesEnabled { get; set; } = true;
    [Export(PropertyHint.Range, "32,256,32")] public int FarSurfaceTileResolution { get; set; } = 128;
    [Export(PropertyHint.Range, "1,512,1")] public int FarSurfaceTileCpuBudgetMb { get; set; } = 64;
    [Export(PropertyHint.Range, "1,512,1")] public int FarSurfaceTileGpuBudgetMb { get; set; } = 64;
    [Export(PropertyHint.Range, "1,4,1")] public int FarSurfaceTileMaximumJobs { get; set; } = 2;
    [Export(PropertyHint.Range, "1,8,1")] public int FarSurfaceTileUploadsPerFrame { get; set; } = 2;
    [Export(PropertyHint.Range, "0.25,8,0.25")] public float FarSurfaceTileUploadBudgetMilliseconds { get; set; } = 2f;

    // Identity includes the immutable procedural recipe hash, edits, geometry layout,
    // palette and sampling layout. Equivalent around-ring translations share a key.
    internal readonly record struct SurfaceTileKey(ulong Definition, int Seed, int Generator, ulong Edits,
        double Circumference, int Width, int Resolution, double Around, double Across,
        double AroundExtent, double AcrossExtent, Color Low, Color High, bool DisplayWater);
    private sealed record SurfaceTilePixels(byte[] Color, byte[] Normal, int Size)
    {
        public long Bytes => (long)Color.Length + Normal.Length;
    }
    private sealed class SurfaceTileEntry
    {
        public SurfaceTilePixels? Pixels;
        public ImageTexture? Color;
        public ImageTexture? Normal;
        public long LastUsed;
        public long GpuBytes;
        public ulong UploadedAtMilliseconds;
    }
    private sealed record SurfaceTileBinding(ShaderMaterial Template, ShaderMaterial Material,
        double Around, double Across, double AroundExtent, double AcrossExtent, bool DisplayWater)
    {
        public SurfaceTileKey Key;
    }
    private sealed record SurfaceTileJob(SurfaceTileKey Key, CancellationTokenSource Cancellation, Task<SurfaceTilePixels> Task);
    private readonly List<SurfaceTileBinding> _surfaceTileBindings = new();
    private readonly Dictionary<SurfaceTileKey, SurfaceTileEntry> _surfaceTileCache = new();
    private readonly List<SurfaceTileJob> _surfaceTileJobs = new();
    private readonly Dictionary<ShaderMaterial, Dictionary<string, Variant>> _surfaceTileTemplateValues = new();
    private readonly Dictionary<Shader, string[]> _surfaceTileUniformNames = new();
    private long _surfaceTileClock;
    private bool _surfaceTilesDisposed;
    private (ulong Definition, ulong Edits, int Resolution, Color Low, Color High)? _surfaceTileIdentity;
    public int SurfaceTileResidentCount => _surfaceTileCache.Values.Count(v => v.Color != null);
    public int SurfaceTilePendingCount => _surfaceTileJobs.Count;
    public long SurfaceTileCpuBytes => _surfaceTileCache.Values.Sum(v => v.Pixels?.Bytes ?? 0)
        + _surfaceTileJobs.Sum(job => (long)(job.Key.Resolution + 2) * (job.Key.Resolution + 2) * 8);
    public long SurfaceTileGpuBytes => _surfaceTileCache.Values.Sum(v => v.GpuBytes);

    private ShaderMaterial BindFarSurfaceMaterial(ShaderMaterial template,
        double aroundStartMeters, double acrossStartMeters, double aroundExtentMeters, double acrossExtentMeters,
        bool duplicate = true, bool displayWater = false)
    {
        var material = duplicate ? (ShaderMaterial)template.Duplicate() : template;
        material.SetShaderParameter("surface_tile_enabled", false);
        _surfaceTileBindings.Add(new(template, material, aroundStartMeters, acrossStartMeters,
            Math.Max(0.001, aroundExtentMeters), Math.Max(0.001, acrossExtentMeters), displayWater));
        return material;
    }

    private void ReleaseFarSurfaceMaterial(ShaderMaterial material)
    {
        _surfaceTileBindings.RemoveAll(binding => binding.Material == material);
        _surfaceTileTemplateValues.Clear();
        material.SetShaderParameter("surface_tile_enabled", false);
    }

    internal static double WrapSurfaceTileAround(double around, double circumference)
    {
        double result = around % circumference;
        return result < 0 ? result + circumference : result;
    }

    internal static SurfaceTileKey MakeSurfaceTileKey(RingTerrainSampler sampler, ulong edits, int resolution,
        double circumference, int width, double around, double across, double aroundExtent, double acrossExtent,
        Color low, Color high, bool displayWater = false)
        => new(sampler.TerrainDefinitionHash, sampler.Seed, sampler.TerrainGeneratorVersion, edits,
            circumference, width, resolution, WrapSurfaceTileAround(around, circumference), across,
            aroundExtent, acrossExtent, low, high, displayWater);

    private void UpdateFarSurfaceTiles()
    {
        if (_surfaceTilesDisposed || _grid?.TerrainSampler is not RingTerrainSampler sampler) return;
        _surfaceTileClock++;
        // Duplicated sector materials must follow live lighting, weather, atmosphere
        // and regional handoff uniforms on their template. Tile parameters are private.
        foreach (var group in _surfaceTileBindings.Where(binding => binding.Template != binding.Material).GroupBy(binding => binding.Template))
        {
            if (!_surfaceTileTemplateValues.TryGetValue(group.Key, out var previous))
                _surfaceTileTemplateValues[group.Key] = previous = new();
            if (!_surfaceTileUniformNames.TryGetValue(group.Key.Shader, out string[]? uniforms))
                _surfaceTileUniformNames[group.Key.Shader] = uniforms = group.Key.Shader.GetShaderUniformList()
                    .Select(uniform => uniform.AsGodotDictionary()["name"].AsString())
                    .Where(name => !name.StartsWith("surface_tile_", StringComparison.Ordinal)).ToArray();
            foreach (string name in uniforms)
            {
                Variant value = group.Key.GetShaderParameter(name);
                if (previous.TryGetValue(name, out Variant old) && old.Equals(value)) continue;
                previous[name] = value;
                foreach (var binding in group) binding.Material.SetShaderParameter(name, value);
            }
        }
        int resolution = Math.Clamp(FarSurfaceTileResolution, 32, 256);
        var identity = (sampler.TerrainDefinitionHash, _grid.LodSurfaceEditVersion, resolution, LowTerrainColor, HighTerrainColor);
        if (_surfaceTileIdentity != identity || !FarSurfaceTilesEnabled)
        {
            foreach (var job in _surfaceTileJobs) job.Cancellation.Cancel();
            ClearSurfaceTileCache();
            _surfaceTileIdentity = identity;
        }
        foreach (var job in _surfaceTileJobs.ToArray())
        {
            if (!job.Task.IsCompleted) continue;
            _surfaceTileJobs.Remove(job);
            if (job.Task.IsCompletedSuccessfully && !job.Cancellation.IsCancellationRequested && FarSurfaceTilesEnabled)
                _surfaceTileCache[job.Key] = new() { Pixels = job.Task.Result, LastUsed = _surfaceTileClock };
            else if (job.Task.IsFaulted)
            {
                Exception? failure = job.Task.Exception?.GetBaseException();
                if (failure is not OperationCanceledException || !job.Cancellation.IsCancellationRequested)
                    GD.PushWarning($"Far surface tile generation failed: {failure?.Message}");
            }
            job.Cancellation.Dispose();
        }
        if (!FarSurfaceTilesEnabled) return;
        foreach (var binding in _surfaceTileBindings)
            binding.Key = MakeSurfaceTileKey(sampler, _grid.LodSurfaceEditVersion, resolution,
                _circumferenceMeters, RingWidthMeters, binding.Around, binding.Across,
                binding.AroundExtent, binding.AcrossExtent, LowTerrainColor, HighTerrainColor, binding.DisplayWater);

        // Closest sector first makes fast travel useful before distant tiles finish.
        double cameraAround = _explorer?.CanonicalPosition.AroundMeters ?? 0;
        double cameraAcross = _explorer?.CanonicalPosition.AcrossMeters ?? 0;
        var ordered = _surfaceTileBindings.OrderBy(binding => SurfaceTilePriority(binding.Key, cameraAround, cameraAcross)).ToArray();
        long tileCpuBytes = (long)(resolution + 2) * (resolution + 2) * 8;
        long tileGpuBytes = tileCpuBytes * 4 / 3 + 64;
        int capacity = (int)Math.Max(1, Math.Min(Math.Clamp(FarSurfaceTileCpuBudgetMb, 1, 512) * 1024L * 1024L / tileCpuBytes,
            Math.Clamp(FarSurfaceTileGpuBudgetMb, 1, 512) * 1024L * 1024L / tileGpuBytes));
        // A bounded desired set prevents cycling through more registered sectors
        // than the budgets can hold. Low priority sectors retain vertex fallback.
        var wanted = ordered.Select(binding => binding.Key).Distinct().Take(capacity).ToHashSet();
        foreach (var job in _surfaceTileJobs)
            if (!wanted.Contains(job.Key)) job.Cancellation.Cancel();
        foreach (var binding in ordered)
            if (!wanted.Contains(binding.Key)) binding.Material.SetShaderParameter("surface_tile_enabled", false);
        foreach (var pair in _surfaceTileCache.ToArray())
            if (!wanted.Contains(pair.Key)) RemoveSurfaceTile(pair.Key, pair.Value);
        var timer = Stopwatch.StartNew();
        int uploads = 0;
        foreach (var binding in ordered)
        {
            if (!wanted.Contains(binding.Key)) continue;
            if (_surfaceTileCache.TryGetValue(binding.Key, out var entry))
            {
                entry.LastUsed = _surfaceTileClock;
                if (entry.Color == null && entry.Pixels != null && uploads < Math.Clamp(FarSurfaceTileUploadsPerFrame, 1, 8)
                    && timer.Elapsed.TotalMilliseconds < Math.Max(0.25f, FarSurfaceTileUploadBudgetMilliseconds))
                {
                    long gpuBytes = entry.Pixels.Bytes * 4 / 3 + 64; // full RGBA mip chains
                    if (MakeSurfaceTileRoom(0, gpuBytes, binding.Key))
                    {
                        entry.Color = UploadSurfaceTile(entry.Pixels.Color, entry.Pixels.Size);
                        entry.Normal = UploadSurfaceTile(entry.Pixels.Normal, entry.Pixels.Size);
                        entry.GpuBytes = gpuBytes;
                        entry.UploadedAtMilliseconds = Time.GetTicksMsec();
                        uploads++;
                    }
                }
                ApplySurfaceTile(binding, entry);
                continue;
            }
            binding.Material.SetShaderParameter("surface_tile_enabled", false);
            if (_surfaceTileJobs.Count >= Math.Clamp(FarSurfaceTileMaximumJobs, 1, 4)
                || _surfaceTileJobs.Any(job => job.Key == binding.Key)) continue;
            long bytes = (long)(resolution + 2) * (resolution + 2) * 8;
            if (!MakeSurfaceTileRoom(bytes, 0, binding.Key)) continue;
            // Warm the terrain palette on the main thread before any worker can
            // trigger its texture-reading static initializer.
            RingTerrainVisuals.PreloadTexturePalette();
            var worker = sampler.CreateIndependentCopy();
            var edits = _grid.CaptureLodSurfaceEditSnapshot();
            var cancellation = new CancellationTokenSource();
            SurfaceTileKey key = binding.Key;
            _surfaceTileJobs.Add(new(key, cancellation, Task.Run(() =>
            {
                try { return BuildSurfaceTilePixels(key, worker, edits, cancellation.Token); }
                finally { worker.Dispose(); }
            })));
        }
        MakeSurfaceTileRoom(0, 0, default);
    }

    private static double SurfaceTileDistance(SurfaceTileKey key, double around, double across)
    {
        double delta = WrapSurfaceTileAround(key.Around + key.AroundExtent * .5 - around, key.Circumference);
        delta = Math.Min(delta, key.Circumference - delta);
        return delta * delta + Math.Pow(key.Across + key.AcrossExtent * .5 - across, 2);
    }

    private double SurfaceTilePriority(SurfaceTileKey key, double around, double across)
    {
        double distance = SurfaceTileDistance(key, around, across);
        if (_farCamera == null) return distance;
        Vector3 center = ScaledPoint(key.Around + key.AroundExtent * .5, 0, key.Across + key.AcrossExtent * .5);
        Vector3 relative = center - _farCamera.GlobalPosition;
        float radius = (float)(Math.Sqrt(key.AroundExtent * key.AroundExtent + key.AcrossExtent * key.AcrossExtent) * .5 * CoordinateScale);
        bool inFront = relative.Dot(-_farCamera.GlobalBasis.Z) >= -radius;
        double area = Math.Max(1, key.AroundExtent * key.AcrossExtent);
        return distance / area + (inFront ? 0 : 1e20);
    }

    private static ImageTexture UploadSurfaceTile(byte[] bytes, int size)
    {
        using Image image = Image.CreateFromData(size, size, false, Image.Format.Rgba8, bytes);
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    private void ApplySurfaceTile(SurfaceTileBinding binding, SurfaceTileEntry entry)
    {
        bool ready = entry.Color != null && entry.Normal != null;
        binding.Material.SetShaderParameter("surface_tile_enabled", ready);
        if (!ready) return;
        binding.Material.SetShaderParameter("surface_tile_color", entry.Color!);
        binding.Material.SetShaderParameter("surface_tile_normal", entry.Normal!);
        binding.Material.SetShaderParameter("surface_tile_resolution", binding.Key.Resolution);
        binding.Material.SetShaderParameter("surface_tile_blend", Math.Clamp((Time.GetTicksMsec() - entry.UploadedAtMilliseconds) / 200f, 0, 1));
        binding.Material.SetShaderParameter("surface_tile_angle_start", (float)(binding.Key.Around / binding.Key.Circumference * Math.Tau));
        binding.Material.SetShaderParameter("surface_tile_angle_span", (float)(binding.Key.AroundExtent / binding.Key.Circumference * Math.Tau));
    }

    private bool MakeSurfaceTileRoom(long cpuAddition, long gpuAddition, SurfaceTileKey keep)
    {
        long cpuLimit = Math.Clamp(FarSurfaceTileCpuBudgetMb, 1, 512) * 1024L * 1024L;
        long gpuLimit = Math.Clamp(FarSurfaceTileGpuBudgetMb, 1, 512) * 1024L * 1024L;
        foreach (var pair in _surfaceTileCache.OrderBy(pair => pair.Value.LastUsed).ToArray())
        {
            if (SurfaceTileCpuBytes + cpuAddition <= cpuLimit && SurfaceTileGpuBytes + gpuAddition <= gpuLimit) return true;
            if (pair.Key == keep) continue;
            RemoveSurfaceTile(pair.Key, pair.Value);
        }
        return SurfaceTileCpuBytes + cpuAddition <= cpuLimit && SurfaceTileGpuBytes + gpuAddition <= gpuLimit;
    }

    private void RemoveSurfaceTile(SurfaceTileKey key, SurfaceTileEntry entry)
    {
        foreach (var binding in _surfaceTileBindings.Where(binding => binding.Key == key))
        {
            binding.Material.SetShaderParameter("surface_tile_enabled", false);
            binding.Material.SetShaderParameter("surface_tile_color", default(Variant));
            binding.Material.SetShaderParameter("surface_tile_normal", default(Variant));
        }
        entry.Color?.Dispose();
        entry.Normal?.Dispose();
        _surfaceTileCache.Remove(key);
    }

    private void ClearSurfaceTileCache()
    {
        foreach (var binding in _surfaceTileBindings) binding.Material.SetShaderParameter("surface_tile_enabled", false);
        foreach (var pair in _surfaceTileCache.ToArray()) RemoveSurfaceTile(pair.Key, pair.Value);
    }

    private void DisposeFarSurfaceTiles()
    {
        _surfaceTilesDisposed = true;
        foreach (var job in _surfaceTileJobs)
        {
            job.Cancellation.Cancel();
            // Observe failures and release token after immutable work stops, without
            // blocking Godot's scene teardown on expensive terrain sampling.
            _ = job.Task.ContinueWith(task => { _ = task.Exception; job.Cancellation.Dispose(); }, TaskScheduler.Default);
        }
        _surfaceTileJobs.Clear();
        ClearSurfaceTileCache();
        _surfaceTileBindings.Clear();
        _surfaceTileTemplateValues.Clear();
        _surfaceTileUniformNames.Clear();
    }

    private static SurfaceTilePixels BuildSurfaceTilePixels(SurfaceTileKey key, RingTerrainSampler sampler,
        RingLodSurfaceEditSnapshot edits, CancellationToken token)
    {
        int size = key.Resolution + 2;
        byte[] colors = new byte[size * size * 4], normals = new byte[colors.Length];
        // Endpoint samples plus an extrapolated border preserve bilinear seams.
        // A fixed derivative step makes shared-edge normals independent of tile LOD.
        const double derivative = 64;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            token.ThrowIfCancellationRequested();
            double around = key.Around + (x - 1.0) / (key.Resolution - 1) * key.AroundExtent;
            double across = Math.Clamp(key.Across + (y - 1.0) / (key.Resolution - 1) * key.AcrossExtent, 0, key.Width);
            long canonicalAround = (long)Math.Round(WrapSurfaceTileAround(around, key.Circumference));
            int canonicalAcross = (int)Math.Round(across);
            TerrainSample sample = edits.GetPresentationTerrainSample(sampler, canonicalAround, canonicalAcross);
            float height = ResolveFarTerrainHeight(sampler, edits, canonicalAround, canonicalAcross);
            if (key.DisplayWater) height = sample.ResolveDisplayedHeight(height);
            Color color = RingTerrainVisuals.ResolveMacroTextureColor(sample, Mathf.RoundToInt(height), sampler, key.Low, key.High);
            float Height(double a, double z)
            {
                long ca = (long)Math.Round(WrapSurfaceTileAround(a, key.Circumference));
                int cz = (int)Math.Round(Math.Clamp(z, 0, key.Width));
                float terrainHeight = ResolveFarTerrainHeight(sampler, edits, ca, cz);
                return key.DisplayWater ? edits.GetPresentationTerrainSample(sampler, ca, cz).ResolveDisplayedHeight(terrainHeight) : terrainHeight;
            }
            double left = Math.Max(0, across - derivative), right = Math.Min(key.Width, across + derivative);
            float da = (Height(around + derivative, across) - Height(around - derivative, across)) / (float)(derivative * 2);
            float dz = (Height(around, right) - Height(around, left)) / (float)Math.Max(1, right - left);
            // Canonical tangent (-sin,0,cos), inward up (-cos,0,-sin), across +Y.
            // Correct around derivative for the actual curved surface arc radius.
            float arcScale = (float)Math.Max(.001, 1.0 - height / (key.Circumference / Math.Tau));
            Vector3 normal = new Vector3(-da / arcScale, 1, -dz).Normalized();
            int index = (y * size + x) * 4;
            colors[index] = PackSurfaceTileByte(color.R); colors[index + 1] = PackSurfaceTileByte(color.G);
            colors[index + 2] = PackSurfaceTileByte(color.B); colors[index + 3] = 255;
            normals[index] = PackSurfaceTileByte(normal.X * .5f + .5f);
            normals[index + 1] = PackSurfaceTileByte(normal.Y * .5f + .5f);
            normals[index + 2] = PackSurfaceTileByte(normal.Z * .5f + .5f); normals[index + 3] = 255;
        }
        return new(colors, normals, size);
    }
    private static byte PackSurfaceTileByte(float value) => (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);
}
