using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Diagnostic-only hybrid generation experiment. It never installs a result:
/// CPU retains jitter, hashes, biome/water/exclusion policy, selection and
/// packing. The local RD device only resolves the already-collected surface
/// queries, including synchronous readback in its reported wall time.
/// </summary>
internal static class RingGrassGpuGenerationPrototype
{
    internal readonly record struct Input(RingGrassTileKey Key, RingGrassTileBounds Bounds, float CellSize,
        int Candidates, float Density, int MaximumInstances, float MaximumSlope, RingTerrainSampler Sampler,
        RingLodSurfaceEditSnapshot Edits, RingLodGeometrySnapshot Geometry,
        SmoothTerrainVolumetricDensitySnapshot Field, RingGrassExclusionSnapshot? Exclusions,
        RingGrassAppearanceSnapshot Appearance);
    internal sealed record TileReport(bool Supported, bool Parity, string Status, int Queries, int CacheBytes,
        double CpuBaselineMilliseconds, double CpuWarmBaselineMilliseconds, double CollectionMilliseconds, double CacheMilliseconds, double CachedCpuMilliseconds,
        double InitializationMilliseconds, double UploadAndComputeMilliseconds, double ReadbackMilliseconds, double ReplayMilliseconds,
        int CpuInstances, int ReplayInstances, float MaximumPositionError, float MaximumBasisError)
    {
        public ulong CpuPlacementHash { get; init; }
        public ulong GpuPlacementHash { get; init; }
        public int UniqueColumns { get; init; }
        public double CachedCpuReplayMilliseconds { get; init; }
        public double GpuResolveTotalMilliseconds { get; init; }
        public double HybridMilliseconds => CollectionMilliseconds + CacheMilliseconds + GpuResolveTotalMilliseconds + ReplayMilliseconds;
        public double CachedCpuHybridMilliseconds => CollectionMilliseconds + CacheMilliseconds + CachedCpuMilliseconds + CachedCpuReplayMilliseconds;
    }

