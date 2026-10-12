using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using Godot.Collections;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>
/// Bounded CPU preparation for gameplay Terrain3D windows. Worker jobs retain
/// only sampler copies and immutable request data; publication remains owned by
/// the caller on the main thread.
/// </summary>
public sealed class RingTerrain3DGameplayStream : IDisposable
{
    private const int TileSide = 128, WindowSide = 384, Spacing = 2;
    private const int TileBytes = TileSide * TileSide * 8;
    // Preserve Godot 4.7's byte representation in cached worlds. Godot 4.8
    // Image.SetPixel rounds RGBA8 channels; these bulk buffers retain truncation.
    internal static byte QuantizeBiomeChannel(float value) => (byte)(Math.Clamp(value, 0f, 1f) * 255f);
    private readonly RingTerrainSampler[] _samplers;
    private readonly bool[] _leased;
    private readonly float _minimumHeight, _maximumHeight;
    private readonly int _width;
    private readonly long _circumference;
    private readonly System.Collections.Generic.Dictionary<TileKey, LinkedListNode<CachedTile>> _cache = new();
    private readonly System.Collections.Generic.Dictionary<TileKey, CachedTile> _workingTiles = new();
    private readonly LinkedList<CachedTile> _lru = new();
    private readonly List<TileJob> _jobs = new();
    private PackJob? _packer;
    private PreparedWindow? _ready;
    private RequestState? _request;
    private bool _disposed;
    private int _workers = 2, _cacheLimitBytes = 8 * 1024 * 1024, _cacheBytes;
    private long _submitted, _completed, _cancelled, _highWaterJobs, _hits, _misses, _readyRevision = -1, _revisionWatermark = -1;
    private double _lastGenerationMilliseconds;
    private string _error = string.Empty;

    public RingTerrain3DGameplayStream(RingTerrainSampler sampler, float minHeight, float maxHeight, int widthMeters, long circumferenceMeters)
    {
        if (sampler == null || !float.IsFinite(minHeight) || !float.IsFinite(maxHeight) || minHeight > maxHeight ||
            widthMeters <= 0 || circumferenceMeters <= 0 || circumferenceMeters % Spacing != 0)
            throw new ArgumentException("Gameplay stream definition is invalid.");
        _minimumHeight = minHeight; _maximumHeight = maxHeight; _width = widthMeters; _circumference = circumferenceMeters;
        _samplers = new RingTerrainSampler[4]; _leased = new bool[4];
        try { for (int i = 0; i < _samplers.Length; i++) _samplers[i] = sampler.CreateIndependentCopy(); }
        catch { for (int i = 0; i < _samplers.Length; i++) _samplers[i]?.Dispose(); throw; }
    }

    public string Error => _error;
    public double LastGenerationMilliseconds => _lastGenerationMilliseconds;
    public void Configure(int workers, int cacheMiB)
    {
        ThrowIfDisposed();
        _workers = Math.Clamp(workers, 1, 4);
        _cacheLimitBytes = Math.Clamp(cacheMiB, 1, 64) * 1024 * 1024;
        TrimCache();
    }

    /// <summary>Seeds the nine startup tiles from the synchronously sampled gameplay baseline.</summary>
    public void PrimeBaselineWindow(RingSurfaceFrame frame, float[] heights, byte[] biomes)
    {
        ThrowIfDisposed();
        if (_jobs.Count != 0 || _request != null || _packer != null)
            throw new InvalidOperationException("Baseline cache can only be primed before streaming begins.");
        if (heights == null || heights.Length != WindowSide * WindowSide || biomes == null || biomes.Length != WindowSide * WindowSide * 4
            || frame.CircumferenceMeters != _circumference || frame.SpacingMeters != Spacing || frame.AcrossDirection is not (1 or -1)
            || !double.IsFinite(frame.RadiusMeters) || !float.IsFinite(frame.AnchorHeight) || frame.AnchorAroundMeters % Spacing != 0 || frame.AnchorAcrossMeters % Spacing != 0)
        {
            throw new ArgumentException("Gameplay baseline cache payload is invalid.");
        }
        for (int i = 0; i < heights.Length; i++)
            if (!float.IsFinite(heights[i]) || heights[i] < _minimumHeight || heights[i] > _maximumHeight)
                throw new ArgumentException("Gameplay baseline heights are outside the stream contract.");

        for (int regionZ = 0; regionZ < 3; regionZ++)
        for (int regionX = 0; regionX < 3; regionX++)
        {
            var tileHeights = new float[TileSide * TileSide];
            var tileBiomes = new byte[TileSide * TileSide * 4];
            for (int row = 0; row < TileSide; row++)
            {
                int source = (regionZ * TileSide + row) * WindowSide + regionX * TileSide;
                Buffer.BlockCopy(heights, source * sizeof(float), tileHeights, row * TileSide * sizeof(float), TileSide * sizeof(float));
                Buffer.BlockCopy(biomes, source * 4, tileBiomes, row * TileSide * 4, TileSide * 4);
            }
            TileKey key = MakeKey(frame, regionX - 1, regionZ - 1);
            PutCache(key, new CachedTile(key, tileHeights, tileBiomes));
        }
    }

