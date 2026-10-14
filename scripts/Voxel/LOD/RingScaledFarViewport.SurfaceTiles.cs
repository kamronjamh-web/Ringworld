using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Diagnostics;
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
    // Last values written to the bound material. Godot queues a material update for every
    // set_shader_parameter call, so unchanged state must not be rewritten each frame.
    // A class, so binding record equality is unaffected. NaN/-1 never equal a real value.
    private sealed class SurfaceTileApplied
    {
        public bool Has;
        public bool Enabled;
        public Texture2D? Color, Normal;
        public int Resolution = -1;
        public float Blend = float.NaN, AngleStart = float.NaN, AngleSpan = float.NaN;
    }
    private sealed record SurfaceTileBinding(ShaderMaterial Template, ShaderMaterial Material,
        double Around, double Across, double AroundExtent, double AcrossExtent, bool DisplayWater)
    {
        public SurfaceTileKey Key;
        public SurfaceTileApplied Applied { get; } = new();
    }
    private sealed record SurfaceTileJob(SurfaceTileKey Key, CancellationTokenSource Cancellation, Task<SurfaceTilePixels> Task);
    private readonly List<SurfaceTileBinding> _surfaceTileBindings = new();
    private readonly Dictionary<SurfaceTileKey, SurfaceTileEntry> _surfaceTileCache = new();
    private readonly List<SurfaceTileJob> _surfaceTileJobs = new();
    private readonly Dictionary<ShaderMaterial, Dictionary<string, Variant>> _surfaceTileTemplateValues = new();
    private readonly Dictionary<Shader, string[]> _surfaceTileUniformNames = new();
    // Template -> duplicated bindings, rebuilt only when bindings are added or released.
    private readonly List<(ShaderMaterial Template, List<SurfaceTileBinding> Bindings)> _surfaceTileTemplateGroups = new();
    private bool _surfaceTileGroupsDirty = true;
    private readonly HashSet<SurfaceTileKey> _surfaceTileWanted = new();
    private readonly List<SurfaceTileBinding> _surfaceTileOrdered = new();
    private readonly List<KeyValuePair<SurfaceTileKey, SurfaceTileEntry>> _surfaceTileUnwanted = new();
    private (double Priority, int Index)[] _surfaceTileOrderBuffer = new (double, int)[64];
    private static readonly Comparer<(double Priority, int Index)> SurfaceTileOrderComparer = Comparer<(double Priority, int Index)>.Create(static (a, b) =>
    {
        int result = a.Priority.CompareTo(b.Priority);
        return result != 0 ? result : a.Index.CompareTo(b.Index);
    });
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
        RingMaterialWriteCounters.Record(RingMaterialWriteCounters.Site.FarSurfaceTileApply);
        var binding = new SurfaceTileBinding(template, material, aroundStartMeters, acrossStartMeters,
            Math.Max(0.001, aroundExtentMeters), Math.Max(0.001, acrossExtentMeters), displayWater);
        binding.Applied.Has = true;
        binding.Applied.Enabled = false;
        _surfaceTileBindings.Add(binding);
        _surfaceTileGroupsDirty = true;
        return material;
    }

    private void ReleaseFarSurfaceMaterial(ShaderMaterial material)
    {
        foreach (var binding in _surfaceTileBindings)
        {
            if (binding.Material != material) continue;
            binding.Applied.Has = true;
            binding.Applied.Enabled = false;
        }
        _surfaceTileBindings.RemoveAll(binding => binding.Material == material);
        _surfaceTileGroupsDirty = true;
        _surfaceTileTemplateValues.Clear();
        material.SetShaderParameter("surface_tile_enabled", false);
        RingMaterialWriteCounters.Record(RingMaterialWriteCounters.Site.FarSurfaceTileApply);
    }

    // Godot.Variant does not override Equals (boxing ValueType.Equals; Objects never match).
    private static bool SameVariant(in Variant a, in Variant b)
    {
        Variant.Type type = a.VariantType;
        if (type != b.VariantType) return false;
        return type switch
        {
            Variant.Type.Nil => true,
            Variant.Type.Bool => a.AsBool() == b.AsBool(),
            Variant.Type.Int => a.AsInt64() == b.AsInt64(),
            Variant.Type.Float => a.AsDouble() == b.AsDouble(),
            Variant.Type.String => a.AsString() == b.AsString(),
            Variant.Type.Vector2 => a.AsVector2() == b.AsVector2(),
            Variant.Type.Vector2I => a.AsVector2I() == b.AsVector2I(),
            Variant.Type.Rect2 => a.AsRect2() == b.AsRect2(),
            Variant.Type.Rect2I => a.AsRect2I() == b.AsRect2I(),
            Variant.Type.Vector3 => a.AsVector3() == b.AsVector3(),
            Variant.Type.Vector3I => a.AsVector3I() == b.AsVector3I(),
            Variant.Type.Transform2D => a.AsTransform2D() == b.AsTransform2D(),
            Variant.Type.Vector4 => a.AsVector4() == b.AsVector4(),
            Variant.Type.Vector4I => a.AsVector4I() == b.AsVector4I(),
            Variant.Type.Plane => a.AsPlane() == b.AsPlane(),
            Variant.Type.Quaternion => a.AsQuaternion() == b.AsQuaternion(),
            Variant.Type.Aabb => a.AsAabb() == b.AsAabb(),
            Variant.Type.Basis => a.AsBasis() == b.AsBasis(),
            Variant.Type.Transform3D => a.AsTransform3D() == b.AsTransform3D(),
            Variant.Type.Projection => a.AsProjection() == b.AsProjection(),
            Variant.Type.Color => a.AsColor() == b.AsColor(),
            Variant.Type.StringName => a.AsStringName() == b.AsStringName(),
            Variant.Type.NodePath => a.AsNodePath() == b.AsNodePath(),
            Variant.Type.Rid => a.AsRid() == b.AsRid(),
            Variant.Type.Object => ReferenceEquals(a.AsGodotObject(), b.AsGodotObject()),
            _ => false
        };
    }

    private void RebuildSurfaceTileTemplateGroups()
    {
        _surfaceTileTemplateGroups.Clear();
        foreach (var binding in _surfaceTileBindings)
        {
            if (binding.Template == binding.Material) continue;
            List<SurfaceTileBinding>? group = null;
            foreach (var existing in _surfaceTileTemplateGroups)
                if (existing.Template == binding.Template) { group = existing.Bindings; break; }
            if (group == null) _surfaceTileTemplateGroups.Add((binding.Template, group = new()));
            group.Add(binding);
        }
        _surfaceTileGroupsDirty = false;
    }

    private static void SetTileEnabled(SurfaceTileBinding binding, bool enabled)
    {
        SurfaceTileApplied applied = binding.Applied;
        if (applied.Has && applied.Enabled == enabled) return;
        binding.Material.SetShaderParameter("surface_tile_enabled", enabled);
        applied.Has = true;
        applied.Enabled = enabled;
        RingMaterialWriteCounters.Record(RingMaterialWriteCounters.Site.FarSurfaceTileApply);
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
        if (_surfaceTileGroupsDirty) RebuildSurfaceTileTemplateGroups();
        foreach (var (template, group) in _surfaceTileTemplateGroups)
        {
            if (!_surfaceTileTemplateValues.TryGetValue(template, out var previous))
                _surfaceTileTemplateValues[template] = previous = new();
            if (!_surfaceTileUniformNames.TryGetValue(template.Shader, out string[]? uniforms))
                _surfaceTileUniformNames[template.Shader] = uniforms = template.Shader.GetShaderUniformList()
                    .Select(uniform => uniform.AsGodotDictionary()["name"].AsString())
                    .Where(name => !name.StartsWith("surface_tile_", StringComparison.Ordinal)).ToArray();
            foreach (string name in uniforms)
            {
                Variant value = template.GetShaderParameter(name);
                if (previous.TryGetValue(name, out Variant old) && SameVariant(old, value)) continue;
                previous[name] = value;
                foreach (var binding in group) binding.Material.SetShaderParameter(name, value);
                RingMaterialWriteCounters.Record(RingMaterialWriteCounters.Site.FarSurfaceTileTemplateSync, group.Count,
                    value.VariantType == Variant.Type.Object ? group.Count : 0);
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
        for (int jobIndex = 0; jobIndex < _surfaceTileJobs.Count;)
        {
            var job = _surfaceTileJobs[jobIndex];
            if (!job.Task.IsCompleted) { jobIndex++; continue; }
            _surfaceTileJobs.RemoveAt(jobIndex);
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
        // Stable priority order (ties keep binding order) without per-frame LINQ allocations.
        int bindingCount = _surfaceTileBindings.Count;
        if (_surfaceTileOrderBuffer.Length < bindingCount) Array.Resize(ref _surfaceTileOrderBuffer, Math.Max(bindingCount, _surfaceTileOrderBuffer.Length * 2));
        for (int i = 0; i < bindingCount; i++)
            _surfaceTileOrderBuffer[i] = (SurfaceTilePriority(_surfaceTileBindings[i].Key, cameraAround, cameraAcross), i);
        Array.Sort(_surfaceTileOrderBuffer, 0, bindingCount, SurfaceTileOrderComparer);
        var ordered = _surfaceTileOrdered;
        ordered.Clear();
        for (int i = 0; i < bindingCount; i++) ordered.Add(_surfaceTileBindings[_surfaceTileOrderBuffer[i].Index]);
        long tileCpuBytes = (long)(resolution + 2) * (resolution + 2) * 8;
        long tileGpuBytes = tileCpuBytes * 4 / 3 + 64;
        int capacity = (int)Math.Max(1, Math.Min(Math.Clamp(FarSurfaceTileCpuBudgetMb, 1, 512) * 1024L * 1024L / tileCpuBytes,
            Math.Clamp(FarSurfaceTileGpuBudgetMb, 1, 512) * 1024L * 1024L / tileGpuBytes));
        // A bounded desired set prevents cycling through more registered sectors
        // than the budgets can hold. Low priority sectors retain vertex fallback.
        var wanted = _surfaceTileWanted;
        wanted.Clear();
        foreach (var binding in ordered)
        {
            if (wanted.Count >= capacity) break;
            wanted.Add(binding.Key);
        }
        foreach (var job in _surfaceTileJobs)
            if (!wanted.Contains(job.Key)) job.Cancellation.Cancel();
        foreach (var binding in ordered)
            if (!wanted.Contains(binding.Key)) SetTileEnabled(binding, false);
        _surfaceTileUnwanted.Clear();
        foreach (var pair in _surfaceTileCache)
            if (!wanted.Contains(pair.Key)) _surfaceTileUnwanted.Add(pair);
        foreach (var pair in _surfaceTileUnwanted) RemoveSurfaceTile(pair.Key, pair.Value);
        _surfaceTileUnwanted.Clear();
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
            SetTileEnabled(binding, false);
            if (_surfaceTileJobs.Count >= Math.Clamp(FarSurfaceTileMaximumJobs, 1, 4)
                || HasSurfaceTileJob(binding.Key)) continue;
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

    private bool HasSurfaceTileJob(SurfaceTileKey key)
    {
        foreach (var job in _surfaceTileJobs)
            if (job.Key == key) return true;
        return false;
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
        SetTileEnabled(binding, ready);
        if (!ready) return;
        ShaderMaterial material = binding.Material;
        SurfaceTileApplied applied = binding.Applied;
        int writes = 0, textureWrites = 0;
        if (!ReferenceEquals(applied.Color, entry.Color))
        {
            material.SetShaderParameter("surface_tile_color", entry.Color!);
            applied.Color = entry.Color;
            writes++; textureWrites++;
        }
        if (!ReferenceEquals(applied.Normal, entry.Normal))
        {
            material.SetShaderParameter("surface_tile_normal", entry.Normal!);
            applied.Normal = entry.Normal;
            writes++; textureWrites++;
        }
        int resolution = binding.Key.Resolution;
        if (applied.Resolution != resolution)
        {
            material.SetShaderParameter("surface_tile_resolution", resolution);
            applied.Resolution = resolution;
            writes++;
        }
        float blend = Math.Clamp((Time.GetTicksMsec() - entry.UploadedAtMilliseconds) / 200f, 0, 1);
        if (applied.Blend != blend)
        {
            material.SetShaderParameter("surface_tile_blend", blend);
            applied.Blend = blend;
            writes++;
        }
        float angleStart = (float)(binding.Key.Around / binding.Key.Circumference * Math.Tau);
        if (applied.AngleStart != angleStart)
        {
            material.SetShaderParameter("surface_tile_angle_start", angleStart);
            applied.AngleStart = angleStart;
            writes++;
        }
        float angleSpan = (float)(binding.Key.AroundExtent / binding.Key.Circumference * Math.Tau);
        if (applied.AngleSpan != angleSpan)
        {
            material.SetShaderParameter("surface_tile_angle_span", angleSpan);
            applied.AngleSpan = angleSpan;
            writes++;
        }
        if (writes > 0) RingMaterialWriteCounters.Record(RingMaterialWriteCounters.Site.FarSurfaceTileApply, writes, textureWrites);
    }

    private bool MakeSurfaceTileRoom(long cpuAddition, long gpuAddition, SurfaceTileKey keep)
    {
        long cpuLimit = Math.Clamp(FarSurfaceTileCpuBudgetMb, 1, 512) * 1024L * 1024L;
        long gpuLimit = Math.Clamp(FarSurfaceTileGpuBudgetMb, 1, 512) * 1024L * 1024L;
        long cpuBytes = SurfaceTileCpuBytes, gpuBytes = SurfaceTileGpuBytes;
        if (cpuBytes + cpuAddition <= cpuLimit && gpuBytes + gpuAddition <= gpuLimit) return true;
        foreach (var pair in _surfaceTileCache.OrderBy(pair => pair.Value.LastUsed).ToArray())
        {
            if (cpuBytes + cpuAddition <= cpuLimit && gpuBytes + gpuAddition <= gpuLimit) return true;
            if (pair.Key == keep) continue;
            RemoveSurfaceTile(pair.Key, pair.Value);
            cpuBytes -= pair.Value.Pixels?.Bytes ?? 0;
            gpuBytes -= pair.Value.GpuBytes;
        }
        return cpuBytes + cpuAddition <= cpuLimit && gpuBytes + gpuAddition <= gpuLimit;
    }

    private void RemoveSurfaceTile(SurfaceTileKey key, SurfaceTileEntry entry)
    {
        foreach (var binding in _surfaceTileBindings.Where(binding => binding.Key == key))
        {
            binding.Material.SetShaderParameter("surface_tile_enabled", false);
            binding.Material.SetShaderParameter("surface_tile_color", default(Variant));
            binding.Material.SetShaderParameter("surface_tile_normal", default(Variant));
            SurfaceTileApplied applied = binding.Applied;
            applied.Has = true;
            applied.Enabled = false;
            applied.Color = null;
            applied.Normal = null;
            RingMaterialWriteCounters.Record(RingMaterialWriteCounters.Site.FarSurfaceTileApply, 3, 2);
        }
        entry.Color?.Dispose();
        entry.Normal?.Dispose();
        _surfaceTileCache.Remove(key);
    }

    private void ClearSurfaceTileCache()
    {
        foreach (var binding in _surfaceTileBindings) SetTileEnabled(binding, false);
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
        _surfaceTileGroupsDirty = true;
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
