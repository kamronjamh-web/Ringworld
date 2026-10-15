using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Godot;
using Dictionary = Godot.Collections.Dictionary;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// M1-7: Terrain3D emitter-site hole mask, collision hole filter, stream payload and native control-map clearing.
/// A mask and aperture agreement, B collision geometry, C stream determinism, D budgeted native hole and clear,
/// E synchronous native hole and clear. Prints EMITTER_SITE_HOLE=PASS.
/// </summary>
public partial class EmitterSiteHoleTest : Node
{
    const int Seed = 1337, Side = 384, Region = 128;
    static readonly long Circumference = CertifiedRingScale.AroundVoxelLength;
    static readonly double Radius = CertifiedRingScale.EffectiveRadiusMeters;
    int _checks;
    bool _started;
    ulong _deadlineUsec;
    uint _observedDefault = uint.MaxValue;
    public bool Finished { get; private set; }
    public bool Passed { get; private set; }

    public void Start() { if (_started) return; _started = true; _deadlineUsec = Time.GetTicksUsec() + 300_000_000; _ = RunAsync(); }
    public bool IsFinished() => Finished;
    public bool IsPassed() => Passed;
    public override void _ExitTree() { if (!Finished) Complete(false, "runner removed before completion"); }

    async Task RunAsync()
    {
        try
        {
            using RingTerrainSampler v4 = EmitterSiteTestWorlds.CreateGen17(Seed);
            using RingTerrainSampler v5 = EmitterSiteTestWorlds.CreateGen17Sites(Seed);
            EmitterSiteRegistry registry = v5.EmitterSites ?? throw new InvalidOperationException("v5 sampler has no registry");
            EmitterSite regular = FindRegular(registry), main = registry.MainSite;
            Check(v4.EmitterSites == null, "v4 sampler has no registry");

            TestMask(registry, regular, main);
            TestApertureAgreement(registry, regular, main);
            TestGeometry(registry, regular, main);
            await TestStream(v4, v5, registry, regular);
            await TestBudgetedNative(registry, main);
            await TestSynchronousNative(v5, registry, main);
            Complete(true, string.Empty);
        }
        catch (Exception exception) { Complete(false, exception.ToString()); }
    }

    // ---- A: mask --------------------------------------------------------------------------------------------------
    void TestMask(EmitterSiteRegistry registry, EmitterSite regular, EmitterSite main)
    {
        Check(RingTerrain3DApertureMask.Build(null, Frame(0, 100000, 0, -1)) == null, "null registry gives a null mask");
        // A v5 frame far from every site (no hole): nearest site more than 2 km away.
        registry.TryGetNearestSite(1_000_000, 100_000, out _, out double fu, out double fw);
        Check(fu * fu + fw * fw > 4_000_000, "far fixture is far from every site");
        Check(RingTerrain3DApertureMask.Build(registry, Frame(1_000_000, 100_000, 0, -1)) == null, "v5 frame far from sites gives a null mask");

        foreach (EmitterSite site in new[] { regular, main })
        {
            (int dAround, int dAcross)[] offsets = site.Kind == SiteKind.Main
                ? new[] { (0, 0), (120, -60), (-400, 250) }
                : new[] { (0, 0), (260, -130), (-300, 200) };
            foreach (int direction in new[] { -1, 1 })
            {
                var masks = new List<(RingSurfaceFrame Frame, byte[] Mask)>();
                foreach ((int da, int dc) in offsets)
                {
                    RingSurfaceFrame frame = Frame(site.Around + da, site.Across + dc, site.PadHeight, direction);
                    byte[] mask = RingTerrain3DApertureMask.Build(registry, frame) ?? throw new InvalidOperationException($"no mask for {site.Kind} offset {da},{dc}");
                    byte[] expected = BruteForce(registry, frame, out bool boundaryOk);
                    Check(mask.AsSpan().SequenceEqual(expected), $"{site.Kind} dir {direction} offset {da},{dc} mask equals brute force");
                    Check(boundaryOk, $"{site.Kind} dir {direction} offset {da},{dc} rho <= R-0.5 is a hole and rho >= R+0.5 is not");
                    int holes = RingTerrain3DApertureMask.CountHoles(mask);
                    Check(holes > 0, $"{site.Kind} offset {da},{dc} has holes ({holes})");
                    byte[] images = FlattenControl(RingTerrain3DApertureMask.CreateControlImages(mask), true);
                    Check(ControlMatches(images, mask), $"{site.Kind} offset {da},{dc} control images carry default|hole bits");
                    masks.Add((frame, mask));
                }
                for (int i = 0; i < masks.Count; i++) for (int j = i + 1; j < masks.Count; j++)
                    Check(SharedVerticesAgree(masks[i], masks[j]), $"{site.Kind} dir {direction} frames {i},{j} agree on shared vertices");
            }
        }
        Check(EmitterSiteApertures.HoleRadius(SiteKind.Regular) == 80f && EmitterSiteApertures.HoleRadius(SiteKind.Main) == 155f, "hole radius constants");
    }

