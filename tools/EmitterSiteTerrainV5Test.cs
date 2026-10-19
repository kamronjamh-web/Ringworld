using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// M1-2 (spec P3): gen-17 v5 recipe with emitter-site shaping through the real RingTerrainSampler. Covers v4 identity, v5 hash
/// determinism, operator agreement at pad / skirt / island points, pad flatness, island dryness through the real water
/// classification, sampler throughput v4 vs v5 and registry build / sharing.
/// </summary>
public partial class EmitterSiteTerrainV5Test : RefCounted
{
    const long Circumference = EmitterSiteTestWorlds.Circumference;
    const int Width = EmitterSiteTestWorlds.Width;
    const int Seed = 1337;

    public int Run()
    {
        var failures = new List<string>();
        try
        {
            using RingTerrainSampler v4 = EmitterSiteTestWorlds.CreateGen17(Seed);

            // (g) cold registry build at sampler construction, confirm sharing.
            EmitterSiteRegistry.ClearCache();
            EmitterSiteTerrainShaper.ClearCache();
            var coldWatch = Stopwatch.StartNew();
            using RingTerrainSampler v5 = EmitterSiteTestWorlds.CreateGen17Sites(Seed);
            coldWatch.Stop();
            EmitterSiteRegistry registry = v5.EmitterSites ?? throw new InvalidOperationException("v5 sampler has no registry");
            Console.WriteLine($"EMITTER_V5_BUILD sampler_ctor_cold_ms={coldWatch.Elapsed.TotalMilliseconds:F0} registry_build_ms={registry.BuildMilliseconds:F0} threads={registry.BuildThreads} sites={registry.Count}");
            var warmWatch = Stopwatch.StartNew();
            using RingTerrainSampler v5b = EmitterSiteTestWorlds.CreateGen17Sites(Seed);
            warmWatch.Stop();
            Console.WriteLine($"EMITTER_V5_BUILD sampler_ctor_cached_ms={warmWatch.Elapsed.TotalMilliseconds:F0}");
            if (!ReferenceEquals(registry, v5b.EmitterSites)) failures.Add("second sampler for the same definition rebuilt the registry");
            if (warmWatch.Elapsed.TotalMilliseconds > coldWatch.Elapsed.TotalMilliseconds * 0.5) failures.Add("cached sampler construction is not clearly cheaper than cold");
            var copyWatch = Stopwatch.StartNew();
            using (RingTerrainSampler copy = v5.CreateIndependentCopy())
            {
                copyWatch.Stop();
                if (!ReferenceEquals(registry, copy.EmitterSites)) failures.Add("independent copy does not share the registry instance");
                if (copy.TerrainDefinitionHash != v5.TerrainDefinitionHash) failures.Add("independent copy hash differs");
                if (copy.GetPresentationSurfaceHeight(registry.MainSite.Around, registry.MainSite.Across) != registry.MainSite.PadHeight) failures.Add("independent copy does not shape the main pad");
            }
            Console.WriteLine($"EMITTER_V5_BUILD independent_copy_ms={copyWatch.Elapsed.TotalMilliseconds:F0} shared_registry=true");
            if (v4.EmitterSites != null) failures.Add("v4 sampler exposes a registry");

            CheckVersioning(v4, v5, failures);
            CheckV4Identity(v4, failures);
            CheckV4Fingerprint(v4, failures);
            using var natural = new TerrainGenerationHeightModel(v5.SurfaceGenerationRecipeJson.Length > 0
                ? TerrainGenerationParameters.FromJson(v5.SurfaceGenerationRecipeJson) : throw new InvalidOperationException(), false);
            CheckRegistryMatchesV4Natural(v4, registry, failures);
            CheckOperatorAgreement(v5, registry, natural, failures);
            CheckPadFlat(v5, registry, failures);
            CheckIslandsDry(v5, registry, failures);
            CheckDiagnostics(v5, registry, natural, failures);
            CheckSurfaceConsumers(v4, v5, registry, failures);
            Benchmark(v4, v5, registry, failures);
        }
        catch (Exception exception) { failures.Add(exception.ToString()); }

        foreach (string failure in failures) GD.PrintErr("EMITTER_V5_FAIL " + failure);
        Console.WriteLine(failures.Count == 0 ? "EMITTER_V5=PASS" : "EMITTER_V5=FAIL failures=" + failures.Count);
        return failures.Count == 0 ? 0 : 1;
    }

