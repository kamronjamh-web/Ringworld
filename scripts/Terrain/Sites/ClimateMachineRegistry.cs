using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RingworldTechDemo.Terrain.Sites;

[Flags]
public enum ClimateMachineFlags : ushort
{
    None = 0,
    Offline = 1,
    /// <summary>The forecourt stencil touched water; the forecourt needs a raised quay.</summary>
    Quay = 2,
    CutFillConflict = 4,
}

/// <summary>One Wall Engine. Positions are on the canonical 2 m lattice; the machine sits on a rim wall (across 0 or W).</summary>
public readonly struct ClimateMachine
{
    /// <summary>Station * 2 + side.</summary>
    public int Index { get; init; }
    /// <summary>Emitter-row gap (0..99) between emitter rows Station and Station + 1.</summary>
    public int Station { get; init; }
    /// <summary>0 = across 0 (west wall), 1 = across W (east wall).</summary>
    public byte Side { get; init; }
    public long Around { get; init; }
    /// <summary>0 or the ring width.</summary>
    public int Across { get; init; }
    /// <summary>Forecourt pad / bay base height in integer metres, presentation space.</summary>
    public int PadHeight { get; init; }
    /// <summary>
    /// World yaw of the machine root about +Y in degrees (Terrain3D frame: local X = around, local Z = -across). The asset frame is +X along the ring,
    /// -Z into the wall, +Z forecourt: west wall faces +across = world -Z, so yaw 180; east wall faces world +Z, so yaw 0.
    /// </summary>
    public float YawDegrees { get; init; }
    public ClimateMachineFlags Flags { get; init; }
    public ClimateAnomalyKind Anomaly { get; init; }
    /// <summary>Radius from the footprint segment outside which this machine never affects terrain.</summary>
    public float InfluenceRadius { get; init; }
    public ulong MachineHash { get; init; }

    public bool IsOffline => (Flags & ClimateMachineFlags.Offline) != 0;
}

/// <summary>Parked weather anomaly of an offline machine: an ellipse in canonical (around, across) space.</summary>
public readonly record struct ClimateAnomaly(int MachineIndex, ClimateAnomalyKind Kind, double CentreAround, double CentreAcross,
    double RadiusAround, double RadiusAcross, int SystemId, ulong Hash);

/// <summary>
/// Deterministic Wall Engine list (docs/emitter-grid-climate-machines-spec.md section C1/C2): one machine per rim wall at every emitter-row gap,
/// 100 gaps x 2 walls = 200 (the main row removes no gap; it is one of the 100 rows and only constrains which gaps may be offline).
/// Immutable after construction; lookups are allocation-free and safe from any thread.
///
/// Index layout: machine = station * 2 + side, station g sits mid-way between emitter rows g and g + 1 (around = RowPhase + (g + 1/2) * pitch).
/// Offline selection (6 of 200): stations ranked by a seed hash, accepted greedily when clear of the main-row exclusion and at least
/// MinOfflineSpacingStations (circularly) from every accepted station; the dead wall is a seed bit. If a ranking jams with fewer than K stations,
/// the next deterministic re-ranking is used (attempt counter in the hash label); attempt 0 is the plain spec algorithm.
/// The accepted stations with the highest land fraction (ties: lower station) become droughts, the rest storms.
/// </summary>
public sealed class ClimateMachineRegistry
{
    private const int MaxSelectionAttempts = 64;
    private static readonly ConcurrentDictionary<(ulong, ulong, ulong), Lazy<ClimateMachineRegistry>> Cache = new();

    private readonly ClimateMachine[] _machines;
    private readonly ClimateAnomaly[] _anomalies;
    private readonly int[] _offline;
    private readonly long _circumference;
    private readonly double _pitch, _phase, _width, _halfSegment, _influenceSquared;
    private readonly int _stations;

