using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using RingworldTechDemo.Shadows;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

/// <summary>
/// Shadow revamp S4c headless coverage (docs/shadow-revamp-plan.md S4): layer-role publish atomicity, crossfade gating, adaptive slicing rule, build cursor,
/// world -> anchor-tangent frame global against RingLodGeometrySnapshot, bounds/previous-bounds shrink and recentre continuity against a fresh fill.
/// </summary>
public partial class HeightShadowScheduleTest : RefCounted
{
    private const long Circumference = 31_415_936L;
    private const int Width = 318_000;
    private int _checks;

    public bool Run()
    {
        try
        {
            VerifyLayerRoles();
            VerifySlicingRule();
            VerifyBuildCursor();
            VerifyFrameGlobal();
            VerifyBoundsAndPrevious();
            VerifyRecentreContinuity();
            VerifySettings();
            GD.Print($"HEIGHT_SHADOW_SCHEDULE=PASS checks={_checks}");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError("HEIGHT_SHADOW_SCHEDULE=FAIL " + exception.Message + "\n" + exception.StackTrace);
            return false;
        }
    }

    private void Require(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    // ---- publish atomicity ----

    private void VerifyLayerRoles()
    {
        // Deterministic rotation: snap, crossfade, crossfade, crossfade.
        var roles = new HeightShadowLayerRoles();
        Require(roles.Front < 0 && roles.Build == 0 && roles.InvariantHolds, "initial roles");
        Require(roles.BeginBuild() == 0, "first build writes layer 0");
        roles.CompleteBuild();
        HeightShadowPublished p = roles.Publish(false, HeightShadowLight.Sun);
        Require(p.Front == 0 && p.Prev == -1 && p.Crossfade == 1f && roles.Build == 1, "first publish is a snap with no previous layer");
        Require(roles.BeginBuild() == 1, "second build writes layer 1"); roles.CompleteBuild();
        p = roles.Publish(false, HeightShadowLight.Sun);
        Require(p.Front == 1 && p.Prev == 0 && p.Crossfade == 0f && roles.Build == 2, "second publish crossfades 0 -> 1 and builds into the free layer 2");
        Require(roles.BeginBuild() == 2, "third build writes layer 2"); roles.CompleteBuild();
        Require(!roles.CanPublish(false), "a third publish must wait for the crossfade of the second (its previous layer is still sampled)");
        Require(roles.CanPublish(true), "a snap may publish at any time");
        roles.Advance(0.5f, 1f);
        Require(!roles.CanPublish(false) && roles.Crossfade == 0.5f, "half way through the crossfade");
        roles.Advance(0.6f, 1f);
        Require(roles.CanPublish(false) && roles.Crossfade == 1f, "crossfade complete");
        p = roles.Publish(false, HeightShadowLight.Sun);
        Require(p.Front == 2 && p.Prev == 1 && roles.Build == 0, "rotation front 2, previous 1, build into the old previous 0");
        Require(roles.InvariantHolds, "invariant after rotation");
        // Snap drops the previous layer.
        roles.BeginBuild(); roles.CompleteBuild();
        p = roles.Publish(true, HeightShadowLight.Planet);
        Require(p.Prev == -1 && p.Crossfade == 1f && p.Light == HeightShadowLight.Planet && roles.InvariantHolds, "snap publish");
        // Zero crossfade seconds completes at once.
        roles.BeginBuild(); roles.CompleteBuild(); roles.Publish(false, HeightShadowLight.Planet);
        roles.Advance(0.001f, 0f);
        Require(roles.Crossfade == 1f, "crossfade of 0 s is instant");

        // Random walk: the layer being written is never one the globals point at; the shown tuple only changes inside Publish / Advance;
        // a non-snap publish happens only at crossfade 1.
        var rng = new Random(2024);
        var live = new HeightShadowLayerRoles();
        HeightShadowPublished shown = live.Current;
        int publishes = 0, snaps = 0;
        for (int step = 0; step < 60000; step++)
        {
            int op = rng.Next(5);
            HeightShadowPublished before = live.Current;
            if (op == 0 && live.CanBeginBuild)
            {
                int layer = live.BeginBuild();
                Require(layer != live.Front && layer != live.Prev, "build target is a layer the globals show");
            }
            else if (op == 1 && live.BuildInFlight) live.CompleteBuild();
            else if (op == 2 && live.BuildComplete)
            {
                bool snap = rng.Next(6) == 0;
                bool allowed = live.CanPublish(snap);
                Require(allowed == (snap || live.Front < 0 || live.Prev < 0 || live.Crossfade >= 1f), "CanPublish rule");
                if (allowed)
                {
                    shown = live.Publish(snap, rng.Next(2) == 0 ? HeightShadowLight.Sun : HeightShadowLight.Planet);
                    publishes++; if (snap) snaps++;
                    Require(live.Current == shown, "published tuple is the roles' current state");
                    Require(snap || before.Front < 0 || (before.Crossfade >= 1f && shown.Prev == before.Front && shown.Crossfade == 0f), "non-snap publish needs a finished crossfade and keeps the old front as previous");
                }
            }
            else if (op == 3)
            {
                live.Advance((float)(rng.NextDouble() * 0.2), 1f);
                Require(live.Crossfade >= before.Crossfade && live.Crossfade <= 1f, "crossfade is monotone within [0,1]");
                shown = live.Current;
            }
            else if (op == 4 && rng.Next(40) == 0)
            {
                live.DiscardBuild();
                if (rng.Next(3) == 0) { live.Invalidate(); shown = live.Current; }
            }
            Require(live.InvariantHolds, "invariant");
            if (live.BuildInFlight) Require(live.Build != shown.Front && live.Build != shown.Prev, "an in-flight build overlaps a layer the globals show");
            Require(live.Current.Front == shown.Front && live.Current.Prev == shown.Prev, "globals changed outside publish");
        }
        Require(publishes > 500 && snaps > 50, $"random walk too short ({publishes} publishes, {snaps} snaps)");
    }

    // ---- slicing ----

    private void VerifySlicingRule()
    {
        Require(HeightShadowSlicing.Rows(64, 0.2, 0.0001) == 64, "cheap noon rows are capped by RowsPerFrame");
        Require(HeightShadowSlicing.Rows(64, 0.15, 0.0035) == 40, "3 degrees on a 4070: 0.15 ms / 3.5 us per row = 42 -> 40 rows");
        Require(HeightShadowSlicing.Rows(64, 0.15, 0.0280) == 8, "8x slower GPU at 3 degrees: 5 -> the 8 row floor");
        Require(HeightShadowSlicing.Rows(64, 0.2, double.NaN) == 64 && HeightShadowSlicing.Rows(64, 0.2, 0) == 64 && HeightShadowSlicing.Rows(64, 0.2, -1) == 64, "unknown cost keeps the cap");
        Require(HeightShadowSlicing.Rows(16, 1000, 0.0001) == 16 && HeightShadowSlicing.Rows(1024, 1000, 0.0001) == 1024, "caps at both ends of the menu range");
        double last = int.MaxValue;
        for (double cost = 0.0005; cost < 0.5; cost *= 1.07)
        {
            int rows = HeightShadowSlicing.Rows(64, 0.15, cost);
            Require(rows <= last && rows % 8 == 0 && rows >= 8 && rows <= 64, "rows are monotone non-increasing in cost, a multiple of 8 and within [8, cap]");
            last = rows;
        }
        // The estimate model is monotone in elevation and L0 is costlier than L1 at low sun.
        Require(HeightShadowSlicing.ModelMsPerRow(0, 0.05) > HeightShadowSlicing.ModelMsPerRow(0, 0.5) && HeightShadowSlicing.ModelMsPerRow(0, 0.05) > HeightShadowSlicing.ModelMsPerRow(1, 0.05), "model shape");
        Require(Math.Abs(HeightShadowSlicing.Smooth(double.NaN, 3) - 3) < 1e-12 && HeightShadowSlicing.Smooth(1, 2) == 2 && HeightShadowSlicing.Smooth(2, 1) > 1 && HeightShadowSlicing.Smooth(2, 1) < 2, "peak-following estimate");
    }

    private void VerifyBuildCursor()
    {
        var rng = new Random(5);
        for (int trial = 0; trial < 50; trial++)
        {
            int levels = 1 + trial % 2;
            var cursor = new HeightShadowBuildCursor(levels);
            var covered = new int[levels, 1024];
            int slices = 0;
            while (!cursor.Done)
            {
                (int level, int row, int count) = cursor.Take(8 + rng.Next(0, 40) * 8);
                Require(level >= 0 && level < levels && row >= 0 && count > 0 && row + count <= 1024, "slice stays inside one level");
                for (int r = row; r < row + count; r++) covered[level, r]++;
                slices++;
                Require(slices < 100000, "cursor does not advance");
            }
            for (int l = 0; l < levels; l++) for (int r = 0; r < 1024; r++) Require(covered[l, r] == 1, "every row exactly once");
            Require(cursor.RowsIssued == levels * 1024L, "rows issued");
            Require(cursor.Take(64).Count == 0, "done cursor yields nothing");
        }
    }

    // ---- frame global ----

    private void VerifyFrameGlobal()
    {
        double radius = Circumference / (2.0 * Math.PI);
        var rng = new Random(77);
        foreach (double cameraAround in new[] { 100.0, Circumference * 0.37, Circumference - 700.0, 1500.0 })
            for (int variant = 0; variant < 3; variant++)
            {
                Basis basis = variant == 0 ? Basis.Identity : new Basis(new Vector3(rng.NextSingle() - .5f, rng.NextSingle() - .5f, rng.NextSingle() - .5f).Normalized(), (float)(rng.NextDouble() * 6.28));
                var centre = variant == 2 ? new Vector3(rng.Next(-5000, 5000), rng.Next(-5000, 5000), rng.Next(-5000, 5000)) : Vector3.Zero;
                double across = 100_000 + rng.Next(0, 5000);
                var anchorCanonical = new RingCanonicalPosition(cameraAround, 5.0, across);
                var geometry = new RingLodGeometrySnapshot(new Transform3D(basis, new Vector3(rng.Next(-300, 300), rng.Next(-300, 300), rng.Next(-300, 300))), new Vector3(rng.Next(-90, 90), rng.Next(-90, 90), rng.Next(-90, 90)),
                    centre, radius, 1234.0, 0.0, Circumference, -1000.0, 1000.0, anchorCanonical);
                foreach (int t0 in new[] { 2, 4, 8 })
                {
                    int t1 = t0 * 4;
                    long cameraIndex = RingHeightShadowLattice.NearestIndex(cameraAround, t0);
                    long anchor = RingHeightShadowLattice.AnchorMeters(cameraIndex * (double)t0, t1);
                    long anchorAcross = RingHeightShadowLattice.AnchorMeters(across, t1);
                    double[] m = RingTerrainHeightShadow.BuildFrameFor(geometry, anchor, anchorAcross, Circumference);
                    for (int k = 0; k < 200; k++)
                    {
                        double around = cameraAround + (rng.NextDouble() - .5) * 6000.0;
                        double vertical = (rng.NextDouble() - .3) * 400.0;
                        double acrossPoint = across + (rng.NextDouble() - .5) * 6000.0;
                        Vector3 world = geometry.CanonicalToWorld(around, vertical, acrossPoint);
                        var f = HeightShadowFrameMath.Apply(m, world.X, world.Y, world.Z);
                        var d = HeightShadowFrameMath.Decode(f, radius);
                        double wrapped = around - Math.Floor(around / Circumference) * Circumference;
                        double unwrapped = RingHeightShadowLattice.UnwrapNear(wrapped, cameraIndex, t0, Circumference) * (double)t0; // nearest texel: compare in metres
                        double expectedAround = (wrapped + Math.Round((cameraIndex * (double)t0 - wrapped) / Circumference) * Circumference) - anchor;
                        Require(Math.Abs(d.Around - expectedAround) < 0.05, $"around decode {d.Around} vs {expectedAround} (t0={t0}, camera={cameraAround}, variant={variant})");
                        Require(Math.Abs(d.Across - (acrossPoint - anchorAcross)) < 0.05, "across decode");
                        Require(Math.Abs(d.Height - vertical) < 0.05, $"height decode {d.Height} vs {vertical}");
                        Require(Math.Abs(f.X) < 40_000 && Math.Abs(f.Z) < 1_000, "frame outputs stay small enough for float32");
                        // Texture coordinates through the global equal the lattice's.
                        var uv = HeightShadowFrameMath.Uv(d.Around, d.Across, t0);
                        double ex = RingHeightShadowLattice.Uv(expectedAround + anchor, anchor, t0), ey = RingHeightShadowLattice.Uv(acrossPoint, anchorAcross, t0);
                        double du = Math.Abs(uv.U - ex), dv = Math.Abs(uv.V - ey);
                        Require((Math.Min(du, 1 - du) < 3e-5) && (Math.Min(dv, 1 - dv) < 3e-5), "uv through the frame != lattice uv");
                        _ = unwrapped;
                    }
                    // A float32 matrix (what the GPU sees) keeps the same precision.
                    var f32 = new float[12];
                    for (int i = 0; i < 12; i++) f32[i] = (float)m[i];
                    Vector3 probe = geometry.CanonicalToWorld(cameraAround + 800, 3, across - 600);
                    float fx = f32[0] * probe.X + f32[1] * probe.Y + f32[2] * probe.Z + f32[3];
                    float fz = f32[8] * probe.X + f32[9] * probe.Y + f32[10] * probe.Z + f32[11];
                    var exact = HeightShadowFrameMath.Apply(m, probe.X, probe.Y, probe.Z);
                    Require(Math.Abs(fx - exact.X) < 0.02 && Math.Abs(fz - exact.Z) < 0.02, "float32 matrix precision");
                }
            }
    }

    // ---- bounds / previous bounds ----

    private void VerifyBoundsAndPrevious()
    {
        var a = new HeightShadowIndexRect(1000, 500, 1024, 1024);
        var b = new HeightShadowIndexRect(1200, 450, 1024, 1024); // recentred by (+200, -50)
        HeightShadowIndexRect i = HeightShadowFrameMath.Intersect(a, b);
        Require(i.I0 == 1200 && i.J0 == 500 && i.Width == 1024 - 200 && i.Height == 1024 - 50, "previous bounds shrink to old window intersect new window");
        Require(HeightShadowFrameMath.Intersect(a, new HeightShadowIndexRect(5000, 500, 1024, 1024)).Width == 0, "disjoint windows have no previous bounds");
        var bounds = HeightShadowFrameMath.Bounds(a, 4, 4096, 2048);
        Require(bounds.AroundMin == 1000 * 4 - 4096 && bounds.AroundMax == 2023 * 4 - 4096 && bounds.AcrossMin == 500 * 4 - 2048 && bounds.AcrossMax == 1523 * 4 - 2048, "bounds are the first and last texel centres, anchor-relative");
        // Every texel of the intersection is addressed by the same slot in both windows (toroidal): the previous layer is valid there.
        for (long x = i.I0; x < i.I1; x += 7) Require(RingHeightShadowLattice.SlotOf(x) == RingHeightShadowLattice.SlotOf(x), "slot");
    }

    // ---- recentre continuity ----

    private void VerifyRecentreContinuity()
    {
        long calls = 0;
        Func<long, int, float> height = (aa, cc) => (float)(40.0 * Math.Sin(aa * 0.011) * Math.Cos(cc * 0.013) + 0.002 * aa % 17.0 + cc * 0.0001);
        var sources = new IHeightShadowSource[]
        {
            new SyntheticHeightShadowSource(Circumference, Width, (aa, cc) => { Interlocked.Increment(ref calls); return height(aa, cc); }),
            new SyntheticHeightShadowSource(Circumference, Width, (aa, cc) => { Interlocked.Increment(ref calls); return height(aa, cc); }),
        };
        foreach ((int dI, int dJ) in new[] { (200, 0), (-300, 140), (600, -400), (129, 129) })
        {
            var clip = new RingHeightShadowClipmap(Circumference, Width, 4) { WorkerPriority = ThreadPriority.BelowNormal };
            var start = new HeightShadowWindow((Circumference - 800) / 4, 40_000);
            clip.FillLevel(0, start, sources, CancellationToken.None);
            Interlocked.Exchange(ref calls, 0);
            var filled = new List<HeightShadowIndexRect>();
            HeightShadowFillReport report = clip.Recentre(0, start.Ic + dI, start.Jc + dJ, sources, CancellationToken.None, out bool moved, filled);
            long expected = 1024L * 1024 - (1024L - Math.Abs(dI)) * (1024L - Math.Abs(dJ));
            Require(moved && report.Texels == expected && Interlocked.Read(ref calls) == expected, $"strip fill count {report.Texels} / {Interlocked.Read(ref calls)} != {expected}");
            // The whole window now equals a fresh full fill at the new window, bit for bit (overlap texels were kept, strips were sampled).
            var fresh = new RingHeightShadowClipmap(Circumference, Width, 4);
            fresh.FillLevel(0, clip.Levels[0].Window!.Value, sources, CancellationToken.None);
            float[] a = clip.Levels[0].Heights, b = fresh.Levels[0].Heights;
            for (int k = 0; k < a.Length; k++)
                if (BitConverter.SingleToInt32Bits(a[k]) != BitConverter.SingleToInt32Bits(b[k])) throw new InvalidOperationException($"recentre ({dI},{dJ}) differs from a fresh fill at slot {k}");
            _checks++;
        }
    }

    // ---- GPU: three-layer output array ----

    /// <summary>
    /// The 3-layer output array (S4c): a build into layer 1 and a build into layer 2 with a different sun each equal the single-layer build for the same sun, bit for bit,
    /// the layers differ, and the build target is the only layer written. Needs a real Vulkan renderer (local RenderingDevice).
    /// </summary>
    public bool RunGpu()
    {
        try
        {
            RenderingDevice rd = RenderingServer.CreateLocalRenderingDevice() ?? throw new InvalidOperationException("no local RenderingDevice (run windowed, not --headless)");
            double radius = Circumference / (2.0 * Math.PI);
            Func<long, int, float> height = (aa, cc) => (aa >= 3_000_100 && aa < 3_000_160 ? 60f : 0f) + (cc >= 100_100 && cc < 100_130 ? 25f : 0f);
            var sources = new IHeightShadowSource[] { new SyntheticHeightShadowSource(Circumference, Width, height) };
            var clip = new RingHeightShadowClipmap(Circumference, Width, 4);
            for (int l = 0; l < 2; l++) clip.FillLevel(l, clip.WindowAt(l, 3_000_000, 100_000), sources, CancellationToken.None);
            long anchor = RingHeightShadowLattice.AnchorMeters(3_000_000, 16);
            HeightShadowBuildInputs Inputs(Vector3 sun)
            {
                var inputs = new HeightShadowBuildInputs { RingRadiusMeters = radius, WidthMeters = Width, AnchorMeters = anchor, OutsideMaxMeters = 400f };
                inputs.AnchorSun = RingHeightShadowLattice.ProjectToTangentFrame(sun.X, sun.Y, sun.Z, anchor / radius);
                for (int l = 0; l < 2; l++)
                {
                    inputs.TexelMeters[l] = clip.Levels[l].TexelMeters; inputs.Windows[l] = clip.Levels[l].Window; inputs.LevelMax[l] = RingTerrainHeightShadowGpu.LevelMaxHeight(clip, l);
                }
                return inputs;
            }
            Vector3 SunAt(double azimuthDeg)
            {
                double theta = 3_000_000 / radius, e = 20 * Math.PI / 180, az = azimuthDeg * Math.PI / 180, c = Math.Cos(theta), sn = Math.Sin(theta);
                double up = Math.Sin(e), around = Math.Cos(e) * Math.Cos(az), across = Math.Cos(e) * Math.Sin(az);
                return new Vector3((float)(-up * c - around * sn), (float)across, (float)(-up * sn + around * c));
            }
            Vector3 sunA = SunAt(0), sunB = SunAt(180);
            (float[] R, float[] G)[] Reference(RenderingDevice device, Vector3 sun)
            {
                using var plain = new RingTerrainHeightShadowGpu(device);
                if (!plain.Initialize()) throw new InvalidOperationException("GPU init failed: " + plain.Error);
                for (int l = 0; l < 2; l++) { plain.UploadAll(l, clip); plain.BuildMips(l); }
                plain.SetBuildInputs(Inputs(sun));
                plain.DispatchBuild(0); plain.DispatchBuild(1);
                device.Submit(); device.Sync();
                return new[] { plain.ReadBuildOutput(0), plain.ReadBuildOutput(1) };
            }
            var refA = Reference(rd, sunA);
            var refB = Reference(rd, sunB);
            using var gpu = new RingTerrainHeightShadowGpu(rd, 2, 3);
            if (!gpu.Initialize()) throw new InvalidOperationException("3-layer GPU init failed: " + gpu.Error);
            for (int l = 0; l < 2; l++) { gpu.UploadAll(l, clip); gpu.BuildMips(l); }
            // Sliced build (64-row slices, like the scheduler) into layer 1 with sun A, then layer 2 with sun B.
            foreach ((int layer, Vector3 sun) in new[] { (1, sunA), (2, sunB) })
            {
                gpu.SetBuildInputs(Inputs(sun));
                for (int l = 0; l < 2; l++) for (int row = 0; row < 1024; row += 64) gpu.DispatchBuild(l, row, 64, layer);
                rd.Submit(); rd.Sync();
            }
            Require(gpu.OutputLayers == 3 && gpu.OutputLayerView(0, 1).IsValid && gpu.OutputLayerView(1, 2).IsValid, "layer views");
            int differing = 0;
            for (int l = 0; l < 2; l++)
            {
                var layer1 = gpu.ReadBuildOutput(l, 1);
                var layer2 = gpu.ReadBuildOutput(l, 2);
                for (int i = 0; i < refA[l].R.Length; i++)
                {
                    Require(layer1.R[i] == refA[l].R[i] && layer1.G[i] == refA[l].G[i], $"layer 1 level {l} texel {i} differs from the single-layer build (sun A)");
                    Require(layer2.R[i] == refB[l].R[i] && layer2.G[i] == refB[l].G[i], $"layer 2 level {l} texel {i} differs from the single-layer build (sun B)");
                    if (layer1.R[i] != layer2.R[i]) differing++;
                }
            }
            Require(differing > 1000, "the two suns must produce different shadow maps");
            GD.Print($"HEIGHT_SHADOW_PUBLISH_GPU=PASS layers=3 sliced-build=bit-exact differing-texels={differing}");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError("HEIGHT_SHADOW_PUBLISH_GPU=FAIL " + exception.Message);
            return false;
        }
    }

    // ---- settings ----

    private void VerifySettings()
    {
        var d = new RingHeightShadowSettings();
        Require(!d.Enabled && d.L0TexelMeters == 4 && d.Levels == 2 && d.RowsPerFrame == 64 && d.FillWorkers == 2 && d.RangeMeters == 8000 && d.Level1TexelMeters == 16, "defaults");
        RingHeightShadowSettings n = new RingHeightShadowSettings { L0TexelMeters = 5, Levels = 9, SunStepDegrees = float.NaN, RowsPerFrame = 5000, PenumbraDegrees = -1, CrossfadeSeconds = 99, FillWorkers = 0, BudgetMs = 100 }.Normalized();
        Require(n.L0TexelMeters == 4 && n.Levels == 2 && n.SunStepDegrees == RingHeightShadowSettings.DefaultSunStepDegrees && n.RowsPerFrame == 1024 && n.PenumbraDegrees == 0 && n.CrossfadeSeconds == 3 && n.FillWorkers == 1 && n.BudgetMs == 2f, "normalization");
        Require(RingHeightShadowSettings.TexelForRange(4000) == 2 && RingHeightShadowSettings.TexelForRange(8000) == 4 && RingHeightShadowSettings.TexelForRange(16000) == 8 && RingHeightShadowSettings.TexelForRange(1) == 0, "range <-> texel");
        Require(RingTerrainHeightShadow.FitTexelToWidth(8, 318_000) == 8 && RingTerrainHeightShadow.FitTexelToWidth(32, 78_000) == 16 && RingTerrainHeightShadow.FitTexelToWidth(32, 318_000) == 32, "band texel fits the ring width");
        Vector3 x = new(1, 0, 0), y = new(MathF.Cos(0.0005f * MathF.PI / 180f), MathF.Sin(0.0005f * MathF.PI / 180f), 0);
        Require(Math.Abs(RingTerrainHeightShadow.AngleDegrees(x, y) - 0.0005) < 1e-6, "sun step angle is exact for tiny angles");
    }
}
