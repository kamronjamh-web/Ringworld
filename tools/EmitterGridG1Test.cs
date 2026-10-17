using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Emitter relay grid phase G1 (docs/emitter-grid-climate-machines-spec.md G1/G2/G3): topology of the six closed chains
/// through the main spire, rebase-safe world endpoints, the deterministic one-way pulse clock and the CPU reference intensity.
/// Expectations come from the live registry; no registry identities or golden values are used.
/// </summary>
public partial class EmitterGridG1Test : RefCounted
{
    const double Circumference = EmitterSiteTestWorlds.Circumference;
    const double Width = EmitterSiteTestWorlds.Width;
    static readonly double Radius = Circumference / Math.Tau;
    const double AcrossBase = 321d;
    static readonly Transform3D Grid = new(Basis.FromEuler(new Vector3(0.3f, 0.7f, -0.2f)), new Vector3(1000f, -50f, 20f));

    static RingLodGeometrySnapshot Snapshot(double a, double v, double x, double verticalBase = 50d)
        => new(Grid, new Vector3(3f, -2f, 1f), new Vector3(123.5f, -45.25f, 67f), Radius, AcrossBase, verticalBase, Circumference, -8000d, 8000d,
            new RingCanonicalPosition(a, v, x));

    static void Expect(List<string> failures, bool ok, string message) { if (!ok) failures.Add(message); }

    public int Run(bool unused)
    {
        var failures = new List<string>();
        try
        {
            foreach (int seed in new[] { 1337, 4242 })
            {
                using RingTerrainSampler sampler = EmitterSiteTestWorlds.CreateGen17(seed);
                using INaturalHeightSource source = EmitterSiteHeightSources.Create(sampler);
                EmitterSiteRegistry registry = EmitterSiteRegistry.GetOrBuild(EmitterSiteRecipe.Default(seed), source);
                EmitterGridTopology topology = EmitterGridTopology.Build(registry);
                string name = $"seed{seed}";
                CheckTopology(name, registry, topology, failures);
                CheckRebase(name, registry, topology, failures);
                CheckPulse(name, seed, topology, failures);
            }
            CheckProfileAndIntensity(failures);
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("EMITTER_GRID_G1=FAIL " + string.Join(" | ", failures.Take(30))); return 1; }
        Console.WriteLine("EMITTER_GRID_G1=PASS");
        return 0;
    }

    static int RowOf(EmitterSiteRegistry r, int site)
    {
        if (site == r.MainIndex) return r.MainRow;
        int compact = site / 6;
        return compact < r.MainRow ? compact : compact + 1;
    }

