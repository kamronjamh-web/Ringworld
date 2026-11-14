using System;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Water;

/// <summary>What the water sound node needs from a finder (T3R-10d): the real <see cref="WaterSoundSourceFinder"/>, or a fixed list of sources in a test.</summary>
public interface IWaterSoundFinder
{
    /// <summary>The sea's significant wave height in metres; lakes follow at 0.24 of it.</summary>
    double SeaWaveHeight { get; set; }
    /// <summary>The masker M of W6 in dB.</summary>
    double Masker { get; set; }
    /// <summary>Sampler calls of the last <see cref="Update"/>, for the node's diagnostics.</summary>
    int LastSamplerCalls { get; }
    ReadOnlySpan<WaterSoundSource> Sources { get; }
    void Update(in WaterSoundListener listener, int maxSamplerCalls);
}

/// <summary>The listener as a canonical position (docs/work-orders/terrain-vegetation/T3.md, Spec W1): ring around and across in metres, vertical in metres above the sea (the same scale as water levels).</summary>
public readonly record struct WaterSoundListener(double Around, double Vertical, double Across);

/// <summary>
/// T3R-10b (docs/work-orders/terrain-vegetation/T3.md, Spec W4): finds the water near the listener for the water sound. Shores of the sea and of lakes come from the sampler (16 directions,
/// radii 4, 8, 16 and so on, five bisection steps at the first change between wet and dry); rivers from <see cref="RiverCarveOperator.ChannelsNear"/> (complete to 2 km); chutes and falls from
/// <see cref="RiverLevel.DropsNear"/>; open water from the listener's own column. Nothing reads a mesh. Main thread, no allocation in steady state. <see cref="Update"/> advances one slice;
/// <see cref="Sources"/> is the current list, with the distances recomputed for the listener's present position from the kept points (W1: height is distance), so a moving listener hears no steps.
/// The source powers are Spec W3's (<see cref="WaterSoundField"/>). Nothing here has a floor: a power of 0 stays 0, and no source is kept for being small. The shield of W8 is T3R-10f's and stays 0.
/// </summary>
public sealed class WaterSoundSourceFinder : IWaterSoundFinder
{
    public const int MaxSources = 8;
    /// <summary>W4: the sampler radii double from 4 m up to 2,048 m; 16 directions; five bisection steps.</summary>
    public const double FirstShoreRadiusMeters = 4d, MaxShoreRadiusMeters = 2048d;
    public const int Directions = 16, BisectionSteps = 5;
    /// <summary>W4: the river query reaches 2,000 m (the loudest fall of W3's table); the drop list is kept until the listener has moved 50 m.</summary>
    public const double MaxRiverRadiusMeters = 2000d, DropRefreshMeters = 50d;
    /// <summary>The river query is repeated when the listener has moved this far (the nearest point of a river moves with it); the centre column is sampled again at 2 m.</summary>
    public const double RiverRefreshMeters = 25d, CenterRefreshMeters = 2d, SweepRestartMeters = 10d;
    /// <summary>W3: a lake's waves are the sea's times 0.24 (the Lake pattern's scale); river-kind water in a world without a graph times 0.11 (the River pattern's).</summary>
    public const double LakeWaveFactor = 0.24, LegacyRiverWaveFactor = 0.11;
    private const int HitCapacity = 512, CandidateCapacity = 24, DropCapacity = 128;
    private const double LoudestFallWatts = 9810d * 500d * 50d, LoudestFallHalfSize = 20d;

    private readonly Func<long, int, TerrainSample> _sample;
    private readonly RiverGraph? _graph;
    private readonly RiverNetwork? _net;
    private readonly RiverCarveOperator? _operator;
    private readonly long _circumference;
    private readonly int _acrossMax;
    private readonly double _highestWaterY;

    // The kept points. A shore keeps one point, a river or a drop the centre point and half width (so that a listener over the water is at distance 0 from it).
    private struct Kept
    {
        public bool Valid;
        public WaterSoundKind Kind;
        public double Power, HalfSize, Around, Across, Level, Top, WaveHeight, Discharge, Drop, S;
        public int Stem;
    }