    static byte[] BruteForce(EmitterSiteRegistry registry, RingSurfaceFrame frame, out bool boundaryOk)
    {
        var mask = new byte[Side * Side];
        boundaryOk = true;
        long circ = frame.CircumferenceMeters, half = circ / 2;
        for (int z = 0; z < Side; z++) for (int x = 0; x < Side; x++)
        {
            long around = ((frame.AnchorAroundMeters + (long)(x - 128) * 2) % circ + circ) % circ;
            long across = frame.AnchorAcrossMeters + (long)frame.AcrossDirection * (z - 128) * 2;
            bool hole = false; double bestMargin = double.MaxValue;
            foreach (EmitterSite site in registry.Sites)
            {
                long d = around - site.Around;
                if (d > half) d -= circ; else if (d < -half) d += circ;
                if (Math.Abs(d) > 400) continue;
                long dw = across - site.Across;
                if (Math.Abs(dw) > 400) continue;
                long radius = (long)EmitterSiteApertures.HoleRadius(site.Kind);
                long rho2 = d * d + dw * dw;
                if (rho2 < radius * radius) hole = true;
                bestMargin = Math.Min(bestMargin, Math.Sqrt(rho2) - radius);
            }
            mask[z * Side + x] = hole ? (byte)1 : (byte)0;
            if (bestMargin <= -0.5 && !hole) boundaryOk = false;
            if (bestMargin >= 0.5 && hole) boundaryOk = false;
        }
        return mask;
    }

    static bool SharedVerticesAgree((RingSurfaceFrame Frame, byte[] Mask) a, (RingSurfaceFrame Frame, byte[] Mask) b)
    {
        long shiftX = a.Frame.SignedAroundDeltaMeters(b.Frame.AnchorAroundMeters) / 2;
        long shiftZ = (long)a.Frame.AcrossDirection * (b.Frame.AnchorAcrossMeters - a.Frame.AnchorAcrossMeters) / 2;
        int compared = 0;
        for (int z = 0; z < Side; z++) for (int x = 0; x < Side; x++)
        {
            long bx = x - shiftX, bz = z - shiftZ;
            if (bx < 0 || bz < 0 || bx >= Side || bz >= Side) continue;
            compared++;
            if (a.Mask[z * Side + x] != b.Mask[bz * Side + bx]) return false;
        }
        return compared > 0;
    }

    static byte[] FlattenControl(Godot.Collections.Array<Image> images, bool dispose)
    {
        var data = new byte[9 * Region * Region * 4];
        for (int i = 0; i < 9; i++)
        {
            byte[] bytes = images[i].GetData();
            Buffer.BlockCopy(bytes, 0, data, i * bytes.Length, bytes.Length);
            if (dispose) images[i].Dispose();
        }
        return data;
    }

    static bool ControlMatches(byte[] flat, byte[] mask)
    {
        for (int i = 0; i < 9; i++) for (int row = 0; row < Region; row++) for (int column = 0; column < Region; column++)
        {
            uint bits = BitConverter.ToUInt32(flat, ((i * Region + row) * Region + column) * 4);
            uint expected = RingTerrain3DApertureMask.DefaultControlBits | (mask[((i / 3) * Region + row) * Side + (i % 3) * Region + column] != 0 ? RingTerrain3DApertureMask.HoleBit : 0u);
            if (bits != expected) return false;
        }
        return true;
    }