    static void CheckVersioning(RingTerrainSampler v4, RingTerrainSampler v5, List<string> failures)
    {
        if (v4.TerrainGeneratorVersion != 17 || v5.TerrainGeneratorVersion != 17) failures.Add("generator version must stay 17");
        if (v4.TerrainDefinitionHash == v5.TerrainDefinitionHash) failures.Add("v5 hash equals v4 hash");
        using RingTerrainSampler again = EmitterSiteTestWorlds.CreateGen17Sites(Seed);
        if (again.TerrainDefinitionHash != v5.TerrainDefinitionHash) failures.Add("v5 hash is not deterministic");
        // Rebuild with cold caches: identical registry, identical hash.
        EmitterSiteRegistry.ClearCache();
        EmitterSiteTerrainShaper.ClearCache();
        using RingTerrainSampler cold = EmitterSiteTestWorlds.CreateGen17Sites(Seed);
        if (cold.TerrainDefinitionHash != v5.TerrainDefinitionHash) failures.Add("v5 hash changes after a cold registry rebuild");
        if (cold.EmitterSites!.Identity != v5.EmitterSites!.Identity) failures.Add("registry identity changes after a cold rebuild");
        if (!cold.EmitterSites.ToCanonicalBytes().AsSpan().SequenceEqual(v5.EmitterSites.ToCanonicalBytes())) failures.Add("registry bytes change after a cold rebuild");
        using RingTerrainSampler other = EmitterSiteTestWorlds.CreateGen17Sites(Seed + 1);
        if (other.TerrainDefinitionHash == v5.TerrainDefinitionHash) failures.Add("v5 hash ignores the seed");
        if (!v5.SurfaceGenerationRecipeJson.Contains("ringworld_surface_sites_v5")) failures.Add("v5 recipe id missing from canonical json");
        TerrainGenerationParameters v4Params = EmitterSiteTestWorlds.Gen17Recipe(Seed), v5Params = EmitterSiteTestWorlds.Gen17SitesRecipe(Seed);
        if (v4Params.DefinitionHash == v5Params.DefinitionHash) failures.Add("recipe definition hashes equal");
        Console.WriteLine($"EMITTER_V5_HASH v4={v4.TerrainDefinitionHash:X16} v5={v5.TerrainDefinitionHash:X16} v5_recipe={v5Params.DefinitionHash:X16} registry={v5.EmitterSites!.Identity:X16} site_recipe={v5.EmitterSites.Recipe.Hash:X16} algo={v5.EmitterSites.Recipe.AlgorithmVersion}");
    }

    // (a) v4 samplers keep the baseline hash and heights equal the unshaped model; v5 base terrain equals v4 beyond influence.
    static void CheckV4Identity(RingTerrainSampler v4, List<string> failures)
    {
        const ulong BaselineHash = 0xC62D22E76ED02DFAUL;
        if (v4.TerrainDefinitionHash != BaselineHash) failures.Add($"v4 hash {v4.TerrainDefinitionHash:X16} != baseline {BaselineHash:X16}");
        using var model = new TerrainGenerationHeightModel(EmitterSiteTestWorlds.Gen17Recipe(Seed));
        if (model.EmitterSites != null) failures.Add("v4 model has a shaper");
        var rng = new Random(11);
        int mismatches = 0;
        for (int i = 0; i < 20000; i++)
        {
            long a = (long)(rng.NextDouble() * (Circumference / 2)) * 2L;
            int x = (int)(rng.NextDouble() * (Width / 2 - 1)) * 2;
            double expected = model.SampleNaturalDiagnostics(a, x).Height;
            if (model.CalculateHeight(a, x) != expected || model.SampleDiagnostics(a, x).Height != expected || v4.GetPresentationSurfaceHeight(a, x) != (float)expected) mismatches++;
        }
        if (mismatches != 0) failures.Add($"v4 heights differ from the natural model at {mismatches} points");
        Console.WriteLine($"EMITTER_V5_V4_IDENTITY hash={v4.TerrainDefinitionHash:X16} sampled=20000 mismatches={mismatches}");
    }


    // M1-6 v4 fingerprint: FNV-1a over terrain samples, biome weights (unfused and fused), one grass tile and one tree tile.
    // Surface-mask consumers must leave every one of these bit-identical on a v4 sampler.
    // Fixed tree review tile (found once by a seeded search on the v4 sampler; the 186000/141000 grass focus tile has no trees).
    static readonly (long Around, long Across) FingerprintTreeTile = (84509L, 762L);
    const ulong ExpectedFingerprintTerrain = 0xD82EFCF9C4FCE3E4UL;
    const ulong ExpectedFingerprintGrass = 0x071B8B28921E7298UL;
    const ulong ExpectedFingerprintTrees = 0x9C6BDB7DF0127E66UL;

    static ulong Fnv(ulong h, ulong v)
    {
        for (int i = 0; i < 8; i++) { h ^= (v >> (i * 8)) & 0xFF; h *= 0x100000001B3UL; }
        return h;
    }

