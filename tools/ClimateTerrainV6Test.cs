using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Phase C2 of docs/emitter-grid-climate-machines-spec.md: gen-17 v6 (ringworld_surface_climate_v6) forecourt shaping through the real
/// RingTerrainSampler. Covers hash determinism and distinctness, v4/v5 unchanged, registry sharing with worker copies, forecourt
/// flatness and cut/fill limits at all 200 machines (with quay and cut/fill-conflict statistics), shaped == natural beyond the influence
/// radius, C0 continuity at the influence boundary, disjointness from emitter-site influence, diagnostics/mask consistency and throughput.
/// </summary>
public partial class ClimateTerrainV6Test : RefCounted
{
    const long Circumference = EmitterSiteTestWorlds.Circumference;
    const int Width = EmitterSiteTestWorlds.Width;
    const int Seed = 1337;
    const ulong BaselineV4Hash = 0xC62D22E76ED02DFAUL;
    const ulong BaselineV5Hash = 0xCD0273AF4AF630D9UL;

    public int Run()
    {
        var failures = new List<string>();
        try
        {
            using RingTerrainSampler v4 = EmitterSiteTestWorlds.CreateGen17(Seed);
            using RingTerrainSampler v5 = EmitterSiteTestWorlds.CreateGen17Sites(Seed);

            EmitterSiteRegistry.ClearCache();
            ClimateMachineRegistry.ClearCache();
            EmitterSiteTerrainShaper.ClearCache();
            var cold = Stopwatch.StartNew();
            using RingTerrainSampler v6 = EmitterSiteTestWorlds.CreateGen17Climate(Seed);
            cold.Stop();
            ClimateMachineRegistry climate = v6.ClimateMachines ?? throw new InvalidOperationException("v6 sampler has no climate registry");
            EmitterSiteRegistry emitters = v6.EmitterSites ?? throw new InvalidOperationException("v6 sampler has no emitter registry");
            var warm = Stopwatch.StartNew();
            using RingTerrainSampler v6b = EmitterSiteTestWorlds.CreateGen17Climate(Seed);
            warm.Stop();
            Console.WriteLine($"CLIMATE_V6_BUILD sampler_ctor_cold_ms={cold.Elapsed.TotalMilliseconds:F0} climate_registry_build_ms={climate.BuildMilliseconds:F0} sampler_ctor_cached_ms={warm.Elapsed.TotalMilliseconds:F0}");

            CheckVersioning(v4, v5, v6, climate, emitters, failures);
            CheckSharing(v4, v5, v6, v6b, climate, emitters, failures);
            using var natural = new TerrainGenerationHeightModel(EmitterSiteTestWorlds.Gen17ClimateRecipe(Seed), false);
            CheckForecourts(v6, climate, emitters, natural, failures);
            CheckBeyondInfluenceAndBoundary(v4, v5, v6, climate, natural, failures);
            CheckDisjointFromEmitters(v6, climate, emitters, failures);
            CheckV5Unaffected(v4, v5, climate, failures);
            CheckCacheKeySource(failures);
            Benchmark(v5, v6, climate, failures);
            CheckColdRebuild(v6, climate, failures);
        }
        catch (Exception exception) { failures.Add(exception.ToString()); }

        foreach (string failure in failures) GD.PrintErr("CLIMATE_V6_FAIL " + failure);
        Console.WriteLine(failures.Count == 0 ? "CLIMATE_V6=PASS" : "CLIMATE_V6=FAIL failures=" + failures.Count);
        return failures.Count == 0 ? 0 : 1;
    }

    // ---- versioning, hash, identity ---------------------------------------------------------------------------