    void TestApertureAgreement(EmitterSiteRegistry registry, EmitterSite regular, EmitterSite main)
    {
        var found = new SurfaceAperture[8];
        foreach (EmitterSite site in new[] { regular, main })
        {
            double radius = EmitterSiteApertures.HoleRadius(site.Kind);
            foreach (double delta in new[] { -0.01, 0.01 })
            {
                bool inside = delta < 0;
                double across = site.Across + radius + delta;
                bool point = EmitterSiteApertures.TryGetApertureAt(registry, site.Around, across, out SurfaceAperture aperture);
                int count = EmitterSiteApertures.GetSurfaceApertures(registry, site.Around, across, 0.0, found);
                bool listed = false;
                for (int i = 0; i < Math.Min(count, found.Length); i++) if (found[i].SiteIndex == site.Index) listed = true;
                Check(point == inside && listed == inside, $"{site.Kind} R{(inside ? "-" : "+")}0.01 apertures agree (point={point}, listed={listed})");
                if (point) Check(aperture.SiteIndex == site.Index && aperture.Radius == radius && aperture.SurfaceHeight == site.PadHeight && aperture.FloorDepth == EmitterSiteApertures.PlugDepthM, "aperture record fields");
                bool alongAround = EmitterSiteApertures.TryGetApertureAt(registry, site.Around + radius + delta, site.Across, out _);
                Check(alongAround == inside, $"{site.Kind} around-axis R{(inside ? "-" : "+")}0.01");
            }
            Check(!EmitterSiteApertures.TryGetApertureAt(null, site.Around, site.Across, out _), "null registry has no aperture");
            Check(EmitterSiteApertures.GetSurfaceApertures(null, site.Around, site.Across, 100.0, found) == 0, "null registry lists no apertures");
        }
        int wide = EmitterSiteApertures.GetSurfaceApertures(registry, main.Around, main.Across, 10_000.0, found);
        bool hasMain = false;
        for (int i = 0; i < Math.Min(wide, found.Length); i++) if (found[i].Kind == SiteKind.Main) hasMain = true;
        Check(hasMain, "wide query includes the main aperture");
    }

    // ---- B: geometry ----------------------------------------------------------------------------------------------
    void TestGeometry(EmitterSiteRegistry registry, EmitterSite regular, EmitterSite main)
    {
        foreach (EmitterSite site in new[] { regular, main })
        foreach (int tileCells in new[] { 16, 32 })
        {
            RingSurfaceFrame frame = Frame(site.Around + 40, site.Across - 30, site.PadHeight, -1);
            byte[] mask = RingTerrain3DApertureMask.Build(registry, frame) ?? throw new InvalidOperationException("geometry mask");
            float[] heights = new float[Side * Side];
            for (int i = 0; i < heights.Length; i++) heights[i] = site.PadHeight + (i % 7) * .25f;
            using var geometry = new RingTerrain3DCollisionGeometry();
            Check(geometry.PrepareGameplayWithHoles(heights, frame.AnchorAroundMeters, frame.AnchorAcrossMeters, frame.AnchorHeight, frame.AcrossDirection, -8000f, 8000f, tileCells, mask), $"{site.Kind} geometry prepares: " + geometry.GetDiagnostics()["error"].AsString());
            Check(geometry.MatchesHoles(mask) && !geometry.MatchesHoles(null) && !geometry.MatchesHoles(new byte[Side * Side]), "geometry MatchesHoles");

            int expectedTriangles = 0, emitted = 0, badVertex = 0, badCentroid = 0, tilesEmpty = 0;
            for (int z = 0; z < 192; z++) for (int x = 0; x < 192; x++)
            {
                bool a = Hole(mask, x, z), b = Hole(mask, x + 1, z), c = Hole(mask, x, z + 1), d = Hole(mask, x + 1, z + 1);
                if (!a && !b && !c) expectedTriangles++;
                if (!b && !d && !c) expectedTriangles++;
            }
            var near = new SurfaceAperture[8];
            int nearCount = Math.Min(near.Length, EmitterSiteApertures.GetSurfaceApertures(registry, frame.AnchorAroundMeters, frame.AnchorAcrossMeters, 600.0, near));
            long totalVertices = 0;
            for (int t = 0; t < geometry.TileCount; t++)
            {
                Vector3[] tile = geometry.GetTile(t);
                totalVertices += tile.Length;
                if (tile.Length == 0) tilesEmpty++;
                if (tile.Length % 3 != 0) badVertex++;
                for (int v = 0; v + 2 < tile.Length; v += 3)
                {
                    emitted++;
                    Vector3 centroid = (tile[v] + tile[v + 1] + tile[v + 2]) / 3f;
                    for (int k = 0; k < 3; k++)
                    {
                        RingSurfaceCoordinates coords = frame.Inverse(tile[v + k]);
                        int ix = (int)Math.Round(coords.AroundOffsetMeters / 2.0) + 128, iz = (int)Math.Round((coords.AcrossMeters - frame.AnchorAcrossMeters) * frame.AcrossDirection / 2.0) + 128;
                        if (mask[iz * Side + ix] != 0) badVertex++;
                    }
                    RingSurfaceCoordinates mid = frame.Inverse(centroid);
                    for (int i = 0; i < nearCount; i++)
                    {
                        double du = mid.AroundOffsetMeters - frame.SignedAroundDeltaMeters(near[i].Around);
                        double dw = mid.AcrossMeters - near[i].Across;
                        if (Math.Sqrt(du * du + dw * dw) < near[i].Radius - 0.1) badCentroid++;
                    }
                }
            }
            Check(emitted == expectedTriangles, $"{site.Kind} cells {tileCells} triangle count {emitted} equals brute force {expectedTriangles}");
            Check(badVertex == 0, $"{site.Kind} cells {tileCells} no triangle uses a hole vertex");
            Check(badCentroid == 0, $"{site.Kind} cells {tileCells} no centroid inside the hole");
            Check(geometry.GetDiagnostics()["geometry_bytes"].AsInt64() == totalVertices * 12, "geometry_bytes follows the actual tile lengths");
            if (site.Kind == SiteKind.Main && tileCells == 32) Check(tilesEmpty > 0, $"main hole fully empties at least one tile ({tilesEmpty})");
        }

        // A null mask is bit-identical to PrepareGameplay.
        EmitterSite probe = regular;
        RingSurfaceFrame f = Frame(probe.Around, probe.Across, probe.PadHeight, -1);
        float[] flat = new float[Side * Side];
        for (int i = 0; i < flat.Length; i++) flat[i] = probe.PadHeight + ((i * 31) % 17) * .125f;
        using var plain = new RingTerrain3DCollisionGeometry();
        using var nullMask = new RingTerrain3DCollisionGeometry();
        Check(plain.PrepareGameplay(flat, f.AnchorAroundMeters, f.AnchorAcrossMeters, f.AnchorHeight, -1, -8000f, 8000f, 32), "plain geometry prepares");
        Check(nullMask.PrepareGameplayWithHoles(flat, f.AnchorAroundMeters, f.AnchorAcrossMeters, f.AnchorHeight, -1, -8000f, 8000f, 32, null), "null-mask geometry prepares");
        bool identical = plain.TileCount == nullMask.TileCount && nullMask.MatchesHoles(null);
        for (int t = 0; identical && t < plain.TileCount; t++)
            identical = plain.GetTile(t).Length == 32 * 32 * 6 && MemoryMarshal.AsBytes(plain.GetTile(t).AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(nullMask.GetTile(t).AsSpan()));
        Check(identical, "null mask vertex arrays are bit-identical to PrepareGameplay");
        long bytes = plain.GetDiagnostics()["geometry_bytes"].AsInt64();
        Check(bytes == nullMask.GetDiagnostics()["geometry_bytes"].AsInt64() && bytes == 36L * 32 * 32 * 6 * 12, "null mask geometry_bytes is the fixed size");
    }