    static void CheckTopology(string name, EmitterSiteRegistry registry, EmitterGridTopology t, List<string> failures)
    {
        double c = Circumference, pitch = c / 100d;
        Expect(failures, registry.Count == 595, $"{name}: site count {registry.Count}");
        Expect(failures, t.LinkCount == 600 && t.Columns == 6 && t.LinksPerChain == 100, $"{name}: link count {t.LinkCount}");
        int chain = 0, knotOut = 0, knotIn = 0;
        var asTarget = new int[registry.Count];
        double minChord = double.MaxValue, maxChord = 0, minKnot = double.MaxValue, maxKnot = 0, minSag = double.MaxValue, maxSag = 0, minMid = double.MaxValue, maxMid = 0;
        double minChainLen = double.MaxValue, maxChainLen = 0, sumChord = 0;
        int seamLinks = 0;
        for (int col = 0; col < 6; col++)
        {
            EmitterLink first = t[t.ChainFirstLink(col)], last = t[t.ChainLastLink(col)];
            Expect(failures, first.Kind == EmitterLinkKind.KnotOut && first.FromSite == registry.MainIndex && RowOf(registry, first.ToSite) == (registry.MainRow + 1) % 100, $"{name}: chain {col} knot-out wrong");
            Expect(failures, last.Kind == EmitterLinkKind.KnotIn && last.ToSite == registry.MainIndex && RowOf(registry, last.FromSite) == (registry.MainRow + 99) % 100, $"{name}: chain {col} knot-in wrong");
            Expect(failures, first.P0 == 0d && last.P1 == c, $"{name}: chain {col} P range {first.P0}..{last.P1}");
            Expect(failures, first.FromRelayHeightM == EmitterSiteFrameMath.DefaultMainCrownHeightMeters && last.ToRelayHeightM == EmitterSiteFrameMath.DefaultMainCrownHeightMeters, $"{name}: main relay height");
            Expect(failures, t.LinkOut(registry.MainIndex, col) == first.Index && t.LinkIn(registry.MainIndex, col) == last.Index, $"{name}: main LinkOut/LinkIn column {col}");
            for (int k = 0; k < 100; k++)
            {
                EmitterLink l = t[col * 100 + k];
                Expect(failures, l.Index == col * 100 + k && l.Column == col, $"{name}: link index/column {l.Index}");
                if (l.Kind == EmitterLinkKind.Chain) chain++; else if (l.Kind == EmitterLinkKind.KnotOut) knotOut++; else knotIn++;
                if (k > 0)
                {
                    EmitterLink prev = t[l.Index - 1];
                    Expect(failures, prev.ToSite == l.FromSite && prev.P1 == l.P0, $"{name}: chain {col} not contiguous at link {k}");
                    Expect(failures, RowOf(registry, l.FromSite) == (registry.MainRow + k) % 100, $"{name}: chain {col} link {k} row order");
                }
                if (l.FromSite != registry.MainIndex) { Expect(failures, l.FromSite % 6 == col, $"{name}: link {l.Index} from-site column"); Expect(failures, t.LinkOut(l.FromSite, col) == l.Index, $"{name}: LinkOut {l.Index}"); }
                if (l.ToSite != registry.MainIndex) { Expect(failures, l.ToSite % 6 == col, $"{name}: link {l.Index} to-site column"); Expect(failures, t.LinkIn(l.ToSite, col) == l.Index, $"{name}: LinkIn {l.Index}"); asTarget[l.ToSite]++; }
                double dAround = EmitterGridTopology.SignedWrap(registry[l.ToSite].Around - registry[l.FromSite].Around, c);
                double len = l.P1 - l.P0;
                Expect(failures, Math.Abs(len - dAround) < 1e-6 && len > 0d, $"{name}: link {l.Index} P span {len} vs signed {dAround}");
                if (registry[l.ToSite].Around < registry[l.FromSite].Around) // crosses the ring seam
                {
                    seamLinks++;
                    Expect(failures, len > 280_000d && len < 345_000d, $"{name}: seam link {l.Index} span {len}");
                }
                EmitterLinkGeometry g = t.GetGeometry(l.Index);
                sumChord += g.ChordLengthM;
                if (l.Kind == EmitterLinkKind.Chain) { minChord = Math.Min(minChord, g.ChordLengthM); maxChord = Math.Max(maxChord, g.ChordLengthM); }
                else { minKnot = Math.Min(minKnot, g.ChordLengthM); maxKnot = Math.Max(maxKnot, g.ChordLengthM); }
                minChainLen = Math.Min(minChainLen, len); maxChainLen = Math.Max(maxChainLen, len);
                minSag = Math.Min(minSag, g.SagittaM); maxSag = Math.Max(maxSag, g.SagittaM);
                minMid = Math.Min(minMid, g.MidSpanAltitudeM); maxMid = Math.Max(maxMid, g.MidSpanAltitudeM);
            }
        }
        Expect(failures, chain == 588 && knotOut == 6 && knotIn == 6, $"{name}: kinds {chain}/{knotOut}/{knotIn}");
        for (int i = 0; i < registry.RegularCount; i++) Expect(failures, asTarget[i] == 1, $"{name}: regular site {i} is the target of {asTarget[i]} links");
        Expect(failures, seamLinks == 6, $"{name}: {seamLinks} seam-crossing links (expected one per chain)");
        Expect(failures, minChord > 0.97 * pitch && maxChord < 1.03 * pitch, $"{name}: chain chord range {minChord}..{maxChord}");
        Expect(failures, minKnot > 0.97 * pitch && maxKnot < 1.10 * pitch, $"{name}: knot chord range {minKnot}..{maxKnot}");
        // Spec F3: sagitta ~ d^2/8R = 2,466 m; mid-span altitude = mean end altitude + sagitta (4.2 km for sea-level pads, 6.2 km for a 2 km pad).
        Expect(failures, minSag > 2_300d && maxSag < 2_700d, $"{name}: sagitta range {minSag}..{maxSag}");
        Expect(failures, minMid > 4_000d && maxMid < 7_000d, $"{name}: mid-span altitude range {minMid}..{maxMid}");
        Expect(failures, EmitterGridTopology.Build(registry).Identity == t.Identity && EmitterGridTopology.Build(registry, 1743d, 1743d).Identity != t.Identity, $"{name}: identity");
        Console.WriteLine($"EMITTER_GRID_G1 {name} topology main_row={registry.MainRow} links={t.LinkCount} chain/knot_out/knot_in={chain}/{knotOut}/{knotIn} seam_links={seamLinks} " +
            $"chain_chord_km={minChord / 1000:F2}..{maxChord / 1000:F2} knot_chord_km={minKnot / 1000:F2}..{maxKnot / 1000:F2} mean_chord_km={sumChord / t.LinkCount / 1000:F2} " +
            $"sagitta_m={minSag:F0}..{maxSag:F0} mid_span_alt_m={minMid:F0}..{maxMid:F0} loop_p_km={t[0].P0 / 1000:F1}..{t[99].P1 / 1000:F1} identity={t.Identity:X16}");
    }

