using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Shadows;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Vegetation.Canopy;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Forest lighting FL-1 headless coverage (docs/forest-lighting-plan.md section 5): the pure canopy raster builder (single crown value, area conservation, multiplicative gap,
/// mips, base/top channels, ring wrap, frame parity against RingLodGeometrySnapshot, determinism under input order, a 13,000 tree build time) and the crown hull table.
/// Run: Godot --headless --path . --script res://tools/forest_canopy_raster_test.gd. Prints FOREST_CANOPY_RASTER=PASS.
/// </summary>
public partial class ForestCanopyRasterTest : RefCounted
{
    private const long Circumference = 31_415_936L;
    private const int S = ForestCanopyRaster.Size;
    private int _checks;
    private readonly ForestCanopyRaster _builder = new();

    public bool Run()
    {
        try
        {
            VerifySingleTree();
            VerifyCoincidentCrowns();
            VerifyMips();
            VerifyUniformStand();
            VerifyWrap();
            VerifyFrameParity();
            VerifyDeterminism();
            VerifyHullTable();
            VerifyTiming();
            GD.Print($"FOREST_CANOPY_RASTER=PASS checks={_checks}");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError("FOREST_CANOPY_RASTER=FAIL " + exception.Message + "\n" + exception.StackTrace);
            return false;
        }
    }

    private void Require(bool ok, string message)
    {
        _checks++;
        if (!ok) throw new InvalidOperationException(message);
    }

    private static CanopyTree Tree(ulong id, double around, double across, float radius, float baseAlt = 4f, float topAlt = 12f, float porosity = 0.2f, float species = 1f)
        => new(id, around, across, radius, baseAlt, topAlt, porosity, species);

    private CanopyRasterResult Build(IReadOnlyList<CanopyTree> trees, long anchorAround, long anchorAcross, float reference = 0f)
        => _builder.Build(trees.ToArray(), anchorAround, anchorAcross, Circumference, reference);

    private const long AnchorA = 160_000, AnchorB = 48_000;

    // ---- (a) ----

    private void VerifySingleTree()
    {
        foreach (float r in new[] { 0.7f, 3f, 6f })
            foreach ((double ox, double oy) in new[] { (1.0, 1.0), (0.3, 0.7), (-0.9, 1.6) })
            {
                var result = Build(new[] { Tree(1, AnchorA + ox, AnchorB + oy, r) }, AnchorA, AnchorB);
                double area = 0;
                float limit = r + (r < 1f ? 3f : 2f);
                for (int j = 0; j < S; j++)
                    for (int i = 0; i < S; i++)
                    {
                        float b = result.Texel(0, i, j, 0);
                        double dx = (i + 0.5) * 2 - 512 - ox, dy = (j + 0.5) * 2 - 512 - oy;
                        if (Math.Sqrt(dx * dx + dy * dy) > limit) Require(b == 0f, $"r={r}: b={b} at distance {Math.Sqrt(dx * dx + dy * dy):F2} beyond r + 1 texel");
                        area += b / 0.8 * 4.0;
                    }
                double exact = Math.PI * r * r;
                Require(Math.Abs(area - exact) <= 0.05 * exact, $"r={r} offset=({ox},{oy}): deposited area {area:F3} vs {exact:F3}");
            }
        foreach (float r in new[] { 3f, 6f })
        {
            var result = Build(new[] { Tree(1, AnchorA + 1, AnchorB + 1, r) }, AnchorA, AnchorB);
            float centre = result.Texel(0, 256, 256, 0);
            Require(Math.Abs(centre - 0.8f) < 1e-3f, $"r={r}: centre b {centre} != 1 - p");
        }
    }

    // ---- (b) ----

    private void VerifyCoincidentCrowns()
    {
        var result = Build(new[] { Tree(1, AnchorA + 1, AnchorB + 1, 3f, 4f, 12f, 0.2f, 1f), Tree(2, AnchorA + 1, AnchorB + 1, 3f, 10f, 20f, 0.3f, 0.6f) }, AnchorA, AnchorB);
        float b = result.Texel(0, 256, 256, 0);
        Require(Math.Abs(b - (1f - 0.2f * 0.3f)) < 1e-3f, $"coincident crowns: b {b} != 1 - p1 p2");
        // Contribution weights are (1 - p): 0.8 and 0.7.
        float baseMean = (0.8f * 4f + 0.7f * 10f) / 1.5f, topMean = (0.8f * 12f + 0.7f * 20f) / 1.5f, speciesMean = (0.8f * 1f + 0.7f * 0.6f) / 1.5f;
        Require(Math.Abs(result.Texel(0, 256, 256, 1) / b - baseMean) < 0.02f, "coincident crowns: base mean");
        Require(Math.Abs(result.Texel(0, 256, 256, 2) / b - topMean) < 0.02f, "coincident crowns: top mean");
        Require(Math.Abs(result.Texel(0, 256, 256, 3) / b - speciesMean) < 1e-3f, "coincident crowns: species mean");
    }

    // ---- (c) ----

    private void VerifyMips()
    {
        var rng = new Random(5);
        var trees = new List<CanopyTree>();
        for (int i = 0; i < 400; i++)
            trees.Add(Tree((ulong)i + 1, AnchorA + rng.Next(-500, 500) + rng.NextDouble(), AnchorB + rng.Next(-500, 500) + rng.NextDouble(), 0.8f + (float)rng.NextDouble() * 5f,
                2f + (float)rng.NextDouble() * 5f, 9f + (float)rng.NextDouble() * 12f, rng.Next(2) == 0 ? 0.2f : 0.3f, rng.Next(2) == 0 ? 1f : 0.6f));
        var result = Build(trees, AnchorA, AnchorB, 3f);
        Require(result.Data.Length == ForestCanopyRaster.TotalTexels * 8 && ForestCanopyRaster.TotalTexels == 349_525, "mip chain size");
        for (int level = 1; level < ForestCanopyRaster.Levels; level++)
        {
            int size = S >> level;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        float mean = (result.Texel(level - 1, 2 * x, 2 * y, c) + result.Texel(level - 1, 2 * x + 1, 2 * y, c) + result.Texel(level - 1, 2 * x, 2 * y + 1, c) + result.Texel(level - 1, 2 * x + 1, 2 * y + 1, c)) * 0.25f;
                        float stored = result.Texel(level, x, y, c);
                        Require(Math.Abs(mean - stored) <= 2.5e-3f * Math.Max(Math.Abs(mean), Math.Abs(stored)) + 2e-4f, $"mip {level} ({x},{y}) c={c}: {stored} vs mean {mean}");
                    }
        }
    }

    // ---- (d) ----

    private void VerifyUniformStand()
    {
        var trees = new List<CanopyTree>();
        ulong id = 1;
        for (int i = -20; i <= 20; i++)
            for (int j = -20; j <= 20; j++) trees.Add(Tree(id++, AnchorA + i * 3.0, AnchorB + j * 3.0, 3f, 5f, 15f, 0.2f, 0.6f));
        var result = Build(trees, AnchorA, AnchorB, 2f);
        int checkedTexels = 0;
        for (int j = 236; j < 276; j++)
            for (int i = 236; i < 276; i++)
            {
                float b = result.Texel(0, i, j, 0);
                if (b < 0.1f) continue;
                checkedTexels++;
                Require(Math.Abs(result.Texel(0, i, j, 1) / b - 3f) < 0.03f, $"G/R {result.Texel(0, i, j, 1) / b} != base - reference");
                Require(Math.Abs(result.Texel(0, i, j, 2) / b - 13f) < 0.05f, $"B/R {result.Texel(0, i, j, 2) / b} != top - reference");
                Require(Math.Abs(result.Texel(0, i, j, 3) / b - 0.6f) < 2e-3f, "A/R != species factor");
            }
        Require(checkedTexels > 1000, "uniform stand covers the centre");
    }

    // ---- (e) ----

    private void VerifyWrap()
    {
        long anchor = Circumference - 100;
        var result = _builder.Build(new[] { Tree(1, 50, AnchorB + 7, 4f) }, anchor, AnchorB, Circumference, 0f);
        double sumB = 0, sumX = 0, sumY = 0;
        for (int j = 0; j < S; j++)
            for (int i = 0; i < S; i++)
            {
                float b = result.Texel(0, i, j, 0);
                sumB += b; sumX += b * ((i + 0.5) * 2 - 512); sumY += b * ((j + 0.5) * 2 - 512);
            }
        Require(sumB > 0, "wrapped tree deposited nothing");
        Require(Math.Abs(sumX / sumB - 150.0) < 0.1 && Math.Abs(sumY / sumB - 7.0) < 0.1, $"wrapped tree centroid ({sumX / sumB:F2}, {sumY / sumB:F2}) != (150, 7)");
        // And the other way round: anchor just after the seam, tree before it.
        result = _builder.Build(new[] { Tree(1, Circumference - 30, AnchorB, 4f) }, 70, AnchorB, Circumference, 0f);
        sumB = sumX = 0;
        for (int j = 0; j < S; j++)
            for (int i = 0; i < S; i++) { float b = result.Texel(0, i, j, 0); sumB += b; sumX += b * ((i + 0.5) * 2 - 512); }
        Require(sumB > 0 && Math.Abs(sumX / sumB + 100.0) < 0.1, "tree behind the seam lands 100 m behind the anchor");
    }

    // ---- (f) ----

    private void VerifyFrameParity()
    {
        double radius = Circumference / (2.0 * Math.PI);
        var rng = new Random(21);
        foreach (long anchorAround in new[] { 160_000L, Circumference - 100L, 64L })
            for (int variant = 0; variant < 3; variant++)
            {
                Basis basis = variant == 0 ? Basis.Identity : new Basis(new Vector3(rng.NextSingle() - .5f, rng.NextSingle() - .5f, rng.NextSingle() - .5f).Normalized(), (float)(rng.NextDouble() * 6.28));
                var centre = variant == 2 ? new Vector3(rng.Next(-5000, 5000), rng.Next(-5000, 5000), rng.Next(-5000, 5000)) : Vector3.Zero;
                long anchorAcross = 100_000 + rng.Next(0, 80) * 16;
                var floatingAnchor = new RingCanonicalPosition(anchorAround, 0.0, anchorAcross);
                var geometry = new RingLodGeometrySnapshot(new Transform3D(basis, new Vector3(rng.Next(-300, 300), rng.Next(-300, 300), rng.Next(-300, 300))), new Vector3(rng.Next(-90, 90), rng.Next(-90, 90), rng.Next(-90, 90)),
                    centre, radius, 1234.0, 0.0, Circumference, -1000.0, 1000.0, floatingAnchor);
                double[] m = RingTerrainHeightShadow.BuildFrameFor(geometry, anchorAround, anchorAcross, Circumference);
                var trees = new List<CanopyTree>();
                var expectedTexel = new List<(double X, double Y, double Altitude)>();
                var worlds = new List<Vector3>();
                for (int k = 0; k < 20; k++)
                {
                    double around = anchorAround + (rng.NextDouble() - .5) * 900.0, across = anchorAcross + (rng.NextDouble() - .5) * 900.0, altitude = (rng.NextDouble() - .3) * 60.0;
                    double canonicalAround = around - Math.Floor(around / Circumference) * Circumference;
                    trees.Add(Tree((ulong)k + 1, canonicalAround, across, 0.5f));
                    worlds.Add(geometry.CanonicalToWorld(canonicalAround, altitude, across));
                    // The builder's texel position of this tree in anchor-local metres (wrapped delta, as in Splat).
                    double local = canonicalAround - anchorAround;
                    if (local > Circumference * 0.5) local -= Circumference; else if (local < -Circumference * 0.5) local += Circumference;
                    expectedTexel.Add((local, across - anchorAcross, altitude));
                }
                // Receiver side: world -> frame -> around-local, across-local, altitude. Compare in float32 matrix form too (what the GPU multiplies).
                for (int k = 0; k < 20; k++)
                {
                    var f = HeightShadowFrameMath.Apply(m, worlds[k].X, worlds[k].Y, worlds[k].Z);
                    var d = HeightShadowFrameMath.Decode(f, radius);
                    Require(Math.Abs(d.Around - expectedTexel[k].X) < 0.01, $"frame parity around {d.Around} vs {expectedTexel[k].X} (anchor {anchorAround}, variant {variant})");
                    Require(Math.Abs(d.Across - expectedTexel[k].Y) < 0.01, $"frame parity across {d.Across} vs {expectedTexel[k].Y}");
                    Require(Math.Abs(d.Height - expectedTexel[k].Altitude) < 0.01, $"frame parity altitude {d.Height} vs {expectedTexel[k].Altitude}");
                    var f32 = new float[12];
                    for (int i = 0; i < 12; i++) f32[i] = (float)m[i];
                    float fx = f32[0] * worlds[k].X + f32[1] * worlds[k].Y + f32[2] * worlds[k].Z + f32[3];
                    float fy = f32[4] * worlds[k].X + f32[5] * worlds[k].Y + f32[6] * worlds[k].Z + f32[7];
                    float fz = f32[8] * worlds[k].X + f32[9] * worlds[k].Y + f32[10] * worlds[k].Z + f32[11];
                    var d32 = HeightShadowFrameMath.Decode((fx, fy, fz), radius);
                    Require(Math.Abs(d32.Around - expectedTexel[k].X) < 0.01 && Math.Abs(d32.Height - expectedTexel[k].Altitude) < 0.01, "frame parity in float32");
                }
                // The raster puts each tree where the frame says: the texel containing (around-local, across-local) holds its crown.
                var result = _builder.Build(trees.ToArray(), anchorAround, anchorAcross, Circumference, 0f);
                for (int k = 0; k < 20; k++)
                {
                    int i = (int)Math.Floor((expectedTexel[k].X + 512) / 2), j = (int)Math.Floor((expectedTexel[k].Y + 512) / 2);
                    float nearby = 0;
                    for (int dj = -1; dj <= 1; dj++) for (int di = -1; di <= 1; di++) nearby += result.Texel(0, i + di, j + dj, 0);
                    Require(nearby > 0f, "tree absent from the raster at the frame's texel");
                }
            }
    }

    // ---- (g) ----

    private void VerifyDeterminism()
    {
        var rng = new Random(9);
        var trees = new List<CanopyTree>();
        for (int i = 0; i < 2000; i++)
            trees.Add(Tree((ulong)(i * 7919 + 13), AnchorA + (rng.NextDouble() - .5) * 900, AnchorB + (rng.NextDouble() - .5) * 900, 1f + (float)rng.NextDouble() * 5f,
                2f + (float)rng.NextDouble() * 5f, 9f + (float)rng.NextDouble() * 12f, rng.Next(2) == 0 ? 0.2f : 0.3f, rng.Next(2) == 0 ? 1f : 0.6f));
        byte[] first = Build(trees, AnchorA, AnchorB, 1f).Data.ToArray(); // the builder reuses its output buffer
        for (int pass = 0; pass < 3; pass++)
        {
            var shuffled = trees.OrderBy(_ => rng.Next()).ToList();
            Require(first.AsSpan().SequenceEqual(Build(shuffled, AnchorA, AnchorB, 1f).Data), $"output bytes differ under input order (pass {pass}); also covers builder reuse");
        }
    }

    // ---- (h) ----

    private void VerifyHullTable()
    {
        TreeCrownHull[] table = TreeCrownHullTable.Get();
        var catalog = ImportedTreeCatalog.Load();
        Require(table.Length == ImportedTreeCatalog.VariantCount && table.Length == 30, "hull table size");
        for (int i = 0; i < table.Length; i++)
        {
            TreeCrownHull h = table[i];
            Require(h.Variant == i && h.Species == catalog[i].Species, $"variant {i} species");
            float[] values = { h.CentreX, h.CentreY, h.CentreZ, h.RadiusHorizontal, h.RadiusVertical, h.Kappa, h.Porosity, h.KSpecies, h.HeightMeters };
            Require(values.All(float.IsFinite), $"variant {i} finite");
            Require(h.RadiusHorizontal > 0 && h.RadiusVertical > 0, $"variant {i} semi-axes");
            Require(h.Base < h.Top && h.Top <= 1.0005f && h.Base >= -0.0005f, $"variant {i} base {h.Base} top {h.Top} within the unit tree");
            Require(Math.Abs(h.HeightMeters - catalog[i].HeightMeters) < 1e-4f, $"variant {i} height");
            bool maple = h.Species == TreeSpecies.Maple;
            Require(h.Porosity == TreeCrownHullTable.MeasuredPorosity[i] && h.KSpecies == (maple ? 0.9f : 0.35f) && h.SpeciesFactor == (maple ? 1f : 0.6f), $"variant {i} species constants");
            Require(Math.Abs(h.Kappa - (-MathF.Log(TreeCrownHullTable.CrownModelPorosity(h.Species)) / (4f / 3f * h.RadiusMean))) < 1e-4f * h.Kappa, $"variant {i} kappa");
        }
        foreach (TreeSpecies species in new[] { TreeSpecies.Maple, TreeSpecies.Fir, TreeSpecies.Pine })
        {
            TreeCrownHull h = table.First(t => t.Species == species);
            GD.Print($"FOREST_CANOPY_HULL variant={h.Variant} {species} height_m={h.HeightMeters:F2} centre=({h.CentreX:F3},{h.CentreY:F3},{h.CentreZ:F3}) rh={h.RadiusHorizontal:F3} rv={h.RadiusVertical:F3} base={h.Base:F3} top={h.Top:F3} kappa={h.Kappa:F3} k_species={h.KSpecies}");
        }
    }

    // ---- (i) ----

    private void VerifyTiming()
    {
        var rng = new Random(33);
        var trees = new CanopyTree[13_000];
        for (int i = 0; i < trees.Length; i++)
        {
            float width = 3f + (float)rng.NextDouble() * 5f;
            bool maple = rng.Next(2) == 0;
            float baseAlt = 3f + (float)rng.NextDouble() * 4f;
            trees[i] = Tree((ulong)i + 1, AnchorA + (rng.NextDouble() - .5) * 1000, AnchorB + (rng.NextDouble() - .5) * 1000, width * 0.5f, baseAlt, baseAlt + 8f + (float)rng.NextDouble() * 10f, maple ? 0.2f : 0.3f, maple ? 1f : 0.6f);
        }
        _builder.Build(trees, AnchorA, AnchorB, Circumference, 3f); // warm up (JIT, scratch allocation)
        var times = new double[7];
        for (int i = 0; i < times.Length; i++) times[i] = _builder.Build(trees, AnchorA, AnchorB, Circumference, 3f).BuildMilliseconds;
        Array.Sort(times);
        GD.Print($"FOREST_CANOPY_BUILD trees=13000 median_ms={times[3]:F2} min_ms={times[0]:F2} max_ms={times[^1]:F2}");
        // The runner sets RINGWORLD_TEST_PARALLEL_LANES above 1 while headless tests share the CPU; the limit then gets 2.5x slack (as tools/RingWindQueryTest.cs).
        double slack = int.TryParse(System.Environment.GetEnvironmentVariable("RINGWORLD_TEST_PARALLEL_LANES"), out int lanes) && lanes > 1 ? 2.5 : 1.0;
        double limit = 10.0 * slack;
        GD.Print($"FOREST_CANOPY_BUILD slack={slack:F1} limit_ms={limit:F1}");
        Require(times[3] < limit, $"13,000 tree build median {times[3]:F2} ms >= {limit:F1} ms");
    }
}
