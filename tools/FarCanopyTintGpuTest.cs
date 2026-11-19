using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T6-10 (docs/work-orders/terrain-vegetation/T6.md, design B5): the C# side of far_canopy_tint_gpu_test.gd. CPU checks of the canopy fold:
///   a. generation 1 identity: a world without bit 8 returns the coordinate overloads' weights bit for bit equal to the sample overload, and its far colour is the same with and without coordinates; in a world with
///      bit 8 the sample overload (the habitat grid's: rocks must not move) still returns the natural weights of generation 1;
///   b. the fold: forest' = max(0, 1 - (desert + tundra + alpine)) * cover, 0 over water, never above the natural vegetated share, and grass is the remainder;
///   c. the boundary: on transects across forest edges the 0.5 contour of the forest weight (the tint, sampled on a proxy lattice of the vertex spacing at 2, 5 and 20 km) lies within one proxy vertex spacing of
///      the contour where the expected stem density (regional density x stem factor x treeline, slope 0) is half its interior value;
///   d. the published canopy albedo (impostor atlas, species shares, self-shading) is plausible.
/// The GPU half (shader identity with the feature off, the tint ramp over distance) is in the .gd. Prints FAR_CANOPY_TINT_GPU lines; Run returns 0 on pass.
/// </summary>
public partial class FarCanopyTintGpuTest : RefCounted
{
    private const long Circ = LandscapeWorld.Circumference;
    private const int Width = LandscapeWorld.Width;
    private static readonly string[] OnReview = { "ground.surface", "vegetation.far_canopy", "vegetation.forest_structure" };
    // Proxy vertex spacing at a distance (m): the level whose band (ends 1030, 2059, 4119, 8237, 11649, 16475, 32950 m for 2, 4, 8, 16, 32, 64, 128 m at the shipped error target and far scale, the LOD_BANDS log line) holds the distance.
    private static readonly (double Distance, int Spacing)[] Distances = { (2000, 8), (5000, 16), (20000, 128) };
    private readonly List<string> _failures = new();

    private void Check(bool ok, string message) { if (!ok && _failures.Count < 24) _failures.Add(message); }
    private static void Detail(string text) => Console.WriteLine("FAR_CANOPY_TINT_GPU_DETAIL " + text);

    private static bool Same(RingTerrainSampler.BiomeTextureWeights a, RingTerrainSampler.BiomeTextureWeights b)
        => BitConverter.SingleToInt32Bits(a.Desert) == BitConverter.SingleToInt32Bits(b.Desert) && BitConverter.SingleToInt32Bits(a.Forest) == BitConverter.SingleToInt32Bits(b.Forest)
            && BitConverter.SingleToInt32Bits(a.Tundra) == BitConverter.SingleToInt32Bits(b.Tundra) && BitConverter.SingleToInt32Bits(a.Alpine) == BitConverter.SingleToInt32Bits(b.Alpine);

    /// <summary>The published canopy albedo (linear, self-shading applied) or a negative vector when the baked atlas is missing; the unshaded mean is in z of the second value.</summary>
    public Vector3 CanopyAlbedo() => RingFarCanopy.TryGetAlbedo(out Vector3 albedo) ? albedo : new Vector3(-1, -1, -1);

    public float SelfShading() => RingFarCanopy.SelfShading;

    /// <summary>
    /// Pose finder for the calibration stills (T6-10 step 4): from a standpoint in the far_canopy review world, the mean forest weight in the bands 4.8-5.4 km and 5.6-6.2 km along each of eight headings (0 = +around, 90 = +across),
    /// so a heading with forest on both sides of the 5.5 km tree range can be chosen. Prints FAR_CANOPY_TINT_GPU_SCAN lines.
    /// </summary>
    public void Scan(long around, int across)
    {
        using RingTerrainSampler on = LandscapeWorld.For(1, OnReview).CreateSampler();
        for (int yaw = 0; yaw < 360; yaw += 45)
        {
            double nearSum = 0, farSum = 0; int nearCount = 0, farCount = 0, nearDry = 0, farDry = 0;
            for (int d = 4800; d <= 6200; d += 50)
            {
                long a = around + (long)Math.Round(Math.Cos(yaw * Math.PI / 180d) * d);
                int z = across + (int)Math.Round(Math.Sin(yaw * Math.PI / 180d) * d);
                if (z < 10 || z > Width - 10) continue;
                TerrainSample t = on.GetTerrainSample(a, z);
                double f = t.HasSurfaceWater ? 0 : on.GetBiomeTextureWeights(a, z).Forest;
                if (d <= 5400) { nearSum += f; nearCount++; if (t.HasSurfaceWater) nearDry++; }
                else if (d >= 5600) { farSum += f; farCount++; if (t.HasSurfaceWater) farDry++; }
            }
            Console.WriteLine($"FAR_CANOPY_TINT_GPU_SCAN ({around},{across}) yaw {yaw}: forest weight {nearSum / Math.Max(1, nearCount):F2} at 4.8-5.4 km, {farSum / Math.Max(1, farCount):F2} at 5.6-6.2 km (water {nearDry}/{farDry})");
        }
    }

