using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Godot;
using RingworldTechDemo.Terrain.Generation;

namespace RingworldTechDemo.Terrain.Generation.Gpu;

/// <summary>Per-level constants of a GPU sector batch (everything RingTerrainProxySector.BuildRawSector reads outside the lattice).</summary>
public sealed class ProxyGpuSectorGlobals
{
    public int Spacing, Size, ParentSpacing;
    public float Underlay, ParentUnderlay, OwnWaterRestore, ParentWaterRestore, SkirtDepth;
    /// <summary>Geometry ring radius and (radius - vertical base offset), both in metres.</summary>
    public double Radius, RadialDistance;
    public double RecipeCircumferenceMeters, GeometryCircumferenceMeters;
    public float BedrockY, MaximumSurfaceHeight, OceanAlpha;
    public Color ShallowOcean, DeepOcean, LowTerrain, HighTerrain;
    /// <summary>Rows of the grid basis and of the owner-inverse basis (Godot Basis rows).</summary>
    public Basis Grid, OwnerInverse;
}

/// <summary>One sector for the GPU: its coordinate, the slot K2 writes and the versions it was queued under.</summary>
public readonly record struct ProxyGpuSectorJob(long AroundSector, int AcrossSector, int Slot, ulong EditVersion, int PoolGeneration, bool SelfCheck = false);

public enum ProxyGpuSectorClass { Dry, Ocean, Coastal }

/// <summary>Summary readback of one sector: class, bounds and the ocean masks (docs/gpu-offload-plan.md 4.2).</summary>
public sealed class ProxyGpuSectorResult
{
    public ProxyGpuSectorJob Job;
    public ProxyGpuSectorClass Class;
    public Vector3 VisualMin, VisualMax, MeshMin, MeshMax;
    public float Reach;
    public uint WetCount, OceanQuads;
    public bool[] OceanCells = Array.Empty<bool>();
    public byte[] DepthBytes = Array.Empty<byte>();
    /// <summary>The 67 x 67 height window, only for a self-check job.</summary>
    public float[]? Heights;
}

/// <summary>Texture RIDs of a vertex-table pool, as the render thread must see them.</summary>
public readonly record struct ProxyGpuSlotTargets(Rid Pos, Rid Chord, Rid Attr, int Generation, bool Ready);

/// <summary>
/// WO-G2 of docs/gpu-offload-plan.md: GPU job queue, budgeted scheduler and render-thread recorder for the coarse proxy levels.
/// Main thread: <see cref="Enqueue"/>, <see cref="Pump"/>, <see cref="TryTakeResult"/>. Render thread: one compute list per batch
/// (S sample, B biome, A assemble, K skirts) on the main RenderingDevice, then an asynchronous summary readback. A frame with nothing
/// queued records nothing: no dispatch, no upload.
/// </summary>
public sealed class ProxyGpuSectorBackend : IDisposable
{
    public const int MaxBatch = 16;
    public const int Contexts = 3;
    public const int WindowFloats = 67 * 67, BiomeEntries = 65 * 65, BiomeFloats = 8, SummaryWords = 1208;
    public const int HeaderWords = 64, SectorWords = 16;
    private const string TimestampPrefix = "ringpx:";
    private const string PassPrefix = "ringpxp:";
    /// <summary>Test and perf diagnostics: one compute list and one timestamp pair per pass (S, B, A, K) instead of a single list.</summary>
    public static bool PassTimingEnabled { get; set; }
    private readonly double[] _passMicroseconds = new double[4];
    private readonly int[] _passSamples = new int[4];
    public double[] PassMicrosecondsMean() { lock (_timeGate) { var r = new double[4]; for (int i = 0; i < 4; i++) r[i] = _passSamples[i] == 0 ? 0 : _passMicroseconds[i] / _passSamples[i]; return r; } }
    private static readonly string[] PassNames = { "S", "B", "A", "K" };

