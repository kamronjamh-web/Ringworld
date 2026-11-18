using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T4-7 (docs/work-orders/terrain-vegetation/T4.md, D11): river bed, bank and dry basin materials behind ground.river_beds. Headless. Run: tools/run_tests.ps1 -Tests river_bed_material_test.
/// Prints RIVER_BED_MATERIAL=PASS. The identity half compares the vertex colours of four river sectors of a rivers-only world (2, 4, 64 m and a lake shore) with a pre-change capture
/// (tools/fixtures/river_bed_precapture_golden.json, written once with --capture before any D11 edit).
/// </summary>
public partial class RiverBedMaterialTest : RefCounted
{
    private const int Seed = 1337;
    private const double StripStart = 9_804_000d;
    private const int StripKm = 400;
    private const int Cells = 64;
    private const string GoldenPath = "res://tools/fixtures/river_bed_precapture_golden.json";

    private readonly List<string> _failures = new();
    private void Fail(string message) { if (_failures.Count < 30) _failures.Add(message); }
    private void Check(bool ok, string message) { if (!ok) Fail(message); }
    private static void Detail(string text) => Console.WriteLine("RIVER_BED_MATERIAL_DETAIL " + text);

    internal sealed record SectorPick(string Label, int Spacing, long AroundSector, int AcrossSector);