    public ClimateMachineRecipe Recipe { get; }
    public ulong NaturalIdentity { get; }
    public ulong EmitterIdentity { get; }
    public ulong Identity { get; }
    public int MainRow { get; }
    /// <summary>Which re-ranking attempt satisfied the offline constraints (0 = the plain spec ranking).</summary>
    public int SelectionAttempt { get; }
    public double BuildMilliseconds { get; private set; }
    public int BuildThreads { get; private set; }
    public int Count => _machines.Length;
    public ReadOnlySpan<ClimateMachine> Machines => _machines;
    public ClimateMachine this[int index] => _machines[index];
    /// <summary>Offline machine indices, ascending.</summary>
    public ReadOnlySpan<int> OfflineIndices => _offline;
    /// <summary>Anomalies ordered by machine index.</summary>
    public ReadOnlySpan<ClimateAnomaly> Anomalies => _anomalies;
    public double InfluenceRadius => Math.Sqrt(_influenceSquared);

    private ClimateMachineRegistry(ClimateMachineRecipe recipe, ulong naturalIdentity, ulong emitterIdentity, int mainRow, int attempt,
        ClimateMachine[] machines, ClimateAnomaly[] anomalies)
    {
        Recipe = recipe;
        NaturalIdentity = naturalIdentity;
        EmitterIdentity = emitterIdentity;
        MainRow = mainRow;
        SelectionAttempt = attempt;
        _machines = machines;
        _anomalies = anomalies;
        var offline = new List<int>();
        foreach (ClimateMachine m in machines) if (m.IsOffline) offline.Add(m.Index);
        _offline = offline.ToArray();
        _circumference = (long)recipe.CircumferenceM;
        _pitch = recipe.RowPitchM;
        _phase = recipe.RowPhaseM;
        _width = recipe.WidthM;
        _stations = recipe.Stations;
        _halfSegment = recipe.ForecourtLengthM * 0.5d;
        _influenceSquared = recipe.InfluenceRadiusM * recipe.InfluenceRadiusM;
        Identity = ComputeIdentity();
    }

