using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RingworldTechDemo.Terrain.Sites;

[Flags]
public enum EmitterSiteFlags : ushort
{
    None = 0,
    Land = 1,
    Island = 2,
    Mountain = 4,
    /// <summary>The grid point itself was open ocean (never nudged).</summary>
    Ocean = 8,
    EnclosureRaise = 16,
    CutFillConflict = 32,
    Nudged = 64,
}

/// <summary>One registry record. Positions are on the canonical 2 m lattice.</summary>
public readonly struct EmitterSite
{
    public int Index { get; init; }
    public SiteKind Kind { get; init; }
    public long Around { get; init; }
    public int Across { get; init; }
    /// <summary>Pad height in integer metres, presentation space.</summary>
    public int PadHeight { get; init; }
    /// <summary>Approach bearing in degrees, measured in the (around, across) plane.</summary>
    public float BetaDegrees { get; init; }
    /// <summary>World yaw of the POI root about +Y in degrees, 67.5 + beta (Terrain3D frame: local Z = -across, AcrossDirection = -1).</summary>
    public float YawDegrees { get; init; }
    public EmitterSiteFlags Flags { get; init; }
    /// <summary>Radius outside which this site never affects terrain.</summary>
    public float InfluenceRadius { get; init; }
    /// <summary>Conservative island/enclosure raise radius (0 when neither applies).</summary>
    public float RaiseRadius { get; init; }
    /// <summary>Distance between the chosen position and the grid point.</summary>
    public float NudgeMeters { get; init; }
    /// <summary>Deepest cut of the terraced cone below natural terrain (0 on islands), from a 64-azimuth ring scan.</summary>
    public float MaxCutMeters { get; init; }
    /// <summary>Highest fill of the embankment cone above natural terrain (0 on islands).</summary>
    public float MaxFillMeters { get; init; }
    public ulong SiteHash { get; init; }

    public bool IsIsland => (Flags & EmitterSiteFlags.Island) != 0;
    public bool IsMountain => (Flags & EmitterSiteFlags.Mountain) != 0;
    public bool IsMain => Kind == SiteKind.Main;
}

/// <summary>
/// Deterministic emitter site list (docs/emitter-site-terrain-spec.md section 1 plus the MAIN SPIRE DECISIONS).
/// 595 sites: the 99 regular rows of 6 and one main site on the ring centre line. Immutable after construction;
/// lookups are O(1), allocation-free and safe from any thread.
///
/// Index layout (compact and dense, so <see cref="Count"/> == number of usable sites and every index in [0, Count) is valid):
///   regular site at grid cell (row, col), row != MainRow:  (row &lt; MainRow ? row : row - 1) * 6 + col   -> 0..593
///   main site:                                             Count - 1 = 594
/// A fixed 600-slot layout with six Removed slots would force every consumer (MultiMesh instances, teleport spinner,
/// streamer arrays) to filter dead entries; the compact layout costs one compare in the O(1) lookup instead.
/// Regular indices after the main row therefore depend on the seed (the main row); the main index is always 594.
/// </summary>
public sealed class EmitterSiteRegistry
{
    private static readonly ConcurrentDictionary<(ulong, ulong), Lazy<EmitterSiteRegistry>> Cache = new();

    private readonly EmitterSite[] _sites;
    private readonly long _circumference;
    private readonly double _pitch, _phase, _firstColumn, _columnSpacing;
    private readonly int _rows, _columns;
    private readonly double[] _influenceSquared;

    public EmitterSiteRecipe Recipe { get; }
    public ulong NaturalIdentity { get; }
    public ulong Identity { get; }
    public double BuildMilliseconds { get; private set; }
    public int BuildThreads { get; private set; }
    /// <summary>Usable site count: 594 regular + 1 main = 595.</summary>
    public int Count => _sites.Length;
    public int RegularCount => _sites.Length - 1;
    /// <summary>Grid row that holds the main site instead of six regular sites.</summary>
    public int MainRow { get; }
    /// <summary>Index of the main site (always the last one).</summary>
    public int MainIndex => _sites.Length - 1;
    public EmitterSite MainSite => _sites[_sites.Length - 1];
    /// <summary>True when no row offered acceptable land and the main site is an enlarged island on the seed-preferred row.</summary>
    public bool MainIsIslandFallback { get; }
    /// <summary>Number of rows whose centre-line candidate was acceptable land (before the dense water check).</summary>
    public int AcceptableMainRowCount { get; }
    public ReadOnlySpan<EmitterSite> Sites => _sites;
    public EmitterSite this[int index] => _sites[index];