    static void CheckVersioning(RingTerrainSampler v4, RingTerrainSampler v5, RingTerrainSampler v6, ClimateMachineRegistry climate, EmitterSiteRegistry emitters, List<string> failures)
    {
        if (v4.TerrainGeneratorVersion != 17 || v5.TerrainGeneratorVersion != 17 || v6.TerrainGeneratorVersion != 17) failures.Add("generator version must stay 17");
        if (v4.TerrainDefinitionHash != BaselineV4Hash) failures.Add($"v4 hash {v4.TerrainDefinitionHash:X16} != baseline");
        if (v5.TerrainDefinitionHash != BaselineV5Hash) failures.Add($"v5 hash {v5.TerrainDefinitionHash:X16} != baseline");
        if (v6.TerrainDefinitionHash == v5.TerrainDefinitionHash || v6.TerrainDefinitionHash == v4.TerrainDefinitionHash) failures.Add("v6 hash equals v4/v5");
        if (v5.ClimateMachines != null || v4.ClimateMachines != null) failures.Add("v4/v5 sampler exposes a climate registry");
        if (!v6.SurfaceGenerationRecipeJson.Contains("ringworld_surface_climate_v6")) failures.Add("v6 recipe id missing from canonical json");
        TerrainGenerationParameters p5 = EmitterSiteTestWorlds.Gen17SitesRecipe(Seed), p6 = EmitterSiteTestWorlds.Gen17ClimateRecipe(Seed);
        if (p5.DefinitionHash == p6.DefinitionHash) failures.Add("recipe definition hashes equal");
        if (!p6.HasClimateMachines || !p6.HasEmitterSites || !p6.HasEnhancedErosion || !p6.HasMountainDetail || p5.HasClimateMachines) failures.Add("recipe feature flags");
        if (p5.CanonicalJson.Replace("ringworld_surface_sites_v5", "ringworld_surface_climate_v6") != p6.CanonicalJson) failures.Add("v6 recipe fields differ from v5 beyond the generator id");

        using (RingTerrainSampler again = EmitterSiteTestWorlds.CreateGen17Climate(Seed))
            if (again.TerrainDefinitionHash != v6.TerrainDefinitionHash) failures.Add("v6 hash is not deterministic");
        using (RingTerrainSampler other = EmitterSiteTestWorlds.CreateGen17Climate(Seed + 1))
            if (other.TerrainDefinitionHash == v6.TerrainDefinitionHash) failures.Add("v6 hash ignores the seed");

        EmitterSiteRegistry e5 = v5.EmitterSites!;
        bool sameSites = e5.Count == emitters.Count;
        for (int i = 0; sameSites && i < e5.Count; i++)
            sameSites = e5[i].Around == emitters[i].Around && e5[i].Across == emitters[i].Across && e5[i].PadHeight == emitters[i].PadHeight && e5[i].Flags == emitters[i].Flags;
        if (!sameSites) failures.Add("v6 emitter sites differ from v5 (natural terrain must be shared)");
        if (climate.EmitterIdentity != emitters.Identity) failures.Add("climate registry is not locked to the emitter registry");
        Console.WriteLine($"CLIMATE_V6_HASH v4={v4.TerrainDefinitionHash:X16} v5={v5.TerrainDefinitionHash:X16} v6={v6.TerrainDefinitionHash:X16} v6_recipe={p6.DefinitionHash:X16} climate_recipe={climate.Recipe.Hash:X16} algo={climate.Recipe.AlgorithmVersion} operator={ClimateForecourtOperator.OperatorVersion} registry={climate.Identity:X16}");
    }

    // Last: clears the shared caches, so every reference captured earlier is stale afterwards.
    static void CheckColdRebuild(RingTerrainSampler v6, ClimateMachineRegistry climate, List<string> failures)
    {
        EmitterSiteRegistry.ClearCache();
        ClimateMachineRegistry.ClearCache();
        EmitterSiteTerrainShaper.ClearCache();
        using (RingTerrainSampler rebuilt = EmitterSiteTestWorlds.CreateGen17Climate(Seed))
        {
            if (rebuilt.TerrainDefinitionHash != v6.TerrainDefinitionHash) failures.Add("v6 hash changes after a cold registry rebuild");
            if (rebuilt.ClimateMachines!.Identity != climate.Identity) failures.Add("climate registry identity changes after a cold rebuild");
            if (!rebuilt.ClimateMachines.ToCanonicalBytes().AsSpan().SequenceEqual(climate.ToCanonicalBytes())) failures.Add("climate registry bytes change after a cold rebuild");
        }
    }