    /// <summary>Cached build keyed by (natural terrain definition hash, recipe hash, emitter registry identity).</summary>
    public static ClimateMachineRegistry GetOrBuild(ClimateMachineRecipe recipe, INaturalHeightSource source, EmitterSiteRegistry emitters, int threads = 0)
    {
        var key = (source.Identity, recipe.Hash, emitters.Identity);
        return Cache.GetOrAdd(key, _ => new Lazy<ClimateMachineRegistry>(
            () => Build(recipe, source, emitters, threads), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public static void ClearCache() => Cache.Clear();

    /// <summary>Uncached build against a real emitter registry (main row and site-disjointness invariant). Output is independent of <paramref name="threads"/>.</summary>
    public static ClimateMachineRegistry Build(ClimateMachineRecipe recipe, INaturalHeightSource source, EmitterSiteRegistry emitters, int threads = 0)
    {
        if (emitters == null) throw new ArgumentNullException(nameof(emitters));
        return BuildCore(recipe, source, emitters.MainRow, emitters.Identity, emitters, threads);
    }

    /// <summary>Uncached build for tests that sweep many seeds without building 595 emitter sites: explicit main row, no emitter disjointness check.</summary>
    internal static ClimateMachineRegistry BuildWithMainRow(ClimateMachineRecipe recipe, INaturalHeightSource source, int mainRow, int threads = 0)
        => BuildCore(recipe, source, mainRow, 0UL, null, threads);

    private struct PadResult { public int Pad; public ClimateMachineFlags Flags; }

    private static ClimateMachineRegistry BuildCore(ClimateMachineRecipe recipe, INaturalHeightSource source, int mainRow, ulong emitterIdentity,
        EmitterSiteRegistry? emitters, int threads)
    {
        if (recipe == null) throw new ArgumentNullException(nameof(recipe));
        if (source == null) throw new ArgumentNullException(nameof(source));
        int workers = threads > 0 ? threads : Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
        long start = Stopwatch.GetTimestamp();
        int count = recipe.MachineCount;
        long circumference = (long)recipe.CircumferenceM;
        int width = (int)recipe.WidthM;

        // 1. Placement and forecourt pad per machine (results by index, so thread count cannot matter).
        var arounds = new long[recipe.Stations];
        for (int g = 0; g < recipe.Stations; g++)
        {
            double a = (recipe.RowPhaseM + (g + 0.5d) * recipe.RowPitchM) % circumference;
            if (a < 0d) a += circumference;
            arounds[g] = (long)EmitterSiteRecipe.Snap2(a) % circumference;
        }
        var pads = new PadResult[count];
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = workers },
            () => source.CreateWorkerCopy(),
            (index, _, worker) => { pads[index] = EvaluatePad(recipe, worker, arounds[index / recipe.Sides], index % recipe.Sides, width); return worker; },
            worker => worker.Dispose());

        // 2. Offline selection (serial, pure function of seed, main row and recipe).
        int offlineCount = recipe.OfflineCount;
        int[] stationsOffline = SelectOfflineStations(recipe, mainRow, offlineCount, out int attempt);

        // 3. Dead side, anomaly ellipse land fraction, balanced kind split.
        var deadSide = new byte[recipe.Stations];
        var kind = new ClimateAnomalyKind[count];
        using (INaturalHeightSource worker = source.CreateWorkerCopy())
        {
            var land = new double[stationsOffline.Length];
            for (int k = 0; k < stationsOffline.Length; k++)
            {
                int g = stationsOffline[k];
                deadSide[g] = (byte)(EmitterSiteHash.Derive(recipe.Seed, "climate-side", g) & 1UL);
                land[k] = LandFraction(recipe, worker, arounds[g], deadSide[g], width, circumference);
            }
            int droughts = (stationsOffline.Length + 1) / 2;
            var order = new int[stationsOffline.Length];
            for (int k = 0; k < order.Length; k++) order[k] = k;
            Array.Sort(order, (x, y) => land[x] != land[y] ? land[y].CompareTo(land[x]) : stationsOffline[x].CompareTo(stationsOffline[y]));
            for (int rank = 0; rank < order.Length; rank++)
            {
                int g = stationsOffline[order[rank]];
                kind[g * recipe.Sides + deadSide[g]] = rank < droughts ? ClimateAnomalyKind.Drought : ClimateAnomalyKind.Storm;
            }
        }

        // 4. Records and anomalies.
        var machines = new ClimateMachine[count];
        float influence = (float)recipe.InfluenceRadiusM;
        for (int i = 0; i < count; i++)
        {
            int g = i / recipe.Sides, side = i % recipe.Sides;
            ClimateMachineFlags flags = pads[i].Flags;
            if (kind[i] != ClimateAnomalyKind.None) flags |= ClimateMachineFlags.Offline;
            machines[i] = FinishRecord(recipe, new ClimateMachine
            {
                Index = i, Station = g, Side = (byte)side, Around = arounds[g], Across = side == 0 ? 0 : width, PadHeight = pads[i].Pad,
                YawDegrees = side == 0 ? 180f : 0f, Flags = flags, Anomaly = kind[i], InfluenceRadius = influence,
            });
        }
        var anomalies = new List<ClimateAnomaly>();
        foreach (ClimateMachine m in machines)
            if (m.Anomaly != ClimateAnomalyKind.None) anomalies.Add(MakeAnomaly(recipe, m));

        var registry = new ClimateMachineRegistry(recipe, source.Identity, emitterIdentity, mainRow, attempt, machines, anomalies.ToArray())
        {
            BuildMilliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency,
            BuildThreads = workers,
        };
        registry.AssertInvariants(emitters);
        return registry;
    }

    private static PadResult EvaluatePad(ClimateMachineRecipe recipe, INaturalHeightSource worker, long around, int side, int width)
    {
        Span<double> samples = stackalloc double[9];
        int n = 0;
        double sea = worker.SeaLevel;
        bool wet = false;
        foreach (int along in new[] { -8_000, 0, 8_000 })
            foreach (int depth in new[] { 500, 1_500, 2_500 })
            {
                long a = around + along;
                a %= (long)recipe.CircumferenceM; if (a < 0) a += (long)recipe.CircumferenceM;
                double h = worker.NaturalHeight(a, side == 0 ? depth : width - depth);
                samples[n++] = h;
                if (h < sea + 2d) wet = true;
            }
        samples.Sort();
        double pad = Math.Max(Math.Round(samples[4]), sea + recipe.MinPadAboveSeaM);
        ClimateMachineFlags flags = wet ? ClimateMachineFlags.Quay : ClimateMachineFlags.None;
        foreach (double h in samples)
            if (Math.Abs(h - pad) > recipe.MaxPadCutFillM) { flags |= ClimateMachineFlags.CutFillConflict; break; }
        return new PadResult { Pad = (int)pad, Flags = flags };
    }

    /// <summary>Distance in rows from a gap centre to the main row (circular).</summary>
    private static double MainDistanceRows(int station, int mainRow, int stations)
    {
        double d = Math.Abs(station + 0.5d - mainRow);
        return Math.Min(d, stations - d);
    }

    internal static bool StationExcluded(ClimateMachineRecipe recipe, int station, int mainRow)
        => MainDistanceRows(station, mainRow, recipe.Stations) < recipe.MainExclusionStations;

    internal static int CircularStationDistance(int a, int b, int stations)
    {
        int d = Math.Abs(a - b);
        return Math.Min(d, stations - d);
    }

    private static int[] SelectOfflineStations(ClimateMachineRecipe recipe, int mainRow, int wanted, out int attemptUsed)
    {
        int stations = recipe.Stations;
        for (int attempt = 0; attempt < MaxSelectionAttempts; attempt++)
        {
            string label = attempt == 0 ? "climate-offline" : "climate-offline-" + attempt;
            var ranking = new (double Key, int Station)[stations];
            for (int g = 0; g < stations; g++) ranking[g] = (EmitterSiteHash.Unit(EmitterSiteHash.Derive(recipe.Seed, label, g)), g);
            Array.Sort(ranking, (x, y) => x.Key != y.Key ? x.Key.CompareTo(y.Key) : x.Station.CompareTo(y.Station));
            var accepted = new List<int>();
            foreach ((double _, int g) in ranking)
            {
                if (accepted.Count >= wanted) break;
                if (StationExcluded(recipe, g, mainRow)) continue;
                bool clear = true;
                foreach (int other in accepted)
                    if (CircularStationDistance(g, other, stations) < recipe.MinOfflineSpacingStations) { clear = false; break; }
                if (clear) accepted.Add(g);
            }
            if (accepted.Count == wanted)
            {
                accepted.Sort();
                attemptUsed = attempt;
                return accepted.ToArray();
            }
        }
        throw new InvalidOperationException($"Could not place {wanted} offline climate machines with spacing {recipe.MinOfflineSpacingStations}.");
    }

    /// <summary>Anomaly geometry for a dead machine of the given kind and wall.</summary>
    private static (double CentreAcross, double RadiusAround, double RadiusAcross) AnomalyGeometry(ClimateMachineRecipe recipe, ClimateAnomalyKind kind, int side)
    {
        bool storm = kind == ClimateAnomalyKind.Storm;
        double inset = storm ? recipe.StormInsetM : recipe.DroughtInsetM;
        return (side == 0 ? inset : recipe.WidthM - inset, storm ? recipe.StormRadiusAroundM : recipe.DroughtRadiusAroundM,
            storm ? recipe.StormRadiusAcrossM : recipe.DroughtRadiusAcrossM);
    }

    /// <summary>
    /// Land fraction of a neutral reference ellipse (mean of the storm and drought geometry; the kind is not known yet) from 64 deterministic
    /// Vogel-spiral samples, across clamped onto the floor.
    /// </summary>
    private static double LandFraction(ClimateMachineRecipe recipe, INaturalHeightSource worker, long around, int side, int width, long circumference)
    {
        double ra = (recipe.StormRadiusAroundM + recipe.DroughtRadiusAroundM) * 0.5d;
        double rw = (recipe.StormRadiusAcrossM + recipe.DroughtRadiusAcrossM) * 0.5d;
        double inset = (recipe.StormInsetM + recipe.DroughtInsetM) * 0.5d;
        double centre = side == 0 ? inset : width - inset;
        const int Samples = 64;
        int land = 0;
        for (int i = 0; i < Samples; i++)
        {
            double r = Math.Sqrt((i + 0.5d) / Samples), angle = i * 2.399963229728653d;
            long a = around + (long)Math.Round(ra * r * Math.Cos(angle) / 2d) * 2L;
            a %= circumference; if (a < 0) a += circumference;
            double x = Math.Clamp(centre + rw * r * Math.Sin(angle), 0d, width);
            if (worker.NaturalHeight(a, (int)Math.Round(x / 2d) * 2) > worker.SeaLevel) land++;
        }
        return land / (double)Samples;
    }

    private static ClimateMachine FinishRecord(ClimateMachineRecipe recipe, ClimateMachine m)
    {
        ulong h = EmitterSiteHash.Offset;
        h = EmitterSiteHash.Mix(h, recipe.Hash);
        h = EmitterSiteHash.Mix(h, (ulong)m.Index);
        h = EmitterSiteHash.Mix(h, (ulong)m.Around);
        h = EmitterSiteHash.Mix(h, (ulong)(uint)m.Across);
        h = EmitterSiteHash.Mix(h, (ulong)(uint)m.PadHeight);
        h = EmitterSiteHash.Mix(h, (ulong)m.Flags);
        h = EmitterSiteHash.Mix(h, (ulong)m.Anomaly);
        h = EmitterSiteHash.Mix(h, BitConverter.SingleToUInt32Bits(m.YawDegrees));
        h = EmitterSiteHash.Mix(h, BitConverter.SingleToUInt32Bits(m.InfluenceRadius));
        return new ClimateMachine
        {
            Index = m.Index, Station = m.Station, Side = m.Side, Around = m.Around, Across = m.Across, PadHeight = m.PadHeight,
            YawDegrees = m.YawDegrees, Flags = m.Flags, Anomaly = m.Anomaly, InfluenceRadius = m.InfluenceRadius, MachineHash = h == 0UL ? 1UL : h,
        };
    }

    private static ClimateAnomaly MakeAnomaly(ClimateMachineRecipe recipe, ClimateMachine m)
    {
        var (centreAcross, ra, rw) = AnomalyGeometry(recipe, m.Anomaly, m.Side);
        ulong h = EmitterSiteHash.Mix(EmitterSiteHash.Mix(EmitterSiteHash.Offset, m.MachineHash), (ulong)m.Anomaly);
        h = EmitterSiteHash.Mix(h, BitConverter.DoubleToUInt64Bits(centreAcross));
        h = EmitterSiteHash.Mix(h, BitConverter.DoubleToUInt64Bits(ra));
        h = EmitterSiteHash.Mix(h, BitConverter.DoubleToUInt64Bits(rw));
        return new ClimateAnomaly(m.Index, m.Anomaly, m.Around, centreAcross, ra, rw, -20_000 - m.Index, h == 0UL ? 1UL : h);
    }

    // ---- Lookups -------------------------------------------------------------------------------------------------

    private double Wrap(double around)
    {
        double c = _circumference;
        double a = around % c;
        return a < 0d ? a + c : a;
    }

    private double SignedAround(double wrapped, long machineAround)
    {
        double d = wrapped - machineAround;
        double half = _circumference >> 1;
        if (d > half) d -= _circumference; else if (d < -half) d += _circumference;
        return d;
    }

    /// <summary>Station whose gap contains the wrapped around coordinate: [phase + g * pitch, phase + (g + 1) * pitch).</summary>
    private int StationOf(double wrapped)
    {
        int g = (int)Math.Floor((wrapped - _phase) / _pitch) % _stations;
        return g < 0 ? g + _stations : g;
    }

    /// <summary>
    /// O(1) lookup: the only machine that can influence (around, across) is on the wall nearer to it in the gap containing the point.
    /// <paramref name="du"/> is the signed along-ring offset from the machine, <paramref name="dw"/> the across offset from the wall.
    /// Influence is the distance to the forecourt footprint segment (a stadium) below the influence radius.
    /// </summary>
    public bool TryGetInfluencingMachine(double around, double across, out int machineIndex, out double du, out double dw)
    {
        double a = Wrap(around);
        int side = across < _width * 0.5d ? 0 : 1;
        int index = StationOf(a) * 2 + side;
        ref readonly ClimateMachine m = ref _machines[index];
        du = SignedAround(a, m.Around);
        dw = across - m.Across;
        machineIndex = index;
        double sx = Math.Max(0d, Math.Abs(du) - _halfSegment);
        return sx * sx + dw * dw < _influenceSquared;
    }

    public bool TryGetInfluencingMachine(long around, double across, out int machineIndex, out double du, out double dw)
        => TryGetInfluencingMachine((double)around, across, out machineIndex, out du, out dw);

    /// <summary>Reference implementation used by tests: scans every machine.</summary>
    public int FindInfluencingMachineBruteForce(double around, double across, out int matchCount)
    {
        double a = Wrap(around);
        int found = -1;
        matchCount = 0;
        for (int i = 0; i < _machines.Length; i++)
        {
            double du = SignedAround(a, _machines[i].Around), dw = across - _machines[i].Across;
            double sx = Math.Max(0d, Math.Abs(du) - _halfSegment);
            if (sx * sx + dw * dw < _influenceSquared) { found = i; matchCount++; }
        }
        return found;
    }

    /// <summary>
    /// Nearest machine centre. <see cref="ClimateMachineFilter.Any"/> inspects the two stations that bracket the point on both walls (the nearest
    /// machine on a wall is always a bracketing one); the other filters scan the at most 12 offline machines.
    /// </summary>
    public bool TryGetNearestMachine(double around, double across, ClimateMachineFilter filter, out int machineIndex, out double du, out double dw)
    {
        double a = Wrap(around);
        double best = double.MaxValue;
        machineIndex = -1; du = 0d; dw = 0d;
        if (filter == ClimateMachineFilter.Any)
        {
            int lo = (int)Math.Floor((a - _phase) / _pitch - 0.5d) % _stations;
            if (lo < 0) lo += _stations;
            for (int k = 0; k < 2; k++)
            {
                int g = (lo + k) % _stations;
                for (int side = 0; side < 2; side++) Consider(g * 2 + side, a, across, ref best, ref machineIndex, ref du, ref dw);
            }
        }
        else
        {
            foreach (int index in _offline)
            {
                ClimateAnomalyKind kind = _machines[index].Anomaly;
                if (filter == ClimateMachineFilter.Storm && kind != ClimateAnomalyKind.Storm) continue;
                if (filter == ClimateMachineFilter.Drought && kind != ClimateAnomalyKind.Drought) continue;
                Consider(index, a, across, ref best, ref machineIndex, ref du, ref dw);
            }
        }
        return machineIndex >= 0;
    }

    private void Consider(int index, double wrapped, double across, ref double best, ref int bestIndex, ref double bestDu, ref double bestDw)
    {
        double tu = SignedAround(wrapped, _machines[index].Around), tw = across - _machines[index].Across;
        double d2 = tu * tu + tw * tw;
        if (d2 < best || (d2 == best && index < bestIndex)) { best = d2; bestIndex = index; bestDu = tu; bestDw = tw; }
    }

    /// <summary>Reference implementation used by tests.</summary>
    public int FindNearestMachineBruteForce(double around, double across, ClimateMachineFilter filter)
    {
        double a = Wrap(around), best = double.MaxValue;
        int found = -1;
        for (int i = 0; i < _machines.Length; i++)
        {
            ClimateAnomalyKind kind = _machines[i].Anomaly;
            if (filter == ClimateMachineFilter.Offline && !_machines[i].IsOffline) continue;
            if (filter == ClimateMachineFilter.Storm && kind != ClimateAnomalyKind.Storm) continue;
            if (filter == ClimateMachineFilter.Drought && kind != ClimateAnomalyKind.Drought) continue;
            double tu = SignedAround(a, _machines[i].Around), tw = across - _machines[i].Across;
            double d2 = tu * tu + tw * tw;
            if (d2 < best) { best = d2; found = i; }
        }
        return found;
    }

    /// <summary>
    /// Writes the indices of all machines whose centre is within <paramref name="radius"/> into <paramref name="results"/> (ascending) and returns how many
    /// exist, which may exceed the span length (then the written entries are some, not necessarily the lowest, matches). Allocation-free.
    /// </summary>
    public int GetMachinesWithinRadius(double around, double across, double radius, Span<int> results)
    {
        double a = Wrap(around);
        double r2 = radius * radius;
        int centre = (int)Math.Floor((a - _phase) / _pitch) % _stations;
        if (centre < 0) centre += _stations;
        int span = (int)Math.Ceiling(radius / _pitch) + 2;
        int total = 0, written = 0;
        if (2 * span + 1 >= _stations)
        {
            for (int index = 0; index < _machines.Length; index++) Add(index, a, across, r2, results, ref total, ref written);
        }
        else
        {
            for (int k = -span; k <= span; k++)
            {
                int g = ((centre + k) % _stations + _stations) % _stations;
                Add(g * 2, a, across, r2, results, ref total, ref written);
                Add(g * 2 + 1, a, across, r2, results, ref total, ref written);
            }
        }
        for (int i = 1; i < written; i++)
        {
            int v = results[i], j = i - 1;
            while (j >= 0 && results[j] > v) { results[j + 1] = results[j]; j--; }
            results[j + 1] = v;
        }
        return total;
    }

    private void Add(int index, double wrapped, double across, double r2, Span<int> results, ref int total, ref int written)
    {
        double tu = SignedAround(wrapped, _machines[index].Around), tw = across - _machines[index].Across;
        if (tu * tu + tw * tw > r2) return;
        if (written < results.Length) results[written++] = index;
        total++;
    }

    /// <summary>
    /// Anomaly weights at a point (0 outside, 1 inside the unfeathered core, smoothstep between), O(K &lt;= 12) with signed-wrap distances.
    /// Storm and drought weights are the maximum over anomalies of that kind.
    /// </summary>
    public void EvaluateAnomalies(double around, double across, out double storm, out double drought)
    {
        storm = 0d; drought = 0d;
        double a = Wrap(around);
        foreach (ClimateAnomaly anomaly in _anomalies)
        {
            double w = AnomalyWeight(anomaly, a, across);
            if (anomaly.Kind == ClimateAnomalyKind.Storm) storm = Math.Max(storm, w); else drought = Math.Max(drought, w);
        }
    }

    /// <summary>Strongest anomaly at the point; false when every weight is 0.</summary>
    public bool TryGetDominantAnomaly(double around, double across, out ClimateAnomaly anomaly, out double weight)
    {
        double a = Wrap(around);
        anomaly = default; weight = 0d;
        foreach (ClimateAnomaly candidate in _anomalies)
        {
            double w = AnomalyWeight(candidate, a, across);
            if (w > weight) { weight = w; anomaly = candidate; }
        }
        return weight > 0d;
    }

    public ClimateAnomaly AnomalyOf(int machineIndex)
    {
        foreach (ClimateAnomaly anomaly in _anomalies) if (anomaly.MachineIndex == machineIndex) return anomaly;
        throw new ArgumentException("Machine is online: " + machineIndex);
    }

    private double AnomalyWeight(in ClimateAnomaly anomaly, double wrappedAround, double across)
    {
        double du = SignedAround(wrappedAround, (long)anomaly.CentreAround) / anomaly.RadiusAround;
        double dw = (across - anomaly.CentreAcross) / anomaly.RadiusAcross;
        double rho = Math.Sqrt(du * du + dw * dw);
        double feather = Recipe.AnomalyFeather;
        if (rho >= 1d) return 0d;
        if (rho <= 1d - feather) return 1d;
        double t = (rho - (1d - feather)) / feather;
        return 1d - t * t * (3d - 2d * t);
    }

    // ---- Identity and invariants ---------------------------------------------------------------------------------

    /// <summary>Canonical little-endian byte image of every record; used for cross-thread determinism checks.</summary>
    public byte[] ToCanonicalBytes()
    {
        using var stream = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(stream);
        writer.Write(Recipe.Hash);
        writer.Write(NaturalIdentity);
        writer.Write(EmitterIdentity);
        writer.Write(MainRow);
        foreach (ClimateMachine m in _machines)
        {
            writer.Write(m.Index); writer.Write(m.Station); writer.Write(m.Side); writer.Write(m.Around); writer.Write(m.Across); writer.Write(m.PadHeight);
            writer.Write(m.YawDegrees); writer.Write((ushort)m.Flags); writer.Write((byte)m.Anomaly); writer.Write(m.InfluenceRadius); writer.Write(m.MachineHash);
        }
        foreach (ClimateAnomaly a in _anomalies)
        {
            writer.Write(a.MachineIndex); writer.Write((byte)a.Kind); writer.Write(a.CentreAround); writer.Write(a.CentreAcross);
            writer.Write(a.RadiusAround); writer.Write(a.RadiusAcross); writer.Write(a.SystemId); writer.Write(a.Hash);
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
        h = EmitterSiteHash.Mix(h, EmitterIdentity);
        h = EmitterSiteHash.Mix(h, (ulong)MainRow);
        foreach (ClimateMachine m in _machines) h = EmitterSiteHash.Mix(h, m.MachineHash);
        foreach (ClimateAnomaly a in _anomalies) h = EmitterSiteHash.Mix(h, a.Hash);
        return h == 0UL ? 1UL : h;
    }

    /// <summary>Distance from a point to the machine's footprint segment (along the wall, +-half forecourt length).</summary>
    private double SegmentDistance(in ClimateMachine m, double around, double across)
    {
        double du = SignedAround(Wrap(around), m.Around), dw = across - m.Across;
        double sx = Math.Max(0d, Math.Abs(du) - _halfSegment);
        return Math.Sqrt(sx * sx + dw * dw);
    }

    private void AssertInvariants(EmitterSiteRegistry? emitters)
    {
        ClimateMachineRecipe r = Recipe;
        if (_machines.Length != r.MachineCount) throw new InvalidOperationException($"Climate machine count {_machines.Length}.");
        int storms = 0, droughts = 0;
        var offlineStations = new List<int>();
        foreach (ClimateMachine m in _machines)
        {
            if (m.Index != m.Station * r.Sides + m.Side) throw new InvalidOperationException($"Climate machine {m.Index} index layout.");
            if ((m.Around & 1L) != 0 || (m.Across & 1) != 0) throw new InvalidOperationException($"Climate machine {m.Index} off the 2 m lattice.");
            if (m.Across != (m.Side == 0 ? 0 : (int)r.WidthM)) throw new InvalidOperationException($"Climate machine {m.Index} is not on its wall.");
            if (m.Around < 0 || m.Around >= _circumference) throw new InvalidOperationException($"Climate machine {m.Index} around out of range.");
            if (m.InfluenceRadius >= _width * 0.5d) throw new InvalidOperationException($"Climate machine {m.Index} influence reaches the far wall.");
            if (m.Anomaly != ClimateAnomalyKind.None != m.IsOffline) throw new InvalidOperationException($"Climate machine {m.Index} offline/anomaly mismatch.");
            if (m.Anomaly == ClimateAnomalyKind.Storm) storms++;
            if (m.Anomaly == ClimateAnomalyKind.Drought) droughts++;
            if (m.IsOffline) offlineStations.Add(m.Station);
        }
        int wanted = r.OfflineCount;
        if (_offline.Length != wanted || storms + droughts != wanted || droughts != (wanted + 1) / 2 || storms != wanted / 2)
            throw new InvalidOperationException($"Climate offline split {storms} storm / {droughts} drought of {_offline.Length}.");
        for (int i = 0; i < offlineStations.Count; i++)
        {
            if (StationExcluded(r, offlineStations[i], MainRow)) throw new InvalidOperationException($"Offline station {offlineStations[i]} is inside the main exclusion.");
            for (int j = i + 1; j < offlineStations.Count; j++)
                if (CircularStationDistance(offlineStations[i], offlineStations[j], _stations) < r.MinOfflineSpacingStations)
                    throw new InvalidOperationException($"Offline stations {offlineStations[i]} and {offlineStations[j]} are closer than the minimum spacing.");
        }
        if (emitters == null) return;
        foreach (ClimateMachine m in _machines)
            foreach (EmitterSite s in emitters.Sites)
                if (SegmentDistance(m, s.Around, s.Across) <= m.InfluenceRadius + s.InfluenceRadius)
                    throw new InvalidOperationException($"Climate machine {m.Index} influence overlaps emitter site {s.Index}.");
    }
}