    private readonly Kept[] _riverCandidates = new Kept[CandidateCapacity];
    private int _riverCandidateCount;
    private Kept[] _dropCandidates = new Kept[DropCapacity], _dropStaging = new Kept[DropCapacity];
    private readonly double[] _pendLo = new double[CandidateCapacity], _pendHi = new double[CandidateCapacity];
    private int _pendCount, _pendIndex, _stagingCount;
    private int _dropCandidateCount;
    private Kept _seaShore, _lakeShore, _open;
    private readonly RiverChannelNear[] _hits = new RiverChannelNear[HitCapacity];
    private readonly RiverDrop[] _dropBuffer = new RiverDrop[128];
    private readonly int[] _stemSlot = new int[CandidateCapacity];
    private readonly double[] _stemLevel = new double[CandidateCapacity];
    private readonly double[] _rangeLo = new double[CandidateCapacity], _rangeHi = new double[CandidateCapacity];
    private readonly WaterSoundSource[] _sources = new WaterSoundSource[MaxSources];
    private int _sourceCount;

    // Centre column.
    private WaterSoundListener _centerAt;
    private bool _centerValid, _centerBaseWet, _centerOpen;
    private SurfaceWaterKind _centerKind;
    private double _centerLevel;
    private int _updatesSinceCenter;

    // The shore sweep, a state machine of one ray at a time.
    private bool _sweeping;
    private int _direction, _radiusIndex, _bisectLeft;
    // The rays of a sweep: the 16 directions, then (T3R-10d, primary decision 2 of 2026-11-14) the two neighbours at half a spacing of the nearest sea direction and of the nearest lake direction.
    private readonly double[] _rayAngle = new double[Directions + 4], _rayLimit = new double[Directions + 4];
    private int _rayTotal, _bestSeaDirection, _bestLakeDirection;
    private bool _refining;
    private double _sweepRadiusLimit, _sweepOriginAround, _sweepOriginAcross;
    private bool _sweepBaseWet, _rayBisecting;
    private double _rayLo, _rayHi, _rayWetRadius, _rayLevel;
    private SurfaceWaterKind _rayWetKind;
    private double _bestSeaDistance, _bestLakeDistance;
    private Kept _sweepSea, _sweepLake;
    private WaterSoundListener _lastSweepAt;
    private bool _sweepEver;
    private int _updatesSinceSweep;

    // Rivers.
    private WaterSoundListener _lastRiverAt, _lastDropAt;
    private bool _riverEver, _dropsEver;
    private double _lastRiverRadius;
    private int _updatesSinceRiver;
    private readonly int[] _dropStems = new int[CandidateCapacity];
    private int _dropStemCount;

    // Reaches (W2, W4), recomputed when the wave height or the masker changes.
    private double _reachHs = double.NaN, _reachMasker = double.NaN, _shoreReach, _riverReach, _openReach;

    private double _seaWaveHeight, _masker;

    /// <summary>The sea's significant wave height in metres (T3.md V10): the node sets it from what the surface shows. Lakes follow at 0.24 of it.</summary>
    public double SeaWaveHeight { get => _seaWaveHeight; set => _seaWaveHeight = value; }

    /// <summary>The masker M of W6 in dB (0 on a quiet day). Water that cannot rise above it is not looked for.</summary>
    public double Masker { get => _masker; set => _masker = value; }

    /// <summary>Sampler calls of the last <see cref="Update"/> and whether it ran the river query or finished a shore sweep, for the node's diagnostics and the tests.</summary>
    public int LastSamplerCalls { get; private set; }
    public double LastRiverRadius => _lastRiverRadius;
    public bool RiverQueryRanLastUpdate { get; private set; }
    public bool SweepFinishedLastUpdate { get; private set; }
    public int SweepsFinished { get; private set; }
    /// <summary>The reaches in metres the last update worked with: shores, rivers and falls, open water (0: silent at the water's edge).</summary>
    public (double Shore, double River, double Open) Reaches => (_shoreReach, _riverReach, _openReach);

    public ReadOnlySpan<WaterSoundSource> Sources => new(_sources, 0, _sourceCount);

