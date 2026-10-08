using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Canonical, circumference-wrapped terrain hierarchy used by global HLOD now
/// and by long-range sun queries in Phase F. Level zero stores conservative
/// ranges over fixed surface cells; each parent aggregates four children.
/// </summary>
public sealed partial class RingTerrainMinMaxPyramid
{
    public readonly record struct ElevationAudit(
        int MinimumHeight,
        int MaximumHeight,
        int CellsAboveRoutineCeiling,
        int CellsAtOrAboveNearSummit,
        int TotalBaseCells);
    public readonly record struct DirectionalShadowMask(
        int Width,
        int Height,
        byte[] Pixels,
        float MaximumTraceDistanceMeters);

    private const uint CacheMagic = 0x504D4D52; // RMMP
    private const int CacheFormatVersion = 5;
    public readonly record struct HeightRange(int MinHeight, int MaxHeight);

    private sealed record Level(int Width, int Height, int CellSize, HeightRange[] Cells);

    private readonly RingTerrainSampler _sampler;
    private readonly int _aroundLength;
    private readonly int _ringWidth;
    private readonly int _baseCellSize;
    private readonly HeightRange[] _baseline;
    private readonly HeightRange[] _activeBase;
    // One exact height per mask receiver cell. Unlike the range hierarchy this
    // remains valid after the worker-owned sampler has been disposed.
    private readonly float[] _baselineReceiverHeights;
    private readonly float[] _activeReceiverHeights;
    private readonly List<Level> _levels = new();
    private readonly HashSet<Vector2I> _editedColumns = new();

    public ulong EditVersion { get; private set; }
    public int LevelCount => _levels.Count;
    public int BaseCellSize => _baseCellSize;
    public int BaseWidth => _levels.Count > 0 ? _levels[0].Width : 0;
    public int BaseHeight => _levels.Count > 0 ? _levels[0].Height : 0;
    public int TotalCellCount { get; private set; }
    public long EstimatedBytes => ((long)_baseline.Length + TotalCellCount) * sizeof(int) * 2L
        + (long)_baselineReceiverHeights.Length * sizeof(float) * 2L;
    public long EstimatedCacheBytes => 64L + (long)_baseline.Length * sizeof(int) * 3L;
    public int LastDirtyBaseCellCount { get; private set; }

    public RingTerrainMinMaxPyramid(RingTerrainSampler sampler, int ringWidth, int baseCellSize = 512, CancellationToken cancellation = default)
    {
        _sampler = sampler;
        _aroundLength = RequireLegacyAroundLength(sampler.AroundRingVoxelLength);
        _ringWidth = Mathf.Max(1, ringWidth);
        _baseCellSize = Mathf.Max(32, baseCellSize);
        int width = Mathf.Max(1, Mathf.CeilToInt(_aroundLength / (float)_baseCellSize));
        int height = Mathf.Max(1, Mathf.CeilToInt(_ringWidth / (float)_baseCellSize));
        _baseline = new HeightRange[width * height];
        _baselineReceiverHeights = new float[width * height];
        BuildBaseline(width, height, cancellation);
        _activeBase = (HeightRange[])_baseline.Clone();
        _activeReceiverHeights = (float[])_baselineReceiverHeights.Clone();
        BuildLevels(_activeBase, cancellation);
    }

    private RingTerrainMinMaxPyramid(
        RingTerrainSampler sampler,
        int ringWidth,
        int baseCellSize,
        HeightRange[] baseline,
        float[] baselineReceiverHeights,
        CancellationToken cancellation)
    {
        _sampler = sampler;
        _aroundLength = RequireLegacyAroundLength(sampler.AroundRingVoxelLength);
        _ringWidth = Mathf.Max(1, ringWidth);
        _baseCellSize = Mathf.Max(32, baseCellSize);
        _baseline = baseline;
        _baselineReceiverHeights = baselineReceiverHeights;
        _activeBase = (HeightRange[])baseline.Clone();
        _activeReceiverHeights = (float[])baselineReceiverHeights.Clone();
        BuildLevels(_activeBase, cancellation);
    }