    static bool Hole(byte[] mask, int pointX, int pointZ) => mask[(32 + pointZ) * Side + 32 + pointX] != 0;

    // ---- C: stream ------------------------------------------------------------------------------------------------
    async Task TestStream(RingTerrainSampler v4, RingTerrainSampler v5, EmitterSiteRegistry registry, EmitterSite site)
    {
        using var stream = new RingTerrain3DGameplayStream(v5, -8000f, 8000f, CertifiedRingScale.RingWidthMeters, Circumference);
        stream.Configure(2, 16);
        RingSurfaceFrame on = Frame(site.Around, site.Across, site.PadHeight, -1);
        RingSurfaceFrame shifted = Frame(site.Around + 256, site.Across, site.PadHeight, -1);
        byte[] firstMask = null!, firstControl = null!;
        long hitsBefore = 0;
        for (int step = 0; step < 3; step++)
        {
            RingSurfaceFrame frame = step == 1 ? shifted : on;
            if (step == 2) hitsBefore = stream.Diagnostics["cache_hits"].AsInt64();
            stream.Request(frame, Array.Empty<RingSurfaceHeightEdit>(), 0, step + 1, 32);
            PreparedWindow prepared = await TakeReady(stream);
            try
            {
                byte[]? expected = RingTerrain3DApertureMask.Build(registry, frame);
                Check(prepared.HoleMask != null && expected != null && prepared.HoleMask.AsSpan().SequenceEqual(expected), $"stream step {step} mask equals the pure mask");
                Check(prepared.ControlImages != null && prepared.ControlImages.Count == 9, $"stream step {step} carries nine control images");
                byte[] control = FlattenControl(prepared.ControlImages!, false);
                Check(ControlMatches(control, prepared.HoleMask!), $"stream step {step} control bytes carry the hole bits");
                Check(prepared.Geometry!.MatchesHoles(prepared.HoleMask), $"stream step {step} geometry matches the mask");
                if (step == 0) { firstMask = (byte[])prepared.HoleMask!.Clone(); firstControl = control; }
                if (step == 2)
                {
                    Check(prepared.HoleMask!.AsSpan().SequenceEqual(firstMask) && control.AsSpan().SequenceEqual(firstControl), "returning to the first frame reproduces the mask and control bytes");
                    Check(stream.Diagnostics["cache_hits"].AsInt64() - hitsBefore == 9, "return request is nine cache hits");
                }
            }
            finally { prepared.Dispose(); }
        }

        using var v4Stream = new RingTerrain3DGameplayStream(v4, -8000f, 8000f, CertifiedRingScale.RingWidthMeters, Circumference);
        v4Stream.Request(on, Array.Empty<RingSurfaceHeightEdit>(), 0, 1, 32);
        PreparedWindow plain = await TakeReady(v4Stream);
        try { Check(plain.HoleMask == null && plain.ControlImages == null && plain.Geometry!.MatchesHoles(null), "v4 stream has a null mask and null control images"); }
        finally { plain.Dispose(); }
    }

