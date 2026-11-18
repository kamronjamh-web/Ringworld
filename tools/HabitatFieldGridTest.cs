using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T5-3 (docs/work-orders/terrain-vegetation/T5.md, design A1-A4): the habitat field grid. Purity across instances, threads and window shapes; ring seam; rim clamp; analytic
/// surfaces through the internal source seam; agreement with the T4 mirror and the proxy builder's C64; the fine-flag identity (A3 lever 4); cost per 240 m tile (printed, not failed); the feature gate.
/// Prints HABITAT_FIELD_GRID=PASS. Headless: tools/run_tests.ps1 -Tests habitat_field_grid_test. --png writes the debug image (rock exposure red, wetness blue).
/// </summary>
public partial class HabitatFieldGridTest : RefCounted
{
    private const long Circ = LandscapeWorld.Circumference;
    private const int Width = LandscapeWorld.Width;
    private const string PngPath = "res://.godot/audit_baseline/common/t5/t5-3_habitat_steep_forest_edge.png";
    private const string DressingFixture = "res://tools/fixtures/landscape_poses_dressing_s1337.json";
    private static readonly string[] DressingNames = { "steep_eye", "scree_eye", "cliff_eye", "breaks_300m", "mountain_2km", "lowland_eye" };
    private readonly List<string> _failures = new();
    private void Check(bool ok, string message) { if (!ok && _failures.Count < 30) _failures.Add(message); }
    private static void Detail(string text) => Console.WriteLine("HABITAT_FIELD_GRID_DETAIL " + text);

    private readonly record struct Tile(string Name, long TileA, long TileZ, double A0, double A1, double Z0, double Z1);

    private LandscapeWorld.Spec _spec = null!;
    private RingTerrainSampler _root = null!;
    private readonly List<Tile> _tiles = new();

    public int Run()
    {
        try
        {
            Setup();
            Purity();
            Seam();
            Rim();
            Analytic();
            Agreement();
            FineFlagIdentity();
            Cost();
            Gate();
            if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--png") >= 0) WritePng();
        }
        catch (Exception e) { Console.Error.WriteLine("HABITAT_FIELD_GRID=FAIL exception " + e); return 1; }
        finally { _root?.Dispose(); }
        if (_failures.Count != 0) { Console.Error.WriteLine("HABITAT_FIELD_GRID=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("HABITAT_FIELD_GRID=PASS");
        return 0;
    }

    // ------------------------------------------------------------------ fixtures

    private bool _groundSurfaceWorld;

    private void Setup()
    {
        try { _spec = LandscapeWorld.For(1, new[] { "ground.surface" }); _groundSurfaceWorld = true; }
        catch (Exception e) { Detail("LandscapeWorld.For(1, ground.surface) failed (" + e.Message + "); using LandscapeWorld.Default"); _spec = LandscapeWorld.Default; }
        Detail("world: " + (_groundSurfaceWorld ? "LandscapeWorld.For(1, [ground.surface])" : "LandscapeWorld.Default") + " tag=" + _spec.Tag);
        _root = _spec.CreateSampler();
        double aroundCell = Circ / Math.Ceiling(Circ / 30.0);
        var t0 = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath(LandscapePoseFinder.FixturePath));
        var dressing = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath(DressingFixture));
        void Add(LandscapePose p)
        {
            long ta = (long)Math.Floor(Math.Floor(p.Around / aroundCell) / 8.0), tz = (long)Math.Floor(p.Across / 240.0);
            _tiles.Add(new Tile(p.Name, ta, tz, ta * 8 * aroundCell, (ta + 1) * 8 * aroundCell, tz * 240.0, (tz + 1) * 240.0));
        }
        foreach (string name in WorldGenerationFingerprint.TilePoses) Add(t0.Poses.First(p => p.Name == name));
        foreach (string name in DressingNames) Add(dressing.Poses.First(p => p.Name == name));
        Check(_tiles.Count == 14, "expected 14 test tiles, got " + _tiles.Count);
    }

    private static RingLodSurfaceEditSnapshot EmptyEdits(RingTerrainSampler sampler) => new(0,
        sampler.AroundRingVoxelLength, new Dictionary<RingTerrainColumnCoordinate, int>(),
        new Dictionary<RingTerrainColumnCoordinate, float>(), sampler.UsesRuntimeSurfaceGeneration);

    private RingHabitatFieldGrid NewGrid(RingTerrainSampler sampler, bool forceAllFine = false)
        => new(sampler, EmptyEdits(sampler), LandscapeWorld.Seed, Circ, Width, forceAllFine);

    private RingTerrainSampler NewCopy(bool lattice = false)
    {
        RingTerrainSampler copy = _root.CreateIndependentCopy();
        if (lattice) copy.EnablePresentationLatticeCache();
        return copy;
    }

    private static void Mix(ref ulong h, float v) { h ^= (uint)BitConverter.SingleToInt32Bits(v); h *= 1099511628211UL; }