    /// <summary>Budget of GPU time the scheduler admits per frame (docs/gpu-offload-plan.md 5.2). Internal: not a menu tunable.</summary>
    public const double BudgetMicroseconds = 150.0;
    /// <summary>
    /// A batch of sectors is recorded over three frames (S, then B, then A and K) so no frame carries more than one budget of GPU time. A pass is
    /// latency-bound well below a full wave (docs/gpu-offload-plan.md 5.2), so slicing by thread range cannot go under that floor; the batch size
    /// adapts instead, shrinking when a measured slice exceeds the budget and growing when slices run well inside it.
    /// </summary>
    public const int MinBatchSectors = 1, MaxAdaptiveBatchSectors = MaxBatch;
    private const int SliceCount = 3;
    private static readonly int[][] SlicePasses = { new[] { 0 }, new[] { 1 }, new[] { 2, 3 } };
    private static ulong _gateFrame = ulong.MaxValue;
    // Round-robin: the first backend turned away by the frame gate (or a yield) owns the next free slice, so no level starves another.
    private static int _priorityOwner;
    private static ulong _priorityFrame;

    /// <summary>One batch in flight: its jobs and slice cursor (main thread) plus the context it holds (render thread).</summary>
    private sealed class Batch
    {
        public List<ProxyGpuSectorJob> Jobs = null!;
        public uint[] Words = null!;
        public int Slice;
        public bool SelfCheck;
        // Render thread only.
        public Context? Ctx;
        public bool Aborted;
        public int SlicesMeasured;
        public double MeasuredUs, MaxSliceUs;
    }

    private Batch? _cur;
    private int _targetSectors = 4;

    private sealed class Link
    {
        public volatile bool Ready, Failed, Released;
        public string Error = "";
        public Core? Core;
        public readonly Context[] Contexts = new Context[ProxyGpuSectorBackend.Contexts];
        public long NextSeq;
        public readonly Dictionary<long, Batch> PendingTimes = new();
        public ulong LastTimestampFrame = ulong.MaxValue;
    }

    private sealed class Context
    {
        public Rid Batch, Heights, Biome, Summary;
        public readonly Rid[] Sets = new Rid[4];
        public int PoolGeneration = -1;
        public volatile bool Busy;
    }

    /// <summary>Compiled kernels and the recipe buffer, shared by every level that uses the same recipe.</summary>
    private sealed class Core
    {
        public Rid Recipe;
        public readonly Rid[] Shaders = new Rid[4];
        public readonly Rid[] Pipelines = new Rid[4];
        public int RefCount;
        public RenderingDevice Rd = null!;
    }

    private static readonly object CoreGate = new();
    private static readonly Dictionary<ulong, Core> Cores = new();

    private readonly Link _link = new();
    private readonly ulong _coreKey;
    private readonly TerrainGenerationParameters _recipe;
    private readonly ProxyGpuClimateSettings _climate;
    private readonly List<ProxyGpuSectorJob> _queue = new();
    private readonly HashSet<(long, int)> _queued = new();
    private readonly Dictionary<(long, int), long> _firstSeen = new();
    private readonly ConcurrentQueue<ProxyGpuSectorResult> _results = new();
    private readonly object _timeGate = new();
    private int _inFlight, _inFlightSectors;
    private bool _disposed;

    public int Spacing { get; }
    public bool Ready => _link.Ready;
    public bool Failed => _link.Failed;
    public string Error => _link.Error;
    public int QueuedCount => _queue.Count;
    public int InFlightCount => Volatile.Read(ref _inFlight);
    /// <summary>Sectors of batches already started and not yet returned (the queue is not counted).</summary>
    public int InFlightSectors => Volatile.Read(ref _inFlightSectors);
    /// <summary>Horizon, in batches, the router may load this backend with: about 6 frames of its own three-frame batches.</summary>
    public const int RoutingHorizonBatches = 2;
    /// <summary>
    /// How many more sectors the router may hand this backend so they all finish within a short horizon at the measured batch size.
    /// Anything beyond that builds on the CPU workers, so a backlog (load, teleport) is never throttled to the GPU's per-frame budget.
    /// </summary>
    public int RoutingRoom { get { lock (_timeGate) return Math.Max(0, _targetSectors * RoutingHorizonBatches - InFlightSectors); } }
    public bool Busy => _queue.Count > 0 || _cur != null || InFlightCount > 0 || !_results.IsEmpty;
    public long BatchesDispatched { get; private set; }
    public long SectorsDispatched { get; private set; }
    public int LastBatchSectors { get; private set; }
    /// <summary>True while a queued job or a part-recorded batch still needs a <see cref="Pump"/> call.</summary>
    public bool PumpWanted => _queue.Count > 0 || _cur != null;
    public int TargetBatchSectors { get { lock (_timeGate) return _targetSectors; } }
    public long SlicesDispatched { get; private set; }

