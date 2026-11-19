using System;
using System.Collections.Generic;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Vegetation;

/// <summary>What a grass candidate reads from the coarse habitat fields (T6 design B4): the values the response formulas use and the A5 community memberships.</summary>
internal readonly struct RingCoverFields
{
    public readonly float Canopy, Wetness, Relief64, Moisture;
    public readonly RingCommunityMemberships Memberships;

    public RingCoverFields(float canopy, float wetness, float relief64, float moisture, in RingCommunityMemberships memberships)
    { Canopy = canopy; Wetness = wetness; Relief64 = relief64; Moisture = moisture; Memberships = memberships; }

    /// <summary>B4 density: the grass chance multiplier, 1 - 0.75 ss(0.35, 0.80, Canopy).</summary>
    public float DensityFactor => DensityFactorOf(Canopy);

    public static float DensityFactorOf(float canopy) => canopy <= .35f ? 1f : 1f - 0.75f * SmoothStep(.35f, .80f, canopy);

    /// <summary>B4 height: taller in wet hollows, shorter on dry crests. The bed / bar term is reserved (1) until T5-11 and T6-12.</summary>
    public float HeightFactor => HeightFactorOf(Wetness, Relief64, Moisture);

    public static float HeightFactorOf(float wetness, float relief64, float moisture)
        => (1f + .35f * SmoothStep(.45f, .85f, wetness)) * (1f - .35f * SmoothStep(.15f, .60f, relief64) * (1f - SmoothStep(.35f, .65f, moisture)));

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float x = float.IsFinite(value) ? Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f) : 0f;
        return x * x * (3f - 2f * x);
    }
}

/// <summary>
/// T6 design B4: the coarse habitat fields of one grass tile. One instance per build (it holds the grid's memos and is not thread safe). The 32 m coarse nodes come from the build's own
/// <see cref="RingHabitatFieldGrid"/> (Canopy, Moisture, Temperature, WetnessTotal, Relief64, RockExposure, Scree, SlopeDeg) plus one <see cref="ForestRegionSampler"/> sample per coarse node, taken
/// with its own full terrain sample, for the clearing field and the regional species pockets. The derived fields (memberships through <see cref="RingPlantCommunities"/>) are computed at the nodes
/// of a canonical 4 m patch lattice over the tile (9 x 9 for a 32 m tile) from the bilinear blend of the four coarse nodes, and read bilinearly per candidate. Pure per position, so adjacent tiles agree
/// on shared nodes, and no fine nodes are needed (grass responds at 32-64 m scales). Needs a circumference that is a multiple of 32 m and a width of at least 64 m, as the grid does.
/// </summary>
internal sealed class RingCoarseHabitatPatch
{
    public const double PatchM = 4.0;
    private const double CoarseM = RingHabitatFieldGrid.CoarseM;

    private readonly struct CoarseValues
    {
        public readonly float Canopy, Moisture, Temperature, Wetness, Relief64, Rock, Scree, Slope, Clearing, Maple, Fir, Pine;
        public CoarseValues(in RingHabitatSample h, in ForestRegionSample forest)
        {
            Canopy = h.Canopy; Moisture = h.Moisture; Temperature = h.Temperature; Wetness = h.WetnessTotal; Relief64 = h.Relief64; Rock = h.RockExposure; Scree = h.Scree; Slope = h.SlopeDeg;
            Clearing = forest.Clearing; Maple = forest.MapleWeight; Fir = forest.FirWeight; Pine = forest.PineWeight;
        }
    }

    private readonly RingTerrainSampler _sampler;
    private readonly RingLodSurfaceEditSnapshot _edits;
    private readonly RingHabitatFieldGrid _grid;
    private readonly ForestRegionSampler _forest;
    private readonly long _circumference, _coarseCount;
    private readonly int _lastCoarse;
    private readonly Dictionary<long, CoarseValues> _nodes = new();
    private readonly double _originAround, _originAcross;
    private readonly int _countAround, _countAcross;
    private readonly float[] _canopy, _wetness, _relief, _moisture, _members; // per patch node (the members array holds the eight memberships of a node in a row)