    private EmitterSiteRegistry(EmitterSiteRecipe recipe, ulong naturalIdentity, EmitterSite[] sites, int mainRow, bool mainFallback, int acceptableRows)
    {
        Recipe = recipe;
        NaturalIdentity = naturalIdentity;
        _sites = sites;
        MainRow = mainRow;
        MainIsIslandFallback = mainFallback;
        AcceptableMainRowCount = acceptableRows;
        _circumference = (long)recipe.CircumferenceM;
        _pitch = recipe.RowPitchM;
        _phase = recipe.RowPhaseM;
        _rows = recipe.Rows;
        _columns = recipe.Columns;
        _firstColumn = recipe.ColumnOffsetsM[0];
        _columnSpacing = _columns > 1 ? recipe.ColumnOffsetsM[1] - recipe.ColumnOffsetsM[0] : 1d;
        _influenceSquared = new double[sites.Length];
        for (int i = 0; i < sites.Length; i++) _influenceSquared[i] = (double)sites[i].InfluenceRadius * sites[i].InfluenceRadius;
        Identity = ComputeIdentity();
    }

    /// <summary>Registry index of the regular site at (<paramref name="row"/>, <paramref name="col"/>); row must not be <see cref="MainRow"/>.</summary>
    public int RegularIndex(int row, int col) => (row < MainRow ? row : row - 1) * _columns + col;

