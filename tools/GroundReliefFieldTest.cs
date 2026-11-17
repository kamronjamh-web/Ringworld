using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T4-4a (docs/work-orders/terrain-vegetation/T4.md, design D5): the 64 m convexity C64 in the proxy builders' data.
/// Generation 1: four fixed sectors (2 m slot, 4 m slot, 64 m mesh, a river sector) hash to the golden captured before the change (tools/fixtures/ground_relief_golden.txt; --capture rewrites it).
/// Feature world (ground.surface): builder values against RingGroundSurfaceModel.Convexity64 at 200 vertices, shared edge vertices of adjacent sectors on Lod1, Lod2 and Lod6, a convex and a concave mound, and the packed slot bits.
/// Prints GROUND_RELIEF_FIELD=PASS. Headless: tools/run_tests.ps1 -Tests ground_relief_field_test.
/// </summary>
public partial class GroundReliefFieldTest : RefCounted
{
    private const int Seed = 1337;
    private const int Cells = 64;
    private const string GoldenPath = "res://tools/fixtures/ground_relief_golden.txt";
    private readonly List<string> _failures = new();
    private void Check(bool ok, string message) { if (!ok && _failures.Count < 30) _failures.Add(message); }
    private static void Detail(string text) => Console.WriteLine("GROUND_RELIEF_FIELD_DETAIL " + text);