    /// <summary>A point's place in the patch: the four surrounding nodes and their bilinear weights. Fields are read from it lazily, so a candidate that is rejected early pays for the canopy only.</summary>
    public readonly struct Cell
    {
        public readonly int I00, I10, I01, I11;
        public readonly float W00, W10, W01, W11;
        public Cell(int i00, int i10, int i01, int i11, float fu, float fv)
        {
            I00 = i00; I10 = i10; I01 = i01; I11 = i11;
            W00 = (1f - fu) * (1f - fv); W10 = fu * (1f - fv); W01 = (1f - fu) * fv; W11 = fu * fv;
        }
    }

    /// <summary>Evaluates the patch lattice over the bounds; null when the world's size does not fit the habitat grid.</summary>
    public static RingCoarseHabitatPatch? TryCreate(RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits, double aroundMin, double aroundMax, double acrossMin, double acrossMax)
    {
        long circumference = Math.Max(1L, sampler.AroundRingVoxelLength);
        int width = Math.Max(1, sampler.RingAcrossWidthMeters);
        if (circumference % 32 != 0 || width < 64) return null;
        return new RingCoarseHabitatPatch(sampler, edits, circumference, width, aroundMin, aroundMax, acrossMin, acrossMax);
    }

    private RingCoarseHabitatPatch(RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits, long circumference, int width, double aroundMin, double aroundMax, double acrossMin, double acrossMax)
    {
        _sampler = sampler;
        _edits = edits;
        _circumference = circumference;
        _coarseCount = circumference / 32;
        _lastCoarse = (width - 1) / 32;
        _grid = new RingHabitatFieldGrid(sampler, edits, sampler.Seed, circumference, width);
        _forest = new ForestRegionSampler(sampler.Seed, circumference, width);
        _originAround = Math.Floor(aroundMin / PatchM) * PatchM;
        _originAcross = Math.Floor(acrossMin / PatchM) * PatchM;
        _countAround = (int)Math.Ceiling((aroundMax - _originAround) / PatchM) + 1;
        _countAcross = (int)Math.Ceiling((acrossMax - _originAcross) / PatchM) + 1;
        int nodes = _countAround * _countAcross;
        _canopy = new float[nodes]; _wetness = new float[nodes]; _relief = new float[nodes]; _moisture = new float[nodes]; _members = new float[nodes * RingCommunityMemberships.Count];
        for (int j = 0; j < _countAcross; j++)
            for (int i = 0; i < _countAround; i++)
            {
                int n = j * _countAround + i;
                RingCoverFields node = EvaluateNode(_originAround + i * PatchM, _originAcross + j * PatchM);
                _canopy[n] = node.Canopy; _wetness[n] = node.Wetness; _relief[n] = node.Relief64; _moisture[n] = node.Moisture;
                for (int c = 0; c < RingCommunityMemberships.Count; c++) _members[n * RingCommunityMemberships.Count + c] = node.Memberships[c];
            }
    }

    /// <summary>The place of a point in the patch: the four surrounding nodes (clamped at the lattice edge) and their bilinear weights.</summary>
    public Cell Locate(double around, double across)
    {
        double u = (around - _originAround) * (1.0 / PatchM), v = (across - _originAcross) * (1.0 / PatchM);
        if (u < 0.0) u = 0.0; else if (u > _countAround - 1) u = _countAround - 1;
        if (v < 0.0) v = 0.0; else if (v > _countAcross - 1) v = _countAcross - 1;
        int i0 = (int)u, j0 = (int)v;
        if (i0 > _countAround - 2) i0 = Math.Max(0, _countAround - 2);
        if (j0 > _countAcross - 2) j0 = Math.Max(0, _countAcross - 2);
        int i1 = i0 + 1 < _countAround ? i0 + 1 : i0, j1 = j0 + 1 < _countAcross ? j0 + 1 : j0;
        return new Cell(j0 * _countAround + i0, j0 * _countAround + i1, j1 * _countAround + i0, j1 * _countAround + i1, (float)(u - i0), (float)(v - j0));
    }

    private static float Blend(float[] values, in Cell c) => values[c.I00] * c.W00 + values[c.I10] * c.W10 + values[c.I01] * c.W01 + values[c.I11] * c.W11;

    public float Canopy(in Cell c) => Blend(_canopy, c);

    /// <summary>The B4 height factor at the cell (wetness, 64 m relief and moisture blended first, then the formula).</summary>
    public float HeightFactor(in Cell c) => RingCoverFields.HeightFactorOf(Blend(_wetness, c), Blend(_relief, c), Blend(_moisture, c));

