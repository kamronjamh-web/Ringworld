using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Underground;

/// <summary>
/// Budgeted sky-openness probe. 17 rays (1 vertical, 8 at 45 degrees, 8 at 10 degrees) march from the camera
/// through the density field; a ray escapes in air above the local surface and blocks on the first solid sample.
/// Sweeps advance round-robin under a per-frame density-sample budget and are cached by camera cell and revision.
/// Pure C#: no Godot nodes, so it runs headless against a synthetic source.
/// </summary>
public sealed class UndergroundEnclosureProbe
{
    public const int RayCount = 17;
    public const double CellMeters = 2.0;
    public const float SurfaceClearanceMeters = 4f;
    private const int MaxCachedCells = 64;

    private readonly double[] _dirAround = new double[RayCount];
    private readonly double[] _dirVertical = new double[RayCount];
    private readonly double[] _dirAcross = new double[RayCount];
    private readonly double[] _distance = new double[RayCount];
    private readonly sbyte[] _result = new sbyte[RayCount]; // 0 marching, 1 escaped, -1 blocked/exhausted
    private readonly Dictionary<(long, long, long), float> _cache = new();
    private bool _sweeping;
    private bool _sweepFailed;
    private double _originAround, _originVertical, _originAcross;
    private (long, long, long) _sweepCell;
    private ulong _sweepRevision;
    private ulong _cacheRevision = ulong.MaxValue;
    private int _cursor;

    public double MaxDistanceMeters { get; set; } = 512.0;
    public int SampleBudgetPerFrame { get; set; } = 24;
    /// <summary>Latest completed openness in 0..1; 1 until the first sweep completes.</summary>
    public float Openness { get; private set; } = 1f;
    /// <summary>True when the latest sweep could not query the source at its origin.</summary>
    public bool SourceFailed { get; private set; }
    public bool SweepInProgress => _sweeping;
    public int LastSampleCount { get; private set; }
    public int LastSurfaceQueryCount { get; private set; }
    public long TotalSweepsCompleted { get; private set; }
    public long CacheHits { get; private set; }

    public UndergroundEnclosureProbe()
    {
        int index = 0;
        _dirAround[index] = 0; _dirVertical[index] = 1; _dirAcross[index] = 0; index++;
        foreach (double elevationDegrees in new[] { 45.0, 10.0 })
        {
            double elevation = elevationDegrees * Math.PI / 180.0;
            for (int i = 0; i < 8; i++, index++)
            {
                double azimuth = i * Math.PI / 4.0;
                _dirAround[index] = Math.Cos(azimuth) * Math.Cos(elevation);
                _dirAcross[index] = Math.Sin(azimuth) * Math.Cos(elevation);
                _dirVertical[index] = Math.Sin(elevation);
            }
        }
    }

    public void Invalidate()
    {
        _cache.Clear();
        _sweeping = false;
    }

    /// <summary>Advances the probe by one frame. Returns the density samples spent this frame.</summary>
    public int Update(IUndergroundProbeSource source, double around, double vertical, double across)
    {
        LastSampleCount = 0;
        LastSurfaceQueryCount = 0;
        if (!source.IsAvailable) { _sweeping = false; return 0; }
        ulong revision = source.Revision;
        if (revision != _cacheRevision) { _cache.Clear(); _cacheRevision = revision; if (_sweeping && _sweepRevision != revision) _sweeping = false; }
        var cell = ((long)Math.Floor(around / CellMeters), (long)Math.Floor(vertical / CellMeters), (long)Math.Floor(across / CellMeters));
        if (!_sweeping)
        {
            if (_cache.TryGetValue(cell, out float cached)) { Openness = cached; SourceFailed = false; CacheHits++; return 0; }
            BeginSweep(cell, revision, around, vertical, across);
        }
        return Advance(source);
    }

    private void BeginSweep((long, long, long) cell, ulong revision, double around, double vertical, double across)
    {
        _sweeping = true;
        _sweepFailed = false;
        _sweepCell = cell;
        _sweepRevision = revision;
        // March from the cell centre so a cached answer is never reused for a different probe position.
        _originAround = (cell.Item1 + .5) * CellMeters;
        _originVertical = (cell.Item2 + .5) * CellMeters;
        _originAcross = (cell.Item3 + .5) * CellMeters;
        Array.Clear(_distance);
        Array.Clear(_result);
        _cursor = 0;
    }

    private int Advance(IUndergroundProbeSource source)
    {
        int budget = Math.Max(1, SampleBudgetPerFrame);
        int spent = 0, active = 0;
        for (int i = 0; i < RayCount; i++) if (_result[i] == 0) active++;
        while (spent < budget && active > 0)
        {
            int ray = _cursor;
            _cursor = (_cursor + 1) % RayCount;
            if (_result[ray] != 0) continue;
            double step = _distance[ray] < 32.0 ? 2.0 : Math.Clamp(_distance[ray] / 8.0, 2.0, 16.0);
            double t = _distance[ray] + step;
            if (t > MaxDistanceMeters) { _result[ray] = -1; active--; continue; }
            _distance[ray] = t;
            double a = _originAround + _dirAround[ray] * t, v = _originVertical + _dirVertical[ray] * t, c = _originAcross + _dirAcross[ray] * t;
            spent++;
            if (!source.TryGetDensity(a, v, c, out float density))
            {
                // An unreadable sample ends the ray as open: failing toward Surface mode is the safe direction.
                if (ray == 0 && t <= step + 1e-6) _sweepFailed = true;
                _result[ray] = 1; active--; continue;
            }
            if (density <= 0f) { _result[ray] = -1; active--; continue; }
            LastSurfaceQueryCount++;
            if (source.TryGetSurfaceHeight(a, c, out float surface) && v > surface + SurfaceClearanceMeters) { _result[ray] = 1; active--; }
        }
        LastSampleCount = spent;
        if (active > 0) return spent;
        int escaped = 0;
        for (int i = 0; i < RayCount; i++) if (_result[i] > 0) escaped++;
        Openness = escaped / (float)RayCount;
        SourceFailed = _sweepFailed;
        _sweeping = false;
        TotalSweepsCompleted++;
        if (_cache.Count >= MaxCachedCells) _cache.Clear();
        if (!_sweepFailed) _cache[_sweepCell] = Openness;
        return spent;
    }
}