    static ulong FnvF(ulong h, float f) => Fnv(h, (ulong)(uint)BitConverter.SingleToInt32Bits(f));

    static ulong FnvWeights(ulong h, RingTerrainSampler.BiomeTextureWeights w)
        => FnvF(FnvF(FnvF(FnvF(h, w.Desert), w.Forest), w.Tundra), w.Alpine);

    static ulong FingerprintTerrain(RingTerrainSampler s)
    {
        ulong h = 0xCBF29CE484222325UL;
        var rng = new Random(4096);
        for (int i = 0; i < 4096; i++)
        {
            long a = (long)(rng.NextDouble() * (Circumference / 2)) * 2L;
            int x = (int)(rng.NextDouble() * (Width / 2 - 1)) * 2;
            TerrainSample t = s.GetTerrainSample(a, x);
            h = Fnv(h, (ulong)t.SurfaceHeight); h = Fnv(h, (ulong)t.Region); h = FnvF(h, t.Continentalness); h = FnvF(h, t.Erosion);
            h = FnvF(h, t.RidgeStrength); h = FnvF(h, t.Temperature); h = FnvF(h, t.Moisture); h = Fnv(h, (ulong)t.Biome);
            h = Fnv(h, (ulong)t.WaterKind); h = Fnv(h, (ulong)t.WaterSurfaceHeight);
            h = FnvWeights(h, s.GetBiomeTextureWeights(t));
            s.GetPresentationSurfaceHeightAndBiomeWeights(a, x, out float height, out RingTerrainSampler.BiomeTextureWeights fused);
            h = FnvF(h, height); h = FnvWeights(h, fused);
        }
        return h;
    }

    static RingGrassTileData BuildGrassTile(RingTerrainSampler sampler, double aroundCenter, double acrossCenter)
    {
        var focus = new RingCanonicalPosition(aroundCenter, 0, acrossCenter);
        var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero, Circumference / Math.Tau,
            0, 0, Circumference, -1000, 1000, focus);
        var edits = new RingLodSurfaceEditSnapshot(1, Circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        var bounds = new RingGrassTileBounds(aroundCenter - 16, aroundCenter + 16, acrossCenter - 16, acrossCenter + 16);
        var key = new RingGrassTileKey((long)(aroundCenter / 32), (long)(acrossCenter / 32));
        return RingGrassTileBuilder.Build(key, bounds, 4, 4, 16, 16_384, 60, sampler, edits, geometry, CancellationToken.None,
            sourceFraction: 1, useGrassClumps: false);
    }

    static List<RingBiomeDecorationLayer.ImportedForestPlacementRecord> BuildTreeTile(RingTerrainSampler sampler, long tileAround, long tileAcross)
        => RingBiomeDecorationLayer.BuildImportedForestReviewTile(sampler, tileAround, tileAcross, (long)Math.Ceiling(Circumference / 30d), 30d, Seed, Circumference, Width);

    static ulong HashTrees(List<RingBiomeDecorationLayer.ImportedForestPlacementRecord> records)
    {
        ulong h = 0xCBF29CE484222325UL;
        foreach (var r in records)
        {
            h = Fnv(h, r.Id); h = Fnv(h, (ulong)r.Around); h = Fnv(h, (ulong)r.Across); h = FnvF(h, r.SurfaceHeight);
            h = FnvF(h, r.Width); h = FnvF(h, r.Height); h = FnvF(h, r.Yaw); h = Fnv(h, (ulong)r.Variant); h = Fnv(h, (ulong)r.Species);
        }
        return Fnv(h, (ulong)records.Count);
    }

    static void CheckV4Fingerprint(RingTerrainSampler v4, List<string> failures)
    {
        ulong terrain = FingerprintTerrain(v4);
        RingGrassTileData grass = BuildGrassTile(v4, 186_016d, 141_016d);
        ulong grassHash = Fnv((ulong)grass.PlacementHash, (ulong)grass.InstanceCount);
        var trees = BuildTreeTile(v4, FingerprintTreeTile.Around, FingerprintTreeTile.Across);
        ulong treeHash = HashTrees(trees);
        Console.WriteLine($"EMITTER_V5_V4_FINGERPRINT terrain=0x{terrain:X16} grass=0x{grassHash:X16} grass_instances={grass.InstanceCount} trees=0x{treeHash:X16} tree_records={trees.Count}");
        if (grass.InstanceCount == 0) failures.Add("v4 fingerprint grass tile is empty");
        if (trees.Count == 0) failures.Add("v4 fingerprint tree tile is empty");
        if (terrain != ExpectedFingerprintTerrain) failures.Add($"v4 terrain/weights fingerprint 0x{terrain:X16} != expected");
        if (grassHash != ExpectedFingerprintGrass) failures.Add($"v4 grass fingerprint 0x{grassHash:X16} != expected");
        if (treeHash != ExpectedFingerprintTrees) failures.Add($"v4 tree fingerprint 0x{treeHash:X16} != expected");
    }