    static void CheckSharing(RingTerrainSampler v4, RingTerrainSampler v5, RingTerrainSampler v6, RingTerrainSampler v6b, ClimateMachineRegistry climate, EmitterSiteRegistry emitters, List<string> failures)
    {
        if (!ReferenceEquals(climate, v6b.ClimateMachines)) failures.Add("second sampler for the same definition rebuilt the climate registry");
        if (!ReferenceEquals(emitters, v6b.EmitterSites)) failures.Add("second sampler rebuilt the emitter registry");
        using RingTerrainSampler copy = v6.CreateIndependentCopy();
        if (!ReferenceEquals(climate, copy.ClimateMachines)) failures.Add("independent copy does not share the climate registry instance");
        if (!ReferenceEquals(emitters, copy.EmitterSites)) failures.Add("independent copy does not share the emitter registry instance");
        if (copy.TerrainDefinitionHash != v6.TerrainDefinitionHash) failures.Add("independent copy hash differs");
        // Worker copies on separate threads agree with the root sampler at every machine pad.
        var errors = new int[1];
        Parallel.For(0, 8, worker =>
        {
            using RingTerrainSampler mine = v6.CreateIndependentCopy();
            if (!ReferenceEquals(climate, mine.ClimateMachines)) Interlocked(ref errors[0]);
            for (int i = worker; i < climate.Count; i += 8)
            {
                ClimateMachine m = climate[i];
                int across = m.Side == 0 ? 1_000 : Width - 1_002;
                if (mine.GetPresentationSurfaceHeight(m.Around, across) != v6.GetPresentationSurfaceHeight(m.Around, across)) Interlocked(ref errors[0]);
            }
        });
        if (errors[0] != 0) failures.Add($"{errors[0]} worker-copy disagreements");
        using RingTerrainSampler seedOther = EmitterSiteTestWorlds.CreateGen17Climate(Seed + 1);
        if (ReferenceEquals(seedOther.ClimateMachines, climate)) failures.Add("a different seed shares the climate registry");
        Console.WriteLine($"CLIMATE_V6_SHARING shared_registry=true worker_copies=8 disagreements={errors[0]}");
    }

    static void Interlocked(ref int value) => System.Threading.Interlocked.Increment(ref value);

    // ---- forecourt flatness and limits at all 200 machines -----------------------------------------------------

    sealed class Stats
    {
        public int Machines, PadPoints, PadMismatches, RegionPoints, Changed, Cut, Fill;
        public double MaxCut, MaxFill, MaxPadDelta, MaxSlopeDeg, MaxNaturalSlopeDeg, MaxConeViolation, SumCut, SumFill;
        public int Wet, Steep60, NaturalSteep60;
    }

