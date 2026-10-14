using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Spec section 8 tests 3, 4 (synthetic + real sites) and 11 for P2: pad flatness, island enclosure, C0 continuity,
/// terrace risers, max-only raise and mask classes on synthetic h0 fields, plus the operator run over the real registry
/// sites of both backends, and a per-call timing.
/// </summary>
public partial class EmitterSiteSurfaceOperatorTest : RefCounted
{
    const double Sea = 0d;
    const double MinPossible = -8000d;

    sealed class Scenario
    {
        public string Name = "";
        public Func<double, double, double> H0 = (_, _) => 0d;
        public EmitterSite Site;
    }

    static EmitterSiteRecipe Recipe = EmitterSiteRecipe.Default(1337);
    static EmitterSiteSurfaceOperator Op = new(Recipe, Sea);

    static EmitterSiteKindShape Shape(SiteKind kind) => Recipe.ShapeFor(kind);

    static double RaiseRadius(EmitterSiteRecipe r, SiteKind kind = SiteKind.Regular)
        => Shape(kind).ShelfRadiusM + Math.Max(0d, Sea - r.ShelfDepthM - MinPossible + r.IslandSoftK) / r.FlankSlope;

    static EmitterSite MakeSite(int pad, EmitterSiteFlags flags, bool raised, SiteKind kind = SiteKind.Regular)
    {
        double raise = raised ? RaiseRadius(Recipe, kind) : 0d;
        return new EmitterSite
        {
            PadHeight = pad, Flags = flags, Kind = kind,
            InfluenceRadius = (float)Math.Max(Shape(kind).SkirtMaxRadiusM, raise), RaiseRadius = (float)raise,
        };
    }

    static List<Scenario> Scenarios()
    {
        double s30 = Math.Tan(30d * Math.PI / 180d);
        var list = new List<Scenario>();
        list.Add(new Scenario { Name = "flat", H0 = (u, w) => 100d, Site = MakeSite(100, EmitterSiteFlags.Land, false) });
        list.Add(new Scenario { Name = "slope30", H0 = (u, w) => 100d + s30 * u, Site = MakeSite(100, EmitterSiteFlags.Land, false) });
        list.Add(new Scenario { Name = "ridge", H0 = (u, w) => 100d + 400d * Math.Exp(-(w * w) / (2d * 300d * 300d)), Site = MakeSite(100, EmitterSiteFlags.Land, false) });
        list.Add(new Scenario { Name = "ocean-4500", H0 = (u, w) => -4500d, Site = MakeSite(24, EmitterSiteFlags.Island | EmitterSiteFlags.Ocean, true) });
        list.Add(new Scenario { Name = "trench-7500", H0 = (u, w) => -7500d, Site = MakeSite(24, EmitterSiteFlags.Island | EmitterSiteFlags.Ocean, true) });
        list.Add(new Scenario
        {
            Name = "summit7km-4km-away-land",
            H0 = (u, w) => 500d + 6500d * Math.Exp(-((u - 4000d) * (u - 4000d) + w * w) / (2d * 1500d * 1500d)),
            Site = MakeSite(500, EmitterSiteFlags.Land, false),
        });
        list.Add(new Scenario
        {
            Name = "summit7km-island-with-hills",
            H0 = (u, w) => -4500d + 11500d * Math.Exp(-((u + 3500d) * (u + 3500d) + w * w) / (2d * 1200d * 1200d)),
            Site = MakeSite(24, EmitterSiteFlags.Island | EmitterSiteFlags.Ocean, true),
        });
        list.Add(new Scenario
        {
            Name = "deep-valley-enclosure-raise",
            H0 = (u, w) => 3200d - 3600d * Math.Exp(-Math.Pow((Math.Sqrt(u * u + w * w) - 1500d) / 250d, 2d)),
            Site = MakeSite(3200, EmitterSiteFlags.Land | EmitterSiteFlags.EnclosureRaise, true),
        });
        // Main-site (kind) scenarios: pad R1000 / apron 150, enclosure R4000, cover depth 1922 - 200.
        const SiteKind M = SiteKind.Main;
        list.Add(new Scenario { Name = "main-flat", H0 = (u, w) => 100d, Site = MakeSite(100, EmitterSiteFlags.Land, false, M) });
        list.Add(new Scenario { Name = "main-slope30", H0 = (u, w) => 100d + s30 * u, Site = MakeSite(100, EmitterSiteFlags.Land, false, M) });
        list.Add(new Scenario { Name = "main-ridge", H0 = (u, w) => 100d + 600d * Math.Exp(-(w * w) / (2d * 700d * 700d)), Site = MakeSite(100, EmitterSiteFlags.Land, false, M) });
        list.Add(new Scenario { Name = "main-ocean-4500", H0 = (u, w) => -4500d, Site = MakeSite(24, EmitterSiteFlags.Island | EmitterSiteFlags.Ocean, true, M) });
        list.Add(new Scenario { Name = "main-trench-7500", H0 = (u, w) => -7500d, Site = MakeSite(24, EmitterSiteFlags.Island | EmitterSiteFlags.Ocean, true, M) });
        list.Add(new Scenario
        {
            Name = "main-summit7km-island-with-hills",
            H0 = (u, w) => -4500d + 11500d * Math.Exp(-((u + 7000d) * (u + 7000d) + w * w) / (2d * 2000d * 2000d)),
            Site = MakeSite(24, EmitterSiteFlags.Island | EmitterSiteFlags.Ocean, true, M),
        });
        list.Add(new Scenario
        {
            Name = "main-deep-valley-enclosure-raise",
            H0 = (u, w) => 3200d - 3600d * Math.Exp(-Math.Pow((Math.Sqrt(u * u + w * w) - 3000d) / 400d, 2d)),
            Site = MakeSite(3200, EmitterSiteFlags.Land | EmitterSiteFlags.EnclosureRaise, true, M),
        });
        return list;
    }