    public void Request(RingSurfaceFrame frame, RingSurfaceHeightEdit[] edits, ulong editRevision, long revision, int tileCells)
    {
        ThrowIfDisposed();
        if (revision < 0 || revision <= _revisionWatermark || tileCells is not (16 or 32) || frame.AcrossDirection is not (1 or -1) ||
            frame.CircumferenceMeters != _circumference || frame.RadiusMeters <= 0 || frame.SpacingMeters != Spacing ||
            !float.IsFinite(frame.AnchorHeight) || frame.AnchorHeight < _minimumHeight || frame.AnchorHeight > _maximumHeight ||
            frame.AnchorAcrossMeters < 0 || frame.AnchorAcrossMeters >= _width)
        { _error = "Gameplay stream request is invalid."; return; }
        foreach (RingSurfaceHeightEdit edit in edits ?? System.Array.Empty<RingSurfaceHeightEdit>())
            if (!float.IsFinite(edit.Height) || edit.Height < _minimumHeight || edit.Height > _maximumHeight || edit.AroundMeters % Spacing != 0 || edit.AcrossMeters % Spacing != 0 || edit.AcrossMeters < 0 || edit.AcrossMeters >= _width)
            { _error = "Gameplay stream edits are invalid."; return; }
        Cancel();
        _workingTiles.Clear();
        _request = new RequestState(frame, edits == null ? System.Array.Empty<RingSurfaceHeightEdit>() : (RingSurfaceHeightEdit[])edits.Clone(), editRevision, revision, tileCells);
        _revisionWatermark = revision;
        _error = string.Empty;
        QueueTiles();
    }

    public void Poll()
    {
        if (_disposed) return;
        for (int i = _jobs.Count - 1; i >= 0; i--)
        {
            TileJob job = _jobs[i];
            if (!WorkerThreadPool.IsTaskCompleted(job.TaskId)) continue;
            WorkerThreadPool.WaitForTaskCompletion(job.TaskId);
            _jobs.RemoveAt(i); _leased[job.SamplerIndex] = false;
            if (job.Error.Length > 0) { if (_request != null && job.Revision == _request.Revision && !job.Cancelled) FailRequest(job.Error); continue; }
            _completed++;
            if (!job.Cancelled && _request != null && job.Revision == _request.Revision)
            {
                var tile = new CachedTile(job.Key, job.Heights!, job.Biomes!);
                _workingTiles[job.Key] = tile;
                PutCache(job.Key, tile);
            }
        }
        if (_request != null && _jobs.Count == 0 && _packer == null && _ready == null && AllTilesAvailable(_request))
            QueuePacker(_request);
        if (_packer != null && WorkerThreadPool.IsTaskCompleted(_packer.TaskId))
        {
            PackJob finished = _packer; _packer = null;
            WorkerThreadPool.WaitForTaskCompletion(finished.TaskId);
            if (finished.Error.Length > 0) { if (_request != null && finished.Revision == _request.Revision && !finished.Cancelled) FailRequest(finished.Error); finished.Dispose(); }
            else if (!finished.Cancelled && _request != null && finished.Revision == _request.Revision)
            {
                _ready?.Dispose(); _ready = finished.TakeResult(); _readyRevision = finished.Revision;
                _lastGenerationMilliseconds = (Stopwatch.GetTimestamp() - _request.StartTimestamp) * 1000.0 / Stopwatch.Frequency;
                _request = null;
            }
            else finished.Dispose();
        }
        if (_request != null && _jobs.Count == 0 && _packer == null && _ready == null) QueueTiles();
    }

    public bool TryTakeReady(out PreparedWindow result)
    {
        if (_ready == null) { result = null!; return false; }
        result = _ready; _ready = null; return true;
    }

    public void Cancel()
    {
        foreach (TileJob job in _jobs) job.Cancelled = true;
        if (_packer != null) _packer.Cancelled = true;
        _cancelled += _jobs.Count + (_packer == null ? 0 : 1);
        _request = null;
        _workingTiles.Clear();
        _ready?.Dispose(); _ready = null;
    }

