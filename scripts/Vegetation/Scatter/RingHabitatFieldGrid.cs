using System;
using System.Collections.Generic;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Vegetation.Scatter;

/// <summary>Where the grid gets heights and climate. The sampler-backed source is the production one; tests substitute a synthetic surface.</summary>
internal interface IHabitatSource
{
    /// <summary>One model evaluation at integer canonical metres. <paramref name="withClimate"/> asks for the full sample (climate fields); otherwise a height-only sample (height, water, site mask).</summary>
    void Evaluate(long around, int across, bool withClimate, out TerrainSample sample, out float height);
    RingTerrainSampler.BiomeTextureWeights Weights(in TerrainSample sample);
    bool IsWater(in TerrainSample sample, float height);
}

/// <summary>
/// T5 design A1-A4: the habitat field grid. Fields live on an 8 m fine lattice with the 32 m T4 C64 lattice under it; every node value is a pure function of the node and the world
/// (sampler recipe and seed, edit snapshot), so any tile, layer or worker gets the same bits. Around wraps exactly (both lattices divide the circumference); across clamps to the edge node.
/// Between nodes, <see cref="Sample"/> reads bilinearly from the fine nodes of a fine-flagged coarse cell and from the coarse nodes otherwise.
/// One instance per job or worker: it holds its own node memos and is not thread safe. No static mutable state, no Godot nodes or resources (it runs on workers).
/// Gradients at the two outermost across nodes divide by the actual one-sided distance. Production call sites gate on <see cref="IsEnabled"/>.
/// </summary>
public sealed class RingHabitatFieldGrid
{
    public const int ScatterRocksBit = 4;
    public const double FineM = 8.0, CoarseM = 32.0;
    /// <summary>A3 lever 4: a coarse cell is fine-flagged when the largest coarse slope of its four corners reaches this many degrees (or a corner has water).</summary>
    public const double HabitatFineSlopeDeg = 12.0;
    /// <summary>T6 A7: the vegetation.forest_structure bit; it also enables the grid and the scatter streamer (its understory rows).</summary>
    public const int ScatterVegetationBit = 6;
    /// <summary>Scatter exists (streamer and grid) when the mask has the rocks bit or the forest structure bit.</summary>
    public static bool IsEnabled(int worldFeatureMask) => (worldFeatureMask & ((1 << ScatterRocksBit) | (1 << ScatterVegetationBit))) != 0;

    private const int FinePerCoarse = 4;

    private struct Node
    {
        public float H;
        public double Gx, Gz;
        public float Slope, C8, C64, Moist, Temp, Forest, Grass, Canopy, Clearing, Maple, Fir, Pine;
        public ForestHabitat Habitat;
        public RingHabitatFlags Flags;
    }

    private readonly record struct HeightEntry(float Height, RingHabitatFlags Flags);

    private readonly IHabitatSource _source;
    private readonly ForestRegionSampler _forest;
    private readonly bool _forceAllFine;
    private readonly long _circ, _fineCount, _coarseCount;
    private readonly int _lastF, _lastC;
    private readonly Dictionary<long, HeightEntry> _heights = new();
    private readonly Dictionary<long, Node> _coarse = new();
    private readonly Dictionary<long, Node> _fine = new();

    /// <summary>Test seam: the threshold actually used by <see cref="IsCellFine"/> (check f lowers it).</summary>
    internal double FineSlopeThresholdDeg { get; set; } = HabitatFineSlopeDeg;

    public long FineEvaluations { get; private set; }
    public long CoarseEvaluations { get; private set; }
    public long ClimateSamples { get; private set; }

    public RingHabitatFieldGrid(RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits, int seed, long circumferenceMeters, int widthMeters, bool forceAllFine = false)
        : this(new SamplerSource(sampler, edits), seed, circumferenceMeters, widthMeters, forceAllFine) { }