    public int Run()
    {
        var failures = new List<string>();
        try
        {
            foreach (Scenario sc in Scenarios())
            {
                CheckPadFlat(sc, failures);
                CheckContinuity(sc, failures);
                CheckBoundaryExactness(sc, failures);
            }
            CheckIslandEnclosure(failures);
            CheckTerraces(failures);
            CheckMaxOnlyRaise(failures);
            CheckMasks(failures);
            CheckMonotoneProfile(failures);
            CheckWorstCases(failures);
            RunRealSites(failures);
            Benchmark();
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
        if (failures.Count != 0)
        {
            Console.Error.WriteLine("EMITTER_SITE_OPERATOR=FAIL " + string.Join(" | ", failures.Take(40)));
            return 1;
        }
        Console.WriteLine("EMITTER_SITE_OPERATOR=PASS");
        return 0;
    }

    static double ShapeAt(Scenario sc, double r, double angle, out SiteSurfaceMask mask)
    {
        double du = r * Math.Cos(angle), dw = r * Math.Sin(angle);
        return Op.Shape(sc.Site, du, dw, sc.H0(du, dw), out mask);
    }

    // ---- test 3: pad flatness --------------------------------------------------------------------------

    static void CheckPadFlat(Scenario sc, List<string> failures)
    {
        int bad = 0;
        double maxApronSlope = 0d;
        EmitterSiteKindShape shape = Shape(sc.Site.Kind);
        for (int a = 0; a < 360; a += 3)
        {
            double angle = a * Math.PI / 180d;
            for (double r = 0d; r <= shape.RhoStartM - 0.01d; r += 2d)
            {
                double h = ShapeAt(sc, r, angle, out _);
                if (h != sc.Site.PadHeight) bad++;
            }
            double prev = ShapeAt(sc, shape.PadRadiusM - 10d, angle, out _);
            for (double r = shape.PadRadiusM - 8d; r <= shape.RhoStartM + 30d; r += 2d)
            {
                double h = ShapeAt(sc, r, angle, out _);
                if (r <= shape.RhoStartM) maxApronSlope = Math.Max(maxApronSlope, Math.Abs(h - prev) / 2d);
                prev = h;
            }
        }
        if (bad != 0) failures.Add($"{sc.Name}: {bad} pad samples not exactly P");
        if (maxApronSlope > 0.5d) failures.Add($"{sc.Name}: apron slope {maxApronSlope}");
    }

    // ---- test 11: C0 continuity ------------------------------------------------------------------------

    static void CheckContinuity(Scenario sc, List<string> failures)
    {
        double influence = sc.Site.InfluenceRadius;
        double worstRatio = 0d, worstAbs = 0d;
        double jumpAtKnot = 0d;
        EmitterSiteKindShape shape = Shape(sc.Site.Kind);
        for (int a = 0; a < 360; a += 15)
        {
            double angle = (a + 0.37d) * Math.PI / 180d;
            double max1 = 0d, max025 = 0d;
            double prev1 = ShapeAt(sc, 1d, angle, out _), prev025 = prev1;
            for (double r = 2d; r <= influence + 300d; r += 1d)
            {
                double h = ShapeAt(sc, r, angle, out _);
                max1 = Math.Max(max1, Math.Abs(h - prev1));
                prev1 = h;
            }
            // Same line, quarter step, same range.
            prev025 = ShapeAt(sc, 1d, angle, out _);
            for (double r = 1.25d; r <= influence + 300d; r += 0.25d)
            {
                double h = ShapeAt(sc, r, angle, out _);
                max025 = Math.Max(max025, Math.Abs(h - prev025));
                prev025 = h;
            }
            worstAbs = Math.Max(worstAbs, max1);
            // A jump does not shrink with the step; a Lipschitz function shrinks about 4x. A kink with a slope break
            // shrinks by the same 4x. Threshold 0.5 leaves ample room.
            if (max1 > 0.05d) worstRatio = Math.Max(worstRatio, max025 / max1);
        }
        // Explicit knots: pad edge, apron end, terrace start, island knots, skirt, influence boundary.
        double[] knots =
        {
            shape.PadRadiusM, shape.RhoStartM, shape.RhoStartM + 40d,
            shape.EnclosureRadiusM, shape.ShoreRadiusM, shape.ShelfRadiusM,
            shape.SkirtFadeStartM, shape.SkirtMaxRadiusM, influence,
        };
        const double Eps = 1e-4;
        foreach (double knot in knots)
            for (int a = 0; a < 360; a += 45)
            {
                double angle = (a + 11d) * Math.PI / 180d;
                double jump = Math.Abs(ShapeAt(sc, knot + Eps, angle, out _) - ShapeAt(sc, knot - Eps, angle, out _));
                jumpAtKnot = Math.Max(jumpAtKnot, jump);
            }
        // The summit scenarios have slopes of order 20, so 2 * Eps * 20 = 4e-3 is the Lipschitz allowance.
        if (jumpAtKnot > 0.02d) failures.Add($"{sc.Name}: jump {jumpAtKnot:E2} across a knot");
        if (worstRatio > 0.5d) failures.Add($"{sc.Name}: step-halving ratio {worstRatio:F2} (jump suspected)");
        Console.WriteLine($"EMITTER_SITE_OPERATOR_C0 {sc.Name} max_dh_per_1m={worstAbs:F2} step_ratio={worstRatio:F2} max_knot_jump_1e-4m={jumpAtKnot:E2}");
    }

    static void CheckBoundaryExactness(Scenario sc, List<string> failures)
    {
        double influence = sc.Site.InfluenceRadius;
        double worst = 0d;
        for (int a = 0; a < 360; a += 5)
        {
            double angle = a * Math.PI / 180d;
            foreach (double r in new[] { influence, influence + 1d, influence + 50d, influence * 3d })
            {
                double du = r * Math.Cos(angle), dw = r * Math.Sin(angle);
                double h0 = sc.H0(du, dw);
                if (Op.Shape(sc.Site, du, dw, h0) != h0) failures.Add($"{sc.Name}: shaped != h0 at r={r}");
            }
            double rr = influence - 1e-3;
            double hh = ShapeAt(sc, rr, angle, out _);
            worst = Math.Max(worst, Math.Abs(hh - sc.H0(rr * Math.Cos(angle), rr * Math.Sin(angle))));
        }
        if (worst > 1e-3d) failures.Add($"{sc.Name}: shaped differs from h0 by {worst:F4} just inside R_inf");
    }

    // ---- test 4 (synthetic): island / enclosure ---------------------------------------------------------

    static void CheckIslandEnclosure(List<string> failures)
    {
        foreach (Scenario sc in Scenarios())
        {
            bool covered = (sc.Site.Flags & (EmitterSiteFlags.Island | EmitterSiteFlags.EnclosureRaise)) != 0;
            if (!covered) continue;
            double pad = sc.Site.PadHeight;
            EmitterSiteKindShape shape = Shape(sc.Site.Kind);
            double need = Math.Max(Sea + Recipe.DryMarginM, pad - shape.CoverDepthM);
            double min = double.MaxValue, minClearance = double.MaxValue;
            for (int a = 0; a < 720; a++)
            {
                double angle = a * Math.PI / 360d + 0.01d;
                for (double r = 0d; r <= shape.EnclosureRadiusM; r += 5d)
                {
                    double h = ShapeAt(sc, r, angle, out _);
                    min = Math.Min(min, h);
                    minClearance = Math.Min(minClearance, h - (pad - shape.CavernRoofDepthM));
                }
            }
            if (min < need - 1e-9) failures.Add($"{sc.Name}: surface {min:F2} below required {need:F2} within R_enc");
            if (minClearance < Recipe.EnclosureCoverM - 1e-9 && pad - shape.CoverDepthM > Sea + 2d) failures.Add($"{sc.Name}: roof clearance {minClearance:F2} < cover");
            Console.WriteLine($"EMITTER_SITE_OPERATOR_ENCLOSURE {sc.Name} min_surface_within_2km={min:F2} required={need:F2} roof_clearance={minClearance:F1}");
        }
    }

    // ---- terraces --------------------------------------------------------------------------------------

    static void CheckTerraces(List<string> failures)
    {
        CheckTerraces(failures, SiteKind.Regular);
        CheckTerraces(failures, SiteKind.Main);
    }

    static void CheckTerraces(List<string> failures, SiteKind kind)
    {
        // A huge plateau: the cone clamp fully controls the surface, giving the terraced bowl.
        var sc = new Scenario { Name = "plateau+5000", H0 = (u, w) => 5100d, Site = MakeSite(100, EmitterSiteFlags.Land, false, kind) };
        double period = Recipe.CutTerraceBenchM + Recipe.CutTerraceRiserM / Recipe.CutRiserSlope;
        double beta = Recipe.CutTerraceBenchM / period;
        double rho0 = Shape(kind).RhoStartM;
        double worstRiser = 0d, maxSlope = 0d;
        double prevBench = double.NaN;
        for (int n = 2; n < 40; n++)
        {
            double r = rho0 + (n + beta * 0.5d) * period;
            double h = ShapeAt(sc, r, 0.3d, out _);
            if (!double.IsNaN(prevBench)) worstRiser = Math.Max(worstRiser, Math.Abs(h - prevBench - Recipe.CutTerraceRiserM));
            prevBench = h;
            // benches are flat across their full width
            double h2 = ShapeAt(sc, r + 0.4d * beta * period, 0.3d, out _), h3 = ShapeAt(sc, r - 0.4d * beta * period, 0.3d, out _);
            if (Math.Abs(h2 - h) > 0.5d || Math.Abs(h3 - h) > 0.5d) failures.Add($"terrace {n} bench not flat");
        }
        double prev = ShapeAt(sc, rho0 + 100d, 0.3d, out _);
        for (double r = rho0 + 100.25d; r < rho0 + 2000d; r += 0.25d)
        {
            double h = ShapeAt(sc, r, 0.3d, out _);
            maxSlope = Math.Max(maxSlope, Math.Abs(h - prev) / 0.25d);
            if (h < prev - 1e-9) failures.Add("terraces not monotone at r=" + r);
            prev = h;
        }
        double expectedPeak = 1.5d * Recipe.CutTerraceRiserM / (period * (1d - beta));
        if (worstRiser > 1e-6d) failures.Add($"terrace riser height error {worstRiser}");
        if (maxSlope > expectedPeak * 1.02d) failures.Add($"terrace peak slope {maxSlope} > {expectedPeak}");
        double avgDeg = Math.Atan(Recipe.CutTerraceRiserM / period) * 180d / Math.PI;
        Console.WriteLine($"EMITTER_SITE_OPERATOR_TERRACE {kind} period={period:F2}m riser_error={worstRiser:E1} avg_wall_deg={avgDeg:F1} peak_slope={maxSlope:F2} ({Math.Atan(maxSlope) * 180d / Math.PI:F1} deg, analytic {expectedPeak:F2})");
    }

    // ---- max-only raise --------------------------------------------------------------------------------

    static void CheckMaxOnlyRaise(List<string> failures)
    {
        foreach (string name in new[] { "ocean-4500", "trench-7500", "summit7km-island-with-hills" })
        {
            Scenario sc = Scenarios().First(s => s.Name == name);
            int lowered = 0;
            for (int a = 0; a < 360; a += 2)
            {
                double angle = a * Math.PI / 180d;
                for (double r = 1d; r < sc.Site.InfluenceRadius + 500d; r += 7d)
                {
                    double du = r * Math.Cos(angle), dw = r * Math.Sin(angle);
                    double h0 = sc.H0(du, dw);
                    double h = Op.Shape(sc.Site, du, dw, h0);
                    // The cone clamp is the only lowering stage and only acts above the pad bound U >= P.
                    if (h < h0 - 1e-9 && h0 <= sc.Site.PadHeight) lowered++;
                }
            }
            if (lowered != 0) failures.Add($"{name}: raise lowered {lowered} samples that were below the pad height");
        }
    }

    // ---- masks -----------------------------------------------------------------------------------------

    static void CheckMasks(List<string> failures)
    {
        void Expect(string label, bool ok) { if (!ok) failures.Add("mask: " + label); }
        Scenario flat = Scenarios().First(s => s.Name == "flat");
        Op.Shape(flat.Site, 200d, 0d, 100d, out SiteSurfaceMask pad);
        Expect("pad class", pad.Class == SiteSurfaceClass.Pad && pad.Hardscape == 1f && !pad.VegetationAllowed && pad.Material == SiteMaterialHint.Rock);
        Op.Shape(flat.Site, 380d, 0d, 100d, out SiteSurfaceMask apron);
        Expect("apron hardscape fades", apron.Class == SiteSurfaceClass.Pad && apron.Hardscape > 0f && apron.Hardscape < 1f);
        Op.Shape(flat.Site, 900d, 0d, 100d, out SiteSurfaceMask untouched);
        Expect("flat untouched is None + vegetation", untouched.Class == SiteSurfaceClass.None && untouched.VegetationAllowed);
        Op.Shape(flat.Site, 5000d, 0d, 100d, out SiteSurfaceMask outside);
        Expect("outside influence is default", outside.Class == SiteSurfaceClass.None && outside.Rock == 0f);

        var wall = new Scenario { H0 = (u, w) => 5100d, Site = MakeSite(100, EmitterSiteFlags.Land, false) };
        Op.Shape(wall.Site, 1000d, 0d, 5100d, out SiteSurfaceMask cut);
        Expect("cut face rock", cut.Class == SiteSurfaceClass.CutFace && cut.Material == SiteMaterialHint.Rock && !cut.VegetationAllowed && cut.Rock > 0.99f);

        Op.Shape(flat.Site, 1500d, 0d, -1500d, out SiteSurfaceMask fill);
        Expect("fill class", fill.Class == SiteSurfaceClass.Fill && fill.VegetationAllowed);

        Scenario ocean = Scenarios().First(s => s.Name == "ocean-4500");
        Op.Shape(ocean.Site, 2100d, 0d, -4500d, out SiteSurfaceMask beach);
        Expect("beach", beach.Class == SiteSurfaceClass.Beach && beach.Material == SiteMaterialHint.Sand && !beach.VegetationAllowed);
        Op.Shape(ocean.Site, 1000d, 0d, -4500d, out SiteSurfaceMask dome);
        Expect("dome flank", dome.Class == SiteSurfaceClass.IslandFlank && dome.VegetationAllowed && dome.Sand == 0f);
        Op.Shape(ocean.Site, 5000d, 0d, -4500d, out SiteSurfaceMask seamount);
        Expect("seamount flank", seamount.Class == SiteSurfaceClass.IslandFlank && seamount.Sand == 0f);
        Op.Shape(ocean.Site, 13_000d, 0d, -4500d, out SiteSurfaceMask far);
        Expect("far ocean none", far.Class == SiteSurfaceClass.None);
    }

    static void CheckMonotoneProfile(List<string> failures)
    {
        foreach (SiteKind kind in new[] { SiteKind.Regular, SiteKind.Main })
        {
            EmitterSiteKindShape shape = Shape(kind);
            foreach (int pad in new[] { 24, 100, 800, 3400 })
            {
                double prev = Op.IslandProfile(0d, pad, kind);
                for (double r = 1d; r < 30_000d; r += 1d)
                {
                    double e = Op.IslandProfile(r, pad, kind);
                    if (e > prev + 1e-9) { failures.Add($"{kind} island profile not monotone at r={r} pad={pad}"); break; }
                    prev = e;
                }
                if (Math.Abs(Op.IslandProfile(shape.EnclosureRadiusM, pad, kind) - (Sea + Recipe.IslandCoastM)) > 1e-9) failures.Add($"{kind} island coast knot");
                if (Math.Abs(Op.IslandProfile(shape.ShoreRadiusM, pad, kind) - (Sea - 0.5d)) > 1e-9) failures.Add($"{kind} island shore knot");
                if (Math.Abs(Op.IslandProfile(shape.ShelfRadiusM, pad, kind) - (Sea - Recipe.ShelfDepthM)) > 1e-9) failures.Add($"{kind} island shelf knot");
            }
        }
    }

    /// <summary>Documents the worst dimple on terrain that already satisfies the cone, and the worst cut under a summit.</summary>
    static void CheckWorstCases(List<string> failures)
    {
        Scenario flat = Scenarios().First(s => s.Name == "flat");
        double worstDip = 0d;
        for (double r = 411d; r < 3100d; r += 0.5d) worstDip = Math.Max(worstDip, 100d - Op.Shape(flat.Site, r, 0d, 100d));
        Console.WriteLine($"EMITTER_SITE_OPERATOR_FLAT worst_dimple_on_flat_terrain={worstDip:F2}m (k/16 bound {Recipe.SoftClampM / 16d:F2})");
        if (worstDip > Recipe.SoftClampM / 16d + 1e-9) failures.Add("flat dimple " + worstDip);
        Scenario mainFlat = Scenarios().First(s => s.Name == "main-flat");
        double mainDip = 0d;
        for (double r = 1151d; r < 6100d; r += 0.5d) mainDip = Math.Max(mainDip, 100d - Op.Shape(mainFlat.Site, r, 0d, 100d));
        Console.WriteLine($"EMITTER_SITE_OPERATOR_FLAT main worst_dimple_on_flat_terrain={mainDip:F2}m");
        if (mainDip > Recipe.SoftClampM / 16d + 1e-9) failures.Add("main flat dimple " + mainDip);
        Scenario summit = Scenarios().First(s => s.Name.StartsWith("summit7km-4km"));
        double maxCut = 0d, maxSlope = 0d;
        for (int a = 0; a < 360; a += 5)
        {
            double angle = a * Math.PI / 180d;
            double prev = ShapeAt(summit, 411d, angle, out _);
            for (double r = 412d; r < 3100d; r += 2d)
            {
                double du = r * Math.Cos(angle), dw = r * Math.Sin(angle);
                double h = Op.Shape(summit.Site, du, dw, summit.H0(du, dw));
                maxCut = Math.Max(maxCut, summit.H0(du, dw) - h);
                maxSlope = Math.Max(maxSlope, Math.Abs(h - prev) / 2d);
                prev = h;
            }
        }
        Console.WriteLine($"EMITTER_SITE_OPERATOR_SUMMIT forced_site_next_to_7km_summit max_cut={maxCut:F0}m max_slope={maxSlope:F2} ({Math.Atan(maxSlope) * 180d / Math.PI:F0} deg; skirt cliff when the cone cannot meet the summit - nudge avoids it)");
    }

    // ---- real registry sites ---------------------------------------------------------------------------

    static void RunRealSites(List<string> failures)
    {
        foreach (bool gen17 in new[] { true, false })
        {
            string name = gen17 ? "gen17_v4_seed_1337" : "gen16_certified_seed_1337";
            using RingTerrainSampler sampler = gen17 ? EmitterSiteTestWorlds.CreateGen17(1337) : EmitterSiteTestWorlds.CreateGen16(1337);
            using INaturalHeightSource source = EmitterSiteHeightSources.Create(sampler);
            EmitterSiteRegistry registry = EmitterSiteRegistry.Build(Recipe, source);
            var op = new EmitterSiteSurfaceOperator(Recipe, source.SeaLevel);
            long circumference = EmitterSiteTestWorlds.Circumference;

            double Natural(in EmitterSite s, double du, double dw)
            {
                long a = ((s.Around + (long)Math.Round(du)) % circumference + circumference) % circumference;
                int x = Math.Clamp(s.Across + (int)Math.Round(dw), 0, EmitterSiteTestWorlds.Width);
                return source.NaturalHeight(a, x);
            }

            double worstPad = 0d, maxSlopeOther = 0d, maxSlopeCut = 0d, maxNaturalSlope = 0d, maxCutObserved = 0d, maxFillObserved = 0d, maxIslandRaise = 0d;
            int enclosureFail = 0, wetFail = 0, roofFail = 0;
            var failingSites = new List<string>();
            var slopes = new List<double>();
            double sea = source.SeaLevel;
            foreach (EmitterSite s in registry.Sites)
            {
                EmitterSiteKindShape shape = Recipe.ShapeFor(s.Kind);
                double scale = shape.PadRadiusM / Recipe.PadRadiusM;
               
                // Pad flatness at 24 azimuths x 8 radii (scaled with the kind: 0..R_pad..apron end).
                for (int a = 0; a < 24; a++)
                {
                    double angle = a * Math.PI / 12d;
                    foreach (double r0 in new[] { 0d, 60d, 150d, 250d, 330d, 350d, 380d, 410d })
                    {
                        double r = r0 * scale;
                        double du = Math.Round(r * Math.Cos(angle)), dw = Math.Round(r * Math.Sin(angle));
                        double h = op.Shape(s, du, dw, Natural(s, du, dw));
                        if (r <= shape.PadRadiusM) worstPad = Math.Max(worstPad, Math.Abs(h - s.PadHeight));
                    }
                }
                // Enclosure: every column within R_enc.
                bool siteFails = false;
                double need = Math.Max(sea + Recipe.DryMarginM, s.PadHeight - shape.CoverDepthM);
                for (int a = 0; a < 36 && !siteFails; a++)
                {
                    double angle = (a * 10d + 3d) * Math.PI / 180d;
                    for (double r = 0d; r <= shape.EnclosureRadiusM; r += shape.EnclosureRadiusM / 20d)
                    {
                        double du = Math.Round(r * Math.Cos(angle)), dw = Math.Round(r * Math.Sin(angle));
                        double h = op.Shape(s, du, dw, Natural(s, du, dw));
                        if (h < need - 1e-6)
                        {
                            enclosureFail++; siteFails = true;
                            if (h < sea + Recipe.DryMarginM) wetFail++; else roofFail++;
                            failingSites.Add($"site {s.Index} ({(s.IsIsland ? "island" : "land")} P={s.PadHeight}) h={h:F1} need={need:F1} at r={r:F0}");
                            break;
                        }
                    }
                }
                // Slopes along 4 radial lines, 10 m step, from the apron end to the skirt.
                for (int a = 0; a < 4; a++)
                {
                    double angle = (a * 90d + 17d) * Math.PI / 180d;
                    double prevS = 0d, prevN = 0d; bool prevCut = false; bool first = true;
                    double prevR = 0d;
                    for (double r = shape.RhoStartM + 2d; r <= shape.SkirtMaxRadiusM + 400d; r += 10d)
                    {
                        double du = Math.Round(r * Math.Cos(angle)), dw = Math.Round(r * Math.Sin(angle));
                        double n = Natural(s, du, dw);
                        double h = op.Shape(s, du, dw, n, out SiteSurfaceMask mask);
                        double rr = Math.Sqrt(du * du + dw * dw);
                        bool cut = mask.Class == SiteSurfaceClass.CutFace;
                        if (!s.IsIsland) maxCutObserved = Math.Max(maxCutObserved, n - h);
                        if (s.IsIsland) maxIslandRaise = Math.Max(maxIslandRaise, h - n); else maxFillObserved = Math.Max(maxFillObserved, h - n);
                        if (!first)
                        {
                            double dist = Math.Max(1d, rr - prevR);
                            double slope = Math.Abs(h - prevS) / dist, nslope = Math.Abs(n - prevN) / dist;
                            maxNaturalSlope = Math.Max(maxNaturalSlope, nslope);
                            if (cut || prevCut) maxSlopeCut = Math.Max(maxSlopeCut, slope);
                            else { maxSlopeOther = Math.Max(maxSlopeOther, slope); slopes.Add(slope); }
                        }
                        first = false; prevS = h; prevN = n; prevCut = cut; prevR = rr;
                    }
                }
            }
            EmitterSite main = registry.MainSite;
            {
                // Dense main-site checks: exact pad (every 2 m lattice vertex on 72 azimuths), enclosure dry + cover, bowl observed cut/fill.
                double padDev = 0d, minSurface = double.MaxValue, cutObs = 0d, fillObs = 0d;
                for (int a = 0; a < 72; a++)
                {
                    double angle = (a * 5d + 1.3d) * Math.PI / 180d;
                    for (double r = 0d; r <= Recipe.Main.SkirtMaxRadiusM + 200d; r += 2d)
                    {
                        double du = Math.Round(r * Math.Cos(angle) / 2d) * 2d, dw = Math.Round(r * Math.Sin(angle) / 2d) * 2d;
                        double nat = Natural(main, du, dw);
                        double h = op.Shape(main, du, dw, nat);
                        double rr = Math.Sqrt(du * du + dw * dw);
                        if (rr <= Recipe.Main.PadRadiusM) padDev = Math.Max(padDev, Math.Abs(h - main.PadHeight));
                        if (rr <= Recipe.Main.EnclosureRadiusM) minSurface = Math.Min(minSurface, h);
                        if (!main.IsIsland) cutObs = Math.Max(cutObs, nat - h);
                        fillObs = Math.Max(fillObs, h - nat);
                    }
                }
                double mainNeed = Math.Max(sea + Recipe.DryMarginM, main.PadHeight - Recipe.Main.CoverDepthM);
                Console.WriteLine($"EMITTER_SITE_OPERATOR_MAIN {name} row={registry.MainRow} {(main.IsIsland ? "island" : "land")} pad={main.PadHeight} pad_deviation={padDev:E1}m min_surface_within_R_enc={minSurface:F1} required={mainNeed:F1} observed_cut={cutObs:F0}m observed_fill_or_raise={fillObs:F0}m registry_cut={main.MaxCutMeters:F0}m registry_fill={main.MaxFillMeters:F0}m influence={main.InfluenceRadius:F0}m");
                if (padDev != 0d) failures.Add(name + ": main pad not exactly flat");
                if (minSurface < mainNeed - 1e-6) failures.Add($"{name}: main enclosure violated ({minSurface:F1} < {mainNeed:F1})");
                // Outside the influence radius the shaped height equals the natural height exactly.
                for (int a = 0; a < 36; a++)
                {
                    double angle = a * Math.PI / 18d;
                    foreach (double r in new[] { main.InfluenceRadius, main.InfluenceRadius + 1d, main.InfluenceRadius + 500d })
                    {
                        double du = Math.Round(r * Math.Cos(angle)), dw = Math.Round(r * Math.Sin(angle));
                        double nat = Natural(main, du, dw);
                        if (op.Shape(main, du, dw, nat) != nat) failures.Add(name + ": main shaped != natural beyond R_inf");
                    }
                }
            }
            slopes.Sort();
            double Deg(double slope) => Math.Atan(slope) * 180d / Math.PI;
            Console.WriteLine($"EMITTER_SITE_OPERATOR_REAL {name} sites={registry.Count} worst_pad_deviation={worstPad:E1}m");
            Console.WriteLine($"EMITTER_SITE_OPERATOR_REAL {name} slope_outside_cut_faces p99={Deg(slopes[(int)(slopes.Count * 0.99)]):F1}deg max={Deg(maxSlopeOther):F1}deg; on_cut_faces max={Deg(maxSlopeCut):F1}deg; natural_max={Deg(maxNaturalSlope):F1}deg (10 m step, 4 lines/site)");
            Console.WriteLine($"EMITTER_SITE_OPERATOR_REAL {name} land observed_max_cut={maxCutObserved:F0}m observed_max_fill={maxFillObserved:F0}m; island max_raise={maxIslandRaise:F0}m (4 lines/site)");
            Console.WriteLine($"EMITTER_SITE_OPERATOR_REAL {name} enclosure_failures={enclosureFail} (wet={wetFail}, roof_cover={roofFail}) of {registry.Count}");
            foreach (string f in failingSites.Take(8)) Console.WriteLine("EMITTER_SITE_OPERATOR_REAL_FAIL " + name + " " + f);
            if (worstPad != 0d) failures.Add(name + ": real pad not exactly flat");
            if (enclosureFail != 0) failures.Add($"{name}: {enclosureFail} sites fail the enclosure guarantee");
        }
    }

    // ---- performance -----------------------------------------------------------------------------------

    static void Benchmark()
    {
        const int Count = 4_000_000;
        Scenario land = Scenarios().First(s => s.Name == "ridge");
        Scenario island = Scenarios().First(s => s.Name == "ocean-4500");
        var rng = new Random(5);
        double[] du = new double[4096], dw = new double[4096], h0 = new double[4096];
        foreach (Scenario sc in new[] { land, island })
        {
            for (int i = 0; i < du.Length; i++)
            {
                double r = 360d + rng.NextDouble() * (sc.Site.InfluenceRadius - 360d), a = rng.NextDouble() * Math.Tau;
                du[i] = r * Math.Cos(a); dw[i] = r * Math.Sin(a); h0[i] = sc.H0(du[i], dw[i]);
            }
            double sink = 0d;
            for (int i = 0; i < 200_000; i++) sink += Op.Shape(sc.Site, du[i & 4095], dw[i & 4095], h0[i & 4095]);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < Count; i++) sink += Op.Shape(sc.Site, du[i & 4095], dw[i & 4095], h0[i & 4095]);
            watch.Stop();
            double plain = watch.Elapsed.TotalMilliseconds * 1e6 / Count;
            watch.Restart();
            for (int i = 0; i < Count; i++) sink += Op.Shape(sc.Site, du[i & 4095], dw[i & 4095], h0[i & 4095], out SiteSurfaceMask m) + (int)m.Class;
            watch.Stop();
            double masked = watch.Elapsed.TotalMilliseconds * 1e6 / Count;
            Console.WriteLine($"EMITTER_SITE_OPERATOR_PERF {sc.Name} shape_ns={plain:F1} shape_with_mask_ns={masked:F1} (assembly configuration: {(IsOptimized() ? "optimized" : "debug")}) sink={sink:F0}");
        }
    }

    static bool IsOptimized()
    {
        foreach (object attr in typeof(EmitterSiteSurfaceOperatorTest).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false))
            if (attr is DebuggableAttribute d) return !d.IsJITOptimizerDisabled;
        return true;
    }
}
