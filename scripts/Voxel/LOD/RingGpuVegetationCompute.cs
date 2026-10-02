using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using RingworldTechDemo.Diagnostics;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Resident source buffers compacted directly into Godot indirect MultiMeshes.
/// Placement remains on the CPU. RD work and retirement run on the render thread.</summary>
public sealed class RingGpuVegetationCompute : IDisposable
{
    public const int TreeFloats = 12, TreeCustomFloats = 16, GrassFloats = 20;
    private const int ParameterBytes = 256, MaximumRootsPerBatch = 1_048_576;
    public sealed record BatchRegistration(string Identity, MultiMesh MultiMesh, Mesh Mesh,
        float[] Candidates, Vector4[]? Metadata, int Capacity, Aabb CustomAabb, bool Grass,
        bool TreeCustomData = false, bool TreeDitherData = false);
    public readonly record struct BatchFrameParameters(int BatchId, Transform3D LocalToWorld, bool Grass,
        int LodPass, float NearDistance, float FarDistance, float TransitionDistance,
        float NearDensity, float FarDensity, bool EnableLod, float TreeRankGate,
        bool ProtectedSelection, Vector4[]? GrassFrustumPlanes, float RadiusGuard,
        Vector2? TreeCameraCanonicalLocal = null, long VisibilityRevision = 0,
        bool DistanceBasedTreeProtection = false, float TreeFadeInStart = -1, float TreeFadeInEnd = -1,
        float TreeFadeOutStart = -1, float TreeFadeOutEnd = -1);
    public sealed record FrameRequest(Vector3 CameraWorld, uint[] LodBudgets,
        IReadOnlyList<BatchFrameParameters> Batches, bool SkipUnchanged = true,
        int MaximumBatchesPerDispatch = 0);

    private sealed class Batch
    {
        public required int Id;
        public required BatchRegistration Registration;
        public volatile bool Retired, Dispatched;
        // Separate from Dispatched (which means ever). A stationary budgeted
        // grass frame only needs one compaction per source/frame revision.
        public volatile int LastGrassFrameRevision;
        // CPU-only freshness token for a resident output's visibility decision.
        public long CompletedVisibilityRevision = 0;
        public Rid Source, Metadata, Parameters, Flags, Counts, Offsets, Set, Output, Commands;
        public SharedSource? SharedSource;
        public Vector4[]? PendingMetadata;
        public byte[] ParameterDataBytes = new byte[ParameterBytes];
        public float[] ParameterWords = new float[64];
        public bool UploadedHasProtectedRoots;
        public int SurfaceCount;
        public bool Allocated;
        public long Bytes;
        public int SourceStride => SourceStrideFor(Registration);
        public int OutputStride => OutputStrideFor(Registration);
        public int Stride => SourceStride;
        public int Count => Registration.Candidates.Length / SourceStride;
        public int Groups => (Count + 63) / 64;
        // Read on the main thread during registration. RenderingDevice work must
        // never query a Godot Mesh.
        public float PrototypeHorizontalRadius;
    }
    private sealed class OutputMirror
    {
        public required MultiMesh Target;
        public required Mesh Mesh;
        public Rid Output, Commands;
        public int SurfaceCount;
        public bool Allocated, Ready, PendingCopy;
        public bool Grass, Custom, Retired;
        public long Bytes;
    }

    /// <summary>One immutable candidate upload may feed more than one indirect
    /// output. Grass near/intermediate passes deliberately share this resource.
    /// Metadata remains per-pass because tree batches can update it independently.</summary>
    private sealed class SharedSource
    {
        public required float[] Candidates;
        public required int Stride;
        public Rid Rid;
        public int References;
        public long Bytes;
    }

    private readonly object _gate = new();
    // All-grass outputs own their scan buffers and do not consume the shared tree
    // budget, so callers may group their compute phases at runtime.
    private volatile bool _groupGrassPhases;
    private readonly Dictionary<int, Batch> _batches = new();
    private readonly Dictionary<int, OutputMirror> _mirrors = new();
    private readonly Queue<OutputMirror> _retiredMirrors = new();
    private readonly HashSet<MultiMesh> _retiringMirrorTargets = new();
    private readonly Dictionary<float[], SharedSource> _sharedSources = new(ReferenceEqualityComparer.Instance);
    private readonly Queue<Batch> _retired = new();
    private RenderingDevice? _rd;
    private Rid _shader, _pipeline, _budgets;
    private FrameRequest? _pending, _lastRequest;
    private int _pendingGrassFrameRevision, _grassFrameRevision;
    private bool _queued, _disposed, _ready, _failed;
    private int _nextId;
    private long _completedUpdates, _skippedUnchangedUpdates, _dispatchCount, _barrierCount, _parameterUploads, _estimatedBytes;
    private long _sharedSourceBytes, _outputBytes;
    private int _sharedSourceCount;
    private uint _membershipRevision, _lastRequestMembershipRevision;
    private readonly List<(Batch Batch, BatchFrameParameters Frame)> _work = new();
    private readonly List<(Batch Batch, BatchFrameParameters Frame)> _selectedWork = new();
    private readonly HashSet<int> _seen = new();
    private readonly byte[] _zeroBudgets = new byte[20];
    private readonly byte[] _push = new byte[32];
    private string _status = "Uninitialized";
    private int _grassDispatchCursor;
    private int _lastFrameRequestedBatchCount, _lastFrameDispatchedBatchCount, _deferredBatchCount;
    public string Status { get { lock (_gate) return _status; } }
    public bool Ready { get { lock (_gate) return _ready && !_disposed && !_failed; } }
    public bool Failed { get { lock (_gate) return _failed; } }
    public int BatchCount { get { lock (_gate) return _batches.Count; } }
    public long CompletedUpdates => Interlocked.Read(ref _completedUpdates);
    public long SkippedUnchangedUpdates => Interlocked.Read(ref _skippedUnchangedUpdates);
    public long DispatchCount => Interlocked.Read(ref _dispatchCount);
    /// <summary>Compute-list barriers submitted by this backend, including legacy and grass paths.</summary>
    public long BarrierCount => Interlocked.Read(ref _barrierCount);
    public long ParameterUploads => Interlocked.Read(ref _parameterUploads);
    public long EstimatedBytes => Interlocked.Read(ref _estimatedBytes);
    /// <summary>Resident immutable candidate uploads. A grass tile has one of
    /// these even when it has both near and intermediate indirect outputs.</summary>
    public long SharedSourceBytes => Interlocked.Read(ref _sharedSourceBytes);
    /// <summary>Indirect MultiMesh output allocations, excluding shared sources.</summary>
    public long OutputBytes => Interlocked.Read(ref _outputBytes);
    public int SharedSourceCount => Volatile.Read(ref _sharedSourceCount);
    /// <summary>Number of valid batches requested by the most recently serviced frame.</summary>
    public int LastFrameRequestedBatchCount => Volatile.Read(ref _lastFrameRequestedBatchCount);
    /// <summary>Number of batches whose indirect output was updated by the most recently serviced frame.</summary>
    public int LastFrameDispatchedBatchCount => Volatile.Read(ref _lastFrameDispatchedBatchCount);
    /// <summary>Valid requested batches retaining their prior indirect result because a grass dispatch budget deferred them.</summary>
    public int DeferredBatchCount => Volatile.Read(ref _deferredBatchCount);

