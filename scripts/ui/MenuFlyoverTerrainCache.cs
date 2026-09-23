using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.UI;

/// <summary>Bounded, menu-only scrolling terrain texture cache. Rows are published atomically.</summary>
public sealed class MenuFlyoverTerrainCache : IDisposable
{
    public const int Rows = 2049;
    public const int Columns = 1537;
    public const int SpacingMeters = 125;
    public const int BehindRows = 384;
    public const int WidthMeters = 192000;
    public const int DepthMeters = 256000;
    private const string SettingsPath = "res://resources/voxel/voxel_terrain_settings_ring_integration.tres";
    private const string ConfigPath = "res://resources/launcher/certified_ring_config.tres";

    private readonly VoxelTerrainSettings _settings;
    private readonly RingTerrainSampler _sampler;
    private readonly Image _heightImage;
    private readonly Image _colorImage;
    private readonly Image _availabilityImage;
    private readonly long[] _publishedRow;
    private readonly Queue<long> _queuedRows = new();
    private readonly HashSet<long> _queuedSet = new();
    private readonly Color[] _workingHeights;
    private readonly Color[] _workingColors;
    private long _workingRow;
    private int _workingColumn;
    private bool _hasWorkingRow;
    private long _firstRow = long.MinValue;
    private long _lastRow = long.MinValue;
    private bool _initialReady;
    private bool _texturesDirty;
    private bool _availabilityDirty;
    private long _lastDataUploadTicks;
    private bool _disposed;

    public ImageTexture HeightTexture { get; }
    public ImageTexture ColorTexture { get; }
    public ImageTexture AvailabilityTexture { get; }
    public long CircumferenceMeters { get; }
    public double RadiusMeters { get; }
    public int RingWidthMeters { get; }
    public long SampleCount { get; private set; }
    public int CommittedRows { get; private set; }
    public int ResidentRows { get; private set; }
    public bool InitialReady => _initialReady;
    public bool WindowReady => ResidentRows == RowCount;
    public int CacheMisses { get; private set; }
    public long StartAroundMeters { get; }
    public int RowCount { get; }
    public int ColumnCount { get; }
    public int SampleSpacingMeters { get; }
    public int BehindRowCount { get; }
    public int RouteSelectionSamples { get; private set; }
    public int QueueCount => _queuedRows.Count + (_hasWorkingRow ? 1 : 0);
    public int TextureUploadCount { get; private set; }

    public void ResolveBackdropSample(long around, int across, out float heightKm, out Color linearRgb)
    {
        TerrainSample sample = _sampler.GetTerrainSample(PositiveMod(around, CircumferenceMeters), Math.Clamp(across, 0, RingWidthMeters - 1));
        heightKm = sample.ResolveDisplayedHeight(sample.SurfaceHeight) * .001f;
        linearRgb = RingTerrainVisuals.ResolveMacroTextureColor(sample, sample.ResolveDisplayedHeight(sample.SurfaceHeight), _sampler,
            new Color(.08f, .16f, .07f), new Color(.55f, .52f, .44f));
        linearRgb.A = sample.HasSurfaceWater ? 1f : 0f;
        if (sample.HasSurfaceWater) linearRgb = new Color(.025f, .13f, .24f, 1f);
    }

