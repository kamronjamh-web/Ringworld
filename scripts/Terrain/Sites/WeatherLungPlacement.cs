using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RingworldTechDemo.Terrain.Sites;

[Flags]
public enum WeatherLungFlags : ushort
{
    None = 0,
    /// <summary>The placement stencil touched water; the forecourt needs a raised quay / plinth.</summary>
    Quay = 1,
}

/// <summary>Terrain setting of the footprint, from the rim stencil (r 0.2..1.5).</summary>
public enum WeatherLungTerrainClass : byte
{
    Land = 0,
    /// <summary>Mixed land and water.</summary>
    Coast = 1,
    /// <summary>Open water with a shallow seabed.</summary>
    Shallow = 2,
    /// <summary>Open water with a deep seabed (plinth platform).</summary>
    Deep = 3,
}

/// <summary>
/// The single Weather Lung site. Position is on the canonical 2 m lattice. The yaw follows <c>EmitterSiteFrameContext.SiteTransform</c>: asset local X maps to
/// (cos yaw, sin yaw) in (around, across), asset local Z to (sin yaw, -cos yaw). Yaw 180 puts the hood (asset -X) prograde, away from the main spire.
/// </summary>
public readonly struct WeatherLungSite
{
    /// <summary>Emitter-row gap (0..99) the site lies in.</summary>
    public int Station { get; init; }
    /// <summary>Index in the deterministic candidate enumeration.</summary>
    public int CandidateIndex { get; init; }
    public long Around { get; init; }
    public int Across { get; init; }
    /// <summary>Datum height in integer metres above the base surface: sea level + <see cref="WeatherLungRecipe.DatumAboveSeaM"/>; asset local Y 0.</summary>
    public int PadHeight { get; init; }
    public float YawDegrees { get; init; }
    public WeatherLungFlags Flags { get; init; }
    public WeatherLungTerrainClass TerrainClass { get; init; }
    /// <summary>Rim stencil statistics of the chosen candidate (metres relative to the datum).</summary>
    public float LandFraction { get; init; }
    public float CutMaxM { get; init; }
    public float CutMeanM { get; init; }
    /// <summary>Mean fill below the datum over the rim stencil (land fill and plinth).</summary>
    public float FillMeanM { get; init; }
    /// <summary>Tallest plinth: datum minus the lowest natural height of the rim stencil.</summary>
    public float PlinthMaxM { get; init; }
    public float Score { get; init; }
    public ulong SiteHash { get; init; }

    public bool IsQuay => (Flags & WeatherLungFlags.Quay) != 0;
    /// <summary>Yaw cosine and sine as passed to <c>SiteTransform</c>; exact for the two supported yaws.</summary>
    public double CosYaw => YawDegrees == 180f ? -1d : YawDegrees == 0f ? 1d : Math.Cos(YawDegrees * Math.PI / 180d);
    public double SinYaw => YawDegrees == 180f || YawDegrees == 0f ? 0d : Math.Sin(YawDegrees * Math.PI / 180d);
}

/// <summary>Stencil statistics and score of one placement candidate (diagnostics for the placement report).</summary>
public readonly record struct WeatherLungCandidateReport(int Index, int Station, long Around, int Across, float YawDegrees, bool Disjoint, double DistanceFromMainM,
    double Land, double CutMaxM, double CutMeanM, double FillMeanM, double PlinthMaxM, bool Wet, WeatherLungTerrainClass TerrainClass, double Score);

/// <summary>
/// Deterministic placement of the Weather Lung (integration plan D4, revised by the user: any across, ocean platforms allowed). The candidates are a
/// grid over the station gaps MainRow +- GapsEachSide: several along-gap offsets times several across positions over the usable ring width. Candidates that
/// overlap an emitter site, a Wall Engine influence band or the main spire's knot links are rejected; the minimum score wins (ties: lower enumeration index).
/// Immutable after construction; lookups are allocation-free and thread-safe. The result is independent of the thread count and of the climate_machines
/// toggle (machine disjointness uses the recipe-derived machine grid).
/// </summary>
public sealed class WeatherLungPlacement
{
    private static readonly ConcurrentDictionary<(ulong, ulong, ulong), Lazy<WeatherLungPlacement>> Cache = new();

    private readonly long _circumference;
    private readonly double _halfX, _halfY;

