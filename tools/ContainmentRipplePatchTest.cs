using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Godot;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Containment field ripple patches (docs/containment-field-design.md 3.5, 3.10.2): generalised patch grid placement, render-state fade to zero, reduced flashes, an empty pool,
/// near selection, the far quad rule and build timing. Pure C#; no scene. Run: Godot --headless --path . --script res://tools/containment_ripple_patch_test.gd.
/// Prints CONTAINMENT_RIPPLE_PATCH=PASS.
/// </summary>
public partial class ContainmentRipplePatchTest : RefCounted
{
    private const double W = 318_000.0;
    private const int V = ContainmentRipplePatches.Quads + 1;

    private readonly List<string> _failures = new();
    private double _worstPlacement;
    private void Check(bool ok, string message) { if (!ok) _failures.Add(message); }
    private void Near(double a, double b, double tol, string message) { if (!(Math.Abs(a - b) <= tol)) _failures.Add($"{message}: {a.ToString("R", CultureInfo.InvariantCulture)} vs {b.ToString("R", CultureInfo.InvariantCulture)} tol {tol}"); }

    private static RingLodGeometrySnapshot Snap(double wallTop, RingCanonicalPosition? anchor = null, Transform3D? grid = null)
        => new(grid ?? Transform3D.Identity, Vector3.Zero, Vector3.Zero, CertifiedRingScale.EffectiveRadiusMeters, 0.0, 0.0,
            CertifiedRingScale.EffectiveCircumferenceMeters, -8192.0, wallTop, anchor);

    private static ContainmentFieldProfile Prof(double wallTop)
        => ContainmentFieldProfile.FromSnapshot(Snap(wallTop), W, RimWallDescription.Current);

    private static ContainmentRipple Rip(ContainmentDisturbanceKind kind, in ContainmentFieldPoint surface, float energy, float radius, float duration, Func<double, float>? flash = null,
        double start = 100.0, long id = 1, float coupling = 1f, float crawl = 0f, int seed = 0)
        => new() { Kind = kind, Surface = surface, Energy = energy, RadiusMeters = radius, DurationSeconds = duration, FlashLevel = flash, FlashCoupling = coupling,
            StartTimeSeconds = start, SourceId = id, CrawlRadiusMeters = crawl, CrawlSeed = seed };