    private float Member(in Cell c, int k)
    {
        const int n = RingCommunityMemberships.Count;
        return _members[c.I00 * n + k] * c.W00 + _members[c.I10 * n + k] * c.W10 + _members[c.I01 * n + k] * c.W01 + _members[c.I11 * n + k] * c.W11;
    }

    public RingCommunityMemberships Memberships(in Cell c)
        => new(Member(c, 0), Member(c, 1), Member(c, 2), Member(c, 3), Member(c, 4), Member(c, 5), Member(c, 6), Member(c, 7));
    /// <summary>All the fields at a point (the same blend the lazy readers use).</summary>
    public RingCoverFields Sample(double around, double across)
    {
        Cell c = Locate(around, across);
        RingCommunityMemberships memberships = Memberships(c);
        return new RingCoverFields(Canopy(c), Blend(_wetness, c), Blend(_relief, c), Blend(_moisture, c), in memberships);
    }

    private CoarseValues Coarse(long i, int j)
    {
        i %= _coarseCount; if (i < 0) i += _coarseCount;
        j = Math.Clamp(j, 0, _lastCoarse);
        long key = (i << 20) | (uint)j;
        if (_nodes.TryGetValue(key, out CoarseValues cached)) return cached;
        RingHabitatSample h = _grid.CoarseNode(i, j);
        TerrainSample terrain = _edits.GetPresentationTerrainSample(_sampler, i * 32, j * 32);
        ForestRegionSample forest = _forest.Sample(i * 32, j * 32, in terrain, h.SlopeDeg);
        return _nodes[key] = new CoarseValues(in h, in forest);
    }

    private RingCoverFields EvaluateNode(double around, double across)
    {
        double w = around % _circumference;
        if (w < 0.0) w += _circumference;
        double a = Math.Clamp(across, 0.0, _lastCoarse * CoarseM);
        long i0 = (long)Math.Floor(w / CoarseM);
        int j0 = (int)Math.Floor(a / CoarseM);
        float fa = (float)(w / CoarseM - i0), fc = (float)(a / CoarseM - j0);
        CoarseValues n00 = Coarse(i0, j0);
        CoarseValues n10 = fa > 0f ? Coarse(i0 + 1, j0) : n00;
        CoarseValues n01 = fc > 0f ? Coarse(i0, j0 + 1) : n00;
        CoarseValues n11 = fa > 0f && fc > 0f ? Coarse(i0 + 1, j0 + 1) : n00;
        float B(float v00, float v10, float v01, float v11) => (v00 * (1f - fa) + v10 * fa) * (1f - fc) + (v01 * (1f - fa) + v11 * fa) * fc;
        float canopy = B(n00.Canopy, n10.Canopy, n01.Canopy, n11.Canopy);
        float moisture = B(n00.Moisture, n10.Moisture, n01.Moisture, n11.Moisture);
        float temperature = B(n00.Temperature, n10.Temperature, n01.Temperature, n11.Temperature);
        float wetness = B(n00.Wetness, n10.Wetness, n01.Wetness, n11.Wetness);
        float relief64 = B(n00.Relief64, n10.Relief64, n01.Relief64, n11.Relief64);
        float rock = B(n00.Rock, n10.Rock, n01.Rock, n11.Rock);
        float scree = B(n00.Scree, n10.Scree, n01.Scree, n11.Scree);
        float slope = B(n00.Slope, n10.Slope, n01.Slope, n11.Slope);
        float clearing = B(n00.Clearing, n10.Clearing, n01.Clearing, n11.Clearing);
        var regional = new ForestRegionSample(ForestHabitat.None, canopy, 0f, clearing,
            B(n00.Maple, n10.Maple, n01.Maple, n11.Maple), B(n00.Fir, n10.Fir, n01.Fir, n11.Fir), B(n00.Pine, n10.Pine, n01.Pine, n11.Pine));
        ForestStructureSampler.SpeciesWeights(moisture, temperature, wetness, relief64, rock, slope, in regional, out _, out float fir, out float pine);
        RingCommunityMemberships memberships = RingPlantCommunities.Memberships(canopy, clearing, Math.Clamp(fir + pine, 0f, 1f), wetness, relief64, rock, moisture, scree);
        return new RingCoverFields(canopy, wetness, relief64, moisture, in memberships);
    }
}