    public long MeasuredBatches { get; private set; }
    private readonly List<(int Sectors, double Microseconds)> _recent = new();
    /// <summary>Recent measured batches (sector count, GPU microseconds summed over the slices), oldest first; for tests and perf reports.</summary>
    public (int Sectors, double Microseconds)[] RecentBatches() { lock (_timeGate) return _recent.ToArray(); }
    /// <summary>Largest single slice measured (the per-frame cost the budget limits).</summary>
    public double MaxMeasuredBatchMicroseconds { get; private set; }

    public static bool IsSupported(TerrainGenerationParameters recipe, out string reason) => ProxyGpuHeightKernel.IsSupported(recipe, out reason);

    public ProxyGpuSectorBackend(int spacing, TerrainGenerationParameters recipe, ProxyGpuClimateSettings climate)
    {
        Spacing = spacing;
        _recipe = recipe;
        _climate = climate;
        _coreKey = recipe.DefinitionHash;
        Link link = _link;
        RenderingServer.CallOnRenderThread(Callable.From(() => InitOnRenderThread(link)));
    }

    // ------------------------------------------------------------------ init (render thread)

    private void InitOnRenderThread(Link link)
    {
        if (link.Released) return;
        try
        {
            RenderingDevice? rd = RenderingServer.GetRenderingDevice();
            if (rd == null) { link.Error = "no RenderingDevice"; link.Failed = true; return; }
            link.Core = AcquireCore(rd);
            for (int c = 0; c < Contexts; c++)
            {
                var ctx = new Context
                {
                    Batch = rd.StorageBufferCreate((uint)((HeaderWords + SectorWords * MaxBatch) * 4)),
                    Heights = rd.StorageBufferCreate((uint)(MaxBatch * WindowFloats * 4)),
                    Biome = rd.StorageBufferCreate((uint)((long)MaxBatch * BiomeEntries * BiomeFloats * 4)),
                    Summary = rd.StorageBufferCreate((uint)(MaxBatch * SummaryWords * 4)),
                };
                link.Contexts[c] = ctx;
            }
            link.Ready = true;
        }
        catch (Exception error) { link.Error = error.Message; link.Failed = true; }
    }