    private static ulong Bits(in RingHabitatSample s, bool flags = true)
    {
        ulong h = 14695981039346656037UL;
        Mix(ref h, s.HeightM); Mix(ref h, s.SlopeDeg); Mix(ref h, s.SlopeBaselineM); Mix(ref h, s.C8M); Mix(ref h, s.C64M);
        Mix(ref h, s.Moisture); Mix(ref h, s.Temperature); Mix(ref h, s.ForestWeight); Mix(ref h, s.GrassWeight); Mix(ref h, s.Canopy);
        Mix(ref h, (float)(int)s.Habitat); Mix(ref h, s.Wetness); Mix(ref h, s.ChannelWetness); Mix(ref h, s.HeightAboveChannelM);
        Mix(ref h, s.RockExposure); Mix(ref h, s.Scree); Mix(ref h, s.SoilDepth);
        if (flags) Mix(ref h, (float)(int)s.Flags);
        return h;
    }

    // ------------------------------------------------------------------ a. purity

    /// <summary>The node set and sample points of a 32 m patch (coarse-aligned) inside a tile, with a 16 m halo.</summary>
    private static (List<(long I, int J)> Coarse, List<(long I, int J)> Fine, List<(double A, double Z)> Points, double A0, double Z0) PatchNodes(Tile tile)
    {
        long ci = (long)Math.Floor((tile.A0 + tile.A1) * 0.5 / 32.0);
        int cj = (int)Math.Floor((tile.Z0 + tile.Z1) * 0.5 / 32.0);
        var coarse = new List<(long, int)>();
        for (long I = ci - 1; I <= ci + 2; I++) for (int J = cj - 1; J <= cj + 2; J++) coarse.Add((I, J));
        var fine = new List<(long, int)>();
        for (long i = ci * 4 - 2; i <= ci * 4 + 6; i++) for (int j = cj * 4 - 2; j <= cj * 4 + 6; j++) fine.Add((i, j));
        var points = new List<(double, double)>();
        var rng = new Random(7 + (int)(ci & 0xffff));
        for (int k = 0; k < 40; k++) points.Add((ci * 32.0 + rng.NextDouble() * 32.0, cj * 32.0 + rng.NextDouble() * 32.0));
        return (coarse, fine, points, ci * 32.0, cj * 32.0);
    }

    private static ulong[] Evaluate(RingHabitatFieldGrid grid, List<(long I, int J)> coarse, List<(long I, int J)> fine, List<(double A, double Z)> points, int rotate)
    {
        int n = coarse.Count + fine.Count + points.Count;
        var result = new ulong[n];
        for (int step = 0; step < n; step++)
        {
            int k = (step + rotate) % n;
            result[k] = k < coarse.Count ? Bits(grid.CoarseNode(coarse[k].I, coarse[k].J))
                : k < coarse.Count + fine.Count ? Bits(grid.FineNode(fine[k - coarse.Count].I, fine[k - coarse.Count].J))
                : Bits(grid.Sample(points[k - coarse.Count - fine.Count].A, points[k - coarse.Count - fine.Count].Z));
        }
        return result;
    }

