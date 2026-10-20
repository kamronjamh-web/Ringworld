using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Stage S4a of docs/weather-lung-integration-plan.md: Weather Lung recipe, placement and basin operator (pure C#, no sampler wiring).
/// Prints WEATHER_LUNG_PLACEMENT for seeds 1337, 1 and 42 against the unshaped v7 landform terrain, then checks the operator on synthetic heights:
/// basin floor, flat pad after the curvature correction, C0 at 6 km, C1 at r 0.05 and 0.15, the wet/dry boundary inside r (0.05, 0.15), disjointness
/// from all 595 sites and 200 machines, thread-count independence, the fallback branch and thread-safety.
/// </summary>
public partial class WeatherLungPlacementTest : RefCounted
{
    static readonly int[] Seeds = { 1337, 1, 42 };

    sealed class FlatSource : INaturalHeightSource
    {
        readonly Func<long, int, double> _height;
        public FlatSource(ulong identity, Func<long, int, double> height) { Identity = identity; _height = height; }
        public double NaturalHeight(long around, int across) => _height(around, across);
        public double SeaLevel => 0d;
        public double MinPossibleHeight => -8000d;
        public ulong Identity { get; }
        public INaturalHeightSource CreateWorkerCopy() => new FlatSource(Identity, _height);
        public bool TryGetExclusionZone(out long around, out int across, out double radiusMeters) { around = 0; across = 0; radiusMeters = 0d; return false; }
        public void Dispose() { }
    }

    public int Run()
    {
        var failures = new List<string>();
        try
        {
            WeatherLungPlacement? first = null;
            EmitterSiteRegistry? firstEmitters = null;
            foreach (int seed in Seeds)
            {
                TerrainGenerationParameters recipe = EmitterSiteTestWorlds.Gen17LandformRecipe(seed);
                using var source = new Gen17NaturalHeightSource(recipe, recipe.DefinitionHash);
                EmitterSiteRecipe emitterRecipe = EmitterSiteRecipe.Default(seed);
                EmitterSiteRegistry emitters = EmitterSiteRegistry.Build(emitterRecipe, source);
                WeatherLungPlacement placement = WeatherLungPlacement.Build(WeatherLungRecipe.For(emitterRecipe), emitters, ClimateMachineRecipe.For(emitterRecipe), source);
                Console.WriteLine(placement.Describe("v7natural"));
                if (seed == 1337)
                {
                    var scan = new System.Text.StringBuilder();
                    EmitterSite mainSite = emitters.MainSite;
                    for (int k = -20; k <= 20; k++)
                    {
                        long a = ((mainSite.Around + k * 20_000L) % (long)emitterRecipe.CircumferenceM + (long)emitterRecipe.CircumferenceM) % (long)emitterRecipe.CircumferenceM;
                        scan.Append(k * 20).Append("km:").Append((int)Math.Round(source.NaturalHeight(a & ~1L, 159_000))).Append(' ');
                    }
                    Console.WriteLine($"WEATHER_LUNG_CENTRELINE seed=1337 main_around={mainSite.Around} main_pad={mainSite.PadHeight} main_flags={mainSite.Flags} natural_height_every_20km_from_main={scan}");
                }
                foreach (WeatherLungCandidateReport c in placement.CandidateReports.Where(c => c.Disjoint).OrderBy(c => c.Score).Take(5))
                    Console.WriteLine($"WEATHER_LUNG_TOP seed={seed} station={c.Station} around={c.Around} across={c.Across} dist_km={c.DistanceFromMainM / 1000d:0} class={c.TerrainClass} land={c.Land:0.000} cut_max={c.CutMaxM:0} fill_mean={c.FillMeanM:0} plinth_max={c.PlinthMaxM:0} score={c.Score:0.0}");
                Console.WriteLine($"WEATHER_LUNG_BUILD seed={seed} ms={placement.BuildMilliseconds:F0} candidates={placement.CandidatesEvaluated} disjoint={placement.CandidatesDisjoint} identity={placement.Identity:X16} recipe_hash={placement.Recipe.Hash:X16}");
                CheckPlacement(seed, placement, emitters, failures);
                if (seed == 1337)
                {
                    first = placement;
                    firstEmitters = emitters;
                    CheckThreadIndependence(seed, source, emitterRecipe, placement, failures);
                    CheckDisjointness(placement, emitters, failures);
                }
            }
            if (first == null || firstEmitters == null) throw new InvalidOperationException("seed 1337 placement missing");
            CheckOperator(first, EmitterSiteRecipe.Default(1337), failures);
            CheckFrame(first, failures);
            CheckSyntheticPlacement(firstEmitters, failures);
            CheckRecipe(failures);
        }
        catch (Exception exception) { failures.Add(exception.ToString()); }
        foreach (string failure in failures) GD.PrintErr("WEATHER_LUNG_PLACEMENT_FAIL " + failure);
        Console.WriteLine(failures.Count == 0 ? "WEATHER_LUNG_PLACEMENT_TEST=PASS" : "WEATHER_LUNG_PLACEMENT_TEST=FAIL failures=" + failures.Count);
        return failures.Count == 0 ? 0 : 1;
    }

    // ---- placement ---------------------------------------------------------------------------------------------

    static void CheckPlacement(int seed, WeatherLungPlacement p, EmitterSiteRegistry emitters, List<string> failures)
    {
        WeatherLungSite s = p.Site;
        WeatherLungRecipe r = p.Recipe;
        if (s.Across < p.UsableAcrossMinM || s.Across > p.UsableAcrossMaxM) failures.Add($"seed {seed}: across {s.Across} outside the usable range [{p.UsableAcrossMinM}, {p.UsableAcrossMaxM}]");
        if (s.PadHeight != 135) failures.Add($"seed {seed}: datum {s.PadHeight}");
        if ((s.Around & 1L) != 0 || (s.Across & 1) != 0 || s.Around < 0 || s.Around >= (long)r.CircumferenceM) failures.Add($"seed {seed}: position {s.Around}/{s.Across} off lattice or range");
        if (s.YawDegrees != 0f && s.YawDegrees != 180f) failures.Add($"seed {seed}: yaw {s.YawDegrees}");
        EmitterSite main = emitters.MainSite;
        double gap = (s.Around - main.Around + r.CircumferenceM * 1.5d) % r.CircumferenceM - r.CircumferenceM * 0.5d;
        if ((gap >= 0d) != (s.YawDegrees == 180f)) failures.Add($"seed {seed}: hood not away from the main spire (offset {gap:F0} m yaw {s.YawDegrees})");
        if (Math.Abs(gap) > r.RowPitchM * (r.GapsEachSide + 1)) failures.Add($"seed {seed}: outside the searched gaps ({gap:F0} m)");
        if (p.CandidatesDisjoint == 0 || p.CandidatesEvaluated != p.CandidateReports.Length) failures.Add($"seed {seed}: candidate bookkeeping");
        // The chosen candidate has the minimum score among disjoint candidates, and the reported runner-up is the next one.
        double best = double.MaxValue, second = double.MaxValue;
        foreach (WeatherLungCandidateReport c in p.CandidateReports)
        {
            if (!c.Disjoint) continue;
            if (c.Score < best) { second = best; best = c.Score; } else if (c.Score < second) second = c.Score;
        }
        if (Math.Abs(best - s.Score) > 1e-3) failures.Add($"seed {seed}: chosen score {s.Score} is not the minimum {best}");
        if (p.RunnerUp is { } ru && Math.Abs(ru.Score - second) > 1e-3) failures.Add($"seed {seed}: runner-up score {ru.Score} != {second}");
        foreach (EmitterSite site in emitters.Sites)
        {
            double gx = Math.Max(0d, Math.Abs((site.Around - s.Around + r.CircumferenceM * 1.5d) % r.CircumferenceM - r.CircumferenceM * 0.5d) - r.BoundHalfXM);
            double gy = Math.Max(0d, Math.Abs(site.Across - s.Across) - r.BoundHalfYM);
            if (Math.Sqrt(gx * gx + gy * gy) <= site.InfluenceRadius) { failures.Add($"seed {seed}: chosen site overlaps emitter site {site.Index}"); break; }
        }
        if (s.PlinthMaxM > 1500f) failures.Add($"seed {seed}: plinth {s.PlinthMaxM:F0} m is taller than the 1500 m sanity limit");
        // Lookup is centred and symmetric.
        if (!p.TryGetInfluence(s.Around, s.Across, out double du, out double dw) || du != 0d || dw != 0d) failures.Add($"seed {seed}: centre lookup");
        if (p.TryGetInfluence(s.Around + 21_000d, s.Across, out _, out _) || p.TryGetInfluence(s.Around, s.Across + 16_000d, out _, out _)) failures.Add($"seed {seed}: lookup outside the bounding box");
        if (!p.TryGetInfluence(s.Around - 19_000d, s.Across - 14_000d, out du, out dw) || Math.Abs(du + 19_000d) > 1e-9 || Math.Abs(dw + 14_000d) > 1e-9) failures.Add($"seed {seed}: lookup offsets");
    }

    static void CheckThreadIndependence(int seed, INaturalHeightSource source, EmitterSiteRecipe emitterRecipe, WeatherLungPlacement reference, List<string> failures)
    {
        foreach (int threads in new[] { 1, 3, 8 })
        {
            EmitterSiteRegistry emitters = EmitterSiteRegistry.Build(emitterRecipe, source, threads);
            WeatherLungPlacement again = WeatherLungPlacement.Build(WeatherLungRecipe.For(emitterRecipe), emitters, null, source);
            if (again.Identity != reference.Identity || again.Site.SiteHash != reference.Site.SiteHash)
                failures.Add($"seed {seed}: placement differs with {threads} registry threads ({again.Identity:X16} vs {reference.Identity:X16})");
        }
        Console.WriteLine("WEATHER_LUNG_THREADS identical across registry builds with 1, 3 and 8 threads (and with the climate recipe omitted)");
    }

    /// <summary>Brute force: no site influence disc and no machine influence region touches the Weather Lung influence, tested through the operator itself.</summary>
    static void CheckDisjointness(WeatherLungPlacement p, EmitterSiteRegistry emitters, List<string> failures)
    {
        var op = new WeatherLungBasinOperator(p.Recipe, emitters.Recipe, 0d);
        WeatherLungSite site = p.Site;
        double circumference = p.Recipe.CircumferenceM;
        int checkedSites = 0, checkedMachines = 0;
        double minGapSite = double.MaxValue;
        foreach (EmitterSite s in emitters.Sites)
        {
            double du = (s.Around - site.Around + circumference * 1.5d) % circumference - circumference * 0.5d;
            double dw = s.Across - site.Across;
            if (Math.Abs(du) > 200_000d || Math.Abs(dw) > 200_000d) continue;
            checkedSites++;
            // Sample the site's influence disc (polar grid): the Weather Lung weight must be exactly 0 everywhere in it.
            for (int ring = 0; ring <= 12; ring++)
                for (int k = 0; k < 48; k++)
                {
                    double rad = s.InfluenceRadius * ring / 12d, ang = Math.Tau * k / 48d;
                    double pu = du + rad * Math.Cos(ang), pw = dw + rad * Math.Sin(ang);
                    if (op.Weight(in site, pu, pw) != 0d) failures.Add($"site {s.Index} influence overlaps the Weather Lung at ({pu:F0}, {pw:F0})");
                }
            minGapSite = Math.Min(minGapSite, Math.Sqrt(Math.Max(0, Math.Abs(du) - p.Recipe.BoundHalfXM) * Math.Max(0, Math.Abs(du) - p.Recipe.BoundHalfXM)
                + Math.Max(0, Math.Abs(dw) - p.Recipe.BoundHalfYM) * Math.Max(0, Math.Abs(dw) - p.Recipe.BoundHalfYM)) - s.InfluenceRadius);
        }
        ClimateMachineRecipe machines = ClimateMachineRecipe.For(emitters.Recipe);
        for (int g = 0; g < machines.Stations; g++)
        {
            double a = EmitterSiteRecipe.Snap2((machines.RowPhaseM + (g + 0.5d) * machines.RowPitchM) % circumference);
            for (int side = 0; side < 2; side++)
            {
                double du = (a - site.Around + circumference * 1.5d) % circumference - circumference * 0.5d;
                double dw = (side == 0 ? 0d : machines.WidthM) - site.Across;
                if (Math.Abs(du) > 200_000d) continue;
                checkedMachines++;
                // The machine's footprint segment and its influence band must not reach the Weather Lung weight region.
                for (int k = -10; k <= 10; k++)
                    for (int d = 0; d <= 12; d++)
                    {
                        double pu = du + machines.ForecourtLengthM * 0.5d * k / 10d;
                        double pw = dw + (side == 0 ? 1d : -1d) * machines.InfluenceRadiusM * d / 12d;
                        if (op.Weight(in site, pu, pw) != 0d) failures.Add($"machine {g * 2 + side} influence overlaps the Weather Lung at ({pu:F0}, {pw:F0})");
                    }
            }
        }
        Console.WriteLine($"WEATHER_LUNG_DISJOINT near_sites_checked={checkedSites} near_machines_checked={checkedMachines} min_site_gap_m={minGapSite:F0} total_sites={emitters.Count}");
        if (emitters.Count != 595) failures.Add("emitter registry does not have 595 sites: " + emitters.Count);
    }
    /// <summary>Synthetic natural heights: flat land, open ocean platform, shallow shelf in a trench, and a mountain world with one valley.</summary>
    static void CheckSyntheticPlacement(EmitterSiteRegistry emitters, List<string> failures)
    {
        EmitterSiteRecipe er = emitters.Recipe;
        WeatherLungRecipe recipe = WeatherLungRecipe.For(er);
        double pitch = recipe.RowPitchM;
        var land = new FlatSource(0xAAAA0001UL, (_, _) => 40d);
        WeatherLungPlacement onLand = WeatherLungPlacement.Build(recipe, emitters, null, land);
        double landDist = Dist(onLand);
        if (onLand.Site.TerrainClass != WeatherLungTerrainClass.Land || onLand.Site.IsQuay || Math.Abs(onLand.Site.FillMeanM - 95f) > 1e-3 || onLand.Site.CutMaxM != 0f || landDist > 120_000d)
            failures.Add($"flat land: class={onLand.Site.TerrainClass} quay={onLand.Site.IsQuay} fill={onLand.Site.FillMeanM} dist={landDist:F0}");
        // Open ocean: an ocean platform is an acceptable outcome; the nearest candidate wins and the plinth is the whole seabed depth.
        var ocean = new FlatSource(0xAAAA0002UL, (_, _) => -3000d);
        WeatherLungPlacement onOcean = WeatherLungPlacement.Build(recipe, emitters, null, ocean);
        if (onOcean.Site.TerrainClass != WeatherLungTerrainClass.Deep || !onOcean.Site.IsQuay || onOcean.Site.LandFraction != 0f || Math.Abs(onOcean.Site.PlinthMaxM - 3135f) > 1e-3 || Dist(onOcean) > 120_000d)
            failures.Add($"ocean: class={onOcean.Site.TerrainClass} quay={onOcean.Site.IsQuay} land={onOcean.Site.LandFraction} plinth={onOcean.Site.PlinthMaxM} dist={Dist(onOcean):F0}");
        // A shallow shelf (-30 m) inside a -3000 m trench, far from the main spire, beats the trench: shallow beats deep despite the distance term.
        long shelfLo = (long)(recipe.RowPhaseM + (emitters.MainRow + 3) * pitch), shelfHi = (long)(recipe.RowPhaseM + (emitters.MainRow + 4) * pitch);
        var shelf = new FlatSource(0xAAAA0003UL, (a, _) => a >= shelfLo - 60_000 && a <= shelfHi + 60_000 ? -30d : -3000d);
        WeatherLungPlacement onShelf = WeatherLungPlacement.Build(recipe, emitters, null, shelf);
        if (onShelf.Site.TerrainClass != WeatherLungTerrainClass.Shallow || onShelf.Site.PlinthMaxM > 200f || !onShelf.Site.IsQuay)
            failures.Add($"shelf: class={onShelf.Site.TerrainClass} plinth={onShelf.Site.PlinthMaxM} quay={onShelf.Site.IsQuay} around={onShelf.Site.Around}");
        // Mountains everywhere except a valley window: the valley is chosen (cut is the worst outcome).
        long valleyLo = (long)(recipe.RowPhaseM + (emitters.MainRow - 2) * pitch), valleyHi = (long)(recipe.RowPhaseM + (emitters.MainRow - 1) * pitch);
        var valley = new FlatSource(0xAAAA0004UL, (a, _) => a >= valleyLo && a <= valleyHi ? 60d : 3000d);
        WeatherLungPlacement inValley = WeatherLungPlacement.Build(recipe, emitters, null, valley);
        if (inValley.Site.TerrainClass != WeatherLungTerrainClass.Land || inValley.Site.CutMaxM != 0f || inValley.Site.Around < valleyLo || inValley.Site.Around > valleyHi)
            failures.Add($"valley: around={inValley.Site.Around} window=[{valleyLo},{valleyHi}] cut={inValley.Site.CutMaxM}");
        if (WeatherLungPlacement.GetOrBuild(recipe, emitters, null, land) != WeatherLungPlacement.GetOrBuild(recipe, emitters, null, land)) failures.Add("cache returned different instances");
        Console.WriteLine($"WEATHER_LUNG_SYNTHETIC land(dist_km={landDist / 1000d:F0}) ocean(class={onOcean.Site.TerrainClass} plinth={onOcean.Site.PlinthMaxM:F0} dist_km={Dist(onOcean) / 1000d:F0}) shelf(class={onShelf.Site.TerrainClass} plinth={onShelf.Site.PlinthMaxM:F0}) valley(around={inValley.Site.Around})");
        CheckOceanPlatform(onOcean, er, failures);
    }

    static double Dist(WeatherLungPlacement p)
    {
        double du = (p.Site.Around - p.MainAround + p.Recipe.CircumferenceM * 1.5d) % p.Recipe.CircumferenceM - p.Recipe.CircumferenceM * 0.5d;
        double dw = p.Site.Across - p.MainAcross;
        return Math.Sqrt(du * du + dw * dw);
    }

    /// <summary>Ocean platform: the pad is flat at the datum, the basin reads below sea level, and the skirt descends to the seabed without cliffs steeper than the cone.</summary>
    static void CheckOceanPlatform(WeatherLungPlacement p, EmitterSiteRecipe er, List<string> failures)
    {
        var op = new WeatherLungBasinOperator(p.Recipe, er, 0d);
        WeatherLungSite site = p.Site;
        WeatherLungRecipe r = p.Recipe;
        const double Seabed = -3000d;
        double maxSlope = 0d, minAboveNatural = double.MaxValue, padErr = 0d;
        bool basinWet = true;
        for (int i = 0; i < 360; i++)
        {
            double t = Math.Tau * i / 360d, ct = Math.Cos(t), st = Math.Sin(t);
            double dist = Math.Sqrt((r.RimGrowthXM * ct) * (r.RimGrowthXM * ct) + (r.RimGrowthYM * st) * (r.RimGrowthYM * st));
            double Height(double s)
            {
                double rr = 1d + s / dist;
                p.FromAssetLocal((r.LipSemiAxisXM + r.RimGrowthXM * rr) * ct, (r.LipSemiAxisYM + r.RimGrowthYM * rr) * st, out double du, out double dw);
                return op.Shape(in site, du, dw, Seabed);
            }
            p.FromAssetLocal((r.LipSemiAxisXM + r.RimGrowthXM) * ct, (r.LipSemiAxisYM + r.RimGrowthYM) * st, out double eu, out double ew);
            padErr = Math.Max(padErr, Math.Abs(op.Shape(in site, eu, ew, Seabed) + op.Curvature(eu) - site.PadHeight));
            p.FromAssetLocal(r.LipSemiAxisXM * 0.5d * ct, r.LipSemiAxisYM * 0.5d * st, out double bu, out double bw);
            if (op.Shape(in site, bu, bw, Seabed) >= 0d) basinWet = false;
            double prev = Height(0d);
            for (double s = 10d; s <= r.InfluenceM; s += 10d)
            {
                double h = Height(s);
                maxSlope = Math.Max(maxSlope, Math.Abs(h - prev) / 10d);
                minAboveNatural = Math.Min(minAboveNatural, h - Seabed);
                prev = h;
            }
        }
        if (padErr > 1e-6) failures.Add("ocean platform pad deviates " + padErr);
        if (!basinWet) failures.Add("ocean platform basin is not below sea level");
        if (minAboveNatural < -1e-9) failures.Add("ocean platform skirt is below the natural seabed (" + minAboveNatural + ")");
        if (maxSlope > 1.2d) failures.Add($"ocean platform skirt too steep: {maxSlope:F2}");
        Console.WriteLine($"WEATHER_LUNG_OCEAN_PLATFORM pad_err={padErr:E2} max_skirt_slope={maxSlope:F3} (atan {Math.Atan(maxSlope) * 180d / Math.PI:F1} deg) basin_below_sea={basinWet} plinth_max_m={site.PlinthMaxM:F0}");
    }

    static void CheckRecipe(List<string> failures)
    {
        WeatherLungRecipe a = WeatherLungRecipe.Default(1337), b = WeatherLungRecipe.Default(1337), c = WeatherLungRecipe.Default(1338);
        if (a.Hash != b.Hash || a.Hash == c.Hash || a.Hash == 0UL) failures.Add("recipe hash not stable or not seed dependent");
        if (a.AlgorithmVersion != 2 || WeatherLungBasinOperator.OperatorVersion != 1) failures.Add("algorithm/operator version");
        Console.WriteLine($"WEATHER_LUNG_RECIPE hash_1337={a.Hash:X16} algorithm={a.AlgorithmVersion} operator={WeatherLungBasinOperator.OperatorVersion}");
    }

    // ---- frame -------------------------------------------------------------------------------------------------

    static void CheckFrame(WeatherLungPlacement p, List<string> failures)
    {
        var rng = new Random(7);
        double worst = 0d;
        for (int i = 0; i < 1000; i++)
        {
            double du = (rng.NextDouble() - 0.5d) * 40_000d, dw = (rng.NextDouble() - 0.5d) * 30_000d;
            p.ToAssetLocal(du, dw, out double x, out double y);
            p.FromAssetLocal(x, y, out double du2, out double dw2);
            worst = Math.Max(worst, Math.Max(Math.Abs(du - du2), Math.Abs(dw - dw2)));
        }
        if (worst > 1e-9) failures.Add("asset-local round trip error " + worst);
        // Yaw 180: asset -X (the hood) is canonical +around.
        p.ToAssetLocal(1000d, 0d, out double hx, out _);
        if ((p.Site.YawDegrees == 180f) != (hx < 0d)) failures.Add("hood side: asset x of +around is " + hx);
    }

    // ---- operator ----------------------------------------------------------------------------------------------

    static double Natural(double du, double dw) => 30d + 25d * Math.Sin(du * 0.00031) * Math.Cos(dw * 0.00047) + 0.004d * du;

    static void CheckOperator(WeatherLungPlacement p, EmitterSiteRecipe emitterRecipe, List<string> failures)
    {
        WeatherLungRecipe recipe = p.Recipe;
        var op = new WeatherLungBasinOperator(recipe, emitterRecipe, 0d);
        WeatherLungSite site = p.Site;
        double lipX = recipe.LipSemiAxisXM, lipY = recipe.LipSemiAxisYM, gx = recipe.RimGrowthXM, gy = recipe.RimGrowthYM;
        void Offsets(double t, double r, out double du, out double dw)
        {
            p.FromAssetLocal((lipX + gx * r) * Math.Cos(t), (lipY + gy * r) * Math.Sin(t), out du, out dw);
        }
        double Dist(double t) => Math.Sqrt((gx * Math.Cos(t)) * (gx * Math.Cos(t)) + (gy * Math.Sin(t)) * (gy * Math.Sin(t)));
        var rng = new Random(11);

        // Elliptic coordinate round trip.
        double worstR = 0d;
        for (int i = 0; i < 4000; i++)
        {
            double t = rng.NextDouble() * Math.Tau, r = -0.6 + rng.NextDouble() * 3.0;
            double x = (lipX + gx * r) * Math.Cos(t), y = (lipY + gy * r) * Math.Sin(t);
            double solved = op.SolveR(x, y);
            if (r >= 3d) continue;
            worstR = Math.Max(worstR, Math.Abs(solved - r));
        }
        if (worstR > 1e-9) failures.Add("elliptic r round trip error " + worstR);

        // 1. Basin floor: r < 0.05 is exactly P - 875 - du^2 / 2R.
        double worstFloor = 0d, worstPad = 0d;
        int floorN = 0, padN = 0;
        for (int i = 0; i < 6000; i++)
        {
            double t = rng.NextDouble() * Math.Tau, r = rng.NextDouble() * 0.0499;
            Offsets(t, r, out double du, out double dw);
            double expected = site.PadHeight - 875d - du * du / (2d * recipe.RingRadiusM);
            worstFloor = Math.Max(worstFloor, Math.Abs(op.Shape(in site, du, dw, Natural(du, dw)) - expected));
            floorN++;
        }
        if (worstFloor > 1e-6) failures.Add("basin floor deviates by " + worstFloor);

        // 2. Pad: r in [0.15, 1] and the 400 m apron beyond the edge are flat to 1e-6 after the curvature correction, with Hardscape and no vegetation.
        int padMaskBad = 0;
        for (int i = 0; i < 8000; i++)
        {
            double t = rng.NextDouble() * Math.Tau;
            double s = rng.NextDouble() < 0.5d ? double.NaN : rng.NextDouble() * recipe.ApronM;
            double r = double.IsNaN(s) ? 0.15d + rng.NextDouble() * 0.85d : 1d + s / Dist(t);
            Offsets(t, r, out double du, out double dw);
            double h = op.Shape(in site, du, dw, Natural(du, dw) + 300d * Math.Sin(du * 0.01), out SiteSurfaceMask mask);
            worstPad = Math.Max(worstPad, Math.Abs(h + op.Curvature(du) - site.PadHeight));
            padN++;
            if (!mask.BlocksVegetation || mask.VegetationAllowed || mask.Class != SiteSurfaceClass.Pad) padMaskBad++;
        }
        if (worstPad > 1e-6) failures.Add("pad deviates by " + worstPad);
        if (padMaskBad != 0) failures.Add(padMaskBad + " pad samples without a Pad/blocking mask");

        // 3. C0 at s = 6 km (and s = apron) against an analytic natural field, and exactly natural beyond.
        double worstC0 = 0d, worstApron = 0d, worstBeyond = 0d;
        foreach (bool quay in new[] { false, true })
        {
            WeatherLungSite probe = quay ? WithFlags(site, WeatherLungFlags.Quay) : site;
            for (int i = 0; i < 720; i++)
            {
                double t = Math.Tau * i / 720d, d = Dist(t);
                foreach ((double sIn, double sOut) in new[] { (recipe.InfluenceM - 1e-3, recipe.InfluenceM + 1e-3) })
                {
                    Offsets(t, 1d + sIn / d, out double du1, out double dw1);
                    Offsets(t, 1d + sOut / d, out double du2, out double dw2);
                    double inside = op.Shape(in probe, du1, dw1, Natural(du1, dw1));
                    double outside = op.Shape(in probe, du2, dw2, Natural(du2, dw2));
                    worstC0 = Math.Max(worstC0, Math.Abs(inside - Natural(du1, dw1)));
                    if (outside != Natural(du2, dw2)) worstBeyond = Math.Max(worstBeyond, Math.Abs(outside - Natural(du2, dw2)));
                }
                Offsets(t, 1d + (recipe.ApronM - 1e-3) / d, out double da, out double wa);
                Offsets(t, 1d + (recipe.ApronM + 1e-3) / d, out double db, out double wb);
                double ha = op.Shape(in probe, da, wa, Natural(da, wa)), hb = op.Shape(in probe, db, wb, Natural(db, wb));
                worstApron = Math.Max(worstApron, Math.Abs(ha - hb));
            }
        }
        if (worstC0 > 0.02d) failures.Add($"not C0 at the influence boundary: {worstC0}");
        if (worstBeyond != 0d) failures.Add($"shaped differs from natural beyond the influence by {worstBeyond}");
        if (worstApron > 0.5d) failures.Add($"apron edge discontinuity {worstApron}");

        // 4. C1 at r = 0.05 and r = 0.15 (one-sided derivatives along r agree) and a monotone wet/dry boundary strictly inside r (0.05, 0.15).
        double worstKink = 0d;
        double minBoundary = 1d, maxBoundary = 0d;
        int boundaryBad = 0;
        const double Eps = 1e-6;
        for (int i = 0; i < 360; i++)
        {
            double t = Math.Tau * (i + 0.37) / 360d;
            double H(double r)
            {
                Offsets(t, r, out double du, out double dw);
                return op.Shape(in site, du, dw, Natural(du, dw));
            }
            foreach (double edge in new[] { recipe.RCarve, recipe.RPad })
            {
                double left = (H(edge) - H(edge - Eps)) / Eps, right = (H(edge + Eps) - H(edge)) / Eps;
                worstKink = Math.Max(worstKink, Math.Abs(left - right));
            }
            double lo = recipe.RCarve, hi = recipe.RPad;
            if (!(H(lo) < 0d && H(hi) > 0d)) { boundaryBad++; continue; }
            double prev = H(lo);
            bool monotone = true;
            for (int k = 1; k <= 200; k++)
            {
                double v = H(lo + (hi - lo) * k / 200d);
                if (v < prev - 1e-9) monotone = false;
                prev = v;
            }
            if (!monotone) boundaryBad++;
            for (int k = 0; k < 60; k++) { double mid = 0.5d * (lo + hi); if (H(mid) < 0d) lo = mid; else hi = mid; }
            minBoundary = Math.Min(minBoundary, lo); maxBoundary = Math.Max(maxBoundary, lo);
        }
        if (worstKink > 5d) failures.Add($"slope kink at r 0.05/0.15: {worstKink}");
        if (boundaryBad != 0 || minBoundary <= recipe.RCarve || maxBoundary >= recipe.RPad) failures.Add($"wet/dry boundary outside (0.05, 0.15): bad={boundaryBad} range=[{minBoundary:F4}, {maxBoundary:F4}]");

        // 5. Weight, masks, determinism and thread safety.
        Offsets(0.3, 0.02, out double cu, out double cw);
        if (op.Weight(in site, cu, cw) != 1d) failures.Add("weight inside the basin is not 1");
        Offsets(0.3, 1d + recipe.ApronM / Dist(0.3), out cu, out cw);
        if (Math.Abs(op.Weight(in site, cu, cw) - 1d) > 1e-9) failures.Add("weight at the apron edge is not 1");
        Offsets(0.3, 1d + recipe.InfluenceM / Dist(0.3) + 1e-3, out cu, out cw);
        if (op.Weight(in site, cu, cw) != 0d) failures.Add("weight at the influence boundary is not 0");
        double prevW = 1d;
        for (int k = 0; k <= 100; k++)
        {
            Offsets(1.1, 1d + (recipe.ApronM + (recipe.InfluenceM - recipe.ApronM) * k / 100d) / Dist(1.1), out cu, out cw);
            double w = op.Weight(in site, cu, cw);
            if (w > prevW + 1e-12) { failures.Add("weight is not monotone"); break; }
            prevW = w;
        }
        Offsets(0.7, 0.02, out cu, out cw);
        op.Shape(in site, cu, cw, 10d, out SiteSurfaceMask basinMask);
        if (basinMask.Class != SiteSurfaceClass.CutFace || basinMask.Material != SiteMaterialHint.Rock || basinMask.Rock != 1f || basinMask.VegetationAllowed) failures.Add("basin mask");
        Offsets(2.0, 1d + 1000d / Dist(2.0), out cu, out cw);
        op.Shape(in site, cu, cw, Natural(cu, cw), out SiteSurfaceMask outer);
        double shapedOuter = op.Shape(in site, cu, cw, Natural(cu, cw));
        if (shapedOuter != op.Shape(in site, cu, cw, Natural(cu, cw), out _)) failures.Add("mask overload changes the height");
        _ = outer;

        var points = new (double, double)[20000];
        for (int i = 0; i < points.Length; i++) points[i] = ((rng.NextDouble() - 0.5d) * 41_000d, (rng.NextDouble() - 0.5d) * 31_000d);
        var serial = new double[points.Length];
        for (int i = 0; i < points.Length; i++) serial[i] = op.Shape(in site, points[i].Item1, points[i].Item2, Natural(points[i].Item1, points[i].Item2));
        var parallel = new double[points.Length];
        WeatherLungSite captured = site;
        Parallel.For(0, points.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => parallel[i] = op.Shape(in captured, points[i].Item1, points[i].Item2, Natural(points[i].Item1, points[i].Item2)));
        int differ = 0;
        for (int i = 0; i < points.Length; i++) if (serial[i] != parallel[i]) differ++;
        if (differ != 0) failures.Add(differ + " samples differ between serial and parallel evaluation");
        Console.WriteLine($"WEATHER_LUNG_OPERATOR floor_n={floorN} floor_err={worstFloor:E2} pad_n={padN} pad_err={worstPad:E2} c0_err={worstC0:E2} apron_step={worstApron:E2} kink={worstKink:E2} " +
            $"wet_dry_r=[{minBoundary:F4},{maxBoundary:F4}] r_roundtrip={worstR:E2}");
    }

    static WeatherLungSite WithFlags(WeatherLungSite s, WeatherLungFlags flags) => new()
    {
        Station = s.Station, CandidateIndex = s.CandidateIndex, Around = s.Around, Across = s.Across, PadHeight = s.PadHeight, YawDegrees = s.YawDegrees,
        Flags = s.Flags | flags, TerrainClass = s.TerrainClass, LandFraction = s.LandFraction, CutMaxM = s.CutMaxM, CutMeanM = s.CutMeanM, FillMeanM = s.FillMeanM,
        PlinthMaxM = s.PlinthMaxM, Score = s.Score, SiteHash = s.SiteHash,
    };
}