    static void CheckRebase(string name, EmitterSiteRegistry registry, EmitterGridTopology t, List<string> failures)
    {
        int n = t.LinkCount;
        var a = new Vector3D[n]; var b = new Vector3D[n];
        var a0 = new Vector3D[n]; var b0 = new Vector3D[n];
        var anchors = new (double, double, double)[]
        {
            (1000d, 20d, 159_000d), (registry.MainSite.Around + 1500d, 30d, 159_000d), (Circumference - 2d, 5d, 20_000d), (-1d, 0d, 300_000d),
            (0d + 955d * Circumference, 40d, 100_000d), (30_000_000d, 10d, 60_000d), (-3d * Circumference + 11d, 1d, 8d), (15_700_000d, 500d, 159_000d),
        };
        // Reference point: a fixed canonical point; endpoint minus reference must be anchor independent (both are absolute evaluations).
        double worstChord = 0, worstRef = 0, worstLen = 0;
        Vector3D reference0 = default;
        for (int i = 0; i < anchors.Length; i++)
        {
            var (aa, av, ax) = anchors[i];
            RingLodGeometrySnapshot snap = Snapshot(aa, av, ax);
            t.FillWorldEndpoints(snap, a, b);
            var ctx = new EmitterSiteFrameContext(snap);
            Vector3D reference = ctx.WorldPosition(12_345_678d, 17d, 90_000d);
            if (i == 0) { Array.Copy(a, a0, n); Array.Copy(b, b0, n); reference0 = reference; }
            for (int k = 0; k < n; k++)
            {
                Vector3D chord = b[k] - a[k], chord0 = b0[k] - a0[k];
                worstChord = Math.Max(worstChord, (chord - chord0).Length);
                if (i > 0) worstRef = Math.Max(worstRef, ((a[k] - reference) - (a0[k] - reference0)).Length);
            }
        }
        // World chord length equals the canonical chord (vertical base 0 so the model matches).
        RingLodGeometrySnapshot flat = Snapshot(500d, 10d, 150_000d, 0d);
        t.FillWorldEndpoints(flat, a, b);
        for (int k = 0; k < n; k++) worstLen = Math.Max(worstLen, Math.Abs((b[k] - a[k]).Length - t.GetGeometry(k).ChordLengthM));
        Expect(failures, worstChord < 1e-3, $"{name}: rebase chord vector drift {worstChord} m");
        Expect(failures, worstRef < 1e-3, $"{name}: rebase endpoint-vs-reference drift {worstRef} m");
        Expect(failures, worstLen < 0.05, $"{name}: world chord vs canonical chord {worstLen} m");

        // Cross-check against the placement adapter: site origin + relay height along the site up equals the link endpoint (near anchors, float tolerance).
        var placements = new EmitterSitePlacementSource(registry);
        double worstSite = 0; int checkedSites = 0;
        for (int site = 0; site < registry.Count; site += 29)
        {
            EmitterSitePlacement p = placements.GetPlacement(site);
            RingLodGeometrySnapshot snap = Snapshot(p.AroundMeters + 800d, p.PadHeightMeters + 10d, p.AcrossMeters - 500d);
            t.FillWorldEndpoints(snap, a, b);
            Transform3D tr = placements.GetWorldTransform(snap, site);
            double relay = site == registry.MainIndex ? t.MainRelayHeightM : t.RegularRelayHeightM;
            Vector3 crown = tr * new Vector3(0f, (float)relay, 0f);
            Vector3D end = default; bool found = false;
            for (int col = 0; col < 6 && !found; col++)
            {
                int outLink = t.LinkOut(site, col);
                if (outLink >= 0) { end = a[outLink]; found = true; }
                else { int inLink = t.LinkIn(site, col); if (inLink >= 0) { end = b[inLink]; found = true; } }
            }
            Expect(failures, found, $"{name}: site {site} has no link");
            if (found) { worstSite = Math.Max(worstSite, new Vector3D(crown.X - end.X, crown.Y - end.Y, crown.Z - end.Z).Length); checkedSites++; }
        }
        Expect(failures, worstSite < 0.02, $"{name}: link endpoint vs site-transform crown {worstSite} m");

        // Cost and allocation.
        RingLodGeometrySnapshot timing = Snapshot(1000d, 20d, 159_000d);
        for (int i = 0; i < 20; i++) t.FillWorldEndpoints(timing, a, b);
        const int Iterations = 200;
        var times = new double[Iterations];
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            t.FillWorldEndpoints(timing, a, b);
            times[i] = (Stopwatch.GetTimestamp() - t0) * 1e3 / Stopwatch.Frequency;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        Array.Sort(times);
        Expect(failures, allocated == 0, $"{name}: FillWorldEndpoints allocated {allocated} bytes");
        Console.WriteLine($"EMITTER_GRID_G1 {name} rebase anchors={anchors.Length} chord_drift_m={worstChord:E2} ref_drift_m={worstRef:E2} world_vs_canonical_chord_m={worstLen:E2} site_crown_sites={checkedSites} site_crown_worst_m={worstSite:E2} fill_median_ms={times[Iterations / 2]:F3} allocated_bytes={allocated}");
    }