    public int Run(bool capture, bool cost = false)
    {
        string scratch = ProjectSettings.GlobalizePath($"res://.godot/river_bed_material_test_{System.Environment.ProcessId}");
        TerrainGenerationParameters riversOnly = EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1, new[] { "water.rivers" });
        TerrainGenerationParameters beds = EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1, new[] { "water.rivers", "ground.river_beds" });
        using VoxelTerrainSettings settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, riversOnly);
        using VoxelTerrainSettings bedSettings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, beds);
        try
        {
            ZoneWeights();
            PlayaBand();
            ApplyIdentity();
            CacheDefinitions();
            if (_failures.Count != 0) { Console.Error.WriteLine("RIVER_BED_MATERIAL=FAIL " + string.Join(" | ", _failures.Take(20))); return 1; }
            RiverTestGraphs.Install(riversOnly, settings, StripStart, StripKm, Math.Clamp(System.Environment.ProcessorCount / 2, 2, 4));
            // The recipes differ only in the feature bit, so the hydrology identity is the same; install the strip graph under the beds recipe's too when it is not.
            ulong bedIdentity = HydrologyBakeIdentity.Compute(beds);
            if (bedIdentity != HydrologyBakeIdentity.Compute(riversOnly)) RiverGraphStore.InstallForTest(bedIdentity, RiverGraphStore.TryGet(HydrologyBakeIdentity.Compute(riversOnly), out RiverGraph? g0) ? g0! : throw new InvalidOperationException("strip graph missing"));
            using var riversSampler = new RingTerrainSampler(settings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
            using var bedSampler = new RingTerrainSampler(bedSettings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
            Check(!riversSampler.HasRiverBeds && bedSampler.HasRiverBeds, "HasRiverBeds is not exactly the ground.river_beds worlds");
            IdentityHashes(riversSampler, capture);
            if (capture) return _failures.Count == 0 ? 0 : 1;
            RealSamples(riversSampler, bedSampler, beds);
            Basins(riversSampler, bedSampler);
            CacheRecords(riversSampler, bedSampler, scratch);
            if (cost) CostProbe(bedSampler, beds);
        }
        catch (Exception e) { Console.Error.WriteLine("RIVER_BED_MATERIAL=FAIL exception " + e); return 1; }
        finally
        {
            RiverGraphStore.Clear();
            try { if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, true); } catch (IOException) { }
        }
        if (_failures.Count != 0) { Console.Error.WriteLine("RIVER_BED_MATERIAL=FAIL " + string.Join(" | ", _failures.Take(20))); return 1; }
        Console.WriteLine("RIVER_BED_MATERIAL=PASS");
        return 0;
    }

    // ------------------------------------------------------------------ zone weights at fabricated inputs

    private const double Tol = 0.006;
    private static bool Near(double a, double b, double tol = Tol) => Math.Abs(a - b) <= tol;

    /// <summary>The quantised result of a river or lake part: cover, sand (Desert), gravel (Alpine) and tundra (the rest).</summary>
    private static (double Cover, double Sand, double Gravel, double Tundra) Zone(InlandWaterKind kind, double dz, double g, double fp)
    {
        var u = RiverBedSurfaceModel.Unpack(RiverBedSurfaceModel.Pack(RiverBedSurfaceModel.RiverPart(kind, dz, g, fp), 0d));
        return (u.RiverCover, u.Sand, u.Gravel, u.Tundra);
    }

    private void ZoneWeights()
    {
        const InlandWaterKind R = InlandWaterKind.River, L = InlandWaterKind.Lake;
        void Expect(string what, (double Cover, double Sand, double Gravel, double Tundra) z, double cover, double sand, double gravel, double tundra)
            => Check(Near(z.Cover, cover) && (cover == 0d || (Near(z.Sand, sand) && Near(z.Gravel, gravel) && Near(z.Tundra, tundra))),
                $"{what}: cover {z.Cover:F3} D {z.Sand:F3} A {z.Gravel:F3} T {z.Tundra:F3}, want cover {cover:F3} D {sand:F3} A {gravel:F3} T {tundra:F3}");
        // The coarseness ramp: 0 at and below 0.6 m/s, 1 at and above 1.4, monotonic between.
        Check(RiverBedSurfaceModel.Coarseness(0.6) == 0d && RiverBedSurfaceModel.Coarseness(0.1) == 0d && RiverBedSurfaceModel.Coarseness(1.4) == 1d && RiverBedSurfaceModel.Coarseness(9d) == 1d, "Coarseness endpoints");
        Check(Near(RiverBedSurfaceModel.Coarseness(1.0), 0.5, 1e-9), "Coarseness(1.0) is not the ramp midpoint");
        // River bed: calm gives wet sand (0.55 D + 0.45 T), fast gives wet gravel (0.60 A + 0.40 T).
        Expect("river calm bed", Zone(R, -1d, 0d, 0d), 1d, 0.55, 0d, 0.45);
        Expect("river fast bed", Zone(R, -1d, 1d, 0d), 1d, 0d, 0.60, 0.40);
        Expect("river mid bed", Zone(R, -1d, 0.5, 0d), 1d, 0.275, 0.30, 0.425);
        // The bar (wet margin) at dz 0.1 is still the bed's material; the bank at dz 0.2 is mud (calm) or gravel (fast).
        Expect("river calm bar dz 0.1", Zone(R, 0.1, 0d, 0d), 1d, 0.55, 0d, 0.45);
        Expect("river fast bar dz 0.1", Zone(R, 0.1, 1d, 0d), 1d, 0d, 0.60, 0.40);
        Expect("river calm bank dz 0.2", Zone(R, 0.2, 0d, 0d), 1d, 0d, 0d, 1d);
        Expect("river fast bank dz 0.2", Zone(R, 0.2, 1d, 0d), 1d, 0d, 1d, 0d);
        Expect("river calm half bank dz 0.15", Zone(R, 0.15, 0d, 0d), 1d, 0.275, 0d, 0.725);
        // Cover: 1 up to dz 0.25, half way at 0.315, 0 from 0.38 (inside the 0.4 m bank top).
        Check(Zone(R, 0.25, 0d, 0d).Cover >= 0.999 && Zone(R, 0.0, 0d, 0d).Cover >= 0.999, "river cover is below 1 under dz 0.25");
        Check(Near(Zone(R, 0.315, 0d, 0d).Cover, 0.5), "river cover at dz 0.315 is not 0.5");
        Check(Zone(R, 0.38, 0d, 0d).Cover == 0d && Zone(R, 0.5, 1d, 0d).Cover == 0d && Zone(R, 3d, 0d, 0d).Cover == 0d, "river cover is not 0 at dz >= 0.38");
        // Lake: sandy shallows, silt at 4 m, a sand shore fading out by dz 0.25.
        Expect("lake shallow bed (depth 0.5)", Zone(L, -0.5, 0d, 0d), 1d, 0.55, 0d, 0.45);
        Expect("lake bed at depth 1", Zone(L, -1d, 0d, 0d), 1d, 0.55, 0d, 0.45);
        Expect("lake mid depth 2.5", Zone(L, -2.5, 0d, 0d), 1d, 0.275, 0d, 0.725);
        Expect("lake bed at depth 4 (silt)", Zone(L, -4d, 0d, 0d), 1d, 0d, 0d, 1d);
        Expect("lake bed at depth 30 (silt)", Zone(L, -30d, 0d, 0d), 1d, 0d, 0d, 1d);
        Expect("lake shore dz 0.05", Zone(L, 0.05, 0d, 0d), 1d, 0.55, 0d, 0.45);
        Check(Near(Zone(L, 0.175, 0d, 0d).Cover, 0.5), "lake shore cover at dz 0.175 is not 0.5");
        Check(Zone(L, 0.25, 0d, 0d).Cover == 0d && Zone(L, 1d, 0d, 0d).Cover == 0d, "lake shore cover is not 0 at dz >= 0.25");
        // Footprint above 4 m: bed only, no bar, bank or shore. At 4 m the margins stay (the rule is fp > 4).
        foreach (double fp in new[] { 8d, 16d, 256d, double.PositiveInfinity })
        {
            Check(Zone(R, 0.1, 0d, fp).Cover == 0d && Zone(R, 0.2, 1d, fp).Cover == 0d && Zone(R, 0.0, 0d, fp).Cover == 0d, $"fp {fp}: a river bar, bank or the waterline kept cover");
            Check(Zone(L, 0.1, 0d, fp).Cover == 0d && Zone(L, 0.0, 0d, fp).Cover == 0d, $"fp {fp}: a lake shore kept cover");
            Expect($"fp {fp} river bed", Zone(R, -0.5, 0d, fp), 1d, 0.55, 0d, 0.45);
            Expect($"fp {fp} lake bed", Zone(L, -0.5, 0d, fp), 1d, 0.55, 0d, 0.45);
            Check(Near(Zone(R, -0.05, 0d, fp).Cover, 0.5, 0.02) || fp <= 4d, $"fp {fp}: the waterline fade is missing");
        }
        Check(Zone(R, 0.1, 0d, 4d).Cover == 1d && Zone(L, 0.05, 0d, 4d).Cover == 1d && Zone(R, 0.1, 0d, 0d).Cover == 1d, "the 4 m level lost its bar or shore");
        Check(Zone(R, 0.1, 0d, 4.5).Cover == 0d, "fp 4.5 kept the bar");
        Check(RiverBedSurfaceModel.RiverPart(InlandWaterKind.None, -1d, 0d, 0d).Cover == 0d, "no owner gave cover");
        // Pack: a part that rounds to no cover is the default mask; a uint round trip keeps all four bytes.
        Check(RiverBedSurfaceModel.Pack(new RiverBedPart(0.001, 0.5, 0.5), 0d).IsDefault, "a part with no cover is not the default mask");
        var mask = new RiverBedMask(200, 140, 30, 77);
        Check(RiverBedMask.FromPacked(mask.Packed).Equals(mask) && default(RiverBedMask).Packed == 0u, "the packed uint does not round-trip");
    }

    private void PlayaBand()
    {
        double Cover(double e, double depth, double moisture = 0.1, double p = 1d) => RiverBedSurfaceModel.PlayaCover(100d + e, 100d, 100d + depth, moisture, p);
        // D = 20: top 8, full 3.2.
        Check(Cover(-5d, 20d) == 1d && Cover(0d, 20d) == 1d && Near(Cover(3.2, 20d), 1d, 1e-9), "playa is not full below 0.4 top");
        Check(Cover(8d, 20d) == 0d && Cover(8.5, 20d) == 0d && Cover(50d, 20d) == 0d, "playa is not 0 above top");
        Check(Near(Cover(5.6, 20d), 0.5, 1e-9), "playa is not half way between full and top");
        // Never above half the basin depth, however deep the basin is.
        for (double depth = 1d; depth <= 100d; depth *= 1.7)
            for (double e = 0d; e <= 120d; e += 0.05)
                if (Cover(e, depth) > 0d && e >= Math.Min(8d, depth / 2d) + 1e-9) { Fail($"playa above top: depth {depth:F2} e {e:F2}"); depth = 1e9; break; }
        Check(Cover(0d, 0d) == 0d && Cover(0d, -3d) == 0d, "a basin without depth has playa");
        Check(Cover(2.9, 6d) > 0d && Cover(3.0, 6d) == 0d && Cover(3.1, 6d) == 0d, "a 6 m basin's playa does not end at half its depth");
        // Aridity: 1 up to moisture 0.30, 0 from 0.45.
        Check(Cover(0d, 20d, 0.30) == 1d && Cover(0d, 20d, 0.45) == 0d && Cover(0d, 20d, 0.9) == 0d && Near(Cover(0d, 20d, 0.375), 0.5, 1e-9), "playa aridity ramp");
        // Membership: 0 on a member / non-member cell edge (P = 0), half at 0.25, full from 0.5, none below 0.
        Check(Cover(0d, 20d, 0.1, 0d) == 0d && Cover(0d, 20d, 0.1, -1d) == 0d && Cover(0d, 20d, 0.1, -0.2) == 0d, "playa at P <= 0");
        Check(Near(Cover(0d, 20d, 0.1, 0.25), 0.5, 1e-9) && Cover(0d, 20d, 0.1, 0.5) == 1d && Cover(0d, 20d, 0.1, 1d) == 1d, "playa membership ramp");
    }

    private void ApplyIdentity()
    {
        static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);
        var rng = new Random(4217);
        int bad = 0;
        for (int i = 0; i < 4000; i++)
        {
            float d = (float)rng.NextDouble() * 0.5f, f = (float)rng.NextDouble() * 0.3f, t = (float)rng.NextDouble() * 0.15f, a = (float)rng.NextDouble() * 0.05f;
            if (i % 7 == 0) { d = f = t = a = 0f; }
            var w = new RingTerrainSampler.BiomeTextureWeights(d, f, t, a);
            RingTerrainSampler.BiomeTextureWeights r = RiverBedSurfaceModel.Apply(w, default);
            Color c = RiverBedSurfaceModel.Apply(new Color(d, f, t, a), default);
            if (Bits(r.Desert) != Bits(d) || Bits(r.Forest) != Bits(f) || Bits(r.Tundra) != Bits(t) || Bits(r.Alpine) != Bits(a)
                || Bits(c.R) != Bits(d) || Bits(c.G) != Bits(f) || Bits(c.B) != Bits(t) || Bits(c.A) != Bits(a)) bad++;
            // A mask with only target bytes and no cover is also the identity.
            if (!RiverBedSurfaceModel.Apply(w, new RiverBedMask(0, 200, 50, 0)).Equals(w)) bad++;
        }
        Check(bad == 0, $"Apply with a zero-cover mask is not the identity ({bad} of 8000)");
        // Full cover lands on the targets; the playa goes first, the river on top.
        RingTerrainSampler.BiomeTextureWeights grass = default;
        RingTerrainSampler.BiomeTextureWeights playa = RiverBedSurfaceModel.Apply(new RingTerrainSampler.BiomeTextureWeights(0.1f, 0.5f, 0f, 0f), new RiverBedMask(0, 0, 0, 255));
        Check(Near(playa.Desert, 0.45, 1e-6) && Near(playa.Forest, 0, 1e-6) && Near(playa.Tundra, 0, 1e-6) && Near(playa.Alpine, 0.55, 1e-6), $"full playa is not 0.45 D + 0.55 A: {playa}");
        RingTerrainSampler.BiomeTextureWeights sand = RiverBedSurfaceModel.Apply(new RingTerrainSampler.BiomeTextureWeights(0.1f, 0.5f, 0f, 0f), new RiverBedMask(255, 140, 0, 255));
        Check(Near(sand.Desert, 140 / 255.0, 1e-6) && Near(sand.Forest, 0, 1e-6) && Near(sand.Tundra, 1 - 140 / 255.0, 1e-6) && Near(sand.Alpine, 0, 1e-6), $"full river cover over a full playa is not the river target: {sand}");
        RingTerrainSampler.BiomeTextureWeights half = RiverBedSurfaceModel.Apply(grass, new RiverBedMask(0, 0, 0, 128));
        Check(Near(half.Desert, 0.45 * 128 / 255.0, 1e-6) && Near(half.Alpine, 0.55 * 128 / 255.0, 1e-6), "half playa over grass is not the half mix");
    }

    private void CacheDefinitions()
    {
        string v11 = "v11|g1|s2|h0000000000000003|a4096|w1024|z1024|p16|q32|m0000000000000000|n64";
        string v13 = v11.Replace("v11|", "v13|");
        Check(RingTerrainProxySector.CacheDefinitionForTest(false, false) == v11, "the generation-1 cache definition changed: " + RingTerrainProxySector.CacheDefinitionForTest(false, false));
        Check(RingTerrainProxySector.CacheDefinitionForTest(true, false) == v13, "the rivers-only cache definition changed: " + RingTerrainProxySector.CacheDefinitionForTest(true, false));
        string v14 = RingTerrainProxySector.CacheDefinitionForTest(true, true);
        Check(v14 == v11.Replace("v11|", "v14|") + "|rb" + RiverBedSurfaceModel.Version, "the river-beds cache definition is not v14 plus the model version: " + v14);
        Check(v14 != v13 && v14 != v11, "the river-beds definition collides with an older one");
    }

    // ------------------------------------------------------------------ identity: vertex colours of four sectors of a rivers-only world

    private static RingLodSurfaceEditSnapshot EmptyEdits(RingTerrainSampler sampler) => new(0,
        sampler.AroundRingVoxelLength, new Dictionary<RingTerrainColumnCoordinate, int>(),
        new Dictionary<RingTerrainColumnCoordinate, float>(), sampler.UsesRuntimeSurfaceGeneration);

    /// <summary>The four sectors of the identity capture, chosen from the graph by hash order so the choice is stable: a river of 6 to 16 m at the 2 m level, 16 to 40 m at 4 m, over 40 m at 64 m, and the left edge of a lake at 4 m.</summary>
    internal static List<SectorPick> PickSectors(RingTerrainSampler sampler)
    {
        RiverGraph graph = sampler.RuntimeRiverGraph!;
        RiverNetwork net = graph.Network;
        double aroundMin = StripStart + 20_000d, aroundMax = StripStart + StripKm * 1000d - 20_000d;
        var candidates = new List<(int Stem, double S)>();
        for (int stem = 0; stem < net.Stems.Length; stem++)
            for (double s = 300d; s < net.Curves[stem].Length - 300d; s += 250d) candidates.Add((stem, s));
        var picks = new List<SectorPick>();
        (string Label, int Spacing, double MinWidth, double MaxWidth)[] classes =
        {
            ("river_2m", 2, 6d, 16d), ("river_4m", 4, 16d, 40d), ("river_64m", 64, 40d, 1e9),
        };
        foreach ((string label, int spacing, double minWidth, double maxWidth) in classes)
        {
            foreach ((int stem, double s) in candidates.OrderBy(c => RiverHash.Mix(Seed, c.Stem, (long)(c.S / 250d) + 31)))
            {
                net.CentreFrame(stem, s, out double around, out double across, out _, out _);
                net.ChannelAt(stem, s, out RiverChannel channel);
                double width = 2d * channel.HalfWidth;
                if (width < minWidth || width >= maxWidth) continue;
                if (around < aroundMin || around > aroundMax || across < 5000d || across > net.WidthM - 5000d) continue;
                int size = Cells * spacing;
                picks.Add(new SectorPick(label, spacing, (long)Math.Floor(around / size), (int)Math.Floor(across / size)));
                break;
            }
        }
        double stepA = graph.AroundStepMeters, stepZ = graph.AcrossStepMeters;
        // A lake of at least 8 cells, away from the strip ends and the rims, whose first flooded cell really is wet at its centre.
        foreach (int id in Enumerable.Range(0, graph.Lakes.Length).OrderBy(i => RiverHash.Mix(Seed, i, 92)))
        {
            RiverLake lake = graph.Lakes[id];
            double a = (lake.MinCol + 0.5) * stepA, z = (lake.MinRow + 0.5) * stepZ;
            if (lake.CellCount < 8 || a < aroundMin + 40_000d || a > aroundMax - 40_000d || lake.MinRow < 8 || lake.MaxRow > graph.Rows - 8 || z < 5000d) continue;
            LakeRun first = default; bool any = false;
            foreach (LakeRun run in graph.Runs)
                if (run.LakeId == id && (!any || run.Col < first.Col || (run.Col == first.Col && run.Row < first.Row))) { first = run; any = true; }
            if (!any) continue;
            // The shore: walk left from the flooded run's first cell centre along its row in 4 m steps and take the last wet sample before dry ground.
            double across = (first.Row + 0.5) * stepZ, centre = (first.Col + 0.5) * stepA, around = centre;
            if (sampler.GetTerrainSample((long)centre, (int)across).WaterKind != SurfaceWaterKind.Lake) continue;
            for (double x = centre; x > centre - 2d * stepA; x -= 4d)
            {
                if (sampler.GetTerrainSample((long)x, (int)across).WaterKind != SurfaceWaterKind.Lake) break;
                around = x;
            }
            picks.Add(new SectorPick("lake_shore_4m", 4, (long)Math.Floor(around / (Cells * 4)), (int)Math.Floor(across / (Cells * 4))));
            break;
        }
        return picks;
    }

    /// <summary>FNV-1a over the float bits of every vertex colour.</summary>
    internal static string HashColors(Color[] colors)
    {
        ulong h = 14695981039346656037UL;
        void Mix(uint v) { unchecked { for (int i = 0; i < 4; i++) { h ^= (v >> (8 * i)) & 0xFFu; h *= 1099511628211UL; } } }
        foreach (Color c in colors)
        {
            Mix(BitConverter.SingleToUInt32Bits(c.R)); Mix(BitConverter.SingleToUInt32Bits(c.G));
            Mix(BitConverter.SingleToUInt32Bits(c.B)); Mix(BitConverter.SingleToUInt32Bits(c.A));
        }
        return h.ToString("X16", CultureInfo.InvariantCulture);
    }

    internal static List<(SectorPick Pick, int Vertices, string Hash)> BuildHashes(RingTerrainSampler sampler)
    {
        var session = RingTerrainProxySector.CreateRiverWidthFixtureSession(EmptyEdits(sampler), sampler, sampler.RingAcrossWidthMeters);
        double radius = EmitterSiteTestWorlds.Circumference / Math.Tau;
        var rows = new List<(SectorPick, int, string)>();
        foreach (SectorPick pick in PickSectors(sampler))
        {
            RingTerrainProxySector.RiverWidthFixtureSector sector = session.Build(new RingTerrainSectorCoordinate(pick.AroundSector, pick.AcrossSector), pick.Spacing, Cells, radius);
            rows.Add((pick, sector.Colors.Length, HashColors(sector.Colors)));
        }
        return rows;
    }

    private void IdentityHashes(RingTerrainSampler riversOnly, bool capture)
    {
        List<(SectorPick Pick, int Vertices, string Hash)> rows = BuildHashes(riversOnly);
        RiverGraph graph = riversOnly.RuntimeRiverGraph!;
        Detail($"strip graph: {graph.Network.Stems.Length} stems, {graph.Lakes.Length} lakes, {graph.Runs.Length} lake runs, {graph.Basins.Length} basins, {graph.DryRuns.Length} dry runs");
        Check(rows.Count == 4, $"the identity capture found {rows.Count} of 4 sectors");
        if (rows.Count != 4) return;
        string path = ProjectSettings.GlobalizePath(GoldenPath);
        var text = new StringBuilder("{\n  \"sectors\": [\n");
        for (int i = 0; i < rows.Count; i++)
        {
            (SectorPick p, int vertices, string hash) = rows[i];
            text.Append(CultureInfo.InvariantCulture, $"    {{ \"label\": \"{p.Label}\", \"spacing\": {p.Spacing}, \"around_sector\": {p.AroundSector}, \"across_sector\": {p.AcrossSector}, \"vertices\": {vertices}, \"color_hash\": \"{hash}\" }}{(i + 1 < rows.Count ? "," : "")}\n");
        }
        text.Append("  ]\n}\n");
        if (capture)
        {
            if (File.Exists(path)) { Fail("the golden exists; refusing to overwrite " + path); return; }
            File.WriteAllText(path, text.ToString());
            Detail("captured " + path);
            foreach (var row in rows) Detail($"{row.Pick.Label} {row.Pick.Spacing} m sector ({row.Pick.AroundSector},{row.Pick.AcrossSector}) vertices {row.Vertices} hash {row.Hash}");
            return;
        }
        if (!File.Exists(path)) { Fail("the pre-capture golden is missing: " + path); return; }
        string golden = File.ReadAllText(path).Replace("\r\n", "\n");
        Check(golden == text.ToString(), "the vertex-colour hashes of the four river sectors differ from the pre-change capture:\n" + text + "golden:\n" + golden);
        foreach (var row in rows) Detail($"{row.Pick.Label} {row.Pick.Spacing} m sector ({row.Pick.AroundSector},{row.Pick.AcrossSector}) vertices {row.Vertices} hash {row.Hash}");
        foreach (var row in rows)
        {
            (int river, int lake) = WetSamples(riversOnly, row.Pick);
            Detail($"{row.Pick.Label}: {river} River and {lake} Lake samples on a 16 x 16 grid");
            Check(river + lake > 0, $"{row.Pick.Label}: the sector holds no river or lake water, so the identity check there proves nothing");
        }
    }

    private static (int River, int Lake) WetSamples(RingTerrainSampler sampler, SectorPick pick)
    {
        int size = Cells * pick.Spacing, river = 0, lake = 0;
        for (int j = 0; j < 16; j++)
            for (int i = 0; i < 16; i++)
            {
                TerrainSample s = sampler.GetTerrainSample(pick.AroundSector * (long)size + i * size / 16 + size / 32, Math.Clamp(pick.AcrossSector * size + j * size / 16 + size / 32, 0, sampler.RingAcrossWidthMeters));
                if (s.WaterKind == SurfaceWaterKind.River) river++; else if (s.WaterKind == SurfaceWaterKind.Lake) lake++;
            }
        return (river, lake);
    }

    // ------------------------------------------------------------------ real samples of a strip graph

    /// <summary>A 64 x 64 lattice over a sector at the pick's spacing (even coordinates, so the lattice samples are the fused ones).</summary>
    private static IEnumerable<(long A, int Z)> Grid(SectorPick pick)
    {
        int size = Cells * pick.Spacing;
        long a0 = pick.AroundSector * (long)size;
        int z0 = pick.AcrossSector * size;
        for (int j = 0; j < Cells; j++)
            for (int i = 0; i < Cells; i++) yield return (a0 + (long)i * pick.Spacing, z0 + j * pick.Spacing);
    }

    private static bool BitsEqual(RingTerrainSampler.BiomeTextureWeights a, RingTerrainSampler.BiomeTextureWeights b)
        => BitConverter.SingleToUInt32Bits(a.Desert) == BitConverter.SingleToUInt32Bits(b.Desert) && BitConverter.SingleToUInt32Bits(a.Forest) == BitConverter.SingleToUInt32Bits(b.Forest)
        && BitConverter.SingleToUInt32Bits(a.Tundra) == BitConverter.SingleToUInt32Bits(b.Tundra) && BitConverter.SingleToUInt32Bits(a.Alpine) == BitConverter.SingleToUInt32Bits(b.Alpine);

    private void RealSamples(RingTerrainSampler rivers, RingTerrainSampler beds, TerrainGenerationParameters bedsRecipe)
    {
        using var model = new TerrainGenerationHeightModel(bedsRecipe);
        RiverGraph graph = beds.RuntimeRiverGraph!;
        var points = new List<(long A, int Z, string Label)>();
        foreach (SectorPick pick in PickSectors(rivers))
            if (pick.Spacing <= 4) foreach ((long a, int z) in Grid(pick)) points.Add((a, z, pick.Label));
        int total = 0, unowned = 0, wetRiver = 0, wetLake = 0, dryMasked = 0, owned = 0, nonDefault = 0, fp8Checked = 0, farChecked = 0, heightOnlyChecked = 0, equalWeights = 0;
        foreach ((long a, int z, string label) in points)
        {
            rivers.GetRuntimeTerrainSampleAndPresentationHeight(a, z, 0d, out TerrainSample sr, out _);
            beds.GetRuntimeTerrainSampleAndPresentationHeight(a, z, 0d, out TerrainSample sb, out float hb);
            TerrainGenerationHeightModel.Diagnostics d = model.SampleDiagnostics(a, z, 0d);
            total++;
            string where = $"{label} ({a},{z})";
            // Bit 3 off: the default mask on every sample, and the old weights (pure sand under River and Lake water).
            Check(sr.RiverBeds.IsDefault, $"{where}: a rivers-only world filled a mask");
            RingTerrainSampler.BiomeTextureWeights wr = rivers.GetBiomeTextureWeights(sr);
            if (sr.WaterKind is SurfaceWaterKind.River or SurfaceWaterKind.Lake)
                Check(BitsEqual(wr, new RingTerrainSampler.BiomeTextureWeights(1f, 0f, 0f, 0f)), $"{where}: a rivers-only wet sample is not pure sand");
            // The feature changes nothing but the mask.
            Check((sb with { RiverBeds = default }).Equals(sr), $"{where}: a river-beds sample differs from the rivers-only sample beyond the mask");
            if (sb.RiverBeds.IsDefault)
            {
                Check(BitsEqual(beds.GetBiomeTextureWeights(sb), wr), $"{where}: a sample with the default mask has other weights than the rivers-only world");
                equalWeights++;
            }
            bool ownedHere = d.InlandWater != InlandWaterKind.None && double.IsFinite(d.WaterLevel);
            if (ownedHere) owned++;
            if (!ownedHere && d.DryBasin < 0) { Check(sb.RiverBeds.IsDefault, $"{where}: a sample with no owner and no basin got a mask"); unowned++; }
            // The mask plumbing: the sampler's mask is the builder's at the same diagnostics, height, moisture and footprint.
            Check(beds.CreateRiverBedMaskLocked(a, z, d, hb, sb.Moisture, 0d).Equals(sb.RiverBeds), $"{where}: the sample mask is not the builder's");
            if (sb.RiverBeds.IsDefault) continue;
            nonDefault++;
            double dz = ownedHere ? hb - d.WaterLevel : double.NaN;
            if (sb.WaterKind == SurfaceWaterKind.River) { wetRiver++; Check(sb.RiverBeds.RiverCover == 255, $"{where}: a wet river sample has cover {sb.RiverBeds.RiverCover}"); }
            else if (sb.WaterKind == SurfaceWaterKind.Lake) { wetLake++; Check(sb.RiverBeds.RiverCover == 255, $"{where}: a wet lake sample has cover {sb.RiverBeds.RiverCover}"); }
            else if (sb.RiverBeds.RiverCover != 0) dryMasked++;
            if (ownedHere && dz >= 0.38) Check(sb.RiverBeds.RiverCover == 0, $"{where}: cover {sb.RiverBeds.RiverCover} at dz {dz:F3}");
            if (!ownedHere) Check(sb.RiverBeds.RiverCover == 0, $"{where}: cover without an owner");
            // Far tier: no river cover, the playa part stays; River becomes None as before.
            TerrainSample far = beds.WithoutFarRiverOwner(sb);
            Check(far.RiverBeds.RiverCover == 0 && far.RiverBeds.PlayaCover == sb.RiverBeds.PlayaCover, $"{where}: WithoutFarRiverOwner did not clear exactly the river cover");
            Check(sb.WaterKind != SurfaceWaterKind.River || far.WaterKind == SurfaceWaterKind.None, $"{where}: WithoutFarRiverOwner kept the River owner");
            farChecked++;
        }
        Check(wetRiver > 0 && wetLake == 0 || wetLake > 0, "the picked sectors hold no wet sample, so the mask checks prove nothing");
        Check(dryMasked > 0, "no dry bar, bank or shore sample was masked in the picked sectors");
        // Margins only where the level resolves them: at footprint 8 a dry sample has no river cover. (The owner at that level is the 8 m level's.)
        foreach ((long a, int z, string label) in points.Where(p => p.Label == "river_2m").Where((_, i) => i % 3 == 0))
        {
            beds.GetRuntimeTerrainSampleAndPresentationHeight(a, z, 8d, out TerrainSample s8, out _);
            fp8Checked++;
            if (s8.WaterKind == SurfaceWaterKind.None) Check(s8.RiverBeds.RiverCover == 0, $"fp 8 ({a},{z}): a dry sample has river cover {s8.RiverBeds.RiverCover}");
        }
        // Height-only samples keep the default mask (they never read climate and feed halos, normals and shorelines).
        foreach ((long a, int z, string label) in points.Where((_, i) => i % 97 == 0))
        {
            beds.GetRuntimeHeightOnlyTerrainSampleAndPresentationHeight(a, z, out TerrainSample ho, out _);
            heightOnlyChecked++;
            Check(ho.RiverBeds.IsDefault, $"height-only ({a},{z}) carries a mask");
        }
        // An edit keeps the unedited mask: the digging does not move the material.
        foreach ((long a, int z, string label) in points.Where(p => p.Label == "river_2m").Where((_, i) => i % 211 == 0))
        {
            TerrainSample plain = beds.GetTerrainSample(a, z);
            var edits = new RingLodSurfaceEditSnapshot(1, EmitterSiteTestWorlds.Circumference, new Dictionary<RingTerrainColumnCoordinate, int>(),
                new Dictionary<RingTerrainColumnCoordinate, float> { [new RingTerrainColumnCoordinate(a, z)] = plain.SurfaceHeight + 3f }, true);
            Check(edits.GetPresentationTerrainSample(beds, a, z).RiverBeds.Equals(plain.RiverBeds), $"edit ({a},{z}) moved the mask");
        }
        // Bit 3 off keeps WithoutFarRiverOwner's old behaviour exactly.
        foreach ((long a, int z, string label) in points.Where((_, i) => i % 53 == 0))
        {
            TerrainSample sr = rivers.GetTerrainSample(a, z);
            TerrainSample expected = sr.WaterKind == SurfaceWaterKind.River ? sr with { WaterKind = SurfaceWaterKind.None, WaterSurfaceHeight = sr.SurfaceHeight, ExactWaterLevel = 0f, HasExactWaterLevel = false } : sr;
            Check(rivers.WithoutFarRiverOwner(sr).Equals(expected), $"({a},{z}): WithoutFarRiverOwner changed in a rivers-only world");
        }
        // A fabricated sample: the far tier clears the river cover and keeps the playa cover and the targets' bytes untouched.
        var fab = new TerrainSample(5, TerrainRegion.Lowlands, 0f, 0f, 0f, 0f, 0f, TerrainBiomeId.Desert, SurfaceWaterKind.None, 0) with { RiverBeds = new RiverBedMask(200, 140, 30, 77) };
        TerrainSample fabFar = beds.WithoutFarRiverOwner(fab);
        Check(fabFar.RiverBeds.RiverCover == 0 && fabFar.RiverBeds.PlayaCover == 77, "WithoutFarRiverOwner on a fabricated sample");
        Check(rivers.WithoutFarRiverOwner(fab).RiverBeds.Equals(fab.RiverBeds), "a rivers-only world's far tier touched a fabricated mask");
        Detail($"real samples: {total} points, {owned} owned, {unowned} with no owner and no basin, {nonDefault} masked ({wetRiver} wet river, {wetLake} wet lake, {dryMasked} dry bar/bank/shore), default-mask weights equal on {equalWeights}, fp 8 checked {fp8Checked}, far {farChecked}, height-only {heightOnlyChecked}");
        // Generation 1 and every world without the feature: no mask, no closed-basin routing.
        using VoxelTerrainSettings gen1Settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1));
        using var gen1 = new RingTerrainSampler(gen1Settings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
        Check(!gen1.HasRiverBeds && !gen1.IntersectsClosedBasin(0, EmitterSiteTestWorlds.Circumference, 0, EmitterSiteTestWorlds.Width), "a generation-1 sampler reports river beds or a closed basin");
        int gen1Masked = 0;
        for (int i = 0; i < 400; i++) if (!gen1.GetTerrainSample(9_804_000L + i * 997L, 50_000 + i * 131).RiverBeds.IsDefault) gen1Masked++;
        Check(gen1Masked == 0, $"generation 1 gave {gen1Masked} masked samples");
    }

    // ------------------------------------------------------------------ closed basins: membership, playa, routing

    private void Basins(RingTerrainSampler rivers, RingTerrainSampler beds)
    {
        RiverGraph graph = beds.RuntimeRiverGraph!;
        double stepA = graph.AroundStepMeters, stepZ = graph.AcrossStepMeters;
        double aroundMin = StripStart + 40_000d, aroundMax = StripStart + StripKm * 1000d - 40_000d;
        using var model = new TerrainGenerationHeightModel(EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1, new[] { "water.rivers", "ground.river_beds" }));
        Func<int, int, int> member = (col, row) => RiverBasinOf(graph, col, row);
        var edgesChecked = 0; var centresChecked = 0; int playaSamples = 0, floorSamples = 0, outsideSamples = 0, edgeSamples = 0;
        (RingTerrainSectorCoordinate Coordinate, int Col, int Row)? routable = null;
        int routingCandidates = 0;
        for (int k = 0; k < graph.DryRuns.Length; k++)
        {
            LakeRun run = graph.DryRuns[k];
            double a0 = (run.Col + 0.5) * stepA;
            if (a0 < aroundMin || a0 > aroundMax || run.Row < 8 || run.Row > graph.Rows - 8) continue;
            int basin = run.LakeId;
            // Membership at the first cell's centre (+1), at the non-member neighbour's centre (-1) and on the edge between them (0).
            int left = (run.Col - 1 + graph.RingColumns) % graph.RingColumns;
            if (member(left, run.Row) != basin)
            {
                double ca = (run.Col + 0.5) * stepA, cz = (run.Row + 0.5) * stepZ;
                Check(beds.BasinMembershipLocked(graph, basin, ca, cz) > 1d - 1e-6, $"basin {basin} cell ({run.Col},{run.Row}): P at a member centre is {beds.BasinMembershipLocked(graph, basin, ca, cz):F6}");
                Check(beds.BasinMembershipLocked(graph, basin, (left + 0.5) * stepA, cz) < -1d + 1e-6, $"basin {basin}: P at the non-member neighbour's centre is not -1");
                Check(Math.Abs(beds.BasinMembershipLocked(graph, basin, run.Col * stepA, cz)) < 1e-6, $"basin {basin} cell ({run.Col},{run.Row}): P on the member / non-member edge is {beds.BasinMembershipLocked(graph, basin, run.Col * stepA, cz):F9}, want 0");
                edgesChecked++; centresChecked += 2;
                // A lattice sample on the edge (the nearest even metre): no playa to speak of (P is a few thousandths there).
                long ea = (long)Math.Round(run.Col * stepA / 2d) * 2L; int ez = (int)Math.Round(cz / 2d) * 2;
                beds.GetRuntimeTerrainSampleAndPresentationHeight(ea, ez, 0d, out TerrainSample edge, out _);
                Check(edge.RiverBeds.PlayaCover <= 1, $"basin {basin}: playa cover {edge.RiverBeds.PlayaCover} on a cell edge");
                edgeSamples++;
            }
            // Samples in the first cell: 3 x 3 inside it.
            if (k % 4 == 0)
                for (int j = 0; j < 3; j++)
                    for (int i = 0; i < 3; i++)
                    {
                        long sa = (long)Math.Round((run.Col + 0.15 + 0.35 * i) * stepA / 2d) * 2L; int sz = (int)Math.Round((run.Row + 0.15 + 0.35 * j) * stepZ / 2d) * 2;
                        beds.GetRuntimeTerrainSampleAndPresentationHeight(sa, sz, 0d, out TerrainSample s, out float h);
                        TerrainGenerationHeightModel.Diagnostics d = model.SampleDiagnostics(sa, sz, 0d);
                        floorSamples++;
                        if (s.RiverBeds.PlayaCover > 0) playaSamples++;
                        if (d.DryBasin < 0 && d.InlandWater != InlandWaterKind.Lake) { outsideSamples++; Check(s.RiverBeds.PlayaCover == 0, $"({sa},{sz}): playa outside every member cell ({s.RiverBeds.PlayaCover})"); }
                    }
            // Routing candidate: a dry cell whose 16 m sector the river envelope does not already keep on the CPU.
            if (routable == null)
            {
                var coordinate = new RingTerrainSectorCoordinate((long)Math.Floor(a0 / 1024d), (int)Math.Floor((run.Row + 0.5) * stepZ / 1024d));
                routingCandidates++;
                if (!RingTerrainProxySector.SectorTouchesRiver(beds, coordinate, 1024, 16, 32)) routable = (coordinate, run.Col, run.Row);
            }
        }
        Check(edgesChecked > 0, "no member / non-member cell edge found in the strip");
        Detail($"basins: {graph.Basins.Length}, {graph.DryRuns.Length} dry runs; {edgesChecked} edges and {centresChecked} centres checked for P, {edgeSamples} edge samples; {floorSamples} floor samples, {playaSamples} with playa, {outsideSamples} outside member cells");
        // Non-member cells beside a member cell never get playa: checked above through DryBasin < 0. A member cell far from the lake edge must be able to show it.
        Check(playaSamples > 0, "no dry basin floor sample showed any playa (no arid basin floor in the strip, or the cover never rises)");

        // IntersectsClosedBasin: dry cells and balance-lake cells are basins; a spilling lake's cell and a plain cell are not; false without the feature.
        int dryTrue = 0, balanceTrue = 0, spillFalse = 0, plainFalse = 0;
        bool Point(RingTerrainSampler sampler, int col, int row) { long a = (long)((col + 0.5) * stepA); double z = (row + 0.5) * stepZ; return sampler.IntersectsClosedBasin(a, a, z, z); }
        foreach (LakeRun run in graph.DryRuns.Where((r, i) => i % 7 == 0 && (r.Col + 0.5) * stepA > aroundMin && (r.Col + 0.5) * stepA < aroundMax))
        {
            Check(Point(beds, run.Col, run.Row), $"dry cell ({run.Col},{run.Row}) is not a closed basin");
            Check(!Point(rivers, run.Col, run.Row), $"a rivers-only world reports the dry cell ({run.Col},{run.Row}) as a closed basin");
            dryTrue++;
        }
        foreach (LakeRun run in graph.Runs.Where((r, i) => i % 5 == 0 && (r.Col + 0.5) * stepA > aroundMin && (r.Col + 0.5) * stepA < aroundMax))
        {
            bool closed = graph.Lakes[run.LakeId].Basin >= 0;
            bool plainHere = graph.DryBasinAt(run.Col, run.Row) < 0;
            if (!plainHere) continue;
            Check(Point(beds, run.Col, run.Row) == closed, $"lake cell ({run.Col},{run.Row}) of lake {run.LakeId} (basin {graph.Lakes[run.LakeId].Basin}): closed basin answer {Point(beds, run.Col, run.Row)}");
            Check(!Point(rivers, run.Col, run.Row), $"a rivers-only world reports lake cell ({run.Col},{run.Row}) as a closed basin");
            if (closed) balanceTrue++; else spillFalse++;
        }
        for (int k = 0; k < graph.Runs.Length && plainFalse < 20; k += 3)
        {
            LakeRun run = graph.Runs[k];
            if ((run.Col + 0.5) * stepA <= aroundMin || (run.Col + 0.5) * stepA >= aroundMax) continue;
            // A plain cell: two columns right of the run's last cell, when that is neither flooded nor dry floor.
            int col = run.Col + run.Length + 1;
            if (graph.LakeAt(col, run.Row) >= 0 || graph.DryBasinAt(col, run.Row) >= 0) continue;
            Check(!Point(beds, col, run.Row), $"plain cell ({col},{run.Row}) is a closed basin");
            plainFalse++;
        }
        Detail($"IntersectsClosedBasin: {dryTrue} dry cells, {balanceTrue} balance-lake cells true; {spillFalse} spilling-lake cells and {plainFalse} plain cells false");
        Check(dryTrue > 0 && plainFalse > 0, "IntersectsClosedBasin was not exercised on dry and plain cells");

        // Routing: a dry-floor sector the river envelope misses routes to the CPU with the feature (the envelope is the river one) and to the GPU without it.
        Check(routable != null, $"no dry basin cell outside the river envelope in {routingCandidates} candidates (nothing to route)");
        if (routable is { } r0)
        {
            const int size = 1024, spacing = 16, parent = 32, width = 318000;
            using VoxelTerrainSettings gen1Settings = EmitterSiteTestWorlds.CreateProductionSettings(Seed, EmitterSiteTestWorlds.Gen17WorldRecipe(Seed, 1));
            using var gen1 = new RingTerrainSampler(gen1Settings, true, EmitterSiteTestWorlds.Circumference, EmitterSiteTestWorlds.Circumference / Math.Tau, EmitterSiteTestWorlds.Width, 8400);
            bool touchesRiver = RingTerrainProxySector.SectorTouchesRiver(beds, r0.Coordinate, size, spacing, parent);
            bool touchesBasin = RingTerrainProxySector.SectorTouchesBasin(beds, r0.Coordinate, size, spacing, parent);
            Check(!touchesRiver && touchesBasin, $"basin sector ({r0.Coordinate.AroundSector},{r0.Coordinate.AcrossSector}): river {touchesRiver}, basin {touchesBasin}");
            Check(!RingTerrainProxySector.GpuSectorRoutable(r0.Coordinate, size, spacing, width, false, false, touchesRiver, false, 0, touchesBasin), "a river-beds sector over a closed basin routes to the GPU");
            Check(RingTerrainProxySector.GpuSectorRoutable(r0.Coordinate, size, spacing, width, false, false, touchesRiver, false, 0, false), "the same sector does not route to the GPU when it touches no basin (control)");
            bool touchesBasinRivers = RingTerrainProxySector.SectorTouchesBasin(rivers, r0.Coordinate, size, spacing, parent);
            Check(!touchesBasinRivers && RingTerrainProxySector.GpuSectorRoutable(r0.Coordinate, size, spacing, width, false, false, RingTerrainProxySector.SectorTouchesRiver(rivers, r0.Coordinate, size, spacing, parent), false, 0, touchesBasinRivers), "without bit 3 the same sector no longer routes as before");
            Check(!RingTerrainProxySector.SectorTouchesBasin(gen1, r0.Coordinate, size, spacing, parent), "a generation-1 sampler touched a basin");
            Detail($"routing: sector ({r0.Coordinate.AroundSector},{r0.Coordinate.AcrossSector}) over dry cell ({r0.Col},{r0.Row}): beds river {touchesRiver} basin {touchesBasin} -> CPU; rivers-only basin {touchesBasinRivers} -> GPU");
        }
    }

    /// <summary>The closed basin a cell belongs to (its dry floor, or the basin of a balance lake there), or -1.</summary>
    private static int RiverBasinOf(RiverGraph graph, int col, int row)
    {
        int dry = graph.DryBasinAt(col, row);
        if (dry >= 0) return dry;
        int lake = graph.LakeAt(col, row);
        return lake >= 0 ? graph.Lakes[lake].Basin : -1;
    }

    // ------------------------------------------------------------------ cache records

    private void CacheRecords(RingTerrainSampler rivers, RingTerrainSampler beds, string scratch)
    {
        var edits = new RingLodSurfaceEditSnapshot(1, EmitterSiteTestWorlds.Circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        var points = new List<(long Around, int Across)>();
        foreach (SectorPick pick in PickSectors(rivers).Where(p => p.Spacing <= 4))
            foreach ((long a, int z) in Grid(pick).Where((_, i) => i % 37 == 0)) points.Add((a, z));
        int masked = points.Count(p => !beds.GetTerrainSample(p.Around, p.Across).RiverBeds.IsDefault);
        Check(masked > 0, "the cache record points carry no mask, so the round trip proves nothing");
        string error = RingTerrainProxySector.RoundTripCacheRecords(System.IO.Path.Combine(scratch, "beds", "lod_cache", "a.bin"), edits, beds, EmitterSiteTestWorlds.Width, points, out int version, out long payload, out bool stale);
        Check(error.Length == 0, "the v14 record did not round-trip the mask: " + error);
        Check(version == 14, $"a river-beds world wrote cache version {version}, want 14");
        Check(payload == 4 + 72L * points.Count, $"v14 payload {payload} bytes, want {4 + 72L * points.Count} (v13 plus 4)");
        Check(!stale, "a stale-version file was accepted by a river-beds world");
        string error13 = RingTerrainProxySector.RoundTripCacheRecords(System.IO.Path.Combine(scratch, "rivers", "lod_cache", "a.bin"), edits, rivers, EmitterSiteTestWorlds.Width, points, out int version13, out long payload13, out bool stale13);
        Check(error13.Length == 0, "the rivers-only record did not round-trip: " + error13);
        Check(version13 == 13 && payload13 == 4 + 68L * points.Count && !stale13, $"a rivers-only world's record changed (version {version13}, payload {payload13}, want 13 and {4 + 68L * points.Count})");
        Detail($"cache: river-beds v{version} {payload} bytes and rivers-only v{version13} {payload13} bytes for {points.Count} records, {masked} with a mask");
    }

    // ------------------------------------------------------------------ CPU micro-cost (run with --cost; prints numbers, checks nothing)

    private void CostProbe(RingTerrainSampler beds, TerrainGenerationParameters bedsRecipe)
    {
        using var model = new TerrainGenerationHeightModel(bedsRecipe);
        RiverGraph graph = beds.RuntimeRiverGraph!;
        double stepA = graph.AroundStepMeters, stepZ = graph.AcrossStepMeters;
        double aroundMin = StripStart + 40_000d, aroundMax = StripStart + StripKm * 1000d - 40_000d;
        var water = new List<(long A, int Z, TerrainGenerationHeightModel.Diagnostics D, float H, float M)>();
        var basin = new List<(long A, int Z, TerrainGenerationHeightModel.Diagnostics D, float H, float M)>();
        foreach (SectorPick pick in PickSectors(beds).Where(p => p.Spacing <= 4))
            foreach ((long a, int z) in Grid(pick))
            {
                beds.GetRuntimeTerrainSampleAndPresentationHeight(a, z, 0d, out TerrainSample s, out float h);
                water.Add((a, z, model.SampleDiagnostics(a, z, 0d), h, s.Moisture));
            }
        foreach (LakeRun run in graph.DryRuns)
        {
            if ((run.Col + 0.5) * stepA < aroundMin || (run.Col + 0.5) * stepA > aroundMax || run.Row < 8 || run.Row > graph.Rows - 8 || basin.Count >= 20_000) continue;
            for (int c = 0; c < run.Length && c < 4; c++)
                for (int k = 0; k < 16; k++)
                {
                    long a = (long)Math.Round((run.Col + c + 0.05 + 0.06 * k) * stepA / 2d) * 2L; int z = (int)Math.Round((run.Row + 0.1 + 0.05 * (k % 8)) * stepZ / 2d) * 2;
                    beds.GetRuntimeTerrainSampleAndPresentationHeight(a, z, 0d, out TerrainSample s, out float h);
                    basin.Add((a, z, model.SampleDiagnostics(a, z, 0d), h, s.Moisture));
                }
        }
        double Time(List<(long A, int Z, TerrainGenerationHeightModel.Diagnostics D, float H, float M)> list, int count, bool shuffled = false)
        {
            long sink = 0, start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < count; i++)
            {
                // Shuffled: a stride that is coprime with the list length, so consecutive evaluations hit other 1 km blocks and the 2 x 2 memo rarely helps.
                var p = list[shuffled ? (int)((i * 7919L) % list.Count) : i % list.Count];
                sink += beds.CreateRiverBedMaskLocked(p.A, p.Z, p.D, p.H, p.M, 0d).Packed;
            }
            double ns = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1e9 / System.Diagnostics.Stopwatch.Frequency / count;
            if (sink == 42) Console.WriteLine();
            return ns;
        }
        Time(water, 100_000); Time(basin, 100_000);
        double nsWater = Time(water, 1_000_000), nsBasin = Time(basin, 1_000_000), nsBasinShuffled = Time(basin, 1_000_000, true);
        // One model evaluation: the diagnostics of the same points (water pocket and basin floors), the full sample (model plus climate) as the second reference.
        int n = 20_000;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        double sinkD = 0d;
        for (int i = 0; i < n; i++) { var p = water[i % water.Count]; sinkD += model.SampleDiagnostics(p.A, p.Z, 0d).Height; }
        double nsModelWater = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e9 / System.Diagnostics.Stopwatch.Frequency / n;
        t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < n; i++) { var p = basin[i % basin.Count]; sinkD += model.SampleDiagnostics(p.A, p.Z, 0d).Height; }
        double nsModelBasin = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e9 / System.Diagnostics.Stopwatch.Frequency / n;
        t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < n; i++) { var p = water[i % water.Count]; beds.GetRuntimeTerrainSampleAndPresentationHeight(p.A, p.Z, 0d, out TerrainSample s, out _); sinkD += s.SurfaceHeight; }
        double nsFullWater = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e9 / System.Diagnostics.Stopwatch.Frequency / n;
        if (sinkD == 42d) Console.WriteLine();
        Detail($"cost: mask near water {nsWater:F1} ns ({water.Count} points), on basin floors {nsBasin:F1} ns in lattice order and {nsBasinShuffled:F1} ns shuffled ({basin.Count} points); model evaluation {nsModelWater:F0} ns near water, {nsModelBasin:F0} ns on basin floors; full sample (model, climate, mask) near water {nsFullWater:F0} ns");
        Detail($"cost ratio: mask / model evaluation {100d * nsWater / nsModelWater:F2} % near water, {100d * nsBasin / nsModelBasin:F2} % on basin floors");
    }
}