    /// <summary>Test hook: the river hits kept after the last query (one per stem: the loudest), before the two loudest are chosen.</summary>
    internal int RiverCandidates(Span<(int Stem, double S, double Power)> output)
    {
        int n = Math.Min(output.Length, _riverCandidateCount);
        for (int i = 0; i < n; i++) output[i] = (_riverCandidates[i].Stem, _riverCandidates[i].S, _riverCandidates[i].Power);
        return n;
    }

    /// <param name="sample">The terrain sample at a ring column (whole metres): <c>RingTerrainSampler.GetTerrainSample</c>.</param>
    /// <param name="graph">The world's river graph, or null (legacy worlds: shores and open water only).</param>
    /// <param name="circumferenceMeters">The ring's circumference; around coordinates wrap at it.</param>
    /// <param name="acrossMeters">The ring's width; across coordinates are clamped to [0, width].</param>
    /// <param name="highestWaterY">The highest water surface of the world in metres (the listener's height above it, less the reach, decides that nothing can be heard); infinity when unknown.</param>
    public WaterSoundSourceFinder(Func<long, int, TerrainSample> sample, RiverGraph? graph, long circumferenceMeters, int acrossMeters, double highestWaterY = double.PositiveInfinity)
    {
        _sample = sample ?? throw new ArgumentNullException(nameof(sample));
        _graph = graph;
        _net = graph?.Network;
        _circumference = circumferenceMeters;
        _acrossMax = acrossMeters;
        _highestWaterY = highestWaterY;
        if (graph != null) _operator = new RiverCarveOperator(graph);
    }

    // ------------------------------------------------------------------ geometry helpers

    private double WrapDelta(double delta)
    {
        double c = _circumference;
        delta %= c;
        if (delta > c / 2) delta -= c; else if (delta < -c / 2) delta += c;
        return delta;
    }

    private double WrapAround(double around)
    {
        double c = _circumference;
        around %= c;
        return around < 0 ? around + c : around;
    }

    private bool Wet(in TerrainSample s) => s.WaterKind == SurfaceWaterKind.Ocean || s.WaterKind == SurfaceWaterKind.Lake || (_graph == null && s.WaterKind == SurfaceWaterKind.River);

    private TerrainSample Probe(double around, double across)
    {
        LastSamplerCalls++;
        return _sample((long)Math.Round(WrapAround(around)), Math.Clamp((int)Math.Round(across), 0, _acrossMax));
    }

    private WaterSoundKind ShoreKind(SurfaceWaterKind kind) => kind == SurfaceWaterKind.Ocean ? WaterSoundKind.SeaShore : WaterSoundKind.LakeShore;

    private double WaveHeightOf(SurfaceWaterKind kind)
        => kind == SurfaceWaterKind.Ocean ? SeaWaveHeight : kind == SurfaceWaterKind.Lake ? SeaWaveHeight * LakeWaveFactor : SeaWaveHeight * LegacyRiverWaveFactor;

    /// <summary>The distance at which a source of the given power would no longer rise above the masker (W2), by bisection on <see cref="WaterSoundField.Level"/>; 0 when it is silent at the water's edge.</summary>
    private double Reach(WaterSoundKind kind, double power, double halfSize, double limit)
    {
        if (!(power > 0d) || !(WaterSoundField.Level(kind, power, halfSize, 0d) > Masker)) return 0d;
        if (WaterSoundField.Level(kind, power, halfSize, limit) > Masker) return limit;
        double lo = 0d, hi = limit;
        for (int i = 0; i < 24; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (WaterSoundField.Level(kind, power, halfSize, mid) > Masker) lo = mid; else hi = mid;
        }
        return lo;
    }

    // ------------------------------------------------------------------ the slice

