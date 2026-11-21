using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Loading;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Per-world disk cache of the sampled output of the initial global terrain segments (docs/load-time-plan.md, #12).
/// Segment sampling is deterministic, so the arrays <see cref="SamplePreparedVertex"/> fills can be stored and replayed:
/// a cached segment skips sampling and recomputes only the two world-space vertex arrays. Any doubt makes the whole file stale.
/// </summary>
public partial class RingTerrainGlobalLod
{
    /// <summary>Bump when the file layout or anything about the sampled output that the key cannot see changes.</summary>
    internal const int GlobalTerrainCacheVersion = 1;
    private const int GlobalTerrainCachePublishesPerFrame = 8;
    private const long GlobalTerrainCacheMinimumFreeBytes = 2L * 1024L * 1024L * 1024L;
    private static readonly byte[] GlobalTerrainCacheMagic = { (byte)'R', (byte)'W', (byte)'G', (byte)'T' };

    /// <summary>
    /// Set by RingWorldLoadingBridge for the real loading screen only (never the warm world, the fly-in or gameplay). While set, cache-served
    /// segments of the initial build may finalise up to <see cref="LoadingScreenCachedSegmentMillisecondsPerFrame"/> a frame instead of the normal deadline.
    /// </summary>
    public bool LoadingScreenBoost { get; set; }
    /// <summary>Per-frame finalise deadline for cache-served initial segments while <see cref="LoadingScreenBoost"/> is set. A constant, not a tunable.</summary>
    internal const double LoadingScreenCachedSegmentMillisecondsPerFrame = 16.0;

    /// <summary>Not a user tunable: a rebuildable cache of deterministic output. --disable-lod-disk-cache also turns it off.</summary>
    [Export] public bool EnableGlobalTerrainDiskCache { get; set; } = true;
    /// <summary>Test hook: directory that replaces the world lod_cache folder.</summary>
    internal string? GlobalTerrainCacheDirectoryOverride { get; set; }
    internal Task? GlobalTerrainCacheWriteTaskFixture => _globalCacheWriteTask;
    internal (string State, int Cached, int Sampled, int Eligible) LastGlobalTerrainCacheResult => _lastGlobalCacheResult;

    private GlobalTerrainCacheSession? _globalCache;
    private Task? _globalCacheWriteTask;
    private (string State, int Cached, int Sampled, int Eligible) _lastGlobalCacheResult = ("none", 0, 0, 0);

    /// <summary>The ring layout of one cached file; the vertex count of every segment follows from it.</summary>
    private sealed class GlobalTerrainCacheRecord
    {
        public int Index;
        public ulong Hash;
        public required float[] Canonical, RawTerrain, RawWater;
        public required Color[] TerrainColors, WaterColors;
        public required bool[] Displayed;
    }

    private sealed class GlobalTerrainCacheLoad
    {
        public required string State;
        public required GlobalTerrainCacheRecord?[] Records;
        public long FileBytes;
        public double Milliseconds;
    }

    private sealed class GlobalTerrainCacheSession
    {
        public required string Path;
        public required string KeyPrefix;
        public required ulong MorphGeneration;
        public required RingLodSurfaceEditSnapshot PreparedEdits;
        public required int SegmentCount, AroundCount, AcrossCount;
        public required long AroundLength;
        public required bool[] Eligible;
        public required int[] ExpectedVertexCounts;
        public required CancellationTokenSource Cancel;
        public Task<GlobalTerrainCacheLoad>? LoadTask;
        public string Key = string.Empty;
        public ulong KeyHash;
        public volatile GlobalTerrainCacheLoad? Loaded;
        public int ServiceFrames;
        public double DeadlineMilliseconds;
        public GlobalTerrainCacheRecord?[] Captured = Array.Empty<GlobalTerrainCacheRecord?>();
        public bool[] Served = Array.Empty<bool>();
        public bool[] Sampled = Array.Empty<bool>();

        public bool HasRecord(int index) => Loaded?.Records[index] != null;

        /// <summary>Worker side: fills the state arrays from the record of its segment, or returns false when it has none and must sample.</summary>
        public bool TryFill(PendingSegmentPreparation state, CancellationToken cancellation)
        {
            GlobalTerrainCacheLoad load;
            try
            {
                LoadTask!.Wait(cancellation);
                load = LoadTask.Result;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception) { return false; }
            GlobalTerrainCacheRecord? record = load.Records[state.SegmentIndex];
            if (record == null || record.Canonical.Length != state.VertexCount) return false;
            // The record is immutable and the worker only reads these arrays afterwards, so they are shared, not copied.
            state.TerrainCanonicalHeights = record.Canonical;
            state.RawTerrainHeights = record.RawTerrain;
            state.RawWaterHeights = record.RawWater;
            state.TerrainColors = record.TerrainColors;
            state.WaterColors = record.WaterColors;
            state.DisplayedWater = record.Displayed;
            // The same two lines as the end of SamplePreparedVertex.
            int row = state.LocalAroundCount + 1;
            for (int index = 0; index < state.VertexCount; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                int z = index / row, x = index % row;
                double around = (state.AroundStartIndex + x) * state.AroundSpacing;
                double across = z * state.AcrossSpacing;
                float terrainCanonicalHeight = record.Canonical[index];
                float waterHeight = record.RawWater[index];
                state.TerrainVertices[index] = state.WorldToLocal * state.Geometry.CanonicalToWorld(around, terrainCanonicalHeight, across);
                state.WaterVertices[index] = state.WorldToLocal * state.Geometry.CanonicalToWorld(around, waterHeight + (state.Sampler.UsesRuntimeSurfaceGeneration ? 0.0 : 1.0) - state.Underlay, across);
            }
            Served[state.SegmentIndex] = true;
            return true;
        }

        /// <summary>Worker side, after sampling: keeps the arrays while a write is due so the build can serialise them.</summary>
        public void Capture(PendingSegmentPreparation state)
        {
            Sampled[state.SegmentIndex] = true;
            if (Loaded is not { } load || load.State == "hit") return;
            Captured[state.SegmentIndex] = new GlobalTerrainCacheRecord
            {
                Index = state.SegmentIndex, Hash = state.OutputHash,
                Canonical = state.TerrainCanonicalHeights, RawTerrain = state.RawTerrainHeights, RawWater = state.RawWaterHeights,
                TerrainColors = state.TerrainColors, WaterColors = state.WaterColors, Displayed = state.DisplayedWater,
            };
        }
    }

    private void AbandonGlobalTerrainCache()
    {
        GlobalTerrainCacheSession? session = _globalCache;
        _globalCache = null;
        session?.Cancel.Cancel();
    }

    private bool GlobalTerrainCacheServes(int segmentIndex)
        => _globalCache is { } session && segmentIndex >= 0 && segmentIndex < session.SegmentCount && session.Eligible[segmentIndex] && session.HasRecord(segmentIndex);

    private void AttachGlobalTerrainCache(PendingSegmentPreparation state, RingLodSurfaceEditSnapshot edits)
    {
        if (_globalCache is not { } session) return;
        AdoptGlobalTerrainCacheEdits(session, edits);
        if (state.SegmentCount != session.SegmentCount || !session.Eligible[state.SegmentIndex]) return;
        state.Cache = session;
    }

    /// <summary>
    /// The initial edit snapshot is replaced during the build (the height field attaching bumps the edit version even with no edit).
    /// Eligibility only ever shrinks: a segment stays cacheable while every snapshot seen so far leaves its neighbourhood unedited.
    /// </summary>
    private static void AdoptGlobalTerrainCacheEdits(GlobalTerrainCacheSession session, RingLodSurfaceEditSnapshot edits)
    {
        if (ReferenceEquals(edits, session.PreparedEdits)) return;
        bool[] current = ComputeGlobalTerrainCacheEligibility(edits, session.SegmentCount, session.AroundLength);
        for (int index = 0; index < current.Length; index++) session.Eligible[index] &= current[index];
        session.PreparedEdits = edits;
    }

    /// <summary>Starts the cache load for the initial build that BuildGlobalRing just queued.</summary>
    private void BeginGlobalTerrainCache(RingTerrainSampler sampler, RingLodSurfaceEditSnapshot rawEdits)
    {
        bool disabled = !EnableGlobalTerrainDiskCache
            || Array.Exists(OS.GetCmdlineUserArgs(), argument => argument.Equals("--disable-lod-disk-cache", StringComparison.OrdinalIgnoreCase))
            || !sampler.UsesRuntimeSurfaceGeneration
            || _initialSegmentEdits == null;
        if (disabled)
        {
            RingLoadTimeline.Mark("global_terrain_cache", "state=off");
            return;
        }
        int aroundCount = Mathf.Max(64, AroundSegments);
        int acrossCount = Mathf.Max(4, AcrossSegments);
        int segmentCount = _initialSegmentCount;
        double circumference = _grid!.GetCanonicalRingCircumferenceMeters64();
        bool[] eligible = ComputeGlobalTerrainCacheEligibility(rawEdits, segmentCount, sampler.AroundRingVoxelLength);
        var vertexCounts = new int[segmentCount];
        for (int index = 0; index < segmentCount; index++)
        {
            int start = index * aroundCount / segmentCount, end = (index + 1) * aroundCount / segmentCount;
            vertexCounts[index] = (Math.Max(1, end - start) + 1) * (acrossCount + 1);
        }
        bool preserve = UseMinMaxFeaturePreservation && _heightPyramid != null;
        var prefix = new StringBuilder(512);
        prefix.Append("gtc").Append(GlobalTerrainCacheVersion)
            .Append("|gen=").Append(sampler.TerrainGeneratorVersion)
            .Append("|seed=").Append(sampler.Seed)
            .Append("|def=").Append(sampler.TerrainDefinitionHash.ToString("X16"))
            .Append("|circ=").Append(BitConverter.DoubleToInt64Bits(circumference).ToString("X16"))
            .Append("|width=").Append(RingWidthMeters)
            .Append("|aroundVox=").Append(sampler.AroundRingVoxelLength)
            .Append("|samplerWidth=").Append(sampler.RingAcrossWidthMeters)
            .Append("|around=").Append(aroundCount)
            .Append("|across=").Append(acrossCount)
            .Append("|segs=").Append(segmentCount)
            .Append("|underlay=").Append(BitConverter.SingleToInt32Bits(RadialUnderlayOffsetMeters).ToString("X8"))
            .Append("|minmax=").Append(preserve ? 1 : 0).Append(':').Append(preserve ? _heightPyramid!.BaseCellSize : 0)
            .Append("|low=").Append(ColorKey(LowTerrainColor))
            .Append("|high=").Append(ColorKey(HighTerrainColor))
            .Append("|features=").Append(sampler.WorldFeatureMask)
            .Append("|riverLine=").Append(sampler.HasRiverLine ? 1 : 0)
            .Append("|riverBeds=").Append(sampler.HasRiverBeds ? 1 : 0)
            .Append("|hydro=").Append(HydrologyVersions.DrainageAlgorithm).Append('.').Append(HydrologyVersions.CarveOperator).Append('.').Append(HydrologyVersions.GraphFormat);
        RingWaterBodyColors.Snapshot water = RingWaterBodyColors.Current;
        prefix.Append("|water=").Append(ColorKey(water.River)).Append(',').Append(ColorKey(water.Lake)).Append(',').Append(BitConverter.SingleToInt32Bits(water.Turbidity).ToString("X8"));
        // The biome texture palette (the static colours the sampler blends) feeds the vertex colours. Read without loading anything.
        foreach (Color paletteColor in RingTerrainVisuals.CaptureLoadingPaletteFallbacks()) prefix.Append('|').Append(ColorKey(paletteColor));
        bool hasCanopyFold = sampler.CanopyFold != null;
        string path = ResolveGlobalTerrainCachePath(sampler.Seed, sampler.TerrainDefinitionHash);

        var session = new GlobalTerrainCacheSession
        {
            Path = path, KeyPrefix = prefix.ToString(), MorphGeneration = _morphGeneration,
            PreparedEdits = _initialSegmentEdits!, SegmentCount = segmentCount, AroundCount = aroundCount, AcrossCount = acrossCount, AroundLength = sampler.AroundRingVoxelLength,
            Eligible = eligible, ExpectedVertexCounts = vertexCounts, Cancel = new CancellationTokenSource(),
        };
        session.Captured = new GlobalTerrainCacheRecord?[segmentCount];
        session.Served = new bool[segmentCount];
        session.Sampled = new bool[segmentCount];
        CancellationToken cancellation = session.Cancel.Token;
        session.LoadTask = Task.Run(() =>
        {
            // The far canopy albedo feeds the vertex colours of canopy worlds, so it belongs to the key. It is cached and thread safe.
            var key = new StringBuilder(session.KeyPrefix, session.KeyPrefix.Length + 80);
            if (hasCanopyFold) key.Append("|canopy=").Append(RingFarCanopy.TryGetAlbedo(out Vector3 albedo) ? Vector3Key(albedo) : "none");
            session.Key = key.ToString();
            session.KeyHash = Fnv1a64(Encoding.UTF8.GetBytes(session.Key));
            long loadStart = System.Diagnostics.Stopwatch.GetTimestamp();
            GlobalTerrainCacheLoad result = LoadGlobalTerrainCacheFile(session.Path, session.Key, session.KeyHash, session.Eligible, session.ExpectedVertexCounts, cancellation);
            result.Milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(loadStart).TotalMilliseconds;
            session.Loaded = result;
            return result;
        }, cancellation);
        _globalCache = session;
    }

    /// <summary>
    /// A segment is cached only when no edited or dirty column lies within its around range plus one segment on each side, the same
    /// neighbourhood RefreshEditedSegments dirties. With edits on a surface that is not yet the float field the whole ring takes the
    /// legacy bilinear path, so nothing is cached then.
    /// </summary>
    private static bool[] ComputeGlobalTerrainCacheEligibility(RingLodSurfaceEditSnapshot edits, int segmentCount, long aroundLength)
    {
        var eligible = new bool[segmentCount];
        Array.Fill(eligible, !(edits.HasEdits && !edits.UsesFloatSurface));
        long length = Math.Max(1L, aroundLength);
        void Mark(RingTerrainColumnCoordinate column)
        {
            long around = ((column.AroundVoxel % length) + length) % length;
            int segment = (int)Math.Clamp(around * segmentCount / length, 0L, segmentCount - 1L);
            for (int offset = -1; offset <= 1; offset++)
                eligible[((segment + offset) % segmentCount + segmentCount) % segmentCount] = false;
        }
        foreach (RingTerrainColumnCoordinate column in edits.CanonicalEditedColumns.Keys) Mark(column);
        foreach (RingTerrainColumnCoordinate column in edits.CanonicalFloatColumns.Keys) Mark(column);
        foreach (RingTerrainColumnCoordinate column in edits.CanonicalDirtyColumns) Mark(column);
        return eligible;
    }

    private string ResolveGlobalTerrainCachePath(int seed, ulong definitionHash)
    {
        if (!string.IsNullOrEmpty(GlobalTerrainCacheDirectoryOverride))
            return Path.Combine(GlobalTerrainCacheDirectoryOverride, "global_terrain.bin");
        string activeWorld = ProjectSettings.GetSetting("ringworld/runtime/active_world_directory", "").AsString();
        if (string.IsNullOrWhiteSpace(activeWorld))
            return ProjectSettings.GlobalizePath($"user://ring_cache/global_terrain_s{seed}_h{definitionHash:X16}.bin");
        return ProjectSettings.GlobalizePath(activeWorld.TrimEnd('/', '\\') + "/lod_cache/global_terrain.bin");
    }

    /// <summary>Called when the initial build has published every segment, before its edit snapshot is released.</summary>
    private void CompleteGlobalTerrainCache()
    {
        GlobalTerrainCacheSession? session = _globalCache;
        _globalCache = null;
        if (session == null) return;
        if (_initialSegmentEdits != null) AdoptGlobalTerrainCacheEdits(session, _initialSegmentEdits);
        bool valid = !session.Cancel.IsCancellationRequested
            && session.MorphGeneration == _morphGeneration
            && ReferenceEquals(_initialSegmentEdits, session.PreparedEdits)
            && _grid?.LodSurfaceEditVersion == _observedEditVersion;
        int eligibleCount = 0, cached = 0, sampled = 0;
        for (int index = 0; index < session.SegmentCount; index++)
        {
            if (session.Eligible[index]) eligibleCount++;
            if (session.Served[index]) cached++;
            else if (session.Sampled[index]) sampled++;
        }
        GlobalTerrainCacheLoad? load = session.Loaded;
        string state = load?.State ?? "unused";
        _lastGlobalCacheResult = (state, cached, sampled, eligibleCount);
        RingLoadTimeline.Mark("global_terrain_cache", $"state={state} segments={session.SegmentCount} eligible={eligibleCount} cached={cached} sampled={sampled} fileBytes={load?.FileBytes ?? 0} loadMs={load?.Milliseconds ?? 0:0.0} serviceFrames={session.ServiceFrames} deadlineMs={session.DeadlineMilliseconds:0.0}");
        if (!valid || load == null || load.State == "hit" || eligibleCount == 0) return;
        var records = new List<GlobalTerrainCacheRecord>(eligibleCount);
        for (int index = 0; index < session.SegmentCount; index++)
        {
            if (!session.Eligible[index]) continue;
            if (session.Captured[index] is not { } record) return;
            records.Add(record);
        }
        string path = session.Path, key = session.Key;
        ulong keyHash = session.KeyHash;
        _globalCacheWriteTask = Task.Run(() => WriteGlobalTerrainCacheFile(path, key, keyHash, records));
    }

    private static GlobalTerrainCacheLoad LoadGlobalTerrainCacheFile(
        string path, string key, ulong keyHash, bool[] eligible, int[] expectedVertexCounts, CancellationToken cancellation)
    {
        var empty = new GlobalTerrainCacheRecord?[eligible.Length];
        try
        {
            if (!File.Exists(path)) return new GlobalTerrainCacheLoad { State = "miss", Records = empty };
            byte[] file = File.ReadAllBytes(path);
            GlobalTerrainCacheLoad Stale() => new() { State = "stale", Records = empty, FileBytes = file.LongLength };
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            if (!TryReadGlobalTerrainCacheHeader(file, out int version, out int keyLength, out int keyOffset, out ulong storedKeyHash, out ulong payloadHash, out int payloadOffset, out int payloadLength)
                || version != GlobalTerrainCacheVersion
                || keyLength != keyBytes.Length
                || storedKeyHash != keyHash
                || !file.AsSpan(keyOffset, keyLength).SequenceEqual(keyBytes)
                || file.LongLength != (long)payloadOffset + payloadLength
                || Fnv1a64(file.AsSpan(payloadOffset, payloadLength)) != payloadHash)
                return Stale();

            var records = new GlobalTerrainCacheRecord?[eligible.Length];
            using var input = new MemoryStream(file, payloadOffset, payloadLength, false);
            using var inflate = new DeflateStream(input, CompressionMode.Decompress);
            Span<byte> head = stackalloc byte[16];
            inflate.ReadExactly(head[..4]);
            int recordCount = BitConverter.ToInt32(head[..4]);
            if (recordCount < 0 || recordCount > eligible.Length) return Stale();
            for (int n = 0; n < recordCount; n++)
            {
                cancellation.ThrowIfCancellationRequested();
                inflate.ReadExactly(head);
                int index = BitConverter.ToInt32(head[..4]);
                int count = BitConverter.ToInt32(head.Slice(4, 4));
                ulong hash = BitConverter.ToUInt64(head.Slice(8, 8));
                if (index < 0 || index >= eligible.Length || records[index] != null || count != expectedVertexCounts[index]) return Stale();
                var record = new GlobalTerrainCacheRecord
                {
                    Index = index, Hash = hash,
                    Canonical = ReadFloats(inflate, count), RawTerrain = ReadFloats(inflate, count), RawWater = ReadFloats(inflate, count),
                    TerrainColors = ReadColors(inflate, count), WaterColors = ReadColors(inflate, count), Displayed = ReadBools(inflate, count),
                };
                // The stored hash is the output hash the sampler computed for these exact arrays.
                if (ComputePreparedOutputHash(count, record.Canonical, record.TerrainColors, record.WaterColors, record.Displayed, record.RawTerrain, record.RawWater) != hash)
                    return Stale();
                records[index] = record;
            }
            // Keep only what this build may use; every eligible segment must be present, otherwise the file is out of date.
            for (int index = 0; index < eligible.Length; index++)
            {
                if (!eligible[index]) { records[index] = null; continue; }
                if (records[index] == null) return Stale();
            }
            return new GlobalTerrainCacheLoad { State = "hit", Records = records, FileBytes = file.LongLength };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return new GlobalTerrainCacheLoad { State = "stale", Records = empty };
        }
    }

    /// <summary>File layout: magic, version, key length, key, key hash, payload hash, payload length, deflated payload.</summary>
    internal static bool TryReadGlobalTerrainCacheHeader(
        ReadOnlySpan<byte> file, out int version, out int keyLength, out int keyOffset, out ulong keyHash, out ulong payloadHash, out int payloadOffset, out int payloadLength)
    {
        version = keyLength = keyOffset = payloadOffset = payloadLength = 0;
        keyHash = payloadHash = 0UL;
        if (file.Length < 12 || !file[..4].SequenceEqual(GlobalTerrainCacheMagic)) return false;
        version = BitConverter.ToInt32(file.Slice(4, 4));
        keyLength = BitConverter.ToInt32(file.Slice(8, 4));
        keyOffset = 12;
        if (keyLength < 0 || keyLength > 65536 || file.Length < keyOffset + keyLength + 20) return false;
        int cursor = keyOffset + keyLength;
        keyHash = BitConverter.ToUInt64(file.Slice(cursor, 8));
        payloadHash = BitConverter.ToUInt64(file.Slice(cursor + 8, 8));
        payloadLength = BitConverter.ToInt32(file.Slice(cursor + 16, 4));
        payloadOffset = cursor + 20;
        return payloadLength >= 0;
    }

    private void WriteGlobalTerrainCacheFile(string path, string key, ulong keyHash, List<GlobalTerrainCacheRecord> records)
    {
        string? temp = null;
        try
        {
            long rawBytes = 0;
            foreach (GlobalTerrainCacheRecord record in records) rawBytes += (long)record.Canonical.Length * 45L;
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) return;
            Directory.CreateDirectory(directory);
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
                if (drive.IsReady && drive.AvailableFreeSpace - rawBytes < GlobalTerrainCacheMinimumFreeBytes) return;
            }
            catch (Exception) { }

            using var payload = new MemoryStream((int)Math.Min(int.MaxValue, rawBytes / 2 + 1024));
            using (var deflate = new DeflateStream(payload, CompressionLevel.Fastest, true))
            {
                deflate.Write(BitConverter.GetBytes(records.Count));
                foreach (GlobalTerrainCacheRecord record in records)
                {
                    int count = record.Canonical.Length;
                    deflate.Write(BitConverter.GetBytes(record.Index));
                    deflate.Write(BitConverter.GetBytes(count));
                    deflate.Write(BitConverter.GetBytes(record.Hash));
                    deflate.Write(MemoryMarshal.AsBytes(record.Canonical.AsSpan()));
                    deflate.Write(MemoryMarshal.AsBytes(record.RawTerrain.AsSpan()));
                    deflate.Write(MemoryMarshal.AsBytes(record.RawWater.AsSpan()));
                    WriteColors(deflate, record.TerrainColors);
                    WriteColors(deflate, record.WaterColors);
                    var flags = new byte[count];
                    for (int i = 0; i < count; i++) flags[i] = record.Displayed[i] ? (byte)1 : (byte)0;
                    deflate.Write(flags);
                }
            }
            byte[] compressed = payload.ToArray();
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            temp = path + "." + System.Environment.ProcessId + "." + System.Environment.CurrentManagedThreadId + ".tmp";
            using (var output = new FileStream(temp, FileMode.Create, System.IO.FileAccess.Write, FileShare.None))
            {
                output.Write(GlobalTerrainCacheMagic);
                output.Write(BitConverter.GetBytes(GlobalTerrainCacheVersion));
                output.Write(BitConverter.GetBytes(keyBytes.Length));
                output.Write(keyBytes);
                output.Write(BitConverter.GetBytes(keyHash));
                output.Write(BitConverter.GetBytes(Fnv1a64(compressed)));
                output.Write(BitConverter.GetBytes(compressed.Length));
                output.Write(compressed);
            }
            File.Move(temp, path, true);
            temp = null;
            RingGeneratedLodCacheBudget.Touch(path);
            RingLoadTimeline.Mark("global_terrain_cache_written", $"bytes={new FileInfo(path).Length} segments={records.Count}");
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Global terrain cache write skipped: {exception.GetBaseException().Message}");
        }
        finally
        {
            if (temp != null)
            {
                try { File.Delete(temp); } catch (Exception) { }
            }
        }
    }

    private static float[] ReadFloats(Stream stream, int count)
    {
        var values = new float[count];
        stream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }

    private static Color[] ReadColors(Stream stream, int count)
    {
        float[] raw = ReadFloats(stream, count * 4);
        var colors = new Color[count];
        for (int i = 0; i < count; i++) colors[i] = new Color(raw[i * 4], raw[i * 4 + 1], raw[i * 4 + 2], raw[i * 4 + 3]);
        return colors;
    }

    private static bool[] ReadBools(Stream stream, int count)
    {
        var raw = new byte[count];
        stream.ReadExactly(raw);
        var values = new bool[count];
        for (int i = 0; i < count; i++) values[i] = raw[i] != 0;
        return values;
    }

    private static void WriteColors(Stream stream, Color[] colors)
    {
        var raw = new float[colors.Length * 4];
        for (int i = 0; i < colors.Length; i++)
        {
            raw[i * 4] = colors[i].R; raw[i * 4 + 1] = colors[i].G; raw[i * 4 + 2] = colors[i].B; raw[i * 4 + 3] = colors[i].A;
        }
        stream.Write(MemoryMarshal.AsBytes(raw.AsSpan()));
    }

    private static ulong Fnv1a64(ReadOnlySpan<byte> bytes)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte value in bytes) hash = (hash ^ value) * 1099511628211UL;
        return hash;
    }

    private static string ColorKey(Color color)
        => $"{BitConverter.SingleToInt32Bits(color.R):X8}{BitConverter.SingleToInt32Bits(color.G):X8}{BitConverter.SingleToInt32Bits(color.B):X8}{BitConverter.SingleToInt32Bits(color.A):X8}";

    private static string Vector3Key(Vector3 value)
        => $"{BitConverter.SingleToInt32Bits(value.X):X8}{BitConverter.SingleToInt32Bits(value.Y):X8}{BitConverter.SingleToInt32Bits(value.Z):X8}";

    /// <summary>Test hook: the per-segment output hashes of the last initial build, in segment order.</summary>
    internal ulong[] GetSegmentOutputHashesFixture()
    {
        var hashes = new ulong[_initialSegmentCount];
        for (int index = 0; index < hashes.Length; index++) hashes[index] = _segmentOutputHashes.GetValueOrDefault(index);
        return hashes;
    }
}
