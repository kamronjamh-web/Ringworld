using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Stage S4b of docs/weather-lung-integration-plan.md: gen-17 v8 (ringworld_surface_weather_lung_v8) through the real RingTerrainSampler. Covers hash
/// determinism and distinctness (v7 untouched), the v7 field set and feature flags, the basin being Ocean at sea level 0 for r &lt; 0.05 and dry (&gt;= 1 m) for r &gt;= 0.15,
/// v8 == v7 beyond the influence, mask and diagnostics consistency, worker copies sharing the placement, the climate_machines toggle not moving the placement,
/// and the frame round trip through <c>EmitterSiteFrameContext.SiteTransform</c> (the sign conventions the world streamer relies on).
/// </summary>
public partial class WeatherLungTerrainV8Test : RefCounted
{
    const long Circumference = EmitterSiteTestWorlds.Circumference;
    static readonly int[] Seeds = { 1337, 1, 42 };

    public int Run()
    {
        var failures = new List<string>();
        try
        {
            CheckRecipes(failures);
            foreach (int seed in Seeds) CheckWorld(seed, seed == 1337, failures);
        }
        catch (Exception exception) { failures.Add(exception.ToString()); }
        foreach (string failure in failures) GD.PrintErr("WEATHER_LUNG_V8_FAIL " + failure);
        Console.WriteLine(failures.Count == 0 ? "WEATHER_LUNG_V8=PASS" : "WEATHER_LUNG_V8=FAIL failures=" + failures.Count);
        return failures.Count == 0 ? 0 : 1;
    }

    static void CheckRecipes(List<string> failures)
    {
        TerrainGenerationParameters v7 = TerrainGenerationParameters.LandformDefault();
        TerrainGenerationParameters v8 = TerrainGenerationParameters.WeatherLungDefault();
        TerrainGenerationParameters v8b = EmitterSiteTestWorlds.Gen17WeatherLungRecipe(1337);
        if (v8.GeneratorId != TerrainGenerationParameters.WeatherLungGeneratorIdentity || v8.GeneratorId != "ringworld_surface_weather_lung_v8") failures.Add("v8 generator id");
        if (v8.DefinitionHash == v7.DefinitionHash || v8.CanonicalJson == v7.CanonicalJson) failures.Add("v8 recipe equals v7");
        if (v8.DefinitionHash != v8b.DefinitionHash) failures.Add("v8 recipe hash not deterministic");
        if (v8.CanonicalJson.Replace("weather_lung_v8", "landform_v7") != v7.CanonicalJson) failures.Add("v8 field set differs from v7");
        if (!v8.HasWeatherLung || v7.HasWeatherLung || TerrainGenerationParameters.ClimateDefault().HasWeatherLung || TerrainGenerationParameters.SitesDefault().HasWeatherLung) failures.Add("HasWeatherLung flags");
        if (!v8.HasEmitterSites || !v8.HasLowlandLandforms || !v8.HasMountainDetail || !v8.HasEnhancedErosion || v8.HasClimateMachines) failures.Add("v8 inherited v7 predicates");
        if (!EmitterSiteTestWorlds.Gen17WeatherLungRecipe(1337, true).HasClimateMachines) failures.Add("v8 climate_machines=1 flag");
        try
        {
            TerrainGenerationParameters.FromJson(v8.CanonicalJson.Replace("\"gully_normalization\":0.6,", ""));
            failures.Add("v8 accepted an incomplete field set");
        }
        catch (ArgumentException) { }
        Console.WriteLine($"WEATHER_LUNG_V8_RECIPE v7_hash={v7.DefinitionHash:X16} v8_hash={v8.DefinitionHash:X16}");
    }