    /// <summary>Advances one slice: the centre column, the shore sweep (at most <paramref name="maxSamplerCalls"/> sampler calls), the river and drop queries when the listener has moved, then recomputes <see cref="Sources"/>.</summary>
    public void Update(in WaterSoundListener listener, int maxSamplerCalls)
    {
        LastSamplerCalls = 0;
        RiverQueryRanLastUpdate = false;
        SweepFinishedLastUpdate = false;
        _updatesSinceCenter++; _updatesSinceSweep++; _updatesSinceRiver++;
        if (_reachHs != _seaWaveHeight || _reachMasker != _masker)
        {
            _reachHs = _seaWaveHeight; _reachMasker = _masker;
            double hs = _seaWaveHeight;
            _shoreReach = Reach(WaterSoundKind.SeaShore, WaterSoundField.ShorePower(hs), WaterSoundField.ShoreHalfSize(hs), MaxShoreRadiusMeters);
            _riverReach = _operator == null ? 0d : Reach(WaterSoundKind.Drop, LoudestFallWatts, LoudestFallHalfSize, MaxRiverRadiusMeters);
            _openReach = Reach(WaterSoundKind.OpenSea, WaterSoundField.OpenWaterPower(hs), WaterSoundConstants.Default.OpenWaterHalfSize, MaxShoreRadiusMeters);
        }
        // W4: when the masker, or the listener's height above every water surface, puts everything out of reach, nothing is looked for (the kept points still follow the listener).
        double maxReach = Math.Max(Math.Max(_shoreReach, _riverReach), _openReach);
        bool idle = !(maxReach > 0d) || listener.Vertical - _highestWaterY > maxReach;
        if (!idle)
        {
            int budget = Math.Max(0, maxSamplerCalls);
            if (budget > 0 && (!_centerValid || _updatesSinceCenter >= 30 || MovedWrapped(listener, _centerAt, CenterRefreshMeters))) { ProbeCenter(listener); budget--; }
            if (_shoreReach > 0d && budget > 0) AdvanceSweep(listener, budget);
            if (_riverReach > 0d && _operator != null) { RefreshRivers(listener, _riverReach); if (!RiverQueryRanLastUpdate) StepDrops(); }
        }
        Compose(listener);
    }

    private bool MovedWrapped(in WaterSoundListener a, in WaterSoundListener b, double meters)
    {
        double da = WrapDelta(a.Around - b.Around), dz = a.Across - b.Across;
        return da * da + dz * dz >= meters * meters;
    }

    private void ProbeCenter(in WaterSoundListener listener)
    {
        TerrainSample s = Probe(listener.Around, listener.Across);
        _centerAt = listener; _centerValid = true; _updatesSinceCenter = 0;
        _centerBaseWet = Wet(s);
        _centerKind = s.WaterKind;
        _centerLevel = s.HasSurfaceWater ? s.WaterLevel : double.NaN;
        _centerOpen = s.WaterKind == SurfaceWaterKind.Ocean || s.WaterKind == SurfaceWaterKind.Lake;
        _open = default;
        if (!_centerOpen) return;
        double hs = WaveHeightOf(s.WaterKind);
        _open = new Kept
        {
            Valid = true, Kind = s.WaterKind == SurfaceWaterKind.Ocean ? WaterSoundKind.OpenSea : WaterSoundKind.OpenLake,
            Power = WaterSoundField.OpenWaterPower(hs), HalfSize = WaterSoundConstants.Default.OpenWaterHalfSize, Around = WrapAround(listener.Around), Across = listener.Across,
            Level = _centerLevel, Top = _centerLevel, WaveHeight = hs, Stem = -1,
        };
    }

    // ------------------------------------------------------------------ shores

    private void AdvanceSweep(in WaterSoundListener listener, int budget)
    {
        if (!_sweeping)
        {
            if (!_centerValid) return;
            if (_sweepEver && !MovedWrapped(listener, _lastSweepAt, SweepRestartMeters) && _updatesSinceSweep < 120) return;
            _sweeping = true; _sweepEver = true; _lastSweepAt = listener; _updatesSinceSweep = 0;
            _direction = 0; _radiusIndex = 0; _rayBisecting = false; _refining = false; _rayTotal = Directions; _bestSeaDirection = -1; _bestLakeDirection = -1;
            _sweepOriginAround = listener.Around; _sweepOriginAcross = listener.Across;
            _sweepBaseWet = _centerBaseWet;
            // Radii up to the first one at or beyond the reach (a shore at 300 m is bracketed by 256 and 512).
            _sweepRadiusLimit = FirstShoreRadiusMeters;
            while (_sweepRadiusLimit < _shoreReach && _sweepRadiusLimit < MaxShoreRadiusMeters) _sweepRadiusLimit *= 2d;
            for (int d = 0; d < Directions; d++) { _rayAngle[d] = d * (Math.PI * 2d / Directions); _rayLimit[d] = _sweepRadiusLimit; }
            _sweepSea = default; _sweepLake = default; _bestSeaDistance = double.PositiveInfinity; _bestLakeDistance = double.PositiveInfinity;
        }
        int start = LastSamplerCalls;
        while (_sweeping && LastSamplerCalls - start < budget) StepSweep();
    }