    /// <summary>Cached build keyed by (natural terrain definition hash, recipe hash).</summary>
    public static EmitterSiteRegistry GetOrBuild(EmitterSiteRecipe recipe, INaturalHeightSource source, int threads = 0)
    {
        var key = (source.Identity, recipe.Hash);
        return Cache.GetOrAdd(key, _ => new Lazy<EmitterSiteRegistry>(
            () => Build(recipe, source, threads), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public static void ClearCache() => Cache.Clear();

    /// <summary>Uncached build. Output is independent of <paramref name="threads"/> and scheduling order.</summary>
    public static EmitterSiteRegistry Build(EmitterSiteRecipe recipe, INaturalHeightSource source, int threads = 0)
    {
        if (recipe == null) throw new ArgumentNullException(nameof(recipe));
        if (source == null) throw new ArgumentNullException(nameof(source));
        int workers = threads > 0 ? threads : Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
        long start = Stopwatch.GetTimestamp();
        var options = new ParallelOptions { MaxDegreeOfParallelism = workers };

        // 1. Score every row's centre-line main candidate (results by row index, so thread count cannot matter).
        var evaluations = new MainRowEvaluation[recipe.Rows];
        Parallel.For(0, recipe.Rows, options,
            () => new EmitterSiteBuilder(recipe, source.CreateWorkerCopy(), source),
            (row, _, builder) => { evaluations[row] = builder.EvaluateMainRow(row); return builder; },
            builder => builder.Dispose());

        // 2. Deterministic row choice (serial): best seed-weighted acceptable row whose full enclosure is dry, else the island fallback.
        MainRowEvaluation mainEval = SelectMainRow(recipe, source, evaluations, out bool fallback, out int acceptable);

        // 3. Regular sites for the 99 other rows, then the main site appended as the last index.
        int regularCount = (recipe.Rows - 1) * recipe.Columns;
        var sites = new EmitterSite[regularCount + 1];
        int mainRow = mainEval.Row;
        Parallel.For(0, regularCount, options,
            () => new EmitterSiteBuilder(recipe, source.CreateWorkerCopy(), source),
            (index, _, builder) =>
            {
                int compactRow = index / recipe.Columns, col = index % recipe.Columns;
                int row = compactRow < mainRow ? compactRow : compactRow + 1;
                sites[index] = builder.BuildRegular(row, col, index);
                return builder;
            },
            builder => builder.Dispose());
        using (var builder = new EmitterSiteBuilder(recipe, source.CreateWorkerCopy(), source))
            sites[regularCount] = builder.BuildMain(mainEval, fallback, regularCount);

        var registry = new EmitterSiteRegistry(recipe, source.Identity, sites, mainRow, fallback, acceptable)
        {
            BuildMilliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency,
            BuildThreads = workers,
        };
        registry.AssertInvariants();
        return registry;
    }

    /// <summary>
    /// Main row selection. Rank acceptable rows by (10 * pad height above sea + score + MainSeedWeight * rowHash), i.e. lowest pad first, flatness and seed hash secondary, and take the first whose dense
    /// 40 x 96 enclosure check is dry; if none exists, take the first row (in order from the seed-preferred row) whose grid
    /// point is clear of the summit exclusion zone and make it an enlarged island at its grid point.
    /// </summary>
    private static MainRowEvaluation SelectMainRow(EmitterSiteRecipe recipe, INaturalHeightSource source, MainRowEvaluation[] evaluations,
        out bool fallback, out int acceptableCount)
    {
        var ranked = new List<(double Key, int Row)>();
        foreach (MainRowEvaluation e in evaluations)
        {
            if (!e.Acceptable) continue;
            double rowHash = EmitterSiteHash.Unit(EmitterSiteHash.Derive(recipe.Seed, "emitter-main-rank", e.Row));
            ranked.Add(((e.Pad - source.SeaLevel) * 10d + e.Score + recipe.MainSeedWeight * rowHash, e.Row));
        }
        acceptableCount = ranked.Count;
        ranked.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Row.CompareTo(b.Row));
        using (var selector = new EmitterSiteBuilder(recipe, source.CreateWorkerCopy(), source))
        {
            foreach (var candidate in ranked)
            {
                MainRowEvaluation e = evaluations[candidate.Row];
                if (selector.MainEnclosureIsDry(e)) { fallback = false; return e; }
            }
            for (int k = 0; k < recipe.Rows; k++)
            {
                int row = (recipe.SeedPreferredMainRow + k) % recipe.Rows;
                if (selector.GridIsExcluded(row)) continue;
                fallback = true;
                return new MainRowEvaluation { Row = row };
            }
        }
        throw new InvalidOperationException("No row is available for the main emitter site.");
    }

    private int RowOf(double wrappedAround)
    {
        int row = (int)Math.Floor((wrappedAround - _phase) / _pitch + 0.5);
        row %= _rows;
        return row < 0 ? row + _rows : row;
    }

    /// <summary>
    /// O(1) lookup (section 1.4): the only site that can influence (around, across) is the one in the nearest grid
    /// cell, because influence radius plus nudge is below half the grid pitch on both axes. The main row has a single
    /// cell-wide site, so any point whose nearest row is the main row maps to the main site.
    /// </summary>
    public bool TryGetInfluencingSite(long around, double across, out int siteIndex, out double du, out double dw)
    {
        long a = around % _circumference;
        if (a < 0) a += _circumference;
        int row = RowOf(a);
        int index;
        if (row == MainRow) index = _sites.Length - 1;
        else
        {
            int col = (int)Math.Round((across - _firstColumn) / _columnSpacing);
            if (col < 0) col = 0; else if (col >= _columns) col = _columns - 1;
            index = (row < MainRow ? row : row - 1) * _columns + col;
        }
        ref readonly EmitterSite site = ref _sites[index];
        long d = a - site.Around;
        long half = _circumference >> 1;
        if (d > half) d -= _circumference; else if (d < -half) d += _circumference;
        du = d;
        dw = across - site.Across;
        siteIndex = index;
        return du * du + dw * dw < _influenceSquared[index];
    }

    /// <summary>
    /// Same lookup for a non-integer around coordinate (terrain sampling between lattice points). Bit-identical to the
    /// <see cref="long"/> overload for integer-valued input.
    /// </summary>
    public bool TryGetInfluencingSite(double around, double across, out int siteIndex, out double du, out double dw)
    {
        double circumference = _circumference;
        double a = around % circumference;
        if (a < 0d) a += circumference;
        int row = RowOf(a);
        int index;
        if (row == MainRow) index = _sites.Length - 1;
        else
        {
            int col = (int)Math.Round((across - _firstColumn) / _columnSpacing);
            if (col < 0) col = 0; else if (col >= _columns) col = _columns - 1;
            index = (row < MainRow ? row : row - 1) * _columns + col;
        }
        ref readonly EmitterSite site = ref _sites[index];
        double d = a - site.Around;
        double half = _circumference >> 1;
        if (d > half) d -= circumference; else if (d < -half) d += circumference;
        du = d;
        dw = across - site.Across;
        siteIndex = index;
        return du * du + dw * dw < _influenceSquared[index];
    }

    /// <summary>
    /// Nearest site with no influence cutoff. Every regular site lies within the nudge radius of its grid point, so the
    /// nearest regular site is in the 3x3 grid neighbourhood of the nearest grid cell (the main row contributes nothing
    /// there); the main site is always a candidate.
    /// </summary>
    public bool TryGetNearestSite(long around, double across, out int siteIndex, out double du, out double dw)
    {
        long a = around % _circumference;
        if (a < 0) a += _circumference;
        int row0 = (int)Math.Floor((a - _phase) / _pitch + 0.5);
        int col0 = (int)Math.Round((across - _firstColumn) / _columnSpacing);
        if (col0 < 0) col0 = 0; else if (col0 >= _columns) col0 = _columns - 1;
        double best = double.MaxValue;
        siteIndex = -1; du = 0d; dw = 0d;
        for (int dr = -1; dr <= 1; dr++)
        {
            int row = (row0 + dr) % _rows;
            if (row < 0) row += _rows;
            if (row == MainRow) continue;
            for (int dc = -1; dc <= 1; dc++)
            {
                int col = col0 + dc;
                if (col < 0 || col >= _columns) continue;
                int index = RegularIndex(row, col);
                double tu = SignedAround(a, _sites[index].Around), tw = across - _sites[index].Across;
                double d2 = tu * tu + tw * tw;
                if (d2 < best || (d2 == best && index < siteIndex)) { best = d2; siteIndex = index; du = tu; dw = tw; }
            }
        }
        {
            int index = _sites.Length - 1;
            double tu = SignedAround(a, _sites[index].Around), tw = across - _sites[index].Across;
            double d2 = tu * tu + tw * tw;
            if (d2 < best || (d2 == best && index < siteIndex)) { best = d2; siteIndex = index; du = tu; dw = tw; }
        }
        return siteIndex >= 0;
    }

    /// <summary>
    /// Writes the indices of all sites whose centre is within <paramref name="radius"/> into
    /// <paramref name="results"/> (ascending by index) and returns how many exist, which may exceed the span length.
    /// Allocation-free.
    /// </summary>
    public int GetSitesWithinRadius(long around, double across, double radius, Span<int> results)
    {
        long a = around % _circumference;
        if (a < 0) a += _circumference;
        double reach = radius + Recipe.NudgeRadiusM;
        int rowLo = (int)Math.Floor((a - reach - _phase) / _pitch + 0.5);
        int rowHi = (int)Math.Floor((a + reach - _phase) / _pitch + 0.5);
        int rowCount = Math.Min(_rows, rowHi - rowLo + 1);
        int colLo = Math.Max(0, (int)Math.Round((across - reach - _firstColumn) / _columnSpacing));
        int colHi = Math.Min(_columns - 1, (int)Math.Round((across + reach - _firstColumn) / _columnSpacing));
        double r2 = radius * radius;
        int total = 0;
        for (int row = 0; row < _rows; row++)
        {
            if (row == MainRow) continue;
            int offset = ((row - rowLo) % _rows + _rows) % _rows;
            if (offset >= rowCount) continue;
            for (int col = colLo; col <= colHi; col++)
            {
                int index = RegularIndex(row, col);
                double tu = SignedAround(a, _sites[index].Around), tw = across - _sites[index].Across;
                if (tu * tu + tw * tw > r2) continue;
                if (total < results.Length) results[total] = index;
                total++;
            }
        }
        {
            int index = _sites.Length - 1;
            double tu = SignedAround(a, _sites[index].Around), tw = across - _sites[index].Across;
            if (tu * tu + tw * tw <= r2)
            {
                if (total < results.Length) results[total] = index;
                total++;
            }
        }
        return total;
    }

    private double SignedAround(long wrappedAround, long siteAround)
    {
        long d = wrappedAround - siteAround;
        long half = _circumference >> 1;
        if (d > half) d -= _circumference; else if (d < -half) d += _circumference;
        return d;
    }

    /// <summary>Reference implementation used by tests: scans every site.</summary>
    public int FindInfluencingSiteBruteForce(long around, double across, out int matchCount)
    {
        long a = around % _circumference;
        if (a < 0) a += _circumference;
        int found = -1;
        matchCount = 0;
        for (int i = 0; i < _sites.Length; i++)
        {
            long d = a - _sites[i].Around;
            long half = _circumference >> 1;
            if (d > half) d -= _circumference; else if (d < -half) d += _circumference;
            double dw = across - _sites[i].Across;
            if ((double)d * d + dw * dw < _influenceSquared[i]) { found = i; matchCount++; }
        }
        return found;
    }

    /// <summary>Canonical little-endian byte image of every record; used for cross-thread determinism checks.</summary>
    public byte[] ToCanonicalBytes()
    {
        using var stream = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(stream);
        writer.Write(Recipe.Hash);
        writer.Write(NaturalIdentity);
        writer.Write(MainRow);
        writer.Write(MainIsIslandFallback);
        foreach (EmitterSite s in _sites)
        {
            writer.Write(s.Index); writer.Write((byte)s.Kind); writer.Write(s.Around); writer.Write(s.Across); writer.Write(s.PadHeight);
            writer.Write(s.BetaDegrees); writer.Write(s.YawDegrees); writer.Write((ushort)s.Flags);
            writer.Write(s.InfluenceRadius); writer.Write(s.RaiseRadius); writer.Write(s.NudgeMeters); writer.Write(s.MaxCutMeters); writer.Write(s.MaxFillMeters); writer.Write(s.SiteHash);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private ulong ComputeIdentity()
    {
        ulong h = EmitterSiteHash.Offset;
        h = EmitterSiteHash.Mix(h, (ulong)Recipe.AlgorithmVersion);
        h = EmitterSiteHash.Mix(h, Recipe.Hash);
        h = EmitterSiteHash.Mix(h, NaturalIdentity);
        h = EmitterSiteHash.Mix(h, (ulong)MainRow);
        foreach (EmitterSite s in _sites) h = EmitterSiteHash.Mix(h, s.SiteHash);
        return h == 0UL ? 1UL : h;
    }

    /// <summary>
    /// Asserts influence radius plus nudge is below half the grid pitch on both axes (also for the main site), the influence
    /// discs stay clear of the walls, the main site sits exactly on the centre line, and the main influence disc overlaps no other site's disc.
    /// </summary>
    private void AssertInvariants()
    {
        double halfAcross = _columnSpacing * 0.5;
        double halfAround = _pitch * 0.5;
        double width = Recipe.WidthM;
        foreach (EmitterSite s in _sites)
        {
            double reach = s.InfluenceRadius + Recipe.NudgeRadiusM;
            if (reach >= Math.Min(halfAcross, halfAround))
                throw new InvalidOperationException($"Emitter site {s.Index}: influence {s.InfluenceRadius} m plus nudge reaches the half grid pitch.");
            if (s.Across - s.InfluenceRadius < 0 || s.Across + s.InfluenceRadius > width)
                throw new InvalidOperationException($"Emitter site {s.Index}: influence disc crosses a ring wall.");
        }
        EmitterSite main = MainSite;
        if (main.Kind != SiteKind.Main || main.Across != (int)Recipe.MainAcrossM)
            throw new InvalidOperationException("Main emitter site is not on the ring centre line.");
        if (RowOf(main.Around) != MainRow)
            throw new InvalidOperationException("Main emitter site left its grid row.");
        for (int i = 0; i < _sites.Length - 1; i++)
        {
            long d = Math.Abs(main.Around - _sites[i].Around);
            d = Math.Min(d, _circumference - d);
            double dx = main.Across - _sites[i].Across;
            double distance = Math.Sqrt((double)d * d + dx * dx);
            if (distance <= main.InfluenceRadius + _sites[i].InfluenceRadius)
                throw new InvalidOperationException($"Main emitter site influence overlaps site {i}.");
            if (_sites[i].Kind != SiteKind.Regular || RowOf(_sites[i].Around) == MainRow)
                throw new InvalidOperationException($"Site {i} is not a regular site outside the main row.");
        }
    }
}