    public WeatherLungRecipe Recipe { get; }
    public WeatherLungSite Site { get; }
    public ulong NaturalIdentity { get; }
    public ulong EmitterIdentity { get; }
    public ulong Identity { get; }
    public int MainRow { get; }
    public long MainAround { get; }
    public int MainAcross { get; }
    /// <summary>Usable across range for the footprint centre (machine influence band + footprint + margin from each ring edge).</summary>
    public double UsableAcrossMinM { get; }
    public double UsableAcrossMaxM { get; }
    public int CandidatesEvaluated { get; }
    public int CandidatesDisjoint { get; }
    /// <summary>Second best disjoint candidate (null when there is only one).</summary>
    public WeatherLungCandidateReport? RunnerUp { get; }
    /// <summary>Every candidate in enumeration order (diagnostics; not part of the identity).</summary>
    public WeatherLungCandidateReport[] CandidateReports { get; }
    public double BuildMilliseconds { get; private set; }

    private WeatherLungPlacement(WeatherLungRecipe recipe, ulong naturalIdentity, ulong emitterIdentity, int mainRow, long mainAround, int mainAcross,
        double acrossMin, double acrossMax, WeatherLungSite site, WeatherLungCandidateReport[] reports, int disjoint, WeatherLungCandidateReport? runnerUp)
    {
        Recipe = recipe;
        NaturalIdentity = naturalIdentity;
        EmitterIdentity = emitterIdentity;
        MainRow = mainRow;
        MainAround = mainAround;
        MainAcross = mainAcross;
        UsableAcrossMinM = acrossMin;
        UsableAcrossMaxM = acrossMax;
        Site = site;
        CandidateReports = reports;
        CandidatesEvaluated = reports.Length;
        CandidatesDisjoint = disjoint;
        RunnerUp = runnerUp;
        _circumference = (long)recipe.CircumferenceM;
        _halfX = recipe.BoundHalfXM;
        _halfY = recipe.BoundHalfYM;
        ulong h = EmitterSiteHash.Offset;
        h = EmitterSiteHash.Mix(h, (ulong)recipe.AlgorithmVersion);
        h = EmitterSiteHash.Mix(h, recipe.Hash);
        h = EmitterSiteHash.Mix(h, naturalIdentity);
        h = EmitterSiteHash.Mix(h, emitterIdentity);
        h = EmitterSiteHash.Mix(h, (ulong)mainRow);
        h = EmitterSiteHash.Mix(h, site.SiteHash);
        Identity = h == 0UL ? 1UL : h;
    }