    public static bool TryLoadCache(
        string path,
        RingTerrainSampler sampler,
        int ringWidth,
        int baseCellSize,
        CancellationToken cancellation,
        out RingTerrainMinMaxPyramid? pyramid)
    {
        pyramid = null;
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != CacheMagic
                || reader.ReadInt32() != CacheFormatVersion
                || reader.ReadInt32() != sampler.TerrainGeneratorVersion
                || reader.ReadInt32() != sampler.Seed
                || reader.ReadUInt64() != sampler.TerrainDefinitionHash
                || reader.ReadInt64() != sampler.AroundRingVoxelLength
                || reader.ReadInt32() != ringWidth
                || reader.ReadInt32() != baseCellSize)
            {
                return false;
            }

            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            int expectedWidth = Mathf.Max(1, Mathf.CeilToInt(RequireLegacyAroundLength(sampler.AroundRingVoxelLength) / (float)baseCellSize));
            int expectedHeight = Mathf.Max(1, Mathf.CeilToInt(ringWidth / (float)baseCellSize));
            if (width != expectedWidth || height != expectedHeight) return false;
            int count = checked(width * height);
            var baseline = new HeightRange[count];
            for (int i = 0; i < count; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int minimum = reader.ReadInt32();
                int maximum = reader.ReadInt32();
                if (minimum > maximum) return false;
                baseline[i] = new HeightRange(minimum, maximum);
            }
            var receiverHeights = new float[count];
            for (int i = 0; i < count; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                float receiverHeight = reader.ReadSingle();
                if (!float.IsFinite(receiverHeight)) return false;
                receiverHeights[i] = receiverHeight;
            }
            if (stream.Position != stream.Length) return false;
            pyramid = new RingTerrainMinMaxPyramid(sampler, ringWidth, baseCellSize, baseline, receiverHeights, cancellation);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            pyramid = null;
            return false;
        }
    }

    private static int RequireLegacyAroundLength(long aroundLength)
    {
        if (aroundLength is <= 0 or > int.MaxValue)
        {
            throw new InvalidOperationException(
                "The legacy full-ring min/max pyramid cannot represent this circumference. Use sparse Mega Ring terrain summaries instead.");
        }

        return (int)aroundLength;
    }

    public void SaveCache(string path, CancellationToken cancellation = default)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporaryPath = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.Create, System.IO.FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(CacheMagic);
                writer.Write(CacheFormatVersion);
                writer.Write(_sampler.TerrainGeneratorVersion);
                writer.Write(_sampler.Seed);
                writer.Write(_sampler.TerrainDefinitionHash);
                writer.Write((long)_aroundLength);
                writer.Write(_ringWidth);
                writer.Write(_baseCellSize);
                writer.Write(BaseWidth);
                writer.Write(BaseHeight);
                for (int i = 0; i < _baseline.Length; i++)
                {
                    if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                    writer.Write(_baseline[i].MinHeight);
                    writer.Write(_baseline[i].MaxHeight);
                }
                for (int i = 0; i < _baselineReceiverHeights.Length; i++)
                {
                    if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                    writer.Write(_baselineReceiverHeights[i]);
                }
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static int ResolveScaleAwareBaseCellSize(
        int aroundLength,
        int ringWidth,
        int requestedBaseCellSize,
        int maxBaseCellCount = 1_000_000)
    {
        int requested = Mathf.Max(32, requestedBaseCellSize);
        int aroundTarget = Mathf.CeilToInt(Mathf.Max(1, aroundLength) / 8192.0f);
        int acrossTarget = Mathf.CeilToInt(Mathf.Max(1, ringWidth) / 128.0f);
        int resolved = NextPowerOfTwo(Mathf.Max(requested, Mathf.Max(aroundTarget, acrossTarget)));
        int cellCap = Mathf.Max(1, maxBaseCellCount);
        while ((long)Mathf.CeilToInt(aroundLength / (float)resolved)
            * Mathf.CeilToInt(ringWidth / (float)resolved) > cellCap)
        {
            resolved = checked(resolved * 2);
        }
        return resolved;
    }

    public void ApplyEdits(RingLodSurfaceEditSnapshot snapshot)
    {
        if (snapshot.Version == EditVersion) return;
        int width = Mathf.Max(1, Mathf.CeilToInt(_aroundLength / (float)_baseCellSize));
        int baseHeight = Mathf.Max(1, _activeBase.Length / width);
        var dirtyBaseCells = new HashSet<int>();
        foreach (Vector2I column in snapshot.DirtyColumns)
            dirtyBaseCells.Add(GetBaseCellIndex(column, width, baseHeight));
        if (dirtyBaseCells.Count == 0)
        {
            foreach (Vector2I column in _editedColumns)
                dirtyBaseCells.Add(GetBaseCellIndex(column, width, baseHeight));
            foreach (Vector2I column in snapshot.EditedColumns.Keys)
                dirtyBaseCells.Add(GetBaseCellIndex(column, width, baseHeight));
        }

        foreach (int index in dirtyBaseCells)
        {
            _activeBase[index] = _baseline[index];
            _activeReceiverHeights[index] = _baselineReceiverHeights[index];
        }
        foreach ((Vector2I column, int height) in snapshot.EditedColumns)
        {
            int index = GetBaseCellIndex(column, width, baseHeight);
            if (!dirtyBaseCells.Contains(index)) continue;
            HeightRange range = _activeBase[index];
            _activeBase[index] = new HeightRange(Mathf.Min(range.MinHeight, height), Mathf.Max(range.MaxHeight, height));
            // The shadow receiver is the fixed base-cell center. Only an edit
            // at that exact canonical column changes its sampled surface.
            if (IsReceiverCenter(column, index, width)) _activeReceiverHeights[index] = height;
        }
        foreach ((RingTerrainColumnCoordinate column, float height) in snapshot.CanonicalFloatColumns)
        {
            if (column.AroundVoxel is < int.MinValue or > int.MaxValue) continue;
            var legacyColumn = new Vector2I((int)column.AroundVoxel, column.AcrossVoxel);
            int index = GetBaseCellIndex(legacyColumn, width, baseHeight);
            if (!dirtyBaseCells.Contains(index)) continue;
            HeightRange range = _activeBase[index];
            _activeBase[index] = new HeightRange(
                Mathf.Min(range.MinHeight, Mathf.FloorToInt(height)),
                Mathf.Max(range.MaxHeight, Mathf.CeilToInt(height)));
            if (IsReceiverCenter(legacyColumn, index, width)) _activeReceiverHeights[index] = height;
        }

        PropagateDirtyCells(dirtyBaseCells);
        _editedColumns.Clear();
        foreach (Vector2I column in snapshot.EditedColumns.Keys) _editedColumns.Add(column);
        foreach (RingTerrainColumnCoordinate column in snapshot.CanonicalFloatColumns.Keys)
            if (column.AroundVoxel is >= int.MinValue and <= int.MaxValue)
                _editedColumns.Add(new Vector2I((int)column.AroundVoxel, column.AcrossVoxel));
        LastDirtyBaseCellCount = dirtyBaseCells.Count;
        EditVersion = snapshot.Version;
    }

    public HeightRange Query(float aroundMeters, float acrossMeters, float footprintMeters)
    {
        int levelIndex = 0;
        while (levelIndex + 1 < _levels.Count && _levels[levelIndex + 1].CellSize <= footprintMeters)
            levelIndex++;
        Level level = _levels[levelIndex];
        int canonicalAround = Mathf.PosMod(Mathf.FloorToInt(aroundMeters), _aroundLength);
        int x = Mathf.Clamp(canonicalAround / level.CellSize, 0, level.Width - 1);
        int y = Mathf.Clamp(Mathf.FloorToInt(acrossMeters) / level.CellSize, 0, level.Height - 1);
        return level.Cells[y * level.Width + x];
    }

    /// <summary>
    /// Builds one conservative R8 sun-occlusion value per base cell. The mask
    /// is intentionally coarse: conventional cascades own nearby detail while
    /// this restores mountain-scale contrast after those cascades fade out.
    /// </summary>
    public DirectionalShadowMask BuildDirectionalShadowMask(
        Vector3 sunDirection,
        CancellationToken cancellation = default,
        int maximumTraceSteps = 10,
        float requestedMaximumTraceDistanceMeters = 96000.0f,
        bool includeStructuralWalls = false,
        float structuralWallTopMeters = 0.0f,
        float structuralWallBottomMeters = 0.0f)
    {
        int width = BaseWidth;
        int height = BaseHeight;
        var pixels = new byte[Math.Max(1, width * height)];
        if (width <= 0 || height <= 0)
            return new DirectionalShadowMask(1, 1, pixels, 0.0f);

        Vector3 sun = sunDirection.LengthSquared() > 0.000001f
            ? sunDirection.Normalized()
            : Vector3.Right;
        double radius = _aroundLength / Math.Tau;
        float maximumTraceDistance = Mathf.Min(
            Mathf.Max(_baseCellSize * 2.0f, requestedMaximumTraceDistanceMeters),
            Mathf.Max(_baseCellSize * 2.0f, (float)radius * 0.12f));
        int traceSteps = Mathf.Clamp(maximumTraceSteps, 1, 32);

        for (int y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            float across = Mathf.Min(
                _ringWidth - 0.5f,
                (y + 0.5f) * _baseCellSize);
            for (int x = 0; x < width; x++)
            {
                float around = Mathf.Min(
                    _aroundLength - 0.5f,
                    (x + 0.5f) * _baseCellSize);
                double angle = around / radius;
                var radialOut = new Vector3((float)Math.Cos(angle), 0.0f, (float)Math.Sin(angle));
                Vector3 surfaceUp = -radialOut;
                float sunUp = sun.Dot(surfaceUp);
                if (sunUp <= 0.000001f)
                    continue;

                // Receiver heights must be sampled independently from the
                // conservative hierarchy maxima used for potential casters.
                float receiverHeight = _activeReceiverHeights[y * width + x];
                RingTerrainShadowGeometry.RingPoint receiver = RingTerrainShadowGeometry.ReceiverPosition(
                    radius, around, across, receiverHeight);
                float occlusion = 0.0f;
                float traceDistance = _baseCellSize * 1.5f;
                for (int step = 0;
                     step < traceSteps && traceDistance <= maximumTraceDistance;
                     step++, traceDistance *= 1.55f)
                {
                    RingTerrainShadowGeometry.RingRaySample ray =
                        RingTerrainShadowGeometry.SampleStraightRay(radius, receiver, sun, traceDistance);
                    if (ray.AcrossMeters < 0.0 || ray.AcrossMeters > _ringWidth)
                        break;

                    HeightRange obstacle = Query(
                        (float)ray.AroundMeters,
                        (float)ray.AcrossMeters,
                        _baseCellSize);
                    float clearance = obstacle.MaxHeight - (float)ray.HeightMeters;
                    float softness = Mathf.Max(64.0f, _baseCellSize * 0.35f);
                    float blocked = SmoothStep(0.0f, softness, clearance);
                    float rangeFade = 1.0f - SmoothStep(
                        maximumTraceDistance * 0.72f,
                        maximumTraceDistance,
                        traceDistance);
                    occlusion = Mathf.Max(occlusion, blocked * rangeFade);
                    if (occlusion >= 0.995f) break;
                }
                if (includeStructuralWalls
                    && RingTerrainShadowGeometry.RayHitsStructuralWall(
                        radius,
                        receiver,
                        sun,
                        _ringWidth,
                        structuralWallBottomMeters,
                        structuralWallTopMeters,
                        maximumTraceDistance,
                        1.0))
                {
                    occlusion = 1.0f;
                }
                pixels[y * width + x] = (byte)Mathf.RoundToInt(
                    Mathf.Clamp(occlusion, 0.0f, 1.0f) * 255.0f);
            }
        }

        return new DirectionalShadowMask(
            width,
            height,
            pixels,
            maximumTraceDistance);
    }

    public HeightRange QueryRegion(
        double aroundStartMeters,
        double aroundEndMeters,
        double acrossStartMeters,
        double acrossEndMeters)
    {
        if (_levels.Count == 0) return new HeightRange(0, 0);
        Level baseLevel = _levels[0];
        double around0 = Math.Min(aroundStartMeters, aroundEndMeters);
        double around1 = Math.Max(aroundStartMeters, aroundEndMeters);
        double across0 = Math.Clamp(Math.Min(acrossStartMeters, acrossEndMeters), 0.0, _ringWidth);
        double across1 = Math.Clamp(Math.Max(acrossStartMeters, acrossEndMeters), 0.0, _ringWidth);
        int aroundCell0 = (int)Math.Floor(around0 / _baseCellSize);
        int aroundCell1 = (int)Math.Floor((Math.Max(around0, around1 - 0.001)) / _baseCellSize);
        int acrossCell0 = Mathf.Clamp((int)Math.Floor(across0 / _baseCellSize), 0, baseLevel.Height - 1);
        int acrossCell1 = Mathf.Clamp(
            (int)Math.Floor(Math.Max(across0, across1 - 0.001) / _baseCellSize),
            0,
            baseLevel.Height - 1);
        int minimum = int.MaxValue;
        int maximum = int.MinValue;
        for (int unwrappedX = aroundCell0; unwrappedX <= aroundCell1; unwrappedX++)
        {
            int x = Mathf.PosMod(unwrappedX, baseLevel.Width);
            for (int y = acrossCell0; y <= acrossCell1; y++)
            {
                HeightRange range = baseLevel.Cells[y * baseLevel.Width + x];
                minimum = Mathf.Min(minimum, range.MinHeight);
                maximum = Mathf.Max(maximum, range.MaxHeight);
            }
        }
        return minimum <= maximum
            ? new HeightRange(minimum, maximum)
            : Query((float)around0, (float)across0, _baseCellSize);
    }

    public int PreserveFeatureHeight(int sampledHeight, float aroundMeters, float acrossMeters, float footprintMeters)
    {
        HeightRange range = Query(aroundMeters, acrossMeters, footprintMeters);
        int towardPeak = range.MaxHeight - sampledHeight;
        int towardValley = range.MinHeight - sampledHeight;
        int strongestDelta = Mathf.Abs(towardPeak) >= Mathf.Abs(towardValley) ? towardPeak : towardValley;
        float preservation = Mathf.Clamp(Mathf.Abs(strongestDelta) / 256.0f, 0.0f, 0.45f);
        return Mathf.RoundToInt(sampledHeight + strongestDelta * preservation);
    }

    public float PreserveFeatureHeight(float sampledHeight, float aroundMeters, float acrossMeters, float footprintMeters)
    {
        HeightRange range = Query(aroundMeters, acrossMeters, footprintMeters);
        float towardPeak = range.MaxHeight - sampledHeight;
        float towardValley = range.MinHeight - sampledHeight;
        float strongestDelta = Mathf.Abs(towardPeak) >= Mathf.Abs(towardValley) ? towardPeak : towardValley;
        float preservation = Mathf.Clamp(Mathf.Abs(strongestDelta) / 256.0f, 0.0f, 0.45f);
        return sampledHeight + strongestDelta * preservation;
    }

    public bool ValidateHierarchy(out string error)
    {
        error = string.Empty;
        if (_levels.Count == 0)
        {
            error = "height hierarchy has no levels";
            return false;
        }
        foreach (Level level in _levels)
        foreach (HeightRange range in level.Cells)
        {
            if (range.MinHeight <= range.MaxHeight) continue;
            error = $"invalid range {range.MinHeight}..{range.MaxHeight} at {level.CellSize}m";
            return false;
        }
        for (int levelIndex = 1; levelIndex < _levels.Count; levelIndex++)
        {
            Level child = _levels[levelIndex - 1];
            Level parent = _levels[levelIndex];
            for (int y = 0; y < parent.Height; y++)
            for (int x = 0; x < parent.Width; x++)
            {
                HeightRange parentRange = parent.Cells[y * parent.Width + x];
                for (int oy = 0; oy < 2; oy++)
                for (int ox = 0; ox < 2; ox++)
                {
                    int childX = Mathf.Min(x * 2 + ox, child.Width - 1);
                    int childY = Mathf.Min(y * 2 + oy, child.Height - 1);
                    HeightRange childRange = child.Cells[childY * child.Width + childX];
                    if (parentRange.MinHeight <= childRange.MinHeight && parentRange.MaxHeight >= childRange.MaxHeight) continue;
                    error = $"non-conservative parent at level {levelIndex}, cell {x},{y}";
                    return false;
                }
            }
        }
        HeightRange seamStart = Query(0.0f, _ringWidth * 0.5f, _baseCellSize);
        HeightRange seamWrapped = Query(_aroundLength, _ringWidth * 0.5f, _baseCellSize);
        if (seamStart != seamWrapped)
        {
            error = "circumference query does not wrap to the first cell";
            return false;
        }
        return true;
    }

    public ElevationAudit CaptureBaselineElevationAudit(int routineCeiling, int nearSummitThreshold)
    {
        int minimum = int.MaxValue;
        int maximum = int.MinValue;
        int aboveRoutine = 0;
        int nearSummit = 0;
        foreach (HeightRange range in _baseline)
        {
            minimum = Mathf.Min(minimum, range.MinHeight);
            maximum = Mathf.Max(maximum, range.MaxHeight);
            if (range.MaxHeight > routineCeiling) aboveRoutine++;
            if (range.MaxHeight >= nearSummitThreshold) nearSummit++;
        }

        return new ElevationAudit(minimum, maximum, aboveRoutine, nearSummit, _baseline.Length);
    }

    private void BuildBaseline(int width, int height, CancellationToken cancellation)
    {
        int cornerWidth = width + 1;
        var cornerHeights = new float[cornerWidth * (height + 1)];
        for (int y = 0; y <= height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            int across = Mathf.Min(_ringWidth, y * _baseCellSize);
            for (int x = 0; x <= width; x++)
            {
                int around = Mathf.Min(_aroundLength - 1, x * _baseCellSize);
                float sample = _sampler.UsesRuntimeSurfaceGeneration
                    ? _sampler.GetPresentationSurfaceHeight(around, across)
                    : _sampler.GetSurfaceHeight(around, across);
                cornerHeights[y * cornerWidth + x] = sample;
            }
        }

        for (int y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int a0 = x * _baseCellSize;
                int a1 = Mathf.Min(_aroundLength - 1, a0 + _baseCellSize);
                int z0 = y * _baseCellSize;
                int z1 = Mathf.Min(_ringWidth, z0 + _baseCellSize);
                // Match the R8 receiver location, including its clipped final
                // partial cell, so cached receiver heights cannot drift.
                int ac = Mathf.Min(_aroundLength - 1, x * _baseCellSize + _baseCellSize / 2);
                int zc = Mathf.Min(_ringWidth - 1, y * _baseCellSize + _baseCellSize / 2);
                float h00 = cornerHeights[y * cornerWidth + x];
                float h10 = cornerHeights[y * cornerWidth + x + 1];
                float h01 = cornerHeights[(y + 1) * cornerWidth + x];
                float h11 = cornerHeights[(y + 1) * cornerWidth + x + 1];
                float hc = _sampler.UsesRuntimeSurfaceGeneration
                    ? _sampler.GetPresentationSurfaceHeight(ac, zc)
                    : _sampler.GetSurfaceHeight(ac, zc);
                _baselineReceiverHeights[y * width + x] = hc;
                _baseline[y * width + x] = new HeightRange(
                    Mathf.FloorToInt(Mathf.Min(Mathf.Min(h00, h10), Mathf.Min(Mathf.Min(h01, h11), hc))),
                    Mathf.CeilToInt(Mathf.Max(Mathf.Max(h00, h10), Mathf.Max(Mathf.Max(h01, h11), hc))));
            }
        }
    }

    private int GetBaseCellIndex(Vector2I column, int width, int height)
    {
        int around = Mathf.PosMod(column.X, _aroundLength);
        int across = Mathf.Clamp(column.Y, 0, _ringWidth - 1);
        int x = Mathf.Clamp(around / _baseCellSize, 0, width - 1);
        int y = Mathf.Clamp(across / _baseCellSize, 0, height - 1);
        return y * width + x;
    }

    private bool IsReceiverCenter(Vector2I column, int index, int width)
    {
        int x = index % width;
        int y = index / width;
        int around = Mathf.Min(_aroundLength - 1, x * _baseCellSize + _baseCellSize / 2);
        int across = Mathf.Min(_ringWidth - 1, y * _baseCellSize + _baseCellSize / 2);
        return Mathf.PosMod(column.X, _aroundLength) == around && column.Y == across;
    }

    private void BuildLevels(HeightRange[] baseCells, CancellationToken cancellation = default)
    {
        _levels.Clear();
        TotalCellCount = 0;
        int width = Mathf.Max(1, Mathf.CeilToInt(_aroundLength / (float)_baseCellSize));
        int height = Mathf.Max(1, Mathf.CeilToInt(_ringWidth / (float)_baseCellSize));
        _levels.Add(new Level(width, height, _baseCellSize, baseCells));
        TotalCellCount += baseCells.Length;
        int cellSize = _baseCellSize;
        while (width > 1 || height > 1)
        {
            cancellation.ThrowIfCancellationRequested();
            Level child = _levels[^1];
            int parentWidth = Mathf.Max(1, (width + 1) / 2);
            int parentHeight = Mathf.Max(1, (height + 1) / 2);
            var parents = new HeightRange[parentWidth * parentHeight];
            for (int y = 0; y < parentHeight; y++)
            for (int x = 0; x < parentWidth; x++)
            {
                int minHeight = int.MaxValue;
                int maxHeight = int.MinValue;
                for (int oy = 0; oy < 2; oy++)
                for (int ox = 0; ox < 2; ox++)
                {
                    int childX = Mathf.Min(x * 2 + ox, child.Width - 1);
                    int childY = Mathf.Min(y * 2 + oy, child.Height - 1);
                    HeightRange range = child.Cells[childY * child.Width + childX];
                    minHeight = Mathf.Min(minHeight, range.MinHeight);
                    maxHeight = Mathf.Max(maxHeight, range.MaxHeight);
                }
                parents[y * parentWidth + x] = new HeightRange(minHeight, maxHeight);
            }
            width = parentWidth;
            height = parentHeight;
            cellSize *= 2;
            _levels.Add(new Level(width, height, cellSize, parents));
            TotalCellCount += parents.Length;
        }
    }

    private static int NextPowerOfTwo(int value)
    {
        int result = 32;
        while (result < value) result = checked(result * 2);
        return result;
    }

    private static float SmoothStep(float from, float to, float value)
    {
        float t = Mathf.Clamp((value - from) / Mathf.Max(to - from, 0.000001f), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    private void PropagateDirtyCells(HashSet<int> dirtyBaseCells)
    {
        if (_levels.Count == 0 || dirtyBaseCells.Count == 0) return;
        var dirtyCoordinates = new HashSet<Vector2I>();
        Level baseLevel = _levels[0];
        foreach (int index in dirtyBaseCells)
        {
            int x = index % baseLevel.Width;
            int y = index / baseLevel.Width;
            baseLevel.Cells[index] = _activeBase[index];
            dirtyCoordinates.Add(new Vector2I(x, y));
        }

        for (int levelIndex = 1; levelIndex < _levels.Count; levelIndex++)
        {
            Level child = _levels[levelIndex - 1];
            Level parent = _levels[levelIndex];
            var dirtyParents = new HashSet<Vector2I>();
            foreach (Vector2I childCoord in dirtyCoordinates)
                dirtyParents.Add(new Vector2I(childCoord.X / 2, childCoord.Y / 2));

            foreach (Vector2I parentCoord in dirtyParents)
            {
                int minHeight = int.MaxValue;
                int maxHeight = int.MinValue;
                for (int oy = 0; oy < 2; oy++)
                for (int ox = 0; ox < 2; ox++)
                {
                    int childX = Mathf.Min(parentCoord.X * 2 + ox, child.Width - 1);
                    int childY = Mathf.Min(parentCoord.Y * 2 + oy, child.Height - 1);
                    HeightRange range = child.Cells[childY * child.Width + childX];
                    minHeight = Mathf.Min(minHeight, range.MinHeight);
                    maxHeight = Mathf.Max(maxHeight, range.MaxHeight);
                }
                parent.Cells[parentCoord.Y * parent.Width + parentCoord.X] = new HeightRange(minHeight, maxHeight);
            }
            dirtyCoordinates = dirtyParents;
        }
    }
}