    // The v5 registry is built from the same natural height v4 exposes: positions, pads and flags equal a registry built through a v4 sampler.
    static void CheckRegistryMatchesV4Natural(RingTerrainSampler v4, EmitterSiteRegistry v5Registry, List<string> failures)
    {
        using INaturalHeightSource source = EmitterSiteHeightSources.Create(v4);
        EmitterSiteRegistry viaV4 = EmitterSiteRegistry.Build(EmitterSiteRecipe.Default(Seed), source);
        int diff = 0;
        if (viaV4.Count != v5Registry.Count) diff++;
        else
            for (int i = 0; i < viaV4.Count; i++)
                if (viaV4[i].Around != v5Registry[i].Around || viaV4[i].Across != v5Registry[i].Across || viaV4[i].PadHeight != v5Registry[i].PadHeight || viaV4[i].Flags != v5Registry[i].Flags || viaV4[i].SiteHash != v5Registry[i].SiteHash) diff++;
        if (diff != 0) failures.Add($"v5 registry differs from the registry built on v4 natural terrain at {diff} sites (shaped height leaked into the build?)");
        Console.WriteLine($"EMITTER_V5_REGISTRY_FROM_NATURAL sites={v5Registry.Count} differing_vs_v4_built={diff}");
    }

    static IEnumerable<int> TestSites(EmitterSiteRegistry r)
    {
        var picked = new List<int> { r.MainIndex };
        picked.AddRange(Enumerable.Range(0, r.Count - 1).Where(i => !r[i].IsIsland).Take(5));
        picked.AddRange(Enumerable.Range(0, r.Count - 1).Where(i => r[i].IsIsland).Take(5));
        picked.AddRange(Enumerable.Range(0, r.Count - 1).Where(i => r[i].IsMountain).Take(2));
        return picked.Distinct();
    }

    static (long Around, int Across) Offset(EmitterSite s, double radius, double azimuthDegrees)
    {
        double rad = azimuthDegrees * Math.PI / 180d;
        long du = (long)Math.Round(radius * Math.Cos(rad) / 2d) * 2L;
        int dw = (int)Math.Round(radius * Math.Sin(rad) / 2d) * 2;
        return (s.Around + du, s.Across + dw);
    }

    // (c) sampler heights equal operator(natural) at pad / apron / cone / skirt / island rings, and natural beyond R_inf.
    internal static void CheckOperatorAgreement(RingTerrainSampler v5, EmitterSiteRegistry r, TerrainGenerationHeightModel natural, List<string> failures)
    {
        var op = v5.EmitterSiteOperator ?? throw new InvalidOperationException("operator missing");
        int checkedPoints = 0, bad = 0, changed = 0, beyond = 0, beyondBad = 0;
        foreach (int index in TestSites(r))
        {
            EmitterSite s = r[index];
            EmitterSiteKindShape shape = r.Recipe.ShapeFor(s.Kind);
            double[] radii = { 0, 0.5 * shape.PadRadiusM, shape.PadRadiusM, shape.RhoStartM - 4, shape.RhoStartM + 4, shape.RhoStartM + 60, shape.RhoStartM + 300,
                shape.SkirtFadeStartM, 0.5 * (shape.SkirtFadeStartM + shape.SkirtMaxRadiusM), shape.SkirtMaxRadiusM + 10,
                shape.EnclosureRadiusM - 100, shape.EnclosureRadiusM + 50, shape.ShoreRadiusM + 20, shape.ShelfRadiusM + 200, shape.ShelfRadiusM + 2000, s.InfluenceRadius - 20 };
            foreach (double radius in radii)
            for (int az = 0; az < 360; az += 45)
            {
                var (a, x) = Offset(s, radius, az + index % 7);
                if (x < 0 || x > Width - 2) continue;
                double du = a - s.Around, dw = x - s.Across;
                double h0 = natural.SampleNaturalDiagnostics(((a % Circumference) + Circumference) % Circumference, x).Height;
                double expected = Math.Clamp(op.Shape(s, du, dw, h0), -8000d, 8000d);
                float actual = v5.GetPresentationSurfaceHeight(a, x);
                checkedPoints++;
                if (actual != (float)expected) { bad++; if (bad <= 5) failures.Add($"site {index} r={radius:F0} az={az}: sampler {actual} != operator {(float)expected} (h0 {h0})"); }
                if ((float)h0 != actual) changed++;
            }
            // Beyond R_inf the sampler must equal the natural model exactly.
            foreach (double beyondR in new[] { s.InfluenceRadius + 50d, s.InfluenceRadius + 700d })
            for (int az = 10; az < 360; az += 90)
            {
                var (a, x) = Offset(s, beyondR, az);
                if (x < 0 || x > Width - 2) continue;
                double wrapped = ((a % Circumference) + Circumference) % Circumference;
                float expected = (float)natural.SampleNaturalDiagnostics(wrapped, x).Height;
                float actual = v5.GetPresentationSurfaceHeight(a, x);
                beyond++;
                if (actual != expected) { beyondBad++; failures.Add($"site {index} beyond R_inf r={beyondR:F0}: sampler {actual} != natural {expected}"); }
            }
        }
        if (changed == 0) failures.Add("operator never changed the terrain at the test points");
        Console.WriteLine($"EMITTER_V5_OPERATOR_AGREEMENT sites={TestSites(r).Count()} points={checkedPoints} mismatches={bad} terrain_changed_points={changed} beyond_points={beyond} beyond_mismatches={beyondBad}");
    }