    /// <summary>One sampler call of the sweep: the next radius of the current ray, or the next bisection step after the first change between wet and dry.</summary>
    private void StepSweep()
    {
        double angle = _rayAngle[_direction];
        double ux = Math.Cos(angle), uz = Math.Sin(angle);
        if (!_rayBisecting)
        {
            double radius = FirstShoreRadiusMeters * (1 << _radiusIndex);
            double z = _sweepOriginAcross + uz * radius;
            if (radius > _rayLimit[_direction] + 1e-9 || z < 0d || z > _acrossMax) { NextDirection(); return; }
            TerrainSample s = Probe(_sweepOriginAround + ux * radius, z);
            bool wet = Wet(s);
            if (wet == _sweepBaseWet) { _radiusIndex++; return; }
            // The first change: the water's edge lies between the previous radius (the listener's own column for the first) and this one.
            _rayLo = _radiusIndex == 0 ? 0d : radius * 0.5; _rayHi = radius; _bisectLeft = BisectionSteps; _rayBisecting = true;
            if (wet) { _rayWetRadius = radius; _rayWetKind = s.WaterKind; _rayLevel = s.WaterLevel; }
            else { _rayWetRadius = _rayLo; _rayWetKind = _centerKind; _rayLevel = double.IsNaN(_centerLevel) ? 0d : _centerLevel; }
            return;
        }
        double mid = 0.5 * (_rayLo + _rayHi);
        TerrainSample m = Probe(_sweepOriginAround + ux * mid, _sweepOriginAcross + uz * mid);
        bool mWet = Wet(m);
        if (mWet) { _rayWetRadius = mid; _rayWetKind = m.WaterKind; _rayLevel = m.WaterLevel; }
        if (mWet == _sweepBaseWet) _rayLo = mid; else _rayHi = mid;
        if (--_bisectLeft > 0) return;
        FinishRay(_sweepOriginAround + ux * _rayWetRadius, _sweepOriginAcross + uz * _rayWetRadius, _rayWetRadius);
    }

    private void FinishRay(double around, double across, double radius)
    {
        WaterSoundKind kind = ShoreKind(_rayWetKind);
        double hs = WaveHeightOf(_rayWetKind);
        var kept = new Kept
        {
            Valid = true, Kind = kind, Power = WaterSoundField.ShorePower(hs), HalfSize = WaterSoundField.ShoreHalfSize(hs),
            Around = WrapAround(around), Across = Math.Clamp(across, 0d, _acrossMax), Level = _rayLevel, Top = _rayLevel, WaveHeight = hs, Stem = -1,
        };
        if (kind == WaterSoundKind.SeaShore) { if (radius < _bestSeaDistance) { _bestSeaDistance = radius; _sweepSea = kept; _bestSeaDirection = _direction; } }
        else if (radius < _bestLakeDistance) { _bestLakeDistance = radius; _sweepLake = kept; _bestLakeDirection = _direction; }
        NextDirection();
    }

    private void NextDirection()
    {
        _rayBisecting = false; _radiusIndex = 0;
        if (++_direction < _rayTotal) return;
        if (!_refining)
        {
            // Refinement: the two rays at half a spacing either side of the nearest direction of each kind (a 32-ray sweep near the nearest shore, about 26 calls each), searched only out to the shore found.
            _refining = true;
            AddRefinementRays(_bestSeaDirection, _bestSeaDistance);
            AddRefinementRays(_bestLakeDirection, _bestLakeDistance);
            if (_direction < _rayTotal) return;
        }
        _sweeping = false;
        _seaShore = _sweepSea; _lakeShore = _sweepLake;
        SweepsFinished++; SweepFinishedLastUpdate = true;
    }