    static void CheckWorld(int seed, bool full, List<string> failures)
    {
        EmitterSiteRegistry.ClearCache();
        ClimateMachineRegistry.ClearCache();
        EmitterSiteTerrainShaper.ClearCache();
        WeatherLungPlacement.ClearCache();
        using RingTerrainSampler v7 = EmitterSiteTestWorlds.CreateGen17Landform(seed);
        var cold = System.Diagnostics.Stopwatch.StartNew();
        using RingTerrainSampler v8 = EmitterSiteTestWorlds.CreateGen17WeatherLung(seed);
        cold.Stop();
        WeatherLungPlacement lung = v8.WeatherLung ?? throw new InvalidOperationException("v8 sampler has no Weather Lung placement");
        WeatherLungBasinOperator op = v8.WeatherLungOperator ?? throw new InvalidOperationException("v8 sampler has no basin operator");
        EmitterSiteRegistry emitters = v8.EmitterSites ?? throw new InvalidOperationException("v8 sampler has no emitter registry");
        Console.WriteLine(lung.Describe("v8natural"));
        Console.WriteLine($"WEATHER_LUNG_V8_BUILD seed={seed} sampler_ctor_cold_ms={cold.Elapsed.TotalMilliseconds:F0} placement_ms={lung.BuildMilliseconds:F0} candidates={lung.CandidatesEvaluated} disjoint={lung.CandidatesDisjoint}");
        WeatherLungSite site = lung.Site;

        // ---- versioning: v7 has no Weather Lung, the hash is deterministic and distinct, v8 placement equals the v7-natural probe ----
        if (v7.WeatherLung != null || v7.WeatherLungOperator != null) failures.Add($"seed {seed}: v7 exposes a Weather Lung");
        if (v8.TerrainDefinitionHash == v7.TerrainDefinitionHash) failures.Add($"seed {seed}: v8 and v7 sampler hashes are equal");
        using (RingTerrainSampler again = EmitterSiteTestWorlds.CreateGen17WeatherLung(seed))
        {
            if (again.TerrainDefinitionHash != v8.TerrainDefinitionHash) failures.Add($"seed {seed}: v8 sampler hash not deterministic");
            if (!ReferenceEquals(again.WeatherLung, lung)) failures.Add($"seed {seed}: second v8 sampler built a separate placement");
        }
        {
            TerrainGenerationParameters recipe7 = EmitterSiteTestWorlds.Gen17LandformRecipe(seed);
            using var source = new Gen17NaturalHeightSource(recipe7, recipe7.DefinitionHash);
            EmitterSiteRegistry e7 = EmitterSiteRegistry.Build(EmitterSiteRecipe.Default(seed), source);
            WeatherLungPlacement probe = WeatherLungPlacement.Build(WeatherLungRecipe.For(e7.Recipe), e7, ClimateMachineRecipe.For(e7.Recipe), source);
            if (probe.Site.Around != site.Around || probe.Site.Across != site.Across || probe.Site.SiteHash != site.SiteHash)
                failures.Add($"seed {seed}: v8 placement differs from the v7-natural probe ({probe.Site.Around}/{probe.Site.Across} vs {site.Around}/{site.Across})");
        }
        if (v8.ClimateMachines != null) failures.Add($"seed {seed}: v8 with climate_machines=0 exposes a climate registry");
        if (full)
        {
            using RingTerrainSampler withClimate = EmitterSiteTestWorlds.CreateGen17WeatherLung(seed, true);
            if (withClimate.ClimateMachines == null || withClimate.ClimateMachines.Count != 200) failures.Add("v8 + climate lacks the 200-machine registry");
            if (withClimate.WeatherLung == null || withClimate.WeatherLung.Site.SiteHash != site.SiteHash) failures.Add("climate_machines toggle moved the Weather Lung placement");
            if (withClimate.TerrainDefinitionHash == v8.TerrainDefinitionHash) failures.Add("climate toggle does not change the v8 hash");
        }
        using (RingTerrainSampler copy = v8.CreateIndependentCopy())
        {
            if (!ReferenceEquals(copy.WeatherLung, lung) || !ReferenceEquals(copy.EmitterSites, emitters)) failures.Add($"seed {seed}: worker copy does not share the placement");
            if (copy.TerrainDefinitionHash != v8.TerrainDefinitionHash) failures.Add($"seed {seed}: worker copy hash differs");
        }

        // ---- sampler behaviour on the basin ----
        var rng = new Random(seed * 31 + 5);
        int oceanBad = 0, dryBad = 0, oceanN = 0, dryN = 0, maskBad = 0, equalBad = 0, equalN = 0, insideDiff = 0;
        int samples = full ? 6000 : 1500;
        double maxPlinthBuilt = 0d;
        for (int i = 0; i < samples; i++)
        {
            double du = (rng.NextDouble() - 0.5d) * 2d * 22_000d, dw = (rng.NextDouble() - 0.5d) * 2d * 17_000d;
            long around = Wrap(site.Around + (long)Math.Round(du / 2d) * 2L);
            int across = site.Across + (int)Math.Round(dw / 2d) * 2;
            double ru = Offset(around, site.Around), rw = across - site.Across;
            WeatherLungBasinOperator.ToAssetLocal(in site, ru, rw, out double x, out double y);
            op.Locate(x, y, out double r, out double s);
            TerrainSample t8 = v8.GetTerrainSample(around, across);
            if (r < 0.05d)
            {
                oceanN++;
                if (!t8.IsOcean || t8.WaterSurfaceHeight != 0) oceanBad++;
            }
            else if (r >= 0.15d && s <= op.ApronM)
            {
                dryN++;
                if (t8.HasSurfaceWater || t8.SurfaceHeight < 1 || !t8.SiteMask.BlocksVegetation || t8.SiteMask.VegetationAllowed) dryBad++;
                maxPlinthBuilt = Math.Max(maxPlinthBuilt, t8.SurfaceHeight);
            }
            if (s >= op.InfluenceM || Math.Abs(x) >= lung.Recipe.BoundHalfXM || Math.Abs(y) >= lung.Recipe.BoundHalfYM)
            {
                equalN++;
                if (v8.GetSurfaceHeight(around, across) != v7.GetSurfaceHeight(around, across)) equalBad++;
                if (t8.SiteMask.BlocksVegetation != v7.GetTerrainSample(around, across).SiteMask.BlocksVegetation) maskBad++;
            }
            else if (s < op.InfluenceM - 1d && v8.GetSurfaceHeight(around, across) != v7.GetSurfaceHeight(around, across)) insideDiff++;
        }
        if (oceanN == 0 || dryN == 0) failures.Add($"seed {seed}: no samples in the basin ({oceanN}) or on the pad ({dryN})");
        if (oceanBad != 0) failures.Add($"seed {seed}: {oceanBad}/{oceanN} basin samples are not Ocean at water surface 0");
        if (dryBad != 0) failures.Add($"seed {seed}: {dryBad}/{dryN} pad samples are wet, below 1 m or vegetated");
        if (equalBad != 0) failures.Add($"seed {seed}: {equalBad}/{equalN} samples beyond the influence differ from v7");
        if (maskBad != 0) failures.Add($"seed {seed}: {maskBad} masks beyond the influence differ from v7");
        if (insideDiff == 0) failures.Add($"seed {seed}: v8 equals v7 inside the influence (operator not applied)");
        Console.WriteLine($"WEATHER_LUNG_V8_SAMPLER seed={seed} basin_ocean={oceanN - oceanBad}/{oceanN} pad_dry={dryN - dryBad}/{dryN} beyond_influence_equal_v7={equalN - equalBad}/{equalN} inside_differs={insideDiff}");

        // ---- diagnostics and mask consistency through the height model ----
        TerrainGenerationParameters recipe = EmitterSiteTestWorlds.Gen17WeatherLungRecipe(seed);
        using var model = new TerrainGenerationHeightModel(recipe);
        int diagBad = 0;
        for (int i = 0; i < 1500; i++)
        {
            double du = (rng.NextDouble() - 0.5d) * 2d * 22_000d, dw = (rng.NextDouble() - 0.5d) * 2d * 17_000d;
            double around = site.Around + du, across = site.Across + dw;
            TerrainGenerationHeightModel.Diagnostics d = model.SampleDiagnostics(around, across);
            if (model.CalculateHeight(around, across) != d.Height) { diagBad++; continue; }
            WeatherLungBasinOperator.ToAssetLocal(in site, du, dw, out double x, out double y);
            op.Locate(x, y, out double r, out double s);
            bool inside = s < op.InfluenceM && Math.Abs(x) < lung.Recipe.BoundHalfXM && Math.Abs(y) < lung.Recipe.BoundHalfYM;
            if (inside)
            {
                if (d.SiteIndex != -1 || d.ClimateMachineIndex != -1) diagBad++;
                else if (Math.Abs(d.WeatherLungWeight - op.Weight(in site, du, dw)) > 1e-12 || d.SiteWeight != d.WeatherLungWeight) diagBad++;
                else if (r <= 0.15d && (d.SiteMask.Class != SiteSurfaceClass.CutFace || d.WeatherLungWeight != 1d)) diagBad++;
                else if (r > 0.15d && s <= 0d && (d.SiteMask.Class != SiteSurfaceClass.Pad || d.SiteMask.Hardscape != 1f || d.WeatherLungWeight != 1d)) diagBad++;
            }
            else if (d.WeatherLungWeight != 0d || d.SiteMask.BlocksVegetation && d.SiteIndex < 0 && d.ClimateMachineIndex < 0) diagBad++;
        }
        if (diagBad != 0) failures.Add($"seed {seed}: {diagBad} diagnostics/mask inconsistencies");
        // Natural diagnostics never carry the Weather Lung weight; v7 diagnostics carry none either.
        if (model.SampleNaturalDiagnostics(site.Around, site.Across).WeatherLungWeight != 0d) failures.Add("natural diagnostics carry a Weather Lung weight");
        using var model7 = new TerrainGenerationHeightModel(EmitterSiteTestWorlds.Gen17LandformRecipe(seed));
        if (model7.SampleDiagnostics(site.Around, site.Across).WeatherLungWeight != 0d) failures.Add("v7 diagnostics carry a Weather Lung weight");

        // ---- thread safety: parallel sampling equals serial ----
        if (full)
        {
            var pts = new (long, int)[4000];
            for (int i = 0; i < pts.Length; i++) pts[i] = (Wrap(site.Around + (long)Math.Round((rng.NextDouble() - 0.5d) * 44_000d / 2d) * 2L), site.Across + (int)Math.Round((rng.NextDouble() - 0.5d) * 34_000d / 2d) * 2);
            var serial = new int[pts.Length];
            for (int i = 0; i < pts.Length; i++) serial[i] = v8.GetSurfaceHeight(pts[i].Item1, pts[i].Item2);
            var parallel = new int[pts.Length];
            Parallel.For(0, pts.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, () => v8.CreateIndependentCopy(), (i, _, worker) => { parallel[i] = worker.GetSurfaceHeight(pts[i].Item1, pts[i].Item2); return worker; }, worker => worker.Dispose());
            int differ = 0;
            for (int i = 0; i < pts.Length; i++) if (serial[i] != parallel[i]) differ++;
            if (differ != 0) failures.Add($"{differ} samples differ between serial and independent-copy parallel sampling");
            CheckFrameRoundTrip(lung, op, failures);
        }
        _ = maxPlinthBuilt;
    }