    private Core AcquireCore(RenderingDevice rd)
    {
        lock (CoreGate)
        {
            if (Cores.TryGetValue(_coreKey, out Core? existing)) { existing.RefCount++; return existing; }
            var core = new Core { Rd = rd, RefCount = 1 };
            try
            {
                Dictionary<string, int> layout = ProxyGpuHeightKernel.ParseLayout(ReadResource("ring_height_model.glslinc") + "\n" + ReadResource("ring_regional_climate.glslinc"));
                uint[] words = ProxyGpuHeightKernel.PackRecipe(_recipe, _climate, layout);
                var bytes = new byte[words.Length * 4];
                Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
                core.Recipe = rd.StorageBufferCreate((uint)bytes.Length, bytes);
                string source = ProxyGpuHeightKernel.ExpandIncludes(ReadResource("ring_proxy_sector_k2.glsl"));
                for (int p = 0; p < 4; p++)
                {
                    string text = source.Replace("#version 450\n", "#version 450\n#define PASS_" + PassNames[p] + "\n");
                    var src = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = text };
                    RDShaderSpirV spirv = rd.ShaderCompileSpirVFromSource(src);
                    if (!string.IsNullOrEmpty(spirv.CompileErrorCompute)) throw new InvalidOperationException("K2 " + PassNames[p] + " compile: " + spirv.CompileErrorCompute);
                    core.Shaders[p] = rd.ShaderCreateFromSpirV(spirv, "ring_proxy_sector_" + PassNames[p]);
                    if (!core.Shaders[p].IsValid) throw new InvalidOperationException("K2 " + PassNames[p] + " shader_create_from_spirv failed");
                    core.Pipelines[p] = rd.ComputePipelineCreate(core.Shaders[p]);
                    if (!core.Pipelines[p].IsValid) throw new InvalidOperationException("K2 " + PassNames[p] + " compute_pipeline_create failed");
                }
            }
            catch { FreeCore(core); throw; }
            Cores[_coreKey] = core;
            return core;
        }
    }

    private static void FreeCore(Core core)
    {
        RenderingDevice rd = core.Rd;
        foreach (Rid rid in core.Pipelines) if (rid.IsValid) rd.FreeRid(rid);
        foreach (Rid rid in core.Shaders) if (rid.IsValid) rd.FreeRid(rid);
        if (core.Recipe.IsValid) rd.FreeRid(core.Recipe);
    }

    private static string ReadResource(string file) => Godot.FileAccess.GetFileAsString("res://materials/terrain_gpu/" + file).Replace("\r\n", "\n");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Link link = _link;
        link.Released = true;
        ulong key = _coreKey;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            RenderingDevice? rd = RenderingServer.GetRenderingDevice();
            if (rd != null)
            {
                foreach (Context? ctx in link.Contexts)
                {
                    if (ctx == null) continue;
                    foreach (Rid set in ctx.Sets) if (set.IsValid && rd.UniformSetIsValid(set)) rd.FreeRid(set);
                    foreach (Rid rid in new[] { ctx.Batch, ctx.Heights, ctx.Biome, ctx.Summary }) if (rid.IsValid) rd.FreeRid(rid);
                }
            }
            if (link.Core != null)
                lock (CoreGate)
                    if (--link.Core.RefCount <= 0) { Cores.Remove(key); if (rd != null) FreeCore(link.Core); }
        }));
    }

    // ------------------------------------------------------------------ queue (main thread)

    public bool IsQueued(long aroundSector, int acrossSector) => _queued.Contains((aroundSector, acrossSector));

    public bool Enqueue(ProxyGpuSectorJob job)
    {
        if (!_queued.Add((job.AroundSector, job.AcrossSector))) return false;
        // First time this sector was handed to the GPU (re-routing every update re-enqueues it): the start of its time to install.
        if (_firstSeen.Count > 1024) _firstSeen.Clear();
        _firstSeen.TryAdd((job.AroundSector, job.AcrossSector), System.Diagnostics.Stopwatch.GetTimestamp());
        _queue.Add(job);
        return true;
    }

    /// <summary>Drops queued jobs the caller no longer wants and hands each back so its slot can be released. Dispatched jobs always run to completion.</summary>
    public void DropQueued(Predicate<ProxyGpuSectorJob> drop, List<ProxyGpuSectorJob> dropped)
    {
        for (int i = _queue.Count - 1; i >= 0; i--)
        {
            if (!drop(_queue[i])) continue;
            dropped.Add(_queue[i]);
            _queued.Remove((_queue[i].AroundSector, _queue[i].AcrossSector));
            _queue.RemoveAt(i);
        }
    }

    public bool TryTakeResult(out ProxyGpuSectorResult? result)
    {
        if (_results.TryDequeue(out result))
        {
            _queued.Remove((result.Job.AroundSector, result.Job.AcrossSector));
            if (_firstSeen.Remove((result.Job.AroundSector, result.Job.AcrossSector), out long t0))
                RingworldTechDemo.Voxel.Phase3.RingProxyBuildCounters.AddGpuLatency(Spacing, (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Records at most one slice this frame: a batch is recorded as S, then B, then A and K, one slice per frame, and the batch size adapts so a
    /// slice stays inside <see cref="BudgetMicroseconds"/>. Returns true when this call started a batch.
    /// </summary>
    public bool Pump(ProxyGpuSectorGlobals globals, Func<ProxyGpuSlotTargets> targets)
    {
        if (!_link.Ready || _link.Failed) return false;
        if (_cur == null && (_queue.Count == 0 || Volatile.Read(ref _inFlight) >= Contexts)) return false;
        ProxyGpuSlotTargets pool = targets();
        if (!pool.Ready) return false;
        // One slice per frame across every level's backend, so the proxy compute adds at most one budget of GPU time to a frame.
        ulong frame = Engine.GetProcessFrames();
        if (_priorityOwner != 0 && _priorityOwner != Spacing && frame - _priorityFrame <= 2UL) return false;
        if (_gateFrame == frame)
        {
            if (_priorityOwner == 0 || frame - _priorityFrame > 2UL) { _priorityOwner = Spacing; _priorityFrame = frame; }
            return false;
        }
        if (_priorityOwner == Spacing) _priorityOwner = 0;
        bool started = false;
        if (_cur == null)
        {
            int target; lock (_timeGate) target = _targetSectors;
            int count = Math.Min(_queue.Count, target);
            // A self-check job rides alone so its heights readback is unambiguous.
            for (int i = 0; i < count; i++)
                if (_queue[i].SelfCheck) { count = i == 0 ? 1 : i; break; }
            var batchJobs = _queue.GetRange(0, count);
            _queue.RemoveRange(0, count);
            _cur = new Batch { Jobs = batchJobs, Words = WriteBatch(globals, batchJobs), SelfCheck = batchJobs[0].SelfCheck };
            Interlocked.Increment(ref _inFlight);
            Interlocked.Add(ref _inFlightSectors, count);
            BatchesDispatched++;
            SectorsDispatched += count;
            LastBatchSectors = count;
            started = true;
        }
        Batch cur = _cur;
        int slice = cur.Slice++;
        bool last = cur.Slice >= SliceCount;
        if (last) _cur = null;
        SlicesDispatched++;
        _gateFrame = frame;
        Link link = _link;
        long seq = ++link.NextSeq;
        RenderingServer.CallOnRenderThread(Callable.From(() => Record(link, seq, cur, slice, last, pool)));
        return started;
    }

    // ------------------------------------------------------------------ batch packing

    public static uint[] WriteBatch(ProxyGpuSectorGlobals g, List<ProxyGpuSectorJob> jobs)
    {
        var w = new uint[HeaderWords + SectorWords * jobs.Count];
        void F(int i, double v) => w[i] = BitConverter.SingleToUInt32Bits((float)v);
        w[0] = (uint)g.Spacing; w[1] = (uint)g.Size; w[2] = (uint)g.ParentSpacing;
        F(4, g.Underlay); F(5, g.ParentUnderlay); F(6, g.OwnWaterRestore); F(7, g.ParentWaterRestore); F(8, g.SkirtDepth);
        F(9, g.Radius); F(10, g.RadialDistance); F(11, g.BedrockY); F(12, g.MaximumSurfaceHeight); F(13, g.OceanAlpha);
        void C(int i, Color c) { F(i, c.R); F(i + 1, c.G); F(i + 2, c.B); }
        C(14, g.ShallowOcean); C(17, g.DeepOcean); C(20, g.LowTerrain); C(23, g.HighTerrain);
        void B(int i, Basis b)
        {
            F(i, b.Row0.X); F(i + 1, b.Row0.Y); F(i + 2, b.Row0.Z);
            F(i + 4, b.Row1.X); F(i + 5, b.Row1.Y); F(i + 6, b.Row1.Z);
            F(i + 8, b.Row2.X); F(i + 9, b.Row2.Y); F(i + 10, b.Row2.Z);
        }
        B(28, g.Grid); B(40, g.OwnerInverse);
        for (int s = 0; s < jobs.Count; s++)
        {
            int o = HeaderWords + s * SectorWords;
            long anchor = jobs[s].AroundSector * g.Size;
            w[o] = unchecked((uint)(int)anchor);
            w[o + 1] = unchecked((uint)(jobs[s].AcrossSector * g.Size));
            w[o + 2] = (uint)jobs[s].Slot;
            var trig = ProxyGpuHeightKernel.AnchorTrig(anchor, g.RecipeCircumferenceMeters);
            w[o + 4] = BitConverter.SingleToUInt32Bits(trig.CosHi); w[o + 5] = BitConverter.SingleToUInt32Bits(trig.CosLo);
            w[o + 6] = BitConverter.SingleToUInt32Bits(trig.SinHi); w[o + 7] = BitConverter.SingleToUInt32Bits(trig.SinLo);
            // Sector-centre angle, as RingLodGeometrySnapshot.CanonicalToAbsoluteLocal wraps and divides it.
            double around = (anchor + g.Size * 0.5) % g.GeometryCircumferenceMeters;
            if (around < 0.0) around += g.GeometryCircumferenceMeters;
            double theta = around / g.Radius;
            F(o + 8, Math.Cos(theta)); F(o + 9, Math.Sin(theta));
        }
        return w;
    }

    // ------------------------------------------------------------------ record (render thread)

    private void Record(Link link, long seq, Batch batch, int slice, bool last, ProxyGpuSlotTargets pool)
    {
        if (batch.Aborted) return;
        List<ProxyGpuSectorJob> jobs = batch.Jobs;
        try
        {
            if (link.Released || !link.Ready || link.Core is not { } core) { Abort(batch); return; }
            RenderingDevice rd = core.Rd;
            if (!pool.Pos.IsValid || !pool.Attr.IsValid || !pool.Chord.IsValid) throw new InvalidOperationException("slot textures are not ready");
            int count = jobs.Count;
            Context? ctx = batch.Ctx;
            if (ctx == null)
            {
                for (int c = 0; c < Contexts; c++) if (!link.Contexts[c].Busy) { ctx = link.Contexts[c]; break; }
                if (ctx == null) throw new InvalidOperationException("no free GPU context");
                batch.Ctx = ctx;
                ctx.Busy = true;
                var bytes = new byte[batch.Words.Length * 4];
                Buffer.BlockCopy(batch.Words, 0, bytes, 0, bytes.Length);
                rd.BufferUpdate(ctx.Batch, 0, (uint)bytes.Length, bytes);
                rd.BufferClear(ctx.Summary, 0, (uint)(count * SummaryWords * 4));
            }
            if (ctx.PoolGeneration != pool.Generation) RebuildSets(rd, core, ctx, pool);
            uint summaryBytes = (uint)(count * SummaryWords * 4);
            using (RingworldTechDemo.Diagnostics.RingPerformanceGpu.BeginScope(RingworldTechDemo.Diagnostics.RingPerformanceGpu.ProxyHeights))
            {
                rd.CaptureTimestamp(TimestampPrefix + seq + ":B");
                int[] groups =
                {
                    (count * WindowFloats + 63) / 64,
                    (count * (Spacing >= 16 ? BiomeEntries : 33 * 33) + 63) / 64,
                    (count * BiomeEntries + 63) / 64,
                    (count * 4 * 64 + 63) / 64,
                };
                byte[] push = new byte[16];
                BitConverter.TryWriteBytes(push.AsSpan(0, 4), count);
                bool perPass = PassTimingEnabled;
                int[] passes = SlicePasses[slice];
                long list = perPass ? 0 : rd.ComputeListBegin();
                for (int r = 0; r < passes.Length; r++)
                {
                    int p = passes[r];
                    if (perPass) { rd.CaptureTimestamp(PassPrefix + (seq * 4 + p) + ":B"); list = rd.ComputeListBegin(); }
                    else if (r > 0) rd.ComputeListAddBarrier(list);
                    rd.ComputeListBindComputePipeline(list, core.Pipelines[p]);
                    rd.ComputeListBindUniformSet(list, ctx.Sets[p], 0);
                    rd.ComputeListSetPushConstant(list, push, 16);
                    rd.ComputeListDispatch(list, (uint)groups[p], 1, 1);
                    if (perPass) { rd.ComputeListEnd(); rd.CaptureTimestamp(PassPrefix + (seq * 4 + p) + ":E"); }
                }
                if (!perPass) rd.ComputeListEnd();
                rd.CaptureTimestamp(TimestampPrefix + seq + ":E");
            }
            lock (link) link.PendingTimes[seq] = batch;
            PollTimestamps(link, rd);
            if (!last) return;
            Context done = ctx;
            bool selfCheck = batch.SelfCheck;
            float[]? heights = null;
            byte[]? summaryData = null;
            int parts = selfCheck ? 2 : 1;
            var gate = new object();
            void Part()
            {
                lock (gate)
                {
                    if (--parts > 0) return;
                }
                try { Decode(jobs, summaryData!, heights); }
                catch (Exception error) { link.Error = "summary decode failed: " + error.Message; link.Failed = true; }
                finally { done.Busy = false; Interlocked.Decrement(ref _inFlight); Interlocked.Add(ref _inFlightSectors, -jobs.Count); }
            }
            if (selfCheck)
                rd.BufferGetDataAsync(ctx.Heights, Callable.From((byte[] data) =>
                {
                    var h = new float[WindowFloats];
                    Buffer.BlockCopy(data, 0, h, 0, Math.Min(data.Length, WindowFloats * 4));
                    heights = h;
                    Part();
                }), 0, (uint)(WindowFloats * 4));
            rd.BufferGetDataAsync(ctx.Summary, Callable.From((byte[] data) => { summaryData = data; Part(); }), 0, summaryBytes);
        }
        catch (Exception error)
        {
            link.Error = "record failed: " + error.Message;
            link.Failed = true;
            Abort(batch);
        }
    }

    /// <summary>Gives up a part-recorded batch exactly once: later slices skip it and its context and in-flight count are released.</summary>
    private void Abort(Batch batch)
    {
        if (batch.Aborted) return;
        batch.Aborted = true;
        if (batch.Ctx != null) batch.Ctx.Busy = false;
        Interlocked.Decrement(ref _inFlight);
        Interlocked.Add(ref _inFlightSectors, -batch.Jobs.Count);
    }

    private void RebuildSets(RenderingDevice rd, Core core, Context ctx, ProxyGpuSlotTargets pool)
    {
        foreach (Rid set in ctx.Sets) if (set.IsValid && rd.UniformSetIsValid(set)) rd.FreeRid(set);
        for (int p = 0; p < 4; p++)
        {
            RDUniform Buf(int binding, Rid rid) { var u = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = binding }; u.AddId(rid); return u; }
            RDUniform Img(int binding, Rid rid) { var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = binding }; u.AddId(rid); return u; }
            var uniforms = new Godot.Collections.Array<RDUniform>
            {
                Buf(0, core.Recipe), Buf(1, ctx.Batch), Buf(2, ctx.Heights), Buf(3, ctx.Biome), Buf(4, ctx.Summary),
                Img(5, pool.Pos), Img(6, pool.Chord), Img(7, pool.Attr),
            };
            ctx.Sets[p] = rd.UniformSetCreate(uniforms, core.Shaders[p], 0);
            if (!ctx.Sets[p].IsValid) throw new InvalidOperationException("K2 uniform set creation failed (pass " + PassNames[p] + ")");
        }
        ctx.PoolGeneration = pool.Generation;
    }

    private void PollTimestamps(Link link, RenderingDevice rd)
    {
        ulong frame = rd.GetCapturedTimestampsFrame();
        if (frame == link.LastTimestampFrame) return;
        link.LastTimestampFrame = frame;
        uint count = rd.GetCapturedTimestampsCount();
        Dictionary<long, (ulong Begin, ulong End)>? found = null, passFound = null;
        for (uint i = 0; i < count; i++)
        {
            string name = rd.GetCapturedTimestampName(i);
            if (name.StartsWith(PassPrefix, StringComparison.Ordinal))
            {
                int pc = name.LastIndexOf(':');
                if (pc > 0 && long.TryParse(name.AsSpan(PassPrefix.Length, pc - PassPrefix.Length), out long pkey))
                {
                    passFound ??= new();
                    passFound.TryGetValue(pkey, out var pp);
                    ulong pt = rd.GetCapturedTimestampGpuTime(i);
                    if (name[pc + 1] == 'B') pp.Begin = pt; else pp.End = pt;
                    passFound[pkey] = pp;
                }
                continue;
            }
            if (!name.StartsWith(TimestampPrefix, StringComparison.Ordinal)) continue;
            int colon = name.LastIndexOf(':');
            if (colon < 0 || !long.TryParse(name.AsSpan(TimestampPrefix.Length, colon - TimestampPrefix.Length), out long seq)) continue;
            found ??= new();
            found.TryGetValue(seq, out var pair);
            ulong time = rd.GetCapturedTimestampGpuTime(i);
            if (name[colon + 1] == 'B') pair.Begin = time; else pair.End = time;
            found[seq] = pair;
        }
        if (passFound != null)
            lock (_timeGate)
                foreach ((long key, (ulong b, ulong e)) in passFound)
                    if (b != 0 && e > b) { int pass = (int)(key & 3); _passMicroseconds[pass] += (e - b) / 1000.0; _passSamples[pass]++; }
        if (found == null) return;
        lock (link)
        {
            foreach ((long seq, (ulong b, ulong e)) in found)
            {
                if (b == 0 || e <= b || !link.PendingTimes.Remove(seq, out Batch? batch)) continue;
                double us = (e - b) / 1000.0;
                if (!double.IsFinite(us)) continue;
                lock (_timeGate)
                {
                    MaxMeasuredBatchMicroseconds = Math.Max(MaxMeasuredBatchMicroseconds, us);
                    batch.MeasuredUs += us;
                    batch.MaxSliceUs = Math.Max(batch.MaxSliceUs, us);
                    // A slice over the budget shrinks the next batch at once; a whole batch that ran well inside it grows the next one.
                    if (us > BudgetMicroseconds) _targetSectors = Math.Max(MinBatchSectors, _targetSectors * 3 / 4);
                    if (++batch.SlicesMeasured >= SliceCount)
                    {
                        MeasuredBatches++;
                        if (batch.MaxSliceUs < BudgetMicroseconds * 0.6 && batch.Jobs.Count >= _targetSectors) _targetSectors = Math.Min(MaxAdaptiveBatchSectors, _targetSectors + 1);
                        _recent.Add((batch.Jobs.Count, batch.MeasuredUs)); if (_recent.Count > 256) _recent.RemoveAt(0);
                    }
                }
            }
            if (link.PendingTimes.Count > 256) link.PendingTimes.Clear();
        }
    }

    /// <summary>Main thread, while jobs are outstanding: asks the render thread to fold in the newest timestamp set.</summary>
    public void PollTimes()
    {
        Link link = _link;
        if (!link.Ready) return;
        bool pending; lock (link) pending = link.PendingTimes.Count > 0;
        if (!pending) return;
        RenderingServer.CallOnRenderThread(Callable.From(() => { if (!link.Released && link.Core is { } core) PollTimestamps(link, core.Rd); }));
    }

    // ------------------------------------------------------------------ summary decode

    private const int SumVisMin = 0, SumVisMax = 3, SumMeshMin = 6, SumMeshMax = 9, SumReach = 12, SumWet = 13, SumQuads = 14, SumMask = 16, SumDepth = 144;

    private static float FromOrdered(uint o)
    {
        uint u = (o & 0x80000000u) != 0u ? o & 0x7FFFFFFFu : ~o;
        return BitConverter.UInt32BitsToSingle(u);
    }

    private void Decode(List<ProxyGpuSectorJob> jobs, byte[] data, float[]? heights)
    {
        var words = new uint[data.Length / 4];
        Buffer.BlockCopy(data, 0, words, 0, words.Length * 4);
        for (int s = 0; s < jobs.Count; s++)
        {
            int b = s * SummaryWords;
            var r = new ProxyGpuSectorResult { Job = jobs[s], WetCount = words[b + SumWet], OceanQuads = words[b + SumQuads] };
            Vector3 Min(int o) => new(FromOrdered(~words[b + o]), FromOrdered(~words[b + o + 1]), FromOrdered(~words[b + o + 2]));
            Vector3 Max(int o) => new(FromOrdered(words[b + o]), FromOrdered(words[b + o + 1]), FromOrdered(words[b + o + 2]));
            r.VisualMin = Min(SumVisMin); r.VisualMax = Max(SumVisMax);
            r.MeshMin = Min(SumMeshMin); r.MeshMax = Max(SumMeshMax);
            r.Reach = BitConverter.UInt32BitsToSingle(words[b + SumReach]);
            r.Class = r.WetCount == 0u ? ProxyGpuSectorClass.Dry : r.WetCount == 65u * 65u ? ProxyGpuSectorClass.Ocean : ProxyGpuSectorClass.Coastal;
            if (r.Class == ProxyGpuSectorClass.Ocean)
            {
                var cells = new bool[64 * 64];
                for (int i = 0; i < cells.Length; i++) cells[i] = (words[b + SumMask + (i >> 5)] >> (i & 31) & 1u) != 0u;
                var depth = new byte[65 * 65];
                for (int i = 0; i < depth.Length; i++) depth[i] = (byte)(words[b + SumDepth + (i >> 2)] >> ((i & 3) * 8));
                r.OceanCells = cells; r.DepthBytes = depth;
            }
            if (s == 0) r.Heights = heights;
            _results.Enqueue(r);
        }
    }
}