    // (d) every site: the pad is exactly P at lattice points through the sampler.
    internal static void CheckPadFlat(RingTerrainSampler v5, EmitterSiteRegistry r, List<string> failures)
    {
        int points = 0, bad = 0;
        for (int i = 0; i < r.Count; i++)
        {
            EmitterSite s = r[i];
            double padR = r.Recipe.ShapeFor(s.Kind).PadRadiusM;
            foreach (double radius in new[] { 0d, 0.25 * padR, 0.5 * padR, 0.9 * padR, padR })
            for (int az = 0; az < 360; az += 30)
            {
                var (a, x) = Offset(s, radius, az);
                float h = v5.GetPresentationSurfaceHeight(a, x);
                points++;
                if (h != s.PadHeight) { bad++; if (bad <= 5) failures.Add($"site {i} pad r={radius:F0}: {h} != {s.PadHeight}"); }
            }
        }
        // Also a dense disc scan on the main pad.
        EmitterSite main = r.MainSite;
        for (int du = -1000; du <= 1000; du += 50)
        for (int dw = -1000; dw <= 1000; dw += 50)
        {
            if (du * du + dw * dw > 1000 * 1000) continue;
            points++;
            if (v5.GetPresentationSurfaceHeight(main.Around + du, main.Across + dw) != main.PadHeight) { bad++; failures.Add($"main pad lattice ({du},{dw}) not flat"); break; }
        }
        Console.WriteLine($"EMITTER_V5_PAD_FLAT sites={r.Count} points={points} mismatches={bad}");
    }

    // (e) islands: no water and surface above sea + dry margin inside the enclosure radius, using the real classification.
    static void CheckIslandsDry(RingTerrainSampler v5, EmitterSiteRegistry r, List<string> failures)
    {
        int islands = 0, points = 0, wet = 0, low = 0;
        var regions = new Dictionary<TerrainRegion, int>();
        var biomes = new Dictionary<string, int>();
        foreach (EmitterSite s in r.Sites.ToArray().Where(s => s.IsIsland))
        {
            islands++;
            double enclosure = r.Recipe.ShapeFor(s.Kind).EnclosureRadiusM;
            double cover = r.Recipe.ShapeFor(s.Kind).CoverDepthM;
            foreach (double fraction in new[] { 0d, 0.1, 0.25, 0.4, 0.55, 0.7, 0.85, 0.95, 1.0 })
            for (int az = 0; az < 360; az += 30)
            {
                var (a, x) = Offset(s, enclosure * fraction, az);
                if (x < 0 || x > Width - 2) continue;
                TerrainSample sample = v5.GetTerrainSample(a, x);
                points++;
                if (sample.HasSurfaceWater) { wet++; if (wet <= 5) failures.Add($"island {s.Index} r={enclosure * fraction:F0}: water kind {sample.WaterKind}"); }
                float h = v5.GetPresentationSurfaceHeight(a, x);
                if (h < r.Recipe.DryMarginM) { low++; if (low <= 5) failures.Add($"island {s.Index} r={enclosure * fraction:F0}: surface {h} below dry margin"); }
                if (h < s.PadHeight - cover - 1d) failures.Add($"island {s.Index} r={enclosure * fraction:F0}: surface {h} breaches the cover guarantee (pad {s.PadHeight})");
                regions[sample.Region] = regions.GetValueOrDefault(sample.Region) + 1;
                biomes[sample.Biome.ToString()] = biomes.GetValueOrDefault(sample.Biome.ToString()) + 1;
            }
        }
        if (islands == 0) failures.Add("no island sites in the seed-1337 registry");
        Console.WriteLine($"EMITTER_V5_ISLANDS islands={islands} points={points} wet={wet} below_dry_margin={low} regions=[{string.Join(", ", regions.Select(p => p.Key + ":" + p.Value))}] biomes=[{string.Join(", ", biomes.Select(p => p.Key + ":" + p.Value))}]");
    }