    internal RingHabitatFieldGrid(IHabitatSource source, int seed, long circumferenceMeters, int widthMeters, bool forceAllFine = false)
    {
        if (circumferenceMeters <= 0 || circumferenceMeters % 32 != 0)
            throw new ArgumentException("The circumference must be a positive multiple of 32 m.", nameof(circumferenceMeters));
        if (widthMeters < 64) throw new ArgumentOutOfRangeException(nameof(widthMeters));
        _source = source;
        _forceAllFine = forceAllFine;
        _circ = circumferenceMeters;
        _fineCount = circumferenceMeters / 8;
        _coarseCount = circumferenceMeters / 32;
        _lastF = (widthMeters - 1) / 8;
        _lastC = (widthMeters - 1) / 32;
        _forest = new ForestRegionSampler(seed, circumferenceMeters, widthMeters);
    }

    // ------------------------------------------------------------------ public nodes

    public RingHabitatSample CoarseNode(long I, int J)
    {
        bool rim = J < 0 || J > _lastC;
        return Make(GetCoarse(I, J), 64.0, rim ? RingHabitatFlags.RimClamped : RingHabitatFlags.None);
    }

    public RingHabitatSample FineNode(long i, int j)
    {
        bool rim = j < 0 || j > _lastF;
        return Make(GetFine(i, j), 16.0, RingHabitatFlags.Fine | (rim ? RingHabitatFlags.RimClamped : RingHabitatFlags.None));
    }

    /// <summary>The coarse cell (I..I+1, J..J+1) reads fine nodes: a pure function of its four coarse corners (A3 lever 4).</summary>
    public bool IsCellFine(long I, int J)
    {
        if (_forceAllFine) return true;
        int j1 = Math.Min(Math.Clamp(J, 0, _lastC) + 1, _lastC);
        Node a = GetCoarse(I, J), b = GetCoarse(I + 1, J), c = GetCoarse(I, j1), d = GetCoarse(I + 1, j1);
        if (((a.Flags | b.Flags | c.Flags | d.Flags) & RingHabitatFlags.Water) != 0) return true;
        return Math.Max(Math.Max(a.Slope, b.Slope), Math.Max(c.Slope, d.Slope)) >= FineSlopeThresholdDeg;
    }

    // ------------------------------------------------------------------ between nodes

    public RingHabitatSample Sample(double around, double across) => SampleCore(around, across, out _, out _);

    /// <summary>The bilinear gradient (rise per metre, around and across) at a point, the one <see cref="Sample"/> takes its slope from.</summary>
    public (double Gx, double Gz) Gradient(double around, double across)
    {
        SampleCore(around, across, out double gx, out double gz);
        return (gx, gz);
    }

    private RingHabitatSample SampleCore(double around, double across, out double gx, out double gz)
    {
        double w = around % _circ;
        if (w < 0.0) w += _circ;
        if (w >= _circ) w = 0.0;
        double acrossMax = _lastC * CoarseM;
        bool rim = !(across >= 0.0 && across <= acrossMax);
        double a = Math.Clamp(across, 0.0, acrossMax);

        long cellI = (long)Math.Floor(w / CoarseM);
        int cellJ = (int)Math.Floor(a / CoarseM);
        bool fine = IsCellFine(cellI, cellJ);
        double step = fine ? FineM : CoarseM;
        double ua = w / step, uc = a / step;
        long i0 = (long)Math.Floor(ua);
        int j0 = (int)Math.Floor(uc);
        double fa = ua - i0, fc = uc - j0;
        Node n00 = Get(fine, i0, j0);
        Node n10 = fa > 0.0 ? Get(fine, i0 + 1, j0) : default;
        Node n01 = fc > 0.0 ? Get(fine, i0, j0 + 1) : default;
        Node n11 = fa > 0.0 && fc > 0.0 ? Get(fine, i0 + 1, j0 + 1) : default;
        Node blend = Blend(in n00, in n10, in n01, in n11, fa, fc);
        // Habitat is the nearest coarse node by position, at either level.
        double ca = w / CoarseM - cellI, cc = a / CoarseM - cellJ;
        blend.Habitat = GetCoarse(cellI + (ca >= 0.5 ? 1 : 0), cellJ + (cc >= 0.5 ? 1 : 0)).Habitat;
        gx = blend.Gx;
        gz = blend.Gz;
        RingHabitatFlags flags = blend.Flags | (fine ? RingHabitatFlags.Fine : RingHabitatFlags.None) | (rim ? RingHabitatFlags.RimClamped : RingHabitatFlags.None);
        return Make(blend, fine ? 16.0 : 64.0, flags);
    }