    private void AddRefinementRays(int direction, double distance)
    {
        if (direction < 0 || !double.IsFinite(distance)) return;
        double limit = FirstShoreRadiusMeters;
        while (limit < distance && limit < _sweepRadiusLimit) limit *= 2d;
        double half = Math.PI / Directions;
        for (int side = -1; side <= 1; side += 2)
        {
            if (_rayTotal >= _rayAngle.Length) return;
            _rayAngle[_rayTotal] = _rayAngle[direction] + side * half;
            _rayLimit[_rayTotal] = limit;
            _rayTotal++;
        }
    }

    // ------------------------------------------------------------------ rivers, chutes and falls

    private bool RiverQueryDue(in WaterSoundListener listener, double radius)
        => !_riverEver || MovedWrapped(listener, _lastRiverAt, RiverRefreshMeters) || radius > _lastRiverRadius * 1.25 || _updatesSinceRiver >= 180;

    private void RefreshRivers(in WaterSoundListener listener, double radius)
    {
        if (!RiverQueryDue(listener, radius)) return;
        _riverEver = true; _lastRiverAt = listener; _lastRiverRadius = radius; _updatesSinceRiver = 0; RiverQueryRanLastUpdate = true;
        RiverNetwork net = _net!;
        int n = _operator!.ChannelsNear(listener.Around, listener.Across, radius, _hits);
        // Per stem the loudest hit; the in-lake part of a stem is the lake's water and gives no river source.
        _riverCandidateCount = 0;
        for (int i = 0; i < n; i++)
        {
            ref readonly RiverChannelNear hit = ref _hits[i];
            if (hit.S < InLakeLength(net, hit.Stem)) continue;
            Kept k = RiverSource(net, in hit, listener, out double level);
            if (!k.Valid || !(k.Power > 0d)) continue;
            int slot = -1;
            for (int j = 0; j < _riverCandidateCount; j++) if (_riverCandidates[j].Stem == hit.Stem) { slot = j; break; }
            if (slot < 0) { if (_riverCandidateCount >= CandidateCapacity) continue; slot = _riverCandidateCount++; _stemLevel[slot] = double.NegativeInfinity; }
            if (level > _stemLevel[slot]) { _stemLevel[slot] = level; _riverCandidates[slot] = k; }
        }
        // Drops: every stem among the hits, over the part within reach; the list is kept until the listener has moved 50 m or the stems change.
        int ranges = 0;
        for (int i = 0; i < n; i++)
        {
            ref readonly RiverChannelNear hit = ref _hits[i];
            double half = Math.Sqrt(Math.Max(0d, radius * radius - hit.T * hit.T)) * 1.3d + 30d;
            int slot = -1;
            for (int j = 0; j < ranges; j++) if (_stemSlot[j] == hit.Stem) { slot = j; break; }
            if (slot < 0) { if (ranges >= CandidateCapacity) continue; slot = ranges++; _stemSlot[slot] = hit.Stem; _rangeLo[slot] = hit.S - half; _rangeHi[slot] = hit.S + half; }
            else { _rangeLo[slot] = Math.Min(_rangeLo[slot], hit.S - half); _rangeHi[slot] = Math.Max(_rangeHi[slot], hit.S + half); }
        }
        bool stemsChanged = ranges != _dropStemCount;
        for (int j = 0; j < ranges && !stemsChanged; j++) stemsChanged = _dropStems[j] != _stemSlot[j];
        if (_dropsEver && !stemsChanged && !MovedWrapped(listener, _lastDropAt, DropRefreshMeters)) return;
        // The drop search costs about as much as the query itself, so it runs one stem per update after this one and swaps in when it is complete.
        _dropsEver = true; _lastDropAt = listener; _dropStemCount = ranges;
        for (int j = 0; j < ranges; j++) { _dropStems[j] = _stemSlot[j]; _pendLo[j] = _rangeLo[j]; _pendHi[j] = _rangeHi[j]; }
        _pendCount = ranges; _pendIndex = 0; _stagingCount = 0;
        if (ranges == 0) _dropCandidateCount = 0;
    }