    // Diagnostics: site index / weight / mask exposed, none outside influence, consistent between model, sampler accessor and operator.
    static void CheckDiagnostics(RingTerrainSampler v5, EmitterSiteRegistry r, TerrainGenerationHeightModel natural, List<string> failures)
    {
        EmitterSite main = r.MainSite;
        if (!v5.TryGetEmitterSiteSurfaceMask(main.Around, main.Across, out SiteSurfaceMask padMask, out int idx, out double weight)
            || idx != r.MainIndex || padMask.Class != SiteSurfaceClass.Pad || padMask.Hardscape != 1f || weight != 1d || padMask.VegetationAllowed)
            failures.Add($"main pad diagnostics wrong: idx={idx} class={padMask.Class} hard={padMask.Hardscape} weight={weight}");
        EmitterSite far = r[0];
        var (a, x) = Offset(far, far.InfluenceRadius + 100d, 30);
        if (v5.TryGetEmitterSiteSurfaceMask(a, x, out _, out int outside, out double w2) || outside != -1 || w2 != 0d) failures.Add("diagnostics report a site outside the influence disc");
        // A cut-face/rock sample somewhere around a mountain/land site, and a beach on an island.
        int rock = 0, sand = 0, hard = 0, total = 0;
        foreach (int index in TestSites(r))
        {
            EmitterSite s = r[index];
            EmitterSiteKindShape shape = r.Recipe.ShapeFor(s.Kind);
            for (double radius = shape.PadRadiusM; radius < shape.ShelfRadiusM + 100d && radius < s.InfluenceRadius; radius += 40d)
            for (int az = 0; az < 360; az += 40)
            {
                var (pa, px) = Offset(s, radius, az);
                if (px < 0 || px > Width - 2) continue;
                if (!v5.TryGetEmitterSiteSurfaceMask(pa, px, out SiteSurfaceMask m, out int i2, out _)) continue;
                total++;
                if (i2 != index) failures.Add($"mask lookup returned site {i2} near site {index}");
                if (m.Rock > 0.5f) rock++;
                if (m.Sand > 0.5f) sand++;
                if (m.Hardscape > 0.5f) hard++;
            }
        }
        Console.WriteLine($"EMITTER_V5_DIAGNOSTICS scanned={total} rock_gt_half={rock} sand_gt_half={sand} hardscape_gt_half={hard}");
    }

    static bool NearAnySite(EmitterSiteRegistry r, long around, int across, double halfExtent)
    {
        foreach ((double du, double dw) in new[] { (0d, 0d), (-halfExtent, -halfExtent), (halfExtent, -halfExtent), (-halfExtent, halfExtent), (halfExtent, halfExtent) })
        {
            long a = ((around + (long)du) % Circumference + Circumference) % Circumference;
            int x = Math.Clamp(across + (int)dw, 0, Width - 2);
            if (r.TryGetInfluencingSite(a, x, out _, out _, out _)) return true;
        }
        return false;
    }

