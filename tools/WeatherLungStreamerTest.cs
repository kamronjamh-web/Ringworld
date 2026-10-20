using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// S5 world streamer: the pure hysteresis/dwell core (part A) and the node on a grid fixture (part B): binds on a v8 sampler and is Unavailable on v7,
/// the POI root equals a fresh SiteTransform after anchor changes (including the ring seam), Detail loads inside 36 km, is released after 2 s beyond
/// 44 km without flapping, collision bodies toggle by distance, and forced load failures degrade without an exception. Prints WEATHER_LUNG_STREAMER=PASS.
/// </summary>
public partial class WeatherLungStreamerTest : RefCounted
{
    private const double Dt = 1d / 60d;
    private const int FinalizeSteps = 5; // controller attach stages + completion, see WeatherLungStreamer

    public int Run(bool unused)
    {
        _ = unused;
        var failures = new List<string>();
        Node3D? fixture = null;
        try
        {
            RunPartA(failures);
            SceneTree tree = (Engine.GetMainLoop() as SceneTree) ?? throw new InvalidOperationException("scene tree unavailable");
            fixture = new Node3D { Name = "WeatherLungStreamerFixture" };
            tree.Root.AddChild(fixture);
            RunPartB(fixture, failures);
            RunPartC(fixture, failures);
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
        finally
        {
            if (fixture != null && GodotObject.IsInstanceValid(fixture))
            {
                fixture.GetParent()?.RemoveChild(fixture);
                fixture.Free();
            }
        }
        if (failures.Count != 0) { Console.Error.WriteLine("WEATHER_LUNG_STREAMER=FAIL " + string.Join(" | ", failures.Take(25))); return 1; }
        Console.WriteLine("WEATHER_LUNG_STREAMER=PASS");
        return 0;
    }

    // ---------------------------------------------------------------- Part A: core

    private static void RunPartA(List<string> failures)
    {
        // Approach: loads on the first frame inside 36 km, not before.
        var core = new WeatherLungStreamCore { LoadMeters = 36000d, UnloadMarginMeters = 8000d };
        int firstLoad = -1;
        for (int frame = 0; frame < 1000; frame++)
            if (core.Update(frame * Dt, 50000d - 100d * frame) == WeatherLungDetailEvent.Load) { firstLoad = frame; break; }
        if (firstLoad != 141) failures.Add($"approach loaded at frame {firstLoad}"); // 50000 - 100 f < 36000  <=>  f > 140

        // Dwell: beyond 44 km, the unload comes exactly 2 s after the first frame beyond it and not before.
        double now = 10d;
        core.Update(now, 40000d); // inside the margin, still wanted
        double beyondAt = now + Dt;
        int unloadFrame = -1;
        for (int frame = 1; frame < 600; frame++)
            if (core.Update(now + frame * Dt, 44500d) == WeatherLungDetailEvent.Unload) { unloadFrame = frame; break; }
        double held = (unloadFrame - 1) * Dt + Dt; // time since the first beyond frame
        if (unloadFrame < 0 || held < 2d - 1e-9 || held > 2d + 2d * Dt) failures.Add($"unload after {held:0.000} s (frame {unloadFrame})");
        _ = beyondAt;

        // Oscillation 35..46 km with a 1 s period for 60 s: loads once, never unloads (each excursion beyond 44 km lasts under 2 s).
        core = new WeatherLungStreamCore { LoadMeters = 36000d, UnloadMarginMeters = 8000d };
        int loads = 0, unloads = 0;
        for (int frame = 0; frame <= 60 * 60; frame++)
        {
            double t = frame * Dt;
            double d = 40500d + 5500d * Math.Sin(2d * Math.PI * t); // 35000 .. 46000
            switch (core.Update(t, d)) { case WeatherLungDetailEvent.Load: loads++; break; case WeatherLungDetailEvent.Unload: unloads++; break; }
        }
        if (loads != 1 || unloads != 0) failures.Add($"oscillation loads {loads} unloads {unloads}");

        // Disabling releases immediately; non-finite distances hold the state.
        core = new WeatherLungStreamCore { LoadMeters = 36000d, UnloadMarginMeters = 8000d };
        core.Update(0d, 1000d);
        if (core.Update(1d, double.NaN) != WeatherLungDetailEvent.None || !core.DetailWanted) failures.Add("NaN distance changed the state");
        core.Enabled = false;
        if (core.Update(2d, 1000d) != WeatherLungDetailEvent.Unload || core.DetailWanted) failures.Add("disable did not release");

        // Collision gate and box distance.
        if (!WeatherLungStreamCore.CollisionGate(false, 2999d, 3000d, 500d) || WeatherLungStreamCore.CollisionGate(false, 3000d, 3000d, 500d)) failures.Add("collision on threshold");
        if (!WeatherLungStreamCore.CollisionGate(true, 3499d, 3000d, 500d) || WeatherLungStreamCore.CollisionGate(true, 3501d, 3000d, 500d)) failures.Add("collision off margin");
        if (Math.Abs(WeatherLungStreamCore.DistanceToBox(5, 5, 5, 0, 0, 0, 10, 10, 10)) > 1e-12
            || Math.Abs(WeatherLungStreamCore.DistanceToBox(13, 14, 5, 0, 0, 0, 10, 10, 10) - 5d) > 1e-12) failures.Add("box distance");
        // Tuning rule: load radius is raised to detail range + 4 km.
        var tuning = WeatherLungTuning.Defaults with { DetailRangeMeters = 40000f, DetailLoadRadiusMeters = 36000f };
        if (Math.Abs(tuning.EffectiveLoadRadiusMeters - 44000f) > 1e-3f) failures.Add("effective load radius rule");
    }

    // ---------------------------------------------------------------- Part B: node

    private static RingCanonicalPosition Observer(WeatherLungSite site, double aroundOffset, double acrossOffset, double height)
        => new(site.Around + aroundOffset, site.PadHeight + height, site.Across + acrossOffset);

    private static double TransformError(Transform3D a, Transform3D b)
    {
        double worst = a.Origin.DistanceTo(b.Origin);
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
                worst = Math.Max(worst, Math.Abs(a.Basis[row][column] - b.Basis[row][column]));
        return worst;
    }

    private static (Node3D Root, VoxelChunkGridManager Grid, WeatherLungStreamer Streamer) MakeRig(Node3D fixture, string name, Action<WeatherLungStreamer>? configure = null)
    {
        var root = new Node3D { Name = name };
        fixture.AddChild(root);
        var grid = new VoxelChunkGridManager
        {
            Name = "ChunkGridManager", BuildOnReady = false, DormantEditableBackend = true, EnableChunkStreaming = false,
            UseDeterministicTerrainGeneration = true, EnableCanonicalFloatingOriginAnchor = true, TerrainSettings = new VoxelTerrainSettings()
        };
        root.AddChild(grid);
        var streamer = new WeatherLungStreamer { Name = "WeatherLung", AutoBind = false, SynchronousLoad = true };
        configure?.Invoke(streamer);
        root.AddChild(streamer);
        return (root, grid, streamer);
    }

    private static void RunPartB(Node3D fixture, List<string> failures)
    {
        using RingTerrainSampler v7 = EmitterSiteTestWorlds.CreateGen17(1337);
        using RingTerrainSampler v8 = EmitterSiteTestWorlds.CreateGen17WeatherLung(1337);
        if (v7.WeatherLung != null) failures.Add("v7 sampler carries a Weather Lung placement");
        WeatherLungPlacement placement = v8.WeatherLung ?? throw new InvalidOperationException("v8 sampler has no Weather Lung placement");
        WeatherLungSite site = placement.Site;
        Console.WriteLine("WEATHER_LUNG_STREAMER placement " + $"around={site.Around} across={site.Across} datum={site.PadHeight} yaw={site.YawDegrees} class={site.TerrainClass}");

        // v7: Unavailable, nothing instantiated.
        {
            var (root, grid, streamer) = MakeRig(fixture, "RigV7");
            if (streamer.BindFromSampler(v7) || !streamer.Unavailable || streamer.IsBound) failures.Add("v7 world was not Unavailable");
            streamer.AdvanceStartup();
            streamer.Step(1d, Observer(site, 0, 0, 10));
            if (streamer.PoiReady || streamer.GetChildCount() != 0) failures.Add("v7 world instantiated a POI");
            fixture.RemoveChild(root);
            root.Free();
        }

        // v8: bind, POI, transform.
        {
            var (root, grid, streamer) = MakeRig(fixture, "RigV8");
            if (!streamer.BindFromSampler(v8) || streamer.Unavailable || !streamer.IsBound) failures.Add("v8 world did not bind");
            grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(1_000_000d, 100d, 150_000d));
            if (!streamer.AdvanceStartup() || !streamer.PoiReady) { failures.Add("POI never became ready"); return; }
            if (streamer.Controller == null || streamer.PoiRoot == null) { failures.Add("POI root has no controller"); return; }
            if (streamer.DetailState != WeatherLungDetailState.None) failures.Add("detail present before any approach");

            double circumference = grid.GetCanonicalRingCircumferenceMeters64();
            var anchors = new[]
            {
                new RingCanonicalPosition(5_000_000d, 100d, 150_000d),
                new RingCanonicalPosition(circumference - 5d, 100d, 150_000d),
                new RingCanonicalPosition(site.Around + 20_000d, 50d, site.Across - 4_000d),
                new RingCanonicalPosition(site.Around, 0d, site.Across)
            };
            double worst = 0d;
            foreach (RingCanonicalPosition anchor in anchors)
            {
                grid.SetFloatingOriginCanonicalAnchor(anchor);
                RingLodGeometrySnapshot snapshot = grid.CaptureLodGeometrySnapshot();
                Transform3D expected = new EmitterSiteFrameContext(snapshot).SiteTransform(site.Around, site.Across, site.PadHeight + 0.05d, site.CosYaw, site.SinYaw);
                worst = Math.Max(worst, TransformError(streamer.PoiRoot.GlobalTransform, expected));
            }
            Console.WriteLine($"WEATHER_LUNG_STREAMER anchor worst_error={worst:E2} refreshes={streamer.AnchorRefreshCount}");
            if (worst > 0.001d) failures.Add($"POI transform drifted {worst}");
            if (streamer.AnchorRefreshCount < anchors.Length) failures.Add($"anchor refreshes {streamer.AnchorRefreshCount}");

            // Detail: far = nothing; 30 km = loaded; collision gates by distance.
            double now = 0d;
            void Step(RingCanonicalPosition observer, int frames = 1) { for (int i = 0; i < frames; i++) { now += Dt; streamer.Step(now, observer); } }
            Step(Observer(site, 60_000d, 0, 100), 5);
            if (streamer.DetailState != WeatherLungDetailState.None) failures.Add("detail loaded at 60 km");
            for (int i = 0; i < 2000 && streamer.DetailState != WeatherLungDetailState.Attached; i++) Step(Observer(site, 30_000d, 0, 100));
            if (streamer.DetailState != WeatherLungDetailState.Attached || streamer.DetailRoot == null) { failures.Add("detail did not attach at 30 km: " + streamer.DetailState); return; }
            if (!streamer.Controller.IsDetailAttached || streamer.DetailRoot.GetParent()?.Name != "DetailMount") failures.Add("detail not mounted under DetailMount");
            if (streamer.DetailRoot.VisibilityParent.IsEmpty) failures.Add("detail visibility parent (HLOD) not set");
            if (streamer.CollisionBodyCount < 16 || streamer.CollisionBodiesOn != 0) failures.Add($"collision bodies {streamer.CollisionBodyCount} on {streamer.CollisionBodiesOn} at 30 km");
            // Asset-local (-11250, 0): inside the arch; the Arch body is on, sectors on the far side (+X) stay off.
            ToCanonicalOffset(placement, -11250d, 0d, out double du, out double dw);
            Step(Observer(site, du, dw, 600), 2);
            if (!streamer.IsCollisionOn("Arch")) failures.Add("Arch collision off inside the arch");
            if (streamer.IsCollisionOn("Sector_00")) failures.Add("Sector_00 collision on 18 km away");
            int onNear = streamer.CollisionBodiesOn;
            ToCanonicalOffset(placement, 9000d, -2000d, out du, out dw);
            Step(Observer(site, du, dw, 50), 2);
            if (streamer.IsCollisionOn("Arch")) failures.Add("Arch collision still on at the far side");
            if (!streamer.IsCollisionOn("Sector_00") && !streamer.IsCollisionOn("Sector_11")) failures.Add("no east sector collision on at the east rim");
            Console.WriteLine($"WEATHER_LUNG_STREAMER collision near_arch_on={onNear} east_rim_on={streamer.CollisionBodiesOn}");
            Step(Observer(site, 30_000d, 0, 100), 2);
            if (streamer.CollisionBodiesOn != 0) failures.Add("collision stayed on at 30 km");

            // Dwell and flap: oscillate 35..46 km at 1 Hz for 30 s: one load, no unload; then 45 km for 1.5 s keeps it, 2.1 s releases it.
            int loadsBefore = streamer.LoadCount, unloadsBefore = streamer.UnloadCount;
            for (int frame = 0; frame < 30 * 60; frame++)
            {
                double distance = 40_500d + 5_500d * Math.Sin(2d * Math.PI * (frame * Dt));
                Step(Observer(site, distance, 0, 100));
            }
            if (streamer.LoadCount != loadsBefore || streamer.UnloadCount != unloadsBefore || streamer.DetailState != WeatherLungDetailState.Attached)
                failures.Add($"detail flapped: loads +{streamer.LoadCount - loadsBefore}, unloads +{streamer.UnloadCount - unloadsBefore}");
            Step(Observer(site, 45_000d, 0, 100), (int)(1.5 / Dt));
            if (streamer.DetailState != WeatherLungDetailState.Attached) failures.Add("detail released before the 2 s dwell");
            Step(Observer(site, 45_000d, 0, 100), (int)(0.7 / Dt));
            if (streamer.DetailState != WeatherLungDetailState.None || streamer.DetailRoot != null) failures.Add("detail not released after the dwell");
            if (streamer.Controller.IsDetailAttached) failures.Add("controller still holds the detail rig");
            // Re-approach loads it again.
            for (int i = 0; i < 2000 && streamer.DetailState != WeatherLungDetailState.Attached; i++) Step(Observer(site, 20_000d, 0, 100));
            if (streamer.DetailState != WeatherLungDetailState.Attached) failures.Add("detail did not reload on the second approach");

            // Disabling hides the POI and releases the detail; re-enabling restores the POI.
            streamer.Enabled = false;
            if (streamer.PoiRoot.Visible || streamer.DetailState != WeatherLungDetailState.None) failures.Add("disable left the station visible or the detail attached");
            streamer.Enabled = true;
            if (!streamer.PoiRoot.Visible) failures.Add("re-enable did not show the station");
            fixture.RemoveChild(root);
            root.Free();
        }

        // Forced detail load failure: no exception, the POI stays, the detail is latched failed.
        {
            var (root, grid, streamer) = MakeRig(fixture, "RigFailDetail", s => s.DetailScenePath = "res://assets/poi/missing/NoSuchDetail.tscn");
            streamer.BindFromSampler(v8);
            streamer.AdvanceStartup();
            if (!streamer.PoiReady) failures.Add("fail-detail rig: POI not ready");
            streamer.Step(1d, Observer(site, 10_000d, 0, 100));
            streamer.Step(1.1d, Observer(site, 10_000d, 0, 100));
            if (streamer.DetailState != WeatherLungDetailState.Failed) failures.Add("missing detail did not latch Failed: " + streamer.DetailState);
            if (!streamer.PoiReady || streamer.DescribeStatus().Length == 0) failures.Add("fail-detail rig lost the POI or status");
            fixture.RemoveChild(root);
            root.Free();
        }

        // Forced POI load failure.
        {
            var (root, grid, streamer) = MakeRig(fixture, "RigFailPoi", s => s.PoiScenePath = "res://assets/poi/missing/NoSuchPoi.tscn");
            streamer.BindFromSampler(v8);
            streamer.AdvanceStartup();
            streamer.Step(1d, Observer(site, 1_000d, 0, 100));
            if (!streamer.PoiFailed || streamer.PoiReady || streamer.DetailState != WeatherLungDetailState.None) failures.Add("missing POI did not latch PoiFailed");
            if (!streamer.DescribeStatus().Contains("not presented", StringComparison.Ordinal)) failures.Add("status does not report the missing scene");
            fixture.RemoveChild(root);
            root.Free();
        }
    }