    static void CheckForecourts(RingTerrainSampler v6, ClimateMachineRegistry climate, EmitterSiteRegistry emitters, TerrainGenerationHeightModel natural, List<string> failures)
    {
        ClimateForecourtOperator op = v6.ClimateForecourtOperator ?? throw new InvalidOperationException("v6 sampler has no forecourt operator");
        var groups = new Dictionary<string, Stats>
        {
            ["all"] = new(), ["quay"] = new(), ["dry"] = new(), ["conflict"] = new(), ["quay_conflict"] = new(), ["clean_dry"] = new(), ["offline"] = new(),
        };
        int mismatchTotal = 0, maskBad = 0, indexBad = 0, coneBad = 0;
        var worst = new List<(int Index, double Cut, double Fill, double Slope, bool Quay, bool Conflict)>();
        double halfSeg = climate.Recipe.ForecourtLengthM * 0.5d;
        double flatR = op.FlatRadiusM, influence = op.InfluenceRadiusM;
        for (int mi = 0; mi < climate.Count; mi++)
        {
            ClimateMachine m = climate[mi];
            bool quay = (m.Flags & ClimateMachineFlags.Quay) != 0, conflict = (m.Flags & ClimateMachineFlags.CutFillConflict) != 0;
            var tags = new List<string> { "all", quay ? "quay" : "dry" };
            if (conflict) tags.Add("conflict");
            if (quay && conflict) tags.Add("quay_conflict");
            if (!quay && !conflict) tags.Add("clean_dry");
            if (m.IsOffline) tags.Add("offline");
            var local = new Stats();
            var rng = new Random(9100 + mi);
            // Pad: dense random points plus the exact edge ring, the segment ends and the wall line.
            var points = new List<(double U, double W)>();
            for (int i = 0; i < 400; i++)
            {
                double u = (rng.NextDouble() * 2d - 1d) * halfSeg;
                double w = rng.NextDouble() * flatR;
                points.Add((u, w));
                points.Add(((rng.NextDouble() < 0.5 ? -1d : 1d) * (halfSeg + rng.NextDouble() * flatR), rng.NextDouble() * flatR));
            }
            for (int i = 0; i < 64; i++) { double a = Math.PI * i / 63d; points.Add((Math.Sign(i - 31.5) * halfSeg + flatR * Math.Cos(a) * Math.Sign(i - 31.5), flatR * Math.Sin(a))); }
            points.Add((0, 0)); points.Add((halfSeg, 0)); points.Add((-halfSeg, 0)); points.Add((0, flatR - 2));
            foreach ((double u, double w) in points)
            {
                (long around, int across) = ToCanonical(m, u, w);
                if (op.FootprintDistance(Rel(m, around, across).U, Rel(m, around, across).W) > flatR) continue;
                float h = v6.GetPresentationSurfaceHeight(around, across);
                local.PadPoints++;
                if (h != m.PadHeight) { local.PadMismatches++; }
                local.MaxPadDelta = Math.Max(local.MaxPadDelta, Math.Abs(h - m.PadHeight));
                double nh = natural.SampleNaturalDiagnostics(around, across).Height;
                if (nh < 0d) local.Wet++;
                TerrainHeightAndMask(v6, around, across, out SiteSurfaceMask mask, out int machineIndex, out _);
                if (machineIndex != mi) indexBad++;
                if (!mask.BlocksVegetation || mask.Class != SiteSurfaceClass.Pad) maskBad++;
                TerrainSample sample = v6.GetTerrainSample(around, across);
                if (!sample.SiteMask.BlocksVegetation) maskBad++;
            }
            // Region: cut/fill, cone limits, gradient.
            for (int i = 0; i < 3000; i++)
            {
                double u = (rng.NextDouble() * 2d - 1d) * (halfSeg + influence);
                double w = rng.NextDouble() * influence;
                (long around, int across) = ToCanonical(m, u, w);
                (double ru, double rw) = Rel(m, around, across);
                double s = op.FootprintDistance(ru, rw);
                if (s >= influence) continue;
                if (across < 0 || across > Width - 2) continue;
                double shaped = v6.GetPresentationSurfaceHeight(around, across);
                TerrainGenerationHeightModel.Diagnostics nd = natural.SampleNaturalDiagnostics(around, across);
                double delta = shaped - nd.Height;
                local.RegionPoints++;
                if (Math.Abs(delta) > 1e-3) local.Changed++;
                if (delta < -1e-3) { local.Cut++; local.SumCut += -delta; local.MaxCut = Math.Max(local.MaxCut, -delta); }
                if (delta > 1e-3) { local.Fill++; local.SumFill += delta; local.MaxFill = Math.Max(local.MaxFill, delta); }
                if (s > flatR && s <= influence - 4_000d)
                {
                    // Cone limits (soft clamp tolerance: the emitter operator's soft width plus float rounding).
                    double rho = s - flatR;
                    double upper = m.PadHeight + op.TerraceHeight(rho) + 30d, lower = m.PadHeight - Math.Tan(33d * Math.PI / 180d) * rho - 30d;
                    double slack = 0d;
                    if (shaped > upper) slack = shaped - upper; else if (shaped < lower) slack = lower - shaped;
                    // The quay raise may legitimately sit above the lower bound only; below-lower cannot happen (max-only), above-upper cannot happen (cut clamp).
                    if (slack > 0.5d) { coneBad++; local.MaxConeViolation = Math.Max(local.MaxConeViolation, slack); }
                }
                // Gradient against the 2 m neighbours.
                double hx = v6.GetPresentationSurfaceHeight(around + 2, across), hz = across + 2 <= Width - 2 ? v6.GetPresentationSurfaceHeight(around, across + 2) : shaped;
                double slope = Math.Atan(Math.Max(Math.Abs(hx - shaped), Math.Abs(hz - shaped)) / 2d) * 180d / Math.PI;
                local.MaxSlopeDeg = Math.Max(local.MaxSlopeDeg, slope);
                double nx = natural.SampleNaturalDiagnostics(around + 2, across).Height, nz = across + 2 <= Width - 2 ? natural.SampleNaturalDiagnostics(around, across + 2).Height : nd.Height;
                double nslope = Math.Atan(Math.Max(Math.Abs(nx - nd.Height), Math.Abs(nz - nd.Height)) / 2d) * 180d / Math.PI;
                local.MaxNaturalSlopeDeg = Math.Max(local.MaxNaturalSlopeDeg, nslope);
                if (slope > 60d) local.Steep60++;
                if (nslope > 60d) local.NaturalSteep60++;
            }
            foreach (string tag in tags)
            {
                Stats g = groups[tag];
                g.Machines++; g.PadPoints += local.PadPoints; g.PadMismatches += local.PadMismatches; g.RegionPoints += local.RegionPoints;
                g.Changed += local.Changed; g.Cut += local.Cut; g.Fill += local.Fill; g.SumCut += local.SumCut; g.SumFill += local.SumFill;
                g.MaxCut = Math.Max(g.MaxCut, local.MaxCut); g.MaxFill = Math.Max(g.MaxFill, local.MaxFill); g.MaxPadDelta = Math.Max(g.MaxPadDelta, local.MaxPadDelta);
                g.MaxSlopeDeg = Math.Max(g.MaxSlopeDeg, local.MaxSlopeDeg); g.MaxConeViolation = Math.Max(g.MaxConeViolation, local.MaxConeViolation);
                g.Wet += local.Wet; g.Steep60 += local.Steep60; g.NaturalSteep60 += local.NaturalSteep60; g.MaxNaturalSlopeDeg = Math.Max(g.MaxNaturalSlopeDeg, local.MaxNaturalSlopeDeg);
            }
            mismatchTotal += local.PadMismatches;
            worst.Add((mi, local.MaxCut, local.MaxFill, local.MaxSlopeDeg, quay, conflict));
        }
        foreach ((string tag, Stats g) in groups)
            Console.WriteLine($"CLIMATE_V6_FORECOURT group={tag} machines={g.Machines} pad_points={g.PadPoints} pad_mismatches={g.PadMismatches} pad_max_delta={g.MaxPadDelta:F3} wet_natural_pad_points={g.Wet} region_points={g.RegionPoints} changed={g.Changed} cut_pts={g.Cut} fill_pts={g.Fill} max_cut_m={g.MaxCut:F1} max_fill_m={g.MaxFill:F1} mean_cut_m={(g.Cut == 0 ? 0 : g.SumCut / g.Cut):F1} mean_fill_m={(g.Fill == 0 ? 0 : g.SumFill / g.Fill):F1} max_slope_deg={g.MaxSlopeDeg:F1} natural_max_slope_deg={g.MaxNaturalSlopeDeg:F1} steep60_shaped={g.Steep60} steep60_natural={g.NaturalSteep60} cone_violation_m={g.MaxConeViolation:F2}");
        foreach (var w in worst.OrderByDescending(x => x.Cut).Take(6))
            Console.WriteLine($"CLIMATE_V6_WORST_CUT machine={w.Index} pad={climate[w.Index].PadHeight} max_cut_m={w.Cut:F0} max_fill_m={w.Fill:F0} max_slope_deg={w.Slope:F1} quay={w.Quay} conflict={w.Conflict}");
        var cutBuckets = new[] { 100, 300, 600, 1000, 2000 };
        Console.WriteLine("CLIMATE_V6_CUT_HIST machines_with_max_cut_over_m " + string.Join(" ", cutBuckets.Select(b => $"{b}={worst.Count(x => x.Cut > b)}")) + " | max_fill_over_m " + string.Join(" ", cutBuckets.Select(b => $"{b}={worst.Count(x => x.Fill > b)}")));
        PrintProfiles(v6, climate, natural, worst.OrderByDescending(x => x.Cut).First().Index, worst.Where(x => x.Quay).OrderByDescending(x => x.Fill).First().Index, worst.Where(x => !x.Quay && !x.Conflict).First().Index);
        if (groups["all"].Machines != 200) failures.Add($"forecourt stats cover {groups["all"].Machines} machines");
        if (mismatchTotal != 0) failures.Add($"{mismatchTotal} pad points differ from the pad height");
        if (maskBad != 0) failures.Add($"{maskBad} pad points do not block vegetation");
        if (indexBad != 0) failures.Add($"{indexBad} pad points report the wrong ClimateMachineIndex");
        if (coneBad != 0) failures.Add($"{coneBad} region points violate the cone limits");
        if (groups["quay"].Machines == 0 || groups["conflict"].Machines == 0) failures.Add("seed 1337 no longer exercises quay or cut/fill-conflict machines");
        // Quay forecourts must be dry above sea level everywhere on the pad.
        if (groups["quay"].PadPoints > 0 && groups["quay"].MaxPadDelta != 0d) failures.Add("quay pad not flat");
        if (climate.Machines.ToArray().Any(m => m.PadHeight < 8)) failures.Add("a pad is below the 8 m minimum");
    }