    async Task<PreparedWindow> TakeReady(RingTerrain3DGameplayStream stream)
    {
        while (true)
        {
            if (Time.GetTicksUsec() >= _deadlineUsec) throw new TimeoutException("hole test deadline");
            stream.Poll();
            if (stream.Error.Length > 0) throw new InvalidOperationException(stream.Error);
            if (stream.TryTakeReady(out PreparedWindow prepared)) return prepared;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    // ---- D: budgeted native -----------------------------------------------------------------------------------------
    async Task TestBudgetedNative(EmitterSiteRegistry registry, EmitterSite main)
    {
        var surface = new RingTerrain3DSurface();
        AddChild(surface);
        surface.SetGameplayWorldFrames(Transform3D.Identity, Transform3D.Identity);
        RingSurfaceFrame holeFrame = Frame(main.Around, main.Across, main.PadHeight, -1);
        byte[] mask = RingTerrain3DApertureMask.Build(registry, holeFrame) ?? throw new InvalidOperationException("native mask");
        Check(mask[128 * Side + 128] != 0, "the anchor vertex is a hole");

        // Window 1: holes, anchor inside the hole.
        Check(Begin(surface, holeFrame, mask, 1, out int emptyTiles), "begin window with holes: " + surface.LastError);
        Check(emptyTiles > 0, $"window 1 has fully emptied collision tiles ({emptyTiles})");
        await Prepare(surface);
        surface.SetGameplayWorldFrames(Transform3D.Identity, Transform3D.Identity);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Check(surface.ActivateStagedWindow(1), "activate window 1 with a hole at the anchor");
        await SettlePhysics();
        CheckHoleWindow(surface, holeFrame, mask, main, 1);
        Check(surface.GetPreparedDiagnostics()["hole_vertices"].AsInt32() == RingTerrain3DApertureMask.CountHoles(mask), "hole_vertices diagnostic counts the mask");

        // Window 2: no holes, fresh owner. Its fresh control texels reveal Terrain3D's default.
        RingSurfaceFrame second = Frame(main.Around + 256, main.Across, main.PadHeight, -1);
        Check(Begin(surface, second, null, 2, out _), "begin window 2 without holes: " + surface.LastError);
        await Prepare(surface);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Check(surface.ActivateStagedWindow(2), "activate window 2");
        await SettlePhysics();
        _observedDefault = BitConverter.ToUInt32(RegionControl(surface, 0), 0);
        GD.Print($"EMITTER_SITE_HOLE observed default control texel=0x{_observedDefault:X} constant=0x{RingTerrain3DApertureMask.DefaultControlBits:X}");
        Check(_observedDefault == RingTerrain3DApertureMask.DefaultControlBits, "a fresh region's default control texel equals DefaultControlBits");
        CheckNoHoleWindow(surface, second, main, 2);

        // Window 3: no holes again, reusing the first owner whose native regions hold the old hole bits.
        Check(Begin(surface, holeFrame, null, 3, out _), "begin window 3 without holes: " + surface.LastError);
        await Prepare(surface);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Check(surface.ActivateStagedWindow(3), "activate window 3 on the reused owner");
        await SettlePhysics();
        CheckNoHoleWindow(surface, holeFrame, main, 3);
        Check(surface.GetPreparedDiagnostics()["hole_vertices"].AsInt32() == 0, "hole_vertices diagnostic is zero after the clear");

        // Window 4: holes again on a reused owner re-applies them.
        Check(Begin(surface, holeFrame, mask, 4, out _), "begin window 4 with holes: " + surface.LastError);
        await Prepare(surface);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Check(surface.ActivateStagedWindow(4), "activate window 4");
        await SettlePhysics();
        CheckHoleWindow(surface, holeFrame, mask, main, 4);

        // Mismatched payloads are rejected before any state changes.
        Check(!BeginMismatched(surface, holeFrame, mask, 5), "geometry/mask mismatch is rejected");
        surface.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    async Task SettlePhysics()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    bool Begin(RingTerrain3DSurface surface, RingSurfaceFrame frame, byte[]? mask, long revision, out int emptyTiles)
    {
        var heights = new float[Side * Side];
        Array.Fill(heights, frame.AnchorHeight);
        var heightImages = new Godot.Collections.Array<Image>();
        var biomeImages = new Godot.Collections.Array<Image>();
        for (int i = 0; i < 9; i++)
        {
            heightImages.Add(Image.CreateFromData(Region, Region, false, Image.Format.Rf, new byte[Region * Region * 4]));
            Image biome = Image.CreateEmpty(Region, Region, false, Image.Format.Rgba8); biome.Fill(new Color(.2f, .4f, .1f, .5f)); biomeImages.Add(biome);
        }
        Godot.Collections.Array<Image>? control = mask == null ? null : RingTerrain3DApertureMask.CreateControlImages(mask);
        var geometry = new RingTerrain3DCollisionGeometry();
        emptyTiles = 0;
        try
        {
            if (!geometry.PrepareGameplayWithHoles(heights, frame.AnchorAroundMeters, frame.AnchorAcrossMeters, frame.AnchorHeight, frame.AcrossDirection, -8192f, 8000f, 32, mask))
                throw new InvalidOperationException(geometry.GetDiagnostics()["error"].AsString());
            for (int t = 0; t < geometry.TileCount; t++) if (geometry.GetTile(t).Length == 0) emptyTiles++;
            bool ok = surface.BeginGameplayPreparedWindowWithHoles(heights, heightImages, biomeImages, geometry, frame.AnchorAroundMeters, frame.AnchorAcrossMeters,
                frame.AnchorHeight, frame.AcrossDirection, revision, -8192f, 8000f, control, mask);
            if (!ok) geometry.Dispose();
            return ok;
        }
        finally
        {
            foreach (Image image in heightImages) image.Dispose();
            foreach (Image image in biomeImages) image.Dispose();
            if (control != null) foreach (Image image in control) image.Dispose();
        }
    }

    bool BeginMismatched(RingTerrain3DSurface surface, RingSurfaceFrame frame, byte[] mask, long revision)
    {
        // Geometry prepared without holes while the payload claims a mask, then control images without a mask.
        var heights = new float[Side * Side];
        Array.Fill(heights, frame.AnchorHeight);
        var heightImages = new Godot.Collections.Array<Image>();
        var biomeImages = new Godot.Collections.Array<Image>();
        for (int i = 0; i < 9; i++)
        {
            heightImages.Add(Image.CreateFromData(Region, Region, false, Image.Format.Rf, new byte[Region * Region * 4]));
            biomeImages.Add(Image.CreateEmpty(Region, Region, false, Image.Format.Rgba8));
        }
        var control = RingTerrain3DApertureMask.CreateControlImages(mask);
        using var geometry = new RingTerrain3DCollisionGeometry();
        geometry.PrepareGameplay(heights, frame.AnchorAroundMeters, frame.AnchorAcrossMeters, frame.AnchorHeight, frame.AcrossDirection, -8192f, 8000f, 32);
        try
        {
            bool orphan = surface.BeginGameplayPreparedWindowWithHoles(heights, heightImages, biomeImages, geometry, frame.AnchorAroundMeters, frame.AnchorAcrossMeters,
                frame.AnchorHeight, frame.AcrossDirection, revision, -8192f, 8000f, control, null);
            Check(!orphan, "control images without a mask are rejected");
            return surface.BeginGameplayPreparedWindowWithHoles(heights, heightImages, biomeImages, geometry, frame.AnchorAroundMeters, frame.AnchorAcrossMeters,
                frame.AnchorHeight, frame.AcrossDirection, revision, -8192f, 8000f, control, mask);
        }
        finally
        {
            foreach (Image image in heightImages) image.Dispose();
            foreach (Image image in biomeImages) image.Dispose();
            foreach (Image image in control) image.Dispose();
        }
    }

    async Task Prepare(RingTerrain3DSurface surface)
    {
        for (int i = 0; i < 600; i++)
        {
            Dictionary state = surface.AdvanceBudgetedPreparation(.5, .5);
            if (!state["ok"].AsBool()) throw new InvalidOperationException("preparation failed: " + state["error"].AsString());
            if (state["ready"].AsBool()) return;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        throw new TimeoutException("native preparation did not become ready");
    }

    void CheckHoleWindow(RingTerrain3DSurface surface, RingSurfaceFrame frame, byte[] mask, EmitterSite site, int window)
    {
        int wrong = 0, holeTexels = 0;
        for (int region = 0; region < 9; region++)
        {
            byte[] data = RegionControl(surface, region);
            for (int row = 0; row < Region; row++) for (int column = 0; column < Region; column++)
            {
                bool hole = mask[((region / 3) * Region + row) * Side + (region % 3) * Region + column] != 0;
                uint bits = BitConverter.ToUInt32(data, (row * Region + column) * 4);
                if (hole) { holeTexels++; if ((bits & RingTerrain3DApertureMask.HoleBit) == 0) wrong++; }
                if (bits != (RingTerrain3DApertureMask.DefaultControlBits | (hole ? RingTerrain3DApertureMask.HoleBit : 0u))) wrong++;
            }
        }
        Check(wrong == 0 && holeTexels == RingTerrain3DApertureMask.CountHoles(mask), $"window {window}: native control texels carry bit 2 exactly on holes ({holeTexels} holes)");
        float radius = EmitterSiteApertures.HoleRadius(site.Kind);
        Check(!RayHits(frame, site, radius - 4, 0, out _), $"window {window}: ray at R-4 (around axis) misses");
        Check(!RayHits(frame, site, 0, radius - 4, out _), $"window {window}: ray at R-4 (across axis) misses");
        Check(!RayHits(frame, site, 0, 0, out _), $"window {window}: ray at the bore centre misses");
        Check(RayHits(frame, site, radius + 4, 0, out Vector3 hit) && hit.DistanceTo(Expected(frame, site, radius + 4, 0)) < .05, $"window {window}: ray at R+4 hits near the surface");
        Check(RayHits(frame, site, -(radius + 4), 0, out _) && RayHits(frame, site, 0, -(radius + 4), out _), $"window {window}: rays on the other sides at R+4 hit");
    }

    void CheckNoHoleWindow(RingTerrain3DSurface surface, RingSurfaceFrame frame, EmitterSite site, int window)
    {
        int wrong = 0;
        for (int region = 0; region < 9; region++)
        {
            byte[] data = RegionControl(surface, region);
            for (int i = 0; i < Region * Region; i++) if (BitConverter.ToUInt32(data, i * 4) != RingTerrain3DApertureMask.DefaultControlBits) wrong++;
        }
        Check(wrong == 0, $"window {window}: every native control texel is back to default ({wrong} differ)");
        if (frame.AnchorAroundMeters == site.Around)
        {
            Check(RayHits(frame, site, 0, 0, out Vector3 hit) && hit.DistanceTo(Expected(frame, site, 0, 0)) < .05, $"window {window}: ray at the old centre hits");
            Check(RayHits(frame, site, EmitterSiteApertures.HoleRadius(site.Kind) - 4, 0, out _), $"window {window}: ray at the old R-4 hits");
        }
    }

    static Vector3 Expected(RingSurfaceFrame frame, EmitterSite site, double offsetAround, double offsetAcross)
        => frame.MapPosition(site.Around + offsetAround, site.Across + offsetAcross, frame.AnchorHeight);

    bool RayHits(RingSurfaceFrame frame, EmitterSite site, double offsetAround, double offsetAcross, out Vector3 position)
    {
        Vector3 p = Expected(frame, site, offsetAround, offsetAcross);
        var query = PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 3f, p - Vector3.Up * 6f, 1);
        Dictionary hit = GetTree().Root.World3D.DirectSpaceState.IntersectRay(query);
        position = hit.Count == 0 ? Vector3.Zero : hit["position"].AsVector3();
        return hit.Count != 0;
    }

    static byte[] RegionControl(RingTerrain3DSurface surface, int region)
    {
        GodotObject data = surface.TerrainNativeData ?? throw new InvalidOperationException("no native data");
        GodotObject r = data.Call("get_region", new Vector2I(region % 3 - 1, region / 3 - 1)).AsGodotObject() ?? throw new InvalidOperationException("missing region " + region);
        return r.Call("get_control_map").As<Image>().GetData();
    }

    // ---- E: synchronous native --------------------------------------------------------------------------------------
    async Task TestSynchronousNative(RingTerrainSampler v5, EmitterSiteRegistry registry, EmitterSite main)
    {
        var surface = new RingTerrain3DSurface();
        AddChild(surface);
        float height = v5.GetPresentationSurfaceHeight(main.Around, main.Across);
        RingSurfaceFrame frame = Frame(main.Around, main.Across, height, -1);
        var field = new RingSurfaceHeightField(v5.TerrainDefinitionHash, Circumference, CertifiedRingScale.RingWidthMeters, -8000f, 8000f, v5.GetPresentationSurfaceHeight, useTriangleInterpolation: true);
        surface.InitializeGameplayWindow(field, frame, v5);
        await SettlePhysics();
        byte[] mask = RingTerrain3DApertureMask.Build(registry, frame) ?? throw new InvalidOperationException("sync mask");
        Check(surface.GetPreparedDiagnostics()["hole_vertices"].AsInt32() == RingTerrain3DApertureMask.CountHoles(mask), "sync window reports the mask's hole count");
        int wrong = 0;
        for (int region = 0; region < 9; region++)
        {
            byte[] data = RegionControl(surface, region);
            for (int row = 0; row < Region; row++) for (int column = 0; column < Region; column++)
            {
                bool hole = mask[((region / 3) * Region + row) * Side + (region % 3) * Region + column] != 0;
                if (BitConverter.ToUInt32(data, (row * Region + column) * 4) != (RingTerrain3DApertureMask.DefaultControlBits | (hole ? RingTerrain3DApertureMask.HoleBit : 0u))) wrong++;
            }
        }
        Check(wrong == 0, $"sync window native control texels match the mask ({wrong} differ)");
        Check(!RayAt(frame, main, 150, 0, height), "sync ray at R-5 misses");
        Check(RayAt(frame, main, 160, 0, height), "sync ray at R+5 hits");

        // Two windows without holes: the second reuses the synchronously built owner.
        Check(Begin(surface, Frame(main.Around + 256, main.Across, height, -1), null, 2, out _), "sync: begin window 2: " + surface.LastError);
        await Prepare(surface);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Check(surface.ActivateStagedWindow(2), "sync: activate window 2");
        Check(Begin(surface, frame, null, 3, out _), "sync: begin window 3 on the reused sync owner: " + surface.LastError);
        await Prepare(surface);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Check(surface.ActivateStagedWindow(3), "sync: activate window 3");
        await SettlePhysics();
        int stale = 0;
        for (int region = 0; region < 9; region++)
        {
            byte[] data = RegionControl(surface, region);
            for (int i = 0; i < Region * Region; i++) if (BitConverter.ToUInt32(data, i * 4) != RingTerrain3DApertureMask.DefaultControlBits) stale++;
        }
        Check(stale == 0, $"sync owner reused without holes is cleared ({stale} stale texels)");
        Check(RayHits(frame, main, 0, 0, out _), "sync owner: ray at the old centre hits after the clear");
        surface.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    bool RayAt(RingSurfaceFrame frame, EmitterSite site, double offsetAround, double offsetAcross, float height)
    {
        Vector3 p = frame.MapPosition(site.Around + offsetAround, site.Across + offsetAcross, height);
        var query = PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 40f, p - Vector3.Up * 40f, 1);
        return GetTree().Root.World3D.DirectSpaceState.IntersectRay(query).Count != 0;
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------
    static EmitterSite FindRegular(EmitterSiteRegistry registry)
    {
        foreach (EmitterSite site in registry.Sites)
            if (site.Kind == SiteKind.Regular && site.Across > 20_000 && site.Across < 300_000 && site.Around % 2 == 0 && site.Across % 2 == 0) return site;
        throw new InvalidOperationException("no suitable regular site");
    }

    static RingSurfaceFrame Frame(long around, int across, float height, int direction)
        => new(around, across, height, Circumference, Radius, 2, direction);

    void Check(bool condition, string label)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException("EMITTER_SITE_HOLE check failed: " + label);
    }

    void Complete(bool passed, string error)
    {
        if (Finished) return;
        Finished = true; Passed = passed;
        if (passed) GD.Print($"EMITTER_SITE_HOLE=PASS checks={_checks} default_control=0x{_observedDefault:X}");
        else GD.PushError("EMITTER_SITE_HOLE=FAIL " + error);
    }
}