    public Dictionary Diagnostics => new()
    {
        ["workers"] = _workers, ["cache_bytes"] = _cacheBytes, ["cache_limit_bytes"] = _cacheLimitBytes,
        ["cache_hits"] = _hits, ["cache_misses"] = _misses, ["submitted"] = _submitted,
        ["completed"] = _completed, ["cancelled"] = _cancelled, ["highwater_jobs"] = _highWaterJobs,
        ["last_generation_ms"] = _lastGenerationMilliseconds, ["ready_revision"] = _readyRevision,
        ["pending_jobs"] = _jobs.Count, ["packing"] = _packer != null, ["error"] = _error
    };

    private void QueueTiles()
    {
        if (_request == null) return;
        for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++)
        {
            TileKey key = MakeKey(_request.Frame, x, z);
            if (_workingTiles.ContainsKey(key)) continue;
            if (_cache.TryGetValue(key, out LinkedListNode<CachedTile>? cached)) { _workingTiles[key] = cached.Value; Touch(key); _hits++; continue; }
            if (_jobs.Exists(job => job.Key.Equals(key))) continue;
            int sampler = FindSampler();
            if (sampler < 0 || ActiveHandleCount >= _workers) return;
            _misses++; _leased[sampler] = true;
            var job = new TileJob(_samplers[sampler], sampler, key, _request.Revision, _minimumHeight, _maximumHeight, _width, _circumference);
            job.TaskId = WorkerThreadPool.AddTask(Callable.From(job.Run), false, "RingTerrain3DGameplay tile");
            _jobs.Add(job); _submitted++; _highWaterJobs = Math.Max(_highWaterJobs, ActiveHandleCount);
        }
    }

    private void QueuePacker(RequestState request)
    {
        var tiles = new CachedTile[9]; int i = 0;
        for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++)
        {
            TileKey key = MakeKey(request.Frame, x, z);
            if (_workingTiles.TryGetValue(key, out CachedTile? retained)) { tiles[i++] = retained; continue; }
            if (!_cache.TryGetValue(key, out LinkedListNode<CachedTile>? node)) return;
            Touch(node.Value.Key); tiles[i++] = node.Value;
        }
        _packer = new PackJob(request, tiles, _minimumHeight, _maximumHeight, _width);
        _packer.TaskId = WorkerThreadPool.AddTask(Callable.From(_packer.Run), false, "RingTerrain3DGameplay pack");
        _submitted++; _highWaterJobs = Math.Max(_highWaterJobs, ActiveHandleCount);
    }

    private int ActiveHandleCount => _jobs.Count + (_packer == null ? 0 : 1);

    private int FindSampler() { for (int i = 0; i < _leased.Length; i++) if (!_leased[i]) return i; return -1; }
    private TileKey MakeKey(RingSurfaceFrame frame, int x, int z) =>
        new(Wrap(frame.AnchorAroundMeters + x * TileSide * Spacing), frame.AnchorAcrossMeters + frame.AcrossDirection * z * TileSide * Spacing, frame.AcrossDirection);
    private long Wrap(long around) { long value = around % _circumference; return value < 0 ? value + _circumference : value; }
    private void PutCache(TileKey key, CachedTile tile)
    {
        if (_cache.TryGetValue(key, out LinkedListNode<CachedTile>? old)) { _lru.Remove(old); _cache.Remove(key); _cacheBytes -= TileBytes; }
        _cache[key] = _lru.AddFirst(tile); _cacheBytes += TileBytes; TrimCache();
    }
    private void Touch(TileKey key) { if (_cache.TryGetValue(key, out LinkedListNode<CachedTile>? node)) { _lru.Remove(node); _lru.AddFirst(node); } }
    private void TrimCache() { while (_cacheBytes > _cacheLimitBytes && _lru.Last != null) { CachedTile tile = _lru.Last.Value; _cache.Remove(tile.Key); _lru.RemoveLast(); _cacheBytes -= TileBytes; } }
    private bool AllTilesAvailable(RequestState request) { for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++) if (!_workingTiles.ContainsKey(MakeKey(request.Frame, x, z))) return false; return true; }
    private void FailRequest(string error) { _error = error; foreach (TileJob job in _jobs) job.Cancelled = true; if (_packer != null) _packer.Cancelled = true; _request = null; _workingTiles.Clear(); }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true; Cancel();
        foreach (TileJob job in _jobs) { WorkerThreadPool.WaitForTaskCompletion(job.TaskId); _leased[job.SamplerIndex] = false; }
        _jobs.Clear();
        if (_packer != null) { WorkerThreadPool.WaitForTaskCompletion(_packer.TaskId); _packer.Dispose(); _packer = null; }
        _ready?.Dispose(); _ready = null;
        foreach (RingTerrainSampler sampler in _samplers) sampler.Dispose();
        _cache.Clear(); _lru.Clear();
    }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(RingTerrain3DGameplayStream)); }

    private readonly record struct TileKey(long Around, int Across, int Direction);
    private sealed class CachedTile { public CachedTile(TileKey key, float[] h, byte[] b) { Key = key; Heights = h; Biomes = b; } public TileKey Key; public float[] Heights; public byte[] Biomes; }
    private sealed class RequestState { public RequestState(RingSurfaceFrame frame, RingSurfaceHeightEdit[] edits, ulong editRevision, long revision, int cells) { Frame = frame; Edits = edits; EditRevision = editRevision; Revision = revision; TileCells = cells; StartTimestamp = Stopwatch.GetTimestamp(); } public RingSurfaceFrame Frame; public RingSurfaceHeightEdit[] Edits; public ulong EditRevision; public long Revision; public int TileCells; public long StartTimestamp; }

    private sealed class TileJob
    {
        private readonly RingTerrainSampler _sampler; private readonly float _min, _max; private readonly int _width; private readonly long _circ;
        public TileJob(RingTerrainSampler sampler, int samplerIndex, TileKey key, long revision, float min, float max, int width, long circ) { _sampler = sampler; SamplerIndex = samplerIndex; Key = key; Revision = revision; _min = min; _max = max; _width = width; _circ = circ; }
        public long TaskId; public int SamplerIndex; public TileKey Key; public long Revision; public volatile bool Cancelled; public string Error = string.Empty; public float[]? Heights; public byte[]? Biomes;
        public void Run()
        {
            try
            {
                Heights = new float[TileSide * TileSide]; Biomes = new byte[TileSide * TileSide * 4];
                for (int z = 0; z < TileSide; z++)
                {
                    if (Cancelled) return;
                    int across = Key.Across + Key.Direction * z * Spacing;
                    for (int x = 0; x < TileSide; x++)
                    {
                        long around = Wrap(Key.Around + x * Spacing); int index = z * TileSide + x;
                        if (across < 0 || across >= _width) { Heights[index] = _min; continue; }
                        _sampler.GetPresentationSurfaceHeightAndBiomeWeights(around, across, out float height, out RingTerrainSampler.BiomeTextureWeights weights);
                        if (!float.IsFinite(height)) throw new InvalidOperationException("Terrain sampler returned a non-finite height.");
                        Heights[index] = Math.Clamp(height, _min, _max);
                        Color color = weights.ToVertexColor();
                        int c = index * 4; Biomes[c] = QuantizeBiomeChannel(color.R); Biomes[c + 1] = QuantizeBiomeChannel(color.G); Biomes[c + 2] = QuantizeBiomeChannel(color.B); Biomes[c + 3] = QuantizeBiomeChannel(color.A);
                    }
                }
            }
            catch (Exception exception) { Error = exception.Message; }
        }
        private long Wrap(long value) { value %= _circ; return value < 0 ? value + _circ : value; }
    }

    private sealed class PackJob : IDisposable
    {
        private readonly RequestState _request; private readonly CachedTile[] _tiles; private readonly float _min, _max; private readonly int _width;
        private PreparedWindow? _result;
        public PackJob(RequestState request, CachedTile[] tiles, float min, float max, int width) { _request = request; _tiles = tiles; _min = min; _max = max; _width = width; }
        public long TaskId; public volatile bool Cancelled; public string Error = string.Empty; public long Revision => _request.Revision;
        public void Run()
        {
            var clock = Stopwatch.StartNew();
            Array<Image>? heightImages = null;
            Array<Image>? biomeImages = null;
            RingTerrain3DCollisionGeometry? geometry = null;
            try
            {
                float[] heights = new float[WindowSide * WindowSide]; byte[] biomes = new byte[WindowSide * WindowSide * 4];
                for (int z = 0; z < WindowSide; z++)
                {
                    if (Cancelled) return;
                    int tz = z / TileSide, localZ = z % TileSide;
                    for (int x = 0; x < WindowSide; x++)
                    {
                        int tx = x / TileSide, localX = x % TileSide, source = tz * 3 + tx;
                        int dst = z * WindowSide + x, src = localZ * TileSide + localX;
                        heights[dst] = _tiles[source].Heights[src]; Buffer.BlockCopy(_tiles[source].Biomes, src * 4, biomes, dst * 4, 4);
                    }
                }
                ApplyEdits(heights);
                heightImages = new Array<Image>(); biomeImages = new Array<Image>();
                for (int z = 0; z < 3; z++) for (int x = 0; x < 3; x++)
                {
                    byte[] h = new byte[TileSide * TileSide * 4], b = new byte[TileSide * TileSide * 4];
                    for (int row = 0; row < TileSide; row++)
                    {
                        int src = (z * TileSide + row) * WindowSide + x * TileSide;
                        for (int col = 0; col < TileSide; col++) BitConverter.TryWriteBytes(h.AsSpan((row * TileSide + col) * 4, 4), heights[src + col] - _request.Frame.AnchorHeight);
                        Buffer.BlockCopy(biomes, src * 4, b, row * TileSide * 4, TileSide * 4);
                    }
                    heightImages.Add(Image.CreateFromData(TileSide, TileSide, false, Image.Format.Rf, h));
                    biomeImages.Add(Image.CreateFromData(TileSide, TileSide, false, Image.Format.Rgba8, b));
                }
                geometry = new RingTerrain3DCollisionGeometry();
                if (!geometry.PrepareGameplay(heights, _request.Frame.AnchorAroundMeters, _request.Frame.AnchorAcrossMeters, _request.Frame.AnchorHeight, _request.Frame.AcrossDirection, _min, _max, _request.TileCells))
                    throw new InvalidOperationException(geometry.GetDiagnostics()["error"].AsString());
                clock.Stop(); _result = new PreparedWindow(heights, heightImages, biomeImages, geometry, _request.Frame, _request.Revision, _request.EditRevision,
                    (Stopwatch.GetTimestamp() - _request.StartTimestamp) * 1000.0 / Stopwatch.Frequency);
                heightImages = null; biomeImages = null; geometry = null;
            }
            catch (Exception exception) { Error = exception.Message; }
            finally
            {
                clock.Stop();
                if (Cancelled && Error.Length == 0) Error = "Preparation cancelled.";
                if (heightImages != null) foreach (Image image in heightImages) image.Dispose();
                if (biomeImages != null) foreach (Image image in biomeImages) image.Dispose();
                geometry?.Dispose();
            }
        }
        private void ApplyEdits(float[] heights)
        {
            foreach (RingSurfaceHeightEdit edit in _request.Edits)
            {
                long delta = _request.Frame.SignedAroundDeltaMeters(edit.AroundMeters);
                int x = (int)(delta / Spacing) + TileSide, z = (_request.Frame.AcrossDirection * (edit.AcrossMeters - _request.Frame.AnchorAcrossMeters)) / Spacing + TileSide;
                if ((uint)x < WindowSide && (uint)z < WindowSide) heights[z * WindowSide + x] = Math.Clamp(edit.Height, _min, _max);
            }
        }
        public PreparedWindow TakeResult() { PreparedWindow result = _result ?? throw new InvalidOperationException("No packed result."); _result = null; return result; }
        public void Dispose() { _result?.Dispose(); _result = null; }
    }
}