    private Node Get(bool fine, long index, int j) => fine ? GetFine(index, j) : GetCoarse(index, j);

    // ------------------------------------------------------------------ window

    /// <summary>
    /// Eagerly evaluates the coarse nodes over the window (with their taps) and the fine nodes of every fine-flagged cell inside it. The caller passes the tile plus 16 m (A3 halo);
    /// anything outside is evaluated lazily by <see cref="Sample"/> through the same memos.
    /// </summary>
    public void PrepareWindow(double aroundMin, double aroundMax, double acrossMin, double acrossMax)
    {
        using var scope = RingPerformanceTelemetry.MeasureCpu("Habitat grid", worker: true);
        long i0 = (long)Math.Floor(aroundMin / CoarseM), i1 = (long)Math.Floor(aroundMax / CoarseM) + 1;
        int j0 = Math.Clamp((int)Math.Floor(acrossMin / CoarseM), 0, _lastC), j1 = Math.Clamp((int)Math.Floor(acrossMax / CoarseM) + 1, 0, _lastC);
        for (long I = i0; I <= i1; I++)
            for (int J = j0; J <= j1; J++)
                GetCoarse(I, J);
        long fi0 = (long)Math.Floor(aroundMin / FineM), fi1 = (long)Math.Ceiling(aroundMax / FineM);
        int fj0 = Math.Clamp((int)Math.Floor(acrossMin / FineM), 0, _lastF), fj1 = Math.Clamp((int)Math.Ceiling(acrossMax / FineM), 0, _lastF);
        for (long I = i0; I < i1; I++)
            for (int J = j0; J <= Math.Max(j1 - 1, j0); J++)
            {
                if (!IsCellFine(I, J)) continue;
                long iLo = Math.Max(I * FinePerCoarse, fi0), iHi = Math.Min(I * FinePerCoarse + FinePerCoarse, fi1);
                int jLo = Math.Max(J * FinePerCoarse, fj0), jHi = Math.Min(J * FinePerCoarse + FinePerCoarse, fj1);
                for (long i = iLo; i <= iHi; i++)
                    for (int j = jLo; j <= jHi; j++)
                        GetFine(i, j);
            }
    }

    // ------------------------------------------------------------------ node evaluation

    private static long Key(long i, int j) => (i << 20) | (uint)j;
    private long WrapFine(long i) { long r = i % _fineCount; return r < 0 ? r + _fineCount : r; }
    private long WrapCoarse(long I) { long r = I % _coarseCount; return r < 0 ? r + _coarseCount : r; }

    private HeightEntry HeightAt(long i8, int j8, bool coarseCaller)
    {
        i8 = WrapFine(i8);
        j8 = Math.Clamp(j8, 0, _lastF);
        long key = Key(i8, j8);
        if (_heights.TryGetValue(key, out HeightEntry entry)) return entry;
        _source.Evaluate(i8 * 8, j8 * 8, false, out TerrainSample s, out float h);
        entry = new HeightEntry(h, FlagsOf(in s, h));
        _heights[key] = entry;
        if (coarseCaller) CoarseEvaluations++; else FineEvaluations++;
        return entry;
    }

    private float CoarseHeight(long I, int J) => HeightAt(WrapCoarse(I) * FinePerCoarse, Math.Clamp(J, 0, _lastC) * FinePerCoarse, true).Height;

    private RingHabitatFlags FlagsOf(in TerrainSample s, float h)
        => (_source.IsWater(in s, h) ? RingHabitatFlags.Water : RingHabitatFlags.None)
         | (s.SiteMask.BlocksVegetation ? RingHabitatFlags.SiteBlocked : RingHabitatFlags.None);

    private static float SlopeDegrees(double gx, double gz) => (float)(Math.Atan(Math.Sqrt(gx * gx + gz * gz)) * 180.0 / Math.PI);

    private Node GetCoarse(long I, int J)
    {
        I = WrapCoarse(I);
        J = Math.Clamp(J, 0, _lastC);
        long key = Key(I, J);
        if (_coarse.TryGetValue(key, out Node node)) return node;
        node = BuildCoarse(I, J);
        _coarse[key] = node;
        return node;
    }