    /// <summary>One stem of the pending drop search: <see cref="RiverLevel.DropsNear"/> over the part of the stem within reach, with the power of W3 for each drop.</summary>
    private void StepDrops()
    {
        if (_pendIndex >= _pendCount) return;
        RiverNetwork net = _net!;
        int r = _pendIndex++;
        int stem = _dropStems[r];
        int count = RiverLevel.DropsNear(net, stem, Math.Max(InLakeLength(net, stem), _pendLo[r]), _pendHi[r], _dropBuffer);
        for (int d = 0; d < count; d++)
        {
            RiverDrop drop = _dropBuffer[d];
            net.ChannelAt(stem, drop.S, out RiverChannel channel);
            double q = DischargeAt(net, stem, drop.S, in channel, out _, out _);
            double power = WaterSoundField.DropPower(q, drop.Height);
            if (!(power > 0d)) continue;
            net.Centre(stem, drop.S, out double around, out double across, out _, out _);
            double bottom = RiverLevel.AtStem(net, stem, drop.S);
            var kept = new Kept
            {
                Valid = true, Kind = WaterSoundKind.Drop, Power = power, HalfSize = channel.HalfWidth, Around = WrapAround(around), Across = across,
                Level = bottom, Top = bottom + drop.Height, Discharge = q, Drop = drop.Height, Stem = stem,
            };
            if (_stagingCount < DropCapacity) { _dropStaging[_stagingCount++] = kept; continue; }
            // Full: the weakest candidate gives way (the three loudest at the listener are chosen from what is kept).
            int weakest = 0;
            for (int j = 1; j < _stagingCount; j++) if (_dropStaging[j].Power < _dropStaging[weakest].Power) weakest = j;
            if (power > _dropStaging[weakest].Power) _dropStaging[weakest] = kept;
        }
        if (_pendIndex < _pendCount) return;
        (_dropCandidates, _dropStaging) = (_dropStaging, _dropCandidates);
        _dropCandidateCount = _stagingCount;
    }

    private static double InLakeLength(RiverNetwork net, int stem) => stem < net.InLakeEnd.Length ? net.InLakeEnd[stem] : 0d;

    /// <summary>The discharge, speed and whiteness at s of a stem: the span's values, tapered at a source's head as T2-rules RS4 gives them (Q by x^(5/3), U by x^(4/9); the depth and the width come tapered from ChannelAt).</summary>
    internal static double DischargeAt(RiverNetwork net, int stem, double s, in RiverChannel channel, out double speed, out double whiteness)
    {
        speed = 0d; whiteness = 0d;
        RiverSpanTable? table = net.SpanTable;
        if (table == null || channel.Span < 0) return 0d;
        int i = channel.Span;
        double q = table.Spans[i].Q;
        speed = table.Speed[i]; whiteness = table.Whiteness[i];
        double taper = net.HeadTaperOverride >= 0d ? net.HeadTaperOverride : RiverHydraulics.HeadTaperMeters;
        if (taper > 0d && s < taper && net.IsSource(stem))
        {
            double t = Math.Min(taper, net.Curves[stem].Length);
            if (s < t)
            {
                double x = Math.Max(0d, s) / t;
                q *= x <= 0d ? 0d : Math.Pow(x, 5d / 3d);
                speed *= x <= 0d ? 0d : Math.Pow(x, 4d / 9d);
            }
        }
        return q;
    }

    /// <summary>The river power of W3 at a stem point (span data, tapered at a head): the same number the finder uses for a hit.</summary>
    internal static double RiverPowerAt(RiverNetwork net, int stem, double s)
    {
        net.ChannelAt(stem, s, out RiverChannel channel);
        double q = DischargeAt(net, stem, s, in channel, out double speed, out double whiteness);
        RiverSpanTable? table = net.SpanTable;
        if (table == null || channel.Span < 0) return 0d;
        return WaterSoundField.RiverPower(q, table.Sr[channel.Span], speed, channel.Depth, whiteness);
    }