    /// <summary>Cached build keyed by (natural terrain definition hash, recipe hash, emitter registry identity).</summary>
    public static WeatherLungPlacement GetOrBuild(WeatherLungRecipe recipe, EmitterSiteRegistry emitters, ClimateMachineRecipe? climate, INaturalHeightSource source)
    {
        var key = (source.Identity, recipe.Hash, emitters.Identity);
        return Cache.GetOrAdd(key, _ => new Lazy<WeatherLungPlacement>(
            () => Build(recipe, emitters, climate, source), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public static void ClearCache() => Cache.Clear();

    private struct Candidate
    {
        public int Index, Station;
        public long Around;
        public int Across;
        public float YawDegrees;
        public bool Disjoint;
        public string Reason;
        public double Distance;
        public double Land, CutMax, CutMean, FillMean, PlinthMax;
        public bool Wet;
        public WeatherLungTerrainClass Class;
        public double Score;
    }

    /// <summary>
    /// Uncached build. Disjointness is asserted against every emitter site (595) and every Wall Engine (200, from <paramref name="climate"/> or the default recipe
    /// of the emitter recipe) including their influence radii, plus the main spire's knot links. The build throws if no candidate is left. Output is independent of the
    /// thread count (candidates are evaluated by index into a results array).
    /// </summary>
    public static WeatherLungPlacement Build(WeatherLungRecipe recipe, EmitterSiteRegistry emitters, ClimateMachineRecipe? climate, INaturalHeightSource source)
    {
        if (recipe == null) throw new ArgumentNullException(nameof(recipe));
        if (emitters == null) throw new ArgumentNullException(nameof(emitters));
        if (source == null) throw new ArgumentNullException(nameof(source));
        long start = Stopwatch.GetTimestamp();
        ClimateMachineRecipe machines = climate ?? ClimateMachineRecipe.For(emitters.Recipe);
        long circumference = (long)recipe.CircumferenceM;
        int width = (int)recipe.WidthM;
        int mainRow = emitters.MainRow;
        EmitterSite main = emitters.MainSite;
        double sea = source.SeaLevel;
        int datum = (int)Math.Round(sea + recipe.DatumAboveSeaM);

        // Usable across range: the footprint's influence box must clear each Wall Engine influence band and the ring edge by the margin.
        double acrossMin = Math.Ceiling((machines.InfluenceRadiusM + recipe.BoundHalfYM + recipe.EdgeMarginM) / 2d) * 2d;
        double acrossMax = width - acrossMin;
        var acrosses = new List<int>();
        for (double a = acrossMin; a <= acrossMax; a += recipe.AcrossStepM) acrosses.Add((int)EmitterSiteRecipe.Snap2(a));
        int centre = (int)EmitterSiteRecipe.Snap2(recipe.AcrossM);
        if (!acrosses.Contains(centre)) { acrosses.Add(centre); acrosses.Sort(); }

        var candidates = new List<Candidate>();
        for (int j = -recipe.GapsEachSide; j <= recipe.GapsEachSide; j++)
        {
            int station = ((mainRow + j) % recipe.Stations + recipe.Stations) % recipe.Stations;
            for (int o = -recipe.OffsetSteps; o <= recipe.OffsetSteps; o++)
            {
                double a = (recipe.RowPhaseM + (station + 0.5d) * recipe.RowPitchM + o * recipe.OffsetStepM) % circumference;
                if (a < 0d) a += circumference;
                long around = (long)EmitterSiteRecipe.Snap2(a) % circumference;
                double signed = SignedAround(around, main.Around, circumference);
                float yaw = signed >= 0d ? 180f : 0f; // hood away from the main spire
                foreach (int across in acrosses)
                {
                    double dw = across - main.Across;
                    candidates.Add(new Candidate
                    {
                        Index = candidates.Count, Station = station, Around = around, Across = across, YawDegrees = yaw,
                        Distance = Math.Sqrt(signed * signed + dw * dw), Reason = "",
                    });
                }
            }
        }

        var machineArounds = new long[machines.Stations];
        for (int g = 0; g < machineArounds.Length; g++)
        {
            double a = (machines.RowPhaseM + (g + 0.5d) * machines.RowPitchM) % circumference;
            if (a < 0d) a += circumference;
            machineArounds[g] = (long)EmitterSiteRecipe.Snap2(a) % circumference;
        }
        var results = new Candidate[candidates.Count];
        for (int i = 0; i < results.Length; i++)
        {
            Candidate c = candidates[i];
            c.Disjoint = IsDisjoint(recipe, emitters, machines, machineArounds, main, c.Around, c.Across, circumference, out c.Reason);
            results[i] = c;
        }
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 8)) },
            () => source.CreateWorkerCopy(),
            (i, _, worker) =>
            {
                if (results[i].Disjoint) EvaluateStencil(recipe, worker, ref results[i], datum, sea, circumference, width);
                return worker;
            },
            worker => worker.Dispose());

        int best = -1, second = -1, disjoint = 0;
        for (int i = 0; i < results.Length; i++)
        {
            if (!results[i].Disjoint) continue;
            disjoint++;
            if (best < 0 || results[i].Score < results[best].Score) { second = best; best = i; }
            else if (second < 0 || results[i].Score < results[second].Score) second = i;
        }
        if (best < 0) throw new InvalidOperationException("No Weather Lung candidate is disjoint from the emitter sites, Wall Engines and knot links; first reasons: " + string.Join("; ", results[0].Reason, results[^1].Reason));