    public int Run()
    {
        try
        {
            double circ = CertifiedRingScale.EffectiveCircumferenceMeters;
            var grid = new Transform3D(new Basis(Vector3.Up, 0.37f), new Vector3(1234f, -56f, 789f));
            foreach (double wallTop in new[] { 10_000.0, 10_400.0 })
            {
                ContainmentFieldProfile p = Prof(wallTop);
                Check(p.IsValid, "valid " + wallTop);
                Placement("wt" + wallTop, p, circ, grid);
            }
            ContainmentFieldProfile prof = Prof(10_000.0);
            FadeToZero(prof);
            ReducedFlashes(prof);
            PoolEmpty(prof, circ);
            NearSelection(prof, circ, grid);
            FarQuad();
            FarStrikeFade();
            Timing(prof, circ, grid);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.ToString());
            Console.Error.WriteLine("CONTAINMENT_RIPPLE_PATCH=FAIL exception " + e.Message);
            return 1;
        }
        finally { LightningVisualPolicy.Reset(); }
        Console.WriteLine($"CONTAINMENT_RIPPLE_PATCH worst placement error {_worstPlacement.ToString("F5", CultureInfo.InvariantCulture)} m");
        if (_failures.Count != 0) { Console.Error.WriteLine("CONTAINMENT_RIPPLE_PATCH=FAIL " + string.Join(" | ", _failures.Take(25))); return 1; }
        Console.WriteLine("CONTAINMENT_RIPPLE_PATCH=PASS");
        return 0;
    }

    // ---- a ---------------------------------------------------------------------------------------------------------------------------------------

    private void Placement(string tag, in ContainmentFieldProfile p, double circ, Transform3D grid)
    {
        double archLength = ContainmentFieldGeometry.ArchLength(p);
        var cases = new (string Name, double Around, double S)[]
        {
            ("leftSkirt", 0.3 * circ + 321.0, 300.0), ("ellipse", 0.3 * circ + 321.0, 4000.0), ("apex", 0.55 * circ, archLength * 0.5),
            ("rightSkirt", 0.3 * circ + 321.0, archLength - 300.0), ("around100", 100.0, 4000.0), ("aroundWrap", circ - 100.0, archLength * 0.5),
        };
        foreach ((string name, double around, double s) in cases)
        {
            string t = $"{tag} {name}";
            ContainmentFieldPoint surface = ContainmentFieldGeometry.SurfaceAtArchLength(p, around, s);
            RingLodGeometrySnapshot live = Snap(p.WallTopMeters, new RingCanonicalPosition(surface.AroundMeters, surface.VerticalMeters, surface.AcrossMeters), grid);
            ContainmentRipple r = Rip(ContainmentDisturbanceKind.FieldStrike, surface, 0.8f, 3000f, 3f, crawl: 5200f);
            ContainmentNearPatchMesh m = ContainmentRipplePatches.BuildPatch(p, live, r);
            double pr = ContainmentRipplePatches.RenderPatchRadius(r);
            Near(pr, 1.15 * 5200.0, 1e-3, t + " patch radius");
            Check(m.QuadsPerSide == ContainmentRipplePatches.Quads && m.Positions.Length == V * V, t + " grid size");
            Vector3 anchorWorld = live.CanonicalToWorld(surface.AroundMeters, surface.VerticalMeters, surface.AcrossMeters);
            foreach (Vector3 v in m.Positions)
            {
                double sd = Math.Abs(ContainmentFieldGeometry.SignedDistance(p, ContainmentFieldPlacement.WorldToCanonical(live, anchorWorld + v)));
                _worstPlacement = Math.Max(_worstPlacement, sd);
                if (!(sd < 0.05)) { Check(false, $"{t} vertex off the field by {sd:F4} m"); break; }
            }
            bool unclampedLow = m.ArchStart > 0.0, unclampedHigh = m.ArchEnd < archLength;
            if (unclampedLow && unclampedHigh)
            {
                int k = (V / 2) * V + V / 2;
                Check(m.Uv[k].Length() < 1e-3f && m.Positions[k].Length() < 0.01f, $"{t} UV origin at the anchor (uv {m.Uv[k]}, pos {m.Positions[k].Length():F4})");
            }
            // UV coverage in surface metres at the anchor's height.
            double umin = double.MaxValue, umax = double.MinValue, vmin = double.MaxValue, vmax = double.MinValue;
            foreach (Vector2 uv in m.Uv) { umin = Math.Min(umin, uv.X); umax = Math.Max(umax, uv.X); vmin = Math.Min(vmin, uv.Y); vmax = Math.Max(vmax, uv.Y); }
            Near(umin * m.SurfaceMetresPerUvX, -pr, pr * 1e-4, t + " uv.x min");
            Near(umax * m.SurfaceMetresPerUvX, pr, pr * 1e-4, t + " uv.x max");
            if (unclampedLow) Near(vmin, -pr, 1e-2, t + " uv.y min"); else { Near(m.ArchStart, 0.0, 0.0, t + " clamped ArchStart"); Near(vmin, -surface.ArchLengthMeters, 1e-3, t + " uv.y min at root"); }
            if (unclampedHigh) Near(vmax, pr, 1e-2, t + " uv.y max"); else { Near(m.ArchEnd, archLength, 0.0, t + " clamped ArchEnd"); Near(vmax, archLength - surface.ArchLengthMeters, 1e-3, t + " uv.y max at root"); }
            // Around-neighbour world spacing vs UV spacing x metres-per-UV, corrected to each row's own height.
            double r0 = live.RingRadiusMeters, radialA = r0 - (live.RingVerticalBaseOffsetMeters + surface.VerticalMeters);
            for (int j = 0; j < V; j += 4)
            {
                double sj = m.ArchStart + (m.ArchEnd - m.ArchStart) * j / ContainmentRipplePatches.Quads;
                ContainmentFieldPoint row = ContainmentFieldGeometry.SurfaceAtArchLength(p, surface.AroundMeters, sj);
                double radialJ = r0 - (live.RingVerticalBaseOffsetMeters + row.VerticalMeters);
                int a = j * V + V / 2, b = a + 1;
                double world = m.Positions[a].DistanceTo(m.Positions[b]), uv = (m.Uv[b].X - m.Uv[a].X) * m.SurfaceMetresPerUvX * radialJ / radialA;
                Near(world / uv, 1.0, 0.002, t + $" around spacing row {j}");
            }
            if (unclampedLow && unclampedHigh)
            {
                int a = (V / 2) * V + V / 2, b = a + 1;
                Near(m.Positions[a].DistanceTo(m.Positions[b]) / ((m.Uv[b].X - m.Uv[a].X) * m.SurfaceMetresPerUvX), 1.0, 0.002, t + " anchor-row around spacing");
            }
        }

        // 400 km from the floating origin: the float world position of the anchor against the double reference.
        double c0 = 0.4 * circ;
        ContainmentFieldPoint far = ContainmentFieldGeometry.SurfaceAtArchLength(p, c0 + 400_000.0, 4000.0);
        RingLodGeometrySnapshot fs = Snap(p.WallTopMeters, new RingCanonicalPosition(c0, far.VerticalMeters, far.AcrossMeters), grid);
        (double fx, double fy, double fz) = fs.CanonicalToAbsoluteLocal(far.AroundMeters, far.VerticalMeters, far.AcrossMeters);
        (double ox, double oy, double oz) = fs.CanonicalToAbsoluteLocal(c0, far.VerticalMeters, far.AcrossMeters);
        Basis b0 = grid.Basis;
        double dx = fx - ox, dy = fy - oy, dz = fz - oz;
        double rx = b0.Column0.X * dx + b0.Column1.X * dy + b0.Column2.X * dz + grid.Origin.X;
        double ry = b0.Column0.Y * dx + b0.Column1.Y * dy + b0.Column2.Y * dz + grid.Origin.Y;
        double rz = b0.Column0.Z * dx + b0.Column1.Z * dy + b0.Column2.Z * dz + grid.Origin.Z;
        Vector3 aw = fs.CanonicalToWorld(far.AroundMeters, far.VerticalMeters, far.AcrossMeters);
        Near(Math.Sqrt((aw.X - rx) * (aw.X - rx) + (aw.Y - ry) * (aw.Y - ry) + (aw.Z - rz) * (aw.Z - rz)), 0.0, 0.05, tag + " 400 km anchor world position");
    }

    // ---- b ---------------------------------------------------------------------------------------------------------------------------------------

    private void FadeToZero(in ContainmentFieldProfile p)
    {
        ContainmentFieldPoint s = ContainmentFieldGeometry.SurfaceAtArchLength(p, 5e5, 4000.0);
        ContainmentFieldPoint sRight = ContainmentFieldGeometry.SurfaceAtArchLength(p, 5e5, ContainmentFieldGeometry.ArchLength(p) - 4000.0);
        const double Start = 100.0;
        var list = new List<(string Name, ContainmentRipple R)>
        {
            ("lightning", Rip(ContainmentDisturbanceKind.Lightning, s, 0.7f, 3000f, 2.5f, t => t - Start < 0.72 ? 1f : 0f)),
            ("strike", Rip(ContainmentDisturbanceKind.FieldStrike, s, 0.8f, 3000f, 3f, t => t - Start < 1.9 ? 1f : 0f, crawl: 5200f, seed: 12345)),
            ("strikeRight", Rip(ContainmentDisturbanceKind.FieldStrike, sRight, 0.8f, 3000f, 3f, t => t - Start < 1.9 ? 1f : 0f, crawl: 5200f, seed: 54321)),
            ("strikeForeverFlash", Rip(ContainmentDisturbanceKind.FieldStrike, s, 1.0f, 3000f, 3f, t => 1f, crawl: 6000f, seed: 7)),
            ("storm", Rip(ContainmentDisturbanceKind.StormContact, s, 0.6f, 4000f, 3f)),
            ("megastorm", Rip(ContainmentDisturbanceKind.MegastormContact, s, 0.5f, 2000f, 3f, t => 0.6f, id: 99)),
            ("emitter", Rip(ContainmentDisturbanceKind.EmitterShot, s, 0.9f, 1500f, 2f, t => 1f)),
            ("body", Rip(ContainmentDisturbanceKind.BodyContact, s, 0.9f, 1500f, 2f)),
        };
        const float Strength = 0.8f;
        foreach ((string name, ContainmentRipple r) in list)
        {
            double t = r.DurationSeconds;
            bool strikeKind = r.Kind == ContainmentDisturbanceKind.FieldStrike || r.Kind == ContainmentDisturbanceKind.MegastormContact;
            ContainmentRippleRenderState prev = default;
            for (double tau = -0.2; tau <= t + 0.5; tau += 1.0 / 240.0)
            {
                double now = Start + tau;
                ContainmentRippleRenderState st = ContainmentRipplePatches.Evaluate(r, now, r.FlashAt(now), Strength, false);
                double realTau = now - r.StartTimeSeconds;
                string tt = $"{name} tau {realTau:F4}";
                if (!(realTau > 0.0) || !(realTau < t)) { Check(st == default, tt + " default outside life"); prev = st; continue; }
                Check(Finite(st), tt + " finite");
                Check(st.Wave.X >= 0f && st.Wave.X <= Strength + 1e-6f && st.Wave.W >= 0f && st.Wave.W <= 1f && st.StrikeLevel >= 0f && st.StrikeLevel <= Strength + 1e-6f, tt + $" ranges A {st.Wave.X} flash {st.Wave.W} strike {st.StrikeLevel}");
                if (!strikeKind) Check(st.StrikeLevel == 0f && st.Crawl == Vector4.Zero, tt + " no strike data");
                else Check(st.Crawl.X > 0f && st.Crawl.Y >= 0f && st.Crawl.Y <= st.Crawl.X && st.Crawl.Z >= 0f && st.Crawl.Z <= 65535f && Math.Abs(st.Crawl.W) >= 0.4f && Math.Abs(st.Crawl.W) <= 1f, tt + $" crawl {st.Crawl}");
                // A is continuous; the strike level steps only where the source flash itself steps (the 1.9 s cut of the strike pulse).
                if (realTau > 0.02 && prev.Visible)
                {
                    Check(Math.Abs(st.Wave.X - prev.Wave.X) < 0.1f, tt + " A continuity");
                    if (st.Wave.W == prev.Wave.W) Check(Math.Abs(st.StrikeLevel - prev.StrikeLevel) < 0.05f, tt + " strike level continuity");
                }
                prev = st;
            }
            Check(ContainmentRipplePatches.Evaluate(r, Start + t, r.FlashAt(Start + t), Strength, false) == default, name + " default at tau == T");
            Check(ContainmentRipplePatches.Evaluate(r, Start, 1f, Strength, false) == default, name + " default at tau == 0");
            Check(ContainmentRipplePatches.Evaluate(r, Start + 0.5, 1f, 0f, false) == default, name + " default at strength 0");
            ContainmentRippleRenderState end = ContainmentRipplePatches.Evaluate(r, Start + t - 1e-4, 1f, Strength, false);
            Check(end.Wave.X < 1e-6f && end.StrikeLevel < 1e-3f, name + $" tail A {end.Wave.X} strike {end.StrikeLevel}");
        }
        // The strike's rim bias leans to the nearer root: left skirt (Phi < pi/2) negative, right positive.
        ContainmentRippleRenderState l = ContainmentRipplePatches.Evaluate(list[1].R, Start + 0.3, 1f, 1f, false), rt = ContainmentRipplePatches.Evaluate(list[2].R, Start + 0.3, 1f, 1f, false);
        Check(l.Crawl.W < 0f && rt.Crawl.W > 0f && l.Crawl.Z == 12345f, "rim bias sign and seed");
        Near(ContainmentRipplePatches.CrawlRadius(list[5].R), 500.0 + 1000.0 * 0.5, 1e-3, "contact crawl radius");
        Near(ContainmentRipplePatches.RenderPatchRadius(list[1].R), list[1].R.PatchRadiusMeters, 1e-3, "patch radius equals the bus's for a strike");
    }

    private static bool Finite(in ContainmentRippleRenderState s)
        => float.IsFinite(s.Wave.X) && float.IsFinite(s.Wave.Y) && float.IsFinite(s.Wave.Z) && float.IsFinite(s.Wave.W) && float.IsFinite(s.Crawl.X) && float.IsFinite(s.Crawl.Y)
            && float.IsFinite(s.Crawl.Z) && float.IsFinite(s.Crawl.W) && float.IsFinite(s.StrikeLevel);

    // ---- c ---------------------------------------------------------------------------------------------------------------------------------------

    private void ReducedFlashes(in ContainmentFieldProfile p)
    {
        try
        {
            ContainmentFieldPoint s = ContainmentFieldGeometry.SurfaceAtArchLength(p, 5e5, 4000.0);
            ContainmentRipple strike = Rip(ContainmentDisturbanceKind.FieldStrike, s, 0.8f, 3000f, 3f, t => 1f, crawl: 5200f, seed: 3);
            float normal = ContainmentRipplePatches.Evaluate(strike, 100.12, 1f, 1f, false).Crawl.Y, reduced = ContainmentRipplePatches.Evaluate(strike, 100.12, 1f, 1f, true).Crawl.Y;
            Check(reduced < normal && reduced > 0f, $"reduced crawl front smaller ({reduced} vs {normal})");
            Near(normal, 5200.0 * (1.0 - Math.Exp(-1.0)), 1.0, "crawl front at tau = tc");
            LightningVisualPolicy.Apply(1f, 1f, true);
            var flashes = new float[6];
            for (int i = 0; i < flashes.Length; i++)
                flashes[i] = Rip(ContainmentDisturbanceKind.Lightning, s, 0.7f, 3000f, 2.5f, t => 0.4f, id: i).FlashAt(100.5);
            ContainmentRipplePatches.CapFlashes(flashes);
            Check(flashes.Sum() <= LightningVisualPolicy.ReducedSummedCap + 1e-5f && flashes.Sum() > 0.5f, $"capped flash sum {flashes.Sum()}");
        }
        finally { LightningVisualPolicy.Reset(); }
    }

    // ---- d ---------------------------------------------------------------------------------------------------------------------------------------

    private void PoolEmpty(in ContainmentFieldProfile p, double circ)
    {
        var bus = new ContainmentDisturbanceBus(p, circ) { Observer = null };
        bus.Advance(0.0);
        var kinds = new[] { ContainmentDisturbanceKind.Lightning, ContainmentDisturbanceKind.StormContact, ContainmentDisturbanceKind.MegastormContact, ContainmentDisturbanceKind.FieldStrike, ContainmentDisturbanceKind.EmitterShot };
        float tMax = 0f;
        for (int i = 0; i < kinds.Length; i++)
        {
            float dur = 2.5f + 0.4f * i;
            tMax = Math.Max(tMax, dur);
            var pos = new RingCanonicalPosition(1e5 + 3e5 * i, p.ApexMeters - 100.0, W * 0.5);
            ContainmentPublishResult res = bus.TryPublish(new ContainmentDisturbance(1000 + i, kinds[i], pos, 0.0, 0.7f, 3000f, dur, 1f, t => t < 0.7 ? 1f : 0f));
            Check(res == ContainmentPublishResult.Accepted, $"publish {kinds[i]} -> {res}");
        }
        bus.Advance(0.5);
        Check(bus.LiveCount == 5 && ContainmentRipplePatches.AnyVisible(bus, 0.5, 1f), "AnyVisible while live");
        Check(!ContainmentRipplePatches.AnyVisible(bus, 0.5, 0f), "AnyVisible false at strength 0 (no far strikes)");
        bus.Advance(tMax + 0.01);
        Check(bus.Slots.All(r => r == null) && bus.LiveCount == 0, "slots empty after T_max");
        Check(!ContainmentRipplePatches.AnyVisible(bus, tMax + 0.01, 1f), "AnyVisible false once empty");
        Check(!ContainmentRipplePatches.AnyVisible(null, 0.0, 1f), "AnyVisible false without a bus");
    }

    // ---- e ---------------------------------------------------------------------------------------------------------------------------------------

    private void NearSelection(in ContainmentFieldProfile profile, double circ, Transform3D grid)
    {
        ContainmentFieldProfile p = profile;
        double sA = ContainmentFieldGeometry.ArchLength(p) * 0.5, aA = 0.4 * circ + 321.0;
        ContainmentFieldPoint anchor = ContainmentFieldGeometry.SurfaceAtArchLength(p, aA, sA);
        RingLodGeometrySnapshot live = Snap(p.WallTopMeters, new RingCanonicalPosition(anchor.AroundMeters, anchor.VerticalMeters, anchor.AcrossMeters), grid);
        ContainmentNearPatchMesh near = ContainmentFieldNearPatch.Build(p, live, anchor, 3000.0);
        ContainmentRipple At(double dAround, double dArch = 0.0, long id = 1)
            => Rip(ContainmentDisturbanceKind.Lightning, ContainmentFieldGeometry.SurfaceAtArchLength(p, aA + dAround, sA + dArch), 0.7f, 4000f, 3f, id: id);

        // 1 km, 7 km (the 4.6 km patch disc reaches the ~3.3 km window), 60 km. (8 km would not overlap: 3.3 + 4.6 = 7.9 km.)
        var slots = new List<ContainmentRipple?> { At(60_000.0), At(7000.0), null, At(1000.0) };
        bool[] vis = { true, true, true, true };
        var outp = new int[8];
        int n = ContainmentRipplePatches.SelectNear(near, live, slots, vis, outp);
        Check(n == 2 && outp[0] == 3 && outp[1] == 1, $"near selection order: n={n} [{string.Join(",", outp.Take(Math.Max(n, 0)))}]");
        vis[3] = false;
        n = ContainmentRipplePatches.SelectNear(near, live, slots, vis, outp);
        Check(n == 1 && outp[0] == 1, "invisible slot skipped");
        // Overlapping six: exactly 4, nearest first (slot order shuffled).
        double[] d = { 3000, 500, 2500, 1000, 2000, 1500 };
        slots = d.Select((x, i) => (ContainmentRipple?)At(x, 0.0, i)).ToList();
        vis = Enumerable.Repeat(true, 6).ToArray();
        n = ContainmentRipplePatches.SelectNear(near, live, slots, vis, outp);
        Check(n == 4 && outp[0] == 1 && outp[1] == 3 && outp[2] == 5 && outp[3] == 4, $"six overlapping: n={n} [{string.Join(",", outp.Take(Math.Max(n, 0)))}]");
        Span<int> two = stackalloc int[2];
        Check(ContainmentRipplePatches.SelectNear(near, live, slots, vis, two) == 2 && two[0] == 1 && two[1] == 3, "output length caps the count");
        // Arch overlap: far along the arch is rejected.
        slots = new List<ContainmentRipple?> { At(0.0, 40_000.0) };
        Check(ContainmentRipplePatches.SelectNear(near, live, slots, new[] { true }, outp) == 0, "arch-distant ripple rejected");

        ContainmentRipple atAnchor = Rip(ContainmentDisturbanceKind.Lightning, anchor, 0.7f, 4000f, 3f);
        Vector4 c = ContainmentRipplePatches.CentreIn(near, atAnchor, live, circ);
        Check(c.X == 0f && c.Y == 0f && c.W == 0f, $"CentreIn at the anchor {c}");
        Near(c.Z, near.SurfaceMetresPerUvX, 1e-6, "CentreIn z equals SurfaceMetresPerUvX");
        Near(c.Z, 1.0, 0.2, "CentreIn z is about 1");
        Vector4 c2 = ContainmentRipplePatches.CentreIn(near, At(1000.0, 500.0), live, circ);
        Near(c2.X, 1000.0 * near.NoiseAroundScale, 1e-2, "CentreIn x");
        Near(c2.Y, 500.0, 1e-2, "CentreIn y");
        // Across the around wrap.
        ContainmentFieldPoint wrapAnchor = ContainmentFieldGeometry.SurfaceAtArchLength(p, circ - 200.0, sA);
        ContainmentNearPatchMesh wn = ContainmentFieldNearPatch.Build(p, live, wrapAnchor, 3000.0);
        Vector4 cw = ContainmentRipplePatches.CentreIn(wn, Rip(ContainmentDisturbanceKind.Lightning, ContainmentFieldGeometry.SurfaceAtArchLength(p, 300.0, sA), 0.7f, 4000f, 3f), live, circ);
        Near(cw.X, 500.0 * wn.NoiseAroundScale, 1e-2, "CentreIn across the wrap");
        Near(near.RangeMeters, 3000.0, 0.0, "Build RangeMeters preserved");
        Check(near.QuadsPerSide == ContainmentNearPatchMesh.Quads, "near QuadsPerSide");
    }

    // ---- f ---------------------------------------------------------------------------------------------------------------------------------------

    private void FarQuad()
    {
        (double start, double end) = ContainmentRipplePatches.FarFade(220_000.0);
        Near(start, 198_000.0, 1e-6, "far fade start");
        Near(end, 215_600.0, 1e-6, "far fade end");
        ContainmentFieldPoint s = default;
        ContainmentRipple lit = Rip(ContainmentDisturbanceKind.Lightning, s, 0.7f, 4000f, 3f, t => 1f);
        ContainmentRipple dark = Rip(ContainmentDisturbanceKind.Lightning, s, 0.7f, 4000f, 3f);
        ContainmentRippleRenderState lit0 = ContainmentRipplePatches.Evaluate(lit, 100.3, 1f, 1f, false), dark0 = ContainmentRipplePatches.Evaluate(dark, 100.3, 0f, 1f, false);
        Check(lit0.Visible && dark0.Visible, "far states visible");
        Check(!ContainmentRipplePatches.NeedsFarQuad(100_000.0, 5000f, start, lit0), "near patch needs no far quad");
        Check(ContainmentRipplePatches.NeedsFarQuad(195_000.0, 5000f, start, lit0), "reaching the fade band needs a far quad");
        Check(!ContainmentRipplePatches.NeedsFarQuad(195_000.0, 5000f, start, dark0), "no flash, no strike: no far quad");
        Check(!ContainmentRipplePatches.NeedsFarQuad(195_000.0, 5000f, start, default), "invisible: no far quad");
        Check(!ContainmentRipplePatches.NeedsFarQuad(190_000.0, 8000f, start, lit0), "exactly at the band start is not past it");
        ContainmentRipple strike = Rip(ContainmentDisturbanceKind.FieldStrike, s, 0.8f, 3000f, 3f, t => 1f, crawl: 5200f);
        Check(ContainmentRipplePatches.NeedsFarQuad(210_000.0, 6000f, start, ContainmentRipplePatches.Evaluate(strike, 100.3, 1f, 1f, false)), "strike far quad");
    }

    private void FarStrikeFade()
    {
        ContainmentFieldPoint s = new(0.0, 0.0, 1000.0, 9000.0, 0.0, 1.0, 100.0);
        var f = new ContainmentFarStrike { SourceId = 77, Surface = s, StartTimeSeconds = 100.0, Energy = 0.8f, CrawlRadiusMeters = 5000f, LifeSeconds = 2.0f };
        Check(ContainmentRipplePatches.EvaluateFar(f, 99.9, 1f, 1f, false) == default, "far strike before start is default");
        Check(ContainmentRipplePatches.EvaluateFar(f, 102.0, 1f, 1f, false) == default, "far strike at T is default");
        Check(ContainmentRipplePatches.EvaluateFar(f, 103.0, 1f, 1f, false) == default, "far strike after T is default");
        Check(ContainmentRipplePatches.EvaluateFar(f, 100.5, 1f, 0f, false) == default, "far strike with strength 0 is default");
        ContainmentRippleRenderState mid = ContainmentRipplePatches.EvaluateFar(f, 100.5, 1f, 1f, false);
        Check(mid.Visible && mid.StrikeLevel > 0.79f && mid.StrikeLevel <= 0.8001f, "far strike mid-life level = E x flash: " + mid.StrikeLevel);
        Check(mid.Wave.X == 0f && mid.Wave.Y == 0f && mid.Wave.Z == 5000f && mid.Wave.W == 0f, "far strike has no ring");
        Check(mid.Crawl.X == 5000f && mid.Crawl.Y > 0f && mid.Crawl.Y <= 5000f && mid.Crawl.Z >= 0f && mid.Crawl.Z <= 65535f && Math.Abs(mid.Crawl.W) >= 0.4f && Math.Abs(mid.Crawl.W) <= 1.0f, "far strike crawl");
        Check(mid.Crawl.W < 0f, "far strike rim bias sign from phi (left root: toward decreasing arch)");
        float previous = float.MaxValue;
        for (double t = 101.8; t < 102.0; t += 0.01)
        {
            float level = ContainmentRipplePatches.EvaluateFar(f, t, 1f, 1f, false).StrikeLevel;
            Check(level <= previous + 1e-6f, "far strike tail is monotonic at " + t);
            previous = level;
        }
        Check(ContainmentRipplePatches.EvaluateFar(f, 101.999, 1f, 1f, false).StrikeLevel < 1e-3f, "far strike fades to zero at T");
        Check(ContainmentRipplePatches.EvaluateFar(f, 101.0, 1f, 1f, false).StrikeLevel > 0.79f, "far strike is full before the tail");
        Near(ContainmentRipplePatches.ChordDistance(new RingCanonicalPosition(0.0, 0.0, 0.0), new RingCanonicalPosition(0.0, 0.0, 3000.0), 1.0e6, 1.0e7), 3000.0, 1e-6, "chord across");
        double circ = CertifiedRingScale.EffectiveCircumferenceMeters, radius = CertifiedRingScale.EffectiveRadiusMeters;
        Near(ContainmentRipplePatches.ChordDistance(new RingCanonicalPosition(100.0, 0.0, 0.0), new RingCanonicalPosition(100.0 + circ - 200.0, 0.0, 0.0), radius, circ), 200.0, 0.5, "chord wraps around");
    }

    // ---- g ---------------------------------------------------------------------------------------------------------------------------------------

    private void Timing(in ContainmentFieldProfile p, double circ, Transform3D grid)
    {
        ContainmentFieldPoint s = ContainmentFieldGeometry.SurfaceAtArchLength(p, 0.4 * circ, 5000.0);
        RingLodGeometrySnapshot live = Snap(p.WallTopMeters, new RingCanonicalPosition(s.AroundMeters, s.VerticalMeters, s.AcrossMeters), grid);
        ContainmentRipple r = Rip(ContainmentDisturbanceKind.FieldStrike, s, 0.8f, 3000f, 3f, crawl: 5200f);
        for (int i = 0; i < 5; i++) ContainmentRipplePatches.BuildPatch(p, live, r);
        long t0 = Stopwatch.GetTimestamp();
        for (int i = 0; i < 20; i++) ContainmentRipplePatches.BuildPatch(p, live, r);
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency / 20.0;
        Console.WriteLine($"CONTAINMENT_RIPPLE_PATCH BuildPatch average {ms.ToString("F3", CultureInfo.InvariantCulture)} ms over 20");
        Check(ms < 2.0, $"BuildPatch average {ms:F3} ms >= 2 ms");
    }
}