public sealed class PreparedWindow : IDisposable
{
    private bool _disposed;
    public PreparedWindow(float[] heights, Array<Image> heightImages, Array<Image> biomeImages, RingTerrain3DCollisionGeometry geometry, RingSurfaceFrame frame, long revision, ulong editRevision, double ms)
    { Heights = heights; HeightImages = heightImages; BiomeImages = biomeImages; Geometry = geometry; Frame = frame; Revision = revision; EditRevision = editRevision; GenerationMilliseconds = ms; }
    public float[] Heights { get; } public Array<Image> HeightImages { get; } public Array<Image> BiomeImages { get; } public RingTerrain3DCollisionGeometry? Geometry { get; private set; }
    public RingSurfaceFrame Frame { get; } public long Revision { get; } public ulong EditRevision { get; } public double GenerationMilliseconds { get; }
    public RingTerrain3DCollisionGeometry TransferGeometryOwnership() { RingTerrain3DCollisionGeometry geometry = Geometry ?? throw new InvalidOperationException("Geometry already transferred."); Geometry = null; return geometry; }
    public void Dispose() { if (_disposed) return; _disposed = true; foreach (Image image in HeightImages) image.Dispose(); foreach (Image image in BiomeImages) image.Dispose(); Geometry?.Dispose(); Geometry = null; }
}