    static void CheckPulse(string name, int seed, EmitterGridTopology t, List<string> failures)
    {
        double c = Circumference;
        var clock = new EmitterGridPulseClock(seed, c);
        double v = clock.SpeedMetersPerSecond;
        Expect(failures, Math.Abs(v - c / 300d) < 1e-6 && Math.Abs(v - 104_719.79) < 0.01, $"{name}: auto speed {v}");
        Expect(failures, Math.Abs(clock.SecondsPerLink(c / 100d) - 3d) < 1e-9, $"{name}: seconds per link {clock.SecondsPerLink(c / 100d)}");
        Expect(failures, clock.PhaseSeconds >= 0d && clock.PhaseSeconds < 300d, $"{name}: phase {clock.PhaseSeconds}");
        Expect(failures, new EmitterGridPulseClock(seed, c).PhaseSeconds == clock.PhaseSeconds && new EmitterGridPulseClock(seed + 1, c).PhaseSeconds != clock.PhaseSeconds, $"{name}: phase seed dependence");

        // Separate period and speed settings.
        var p120 = new EmitterGridPulseClock(seed, c, new EmitterGridPulseSettings { PeriodSeconds = 120d });
        Expect(failures, Math.Abs(p120.SpeedMetersPerSecond - c / 120d) < 1e-6, $"{name}: auto speed follows period");
        var manual = new EmitterGridPulseClock(seed, c, new EmitterGridPulseSettings { AutoSpeed = false, ManualSpeedMetersPerSecond = 25_000d });
        Expect(failures, manual.SpeedMetersPerSecond == 25_000d && manual.PeriodSeconds == 300d, $"{name}: manual speed keeps the period");
        Expect(failures, new EmitterGridPulseClock(seed, c, new EmitterGridPulseSettings { AutoSpeed = false, ManualSpeedMetersPerSecond = 1d }).SpeedMetersPerSecond == 5_000d
            && new EmitterGridPulseClock(seed, c, new EmitterGridPulseSettings { AutoSpeed = false, ManualSpeedMetersPerSecond = 1e9 }).SpeedMetersPerSecond == 500_000d, $"{name}: manual speed clamp");

        // One front in flight per chain, all six in sync, at auto speed.
        var rng = new Random(seed);
        Span<EmitterPulseFront> fronts = stackalloc EmitterPulseFront[16];
        int maxFronts = 0, badInFlight = 0, badSync = 0;
        for (int i = 0; i < 4000; i++)
        {
            double tt = (rng.NextDouble() - 0.1) * 2_000_000d;
            int inFlight = -1;
            for (int col = 0; col < 6; col++)
            {
                int n = clock.GetFronts(tt, col, fronts);
                maxFronts = Math.Max(maxFronts, n);
                int live = 0;
                for (int j = 0; j < n; j++) if (fronts[j].HeadQ >= 0d && fronts[j].HeadQ <= c) live++;
                if (live != 1) badInFlight++;
                if (inFlight >= 0 && live != inFlight) badSync++;
                inFlight = live;
            }
            double q = rng.NextDouble() * c;
            double p0 = clock.PulseAtQ(tt, 0, q);
            for (int col = 1; col < 6; col++) if (clock.PulseAtQ(tt, col, q) != p0) badSync++;
        }
        Expect(failures, badInFlight == 0, $"{name}: {badInFlight} chain samples without exactly one front in flight");
        Expect(failures, badSync == 0, $"{name}: {badSync} chain sync mismatches");
        // Slow manual speed puts several fronts in flight.
        int slowLive = 0;
        { int n = manual.GetFronts(777_777d, 2, fronts); for (int j = 0; j < n; j++) if (fronts[j].HeadQ >= 0d && fronts[j].HeadQ <= c) slowLive++; }
        Expect(failures, slowLive >= 4 && slowLive <= 5, $"{name}: slow speed fronts in flight {slowLive}");

        // Stagger: V-shaped delay, column 0 and 5 equal and largest.
        var stag = new EmitterGridPulseClock(seed, c, new EmitterGridPulseSettings { ColumnStaggerSeconds = 4d });
        Expect(failures, stag.StaggerSeconds(0) == 4d && stag.StaggerSeconds(5) == 4d && Math.Abs(stag.StaggerSeconds(2) - 0.8d) < 1e-12 && stag.StaggerSeconds(2) == stag.StaggerSeconds(3), $"{name}: stagger shape");
        Expect(failures, clock.StaggerSeconds(0) == 0d, $"{name}: default stagger");

        // Exit / return link and direction.
        var retro = new EmitterGridPulseClock(seed, c, new EmitterGridPulseSettings { Direction = EmitterPulseDirection.Retrograde });
        for (int col = 0; col < 6; col++)
        {
            Expect(failures, clock.ExitLink(t, col) == col * 100 && clock.ReturnLink(t, col) == col * 100 + 99, $"{name}: prograde exit/return {col}");
            Expect(failures, retro.ExitLink(t, col) == col * 100 + 99 && retro.ReturnLink(t, col) == col * 100, $"{name}: retrograde exit/return {col}");
        }
        Expect(failures, clock.ToQ(1000d) == 1000d && retro.ToQ(1000d) == c - 1000d, $"{name}: ToQ");

        // Link timing: the head crosses consecutive link starts 3.0 s apart (+/-0.1 s from nudged lengths) and the loop in one period.
        double launch = clock.LaunchTime(10);
        for (int dirIndex = 0; dirIndex < 2; dirIndex++)
        {
            EmitterGridPulseClock ck = dirIndex == 0 ? clock : retro;
            long n10 = 10;
            double lastArrival = double.NaN; double minGap = 1e9, maxGap = 0, sumGap = 0; int gaps = 0;
            for (int k = 0; k < 100; k++)
            {
                int linkIndex = dirIndex == 0 ? 2 * 100 + k : 2 * 100 + 99 - k; // pulse order along column 2
                EmitterLink l = t[linkIndex];
                double qStart = ck.ToQ(dirIndex == 0 ? l.P0 : l.P1);
                double arrival = ck.ArrivalTime(qStart, 2, n10);
                if (!double.IsNaN(lastArrival)) { double gap = arrival - lastArrival; minGap = Math.Min(minGap, gap); maxGap = Math.Max(maxGap, gap); sumGap += gap; gaps++; }
                lastArrival = arrival;
                Expect(failures, Math.Abs(ck.SecondsPerLink(Math.Abs(l.P1 - l.P0)) - (ck.ArrivalTime(ck.ToQ(dirIndex == 0 ? l.P1 : l.P0), 2, n10) - arrival)) < 1e-9, $"{name}: link {linkIndex} crossing time");
            }
            Expect(failures, minGap > 2.85 && maxGap < 3.15 && Math.Abs(sumGap / gaps - 3d) < 0.02, $"{name}: link gap {minGap:F3}..{maxGap:F3} mean {sumGap / gaps:F4}");
            Expect(failures, Math.Abs(ck.ArrivalTime(c, 2, n10) - (launch + 300d)) < 1e-6, $"{name}: loop arrival");
            // Intensity peaks where and when the head is: at the arrival time the profile at that q is the full head.
            double qMid = c * 0.37;
            double tHead = ck.ArrivalTime(qMid, 2, n10);
            Expect(failures, ck.PulseAtQ(tHead, 2, qMid) > 0.99, $"{name}: dir {dirIndex} head intensity {ck.PulseAtQ(tHead, 2, qMid)}");
            Expect(failures, ck.PulseAtQ(tHead - 1d, 2, qMid) < 0.01 && ck.PulseAtQ(tHead + 1d, 2, qMid) > 0.5, $"{name}: dir {dirIndex} ahead/behind");
            // Overhead pass: time above half brightness at a fixed point is the streak plus ln2 * trail seconds (~1.75 s; spec "about 2 s").
            double above = 0d;
            for (double dt = -6d; dt <= 6d; dt += 0.001) if (ck.PulseAtQ(tHead + dt, 2, qMid) >= 0.5) above += 0.001;
            Expect(failures, above > 1.5 && above < 2.1, $"{name}: overhead half-brightness duration {above:F3} s");
        }

        // Relaunch flare at the main spire: 1.5 s window, 3x crown, knot links of all six chains light, head hand-over is seamless.
        double tl = clock.LaunchTime(25);
        Expect(failures, clock.FlareEnvelope(tl) == 1d && clock.CrownFlareGain(tl) == 3d, $"{name}: flare at launch");
        Expect(failures, clock.FlareEnvelope(tl + 1.5d) == 0d && clock.CrownFlareGain(tl + 1.6d) == 1d && clock.FlareEnvelope(tl - 0.01) == 0d, $"{name}: flare window");
        Expect(failures, clock.FlareEnvelope(tl + 0.75d) > 0.4 && clock.FlareEnvelope(tl + 0.75d) < 0.6, $"{name}: flare midpoint");
        for (int col = 0; col < 6; col++)
        {
            Expect(failures, clock.PulseAtLink(tl + 0.5d, t[col * 100 + 50], 0.5) < 0.01, $"{name}: mid-chain link must not flare");
            Expect(failures, clock.PulseAtLink(tl + 0.5d, t[t.ChainFirstLink(col)], 0.9) > 0.5 && clock.PulseAtLink(tl + 0.5d, t[t.ChainLastLink(col)], 0.1) > 0.5, $"{name}: knot links lit at flare {col}");
            Expect(failures, clock.PulseAtQ(tl, col, 1d) > 0.99 && clock.PulseAtQ(tl, col, c - 1d) > 0.99, $"{name}: hand-over {col}");
        }
        Expect(failures, clock.PulseAtLink(tl + 2d, t[0], 0.001) > 0.3, $"{name}: new head leaves the main spire");

        // Determinism, periodicity (auto speed), huge and negative times, ring-seam continuity.
        var twin = new EmitterGridPulseClock(seed, c);
        double worstPeriodic = 0, worstDeterminism = 0;
        foreach (double tt in new[] { 0d, 123.456, -98_765.4, 1.0e6, 3.1e8, 1.0e9, -1.0e9 })
            for (int s = 0; s < 40; s++)
            {
                double q = (s * 0.0257 + 0.003) * c, ts = tt + s * 1.7;
                double a1 = clock.PulseAtQ(ts, s % 6, q), a2 = twin.PulseAtQ(ts, s % 6, q);
                worstDeterminism = Math.Max(worstDeterminism, Math.Abs(a1 - a2));
                worstPeriodic = Math.Max(worstPeriodic, Math.Abs(a1 - clock.PulseAtQ(ts + 3000d * 300d, s % 6, q)));
            }
        Expect(failures, worstDeterminism == 0d, $"{name}: determinism {worstDeterminism}");
        Expect(failures, worstPeriodic < 1e-3, $"{name}: periodicity at large t {worstPeriodic}");
        double worstJump = 0; double step = 10d;
        foreach (double tt in new[] { 1.0e9 + 0.37, 5.0e8 + 150.1, tl + 7d })
            for (int col = 0; col < 6; col++)
            {
                double prev = double.NaN;
                for (double p = 0d; p <= c; p += step)
                {
                    double cur = clock.PulseAtQ(tt, col, p);
                    if (!double.IsNaN(prev)) worstJump = Math.Max(worstJump, Math.Abs(cur - prev));
                    prev = cur;
                }
            }
        // The link containing the ring seam and the link boundaries evaluate continuously (same p from either side).
        double worstBoundary = 0;
        for (int k = 0; k < 6 * 99; k++)
        {
            int idx = k / 99 * 100 + k % 99;
            EmitterLink l0 = t[idx], l1 = t[idx + 1];
            foreach (double tt in new[] { tl + 20d, tl + 150d, 777_777.7d })
                worstBoundary = Math.Max(worstBoundary, Math.Abs(clock.PulseAtLink(tt, l0, 1d) - clock.PulseAtLink(tt, l1, 0d)));
        }
        Expect(failures, worstJump < 0.02, $"{name}: intensity discontinuity {worstJump} per {step} m");
        Expect(failures, worstBoundary < 1e-9, $"{name}: link boundary mismatch {worstBoundary}");

        // Lit links (CPU twin of the vertex collapse rule): at most ~90 at auto speed, both directions.
        int maxLit = 0, minLit = int.MaxValue; double sumLit = 0; int samples = 0;
        foreach (EmitterGridPulseClock ck in new[] { clock, retro })
            for (double tt = 0; tt < 900d; tt += 0.37)
            {
                int lit = ck.CountLitLinks(t, tt + 4_000d);
                maxLit = Math.Max(maxLit, lit); minLit = Math.Min(minLit, lit); sumLit += lit; samples++;
            }
        Expect(failures, maxLit <= 90, $"{name}: max lit links {maxLit}");
        Console.WriteLine($"EMITTER_GRID_G1 {name} pulse speed_kms={v / 1000:F3} link_s={clock.SecondsPerLink(c / 100d):F3} phase_s={clock.PhaseSeconds:F3} max_fronts_per_chain={maxFronts} lit_links_min/mean/max={minLit}/{sumLit / samples:F1}/{maxLit} " +
            $"flare_s={clock.Settings.RelaunchFlareSeconds} flare_gain={clock.Settings.RelaunchFlareGain} slow_fronts={slowLive} seam_step_jump={worstJump:E2} periodic_err={worstPeriodic:E2}");
    }