    public MenuFlyoverTerrainCache(int seed, int spacingMeters = SpacingMeters, long? startAroundMeters = null)
    {
        if (spacingMeters is not (125 or 250 or 500)) throw new ArgumentOutOfRangeException(nameof(spacingMeters), "Menu terrain spacing must be 125, 250, or 500 metres.");
        SampleSpacingMeters = spacingMeters;
        RowCount = DepthMeters / SampleSpacingMeters + 1;
        ColumnCount = WidthMeters / SampleSpacingMeters + 1;
        BehindRowCount = 48000 / SampleSpacingMeters;
        _publishedRow = new long[RowCount];
        _workingHeights = new Color[ColumnCount];
        _workingColors = new Color[ColumnCount];
        Resource config = GD.Load<Resource>(ConfigPath) ?? throw new InvalidOperationException("Missing certified ring config.");
        double requestedRadius = config.Get("default_radius_km").AsDouble() * 1000.0;
        RingWidthMeters = (int)Math.Round(config.Get("ring_width_km").AsDouble() * 1000.0);
        CircumferenceMeters = (long)Math.Round(Math.Tau * requestedRadius / 32.0) * 32;
        RadiusMeters = CircumferenceMeters / Math.Tau;
        _settings = (VoxelTerrainSettings)(GD.Load<Resource>(SettingsPath) ?? throw new InvalidOperationException("Missing terrain settings.")).Duplicate(true);
        _settings.Seed = seed;
        _settings.BaseSurfaceHeightVoxels = (int)Math.Round(config.Get("base_surface_height").AsDouble());
        _settings.HeightAmplitudeVoxels = (int)Math.Round(config.Get("height_amplitude").AsDouble());
        _settings.DetailAmplitudeVoxels = (int)Math.Round(config.Get("detail_amplitude").AsDouble());
        _settings.ContinentalContrast = (float)config.Get("continental_contrast").AsDouble();
        _settings.RegionalDetailStrength = (float)config.Get("regional_detail_strength").AsDouble();
        _settings.RarePeakY = (int)Math.Round(config.Get("rare_peak_height").AsDouble());
        _settings.BedrockY = -256;
        _settings.SeaLevelY = 0;
        _settings.AutoRaiseRarePeakToBuildLimit = false;
        _sampler = new RingTerrainSampler(_settings, true, CircumferenceMeters, RadiusMeters, RingWidthMeters,
            (int)Math.Round(config.Get("build_height_y").AsDouble()));
        RingTerrainVisuals.PreloadTexturePalette();
        StartAroundMeters = startAroundMeters ?? SelectLandRichStart(seed);
        Array.Fill(_publishedRow, long.MinValue);
        _heightImage = Image.CreateEmpty(ColumnCount, RowCount, false, Image.Format.Rf);
        _colorImage = Image.CreateEmpty(ColumnCount, RowCount, false, Image.Format.Rgba8);
        _availabilityImage = Image.CreateEmpty(1, RowCount, false, Image.Format.R8);
        HeightTexture = ImageTexture.CreateFromImage(_heightImage);
        ColorTexture = ImageTexture.CreateFromImage(_colorImage);
        AvailabilityTexture = ImageTexture.CreateFromImage(_availabilityImage);
    }

    private long SelectLandRichStart(int seed)
    {
        long alignedOffset = PositiveMod((long)seed * 1103515245L + 12345L, CircumferenceMeters);
        alignedOffset = alignedOffset / SampleSpacingMeters * SampleSpacingMeters;
        long bestAround = alignedOffset;
        int bestScore = int.MinValue;
        int[] forward = { 0, 20000, 60000, 30000, 30000 };
        int[] acrossOffset = { 0, 0, 0, -20000, 20000 };
        for (int candidate = 0; candidate < 32; candidate++)
        {
            long around = PositiveMod(alignedOffset + candidate * (CircumferenceMeters / 32), CircumferenceMeters);
            around = around / SampleSpacingMeters * SampleSpacingMeters;
            int dry = 0;
            int minimum = int.MaxValue;
            int maximum = int.MinValue;
            int shallowPenalty = 0;
            for (int probe = 0; probe < forward.Length; probe++)
            {
                TerrainSample sample = _sampler.GetTerrainSample(PositiveMod(around + forward[probe], CircumferenceMeters),
                    Math.Clamp(RingWidthMeters / 2 + acrossOffset[probe], 0, RingWidthMeters - 1));
                RouteSelectionSamples++;
                if (!sample.HasSurfaceWater) dry++;
                else if (sample.WaterDepth < 150) shallowPenalty += 250;
                minimum = Math.Min(minimum, sample.SurfaceHeight);
                maximum = Math.Max(maximum, sample.SurfaceHeight);
            }
            int score = dry * 2000 + Math.Min(1000, maximum - minimum) - shallowPenalty;
            if (score > bestScore) { bestScore = score; bestAround = around; }
        }
        return bestAround;
    }

    public void EnsureCoverage(long centerRow)
    {
        long first = centerRow - BehindRowCount;
        long last = first + RowCount - 1;
        if (first == _firstRow) return;
        _firstRow = first;
        _lastRow = last;
        int queuedCount = _queuedRows.Count;
        for (int index = 0; index < queuedCount; index++)
        {
            long queued = _queuedRows.Dequeue();
            if (queued >= first && queued <= last) _queuedRows.Enqueue(queued);
            else _queuedSet.Remove(queued);
        }
        if (_hasWorkingRow && (_workingRow < first || _workingRow > last)) _hasWorkingRow = false;
        ResidentRows = 0;
        for (int offset = 0; offset < RowCount; offset++)
        {
            long row = first + offset;
            if (IsRowPublished(row))
            {
                ResidentRows++;
                continue;
            }
            int slot = Mod(row, RowCount);
            // A recycled physical row must not display the previous location's
            // height or color while the new row is still being sampled.
            if (_publishedRow[slot] != long.MinValue)
            {
                _publishedRow[slot] = long.MinValue;
                var rect = new Rect2I(0, slot, ColumnCount, 1);
                _heightImage.FillRect(rect, Colors.Transparent);
                _availabilityImage.FillRect(rect, Colors.Transparent);
                _availabilityDirty = true;
            }
            QueueRow(row);
        }
        UploadDirtyTextures();
    }

