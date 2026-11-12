using System;
using System.Collections.Generic;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>
/// T3R-3 (T3.md Spec E4, "Exclusions"): where a river may not be dangerous because players spend time there. Within <see cref="SiteMarginMeters"/> of an emitter site disc, a Wall Engine
/// stadium (the forecourt segment with its influence radius) or the Weather Lung footprint, or within <see cref="SpawnRadiusMeters"/> of the nominal fresh-spawn point, a span that would be
/// fierce or torrent becomes a landing. The shapes are the ones <see cref="DrainageBarriers"/> uses. Immutable and thread-safe.
/// </summary>
public sealed class RiverExclusions
{
    public const double SiteMarginMeters = 1500d, SpawnRadiusMeters = 3000d;

    private readonly double _circumference;
    private readonly List<(double Around, double Across, double Reach)> _discs = new();
    private readonly List<(double Around, double Across, double Half, double Reach)> _stadia = new();
    private (double Around, double Across, double Hx, double Hy)? _lung;
    private (double Around, double Across)? _spawn;

    private RiverExclusions(double circumference) { _circumference = circumference; }

    public int ZoneCount => _discs.Count + _stadia.Count + (_lung.HasValue ? 1 : 0) + (_spawn.HasValue ? 1 : 0);

    /// <summary>Test hook: zones made of the given discs (centre and reach in metres), for the negative controls.</summary>
    internal static RiverExclusions Custom(double circumference, IEnumerable<(double Around, double Across, double Reach)> discs)
    {
        var zones = new RiverExclusions(circumference);
        zones._discs.AddRange(discs);
        return zones;
    }

    /// <summary>The zones of a recipe, or null when it has no emitter sites (nothing to keep clear).</summary>
    public static RiverExclusions? Build(TerrainGenerationParameters recipe)
    {
        if (!recipe.HasEmitterSites) return null;
        EmitterSiteTerrainShaper shaper = EmitterSiteTerrainShaper.GetOrCreate(recipe);
        var zones = new RiverExclusions(recipe.CircumferenceM);
        foreach (EmitterSite site in shaper.Registry.Sites) zones._discs.Add((site.Around, site.Across, site.InfluenceRadius + SiteMarginMeters));
        if (shaper.Climate != null)
        {
            double half = shaper.Climate.Recipe.ForecourtLengthM * 0.5, reach = shaper.Climate.Recipe.InfluenceRadiusM + SiteMarginMeters;
            foreach (ClimateMachine m in shaper.Climate.Machines) zones._stadia.Add((m.Around, m.Across, half, reach));
        }
        if (shaper.WeatherLung != null)
            zones._lung = (shaper.WeatherLung.Site.Around, shaper.WeatherLung.Site.Across, shaper.WeatherLung.Recipe.BoundHalfXM, shaper.WeatherLung.Recipe.BoundHalfYM);
        if (EmitterSiteNavigator.TryComputeNominalFreshSpawn(shaper.Registry, EmitterSiteNavigator.DefaultFarKm, out double around, out double across)) zones._spawn = (around, across);
        return zones;
    }

    private const double CellMeters = 50_000d;
    private int[][]? _cells;
    private readonly object _cellsLock = new();

    private double Wrap(double delta)
    {
        delta %= _circumference;
        if (delta > _circumference / 2) delta -= _circumference; else if (delta < -_circumference / 2) delta += _circumference;
        return delta;
    }

    /// <summary>Buckets of 50 km along the ring holding every zone whose reach touches the bucket, so a query looks at a handful of zones instead of all of them (the whole ring has about 600).</summary>
    private int[][] Cells()
    {
        int[][]? cells = _cells;
        if (cells != null) return cells;
        lock (_cellsLock)
        {
            if (_cells != null) return _cells;
            int n = Math.Max(1, (int)Math.Ceiling(_circumference / CellMeters));
            var lists = new List<int>[n];
            void Add(int code, double around, double extent)
            {
                int lo = (int)Math.Floor((around - extent) / CellMeters), hi = (int)Math.Floor((around + extent) / CellMeters);
                if (hi - lo + 1 >= n) { lo = 0; hi = n - 1; }
                for (int c = lo; c <= hi; c++) (lists[((c % n) + n) % n] ??= new List<int>()).Add(code);
            }
            for (int i = 0; i < _discs.Count; i++) Add(i, _discs[i].Around, _discs[i].Reach);
            for (int i = 0; i < _stadia.Count; i++) Add((1 << 24) | i, _stadia[i].Around, _stadia[i].Half + _stadia[i].Reach);
            if (_lung is { } lung) Add(2 << 24, lung.Around, lung.Hx + SiteMarginMeters);
            if (_spawn is { } spawn) Add(3 << 24, spawn.Around, SpawnRadiusMeters);
            var result = new int[n][];
            for (int c = 0; c < n; c++) result[c] = lists[c]?.ToArray() ?? Array.Empty<int>();
            _cells = result;
            return result;
        }
    }