    internal sealed class Session : IDisposable
    {
        private readonly RenderingDevice _rd;
        private Rid _shader, _pipeline;
        private bool _disposed;
        public double InitializationMilliseconds { get; }
        public double TeardownMilliseconds { get; private set; }
        public Session()
        {
            Stopwatch watch = Stopwatch.StartNew();
            _rd = RenderingServer.CreateLocalRenderingDevice() ?? throw new NotSupportedException("Local RenderingDevice unavailable.");
            try
            {
                const string path = "res://materials/vegetation_gpu/ring_gpu_grass_generation.glsl";
                using RDShaderSpirV spirv = LoadShader(_rd, path);
                if (!string.IsNullOrWhiteSpace(spirv.CompileErrorCompute)) throw new InvalidOperationException(spirv.CompileErrorCompute);
                _shader = _rd.ShaderCreateFromSpirV(spirv);
                if (!_shader.IsValid) throw new InvalidOperationException("GPU generation shader creation failed.");
                _pipeline = _rd.ComputePipelineCreate(_shader);
                if (!_pipeline.IsValid) throw new InvalidOperationException("GPU generation pipeline creation failed.");
                InitializationMilliseconds = watch.Elapsed.TotalMilliseconds;
            }
            catch { Dispose(); throw; }
        }
        public (RingGrassSurfaceHit?[] Hits, double UploadAndCompute, double Readback) Resolve(RingGrassGpuQueryCache cache, CancellationToken cancellation)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ResolveWithDevice(_rd, _shader, _pipeline, cache, cancellation);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stopwatch watch = Stopwatch.StartNew();
            Free(_rd, ref _pipeline); Free(_rd, ref _shader); _rd.Free();
            TeardownMilliseconds = watch.Elapsed.TotalMilliseconds;
        }
    }

    private static RDShaderSpirV LoadShader(RenderingDevice rd, string path)
    {
        if (!FileAccess.FileExists(path))
        {
            using RDShaderFile file = GD.Load<RDShaderFile>(path) ?? throw new NotSupportedException("GPU generation shader unavailable.");
            return file.GetSpirV();
        }
        using var source = new RDShaderSource { SourceCompute = FileAccess.GetFileAsString(path).Replace("#[compute]", "", StringComparison.Ordinal) };
        return rd.ShaderCompileSpirVFromSource(source);
    }

    internal static TileReport Run(Session session, in Input input, CancellationToken cancellation = default)
    {
        var queries = new List<(double Around, double Across)>();
        Stopwatch timer = Stopwatch.StartNew();
        RingGrassTileData cpu = Build(input, cancellation, null);
        double baselineMs = timer.Elapsed.TotalMilliseconds;
        double collectionMs = 0;
        try
        {
            // Null with override-only collects eligible queries without resolving
            // their surfaces. Collection is part of the hybrid generation cost.
            timer.Restart();
            _ = Build(input, cancellation, (around, across) =>
            {
                if (queries.Count >= 100_000) throw new NotSupportedException("Prototype query limit exceeded.");
                queries.Add((around, across));
                return null;
            }, surfaceOverrideOnly: true);
            collectionMs = timer.Elapsed.TotalMilliseconds;
            if (queries.Count == 0) return new(true, cpu.InstanceCount == 0, "no eligible surface queries", 0, 0,
                baselineMs, baselineMs, collectionMs, 0, 0, 0, 0, 0, 0, cpu.InstanceCount, 0, 0, 0)
                { CpuPlacementHash = cpu.PlacementHash, GpuPlacementHash = cpu.PlacementHash };
            timer.Restart();
            var resolver = new RingGrassSurfaceResolver(input.Field, input.Edits, input.Sampler, cancellation);
            RingGrassGpuQueryCache cache = resolver.CaptureGpuQueryCache(queries);
            double cacheMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            RingGrassSurfaceHit?[] cachedHits = RingGrassSurfaceResolver.ResolveGpuQueryCacheOnCpu(cache, cancellation);
            double cachedCpuMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            RingGrassTileData cachedReplay = Replay(input, queries, cachedHits, cancellation);
            double cachedReplayMs = timer.Elapsed.TotalMilliseconds;
            if (cachedReplay.InstanceCount != cpu.InstanceCount || cachedReplay.PlacementHash != cpu.PlacementHash)
                throw new InvalidOperationException("Cached CPU path changed authoritative placement.");
            timer.Restart();
            (RingGrassSurfaceHit?[] hits, double uploadComputeMs, double readbackMs) = session.Resolve(cache, cancellation);
            double gpuResolveTotalMs = timer.Elapsed.TotalMilliseconds;
            if (!SameHits(cachedHits, hits)) throw new InvalidOperationException($"GPU cached surface result differs from CPU cache reference ({cachedCpuMs:F3}ms).");
            timer.Restart();
            RingGrassTileData replay = Replay(input, queries, hits, cancellation);
            double replayMs = timer.Elapsed.TotalMilliseconds;
            Compare(cpu, replay, out bool parity, out float positionError, out float basisError);
            timer.Restart();
            RingGrassTileData warm = Build(input, cancellation, null);
            double warmMs = timer.Elapsed.TotalMilliseconds;
            if (warm.InstanceCount != cpu.InstanceCount || warm.PlacementHash != cpu.PlacementHash)
                throw new InvalidOperationException("Warm CPU baseline changed authoritative placement.");
            return new(true, parity, parity ? "hybrid parity" : "hybrid payload mismatch", queries.Count,
                checked(cache.Queries.Length * sizeof(float) + cache.Cells.Length * sizeof(float)), baselineMs, warmMs, collectionMs, cacheMs, cachedCpuMs,
                0, uploadComputeMs, readbackMs, replayMs, cpu.InstanceCount, replay.InstanceCount, positionError, basisError)
            {
                CpuPlacementHash = cpu.PlacementHash, GpuPlacementHash = replay.PlacementHash,
                UniqueColumns = cache.UniqueColumnCount, CachedCpuReplayMilliseconds = cachedReplayMs,
                GpuResolveTotalMilliseconds = gpuResolveTotalMs
            };
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException or OverflowException)
        {
            return new(false, false, "CPU fallback: " + error.Message, queries.Count, 0, baselineMs, 0, collectionMs, 0, 0, 0, 0, 0, 0,
                cpu.InstanceCount, 0, 0, 0);
        }
    }

    private static RingGrassTileData Replay(in Input input, IReadOnlyList<(double Around, double Across)> queries,
        RingGrassSurfaceHit?[] hits, CancellationToken cancellation)
    {
        int index = 0;
        RingGrassTileData tile = Build(input, cancellation, (around, across) =>
        {
            if ((uint)index >= (uint)queries.Count) throw new InvalidOperationException("Replay exceeded collected queries.");
            var expected = queries[index];
            if (expected.Around != around || expected.Across != across) throw new InvalidOperationException("Replay query order changed.");
            return hits[index++];
        }, surfaceOverrideOnly: true);
        if (index != queries.Count) throw new InvalidOperationException($"Replay query count {index}/{queries.Count}.");
        return tile;
    }

    private static RingGrassTileData Build(in Input i, CancellationToken cancellation,
        Func<double, double, RingGrassSurfaceHit?>? observer, bool surfaceOverrideOnly = false) =>
        RingGrassTileBuilder.Build(i.Key, i.Bounds, i.CellSize, i.Candidates, i.Density, i.MaximumInstances,
            i.MaximumSlope, i.Sampler, i.Edits, i.Geometry, cancellation, i.Field, i.Exclusions, i.Appearance,
            observer, surfaceOverrideOnly);

    private static (RingGrassSurfaceHit?[] Hits, double UploadAndCompute, double Readback) ResolveWithDevice(
        RenderingDevice rd, Rid shader, Rid pipeline, RingGrassGpuQueryCache cache, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (cache.ColumnCount == 0) return (Array.Empty<RingGrassSurfaceHit?>(), 0, 0);
        Stopwatch watch = Stopwatch.StartNew();
        Rid queries = default, cells = default, output = default, set = default;
        try
        {
            if (!shader.IsValid || !pipeline.IsValid) throw new InvalidOperationException("GPU generation session unavailable.");
            queries = rd.StorageBufferCreate((uint)(cache.Queries.Length * 4), FloatBytes(cache.Queries));
            cells = rd.StorageBufferCreate((uint)Math.Max(4, cache.Cells.Length * 4), cache.Cells.Length == 0 ? new byte[4] : FloatBytes(cache.Cells));
            output = rd.StorageBufferCreate((uint)(cache.ColumnCount * 8 * 4));
            var uniforms = new Godot.Collections.Array<RDUniform>();
            foreach ((Rid rid, int binding) in new[] { (queries, 0), (cells, 1), (output, 2) })
            { var uniform = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = binding }; uniform.AddId(rid); uniforms.Add(uniform); }
            try { set = rd.UniformSetCreate(uniforms, shader, 0); }
            finally { foreach (RDUniform uniform in uniforms) uniform.Dispose(); }
            if (!queries.IsValid || !cells.IsValid || !output.IsValid || !set.IsValid) throw new InvalidOperationException("GPU generation buffer allocation failed.");
            byte[] push = new byte[16]; BitConverter.TryWriteBytes(push.AsSpan(), (uint)cache.ColumnCount);
            long list = rd.ComputeListBegin(); rd.ComputeListBindComputePipeline(list, pipeline); rd.ComputeListBindUniformSet(list, set, 0);
            rd.ComputeListSetPushConstant(list, push, 16); rd.ComputeListDispatch(list, (uint)((cache.ColumnCount + 63) / 64), 1, 1); rd.ComputeListEnd(); rd.Submit(); rd.Sync();
            cancellation.ThrowIfCancellationRequested();
            double uploadComputeMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            byte[] bytes = rd.BufferGetData(output);
            if (bytes.Length != cache.ColumnCount * 32) throw new InvalidOperationException("Incomplete GPU query readback.");
            var result = new RingGrassSurfaceHit?[cache.ColumnCount];
            for (int i = 0; i < result.Length; i++)
            {
                int o = i * 8 * 4;
                float valid = BitConverter.ToSingle(bytes, o);
                if (valid == 0) continue;
                if (valid != 1) throw new InvalidOperationException("Invalid GPU query flag.");
                for (int component = 1; component < 8; component++)
                    if (!float.IsFinite(BitConverter.ToSingle(bytes, o + component * 4)))
                        throw new InvalidOperationException("Nonfinite GPU surface result.");
                int vertical = (int)BitConverter.ToSingle(bytes, o + 4); float fraction = BitConverter.ToSingle(bytes, o + 8);
                result[i] = new RingGrassSurfaceHit((double)(vertical + fraction),
                    new Vector3(BitConverter.ToSingle(bytes,o+12), BitConverter.ToSingle(bytes,o+16), BitConverter.ToSingle(bytes,o+20)),
                    (SmoothTerrainMaterial)(int)BitConverter.ToSingle(bytes,o+24));
            }
            return (result, uploadComputeMs, watch.Elapsed.TotalMilliseconds);
        }
        finally { Free(rd, ref set); Free(rd, ref output); Free(rd, ref cells); Free(rd, ref queries); }
    }
    private static byte[] FloatBytes(float[] values) { byte[] bytes = new byte[values.Length * 4]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length); return bytes; }
    private static void Free(RenderingDevice rd, ref Rid rid) { if (rid.IsValid) rd.FreeRid(rid); rid = default; }
    private static void Compare(RingGrassTileData cpu, RingGrassTileData replay, out bool parity, out float maxPosition, out float maxBasis)
    {
        maxPosition = maxBasis = 0; parity = cpu.InstanceCount == replay.InstanceCount;
        int count = Math.Min(cpu.InstanceCount, replay.InstanceCount);
        for (int i=0;i<count;i++) for(int j=0;j<RingGrassTileData.FloatsPerInstance;j++)
        { float a=cpu.Buffer[i*20+j], b=replay.Buffer[i*20+j]; if (!float.IsFinite(a) || !float.IsFinite(b)) { parity=false; continue; } float d=MathF.Abs(a-b); if(j is 3 or 7 or 11)maxPosition=Math.Max(maxPosition,d); else if(j<12)maxBasis=Math.Max(maxBasis,d); else if(d!=0)parity=false; }
        parity &= maxPosition <= .001f && maxBasis <= .001f;
    }
    private static bool SameHits(RingGrassSurfaceHit?[] cpu, RingGrassSurfaceHit?[] gpu)
    {
        if (cpu.Length != gpu.Length) return false;
        for (int i=0;i<cpu.Length;i++)
        {
            if (cpu[i].HasValue != gpu[i].HasValue) return false;
            if (!cpu[i].HasValue) continue;
            RingGrassSurfaceHit a=cpu[i]!.Value,b=gpu[i]!.Value;
            if (!double.IsFinite(a.Height) || !double.IsFinite(b.Height) || !a.CanonicalNormal.IsFinite() || !b.CanonicalNormal.IsFinite()
                || a.Material!=b.Material || Math.Abs(a.Height-b.Height)>.001 || (a.CanonicalNormal-b.CanonicalNormal).Length()>.001f) return false;
        }
        return true;
    }
}