    /// <summary>Perpendicular profile (along = 0) of the worst-cut machine, the highest quay plinth and a clean dry machine, every 400 m, natural vs shaped.</summary>
    static void PrintProfiles(RingTerrainSampler v6, ClimateMachineRegistry climate, TerrainGenerationHeightModel natural, params int[] machines)
    {
        foreach (int mi in machines)
        {
            ClimateMachine m = climate[mi];
            var parts = new List<string>();
            for (int s = 0; s <= 8400; s += 400)
            {
                (long around, int across) = ToCanonical(m, 0, s);
                parts.Add($"{s}:{natural.SampleNaturalDiagnostics(around, across).Height:F0}/{v6.GetPresentationSurfaceHeight(around, across):F0}");
            }
            Console.WriteLine($"CLIMATE_V6_PROFILE machine={mi} pad={m.PadHeight} flags={m.Flags} dist_m:natural/shaped " + string.Join(" ", parts));
        }
    }

    /// <summary>Signed along-ring offset and across distance from the wall of a canonical point, relative to the machine.</summary>
    static (double U, double W) Rel(ClimateMachine m, long around, int across)
    {
        long d = ((around - m.Around) % Circumference + Circumference) % Circumference;
        if (d > Circumference / 2) d -= Circumference;
        return (d, Math.Abs(across - m.Across));
    }