    // M1-6 (spec P4a): surface-mask consumers (biome texture weights, grass, trees).
    static void CheckSurfaceConsumers(RingTerrainSampler v4, RingTerrainSampler v5, EmitterSiteRegistry r, List<string> failures)
    {
        int padPoints = 0, padBad = 0;
        foreach (int index in TestSites(r))
        {
            EmitterSite s = r[index];
            double padR = r.Recipe.ShapeFor(s.Kind).PadRadiusM;
            foreach (double radius in new[] { 0d, 0.5 * padR })
            for (int az = 0; az < 360; az += 45)
            {
                var (a, x) = Offset(s, radius, az);
                if (x < 0 || x > Width - 2) continue;
                TerrainSample t = v5.GetTerrainSample(a, x);
                RingTerrainSampler.BiomeTextureWeights w = v5.GetBiomeTextureWeights(t);
                v5.GetPresentationSurfaceHeightAndBiomeWeights(a, x, out _, out RingTerrainSampler.BiomeTextureWeights fused);
                padPoints++;
                bool ok = t.SiteMask.Class == SiteSurfaceClass.Pad && t.SiteMask.BlocksVegetation && w.Alpine >= 0.999f
                    && w.Desert <= 1e-3f && w.Forest <= 1e-3f && w.Tundra <= 1e-3f && fused == w;
                if (!ok) { padBad++; if (padBad <= 5) failures.Add($"site {index} pad r={radius:F0} az={az}: class={t.SiteMask.Class} blocks={t.SiteMask.BlocksVegetation} weights={w} fused={fused}"); }
            }
        }

        int rockFound = 0, rockBad = 0, sandFound = 0, sandBad = 0, fusedBad = 0;
        foreach (int index in TestSites(r))
        {
            EmitterSite s = r[index];
            EmitterSiteKindShape shape = r.Recipe.ShapeFor(s.Kind);
            for (double radius = shape.PadRadiusM; radius < shape.ShelfRadiusM + 100d && radius < s.InfluenceRadius; radius += 40d)
            for (int az = 0; az < 360; az += 40)
            {
                var (a, x) = Offset(s, radius, az);
                if (x < 0 || x > Width - 2) continue;
                TerrainSample t = v5.GetTerrainSample(a, x);
                SiteSurfaceMask m = t.SiteMask;
                if (m.Rock <= 0.5f && m.Sand <= 0.5f) continue;
                RingTerrainSampler.BiomeTextureWeights w = v5.GetBiomeTextureWeights(t);
                v5.GetPresentationSurfaceHeightAndBiomeWeights(a, x, out _, out RingTerrainSampler.BiomeTextureWeights fused);
                if (fused != w) fusedBad++;
                if (m.Rock > 0.5f) { rockFound++; if (w.Alpine < m.Rock - 1e-4f) rockBad++; }
                if (m.Sand > 0.5f) { sandFound++; if (w.Desert < m.Sand - 1e-4f) sandBad++; }
            }
        }
        // Beach: radial search on every island starting just outside the enclosure edge.
        foreach (EmitterSite s in r.Sites.ToArray().Where(s => s.IsIsland))
        {
            double enclosure = r.Recipe.ShapeFor(s.Kind).EnclosureRadiusM;
            for (double radius = enclosure + 100d; radius < enclosure + 1500d && radius < s.InfluenceRadius; radius += 20d)
            for (int az = 0; az < 360; az += 90)
            {
                var (a, x) = Offset(s, radius, az);
                if (x < 0 || x > Width - 2) continue;
                TerrainSample t = v5.GetTerrainSample(a, x);
                if (t.SiteMask.Sand <= 0.5f) continue;
                RingTerrainSampler.BiomeTextureWeights w = v5.GetBiomeTextureWeights(t);
                sandFound++;
                if (w.Desert < t.SiteMask.Sand - 1e-4f) sandBad++;
            }
        }
        if (rockFound == 0) failures.Add("no cut-face sample with Rock > 0.5 found");
        if (sandFound == 0) failures.Add("no beach sample with Sand > 0.5 found");
        if (rockBad != 0 || sandBad != 0 || fusedBad != 0) failures.Add($"weights: rock_bad={rockBad} sand_bad={sandBad} fused_vs_unfused_bad={fusedBad}");

        // Grass: no instances on pads, identical to v4 beyond influence.
        EmitterSite main = r.MainSite;
        EmitterSite regular = r[TestSites(r).First(i => i != r.MainIndex && !r[i].IsIsland)];
        int mainGrass = BuildGrassTile(v5, main.Around, main.Across).InstanceCount;
        int regularGrass = BuildGrassTile(v5, regular.Around, regular.Across).InstanceCount;
        int regularGrassV4 = BuildGrassTile(v4, regular.Around, regular.Across).InstanceCount;
        if (mainGrass != 0) failures.Add($"main pad grass tile has {mainGrass} instances");
        if (regularGrass != 0) failures.Add($"regular pad grass tile has {regularGrass} instances");
        int farGrassChecked = 0, farGrassNonEmpty = 0;
        var grng = new Random(21);
        for (int i = 0; i < 60 && farGrassChecked < 3; i++)
        {
            double a = Math.Floor(grng.NextDouble() * Circumference / 32d) * 32d + 16d, x = Math.Floor(grng.NextDouble() * (Width - 64) / 32d) * 32d + 48d;
            if (NearAnySite(r, (long)a, (int)x, 40d)) continue;
            RingGrassTileData g4 = BuildGrassTile(v4, a, x), g5 = BuildGrassTile(v5, a, x);
            farGrassChecked++;
            if (g4.InstanceCount > 0) farGrassNonEmpty++;
            if (g4.PlacementHash != g5.PlacementHash || g4.InstanceCount != g5.InstanceCount) failures.Add($"far grass tile at {a},{x}: v5 hash differs from v4");
        }
        if (farGrassChecked == 0 || farGrassNonEmpty == 0) failures.Add($"far grass check had no populated tiles (checked={farGrassChecked} nonempty={farGrassNonEmpty})");

        // Trees: the review tile (8 cells of 30 m) containing each pad centre is empty; a tile far from all sites equals v4.
        int treePadTiles = 0, treePadV4Records = 0;
        foreach (int index in TestSites(r).Where(i => !r[i].IsIsland))
        {
            EmitterSite s = r[index];
            long ta = (long)Math.Floor(s.Around / 30d) / 8, tz = (long)Math.Floor(s.Across / 30d) / 8;
            var records = BuildTreeTile(v5, ta, tz);
            treePadTiles++;
            treePadV4Records += BuildTreeTile(v4, ta, tz).Count;
            if (records.Count != 0) failures.Add($"site {index} pad tile ({ta},{tz}) has {records.Count} tree records");
        }
        long fa = FingerprintTreeTile.Around, fz = FingerprintTreeTile.Across;
        var farV4 = BuildTreeTile(v4, fa, fz);
        var farV5 = BuildTreeTile(v5, fa, fz);
        if (NearAnySite(r, fa * 240L + 120L, (int)(fz * 240L + 120L), 160d)) failures.Add("fixed far tree tile is within a site influence disc");
        if (farV4.Count == 0 || !farV4.SequenceEqual(farV5)) failures.Add($"far tree tile v4 ({farV4.Count}) != v5 ({farV5.Count})");

        // The v4 fingerprint is enforced in CheckV4Fingerprint.
        Console.WriteLine($"EMITTER_V5_SURFACE_CONSUMERS pad_points={padPoints} pad_bad={padBad} rock_found={rockFound} sand_found={sandFound} grass_main={mainGrass} grass_regular={regularGrass} (v4 {regularGrassV4}) far_grass_tiles={farGrassChecked} nonempty={farGrassNonEmpty} tree_pad_tiles={treePadTiles} (v4 records {treePadV4Records}) far_tree_records={farV5.Count}");
        if (padBad != 0) failures.Add($"pad weight / class mismatches: {padBad}");
    }