    private void Purity()
    {
        int compared = 0;
        foreach (int index in new[] { 0, 3, 8, 10 })
        {
            Tile tile = _tiles[index];
            var (coarse, fine, points, pa, pz) = PatchNodes(tile);
            // A: the tile window on the main thread. B: the 32 m patch window on another thread with its own sampler copy.
            RingTerrainSampler copyA = NewCopy(), copyB = NewCopy();
            var gridA = NewGrid(copyA);
            gridA.PrepareWindow(tile.A0 - 16, tile.A1 + 16, tile.Z0 - 16, tile.Z1 + 16);
            ulong[] a = Evaluate(gridA, coarse, fine, points, 0);
            ulong[] b = Task.Run(() =>
            {
                var gridB = NewGrid(copyB);
                gridB.PrepareWindow(pa - 16, pa + 48, pz - 16, pz + 48);
                return Evaluate(gridB, coarse, fine, points, 17);
            }).GetAwaiter().GetResult();
            for (int k = 0; k < a.Length; k++) Check(a[k] == b[k], $"purity tile window vs patch window ({tile.Name}) item {k} differs");
            compared += a.Length;

            // 1 worker against 4 workers over the same node set, each in its own rotation.
            RingTerrainSampler[] copies = Enumerable.Range(0, 4).Select(_ => NewCopy()).ToArray();
            var results = new ulong[4][];
            Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, w =>
            {
                results[w] = Evaluate(NewGrid(copies[w]), coarse, fine, points, w * 37);
            });
            for (int w = 0; w < 4; w++)
                for (int k = 0; k < a.Length; k++) Check(results[w][k] == a[k], $"purity 1 vs 4 workers ({tile.Name}) worker {w} item {k} differs");
            compared += 4 * a.Length;
            copyA.Dispose(); copyB.Dispose();
            foreach (var c in copies) c.Dispose();
        }
        Detail($"purity: {compared} item comparisons across instances, window shapes and 1 vs 4 workers");
    }

    // ------------------------------------------------------------------ b. ring seam

    private void Seam()
    {
        long N = Circ / 8, M = Circ / 32;
        using RingTerrainSampler copy = NewCopy();
        RingLodSurfaceEditSnapshot edits = EmptyEdits(copy);
        var grid = new RingHabitatFieldGrid(copy, edits, LandscapeWorld.Seed, Circ, Width);
        int checks = 0;
        foreach (string pose in new[] { "steep_eye", "plains_eye" })
        {
            Tile tile = _tiles.First(t => t.Name == pose);
            int j = (int)((tile.Z0 + 120) / 8), J = j / 4;
            foreach (long i in new long[] { N - 2, N - 1, 0, 1 })
            {
                // The node sits where its index says: its height is the canonical height at 8 i (wrapped), not a neighbour's.
                Check(grid.FineNode(i, j).HeightM == edits.GetPresentationSurfaceHeight(copy, (double)(i * 8L), (double)(j * 8)), $"seam fine node {i} ({pose}) is not at its canonical position");
                ulong f = Bits(grid.FineNode(i, j));
                Check(f == Bits(grid.FineNode(i + N, j)) && f == Bits(grid.FineNode(i - N, j)), $"seam fine node {i} ({pose}) differs from its wrapped copies");
                checks++;
            }
            foreach (long I in new long[] { M - 2, M - 1, 0, 1 })
            {
                Check(grid.CoarseNode(I, J).HeightM == edits.GetPresentationSurfaceHeight(copy, (double)(I * 32L), (double)(J * 32)), $"seam coarse node {I} ({pose}) is not at its canonical position");
                ulong c = Bits(grid.CoarseNode(I, J));
                Check(c == Bits(grid.CoarseNode(I + M, J)) && c == Bits(grid.CoarseNode(I - M, J)), $"seam coarse node {I} ({pose}) differs from its wrapped copies");
                checks++;
            }
            double across = J * 32.0 + 32.0 * 2; // on the coarse lattice, so the across weight is zero
            int Jx = (int)(across / 32.0);
            foreach (double dx in new[] { 4.0, 0.0, 3.0 })
            {
                // around = C - 4 (and the same point written as -4 and 2C - 4), plus a point straddling the seam
                double around = Circ - dx;
                ulong s = Bits(grid.Sample(around, across));
                Check(s == Bits(grid.Sample(around - Circ, across)) && s == Bits(grid.Sample(around + Circ, across)), $"seam sample at C-{dx} ({pose}) differs from its wrapped copies");
                checks++;
            }
            // straddling sample equals the bilinear of the wrapped nodes it reads
            double ac = Circ - 3.0;
            RingHabitatSample straddle = grid.Sample(ac, across);
            bool fine = grid.IsCellFine(M - 1, Jx);
            RingHabitatSample n0 = fine ? grid.FineNode(N - 1, Jx * 4) : grid.CoarseNode(M - 1, Jx);
            RingHabitatSample n1 = fine ? grid.FineNode(0, Jx * 4) : grid.CoarseNode(0, Jx);
            double fa = fine ? (ac - (Circ - 8.0)) / 8.0 : (ac - (Circ - 32.0)) / 32.0;
            double B(double a, double b) => a * (1.0 - fa) + b * fa;
            Check(straddle.HeightM == (float)B(n0.HeightM, n1.HeightM), $"seam straddle height ({pose}) {straddle.HeightM} vs {(float)B(n0.HeightM, n1.HeightM)}");
            Check(straddle.C64M == (float)B(n0.C64M, n1.C64M), $"seam straddle C64 ({pose})");
            Check(straddle.Moisture == (float)B(n0.Moisture, n1.Moisture), $"seam straddle moisture ({pose})");
            checks += 3;
            Detail($"seam {pose}: straddle cell read {(fine ? "fine" : "coarse")} nodes");
        }
        Detail($"seam: {checks} checks");
    }

    // ------------------------------------------------------------------ c. rim

    private void Rim()
    {
        int lastF = (Width - 1) / 8, lastC = (Width - 1) / 32;
        using RingTerrainSampler copy = NewCopy();
        var grid = NewGrid(copy);
        long i = 625_000, I = i / 4;
        void Same(RingHabitatSample clamped, RingHabitatSample edge, string what)
        {
            Check(Bits(clamped, false) == Bits(edge, false), what + " differs from the edge node");
            Check((clamped.Flags & RingHabitatFlags.RimClamped) != 0 && (edge.Flags & RingHabitatFlags.RimClamped) == 0, what + " rim flag wrong");
        }
        Same(grid.FineNode(i, -3), grid.FineNode(i, 0), "fine below 0");
        Same(grid.FineNode(i, lastF + 5), grid.FineNode(i, lastF), "fine above last");
        Same(grid.CoarseNode(I, -2), grid.CoarseNode(I, 0), "coarse below 0");
        Same(grid.CoarseNode(I, lastC + 3), grid.CoarseNode(I, lastC), "coarse above last");
        double around = I * 32.0 + 11.0;
        double edgeAcross = lastC * 32.0;
        Same(grid.Sample(around, -7.0), grid.Sample(around, 0.0), "sample below 0");
        Same(grid.Sample(around, Width), grid.Sample(around, edgeAcross), "sample above last node");
        Same(grid.Sample(around, edgeAcross + 9.0), grid.Sample(around, edgeAcross), "sample just above last node");
        var (gx, gz) = grid.Gradient(around, -50.0);
        var (ex, ez) = grid.Gradient(around, 0.0);
        Check(gx == ex && gz == ez, "rim gradient differs");
        Detail($"rim: last fine node {lastF} ({lastF * 8} m), last coarse node {lastC} ({lastC * 32} m)");
    }

    // ------------------------------------------------------------------ d. analytic

    private sealed class SyntheticSource : IHabitatSource
    {
        private readonly Func<double, double, double> _f;
        public SyntheticSource(Func<double, double, double> f) { _f = f; }
        public void Evaluate(long around, int across, bool withClimate, out TerrainSample sample, out float height)
        {
            sample = default(TerrainSample) with { Moisture = 0.5f, Temperature = 0.5f };
            height = (float)_f(around, across);
        }
        public RingTerrainSampler.BiomeTextureWeights Weights(in TerrainSample sample) => new(0f, 0f, 0f, 0f);
        public bool IsWater(in TerrainSample sample, float height) => false;
    }

    private void Analytic()
    {
        const double ca = 1_000_000.0, cz = 100_000.0;
        RingHabitatFieldGrid Grid(Func<double, double, double> f, bool force) => new(new SyntheticSource(f), 1, Circ, Width, force);

        // Plane: gradient (0.5, 0.25).
        var plane = Grid((a, z) => 0.5 * (a - ca) + 0.25 * (z - cz), true);
        double planeSlope = Math.Atan(Math.Sqrt(0.5 * 0.5 + 0.25 * 0.25)) * 180.0 / Math.PI;
        double worstC8 = 0, worstC64 = 0, worstSlope = 0;
        for (long i = (long)(ca / 8) - 6; i <= (long)(ca / 8) + 6; i++)
            for (int j = (int)(cz / 8) - 6; j <= (int)(cz / 8) + 6; j++)
            {
                var s = plane.FineNode(i, j);
                worstC8 = Math.Max(worstC8, Math.Abs(s.C8M)); worstC64 = Math.Max(worstC64, Math.Abs(s.C64M)); worstSlope = Math.Max(worstSlope, Math.Abs(s.SlopeDeg - planeSlope));
            }
        for (long I = (long)(ca / 32) - 4; I <= (long)(ca / 32) + 4; I++)
            for (int J = (int)(cz / 32) - 4; J <= (int)(cz / 32) + 4; J++)
            {
                var s = plane.CoarseNode(I, J);
                worstC64 = Math.Max(worstC64, Math.Abs(s.C64M)); worstSlope = Math.Max(worstSlope, Math.Abs(s.SlopeDeg - planeSlope));
            }
        var ps = plane.Sample(ca + 13.7, cz + 21.3);
        worstC8 = Math.Max(worstC8, Math.Abs(ps.C8M)); worstC64 = Math.Max(worstC64, Math.Abs(ps.C64M)); worstSlope = Math.Max(worstSlope, Math.Abs(ps.SlopeDeg - planeSlope));
        Check(worstC8 < 1e-4 && worstC64 < 1e-4, $"plane curvature C8 {worstC8:E2} C64 {worstC64:E2}");
        Check(worstSlope < 1e-3, $"plane slope error {worstSlope:E2} deg (expected {planeSlope:F4})");
        Detail($"plane: worst |C8| {worstC8:E2}, |C64| {worstC64:E2}, slope error {worstSlope:E2} deg ({planeSlope:F3} deg)");

        // Paraboloid h = k r^2: C8 = -64 k, C64 = -4096 k, slope atan(2 k r).
        const double k = -0.0005;
        var bowl = Grid((a, z) => k * ((a - ca) * (a - ca) + (z - cz) * (z - cz)), true);
        double expectC8 = -64.0 * k, expectC64 = -4096.0 * k, worstE8 = 0, worstE64 = 0, worstES = 0;
        for (long i = (long)(ca / 8) - 6; i <= (long)(ca / 8) + 6; i++)
            for (int j = (int)(cz / 8) - 6; j <= (int)(cz / 8) + 6; j++)
            {
                var s = bowl.FineNode(i, j);
                double x = i * 8.0 - ca, y = j * 8.0 - cz;
                worstE8 = Math.Max(worstE8, Math.Abs(s.C8M - expectC8));
                worstES = Math.Max(worstES, Math.Abs(s.SlopeDeg - Math.Atan(2 * Math.Abs(k) * Math.Sqrt(x * x + y * y)) * 180.0 / Math.PI));
            }
        for (long I = (long)(ca / 32) - 4; I <= (long)(ca / 32) + 4; I++)
            for (int J = (int)(cz / 32) - 4; J <= (int)(cz / 32) + 4; J++)
                worstE64 = Math.Max(worstE64, Math.Abs(bowl.CoarseNode(I, J).C64M - expectC64));
        var sb = bowl.Sample(ca + 40.0, cz - 24.0);
        worstE8 = Math.Max(worstE8, Math.Abs(sb.C8M - expectC8)); worstE64 = Math.Max(worstE64, Math.Abs(sb.C64M - expectC64));
        Check(worstE8 < 2e-4, $"paraboloid C8 error {worstE8:E2} (expected {expectC8:F5})");
        Check(worstE64 < 1e-3, $"paraboloid C64 error {worstE64:E2} (expected {expectC64:F5})");
        Check(worstES < 2e-3, $"paraboloid slope error {worstES:E2}");
        Check(expectC8 > 0 && expectC64 > 0 && sb.C8M > 0 && sb.C64M > 0, "paraboloid crest has the wrong sign");
        var bowlDown = Grid((a, z) => -k * ((a - ca) * (a - ca) + (z - cz) * (z - cz)), true).Sample(ca + 40.0, cz - 24.0);
        Check(bowlDown.C8M < 0 && bowlDown.C64M < 0, "paraboloid hollow has the wrong sign");
        Detail($"paraboloid: expected C8 {expectC8:F4}, C64 {expectC64:F4}; worst error C8 {worstE8:E2}, C64 {worstE64:E2}, slope {worstES:E2} deg");

        // 50 degree plane: rock exposure 1 at fine nodes, coarse nodes and Sample (default rule: slope selects the fine level).
        double t50 = Math.Tan(50.0 * Math.PI / 180.0);
        var steep = Grid((a, z) => t50 * (a - ca), false);
        var fn = steep.FineNode((long)(ca / 8) + 3, (int)(cz / 8));
        var cn = steep.CoarseNode((long)(ca / 32) + 1, (int)(cz / 32));
        var sp = steep.Sample(ca + 21.0, cz + 9.0);
        Check(fn.RockExposure == 1f && cn.RockExposure == 1f && sp.RockExposure == 1f, $"50 degree plane rock exposure fine {fn.RockExposure} coarse {cn.RockExposure} sample {sp.RockExposure}");
        Check(Math.Abs(fn.SlopeDeg - 50.0) < 1e-3 && Math.Abs(sp.SlopeDeg - 50.0) < 1e-3, $"50 degree plane slope {fn.SlopeDeg} {sp.SlopeDeg}");
        Check((sp.Flags & RingHabitatFlags.Fine) != 0 && sp.SlopeBaselineM == 16f, "50 degree plane sample did not read the fine level");
        Check(Math.Abs(sp.SoilDepth) < 1e-6f, "50 degree plane soil depth is not 0");
        Detail($"50 degree plane: rock exposure {fn.RockExposure}/{cn.RockExposure}/{sp.RockExposure}, slope {fn.SlopeDeg:F4}");
    }

    // ------------------------------------------------------------------ e. agreement with the mirror and the proxy builder

    private static RingLodGeometrySnapshot GeometryFor(RingTerrainSampler sampler, long around0, int across0)
        => new(new Transform3D(new Basis(Quaternion.FromEuler(new Vector3(0.21f, 0.9f, -0.33f))), new Vector3(3, 4, 5)), new Vector3(1, 2, 3), new Vector3(11, 12, 13),
            CertifiedRingScale.EffectiveRadiusMeters, 17.0, 120.5, sampler.AroundRingVoxelLength,
            CertifiedRingScale.TerrainFloorMeters, CertifiedRingScale.StructuralWallTopMeters,
            new RingCanonicalPosition(around0 + 7.0, 0.0, across0 + 3.0));

    private static RingTerrainProxySector.VertexTableFixtureSector? BuildSector(RingTerrainProxySector.VertexTableFixtureSession session, RingTerrainSampler sampler,
        RingTerrainSectorCoordinate coordinate, int spacing)
    {
        const int cells = 64;
        int size = cells * spacing;
        return session.Build(coordinate, spacing, cells, GeometryFor(sampler, coordinate.AroundSector * size, coordinate.AcrossSector * size), buildWalls: false, buildSkirts: true,
            0.2f, 0.35f, new Transform3D(new Basis(Quaternion.FromEuler(new Vector3(-0.4f, 0.15f, 0.7f))), Vector3.Zero), 8.0f, new Color(0.19f, 0.28f, 0.16f), new Color(0.42f, 0.38f, 0.27f), true);
    }

    private void Agreement()
    {
        RingTerrainSampler sampler = _root;
        RingLodSurfaceEditSnapshot edits = EmptyEdits(sampler);
        var grid = new RingHabitatFieldGrid(sampler, edits, LandscapeWorld.Seed, Circ, Width);

        // Pinned choice 1: the full and the height-only path give identical height bits at coarse nodes (>= 200 over the test tiles).
        int pairs = 0, mismatches = 0;
        foreach (Tile tile in _tiles)
        {
            long ci = (long)Math.Floor((tile.A0 + tile.A1) * 0.5 / 32.0);
            int cj = (int)Math.Floor((tile.Z0 + tile.Z1) * 0.5 / 32.0);
            for (int di = 0; di < 4; di++)
                for (int dj = 0; dj < 4; dj++)
                {
                    long around = (ci + di) * 32; int across = (cj + dj) * 32;
                    edits.GetPresentationTerrainSampleAndHeight(sampler, around, across, out _, out float full);
                    edits.GetPresentationHeightOnlyTerrainSampleAndHeight(sampler, around, across, out _, out float only);
                    pairs++;
                    if (BitConverter.SingleToInt32Bits(full) != BitConverter.SingleToInt32Bits(only)) mismatches++;
                }
        }
        Check(pairs >= 200 && mismatches == 0, $"coarse node heights: full vs height-only mismatches {mismatches} of {pairs}");
        Detail($"height paths: {pairs} coarse nodes, {mismatches} mismatches (bit compare)");

        // C64 at coarse nodes against NodeConvexity over the presentation height.
        double HeightAt(double a, double z) => edits.GetPresentationSurfaceHeight(sampler, a, z);
        int nodes = 0, badNodes = 0; double worstModel = 0;
        foreach (Tile tile in _tiles)
        {
            long ci = (long)Math.Floor((tile.A0 + tile.A1) * 0.5 / 32.0);
            int cj = (int)Math.Floor((tile.Z0 + tile.Z1) * 0.5 / 32.0);
            for (int k = 0; k < 4; k++)
            {
                long I = ci + k * 2 - 3; int J = cj + (k % 2) * 3 - 1;
                float expected = (float)RingGroundSurfaceModel.NodeConvexity(HeightAt, I * 32.0, J * 32.0);
                float actual = grid.CoarseNode(I, J).C64M;
                nodes++;
                worstModel = Math.Max(worstModel, Math.Abs(expected - actual));
                if (BitConverter.SingleToInt32Bits(expected) != BitConverter.SingleToInt32Bits(actual)) badNodes++;
            }
        }
        Check(nodes >= 50 && badNodes == 0, $"C64 vs NodeConvexity: {badNodes} of {nodes} nodes not bit-equal (worst {worstModel:E2} m)");
        Detail($"C64 vs RingGroundSurfaceModel.NodeConvexity: {nodes} nodes, {badNodes} not bit-equal, worst {worstModel:E2} m");

        // The proxy builder's node values (Lod1-style 32 m sector, relief array), when the world carries the ground.surface bit.
        if (_groundSurfaceWorld)
        {
            var session = RingTerrainProxySector.CreateVertexTableFixtureSession(edits, sampler, sampler.RingAcrossWidthMeters);
            const int cells = 64, spacing = 32, size = cells * spacing;
            long aroundSectors = (sampler.AroundRingVoxelLength + size - 1) / size;
            int acrossSectors = (sampler.RingAcrossWidthMeters + size - 1) / size;
            RingTerrainSectorCoordinate coordinate = default;
            RingTerrainProxySector.VertexTableFixtureSector? sector = null;
            for (int j = 2; j < 400 && sector == null; j++)
            {
                var c = new RingTerrainSectorCoordinate((long)(((j * 0.6180339887) % 1.0) * aroundSectors), Math.Clamp((int)(((j * 0.7548776662) % 1.0) * acrossSectors), 2, acrossSectors - 3));
                var s = BuildSector(session, sampler, c, spacing);
                if (s != null && s.OceanIndexCount == 0) { sector = s; coordinate = c; }
            }
            Check(sector?.Relief != null, "no 32 m proxy sector with a relief array found");
            if (sector?.Relief != null)
            {
                int row = cells + 1, compared = 0, notBit = 0; double worst = 0;
                var rng = new Random(32);
                for (int k = 0; k < 60; k++)
                {
                    int x = rng.Next(0, cells + 1), z = rng.Next(0, cells + 1);
                    float proxy = sector.Relief[z * row + x];
                    float mine = grid.CoarseNode(coordinate.AroundSector * (size / 32) + x, (int)(coordinate.AcrossSector * (size / 32) + z)).C64M;
                    worst = Math.Max(worst, Math.Abs(proxy - mine));
                    if (BitConverter.SingleToInt32Bits(proxy) != BitConverter.SingleToInt32Bits(mine)) notBit++;
                    compared++;
                }
                Check(worst <= 1e-3, $"C64 vs the proxy builder: worst difference {worst:E2} m");
                Detail($"C64 vs proxy Lod1-style 32 m sector {coordinate}: {compared} nodes, {notBit} not bit-equal, worst {worst:E2} m ({(notBit == 0 ? "bit-equal" : "tolerance 1e-3 m")})");
            }
        }
        else Detail("proxy comparison skipped (no ground.surface world)");

        // Rock exposure and scree at 64 points equal the mirror functions over the point's own inputs.
        var random = new Random(64);
        int pointChecks = 0;
        for (int k = 0; k < 64; k++)
        {
            Tile tile = _tiles[k % _tiles.Count];
            double a = tile.A0 + random.NextDouble() * (tile.A1 - tile.A0), z = tile.Z0 + random.NextDouble() * (tile.Z1 - tile.Z0);
            RingHabitatSample s = grid.Sample(a, z);
            double relief = RingGroundSurfaceModel.Relief(s.C64M);
            double rockD = RingGroundSurfaceModel.RockExposure(s.SlopeDeg, s.SlopeBaselineM, relief, s.ForestWeight);
            float rock = (float)rockD;
            float scree = (float)RingGroundSurfaceModel.ScreeWeight(s.SlopeDeg, s.SlopeBaselineM, relief, (double)s.GrassWeight + s.ForestWeight);
            Check(rock == s.RockExposure && scree == s.Scree, $"mirror mismatch at ({a:F1}, {z:F1}): rock {s.RockExposure} vs {rock}, scree {s.Scree} vs {scree}");
            Check(s.SoilDepth == (float)(1.0 - rockD), "soil depth is not 1 - rock exposure");
            Check(s.ChannelWetness == 0f && float.IsPositiveInfinity(s.HeightAboveChannelM), "channel fields are not reserved values");
            pointChecks++;
        }
        // A bilinear value exactly at a node equals the node (flags only add: the point reads just that node).
        int atNode = 0;
        foreach (Tile tile in _tiles.Take(6))
        {
            long ci = (long)Math.Floor((tile.A0 + tile.A1) * 0.5 / 32.0);
            int cj = (int)Math.Floor((tile.Z0 + tile.Z1) * 0.5 / 32.0);
            RingHabitatSample c = grid.CoarseNode(ci, cj), f = grid.FineNode(ci * 4 + 1, cj * 4 + 2);
            bool cellFine = grid.IsCellFine(ci, cj);
            RingHabitatSample sc = grid.Sample(ci * 32.0, cj * 32.0);
            RingHabitatSample sf = grid.Sample(ci * 32.0 + 8.0, cj * 32.0 + 16.0);
            if (cellFine)
            {
                // the 32 m node (4I, 4J) is also a fine node
                RingHabitatSample fc = grid.FineNode(ci * 4, cj * 4);
                Check(Bits(sc) == Bits(fc), $"Sample at a coarse/fine node of a fine cell differs from the fine node ({tile.Name})");
                Check(Bits(sf) == Bits(f), $"Sample at a fine node differs from the node ({tile.Name})");
            }
            else
            {
                Check(Bits(sc) == Bits(c), $"Sample at a coarse node differs from the node ({tile.Name})");
            }
            atNode += 2;
        }
        Detail($"mirror: {pointChecks} points; at-node equality: {atNode} checks");
    }

    // ------------------------------------------------------------------ f. fine-flag identity

    private void FineFlagIdentity()
    {
        double chosen = -1;
        foreach (double threshold in new[] { 12.0, 10.0, 8.0, 6.0 })
        {
            long total = 0;
            var lines = new List<string>();
            foreach (Tile tile in _tiles)
            {
                using RingTerrainSampler ruleCopy = NewCopy(true), allCopy = NewCopy(true);
                var rule = NewGrid(ruleCopy);
                rule.FineSlopeThresholdDeg = threshold;
                var all = NewGrid(allCopy, true);
                long i0 = (long)Math.Floor(tile.A0 / 32.0), i1 = (long)Math.Floor(tile.A1 / 32.0);
                int j0 = (int)Math.Floor(tile.Z0 / 32.0), j1 = (int)Math.Floor(tile.Z1 / 32.0);
                int cellCount = 0, flagged = 0; long violating = 0, nodesChecked = 0; double worstRock = 0, worstScree = 0;
                for (long I = i0; I <= i1; I++)
                    for (int J = j0; J <= j1; J++)
                    {
                        cellCount++;
                        Check(all.IsCellFine(I, J), "forceAllFine left a cell unflagged");
                        if (rule.IsCellFine(I, J)) { flagged++; continue; }
                        for (int a = 0; a <= 4; a++)
                            for (int b = 0; b <= 4; b++)
                            {
                                var s = all.FineNode(I * 4 + a, J * 4 + b);
                                nodesChecked++;
                                worstRock = Math.Max(worstRock, s.RockExposure); worstScree = Math.Max(worstScree, s.Scree);
                                if (s.RockExposure > 0.01f || s.Scree > 0.01f) violating++;
                            }
                    }
                total += violating;
                lines.Add($"  {tile.Name,-22} cells {cellCount,3} flagged {100.0 * flagged / cellCount,5:F1}% unflagged nodes {nodesChecked,5} violating {violating} (worst rock {worstRock:F3}, scree {worstScree:F3})");
            }
            Detail($"fine-flag identity at HabitatFineSlopeDeg {threshold}: {total} violating nodes\n" + string.Join("\n", lines));
            if (total == 0) { chosen = threshold; break; }
        }
        Check(chosen > 0, "fine-flag identity fails down to 6 degrees; stop and report");
        Detail($"fine-flag identity final threshold: {chosen} degrees (shipped constant {RingHabitatFieldGrid.HabitatFineSlopeDeg})");
        Check(chosen == RingHabitatFieldGrid.HabitatFineSlopeDeg, $"the constant must be lowered to {chosen}");
    }

    // ------------------------------------------------------------------ g. cost

    private static readonly Dictionary<string, (double Lo, double Hi, string Text)> Estimates = new()
    {
        ["plains_eye"] = (0.6, 0.6, "0.6"), ["coast_eye"] = (0.6, 0.6, "0.6"), ["hills_eye"] = (1.4, 1.7, "1.4-1.7"),
        ["forest_interior_eye"] = (0.7, 1.7, "0.7-1.7"), ["forest_edge_eye"] = (0.7, 1.7, "0.7-1.7"), ["forest_clearing_eye"] = (0.7, 1.7, "0.7-1.7"), ["lowland_eye"] = (0.7, 1.7, "0.7-1.7"),
        ["steep_eye"] = (3.7, 4.4, "3.7-4.4"), ["scree_eye"] = (3.7, 4.4, "3.7-4.4"), ["cliff_eye"] = (3.7, 4.4, "3.7-4.4"), ["breaks_300m"] = (3.7, 4.4, "3.7-4.4"), ["mountain_2km"] = (3.7, 4.4, "3.7-4.4"),
    };

    private void Cost()
    {
        // Warm-up (JIT) on a throwaway sampler copy.
        using (RingTerrainSampler warm = NewCopy(true))
        {
            Tile w = _tiles[0];
            NewGrid(warm).PrepareWindow(w.A0 - 16, w.A1 + 16, w.Z0 - 16, w.Z1 + 16);
        }
        var lines = new List<string>();
        int over = 0;
        foreach (Tile tile in _tiles)
        {
            using RingTerrainSampler copy = NewCopy(true);
            var grid = NewGrid(copy);
            long t0 = Stopwatch.GetTimestamp();
            grid.PrepareWindow(tile.A0 - 16, tile.A1 + 16, tile.Z0 - 16, tile.Z1 + 16);
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            // Same sampler again with a fresh grid: the sampler's model and lattice caches are warm, as after a tree build in the same job.
            var grid2 = NewGrid(copy);
            long t1 = Stopwatch.GetTimestamp();
            grid2.PrepareWindow(tile.A0 - 16, tile.A1 + 16, tile.Z0 - 16, tile.Z1 + 16);
            double warmMs = (Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency;
            long i0 = (long)Math.Floor((tile.A0 - 16) / 32.0), i1 = (long)Math.Floor((tile.A1 + 16) / 32.0);
            int j0 = Math.Max(0, (int)Math.Floor((tile.Z0 - 16) / 32.0)), j1 = (int)Math.Floor((tile.Z1 + 16) / 32.0);
            int cells = 0, flagged = 0;
            for (long I = i0; I < i1; I++) for (int J = j0; J < j1; J++) { cells++; if (grid.IsCellFine(I, J)) flagged++; }
            string estimate = "n/a", mark = "";
            if (Estimates.TryGetValue(tile.Name, out var e)) { estimate = e.Text; if (ms > e.Hi * 1.5) { mark = " OVER+50%"; over++; } }
            lines.Add($"  {tile.Name,-22} fine cells {100.0 * flagged / cells,5:F1}% fineEval {grid.FineEvaluations,5} coarseEval {grid.CoarseEvaluations,4} climate {grid.ClimateSamples,4} {ms,7:F2} ms (warm sampler {warmMs,5:F2} ms)  (A3 stage B grid: {estimate} ms){mark}");
        }
        Detail("cost per 240 m tile (PrepareWindow over tile +/- 16 m, fresh sampler copy with the lattice cache, this machine):\n" + string.Join("\n", lines));
        if (over > 0) Detail($"COST_ESCALATION {over} tile(s) exceed the A3 stage B grid estimate by more than 50 percent");
    }

    // ------------------------------------------------------------------ h. gate

    private void Gate()
    {
        Check(!RingHabitatFieldGrid.IsEnabled(0), "IsEnabled(0) is true");
        Check(RingHabitatFieldGrid.IsEnabled(1 << 4), "IsEnabled(1 << 4) is false");
        Check(!RingHabitatFieldGrid.IsEnabled(~(1 << 4)), "IsEnabled ignores the bit");
        Check(!RingHabitatFieldGrid.IsEnabled(_root.WorldFeatureMask), $"the world's feature mask {_root.WorldFeatureMask} has the scatter.rocks bit set");
        bool threw = false;
        try { _ = new RingHabitatFieldGrid(new SyntheticSource((a, z) => 0), 1, 31_415_937L, Width); } catch (ArgumentException) { threw = true; }
        Check(threw, "a circumference that is not a multiple of 32 was accepted");
        Detail($"gate: world feature mask {_root.WorldFeatureMask}, scatter.rocks bit clear");
    }

    // ------------------------------------------------------------------ debug image

    private void WritePng()
    {
        const int scale = 3, samples = 120;
        Image image = Image.CreateEmpty(samples * scale * 2, samples * scale, false, Image.Format.Rgb8);
        int offset = 0;
        foreach (string name in new[] { "steep_eye", "forest_edge_eye" })
        {
            Tile tile = _tiles.First(t => t.Name == name);
            using RingTerrainSampler copy = NewCopy(true);
            var grid = NewGrid(copy);
            grid.PrepareWindow(tile.A0 - 16, tile.A1 + 16, tile.Z0 - 16, tile.Z1 + 16);
            for (int y = 0; y < samples; y++)
                for (int x = 0; x < samples; x++)
                {
                    RingHabitatSample s = grid.Sample(tile.A0 + 2.0 * x, tile.Z0 + 2.0 * y);
                    var colour = new Color(s.RockExposure, 0f, s.Wetness);
                    for (int dy = 0; dy < scale; dy++)
                        for (int dx = 0; dx < scale; dx++)
                            image.SetPixel(offset + x * scale + dx, (samples - 1 - y) * scale + dy, colour);
                }
            offset += samples * scale;
        }
        string path = ProjectSettings.GlobalizePath(PngPath);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        Check(image.SavePng(path) == Error.Ok, "png write failed");
        Detail("png: " + path);
    }
}