    public int Run()
    {
        bool capture = Array.IndexOf(OS.GetCmdlineUserArgs(), "--capture") >= 0;
        try
        {
            GenerationOne(capture);
            FeatureWorld();
            Mounds();
        }
        catch (Exception e) { Console.Error.WriteLine("GROUND_RELIEF_FIELD=FAIL exception " + e); return 1; }
        finally { RiverGraphStore.Clear(); }
        if (_failures.Count != 0) { Console.Error.WriteLine("GROUND_RELIEF_FIELD=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("GROUND_RELIEF_FIELD=PASS");
        return 0;
    }

    private static RingLodSurfaceEditSnapshot EmptyEdits(RingTerrainSampler sampler) => new(0,
        sampler.AroundRingVoxelLength, new Dictionary<RingTerrainColumnCoordinate, int>(),
        new Dictionary<RingTerrainColumnCoordinate, float>(), sampler.UsesRuntimeSurfaceGeneration);

    private static RingTerrainSampler CreateSampler(TerrainGenerationParameters recipe, VoxelTerrainSettings settings)
        => new(settings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);

    private static RingLodGeometrySnapshot GeometryFor(RingTerrainSampler sampler, long around0, int across0)
        => new(new Transform3D(new Basis(Quaternion.FromEuler(new Vector3(0.21f, 0.9f, -0.33f))), new Vector3(3, 4, 5)), new Vector3(1, 2, 3), new Vector3(11, 12, 13),
            CertifiedRingScale.EffectiveRadiusMeters, 17.0, 120.5, sampler.AroundRingVoxelLength,
            CertifiedRingScale.TerrainFloorMeters, CertifiedRingScale.StructuralWallTopMeters,
            new RingCanonicalPosition(around0 + 7.0, 0.0, across0 + 3.0));

    private static RingTerrainProxySector.VertexTableFixtureSector? BuildSector(RingTerrainProxySector.VertexTableFixtureSession session, RingTerrainSampler sampler,
        RingTerrainSectorCoordinate coordinate, int spacing)
    {
        int size = Cells * spacing;
        return session.Build(coordinate, spacing, Cells, GeometryFor(sampler, coordinate.AroundSector * size, coordinate.AcrossSector * size), buildWalls: false, buildSkirts: true,
            0.2f, 0.35f, new Transform3D(new Basis(Quaternion.FromEuler(new Vector3(-0.4f, 0.15f, 0.7f))), Vector3.Zero), 8.0f, new Color(0.19f, 0.28f, 0.16f), new Color(0.42f, 0.38f, 0.27f), true);
    }

    /// <summary>The first sector of a fixed golden-ratio sequence that is dry land (no water geometry), so the hash covers real terrain data.</summary>
    private static (RingTerrainSectorCoordinate Coordinate, RingTerrainProxySector.VertexTableFixtureSector Sector) FirstDrySector(
        RingTerrainProxySector.VertexTableFixtureSession session, RingTerrainSampler sampler, int spacing)
    {
        int size = Cells * spacing, ringWidth = sampler.RingAcrossWidthMeters;
        long aroundSectors = (sampler.AroundRingVoxelLength + size - 1) / size;
        int acrossSectors = (ringWidth + size - 1) / size;
        for (int j = 2; j < 400; j++)
        {
            long around = (long)(((j * 0.6180339887) % 1.0) * aroundSectors);
            int across = Math.Clamp((int)(((j * 0.7548776662) % 1.0) * acrossSectors), 2, acrossSectors - 3);
            var coordinate = new RingTerrainSectorCoordinate(around, across);
            RingTerrainProxySector.VertexTableFixtureSector? sector = BuildSector(session, sampler, coordinate, spacing);
            if (sector != null && sector.OceanIndexCount == 0) return (coordinate, sector);
        }
        throw new InvalidOperationException($"no dry sector found at {spacing} m");
    }

    // ------------------------------------------------------------------ generation 1 identity

    private void GenerationOne(bool capture)
    {
        var hashes = new SortedDictionary<string, ulong>(StringComparer.Ordinal);
        TerrainGenerationParameters recipe = EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1);
        using (VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, recipe))
        using (RingTerrainSampler sampler = CreateSampler(recipe, settings))
        {
            var session = RingTerrainProxySector.CreateVertexTableFixtureSession(EmptyEdits(sampler), sampler, sampler.RingAcrossWidthMeters);
            foreach (int spacing in new[] { 2, 4, 64 })
            {
                var (coordinate, sector) = FirstDrySector(session, sampler, spacing);
                hashes[$"gen1_{spacing}m"] = sector.DataHash();
                Detail($"gen1 {spacing} m sector {coordinate} hash {sector.DataHash():x16}");
            }
        }
        // A river sector: the first section of a strip graph, built at 8 m (slot) so the river line payload rides in attr.w.
        recipe = EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1, new[] { "water.rivers" });
        using (VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, recipe))
        {
            RiverTestGraphs.Install(recipe, settings, 9_804_000d, 120, 4);
            using RingTerrainSampler sampler = CreateSampler(recipe, settings);
            var graph = sampler.RuntimeRiverGraph!;
            int longest = 0;
            if (graph.Network.Stems.Length == 0) throw new InvalidOperationException("the strip graph has no river stems");
            for (int stem = 1; stem < graph.Network.Stems.Length; stem++)
                if (graph.Network.Curves[stem].Length > graph.Network.Curves[longest].Length) longest = stem;
            graph.Network.CentreFrame(longest, graph.Network.Curves[longest].Length * 0.5, out double around, out double across, out _, out _);
            const int spacing = 8;
            int size = Cells * spacing;
            var coordinate = new RingTerrainSectorCoordinate((long)Math.Floor(around / size), (int)Math.Floor(across / size));
            var session = RingTerrainProxySector.CreateVertexTableFixtureSession(EmptyEdits(sampler), sampler, sampler.RingAcrossWidthMeters);
            RingTerrainProxySector.VertexTableFixtureSector? sector = BuildSector(session, sampler, coordinate, spacing);
            Check(sector != null, "river sector did not pack");
            if (sector != null)
            {
                hashes["gen1_river_8m"] = sector.DataHash();
                Detail($"gen1 river 8 m sector {coordinate} hash {sector.DataHash():x16}");
            }
        }
        string text = string.Join("\n", hashes.Select(pair => $"{pair.Key}={pair.Value:x16}")) + "\n";
        if (capture)
        {
            System.IO.File.WriteAllText(ProjectSettings.GlobalizePath(GoldenPath), text);
            Detail("golden captured\n" + text);
            return;
        }
        string golden = Godot.FileAccess.FileExists(GoldenPath) ? Godot.FileAccess.GetFileAsString(GoldenPath).Replace("\r\n", "\n") : "";
        Check(golden.Length != 0, "golden file missing; run with --capture before the change");
        if (golden.Length == 0) return;
        foreach (string line in golden.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('=');
            Check(hashes.TryGetValue(parts[0], out ulong actual) && actual.ToString("x16", CultureInfo.InvariantCulture) == parts[1], $"generation 1 sector {parts[0]} differs from the golden");
        }
        Detail("generation 1 hashes: " + string.Join(", ", hashes.Select(pair => $"{pair.Key}={pair.Value:x16}")));
    }

    // ------------------------------------------------------------------ feature world

    private static (double Around, double Across) VertexPosition(RingTerrainSectorCoordinate c, int spacing, int x, int z)
        => ((double)(c.AroundSector * Cells * spacing + (long)x * spacing), (double)(c.AcrossSector * Cells * spacing + z * spacing));

    private void FeatureWorld()
    {
        TerrainGenerationParameters recipe = EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1, new[] { "ground.surface" });
        using VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, recipe);
        using RingTerrainSampler sampler = CreateSampler(recipe, settings);
        Check((sampler.WorldFeatureMask & (1 << RingGroundSurfaceModel.SurfaceBit)) != 0, "the sampler of a ground.surface world does not carry the bit");
        var session = RingTerrainProxySector.CreateVertexTableFixtureSession(EmptyEdits(sampler), sampler, sampler.RingAcrossWidthMeters);
        double HeightAt(double around, double across) => sampler.GetPresentationSurfaceHeight((long)Math.Round(around), (int)Math.Round(across));
        int total = 0;
        double worstModel = 0.0, worstSlot = 0.0;
        foreach (int spacing in new[] { 2, 4, 8, 16, 32, 64 })
        {
            var (coordinate, sector) = FirstDrySector(session, sampler, spacing);
            float[]? relief = sector.Relief;
            Check(relief != null, $"{spacing} m sector of a ground.surface world carries no relief");
            if (relief == null) continue;
            int row = Cells + 1;
            var random = new Random(spacing);
            int rangeCount = 0;
            for (int k = 0; k < 200; k++)
            {
                int x = random.Next(0, Cells + 1), z = random.Next(0, Cells + 1);
                (double around, double across) = VertexPosition(coordinate, spacing, x, z);
                double expected = RingGroundSurfaceModel.Convexity64(HeightAt, around, across);
                double actual = relief[z * row + x];
                worstModel = Math.Max(worstModel, Math.Abs(expected - actual));
                if (Math.Abs(expected) > 0.05) rangeCount++;
                Check(Math.Abs(expected - actual) < 1e-3, $"{spacing} m vertex ({x},{z}): builder {actual:F5} model {expected:F5}");
                total++;
            }
            // Slot bits: terrain lattice texels hold snorm16(C64 / 32); water rows hold 0.
            byte[] attr = sector.Blocks.Attr;
            for (int i = 0; i < row * row; i += 37)
            {
                uint z = BitConverter.ToUInt32(attr, (i * 4 + 2) * 4);
                double slot = RingTerrainProxySector.DecodeReliefSnorm16Public(z >> 16);
                worstSlot = Math.Max(worstSlot, Math.Abs(slot - relief[i]));
                Check(Math.Abs(slot - relief[i]) < 0.0011, $"{spacing} m slot bits at {i}: {slot:F5} vs {relief[i]:F5}");
                uint water = BitConverter.ToUInt32(attr, ((row * row + i) * 4 + 2) * 4);
                Check(water >> 16 == 0, $"{spacing} m water texel {i} has relief bits");
            }
            Detail($"{spacing} m sector {coordinate}: relief range {relief.Take(row * row).Min():F3} .. {relief.Take(row * row).Max():F3} m, {rangeCount}/200 samples above 5 cm");
            // Shared edge: the neighbouring sector's first column equals this sector's last column, and across likewise.
            foreach (bool alongAround in new[] { true, false })
            {
                var other = alongAround ? new RingTerrainSectorCoordinate(coordinate.AroundSector + 1, coordinate.AcrossSector) : new RingTerrainSectorCoordinate(coordinate.AroundSector, coordinate.AcrossSector + 1);
                var neighbour = session2(session, sampler, other, spacing);
                if (neighbour?.Relief == null) { Check(false, $"{spacing} m neighbour sector carries no relief"); continue; }
                for (int t = 0; t <= Cells; t++)
                {
                    float a = alongAround ? relief[t * row + Cells] : relief[Cells * row + t];
                    float n = alongAround ? neighbour.Relief[t * row] : neighbour.Relief[t];
                    Check(a == n, $"{spacing} m shared edge ({(alongAround ? "around" : "across")}) vertex {t}: {a} vs {n}");
                }
            }
        }
        Detail($"feature world: {total} vertices against Convexity64, worst {worstModel:E2} m; worst slot quantisation {worstSlot:E2} m");
        // Generation 1 builder: no relief array at all.
        TerrainGenerationParameters one = EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1);
        using VoxelTerrainSettings oneSettings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, one);
        using RingTerrainSampler oneSampler = CreateSampler(one, oneSettings);
        var oneSession = RingTerrainProxySector.CreateVertexTableFixtureSession(EmptyEdits(oneSampler), oneSampler, oneSampler.RingAcrossWidthMeters);
        Check(FirstDrySector(oneSession, oneSampler, 8).Sector.Relief == null, "a generation 1 sector carries a relief array");
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--cost") >= 0) Cost(oneSession, oneSampler, session, sampler);
    }

    private static RingTerrainProxySector.VertexTableFixtureSector? session2(RingTerrainProxySector.VertexTableFixtureSession session, RingTerrainSampler sampler, RingTerrainSectorCoordinate c, int spacing)
        => BuildSector(session, sampler, c, spacing);

    /// <summary>Cost: the median build time of the same sectors with and without the feature, per level (--cost).</summary>
    private void Cost(RingTerrainProxySector.VertexTableFixtureSession offSession, RingTerrainSampler offSampler, RingTerrainProxySector.VertexTableFixtureSession onSession, RingTerrainSampler onSampler)
    {
        foreach (int spacing in new[] { 2, 4, 8, 16, 32, 64 })
        {
            int size = Cells * spacing;
            var offTimes = new List<double>();
            var onTimes = new List<double>();
            long aroundSectors = (offSampler.AroundRingVoxelLength + size - 1) / size;
            int acrossSectors = (offSampler.RingAcrossWidthMeters + size - 1) / size;
            for (int round = 0; round < 3; round++)
            for (int j = 2; j < 30; j++)
            {
                var c = new RingTerrainSectorCoordinate((long)(((j * 0.6180339887) % 1.0) * aroundSectors), Math.Clamp((int)(((j * 0.7548776662) % 1.0) * acrossSectors), 2, acrossSectors - 3));
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                BuildSector(offSession, offSampler, c, spacing);
                var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                BuildSector(onSession, onSampler, c, spacing);
                var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (round == 0) continue; // warm-up
                offTimes.Add((t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                onTimes.Add((t2 - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            }
            offTimes.Sort(); onTimes.Sort();
            double off = offTimes[offTimes.Count / 2], on = onTimes[onTimes.Count / 2];
            Detail($"COST {spacing} m: median build off {off:F2} ms, on {on:F2} ms, {100.0 * (on - off) / off:+0.0;-0.0}% (n={offTimes.Count})");
        }
    }

    // ------------------------------------------------------------------ synthetic mounds

    private void Mounds()
    {
        Func<double, double, double> convex = (a, c) => 20.0 - 0.0005 * ((a - 3200) * (a - 3200) + (c - 3200) * (c - 3200));
        Func<double, double, double> concave = (a, c) => -convex(a, c);
        double top = RingGroundSurfaceModel.Convexity64(convex, 3200, 3200), bowl = RingGroundSurfaceModel.Convexity64(concave, 3200, 3200);
        Check(top > 0.5 && bowl < -0.5, $"mound C64 signs: convex {top:F3}, concave {bowl:F3}");
        Check(Math.Abs(top + bowl) < 1e-9, "convex and concave mounds are not mirror images");
        Check(Math.Abs(RingGroundSurfaceModel.Convexity64((a, c) => 5.0 + 0.1 * a - 0.2 * c, 1234.5, 987.25)) < 1e-9, "a plane has non-zero C64");
        Detail($"mounds: crest {top:F3} m, bowl {bowl:F3} m");
    }
}