    // (f) throughput.
    static void Benchmark(RingTerrainSampler v4, RingTerrainSampler v5, EmitterSiteRegistry r, List<string> failures)
    {
        const int N = 100000;
        var rng = new Random(77);
        var randomPoints = new (long, int)[N];
        for (int i = 0; i < N; i++)
            randomPoints[i] = ((long)(rng.NextDouble() * (Circumference / 2)) * 2L, (int)(rng.NextDouble() * (Width / 2 - 1)) * 2);
        int inside = randomPoints.Count(p => r.TryGetInfluencingSite(p.Item1, p.Item2, out _, out _, out _));
        var sitePoints = new (long, int)[N];
        for (int i = 0; i < N; i++)
        {
            EmitterSite s = r[rng.Next(r.Count)];
            double rad = rng.NextDouble() * Math.Min(s.InfluenceRadius, 3000d), az = rng.NextDouble() * Math.PI * 2d;
            var p = ((s.Around + (long)(rad * Math.Cos(az) / 2d) * 2L), s.Across + (int)(rad * Math.Sin(az) / 2d) * 2);
            sitePoints[i] = (p.Item1, Math.Clamp(p.Item2, 0, Width - 2));
        }
        double Time(RingTerrainSampler sampler, (long, int)[] pts)
        {
            double best = double.MaxValue;
            for (int pass = 0; pass < 3; pass++)
            {
                var sw = Stopwatch.StartNew();
                float sink = 0f;
                foreach ((long a, int x) in pts) sink += sampler.GetPresentationSurfaceHeight(a, x);
                sw.Stop();
                if (float.IsNaN(sink)) throw new InvalidOperationException();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            return best;
        }
        double v4Random = Time(v4, randomPoints), v5Random = Time(v5, randomPoints);
        double v4Sites = Time(v4, sitePoints), v5Sites = Time(v5, sitePoints);
        double randomOverhead = (v5Random / v4Random - 1d) * 100d, siteOverhead = (v5Sites / v4Sites - 1d) * 100d;
        Console.WriteLine($"EMITTER_V5_PERF random_points={N} inside_influence={inside} v4_ms={v4Random:F1} v5_ms={v5Random:F1} overhead_pct={randomOverhead:F2} v4_us_per={v4Random * 1000d / N:F2} v5_us_per={v5Random * 1000d / N:F2}");
        Console.WriteLine($"EMITTER_V5_PERF site_area_points={N} v4_ms={v4Sites:F1} v5_ms={v5Sites:F1} overhead_pct={siteOverhead:F2}");
        if (randomOverhead > 10d) failures.Add($"random-point overhead {randomOverhead:F1}% exceeds 10%");
        if (siteOverhead > 25d) failures.Add($"site-area overhead {siteOverhead:F1}% exceeds 25%");
    }
}