    /// <summary>Enables phase-grouped all-grass dispatches. Tree and mixed requests
    /// retain their ordered dispatches because tree batches share the LOD budget.</summary>
    public bool GroupGrassPhases { get => _groupGrassPhases; set => _groupGrassPhases = value; }
    /// <summary>Fuses eligible small tree compactions while retaining the legacy path for A/B comparison.</summary>
    public bool OptimizeTreeDispatches { get; set; } = true;

    /// <summary>Creates the vegetation compute backend with grouped grass phases
    /// disabled unless explicitly requested.</summary>
    public RingGpuVegetationCompute(bool groupGrassPhases = false)
    {
        _groupGrassPhases = groupGrassPhases;
    }

    public bool IsBatchReady(int id)
    {
        lock (_gate) return _ready && !_failed && _batches.TryGetValue(id, out Batch? batch)
            && batch.Dispatched && !batch.Retired;
    }

    /// <summary>Reports whether this output has completed the caller's current
    /// visibility decision, rather than merely retaining an older indirect result.</summary>
    public bool IsBatchReady(int id, long visibilityRevision)
    {
        lock (_gate) return _ready && !_failed && _batches.TryGetValue(id, out Batch? batch)
            && batch.Dispatched && !batch.Retired
            && Volatile.Read(ref batch.CompletedVisibilityRevision) == visibilityRevision;
    }

    /// <summary>One coherent readiness snapshot for the caller's visibility decisions.
    /// A dispatch completing after this snapshot becomes visible on the next refresh.</summary>
    public void CaptureReadyBatches(IReadOnlyList<BatchFrameParameters> frames, HashSet<int> ready)
    {
        ready.Clear();
        lock (_gate)
        {
            if (!_ready || _failed) return;
            foreach (BatchFrameParameters frame in frames)
                if (_batches.TryGetValue(frame.BatchId, out Batch? batch) && batch.Dispatched && !batch.Retired
                    && Volatile.Read(ref batch.CompletedVisibilityRevision) == frame.VisibilityRevision)
                    ready.Add(frame.BatchId);
        }
    }

    public void Initialize()
    {
        lock (_gate) if (!_disposed && !_failed && !_ready) QueueWork();
    }

    /// <summary>Call on the main thread with a hidden output. Do not write to the
    /// registered MultiMesh again; replace it with a fresh resource for CPU fallback.</summary>
    public int RegisterBatch(BatchRegistration registration)
    {
        if (!Valid(registration)) return -1;
        lock (_gate)
        {
            if (_disposed || _failed) return -1;
            foreach (Batch existing in _batches.Values)
                if (existing.Registration.MultiMesh == registration.MultiMesh) return -1;
            PrepareResource(registration);
            int id = ++_nextId;
            _batches.Add(id, new Batch { Id = id, Registration = registration,
                PrototypeHorizontalRadius = PrototypeHorizontalRadius(registration.Mesh),
                UploadedHasProtectedRoots = HasProtectedRoots(registration.Metadata) });
            _membershipRevision++;
            QueueWork();
            return id;
        }
    }

    /// <summary>Replacement requires a fresh MultiMesh so queued retirement cannot
    /// invalidate the replacement. Camera changes belong in RequestFrame.</summary>
    public bool UpdateBatch(int id, BatchRegistration registration)
    {
        if (!Valid(registration)) return false;
        lock (_gate)
        {
            if (_disposed || _failed || !_batches.TryGetValue(id, out Batch? previous)
                || previous.Registration.MultiMesh == registration.MultiMesh) return false;
            PrepareResource(registration);
            previous.Retired = true;
            _retired.Enqueue(previous);
            _batches[id] = new Batch { Id = id, Registration = registration,
                PrototypeHorizontalRadius = PrototypeHorizontalRadius(registration.Mesh),
                UploadedHasProtectedRoots = HasProtectedRoots(registration.Metadata) };
            _membershipRevision++;
            QueueWork();
            return true;
        }
    }

    /// <summary>Changes collision priority without reuploading transforms.</summary>
    public bool UpdateMetadata(int id, Vector4[] metadata)
    {
        lock (_gate)
        {
            if (_disposed || _failed || !_batches.TryGetValue(id, out Batch? batch)
                || metadata.Length != batch.Count) return false;
            batch.PendingMetadata = (Vector4[])metadata.Clone();
            _membershipRevision++;
            QueueWork();
            return true;
        }
    }

    public bool RemoveBatch(int id)
    {
        lock (_gate)
        {
            if (!_batches.Remove(id, out Batch? batch)) return false;
            RetireMirror(id);
            batch.Retired = true;
            _retired.Enqueue(batch);
            _membershipRevision++;
            QueueWork();
            return true;
        }
    }

    /// <summary>Copies a parent indirect output into a same-format render-only MultiMesh after each parent dispatch.</summary>
    public bool AttachOutputMirror(int sourceBatchId, MultiMesh target, Mesh targetMesh)
    {
        lock (_gate)
        {
            if (_disposed || _failed || target is null || targetMesh is null || _retiringMirrorTargets.Contains(target) || !_batches.TryGetValue(sourceBatchId, out Batch? source)
                || source.Registration.MultiMesh == target || targetMesh.GetSurfaceCount() <= 0) return false;
            target.InstanceCount = 0;
            target.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
            target.UseColors = source.Registration.Grass;
            target.UseCustomData = source.Registration.Grass || source.Registration.TreeCustomData || source.Registration.TreeDitherData;
            target.Mesh = targetMesh;
            target.InstanceCount = source.Registration.Capacity;
            target.VisibleInstanceCount = -1;
            target.CustomAabb = source.Registration.CustomAabb;
            RetireMirror(sourceBatchId);
            bool custom = source.Registration.Grass || source.Registration.TreeCustomData || source.Registration.TreeDitherData;
            _mirrors[sourceBatchId] = new OutputMirror { Target = target, Mesh = targetMesh, PendingCopy = true,
                Grass = source.Registration.Grass, Custom = custom };
            QueueWork();
            return true;
        }
    }