        Candidate pick = results[best];
        WeatherLungSite site = FinishRecord(recipe, new WeatherLungSite
        {
            Station = pick.Station, CandidateIndex = pick.Index, Around = pick.Around, Across = pick.Across, PadHeight = datum, YawDegrees = pick.YawDegrees,
            Flags = pick.Wet ? WeatherLungFlags.Quay : WeatherLungFlags.None, TerrainClass = pick.Class, LandFraction = (float)pick.Land,
            CutMaxM = (float)pick.CutMax, CutMeanM = (float)pick.CutMean, FillMeanM = (float)pick.FillMean, PlinthMaxM = (float)pick.PlinthMax, Score = (float)pick.Score,
        });
        var reports = new WeatherLungCandidateReport[results.Length];
        for (int i = 0; i < reports.Length; i++) reports[i] = Report(results[i]);
        return new WeatherLungPlacement(recipe, source.Identity, emitters.Identity, mainRow, main.Around, main.Across, acrossMin, acrossMax, site, reports, disjoint,
            second < 0 ? null : reports[second])
        {
            BuildMilliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency,
        };
    }

    private static WeatherLungCandidateReport Report(Candidate c)
        => new(c.Index, c.Station, c.Around, c.Across, c.YawDegrees, c.Disjoint, c.Distance, c.Land, c.CutMax, c.CutMean, c.FillMean, c.PlinthMax, c.Wet, c.Class, c.Score);

    /// <summary>
    /// Rim stencil: 16 angles x r in {0.2, 0.6, 1.0} plus 16 at r = 1.5 (64 samples), plus the centre for the land fraction only (the centre is the carved basin).
    /// cut = natural above the datum, fill / plinth = natural below the datum (land fill and seabed alike).
    /// </summary>
    private static void EvaluateStencil(WeatherLungRecipe recipe, INaturalHeightSource worker, ref Candidate c, int datum, double sea, long circumference, int width)
    {
        // Asset-local (x, y) -> canonical offsets: du = x cos - y sin, dw = x sin + y cos (sin = 0 for the two supported yaws).
        double cosYaw = c.YawDegrees == 180f ? -1d : 1d;
        long siteAround = c.Around;
        int siteAcross = c.Across;
        int rim = 0, land = 0, wetCount = 0, waterCount = 0;
        double cutSum = 0d, cutMax = 0d, fillSum = 0d, plinthMax = 0d, waterDepthSum = 0d;
        bool wet = false;
        double Height(double x, double y)
        {
            double du = x * cosYaw, dw = y * cosYaw;
            long a = (long)Math.Round((siteAround + du) / 2d) * 2L % circumference;
            if (a < 0) a += circumference;
            int z = (int)Math.Clamp(Math.Round((siteAcross + dw) / 2d) * 2d, 0d, width);
            return worker.NaturalHeight(a, z);
        }
        double centre = Height(0d, 0d);
        double[] radii = { recipe.StencilR0, recipe.StencilR1, recipe.StencilR2, recipe.StencilOuterR };
        for (int i = 0; i < recipe.StencilAngles; i++)
        {
            double t = Math.Tau * i / recipe.StencilAngles, ct = Math.Cos(t), st = Math.Sin(t);
            foreach (double r in radii)
            {
                double h = Height((recipe.LipSemiAxisXM + recipe.RimGrowthXM * r) * ct, (recipe.LipSemiAxisYM + recipe.RimGrowthYM * r) * st);
                rim++;
                if (h > sea) land++; else { waterCount++; waterDepthSum += sea - h; }
                if (h < sea + recipe.QuayWetMarginM) { wet = true; wetCount++; }
                double cut = Math.Max(0d, h - datum), fill = Math.Max(0d, datum - h);
                cutSum += cut; cutMax = Math.Max(cutMax, cut);
                fillSum += fill; plinthMax = Math.Max(plinthMax, fill);
            }
        }
        double landFraction = land / (double)rim;
        double meanWaterDepth = waterCount == 0 ? 0d : waterDepthSum / waterCount;
        WeatherLungTerrainClass terrain = landFraction >= recipe.LandClassFraction ? WeatherLungTerrainClass.Land
            : landFraction >= recipe.CoastalLandMin ? WeatherLungTerrainClass.Coast
            : meanWaterDepth <= recipe.ShallowWaterDepthM ? WeatherLungTerrainClass.Shallow : WeatherLungTerrainClass.Deep;
        bool coastal = landFraction >= recipe.CoastalLandMin && landFraction <= recipe.CoastalLandMax && meanWaterDepth <= recipe.ShallowWaterDepthM;
        c.Land = ((centre > sea ? 1 : 0) + land) / (double)(rim + 1);
        c.CutMax = cutMax;
        c.CutMean = cutSum / rim;
        c.FillMean = fillSum / rim;
        c.PlinthMax = plinthMax;
        c.Wet = wet;
        c.Class = terrain;
        c.Score = recipe.ScoreCutMeanWeight * c.CutMean + recipe.ScoreCutMaxWeight * cutMax
            + recipe.ScorePlinthMeanWeight * c.FillMean + recipe.ScorePlinthMaxWeight * plinthMax
            + recipe.ScorePerKmFromMain * c.Distance / 1000d - (coastal ? recipe.ScoreCoastalBonus : 0d);
        _ = wetCount;
    }

    private static double SignedAround(double a, double b, long circumference)
    {
        double d = (a - b) % circumference;
        double half = circumference * 0.5d;
        if (d > half) d -= circumference; else if (d < -half) d += circumference;
        return d;
    }

    /// <summary>
    /// Conservative disjointness: the influence bounding box (axis aligned in canonical space, valid for yaw 0 and 180) against every site and machine influence
    /// radius, the ring edge, and the main spire's knot links (segments from the main spire to the neighbouring rows' outer columns).
    /// </summary>
    private static bool IsDisjoint(WeatherLungRecipe recipe, EmitterSiteRegistry emitters, ClimateMachineRecipe machines, long[] machineArounds, EmitterSite main,
        long around, int across, long circumference, out string reason)
    {
        double hx = recipe.BoundHalfXM, hy = recipe.BoundHalfYM;
        foreach (EmitterSite s in emitters.Sites)
        {
            double gx = Math.Max(0d, Math.Abs(SignedAround(s.Around, around, circumference)) - hx);
            double gy = Math.Max(0d, Math.Abs(s.Across - across) - hy);
            if (Math.Sqrt(gx * gx + gy * gy) <= s.InfluenceRadius) { reason = "overlaps emitter site " + s.Index; return false; }
        }
        double half = machines.ForecourtLengthM * 0.5d, machineInfluence = machines.InfluenceRadiusM;
        for (int g = 0; g < machineArounds.Length; g++)
            for (int side = 0; side < machines.Sides; side++)
            {
                double machineAcross = side == 0 ? 0d : machines.WidthM;
                double gx = Math.Max(0d, Math.Abs(SignedAround(machineArounds[g], around, circumference)) - hx - half);
                double gy = Math.Max(0d, Math.Abs(machineAcross - across) - hy);
                if (Math.Sqrt(gx * gx + gy * gy) <= machineInfluence) { reason = "overlaps climate machine " + (g * machines.Sides + side); return false; }
            }
        if (across - hy < 0d || across + hy > recipe.WidthM) { reason = "outside the ring width"; return false; }
        // Knot links: footprint (outer edge) plus clearance against the main-spire links.
        double fx = recipe.LipSemiAxisXM + recipe.RimGrowthXM + recipe.KnotClearM, fy = recipe.LipSemiAxisYM + recipe.RimGrowthYM + recipe.KnotClearM;
        foreach (double rowSign in new[] { -1d, 1d })
            foreach (double endAcross in new[] { 2d * recipe.KnotAcrossLowM - main.Across, 2d * recipe.KnotAcrossHighM - main.Across })
                for (int k = 0; k <= 256; k++)
                {
                    double u = k / 256d;
                    double linkAround = main.Around + rowSign * recipe.RowPitchM * u, linkAcross = main.Across + (endAcross - main.Across) * u;
                    if (Math.Abs(SignedAround(linkAround, around, circumference)) < fx && Math.Abs(linkAcross - across) < fy) { reason = "near a main-spire knot link"; return false; }
                }
        reason = "";
        return true;
    }

    private static WeatherLungSite FinishRecord(WeatherLungRecipe recipe, WeatherLungSite s)
    {
        ulong h = EmitterSiteHash.Offset;
        h = EmitterSiteHash.Mix(h, recipe.Hash);
        h = EmitterSiteHash.Mix(h, (ulong)s.Station);
        h = EmitterSiteHash.Mix(h, (ulong)s.CandidateIndex);
        h = EmitterSiteHash.Mix(h, (ulong)s.Around);
        h = EmitterSiteHash.Mix(h, (ulong)(uint)s.Across);
        h = EmitterSiteHash.Mix(h, (ulong)(uint)s.PadHeight);
        h = EmitterSiteHash.Mix(h, BitConverter.SingleToUInt32Bits(s.YawDegrees));
        h = EmitterSiteHash.Mix(h, (ulong)s.Flags);
        h = EmitterSiteHash.Mix(h, (ulong)s.TerrainClass);
        h = EmitterSiteHash.Mix(h, BitConverter.SingleToUInt32Bits(s.LandFraction));
        h = EmitterSiteHash.Mix(h, BitConverter.SingleToUInt32Bits(s.CutMaxM));
        h = EmitterSiteHash.Mix(h, BitConverter.SingleToUInt32Bits(s.CutMeanM));
        h = EmitterSiteHash.Mix(h, BitConverter.SingleToUInt32Bits(s.FillMeanM));
        h = EmitterSiteHash.Mix(h, BitConverter.SingleToUInt32Bits(s.PlinthMaxM));
        h = EmitterSiteHash.Mix(h, BitConverter.SingleToUInt32Bits(s.Score));
        return new WeatherLungSite
        {
            Station = s.Station, CandidateIndex = s.CandidateIndex, Around = s.Around, Across = s.Across, PadHeight = s.PadHeight, YawDegrees = s.YawDegrees,
            Flags = s.Flags, TerrainClass = s.TerrainClass, LandFraction = s.LandFraction, CutMaxM = s.CutMaxM, CutMeanM = s.CutMeanM, FillMeanM = s.FillMeanM,
            PlinthMaxM = s.PlinthMaxM, Score = s.Score, SiteHash = h == 0UL ? 1UL : h,
        };
    }

    // ---- Lookup --------------------------------------------------------------------------------------------------

    /// <summary>
    /// O(1) influence lookup: signed canonical offsets (<paramref name="du"/> along the ring, <paramref name="dw"/> across) from the site centre, true while the
    /// point is inside the influence bounding box. The operator makes the exact decision.
    /// </summary>
    public bool TryGetInfluence(double around, double across, out double du, out double dw)
    {
        double a = around % _circumference;
        if (a < 0d) a += _circumference;
        du = SignedAround(a, Site.Around, _circumference);
        dw = across - Site.Across;
        return Math.Abs(du) < _halfX && Math.Abs(dw) < _halfY;
    }

    /// <summary>Asset-local (Blender x, y) of canonical offsets: x = du cos + dw sin, y = dw cos - du sin (derived from <c>SiteTransform</c>).</summary>
    public void ToAssetLocal(double du, double dw, out double x, out double y)
    {
        double c = Site.CosYaw, s = Site.SinYaw;
        x = du * c + dw * s;
        y = dw * c - du * s;
    }

    /// <summary>Canonical offsets of an asset-local (x, y): the inverse of <see cref="ToAssetLocal"/>.</summary>
    public void FromAssetLocal(double x, double y, out double du, out double dw)
    {
        double c = Site.CosYaw, s = Site.SinYaw;
        du = x * c - y * s;
        dw = x * s + y * c;
    }

    /// <summary>One-line placement report used by the test runner and diagnostics.</summary>
    public string Describe(string recipeLabel)
    {
        string runner = RunnerUp is { } r
            ? $"runner_up=station{r.Station}/around{r.Around}/across{r.Across}/class{r.TerrainClass}/score{r.Score:0.0}"
            : "runner_up=none";
        double signed = SignedAround(Site.Around, MainAround, _circumference);
        double dist = Math.Sqrt(signed * signed + (double)(Site.Across - MainAcross) * (Site.Across - MainAcross));
        return $"WEATHER_LUNG_PLACEMENT seed={Recipe.Seed} recipe={recipeLabel} station={Site.Station} around={Site.Around} across={Site.Across} datum={Site.PadHeight} "
            + $"yaw={Site.YawDegrees:0} dist_main_km={dist / 1000d:0.0} class={Site.TerrainClass} land={Site.LandFraction:0.000} cut_max={Site.CutMaxM:0.0} cut_mean={Site.CutMeanM:0.0} "
            + $"fill_mean={Site.FillMeanM:0.0} plinth_max={Site.PlinthMaxM:0.0} quay={(Site.IsQuay ? "yes" : "no")} score={Site.Score:0.0} {runner} mainRow={MainRow}";
    }
}