    static void CheckProfileAndIntensity(List<string> failures)
    {
        var clock = new EmitterGridPulseClock(1337, Circumference);
        double v = clock.SpeedMetersPerSecond;
        Expect(failures, clock.Profile(0d) == 1d && clock.Profile(-3_000d) == 0d && clock.Profile(-10_000d) == 0d && Math.Abs(clock.Profile(-1_500d) - 0.5) < 1e-9, "profile leading edge");
        double streak = v / 60d;
        Expect(failures, clock.Profile(streak) > 0.999, "profile head streak");
        double trail = clock.Profile(streak + v * 2.5);
        Expect(failures, Math.Abs(trail - (Math.Exp(-1) + 0.05 * Math.Exp(-(streak + v * 2.5) / (v * 10d)))) < 1e-12, $"profile trail {trail}");
        double wake = clock.Profile(v * 10d);
        Expect(failures, wake > 0.03 && wake < 0.045, $"profile wake {wake}");
        Expect(failures, clock.Profile(clock.CutoffMeters + 1d) == 0d && Math.Abs(clock.CutoffMeters - 4d * v * 10d) < 1e-6, "profile cutoff");
        double prev = 1d; bool monotone = true;
        for (double b = 0; b < clock.CutoffMeters; b += 500d) { double p = clock.Profile(b); if (p > prev + 1e-12) monotone = false; prev = p; }
        Expect(failures, monotone, "profile monotone behind the head");

        // Day/night/eclipse (spec G2).
        var s = new EmitterGridVisibilitySettings();
        double dayFull = EmitterGridIntensity.DayAt(1d, 1d), night = EmitterGridIntensity.DayAt(-1d, 1d), eclipse = EmitterGridIntensity.DayAt(1d, 0d);
        Expect(failures, dayFull == 1d && night == 0d && eclipse == 0d, $"day factors {dayFull}/{night}/{eclipse}");
        double bDay = EmitterGridIntensity.Background(dayFull, dayFull, 1000d), bNight = EmitterGridIntensity.Background(night, night, 1000d), bEclipse = EmitterGridIntensity.Background(eclipse, eclipse, 1e6);
        Expect(failures, bDay == 1d && bNight == 0d && bEclipse == 0d, "background brightness");
        Expect(failures, EmitterGridIntensity.Background(0d, 1d, 20_000d) == 0d && EmitterGridIntensity.Background(0d, 1d, 600_000d) == 1d, "far sunlit backdrop brightens distant beams only");
        double headNight = EmitterGridIntensity.Compute(1d, bNight), headDay = EmitterGridIntensity.Compute(1d, bDay), headEclipse = EmitterGridIntensity.Compute(1d, bEclipse);
        Expect(failures, Math.Abs(headNight - 1.03) < 1e-12 && Math.Abs(headDay - 0.2) < 1e-12 && headEclipse == headNight, $"head intensity {headNight}/{headDay}/{headEclipse}");
        Expect(failures, headNight > headDay * 4d, "night brighter than day");
        double residNight = EmitterGridIntensity.Compute(0d, 0d), residDay = EmitterGridIntensity.Compute(0d, 1d);
        Expect(failures, residNight == 0.03 && residDay == 0d, $"residual {residNight}/{residDay}");
        double last = double.MaxValue; bool mono = true;
        for (double b = 0; b <= 1d; b += 0.1) { double i = EmitterGridIntensity.Compute(1d, b); if (i > last) mono = false; last = i; }
        Expect(failures, mono, "intensity decreases with background brightness");
        Expect(failures, Math.Abs(EmitterGridIntensity.Compute(1d, 0d, 0.2d, 0.75d) - (1d + 0.03 + 0.2) * 0.25) < 1e-12, "atmosphere and crawl");

        // Reference at a link: unlit link at night shows only the residual; the head link shows the head.
        var seedClock = new EmitterGridPulseClock(1337, Circumference);
        var link = new EmitterLink(150, 0, 1, 1, EmitterLinkKind.Chain, 5_000_000d, 5_314_159d, 1743d, 1743d);
        double tHead = seedClock.ArrivalTime(5_157_000d, 1, 7);
        Expect(failures, EmitterGridIntensity.AtLink(seedClock, link, 0.5, tHead - 100d, 0d) == 0.03, "unlit link at night = residual");
        Expect(failures, EmitterGridIntensity.AtLink(seedClock, link, 0.5, tHead, 0d) > 1.02 && EmitterGridIntensity.AtLink(seedClock, link, 0.5, tHead, 1d) < 0.21, "head link night vs day");
        Console.WriteLine($"EMITTER_GRID_G1 intensity night_head={headNight:F3} day_head={headDay:F3} eclipse_head={headEclipse:F3} night_residual={residNight:F3} day_residual={residDay:F3} trail_2.5s={trail:F4} wake_10s={wake:F4} cutoff_km={clock.CutoffMeters / 1000:F0}");
    }
}