    private Node BuildCoarse(long I, int J)
    {
        _source.Evaluate(I * 32, J * 32, true, out TerrainSample s, out float h);
        ClimateSamples++;
        long heightKey = Key(I * FinePerCoarse, J * FinePerCoarse);
        if (!_heights.ContainsKey(heightKey))
        {
            _heights[heightKey] = new HeightEntry(h, FlagsOf(in s, h));
            CoarseEvaluations++;
        }
        int jn = Math.Min(J + 1, _lastC), js = Math.Max(J - 1, 0);
        double gx = ((double)CoarseHeight(I + 1, J) - CoarseHeight(I - 1, J)) / (2.0 * CoarseM);
        double gz = ((double)CoarseHeight(I, jn) - CoarseHeight(I, js)) / ((jn - js) * CoarseM);
        float slope = SlopeDegrees(gx, gz);
        // C64 exactly as BuildSectorRelief: coarse heights, taps two coarse nodes away, around wraps, across clamps.
        float c64 = (float)((double)h - 0.25 * ((double)CoarseHeight(I + 2, J) + CoarseHeight(I - 2, J) + CoarseHeight(I, J + 2) + CoarseHeight(I, J - 2)));
        RingTerrainSampler.BiomeTextureWeights weights = _source.Weights(in s);
        ForestRegionSample forest = _forest.Sample(I * 32, J * 32, in s, slope);
        return new Node
        {
            H = h, Gx = gx, Gz = gz, Slope = slope, C8 = 0f, C64 = c64,
            Moist = s.Moisture, Temp = s.Temperature, Forest = weights.Forest, Grass = 1f - (weights.Desert + weights.Forest + weights.Tundra + weights.Alpine),
            Canopy = forest.Density, Habitat = forest.Habitat, Flags = FlagsOf(in s, h),
            Clearing = forest.Clearing, Maple = forest.MapleWeight, Fir = forest.FirWeight, Pine = forest.PineWeight
        };
    }

    private Node GetFine(long i, int j)
    {
        i = WrapFine(i);
        j = Math.Clamp(j, 0, _lastF);
        long key = Key(i, j);
        if (_fine.TryGetValue(key, out Node node)) return node;
        node = BuildFine(i, j);
        _fine[key] = node;
        return node;
    }

    private Node BuildFine(long i, int j)
    {
        HeightEntry centre = HeightAt(i, j, false);
        int jn = Math.Min(j + 1, _lastF), js = Math.Max(j - 1, 0);
        float hE = HeightAt(i + 1, j, false).Height, hW = HeightAt(i - 1, j, false).Height, hN = HeightAt(i, jn, false).Height, hS = HeightAt(i, js, false).Height;
        double gx = ((double)hE - hW) / (2.0 * FineM);
        double gz = ((double)hN - hS) / ((jn - js) * FineM);
        float c8 = (float)((double)centre.Height - 0.25 * ((double)hE + hW + hN + hS));

        // Coarse-only fields: the bilinear value of the node's coarse cell (a node on the coarse lattice takes the coarse node exactly).
        long I = i / FinePerCoarse;
        int r = (int)(i - I * FinePerCoarse), J = j / FinePerCoarse, rj = j % FinePerCoarse;
        double fa = r / (double)FinePerCoarse, fc = rj / (double)FinePerCoarse;
        Node n00 = GetCoarse(I, J);
        Node n10 = r > 0 ? GetCoarse(I + 1, J) : default;
        Node n01 = rj > 0 ? GetCoarse(I, J + 1) : default;
        Node n11 = r > 0 && rj > 0 ? GetCoarse(I + 1, J + 1) : default;
        Node blend = Blend(in n00, in n10, in n01, in n11, fa, fc);
        return new Node
        {
            H = centre.Height, Gx = gx, Gz = gz, Slope = SlopeDegrees(gx, gz), C8 = c8, C64 = blend.C64,
            Moist = blend.Moist, Temp = blend.Temp, Forest = blend.Forest, Grass = blend.Grass, Canopy = blend.Canopy,
            Habitat = GetCoarse(I + (r >= 2 ? 1 : 0), J + (rj >= 2 ? 1 : 0)).Habitat, Flags = centre.Flags,
            Clearing = blend.Clearing, Maple = blend.Maple, Fir = blend.Fir, Pine = blend.Pine
        };
    }