    public void UpdateBudgeted()
    {
        long started = Stopwatch.GetTimestamp();
        int sampled = 0;
        while (sampled < 2048 && Stopwatch.GetElapsedTime(started).TotalMilliseconds < 2.0)
        {
            if (!_hasWorkingRow)
            {
                if (_queuedRows.Count == 0) break;
                _workingRow = _queuedRows.Dequeue();
                _queuedSet.Remove(_workingRow);
                _workingColumn = 0;
                _hasWorkingRow = true;
            }
            SampleColumn(_workingRow, _workingColumn++);
            sampled++;
            if (_workingColumn == ColumnCount) CommitWorkingRow();
        }
        UploadDirtyTextures();
    }

    public bool IsRowPublished(long row) => _publishedRow[Mod(row, RowCount)] == row;
    public int PhysicalSlot(long row) => Mod(row, RowCount);

    public bool TryGetHeightKilometers(long row, int column, out float heightKilometers)
    {
        if (!IsRowPublished(row))
        {
            heightKilometers = 0.0f;
            return false;
        }
        heightKilometers = _heightImage.GetPixel(Math.Clamp(column, 0, ColumnCount - 1), Mod(row, RowCount)).R;
        return true;
    }

    private void QueueRow(long row)
    {
        if (IsRowPublished(row) || (_hasWorkingRow && _workingRow == row) || !_queuedSet.Add(row)) return;
        _queuedRows.Enqueue(row);
        CacheMisses++;
    }

    private void SampleColumn(long row, int column)
    {
        long around = Mod(row * (long)SampleSpacingMeters, CircumferenceMeters);
        int across = Math.Clamp((column - ColumnCount / 2) * SampleSpacingMeters + RingWidthMeters / 2, 0, RingWidthMeters - 1);
        TerrainSample sample = _sampler.GetTerrainSample(around, across);
        float heightKilometers = sample.ResolveDisplayedHeight(sample.SurfaceHeight) * 0.001f;
        _workingHeights[column] = new Color(heightKilometers, 0, 0, 1);
        Color color = RingTerrainVisuals.ResolveMacroTextureColor(sample, sample.ResolveDisplayedHeight(sample.SurfaceHeight), _sampler,
            new Color(.08f, .16f, .07f), new Color(.55f, .52f, .44f));
        color.A = 0.0f;
        if (sample.HasSurfaceWater)
        {
            color = sample.WaterKind == SurfaceWaterKind.Ocean ? new Color(.025f, .13f, .24f) : new Color(.04f, .23f, .28f);
            color.A = 1.0f;
        }
        _workingColors[column] = color.LinearToSrgb();
        SampleCount++;
    }

    private void CommitWorkingRow()
    {
        if (_workingRow < _firstRow || _workingRow > _lastRow)
        {
            _hasWorkingRow = false;
            return;
        }
        int slot = Mod(_workingRow, RowCount);
        if (_publishedRow[slot] == long.MinValue) ResidentRows++;
        for (int column = 0; column < ColumnCount; column++)
        {
            _heightImage.SetPixel(column, slot, _workingHeights[column]);
            _colorImage.SetPixel(column, slot, _workingColors[column]);
            if (column == 0) _availabilityImage.SetPixel(0, slot, Colors.Transparent);
        }
        _publishedRow[slot] = _workingRow;
        _texturesDirty = true;
        CommittedRows++;
        _hasWorkingRow = false;
    }

    private void UploadDirtyTextures()
    {
        if (_availabilityDirty) { AvailabilityTexture.Update(_availabilityImage); _availabilityDirty = false; TextureUploadCount++; }
        bool forceUpload = !_initialReady && QueueCount == 0 && ResidentRows == RowCount;
        if (!_texturesDirty || (!forceUpload && Stopwatch.GetElapsedTime(_lastDataUploadTicks).TotalMilliseconds < 100.0)) return;
        HeightTexture.Update(_heightImage); ColorTexture.Update(_colorImage);
        for (int row = 0; row < RowCount; row++) if (_publishedRow[row] != long.MinValue) _availabilityImage.SetPixel(0, row, Colors.White);
        AvailabilityTexture.Update(_availabilityImage); TextureUploadCount += 3; _lastDataUploadTicks = Stopwatch.GetTimestamp(); _texturesDirty = false;
        if (ResidentRows == RowCount && QueueCount == 0) _initialReady = true;
    }

    private static int Mod(long value, int divisor) => (int)((value % divisor + divisor) % divisor);
    private static long Mod(long value, long divisor) => (value % divisor + divisor) % divisor;
    private static long PositiveMod(long value, long divisor) => (value % divisor + divisor) % divisor;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sampler.Dispose();
        _settings.Dispose();
        HeightTexture.Dispose();
        ColorTexture.Dispose();
        AvailabilityTexture.Dispose();
        _heightImage.Dispose();
        _colorImage.Dispose();
        _availabilityImage.Dispose();
    }
}