    static (long Around, int Across) ToCanonical(ClimateMachine m, double u, double w)
    {
        long around = (m.Around + (long)Math.Round(u / 2d) * 2L) % Circumference;
        if (around < 0) around += Circumference;
        int across = m.Side == 0 ? (int)Math.Round(w / 2d) * 2 : Width - (int)Math.Round(w / 2d) * 2;
        return (around, Math.Clamp(across, 0, Width - 2));
    }

    static void TerrainHeightAndMask(RingTerrainSampler sampler, long around, int across, out SiteSurfaceMask mask, out int machineIndex, out double weight)
        => sampler.TryGetClimateMachineSurfaceMask(around, across, out mask, out machineIndex, out weight);

    // ---- beyond influence, boundary continuity -----------------------------------------------------------------

    static void CheckBeyondInfluenceAndBoundary(RingTerrainSampler v4, RingTerrainSampler v5, RingTerrainSampler v6, ClimateMachineRegistry climate, TerrainGenerationHeightModel natural, List<string> failures)
    {
        ClimateForecourtOperator op = v6.ClimateForecourtOperator!;
        double halfSeg = climate.Recipe.ForecourtLengthM * 0.5d, influence = op.InfluenceRadiusM;
        int beyond = 0, beyondBad = 0, boundary = 0; double boundaryMax = 0d, boundaryMaxQuay = 0d;
        for (int mi = 0; mi < climate.Count; mi++)
        {
            ClimateMachine m = climate[mi];
            bool quay = (m.Flags & ClimateMachineFlags.Quay) != 0;
            var rng = new Random(777 + mi);
            for (int i = 0; i < 400; i++)
            {
                // Outside the influence region (but near it, and farther out): shaped == natural bit-exact, and v5 == v6 there.
                double u = (rng.NextDouble() * 2d - 1d) * (halfSeg + influence + 30_000d);
                double w = rng.NextDouble() * (influence + 30_000d);
                double s = op.FootprintDistance(u, w);
                if (s < influence + 2d) continue;
                (long around, int across) = ToCanonical(m, u, w);
                if (across < 0 || across > Width - 2) continue;
                if (v6.EmitterSites!.TryGetInfluencingSite(around, across, out _, out _, out _)) continue;
                beyond++;
                float h6 = v6.GetPresentationSurfaceHeight(around, across), h5 = v5.GetPresentationSurfaceHeight(around, across);
                double nh = natural.SampleNaturalDiagnostics(around, across).Height;
                if (h6 != (float)nh || h6 != h5 || v4.GetPresentationSurfaceHeight(around, across) != h5) beyondBad++;
            }
            // Continuity: points 2 m and 8 m inside the influence boundary (side band and both end caps) must be within a few metres of natural.
            for (int b = 0; b < 24; b++)
            {
                double theta = b < 12 ? Math.PI / 2d : Math.PI * (b - 11) / 26d;
                foreach (double inside in new[] { 2d, 8d })
                {
                    double s = influence - inside;
                    double sign = b % 2 == 0 ? 1d : -1d;
                    double u = b < 12 ? (rng.NextDouble() * 2d - 1d) * halfSeg : sign * (halfSeg + s * Math.Cos(theta)), w = s * Math.Sin(theta);
                    (long around, int across) = ToCanonical(m, u, w);
                    if (across < 0 || across > Width - 2) continue;
                    if (v6.EmitterSites!.TryGetInfluencingSite(around, across, out _, out _, out _)) continue;
                    double delta = Math.Abs(v6.GetPresentationSurfaceHeight(around, across) - natural.SampleNaturalDiagnostics(around, across).Height);
                    boundary++;
                    boundaryMax = Math.Max(boundaryMax, delta);
                    if (quay) boundaryMaxQuay = Math.Max(boundaryMaxQuay, delta);
                }
            }
        }
        Console.WriteLine($"CLIMATE_V6_BEYOND points={beyond} mismatches={beyondBad} boundary_points={boundary} boundary_max_delta_m={boundaryMax:F3} boundary_max_delta_quay_m={boundaryMaxQuay:F3}");
        if (beyondBad != 0) failures.Add($"{beyondBad} points beyond the influence radius differ from natural/v5/v4");
        if (boundaryMax > 3d) failures.Add($"height jumps {boundaryMax:F2} m within 2 m of the influence boundary");
    }