    private Kept RiverSource(RiverNetwork net, in RiverChannelNear hit, in WaterSoundListener listener, out double level)
    {
        level = double.NegativeInfinity;
        net.ChannelAt(hit.Stem, hit.S, out RiverChannel channel);
        double q = DischargeAt(net, hit.Stem, hit.S, in channel, out double speed, out double whiteness);
        RiverSpanTable? table = net.SpanTable;
        if (table == null || channel.Span < 0) return default;
        double power = WaterSoundField.RiverPower(q, table.Sr[channel.Span], speed, channel.Depth, whiteness);
        net.Centre(hit.Stem, hit.S, out double around, out double across, out _, out _);
        var kept = new Kept
        {
            Valid = true, Kind = WaterSoundKind.River, Power = power, HalfSize = channel.HalfWidth, Around = WrapAround(around), Across = across,
            Level = hit.Level, Top = hit.Level, Discharge = q, Stem = hit.Stem, S = hit.S,
        };
        if (power > 0d) level = WaterSoundField.Level(WaterSoundKind.River, power, channel.HalfWidth, Distance(in kept, in listener, out _, out _, out _));
        return kept;
    }

    /// <summary>W1: the straight distance from the listener to the kept source's nearest water, and that nearest point. A river or a drop keeps its centre point and half width, a shore its edge point, open water the column under the listener.</summary>
    private double Distance(in Kept k, in WaterSoundListener listener, out double nearAround, out double nearVertical, out double nearAcross)
    {
        double da = WrapDelta(k.Around - listener.Around), dz = k.Across - listener.Across;
        double dist = Math.Sqrt(da * da + dz * dz);
        bool open = k.Kind == WaterSoundKind.OpenSea || k.Kind == WaterSoundKind.OpenLake;
        bool shore = k.Kind == WaterSoundKind.SeaShore || k.Kind == WaterSoundKind.LakeShore;
        double reach = open ? dist : shore ? 0d : k.HalfSize;
        double h = Math.Max(0d, dist - reach);
        double along = dist > 1e-9 ? Math.Min(reach, dist) / dist : 0d;
        nearAround = WrapAround(k.Around - da * along); nearAcross = k.Across - dz * along;
        double v;
        if (listener.Vertical > k.Top) { v = listener.Vertical - k.Top; nearVertical = k.Top; }
        else if (listener.Vertical < k.Level) { v = open ? 0d : k.Level - listener.Vertical; nearVertical = k.Level; }
        else { v = 0d; nearVertical = listener.Vertical; }
        return WaterSoundField.Distance(h, v);
    }

    // ------------------------------------------------------------------ the list

    private void Compose(in WaterSoundListener listener)
    {
        _sourceCount = 0;
        Add(in _seaShore, in listener);
        Add(in _lakeShore, in listener);
        Add(in _open, in listener);
        AddLoudest(_riverCandidates, _riverCandidateCount, 2, in listener);
        AddLoudest(_dropCandidates, _dropCandidateCount, 3, in listener);
    }

    private void Add(in Kept k, in WaterSoundListener listener)
    {
        if (!k.Valid || _sourceCount >= MaxSources) return;
        double x = Distance(in k, in listener, out double a, out double v, out double z);
        _sources[_sourceCount++] = new WaterSoundSource(k.Kind, k.Power, k.HalfSize, a, v, z, x, k.WaveHeight, k.Discharge, k.Drop, 0d, k.Stem);
    }

    private void AddLoudest(Kept[] list, int count, int take, in WaterSoundListener listener)
    {
        Span<double> levels = stackalloc double[take];
        Span<int> picks = stackalloc int[take];
        for (int t = 0; t < take; t++) { levels[t] = double.NegativeInfinity; picks[t] = -1; }
        for (int i = 0; i < count; i++)
        {
            double x = Distance(in list[i], in listener, out _, out _, out _);
            double level = WaterSoundField.Level(list[i].Kind, list[i].Power, list[i].HalfSize, x);
            for (int t = 0; t < take; t++)
            {
                if (picks[t] >= 0 && !(level > levels[t])) continue;
                for (int u = take - 1; u > t; u--) { levels[u] = levels[u - 1]; picks[u] = picks[u - 1]; }
                levels[t] = level; picks[t] = i;
                break;
            }
        }
        for (int t = 0; t < take; t++) if (picks[t] >= 0) Add(in list[picks[t]], in listener);
    }
}