    /// <summary>
    /// Distance to the nearest exclusion limit at the point: negative inside an exclusion zone, positive outside (metres to the nearest limit among the zones near the point;
    /// +infinity when none is near).
    /// </summary>
    public double Clearance(double around, double across)
    {
        int[][] cells = Cells();
        double wrapped = around % _circumference;
        if (wrapped < 0d) wrapped += _circumference;
        int[] near = cells[Math.Min(cells.Length - 1, (int)(wrapped / CellMeters))];
        double best = double.PositiveInfinity;
        foreach (int code in near)
        {
            int index = code & 0xFFFFFF;
            switch (code >> 24)
            {
                case 0:
                {
                    (double a, double x, double reach) = _discs[index];
                    double da = Wrap(around - a), dw = across - x;
                    best = Math.Min(best, Math.Sqrt(da * da + dw * dw) - reach);
                    break;
                }
                case 1:
                {
                    (double a, double x, double half, double reach) = _stadia[index];
                    double sx = Math.Max(0d, Math.Abs(Wrap(around - a)) - half), dw = across - x;
                    best = Math.Min(best, Math.Sqrt(sx * sx + dw * dw) - reach);
                    break;
                }
                case 2:
                {
                    (double a, double x, double hx, double hy) = _lung!.Value;
                    double ex = Math.Max(0d, Math.Abs(Wrap(around - a)) - hx), ey = Math.Max(0d, Math.Abs(across - x) - hy);
                    best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey) - SiteMarginMeters);
                    break;
                }
                default:
                {
                    (double a, double x) = _spawn!.Value;
                    double da = Wrap(around - a), dw = across - x;
                    best = Math.Min(best, Math.Sqrt(da * da + dw * dw) - SpawnRadiusMeters);
                    break;
                }
            }
        }
        return best;
    }

    /// <summary>True when any point of the base curve of the span (sampled every 50 m and at both ends) lies inside a zone.</summary>
    public bool Touches(RiverNetwork net, int stem, double s0, double s1, double stepMeters = 50d)
    {
        RiverStemCurve curve = net.Curves[stem];
        int count = Math.Max(1, (int)Math.Ceiling((s1 - s0) / stepMeters));
        for (int k = 0; k <= count; k++)
        {
            double s = s0 + (s1 - s0) * k / count;
            curve.Eval(s, out double x, out double y, out _, out _);
            if (Clearance(curve.OriginAround + x, curve.OriginAcross + y) < 0d) return true;
        }
        return false;
    }

    /// <summary>Flags each span that touches a zone.</summary>
    public bool[] Mark(RiverNetwork net, RiverSpan[] spans)
    {
        var flags = new bool[spans.Length];
        for (int i = 0; i < spans.Length; i++) flags[i] = Touches(net, net.Reaches[spans[i].Reach].Stem, spans[i].SStart, spans[i].SEnd);
        return flags;
    }
}

/// <summary>T3R-3: the span gradient of the bake (E1), from the stored profile after the levels are built.</summary>
public static class RiverSpanBuilder
{
    public const double WindowHalfMeters = 2500d;

    /// <summary>
    /// Sets <see cref="RiverSpan.Sh"/> on every span: <c>max((W(s_m - x0) - W(s_m + x1)) / (x0 + x1), 1e-4)</c> with W the stored profile, <c>s_m</c> the span's middle,
    /// <c>x0 = min(2500, s_m)</c> and <c>x1 = min(2500, s_end - s_m)</c> with s_end the end of the stem. Without a profile (a build without levels) the reach gradient is used.
    /// </summary>
    public static void FillGradients(RiverNetwork net, RiverSpan[] spans)
    {
        for (int i = 0; i < spans.Length; i++)
        {
            RiverReach reach = net.Reaches[spans[i].Reach];
            int stem = reach.Stem;
            double gradient = reach.S;
            if (net.Knots.Count > 0 && net.Knots.KnotCount(stem) > 0)
            {
                RiverStem st = net.Stems[stem];
                double send = net.Reaches[st.ReachStart + st.ReachCount - 1].SEnd, m = 0.5 * ((double)spans[i].SStart + spans[i].SEnd);
                double x0 = Math.Min(WindowHalfMeters, m), x1 = Math.Min(WindowHalfMeters, send - m);
                if (x0 + x1 > 1d)
                {
                    double v = (RiverLevel.Stored(net, stem, m - x0) - RiverLevel.Stored(net, stem, m + x1)) / (x0 + x1);
                    if (!double.IsNaN(v)) gradient = v;
                }
            }
            spans[i].Sh = (float)Math.Max(gradient, 1e-4);
        }
    }
}