    // ---- frame round trip ----------------------------------------------------------------------------------------

    /// <summary>
    /// Three asset points (inner lip at t = 0 and pi/2, and the dam at 322 degrees) go through EmitterSiteFrameContext.SiteTransform (the exact call the world streamer
    /// makes) to world space and are inverted back to canonical (around, across) by a numeric solve of WorldPosition. They must land at the canonical offsets predicted by
    /// the placement frame convention (x = du cos + dw sin, y = dw cos - du sin), at r = 0 on the lip, within 1 m.
    /// </summary>
    static void CheckFrameRoundTrip(WeatherLungPlacement lung, WeatherLungBasinOperator op, List<string> failures)
    {
        WeatherLungSite site = lung.Site;
        WeatherLungRecipe r = lung.Recipe;
        double radius = Circumference / Math.Tau;
        var snapshot = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero, radius, 321d, 50d, Circumference, -8000d, 8000d,
            new RingCanonicalPosition(site.Around + 1500d, site.PadHeight + 20d, site.Across - 900d));
        var ctx = new EmitterSiteFrameContext(snapshot);
        const double Clearance = 0.05;
        Transform3D xf = ctx.SiteTransform(site.Around, site.Across, site.PadHeight + Clearance, site.CosYaw, site.SinYaw);
        double worst = 0d, worstR = 0d;
        string report = "";
        foreach ((string name, double t) in new[] { ("lip_t0", 0d), ("lip_t90", Math.PI / 2d), ("dam_t322", 322d * Math.PI / 180d) })
        {
            // Blender (x, y) at the datum; Godot local = (x, 0, -y).
            double bx = r.LipSemiAxisXM * Math.Cos(t), by = r.LipSemiAxisYM * Math.Sin(t);
            Vector3 world = xf * new Vector3((float)bx, 0f, (float)-by);
            lung.FromAssetLocal(bx, by, out double du, out double dw);
            // Invert WorldPosition with Newton (vertical free).
            double a = site.Around + du, z = site.Across + dw, v = site.PadHeight;
            for (int it = 0; it < 12; it++)
            {
                Vector3D p = ctx.WorldPosition(a, v, z);
                double[] f = { world.X - p.X, world.Y - p.Y, world.Z - p.Z };
                if (Math.Abs(f[0]) + Math.Abs(f[1]) + Math.Abs(f[2]) < 1e-4) break;
                const double H = 5d;
                Vector3D pa = ctx.WorldPosition(a + H, v, z), pz = ctx.WorldPosition(a, v, z + H), pv = ctx.WorldPosition(a, v + H, z);
                double[,] j = { { (pa.X - p.X) / H, (pv.X - p.X) / H, (pz.X - p.X) / H }, { (pa.Y - p.Y) / H, (pv.Y - p.Y) / H, (pz.Y - p.Y) / H }, { (pa.Z - p.Z) / H, (pv.Z - p.Z) / H, (pz.Z - p.Z) / H } };
                double[] dx = Solve3(j, f);
                a += dx[0]; v += dx[1]; z += dx[2];
            }
            double gotDu = a - site.Around, gotDw = z - site.Across;
            double err = Math.Max(Math.Abs(gotDu - du), Math.Abs(gotDw - dw));
            worst = Math.Max(worst, err);
            // Back to the elliptic coordinate: a lip point must have r = 0 (|r| * 4100 m within 1 m).
            WeatherLungBasinOperator.ToAssetLocal(in site, gotDu, gotDw, out double rx, out double ry);
            op.Locate(rx, ry, out double rr, out _);
            worstR = Math.Max(worstR, Math.Abs(rr) * 3350d);
            report += $" {name}:err={err:F3}m,r={rr:E1},du={gotDu:F1},dw={gotDw:F1}";
        }
        // Sign checks the world streamer relies on: asset +X is canonical (cosYaw, sinYaw) in (around, across); asset -Z (Blender +y) is canonical (-sinYaw... ) per the formula.
        Vector3 ex = xf.Basis * new Vector3(1f, 0f, 0f);
        Vector3 ez = xf.Basis * new Vector3(0f, 0f, 1f);
        Vector3D t0 = ctx.WorldPosition(site.Around, site.PadHeight, site.Across), t1 = ctx.WorldPosition(site.Around + 100d, site.PadHeight, site.Across), t2 = ctx.WorldPosition(site.Around, site.PadHeight, site.Across + 100d);
        Vector3D dAround = (t1 - t0).Normalized(), dAcross = (t2 - t0).Normalized();
        double xAround = ex.X * dAround.X + ex.Y * dAround.Y + ex.Z * dAround.Z, xAcross = ex.X * dAcross.X + ex.Y * dAcross.Y + ex.Z * dAcross.Z;
        double zAround = ez.X * dAround.X + ez.Y * dAround.Y + ez.Z * dAround.Z, zAcross = ez.X * dAcross.X + ez.Y * dAcross.Y + ez.Z * dAcross.Z;
        double c = site.CosYaw, s = site.SinYaw;
        if (Math.Abs(xAround - c) > 1e-3 || Math.Abs(xAcross - s) > 1e-3 || Math.Abs(zAround - s) > 1e-3 || Math.Abs(zAcross + c) > 1e-3)
            failures.Add($"frame axes: asset X=({xAround:F4},{xAcross:F4}) expected ({c},{s}); asset Z=({zAround:F4},{zAcross:F4}) expected ({s},{-c})");
        if (worst > 1d) failures.Add($"frame round trip error {worst} m");
        if (worstR > 1d) failures.Add($"frame round trip lip radius error {worstR} m");
        Console.WriteLine($"WEATHER_LUNG_FRAME yaw={site.YawDegrees:0} worst_err_m={worst:F3} lip_r_err_m={worstR:F3} asset_X_in_(around,across)=({xAround:F3},{xAcross:F3}) asset_Z_in_(around,across)=({zAround:F3},{zAcross:F3}){report}");
    }

    static double[] Solve3(double[,] a, double[] b)
    {
        // Cramer's rule for the 3x3 Newton step.
        double Det(double[,] m) => m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        double d = Det(a);
        var result = new double[3];
        for (int col = 0; col < 3; col++)
        {
            var m = (double[,])a.Clone();
            for (int row = 0; row < 3; row++) m[row, col] = b[row];
            result[col] = Det(m) / d;
        }
        return result;
    }

    static long Wrap(long around) => ((around % Circumference) + Circumference) % Circumference;

    static double Offset(long around, long centre)
    {
        double d = (around - centre) % Circumference;
        if (d > Circumference / 2) d -= Circumference; else if (d < -Circumference / 2) d += Circumference;
        return d;
    }
}