    // ---- disjoint from emitter site influence ------------------------------------------------------------------

    static void CheckDisjointFromEmitters(RingTerrainSampler v6, ClimateMachineRegistry climate, EmitterSiteRegistry emitters, List<string> failures)
    {
        var rng = new Random(31337);
        int both = 0, diagnosticsBad = 0, checkedPoints = 0, hits = 0;
        double halfSeg = climate.Recipe.ForecourtLengthM * 0.5d;
        // Every machine's whole region against the brute-force emitter list, plus random ring points through the real diagnostics.
        for (int mi = 0; mi < climate.Count; mi++)
        {
            ClimateMachine m = climate[mi];
            for (int i = 0; i < 300; i++)
            {
                double u = (rng.NextDouble() * 2d - 1d) * (halfSeg + m.InfluenceRadius), w = rng.NextDouble() * m.InfluenceRadius;
                (long around, int across) = ToCanonical(m, u, w);
                if (emitters.TryGetInfluencingSite(around, across, out _, out _, out _)) both++;
                checkedPoints++;
            }
        }
        // The two registries' bounding stations: nearest emitter site centre to any machine footprint must clear the sum of the reaches.
        double minClear = double.MaxValue;
        foreach (ClimateMachine m in climate.Machines)
            foreach (EmitterSite site in emitters.Sites)
            {
                double dAround = Math.Abs(m.Around - site.Around); dAround = Math.Min(dAround, Circumference - dAround);
                double dx = Math.Max(0d, dAround - halfSeg), dw = Math.Abs(m.Across - site.Across);
                minClear = Math.Min(minClear, Math.Sqrt(dx * dx + dw * dw) - site.InfluenceRadius - m.InfluenceRadius);
            }
        if (minClear <= 0d) both++;
        for (int i = 0; i < 400_000; i++)
        {
            long around = (long)(rng.NextDouble() * (Circumference / 2)) * 2L;
            int across = (int)(rng.NextDouble() * (Width / 2 - 1)) * 2;
            TerrainGenerationHeightModel.Diagnostics d = SampleDiagnostics(v6, around, across);
            if (d.ClimateMachineIndex >= 0) hits++;
            if (d.ClimateMachineIndex >= 0 && d.SiteIndex >= 0) diagnosticsBad++;
            bool emitterHit = emitters.TryGetInfluencingSite(around, across, out _, out _, out _);
            if (emitterHit != (d.SiteIndex >= 0)) diagnosticsBad++;
        }
        Console.WriteLine($"CLIMATE_V6_DISJOINT machine_region_points={checkedPoints} overlapping_points={both} min_clearance_to_nearest_site_m={minClear:F0} diagnostics_points=400000 climate_hits={hits} diagnostics_conflicts={diagnosticsBad}");
        if (both != 0) failures.Add($"{both} machine-region points also lie in an emitter site's influence");
        if (diagnosticsBad != 0) failures.Add($"{diagnosticsBad} diagnostics conflicts between emitter sites and climate machines");
    }