    public int Run()
    {
        RingTerrainSampler? off = null, on = null;
        try
        {
            off = LandscapeWorld.For(1, new[] { "ground.surface" }).CreateSampler();
            on = LandscapeWorld.For(1, OnReview).CreateSampler();
            Check(off.CanopyFold == null && (off.WorldFeatureMask & (1 << 8)) == 0, "the ground.surface world has bit 8");
            Check(on.CanopyFold != null && (on.WorldFeatureMask & (1 << 8)) != 0, "the far_canopy world lacks bit 8");
            Identity(off, on);
            Fold(on);
            Boundary(on);
            Albedo();
        }
        catch (Exception e) { Console.Error.WriteLine("FAR_CANOPY_TINT_GPU=FAIL exception " + e); return 1; }
        finally { off?.Dispose(); on?.Dispose(); }
        if (_failures.Count != 0) { Console.Error.WriteLine("FAR_CANOPY_TINT_GPU=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("FAR_CANOPY_TINT_GPU_CPU=PASS");
        return 0;
    }

    // ------------------------------------------------------------------ a. identity

    private void Identity(RingTerrainSampler off, RingTerrainSampler on)
    {
        var rng = new Random(20261119);
        int compared = 0, farColour = 0;
        Color low = new(0.12f, 0.19f, 0.12f, 1f), high = new(0.29f, 0.27f, 0.20f, 1f);
        for (int i = 0; i < 1500; i++)
        {
            long around = (long)(rng.NextDouble() * Circ);
            int across = rng.Next(0, Width - 1);
            TerrainSample sample = off.GetTerrainSample(around, across);
            RingTerrainSampler.BiomeTextureWeights natural = off.GetBiomeTextureWeights(sample);
            Check(Same(natural, off.GetBiomeTextureWeights(around, across)), $"({around},{across}): the coordinate overload moved without bit 8");
            Check(Same(natural, off.GetBiomeTextureWeights(sample, around, across)), $"({around},{across}): the sample + coordinate overload moved without bit 8");
            int height = Mathf.RoundToInt(sample.SurfaceHeight);
            Check(RingTerrainVisuals.ResolveMacroTextureColor(sample, height, off, low, high) == RingTerrainVisuals.ResolveMacroTextureColor(sample, height, off, low, high, around, across),
                $"({around},{across}): the far colour moved without bit 8");
            farColour++;
            // The same sample in the bit 8 world: the sample overload (habitat grid, rocks) is the natural weights of the world without the bit, and the folded one never exceeds the vegetated share.
            TerrainSample onSample = on.GetTerrainSample(around, across);
            RingTerrainSampler.BiomeTextureWeights onNatural = on.GetBiomeTextureWeights(onSample);
            Check(Same(natural, onNatural), $"({around},{across}): the sample overload of the far_canopy world differs from generation 1 (rocks would move)");
            compared++;
        }
        Detail($"identity: {compared} points, {farColour} far colours");
    }

    // ------------------------------------------------------------------ b. the fold

    private void Fold(RingTerrainSampler on)
    {
        var rng = new Random(7);
        int land = 0, forestMoved = 0, water = 0;
        double maxNaturalForest = 0, maxFolded = 0;
        for (int i = 0; i < 3000; i++)
        {
            long around = (long)(rng.NextDouble() * Circ);
            int across = rng.Next(0, Width - 1);
            TerrainSample sample = on.GetTerrainSample(around, across);
            RingTerrainSampler.BiomeTextureWeights natural = on.GetBiomeTextureWeights(sample);
            RingTerrainSampler.BiomeTextureWeights folded = on.GetBiomeTextureWeights(around, across);
            if (sample.HasSurfaceWater) { water++; Check(folded.Forest == 0f, $"({around},{across}): forest weight over water"); continue; }
            land++;
            float veg = Math.Max(0f, 1f - (natural.Desert + natural.Tundra + natural.Alpine));
            Check(folded.Desert == natural.Desert && folded.Tundra == natural.Tundra && folded.Alpine == natural.Alpine, $"({around},{across}): the fold moved desert, tundra or alpine");
            Check(folded.Forest >= 0f && folded.Forest <= veg + 1e-6f, $"({around},{across}): forest' {folded.Forest} above the vegetated share {veg}");
            float cover = on.CanopyFold!.Cover(around, across, sample);
            Check(Math.Abs(folded.Forest - veg * cover) < 1e-6f, $"({around},{across}): forest' {folded.Forest} != veg x cover {veg * cover}");
            if (Math.Abs(folded.Forest - natural.Forest) > 1e-4f) forestMoved++;
            maxNaturalForest = Math.Max(maxNaturalForest, natural.Forest); maxFolded = Math.Max(maxFolded, folded.Forest);
        }
        Check(forestMoved > land / 10, $"the fold changed the forest weight at only {forestMoved} of {land} land points");
        Detail($"fold: {land} land, {water} water, forest weight moved at {forestMoved}; max natural forest {maxNaturalForest:F3}, max folded {maxFolded:F3}");
    }

    // ------------------------------------------------------------------ c. the boundary against the stems

    private readonly record struct Probe(long Around, int Across);

    private static float Ss(float e0, float e1, float v) { float t = Math.Clamp((v - e0) / (e1 - e0), 0f, 1f); return t * t * (3f - 2f * t); }

    private static float Stems(RingTerrainSampler s, ForestRegionSampler forest, ForestStructureSampler structure, long around, int across)
    {
        TerrainSample sample = s.GetTerrainSample(around, across);
        ForestRegionSample regional = forest.Sample(around, across, sample, 0f);
        return regional.Density * ForestStructureSampler.StemFactor(structure.EffectiveAge(around, across, regional, sample.Temperature)) * ForestStructureSampler.TreelineDensity(sample.Temperature);
    }

    private static double Crossing(IReadOnlyList<(double X, double V)> points, double level)
    {
        for (int i = 1; i < points.Count; i++)
            if (points[i - 1].V < level && points[i].V >= level)
                return points[i - 1].X + (level - points[i - 1].V) / (points[i].V - points[i - 1].V) * (points[i].X - points[i - 1].X);
        return double.NaN;
    }

    private void Boundary(RingTerrainSampler on)
    {
        var forest = new ForestRegionSampler(on.Seed, on.AroundRingVoxelLength, on.RingAcrossWidthMeters);
        ForestStructureSampler structure = ForestStructureSampler.For(on);
        // Edges: rising crossings of the forest weight 0.5 along around, with the weight under 0.15 400 m before and over 0.85 400 m after.
        var edges = new List<Probe>();
        for (int line = 0; line < 14 && edges.Count < 6; line++)
        {
            int across = 20000 + line * 21000;
            double previous = 0;
            for (long around = 1_000_000; around < 3_000_000 && edges.Count < 6; around += 50)
            {
                double f = on.GetBiomeTextureWeights(around, across).Forest;
                if (around > 1_000_000 && previous < 0.5 && f >= 0.5)
                {
                    double before = on.GetBiomeTextureWeights(around - 400, across).Forest, after = on.GetBiomeTextureWeights(around + 400, across).Forest;
                    if (before < 0.15 && after > 0.85) { edges.Add(new Probe(around, across)); around += 4000; }
                }
                previous = f;
            }
        }
        Check(edges.Count >= 3, $"only {edges.Count} forest edges found for the boundary transects");
        Detail($"boundary: {edges.Count} edges");
        foreach ((double distance, int spacing) in Distances)
        {
            // The biome lattice of the proxies is 16 m. At 2 and 5 km the limit is three lattice cells (48 m): the tint itself starts past 5.5 km, so there the contour only moves the ground texture under the
            // edge trees, which young edge stands (stem factor up to 1.35) push outward. 20 km keeps one vertex spacing.
            int tolerance = distance <= 5000 ? 48 : Math.Max(spacing, 16);
            double worst = 0, alternativeWorst = 0, plainWorst = 0; int judged = 0;
            foreach (Probe edge in edges)
            {
                // Fine transect, 2 m steps over +-600 m: the exact forest weight and the stem density.
                var exact = new List<(double X, double V)>();
                var stems = new List<(double X, double V)>();
                var plainStems = new List<(double X, double V)>(); // diagnostic: density x treeline without the age (stem factor) term
                double interior = 0, plainInterior = 0;
                for (int k = -300; k <= 300; k++)
                {
                    long around = edge.Around + k * 2;
                    float e = Stems(on, forest, structure, around, edge.Across);
                    stems.Add((k * 2, e)); interior = Math.Max(interior, e);
                    TerrainSample ts = on.GetTerrainSample(around, edge.Across);
                    float plain = forest.Sample(around, edge.Across, ts, 0f).Density * ForestStructureSampler.TreelineDensity(ts.Temperature);
                    plainStems.Add((k * 2, plain)); plainInterior = Math.Max(plainInterior, plain);
                    exact.Add((k * 2, on.GetBiomeTextureWeights(around, edge.Across).Forest));
                }
                double stemContour = Crossing(stems, interior * 0.5);
                double plainContour = Crossing(plainStems, plainInterior * 0.5);
                // The tint as the proxy shows it: the weight on a lattice of the vertex spacing, linearly interpolated.
                var lattice = new List<(double X, double V)>();
                for (int x = -600; x <= 600; x += spacing) lattice.Add((x, on.GetBiomeTextureWeights(edge.Around + x, edge.Across).Forest));
                double tintContour = Crossing(lattice, 0.5);
                // Diagnostic only: the contour of the same fold with SmoothStep(0.05, 0.45) (midpoint 0.25), to size the offset a narrower smoothstep would remove.
                var alternative = new List<(double X, double V)>();
                for (int x = -600; x <= 600; x += spacing)
                {
                    long a = edge.Around + x;
                    TerrainSample t = on.GetTerrainSample(a, edge.Across);
                    RingTerrainSampler.BiomeTextureWeights n = on.GetBiomeTextureWeights(t);
                    float v = Math.Max(0f, 1f - (n.Desert + n.Tundra + n.Alpine));
                    float d = forest.Sample(a, edge.Across, t, 0f).Density;
                    alternative.Add((x, v * Ss(0.05f, 0.45f, d) * ForestStructureSampler.TreelineDensity(t.Temperature)));
                }
                double alternativeContour = Crossing(alternative, 0.5);
                if (!double.IsNaN(alternativeContour) && !double.IsNaN(stemContour)) alternativeWorst = Math.Max(alternativeWorst, Math.Abs(alternativeContour - stemContour));
                if (double.IsNaN(stemContour) || double.IsNaN(tintContour)) { Detail($"  {distance / 1000:F0} km edge ({edge.Around},{edge.Across}): no crossing (stem {stemContour:F1}, tint {tintContour:F1})"); continue; }
                judged++;
                double delta = Math.Abs(stemContour - tintContour);
                worst = Math.Max(worst, delta);
                if (!double.IsNaN(plainContour)) plainWorst = Math.Max(plainWorst, Math.Abs(plainContour - tintContour));
                Detail($"  {distance / 1000:F0} km edge ({edge.Around},{edge.Across}): interior stem {interior:F3}, stem contour {stemContour:F1} m, tint contour {tintContour:F1} m (exact {Crossing(exact, 0.5):F1}), delta {delta:F1} m");
            }
            Check(judged >= 3, $"{distance / 1000:F0} km: only {judged} edges judged");
            Check(worst <= tolerance, $"{distance / 1000:F0} km: the tint contour is {worst:F1} m from the stem contour, over one proxy vertex spacing ({tolerance} m)");
            Detail($"boundary {distance / 1000:F0} km: spacing {spacing} m, worst delta {worst:F1} m over {judged} edges (limit {tolerance} m); with SmoothStep(0.05, 0.45) it would be {alternativeWorst:F1} m; against density x treeline without the age term {plainWorst:F1} m");
        }
    }

    // ------------------------------------------------------------------ d. the albedo

    private void Albedo()
    {
        Vector3 albedo = CanopyAlbedo();
        Check(albedo.X >= 0f, "the baked impostor atlas is missing: no canopy albedo");
        if (albedo.X < 0f) return;
        float luminance = 0.2126f * albedo.X + 0.7152f * albedo.Y + 0.0722f * albedo.Z;
        Check(luminance > 0.01f && luminance < 0.25f && albedo.Y > albedo.X && albedo.Y > albedo.Z, $"canopy albedo {albedo} is not a plausible dim green (luminance {luminance:F3})");
        Detail($"canopy albedo (linear, self-shading {RingFarCanopy.SelfShading}) = ({albedo.X:F4}, {albedo.Y:F4}, {albedo.Z:F4}), luminance {luminance:F4}");
    }
}