    // ---------------------------------------------------------------- Part C: sliced attach

    private static void RunPartC(Node3D fixture, List<string> failures)
    {
        using RingTerrainSampler v8 = EmitterSiteTestWorlds.CreateGen17WeatherLung(1337);
        WeatherLungSite site = (v8.WeatherLung ?? throw new InvalidOperationException("no placement")).Site;

        // Budget: an injectable clock that advances 0.25 ms per reading (one piece = 4 readings = 1 ms). Budget 4 ms -> at most 4 pieces per frame, at least 1; budget 1 -> exactly 1 per frame.
        foreach (float budget in new[] { 4f, 1f })
        {
            var (root, grid, streamer) = MakeRig(fixture, "RigAttach" + budget);
            double fake = 0d;
            streamer.AttachClockMs = () => fake += 0.25d; // a piece takes four readings (diagnostics) = 1 ms
            streamer.BindFromSampler(v8);
            streamer.AdvanceStartup();
            streamer.Configure(WeatherLungTuning.Defaults with { DetailAttachBudgetMs = budget });
            WeatherLungController controller = streamer.Controller ?? throw new InvalidOperationException("no controller");
            double now = 0d;
            int frames = 0, previousDone = 0, maxPerFrame = 0, minPerFrame = int.MaxValue;
            bool revealed = false, hiddenOk = true, proxyHeld = true;
            var proxy = streamer.PoiRoot!.GetNode<MeshInstance3D>("FarProxy");
            while (streamer.DetailState != WeatherLungDetailState.Attached && frames < 400)
            {
                now += Dt;
                frames++;
                streamer.Step(now, Observer(site, 10_000d, 0, 100));
                if (streamer.DetailState == WeatherLungDetailState.Attaching)
                {
                    int done = streamer.AttachSlicesDone;
                    maxPerFrame = Math.Max(maxPerFrame, done - previousDone);
                    if (done - previousDone > 0) minPerFrame = Math.Min(minPerFrame, done - previousDone);
                    previousDone = done;
                    // Nothing may be revealed before the last piece: hidden assembly, no detail root, no rig, no collision, proxy held visible at every distance.
                    // The rig binds only in the finalize steps, after every piece is in.
                    hiddenOk &= streamer.AssemblyRoot is { Visible: false } && streamer.DetailRoot == null && streamer.CollisionBodyCount == 0
                        && (streamer.AttachSlicesDone >= streamer.AttachSliceCount - FinalizeSteps || !controller.IsDetailAttached);
                    proxyHeld &= controller.HoldProxyVisible && proxy.VisibilityRangeBegin == 0f;
                    revealed |= streamer.AssemblyRoot?.IsVisibleInTree() == true;
                }
            }
            if (streamer.DetailState != WeatherLungDetailState.Attached) { failures.Add($"budget {budget}: never attached"); fixture.RemoveChild(root); root.Free(); continue; }
            Console.WriteLine($"WEATHER_LUNG_STREAMER attach budget={budget} ms: {streamer.AttachSliceCount} steps in {streamer.AttachFrames} frames, max {maxPerFrame} per frame");
            // Pieces cost 1 ms of fake clock, the cheaper finalize stages 0.75 ms: a 1 ms budget takes one piece per frame (two finalize stages), a 4 ms budget about four.
            if (budget == 1f && (maxPerFrame > 2 || streamer.AttachFrames < streamer.AttachSliceCount - FinalizeSteps || streamer.AttachFrames > streamer.AttachSliceCount)) failures.Add($"budget 1 ms: {maxPerFrame} per frame, {streamer.AttachFrames} frames for {streamer.AttachSliceCount} steps");
            if (budget == 4f && (maxPerFrame > 5 || maxPerFrame < 2 || streamer.AttachFrames > streamer.AttachSliceCount / 2 || streamer.AttachFrames < streamer.AttachSliceCount / 6)) failures.Add($"budget 4 ms: {maxPerFrame} per frame, {streamer.AttachFrames} frames for {streamer.AttachSliceCount} steps");
            if (minPerFrame < 1) failures.Add("a frame made no progress");
            if (!hiddenOk) failures.Add($"budget {budget}: something was revealed or bound before completion");
            if (!proxyHeld) failures.Add($"budget {budget}: the far proxy was not held visible during the assembly");
            if (revealed) failures.Add($"budget {budget}: the assembly was visible in the tree");
            // Handover: visible, HLOD parent set, proxy back on its range, rig bound, collision gating live.
            if (streamer.DetailRoot is not { Visible: true } || controller.HoldProxyVisible || proxy.VisibilityRangeBegin != 28000f || streamer.DetailRoot.VisibilityParent.IsEmpty || !controller.IsDetailAttached || streamer.CollisionBodyCount < 16)
                failures.Add($"budget {budget}: handover state wrong");
            if (streamer.AssemblyRoot != null) failures.Add("assembly root left behind");
            fixture.RemoveChild(root);
            root.Free();
        }

        // Cancel mid-attach by disabling, and by walking away (unload after a shortened dwell): partial scene freed, proxy back on its range, state None.
        foreach (bool byDisable in new[] { true, false })
        {
            var (root, grid, streamer) = MakeRig(fixture, "RigCancel" + byDisable);
            double fake = 0d;
            streamer.AttachClockMs = () => fake += 5d; // every reading costs 5 ms: one piece per frame
            streamer.BindFromSampler(v8);
            streamer.AdvanceStartup();
            streamer.Core.UnloadDwellSeconds = 0.05d;
            WeatherLungController controller = streamer.Controller!;
            var proxy = streamer.PoiRoot!.GetNode<MeshInstance3D>("FarProxy");
            double now = 0d;
            for (int i = 0; i < 6; i++) { now += Dt; streamer.Step(now, Observer(site, 10_000d, 0, 100)); }
            if (streamer.DetailState != WeatherLungDetailState.Attaching || streamer.AssemblyRoot == null || streamer.AttachSlicesDone < 2 || streamer.AttachSlicesDone >= streamer.AttachSliceCount - 1)
            { failures.Add($"cancel rig {byDisable}: not mid-attach ({streamer.DetailState}, {streamer.AttachSlicesDone}/{streamer.AttachSliceCount})"); fixture.RemoveChild(root); root.Free(); continue; }
            Node3D partial = streamer.AssemblyRoot;
            Node mount = controller.DetailMountNode;
            if (byDisable) streamer.Enabled = false;
            else for (int i = 0; i < 20 && streamer.DetailState != WeatherLungDetailState.None; i++) { now += Dt; streamer.Step(now, Observer(site, 60_000d, 0, 100)); }
            streamer.Enabled = true;
            if (streamer.DetailState != WeatherLungDetailState.None || streamer.AssemblyRoot != null || streamer.DetailRoot != null) failures.Add($"cancel rig {byDisable}: state {streamer.DetailState} after the cancel");
            if (GodotObject.IsInstanceValid(partial)) failures.Add($"cancel rig {byDisable}: the partial scene was not freed");
            if (mount.GetChildCount() != 0) failures.Add($"cancel rig {byDisable}: the mount still has {mount.GetChildCount()} children");
            if (controller.HoldProxyVisible || proxy.VisibilityRangeBegin != 28000f) failures.Add($"cancel rig {byDisable}: the far proxy range was not restored");
            if (controller.IsDetailAttached || streamer.CollisionBodyCount != 0) failures.Add($"cancel rig {byDisable}: rig or collision bound after the cancel");
            // A fresh approach after the cancel attaches normally.
            fake = 0d;
            streamer.AttachClockMs = null;
            for (int i = 0; i < 2000 && streamer.DetailState != WeatherLungDetailState.Attached; i++) { now += Dt; streamer.Step(now, Observer(site, 10_000d, 0, 100)); }
            if (streamer.DetailState != WeatherLungDetailState.Attached) failures.Add($"cancel rig {byDisable}: did not attach after the cancel");
            fixture.RemoveChild(root);
            root.Free();
        }

        // Cancel in the middle of the finalize stages (the rig is already partly bound): everything is unbound again and the partial scene freed.
        {
            var (root, grid, streamer) = MakeRig(fixture, "RigCancelFinalize");
            double fake = 0d;
            streamer.AttachClockMs = () => fake += 5d;
            streamer.BindFromSampler(v8);
            streamer.AdvanceStartup();
            WeatherLungController controller = streamer.Controller!;
            var proxy = streamer.PoiRoot!.GetNode<MeshInstance3D>("FarProxy");
            double now = 0d;
            for (int i = 0; i < 200 && !(streamer.DetailState == WeatherLungDetailState.Attaching && streamer.AttachSlicesDone >= streamer.AttachSliceCount - FinalizeSteps + 3); i++)
            { now += Dt; streamer.Step(now, Observer(site, 10_000d, 0, 100)); }
            if (streamer.DetailState != WeatherLungDetailState.Attaching || !controller.IsDetailAttached)
                failures.Add($"cancel in finalize: not in the finalize stages ({streamer.DetailState}, {streamer.AttachSlicesDone}/{streamer.AttachSliceCount}, rig {controller.IsDetailAttached})");
            streamer.Enabled = false;
            streamer.Enabled = true;
            if (streamer.DetailState != WeatherLungDetailState.None || controller.IsDetailAttached || controller.DetailMountNode.GetChildCount() != 0 || controller.HoldProxyVisible || proxy.VisibilityRangeBegin != 28000f)
                failures.Add("cancel in finalize: rig, mount or proxy range not restored");
            fixture.RemoveChild(root);
            root.Free();
        }
    }

    /// <summary>Canonical (around, across) offset of the station-local Godot point (lx, lz): FromAssetLocal takes the Blender y = -lz.</summary>
    private static void ToCanonicalOffset(WeatherLungPlacement placement, double lx, double lz, out double du, out double dw)
        => placement.FromAssetLocal(lx, -lz, out du, out dw);
}