    private static Node Blend(in Node n00, in Node n10, in Node n01, in Node n11, double fa, double fc)
    {
        double B(double a, double b, double c, double d) => (a * (1.0 - fa) + b * fa) * (1.0 - fc) + (c * (1.0 - fa) + d * fa) * fc;
        return new Node
        {
            H = (float)B(n00.H, n10.H, n01.H, n11.H),
            Gx = B(n00.Gx, n10.Gx, n01.Gx, n11.Gx),
            Gz = B(n00.Gz, n10.Gz, n01.Gz, n11.Gz),
            C8 = (float)B(n00.C8, n10.C8, n01.C8, n11.C8),
            C64 = (float)B(n00.C64, n10.C64, n01.C64, n11.C64),
            Moist = (float)B(n00.Moist, n10.Moist, n01.Moist, n11.Moist),
            Temp = (float)B(n00.Temp, n10.Temp, n01.Temp, n11.Temp),
            Forest = (float)B(n00.Forest, n10.Forest, n01.Forest, n11.Forest),
            Grass = (float)B(n00.Grass, n10.Grass, n01.Grass, n11.Grass),
            Canopy = (float)B(n00.Canopy, n10.Canopy, n01.Canopy, n11.Canopy),
            Clearing = (float)B(n00.Clearing, n10.Clearing, n01.Clearing, n11.Clearing),
            Maple = (float)B(n00.Maple, n10.Maple, n01.Maple, n11.Maple),
            Fir = (float)B(n00.Fir, n10.Fir, n01.Fir, n11.Fir),
            Pine = (float)B(n00.Pine, n10.Pine, n01.Pine, n11.Pine),
            Flags = n00.Flags | n10.Flags | n01.Flags | n11.Flags
        };
    }

    // ------------------------------------------------------------------ point fields (A2)

    private static double Saturate(double v) => Math.Clamp(v, 0.0, 1.0);

    private static RingHabitatSample Make(in Node n, double baselineM, RingHabitatFlags flags)
    {
        float slope = SlopeDegrees(n.Gx, n.Gz);
        double relief64 = RingGroundSurfaceModel.Relief(n.C64);
        double relief8 = n.C8 / (Math.Abs((double)n.C8) + 0.25);
        double wetness = Saturate(0.55 * RingGroundSurfaceModel.SmoothStep(0.25, 0.85, n.Moist) + 0.45 * Saturate(-(0.7 * relief64 + 0.3 * relief8)))
            * (1.0 - 0.6 * RingGroundSurfaceModel.SmoothStep(20.0, 40.0, slope));
        double rock = RingGroundSurfaceModel.RockExposure(slope, baselineM, relief64, n.Forest);
        double scree = RingGroundSurfaceModel.ScreeWeight(slope, baselineM, relief64, (double)n.Grass + n.Forest);
        return new RingHabitatSample(n.H, slope, (float)baselineM, n.C8, n.C64, n.Moist, n.Temp, n.Forest, n.Grass, n.Canopy, n.Habitat,
            (float)wetness, 0f, float.PositiveInfinity, (float)rock, (float)scree, (float)(1.0 - rock), flags | n.Flags,
            n.Clearing, n.Maple, n.Fir, n.Pine);
    }

    // ------------------------------------------------------------------ production source

    private sealed class SamplerSource : IHabitatSource
    {
        private readonly RingTerrainSampler _sampler;
        private readonly RingLodSurfaceEditSnapshot _edits;
        public SamplerSource(RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits) { _sampler = sampler; _edits = edits; }

        public void Evaluate(long around, int across, bool withClimate, out TerrainSample sample, out float height)
        {
            if (withClimate) _edits.GetPresentationTerrainSampleAndHeight(_sampler, around, across, out sample, out height);
            else _edits.GetPresentationHeightOnlyTerrainSampleAndHeight(_sampler, around, across, out sample, out height);
        }

        public RingTerrainSampler.BiomeTextureWeights Weights(in TerrainSample sample) => _sampler.GetBiomeTextureWeights(in sample);
        public bool IsWater(in TerrainSample sample, float height) => RingBiomeDecorationLayer.IsSurfaceWater(_edits, _sampler, sample, height);
    }
}