    public bool DetachOutputMirror(int sourceBatchId) { lock (_gate) { if (!_mirrors.ContainsKey(sourceBatchId)) return false; RetireMirror(sourceBatchId); QueueWork(); return true; } }
    public bool IsOutputMirrorReady(int sourceBatchId) { lock (_gate) return _mirrors.TryGetValue(sourceBatchId, out OutputMirror? mirror) && mirror.Ready; }

    private void RetireMirror(int sourceBatchId)
    {
        if (_mirrors.Remove(sourceBatchId, out OutputMirror? mirror)) { mirror.Retired = true; _retiringMirrorTargets.Add(mirror.Target); _retiredMirrors.Enqueue(mirror); }
    }

    public bool RequestFrame(FrameRequest request)
    {
        if (!request.CameraWorld.IsFinite()) return false;
        lock (_gate)
        {
            if (_disposed || _failed) return false;
            // This comparison deliberately reads caller-owned inputs before copying them.
            // It is exact, and registration/metadata changes invalidate it independently.
            bool budgetedGrass = request.MaximumBatchesPerDispatch > 0 && request.Batches.Count > 0
                && AllGrass(request.Batches);
            bool unchanged = _lastRequest is not null
                && _lastRequestMembershipRevision == _membershipRevision
                && FrameEquals(request, _lastRequest);
            if (request.SkipUnchanged && unchanged
                && (!budgetedGrass || GrassFrameComplete(request, _grassFrameRevision)))
            { Interlocked.Increment(ref _skippedUnchangedUpdates); return true; }
        }
        // Snapshot small frame inputs; callers may reuse their lists next frame.
        var frames = new BatchFrameParameters[request.Batches.Count];
        for (int i = 0; i < frames.Length; i++)
        {
            BatchFrameParameters frame = request.Batches[i];
            frames[i] = frame with { GrassFrustumPlanes = frame.GrassFrustumPlanes is null
                ? null : (Vector4[])frame.GrassFrustumPlanes.Clone() };
        }
        lock (_gate)
        {
            if (_disposed || _failed) return false;
            bool budgetedGrass = request.MaximumBatchesPerDispatch > 0 && request.Batches.Count > 0
                && AllGrass(request.Batches);
            bool unchanged = _lastRequest is not null
                && _lastRequestMembershipRevision == _membershipRevision
                && FrameEquals(request, _lastRequest);
            // Retain a state revision while the bounded set catches up. New
            // camera/frame/membership inputs begin a fresh catch-up pass.
            if (!unchanged) _grassFrameRevision++;
            _pendingGrassFrameRevision = budgetedGrass ? _grassFrameRevision : 0;
            _pending = new FrameRequest(request.CameraWorld, (uint[])request.LodBudgets.Clone(), frames,
                request.SkipUnchanged, request.MaximumBatchesPerDispatch);
            _lastRequest = _pending;
            _lastRequestMembershipRevision = _membershipRevision;
            QueueWork();
            return true;
        }
    }

    private void QueueWork()
    {
        if (_queued) return;
        _queued = true;
        RenderingServer.CallOnRenderThread(Callable.From(Work));
    }

    private void Work()
    {
        try
        {
            DrainRetired();
            DrainRetiredMirrors();
            lock (_gate) if (_disposed || _failed) return;
            if (!InitializeDevice()) return;
            Batch[] batches;
            FrameRequest? frame;
            int grassFrameRevision;
            lock (_gate)
            {
                batches = new List<Batch>(_batches.Values).ToArray();
                frame = _pending;
                grassFrameRevision = _pendingGrassFrameRevision;
                _pending = null;
            }
            foreach (Batch batch in batches)
            {
                if (batch.Retired) continue;
                EnsureBatch(batch);
                Vector4[]? metadata;
                lock (_gate) { metadata = batch.PendingMetadata; batch.PendingMetadata = null; }
                if (metadata is not null)
                {
                    _rd!.BufferUpdate(batch.Metadata, 0, (uint)(batch.Count * 16), MetadataBytes(metadata, batch.Count));
                    batch.UploadedHasProtectedRoots = HasProtectedRoots(metadata);
                }
                EnsureMirror(batch);
            }
            if (frame is not null) Dispatch(frame, grassFrameRevision);
            else CopyOutputMirrors(_rd!);
        }
        catch (Exception error)
        {
            lock (_gate) { _failed = true; _ready = false; _pending = null; _status = "GPU error: " + error.Message; }
            GD.PushWarning("Vegetation compute fallback: " + error.Message);
        }
        finally
        {
            lock (_gate)
            {
                _queued = false;
                // Unsupported renderers never spin a render-thread retry loop.
                bool pendingMirror = false;
                foreach (KeyValuePair<int, OutputMirror> entry in _mirrors)
                    if (!entry.Value.Allocated || (entry.Value.PendingCopy && _batches.TryGetValue(entry.Key, out Batch? source) && source.Dispatched)) { pendingMirror = true; break; }
                if (_retired.Count > 0 || _retiredMirrors.Count > 0 || (!_disposed && !_failed && (_pending is not null || pendingMirror))) QueueWork();
            }
        }
    }