    static TerrainGenerationHeightModel.Diagnostics SampleDiagnostics(RingTerrainSampler sampler, long around, int across)
    {
        // The root sampler exposes diagnostics only through the surface mask accessors; use the copy-safe path through a model on the same parameters.
        return ModelFor(sampler).SampleDiagnostics(around, across);
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RingTerrainSampler, TerrainGenerationHeightModel> Models = new();
    static TerrainGenerationHeightModel ModelFor(RingTerrainSampler sampler)
        => Models.GetValue(sampler, s => new TerrainGenerationHeightModel(TerrainGenerationParameters.FromJson(s.SurfaceGenerationRecipeJson)));

    // ---- v5 / v4 unaffected near machines ----------------------------------------------------------------------

    static void CheckV5Unaffected(RingTerrainSampler v4, RingTerrainSampler v5, ClimateMachineRegistry climate, List<string> failures)
    {
        var rng = new Random(5150);
        int points = 0, bad = 0;
        double halfSeg = climate.Recipe.ForecourtLengthM * 0.5d;
        for (int mi = 0; mi < climate.Count; mi++)
        {
            ClimateMachine m = climate[mi];
            for (int i = 0; i < 100; i++)
            {
                double u = (rng.NextDouble() * 2d - 1d) * (halfSeg + 4_000d), w = rng.NextDouble() * 6_000d;
                (long around, int across) = ToCanonical(m, u, w);
                points++;
                if (v5.GetPresentationSurfaceHeight(around, across) != v4.GetPresentationSurfaceHeight(around, across)) bad++;
            }
        }
        Console.WriteLine($"CLIMATE_V6_V5_UNCHANGED machine_region_points={points} v5_vs_v4_mismatches={bad}");
        if (bad != 0) failures.Add($"{bad} v5 points inside machine regions differ from v4");
    }

    static void CheckCacheKeySource(List<string> failures)
    {
        string path = ProjectSettings.GlobalizePath("res://scripts/Voxel/LOD/RingBiomeDecorationLayer.TreeStreaming.cs");
        string text = File.ReadAllText(path);
        if (!text.Contains("|es1") || !text.Contains("sampler.ClimateMachines != null ? \"|cm1\"")) failures.Add("tree disk-cache key does not carry the climate identity (|cm1)");
    }

    // ---- throughput --------------------------------------------------------------------------------------------

    static void Benchmark(RingTerrainSampler v5, RingTerrainSampler v6, ClimateMachineRegistry climate, List<string> failures)
    {
        var rng = new Random(99);
        var pts = new (long, int)[100_000];
        for (int i = 0; i < pts.Length; i++) pts[i] = ((long)(rng.NextDouble() * (Circumference / 2)) * 2L, (int)(rng.NextDouble() * (Width / 2 - 1)) * 2);
        double Time(RingTerrainSampler s)
        {
            float sink = 0;
            var w = Stopwatch.StartNew();
            foreach ((long a, int x) in pts) sink += s.GetPresentationSurfaceHeight(a, x);
            w.Stop();
            GC.KeepAlive(sink);
            return w.Elapsed.TotalMilliseconds;
        }
        Time(v5); Time(v6);
        double t5 = Math.Min(Time(v5), Time(v5)), t6 = Math.Min(Time(v6), Time(v6));
        Console.WriteLine($"CLIMATE_V6_PERF random_points={pts.Length} v5_ms={t5:F1} v6_ms={t6:F1} overhead_pct={(t6 / t5 - 1d) * 100d:F1}");
        if (t6 > t5 * 1.25d) failures.Add($"v6 random-point overhead over v5 is {(t6 / t5 - 1d) * 100d:F1}% (limit 25%)");
    }
}