    private bool InitializeDevice()
    {
        if (_ready) return true;
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd is null)
        {
            lock (_gate) { _failed = true; _pending = null; _status = "CPU fallback: RenderingDevice unavailable"; }
            return false;
        }
        const string shaderPath = "res://materials/vegetation_gpu/ring_gpu_vegetation_indirect.glsl";
        // Fresh workspaces can run before editor import. Exported games instead
        // use the imported shader resource, since raw GLSL may be omitted.
        using RDShaderSpirV spirv = LoadComputeShader(_rd, shaderPath);
        if (!string.IsNullOrWhiteSpace(spirv.CompileErrorCompute)) throw new InvalidOperationException(spirv.CompileErrorCompute);
        _shader = _rd.ShaderCreateFromSpirV(spirv);
        if (!_shader.IsValid) throw new InvalidOperationException("Shader creation failed");
        _pipeline = _rd.ComputePipelineCreate(_shader);
        _budgets = _rd.StorageBufferCreate(20, _zeroBudgets);
        if (!_pipeline.IsValid || !_budgets.IsValid) throw new InvalidOperationException("Compute allocation failed");
        lock (_gate) { _ready = true; _status = "GPU compute + indirect draws"; }
        return true;
    }

    private static RDShaderSpirV LoadComputeShader(RenderingDevice rd, string path)
    {
        if (!FileAccess.FileExists(path))
        {
            RDShaderFile file = GD.Load<RDShaderFile>(path)
                ?? throw new InvalidOperationException("Vegetation compute shader missing");
            return file.GetSpirV();
        }
        string code = FileAccess.GetFileAsString(path);
        using var source = new RDShaderSource { SourceCompute = code.Replace("#[compute]", "", StringComparison.Ordinal) };
        return rd.ShaderCompileSpirVFromSource(source);
    }

    private static int SourceStrideFor(BatchRegistration r) => r.Grass ? GrassFloats : r.TreeCustomData ? TreeCustomFloats : TreeFloats;
    private static int OutputStrideFor(BatchRegistration r) => r.Grass ? GrassFloats : r.TreeDitherData ? TreeCustomFloats : SourceStrideFor(r);

    private static bool Valid(BatchRegistration r)
    {
        int stride = SourceStrideFor(r);
        return r.MultiMesh is not null && r.Mesh is not null && r.Mesh.GetSurfaceCount() > 0
            && r.Capacity is > 0 and <= MaximumRootsPerBatch
            && r.Candidates is { Length: > 0 } && r.Candidates.Length % stride == 0
            && r.Candidates.Length / stride <= MaximumRootsPerBatch
            && r.Capacity <= r.Candidates.Length / stride
            && (r.Metadata is null || r.Metadata.Length == r.Candidates.Length / stride)
            && r.CustomAabb.Size.IsFinite() && r.CustomAabb.Position.IsFinite();
    }

    private static void PrepareResource(BatchRegistration r)
    {
        // MultiMesh caches these fields outside RenderingServer. Configure once
        // here, then the render thread replaces the normal allocation with indirect.
        r.MultiMesh.InstanceCount = 0;
        r.MultiMesh.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
        r.MultiMesh.UseColors = r.Grass;
        r.MultiMesh.UseCustomData = r.Grass || r.TreeCustomData || r.TreeDitherData;
        r.MultiMesh.Mesh = r.Mesh;
        r.MultiMesh.InstanceCount = r.Capacity;
        r.MultiMesh.VisibleInstanceCount = -1;
        r.MultiMesh.CustomAabb = r.CustomAabb;
    }

    private void EnsureBatch(Batch b)
    {
        if (b.Allocated) return;
        RenderingDevice rd = _rd!;
        BatchRegistration r = b.Registration;
        Rid multimesh = r.MultiMesh.GetRid();
        RenderingServer.MultimeshSetMesh(multimesh, default);
        RenderingServer.MultimeshAllocateData(multimesh, 0, RenderingServer.MultimeshTransformFormat.Transform3D, r.Grass, r.Grass || r.TreeCustomData || r.TreeDitherData, false);
        RenderingServer.MultimeshAllocateData(multimesh, r.Capacity, RenderingServer.MultimeshTransformFormat.Transform3D, r.Grass, r.Grass || r.TreeCustomData || r.TreeDitherData, true);
        RenderingServer.MultimeshSetMesh(multimesh, r.Mesh.GetRid());
        RenderingServer.MultimeshSetVisibleInstances(multimesh, -1);
        RenderingServer.MultimeshSetCustomAabb(multimesh, r.CustomAabb);
        b.Output = RenderingServer.MultimeshGetBufferRdRid(multimesh);
        b.Commands = RenderingServer.MultimeshGetCommandBufferRdRid(multimesh);
        b.SurfaceCount = r.Mesh.GetSurfaceCount();
        if (!b.Output.IsValid || !b.Commands.IsValid) throw new InvalidOperationException("Indirect MultiMesh buffers unavailable");
        SharedSource source = AcquireSource(rd, r);
        b.SharedSource = source;
        b.Source = source.Rid;
        b.Metadata = rd.StorageBufferCreate((uint)(b.Count * 16), MetadataBytes(r.Metadata, b.Count));
        b.Parameters = rd.StorageBufferCreate(ParameterBytes, b.ParameterDataBytes);
        b.Flags = rd.StorageBufferCreate((uint)(b.Count * 4));
        b.Counts = rd.StorageBufferCreate((uint)(b.Groups * 4));
        b.Offsets = rd.StorageBufferCreate((uint)((b.Groups + 2) * 4));
        var uniforms = new Godot.Collections.Array<RDUniform>();
        Rid[] resources = { b.Source, b.Metadata, b.Output, b.Commands, b.Parameters, b.Flags, b.Counts, b.Offsets, _budgets };
        for (int i = 0; i < resources.Length; i++)
        {
            if (!resources[i].IsValid) throw new InvalidOperationException("Vegetation buffer allocation failed");
            var uniform = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = i };
            uniform.AddId(resources[i]);
            uniforms.Add(uniform);
        }
        b.Set = rd.UniformSetCreate(uniforms, _shader, 0);
        if (!b.Set.IsValid || !rd.UniformSetIsValid(b.Set)) throw new InvalidOperationException("Vegetation bindings invalid");
        b.Allocated = true;
        b.Bytes = b.Count * 20L + ParameterBytes + (b.Groups * 2L + 2) * 4
            + r.Capacity * b.OutputStride * 4L + b.SurfaceCount * 20L;
        Interlocked.Add(ref _estimatedBytes, b.Bytes);
        Interlocked.Add(ref _outputBytes, r.Capacity * (long)b.OutputStride * 4L);
    }

    private SharedSource AcquireSource(RenderingDevice rd, BatchRegistration registration)
    {
        if (_sharedSources.TryGetValue(registration.Candidates, out SharedSource? existing))
        {
            if (existing.Stride != SourceStrideFor(registration))
                throw new InvalidOperationException("Vegetation source stride mismatch");
            existing.References++;
            return existing;
        }
        Rid rid = rd.StorageBufferCreate((uint)(registration.Candidates.Length * 4), FloatBytes(registration.Candidates));
        if (!rid.IsValid) throw new InvalidOperationException("Vegetation source buffer allocation failed");
        var source = new SharedSource { Candidates = registration.Candidates,
            Stride = SourceStrideFor(registration), Rid = rid, References = 1,
            Bytes = registration.Candidates.LongLength * 4 };
        _sharedSources.Add(source.Candidates, source);
        Interlocked.Increment(ref _sharedSourceCount);
        Interlocked.Add(ref _estimatedBytes, source.Bytes);
        Interlocked.Add(ref _sharedSourceBytes, source.Bytes);
        return source;
    }

    private void Dispatch(FrameRequest frame, int grassFrameRevision)
    {
        RenderingDevice rd = _rd!;
        _work.Clear(); _seen.Clear();
        // Preserve caller order: tree tiles are ordered by priority by the layer,
        // then GPU scans enforce deterministic hard caps.
        lock (_gate)
            foreach (BatchFrameParameters parameters in frame.Batches)
                if (_seen.Add(parameters.BatchId) && _batches.TryGetValue(parameters.BatchId, out Batch? batch)
                    && !batch.Retired && batch.Allocated && batch.Registration.Grass == parameters.Grass)
                    _work.Add((batch, parameters));
        Volatile.Write(ref _lastFrameRequestedBatchCount, _work.Count);
        _selectedWork.Clear();
        bool grassOnly = _work.Count > 0 && AllGrass(_work);
        int maximum = Math.Max(0, frame.MaximumBatchesPerDispatch);
        if (grassOnly && maximum > 0 && _work.Count > maximum)
            SelectBudgetedGrassWork(maximum);
        else
            _selectedWork.AddRange(_work);
        Volatile.Write(ref _lastFrameDispatchedBatchCount, _selectedWork.Count);
        Volatile.Write(ref _deferredBatchCount, _work.Count - _selectedWork.Count);
        foreach (var item in _selectedWork)
        {
            if (!rd.UniformSetIsValid(item.Batch.Set)) throw new InvalidOperationException("Renderer changed a vegetation buffer");
            ParameterData(frame, item.Frame, item.Batch, item.Batch.ParameterWords, item.Batch.ParameterDataBytes);
            rd.BufferUpdate(item.Batch.Parameters, 0, ParameterBytes, item.Batch.ParameterDataBytes);
            Interlocked.Increment(ref _parameterUploads);
        }
        // RD disallows buffer updates inside a compute list.
        rd.BufferUpdate(_budgets, 0, 20, _zeroBudgets);
        if (_selectedWork.Count == 0) { Interlocked.Increment(ref _completedUpdates); return; }
        string diagnosticCategory = grassOnly ? RingPerformanceGpu.GrassCulling
            : _work.TrueForAll(static item => !item.Batch.Registration.Grass) ? RingPerformanceGpu.TreeCulling
            : RingPerformanceGpu.Vegetation;
        using RingPerformanceGpu.GpuScope diagnosticScope = RingPerformanceGpu.BeginScope(diagnosticCategory);
        long list = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(list, _pipeline);
        if (_groupGrassPhases && grassOnly)
        {
            // Grass does not read the shared LOD budget. Each selected output owns
            // its scan buffers, so its same phase can run independently before the
            // one barrier needed to publish that phase to the following pass.
            for (uint phase = 0; phase < 3; phase++)
            {
                foreach (var item in _selectedWork)
                {
                    Batch batch = item.Batch;
                    if (batch.Retired) continue;
                    rd.ComputeListBindUniformSet(list, batch.Set, 0);
                    WritePush(phase, (uint)batch.Count, (uint)batch.Registration.Capacity,
                        (uint)batch.SurfaceCount, 1, 0);
                    rd.ComputeListSetPushConstant(list, _push, 32);
                    rd.ComputeListDispatch(list, phase == 1 ? 1 : (uint)batch.Groups, 1, 1);
                    Interlocked.Increment(ref _dispatchCount);
                }
                AddBarrier(rd, list);
            }
        }
        else if (OptimizeTreeDispatches && _work.TrueForAll(static item => !item.Batch.Registration.Grass))
        {
            // Tree LOD budgets are shared. Keep protected LOD0 work ahead of the
            // ordinary sweep, then retain caller order for each serial budget claim.
            DispatchOptimizedTreeSweep(rd, list, protectedSweep: true);
            DispatchOptimizedTreeSweep(rd, list, protectedSweep: false);
        }
        else
        {
            for (uint sweep = 0; sweep < 2; sweep++)
            {
                foreach (var item in _selectedWork)
                {
                    Batch batch = item.Batch;
                    bool protectedSweep = sweep == 0;
                    bool runProtected = HasProtectedSweep(item);
                    if (batch.Retired || (protectedSweep && !runProtected)) continue;
                    rd.ComputeListBindUniformSet(list, batch.Set, 0);
                    for (uint phase = 0; phase < 3; phase++)
                    {
                        WritePush(phase, (uint)batch.Count, (uint)batch.Registration.Capacity,
                        (uint)batch.SurfaceCount, sweep, ProtectedBaseValid(item, sweep == 0));
                        rd.ComputeListSetPushConstant(list, _push, 32);
                        rd.ComputeListDispatch(list, phase == 1 ? 1 : (uint)batch.Groups, 1, 1);
                        AddBarrier(rd, list);
                        Interlocked.Increment(ref _dispatchCount);
                    }
                }
            }
        }
        rd.ComputeListEnd();
        foreach (var item in _selectedWork)
        {
            if (item.Batch.Retired) continue;
            item.Batch.Dispatched = true;
            Volatile.Write(ref item.Batch.CompletedVisibilityRevision, item.Frame.VisibilityRevision);
            if (grassOnly) item.Batch.LastGrassFrameRevision = grassFrameRevision;
        }
        CopyOutputMirrors(rd);
        Interlocked.Increment(ref _completedUpdates);
    }

    private void EnsureMirror(Batch source)
    {
        lock (_gate)
        {
        _mirrors.TryGetValue(source.Id, out OutputMirror? mirror);
        if (mirror is null || mirror.Allocated) return;
        Rid multimesh = mirror.Target.GetRid();
        RenderingServer.MultimeshSetMesh(multimesh, default);
        bool custom = source.Registration.Grass || source.Registration.TreeCustomData || source.Registration.TreeDitherData;
        RenderingServer.MultimeshAllocateData(multimesh, 0, RenderingServer.MultimeshTransformFormat.Transform3D, source.Registration.Grass, custom, false);
        RenderingServer.MultimeshAllocateData(multimesh, source.Registration.Capacity, RenderingServer.MultimeshTransformFormat.Transform3D, source.Registration.Grass, custom, true);
        RenderingServer.MultimeshSetMesh(multimesh, mirror.Mesh.GetRid());
        RenderingServer.MultimeshSetVisibleInstances(multimesh, -1);
        RenderingServer.MultimeshSetCustomAabb(multimesh, source.Registration.CustomAabb);
        mirror.Output = RenderingServer.MultimeshGetBufferRdRid(multimesh);
        mirror.Commands = RenderingServer.MultimeshGetCommandBufferRdRid(multimesh);
        mirror.SurfaceCount = mirror.Mesh.GetSurfaceCount();
        if (!mirror.Output.IsValid || !mirror.Commands.IsValid) throw new InvalidOperationException("Indirect mirror buffers unavailable");
        mirror.Allocated = true;
        mirror.Bytes = source.Registration.Capacity * (long)source.OutputStride * 4L + mirror.SurfaceCount * 20L;
        Interlocked.Add(ref _estimatedBytes, mirror.Bytes);
        Interlocked.Add(ref _outputBytes, source.Registration.Capacity * (long)source.OutputStride * 4L);
        }
    }

    private void CopyOutputMirrors(RenderingDevice rd)
    {
        lock (_gate)
        {
        foreach (KeyValuePair<int, OutputMirror> pair in _mirrors)
        {
            if (!_batches.TryGetValue(pair.Key, out Batch? source) || source.Retired || !source.Dispatched) continue;
            OutputMirror mirror = pair.Value;
            if (!mirror.Allocated || !source.Output.IsValid || !source.Commands.IsValid) continue;
            rd.BufferCopy(source.Output, mirror.Output, 0, 0, (uint)(source.Registration.Capacity * source.OutputStride * 4));
            for (int surface = 0; surface < mirror.SurfaceCount; surface++)
                rd.BufferCopy(source.Commands, mirror.Commands, 4, (uint)(surface * 20 + 4), 4);
            mirror.PendingCopy = false;
            mirror.Ready = true;
        }
        }
    }

    /// <summary>Called while holding <see cref="_gate"/>. A budgeted stationary
    /// grass frame goes idle after each currently requested output has processed
    /// its current camera/list/membership state once.</summary>
    private bool GrassFrameComplete(FrameRequest request, int revision)
    {
        if (revision <= 0) return false;
        foreach (BatchFrameParameters parameters in request.Batches)
            if (!_batches.TryGetValue(parameters.BatchId, out Batch? batch) || batch.Retired
                || !batch.Allocated || batch.LastGrassFrameRevision != revision)
                return false;
        return true;
    }

    /// <summary>Chooses a bounded grass subset without modifying the indirect output
    /// of deferred batches. First exposure is favored, then the stable caller order
    /// advances round-robin so a continuously requested batch is reached within
    /// ceil(requested/budget) dispatches.</summary>
    private void SelectBudgetedGrassWork(int maximum)
    {
        int count = _work.Count;
        if (count == 0 || maximum <= 0) return;
        maximum = Math.Min(maximum, count);
        int start = PositiveModulo(_grassDispatchCursor, count);
        var selected = new HashSet<int>();
        // New, replaced, or visibility-stale outputs are refreshed before an
        // already-current batch receives another pass. The cursor determines order.
        for (int offset = 0; offset < count && _selectedWork.Count < maximum; offset++)
        {
            int index = (start + offset) % count;
            var item = _work[index];
            if ((!item.Batch.Dispatched || Volatile.Read(ref item.Batch.CompletedVisibilityRevision) != item.Frame.VisibilityRevision)
                && selected.Add(item.Batch.Id)) _selectedWork.Add(item);
        }
        int cursor = start;
        for (int offset = 0; offset < count && _selectedWork.Count < maximum; offset++)
        {
            int index = (start + offset) % count;
            var item = _work[index];
            if (selected.Add(item.Batch.Id))
            {
                _selectedWork.Add(item);
                cursor = (index + 1) % count;
            }
        }
        // When initial exposure consumed the entire allowance, advance beyond the
        // last selected item. This keeps a changing membership list safe while
        // avoiding a permanent preference for its first element.
        if (_selectedWork.Count > 0 && cursor == start)
        {
            int last = _work.IndexOf(_selectedWork[^1]);
            cursor = last >= 0 ? (last + 1) % count : start;
        }
        _grassDispatchCursor = cursor;
    }

    private static int PositiveModulo(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static bool AllGrass(IReadOnlyList<BatchFrameParameters> batches)
    {
        for (int i = 0; i < batches.Count; i++) if (!batches[i].Grass) return false;
        return true;
    }

    private static bool AllGrass(List<(Batch Batch, BatchFrameParameters Frame)> batches)
    {
        for (int i = 0; i < batches.Count; i++) if (!batches[i].Batch.Registration.Grass) return false;
        return true;
    }

    private static void ParameterData(FrameRequest frame, BatchFrameParameters p, Batch batch, float[] data, byte[] bytes)
    {
        Array.Clear(data);
        data[0] = frame.CameraWorld.X; data[1] = frame.CameraWorld.Y; data[2] = frame.CameraWorld.Z;
        data[3] = Finite(p.NearDistance, 0); data[4] = Math.Max(data[3], Finite(p.FarDistance, 1));
        data[5] = Math.Max(.001f, Finite(p.TransitionDistance, 1));
        data[6] = Mathf.Clamp(Finite(p.NearDensity, 1), 0, 1); data[7] = Mathf.Clamp(Finite(p.FarDensity, 1), 0, 1);
        data[8] = Mathf.Clamp(Finite(p.TreeRankGate, 1), 0, 1); data[9] = Math.Max(0, Finite(p.RadiusGuard, 0));
        data[10] = p.Grass ? 1 : 0; data[11] = Math.Clamp(p.LodPass, 0, 4); data[12] = p.EnableLod ? 1 : 0;
        data[13] = p.Grass ? Math.Min(6, p.GrassFrustumPlanes?.Length ?? 0) : 0;
        data[14] = !p.Grass && batch.Registration.TreeCustomData ? 1 : 0;
        data[15] = p.LodPass >= 0 && p.LodPass < frame.LodBudgets.Length ? Math.Min(frame.LodBudgets[p.LodPass], MaximumRootsPerBatch) : 0;
        int offset = 16;
        Vector3 value = p.LocalToWorld.Basis.X;
        data[offset++] = value.X; data[offset++] = value.Y; data[offset++] = value.Z;
        value = p.LocalToWorld.Basis.Y;
        data[offset++] = value.X; data[offset++] = value.Y; data[offset++] = value.Z;
        value = p.LocalToWorld.Basis.Z;
        data[offset++] = value.X; data[offset++] = value.Y; data[offset++] = value.Z;
        value = p.LocalToWorld.Origin;
        data[offset++] = value.X; data[offset++] = value.Y; data[offset++] = value.Z;
        for (int i = 0; i < (int)data[13]; i++)
        {
            Vector4 plane = p.GrassFrustumPlanes![i];
            data[offset++] = plane.X; data[offset++] = plane.Y; data[offset++] = plane.Z; data[offset++] = plane.W;
        }
        if (p.TreeCameraCanonicalLocal is Vector2 canonical)
        { data[52] = canonical.X; data[53] = canonical.Y; data[54] = 1; }
        data[55] = p.DistanceBasedTreeProtection ? 1 : 0;
        data[56] = Finite(p.TreeFadeInStart, -1); data[57] = Finite(p.TreeFadeInEnd, -1);
        data[58] = Finite(p.TreeFadeOutStart, -1); data[59] = Finite(p.TreeFadeOutEnd, -1);
        data[60] = batch.Registration.TreeDitherData ? 1 : 0;
        // Grass-only: local mesh radius around its root. The shader combines
        // this with the source and parent transforms before frustum rejection.
        data[61] = p.Grass ? batch.PrototypeHorizontalRadius : 0;
        Buffer.BlockCopy(data, 0, bytes, 0, ParameterBytes);
    }

    private static float PrototypeHorizontalRadius(Mesh mesh)
    {
        if (mesh is null) return 0;
        Aabb aabb = mesh.GetAabb();
        if (!aabb.Position.IsFinite() || !aabb.Size.IsFinite()) return 0;
        Vector3 center = aabb.GetCenter();
        Vector3 half = aabb.Size * .5f;
        float x = Math.Max(Math.Abs(center.X - half.X), Math.Abs(center.X + half.X));
        float z = Math.Max(Math.Abs(center.Z - half.Z), Math.Abs(center.Z + half.Z));
        float radius = MathF.Sqrt(x * x + z * z);
        return float.IsFinite(radius) && radius >= 0 ? radius : 0;
    }

    private void WritePush(uint phase, uint count, uint capacity, uint surfaces, uint sweep, uint protectedBaseValid)
    {
        BitConverter.TryWriteBytes(_push.AsSpan(0, 4), phase);
        BitConverter.TryWriteBytes(_push.AsSpan(4, 4), count);
        BitConverter.TryWriteBytes(_push.AsSpan(8, 4), capacity);
        BitConverter.TryWriteBytes(_push.AsSpan(12, 4), surfaces);
        BitConverter.TryWriteBytes(_push.AsSpan(16, 4), sweep);
        BitConverter.TryWriteBytes(_push.AsSpan(20, 4), protectedBaseValid);
        _push.AsSpan(24, 8).Clear();
    }

    private void DispatchOptimizedTreeSweep(RenderingDevice rd, long list, bool protectedSweep)
    {
        bool any = false;
        foreach (var item in _selectedWork)
        {
            Batch batch = item.Batch;
            bool eligible = !batch.Retired && (!protectedSweep
                || HasProtectedSweep(item));
            if (!eligible || batch.Count <= 64) continue;
            any = true;
            rd.ComputeListBindUniformSet(list, batch.Set, 0);
            WritePush(0, (uint)batch.Count, (uint)batch.Registration.Capacity, (uint)batch.SurfaceCount,
                protectedSweep ? 0u : 1u, ProtectedBaseValid(item, protectedSweep));
            rd.ComputeListSetPushConstant(list, _push, 32);
            rd.ComputeListDispatch(list, (uint)batch.Groups, 1, 1);
            Interlocked.Increment(ref _dispatchCount);
        }
        if (any) AddBarrier(rd, list);

        // Phase 1 claims the shared LOD budget and therefore stays serial in the
        // exact request order. Small batches do all three phases inside one group.
        foreach (var item in _selectedWork)
        {
            Batch batch = item.Batch;
            bool eligible = !batch.Retired && (!protectedSweep
                || HasProtectedSweep(item));
            if (!eligible) continue;
            rd.ComputeListBindUniformSet(list, batch.Set, 0);
            uint phase = batch.Count <= 64 ? 3u : 1u;
            WritePush(phase, (uint)batch.Count, (uint)batch.Registration.Capacity, (uint)batch.SurfaceCount,
                protectedSweep ? 0u : 1u, ProtectedBaseValid(item, protectedSweep));
            rd.ComputeListSetPushConstant(list, _push, 32);
            rd.ComputeListDispatch(list, 1, 1, 1);
            Interlocked.Increment(ref _dispatchCount);
            AddBarrier(rd, list);
        }

        any = false;
        foreach (var item in _selectedWork)
        {
            Batch batch = item.Batch;
            bool eligible = !batch.Retired && (!protectedSweep
                || HasProtectedSweep(item));
            if (!eligible || batch.Count <= 64) continue;
            any = true;
            rd.ComputeListBindUniformSet(list, batch.Set, 0);
            WritePush(2, (uint)batch.Count, (uint)batch.Registration.Capacity, (uint)batch.SurfaceCount,
                protectedSweep ? 0u : 1u, ProtectedBaseValid(item, protectedSweep));
            rd.ComputeListSetPushConstant(list, _push, 32);
            rd.ComputeListDispatch(list, (uint)batch.Groups, 1, 1);
            Interlocked.Increment(ref _dispatchCount);
        }
        if (any) AddBarrier(rd, list);
    }

    private void AddBarrier(RenderingDevice rd, long list)
    {
        rd.ComputeListAddBarrier(list);
        Interlocked.Increment(ref _barrierCount);
    }

    private static uint ProtectedBaseValid((Batch Batch, BatchFrameParameters Frame) item, bool protectedSweep)
        => !protectedSweep && item.Batch.UploadedHasProtectedRoots
            && (Math.Clamp(item.Frame.LodPass, 0, 4) == 0 || item.Frame.DistanceBasedTreeProtection) ? 1u : 0u;

    private static bool HasProtectedSweep((Batch Batch, BatchFrameParameters Frame) item)
        => !item.Batch.Registration.Grass && item.Batch.UploadedHasProtectedRoots
            && (Math.Clamp(item.Frame.LodPass, 0, 4) == 0 || item.Frame.DistanceBasedTreeProtection);

    private static bool HasProtectedRoots(Vector4[]? metadata)
    {
        if (metadata is not null)
            for (int i = 0; i < metadata.Length; i++) if (metadata[i].Y > .5f) return true;
        return false;
    }

    private static bool FrameEquals(FrameRequest a, FrameRequest b)
    {
        if (!a.CameraWorld.Equals(b.CameraWorld) || a.MaximumBatchesPerDispatch != b.MaximumBatchesPerDispatch || a.LodBudgets.Length != b.LodBudgets.Length || a.Batches.Count != b.Batches.Count) return false;
        for (int i = 0; i < a.LodBudgets.Length; i++) if (a.LodBudgets[i] != b.LodBudgets[i]) return false;
        for (int i = 0; i < a.Batches.Count; i++)
        {
            BatchFrameParameters x = a.Batches[i], y = b.Batches[i];
            if (x.BatchId != y.BatchId || !x.LocalToWorld.Equals(y.LocalToWorld) || x.Grass != y.Grass || x.LodPass != y.LodPass
                || x.NearDistance != y.NearDistance || x.FarDistance != y.FarDistance || x.TransitionDistance != y.TransitionDistance
                || x.NearDensity != y.NearDensity || x.FarDensity != y.FarDensity || x.EnableLod != y.EnableLod
                || x.TreeRankGate != y.TreeRankGate || x.ProtectedSelection != y.ProtectedSelection || x.RadiusGuard != y.RadiusGuard
                || x.VisibilityRevision != y.VisibilityRevision || x.DistanceBasedTreeProtection != y.DistanceBasedTreeProtection
                || x.TreeFadeInStart != y.TreeFadeInStart || x.TreeFadeInEnd != y.TreeFadeInEnd
                || x.TreeFadeOutStart != y.TreeFadeOutStart || x.TreeFadeOutEnd != y.TreeFadeOutEnd
                || !Nullable.Equals(x.TreeCameraCanonicalLocal, y.TreeCameraCanonicalLocal) || !PlanesEqual(x.GrassFrustumPlanes, y.GrassFrustumPlanes)) return false;
        }
        return true;
    }

    private static bool PlanesEqual(Vector4[]? a, Vector4[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return false;
        return true;
    }

    private void DrainRetired()
    {
        while (true)
        {
            Batch batch;
            lock (_gate) { if (_retired.Count == 0) return; batch = _retired.Dequeue(); }
            FreeBatch(batch);
        }
    }

    private void DrainRetiredMirrors()
    {
        if (_rd is null) return;
        while (true)
        {
            OutputMirror mirror;
            lock (_gate) { if (_retiredMirrors.Count == 0) return; mirror = _retiredMirrors.Dequeue(); }
            if (mirror.Commands.IsValid)
            {
                // Godot owns the output RID, but command RIDs need the same detach
                // workaround as regular indirect MultiMeshes before they are freed.
                RenderingServer.MultimeshAllocateData(mirror.Target.GetRid(), 0,
                    RenderingServer.MultimeshTransformFormat.Transform3D, mirror.Grass, mirror.Custom, false);
                Free(ref mirror.Commands);
            }
            mirror.Output = default;
            if (mirror.Bytes != 0)
            {
                Interlocked.Add(ref _estimatedBytes, -mirror.Bytes);
                // Bytes includes the command buffer after the output allocation.
                long output = mirror.Bytes - mirror.SurfaceCount * 20L;
                Interlocked.Add(ref _outputBytes, -output);
                mirror.Bytes = 0;
            }
            lock (_gate) _retiringMirrorTargets.Remove(mirror.Target);
        }
    }

    private void FreeBatch(Batch batch)
    {
        if (_rd is null) return;
        Free(ref batch.Set, uniform: true);
        Free(ref batch.Offsets); Free(ref batch.Counts); Free(ref batch.Flags);
        Free(ref batch.Parameters); Free(ref batch.Metadata);
        if (batch.Commands.IsValid)
        {
            // Godot 4.7.2 drops the command RID during reallocation without
            // freeing it (verified by vegetation_indirect_api_probe.gd). Detach
            // it before explicit retirement. The renderer owns Output.
            BatchRegistration r = batch.Registration;
                RenderingServer.MultimeshAllocateData(r.MultiMesh.GetRid(), 0,
                RenderingServer.MultimeshTransformFormat.Transform3D, r.Grass, r.Grass || r.TreeCustomData || r.TreeDitherData, false);
            Free(ref batch.Commands);
        }
        batch.Output = default;
        Interlocked.Add(ref _estimatedBytes, -batch.Bytes);
        if (batch.Allocated)
        Interlocked.Add(ref _outputBytes, -batch.Registration.Capacity * (long)batch.OutputStride * 4L);
        batch.Bytes = 0;
        batch.Allocated = false;
        ReleaseSource(batch);
    }

    private void ReleaseSource(Batch batch)
    {
        SharedSource? source = batch.SharedSource;
        batch.SharedSource = null;
        batch.Source = default;
        if (source is null || --source.References > 0) return;
        _sharedSources.Remove(source.Candidates);
        Interlocked.Decrement(ref _sharedSourceCount);
        Free(ref source.Rid);
        Interlocked.Add(ref _estimatedBytes, -source.Bytes);
        Interlocked.Add(ref _sharedSourceBytes, -source.Bytes);
    }

    private void Free(ref Rid rid, bool uniform = false)
    {
        if (rid.IsValid && (!uniform || _rd!.UniformSetIsValid(rid))) _rd!.FreeRid(rid);
        rid = default;
    }

    /// <summary>Fixtures only; gameplay never reads instance buffers.</summary>
    public void RequestReadbackForTests(int id, Action<uint[], float[]> callback)
    {
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            uint[] commands = Array.Empty<uint>(); float[] instances = Array.Empty<float>();
            Batch? batch;
            lock (_gate) _batches.TryGetValue(id, out batch);
            if (_rd is not null && batch is { Retired: false, Dispatched: true } && !_disposed)
            {
                byte[] commandBytes = _rd.BufferGetData(batch.Commands);
                commands = new uint[commandBytes.Length / 4]; Buffer.BlockCopy(commandBytes, 0, commands, 0, commandBytes.Length);
                byte[] rows = _rd.BufferGetData(batch.Output);
                instances = new float[rows.Length / 4]; Buffer.BlockCopy(rows, 0, instances, 0, rows.Length);
            }
            Callable.From(() => callback(commands, instances)).CallDeferred();
        }));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _ready = false; _pending = null;
            foreach (Batch batch in _batches.Values) { batch.Retired = true; _retired.Enqueue(batch); }
            _batches.Clear();
            foreach (OutputMirror mirror in _mirrors.Values) { mirror.Retired = true; _retiredMirrors.Enqueue(mirror); }
            _mirrors.Clear();
        }
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            DrainRetired();
            DrainRetiredMirrors();
            if (_rd is not null) { Free(ref _budgets); Free(ref _pipeline); Free(ref _shader); }
        }));
        GC.SuppressFinalize(this);
    }

    private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;
    private static byte[] FloatBytes(float[] data)
    { var bytes = new byte[data.Length * 4]; Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length); return bytes; }
    private static byte[] MetadataBytes(Vector4[]? metadata, int count)
    {
        var data = new float[count * 4];
        if (metadata is not null)
            for (int i = 0; i < count; i++)
            { data[i * 4] = metadata[i].X; data[i * 4 + 1] = metadata[i].Y; data[i * 4 + 2] = metadata[i].Z; data[i * 4 + 3] = metadata[i].W; }
        return FloatBytes(data);
    }
}
